# Feedback bundle (Windows)

What the Windows app gathers for "Send feedback" (#738), before it is sealed ([feedback.md](protocol/feedback.md))
and uploaded. Code: `src/AgentBridge.App.Core/Feedback`. It has no UI; the review screen shows
`FeedbackBundle.Text` and the sender seals `FeedbackBundle.CanonicalBytes`, which are the same bytes.

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


`FeedbackRedactor.ContainsSecret` and `BundleHasSecret` use the same credential patterns, so the sender's last check before upload cannot disagree with the redactor. A source that fails or ignores cancellation is cut off at the 10 s bound; a failing session source refuses with `SessionUnavailable`.
