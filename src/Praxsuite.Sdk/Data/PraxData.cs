using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// Reads and writes table rows.
    ///
    /// Every call is authorised twice: the credential (or the signed-in player's role) must be
    /// scoped to the table, and any row filter on that scope is applied on top of your
    /// conditions. A client cannot widen either. That means the safe pattern for a game is:
    /// scope the player role to read-only on everything they should see, add a __SELF__ row
    /// filter on their own tables, and route anything a cheater would want to forge -
    /// currency, inventory grants, score submission - through <c>Prax.Endpoints</c>, where an
    /// automation you control does the writing.
    ///
    /// There is deliberately no "act as player X" parameter on this API. Identity comes from
    /// the player's own token and nothing else, because only a value the server derives itself
    /// can scope anything - an impersonation argument the server does not enforce is worse than
    /// none, since it reads like a security boundary while being decorative.
    /// </summary>
    public class PraxData
    {
        private readonly PraxsuiteClient _client;

        internal PraxData(PraxsuiteClient client)
        {
            _client = client;
        }

        // ------------------------------------------------------------------- read

        /// <summary>Starts a query against a table, by name or GUID.</summary>
        public PraxQuery From(string tableNameOrId)
        {
            return new PraxQuery(this, tableNameOrId);
        }

        /// <summary>
        /// Fetches a single row by its primary key.
        /// </summary>
        public Task<PraxRow> GetAsync(string tableNameOrId, string rowId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rowId))
                throw new ArgumentException("rowId is required.", nameof(rowId));

            return From(tableNameOrId).Where(PraxFilter.Eq("ID", rowId)).FirstAsync(ct);
        }

        // ------------------------------------------------------------------ insert

        /// <summary>
        /// Inserts one row.
        ///
        /// Do not include native columns (ID, CREATEDDATE, CREATEDBY, POSITION) - the backend
        /// fills those in and rejects a request that supplies them.
        /// </summary>
        /// <param name="values">Column name to value.</param>
        /// <param name="returning">Return the inserted row. Costs a little egress; on by default because callers usually want the new ID.</param>
        public Task<PraxMutationResult> InsertAsync(string tableNameOrId,
            IDictionary<string, object> values, bool returning = true, CancellationToken ct = default)
        {
            // Not an async method on purpose - see the note on UpdateAsync.
            if (values == null || values.Count == 0)
                throw new ArgumentException("At least one column value is required.", nameof(values));

            return InsertManyAsync(tableNameOrId, new[] { values }, returning, ct);
        }

        /// <summary>
        /// Inserts several rows in one request. Much cheaper than a loop of single inserts:
        /// one round trip, and one API call against the workspace's plan allowance.
        /// </summary>
        public Task<PraxMutationResult> InsertManyAsync(string tableNameOrId,
            IEnumerable<IDictionary<string, object>> rows, bool returning = true,
            CancellationToken ct = default)
        {
            var values = new List<object>();
            if (rows != null)
                foreach (var row in rows)
                    if (row != null && row.Count > 0) values.Add(ToPlainMap(row));

            if (values.Count == 0)
                throw new ArgumentException("At least one row is required.", nameof(rows));

            return InsertManyCoreAsync(tableNameOrId, values, returning, ct);
        }

        private async Task<PraxMutationResult> InsertManyCoreAsync(string tableNameOrId,
            List<object> values, bool returning, CancellationToken ct)
        {
            var mutation = new Dictionary<string, object>
            {
                { "type", "insert" },
                { "table", "t" },
                { "values", values }
            };
            if (returning) mutation["returning"] = true;

            var body = await ExecuteAsync(new Dictionary<string, object>
            {
                { "refs", new Dictionary<string, object>
                    { { "t", await ResolveTableAsync(tableNameOrId, ct).ConfigureAwait(false) } } },
                { "mutation", mutation }
            }, ct).ConfigureAwait(false);

            return ParseMutation(body);
        }

        // ------------------------------------------------------------------ update

        /// <summary>
        /// Updates rows matching <paramref name="filters"/>.
        ///
        /// A filter is mandatory. The gateway rejects an unscoped update outright, and so does
        /// this method - an accidental "update every row in the table" is the kind of mistake
        /// that has no undo.
        /// </summary>
        /// <remarks>
        /// Deliberately not an <c>async</c> method. An exception thrown inside an async method is
        /// captured into the returned Task rather than raised at the call site, so a caller who
        /// launches this fire-and-forget would get silence: no update, no error, just an
        /// unobserved task exception. For a guardrail whose entire job is to stop an accidental
        /// table-wide write, silence is the worst possible outcome - so validation happens
        /// synchronously and the request runs in a separate core method.
        /// </remarks>
        public Task<PraxMutationResult> UpdateAsync(string tableNameOrId,
            IDictionary<string, object> set, PraxFilter[] filters, CancellationToken ct = default)
        {
            if (set == null || set.Count == 0)
                throw new ArgumentException("At least one column to set is required.", nameof(set));

            var where = Compact(filters);
            if (where.Count == 0)
                throw new ArgumentException(
                    "UpdateAsync requires at least one filter. An update with no WHERE clause " +
                    "would rewrite every row the credential can reach, so both this SDK and the " +
                    "gateway refuse it. To target one row, filter on its ID.", nameof(filters));

            return UpdateCoreAsync(tableNameOrId, set, where, ct);
        }

        private async Task<PraxMutationResult> UpdateCoreAsync(string tableNameOrId,
            IDictionary<string, object> set, List<PraxFilter> where, CancellationToken ct)
        {
            var body = await ExecuteAsync(new Dictionary<string, object>
            {
                { "refs", new Dictionary<string, object>
                    { { "t", await ResolveTableAsync(tableNameOrId, ct).ConfigureAwait(false) } } },
                { "mutation", new Dictionary<string, object>
                    {
                        { "type", "update" },
                        { "table", "t" },
                        { "set", ToPlainMap(set) },
                        { "where", PraxFilter.ToWireList(where) }
                    }

                }
            }, ct).ConfigureAwait(false);

            return ParseMutation(body);
        }

        /// <summary>Updates a single row by primary key.</summary>
        public Task<PraxMutationResult> UpdateByIdAsync(string tableNameOrId, string rowId,
            IDictionary<string, object> set, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rowId))
                throw new ArgumentException("rowId is required.", nameof(rowId));

            return UpdateAsync(tableNameOrId, set, new[] { PraxFilter.Eq("ID", rowId) }, ct);
        }

        // ------------------------------------------------------------------ delete

        /// <summary>
        /// Deletes rows matching <paramref name="filters"/>. A filter is mandatory, for the
        /// same reason as on update.
        /// </summary>
        /// <remarks>
        /// Not an <c>async</c> method, for the reason given on <see cref="UpdateAsync"/>: a
        /// guardrail that throws from inside a Task nobody awaits is a guardrail that does
        /// nothing.
        /// </remarks>
        public Task<PraxMutationResult> DeleteAsync(string tableNameOrId,
            PraxFilter[] filters, CancellationToken ct = default)
        {
            var where = Compact(filters);
            if (where.Count == 0)
                throw new ArgumentException(
                    "DeleteAsync requires at least one filter. A delete with no WHERE clause " +
                    "would empty the table, so both this SDK and the gateway refuse it.",
                    nameof(filters));

            return DeleteCoreAsync(tableNameOrId, where, ct);
        }

        private async Task<PraxMutationResult> DeleteCoreAsync(string tableNameOrId,
            List<PraxFilter> where, CancellationToken ct)
        {
            var body = await ExecuteAsync(new Dictionary<string, object>
            {
                { "refs", new Dictionary<string, object>
                    { { "t", await ResolveTableAsync(tableNameOrId, ct).ConfigureAwait(false) } } },
                { "mutation", new Dictionary<string, object>
                    {
                        { "type", "delete" },
                        { "table", "t" },
                        { "where", PraxFilter.ToWireList(where) }
                    }
                }
            }, ct).ConfigureAwait(false);

            return ParseMutation(body);
        }

        /// <summary>Deletes a single row by primary key.</summary>
        public Task<PraxMutationResult> DeleteByIdAsync(string tableNameOrId, string rowId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rowId))
                throw new ArgumentException("rowId is required.", nameof(rowId));

            return DeleteAsync(tableNameOrId, new[] { PraxFilter.Eq("ID", rowId) }, ct);
        }

        // ----------------------------------------------------------------- upsert

        /// <summary>
        /// Updates the row matching <paramref name="filters"/>, or inserts one when none
        /// matches. The natural shape for a player save row.
        ///
        /// This is two requests, not an atomic upsert - the gateway has no single-call upsert.
        /// Two clients racing on the same key can therefore both insert. For a player's own
        /// save that is not a real risk (one session, one row, and a __SELF__ filter scopes it),
        /// but for anything contended put the write behind a gateway endpoint where an
        /// automation can serialise it.
        /// </summary>
        public Task<PraxMutationResult> UpsertAsync(string tableNameOrId,
            IDictionary<string, object> values, PraxFilter[] filters, CancellationToken ct = default)
        {
            if (values == null || values.Count == 0)
                throw new ArgumentException("At least one column value is required.", nameof(values));

            var where = Compact(filters);
            if (where.Count == 0)
                throw new ArgumentException(
                    "UpsertAsync needs a filter identifying the row to match.", nameof(filters));

            return UpsertCoreAsync(tableNameOrId, values, where, ct);
        }

        private async Task<PraxMutationResult> UpsertCoreAsync(string tableNameOrId,
            IDictionary<string, object> values, List<PraxFilter> where, CancellationToken ct)
        {
            var existing = await From(tableNameOrId).Where(where.ToArray())
                .Select("ID").FirstAsync(ct).ConfigureAwait(false);

            if (existing != null && !string.IsNullOrEmpty(existing.Id))
                return await UpdateByIdAsync(tableNameOrId, existing.Id, values, ct).ConfigureAwait(false);

            return await InsertAsync(tableNameOrId, values, true, ct).ConfigureAwait(false);
        }

        // -------------------------------------------------------------- execution

        internal Task<string> ResolveTableAsync(string tableNameOrId, CancellationToken ct)
        {
            return _client.Schema.ResolveAsync(tableNameOrId, ct);
        }

        internal Task<Dictionary<string, object>> ExecuteAsync(Dictionary<string, object> request,
            CancellationToken ct)
        {
            return PraxHttp.SendJsonAsync(_client, "POST",
                PraxRoutes.Query(_client.BaseUrl, _client.WorkspaceId),
                request, PraxHttp.AuthMode.PreferSession, ct);
        }

        /// <summary>
        /// Sends a hand-built PraxQL request. An escape hatch for query shapes the builder does
        /// not cover; prefer <see cref="From"/> where it does.
        /// </summary>
        public Task<Dictionary<string, object>> ExecuteRawAsync(Dictionary<string, object> request,
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return ExecuteAsync(request, ct);
        }

        // ---------------------------------------------------------------- parsing

        internal static PraxRowPage ParsePage(Dictionary<string, object> body)
        {
            var page = new PraxRowPage();

            if (body.TryGetValue("data", out var dataNode) && dataNode is List<object> list)
            {
                var rows = new List<PraxRow>(list.Count);
                foreach (var item in list)
                    if (item is Dictionary<string, object> map) rows.Add(new PraxRow(map));
                page.Rows = rows;
            }

            if (body.TryGetValue("meta", out var metaNode) &&
                metaNode is Dictionary<string, object> meta)
            {
                page.Count = (int)AsLong(meta, "count");
                page.Limit = (int)AsLong(meta, "limit");
                page.Offset = (int)AsLong(meta, "offset");
                page.DurationMs = AsLong(meta, "durationMs");

                // The gateway calls this "total" - not "totalCount". Reading the wrong key is
                // why the Lua SDK's Count() always reported zero.
                if (meta.TryGetValue("total", out var total) && total != null)
                    page.Total = AsLong(meta, "total");
            }

            if (page.Count == 0 && page.Rows.Count > 0) page.Count = page.Rows.Count;
            return page;
        }

        internal static PraxMutationResult ParseMutation(Dictionary<string, object> body)
        {
            var result = new PraxMutationResult
            {
                AffectedRows = (int)AsLong(body, "affectedRows")
            };

            if (body.TryGetValue("data", out var dataNode) && dataNode is List<object> list)
            {
                var rows = new List<PraxRow>(list.Count);
                foreach (var item in list)
                    if (item is Dictionary<string, object> map) rows.Add(new PraxRow(map));
                result.Rows = rows;
            }

            if (body.TryGetValue("meta", out var metaNode) &&
                metaNode is Dictionary<string, object> meta)
                result.DurationMs = AsLong(meta, "durationMs");

            return result;
        }

        // ---------------------------------------------------------------- helpers

        private static List<PraxFilter> Compact(PraxFilter[] filters)
        {
            var list = new List<PraxFilter>();
            if (filters == null) return list;
            foreach (var f in filters)
                if (f != null) list.Add(f);
            return list;
        }

        /// <summary>
        /// Copies an IDictionary into a plain Dictionary so the JSON writer sees a shape it
        /// serialises as an object rather than as a sequence of key-value pairs.
        /// </summary>
        private static Dictionary<string, object> ToPlainMap(IDictionary<string, object> source)
        {
            var map = new Dictionary<string, object>(source.Count, StringComparer.Ordinal);
            foreach (var pair in source) map[pair.Key] = pair.Value;
            return map;
        }

        private static long AsLong(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return 0;
            if (value is long l) return l;
            if (value is double d) return (long)Math.Round(d);
            return long.TryParse(Convert.ToString(value), out var parsed) ? parsed : 0;
        }
    }
}
