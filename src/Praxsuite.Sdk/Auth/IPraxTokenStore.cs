using System;

namespace Praxsuite
{
    /// <summary>
    /// A player's signed-in session as held by the SDK.
    /// </summary>
    [Serializable]
    public class PraxSession
    {
        /// <summary>Short-lived gateway JWT. Sent as Authorization: Bearer on data calls.</summary>
        public string accessToken;

        /// <summary>Long-lived, single-use token. The gateway rotates it on every refresh.</summary>
        public string refreshToken;

        /// <summary>Access token expiry, UTC. Zero when the gateway did not report one.</summary>
        public long accessExpiresAtUnix;

        /// <summary>Refresh token expiry, UTC. Zero when the gateway did not report one.</summary>
        public long refreshExpiresAtUnix;

        public string endUserId;
        public string email;
        public string username;
        public string firstName;
        public string lastName;
        public string[] roles = Array.Empty<string>();

        public DateTimeOffset AccessExpiresAt =>
            accessExpiresAtUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(accessExpiresAtUnix)
                : DateTimeOffset.MinValue;

        public bool HasAccessToken => !string.IsNullOrEmpty(accessToken);
        public bool HasRefreshToken => !string.IsNullOrEmpty(refreshToken);

        /// <summary>
        /// True when the access token is expired or within <paramref name="leadSeconds"/> of it.
        /// An unknown expiry is treated as "not stale" - the server is the authority, and a
        /// 401 triggers a refresh anyway.
        /// </summary>
        public bool IsAccessStale(int leadSeconds)
        {
            if (accessExpiresAtUnix <= 0) return false;
            return DateTimeOffset.UtcNow.AddSeconds(leadSeconds) >= AccessExpiresAt;
        }

        public bool IsRefreshExpired()
        {
            if (refreshExpiresAtUnix <= 0) return false;
            return DateTimeOffset.UtcNow >= DateTimeOffset.FromUnixTimeSeconds(refreshExpiresAtUnix);
        }
    }

    /// <summary>
    /// Where a session lives between calls, and optionally between app runs.
    ///
    /// Implement this to move sessions somewhere your platform trusts more - the iOS
    /// Keychain, the Android Keystore, or a console's save API. The SDK never touches
    /// PlayerPrefs: on desktop that is a plaintext registry key or file that any other
    /// process on the machine can read.
    /// </summary>
    public interface IPraxTokenStore
    {
        PraxSession Load();
        void Save(PraxSession session);
        void Clear();
    }
}
