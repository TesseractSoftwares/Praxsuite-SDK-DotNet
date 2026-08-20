using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>One column, as the gateway describes it.</summary>
    public class PraxColumnInfo
    {
        public string Id;
        public string Name;

        /// <summary>Praxsuite column type: Text, Number, Bool, Date, Enduser, File, Table, Status, ...</summary>
        public string Type;

        public bool IsKey;

        /// <summary>
        /// System-managed column (ID, CREATEDDATE, CREATEDBY, POSITION, ...). Never send these
        /// in an insert or update - the backend fills them in and rejects the attempt.
        /// </summary>
        public bool IsNative;

        public bool IsRequired;

        /// <summary>For relation columns, the id of the table pointed at.</summary>
        public string PointsTo;

        /// <summary>For Status columns, the status group id; for Secret columns, the secret id.</summary>
        public string EntityId;

        /// <summary>
        /// True for the Enduser column type. This is the column a __SELF__ row filter binds
        /// to, so it is what makes per-player isolation work on this table.
        /// </summary>
        public bool IsEndUser =>
            string.Equals(Type, "Enduser", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One table, as the gateway describes it.</summary>
    public class PraxTableInfo
    {
        public string Id;
        public string Name;
        public IReadOnlyList<PraxColumnInfo> Columns = Array.Empty<PraxColumnInfo>();

        /// <summary>Finds a column by name, case-insensitively.</summary>
        public PraxColumnInfo Column(string name)
        {
            if (Columns == null || string.IsNullOrEmpty(name)) return null;
            foreach (var c in Columns)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>The Enduser column, if this table has one.</summary>
        public PraxColumnInfo EndUserColumn
        {
            get
            {
                if (Columns == null) return null;
                foreach (var c in Columns)
                    if (c.IsEndUser) return c;
                return null;
            }
        }
    }

    /// <summary>
    /// Maps table names to the GUIDs the query API needs.
    ///
    /// PraxQL addresses tables by GUID, which would mean pasting GUIDs through your game
    /// code. Instead the SDK fetches the schema once and lets you write
    /// <c>Prax.Data.From("PlayerSaves")</c>.
    ///
    /// The schema endpoint returns only what the calling credential is scoped to see, and
    /// only tables whose scope enables introspection - so this reflects your permissions, not
    /// the whole workspace. A table missing from it is usually a scope that was never granted.
    /// </summary>
    public class PraxSchema
    {
        private readonly PraxsuiteClient _client;
        private readonly Dictionary<string, string> _idsByName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PraxTableInfo> _tablesByName =
            new Dictionary<string, PraxTableInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        private Task _fetchInFlight;
        private bool _fetched;

        internal PraxSchema(PraxsuiteClient client)
        {
            _client = client;
        }

        /// <summary>Tables known so far. Empty until the schema has been fetched.</summary>
        public IReadOnlyList<PraxTableInfo> Tables
        {
            get
            {
                lock (_gate) return new List<PraxTableInfo>(_tablesByName.Values);
            }
        }

        /// <summary>True when the schema has been fetched at least once.</summary>
        public bool IsLoaded
        {
            get { lock (_gate) return _fetched; }
        }

        /// <summary>
        /// Registers a name-to-GUID mapping by hand. Use this to skip the schema request
        /// entirely (set <c>autoFetchSchema = false</c>) - useful when startup latency matters
        /// or the credential has introspection disabled.
        /// </summary>
        public void Register(string tableName, string tableId)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("tableName is required.", nameof(tableName));
            if (!Guid.TryParse((tableId ?? "").Trim(), out var parsed))
                throw new ArgumentException("tableId must be a GUID, got: " + tableId, nameof(tableId));

            lock (_gate) _idsByName[tableName.Trim()] = parsed.ToString();
        }

        /// <summary>Registers several mappings at once.</summary>
        public void RegisterMany(IDictionary<string, string> tables)
        {
            if (tables == null) return;
            foreach (var pair in tables) Register(pair.Key, pair.Value);
        }

        /// <summary>
        /// True when this table name resolves - either fetched from the schema or registered by
        /// hand. Useful for branching on an optional table without catching an exception from
        /// the query itself.
        /// </summary>
        public bool Has(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName)) return false;
            lock (_gate) return _idsByName.ContainsKey(tableName.Trim());
        }

        /// <summary>Metadata for a table, or null when the schema has not been fetched.</summary>
        public PraxTableInfo Describe(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return null;
            lock (_gate) return _tablesByName.TryGetValue(tableName.Trim(), out var info) ? info : null;
        }

        /// <summary>
        /// Fetches the schema. Concurrent callers share one request, and it will not refetch
        /// unless asked - the schema of a live workspace does not change mid-session.
        /// </summary>
        public Task FetchAsync(bool force = false, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_fetched && !force) return Task.CompletedTask;
                if (_fetchInFlight != null && !_fetchInFlight.IsCompleted) return _fetchInFlight;
                _fetchInFlight = FetchCoreAsync(ct);
                return _fetchInFlight;
            }
        }

        private async Task FetchCoreAsync(CancellationToken ct)
        {
            var body = await PraxHttp.SendJsonAsync(_client, "GET",
                PraxRoutes.Schema(_client.BaseUrl, _client.WorkspaceId),
                null, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);

            // The gateway answers {tables:[...]}; tolerate a bare array too.
            List<object> tableNodes = null;
            if (body.TryGetValue("tables", out var node)) tableNodes = node as List<object>;

            var count = 0;
            lock (_gate)
            {
                if (tableNodes != null)
                {
                    foreach (var entry in tableNodes)
                    {
                        if (!(entry is Dictionary<string, object> map)) continue;

                        var name = PraxHttp.AsString(map, "name");
                        var id = PraxHttp.AsString(map, "id");
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;

                        var info = new PraxTableInfo { Id = id, Name = name };

                        if (map.TryGetValue("columns", out var colsNode) && colsNode is List<object> cols)
                        {
                            var columns = new List<PraxColumnInfo>(cols.Count);
                            foreach (var c in cols)
                            {
                                if (!(c is Dictionary<string, object> col)) continue;
                                columns.Add(new PraxColumnInfo
                                {
                                    Id = PraxHttp.AsString(col, "id"),
                                    Name = PraxHttp.AsString(col, "name"),
                                    Type = PraxHttp.AsString(col, "type"),
                                    IsKey = AsBool(col, "isKey"),
                                    IsNative = AsBool(col, "isNative"),
                                    IsRequired = AsBool(col, "isRequired"),
                                    PointsTo = PraxHttp.AsString(col, "pointsTo"),
                                    EntityId = PraxHttp.AsString(col, "entityId")
                                });
                            }
                            info.Columns = columns;
                        }

                        // A manual Register() wins: it was an explicit choice by the developer.
                        if (!_idsByName.ContainsKey(name)) _idsByName[name] = id;
                        _tablesByName[name] = info;
                        count++;
                    }
                }

                _fetched = true;
            }

            PraxLog.Info("Schema loaded: " + count + " table(s) visible to this credential.");
        }

        /// <summary>
        /// Resolves a table name or GUID to a GUID, fetching the schema if needed.
        /// A GUID passed straight through is returned as-is, so you never have to depend on
        /// the schema request at all.
        /// </summary>
        internal async Task<string> ResolveAsync(string tableNameOrId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(tableNameOrId))
                throw new ArgumentException("A table name or id is required.", nameof(tableNameOrId));

            var key = tableNameOrId.Trim();

            if (Guid.TryParse(key, out var direct)) return direct.ToString();

            lock (_gate)
            {
                if (_idsByName.TryGetValue(key, out var known)) return known;
            }

            if (_client.AutoFetchSchema)
            {
                await FetchAsync(false, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_idsByName.TryGetValue(key, out var found)) return found;
                }
            }

            throw new PraxException("UNKNOWN_TABLE", BuildUnknownTableMessage(key));
        }

        private string BuildUnknownTableMessage(string name)
        {
            List<string> known;
            bool fetched;
            lock (_gate)
            {
                fetched = _fetched;
                known = new List<string>(_idsByName.Keys);
            }

            var message = "Table '" + name + "' is not available to this credential.\n\n";

            if (!fetched && !_client.AutoFetchSchema)
            {
                return message +
                       "Schema auto-fetch is off, so the SDK has no name-to-id mapping. Either " +
                       "turn it on in PraxsuiteSettings, call Prax.Schema.Register(\"" + name +
                       "\", \"<table-guid>\"), or pass the table's GUID directly.";
            }

            if (known.Count == 0)
            {
                return message +
                       "The schema endpoint returned no tables at all, which means this credential " +
                       "has no table scopes with introspection enabled. In the portal, open API " +
                       "Gateway / Credentials (or Roles, for signed-in players), add a scope for " +
                       "this table, and enable 'Allow schema introspection'.";
            }

            known.Sort(StringComparer.OrdinalIgnoreCase);
            return message +
                   "Visible tables: " + string.Join(", ", known) + "\n\n" +
                   "Table names are case-insensitive but must otherwise match exactly. If the " +
                   "table exists in the workspace but is missing here, its scope has not been " +
                   "granted to this credential or role, or introspection is disabled on it.";
        }

        private static bool AsBool(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return false;
            if (value is bool b) return b;
            return bool.TryParse(Convert.ToString(value), out var parsed) && parsed;
        }
    }
}
