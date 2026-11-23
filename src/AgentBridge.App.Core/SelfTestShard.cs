namespace AgentBridge.App.Core;

// The published-app self-test's stages, in the order they run, and which shard of 1, 2 or 3 runs each (#1755).
// A shard takes a contiguous run of stages, so what a stage leaves behind (the launched session, the approval)
// is still there for the stages after it in the same shard.
public static class SelfTestStages
{
    public static readonly IReadOnlyList<string> All = ["layout", "launch", "approve", "themes", "signin", "progress", "contrast", "resize"];

    public const int MaxShards = 3;

    // Rows are shard counts; a row's items are the stages of shards 1, 2 and 3 of that count.
    static readonly string[][][] Plans =
    [
        [["layout", "launch", "approve", "themes", "signin", "progress", "contrast", "resize"]],
        [["layout", "launch", "approve", "themes"], ["signin", "progress", "contrast", "resize"]],
        [["layout", "launch", "approve"], ["themes", "signin", "progress"], ["contrast", "resize"]],
    ];

    public static int ShardOf(string stage, int count)
    {
        if (!All.Contains(stage)) { throw new ArgumentException($"Unknown self-test stage '{stage}'.", nameof(stage)); }
        if (count is < 1 or > MaxShards) { throw new ArgumentOutOfRangeException(nameof(count), "Shard counts are 1 to 3."); }
        return Array.FindIndex(Plans[count - 1], shard => shard.Contains(stage)) + 1;
    }
}

public sealed record SelfTestShard(int Index, int Count)
{
    public static readonly SelfTestShard Whole = new(1, 1);

    // Exactly "i/n", one digit each, 1 <= i <= n <= 3.
    public static bool TryParse(string? text, out SelfTestShard shard)
    {
        shard = Whole;
        if (text is not { Length: 3 } || text[1] != '/' || !char.IsAsciiDigit(text[0]) || !char.IsAsciiDigit(text[2])) { return false; }
        int index = text[0] - '0', count = text[2] - '0';
        if (count is < 1 or > SelfTestStages.MaxShards || index < 1 || index > count) { return false; }
        shard = new(index, count);
        return true;
    }

    public bool Runs(string stage) => SelfTestStages.ShardOf(stage, Count) == Index;

    public override string ToString() => $"{Index}/{Count}";
}