using System.Collections.Specialized;
using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace AgentBridge.App;

// A sign-in banner's line while it names its sessions (ui.md §6.13, owner decision 6675817525):
// "2 sessions affected: A, B", wrapping as prose, each title a link that opens its session, and no
// line of titles of its own. Drawn again only when the words or the sessions change, so a link with
// focus keeps it as the banner redraws.
public sealed partial class SignInSessionLinks : UserControl
{
    public static readonly DependencyProperty BannerProperty = DependencyProperty.Register(
        nameof(Banner), typeof(SignInBannerViewModel), typeof(SignInSessionLinks), new PropertyMetadata(null, (d, _) => ((SignInSessionLinks)d).Watch()));

    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap };
    SignInBannerViewModel? watched;
    string drawn = "";

    public SignInSessionLinks()
    {
        IsTabStop = false;
        Content = text;
        AutomationProperties.SetAutomationId(text, "SignInAffected");
        Loaded += (_, _) => Watch();
        Unloaded += (_, _) => Unwatch();
    }

    public SignInBannerViewModel? Banner
    {
        get => (SignInBannerViewModel?)GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    void Watch()
    {
        Unwatch();
        if (Banner is not { } b || !IsLoaded) { Draw(); return; }
        watched = b;
        b.PropertyChanged += OnChanged;
        b.Sessions.CollectionChanged += OnSessions;
        Draw();
    }

    void Unwatch()
    {
        if (watched is null) { return; }
        watched.PropertyChanged -= OnChanged;
        watched.Sessions.CollectionChanged -= OnSessions;
        watched = null;
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e) => Draw();
    void OnSessions(object? sender, NotifyCollectionChangedEventArgs e) => Draw();

    void Draw()
    {
        var b = Banner;
        var key = b is null ? "" : string.Join("\n", b.Sessions.Select(s => $"{s.Id}\n{s.Title}").Prepend(b.Affected));
        if (key == drawn) { return; }
        drawn = key;
        text.Inlines.Clear();
        if (b is null) { return; }
        text.Inlines.Add(new Run { Text = b.Affected });
        for (var i = 0; i < b.Sessions.Count; i++)
        {
            if (i > 0) { text.Inlines.Add(new Run { Text = ", " }); }
            // Each keeps only where it goes, never the banner, whose flow may hold a code.
            var session = b.Sessions[i];
            var link = new Hyperlink { UnderlineStyle = UnderlineStyle.Single };
            link.Inlines.Add(new Run { Text = session.Title });
            AutomationProperties.SetAutomationId(link, "SignInSession");
            link.Click += (_, _) => session.Press();
            text.Inlines.Add(link);
        }
    }
}
