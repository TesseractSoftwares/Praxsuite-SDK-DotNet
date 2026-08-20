using System;
using System.IO;
using System.Text;

namespace Praxsuite
{
    /// <summary>
    /// Keeps the session in memory only. The default, and the right one for a web service or a
    /// short-lived process: nothing touches disk, and nothing outlives the process.
    /// </summary>
    public class PraxMemoryTokenStore : IPraxTokenStore
    {
        private PraxSession _session;

        public PraxSession Load() => _session;
        public void Save(PraxSession session) => _session = session;
        public void Clear() => _session = null;
    }

    /// <summary>
    /// Persists the session to a file, encrypted at rest with a key you supply.
    ///
    /// Only useful for a process that should stay signed in across restarts - a desktop app, a
    /// CLI, a game client. A server holding one credential does not need it, and a server
    /// holding many users' sessions should not be using this SDK's session store at all: each
    /// request should carry its own token.
    ///
    /// You supply the passphrase because there is no sound way for a library to invent one. A
    /// key derived from machine identifiers only obscures the file from a casual reader, and
    /// pretending otherwise would be worse than being explicit. Use the OS keychain, DPAPI, or
    /// a value the user types.
    ///
    /// On .NET Standard 2.1 this uses AES-256-CBC with a random salt and IV per write, and
    /// PBKDF2-SHA256 for derivation. It is not authenticated encryption: it protects
    /// confidentiality at rest, not integrity against an attacker who can rewrite the file. If
    /// you need that, implement <see cref="IPraxTokenStore"/> over your platform's secure store.
    /// </summary>
    public class PraxEncryptedFileTokenStore : IPraxTokenStore
    {
        private const int SaltSize = 16;
        private const int IvSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 100_000;
        private const byte FormatVersion = 1;

        private readonly string _path;
        private readonly string _passphrase;
        private PraxSession _cached;
        private bool _loaded;

        /// <param name="path">Where to write. The directory is created if missing.</param>
        /// <param name="passphrase">
        /// Encryption passphrase. Must not be null or empty - an unencrypted session file
        /// containing a refresh token is not something this class will produce silently.
        /// </param>
        public PraxEncryptedFileTokenStore(string path, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A file path is required.", nameof(path));
            if (string.IsNullOrEmpty(passphrase))
                throw new ArgumentException(
                    "A passphrase is required. Writing a refresh token to disk in the clear is " +
                    "not a default this class will pick for you - supply a passphrase from your " +
                    "platform's secure store, or use PraxMemoryTokenStore.", nameof(passphrase));

            _path = path;
            _passphrase = passphrase;
        }

        public PraxSession Load()
        {
            if (_loaded) return _cached;
            _loaded = true;

            try
            {
                if (!File.Exists(_path)) return null;

                var blob = File.ReadAllBytes(_path);
                if (blob.Length < 1 + SaltSize + IvSize + 1) return null;
                if (blob[0] != FormatVersion)
                {
                    PraxLog.Info("Stored session uses an older format; discarding it.");
                    Clear();
                    return null;
                }

                var salt = new byte[SaltSize];
                var iv = new byte[IvSize];
                Buffer.BlockCopy(blob, 1, salt, 0, SaltSize);
                Buffer.BlockCopy(blob, 1 + SaltSize, iv, 0, IvSize);

                var cipherLength = blob.Length - 1 - SaltSize - IvSize;
                var cipher = new byte[cipherLength];
                Buffer.BlockCopy(blob, 1 + SaltSize + IvSize, cipher, 0, cipherLength);

                var json = Decrypt(cipher, DeriveKey(salt), iv);
                _cached = PraxSessionSerializer.FromJson(json);
                return _cached;
            }
            catch (Exception ex)
            {
                // A session that will not decrypt is not worth surfacing: a changed passphrase or
                // a truncated file. Fail closed and let the user sign in again.
                PraxLog.Info("Could not read the stored session (" + ex.GetType().Name +
                             "); signing in again will be required.");
                TryDelete();
                return null;
            }
        }

        public void Save(PraxSession session)
        {
            _cached = session;
            _loaded = true;

            if (session == null) { Clear(); return; }

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var salt = RandomBytes(SaltSize);
                var iv = RandomBytes(IvSize);
                var cipher = Encrypt(PraxSessionSerializer.ToJson(session), DeriveKey(salt), iv);

                var blob = new byte[1 + SaltSize + IvSize + cipher.Length];
                blob[0] = FormatVersion;
                Buffer.BlockCopy(salt, 0, blob, 1, SaltSize);
                Buffer.BlockCopy(iv, 0, blob, 1 + SaltSize, IvSize);
                Buffer.BlockCopy(cipher, 0, blob, 1 + SaltSize + IvSize, cipher.Length);

                // Write then move, so a crash mid-write cannot leave a truncated session file.
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, blob);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                // Losing persistence degrades to memory-only, which is safe. Do not throw out of
                // a successful login because the disk refused us.
                PraxLog.Warn("Could not persist the session: " + ex.Message +
                             ". It will stay in memory for this process only.");
            }
        }

        public void Clear()
        {
            _cached = null;
            _loaded = true;
            TryDelete();
        }

        private void TryDelete()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                PraxLog.Warn("Could not delete the stored session file: " + ex.Message);
            }
        }

        private byte[] DeriveKey(byte[] salt)
        {
            using (var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(
                       _passphrase, salt, Iterations,
                       System.Security.Cryptography.HashAlgorithmName.SHA256))
                return kdf.GetBytes(KeySize);
        }

        private static byte[] Encrypt(string plaintext, byte[] key, byte[] iv)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor())
                {
                    var bytes = Encoding.UTF8.GetBytes(plaintext);
                    return encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
                }
            }
        }

        private static string Decrypt(byte[] cipher, byte[] key, byte[] iv)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
            }
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }
    }

    /// <summary>
    /// Serialises a session without pulling in a JSON dependency - the SDK's own codec does it.
    /// Unity used JsonUtility here; there is no equivalent in plain .NET.
    /// </summary>
    internal static class PraxSessionSerializer
    {
        internal static string ToJson(PraxSession s)
        {
            return PraxJson.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["accessToken"] = s.accessToken,
                ["refreshToken"] = s.refreshToken,
                ["accessExpiresAtUnix"] = s.accessExpiresAtUnix,
                ["refreshExpiresAtUnix"] = s.refreshExpiresAtUnix,
                ["endUserId"] = s.endUserId,
                ["email"] = s.email,
                ["username"] = s.username,
                ["firstName"] = s.firstName,
                ["lastName"] = s.lastName,
                ["roles"] = s.roles,
            });
        }

        internal static PraxSession FromJson(string json)
        {
            var map = PraxJson.ParseObject(json);
            string Str(string key) => map.TryGetValue(key, out var v) ? v as string : null;
            long Num(string key) =>
                map.TryGetValue(key, out var v) && v != null
                    ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;

            var roles = Array.Empty<string>();
            if (map.TryGetValue("roles", out var rolesNode) && rolesNode is System.Collections.Generic.List<object> list)
            {
                roles = new string[list.Count];
                for (var i = 0; i < list.Count; i++) roles[i] = Convert.ToString(list[i]);
            }

            return new PraxSession
            {
                accessToken = Str("accessToken"),
                refreshToken = Str("refreshToken"),
                accessExpiresAtUnix = Num("accessExpiresAtUnix"),
                refreshExpiresAtUnix = Num("refreshExpiresAtUnix"),
                endUserId = Str("endUserId"),
                email = Str("email"),
                username = Str("username"),
                firstName = Str("firstName"),
                lastName = Str("lastName"),
                roles = roles,
            };
        }
    }
}
