# Offline encryption keyset samples

The snippets below provide anonymised DPAPI/AES configuration samples for the
offline runner. Replace the placeholder values with environment-specific keys
before using them.

## Base64 keyset template

```json
{
  "schema": "https://driftbuster.dev/offline-runner/encryption/keyset/v1",
  "aes_key": {
    "encoding": "base64",
    "data": "toMNnCNORi1NulOpVJlMaTS+gGXpEAo0fNRZNaZfrIE="
  },
  "hmac_key": {
    "encoding": "base64",
    "data": "WaTdMJWpiVF3l4E56D2bufJPRjfE8swZo8dJQinVHaE="
  }
}
```

Generate replacements with `os.urandom(32)` (see `docs/encryption.md`). Store
the file with ACLs restricting access to the operator account.

## DPAPI-wrapped AES key example

```json
{
  "schema": "https://driftbuster.dev/offline-runner/encryption/keyset/v1",
  "aes_key": {
    "encoding": "dpapi",
    "scope": "current_user",
    "data": "BASE64_DPAPI_AES_BLOB"
  },
  "hmac_key": {
    "encoding": "base64",
    "data": "WaTdMJWpiVF3l4E56D2bufJPRjfE8swZo8dJQinVHaE="
  }
}
```

Create the DPAPI blob by calling `ProtectedData::Protect` on the AES key bytes
from a Windows session tied to the operator account. Store the plaintext AES key
securely until the DPAPI-wrapped blob is written to the keyset file, then remove
the plaintext copy.

## Runner configuration stub

```jsonc
{
  "runner": {
    "compress": true,
    "encryption": {
      "enabled": true,
      "mode": "dpapi-aes",
      "keyset_path": "C:/secure/keysets/aes-hmac.json",
      "output_extension": ".enc",
      "remove_plaintext": true
    }
  }
}
```

Keep the keyset path outside the collection directory so encrypted packages can
be shared without leaking key material.
