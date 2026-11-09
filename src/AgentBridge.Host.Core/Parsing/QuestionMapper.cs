using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Maps each agent's way of asking onto the contract's Question and Field.
internal static class QuestionMapper
{
    // Approve is once: this request, never a standing grant (Codex's y, Copilot's plain Yes).
    public static readonly IReadOnlyList<Option> ApprovalOptions = [new("approve", "Approve once"), new("deny", "Deny")];

    // The contract's message limit. A question whose message had to be cut says so, so a
    // client shows it read-only and sends the person to the machine for the rest.
    static string Cut(string message) => Limits.KeepStart(message, Limits.QuestionMessage)!;

    static bool? IsCut(string message) => Cut(message).Length < message.Length ? true : null;
    // A permission prompt: a fixed approve-or-deny choice. atMachine marks one no host can
    // answer from another device, so a client shows it without the buttons. Whatever agent
    // asked, a character not drawn where it runs, and a right-to-left letter, is shown as a
    // visible escape, and Approve once is still offered (#220; the owner's #253 decision 3): what
    // a reader is shown is then what runs. A client that cannot draw it exactly as written holds
    // it at the machine itself.
    //
    // An approval the host escaped anything in carries Escaped (#803): where its escapes are, the
    // digest of the message as sent, and approve, the machine's permissions.escapedApproval, which
    // says whether Approve may come as a confirmation from an approved device or only at the machine.
    // marks: given when message is already shown so (CopilotPermissionPrompt), and then not escaped
    // again.
    public static Question Approval(string id, string message, DateTimeOffset askedAt, bool atMachine = false,
        string approve = EscapedApproval.Confirm, IReadOnlyList<(int Start, int Length)>? marks = null)
    {
        // Escaped from no more than it can carry (RevealWithin): still cut, and said to be, if it is over.
        var (shown, escapes) = marks is null ? ShownText.RevealWithinMarked(message, Limits.QuestionMessage, rightToLeft: true) : (message, marks);
        var cut = Cut(shown);
        // More escapes than a question describes is a message not described exactly: Approve is the
        // machine's (TooMany), never confirmed. Not truncated, which would refuse Deny too: the whole
        // message came, and denying it is safe from anywhere (#836 review).
        var many = escapes.Count > EscapedApproval.MaxMarks;
        return new()
        {
            Id = id,
            Kind = QuestionKind.Approval,
            Message = cut,
            Truncated = IsCut(shown),
            Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = ApprovalOptions, Required = true }],
            AskedAt = askedAt,
            AnswerAtMachine = atMachine ? true : null,
            Escaped = escapes.Count == 0 ? null : EscapedApproval.For(cut, escapes.Take(EscapedApproval.MaxMarks), many ? EscapedApproval.TooMany : approve),
        };
    }

    // A Codex approval from its hook's marker (#209): the whole command, literal, in a fence longer
    // than any run of backticks inside it, under the tool that asked. A marker that does not carry
    // the whole command (agent-ha-bridge's hook and older hosts wrote only a 300-character preview,
    // and a command past the hook's CommandMax is kept to its start) is truncated, so no client
    // approves what it cannot show; one with no command at all (a tool that runs none) shows the
    // tool and is answered at the machine.
    //
    // Its id is the marker's DecisionId and a digest of what is shown (CodexApprovalId, #958),
    // worked out from the content alone; atMachine (agent-ha-bridge already typed into it) is
    // laid on after, so it holds the approval without changing which one it is. approve is the
    // machine's permissions.escapedApproval, carried in Escaped for one the host escaped
    // anything in (#803); a confirmation names this id, so it too approves only what was shown.
    public static Question CodexApproval(string decisionId, string? tool, string? command, bool cut, string preview, DateTimeOffset askedAt, bool atMachine,
        string approve = EscapedApproval.Confirm, string? lossy = null)
    {
        var shown = CodexApprovalShown(tool, command, cut, preview, askedAt, approve);
        // A marker the tolerant read rewrote (half a surrogate pair, U+FFFD) is held at the machine,
        // and its id binds the marker as written (#836, #961; Engineering's review of #921).
        if (lossy is not null) { shown = shown with { AnswerAtMachine = true }; }
        var q = shown with { Id = CodexApprovalId.For(decisionId, tool, command, cut, preview, shown, lossy) };
        return atMachine ? q with { AnswerAtMachine = true } : q;
    }

    static Question CodexApprovalShown(string? tool, string? command, bool cut, string preview, DateTimeOffset askedAt, string approve)
    {
        const string id = "";
        var who = $"Allow **{CopilotPermission.Plain(string.IsNullOrWhiteSpace(tool) ? "Codex" : tool)}**?";
        if (command is not { Length: > 0 })
        {
            var legacy = preview.StartsWith("Needs approval: ", StringComparison.Ordinal) ? preview["Needs approval: ".Length..] : preview;
            var held = Approval(id, legacy.Length > 0 ? $"{who}\n\n{CopilotPermission.Fence(legacy)}" : who, askedAt, approve: approve);
            return tool is null && command is null ? held with { Truncated = true } : held with { AnswerAtMachine = true };
        }
        var q = Approval(id, $"{who}\n\n{CopilotPermission.Fence(command)}", askedAt, approve: approve);
        if (cut) { q = q with { Truncated = true }; }
        // Whatever the command, what is sent is that shape, which every client checks before it
        // offers Approve; one that is not would be answered nowhere else, so it is held here. One
        // already truncated is refused for that, and is left so: cut past the message's limit, it
        // has lost its closing fence, and held here too, a client said only that it is answered at
        // the machine, not that part of it is missing.
        if (q.Truncated != true && !ApprovalShape.IsFencedCommand(q.Message)) { q = q with { AnswerAtMachine = true }; }
        return q;
    }

    // Copilot's ask_user. Current builds pass `message` and `requestedSchema` (a JSON
    // Schema form); older ones passed `question` and a `choices` list, sometimes as a
    // JSON string. Without reading both, a multiple choice became a free-text box titled
    // "needs your input".
    // patterns: the budget the caller's patterns share when it reads more than one ask_user: a
    // history request gave each its own, and a page of them ran for minutes (review of #686).
    public static Question FromCopilotAskUser(string id, JsonElement arguments, DateTimeOffset askedAt, PatternBudget? patterns = null)
    {
        // Shown, not GetString: half a surrogate pair in the message threw outside the guard below,
        // freezing the session (#921 review). It is shown as U+FFFD, as a client shows one.
        var message = CopilotFieldSchema.Shown(arguments, "message") ?? CopilotFieldSchema.Shown(arguments, "question") ?? "";
        IReadOnlyList<Field> fields = [];
        var atMachine = false;
        string? atMachineReason = null;
        try { (fields, atMachine, atMachineReason) = AskUserFields(id, arguments, patterns); }
        // Text .NET will not read, half a surrogate pair, anywhere in the form (a name, a title,
        // an option, a type): reading it threw, and the transcript line holding it failed on every
        // pass, freezing the session (#921 review). Such a form is answered at the machine, and
        // so is any read a later change adds that meets one.
        catch (Exception e) when (e is InvalidOperationException or JsonException) { (fields, atMachine) = ([], true); }
        // Rewritten text (or a literal U+FFFD) in a name or option is no name to answer by.
        if (Json.IsLossy(arguments)) { atMachine = true; }
        return new Question
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = Cut(message),
            Truncated = IsCut(message),
            Fields = fields,
            AskedAt = askedAt,
            AnswerAtMachine = atMachine ? true : null,
            AnswerAtMachineReason = atMachineReason,
        };
    }


    static (IReadOnlyList<Field> Fields, bool AtMachine, string? Reason) AskUserFields(string id, JsonElement arguments, PatternBudget? patterns)
    {
        IReadOnlyList<Field> fields = [];
        var atMachine = false;
        var choices = ChoiceList(Json.Get(arguments, "choices"));
        if (choices.Count > 0)
        {
            fields =
            [
                new Field
                {
                    Name = "answer",
                    Type = FieldType.Choice,
                    Options = [.. choices.Select(c => new Option(c))],
                    // The old tool took free text unless told otherwise.
                    AllowOther = Json.Bool(arguments, "allow_freeform") ?? true,
                    Required = true,
                },
            ];
        }
        else if (Json.Get(arguments, "requestedSchema") is { } schema)
        {
            (fields, var drivable) = SchemaForm(schema, id, patterns);
            // Every Copilot option list ends in "Other (type your answer)", and a lone list's
            // Other takes words at the CLI, but not from here yet. In a live 1.0.105 session the
            // Down past its last option opens Other as a label in the form ("Other (type your
            // answer below)") and takes the words in a box of the session's own, drawn apart from
            // the form (`Dxn`'s onExternalFreeform, `bOe` in pinnedFooterContent), above or below
            // it by the footer's mode; the host holds keys only to the form, so it refuses the
            // words before any key (AnswerService). The field says so rather than leave Other out:
            // a client shows it disabled, with why (#510 review, Trust on 5c1d5147; #908). Typed
            // blind, as agent-ha-bridge did, the words reached that box.
            if (fields is [{ Type: FieldType.Choice } only]) { fields = [only with { OtherUnavailable = AnswerReasons.OtherNotSendable }]; }
            // A field the CLI draws as something other than its schema says, or that cannot be
            // told, is never typed into, so a client shows the question to be answered at the
            // machine rather than offer what the host would then refuse (#662).
            atMachine = !drivable || !Drivable(schema);
        }
        // What the host does not type, listed for the machine rather than offered with a box or
        // list whose answer is always refused, as CopilotForm.Read refuses it, in its order: the
        // older format (`choices` present at all, an empty list or one beside a schema included),
        // then a question with no fields once its schema is read: free text, with no form on
        // screen to hold its keys to, or a schema that is empty, not an object, or whose
        // properties are none of them objects (#510 review, Copilot 5784910042, 4480874464).
        var shape = Json.Get(arguments, "choices") is not null || fields.Count == 0;
        return (fields, atMachine || shape, shape ? "shape" : null);
    }

    static List<string> ChoiceList(JsonElement? value)
    {
        if (value is not { } v) { return []; }
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString() ?? "";
            try
            {
                using var doc = JsonDocument.Parse(Json.Repaired(System.Text.Encoding.UTF8.GetBytes(text)));
                return ChoiceList(doc.RootElement);
            }
            // Half a surrogate pair is the replacement character (Json.Repaired); a backstop reads text that still can't be decoded as JSON that isn't.
            catch (Exception e) when (e is JsonException or InvalidOperationException) { return string.IsNullOrWhiteSpace(text) ? [] : [text]; }
        }
        if (v.ValueKind != JsonValueKind.Array) { return []; }
        return [.. v.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.GetRawText()).Where(c => !string.IsNullOrWhiteSpace(c))];
    }

    // One Field per schema property, in order. A schema that arrived as text (the model
    // wrote it as a string) is parsed first, once, as CopilotForm.Read parses it: text that
        // parses to more text is no form (#510 review, Trust on 8335427d).
        public static IReadOnlyList<Field> SchemaFields(JsonElement schema) => SchemaForm(schema, "").Fields;

    // The fields, and whether every one reads as one input Copilot CLI is known to draw for it
    // with constraints the host can check. Each property is read once, and its patterns share one
    // budget for the whole ask_user: read twice with 250 ms a match, 200 slow patterns stalled the
    // refresh of every session for minutes (#844 review).
    static (IReadOnlyList<Field> Fields, bool Drivable) SchemaForm(JsonElement schema, string question, PatternBudget? budget = null)
    {
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(Json.Repaired(System.Text.Encoding.UTF8.GetBytes(schema.GetString() ?? "")));
                                if (doc.RootElement.ValueKind != JsonValueKind.Object) { return ([], true); }
                                var form = SchemaForm(doc.RootElement, question, budget);
                return (form.Fields, form.Drivable && !Json.IsLossy(doc.RootElement));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { return ([], true); }
        }
        var whole = CopilotFieldConstraints.FormUnchecked(schema) is null;
        if (Json.Obj(schema, "properties") is not { } properties) { return ([], whole); }
        var required = Json.Arr(schema, "required") is { } r
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var patterns = budget ?? new PatternBudget();
        var fields = new List<Field>();
        var drivable = whole;

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) { continue; }
            var read = CopilotFieldSchema.Read(property.Value);
            var (limits, uncheckable) = CopilotFieldConstraints.Read(property.Value, patterns);
            var field = SchemaField(property.Name, property.Value, required.Contains(property.Name), read, limits);
            // A text box opening with only spaces, required or not, is one of these (#844, #921).
            drivable &= read.Problem is null && uncheckable is null && CopilotFieldSchema.PrefillAnswerable(read.Input, property.Value);
            fields.Add(field);
        }
        // An answer over Limits.AnswerRequest is refused whole, so a form whose smallest answer is
        // over it has none a device can send. Measured as that smallest answer (Least: each field
        // an answer must carry, at its smallest) in an envelope at its largest: the session id as
        // long as a host takes one, the real question id, a client's 36-character request id, and
        // progress asked for. Counting the text defaults alone, then those and the number defaults
        // as written, forms a little under the bound were offered and every answer to them refused
        // (#921 reviews). This errs toward the machine: a form it offers has an answer a device
        // can send; one it sends there might have fitted with a shorter session id.
        if (OverAnAnswer(fields, question)) { drivable = false; }
        return (fields, drivable);
    }

    // Whether every field of a schema reads as one input Copilot CLI is known to draw for it,
    // under a heading the host can find on screen and tell from every other field's. A schema
    // sent as text is parsed once, as CopilotForm.Read parses it.
    static bool Drivable(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(schema.GetString() ?? "");
                return doc.RootElement.ValueKind == JsonValueKind.Object && Drivable(doc.RootElement);
            }
            catch (JsonException) { return false; }
        }
        if (Json.Obj(schema, "properties") is not { } properties) { return true; }
        var headings = new List<string?>();
        foreach (var p in properties.EnumerateObject())
        {
            // A property that is not an object is refused too (CopilotForm.Schema), even beside
            // one that is: listed as answerable, that form was refused every time (#510 review,
            // Copilot 4480874464). So is a field with no heading to find on screen, or two whose
            // headings cannot be told apart there (CopilotFormScreen; Trust on 8335427d).
            if (p.Value.ValueKind != JsonValueKind.Object) { return false; }
            var (input, _, problem) = CopilotFieldSchema.Read(p.Value);
            if (problem is not null || CopilotFieldSchema.Label(p.Name, p.Value) is not { } label) { return false; }
            headings.Add(CopilotFieldSchema.Heading(label, input is CopilotFieldSchema.Input.Text or CopilotFieldSchema.Input.Number));
        }
        return CopilotFieldSchema.Alike(headings) is null;
    }
    // Whether the smallest answer (Least) is over Limits.AnswerRequest, added up field by field
    // and stopped as soon as it is. Built whole, each minLength written out and the request then
    // serialised, about 45 bytes of {"type":"string","minLength":5000} cost 16 KiB: 6,000 such
    // fields took 640 MB, and 24,000 took 2.3 GB, on every read of the line (#921 review). Each
    // field is counted as the request would serialise it, its name included, and text of minLength
    // as that many bytes and its quotes, never written out. A required number without a default is
    // counted as one byte: a lower bound, since the nearest-to-zero value can serialize longer
    // than another allowed value. Field names are distinct here, or the form is answered at the
    // machine already (FormUnchecked).
    static bool OverAnAnswer(IReadOnlyList<Field> fields, string question)
    {
        long total = -1;
        foreach (var field in fields)
        {
            if (Least(field) is not { } least) { continue; }
            // The envelope with no fields, `"fields":{}`, counted once there is a field: a form
            // with none an answer must carry is not measured.
            if (total < 0)
            {
                total = ProtocolJson.ByteCount(new AnswerRequest
                {
                    Id = LongestSessionId,
                    Question = question,
                    Request = RequestId,
                    Fields = new Dictionary<string, JsonElement>(),
                    Progress = true,
                });
            }
            else { total += 1; }
            // `"name":value` as the request's dictionary writes it: its braces off.
            total += ProtocolJson.ByteCount(new Dictionary<string, JsonElement>(1) { [field.Name] = least.Value }) - 2 + least.Filler;
            if (total > Limits.AnswerRequest) { return true; }
        }
        return false;
    }

    // A field's smallest value: Value as it is sent, and Filler more characters inside its string
    // that need no escaping, counted and never written out.
    readonly record struct LeastValue(JsonElement Value, long Filler = 0);

    static readonly JsonElement NoText = JsonSerializer.SerializeToElement("");
    static readonly JsonElement NumberLowerBound = JsonSerializer.SerializeToElement(0);

    // Text no answer can carry, past the whole bound however short the rest.
    static readonly LeastValue PastTheBound = new(NoText, Limits.AnswerRequest + 1);

    // The longest session id a host takes (Stops.Check), longer than any agent's: Copilot's and
    // Claude's are UUIDs. A client's request id is a UUID.
    static readonly string LongestSessionId = new('x', 256);
    const string RequestId = "00000000-0000-4000-8000-000000000000";

    // The smallest value a field's answer has to carry, or null for a field an answer can leave
    // out. A text or number box that opens with its default typed in is answered with that
    // default (#662), a number as the number held, since neither client sends it as written:
    // the PWA sends Number(default), written as JavaScript writes it (1e20 goes as
    // 100000000000000000000), and the app a double, as .NET writes it (1E+20); the longer
    // counts. A default spelled long ("1." and 4,090 zeros) is one short number. Every other field
    // with a default is sent too, the PWA's buildAnswer starting from it, and so is every
    // required field: each at its smallest, a choice's shortest option, a list's fewest
    // (minItems), true, or a one-byte lower bound for a number without a default. The number
    // nearest zero need not be shortest when serialized: minimum: 1e20 allows 1e21, sent as 1e+21
    // by the PWA. Text that must match a pattern or format is counted at its minLength
    // all the same: a pattern is not solved for its shortest match, so such a form can be offered
    // and its answer refused as too large, before any key is typed. A default that cannot be read is left out: its
    // field is answered at the machine anyway (PrefillAnswerable), and it cannot be measured.
    static LeastValue? Least(Field field)
    {
        var d = field.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } x
            && !(x.ValueKind == JsonValueKind.String && x.ValueEquals("")) ? x : (JsonElement?)null;
        if (d is null && field.Required != true) { return null; }
        switch (field.Type)
        {
            case FieldType.Text:
                if (d is { } t) { return CopilotFieldSchema.Text(t) is { } words ? new LeastValue(JsonSerializer.SerializeToElement(words)) : null; }
                // Text longer than an answer's (Limits.AnswerText) is refused however it is typed,
                // so a minLength past it has no answer: counted past the whole bound, not written
                // out, which for 2,147,483,647 asked for 4 GiB (#921 review). Text it can carry is
                // counted, as minLength characters needing no escape, and not written out either.
                var length = Math.Max(1, field.Constraints?.MinLength ?? 1);
                return length > Limits.AnswerText ? PastTheBound : new LeastValue(NoText, length);
            case FieldType.Number:
                if (d is not { } n)
                {
                    // Past what a client can hold, or not worked out: no answer known, so counted
                    // past the whole bound.
                    return Nearest(field.Constraints) is { } nearest && double.IsFinite(nearest)
                        ? new LeastValue(NumberLowerBound)
                        : PastTheBound;
                }
                var written = n.ValueKind == JsonValueKind.Number ? n.GetRawText() : CopilotFieldSchema.Text(n)?.Trim();
                return Read(written) is { } number ? new LeastValue(Sent(number)) : null;
            case FieldType.Boolean:
                return new LeastValue(JsonSerializer.SerializeToElement(true));
            case FieldType.Choice:
                return field.Options is { Count: > 0 } choices ? new LeastValue(Shortest(choices).First()) : null;
            case FieldType.Multi:
                var fewest = Math.Max(0, field.Constraints?.MinItems ?? 0);
                return new LeastValue(JsonSerializer.SerializeToElement(field.Options is { } all ? Shortest(all).Take(fewest).ToList() : []));
            default:
                return null;
        }
    }

    // A number as a client sends it: the longer of how JavaScript (the PWA) and .NET (the app)
    // write it.
    static JsonElement Sent(double number)
    {
        var script = CopilotFieldSchema.JavaScriptNumber(number);
        var dotnet = JsonSerializer.Serialize(number, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(script.Length >= dotnet.Length ? script : dotnet);
        return doc.RootElement.Clone();
    }

    static double? Read(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;

    // The number nearest 0 an answer can be, as a client holds it: 0 when the bounds allow it,
    // else the least past a lower bound above 0, or the greatest short of an upper bound below
    // it. Stepped to a multiple of multipleOf, to a whole number for an integer (both: a multiple
    // of their least common multiple, Unit), and past an exclusive bound, exactly (ExactDecimal):
    // {minimum: 1, multipleOf: 123456789} is 123456789, an integer's exclusiveMinimum 9 is 10,
    // not 9, and a multiple of 0.5 that is whole and at least 0.2 is 1 (#921 reviews). Past an
    // exclusive bound with no step, it is the next double, which is all a client can hold there:
    // exclusiveMinimum 0.5 is 0.5000000000000001, not 0.5, which the host refuses. Null for
    // bounds too far apart in scale to step exactly, which no bound a host carries is
    // (CopilotFieldConstraints holds each to a double): no answer is known, and none is counted.
    static double? Nearest(FieldConstraints? c)
    {
        static ExactDecimal? Exact(JsonElement? b) => b is { ValueKind: JsonValueKind.Number } v ? ExactDecimal.Parse(v.GetRawText()) : null;
        var unit = Unit(Exact(c?.MultipleOf) is { Sign: > 0 } s ? s : null, c?.Integer == true);
        var low = Tighter(Exact(c?.Minimum), Exact(c?.ExclusiveMinimum), upper: false);
        var high = Tighter(Exact(c?.Maximum), Exact(c?.ExclusiveMaximum), upper: true);
        if (low is { } l && (l.Value.Sign > 0 || (l.Value.Sign == 0 && l.Open)))
        {
            return Past(l.Value, l.Open, unit);
        }
        if (high is { } h && (h.Value.Sign < 0 || (h.Value.Sign == 0 && h.Open)))
        {
            return -Past(new ExactDecimal(-h.Value.Mantissa, h.Value.Exponent), h.Open, unit);
        }
        return 0;
    }

    // The least number at or past bound (>= 0; past it when open) that is a multiple of unit, as
    // a double; with no unit, the bound, or the next double past it when open.
    static double? Past(ExactDecimal bound, bool open, ExactDecimal? unit)
    {
        if (unit is not { } u)
        {
            var d = Double(bound);
            return open ? Math.BitIncrement(d) : d;
        }
        return Up(bound, open, u) is { } up ? Double(up) : null;
    }

    // What every answer is a multiple of: multipleOf, 1 for an integer, or for both their least
    // common multiple. A step m × 10^-k (k > 0, m not a multiple of 10) and 1 have m / gcd(m, 10^k):
    // 0.5 and 1 have 1, 0.3 and 1 have 3, 1.5 and 1 have 3. Null for neither.
    static ExactDecimal? Unit(ExactDecimal? step, bool whole)
    {
        var one = new ExactDecimal(System.Numerics.BigInteger.One, 0);
        if (step is not { } s) { return whole ? one : null; }
        if (!whole || s.IsInteger) { return s; }
        // A step's exponent is past -1,100 when a host carries it (a double); beyond that, no unit.
        if (s.Exponent < -1_100) { return null; }
        var m = System.Numerics.BigInteger.Abs(s.Mantissa);
        return new ExactDecimal(m / System.Numerics.BigInteger.GreatestCommonDivisor(m, System.Numerics.BigInteger.Pow(10, (int)-s.Exponent)), 0);
    }

    // The tighter of an inclusive and an exclusive bound, and whether it is exclusive.
    static (ExactDecimal Value, bool Open)? Tighter(ExactDecimal? closed, ExactDecimal? open, bool upper)
    {
        if (closed is not { } c) { return open is { } o ? (o, true) : null; }
        if (open is not { } x) { return (c, false); }
        var order = ExactDecimal.Compare(x, c);
        return (upper ? order <= 0 : order >= 0) ? (x, true) : (c, false);
    }

    // The least multiple of unit at or past bound (>= 0; past it when open).
    static ExactDecimal? Up(ExactDecimal bound, bool open, ExactDecimal unit)
    {
        if (Math.Abs(bound.Exponent - unit.Exponent) > 1_000) { return null; }
        var e = Math.Min(bound.Exponent, unit.Exponent);
        var b = bound.Mantissa * System.Numerics.BigInteger.Pow(10, (int)(bound.Exponent - e));
        var u = unit.Mantissa * System.Numerics.BigInteger.Pow(10, (int)(unit.Exponent - e));
        var k = System.Numerics.BigInteger.DivRem(b, u, out var rest);
        if (rest.Sign > 0 || open) { k += 1; }
        return new ExactDecimal(k * u, e);
    }

    static double Double(ExactDecimal x) =>
        double.Parse($"{x.Mantissa.ToString(CultureInfo.InvariantCulture)}E{x.Exponent.ToString(CultureInfo.InvariantCulture)}", NumberStyles.Float, CultureInfo.InvariantCulture);

    static IEnumerable<JsonElement> Shortest(IReadOnlyList<Option> options) =>
        options.Select(o => JsonSerializer.SerializeToElement(o.Value)).OrderBy(ProtocolJson.ByteCount);
    // Each field as the input Copilot CLI draws for it (CopilotFieldSchema), which is not
    // always what its keywords suggest: a number with an `enum` is still a number box, and a
    // string with only `anyOf` a text box.
    static Field SchemaField(string name, JsonElement p, bool required, (CopilotFieldSchema.Input Input, IReadOnlyList<CopilotFieldSchema.Choice> Choices, string? Problem) read, FieldConstraints? limits)
    {
        var (input, choices, _) = read;
        var kind = input switch
        {
            CopilotFieldSchema.Input.Multi => FieldType.Multi,
            CopilotFieldSchema.Input.Choice => FieldType.Choice,
            CopilotFieldSchema.Input.Boolean => FieldType.Boolean,
            CopilotFieldSchema.Input.Number => FieldType.Number,
            _ => FieldType.Text,
        };
        IReadOnlyList<Option>? options = kind is FieldType.Multi or FieldType.Choice && choices.Count > 0
            ? [.. choices.Select(c => new Option(Scalar(c.Value), c.Label is { } label && label != Scalar(c.Value) ? label : null))]
            : null;
        return new Field
        {
            Name = name,
            Title = Written(p, "title"),
            Description = Written(p, "description"),
            Type = kind,
            Options = options,
            Default = Json.Get(p, "default") is { } d ? Json.Detach(d) : null,
            Required = required ? true : null,
            Constraints = limits,
        };
    }

    static string Scalar(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

    // A form's text read strictly, not as Json.Str shows it: half a surrogate pair throws, and
    // FromCopilotAskUser's guard sends the form to the machine, as for one anywhere else in it.
    static string? Written(JsonElement e, string name) => Json.Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    // Claude's AskUserQuestion: { questions: [ { question, header, multiSelect, options:
    // [ { label, description } ] } ] }. Claude records the answer under the question text.
    // Option descriptions carry real content but have no place on an Option, so they are
    // listed under the message, as the old bridge did.
    //
    // Claude's prompt also takes free text ("Type something"), but no field offers it:
    // agent-ha-bridge's only walk to it overshoots the list by two rows and was never run
    // against a real question, and a wrong walk answers in the person's name. A prompt
    // the keys cannot drive at all (no questions, more than the tool's four, or an option
    // past the ninth that a multi-select can only tick by its digit) is answered at the
    // machine, as agent-ha-bridge leaves it to the terminal.
    public static Question FromClaudeAskUserQuestion(string id, JsonElement input, DateTimeOffset askedAt)
    {
        var fields = new List<Field>();
        var questions = new List<string>();
        var notes = new StringBuilder();
        var drivable = true;
        if (Json.Arr(input, "questions") is { } list)
        {
            foreach (var q in list.EnumerateArray())
            {
                var text = Json.Str(q, "question");
                if (string.IsNullOrWhiteSpace(text)) { continue; }
                questions.Add(text);
                var options = new List<Option>();
                if (Json.Arr(q, "options") is { } opts)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        var label = o.ValueKind == JsonValueKind.String ? Json.Text(o) : Json.Str(o, "label");
                        if (string.IsNullOrWhiteSpace(label)) { continue; }
                        options.Add(new Option(label));
                        if (Json.Str(o, "description") is { Length: > 0 } description)
                        {
                            notes.Append("\n- **").Append(label).Append("**: ").Append(description);
                        }
                    }
                }
                var multi = Json.Bool(q, "multiSelect") == true;
                if (options.Count == 0 || (multi && options.Count > 9) || fields.Any(f => f.Name == text)) { drivable = false; }
                fields.Add(new Field
                {
                    Name = text,
                    Title = Json.Str(q, "header"),
                    Description = Json.Str(q, "header") is null ? null : text,
                    Type = multi ? FieldType.Multi : FieldType.Choice,
                    Options = options,
                    Required = true,
                });
            }
        }
        if (fields.Count is 0 or > 4 || Json.IsLossy(input)) { drivable = false; }
        var message = questions.Count == 0 ? "Claude needs an answer." : string.Join("\n\n", questions);
        if (notes.Length > 0) { message += "\n" + notes; }
        return new Question
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = Cut(message),
            Truncated = IsCut(message),
            Fields = fields,
            AskedAt = askedAt,
            AnswerAtMachine = drivable ? null : true,
        };
    }
}
