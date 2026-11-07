using System.Security.Cryptography;

namespace AgentBridge.Roster;

public enum CodeResult
{
    // The person typed the right code.
    Match,
    // A wrong or malformed code; the person may try again.
    Wrong,
    // The attempt is over: three wrong entries, or it ran out of time. A new attempt starts afresh.
    Ended,
    // Attempts are refused for now (§4.4 lockout).
    Locked,
}

// The limits of §4.4, counted by the device the person is typing on. The relay's own limits only
// bound its load; a hostile relay must not be able to reset these.
public sealed class EnrollmentLockout
{
    public const int WrongEntriesPerAttempt = 3;
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public enum Counts
    {
        // The approver: three wrong code-1 entries in an hour, over any devices, lock it.
        WrongEntries,
        // The joiner: three ended attempts in an hour lock its joining.
        EndedAttempts,
    }

    readonly Func<DateTimeOffset> _now;
    readonly Counts _counts;
    readonly Dictionary<string, (int Wrong, DateTimeOffset? BlockedUntil)> _keys = new(StringComparer.Ordinal);
    readonly List<DateTimeOffset> _marks = [];
    DateTimeOffset? _lockedUntil;

    public EnrollmentLockout(Counts counts, Func<DateTimeOffset> now)
    {
        _counts = counts;
        _now = now;
    }

    // Whether key (the pending device's id, at an approver) may try now: its own three wrong entries, or the device's lockout.
    public bool IsBlocked(string key)
    {
        var now = _now();
        if (_lockedUntil is { } locked && now < locked) { return true; }
        return _keys.TryGetValue(key, out var k) && k.BlockedUntil is { } until && now < until;
    }

    // Counts a wrong entry in the attempt for `key`; true when it ends the attempt.
    public bool RecordWrong(string key)
    {
        var now = _now();
        _keys.TryGetValue(key, out var k);
        k.Wrong++;
        var ended = k.Wrong >= WrongEntriesPerAttempt;
        if (ended) { k = (0, _counts == Counts.WrongEntries ? now + LockFor : null); }
        _keys[key] = k;
        if (_counts == Counts.WrongEntries) { Mark(now); }
        else if (ended) { Mark(now); }
        return ended;
    }

    // An attempt that ran out of time: it counts as an ended attempt at a joiner, and blocks no one at an approver.
    public void RecordEnded(string key)
    {
        if (_counts == Counts.EndedAttempts) { Mark(_now()); }
    }

    void Mark(DateTimeOffset now)
    {
        _marks.RemoveAll(m => now - m > Window);
        _marks.Add(now);
        if (_marks.Count >= WrongEntriesPerAttempt) { _lockedUntil = now + LockFor; }
    }
}

// The approver's side of the code exchange (§4.2), with no I/O: the caller carries the messages
// over the Noise channel and passes in `h`, that channel's handshake hash. A relay between the two
// devices has two channels with two different `h`, so the two ends compute different codes.
public sealed class EnrollmentApprover
{
    readonly string _account;
    readonly byte[] _h;
    readonly string _pendingId;
    readonly Func<DateTimeOffset> _now;
    readonly EnrollmentLockout _lockout;
    readonly DateTimeOffset _expires;
    byte[]? _commit;
    byte[]? _nA;
    byte[]? _transcript;
    bool _code1Matched;
    bool _ended;

    public EnrollmentApprover(string account, byte[] h, string pendingId, EnrollmentLockout lockout, Func<DateTimeOffset> now)
    {
        _account = account;
        _h = h;
        _pendingId = pendingId;
        _lockout = lockout;
        _now = now;
        _expires = now() + EnrollmentLockout.AttemptLifetime;
    }

    // N → A: the commitment. Returns A's offer, its nonce; null if the attempt cannot go on.
    public byte[]? OnCommit(byte[] commit, byte[]? nonce = null)
    {
        if (_commit is not null || Over() || commit.Length != 32) { return null; }
        _commit = commit;
        _nA = nonce ?? RandomNumberGenerator.GetBytes(Enrollment.NonceBytes);
        return _nA;
    }

    // N → A: the reveal. False abandons the attempt: N's nonce is not the one it committed to.
    public bool OnReveal(byte[] nN)
    {
        if (_commit is null || _nA is null || _transcript is not null || Over()) { return false; }
        if (nN.Length != Enrollment.NonceBytes || !Enrollment.MacMatches(_commit, Enrollment.Commit(_h, nN)))
        {
            _ended = true;
            return false;
        }
        _transcript = Enrollment.Transcript(_account, _h, _nA, nN);
        return true;
    }

    // What the person typed at A, from N's screen. Code 2 is shown only after a match.
    public CodeResult EnterCode1(string input)
    {
        if (_transcript is null || Over()) { return _ended ? CodeResult.Ended : CodeResult.Wrong; }
        if (_lockout.IsBlocked(_pendingId)) { return CodeResult.Locked; }
        if (Enrollment.Matches(Enrollment.Code1(_transcript), input))
        {
            _code1Matched = true;
            return CodeResult.Match;
        }
        if (_lockout.RecordWrong(_pendingId))
        {
            _ended = true;
            return CodeResult.Ended;
        }
        return CodeResult.Wrong;
    }

    // Code 2, for the person to type at N: only after code 1 matched.
    public string? Code2Shown => _code1Matched && _transcript is not null ? Enrollment.Shown(Enrollment.Code2(_transcript)) : null;

    // The value the signed add's `approval.transcript` carries: only after code 1 matched, and the
    // approver still has to see N's `ok` (§4.2 step 6) before it signs.
    public byte[]? TranscriptToSign => _code1Matched ? _transcript : null;

    bool Over()
    {
        if (_ended) { return true; }
        if (_now() < _expires) { return false; }
        _ended = true;
        _lockout.RecordEnded(_pendingId);
        return true;
    }
}

// The joining device's side (§4.2): it commits to its nonce before it sees the approver's, shows
// code 1, and checks code 2 and the signed add itself, so that it too authenticates its approver
// through the person, not through the relay.
public sealed class EnrollmentJoiner
{
    const string Attempt = "attempt";

    readonly string _account;
    readonly byte[] _h;
    readonly Func<DateTimeOffset> _now;
    readonly EnrollmentLockout _lockout;
    readonly DateTimeOffset _expires;
    readonly byte[] _nN;
    byte[]? _transcript;
    bool _codeMatched;
    bool _ended;

    public EnrollmentJoiner(string account, byte[] h, EnrollmentLockout lockout, Func<DateTimeOffset> now, byte[]? nonce = null)
    {
        _account = account;
        _h = h;
        _lockout = lockout;
        _now = now;
        _expires = now() + EnrollmentLockout.AttemptLifetime;
        _nN = nonce ?? RandomNumberGenerator.GetBytes(Enrollment.NonceBytes);
    }

    // The commitment to send first; null while joining is locked.
    public byte[]? Commit() => _lockout.IsBlocked(Attempt) ? null : Enrollment.Commit(_h, _nN);

    // A → N: the offer. Returns the reveal (N's nonce); N then shows code 1.
    public byte[]? OnOffer(byte[] nA)
    {
        if (_transcript is not null || Over() || nA.Length != Enrollment.NonceBytes) { return null; }
        _transcript = Enrollment.Transcript(_account, _h, nA, _nN);
        return _nN;
    }

    public string? Code1Shown => _transcript is null ? null : Enrollment.Shown(Enrollment.Code1(_transcript));

    // What the person typed at N, from the approver's screen.
    public CodeResult EnterCode2(string input)
    {
        if (_transcript is null || Over()) { return CodeResult.Ended; }
        if (_lockout.IsBlocked(Attempt)) { return CodeResult.Locked; }
        if (Enrollment.Matches(Enrollment.Code2(_transcript), input))
        {
            _codeMatched = true;
            return CodeResult.Match;
        }
        if (_lockout.RecordWrong(Attempt))
        {
            _ended = true;
            return CodeResult.Ended;
        }
        return CodeResult.Wrong;
    }

    // Step 8: the newest entry of a roster the joiner has already verified (§3.5) must add exactly
    // this device, signed by the approver the person confirmed, carrying this exchange's transcript.
    // A relay that played approver on another channel cannot produce this transcript.
    public bool Accepts(Entry newest, DeviceDescriptor own, string approverId)
    {
        var body = newest.Body;
        return _codeMatched
            && _transcript is not null
            && body.Op == EntryOp.Add
            && body.By == approverId
            && body.Device is { } added && SameDevice(added, own)
            && body.Approval is { Method: "code", Transcript: { } t }
            && Enrollment.MacMatches(_transcript, t);
    }

    // Record equality would compare the key arrays by reference.
    static bool SameDevice(DeviceDescriptor a, DeviceDescriptor b) =>
        a.Id == b.Id && a.Kind == b.Kind && a.Platform == b.Platform && a.Name == b.Name && a.Presence == b.Presence
        && a.SignAlg == b.SignAlg && a.Dh.AsSpan().SequenceEqual(b.Dh)
        && (a.Sign is null ? b.Sign is null : b.Sign is not null && a.Sign.AsSpan().SequenceEqual(b.Sign))
        && (a.WebAuthn is null ? b.WebAuthn is null : b.WebAuthn is not null && a.WebAuthn.RpId == b.WebAuthn.RpId
            && a.WebAuthn.CredentialId.AsSpan().SequenceEqual(b.WebAuthn.CredentialId) && a.WebAuthn.Key.AsSpan().SequenceEqual(b.WebAuthn.Key)
            && a.WebAuthn.Attested == b.WebAuthn.Attested && a.WebAuthn.BackupEligible == b.WebAuthn.BackupEligible);

    bool Over()
    {
        if (_ended) { return true; }
        if (_now() < _expires) { return false; }
        _ended = true;
        _lockout.RecordEnded(Attempt);
        return true;
    }
}
