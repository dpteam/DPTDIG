using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dig.Core
{
    /// <summary>
    /// Tiny streaming JSON writer. C# 4 has no System.Text.Json and the project
    /// refuses external libraries, so this covers the shapes the API actually
    /// produces: objects, arrays, strings, numbers, booleans and null.
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(1024);
        // true = the current container already has at least one member
        private readonly Stack<bool> _stack = new Stack<bool>();
        private bool _afterKey;

        public JsonWriter BeginObject()
        {
            BeforeValue();
            _sb.Append('{');
            _stack.Push(false);
            return this;
        }

        public JsonWriter EndObject()
        {
            _sb.Append('}');
            if (_stack.Count > 0) _stack.Pop();
            return this;
        }

        public JsonWriter BeginArray()
        {
            BeforeValue();
            _sb.Append('[');
            _stack.Push(false);
            return this;
        }

        public JsonWriter EndArray()
        {
            _sb.Append(']');
            if (_stack.Count > 0) _stack.Pop();
            return this;
        }

        public JsonWriter Key(string name)
        {
            BeforeValue();
            WriteString(name);
            _sb.Append(':');
            _afterKey = true;
            return this;
        }

        public JsonWriter Value(string value)
        {
            BeforeValue();
            if (value == null) _sb.Append("null");
            else WriteString(value);
            return this;
        }

        public JsonWriter Value(long value)
        {
            BeforeValue();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(int value) { return Value((long)value); }

        public JsonWriter Value(double value)
        {
            BeforeValue();
            if (double.IsNaN(value) || double.IsInfinity(value)) _sb.Append('0');
            else _sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(bool value)
        {
            BeforeValue();
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonWriter Null()
        {
            BeforeValue();
            _sb.Append("null");
            return this;
        }

        public JsonWriter Raw(string json)
        {
            BeforeValue();
            _sb.Append(json ?? "null");
            return this;
        }

        // Convenience: key + value in one call.
        public JsonWriter Prop(string name, string value) { Key(name); return Value(value); }
        public JsonWriter Prop(string name, long value) { Key(name); return Value(value); }
        public JsonWriter Prop(string name, int value) { Key(name); return Value((long)value); }
        public JsonWriter Prop(string name, double value) { Key(name); return Value(value); }
        public JsonWriter Prop(string name, bool value) { Key(name); return Value(value); }

        public override string ToString() { return _sb.ToString(); }

        private void BeforeValue()
        {
            if (_afterKey)
            {
                _afterKey = false;
                return;
            }
            if (_stack.Count == 0) return;
            bool hasItems = _stack.Pop();
            if (hasItems) _sb.Append(',');
            _stack.Push(true);
        }

        private void WriteString(string value)
        {
            _sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
