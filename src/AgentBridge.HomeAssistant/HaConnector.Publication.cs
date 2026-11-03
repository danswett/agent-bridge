namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    HaPublicationReceipts? publicationReceipts;
    HaPublicationWriter? publicationWriter;

    void InitializePublication()
    {
        if (options.ProfileFolder is not null && options.BaseUrl is not null)
        {
            publicationReceipts = new(options.ProfileFolder, options.BaseUrl, options.DashboardUrlPath);
        }
        if (!options.Publication.Enabled) { return; }
        if (publicationReceipts is null) { throw new ArgumentException("Native publication requires an instance-scoped protected receipt store."); }
        var configured = options.Publication;
        if (configured.Generation < 1 || string.IsNullOrEmpty(configured.Authority) ||
            string.IsNullOrEmpty(configured.Participant) || string.IsNullOrEmpty(configured.Writer))
        {
            throw new ArgumentException("Native publication requires an explicit protocol-1 identity and generation.");
        }
        publicationWriter = new(ha, options.DashboardUrlPath, publicationReceipts,
            new(configured.Authority, configured.Participant, configured.Writer, configured.Generation));
    }

    async Task PublicationAsync(CancellationToken ct)
    {
        if (publicationWriter is null) { return; }
        var state = await publicationWriter.ReadAsync(ct);
        if (state.Policy is null) { throw new InvalidDataException("Native publication requires an explicit established writer handover; automatic bootstrap is refused."); }
        HaFleetSession[] local;
        lock (gate) { local = [.. published.Values.Select(p => new HaFleetSession(p.Node, p.Name, options.MachineName, p.Kind))]; }
        var capabilities = HaPayloads.Capabilities(helperExists, options.Dev, LaunchCapabilities());
        var fleet = HaPublicationFleet.Read(await ha.GetStatesAsync(ct), Slug, options.MachineName, local, capabilities);
        // Never downgrade a newer accepted card merely because this binary carries
        // an older one. Its actual inline bytes must still prove the policy target.
        if (HaPublicationRenderer.Card.Version.CompareTo(state.Policy.Card.Version) >= 0)
        {
            await publicationWriter.PublishCardAsync(HaPublicationRenderer.CardUrl, ct);
            state = await publicationWriter.ReadAsync(ct);
        }
        if (HaPublicationArtifact.ReadInlineCard(state.CardUrl) != state.Policy!.Card)
        {
            throw new InvalidDataException("The actual served card cannot prove the accepted publication target.");
        }
        var selector = await publicationWriter.ReconcileSelectorAsync(fleet.Machines, HaPublicationRenderer.Artifact, ct);
        state = await publicationWriter.ReadAsync(ct);
        var rendered = HaPublicationRenderer.Render(fleet.Sessions, fleet.Machines, selector, state.CardUrl, cardTrusted: true);
        HaVerifiedPublication? proof = null;
        try { proof = HaVerifiedPublication.Read(state); }
        catch (InvalidDataException)
        {
            // Policy/card authority was checked independently above. An overwritten
            // dashboard receipt needs repair, not permanent writer lockout.
        }
        if (proof is { Verified: true } && proof.InputHash == rendered.InputHash && state.Policy!.Render == HaPublicationRenderer.Artifact &&
            proof.RenderedNodes.SetEquals(rendered.RenderedNodes))
        {
            return;
        }
        await publicationWriter.PublishDashboardAsync(rendered.Config, rendered.InputHash, rendered.RenderedNodes, HaPublicationRenderer.Artifact, ct);
    }

    void HoldRetirement()
    {
        lock (gate) { foreach (var retiringSession in retiring.Values) { retiringSession.AbsentSince = null; } }
    }
}
