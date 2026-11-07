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

1. Open the newest pre-release at
   <https://github.com/danswett/agent-bridge/releases>
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
4. Slipgates opens. In **Settings**, the **Agents on this machine** section shows whether your
   coding tools are ready and offers **Set up** or **Repair** for the hooks. Slipgates doesn't
   install your coding CLI, sign you in to it or trust your project folder; do those yourself
   first. If **Set up** can't make an agent ready, tell us which step it stopped at. The app
   keeps itself up to date and offers **Restart to update** when there is a newer version.

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

**[confirm: the controller's name and private contact, what each kind of data is used for,
who handles it, where it is stored and for how long, and your rights, from Compliance's finding
on #744 and the D2 design (#732). Do not send invitations until this is filled in.]**

- Normal use: the sessions you watch send their prompts, replies and approvals to your own phone
  through Slipgates' relay, encrypted end to end so the relay cannot read them. They do leave your
  PC to reach your phone.
- The relay does see connection details: which of your devices are online, when, and how much
  each sends.
- The optional automatic diagnostics described in #741 are not enabled in this beta and cannot
  be turned on yet. Their design excludes session content; they must pass #732's technical and
  privacy reviews before any uploads are enabled.
- Feedback is only what you choose to send, by replying to your invitation today. When the
  in-app Send feedback button lands, you will see a report before it goes. The feedback team can
  read what you send; it is not visible only to you.

## Getting help and sending feedback

Until the in-app Send feedback button lands (#738, #739), reply in the email or Teams
conversation your invitation came in. Please do not file it as a public issue. If something
fails, tell us the first step that failed. Send only the minimum we need, and remove names,
paths, prompts, code, tokens and sign-in codes first. Do not send a screenshot or log until you
have looked through it. If it all worked, tell us that too: a PC session showing on your phone is
the end of the guide.

## Uninstall

Settings > Apps > Installed apps > Slipgates > **Uninstall**. This stops the background service and
removes what Slipgates added: its scheduled task, its hooks in your CLIs and its install folder.
It never removes a CLI or signs you out of an account. Your project files are untouched.

Your profile (device key, account, settings and logs) stays in `%LOCALAPPDATA%\AgentBridge`, so a
reinstall picks up where you left off. To remove it too, close Slipgates, uninstall, then delete
that folder.
