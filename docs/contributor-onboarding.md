# Fresh-context contributor exercise

This checks discoverability and application of the contribution rules, not whether
someone remembers a briefing. The first owner-designated participant is Welshofer.
Scheduling, participation and results are **pending**; publishing these docs is not
evidence that a person or agent has adopted them.
Track results and follow-up fixes on
[#1045](https://github.com/danswett/agent-bridge/issues/1045).

## Facilitator setup

After the contributor docs land, select a small, unclaimed documentation task with
clear acceptance criteria, or agree a bounded slice with its accepted owner.
Do not use a production incident, permission change or live-host experiment.
Record the repository commit under test and the task issue. The task must not be
the onboarding implementation itself.

Give the participant only the repository URL, the task issue and the prompt below.
Do not send direct links to the answers first. They should use a fresh checkout
and fresh agent sessions, not resume the implementing session.
Use normal trust and permission approvals; do not disable safety instructions.
Record any pre-existing personal instructions or memories that supply project
rules, without copying private contents. Such a run can expose usability problems
but is not proof of a context-free start.

```text
Repository: https://github.com/danswett/agent-bridge
Task: <canonical issue URL>

Use the repository to work out how you and your agents should contribute.
Before editing, describe the applicable rules and where you found them, establish
whether this task is available, and record your accepted scope.
Then make the agreed small change, verify it, open a PR through the normal process,
and leave a handoff that another fresh session can act on.
Note every step that needed help or an assumption. Do not change any live system.
```

## Observe without coaching

Have the person find the entry path first, then let their fresh agent independently
inspect the repository. Record the agent tool and version, starting directory,
instruction files actually loaded and repository commit. Check the tool's
instruction display where available; file presence alone does not prove loading.
Do not copy the human's answers into the agent prompt before observing discovery.
The agent still needs the person's accepted task scope before implementation.
For Copilot CLI, `/instructions` displays custom instruction files.

| Check | Evidence needed |
|---|---|
| Finds the entry path | Person finds the contributor guide from README; agent finds AGENTS.md directly or through its tool entry point |
| Identifies boundaries | Names worktree isolation, live-system restrictions and person-versus-agent permission boundaries, with source links |
| Avoids ownership collisions | Reads issue/PR activity, records bounded acceptance and obtains existing-owner agreement if needed |
| Chooses appropriate participation | Does not self-enroll as a standing coordinator or start reporting timers, collectors or a worker fleet |
| Uses canonical records | Scope/dependencies stay on the issue; head-specific verification and reviews stay on the PR |
| Verifies and requests landing correctly | Reports actual selected checks and missing checks; follows AGENTS.md rather than treating a checkbox as a merge grant |
| Leaves usable state | Another fresh session can identify the exact head, remaining work, blockers and accepted next owner or unclaimed handoff |

Include two read-only questions: "What if the private Project is inaccessible?"
and "What if an old handoff conflicts with the current issue?"
The expected behavior is to surface unknowns and ask the responsible authority,
not infer consent or bypass a gate. Do not create real permission failures to test this.

## Record results and fix the entry path

Post one result on #1045, linking the participant's issue and PR:

```text
Repository commit tested:
Participant and agent tool/version:
Fresh context and instruction-discovery evidence:
Task issue and PR:
Each check: passed / needed help / blocked, with public evidence:
Confusing step or missing rule:
Next documentation fix and accepted owner:
```

Do not record an outcome as passed without observed evidence. If the person had to
explain a repository rule the agent could not find, improve the entry path instead
of adding that rule only to a private prompt. Repeat only the failed step in a new
fresh context after the fix. Technical PR checks and human onboarding results are
separate: green CI cannot establish that the contributor exercise passed.
