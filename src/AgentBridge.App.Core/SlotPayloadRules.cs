using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.App.Core;

// The two pure rules a prepared slot payload and the closed hostw bundle share: which relative paths are safe,
// and which runtime assets a deps.json requires. Moved verbatim from SlotPreparation so the bundle verifier and the
// slot preparation can land as separate pull requests (#2869); no behaviour change.
internal static class SlotPayloadRules
{
    internal static IEnumerable<(string Name, byte Type)> RuntimeAssets(JsonElement deps, string target)
    {
        var libraries = deps.GetProperty("targets").GetProperty(target);
        var graph = libraries.EnumerateObject().ToDictionary(l => l.Name, _ => new List<string>(), StringComparer.Ordinal);
        var incoming = graph.Keys.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Value.TryGetProperty("dependencies", out var dependencies))
            foreach (var dependency in dependencies.EnumerateObject())
            {
                var name = dependency.Name + "/" + dependency.Value.GetString();
                if (!incoming.ContainsKey(name)) { throw new InvalidDataException("Missing runtime dependency: " + dependency.Name); }
                graph[library.Name].Add(name); incoming[name]++;
            }
        }
        var ready = new Queue<string>(incoming.Where(n => n.Value == 0).Select(n => n.Key));
        var visited = 0;
        while (ready.TryDequeue(out var node))
        {
            visited++;
            foreach (var child in graph[node]) { if (--incoming[child] == 0) { ready.Enqueue(child); } }
        }
        if (visited != graph.Count) { throw new InvalidDataException("Cyclic runtime dependency graph."); }
        foreach (var library in libraries.EnumerateObject())
        {
            foreach (var group in library.Value.EnumerateObject().Where(g => g.Name is "runtime" or "native" or "resources" or "runtimeTargets"))
            foreach (var asset in group.Value.EnumerateObject())
            {
                if (group.Name == "runtimeTargets" && asset.Value.GetProperty("rid").GetString() != target.Split('/')[^1]) { continue; }
                var name = asset.Name.Split('/')[^1];
                if (name == "_._") { continue; }
                yield return (group.Name == "resources" ? asset.Value.GetProperty("locale").GetString() + "/" + name : name,
                    group.Name == "native" || group.Name == "runtimeTargets" && asset.Value.GetProperty("assetType").GetString() == "native" ? (byte)2 : (byte)1);
            }
        }
    }

    internal static bool SafeRelative(string path) => path.Length > 0 && !path.Contains('\\')
        && path.Split('/').All(p => p.Length > 0 && p is not "." and not ".." && !p.EndsWith('.')
            && !p.EndsWith(' ') && !p.Any(c => c < 32 || "<>:\"|?*~".Contains(c))
            && !Regex.IsMatch(p, @"\A(?:CON(?:IN\$|OUT\$)?|PRN|AUX|NUL|(?:COM|LPT)[0-9\u00B9\u00B2\u00B3])(?:\.|\z)", RegexOptions.IgnoreCase));
}
