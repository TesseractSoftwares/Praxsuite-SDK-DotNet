using System;
using System.Collections.Generic;

namespace Praxsuite
{
    /// <summary>
    /// Turns a raw gateway response into <see cref="PraxRow"/> objects.
    ///
    /// The client API does this for you. This exists for callers holding a response they built
    /// themselves - <c>Prax.Data.ExecuteRawAsync</c>, <c>Prax.Endpoints.CallAsync</c>, or the
    /// server module, which lives in another assembly and so cannot reach the internal
    /// parsing helpers.
    /// </summary>
    public static class PraxRowReader
    {
        /// <summary>Reads the <c>data</c> array of a PraxQL response as rows.</summary>
        public static IReadOnlyList<PraxRow> ReadRows(Dictionary<string, object> response)
        {
            if (response == null) return Array.Empty<PraxRow>();

            if (!response.TryGetValue("data", out var node) || !(node is List<object> list))
                return Array.Empty<PraxRow>();

            var rows = new List<PraxRow>(list.Count);
            foreach (var item in list)
                if (item is Dictionary<string, object> map) rows.Add(new PraxRow(map));
            return rows;
        }

        /// <summary>Wraps a single object as a row - an endpoint response, for instance.</summary>
        public static PraxRow ReadRow(Dictionary<string, object> map)
        {
            return new PraxRow(map ?? new Dictionary<string, object>());
        }

        /// <summary>Reads the <c>meta</c> block of a PraxQL response.</summary>
        public static PraxRowPage ReadPage(Dictionary<string, object> response)
        {
            return PraxData.ParsePage(response ?? new Dictionary<string, object>());
        }
    }
}
