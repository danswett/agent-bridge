using System.ComponentModel;
using System.Numerics;
using AgentBridge.App.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentBridge.App;

// One session's row in the list (ui.md §6.1). Everything it shows comes from its
// SessionCardViewModel; this adds what only a window can do: its menu, the lifted look and grip of a
// drag the list runs (SessionsPage.Reorder, #592), and focus following a keyboard move.
public sealed partial class SessionRow : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(SessionRow), new PropertyMetadata(null, (d, e) => ((SessionRow)d).OnCard(e.OldValue as SessionCardViewModel, e.NewValue as SessionCardViewModel)));

    // The Words state the status words are in; null until entered, or after a theme change.
    string? words;
    // Listening to the card only while on screen: the list recycles row controls as the machine
    // picker and scrolling take rows off and put them back, while the card lives on, so a control
    // that kept listening once off screen would be kept, and called, for as long as the card lives.
    // Loaded and Unloaded can come in either order when a control is moved, so Unloaded counts only
    // once the control is no longer loaded.
    bool attached;

    public SessionRow()
    {
        InitializeComponent();
        // A parked row's generated bindings start tracking again as it loads: they stop again.
        Loaded += (_, _) => { if (parked) { Bindings.StopTracking(); } else { Attach(); } };
        Unloaded += (_, _) => { if (!IsLoaded) { Detach(); } };
        ActualThemeChanged += (_, _) => { words = null; ShowWords(); };
    }

    void OnTouchGrips() => ShowGrip();

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    public static FontWeight Weight(bool unread) => unread ? FontWeights.SemiBold : FontWeights.Normal;
    public static string Separator(string? place) => string.IsNullOrEmpty(place) ? "" : " · ";
    public static string WordsState(StatusTone tone) => $"Words{tone}";

    void OnCard(SessionCardViewModel? old, SessionCardViewModel? card)
    {
        if (attached && old is not null) { old.PropertyChanged -= OnCardChanged; }
        if (card is not null)
        {
            if (attached) { card.PropertyChanged += OnCardChanged; }
            AutomationProperties.SetAutomationId(this, $"Row:{card.Id}");
        }
        if (!attached) { return; }
        Bindings.Update();
        Settle();
        NameRow();
        ShowWords();
        // A row given another session's item as the list redraws for a move (#410).
        TakeFocus();
    }

    // Shown for another session, or again: what was moving was the last state's, not news.
    void Settle()
    {
        RowGlyph.Settle();
        Motion.Settle(UnreadDot);
        Motion.Hide(UnreadGone);
        unread = Card?.Unread == true;
        // A lifted look belongs to the card held, which the list gives back to its own row.
        HideLift();
        ShowGrip();
    }

    // The dot last shown, so only a change of it on this session fades.
    bool unread;

    // Put aside by the list for reuse (SessionsPage), still loaded and still holding its old
    // session: Loaded comes again there, and must not have it listen to that session.
    bool parked;

    // Given a session to show again, by the list.
    internal void Unpark()
    {
        parked = false;
        if (IsLoaded) { Attach(); }
    }

    internal void Park()
    {
        parked = true;
        Detach();
    }

    void Attach()
    {
        if (attached) { return; }
        attached = true;
        if (Card is { } c) { c.PropertyChanged += OnCardChanged; }
        TouchGripsChanged += OnTouchGrips;
        Bindings.Update();
        Settle();
        NameRow();
        words = null;
        ShowWords();
        TakeFocus();
    }

    void Detach()
    {
        if (!attached) { return; }
        attached = false;
        if (Card is { } c) { c.PropertyChanged -= OnCardChanged; }
        TouchGripsChanged -= OnTouchGrips;
        Bindings.StopTracking();
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(SessionCardViewModel.StatusTone)) { ShowWords(); }
        if (all || e.PropertyName == nameof(SessionCardViewModel.Unread)) { ShowUnread(); }
        if (all || e.PropertyName is nameof(SessionCardViewModel.RowLabel) or nameof(SessionCardViewModel.Unread)) { NameRow(); }
        if (e.PropertyName == nameof(SessionCardViewModel.WantsRowFocus)) { TakeFocus(); }
    }

    // The session updated while not open: the dot fades in. Opened: it fades out where it was.
    void ShowUnread()
    {
        var now = Card?.Unread == true;
        if (now == unread) { return; }
        unread = now;
        if (now) { Motion.Hide(UnreadGone); Motion.Enter(UnreadDot, "ActivityDot"); }
        else { Motion.Leave(UnreadGone, "ActivityDot"); }
    }

    // The row read aloud (§9), on the list item that has focus, not only on this control.
    void NameRow()
    {
        if (Card is not { } c) { return; }
        AutomationProperties.SetName(this, c.RowLabel);
        if (Container is { } item) { AutomationProperties.SetName(item, c.RowLabel); }
    }

    void ShowWords()
    {
        var next = WordsState(Card?.StatusTone ?? StatusTone.Plain);
        if (words == next) { return; }
        if (words is null) { VisualStateManager.GoToState(this, "WordsUnset", false); }
        VisualStateManager.GoToState(this, next, false);
        words = next;
    }

    internal ListViewItem? Container
    {
        get
        {
            DependencyObject? node = this;
            while (node is not null and not ListViewItem) { node = VisualTreeHelper.GetParent(node); }
            return node as ListViewItem;
        }
    }

    // After a keyboard or menu move the row is drawn in its new place, perhaps by a new control: its
    // list item takes focus back there, so the next Alt+Up/Down moves the same row.
    // Asked for again by whichever row shows the card when it attaches or is given the card, and
    // cleared only once focus has landed: a row parked for reuse while this was queued still holds
    // the card, and clearing it there left the row drawn for it unfocused, the next Alt+Up/Down
    // going elsewhere.
    // #410: the list redraws its rows for a move in a layout pass, keeping the moved session's old
    // item off screen, and this control may sit in that item, or in one the pass gives another
    // session: focus put there ended off screen or on another row. The pass is run first, and focus
    // goes to the item the list draws the session in; SessionsPage.KeepFocusWithSession covers a
    // pass that comes later.
    void TakeFocus()
    {
        if (parked || !attached || Card is not { WantsRowFocus: true } c) { return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (parked || !attached || !c.WantsRowFocus || Card != c || !IsLoaded || ListOf(this) is not { } list) { return; }
            list.UpdateLayout();
            // Not drawn (scrolled away): drawn, and the row given it asks again as it is.
            if (list.ContainerFromItem(c) is not ListViewItem item) { list.ScrollIntoView(c); return; }
            if (!ReferenceEquals(item.Content, c) || !item.Focus(FocusState.Keyboard)) { return; }
            c.WantsRowFocus = false;
            item.StartBringIntoView();
        });
    }

    static ListViewBase? ListOf(DependencyObject node)
    {
        for (var up = VisualTreeHelper.GetParent(node); up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is ListViewBase list) { return list; }
        }
        return null;
    }

    // Shift+F10 or the menu key on the list item: the row's own menu, as right-click opens it.
    internal void OpenMenu() => RowMenu.ShowAt(RowRoot, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });

    // A touch held on the card and let go without moving (#592): its menu, where the finger was.
    internal void OpenMenuAt(Point at) => RowMenu.ShowAt(RowRoot, new FlyoutShowOptions { Position = at });

    // A right-click, or a touch held: the menu, unless the list is holding the card for a touch, which
    // opens the menu itself if the finger lifts without moving (SessionsPage.Reorder).
    void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (Page?.HoldsTouch == true) { return; }
        if (e.TryGetPosition(RowRoot, out var at)) { OpenMenuAt(at); }
        else { OpenMenu(); }
    }

    SessionsPage? Page
    {
        get
        {
            for (var up = VisualTreeHelper.GetParent(this); up is not null; up = VisualTreeHelper.GetParent(up))
            {
                if (up is SessionsPage page) { return page; }
            }
            return null;
        }
    }

    void OnRowMenuOpening(object sender, object e)
    {
        var row = Card?.Row;
        MoveUpItem.IsEnabled = row?.CanMoveUp == true;
        MoveDownItem.IsEnabled = row?.CanMoveDown == true;
        PinItem.Text = Card?.Pinned == true ? "Unpin" : "Pin";
        PinItem.IsEnabled = row is not null;
    }

    void OnMoveUp(object sender, RoutedEventArgs e) => MoveBy(-1);
    void OnMoveDown(object sender, RoutedEventArgs e) => MoveBy(1);

    // Alt+Up and Alt+Down on the list item (SessionsPage), and the menu: within the row's group (#164),
    // with a drag's motion (ui.md §5.3, Reorder): the moved card and the one it passes trade places
    // together. Said aloud as its new place, or as its group's end when it can go no further.
    internal void MoveBy(int delta)
    {
        if (Card is not { Row: { } row } c || ListOf(this) is not { } list) { return; }
        var cards = list.Items.OfType<SessionCardViewModel>().ToList();
        var at = cards.IndexOf(c);
        var passed = at + delta >= 0 && at + delta < cards.Count ? cards[at + delta] : null;
        var from = Place();
        var passedFrom = passed is not null && list.ContainerFromItem(passed) is ListViewItem { ContentTemplateRoot: SessionRow other } ? other.Place() : null;
        c.WantsRowFocus = true;
        if (!(delta < 0 ? row.MoveUp() : row.MoveDown()))
        {
            c.WantsRowFocus = false;
            var group = cards.Where(x => x.Pinned == c.Pinned).ToList();
            var end = delta < 0 ? group.FirstOrDefault() : group.LastOrDefault();
            if (row.CanDrag && end == c) { Say(ReorderWords.End(delta < 0, c.Pinned)); }
            return;
        }
        SlideFrom(c, from);
        if (passed is not null) { SlideFrom(passed, passedFrom); }
        Page?.Moved();
        var now = list.Items.OfType<SessionCardViewModel>().Where(x => x.Pinned == c.Pinned).ToList();
        Say(ReorderWords.Dropped((now.IndexOf(c) + 1, now.Count), crossed: false, c.Pinned));
    }

    // Where this row's glyph sits in its list's panel, which scrolling does not move: the glyph,
    // not the row's top, since a group header moves from one row to another as rows move.
    (ListViewBase List, double Top)? Place() =>
        Container is { } item && ItemsControl.ItemsControlFromItemContainer(item) is ListViewBase list && list.ItemsPanelRoot is { } panel ? (list, Motion.TopIn(RowGlyph, panel)) : null;

    // A moved row slides from where it was to its new place (ui.md §5.3, Reorder), as the list has no
    // transitions of its own.
    static void SlideFrom(SessionCardViewModel card, (ListViewBase List, double Top)? from)
    {
        if (from is not { } f) { return; }
        Motion.AfterLayout(f.List, card, container =>
        {
            if (f.List.ItemsPanelRoot is not { } panel) { return; }
            if (container is not ListViewItem { ContentTemplateRoot: SessionRow row }) { return; }
            var delta = f.Top - Motion.TopIn(row.RowGlyph, panel);
            if (Math.Abs(delta) >= 1) { Motion.Slide(container, "Reorder", new System.Numerics.Vector2(0, (float)delta)); }
        });
    }

    void OnPin(object sender, RoutedEventArgs e)
    {
        if (Card is not { Row: { } row } c) { return; }
        c.WantsRowFocus = true;
        var pinning = !c.Pinned;
        row.TogglePin();
        Say(pinning ? "Pinned" : "Unpinned");
    }

    // What a move did, said to a screen reader, as the PWA's live region does.
    internal void Say(string text)
    {
        LastSaid = text;
        // Created if need be: FromElement finds only a peer something already asked for.
        FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, text, "AgentBridgeRowMoved");
    }

    // The last thing a row said, for the self-test.
    internal static string? LastSaid { get; private set; }

    // --- The grip and the lifted card (#592). The drag itself is the list's (SessionsPage.Reorder):
    // the row only draws what it is told. ---

    // Every card's grip shows once touch has been used on the list, until a mouse is again (ui.md
    // §6.1, Moving: on touch the grip shows on every card). One setting for all rows.
    static bool touchGrips;
    static event Action? TouchGripsChanged;

    internal static bool TouchGrips
    {
        get => touchGrips;
        set
        {
            if (touchGrips == value) { return; }
            touchGrips = value;
            TouchGripsChanged?.Invoke();
        }
    }

    bool hovered;
    bool held;

    void OnPointerEntered(object sender, PointerRoutedEventArgs e) { hovered = true; ShowGrip(); }
    void OnPointerExited(object sender, PointerRoutedEventArgs e) { hovered = false; ShowGrip(); }

    void ShowGrip()
    {
        var state = held ? "GripHeld" : Card?.Row?.CanDrag != true ? "GripHidden" : touchGrips ? "GripTouch" : hovered ? "GripHover" : "GripHidden";
        VisualStateManager.GoToState(this, state, false);
    }

    // A press on the grip is the list's to lift the card (SessionsPage.Reorder sees it, handled or
    // not): handled here, so the list item neither shows it pressed nor takes it for a click.
    void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Card?.Row?.CanDrag == true) { e.Handled = true; }
    }

    internal FrameworkElement GripHit => GripArea;

    // The height of the group header drawn at the row's top, with its spacing; 0 when it has none.
    internal double HeaderPart => GroupHeading.Visibility == Visibility.Visible ? GroupHeading.ActualHeight + GroupHeading.Margin.Top + GroupHeading.Margin.Bottom : 0;

    // While a card is held, the list draws the group headers where they are to be, not in the rows.
    internal void HideHeading(bool hide) => GroupHeading.Opacity = hide ? 0 : 1;

    // Where the heading is drawn in the row's list item.
    internal Point HeadingOffset(UIElement item) => GroupHeading.TransformToVisual(item).TransformPoint(default);

    // The lifted look's layer, faded in as the card lifts and out as it lowers.
    internal UIElement LiftLayer => LiftLook;

    // The card lifted: the tinted plate with the drag shadow, or (reduced motion, high contrast) the
    // plate with a 2 px Bridge outline and neither shadow nor tint.
    internal void ShowLift(bool outline, bool selected)
    {
        held = true;
        ShowGrip();
        LiftLook.Visibility = Visibility.Visible;
        LiftPlate.BorderThickness = new Thickness(outline ? 0 : 1);
        LiftOutline.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        LiftTint.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        // 5% Bridge over card, 4% in light (ui.md §5.3, Lift).
        LiftTint.Opacity = ActualTheme == ElementTheme.Light ? 0.04 : 0.05;
        LiftSelected.Visibility = LiftPill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        LiftShadow.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        if (!outline) { CastShadow(); }
    }

    internal void HideLift()
    {
        if (!held && LiftLook.Visibility == Visibility.Collapsed) { return; }
        held = false;
        ShowGrip();
        LiftLook.Visibility = Visibility.Collapsed;
        ShowChip(null);
        HideHeading(false);
        Motion.Settle(LiftLook);
    }

    internal bool Lifted => held;
    // Drawn as high contrast and reduced motion draw it: an outline, with no shadow or tint.
    internal bool Outlined => LiftOutline.Visibility == Visibility.Visible && LiftShadow.Visibility == Visibility.Collapsed && LiftTint.Visibility == Visibility.Collapsed;

    // Pin, Unpin or Cancel in place of the age; null for none.
    internal void ShowChip(string? text)
    {
        ChipText.Text = text ?? "";
        VisualStateManager.GoToState(this, text is null ? "ChipNone" : text == ReorderWords.Cancel ? "ChipCancel" : "ChipBridge", false);
    }

    internal string? ChipShown => Chip.Visibility == Visibility.Visible ? ChipText.Text : null;

    // The drag shadow (ui.md §5.3, Lift): dark 0 8 16 black at 0.28 with 0 0 2 at 0.24; light 0.14
    // and 0.12. Fluent's 16, as two composition drop shadows of the card's rounded shape, since a
    // ThemeShadow would be cast by the whole list item, its group header included.
    readonly List<(LayerVisual Layer, ShapeVisual Shape, CompositionRoundedRectangleGeometry Geometry, DropShadow Drop, float Light, float Dark)> shadows = [];

    void CastShadow()
    {
        if (shadows.Count == 0)
        {
            var c = ElementCompositionPreview.GetElementVisual(LiftShadow).Compositor;
            var root = c.CreateContainerVisual();
            // The shadow's colour, whole: how dark each shadow is is its opacity, by theme.
            var ink = (Application.Current.Resources["AbShadow"] as SolidColorBrush)?.Color ?? default;
            ink.A = 255;
            foreach (var (y, blur, light, dark) in new[] { (8f, 16f, 0.14f, 0.28f), (0f, 2f, 0.12f, 0.24f) })
            {
                var geometry = c.CreateRoundedRectangleGeometry();
                geometry.CornerRadius = new Vector2(8, 8);
                var sprite = c.CreateSpriteShape(geometry);
                sprite.FillBrush = c.CreateColorBrush(ink);
                var shape = c.CreateShapeVisual();
                shape.Shapes.Add(sprite);
                var layer = c.CreateLayerVisual();
                layer.Children.InsertAtTop(shape);
                var drop = c.CreateDropShadow();
                drop.BlurRadius = blur;
                drop.Offset = new Vector3(0, y, 0);
                drop.Color = ink;
                layer.Shadow = drop;
                root.Children.InsertAtTop(layer);
                shadows.Add((layer, shape, geometry, drop, light, dark));
            }
            ElementCompositionPreview.SetElementChildVisual(LiftShadow, root);
            LiftShadow.SizeChanged += (_, _) => SizeShadow();
        }
        SizeShadow();
        var isDark = ActualTheme != ElementTheme.Light;
        foreach (var s in shadows) { s.Drop.Opacity = isDark ? s.Dark : s.Light; }
    }

    void SizeShadow()
    {
        var size = new Vector2((float)LiftShadow.ActualWidth, (float)LiftShadow.ActualHeight);
        foreach (var s in shadows) { s.Layer.Size = s.Shape.Size = s.Geometry.Size = size; }
    }
}
