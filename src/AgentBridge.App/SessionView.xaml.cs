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
        Loaded += (_, _) =>
        {
            Attach();
            WatchReleases();
            FocusManager.GettingFocus -= OnFocusMoving;
            FocusManager.GettingFocus += OnFocusMoving;
        };
        SizeChanged += (_, e) => FitComposer(e.NewSize.Height);
        Unloaded += (_, _) => { if (!IsLoaded) { Detach(); UnwatchReleases(); FocusManager.GettingFocus -= OnFocusMoving; } };
        // The Words states' ThemeResources are read as a state is entered: entered again, they are
        // the new theme's.
        ActualThemeChanged += (_, _) => { words = null; ShowWords(); };
        // Where the person has left the conversation, and whether a finger is on it (ui.md §5.3,
        // Scroll to a question). Touch through handled events too: the scroller handles them. The
        // scroller is the conversation list's own, there once its template is (HookConversation).
        // Hooked once the list's template is there: at Loaded, or at its first layout when it was
        // collapsed then (nothing chosen yet), since showing it raises no Loaded.
        ConversationList.Loaded += (_, _) => HookConversation();
        ConversationList.SizeChanged += (_, e) =>
        {
            HookConversation();
            if (e.PreviousSize.Width != e.NewSize.Width) { KeepAnchor(); }
        };
        AddHandler(LosingFocusEvent, new Windows.Foundation.TypedEventHandler<UIElement, LosingFocusEventArgs>(OnLeavingBlock), true);
        AddHandler(GettingFocusEvent, new Windows.Foundation.TypedEventHandler<UIElement, GettingFocusEventArgs>(OnReachingBlock), true);
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnTabKey), true);
        // A press anywhere in the session moves focus as the person means it: a held Tab then does nothing (Resume).
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => presses++), true);
        ConversationList.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (Touch(e)) { follow.TouchDown(e.Pointer.PointerId); }
            else { follow.Pressed(e.Pointer.PointerId); }
        }), true);
        ConversationList.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => { if (Touch(e)) { follow.TouchUp(e.Pointer.PointerId); } }), true);
        ConversationList.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, e) => { if (Touch(e)) { follow.TouchCanceled(e.Pointer.PointerId); } }), true);
        ConversationList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, e) => { if (Touch(e)) { follow.TouchCanceled(e.Pointer.PointerId); } }), true);
        // A mouse or pen over the conversation holds it as a finger does (#261's guard).
        ConversationList.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, e) => { if (!Touch(e)) { follow.PointerOver(e.Pointer.PointerId); } }), true);
        ConversationList.AddHandler(PointerExitedEvent, new PointerEventHandler((_, e) => { if (!Touch(e)) { follow.PointerLeft(e.Pointer.PointerId); } }), true);
        // The question's answers moved where they are (#491): held again. And a pointer's press on
        // one reads where they are first, and is dropped if they moved (PointerLets).
        answers = new AnswerWatch(QuestionCard, () => questionView, OnAnswersMoved);
        QuestionCard.AddHandler(PointerPressedEvent, new PointerEventHandler(OnQuestionPressed), true);
        QuestionCard.AddHandler(KeyDownEvent, new KeyEventHandler((_, _) => answers.Own()), true);
    }

    readonly AnswerWatch answers;

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
        promptShownOpen = Card?.PromptOpen == true;
        Motion.Settle(PromptRule);
        Motion.Turn(PromptChevron, "Disclosure", promptShownOpen, instant: true);
    }

    // Show prompt (§5.3, Disclosure): the chevron turns, and what opens settles 4 down into a space
    // opened at once; closing is at once but for the chevron turning back.
    bool promptShownOpen;
    void Disclose()
    {
        if (Card is not { } c || c.PromptOpen == promptShownOpen) { return; }
        promptShownOpen = c.PromptOpen;
        Motion.Turn(PromptChevron, "Disclosure", promptShownOpen);
        if (promptShownOpen) { Motion.Enter(PromptRule, "Disclosure", new System.Numerics.Vector2(0, -1)); }
        else { Motion.Settle(PromptRule); }
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
            // What a screen reader hears of it is the view model's ReceiptAnnouncement, once a
            // change and at most one a second (Receipt), bound on the line.
            if (c.ReceiptText.Length > 0) { Motion.Enter(Receipt, "Acknowledge"); }
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
            if (layout is not null) { layout.PropertyChanged -= OnLayout; layout.QuestionWanted -= OnQuestionWanted; layout.ConversationShifted -= GuardAnswer; }
            layout = value;
            if (layout is not null) { layout.PropertyChanged += OnLayout; layout.QuestionWanted += OnQuestionWanted; layout.ConversationShifted += GuardAnswer; }
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
        // Another session's answers: measured from where they are drawn, held as they appear.
        answers.Forget();
        SyncQuestion();
        ReceiptMarkView.Hushed(Bindings.Update);
        Settle();
        // Another session: nothing the last one was doing (a push, a tab's fade) carries over.
        Motion.Settle(ViewRoot);
        Motion.Settle(TabContent);
        ShowWords();
        follow.Reset();
        ShowResponse(reset: true);
        ConversationScroll?.ChangeView(null, 0, null, disableAnimation: true);
        TakeFocus();
    }

    void Attach()
    {
        if (attached) { return; }
        attached = true;
        if (Card is { } c) { c.PropertyChanged += OnCardChanged; c.Delegates.ReplyWanted += OnReplyWanted; }
        answers.Start();
        SyncQuestion();
        ReceiptMarkView.Hushed(Bindings.Update);
        ShowResponse(reset: false);
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
        answers.Stop();
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
        if (all || e.PropertyName is nameof(SessionCardViewModel.Response) or nameof(SessionCardViewModel.HasVoice)) { ShowResponse(reset: false); }
        if (all || e.PropertyName == nameof(SessionCardViewModel.PromptOpen)) { Disclose(); }
        if ((all || e.PropertyName == nameof(SessionCardViewModel.AsksText)) && questionView is not null && Card is { } c && questionView.Heading != c.AsksText)
        {
            // Who asks, drawn by the app in the question: not the person's typing laid out.
            answers.NotOwn();
            questionView.Heading = c.AsksText;
        }
        if (all || e.PropertyName == nameof(SessionCardViewModel.StatusTone)) { ShowWords(); }
        if (e.PropertyName == nameof(SessionCardViewModel.WantsFocus)) { TakeFocus(); }
    }

    // Opened from Needs you or a toast (SessionCardViewModel.WantsFocus): keyboard focus on the
    // question's heading, or the reply box when there is none, so the next Tab is in the session
    // and not back in the lane. Never on the question's first control: an approval's is Approve
    // once, and the next Enter or Space answered it before the person had read the command
    // (#484). Tried again after each layout pass while the pane is still hidden (one column,
    // before the layout changes), and cleared only once it lands (it was once cleared by a row
    // the list had hidden), or once the pane is shown with nothing to take it. The heading is
    // focused programmatically, so its ring shows only if a key brought it there, not after a
    // click on a toast (ui.md A12).
    bool focusPending;
    void TakeFocus()
    {
        if (!attached || Card is not { WantsFocus: true } c) { return; }
        var shown = Visibility == Visibility.Visible && ActualWidth > 0 && ActualHeight > 0;
        var heading = questionView?.HeadingTarget;
        var target = heading
            ?? (ReplyBox is { IsEnabled: true, Visibility: Visibility.Visible } ? ReplyBox : null)
            ?? FocusManager.FindFirstFocusableElement(this) as Control;
        if (target is null && shown)
        {
            c.WantsFocus = false;
            if (focusPending) { focusPending = false; LayoutUpdated -= OnFocusLayout; }
            return;
        }
        if (target is not null && (target == heading ? questionView!.FocusHeading(FocusState.Programmatic) : target.Focus(FocusState.Keyboard)))
        {
            c.WantsFocus = false;
            // One scroll for the question: focus inside it brings the whole card into view, heading
            // first, on the same terms as QuestionWanted's (smooth once the page has drawn, a jump
            // at start or with animations off), and that queued scroll then stands down. A card
            // taller than the conversation's viewport brings the focused heading instead: brought
            // in with the least scroll, a card already filling the viewport from midway through
            // it moves nowhere, and the heading with focus stays out of sight (#893 review, Copilot
            // 4481392664, the PWA's revealQuestion).
            var inQuestion = Motion.Within(target, QuestionCard);
            var tall = ConversationScroll is { ViewportHeight: > 0 } scroll && QuestionCard.ActualHeight > scroll.ViewportHeight;
            (inQuestion && !(tall && target == heading) ? (UIElement)QuestionCard : target).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = Ready && Motion.Scrolls });
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
        if (!Ready || !follow.Follows || layout?.ViewingConversation != true || Card is not { } card) { return; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Asked again: a scroll of the person's own since (a wheel, a key) has the last word.
            // Follows changes only as the view moves, never as the question is laid out above it.
            if (Card != card || !follow.Follows || layout?.ViewingConversation != true || QuestionCard.Visibility != Visibility.Visible) { return; }
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
        if (e.PropertyName is (nameof(PaneLayoutViewModel.ViewingConversation) or "" or null) && layout is not null
            && guard.Shown(layout.ViewingConversation, DateTimeOffset.UtcNow)) { GuardAnswer(); }
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
        questionView = q is null ? null : new QuestionView(q, Editing, Card?.AsksText, answers.Own) { Tag = q };
        QuestionHost.Content = questionView;
        answers.Forget();
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

    // The window activated again with the conversation in view (SessionsPage.Reactivated).
    internal void Reactivated() { if (layout?.ViewingConversation == true) { GuardAnswer(); } }

    // For the self-test: the question's controls, and whether they take a pointer.
    internal UIElement? QuestionShown => questionView;

    // Where the question's answers are moved, with the question still: on screen, held again.
    void OnAnswersMoved() { if (layout?.ViewingConversation != false) { GuardAnswer(); } }

    // A pointer pressed in the question: on one of its answers, let through only if they are where
    // they were and the wait has run out (PointerLets). Dropped, the button lets the pointer go, so
    // no click follows from this press. Let through, what the question lays out next is theirs.
    void OnQuestionPressed(object sender, PointerRoutedEventArgs e)
    {
        if (questionView is null || e.OriginalSource is not DependencyObject source || !Motion.Within(source, questionView)) { return; }
        if (PointerLets(source)) { answers.Own(); return; }
        (answers.AnswerAt(source) as UIElement)?.ReleasePointerCaptures();
    }

    // Whether a pointer's press at target goes through (#491, #559's click-time check). On an answer,
    // where the answers are is read first: one moved since the last read (laid out after an update
    // in the same task) holds them, and so does a read that fails; either way this press is dropped.
    // Keys, a gamepad and UI Automation never come here: they act at once.
    internal bool PointerLets(DependencyObject target)
    {
        if (answers.AnswerAt(target) is null) { return true; }
        try
        {
            // Held here as well as by the move's own call: that one holds only a conversation on
            // screen, and this press is on the question whatever the layout says.
            if (answers.Check()) { GuardAnswer(); return false; }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write($"answers: unreadable as a pointer pressed one ({e.GetType().Name})");
            GuardAnswer();
            return false;
        }
        if (guard.Accepts(pointer: true, DateTimeOffset.UtcNow)) { return true; }
        GuardAnswer();
        return false;
    }

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