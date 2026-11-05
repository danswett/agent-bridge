using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.SignIn;

internal sealed record SignInTarget(string Store, AgentKind Agent, string? Account);
internal enum SignInOutcome { SignedIn, Expired, Cancelled, Failed, Refused }

// This is an adapter frame contract, not guessed Copilot console output. No production
// factory is supplied until the auth entrypoint, fixtures and side effects are approved.
internal interface IPrivateSignInProcessFactory
{
    // Recheck authorize at the actual launch, including after any factory queue.
    Task<IPrivateSignInProcess> StartAsync(SignInTarget target, Func<bool> authorize, CancellationToken ct);
}

internal interface IPrivateSignInProcess : IAsyncDisposable
{
    Stream Output { get; }
    Stream Error { get; }
    Task<int> ExitCode { get; }
    // Must nonblockingly terminate the contained process group and close its private pipes.
    void Abort();
}

internal sealed class PrivateSignInCode(string userCode, string verificationUri, DateTimeOffset expiresAt)
{
    public string UserCode { get; } = userCode;
    public string VerificationUri { get; } = verificationUri;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "Private sign-in instructions";

    public static bool Valid(string? code, string? uri) =>
        uri == "https://github.com/login/device" && code is { Length: 9 } && code[4] == '-'
        && code.Where((_, i) => i != 4).All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
}

internal sealed class PrivateSignInRunner(IPrivateSignInProcessFactory factory, TimeProvider time)
{
    readonly object gate = new();
    readonly HashSet<string> stores = new(StringComparer.Ordinal);
    public TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan CodeWait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Cleanup { get; init; } = TimeSpan.FromSeconds(1);
    const int LineBytes = 2048;
    const int TotalBytes = 32 * 1024;
    static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<SignInOutcome> RunAsync(SignInTarget target, Func<PrivateSignInCode, CancellationToken, Task<bool>> show,
        CancellationToken ct, Func<bool> authorize)
    {
        lock (gate) { if (stores.Count >= 8 || !stores.Add(target.Store)) { return SignInOutcome.Refused; } }
        var deadline = time.GetUtcNow() + Limit;
        using var budget = new CancellationTokenSource(Limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var token = linked.Token;
        IPrivateSignInProcess? process = null;
        Task? work = null;
        Task<IPrivateSignInProcess>? starting = null;
        var outcome = SignInOutcome.Failed;
        try
        {
            if (token.IsCancellationRequested || !authorize()) { return SignInOutcome.Refused; }
            starting = factory.StartAsync(target, authorize, token);
            process = await starting.WaitAsync(token);
            var read = ReadAsync(process, show, budget, deadline, token);
            var drain = DrainAsync(process.Error, token);
            var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            FailOnError(drain, failure);
            FailOnError(process.ExitCode, failure);
            work = Task.WhenAll(read, drain, process.ExitCode);
            if (await Task.WhenAny(read, failure.Task).WaitAsync(token) == failure.Task) { throw new InvalidDataException(); }
            var parsed = await read.WaitAsync(token);
            if (parsed != SignInOutcome.SignedIn) { outcome = parsed; }
            else
            {
                await work.WaitAsync(token);
                outcome = process.ExitCode.Result == 0 ? parsed : SignInOutcome.Failed;
            }
        }
        catch (OperationCanceledException) { outcome = ct.IsCancellationRequested ? SignInOutcome.Cancelled : SignInOutcome.Expired; }
        catch (Exception) { outcome = SignInOutcome.Refused; }
        finally
        {
            if (process is not null)
            {
                try { process.Abort(); } catch (Exception) { }
                if (work is not null) { Observe(work); }
                try
                {
                    var disposing = process.DisposeAsync().AsTask();
                    Observe(disposing);
                    await disposing.WaitAsync(Cleanup, time);
                    lock (gate) { stores.Remove(target.Store); }
                }
                catch (Exception) { outcome = SignInOutcome.Failed; }
            }
            else if (starting is not null)
            {
                _ = starting.ContinueWith(async t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion) { _ = t.Exception; lock (gate) { stores.Remove(target.Store); } return; }
                    try
                    {
                        t.Result.Abort();
                        var disposing = t.Result.DisposeAsync().AsTask();
                        Observe(disposing);
                        await disposing.WaitAsync(Cleanup, time);
                        lock (gate) { stores.Remove(target.Store); }
                    }
                    catch (Exception) { }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            }
            else { lock (gate) { stores.Remove(target.Store); } }
        }
        return outcome;
    }

    static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    static void FailOnError(Task task, TaskCompletionSource failure) => _ = task.ContinueWith(t =>
    {
        if (!t.IsCompletedSuccessfully) { _ = t.Exception; failure.TrySetResult(); }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    static async Task<int> ReadBoundedAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var reading = stream.ReadAsync(buffer, ct).AsTask();
        Observe(reading);
        return await reading.WaitAsync(ct);
    }

    async Task<SignInOutcome> ReadAsync(IPrivateSignInProcess process, Func<PrivateSignInCode, CancellationToken, Task<bool>> show,
        CancellationTokenSource lifetime, DateTimeOffset deadline, CancellationToken ct)
    {
        using var waiting = new CancellationTokenSource(CodeWait, time);
        using var beforeCode = CancellationTokenSource.CreateLinkedTokenSource(ct, waiting.Token);
        var seen = false;
        var completed = false;
        var result = SignInOutcome.Failed;
        await foreach (var line in LinesAsync(process.Output, () => seen ? ct : beforeCode.Token, ct))
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 3 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return SignInOutcome.Refused; }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject()) { if (!keys.Add(p.Name)) { return SignInOutcome.Refused; } }
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || completed) { return SignInOutcome.Refused; }
            if (kind.GetString() == "code")
            {
                if (seen || !keys.SetEquals(["kind", "userCode", "verificationUri", "expiresAt"])
                    || !root.TryGetProperty("userCode", out var code) || code.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("verificationUri", out var uri) || uri.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("expiresAt", out var expiry) || expiry.ValueKind != JsonValueKind.String
                    || !expiry.TryGetDateTimeOffset(out var at) || at <= time.GetUtcNow()
                    || !PrivateSignInCode.Valid(code.GetString(), uri.GetString())) { return SignInOutcome.Refused; }
                at = at < deadline ? at : deadline;
                lifetime.CancelAfter(at - time.GetUtcNow());
                seen = true;
                if (!await show(new PrivateSignInCode(code.GetString()!, uri.GetString()!, at), ct).WaitAsync(ct)) { return SignInOutcome.Refused; }
            }
            else if (kind.GetString() == "done")
            {
                if (!seen || !keys.SetEquals(["kind", "outcome", "persisted", "policyAllowed"])
                    || root.GetProperty("persisted").ValueKind != JsonValueKind.True
                    || root.GetProperty("policyAllowed").ValueKind != JsonValueKind.True
                    || root.GetProperty("outcome").GetString() != "signedIn") { return SignInOutcome.Refused; }
                completed = true;
                result = SignInOutcome.SignedIn;
            }
            else { return SignInOutcome.Refused; }
        }
        return result;
    }

    static async IAsyncEnumerable<string> LinesAsync(Stream stream, Func<CancellationToken> readToken,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[1];
        using var line = new MemoryStream();
        var total = 0;
        try
        {
            while (await ReadBoundedAsync(stream, buffer.AsMemory(), readToken()) != 0)
            {
                ct.ThrowIfCancellationRequested();
                if (++total > TotalBytes || line.Length >= LineBytes) { throw new InvalidDataException(); }
                if (buffer[0] == '\n')
                {
                    yield return Utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
                    Array.Clear(line.GetBuffer());
                    line.SetLength(0);
                }
                else { line.WriteByte(buffer[0]); }
            }
            if (line.Length > 0) { yield return Utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)); }
        }
        finally { Array.Clear(buffer); Array.Clear(line.GetBuffer()); }
    }

    static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var total = 0;
        int n;
        try
        {
            while ((n = await ReadBoundedAsync(stream, buffer.AsMemory(), ct)) != 0)
            {
                if ((total += n) > TotalBytes) { throw new InvalidDataException(); }
                Array.Clear(buffer);
            }
        }
        finally { Array.Clear(buffer); }
    }
}
