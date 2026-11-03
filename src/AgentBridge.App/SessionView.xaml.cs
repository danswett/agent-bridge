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
        Loaded += (_, _) => { Attach(); WatchReleases(); };
        SizeChanged += (_, e) => FitComposer(e.NewSize.Height);
        Unloaded += (_, _) => { if (!IsLoaded) { Detach(); UnwatchReleases(); } };
        // The Words states' ThemeResources are read as a state is entered: entered again, they are
        // the new theme's.
        ActualThemeChanged += (_, _) => { words = null; ShowWords(); };
        // Where the person has left the conversation, and whether a finger is on it (ui.md §5.3,
        // Scroll to a question). Touch through handled events too: the scroller handles them.
        ConversationScroll.ViewChanged += (_, _) =>
        {
            follow.Scrolled(ConversationScroll.VerticalOffset, ConversationScroll.ScrollableHeight);
            // A question still moving into view is held from the pointer until it has stopped.
            guard.Scrolled(DateTimeOffset.UtcNow);
        };
        ConversationScroll.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (Touch(e)) { follow.TouchDown(e.Pointer.PointerId); }
            else { follow.Pressed(e.Pointer.PointerId); }
        }), true);
        ConversationScroll.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => { if (Touch(e)) { follow.TouchUp(e.Pointer.PointerId); } }), true);
        ConversationScroll.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, e) => { if (Touch(e)) { follow.TouchCanceled(e.Pointer.PointerId); } }), true);
        ConversationScroll.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, e) => { if (Touch(e)) { follow.TouchCanceled(e.Pointer.PointerId); } }), true);
        // A mouse or pen over the conversation holds it as a finger does (#261's guard).
        ConversationScroll.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, e) => { if (!Touch(e)) { follow.PointerOver(e.Pointer.PointerId); } }), true);
        ConversationScroll.AddHandler(PointerExitedEvent, new PointerEventHandler((_, e) => { if (!Touch(e)) { follow.PointerLeft(e.Pointer.PointerId); } }), true);
        ConversationScroll.DirectManipulationStarted += (_, _) => follow.PanStarted();
        ConversationScroll.DirectManipulationCompleted += (_, _) => follow.PanEnded();
    }

    readonly ConversationFollow follow = new();
    static bool Touch(PointerRoutedEventArgs e) => e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;

    // A mouse or pen pressed in the conversation is let go wherever in the window it is released,
    // or, released outside the window, as soon as it moves in the window again not pressed.
    UIElement? releases;
    PointerEventHandler? onRelease, onMove;

    void WatchReleases()
    {
        if (releases is not null || XamlRoot?.Content is not UIElement root) { return; }
        releases = root;
        onRelease ??= (_, e) => { if (!Touch(e)) { follow.Released(e.Pointer.PointerId); } };
        onMove ??= (_, e) => { if (!Touch(e) && !e.Pointer.IsInContact) { follow.Released(e.Pointer.PointerId); } };
        root.AddHandler(PointerReleasedEvent, onRelease, true);
        root.AddHandler(PointerCanceledEvent, onRelease, true);
        root.AddHandler(PointerCaptureLostEvent, onMove, true);
        root.AddHandler(PointerMovedEvent, onMove, true);
    }

    void UnwatchReleases()
    {
        if (releases is null) { return; }
        releases.RemoveHandler(PointerReleasedEvent, onRelease);
        releases.RemoveHandler(PointerCanceledEvent, onRelease);
        releases.RemoveHandler(PointerCaptureLostEvent, onMove);
        releases.RemoveHandler(PointerMovedEvent, onMove);
        releases = null;
    }

    // What the conversation last showed of these, so only a change on this session moves.
    string shownMessage = "";
    string shownReceipt = "";

    // The panes' motion on this pane (SessionsPage passes it on once the app has drawn): the
    // session pushed in from the trailing side, fading only when it asks a question; a question
    // arriving mid-push settles it; a tab chosen fades its content in without sliding.
    internal void Moved(PaneMove move)
    {
        switch (move.Kind)
        {
            case PaneMotion.Push: Motion.Enter(ViewRoot, "Push", move.Travels ? Motion.Trailing(this) : default); break;
            case PaneMotion.Settle: Motion.Settle(ViewRoot); break;
            case PaneMotion.Tab: Motion.Enter(TabContent, "Tab"); break;
        }
    }

    // The last question this session was seen asking, kept through a moment with none.
    string? questionSeen;

    // Shown for another session, or again: what it shows now is not news.
    void Settle()
    {
        questionSeen = Card?.Question?.Id;
        HeaderGlyph.Settle();
        Motion.Settle(YourMessage);
        Motion.Settle(Receipt);
        shownMessage = Card?.UserMessage ?? "";
        shownReceipt = Card?.ReceiptText ?? "";
    }

    // Your message rises 12 as it fades in; an answer's outcome fades in where it is.
    void Acknowledge()
    {
        if (Card is not { } c) { return; }
        if (c.UserMessage != shownMessage)
        {
            shownMessage = c.UserMessage;
            if (c.HasUserMessage) { Motion.Enter(YourMessage, "Acknowledge", new System.Numerics.Vector2(0, 1)); }
        }
        if (c.ReceiptText != shownReceipt)
        {
            shownReceipt = c.ReceiptText;
            if (c.HasReceipt) { Motion.Enter(Receipt, "Acknowledge"); }
        }
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
        Settle();
        // Another session: nothing the last one was doing (a push, a tab's fade) carries over.
        Motion.Settle(ViewRoot);
        Motion.Settle(TabContent);
        ShowWords();
        follow.Reset();
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
        Settle();
        words = null;
        ShowWords();
        TakeFocus();
    }

    void Detach()
    {
        if (!attached) { return; }
        attached = false;
        if (Card is { } c) { c.PropertyChanged -= OnCardChanged; c.Delegates.ReplyWanted -= OnReplyWanted; }
        // The question goes with the view: its guard's timer has nothing left to let go of.
        guardTimer?.Stop();
        questionView?.Detach();
        questionView = null;
        QuestionHost.Content = null;
        Bindings.StopTracking();
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(SessionCardViewModel.Question))
        {
            SyncQuestion();
            // A question the host asked since: not the same one rebuilt (new capabilities replace
            // it through a moment with none), nor one shown before.
            if (Card?.Question is { Id: var id } && id != questionSeen)
            {
                questionSeen = id;
                FollowQuestion();
            }
        }
        if (all || e.PropertyName is nameof(SessionCardViewModel.UserMessage) or nameof(SessionCardViewModel.ReceiptText)) { Acknowledge(); }
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
            // One scroll for the question: focus inside it brings the whole card into view, heading
            // first, on the same terms as QuestionWanted's (smooth once the page has drawn, a jump
            // at start or with animations off), and that queued scroll then stands down.
            var inQuestion = Motion.Within(target, QuestionCard);
            (inQuestion ? (UIElement)QuestionCard : target).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = Ready && Motion.Scrolls });
            if (inQuestion) { GuardAnswer(); }
            questionScrolled |= inQuestion;
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
    void OnQuestionWanted(SessionCardViewModel card)
    {
        // The conversation is shown at once for the question, not still fading in from a tab.
        Motion.Settle(TabContent);
        // A toast that started the app jumps to its question: nothing moves as the app starts.
        var smooth = Ready && Motion.Scrolls;
        // Only a scroll focus made for this same request stands in for this one: focus left in the
        // question from before (a toast reopening the session) scrolls nothing by itself.
        questionScrolled = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Card != card || QuestionCard.Visibility != Visibility.Visible || questionScrolled) { return; }
            QuestionCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = smooth });
            GuardAnswer();
        });
    }

    // Focus going into the question since the last request brought the card into view.
    bool questionScrolled;

    // The Sessions page has drawn its list (SessionsPage): until then, nothing here moves.
    internal bool Ready { get; set; }

    // A question arriving on the session shown is scrolled to, smoothly, only while the person was
    // at the end of the conversation and not touching it, as they left it before the question was
    // laid out; never once they have scrolled back through it (ui.md §5.3, Scroll to a question).
    void FollowQuestion()
    {
        if (!Ready || !follow.Follows || layout?.ShowConversation != true || Card is not { } card) { return; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Asked again: a scroll of the person's own since (a wheel, a key) has the last word.
            // Follows changes only as the view moves, never as the question is laid out above it.
            if (Card != card || !follow.Follows || layout?.ShowConversation != true || QuestionCard.Visibility != Visibility.Visible) { return; }
            QuestionCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = Motion.Scrolls });
            GuardAnswer();
        });
    }

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
        // The conversation back (its tab picked again, the session pushed in, the window shown):
        // its question is under a pointer aimed at what was there, motion or none.
        if (e.PropertyName is (nameof(PaneLayoutViewModel.ShowConversation) or "" or null) && layout is not null
            && guard.Shown(layout.ShowConversation && layout.OnScreen, DateTimeOffset.UtcNow)) { GuardAnswer(); }
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
        if (questionView is not null) { GuardAnswer(); }
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

    // The question's controls take no click for a moment after they appear or are brought into
    // view (App.Core's AnswerGuard): out of hit testing meanwhile, so a pointer passes through to
    // nothing, while keyboard focus and keys work as ever.
    readonly AnswerGuard guard = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? guardTimer;

    void GuardAnswer()
    {
        if (questionView is null) { return; }
        guard.Arm(DateTimeOffset.UtcNow);
        questionView.IsHitTestVisible = false;
        guardTimer ??= NewGuardTimer();
        guardTimer.Interval = guard.Remaining(DateTimeOffset.UtcNow);
        guardTimer.Start();
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer NewGuardTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            var left = guard.Remaining(DateTimeOffset.UtcNow);
            if (left > TimeSpan.Zero) { t.Interval = left; t.Start(); return; }
            if (questionView is not null) { questionView.IsHitTestVisible = true; }
        };
        return timer;
    }
}