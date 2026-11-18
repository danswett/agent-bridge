using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

// One private random key per serving host run, shared by its sessions and reconnects.
public sealed class FleetEvidenceCapture(IConsoleInjector screens, Func<SessionProcess, bool>? current = null, TimeProvider? time = null) : IDisposable
{
    readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    readonly Lock gate = new();
    readonly TimeProvider clock = time ?? TimeProvider.System;
    bool disposed;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    public static Dictionary<string, object> Facts(FleetSessionSnapshot snapshot, DateTimeOffset now)
    {
        var fields = new Dictionary<string, object>();
        if (snapshot.ObservedAt is { } at) { fields["observedAt"] = at; fields["stale"] = now - at > StaleAfter; }
        if (snapshot.OpenAsks is { } asks) { fields["openAsks"] = asks; }
        if (snapshot.Attention is { } kind) { fields["attention"] = new { kind }; }
        if (snapshot.LastReplyAt is { } reply) { fields["lastReplyAt"] = reply; }
        return fields;
    }

    public static SessionEvidence? Evidence(Dictionary<string, object> fields) =>
        SessionEvidence.Read(JsonSerializer.SerializeToElement(fields, ProtocolJson.Options));

    public async Task<SessionEvidence?> ReadAsync(FleetSessionSnapshot snapshot, CancellationToken ct = default)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); }
        var fields = Facts(snapshot, clock.GetUtcNow());
        var binding = snapshot.Process;
        bool Bound() => binding is { Id: > 0, StartedAt: not null } && (current?.Invoke(binding)
            ?? AgentCredentials.SameProcessAlive(binding.Id, binding.StartedAt.Value));
        if (snapshot.Session.Agent != AgentKind.Copilot || !Bound()) { return Evidence(fields); }
        var screen = await screens.ReadScreenAsync(binding!.Id, ct);
        ct.ThrowIfCancellationRequested();
        if (!Bound() || screen.Rows is null) { return Evidence(fields); }
        fields["observedAt"] = clock.GetUtcNow();
        var read = CopilotInputBoxReader.Read(screen.Rows);
        if (StartupQuestions.Read(screen.Rows) == StartupQuestion.Trust) { fields["attention"] = new { kind = "trust" }; }
        else if (!read.Seen || read.State is not { Main: true, Complete: true, Shell: false, Vim: not CopilotVimMode.Normal })
        {
            fields["attention"] = new { kind = snapshot.Attention is "question" or "approval" or "permission" ? snapshot.Attention : "unknown" };
        }
        else
        {
            var content = read.State.Content;
            fields["unsentLength"] = content.Length;
            var proof = new Dictionary<string, object> { ["source"] = "host-observed" };
            if (content.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                try
                {
                    lock (gate)
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);
                        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(key, bytes));
                        fields["unsentHash"] = hash; proof["unsentHash"] = hash;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            fields["provenance"] = proof;
        }
        return Evidence(fields);
    }

    public void Dispose()
    {
        lock (gate) { CryptographicOperations.ZeroMemory(key); disposed = true; }
    }
}
