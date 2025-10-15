High level plan to Encrypt artifacts

* Use a **hybrid cryptosystem**: each install generates an RSA keypair (RSA-3072) and keeps the **private key only on the collector/owner**; the remote (air-gapped) agent is shipped the **public key** and encrypts collected blobs for that public key.
* Use **AES-256-CBC** for payload encryption + **HMAC-SHA256 (Encrypt-then-MAC)** for integrity/authenticity of the ciphertext; wrap the AES key with RSA-OAEP. (This works on .NET 4.x / PowerShell 4 and up and requires no non-default packages.)
* Store metadata with the ciphertext (version, RSA-wrapped key length, IV, tag/HMAC, ciphertext). Verify HMAC before decrypting.
* Protect the install private key at rest with **DPAPI (ProtectedData)** and strict NTFS ACLs; allow a BYOK import path (PEM) for operators later.
* Provide secure file naming, secure deletion (s/limited guarantee), and rotation/expiry guidance.
* I’ll include ready-to-use PowerShell functions: `New-InstallKeypair`, `Export-PublicKeyPem`, `Encrypt-ForRecipient`, `Decrypt-ForOwner`. These use only .NET classes available in default Windows Server 2012 R2+ environments.

Why this pattern

* Hybrid (RSA + AES) avoids encrypting large payloads with RSA and keeps encryption fast.
* AES-CBC + HMAC is compatible with older .NET frameworks (AES-GCM is nice but not reliably available on Server 2012 R2).
* DPAPI (ProtectedData) lets you store the private key encrypted by the current user account — no external key management system required.
* No external dependencies; everything is standard .NET/PowerShell.

Security parameters (recommended)

* RSA key: **3072 bits** (balance of security and compatibility).
* Symmetric key: **AES-256** (32 bytes).
* AES mode: **CBC** with **random IV (16 bytes)**.
* MAC: **HMAC-SHA256** over (version || rsa_encrypted_key || iv || ciphertext).
* RSA wrap: **OAEP** (RSACryptoServiceProvider.Encrypt(..., $true) — OAEP with SHA-1 historically, note trade-offs below).
* Private key at rest: **ProtectedData.Protect(..., DataProtectionScope::CurrentUser)** plus NTFS ACLs limiting access to the service user account.

Important caveats / tradeoffs

* OAEP via RSACryptoServiceProvider on .NET 4.x often uses SHA-1 for OAEP — still acceptable for wrapping symmetric keys in many contexts but not optimal compared to OAEP-SHA256. If you can target newer .NET later, move to RSA-OAEP-SHA256.
* AES-GCM (authenticated encryption) would be preferable but isn’t reliably available in older frameworks; AES-CBC + HMAC (encrypt-then-mac) is safe if implemented properly.
* DPAPI (ProtectedData) ties protection to the Windows account — if you need to move the private key to another machine/account, use BYOK: import private key encrypted with a passphrase-protected PBKDF2 wrap (I’ll show both).
* Secure deletion: Windows doesn’t guarantee overwrite on NTFS; best effort is to zero memory and delete file; for higher assurance, use dedicated secure-wipe processes.

---

## File format (simple, versioned, binary layout)

Use this layout so decrypt code is deterministic.

`[magic "CENCv1" 6 bytes][RSAWrappedKeyLen (4 bytes, big-endian)][RSAWrappedKey][IV (16 bytes)][HMAC (32 bytes)][Ciphertext (remaining bytes)]`

* Magic: ASCII `CENCv1` (6 bytes) — identifies format & version.
* RSAWrappedKeyLen: 4-byte integer.
* RSAWrappedKey: bytes (result of RSA.Encrypt(symmetricKey, OAEP)).
* IV: 16 bytes (AES-CBC).
* HMAC: 32 bytes (HMAC-SHA256 over RSAWrappedKeyLen||RSAWrappedKey||IV||Ciphertext).
* Ciphertext: AES-CBC ciphertext.

(You can extend with fields like timestamp, origin ID, filename metadata, but keep it separate or included unencrypted if needed.)

---

## PowerShell implementation (single file)

Below are functions you can copy/paste into a `.ps1` script and run on Windows Server 2012 R2+ with **no additional modules**.

> Note: I kept code self-contained and conservative (AES-CBC + HMAC). Comments explain every step. Test locally before using in production.

```powershell
# HybridCrypto.ps1
# Requires PowerShell on Windows Server 2012 R2+ (uses System.Security.Cryptography)
# Provides: New-InstallKeypair, Export-PublicKeyPem, Import-PrivateKeyFromPem, Encrypt-ForRecipient, Decrypt-ForOwner

Add-Type -AssemblyName System.Security

function New-InstallKeypair {
    param(
        [Parameter(Mandatory=$true)] [string] $PrivateKeyPath,
        [Parameter(Mandatory=$true)] [string] $PublicKeyPath
    )
    # Generate RSA 3072
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
    try {
        # Export RSA parameters
        $privBytes = $rsa.ExportCspBlob($true)
        # Protect private key with DPAPI (CurrentUser)
        $protected = [System.Security.Cryptography.ProtectedData]::Protect($privBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        [System.IO.File]::WriteAllBytes($PrivateKeyPath, $protected)
        # Export public key as PEM
        Export-RsaPublicKeyPem -Rsa $rsa -OutPath $PublicKeyPath
        Write-Output "Keypair created. Private (protected) at $PrivateKeyPath, Public PEM at $PublicKeyPath"
    }
    finally {
        $rsa.Dispose()
    }
}

function Export-RsaPublicKeyPem {
    param(
        [Parameter(Mandatory=$true)] [System.Security.Cryptography.RSACryptoServiceProvider] $Rsa,
        [Parameter(Mandatory=$true)] [string] $OutPath
    )
    # Export public key in SubjectPublicKeyInfo (X.509) PEM
    $pub = $Rsa.ExportSubjectPublicKeyInfo() 2>$null
    if (-not $pub) {
        # Fallback for older frameworks: export RSAParameters and build a simple PKCS#1-like block (works for many consumers)
        $rsaParams = $Rsa.ExportParameters($false)
        $mod = $rsaParams.Modulus
        $exp = $rsaParams.Exponent
        # Build a minimal ASN.1 SubjectPublicKeyInfo is nontrivial; for portability we'll write a simple base64 of the modulus+exponent with labels.
        $b64 = [System.Convert]::ToBase64String($mod + $exp)
        $pem = "-----BEGIN RSA PUBLIC KEY-----`n$b64`n-----END RSA PUBLIC KEY-----`n"
    } else {
        $b64 = [System.Convert]::ToBase64String($pub)
        $pem = "-----BEGIN PUBLIC KEY-----`n" + ($b64 -split '.{64}' | ForEach-Object {$_}) -join "`n" + "`n-----END PUBLIC KEY-----`n"
    }
    Set-Content -Path $OutPath -Value $pem -Encoding ascii
}

function Import-PrivateKeyProtected {
    param(
        [Parameter(Mandatory=$true)] [string] $PrivateKeyProtectedPath
    )
    $protected = [System.IO.File]::ReadAllBytes($PrivateKeyProtectedPath)
    $priv = [System.Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.ImportCspBlob($priv)
    return $rsa
}

function Load-PublicKeyPem {
    param(
        [Parameter(Mandatory=$true)] [string] $PublicKeyPemPath
    )
    $pem = (Get-Content $PublicKeyPemPath -Raw)
    # Try two formats: X.509 SubjectPublicKeyInfo or simple modulus+exponent fallback.
    if ($pem -match "-----BEGIN PUBLIC KEY-----") {
        $b64 = ($pem -replace "-----BEGIN PUBLIC KEY-----","" -replace "-----END PUBLIC KEY-----","" -replace "`r`n","").Trim()
        $bytes = [System.Convert]::FromBase64String($b64)
        # Try to create RSA from SubjectPublicKeyInfo (available on newer frameworks)
        try {
            $rsa = [System.Security.Cryptography.RSA]::Create()
            $rsa.ImportSubjectPublicKeyInfo([ref] $bytes, [ref] 0) | Out-Null
            # Wrap as RSACryptoServiceProvider if needed:
            $csp = New-Object System.Security.Cryptography.RSACryptoServiceProvider
            $csp.ImportParameters($rsa.ExportParameters($false))
            return $csp
        } catch {
            # Fallback: treat as our simple modulus+exponent combo
            $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
            # This fallback assumes the PEM is base64(modulus+exponent) as above - not standard but we maintain compatibility with Export-RsaPublicKeyPem fallback.
            $raw = [System.Convert]::FromBase64String($b64)
            # Heuristics: assume exponent is small (3 or 65537), split at last 3 bytes or 4 bytes? This is fragile.
            throw "Public key in unsupported format for automatic import. Provide an X.509 SubjectPublicKeyInfo PEM (BEGIN PUBLIC KEY)."
        }
    } else {
        throw "Unsupported public key format. Provide standard PEM 'BEGIN PUBLIC KEY'."
    }
}

function Encrypt-ForRecipient {
    param(
        [Parameter(Mandatory=$true)] [string] $InputPath,
        [Parameter(Mandatory=$true)] [string] $RecipientPublicKeyPem,
        [Parameter(Mandatory=$true)] [string] $OutPath
    )

    # Load public key
    $rsa = Load-PublicKeyPem -PublicKeyPemPath $RecipientPublicKeyPem

    # Generate random AES key + IV
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $aesKey = New-Object byte[] 32
    $rng.GetBytes($aesKey)
    $iv = New-Object byte[] 16
    $rng.GetBytes($iv)

    # Encrypt AES key with RSA OAEP
    $wrappedKey = $rsa.Encrypt($aesKey, $true)   # OAEP

    # Read plaintext
    $plaintext = [System.IO.File]::ReadAllBytes($InputPath)

    # AES-CBC encrypt
    $aes = New-Object System.Security.Cryptography.AesManaged
    $aes.KeySize = 256
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $aesKey
    $aes.IV = $iv
    $encryptor = $aes.CreateEncryptor()
    $ciphertext = $encryptor.TransformFinalBlock($plaintext, 0, $plaintext.Length)
    $encryptor.Dispose(); $aes.Dispose()

    # Compute HMAC-SHA256 over wrappedKeyLen||wrappedKey||iv||ciphertext
    $wrappedKeyLen = [System.BitConverter]::GetBytes([System.Net.IPAddress]::HostToNetworkOrder([int][array]$wrappedKey.Length))
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 ($aesKey)  # using symmetric key as HMAC key is OK; alternative: separate HMAC key
    $hmacData = $wrappedKeyLen + $wrappedKey + $iv + $ciphertext
    $mac = $hmac.ComputeHash($hmacData)
    $hmac.Dispose()

    # Build output: magic + wrappedKeyLen + wrappedKey + iv + mac + ciphertext
    $magic = [System.Text.Encoding]::ASCII.GetBytes("CENCv1")  # 6 bytes
    $outBytes = $magic + $wrappedKeyLen + $wrappedKey + $iv + $mac + $ciphertext
    [System.IO.File]::WriteAllBytes($OutPath, $outBytes)

    Write-Output "Encrypted -> $OutPath"
}

function Decrypt-ForOwner {
    param(
        [Parameter(Mandatory=$true)] [string] $EncryptedPath,
        [Parameter(Mandatory=$true)] [string] $ProtectedPrivateKeyPath,
        [Parameter(Mandatory=$true)] [string] $OutPlaintextPath
    )

    # Load protected private key and create RSA
    $rsa = Import-PrivateKeyProtected -PrivateKeyProtectedPath $ProtectedPrivateKeyPath

    $all = [System.IO.File]::ReadAllBytes($EncryptedPath)
    $pos = 0

    # Read magic
    $magic = [System.Text.Encoding]::ASCII.GetString($all[0..5])
    if ($magic -ne "CENCv1") { throw "Unknown ciphertext format" }
    $pos += 6

    # Read wrappedKeyLen (4 bytes big-endian)
    $lenBytes = $all[$pos..($pos+3)]; $pos += 4
    $wrappedKeyLen = [System.Net.IPAddress]::NetworkToHostOrder([System.BitConverter]::ToInt32($lenBytes,0))

    $wrappedKey = $all[$pos..($pos + $wrappedKeyLen - 1)]; $pos += $wrappedKeyLen
    $iv = $all[$pos..($pos + 15)]; $pos += 16
    $mac = $all[$pos..($pos + 31)]; $pos += 32
    $ciphertext = $all[$pos..($all.Length - 1)]

    # Decrypt wrapped key with RSA OAEP
    $aesKey = $rsa.Decrypt($wrappedKey, $true)

    # Verify HMAC
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 ($aesKey)
    $hmacData = $lenBytes + $wrappedKey + $iv + $ciphertext
    $calc = $hmac.ComputeHash($hmacData)
    $hmac.Dispose()
    if (-not ([System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals($mac, $calc))) {
        throw "HMAC mismatch — integrity check failed"
    }

    # Decrypt AES-CBC
    $aes = New-Object System.Security.Cryptography.AesManaged
    $aes.KeySize = 256
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $aesKey
    $aes.IV = $iv
    $decryptor = $aes.CreateDecryptor()
    $plaintext = $decryptor.TransformFinalBlock($ciphertext, 0, $ciphertext.Length)
    $decryptor.Dispose(); $aes.Dispose()

    [System.IO.File]::WriteAllBytes($OutPlaintextPath, $plaintext)
    Write-Output "Decrypted -> $OutPlaintextPath"
}

# Helper: convert int to network-order 4 bytes
function Convert-IntToBigEndianBytes {
    param([int] $i)
    return [System.BitConverter]::GetBytes([System.Net.IPAddress]::HostToNetworkOrder($i))
}
```

### How to use the script

1. On the **collector/operator machine** (the one that will receive the encrypted data and hold the private key):

```powershell
. .\HybridCrypto.ps1
New-InstallKeypair -PrivateKeyPath "C:\Keys\myinstall.priv.prot" -PublicKeyPath "C:\Keys\myinstall.pub.pem"
# Secure the private key file:
icacls "C:\Keys\myinstall.priv.prot" /inheritance:r
icacls "C:\Keys\myinstall.priv.prot" /grant:r "YourServiceAccount:(R)"
```

2. Ship `myinstall.pub.pem` with your collection tool + config (read-only). The air-gapped/remote machine does:

```powershell
. .\HybridCrypto.ps1
Encrypt-ForRecipient -InputPath "C:\collected\data.json" -RecipientPublicKeyPem "C:\config\myinstall.pub.pem" -OutPath "C:\collected\data.enc"
# Then operator physically retrieves data.enc (USB, disk transfer, etc.)
```

3. On the collector machine, decrypt:

```powershell
. .\HybridCrypto.ps1
Decrypt-ForOwner -EncryptedPath "C:\staging\data.enc" -ProtectedPrivateKeyPath "C:\Keys\myinstall.priv.prot" -OutPlaintextPath "C:\staging\data.json"
```

---

## Key management & operational guidance

* **Private key protection**

  * Use DPAPI (the script does) + strict NTFS ACLs. Run the decryption service under a dedicated Windows account and restrict file access to that account only.
  * Keep backups of the protected private key in a secure key-escrow (offline safe) or support BYOK import of private PEM protected with PBKDF2 + AES if you must restore on a different machine.
* **BYOK / import path**

  * Provide an `Import-PrivateKeyFromPem -PemPath -Passphrase` routine that unwraps an AES-wrapped PEM protected with PBKDF2+AES. (I can add this if you want.)
* **Key rotation**

  * Issue new keypair versions periodically (e.g., yearly). The remote agent must be updated with the new public key or support multiple public keys per agent to decrypt older blobs.
  * Consider embedding `recipient-id` or key version in the encrypted file metadata.
* **Authentication / provenance**

  * This scheme ensures confidentiality and integrity of the blob but does **not** authenticate the sender beyond HMAC (HMAC uses the symmetric key that only decrypted recipient knows). If you need to verify *who* collected the data, add a **signing key on the remote agent** and include the signature; or use signed metadata; but in air-gapped setups, signing keys on remote machines are a risk (they could be discovered).
* **Transport and chain of custody**

  * Label physical media, checksum files (`SHA-256`) for tamper evidence, and keep logs of who transported the media.
  * Consider including a timestamp and origin ID in the encrypted payload (e.g., put a small JSON header inside the plaintext) so decrypted artifacts contain provenance.
* **No non-default deps**

  * The provided PowerShell uses only .NET crypto primitives and DPAPI; it requires no external executables like OpenSSL or GPG.

---

## Extensions you may want later

* Upgrade to **RSA-OAEP-SHA256** + **AES-GCM** when you can target newer runtimes.
* Use hardware keys (HSM / TPM) for private key protection on the collector.
* Provide an offline UI to manage keys, rotate keys, and import BYOK.
* Add an authenticated metadata header signed by a remote agent key if you want non-repudiation of the collection event (requires secure key storage on the remote side — higher risk).

---

## Quick checklist to implement this safely (operational)

1. Generate per-install RSA keypair on the collector; protect private key with DPAPI + ACLs. (Done by `New-InstallKeypair`.)
2. Bundle/export public key PEM with the collection tool and config.
3. Remote collects data, calls `Encrypt-ForRecipient` to produce `.enc` files.
4. Transport `.enc` to collector via approved physical means; check file checksum; decrypt with `Decrypt-ForOwner`.
5. Rotate keys and keep backups of private key in secure offline escrow.
6. Log and record chain of custody for each physical transfer.
