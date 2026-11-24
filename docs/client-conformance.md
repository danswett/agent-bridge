# Recorded client conformance

`agent-bridge-rehearsal conformance --client PWA --runs runs.json --capabilities capabilities.json --results results.jsonl`
checks recorded evidence, not a live client. This is #1407 slice 2, not retirement certification.

`runs.json` is an array of the existing `ConformanceRun` records, declared independently
of results: `client`, `version`, `protocolVersion`, full `implementationHead`, `runId`.
`capabilities.json` is an array of `ConformanceCapability`: `name`, optional `live`.
`results.jsonl` contains the existing `ConformanceResult` records, one per line:
`run` (the matching run object), `capability`, `verdict`, `reason`, optional `evidence` URL.
Verdicts are `pass`, `fail`, `notSupported`, `missing`; live passes need a recorded run link.

Standard output contains only one JSON line per declared version/capability for the
named client. Unreported results become `missing`. Exit 0 means every selected row
passed; 1 means any other verdict; 2 means invalid/unreadable input (error on stderr).
All input rows are validated before output, including other clients' rows.
`ConformanceInventory` (#1407 slice 3a) records the pinned 34-row fleet inventory and
decision-linked dispositions. Host/fleet obligations remain obligations, not client passes;
intentional drops, the excluded Apple host cohort and resolved owner inventory stay distinct.
Client scheduling (required, parked, unshipped or optional) never excuses a missing behavior
when that client is explicitly selected. Multi-field forms remain required despite retiring
HA's dropdown workaround.

For `--client Windows`, the production command passes `WindowsClientCallback.Create` to the
existing driver. Its minimal injectable self-test evidence seam is normalized, recorded
`ConformanceResult` JSONL in `--results`, using the schema above: exact declared run
(including version, protocol and implementation head), inventory capability, verdict,
reason and evidence link. It reads no client process, pipe or live host. There is no
native self-test producer wired in this slice: the app's raw `passed`/`failed`/`unfinished`
report is **not** accepted or automatically promoted to conformance proof.

Windows always uses every required row in `ConformanceInventory.ForClient("Windows")`,
not a caller-selected subset. `--capabilities` remains required and must be valid JSON,
but does not narrow Windows coverage. Absent evidence stays `missing`; a live pass
without a recorded HTTP(S) run link also stays `missing`. Invalid or duplicate evidence,
including another run or an unknown capability, is refused before output.
Exit 0 requires all required rows to pass. Other clients retain their explicit capabilities
and recorded-result path. Exclusion reporting and recorded live Windows pass results
remain separate work; synthetic test records are not retirement certification.
