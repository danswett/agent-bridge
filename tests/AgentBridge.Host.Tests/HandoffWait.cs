// The bound on a test's wait for something to happen (a task completing, a handoff being reached) when no clock in
// the code under test drives it. It exists only to turn a real deadlock into a failure: a full parallel run on one
// CPU can starve the thread pool for longer than 5 to 10 s, and a bound that short failed healthy tests (#2558).
internal static class HandoffWait
{
    public static readonly TimeSpan Hang = TimeSpan.FromMinutes(1);
}
