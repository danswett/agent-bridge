# Slipgates beta guide

<!-- Source for slipgates.com/docs (#762). Items marked [confirm] wait on the owner or Platform
and are filled in before the first tester gets the link. -->

Thank you for trying Slipgates early. This page is the whole guide: what it is, how to install
it, what is rough, what we collect, and how to reach us.

## What Slipgates is

Slipgates lets you keep an eye on your AI coding sessions (Copilot CLI, Claude Code) from your
phone. When a session needs you, a notification on your PC and a card on your phone say so, and
you can answer its questions, approve or deny what it wants to do, or stop it from wherever you
are.

## Install on Windows

1. Open the newest pre-release (v0.1.3215 at the time of writing) at
   <https://github.com/danswett/agent-bridge/releases/tag/v0.1.3215>
   **[confirm: a stable download link replaces this when #735 lands]**.
2. Download the installer for your PC:

   | PC | Installer |
   |---|---|
   | Intel or AMD (most PCs) | `AgentBridgeApp-win-Setup.exe` |
   | Windows on Arm (Snapdragon and similar) | `AgentBridgeApp-win-arm64-Setup.exe` |

   Not sure? Open Settings > System > About and look at "System type". "x64-based processor"
   means the first file; "ARM-based processor" means the second. The installer files are named
   `AgentBridgeApp`, but the app itself is called Slipgates.
3. Run it. Windows shows a blue box, **Windows protected your PC**. This is expected: the beta
   installer is not signed yet, and Windows warns about any installer it does not recognize.
   Click **More info**, then **Run anyway**.
4. Slipgates opens. Under **Agents on this machine** it shows whether your coding tools are
   ready and offers **Set up** or **Repair** for the hooks. Slipgates doesn't install your coding
      CLI, sign you in to it or trust your project folder; do those yourself first. If **Set up**
      can't make an agent ready, tell us which step it stopped at. The app keeps itself up to date and offers **Restart to update**
   when there is a newer version.

## Connect your phone

1. On your PC, open Slipgates > **Settings** and, under **Your devices**, choose
   **Sign in with GitHub**. It shows a code and a GitHub link.
2. On your phone, open <https://danswett.github.io/agent-bridge/app/> and choose
   **Sign in with GitHub**. Open the GitHub link it shows, enter the code, and use the same
   GitHub account as your PC.
3. Return to the phone app. Your PC's sessions appear there.

## Known issues

- The installer is unsigned, so Windows warns as described above (#734).
- On Windows on Arm, use the Arm installer. The Intel/AMD one runs there but we haven't tested it.
- See the list of open bugs for the rest: <https://github.com/danswett/agent-bridge/issues>.

## What we collect

**[confirm: copy the exact wording of what Send feedback and the opt-in telemetry collect from
the D2 design (#732).]** Separately, the sessions you watch send their prompts, replies and
approvals to your own phone through Slipgates' relay, encrypted end to end so the relay cannot
read them. The relay does see connection details: which of your devices are online, when, and
how much each sends.

## Getting help and sending feedback

Until the in-app Send feedback button lands (#738, #739), reply in the email or Teams
conversation your invitation came in. Please do not file it as a public issue. If something
fails, tell us the first step that failed and its exact error message. If it all worked, tell
us that too: a PC session showing on your phone is the end of the guide. Never send tokens or
sign-in codes.

## Uninstall

Settings > Apps > Installed apps > Slipgates > **Uninstall**. This stops the background service and
removes what Slipgates added: its scheduled task, its hooks in your CLIs and its app data. It
never removes a CLI or signs you out of an account. Your project files are untouched.
