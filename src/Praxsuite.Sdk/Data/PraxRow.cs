using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Praxsuite
{
    /// <summary>
    /// One row, with typed accessors over the raw column values.
    ///
    /// Rows are open-ended maps rather than fixed classes because a Praxsuite table's shape is
    /// defined in the portal and can gain columns without a client rebuild. Read fields with
    /// the typed getters, or call <see cref="As{T}"/> to project into your own class.
    ///
    /// Accessors return defaults rather than throwing when a column is missing: a game that
    /// gained a column last week should not crash on a save row written before it existed.
    /// Use <see cref="Has"/> when the difference matters.
    /// </summary>
    public class PraxRow
    {
        private readonly Dictionary<string, object> _values;

        internal PraxRow(Dictionary<string, object> values)
        {
            // Case-insensitive so "score" and "Score" both work - column casing in the portal
            // is a display choice, not an API contract.
            _values = new Dictionary<string, object>(
                values ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The raw values, as parsed from JSON.</summary>
        public IReadOnlyDictionary<string, object> Values => _values;

        public IEnumerable<string> ColumnNames => _values.Keys;

        public bool Has(string column) => _values.ContainsKey(column);

        /// <summary>The raw value, or null.</summary>
        public object this[string column] =>
            _values.TryGetValue(column, out var value) ? value : null;

        // ---------------------------------------------------------------- getters

        public string GetString(string column, string fallback = null)
        {
            var value = this[column];
            if (value == null) return fallback;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public int GetInt(string column, int fallback = 0)
        {
            return (int)GetLong(column, fallback);
        }

        public long GetLong(string column, long fallback = 0)
        {
            var value = this[column];
            switch (value)
            {
                case null: return fallback;
                case long l: return l;
                case double d: return (long)Math.Round(d);
                case bool b: return b ? 1 : 0;
            }
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        public float GetFloat(string column, float fallback = 0f)
        {
            return (float)GetDouble(column, fallback);
        }

        public double GetDouble(string column, double fallback = 0d)
        {
            var value = this[column];
            switch (value)
            {
                case null: return fallback;
                case double d: return d;
                case long l: return l;
                case bool b: return b ? 1 : 0;
            }
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        public bool GetBool(string column, bool fallback = false)
        {
            var value = this[column];
            switch (value)
            {
                case null: return fallback;
                case bool b: return b;
                case long l: return l != 0;
                case double d: return Math.Abs(d) > double.Epsilon;
            }
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
                ? parsed
                : fallback;
        }

        public DateTimeOffset? GetDate(string column)
        {
            var raw = GetString(column);
            if (string.IsNullOrEmpty(raw)) return null;

            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        /// <summary>The row's primary key, from the native ID column.</summary>
        public string Id => GetString("ID") ?? GetString("Id") ?? GetString("id");

        /// <summary>
        /// A nested relation included via <c>Include()</c>. Empty when the relation was not
        /// requested or matched nothing.
        /// </summary>
        public IReadOnlyList<PraxRow> GetRelation(string column)
        {
            var value = this[column];

            if (value is List<object> list)
            {
                var rows = new List<PraxRow>(list.Count);
                foreach (var item in list)
                    if (item is Dictionary<string, object> map) rows.Add(new PraxRow(map));
                return rows;
            }

            // A to-one relation arrives as a single object.
            if (value is Dictionary<string, object> single)
                return new List<PraxRow> { new PraxRow(single) };

            return Array.Empty<PraxRow>();
        }

        /// <summary>
        /// Projects the row onto a new <typeparamref name="T"/> by matching column names to
        /// public fields and settable properties, case-insensitively. Members with no matching
        /// column keep their default value.
        ///
        /// This is reflection-based, so it is convenient rather than fast. In a hot loop -
        /// deserialising a thousand leaderboard entries a frame - read columns directly with
        /// the typed getters instead.
        /// </summary>
        public T As<T>() where T : new()
        {
            var target = new T();
            var type = typeof(T);

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!TryFind(field.Name, out var value)) continue;
                var converted = Coerce(value, field.FieldType);
                if (converted != null || IsNullable(field.FieldType)) field.SetValue(target, converted);
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || property.GetIndexParameters().Length > 0) continue;
                if (!TryFind(property.Name, out var value)) continue;
                var converted = Coerce(value, property.PropertyType);
                if (converted != null || IsNullable(property.PropertyType))
                    property.SetValue(target, converted);
            }

            return target;
        }

        private bool TryFind(string memberName, out object value)
        {
            return _values.TryGetValue(memberName, out value);
        }

        private static bool IsNullable(Type type)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }

        private static object Coerce(object value, Type target)
        {
            if (value == null) return null;

            var underlying = Nullable.GetUnderlyingType(target) ?? target;
            if (underlying.IsInstanceOfType(value)) return value;

            try
            {
                if (underlying == typeof(string))
                    return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

                if (underlying.IsEnum)
                {
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return Enum.Parse(underlying, text, true);
                }

                if (underlying == typeof(DateTimeOffset))
                    return DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

                if (underlying == typeof(DateTime))
                    return DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

                if (underlying == typeof(Guid))
                    return Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture));

                return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // A single unmappable column should not fail the whole projection - leave the
                // member at its default and carry on.
                return null;
            }
        }

        public override string ToString()
        {
            return "PraxRow(" + string.Join(", ", _values.Keys) + ")";
        }
    }

    /// <summary>A page of rows plus the query's metadata.</summary>
    public class PraxRowPage
    {
        public IReadOnlyList<PraxRow> Rows = Array.Empty<PraxRow>();

        /// <summary>Rows in this page.</summary>
        public int Count;

        /// <summary>The limit actually applied - the server caps it per table scope.</summary>
        public int Limit;

        public int Offset;

        /// <summary>
        /// Total matching rows ignoring limit and offset. Only present when the query asked
        /// for it via <c>WithTotalCount()</c>, since counting costs an extra pass.
        /// </summary>
        public long? Total;

        public long DurationMs;

        /// <summary>True when more rows may exist past this page.</summary>
        public bool HasMore => Total.HasValue ? Offset + Count < Total.Value : Count >= Limit && Limit > 0;
    }

    /// <summary>Result of an insert, update or delete.</summary>
    public class PraxMutationResult
    {
        public int AffectedRows;

        /// <summary>Rows returned by an insert with returning enabled. Empty otherwise.</summary>
        public IReadOnlyList<PraxRow> Rows = Array.Empty<PraxRow>();

        /// <summary>The first returned row, or null.</summary>
        public PraxRow Row => Rows != null && Rows.Count > 0 ? Rows[0] : null;

        public long DurationMs;
    }
}
