using System.Text.RegularExpressions;

namespace AgentBridge.Learning;

sealed record Guidance(string Do, string Avoid, string Recognize, string Why);
sealed record Endorsement(string Link, string Rationale);

sealed record Evidence
{
    public string[]? Failures { get; init; }
    public string? Diagnosis { get; init; }
    public string? Fix { get; init; }
    public string[]? Verification { get; init; }
    public string? Exemplar { get; init; }
    public string? Check { get; init; }
    public Endorsement? Endorsement { get; init; }
    public string? Runbook { get; init; }
    public string? SuccessfulRun { get; init; }
    public string? Decision { get; init; }
    public string? ImplementedBy { get; init; }
    public string? Source { get; init; }
}

sealed record Note(
    int Version, string Id, string Kind, string Status, string Category,
    string[] Paths, string Owner, string VerifiedCommit, string[] References,
    DateOnly RecheckBy, string StaleWhen, string[] Effects, Guidance Guidance, Evidence Evidence)
{
    public static Note Parse(string text)
    {
        var note = JsonData.Read<Note>(text);
        note.Validate();
        return note;
    }

    public void Validate()
    {
        if (Version != 1) throw new FormatException("Unsupported note version.");
        Require(Id, "id");
        if (!Regex.IsMatch(Id, @"^K-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
            throw new FormatException("Note id must be K- followed by lowercase words or numbers.");
        if (Kind is not ("gotcha" or "invariant" or "convention" or "pattern" or "procedure" or "decision" or "fact"))
            throw new FormatException($"Unknown note kind '{Kind}'.");
        if (Status is not ("draft" or "approved" or "needs-verification" or "retired" or "replaced"))
            throw new FormatException($"Unknown note status '{Status}'.");
        Require(Category, "category");
        Require(Owner, "owner");
        CommitId(VerifiedCommit);
        if (Paths is not { Length: > 0 } || References is not { Length: > 0 })
            throw new FormatException("A note needs paths and source references.");
        foreach (var path in Paths) Path(path, glob: true);
        foreach (var path in References) Path(path);
        if (Paths.Distinct(StringComparer.Ordinal).Count() != Paths.Length
            || References.Distinct(StringComparer.Ordinal).Count() != References.Length)
            throw new FormatException("Paths and references must not contain duplicates.");
        if (RecheckBy == default) throw new FormatException("A note needs a recheckBy date.");
        Require(StaleWhen, "staleWhen");
        if (Effects is null) throw new FormatException("Effects must be an array, even when empty.");
        foreach (var effect in Effects) Require(effect, "effects");
        if (Guidance is null || Evidence is null) throw new FormatException("A note needs guidance and evidence.");
        foreach (var value in new[] { Guidance.Do, Guidance.Avoid, Guidance.Recognize, Guidance.Why })
            Require(value, "guidance");
        var words = Regex.Matches(string.Join(" ", Guidance.Do, Guidance.Avoid, Guidance.Recognize, Guidance.Why), @"\S+").Count;
        if (words > 150) throw new FormatException("Guidance exceeds 150 words.");

        switch (Kind)
        {
            case "gotcha":
            case "invariant":
                Links(Evidence.Failures, "failures");
                Require(Evidence.Diagnosis, "diagnosis");
                if (!Evidence.Failures!.Any(f => Evidence.Diagnosis!.Contains(f, StringComparison.Ordinal)))
                    throw new FormatException("Diagnosis must cite one of the failure links.");
                Link(Evidence.Fix, "fix");
                Links(Evidence.Verification, "verification");
                break;
            case "convention":
            case "pattern":
                Link(Evidence.Exemplar, "exemplar");
                if (Evidence.Check is null) Endorsed(Evidence.Endorsement);
                else Link(Evidence.Check, "check");
                break;
            case "procedure":
                Link(Evidence.Runbook, "runbook");
                Link(Evidence.SuccessfulRun, "successfulRun");
                break;
            case "decision":
                Link(Evidence.Decision, "decision");
                Link(Evidence.ImplementedBy, "implementedBy");
                break;
            case "fact":
                Link(Evidence.Source, "source");
                Endorsed(Evidence.Endorsement);
                break;
        }
    }

    public string Render() =>
        $"[{Id}] {Kind}; scope: {string.Join(", ", Paths)}\n" +
        $"Do: {Guidance.Do}\nAvoid: {Guidance.Avoid}\nRecognize: {Guidance.Recognize}\nWhy: {Guidance.Why}\n" +
        $"Effects: {(Effects.Length == 0 ? "none declared" : string.Join("; ", Effects))}";

    internal static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
            throw new FormatException($"Invalid or missing {field}.");
    }

    internal static void CommitId(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant))
            throw new FormatException("Use a full lowercase Git object id, never a branch, tag or abbreviated SHA.");
    }

    internal static void Path(string path, bool glob = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') || path.Contains(':')
            || path.Any(char.IsControl) || path.Split('/').Any(p => p is "" or "." or "..")
            || (!glob && path.IndexOfAny(['*', '?', '[', ']']) >= 0)
            || (glob && path.IndexOfAny(['[', ']', '{', '}']) >= 0))
            throw new FormatException("Paths must be repository-relative with forward slashes; globs support only *, ** and ?.");
    }

    static void Link(string? value, string field)
    {
        Require(value, field);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0)
            throw new FormatException($"{field} must be an HTTPS evidence URL without credentials or a query string.");
    }

    static void Links(string[]? values, string field)
    {
        if (values is not { Length: > 0 }) throw new FormatException($"Missing evidence: {field}.");
        foreach (var value in values) Link(value, field);
    }

    static void Endorsed(Endorsement? endorsement)
    {
        if (endorsement is null) throw new FormatException("Missing owner endorsement.");
        Link(endorsement.Link, "endorsement.link");
        Require(endorsement.Rationale, "endorsement.rationale");
    }
}
