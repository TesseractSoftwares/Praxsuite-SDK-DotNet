using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>A platform identity link recorded by the gateway.</summary>
    public class PraxPlayerIdentity
    {
        public string Id;

        /// <summary>Platform slug: unity, steam, roblox, minecraft, fivem, unreal, custom.</summary>
        public string Platform;

        public string PlatformPlayerId;
        public string DisplayName;

        /// <summary>Linked Praxsuite contact, when one exists.</summary>
        public string ContactId;

        public DateTimeOffset? FirstSeenAt;
        public DateTimeOffset? LastSeenAt;

        /// <summary>
        /// True only when the gateway confirmed the id against the platform's own API.
        /// Praxsuite ships a validator for Roblox; for Steam and others there is nothing to
        /// check against yet, so this stays false and the id is an unverified label.
        /// </summary>
        public bool IsValidated;
    }

    /// <summary>
    /// Records platform identities - a Steam ID, a device id, a launcher account - alongside a
    /// player.
    ///
    /// Read this before using it as an identity system, because it is not one. A platform id
    /// recorded here is a label the client supplied. Nothing signs it, and (except for Roblox,
    /// which the gateway can check against the Roblox users API) nothing verifies it, so a
    /// modified client can claim any id it likes. <c>IsValidated</c> tells you which case you
    /// are in.
    ///
    /// So: use this for analytics, for cross-platform account linking, and for showing a
    /// friendly name. Do NOT use it to decide what data a player may reach. That decision
    /// belongs to <see cref="PraxAuth"/>, where the gateway issues the token and applies the
    /// row filter, and where the client cannot choose who it is.
    ///
    /// If you want platform login to be the only sign-in your players see, the shape that
    /// actually holds is: client sends the platform's signed session ticket to a gateway
    /// endpoint, an automation verifies that ticket with the platform's server API, and only
    /// then does the automation return credentials. The verification has to happen somewhere
    /// the player cannot edit.
    /// </summary>
    public class PraxPlayers
    {
        private readonly PraxsuiteClient _client;

        internal PraxPlayers(PraxsuiteClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Records or refreshes a platform identity link.
        /// </summary>
        /// <param name="platform">
        /// Platform slug. The gateway accepts unity, steam, roblox, minecraft, fivem, unreal
        /// and custom, and only validates roblox.
        /// </param>
        /// <param name="platformPlayerId">The id on that platform.</param>
        /// <param name="displayName">Cached for dashboards and admin views.</param>
        /// <param name="metadata">Anything else worth storing with the link.</param>
        public async Task<PraxPlayerIdentity> IdentifyAsync(
            string platform,
            string platformPlayerId,
            string displayName = null,
            IDictionary<string, object> metadata = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(platform))
                throw new ArgumentException("platform is required.", nameof(platform));
            if (string.IsNullOrWhiteSpace(platformPlayerId))
                throw new ArgumentException("platformPlayerId is required.", nameof(platformPlayerId));

            var payload = new Dictionary<string, object>
            {
                { "platform", platform.Trim().ToLowerInvariant() },
                { "platformPlayerId", platformPlayerId.Trim() }
            };
            if (!string.IsNullOrWhiteSpace(displayName)) payload["displayName"] = displayName.Trim();

            if (metadata != null && metadata.Count > 0)
            {
                var map = new Dictionary<string, object>(metadata.Count);
                foreach (var pair in metadata) map[pair.Key] = pair.Value;
                payload["metadata"] = map;
            }

            var body = await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Players(_client.BaseUrl, _client.WorkspaceId, "identify"),
                payload, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);

            return ParseIdentity(PraxAuthMapper.Unwrap(body));
        }

        /// <summary>Looks up an identity link. Returns null when there is none.</summary>
        public async Task<PraxPlayerIdentity> ResolveAsync(string platform, string platformPlayerId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(platform))
                throw new ArgumentException("platform is required.", nameof(platform));
            if (string.IsNullOrWhiteSpace(platformPlayerId))
                throw new ArgumentException("platformPlayerId is required.", nameof(platformPlayerId));

            var path = "resolve/" + Uri.EscapeDataString(platform.Trim().ToLowerInvariant()) +
                       "/" + Uri.EscapeDataString(platformPlayerId.Trim());

            try
            {
                var body = await PraxHttp.SendJsonAsync(_client, "GET",
                    PraxRoutes.Players(_client.BaseUrl, _client.WorkspaceId, path),
                    null, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);

                return ParseIdentity(PraxAuthMapper.Unwrap(body));
            }
            catch (PraxException ex) when (ex.StatusCode == 404)
            {
                // "No link yet" is an ordinary answer, not a failure.
                return null;
            }
        }

        private static PraxPlayerIdentity ParseIdentity(Dictionary<string, object> map)
        {
            if (map == null || map.Count == 0) return null;

            var identity = new PraxPlayerIdentity
            {
                Id = PraxHttp.AsString(map, "id"),
                Platform = PraxHttp.AsString(map, "platform"),
                PlatformPlayerId = PraxHttp.AsString(map, "platformPlayerId"),
                DisplayName = PraxHttp.AsString(map, "displayName"),
                ContactId = PraxHttp.AsString(map, "contactId")
            };

            if (map.TryGetValue("isValidated", out var validated) && validated is bool b)
                identity.IsValidated = b;

            identity.FirstSeenAt = ParseDate(PraxHttp.AsString(map, "firstSeenAt"));
            identity.LastSeenAt = ParseDate(PraxHttp.AsString(map, "lastSeenAt"));

            return identity;
        }

        private static DateTimeOffset? ParseDate(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }
    }
}
