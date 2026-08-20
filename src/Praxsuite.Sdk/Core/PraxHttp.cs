using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// The SDK's transport. One place that knows how to talk to the gateway, so retry policy,
    /// credential handling, redaction and error shaping are decided once.
    /// </summary>
    internal static class PraxHttp
    {
        private const string ApiKeyHeader = "x-api-key";
        private const int MaxBackoffMs = 30_000;

        // Backoff jitter can be computed from any thread, and Random is not thread-safe.
        private static readonly Random Jitter = new Random();
        private static readonly object JitterGate = new object();

        private static double NextJitter()
        {
            lock (JitterGate) return Jitter.NextDouble();
        }

        internal enum AuthMode
        {
            /// <summary>Send the workspace publishable key.</summary>
            ApiKey,
            /// <summary>Send the signed-in user's access token, falling back to the api key.</summary>
            PreferSession,
            /// <summary>Send no credential. Only /auth/config and /auth/logo allow this.</summary>
            None
        }

        internal class Response
        {
            public int Status;
            public string Body;
            public HttpResponseHeaders Headers;
            public bool Ok => Status >= 200 && Status < 300;
        }

        /// <summary>
        /// Attaches the credential.
        ///
        /// The gateway accepts either header. x-api-key carries keys and Authorization carries
        /// session tokens, matching how the backend middleware documents them - the distinction
        /// keeps gateway access logs readable.
        /// </summary>
        private static void ApplyCredential(HttpRequestMessage request, string credential)
        {
            if (string.IsNullOrEmpty(credential)) return;

            if (PraxKeyGuard.Classify(credential) == PraxKeyGuard.KeyKind.EndUserJwt)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            else
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, credential);
        }

        /// <summary>
        /// Sends a JSON request and returns the parsed body.
        ///
        /// Handles credential selection, one silent token refresh on 401, retry with exponential
        /// backoff and jitter for transient failures, and mapping any non-2xx into a
        /// <see cref="PraxException"/> whose Code matches what the gateway sent.
        /// </summary>
        internal static async Task<Dictionary<string, object>> SendJsonAsync(
            PraxsuiteClient client, string method, string url, object body, AuthMode authMode,
            CancellationToken ct = default)
        {
            var response = await SendAsync(client, method, url, body, authMode, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response.Body)) return new Dictionary<string, object>();

            try
            {
                return PraxJson.ParseObject(response.Body);
            }
            catch (PraxJsonException ex)
            {
                throw new PraxException("MALFORMED_RESPONSE",
                    "The gateway returned a body that is not valid JSON. " + ex.Message,
                    response.Status, null, response.Body);
            }
        }

        internal static async Task<Response> SendAsync(
            PraxsuiteClient client, string method, string url, object body, AuthMode authMode,
            CancellationToken ct = default)
        {
            var payload = body == null ? null : PraxJson.Serialize(body);
            var attempts = client.MaxRetries + 1;
            var refreshAttempted = false;

            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var credential = await client.ResolveCredentialAsync(authMode, ct).ConfigureAwait(false);

                if (PraxLog.Minimum >= PraxLog.Level.Verbose)
                {
                    PraxLog.Verbose(method + " " + url + " auth=" + PraxKeyGuard.Redact(credential) +
                                    (payload == null ? "" : " body=" + payload));
                }

                Response response;
                try
                {
                    response = await SendOnceAsync(client, method, url, payload, credential, ct)
                        .ConfigureAwait(false);
                }
                catch (PraxException transport) when (transport.IsNetworkError)
                {
                    if (attempt >= attempts) throw;
                    var wait = Backoff(null, attempt);
                    PraxLog.Warn("Attempt " + attempt + "/" + attempts + " failed (" + transport.Code +
                                 "). Retrying in " + wait + "ms.");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                if (response.Ok)
                {
                    PraxLog.Verbose("HTTP " + response.Status + " <- " + url +
                                    " (" + (response.Body?.Length ?? 0) + " bytes)");
                    return response;
                }

                var error = BuildError(response);

                // A 401 on a session-backed call usually means the access token aged out between
                // our expiry check and the server's. Refresh once and replay; if the refresh
                // itself fails the session is genuinely gone.
                if (response.Status == 401 && authMode == AuthMode.PreferSession && !refreshAttempted)
                {
                    refreshAttempted = true;
                    if (await client.TryRefreshSessionAsync(ct).ConfigureAwait(false))
                    {
                        PraxLog.Info("Access token was rejected; refreshed the session and retrying.");
                        continue;
                    }
                }

                if (!error.IsTransient || attempt >= attempts) throw error;

                var delay = Backoff(response, attempt);
                PraxLog.Warn("Attempt " + attempt + "/" + attempts + " failed (" + error.Code +
                             "). Retrying in " + delay + "ms.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        private static async Task<Response> SendOnceAsync(
            PraxsuiteClient client, string method, string url, string payload, string credential,
            CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(new HttpMethod(method), url))
            {
                if (payload != null)
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                ApplyCredential(request, credential);

                try
                {
                    var httpResponse = await client.Http.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                    using (httpResponse)
                    {
                        return new Response
                        {
                            Status = (int)httpResponse.StatusCode,
                            Body = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
                            Headers = httpResponse.Headers,
                        };
                    }
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    // HttpClient reports its own timeout as a cancellation, which is
                    // indistinguishable from a caller cancel unless the token is checked.
                    throw new PraxException("TIMEOUT",
                        "The request to " + url + " timed out after " + client.Http.Timeout.TotalSeconds + "s.");
                }
                catch (HttpRequestException ex)
                {
                    throw new PraxException("NETWORK_ERROR",
                        "Could not reach the Praxsuite gateway: " + ex.Message + "\nURL: " + url);
                }
            }
        }

        /// <summary>Uploads raw bytes as multipart/form-data under the field name "file".</summary>
        internal static async Task<Dictionary<string, object>> SendMultipartAsync(
            PraxsuiteClient client, string url, byte[] fileBytes, string fileName, string contentType,
            AuthMode authMode, CancellationToken ct = default)
        {
            var credential = await client.ResolveCredentialAsync(authMode, ct).ConfigureAwait(false);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            using (var form = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                form.Add(fileContent, "file", fileName);

                request.Content = form;
                ApplyCredential(request, credential);

                using (var httpResponse = await client.Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var text = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var response = new Response
                    {
                        Status = (int)httpResponse.StatusCode,
                        Body = text,
                        Headers = httpResponse.Headers,
                    };

                    if (!response.Ok) throw BuildError(response);
                    return string.IsNullOrEmpty(text)
                        ? new Dictionary<string, object>()
                        : PraxJson.ParseObject(text);
                }
            }
        }

        /// <summary>Downloads raw bytes (file content). Bypasses JSON parsing.</summary>
        internal static async Task<byte[]> GetBytesAsync(
            PraxsuiteClient client, string url, AuthMode authMode, CancellationToken ct = default)
        {
            var credential = await client.ResolveCredentialAsync(authMode, ct).ConfigureAwait(false);

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyCredential(request, credential);

                using (var httpResponse = await client.Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        throw BuildError(new Response
                        {
                            Status = (int)httpResponse.StatusCode,
                            Body = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
                            Headers = httpResponse.Headers,
                        });
                    }
                    return await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        // ------------------------------------------------------------------ errors

        /// <summary>
        /// Maps a non-2xx response to a typed error.
        ///
        /// The gateway is not uniform: /query returns a bare {error:{code,message,details}},
        /// /auth/* returns the platform envelope {isSuccess,message,errors,data}, and the files
        /// controller returns a bare {error:"..."}. All three are handled so callers see one
        /// consistent exception.
        /// </summary>
        internal static PraxException BuildError(Response response)
        {
            string code = null, message = null;
            List<string> details = null;

            if (!string.IsNullOrEmpty(response.Body))
            {
                try
                {
                    var root = PraxJson.ParseObject(response.Body);

                    if (root.TryGetValue("error", out var errNode) &&
                        errNode is Dictionary<string, object> err)
                    {
                        code = AsString(err, "code");
                        message = AsString(err, "message");
                        details = AsStringList(err, "details");
                    }
                    else if (errNode is string plain)
                    {
                        message = plain;
                    }
                    else
                    {
                        message = AsString(root, "message");
                        details = AsStringList(root, "errors");
                    }
                }
                catch (PraxJsonException)
                {
                    // Non-JSON body - an HTML error page from an edge proxy, most likely.
                    message = response.Body.Length > 400 ? response.Body.Substring(0, 400) + "..." : response.Body;
                }
            }

            return new PraxException(
                string.IsNullOrEmpty(code) ? "HTTP_" + response.Status : code,
                string.IsNullOrEmpty(message) ? DescribeStatus(response.Status) : message,
                response.Status, details, response.Body);
        }

        private static string DescribeStatus(int status)
        {
            switch (status)
            {
                case 400: return "The gateway rejected the request as malformed.";
                case 401: return "Not authenticated. The API key or session token is missing, expired, or does not belong to this workspace.";
                case 403: return "Authenticated, but not permitted. Check the credential's or role's table scopes in API Gateway settings.";
                case 404: return "Not found. Verify the workspace id, and that you are pointed at the tier that hosts it - a workspace on another tier returns 404 here.";
                case 413: return "The payload is larger than the workspace plan allows.";
                case 429: return "Rate limited or out of plan allowance.";
                case 500: return "The gateway hit an internal error.";
                case 502:
                case 503:
                case 504: return "The gateway is unavailable or timed out upstream.";
                default: return "The gateway returned HTTP " + status + ".";
            }
        }

        /// <summary>
        /// Exponential backoff with +/-25% jitter, capped. Honours Retry-After when the gateway
        /// sends one, since it knows better than we do.
        /// </summary>
        private static int Backoff(Response response, int attempt)
        {
            var retryAfter = response?.Headers?.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue)
                    return (int)Math.Min(retryAfter.Delta.Value.TotalMilliseconds, MaxBackoffMs);
                if (retryAfter.Date.HasValue)
                {
                    var ms = (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds;
                    if (ms > 0) return (int)Math.Min(ms, MaxBackoffMs);
                }
            }

            var backoff = Math.Pow(2, attempt - 1) * 1000;
            var jitter = backoff * 0.25 * (NextJitter() * 2 - 1);
            return (int)Math.Min(Math.Max(backoff + jitter, 100), MaxBackoffMs);
        }

        // ----------------------------------------------------------------- helpers

        internal static string AsString(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static List<string> AsStringList(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value)) return null;
            if (!(value is List<object> list) || list.Count == 0) return null;

            var result = new List<string>(list.Count);
            foreach (var item in list)
                if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result.Count > 0 ? result : null;
        }
    }
}
