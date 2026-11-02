using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace AgentBridge.App;

// One session's card (pwa/js/ui/session-card.js). Everything it shows comes from its
// SessionCardViewModel; this adds what only a window can do: pasting and picking files, the
// clipboard and keyboard shortcuts. Its status glyph turns on its own (StatusGlyphView).
public sealed partial class SessionCard : UserControl
{
    // Pictures are read to be made smaller; nothing else over the limit is read at all.
    const long MostRead = 32 * 1024 * 1024;

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(SessionCard), new PropertyMetadata(null, (d, e) => ((SessionCard)d).OnCard(e.OldValue as SessionCardViewModel, e.NewValue as SessionCardViewModel)));

    QuestionView? questionView;
    // Listening to the card: only while on screen. The machine picker takes cards off the list
    // and puts them back, which makes a new control each time while the card stays alive, so a
    // control that kept listening once off screen would be kept, and called, for as long as the
    // card lives. Loaded and Unloaded can come in either order when a control is moved, so
    // Unloaded counts only once the control is no longer loaded.
    bool attached;

    public SessionCard()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (!IsLoaded) { Detach(); } };
    }

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    public static double ChevronAngle(bool folded) => folded ? 0 : 90;
    public static string FoldName(string title, bool folded) => $"{title}: {(folded ? "folded, press to open" : "open, press to fold")}";
    public static string FoldLabel(string title, bool folded, bool unread) => unread ? $"{FoldName(title, folded)}, new activity" : FoldName(title, folded);
    public static string RemoveName(string name) => $"Remove {name}";

    void OnCard(SessionCardViewModel? old, SessionCardViewModel? card)
    {
        if (attached && old is not null) { old.PropertyChanged -= OnCardChanged; }
        if (card is not null)
        {
            if (attached) { card.PropertyChanged += OnCardChanged; }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, $"Card:{card.Id}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, card.Title);
        }
        if (!attached) { return; }
        SyncQuestion();
        Bindings.Update();
    }

    void Attach()
    {
        if (attached) { return; }
        attached = true;
        if (Card is { } c) { c.PropertyChanged += OnCardChanged; }
        SyncQuestion();
        Bindings.Update();
        TakeFocus();
    }

    void Detach()
    {
        if (!attached) { return; }
        attached = false;
        if (Card is { } c) { c.PropertyChanged -= OnCardChanged; }
        questionView?.Detach();
        questionView = null;
        QuestionHost.Content = null;
        Bindings.StopTracking();
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(SessionCardViewModel.Question)) { SyncQuestion(); }
        if (e.PropertyName == nameof(SessionCardViewModel.WantsFocus)) { TakeFocus(); }
    }

    // After a keyboard move the card is drawn in its new place, perhaps by a new control: the
    // title takes focus back there, so the next Alt+Up/Down moves the same card.
    void TakeFocus()
    {
        if (Card is not { WantsFocus: true } c) { return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!c.WantsFocus || Card != c || !IsLoaded) { return; }
            c.WantsFocus = false;
            FoldButton.Focus(FocusState.Keyboard);
            FoldButton.StartBringIntoView();
        });
    }

    // Alt+Up and Alt+Down on the title move the card within its group (#164).
    void OnFoldKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down) || Card?.Row is null) { return; }
        var alt = e.KeyStatus.IsMenuKeyDown || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt) { return; }
        e.Handled = true;
        MoveBy(e.Key == VirtualKey.Up ? -1 : 1);
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

    void MoveBy(int delta)
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
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(FoldButton)?.RaiseNotificationEvent(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.MostRecent, text, "AgentBridgeRowMoved");

    // A drag by the grip: the card being dragged, by key, while it is. Only this app's own cards
    // are dropped here, so nothing else needs to travel in the data package.
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

    void SyncQuestion()
    {
        var q = Card?.Question;
        if (questionView is not null && q is not null && ReferenceEquals(questionView.Tag, q)) { return; }
        questionView?.Detach();
        questionView = q is null ? null : new QuestionView(q, Editing) { Tag = q };
        QuestionHost.Content = questionView;
        QuestionHost.Visibility = Ui.Some(questionView);
    }

    void Editing(bool on)
    {
        if (Card is { } c) { c.Editing = on; }
    }

    void OnFold(object sender, RoutedEventArgs e)
    {
        if (Card is { } c) { c.Folded = !c.Folded; }
    }

    void OnOrphanClosed(InfoBar sender, InfoBarClosedEventArgs args) => Card?.DismissOrphan();

    void OnCopyOrphan(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(Card?.OrphanText ?? "");
        Clipboard.SetContent(data);
    }

    void OnEditing(object sender, RoutedEventArgs e) => Editing(true);
    void OnNotEditing(object sender, RoutedEventArgs e) => Editing(false);

    // Wired to PreviewKeyDown: this multi-line TextBox handles Enter itself and marks KeyDown
    // handled, so on KeyDown Ctrl+Enter inserted a line and never sent (#119).
    void OnReplyKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) { return; }
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) { return; }
        e.Handled = true;
        Card?.Reply?.Send();
    }

    void OnSendReply(object sender, RoutedEventArgs e) => Card?.Reply?.Send();
    void OnResendReply(object sender, RoutedEventArgs e) => Card?.Reply?.Resend();
    void OnEnd(object sender, RoutedEventArgs e) => Card?.End?.Press();
    void OnResendEnd(object sender, RoutedEventArgs e) => Card?.End?.Resend();

    void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReplyAttachment file }) { Card?.Reply?.Remove(file); }
    }

    void OnHistoryOpen(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (Card is not { } c) { return; }
        c.HistoryOpen = true;
        _ = c.LoadHistoryAsync();
    }

    void OnHistoryClosed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (Card is { } c) { c.HistoryOpen = false; }
    }

    // A pasted picture or file goes with the reply, as on the PWA; pasted text is just text.
    // These handlers are async void: anything they let escape would end the app and every
    // unsent draft with it, so the clipboard and files failing (locked, deleted, offline) is
    // reported on the reply instead.
    async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        if (Card?.Reply is not { ShowAttach: true } reply) { return; }
        try
        {
            var clip = Clipboard.GetContent();
            if (clip.Contains(StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                foreach (var item in await clip.GetStorageItemsAsync())
                {
                    if (item is StorageFile file) { await AddFileAsync(reply, file); }
                }
            }
            else if (clip.Contains(StandardDataFormats.Bitmap))
            {
                e.Handled = true;
                var reference = await clip.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                await reply.AddAsync("pasted.png", "image/png", await AsPngAsync(stream));
            }
        }
        catch (Exception ex) when (Unreadable(ex)) { reply.Unreadable("what was pasted"); }
    }

    async void OnAttach(object sender, RoutedEventArgs e)
    {
        if (Card?.Reply is not { } reply) { return; }
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            foreach (var file in files) { await AddFileAsync(reply, file); }
        }
        catch (Exception ex) when (Unreadable(ex)) { reply.Unreadable("the chosen file"); }
    }

    static bool Unreadable(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException;

    // Each file on its own: one that went missing does not stop the others.
    static async Task AddFileAsync(ReplyViewModel reply, StorageFile file)
    {
        string type;
        byte[] bytes;
        try
        {
            var size = (long)(await file.GetBasicPropertiesAsync()).Size;
            type = file.ContentType;
            var image = type.StartsWith("image/", StringComparison.Ordinal);
            if (size > reply.MaxBytes && (!image || size > MostRead))
            {
                reply.Refuse(file.Name, type, size);
                return;
            }
            var buffer = await FileIO.ReadBufferAsync(file);
            bytes = new byte[buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
        }
        catch (Exception e) when (Unreadable(e))
        {
            reply.Unreadable(file.Name);
            return;
        }
        await reply.AddAsync(file.Name, type, bytes);
    }

    // The clipboard's picture as PNG, whatever form Windows holds it in.
    static async Task<byte[]> AsPngAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
