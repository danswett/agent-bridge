using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task<(byte[] Pixels, uint Width, uint Height, BitmapAlphaMode Alpha)> PicturePixels(UIElement element)
    {
        // RenderTargetBitmap captures XAML, not Window.SystemBackdrop. In particular,
        // NavigationView's translucent layer then sits over transparency instead of Mica.
        // Capture the composed client for whole-window pictures; keep isolated XAML
        // captures for controls (including controls deliberately drawn off screen).
        if (element is FrameworkElement root && ReferenceEquals(root.XamlRoot?.Content, root))
        {
            await CaptureFrame(root);
            var title = ById<FrameworkElement>(root, "TitleBar");
            return WindowPicture.Take(MainWindow.CurrentHandle, (title?.ActualHeight ?? 0) / root.ActualHeight);
        }
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        return ((await bitmap.GetPixelsAsync()).ToArray(), (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, BitmapAlphaMode.Premultiplied);
    }

    // High contrast's rule (#1202): nothing inside a control sets its own background. An accent
    // button is drawn from the system pair only, Highlight and HighlightText, whichever way round,
    // with antialiasing between them. Samples the drawn pixels inside the button, clear of its
    // border and corners; returns the button's name and the first other colour, or null.
    static async Task<string?> AccentPlate(Button button)
    {
        Windows.UI.Color? System(string key) => Application.Current.Resources.TryGetValue(key, out var c) && c is Windows.UI.Color colour ? colour : null;
        var name = AutomationProperties.GetName(button) is { Length: > 0 } n ? n : AutomationProperties.GetAutomationId(button) is { Length: > 0 } id ? id : button.Content?.ToString() ?? "a button";
        if (System("SystemColorHighlightColor") is not { } fill || System("SystemColorHighlightTextColor") is not { } ink) { return $"{name}: the system colours are missing"; }
        if (button.ActualWidth <= 0 || button.ActualHeight <= 0) { return $"{name}: not drawn"; }
        (byte[] Pixels, uint Width, uint Height, BitmapAlphaMode Alpha) picture;
        try { picture = await PicturePixels(button); }
        catch (ArgumentException e) { return $"{name}: could not be drawn for the check ({e.Message})"; }
        var scale = picture.Width / button.ActualWidth;
        var inset = (int)Math.Ceiling(4 * scale);
        double fr = fill.R, fg = fill.G, fb = fill.B;
        var (dr, dg, db) = (ink.R - fr, ink.G - fg, ink.B - fb);
        var length = dr * dr + dg * dg + db * db;
        for (var y = inset; y < picture.Height - inset; y++)
        {
            for (var x = inset; x < picture.Width - inset; x++)
            {
                var i = (int)((y * picture.Width + x) * 4);
                if (picture.Pixels[i + 3] != 255) { continue; }
                double b = picture.Pixels[i], g = picture.Pixels[i + 1], r = picture.Pixels[i + 2];
                // How far the pixel is from the line between the two colours: on it is a blend.
                var t = length == 0 ? 0 : Math.Clamp(((r - fr) * dr + (g - fg) * dg + (b - fb) * db) / length, 0, 1);
                var off = Math.Sqrt(Math.Pow(r - (fr + t * dr), 2) + Math.Pow(g - (fg + t * dg), 2) + Math.Pow(b - (fb + t * db), 2));
                if (off > 24) { return $"{name}: a pixel at {x},{y} is #{(int)r:X2}{(int)g:X2}{(int)b:X2}, neither Highlight nor HighlightText"; }
            }
        }
        return null;
    }

    static async Task CaptureFrame(FrameworkElement root)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        TimeSpan? previous = null;
        var queued = false;
        void Rendering(object? sender, object args)
        {
            if (args is not RenderingEventArgs frame)
            {
                completed.TrySetException(new InvalidOperationException("The self-test capture received no XAML rendering timestamp."));
                return;
            }
            if (previous == frame.RenderingTime) { return; }
            previous = frame.RenderingTime;
            frames++;
            if (frames != 2) { return; }
            queued = true;
            // Rendering ticks even when no dirty content is submitted; Rendered need
            // not fire then. The next distinct tick lets the preceding XAML update
            // cycle finish. Leave this callback before DwmFlush waits for its commit.
            if (!root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => completed.TrySetResult()))
            {
                completed.TrySetException(new InvalidOperationException("The UI dispatcher refused the self-test capture frame barrier."));
            }
        }
        try
        {
            // Subscribing requests frames even when XAML would otherwise be idle.
            CompositionTarget.Rendering += Rendering;
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"The XAML capture frame barrier did not complete within 10 seconds (distinct rendering ticks: {frames}; UI turn queued: {queued}).", e);
        }
        finally
        {
            CompositionTarget.Rendering -= Rendering;
        }
    }

    static async Task CheckDarkPagePicture(MainWindow window, string file, Action<bool, string> check)
    {
        var content = ById<FrameworkElement>(window.RootElement, "Content");
        var layer = content is null ? null : Above<Grid>(content, g => g.Name == "ContentGrid");
        var root = (Panel)window.RootElement;
        // A ThemeResource in an explicitly dark element resolves the platform's actual
        // layer token, independently of the application's (usually Light on CI) theme.
        var probe = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    RequestedTheme="Dark" Opacity="0" IsHitTestVisible="False"
                    Background="{ThemeResource LayerFillColorDefaultBrush}" />
            """);
        root.Children.Add(probe);
        try
        {
            probe.UpdateLayout();
            if (layer is null || probe.Background is not SolidColorBrush surface || window.RootElement.ActualTheme != ElementTheme.Dark)
            {
                check(false, $"reads the dark page's surface token for {Path.GetFileName(file)}");
                return;
            }
            if (window.CurrentPage == "sessions") { await CheckDarkPictureGuard(surface.Color, check); }
            // Inside ContentGrid's bottom-left padding: no text, row, composer, scrollbar,
            // divider or rounded top corner. Map effective coordinates to the output PNG.
            var point = layer.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(4, layer.ActualHeight - 4));
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync((await File.ReadAllBytesAsync(file)).AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await StraightPixels(decoder);
            var x = (int)Math.Floor(point.X * decoder.PixelWidth / root.ActualWidth);
            var y = (int)Math.Floor(point.Y * decoder.PixelHeight / root.ActualHeight);
            var inBounds = x >= 0 && y >= 0 && x < decoder.PixelWidth && y < decoder.PixelHeight;
            var offset = inBounds ? checked((y * (int)decoder.PixelWidth + x) * 4) : 0;
            var ok = inBounds && DarkPagePixel(pixels.AsSpan(offset, 4), surface.Color);
            var seen = inBounds ? $"#{pixels[offset + 3]:X2}{pixels[offset + 2]:X2}{pixels[offset + 1]:X2}{pixels[offset]:X2}" : "outside the PNG";
            check(ok, $"saves an opaque dark page background no lighter than its platform layer token in {Path.GetFileName(file)} ({x},{y}: {seen}, layer {surface.Color})");
        }
        finally { root.Children.Remove(probe); }
    }

    static async Task<byte[]> StraightPixels(BitmapDecoder decoder) =>
        (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

    static async Task CheckDarkPictureGuard(Windows.UI.Color surface, Action<bool, string> check)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 4, 1, 96, 96,
            [surface.B, surface.G, surface.R, 255, 255, 255, 255, 255, 0, 0, 0, 0, surface.B, surface.G, surface.R, 128]);
        await encoder.FlushAsync();
        stream.Seek(0);
        var pixels = await StraightPixels(await BitmapDecoder.CreateAsync(stream));
        check(DarkPagePixel(pixels.AsSpan(0, 4), surface) && !DarkPagePixel(pixels.AsSpan(4, 4), surface),
            "accepts an encoded opaque dark surface and rejects an encoded white page");
        check(!DarkPagePixel(pixels.AsSpan(8, 4), surface) && !DarkPagePixel(pixels.AsSpan(12, 4), surface),
            "rejects encoded transparent black and a dark layer missing its opaque backdrop");
    }

    static bool DarkPagePixel(ReadOnlySpan<byte> bgra, Windows.UI.Color surface)
    {
        // The layer is translucent over Mica. Its straight RGB is the dark surface's
        // upper bound, not its premultiplied RGB over transparent black. An output PNG
        // must be opaque: even RGB black at alpha zero becomes white in a white viewer.
        static double Linear(byte value)
        {
            var s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(byte r, byte g, byte b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
        return bgra[3] == 255 && Luminance(bgra[2], bgra[1], bgra[0]) <= Luminance(surface.R, surface.G, surface.B);
    }

    static partial class WindowPicture
    {
        const uint ClientOnly = 1, RenderFullContent = 2;

        internal static (byte[] Pixels, uint Width, uint Height, BitmapAlphaMode Alpha) Take(nint window, double bodyTop)
        {
            if (!GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the self-test window's client bounds"); }
            var dc = GetDC(window);
            if (dc == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the self-test window's DC"); }
            nint memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(dc);
                var info = new BitmapInfo
                {
                    Size = 40, Width = rect.Right, Height = -rect.Bottom, Planes = 1, BitCount = 32
                };
                bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
                if (memory == 0 || bitmap == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Allocating the self-test window's bitmap"); }
                previous = SelectObject(memory, bitmap);
                if (previous == 0 || previous == -1) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Selecting the self-test window's bitmap"); }
                Marshal.ThrowExceptionForHR(DwmFlush());
                // PW_CLIENTONLY | PW_RENDERFULLCONTENT: the window's composition, not the
                // desktop (which could contain another app), including its system backdrop.
                if (!PrintWindow(window, memory, ClientOnly | RenderFullContent)) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Capturing the self-test window"); }
                if (!GdiFlush()) { throw new InvalidOperationException("Flushing the self-test window's bitmap failed."); }
                var pixels = new byte[checked(rect.Right * rect.Bottom * 4)];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                var painted = false;
                // Native caption buttons alone do not prove that WinUI's body was captured.
                for (var i = checked((int)Math.Ceiling(bodyTop * rect.Bottom) * rect.Right * 4); i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) { painted = true; break; }
                }
                if (!painted) { throw new InvalidOperationException("The composed self-test window capture is blank."); }
                // A 32-bit BI_RGB DIB is BGRX, not BGRA; X is undefined, never alpha.
                return (pixels, (uint)rect.Right, (uint)rect.Bottom, BitmapAlphaMode.Ignore);
            }
            finally
            {
                if (previous != 0 && previous != -1) { SelectObject(memory, previous); }
                if (bitmap != 0) { DeleteObject(bitmap); }
                if (memory != 0) { DeleteDC(memory); }
                ReleaseDC(window, dc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfo
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant, Colors;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(nint window, out Rect rect);
        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial nint GetDC(nint window);
        [LibraryImport("user32.dll")]
        private static partial int ReleaseDC(nint window, nint dc);
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PrintWindow(nint window, nint dc, uint flags);
        [LibraryImport("dwmapi.dll")]
        private static partial int DwmFlush();
        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial nint CreateCompatibleDC(nint dc);
        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GdiFlush();
        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [LibraryImport("gdi32.dll")]
        private static partial nint SelectObject(nint dc, nint value);
        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteObject(nint value);
        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteDC(nint dc);
    }
}
