// Where an implementation does not do today what a vector says, each with the issue that decides
// which side moves. Written into the vector files' knownDifferences; the PWA's replay
// (pwa/test/unit/protocol-vectors.test.mjs) checks that each still differs, so a fix fails it
// until its entry here goes.
public static class KnownDifferences
{
    // #446: a host record missing a required field, or with one of the wrong JSON type. C# refuses
    // the record; the PWA reads the field as absent or as its default, and drops a record only
    // when it has no identity to file it under.
    static readonly string[] TolerantReaders =
    [
        "AttachmentState/wrong-type-received", "AttachmentDiscarded/wrong-type-discarded",
        "AnswerResult/wrong-type-reason", "ReplyResult/wrong-type-reason", "StopResult/wrong-type-reason",
        "LaunchedTransfer/missing-session", "LaunchedTransfer/missing-bytes", "LaunchedWith/missing-agent", "LaunchedWith/missing-permissions",
        "LaunchedWith/wrong-type-agent", "LaunchResult/wrong-type-session", "LaunchTuning/wrong-type-model", "LaunchAgent/missing-label",
        "LaunchAgent/missing-kind", "LaunchWorkspace/missing-path", "ResumableSession/missing-agent", "ResumableSession/missing-title",
        "LaunchOptions/missing-defaults", "LaunchOptions/wrong-type-busy", "TrustQuestion/missing-agent", "TrustQuestion/missing-kind",
        "TrustQuestion/missing-label", "TrustQuestion/missing-askedAt", "TrustQuestion/missing-answerable", "TrustAnswerResult/wrong-type-session",
        "MachineInfo/wrong-type-id", "HelloResult/wrong-type-protocol", "ToolCall/missing-id", "ToolCall/missing-name", "ToolCall/missing-status",
        "ToolCall/missing-startedAt", "ToolCall/wrong-type-id", "Option/wrong-type-value", "Field/missing-type", "Question/missing-id",
        "Question/missing-kind", "Question/missing-message", "Question/missing-askedAt", "Question/wrong-type-id", "Background/wrong-type-agents",
        "ActivitySource/wrong-type-response", "OwnActivity/wrong-type-response", "Activity/wrong-type-response", "Subagent/missing-kind",
        "Subagent/missing-status", "Session/missing-agent", "Session/missing-status", "Session/missing-statusSince", "Session/missing-updatedAt",
        "Session/missing-activity", "SessionList/wrong-type-next", "DiscoveryChanged/wrong-type-discoveryHeld", "SessionsSynced/wrong-type-revision",
        "UpdateResult/missing-outcome", "UpdateResult/missing-at", "UpdateResult/wrong-type-from", "UpdateState/wrong-type-current",
        "UpdateCommandResult/wrong-type-reason", "UsageWindow/missing-key", "UsageWindow/missing-label", "UsageWindow/wrong-type-key",
        "AgentUsage/missing-agent", "AgentUsage/missing-label", "AgentUsage/missing-source", "AgentUsage/missing-measuredAt",
        "AgentUsage/wrong-type-label", "UsageReport/wrong-type-agents",
    ];

    // #447: checkLaunch does not run TransferProtocol.Check on a launch's transfer.
    static readonly string[] UncheckedTransfer =
    [
        "LaunchRequest/a-transfer-from-something-not-a-device-id", "LaunchRequest/a-transfer-of-something-not-a-uuid",
        "LaunchRequest/a-transfer-without-a-command", "LaunchRequest/a-transfer-with-a-malformed-command",
    ];

    public static readonly IReadOnlyList<KnownDifference> All =
    [
        .. TolerantReaders.Select(v => new KnownDifference(v, 446, "pwa", "read",
            "refuses the record: it cannot be deserialized",
            "reads it, the field absent or its default")),
        .. UncheckedTransfer.Select(v => new KnownDifference(v, 447, "pwa", "check",
            "LaunchProtocol.Check refuses it with TransferProtocol.Check's reason",
            "checkLaunch takes it")),
        // #448: U+FEFF is whitespace to JavaScript's \s, not to C#'s char.IsWhiteSpace.
        new("LaunchRequest/a-prompt-led-by-a-byte-order-mark", 448, "pwa", "check",
            "takes it: the prompt's first character is U+FEFF, not /",
            "checkLaunch skips U+FEFF as whitespace and refuses it as a slash command"),
        new("AnswerRequest/a-byte-order-mark-is-text", 448, "pwa", "check",
            "takes it: U+FEFF is not whitespace to string.IsNullOrWhiteSpace",
            "checkAnswer reads it as blank and refuses it as empty"),
    ];
}
