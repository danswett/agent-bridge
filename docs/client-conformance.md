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
HA's dropdown workaround. Catalog-to-CLI wiring, exclusion reporting and drivers are later slices;
the command above still requires an explicit capabilities file and does not apply the catalog.
