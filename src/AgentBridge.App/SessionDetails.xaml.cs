using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed partial class SessionDetails : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(object), typeof(SessionDetails), new PropertyMetadata(null, (d, _) => ((SessionDetails)d).Changed()));

    public SessionDetails()
    {
        InitializeComponent();
        Loaded += (_, _) => Bindings.Update();
        // Let go of the card off screen, so a closed flyout or a hidden inspector keeps nothing alive.
        Unloaded += (_, _) => { if (!IsLoaded) { Bindings.StopTracking(); } };
    }

    public SessionCardViewModel? Card
    {
        get => GetValue(CardProperty) as SessionCardViewModel;
        set => SetValue(CardProperty, value);
    }

    // A row of Details is a Row-role line: mono values at the role's inline mono size (ui.md §4.2).
    public static Style ValueStyle(bool mono) => (Style)Application.Current.Resources[mono ? "InlineMonoRow" : "AbTypeRow"];
    public static string ValueId(string label) => $"Detail:{label}";

    // A value wraps rather than being cut off (ui.md §6.2): the Message sentence says what is wrong,
    // and a status or a count that ended in an ellipsis lost words. Only a folder shortens, by its
    // middle (OnValueSized), so it stays on one line.
    public static TextWrapping Wrap(bool path) => path ? TextWrapping.NoWrap : TextWrapping.Wrap;

    void Changed()
    {
        if (IsLoaded) { Bindings.Update(); }
    }

    // A path cut in the middle to the width it has, keeping its start and the end people tell
    // paths apart by (#214), rather than its end cut off.
    void OnValueSized(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock { DataContext: DetailRow { Path: true } row } block) { FitMiddle(block, row.Value, e.NewSize.Width); }
    }

    static void FitMiddle(TextBlock block, string full, double width)
    {
        var probe = new TextBlock { Style = block.Style, FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight };
        double Width(string text)
        {
            probe.Text = text;
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }
        // A pixel spare: measured and laid-out widths round differently.
        var room = width - 1;
        if (width <= 0 || Width(full) <= room) { block.Text = full; return; }
        int low = 5, high = full.Length - 1, best = 5;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Width(Words.Middle(full, mid)) <= room) { best = mid; low = mid + 1; }
            else { high = mid - 1; }
        }
        block.Text = Words.Middle(full, best);
    }
    void OnEnd(object sender, RoutedEventArgs e) => Card?.End?.Press();
    void OnResendEnd(object sender, RoutedEventArgs e) => Card?.End?.Resend();
}