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
        Loaded += (_, _) => Attach();
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
        NameRow();
        ShowWords();
    }

    internal void Attach()
    {
        if (attached) { return; }
        attached = true;
        if (Card is { } c) { c.PropertyChanged += OnCardChanged; }
        Bindings.Update();
        NameRow();
        words = null;
        ShowWords();
        TakeFocus();
    }

    internal void Detach()
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
        if (all || e.PropertyName is nameof(SessionCardViewModel.RowLabel) or nameof(SessionCardViewModel.Unread)) { NameRow(); }
        if (e.PropertyName == nameof(SessionCardViewModel.WantsFocus)) { TakeFocus(); }
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
    void TakeFocus()
    {
        if (Card is not { WantsFocus: true } c) { return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!c.WantsFocus || Card != c || !IsLoaded) { return; }
            c.WantsFocus = false;
            if (Container is { } item)
            {
                item.Focus(FocusState.Keyboard);
                item.StartBringIntoView();
            }
        });
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
        c.WantsFocus = true;
        if (!(delta < 0 ? row.MoveUp() : row.MoveDown())) { c.WantsFocus = false; return; }
        Say(delta < 0 ? "Moved up" : "Moved down");
    }

    void OnPin(object sender, RoutedEventArgs e)
    {
        if (Card is not { Row: { } row } c) { return; }
        c.WantsFocus = true;
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

    void OnPointerEntered(object sender, PointerRoutedEventArgs e) => Grip.Opacity = Card?.Row?.CanDrag == true ? 1 : 0;
    void OnPointerExited(object sender, PointerRoutedEventArgs e) => Grip.Opacity = 0;

    void OnGripDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Card?.Row is not { CanDrag: true } row) { args.Cancel = true; return; }
        dragging = row.Key;
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    void OnGripDropCompleted(UIElement sender, DropCompletedEventArgs args) => dragging = null;

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (dragging is null || Card?.Row is not { } row || row.Key == dragging) { return; }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = e.GetPosition(this).Y > ActualHeight / 2 ? "Move below" : "Move above";
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (dragging is not { } key || Card?.Row is not { } row || row.Key == key) { return; }
        dragging = null;
        if (row.Drop(key, after: e.GetPosition(this).Y > ActualHeight / 2)) { Say("Moved"); }
    }
}
