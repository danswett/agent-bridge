using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class MeasuredScrollDriverRaceTests
{
    [Theory, InlineData(4, 299, false), InlineData(8, 8.5, false), InlineData(4, 4, true), InlineData(8, 8, true), InlineData(299, 299, true)]
    public async Task Completed_frames_only_qualify_the_current_measured_destination(double gap, double finalGap, bool expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var helper = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", "SelfTest.HistoryScrollPreserve.cs")))
            .GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(m => m.Identifier.ValueText == "ReachMeasuredBottom");
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            static class CompositionTarget {
                public static event EventHandler<object> Rendering;
                public static void Frame() => Rendering?.Invoke(null, new object());
            }
            sealed class Scroll { public double VerticalOffset, ScrollableHeight = 52306; }
            sealed class View { public bool ChangeConversationView(Scroll scroll, double to) { scroll.VerticalOffset = to; return true; } }
            public sealed class Probe {
                readonly Scroll scroll = new Scroll();
                readonly View view = new View();
                readonly double finalGap;
                Probe(double gap, double finalGap) { scroll.VerticalOffset = scroll.ScrollableHeight - gap; this.finalGap = finalGap; }
                {{helper}}
                Task<bool> Until(Func<bool> ready, TimeSpan timeout) {
                    CompositionTarget.Frame(); CompositionTarget.Frame();
                    scroll.ScrollableHeight = 52147;
                    scroll.VerticalOffset = scroll.ScrollableHeight - finalGap;
                    return Task.FromResult(ready());
                }
                public static Task<bool> Run(double gap, double finalGap) => new Probe(gap, finalGap).ReachMeasuredBottom(gap);
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        using var output = new MemoryStream();
        var result = CSharpCompilation.Create("MeasuredScrollDriverProbe", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var reached = await (Task<bool>)Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!.Invoke(null, [gap, finalGap])!;
        Assert.Equal(expected, reached);
    }
}
