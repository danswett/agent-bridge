using System.Text.RegularExpressions;

namespace AgentBridge.Host.Delivery.Copilot;

// Whether the session's screen shows the ask_user form at the field the next keys are for
// (#510 review). A screen that has settled is not enough: a permission dialog that opens
// before its transcript entry is written is just as steady, and an Enter held to its rows
// would pick whatever it highlights. So the rows a field's keys are held to must be the form's,
// at that field, before they are bound.
//
// From Copilot CLI 1.0.105's cli-main.js (minified names; recheck them on a CLI update):
//   * The form (`MIo`) is headed "<who> needs information." ("Copilot needs information." for
//     ask_user), then the question, then the fields (`Uxn`).
//   * Only the field being answered is drawn, headed `<title ?? name>:` on a row of its own
//     (`Fxn`, `Lxn`, `Oxn`, `Dxn`, `Nxn`); with more than one field, a tab bar names them all,
//     without the colon.
//   * Under it, the key hints (`Ft`, joined by " · ", wrapping): "enter accept", "tab next"
//     unless at the last field, "shift+tab prev" unless at the first, "ctrl+d decline",
//     "esc cancel".
//   * A permission prompt is a numbered list with a plain "1. Yes" (docs/host-answers.md); a
//     form's options are never numbered.
public static partial class CopilotFormScreen
{
    // Null when the rows show the form at field `index`; otherwise why not, said as a reason
    // for stopping before the next key.
    public static string? Showing(IReadOnlyList<string> rows, CopilotForm form, int index)
    {
        if (rows.Any(r => PermissionRow().IsMatch(r)))
        {
            return "a permission prompt showed on the session's screen in place of the question's form";
        }
        var text = Collapse(string.Join(' ', rows));
        if (!rows.Any(r => r.Contains("needs information.", StringComparison.Ordinal))
            || !text.Contains("ctrl+d decline", StringComparison.Ordinal) || !text.Contains("esc cancel", StringComparison.Ordinal))
        {
            return "the session's screen no longer showed the question's form";
        }
        if (index < 0 || index >= form.Fields.Count) { return "the question's form has no such field"; }
        var field = form.Fields[index];
        if (field.Label is not { } label)
        {
            return $"the host cannot tell how the form heads its field \"{field.Name}\", so it cannot see that the form shows it";
        }
        var count = form.Fields.Count;
        if (!rows.Any(r => Collapse(r) == Collapse(label) + ":")
            || (count > 1 && (text.Contains("tab next", StringComparison.Ordinal) != index < count - 1
                || text.Contains("shift+tab prev", StringComparison.Ordinal) != index > 0)))
        {
            return $"the form on the session's screen was not at the field \"{label}\"";
        }
        return null;
    }

    static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\s*(?:\u276F\s*)?1\.\s+Yes\b")]
    private static partial Regex PermissionRow();
}
