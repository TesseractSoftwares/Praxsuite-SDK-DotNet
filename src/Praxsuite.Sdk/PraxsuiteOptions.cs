using System;

namespace Praxsuite
{
    /// <summary>
    /// Client configuration.
    ///
    /// Bind it from IConfiguration, environment variables, or set it in code. WorkspaceId is the
    /// only required value - the publishable key is fetched from the workspace's public config
    /// endpoint when omitted.
    /// </summary>
    public class PraxsuiteOptions
    {
        /// <summary>Workspace GUID. Required.</summary>
        public string WorkspaceId;

        /// <summary>Gateway base URL. Defaults to Praxsuite Cloud.</summary>
        public string BaseUrl = PraxRoutes.CloudHost;

        /// <summary>
        /// Publishable key (pk_live_). Optional - when omitted the SDK fetches it from the
        /// workspace's public config endpoint.
        ///
        /// A secret key here throws immediately. Secret keys belong in a dedicated server
        /// build via Praxsuite.Server.PraxServer, never in anything a player can run.
        /// </summary>
        public string PublishableKey;

        public int TimeoutSeconds = 30;
        public int MaxRetries = 3;
        public int RefreshLeadSeconds = 60;
        public bool AutoFetchSchema = true;
        public bool VerboseLogging;

        /// <summary>
        /// Persist the player session between runs using the built-in encrypted file store.
        /// Ignored when <see cref="TokenStore"/> is set.
        /// </summary>
        public bool PersistSession = true;

        /// <summary>
        /// Supply your own session storage - the iOS Keychain, the Android Keystore, a
        /// console save API, or <see cref="PraxMemoryTokenStore"/> to keep nothing on disk.
        /// </summary>
        public IPraxTokenStore TokenStore;

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(WorkspaceId))
                throw new PraxException("INVALID_CONFIG", "PraxsuiteOptions.WorkspaceId is required.");

            if (!Guid.TryParse(WorkspaceId.Trim(), out _))
                throw new PraxException("INVALID_CONFIG",
                    "PraxsuiteOptions.WorkspaceId is not a valid GUID: " + WorkspaceId);

            if (TimeoutSeconds < 1) TimeoutSeconds = 1;
            if (MaxRetries < 0) MaxRetries = 0;
            if (RefreshLeadSeconds < 0) RefreshLeadSeconds = 0;
        }
    }
}
