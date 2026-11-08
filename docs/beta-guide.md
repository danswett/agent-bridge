# Slipgates beta guide

<!-- Source for slipgates.com/docs (#762). -->

Thank you for trying Slipgates early. This page is the whole guide: what it is, how to install
it, what is rough, what we collect, and how to reach us.

## What Slipgates is

Slipgates lets you keep an eye on your AI coding sessions (Copilot CLI, Claude Code) from your
phone. When a session needs you, a notification on your PC and a card on your phone say so, and
you can answer its questions, approve or deny what it wants to do, or stop it from wherever you
are.

## Install on Windows

1. Open the download page, <https://slipgates.com/download/>.
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
- Getting help: reply to the invitation email or write support@slipgates.com.
- Who answers: support@slipgates.com is live and receives mail; the owner's confirmed statement
  and its limits are under "Support email and privacy requests" below.
- Privacy notice: <https://slipgates.com/privacy>.

Invitations are currently on hold. Real reports must not be sent through the in-app or staging
feedback service while real-report collection and processing remain uncleared. You can still send
privacy questions to support@slipgates.com.

The operating details below are based on the November 7 deployed-build and settings reviews, plus
the confirmed support contact and report-preview behavior. Those reviews are not an independent
audit. Some client, provider and deletion details are still being confirmed and are identified
below. This notice does not promise anonymous use, US-only processing, no retained copies or
deletion of everything within 30 days. "Not yet confirmed" below is deliberate wording: it marks a
fact we do not yet know, not one we are leaving out.

### Hosting and GitHub sign-in

- **Use and providers.** The web application is configured for GitHub Pages. The production relay
  uses GitHub device-flow sign-in, receives the device-flow and token responses, obtains your
  numeric GitHub account ID and login, and issues a Slipgates account token containing the account
  ID, login and expiry. The actual hosting and proxy path for this website, Cloudflare's role and
  the full visitor-data handling have not yet been confirmed; an app-hosting configuration is not
  proof of this website's served content or provider behavior.
- **Location and access.** The reviewed relay compute is in Azure West US 2, United States.
  GitHub and Cloudflare processing locations, visitor-data categories and provider or
  administrative access roles have not yet been confirmed.
- **Retention and requests.** The Slipgates account token is valid for 30 days; that is not a
  30-day deletion promise. The deployed relay application-code review found GitHub tokens handled
  in memory during sign-in, not stored or logged by that application code. Automatic GitHub-token
  revocation was not configured according to the settings review. You can revoke the application's
  authorization in GitHub; that is different from deleting Slipgates account records. Client-side
  token storage, provider records and copies, and their retention and deletion procedures have not
  yet been confirmed.

### Account and device records

- **Use and providers.** Azure Blob Storage holds a roster for your account with machine ID, name,
  kind (host) and last-seen time, plus recently forgotten machine IDs and times. The reviewed
  implementation supports up to 64 machines.
- **Location and access.** The identified storage and relay compute are in Azure West US 2. The
  relay deployer's review reports account-scoped presence access for that account's signed-in
  devices. Azure service identities and operators also have administrative access paths. The
  complete effective reader-role set and processing outside the identified regional resources have
  not yet been confirmed.
- **Retention and requests.** A roster is configured to be deleted after all of its hosts have been
  offline for 180 days; an active account keeps its older entries. Soft delete,
  versioning and change feed are off in the reviewed account. Regional replication is not an
  absence of copies; other provider and downstream copies have not yet been confirmed. Forgetting
  a machine is not account erasure, and recently forgotten IDs and times remain in the roster.
  There is no relay account-erasure feature. A documented, accepted manual account-purge procedure
  has not yet been confirmed; contact the privacy address for requests.

### Session delivery and content

- **Use and providers.** Slipgates forwards session information, including prompts, replies and
  approvals, between the PC and devices connected to the account through the Azure relay. A review
  of the deployed relay code found socket-to-socket forwarding and no normal-frame persistence path
  in that code; the separate temporary-transfer path is described below.
- **Location and access.** The reviewed relay is in Azure West US 2. The deployer reports routing
  to the account's connected devices. Client encryption, actual key holders and endpoint access,
  and end-to-end content transit and processing locations have not yet been confirmed for this
  notice. We are not making an end-to-end-encryption or relay-unreadability guarantee on that
  evidence alone.
- **Retention and requests.** Normal frames are forwarded through process memory according to the
  code review, but actual memory and crash-dump handling and client and device copies, backups and
  retention have not yet been confirmed. Connection records have the separate retention periods
  below. A confirmed content-access and deletion procedure covering endpoints and remaining
  provider copies is not yet available; uninstalling or closing a connection must not be treated
  as erasing every copy.

### Connection, fault and security records

- **Use and providers.** The reviewed relay application logs connection tag, device kind,
  duration, frame and byte counts, sign-in completion and counters, and push host, status and byte
  counts, rather than normal message content in those identified logging statements. The
  monitoring services are Azure Container Apps, Log Analytics and Application Insights. The
  complete set of automatically collected fields, including any URLs, identifiers, IP information
  or payload data captured elsewhere, has not yet been confirmed. This is not a "no identifiers,"
  "no IPs" or "nothing logged" promise.
- **Location and access.** The identified compute and monitoring resources are in Azure West US 2.
  The subscription review reported a human Owner and relay-deployment identities; it did not
  establish the complete telemetry reader set or inherited and provider access. Processing outside
  the identified resources remains unconfirmed.
- **Retention and requests.** Azure monitoring retention is configured per workspace and table.
  The production relay's Application Insights AppRequests table is configured to retain data for
  90 days. Its Log Analytics workspace default is 30 days; retention settings for other tables
  have not yet been confirmed. The separate feedback-vault logging workspace is configured for 30
  days. These are configuration settings, not a guarantee of deletion from every copy. The
  contents of client-IP and payload fields have not been checked and remain unconfirmed. We do not
  claim that those fields contain no IP addresses or session content. Other copies and provider
  residuals are unconfirmed. No
  per-person telemetry deletion procedure is currently established in the supplied operational
  record. The privacy contact coordinates requests; scheduled ageing is not proof of
  request-based or all-copy erasure.

### Temporary transfers and push notifications

- **Use and providers.** Temporary transfers use Azure Blob Storage. The deployed-relay review
  describes receiving client-encrypted blobs; actual client encryption and key handling still need
  client-side confirmation. The configured web-push path signs and forwards notifications and
  handles the push endpoint, size and time. Depending on browser or operating system, its allowed
  delivery services include Apple web push, Google FCM, Mozilla and Microsoft Windows
  notifications. This allow-list does not establish which vendors have actually been used.
  Client-side push encryption, endpoint ownership and additional recipient restrictions remain
  unconfirmed.
- **Location and access.** The reviewed blob storage is in Azure West US 2. Extra-regional
  processing and push-provider locations and access have not yet been confirmed. The deployer
  reports HTTPS user-delegation links scoped to one blob and permission: upload and read links
  last 30 minutes and a transfer ticket lasts 24 hours. Do not share transfer links; further
  sharing and recipient enforcement and effective storage permissions remain unconfirmed.
- **Retention and requests.** The temporary `blobs/` prefix is configured for lifecycle deletion
  one day after creation. Soft delete and versioning are off in the reviewed storage; other copies
  remain unconfirmed. Push TTL is capped at 24 hours in the reviewed path, not a promise that every
  vendor copy is erased then. Link expiry is not data deletion. Effective manual blob-delete
  authorization, per-user deletion procedures and push and provider deletion limits have not yet
  been confirmed.

### Support email and privacy requests

For beta support, reply to the invitation email or write support@slipgates.com. Support is by
email only. The support mailbox can be read by the operator; send only the minimum needed. Do not
post support content or
personal information in public GitHub issues. Screenshots, logs and attachments can contain names,
paths, prompts, code, tokens or other private information; check them before sharing.

The confirmed contact for beta support and privacy requests is support@slipgates.com. Beta support
is email only: reply to your invitation email or write support@slipgates.com. Teams is not a beta
support route. Cloudflare Email Routing forwards messages sent to support@slipgates.com to the
operator's personal Gmail account. Only the operator reads support mail in the current beta setup;
there are no delegates, further forwarding or integrations receiving its content. There is no
automatic deletion: the operator keeps mail until he deletes it. Gmail and Cloudflare processing
locations, provider access, backup retention and other provider-copy/deletion limits remain
unconfirmed. This is not a guarantee that only the operator can technically access every provider
copy or that support messages are erased within 30 days.

The owner has confirmed:

> support@slipgates.com is live and receives mail. I, Dan Swett, check it each business day,
> arrange authorized coverage when away, aim to acknowledge privacy requests within three business
> days, and own triage and coordination with the relevant service operators for access, correction
> and deletion requests, subject to applicable law and the disclosed retention/provider limits.

The acknowledgement is an operational aim, not a legal response deadline or a promise to complete
a request or erase data within three days. Requests will be handled as required by applicable
law. Request coordination does not itself establish each service's ability or procedure to delete
records.

The beta does not offer an account-erasure feature or promise manual deletion. Roster records have
a configured expiry after 180 days untouched. For relay monitoring, the Application Insights
AppRequests table is configured for 90 days and its Log Analytics workspace default is 30 days;
other tables' retention settings have not yet been confirmed. The separate feedback-vault logging
workspace is configured for 30 days. Temporary transfer blobs have lifecycle deletion configured
one day after creation. These are configuration settings, not a guarantee of erasure from every
copy; support-mail and other provider-copy retention are not yet fully confirmed.

You can revoke Slipgates' authorization in GitHub. That does not delete Slipgates records or
guarantee immediate invalidation of already-issued Slipgates account tokens. For privacy questions
or requests, email support@slipgates.com. The privacy address still receives and answers requests
as required by applicable law. This description does not waive rights or duties that applicable
law imposes.

### In-app feedback, the preview and the redactor

The reviewed production relay has feedback uploads disabled: the reviewed deployed build has no
feedback upload route and no feedback settings. The staging service can accept uploads from
signed-in accounts and does not itself enforce the synthetic-only rule. Staging is for instructed
synthetic testing, not real reports; do not submit real reports there. Client endpoint selection
and enforcement of the current hold still need confirmation. A report-builder or preview screen is
not permission to send real data while that hold applies.

The client includes a report preview before you choose Send. The redactor removes values from
recognized password, token and similar named fields. It does not catch secrets without a
recognized name, and other sensitive information can remain. **Check the preview:** it shows the
report content you would send. Do not choose Send if it includes anything you do not want to
share. Redaction is not a guarantee that a report, screenshot, log or attachment contains no
sensitive information.

The provisioned production feedback storage and identified vault and audit resources are in Azure
West US 2; the reviewed staging storage and relay are also in West US 2. The provisioned relay
feedback identity has create-only access, not read, list or delete. The worker identity has
container-scoped Blob Data Reader and Secrets User access to the dedicated vault. These grants do
not establish complete custodian isolation or every inherited or provider access path.

The production ciphertext container has a locked 30-day minimum hold and lifecycle deletion
configured for day 31 after creation; deletion is not available during the minimum hold, and
lifecycle configuration is not proof of erasure from every copy. Staging's 30-day policy is not
locked. The reviewed production and staging feedback accounts have blob and container soft delete
and versioning off. The configured feedback read-audit period is 30 days; the credential vault has
90-day secret soft delete and purge protection. Credential and audit schedules are not
report-content deadlines.

Planned automated feedback handling would decrypt reports for authorized feedback staff and store
plaintext in private GitHub issues. The approved design gives the supervising AI service status
only, not report plaintext or keys; this isolation is not yet proven in a ready production worker.
Real-report processing is not cleared. The adopted policy is to remove active plaintext within 30
days of the original server-recorded upload time, earlier for actionable requests; implementation
and complete deletion and replay suppression are not yet proven. Actual worker, App and
key-custody readiness, GitHub readers, integrations, copies and locations, receipt, failure and
private-copy schedules, and the complete deletion procedure and provider residuals remain
unconfirmed. No configured backup is not a guarantee of no copies.

### Optional automatic diagnostics

Optional automatic diagnostics are not enabled in this beta and cannot be turned on yet. Their
design excludes session content; they must pass technical and privacy reviews before uploads are
enabled. Any future optional diagnostics require your separate opt-in, worldwide. You can use
Slipgates without agreeing to them. This is about the optional diagnostic feature, not a claim
that normal sign-in, hosting, connection records or information you deliberately share contain no
personal or session information.

If optional diagnostics become available, you can refuse them or later turn them off without
losing normal functionality. Turning them off stops future optional diagnostic collection and
uploads; it does not itself delete records already received. Contact the privacy address about
retained information.

### Uninstalling and unresolved provider handling

Uninstalling does not itself erase server or support records. The Windows profile, including
device keys, settings and logs, also remains locally unless separately removed using the uninstall
instructions.

The US invitation scope does not mean all processing stays in the US. The identified Azure
regional resources do not establish all provider, transit, endpoint, support or backup locations.
Provider access, residual copies and retention and deletion limits identified as unconfirmed above
remain unresolved. This website does not make a blanket assurance that its hosting and proxy
providers collect no visitor information, set no cookies, use no analytics or involve no other
services.

## Beta conditions

Slipgates is an experimental beta operated by Dan Swett, an individual, for invited adults in the
United States only. Features may fail or change. Review remote approvals carefully: an approval on
your phone can authorize an action on your PC. Use only devices, accounts and sessions you are
authorized to control; keep a recoverable copy of important work.

See the beta guide for supported configurations, known issues and update instructions. For beta
support or privacy questions, contact support@slipgates.com. Read the privacy notice at
https://slipgates.com/privacy for the current data-handling facts, unresolved details and choices.
Optional diagnostics are not a condition of participation and require separate opt-in if made
available. Do not send real reports through an in-app or staging feedback path while that service
remains held.

You can stop participating, disconnect devices or uninstall. Uninstalling or revoking GitHub
authorization does not itself erase server or support records. Use the privacy contact for separate
requests about retained information; the privacy notice describes the beta's plan to let records
age out without promised manual deletion, and known limits. The published acknowledgement aim is
not a guaranteed
support response, resolution or erasure deadline.

The applicable open-source and third-party licences accompany the distribution. These beta
conditions do not take away rights those licences grant or mandatory rights the law gives you.

## Getting help and sending feedback

For help today, reply to the invitation email or write support@slipgates.com. How that channel
handles and keeps what you send is described, with what is not yet confirmed, under "Support email
and privacy requests" above. Send only the
minimum needed. Remove names, paths, prompts, code, tokens and sign-in codes, and review any
screenshot or log before sending it. Do not post support content or personal information in public
GitHub issues. If something fails, tell us the first step that failed. If it all worked, tell us
that too: a PC session showing on your phone is the end of the guide.

The app has a report preview before you choose Send. The redactor removes values from recognized
password, token and similar named fields; it does not catch secrets without a recognized name, so
check the preview yourself. The app shows this warning:

> Check the preview. The redactor removes values from recognized password, token and similar named
> fields; secrets without a recognized name and other sensitive information may remain. The
> preview shows the report content you would send. Do not choose Send if it includes anything you
> do not want to share.

While the real-report hold remains, it also shows:

> Real reports are not open for submission. Do not send real session content or personal
> information through this feedback path. Only explicitly instructed synthetic testing is
> permitted.

## Uninstall

Settings > Apps > Installed apps > Slipgates > **Uninstall**. This stops the background service and
removes what Slipgates added: its scheduled task, its hooks in your CLIs and its install folder.
It never removes a CLI or signs you out of an account. Your project files are untouched.

Uninstalling does not itself erase server or support records; use the privacy contact for those.
Your profile (device key, account, settings and logs) stays in `%LOCALAPPDATA%\AgentBridge`, so a
reinstall picks up where you left off. To remove it too, close Slipgates, uninstall, then delete
that folder.
