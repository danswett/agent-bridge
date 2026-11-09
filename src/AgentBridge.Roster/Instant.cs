namespace AgentBridge.Roster;

// An RFC 3339 UTC time (Times.IsUtc) as an exact instant: seconds since the Unix epoch and the
// nanoseconds after them. Person commands compare their times exactly, so every implementation
// reaches the same verdict at a boundary, whatever its clock's resolution.
public readonly record struct Instant(long Seconds, int Nanos) : IComparable<Instant>
{
    public int CompareTo(Instant other) => Seconds != other.Seconds ? Seconds.CompareTo(other.Seconds) : Nanos.CompareTo(other.Nanos);

    public static bool operator <(Instant a, Instant b) => a.CompareTo(b) < 0;
    public static bool operator >(Instant a, Instant b) => a.CompareTo(b) > 0;
    public static bool operator <=(Instant a, Instant b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Instant a, Instant b) => a.CompareTo(b) >= 0;

    public Instant AddSeconds(long seconds) => new(Seconds + seconds, Nanos);

    // A time Times.IsUtc accepts.
    public static Instant Parse(string at)
    {
        if (!Times.IsUtc(at)) { throw new FormatException("not an RFC 3339 UTC time"); }
        static int N(string s, int start, int count) => int.Parse(s.AsSpan(start, count), System.Globalization.CultureInfo.InvariantCulture);
        var days = DaysFromCivil(N(at, 0, 4), N(at, 5, 2), N(at, 8, 2));
        var seconds = days * 86400L + N(at, 11, 2) * 3600L + N(at, 14, 2) * 60L + N(at, 17, 2);
        var nanos = 0;
        if (at.Length > 20)
        {
            var fraction = at[20..^1];
            nanos = N(fraction.PadRight(9, '0'), 0, 9);
        }
        return new Instant(seconds, nanos);
    }

    public static Instant From(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var rest);
        if (rest < 0) { seconds--; rest += TimeSpan.TicksPerSecond; }
        return new Instant(seconds, (int)(rest * 100));
    }

    // Days from 1970-01-01 to a proleptic Gregorian date.
    static long DaysFromCivil(int year, int month, int day)
    {
        long y = month <= 2 ? year - 1 : year;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }
}
