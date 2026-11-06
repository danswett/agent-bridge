using AgentBridge.Host.Core.Restore;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// What a host's start can start an agent with waits for the app's Stop (#986; Trust's finding 3 on
// #837). The restore recorder started well before the host looked for the Stop marker, so a host the
// logon task's launcher started after the person pressed Stop in the app resumed an agent before it
// saw the marker and exited. Now the marker is looked for before the recorder starts, and a start
// the app stopped restores nothing. A Stop pressed after that look and before the host's pipe opens
// reaches no pipe, and is seen only by the look Program takes once the pipe is open
// (HostHandover.StoppedFromTheApp), so the recorder's resumes are held until that look has passed
// (Serving): one held when the host stops is withdrawn, starting nothing, as at any stop.
public sealed class HostStartGate(HostHandover handover, string profileFolder)
{
    readonly TaskCompletionSource serving = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Gives the recorder its resume path, held until Serving, and starts it; unless the app's Stop
    // came first, and then nothing is started, and the host is not to serve.
    public bool StartRestore(RestoreRecorder restore, ISessionResumer resumer)
    {
        if (handover.StoppedFromTheApp(profileFolder)) { return false; }
        restore.Restore(Watched(new Held(serving.Task, resumer)));
        restore.Start();
        return true;
    }

    // The host has looked for the marker with its pipe open, and serves: from here the app's Stop
    // reaches it through the pipe.
    public void Serving() => serving.TrySetResult();

    // Tests only: told as the recorder asks to resume a session, before the hold, so a test can press
    // Stop with a resume provably under way (Engineering's 6690940551: left to the scheduler, a test
    // without the hold failed 6 runs in 30). Unset, the recorder gets the hold itself, as before.
    internal Action? Resuming { get; init; }

    ISessionResumer Watched(ISessionResumer path) => Resuming is { } told ? new Told(told, path) : path;

    sealed class Told(Action told, ISessionResumer inner) : ISessionResumer
    {
        public Task<LaunchResult> ResumeAsync(ResumeAsk ask, CancellationToken cancellationToken = default)
        {
            told();
            return inner.ResumeAsync(ask, cancellationToken);
        }
    }

    sealed class Held(Task serving, ISessionResumer inner) : ISessionResumer
    {
        public async Task<LaunchResult> ResumeAsync(ResumeAsk ask, CancellationToken cancellationToken = default)
        {
            try { await serving.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new LaunchResult(LaunchOutcome.Refused, "the host is stopping"); }
            return await inner.ResumeAsync(ask, cancellationToken).ConfigureAwait(false);
        }
    }
}
