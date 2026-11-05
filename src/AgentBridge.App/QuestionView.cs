using System.ComponentModel;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentBridge.App;

// A card's pending question (pwa/js/ui/question-view.js): its message, then either the answer
// controls or why it can only be answered at the machine. A single-choice question answers on a
// click; Other opens a box; forms have one control per field; Ctrl+Enter sends. Built in code:
// the shape depends on the question's fields.
public sealed class QuestionView : StackPanel
{
    readonly QuestionViewModel vm;
    readonly Action<bool> editing;
    readonly List<Action> refreshers = [];
    readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> subscriptions = [];
    // The answer's receipt (ui.md §6.4, §6.5, #600, #617): words only, for an exception or "You
    // answered", a polite live region that hears every stage, in a row that keeps its place from
    // Send to the outcome. The ring and the check are the button's own.
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly Grid receipt = new();
    // Drawn clear and unheard under the words: the room the longest outcome needs at this width
    // (ReceiptWords.Room), kept from Send so neither a stage nor the outcome moves what is below.
    readonly TextBlock room = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0, IsHitTestVisible = false };
    readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly Button resend = new() { Content = "Send again" };
    Button? send;

    // heading: who asks (ui.md §6.4, "Main agent asks" or "<delegate> asks"), in place of the
    // question's own Waiting on you or Permission requested.
    readonly TextBlock head;
    // The heading as where keyboard focus lands when the session is opened for its question (a
    // toast, Needs you): the question read first, its answers a Tab away. On the first control
    // instead, an approval's Approve once, the next Enter or Space answered it before the person
    // had read the command (#484). A tab stop only while it has focus, so Tab and Shift+Tab pass
    // over it otherwise; named, and a heading, for a screen reader.
    readonly ContentControl headFocus;

    // Who asks, again: a delegate's name can arrive after its question (S1's subagents list).
    public string Heading
    {
        get => head.Text;
        set
        {
            head.Text = value;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(headFocus, value);
        }
    }

    // Keyboard focus on the question's heading (#484): never on one of its answers.
    public bool FocusHeading(FocusState state)
    {
        headFocus.IsTabStop = true;
        if (headFocus.Focus(state)) { return true; }
        headFocus.IsTabStop = false;
        return false;
    }

    internal Control HeadingTarget => headFocus;

    // acted: the person activated one of its controls (a click, a key, UI Automation), as the control
    // acts: what the question lays out next is theirs (SessionView's AnswerWatch.Own). Called as the
    // click happens, not as the pointer went down: a press held longer than a frame or two, or Space
    // held down, acts only on release.
    public QuestionView(QuestionViewModel vm, Action<bool> editing, string? heading = null, Action? acted = null)
    {
        this.vm = vm;
        this.editing = editing;
        this.acted = acted;
        Spacing = 8;
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = Ui.Brush("AbNeeds");
        AutomationId(this, "Question");

        head = new TextBlock { Text = heading ?? vm.Heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("AbNeedsHead") };
        headFocus = new ContentControl { Content = head, IsTabStop = false, UseSystemFocusVisuals = true, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties(headFocus, head.Text, "QuestionHeading");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(headFocus, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        headFocus.LostFocus += (_, _) => headFocus.IsTabStop = false;
        Children.Add(headFocus);
        // What its escapes stand for, above its text (ui.md §6.4): an escape can be read past while its
        // character still acts.
        foreach (var note in vm.Notes)
        {
            Children.Add(new TextBlock { Text = note, Style = Muted(), TextWrapping = TextWrapping.Wrap });
        }
        Children.Add(new MarkdownView { Approval = vm.Approval, Markdown = vm.Message });

        if (!vm.Answerable)
        {
            Children.Add(new TextBlock { Text = vm.ReadOnlyNote, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("AbNeedsText") });
            if (vm.ShowReadOnlyFields)
            {
                foreach (var f in vm.Fields)
                {
                    var options = string.Join(", ", f.Options.Select(o => o.Label));
                    Children.Add(new TextBlock { Text = options.Length > 0 ? $"{f.Label}: {options}" : f.Label, Style = Muted(), TextWrapping = TextWrapping.Wrap });
                }
            }
        }
        else if (vm.Fields.Count == 0) { FreeText(); }
        else if (vm.Tap) { Tap(vm.Fields[0]); }
        else { Form(); }

        if (vm.Answerable)
        {
            error.Foreground = Ui.Brush("AbDangerText");
            Acting(resend).Click += (_, _) => vm.Resend();
            AutomationId(resend, "Resend");
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (send is not null) { footer.Children.Add(send); }
            footer.Children.Add(resend);
            Children.Add(footer);
            Children.Add(error);
            room.Text = vm.StatusRoom;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(room, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            receipt.Children.Add(room);
            receipt.Children.Add(status);
            Children.Add(receipt);
        }

        Watch(vm, Refresh);
        Refresh();
        drawn = true;
    }

    readonly Action? acted;

    T Acting<T>(T b) where T : ButtonBase
    {
        b.Click += (_, _) => acted?.Invoke();
        return b;
    }

    // Past the first draw: what changes from here is news (an answer's outcome).
    bool drawn;
    // What the receipt last told a screen reader.
    string announced = "";

    // The card let go of this question; the view stops following it.
    public void Detach()
    {
        foreach (var (s, h) in subscriptions) { s.PropertyChanged -= h; }
        subscriptions.Clear();
    }

    void Watch(INotifyPropertyChanged source, Action changed)
    {
        PropertyChangedEventHandler h = (_, _) => changed();
        source.PropertyChanged += h;
        subscriptions.Add((source, h));
    }

    void Refresh()
    {
        // Each change of its words fades in (ui.md §5.3, Send and Answer); under reduced motion
        // Motion makes that the cross-fade alone.
        var fade = drawn && vm.StatusText.Length > 0 && vm.StatusText != status.Text;
        status.Text = vm.StatusText;
        status.Foreground = Ui.ReceiptBrush(vm.StatusTone);
        receipt.Visibility = Ui.Show(vm.StatusText.Length > 0 || vm.InFlight);
        if (fade) { Motion.Enter(status, "Acknowledge"); }
        // Heard as it changes, not as the card is first drawn with what was said before.
        if (vm.Announcement != announced)
        {
            announced = vm.Announcement;
            if (drawn)
            {
                ReceiptMarkView.SetUrgent(status, vm.AnnouncementUrgent);
                ReceiptMarkView.SetAnnouncement(status, announced);
            }
        }
        error.Text = vm.Error ?? "";
        error.Visibility = Ui.Show(vm.HasError);
        resend.Visibility = Ui.Show(vm.ShowResend);
        if (send is not null)
        {
            send.Visibility = Ui.Show(vm.ShowSend);
            // The ring, then the check, on Send, unless one of the options sent it; Send keeps its
            // look while it shows them (ui.md §6.5), and vm.Send does nothing unless CanAnswer.
            var mine = !(vm.Tap && vm.Fields is [var tapped, ..] && tapped.Options.Any(o => o.Value == vm.SentValue));
            send.IsEnabled = vm.CanAnswer || (mine && (vm.InFlight || vm.SendCheck));
            Ring(send, mine && vm.InFlight, mine && vm.SendCheck, vm.StatusMark == ReceiptMark.Still);
        }
        foreach (var r in refreshers) { r(); }
    }

    void FreeText()
    {
        var box = Box("Type your answer", multiline: true);
        box.Text = vm.FreeText;
        box.TextChanged += (_, _) => vm.FreeText = box.Text;
        AutomationId(box, "AnswerText");
        Children.Add(box);
        send = SendButton();
    }

    // One click answers. Recommended first, allow and deny coloured on an approval.
    void Tap(FieldViewModel f)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var o in f.Options)
        {
            var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var label = new TextBlock { Text = o.Label, TextWrapping = TextWrapping.Wrap };
            b.Content = WithRing(label, trailing: true);
            if (o.Recommended || (vm.Approval && o.Tone == "allow")) { b.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; }
            if (vm.Approval && o.Tone == "deny") { b.Foreground = Ui.Brush("AbDangerText"); }
            AutomationProperties(b, o.Label, $"Option:{o.Value}");
            // Choose changes the selection first, so only while the question can be answered.
            Acting(b).Click += (_, _) => { if (vm.CanAnswer) { vm.Choose(o); } };
            refreshers.Add(() =>
            {
                var sent = vm.SentValue == o.Value;
                // Tap to answer (ui.md §6.5): the tapped option is the control that sent it, so it
                // keeps full opacity, with its ring and then its check, while the others are disabled,
                // and stays pressed after: a stale or unconfirmed answer may have been typed, and is the
                // prompt's one attempt. Busy or refused typed nothing, so all are enabled again, none
                // pressed (QuestionViewModel.CanAnswer, SentValue). A press on it does nothing (Click).
                b.IsEnabled = vm.CanAnswer || sent;
                // Its ring and check say it goes; the ✓ marks which option went once they are done.
                label.Text = sent && !vm.InFlight && !vm.SendCheck ? $"✓ {o.Label}" : o.Label;
                Ring(b, sent && vm.InFlight, sent && vm.SendCheck, vm.StatusMark == ReceiptMark.Still);
            });
            panel.Children.Add(b);
        }
        Children.Add(panel);
        if (!f.AllowOther) { return; }

        var other = new ToggleButton { Content = "Other…", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties(other, "Other", "Other");
        Acting(other).Click += (_, _) => vm.ToggleOther();
        panel.Children.Add(other);
        var box = Box("Type your own answer", multiline: false);
        box.Text = f.OtherText;
        box.TextChanged += (_, _) => f.OtherText = box.Text;
        AutomationId(box, "OtherText");
        Children.Add(box);
        send = SendButton();
        refreshers.Add(() =>
        {
            other.IsChecked = vm.OtherOpen;
            other.IsEnabled = vm.CanAnswer;
            box.Visibility = Ui.Show(vm.OtherOpen);
        });
    }

    void Form()
    {
        foreach (var f in vm.Fields)
        {
            var field = new StackPanel { Spacing = 4 };
            field.Children.Add(new TextBlock
            {
                Text = f.Required ? $"{f.Label} (required)" : f.Label,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(f.Description)) { field.Children.Add(new TextBlock { Text = f.Description, Style = Muted(), TextWrapping = TextWrapping.Wrap }); }
            field.Children.Add(new TextBlock { Text = f.TypeLabel, Style = Muted(), FontSize = 12 });
            field.Children.Add(Control(f));
            var frame = new Border { Child = field, BorderThickness = new Thickness(0, 0, 0, 0), Padding = new Thickness(0, 2, 0, 2) };
            Watch(f, () => FieldInvalid(frame, f));
            FieldInvalid(frame, f);
            Children.Add(frame);
        }
        send = SendButton();
    }

    // The bar sits in the gutter to the field's left, pulled out by a margin as wide as it and its
    // padding: the field's own controls stay where they are, invalid or not. Pushing them right
    // moved the next field's box under the pointer as a second Send marked it, which the answer
    // guard (#491) held as a move, dropping the person's click into it.
    static void FieldInvalid(Border frame, FieldViewModel f)
    {
        frame.BorderThickness = new Thickness(f.Invalid ? 2 : 0, 0, 0, 0);
        frame.BorderBrush = Ui.Brush("AbDanger");
        frame.Padding = new Thickness(f.Invalid ? 8 : 0, 2, 0, 2);
        frame.Margin = new Thickness(f.Invalid ? -10 : 0, 0, 0, 0);
    }

    UIElement Control(FieldViewModel f)
    {
        switch (f.Type)
        {
            case FieldType.Choice:
            case FieldType.Multi:
                {
                    var panel = new StackPanel { Spacing = 2 };
                    var group = $"{vm.Id}:{f.Name}";
                    foreach (var o in f.Options)
                    {
                        var choice = Acting<ToggleButton>(f.Type == FieldType.Choice ? new RadioButton { GroupName = group } : new CheckBox());
                        choice.Content = new TextBlock { Text = o.Label, TextWrapping = TextWrapping.Wrap };
                        AutomationProperties(choice, o.Label, $"Option:{f.Name}:{o.Value}");
                        choice.IsChecked = o.IsSelected;
                        choice.Checked += (_, _) => o.IsSelected = true;
                        choice.Unchecked += (_, _) => o.IsSelected = false;
                        Watch(o, () => { if (choice.IsChecked != o.IsSelected) { choice.IsChecked = o.IsSelected; } });
                        panel.Children.Add(choice);
                    }
                    if (f.AllowOther)
                    {
                        var other = Acting<ToggleButton>(f.Type == FieldType.Choice ? new RadioButton { GroupName = group, Content = "Other" } : new CheckBox { Content = "Other" });
                        AutomationProperties(other, "Other", $"Other:{f.Name}");
                        other.IsChecked = f.OtherChosen;
                        other.Checked += (_, _) => f.OtherChosen = true;
                        other.Unchecked += (_, _) => f.OtherChosen = false;
                        var box = Box("Type your own answer", multiline: false);
                        box.Text = f.OtherText;
                        box.TextChanged += (_, _) => f.OtherText = box.Text;
                        Watch(f, () => { if (other.IsChecked != f.OtherChosen) { other.IsChecked = f.OtherChosen; } });
                        panel.Children.Add(other);
                        panel.Children.Add(box);
                    }
                    return panel;
                }
            case FieldType.Boolean:
                {
                    var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var yes = new ToggleButton { Content = "Yes", MinWidth = 72 };
                    var no = new ToggleButton { Content = "No", MinWidth = 72 };
                    AutomationProperties(yes, $"{f.Label}: Yes", $"Yes:{f.Name}");
                    AutomationProperties(no, $"{f.Label}: No", $"No:{f.Name}");
                    void Show() { yes.IsChecked = f.BoolValue == true; no.IsChecked = f.BoolValue == false; }
                    Acting(yes).Click += (_, _) => { f.ToggleBool(true); Show(); };
                    Acting(no).Click += (_, _) => { f.ToggleBool(false); Show(); };
                    Watch(f, Show);
                    Show();
                    panel.Children.Add(yes);
                    panel.Children.Add(no);
                    return panel;
                }
            case FieldType.Number:
                {
                    var box = Box("A number", multiline: false);
                    box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
                    box.Text = f.Text;
                    box.TextChanged += (_, _) => f.Text = box.Text;
                    AutomationProperties(box, f.Label, $"Field:{f.Name}");
                    return box;
                }
            default:
                {
                    var box = Box("Type here", multiline: true);
                    box.Text = f.Text;
                    box.TextChanged += (_, _) => f.Text = box.Text;
                    AutomationProperties(box, f.Label, $"Field:{f.Name}");
                    return box;
                }
        }
    }

    // A button's label with the platform's progress ring beside it, shown while the answer it sent
    // is on its way (ui.md §5.3, Send and Answer; InFlightRing), and then the check for about a
    // second once it is delivered (§6.5, CheckGlyph). Beside the label, never over it or fading
    // it: before it on the answer's button, at a tapped option's trailing edge (§6.5, "Tap to
    // answer"). A grid, not a horizontal stack: a stack measures the label at any width, and a
    // long option would no longer wrap to the card. An option's indicator column keeps its room at
    // rest: sized to what it held, it grew from nothing to 24 px as the ring came, and a tapped
    // option's label re-wrapped and moved the options below it (#625, Copilot 4478422056). Send's
    // stays sized to it: the button grows by the ring beside its one-line label, which moves nothing
    // (Send again shows only once Send is at rest), where a gap kept at rest would set "Send" off
    // centre in its most common state.
    static Grid WithRing(FrameworkElement label, bool trailing = false)
    {
        const double Indicator = 16, Gap = 8;
        var margin = trailing ? new Thickness(Gap, 0, 0, 0) : new Thickness(0, 0, Gap, 0);
        var ring = new InFlightRing { Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        var check = new CheckGlyph { Margin = margin, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var grid = new Grid();
        var auto = new ColumnDefinition { Width = trailing ? new GridLength(Indicator + Gap) : GridLength.Auto };
        var rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        grid.ColumnDefinitions.Add(trailing ? rest : auto);
        grid.ColumnDefinitions.Add(trailing ? auto : rest);
        Grid.SetColumn(label, trailing ? 0 : 1);
        Grid.SetColumn(ring, trailing ? 1 : 0);
        Grid.SetColumn(check, trailing ? 1 : 0);
        grid.Children.Add(ring);
        grid.Children.Add(check);
        grid.Children.Add(label);
        return grid;
    }

    // still: the answer waits on this device for its machine, so the ring stands still (§6.5).
    static void Ring(Button b, bool on, bool check, bool still)
    {
        if (b.Content is not Grid { Children: [InFlightRing ring, CheckGlyph glyph, ..] }) { return; }
        ring.Still = on && still;
        ring.Active = on;
        glyph.Visibility = Ui.Show(check && !on);
    }

    Button SendButton()
    {
        var b = new Button { Content = WithRing(new TextBlock { Text = vm.SendLabel }), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties(b, vm.SendLabel, "SendAnswer");
        Acting(b).Click += (_, _) => vm.Send();
        return b;
    }

    // A text box that sends on Ctrl+Enter and holds the card list still while it has focus.
    TextBox Box(string placeholder, bool multiline)
    {
        var box = new TextBox { PlaceholderText = placeholder, TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MinHeight = multiline ? 64 : 0, MaxHeight = 240 };
        box.GotFocus += (_, _) => editing(true);
        box.LostFocus += (_, _) => editing(false);
        // Its text changed by the person however they did it, keys or not (a paste or Cut from the
        // context menu, an IME, undo): the box growing with it is theirs (acted). A paste or Cut
        // says so before anything is put in or taken out; any other edit, as early as the box tells
        // of it. (Text set from code is laid out before the box tells of it, so a self-test can't
        // stand in for a paste here.)
        box.BeforeTextChanging += (_, _) => { if (box.IsLoaded) { acted?.Invoke(); } };
        box.Paste += (_, _) => acted?.Invoke();
        box.CuttingToClipboard += (_, _) => acted?.Invoke();
        // Preview: a multi-line TextBox handles Enter itself and marks KeyDown handled, so a
        // KeyDown handler never saw Ctrl+Enter, and it inserted a line instead of sending (#119).
        box.PreviewKeyDown += (_, e) =>
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Enter && (ctrl || !multiline))
            {
                e.Handled = true;
                vm.Send();
            }
        };
        return box;
    }

    static Style Muted() => (Style)Application.Current.Resources["AbSecondaryText"];

    static void AutomationId(DependencyObject o, string id) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(o, id);

    static void AutomationProperties(DependencyObject o, string name, string id)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(o, id);
    }
}
