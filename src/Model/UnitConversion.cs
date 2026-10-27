using System.Globalization;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model;

/// <summary>
/// k123 (замечание 27.10, 21:55, п. 3): СМЕНА ЕДИНИЦЫ ДЛИНЫ ДЕТАЛИ. Внутри модель — в миллиметрах; единица детали —
/// единица показа и ввода. Два пути:
///  • ПРЕОБРАЗОВАТЬ (convert = true): деталь прежняя, числа размеров — в новых единицах (45 in вместо 1143 мм). Формулы,
///    которые были только переводом единиц («45in», «1.5*25.4»), снимаются — остаётся значение; параметры длины
///    показываются в новой единице.
///  • НЕ ПРЕОБРАЗОВЫВАТЬ (convert = false): числа прежние, единица новая — деталь меняет размер в k раз (45 мм → 45 in):
///    координаты эскизов, длины размеров эскизов и операций, точки операций, плоскости эскизов; параметры длины —
///    то же число в новой единице. Углы, количества и доли не меняются.
/// </summary>
public static class UnitConversion
{
    public sealed class Report
    {
        public int SketchDims, FeatureValues, Parameters, FormulasDropped, Points;
        public double Scale = 1;
        public override string ToString() => $"dims {SketchDims}, feature values {FeatureValues}, parameters {Parameters}, formulas dropped {FormulasDropped}, points {Points}, scale {Scale:0.####}";
    }

    static readonly Regex NumUnit = new(@"^\s*([-+]?\d+(?:[.,]\d+)?(?:[eE][-+]?\d+)?)\s*([A-Za-z""']+)\s*$", RegexOptions.Compiled);
    static readonly Regex NumMul = new(@"^\s*([-+]?\d+(?:[.,]\d+)?)\s*([*/])\s*(\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);
    static double D(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    /// <summary>
    /// Формула — только перевод единиц: «число единица» (единица длины) или «число × множитель», где множитель — отношение
    /// прежней и новой единиц (или обратное). number — число, как его видит пользователь в новой единице (для «не
    /// преобразовывать» — то же число).
    /// </summary>
    public static bool IsConversionOnly(string? formula, double fromMm, double toMm, out double literal)
    {
        literal = 0;
        if (string.IsNullOrWhiteSpace(formula)) return false;
        var m = NumUnit.Match(formula);
        if (m.Success && Units.TryParse(m.Groups[2].Value, out var u, out _) && u!.Dim == Dimension.Length) { literal = D(m.Groups[1].Value); return true; }
        m = NumMul.Match(formula);
        if (m.Success)
        {
            double f = D(m.Groups[3].Value);
            double r1 = fromMm / toMm, r2 = toMm / fromMm;
            bool ratio = Math.Abs(f - r1) <= 1e-9 * Math.Max(1, r1) || Math.Abs(f - r2) <= 1e-9 * Math.Max(1, r2) || Math.Abs(f - fromMm) < 1e-9 || Math.Abs(f - toMm) < 1e-9;
            if (ratio) { literal = D(m.Groups[1].Value); return true; }
        }
        return false;
    }

    static readonly string[] NonLength = { "Angle", "Twist", "Draft", "Count", "Number", "Fraction", "Ratio", "Turns", "Revolutions", "Pitch" + "Count", "Scale", "Factor", "Percent", "Weight", "Tension", "Degree" };
    /// <summary>Поле операции — длина (по имени): углы, количества, доли — нет.</summary>
    public static bool IsLengthField(string name) => !NonLength.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)) && name is not ("N" or "M" or "K");
    static readonly string[] PointNames = { "Point", "Origin", "Center", "Position", "At", "Location", "Start", "End", "Base" };
    static bool IsPointField(string name) => PointNames.Any(x => name.Contains(x, StringComparison.Ordinal)) && !name.Contains("Dir") && !name.Contains("Normal") && !name.Contains("Axis") && !name.Contains("Id");

    public static Report Apply(Part part, ParameterTable? docParams, UnitSystem from, UnitSystem to, bool convert)
    {
        var rep = new Report();
        double fromMm = Units.TryParse(from.Length, out var fu, out _) ? fu!.Factor : 1;
        double toMm = Units.TryParse(to.Length, out var tu, out _) ? tu!.Factor : 1;
        double k = convert ? 1 : toMm / fromMm;
        rep.Scale = k;
        // эскизы
        foreach (var sk in part.Sketches)
        {
            if (!convert && Math.Abs(k - 1) > 1e-15)
            {
                var builtin = new HashSet<SkEntity>(sk.Builtins, ReferenceEqualityComparer.Instance);
                var done = new HashSet<SkPoint>(ReferenceEqualityComparer.Instance);
                foreach (var p in sk.AllPoints())
                {
                    if (builtin.Contains(p) || p.Owner is { } o && builtin.Contains(o) || !done.Add(p)) continue;
                    p.X *= k; p.Y *= k; rep.Points++;
                }
                foreach (var e in sk.Entities)
                {
                    if (e is SkCircle c) c.Radius *= k;
                    if (e is SkEllipse el) { el.RU *= k; el.RV *= k; }
                }
                sk.Plane = new Frame3(sk.Plane.Origin * k, sk.Plane.U, sk.Plane.V);
                sk.BaseFrame = new Frame3(sk.BaseFrame.Origin * k, sk.BaseFrame.U, sk.BaseFrame.V);
                sk.OriginOffset *= k;
                if (sk.OriginBase is { } ob) sk.OriginBase = ob * k;
                foreach (var d in sk.Dimensions) d.TextOffset *= k;
            }
            foreach (var d in sk.Dimensions)
            {
                if (d.IsAngular) continue;
                if (IsConversionOnly(d.Formula, fromMm, toMm, out double lit))
                {
                    d.Formula = null; rep.FormulasDropped++;
                    if (!convert) d.Value = lit * toMm * Math.Sign(d.Value == 0 ? 1 : d.Value);
                }
                else if (!convert && d.Formula == null) d.Value *= k;
                rep.SketchDims++;
            }
            sk.NotifyChanged(sk);
        }
        // операции: поля длины (ValueRef) и точки
        foreach (var f in part.Features)
        {
            foreach (var fi in f.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (fi.IsInitOnly && (fi.FieldType == typeof(ValueRef) || fi.FieldType == typeof(ValueRef?) || fi.FieldType == typeof(Vector3d))) continue;
                if (fi.FieldType == typeof(ValueRef) || fi.FieldType == typeof(ValueRef?))
                {
                    if (!IsLengthField(fi.Name)) continue;
                    if (fi.GetValue(f) is not ValueRef v) continue;
                    var nv = Scale(v, fromMm, toMm, k, convert, rep);
                    fi.SetValue(f, fi.FieldType == typeof(ValueRef?) ? (ValueRef?)nv : nv);
                    rep.FeatureValues++;
                }
                else if (!convert && fi.FieldType == typeof(Vector3d) && IsPointField(fi.Name)) { fi.SetValue(f, (Vector3d)fi.GetValue(f)! * k); rep.Points++; }
                else if (!convert && fi.FieldType == typeof(List<Vector3d>) && IsPointField(fi.Name) && fi.GetValue(f) is List<Vector3d> pts)
                    for (int i = 0; i < pts.Count; i++) { pts[i] *= k; rep.Points++; }
                else if (fi.GetValue(f) is { } sub && sub.GetType().IsClass && sub.GetType().Namespace == typeof(Feature).Namespace && sub is not Feature)
                {
                    // вложенные настройки (стороны выдавливания и т. п.)
                    foreach (var si in sub.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        if ((si.FieldType == typeof(ValueRef) || si.FieldType == typeof(ValueRef?)) && IsLengthField(si.Name) && si.GetValue(sub) is ValueRef sv)
                        {
                            var nv = Scale(sv, fromMm, toMm, k, convert, rep);
                            si.SetValue(sub, si.FieldType == typeof(ValueRef?) ? (ValueRef?)nv : nv);
                            rep.FeatureValues++;
                        }
                }
            }
            part.NotifyChanged(f);
        }
        // параметры длины: единица показа — новая; не преобразовывать — то же число в новой единице
        foreach (var t in new[] { part.LocalParameters, docParams })
        {
            if (t == null) continue;
            foreach (var p in t.All)
            {
                if (string.IsNullOrWhiteSpace(p.Unit) || !Units.TryParse(p.Unit, out var pu, out _) || pu!.Dim != Dimension.Length) continue;
                if (p.Kind == ParamKind.Formula && IsConversionOnly(p.Formula, fromMm, toMm, out double lit))
                {
                    p.Kind = ParamKind.Real; p.Formula = string.Empty; rep.FormulasDropped++;
                    if (!convert) p.Number = lit * toMm;
                }
                else if (!convert && p.Kind != ParamKind.Formula) p.Number = p.Number / pu.Factor * toMm;
                p.Unit = to.Length;
                rep.Parameters++;
            }
            t.Recalculate();
        }
        part.Invalidate();
        return rep;
    }

    static ValueRef Scale(ValueRef v, double fromMm, double toMm, double k, bool convert, Report rep)
    {
        if (IsConversionOnly(v.Formula, fromMm, toMm, out double lit))
        {
            rep.FormulasDropped++;
            return new ValueRef(convert ? v.Value : lit * toMm);
        }
        if (v.Formula != null || convert) return v;
        return new ValueRef(v.Value * k);
    }
}
