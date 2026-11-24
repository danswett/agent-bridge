using System.Runtime.InteropServices;

namespace AgentBridge.Host.Delivery.Windows;

[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct ConsoleKeyRecord
{
    [FieldOffset(0)] public ushort EventType;
    [FieldOffset(4)] public int KeyDown;
    [FieldOffset(8)] public ushort RepeatCount;
    [FieldOffset(10)] public ushort VirtualKeyCode;
    [FieldOffset(12)] public ushort VirtualScanCode;
    [FieldOffset(14)] public char UnicodeChar;
    [FieldOffset(16)] public uint ControlKeyState;
}

internal static class ConsoleKeyInput
{
    public static ConsoleKeyRecord[] Text(string text)
    {
        var records = new ConsoleKeyRecord[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            records[2 * i] = Key(text[i], text[i] == '\r' ? (ushort)13 : (ushort)0, 1);
            records[2 * i + 1] = records[2 * i];
            records[2 * i + 1].KeyDown = 0;
        }
        return records;
    }

    public static ConsoleKeyRecord[] Submit(SubmitKey key) => key switch
    {
        SubmitKey.None => [],
        SubmitKey.Enter => Text("\r"),
        SubmitKey.Queue => [Key('\u0011', 0x51, 1, 8), Key('\u0011', 0x51, 0, 8)],
        _ => throw new ArgumentException("Unknown submit key; no keys are sent.", nameof(key)),
    };

    static ConsoleKeyRecord Key(char character, ushort virtualKey, int down, uint control = 0) =>
        new() { EventType = 1, KeyDown = down, RepeatCount = 1, VirtualKeyCode = virtualKey,
            UnicodeChar = character, ControlKeyState = control };
}
