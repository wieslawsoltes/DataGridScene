using System.Globalization;
using System.Text;

namespace DataGridScene.Core;

/// <summary>A bounded, non-evaluating criteria parser. No arbitrary code or reflection is executed.</summary>
public static class CriteriaParser
{
    public static GridFilter? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        if (expression.Length > 16384) throw new FormatException("The filter is limited to 16,384 characters.");
        return new Parser(expression).Parse();
    }
    public static bool TryParse(string? expression, out GridFilter? filter, out string? error)
    {
        try { filter = Parse(expression); error = null; return true; }
        catch (FormatException ex) { filter = null; error = ex.Message; return false; }
    }
    public static GridFilter? AutoFilter(string field, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) return new FieldFilter(field, FilterOperator.IsNull);
        if (text.Equals("!null", StringComparison.OrdinalIgnoreCase)) return new FieldFilter(field, FilterOperator.IsNotNull);
        foreach (var (prefix, op) in new[] { (">=", FilterOperator.GreaterOrEqual), ("<=", FilterOperator.LessOrEqual), ("!=", FilterOperator.NotEqual), ("<>", FilterOperator.NotEqual), (">", FilterOperator.Greater), ("<", FilterOperator.Less), ("=", FilterOperator.Equal) })
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return new FieldFilter(field, op, text[prefix.Length..].Trim());
        return new FieldFilter(field, FilterOperator.Contains, text);
    }

    private enum Kind { End, Identifier, Field, String, Number, Operator, Open, Close, Comma }
    private readonly record struct Token(Kind Kind, string Text, int Position);
    private sealed class Parser
    {
        private readonly string _source;
        private int _position;
        private int _depth;
        private int _nodes;
        private Token _current;
        public Parser(string source) { _source = source; _current = Next(); }
        private FormatException Error(string message) => new($"{message} (position {_current.Position + 1}).");
        private void Advance() => _current = Next();
        private bool Is(string word) => _current.Kind == Kind.Identifier && _current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);
        private bool Accept(string word) { if (!Is(word)) return false; Advance(); return true; }
        private void Expect(Kind kind) { if (_current.Kind != kind) throw Error($"Expected {kind}"); Advance(); }
        private void Word(string word) { if (!Accept(word)) throw Error($"Expected {word}"); }
        public GridFilter Parse()
        {
            var result = Or();
            if (_current.Kind != Kind.End) throw Error("Unexpected token");
            return result;
        }
        private GridFilter Or()
        {
            var first = And();
            if (!Accept("or")) return first;
            var terms = new List<GridFilter> { first, And() };
            while (Accept("or")) terms.Add(And());
            // A flat chain is one group, not a left-deep tree that trips snapshot
            // depth limits. Explicit parentheses, NOT and mixed precedence remain.
            return new FilterGroup(false, terms.AsReadOnly());
        }
        private GridFilter And()
        {
            var first = Unary();
            if (!Accept("and")) return first;
            var terms = new List<GridFilter> { first, Unary() };
            while (Accept("and")) terms.Add(Unary());
            return new FilterGroup(true, terms.AsReadOnly());
        }
        private GridFilter Unary()
        {
            if (++_nodes > 1024 || ++_depth > 32) throw Error("Filter complexity limit exceeded");
            try
            {
                if (Accept("not")) return new NotFilter(Unary());
                if (_current.Kind == Kind.Open) { Advance(); var nested = Or(); Expect(Kind.Close); return nested; }
                return Term();
            }
            finally { _depth--; }
        }
        private string Field()
        {
            if (_current.Kind is not (Kind.Field or Kind.Identifier)) throw Error("Expected a field name");
            string field = _current.Text; Advance(); return field;
        }
        private object? Literal()
        {
            var token = _current;
            if (token.Kind == Kind.String) { Advance(); return token.Text; }
            if (token.Kind == Kind.Number)
            {
                Advance();
                if (!decimal.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) throw Error("Invalid number");
                return number;
            }
            if (Accept("null")) return null;
            if (Accept("true")) return true;
            if (Accept("false")) return false;
            throw Error("Expected a quoted string, number, boolean or null");
        }
        private GridFilter Term()
        {
            string field = Field();
            if (_current.Kind == Kind.Open)
            {
                Advance();
                string argument = Field();
                if (field.Equals("IsNull", StringComparison.OrdinalIgnoreCase)) { Expect(Kind.Close); return new FieldFilter(argument, FilterOperator.IsNull); }
                Expect(Kind.Comma); object? value = Literal(); Expect(Kind.Close);
                var op = field.ToUpperInvariant() switch
                {
                    "CONTAINS" => FilterOperator.Contains,
                    "STARTSWITH" => FilterOperator.StartsWith,
                    "ENDSWITH" => FilterOperator.EndsWith,
                    _ => throw Error($"Unknown function {field}")
                };
                return new FieldFilter(argument, op, value);
            }
            if (Accept("is")) { bool negate = Accept("not"); Word("null"); return new FieldFilter(field, negate ? FilterOperator.IsNotNull : FilterOperator.IsNull); }
            bool not = Accept("not");
            GridFilter result;
            if (Accept("in"))
            {
                Expect(Kind.Open); var values = new List<object?>();
                if (_current.Kind != Kind.Close)
                {
                    values.Add(Literal());
                    while (_current.Kind == Kind.Comma) { Advance(); values.Add(Literal()); if (values.Count > 1024) throw Error("Too many values"); }
                }
                Expect(Kind.Close); result = new FieldFilter(field, FilterOperator.In, Values: values);
            }
            else if (Accept("between")) { var lower = Literal(); Word("and"); result = new FieldFilter(field, FilterOperator.Between, lower, Literal()); }
            else
            {
                if (not) throw Error("Expected IN or BETWEEN after NOT");
                if (_current.Kind != Kind.Operator) throw Error("Expected a comparison operator");
                var op = _current.Text switch
                {
                    "=" or "==" => FilterOperator.Equal, "!=" or "<>" => FilterOperator.NotEqual,
                    "<" => FilterOperator.Less, "<=" => FilterOperator.LessOrEqual,
                    ">" => FilterOperator.Greater, ">=" => FilterOperator.GreaterOrEqual,
                    _ => throw Error("Unknown comparison operator")
                };
                Advance(); result = new FieldFilter(field, op, Literal());
            }
            return not ? new NotFilter(result) : result;
        }
        private Token Next()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++;
            int start = _position;
            if (_position == _source.Length) return new(Kind.End, "", start);
            char c = _source[_position++];
            if (c == '(') return new(Kind.Open, "(", start);
            if (c == ')') return new(Kind.Close, ")", start);
            if (c == ',') return new(Kind.Comma, ",", start);
            if (c is '\'' or '"' or '[')
            {
                char end = c == '[' ? ']' : c;
                var text = new StringBuilder();
                while (_position < _source.Length)
                {
                    char current = _source[_position++];
                    if (current == end)
                    {
                        if (_position < _source.Length && _source[_position] == end) { _position++; text.Append(end); continue; }
                        return new(c == '[' ? Kind.Field : Kind.String, text.ToString(), start);
                    }
                    text.Append(current);
                }
                throw new FormatException($"Unterminated quoted value at position {start + 1}.");
            }
            if (c is '<' or '>' or '=' or '!')
            {
                if (_position < _source.Length && (_source[_position] == '=' || c == '<' && _source[_position] == '>')) _position++;
                return new(Kind.Operator, _source[start.._position], start);
            }
            if (char.IsDigit(c) || c is '-' or '+' or '.')
            {
                while (_position < _source.Length && (char.IsDigit(_source[_position]) || _source[_position] is '.' or 'e' or 'E' or '+' or '-')) _position++;
                return new(Kind.Number, _source[start.._position], start);
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] is '_' or '.')) _position++;
                return new(Kind.Identifier, _source[start.._position], start);
            }
            throw new FormatException($"Unexpected character '{c}' at position {start + 1}.");
        }
    }
}
