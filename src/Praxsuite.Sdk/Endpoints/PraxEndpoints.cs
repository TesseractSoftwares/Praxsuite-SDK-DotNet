using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// Calls gateway endpoints - the server-authoritative half of the SDK, and the one that
    /// matters most for a game.
    ///
    /// An endpoint is a URL in your workspace bound to an automation you built in the portal.
    /// The client posts a payload; the automation decides what actually happens. This is where
    /// every rule a player must not be able to break belongs:
    ///
    ///   - granting currency, items or XP        (client says "I killed the boss", server decides the reward)
    ///   - submitting a score                    (server validates it is reachable before writing it)
    ///   - spending currency                     (server checks the balance it owns)
    ///   - anything involving another player     (server enforces who may touch whom)
    ///
    /// The rule of thumb: if a modified client sending an arbitrary payload could get something
    /// it should not, that operation belongs in an endpoint and the table behind it must not be
    /// writable by the player's role. A direct table write is right for a player's own
    /// cosmetic state - settings, last-played level, key bindings - and wrong for anything with
    /// value.
    ///
    /// Two modes, set per endpoint in the portal:
    ///   Sync  - holds the connection, runs one automation, returns its Response node output.
    ///           Use <see cref="CallAsync"/> when you need the answer.
    ///   Async - returns immediately, runs subscribed automations in the background.
    ///           Use <see cref="FireAsync"/> for telemetry and fire-and-forget events.
    /// </summary>
    public class PraxEndpoints
    {
        private readonly PraxsuiteClient _client;

        internal PraxEndpoints(PraxsuiteClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Calls a sync endpoint and returns the automation's response.
        ///
        /// The connection is held while the automation runs, so a slow automation shows up as a
        /// slow call - keep the transport timeout in mind for anything heavy, and prefer
        /// <see cref="FireAsync"/> when you do not need the result.
        /// </summary>
        /// <param name="slug">Endpoint id or friendly slug, from the portal's Gateway view.</param>
        /// <param name="payload">
        /// Any JSON-serialisable value. Reachable in the automation as
        /// <c>{{context.request.body.yourField}}</c>.
        /// </param>
        public async Task<Dictionary<string, object>> CallAsync(string slug, object payload = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(slug))
                throw new ArgumentException("An endpoint slug is required.", nameof(slug));

            // Sends the player's token when signed in, so the automation can identify the
            // caller from a claim rather than trusting a player id in the payload.
            return await PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Endpoint(_client.BaseUrl, _client.WorkspaceId, slug.Trim()),
                payload, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);
        }

        /// <summary>Calls a sync endpoint and projects the response onto <typeparamref name="T"/>.</summary>
        public async Task<T> CallAsync<T>(string slug, object payload = null,
            CancellationToken ct = default) where T : new()
        {
            var body = await CallAsync(slug, payload, ct).ConfigureAwait(false);
            return new PraxRow(body).As<T>();
        }

        /// <summary>
        /// Posts to an endpoint without caring about the result - telemetry, analytics, a
        /// "player quit" event.
        ///
        /// Never throws. A dropped analytics event must not surface as an exception in the
        /// middle of gameplay, and there is nothing useful for the caller to do about it.
        /// Returns false when the call did not land, and logs why.
        /// </summary>
        public async Task<bool> FireAsync(string slug, object payload = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                PraxLog.Warn("FireAsync was called with no endpoint slug; ignoring it.");
                return false;
            }

            try
            {
                await PraxHttp.SendAsync(_client, "POST",
                    PraxRoutes.Endpoint(_client.BaseUrl, _client.WorkspaceId, slug.Trim()),
                    payload, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (PraxException ex)
            {
                PraxLog.Warn("Endpoint '" + slug + "' did not accept the event (" + ex.Code + "): " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                PraxLog.Warn("Endpoint '" + slug + "' failed unexpectedly: " + ex.Message);
                return false;
            }
        }
    }
}
