using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class SelfTestShardTests
{
    [Theory]
    [InlineData("1/1", 1, 1)]
    [InlineData("2/3", 2, 3)]
    [InlineData("3/3", 3, 3)]
    public void A_shard_is_read_as_index_over_count(string text, int index, int count)
    {
        Assert.True(SelfTestShard.TryParse(text, out var shard));
        Assert.Equal(new SelfTestShard(index, count), shard);
        Assert.Equal(text, shard.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0/3")]
    [InlineData("4/3")]
    [InlineData("1/4")]
    [InlineData("1/0")]
    [InlineData("a/b")]
    [InlineData("2/3/4")]
    [InlineData(" 2/3")]
    [InlineData("2 /3")]
    public void Anything_else_is_not_a_shard(string? text) => Assert.False(SelfTestShard.TryParse(text, out _));

    [Fact]
    public void The_whole_run_does_every_stage()
    {
        foreach (var stage in SelfTestStages.All) { Assert.True(SelfTestShard.Whole.Runs(stage)); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_stage_is_in_exactly_one_shard_each_shard_has_stages_and_they_stay_in_order(int count)
    {
        var shards = Enumerable.Range(1, count).Select(i => new SelfTestShard(i, count)).ToArray();
        foreach (var stage in SelfTestStages.All) { Assert.Single(shards, s => s.Runs(stage)); }
        Assert.All(shards, s => Assert.Contains(SelfTestStages.All, s.Runs));
        var owners = SelfTestStages.All.Select(stage => shards.Single(s => s.Runs(stage)).Index).ToArray();
        Assert.Equal(owners.Order(), owners);
    }

    [Fact]
    public void Three_shards_split_the_stages_as_the_design_says()
    {
        string[] Of(int i) => SelfTestStages.All.Where(new SelfTestShard(i, 3).Runs).ToArray();
        Assert.Equal(["layout", "launch", "approve"], Of(1));
        Assert.Equal(["themes", "signin", "progress"], Of(2));
        Assert.Equal(["contrast", "resize"], Of(3));
    }

    [Fact]
    public void A_stage_that_does_not_exist_is_refused_not_skipped() =>
        Assert.Throws<ArgumentException>(() => SelfTestShard.Whole.Runs("nonsense"));
}