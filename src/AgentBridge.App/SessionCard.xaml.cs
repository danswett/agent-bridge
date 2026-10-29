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
// SessionCardViewModel; this adds what only a window can do: the glow, pasting and picking
// files, the clipboard and keyboard shortcuts.
public sealed partial class SessionCard : UserControl
{
    // Pictures are read to be made smaller; nothing else over the limit is read at all.
    const long MostRead = 32 * 1024 * 1024;

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(SessionCard), new PropertyMetadata(null, (d, e) => ((SessionCard)d).OnCard(e.OldValue as SessionCardViewModel, e.NewValue as SessionCardViewModel)));

    static readonly bool Animations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    QuestionView? questionView;
    bool pulsing;

    public SessionCard() => InitializeComponent();

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    public static string Chevron(bool folded) => folded ? "\uE76C" : "\uE70D";
    public static string FoldName(string title, bool folded) => $"{title}: {(folded ? "folded, press to open" : "open, press to fold")}";
    public static string RemoveName(string name) => $"Remove {name}";

    void OnCard(SessionCardViewModel? old, SessionCardViewModel? card)
    {
        if (old is not null) { old.PropertyChanged -= OnCardChanged; }
        if (card is not null)
        {
            card.PropertyChanged += OnCardChanged;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, $"Card:{card.Id}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, card.Title);
        }
        SyncQuestion();
        SyncGlow();
        Bindings.Update();
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(SessionCardViewModel.Question)) { SyncQuestion(); }
        if (all) { SyncGlow(); }
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

    void SyncGlow()
    {
        var on = Card?.View is "working" or "waiting";
        if (on && Animations)
        {
            if (!pulsing) { Pulse.Begin(); pulsing = true; }
            return;
        }
        if (pulsing) { Pulse.Stop(); pulsing = false; }
        Glow.Opacity = on ? 0.6 : 0;
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
