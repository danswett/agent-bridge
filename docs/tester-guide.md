# Trying Slipgates: first-draft tester guide

This draft walks you from a Windows install to a session on your phone.
It is not a launch announcement. Beta invitations remain on hold; use this guide
only within an explicitly invited or approved test.

## What Slipgates is

Slipgates lets you follow your AI coding sessions across your PCs and phone.
When a session needs an answer, you can read the question and respond from your
phone instead of returning to that PC.

Remote approvals can authorize actions on your PC. Read each request before
answering, and use only accounts, devices and sessions you are allowed to control.
Keep a recoverable copy of important work while trying the beta.

## Install on Windows

1. Open the [download page](https://slipgates.com/download/).
2. Choose the installer for your PC:

   | Your PC | Installer |
   |---|---|
   | Intel or AMD, x64 | `Slipgates-win-Setup.exe` |
   | Windows on Arm | `Slipgates-win-arm64-Setup.exe` |

   To check, open Windows **Settings > System > About > System type**.
   Older releases may have filenames beginning `AgentBridgeApp`; use the file
   linked for that release and your architecture.
3. Run the installer. The beta installer is currently unsigned. Windows may show
   **Windows protected your PC**. If you downloaded the expected file from the
   download page and want to continue, choose **More info > Run anyway**.
   If your organization blocks it or you cannot verify the download, stop and
   ask for help; do not change Windows or organizational security settings.
4. Open Slipgates. In **Settings > Agents on this machine**, check your coding
   tools and use **Set up** or **Repair** where offered.

Slipgates does not install your coding CLI, sign you in to it or trust a project
folder for you. Complete those steps yourself at the PC. An agent session cannot
approve host setup or trust on your behalf.

When an update is available, the app offers **Restart to update**. Choose a time
that works for you rather than interrupting work to try the latest build.

## Connect your phone

1. On the PC, open Slipgates **Settings > Your devices** and choose
   **Sign in with GitHub**. Follow the displayed GitHub link and code.
2. On your phone, open the [Slipgates web app](https://app.slipgates.com/).
   Choose **Sign in with GitHub** and complete its displayed sign-in steps using
   the same GitHub account as the PC.
3. Return to the phone app. Start or open a coding session on your PC and check
   that it appears on the phone.
4. Try answering a harmless question. Check the original session at the PC to
   confirm it received your answer.

Keep sign-in codes private. If the PC or session does not appear, check that both
devices are online and signed in to the same account. Report the first step that
did not work rather than repeatedly sending an answer.

If you installed the phone app from the old GitHub Pages address, use the new
address and sign in again. See the [address move notes](app-domain.md) for what
does and does not carry over.

## Known issues

- The Windows installer is unsigned and may trigger SmartScreen, as above.
- Use the Arm installer on Windows on Arm; the x64 installer there is not a
  tested configuration.
- This tester path covers Windows and the phone web app, not native Apple apps.
- Other problems remain in the beta. The [public issue list](https://github.com/danswett/agent-bridge/issues)
  records known work; it is not a place to upload private support material.

## Send feedback and opt-in data: placeholder

**Pending #738 and #741.** This draft intentionally does not describe what Send
feedback or optional diagnostics collect, send, retain or delete.
That section will be written after those features and their reviewed wording
settle. Do not treat this placeholder as consent or permission to submit a report.
Do not send real reports through held in-app or staging feedback paths.

For existing beta conditions and data-handling notices, read the
[beta guide](beta-guide.md) and [privacy notice](https://slipgates.com/privacy).
This first draft does not replace them.

## Get help

Reply to your invitation email or write to **support@slipgates.com**.
Say which step failed, your app version and whether your PC is x64 or Arm.
Review anything you share: remove sign-in codes, tokens, prompts, source code,
project paths and other private information from text and screenshots.
Do not post personal information or support logs in public GitHub issues.

## Uninstall cleanly

1. In Windows **Settings > Apps > Installed apps**, find **Slipgates** and choose
   **Uninstall**. Older installs may use the former name, Agent Bridge.
2. Uninstall removes Slipgates' installed app, scheduled task and CLI hooks.
   It does not remove your coding CLIs or projects, or sign you out of them.
3. Your local profile remains in `%LOCALAPPDATA%\AgentBridge`, so reinstalling
   can pick up your device identity, settings and logs. If you want to remove
   that profile too, close Slipgates, uninstall first, then delete only that
   specific folder. This removes the saved local identity and settings.

Uninstalling is not a request to erase server or support records. Use the privacy
contact in the privacy notice for those requests.
