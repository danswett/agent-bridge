// session.patch (docs/protocol/v1-sessions.md, Patches): a live change as only what changed, for a
// client that asked for it on sessions.subscribe. A whole upsert was sent again for any change,
// 21.7 KB for a session whose 16.6 KB of sub-agents had not moved (#597).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Protocol;

// Base: the seq of the event on this channel that last gave the client this session; absent, the
// patch is the whole session. Revision: every change up to it has been sent on this channel. Set:
// an RFC 7396 merge patch of the session as an upsert carries it. Subagents: entries by id. Append:
// text added to the end of fields that grew. Applied in that order: Set, Subagents, Append.
// Every part is strict: a patch read past a part it could not read would leave the client's
// session silently unlike the host's.
public sealed record SessionPatch
{
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    [Strict(Strictness.Identity)]
    public long? Base { get; init; }
    public long? Revision { get; init; }
    [Strict(Strictness.Identity)]
    public JsonElement? Set { get; init; }
    [Strict(Strictness.Identity)]
    public SubagentsPatch? Subagents { get; init; }
    [Strict(Strictness.Identity)]
    public JsonElement? Append { get; init; }
}

// Remove drops entries by id; each of Set merges into the entry with its id, or is added at the
// end; Order, when present, is every id in the order the list then has.
public sealed record SubagentsPatch
{
    [Strict(Strictness.Identity)]
    public IReadOnlyList<JsonElement>? Set { get; init; }
    [Strict(Strictness.Identity)]
    public IReadOnlyList<string>? Remove { get; init; }
    [Strict(Strictness.Identity)]
    public IReadOnlyList<string>? Order { get; init; }
}

// A patch a client cannot apply. It drops it whole and subscribes again, as after a gap in seq.
public sealed class SessionPatchException(string reason) : Exception(reason);

public static class SessionPatches
{
    // Why a patch cannot be applied, word for word as the PWA's applyPatch says it.
    public const string NotOnHeld = "the patch is not on the session as held";
    public const string NotAMerge = "the patch is not a merge of the session";
    public const string NotByIds = "the patch's delegates are not entries by id";

    const string SubagentsKey = "subagents";
    const string IdKey = "id";

    public static JsonObject Json(Session session) => JsonSerializer.SerializeToNode(session, ProtocolJson.Options)!.AsObject();

    // What a host sends a client that holds `sent` (null: none of it on this subscription) from
    // the event numbered sentSeq, for the session now `now`: a patch on it, or the whole session
    // when that is no larger. Null when nothing an upsert carries changed.
    public static SessionPatch? Next(JsonObject? sent, long sentSeq, JsonObject now, long? revision)
    {
        if (sent is null) { return Whole(now, revision); }
        var patch = Diff(sent, sentSeq, now, revision);
        if (patch is null) { return null; }
        // Against the session alone, which the whole patch is a few bytes more than: building that
        // patch only to measure it doubled the cost of every change.
        return ProtocolJson.ByteCount(patch) < ProtocolJson.ByteCount(now) ? patch : Whole(now, revision);
    }

    // The whole session, applied to nothing.
    public static SessionPatch Whole(JsonObject session, long? revision = null) => new()
    {
        Id = IdOf(session),
        Revision = revision,
        Set = JsonSerializer.SerializeToElement(session, ProtocolJson.Options),
    };

    // The patch that takes a client holding `from` (given it by the event numbered fromSeq) to
    // `to`, or null when they are the same.
    public static SessionPatch? Diff(JsonObject from, long fromSeq, JsonObject to, long? revision = null)
    {
        var id = IdOf(to);
        if (IdOf(from) != id) { throw new ArgumentException("a patch is between two forms of one session"); }
        var set = new JsonObject();
        var append = new JsonObject();
        DiffObject(from, to, set, append, root: true);
        var subagents = DiffSubagents(from[SubagentsKey], to[SubagentsKey], set);
        if (set.Count == 0 && append.Count == 0 && subagents is null) { return null; }
        return new SessionPatch
        {
            Id = id,
            Base = fromSeq,
            Revision = revision,
            Set = set.Count > 0 ? JsonSerializer.SerializeToElement(set, ProtocolJson.Options) : null,
            Subagents = subagents,
            Append = append.Count > 0 ? JsonSerializer.SerializeToElement(append, ProtocolJson.Options) : null,
        };
    }

    static string IdOf(JsonObject session) =>
        session[IdKey] is JsonValue v && v.TryGetValue<string>(out var id) ? id : throw new ArgumentException("a session without an id");

    // append is null where text is only ever set whole (inside a sub-agent's entry).
    static void DiffObject(JsonObject from, JsonObject to, JsonObject set, JsonObject? append, bool root)
    {
        foreach (var (key, value) in to)
        {
            if (root && key is SubagentsKey or IdKey) { continue; }
            if (value is null) { continue; }
            var old = from[key];
            if (old is null) { set[key] = value.DeepClone(); continue; }
            if (value is JsonObject vo && old is JsonObject oo)
            {
                var s = new JsonObject();
                var a = append is null ? null : new JsonObject();
                DiffObject(oo, vo, s, a, root: false);
                if (s.Count > 0) { set[key] = s; }
                if (a?.Count > 0) { append![key] = a; }
                continue;
            }
            if (append is not null && Text(old) is { Length: > 0 } was && Text(value) is { } now && now.Length > was.Length
                && now.StartsWith(was, StringComparison.Ordinal) && !char.IsHighSurrogate(was[^1]))
            {
                append[key] = now[was.Length..];
                continue;
            }
            if (!JsonNode.DeepEquals(old, value)) { set[key] = value.DeepClone(); }
        }
        foreach (var (key, value) in from)
        {
            if (root && key is SubagentsKey or IdKey) { continue; }
            if (value is not null && to[key] is null) { set[key] = null; }
        }
    }

    static string? Text(JsonNode node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    static SubagentsPatch? DiffSubagents(JsonNode? from, JsonNode? to, JsonObject set)
    {
        if (JsonNode.DeepEquals(from, to)) { return null; }
        if (to is null)
        {
            set[SubagentsKey] = null;
            return null;
        }
        if (from is not JsonArray old || to is not JsonArray now || now.Count == 0 || Ids(old) is not { } oldIds || Ids(now) is not { } newIds)
        {
            set[SubagentsKey] = to.DeepClone();
            return null;
        }
        var oldById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var i = 0; i < old.Count; i++) { oldById[oldIds[i]] = (JsonObject)old[i]!; }
        var kept = newIds.ToHashSet(StringComparer.Ordinal);
        var remove = oldIds.Where(id => !kept.Contains(id)).ToList();
        var entries = new List<JsonElement>();
        var added = new List<string>();
        for (var i = 0; i < now.Count; i++)
        {
            var entry = (JsonObject)now[i]!;
            if (oldById.TryGetValue(newIds[i], out var was))
            {
                var s = new JsonObject();
                DiffObject(was, entry, s, append: null, root: false);
                if (s.Count == 0) { continue; }
                var patch = new JsonObject { [IdKey] = newIds[i] };
                foreach (var (k, v) in s) { if (k != IdKey) { patch[k] = v?.DeepClone(); } }
                entries.Add(JsonSerializer.SerializeToElement(patch, ProtocolJson.Options));
            }
            else
            {
                entries.Add(JsonSerializer.SerializeToElement(entry, ProtocolJson.Options));
                added.Add(newIds[i]);
            }
        }
        // The order a client comes to without one: the entries it had, less those removed, then the
        // new ones as they were sent. Sent only when the list is in another.
        var natural = oldIds.Where(kept.Contains).Concat(added).ToList();
        var order = natural.SequenceEqual(newIds, StringComparer.Ordinal) ? null : newIds;
        if (remove.Count == 0 && entries.Count == 0 && order is null) { return null; }
        return new SubagentsPatch
        {
            Set = entries.Count > 0 ? entries : null,
            Remove = remove.Count > 0 ? remove : null,
            Order = order,
        };
    }

    // The entries' ids, in order, when every entry is an object with a text id of its own; null
    // otherwise (a list a patch by id cannot be applied to, which is then sent whole).
    static List<string>? Ids(JsonArray list)
    {
        var ids = new List<string>(list.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in list)
        {
            if (e is not JsonObject o || o[IdKey] is not JsonValue v || v.GetValueKind() != JsonValueKind.String) { return null; }
            var id = v.GetValue<string>();
            if (!seen.Add(id)) { return null; }
            ids.Add(id);
        }
        return ids;
    }

    // The session a client holds once it has applied the patch to `held`, the JSON it last
    // received of it, from the event numbered heldSeq (null: it holds none). Held is not changed.
    // Throws SessionPatchException, with the reason, for a patch that cannot be applied; nothing of
    // it is applied then.
    public static JsonObject Apply(JsonObject? held, long? heldSeq, SessionPatch patch)
    {
        JsonObject target;
        if (patch.Base is { } at)
        {
            if (held is null || heldSeq != at || held[IdKey] is not JsonValue hv || !hv.TryGetValue<string>(out var heldId) || heldId != patch.Id)
            {
                throw new SessionPatchException(NotOnHeld);
            }
            target = held.DeepClone().AsObject();
        }
        else { target = []; }
        if (patch.Set is { } set) { Merge(target, Object(set)); }
        if (patch.Subagents is { } subagents) { ApplySubagents(target, subagents); }
        if (patch.Append is { } append) { Append(target, Object(append)); }
        // A whole session names itself; one that names another, or none, is not this patch's.
        if (target[IdKey] is not JsonValue id || !id.TryGetValue<string>(out var name) || name != patch.Id) { throw new SessionPatchException(NotOnHeld); }
        return target;
    }

    static JsonObject Object(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? JsonNode.Parse(e.GetRawText())!.AsObject() : throw new SessionPatchException(NotAMerge);

    // RFC 7396.
    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null) { target.Remove(key); continue; }
            if (value is JsonObject po)
            {
                if (target[key] is not JsonObject to)
                {
                    to = [];
                    target[key] = to;
                }
                Merge(to, po);
                continue;
            }
            target[key] = value.DeepClone();
        }
    }

    static void Append(JsonObject target, JsonObject append)
    {
        foreach (var (key, value) in append)
        {
            if (value is JsonObject inner && target[key] is JsonObject into) { Append(into, inner); continue; }
            if (value is not null && Text(value) is { } more && target[key] is { } was && Text(was) is { } text)
            {
                target[key] = text + more;
                continue;
            }
            throw new SessionPatchException(NotAMerge);
        }
    }

    static void ApplySubagents(JsonObject target, SubagentsPatch patch)
    {
        List<JsonObject> entries = target[SubagentsKey] switch
        {
            null => [],
            JsonArray a when Ids(a) is not null => [.. a.Select(e => e!.DeepClone().AsObject())],
            _ => throw new SessionPatchException(NotByIds),
        };
        if (patch.Remove is { } remove)
        {
            var gone = remove.ToHashSet(StringComparer.Ordinal);
            entries.RemoveAll(e => gone.Contains(e[IdKey]!.GetValue<string>()));
        }
        foreach (var e in patch.Set ?? [])
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(IdKey, out var idValue) || idValue.ValueKind != JsonValueKind.String) { throw new SessionPatchException(NotByIds); }
            var id = idValue.GetString()!;
            var entry = entries.FirstOrDefault(x => x[IdKey]!.GetValue<string>() == id);
            if (entry is null)
            {
                entry = [];
                entries.Add(entry);
            }
            Merge(entry, JsonNode.Parse(e.GetRawText())!.AsObject());
            if (entry[IdKey] is not JsonValue v || !v.TryGetValue<string>(out var still) || still != id) { throw new SessionPatchException(NotByIds); }
        }
        if (patch.Order is { } order)
        {
            var byId = entries.ToDictionary(e => e[IdKey]!.GetValue<string>(), StringComparer.Ordinal);
            if (order.Count != byId.Count || order.Distinct(StringComparer.Ordinal).Count() != order.Count || !order.All(byId.ContainsKey)) { throw new SessionPatchException(NotByIds); }
            entries = [.. order.Select(id => byId[id])];
        }
        if (entries.Count == 0) { target.Remove(SubagentsKey); }
        else { target[SubagentsKey] = new JsonArray([.. entries]); }
    }
}
