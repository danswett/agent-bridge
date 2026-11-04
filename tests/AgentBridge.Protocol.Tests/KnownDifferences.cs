// Where an implementation does not do today what a vector says, each with the issue that decides
// which side moves. Written into the vector files' knownDifferences; the PWA's replay
// (pwa/test/unit/protocol-vectors.test.mjs) checks that each still differs, so a fix fails it
// until its entry here goes.
public static class KnownDifferences
{
    // #446 (v1-sessions.md "Malformed records"): a strict field missing or of the wrong type. C#
    // refuses the record: it is filed or decided on by that field. The PWA reads it, the field
    // absent or its default.
    static readonly string[] StrictFields =
    [
        "ActivitySource/wrong-type-response", "Field/missing-type", "HelloResult/wrong-type-protocol", "MachineInfo/wrong-type-id",
        "LaunchedWith/missing-permissions", "LaunchOptions/missing-defaults", "Option/wrong-type-value", "Question/missing-id",
        "Question/missing-kind", "Question/missing-message", "Question/wrong-type-id", "ToolCall/missing-id", "ToolCall/wrong-type-id",
        "TrustQuestion/missing-agent", "TrustQuestion/missing-answerable", "TrustQuestion/missing-kind",
        "Question/a-choice-that-cannot-be-read", "Question/a-choice-label-that-is-not-text", "Question/a-delegate-that-is-not-an-id",
        "DiscoveryChanged/wrong-type-discoveryHeld",
    ];

    // #446: a required field missing, or one of the wrong type, that both read past. C# reads it as
    // its type's default, as the vector's reads says ("", 0, 0001-01-01T00:00:00.000Z); the PWA
    // fills in no time, no number, or a label from the record's id or kind.
    static readonly string[] OtherDefaults =
    [
        "AgentUsage/missing-label", "AgentUsage/missing-measuredAt", "AgentUsage/wrong-type-label", "LaunchAgent/missing-label",
        "LaunchedTransfer/missing-bytes", "LaunchedTransfer/missing-session", "LaunchedWith/missing-agent", "LaunchedWith/wrong-type-agent",
        "Question/missing-askedAt", "ResumableSession/missing-title", "Session/missing-statusSince", "Session/missing-updatedAt",
        "SessionsSynced/wrong-type-revision", "Subagent/missing-status", "ToolCall/missing-name", "ToolCall/missing-startedAt",
        "TrustQuestion/missing-askedAt", "TrustQuestion/missing-label", "UpdateResult/missing-at", "UsageWindow/missing-label",
    ];

    // #447: checkLaunch does not run TransferProtocol.Check on a launch's transfer.
    static readonly string[] UncheckedTransfer =
    [
        "LaunchRequest/a-transfer-from-something-not-a-device-id", "LaunchRequest/a-transfer-of-something-not-a-uuid",
        "LaunchRequest/a-transfer-without-a-command", "LaunchRequest/a-transfer-with-a-malformed-command",
    ];

    public static readonly IReadOnlyList<KnownDifference> All =
    [
        .. StrictFields.Select(v => new KnownDifference(v, 446, "pwa", "read",
            "refuses the record: the field is strict (v1-sessions.md, Malformed records)",
            "reads it, the field absent or its default")),
        .. OtherDefaults.Select(v => new KnownDifference(v, 446, "pwa", "read",
            "reads it, the field its type's default",
            "reads it, the field absent or filled in from another")),
        // #489: who wrote the latest prompt. The PWA does not read the field yet (B's PR follows
        // this one), so it shows every prompt as the person's, as before the field.
        .. new[] { "Activity/a-scheduled-prompt", "Activity/a-prompt-an-agent-sent", "Activity/an-unknown-sender-reads-as-other-never-the-person" }
            .Select(v => new KnownDifference(v, 489, "pwa", "read",
                "reads userMessageFrom and userMessageLabel, an unknown sender as other",
                "does not read them yet: the prompt reads as the person's")),
        new("Activity/a-sender-that-is-not-text", 489, "pwa", "read",
            "refuses it: userMessageFrom is strict, so the prompt is never shown as the person's",
            "does not read userMessageFrom yet, so reads the prompt as the person's"),
        new("SessionsSynced/not-a-record", 446, "pwa", "read",
            "refuses it: the snapshot does not end, so no session is removed and no hold lifted",
            "reads it as a synced with no revision, which ends the snapshot"),
        new("SessionsSynced/a-live-id-that-is-not-text", 601, "pwa", "read",
            "refuses it: live is strict and whole, so no running session is dropped from a resumed list",
            "reads it, live unread: it does not resume yet"),
        .. UncheckedTransfer.Select(v => new KnownDifference(v, 447, "pwa", "check",
            "LaunchProtocol.Check refuses it with TransferProtocol.Check's reason",
            "checkLaunch takes it")),
    ];
}
