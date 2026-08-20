using System;
using System.Collections;
using System.Collections.Generic;

namespace Praxsuite
{
    /// <summary>
    /// A where condition, or a group of them.
    ///
    /// Only the operators the gateway's PraxQL parser actually accepts are exposed here.
    /// Verified against Praxsuite-Backend-Core ApiGateway/Services/PraxQLParser.cs:
    ///   eq, neq, gt, gte, lt, lte, like, ilike, in, is, between, contains, textsearch
    ///
    /// There is deliberately no IsNull/NotIn/StartsWith/EndsWith helper: those look
    /// reasonable but the server rejects them, so offering them would only produce
    /// runtime 400s. Use <see cref="IsNull"/> and <see cref="IsNotNull"/>, which compile
    /// down to the supported <c>is</c> operator, and <see cref="Like"/> with an explicit
    /// pattern for prefix and suffix matching.
    /// </summary>
    public class PraxFilter
    {
        internal string Field;
        internal string Op;
        internal object Value;
        internal bool HasValue;
        internal List<PraxFilter> AnyOf;   // OR group
        internal List<PraxFilter> AllOf;   // AND group

        private PraxFilter() { }

        // ------------------------------------------------------------- comparison

        /// <summary>field == value</summary>
        public static PraxFilter Eq(string field, object value) => Simple(field, "eq", value);

        /// <summary>field != value</summary>
        public static PraxFilter Neq(string field, object value) => Simple(field, "neq", value);

        /// <summary>field &gt; value</summary>
        public static PraxFilter Gt(string field, object value) => Simple(field, "gt", value);

        /// <summary>field &gt;= value</summary>
        public static PraxFilter Gte(string field, object value) => Simple(field, "gte", value);

        /// <summary>field &lt; value</summary>
        public static PraxFilter Lt(string field, object value) => Simple(field, "lt", value);

        /// <summary>field &lt;= value</summary>
        public static PraxFilter Lte(string field, object value) => Simple(field, "lte", value);

        // ------------------------------------------------------------------- text

        /// <summary>
        /// SQL LIKE, case-sensitive. You supply the wildcards: <c>"Sword%"</c> for a prefix
        /// match, <c>"%blade"</c> for a suffix, <c>"%fire%"</c> for anywhere.
        /// </summary>
        public static PraxFilter Like(string field, string pattern) => Simple(field, "like", pattern);

        /// <summary>Case-insensitive LIKE. Same wildcard rules as <see cref="Like"/>.</summary>
        public static PraxFilter ILike(string field, string pattern) => Simple(field, "ilike", pattern);

        /// <summary>Substring match, no wildcards needed.</summary>
        public static PraxFilter Contains(string field, string text) => Simple(field, "contains", text);

        /// <summary>Full-text search over the column.</summary>
        public static PraxFilter TextSearch(string field, string query) => Simple(field, "textsearch", query);

        // ------------------------------------------------------------------- sets

        /// <summary>field IN (values). Pass at least one value.</summary>
        public static PraxFilter In(string field, IEnumerable values)
        {
            var list = new List<object>();
            if (values != null)
                foreach (var v in values) list.Add(v);

            if (list.Count == 0)
                throw new ArgumentException(
                    "In(\"" + field + "\", ...) needs at least one value. An empty IN list matches " +
                    "nothing, which is almost never what a caller means - skip the filter instead.",
                    nameof(values));

            return Simple(field, "in", list);
        }

        /// <summary>field IN (values), for a fixed set.</summary>
        public static PraxFilter In(string field, params object[] values) => In(field, (IEnumerable)values);

        /// <summary>field BETWEEN low AND high, inclusive.</summary>
        public static PraxFilter Between(string field, object low, object high)
        {
            return Simple(field, "between", new List<object> { low, high });
        }

        // ------------------------------------------------------------------- null

        /// <summary>field IS NULL.</summary>
        public static PraxFilter IsNull(string field) => Simple(field, "is", null);

        /// <summary>
        /// field IS NOT NULL.
        /// Expressed as <c>neq null</c>, since the gateway's <c>is</c> operator only tests
        /// for null.
        /// </summary>
        public static PraxFilter IsNotNull(string field) => Simple(field, "neq", null);

        // ------------------------------------------------------------------ groups

        /// <summary>Matches when any child matches (OR).</summary>
        public static PraxFilter Any(params PraxFilter[] filters)
        {
            return new PraxFilter { AnyOf = Collect(filters, nameof(Any)) };
        }

        /// <summary>
        /// Matches when every child matches (AND). Top-level filters are already ANDed, so
        /// this is only needed to nest an AND group inside an <see cref="Any"/>.
        /// </summary>
        public static PraxFilter All(params PraxFilter[] filters)
        {
            return new PraxFilter { AllOf = Collect(filters, nameof(All)) };
        }

        private static List<PraxFilter> Collect(PraxFilter[] filters, string caller)
        {
            var list = new List<PraxFilter>();
            if (filters != null)
                foreach (var f in filters)
                    if (f != null) list.Add(f);

            if (list.Count == 0)
                throw new ArgumentException(caller + "() needs at least one filter.", nameof(filters));

            return list;
        }

        private static PraxFilter Simple(string field, string op, object value)
        {
            if (string.IsNullOrWhiteSpace(field))
                throw new ArgumentException("A column name is required.", nameof(field));

            return new PraxFilter
            {
                Field = field.Trim(),
                Op = op,
                Value = value,
                HasValue = true
            };
        }

        /// <summary>Converts to the wire shape the gateway expects.</summary>
        internal Dictionary<string, object> ToWire()
        {
            if (AnyOf != null)
                return new Dictionary<string, object> { { "or", ToWireList(AnyOf) } };

            if (AllOf != null)
                return new Dictionary<string, object> { { "and", ToWireList(AllOf) } };

            var map = new Dictionary<string, object>
            {
                { "field", Field },
                { "op", Op }
            };
            if (HasValue) map["value"] = Value;
            return map;
        }

        internal static List<object> ToWireList(IEnumerable<PraxFilter> filters)
        {
            var list = new List<object>();
            if (filters == null) return list;
            foreach (var f in filters)
                if (f != null) list.Add(f.ToWire());
            return list;
        }
    }
}
