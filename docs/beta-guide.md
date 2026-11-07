# Slipgates beta guide

Thank you for trying Slipgates early. This page is the whole guide: what it is, how to install
it, what is rough, what we collect, and how to reach us. It is the source for slipgates.com/docs
(#762).

Open items marked **[confirm]** are waiting on the owner or Platform and are filled in before
the first tester gets the link.

## What Slipgates is

Slipgates lets you keep an eye on your AI coding sessions (Copilot CLI, Claude Code) from your
phone. When a session needs you, a toast on your PC and a card on your phone say so, and you can
answer, reply or stop it from wherever you are.

## Install on Windows

1. Open the newest pre-release at <https://github.com/danswett/agent-bridge/releases>
   **[confirm: a stable download link replaces this when #735 lands]**.
2. Download the installer for your PC:

   | PC | Installer |
   |---|---|
   | Intel or AMD (most PCs) | `AgentBridgeApp-win-Setup.exe` |
   | Windows on Arm (Snapdragon and similar) | `AgentBridgeApp-win-arm64-Setup.exe` |

   Not sure? Settings, System, About, "System type" says which.
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
the D2 design (#732).]** Nothing leaves your PC beyond that.

## Getting help and sending feedback

**[confirm: the private channel the owner names (#738, #739).]** Please do not file feedback as a
public issue.

## Uninstall

Settings, Apps, Installed apps, Slipgates, **Uninstall**. This stops the background service and
removes what Slipgates added: its scheduled task, its hooks in your CLIs and its app data. It
never removes a CLI or signs you out of an account. Your project files are untouched.
