# Forms soak: first run

One pass, Copilot CLI 1.0.107, run on the author's Windows machine (not a dedicated one) on
2026-11-07, PowerShell 7.6.9, host assemblies from an installed app build. Screen-only mode.

| Shape | 120x30 | 120x50 | 200x50 | 250x40 |
| --- | --- | --- | --- | --- |
| choice | delivered | delivered | delivered | delivered |
| choice-text | delivered | delivered | delivered | delivered |
| choice-yesno-text | delivered | delivered | delivered | delivered |
| number | delivered | delivered | delivered | delivered |
| long-title | refused | refused | delivered | delivered |
| tall | refused | delivered | delivered | delivered |
| other | refused | refused | refused | refused |

"refused" is refused-before-any-key: nothing was typed. No run was wedged.

- long-title at 120 columns: the host's screen check could not place the form at its field
  (`field-heading`), so it refused. This is the narrow-window case of #1123.
- tall at 120x30: the form's heading scrolled off the screen, so the check refused.
- other: refused by design, as `host-answers.md` says. In the first pass the rig did not stop
  before typing, and the CLI was given the last option instead (`half-answered`); the rig now
  stops there as the host does, and one re-run at 200x50 confirmed `refused`.

Not covered: other CLI versions, repeat passes (flakiness), a dedicated machine, a hosted runner.
