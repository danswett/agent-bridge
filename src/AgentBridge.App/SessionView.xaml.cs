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

    public SessionView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
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
            if (layout is not null) { layout.PropertyChanged -= OnLayout; }
            layout = value;
            if (layout is not null) { layout.PropertyChanged += OnLayout; }
            Bindings.Update();
            ShowTab();
        }
    }

    public static string RemoveName(string name) => $"Remove {name}";
    public static string PinName(bool pinned) => pinned ? "Unpin" : "Pin";
    public static Brush PinBrush(bool pinned) => Ui.Brush(pinned ? "AbBridge" : "AbText2");
    public static string WordsState(StatusTone tone) => $"Words{tone}";

    void OnCard(SessionCardViewModel? old, SessionCardViewModel? card)
    {
        if (attached && old is not null) { old.PropertyChanged -= OnCardChanged; }
        if (card is not null)
        {
            if (attached) { card.PropertyChanged += OnCardChanged; }
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
    }

    void Attach()
    {
        if (attached) { return; }
        attached = true;
        if (Card is { } c) { c.PropertyChanged += OnCardChanged; }
        SyncQuestion();
        Bindings.Update();
        words = null;
        ShowWords();
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
        if (all || e.PropertyName == nameof(SessionCardViewModel.StatusTone)) { ShowWords(); }
    }

    void ShowWords()
    {
        var next = WordsState(Card?.StatusTone ?? StatusTone.Plain);
        if (words == next) { return; }
        if (words is null) { VisualStateManager.GoToState(this, "WordsUnset", false); }
        VisualStateManager.GoToState(this, next, false);
        words = next;
    }

    void OnLayout(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneLayoutViewModel.Tab) or "" or null) { ShowTab(); }
    }

    // The tab the panes say, picked in the bar (a resize can bring the bar back with it).
    void ShowTab()
    {
        if (layout is null) { return; }
        var want = layout.Tab switch { SessionTab.Delegates => DelegatesTab, SessionTab.Tools => ToolsTab, _ => ConversationTab };
        if (Tabs.SelectedItem != want) { Tabs.SelectedItem = want; }
    }

    void OnTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (layout is null || sender.SelectedItem?.Tag is not string tag || !Enum.TryParse<SessionTab>(tag, out var tab)) { return; }
        layout.Tab = tab;
    }

    void OnBack(object sender, RoutedEventArgs e) => layout?.Back();
    void OnToggleInspector(object sender, RoutedEventArgs e) => layout?.ToggleInspector();
    void OnPin(object sender, RoutedEventArgs e) => Card?.Row?.TogglePin();

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
