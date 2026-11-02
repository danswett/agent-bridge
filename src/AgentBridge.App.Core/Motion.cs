using System.Globalization;

namespace AgentBridge.App.Core;

// Motion (ui.md §5.3): the catalogue's entries as the generated AbMotion* resources time them,
// and what each becomes with Windows' animation effects off. The app reads the resources by key
// (App's Motion), the tests read the same generated file, so no duration, curve or distance here
// is written by hand. The platform's own transitions (Expander, MenuFlyout, NavigationView,
// SelectorBar, the controls' pressed states) keep their own timing and follow the setting
// themselves; these time only what the app draws itself.
public enum MotionReduced { Instant, CrossFade }

// A cubic Bézier easing, as a KeySpline's two control points.
public readonly record struct MotionEase(double X1, double Y1, double X2, double Y2)
{
    public static MotionEase Parse(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4) { throw new FormatException($"an easing is four numbers, not '{text}'"); }
        var n = parts.Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        return new MotionEase(n[0], n[1], n[2], n[3]);
    }
}

// One catalogue entry: in, out (an entry with no exit of its own leaves as it came), how far it
// travels, and its reduced-motion form.
public sealed record MotionEntry(string Name, TimeSpan Duration, MotionEase Ease, double Distance, TimeSpan ExitDuration, MotionEase ExitEase, MotionReduced Reduced);

// What to run: Duration zero runs nothing, the change simply shows.
public readonly record struct MotionStep(TimeSpan Duration, MotionEase Ease, double Distance)
{
    public static readonly MotionStep None = new(TimeSpan.Zero, default, 0);
    public bool Runs => Duration > TimeSpan.Zero;
}

public sealed class MotionCatalogue
{
    // Every entry in ui.md §5.3's catalogue, by its tokens.json name with a capital.
    public static readonly IReadOnlyList<string> Names =
        ["Push", "Pane", "Tab", "Disclosure", "ListItem", "Glyph", "ActivityDot", "Selection", "Press", "Acknowledge", "Toast", "Sheet", "Menu", "Scroll", "Reorder"];

    readonly Dictionary<string, MotionEntry> entries;

    MotionCatalogue(Dictionary<string, MotionEntry> entries, TimeSpan crossFade, TimeSpan working, TimeSpan delegating)
    {
        this.entries = entries;
        CrossFade = crossFade;
        WorkingPeriod = working;
        DelegatingPeriod = delegating;
    }

    // Reduced motion's fade, for every entry marked CrossFade.
    public TimeSpan CrossFade { get; }
    // §2.1's two loops: the working arc's turn and the delegating orbit's.
    public TimeSpan WorkingPeriod { get; }
    public TimeSpan DelegatingPeriod { get; }

    public MotionEntry this[string name] => entries[name];

    // From the generated resources, each read by its key (AbMotionPushDuration and so on) as the
    // invariant text the generator wrote. A missing key throws: an entry with no tokens would
    // otherwise run untimed.
    public static MotionCatalogue From(Func<string, string?> resource)
    {
        string Need(string key) => resource(key) ?? throw new KeyNotFoundException($"no motion resource {key}");
        TimeSpan Time(string key) => TimeSpan.Parse(Need(key), CultureInfo.InvariantCulture);
        var all = new Dictionary<string, MotionEntry>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            var key = $"AbMotion{name}";
            var duration = Time($"{key}Duration");
            var ease = MotionEase.Parse(Need($"{key}Ease"));
            var distance = resource($"{key}Distance") is { } d ? double.Parse(d, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
            var exit = resource($"{key}ExitDuration") is { } x ? TimeSpan.Parse(x, CultureInfo.InvariantCulture) : duration;
            var exitEase = resource($"{key}ExitEase") is { } xe ? MotionEase.Parse(xe) : ease;
            var reduced = Enum.Parse<MotionReduced>(Need($"{key}Reduced"));
            all[name] = new MotionEntry(name, duration, ease, distance, exit, exitEase, reduced);
        }
        return new MotionCatalogue(all, Time("AbMotionCrossFade"), Time("AbMotionGlyphWorkingPeriod"), Time("AbMotionGlyphDelegatingPeriod"));
    }

    // An entry coming in. With animations off it travels nowhere: a CrossFade entry fades over
    // CrossFade, and any other changes at once.
    public MotionStep Enter(string name, bool animations)
    {
        var e = entries[name];
        return animations ? new MotionStep(e.Duration, e.Ease, e.Distance) : Reduced(e);
    }

    // The same entry going out: quicker, accelerating, where it has an exit of its own.
    public MotionStep Exit(string name, bool animations)
    {
        var e = entries[name];
        return animations ? new MotionStep(e.ExitDuration, e.ExitEase, e.Distance) : Reduced(e);
    }

    MotionStep Reduced(MotionEntry e) => e.Reduced == MotionReduced.CrossFade ? new MotionStep(CrossFade, e.Ease, 0) : MotionStep.None;

    // The §2.1 glyphs and an in-flight progress ring: the only loops, and still with animations off.
    public static bool Loops(bool animations) => animations;
}

// Scroll to a question (ui.md §5.3): the conversation follows a question that arrives only while
// the person is at its end, and never while they have scrolled back through it or are touching it.
// "At the end" is where the person last left the view: content growing under them (a response
// streaming in, a question laid out) does not make them someone who scrolled back.
public sealed class ConversationFollow
{
    // A pixel or so short of the end still reads as the end: offsets are fractional at scale.
    public const double Slack = 1;

    readonly HashSet<uint> contacts = [];
    bool manipulating;
    double? offset;

    public bool AtEnd { get; private set; } = true;
    public bool Touching => contacts.Count > 0 || manipulating;

    // The view moved. Only a move of the view counts: the conversation growing or shrinking under
    // a still view (a response streaming in, a question laid out above) leaves the person where
    // they were, at the end or not.
    public void Scrolled(double offset, double scrollable)
    {
        if (this.offset is { } was && Math.Abs(was - offset) < Slack) { return; }
        this.offset = offset;
        AtEnd = offset >= scrollable - Slack;
    }

    // Another session shown, from its top: where the last one was left says nothing about it.
    public void Reset()
    {
        offset = null;
        AtEnd = true;
    }

    // Each finger by its pointer id: the view is free only once the last one lifts.
    public void TouchDown(uint pointer) => contacts.Add(pointer);
    public void TouchUp(uint pointer) => contacts.Remove(pointer);
    // A touch the panning took over from: Windows cancels the pointer while the finger is still
    // down, and the manipulation carries on. Only a cancel with no pan under way ends the touch.
    public void TouchCanceled(uint pointer) { if (!manipulating) { contacts.Remove(pointer); } }
    public void PanStarted() => manipulating = true;
    public void PanEnded() { manipulating = false; contacts.Clear(); }

    // A question arrived on the session shown: whether to bring it into view.
    public bool Follows => AtEnd && !Touching;
}
