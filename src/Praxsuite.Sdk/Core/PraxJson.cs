using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Praxsuite
{
    /// <summary>
    /// Minimal, allocation-conscious JSON reader/writer.
    ///
    /// The SDK ships its own parser on purpose: Praxsuite rows are open-ended
    /// <c>Dictionary&lt;string, object&gt;</c> shapes that Unity's built-in
    /// <c>JsonUtility</c> cannot represent, and pulling in Newtonsoft would drag a UPM
    /// dependency (plus its well-known version conflicts) into every project that
    /// installs this package. Zero dependencies is a feature.
    ///
    /// Parsed values map to: <c>Dictionary&lt;string, object&gt;</c>, <c>List&lt;object&gt;</c>,
    /// <c>string</c>, <c>double</c>, <c>long</c>, <c>bool</c>, <c>null</c>.
    /// </summary>
    public static class PraxJson
    {
        // ---------------------------------------------------------------- parse

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var index = 0;
            var value = ParseValue(json, ref index);
            SkipWhitespace(json, ref index);
            return value;
        }

        /// <summary>Parses and casts to an object map. Returns an empty map rather than null.</summary>
        public static Dictionary<string, object> ParseObject(string json)
        {
            return Parse(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw Err(s, i, "unexpected end of input");

            switch (s[i])
            {
                case '{': return ParseMap(s, ref i);
                case '[': return ParseList(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseMap(string s, ref int i)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // opening brace
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Err(s, i, "expected a property name");
                var key = ParseString(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Err(s, i, "expected a colon");
                i++;

                map[key] = ParseValue(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw Err(s, i, "unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw Err(s, i, "expected a comma or closing brace");
            }
        }

        private static List<object> ParseList(string s, ref int i)
        {
            var list = new List<object>();
            i++; // opening bracket
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw Err(s, i, "unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw Err(s, i, "expected a comma or closing bracket");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                var esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Err(s, i, "truncated unicode escape");
                        // Surrogate pairs arrive as two consecutive \u escapes; appending each
                        // code unit in order reassembles the astral character correctly.
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw Err(s, i, "unknown escape character");
                }
            }
            throw Err(s, i, "unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            var start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            var isFloat = false;
            while (i < s.Length)
            {
                var c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; i++; continue; }
                break;
            }

            var raw = s.Substring(start, i - start);
            if (raw.Length == 0) throw Err(s, start, "expected a number");

            if (!isFloat && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;

            throw Err(s, start, "malformed number");
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length ||
                string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw Err(s, i, "expected literal " + literal);
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        private static PraxJsonException Err(string s, int i, string message)
        {
            var from = Math.Max(0, i - 20);
            var len = Math.Min(40, s.Length - from);
            var near = len > 0 ? s.Substring(from, len) : string.Empty;
            return new PraxJsonException("JSON error at index " + i + ": " + message + ". Near: " + near);
        }

        // --------------------------------------------------------------- write

        /// <summary>Serialises a value tree to compact JSON.</summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string str:
                    WriteString(sb, str);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case Guid g:
                    WriteString(sb, g.ToString());
                    return;
                case DateTime dt:
                    WriteString(sb, dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    return;
                case DateTimeOffset dto:
                    WriteString(sb, dto.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case decimal dec:
                    sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                    return;
                case IDictionary dict:
                    WriteMap(sb, dict);
                    return;
            }

            if (value is Enum)
            {
                WriteString(sb, value.ToString());
                return;
            }
            if (value is IConvertible && value.GetType().IsPrimitive)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is IEnumerable seq)
            {
                WriteList(sb, seq);
                return;
            }

            // Unknown reference type: stringify rather than silently drop it.
            WriteString(sb, value.ToString());
        }

        private static void WriteMap(StringBuilder sb, IDictionary dict)
        {
            sb.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                sb.Append(':');
                Write(sb, entry.Value);
            }
            sb.Append('}');
        }

        private static void WriteList(StringBuilder sb, IEnumerable seq)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in seq)
            {
                if (!first) sb.Append(',');
                first = false;
                Write(sb, item);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s == null) { sb.Append('"'); return; }

            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c); // emoji and other astral chars pass through as UTF-8
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Thrown when a JSON payload cannot be parsed.</summary>
    public class PraxJsonException : Exception
    {
        public PraxJsonException(string message) : base(message) { }
    }
}
