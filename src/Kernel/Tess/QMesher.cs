using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// СЕТКА ТЕЛА С ОБЩЕЙ РАЗМЕТКОЙ РЁБЕР — для тел, построенных по квадрике.
///
/// Обычное гранение размечает каждую грань отдельно, и у тела по квадрике сетка не сходится:
/// измерено — 302 треугольника и 316 сторон без пары из 611, потому что кромку отверстия крышка
/// размечает 78 точками, а стенка 144. Из-за этого такое тело не проходит в булеву, и вычитание
/// цилиндра нельзя было пустить общим путём.
///
/// Здесь каждое ребро размечается ОДИН раз, и обе смежные грани берут одни и те же точки — тот
/// же приём, что давно работает в обычной булевой.
/// </summary>
public static class QMesher
{
    /// <summary>
    /// Включено по умолчанию. Первая попытка была неверной: она угадывала порядок точек полосы
    /// (первая половина — низ, вторая — верх наоборот), и сетка выходила вывернутой — объём
    /// глухого отверстия −181 мм³ вместо 251.3. Теперь порядок восстанавливается по делу:
    /// каждая точка относится к тому краю, к которому ближе вдоль оси, и внутри края
    /// упорядочивается по углу.
    ///
    /// Отключается переключателем P3D_QMESH=0, если понадобится сравнить с прежним гранением.
    /// </summary>
    private static bool _gEnabled = Environment.GetEnvironmentVariable("P3D_QMESH") != "0";
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool Enabled
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.QMeshEnabled : _gEnabled;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gEnabled, value, "QMesher.Enabled");
    }

    /// <summary>Отладка: какая грань не взялась.</summary>
    public static Action<string>? Debug;

    /// <summary>
    /// KRN-07 (07.2): грань квадрики — внутренняя метка «quadric:…» или роль отверстия «hole:…» с кромками только из
    /// окружностей (дуг) в плоскостях одного направления — так, как строит соосный путь. Имена граней отверстия едины для
    /// всех маршрутов, а эта разметка задумана только для соосных отверстий: наклонное (эллипсы), у края (отрезки) и
    /// поперёк вала (кривые пересечения) сюда не идут, как и прежде.
    /// </summary>
    static bool QuadricFace(TFace f)
    {
        if (f.Tag == null) return false;
        string b = Ops.TransformOp.WithoutInstance(Topology.EdgeRef.Bare(f.Tag));
        if (b.StartsWith("quadric:")) return true;
        if (!b.StartsWith("hole:") || b == "hole:face") return false;
        Vector3d? n = null;
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
        {
            if (ce.Edge.Curve is not ArcCurve3 a) return false;
            var w = a.Frame.W.Normalized();
            if (n == null) n = w;
            else if (Math.Abs(Math.Abs(Vector3d.Dot(n.Value, w)) - 1) > 1e-9) return false;
        }
        return n != null;
    }

    /// <summary>Нужна ли телу эта дорога: построено ли оно по квадрике.</summary>
    public static bool Fits(TSolid solid) =>
        // Подпись сравнивается без ключа операции «#ключ/» (K05): иначе после именования граней
        // деталью ни одно тело больше не считалось квадрикой, общая разметка рёбер не включалась, и
        // тело с бобышкой при прогибе 0.002 мм приходило в булеву дырявым (найдено K08).
        solid.Faces.Any(QuadricFace) &&
        // Только тело из плоскостей и стенок квадрик (плита с отверстиями и бобышками) — для
        // него общая разметка и задумана. Тело со скруглениями и фасками она размечала с
        // вывернутыми гранями (343 стороны с одинаковым обходом на реалистичной детали K08).
        solid.Faces.All(f =>
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            return src is PlaneSurface || (src is ExtrudedSurface ex && ex.Base is LineCurve3)
                || QuadricFace(f);
        });

    /// <summary>
    /// Разметить тело ПО ГРАНЯМ, отдав каждой свою сетку. Нужно булевой: она собирает
    /// треугольники с привязкой к грани, а не одной кучей.
    /// </summary>
    public static bool TessellatePerFace(TSolid solid, Dictionary<TFace, MeshBuilder> result)
    {
        var edgePoints = new Dictionary<TEdge, List<Vector3d>>();
        foreach (var e in solid.Edges)
        {
            if (e.Curve == null) return false;
            var pts = new List<Vector3d>();
            e.Curve.Tessellate(pts, true);
            if (pts.Count < 2) return false;
            edgePoints[e] = pts;
        }

        foreach (var f in solid.Faces)
        {
            var loops = LoopsOf(f, edgePoints);
            if (loops == null) return false;

            var mb = new MeshBuilder();
            mb.BeginFace(f.Id);
            if (!FaceInto(f, loops, mb)) return false;
            result[f] = mb;
        }

        return true;
    }

    /// <summary>Контуры грани точками общих рёбер.</summary>
    private static List<List<Vector3d>>? LoopsOf(TFace f, Dictionary<TEdge, List<Vector3d>> edgePoints)
    {
        var loops = new List<List<Vector3d>>();

        foreach (var loop in f.Loops)
        {
            var pts = new List<Vector3d>();
            foreach (var ce in loop.CoEdges)
            {
                if (!edgePoints.TryGetValue(ce.Edge, out var ep)) return null;
                for (int i = 0; i < ep.Count - 1; i++)
                    pts.Add(ce.Reversed ? ep[ep.Count - 1 - i] : ep[i]);
            }
            if (pts.Count < 3) return null;
            loops.Add(pts);
        }

        return loops;
    }

    /// <summary>
    /// Разметить тело. Возвращает false, если какая-то грань не берётся, — тогда работает
    /// обычное гранение.
    /// </summary>
    public static bool Tessellate(TSolid solid, MeshBuilder mb)
    {
        // Точки рёбер — по одному разу на ребро.
        var edgePoints = new Dictionary<TEdge, List<Vector3d>>();

        foreach (var e in solid.Edges)
        {
            if (e.Curve == null) return false;
            var pts = new List<Vector3d>();
            e.Curve.Tessellate(pts, true);
            if (pts.Count < 2) return false;
            edgePoints[e] = pts;
        }

        foreach (var f in solid.Faces)
        {
            // НОМЕР ГРАНИ ОБЯЗАТЕЛЕН. Без него треугольники не принадлежат ничему: грань
            // перестаёт подсвечиваться под курсором и выбираться — именно это и случилось,
            // когда разметка стала складывать все грани в одну сетку.
            mb.BeginFace(f.Id);

            var loops = new List<List<Vector3d>>();

            foreach (var loop in f.Loops)
            {
                var pts = new List<Vector3d>();
                foreach (var ce in loop.CoEdges)
                {
                    if (!edgePoints.TryGetValue(ce.Edge, out var ep)) return false;

                    // Точки берутся ОБЩИЕ; направление — по обходу контура.
                    for (int i = 0; i < ep.Count - 1; i++)
                        pts.Add(ce.Reversed ? ep[ep.Count - 1 - i] : ep[i]);
                }

                if (pts.Count < 3) return false;
                loops.Add(pts);
            }

            if (!FaceInto(f, loops, mb))
            {
                Debug?.Invoke($"грань [{f.Tag}] не взялась: {(f.Surface as object)?.GetType().Name}, контуров {loops.Count}");
                return false;
            }
        }

        return true;
    }

    /// <summary>Сетка одной грани по готовым точкам её контуров.</summary>
    private static bool FaceInto(TFace f, List<List<Vector3d>> loops, MeshBuilder mb)
    {
        var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

        // Плоская грань. Боковая грань бруска записана выдавливанием ОТРЕЗКА — она тоже плоская,
        // и без этого разбор её не брал, а всё тело уходило на обычное гранение.
        Frame3? planeFrame = null;
        if (src is PlaneSurface pls) planeFrame = pls.F;
        else if (src is ExtrudedSurface exl && exl.Base is LineCurve3 ln)
        {
            Vector3d u = (ln.B - ln.A).Normalized();
            Vector3d nrm = Vector3d.Cross(u, exl.Dir.Normalized());
            if (nrm.LengthSquared > 1e-18) planeFrame = Frame3.FromNormal(ln.A, nrm.Normalized());
        }

        if (planeFrame is { } pf)
        {
            var frame = pf;
            Vector2d To2(Vector3d p) => new(Vector3d.Dot(p - frame.Origin, frame.U),
                                            Vector3d.Dot(p - frame.Origin, frame.V));

            var outer = loops[0].Select(To2).ToList();
            var holes = loops.Skip(1).Select(l => (IReadOnlyList<Vector2d>)l.Select(To2).ToList()).ToList();

            var tris = Cdt.Triangulate(outer, holes, out _);
            if (tris == null) return false;

            var all = new List<Vector3d>(loops[0]);
            foreach (var h in loops.Skip(1)) all.AddRange(h);

            Emit(f, all, tris, frame.W, mb);
            return true;
        }

        // Стенка отверстия: полоса между двумя кромками.
        //
        // ПОРЯДОК ТОЧЕК НЕ УГАДЫВАЕТСЯ, А ВОССТАНАВЛИВАЕТСЯ. Первая попытка считала, что первая
        // половина контура — нижняя кромка, вторая — верхняя в обратном порядке; сетка выходила
        // вывернутой (объём глухого отверстия −181 вместо 251.3). Теперь точки разбираются по
        // делу: каждая относится к тому краю, к которому ближе вдоль оси, и внутри края
        // упорядочивается по углу. Так сшивка не зависит от того, как контур сложился.
        if (src is ExtrudedSurface ex && ex.Base is ArcCurve3 baseArc)
        {
            // Стенка — одно кольцо со швом или (после K05, где каждое кольцо — свой контур) две
            // кромки отдельными контурами; точки всё равно разбираются по краям ниже.
            if (loops.Count < 1 || loops.Count > 2) return false;

            Vector3d axis = ex.Dir.LengthSquared < 1e-18 ? Vector3d.UnitZ : ex.Dir.Normalized();
            Vector3d centre = baseArc.Frame.Origin;

            var all = loops.Count == 1 ? loops[0] : loops.SelectMany(l => l).ToList();
            double lo = all.Min(p => Vector3d.Dot(p - centre, axis));
            double hi = all.Max(p => Vector3d.Dot(p - centre, axis));
            if (hi - lo <= Tol.Weld) return false;

            double mid = (lo + hi) / 2;
            var lowRim = new List<Vector3d>();
            var highRim = new List<Vector3d>();
            foreach (var p in all)
                (Vector3d.Dot(p - centre, axis) < mid ? lowRim : highRim).Add(p);

            if (lowRim.Count < 3 || lowRim.Count != highRim.Count) return false;

            double Angle(Vector3d p)
            {
                Vector3d d = p - centre;
                d -= axis * Vector3d.Dot(d, axis);
                return Math.Atan2(Vector3d.Dot(d, baseArc.Frame.V), Vector3d.Dot(d, baseArc.Frame.U));
            }

            lowRim.Sort((x, y) => Angle(x).CompareTo(Angle(y)));
            highRim.Sort((x, y) => Angle(x).CompareTo(Angle(y)));

            int n = lowRim.Count;
            int start = mb.VertexCount;

            foreach (var p in lowRim.Concat(highRim))
            {
                Vector3d nr = f.Surface.NormalAt(f.Surface.ProjectToUV(p));
                if (EffReversed(f)) nr = -nr;
                mb.AddVertex(p, nr);
            }

            for (int i = 0; i < n; i++)
            {
                int a0 = start + i, a1 = start + (i + 1) % n;
                int b0 = start + n + i, b1 = start + n + (i + 1) % n;

                // Обход согласуется с нормалью грани: у стенки отверстия она смотрит внутрь.
                if (EffReversed(f))
                {
                    mb.AddTriangle(a0, b0, a1);
                    mb.AddTriangle(a1, b0, b1);
                }
                else
                {
                    mb.AddTriangle(a0, a1, b0);
                    mb.AddTriangle(a1, b1, b0);
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// g.11: сторона грани для разметки. У грани, размеченной от контуров (MeshFacesFromLoops), сторону несут треугольники
    /// гранёной поверхности, а Reversed сброшен; по источнику с Reversed = false нижняя грань перенесённого бруска
    /// выходила вывернутой (8 сторон без пары). Здесь — по треугольникам относительно нормали источника. Мутация P3D_QMSIDE=0.
    /// </summary>
    static readonly bool SideOld = true;   // опыт g.11 не удался (стенка отверстия выворачивалась) — прежняя сторона
    private static bool EffReversed(TFace f)
    {
        if (SideOld || f.Surface is not FacetedSurface fs || fs.Source == null || fs.Facets.Count == 0) return f.Reversed;
        var (a, b, c) = fs.Facets.ReadView.OrderByDescending(t => Vector3d.Cross(t.B - t.A, t.C - t.A).LengthSquared).First();
        var nt = Vector3d.Cross(b - a, c - a); var g = (a + b + c) / 3;
        var ns = fs.Source.NormalAt(fs.Source.ProjectToUV(g));
        double d = Vector3d.Dot(nt, ns);
        return Math.Abs(d) < 1e-30 ? f.Reversed : d < 0;
    }

    private static void Emit(TFace f, List<Vector3d> pts, List<int> tris, Vector3d normal, MeshBuilder mb)
    {
        Vector3d n = normal.LengthSquared < 1e-24 ? Vector3d.UnitZ : normal.Normalized();
        if (EffReversed(f)) n = -n;

        int start = mb.VertexCount;
        foreach (var p in pts) mb.AddVertex(p, n);

        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector3d a = pts[tris[i]], b = pts[tris[i + 1]], c = pts[tris[i + 2]];
            Vector3d tn = Vector3d.Cross(b - a, c - a);
            if (Vector3d.Dot(tn, n) < 0) mb.AddTriangle(start + tris[i], start + tris[i + 2], start + tris[i + 1]);
            else mb.AddTriangle(start + tris[i], start + tris[i + 1], start + tris[i + 2]);
        }
    }
}
