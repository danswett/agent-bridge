using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

// The checks a host runs on a record it has read, by the name a vector file gives them. A
// refused vector names one when the record parses but its check refuses it, with the check's
// own words, which clients that mirror the check (the PWA's checkAnswer, checkReply and
// checkLaunch) give word for word.
public static class VectorChecks
{
    public const string Answers = "Answers.Check";
    public const string Replies = "Replies.Check";
    public const string Stops = "Stops.Check";
    public const string Launch = "LaunchProtocol.Check";
    public const string AttachmentStart = "Attachments.Check";
    public const string AttachmentChunk = "Attachments.Read";
    public const string Transfer = "TransferProtocol.Check";
    public const string TransferResult = "TransferLimits.CheckResult";

    // Null when the check takes it; otherwise why not. context is the vector's own: the question
    // an answer answers, the most a channel takes, the most a machine takes of a bundle.
    public static string? Run(string check, object record, JsonNode? context) => check switch
    {
        Answers => AgentBridge.Protocol.Answers.Check(Context<Question>(context, "question"), (AnswerRequest)record),
        Replies => AgentBridge.Protocol.Replies.Check((ReplyRequest)record),
        Stops => AgentBridge.Protocol.Stops.Check((StopRequest)record),
        Launch => LaunchProtocol.Check((LaunchRequest)record),
        AttachmentStart => Attachments.Check((AttachmentStart)record, context?["inChannel"]?.GetValue<long>() ?? AttachmentLimits.Bytes),
        AttachmentChunk => Attachments.Read((AttachmentChunk)record).Problem,
        Transfer => record switch
        {
            LaunchTransfer t => TransferProtocol.Check(t),
            SignedCommand c => TransferProtocol.Check(c),
            _ => throw new ArgumentException($"{check} does not check a {record.GetType().Name}"),
        },
        TransferResult => TransferLimits.CheckResult((AgentBridge.Protocol.TransferResult)record, context?["maxBytes"]?.GetValue<long>() ?? TransferProtocol.DefaultMaxBytes),
        _ => throw new ArgumentException($"no check is called {check}"),
    };

    static T Context<T>(JsonNode? context, string name) =>
        (context?[name] ?? throw new ArgumentException($"the vector's context has no {name}")).Deserialize<T>(ProtocolJson.Options)!;
}
