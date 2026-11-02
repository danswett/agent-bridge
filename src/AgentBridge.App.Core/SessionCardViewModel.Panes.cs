namespace AgentBridge.App.Core;

// One fact in a session's Details (ui.md §6.2): what rows never carry (§6.1). Mono for what is
// code (§4.1, #215: paths, branches, ids, commands); a long one is cut in the middle, with the
// whole of it in Full.
public sealed record DetailRow(string Label, string Value, bool Mono = false, string? Full = null)
{
    public string Tip => Full ?? Value;
}

// What the window's panes show of a session (#180): its row in the list (§6.1), its header
// (§6.2), its Details and its delegates' counts until U8 (#176) lists them.
public sealed partial class SessionCardViewModel
{
    // How long a path may be in Details before it is cut in the middle.
    const int PathMost = 48;

    bool selected;
    public bool Selected { get => selected; internal set => Set(ref selected, value); }

    // The row's trailing age, when there's no unread dot.
    public string AgeText { get; private set; } = "";
    public bool ShowAge => !Unread;

    // The repository a person reads as a name (#215: in the text face), or the folder's last part.
    public string Place => session.Repository ?? Words.LastSegment(session.Cwd);
    string? Machine => ctx.ShowMachine() ? ctx.MachineName() : null;

    // Line 2: at most two facts (§6.1), what the session is doing then where, on one line, so no
    // separator ever starts a line (#214). A question's words win over "Driven by an agent" (§2.2
    // rules 1, 2 and 7). Where: the machine once there is more than one, else the repository.
    // Only the status's first fact: "Waiting on you · 2 delegates still running" is one fact on a
    // row and both in the header.
    public string RowStatus => Driven && Glyph != StatusGlyph.Needs ? "Driven by an agent" : StatusText.Split(" · ")[0];
    public string RowPlace => Machine ?? Place;
    public string RowFacts => string.Join(" · ", new[] { RowStatus, RowPlace }.Where(s => !string.IsNullOrEmpty(s)));
    // A row read aloud (§9): its glyph's name already says the status and machine.
    public string RowLabel => Unread ? $"{GlyphLabel}, new activity" : GlyphLabel;

    // The header's one meta line after the glyph (§6.2): status, machine, repository.
    public string HeaderPlace => string.Join(" · ", new[] { Machine, Place }.Where(s => !string.IsNullOrEmpty(s)));

    // Everything rows leave out (§6.1), for Details and the inspector.
    public IReadOnlyList<DetailRow> Details
    {
        get
        {
            var now = ctx.Time.GetUtcNow();
            var rows = new List<DetailRow> { new("Status", StatusText), new("Agent", AgentName) };
            if (ctx.MachineName() is { Length: > 0 } m) { rows.Add(new("Machine", m)); }
            if (session.Model is { Length: > 0 } model) { rows.Add(new("Model", model)); }
            if (session.Repository is { Length: > 0 } repo) { rows.Add(new("Repository", repo)); }
            if (session.Branch is { Length: > 0 } branch) { rows.Add(new("Branch", branch, Mono: true)); }
            if (session.Cwd is { Length: > 0 } cwd) { rows.Add(new("Folder", Words.Middle(cwd, PathMost), Mono: true, Full: cwd)); }
            if (Driven) { rows.Add(new("Driven by", DriverPrincipal ?? "an agent")); }
            // In three panes there is no Delegates tab: the inspector carries the counts until U8 (#176).
            if (HasDelegates) { rows.Add(new("Background", DelegatesText)); }
            if (session.Activity.TurnCount is { } t) { rows.Add(new("Turns", t.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
            if (session.Activity.Compactions is > 0 and var c) { rows.Add(new("Compactions", c.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
            if (session.ProcessId is { } pid) { rows.Add(new("Process", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Mono: true)); }
            if (View != "idle") { rows.Add(new("For", Words.Duration(session.StatusSince, now))); }
            rows.Add(new("Updated", Words.Ago(session.UpdatedAt, now)));
            rows.Add(new("ID", session.Id, Mono: true));
            return rows;
        }
    }

    // Who asks the question in the conversation (§6.4): the main agent, or the delegate the
    // question names (S1's Question.agent), "A delegate" when the list left it out (§2.2 rule 2).
    public string AsksText => session.Question?.Agent is { Length: > 0 } agent
        ? $"{(session.Subagents?.FirstOrDefault(s => s.Id == agent)?.Name is { Length: > 0 } name ? name : "A delegate")} asks"
        : "Main agent asks";

    // Until U8 (#176) lists delegates, the Delegates tab has only the counts the host reports.
    public bool HasDelegates => session.Activity.Background is { } b && (b.Agents > 0 || b.Shells > 0);
    public string DelegatesText
    {
        get
        {
            var b = session.Activity.Background;
            return string.Join(" · ", new[]
            {
                b?.Agents > 0 ? Words.Plural(b.Agents, "background agent", "background agents") : null,
                b?.Shells > 0 ? Words.Plural(b.Shells, "background command", "background commands") : null,
            }.Where(s => s is not null));
        }
    }
}
