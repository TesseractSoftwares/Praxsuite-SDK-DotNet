using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// A fluent read query. Build it, then await a terminal method
    /// (<see cref="ToListAsync"/>, <see cref="FirstAsync"/>, <see cref="CountAsync"/>, ...).
    ///
    /// <code>
    /// var top = await Prax.Data.From("Scores")
    ///     .Select("PlayerName", "Score")
    ///     .Where(PraxFilter.Gt("Score", 0))
    ///     .OrderByDescending("Score")
    ///     .Limit(10)
    ///     .ToListAsync();
    /// </code>
    ///
    /// Nothing is sent until a terminal method is awaited, so a query object is cheap to
    /// build and safe to hold. Note that the server caps <c>limit</c> per table scope
    /// (default 200 for a credential, 100 for a role) and silently clamps a larger request -
    /// read <c>PraxRowPage.Limit</c> to see what was actually applied.
    /// </summary>
    public class PraxQuery
    {
        private const string RootAlias = "t";

        private readonly PraxData _data;
        private readonly string _tableNameOrId;

        private readonly List<object> _select = new List<object>();
        private readonly List<PraxFilter> _where = new List<PraxFilter>();
        private readonly List<Dictionary<string, object>> _orderBy = new List<Dictionary<string, object>>();
        private readonly List<string> _groupBy = new List<string>();
        private readonly List<PraxFilter> _having = new List<PraxFilter>();
        private readonly Dictionary<string, string> _extraRefs = new Dictionary<string, string>();

        private int? _limit;
        private int? _offset;
        private bool _totalCount;

        internal PraxQuery(PraxData data, string tableNameOrId)
        {
            _data = data;
            _tableNameOrId = tableNameOrId;
        }

        // ----------------------------------------------------------------- select

        /// <summary>
        /// Restricts the columns returned. Omit to get every column the credential may read.
        ///
        /// Worth doing on a table with wide text or file columns: the gateway meters egress
        /// against the workspace plan, so selecting three columns instead of thirty is a real
        /// saving, not a micro-optimisation.
        /// </summary>
        public PraxQuery Select(params string[] columns)
        {
            if (columns == null) return this;
            foreach (var c in columns)
                if (!string.IsNullOrWhiteSpace(c)) _select.Add(c.Trim());
            return this;
        }

        /// <summary>
        /// Includes a related table as a nested array on each row, read back with
        /// <c>row.GetRelation(name)</c>.
        ///
        /// The join column is detected from the relation, so you only name the table. Note
        /// that relations must be enabled on the table scope, and depth is capped server-side.
        /// </summary>
        /// <param name="relatedTableNameOrId">Related table name or GUID.</param>
        /// <param name="columns">Columns to include from the relation. Omit for all readable ones.</param>
        /// <param name="limit">Cap on nested rows per parent row.</param>
        public PraxQuery Include(string relatedTableNameOrId, string[] columns = null, int? limit = null)
        {
            if (string.IsNullOrWhiteSpace(relatedTableNameOrId))
                throw new ArgumentException("A related table name or id is required.",
                    nameof(relatedTableNameOrId));

            // Each relation needs its own alias in refs; r1, r2, ... in declaration order.
            var alias = "r" + (_extraRefs.Count + 1);
            _extraRefs[alias] = relatedTableNameOrId.Trim();

            var relation = new Dictionary<string, object> { { "table", alias } };

            if (columns != null && columns.Length > 0)
            {
                var picked = new List<object>();
                foreach (var c in columns)
                    if (!string.IsNullOrWhiteSpace(c)) picked.Add(c.Trim());
                if (picked.Count > 0) relation["select"] = picked;
            }

            if (limit.HasValue) relation["limit"] = limit.Value;

            _select.Add(relation);
            return this;
        }

        // ------------------------------------------------------------------ where

        /// <summary>Adds conditions. Multiple calls and multiple arguments are ANDed.</summary>
        public PraxQuery Where(params PraxFilter[] filters)
        {
            if (filters == null) return this;
            foreach (var f in filters)
                if (f != null) _where.Add(f);
            return this;
        }

        /// <summary>Shorthand for <c>Where(PraxFilter.Eq(column, value))</c>.</summary>
        public PraxQuery Where(string column, object value)
        {
            _where.Add(PraxFilter.Eq(column, value));
            return this;
        }

        // ------------------------------------------------------------------ order

        public PraxQuery OrderBy(string column) => AddOrder(column, "asc");

        public PraxQuery OrderByDescending(string column) => AddOrder(column, "desc");

        private PraxQuery AddOrder(string column, string direction)
        {
            if (string.IsNullOrWhiteSpace(column))
                throw new ArgumentException("A column name is required.", nameof(column));

            _orderBy.Add(new Dictionary<string, object>
            {
                { "field", column.Trim() },
                { "dir", direction }
            });
            return this;
        }

        // ------------------------------------------------------------------ paging

        /// <summary>
        /// Maximum rows to return. The server clamps this to the table scope's cap, so asking
        /// for 10,000 does not get you 10,000 - page with <see cref="Offset"/> instead.
        /// </summary>
        public PraxQuery Limit(int limit)
        {
            _limit = Math.Max(1, limit);
            return this;
        }

        /// <summary>Rows to skip, for paging.</summary>
        public PraxQuery Offset(int offset)
        {
            _offset = Math.Max(0, offset);
            return this;
        }

        /// <summary>
        /// Asks the gateway for the total match count alongside the page, exposed as
        /// <c>PraxRowPage.Total</c>. Off by default because it costs a second count pass.
        /// </summary>
        public PraxQuery WithTotalCount()
        {
            _totalCount = true;
            return this;
        }

        // -------------------------------------------------------------- aggregates

        /// <summary>
        /// Adds an aggregate to the select list: count, sum, avg, min or max.
        ///
        /// Aggregations must be enabled on the table scope (they are off by default) - a 403
        /// here means the scope, not the query.
        /// </summary>
        /// <param name="function">count, sum, avg, min, or max.</param>
        /// <param name="column">Column to aggregate. Use "*" with count.</param>
        /// <param name="alias">Result key. Letters, digits and underscore only.</param>
        public PraxQuery Aggregate(string function, string column, string alias)
        {
            if (string.IsNullOrWhiteSpace(function))
                throw new ArgumentException("An aggregate function is required.", nameof(function));
            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("An alias is required.", nameof(alias));

            var fn = function.Trim().ToLowerInvariant();
            if (fn != "count" && fn != "sum" && fn != "avg" && fn != "min" && fn != "max")
                throw new ArgumentException(
                    "Unsupported aggregate '" + function + "'. The gateway accepts count, sum, " +
                    "avg, min and max.", nameof(function));

            _select.Add(new Dictionary<string, object>
            {
                { "field", string.IsNullOrWhiteSpace(column) ? "*" : column.Trim() },
                { "fn", fn },
                { "alias", alias.Trim() }
            });
            return this;
        }

        /// <summary>Groups rows for aggregation.</summary>
        public PraxQuery GroupBy(params string[] columns)
        {
            if (columns == null) return this;
            foreach (var c in columns)
                if (!string.IsNullOrWhiteSpace(c)) _groupBy.Add(c.Trim());
            return this;
        }

        /// <summary>Filters groups after aggregation.</summary>
        public PraxQuery Having(params PraxFilter[] filters)
        {
            if (filters == null) return this;
            foreach (var f in filters)
                if (f != null) _having.Add(f);
            return this;
        }

        // -------------------------------------------------------------- terminals

        /// <summary>Runs the query and returns the page plus its metadata.</summary>
        public async Task<PraxRowPage> ToPageAsync(CancellationToken ct = default)
        {
            var body = await _data.ExecuteAsync(await BuildRequestAsync(ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
            return PraxData.ParsePage(body);
        }

        /// <summary>Runs the query and returns the rows.</summary>
        public async Task<IReadOnlyList<PraxRow>> ToListAsync(CancellationToken ct = default)
        {
            var page = await ToPageAsync(ct).ConfigureAwait(false);
            return page.Rows;
        }

        /// <summary>Runs the query and projects each row onto <typeparamref name="T"/>.</summary>
        public async Task<List<T>> ToListAsync<T>(CancellationToken ct = default) where T : new()
        {
            var rows = await ToListAsync(ct).ConfigureAwait(false);
            var results = new List<T>(rows.Count);
            foreach (var row in rows) results.Add(row.As<T>());
            return results;
        }

        /// <summary>The first matching row, or null. Fetches only one row.</summary>
        public async Task<PraxRow> FirstAsync(CancellationToken ct = default)
        {
            var saved = _limit;
            _limit = 1;
            try
            {
                var page = await ToPageAsync(ct).ConfigureAwait(false);
                return page.Rows.Count > 0 ? page.Rows[0] : null;
            }
            finally
            {
                _limit = saved;
            }
        }

        /// <summary>The first matching row projected onto <typeparamref name="T"/>, or default.</summary>
        public async Task<T> FirstAsync<T>(CancellationToken ct = default) where T : new()
        {
            var row = await FirstAsync(ct).ConfigureAwait(false);
            return row == null ? default : row.As<T>();
        }

        /// <summary>True when at least one row matches.</summary>
        public async Task<bool> AnyAsync(CancellationToken ct = default)
        {
            return await FirstAsync(ct).ConfigureAwait(false) != null;
        }

        /// <summary>
        /// The number of matching rows, ignoring limit and offset.
        ///
        /// Implemented with includeTotalCount and a single-row fetch: the gateway clamps
        /// <c>limit</c> to a minimum of 1, so asking for zero rows is not possible and one row
        /// is the cheapest honest way to get the count.
        /// </summary>
        public async Task<long> CountAsync(CancellationToken ct = default)
        {
            var savedLimit = _limit;
            var savedTotal = _totalCount;
            var savedOffset = _offset;

            _limit = 1;
            _totalCount = true;
            _offset = null;

            try
            {
                var page = await ToPageAsync(ct).ConfigureAwait(false);

                if (page.Total.HasValue) return page.Total.Value;

                // The gateway did not return a total. Report the shortfall rather than
                // silently handing back a wrong number.
                throw new PraxException("COUNT_UNAVAILABLE",
                    "The gateway did not return a total count for this query. Aggregations may " +
                    "be disabled on this table's scope - enable them in API Gateway settings, or " +
                    "use Aggregate(\"count\", \"*\", \"n\") instead.");
            }
            finally
            {
                _limit = savedLimit;
                _totalCount = savedTotal;
                _offset = savedOffset;
            }
        }

        // ----------------------------------------------------------------- request

        internal async Task<Dictionary<string, object>> BuildRequestAsync(CancellationToken ct)
        {
            var refs = new Dictionary<string, object>
            {
                { RootAlias, await _data.ResolveTableAsync(_tableNameOrId, ct).ConfigureAwait(false) }
            };

            foreach (var pair in _extraRefs)
                refs[pair.Key] = await _data.ResolveTableAsync(pair.Value, ct).ConfigureAwait(false);

            var query = new Dictionary<string, object> { { "from", RootAlias } };

            if (_select.Count > 0) query["select"] = _select;
            if (_where.Count > 0) query["where"] = PraxFilter.ToWireList(_where);
            if (_orderBy.Count > 0) query["orderBy"] = _orderBy;
            if (_groupBy.Count > 0) query["groupBy"] = _groupBy;
            if (_having.Count > 0) query["having"] = PraxFilter.ToWireList(_having);
            if (_limit.HasValue) query["limit"] = _limit.Value;
            if (_offset.HasValue) query["offset"] = _offset.Value;

            var request = new Dictionary<string, object>
            {
                { "refs", refs },
                { "query", query }
            };
            if (_totalCount) request["includeTotalCount"] = true;

            return request;
        }
    }
}
