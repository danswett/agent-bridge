# Beta feedback inbox

The inbox is a separate worker from the relay. The relay's feedback upload is write-only
and carries ciphertext; this worker reads the dedicated private feedback container using
its own identity. It never uses a relay read URL or the relay's identity.

The first slice defines the processing boundary:

- `IEncryptedFeedbackBundleSource` lists and reads bounded encrypted bundles, then removes
  them only after the private record and any public update have succeeded.
- `IFeedbackEnvelopeDecryptor` and `IFeedbackPrivateKeyProvider` isolate the envelope and
  key-vault implementation. The client-owned #738 envelope format is not guessed here.
- `IFeedbackPrivateStore` is the only destination for decrypted content. It must upsert
  idempotently by bundle name, group by fingerprint, and count distinct opaque tester
  keys. Tester keys remain private; the public count is `unknown` until a stable key is
  supplied by the envelope.
- `IPublicFeedbackIssueStore` receives a separate, validated type that has no free text,
  logs, screenshots, paths, names, session content or decrypted payload. It owns durable
  hourly creation limiting and fingerprint-based create-or-update behavior.

Public error and stage values must match the explicit `PublicErrorTypes` and `PublicStages`
allowlists. Both default to empty, so an unrecognized code stays private until the #738
envelope vocabulary is reviewed and configured. Public versions accept only numeric
components with an optional `alpha`, `beta` or `preview` channel and numeric suffix.

Public titles and labels follow #740: `[beta-feedback] <error type> at <stage> (<fp8>)`,
`beta-feedback`, and `fb-kind:<error-type>`. The body is a deterministic rendering of
allowlisted error type and stage, versions, OS, architecture, known tester count (or
`unknown`) and the fingerprint. The worker is disabled unless explicitly enabled;
`Enabled` is its kill switch, and the default public creation cap is ten per hour.
Adapters must translate retryable I/O failures to `FeedbackInboxTransientException`; the
worker logs them and retries on the next poll without discarding pending bundles.

This core slice is tested with fakes only. It does not configure a real private repository,
Key Vault, storage identity or schedule deployment. Those adapters must be wired with
least-privilege identities and secrets held outside the repository before live feedback is
processed.
