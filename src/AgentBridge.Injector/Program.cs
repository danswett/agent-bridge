using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBridge.Injector;

// agent-bridge-injector <pid> [--screen]
//
// Reads a key plan on stdin (KeyPlan.ToWorkerScript in AgentBridge.Host.Delivery) and
// types it into the console of <pid>, one attach-write-detach per send, the shape
// agent-ha-bridge's ConsoleInjector.Send has long delivered with. It reports on stdout,
// one line each, flushed:
//
//   writing <n>[ enter]   before every write to the console, so whoever started it knows
//                         a key may have landed whatever happens next
//   wrote <n>             send n is complete
//   ok                    every send is complete
//   fail <stage> <code>   it stopped: usage, plan, attach, input, write or partial, with
//                         the Win32 error
//
// A send may name rows of the console's visible window and their text: it is typed only if,
// inside the same attach, every one still reads so; otherwise "fail screen 0" and nothing of
// it is written. With --screen nothing is typed at all: it says "row <base64>" for each row
// of the visible window, top to bottom, then "ok".
//
// The host treats a worker that ends any other way after a "writing" line as "some of the
// answer may have gone", and never retries it. It is a separate process because Windows
// ends every process attached to a console when that console's window closes (spike S1).
internal static unsafe partial class Program
{
    const string Header = "agent-bridge-keys 1";

    static Stream output = Stream.Null;

    static int Main(string[] args)
    {
        // Taken before any attach: these are the pipes the host gave us, and stay so.
        output = Console.OpenStandardOutput();
        if (args.Length is not (1 or 2) || (args.Length == 2 && args[1] != "--screen")
            || !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0)
        {
            Say("fail usage 0");
            return 2;
        }
        if (args.Length == 2) { return Screen(pid); }
        var sends = Read(Console.OpenStandardInput());
        if (sends is null)
        {
            Say("fail plan 0");
            return 2;
        }
        Ignore();
        for (var i = 0; i < sends.Count; i++)
        {
            if (Send(pid, i, sends[i]) is { } failure)
            {
                Say(failure);
                return 3;
            }
        }
        Say("ok");
        return 0;
    }

    sealed record KeySend(string Text, bool Enter, int EnterDelayMs, int PauseAfterMs, List<(int Row, string Text)> Expect);

    static List<KeySend>? Read(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != Header) { return null; }
        var sends = new List<KeySend>();
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { continue; }
            var parts = line.Split(' ');
            if (parts.Length < 5 || (parts.Length - 5) % 2 != 0 || parts[0] != "send") { return null; }
            try
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                var expect = new List<(int, string)>();
                for (var k = 5; k < parts.Length; k += 2)
                {
                    expect.Add((int.Parse(parts[k], NumberStyles.None, CultureInfo.InvariantCulture), Encoding.UTF8.GetString(Convert.FromBase64String(parts[k + 1]))));
                }
                sends.Add(new KeySend(text, parts[2] == "1",
                    int.Parse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture),
                    int.Parse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture), expect));
            }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
        }
        return sends;
    }

    static string? Send(uint pid, int index, KeySend send)
    {
        // A process holds one console at a time, and AttachConsole fails while it has one.
        FreeConsole();
        if (!AttachConsole(pid)) { return $"fail attach {Marshal.GetLastPInvokeError()}"; }
        // Again after attaching: spike S1 found a registration made before AttachConsole
        // does not cover the console attached later, and a Ctrl+C typed in that window
        // would otherwise end this process mid-answer.
        Ignore();
        var input = CreateFile("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (input == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            FreeConsole();
            return $"fail input {error}";
        }
        try
        {
            if (send.Expect.Count > 0)
            {
                var (rows, failure) = Window();
                if (rows is null) { return failure; }
                foreach (var (row, expected) in send.Expect)
                {
                    if (row >= rows.Count || !string.Equals(rows[row], expected.TrimEnd(), StringComparison.Ordinal)) { return "fail screen 0"; }
                }
            }
            if (send.Text.Length > 0)
            {
                Say($"writing {index}");
                if (Write(input, send.Text) is { } failed) { return failed; }
            }
            if (send.Enter)
            {
                // A separate keystroke after a pause: a burst of characters is a paste, and
                // an Enter inside it would not submit.
                if (send.EnterDelayMs > 0) { Thread.Sleep(send.EnterDelayMs); }
                Say($"writing {index} enter");
                if (Write(input, "\r") is { } failed) { return failed; }
            }
        }
        finally
        {
            CloseHandle(input);
            FreeConsole();
        }
        Say($"wrote {index}");
        if (send.PauseAfterMs > 0) { Thread.Sleep(send.PauseAfterMs); }
        return null;
    }

    static int Screen(uint pid)
    {
        FreeConsole();
        if (!AttachConsole(pid))
        {
            Say($"fail attach {Marshal.GetLastPInvokeError()}");
            return 3;
        }
        Ignore();
        List<string>? rows;
        string? failure;
        try { (rows, failure) = Window(); }
        finally { FreeConsole(); }
        if (rows is null)
        {
            Say(failure!);
            return 3;
        }
        foreach (var row in rows) { Say("row " + Convert.ToBase64String(Encoding.UTF8.GetBytes(row))); }
        Say("ok");
        return 0;
    }

    // The visible window of the attached console's active screen buffer, each row with its
    // trailing spaces removed. Only read: nothing is written to CONOUT$.
    static (List<string>? Rows, string? Failure) Window()
    {
        var screen = CreateFile("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (screen == -1) { return (null, $"fail output {Marshal.GetLastPInvokeError()}"); }
        try
        {
            if (!GetConsoleScreenBufferInfo(screen, out var info)) { return (null, $"fail output {Marshal.GetLastPInvokeError()}"); }
            var width = info.WindowRight - info.WindowLeft + 1;
            if (width <= 0 || info.WindowBottom < info.WindowTop) { return (null, "fail output 0"); }
            var buffer = new char[width];
            var rows = new List<string>();
            for (var y = info.WindowTop; y <= info.WindowBottom; y++)
            {
                uint read;
                fixed (char* first = buffer)
                {
                    if (!ReadConsoleOutputCharacterW(screen, first, (uint)width, new Coord { X = info.WindowLeft, Y = (short)y }, out read))
                    {
                        return (null, $"fail output {Marshal.GetLastPInvokeError()}");
                    }
                }
                rows.Add(new string(buffer, 0, (int)read).TrimEnd());
            }
            return (rows, null);
        }
        finally { CloseHandle(screen); }
    }

    // Every UTF-16 code unit as its own key down and key up, Unicode throughout: a character
    // outside the BMP is its surrogate pair, two key events. The records go to the API by
    // pointer, never marshalled, so nothing can pass UnicodeChar through a code page
    // (agent-ha-bridge's struct, declared without CharSet, did, and turned "–" and CJK into "?").
    static string? Write(nint input, string text)
    {
        var records = new InputRecord[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var vk = c == '\r' ? VkReturn : (ushort)0;
            records[2 * i] = new InputRecord { EventType = KeyEvent, KeyDown = 1, RepeatCount = 1, VirtualKeyCode = vk, UnicodeChar = c };
            records[2 * i + 1] = new InputRecord { EventType = KeyEvent, KeyDown = 0, RepeatCount = 1, VirtualKeyCode = vk, UnicodeChar = c };
        }
        fixed (InputRecord* first = records)
        {
            if (!WriteConsoleInputW(input, first, (uint)records.Length, out var written)) { return $"fail write {Marshal.GetLastPInvokeError()}"; }
            if (written != records.Length) { return $"fail partial {written}"; }
        }
        return null;
    }

    static void Say(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        try
        {
            output.Write(bytes);
            output.Flush();
        }
        // The host has gone; there is nobody left to tell.
        catch (IOException) { }
    }

    static void Ignore()
    {
        // The "ignore Ctrl+C" attribute, and a handler that says every event was handled.
        SetConsoleCtrlHandler(null, true);
        SetConsoleCtrlHandler(&Handled, false);
        SetConsoleCtrlHandler(&Handled, true);
    }

    [UnmanagedCallersOnly]
    static int Handled(uint type) => 1;

    // INPUT_RECORD with its KEY_EVENT_RECORD laid out by hand, every field blittable.
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

    [StructLayout(LayoutKind.Sequential)]
    struct Coord
    {
        public short X;
        public short Y;
    }

    // CONSOLE_SCREEN_BUFFER_INFO: size, cursor, attributes, window, largest window.
    [StructLayout(LayoutKind.Sequential)]
    struct ScreenBufferInfo
    {
        public short SizeX;
        public short SizeY;
        public short CursorX;
        public short CursorY;
        public ushort Attributes;
        public short WindowLeft;
        public short WindowTop;
        public short WindowRight;
        public short WindowBottom;
        public short MaxX;
        public short MaxY;
    }

    const ushort KeyEvent = 1;
    const ushort VkReturn = 0x0D;
    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileShareRead = 1;
    const uint FileShareWrite = 2;
    const uint OpenExisting = 3;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfo(nint output, out ScreenBufferInfo info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadConsoleOutputCharacterW(nint output, char* characters, uint length, Coord at, out uint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteConsoleInputW(nint input, InputRecord* records, uint length, out uint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
