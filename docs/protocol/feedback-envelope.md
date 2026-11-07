# Sealed feedback envelope v1

The client serializes the reviewed feedback bundle as UTF-8 JSON, then encrypts it to the
configured feedback recipient key. The relay receives and stores only the padded envelope; it
does not receive the plaintext, key, bundle fields, or a read URL.

The envelope is one byte sequence:

| Field | Size | Encoding |
| --- | ---: | --- |
| Magic | 4 | ASCII `SGBF` |
| Version | 1 | `1` |
| Key ID length | 1 | unsigned byte, 1–64 |
| Key ID | length above | ASCII |
| Bundle ID | 16 | random bytes |
| Ephemeral public key | 32 | raw X25519 |
| HKDF salt | 32 | random bytes |
| AES-GCM nonce | 12 | random bytes |
| Ciphertext length | 4 | unsigned big-endian; includes the 16-byte GCM tag |
| Ciphertext | length above | AES-256-GCM |
| Padding | to bucket | zero bytes |

The shared secret is X25519 between a fresh ephemeral private key and the recipient public key.
HKDF-SHA-256 derives the AES-256-GCM key using the random salt and
`UTF8("slipgates feedback envelope v1") || keyId || bundleId` as `info`. The exact same byte
sequence is AES-GCM additional authenticated data. The ephemeral public key, salt, nonce, and
ciphertext length are protected through key derivation or the authenticated encryption operation.

The stored length is the smallest blob bucket that contains the header and ciphertext: 4096 bytes
minimum, powers of two through 1 MiB, then whole MiB. Padding bytes must be zero. Parsers reject
unknown versions, malformed lengths, non-zero padding, and authentication failures. A parser
must not log or publish decrypted content.

The key ID and recipient public key are deployment configuration, never a private key in the
client. A deployment without both values keeps feedback sending disabled.
