using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AgentBridge.App;

// One session's row in the list (ui.md §6.1). Everything it shows comes from its
// SessionCardViewModel; this adds what only a window can do: its menu, the drag by its grip, and
// focus following a keyboard move.
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

    ListViewItem? Container
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
    internal void OpenMenu() => RowMenu.ShowAt(RowRoot, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });

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

    // Alt+Up and Alt+Down on the list item (SessionsPage), and the menu: within the row's group (#164).
    internal void MoveBy(int delta)
    {
        if (Card is not { Row: { } row } c) { return; }
        var from = Place();
        c.WantsRowFocus = true;
        if (!(delta < 0 ? row.MoveUp() : row.MoveDown())) { c.WantsRowFocus = false; return; }
        SlideFrom(c, from);
        Say(delta < 0 ? "Moved up" : "Moved down");
    }

    // Where this row's glyph sits in its list's panel, which scrolling does not move: the glyph,
    // not the row's top, since a group header moves from one row to another as rows move.
    (ListViewBase List, double Top)? Place() =>
        Container is { } item && ItemsControl.ItemsControlFromItemContainer(item) is ListViewBase list && list.ItemsPanelRoot is { } panel ? (list, Motion.TopIn(RowGlyph, panel)) : null;

    // A moved row slides from where it was to its new place (ui.md §5.3, Reorder); the row it
    // passed takes its own place at once, as the list has no transitions of its own.
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
    void Say(string text) =>
        // Created if need be: FromElement finds only a peer something already asked for.
        FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, text, "AgentBridgeRowMoved");

    // A drag by the grip: the row being dragged, by key, while it is. Only this app's own rows are
    // dropped here, so nothing else needs to travel in the data package.
    static string? dragging;
    static SessionCardViewModel? draggingCard;
    // Where the pointer took hold, below the dragged row's glyph, so the row settles from under it.
    static double grabbedAt;

    void OnPointerEntered(object sender, PointerRoutedEventArgs e) => Grip.Opacity = Card?.Row?.CanDrag == true ? 1 : 0;
    void OnPointerExited(object sender, PointerRoutedEventArgs e) => Grip.Opacity = 0;

    void OnGripDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Card?.Row is not { CanDrag: true } row) { args.Cancel = true; return; }
        dragging = row.Key;
        draggingCard = Card;
        grabbedAt = args.GetPosition(RowGlyph).Y;
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    void OnGripDropCompleted(UIElement sender, DropCompletedEventArgs args) { dragging = null; draggingCard = null; }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (dragging is null || Card?.Row is not { } row || row.Key == dragging) { return; }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = e.GetPosition(this).Y > ActualHeight / 2 ? "Move below" : "Move above";
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (dragging is not { } key || Card?.Row is not { } row || row.Key == key) { return; }
        // Where the dropped row was let go: it settles from there into its place.
        var moved = draggingCard;
        dragging = null;
        draggingCard = null;
        var at = Place() is { } here && here.List.ItemsPanelRoot is { } panel ? (here.List, e.GetPosition(panel).Y - grabbedAt) : ((ListViewBase, double)?)null;
        if (!row.Drop(key, after: e.GetPosition(this).Y > ActualHeight / 2)) { return; }
        if (moved is not null) { SlideFrom(moved, at); }
        Say("Moved");
    }
}
