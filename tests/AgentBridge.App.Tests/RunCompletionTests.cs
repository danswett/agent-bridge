using System.Reflection;
using AgentBridge.App.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AgentBridge.App.Tests;

// Execute the actual platform method bodies against offline window/installer contracts.
public sealed class RunCompletionTests : IDisposable
{
    static readonly Lazy<MethodInfo> run = new(Compile);
    readonly string folder = Directory.CreateTempSubdirectory("run-completion-").FullName;
    RunMarker? marker;

    public void Dispose()
    {
        (marker as object as IDisposable)?.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tray_quit_completes_the_marker_after_successful_close_in_either_dispatch_branch(bool queued) =>
        Assert.True(Run("Quit", queued));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_handoff_completes_the_marker_before_exit_even_if_recording_the_handoff_fails(bool callbackThrows) =>
        Assert.True(Run("Update", callbackThrows));

    [Theory]
    [InlineData("Quit")]
    [InlineData("Update")]
    public void Failed_close_or_update_handoff_keeps_the_run_unclean(string operation)
    {
        Assert.Throws<TargetInvocationException>(() => Run(operation, false, fail: true));
        Assert.Single(Directory.GetFiles(folder, "run-marker*.json"));
    }

    [Theory]
    [InlineData("Quit")]
    [InlineData("Update")]
    public void Successful_quit_or_update_preserves_existing_crash_evidence(string operation) =>
        Assert.False(Run(operation, false, crash: true));

    bool Run(string operation, bool flag, bool fail = false, bool crash = false)
    {
        marker = RunMarker.Start(folder, "1");
        if (crash) { marker.RecordCrash(new InvalidOperationException()); }
        File.WriteAllText(Path.Combine(folder, "Update.fake"), "");
        return (bool)run.Value.Invoke(null, [operation, marker, folder, flag, fail])!;
    }

    static MethodInfo Compile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        string Methods(string file, params string[] names) =>
            string.Join("\n", CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", file)))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => names.Contains(m.Identifier.ValueText)));
        var source = $$"""
            using System;
            using System.IO;
            using System.Threading.Tasks;
            using AgentBridge.App.Core;
            namespace Microsoft.UI.Dispatching { public enum DispatcherQueuePriority { Low } }
            namespace AgentBridge.App {
            public static class Harness {
                public static bool Run(string operation, RunMarker marker, string folder, bool flag, bool fail) {
                    Program.Marker = marker; State.Folder = folder; State.Clean = false;
                    if (operation == "Quit") App.Run(flag, fail); else Updater.Run(flag, fail);
                    return State.Clean;
                }
            }
            static class State {
                public static string Folder = ""; public static bool Clean;
                public static void Exit() => Clean = Directory.GetFiles(Folder, "run-marker*.json").Length == 0;
            }
            static class Program {
                public static RunMarker Marker = null!;
                {{Methods("Program.cs", "CompleteRun")}}
            }
            static class AppLog { public static void Write(string text) { } }
            class Fake : IDisposable, IAsyncDisposable {
                public bool Queued, Fail; public string SelfTest => null;
                public Fake AppWindow => this; public Fake DispatcherQueue => this; public Fake Order => this;
                public void Stop() { } public void Flush() { } public void SavePlacement() { }
                public void Quit(TimeSpan wait, Action<string> log) { } public void Unregister(object state) { }
                public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask;
                public void Hide() { } public void Close() { if (Fail) throw new InvalidOperationException(); }
                public bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority priority, Action action) {
                    if (Queued) action(); return Queued;
                }
            }
            class App {
                bool quitting;
                Fake timer, updateTimer, main, manager, showWait, connection, demo, fleet;
                Fake tray = new(), options = new(), window;
                public static void Run(bool queued, bool fail) => new App { window = new Fake { Queued = queued, Fail = fail } }.Quit();
                void Exit() => State.Exit();
                {{Methods("App.xaml.cs", "Quit", "CompleteRun")}}
            }
            class Asset { public string Version => "2"; }
            class Release { public Asset TargetFullRelease => new(); }
            class Handoff {
                public bool Fail;
                public object Prepare(string file, string version, string source) => new();
                public IDisposable Launch(object copy, string root, string packages, string[] args, Action<string[]> start) {
                    if (Fail) throw new InvalidOperationException(); start(args); return null;
                }
            }
            class Process {
                public void StartProcess(string exe, string[] args, string dir, bool wait) { }
                public void Exit(int code) => State.Exit();
            }
            class VelopackLocator {
                public static bool IsCurrentSet => true; public static VelopackLocator Current => new();
                public string UpdateExePath => Path.Combine(State.Folder, "Update.fake");
                public string RootAppDir => State.Folder; public string PackagesDir => State.Folder;
                public Process Process => new();
            }
            class Updater {
                string Ready => "2"; Release pending = new(); Asset local; string source = "", profile = "";
                Handoff Handoff = new(); IDisposable handedOver; Action<string> log = AppLog.Write;
                void Manager() { } string PackageFile(Asset asset) => Path.Combine(State.Folder, "Update.fake");
                public static void Run(bool callbackThrows, bool fail) =>
                    new Updater { Handoff = new Handoff { Fail = fail } }.ApplyAndRestart("2",
                        () => { if (callbackThrows) throw new InvalidOperationException(); });
                {{Methods("Platform.cs", "ApplyAndRestart")}}
            }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RunMarker).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        using var output = new MemoryStream();
        var result = CSharpCompilation.Create("RunCompletionProbe", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(output.ToArray()).GetType("AgentBridge.App.Harness")!.GetMethod("Run")!;
    }
}
