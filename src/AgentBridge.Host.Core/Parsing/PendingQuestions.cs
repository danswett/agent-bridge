using System.Collections;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Id lookup and linked order: replacing or answering a question never walks its peers.
// Visible order is separate so restore boundaries do not make every status read scan again.
internal sealed class PendingQuestions : IReadOnlyCollection<Question>
{
    sealed class Entry(Question question)
    {
        public readonly LinkedListNode<Question> All = new(question);
        public readonly LinkedListNode<string> Owner = new(question.Id);
        public LinkedListNode<Question>? Visible;
        public LinkedListNode<Question>? Main;
    }

    readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    readonly LinkedList<Question> _all = new(), _visible = new(), _main = new();
    readonly LinkedList<string> _own = new();
    readonly Dictionary<string, LinkedList<string>> _agents = new(StringComparer.Ordinal);

    public int Count => _byId.Count;
    public IReadOnlyCollection<Question> Asking => _visible;
    public Question? Newest => _main.Last?.Value ?? _visible.Last?.Value;
    public bool Contains(string id) => _byId.ContainsKey(id);

    public void Add(Question question, bool hidden)
    {
        Remove(question.Id);
        var entry = new Entry(question);
        _byId.Add(question.Id, entry);
        _all.AddLast(entry.All);
        if (!hidden) { Show(entry); }
        if (question.Agent is not { } agent) { _own.AddLast(entry.Owner); }
        else
        {
            if (!_agents.TryGetValue(agent, out var ids)) { _agents.Add(agent, ids = new()); }
            ids.AddLast(entry.Owner);
        }
    }

    void Show(Entry entry)
    {
        var question = entry.All.Value;
        entry.Visible = _visible.AddLast(question);
        if (question.Agent is null) { entry.Main = _main.AddLast(question); }
    }

    public void Hide(IReadOnlySet<string> hidden)
    {
        _visible.Clear();
        _main.Clear();
        foreach (var question in _all)
        {
            var entry = _byId[question.Id];
            entry.Visible = entry.Main = null;
            if (!hidden.Contains(question.Id)) { Show(entry); }
        }
    }

    public bool Remove(string id)
    {
        if (!_byId.Remove(id, out var entry)) { return false; }
        _all.Remove(entry.All);
        if (entry.Visible is { } visible) { _visible.Remove(visible); }
        if (entry.Main is { } main) { _main.Remove(main); }
        if (entry.All.Value.Agent is not { } agent) { _own.Remove(entry.Owner); }
        else
        {
            var ids = _agents[agent];
            ids.Remove(entry.Owner);
            if (ids.Count == 0) { _agents.Remove(agent); }
        }
        return true;
    }

    public int RemoveFor(string? agent)
    {
        var ids = agent is null ? _own : _agents.GetValueOrDefault(agent);
        if (ids is null) { return 0; }
        var count = ids.Count;
        while (ids.First is { } first) { Remove(first.Value); }
        return count;
    }

    public int RemoveEnded(BackgroundWork work, string? agent = null)
    {
        var count = agent is null ? 0 : RemoveFor(agent);
        // BackgroundWork holds at most 256 agents; never scan unbounded pending questions.
        foreach (var ended in work.EndedAgents) { count += RemoveFor(ended); }
        return count;
    }

    public void Clear()
    {
        _byId.Clear();
        _all.Clear();
        _visible.Clear();
        _main.Clear();
        _own.Clear();
        _agents.Clear();
    }

    public IEnumerator<Question> GetEnumerator() => _all.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
