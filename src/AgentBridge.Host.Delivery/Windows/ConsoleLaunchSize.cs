namespace AgentBridge.Host.Delivery.Windows;

internal readonly record struct ConsoleGeometry(short Columns, short Rows, short Left, short Top, short Right, short Bottom);

internal interface IConsoleSize
{
    bool Read(out ConsoleGeometry geometry);
    bool Buffer(short columns, short rows);
    bool Window(short left, short top, short right, short bottom);
}

internal static class ConsoleLaunchSize
{
    public static bool Apply(IConsoleSize console, short columns)
    {
        if (!console.Read(out var before)) { return false; }
        var width = Math.Max(columns, before.Right - before.Left + 1);
        var height = Math.Max(30, before.Bottom - before.Top + 1);
        var right = before.Left + width - 1;
        var bottom = before.Top + height - 1;
        if (right >= short.MaxValue || bottom >= short.MaxValue) { return false; }
        var bufferWidth = (short)Math.Max(before.Columns, right + 1);
        var bufferHeight = (short)Math.Max(before.Rows, bottom + 1);
        // The window cannot grow beyond its buffer; never shrink either dimension.
        if ((bufferWidth != before.Columns || bufferHeight != before.Rows) && !console.Buffer(bufferWidth, bufferHeight)) { return false; }
        if ((right != before.Right || bottom != before.Bottom)
            && !console.Window(before.Left, before.Top, (short)right, (short)bottom)) { return false; }
        return console.Read(out var after) && after.Columns >= columns && after.Rows >= 30
            && after.Right - after.Left + 1 >= columns && after.Bottom - after.Top + 1 >= 30;
    }
}
