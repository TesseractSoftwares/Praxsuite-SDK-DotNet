using System.Collections.Generic;
using NUnit.Framework;

namespace Praxsuite.Tests
{
    public class PraxJsonTests
    {
        [Test]
        public void Parses_nested_objects_and_arrays()
        {
            var root = PraxJson.ParseObject(
                "{\"data\":[{\"Name\":\"Aria\",\"Score\":120}],\"meta\":{\"total\":7}}");

            var data = (List<object>)root["data"];
            var first = (Dictionary<string, object>)data[0];

            Assert.AreEqual("Aria", first["Name"]);
            Assert.AreEqual(120L, first["Score"]);
            Assert.AreEqual(7L, ((Dictionary<string, object>)root["meta"])["total"]);
        }

        [Test]
        public void Distinguishes_integers_from_floats()
        {
            var root = PraxJson.ParseObject("{\"i\":42,\"f\":42.5,\"e\":1e3,\"neg\":-7}");

            Assert.IsInstanceOf<long>(root["i"]);
            Assert.IsInstanceOf<double>(root["f"]);
            Assert.IsInstanceOf<double>(root["e"]);
            Assert.AreEqual(-7L, root["neg"]);
        }

        [Test]
        public void Round_trips_escapes()
        {
            const string awkward = "line\nbreak \"quoted\" back\\slash\ttab";
            var json = PraxJson.Serialize(new Dictionary<string, object> { { "v", awkward } });
            var parsed = PraxJson.ParseObject(json);

            Assert.AreEqual(awkward, parsed["v"]);
        }

        [Test]
        public void Round_trips_astral_emoji()
        {
            // Player display names contain emoji constantly. A parser that mangles surrogate
            // pairs corrupts them silently, so this is worth pinning down.
            const string name = "Aria \U0001F680\U0001F1E8\U0001F1F1";

            var json = PraxJson.Serialize(new Dictionary<string, object> { { "name", name } });
            Assert.AreEqual(name, PraxJson.ParseObject(json)["name"]);

            // And when the server sends them as \u escapes rather than raw UTF-8.
            var escaped = PraxJson.ParseObject("{\"name\":\"\\ud83d\\ude80\"}");
            Assert.AreEqual("\U0001F680", escaped["name"]);
        }

        [Test]
        public void Handles_null_true_false_and_empty_containers()
        {
            var root = PraxJson.ParseObject(
                "{\"n\":null,\"t\":true,\"f\":false,\"arr\":[],\"obj\":{}}");

            Assert.IsNull(root["n"]);
            Assert.AreEqual(true, root["t"]);
            Assert.AreEqual(false, root["f"]);
            Assert.AreEqual(0, ((List<object>)root["arr"]).Count);
            Assert.AreEqual(0, ((Dictionary<string, object>)root["obj"]).Count);
        }

        [Test]
        public void Serializes_dictionaries_as_objects_not_pair_arrays()
        {
            // Regression guard: IDictionary also satisfies IEnumerable, so an ordering mistake
            // in the writer's type switch would emit [{"Key":..,"Value":..}] and every mutation
            // the SDK sends would be rejected by the gateway.
            var json = PraxJson.Serialize(new Dictionary<string, object> { { "Score", 10 } });

            Assert.AreEqual("{\"Score\":10}", json);
        }

        [Test]
        public void Rejects_malformed_input()
        {
            Assert.Throws<PraxJsonException>(() => PraxJson.Parse("{\"a\":}"));
            Assert.Throws<PraxJsonException>(() => PraxJson.Parse("{\"unterminated\":\"x"));
            Assert.Throws<PraxJsonException>(() => PraxJson.Parse("[1,2"));
        }
    }
}
