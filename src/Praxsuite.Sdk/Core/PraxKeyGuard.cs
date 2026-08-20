using System;

namespace Praxsuite
{
    /// <summary>
    /// Classifies Praxsuite credentials and refuses to let a secret key reach client code.
    ///
    /// Praxsuite issues two kinds of gateway credential:
    ///
    ///   pk_live_...  Publishable key. It identifies the workspace, carries only the scopes an
    ///                administrator granted it, and is safe to embed in a game client - the
    ///                same idea as a publishable API key in any payment SDK.
    ///
    ///   sk_live_...  Secret key. Full credential scope. A player who extracts this from a
    ///                shipped build can read and write every table the key can reach.
    ///                It must never exist inside a client build.
    ///
    /// Every client-side entry point runs <see cref="RequireClientSafe"/>. The check is
    /// deliberately loud and unconditional - there is no opt-out flag, because every
    /// "just for testing" opt-out eventually ships. The editor-side build guard
    /// (Praxsuite.Editor/PraxBuildGuard) enforces the same rule at build time so the
    /// mistake is caught before a binary exists, not after players find it.
    /// </summary>
    public static class PraxKeyGuard
    {
        public const string PublishablePrefix = "pk_live_";
        public const string SecretPrefix = "sk_live_";

        public enum KeyKind
        {
            /// <summary>Empty or unrecognised.</summary>
            Unknown,
            /// <summary>pk_live_ - safe to ship in a client.</summary>
            Publishable,
            /// <summary>sk_live_ - server only.</summary>
            Secret,
            /// <summary>A gateway-issued end user JWT (three dot-separated segments).</summary>
            EndUserJwt
        }

        public static KeyKind Classify(string credential)
        {
            if (string.IsNullOrEmpty(credential)) return KeyKind.Unknown;

            if (credential.StartsWith(SecretPrefix, StringComparison.Ordinal)) return KeyKind.Secret;
            if (credential.StartsWith(PublishablePrefix, StringComparison.Ordinal)) return KeyKind.Publishable;

            // A JWT is header.payload.signature - exactly two dots, no whitespace.
            var dots = 0;
            foreach (var c in credential)
            {
                if (c == '.') dots++;
                else if (char.IsWhiteSpace(c)) return KeyKind.Unknown;
            }
            return dots == 2 ? KeyKind.EndUserJwt : KeyKind.Unknown;
        }

        /// <summary>
        /// Throws if <paramref name="credential"/> must not be used from client code.
        /// Call this at every boundary that accepts a caller-supplied credential.
        /// </summary>
        public static void RequireClientSafe(string credential, string context)
        {
            if (Classify(credential) != KeyKind.Secret) return;

            throw new PraxSecurityException(
                "Refusing to use a secret key (" + SecretPrefix + "...) from client code" +
                (string.IsNullOrEmpty(context) ? "" : " in " + context) + ".\n" +
                "\n" +
                "A secret key placed in a Unity project ships inside the build and can be\n" +
                "extracted from it in minutes. Anyone who does so gains that key's full access\n" +
                "to your workspace data.\n" +
                "\n" +
                "Use a publishable key (" + PublishablePrefix + "...) here instead, and give the\n" +
                "player their own identity with Prax.Auth.LoginAsync(). Row-level filters then\n" +
                "scope every read and write to that player, server-side.\n" +
                "\n" +
                "If you genuinely need secret-key access, it belongs in a dedicated server build:\n" +
                "define PRAXSUITE_SERVER, use Praxsuite.Server.PraxServer, and supply the key\n" +
                "through the PRAXSUITE_SECRET_KEY environment variable - never an asset file.\n" +
                "\n" +
                "See docs/security.md.");
        }

        /// <summary>
        /// Masks a credential for logs and error messages. Keeps enough to identify which
        /// key was used without disclosing it.
        /// </summary>
        public static string Redact(string credential)
        {
            if (string.IsNullOrEmpty(credential)) return "(none)";

            switch (Classify(credential))
            {
                case KeyKind.EndUserJwt:
                    return "(end user jwt)";
                case KeyKind.Publishable:
                case KeyKind.Secret:
                {
                    // Prefix is public information; the entropy after it is not.
                    var prefixLen = Math.Min(credential.Length, PublishablePrefix.Length + 4);
                    return credential.Substring(0, prefixLen) + "...";
                }
                default:
                    return "(redacted)";
            }
        }
    }

    /// <summary>
    /// Thrown when the SDK blocks an operation that would expose credentials or player data.
    /// These are programmer errors, not runtime conditions - do not catch and ignore them.
    /// </summary>
    public class PraxSecurityException : Exception
    {
        public PraxSecurityException(string message) : base(message) { }
    }
}
