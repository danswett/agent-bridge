using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// Update now's words in the Windows app (#1159): the PWA's (pwa/js/update.js), read from the
// machine's codes and counts, never its own text.
public sealed class UpdateNowWordsTests
{
    static UpdateBusySession S(string id, string state) => new(id, state);

    [Fact]
    public void The_busy_list_is_read_approvals_first_then_questions_then_turns_at_most_32_and_counts_are_clamped()
    {
        var sessions = Enumerable.Range(0, 40).Select(i => S($"w{i}", UpdateBusyState.Working))
            .Append(S("q", UpdateBusyState.Question)).Append(S("a", UpdateBusyState.Approval)).Append(S("x", "a state from later")).ToList();

        var read = UpdateNowWords.Read(new UpdateBusy { Working = -3, Waiting = int.MaxValue, Sessions = sessions, MoreApprovals = 1, MoreOther = 2 });

        Assert.Equal(UpdateProtocol.BusyListed, read.Sessions!.Count);
        Assert.Equal(new[] { "a", "q", "w0" }, read.Sessions.Take(3).Select(s => s.Id));
        Assert.DoesNotContain(read.Sessions, s => s.Id == "x");
        Assert.Equal(0, read.Working);
        Assert.Equal(UpdateProtocol.BusyMost, read.Waiting);
    }

    [Fact]
    public void A_machine_that_reports_no_busy_reads_as_cant_tell_and_is_never_quiet()
    {
        var read = UpdateNowWords.Read(null);

        Assert.True(read.Unknown);
        Assert.False(UpdateNowWords.Quiet(read));
        Assert.True(UpdateNowWords.Quiet(UpdateNowWords.Read(new UpdateBusy { Working = 0, Waiting = 0, Sessions = [] })));
    }

    [Fact]
    public void The_confirmation_names_each_session_as_this_device_does_counts_what_it_left_out_and_warns_while_one_waits()
    {
        var busy = UpdateNowWords.Read(new UpdateBusy
        {
            Working = 1, Waiting = 1, Sessions = [S("s1", UpdateBusyState.Working), S("s2", UpdateBusyState.Approval)], MoreApprovals = 2, MoreOther = 3,
        });

        var sheet = UpdateNowWords.Sheet("Desk PC", busy, id => id == "s1" ? "Fix the build" : null);

        Assert.Equal("Update Desk PC now?", sheet.Title);
        Assert.Equal(new[] { new UpdateNowSheetRow("A session", "Approval waiting"), new UpdateNowSheetRow("Fix the build", "Mid-turn") }, sheet.Sessions);
        Assert.Equal("and 5 more, 2 of them with an approval waiting", sheet.More);
        Assert.Equal(UpdateNowWords.WarnLine, sheet.Warn);
        Assert.Null(sheet.Unknown);
    }

    [Fact]
    public void A_turn_alone_is_not_warned_of_and_what_is_signed_is_exactly_what_was_shown()
    {
        var busy = UpdateNowWords.Read(new UpdateBusy { Working = 1, Waiting = 0, Sessions = [S("s1", UpdateBusyState.Working)], MoreApprovals = 0, MoreOther = 0 });

        var sheet = UpdateNowWords.Sheet("Desk PC", busy, _ => null);
        var accept = UpdateNowWords.AcceptOf(sheet.Busy);

        Assert.Null(sheet.Warn);
        Assert.Null(sheet.More);
        Assert.Equal(1, accept.Working);
        Assert.Equal(0, accept.Waiting);
        Assert.Null(accept.Unknown);
        Assert.Equal(new[] { S("s1", UpdateBusyState.Working) }, accept.Sessions);
        Assert.Null(accept.MoreApprovals);
        Assert.Null(accept.MoreOther);
    }

    [Fact]
    public void Why_an_Update_now_did_not_happen_comes_from_its_code_never_the_machines_text()
    {
        Assert.Equal("Not updated: another bridge profile is running there.", UpdateNowWords.NotUpdated(UpdateCode.OtherProfile));
        Assert.Equal("Not updated: the machine refused it.", UpdateNowWords.NotUpdated("a code from later"));
        Assert.Equal("Not updated: Update now has used its restarts for the last 6 hours; try again later.", UpdateNowWords.NotUpdated(UpdateCode.RestartCap));
        Assert.Equal("Not updated: the update didn't take.", UpdateNowWords.NotUpdated(null, failed: true));
        Assert.Null(UpdateNowWords.Reason("a reason from later", null));
        Assert.Equal("Update now needs a newer app there. Install still works.", UpdateNowWords.Reason(UpdateNowWhy.OldApp, null));
    }

    [Fact]
    public void The_presses_remembered_are_the_last_32()
    {
        var presses = new UpdateNowPresses();
        for (var i = 0; i < 33; i++) { presses.Add($"r{i}"); }

        Assert.False(presses.Contains("r0"));
        Assert.True(presses.Contains("r32"));
        Assert.False(presses.Contains(null));
    }
}
