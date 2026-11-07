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

1. Open the newest pre-release at <https://github.com/danswett/agent-bridge/releases>
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
   installer is not signed yet, and Windows warns about any installer it does not recognise.
   Click **More info**, then **Run anyway**.
4. Slipgates opens and walks you through setup. The app then keeps itself up to date and offers
   **Restart to update** when there is a newer version.

## Connect your phone

**[confirm: Platform supplies the phone address and the sign-in steps.]**

## Known issues

- The installer is unsigned, so Windows warns as described above (#734).
- On Windows on Arm the x64 installer runs but is untested; use the Arm installer.
- See the list of open bugs for the rest: <https://github.com/danswett/agent-bridge/issues>.

## What we collect

**[confirm: copy the exact wording of what Send feedback and the opt-in telemetry collect from
the D2 design (#732).]** Separately, the sessions you watch send their prompts, replies and
approvals to your own phone through Slipgates' relay; that is how the phone sees them.

## Getting help and sending feedback

Until the in-app Send feedback button lands (#738, #739), email or message us on Teams
**[confirm: the address the owner names]**. Please do not file it as a public issue.

## Uninstall

Settings > Apps > Installed apps > Slipgates > **Uninstall**. This stops the background service and
removes what Slipgates added: its scheduled task, its hooks in your CLIs and its app data. It
never removes a CLI or signs you out of an account. Your project files are untouched.
