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
    public const string Patches = "SessionPatches.Apply";
    public const string History = "HistoryProtocol.Check";

    // Null when the check takes it; otherwise why not. context is the vector's own: the question
    // an answer answers, the most a channel takes, the most a machine takes of a bundle.
    public static string? Run(string check, object record, JsonNode? context) => check switch
    {
        Answers => AnswerCheck(Context<Question>(context, "question"), (AnswerRequest)record),
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
        Patches => PatchCheck((SessionPatch)record, context),
        History => HistoryProtocol.Check((HistoryRequest)record),
        _ => throw new ArgumentException($"no check is called {check}"),
    };

    // The context holds the session as the client last received it (absent: none) and the seq of
    // the event that gave it, and for a valid patch the session it must then hold, exactly.
    static string? PatchCheck(SessionPatch patch, JsonNode? context)
    {
        var held = context?["session"]?.DeepClone().AsObject();
        JsonObject applied;
        try { applied = SessionPatches.Apply(held, context?["seq"]?.GetValue<long>(), patch); }
        catch (SessionPatchException e) { return e.Message; }
        return context?["result"] is { } result && !JsonNode.DeepEquals(result, applied)
            ? throw new InvalidOperationException($"the patch gives {applied.ToJsonString()}, not its context's result")
            : null;
    }

    // A host takes an answer only for the question pending (stale otherwise) before Answers.Check
    // runs, so a vector answering another question than its context's would say nothing.
    static string? AnswerCheck(Question question, AnswerRequest answer) =>
        answer.Question == question.Id ? AgentBridge.Protocol.Answers.Check(question, answer)
        : throw new InvalidOperationException($"the answer is for question {answer.Question}, but its context asks {question.Id}");

    static T Context<T>(JsonNode? context, string name) =>
        (context?[name] ?? throw new ArgumentException($"the vector's context has no {name}")).Deserialize<T>(ProtocolJson.Options)!;
}
