using AgentBridge.Host.Delivery.Handoff;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Delivery.Tests;

public sealed class HandoffTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "handoff-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
    }

    sealed class FakeAgent(Func<TimeProvider, CancellationToken, Task<string?>> answer) : IHandoffAgent
    {
        public int Asked;
        public string? Prompt;
        public async Task<string?> AskForNoteAsync(string sessionId, string prompt, CancellationToken cancellationToken)
        {
            Asked++;
            Prompt = prompt;
            return await answer(TimeProvider.System, cancellationToken);
        }
    }

    sealed class FakeTranscript(HandoffDigest? digest) : IHandoffTranscript
    {
        public Task<HandoffDigest?> ReadAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(digest);
    }

    sealed class FailingStore : IHandoffStore
    {
        public Task WriteAsync(string sessionId, string note, CancellationToken cancellationToken) => throw new IOException("disk full");
    }

    HandoffService Service(IHandoffAgent agent, IHandoffTranscript? transcript = null, IHandoffStore? store = null) =>
        new(agent, transcript ?? new FakeTranscript(null), store ?? new FileHandoffStore(folder), clock) { AnswerTimeout = TimeSpan.FromMilliseconds(200) };

    [Fact]
    public async Task An_idle_session_that_writes_its_note_is_confirmed_with_the_notes_size()
    {
        var agent = new FakeAgent((_, _) => Task.FromResult<string?>("Done: x\nOpen: y\nFiles: a.cs"));
        var result = await Service(agent).RunAsync("s1", HandoffSessionState.Idle);

        Assert.True(result.Confirmed);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("Done: x\nOpen: y\nFiles: a.cs"), result.Bytes);
        Assert.Contains("files you touched", agent.Prompt);
        Assert.Equal("Done: x\nOpen: y\nFiles: a.cs", File.ReadAllText(Path.Combine(folder, "s1.handoff.md")));
    }

    [Fact]
    public async Task An_idle_session_that_does_not_answer_fails_at_handoff_and_saves_nothing()
    {
        var agent = new FakeAgent(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });
        var result = await Service(agent).RunAsync("s1", HandoffSessionState.Idle);

        Assert.False(result.Confirmed);
        Assert.Equal("handoff", result.FailedStep);
        Assert.StartsWith("failed at handoff:", result.Reason);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task An_ended_session_is_built_from_its_transcript_without_asking_the_agent()
    {
        var agent = new FakeAgent((_, _) => Task.FromResult<string?>("never"));
        var digest = new HandoffDigest(["added the parser"], ["wire the host"], ["src/Parser.cs"]);
        var result = await Service(agent, new FakeTranscript(digest)).RunAsync("s1", HandoffSessionState.Ended);

        Assert.True(result.Confirmed);
        Assert.Equal(0, agent.Asked);
        var note = File.ReadAllText(Path.Combine(folder, "s1.handoff.md"));
        Assert.Contains("added the parser", note);
        Assert.Contains("wire the host", note);
        Assert.Contains("src/Parser.cs", note);
        Assert.Equal(note.Length, result.Bytes);
    }

    [Fact]
    public async Task An_ended_session_with_an_unreadable_transcript_fails_at_handoff()
    {
        var result = await Service(new FakeAgent((_, _) => Task.FromResult<string?>(null))).RunAsync("s1", HandoffSessionState.Ended);

        Assert.False(result.Confirmed);
        Assert.Equal("handoff", result.FailedStep);
    }

    [Fact]
    public async Task A_note_that_cannot_be_saved_fails_at_write_and_leaves_the_transcript_alone()
    {
        Directory.CreateDirectory(folder);
        var transcript = Path.Combine(folder, "events.jsonl");
        File.WriteAllText(transcript, "line\n");
        var agent = new FakeAgent((_, _) => Task.FromResult<string?>("note"));
        var result = await Service(agent, store: new FailingStore()).RunAsync("s1", HandoffSessionState.Idle);

        Assert.Equal("write", result.FailedStep);
        Assert.Equal("line\n", File.ReadAllText(transcript));
        Assert.Equal([transcript], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task The_steps_carry_start_and_end_times_for_the_move_timeline()
    {
        var agent = new FakeAgent((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(7));
            return Task.FromResult<string?>("note");
        });
        var result = await Service(agent).RunAsync("s1", HandoffSessionState.Idle);

        Assert.Equal(["handoffRequested", "handoffConfirmed"], result.Steps.Select(s => s.Name));
        Assert.Equal(0, result.Steps[0].Ms);
        Assert.Equal(7000, result.Steps[1].Ms);
        Assert.Equal(result.Steps[0].At.AddSeconds(7), result.Steps[1].At);
    }

    [Fact]
    public async Task A_session_id_that_is_a_path_is_refused_before_anything_is_written()
    {
        var result = await Service(new FakeAgent((_, _) => Task.FromResult<string?>("note"))).RunAsync("..\\evil", HandoffSessionState.Idle);

        Assert.False(result.Confirmed);
        Assert.False(Directory.Exists(folder));
    }
}
