# Shared learning experiment

This is opt-in development tooling, not a feature of the running bridge. It is being
developed on an experimental branch. Checking out the branch does not activate anything.
There are no new hooks, services, scheduled jobs, model calls, permissions or deploy steps.
The host, relay, installed hooks and live Home Assistant are unchanged.

The first slice implements four read-only commands in `tools/AgentBridge.Learning`.
They print JSON to stdout and errors to stderr; they never write a repository, profile or
log. Save output explicitly in a private experiment directory **outside every checkout**.
Do not commit captured run data. The tests use synthetic repositories under their own
build output, with commit hooks disabled only in those fixtures.

## What this slice proves, and what it does not

| Implemented | Not implemented or claimed |
|---|---|
| Strict, versioned lesson schema and evidence shapes | Verification that links resolve or that the diagnosis is true |
| Approved-only retrieval from a full, explicitly selected Git commit | Human approval enforcement; a status field is not approval |
| Source-reference drift and expiry detection | Automatic semantic verification or conflict resolution |
| Frozen code/notes commits, note blobs, task digest and tool-binary digest | A signed attestation, a sandbox, or proof of the actual model/harness used |
| Deterministic TRX failure signatures and visible unparsed outcomes | General build-log/TAP ingestion, category aliases or a measured improvement |
| Bounded full-note results, pending ids and overlap warnings | Prompt delivery, pre-edit coverage, session caching or automatic retry |

This follows the evidence-to-reviewed-guidance part of *Shared Learning for Engineering
Agents v7*. It does not activate the paper's pilot. A librarian, janitor, MCP endpoint,
instruction generator, hooks, approval-policy changes and autonomous merging are out of
this first slice. No unreviewed draft is ever returned by `search`.

## Build and run

Use the existing .NET 10 SDK:

```sh
dotnet build tools/AgentBridge.Learning -c Release
dotnet tools/AgentBridge.Learning/bin/Release/net10.0/agent-bridge-learning.dll --help
dotnet test tests/AgentBridge.Learning.Tests -c Release
```

The project is also in `AgentBridge.slnx` and the existing quality catalog. The normal
ring-0 runner excludes `Category=Process`: run the focused command above for the full
synthetic-Git integration suite. No extra CI workflow or hosted macOS job is added.

In the examples below, `<tool>` means
`dotnet tools/AgentBridge.Learning/bin/Release/net10.0/agent-bridge-learning.dll`.
Replace bracketed placeholders before running.

### Validate a candidate, without promoting it

```text
<tool> validate --note quality/learning/notes/K-app-caption-paint.json
```

The included candidate is deliberately **draft**. Its links point to the investigation
and verification recorded in PR #430; neither their availability nor the reproduction has
been independently re-verified by this tool. `shapeValid: true` means only that the
required evidence fields are present and well formed. It never means approved or proven.

Notes are JSON files under `quality/learning/notes/`. Each contains:

- `version`, stable `id`, `kind`, lifecycle `status` and stable mistake `category`;
- repository-relative `paths` using the quality selector's `*`, `**`, `?` semantics;
- `owner`, a full `verifiedCommit`, and explicit source-file `references`;
- `recheckBy`, `staleWhen`, and any notable `effects`;
- `guidance`: `do`, `avoid`, `recognize`, `why`, at most 150 words in total;
- `evidence`: the shape for its kind.

| Kind | Required evidence |
|---|---|
| gotcha, invariant | `failures` links, `diagnosis` citing one, `fix`, `verification` links |
| convention, pattern | `exemplar` plus `check` or `endorsement` with `link` and `rationale` |
| procedure | `runbook`, `successfulRun` |
| decision | `decision`, `implementedBy` |
| fact | `source`, `endorsement` with `link` and `rationale` |

Evidence links must be HTTPS without credentials or query strings; they are not fetched.
This is **not** a general secret detector or prompt-injection defense. Humans must inspect
the content, evidence and side effects before approval. The existing permission system is
never relaxed by a note.

### Capture a local or CI test report

Produce TRX with the existing test runner's `--logger trx` option, then:

```text
<tool> capture --trx <report.trx> --suite app-core --head <full-tested-commit> --run-id <run-id> --stage local
```

`--stage ci` identifies a CI report. The output includes an input digest, a stable record
id, result counts, and normalized `suite|test:Class.Method` signatures. Re-importing the
same report with the same metadata yields the same record id; downstream storage must
deduplicate by that id. Distinct test cases with the same method aggregate as occurrences.

The importer does not retain display names, theory arguments, diagnostic messages, stack
traces, attachments or machine paths. Unsupported outcomes, missing failure identities,
empty results, run-level failures without test failures and counter mismatches are visible
as `unparsed`, not silently counted as clean. Contradictory execution totals and nonzero
unsupported outcome counters are included in that accounting. Duplicate singleton report
sections, invalid XML and external entities are refused.
The head is labeled `declaredHead`: this tool cannot prove that the report ran on that SHA.
Capture records are observations, not diagnoses or proposed remedies.

### Freeze an evaluation checkpoint

The operator must independently select a **reviewed notes commit**, kept outside the
writer's control. A full SHA freezes bytes; it does not authenticate their author or
approval. There is intentionally no `approve` command, no branch-name fallback and no
working-tree note lookup. Git replacement objects are ignored, and lazy fetching is
disabled: missing objects fail locally instead of contacting a remote.

```text
<tool> checkpoint --repo <checkout> --code-commit <full-product-commit> --notes-commit <full-reviewed-notes-commit> --tasks <task-set-file> --harness <name> --harness-version <version> --model <name> --model-version <version>
```

Save stdout as `checkpoint.json` in the private experiment directory. A revision containing
only drafts is refused. The task file is hashed as exact bytes; its format is owned by the
evaluation driver. The checkpoint records the executing tool assembly's SHA-256, not a
claimed source revision. Harness/model identity is operator-supplied metadata, not telemetry.
Keep this checkpoint and the referenced Git objects available for the whole evaluation.

### Retrieve against that checkpoint

```text
<tool> search --repo <checkout> --checkpoint <private-directory>/checkpoint.json --tasks <task-set-file> --path src/AgentBridge.App/SelfTest.cs
```

Repeat `--path` for more paths. The task and tool digests and approved-note inventory must
still match the checkpoint. Only approved notes at its notes commit can be returned.
Changing or even committing a note in the writer's worktree does not change that snapshot.

For each applicable note, the tool compares its source-reference blobs and modes between
`verifiedCommit` and the checkpoint's product commit. A missing, non-regular or changed
reference withholds the note for re-verification. An expired note is also withheld, even
an invariant. This is intentionally conservative: unchanged references cannot prove that
all relevant semantics stayed unchanged, and the `staleWhen` prose is not executed.

Invariants sort first, then ids. A response returns at most 12 complete notes and 12,000
UTF-8 bytes of rendered note text; `--max-notes` and `--max-bytes` can lower those caps.
This is a byte budget, not an asserted tokenizer count, and excludes JSON transport overhead.
Omitted notes remain `pending`, with ids and blob revisions. Overlapping matches are
flagged without deciding that one overrides another. There is no implicit continuation or
delivery cache in this slice.

The output says **returned-only**: it does not prove that a model read or followed anything.
It describes the pinned product snapshot, not the current working tree. A future harness
must enforce that checkout, isolate agent profiles and credentials, deliver the exact
payload before edits, and measure that behavior before the paper's pilot can open.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Command completed. Capture may contain failures; validation does not imply approval. |
| 1 | Valid report needs attention: unparsed capture, or pending/withheld/overlapping retrieval. JSON is still emitted. |
| 2 | Invalid input, checkpoint mismatch, missing evidence shape, or read failure. No successful report is emitted. |

Do not turn code 1 into an empty or successful-looking result. Preserve its JSON and route
it for inspection. No command runs a shell command named in a lesson.

## Working while main changes

Develop and rebase **only the experimental worktree's branch** at deliberate checkpoints.
Do not change, reset, merge into or check out `main`, or operate in the primary checkout.
Follow the repository's usual review and pre-push rules if a push is later authorized.
The experiment itself requires no push or workflow dispatch.

An evaluation batch stays pinned while development moves. Use the same code, tasks, model,
harness and ordinary quality gates for control and treatment; only approved guidance is
the intended difference. Keep future fixes, reviewer answers and unreviewed drafts out of
both writer contexts. A rebase, tool change or task change starts a new checkpoint/batch,
not a silent update to an existing comparison. A note source change requires re-verification.

Record quality, time to accepted output, correction rounds, CI/agent cost, and preventable
human interventions separately from required approvals. Freeze outcome definitions and
limits before running trials; do not infer benefit from note counts or a handful of successes.

Merge back through small reviewed PRs only after authorization: first the read-only tooling,
then approved lessons, then separately approved activation. Evidence records are not product
changes. Product fixes produced in trials need their own ordinary review. The branch and
worktree are not a sandbox: live hook trials require a disposable environment without live
bridge access, production credentials or deploy permissions.
