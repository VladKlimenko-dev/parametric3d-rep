using System.Globalization;

namespace Parametric3D.Core;

/// <summary>
/// m.x4: ЕДИНИЦЫ ИЗМЕРЕНИЯ С РАЗМЕРНОСТЬЮ. Модель хранит величины во внутренних единицах: длина — мм, масса — кг, время — с,
/// угол — градус (как принимают операции и формулы: тригонометрия формул — в градусах), температура — К, сила тока — А.
/// Пересчёт — только на границе ввода и отображения: документ задаёт свою систему единиц, пользователь может смотреть в своей,
/// модель от этого не меняется. Множители единиц — точные определения (дюйм = 25.4 мм, фунт = 0.45359237 кг).
/// </summary>
public readonly record struct Dimension(sbyte L, sbyte M, sbyte T, sbyte A, sbyte K = 0, sbyte I = 0)
{
    public static readonly Dimension None = new(0, 0, 0, 0);
    public static readonly Dimension Length = new(1, 0, 0, 0), Mass = new(0, 1, 0, 0), Time = new(0, 0, 1, 0), Angle = new(0, 0, 0, 1);
    public static readonly Dimension Area = new(2, 0, 0, 0), Volume = new(3, 0, 0, 0);
    public static readonly Dimension Density = new(-3, 1, 0, 0), Force = new(1, 1, -2, 0), Pressure = new(-1, 1, -2, 0);
    public static readonly Dimension Velocity = new(1, 0, -1, 0), Acceleration = new(1, 0, -2, 0), Energy = new(2, 1, -2, 0);
    public static readonly Dimension Temperature = new(0, 0, 0, 0, 1), Current = new(0, 0, 0, 0, 0, 1);
    public static Dimension operator *(Dimension a, Dimension b) => new((sbyte)(a.L + b.L), (sbyte)(a.M + b.M), (sbyte)(a.T + b.T), (sbyte)(a.A + b.A), (sbyte)(a.K + b.K), (sbyte)(a.I + b.I));
    public static Dimension operator /(Dimension a, Dimension b) => new((sbyte)(a.L - b.L), (sbyte)(a.M - b.M), (sbyte)(a.T - b.T), (sbyte)(a.A - b.A), (sbyte)(a.K - b.K), (sbyte)(a.I - b.I));
    public Dimension Pow(int n) => new((sbyte)(L * n), (sbyte)(M * n), (sbyte)(T * n), (sbyte)(A * n), (sbyte)(K * n), (sbyte)(I * n));
    /// <summary>Корень степени n — если все показатели делятся на n; иначе null.</summary>
    public Dimension? Root(int n) => L % n == 0 && M % n == 0 && T % n == 0 && A % n == 0 && K % n == 0 && I % n == 0
        ? new Dimension((sbyte)(L / n), (sbyte)(M / n), (sbyte)(T / n), (sbyte)(A / n), (sbyte)(K / n), (sbyte)(I / n)) : null;
    public bool IsNone => this == None;
    public string Name => Units.DimensionName(this);
    public override string ToString() => Name;
}

/// <summary>Единица: обозначение, размерность и множитель к внутренней единице этой размерности (value_internal = v·Factor).</summary>
public sealed record Unit(string Symbol, Dimension Dim, double Factor, string Title)
{
    public double ToInternal(double v) => v * Factor;
    public double FromInternal(double v) => v / Factor;
    public override string ToString() => Symbol;
}

/// <summary>Величина: значение во внутренних единицах и размерность. Bare — число без единицы (берёт единицу документа по месту).</summary>
public readonly record struct Quantity(double Value, Dimension Dim, bool Bare = false)
{
    public static Quantity Number(double v) => new(v, Dimension.None, true);
    public override string ToString() => $"{Value.ToString("R", CultureInfo.InvariantCulture)} [{Dim}]";
}

/// <summary>Система единиц показа и ввода: единица для каждой размерности, которую надо показать.</summary>
public sealed class UnitSystem
{
    public string Length = "mm", Mass = "kg", Time = "s", Angle = "deg", Temperature = "K";
    /// <summary>Единицы производных размерностей (площадь, объём, плотность, сила, давление…); нет — из базовых.</summary>
    public readonly Dictionary<Dimension, string> Derived = new();

    public static UnitSystem Metric => new();
    public static UnitSystem MetricMeters => new() { Length = "m" };
    public static UnitSystem Imperial
    {
        get
        {
            var s = new UnitSystem { Length = "in", Mass = "lb", Time = "s", Angle = "deg" };
            s.Derived[Dimension.Force] = "lbf"; s.Derived[Dimension.Pressure] = "psi"; s.Derived[Dimension.Density] = "lb/in^3";
            return s;
        }
    }

    public UnitSystem Clone() { var c = new UnitSystem { Length = Length, Mass = Mass, Time = Time, Angle = Angle, Temperature = Temperature }; foreach (var kv in Derived) c.Derived[kv.Key] = kv.Value; return c; }

    /// <summary>Единица показа размерности: явная — из Derived или базовых, иначе составная из базовых (мм², кг/мм³…).</summary>
    public Unit UnitFor(Dimension d)
    {
        if (Derived.TryGetValue(d, out var sym)) return Units.Parse(sym);
        if (d == Dimension.Length) return Units.Parse(Length);
        if (d == Dimension.Mass) return Units.Parse(Mass);
        if (d == Dimension.Time) return Units.Parse(Time);
        if (d == Dimension.Angle) return Units.Parse(Angle);
        if (d == Dimension.Temperature) return Units.Parse(Temperature);
        if (d.IsNone) return Units.One;
        // составная: длина^L · масса^M · время^T · угол^A
        var parts = new List<string>(); double f = 1;
        void Add(string s, int e) { if (e == 0) return; var u = Units.Parse(s); f *= Math.Pow(u.Factor, e); parts.Add(e == 1 ? s : $"{s}^{e}"); }
        Add(Mass, d.M); Add(Length, d.L); Add(Time, d.T); Add(Angle, d.A); Add(Temperature, d.K);
        if (d.I != 0) { f *= 1; parts.Add(d.I == 1 ? "A" : $"A^{d.I}"); }
        return new Unit(string.Join("·", parts), d, f, "composite");
    }

    public Dictionary<string, string> ToDictionary()
    {
        var r = new Dictionary<string, string> { ["length"] = Length, ["mass"] = Mass, ["time"] = Time, ["angle"] = Angle, ["temperature"] = Temperature };
        foreach (var kv in Derived) r[Units.DimensionKey(kv.Key)] = kv.Value;
        return r;
    }

    public static UnitSystem FromDictionary(IReadOnlyDictionary<string, string>? d)
    {
        var s = new UnitSystem(); if (d == null) return s;
        foreach (var (k, v) in d)
        {
            var u = Units.Parse(v);                                         // неизвестная единица — исключение с причиной
            switch (k)
            {
                case "length": Check(u, Dimension.Length); s.Length = v; break;
                case "mass": Check(u, Dimension.Mass); s.Mass = v; break;
                case "time": Check(u, Dimension.Time); s.Time = v; break;
                case "angle": Check(u, Dimension.Angle); s.Angle = v; break;
                case "temperature": Check(u, Dimension.Temperature); s.Temperature = v; break;
                default: s.Derived[u.Dim] = v; break;
            }
        }
        return s;
        static void Check(Unit u, Dimension want) { if (u.Dim != want) throw new InvalidDataException($"unit '{u.Symbol}' is {u.Dim}, expected {want}"); }
    }
}

public static class Units
{
    public static readonly Unit One = new("", Dimension.None, 1, "number");
    static readonly Dictionary<string, Unit> Catalog = new(StringComparer.Ordinal);

    static void Def(string sym, Dimension d, double f, string title, params string[] aliases)
    {
        var u = new Unit(sym, d, f, title); Catalog[sym] = u; foreach (var a in aliases) Catalog[a] = u with { Symbol = sym };
    }

    static Units()
    {
        var L = Dimension.Length; var M = Dimension.Mass; var T = Dimension.Time; var A = Dimension.Angle;
        // длина (внутренняя — мм)
        Def("nm", L, 1e-6, "nanometre"); Def("um", L, 1e-3, "micrometre", "µm", "μm"); Def("mm", L, 1, "millimetre");
        Def("cm", L, 10, "centimetre"); Def("dm", L, 100, "decimetre"); Def("m", L, 1000, "metre"); Def("km", L, 1e6, "kilometre");
        Def("in", L, 25.4, "inch", "\"", "inch"); Def("ft", L, 304.8, "foot", "'", "feet"); Def("yd", L, 914.4, "yard"); Def("mil", L, 0.0254, "thou", "thou");
        Def("mi", L, 1609344, "mile");
        // масса (кг)
        Def("mg", M, 1e-6, "milligram"); Def("g", M, 1e-3, "gram"); Def("kg", M, 1, "kilogram"); Def("t", M, 1000, "tonne");
        Def("lb", M, 0.45359237, "pound", "lbm"); Def("oz", M, 0.45359237 / 16, "ounce"); Def("slug", M, 14.593902937206364, "slug");
        // время (с)
        Def("ms", T, 1e-3, "millisecond"); Def("s", T, 1, "second", "sec"); Def("min", T, 60, "minute"); Def("h", T, 3600, "hour", "hr");
        // угол (градус)
        Def("deg", A, 1, "degree", "°"); Def("rad", A, 180 / Math.PI, "radian"); Def("grad", A, 0.9, "gradian"); Def("rev", A, 360, "revolution", "turn");
        Def("arcmin", A, 1.0 / 60, "arcminute"); Def("arcsec", A, 1.0 / 3600, "arcsecond");
        // температура (К; только разности — смещения шкал не применяются к величинам формул)
        Def("K", Dimension.Temperature, 1, "kelvin");
        Def("A", Dimension.Current, 1, "ampere");
        // площадь и объём (мм², мм³)
        Def("ha", Dimension.Area, 1e10, "hectare"); Def("L", Dimension.Volume, 1e6, "litre", "l"); Def("mL", Dimension.Volume, 1e3, "millilitre", "ml");
        Def("gal", Dimension.Volume, 3785411.784, "US gallon");
        // сила, давление, энергия (из кг, мм, с: Н = кг·м/с² = 1000 кг·мм/с²)
        Def("N", Dimension.Force, 1000, "newton"); Def("kN", Dimension.Force, 1e6, "kilonewton"); Def("lbf", Dimension.Force, 4448.2216152605, "pound-force");
        Def("Pa", Dimension.Pressure, 1e-3, "pascal"); Def("kPa", Dimension.Pressure, 1, "kilopascal"); Def("MPa", Dimension.Pressure, 1e3, "megapascal");
        Def("GPa", Dimension.Pressure, 1e6, "gigapascal"); Def("bar", Dimension.Pressure, 100, "bar"); Def("psi", Dimension.Pressure, 6.894757293168361, "psi");
        Def("J", Dimension.Energy, 1e6, "joule");
    }

    public static bool IsUnitSymbol(string s) => Catalog.ContainsKey(s);
    public static Unit? Find(string s) => Catalog.TryGetValue(s, out var u) ? u : null;

    /// <summary>
    /// Разбор обозначения единицы: «mm», «kg/m^3», «N·m», «mm^2», «g/cm³», «in*lbf». Неизвестное — исключение с причиной.
    /// </summary>
    public static Unit Parse(string text)
    {
        if (TryParse(text, out var u, out var why)) return u!;
        throw new FormatException(why);
    }

    public static bool TryParse(string text, out Unit? unit, out string? why)
    {
        unit = null; why = null;
        string s = (text ?? "").Trim().Replace("²", "^2").Replace("³", "^3").Replace("·", "*").Replace(" ", "");
        if (s.Length == 0) { unit = One; return true; }
        if (Catalog.TryGetValue(s, out var direct)) { unit = direct; return true; }
        double f = 1; var d = Dimension.None; int i = 0; int sign = 1; bool first = true;
        while (i < s.Length)
        {
            if (!first)
            {
                if (s[i] == '*') sign = 1; else if (s[i] == '/') sign = -1; else { why = $"unit '{text}': expected '*' or '/' at '{s[i..]}'"; return false; }
                i++;
            }
            else if (s[i] == '1' && i + 1 < s.Length && s[i + 1] == '/') { i += 2; sign = -1; first = false; continue; }
            int j = i; while (j < s.Length && s[j] != '*' && s[j] != '/' && s[j] != '^') j++;
            string sym = s[i..j];
            if (!Catalog.TryGetValue(sym, out var a)) { why = $"unknown unit '{sym}' in '{text}'"; return false; }
            int e = 1; i = j;
            if (i < s.Length && s[i] == '^')
            {
                i++; int k = i; if (k < s.Length && s[k] == '-') k++; while (k < s.Length && char.IsDigit(s[k])) k++;
                if (!int.TryParse(s[i..k], out e)) { why = $"unit '{text}': bad exponent"; return false; }
                i = k;
            }
            f *= Math.Pow(a.Factor, sign * e); d = d * a.Dim.Pow(sign * e); first = false;
        }
        unit = new Unit(text.Trim(), d, f, "composite"); return true;
    }

    public static double Convert(double v, string from, string to)
    {
        var a = Parse(from); var b = Parse(to);
        if (a.Dim != b.Dim) throw new InvalidOperationException($"cannot convert {a.Dim} ({from}) to {b.Dim} ({to})");
        return b.FromInternal(a.ToInternal(v));
    }

    public static string Format(Quantity q, UnitSystem sys, int decimals = 4)
    {
        var u = sys.UnitFor(q.Dim);
        string num = u.FromInternal(q.Value).ToString("0." + new string('#', Math.Max(0, decimals)), CultureInfo.InvariantCulture);
        return u.Symbol.Length == 0 ? num : $"{num} {u.Symbol}";
    }

    public static string DimensionName(Dimension d)
    {
        if (d == Dimension.None) return "number";
        if (d == Dimension.Length) return "length"; if (d == Dimension.Area) return "area"; if (d == Dimension.Volume) return "volume";
        if (d == Dimension.Mass) return "mass"; if (d == Dimension.Time) return "time"; if (d == Dimension.Angle) return "angle";
        if (d == Dimension.Density) return "density"; if (d == Dimension.Force) return "force"; if (d == Dimension.Pressure) return "pressure";
        if (d == Dimension.Velocity) return "velocity"; if (d == Dimension.Acceleration) return "acceleration"; if (d == Dimension.Energy) return "energy";
        if (d == Dimension.Temperature) return "temperature"; if (d == Dimension.Current) return "current";
        return $"L^{d.L} M^{d.M} T^{d.T} A^{d.A}" + (d.K != 0 ? $" K^{d.K}" : "") + (d.I != 0 ? $" I^{d.I}" : "");
    }

    public static string DimensionKey(Dimension d) => DimensionName(d).Replace(" ", "");
}
