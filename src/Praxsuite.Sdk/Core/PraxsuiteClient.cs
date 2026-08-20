using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// A configured Praxsuite client: configuration, credential resolution, and the signed-in
    /// session.
    ///
    /// Unlike the Unity SDK there is no ambient singleton. A .NET process may talk to several
    /// workspaces, and a web request may act as a different user each time, so the client is an
    /// ordinary object you construct and own. Register it as a singleton in DI if one workspace
    /// and one credential is all you need.
    ///
    /// Thread-safe for concurrent requests. Session mutation is guarded, and a token refresh is
    /// shared rather than duplicated.
    /// </summary>
    public sealed class PraxsuiteClient : IDisposable
    {
        internal readonly string WorkspaceId;
        internal readonly string BaseUrl;
        internal readonly int MaxRetries;
        internal readonly int RefreshLeadSeconds;
        internal readonly bool AutoFetchSchema;
        internal readonly HttpClient Http;

        private readonly bool _ownsHttpClient;
        private readonly IPraxTokenStore _tokenStore;

        /// <summary>Table name to id mapping.</summary>
        public PraxSchema Schema { get; }

        /// <summary>User accounts: register, sign in, sessions, password flows, OIDC.</summary>
        public PraxAuth Auth { get; }

        /// <summary>Table reads and writes.</summary>
        public PraxData Data { get; }

        /// <summary>Gateway endpoints - the server-authoritative path.</summary>
        public PraxEndpoints Endpoints { get; }

        /// <summary>File upload and download.</summary>
        public PraxFiles Files { get; }

        /// <summary>Platform identity links, for analytics and account linking.</summary>
        public PraxPlayers Players { get; }

        /// <summary>Raised after a successful sign-in, or a session restored from a store.</summary>
        public event Action<PraxSession> SignedIn;

        /// <summary>Raised on sign-out, and when a session is dropped as unrecoverable.</summary>
        public event Action SignedOut;

        private string _publishableKey;
        private Task<string> _keyDiscovery;
        private Task<bool> _refreshInFlight;
        private PraxSession _session;
        private bool _sessionLoaded;

        private readonly object _keyGate = new object();
        private readonly object _sessionGate = new object();

        /// <summary>
        /// Creates a client.
        ///
        /// Pass an <see cref="HttpClient"/> when your app already manages one - via
        /// IHttpClientFactory, with a proxy, or with custom handlers. When omitted the client
        /// creates and owns one, which is correct for a console app or a game but wrong for a
        /// long-lived service that creates many clients (socket exhaustion).
        /// </summary>
        public PraxsuiteClient(PraxsuiteOptions options, HttpClient httpClient = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            WorkspaceId = options.WorkspaceId.Trim();
            BaseUrl = PraxRoutes.NormalizeBaseUrl(options.BaseUrl);
            MaxRetries = options.MaxRetries;
            RefreshLeadSeconds = options.RefreshLeadSeconds;
            AutoFetchSchema = options.AutoFetchSchema;

            if (options.VerboseLogging) PraxLog.Minimum = PraxLog.Level.Verbose;

            if (!string.IsNullOrWhiteSpace(options.PublishableKey))
            {
                var key = options.PublishableKey.Trim();
                // A key supplied at runtime has passed through no build-time check.
                PraxKeyGuard.RequireClientSafe(key, "PraxsuiteOptions.PublishableKey");
                _publishableKey = key;
            }

            if (PraxRoutes.IsInsecureRemote(BaseUrl))
            {
                // Plaintext to a remote host would put the key and every session token on the
                // wire in clear. Loopback is allowed for local development.
                throw new PraxSecurityException(
                    "Refusing to use a plaintext http:// gateway URL for a remote host (" + BaseUrl + ").\n" +
                    "API keys and session tokens would travel unencrypted. Use https://, or point " +
                    "at localhost for local development.");
            }

            _ownsHttpClient = httpClient == null;
            Http = httpClient ?? new HttpClient();
            if (_ownsHttpClient) Http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            _tokenStore = options.TokenStore ?? new PraxMemoryTokenStore();

            Schema = new PraxSchema(this);
            Auth = new PraxAuth(this);
            Data = new PraxData(this);
            Endpoints = new PraxEndpoints(this);
            Files = new PraxFiles(this);
            Players = new PraxPlayers(this);
        }

        /// <summary>Convenience overload for the common case: a workspace id and nothing else.</summary>
        public PraxsuiteClient(string workspaceId, string baseUrl = null, string publishableKey = null)
            : this(new PraxsuiteOptions
            {
                WorkspaceId = workspaceId,
                BaseUrl = baseUrl ?? PraxRoutes.CloudHost,
                PublishableKey = publishableKey,
            })
        {
        }

        // ------------------------------------------------------------- credentials

        /// <summary>
        /// Returns the workspace publishable key, discovering it from the public /auth/config
        /// endpoint when the app did not supply one. Discovery runs once; concurrent callers
        /// share the request.
        /// </summary>
        internal Task<string> GetPublishableKeyAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_publishableKey)) return Task.FromResult(_publishableKey);

            lock (_keyGate)
            {
                if (!string.IsNullOrEmpty(_publishableKey)) return Task.FromResult(_publishableKey);
                if (_keyDiscovery != null && !_keyDiscovery.IsFaulted) return _keyDiscovery;
                _keyDiscovery = DiscoverPublishableKeyAsync(ct);
                return _keyDiscovery;
            }
        }

        private async Task<string> DiscoverPublishableKeyAsync(CancellationToken ct)
        {
            PraxLog.Info("No publishable key configured; fetching the workspace public config.");

            Dictionary<string, object> body;
            try
            {
                body = await PraxHttp.SendJsonAsync(this, "GET",
                    PraxRoutes.Auth(BaseUrl, WorkspaceId, "config"),
                    null, PraxHttp.AuthMode.None, ct).ConfigureAwait(false);
            }
            catch (PraxException ex) when (ex.StatusCode == 404)
            {
                throw new PraxException("WORKSPACE_NOT_FOUND",
                    "Workspace " + WorkspaceId + " was not found at " + BaseUrl + ".\n\n" +
                    "Either the workspace id is wrong, or the workspace lives on a different " +
                    "Praxsuite tier - a workspace hosted on another tier returns 404 here. Check " +
                    "the URL on your workspace's API Gateway settings page.", 404);
            }

            var key = PraxHttp.AsString(body, "publicKey");
            if (string.IsNullOrEmpty(key))
            {
                throw new PraxException("NO_PUBLISHABLE_KEY",
                    "Workspace " + WorkspaceId + " has no credential marked publishable.\n\n" +
                    "Create a client credential in the portal under API Gateway / Credentials, " +
                    "scope it to the minimum a public client needs, and mark it publishable.");
            }

            PraxKeyGuard.RequireClientSafe(key, "the workspace public config endpoint");
            _publishableKey = key;
            PraxLog.Info("Resolved publishable key " + PraxKeyGuard.Redact(key) + ".");
            return key;
        }

        /// <summary>Picks the credential for a request: the user's token when wanted, else the key.</summary>
        internal async Task<string> ResolveCredentialAsync(PraxHttp.AuthMode mode, CancellationToken ct)
        {
            if (mode == PraxHttp.AuthMode.None) return null;

            if (mode == PraxHttp.AuthMode.PreferSession)
            {
                var session = CurrentSession;
                if (session != null && session.HasAccessToken)
                {
                    // Refresh proactively so the request does not spend a round trip on a 401.
                    if (session.HasRefreshToken && session.IsAccessStale(RefreshLeadSeconds))
                    {
                        await TryRefreshSessionAsync(ct).ConfigureAwait(false);
                        session = CurrentSession;
                    }
                    if (session != null && session.HasAccessToken) return session.accessToken;
                }
            }

            return await GetPublishableKeyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Sends a request through the shared transport. Used by every module.</summary>
        internal Task<Dictionary<string, object>> RequestAsync(
            string method, string url, object body, PraxHttp.AuthMode authMode, CancellationToken ct)
        {
            return PraxHttp.SendJsonAsync(this, method, url, body, authMode, ct);
        }

        // ---------------------------------------------------------------- sessions

        /// <summary>The current session, loading a persisted one on first access.</summary>
        internal PraxSession CurrentSession
        {
            get
            {
                lock (_sessionGate)
                {
                    if (!_sessionLoaded)
                    {
                        _sessionLoaded = true;
                        var stored = _tokenStore.Load();
                        if (stored != null && stored.IsRefreshExpired())
                        {
                            PraxLog.Info("The stored session's refresh token has expired; discarding it.");
                            _tokenStore.Clear();
                        }
                        else if (stored != null)
                        {
                            _session = stored;
                            PraxLog.Info("Restored a stored session for " + (stored.email ?? stored.endUserId) + ".");
                            RaiseSignedIn(stored);
                        }
                    }
                    return _session;
                }
            }
        }

        internal void SetSession(PraxSession session)
        {
            lock (_sessionGate)
            {
                _session = session;
                _sessionLoaded = true;
                _tokenStore.Save(session);
            }

            if (session != null) RaiseSignedIn(session);
            else RaiseSignedOut();
        }

        internal void ClearSession()
        {
            lock (_sessionGate)
            {
                _session = null;
                _sessionLoaded = true;
                _tokenStore.Clear();
            }
            RaiseSignedOut();
        }

        /// <summary>
        /// Exchanges the refresh token for a new pair.
        ///
        /// Refresh tokens rotate: the gateway invalidates the old one as it issues the new one.
        /// Two concurrent refreshes would race, with the loser holding a token the server has
        /// already retired - so callers share a single in-flight refresh.
        /// </summary>
        internal Task<bool> TryRefreshSessionAsync(CancellationToken ct)
        {
            lock (_sessionGate)
            {
                if (_refreshInFlight != null && !_refreshInFlight.IsCompleted) return _refreshInFlight;

                var session = _session;
                if (session == null || !session.HasRefreshToken) return Task.FromResult(false);

                _refreshInFlight = RefreshCoreAsync(session.refreshToken, ct);
                return _refreshInFlight;
            }
        }

        private async Task<bool> RefreshCoreAsync(string refreshToken, CancellationToken ct)
        {
            try
            {
                var body = await PraxHttp.SendJsonAsync(this, "POST",
                    PraxRoutes.Auth(BaseUrl, WorkspaceId, "refresh"),
                    new Dictionary<string, object> { { "refreshToken", refreshToken } },
                    PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

                var refreshed = PraxAuthMapper.ToSession(body);
                if (refreshed == null || !refreshed.HasAccessToken)
                {
                    PraxLog.Warn("The refresh response carried no access token; signing out.");
                    ClearSession();
                    return false;
                }

                // The refresh response carries tokens but not always the user block; losing the
                // profile mid-session would be a visible bug.
                lock (_sessionGate)
                {
                    if (_session != null) PraxAuthMapper.CarryOverProfile(_session, refreshed);
                }

                SetSession(refreshed);
                PraxLog.Info("Session refreshed.");
                return true;
            }
            catch (PraxException ex)
            {
                if (ex.IsTransient)
                {
                    // Keep the session: the token may still be good once the network recovers.
                    PraxLog.Warn("Could not refresh the session right now (" + ex.Code + "). Keeping it.");
                    return false;
                }

                PraxLog.Info("The refresh token was rejected (" + ex.Code + "); signing out.");
                ClearSession();
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private void RaiseSignedIn(PraxSession session)
        {
            try { SignedIn?.Invoke(session); }
            catch (Exception ex) { PraxLog.Error("A SignedIn handler threw.", ex); }
        }

        private void RaiseSignedOut()
        {
            try { SignedOut?.Invoke(); }
            catch (Exception ex) { PraxLog.Error("A SignedOut handler threw.", ex); }
        }

        public void Dispose()
        {
            // Only dispose what we created; a caller-supplied HttpClient belongs to the caller.
            if (_ownsHttpClient) Http?.Dispose();
        }
    }
}
