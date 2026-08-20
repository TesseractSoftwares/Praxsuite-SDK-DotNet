using System;
using System.Collections.Generic;
using System.Globalization;

namespace Praxsuite
{
    /// <summary>The signed-in player's profile, as the gateway reports it.</summary>
    public class PraxUser
    {
        public string Id;
        public string Email;
        public string Username;
        public string FirstName;
        public string LastName;
        public IReadOnlyList<string> Roles = Array.Empty<string>();

        /// <summary>Username, then full name, then the email local part - whichever exists.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Username)) return Username;

                var full = ((FirstName ?? "") + " " + (LastName ?? "")).Trim();
                if (full.Length > 0) return full;

                if (!string.IsNullOrEmpty(Email))
                {
                    var at = Email.IndexOf('@');
                    return at > 0 ? Email.Substring(0, at) : Email;
                }
                return "Player";
            }
        }

        public bool HasRole(string role)
        {
            if (Roles == null || string.IsNullOrEmpty(role)) return false;
            foreach (var r in Roles)
                if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// Result of a sign-in or registration attempt.
    ///
    /// Registration does not always produce a session: when the workspace requires email
    /// confirmation the account is created but unusable until the player clicks the link, and
    /// <see cref="RequiresEmailConfirmation"/> is set with no tokens issued. Check it before
    /// assuming the player is in.
    /// </summary>
    public class PraxAuthResult
    {
        public bool IsSignedIn;
        public PraxUser User;
        public bool RequiresEmailConfirmation;
        public string Email;
        public bool EmailVerified;

        /// <summary>Where the workspace wants the player sent after login, when configured.</summary>
        public string PostLoginRedirectUrl;
    }

    /// <summary>
    /// A workspace's public configuration: the publishable key, branding, and which
    /// auth features are enabled. Fetched without any credential.
    ///
    /// Use it to build a sign-in screen that matches the workspace without hardcoding
    /// colours or hiding a social button that is not configured.
    /// </summary>
    public class PraxWorkspaceConfig
    {
        public string PublishableKey;
        public string WorkspaceName;

        /// <summary>Hex colours without the leading '#', as stored by the workspace.</summary>
        public string LightPrimary;
        public string LightBackground;
        public string LightText;
        public string DarkPrimary;
        public string DarkBackground;
        public string DarkText;

        public bool HasLogo;

        /// <summary>Absolute URL for the workspace logo, or null. Needs no credential.</summary>
        public string LogoUrl;

        public string DefaultLanguage = "en";
        public bool RequireEmailConfirmation;
        public string TermsUrl;
        public string PrivacyUrl;

        /// <summary>Registration fields the workspace wants collected, e.g. firstName, lastName.</summary>
        public IReadOnlyList<string> EnabledRegisterFields = Array.Empty<string>();

        /// <summary>OIDC provider slugs configured for this workspace.</summary>
        public IReadOnlyList<string> OidcProviders = Array.Empty<string>();
    }

    /// <summary>Maps gateway auth payloads onto SDK types.</summary>
    internal static class PraxAuthMapper
    {
        /// <summary>
        /// Unwraps the platform response envelope. Auth routes answer with
        /// {isSuccess, message, errors, data:{...}}, so the payload is one level down - but
        /// some routes answer flat, so fall back to the root.
        /// </summary>
        internal static Dictionary<string, object> Unwrap(Dictionary<string, object> body)
        {
            if (body == null) return new Dictionary<string, object>();
            if (body.TryGetValue("data", out var data) && data is Dictionary<string, object> inner)
                return inner;
            return body;
        }

        internal static PraxSession ToSession(Dictionary<string, object> body)
        {
            var payload = Unwrap(body);

            var accessToken = Str(payload, "accessToken");
            if (string.IsNullOrEmpty(accessToken)) return null;

            var session = new PraxSession
            {
                accessToken = accessToken,
                refreshToken = Str(payload, "refreshToken"),
                accessExpiresAtUnix = Unix(payload, "accessTokenExpiresAt"),
                refreshExpiresAtUnix = Unix(payload, "refreshTokenExpiresAt")
            };

            if (payload.TryGetValue("user", out var userNode) &&
                userNode is Dictionary<string, object> user)
            {
                session.endUserId = Str(user, "id");
                session.email = Str(user, "email");
                session.username = Str(user, "username");
                session.firstName = Str(user, "firstName");
                session.lastName = Str(user, "lastName");
                session.roles = StrArray(user, "roles");
            }

            // Some responses report identity at the top level rather than under "user".
            if (string.IsNullOrEmpty(session.endUserId)) session.endUserId = Str(payload, "endUserId");
            if (string.IsNullOrEmpty(session.email)) session.email = Str(payload, "email");

            return session;
        }

        /// <summary>
        /// Copies profile fields from an existing session onto a refreshed one. The refresh
        /// response carries tokens but not always the user block, and losing the player's
        /// name mid-play would be a visible bug.
        /// </summary>
        internal static void CarryOverProfile(PraxSession from, PraxSession to)
        {
            if (from == null || to == null) return;

            if (string.IsNullOrEmpty(to.endUserId)) to.endUserId = from.endUserId;
            if (string.IsNullOrEmpty(to.email)) to.email = from.email;
            if (string.IsNullOrEmpty(to.username)) to.username = from.username;
            if (string.IsNullOrEmpty(to.firstName)) to.firstName = from.firstName;
            if (string.IsNullOrEmpty(to.lastName)) to.lastName = from.lastName;
            if (to.roles == null || to.roles.Length == 0) to.roles = from.roles;
        }

        internal static PraxUser ToUser(PraxSession session)
        {
            if (session == null) return null;
            return new PraxUser
            {
                Id = session.endUserId,
                Email = session.email,
                Username = session.username,
                FirstName = session.firstName,
                LastName = session.lastName,
                Roles = session.roles ?? Array.Empty<string>()
            };
        }

        internal static PraxAuthResult ToAuthResult(Dictionary<string, object> body, PraxSession session)
        {
            var payload = Unwrap(body);

            return new PraxAuthResult
            {
                IsSignedIn = session != null && session.HasAccessToken,
                User = ToUser(session),
                RequiresEmailConfirmation = Bool(payload, "requiresEmailConfirmation"),
                Email = Str(payload, "email") ?? (session != null ? session.email : null),
                EmailVerified = Bool(payload, "emailVerified"),
                PostLoginRedirectUrl = Str(payload, "postLoginRedirectUrl")
            };
        }

        internal static PraxWorkspaceConfig ToWorkspaceConfig(Dictionary<string, object> body, string baseUrl)
        {
            var config = new PraxWorkspaceConfig
            {
                PublishableKey = Str(body, "publicKey")
            };

            if (body.TryGetValue("branding", out var brandingNode) &&
                brandingNode is Dictionary<string, object> branding)
            {
                config.WorkspaceName = Str(branding, "name");
                config.LightPrimary = Str(branding, "lightPrimary");
                config.LightBackground = Str(branding, "lightBackground");
                config.LightText = Str(branding, "lightText");
                config.DarkPrimary = Str(branding, "darkPrimary");
                config.DarkBackground = Str(branding, "darkBackground");
                config.DarkText = Str(branding, "darkText");
                config.HasLogo = Bool(branding, "hasLogo");

                // The gateway returns a workspace-relative logo path; make it usable directly.
                var logo = Str(branding, "logoUrl");
                if (!string.IsNullOrEmpty(logo))
                {
                    config.LogoUrl = logo.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? logo
                        : PraxRoutes.NormalizeBaseUrl(baseUrl) + logo;
                }
            }

            if (body.TryGetValue("authPageConfig", out var authNode) &&
                authNode is Dictionary<string, object> auth)
            {
                config.DefaultLanguage = Str(auth, "defaultLanguage") ?? "en";
                config.RequireEmailConfirmation = Bool(auth, "requireEmailConfirmation");
                config.TermsUrl = Str(auth, "termsUrl");
                config.PrivacyUrl = Str(auth, "privacyUrl");
                config.EnabledRegisterFields = StrArray(auth, "enabledRegisterFields");
            }

            if (body.TryGetValue("oidcProviders", out var providersNode) &&
                providersNode is List<object> providers)
            {
                var slugs = new List<string>(providers.Count);
                foreach (var p in providers)
                {
                    if (p is Dictionary<string, object> provider)
                    {
                        var slug = Str(provider, "slug") ?? Str(provider, "name");
                        if (!string.IsNullOrEmpty(slug)) slugs.Add(slug);
                    }
                    else if (p is string s) slugs.Add(s);
                }
                config.OidcProviders = slugs;
            }

            return config;
        }

        // ------------------------------------------------------------------ helpers

        private static string Str(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return null;
            var s = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static bool Bool(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return false;
            if (value is bool b) return b;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed;
        }

        private static string[] StrArray(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || !(value is List<object> list))
                return Array.Empty<string>();

            var result = new List<string>(list.Count);
            foreach (var item in list)
                if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result.ToArray();
        }

        /// <summary>Parses an ISO-8601 timestamp to a Unix second count, or 0 if absent.</summary>
        private static long Unix(Dictionary<string, object> map, string key)
        {
            var raw = Str(map, key);
            if (string.IsNullOrEmpty(raw)) return 0;

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed.ToUnixTimeSeconds();

            return 0;
        }
    }
}
