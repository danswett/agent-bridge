using System.Runtime.InteropServices;

namespace AgentBridge.App;

static partial class SelfTest
{
    // A screen big enough for three panes (ui.md §5.2: from 1280 px) for the self-test's pass, on CI
    // only, where the runner's screen is 1024×768 and a window is never wider than its screen: a
    // developer's own screen is never changed. Changed for this session only, not saved, and put
    // back when the pass ends. Off the UI thread: the change is broadcast to every window, this
    // app's included, as high contrast's is.
    sealed partial class DisplayPass
    {
        const int CurrentSettings = -1;
        const uint PelsWidth = 0x80000, PelsHeight = 0x100000;
        bool changed;

        public string How { get; private set; } = "";

        public static async Task<DisplayPass> BeginAsync(uint width, uint height)
        {
            var pass = new DisplayPass();
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") { pass.How = "not changed: the screen is changed only on CI"; return pass; }
            var (changed, how) = await Task.Run(() => Change(width, height));
            pass.changed = changed;
            pass.How = how;
            return pass;
        }

        public async Task EndAsync()
        {
            if (!changed) { return; }
            changed = false;
            // No mode: back to the one saved in the registry, which this pass never changed.
            await Task.Run(() => ChangeDisplaySettingsEx(null, 0, 0, 0, 0));
        }

        static (bool Changed, string How) Change(uint width, uint height)
        {
            var current = Mode();
            if (!EnumDisplaySettings(null, CurrentSettings, ref current)) { return (false, "not changed: reading the screen's mode failed"); }
            if (current.dmPelsWidth >= width && current.dmPelsHeight >= height) { return (false, $"{current.dmPelsWidth}×{current.dmPelsHeight} already"); }
            DEVMODE? best = null;
            for (var i = 0; i < 1000; i++)
            {
                var m = Mode();
                if (!EnumDisplaySettings(null, i, ref m)) { break; }
                if (m.dmPelsWidth < width || m.dmPelsHeight < height || m.dmBitsPerPel != current.dmBitsPerPel) { continue; }
                if (best is null || (ulong)m.dmPelsWidth * m.dmPelsHeight < (ulong)best.Value.dmPelsWidth * best.Value.dmPelsHeight) { best = m; }
            }
            if (best is not { } mode) { return (false, $"not changed: no mode of at least {width}×{height} beside {current.dmPelsWidth}×{current.dmPelsHeight}"); }
            mode.dmFields = PelsWidth | PelsHeight;
            var r = ChangeDisplaySettingsEx(null, ref mode, 0, 0, 0);
            return r == 0 ? (true, $"{mode.dmPelsWidth}×{mode.dmPelsHeight} for the pass, from {current.dmPelsWidth}×{current.dmPelsHeight}") : (false, $"not changed: the change failed ({r})");
        }

        static DEVMODE Mode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

        [StructLayout(LayoutKind.Sequential)]
        unsafe struct DEVMODE
        {
            public fixed char dmDeviceName[32];
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            public fixed char dmFormName[32];
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumDisplaySettings(string? device, int mode, ref DEVMODE value);

        [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int ChangeDisplaySettingsEx(string? device, ref DEVMODE mode, nint window, uint flags, nint param);

        [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int ChangeDisplaySettingsEx(string? device, nint mode, nint window, uint flags, nint param);
    }
}