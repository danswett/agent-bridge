# Beta feedback custody

**Draft, not approved or operational.** Real-report decryption remains off until
person-controlled setup approval, Engineering and Compliance review, and
Quality's synthetic proof are complete. Requirements below are not evidence of
deployed controls.
Feedback Inbox owns this document; SRE supplies the infrastructure and access
sections. This is the single custody document for [#740](https://github.com/danswett/agent-bridge/issues/740).

## Hosting and security boundary

**SRE: actual configuration and evidence pending.** The owner selected the plan
for an isolated Azure worker with managed identity. The VM3 certificate remains
a staging test identity and must not be used for production. The selection does
not authorize creating resources or granting access. SRE must provide cost,
region and exact permissions for separate
person-controlled setup approval. Prove that the supervisor cannot retrieve worker
secrets, debug or modify the worker, or redirect plaintext. Record the approved
host, identity, administrative access, region, review date and evidence without
publishing credentials.

## Key custody

V1 uses X25519. Azure Key Vault can hold a protected secret but its native EC key
operation does not perform X25519 agreement. Select and verify one concrete
secret location, access path and worker-only boundary. Only the worker and
explicitly approved, named recovery custodians may retrieve the private key;
the supervisor, relay and testers may not. Separate staging and production key
material and key IDs. Hold plaintext keys only in worker memory, never on disk
in plaintext or in chat, logs or session history.

**Pending:** location, ownership, key IDs, public-key distribution, rotation,
old-key recovery window covering unprocessed retained uploads, recovery drill,
revocation and compromise response. Do not publish private-key values. Managed
identity is not the feedback decryption key.

## Standing access and audit

**SRE: actual assignments, audit and evidence pending.** The worker's managed
identity needs container-scoped list/read only: no blob write, delete or
account-wide access. Separate staging and production resources and access.
Record the identities, provider regions, role scopes, audit, access review
and one-step revocation.

A separate private-repository-only GitHub App installation token needs Issues
read/write without repository visibility or administrative permissions. Do not
reuse the owner's general token. Record token issuance, expiry, rotation, audit
and revocation without publishing credentials. The owner provisions the app key
through an approved secret store, never through chat. The person authorizes and
provisions host setup and permissions.

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
day-31 lifecycle rule alone does not prove erasure, and the actual production
lock state is unverified.

The owner adopted a maximum of 30 days from the original upload for active
plaintext, with earlier actionable deletion requests; enforcement is not yet
implemented. Use the actual original upload timestamp, preserved through
processing, retries and restores. The current processor uses processing time,
and its source interface does not provide upload time; both must be corrected
and verified before real-report processing. Define a private report-reference request route without
collecting extra identity. Remove all applicable issue chunks and partial
writes, describe provider residuals and backups honestly, and keep only minimal,
time-bounded suppression metadata so retries and restores do not recreate
deleted reports. Closing an issue or destroying the key does not erase filed
plaintext. Explicitly document immutable-upload limitations and the need for legal
determination of deletion and provider obligations.

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

The owner selected an isolated Azure worker, a repository-restricted GitHub App
and the 30-day active-plaintext limit. These choices do not authorize setup:
SRE must provide cost, region and exact permissions for separate person-controlled
approval. SRE supplies actual infrastructure, access and retention facts in
this document. Engineering reviews consistency and hands the document to
Compliance.

Quality must prove synthetic end-to-end identity and key restrictions,
private-only writes, privacy-check failure, key outage, malformed report,
retry and restart, single-writer behavior, deletion without replay, key
rotation and revocation, backlog and expiry alerts, and absence of canary
content or secrets from outputs and supervisor history. Update #744 and the
privacy notice with actual providers, regions, retention and request limitations
before invitations. Until these gates pass, real-report decryption remains off.
