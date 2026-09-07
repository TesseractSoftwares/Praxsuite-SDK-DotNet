using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// Player accounts.
    ///
    /// This is the part of the SDK that makes per-player data safe. When a player signs in,
    /// the gateway issues them a JWT that identifies them, and the workspace's role scopes
    /// apply a row filter to every query made with it - typically "the Enduser column of
    /// this row equals the caller's own id". That filter is applied server-side and cannot
    /// be overridden by the client, so a modified build cannot read or write another
    /// player's rows no matter what query it sends.
    ///
    /// This is the difference between real isolation and the appearance of it. Passing a
    /// player id in a header or a where clause is not isolation - the client chooses that
    /// value, so a cheater chooses it too.
    ///
    /// Setting it up, once, in the portal:
    ///   1. Give the table an Enduser-type column, e.g. Owner.
    ///   2. Under API Gateway / Roles, create a role for players and scope it to that table.
    ///   3. On the table scope, set the row filter to __SELF__.
    ///   4. On the Owner column's scope, set the default value template to {{claim:sub}}.
    ///   5. Assign that role to your end users (or make it the default for new ones).
    ///
    /// Steps 3 and 4 are both required. The row filter scopes reads, updates and deletes, but
    /// an INSERT has no WHERE clause for it to touch - the default value template is what
    /// stamps the owner from the caller's verified token, and a column carrying one cannot be
    /// set by the client at all. Configure only the filter and inserts land with a NULL owner
    /// that the filter then hides, so a player saves and cannot read their save back.
    /// </summary>
    public class PraxAuth
    {
        private readonly PraxsuiteClient _client;

        internal PraxAuth(PraxsuiteClient client)
        {
            _client = client;
        }

        // ------------------------------------------------------------------- state

        /// <summary>True when a player is signed in and holds an access token.</summary>
        public bool IsSignedIn
        {
            get
            {
                var session = _client.CurrentSession;
                return session != null && session.HasAccessToken;
            }
        }

        /// <summary>The signed-in player, or null.</summary>
        public PraxUser CurrentUser => PraxAuthMapper.ToUser(_client.CurrentSession);

        /// <summary>
        /// The signed-in player's end user id, or null. This is the JWT 'sub' claim, and the
        /// value a __SELF__ row filter compares against.
        /// </summary>
        public string CurrentUserId => _client.CurrentSession?.endUserId;

        /// <summary>
        /// Raised on sign-in, and when a stored session is restored at startup. Delivered on
        /// the main thread, so it is safe to touch UI from a handler.
        /// </summary>
        public event Action<PraxUser> SignedIn
        {
            add
            {
                if (value == null) return;
                _client.SignedIn += Wrap(value);
            }
            remove
            {
                if (value == null) return;
                if (Unwrap(value, out var wrapper)) _client.SignedIn -= wrapper;
            }
        }

        // The client raises Action<PraxSession>; callers want Action<PraxUser>. Caching the
        // wrapper per handler is what keeps -= working: a freshly created wrapper would be a
        // different delegate instance from the one that was added, so the handler would leak.
        private readonly Dictionary<Action<PraxUser>, Action<PraxSession>> _wrappers =
            new Dictionary<Action<PraxUser>, Action<PraxSession>>();

        private Action<PraxSession> Wrap(Action<PraxUser> handler)
        {
            lock (_wrappers)
            {
                if (_wrappers.TryGetValue(handler, out var existing)) return existing;
                Action<PraxSession> wrapper = session => handler(PraxAuthMapper.ToUser(session));
                _wrappers[handler] = wrapper;
                return wrapper;
            }
        }

        private bool Unwrap(Action<PraxUser> handler, out Action<PraxSession> wrapper)
        {
            lock (_wrappers)
            {
                if (!_wrappers.TryGetValue(handler, out wrapper)) return false;
                _wrappers.Remove(handler);
                return true;
            }
        }

        /// <summary>Raised on sign-out, and when a session is dropped as unrecoverable.</summary>
        public event Action SignedOut
        {
            add { _client.SignedOut += value; }
            remove { _client.SignedOut -= value; }
        }

        // ------------------------------------------------------------------ config

        /// <summary>
        /// Fetches the workspace's public configuration - publishable key, branding, and which
        /// auth features are enabled. Requires no credential.
        ///
        /// Use it to build a sign-in screen that matches the workspace: its name, logo and
        /// colours, only the registration fields it wants, only the social providers actually
        /// configured, and its terms and privacy links.
        /// </summary>
        public async Task<PraxWorkspaceConfig> GetWorkspaceConfigAsync(CancellationToken ct = default)
        {
            var body = await PraxHttp.SendJsonAsync(_client, "GET",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "config"),
                null, PraxHttp.AuthMode.None, ct).ConfigureAwait(false);

            return PraxAuthMapper.ToWorkspaceConfig(body, _client.BaseUrl);
        }

        // --------------------------------------------------------------- register

        /// <summary>
        /// Creates an account and signs the player in.
        ///
        /// When the workspace requires email confirmation the account is created but no
        /// session is issued: the result carries <c>RequiresEmailConfirmation = true</c> and
        /// <c>IsSignedIn = false</c>. Check that before moving the player past your sign-in
        /// screen.
        /// </summary>
        /// <param name="password">
        /// At least 8 characters, enforced by the gateway. Validate in your UI first so the
        /// player gets immediate feedback instead of a round trip.
        /// </param>
        public async Task<PraxAuthResult> RegisterAsync(
            string email,
            string password,
            string username = null,
            string firstName = null,
            string lastName = null,
            CancellationToken ct = default)
        {
            Require(email, nameof(email));
            Require(password, nameof(password));

            var payload = new Dictionary<string, object>
            {
                { "email", email.Trim() },
                { "password", password }
            };
            if (!string.IsNullOrWhiteSpace(username)) payload["username"] = username.Trim();
            if (!string.IsNullOrWhiteSpace(firstName)) payload["firstName"] = firstName.Trim();
            if (!string.IsNullOrWhiteSpace(lastName)) payload["lastName"] = lastName.Trim();

            var body = await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "register"),
                payload, PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

            var session = PraxAuthMapper.ToSession(body);
            if (session != null) _client.SetSession(session);

            return PraxAuthMapper.ToAuthResult(body, session);
        }

        // ------------------------------------------------------------------ login

        /// <summary>
        /// Signs a player in and stores their session.
        ///
        /// If the workspace has an OIDC provider configured, the gateway validates against it
        /// transparently - no browser redirect, and nothing to change here.
        /// </summary>
        public async Task<PraxAuthResult> LoginAsync(string email, string password,
            CancellationToken ct = default)
        {
            Require(email, nameof(email));
            Require(password, nameof(password));

            var body = await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "login"),
                new Dictionary<string, object>
                {
                    { "email", email.Trim() },
                    { "password", password }
                },
                PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

            var session = PraxAuthMapper.ToSession(body);
            if (session != null) _client.SetSession(session);

            var result = PraxAuthMapper.ToAuthResult(body, session);

            if (!result.IsSignedIn && result.RequiresEmailConfirmation)
                PraxLog.Info("Sign-in blocked: this account has not confirmed its email address yet.");

            return result;
        }

        /// <summary>
        /// Signs out: revokes the refresh token server-side and clears local state.
        ///
        /// Local state is cleared even when the network call fails, so the player is never left
        /// looking signed in with a session the SDK has given up on.
        /// </summary>
        public async Task SignOutAsync(CancellationToken ct = default)
        {
            var refreshToken = _client.CurrentSession?.refreshToken;

            _client.ClearSession();

            if (string.IsNullOrEmpty(refreshToken)) return;

            try
            {
                await PraxHttp.SendJsonAsync(_client, "POST",
                    PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "logout"),
                    new Dictionary<string, object> { { "refreshToken", refreshToken } },
                    PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);
            }
            catch (PraxException ex)
            {
                // The local session is already gone; a failed revoke only means the refresh
                // token stays valid server-side until it expires on its own.
                PraxLog.Warn("Signed out locally, but the server-side revoke failed: " + ex.Code);
            }
        }

        /// <summary>
        /// Forces a token refresh. The SDK already refreshes on its own - before a stale token
        /// is used, and once in response to a 401 - so calling this is rarely needed.
        /// </summary>
        /// <returns>True if a session is held afterwards.</returns>
        public Task<bool> RefreshSessionAsync(CancellationToken ct = default)
        {
            return _client.TryRefreshSessionAsync(ct);
        }

        // -------------------------------------------------------------- passwords

        /// <summary>
        /// Emails the player a 6-digit reset code.
        ///
        /// Always succeeds, whether or not the address exists - the gateway does that
        /// deliberately so the response cannot be used to enumerate accounts. Show the same
        /// "check your email" message either way; telling the player "no such account" would
        /// reintroduce the leak the API is avoiding.
        /// </summary>
        public async Task ForgotPasswordAsync(string email, CancellationToken ct = default)
        {
            Require(email, nameof(email));

            await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "forgot-password"),
                new Dictionary<string, object> { { "email", email.Trim() } },
                PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies the 6-digit code and returns a short-lived session token to pass to
        /// <see cref="ResetPasswordAsync"/>. Use it promptly - it is meant for the next call,
        /// not for storage.
        /// </summary>
        public async Task<string> VerifyResetCodeAsync(string email, string code,
            CancellationToken ct = default)
        {
            Require(email, nameof(email));
            Require(code, nameof(code));

            var body = await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "verify-reset-code"),
                new Dictionary<string, object>
                {
                    { "email", email.Trim() },
                    { "code", code.Trim() }
                },
                PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

            var token = PraxHttp.AsString(PraxAuthMapper.Unwrap(body), "sessionToken");
            if (string.IsNullOrEmpty(token))
                throw new PraxException("INVALID_RESET_CODE",
                    "The reset code was not accepted. It may be wrong or expired.");

            return token;
        }

        /// <summary>Sets a new password using the token from <see cref="VerifyResetCodeAsync"/>.</summary>
        public async Task ResetPasswordAsync(string sessionToken, string newPassword,
            CancellationToken ct = default)
        {
            Require(sessionToken, nameof(sessionToken));
            Require(newPassword, nameof(newPassword));

            await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "reset-password"),
                new Dictionary<string, object>
                {
                    { "sessionToken", sessionToken },
                    { "newPassword", newPassword },
                    { "confirmPassword", newPassword }
                },
                PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Changes the signed-in player's password. Requires an active session - this is the
        /// one auth route the gateway authenticates with the player's own token rather than the
        /// workspace key.
        /// </summary>
        public async Task ChangePasswordAsync(string currentPassword, string newPassword,
            CancellationToken ct = default)
        {
            Require(currentPassword, nameof(currentPassword));
            Require(newPassword, nameof(newPassword));

            if (!IsSignedIn)
                throw new PraxException("NOT_SIGNED_IN",
                    "ChangePasswordAsync needs a signed-in player. Use ForgotPasswordAsync for a " +
                    "player who cannot sign in.");

            await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "change-password"),
                new Dictionary<string, object>
                {
                    { "currentPassword", currentPassword },
                    { "newPassword", newPassword },
                    { "confirmPassword", newPassword }
                },
                PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Resends the confirmation email. Like <see cref="ForgotPasswordAsync"/>, always
        /// reports success so it cannot be used to probe for accounts.
        /// </summary>
        public async Task ResendConfirmationAsync(string email, CancellationToken ct = default)
        {
            Require(email, nameof(email));

            await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "resend-confirmation"),
                new Dictionary<string, object> { { "email", email.Trim() } },
                PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------- OIDC

        /// <summary>
        /// Returns the provider URL to open for a social or enterprise sign-in.
        ///
        /// The flow needs a browser, so it suits desktop and mobile rather than consoles: open
        /// the URL with <c>Application.OpenURL</c>, catch the redirect with a deep link or a
        /// loopback listener, then pass the code and state to <see cref="CompleteOidcLoginAsync"/>.
        ///
        /// Provider slugs come from <see cref="GetWorkspaceConfigAsync"/> - showing a button for
        /// a provider the workspace has not configured only produces a dead end.
        /// </summary>
        public async Task<PraxOidcStart> StartOidcLoginAsync(string providerSlug,
            CancellationToken ct = default)
        {
            Require(providerSlug, nameof(providerSlug));

            var body = await PraxHttp.SendJsonAsync(_client, "GET",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId,
                    "oidc/" + Uri.EscapeDataString(providerSlug.Trim())),
                null, PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

            var payload = PraxAuthMapper.Unwrap(body);
            var url = PraxHttp.AsString(payload, "authorizationUrl") ?? PraxHttp.AsString(payload, "url");

            if (string.IsNullOrEmpty(url))
                throw new PraxException("OIDC_NO_URL",
                    "The gateway did not return an authorization URL for provider '" + providerSlug +
                    "'. Check that the provider is configured and enabled for this workspace.");

            return new PraxOidcStart
            {
                AuthorizationUrl = url,
                State = PraxHttp.AsString(payload, "state") ?? string.Empty,
            };
        }

        /// <summary>
        /// Returns only the URL. Prefer <see cref="StartOidcLoginAsync"/>, which also returns the
        /// state the callback requires.
        /// </summary>
        [Obsolete("Use StartOidcLoginAsync, which also returns the state the callback requires.")]
        public async Task<string> GetOidcAuthorizationUrlAsync(string providerSlug,
            CancellationToken ct = default)
        {
            var start = await StartOidcLoginAsync(providerSlug, ct).ConfigureAwait(false);
            return start.AuthorizationUrl;
        }

        /// <summary>
        /// Completes an external sign-in, exchanging the provider's code for a Praxsuite session.
        ///
        /// All four values are required by the gateway, and three of them are why this call fails
        /// when it fails. <paramref name="providerSlug"/> scopes the one-time state, so omitting it
        /// makes every callback look expired. <paramref name="state"/> is consumed once; reusing or
        /// skipping it is rejected. <paramref name="redirectUri"/> is compared against the value
        /// configured for that provider and must match exactly - pass the URI you were actually
        /// redirected to rather than rebuilding it, which is how it ends up differing by a trailing
        /// slash and failing with a message about redirect URIs that nobody can act on.
        ///
        /// The session is stored exactly as a password login stores it, so refresh, sign-out and
        /// every authenticated call behave identically afterwards.
        ///
        /// An email already registered as a local password account comes back as a 400 rather than
        /// a session; the user's fix is to sign in with their password.
        /// </summary>
        public async Task<PraxAuthResult> CompleteOidcLoginAsync(string providerSlug, string code,
            string state, string redirectUri, CancellationToken ct = default)
        {
            Require(providerSlug, nameof(providerSlug));
            Require(code, nameof(code));
            Require(state, nameof(state));
            Require(redirectUri, nameof(redirectUri));

            var payload = new Dictionary<string, object>
            {
                { "providerSlug", providerSlug },
                { "code", code },
                { "state", state },
                { "redirectUri", redirectUri },
            };

            var body = await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Auth(_client.BaseUrl, _client.WorkspaceId, "oidc/callback"),
                payload, PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

            var session = PraxAuthMapper.ToSession(body);
            if (session != null) _client.SetSession(session);

            return PraxAuthMapper.ToAuthResult(body, session);
        }

        // ---------------------------------------------------------------- helpers

        private static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(name + " is required.", name);
        }
    }
}
