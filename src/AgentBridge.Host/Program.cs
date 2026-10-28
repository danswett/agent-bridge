using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

// agent-bridge-host: the Bridge Host. For now a console front for the read-only core, so
// the sessions it sees can be checked against the real machine:
//
//   agent-bridge-host sessions [--compact]      one scan, the SessionList as JSON
//   agent-bridge-host get <id>                  one session with its tool history
//   agent-bridge-host watch [--json]            a line per upsert and removal, until Ctrl+C
//
// Roots default to the current user's; --copilot-home, --claude-home and --codex-home
// point elsewhere. Nothing under them is ever written.

var arguments = args.ToList();
var command = arguments.Count > 0 ? arguments[0] : "help";
string? Option(string name)
{
    var i = arguments.IndexOf(name);
    return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
}
bool Flag(string name) => arguments.Contains(name);

var defaults = SessionSourceOptions.ForCurrentUser();
var options = new SessionSourceOptions
{
    CopilotHome = Option("--copilot-home") ?? defaults.CopilotHome,
    ClaudeHome = Option("--claude-home") ?? defaults.ClaudeHome,
    CodexHome = Option("--codex-home") ?? defaults.CodexHome,
    ClaudeRegistrationRoots = defaults.ClaudeRegistrationRoots,
    CodexRegistrationRoots = defaults.CodexRegistrationRoots,
};
var pretty = new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = !Flag("--compact") };

switch (command)
{
    case "sessions":
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            Console.WriteLine(JsonSerializer.Serialize(new SessionList(source.Snapshot(), source.Revision), pretty));
            return 0;
        }
    case "get" when arguments.Count > 1:
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            var session = source.Get(arguments[1]);
            Console.WriteLine(session is null ? "{\"missing\":true}" : JsonSerializer.Serialize(session, pretty));
            return session is null ? 1 : 0;
        }
    case "watch":
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await using var source = new SessionSource(options);
            await source.StartAsync(stop.Token);
            using var subscription = source.Subscribe();
            var json = Flag("--json");
            try
            {
                await foreach (var ev in subscription.Events.ReadAllAsync(stop.Token))
                {
                    Console.WriteLine(json ? JsonSerializer.Serialize(new { ev = ev.Name, data = ev.Data }, ProtocolJson.Options) : Describe(ev));
                }
            }
            catch (OperationCanceledException) { }
            return 0;
        }
    default:
        Console.Error.WriteLine("usage: agent-bridge-host sessions [--compact] | get <id> | watch [--json]  [--copilot-home <dir>] [--claude-home <dir>] [--codex-home <dir>]");
        return command == "help" ? 0 : 2;
}

static string Describe(SessionEvent ev) => ev.Data switch
{
    Session s => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} upsert {s.Id} {s.Agent} {s.Status} since {s.StatusSince:HH:mm:ss} tool={s.Activity.CurrentTool?.Name ?? "-"} question={(s.Question is null ? "-" : s.Question.Kind.ToString())} \"{s.Title}\"",
    SessionRemoved r => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} remove {r.Id}",
    SessionsSynced y => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} synced revision {y.Revision}",
    _ => ev.Name,
};
