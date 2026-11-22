using System.Reflection;
using AgentBridge.App.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class ConversationResponseTests
{
    [Fact]
    public void The_first_response_loaded_after_a_detached_card_is_bound_and_replacements_keep_that_source() => RunProbe("Run");

    [Fact]
    public void A_retained_prefix_trim_leaves_the_lists_anchoring_while_a_wholesale_replacement_keeps_its_place() => RunProbe("Trim");

    [Theory, InlineData("numeric"), InlineData("row"), InlineData("end")]
    public void A_retained_trim_invalidates_queued_numeric_restores_but_keeps_row_and_end_anchors(string anchor) => RunProbe("QueuedUpdate", anchor);

    [Fact]
    public void A_plain_append_preserves_the_numeric_restore_queued_by_a_wholesale_replacement() => RunProbe("QueuedUpdate", "append");

    static void RunProbe(string run, string? anchor = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var method = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", "SessionView.Conversation.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "ShowResponse");
        var preserve = run == "QueuedUpdate"
            ? CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", "SessionView.History.cs")))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "KeepPlace").ToString()
            : "void KeepPlace(bool atEnd, double offset, (string Id, double Top)? reading = null) { kept++; }";
        var source = $$"""
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using AgentBridge.App.Core;
            using Microsoft.UI.Xaml;
            namespace Microsoft.UI.Xaml { public class FrameworkElement { public void UpdateLayout() { } } }
            namespace Microsoft.UI.Dispatching { public enum DispatcherQueuePriority { Low } }
            public sealed class Node { public object? ItemsSource; public double Height; }
            public sealed class CardData { public bool HasVoice => true; public string Response = "First response."; }
            public sealed class Queue {
                readonly Queue<Action> pending = new();
                public bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority priority, Action action) { pending.Enqueue(action); return true; }
                public void Drain() { while (pending.TryDequeue(out var action)) action(); }
            }
            public sealed class Follow { public bool AtEnd; public bool Held; }
            public sealed class Scroll { public double VerticalOffset; public double ScrollableHeight = 200; public List<double> Targets = []; }
            public sealed record Row(string Id);
            public sealed class Rows { public object GetOrCreateElement(int index) => new FrameworkElement(); }
            public sealed class Probe {
                ConversationItems blocks = [];
                CardData? blocksOf;
                string? blocksFrom;
                readonly CardData Card = new();
                readonly Node ConversationList = new(), FooterLead = new();
                readonly Queue DispatcherQueue = new();
                readonly Follow follow = new();
                Scroll? ConversationScroll = null;
                bool pinEnd;
                ScrollAnchor? anchor = null;
                bool atEnd, panning, scrolling;
                double? anchoring, keyTarget;
                string shownHistory = "probe";
                int pinReleases, responseAnchors;
                object? restoreReading, navigationPending;
                readonly HashSet<uint> scrollbarPointers = [];
                readonly FrameworkElement HistoryColumn = new();
                readonly List<Row> historyShown = [];
                readonly Rows HistoryRows = new();
                (string Id, double Top)? reading;
                bool HistoryAtEnd() => atEnd;
                (string Id, double Top)? ReadingHistory() => reading;
                double Top(FrameworkElement row, Scroll scroll) => 0;
                void Anchoring(Scroll scroll, double target) { anchoring = target; scroll.Targets.Add(target); }
                void TakeAnchor() { }
                int kept;
                {{preserve}}
                {{method}}
                public static void Run() {
                    var pane = new Probe();
                    pane.ShowResponse(reset: false);
                    var bound = pane.ConversationList.ItemsSource;
                    if (!ReferenceEquals(bound, pane.blocks) || pane.blocks.Count == 0)
                        throw new InvalidOperationException("First detached-card response is not bound.");
                    pane.Card.Response = "Wholly different replacement.";
                    pane.ShowResponse(reset: false);
                    if (!ReferenceEquals(bound, pane.ConversationList.ItemsSource) || !ReferenceEquals(bound, pane.blocks))
                        throw new InvalidOperationException("Replacement changed the bound source.");
                }
                public static void Trim() {
                    var pane = new Probe();
                    pane.Card.Response = "Leading paragraph.\n\nSurviving paragraph.";
                    pane.ShowResponse(reset: false);
                    (pane.kept, pane.Card.Response) = (0, "Surviving paragraph.");
                    pane.ShowResponse(reset: false);
                    if (pane.kept != 0) throw new InvalidOperationException("A retained trim restored a stale offset.");
                    pane.Card.Response = "Wholly different replacement.";
                    pane.ShowResponse(reset: false);
                    if (pane.kept != 1) throw new InvalidOperationException("A wholesale replacement no longer keeps its place.");
                }
                public static void QueuedUpdate(string anchor) {
                    var pane = new Probe();
                    pane.ShowResponse(reset: true);
                    pane.ConversationScroll = new Scroll { VerticalOffset = 60 };
                    pane.Card.Response = "Leading paragraph.\n\nSurviving paragraph.";
                    pane.atEnd = anchor == "end";
                    if (anchor == "row") { pane.reading = ("row", 10); pane.historyShown.Add(new("row")); }
                    pane.ShowResponse(reset: false);
                    pane.atEnd = false; pane.reading = null;
                    pane.ConversationScroll.VerticalOffset = 50;
                    pane.Card.Response = anchor == "append" ? "Leading paragraph.\n\nSurviving paragraph.\n\nAppended paragraph." : "Surviving paragraph.";
                    pane.ShowResponse(reset: false);
                    pane.DispatcherQueue.Drain();
                    var expected = anchor == "numeric" ? Array.Empty<double>() : new[] { anchor == "append" ? 60.0 : anchor == "row" ? 40.0 : 200 };
                    if (!pane.ConversationScroll.Targets.SequenceEqual(expected))
                        throw new InvalidOperationException($"Queued {anchor} restore targets: {string.Join(",", pane.ConversationScroll.Targets)}.");
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(ConversationItems).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        using var output = new MemoryStream();
        var result = CSharpCompilation.Create("ConversationResponseProbe", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod(run)!.Invoke(null, anchor is null ? null : [anchor]);
    }
}
