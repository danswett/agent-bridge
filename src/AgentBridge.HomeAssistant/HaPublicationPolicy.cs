using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBridge.HomeAssistant;

internal enum HaPublicationComponent { Card, Render }

// Protocol 1 in agent-ha-bridge's decision-ha-websocket.ps1. This is a fence, not a
// distributed lease: a future handover must stop the previous writer before changing it.
internal sealed class HaPublicationPolicy
{
    internal const string ResourcePrefix = "data:text/javascript;base64,ZXhwb3J0IHt9Ow==#agent-bridge-publication-policy.js?policy=";
    const int MaxPayload = 16384;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly string[] Members = ["protocol", "authority", "writer", "generation", "dashboard", "mode", "card", "render", "highCard", "highRender", "legacyCard"];

    public string Authority { get; }
    public string Writer { get; }
    public int Generation { get; }
    public string Dashboard { get; }
    public bool Pinned { get; }
    public HaPublicationArtifact Card { get; }
    public HaPublicationArtifact Render { get; }
    public HaPublicationArtifact HighCard { get; }
    public HaPublicationArtifact HighRender { get; }
    public string LegacyCard { get; }

    HaPublicationPolicy(JsonElement json, string dashboard)
    {
        ExactMembers(json, Members, "publication policy");
        var protocolValue = json.GetProperty("protocol");
        if (protocolValue.ValueKind != JsonValueKind.Number || !protocolValue.TryGetInt32(out var protocol) || protocol != 1)
        {
            throw new InvalidDataException("Unsupported publication fencing protocol.");
        }
        Authority = Identity(json, "authority");
        Writer = Identity(json, "writer");
        var generationValue = json.GetProperty("generation");
        if (generationValue.ValueKind != JsonValueKind.Number || !generationValue.TryGetInt32(out var generation) || generation < 1)
        {
            throw new InvalidDataException("Publication generation must be a positive 32-bit integer.");
        }
        Generation = generation;
        Dashboard = Text(json, "dashboard");
        if (Dashboard != dashboard) { throw new InvalidDataException("Publication policy names a different dashboard."); }
        var mode = Text(json, "mode");
        if (mode is not ("advance" or "pin")) { throw new InvalidDataException("Publication mode must be advance or pin."); }
        Pinned = mode == "pin";
        LegacyCard = Text(json, "legacyCard");
        if (LegacyCard.Length != 0 && !IsHash(LegacyCard)) { throw new InvalidDataException("Invalid legacy card digest."); }
        Card = HaPublicationArtifact.Read(json.GetProperty("card"));
        Render = HaPublicationArtifact.Read(json.GetProperty("render"));
        HighCard = HaPublicationArtifact.Read(json.GetProperty("highCard"));
        HighRender = HaPublicationArtifact.Read(json.GetProperty("highRender"));
        foreach (var (target, high) in new[] { (Card, HighCard), (Render, HighRender) })
        {
            if (target.Version.CompareTo(high.Version) > 0 || (!Pinned && target != high))
            {
                throw new InvalidDataException("Publication high-water fence is inconsistent with its target.");
            }
        }
    }

    public static HaPublicationPolicy Read(JsonElement json, string dashboard) => new(json, dashboard);

    public static HaPublicationPolicy ReadResource(string url, string dashboard)
    {
        if (!url.StartsWith(ResourcePrefix, StringComparison.Ordinal) || url.Length - ResourcePrefix.Length > MaxPayload)
        {
            throw new InvalidDataException("Malformed or oversized publication policy resource; restore the established policy.");
        }
        try
        {
            var bytes = Convert.FromBase64String(url[ResourcePrefix.Length..]);
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 10 });
            return Read(document.RootElement, dashboard);
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException or JsonException)
        {
            throw new InvalidDataException("Unreadable publication policy resource; it is not an absent policy.", e);
        }
    }

    // Ordinally sorted keys reproduce ConvertTo-BridgePublicationJson. Policy strings
    // are ASCII by validation; hashes do not depend on a serializer's Unicode escaping.
    public string CanonicalJson => ToJson().ToJsonString();
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));
    public string ResourceUrl => ResourcePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(CanonicalJson));

    public JsonObject ToJson() => new()
    {
        ["authority"] = Authority,
        ["card"] = Card.ToJson(),
        ["dashboard"] = Dashboard,
        ["generation"] = Generation,
        ["highCard"] = HighCard.ToJson(),
        ["highRender"] = HighRender.ToJson(),
        ["legacyCard"] = LegacyCard,
        ["mode"] = Pinned ? "pin" : "advance",
        ["protocol"] = 1,
        ["render"] = Render.ToJson(),
        ["writer"] = Writer,
    };

    // Assert-BridgePublicationProgress. Observers may catch up across several
    // generations, but may not forget a high-water mark or silently adopt another authority.
    public void CheckProgressFrom(HaPublicationPolicy previous)
    {
        if (Authority != previous.Authority || Generation < previous.Generation)
        {
            throw new InvalidDataException("Publication authority or generation conflicts with the established receipt.");
        }
        var sameGeneration = Generation == previous.Generation;
        if (sameGeneration && (Writer != previous.Writer || Pinned != previous.Pinned))
        {
            throw new InvalidDataException("Changing the publication writer or rollback mode requires a new explicit generation.");
        }
        foreach (var (target, high, oldTarget, oldHigh) in new[]
        {
            (Card, HighCard, previous.Card, previous.HighCard),
            (Render, HighRender, previous.Render, previous.HighRender),
        })
        {
            var comparison = high.Version.CompareTo(oldHigh.Version);
            if (comparison < 0 || (comparison == 0 && high.Hash != oldHigh.Hash))
            {
                throw new InvalidDataException("Publication high-water version/content conflicts with the established receipt.");
            }
            if (!sameGeneration) { continue; }
            comparison = target.Version.CompareTo(oldTarget.Version);
            if ((Pinned && target != oldTarget) || comparison < 0 || (comparison == 0 && target.Hash != oldTarget.Hash))
            {
                throw new InvalidDataException("Publication target changed without bounded rollback authority.");
            }
        }
    }

    // Assert-BridgePublicationWriter. A renderer pinned for rollback must also
    // prove the actual served card, independently of any dashboard receipt.
    public void CheckWriter(string authority, string participant, string writer, int generation,
        HaPublicationComponent component, HaPublicationArtifact artifact, string cardUrl)
    {
        if (component is not (HaPublicationComponent.Card or HaPublicationComponent.Render))
        {
            throw new ArgumentOutOfRangeException(nameof(component));
        }
        if (component == HaPublicationComponent.Render && Pinned)
        {
            if (HaPublicationArtifact.ReadInlineCard(cardUrl) != Card)
            {
                throw new InvalidDataException("Pinned card version/content does not match the exact target; repair is required.");
            }
        }
        if (authority != Authority || writer != Writer || generation != Generation || participant != Writer)
        {
            throw new InvalidDataException("This participant is not the configured writer for the established authority/generation.");
        }
        var target = component == HaPublicationComponent.Card ? Card : Render;
        var comparison = artifact.Version.CompareTo(target.Version);
        if (Pinned && artifact != target)
        {
            throw new InvalidDataException("Authorized rollback pins an exact publication target; automatic writers cannot leave that pin.");
        }
        if (!Pinned && (comparison < 0 || (comparison == 0 && artifact.Hash != target.Hash)))
        {
            throw new InvalidDataException("An older or equal-version conflicting artifact cannot cross the publication fence.");
        }
    }

    internal static void ExactMembers(JsonElement json, IReadOnlyCollection<string> members, string name)
    {
        if (json.ValueKind != JsonValueKind.Object) { throw new InvalidDataException($"The {name} is not an object."); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
        {
            if (!members.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException($"The {name} has unexpected or duplicate members.");
            }
        }
        if (seen.Count != members.Count) { throw new InvalidDataException($"The {name} is incomplete."); }
    }

    internal static string Text(JsonElement json, string name)
    {
        var value = json.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) { throw new InvalidDataException($"Publication {name} is not text."); }
        return value.GetString()!;
    }

    static string Identity(JsonElement json, string name)
    {
        var text = Text(json, name);
        if (text.Length is < 1 or > 80 || !char.IsAsciiLetterOrDigit(text[0]) ||
            text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-')))
        {
            throw new InvalidDataException($"Invalid publication {name} identity.");
        }
        return text;
    }

    internal static bool IsHash(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record HaPublicationArtifact
{
    public string VersionText { get; }
    public Version Version { get; }
    public string Hash { get; }

    HaPublicationArtifact(string versionText, string hash)
    {
        if (versionText.Length > 43 || !Regex.IsMatch(versionText, @"^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(versionText, out var version) || !HaPublicationPolicy.IsHash(hash))
        {
            throw new InvalidDataException("Publication artifact requires an exact numeric version and lowercase SHA256 hash.");
        }
        VersionText = versionText;
        Version = version;
        Hash = hash;
    }

    public static HaPublicationArtifact Read(JsonElement json)
    {
        HaPublicationPolicy.ExactMembers(json, ["version", "hash"], "publication artifact");
        return new(HaPublicationPolicy.Text(json, "version"), HaPublicationPolicy.Text(json, "hash"));
    }

    public JsonObject ToJson() => new() { ["hash"] = Hash, ["version"] = VersionText };

    public static HaPublicationArtifact ReadInlineCard(string url)
    {
        const string prefix = "data:text/javascript;base64,";
        const int maxCardUrl = 4 * 1024 * 1024;
        if (url.Length > maxCardUrl || !url.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pinned card content is missing or unverifiable; repair requires the exact inline artifact.");
        }
        var fragment = url.IndexOf('#', StringComparison.Ordinal);
        if (fragment <= prefix.Length) { throw new InvalidDataException("The registered inline card is malformed."); }
        var match = Regex.Match(url[fragment..], @"[?&]v=([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:&|$)", RegexOptions.CultureInvariant);
        if (!match.Success) { throw new InvalidDataException("The registered card version is unknown."); }
        try
        {
            var bytes = Convert.FromBase64String(url[prefix.Length..fragment]);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var declared = Regex.Match(text, @"CARD_VERSION\s*=\s*'([^']+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!declared.Success || declared.Groups[1].Value != match.Groups[1].Value)
            {
                throw new InvalidDataException("Registered card content and declared version conflict.");
            }
            return new(match.Groups[1].Value, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            throw new InvalidDataException("The registered inline card cannot be verified.", e);
        }
    }
}
