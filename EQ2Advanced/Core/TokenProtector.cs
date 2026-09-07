using System;
using System.Security.Cryptography;
using System.Text;

namespace EQ2Advanced.Core
{
    /// <summary>
    /// Wraps the device token at rest with Windows DPAPI (CurrentUser scope) so
    /// the config file on disk is no longer a bare credential — the ciphertext
    /// only decrypts under the same Windows account that wrote it, which is
    /// exactly the info-stealer-malware and "pasted my config in a support
    /// thread" exposure a plaintext token had. It is still not a password: the
    /// token itself is scoped to sending log lines and is revocable from the
    /// site, and anyone with an interactive session as this Windows user could
    /// still ask the plugin for it. DPAPI raises the bar against exfiltration,
    /// it does not claim to defeat a fully compromised session.
    /// </summary>
    internal static class TokenProtector
    {
        private const string Prefix = "dpapi:v1:";

        /// <summary>Encrypt for storage. `ok` is false when DPAPI itself threw —
        /// Wine/Proton, a broken or roaming Windows profile, and some antivirus/
        /// EDR products can all block it — and the plaintext had to be written
        /// instead. A save can still never FAIL because of this (the token is
        /// always written, one way or the other); `ok` exists so the caller can
        /// tell the user their token is sitting on disk unprotected, rather than
        /// that going unnoticed the way it used to.</summary>
        public static string Protect(string plain, out bool ok)
        {
            ok = true;
            if (string.IsNullOrEmpty(plain)) return plain ?? "";
            try
            {
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(bytes);
            }
            catch { ok = false; return plain; }
        }

        /// <summary>Whether a value as stored on disk is DPAPI ciphertext (or
        /// empty — nothing to protect). False means it is a bare token, either a
        /// pre-DPAPI config or one <see cref="Protect"/> failed to encrypt.</summary>
        public static bool IsProtected(string stored) =>
            string.IsNullOrEmpty(stored) || stored.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Decrypt what Load() read off disk. A value with no prefix is
        /// an existing plaintext token from before this shipped, or DPAPI itself
        /// failing over — passed through so pairing keeps working; the next Save
        /// re-encrypts it. A prefixed value that fails to unprotect (moved to a
        /// different Windows account, corrupted) comes back empty rather than as
        /// ciphertext, so a bad token reads as unpaired instead of being sent.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored ?? "";
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored.Substring(Prefix.Length)),
                    null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
    }
}
