using System.Text.Json;

namespace AgentBridge.Learning.Tests;

public class NoteTests
{
    [Fact]
    public void A_complete_gotcha_round_trips_without_changing_its_evidence()
    {
        var note = Note.Parse(JsonData.Write(Fixture.Note()));
        Assert.Equal(Fixture.Link, Assert.Single(note.Evidence.Failures!));
        Assert.Equal("gotcha", note.Kind);
    }

    [Fact]
    public void A_failure_link_alone_is_not_a_lesson()
    {
        var note = Fixture.Note() with { Evidence = new Evidence { Failures = [Fixture.Link] } };
        Assert.Throws<FormatException>(() => note.Validate());
    }

    [Theory]
    [InlineData("diagnosis")]
    [InlineData("fix")]
    [InlineData("verification")]
    public void Every_link_in_the_corrective_chain_is_required(string missing)
    {
        var note = Fixture.Note();
        note = note with
        {
            Evidence = missing switch
            {
                "diagnosis" => note.Evidence with { Diagnosis = "An unsupported explanation." },
                "fix" => note.Evidence with { Fix = null },
                _ => note.Evidence with { Verification = [] },
            },
        };
        Assert.Throws<FormatException>(() => note.Validate());
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("convention")]
    [InlineData("procedure")]
    [InlineData("decision")]
    [InlineData("fact")]
    public void Each_kind_accepts_its_own_evidence_and_rejects_an_empty_shape(string kind)
    {
        var evidence = kind switch
        {
            "pattern" or "convention" => new Evidence { Exemplar = Fixture.Link, Check = Fixture.Link },
            "procedure" => new Evidence { Runbook = Fixture.Link, SuccessfulRun = Fixture.Link },
            "decision" => new Evidence { Decision = Fixture.Link, ImplementedBy = Fixture.Link },
            _ => new Evidence { Source = Fixture.Link, Endorsement = new(Fixture.Link, "Confirmed against the implementation.") },
        };
        var note = Fixture.Note() with { Kind = kind, Evidence = evidence };
        note.Validate();
        Assert.Throws<FormatException>(() => (note with { Evidence = new() }).Validate());
    }

    [Fact]
    public void An_endorsement_needs_a_rationale_not_just_a_link()
    {
        var note = Fixture.Note() with
        {
            Kind = "pattern",
            Evidence = new() { Exemplar = Fixture.Link, Endorsement = new(Fixture.Link, "") },
        };
        Assert.Throws<FormatException>(() => note.Validate());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("src\\file.cs")]
    [InlineData("src/./file.cs")]
    [InlineData("src/[ab].cs")]
    [InlineData(":(glob)**")]
    public void Ambiguous_or_escaping_scope_paths_are_rejected(string path)
    {
        Assert.Throws<FormatException>(() => (Fixture.Note() with { Paths = [path] }).Validate());
    }

    [Theory]
    [InlineData("main")]
    [InlineData("HEAD")]
    [InlineData("1111111")]
    public void Moving_refs_cannot_stand_in_for_verification_commits(string reference)
    {
        Assert.Throws<FormatException>(() => (Fixture.Note() with { VerifiedCommit = reference }).Validate());
    }

    [Fact]
    public void Guidance_allows_150_words_but_not_151()
    {
        var note = Fixture.Note() with { Guidance = new(string.Join(' ', Enumerable.Repeat("word", 147)), "avoid", "recognize", "why") };
        note.Validate();
        Assert.Throws<FormatException>(() => (note with { Guidance = note.Guidance with { Do = note.Guidance.Do + " extra" } }).Validate());
    }

    [Theory]
    [InlineData("http://example.test/run/1")]
    [InlineData("https://user:password@example.test/run/1")]
    [InlineData("https://example.test/run/1?token=credential")]
    [InlineData("file:///private/log")]
    public void Evidence_links_cannot_be_local_paths_or_credential_bearing_urls(string url)
    {
        var note = Fixture.Note();
        Assert.Throws<FormatException>(() => (note with { Evidence = note.Evidence with { Fix = url } }).Validate());
    }

    [Fact]
    public void Unknown_duplicate_and_null_required_fields_are_not_silently_accepted()
    {
        var json = JsonData.Write(Fixture.Note());
        Assert.Throws<JsonException>(() => Note.Parse(json.Replace("\"version\": 1", "\"version\": 1, \"approve\": true")));
        Assert.Throws<FormatException>(() => Note.Parse(json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1")));
        Assert.Throws<JsonException>(() => Note.Parse(json.Replace("\"owner\": \"fixture-owner\"", "\"owner\": null")));
        Assert.Throws<FormatException>(() => Note.Parse(json.Replace("\"src/**/*.cs\"", "null")));
    }

    [Fact]
    public void The_shipped_seed_is_valid_but_remains_a_draft()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "AgentBridge.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var file = System.IO.Path.Combine(directory.FullName, GitSnapshot.NotesFolder, "K-app-caption-paint.json");
        var output = new StringWriter();
        Assert.Equal(0, Program.Run(["validate", "--note", file], output));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("draft", report.RootElement.GetProperty("status").GetString());
        Assert.False(report.RootElement.GetProperty("approvalVerified").GetBoolean());
    }
}
