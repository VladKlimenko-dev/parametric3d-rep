using Parametric3D.Core;

namespace Parametric3D.Model.Params;

/// <summary>Источник геометрических величин для функций Length/Area/Volume.</summary>
public interface IGeometryResolver
{
    bool TryLength(string reference, out double value);
    bool TryArea(string reference, out double value);
    bool TryVolume(string reference, out double value);
}

/// <summary>Источник значений именованных параметров.</summary>
public interface IValueResolver
{
    bool TryValue(string name, out double value);
}

public abstract class Expr
{
    public abstract double Eval(IValueResolver vals, IGeometryResolver? geom);
    public abstract void CollectRefs(HashSet<string> dst);
    public override string ToString() => Print();
    public abstract string Print();
}

public sealed class NumExpr : Expr
{
    public readonly double V;
    public NumExpr(double v) { V = v; }
    public override double Eval(IValueResolver v, IGeometryResolver? g) => V;
    public override void CollectRefs(HashSet<string> dst) { }
    public override string Print() => V.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class RefExpr : Expr
{
    public readonly string Name;
    public RefExpr(string name) { Name = name; }
    public override double Eval(IValueResolver v, IGeometryResolver? g)
    {
        if (v.TryValue(Name, out double x)) return x;
        throw new P3DException($"Unknown parameter '{Name}'.");
    }
    public override void CollectRefs(HashSet<string> dst) => dst.Add(Name);
    public override string Print() => Name;
}

public sealed class UnaryExpr : Expr
{
    public readonly char Op; public readonly Expr A;
    public UnaryExpr(char op, Expr a) { Op = op; A = a; }
    public override double Eval(IValueResolver v, IGeometryResolver? g)
        => Op == '-' ? -A.Eval(v, g) : A.Eval(v, g);
    public override void CollectRefs(HashSet<string> dst) => A.CollectRefs(dst);
    public override string Print() => $"{Op}{A.Print()}";
}

public sealed class BinExpr : Expr
{
    public readonly string Op; public readonly Expr A, B;
    public BinExpr(string op, Expr a, Expr b) { Op = op; A = a; B = b; }

    public override double Eval(IValueResolver v, IGeometryResolver? g)
    {
        double a = A.Eval(v, g), b = B.Eval(v, g);
        switch (Op)
        {
            case "+": return a + b;
            case "-": return a - b;
            case "*": return a * b;
            case "/":
                if (Math.Abs(b) < 1e-15) throw new P3DException("Division by zero.");
                return a / b;
            case "^": return Math.Pow(a, b);
            case "%": return a / 100.0 * b;   // «a% of b»
            default: throw new P3DException($"Unknown operator '{Op}'.");
        }
    }
    public override void CollectRefs(HashSet<string> dst) { A.CollectRefs(dst); B.CollectRefs(dst); }
    public override string Print() => $"({A.Print()} {Op} {B.Print()})";
}

public sealed class CallExpr : Expr
{
    public readonly string Name;
    public readonly List<Expr> Args;
    /// <summary>Для Length(path)/Area(surface)/Volume(part): имя ссылки, если аргумент — идентификатор.</summary>
    public readonly string? GeomRef;

    public CallExpr(string name, List<Expr> args)
    {
        Name = name; Args = args;
        GeomRef = args.Count == 1 && args[0] is RefExpr r ? r.Name : null;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;

    public override double Eval(IValueResolver v, IGeometryResolver? g)
    {
        string n = Name.ToLowerInvariant();

        // Геометрические функции с ссылочным аргументом.
        if (GeomRef != null && g != null)
        {
            switch (n)
            {
                case "length": if (g.TryLength(GeomRef, out double L)) return L; break;
                case "area": if (g.TryArea(GeomRef, out double A)) return A; break;
                case "volume": if (g.TryVolume(GeomRef, out double V)) return V; break;
            }
        }

        double[] a = Args.Select(x => x.Eval(v, g)).ToArray();

        switch (n)
        {
            case "sin": Need(a, 1); return Math.Sin(Deg2Rad(a[0]));
            case "cos": Need(a, 1); return Math.Cos(Deg2Rad(a[0]));
            case "tg":
            case "tan": Need(a, 1); return Math.Tan(Deg2Rad(a[0]));
            case "asin": Need(a, 1); return Rad2Deg(Math.Asin(Clamp1(a[0])));
            case "acos": Need(a, 1); return Rad2Deg(Math.Acos(Clamp1(a[0])));
            case "atg":
            case "atan": Need(a, 1); return Rad2Deg(Math.Atan(a[0]));
            case "atan2": Need(a, 2); return Rad2Deg(Math.Atan2(a[0], a[1]));
            case "sqrt": Need(a, 1); return Math.Sqrt(Math.Max(0, a[0]));
            case "abs": Need(a, 1); return Math.Abs(a[0]);
            case "floor": Need(a, 1); return Math.Floor(a[0]);
            case "ceil": Need(a, 1); return Math.Ceiling(a[0]);
            case "round": return a.Length == 2 ? Math.Round(a[0], (int)a[1]) : Math.Round(a[0]);
            case "min": Need(a, 2); return Math.Min(a[0], a[1]);
            case "max": Need(a, 2); return Math.Max(a[0], a[1]);
            case "pi": return Math.PI;
            case "length":
                if (a.Length == 2) return Math.Sqrt(a[0] * a[0] + a[1] * a[1]);
                if (a.Length == 3) return Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
                throw new P3DException("Length: expected 2 or 3 numbers, or a geometry reference.");
            case "area": throw new P3DException("Area: expected a surface reference.");
            case "volume": throw new P3DException("Volume: expected a part reference.");
        }
        throw new P3DException($"Unknown function '{Name}'.");
    }

    private static void Need(double[] a, int n)
    {
        if (a.Length != n) throw new P3DException($"Expected {n} argument(s).");
    }
    private static double Clamp1(double x) => Math.Clamp(x, -1.0, 1.0);

    public override void CollectRefs(HashSet<string> dst)
    {
        // Ссылка на геометрию не является параметром — в граф зависимостей не идёт.
        if (GeomRef != null) return;
        foreach (var e in Args) e.CollectRefs(dst);
    }

    public override string Print() => $"{Name}({string.Join(", ", Args.Select(x => x.Print()))})";
}

/// <summary>
/// Рекурсивный нисходящий разбор формул.
///
/// Грамматика (по возрастанию приоритета):
///   expr    := term (('+' | '-') term)*
///   term    := power (('*' | '/' | '%' ['of']) power)*
///   power   := unary ('^' power)?          -- правоассоциативно
///   unary   := ['-' | '+'] atom
///   atom    := number | ident '(' args ')' | ident | '(' expr ')'
///
/// Углы тригонометрии — в ГРАДУСАХ (принято в CAD). «a% of b» = a/100*b.
/// </summary>
public static class ExprParser
{
    public static Expr Parse(string src)
    {
        var p = new Parser(src);
        Expr e = p.ParseExpr();
        p.ExpectEnd();
        return e;
    }

    public static bool TryParse(string src, out Expr? expr, out string? error)
    {
        try { expr = Parse(src); error = null; return true; }
        catch (Exception ex) { expr = null; error = ex.Message; return false; }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public Parser(string s) { _s = s ?? ""; _i = 0; }

        private void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private bool Eof { get { Skip(); return _i >= _s.Length; } }
        private char Cur { get { Skip(); return _i < _s.Length ? _s[_i] : '\0'; } }

        private bool Take(char c)
        {
            Skip();
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        private bool TakeWord(string w)
        {
            Skip();
            if (_i + w.Length > _s.Length) return false;
            if (string.Compare(_s, _i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            int after = _i + w.Length;
            if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_')) return false;
            _i = after;
            return true;
        }

        public void ExpectEnd()
        {
            if (!Eof) throw new P3DException($"Unexpected trailing text at position {_i}: '{_s[_i..]}'.");
        }

        public Expr ParseExpr()
        {
            Expr a = ParseTerm();
            while (true)
            {
                if (Take('+')) a = new BinExpr("+", a, ParseTerm());
                else if (Take('-')) a = new BinExpr("-", a, ParseTerm());
                else return a;
            }
        }

        private Expr ParseTerm()
        {
            Expr a = ParsePower();
            while (true)
            {
                if (Take('*')) a = new BinExpr("*", a, ParsePower());
                else if (Take('/')) a = new BinExpr("/", a, ParsePower());
                else if (Take('%'))
                {
                    TakeWord("of");                      // «a% of b» и «a % b» — одно и то же
                    a = new BinExpr("%", a, ParsePower());
                }
                else return a;
            }
        }

        private Expr ParsePower()
        {
            Expr a = ParseUnary();
            if (Take('^')) return new BinExpr("^", a, ParsePower());
            return a;
        }

        private Expr ParseUnary()
        {
            if (Take('-')) return new UnaryExpr('-', ParseUnary());
            if (Take('+')) return ParseUnary();
            return ParseAtom();
        }

        private Expr ParseAtom()
        {
            Skip();
            if (Eof) throw new P3DException("Formula ends unexpectedly.");

            if (Take('('))
            {
                Expr e = ParseExpr();
                if (!Take(')')) throw new P3DException("Unbalanced parenthesis.");
                return e;
            }

            char c = Cur;
            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c) || c == '_') return ParseIdentOrCall();

            throw new P3DException($"Unexpected character '{c}' at position {_i}.");
        }

        private Expr ParseNumber()
        {
            Skip();
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                int save = _i;
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                if (_i < _s.Length && char.IsDigit(_s[_i])) { while (_i < _s.Length && char.IsDigit(_s[_i])) _i++; }
                else _i = save;
            }
            string t = _s[start.._i];
            if (!double.TryParse(t, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double v))
                throw new P3DException($"Not a number: '{t}'.");
            return new NumExpr(v);
        }

        private Expr ParseIdentOrCall()
        {
            Skip();
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == ':' || _s[_i] == '.')) _i++;
            string name = _s[start.._i];

            if (Take('('))
            {
                var args = new List<Expr>();
                if (!Take(')'))
                {
                    do { args.Add(ParseExpr()); } while (Take(','));
                    if (!Take(')')) throw new P3DException($"Unbalanced parenthesis in call to '{name}'.");
                }
                return new CallExpr(name, args);
            }

            if (string.Equals(name, "pi", StringComparison.OrdinalIgnoreCase)) return new NumExpr(Math.PI);
            return new RefExpr(name);
        }
    }
}
