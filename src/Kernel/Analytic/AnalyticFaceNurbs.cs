using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// KRN-07 (07.8), шаг N1a: ГРАНЬ NURBS С ЕСТЕСТВЕННОЙ ГРАНИЦЕЙ — «пассивная» грань общей булевой, как лента катящегося шара:
/// пересечение с другой гранью не строится (выпуклые оболочки врозь — пусто, иначе отказ маршрута), а классификатор
/// пересекает её лучом точно (затравка — треугольники грани, уточнение Ньютоном на самой поверхности). Граница грани —
/// прямоугольник параметров [ua, ub] × [va, vb] (каждая точка каждого ребра лежит на его стороне — иначе отказ: обрезанная
/// грань — следующий шаг), поэтому принадлежность точки — сравнением параметров, без ломаных. Габарит — по опорным точкам
/// (выпуклая оболочка при положительных весах — строгая граница поверхности). Мутация P3D_NURBSBOOL=0 — прежний отказ.
/// </summary>
public sealed partial class AnalyticFace
{
    public bool Param;
    internal Surfaces.NurbsSurface? ParamS;
    internal double PUa, PUb, PVa, PVb;            // прямоугольник параметров грани
    internal double PSpeedU, PSpeedV;              // верхние оценки |∂S/∂u|, |∂S/∂v| (мм на единицу параметра) с запасом
    internal Vector3d ParamLo, ParamHi;
    internal List<(Vector3d A, Vector3d B, Vector3d C)>? ParamTris;
    internal static readonly bool NurbsBoolOff = Environment.GetEnvironmentVariable("P3D_NURBSBOOL") == "0";
    static readonly bool WholeOff = Environment.GetEnvironmentVariable("P3D_NURBSWHOLE") == "0";

    static AnalyticFace? OfNurbs(TFace f, Surfaces.NurbsSurface ns, out string? why)
    {
        why = null;
        var fp = new AnalyticFace(f) { Param = true, ParamS = ns };
        // габарит — опорные точки (положительные веса: поверхность в их выпуклой оболочке)
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        for (int i = 0; i < ns.CountU; i++)
            for (int j = 0; j < ns.CountV; j++)
            {
                if (!(ns.Weight(i, j) > 0)) { why = $"face {f.Id}: NURBS with a non-positive weight"; return null; }
                var c = ns.ControlPoint(i, j); lo = Vector3d.ComponentMin(lo, c); hi = Vector3d.ComponentMax(hi, c);
            }
        var m = new Vector3d(1e-6 + 1e-9 * (hi - lo).Length);
        fp.ParamLo = lo - m; fp.ParamHi = hi + m;
        // граница — прямоугольник параметров
        var uvs = new List<Vector2d>();
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
        {
            var cv = ce.Edge.Curve;
            for (int k = 0; k <= 64; k++)
            {
                var p = cv.PointAt(cv.T0 + (cv.T1 - cv.T0) * k / 64.0); var uv = ns.ProjectToUV(p);
                if ((ns.PointAt(uv) - p).Length > 1e-6) { why = $"face {f.Id}: a boundary point is off the NURBS surface"; return null; }
                uvs.Add(uv);
            }
        }
        // m.t3: ЦЕЛАЯ ЗАМКНУТАЯ ПОВЕРХНОСТЬ (грань без рёбер: «лимон», «яблоко» — края параметров — полюса и шов) — вся область
        // параметров узлового вектора. P3D_NURBSWHOLE=0 — отказ, как прежде.
        if (uvs.Count == 0 && !WholeOff && f.Loops.All(l => l.CoEdges.Count == 0))
        {
            fp.PUa = ns.KnotsU[ns.DegreeU]; fp.PUb = ns.KnotsU[ns.KnotsU.Count - ns.DegreeU - 1]; fp.PVa = ns.KnotsV[ns.DegreeV]; fp.PVb = ns.KnotsV[ns.KnotsV.Count - ns.DegreeV - 1];
            fp.FullSurface = true;
        }
        else
        {
        if (uvs.Count == 0) { why = $"face {f.Id}: NURBS face without a boundary"; return null; }
        fp.PUa = uvs.Min(x => x.X); fp.PUb = uvs.Max(x => x.X); fp.PVa = uvs.Min(x => x.Y); fp.PVb = uvs.Max(x => x.Y);
        if (!(fp.PUb - fp.PUa > 1e-6 && fp.PVb - fp.PVa > 1e-6)) { why = $"face {f.Id}: NURBS face of zero parameter area"; return null; }
        const double eb = 1e-7;
        foreach (var uv in uvs)
            if (Math.Abs(uv.X - fp.PUa) > eb && Math.Abs(uv.X - fp.PUb) > eb && Math.Abs(uv.Y - fp.PVa) > eb && Math.Abs(uv.Y - fp.PVb) > eb)
            {
                // KRN-07 N1c: ОБРЕЗАННАЯ ГРАНЬ — граница общими контурами (ломаные в параметрах и в пространстве)
                if (TrimmedOff) { why = $"face {f.Id}: trimmed NURBS face (the boundary is not a parameter rectangle)"; return null; }
                if (!fp.InitTrim(out why)) return null;
                break;
            }
        }
        // скорости — по сетке 17×17 с запасом в полтора раза (оценка «мм на единицу параметра» для допуска у края)
        double su = 0, sv = 0, h = 1e-6;
        for (int i = 0; i <= 16; i++)
            for (int j = 0; j <= 16; j++)
            {
                double u = fp.PUa + (fp.PUb - fp.PUa) * i / 16.0, v = fp.PVa + (fp.PVb - fp.PVa) * j / 16.0;
                su = Math.Max(su, (ns.PointAt(new Vector2d(Math.Min(1, u + h), v)) - ns.PointAt(new Vector2d(Math.Max(0, u - h), v))).Length / (Math.Min(1, u + h) - Math.Max(0, u - h)));
                sv = Math.Max(sv, (ns.PointAt(new Vector2d(u, Math.Min(1, v + h))) - ns.PointAt(new Vector2d(u, Math.Max(0, v - h)))).Length / (Math.Min(1, v + h) - Math.Max(0, v - h)));
            }
        fp.PSpeedU = 1.5 * su + 1e-9; fp.PSpeedV = 1.5 * sv + 1e-9;
        // треугольники — затравки луча
        var tris = new List<(Vector3d, Vector3d, Vector3d)>();
        if (f.Surface is Surfaces.FacetedSurface fs && fs.Facets.Count > 0) tris.AddRange(fs.Facets.ReadView);
        else
        {
            var mb = new Tess.MeshBuilder(); ns.TessellateFace(f, mb); var md = new Tess.MeshData(); mb.FillInto(md);
            for (int i = 0; i + 2 < md.Indices.Length; i += 3) tris.Add((mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]));
        }
        if (tris.Count == 0) { why = $"face {f.Id}: NURBS face without triangles"; return null; }
        fp.ParamTris = tris;
        return fp;
    }

    // ---------------------------------------------------------------- обрезанная грань (N1c)
    internal static readonly bool TrimmedOff = Environment.GetEnvironmentVariable("P3D_NURBSTRIM") == "0";
    internal bool PTrim;
    internal List<List<Vector2d>>? PLoopsUV;           // контуры в параметрах (замкнутые ломаные)
    internal List<List<Vector3d>>? PLoops3;            // те же точки в пространстве (лежат на рёбрах точно)
    internal double PSag3, PSagUV;                     // наибольший прогиб ломаных от кривых: мм и параметры

    bool InitTrim(out string? why)
    {
        why = null; var S = ParamS!; PTrim = true; PLoopsUV = new(); PLoops3 = new();
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in Face.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        foreach (var l in Face.Loops)
        {
            var ord = l.Ordered() ?? l.CoEdges.ToList();
            var luv = new List<Vector2d>(); var l3 = new List<Vector3d>();
            Vector2d hint = S.ProjectToUV(ord.Count > 0 ? ord[0].Start.P : Vector3d.Zero);
            foreach (var ce in ord)
            {
                if (uses[ce.Edge] != 1) continue;
                var c = ce.Edge.Curve; int n = c is LineCurve3 ? 64 : 256;
                bool fw = (c.PointAt(c.T0) - ce.Start.P).Length <= (c.PointAt(c.T1) - ce.Start.P).Length;
                double Tq(int k) { double a = (double)k / n; return fw ? c.T0 + (c.T1 - c.T0) * a : c.T1 - (c.T1 - c.T0) * a; }
                for (int k = 0; k <= n; k++)
                {
                    if (k == 0 && luv.Count > 0) continue;
                    var p = c.PointAt(Tq(k)); var uv = S.ProjectToUV(p, hint); hint = uv;
                    if ((S.PointAt(uv) - p).Length > 1e-6) { why = $"face {Face.Id}: a boundary point is off the NURBS surface"; return false; }
                    luv.Add(uv); l3.Add(p);
                    if (k > 0)
                    {
                        // прогиб: середина шага на кривой против середины хорды (в пространстве и в параметрах)
                        var pm = c.PointAt((Tq(k - 1) + Tq(k)) / 2); var uvm = S.ProjectToUV(pm, uv);
                        PSag3 = Math.Max(PSag3, DistSeg3(pm, l3[^2], l3[^1]));
                        PSagUV = Math.Max(PSagUV, DistSeg2(uvm, luv[^2], luv[^1]));
                    }
                }
            }
            if (luv.Count >= 3) { PLoopsUV.Add(luv); PLoops3.Add(l3); }
        }
        if (PLoopsUV.Count == 0) { why = $"face {Face.Id}: trimmed NURBS face without loops"; return false; }
        return true;
    }

    static double DistSeg3(Vector3d p, Vector3d a, Vector3d b) { var e = b - a; double l2 = e.LengthSquared; double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(p - a, e) / l2, 0, 1) : 0; return (a + e * t - p).Length; }
    static double DistSeg2(Vector2d p, Vector2d a, Vector2d b) { var e = b - a; double l2 = e.LengthSquared; double t = l2 > 0 ? Math.Clamp(Vector2d.Dot(p - a, e) / l2, 0, 1) : 0; return (a + e * t - p).Length; }

    /// <summary>Обрезанная грань: расстояние (мм) до границы и принадлежность (чётность в параметрах); сомнительно — у границы.</summary>
    double TrimSigned(Vector3d q)
    {
        double d3 = double.MaxValue;
        foreach (var l in PLoops3!) for (int i = 0; i + 1 < l.Count; i++) d3 = Math.Min(d3, DistSeg3(q, l[i], l[i + 1]));
        double dTrue = Math.Max(0, d3 - PSag3);                         // до самих кривых — не меньше
        var uv = ParamS!.ProjectToUV(q);
        double duv = double.MaxValue;
        foreach (var l in PLoopsUV!) for (int i = 0; i + 1 < l.Count; i++) duv = Math.Min(duv, DistSeg2(uv, l[i], l[i + 1]));
        if (d3 < Tol + 2 * PSag3 || duv < 2 * PSagUV + 1e-12) return 0;   // у границы: решение по ломаным ненадёжно
        // чётность луча в параметрах под общим углом
        var dir = new Vector2d(Math.Cos(0.613), Math.Sin(0.613)); int cr = 0;
        foreach (var l in PLoopsUV!)
            for (int i = 0; i + 1 < l.Count; i++)
            {
                var a = l[i]; var e = l[i + 1] - a; double den = dir.X * e.Y - dir.Y * e.X; if (Math.Abs(den) < 1e-300) continue;
                var w = a - uv; double t = (w.X * e.Y - w.Y * e.X) / den, u = (w.X * dir.Y - w.Y * dir.X) / den;
                if (t > 0 && u >= 0 && u < 1) cr++;
            }
        return (cr & 1) == 1 ? dTrue + 1e-300 : -(dTrue + 1e-300);
    }

    /// <summary>Знаковое расстояние (мм, оценка сверху по скоростям) от точки поверхности до края прямоугольника; плюс — внутри.</summary>
    double ParamSignedBoundary(Vector3d q)
    {
        if (PTrim) return TrimSigned(q);
        var uv = ParamS!.ProjectToUV(q);
        double du = Math.Min(uv.X - PUa, PUb - uv.X) * PSpeedU, dv = Math.Min(uv.Y - PVa, PVb - uv.Y) * PSpeedV;
        return Math.Min(du, dv);
    }

    /// <summary>Пересечения луча с поверхностью грани: затравки — треугольники, уточнение Ньютоном по (t, u, v).</summary>
    internal List<(double T, Vector3d Q, bool Grazing)> ParamRayHits(Vector3d p, Vector3d dir)
    {
        var res = new List<(double T, Vector3d Q, bool Grazing)>();
        {
            double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
            for (int k = 0; k < 3; k++)
            {
                double o = k == 0 ? p.X : k == 1 ? p.Y : p.Z, dd = k == 0 ? dir.X : k == 1 ? dir.Y : dir.Z;
                double lo = k == 0 ? ParamLo.X : k == 1 ? ParamLo.Y : ParamLo.Z, hi = k == 0 ? ParamHi.X : k == 1 ? ParamHi.Y : ParamHi.Z;
                if (Math.Abs(dd) < 1e-300) { if (o < lo || o > hi) return res; continue; }
                double a = (lo - o) / dd, b = (hi - o) / dd; if (a > b) (a, b) = (b, a);
                t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
            }
            if (t0 > t1) return res;
        }
        var S = ParamS!; const double h = 1e-7;
        // k186: ПОЛНЫЙ ПОИСК ЗАТРАВОК — куски Безье грани, деление пополам с исключением по выпуклой оболочке относительно двух
        // плоскостей луча; Ньютон — из центра каждого уцелевшего малого куска. Прежние затравки (треугольники сетки с запасом
        // в четверть) теряли пересечения: резец резьбы DLOT — луч у угла соседних витков (зазор 0.002 P) прошёл боковую грань
        // вдали от её края, затравки не нашлось, чётность 3 вместо 4, проверка CSG отвергла верный точный результат.
        // Малый кусок, который оболочка не исключает, а Ньютон не сошёлся, — касание в пределах куска: сомнение (другой луч).
        // Мутация P3D_RAYBEZ=0 — прежние треугольники.
        if (!RayBezOff && (ParamBez ??= BuildParamBez()) is { Count: > 0 } pieces)
        {
            var n1 = Vector3d.Cross(dir, Math.Abs(dir.X) < 0.6 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
            var n2 = Vector3d.Cross(dir, n1).Normalized();
            double c1 = Vector3d.Dot(n1, p), c2 = Vector3d.Dot(n2, p);
            double leaf = Math.Max(1e-3, 1e-4 * (ParamHi - ParamLo).Length), eps = 1e-12 * Math.Max(1, (ParamHi - ParamLo).Length);
            bool doubt = false;
            var stack = new Stack<(Surfaces.BezierPatch P, int D)>();
            foreach (var bp in pieces) stack.Push((bp, 0));
            while (stack.Count > 0)
            {
                var (bp, depth) = stack.Pop();
                bool pos1 = true, neg1 = true, pos2 = true, neg2 = true;
                foreach (var q4 in bp.Pw)
                {
                    double f1 = n1.X * q4.X + n1.Y * q4.Y + n1.Z * q4.Z - c1 * q4.W, f2 = n2.X * q4.X + n2.Y * q4.Y + n2.Z * q4.Z - c2 * q4.W;
                    double e4 = eps * q4.W;
                    if (f1 <= e4) pos1 = false; if (f1 >= -e4) neg1 = false; if (f2 <= e4) pos2 = false; if (f2 >= -e4) neg2 = false;
                }
                if (pos1 || neg1 || pos2 || neg2) continue;                    // оболочка куска по одну сторону плоскости луча
                var (blo, bhi) = bp.Box();
                if ((bhi - blo).Length > leaf && depth < 60) { var (A, B) = bp.Split(SplitAlongU(bp)); stack.Push((A, depth + 1)); stack.Push((B, depth + 1)); continue; }
                double u = (bp.U0 + bp.U1) / 2, v = (bp.V0 + bp.V1) / 2;
                double tt = Vector3d.Dot(S.PointAt(new Vector2d(u, v)) - p, dir);
                if (!RayNewton(S, p, dir, ref tt, ref u, ref v, h, out double gap)) { if (gap < leaf) doubt = true; continue; }
                if (res.Any(x => Math.Abs(x.T - tt) < 1e-8)) continue;
                var qh = p + dir * tt; var nh = S.NormalAt(new Vector2d(u, v));
                res.Add((tt, qh, Math.Abs(Vector3d.Dot(nh, dir)) < 1e-4));
            }
            if (doubt) res.Add((double.MaxValue, p, true));                    // сомнение: классификатор возьмёт другой луч
            return res;
        }
        foreach (var (a, b, c) in ParamTris!)
        {
            var e1 = b - a; var e2 = c - a; var pv = Vector3d.Cross(dir, e2); double det = Vector3d.Dot(e1, pv);
            if (Math.Abs(det) < 1e-18) continue;
            var sv0 = p - a; double bu = Vector3d.Dot(sv0, pv) / det; if (bu < -0.25 || bu > 1.25) continue;
            var qv = Vector3d.Cross(sv0, e1); double bv = Vector3d.Dot(dir, qv) / det; if (bv < -0.25 || bu + bv > 1.25) continue;
            double tt = Vector3d.Dot(e2, qv) / det;
            var uv = S.ProjectToUV(p + dir * tt); double u = uv.X, v = uv.Y; bool conv = false;
            for (int it = 0; it < 40; it++)
            {
                var X = S.PointAt(new Vector2d(u, v)); var G = p + dir * tt - X;
                if (G.Length < 1e-12) { conv = true; break; }
                double ua = Math.Max(0, u - h), ub = Math.Min(1, u + h), va = Math.Max(0, v - h), vb = Math.Min(1, v + h);
                var Su = (S.PointAt(new Vector2d(ub, v)) - S.PointAt(new Vector2d(ua, v))) / (ub - ua);
                var Sv = (S.PointAt(new Vector2d(u, vb)) - S.PointAt(new Vector2d(u, va))) / (vb - va);
                var dx = Cols3(dir, -Su, -Sv, -G);
                if (dx == null) break;
                tt += dx.Value.X; u = Math.Clamp(u + dx.Value.Y, 0, 1); v = Math.Clamp(v + dx.Value.Z, 0, 1);
                if (dx.Value.Length < 1e-15) { conv = (p + dir * tt - S.PointAt(new Vector2d(u, v))).Length < 1e-9; break; }
            }
            if (!conv) continue;
            if (res.Any(x => Math.Abs(x.T - tt) < 1e-8)) continue;
            var q = p + dir * tt; var n = S.NormalAt(new Vector2d(u, v));
            res.Add((tt, q, Math.Abs(Vector3d.Dot(n, dir)) < 1e-4));
        }
        return res;
    }

    internal List<Surfaces.BezierPatch>? ParamBez;
    static readonly bool RayBezOff = Environment.GetEnvironmentVariable("P3D_RAYBEZ") == "0";
    /// <summary>k186: куски Безье поверхности, обрезанные прямоугольником параметров грани (с запасом в 1e-9).</summary>
    List<Surfaces.BezierPatch> BuildParamBez()
    {
        var list = new List<Surfaces.BezierPatch>();
        double ua = PUa - 1e-9, ub = PUb + 1e-9, va = PVa - 1e-9, vb = PVb + 1e-9;
        foreach (var bp in Surfaces.NurbsTools.Beziers(ParamS!))
        {
            double u0 = Math.Max(bp.U0, ua), u1 = Math.Min(bp.U1, ub), v0 = Math.Max(bp.V0, va), v1 = Math.Min(bp.V1, vb);
            if (u0 >= u1 || v0 >= v1) continue;
            list.Add(u0 == bp.U0 && u1 == bp.U1 && v0 == bp.V0 && v1 == bp.V1 ? bp : bp.Sub(u0, u1, v0, v1));
        }
        return list;
    }
    /// <summary>Делить по длинной стороне куска в пространстве.</summary>
    static bool SplitAlongU(Surfaces.BezierPatch bp)
    {
        int nu = bp.Pw.GetLength(0) - 1, nv = bp.Pw.GetLength(1) - 1;
        Vector3d P(int i, int j) { var q = bp.Pw[i, j]; return new Vector3d(q.X / q.W, q.Y / q.W, q.Z / q.W); }
        double lu = (P(nu, 0) - P(0, 0)).Length + (P(nu, nv) - P(0, nv)).Length, lv = (P(0, nv) - P(0, 0)).Length + (P(nu, nv) - P(nu, 0)).Length;
        return lu >= lv;
    }
    /// <summary>Ньютон по (t, u, v) для p + t·dir = S(u, v); gap — остаток при отказе.</summary>
    static bool RayNewton(Surfaces.NurbsSurface S, Vector3d p, Vector3d dir, ref double tt, ref double u, ref double v, double h, out double gap)
    {
        gap = double.MaxValue;
        for (int it = 0; it < 40; it++)
        {
            var X = S.PointAt(new Vector2d(u, v)); var G = p + dir * tt - X; gap = G.Length;
            if (gap < 1e-12) return true;
            double ua = Math.Max(0, u - h), ub = Math.Min(1, u + h), va = Math.Max(0, v - h), vb = Math.Min(1, v + h);
            var Su = (S.PointAt(new Vector2d(ub, v)) - S.PointAt(new Vector2d(ua, v))) / (ub - ua);
            var Sv = (S.PointAt(new Vector2d(u, vb)) - S.PointAt(new Vector2d(u, va))) / (vb - va);
            var dx = Cols3(dir, -Su, -Sv, -G);
            if (dx == null) return false;
            tt += dx.Value.X; u = Math.Clamp(u + dx.Value.Y, 0, 1); v = Math.Clamp(v + dx.Value.Z, 0, 1);
            if (dx.Value.Length < 1e-15) { gap = (p + dir * tt - S.PointAt(new Vector2d(u, v))).Length; return gap < 1e-9; }
        }
        return false;
    }
}
