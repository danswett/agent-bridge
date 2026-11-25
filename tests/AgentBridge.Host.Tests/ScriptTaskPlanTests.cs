using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.ScriptRun;
using Xunit;

// #3305 S2: what the app shows the person, what the elevated step registers and what the host pins are ONE plan. These tests hold the plan to the launcher's own parser
// and the host's own task reader, so a plan the press would show can never be one the pin or the launcher reads differently.
public sealed class ScriptTaskPlanTests
{
    const string Launcher = @"C:\Program Files\Slipgates\script-run\script-run.exe";
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    const string Script = @"C:\Program Files\Slipgates\fleet\Cycle.ps1";
    const string Sid = "S-1-5-21-1-2-3-1000";

    static ScriptTaskPlan Make(string name = "dispatcher", string script = Script, IReadOnlyList<string>? extra = null, int every = 5, string sid = Sid, string pwsh = Pwsh)
    {
        Assert.True(ScriptTaskPlan.TryCreate(name, sid, Launcher, pwsh, script, extra ?? [], every, out var plan, out var why), why);
        return plan!;
    }

    [Fact]
    public void The_plan_shows_the_exact_argument_line_and_lists_launcher_interpreter_script_and_extras_in_that_order()
    {
        var plan = Make(extra: [@"C:\Program Files\Slipgates\fleet\lib.ps1"]);
        Assert.Equal(@"dispatcher -- ""C:\Program Files\PowerShell\7\pwsh.exe"" -NoProfile -NonInteractive -File ""C:\Program Files\Slipgates\fleet\Cycle.ps1""", plan.Arguments);
        Assert.Equal([Launcher, Pwsh, Script, @"C:\Program Files\Slipgates\fleet\lib.ps1"], plan.Files);
        Assert.Equal(@"\Slipgates\dispatcher", plan.Task);
        Assert.Contains(plan.Arguments, plan.Summary);
        Assert.All(plan.Files, f => Assert.Contains(f, plan.Summary));
    }

    [Fact]
    public void The_task_xml_is_read_by_the_hosts_own_reader_as_logged_on_only_with_exactly_the_planned_files()
    {
        var plan = Make();
        Assert.True(ScriptTaskXml.LoggedOnOnly(plan.TaskXml));
        Assert.Equal([Launcher, Pwsh, Script], ScriptTaskXml.ActionFiles(plan.TaskXml));
        Assert.NotNull(ScriptTaskXml.Definition(plan.TaskXml));
        Assert.Contains("<Interval>PT5M</Interval>", plan.TaskXml);
        Assert.Contains($"<UserId>{Sid}</UserId>", plan.TaskXml);
    }

    [Fact]
    public void The_launcher_parses_the_planned_arguments_to_the_same_program_and_script_the_plan_pins()
    {
        var plan = Make();
        var tokens = CommandLine.Tokens(plan.Arguments);
        Assert.NotNull(tokens);
        var command = CommandLine.Parse(tokens!);
        Assert.NotNull(command);
        Assert.Equal((plan.Name, Pwsh, Script), (command!.Name, command.Program, command.Script));
    }

    [Theory]
    [InlineData("dispatcher", @"Cycle.ps1", "a relative script")]
    [InlineData("dispatcher", @"C:\Locked\%TEMP%\Cycle.ps1", "an environment variable")]
    [InlineData("dispatcher", @"C:\Locked\..\Evil\Cycle.ps1", "a relative segment")]
    [InlineData("dispatcher", @"C:\Locked\Cycle.txt", "not a PowerShell script")]
    [InlineData("dispatcher", @"C:\Locked\Cycle.ps1"" -Command ""x", "a quote that smuggles an argument")]
    [InlineData("..", @"C:\Locked\Cycle.ps1", "a dot-dot name")]
    [InlineData("a b", @"C:\Locked\Cycle.ps1", "a name with a space")]
    public void A_name_or_script_the_pin_or_the_launcher_could_read_differently_makes_no_plan(string name, string script, string why)
    {
        Assert.False(ScriptTaskPlan.TryCreate(name, Sid, Launcher, Pwsh, script, [], 5, out var plan, out var reason), why);
        Assert.Null(plan);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void An_interpreter_that_is_not_pwsh_or_powershell_an_unknown_interval_a_bad_user_or_too_many_files_makes_no_plan()
    {
        Assert.False(ScriptTaskPlan.TryCreate("d", Sid, Launcher, @"C:\Windows\System32\cmd.exe", Script, [], 5, out _, out _));
        Assert.False(ScriptTaskPlan.TryCreate("d", Sid, Launcher, Pwsh, Script, [], 7, out _, out _));
        Assert.False(ScriptTaskPlan.TryCreate("d", "alice", Launcher, Pwsh, Script, [], 5, out _, out _));
        Assert.False(ScriptTaskPlan.TryCreate("d", Sid, Launcher, Pwsh, Script, [.. Enumerable.Range(0, 14).Select(i => $@"C:\Locked\f{i}.ps1")], 5, out _, out _));
        Assert.True(ScriptTaskPlan.TryCreate("d", Sid, Launcher, Pwsh, Script, [.. Enumerable.Range(0, 13).Select(i => $@"C:\Locked\f{i}.ps1")], 5, out _, out _));
    }

    [Fact]
    public void An_extra_file_that_is_relative_or_repeats_another_makes_no_plan()
    {
        Assert.False(ScriptTaskPlan.TryCreate("d", Sid, Launcher, Pwsh, Script, [@"lib.ps1"], 5, out _, out _));
        Assert.False(ScriptTaskPlan.TryCreate("d", Sid, Launcher, Pwsh, Script, [Script.ToUpperInvariant()], 5, out _, out _));
    }
}
