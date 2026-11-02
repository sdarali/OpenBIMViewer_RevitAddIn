using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenBimViewer.Collab
{
    /// <summary>
    /// A small JSON reader and writer for what crosses the native bridge and the settings file.
    /// Objects are <see cref="Dictionary{TKey,TValue}"/>, arrays are <see cref="List{T}"/>, numbers are
    /// <see cref="double"/>. Kept in-house so the add-in ships no JSON library that could clash
    /// with the one already loaded in Revit's process.
    /// </summary>
    public static class Json
    {
        public static object? Parse(string text)
        {
            var reader = new Reader(text);
            reader.SkipWhitespace();
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw new FormatException($"Unexpected text after JSON value at {reader.Position}.");
            return value;
        }

        public static Dictionary<string, object?> ParseObject(string text) =>
            Parse(text) as Dictionary<string, object?> ?? throw new FormatException("Expected a JSON object.");

        public static string Write(object? value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        // ---- Lookups that tolerate missing or mistyped members ----

        public static string Str(this IDictionary<string, object?> obj, string key) =>
            obj.TryGetValue(key, out var v) && v is string s ? s : "";

        public static double Num(this IDictionary<string, object?> obj, string key, double fallback = 0) =>
            obj.TryGetValue(key, out var v) && v is double d ? d : fallback;

        public static bool Bool(this IDictionary<string, object?> obj, string key) =>
            obj.TryGetValue(key, out var v) && v is bool b && b;

        public static Dictionary<string, object?>? Obj(this IDictionary<string, object?> obj, string key) =>
            obj.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;

        public static List<object?> Arr(this IDictionary<string, object?> obj, string key) =>
            obj.TryGetValue(key, out var v) && v is List<object?> list ? list : new List<object?>();

        public static List<string> Strings(this IDictionary<string, object?> obj, string key)
        {
            var result = new List<string>();
            foreach (var item in obj.Arr(key))
            {
                if (item is string s) result.Add(s);
            }
            return result;
        }

        // ---- Writing ----

        private static void WriteValue(StringBuilder sb, object? value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    WriteNumber(sb, d);
                    break;
                case float f:
                    WriteNumber(sb, f);
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object?> obj:
                    sb.Append('{');
                    var first = true;
                    foreach (var pair in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        WriteValue(sb, pair.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable items:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in items)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    throw new ArgumentException($"Can't write {value.GetType().Name} as JSON.");
            }
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- Reading ----

        private sealed class Reader
        {
            private readonly string _text;
            private int _pos;

            public Reader(string text) => _text = text ?? throw new ArgumentNullException(nameof(text));

            public bool AtEnd => _pos >= _text.Length;
            public int Position => _pos;

            public void SkipWhitespace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
            }

            public object? ReadValue()
            {
                if (AtEnd) throw Error("Unexpected end of JSON");
                switch (_text[_pos])
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            private Dictionary<string, object?> ReadObject()
            {
                var obj = new Dictionary<string, object?>();
                _pos++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Error("Expected a member name");
                    var key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':') throw Error("Expected ':'");
                    _pos++;
                    SkipWhitespace();
                    obj[key] = ReadValue();
                    SkipWhitespace();
                    var c = Next();
                    if (c == '}') return obj;
                    if (c != ',') throw Error("Expected ',' or '}'");
                }
            }

            private List<object?> ReadArray()
            {
                var list = new List<object?>();
                _pos++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return list;
                }
                while (true)
                {
                    SkipWhitespace();
                    list.Add(ReadValue());
                    SkipWhitespace();
                    var c = Next();
                    if (c == ']') return list;
                    if (c != ',') throw Error("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated string");
                    var c = _text[_pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (AtEnd) throw Error("Unterminated escape");
                    var e = _text[_pos++];
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
                            if (_pos + 4 > _text.Length) throw Error("Bad \\u escape");
                            sb.Append((char)int.Parse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _pos += 4;
                            break;
                        default: throw Error("Bad escape");
                    }
                }
            }

            private double ReadNumber()
            {
                var start = _pos;
                while (_pos < _text.Length && "+-0123456789.eE".IndexOf(_text[_pos]) >= 0) _pos++;
                if (start == _pos) throw Error("Unexpected character");
                return double.Parse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw Error("Unexpected token");
                _pos += word.Length;
            }

            private char Peek() => AtEnd ? '\0' : _text[_pos];
            private char Next() => AtEnd ? '\0' : _text[_pos++];
            private FormatException Error(string what) => new FormatException($"{what} at {_pos}.");
        }
    }
}
