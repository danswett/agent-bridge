using AgentBridge.AgentEnvelope;

namespace AgentBridge.App.Core.Grants;

public interface IVerifiedGrantReader
{
    Task<VerifiedGrantRead> ReadAsync(CancellationToken ct = default);
}

public sealed class VerifiedGrantRead
{
    // The App adapter verifies the retained context and computes readiness before construction.
    internal VerifiedGrantRead(FleetGrantReadProblem problem, bool g7Ready, GrantReadResult data)
    {
        if (!Enum.IsDefined(problem) || data is null || data.Grants.IsDefault)
        { throw new InvalidDataException("invalid verified grant read"); }
        Problem = problem; G7Ready = g7Ready; Data = data;
    }
    public FleetGrantReadProblem Problem { get; }
    public bool G7Ready { get; }
    public GrantReadResult Data { get; }
}

public sealed class UnavailableVerifiedGrantReader : IVerifiedGrantReader
{
    public Task<VerifiedGrantRead> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new VerifiedGrantRead(FleetGrantReadProblem.Unavailable, false, new([], "unavailable")));
    }
}
