namespace AgentBridge.App.Core;

// The self-test's look at whether an act arms the answer guard, which holds a question's answers for about half a
// second and then lets go (SelfTest.AnswerMoves: a text box's clear button appearing). Two things are kept apart:
// work the act did not start (a loaded runner's late layout, #3752) is waited out BEFORE the act, with a quiet
// longer than the guard, so it cannot be mistaken for the act's doing; and the look AFTER the act stays short and
// strict, since waiting for a hold to end would pass a regression whose guard simply lets go.
public static class HoldProbe
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    // Settled: the question was free before the act, after a quiet. Held: it was held at the look after the act.
    // A caller that sees Settled false fails on that, not on the act.
    public static async Task<(bool Settled, bool Held)> AroundAsync(Func<bool> free, Func<Func<bool>, TimeSpan, Task<bool>> until, Func<TimeSpan, Task> delay, Func<Task> act)
    {
        var settled = await until(free, Settle);
        await delay(Quiet);
        settled &= free();
        await act();
        await delay(Look);
        return (settled, !free());
    }
}
