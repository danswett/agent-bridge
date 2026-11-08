# Forms soak rig

The forms soak in [`tools/lab/forms-soak`](../tools/lab/forms-soak/README.md) draws Copilot CLI
forms of each shape (choice, choice with text, choice with yes/no and text, number, long title,
tall message, "Other") from a stub MCP server, in consoles of 120x30, 120x50, 200x50 and 250x40,
and answers them with the host's own form reader, planner, screen check and injector. It needs
no model call for the form, so it can run unattended in a loop on a dedicated machine.

It is a piece of the agent CLI compatibility lab ([#1138](https://github.com/danswett/agent-bridge/issues/1138)):
it checks that every delivery ends delivered or refused before any key, and never half-answered
or wedged, for each CLI build available. It is screen-only: it tests the screen reader and the
injector, not `session.answer` or the host's transcript reading. See the README for the result
classes, how to run it as a scheduled task, how to stop it, and what it never touches.

The design for #1138 is `docs/design/host-answer-delivery.md` (PR #1135, not yet on `main`;
its canary and soak sections, §6 and §7, are where this rig belongs). Link this page from it
when that merges.
