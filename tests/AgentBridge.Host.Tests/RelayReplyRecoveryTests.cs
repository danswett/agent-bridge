using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed class RelayReplyRecoveryTests
{
    sealed class Terminal : ISessionReplier
    {
        public bool Available => true;
        public readonly TaskCompletionSource Typed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        { Assert.True(request.RequireIdle); Typed.TrySetResult(); return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered)); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Source_changes_wake_production_recovery_for_raw_and_roster_node_admissions(bool rosterNode, bool question)
    {
        var folder = Directory.CreateTempSubdirectory("relay-wake-").FullName;
        using var stopped = new CancellationTokenSource();
        Task pump = Task.CompletedTask;
        try
        {
            var source = new FleetPublisherTests.Source();
            source.Sessions = [source.Sessions[0] with
            {
                Status = question ? SessionStatus.Idle : SessionStatus.Working,
                Question = question ? new() { Id = "question", Kind = QuestionKind.Approval, Message = "fixture", AskedAt = DateTimeOffset.UtcNow } : null,
            }];
            var terminal = new Terminal();
            var service = new SessionService(source, new MachineInfo("fixture", "fixture", "test", "0"), _ => { }, replies: terminal);
            service.ConfigureRelayInbox(Path.Combine(folder, "replies.inbox"), (_, _) => true, _ => true);
            pump = service.RunRelayRecoveryAsync(stopped.Token);
            var rid = Guid.NewGuid().ToString("D");
            var reply = new ReplyRequest { Id = source.Sessions[0].Id, Request = rid, Text = "private reply" };
            var node = rosterNode ? AgentBridge.HomeAssistant.HaContract.NodeId(reply.Id) : reply.Id;
            Assert.Equal(AnswerOutcome.Busy, (await service.RelayInbox!.ReceiveAsync(node, rid, new(reply, "peer", "account"))).Outcome);
            Assert.False(terminal.Typed.Task.IsCompleted);
            source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Idle, Question = null };
            source.Signal();
            await terminal.Typed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stopped.Cancel(); await pump; Directory.Delete(folder, true); }
    }
}
