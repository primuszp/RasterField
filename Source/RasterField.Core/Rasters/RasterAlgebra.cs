using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RasterField.Rasters
{
    /// <summary>Thrown when a raster-algebra expression cannot be parsed or references an unknown band/function.</summary>
    public sealed class RasterAlgebraException : Exception
    {
        /// <summary>Creates the exception with a message describing the parse or evaluation failure.</summary>
        public RasterAlgebraException(string message) : base(message) { }
    }

    /// <summary>
    /// Evaluates a small arithmetic expression language over one or more same-sized rasters —
    /// band math, e.g. an NDVI-style index <c>(b1 - b2) / (b1 + b2)</c>.
    /// </summary>
    /// <remarks>
    /// Supported grammar: <c>+ - * /</c>, unary minus, parentheses, numeric literals (decimal or
    /// exponent form), the constants <c>pi</c>/<c>e</c>, band names (however you name them in the
    /// dictionary passed to <see cref="Evaluate"/>), comparisons <c>== != &lt; &lt;= &gt; &gt;=</c>
    /// (yielding 1.0/0.0), and the functions <c>abs, sqrt, exp, log, log10, min, max, pow</c> and
    /// <c>iif(condition, ifTrue, ifFalse)</c>. If <i>any</i> band referenced anywhere in the
    /// expression is no-data at a cell, that cell's output is no-data — the expression is not
    /// evaluated for it (so a division by a would-be-zero no-data cell never runs).
    /// </remarks>
    public static class RasterAlgebra
    {
        /// <summary>Parses and evaluates <paramref name="expression"/> over <paramref name="bands"/>, producing a new raster of the same size.</summary>
        public static Raster Evaluate(string expression, IReadOnlyDictionary<string, Raster> bands, double? outputNoDataValue = null)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("An expression is required.", nameof(expression));
            if (bands == null || bands.Count == 0) throw new ArgumentException("At least one band is required.", nameof(bands));

            var order = bands.Keys.ToList();
            int width = -1, height = -1;
            foreach (var name in order)
            {
                var b = bands[name];
                if (width < 0) { width = b.Width; height = b.Height; }
                else if (b.Width != width || b.Height != height)
                    throw new ArgumentException("All bands must have the same dimensions.", nameof(bands));
            }

            var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++) indexOf[order[i]] = i;

            var used = new HashSet<int>();
            var tokens = Tokenize(expression);
            int pos = 0;
            Node root = ParseComparison(tokens, ref pos, indexOf, used);
            if (tokens[pos].Type != TokType.End)
                throw new RasterAlgebraException($"Unexpected '{tokens[pos].Text}' after the expression.");

            double noData = outputNoDataValue ?? order.Select(n => bands[n].NoDataValue).FirstOrDefault(v => !double.IsNaN(v));
            var result = new Raster(width, height, noData);

            var sourceArrays = order.Select(n => bands[n]).ToArray();
            var values = new double[order.Count];
            int[] usedIndices = used.ToArray();
            int cellCount = width * height;

            for (int p = 0; p < cellCount; p++)
            {
                bool anyNoData = false;
                foreach (int idx in usedIndices)
                {
                    float v = sourceArrays[idx].Samples[p];
                    if (sourceArrays[idx].IsNoData(v)) { anyNoData = true; break; }
                    values[idx] = v;
                }

                result.Samples[p] = anyNoData ? float.NaN : (float)root.Evaluate(values);
            }

            result.InvalidateStatistics();
            return result;
        }

        // ---- AST -------------------------------------------------------------------

        private abstract class Node { public abstract double Evaluate(double[] v); }

        private sealed class ConstNode : Node
        {
            private readonly double _value;
            public ConstNode(double value) => _value = value;
            public override double Evaluate(double[] v) => _value;
        }

        private sealed class BandNode : Node
        {
            private readonly int _index;
            public BandNode(int index) => _index = index;
            public override double Evaluate(double[] v) => v[_index];
        }

        private sealed class UnaryNegNode : Node
        {
            private readonly Node _operand;
            public UnaryNegNode(Node operand) => _operand = operand;
            public override double Evaluate(double[] v) => -_operand.Evaluate(v);
        }

        private sealed class BinaryNode : Node
        {
            private readonly TokType _op;
            private readonly Node _left, _right;
            public BinaryNode(TokType op, Node left, Node right) { _op = op; _left = left; _right = right; }
            public override double Evaluate(double[] v)
            {
                double a = _left.Evaluate(v), b = _right.Evaluate(v);
                return _op switch
                {
                    TokType.Plus => a + b,
                    TokType.Minus => a - b,
                    TokType.Star => a * b,
                    TokType.Slash => a / b,
                    TokType.Lt => a < b ? 1.0 : 0.0,
                    TokType.Le => a <= b ? 1.0 : 0.0,
                    TokType.Gt => a > b ? 1.0 : 0.0,
                    TokType.Ge => a >= b ? 1.0 : 0.0,
                    TokType.Eq => a == b ? 1.0 : 0.0,
                    TokType.Ne => a != b ? 1.0 : 0.0,
                    _ => throw new InvalidOperationException(),
                };
            }
        }

        private sealed class FuncNode : Node
        {
            private readonly string _name;
            private readonly Node[] _args;
            public FuncNode(string name, Node[] args) { _name = name; _args = args; }
            public override double Evaluate(double[] v)
            {
                switch (_name)
                {
                    case "abs": return Math.Abs(_args[0].Evaluate(v));
                    case "sqrt": return Math.Sqrt(_args[0].Evaluate(v));
                    case "exp": return Math.Exp(_args[0].Evaluate(v));
                    case "log": return Math.Log(_args[0].Evaluate(v));
                    case "log10": return Math.Log10(_args[0].Evaluate(v));
                    case "min": return Math.Min(_args[0].Evaluate(v), _args[1].Evaluate(v));
                    case "max": return Math.Max(_args[0].Evaluate(v), _args[1].Evaluate(v));
                    case "pow": return Math.Pow(_args[0].Evaluate(v), _args[1].Evaluate(v));
                    case "iif": return _args[0].Evaluate(v) != 0.0 ? _args[1].Evaluate(v) : _args[2].Evaluate(v);
                    default: throw new InvalidOperationException();
                }
            }
        }

        private static readonly Dictionary<string, int> FunctionArity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["abs"] = 1, ["sqrt"] = 1, ["exp"] = 1, ["log"] = 1, ["log10"] = 1,
            ["min"] = 2, ["max"] = 2, ["pow"] = 2, ["iif"] = 3,
        };

        // ---- tokenizer ---------------------------------------------------------------

        private enum TokType { Number, Ident, Plus, Minus, Star, Slash, LParen, RParen, Comma, Lt, Le, Gt, Ge, Eq, Ne, End }

        private readonly struct Token
        {
            public Token(TokType type, string text = "", double number = 0) { Type = type; Text = text; Number = number; }
            public TokType Type { get; }
            public string Text { get; }
            public double Number { get; }
        }

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                    {
                        i++;
                        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                    }
                    string numText = s.Substring(start, i - start);
                    tokens.Add(new Token(TokType.Number, number: double.Parse(numText, CultureInfo.InvariantCulture)));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    tokens.Add(new Token(TokType.Ident, s.Substring(start, i - start)));
                    continue;
                }

                switch (c)
                {
                    case '+': tokens.Add(new Token(TokType.Plus)); i++; break;
                    case '-': tokens.Add(new Token(TokType.Minus)); i++; break;
                    case '*': tokens.Add(new Token(TokType.Star)); i++; break;
                    case '/': tokens.Add(new Token(TokType.Slash)); i++; break;
                    case '(': tokens.Add(new Token(TokType.LParen)); i++; break;
                    case ')': tokens.Add(new Token(TokType.RParen)); i++; break;
                    case ',': tokens.Add(new Token(TokType.Comma)); i++; break;
                    case '<':
                        i++;
                        if (i < s.Length && s[i] == '=') { tokens.Add(new Token(TokType.Le)); i++; }
                        else tokens.Add(new Token(TokType.Lt));
                        break;
                    case '>':
                        i++;
                        if (i < s.Length && s[i] == '=') { tokens.Add(new Token(TokType.Ge)); i++; }
                        else tokens.Add(new Token(TokType.Gt));
                        break;
                    case '=':
                        i++;
                        if (i < s.Length && s[i] == '=') { tokens.Add(new Token(TokType.Eq)); i++; }
                        else throw new RasterAlgebraException("Expected '==' (a single '=' is not valid).");
                        break;
                    case '!':
                        i++;
                        if (i < s.Length && s[i] == '=') { tokens.Add(new Token(TokType.Ne)); i++; }
                        else throw new RasterAlgebraException("Unexpected '!' (did you mean '!='?).");
                        break;
                    default:
                        throw new RasterAlgebraException($"Unexpected character '{c}' in the expression.");
                }
            }
            tokens.Add(new Token(TokType.End));
            return tokens;
        }

        // ---- recursive-descent parser --------------------------------------------------

        private static Node ParseComparison(List<Token> t, ref int pos, Dictionary<string, int> indexOf, HashSet<int> used)
        {
            Node left = ParseAdditive(t, ref pos, indexOf, used);
            TokType op = t[pos].Type;
            if (op is TokType.Lt or TokType.Le or TokType.Gt or TokType.Ge or TokType.Eq or TokType.Ne)
            {
                pos++;
                Node right = ParseAdditive(t, ref pos, indexOf, used);
                return new BinaryNode(op, left, right);
            }
            return left;
        }

        private static Node ParseAdditive(List<Token> t, ref int pos, Dictionary<string, int> indexOf, HashSet<int> used)
        {
            Node node = ParseMultiplicative(t, ref pos, indexOf, used);
            while (t[pos].Type is TokType.Plus or TokType.Minus)
            {
                TokType op = t[pos].Type;
                pos++;
                Node right = ParseMultiplicative(t, ref pos, indexOf, used);
                node = new BinaryNode(op, node, right);
            }
            return node;
        }

        private static Node ParseMultiplicative(List<Token> t, ref int pos, Dictionary<string, int> indexOf, HashSet<int> used)
        {
            Node node = ParseUnary(t, ref pos, indexOf, used);
            while (t[pos].Type is TokType.Star or TokType.Slash)
            {
                TokType op = t[pos].Type;
                pos++;
                Node right = ParseUnary(t, ref pos, indexOf, used);
                node = new BinaryNode(op, node, right);
            }
            return node;
        }

        private static Node ParseUnary(List<Token> t, ref int pos, Dictionary<string, int> indexOf, HashSet<int> used)
        {
            if (t[pos].Type == TokType.Minus)
            {
                pos++;
                return new UnaryNegNode(ParseUnary(t, ref pos, indexOf, used));
            }
            if (t[pos].Type == TokType.Plus) { pos++; return ParseUnary(t, ref pos, indexOf, used); }
            return ParsePrimary(t, ref pos, indexOf, used);
        }

        private static Node ParsePrimary(List<Token> t, ref int pos, Dictionary<string, int> indexOf, HashSet<int> used)
        {
            Token tok = t[pos];
            switch (tok.Type)
            {
                case TokType.Number:
                    pos++;
                    return new ConstNode(tok.Number);

                case TokType.LParen:
                {
                    pos++;
                    Node inner = ParseComparison(t, ref pos, indexOf, used);
                    Expect(t, ref pos, TokType.RParen);
                    return inner;
                }

                case TokType.Ident:
                {
                    pos++;
                    if (t[pos].Type == TokType.LParen)
                    {
                        pos++;
                        var args = new List<Node>();
                        if (t[pos].Type != TokType.RParen)
                        {
                            args.Add(ParseComparison(t, ref pos, indexOf, used));
                            while (t[pos].Type == TokType.Comma)
                            {
                                pos++;
                                args.Add(ParseComparison(t, ref pos, indexOf, used));
                            }
                        }
                        Expect(t, ref pos, TokType.RParen);
                        return BuildFunction(tok.Text, args);
                    }

                    if (string.Equals(tok.Text, "pi", StringComparison.OrdinalIgnoreCase)) return new ConstNode(Math.PI);
                    if (string.Equals(tok.Text, "e", StringComparison.OrdinalIgnoreCase)) return new ConstNode(Math.E);

                    if (!indexOf.TryGetValue(tok.Text, out int idx))
                        throw new RasterAlgebraException($"Unknown band or constant '{tok.Text}'.");
                    used.Add(idx);
                    return new BandNode(idx);
                }

                default:
                    throw new RasterAlgebraException($"Unexpected token '{(tok.Text.Length > 0 ? tok.Text : tok.Type.ToString())}' in the expression.");
            }
        }

        private static Node BuildFunction(string name, List<Node> args)
        {
            string key = name.ToLowerInvariant();
            if (!FunctionArity.TryGetValue(key, out int arity))
                throw new RasterAlgebraException($"Unknown function '{name}'.");
            if (args.Count != arity)
                throw new RasterAlgebraException($"'{name}' expects {arity} argument(s) but {args.Count} were given.");
            return new FuncNode(key, args.ToArray());
        }

        private static void Expect(List<Token> t, ref int pos, TokType type)
        {
            if (t[pos].Type != type)
                throw new RasterAlgebraException($"Expected '{type}' but found '{(t[pos].Text.Length > 0 ? t[pos].Text : t[pos].Type.ToString())}'.");
            pos++;
        }
    }
}
