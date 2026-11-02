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
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly Button resend = new() { Content = "Send again" };
    Button? send;

    public QuestionView(QuestionViewModel vm, Action<bool> editing)
    {
        this.vm = vm;
        this.editing = editing;
        Spacing = 8;
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = Ui.Brush("AbNeeds");
        AutomationId(this, "Question");

        Children.Add(new TextBlock { Text = vm.Heading, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("AbNeedsHead") });
        Children.Add(new MarkdownView { Markdown = vm.Message });

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
            resend.Click += (_, _) => vm.Resend();
            AutomationId(resend, "Resend");
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (send is not null) { footer.Children.Add(send); }
            footer.Children.Add(resend);
            Children.Add(footer);
            Children.Add(error);
            Children.Add(status);
        }

        Watch(vm, Refresh);
        Refresh();
    }

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
        status.Text = vm.StatusText;
        status.Foreground = Ui.ToneBrush(vm.StatusTone);
        status.Visibility = Ui.Some(vm.StatusText);
        error.Text = vm.Error ?? "";
        error.Visibility = Ui.Show(vm.HasError);
        resend.Visibility = Ui.Show(vm.ShowResend);
        if (send is not null)
        {
            send.Visibility = Ui.Show(vm.ShowSend);
            send.IsEnabled = vm.CanAnswer;
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
            var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Content = new TextBlock { Text = o.Label, TextWrapping = TextWrapping.Wrap };
            if (o.Recommended || (vm.Approval && o.Tone == "allow")) { b.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; }
            if (vm.Approval && o.Tone == "deny") { b.Foreground = Ui.Brush("AbDangerText"); }
            AutomationProperties(b, o.Label, $"Option:{o.Value}");
            b.Click += (_, _) => vm.Choose(o);
            refreshers.Add(() =>
            {
                b.IsEnabled = vm.CanAnswer;
                var sent = vm.SentValue == o.Value;
                ((TextBlock)b.Content).Text = sent ? $"✓ {o.Label}" : o.Label;
            });
            panel.Children.Add(b);
        }
        Children.Add(panel);
        if (!f.AllowOther) { return; }

        var other = new ToggleButton { Content = "Other…", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties(other, "Other", "Other");
        other.Click += (_, _) => vm.ToggleOther();
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

    static void FieldInvalid(Border frame, FieldViewModel f)
    {
        frame.BorderThickness = new Thickness(f.Invalid ? 2 : 0, 0, 0, 0);
        frame.BorderBrush = Ui.Brush("AbDanger");
        frame.Padding = new Thickness(f.Invalid ? 8 : 0, 2, 0, 2);
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
                        ToggleButton choice = f.Type == FieldType.Choice ? new RadioButton { GroupName = group } : new CheckBox();
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
                        ToggleButton other = f.Type == FieldType.Choice ? new RadioButton { GroupName = group, Content = "Other" } : new CheckBox { Content = "Other" };
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
                    yes.Click += (_, _) => { f.ToggleBool(true); Show(); };
                    no.Click += (_, _) => { f.ToggleBool(false); Show(); };
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

    Button SendButton()
    {
        var b = new Button { Content = vm.SendLabel, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationId(b, "SendAnswer");
        b.Click += (_, _) => vm.Send();
        return b;
    }

    // A text box that sends on Ctrl+Enter and holds the card list still while it has focus.
    TextBox Box(string placeholder, bool multiline)
    {
        var box = new TextBox { PlaceholderText = placeholder, TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MinHeight = multiline ? 64 : 0, MaxHeight = 240 };
        box.GotFocus += (_, _) => editing(true);
        box.LostFocus += (_, _) => editing(false);
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
