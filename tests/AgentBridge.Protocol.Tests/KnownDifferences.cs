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
        // The PWA reads escaped text it cannot read as held at the machine: Deny, never a one-tap Approve.
        "Question/escaped-text-that-cannot-be-read",
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

    // #766: the PWA carries a field's constraints but does not check them yet; the host does.
    static readonly string[] Constraints =
    [
        "a-number-below-its-minimum", "a-number-above-its-maximum", "a-number-that-is-not-whole", "a-number-off-its-step",
        "a-number-at-its-exclusive-bound", "text-shorter-than-its-minimum", "whitespace-that-is-not-an-email-address", "text-longer-than-its-maximum-in-utf16-units", "text-off-its-pattern",
        "text-that-is-not-an-email-address", "text-that-is-not-a-date", "too-few-ticked", "too-many-ticked",
    ];

    public static readonly IReadOnlyList<KnownDifference> All =
    [
        .. StrictFields.Select(v => new KnownDifference(v, 446, "pwa", "read",
            "refuses the record: the field is strict (v1-sessions.md, Malformed records)",
            "reads it, the field absent or its default")),
        .. OtherDefaults.Select(v => new KnownDifference(v, 446, "pwa", "read",
            "reads it, the field its type's default",
            "reads it, the field absent or filled in from another")),
        new("SessionsSynced/not-a-record", 446, "pwa", "read",
            "refuses it: the snapshot does not end, so no session is removed and no hold lifted",
            "reads it as a synced with no revision, which ends the snapshot"),
        .. UncheckedTransfer.Select(v => new KnownDifference(v, 447, "pwa", "check",
            "LaunchProtocol.Check refuses it with TransferProtocol.Check's reason",
            "checkLaunch takes it")),
        .. Constraints.Select(v => new KnownDifference($"AnswerRequest/{v}", 766, "pwa", "check",
            "Answers.Check refuses it: outside the field's constraints",
            "checkAnswer takes it: it does not check constraints yet")),
        // #913 host slice 2: the clients' half (CX) is to come. Until then the PWA shows a grouped
        // notice as a question naming no session, which opens the machine, and a reminder as the
        // question's own notice.
        .. new[] { "PushNoticeData/group", "PushNotification/group" }.Select(v => new KnownDifference(v, 913, "pwa", "read",
            "reads kind group: questions of several sessions as one notice, opening Needs you",
            "reads it as a question, an unknown kind, naming no session: it opens the machine")),
        new("PushNoticeData/a-reminder", 913, "pwa", "read",
            "reads reminder: true",
            "does not read reminder: it shows the question's notice as it is"),
    ];
}
