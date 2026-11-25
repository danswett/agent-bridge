# Explicit retry: bounded split and pending PWA RED

This is the approved host/PWA split of
[PR 2911](https://github.com/danswett/agent-bridge/pull/2911), not completion of
[issue 2280](https://github.com/danswett/agent-bridge/issues/2280).
The original PR remains RED and must not be described as whole-PR GREEN.

Both cuts have actual parent `f408058e129ee27a48c96f55eab6803bd4304734`.
The original head `f1ff7fcf0e0800f068c18de369f1cba8fe72e636` is 156 WHOLE:
89 host/app/protocol lines (+78/-11) and 67 PWA test lines (+67/-0).
The host cut retains those four host/app/protocol files byte-for-byte; this
tracking document is additional WHOLE scope, not excluded from its size.
No updater cap, source authority, guard count or PWA implementation is changed.
Existing TDD provenance is [RED 695355bc](https://github.com/danswett/agent-bridge/commit/695355bc1dd99b44d0014606f1bbc8ef39bbdc1d)
then [GREEN 26911876](https://github.com/danswett/agent-bridge/commit/269118761454ca8ec12a51637c12668211410815);
this is a mechanical extraction, not a new failing-first cycle.

Focused offline validation of the host-only cut, serial on CPU 1 at BelowNormal:
5 C# retry/download-hold cases PASS; its existing 35 PWA update unit cases PASS;
its existing 11 Chromium update cases PASS. The 11 WebKit cases could not launch:
the installed browser executable is missing. This is not a WebKit PASS or hosted
CI proof. No live or installed-host tests were run.

All 67 admitted PWA RED lines are preserved, without assertion changes, in
[pending RED head 1775ce959afb2a65d598a3c574951a98a6fdb667](https://github.com/danswett/agent-bridge/commit/1775ce959afb2a65d598a3c574951a98a6fdb667).
Its entire `pwa/` tree matches the original PR head. Its 37 update unit cases
remain 35 PASS / 2 FAIL; its three admitted Chromium cases remain RED:
cap-to-idle, Try again now after a failed download, and the Check menu moves.
The three browser failures were reproduced on the original byte-identical PWA tree.
They are not skipped, weakened, counted as passing, or part of the host-only denominator.

PWA implementation is explicitly deferred to the remaining issue 2280 work,
after the host slices identified in its [Product correction](https://github.com/danswett/agent-bridge/issues/2280#issuecomment-6966449193). Start from the
preserved RED tests, migrate conflicting legacy UI expectations honestly, and
obtain GREEN evidence before claiming that PWA scope complete. Quality owns
publication and replacement of the original mixed PR; this split closes nothing.
