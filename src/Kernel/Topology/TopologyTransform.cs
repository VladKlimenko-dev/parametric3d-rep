using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// ТОЧНОЕ ЖЁСТКОЕ ПРЕОБРАЗОВАНИЕ ТЕЛА (сдвиг, поворот, зеркало) — копия графа B-rep с преобразованными вершинами, кривыми
/// и поверхностями. Прежде копия под преобразованием (зеркало, массивы, повёрнутые заготовки) ПЕРЕСОБИРАЛАСЬ из
/// треугольников гранения: тело становилось многогранным по устройству, дуги — подогнанными (объём повёрнутого
/// цилиндра — 7.5e-8 от точного), круговой массив под PreserveBRep — отказ.
///
/// Параметризация поверхностей и кривых при сдвиге и повороте сохраняется — p-кривые остаются верными. Зеркало меняет
/// обход: рамки получают v → −v (нормаль поверхности переходит в отражённую наружную), а контуры обращаются (материал
/// слева сохраняется); p-кривые сбрасываются. null — в теле есть то, что точно пока не переносится (кривая пересечения,
/// сплайновая поверхность, авторитетная многогранная форма): вызывающий берёт прежний способ.
/// </summary>
public static class TopologyTransform
{
    /// <summary>Мутация P3D_XFORMPROV=0: перенесённая копия без карты происхождения.</summary>
    static readonly bool XformProvOff = Environment.GetEnvironmentVariable("P3D_XFORMPROV") == "0";
    static readonly bool XformHintOff = Environment.GetEnvironmentVariable("P3D_XFORMHINT") == "0";
    static readonly bool XformMeshOff = Environment.GetEnvironmentVariable("P3D_XFORMMESH") == "0";
    /// <summary>Мутация P3D_XFORMPROC=0: кривая и поверхность по формуле (лента катящегося шара, 06.12) и кривая пересечения
    /// не переносятся точно — копия пересобирается из треугольников (прежде: «Unspecified», десятки секунд).</summary>
    static readonly bool XformProcOff = Environment.GetEnvironmentVariable("P3D_XFORMPROC") == "0";

    public static TSolid? Apply(TSolid src, TransformOp.Xform x)
    {
        if (src.Exact != null && src.ExactAuthoritative) return null;     // рациональная многогранная форма не поворачивается точно
        var curves = new Dictionary<ICurve3, ICurve3>(ReferenceEqualityComparer.Instance);
        var surfs = new Dictionary<ISurface3, ISurface3>(ReferenceEqualityComparer.Instance);
        ICurve3? C(ICurve3 c)
        {
            if (curves.TryGetValue(c, out var m)) return m;
            ICurve3? r = c switch
            {
                LineCurve3 l => new LineCurve3(x.Point(l.A), x.Point(l.B)),
                ArcCurve3 a => new ArcCurve3(Frame(a.Frame, x), a.Radius, x.Flips ? -a.A0 : a.A0, x.Flips ? -a.A1 : a.A1),
                EllipseCurve3 e => new EllipseCurve3(Frame(e.Frame, x), e.RU, e.RV, x.Flips ? -e.A0 : e.A0, x.Flips ? -e.A1 : e.A1),
                SplineCurve3 sp => new SplineCurve3(sp.ControlPoints.Select(x.Point).ToList(), sp.Degree, sp.Weights, sp.Knots),
                // KRN-06 (06.12): формула переносится отображением точки; кривая пересечения — те же поверхности, перенесённые
                ProcCurve3 pc when !XformProcOff => pc.Mapped(x.Point),
                IntersectionCurve3 ic when !XformProcOff && S(ic.A) is { } ia && S(ic.B) is { } ib => new IntersectionCurve3(ia, ib, ic.Seed.Select(x.Point).ToList()),
                _ => null
            };
            if (r != null) curves[c] = r;
            return r;
        }
        ISurface3? S(ISurface3? s)
        {
            if (s == null) return null;
            if (surfs.TryGetValue(s, out var m)) return m;
            ISurface3? r = s switch
            {
                PlaneSurface pl => new PlaneSurface(Frame(pl.F, x)),
                ExtrudedSurface ex when C(ex.Base) is { } b => new ExtrudedSurface(b, x.Direction(ex.Dir)),
                RevolvedSurface rv when C(rv.Base) is { } b => new RevolvedSurface(b, Frame(rv.Axis, x), rv.A0, rv.A1),
                CylinderSurface cs => new CylinderSurface(Frame(cs.Axis, x), cs.Radius),
                SectionSurface ss when !XformProcOff => ss.Mapped(q => q.IsArc
                    ? SweepSection.Arc(x.Point(q.C), x.Direction(q.E1).Normalized(), x.Direction(q.E2).Normalized(), q.R, q.Phi)
                    : SweepSection.Line(x.Point(q.A), x.Point(q.B))),
                _ => null
            };
            if (r != null) surfs[s] = r;
            return r;
        }

        var dst = TopologyClone.Deep(src);
        // всё ли переносится
        foreach (var e in dst.Edges) if (C(e.Curve) == null) return null;
        foreach (var f in dst.Faces)
        {
            if (f.Surface is FacetedSurface fs) { if (fs.Source != null && S(fs.Source) == null) return null; }
            else if (S(f.Surface) == null) return null;
        }
        // наружная нормаль до переноса — в точке каждой грани (порядок граней у копии тот же)
        var srcFaces = src.Faces.ToList(); var want = new List<(Vector3d P, Vector3d N)?>();
        foreach (var f in srcFaces)
        {
            var ce0 = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault();
            Vector3d p;
            if (ce0 != null) p = ce0.Edge.Curve.PointAt((ce0.Edge.Curve.T0 + ce0.Edge.Curve.T1) / 2);
            else { var sf = GeometryAudit.AuthoritativeSurface(f); if (sf == null) { want.Add(null); continue; } p = sf.PointAt(new Vector2d(0.37, 0.41)); }
            Vector3d n; try { n = f.NormalAt(p); } catch { want.Add(null); continue; }
            want.Add((x.Point(p), x.Direction(n)));
        }
        dst.BeginEdit("TopologyTransform", EditKind.Derive);
        foreach (var v in dst.Vertices) v.P = x.Point(v.P);
        foreach (var e in dst.Edges) { e.Curve = C(e.Curve)!; e.PolylineCache = null; }
        foreach (var f in dst.Faces)
        {
            if (f.Surface is FacetedSurface fs)
            {
                var nf = new FacetedSurface(S(fs.Source), x.Direction(fs.FallbackNormal));
                foreach (var (a, b, c) in fs.Facets.ReadView) nf.Facets.Add(x.Flips ? (x.Point(a), x.Point(c), x.Point(b)) : (x.Point(a), x.Point(b), x.Point(c)));
                // видимая сетка (вершины швов на точных рёбрах) — переносится тоже: без неё показывались бы Facets, а они
                // у соседних граней не согласованы (отверстие: 8 сторон без пары)
                var ds = fs.DisplaySnapshot;
                if (ds != null)
                {
                    var tris = ds.Tris.Select(t => x.Flips ? (x.Point(t.A), x.Point(t.C), x.Point(t.B)) : (x.Point(t.A), x.Point(t.B), x.Point(t.C))).ToList();
                    var nrm = ds.Normals?.Select(t => x.Flips ? (x.Direction(t.A), x.Direction(t.C), x.Direction(t.B)) : (x.Direction(t.A), x.Direction(t.B), x.Direction(t.C))).ToList();
                    nf.SetDisplay(tris, nrm);
                }
                f.Surface = nf;
            }
            else f.Surface = S(f.Surface)!;
            // g.05: точка грани переносится с гранью. Прежде оставалась на старом месте — сторона сферической грани и объём грани
            // тора по ней у перенесённого тела ошибались (куб с полостью-шаром, перенесённый на 1285 мм: объём 219 665 вместо
            // 211 811, G11-DISPLAY-CLOSED-IN-DECLARED-REGION). Мутация P3D_XFORMHINT=0.
            if (!XformHintOff && f.InteriorHint is { } ih) f.InteriorHint = x.Point(ih);
            if (x.Flips)
                foreach (var l in f.Loops)
                {
                    var items = l.CoEdges.Select(ce => (ce.Edge, ce.Reversed)).ToList();
                    foreach (var ce in l.CoEdges.ToList()) { ce.Edge.CoEdges.Remove(ce); l.CoEdges.Remove(ce); }
                    for (int i = items.Count - 1; i >= 0; i--) l.Add(items[i].Edge, !items[i].Reversed);
                }
            if (x.Flips) foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { ce.PCurve = null; ce.PCurveDeviation = 0; }
        }
        // сторона грани: нормаль копии — как перенесённая наружная нормаль оригинала
        var dstFaces = dst.Faces.ToList();
        for (int i = 0; i < dstFaces.Count && i < want.Count; i++)
            if (want[i] is { } w)
            {
                Vector3d n2; try { n2 = dstFaces[i].NormalAt(w.P); } catch { continue; }
                if (Vector3d.Dot(n2, w.N) < 0) dstFaces[i].Reversed = !dstFaces[i].Reversed;
            }
        dst.DisplayMeshKey = null;
        // g.11: сетка — от контуров с общими точками рёбер (как у результатов точной булевой). Без этого повёрнутое тело
        // размечалось по граням независимо (общая разметка квадрик — только для тел по осям), и соседние грани делили
        // общее ребро по-разному: трещины отображения (брусок — 6 сторон без пары). Мутация P3D_XFORMMESH=0.
        if (!XformMeshOff && dst.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(dst);
        // тело, которое размечает общая разметка квадрик (QMesher: плиты с отверстиями), не трогается — она согласована сама
        if (!XformMeshOff && dst.TopologyConvention >= TopologyConventions.Version && dst.Faces.All(f => f.Surface is not FacetedSurface) && !Tess.QMesher.Fits(dst))
        {
            using var _fs = Ops.BooleanOp.FacetingScope();     // гранение CAD, не хорда отображения (g.04)
            Export.StepImport.MeshFacesFromLoops(dst, null, "Transform");
        }
        // KRN-01 (01.5): копия — каждая грань из своей грани исходного тела (порядок тот же), форма перенесена (изменена)
        if (!XformProvOff)
        {
            var pv = new Provenance { Operation = "transform" };
            var df = dst.Faces.ToList();
            for (int i = 0; i < df.Count && i < srcFaces.Count; i++) pv.Faces.Add(new FaceProvenance(df[i], srcFaces[i], FaceOrigin.Modified, "transformed copy"));
            dst.SetProvenance(pv);
        }
        return dst;
    }

    static Frame3 Frame(Frame3 f, TransformOp.Xform x)
    {
        Vector3d u = x.Direction(f.U).Normalized();
        Vector3d v = x.Direction(f.V).Normalized();
        if (x.Flips) v = -v;
        return new Frame3(x.Point(f.Origin), u, v);
    }
}
