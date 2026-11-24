using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.App.Core;

/// <summary>One element of the UI as the snapshot sees it: its type name, its x:Name, and how many pointers it holds captured. No text, no content.</summary>
public sealed record UiElementInfo(string Type, string? Name, int PointerCaptures);

/// <summary>
/// What the UI looked like at its last answered ping, for the log when it freezes: the open popups, the focused element and whoever holds the pointer.
/// NAMES ONLY. A type must be a plain type name and a name a plain identifier, or it is not shown (a name someone typed into a field never is);
/// the list is cut so the line stays short.
/// </summary>
public static partial class UiStateSnapshot
{
    const int MostPopups = 8;
    const int LongestName = 40;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.`+]{0,79}$")]
    private static partial Regex PlainType();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainName();

    public static string Describe(IReadOnlyList<UiElementInfo> popups, UiElementInfo? focused)
    {
        var shown = popups.Take(MostPopups).Select(Label).ToList();
        if (popups.Count > MostPopups) { shown.Add($"+{popups.Count - MostPopups} more"); }
        var holders = popups.Where(p => p.PointerCaptures > 0).Concat(focused is { PointerCaptures: > 0 } ? [focused] : []).Select(Label).ToList();
        return new StringBuilder()
            .Append("popups: ").Append(popups.Count == 0 ? "none" : $"{popups.Count} ({string.Join(", ", shown)})")
            .Append("; focus: ").Append(focused is null ? "none" : Label(focused))
            .Append("; pointer capture: ").Append(holders.Count == 0 ? "none" : string.Join(", ", holders))
            .ToString();
    }

    static string Label(UiElementInfo element)
    {
        var type = PlainType().IsMatch(element.Type) ? element.Type : "Unknown";
        return element.Name is { } name && PlainName().IsMatch(name) ? $"{type} '{(name.Length > LongestName ? name[..LongestName] : name)}'" : type;
    }
}
