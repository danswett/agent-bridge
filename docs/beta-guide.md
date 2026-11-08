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
   | Intel or AMD (most PCs) | `Slipgates-win-Setup.exe` |
   | Windows on Arm (Snapdragon and similar) | `Slipgates-win-arm64-Setup.exe` |

   Not sure? Open Settings > System > About and look at "System type". "x64-based processor"
   means the first file; "ARM-based processor" means the second. Older releases may still have
   installer filenames beginning `AgentBridgeApp`; download links use each release's actual files.
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
2. On your phone, open <https://app.slipgates.com/> and choose
   **Sign in with GitHub**. Open the GitHub link it shows, enter the code, and use the same
   GitHub account as your PC.
3. Return to the phone app. Your PC's sessions appear there.

Installed the app earlier from `danswett.github.io/agent-bridge/app/`? Open the new address,
install it again and sign in again: the old copy's sign-in and settings do not move across.
What you lose and what to do is in [the address move notes](app-domain.md#if-you-installed-the-app-from-the-old-address).

## Known issues

- The installer is unsigned, so Windows warns as described above (#734).
- On Windows on Arm, use the Arm installer. The Intel/AMD one runs there but we haven't tested it.
- See the list of open bugs for the rest: <https://github.com/danswett/agent-bridge/issues>.

## What we collect

**Who runs this beta**

- Operator: Dan Swett, an individual.
- Contact for privacy and support requests: support@slipgates.com.
- First beta cohort: invited adults in the United States only.
- Getting help: reply in the email or Teams conversation your invitation came in.
- Privacy notice and beta terms: [confirm: links to the published privacy notice and terms].

**What we keep, and for how long**

These are the retention and expiry settings as configured. They are not a promise that data is
erased from every copy, and a request to delete is handled separately (see below).

- The identified Azure resources are in West US 2.
- The account and device roster expires after 180 days untouched.
- Operational logs are kept 30 days (Log Analytics) and application telemetry 90 days
  (Application Insights).
- Temporary delivery files (`blobs/`) are set to expire one day after they are created.
- Feedback storage (`feedback/`) exists but production feedback uploads are off today. It is
  locked for a minimum of 30 days, then a lifecycle rule applies from day 31. The 30 days is a
  minimum hold, not a maximum.

**What happens when you use it**

Slipgates connects the coding sessions on your PC to your own devices. These are the paths in use
in the beta today.

- **Signing in.** The app uses GitHub's device sign-in. The relay asks GitHub who you are and
  keeps your GitHub account number and login name in a sign-in token that is valid for 30 days.
  The relay does not store or log your GitHub access token. It does not revoke that token
  automatically, so you can revoke the app yourself in your GitHub settings. Signing out or
  revoking is separate from asking us to erase relay records.
- **Your devices.** The relay keeps a list for each account: each device's id, name, kind and
  last-seen time, up to 64 devices, plus ids of devices you recently removed. The relay's code
  returns a device only to the account that owns it. An entry expires after 180 days untouched.
- **Session content.** Prompts, replies and approvals leave your PC and pass through the relay to
  your other devices. The relay forwards them from memory and its code has no step that saves
  them. We can't confirm from the relay that your devices encrypt them, so we don't promise that
  the relay can't read them. Copies on your own devices are outside this list.
- **Larger items.** Items too big to send directly go through temporary Azure Blob Storage links
  that work for 30 minutes and are set to expire one day after they are created.
- **Connection records.** The relay's logs record a connection tag, device kind, how long a
  connection lasted, how many frames and bytes it carried, sign-in completions and, for
  notifications, the push host, result and size, not the message. Other details collected
  automatically may also appear in these records.
- **Push notifications.** Push is switched on in production. The relay signs the notification and
  forwards it to the push service your device uses, and it may be one of up to four: Apple,
  Google, Mozilla or Windows. The payload is encrypted by your device, and the relay sees the
  address, size and time. The relay keeps no push subscriptions.
- **Feedback.** Sending feedback from the app is not available in production, because the
  production build has no feedback code. Use the support route below. A testing environment
  accepts reports from any signed-in account, so do not send anything real there.

**Still to confirm before invitations go out**

- [confirm: where GitHub, Cloudflare and the push services process data, and what Cloudflare
  handles for the app's address]
- [confirm: which other details the relay's logs can hold, for example IP addresses, and who can
  read them]
- [confirm: whether anyone besides your own devices can decrypt session content, and what copies
  exist on devices or with push services]
- [confirm: which push services are actually used, and who can open a temporary item link]
- [confirm: who runs the support mailbox and Teams conversations, how long messages are kept, and
  who can read them]
- [confirm: the written procedure and the person responsible for access, correction and deletion
  requests, including relay records, which has no automatic erasure today]

Optional diagnostics are always opt-in: they are off unless you choose to turn them on, and this
applies to everyone, worldwide. Their design excludes session content. You can use Slipgates
without agreeing to them. This build does not yet send diagnostics.

Logs and telemetry on the relay are separate from that: operational logs are kept 30 days and
application telemetry 90 days, as configured above.

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
support team can read what you send. How that channel handles and keeps it is still to be
confirmed (see the list above). Send only the minimum needed. Remove names, paths, prompts, code, tokens and sign-in
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
