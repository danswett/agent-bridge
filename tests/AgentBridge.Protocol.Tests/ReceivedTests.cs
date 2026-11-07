using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

// A host record as a client reads it (Received; v1-sessions.md "Malformed records", #446). A field
// it cannot read is absent and a required one missing takes its default, as the PWA has always
// read them; but a record is never filed under a guessed id, and nothing a person decides on is
// guessed at or shown with a part left out.
public class ReceivedTests
{
    const string T = "2026-10-28T04:22:43.182Z";

    static JsonObject Session() => JsonNode.Parse($$"""
        {"id":"s1","agent":"copilot","title":"Fix the build","status":"waiting","statusSince":"{{T}}","updatedAt":"{{T}}","processId":4120,
         "activity":{"response":"Done.","currentTool":{"id":"t1","name":"powershell","status":"running","startedAt":"{{T}}"},
                     "history":[{"id":"t0","name":"edit","status":"done","startedAt":"{{T}}"}]},
         "question":{"id":"q1","kind":"approval","message":"Allow **Run a command**?","askedAt":"{{T}}","agent":"a1",
                     "fields":[{"name":"decision","type":"choice","options":[{"value":"yes","label":"Approve once"},{"value":"no","label":"Deny"}]}]},
         "subagents":[{"id":"a1","kind":"background","status":"waiting","name":"Run the suite"},{"id":"a2","kind":"task","status":"working"}]}
        """)!.AsObject();

    static Session Read(JsonNode node) => Received.Read<Session>(JsonSerializer.SerializeToElement(node));

    static string Json<T>(T value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    [Fact]
    public void A_well_formed_record_reads_exactly_as_the_strict_reader_reads_it()
    {
        var node = Session();
        Assert.Equal(Json(node.Deserialize<Session>(ProtocolJson.Options)), Json(Read(node)));
    }

    [Theory]
    [InlineData("sessions-list.json")]
    [InlineData("session-subagents.json")]
    public void Every_example_in_the_spec_reads_as_the_strict_reader_reads_it(string file)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", file)))!;
        var sessions = doc["sessions"]?.AsArray() is { } list ? [.. list.Select(s => s!)] : new[] { doc["result"] ?? doc };
        foreach (var s in sessions)
        {
            Assert.Equal(Json(s.Deserialize<Session>(ProtocolJson.Options)), Json(Read(s)));
        }
    }

    [Fact]
    public void A_session_missing_a_required_field_other_than_its_id_is_kept_with_that_fields_default()
    {
        var node = Session();
        node.Remove("status");
        node.Remove("statusSince");
        node.Remove("activity");
        node.Remove("agent");

        var s = Read(node);

        Assert.Equal(("s1", SessionStatus.Idle, AgentKind.Unknown), (s.Id, s.Status, s.Agent));
        Assert.Equal(DateTimeOffset.MinValue, s.StatusSince);
        Assert.Null(s.Activity.Response);
        Assert.NotNull(s.Question);
    }

    [Fact]
    public void A_field_of_the_wrong_type_reads_as_absent()
    {
        var node = Session();
        node["title"] = 5;
        node["processId"] = "4120";
        node["status"] = 1;
        node["updatedAt"] = "yesterday";
        node["activity"]!["response"] = new JsonObject();
        node["subagents"] = "none";

        var s = Read(node);

        Assert.Equal((null, null, SessionStatus.Idle, DateTimeOffset.MinValue), (s.Title, s.ProcessId, s.Status, s.UpdatedAt));
        Assert.Null(s.Activity.Response);
        Assert.Null(s.Subagents);
        Assert.Equal("powershell", s.Activity.CurrentTool!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public void A_session_whose_id_is_missing_or_not_a_string_is_refused(int? id)
    {
        var node = Session();
        if (id is { } n) { node["id"] = n; } else { node.Remove("id"); }

        var e = Assert.Throws<MalformedRecordException>(() => Read(node));
        Assert.Equal("$.id", e.Path);
    }

    [Fact]
    public void A_tool_call_or_delegate_with_no_id_is_left_out_and_the_session_kept()
    {
        var node = Session();
        node["activity"]!["currentTool"]!.AsObject().Remove("id");
        node["activity"]!["history"]!.AsArray().Add(new JsonObject { ["name"] = "orphan", ["status"] = "done", ["startedAt"] = T });
        node["subagents"]![1]!["id"] = 2;

        var s = Read(node);

        Assert.Null(s.Activity.CurrentTool);
        Assert.Equal(["t0"], s.Activity.History!.Select(t => t.Id));
        Assert.Equal(["a1"], s.Subagents!.Select(a => a.Id));
    }

    [Fact]
    public void A_list_entry_that_is_null_or_of_the_wrong_type_is_left_out()
    {
        var node = Session();
        node["subagents"]!.AsArray().Add(null);
        node["subagents"]!.AsArray().Add("a3");

        Assert.Equal(["a1", "a2"], Read(node).Subagents!.Select(a => a.Id));
    }

    // Read as absent, or as fewer agents, a hold the host still has read as lifted: the snapshot
    // ended and the app took a held agent's sessions as gone.
    [Theory]
    [InlineData("""{"agents":["copilot"]}""")]
    [InlineData("""{"reason":5,"agents":["copilot"]}""")]
    [InlineData("""{"reason":"busy","agents":"copilot"}""")]
    [InlineData("""{"reason":"busy","agents":["copilot",3]}""")]
    [InlineData("""{"reason":"busy","agents":["copilot",null]}""")]
    public void A_discovery_hold_that_cannot_be_read_refuses_the_message_that_holds_it(string hold)
    {
        var held = JsonNode.Parse(hold)!;

        Assert.Throws<MalformedRecordException>(() => Received.Read<SessionsSynced>(JsonSerializer.SerializeToElement(new JsonObject { ["revision"] = 3, ["discoveryHeld"] = held.DeepClone() })));
        Assert.Throws<MalformedRecordException>(() => Received.Read<DiscoveryChanged>(JsonSerializer.SerializeToElement(new JsonObject { ["discoveryHeld"] = held.DeepClone() })));
    }

    [Fact]
    public void A_default_holding_half_a_surrogate_pair_is_left_out_and_its_session_kept()
    {
        var node = Session();
        node["question"]!["fields"]![0]!["default"] = "PLACEHOLDER";

        var s = ReadText(node, "\"PLACEHOLDER\"", "\"yes \\uD800\"");

        Assert.Equal("s1", s.Id);
        Assert.Null(s.Question!.Fields[0].Default);
    }

    // An approval's id answers it, its message is what it allows, its kind and fields how it is
    // answered and with what, answerAtMachine and truncated whether it may be from here, and agent
    // who asks (#263). None of them is guessed: the question is dropped, the session kept.
    [Theory]
    [InlineData("id", null)]
    [InlineData("id", 5)]
    [InlineData("kind", null)]
    [InlineData("kind", 1)]
    [InlineData("message", null)]
    [InlineData("message", 2)]
    [InlineData("agent", 3)]
    [InlineData("answerAtMachine", "no")]
    [InlineData("truncated", 0)]
    [InlineData("fields", "decision")]
    public void A_question_whose_decision_or_who_asks_cannot_be_read_is_dropped_and_the_session_kept(string field, object? value)
    {
        var node = Session();
        var q = node["question"]!.AsObject();
        if (value is null) { q.Remove(field); } else { q[field] = JsonValue.Create(value); }

        var s = Read(node);

        Assert.Null(s.Question);
        Assert.Equal(("s1", SessionStatus.Waiting), (s.Id, s.Status));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("options")]
    [InlineData("allowOther")]
    [InlineData("option")]
    [InlineData("null-option")]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("label")]
    public void A_question_with_a_field_or_choice_it_cannot_read_is_dropped_whole_rather_than_shown_without_it(string broken)
    {
        var node = Session();
        var field = node["question"]!["fields"]![0]!.AsObject();
        switch (broken)
        {
            case "name": field.Remove("name"); break;
            case "type": field["type"] = 3; break;
            case "options": field["options"] = "yes,no"; break;
            case "allowOther": field["allowOther"] = "true"; break;
            case "option": field["options"]![1]!["value"] = false; break;
            case "null-option": field["options"]!.AsArray().Add(null); break;
            // The words a choice is asked with: never shown as a bare key or value instead.
            case "title": field["title"] = 5; break;
            case "description": field["description"] = new JsonArray(); break;
            case "label": field["options"]![0]!["label"] = true; break;
        }

        Assert.Null(Read(node).Question);
    }

    [Fact]
    public void A_field_or_choice_with_no_title_description_or_label_at_all_is_still_shown()
    {
        var node = Session();
        var field = node["question"]!["fields"]![0]!.AsObject();
        field.Remove("title");
        field.Remove("description");
        field["options"]![0]!.AsObject().Remove("label");

        var q = Read(node).Question!;

        Assert.Equal((null, "yes", "Deny"), (q.Fields[0].Options![0].Label, q.Fields[0].Options![0].Value, q.Fields[0].Options![1].Label));
    }

    // "\uD800" is valid JSON that .NET cannot decode: GetString throws InvalidOperationException,
    // which escaped the cleaner, and the launch page (catching JsonException) was left loading.
    static Session ReadText(JsonNode node, string replace, string with) =>
        Received.Read<Session>(JsonDocument.Parse(node.ToJsonString().Replace(replace, with, StringComparison.Ordinal)).RootElement);

    [Fact]
    public void Text_holding_half_a_surrogate_pair_reads_as_absent()
    {
        var s = ReadText(Session(), "\"Fix the build\"", "\"Fix \\uD800 the build\"");

        Assert.Equal(("s1", (string?)null), (s.Id, s.Title));
        Assert.NotNull(s.Question);
    }

    [Fact]
    public void Text_holding_half_a_surrogate_pair_in_a_strict_field_refuses_its_record()
    {
        Assert.Null(ReadText(Session(), "\"Approve once\"", "\"Approve \\uDC00 once\"").Question);
        var e = Assert.Throws<MalformedRecordException>(() => ReadText(Session(), "\"id\":\"s1\"", "\"id\":\"s\\uD800\""));
        Assert.Equal("$.id", e.Path);
    }

    [Fact]
    public void A_property_name_holding_half_a_surrogate_pair_is_no_field_of_the_record()
    {
        var s = ReadText(Session(), "\"processId\"", "\"process\\uD800Id\"");

        Assert.Equal(("s1", (int?)null), (s.Id, s.ProcessId));
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("future")]
    [InlineData(null)]
    public void An_at_machine_reason_is_optional_display_text_and_does_not_change_the_hold(string? reason)
    {
        var node = Session();
        node["question"]!["answerAtMachine"] = true;
        node["question"]!["answerAtMachineReason"] = reason;

        var q = Read(node).Question!;
        Assert.True(q.AnswerAtMachine);
        Assert.Equal(reason, q.AnswerAtMachineReason);
        node["question"]!["answerAtMachineReason"] = new JsonArray();
        Assert.True(Read(node).Question!.AnswerAtMachine);
        Assert.Null(Read(node).Question!.AnswerAtMachineReason);
    }

    [Fact]
    public void An_unknown_question_kind_still_reads_as_a_form_as_the_spec_says()
    {
        var node = Session();
        node["question"]!["kind"] = "survey";

        Assert.Equal(QuestionKind.Form, Read(node).Question!.Kind);
    }

    [Fact]
    public void A_missing_outcome_reads_as_the_enums_unknown_value_never_as_its_first()
    {
        var empty = JsonSerializer.SerializeToElement(new JsonObject());

        Assert.Equal(AnswerOutcome.Unconfirmed, Received.Read<AnswerResult>(empty).Outcome);
        Assert.Equal(LaunchOutcome.Unconfirmed, Received.Read<LaunchResult>(empty).Outcome);
        Assert.Equal(TrustAnswerOutcome.Unconfirmed, Received.Read<TrustAnswerResult>(empty).Outcome);
        Assert.Equal(UpdateOutcome.Failed, Received.Read<UpdateState>(JsonSerializer.SerializeToElement(new JsonObject { ["last"] = new JsonObject { ["at"] = T } })).Last!.Outcome);
    }

    static JsonObject Options() => JsonNode.Parse($$"""
        {"agents":[{"id":"copilot","label":"Copilot","kind":"copilot"},{"label":"no id","kind":"claude"}],
         "workspaces":[{"label":"repo","path":"C:\\repo"},{"path":"C:\\nolabel"}],
         "resumable":[{"id":"r1","agent":"copilot","launcher":"copilot","title":"Old"},{"id":"r2","agent":"codex","title":"no launcher"}],
         "defaults":{"agent":"copilot","workspace":"repo","permissions":"ask"},
         "trust":[{"id":"tq1","agent":"copilot","kind":"copilot","label":"Copilot","folder":"C:\\repo","askedAt":"{{T}}","answerable":true}]}
        """)!.AsObject();

    static LaunchOptions ReadOptions(JsonNode node) => Received.Read<LaunchOptions>(JsonSerializer.SerializeToElement(node));

    [Fact]
    public void Launch_options_leave_out_an_agent_workspace_or_resumable_session_with_nothing_to_launch_it_by()
    {
        var o = ReadOptions(Options());

        Assert.Equal(["copilot"], o.Agents.Select(a => a.Id));
        Assert.Equal(["repo"], o.Workspaces.Select(w => w.Label));
        Assert.Equal(["r1"], o.Resumable.Select(r => r.Id));
        Assert.Equal("tq1", Assert.Single(o.Trust!).Id);
    }

    [Theory]
    [InlineData("folder", null)]
    [InlineData("folder", 1)]
    [InlineData("answerable", null)]
    [InlineData("answerable", "yes")]
    [InlineData("agent", 2)]
    [InlineData("warning", 3)]
    [InlineData("root", false)]
    public void A_folder_trust_question_a_person_could_not_read_in_full_is_dropped(string field, object? value)
    {
        var node = Options();
        var q = node["trust"]![0]!.AsObject();
        if (value is null) { q.Remove(field); } else { q[field] = JsonValue.Create(value); }

        Assert.Empty(ReadOptions(node).Trust!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Launch_options_whose_default_permissions_cannot_be_read_are_refused_rather_than_guessed(int? permissions)
    {
        var node = Options();
        var d = node["defaults"]!.AsObject();
        if (permissions is { } p) { d["permissions"] = p; } else { d.Remove("permissions"); }

        Assert.Throws<MalformedRecordException>(() => ReadOptions(node));
    }

    [Fact]
    public void A_usage_window_with_no_percent_is_left_out_and_its_reading_kept()
    {
        var report = JsonNode.Parse($$"""
            {"agents":[{"agent":"copilot","label":"Copilot","source":"api","measuredAt":"{{T}}",
              "windows":[{"key":"month","label":"Month","percent":40},{"key":"week","label":"Week"},{"key":"day","label":"Day","percent":"10%"}]},
             {"agent":"claude","windows":[]}]}
            """)!;

        var r = Received.Read<UsageReport>(JsonSerializer.SerializeToElement(report));

        Assert.Equal(["month"], r.Agents[0].Windows.Select(w => w.Key));
        Assert.Equal((AgentKind.Claude, "", UsageSource.None, DateTimeOffset.MinValue), (r.Agents[1].Agent, r.Agents[1].Label, r.Agents[1].Source, r.Agents[1].MeasuredAt));
    }

    // Read as absent, a prompt a schedule or an agent sent would be shown as the person's own (#489).
    [Fact]
    public void A_prompt_whose_sender_cannot_be_read_is_not_shown_and_an_unknown_sender_is_never_the_person()
    {
        var node = Session();
        node["activity"]!["userMessage"] = "Carry on.";
        node["activity"]!["userMessageFrom"] = 4;
        Assert.Null(Read(node).Activity.UserMessage);

        node["activity"]!["userMessageFrom"] = "robot";
        Assert.Equal(("Carry on.", UserMessageFrom.Other), (Read(node).Activity.UserMessage, Read(node).Activity.UserMessageFrom));
    }

    // Read as absent, a reading another machine took was filed as the passing host's own, past the
    // reader's own forget and roster filtering.
    [Theory]
    [InlineData("""{"id":5,"name":"Laptop"}""")]
    [InlineData("""{"name":"Laptop"}""")]
    [InlineData("\"laptop-id\"")]
    public void A_reading_whose_machine_cannot_be_read_is_left_out_rather_than_filed_as_the_hosts(string readBy)
    {
        var report = JsonNode.Parse($$"""
            {"agents":[{"agent":"copilot","label":"Copilot","source":"api","measuredAt":"{{T}}","windows":[]},
                       {"agent":"claude","label":"Claude","source":"api","measuredAt":"{{T}}","windows":[],"readBy":{{readBy}}}]}
            """)!;

        var r = Received.Read<UsageReport>(JsonSerializer.SerializeToElement(report));

        Assert.Equal([AgentKind.Copilot], r.Agents.Select(a => a.Agent));
    }

    [Fact]
    public void A_record_read_past_a_field_says_it_was_not_read_exactly()
    {
        Received.Read<Session>(JsonSerializer.SerializeToElement(Session()), out var exact);
        Assert.True(exact);

        foreach (var change in new Action<JsonObject>[] { s => s.Remove("status"), s => s["title"] = 5, s => s["subagents"]!.AsArray().Add(null) })
        {
            var node = Session();
            change(node);
            Received.Read<Session>(JsonSerializer.SerializeToElement(node), out exact);
            Assert.False(exact);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"s1\"")]
    [InlineData("null")]
    public void A_message_that_is_not_a_record_at_all_is_refused(string json) =>
        Assert.Throws<MalformedRecordException>(() => Received.Read<Session>(JsonDocument.Parse(json).RootElement));

    // Read as "", a session with no id would be offered to resume under no session at all (#780).
    [Fact]
    public void A_session_that_went_with_the_host_and_has_no_id_it_can_be_resumed_by_is_not_offered()
    {
        var notice = JsonNode.Parse($$"""
            {"at":"{{T}}","cause":"reboot","why":"pending",
             "offered":[{"agent":"copilot","title":"No id"},{"id":42,"agent":"claude"},{"id":"s1","agent":"copilot","title":"Fix the build"}]}
            """)!;

        var n = Received.Read<RestoreNotice>(JsonSerializer.SerializeToElement(notice));

        Assert.Equal(["s1"], n.Offered.Select(o => o.Id));
        Assert.Throws<MalformedRecordException>(() => Received.Read<RestoreSession>(JsonDocument.Parse("""{"agent":"copilot"}""").RootElement));
    }

    // The spec's table of strict fields and the [Strict] marks are one list: a field marked here and
    // not there, or there and not here, or in the wrong column, fails.
    [Fact]
    public void The_specs_table_of_strict_fields_is_exactly_the_fields_marked_strict()
    {
        var spec = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "spec", "v1-sessions.md"));
        var start = Array.IndexOf(spec, "## Malformed records");
        var rows = spec.Skip(start).TakeWhile((l, i) => i == 0 || !l.StartsWith("## ", StringComparison.Ordinal))
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('|'))
            .ToList();
        static IEnumerable<string> Names(string cell) => System.Text.RegularExpressions.Regex.Matches(cell, "`([^`]+)`").Select(m => m.Groups[1].Value);
        var documented = rows.SelectMany(r => Names(r[1]).SelectMany(record =>
            Names(r[2]).Select(f => $"{record}.{f} Identity").Concat(Names(r[3]).Select(f => $"{record}.{f} Security")))).ToHashSet();

        var marked = typeof(Received).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Property: p, Strict: p.GetCustomAttributes(typeof(StrictAttribute), false).OfType<StrictAttribute>().SingleOrDefault())))
            .Where(x => x.Strict is not null)
            .Select(x => $"{(x.Type == typeof(SessionRemoved) ? "session.remove" : x.Type.Name)}.{JsonNamingPolicy.CamelCase.ConvertName(x.Property.Name)} {x.Strict!.Why}")
            .ToHashSet();

        Assert.Empty(marked.Except(documented));
        Assert.Empty(documented.Except(marked));
    }
}
