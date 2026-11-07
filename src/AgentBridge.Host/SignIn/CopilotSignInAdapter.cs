using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.SignIn;

// Copilot's production sign-in adapter (#972): the installed CLI's own `copilot login --device-code`,
// unpatched, under Engineering's conditional approval (#800 6648319937): the machine opens a
// browser and copies the code; --log-level none, the CLI's supported option, keeps its debug log
// off; its keychain persistence and organization-policy checks run as they are (no plaintext
// consent is ever given: standard input is closed); and both output streams stay in this process.
// Standard output is translated into the runner's adapter frames here and nowhere else; standard
// error is handed to the runner, which drains and discards it. Neither reaches a log.
internal sealed class CopilotSignInAdapter(Func<string?> executable, string copilotHome, TimeProvider? time = null,
    Func<ProcessStartInfo, Process?>? start = null) : IPrivateSignInProcessFactory
{
    // Never through a shell: a .cmd or .bat shim would need cmd.exe to run it.
    public static bool Runnable(string? exe) => exe is not null && Path.IsPathFullyQualified(exe)
        && (!OperatingSystem.IsWindows() || exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    public Task<IPrivateSignInProcess> StartAsync(SignInTarget target, Func<bool> authorize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var home = Path.GetFullPath(copilotHome);
        if (target.Agent != AgentKind.Copilot || target.Store != home) { throw new InvalidOperationException("not this host's Copilot credential store"); }
        if (executable() is not { } exe || !Runnable(exe)) { throw new InvalidOperationException("Copilot's executable cannot be started for sign-in"); }
        var info = CopilotDeviceCommand.Create(exe, home);
        if (!authorize()) { throw new InvalidOperationException("sign-in launch is no longer authorized"); }
        var process = (start ?? ProcessLaunch.Start)(info) ?? throw new InvalidOperationException("Copilot's sign-in could not be started");
        return Task.FromResult<IPrivateSignInProcess>(CopilotLoginProcess.Contain(process, time ?? TimeProvider.System));
    }
}

// One run of the CLI's login: its standard output read only by Translate, its error stream only by
// the runner's drain, the process and anything it starts ended together.
internal sealed class CopilotLoginProcess : IPrivateSignInProcess
{
    // The CLI prints no expiry for its code. GitHub's device codes last 15 minutes by default, so
    // the host's own 10-minute flow deadline, which the runner caps every code at anyway, never
    // outlives the code it shows.
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    const int LineBytes = 2048;
    const int TotalBytes = 32 * 1024;
    const string Visit = "To authenticate, visit ";
    const string Enter = " and enter code ";
    static readonly UTF8Encoding Utf8 = new(false, true);

    readonly FrameStream frames = new();
    readonly CancellationTokenSource stop = new();
    readonly Action abort;
    readonly Func<ValueTask> release;
    readonly Task translation;
    int disposed;

    internal CopilotLoginProcess(Stream output, Stream error, Task<int> exit, Action abort, Func<ValueTask> release, TimeProvider time)
    {
        Error = error;
        ExitCode = exit;
        this.abort = abort;
        this.release = release;
        translation = Task.Run(async () =>
        {
            try { await TranslateAsync(output, frames, time, stop.Token); }
            catch (Exception) { Abort(); }
        });
    }

    public Stream Output => frames;
    public Stream Error { get; }
    public Task<int> ExitCode { get; }

    public void Abort()
    {
        frames.Fail();
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        Abort();
        try { await translation; } catch (Exception) { }
        frames.Clear();
        stop.Dispose();
        await release();
    }

    internal static CopilotLoginProcess Contain(Process process, TimeProvider time)
    {
        Job? job = null;
        try
        {
            // Assigned before the CLI's first network round trip, which comes before anything it
            // starts (the browser); Abort ends the whole tree even without it. A browser the login
            // started fresh is in the job too, and is closed with it when the flow ends.
            if (OperatingSystem.IsWindows()) { job = Job.Assign(process); }
            // Never answers the CLI's plaintext-storage question: a closed, non-TTY input is a no.
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            job?.Dispose();
            process.Dispose();
            throw;
        }
        var exit = ExitAsync(process);
        return new CopilotLoginProcess(process.StandardOutput.BaseStream, process.StandardError.BaseStream, exit,
            () =>
            {
                if (job is null || !job.Terminate()) { try { process.Kill(entireProcessTree: true); } catch (Exception) { } }
            },
            () =>
            {
                job?.Dispose();
                process.Dispose();
                return ValueTask.CompletedTask;
            }, time);
    }

    static async Task<int> ExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    // The CLI's own words, as 1.0.105's login prints them, in order and nothing else:
    //   To authenticate, visit <verificationUri> and enter code <userCode>
    //   Waiting for authorization...
    //   Signed in successfully as <account>.
    // The last only once its credential is stored and the organization's policy allows it, before
    // exit code 0. Any other line, or one out of order, ends the flow without saying what it was:
    // a wording this does not know is never guessed at.
    internal static async Task TranslateAsync(Stream raw, FrameStream frames, TimeProvider time, CancellationToken ct)
    {
        var state = 0;
        try
        {
            await foreach (var line in LinesAsync(raw, ct))
            {
                if (state == 0 && Code(line) is { } code)
                {
                    frames.Write(new { kind = "code", userCode = code.UserCode, verificationUri = code.Uri, expiresAt = time.GetUtcNow() + CodeLifetime });
                    state = 1;
                }
                else if (state == 1 && line == "Waiting for authorization...") { state = 2; }
                else if (state == 2 && line.StartsWith("Signed in successfully as ", StringComparison.Ordinal) && line.EndsWith('.'))
                {
                    frames.Write(new { kind = "done", outcome = "signedIn", persisted = true, policyAllowed = true });
                    state = 3;
                }
                else { throw new InvalidDataException(); }
            }
            frames.Complete();
        }
        catch (Exception)
        {
            frames.Fail();
            throw new InvalidDataException();
        }
    }

    static (string UserCode, string Uri)? Code(string line)
    {
        if (!line.StartsWith(Visit, StringComparison.Ordinal)) { return null; }
        var at = line.IndexOf(Enter, Visit.Length, StringComparison.Ordinal);
        if (at < 0) { return null; }
        var uri = line[Visit.Length..at];
        var code = line[(at + Enter.Length)..];
        return PrivateSignInCode.Valid(code, uri) ? (code, uri) : null;
    }

    static async IAsyncEnumerable<string> LinesAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[512];
        var line = new byte[LineBytes];
        var length = 0;
        var total = 0;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if ((total += n) > TotalBytes) { throw new InvalidDataException(); }
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != '\n')
                    {
                        if (length == LineBytes) { throw new InvalidDataException(); }
                        line[length++] = buffer[i];
                        continue;
                    }
                    var end = length > 0 && line[length - 1] == '\r' ? length - 1 : length;
                    var text = Utf8.GetString(line, 0, end);
                    Array.Clear(line);
                    length = 0;
                    yield return text;
                }
                Array.Clear(buffer);
            }
            if (length > 0) { yield return Utf8.GetString(line, 0, length); }
        }
        finally { Array.Clear(buffer); Array.Clear(line); }
    }

    // A Windows job that ends everything in it when it is closed or terminated.
    sealed class Job : SafeHandleZeroOrMinusOneIsInvalid
    {
        Job() : base(true) { }

        public static Job Assign(Process process)
        {
            var job = CreateJobObjectW(0, null);
            if (job.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())
                || !AssignProcessToJobObject(job, process.SafeHandle))
            {
                var e = new Win32Exception(Marshal.GetLastWin32Error());
                job.Dispose();
                throw e;
            }
            return job;
        }

        public bool Terminate() => !IsClosed && TerminateJobObject(this, 1);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Job CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(Job job, int informationClass, ref JobLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(Job job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateJobObject(Job job, uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}

// The adapter frames, one JSON line each, read by the runner as a stream. Failing it makes the
// runner's next read throw, so a flow the translation gave up on is refused, never left waiting.
internal sealed class FrameStream : Stream
{
    readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    byte[] current = [];
    int at;

    public void Write(object frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        Array.Clear(bytes);
        if (!queue.Writer.TryWrite(line)) { Array.Clear(line); }
    }

    public void Complete() => queue.Writer.TryComplete();
    public void Fail() => queue.Writer.TryComplete(new InvalidDataException());

    public void Clear()
    {
        Fail();
        while (queue.Reader.TryRead(out var left)) { Array.Clear(left); }
        Array.Clear(current);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (at >= current.Length)
        {
            Array.Clear(current);
            if (!await queue.Reader.WaitToReadAsync(ct)) { return 0; }
            if (!queue.Reader.TryRead(out var next)) { continue; }
            current = next;
            at = 0;
        }
        var n = Math.Min(buffer.Length, current.Length - at);
        current.AsMemory(at, n).CopyTo(buffer);
        at += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
