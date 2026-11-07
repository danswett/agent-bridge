using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Copilot;

// One log line for each answer to a Copilot form (#1123): the form's shape, how the session's
// screen read, the check that failed if one did, how long each stage took and how it ended.
// A refusal that could not be told apart from the log took a morning to trace. Never a word of
// the question, the screen or the answer, only counts, positions, names and times.
//
// Its shape is versioned (v=1), space-separated key=value pairs in a fixed order: keys are
// added at the end and never renamed or removed, so whatever reads it (#1132's feedback
// reports) keeps reading it.
public sealed partial class CopilotAnswerTrace(TimeProvider time, string request, string session)
{
    // The trace of the answer being delivered on this flow, if any: screen reads count against it.
    public static readonly AsyncLocal<CopilotAnswerTrace?> Current = new();

    readonly TimeProvider clock = time;
    readonly long started = time.GetTimestamp();
    long last = time.GetTimestamp();
    readonly List<(string Name, long Ms)> stages = [];
    readonly Lock gate = new();
    string? cli;
    string shape = "-";
    int fields;
    int reads, rereads, retries, taken;
    bool steady = true;
    string check = "none";

    // The Copilot CLI the session started under, as its transcript's first event says.
    public void Cli(string? version) { lock (gate) { cli = version; } }

    // How many fields and of which kinds, with each list's number of options: "choice3+text".
    public void Form(CopilotForm form)
    {
        lock (gate)
        {
            fields = form.Fields.Count;
            shape = form.Fields.Count == 0 ? "-" : string.Join('+', form.Fields.Select(f =>
                $"{f.Kind.ToString().ToLowerInvariant()}{(f.Options.Count > 0 ? f.Options.Count.ToString(CultureInfo.InvariantCulture) : "")}"));
        }
    }

    public void Read() { lock (gate) { reads++; } }

    // The form did not show on a read, and the screen is read again before anything is said.
    public void Reread() { lock (gate) { rereads++; steady = false; } }

    // A write the worker refused because the screen changed, typed again once the form read the same.
    public void Retried() { lock (gate) { retries++; } }

    public void Taken(int count) { lock (gate) { taken = count; } }

    public void Failed(FormCheck failed, int row)
    {
        lock (gate) { check = $"{CopilotFormScreen.Name(failed)}{(row >= 0 ? $"@{row.ToString(CultureInfo.InvariantCulture)}" : "")}"; }
    }

    // The time since the last stage, or since the trace began, under this stage's name.
    public void Stage(string name)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            stages.Add((name, (long)clock.GetElapsedTime(last, now).TotalMilliseconds));
            last = now;
        }
    }

    public string Line(AnswerResult? result)
    {
        lock (gate)
        {
            var total = (long)clock.GetElapsedTime(started).TotalMilliseconds;
            var line = new StringBuilder("answer-attempt v=1");
            line.Append(CultureInfo.InvariantCulture, $" request={Short(request)} session={Short(session)} cli={cli ?? "-"}");
            line.Append(CultureInfo.InvariantCulture, $" fields={fields} shape={shape} reads={reads} rereads={rereads} steady={(steady ? "yes" : "no")} retries={retries}");
            line.Append(CultureInfo.InvariantCulture, $" check={check} taken={taken}/{fields}");
            line.Append(CultureInfo.InvariantCulture, $" stages={(stages.Count == 0 ? "-" : string.Join(',', stages.Select(s => $"{s.Name}:{s.Ms}")))} total={total}");
            line.Append(CultureInfo.InvariantCulture, $" outcome={(result is null ? "failed" : result.Outcome.ToString().ToLowerInvariant())}");
            return line.ToString();
        }
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    // The version on the transcript's first event (session.start's copilotVersion), read from
    // no more than its first 64 KiB; null when there is none or it is not a plain version.
    public static string? CliOf(string transcript)
    {
        try
        {
            using var file = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            var length = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            using var first = JsonDocument.Parse(buffer.AsMemory(0, end < 0 ? length : end));
            return first.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("copilotVersion", out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { } version && Plain().IsMatch(version) ? version : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    [GeneratedRegex(@"^[0-9A-Za-z.+-]{1,40}$")]
    private static partial Regex Plain();
}
