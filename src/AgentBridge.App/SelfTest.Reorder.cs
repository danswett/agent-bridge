using System.Runtime.InteropServices;
using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentBridge.App;

// Moving a session by lifting its card (ui.md §5.3 Lift and Reorder, §6.1 Moving; #594): the card
// lifts above the list and the cards it passes make room, in both themes, pictured from the screen
// (the lift is Composition's, which a XAML render leaves out); across the Sessions header it says it
// will pin; Escape and a release far outside put it back; a drop saves the order where it landed,
// keeps focus with the session and holds Needs you's buttons for a moment; with animation effects off
// nothing animates; and a real mouse, driven through Windows' input, drags a card by its grip.
static partial class SelfTest
{
    static async Task CheckReorder(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var list = page.Rows;
        // The window, the machine tab, the sort and the session open as the checks before left them,
        // given back to the checks after.
        var size = window.AppWindow.Size;
        var sort = main.SortIndex;
        var machine = main.SelectedMachine;
        var open = main.Selected;
        main.SelectedMachine = main.Machines[0];
        main.SortIndex = (int)SessionSort.Yours;
        if (window.RootElement.ActualWidth < 1439) { Resize(window, 1440, 1000); }
        await Task.Delay(700);
        var original = main.Sessions.ToList();
        string Order() => string.Join(", ", main.Sessions.Select(c => c.Id));
        // Something pinned, so there are two groups and a header to cross.
        var pin = original.LastOrDefault(c => c.Row?.CanDrag == true);
        var movable = original.Where(c => c.Row?.CanDrag == true && c != pin).ToList();
        if (pin is null || movable.Count < 4)
        {
            check(false, $"finds four sessions to move and one to pin ({Order()})");
            main.SelectedMachine = machine;
            main.SortIndex = sort;
            main.Selected = open;
            window.AppWindow.Resize(size);
            return;
        }
        if (!pin.Pinned) { pin.Row!.TogglePin(); }
        // The list on screen (in one column, with no session over it), from its first card: Needs you
        // scrolled away above it, so the cards dragged here are on screen and clear of the edges.
        if (page.Layout?.Mode == PaneMode.One) { main.Selected = null; }
        await Task.Delay(500);
        list.ScrollIntoView(main.Sessions[0], ScrollIntoViewAlignment.Leading);
        await Task.Delay(500);
        var before = Order();

        ListViewItem? Item(SessionCardViewModel c) => list.ContainerFromItem(c) as ListViewItem;
        // Just below a card: held there by its middle, a card lands after it, before the next group's header.
        Point Below(SessionCardViewModel c) => Item(c) is { } i ? i.TransformToVisual(list).TransformPoint(new Point(12, i.ActualHeight + 8)) : default;
        var held = main.Sessions.Where(c => !c.Pinned).ElementAt(1);
        var passed = main.Sessions.Where(c => !c.Pinned).Skip(2).Take(2).ToList();
        var slot = Item(passed[0])?.ActualHeight ?? 60;
        var root = window.RootElement;
        // A card taken hold of near its top, on its grip: where the card lands goes by the card, not the
        // pointer, and the pointer then stays furthest from the list's foot as the card goes down.
        Point Grab(SessionCardViewModel c) => Item(c) is { } i ? i.TransformToVisual(list).TransformPoint(new Point(12, i.ActualHeight - slot + 6)) : default;
        // Room for each drag in however short a list (a small screen): the list scrolled so the pinned
        // card's foot is just under the top edge's auto-scroll zone, which leaves the most room below it,
        // and every drag here held clear of both edges' zones, so the list stays still under it.
        var scroll = Find<ScrollViewer>(list).FirstOrDefault();
        var zone = ReorderDrag.ScrollZone(list.ActualHeight);
        async Task Fit()
        {
            if (scroll is null || Item(pin) is null) { return; }
            scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + Below(pin).Y - (zone + 12)), null, disableAnimation: true);
            await Task.Delay(300);
        }
        await Fit();
        var room = Grab(held).Y + (2 * slot) + 6 < list.ActualHeight - zone;
        if (!room) { check(false, $"has room in the list ({list.ActualHeight:0} px) to drag a card two down clear of its auto-scroll edges (from {Grab(held).Y:0}, {slot:0} px a card)"); }

        // Lifted, then making room, then over Pinned, in each theme: pictured, and put back with Escape's path.
        // With Windows' animation effects on, whatever the runner's are: its remote desktop may have them off.
        Motion.Forced = true;
        var looks = new List<string>();
        var shadowed = new List<string>();
        var legible = new List<string>();
        var motions = page.ReorderMotions;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            await Fit();
            var name = theme.ToString().ToLowerInvariant();
            var at = Grab(held);
            if (!page.Begin(held, at)) { looks.Add($"{name}: did not lift ({(Item(held) is { } hi ? $"its item {hi.ActualHeight:0} px at {at.Y:0} of {list.ActualHeight:0}" : "no item")}, {page.Layout?.Mode}, list shown {page.Layout?.ShowList})"); continue; }
            page.MoveTo(new Point(at.X, at.Y + 6));
            await Task.Delay(350);
            var item = page.HeldItem;
            var row = item?.ContentTemplateRoot as SessionRow;
            var origin = page.Origin;
            if (row is not { Lifted: true } || item is null || Canvas.GetZIndex(item) != 100 || page.Target != origin || SessionRow.LastSaid?.StartsWith($"Picked up {held.Title}, position 2 of ", StringComparison.Ordinal) != true)
            {
                looks.Add($"{name}: lifted {row?.Lifted}, above {(item is null ? 0 : Canvas.GetZIndex(item))}, at {page.Target} of {origin}, said \"{SessionRow.LastSaid}\"");
            }
            // Its shadow in the generated drag-shadow colours (#594), the PWA's too.
            var want = new[] { DesignColour("AbDragShadow", theme), DesignColour("AbDragShadowEdge", theme) };
            if (row is null || !row.ShadowColours.SequenceEqual(want) || want.Any(c => c.A == 0)) { shadowed.Add($"{name}: {string.Join(" and ", row?.ShadowColours.Select(c => c.ToString()) ?? [])}, not {string.Join(" and ", want.Select(c => c.ToString()))}"); }
            await ScreenPicture(window, Path.ChangeExtension(outFile, $".reorder.lifted.{name}.png"));

            // Two cards down: those two move up a slot each, and the gap opens below them.
            page.MoveTo(new Point(at.X, at.Y + (2 * slot) + 6));
            await Task.Delay(400);
            var shifts = passed.Select(c => Item(c) is { } i ? page.ShiftOf(i) : null).ToList();
            if (page.Target is not { Pinned: false } t || origin is not { } o || t.Index != o.Index + 2 || shifts.Any(s => s is not { } v || Math.Abs(v + page.GapHeight) > 1))
            {
                looks.Add($"{name}: two down, at {page.Target}, the passed cards moved {string.Join(" and ", shifts.Select(s => s?.ToString("0") ?? "not"))} of {page.GapHeight:0}, held at {at.Y + (2 * slot) + 6:0} of the list's {list.ActualHeight:0} px");
            }
            await ScreenPicture(window, Path.ChangeExtension(outFile, $".reorder.room.{name}.png"));

            // Over the pinned card: it will pin, and the chip says so.
            var over = Below(pin);
            page.MoveTo(over);
            await Task.Delay(400);
            if (page.Target is not { Pinned: true } || row?.ChipShown != ReorderWords.Chip(pinned: true))
            {
                looks.Add($"{name}: over Pinned, at {page.Target}, chip {row?.ChipShown ?? "none"}");
            }
            if (row is not null && ChipContrast(row) is var pinRatio && pinRatio < 4.5) { legible.Add($"{name} Pin: {pinRatio:0.00}:1"); }
            await ScreenPicture(window, Path.ChangeExtension(outFile, $".reorder.pin.{name}.png"));

            // Far outside the list: Cancel in place of the age, and the gap back where it came from.
            page.MoveTo(new Point(list.ActualWidth + 80, over.Y));
            await Task.Delay(300);
            if (row?.ChipShown != ReorderWords.Cancel || page.Target != origin) { looks.Add($"{name}: outside, chip {row?.ChipShown ?? "none"}, at {page.Target}"); }
            if (row is not null && ChipContrast(row) is var cancelRatio && cancelRatio < 4.5) { legible.Add($"{name} Cancel: {cancelRatio:0.00}:1"); }
            page.PutBack();
            await Task.Delay(400);
            if (page.Dragging || Order() != before || SessionRow.LastSaid != $"Cancelled. Back at position 2 of {main.Sessions.Count(c => !c.Pinned)}.") { looks.Add($"{name}: put back, still held {page.Dragging}, order {Order()}, said \"{SessionRow.LastSaid}\""); }
        }
        root.RequestedTheme = ElementTheme.Default;
        await Task.Delay(300);
        check(looks.Count == 0 && page.ReorderMotions > motions,
            $"lifts a card above the list, the cards it passes making room, says it will pin over Pinned and Cancel outside the list, and puts it back, in Light and Dark{(looks.Count > 0 ? $": not {string.Join("; ", looks)}" : "")}");
        check(shadowed.Count == 0, $"casts the lifted card's shadow in the generated drag-shadow colours, in Light and Dark{(shadowed.Count > 0 ? $": {string.Join("; ", shadowed)}" : "")}");
        check(legible.Count == 0, $"sets the Pin and Cancel chips' words at 4.5:1 or more on their fills over the lifted card, in Light and Dark{(legible.Count > 0 ? $": {string.Join("; ", legible)}" : "")}");

        // Dropped two places down: saved there, focus with the session, Needs you held for a moment.
        {
            await Fit();
            var at = Grab(held);
            page.Begin(held, at);
            page.MoveTo(new Point(at.X, at.Y + (2 * slot) + 6));
            await Task.Delay(300);
            page.LetGo();
            var laneHeld = page.LaneHeld;
            var said = SessionRow.LastSaid;
            var sessions = main.Sessions.Where(c => !c.Pinned).ToList();
            var landed = sessions.IndexOf(held) == 3 && sessions.IndexOf(passed[0]) == 1 && sessions.IndexOf(passed[1]) == 2;
            bool Focused() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is ListViewItem f && ReferenceEquals(f.Content, held) && ReferenceEquals(Item(held), f);
            var focused = await Until(() => !held.WantsRowFocus && Focused(), TimeSpan.FromSeconds(5));
            var saved = SavedOrder(main);
            var savedHere = saved.IndexOf(main.KeyOf(held)) is var h && h > saved.IndexOf(main.KeyOf(passed[1])) && saved.IndexOf(main.KeyOf(passed[1])) > saved.IndexOf(main.KeyOf(passed[0]));
            await Task.Delay(400);
            check(landed && savedHere && focused && laneHeld && said == $"Moved to position 4 of {sessions.Count} in Sessions." && Item(held)?.ContentTemplateRoot is SessionRow { Lifted: false },
                $"drops a card where its gap was, saves the order there, keeps keyboard focus with it and holds Needs you from a click for a moment (order {Order()}; saved {(savedHere ? "there" : "elsewhere")}; {FocusedName(window)}, the card {(held.WantsRowFocus ? "still asking for focus" : "not asking")}, its item {ItemState(window, held)}; Needs you {(laneHeld ? "held" : "not held")}; said \"{said}\")");
        }

        // Dropped among the pinned: pinned there, said so.
        {
            await Fit();
            var at = Grab(held);
            page.Begin(held, at);
            page.MoveTo(Below(pin));
            await Task.Delay(300);
            page.LetGo();
            await Task.Delay(500);
            var said = SessionRow.LastSaid;
            check(held.Pinned && main.Sessions.IndexOf(held) == 1 && said == "Pinned. Position 2 of 2 in Pinned.",
                $"pins a card dropped across the Sessions header, at the place it was dropped ({Order()}; said \"{said}\")");
            held.Row!.TogglePin();
            await Task.Delay(300);
        }

        // Needs you, the list's header, growing under a held card (a question arriving): the card stays
        // under the still pointer.
        {
            await Fit();
            var at = Grab(held);
            page.Begin(held, at);
            page.MoveTo(new Point(at.X, at.Y + 6));
            await Task.Delay(200);
            var header = page.ListHeaderView;
            var min = header.MinHeight;
            header.MinHeight = header.ActualHeight + 40;
            await Task.Delay(300);
            var drift = page.HeldDrift;
            header.MinHeight = min;
            await Task.Delay(200);
            var back = page.HeldDrift;
            page.PutBack();
            await Task.Delay(400);
            check(drift is { } grown && Math.Abs(grown) < 1 && back is { } shrunk && Math.Abs(shrunk) < 1,
                $"keeps a held card under the pointer as the Needs you lane above the list grows and shrinks ({drift:0.#} and {back:0.#} px off)");
        }

        // A touch's hold outlasting Your order (the sort changed while the finger waits) lifts nothing;
        // one that doesn't, lifts.
        {
            page.Hold(held);
            await Task.Delay(700);
            var lifts = page.Dragging;
            page.PutBack();
            await Task.Delay(400);
            page.Hold(held);
            await Task.Delay(100);
            main.SortIndex = (int)SessionSort.Recent;
            await Task.Delay(700);
            var liftedAnyway = page.Dragging;
            if (liftedAnyway) { page.PutBack(); }
            main.SortIndex = (int)SessionSort.Yours;
            await Task.Delay(400);
            check(lifts && !liftedAnyway, $"lifts a card held by touch, and nothing once the sort leaves Your order during the hold ({(lifts ? "lifts" : "did not lift")}; {(liftedAnyway ? "lifted after the sort changed" : "nothing after the sort changed")})");
        }

        // With animation effects off: the card still follows, but nothing lifts, slides or settles.
        {
            Motion.Forced = false;
            await Fit();
            var start = page.ReorderMotions;
            var at = Grab(held);
            page.Begin(held, at);
            page.MoveTo(new Point(at.X, at.Y + slot + 6));
            await Task.Delay(200);
            // After the animated drops above, which faded its look out as each card settled.
            var shown = (page.HeldItem?.ContentTemplateRoot as SessionRow)?.LookShown == true;
            page.LetGo();
            await Task.Delay(300);
            Motion.Forced = true;
            check(shown, $"draws the lifted look with animation effects off after an animated drop faded it out ({(shown ? "drawn" : "not drawn")})");
            check(page.ReorderMotions == start && main.Sessions.Where(c => !c.Pinned).ToList().IndexOf(held) == 1,
                $"with animation effects off, moves the cards a dragged card passes at once and animates nothing ({page.ReorderMotions - start} motions; {Order()})");
        }

        // Alt+Down and Alt+Up say the new place, and the end of the group.
        {
            var row = Rows(window).FirstOrDefault(r => r.Card == held);
            row?.MoveBy(1);
            await Task.Delay(400);
            var down = SessionRow.LastSaid;
            var last = main.Sessions.Last(c => !c.Pinned);
            var count = main.Sessions.Count(c => !c.Pinned);
            // The group's last card on screen (in a short list it is below the fold), so its row is there to move.
            list.ScrollIntoView(last);
            await Task.Delay(300);
            Rows(window).FirstOrDefault(r => r.Card == last)?.MoveBy(1);
            await Task.Delay(300);
            var end = SessionRow.LastSaid;
            list.ScrollIntoView(held);
            await Task.Delay(300);
            Rows(window).FirstOrDefault(r => r.Card == held)?.MoveBy(-1);
            await Task.Delay(400);
            check(down == $"Moved to position 3 of {count} in Sessions." && end == "End of Sessions.",
                $"says where Alt+Down moved a card, and the end of its group when it can go no further (\"{down}\", \"{end}\")");
        }

        // A real mouse, through Windows' input: pressed on the grip, dragged two cards down, let go.
        await Fit();
        result["reorderMouse"] = await RealDrag(window, main, check, held, slot, Order);

        Motion.Forced = null;

        // Where no drag can move a card (Most recent), a touch on its grip's strip scrolls the list.
        {
            main.SortIndex = (int)SessionSort.Recent;
            await Task.Delay(300);
            var taking = Rows(window).Where(r => r.Card is not null && r.GripTakesTouch).Select(r => r.Card!.Id).ToList();
            main.SortIndex = (int)SessionSort.Yours;
            await Task.Delay(300);
            var refusing = Rows(window).Where(r => r.Card is { Movable: true } && !r.GripTakesTouch).Select(r => r.Card!.Id).ToList();
            check(taking.Count == 0 && refusing.Count == 0, $"lets a touch on the grip scroll the list where the order can't be changed, and takes it again in Your order (taking in Most recent: {string.Join(", ", taking)}; refusing in Your order: {string.Join(", ", refusing)})");
        }

        // Back as it was, pins and all.
        foreach (var group in original.GroupBy(c => c.Pinned))
        {
            var cards = group.ToList();
            for (var i = 0; i < cards.Count; i++) { cards[i].Row?.MoveTo(group.Key, i); }
        }
        main.SelectedMachine = machine;
        main.SortIndex = sort;
        main.Selected = open;
        window.AppWindow.Resize(size);
        await Task.Delay(400);
    }

    // In high contrast (on CI, which turns it on for a pass): the lifted card has a 2 px Highlight
    // outline and no shadow or tint, and the gap a dashed GrayText outline. Pictured.
    static async Task CheckReorderInHighContrast(MainWindow window, Action<bool, string> check, string outFile)
    {
        var page = window.Sessions;
        var main = window.ViewModel;
        if (page.Layout?.Mode == PaneMode.One) { main.Selected = null; }
        await Task.Delay(400);
        var held = main.Sessions.Where(c => c.Row?.CanDrag == true).Skip(1).FirstOrDefault();
        if (held is null || page.Rows.ContainerFromItem(held) is not ListViewItem { ContentTemplateRoot: SessionRow row } item)
        {
            check(false, "finds a card to lift in high contrast");
            return;
        }
        page.Rows.ScrollIntoView(held);
        await Task.Delay(300);
        var at = item.TransformToVisual(page.Rows).TransformPoint(new Point(12, item.ActualHeight / 2));
        var lifted = page.Begin(held, at);
        page.MoveTo(new Point(at.X, at.Y + item.ActualHeight + 6));
        await Task.Delay(400);
        var outlined = row.Outlined;
        var dashed = page.GapDashed;
        await ScreenPicture(window, Path.ChangeExtension(outFile, ".reorder.highcontrast.png"));
        page.PutBack();
        await Task.Delay(400);
        check(lifted && outlined && dashed, $"draws a lifted card in high contrast with an outline and no shadow or tint, and its gap dashed ({(lifted ? "lifted" : "not lifted")}, {(outlined ? "outlined" : "not outlined")}, {(dashed ? "dashed" : "not dashed")})");
    }

    // What the mouse's drag found, for the result: run, or why not.
    static async Task<string> RealDrag(MainWindow window, MainViewModel main, Action<bool, string> check, SessionCardViewModel held, double slot, Func<string> order)
    {
        var page = window.Sessions;
        var list = page.Rows;
        window.Activate();
        SetForegroundWindow(window.Handle);
        await Task.Delay(300);
        var before = order();
        var start = main.Sessions.Where(c => !c.Pinned).ToList();
        var at = start.IndexOf(held);
        if (at < 0 || at + 2 >= start.Count || list.ContainerFromItem(held) is not ListViewItem { ContentTemplateRoot: SessionRow row })
        {
            check(false, "finds a card to drag with the mouse");
            return "no card to drag";
        }
        // Near the grip's top, as the card goes down: clear of the list's foot (CheckReorder's Grab).
        var grip = Screen(window, row.GripHit, new Point(row.GripHit.ActualWidth / 2, Math.Max(0, row.GripHit.ActualHeight - slot) + 6));
        var step = slot * window.RootElement.XamlRoot.RasterizationScale;
        MouseTo(grip.X, grip.Y);
        await Task.Delay(200);
        // Windows' input reaches this window only from a desktop someone (or CI's runner) is attached to:
        // a remote session left disconnected has none, its pointer still and its screen black, as the
        // screen pictures find. There the drag can't be driven, and the checks above stand for it.
        if (!GetCursorPos(out var cursor) || Math.Abs(cursor.X - grip.X) > 2 || Math.Abs(cursor.Y - grip.Y) > 2 || GetForegroundWindow() != window.Handle)
        {
            var why = $"Windows' input doesn't reach the window here (the pointer at {cursor.X},{cursor.Y}, not {grip.X:0},{grip.Y:0}; {(GetForegroundWindow() == window.Handle ? "in front" : "not in front")})";
            // CI's runner has a desktop attached: there it must run.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") { check(false, $"drags a card by its grip with the mouse ({why})"); }
            return $"not run: {why}";
        }
        MouseButton(down: true);
        await Task.Delay(200);
        var lifted = page.Dragging;
        for (var i = 1; i <= 10; i++)
        {
            MouseTo(grip.X, grip.Y + (2.2 * step * i / 10));
            await Task.Delay(40);
        }
        await Task.Delay(300);
        var target = page.Target;
        MouseButton(down: false);
        await Task.Delay(600);
        var sessions = main.Sessions.Where(c => !c.Pinned).ToList();
        var moved = sessions.IndexOf(held) == at + 2 && sessions.IndexOf(start[at + 1]) == at;
        check(lifted && moved && !page.Dragging,
            $"drags a card by its grip with the mouse, lifted as pressed, and saves where it is let go ({(lifted ? "lifted" : "not lifted")}, at {target}; {before} → {order()})");

        // Escape while held: back where it was, nothing saved.
        if (list.ContainerFromItem(held) is ListViewItem { ContentTemplateRoot: SessionRow again })
        {
            var from = order();
            grip = Screen(window, again.GripHit, new Point(again.GripHit.ActualWidth / 2, again.GripHit.ActualHeight / 2));
            MouseTo(grip.X, grip.Y);
            await Task.Delay(200);
            MouseButton(down: true);
            await Task.Delay(200);
            for (var i = 1; i <= 5; i++)
            {
                MouseTo(grip.X, grip.Y - (1.2 * step * i / 5));
                await Task.Delay(40);
            }
            var heldNow = page.Dragging;
            Key(0x1B);
            await Task.Delay(400);
            var stillHeld = page.Dragging;
            MouseButton(down: false);
            await Task.Delay(400);
            check(heldNow && !stillHeld && order() == from && SessionRow.LastSaid?.StartsWith($"Cancelled. Back at position {at + 3} of ", StringComparison.Ordinal) == true,
                $"puts a dragged card back when Escape is pressed ({(heldNow ? "held" : "not held")}, then {(stillHeld ? "still held" : "let go")}; {from} → {order()}; said \"{SessionRow.LastSaid}\")");
        }
        // The pointer out of the way of the checks that follow.
        MouseTo(grip.X + 600, grip.Y);
        return "run";
    }

    // A generated colour as a theme has it, read from the design dictionary itself.
    static Windows.UI.Color DesignColour(string key, ElementTheme theme)
    {
        var design = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        return design?.ThemeDictionaries.TryGetValue(theme.ToString(), out var d) == true && d is ResourceDictionary themed && themed.TryGetValue(key, out var b) && b is Microsoft.UI.Xaml.Media.SolidColorBrush brush ? brush.Color : default;
    }

    // The held card's chip: its words against its fill, laid over the lifted plate (card, then tint),
    // as WCAG measures contrast.
    static double ChipContrast(SessionRow row)
    {
        var c = row.ChipColours;
        var plate = Over(c.Tint, c.TintOpacity, c.Card);
        var fill = Over(c.Fill, c.FillOpacity, plate);
        var words = Over(c.Text, 1, fill);
        double Lum(Windows.UI.Color x)
        {
            static double L(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return (0.2126 * L(x.R)) + (0.7152 * L(x.G)) + (0.0722 * L(x.B));
        }
        double a = Lum(words) + 0.05, b = Lum(fill) + 0.05;
        return Math.Max(a, b) / Math.Min(a, b);
    }

    static Windows.UI.Color Over(Windows.UI.Color top, double opacity, Windows.UI.Color under)
    {
        var a = top.A / 255.0 * opacity;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * a) + (u * (1 - a)));
        return new Windows.UI.Color { A = 255, R = Mix(top.R, under.R), G = Mix(top.G, under.G), B = Mix(top.B, under.B) };
    }

    // The order as saved on disk, its keys in order.
    static List<string> SavedOrder(MainViewModel main)
    {
        try
        {
            if (main.Order.Saved is not { } file || !File.Exists(file)) { return []; }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("order", out var o) ? [.. o.EnumerateArray().Select(e => e.GetString() ?? "")] : [];
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    // A point in an element, on the screen in physical pixels.
    static Point Screen(MainWindow window, UIElement element, Point at)
    {
        var p = element.TransformToVisual(null).TransformPoint(at);
        var scale = window.RootElement.XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        ClientToScreen(window.Handle, ref origin);
        return new Point(origin.X + (p.X * scale), origin.Y + (p.Y * scale));
    }

    // A picture of the window as the screen shows it, Composition's drawing (the lift's scale and
    // shadow, the cards' translations) and all; where the screen shows nothing (a remote desktop
    // not connected to anything), the window's XAML rendered instead.
    static async Task ScreenPicture(MainWindow window, string file)
    {
        FrameworkElement element = window.RootElement;
        try
        {
            SetForegroundWindow(window.Handle);
            var topLeft = Screen(window, element, default);
            var scale = window.RootElement.XamlRoot.RasterizationScale;
            int w = (int)(element.ActualWidth * scale), h = (int)(element.ActualHeight * scale);
            var pixels = new byte[w * h * 4];
            var screen = GetDC(0);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, w, h);
            var old = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, w, h, screen, (int)topLeft.X, (int)topLeft.Y, 0x00CC0020 | 0x40000000);
            // GetDIBits reads a bitmap no device context holds.
            SelectObject(memory, old);
            var info = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            GetDIBits(memory, bitmap, 0, (uint)h, pixels, ref info, 0);
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
            if (pixels.All(b => b == 0)) { await Picture(element, file); return; }
            for (var i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (Exception e) when (e is COMException or ArgumentException or IOException)
        {
            AppLog.Write($"self-test screen picture not saved: {e.Message}");
        }
    }

    static void MouseTo(double x, double y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var input = new Input
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = (int)Math.Round((x - vx) * 65535 / Math.Max(1, vw - 1)),
                Y = (int)Math.Round((y - vy) * 65535 / Math.Max(1, vh - 1)),
                // Move, absolute, over the whole virtual desktop.
                Flags = 0x0001 | 0x8000 | 0x4000,
            },
        };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    static void MouseButton(bool down)
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Flags = down ? 0x0002u : 0x0004u } };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    static void Key(ushort vk)
    {
        Input[] inputs = [new() { Type = 1, Keyboard = new KeyboardInput { Vk = vk } }, new() { Type = 1, Keyboard = new KeyboardInput { Vk = vk, Flags = 0x0002 } }];
        _ = SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        // Room for the colour table GetDIBits may write after the header.
        public ulong Pad0, Pad1, Pad2;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint op);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}
