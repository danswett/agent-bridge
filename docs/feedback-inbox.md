# Beta feedback inbox

The inbox is a separate worker from the relay. The relay's feedback upload is write-only
and carries ciphertext; this worker reads the dedicated private feedback container using
its own identity. It never uses a relay read URL or the relay's identity.

The first slice defines the processing boundary:

- `IEncryptedFeedbackBundleSource` lists and reads bounded encrypted bundles, then records
  completion only after the private record and any public update have succeeded. The
  read-only staging identity cannot delete the retention-protected blobs.
- `IFeedbackEnvelopeDecryptor` and `IFeedbackPrivateKeyProvider` isolate the envelope and
  key custody. Azure Key Vault's EC keys cannot perform v1 X25519 agreement; SRE's proposed
  production design uses a dedicated account with separately protected key material.
- `IFeedbackPrivateStore` is the only destination for decrypted content. Before listing
  or decrypting bundles, the processor requires an explicit confirmation that the
  configured repository is private. A confirmed-public result pauses the worker, and an
  unavailable result is retried; neither permits processing. It must upsert idempotently
  by bundle name and group by fingerprint.
- `IPublicFeedbackIssueStore` receives a separate, validated type that has no free text,
  logs, screenshots, paths, names, session content or decrypted payload. It owns durable
  hourly creation limiting and fingerprint-based create-or-update behavior.

The public tester count remains `unknown`. A key inside client-encrypted feedback is
still client-controlled and forgeable, so the processor does not accept or count it.
Only a future backend-authenticated identity source could safely support that count.

Public error and stage values must match the explicit `PublicErrorTypes` and `PublicStages`
allowlists. Both default to empty, so an unrecognized code stays private until the #738
envelope vocabulary is reviewed and configured. Public versions accept only numeric
components of up to four digits with an optional `alpha`, `beta` or `preview` channel and
numeric suffix. This accommodates workflow build numbers such as `0.1.1108`. Unclassified
decrypted feedback retains its original metadata and private content in the private store
for possible later classification.

Public titles and labels follow #740: `[beta-feedback] <error type> at <stage> (<fp8>)`,
`beta-feedback`, and `fb-kind:<error-type>`. The body is a deterministic rendering of
allowlisted error type and stage, versions, OS, architecture, an `unknown` tester count,
and the fingerprint. The worker is disabled unless explicitly enabled;
`Enabled` is its kill switch, and the default public creation cap is ten per hour.
Adapters must translate retryable I/O failures to `FeedbackInboxTransientException`; the
worker logs them and retries on the next poll without discarding pending bundles.
The private-repository verifier returns `false` only for an explicitly confirmed public
repository, returns `null` when the status is unavailable, and throws
`FeedbackInboxTransientException` for retryable GitHub I/O failures.
Envelope parsers must map malformed/authentication failures to `FeedbackEnvelopeException`;
key-provider outages must use `FeedbackInboxTransientException`, not
`CryptographicException`.

The inbox uses the shared v1 envelope parser to validate the client header, ciphertext
length and canonical zero-padded bucket. The shared decryptor authenticates with X25519, HKDF-SHA-256 and
AES-256-GCM. Both HKDF info and AES-GCM additional authenticated data are the UTF-8 bytes
of `slipgates feedback envelope v1`, followed directly by the ASCII key ID and the 16 raw
bundle ID bytes. This matches the #1068 client at
`69030152bb1bc7a530981f7fd821269819568da2`. It leaves all decrypted client v1 bundles unclassified and private until
their public classification vocabulary has been reviewed. The key provider must keep
keys outside the repository, report an unknown key ID as an envelope failure, and report
temporarily unavailable keys as transient failures.

`AzureFeedbackBlobReader` accepts a container client supplied by the host, lists and reads
only, and bounds downloads to the relay's 8 MiB limit. `FeedbackBlobSource` filters
completed blob names through `IFeedbackProcessedReceipts` instead of deleting blobs.
Receipts must be durable and idempotent, private, and scoped to the container; write one
only after private and public work succeeds. Replays before the receipt must remain safe
through the private and public stores' idempotent upserts. No receipt store is wired yet.

This slice is tested with fakes only. It does not configure a real private repository,
key provider, storage identity or schedule deployment. Those adapters must be wired with
least-privilege identities and secrets held outside the repository before live feedback is
processed. The intended private issue destination is `danswett/slipgates-feedback`; its
privacy must still be confirmed at runtime before processing.

The shared `AgentBridge.FeedbackEnvelope` project provides v1 `Seal`, `Parse` and `Open`
for Windows senders and the inbox. `Seal` takes nonempty plaintext, a validated key ID, a
32-byte X25519 public key and a 16-byte bundle ID; it generates an ephemeral key, salt
and nonce. `Open` takes the envelope and the 32-byte recipient private key and returns
plaintext bytes. Callers must clear returned plaintext and private-key buffers when finished.
The key ID and bundle ID are authenticated alongside the label
`slipgates feedback envelope v1`; their concatenation is both HKDF info and AES-GCM AAD.
The envelope uses 4 KiB-to-8 MiB canonical zero-padded buckets and matches the client v1
format. This library does not select or persist keys, send feedback, or enable the inbox.
`FeedbackEnvelopeSharedTests.Synthetic_vectors_interoperate_with_independent_webcrypto`
exercises both directions across bucket boundaries with freshly generated, synthetic
keys; no fixture private key is checked in. Run the process-category test explicitly
when validating interoperability, since the local fast ring excludes process tests.

`FeedbackGitHubPrivateStore` is a private-issue adapter for the fixed repository
`danswett/slipgates-feedback`. It confirms that exact repository is private before each
write and does not follow redirects. The token provider is supplied by the host; this
slice does not provision or store credentials. The GitHub identity must not have permission
to change repository visibility, and administrators must keep it private throughout
processing: GitHub's privacy check and issue write are separate requests, not an atomic
operation.

The adapter records each feedback bundle as bounded JSON chunks in comments on its private
group issue, with a completion marker written last. Retries reuse completed chunks; a
private first-seen timestamp marker keeps chunk content stable across retries, even when
the processor observes a different time. A completed bundle is not written twice even
when its receipt has not yet been saved.
Unclassified bundles use a separate issue keyed by a hash of the bundle name. This design
requires exactly one writer at a time; the deployment must enforce that invariant. Duplicate
group issues stop processing for manual reconciliation. The adapter does not implement a
receipt store, credential provider or scheduled worker and is tested only against fakes.
