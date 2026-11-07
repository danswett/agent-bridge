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

**[confirm: the facts box below needs operator-approved facts before invitations go out
(Compliance, #744 comment 6711271803): (1) the legal operator or controller's name, a private
privacy and support contact, and the first cohort's countries and age scope; (2) each purpose,
lawful basis, recipient, provider and storage or processing country, covering GitHub sign-in and
hosting, account and device records, relay metadata, enabled content, blob and push paths, and
support email or Teams; (3) actual retention or deletion criteria, access roles, and who handles
access and deletion requests. Do not send invitations until this is filled in, and link the
published privacy notice and beta conditions.]**

Slipgates connects the coding sessions on your PC to your own devices. Normal use sends prompts,
replies and approvals between those devices, encrypted end to end so the relay cannot read them.
That content does leave your PC to reach your phone. The relay handles connection details,
including which devices are online, when they connect and how much they send.

Optional automatic diagnostics are not enabled in this beta and cannot be turned on yet. Their
design excludes session content; they must pass technical and privacy reviews before any uploads
are enabled. Any future optional diagnostics will require your separate opt-in. You can use
Slipgates without agreeing to them.

Your choices and rights depend on the law that applies. Use the private privacy contact above to
request access, correction, deletion, restriction or portability, or to object where those rights
apply. You may complain to the relevant data-protection authority. If optional diagnostics become
available and you consent, you can withdraw that consent without affecting processing already
lawfully carried out. A request to delete data we already received is separate from turning
diagnostics off.

## Beta conditions

Slipgates is an experimental beta operated by the organisation or person identified in the
privacy notice. Features may fail or change. Review remote approvals carefully: an approval on
your phone can authorize an action on your PC. Use only devices, accounts and sessions you are
authorized to control, and keep a recoverable copy of important work.

This guide names supported configurations, known issues, updates and the private support route.
You can stop participating, disconnect devices or uninstall; use the privacy contact for
separate requests about retained data. Optional diagnostics are not a condition of
participation.

The applicable open-source and third-party licences accompany the distribution. These beta
conditions do not take away rights those licences grant or mandatory rights the law gives you.

## Getting help and sending feedback

For help today, reply privately in the email or Teams conversation your invitation came in. The
support team can read what you send, subject to that channel's handling and retention described
above. Send only the minimum needed. Remove names, paths, prompts, code, tokens and sign-in
codes, and review any screenshot or log before sending it. Do not post support content or
personal information in public GitHub issues. We are not promising an in-app report preview in
this build. If something fails, tell us the first step that failed. If it all worked, tell us
that too: a PC session showing on your phone is the end of the guide.

## Uninstall

Settings > Apps > Installed apps > Slipgates > **Uninstall**. This stops the background service and
removes what Slipgates added: its scheduled task, its hooks in your CLIs and its install folder.
It never removes a CLI or signs you out of an account. Your project files are untouched.

Uninstalling does not itself erase server or support records; use the privacy contact for those.
Your profile (device key, account, settings and logs) stays in `%LOCALAPPDATA%\AgentBridge`, so a
reinstall picks up where you left off. To remove it too, close Slipgates, uninstall, then delete
that folder.
