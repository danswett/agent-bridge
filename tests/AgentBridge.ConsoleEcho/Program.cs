using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// agent-bridge-console-echo <log> [--exit-on <line>] [--show <file>]
//
// Writes "ready <pid> <console window>" to <log>, then one line per key event read from
// its own console input: "key <down> <vk> <char code>". Runs until its console closes or
// it is stopped, or, with --exit-on, until that line is typed and Enter pressed, as an agent
// CLI exits on /exit. With --show, it first writes that file's text to its console, the way
// an agent CLI draws a prompt. Every line is flushed as it is written, so a test reads them
// live.
internal static unsafe partial class Program
{
    static int Main(string[] args)
    {
        if (args.Length is not (1 or 3 or 5)) { return 2; }
        string? exitOn = null, show = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--exit-on": exitOn = args[i + 1]; break;
                case "--show": show = args[i + 1]; break;
                default: return 2;
            }
        }
        var line = new StringBuilder();
        using var log = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        void Say(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            log.Write(bytes);
            log.Flush();
        }

        if (show is not null)
        {
            var text = File.ReadAllText(show).Replace("\r\n", "\n").Replace("\n", "\r\n");
            fixed (char* first = text)
            {
                if (!WriteConsoleW(GetStdHandle(-11), first, (uint)text.Length, out _, 0)) { return 1; }
            }
        }

        var input = GetStdHandle(-10);
        Say(string.Create(CultureInfo.InvariantCulture, $"ready {Environment.ProcessId} {GetConsoleWindow()}"));
        var records = new InputRecord[64];
        while (true)
        {
            uint read;
            fixed (InputRecord* first = records)
            {
                if (!ReadConsoleInputW(input, first, (uint)records.Length, out read)) { return 1; }
            }
            for (var i = 0; i < read; i++)
            {
                var r = records[i];
                if (r.EventType != 1) { continue; }
                Say(string.Create(CultureInfo.InvariantCulture, $"key {r.KeyDown} {r.VirtualKeyCode} {(int)r.UnicodeChar}"));
                if (r.KeyDown == 0 || exitOn is null) { continue; }
                if (r.UnicodeChar != '\r') { line.Append(r.UnicodeChar); continue; }
                if (line.ToString() == exitOn) { Say("exiting"); return 0; }
                line.Clear();
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public char UnicodeChar;
        [FieldOffset(16)] public uint ControlKeyState;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteConsoleW(nint output, char* text, uint length, out uint written, nint reserved);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadConsoleInputW(nint input, InputRecord* records, uint length, out uint read);
}
