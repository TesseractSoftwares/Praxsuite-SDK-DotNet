using System.Collections.Generic;
using NUnit.Framework;

namespace Praxsuite.Tests
{
    /// <summary>
    /// Checks the wire shapes the SDK produces and parses. These are the contracts against the
    /// gateway, so they are worth pinning even though they look mechanical - the Lua SDK's
    /// broken Count() was exactly a mismatch of this kind.
    /// </summary>
    public class PraxQueryTests
    {
        [Test]
        public void Filters_emit_the_gateway_wire_shape()
        {
            var wire = PraxFilter.Gt("Score", 100).ToWire();

            Assert.AreEqual("Score", wire["field"]);
            Assert.AreEqual("gt", wire["op"]);
            Assert.AreEqual(100, wire["value"]);
        }

        [Test]
        public void IsNull_uses_the_is_operator_and_IsNotNull_uses_neq()
        {
            // The gateway's parser accepts "is" only for null tests; there is no "isnotnull".
            var isNull = PraxFilter.IsNull("DeletedAt").ToWire();
            Assert.AreEqual("is", isNull["op"]);
            Assert.IsNull(isNull["value"]);

            var isNotNull = PraxFilter.IsNotNull("DeletedAt").ToWire();
            Assert.AreEqual("neq", isNotNull["op"]);
        }

        [Test]
        public void Or_groups_nest_under_an_or_key()
        {
            var wire = PraxFilter.Any(
                PraxFilter.Eq("Rarity", "legendary"),
                PraxFilter.Eq("Rarity", "epic")).ToWire();

            var children = (List<object>)wire["or"];
            Assert.AreEqual(2, children.Count);
            Assert.AreEqual("Rarity", ((Dictionary<string, object>)children[0])["field"]);
        }

        [Test]
        public void Empty_In_lists_are_rejected_rather_than_matching_nothing()
        {
            Assert.Throws<System.ArgumentException>(() => PraxFilter.In("Id", new string[0]));
        }

        [Test]
        public void Unsupported_aggregate_functions_are_rejected_at_build_time()
        {
            var query = new PraxQuery(null, "Scores");

            // "median" is plausible but the gateway does not implement it. Failing here beats
            // a 400 from the server at runtime.
            Assert.Throws<System.ArgumentException>(() => query.Aggregate("median", "Score", "m"));
            Assert.DoesNotThrow(() => query.Aggregate("sum", "Score", "total"));
        }

        [Test]
        public void Page_parsing_reads_total_not_totalCount()
        {
            // The gateway's PraxQLResultMeta serialises this field as "total". Reading
            // "totalCount" instead is why the Lua SDK's Count() always returned zero.
            var body = PraxJson.ParseObject(
                "{\"data\":[{\"ID\":\"a\"},{\"ID\":\"b\"}]," +
                "\"meta\":{\"limit\":50,\"offset\":0,\"count\":2,\"total\":137,\"durationMs\":12}}");

            var page = PraxRowReader.ReadPage(body);

            Assert.AreEqual(2, page.Rows.Count);
            Assert.AreEqual(2, page.Count);
            Assert.AreEqual(50, page.Limit);
            Assert.AreEqual(137L, page.Total);
            Assert.AreEqual(12L, page.DurationMs);
            Assert.IsTrue(page.HasMore);
        }

        [Test]
        public void Rows_expose_typed_accessors_with_forgiving_fallbacks()
        {
            var row = PraxRowReader.ReadRow(PraxJson.ParseObject(
                "{\"ID\":\"row-1\",\"Score\":420,\"Ratio\":0.75,\"Active\":true," +
                "\"Name\":\"Aria\",\"When\":\"2026-08-19T10:30:00Z\"}"));

            Assert.AreEqual("row-1", row.Id);
            Assert.AreEqual(420, row.GetInt("Score"));
            Assert.AreEqual(0.75f, row.GetFloat("Ratio"), 0.0001f);
            Assert.IsTrue(row.GetBool("Active"));
            Assert.AreEqual("Aria", row.GetString("name"), "column lookup should be case-insensitive");
            Assert.IsNotNull(row.GetDate("When"));

            // A column added to the table after this save was written must not crash the game.
            Assert.AreEqual(0, row.GetInt("ColumnAddedLater"));
            Assert.AreEqual("fallback", row.GetString("Missing", "fallback"));
            Assert.IsFalse(row.Has("Missing"));
        }

        private class SaveData
        {
            public string Name;
            public int Score;
            public bool Active;
            public float Ratio;
        }

        [Test]
        public void Rows_project_onto_plain_classes()
        {
            var row = PraxRowReader.ReadRow(PraxJson.ParseObject(
                "{\"Name\":\"Aria\",\"Score\":420,\"Active\":true,\"Ratio\":0.5}"));

            var save = row.As<SaveData>();

            Assert.AreEqual("Aria", save.Name);
            Assert.AreEqual(420, save.Score);
            Assert.IsTrue(save.Active);
            Assert.AreEqual(0.5f, save.Ratio, 0.0001f);
        }

        [Test]
        public void Mutation_parsing_reads_affected_rows_and_returned_data()
        {
            var body = PraxJson.ParseObject(
                "{\"affectedRows\":1,\"data\":[{\"ID\":\"new-row\"}]," +
                "\"meta\":{\"type\":\"insert\",\"durationMs\":8}}");

            var result = PraxData.ParseMutation(body);

            Assert.AreEqual(1, result.AffectedRows);
            Assert.AreEqual("new-row", result.Row.Id);
            Assert.AreEqual(8L, result.DurationMs);
        }

        [Test]
        public void Routes_use_the_frontdoor_short_form()
        {
            const string ws = "1eb92f32-d628-4656-8c64-cd0d43c9869d";

            Assert.AreEqual("https://gateway.praxsuite.com/" + ws + "/query",
                PraxRoutes.Query("https://gateway.praxsuite.com", ws));

            Assert.AreEqual("https://gateway.praxsuite.com/" + ws + "/auth/login",
                PraxRoutes.Auth("https://gateway.praxsuite.com", ws, "login"));

            // Trailing slashes and a missing scheme are both normalised away.
            Assert.AreEqual("https://gateway.praxsuite.com/" + ws + "/schema",
                PraxRoutes.Schema("gateway.praxsuite.com/", ws));
        }

        [Test]
        public void Errors_classify_retryable_and_terminal_conditions()
        {
            Assert.IsTrue(new PraxException("RATE_LIMIT_EXCEEDED", "slow down", 429).IsTransient);
            Assert.IsTrue(new PraxException("NETWORK_ERROR", "offline").IsTransient);
            Assert.IsTrue(new PraxException("HTTP_503", "unavailable", 503).IsTransient);

            // Quota exhaustion is a 429 but retrying cannot fix it - only a plan change can.
            var quota = new PraxException("QUOTA_EXCEEDED", "out of calls", 429);
            Assert.IsTrue(quota.IsQuotaExceeded);
            Assert.IsFalse(quota.IsTransient);

            Assert.IsFalse(new PraxException("FORBIDDEN", "no scope", 403).IsTransient);
        }
    }
}
