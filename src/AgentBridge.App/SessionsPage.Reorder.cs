using System.Diagnostics;
using System.Numerics;
using AgentBridge.App.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace AgentBridge.App;

// Moving a session by lifting its card (ui.md §5.3 Lift and Reorder, §6.1 Moving, §9; #592, #594).
// Not the system's drag and drop, whose ghost and "Move above" caption were all a person saw: the
// card itself rises (Composition scale, the drag shadow, above every row) and follows the pointer
// with no easing, and the cards it passes ease one slot aside on `reorder`, so the gap where it will
// land opens under it. Near the list's edges the list scrolls; across the other group's header the
// card is pinned or unpinned. On release the order is saved at once and the card settles into the
// gap, so a press during the settle acts on the card in its new place: composition moves nothing
// that layout or hit testing sees. Escape, a release well outside the list, the system taking the
// pointer, a second finger or the window losing focus put it back.
//
// The pointer is the list's: a press anywhere on a card is seen here whoever handled it (the grip
// handles its own, so the list item neither shows it pressed nor takes it for a click), and the
// pointer is captured only once the card lifts, so a click still opens a session and a right-click
// its menu. The model (where the gap goes, what moves, what is said) is App.Core's ReorderDrag.
public sealed partial class SessionsPage
{
    // §6.1: a mouse or pen lifts the card once it has moved 4 with the button down, or at once on
    // the grip; a touch after a long press (Windows has no setting of its own: 500, as the PWA takes
    // where the platform has none), or 150 or a move of 4 on the grip. A finger that moves 10 first
    // scrolls. A release more than 32 outside the list cancels.
    const double Slop = 4;
    const double TouchSlop = 10;
    const double Outside = 32;
    static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan GripHold = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan SaidEvery = TimeSpan.FromMilliseconds(500);
    // The lifted card grows to 1.02 about its centre, and sinks to 0.985 while a touch holds it.
    const float LiftScale = 1.02f;
    const float SinkScale = 0.985f;

    sealed class Press
    {
        // None for the self-test's hold (Hold).
        public Pointer? Pointer { get; init; }
        public required SessionRow Row { get; init; }
        public required SessionCardViewModel Card { get; init; }
        public required ListViewItem Item { get; init; }
        public required Point Start { get; init; }
        public required bool Grip { get; init; }
        public required PointerDeviceType Kind { get; init; }
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? Timer { get; set; }
        public long Pressed { get; } = Stopwatch.GetTimestamp();
    }

    sealed class Drag
    {
        public required SessionCardViewModel Card { get; init; }
        public required ListViewItem Item { get; set; }
        public required SessionRow Row { get; set; }
        public required ReorderDrag Model { get; set; }
        public Pointer? Pointer { get; init; }
        public PointerDeviceType Kind { get; init; }
        // Lifted by a touch held still: let go without moving, it opens the menu instead.
        public bool ByHold { get; init; }
        public required Point Start { get; init; }
        // Where the pointer took hold, below the card's top, so the card follows from there.
        public double Grab { get; init; }
        public double ItemTop { get; set; }
        public double Header { get; set; }
        public ReorderTarget Target { get; set; }
        public ReorderLayout? Layout { get; set; }
        public bool Away { get; set; }
        public bool Moved { get; set; }
        public Point At { get; set; }
        public double Translation { get; set; }
        public double Speed { get; set; }
        public double? ScrollTo { get; set; }
        public long LastFrame { get; set; }
        public bool Scrolled { get; set; }
        // Every other card's item drawn now, by key, and the keys of those not drawn.
        public Dictionary<string, ListViewItem> Items { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SessionCardViewModel> Unrealized { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SessionCardViewModel> Cards { get; } = new(StringComparer.Ordinal);
        public long SaidAt { get; set; }
        public string? Unsaid { get; set; }
        // The lift's growth, from what scale and since when, so the scale it is drawn at can be worked out
        // (Composition reads back a property's set value, not the one an animation draws).
        public float LiftFrom { get; set; } = 1;
        public long LiftStart { get; set; }
        public MotionStep Lift { get; set; } = MotionStep.None;

        public float Scale => !Lift.Runs ? 1
            : LiftFrom + ((LiftScale - LiftFrom) * (float)Lift.Ease.At(Stopwatch.GetElapsedTime(LiftStart) / Lift.Duration));
    }

    Press? press;
    Drag? drag;
    // A drop or cancel settling, or a held card fading as its session ends: the items it still moves,
    // each with what its end does, done when it ends, another lifts, or the list recycles the item.
    readonly Dictionary<ListViewItem, Action> settling = [];
    Microsoft.UI.Dispatching.DispatcherQueueTimer? sayTimer;

    // A touch is on a card the list may lift: the row's own long-press menu waits for it.
    internal bool HoldsTouch => press?.Kind == PointerDeviceType.Touch || drag?.Kind == PointerDeviceType.Touch;
    internal bool Dragging => drag is not null;
    // How many motions a drag started, for the self-test: none with Windows' animation effects off.
    internal int ReorderMotions { get; private set; }

    void WatchReorder()
    {
        List.AddHandler(PointerPressedEvent, new PointerEventHandler(OnCardPressed), true);
        List.AddHandler(PointerMovedEvent, new PointerEventHandler(OnCardMoved), true);
        List.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnCardReleased), true);
        List.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnCardCanceled), true);
        List.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnCardCaptureLost), true);
        // Needs you, the list's header, changing height moves the cards under a settling one: the layer
        // goes with them. (While a card is held, OnFrame keeps it under the pointer.)
        ListHeader.SizeChanged += (_, _) => { if (drag is null && settling.Count > 0) { PlaceLayer(); } };
    }

    // The main view model's changes that end a drag: the sort leaving Your order, another machine's
    // tab, and the held session ending.
    void WatchHeld(MainViewModel main)
    {
        main.HeldGone += card => { if (drag?.Card == card) { Vanish(); } };
        main.PropertyChanged += (_, e) =>
        {
            if (drag is not null && e.PropertyName is nameof(MainViewModel.SortIndex) or nameof(MainViewModel.SelectedMachine)) { Cancel(say: true); }
        };
    }

    // --- The pointer. ---

    void OnCardPressed(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.Pointer.PointerDeviceType;
        // A second finger (a pinch) puts a held card back, and lifts nothing.
        if (drag is not null)
        {
            if (kind == PointerDeviceType.Touch && e.Pointer.PointerId != drag.Pointer?.PointerId) { Cancel(say: true); }
            return;
        }
        if (press is not null) { Unpress(); return; }
        SessionRow.TouchGrips = kind == PointerDeviceType.Touch || (kind != PointerDeviceType.Mouse && SessionRow.TouchGrips);
        if (viewModel is null || Layout?.ShowList != true || RowOf(e.OriginalSource as DependencyObject) is not { Card: { Row.CanDrag: true } card } row) { return; }
        if (List.ContainerFromItem(card) is not ListViewItem item) { return; }
        var point = e.GetCurrentPoint(List);
        var p = point.Properties;
        if (kind != PointerDeviceType.Touch && (!p.IsLeftButtonPressed || p.IsRightButtonPressed || p.IsBarrelButtonPressed || p.IsEraser)) { return; }
        var grip = Motion.Within(e.OriginalSource as DependencyObject, row.GripHit);
        press = new Press { Pointer = e.Pointer, Kind = kind, Row = row, Card = card, Item = item, Start = point.Position, Grip = grip };
        if (grip && kind != PointerDeviceType.Touch) { Lift(press, point.Position, byHold: false); return; }
        if (kind != PointerDeviceType.Touch) { return; }
        Wait(press);
    }

    // A touch: held still long enough, it lifts. On the card, it sinks while it waits.
    void Wait(Press waiting)
    {
        var (grip, item) = (waiting.Grip, waiting.Item);
        waiting.Timer = DispatcherQueue.CreateTimer();
        waiting.Timer.IsRepeating = false;
        waiting.Timer.Interval = grip ? GripHold : LongPress;
        waiting.Timer.Tick += (_, _) => { if (press == waiting) { Lift(waiting, waiting.Start, byHold: true); } };
        waiting.Timer.Start();
        if (!grip) { Sink(item); }
    }

    void OnCardMoved(object sender, PointerRoutedEventArgs e)
    {
        if (drag is { } d)
        {
            if (e.Pointer.PointerId != d.Pointer?.PointerId) { return; }
            e.Handled = true;
            Follow(e.GetCurrentPoint(List).Position);
            return;
        }
        if (press is not { } p || e.Pointer.PointerId != p.Pointer?.PointerId) { return; }
        var at = e.GetCurrentPoint(List);
        var far = Distance(at.Position, p.Start);
        if (p.Kind == PointerDeviceType.Touch)
        {
            if (p.Grip) { if (far > Slop) { Lift(p, at.Position, byHold: false); } }
            // A finger that moves first is a scroll: the list's own.
            else if (far > TouchSlop) { Unpress(); }
        }
        else if (at.IsInContact && far > Slop) { Lift(p, at.Position, byHold: false); }
    }

    void OnCardReleased(object sender, PointerRoutedEventArgs e)
    {
        if (drag is { } d && e.Pointer.PointerId == d.Pointer?.PointerId)
        {
            // The release is the drag's: the list item under it takes no click.
            e.Handled = true;
            Release();
            return;
        }
        if (press is { } p && e.Pointer.PointerId == p.Pointer?.PointerId)
        {
            if (p.Kind == PointerDeviceType.Touch && p.Grip) { Hint(p.Row); }
            Unpress();
        }
    }

    // The system took the pointer: a pan that took a touch over, or anything else.
    void OnCardCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (drag is { } d && e.Pointer.PointerId == d.Pointer?.PointerId) { Cancel(say: true); }
        else if (press is { } p && e.Pointer.PointerId == p.Pointer?.PointerId) { Unpress(); }
    }

    // Only the list's own capture: the list item's, which it gives up as the list takes the pointer,
    // bubbles here too.
    void OnCardCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (drag is { } d && e.Pointer.PointerId == d.Pointer?.PointerId && ReferenceEquals(e.OriginalSource, List)) { Cancel(say: true); }
    }

    static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    void Unpress()
    {
        if (press is not { } p) { return; }
        press = null;
        p.Timer?.Stop();
        if (drag?.Item != p.Item) { Unsink(p.Item); }
    }

    // A tap on a grip, on touch: how to move a session, shown and said.
    void Hint(SessionRow row)
    {
        GripHint.Target = row.GripHit;
        GripHint.IsOpen = true;
        row.Say(GripHint.Subtitle);
    }

    // The window lost focus: a held card goes back.
    internal void Deactivated()
    {
        Unpress();
        if (drag is not null) { Cancel(say: true); }
    }

    // --- Lifting. ---

    void Lift(Press p, Point at, bool byHold)
    {
        press = null;
        p.Timer?.Stop();
        // Still a card a drag can move, in a list on screen: a hold's wait may have outlasted either (the
        // sort changed, the list went for the session).
        if (viewModel is null || p.Card.Row?.CanDrag != true || Layout is not { ShowList: true, OnScreen: true } || List.ItemsPanelRoot is null || List.ContainerFromItem(p.Card) is not ListViewItem item || item.ContentTemplateRoot is not SessionRow row)
        {
            Unsink(p.Item);
            return;
        }
        // The touch is the card's from here: the list's pan lets it go, and the list holds it.
        if (p.Kind == PointerDeviceType.Touch) { item.CancelDirectManipulations(); }
        if (p.Pointer is { } pointer && !List.CapturePointer(pointer))
        {
            Unsink(item);
            return;
        }
        Begin(p.Card, item, row, at, p.Pointer, p.Kind, byHold);
    }

    // The self-test's drag, through the same steps as a pointer's, without one to capture.
    internal bool Begin(SessionCardViewModel card, Point at)
    {
        if (drag is not null || card.Row?.CanDrag != true || List.ContainerFromItem(card) is not ListViewItem { ContentTemplateRoot: SessionRow row } item) { return false; }
        Begin(card, item, row, at, null, PointerDeviceType.Mouse, byHold: false);
        return drag is not null;
    }

    // The self-test's touch held on a card, without a finger: it lifts when its wait ends, as a touch's.
    internal void Hold(SessionCardViewModel card)
    {
        if (drag is not null || press is not null || List.ContainerFromItem(card) is not ListViewItem { ContentTemplateRoot: SessionRow row } item) { return; }
        var at = item.TransformToVisual(List).TransformPoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
        Wait(press = new Press { Kind = PointerDeviceType.Touch, Row = row, Card = card, Item = item, Start = at, Grip = false });
    }

    // How far the held card is drawn from where the pointer holds it: measured afresh, the item where the
    // list lays it out now and the translation its visual has, not the drag's own figures.
    internal double? HeldDrift
    {
        get
        {
            if (drag is not { } d || List.ItemsPanelRoot is not { } panel) { return null; }
            Visual(d.Item).Properties.TryGetVector3("Translation", out var drawn);
            var top = LaidOut(d.Item).Y;
            return List.TransformToVisual(panel).TransformPoint(d.At).Y - d.Grab - top - drawn.Y;
        }
    }
    internal FrameworkElement ListHeaderView => ListHeader;

    internal void MoveTo(Point at) { if (drag is not null) { Follow(at); } }
    internal void LetGo() { if (drag is not null) { Release(); } }
    internal void PutBack() { if (drag is not null) { Cancel(say: true); } }
    internal ReorderTarget? Target => drag?.Target;
    internal ReorderTarget? Origin => drag?.Model.Origin;
    // Where a drag has moved an item to, from where the list lays it out.
    internal double? ShiftOf(UIElement item) => glides.TryGetValue(item, out var g) ? g.To : null;
    internal double GapHeight => drag?.Model.GapHeight ?? 0;
    // Needs you taking no click (AnswerGuard).
    internal bool LaneHeld => !LaneItems.IsHitTestVisible;
    // The gap drawn as high contrast draws it: a dashed outline, nothing filling it.
    internal bool GapDashed => ReorderGapDashes.Visibility == Visibility.Visible && ReorderGapFill.Visibility == Visibility.Collapsed;
    internal ListViewItem? HeldItem => drag?.Item;

    void Begin(SessionCardViewModel card, ListViewItem item, SessionRow row, Point at, Pointer? pointer, PointerDeviceType kind, bool byHold)
    {
        if (viewModel is null || List.ItemsPanelRoot is not { } panel) { return; }
        FinishSettling();
        var itemTop = LaidOut(item).Y;
        var panelAt = List.TransformToVisual(panel).TransformPoint(at);
        var d = new Drag
        {
            Card = card,
            Item = item,
            Row = row,
            Model = null!,
            Pointer = pointer,
            Kind = kind,
            ByHold = byHold,
            Start = at,
            At = at,
            Grab = panelAt.Y - itemTop,
            ItemTop = itemTop,
            Header = row.HeaderPart,
        };
        if (!Measure(d)) { Unsink(item); return; }
        drag = d;
        d.Target = d.Model.Origin;
        viewModel.Held = card;
        Tick(pointer);

        // The card rises: above every row and header, its look faded in as it grows to 1.02 (Lift).
        // With animation effects off, or in high contrast, a 2 px Bridge outline and no shadow or tint;
        // with them off it neither grows nor fades.
        var outline = !Motion.Enabled || Contrast.HighContrast;
        row.ShowLift(outline, item.IsSelected);
        Canvas.SetZIndex(item, 100);
        var visual = Visual(item);
        visual.CenterPoint = new Vector3((float)(item.ActualWidth / 2), (float)(d.Header + (d.Model.GapHeight / 2)), 0);
        if (d.Header > 0) { visual.Clip = visual.Compositor.CreateInsetClip(0, (float)d.Header - 2, 0, 0); }
        var lift = Motion.Catalogue.Enter("Lift", Motion.Enabled);
        if (lift.Runs)
        {
            d.LiftFrom = SunkTo(item);
            d.LiftStart = Stopwatch.GetTimestamp();
            d.Lift = lift;
            Animate(visual, "Scale", new Vector3(d.LiftFrom, d.LiftFrom, 1), new Vector3(LiftScale, LiftScale, 1), lift);
            // Tracked, so the row puts its opacity back when it is shown again (Motion.Settle).
            Motion.Track(row.LiftLayer);
            var look = Visual(row.LiftLayer);
            look.Opacity = 0;
            AnimateOpacity(look, 0, 1, lift);
        }
        else
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
        }
        sunk.Remove(item);

        // The headers are drawn where they are to be from here, not by the rows (§6.1, Groups).
        ShowLayer(d);
        XamlRoot.Content.AddHandler(KeyDownEvent, escape, true);
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        // Needs you takes no click while a card is held, wherever the pointer goes (AnswerGuard).
        GuardLane();
        Arrange(d, d.Target, instant: true);
        Follow(at);
        var (place, of) = d.Model.Place(d.Target);
        d.SaidAt = Stopwatch.GetTimestamp();
        row.Say(ReorderWords.PickedUp(card.Title, (place, of), card.Pinned));
    }

    KeyEventHandler? escapeHandler;
    KeyEventHandler escape => escapeHandler ??= (_, e) =>
    {
        if (drag is null || e.Key != VirtualKey.Escape) { return; }
        e.Handled = true;
        Cancel(say: true);
    };

    // One light tick as the card lifts, where the hardware has one (§6.1, D4): a haptic pen's.
    // Windows' touch screens have none to ask for, so a finger gets nothing.
    static void Tick(Pointer? pointer)
    {
        if (pointer is not { PointerDeviceType: PointerDeviceType.Pen }) { return; }
        try
        {
            var haptics = Windows.Devices.Input.PenDevice.GetFromPointerId(pointer.PointerId)?.SimpleHapticsController;
            var click = haptics?.SupportedFeedback.FirstOrDefault(f => f.Waveform == Windows.Devices.Haptics.KnownSimpleHapticsControllerWaveforms.Click);
            if (haptics is not null && click is not null) { haptics.SendHapticFeedback(click); }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException) { }
    }

    // Each card's place in the list's panel as laid out now. Cards the list hasn't drawn (far off
    // screen) are placed by the drawn ones' height, and measured once auto-scroll draws them.
    bool Measure(Drag d)
    {
        if (viewModel is null || List.ItemsPanelRoot is not { } panel) { return false; }
        var cards = viewModel.Sessions.ToList();
        var drawn = new (double Top, double Header, double Height)?[cards.Count];
        d.Items.Clear();
        d.Unrealized.Clear();
        d.Cards.Clear();
        for (var i = 0; i < cards.Count; i++)
        {
            var key = viewModel.KeyOf(cards[i]);
            d.Cards[key] = cards[i];
            if (List.ContainerFromItem(cards[i]) is ListViewItem { ContentTemplateRoot: SessionRow r } li && li.Visibility == Visibility.Visible && li.ActualHeight > 0)
            {
                drawn[i] = (LaidOut(li).Y, r.HeaderPart, li.ActualHeight);
                if (cards[i] != d.Card) { d.Items[key] = li; }
            }
            else { d.Unrealized[key] = cards[i]; }
        }
        // The space between two drawn cards, and a card's height without a header, for those not drawn.
        double space = 0, plain = 0, heading = 0;
        for (var i = 0; i < cards.Count; i++)
        {
            if (drawn[i] is not { } a) { continue; }
            if (i + 1 < cards.Count && drawn[i + 1] is { } b) { space = Math.Max(0, b.Top - (a.Top + a.Height)); }
            if (a.Header == 0) { plain = a.Height; } else { heading = a.Header; }
        }
        var first = Array.FindIndex(drawn, x => x is not null);
        if (first < 0) { return false; }
        plain = plain > 0 ? plain : drawn[first]!.Value.Height - drawn[first]!.Value.Header;
        var list = new ReorderCard[cards.Count];
        double Extent(int i) => i + 1 < cards.Count && drawn[i] is { } a && drawn[i + 1] is { } b ? b.Top - a.Top : (drawn[i]?.Height ?? 0) + space;
        // Those above the first drawn card, upwards from it; then each below from the one before.
        var top = drawn[first]!.Value.Top;
        for (var i = first - 1; i >= 0; i--)
        {
            var header = cards[i].HasGroupHeader ? heading : 0;
            top -= plain + header + space;
            list[i] = new ReorderCard(viewModel.KeyOf(cards[i]), cards[i].Pinned, viewModel.IsFixed(cards[i]), top, header, plain + header + space);
        }
        for (var i = first; i < cards.Count; i++)
        {
            if (drawn[i] is { } a) { list[i] = new ReorderCard(viewModel.KeyOf(cards[i]), cards[i].Pinned, viewModel.IsFixed(cards[i]), a.Top, a.Header, Extent(i)); continue; }
            var prev = list[i - 1];
            var header = cards[i].HasGroupHeader ? heading : 0;
            list[i] = new ReorderCard(viewModel.KeyOf(cards[i]), cards[i].Pinned, viewModel.IsFixed(cards[i]), prev.Top + prev.Extent, header, plain + header + space);
        }
        d.Model = new ReorderDrag(list, viewModel.KeyOf(d.Card));
        return true;
    }

    // --- Holding. ---

    void Follow(Point at)
    {
        if (drag is not { } d || List.ItemsPanelRoot is not { } panel) { return; }
        d.At = at;
        // A card auto-scroll has drawn since, or the cards laid out again elsewhere in the panel (the
        // list's header, Needs you, changing height): measured again, and put where they belong at once.
        var laidOut = LaidOut(d.Item).Y;
        if (Math.Abs(laidOut - d.ItemTop) >= 0.5 || d.Unrealized.Values.Any(c => List.ContainerFromItem(c) is ListViewItem { ContentTemplateRoot: SessionRow }))
        {
            var target = d.Target;
            if (Measure(d))
            {
                d.ItemTop = laidOut;
                d.Target = d.Model.Allows(target) ? target : d.Model.Origin;
                ShowLayer(d);
                Arrange(d, d.Target, instant: true);
            }
        }
        var panelAt = List.TransformToVisual(panel).TransformPoint(at);
        // 1:1 with the pointer and with no easing, up and down only, from where it was taken hold of.
        d.Translation = panelAt.Y - d.Grab - d.ItemTop;
        var visual = Visual(d.Item);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", new Vector3(0, (float)d.Translation, 0));
        Motion.Track(d.Item);
        if (Distance(at, d.Start) > Slop) { d.Moved = true; }

        var away = at.X < -Outside || at.Y < -Outside || at.X > List.ActualWidth + Outside || at.Y > List.ActualHeight + Outside;
        var next = away ? d.Model.Origin : d.Model.Nearest(d.ItemTop + d.Translation + d.Header + (d.Model.GapHeight / 2), d.Target);
        var changed = away != d.Away;
        d.Away = away;
        if (next != d.Target) { Arrange(d, next, instant: false); Say(d); }
        else if (changed) { Chip(d); }
        PlaceLayer();
        // Auto-scroll, once the card has moved since it lifted: a card lifted in the zone doesn't scroll.
        d.Speed = d.Moved && !away ? ReorderDrag.ScrollSpeed(at.Y, 0, List.ActualHeight) : 0;
        if (d.Speed == 0) { d.ScrollTo = null; }
    }

    // The gap at a place: the cards and headers between ease there on `reorder` (or jump, with
    // animation effects off), and the gap and the target header's cue follow.
    void Arrange(Drag d, ReorderTarget target, bool instant)
    {
        d.Target = target;
        var layout = d.Layout = d.Model.Layout(target);
        foreach (var (key, item) in d.Items)
        {
            Glide(item, layout.Shifts.TryGetValue(key, out var shift) ? shift : 0, instant);
        }
        var crosses = d.Model.Crosses(target) && !d.Away;
        if (layout.PinnedHeader is { } p) { GlideHeading(PinnedHeading, PinnedHeadingCue, pinnedFrom, p, crosses && target.Pinned, instant); }
        if (layout.SessionsHeader is { } s) { GlideHeading(SessionsHeading, SessionsHeadingCue, sessionsFrom, s, crosses && !target.Pinned, instant); }
        Glide(ReorderGap, layout.GapTop - gapFrom, instant);
        ReorderKeep.Visibility = layout.Placeholder is not null ? Visibility.Visible : Visibility.Collapsed;
        if (layout.Placeholder is { } keep) { Canvas.SetTop(ReorderKeep, keep + 2); }
        ReorderKeepText.Text = ReorderWords.Keep(d.Card.Pinned);
        Chip(d);
    }

    void Chip(Drag d) => d.Row.ShowChip(d.Away ? ReorderWords.Cancel : d.Model.Crosses(d.Target) ? ReorderWords.Chip(d.Target.Pinned) : null);

    // Where each heading and the gap were drawn as the card lifted: they glide from there.
    double pinnedFrom;
    double sessionsFrom;
    double gapFrom;
    double headingInset;

    void GlideHeading(TextBlock plain, TextBlock cue, double from, double top, bool target, bool instant)
    {
        plain.Visibility = target ? Visibility.Collapsed : Visibility.Visible;
        cue.Visibility = target ? Visibility.Visible : Visibility.Collapsed;
        Glide(plain, top - from, instant);
        Glide(cue, top - from, instant);
    }

    // The layer behind the list: the gap where the card left, the headings where the rows drew them,
    // the rows' own headings hidden and their items clipped below them, so a header never moves with
    // a card it sits on.
    void ShowLayer(Drag d)
    {
        if (List.ItemsPanelRoot is not { } panel) { return; }
        var origin = d.Model.Layout(d.Model.Origin);
        var width = d.Item.ActualWidth;
        var left = LaidOut(d.Item).X;
        var headingAt = HeadingPlace(d, panel);
        headingInset = headingAt.Y;
        pinnedFrom = origin.PinnedHeader ?? 0;
        sessionsFrom = origin.SessionsHeader ?? 0;
        gapFrom = origin.GapTop;
        PinnedHeading.Text = ReorderWords.Group(pinned: true);
        PinnedHeadingCue.Text = ReorderWords.Group(pinned: true) + ReorderWords.HeaderCue(pinned: true);
        SessionsHeading.Text = ReorderWords.Group(pinned: false);
        SessionsHeadingCue.Text = ReorderWords.Group(pinned: false) + ReorderWords.HeaderCue(pinned: false);
        foreach (var (heading, top, shown) in new[] { (PinnedHeading, pinnedFrom, origin.PinnedHeader is not null), (PinnedHeadingCue, pinnedFrom, false), (SessionsHeading, sessionsFrom, origin.SessionsHeader is not null), (SessionsHeadingCue, sessionsFrom, false) })
        {
            Canvas.SetLeft(heading, left + headingAt.X);
            Canvas.SetTop(heading, top + headingInset);
            heading.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }
        // The plate's edges in the item (SessionRow's LiftLook): 4 in from each side, 2 from the top and bottom.
        foreach (var plate in new FrameworkElement[] { ReorderGap, ReorderKeep })
        {
            Canvas.SetLeft(plate, left + 4);
            plate.Width = Math.Max(0, width - 8);
            plate.Height = Math.Max(0, d.Model.GapHeight - 4);
        }
        Canvas.SetTop(ReorderGap, gapFrom + 2);
        // High contrast: the gap is a 1 px dashed GrayText outline, and nothing fills it.
        var contrast = Contrast.HighContrast;
        ReorderGapFill.Visibility = contrast ? Visibility.Collapsed : Visibility.Visible;
        ReorderGapDashes.Visibility = contrast ? Visibility.Visible : Visibility.Collapsed;
        if (contrast && Application.Current.Resources.TryGetValue("SystemColorGrayTextColor", out var gray) && gray is Windows.UI.Color c)
        {
            ReorderGapDashes.Stroke = ReorderKeepDashes.Stroke = new SolidColorBrush(c);
        }
        foreach (var item in d.Items.Values.Append(d.Item))
        {
            if (item.ContentTemplateRoot is not SessionRow r) { continue; }
            r.HideHeading(true);
            var header = r.HeaderPart;
            if (header > 0 && item != d.Item) { Visual(item).Clip = Visual(item).Compositor.CreateInsetClip(0, (float)header, 0, 0); clipped.Add(item); }
        }
        ReorderContent.Visibility = Visibility.Visible;
        ElementCompositionPreview.GetElementVisual(ReorderLayer).Clip ??= ElementCompositionPreview.GetElementVisual(ReorderLayer).Compositor.CreateInsetClip();
        PlaceLayer();
    }

    // Where a row draws its heading in its item, from one that has one, or the row's padding.
    static Point HeadingPlace(Drag d, UIElement panel)
    {
        foreach (var item in d.Items.Values.Prepend(d.Item))
        {
            if (item.ContentTemplateRoot is SessionRow { HeaderPart: > 0 } r) { return r.HeadingOffset(item); }
        }
        return new Point(16, 14);
    }

    // The layer's content in the panel's coordinates, wherever the list has scrolled to.
    void PlaceLayer()
    {
        if (List.ItemsPanelRoot is not { } panel || ReorderContent.Visibility != Visibility.Visible) { return; }
        var o = panel.TransformToVisual(ReorderLayer).TransformPoint(default);
        ReorderContent.RenderTransform = new TranslateTransform { X = o.X, Y = o.Y };
    }

    // Said as the card's place changes, at most every 500 ms; the latest place once the wait is over.
    void Say(Drag d)
    {
        var (place, of) = d.Model.Place(d.Target);
        d.Unsaid = ReorderWords.Held((place, of), d.Model.Crosses(d.Target), d.Target.Pinned);
        var wait = SaidEvery - Stopwatch.GetElapsedTime(d.SaidAt);
        if (wait <= TimeSpan.Zero) { SayNow(d); return; }
        sayTimer ??= DispatcherQueue.CreateTimer();
        sayTimer.IsRepeating = false;
        sayTimer.Interval = wait;
        sayTimer.Tick -= OnSayTimer;
        sayTimer.Tick += OnSayTimer;
        sayTimer.Start();
    }

    void OnSayTimer(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object e) { if (drag is { } d) { SayNow(d); } }

    void SayNow(Drag d)
    {
        if (d.Unsaid is not { } text) { return; }
        d.Unsaid = null;
        d.SaidAt = Stopwatch.GetTimestamp();
        d.Row.Say(text);
    }

    // Each frame while held near an edge: the list scrolls by speed × the frame's time, the fraction
    // carried to the next, and stops at once outside the zone. The card follows as the list moves
    // (OnListScrolled).
    void OnFrame(object? sender, object e)
    {
        if (drag is not { } d) { return; }
        // Anything that laid the cards out again under a still pointer (Needs you, the list's header,
        // changing height as a question arrives or is answered, and the list's scroll anchoring after it,
        // which no event reports in order): the card is put back under the pointer, the cards measured
        // again, and the gap and headings with them.
        if (List.ItemsPanelRoot is { } panel)
        {
            var pointer = List.TransformToVisual(panel).TransformPoint(d.At).Y;
            var laidOut = LaidOut(d.Item).Y;
            if (Math.Abs(pointer - d.Grab - laidOut - d.Translation) >= 0.5) { Follow(d.At); }
        }
        if (listScroll is not { } scroll) { return; }
        var now = Stopwatch.GetTimestamp();
        var dt = d.LastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(d.LastFrame, now).TotalSeconds;
        d.LastFrame = now;
        if (d.Speed == 0 || dt <= 0) { return; }
        var from = d.ScrollTo ?? scroll.VerticalOffset;
        var to = Math.Clamp(from + (d.Speed * Math.Min(dt, 0.1)), 0, scroll.ScrollableHeight);
        d.ScrollTo = to;
        if (Math.Abs(to - scroll.VerticalOffset) < 0.01) { return; }
        d.Scrolled = true;
        scroll.ChangeView(null, to, null, disableAnimation: true);
    }

    // The list moved under a held card (auto-scroll): the card stays under the pointer, the gap
    // follows it, and Needs you, which scrolls with the list, takes no click for a moment.
    void OnListScrolled()
    {
        if (drag is { } d) { Follow(d.At); }
        else if (settling.Count > 0) { PlaceLayer(); }
        if (drag is not null || DateTime.UtcNow < reorderedUntil) { GuardLane(); }
    }

    // --- Letting go. ---

    void Release()
    {
        if (drag is not { } d) { return; }
        // Away from the list: back where it came from.
        if (d.Away) { Cancel(say: true); return; }
        // A touch held still and let go: the row's menu, where the finger is, and the card goes back.
        // The row showing the session after the cancel, which drew what was held back meanwhile: the one
        // it was lifted in may show another session now.
        if (d.ByHold && !d.Moved)
        {
            Cancel(say: false);
            if (List.ContainerFromItem(d.Card) is ListViewItem { ContentTemplateRoot: SessionRow row } && row.Card == d.Card)
            {
                row.OpenMenuAt(List.TransformToVisual(row).TransformPoint(d.At));
            }
            return;
        }
        Land(d);
    }

    // The order is saved at the release, and the card settles from where the pointer left it into its
    // new place (§5.3, Reorder); the cards it passed are already there.
    void Land(Drag d)
    {
        var target = d.Target;
        var crossed = d.Model.Crosses(target);
        var drawn = d.Model.Place(target);
        var from = d.ItemTop + d.Translation + d.Header;
        var scale = d.Scale;
        var gliding = Gliding(d);
        End(d);
        var moving = target != d.Model.Origin;
        // Placed among the cards as drawn: sessions that arrived or ended while it was held, which the
        // list draws only now, don't shift where it lands.
        var moved = moving && d.Card.Row?.MoveTo(target.Pinned, target.Index, d.Model.Group(target.Pinned)) == true;
        // Focus stays with the session: the dropped card asks for it, as after Alt+Up/Down.
        if (!moved) { d.Card.WantsRowFocus = true; }
        SettleWhereDrawn(d, from, scale, gliding, afterLayout: moved);
        FocusSession(d.Card, 10);
        reorderedUntil = DateTime.UtcNow.AddSeconds(1);
        GuardLane();
        d.Row.Say(ReorderWords.Dropped(PlaceNow(d, drawn), crossed, target.Pinned));
    }

    // Its place said as the list now has it, with the sessions held back while it was held drawn.
    (int Position, int Of) PlaceNow(Drag d, (int Position, int Of) drawn)
    {
        var group = viewModel?.Sessions.Where(c => c.Pinned == d.Card.Pinned).ToList();
        var at = group?.IndexOf(d.Card) ?? -1;
        return at < 0 ? drawn : (at + 1, group!.Count);
    }

    // The held card settles in the item showing its session once the list has drawn what changed as it
    // was let go (its move, the sessions held back meanwhile), which may be another item than it was
    // lifted in; every item the drag moved is put right, and the other cards' glides go on from where
    // they are drawn in whichever items now show them.
    void SettleWhereDrawn(Drag d, double from, float scale, Dictionary<SessionCardViewModel, double> gliding, bool afterLayout)
    {
        var items = d.Items.Values.Append(d.Item).ToList();
        void Here(FrameworkElement container)
        {
            Settle(d, container, from, scale, reset: items.Where(i => i != container));
            Resume(gliding);
        }
        void Gone()
        {
            Reset(items);
            HideLayer();
            Resume(gliding);
        }
        if (!afterLayout)
        {
            List.UpdateLayout();
            if (List.ContainerFromItem(d.Card) is ListViewItem container) { Here(container); } else { Gone(); }
            return;
        }
        var settled = false;
        Motion.AfterLayout(List, d.Card, container =>
        {
            settled = true;
            Here(container);
        });
        // Not drawn after the move (scrolled away): nothing to settle, everything put right.
        var fallback = DispatcherQueue.CreateTimer();
        fallback.IsRepeating = false;
        fallback.Interval = TimeSpan.FromMilliseconds(300);
        fallback.Tick += (t, _) =>
        {
            t.Stop();
            if (!settled) { Gone(); }
        };
        fallback.Start();
    }

    // How far each other card is drawn from where it is going, by session.
    Dictionary<SessionCardViewModel, double> Gliding(Drag d)
    {
        var now = Stopwatch.GetTimestamp();
        var left = new Dictionary<SessionCardViewModel, double>();
        foreach (var (key, item) in d.Items)
        {
            if (glides.TryGetValue(item, out var g) && d.Cards.TryGetValue(key, out var card) && Math.Abs(g.Now(now) - g.To) >= 0.5) { left[card] = g.Now(now) - g.To; }
        }
        return left;
    }

    // Those cards go on to their places from where they were drawn, rather than jumping there.
    void Resume(Dictionary<SessionCardViewModel, double> left)
    {
        var step = Motion.Catalogue.Enter("Reorder", Motion.Enabled);
        if (!step.Runs) { return; }
        var now = Stopwatch.GetTimestamp();
        foreach (var (card, offset) in left)
        {
            if (List.ContainerFromItem(card) is not ListViewItem item || settling.ContainsKey(item)) { continue; }
            Motion.Track(item);
            Animate(Visual(item), "Translation", new Vector3(0, (float)offset, 0), Vector3.Zero, step);
            glides[item] = new GlideState(offset, 0, now, step.Duration, step.Ease);
        }
    }

    // Keyboard focus with the dropped session (as after Alt+Up/Down, #410, #506): its list item once the
    // list has drawn it there. The row's own request (SessionRow.TakeFocus) can lose the race as the list
    // hands its items to sessions again after the move, a row control given the card then given another
    // before it asks, so the list asks too, a few times, until focus lands.
    void FocusSession(SessionCardViewModel card, int tries)
    {
        if (!card.WantsRowFocus || tries <= 0) { return; }
        // A frame or more apart: the list lays out its items again across a pass or two after the move.
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (!card.WantsRowFocus || XamlRoot is null) { return; }
            List.UpdateLayout();
            if (List.ContainerFromItem(card) is ListViewItem item && ReferenceEquals(item.Content, card) && item.Focus(FocusState.Keyboard))
            {
                card.WantsRowFocus = false;
                item.StartBringIntoView();
                return;
            }
            FocusSession(card, tries - 1);
        };
        timer.Start();
    }

    // Put back where it came from: the cards close the gap there, and the card settles into it.
    void Cancel(bool say)
    {
        if (drag is not { } d) { return; }
        var from = d.ItemTop + d.Translation + d.Header;
        var scale = d.Scale;
        d.Away = false;
        Arrange(d, d.Model.Origin, instant: false);
        var gliding = Gliding(d);
        End(d);
        SettleWhereDrawn(d, from, scale, gliding, afterLayout: false);
        reorderedUntil = DateTime.UtcNow.AddSeconds(1);
        GuardLane();
        if (say) { d.Row.Say(ReorderWords.Cancelled(PlaceNow(d, d.Model.Place(d.Model.Origin)))); }
    }

    // The held session ended (its host removed it): the drag ends, the cards close up at once, and
    // the card fades out where it is (§5.3, Arriving and leaving) before the list lets it go.
    void Vanish()
    {
        if (drag is not { } d) { return; }
        drag = null;
        reorderedUntil = DateTime.UtcNow.AddSeconds(1);
        StopHolding();
        // Said in place of anything still waiting to be said, while its row is still there to say it.
        d.Row.Say(ReorderWords.Ended(d.Card.Title));
        Reset(d.Items.Values);
        HideLayer();
        var visual = Visual(d.Item);
        var step = Motion.Catalogue.Exit("ListItem", Motion.Enabled);
        void Gone()
        {
            Reset([d.Item]);
            Release(d.Card);
        }
        if (!step.Runs) { Gone(); return; }
        settling[d.Item] = Gone;
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        AnimateOpacity(visual, 1, 0, step);
        batch.End();
        batch.Completed += (_, _) => { if (settling.Remove(d.Item, out var done)) { done(); } };
    }

    // The pointer, the keys and the frames let go; the list's own changes drawn again.
    void End(Drag d, bool later = false)
    {
        drag = null;
        reorderedUntil = DateTime.UtcNow.AddSeconds(1);
        StopHolding();
        if (d.Pointer is { } p) { List.ReleasePointerCapture(p); }
        if (later) { Release(d.Card); }
        else if (viewModel is not null && viewModel.Held == d.Card) { viewModel.Held = null; }
    }

    // The list's own changes held for a card drawn once the code changing the list now (a change to
    // Sessions being handed out, an item being recycled) has returned: Sessions takes no change while
    // it tells of one.
    void Release(SessionCardViewModel card) => DispatcherQueue.TryEnqueue(() =>
    {
        if (viewModel is not null && viewModel.Held == card) { viewModel.Held = null; }
    });

    void StopHolding()
    {
        sayTimer?.Stop();
        XamlRoot?.Content?.RemoveHandler(KeyDownEvent, escape);
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
    }

    DateTime reorderedUntil;

    // A row moved by Alt+Up/Down or its menu: Needs you is held as after a drop, the list scrolling it
    // into view moving the lane too.
    internal void Moved()
    {
        reorderedUntil = DateTime.UtcNow.AddSeconds(1);
        GuardLane();
    }

    // How long Needs you must stay held for a reorder: while a card is held, and until a second after it
    // is let go or a row is moved.
    TimeSpan ReorderHold() => drag is not null ? TimeSpan.FromMilliseconds(100) : reorderedUntil - DateTime.UtcNow;

    // The card lowers as it settles into its place over `reorder`: from where it was drawn to where
    // the list now lays it out, back to its size, its look fading. With animation effects off, at once.
    void Settle(Drag d, FrameworkElement container, double from, float scale, IEnumerable<ListViewItem> reset)
    {
        var resetting = reset.ToList();
        if (List.ItemsPanelRoot is not { } panel || container is not ListViewItem item || item.ContentTemplateRoot is not SessionRow row)
        {
            Reset(resetting.Append(d.Item));
            HideLayer();
            return;
        }
        // The cards it passed are where the list now lays them: their shifts end without a jump, as
        // the layout pass that put them there is the one this follows.
        Reset(resetting);
        var header = row.HeaderPart;
        var dy = from - (LaidOut(item).Y + header);
        var step = Motion.Catalogue.Enter("Reorder", Motion.Enabled);
        if (!step.Runs || Math.Abs(dy) < 0.5 && !row.Lifted)
        {
            Reset([item]);
            HideLayer();
            return;
        }
        settling[item] = () =>
        {
            Reset([item]);
            if (settling.Count == 0 && drag is null) { HideLayer(); }
        };
        Canvas.SetZIndex(item, 100);
        if (!row.Lifted) { row.ShowLift(!Motion.Enabled || Contrast.HighContrast, item.IsSelected); }
        // Its header, if it now has one, stays where it is drawn, in the layer, until the card is down.
        row.HideHeading(header > 0);
        var visual = Visual(item);
        visual.CenterPoint = new Vector3((float)(item.ActualWidth / 2), (float)(header + (row.ActualHeight - header) / 2), 0);
        visual.Clip = header > 0 ? visual.Compositor.CreateInsetClip(0, (float)header - 2, 0, 0) : null;
        ShowSettlingHeading(item, row, header, panel);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Animate(visual, "Translation", new Vector3(0, (float)dy, 0), Vector3.Zero, step);
        Motion.Track(item);
        Animate(visual, "Scale", new Vector3(scale, scale, 1), Vector3.One, step);
        Motion.Track(row.LiftLayer);
        AnimateOpacity(Visual(row.LiftLayer), 1, 0, step);
        batch.End();
        batch.Completed += (_, _) => { if (settling.Remove(item, out var done)) { done(); } };
    }

    // While a dropped card settles first in its group, its group's heading is the layer's, at its place;
    // the gap is gone, the card filling it.
    void ShowSettlingHeading(ListViewItem item, SessionRow row, double header, UIElement panel)
    {
        foreach (var e in new UIElement[] { PinnedHeadingCue, SessionsHeadingCue, ReorderGap, ReorderKeep }) { e.Visibility = Visibility.Collapsed; }
        // The rows' own headings stay hidden until the card is down: the layer's stay, where the list
        // now has them (gliding there still, after a cancel), for the groups the list now has.
        var pinned = viewModel?.Sessions.Any(c => c.Pinned) == true;
        PinnedHeading.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        SessionsHeading.Visibility = pinned && viewModel?.Sessions.Any(c => !c.Pinned) == true ? Visibility.Visible : Visibility.Collapsed;
        if (header <= 0) { ReorderContent.Visibility = Visibility.Visible; PlaceLayer(); return; }
        var heading = row.Card?.Pinned == true ? PinnedHeading : SessionsHeading;
        heading.Visibility = Visibility.Visible;
        var o = row.HeadingOffset(item);
        var top = LaidOut(item);
        Settle(heading);
        Canvas.SetLeft(heading, top.X + o.X);
        Canvas.SetTop(heading, top.Y + o.Y);
        ReorderContent.Visibility = Visibility.Visible;
        PlaceLayer();
    }

    void HideLayer()
    {
        ReorderContent.Visibility = Visibility.Collapsed;
        foreach (var e in new UIElement[] { PinnedHeading, PinnedHeadingCue, SessionsHeading, SessionsHeadingCue, ReorderGap, ReorderKeep }) { Settle(e); }
        ReorderGap.Visibility = Visibility.Visible;
        foreach (var row in RowsUnder(List)) { if (!row.Lifted) { row.HideHeading(false); } }
        foreach (var item in clipped) { if (!settling.ContainsKey(item)) { Visual(item).Clip = null; } }
        clipped.RemoveWhere(i => !settling.ContainsKey(i));
    }

    // The items whose header band a drag clipped away.
    readonly HashSet<ListViewItem> clipped = [];

    // The list changed under a held card by a person's own choice elsewhere (a pin from the session's
    // header, say): what it was measured from is gone, so it is put back at once.
    void Abandon()
    {
        if (drag is not { } d) { return; }
        End(d, later: true);
        Reset(d.Items.Values.Append(d.Item));
        HideLayer();
        d.Row.Say(ReorderWords.Cancelled(d.Model.Place(d.Model.Origin)));
    }

    // A settle still running when another card lifts ends where it was going.
    void FinishSettling()
    {
        if (settling.Count == 0) { return; }
        var ends = settling.Values.ToList();
        var items = settling.Keys.ToList();
        settling.Clear();
        foreach (var end in ends) { end(); }
        Reset(items);
        HideLayer();
    }

    // An item the list puts aside or gives another session while a drag or settle moves it.
    void ReorderRecycled(ListViewItem item)
    {
        if (drag is { } d)
        {
            if (d.Item == item) { Abandon(); return; }
            foreach (var key in d.Items.Where(p => p.Value == item).Select(p => p.Key).ToList())
            {
                d.Items.Remove(key);
                d.Unrealized[key] = d.Cards[key];
            }
        }
        if (settling.Remove(item, out var done)) { done(); }
        Reset([item]);
    }

    // Every drag motion off an item, which shows where the list lays it out.
    void Reset(IEnumerable<ListViewItem> items)
    {
        foreach (var item in items)
        {
            glides.Remove(item);
            Canvas.SetZIndex(item, 0);
            var visual = Visual(item);
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            visual.Clip = null;
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            sunk.Remove(item);
            clipped.Remove(item);
            if (item.ContentTemplateRoot is SessionRow row) { row.HideLift(); }
        }
    }

    // --- Motion. ---

    // Where the list lays an item out in its panel: not where it is drawn, which TransformToVisual gives,
    // with the drag's Translation and Scale on it (a held or gliding card).
    static Point LaidOut(FrameworkElement item)
    {
        var slot = LayoutInformation.GetLayoutSlot(item);
        return new Point(slot.X + item.Margin.Left, slot.Y + item.Margin.Top);
    }

    static Visual Visual(UIElement e)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        return ElementCompositionPreview.GetElementVisual(e);
    }

    // A slide that is interrupted retargets from where it is: each element's last glide, from where
    // to where and since when, so its place now can be worked out (MotionEase.At).
    readonly record struct GlideState(double From, double To, long Start, TimeSpan Duration, MotionEase Ease)
    {
        public double Now(long at) => Duration <= TimeSpan.Zero ? To : From + ((To - From) * Ease.At(Stopwatch.GetElapsedTime(Start, at) / Duration));
    }

    readonly Dictionary<UIElement, GlideState> glides = [];

    void Glide(UIElement element, double to, bool instant)
    {
        var now = Stopwatch.GetTimestamp();
        var was = glides.TryGetValue(element, out var g) ? g : new GlideState(0, 0, now, TimeSpan.Zero, default);
        if (Math.Abs(was.To - to) < 0.5 && glides.ContainsKey(element)) { return; }
        var visual = Visual(element);
        Motion.Track(element);
        var step = Motion.Catalogue.Enter("Reorder", Motion.Enabled);
        if (instant || !step.Runs)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", new Vector3(0, (float)to, 0));
            glides[element] = new GlideState(to, to, now, TimeSpan.Zero, default);
            return;
        }
        var from = was.Now(now);
        Animate(visual, "Translation", new Vector3(0, (float)from, 0), new Vector3(0, (float)to, 0), step);
        glides[element] = new GlideState(from, to, now, step.Duration, step.Ease);
    }

    static void Settle(UIElement element)
    {
        var visual = Visual(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    void Animate(Visual visual, string property, Vector3 from, Vector3 to, MotionStep step)
    {
        var c = visual.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, c.CreateCubicBezierEasingFunction(new Vector2((float)step.Ease.X1, (float)step.Ease.Y1), new Vector2((float)step.Ease.X2, (float)step.Ease.Y2)));
        a.Duration = step.Duration;
        visual.StartAnimation(property, a);
        ReorderMotions++;
    }

    void AnimateOpacity(Visual visual, float from, float to, MotionStep step)
    {
        var c = visual.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, c.CreateCubicBezierEasingFunction(new Vector2((float)step.Ease.X1, (float)step.Ease.Y1), new Vector2((float)step.Ease.X2, (float)step.Ease.Y2)));
        a.Duration = step.Duration;
        visual.Opacity = to;
        visual.StartAnimation("Opacity", a);
        ReorderMotions++;
    }

    // While a touch holds a card, before it lifts, the card sinks to 0.985, linearly over the hold,
    // so the hold shows its progress (§5.3, Lift). Not with animation effects off.
    readonly Dictionary<ListViewItem, long> sunk = [];

    void Sink(ListViewItem item)
    {
        if (!Motion.Enabled) { return; }
        var visual = Visual(item);
        visual.CenterPoint = new Vector3((float)(item.ActualWidth / 2), (float)(item.ActualHeight / 2), 0);
        var a = visual.Compositor.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, Vector3.One);
        a.InsertKeyFrame(1, new Vector3(SinkScale, SinkScale, 1), visual.Compositor.CreateLinearEasingFunction());
        a.Duration = LongPress;
        visual.StartAnimation("Scale", a);
        sunk[item] = Stopwatch.GetTimestamp();
        ReorderMotions++;
    }

    float SunkTo(ListViewItem item) =>
        sunk.TryGetValue(item, out var at) ? 1 - ((1 - SinkScale) * (float)Math.Min(1, Stopwatch.GetElapsedTime(at) / LongPress)) : 1;

    void Unsink(ListViewItem item)
    {
        if (!sunk.Remove(item)) { return; }
        var visual = Visual(item);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
    }
}

// Windows' high contrast, read as each drag starts.
static class Contrast
{
    static readonly Windows.UI.ViewManagement.AccessibilitySettings Settings = new();
    public static bool HighContrast => Settings.HighContrast;
}
