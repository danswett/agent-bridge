using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// Where a question's answers are on screen, watched (#491, App.Core's AnswerMoves): read after every
// layout pass and every scroll of what holds them, and a move of any answer calls moved, which holds
// them from the pointer again (AnswerGuard). The answers are the holder's buttons and text boxes
// that are shown, in window coordinates. A layout pass that moves them raises LayoutUpdated before
// the next input is hit tested against it, so the guard is armed before a click can land on them;
// a pointer's press on one also reads them first (Check), which catches a change laid out since.
sealed class AnswerWatch
{
    readonly FrameworkElement holder;
    readonly Func<FrameworkElement?>? question;
    readonly Action moved;
    readonly AnswerMoves moves = new();
    readonly Dictionary<ScrollViewer, PersonScroll> scrollers = [];
    bool watching;
    bool reading;

    // holder: what holds the answers, there for as long as this watches. question: the session
    // view's question, whose own laying out after the person's act in it isn't a move (Own); none
    // for Needs you.
    public AnswerWatch(FrameworkElement holder, Func<FrameworkElement?>? question, Action moved)
    {
        this.holder = holder;
        this.question = question;
        this.moved = moved;
    }

    public void Start()
    {
        if (watching) { return; }
        watching = true;
        moves.Forget();
        holder.LayoutUpdated += OnLayout;
    }

    public void Stop()
    {
        if (!watching) { return; }
        watching = false;
        holder.LayoutUpdated -= OnLayout;
        foreach (var (sv, p) in scrollers) { p.Unhook(sv); }
        scrollers.Clear();
        moves.Forget();
    }

    // A new set of answers drawn (another question, another session): measured from where they are.
    public void Forget() => moves.Forget();

    // The person pressed, typed, pasted or clicked in the question: what it lays out for a moment
    // after is theirs, if nothing else changes with it (AnswerMoves.Theirs). Each act holds it from
    // the latest. A moment, not a count of frames: a control shown for the first time (Other's box)
    // takes more than one layout pass, and on a machine drawing at 60 Hz two frames had gone before
    // the last of them, so the person's own click held their question (CI, x64). Where nothing is
    // drawn (a disconnected session) frames hardly come at all, and the window never closed.
    public void Own()
    {
        ownAt = DateTimeOffset.UtcNow;
        ownUntil = ownAt + Act;
        told = false;
    }

    // When the person's act was, and whether a hold near it has been logged: the first is, with
    // what moved, so a hold of their own click can be told apart from a real move afterwards.
    DateTimeOffset ownAt;
    bool told;
    AnswerLook? last;

    // The app drew something in the question itself (who asks, its heading): whatever moves in the
    // moment it is drawn in counts, whatever the person is doing.
    public void NotOwn() => forcedUntil = DateTimeOffset.UtcNow + Act;

    // Rows of the list holding the answers came, went or moved: the list may keep the rows in sight
    // in place by scrolling (ListView's KeepItemsInView), which moves Needs you above them. For a
    // moment no scroll is taken to be the person's, so that one is found as a move, even mid-scroll.
    public void Unsettled() => unsettledUntil = DateTimeOffset.UtcNow + Act;

    static readonly TimeSpan Act = TimeSpan.FromMilliseconds(300);
    DateTimeOffset ownUntil, forcedUntil, unsettledUntil;
    // Set while a pointer's press lays out what is pending: the read that layout pass brings, as
    // the one after it, exempts nothing.
    bool checking;

    // Read now, as a pointer's press on an answer comes: a change made since the last layout pass
    // (an update in the same task as the click, before any frame) is laid out and found before the
    // press is let through. Nothing the person did is exempt here. Says whether it found a move;
    // throws if where the answers are can't be read.
    public bool Check()
    {
        // A move the layout pass's own read finds counts for this press too: that read takes it
        // in, and the one after it has nothing left to find.
        checking = true;
        foundChecking = false;
        try { (holder.XamlRoot?.Content ?? holder).UpdateLayout(); }
        finally { checking = false; }
        return Read(exempt: false) | foundChecking;
    }

    bool foundChecking;

    void OnLayout(object? sender, object e) => ReadOrHold();

    // A read after a layout pass or a scroll. Where they are can't be read: held, rather than taken
    // to have stayed put.
    void ReadOrHold()
    {
        try { Read(exempt: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!unreadable) { AppLog.Write($"answers: where they are can't be read ({ex.GetType().Name}: {ex.Message})"); }
            unreadable = true;
            moves.Forget();
            moved();
        }
    }

    bool unreadable;

    bool Read(bool exempt)
    {
        if (!watching || reading || holder.XamlRoot is not { } root) { return false; }
        reading = true;
        try
        {
            var shown = HolderShown() ? Answers().ToList() : [];
            var chain = Scrollers();
            var look = new AnswerLook(
                [.. shown.Select(a => { var p = a.TransformToVisual(null).TransformPoint(default); return (p.X, p.Y); })],
                [.. chain.Select(Look)],
                root.Size.Width,
                root.Size.Height,
                QuestionBox(chain.FirstOrDefault()));
            var now = DateTimeOffset.UtcNow;
            var found = moves.Read(look, own: exempt && !checking && now < ownUntil && now >= forcedUntil);
            if (found && !told && now < ownUntil + TimeSpan.FromSeconds(1))
            {
                told = true;
                AppLog.Write($"answers: held {(now - ownAt).TotalMilliseconds:0} ms after the person's own act (checking: {checking}; app drawing: {now < forcedUntil}): {Brief(last)} -> {Brief(look)}");
            }
            last = look;
            if (found) { foundChecking |= checking; moved(); }
            return found;
        }
        finally { reading = false; }
    }

    static string Brief(AnswerLook? l) => l is null ? "none" :
        $"{l.Answers.Count} at {string.Join(" ", l.Answers.Select(a => $"{a.X:0.#},{a.Y:0.#}"))}; question {l.Question}; window {l.WindowWidth}x{l.WindowHeight}; "
        + string.Join(" | ", l.Scrollers.Select(s => $"scroller at {s.Left:0.#},{s.Top:0.#} view {s.ViewportWidth:0.#}x{s.ViewportHeight:0.#} offset {s.OffsetY:0.#} extent {s.ExtentWidth:0.#}x{s.ExtentHeight:0.#}{(s.Person ? " (theirs)" : "")}"));
    // The holder's buttons (choices, Approve once, Deny, Send, Other, a link, a code block's Copy)
    // and text boxes, where they are shown.
    IEnumerable<FrameworkElement> Answers()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(holder);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is UIElement { Visibility: Visibility.Collapsed }) { continue; }
            if (node != holder && node is ButtonBase or TextBox)
            {
                if (node is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } shown) { yield return shown; }
                // Its template's own parts (a text box's clear button, shown as it has text and
                // focus) come and go with the person's typing; they aren't answers of their own.
                continue;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) { stack.Push(VisualTreeHelper.GetChild(node, i)); }
        }
    }

    // Nothing above the holder collapsed (a pane or tab hidden), which keeps its last sizes: none shown.
    bool HolderShown()
    {
        for (DependencyObject? up = holder; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is UIElement { Visibility: Visibility.Collapsed }) { return false; }
        }
        return true;
    }

    // Every scroller holding the answers, innermost first, each watched for the person scrolling it.
    List<ScrollViewer> Scrollers()
    {
        var chain = new List<ScrollViewer>();
        for (var up = VisualTreeHelper.GetParent(holder); up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is not ScrollViewer sv) { continue; }
            chain.Add(sv);
            if (!scrollers.ContainsKey(sv)) { scrollers[sv] = PersonScroll.Hook(sv, ReadOrHold); }
        }
        return chain;
    }

    ScrollerLook Look(ScrollViewer sv)
    {
        var at = sv.TransformToVisual(null).TransformPoint(default);
        return new ScrollerLook(at.X, at.Y, sv.HorizontalOffset, sv.VerticalOffset, sv.ScrollableWidth, sv.ScrollableHeight,
            sv.ViewportWidth, sv.ViewportHeight, sv.ExtentWidth, sv.ExtentHeight,
            DateTimeOffset.UtcNow >= unsettledUntil && scrollers.TryGetValue(sv, out var p) && p.Scrolling);
    }

    // The question's box in what the innermost scroller scrolls, which that scroller's own
    // scrolling doesn't move.
    LookBox? QuestionBox(ScrollViewer? inner)
    {
        if (question?.Invoke() is not { } q || inner?.Content is not UIElement content || q.XamlRoot is null) { return null; }
        var at = q.TransformToVisual(content).TransformPoint(default);
        return new LookBox(at.X, at.Y, q.ActualWidth, q.ActualHeight);
    }

    // Whether the person is scrolling a scroller: a wheel turned or a key pressed over it a moment
    // ago, a finger or the wheel's smooth scroll under way (direct manipulation), or its scroll bar
    // held, and for a moment after each step of that scroll while it lasts. Anything else that
    // scrolls it (the app bringing a question or a row into view, a clamp) is not theirs.
    sealed class PersonScroll
    {
        static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(300);
        static readonly TimeSpan Step = TimeSpan.FromMilliseconds(150);
        DateTimeOffset until;
        bool manipulating;
        readonly HashSet<uint> bar = [];
        PointerEventHandler? wheel, pressed, released;
        KeyEventHandler? key;
        EventHandler<object>? started, completed;
        EventHandler<ScrollViewerViewChangedEventArgs>? changed;

        public bool Scrolling => manipulating || bar.Count > 0 || DateTimeOffset.UtcNow < until;

        void Briefly(TimeSpan span) { if (DateTimeOffset.UtcNow + span > until) { until = DateTimeOffset.UtcNow + span; } }

        public static PersonScroll Hook(ScrollViewer sv, Action scrolled)
        {
            var p = new PersonScroll();
            p.wheel = (_, _) => p.Briefly(Moment);
            p.key = (_, _) => p.Briefly(Moment);
            p.pressed = (_, e) => { if (e.OriginalSource is DependencyObject s && Above<ScrollBar>(s, sv)) { p.bar.Add(e.Pointer.PointerId); } };
            p.released = (_, e) => { if (p.bar.Remove(e.Pointer.PointerId)) { p.Briefly(Step); } };
            p.started = (_, _) => p.manipulating = true;
            p.completed = (_, _) => { p.manipulating = false; p.Briefly(Step); };
            p.changed = (_, _) =>
            {
                if (p.Scrolling) { p.Briefly(Step); }
                scrolled();
            };
            sv.AddHandler(UIElement.PointerWheelChangedEvent, p.wheel, true);
            sv.AddHandler(UIElement.KeyDownEvent, p.key, true);
            sv.AddHandler(UIElement.PointerPressedEvent, p.pressed, true);
            sv.AddHandler(UIElement.PointerReleasedEvent, p.released, true);
            sv.AddHandler(UIElement.PointerCaptureLostEvent, p.released, true);
            sv.DirectManipulationStarted += p.started;
            sv.DirectManipulationCompleted += p.completed;
            sv.ViewChanged += p.changed;
            return p;
        }

        public void Unhook(ScrollViewer sv)
        {
            sv.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
            sv.RemoveHandler(UIElement.KeyDownEvent, key);
            sv.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            sv.RemoveHandler(UIElement.PointerReleasedEvent, released);
            sv.RemoveHandler(UIElement.PointerCaptureLostEvent, released);
            sv.DirectManipulationStarted -= started;
            sv.DirectManipulationCompleted -= completed;
            sv.ViewChanged -= changed;
        }

        static bool Above<T>(DependencyObject node, DependencyObject stop) where T : DependencyObject
        {
            for (var up = node; up is not null && up != stop; up = VisualTreeHelper.GetParent(up))
            {
                if (up is T) { return true; }
            }
            return false;
        }
    }

    // The answer a pointer pressed, if it pressed one: the button or text box it is in, in the holder.
    public FrameworkElement? AnswerAt(DependencyObject? source)
    {
        for (var up = source; up is not null && up != holder; up = VisualTreeHelper.GetParent(up))
        {
            if (up is ButtonBase or TextBox) { return (FrameworkElement)up; }
        }
        return null;
    }
}
