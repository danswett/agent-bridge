using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace AgentBridge.App;

public sealed partial class SentMessageView : StackPanel
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(SentMessageView), new PropertyMetadata("", (d, _) => ((SentMessageView)d).Draw()));
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool Reading { get; set; } = true;
    int generation;
    string? shown;

    [GeneratedRegex(@"^@((?:[a-z]:[\\/]|/|\\\\)[^\s]*[\\/]attachments[\\/][0-9a-f]{32}-([0-9a-z._-]+))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    sealed record Attachment(string Path, string Name, bool Image);
    sealed record Picture(Attachment File, long Bytes, uint Width, uint Height, BitmapImage Thumbnail);

    public SentMessageView()
    {
        Spacing = 8;
        Loaded += (_, _) => Draw();
        Unloaded += (_, _) => { generation++; shown = null; Children.Clear(); };
    }

    void Draw()
    {
        if (!IsLoaded || shown == Message) { return; }
        shown = Message;
        var version = ++generation;
        Children.Clear();
        var files = new List<Attachment>();
        var parts = Regex.Split(Message ?? "", @"(\s+)");
        var pasted = 0;
        for (var i = 0; i < parts.Length; i += 2)
        {
            var match = Reference().Match(parts[i]);
            if (!match.Success) { continue; }
            var name = match.Groups[2].Value;
            var image = Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff";
            if (image && name.StartsWith("pasted.", StringComparison.OrdinalIgnoreCase)) { name = $"Pasted image{(++pasted > 1 ? $" {pasted}" : "")}"; }
            files.Add(new(match.Groups[1].Value, name, image));
            parts[i] = "";
            if (i + 1 < parts.Length) { parts[i + 1] = ""; }
        }
        var attachments = new StackPanel { Spacing = 4 };
        if (files.Count > 0) { Children.Add(attachments); }
        var text = files.Count > 0 ? string.Concat(parts).Trim() : Message;
        if (!string.IsNullOrEmpty(text))
        {
            Children.Add(new TextBlock
            {
                Text = text, Style = (Style)Application.Current.Resources[Reading ? "AbTypeReading" : "AbTypeRow"],
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(8, 2, 8, 2),
            });
        }
        if (files.Count > 0) { _ = DrawAttachments(files, attachments, version); }
    }

    static bool Local(string path) => path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && new DriveInfo(path[..3]).DriveType is DriveType.Fixed or DriveType.Removable;
    static bool Unavailable(Exception e) => e is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException;
    static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    static async Task<Picture?> Read(Attachment file)
    {
        if (!file.Image) { return null; }
        try
        {
            // Never probe UNC paths or mapped network drives, even if a remote host mentioned one.
            if (!Local(file.Path)) { return null; }
            using var input = File.OpenRead(file.Path);
            if (input.Length > 32 * 1024 * 1024) { return null; }
            using var stream = input.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 16_000_000) { return null; }
            var scale = Math.Min(1, 240.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var bitmap = new BitmapImage { DecodePixelWidth = Math.Max(1, (int)(decoder.PixelWidth * scale)), DecodePixelHeight = Math.Max(1, (int)(decoder.PixelHeight * scale)) };
            return new(file, input.Length, decoder.PixelWidth, decoder.PixelHeight, bitmap);
        }
        catch (Exception e) when (Unavailable(e))
        {
            AppLog.Write($"sent image unavailable: {e.GetType().Name}");
            return null;
        }
    }

    static Border Chip(Attachment file) => new()
    {
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("AbStroke"),
        Padding = new Thickness(8), MinHeight = 48, MaxWidth = 280, HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = file.Name + (file.Image ? "\nNot available on this device" : ""), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["AbTypeRow"] },
    };

    async Task DrawAttachments(List<Attachment> files, StackPanel panel, int version)
    {
        var pictures = new List<Picture>();
        var chips = new List<Attachment>();
        foreach (var file in files)
        {
            var picture = await Read(file);
            if (version != generation) { return; }
            if (picture is not null) { pictures.Add(picture); } else { chips.Add(file); }
        }
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (pictures.Count > 1) { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); }
        var count = Math.Min(pictures.Count, 4);
        var previews = new List<(Picture Picture, Button Button, ProgressRing Busy)>();
        for (var i = 0; i < count; i++)
        {
            if (i % 2 == 0) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            var index = i;
            var p = pictures[i];
            var more = i == 3 && pictures.Count > 4;
            var scale = Math.Min(1, 238.0 / Math.Max(p.Width, p.Height));
            var image = new Image { Source = p.Thumbnail, Stretch = pictures.Count == 1 ? Stretch.Uniform : Stretch.UniformToFill };
            if (pictures.Count == 1) { image.Width = p.Width * scale; image.Height = p.Height * scale; image.HorizontalAlignment = HorizontalAlignment.Center; image.VerticalAlignment = VerticalAlignment.Center; }
            var busy = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(image, "SentImage");
            AutomationProperties.SetName(image, p.File.Name);
            var button = new Button
            {
                Content = more ? (object)new TextBlock { Text = $"+{pictures.Count - 3}" } : new Border { CornerRadius = new CornerRadius(8), Child = new Grid { Children = { image, busy } } },
                Width = pictures.Count == 1 ? Math.Max(44, p.Width * scale + 2) : 116,
                Height = pictures.Count == 1 ? Math.Max(44, p.Height * scale + 2) : 116,
                MinWidth = 44, MinHeight = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            };
            AutomationProperties.SetAutomationId(button, "SentAttachment");
            AutomationProperties.SetName(button, more ? $"{pictures.Count - 3} more images. Opens larger." : $"Image, {p.File.Name}, {Size(p.Bytes)}. Opens larger.");
            button.Click += async (_, _) => await Enlarge(pictures, index);
            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, pictures.Count == 1 ? 0 : i % 2);
            grid.Children.Add(button);
            if (!more) { previews.Add((p, button, busy)); }
        }
        if (count > 0) { panel.Children.Add(grid); }
        foreach (var file in chips.Take(3)) { panel.Children.Add(Chip(file)); }
        if (chips.Count > 3)
        {
            var all = new StackPanel { Spacing = 4 };
            foreach (var file in chips.Skip(3)) { all.Children.Add(Chip(file)); }
            panel.Children.Add(new Button { Content = $"+{chips.Count - 3} more files", MinHeight = 44, Flyout = new Flyout { Content = new ScrollViewer { Content = all, MaxHeight = 400 } } });
        }
        // Reserve each box from its header before decoding pixels, so loading cannot reflow the bubble.
        foreach (var (picture, button, busy) in previews)
        {
            if (version != generation) { return; }
            try
            {
                using var input = File.OpenRead(picture.File.Path);
                if (input.Length > 32 * 1024 * 1024) { throw new IOException("Image exceeds the display limit."); }
                using var stream = input.AsRandomAccessStream();
                var loading = picture.Thumbnail.SetSourceAsync(stream).AsTask();
                if (await Task.WhenAny(loading, Task.Delay(300)) != loading && version == generation) { busy.Visibility = Visibility.Visible; busy.IsActive = true; }
                await loading;
            }
            catch (Exception e) when (Unavailable(e))
            {
                button.Content = Chip(picture.File);
                button.IsEnabled = false;
                AutomationProperties.SetName(button, $"{picture.File.Name}, Not available on this device");
                AppLog.Write($"sent image preview unavailable: {e.GetType().Name}");
            }
            finally { busy.IsActive = false; busy.Visibility = Visibility.Collapsed; }
        }
    }

    async Task Enlarge(List<Picture> pictures, int index)
    {
        var image = new Image { Stretch = Stretch.Uniform, MaxWidth = XamlRoot.Size.Width * 0.9 - 48, MaxHeight = Math.Max(44, XamlRoot.Size.Height * 0.9 - 180) };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 8, Children = { image, note } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Content = body, CloseButtonText = "Close", PrimaryButtonText = "Copy", SecondaryButtonText = "Save as",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = XamlRoot.Size.Width * 0.9;
        var request = 0;
        var closed = false;
        dialog.Closed += (_, _) => { closed = true; request++; image.Source = null; };
        dialog.Tapped += (_, e) => { if (e.OriginalSource is FrameworkElement { Name: "SmokeLayerBackground" }) { dialog.Hide(); } };
        async Task Show()
        {
            var token = ++request;
            var p = pictures[index];
            dialog.Title = $"{p.File.Name} · {Size(p.Bytes)}";
            note.Text = "";
            image.Source = null;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            try
            {
                using var input = File.OpenRead(p.File.Path);
                if (input.Length > 32 * 1024 * 1024) { throw new IOException("Image exceeds the display limit."); }
                using var stream = input.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 16_000_000) { throw new IOException("Image exceeds the display limit."); }
                stream.Seek(0);
                var scale = Math.Min(1, Math.Min(image.MaxWidth / decoder.PixelWidth, image.MaxHeight / decoder.PixelHeight));
                var bitmap = new BitmapImage { DecodePixelWidth = Math.Max(1, (int)(decoder.PixelWidth * scale)), DecodePixelHeight = Math.Max(1, (int)(decoder.PixelHeight * scale)) };
                await bitmap.SetSourceAsync(stream);
                if (closed || token != request) { return; }
                image.Width = decoder.PixelWidth * scale;
                image.Height = decoder.PixelHeight * scale;
                image.Source = bitmap;
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
            }
            catch (Exception e) when (Unavailable(e))
            {
                if (closed || token != request) { return; }
                image.Source = null;
                note.Text = "Not available on this device";
                AppLog.Write($"sent image viewer unavailable: {e.GetType().Name}");
            }
        }
        async Task Action(bool save)
        {
            try
            {
                var p = pictures[index];
                var file = await StorageFile.GetFileFromPathAsync(p.File.Path);
                if (save)
                {
                    var picker = new FileSavePicker { SuggestedFileName = p.File.Name };
                    picker.FileTypeChoices.Add("Image", [Path.GetExtension(p.File.Path)]);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
                    if (await picker.PickSaveFileAsync() is { } destination) { await file.CopyAndReplaceAsync(destination); }
                }
                else
                {
                    var data = new DataPackage();
                    data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    Clipboard.SetContent(data);
                }
            }
            catch (Exception e) when (Unavailable(e))
            {
                note.Text = save ? "Couldn't save the image." : "Couldn't copy the image.";
                AppLog.Write($"sent image action failed: {e.GetType().Name}");
            }
        }
        dialog.PrimaryButtonClick += async (_, e) => { e.Cancel = true; await Action(false); };
        dialog.SecondaryButtonClick += async (_, e) => { e.Cancel = true; await Action(true); };
        dialog.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not (VirtualKey.Left or VirtualKey.Right) || pictures.Count < 2) { return; }
            e.Handled = true;
            index = (index + (e.Key == VirtualKey.Right ? 1 : pictures.Count - 1)) % pictures.Count;
            await Show();
        };
        try { await Show(); await dialog.ShowAsync(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { AppLog.Write($"sent image dialog failed: {e.GetType().Name}"); }
        finally { image.Source = null; }
    }
}
