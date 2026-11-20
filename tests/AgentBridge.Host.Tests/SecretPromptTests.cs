using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Diagnostics;
using AgentBridge.HomeAssistant;
using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class SecretPromptTests : IDisposable
{
    internal const string Token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJmYWtlLW5vdC1hLWNyZWRlbnRpYWwiLCJpYXQiOjEsImV4cCI6Mn0.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // Expired fixture, dummy signature. gitleaks:allow
    readonly string root = Directory.CreateTempSubdirectory("ab-secret-").FullName;
    string Folder => Path.Combine(root, "fixture");
    static readonly Uri Url = new("http://ha.test:8123");
    public SecretPromptTests()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, HostSettings.FileName), """{"homeAssistant":{"baseUrl":"http://ha.test:8123"}}""");
    }
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("enter")]
    [InlineData("backspace")]
    [InlineData("pipe")]
    public void Empty_input_explains_how_to_paste_without_checking_JWT_shape_or_changing_credentials(string input)
    {
        HaTokenStore.Save(Folder, Token);
        var record = HaAuthGuard.FileFor(Folder);
        File.WriteAllText(record, "unchanged refusal record");
        var keys = new Queue<ConsoleKeyInfo>();
        if (input == "backspace")
        {
            keys.Enqueue(new('x', ConsoleKey.X, false, false, false));
            keys.Enqueue(new('\b', ConsoleKey.Backspace, false, false, false));
        }
        keys.Enqueue(new('\r', ConsoleKey.Enter, false, false, false));
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(1, HaCommands.Run(["ha", "token", "set"], Folder,
            () => input == "pipe" ? "" : HaCommands.ReadSecret(error, () => keys.Dequeue()), output, error, false, null,
            confirm: () => throw new InvalidOperationException("Empty input must not ask for consent")));

        Assert.EndsWith("Nothing was entered (0 characters). Paste the token and press Enter, or pipe it: Get-Clipboard | agent-bridge-host ha token set" + Environment.NewLine,
            error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("JWT", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.Equal(Token, HaTokenStore.Load(Folder));
        Assert.Equal("unchanged refusal record", File.ReadAllText(record));
    }

    [Theory]
    [InlineData("not-a-token", "prefix")]
    [InlineData("eyJ.a.b", "length")]
    [InlineData(Token + " ", "whitespace")]
    [InlineData(Token + "\t", "whitespace")]
    [InlineData(Token + ".", "segments")]
    [InlineData("eyJub3QiOiJoYSJ9.eyJpc3MiOiJmYWtlLW5vdC1hLWNyZWRlbnRpYWwiLCJpYXQiOjEsImV4cCI6Mn0.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "header")]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.________________________.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "JSON")]
    public void Malformed_input_is_refused_locally_without_storing_or_repeating_it(string token, string reason)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exception = Record.Exception(() =>
            Assert.Equal(1, HaCommands.Run(["ha", "token", "set"], Folder, () => token, output, error, false, null)));
        Assert.Null(exception);
        Assert.Contains(reason, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(HaTokenStore.Exists(Folder));
        Assert.DoesNotContain(token, output + error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_shows_length_shape_and_only_the_eight_character_hash_before_defaulting_to_no()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = HaCommands.Run(["ha", "token", "set"], Folder, () => Token, output, error, false, null);
        var text = output + error.ToString();
        Assert.Contains($"Received {Token.Length} characters (masked)", text, StringComparison.Ordinal);
        Assert.Contains("whitespace: no", text, StringComparison.Ordinal);
        Assert.Contains("sha256: " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)))[..8], text, StringComparison.Ordinal);
        Assert.Contains("3 of 3", text, StringComparison.Ordinal);
        Assert.Contains("yes", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, code);
        Assert.False(HaTokenStore.Exists(Folder));
        foreach (var segment in Token.Split('.')) { Assert.DoesNotContain(segment, text, StringComparison.Ordinal); }
    }

    [Fact]
    public void An_unconfigured_endpoint_never_authorizes_a_future_unknown_destination()
    {
        File.Delete(Path.Combine(Folder, HostSettings.FileName));
        var error = new StringWriter();
        Assert.Equal(1, HaCommands.Run(["ha", "token", "set"], Folder, () => Token, new StringWriter(), error, false, null,
            confirm: () => throw new InvalidOperationException("Consent must be bound to a configured endpoint")));
        Assert.Contains("baseUrl", error.ToString(), StringComparison.Ordinal);
        Assert.False(HaTokenStore.Exists(Folder));
    }

    [Fact]
    public void Typed_and_pasted_characters_echo_only_masks_and_backspace_removes_a_mask()
    {
        var keys = new Queue<ConsoleKeyInfo>(("x" + Token).Select(c => new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false)));
        keys.Enqueue(new('\0', ConsoleKey.Backspace, false, false, false));
        keys.Enqueue(new('\r', ConsoleKey.Enter, false, false, false));
        var output = new StringWriter();
        Assert.Equal(("x" + Token)[..^1], HaCommands.ReadSecret(output, () => keys.Dequeue()));
        Assert.Contains(new string('*', Token.Length + 1) + "\b \b", output.ToString(), StringComparison.Ordinal);
        foreach (var segment in Token.Split('.')) { Assert.DoesNotContain(segment, output.ToString(), StringComparison.Ordinal); }
    }

    [Fact]
    public async Task PowerShell_7_uses_the_host_prompt_without_exposing_the_pasted_fixture()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var script = $$"""
            [void][Reflection.Assembly]::LoadFrom('{{typeof(HaCommands).Assembly.Location.Replace("'", "''")}}')
            $q = [Collections.Generic.Queue[ConsoleKeyInfo]]::new()
            '{{Token}}'.ToCharArray() | ForEach-Object { $q.Enqueue([ConsoleKeyInfo]::new($_, [ConsoleKey]::A, $false, $false, $false)) }
            $q.Enqueue([ConsoleKeyInfo]::new("`r", [ConsoleKey]::Enter, $false, $false, $false))
            $w = [IO.StringWriter]::new()
            $value = [AgentBridge.Host.HaCommands]::ReadSecret($w, [Func[ConsoleKeyInfo]]{ $q.Dequeue() })
            if ($value.Length -ne {{Token.Length}}) { throw 'Fixture input changed' }
            $w.ToString()
            """;
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await error);
        Assert.Contains(new string('*', Token.Length), await output, StringComparison.Ordinal);
        foreach (var segment in Token.Split('.')) { Assert.DoesNotContain(segment, await output, StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no")]
    [InlineData(Token)]
    public void Missing_or_negative_consent_preserves_the_token_and_refusal_record(string? answer)
    {
        HaTokenStore.Save(Folder, Token);
        var record = HaAuthGuard.FileFor(Folder);
        File.WriteAllText(record, "unreadable record");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, HaCommands.Run(["ha", "token", "set"], Folder, () => Token, output, error, false, null, confirm: () => answer));
        Assert.Equal(Token, HaTokenStore.Load(Folder));
        Assert.Contains("nothing changed", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, output + error.ToString(), StringComparison.Ordinal);
        Assert.Equal("unreadable record", File.ReadAllText(record));
        Assert.Contains("unknown of 3", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Consent_sees_remaining_attempts_before_rearming_and_one_refusal_stops_even_after_restart(int rejections)
    {
        var record = HaAuthGuard.FileFor(Folder);
        var logs = new StringWriter();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var guard = new HaAuthGuard(record, time, logs.WriteLine);
        for (var i = 0; i < rejections; i++)
        {
            using var first = await guard.EnterAsync(guard.Credential(Url, Token));
            first.Answered(HttpStatusCode.Unauthorized);
            time.Advance(TimeSpan.FromMinutes(20));
        }
        var before = File.ReadAllText(record);
        var output = new StringWriter();
        var error = new StringWriter();
        var confirmed = false;
        Assert.Equal(0, HaCommands.Run(["ha", "token", "set"], Folder, () => Token, output, error, false, null, confirm: () =>
        {
            confirmed = true;
            Assert.Contains($"{3 - rejections} of 3", output + error.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(record));
            Assert.False(HaTokenStore.Exists(Folder));
            return "yes";
        }));
        Assert.True(confirmed);
        var restarted = new HaAuthGuard(record, time, logs.WriteLine);
        var credential = restarted.Credential(Url, Token);
        using (var attempt = await restarted.EnterAsync(credential)) { attempt.Answered(HttpStatusCode.Unauthorized); }
        time.Advance(TimeSpan.FromDays(1));
        restarted = new HaAuthGuard(record, time, logs.WriteLine);
        var exception = await Record.ExceptionAsync(async () => { using var held = await restarted.EnterAsync(restarted.Credential(Url, Token)); });
        Assert.False(Assert.IsType<HomeAssistantAuthException>(exception).Sent);
        var diagnostics = output + error.ToString() + logs + exception;
        foreach (var segment in Token.Split('.')) { Assert.DoesNotContain(segment, diagnostics, StringComparison.Ordinal); }
        Assert.DoesNotContain(Token, File.ReadAllText(record), StringComparison.Ordinal);
    }
}
