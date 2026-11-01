# Parity with agent-ha-bridge

What agent-ha-bridge does that a person can see or rely on, and whether agent-bridge does it
too, with the evidence. This is the audit the migration plan starts from (design doc §10,
"Path to migration", stage 0). A machine leaves agent-ha-bridge's PowerShell daemon only
when the rows that matter to it are `Verified`, or `Intentionally different` for a stated
reason.

* **Spec:** agent-ha-bridge at `8b9f2f4` (1.33.33): `frontend/agent-bridge-reply-card.js`,
  the dashboard generator (`Save-CopilotSessionDashboard` in
  `hooks/decision-ha-websocket.ps1`, `hooks/bridge-frontend-cards.ps1`), `README.md`,
  `mcp/`, `hook/`, `hooks/`, `claude/`, `codex/`, `install.ps1`, `uninstall.ps1`, and
  every test suite (87).
* **Audited:** agent-bridge at `2a25120` (#36). Every status below was checked against the
  code and tests, not only the docs. The tests are named `Class.Method` (xUnit) or
  `file: "title"` (PWA).
* **Live evidence** is a run recorded in the design doc's **Progress** section
  (`agent-bridge-standalone-app-design.md`, "Progress (2026-10-28)"). It was done on a
  disposable Hyperloop VM from an iPhone and the Windows app, and is cited as
  "Live: Progress › *item*".

| Status | Meaning |
|---|---|
| `Verified` | A test **and** a recorded live run |
| `Implemented, untested live` | Code and tests, no live run yet. Live runs are tracked by #39 ([parity-checklist.md](parity-checklist.md)) |
| `Partial` | Some of it; the gap column says what is missing |
| `Missing` | Not built; an issue is linked |
| `Intentionally different` | Changed on purpose; the decision is cited |
| `N/A` | Home Assistant plumbing the design replaces (§2, §6.1 "Deleted, not ported") |
| `In flight (<slice>)` | Owned by a slice already under way, with no separate issue: **reclaimer**, **Claude/Codex live** (Claude and Codex live tests on the VM), **HA connector** (agent-ha-bridge's per-machine entity contract, so a migrated machine still shows on the old dashboard) |

Agents: **Cp** Copilot CLI, **Cl** Claude Code, **Cx** Codex, **All**.

## Summary

| Status | Rows |
|---|---|
| `Verified` | 49 |
| `Implemented, untested live` | 46 |
| `Partial` | 1 |
| `Missing` | 13 |
| `Intentionally different` | 18 |
| `N/A` | 12 |
| `In flight (…)` | 8: Claude/Codex live 8 |
| **Total** | **147** feature rows in §1–§18 |

Of the 15 §6.3 invariants: 8 `Verified`, 6 `Implemented, untested live`, 1 `Partial`. Every `Missing` and `Partial` row links an issue labelled
[`parity`](https://github.com/danswett/agent-bridge/issues?q=label%3Aparity).

## 1. Session cards

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| A session appears as soon as its CLI opens, before it has written a transcript | Cp | README "Start a conversation" | `Verified` | `DiscoveryTests.A_Copilot_session_that_has_not_written_a_transcript_yet_is_still_listed_as_idle`. Live: Progress › Launch (registered in 2.8 s) | |
| Claude and Codex sessions are listed while their process runs and retired when it exits or Codex ends | Cl, Cx | `claude/hooks/claude-session.ps1`, `codex/hooks/codex-hooks.ps1` | `In flight (Claude/Codex live)` | `DiscoveryTests.A_Claude_registration_is_a_session_while_its_process_runs_and_is_removed_when_it_exits`, `An_ended_Codex_registration_is_not_a_session_even_while_its_process_still_runs` | Reads agent-ha-bridge's registrations. After migration they must come from the host (#37) |
| Status working, waiting or idle, from the transcript; the turn end is idle | Cp | `hooks/daemon-activity.ps1`; `tests/test-turn-end.ps1` | `Verified` | `CopilotTests.A_prompt_makes_it_working_tool_rounds_keep_it_working_and_a_turn_without_tool_requests_ends_it_idle`, `A_pending_ask_user_makes_it_waiting_until_the_human_response_is_recorded`. Live: Progress › Answers, Steering | |
| The same for Claude (Stop hook status wins over the transcript) and Codex (task events, registration status) | Cl, Cx | `claude/hooks/claude-transcript.ps1`, `codex/hooks/codex-transcript.ps1` | `In flight (Claude/Codex live)` | `AgentTests.A_hook_status_newer_than_the_transcript_overrides_what_the_transcript_implies`, `Task_started_and_complete_drive_status_and_the_older_event_set_fills_the_activity`, `A_registration_status_newer_than_the_rollout_is_used` | |
| Current tool, last response as Markdown, last user message, title | All | card `agent-bridge-session-card`, `agent-bridge-activity-card` | `Verified` | `pwa/test/ui.spec.mjs: "the latest response is rendered as Markdown"`, `ModelTests.Headings_paragraphs_emphasis_and_code_become_styled_runs`, `CopilotTests.Without_a_workspace_name_the_title_is_the_first_prompt_on_one_line`. Live: Progress › Windows app (cards), iPhone cards | |
| Reasoning and the tool history on the card | Cp, Cl, Cx | README "Detailed activity"; `tests/test-detailed-activity.ps1` | `Verified` | `LocalApiTests.Getting_a_session_returns_its_tool_history`, `pwa/test/ui.spec.mjs: "opening the tool history fetches the session with session.get"`. Live: Progress › Subagent rule | Claude and Codex reasoning are in flight (Claude/Codex live) |
| `detailedActivity: false` and the per-machine Detail toggle hide reasoning and tools | All | `config.json` `detailedActivity`; status card Detail toggle | `Intentionally different` | Every client gets the full activity; the PWA folds reasoning and history under `<details>`, and the app has "show reasoning and tools" (`docs/app-windows.md` › The app; `Stores.cs` `ShowDetail`) | Design §9: the toggle is no longer an HA helper. On the old dashboard, a machine on the HA connector keeps its Detail switch, created when it has none (stage 7) |
| A subagent's words and tools reach its parent's card; its prompt and model are not the session's | Cp | `hooks/daemon-activity.ps1` | `Verified` | `CopilotTests.A_subagents_words_and_tools_reach_the_card_but_its_turns_prompt_and_model_are_not_the_sessions`. Live: Progress › Subagent rule | Labelling which subagent wrote it is an enhancement (#20) |
| An idle session waiting for background agents or commands reads as delegating, not idle | Cp | README "Background agents"; `Update-DaemonBackgroundAgents` | `Implemented, untested live` | `BackgroundTests` (13), `pwa/test/ui.spec.mjs: "an idle session still waiting on background work is delegating…"` | #39 |
| Compaction count beside the status | All | card ~line 1320 ("compacted once", "compacted N times") | `Implemented, untested live` | `CopilotTests.Only_finished_compactions_are_counted_and_the_model_follows_model_changes` | Worded "N compactions" (#53). #39 |
| Model shown, following model changes | All | `hooks/daemon-activity.ps1` | `Implemented, untested live` | `CopilotTests.Only_finished_compactions_are_counted_and_the_model_follows_model_changes`; `pwa/js/ui/session-card.js` meta line | #39 |
| Glow: blue while working, amber while waiting, steady edge while delegating; still under reduced motion | All | README "What you get"; card glow | `Implemented, untested live` | `pwa/test/ui.spec.mjs: "each status has its glow…"`, `"with reduced motion the glow holds still and the spinner stops"`; `SessionCard.xaml` | #39 |
| Purple edge on a session an agent started or replied to | All | README "Telling an agent's turn from yours"; `tests/test-driver-card.ps1`, `test-agent-launched.ps1` | `Implemented, untested live` | `Session.driver` and `driverPrincipal` ([v1-sessions.md](protocol/v1-sessions.md#driver)) from the host's one record (`SessionDrivers`), shared with the Home Assistant cards: `DriverTests` (Host.Core), `ReplyFlowTests.A_reply_from_a_device_takes_the_session_back_from_an_agent_and_one_refused_leaves_it_with_the_agent`, `ContractTests.A_session_is_sent_as_the_persons_by_leaving_the_driver_out_and_an_unknown_driver_reads_as_the_persons`; the PWA (`ui.spec.mjs`, purple edge and reduced motion) and the app (`CardTests.A_session_an_agent_drives_says_so_with_its_principal_until_the_person_takes_it_back`). Agents reach a host only through its Home Assistant cards until they have a principal on the pipe (#45) | #46 |
| Spinner verbs while working | All | card `SPINNER_VERBS` | `Missing` | Plain spinner on the tool line (`pwa/app.css` `.spinner`) | #53 |
| Fold a card, remembered per session; a new question unfolds it | All | card fold state | `Implemented, untested live` | `pwa/test/ui.spec.mjs: "folding is remembered across a reload, and a new question unfolds the card"`, `CardTests.A_folded_card_stays_folded_across_restarts_until_a_new_question_opens_it_once` | #39 |
| Long text is bounded (response 6,000, reasoning 4,000 characters) | All | `agent-bridge-daemon.ps1` `ResponseMaxChars`, `ReasoningMaxChars` | `Intentionally different` | Byte budgets per session; a cut question is read-only (`ContractTests.A_session_too_big_for_the_budget_loses_its_oldest_history_first_and_keeps_its_question`, `AnswerTests.A_truncated_question_is_never_answered_remotely`) | `docs/protocol/v1-sessions.md` budgets |
| Unknown statuses, agents and fields from a newer bridge read safely | All | card version tolerance | `Implemented, untested live` | `ContractTests.A_client_reading_a_newer_host_ignores_fields_it_does_not_know`, `pwa/test/ui.spec.mjs: "an unknown status shows as idle…"` | Exercised at the first mixed-version update |

## 2. Questions and answers

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Free-text `ask_user`: the words, then Enter as a keystroke of its own | Cp | `hooks/decision-inject.ps1` | `Verified` | `CopilotPlannerTests.A_free_text_question_types_the_words_then_presses_Enter_as_its_own_keystroke`. Live: Progress › Windows app (free text with emoji and accents) | |
| Single choice: Up×(N−1) to the top, Down to the row, Enter | Cp | `hooks/decision-inject.ps1` | `Verified` | `CopilotPlannerTests.A_choice_walks_up_to_the_top_wherever_the_caret_was_left_then_down_to_the_chosen_row_and_commits_with_Enter`. Live: Progress › Answers (single choice), Windows app (card and toast) | |
| Forms of any number of fields (choice, boolean, text, number), each committed with its own Enter | Cp | `Get-BridgeFormPayloads`, `Send-CopilotSessionForm`; `tests/test-decision-args.ps1` | `Verified` | `CopilotPlannerTests.Each_field_of_a_form_is_answered_in_order_with_its_own_Enter_and_a_pause_after_it`, `pwa/test/answer.spec.mjs` (forms). Live: Progress › Answers (five-field form, é and 🚀 exact) | |
| Multi-select checkboxes, toggling only rows that must change | Cp | `tests/test-choices-form.ps1` | `Implemented, untested live` | `CopilotPlannerTests.A_multi_select_list_is_walked_once_from_its_top_row_wherever_the_caret_was_left_toggling_only_rows_that_must_change` | `docs/host-answers.md`: "Up stops at the first row of a checkbox list" is assumed, not seen. #39 |
| Other (type your answer): Up×(N−1), Down×N, the words, a single Enter (#240) | Cp | `hooks/decision-inject.ps1`; `tests/test-answer-match.ps1` | `Verified` | `CopilotPlannerTests.Words_through_Other_walk_up_to_the_top_then_down_past_the_last_option_and_submit_separately`, `TranscriptAndVerifierTests.Words_through_Other_match_only_when_the_words_were_recorded_and_not_an_option`. Live: Progress › Answers (Other with smart quotes) | |
| Other inside a multi-field form or a checkbox list | Cp | No walk in agent-ha-bridge either | `Intentionally different` | Refused (`CopilotPlannerTests.Other_in_a_tabbed_form_or_a_checkbox_list_is_refused_because_no_walk_reaches_it`) | `docs/host-answers.md` › What can be answered |
| The older `question` / `choices` format | Cp | Not driven by agent-ha-bridge | `Intentionally different` | Shown, refused for typing (`CopilotPlannerTests.The_older_choices_format_and_a_default_that_names_no_option_are_refused_before_planning`) | `docs/host-answers.md` |
| Non-Latin-1 text typed exactly (dashes, smart quotes, CJK, emoji) | All | Broken before agent-ha-bridge #252 | `Verified` | `CopilotPlannerTests.Emoji_and_CJK_are_typed_as_they_are_and_long_words_go_in_one_burst`. Live: Progress › Answers (é 🚀), Other (smart quotes) | |
| The terminal and the remote are both live; the first answer wins and a late one is stale | All | README "Dual input"; `tests/test-decision-lifecycle.ps1` | `Verified` | `AnswerFlowTests.An_answer_to_a_question_no_longer_asked_is_stale_and_types_nothing`, `AgentAnswerFlowTests.A_Claude_question_answered_at_the_machine_a_moment_ago_is_stale_and_nothing_is_typed`. Live: Progress › Push (cleared once answered) | |
| An answer counts only when the transcript records it; otherwise `unconfirmed` | All | `tests/test-answer-match.ps1`, `test-p3b-contracts.ps1` | `Verified` | `AnswerFlowTests.An_answer_the_transcript_then_records_is_delivered_after_typing_exactly_the_planned_keys`, `A_different_recorded_answer_or_none_at_all_is_unconfirmed_never_delivered`. Live: Progress › Answers | |
| One typing attempt per prompt, kept across restarts | All | `Start-CopilotDecisionAttempt`, `DeliveryAttempted`; `tests/test-restart-restore.ps1` | `Implemented, untested live` | `AgentAnswerFlowTests.A_prompt_once_typed_into_is_never_typed_into_again_by_a_new_request_or_after_a_restart`, `ClaudeAndCodexPlannerTests.A_prompt_is_claimed_once_across_restarts_and_only_a_release_frees_it` | #39 |
| A resent request returns the first outcome and types nothing | All | `tests/test-p3b-contracts.ps1` (exact-once) | `Implemented, untested live` | `AnswerFlowTests.A_repeated_request_returns_the_first_outcome_and_never_types_again`, `pwa/test/e2e/e2e.spec.mjs: "…an answer in flight when the socket drops is resent and typed once"` (through a real relay in CI) | #39 |
| **Cancel request** row: decline the question in one tap | All | `CancelOption`, `Read-DaemonFormAnswer`; `tests/test-choices-form.ps1` | `Missing` | No decline control | #56 |
| Claude `AskUserQuestion`: one to four questions, single or multi | Cl | `Complete-DaemonClaudeAnswer`; `claude/tests/test-claude-ask-parser.ps1` | `In flight (Claude/Codex live)` | `ClaudeAndCodexPlannerTests` (9), `AgentAnswerFlowTests.A_Claude_answer_the_transcript_then_records_by_its_tool_use_id_is_delivered` | |
| Claude's review screen gets exactly one more Enter | Cl | `Complete-DaemonClaudeAnswer` | `In flight (Claude/Codex live)` | `AgentAnswerFlowTests.A_Claude_prompt_left_on_its_review_screen_gets_exactly_one_more_Enter` | |
| Claude free text ("Type something") and an empty multi-select | Cl | `Complete-DaemonClaudeAnswer` (marked "not re-verified") | `Missing` | Refused: `ClaudeAndCodexPlannerTests.An_empty_multi_select_and_other_words_are_refused_since_no_walk_is_proven_for_them` | #42 |
| Question forms on the phone with no 255-character, position-encoding or 10-option limits | All | `tests/test-choices-form.ps1`, `test-decision-args.ps1` [HA limits] | `Intentionally different` | `pwa/test/answer.spec.mjs` (16 KiB request, 4 KiB text) | Design §1 parity contract: HA-forced limits go |
| Draft text survives a question changing or the host restarting | All | Card "Not used as the answer" restore | `Implemented, untested live` | `pwa/test/answer.spec.mjs: "text typed for a question that is replaced is kept…"`, `CardTests.Words_typed_for_a_question_that_changes_are_kept_on_the_card` | #39 |

## 3. Permission prompts and approvals

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Copilot permission prompts approved once or denied from another device | Cp | Mirrored as an alert only | `Verified` | `CopilotPermissionFlowTests` (19), `CopilotPermissionTests` (15). Live: Progress › Copilot permission prompts | Beyond parity (decided 2026-10-28) |
| A machine can turn remote approval off | Cp | — | `Implemented, untested live` | `HostSettingsTests.Remote_approval_is_on_without_a_file_or_a_setting_and_off_only_when_set_false`, `SettingsTests.Turning_off_approval_from_other_devices_writes_it_through_the_host_and_offers_a_restart` | #39 |
| Codex approvals: Approve types `y`, Deny `n`, then Enter; always `unconfirmed` | Cx | `Invoke-PendingCodexApprovals` | `In flight (Claude/Codex live)` | `AgentAnswerFlowTests.A_Codex_approval_types_y_or_n_and_is_unconfirmed_since_Codex_records_no_answer_to_it` | The marker is written by agent-ha-bridge's hook only after it reaches HA. On a migrated machine the host's intake writes it for every approval (`IntakeTests.A_Codex_approval_is_recorded_waiting_with_a_marker_the_answer_path_reads_and_no_Home_Assistant`) |
| Claude permission prompts | Cl | Reported; the reply box only, best effort | `Intentionally different` | Shown as waiting at the machine (`AgentTests.A_waiting_hook_over_a_running_tool_is_an_approval_for_that_tool`) | `docs/host-answers.md` › What can be answered |
| A prompt only the machine can answer says so and offers nothing | All | Terminal-only questions offer no buttons (`tests/test-answer-buttons.ps1`) | `Implemented, untested live` | `pwa/test/ui.spec.mjs: "a permission prompt only the machine can answer shows no buttons…"`, `PushNotifierTests.A_prompt_only_the_machine_can_answer_is_notified_as_waiting_at_the_machine` | #39 |

## 4. Replies and attachments

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Reply to a session, typed as one line then Enter, confirmed in the transcript | Cp | `Send-CopilotSessionPrompt`; `tests/test-daemon-replies.ps1` | `Verified` | `ReplyFlowTests.A_reply_the_transcript_then_records_as_the_persons_prompt_is_delivered_typed_as_one_line_then_Enter`. Live: Progress › Replies | |
| Reply to a working session (steering or queued) | Cp | `Test-DaemonReplyBoxFree` | `Verified` | `ReplyFlowTests.A_reply_to_a_working_session_is_delivered_once_copilot_takes_it_at_its_next_step`. Live: Progress › Steering | |
| Replies to Claude and Codex | Cl, Cx | `Invoke-DaemonReply`, `Confirm-DaemonClaudeSubmit` | `Verified` | Typed only into the empty input box seen on screen, confirmed by identity, Enter again only while the box holds the reply (`AgentReplyFlowTests`; live on the VM, #41) | Without hooks, a question or permission prompt is seen only on screen ([host-replies.md](host-replies.md#claude-code-and-codex)) |
| A leading `/` or `!` is refused | All | `hooks/daemon-replies.ps1` | `Verified` | `ReplyTests.A_slash_or_bang_command_is_refused_wherever_leading_space_or_control_characters_hide_it`. Live: Progress › Replies (`/help` refused) | |
| A reply waits while a question or permission prompt is open | All | `Test-DaemonReplyBoxFree` | `Verified` | `ReplyFlowTests.While_the_session_asks_a_question_a_reply_is_refused_untyped_even_before_the_host_has_read_it`, `CopilotPermissionFlowTests.A_reply_waits_while_a_permission_prompt_that_can_be_answered_is_pending`. Live: Progress › Replies | |
| Ctrl/Cmd+Enter sends; Enter is a new line; text is kept until it is consumed | All | Reply card | `Implemented, untested live` | `pwa/test/reply.spec.mjs: "Ctrl+Enter or Cmd+Enter sends…"`, `"words typed while a reply is on its way stay in the box…"`, `CardTests.A_reply_goes_with_its_files_sent_first_in_chunks_and_leaves_what_was_typed_meanwhile` | #39 |
| Paste an image or attach a file (up to 256 KiB), mentioned as `@path` | Cp | `New-BridgeAttachmentPrompt`, `Save-BridgeReplyFile` | `Verified` | `ReplyFlowTests.A_reply_with_files_sent_first_mentions_each_ahead_of_the_words_and_is_delivered_once_copilot_attaches_them`, `pwa/test/attach.spec.mjs`. Live: Progress › Attachments (#27) | |
| Images of any size (HA image store) and files over 256 KiB | All | `/api/image/upload` | `Implemented, untested live` | Through the relay's blob store, end-to-end encrypted (#61; [blobs.md](protocol/blobs.md)): `BlobTests`, `pwa/test/attach.spec.mjs`; from the Windows app to another machine, `CardTests.On_another_machine_a_file_over_256_KiB_goes_sealed_through_the_relays_blob_store_not_in_chunks`, `RelayFleetLinkTests.A_large_file_goes_sealed_through_the_relays_blob_store_to_the_one_machine_it_is_for` | #39 |
| Attachments to Claude and Codex | Cl, Cx | `New-BridgeAttachmentPrompt` | `Verified` | Claude: `@<path>`, read back from its `attachment` entries; Codex: images pasted as paths, read back as `local_image` (`AgentReplyFlowTests`; live on the VM, #41) | Codex takes images only; other files are refused |
| Attachments staged in a protected per-installation folder, never shared TEMP; swept after 24 h | All | `Get-BridgeAttachmentRootForInstallation`, `Remove-BridgeStaleAttachment`; `tests/test-p5-attachment-privacy.ps1` | `Verified` | `AttachmentStoreTests.Staged_files_go_in_a_folder_only_this_user_can_open_named_safely_with_the_extension_their_bytes_call_for`, `The_sweep_deletes_only_old_files_it_staged_and_nothing_through_a_linked_folder`. Live: Progress › Attachments | |

## 5. Ending a session

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| One press when idle; two presses within 10 s otherwise, enforced by the host | All | `Invoke-PendingStops`, `StopConfirmSeconds`; `tests/test-stop-session.ps1` | `Verified` | `StopFlowTests.A_working_session_takes_a_second_press_with_the_token_the_first_returned_and_the_first_press_does_nothing`, `pwa/test/stop.spec.mjs`. Live: Progress › End session | The second press carries a token (`docs/host-stop.md`) |
| A lapsed confirmation says it was not confirmed | All | "End session NOT confirmed" | `Verified` | `pwa/test/stop.spec.mjs: "a second press after the window lapsed…"`. Live: Progress › End session (lapsed after 10 s) | |
| `/exit`, then terminate after 12 s; the same process (pid and start time) only | All | `Stop-BridgeCopilotSession` | `Verified` | `StopFlowTests.A_session_that_does_not_exit_on_exit_is_terminated_after_the_grace_and_says_so`, `A_confirmation_never_ends_a_process_that_replaced_the_one_the_first_press_saw`. Live: Progress › End session | |
| A session asking a question is terminated without `/exit` being typed into the question | All | Typed `/exit` into it | `Intentionally different` | `StopFlowTests.A_session_asking_a_question_is_terminated_without_exit_being_typed_which_would_answer_it`. Live: Progress › End session (blocked on a permission prompt) | `docs/host-stop.md` › What this host does |
| Ending a Claude or Codex session | Cl, Cx | Same `/exit` path | `In flight (Claude/Codex live)` | `StopFlowTests.A_claude_session_ends_the_same_way_with_exit_typed_into_its_own_process` | |
| Status reads `ending`, then the card goes | All | `ending` status | `Verified` | `StopFlowTests.An_idle_session_ends_on_one_press_with_exit_typed_into_its_console_shows_as_ending_and_is_then_removed`. Live: Progress › End session | |
| Closes the console window the bridge launched | All | `Stop-BridgeCopilotSession` | `Intentionally different` | The host's own launches get their own console, which closes with the agent | `docs/host-stop.md` |

## 6. Launch and resume

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Launch in an approved workspace with an opening prompt of any length | All | `Start-BridgeCopilotSession`; `tests/test-new-session.ps1` | `Verified` | `LaunchFlowTests.A_new_session_starts_in_its_workspace_with_the_whole_prompt_and_the_answer_names_the_session_it_became`, `pwa/test/launch.spec.mjs`. Live: Progress › Launch (300-character prompt) | Copilot live. Claude and Codex are in flight (Claude/Codex live) |
| Workspaces are an explicit allowlist; system folders are refused; discovery never approves | All | `Test-BridgeWorkspacePathApproved`, `Test-BridgeSystemDirectory`; `tests/test-install-workspaces.ps1` | `Verified` | `LaunchPieceTests.System_folders_and_drive_roots_are_never_places_to_launch`, `HostSettingsTests.A_workspace_is_approved_at_the_machine_with_the_cli_and_removed_the_same_way`. Live: Progress › Launch (`workspace add`) | |
| Agent: Agency, Copilot, Claude, Codex; `auto` | All | `Resolve-BridgeLauncher` | `Intentionally different` | `auto` is the first installed, not the most recently used (`docs/host-launch.md`) | |
| Agency profiles: offered, validated, a missing one refused | Cp | `Resolve-BridgeAgencyProfile` | `Implemented, untested live` | `LaunchFlowTests.Agency_starts_with_the_chosen_profile_and_a_profile_it_does_not_list_is_refused`, `LaunchPieceTests.Agencys_listings_are_read_as_it_prints_them` | #39 |
| Model, effort and context per agent, checked against what the machine offers | All | `Resolve-BridgeLaunchTuning` | `Implemented, untested live` | `LaunchFlowTests.Settings_this_machine_does_not_offer_are_refused_before_anything_starts`, `The_machines_defaults_fill_what_a_launch_leaves_out_and_an_empty_value_asks_for_the_agents_own` | Models are not discovered by starting each agent (`docs/host-launch.md`). #39 |
| Permissions: ask, or Allow all (each agent's flag), chosen per launch | All | `newSession.allowAllTools` | `Implemented, untested live` | `LaunchPieceTests.Nothing_is_approved_and_permission_prompts_stay_on_until_a_person_says_otherwise` | #39 |
| Extra arguments and agent paths | All | `newSession.extraArgs`, `*Path` | `Implemented, untested live` | `docs/host-config.md` (`launch.agents.<id>.extraArgs`, `.path`) | #39 |
| Resume: recent sessions in approved workspaces, reopened in their own folder under their own id | All | `Get-BridgeResumableSessions`; `tests/test-resume-discovery.ps1` | `Verified` | `LaunchFlowTests.A_resume_reopens_the_session_in_its_own_folder_with_its_own_id`. Live: Progress › Launch (resumed under its own id) | |
| Live sessions are never offered for resume | All | `Get-BridgeResumableSessions` | `Implemented, untested live` | `LaunchFlowTests.The_options_list_installed_agents_approved_workspaces_and_only_resumable_sessions_in_them`, `A_session_being_resumed_is_not_resumed_again_by_another_press_until_its_agent_has_gone`, `While_Copilot_discovery_is_held_none_of_its_sessions_is_offered_for_resume_and_resuming_one_is_busy` | #43's live check (#39) |
| Folder trust is never answered on a press of Launch; a launch meeting it is `busy`. A person may answer it from their phone, browser or Windows app (Trust or Quit, a command that device signs) where the machine turns `launch.remoteFolderTrust` on; off by default, it is shown read-only | Cl, Cp, Cx | Answered on a second press (Codex's folder question not recognised) | `Intentionally different` | `LaunchFlowTests.A_folder_trust_question_is_left_for_the_machine_and_other_launches_are_busy_meanwhile`, `A_Copilot_that_registers_and_then_asks_whether_to_trust_its_folder_is_left_for_the_machine_and_other_launches_are_busy`, `A_Codex_asking_whether_to_trust_its_folder_is_left_for_the_machine_and_other_launches_are_busy_meanwhile`, `A_person_trusts_a_waiting_Codex_folder_from_their_phone_and_the_launch_completes`, `Only_the_persons_own_device_with_a_command_for_that_choice_and_question_can_answer` | §6.3 invariant 11. Run live for Copilot and Claude in never-trusted folders (#74), and for Codex resumed by a transfer into a folder it had not trusted. Answered from devices live on a VM (#133, #135, #137; 2026-10-31): Trust and Quit for each agent from the Windows app (this machine and another through the relay) and the PWA; read-only when off or for a device not approved. Claude's Bypass Permissions warning is named as itself, not as folder trust (#149) |
| Codex launched with no prompt | Cx | `newSession.codexStartPrompt` | `Intentionally different` | Refused: `LaunchFlowTests.Codex_is_launched_only_with_a_first_message_and_its_launch_is_bound_to_the_session_it_chose` | `docs/host-launch.md` |
| A launch's result: started (with the session), refused, failed, busy, unconfirmed | All | Launch result sensor, request ids | `Verified` | `pwa/test/unit/launch.test.mjs`, `LaunchFlowTests.A_resent_launch_gets_the_first_outcome_and_starts_nothing_more`. Live: Progress › Launch | |
| Launch from the Windows app | All | Launch card | `Verified` | The app's Launch page starts or resumes a session on any machine that offers `sessions.launch`, with that machine's choices and the whole prompt (`LaunchTests`, `RemoteLaunchTests`; the self-test launches from it). Live (2026-10-29, #49): on the VM the app launched Copilot in claude-test (unconfirmed while Copilot asked whether to trust the folder, then registered once a person answered), resumed an ended Claude session, and launched Claude on a second host through the relay | #49 |
| A batch-file agent (`copilot.cmd`) cannot be broken out of by the prompt | All | Quoted for `CommandLineToArgvW` only | `Intentionally different` | `LaunchPieceTests.A_line_too_long_for_cmd_exe_is_refused_rather_than_cut`; `docs/host-launch.md` (CVE-2024-24576 quoting) | Stricter |

## 7. Worktree isolation

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| `isolate: true`: a detached worktree at a freshly fetched base, marked, locked while starting | All | `New-BridgeSessionWorktree`; `tests/test-worktree-isolation.ps1` | `Verified` | `LaunchPieceTests.An_isolated_launch_gets_a_detached_worktree_at_the_fetched_base_named_by_time_and_marked_as_this_hosts`, `LaunchFlowTests.An_isolated_workspace_starts_each_session_in_a_worktree_of_its_own_and_unlocks_it_once_it_registers`. Live: Progress › Launch | |
| Fails closed; a cap per repository with no fallback to the primary clone | All | `newSession.worktreeLimit` | `Implemented, untested live` | `LaunchPieceTests.The_cap_counts_only_this_hosts_worktrees_and_refuses_rather_than_falling_back`, `A_folder_outside_git_fails_closed` | The cap has not been hit live (#39) |
| Reclaim finished worktrees: marked, unlocked, clean, detached, idle; the given repository only | All | `Remove-BridgeFinishedWorktree`; `tests/test-p4-worktree-safety.ps1` | `Implemented, untested live` | `WorktreeReclaimerTests` (#58); held while discovery is: `WorktreeReclaimerTests.Nothing_is_reclaimed_while_session_discovery_is_held_and_finished_trees_go_once_it_is_complete`, `A_hold_that_begins_while_a_worktree_is_being_checked_keeps_it` | #39 |
| Neither product counts or claims the other's trees | All | Marker `agent-bridge-created` | `Intentionally different` | Marker `agent-bridge-host-created` (`docs/host-launch.md`) | While migrated nobody reclaims the old trees; a rollback resumes agent-ha-bridge's reclaimer, and a machine leaving agent-ha-bridge for good runs its `Remove-BridgeFinishedWorktree` once (`docs/migration.md`) |

## 8. Agent usage

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Copilot allowance read live with the CLI's credential, else its cache, and says why | Cp | `hooks/daemon-usage.ps1`; README "Agent usage" | `Verified` | `UsageTests.Copilot_reads_the_signed_in_logins_allowance_from_GitHub_with_the_token_the_CLI_would_spend`, `Copilot_falls_back_to_the_newest_cached_answer_for_the_same_login_and_says_why`. Live: Progress › Usage (cache on the VM) | |
| Claude session and weekly windows, grouped by an account digest; an expired token is never spent | Cl | `daemon-usage.ps1` | `In flight (Claude/Codex live)` | `UsageTests.Claude_reads_every_window_its_plan_has_with_the_week_start_for_pace`, `Claude_is_told_apart_by_a_digest_of_its_account_and_organisation_never_by_either` | |
| Codex 5-hour and weekly windows from its transcripts | Cx | `daemon-usage.ps1` | `In flight (Claude/Codex live)` | `UsageTests.Codex_reports_the_newest_rate_limits_its_transcripts_recorded` | |
| Pace: 2 min with a session, 15 min without, at least 30 s apart; republished every 30 min | All | README "How current it is" | `Implemented, untested live` | `UsageTests.An_agent_with_no_session_here_is_asked_every_quarter_hour_and_one_with_a_session_every_two_minutes`, `An_unchanged_reading_is_announced_again_only_after_half_an_hour` | #39 |
| Bars with pace mark, amber at 75 %, red at 90 %, "resets in"; one row per account across machines | All | Usage card | `Verified` | `pwa/test/unit/usage.test.mjs`, `ModelTests.Rows_are_fullest_first_coloured_at_75_and_90_with_their_pace_and_reset`. Live: Progress › Usage (matches the HA card) | |
| A failure is shown only beside a reading over an hour old | All | README "When a read fails" | `Implemented, untested live` | `ModelTests.A_failure_beside_a_fresh_reading_is_not_shown_but_one_beside_an_old_reading_is`, `pwa/test/unit/usage.test.mjs: "an error is shown unless there is a reading that is not stale"` | #39 |
| `usage.copilotAccounts`, `usage.publish: false`, the pace settings | Cp, All | `config.json` `usage.*`; README "More than one Copilot account" | `Implemented, untested live` | `UsageSettings`, `CopilotAccountCollector` (`gh auth token --user <login>` only, an answer for another login refused). `UsageTests.A_further_account_is_read_only_with_the_token_gh_holds_for_exactly_that_login`, `Further_accounts_are_read_one_a_pass_longest_waiting_first_at_the_idle_pace_whatever_is_open`, `LifecycleTests.A_further_copilot_login_goes_on_the_numbered_sensor_of_its_place_and_one_no_longer_listed_is_withdrawn`, the contract fixture's `usage_copilot_2` and `_4`. Live: `agent-bridge-host usage` on DSWETT-DEV-VM1 read both `gh` logins | Both rows on the phone (E8-04); `migrate` carries `usage.*` once #68 lands |
| `usage.keychain` (macOS) | Cp | `config.json` `usage.keychain` | `Missing` | The macOS keychain item is never read (`CopilotCredentials.cs`) | #52 |
| Only the publication writer asks for a shared account's figure | All | README "Only one machine asks" | `N/A` | Clients merge readings by account (`pwa/test/unit/usage.test.mjs: "one allowance read by two machines is one row…"`) | Tied to the HA writer role |

## 9. Machines, fleet and updates

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Every machine's sessions on one screen | All | One dashboard; `Get-DaemonPeerMachines` | `Verified` | The Windows app shows every machine's sessions together, each card naming its machine, with a picker for one machine (`FleetTests.Each_other_machine_the_relay_lists_gets_its_cards_and_this_machines_own_host_is_not_listed_twice`); the PWA does the same with its Machines panel's All machines, the default with more than one machine, each card's actions going to its own machine (`pwa/test/machines.spec.mjs`, `pwa/test/unit/host-session.test.mjs`), or shows one machine at a time when one is picked. Live (2026-10-29, #49): the VM's app listed a second host through the relay, and replied (with an image), answered and ended sessions on it | #49 |
| Machine row: sessions, version, dev marker | All | Status card | `Verified` | `UsageFlowTests.Hello_offers_usage_and_says_how_many_sessions_are_live_and_that_the_build_is_dev`, `pwa/test/usage.spec.mjs`. Live: Progress › Usage ("N sessions · version (dev)") | |
| Offline machines listed as offline; Forget an offline machine with two presses | All | Status card; `tests/test-multi-machine.ps1` | `Implemented, untested live` | The relay's machine roster ([roster.md](protocol/roster.md)): presence lists the account's offline hosts with when each was last seen, and a person's device (never a host) forgets one that is not connected. The PWA and the app list them after the online machines with a two-press Forget: `RosterTests`, `FleetTests.Forget_takes_two_presses_and_asks_the_relay_for_that_machine_alone`, `RelayFleetLinkTests.The_app_lists_a_machine_that_has_gone_offline_and_as_a_persons_device_can_forget_it`, `pwa/test/forget.spec.mjs`, `pwa/test/unit/forget.test.mjs`. Needs the production relay deployed with it ([relay-deploy.md](relay-deploy.md#machine-roster)). On the old dashboard, the X forgets a machine on the HA connector as it forgets one on the daemon: `UpkeepTests.Everything_this_connector_keeps_in_Home_Assistant_is_on_the_list_the_dashboards_forget_clears` (stage 7) | #50 |
| Update available, install from another device, progress, outcome, fleet-wide check | All | `install_update` and `check_updates` buttons; `bridge-update.ps1` | `Implemented, untested live` | The Home Assistant connector serves both buttons on a host its updater installed (docs/ha-connector.md, stage 6): `UpdateTests` (the daemon's publications at six moments, the busy notice, a check, an install through download and hand-over, the outcome after the restart); `LiveHomeAssistantTests.The_update_entity_shows_the_release_found_and_Install_downloads_it_and_hands_it_to_the_updater` against a disposable Home Assistant with a stand-in updater. The app's own Settings update too. From the PWA and the app on another machine (#51, [v1-update.md](protocol/v1-update.md)): `update.*` through the relay, the machine's app installing by its automatic-update rules, only from a person's device, none on a read-only machine, refused while another profile runs from the installation, and the outcome reported after the restart; Check all is the fleet-wide check: `UpdateFlowTests`, `LocalApiTests.Only_the_app_reports_this_machines_updates_and_the_pipe_asks_for_none`, `AutoUpdaterTests` (`Install_asked_for_…`, `The_hosts_asks_reach_the_updater_…`), `MachineUpdateTests`, `pwa/test/update.spec.mjs`, `pwa/test/unit/update.test.mjs`. Live (2026-11-01 11:06 PT): Install pressed in the PWA updated DSWETT-ULTRA, a migrated machine, from 0.1.690 to 0.1.700; the PWA showed progress and then the installed outcome, and HA's update entity followed | Still to run live: Install from the HA card's button on a migrated machine (#161) |
| The machine updates itself (app and host as one version) | All | `agent-ha-bridge update`; `tests/test-update*.ps1` | `Implemented, untested live` | `SettingsTests.An_update_stops_the_host_before_the_app_restarts_into_the_new_version`; `docs/app-windows.md` › Updates | S3 spike verified Velopack deltas. The product's update path not yet run live (#39) |

## 10. Notifications

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| A question reaches the phone; a tap opens it | All | `daemon-decision-notify.ps1`; `notify.mobile_app_*` | `Verified` | `PushFlowTests.A_question_reaches_the_phone_encrypted_through_the_relay_and_is_replaced_once_answered_at_the_machine`. Live: Progress › Push | |
| A finished turn notifies with the response's start | All | `Send-BridgeResponseNotification` | `Verified` | `PushNotifierTests.A_finished_turn_is_notified_with_the_start_of_its_response_after_it_stays_idle`. Live: Progress › Push ("finished") | |
| Answered anywhere withdraws the notice; a stale tap is refused | All | README "Answering from the notification" | `Verified` | `PushNotifierTests.An_answered_question_is_replaced_quietly_where_it_was_shown_but_not_on_the_device_that_answered`. Live: Progress › Push (cleared) | |
| One notification per session (tags); several sessions at once | All | Per-session tags | `Verified` | `PushNoticeTests.Each_session_has_one_topic_a_push_service_accepts_and_one_tag`, `pwa/test/push.spec.mjs`. Live: Progress › Push | |
| Option buttons and "Type an answer" on the phone notification | All | `answerButtons.*` | `Intentionally different` | Web Push on iOS has no action buttons or text inputs; a tap opens the card | Design §8 (S5). The old buttons return for migrated machines through the HA connector's stage 5b ([ha-connector.md](ha-connector.md#stage-5b-phone-notifications); `NotifyTests`, live in the `ha-integration` job) |
| Desktop notifications with option buttons and a text box | All | — (HA only) | `Verified` | `ToastTests` (11). Live: Progress › Windows app (answered from a toast button; withdrawn once answered) | Beyond parity |
| Notification styling (icon, colour, interruption level) | All | `answerButtons.icon`, `.color`, `.interruptionLevel` | `N/A` | HA companion-app specific | Carried for the HA connector's answer buttons (`homeAssistant.answerButtons.*`) |

## 11. Transfer and cross-machine resume

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Copy a session to another machine and resume it there (fork under a new id; up to 25 MB) | All | `Send-BridgeSessionBundle`; `docs/cross-machine-resume.md`; `tests/test-session-fork.ps1`, `test-session-transfer.ps1` | `Implemented, untested live` | Through the blob store, end-to-end encrypted ([host-transfer.md](host-transfer.md), [v1-transfer.md](protocol/v1-transfer.md)); 25 MiB by default, up to 200 MiB: `LaunchFlowTests.A_session_that_ended_on_another_machine_is_brought_here_under_an_id_of_its_own_and_resumed_in_the_workspace_chosen`, `SessionBundleTests` (Copilot, Claude, Codex), `pwa/test/launch.spec.mjs: "bringing a session from another machine"`; from the Windows app, `RemoteLaunchTests.A_session_from_a_third_machine_is_brought_to_another_with_a_command_signed_for_the_machine_it_is_on`, `RelayFleetLinkTests.The_account_s_machines_that_hand_sessions_on_are_listed_with_their_keys_and_a_command_signed_for_one_is_theirs_alone` | #39. The session opens in a workspace picked on the receiving machine, not the path it had (invariant 7). Codex's version is not compared |
| Resume choices shared across machines | All | `tests/test-resume-sharing.ps1` | `Implemented, untested live` | The phone lists, under Resume, the sessions each of the account's other machines offers: `pwa/test/unit/transfer.test.mjs: "the other machines' sessions come from those that hand them on…"`, `pwa/test/launch.spec.mjs` | #39. Read live from each machine, not published |
| Transfers signed with the fleet secret | All | `Get-BridgeTransferSignature` | `Intentionally different` | A command the person's device signs for the machine the session is on, once, for minutes, naming the machine that receives it; host to host over an end-to-end channel: `SignedCommandTests`, `LaunchFlowTests.A_command_is_carried_out_once_for_the_machines_it_names_and_by_a_machine_that_hands_its_sessions_on` | Design §9 ("Fleet secret retired") |

## 12. Hooks and intake

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| A native hook spools events while the daemon's heartbeat is fresh and returns in tens of ms | All | `hook/main.go`; `tests/test-hook-spool.ps1`, `test-native-hook.ps1` | `Implemented, untested live` | `agent-bridge-host-hook` (NativeAOT, the same spool contract) and `HookIntake` on a migrated machine (`docs/host-hooks.md`): `HookRunnerTests`, `HookProcessTests` (CI runs the published build), `IntakeTests.Every_complete_event_is_handled_in_the_order_it_was_written_and_then_removed`, `A_file_a_hook_renames_into_place_is_noticed_at_once_and_handled` | #39: hook timings on the VM after migrating (#38) |
| PowerShell fallback while the daemon is down; the fixed safe reply on any error | All | `runPowerShell`; `tests/test-hook-functions.ps1`, `test-daemon-replies.ps1` | `Intentionally different` | With the host down, the host's hook prints the fixed reply and runs nothing else (no PowerShell, no Home Assistant): `HookRunnerTests.With_no_host_taking_events_the_fixed_reply_is_printed_and_nothing_is_spooled`, `An_unexpected_failure_still_prints_the_fixed_reply_and_exits_zero`, `HookProcessTests.With_the_host_down_the_real_hook_still_gives_each_agent_its_fixed_reply_and_exits_zero` | `docs/host-hooks.md` › Decision. #39: stop the host on the VM |
| Copilot `ask_user` hook returns at once; the native prompt stays live | Cp | `route-ask-user-v3.ps1` | `Implemented, untested live` | Questions come from the transcript, and the terminal stays answerable (first answer wins). The host's hook allows `ask_user` at once on every path: `HookRunnerTests.Copilot_ask_user_gets_its_allow_at_once_even_with_more_input_than_any_event` | #39 |
| Claude and Codex registrations and the Codex approval marker | Cl, Cx | `Write-ClaudeSessionRegistration`, `Write-CodexApprovalMarker` | `Implemented, untested live` | Written by the intake on a migrated machine, in agent-ha-bridge's shapes, with no Home Assistant; the marker for every approval: `IntakeTests` (Claude, Codex, pruning) | #39: answer a Codex approval from the phone after migrating |
| Hook health in `status` (heartbeat, path taken, timings, log rotation) | All | `tests/test-hook-status.ps1`, `test-hook-stats.ps1` | `Implemented, untested live` | `agent-bridge-host hooks status`: `IntakeTests.Hook_health_counts_each_path_and_folds_the_numbers_out_of_the_reasons`, `HookRunnerTests.The_log_keeps_one_previous_megabyte` | Not on the app's Settings page yet |
| Register hooks and owned instruction blocks for each agent; uninstall removes only those | All | `install.ps1`, `claude/install-claude.ps1`, `codex/install-codex.ps1`; `Set-BridgeManagedInstructionBlock` | `Missing` | The app writes none (`docs/app-windows.md`, G4) | #54 |

## 13. Discovery and transcript reading

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Copilot sessions from live locks, resumed sessions from the command line, pid reuse guarded | Cp | `hooks/daemon-discovery.ps1` | `Verified` | `DiscoveryTests` (15). Live: the host reads real sessions on DSWETT-DEV-VM1 and the VM (Progress › VM test method) | |
| Byte-level LF framing; split characters wait; shrink or replacement rereads | All | README "How it works"; `tests/test-transcript-framing.ps1` | `Verified` | `TailTests.A_split_multibyte_character_at_the_end_is_not_decoded_until_its_line_is_complete`, `A_transcript_that_shrank_is_read_again_from_the_start`. Live: as above | |
| Huge transcripts: the tail parsed, the rest skimmed; bounded reads | All | `MaxTailBytes`; `tests/test-transcript-cache.ps1` | `Verified` | `TailTests.A_huge_Copilot_transcript_takes_its_title_turns_and_compactions_from_the_skim_and_its_state_from_the_tail`. Live: as above | |
| Incomplete discovery holds removal, cleanup and launch decisions; unaccounted processes get a 2-minute grace | All | `Select-DaemonHoldingProcesses`; `tests/test-daemon-discovery.ps1` | `Implemented, untested live` | `DiscoveryHoldTests` (17), `LaunchFlowTests.While_Copilot_discovery_is_held_…`, `A_new_Codex_session_is_not_launched_while_Codex_discovery_is_held_…`. Shown as "discovery held: …": `MainTests.What_holds_the_hosts_discovery_is_shown_until_it_clears`, `pwa/test/usage.spec.mjs` | Intentionally narrower: only launches that read an absence (resumes, new Codex) are held, not every launch (`docs/protocol/v1-launch.md`). Live check: #43 |
| Never writes, locks, renames or deletes the agents' or agent-ha-bridge's files | All | — | `Verified` | `SourceTests.A_full_scan_and_watch_cycle_never_writes_under_the_agent_and_bridge_roots`. Live: the host runs beside agent-ha-bridge on DSWETT-DEV-VM1 | |

## 14. Install, update, uninstall, configuration

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| One-step per-user install, no admin; starts at logon | All | `bootstrap.ps1`, `install.ps1`; scheduled task and VBS launcher | `Verified` | `HostManagerTests.Starting_at_sign_in_registers_an_unelevated_interactive_logon_task_that_runs_the_launcher`. Live: Progress › Windows app (`Setup.exe`, logon task, Run entry) | |
| Restarted after a crash; one instance per profile | All | `agent-bridge-supervisor.ps1`, mutexes | `Implemented, untested live` | `LocalPipeTests.A_second_server_for_the_same_profile_is_refused`; the logon task's restart rule (`docs/app-windows.md`) | #39 |
| Own names, folders, task and pipe; never agent-ha-bridge's (G3) | All | `installation.json` isolation | `Verified` | `HostManagerTests.The_task_is_named_for_this_product_profile_and_user_and_never_like_agent_ha_bridges`, `IdentityTests.The_default_identity_folder_is_agent_bridges_own_and_never_agent_ha_bridges`. Live: Progress › Windows app | |
| Uninstall removes the task, Run entry and toast registration, and keeps the profile | All | `uninstall.ps1` (`-KeepConfig`) | `Implemented, untested live` | `Program.cs` `Lifecycle.BeforeUninstall`; `docs/app-windows.md` › Updates | Verified in spike S3. Not yet run on the product (#39) |
| `status`, `logs`, `restart` | All | `bin/agent-ha-bridge.ps1` | `Intentionally different` | The app's Settings page (state, start/stop/restart, log tail): `HostManagerTests.The_log_tail_is_the_end_of_the_hosts_file_even_when_the_host_is_not_running` | Design §7.3 |
| `configure`: approved workspaces and launch settings | All | `install.ps1 -Workspace`, `configure` | `Implemented, untested live` | `agent-bridge-host workspace` and `config`: `HostSettingsTests.The_cli_refuses_a_setting_it_does_not_know_or_a_value_serve_could_not_read_and_leaves_the_file_as_it_was` | `workspace add` was used live (Progress › Launch). Other keys need #39 |
| A config that cannot be read turns the risky features off | All | `bridge-control-policy.ps1` (unreadable is unavailable) | `Implemented, untested live` | `HostSettingsTests.A_setting_that_cannot_be_read_as_true_or_false_turns_remote_approval_off_and_says_why`, `A_config_from_a_newer_schema_is_not_guessed_at` | #39 |
| Detect agents and install missing ones (winget, npm) | All | `install.ps1`; `tests/test-install-deps.ps1` | `Missing` | Deferred: "first-run wizard's agent detection and winget installs" (`docs/app-windows.md`) | #54 |
| Migrate an existing agent-ha-bridge install, and roll back | All | — (design G8) | `Implemented, untested live` | `agent-bridge-host migrate` / `rollback` (`docs/migration.md`). `MigrationTests`: against a fake installation laid out as its installers lay one out, `Rolling_back_restores_every_file_byte_for_byte_and_starts_agent_ha_bridge_again`, `A_failed_hand_over_rolls_back_on_its_own`, `The_dashboards_publication_writer_is_refused_before_anything_is_touched` | The VM rehearsal (#39). The app's Settings page offers it in WinUI slice 2 |
| Layout migration from older agent-ha-bridge layouts | All | `tests/test-layout-migration.ps1` | `N/A` | Older agent-ha-bridge layouts are its own concern; migration starts from 1.33.x (#38) | |

## 15. Agents driving sessions

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| MCP tools: list, read, reply, launch (with settings), launch result, launch options, ask | All | `mcp/src/server.js`, `sessions.js`; `mcp/test/*.js` | `Missing` | No MCP server | #45. The old server keeps working through the in-flight HA connector |
| Launched sessions drive as an agent principal, not as the person | All | `Get-BridgeAgentTokenEnvironment`; `tests/test-launch-permissions.ps1` | `Partial` | Inside an agent session, the pipe refuses answering, replying, ending and launching rather than carry them as the person: `LocalApiTests.A_process_inside_a_live_agent_session_cannot_stop_restart_reconfigure_or_sign_in_the_host_but_the_app_can`. Launched agents carry `AGENT_BRIDGE_SESSION`: `LaunchPieceTests.A_started_agent_gets_the_session_variable_on_top_of_the_hosts_own_environment` | No agent principal yet (#45) |
| A session cannot restart or stop the bridge | All | §6.3 invariant 13 | `Implemented, untested live` | `LocalApiTests.A_process_inside_a_live_agent_session_…`, `A_process_that_is_itself_an_agent_CLI_is_refused_…`, `Only_the_app_beside_the_host_manages_it_…`; `CallerGuardTests`; CLI: `CallerGuardTests.The_hosts_command_line_knows_it_runs_inside_a_session_…` | A safety rail, not a boundary (`docs/protocol/local-pipe.md`). Live check: #44 |

## 16. Platforms and Dev Box

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Elevated targets refused with an explicit message | All | `decision-inject.ps1`; README troubleshooting | `Verified` | `AgentAnswerFlowTests.An_elevated_Claude_or_Codex_session_is_refused_untyped`, `StopFlowTests.What_the_host_must_not_end_is_refused_on_the_first_press_and_nothing_is_typed_or_terminated`. Live: Progress › VM test method (processes Hyperloop starts elevated are refused) | |
| Windowless child processes (no focus-stealing windows) | All | `Start-BridgeWindowlessProcess` | `Implemented, untested live` | `CreateNoWindow` in `CommandRunner.cs`, `BatchFileRunner.cs`, `Worktrees.cs` (git) and `WorkerInjector.cs`; `agent-bridge-hostw` starts the host with no console | #39 |
| macOS and Linux: answer, reply, end and launch through tmux; Terminal or iTerm window | All | `Start-BridgeTmuxSession`; `tests/test-terminal-window.ps1`, `test-platform.ps1` | `Missing` | Read-only off Windows ("not supported on this platform yet") | #48 |
| Dev Box keep-awake | All | `Invoke-BridgeDevBoxKeepAwake`; `tests/test-devbox-keepawake.ps1` | `Missing` | — | #47 |

## 17. Security, identity and pairing

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Only the user's own devices can see and drive sessions | All | HA credentials | `Verified` | `RelayTests.Another_accounts_device_is_neither_listed_nor_reachable`, `ChannelTests.A_request_crosses_the_relay_end_to_end_and_the_relay_sees_and_logs_only_ciphertext`. Live: Progress › Production relay | End to end; the relay is blind |
| A new machine joins only after a matching code is approved on an existing one | All | `docs/fleet-pairing.md`; `tests/test-pairing*.ps1`, `test-secret-owner-rights.ps1` | `Missing` | Any device signed in to the GitHub account is trusted | #55 (design §5.4) |
| Control requests validated: one request id, content digest, unreadable config is unavailable | All | `bridge-control-policy.ps1`; `tests/test-control-policy.ps1` | `Intentionally different` | Typed requests with UUIDs, checked per op (`AnswerTests`, `ReplyTests`, `LaunchTests`, `StopTests`) over an authenticated channel | Design §2 (control-authorization becomes §5.6) |
| One target per action | All | `Assert-HomeAssistantServiceTarget`; `tests/test-service-target.ps1` | `Verified` | Every command names one session `id` (`StopTests.A_stop_names_a_session_and_any_confirmation_is_a_token_the_host_could_have_issued`). Live: every Progress run | HA "count exactly one" is in flight (HA connector) |
| Hostile names cannot inject into the UI | All | `tests/test-security.ps1` | `Implemented, untested live` | `pwa/test/ui.spec.mjs: "raw HTML and script links from a host are shown as text and never run"`, `ToastTests.Whatever_a_session_says_is_escaped_into_the_toast` | #39 |
| Secrets never logged; credentials read, used once, dropped | All | `tests/test-security.ps1`; README "Agent usage" | `Implemented, untested live` | `UsageTests.Claude_never_writes_to_its_credentials`, `RelayDeploymentTests.Relay_logs_name_no_account_login_device_or_device_name` | #39 |

## 18. Home Assistant plumbing

On a migrated machine, the **HA connector** ([ha-connector.md](ha-connector.md), stages 1–7)
keeps these entities and inputs wire-compatible, so the existing dashboard and phone flow keep
working (G9). The new product itself replaces them.

| Feature / behaviour | Agents | agent-ha-bridge | Status | Evidence in agent-bridge | Gap / validation |
|---|---|---|---|---|---|
| Per-machine and per-session MQTT entities (status, activity, decision selects, reply text, buttons) | All | `decision-mqtt.ps1`, `bridge-adapter.ps1`; `tests/test-bridge-adapter.ps1`, `test-status-card.ps1`, `test-reply-card.ps1`, `test-choices-form.ps1` | `Implemented, untested live` | Recorded from the daemon's own functions (`tools/ha-contract`): `ContractTests`, `DecisionContractTests`, `LifecycleTests`, `AnswerTests`, `ReplyTests`, `StopTests`, `UpkeepTests` (a live session's entities put back, its reply box and question after a Home Assistant restart). Live on a disposable Home Assistant in CI: `LiveHomeAssistantTests`, including a Home Assistant restart with a question waiting | #39: E18-01 to E18-04 against the generated dashboard |
| Actionable phone notifications through `notify.mobile_app_*`, and taps through `mobile_app_notification_action` | All | `daemon-decision-notify.ps1`; `tests/test-answer-buttons.ps1` | `Implemented, untested live` | `NotifyTests` (stage 5b). Live on a disposable Home Assistant in CI: `LiveHomeAssistantTests``.A_question_goes_to_the_phones_with_its_buttons_a_tap_answers_it_and_the_hooks_pushes_follow` | #39: E18-04 on a phone |
| Launch request payload topic and result sensor (`promptpayload`, `new_session_result`) | All | `daemon-launch.ps1` | `Implemented, untested live` | `LaunchTests` (stage 5). Live on a disposable Home Assistant in CI: `LiveHomeAssistantTests``.Launch_on_the_card_and_an_agents_request_both_reach_the_hosts_launcher_and_the_request_hears_how_it_went` | #39: E18-04 |
| Per-machine `online` heartbeat and `sessions` sensor | All | `agent-bridge-daemon.ps1` | `Implemented, untested live` | `LifecycleTests.Liveness_is_declared_before_its_first_beat_which_is_never_retained_and_every_entity_gets_its_contract_id`; the sensor's repair and the Detailed activity switch, `UpkeepTests` (stage 7). Live on a disposable Home Assistant in CI: `LiveHomeAssistantTests` | #39: E18-01, E18-03, E18-05 |
| Agent attribution through `context.user_id` and `agentUserIds` | All | `Test-BridgeAgentUserId` | `Implemented, untested live` | `driver` from the pressing account in `AnswerTests`, `ReplyTests`, `LaunchTests`, `NotifyTests`; an agent may not press Install (`UpdateTests`). Live on a disposable Home Assistant in CI: `LiveHomeAssistantTests``.A_press_on_the_card_reaches_the_hosts_answer_and_says_who_pressed_it` | #39: E18-04 |
| Generated Lovelace dashboard, the 7 custom cards, card-version gating | All | `Save-CopilotSessionDashboard`, `bridge-frontend-cards.ps1`; `tests/test-dashboard.ps1`, `test-install-cards.ps1`, `frontend/test/test-cards.js` | `N/A` | Native app and PWA render from protocol state | G7: the new stack never publishes it |
| Publication authority, fences, rollback pins, render fingerprints | All | `tests/test-publication-policy.ps1`, `test-write-boundary-*.ps1` | `N/A` | No shared renderer | Design §2 |
| Entity-registry pinning, legacy and unscoped entity sweeps | All | `tests/test-registration-isolation.ps1`, `test-multi-machine.ps1` | `N/A` | | |
| 255-character text, optimistic selects, position-encoded multi-select, 10-option cap | All | `tests/test-choices-form.ps1`, `test-decision-select-race.ps1` | `N/A` | | Design §1 parity contract |
| HA image store for pasted images | All | `/api/image/upload`, `image/delete` | `N/A` | In-channel attachments, and the relay's blob store for larger ones | |
| HA REST and WebSocket client (state snapshot, filtered reads, retry, auth backoff, HTTP session reuse) | All | `tests/test-state-read.ps1`, `test-state-snapshot.ps1`, `test-reliability.ps1`, `test-auth-backoff.ps1`, `test-http-session.ps1` | `N/A` | Relay reconnect and backoff: `ChannelTests`, `RelayTests.A_relay_that_is_stopping_tells_connected_devices_to_reconnect_at_once_with_1012` | |
| Pairing through HA helper state (`input_text.agent_bridge_pairing`) | All | `bridge-pairing-io.ps1` | `N/A` | Replaced by device approval (#55) | |
| Detached-entity repair, global reassert, peer stand-in sessions | All | `agent-bridge-daemon.ps1` (`DetachedEntity*`, `GlobalReassertSeconds`, `PeerSessionsStandInSeconds`) | `N/A` | Snapshot plus ordered events with gap detection: `ConnectionTests.A_gap_in_the_events_after_a_reconnect_reads_the_sessions_status_and_usage_again`. For the old dashboard, the HA connector ports the repair and the reassert (`UpkeepTests`, stage 7); peer stand-ins are the writer's | |
| The test runner's HTTP guard and offline isolation | — | `tests/test-http-guard.ps1`, `test-runner.ps1`, `test-install-http-guard.ps1` | `N/A` | xUnit with fakes; no test reaches a real agent, HA or relay | Design §6.5 |

## Test suites

All 87 agent-ha-bridge suites, grouped by feature, with where the behaviour stands here.
"[HA]" marks suites that are wholly or mostly Home Assistant plumbing.

| Group | Suites | Where it stands in agent-bridge |
|---|---|---|
| Copilot activity and transcripts | `test-copilot-activity`, `test-transcript-framing`, `test-transcript-cache`, `test-turn-end`, `test-detailed-activity` | `CopilotTests`, `TailTests`, `BackgroundTests`, `SourceTests`: Verified (§1, §13) |
| Claude transcripts and questions | `claude/test-claude-transcript`, `claude/test-claude-ask-parser`, `claude/test-claude-integration` [HA] | `AgentTests`, `ClaudeAndCodexPlannerTests`: In flight (Claude/Codex live) |
| Codex | `codex/test-codex`, `codex/test-codex-integration` [HA] | `AgentTests`, `AgentAnswerFlowTests`: In flight (Claude/Codex live). Install and cleanup half: #54 |
| Discovery and sessions | `test-daemon-discovery`, `test-daemon-sessions`, `test-daemon-agents`, `test-daemon-loop`, `test-resume-discovery`, `test-registration-isolation` [HA] | `DiscoveryTests`, `SourceTests`, `DiscoveryHoldTests` |
| Decisions and answers | `test-daemon-decisions`, `test-decision-args`, `test-decision-lifecycle`, `test-decision-retry` [HA], `test-decision-select-race` [HA], `test-answer-match`, `test-p3b-contracts`, `test-choices-form` [HA] | `CopilotPlannerTests`, `TranscriptAndVerifierTests`, `AnswerFlowTests`, `AnswerTests`: Verified for Copilot (§2). Cancel request: #56 |
| Notifications | `test-answer-buttons` [HA] | `PushNotifierTests`, `PushFlowTests`, `ToastTests`: Verified (§10) |
| Replies and attachments | `test-daemon-replies`, `test-reply-card` [HA], `test-p5-attachment-privacy` | `ReplyFlowTests`, `CopilotReplyTests`, `AttachmentStoreTests`, `AgentReplyFlowTests`: Verified for Copilot, Claude and Codex |
| End session | `test-stop-session`, `test-write-boundary-stop` [HA] | `StopFlowTests`, `StopTests`: Verified (§5) |
| Launch, resume, worktrees | `test-new-session`, `test-launchers`, `test-launch-permissions`, `test-agent-launched`, `test-worktree-isolation`, `test-p4-worktree-safety`, `test-resume-sharing` | `LaunchFlowTests`, `LaunchPieceTests`: Verified or untested live (§6, §7). Reclaimer and sharing in flight. Agent principal: #44, #45 |
| Transfer and fork | `test-session-transfer`, `test-session-fork` | `SessionBundleTests`, `SignedCommandTests`, `LaunchFlowTests` (transfer): Implemented, untested live (§11) |
| Usage | `test-usage` | `UsageTests`, `UsageFlowTests`: §8. Settings: #52 |
| Machines and fleet | `test-multi-machine` [HA], `test-status-card` [HA], `test-driver-card` | §9. Offline and Forget #50; purple edge #46 |
| Dashboard and cards | `test-dashboard` [HA], `test-install-cards` [HA], `test-bridge-adapter` [HA], `frontend/test-cards.js` | N/A (§18); the card behaviours are rows in §1–§9 |
| Publication and write boundary | `test-publication-policy`, `test-write-boundary-callers`, `test-write-boundary-context` [HA] | N/A |
| Hooks | `test-hook-spool`, `test-hook-functions`, `test-hook-status`, `test-hook-stats`, `test-native-hook`, `test-native-hook-install`, `hook/main_test.go` | `HookRunnerTests`, `HookProcessTests`, `IntakeTests` (§12). Installing the hook for a fresh install: #54 |
| Install, update, uninstall | `test-install-clients`, `test-install-command`, `test-install-connection` [HA], `test-install-deps`, `test-install-workspaces`, `test-install-http-guard` [HA], `test-layout-migration`, `test-bootstrap-download`, `test-bootstrap.sh`, `test-update`, `test-update-cli`, `test-update-intake`, `test-update-outcome`, `test-vbs-launchers`, `claude/test-claude-install` | `HostManagerTests`, `SettingsTests`, `HostSettingsTests` (§14). Hooks: #54. Migration: #38. Remote update: §9, `update.*` (#51) |
| Pairing and secrets | `test-pairing`, `test-pairing-flow` [HA], `test-secret-owner-rights` | #55 |
| Control and security | `test-control-policy`, `test-security`, `test-service-target` | §17 |
| Restart and reliability | `test-restart-restore`, `test-reliability` [HA], `test-state-read` [HA], `test-state-snapshot` [HA], `test-auth-backoff` [HA], `test-http-session` [HA] | `ConnectionTests`, `PushNotifierTests.A_question_already_pending_when_the_host_starts_is_notified_as_after_a_daemon_restart`, the answer ledger (§2) |
| Platforms | `test-platform`, `test-terminal-window` | #48 |
| Dev Box | `test-devbox-keepawake` | #47 |
| Detail toggle | `test-verbose-toggle` [HA] | Intentionally different (§1) |
| Test infrastructure | `test-runner`, `test-http-guard` | N/A |
| MCP server | `mcp/test-config`, `test-ha` [HA], `test-http`, `test-http-e2e`, `test-sessions` | #45 |

agent-ha-bridge's manual checks, `tests/verify-mac-terminal.ps1`,
`verify-permissions-live.ps1` and `claude/tests/verify-askuserquestion.ps1`, map to #48,
#39 and #42.

## The §6.3 invariants

| # | Invariant | Status | Evidence | Gap |
|---|---|---|---|---|
| 1 | The transcript is the source of truth for "answered" | `Verified` | `CopilotTests.A_pending_ask_user_makes_it_waiting_until_the_human_response_is_recorded`, `AnswerFlowTests.An_answer_the_transcript_then_records_is_delivered_after_typing_exactly_the_planned_keys`, `ClaudeAndCodexPlannerTests.Claude_is_confirmed_by_the_answers_it_recorded_for_the_call_and_by_its_text_without_them`. Live: Progress › Answers | |
| 2 | Answers are read by identity, never by clock | `Verified` | `AnswerFlowTests.An_answer_to_a_question_no_longer_asked_is_stale_and_types_nothing`, `TranscriptAndVerifierTests.An_answer_to_that_call_is_found_by_id_and_answers_to_other_calls_are_not`, `ReplyFlowTests.Two_replies_of_the_same_words_need_two_prompts_one_each`; `docs/host-answers.md` step 5. Live: Progress › Answers, Replies | |
| 3 | The `ask_user` hook never blocks; the native prompt stays live | `Partial` | The host needs no hook and leaves the prompt live: `AgentAnswerFlowTests.A_Claude_question_answered_at_the_machine_a_moment_ago_is_stale_and_nothing_is_typed`. Live: Progress › Answers | Until a machine migrates the hook in use is agent-ha-bridge's; the host's own allows at once on every path (`HookRunnerTests.Copilot_ask_user_gets_its_allow_at_once_even_with_more_input_than_any_event`) |
| 4 | Copilot Other: Up×(N−1), Down×N, words, one Enter; typed words checked against the record | `Verified` | `CopilotPlannerTests.Words_through_Other_walk_up_to_the_top_then_down_past_the_last_option_and_submit_separately`, `TranscriptAndVerifierTests.Words_through_Other_match_only_when_the_words_were_recorded_and_not_an_option`. Live: Progress › Answers (Other with smart quotes) | |
| 5 | Control characters collapsed before injection; elevated targets refused explicitly | `Verified` | `CopilotPlannerTests.Line_breaks_and_control_characters_in_typed_words_become_spaces_so_they_cannot_submit_early`, `AgentAnswerFlowTests.An_elevated_Claude_or_Codex_session_is_refused_untyped`. Live: Progress › Replies (two lines delivered as one), VM test method (elevated refused) | |
| 6 | Incomplete discovery holds retirement, cleanup and launch decisions | `Implemented, untested live` | `DiscoveryHoldTests`, `LaunchFlowTests.While_Copilot_discovery_is_held_none_of_its_sessions_is_offered_for_resume_and_resuming_one_is_busy`, `WorktreeReclaimerTests.Nothing_is_reclaimed_while_session_discovery_is_held_and_finished_trees_go_once_it_is_complete` | #43's live check |
| 7 | Launched sessions get worktree isolation and fail closed; the cap has no fallback | `Implemented, untested live` | `LaunchPieceTests.The_cap_counts_only_this_hosts_worktrees_and_refuses_rather_than_falling_back`, `A_folder_outside_git_fails_closed`, `LaunchFlowTests.An_isolated_workspace_starts_each_session_in_a_worktree_of_its_own_and_unlocks_it_once_it_registers`. Live: Progress › Launch (isolated worktree) | Isolation is verified live; failing closed and the cap only offline (#39) |
| 8 | Reclaim only marked, unlocked, clean, detached, idle trees, in the given repository only | `Implemented, untested live` | `WorktreeReclaimerTests` (#58), held while discovery is held | #39 |
| 9 | Attachments in protected per-installation staging, never shared TEMP | `Verified` | `AttachmentStoreTests.Staged_files_go_in_a_folder_only_this_user_can_open_named_safely_with_the_extension_their_bytes_call_for`, `A_folder_someone_else_could_read_is_narrowed_before_anything_is_staged_in_it`. Live: Progress › Attachments | |
| 10 | Live sessions never offered for resume; remote resume only in approved workspaces | `Implemented, untested live` | `LaunchFlowTests.The_options_list_installed_agents_approved_workspaces_and_only_resumable_sessions_in_them`, `A_session_being_resumed_is_not_resumed_again_by_another_press_until_its_agent_has_gone`. Live: Progress › Launch (resume) | #43's live check |
| 11 | Folder trust is never answered by a press of Launch, Home Assistant, the MCP server or an agent; a launch that meets one returns `busy`. Only a person answers it: at the machine, or, where the machine allows it (`launch.remoteFolderTrust`, off by default), with a command their own PWA or app signs (`launch.trust.answer`) | `Verified` | `LaunchFlowTests.A_folder_trust_question_is_left_for_the_machine_and_other_launches_are_busy_meanwhile`, `A_Copilot_that_registers_and_then_asks_whether_to_trust_its_folder_is_left_for_the_machine_and_other_launches_are_busy`, `A_Codex_asking_whether_to_trust_its_folder_is_left_for_the_machine_and_other_launches_are_busy_meanwhile`, `LaunchPieceTests.Questions_an_agent_stops_on_before_it_starts_are_told_apart_by_what_it_draws`. Live on the test VM: a Copilot launch into a never-trusted folder answered `unconfirmed` (the question named), the next launch `busy`, nothing typed; Claude the same; and Codex 0.166 resumed by a transfer into a folder it had not trusted (before this fix it answered "had not appeared after 60 s" and later launches were not busy) | #74 |
| 12 | End session: one press when idle, two within 10 s otherwise, enforced by the host | `Verified` | `StopFlowTests` (19), `pwa/test/stop.spec.mjs`, `CardTests.End_session_on_a_working_session_takes_a_second_press_with_the_hosts_token_inside_the_window`. Live: Progress › End session | |
| 13 | No session launched by the bridge may restart the bridge | `Implemented, untested live` | `LocalApiTests.A_process_inside_a_live_agent_session_cannot_stop_restart_reconfigure_or_sign_in_the_host_but_the_app_can`, `CallerGuardTests` | #44's live check |
| 14 | Hooks never fail the agent: the fixed safe reply on any error | `Implemented, untested live` | `agent-bridge-host-hook`: `HookRunnerTests.An_unexpected_failure_still_prints_the_fixed_reply_and_exits_zero`, `HookProcessTests` against the NativeAOT build | #39 |
| 15 | One target per action; the HA connector keeps "count exactly one" | `Verified` | Commands name one session in a typed field (`StopTests`, `ReplyTests.A_reply_request_reads_from_and_writes_to_the_contract_json`). Live: every Progress run | The connector's rule is in flight (HA connector) |

## §6.4 trace replay and shadow mode

| Part | Status | Notes |
|---|---|---|
| Trace recorder in agent-ha-bridge (opt-in, off by default, G10) | `Intentionally different` | Not needed for the shadow diff, which reads the daemon's own state file, heartbeat and question markers, so agent-ha-bridge is unchanged ([host-shadow.md](host-shadow.md)). A recorder may still be wanted for replay traces. #40 |
| Replay harness (recorded traces become xUnit cases named for the PS scenarios) | `Missing` | #40 |
| Shadow mode (read-only beside the daemon, diffing state) | `Implemented, untested live` | `agent-bridge-host shadow run` and `summary` ([host-shadow.md](host-shadow.md)); `ShadowTests` (22), `ShadowWitnessTests` (6), `ShadowCoverageTests` (12). Compares sessions, status, background work, model, compactions, the response's end, questions and their fields, machine status (daemon up, host up, G5), and usage and the resume list (the daemon's read from Home Assistant: GET of four entities only, with its own token, owner-approved). Five daemon bugs it found are filed (agent-ha-bridge#258 to #262); a difference whose transcript evidence matches one's signature is tallied as known, with the issue, and is not a mismatch. Coverage is counted in events (sessions, questions, transitions, background work, compactions, model changes, resume snapshots, usage readings, restarts) and `shadow summary --gate` gives the verdict. Running on DEV-VM1 since 2026-10-29 (`build/shadow-week.ps1`). #40 |

**For the shadow comparison on DEV-VM1:**

1. `pwsh build/shadow-week.ps1 -Install`: the host serving a profile of its own and `agent-bridge-host shadow run`, each a per-user logon task, beside the daemon
   ([host-shadow.md](host-shadow.md)). It writes a daily report: sessions missing on either
   side, disagreements in status and the other compared fields that last longer than the
   grace (60 s), question identity and field mismatches, and timing skew. It never
   injects, launches, writes HA or consumes the spool.
2. #43 (discovery holds) lands first, so a transient read does not read as a session
   ending.
3. `shadow summary --gate` says `PASS` before the cutover: enough of each kind of event
   compared (100 questions with a 95% upper bound on their mismatch rate of at most 3%, 30
   sessions, 500 status transitions, 50 background starts and finishes, 5 compactions, 100
   resume snapshots, 50 usage readings, a restart seen and recovered), at least 24 hours
   since the first sample with 8 of them compared, and no unexplained mismatch in the last
   24 hours ([host-shadow.md › The gate](host-shadow.md#the-gate)). Not a fixed week.

## Migration readiness

### What migration and rollback do to each piece

| Piece | Migrate | Roll back |
|---|---|---|
| **Daemon** | Stop and **disable** (not delete) `AgentBridgeDaemon_<id>` and any `AgentBridgeDevBoxKeepAwake_<id>` | Re-enable both and start the daemon; G5 then puts the host back to read-only |
| **Hooks** | agent-ha-bridge's own entries in the Copilot, Claude and Codex hook files are re-pointed at `agent-bridge-host-hook` (`docs/host-hooks.md` has why it is not the Go hook), installed in `%LOCALAPPDATA%\AgentBridge\bin\<installation id>`; the host's intake keeps the heartbeat so hooks spool to it | Restore the files byte for byte from the snapshot taken at migrate time (or, for one edited since, only the re-pointed entries). The installed hook stays, for sessions started while migrated |
| **Instruction blocks** | Left in place (they name agent-ha-bridge's token, which still authenticates) and recorded by hash | Never written, so nothing to restore; a change made while migrated is reported |
| **Config** | `newSession.*`, and the HA connector's address, dashboard and attribution settings, imported into the profile's `config.json` (`docs/migration.md` has the key map). Keys with no equivalent are reported and stay in the old file; no secret is copied. `~/.agent-ha-bridge` is never modified but for the Codex plugin's `hooks.json` | The profile's `config.json` is restored (or removed) unless changed since; the old file was never written |
| **Approved workspaces and profiles** | `newSession.workspaces`, `defaultWorkspace`, `profiles`, `defaultProfile` go to `launch.*` with the same labels, so the phone's choices read the same | — |
| **Worktrees** | Old trees carry `agent-bridge-created` and are neither counted, claimed nor deleted by the host. While migrated nobody reclaims them (they are a fixed set); the reclaimer covers the host's own | The daemon's reclaimer resumes on its own trees. Leaving agent-ha-bridge for good: run its `Remove-BridgeFinishedWorktree` once first |
| **Attachments staging** | The old `install-<id>` staging is left as is, and its 24 h sweep pauses with the daemon; the host stages under its profile (`attachments`, protected, also 24 h) | The daemon's sweep resumes |
| **Publication role** | Refused on the configured writer (`dashboard.publication.writer`, G7) and on any machine whose policy cannot be read. The writer, DSWETT-HOME, migrates last, at P7 | — |
| **Home Assistant** | The in-flight HA connector publishes this machine's entities in agent-ha-bridge's contract (G9). It never touches the dashboard, Lovelace resources or policy | The connector stops; the daemon publishes again |
| **Dev Box keep-awake** | Needs #47 on a Dev Box; not relevant to the VM or DEV-VM1 | Task re-enabled |

### (a) Before the migrate-and-rollback rehearsal on the disposable Hyperloop VM

- [ ] #37: the host takes the spool and owns the fallback, writing the Claude and Codex
  registrations and the Codex approval marker. Without it a migrated machine runs every
  hook through PowerShell, and that PowerShell still writes HA.
- [ ] #38: Migrate and Roll back exist, with the byte-for-byte snapshot and the refusal on
  the publication writer.
- [x] #39: the scripted parity checklist exists: [parity-checklist.md](parity-checklist.md),
  with `tools/AgentBridge.Rehearsal` for the automated checks. It runs on the new host after
  migrating and on agent-ha-bridge after rolling back.
- [ ] A second disposable machine for the publication writer. agent-ha-bridge names its
  per-machine entities after the computer (`Get-BridgeMachineSlug`), so a writer and a
  participant cannot share one VM ([parity-checklist.md › What runs where](parity-checklist.md#what-runs-where)).
- [ ] HA connector (in flight) at least publishes status, pointed at a **disposable** HA
  and Mosquitto, never the live one (G6).
- [ ] Claude/Codex live (in flight): Claude and Codex answers verified on the VM, so the
  rehearsal exercises all three agents.
- [ ] A VM snapshot taken before agent-ha-bridge is installed, and another after, so the
  rehearsal can repeat from a known state.

### (b) Before cutting over DSWETT-DEV-VM1

- [ ] Everything in (a), with the rehearsal passed **in both directions** and repeated
  from a restored snapshot.
- [ ] #40: `agent-bridge-host shadow summary --gate --profile shadow` reads `PASS` on DEV-VM1.
- [ ] #43: incomplete discovery holds removal, resume offers and reclaim (invariants 6
  and 10).
- [ ] #44: a session cannot stop or restart its host through the pipe (invariant 13).
- [ ] #41: replies to Claude and Codex, if either is used on DEV-VM1.
- [ ] Reclaimer (in flight) merged, after #43, or the cap raised and watched, so isolated
  launches are not refused after ten.
- [x] Blob store merged (#61), so a phone photo over 256 KiB reaches a session whole.
- [ ] HA connector (in flight) at commands as well as status, so the old dashboard and
  MCP server can still drive DEV-VM1's sessions.
- [ ] Every row that DEV-VM1's daily use touches is `Verified` in this document after the
  rehearsal (#39).
- [ ] Rollback is rehearsed on DEV-VM1 itself once, right after the cutover, and then
  left available for the one-week soak.

## Priorities

**Blocks the VM rehearsal**

1. #37 Hook intake and fallback
2. #38 Migrate and roll back
3. #39 Scripted parity checklist
4. In flight: HA connector (status, against a disposable HA), Claude/Codex live

**Blocks the DEV-VM1 cutover** (after the rehearsal passes)

1. #43 Discovery holds (invariants 6, 10)
2. #44 Sessions cannot stop or restart their host (invariant 13)
3. #40 Shadow diff (built), then a clean week
4. #41 Claude and Codex replies (if used there)
5. In flight: reclaimer, HA connector commands

**Can follow**

- #45 MCP server re-targeted; #46 purple edge
- #49 remote machines and Launch in the Windows app; #50 offline machines and Forget; #51
  remote update
- #52 usage settings; #53 card wording; #56 Cancel request; #42 Claude free text
- #54 hook registration for fresh installs; #55 device approval
- #47 Dev Box keep-awake (before the Dev Box migrates); #48 tmux (before the Mac migrates,
  P5)
- #20 subagent labels (beyond parity)
