# Beta feedback custody

**Draft, not cleared for real reports or operational.** Product reported owner
approval of the Azure build and storage retention configuration. Build and test
with synthetic data only. Real-report decryption remains off until Engineering
and Compliance review, original-upload-time removal and deletion procedures,
and Quality's synthetic proof are complete. Requirements below are not evidence
of deployed controls.
Feedback Inbox owns this document; SRE supplies the infrastructure and access
sections. This is the single custody document for [#740](https://github.com/danswett/agent-bridge/issues/740).

## Hosting and security boundary

**SRE: actual configuration and evidence pending.** Product reported approval
of SRE's isolated Azure worker plan: West US 2, resource group
`rg-agent-bridge-feedback`, a dedicated Container Apps environment and a small
scheduled job with no public inbound access. Estimated cost is $3 to $7 per
month, not a billed amount. The worker uses managed identity; the VM3
certificate remains staging-only and must not be used for production. Prove
that the supervisor cannot retrieve worker secrets, debug or modify the worker,
or redirect plaintext. Record actual deployment, administrative access, review
date and evidence without publishing credentials.

## Key custody

V1 uses X25519. Azure Key Vault can hold a protected secret but its native EC key
operation does not perform X25519 agreement. Select and verify one concrete
secret location, access path and worker-only boundary. Only the worker and
explicitly approved, named recovery custodians may retrieve the private key;
the supervisor, relay and testers may not. Separate staging and production key
material and key IDs. Hold plaintext keys only in worker memory, never on disk
in plaintext or in chat, logs or session history.

The approved plan uses a new dedicated Key Vault for the decryption and GitHub
App keys, with soft delete, purge protection and read audit; deployment and
effective isolation remain unverified. **Pending:** ownership, key IDs,
public-key distribution, rotation, old-key recovery window covering unprocessed
retained uploads, recovery drill, revocation and compromise response. Do not
publish private-key values. Managed identity is not the feedback decryption key.

### Production recipient-key handoff for Windows

The Windows production recipient key is a reviewed, compiled-in public constant,
not a value learned from the relay ready message. Its matching private key must
be the Customer Feedback worker's production key in the dedicated vault. Do not
generate a separate sender-only key, reuse the staging key or use the VM3
certificate as production custody. Until the evidence below is complete, the
production defaults remain empty and sending remains unavailable; staging
configuration must not supply a production fallback.

Feedback Inbox owns the handoff; SRE owns actual provisioning, effective access
and deployment evidence, Trust reviews the key binding, and Quality verifies
the synthetic proof. These assignments do not grant the supervising agent
secret access or authorize changes to host setup or permissions. Generation or
import must use the owner-approved provisioning path within the approved
custody boundary. Never return private material through an agent tool, terminal
output, a shell transcript or a generated patch. Secret-store writes must
suppress secret-bearing responses, not just hide the input command.

Record one nonsecret handoff packet in this document when the actual worker and
key exist. Do not fill missing fields with sample or staging values.

| Evidence | Required value or proof |
|---|---|
| Recipient key ID | A production-only ID matching `[A-Za-z0-9._-]{1,64}`; never remap an existing ID to different key material. |
| Public key | Canonical, unpadded base64url encoding of exactly 32 X25519 public-key bytes, derived from the retained production private key. |
| Private-key custody | Actual vault name, secret name and immutable secret version, without the secret value, credentials or a secret-retrieval command. |
| Worker binding | Deployed worker revision, managed-identity principal and effective secret-read scope; confirm relay, supervisor and staging identities cannot read the key. |
| Matching-key proof | Quality seals a synthetic envelope with the proposed public constant; the deployed worker reads the recorded private-key version and verifies the expected synthetic content internally. Output is fixed pass/fail only. |
| Negative and output proof | Wrong recipient ID or key is refused, unavailable vault access does not process a report, and private material or synthetic canary text does not appear in worker, supervisor or provider outputs. |
| Reviewed sender head | Exact Windows commit containing both constants, with independent review and Quality's proof bound to that key ID, public key and worker secret version. |

No production packet has been verified here. SRE's resource and access evidence,
the public key and key ID, Trust's binding review and Quality's matching-key
proof remain pending. A filled packet is not Compliance clearance to process
real reports, nor proof that production collection is enabled.

For rotation, record the old and new key IDs and vault versions, supported
senders, retained-upload recovery window, approved custodians and revocation
route before changing the compiled default. Keep each supported key ID bound
to its original private-key version through the reviewed recovery window; do
not remove an old version merely because a newer sender was published.
Compromise response must explicitly cover both sender rollout and retained
uploads. Removing a key does not delete already filed plaintext.

## Standing access and audit

**SRE: actual assignments, audit and evidence pending.** The worker's managed
identity needs Storage Blob Data Reader on the production feedback container
only, Key Vault Secrets User for the two secrets in the dedicated vault, and
container-registry pull rights. It must not have blob write, delete or
account-wide access. Separate staging and production resources and access;
the relay and supervising agent must not have access to the vault. Verify
effective permissions and record identities, role scopes, audit, access
review and one-step revocation.

A separate private-repository-only GitHub App installation token needs Issues
read/write and Metadata read without repository visibility or administrative
permissions. Do not reuse the owner's general token. Record token issuance,
expiry, rotation, audit and revocation without publishing credentials. The owner
provisions the app key through an approved secret store, never through chat.
The person authorizes and provisions host setup and permissions.

## Plaintext and extra copies

The deterministic worker decrypts and writes private issues; the supervising
agent sees fixed status and counts, not report text or keys. Necessary transient
worker memory and TLS transmission are distinct from retained plaintext.
Persist report content only in the approved private issue store. Confirm private
visibility before processing and before writes; stop if it is false or unknown.
Do not place content in agent context or history, stdout, shell transcripts,
temporary exports, dumps, logs, telemetry, public issues or automatic model
summaries.

Inventory GitHub email notifications, webhooks, Apps, Actions and AI
integrations and prevent additional copies or recipients, or obtain approval
for their actual scope and notice before enabling them. Record private readers
and visibility-changing administrators. **These controls and integrations are
unverified.** Automatic public filing is disabled; current v1 reports are
unclassified, not reviewed grouping or summarization. The lack of an
authenticated tester count does not make report content anonymous.

## Retention and deletion

**SRE: actual production schedules and evidence pending.** Record the policy,
owner, enforcement and deletion verification separately for encrypted uploads
and immutable hold; private issue bodies and comment chunks; partial writes;
receipts and failure records; operational and access-audit logs; backups; and
notifications or exports. A 30-day immutable minimum is not a maximum; a
day-31 lifecycle rule alone does not prove erasure. Product reported owner
approval for a 30-day production container retention lock and day-31 deletion;
SRE must verify actual configuration, lock state and deletion behavior.

The owner adopted a maximum of 30 days from the original upload for active
plaintext, with earlier actionable deletion requests; enforcement is not yet
implemented. Use the actual original upload timestamp, preserved through
processing, retries and restores. The current processor uses processing time,
and its source interface does not provide upload time; both must be corrected
and verified before real-report processing. Define a private report-reference
request route without collecting extra identity. Remove all applicable issue
chunks and partial writes, describe provider residuals and backups honestly,
and keep only minimal, time-bounded suppression metadata so retries and
restores do not recreate deleted reports. Closing an issue or destroying the
key does not erase filed plaintext. Explicitly document immutable-upload
limitations and the need for legal determination of deletion and provider
obligations.

## Logging allow-list

Allow only fixed outcome and error codes, counts, durations and minimum
access-audit metadata. Restricted reconciliation identifiers need a documented
purpose, access and expiry. Do not log report text, prompts, screenshots,
names or paths from reports, keys, bearer or upload tokens, request or response
bodies, or raw exception dumps in worker output, supervisor history, alerts,
CI artifacts or monitoring. Storage access audit is a separately retained
category. Synthetic canary testing must cover the actual host, providers and
integrations; that proof is pending.

## Failure, receipts and recovery

Write a durable private per-report receipt only after the private issue write
succeeds. Maintain explicit pending, filed and failed or rejected states, a
restricted durable failure queue, single-writer enforcement and idempotent
retry and restart reconciliation. Preserve original ciphertext through verified
retention and escalate expiry risk. Do not count failed decrypts as filed or
promise recovery of permanently unreadable reports.

The current processor saves an invalid envelope privately and marks it processed;
this is not the required unresolved-failure state. Fix it or approve an
equivalent reviewed design before use. Upload success means upload only, not a
filing or a reply.

## Monitoring and responder

**SRE: actual transport, configuration and responder pending.** Poll and emit a
worker heartbeat every five minutes; target filing within 15 minutes under
healthy operation. Engineering proposes a dashboard warning at 15 minutes and
paging at 30 minutes. Confirm the signal transport and authentication, failure
count, oldest pending calculation, email or page destination, accepted responder
and backup. Detect both a dead worker and stalled processing: heartbeat alone
does not prove a successful sweep.

## Decisions and proof before real reports

Product reported owner approval of the specified Azure worker and production
storage retention configuration. SRE supplies actual infrastructure, access
and retention facts in this document. Engineering reviews consistency and
hands the document to Compliance. The approvals do not clear real-report
processing; Compliance clearance, the original-upload-time plaintext limit and
the deletion procedure remain prerequisites.

Quality must prove synthetic end-to-end identity and key restrictions,
private-only writes, privacy-check failure, key outage, malformed report,
retry and restart, single-writer behavior, deletion without replay, key
rotation and revocation, backlog and expiry alerts, and absence of canary
content or secrets from outputs and supervisor history. Update #744 and the
privacy notice with actual providers, regions, retention and request limitations
before invitations. Until these gates pass, real-report decryption remains off.
