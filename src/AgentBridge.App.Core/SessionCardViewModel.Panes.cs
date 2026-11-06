namespace AgentBridge.App.Core;

// One fact in a session's Details (ui.md §6.2): what rows never carry (§6.1). Mono for what is
// code (§4.1, #215: paths, branches, ids, commands). A path is cut in the middle to the width it
// is shown at, by the view (Words.Middle), keeping the end people tell paths apart by (#214), with
// the whole of it in the tooltip.
public sealed record DetailRow(string Label, string Value, bool Mono = false, bool Path = false)
{
    public string Tip => Value;
}
// What the window's panes show of a session (#180): its row in the list (§6.1), its header
// (§6.2), its Details and its delegates' counts (Delegates lists them, §6.3).
public sealed partial class SessionCardViewModel
{
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
    // Only the status's first fact, as StatusRules made it, never its words cut at a separator
    // (#286): "Waiting on you · 2 delegates still running" is one fact on a row and both in the
    // header.
    public string RowStatus => DriverDrawn ? DriverText : Line.FirstFact;
    // Nor over rules 10-12 (#800), which say what is wrong; the glyph keeps the driven badge.
    bool DriverDrawn => Driven && Glyph != StatusGlyph.Needs && Line.Rule is not (StatusRules.SignIn or StatusRules.Error or StatusRules.NotResponding);
    public string RowPlace => Machine ?? Place;
    public string RowFacts => string.Join(StatusLine.Separator, new[] { RowStatus, RowPlace }.Where(s => !string.IsNullOrEmpty(s)));

    // A row read aloud (ui.md §9.1 A1, #796). It starts with the words the row draws, in the order
    // drawn and from the properties the row binds, so the two can't drift: the title, line 2's
    // status and place, then the age, or "New activity" where the dot takes the age's place. Then
    // what else the glyph's name (§9.2) says that the row doesn't draw, so nothing it said before
    // goes unsaid: the status's later facts, or all of it under "Driven by an agent"; the driver
    // while a question's words take its place; and the machine while the row shows where instead.
    // The glyph's name alone left out line 2's place and the age (#796). Each part as the PWA names
    // its row (#791), so both clients read a row alike. A delegate's name in a status fact is read
    // without its commas, as in the glyph's name, so it can't read as facts of its own (#303).
    public string RowLabel
    {
        get
        {
            var line = Line;
            var unsaid = DriverDrawn ? line.Spoken : string.Join(StatusLine.Separator, line.Facts.Skip(1).Select(DelegateLabel.Aloud));
            return string.Join(", ", new[]
            {
                Title,
                DelegateLabel.Aloud(RowStatus),
                RowPlace,
                Unread ? "New activity" : AgeText,
                unsaid,
                Driven && !DriverDrawn ? "driven by an agent" : null,
                Machine is null ? ctx.MachineName() : null,
            }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    // The header's one meta line after the glyph (§6.2): status, machine, repository.
    public string HeaderPlace => string.Join(" · ", new[] { Machine, Place }.Where(s => !string.IsNullOrEmpty(s)));

    // Everything rows leave out (§6.1), for Details and the inspector. Kept, and changed a row at a
    // time: the clock ticks every five seconds, and a list made afresh each time would draw every
    // value again and lose a person's selection of a path or an id.
    public System.Collections.ObjectModel.ObservableCollection<DetailRow> Details { get; } = [];

    void SyncDetails() => SyncDetailRows(Details, CurrentDetails());

    // Also the delegate inspector's: its Model can arrive after its Kind and ID rows.
    internal static void SyncDetailRows(System.Collections.ObjectModel.ObservableCollection<DetailRow> rows, IReadOnlyList<DetailRow> want)
    {
        // A fact that has gone (Background at zero, For once idle) goes first, by label: inserting
        // before it instead made new copies of every row after it, the ID among them, and a
        // selection in one was lost to an ordinary update.
        var labels = want.Select(d => d.Label).ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!labels.Contains(rows[i].Label)) { rows.RemoveAt(i); }
        }
        for (var i = 0; i < want.Count; i++)
        {
            if (i < rows.Count && rows[i] == want[i]) { continue; }
            if (i < rows.Count && rows[i].Label == want[i].Label) { rows[i] = want[i]; }
            else { rows.Insert(i, want[i]); }
        }
        while (rows.Count > want.Count) { rows.RemoveAt(rows.Count - 1); }
    }

    List<DetailRow> CurrentDetails()
    {
        var now = ctx.Time.GetUtcNow();
        var rows = new List<DetailRow> { new("Status", StatusText), new("Agent", AgentName) };
        if (ctx.MachineName() is { Length: > 0 } m) { rows.Add(new("Machine", m)); }
        if (session.Model is { Length: > 0 } model) { rows.Add(new("Model", model)); }
        if (session.Repository is { Length: > 0 } repo) { rows.Add(new("Repository", repo)); }
        if (session.Branch is { Length: > 0 } branch) { rows.Add(new("Branch", branch, Mono: true)); }
        if (session.Cwd is { Length: > 0 } cwd) { rows.Add(new("Folder", cwd, Mono: true, Path: true)); }
        if (Driven) { rows.Add(new("Driven by", DriverPrincipal ?? "an agent")); }
        // The counts the host reports, beside the delegates the strip lists (or in place of them).
        if (HasDelegates) { rows.Add(new("Background", DelegatesText)); }
        if (session.Activity.TurnCount is { } t) { rows.Add(new("Turns", t.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
        if (session.Activity.Compactions is > 0 and var c) { rows.Add(new("Compactions", c.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
        if (session.ProcessId is { } pid) { rows.Add(new("Process", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Mono: true)); }
        // What the agent said went wrong (#800): plain text, revealed and cut short by AuthState,
        // never a link, and only here, never on its row.
        // The host's sentence for what is wrong (6649775484), as it is: never the agent's own text.
        if (AuthState.Of(session)?.Message is { } said) { rows.Add(new("Message", said)); }
        // Not responding: the time it went silent, which the row's age counts from (ui.md §6.13).
        if (AuthState.Clock(SilentSince) is { } silent) { rows.Add(new("Not responding since", silent)); }
        if (View != "idle") { rows.Add(new("For", Words.Duration(session.StatusSince, now))); }
        rows.Add(new("Updated", Words.Ago(session.UpdatedAt, now)));
        rows.Add(new("ID", session.Id, Mono: true));
        return rows;
    }

    // Who asks the question in the conversation (§6.4): the main agent, or the delegate the
    // question names (S1's Question.agent, or the session's waitingOn), "A delegate" when the list
    // left it out (§2.2 rule 2). Needs you's own rule, so opening a question never changes who it
    // says asked.
    public string AsksText => NeedsYouEntry.DelegateOf(session) is { } name ? $"{name} asks" : "Main agent asks";

    // The counts the host reports: all a host that can't list its delegates (Claude, Codex, an older
    // host: status rule 8) says of them, in the Delegates tab and in Details.
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
