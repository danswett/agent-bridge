using AgentBridge.Host.Delivery;
using Xunit;

public sealed class ProcessTreeEvidenceTests
{
    [Fact]
    public void A_reused_child_cannot_inherit_a_parent_edge_observed_before_its_creation_read()
    {
        var rig = new TreeRig();
        rig.Nodes.Remove(103);
        using var root = rig.OpenRoot();
        var evidence = rig.Evidence();
        var discovery = evidence.Capture()!;
        rig.BeforeCreated = id =>
        {
            if (id == 102 && rig.Nodes[id].Parent == 101) { rig.Nodes[id] = new TreeNode(900, TreeRig.T0.AddMinutes(1)); }
        };
        var proof = evidence.Child(root, 102, discovery);
        using var child = proof.Process;
        child?.Terminate();
        Assert.Empty(rig.Terminated);
        Assert.NotNull(proof.Problem);
        Assert.False(proof.Unrelated);
        Assert.Equal(1, rig.Handles);
    }

    [Fact]
    public void A_vanished_leaf_is_unknown_even_if_its_old_snapshot_has_no_children()
    {
        var rig = new TreeRig();
        rig.Nodes.Remove(103);
        using var root = rig.OpenRoot();
        var evidence = rig.Evidence();
        var discovery = evidence.Capture()!;
        rig.BeforeOpen = id =>
        {
            if (id != 102) { return; }
            rig.Nodes.Add(103, new TreeNode(102, TreeRig.T0.AddSeconds(2)));
            rig.Nodes[id].Running = false;
        };
        var proof = evidence.Child(root, 102, discovery);
        Assert.False(proof.Unrelated);
        Assert.NotNull(proof.Problem);
        Assert.Null(proof.Process);
        Assert.True(rig.Nodes[103].Running);
        Assert.Equal(1, rig.Handles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Child_proof_requires_a_complete_post_open_snapshot_and_the_held_creation_time(bool snapshot)
    {
        var rig = new TreeRig();
        using var root = rig.OpenRoot();
        var evidence = rig.Evidence();
        var discovery = evidence.Capture()!;
        if (snapshot) { rig.AfterOpen = id => { if (id == 102) { rig.SnapshotError = 5; } }; }
        else { rig.BeforeOpen = id => { if (id == 102) { rig.Nodes[id].Started = rig.Nodes[id].Started.AddTicks(1); } }; }
        var proof = evidence.Child(root, 102, discovery);
        using var child = proof.Process;
        Assert.Null(child);
        Assert.NotNull(proof.Problem);
        Assert.Equal(1, rig.Handles);
    }

    [Fact]
    public void A_verified_child_lease_is_owned_by_the_caller_not_the_identity_probe()
    {
        var rig = new TreeRig();
        using var root = rig.OpenRoot();
        var evidence = rig.Evidence();
        var proof = evidence.Child(root, 102, evidence.Capture()!);
        Assert.Null(proof.Problem);
        Assert.Equal(TreeRig.T0.AddSeconds(1), proof.Started);
        Assert.Equal(2, rig.Handles);
        proof.Process!.Dispose();
        Assert.Equal(1, rig.Handles);
        Assert.Empty(rig.Terminated);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("elevation")]
    public void An_owner_or_elevation_refusal_never_returns_a_child_lease(string reason)
    {
        var rig = new TreeRig();
        rig.Nodes[102].Refusal = reason;
        using var root = rig.OpenRoot();
        var evidence = rig.Evidence();
        var proof = evidence.Child(root, 102, evidence.Capture()!);
        Assert.Null(proof.Process);
        Assert.Equal(reason, proof.Problem);
        Assert.Equal(1, rig.Handles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_partial_enumeration_is_never_complete_snapshot_evidence(int error)
    {
        var result = ProcessTreeSnapshot.Read(add => { add(101, 0); add(102, 101); return error; });
        Assert.Null(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Declined_or_duplicate_rows_cannot_claim_a_complete_snapshot(bool deadline)
    {
        var result = ProcessTreeSnapshot.Read(add =>
        {
            add(101, 0);
            add(101, 900);
            return 18;
        }, () => !deadline);
        Assert.Null(result);
    }
}
