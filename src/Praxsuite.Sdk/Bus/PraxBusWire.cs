using System;
using System.Collections.Generic;

namespace Praxsuite
{
    /// <summary>
    /// The Event Bus wire format: SignalR's JSON hub protocol, version 1.
    ///
    /// Everything here is pure and synchronous so it can be tested with no socket, which is how
    /// the conformance cases in <c>cases/event-bus.json</c> are run.
    ///
    /// The protocol is spoken directly rather than through Microsoft.AspNetCore.SignalR.Client.
    /// The surface used here is four message types wide, this SDK ships with zero dependencies
    /// (which is what lets it load into Godot and Unity), and the official client defaults
    /// <c>withCredentials</c> to true - the one setting that makes the handshake fail against our
    /// gateway, because the CORS spec forbids answering a credentialed request with the wildcard
    /// origin the front door sends.
    /// </summary>
    public static class PraxBusWire
    {
        /// <summary>ASCII record separator. SignalR terminates every frame with it.</summary>
        public const char RecordSeparator = '\u001e';

        /// <summary>
        /// The handshake, byte for byte. SignalR compares it literally - a trailing newline or a
        /// space after a colon fails it, with an error that does not say so.
        /// </summary>
        public const string HandshakeFrame = "{\"protocol\":\"json\",\"version\":1}";

        /// <summary>
        /// The hub's path. There is no workspace segment: the workspace comes from the token.
        /// </summary>
        public const string BusPath = "/hubs/event-bus";

        public const int MessageInvocation = 1;
        public const int MessageCompletion = 3;
        public const int MessagePing = 6;
        public const int MessageClose = 7;

        /// <summary>
        /// Splits a received buffer into whole frames, returning the trailing fragment.
        ///
        /// Two things go wrong without this. A single physical message can carry SEVERAL frames,
        /// so parsing the whole buffer as JSON throws exactly when traffic picks up - the load the
        /// bus exists for. And a transport may split one frame across two reads, so the tail is
        /// kept rather than parsed or discarded.
        /// </summary>
        public static List<string> SplitFrames(string buffer, out string remainder)
        {
            var frames = new List<string>();
            if (string.IsNullOrEmpty(buffer))
            {
                remainder = string.Empty;
                return frames;
            }

            var parts = buffer.Split(RecordSeparator);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length > 0) frames.Add(parts[i]);
            }

            remainder = parts[parts.Length - 1];
            return frames;
        }

        /// <summary>Wraps a frame for sending.</summary>
        public static string Frame(string payload)
        {
            return payload + RecordSeparator;
        }

        /// <summary>
        /// Folds a bus key the way the server does: the TOPIC segment to lowercase, the instance
        /// untouched.
        ///
        /// <c>BusAddress.ForCaller</c> folds the topic both when it resolves the topic and when it
        /// builds the SignalR group name, so <c>Office:hq</c> and <c>office:hq</c> are one bus.
        /// Folding the whole key instead would merge <c>office:HQ</c> and <c>office:hq</c>, which
        /// are two genuinely different buses. Fold the same half the server folds and neither
        /// mistake is possible.
        /// </summary>
        public static string NormalizeBusKey(string busKey)
        {
            if (string.IsNullOrEmpty(busKey)) return string.Empty;

            var key = busKey.Trim();
            var separator = key.IndexOf(':');
            if (separator <= 0) return key.ToLowerInvariant();

            return key.Substring(0, separator).ToLowerInvariant() + key.Substring(separator);
        }

        /// <summary>
        /// Rejects keys the server would reject anyway, without spending a round trip on it.
        /// </summary>
        public static string RequireValidBusKey(string busKey)
        {
            var key = NormalizeBusKey(busKey);

            if (string.IsNullOrEmpty(key))
            {
                throw new PraxException("INVALID_BUS_KEY",
                    "A bus key is required. It looks like \"topic:instance\", e.g. \"office:hq\".");
            }

            // The group name is built by concatenation, so a key carrying the separator could
            // climb out of its own segment and name another workspace's group.
            if (key.IndexOf("ws:", StringComparison.Ordinal) >= 0)
            {
                throw new PraxException("INVALID_BUS_KEY",
                    "A bus key may not contain \"ws:\" (got \"" + busKey + "\"). The server refuses it.");
            }

            if (key.Length > 200)
            {
                throw new PraxException("INVALID_BUS_KEY",
                    "Bus key is too long (" + key.Length + " characters).");
            }

            return key;
        }

        /// <summary>
        /// Builds an invocation frame. <paramref name="invocationId"/> is a STRING - SignalR
        /// matches completions on it by value, and a numeric id never matches.
        /// </summary>
        public static string BuildInvocation(string invocationId, string target, List<object> args)
        {
            var message = new Dictionary<string, object>
            {
                { "type", MessageInvocation },
                { "invocationId", invocationId },
                { "target", target },
                { "arguments", args ?? new List<object>() },
            };
            return PraxJson.Serialize(message);
        }

        /// <summary>
        /// Reads a completion frame.
        ///
        /// The trap this exists for: the hub answers a REJECTED call with a SUCCESSFUL completion
        /// whose result carries <c>ok:false</c>. Code that only inspects SignalR's <c>error</c>
        /// field reports every denied join as a success. And <c>LeaveBus</c> is void, so its
        /// result is literally <c>null</c>.
        /// </summary>
        public static PraxBusResult ParseBusResult(Dictionary<string, object> message)
        {
            var result = new PraxBusResult();
            if (message == null) return result;

            object error;
            if (message.TryGetValue("error", out error) && error is string && ((string)error).Length > 0)
            {
                result.Ok = false;
                result.Error = (string)error;
                result.IsTransportError = true;
                return result;
            }

            object payload;
            if (!message.TryGetValue("result", out payload) || !(payload is Dictionary<string, object>))
                return result; // void, e.g. LeaveBus

            var body = (Dictionary<string, object>)payload;

            object ok;
            result.Ok = !(body.TryGetValue("ok", out ok) && ok is bool && !(bool)ok);

            object code;
            if (body.TryGetValue("error", out code) && code is string) result.Error = (string)code;

            object recipients;
            if (body.TryGetValue("recipients", out recipients)) result.Recipients = ToInt(recipients);

            object peers;
            if (body.TryGetValue("peers", out peers) && peers is List<object>)
            {
                foreach (var entry in (List<object>)peers)
                {
                    var peer = entry as Dictionary<string, object>;
                    if (peer == null) continue;
                    result.Peers.Add(new PraxBusPeerState
                    {
                        UserId = AsString(peer, "userId"),
                        Event = AsString(peer, "event"),
                        Payload = Get(peer, "payload"),
                    });
                }
            }

            return result;
        }

        /// <summary>Negotiate: a zero-length POST with the end-user JWT as a bearer token.</summary>
        public static string NegotiateUrl(string baseUrl)
        {
            return PraxRoutes.NormalizeBaseUrl(baseUrl) + BusPath + "/negotiate?negotiateVersion=1";
        }

        /// <summary>
        /// The WebSocket URL, with the session token in the query string.
        ///
        /// The token goes in the query because a browser WebSocket cannot set an Authorization
        /// header, and the hub accepts <c>access_token</c> for exactly that reason. Keeping one
        /// placement across every runtime is what makes this SDK's transport identical in a
        /// console app, in Godot and in a browser build.
        /// </summary>
        public static string SocketUrl(string baseUrl, string accessToken)
        {
            var http = PraxRoutes.NormalizeBaseUrl(baseUrl);
            string ws;
            if (http.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ws = "wss://" + http.Substring("https://".Length);
            else if (http.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ws = "ws://" + http.Substring("http://".Length);
            else
                ws = http;

            return ws + BusPath + "?access_token=" + Uri.EscapeDataString(accessToken ?? string.Empty);
        }

        /// <summary>
        /// Turns a hub error code into a sentence worth reading. The codes themselves are stable
        /// and are what callers should branch on; these strings are not.
        /// </summary>
        public static string DescribeError(string code)
        {
            switch (code)
            {
                case "unknown_topic":
                    return "That topic is not declared in this workspace. Buses are never auto-created - " +
                           "declare the topic under API Gateway / Event Bus first.";
                case "denied":
                    return "The topic refused this user. Check its access mode: Workspace, Roles (read " +
                           "straight from the JWT), or Grants (which needs a grant on this exact bus instance).";
                case "invalid_ticket":
                    return "The ticket was missing, expired, or minted for a different user, workspace or bus.";
                case "not_a_member":
                    return "Publish to a bus this connection has not joined. Join it first - membership is " +
                           "the authorization check on the publish path.";
                case "invalid_bus_key":
                    return "The key is malformed, or it named another user's \"user:\" bus. Only \"user:self\" " +
                           "is addressable.";
                case "invalid_event_name":
                    return "The event name was empty or too long.";
                case "payload_too_large":
                    return "The payload is over this topic's byte limit.";
                case "bus_full_or_too_many_buses":
                    return "The bus is at its peer limit, or this connection already holds as many buses as it may.";
                case "rate_limited":
                    return "Too many publishes. The limit is priced by RECIPIENTS, so a large bus exhausts it " +
                           "faster than a small one.";
                default:
                    return string.IsNullOrEmpty(code) ? "The bus refused the call." : code;
            }
        }

        internal static object Get(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value : null;
        }

        internal static string AsString(Dictionary<string, object> source, string key)
        {
            var value = Get(source, key);
            return value as string ?? string.Empty;
        }

        internal static int ToInt(object value)
        {
            if (value is int) return (int)value;
            if (value is long) return (int)(long)value;
            if (value is double) return (int)(double)value;
            if (value is decimal) return (int)(decimal)value;

            int parsed;
            return value != null && int.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }
    }

    /// <summary>One peer's last known state within a bus.</summary>
    public sealed class PraxBusPeerState
    {
        public string UserId { get; set; }
        public string Event { get; set; }

        /// <summary>UNTRUSTED. Written by another user and relayed without inspection.</summary>
        public object Payload { get; set; }
    }

    /// <summary>What the hub returns from JoinBus, Publish and LeaveBus, in one shape.</summary>
    public sealed class PraxBusResult
    {
        /// <summary>
        /// False for a policy rejection. Rejections arrive INSIDE a successful completion, which
        /// is why this exists rather than an exception.
        /// </summary>
        public bool Ok { get; set; } = true;

        /// <summary>One of the hub's error codes, or null.</summary>
        public string Error { get; set; }

        /// <summary>
        /// JoinBus: every peer's last retained message. Empty when the topic does not retain.
        /// </summary>
        public List<PraxBusPeerState> Peers { get; } = new List<PraxBusPeerState>();

        /// <summary>
        /// Publish: how many OTHER connections it reached. Zero is success, not failure - it
        /// means the message went out and nobody was joined.
        /// </summary>
        public int Recipients { get; set; }

        /// <summary>
        /// True when SignalR itself failed the call - a server fault rather than a policy
        /// decision. Kept apart because the two want different handling.
        /// </summary>
        public bool IsTransportError { get; set; }
    }

    /// <summary>
    /// An event relayed from another peer.
    ///
    /// <see cref="FromUserId"/> is stamped by the server from the validated token, never read
    /// from the payload, so a peer cannot claim to be somebody else.
    /// </summary>
    public sealed class PraxBusEvent
    {
        public string Bus { get; set; }
        public string FromUserId { get; set; }
        public string Event { get; set; }

        /// <summary>
        /// UNTRUSTED. The bus relays opaque JSON between USERS and parses none of it, so every
        /// server-side sanitizer is bypassed. Rendering this as HTML is a stored XSS delivered
        /// peer to peer.
        /// </summary>
        public object Payload { get; set; }
    }
}
