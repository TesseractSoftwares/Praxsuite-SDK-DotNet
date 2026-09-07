using System.Collections.Generic;
using NUnit.Framework;

namespace Praxsuite.Tests
{
    /// <summary>
    /// Mirrors <c>cases/event-bus.json</c> from the SDK conformance contract.
    ///
    /// Every case was either measured against the live hub on 2026-09-07 or exists because
    /// getting it wrong produces silently wrong behaviour rather than an exception - the class of
    /// bug this SDK's contract exists to catch.
    /// </summary>
    public class PraxBusTests
    {
        private const string Rs = "\u001e";
        private const string User = "729531eb-98ca-4cfd-bb79-452dbe177ca5";

        // ── bus keys ────────────────────────────────────────────────────────

        [Test]
        public void Folds_the_topic_and_leaves_the_instance()
        {
            // Both peers would be admitted either way, but two clients differing only in the case
            // of the topic would land in different groups and never see each other.
            Assert.AreEqual("office:HQ", PraxBusWire.NormalizeBusKey("Office:HQ"));
        }

        [Test]
        public void A_lowercase_key_is_unchanged()
        {
            Assert.AreEqual("channel:9f1c0f2e", PraxBusWire.NormalizeBusKey("channel:9f1c0f2e"));
        }

        [Test]
        public void A_key_without_an_instance_still_folds()
        {
            Assert.AreEqual("lobby", PraxBusWire.NormalizeBusKey("LOBBY"));
        }

        [Test]
        public void Whitespace_is_trimmed()
        {
            Assert.AreEqual("office:hq", PraxBusWire.NormalizeBusKey("  office:hq  "));
        }

        [Test]
        public void User_self_passes_through_untouched()
        {
            Assert.AreEqual("user:self", PraxBusWire.NormalizeBusKey("user:self"));
        }

        [Test]
        public void An_empty_key_is_rejected_before_the_round_trip()
        {
            Assert.Throws<PraxException>(() => PraxBusWire.RequireValidBusKey("   "));
        }

        [Test]
        public void A_key_containing_ws_is_rejected()
        {
            Assert.Throws<PraxException>(() => PraxBusWire.RequireValidBusKey("x:ws:something"));
        }

        // ── frames ──────────────────────────────────────────────────────────

        [Test]
        public void The_handshake_is_byte_exact()
        {
            Assert.AreEqual("{\"protocol\":\"json\",\"version\":1}", PraxBusWire.HandshakeFrame);
        }

        [Test]
        public void A_single_frame_splits_to_one_message()
        {
            string remainder;
            var frames = PraxBusWire.SplitFrames("{\"type\":6}" + Rs, out remainder);

            Assert.AreEqual(1, frames.Count);
            Assert.AreEqual(string.Empty, remainder);
        }

        [Test]
        public void Two_coalesced_frames_split_into_two()
        {
            // One physical message can carry several frames. Parsing the whole buffer breaks
            // under exactly the load the bus exists for.
            string remainder;
            var frames = PraxBusWire.SplitFrames(
                "{\"type\":6}" + Rs + "{\"type\":3,\"invocationId\":\"1\",\"result\":null}" + Rs,
                out remainder);

            Assert.AreEqual(2, frames.Count);
            Assert.AreEqual("{\"type\":6}", frames[0]);
        }

        [Test]
        public void A_trailing_partial_frame_is_buffered_not_parsed()
        {
            string remainder;
            var frames = PraxBusWire.SplitFrames(
                "{\"type\":6}" + Rs + "{\"type\":3,\"invoca", out remainder);

            Assert.AreEqual(1, frames.Count);
            Assert.AreEqual("{\"type\":3,\"invoca", remainder);
        }

        [Test]
        public void An_empty_segment_is_dropped()
        {
            string remainder;
            var frames = PraxBusWire.SplitFrames(Rs + "{\"type\":6}" + Rs, out remainder);

            Assert.AreEqual(1, frames.Count);
        }

        // ── invocations ─────────────────────────────────────────────────────

        [Test]
        public void Join_without_a_ticket_sends_an_explicit_null()
        {
            var frame = PraxBusWire.BuildInvocation(
                "1", "JoinBus", new List<object> { "office:hq", null });
            var parsed = PraxJson.ParseObject(frame);

            Assert.AreEqual(1, PraxBusWire.ToInt(parsed["type"]));
            Assert.AreEqual("JoinBus", parsed["target"]);

            var args = (List<object>)parsed["arguments"];
            Assert.AreEqual(2, args.Count);
            Assert.AreEqual("office:hq", args[0]);
            Assert.IsNull(args[1]);
        }

        [Test]
        public void The_invocation_id_is_a_string()
        {
            // SignalR matches completions on it by value; a numeric id never matches.
            var parsed = PraxJson.ParseObject(
                PraxBusWire.BuildInvocation("5", "LeaveBus", new List<object> { "x:y" }));

            Assert.IsInstanceOf<string>(parsed["invocationId"]);
        }

        [Test]
        public void Publish_carries_key_event_and_payload_in_that_order()
        {
            var parsed = PraxJson.ParseObject(PraxBusWire.BuildInvocation(
                "3", "Publish", new List<object> { "office:hq", "move", 42 }));

            var args = (List<object>)parsed["arguments"];
            Assert.AreEqual("office:hq", args[0]);
            Assert.AreEqual("move", args[1]);
        }

        // ── completions ─────────────────────────────────────────────────────

        [Test]
        public void A_join_completion_carries_retained_peers()
        {
            var result = PraxBusWire.ParseBusResult(PraxJson.ParseObject(
                "{\"type\":3,\"invocationId\":\"1\",\"result\":{\"ok\":true,\"error\":null," +
                "\"peers\":[{\"userId\":\"" + User + "\",\"event\":\"move\"," +
                "\"payload\":{\"x\":1,\"y\":2}}]}}"));

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, result.Peers.Count);
            Assert.AreEqual("move", result.Peers[0].Event);
        }

        [Test]
        public void A_rejection_arrives_inside_a_successful_completion()
        {
            // Not an exception and not an HTTP status. Code that only inspects SignalR's error
            // channel reports every denied join as a success.
            var result = PraxBusWire.ParseBusResult(PraxJson.ParseObject(
                "{\"type\":3,\"invocationId\":\"3\",\"result\":{\"ok\":false," +
                "\"error\":\"unknown_topic\",\"peers\":[]}}"));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("unknown_topic", result.Error);
            Assert.IsFalse(result.IsTransportError);
        }

        [Test]
        public void Zero_recipients_is_success_not_failure()
        {
            var result = PraxBusWire.ParseBusResult(PraxJson.ParseObject(
                "{\"type\":3,\"invocationId\":\"2\",\"result\":{\"ok\":true,\"error\":null," +
                "\"recipients\":0}}"));

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Recipients);
        }

        [Test]
        public void A_void_result_is_ok()
        {
            // LeaveBus returns null. Reading result.ok on it must not report failure.
            var result = PraxBusWire.ParseBusResult(PraxJson.ParseObject(
                "{\"type\":3,\"invocationId\":\"7\",\"result\":null}"));

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Peers.Count);
        }

        [Test]
        public void A_hub_error_is_kept_apart_from_a_policy_rejection()
        {
            var result = PraxBusWire.ParseBusResult(PraxJson.ParseObject(
                "{\"type\":3,\"invocationId\":\"9\",\"error\":\"An unexpected error occurred.\"}"));

            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.IsTransportError);
        }

        [Test]
        public void Every_hub_error_code_has_a_sentence_worth_reading()
        {
            var codes = new[]
            {
                "invalid_bus_key", "unknown_topic", "denied", "invalid_ticket",
                "bus_full_or_too_many_buses", "not_a_member", "invalid_event_name",
                "payload_too_large", "rate_limited",
            };

            foreach (var code in codes)
                Assert.Greater(PraxBusWire.DescribeError(code).Length, 20, code);
        }

        // ── urls ────────────────────────────────────────────────────────────

        [Test]
        public void The_hub_has_no_workspace_segment()
        {
            // The workspace comes from the token; adding a segment 404s.
            var url = PraxBusWire.NegotiateUrl("https://gateway.example.test");

            Assert.AreEqual(
                "https://gateway.example.test/hubs/event-bus/negotiate?negotiateVersion=1", url);
        }

        [Test]
        public void The_socket_url_upgrades_the_scheme_and_carries_the_token()
        {
            var url = PraxBusWire.SocketUrl("https://gateway.example.test", "abc.def");

            Assert.AreEqual(
                "wss://gateway.example.test/hubs/event-bus?access_token=abc.def", url);
        }

        [Test]
        public void A_plaintext_base_url_produces_a_plaintext_socket_url()
        {
            Assert.IsTrue(PraxBusWire.SocketUrl("http://localhost:5000", "t").StartsWith("ws://"));
        }

        // ── channels ────────────────────────────────────────────────────────

        [Test]
        public void Topic_composes_the_key_and_returns_the_same_channel_object()
        {
            using (var client = new PraxsuiteClient("1eb92f32-d628-4656-8c64-cd0d43c9869d",
                       "https://gateway.example.test",
                       "pk_live_" + "fedcba9876543210fedcba9876543210"))
            {
                var a = client.Bus.Topic("Office").Channel("hq");
                var b = client.Bus.Channel("office:hq");

                Assert.AreSame(a, b);
                Assert.AreEqual("office:hq", a.Key);
                Assert.AreEqual("office", a.Topic);
                Assert.AreEqual("hq", a.Instance);
            }
        }

        [Test]
        public void Self_is_the_reserved_user_bus()
        {
            using (var client = new PraxsuiteClient("1eb92f32-d628-4656-8c64-cd0d43c9869d",
                       "https://gateway.example.test",
                       "pk_live_" + "fedcba9876543210fedcba9876543210"))
            {
                Assert.AreEqual("user:self", client.Bus.Self.Key);
            }
        }
    }
}
