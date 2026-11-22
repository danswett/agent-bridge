using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.App.Core.Grants;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class GrantIssueViewFailureTests
{
    static readonly Lazy<Type> View = new(CompileView);

    [Theory]
    [InlineData(false, "Not issued", false)]
    [InlineData(true, "Unconfirmed", false)]
    [InlineData(true, "Unconfirmed", true)]
    public void Invalid_receiver_data_is_reported_without_escaping_the_UI_handler(bool confirming, string outcome, bool format)
    {
        var model = new GrantIssueViewModel(new ThrowingReceiver(confirming, format));
        var view = Activator.CreateInstance(View.Value)!;
        View.Value.GetProperty("ViewModel")!.SetValue(view, model);
        View.Value.GetMethod("Prepare")!.Invoke(view, ["{}"]);
        Assert.False((bool)View.Value.GetProperty("IsSubmitting")!.GetValue(view)!);
        if (confirming)
        {
            Assert.True(model.CanConfirm);
            View.Value.GetMethod("Confirm")!.Invoke(view, [model.Digest]);
        }
        Assert.Contains(outcome, (string)View.Value.GetProperty("Message")!.GetValue(view)!);
        Assert.Equal(outcome, (string)View.Value.GetProperty("Outcome")!.GetValue(view)!);
        Assert.False((bool)View.Value.GetProperty("IsSubmitting")!.GetValue(view)!);
        Assert.False(model.Active);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Rotation_notice_requires_a_matching_active_receipt(bool rotated, bool mismatch)
    {
        var model = new GrantIssueViewModel(new ThrowingReceiver(true, false, rotated, mismatch));
        var view = Activator.CreateInstance(View.Value)!;
        View.Value.GetProperty("ViewModel")!.SetValue(view, model);
        View.Value.GetMethod("Prepare")!.Invoke(view, ["{}"]);
        View.Value.GetMethod("Confirm")!.Invoke(view, [model.Digest]);
        Assert.Equal(rotated && !mismatch, !string.IsNullOrEmpty((string)View.Value.GetProperty("Notice")!.GetValue(view)!));
    }

    static Type CompileView()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "AgentBridge.App", "GrantIssueView.xaml.cs")))
        { root = root.Parent; }
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", "GrantIssueView.xaml.cs"));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(GrantIssueViewModel).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GrantViewProbe", [
            CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Stubs),
            CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Net.Http; global using System.Collections.Generic;")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("AgentBridge.App.GrantIssueView")!;
    }

    sealed class ThrowingReceiver(bool confirming, bool format, bool? rotated = null, bool mismatch = false) : IGrantIssueReceiver
    {
        Exception Error => format ? new FormatException("receiver-format") : new InvalidDataException("receiver-proof");
        GrantReceipt? receipt;
        public string Receiver => new('1', 32);
        public bool Online => true;
        public string? Problem => null;
        public Task<GrantPrepareResult> PrepareIssueAsync(JsonElement input, CancellationToken ct = default)
        {
            if (!confirming) { return Task.FromException<GrantPrepareResult>(Error); }
            var id = Guid.NewGuid();
            var document = JsonSerializer.Serialize(new { id, generation = 1, audience = new { device = Receiver, profile = "default" },
                caps = new { hourly = 1, daily = 2, live = 1 } });
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
            receipt = new(id, 1, Receiver, mismatch ? new('0', 64) : digest, rotated ?? false);
            return Task.FromResult(new GrantPrepareResult(new(id, 1, Receiver, "default", document, digest, new(1, 2, 1), "ask", []), null));
        }
        public Task<GrantIssueResult> ConfirmIssueAsync(Guid id, long generation, string digest, CancellationToken ct = default) =>
            rotated is null ? Task.FromException<GrantIssueResult>(Error) : Task.FromResult(new GrantIssueResult(receipt, null));
        public Task<GrantReadResult> ReadAsync(CancellationToken ct = default) => Task.FromResult(new GrantReadResult([], null));
        public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default) => throw new NotSupportedException();
    }

    const string Stubs = """
        namespace Microsoft.UI.Xaml
        {
            public class FrameworkElement { }
            public class RoutedEventArgs { }
        }
        namespace Microsoft.UI.Xaml.Controls
        {
            public class UserControl : Microsoft.UI.Xaml.FrameworkElement
            {
                public Queue DispatcherQueue { get; } = new();
            }
            public class Queue
            {
                public bool TryEnqueue(System.Action action) { action(); return true; }
            }
            public class TextBox { public string Text { get; set; } = ""; }
            public class TextBlock : Microsoft.UI.Xaml.FrameworkElement
            {
                public string Text { get; set; } = "";
            }
        }
        namespace Microsoft.UI.Xaml.Automation.Peers
        {
            public enum AutomationEvents { LiveRegionChanged }
            public class FrameworkElementAutomationPeer
            {
                public static FrameworkElementAutomationPeer CreatePeerForElement(Microsoft.UI.Xaml.FrameworkElement element) => new();
                public void RaiseAutomationEvent(AutomationEvents value) { }
            }
        }
        namespace AgentBridge.App
        {
            public static class AppLog { public static void Write(string message) { } }
            public sealed partial class GrantIssueView
            {
                readonly Microsoft.UI.Xaml.Controls.TextBox SourceDocument = new(), PersonDigest = new();
                readonly Microsoft.UI.Xaml.Controls.TextBlock InputProblem = new(), StatusLine = new(), RotationNotice = new();
                void InitializeComponent() { }
                static class Bindings { public static void Update() { } }
                public void Prepare(string source) { SourceDocument.Text = source; OnPrepare(this, new()); }
                public void Confirm(string digest) { PersonDigest.Text = digest; OnConfirm(this, new()); }
                public string Message => InputProblem.Text;
                public string Outcome => StatusLine.Text;
                public string Notice => RotationNotice.Text;
            }
        }
        """;
}
