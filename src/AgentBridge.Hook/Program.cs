using System.Reflection;
using AgentBridge.Hooks;

// agent-bridge-host-hook <agent> <hook>: see HookRunner. Always exits 0.
var version = typeof(HookRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
var plus = version.IndexOf('+');
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();
return HookRunner.Run(args, stdin, stdout, new HookEnvironment
{
    ExecutableDirectory = Path.GetDirectoryName(Environment.ProcessPath),
    Version = plus < 0 ? version : version[..plus],
});
