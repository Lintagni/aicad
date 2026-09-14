using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AiCad.Json
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Minimal self-contained JSON tree. Deliberately dependency-free so the
    /// plug-in ships as a single DLL with nothing to deploy alongside it.
    /// </summary>
    public class JsonValue
    {
        public JsonKind Kind;
        public bool Bool;
        public double Number;
        public string Text;
        public List<JsonValue> Items;
        public Dictionary<string, JsonValue> Members;

        public static JsonValue NewObject()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Object;
            v.Members = new Dictionary<string, JsonValue>(StringComparer.OrdinalIgnoreCase);
            return v;
        }

        public static JsonValue NewArray()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Array;
            v.Items = new List<JsonValue>();
            return v;
        }

        public static JsonValue New(string s)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.String;
            v.Text = s;
            return v;
        }

        public static JsonValue New(double d)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Number;
            v.Number = d;
            return v;
        }

        public static JsonValue New(bool b)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Bool;
            v.Bool = b;
            return v;
        }

        public bool Has(string name)
        {
            return Kind == JsonKind.Object && Members.ContainsKey(name);
        }

        /// <summary>Member lookup that never throws; returns null when absent.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (Kind != JsonKind.Object) return null;
                JsonValue v;
                if (Members.TryGetValue(name, out v)) return v;
                return null;
            }
            set
            {
                if (Kind != JsonKind.Object) throw new InvalidOperationException("Not an object.");
                Members[name] = value;
            }
        }

        public JsonValue At(int index)
        {
            if (Kind != JsonKind.Array || index < 0 || index >= Items.Count) return null;
            return Items[index];
        }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array) return Items.Count;
                if (Kind == JsonKind.Object) return Members.Count;
                return 0;
            }
        }

        public string GetString(string name, string fallback)
        {
            JsonValue v = this[name];
            if (v == null) return fallback;
            if (v.Kind == JsonKind.String) return v.Text;
            if (v.Kind == JsonKind.Number) return v.Number.ToString(CultureInfo.InvariantCulture);
            if (v.Kind == JsonKind.Bool) return v.Bool ? "true" : "false";
            return fallback;
        }

        public double GetDouble(string name, double fallback)
        {
            JsonValue v = this[name];
            if (v == null) return fallback;
            if (v.Kind == JsonKind.Number) return v.Number;
            if (v.Kind == JsonKind.String)
            {
                double d;
                if (double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return fallback;
        }

        public int GetInt(string name, int fallback)
        {
            return (int)Math.Round(GetDouble(name, fallback));
        }

        public bool GetBool(string name, bool fallback)
        {
            JsonValue v = this[name];
            if (v == null) return fallback;
            if (v.Kind == JsonKind.Bool) return v.Bool;
            if (v.Kind == JsonKind.Number) return Math.Abs(v.Number) > double.Epsilon;
            if (v.Kind == JsonKind.String)
            {
                bool b;
                if (bool.TryParse(v.Text, out b)) return b;
            }
            return fallback;
        }

        /// <summary>Array member, or an empty array when missing / wrong type.</summary>
        public List<JsonValue> GetArray(string name)
        {
            JsonValue v = this[name];
            if (v == null || v.Kind != JsonKind.Array) return new List<JsonValue>();
            return v.Items;
        }

        public void Add(JsonValue item)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Not an array.");
            Items.Add(item);
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, this);
            return sb.ToString();
        }

        // ---------- writing ----------

        private static void Write(StringBuilder sb, JsonValue v)
        {
            if (v == null || v.Kind == JsonKind.Null) { sb.Append("null"); return; }
            switch (v.Kind)
            {
                case JsonKind.Bool:
                    sb.Append(v.Bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    if (double.IsNaN(v.Number) || double.IsInfinity(v.Number)) sb.Append("0");
                    else sb.Append(v.Number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(sb, v.Text);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < v.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(sb, v.Items[i]);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, JsonValue> kv in v.Members)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
            }
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
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
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder();
            WriteString(sb, s);
            return sb.ToString();
        }

        // ---------- parsing ----------

        public static JsonValue Parse(string text)
        {
            int pos = 0;
            JsonValue v = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            return v;
        }

        /// <summary>
        /// Models occasionally wrap JSON in prose or a fenced code block. Pull out
        /// the first balanced object so a chatty reply still parses.
        /// </summary>
        public static JsonValue ParseLenient(string text)
        {
            return ParseLenient(text, null);
        }

        /// <summary>
        /// When preferredKey is given, a recovered fragment is only accepted if
        /// it contains that key. Without this, a truncated reply can yield an
        /// inner object - a single layer, say - that parses cleanly but is not
        /// the plan, which surfaces later as a mystifying empty result.
        /// </summary>
        public static JsonValue ParseLenient(string text, string preferredKey)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                JsonValue whole = Parse(text);
                if (whole != null && (preferredKey == null || whole.Has(preferredKey))) return whole;
            }
            catch (Exception) { }

            JsonValue firstParsed = null;
            int start = text.IndexOf('{');
            while (start >= 0)
            {
                int depth = 0;
                bool inString = false;
                bool escape = false;
                for (int i = start; i < text.Length; i++)
                {
                    char c = text[i];
                    if (inString)
                    {
                        if (escape) escape = false;
                        else if (c == '\\') escape = true;
                        else if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            string candidate = text.Substring(start, i - start + 1);
                            try
                            {
                                JsonValue parsed = Parse(candidate);
                                if (parsed != null)
                                {
                                    if (preferredKey == null || parsed.Has(preferredKey)) return parsed;
                                    if (firstParsed == null) firstParsed = parsed;
                                }
                            }
                            catch (Exception) { }
                            break;
                        }
                    }
                }
                start = text.IndexOf('{', start + 1);
            }

            // Nothing carried the expected key; a fragment is better than nothing
            // only when no key was demanded.
            return preferredKey == null ? firstParsed : null;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static JsonValue ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[pos];
            if (c == '{') return ParseObject(s, ref pos);
            if (c == '[') return ParseArray(s, ref pos);
            if (c == '"') return New(ParseString(s, ref pos));
            if (Match(s, ref pos, "true")) return New(true);
            if (Match(s, ref pos, "false")) return New(false);
            if (Match(s, ref pos, "null")) return new JsonValue();
            return New(ParseNumber(s, ref pos));
        }

        private static bool Match(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length) return false;
            if (string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0) return false;
            pos += literal.Length;
            return true;
        }

        private static JsonValue ParseObject(string s, ref int pos)
        {
            JsonValue obj = NewObject();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return obj; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new FormatException("Expected object key.");
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("Expected ':'.");
                pos++;
                obj.Members[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated object.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return obj; }
                throw new FormatException("Expected ',' or '}'.");
            }
        }

        private static JsonValue ParseArray(string s, ref int pos)
        {
            JsonValue arr = NewArray();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return arr; }
            while (true)
            {
                arr.Items.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated array.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return arr; }
                throw new FormatException("Expected ',' or ']'.");
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            StringBuilder sb = new StringBuilder();
            pos++; // opening quote
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) break;
                char e = s[pos++];
                switch (e)
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
                        if (pos + 4 > s.Length) throw new FormatException("Bad unicode escape.");
                        sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("Bad escape sequence.");
                }
            }
            throw new FormatException("Unterminated string.");
        }

        private static double ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' ||
                   ((s[pos] == '-' || s[pos] == '+') && (s[pos - 1] == 'e' || s[pos - 1] == 'E')))) pos++;
            double d;
            string raw = s.Substring(start, pos - start);
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("Bad number '" + raw + "'.");
            return d;
        }
    }
}
