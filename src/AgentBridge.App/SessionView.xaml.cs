using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace AgentBridge.App;

// The session pane (ui.md §6.2, §6.4, §6.5; pwa/js/ui/session-card.js before #179): the session
// open in the list, MainViewModel.Selected. Everything it shows comes from its
// SessionCardViewModel and the panes' PaneLayoutViewModel; this adds what only a window can do:
// pasting and picking files, the clipboard and keyboard shortcuts. Its status glyph turns on its own
// (StatusGlyphView).
public sealed partial class SessionView : UserControl
{
    // Pictures are read to be made smaller; nothing else over the limit is read at all.
    const long MostRead = 32 * 1024 * 1024;

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(SessionView), new PropertyMetadata(null, (d, e) => ((SessionView)d).OnCard(e.OldValue as SessionCardViewModel, e.NewValue as SessionCardViewModel)));

    QuestionView? questionView;
    // The Words state the status words are in; null until entered, or after a theme change.
    string? words;
    // Listening to the card only while on screen (SessionRow says why).
    bool attached;
    PaneLayoutViewModel? layout;
    bool showingTab;

    public SessionView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        SizeChanged += (_, e) => FitComposer(e.NewSize.Height);
        Unloaded += (_, _) => { if (!IsLoaded) { Detach(); } };
        // The Words states' ThemeResources are read as a state is entered: entered again, they are
        // the new theme's.
        ActualThemeChanged += (_, _) => { words = null; ShowWords(); };
    }

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    // The panes this view sits in: its back button, tabs and inspector toggle. Set once.
    public PaneLayoutViewModel? Layout
    {
        get => layout;
        set
        {
            if (layout is not null) { layout.PropertyChanged -= OnLayout; layout.QuestionWanted -= OnQuestionWanted; }
            layout = value;
            if (layout is not null) { layout.PropertyChanged += OnLayout; layout.QuestionWanted += OnQuestionWanted; }
            TabDelegate.Layout = value;
            Bindings.Update();
            ShowTab();
        }
    }

    public static string RemoveName(string name) => $"Remove {name}";
    public static string StripId(string id) => $"Strip:{id}";
    public static string RowId(string id) => $"Delegate:{id}";
    public static Thickness IndentOf(double indent) => new(indent, 0, 0, 0);
    public static string PinName(bool pinned) => pinned ? "Unpin" : "Pin";
    public static string WordsState(StatusTone tone) => $"Words{tone}";
    public static Visibility Unpinned(bool pinned) => Ui.Show(!pinned);

    void OnCard(SessionCardViewModel? old, SessionCardViewModel? card)
    {
        if (attached && old is not null) { old.PropertyChanged -= OnCardChanged; old.Delegates.ReplyWanted -= OnReplyWanted; }
        // The box the person is typing in now belongs to the next session: the last one is no longer
        // being edited, which would otherwise keep holding its list's order (MainViewModel.Rearrange).
        if (old is not null) { old.Editing = false; old.WantsFocus = false; }
        if (card is not null) { card.Editing = ReplyBox.FocusState != FocusState.Unfocused; }
        if (card is not null)
        {
            if (attached) { card.PropertyChanged += OnCardChanged; card.Delegates.ReplyWanted += OnReplyWanted; }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, $"Session:{card.Id}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, card.Title);
        }
        DetailsFlyout.Card = card;
        ToolsTabList.Card = card;
        if (!attached) { return; }
        SyncQuestion();
        Bindings.Update();
        ShowWords();
        ConversationScroll.ChangeView(null, 0, null, disableAnimation: true);
        TakeFocus();
    }

    void Attach()
    {
        if (attached) { return; }
        attached = true;
        if (Card is { } c) { c.PropertyChanged += OnCardChanged; c.Delegates.ReplyWanted += OnReplyWanted; }
        SyncQuestion();
        Bindings.Update();
        words = null;
        ShowWords();
        TakeFocus();
    }

    void Detach()
    {
        if (!attached) { return; }
        attached = false;
        if (Card is { } c) { c.PropertyChanged -= OnCardChanged; c.Delegates.ReplyWanted -= OnReplyWanted; }
        questionView?.Detach();
        questionView = null;
        QuestionHost.Content = null;
        Bindings.StopTracking();
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(SessionCardViewModel.Question)) { SyncQuestion(); }
        if ((all || e.PropertyName == nameof(SessionCardViewModel.AsksText)) && questionView is not null && Card is { } c) { questionView.Heading = c.AsksText; }
        if (all || e.PropertyName == nameof(SessionCardViewModel.StatusTone)) { ShowWords(); }
        if (e.PropertyName == nameof(SessionCardViewModel.WantsFocus)) { TakeFocus(); }
    }

    // Opened from Needs you (SessionCardViewModel.WantsFocus): keyboard focus into the question, or
    // the reply box when there is none, so the next Tab is in the session and not back in the lane.
    // A question with nothing to press (one answered at the machine) has the session's own first
    // control instead. Tried again after each layout pass while the pane is still hidden (one
    // column, before the layout changes), and cleared only once it lands (it was once cleared by a
    // row the list had hidden), or once the pane is shown with nothing to take it.
    bool focusPending;
    void TakeFocus()
    {
        if (!attached || Card is not { WantsFocus: true } c) { return; }
        var shown = Visibility == Visibility.Visible && ActualWidth > 0 && ActualHeight > 0;
        var target = (questionView is not null ? FocusManager.FindFirstFocusableElement(questionView) as Control : null)
            ?? (ReplyBox is { IsEnabled: true, Visibility: Visibility.Visible } ? ReplyBox : null)
            ?? FocusManager.FindFirstFocusableElement(this) as Control;
        if (target is null && shown)
        {
            c.WantsFocus = false;
            if (focusPending) { focusPending = false; LayoutUpdated -= OnFocusLayout; }
            return;
        }
        if (target is not null && target.Focus(FocusState.Keyboard))
        {
            c.WantsFocus = false;
            target.StartBringIntoView();
            if (focusPending) { focusPending = false; LayoutUpdated -= OnFocusLayout; }
        }
        else if (!focusPending)
        {
            focusPending = true;
            LayoutUpdated += OnFocusLayout;
        }
    }

    void OnFocusLayout(object? sender, object e)
    {
        if (!attached || Card is not { WantsFocus: true })
        {
            focusPending = false;
            LayoutUpdated -= OnFocusLayout;
            return;
        }
        TakeFocus();
    }

    // After the pane has laid out the session and its tab: the conversation's top is set as a
    // session is shown (OnCard), and the question is brought into view after that.
    void OnQuestionWanted(SessionCardViewModel card) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Card == card && QuestionCard.Visibility == Visibility.Visible) { QuestionCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); }
        });

    // The reply box takes at most half the pane and scrolls inside that, so the conversation, and a
    // question arriving while a long draft is held, always keep the other half.
    void FitComposer(double height)
    {
        if (height > 0) { Composer.MaxHeight = Math.Floor(height / 2); }
    }

    void ShowWords()    {
        var next = WordsState(Card?.StatusTone ?? StatusTone.Plain);
        if (words == next) { return; }
        if (words is null) { VisualStateManager.GoToState(this, "WordsUnset", false); }
        VisualStateManager.GoToState(this, next, false);
        words = next;
    }

    void OnLayout(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneLayoutViewModel.ShownTab) or "" or null) { ShowTab(); }
    }

    // The tab the panes show, selected in the bar: the one picked, or Conversation once the picked
    // one has gone (a resize can bring the bar back with it).
    void ShowTab()
    {
        if (layout is null) { return; }
        var want = layout.ShownTab switch { SessionTab.Delegates => DelegatesTab, SessionTab.Tools => ToolsTab, _ => ConversationTab };
        if (Tabs.SelectedItem == want) { return; }
        // Not the person's choice: the tab they picked stays picked, to come back to when the session
        // or the width has it again.
        showingTab = true;
        try { Tabs.SelectedItem = want; }
        finally { showingTab = false; }
    }

    void OnTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (showingTab || layout is null || sender.SelectedItem?.Tag is not string tag || !Enum.TryParse<SessionTab>(tag, out var tab)) { return; }
        layout.Tab = tab;
    }

    void OnBack(object sender, RoutedEventArgs e) => layout?.Back();
    void OnToggleInspector(object sender, RoutedEventArgs e) => layout?.ToggleInspector();
    void OnPin(object sender, RoutedEventArgs e) => Card?.Row?.TogglePin();

    // A strip item (PaneLayoutViewModel.PressStripItem).
    void OnStripItem(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DelegateItem item) { layout?.PressStripItem(item); }
    }

    void OnDelegateRow(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DelegateItem item) { layout?.OpenDelegate(item.Id); }
    }

    void OnDelegateEvent(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) { layout?.OpenDelegate(id); }
    }

    // "Ask main agent to stop it" filled the reply box: the person reads it there, at its end, and
    // sends it or not.
    void OnReplyWanted()
    {
        if (ReplyBox is not { IsEnabled: true, Visibility: Visibility.Visible }) { return; }
        ReplyBox.Focus(FocusState.Programmatic);
        ReplyBox.Select(ReplyBox.Text.Length, 0);
    }

    void SyncQuestion()
    {
        var q = Card?.Question;
        if (questionView is not null && q is not null && ReferenceEquals(questionView.Tag, q)) { return; }
        questionView?.Detach();
        questionView = q is null ? null : new QuestionView(q, Editing, Card?.AsksText) { Tag = q };
        QuestionHost.Content = questionView;
        QuestionCard.Visibility = Ui.Some(questionView);
    }

    void Editing(bool on)
    {
        if (Card is { } c) { c.Editing = on; }
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

    void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReplyAttachment file }) { Card?.Reply?.Remove(file); }
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
