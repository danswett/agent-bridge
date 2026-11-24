using System.Reflection;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

// Execute the pane's actual reply wiring and fade callback, without WinUI or a running host.
public sealed class ReplyMotionTests
{
    static readonly Lazy<MethodInfo> bind = new(Compile);

    [Fact]
    public async Task A_read_only_card_can_attach_and_detach_without_a_reply()
    {
        await using var host = Offline();
        var card = Card(host, () => []);
        try
        {
            Assert.Null(card.Reply);
            var run = Bind(card);
            run("attach", null);
            run("detach", null);
        }
        finally { card.Detach(); }
    }

    [Fact]
    public async Task Capability_changes_rewire_only_the_current_reply_and_detach_releases_it()
    {
        await using var host = Offline();
        IReadOnlyList<string> caps = [Capabilities.SessionsReply];
        var card = Card(host, () => caps);
        try
        {
            var run = Bind(card);
            run("attach", null);
            var old = Assert.IsType<ReplyViewModel>(card.Reply);
            Assert.True(run("refresh", old) > 0);
            caps = [Capabilities.SessionsRead];
            card.CapabilitiesChanged();
            Assert.Null(card.Reply);
            Assert.Equal(0, run("refresh", old));
            caps = [Capabilities.SessionsReply];
            card.CapabilitiesChanged();
            var next = Assert.IsType<ReplyViewModel>(card.Reply);
            Assert.NotSame(old, next);
            Assert.True(run("refresh", next) > 0);
            run("detach", null);
            Assert.Equal(0, run("refresh", next));
        }
        finally { card.Detach(); }
    }

    [Fact]
    public async Task A_fade_completion_tolerates_a_card_losing_its_reply_capability()
    {
        await using var host = Offline();
        IReadOnlyList<string> caps = [Capabilities.SessionsReply];
        var card = Card(host, () => caps);
        try
        {
            var run = Bind(card);
            var reply = Assert.IsType<ReplyViewModel>(card.Reply);
            typeof(ReplyViewModel).GetProperty(nameof(ReplyViewModel.StatusText))!.SetValue(reply, "Waiting");
            run("show", null);
            typeof(ReplyViewModel).GetProperty(nameof(ReplyViewModel.StatusText))!.SetValue(reply, "");
            run("show", null);
            caps = [Capabilities.SessionsRead];
            card.CapabilitiesChanged();
            Assert.Null(card.Reply);
            run("complete", null);
        }
        finally { card.Detach(); }
    }

    static HostConnection Offline() => new(_ => Task.FromException<LocalChannel>(new InvalidOperationException("No connection allowed")), new LockedDispatcher());
    static SessionCardViewModel Card(HostConnection host, Func<IReadOnlyList<string>> caps) =>
        new(Make.Session(), new CardContext { Deliveries = new(host, new LockedDispatcher()), Folds = new(null), Capabilities = caps });
    static Func<string, ReplyViewModel?, int> Bind(SessionCardViewModel card) => (Func<string, ReplyViewModel?, int>)bind.Value.Invoke(null, [card])!;

    static MethodInfo Compile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var methods = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", "SessionView.xaml.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToDictionary(m => m.Identifier.ValueText);
        string Prefix(string name, string stop) => string.Join("\n", methods[name].Body!.Statements.TakeWhile(s => !s.ToString().StartsWith(stop, StringComparison.Ordinal)));
        var replyChanged = string.Join("\n", methods["OnCardChanged"].Body!.Statements.OfType<IfStatementSyntax>()
            .Where(s => s.Condition.ToString().Contains("SessionCardViewModel.Reply", StringComparison.Ordinal)));
        var source = $$"""
            #nullable enable
            using System;
            using System.ComponentModel;
            using AgentBridge.App.Core;
            public enum Visibility { Visible, Collapsed }
            public sealed class Node {
                public static int Writes;
                Visibility visibility = Visibility.Collapsed;
                public Visibility Visibility { get => visibility; set { visibility = value; Writes++; } }
                public string Text = "";
            }
            public static class Motion {
                public static Action? Pending;
                public static void Hide(Node node) => node.Visibility = Visibility.Collapsed;
                public static void Settle(Node node) { }
                public static void Enter(Node node, string entry) { }
                public static void Leave(Node node, string entry, Action? completed = null) => Pending = completed;
            }
            public static class Ui { public static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed; }
            public static class ReceiptMarkView { public static void SetTone(Node node, Tone tone) { } }
            public sealed class Probe {
                bool attached;
                ReplyViewModel? watchedReply;
                SessionCardViewModel? Card;
                readonly ExceptionLineFade replyLine = new();
                readonly Node ReplyStatusGone = new(), ReplyStatusLine = new(), ReplyStatusSpace = new();
                public static Func<string, ReplyViewModel?, int> Bind(SessionCardViewModel card) {
                    var pane = new Probe { Card = card };
                    Motion.Pending = null;
                    return (operation, reply) => {
                        Node.Writes = 0;
                        switch (operation) {
                            case "attach": pane.Attach(); break;
                            case "detach": pane.Detach(); break;
                            case "refresh": reply!.Refresh(); break;
                            case "show": pane.ShowReplyStatus(); break;
                            case "complete": (Motion.Pending ?? throw new InvalidOperationException("No fade pending"))(); break;
                        }
                        return Node.Writes;
                    };
                }
                void Attach() { {{Prefix("Attach", "answers.Start")}} }
                void Detach() { {{Prefix("Detach", "guardTimer")}} }
                void OnCardChanged(object? sender, PropertyChangedEventArgs e) {
                    var all = string.IsNullOrEmpty(e.PropertyName);
                    {{replyChanged}}
                }
                void OnReplyWanted() { }
                void OnDelegatesChanged(object? sender, PropertyChangedEventArgs e) { }
                {{methods.GetValueOrDefault("WatchReply")}}
                {{methods["ShowReplyStatus"]}}
                {{methods["OnReplyStatusChanged"]}}
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SessionCardViewModel).Assembly.Location).Append(typeof(Session).Assembly.Location)
            .Distinct().Select(path => MetadataReference.CreateFromFile(path));
        using var output = new MemoryStream();
        var result = CSharpCompilation.Create("ReplyMotionProbe", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8602");
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Bind")!;
    }
}
