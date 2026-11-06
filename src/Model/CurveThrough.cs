using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Model;

/// <summary>
/// k177: КАСАТЕЛЬНАЯ В ТОЧКЕ КРИВОЙ «ЧЕРЕЗ ТОЧКИ». Откуда точка (эскиз и его точка, вершина тела, опорная точка) и чем задана
/// касательная: «по нормали эскиза» (точка эскиза — касательная по нормали его плоскости), «по нормали поверхности» (выбранная
/// грань, сходящаяся в точке, — по её нормали в точке); обратить направление; вес (длина касательной в долях хорды).
/// Без условия — касательная по соседним точкам. Направление, найденное при построении, хранится (кривая не перестраивается
/// без правки).
/// </summary>
public sealed class CurvePointSpec
{
    /// <summary>Эскиз и его точка, из которых взята точка кривой (0 — не из эскиза).</summary>
    public uint SketchId, SketchPointId;
    /// <summary>Нормаль плоскости эскиза точки (Zero — точка не из эскиза).</summary>
    public Vector3d SketchNormal;
    /// <summary>Вершина тела (номер при выборе; 0 — нет).</summary>
    public uint VertexId;
    public bool NormalToSketch, NormalToSurface, Flip;
    /// <summary>Выбранная грань (устойчивое имя и номер при выборе) и её внешняя нормаль в точке.</summary>
    public string? FaceTag;
    public uint FaceId;
    public Vector3d FaceNormal;
    /// <summary>Грань выбрана по умолчанию (не щелчком): при смене точек кривой выбирается заново.</summary>
    public bool FaceAuto = true;
    /// <summary>Длина касательной в долях хорды соседнего участка (1 — обычная).</summary>
    public double Weight = 1;

    public bool FromSketch => SketchId != 0 && SketchNormal.LengthSquared > 0;
    /// <summary>Условие касательной задано и его направление известно.</summary>
    public Vector3d? Direction =>
        NormalToSketch && FromSketch ? SketchNormal.Normalized()
        : NormalToSurface && !NormalToSketch && FaceNormal.LengthSquared > 0 ? FaceNormal.Normalized() : null;
    public bool Constrained => Direction != null;
    public CurvePointSpec Copy() => (CurvePointSpec)MemberwiseClone();
}

/// <summary>
/// k177: ГЛАДКАЯ КРИВАЯ ЧЕРЕЗ ТОЧКИ С УСЛОВИЯМИ КАСАТЕЛЬНЫХ — кубические участки Эрмита (B-сплайн степени 3 с кратными
/// внутренними узлами, точно через точки, гладкость G1). Касательная в точке без условия — по Бесселю (среднее единичных
/// направлений соседних хорд), у свободного конца разомкнутой — отражение касательной соседа относительно хорды (параболический
/// конец). С условием — направление условия; знак — по ходу кривой (по хорде к соседям), «обратить» меняет его. Длина
/// касательной на участке — вес × хорда участка (контрольные точки на трети).
/// </summary>
public static class CurveThrough
{
    /// <summary>Касательные (единичные) в точках с учётом условий; dirs[i] — направление условия или null.</summary>
    public static Vector3d[] Tangents(IReadOnlyList<Vector3d> q, IReadOnlyList<Vector3d?> dirs, IReadOnlyList<bool> flip, bool closed)
    {
        int n = q.Count; var t = new Vector3d[n];
        Vector3d Chord(int a, int b) { var d = q[b] - q[a]; return d.LengthSquared > 0 ? d.Normalized() : Vector3d.Zero; }
        int Prev(int i) => closed ? (i - 1 + n) % n : i - 1;
        int Next(int i) => closed ? (i + 1) % n : i + 1;
        // ход кривой в точке — по соседним хордам (для знака условия и для свободных точек)
        Vector3d Run(int i)
        {
            Vector3d s = Vector3d.Zero;
            if (closed || i > 0) s += Chord(Prev(i), i);
            if (closed || i < n - 1) s += Chord(i, Next(i));
            return s.LengthSquared > 1e-24 ? s.Normalized() : (n > 1 ? Chord(0, n - 1) : Vector3d.UnitX);
        }
        var fixedDir = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (dirs[i] is { } d && d.LengthSquared > 0)
            {
                var u = d.Normalized(); if (Vector3d.Dot(u, Run(i)) < 0) u = -u;
                if (flip[i]) u = -u;
                t[i] = u; fixedDir[i] = true;
            }
            else if (closed || (i > 0 && i < n - 1)) t[i] = Run(i);
        }
        if (!closed && n >= 2)
        {
            // свободный конец — отражение касательной соседа относительно хорды
            static Vector3d Reflect(Vector3d tn, Vector3d c) => (c * (2 * Vector3d.Dot(c, tn)) - tn).Normalized();
            if (!fixedDir[0])
            {
                var c = Chord(0, 1);
                t[0] = n == 2 && !fixedDir[1] ? c : Reflect(n == 2 ? t[1] : t[1], c);
            }
            if (!fixedDir[n - 1])
            {
                var c = Chord(n - 2, n - 1);
                t[n - 1] = n == 2 && !fixedDir[0] ? c : Reflect(t[n - 2], c);
            }
        }
        return t;
    }

    /// <summary>Кривая (B-сплайн степени 3) через точки q; dirs, flip, weight — по точкам (могут быть null — без условий).</summary>
    public static SplineCurve3? Build(IReadOnlyList<Vector3d> q, IReadOnlyList<Vector3d?>? dirs, IReadOnlyList<bool>? flip, IReadOnlyList<double>? weight, bool closed)
    {
        // совпадающие соседние точки — одна
        var pts = new List<Vector3d>(); var map = new List<int>();
        for (int i = 0; i < q.Count; i++) if (pts.Count == 0 || (q[i] - pts[^1]).Length > 1e-9) { pts.Add(q[i]); map.Add(i); }
        if (closed && pts.Count > 2 && (pts[0] - pts[^1]).Length <= 1e-9) { pts.RemoveAt(pts.Count - 1); map.RemoveAt(map.Count - 1); }
        int n = pts.Count; if (n < 2) return null;
        if (closed && n < 3) closed = false;
        var d = map.Select(i => dirs != null && i < dirs.Count ? dirs[i] : null).ToList();
        var f = map.Select(i => flip != null && i < flip.Count && flip[i]).ToList();
        var w = map.Select(i => weight != null && i < weight.Count && weight[i] > 0 ? weight[i] : 1.0).ToList();
        var t = Tangents(pts, d, f, closed);
        int segs = closed ? n : n - 1;
        var cp = new List<Vector3d> { pts[0] };
        for (int s = 0; s < segs; s++)
        {
            int a = s, b = (s + 1) % n;
            double L = (pts[b] - pts[a]).Length;
            cp.Add(pts[a] + t[a] * (w[a] * L / 3));
            cp.Add(pts[b] - t[b] * (w[b] * L / 3));
            cp.Add(pts[b]);
        }
        // узлы: зажатые концы, внутренние — кратности 3 (по длине хорд)
        var kn = new List<double> { 0, 0, 0, 0 };
        double acc = 0;
        for (int s = 0; s < segs; s++)
        {
            acc += Math.Max(1e-12, (pts[(s + 1) % n] - pts[s]).Length);
            if (s < segs - 1) { kn.Add(acc); kn.Add(acc); kn.Add(acc); }
        }
        kn.Add(acc); kn.Add(acc); kn.Add(acc); kn.Add(acc);
        for (int i = 0; i < kn.Count; i++) kn[i] /= acc;
        return new SplineCurve3(cp, 3, null, kn);
    }

    /// <summary>Сплайн из одного куска (степень p, p + 1 опорная точка) — как кривая Безье (для направляющих лофта); иначе null.</summary>
    public static BezierCurve3? AsBezier(SplineCurve3 s)
    {
        if (s.ControlPoints.Count != s.Degree + 1) return null;
        if (s.Weights.Any(x => Math.Abs(x - 1) > 1e-12)) return null;
        return new BezierCurve3(s.ControlPoints);
    }
}
