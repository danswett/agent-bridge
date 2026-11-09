# Feedback bundle (Windows)

What the Windows app gathers for "Send feedback" (#738), before it is sealed ([feedback.md](protocol/feedback.md))
and uploaded. Code: `src/AgentBridge.App.Core/Feedback`. It has no UI; the review screen shows
`FeedbackBundle.Text` and the sender seals `FeedbackBundle.CanonicalBytes`, which are the same bytes.

The privacy design (consent, preview, retention, deletion, notice) is in
[feedback-privacy.md](feedback-privacy.md), a draft under review (#732).

## Contents

Schema `slipgates-feedback.v1`, the PWA's, with `platform: "Windows"`. In order:

| Item | What | Bound |
|---|---|---|
| versions | app, channel, OS, architecture, protocol, host versions | short strings, 20 hosts |
| agents | agents detected, with versions | 20 |
| config | allow-listed settings only (`FeedbackBundleSource.ConfigAllowList`), values redacted; the rest are counted, never named | 100 chars a value |
| journal | the last lines of the activity log, redacted | 200 lines of 400 chars |
| crashes | recent crash records, redacted | 5 of 8000 chars |
| note | the tester's text, with secrets replaced by `[redacted]` | 1000 chars |
| screenshot | optional PNG, base64 | 1 MiB |
| sessionTurns | optional, off by default, one session the person names | 20 turns of 2000 chars |

The whole bundle is capped at 2 MiB and gathering at 10 s. A source that cannot be read
becomes `"unavailable"`; the error is never recorded, since it could carry content.

## Redaction

`FeedbackRedactor` replaces tokens (GitHub, AWS, Slack, `sk-`), JWTs, `Bearer` values, private key blocks,
credentials in URLs, whole `Authorization` values, quoted or bare values of keys named like a secret (also inside escaped JSON), `--token`/`--password` style flags, email addresses, the profile folder name in any `C:\Users\...`, `/home/...` or `/Users/...` path, this machine's user and computer names, and long base64 or hex runs. It only removes; it does not
promise to catch every secret, which is why the person sees the exact bytes first. Nothing here is logged.


`FeedbackRedactor.ContainsSecret` and `BundleHasSecret` use the same credential patterns, so the sender's last check before upload cannot disagree with the redactor. `BundleHasSecret` also joins each JSON name with its string, number or boolean value (also inside nested objects and arrays), so `{"password":"hunter2"}` and `{"token":true}` are caught. Every text is checked twice: as written, and with escapes decoded repeatedly (up to 4 passes). A credential visible only in the decoded form, or escapes still left after the cap, replaces the whole text with `[redacted: escaped secret]`. A bare word such as `token hunter2` is prose and is not treated as a credential. A source that fails or ignores cancellation is cut off at the 10 s bound; a failing session source refuses with `SessionUnavailable`.

## Sending (Windows)
`FeedbackSender` implements `IFeedbackSender`: it re-scans the bundle with the same redactor (JSON values decoded, so `{"password":"..."}` is caught; a leftover email or profile path is refused too; malformed JSON and any scan error are refused) as `BadRequest`, with no network request, seals `CanonicalBytes` into this envelope using the bundle id, requests a `feedback-put` slot and uploads the sealed bytes. The whole send is bounded by 60 seconds; a timeout that is not the caller cancelling is `Unreachable`.

| Outcome | Result |
| --- | --- |
| Uploaded | `Sent(now)` |
| No connection | `Failed(Offline, WhenOnline)` |
| 429 / relay `rate-limited` | `Failed(RateLimited, AfterDelay, 1 min)` |
| relay `quota` | `Failed(Quota, AfterDelay, 1 h)` |
| 500 / 502 / 503 / 504 / 408 / timeout | `Failed(Unreachable, Now)` |
| Relay or storage refuses | `Failed(UploadRejected, Never)` |
| No recipient key | `Failed(KeyUnavailable, Never)` |
| Caller cancels | `Failed(Cancelled, Now)` |

There is no offline queue and nothing is written to disk. The window keeps the note and the reviewed preview in memory for a manual Try again (the bundle is sealed again with a fresh key) and clears them on close. Failure results carry no report content.

## Review step (Windows)
`FeedbackReviewViewModel` drives the review before sending. It gathers through `IFeedbackBundleSource` and hands each bundle to `FeedbackSendViewModel.Ready`; `Rows` has one entry per bundle item (label, summary, optional), and `RowsExpanded` starts collapsed. Session turns are a separate option, off by default, named after the session. Changing the note, the screenshot or that option gathers again with a new `FeedbackRequest`, cancels the earlier gather, and drops any gather that finishes after a newer request or after Close. A refusal (`FeedbackRefusalReason`) or a failed gather shows through the send view model (`TooLarge` for `BundleTooLarge`, `Refused` for the rest, `Failed` for an error). Nothing is sent until `SendAsync` is called.

## Sources and availability (Windows)

`AppFeedbackSources` reads what the bundle holds: the last 200 lines of `app.log` (with `app.log.1` before it), the last five records of `crash.log` (newest first, stack lines kept), the app's settings scalars, the host's agent report, and a session's `session.history`. Reads are read-only and share the files with the running app; a missing, locked or malformed file leaves that part empty. An unknown session reads as not found; a host that cannot answer reads as unavailable.

`FeedbackSenderFactory.Availability` says whether sending is possible: `Available`, `UploadOff`, `NoRecipientKey`, `NoRelay` or `RelayNotAccepting`. The feedback key id and public key are compiled in (`FeedbackSenderFactory.ProductionKeyId` and `ProductionPublicKey`, empty until the production values are issued, which keeps feedback unavailable). The app settings `feedbackKeyId` and `feedbackPublicKey` override them for staging, only when both are set; `feedbackUpload` turns feedback off. When unavailable the dialog is read-only with Send disabled and shows "Sending feedback isn't available right now". There is no plaintext or email fallback.
