using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>Направление взгляда вида и оси листа (ортогональная проекция).</summary>
public readonly record struct ViewFrame(Vector3d Right, Vector3d Up)
{
    /// <summary>К наблюдателю (Right × Up).</summary>
    public Vector3d Toward => Vector3d.Cross(Right, Up).Normalized();
    public Vector2d Project(Vector3d p) => new(Vector3d.Dot(p, Right), Vector3d.Dot(p, Up));
    public double Depth(Vector3d p) => Vector3d.Dot(p, Toward);

    /// <summary>
    /// Основные виды ГОСТ 2.305 и изометрия ГОСТ 2.317 — k161: как виды окна модели (Camera.OrientationQuat; ось Y вверх, вид
    /// спереди — взгляд вдоль −Z): спереди (X вправо, Y вверх), сзади, слева (взгляд вдоль +X), справа, сверху (взгляд вдоль −Y,
    /// X вправо, −Z вверх), снизу, изометрия — та же, что у кнопки «Изометрия». Прежде (k155–k160) чертёж считал вверх ось Z:
    /// вид «спереди» показывал вид модели сверху, «сверху» — вид слева, «слева» — спереди. Мутация P3D_VIEWYUP=0 — прежнее.
    /// </summary>
    public static ViewFrame Of(string name) => ZUp ? OfZUp(name) : name switch
    {
        "front" => new(Vector3d.UnitX, Vector3d.UnitY),
        "back" => new(-Vector3d.UnitX, Vector3d.UnitY),
        "left" => new(Vector3d.UnitZ, Vector3d.UnitY),
        "right" => new(-Vector3d.UnitZ, Vector3d.UnitY),
        "top" => new(Vector3d.UnitX, -Vector3d.UnitZ),
        "bottom" => new(Vector3d.UnitX, Vector3d.UnitZ),
        "iso" => IsoYUp(),
        _ when name.StartsWith("v:") => Parse(name),
        _ => throw new ArgumentException("unknown view " + name),
    };
    static readonly bool ZUp = Environment.GetEnvironmentVariable("P3D_VIEWYUP") == "0";

    /// <summary>Изометрия окна модели: поворот камеры Rx(−30°)·Ry(45°); оси листа — обратный поворот осей экрана.</summary>
    static ViewFrame IsoYUp()
    {
        var q = Quaterniond.FromAxisAngle(Vector3d.UnitX, -Math.PI / 6) * Quaterniond.FromAxisAngle(Vector3d.UnitY, Math.PI / 4);
        var inv = q.Inverted();
        return new(Vector3d.Transform(Vector3d.UnitX, inv).Normalized(), Vector3d.Transform(Vector3d.UnitY, inv).Normalized());
    }

    static ViewFrame OfZUp(string name) => name switch
    {
        "front" => new(Vector3d.UnitX, Vector3d.UnitZ),
        "top" => new(Vector3d.UnitX, Vector3d.UnitY),
        "left" => new(-Vector3d.UnitY, Vector3d.UnitZ),
        "right" => new(Vector3d.UnitY, Vector3d.UnitZ),
        "bottom" => new(Vector3d.UnitX, -Vector3d.UnitY),
        "back" => new(-Vector3d.UnitX, Vector3d.UnitZ),
        "iso" => Iso(),
        _ when name.StartsWith("v:") => Parse(name),
        _ => throw new ArgumentException("unknown view " + name),
    };

    /// <summary>k158: своя рамка вида (вспомогательный вид): «v:Rx,Ry,Rz;Ux,Uy,Uz».</summary>
    public string Encode() => FormattableString.Invariant($"v:{Right.X:R},{Right.Y:R},{Right.Z:R};{Up.X:R},{Up.Y:R},{Up.Z:R}");

    static ViewFrame Parse(string s)
    {
        var n = s[2..].Split(',', ';').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (n.Length != 6) throw new ArgumentException("bad view frame " + s);
        var r = new Vector3d(n[0], n[1], n[2]).Normalized();
        var u = new Vector3d(n[3], n[4], n[5]);
        u = (u - r * Vector3d.Dot(u, r)).Normalized();
        return new(r, u);
    }

    static ViewFrame Iso()
    {
        var toward = new Vector3d(1, -1, 1).Normalized();
        var right = Vector3d.Cross(Vector3d.UnitZ, toward).Normalized();
        var up = Vector3d.Cross(toward, right).Normalized();
        return new(right, up);
    }
}

/// <summary>Линия вида: ломаная на плоскости вида (мм модели), видимость, вид линии и ребро-источник.</summary>
/// <summary>Body — номер тела в списке проекции (для сборки: какой детали линия; выноски позиций).</summary>
public sealed record ViewLine(List<Vector2d> Points, bool Visible, ViewLineKind Kind, uint SourceId, int Body = 0);
/// <summary>Дуга ребра в плоскости вида (ось дуги — вдоль взгляда): центр, радиус, углы (рад, против часовой), полная окружность.</summary>
public sealed record ViewArc(Vector2d Center, double Radius, double A0, double A1, bool Full, int Body = 0);
public enum ViewLineKind { Edge, Silhouette, Tangent }
/// <summary>Ось тела вращения на виде: Cross — ось вдоль взгляда (перекрестие центра окружности радиуса Radius), иначе — отрезок
/// оси от Lo до Hi вдоль Dir через Center.</summary>
public sealed record ViewAxis(Vector2d Center, Vector2d Dir, double Lo, double Hi, bool Cross, double Radius);

/// <summary>
/// k155 (D2, начало): ПРОЕКЦИЯ ТЕЛА НА ВИД СО СКРЫТИЕМ ЛИНИЙ. Рёбра тела (точные кривые) и очерки кривых граней (линии, где
/// нормаль грани перпендикулярна взгляду) проецируются ортогонально; видимость точки — нет ли перед ней (ближе к наблюдателю)
/// треугольника сетки тела, накрывающего её проекцию (сетка — ускоритель: ячейки 2D). Граница видимых и невидимых участков
/// уточняется делением пополам. Рёбра гладкого стыка граней — линии перехода (Tangent; на чертеже ГОСТ — не показываются).
/// </summary>
public static class ViewProjection
{
    static readonly bool FullTurnOff = Environment.GetEnvironmentVariable("P3D_AXISFULLTURN") == "0";
    static readonly bool SharedOff = Environment.GetEnvironmentVariable("P3D_HLRSHARED") == "0";
    static readonly bool DedupOff = Environment.GetEnvironmentVariable("P3D_HLRDEDUP") == "0";
    static readonly bool SkipNearOff = Environment.GetEnvironmentVariable("P3D_HLRSKIPNEAR") == "0";
    static readonly bool SeamSilOff = Environment.GetEnvironmentVariable("P3D_HLRSEAMSIL") == "0";
    static readonly bool SilOutOff = Environment.GetEnvironmentVariable("P3D_HLRSILOUT") == "0";
    static readonly bool SmoothOwnOff = Environment.GetEnvironmentVariable("P3D_HLRSMOOTHOWN") == "0";
    /// <summary>Разбор: точек, проверенных на видимость.</summary>
    public static long VisibilityTests;

    sealed class Tri { public Vector3d A, B, C; public uint Face; }

    sealed class Scene
    {
        public readonly List<Tri> Tris = new();
        public double MinX, MinY, Cell; public int Nx, Ny;
        public List<int>[] Grid = Array.Empty<List<int>>();
        public double Eps;
        /// <summary>
        /// k166 (замечание 05.11, 15:03, п. 1, 5): грани, соединённые гладкими рёбрами (цилиндр, разрезанный на несколько граней
        /// швами и продольными рёбрами), — одна «своя» поверхность для правила своей грани. Прежде очерк одной грани закрывала
        /// сетка соседней гладкой грани (по сетке она «перед» очерком на корень из прогиба): на изометрии очерки трубы шли с
        /// разрывами. Мутация P3D_HLRSMOOTHOWN=0 — своя только сама грань.
        /// </summary>
        public Dictionary<uint, uint> Group = new();
        public uint G(uint f) => Group.TryGetValue(f, out var g) ? g : f;

        public void Index()
        {
            double maxX = double.MinValue, maxY = double.MinValue; MinX = MinY = double.MaxValue;
            foreach (var t in Tris) foreach (var p in new[] { t.A, t.B, t.C }) { MinX = Math.Min(MinX, p.X); MinY = Math.Min(MinY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            double size = Math.Max(maxX - MinX, maxY - MinY);
            if (!(size > 0)) size = 1;
            Nx = Ny = Math.Clamp((int)Math.Sqrt(Tris.Count / 2.0), 1, 256);
            Cell = size / Nx * 1.000001;
            Nx = Math.Max(1, (int)Math.Ceiling((maxX - MinX) / Cell) + 1); Ny = Math.Max(1, (int)Math.Ceiling((maxY - MinY) / Cell) + 1);
            Grid = new List<int>[Nx * Ny];
            for (int i = 0; i < Tris.Count; i++)
            {
                var t = Tris[i];
                int x0 = CellX(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X))), x1 = CellX(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
                int y0 = CellY(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))), y1 = CellY(Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) (Grid[y * Nx + x] ??= new()).Add(i);
            }
            Eps = Math.Max(1e-6, size * 2e-4);
        }
        int CellX(double x) => Math.Clamp((int)((x - MinX) / Cell), 0, Nx - 1);
        int CellY(double y) => Math.Clamp((int)((y - MinY) / Cell), 0, Ny - 1);

        /// <summary>Точка (координаты вида, Z — к наблюдателю) закрыта треугольником, кроме граней skip.</summary>
        public bool Hidden(Vector3d p, uint skipA, uint skipB) => Hidden(p, skipA, skipB, Vector3d.Zero);

        /// <summary>
        /// k166: shift — сдвиг точки очерка наружу: свои (гладко соединённые) грани проверяются у сдвинутой точки, чужие — у
        /// самой точки (сдвиг не уводит очерк под чужую кромку).
        /// </summary>
        public bool Hidden(Vector3d p, uint skipA, uint skipB, Vector3d shift)
        {
            Interlocked.Increment(ref VisibilityTests);
            uint ga = G(skipA), gb = G(skipB);
            bool shifted = shift != Vector3d.Zero;
            // проход 0 — точка (без сдвига — все грани, со сдвигом — чужие); 1 и 2 — свои грани у точки, сдвинутой в обе
            // стороны (у вогнутого участка «наружу» — под свою же поверхность): своя закрывает, только если закрывает обе
            bool ownPlus = false;
            for (int pass = 0; pass < (shifted ? 3 : 1); pass++)
            {
                var q = pass == 0 ? p : pass == 1 ? p + shift : p - shift;
                bool hit = false;
                var cell = Grid[CellY(q.Y) * Nx + CellX(q.X)];
                if (cell != null)
                    foreach (int i in cell)
                    {
                        var t = Tris[i];
                        bool own = t.Face == skipA || t.Face == skipB || !SmoothOwnOff && (G(t.Face) == ga || G(t.Face) == gb);
                        if (own && SkipNearOff) continue;
                        if (shifted && own != (pass > 0)) continue;
                        // k161 (замечание 05.11, 01:23, п. 8): своя грань ребра закрывает его, если она перед ним на глубину больше 3 %
                        // габарита (ребро кармана на задней стороне трубы — за передней стороной той же цилиндрической грани).
                        // У очерка своя грань по сетке «перед» точкой на корень из прогиба (у R92 при прогибе 0.05 — около 3 мм): такие
                        // не закрывают. Прежде своя грань пропускалась целиком: ребро за ней рисовалось видимым. Мутация
                        // P3D_HLRSKIPNEAR=0 — прежнее.
                        if (!Inside(t, q.X, q.Y, out double z)) continue;
                        if (z > q.Z + (own ? 150 * Eps : Eps)) { hit = true; break; }
                    }
                if (pass == 0 && hit) return true;
                if (pass == 1) { if (!hit) return false; ownPlus = true; }
                if (pass == 2) return hit && ownPlus;
            }
            return false;
        }

        /// <summary>Точка ближе d к габариту треугольника (в пространстве вида).</summary>
        static bool Near(Tri t, Vector3d p, double d)
        {
            double dx = Math.Max(0, Math.Max(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) - p.X, p.X - Math.Max(t.A.X, Math.Max(t.B.X, t.C.X))));
            double dy = Math.Max(0, Math.Max(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) - p.Y, p.Y - Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y))));
            double dz = Math.Max(0, Math.Max(Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z)) - p.Z, p.Z - Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z))));
            return dx * dx + dy * dy + dz * dz < d * d;
        }

        static bool Inside(Tri t, double x, double y, out double z)
        {
            z = 0;
            double d = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
            if (Math.Abs(d) < 1e-18) return false;
            double l1 = ((t.B.Y - t.C.Y) * (x - t.C.X) + (t.C.X - t.B.X) * (y - t.C.Y)) / d;
            double l2 = ((t.C.Y - t.A.Y) * (x - t.C.X) + (t.A.X - t.C.X) * (y - t.C.Y)) / d;
            double l3 = 1 - l1 - l2;
            const double e = 1e-9;
            if (l1 < -e || l2 < -e || l3 < -e) return false;
            z = l1 * t.A.Z + l2 * t.B.Z + l3 * t.C.Z;
            return true;
        }
    }

    static Vector3d ToView(ViewFrame v, Vector3d p) => new(Vector3d.Dot(p, v.Right), Vector3d.Dot(p, v.Up), v.Depth(p));

    /// <summary>Линии вида тела. chord — наибольший отход ломаной от кривой (мм модели).</summary>
    public static List<ViewLine> Project(TSolid body, ViewFrame view, double chord = 0.02, bool silhouettes = true) =>
        ProjectMany(new[] { body }, view, chord, silhouettes);

    /// <summary>
    /// k156 (D2): линии вида нескольких тел (сборка) — общая сцена скрытия: деталь закрывает деталь. Линии помечены номером
    /// тела (Body).
    /// </summary>
    public static List<ViewLine> ProjectMany(IReadOnlyList<TSolid> bodies, ViewFrame view, double chord = 0.02, bool silhouettes = true)
    {
        var meshes = bodies.Select(MeshOf).ToList();
        // мутация P3D_HLRSHARED=0: у каждого тела своя сцена (деталь не закрывает деталь)
        if (SharedOff && bodies.Count > 1)
        {
            var all = new List<ViewLine>();
            for (int bi = 0; bi < bodies.Count; bi++) all.AddRange(ProjectMany(new[] { bodies[bi] }, view, chord, silhouettes).Select(l => l with { Body = bi }));
            return all;
        }
        var sc = new Scene();
        foreach (var mesh in meshes)
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                var a = mesh.Vertices[mesh.Indices[t]]; var b = mesh.Vertices[mesh.Indices[t + 1]]; var c = mesh.Vertices[mesh.Indices[t + 2]];
                sc.Tris.Add(new Tri { A = ToView(view, (Vector3d)a.P), B = ToView(view, (Vector3d)b.P), C = ToView(view, (Vector3d)c.P), Face = a.FaceId });
            }
        sc.Index();
        // k166: группы гладко соединённых граней (объединение по гладким рёбрам)
        if (!SmoothOwnOff)
            foreach (var b in bodies)
            {
                uint Find(uint f) { while (sc.Group.TryGetValue(f, out var g) && g != f) { var gg = sc.Group.TryGetValue(g, out var g2) ? g2 : g; sc.Group[f] = gg; f = g; } return f; }
                foreach (var e in b.Edges)
                {
                    if (e.Curve == null) continue;
                    var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                    if (fs.Count != 2 || !Smooth(e, fs[0], fs[1])) continue;
                    uint r0 = Find(fs[0].Id), r1 = Find(fs[1].Id);
                    if (r0 != r1) sc.Group[Math.Max(r0, r1)] = Math.Min(r0, r1);
                    sc.Group.TryAdd(Math.Min(r0, r1), Math.Min(r0, r1));
                }
                foreach (var f in sc.Group.Keys.ToList()) sc.Group[f] = Find(f);
            }
        var res = new List<ViewLine>();
        for (int bi = 0; bi < bodies.Count; bi++)
        {
            int from = res.Count;
            foreach (var e in bodies[bi].Edges)
            {
                if (e.Curve == null) continue;
                var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                uint fa = faces.Count > 0 ? faces[0].Id : uint.MaxValue, fb = faces.Count > 1 ? faces[1].Id : uint.MaxValue;
                var kind = faces.Count == 2 && Smooth(e, faces[0], faces[1]) ? ViewLineKind.Tangent : ViewLineKind.Edge;
                // k161 (п. 7): гладкий стык граней, лежащий на очерке (нормаль поперёк взгляда: шов цилиндра у края вида), —
                // это очерк, а не линия перехода; очерк сетки у такого шва выпадает (нуль n·w — в вершинах), и край вида
                // оставался с разрывом. Мутация P3D_HLRSEAMSIL=0 — прежнее (линия перехода, не показывается).
                if (kind == ViewLineKind.Tangent && !SeamSilOff && OnOutline(e, faces[0], view.Toward)) kind = ViewLineKind.Edge;   // как ребро: очерк сетки рядом — повтор (DropCovered)
                var pts = Sample(e, chord);
                Split(sc, view, pts, fa, fb, kind, e.Id, res);
            }
            if (silhouettes) Silhouettes(meshes[bi], view, sc, res);
            for (int i = from; i < res.Count; i++) res[i] = res[i] with { Body = bi };
        }
        if (DbgEdge is { } de)
            foreach (var l in res.Where(l => l.SourceId.ToString() == de))
                Console.WriteLine($"HLRDBG edge {de}: {(l.Visible ? "V" : "h")} {l.Kind} n {l.Points.Count} x {l.Points.Min(q => q.X):0.###}..{l.Points.Max(q => q.X):0.###} y {l.Points.Min(q => q.Y):0.###}..{l.Points.Max(q => q.Y):0.###}");
        return DropCovered(res, sc.Eps * 5);
    }
    static readonly string? DbgEdge = Environment.GetEnvironmentVariable("P3D_HLRDBG");
    static readonly bool SimplifyOff = Environment.GetEnvironmentVariable("P3D_HLRSIMPLIFY") == "0";

    /// <summary>
    /// k156 (D3): дуги рёбер в плоскости вида (ось дуги вдоль взгляда) — для размеров радиусов и диаметров. Одинаковые по
    /// проекции (верхняя и нижняя кромки отверстия) — одна.
    /// </summary>
    public static List<ViewArc> Arcs(IReadOnlyList<TSolid> bodies, ViewFrame view)
    {
        var w = view.Toward;
        var res = new List<ViewArc>();
        for (int bi = 0; bi < bodies.Count; bi++)
            foreach (var e in bodies[bi].Edges)
            {
                if (e.Curve is not Kernel.Curves.ArcCurve3 a) continue;
                if (Math.Abs(Math.Abs(Vector3d.Dot(a.Frame.W.Normalized(), w)) - 1) > 1e-9) continue;
                var c = view.Project(a.Frame.Origin);
                bool full = Math.Abs(Math.Abs(a.A1 - a.A0) - 2 * Math.PI) < 1e-6;
                var p0 = view.Project(a.PointAt(a.T0)) - c; var p1 = view.Project(a.PointAt(a.T1)) - c; var pm = view.Project(a.PointAt((a.T0 + a.T1) / 2)) - c;
                double a0 = Math.Atan2(p0.Y, p0.X), a1 = Math.Atan2(p1.Y, p1.X), am = Math.Atan2(pm.Y, pm.X);
                // обход против часовой от a0 до a1 через am
                double N(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
                if (N(am - a0) > N(a1 - a0)) (a0, a1) = (a1, a0);
                if (res.Any(r => (r.Center - c).Length < 1e-6 && Math.Abs(r.Radius - a.Radius) < 1e-6 && (r.Full || full || Math.Abs(N(r.A0 - a0)) < 1e-6))) continue;
                res.Add(new ViewArc(c, a.Radius, a0, a1, full, bi));
            }
        // k164 (замечание 05.11, 10:33, п. 4): ребро на цилиндре с осью вдоль взгляда — дуга на виде, даже если кривая ребра не
        // дуга (линия пересечения: паз прорезан через кривую грань). Прежде такие дуги (верх шпоночного паза) не давали ни
        // центра, ни радиуса. Проверка — все точки проекции на окружности цилиндра. Мутация P3D_ARCSONCYL=0.
        if (!ArcsOnCylOff)
            for (int bi = 0; bi < bodies.Count; bi++)
                foreach (var f in bodies[bi].Faces)
                {
                    var srf = GeometryAudit.AuthoritativeSurface(f);
                    (Vector3d O, Vector3d W, double R)? cyl = srf switch
                    {
                        Kernel.Surfaces.CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W, cs.Radius),
                        Kernel.Surfaces.ExtrudedSurface es when es.Base is Kernel.Curves.ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), es.Dir.Normalized())) - 1) < 1e-9 => (arc.Frame.Origin, es.Dir, arc.Radius),
                        _ => null,
                    };
                    if (cyl is not { } cy || Math.Abs(Math.Abs(Vector3d.Dot(cy.W.Normalized(), w)) - 1) > 1e-9) continue;
                    var c = view.Project(cy.O);
                    foreach (var e in f.Edges)
                    {
                        if (e.Curve is null or Kernel.Curves.ArcCurve3) continue;
                        var cu = e.Curve;
                        var pts = Enumerable.Range(0, 25).Select(k => view.Project(cu.PointAt(cu.T0 + (cu.T1 - cu.T0) * k / 24.0)) - c).ToList();
                        if (pts.Any(q => Math.Abs(q.Length - cy.R) > 1e-4 * Math.Max(1, cy.R))) continue;
                        if ((pts[0] - pts[24]).Length < 1e-4 * cy.R && pts.Max(q => (q - pts[0]).Length) < 1e-3 * cy.R) continue;   // ребро вдоль взгляда — точка
                        bool full = (pts[0] - pts[24]).Length < 1e-6 * Math.Max(1, cy.R);
                        double a0 = Math.Atan2(pts[0].Y, pts[0].X), a1 = Math.Atan2(pts[24].Y, pts[24].X), am = Math.Atan2(pts[12].Y, pts[12].X);
                        double N(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
                        if (N(am - a0) > N(a1 - a0)) (a0, a1) = (a1, a0);
                        if (res.Any(r => (r.Center - c).Length < 1e-6 * Math.Max(1, cy.R) && Math.Abs(r.Radius - cy.R) < 1e-6 * Math.Max(1, cy.R) && (r.Full || full || Math.Min(N(r.A0 - a0), 2 * Math.PI - N(r.A0 - a0)) < 1e-4))) continue;
                        res.Add(new ViewArc(c, cy.R, a0, a1, full, bi));
                    }
                }
        return res;
    }
    static readonly bool ArcsOnCylOff = Environment.GetEnvironmentVariable("P3D_ARCSONCYL") == "0";

    /// <summary>
    /// Оси граней вращения (цилиндр, поверхность вращения): грани с общей осью (та же прямая с точностью 1e-6 габарита)
    /// объединяются; ось вдоль взгляда — перекрестие центра, поперёк взгляда — отрезок по длине граней; наклонные оси не
    /// рисуются (на них — вспомогательные виды, D5).
    /// </summary>
    /// <param name="tilted">k167 (замечание 05.11, 16:17, п. 7): и наклонные к виду оси (изометрия) — проекция оси по длине граней.</param>
    public static List<ViewAxis> Axes(TSolid body, ViewFrame view, bool tilted = false)
    {
        var w = view.Toward;
        var groups = new List<(Vector3d O, Vector3d W, List<Vector3d> Pts)>();
        foreach (var f in body.Faces)
        {
            var srf = GeometryAudit.AuthoritativeSurface(f);
            (Vector3d Origin, Vector3d W)? ax = srf switch
            {
                Kernel.Surfaces.CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W),
                Kernel.Surfaces.RevolvedSurface rs => (rs.Axis.Origin, rs.Axis.W),
                // выдавленная дуга, направление — вдоль оси дуги: цилиндр
                Kernel.Surfaces.ExtrudedSurface es when es.Base is Kernel.Curves.ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), es.Dir.Normalized())) - 1) < 1e-9 => (arc.Frame.Origin, es.Dir),
                _ => null,
            };
            if (ax is not { } a) continue;
            var pts = new List<Vector3d>();
            foreach (var e in f.Edges) { if (e.Curve == null) continue; for (int k = 0; k <= 72; k++) pts.Add(e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 72)); }
            if (pts.Count == 0) continue;
            var W = a.W.Normalized();
            int gi = groups.FindIndex(g => Math.Abs(Math.Abs(Vector3d.Dot(g.W, W)) - 1) < 1e-9 && Vector3d.Cross(a.Origin - g.O, g.W).Length < 1e-6 * Math.Max(1, (a.Origin - g.O).Length));
            if (gi < 0) groups.Add((a.Origin, W, pts)); else groups[gi].Pts.AddRange(pts);
        }
        var res = new List<ViewAxis>();
        foreach (var (o, W, pts) in groups)
        {
            double along = Math.Abs(Vector3d.Dot(W, w));
            double r = pts.Max(p => ((p - o) - W * Vector3d.Dot(p - o, W)).Length);
            if (r < 1e-9) continue;
            // ось — только у полного оборота (отверстие, вал, бобышка); у скругления угла (четверть оборота) оси нет
            if (!FullTurnOff)
            {
                var u = Math.Abs(W.X) < 0.9 ? Vector3d.Cross(W, Vector3d.UnitX).Normalized() : Vector3d.Cross(W, Vector3d.UnitY).Normalized();
                var vv = Vector3d.Cross(W, u);
                var bins = new bool[36];
                foreach (var p in pts)
                {
                    var q = p - o - W * Vector3d.Dot(p - o, W);
                    if (q.Length < 1e-9) continue;
                    double ang = Math.Atan2(Vector3d.Dot(q, vv), Vector3d.Dot(q, u));
                    bins[Math.Clamp((int)((ang + Math.PI) / (2 * Math.PI) * 36), 0, 35)] = true;
                }
                if (bins.Count(b => b) < 30) continue;
            }
            if (along > 1 - 1e-9) { res.Add(new ViewAxis(view.Project(o), Vector2d.UnitX, -r, r, true, r)); continue; }
            if (along > 1e-9 && !tilted) continue;
            var d2 = view.Project(o + W) - view.Project(o);
            if (d2.Length < 1e-12) continue;
            d2.Normalize();
            var c = view.Project(o);
            var t = pts.Select(p => Vector2d.Dot(view.Project(p) - c, d2)).ToList();
            res.Add(new ViewAxis(c, d2, t.Min(), t.Max(), false, r));
        }
        return res;
    }

    /// <summary>
    /// k162: сетка тела для чертежа — один раз на тело (тела входа чертежа не меняются; новая модель — новые тела). Прежде
    /// каждый вид, тона и масса строили сетку заново.
    /// </summary>
    public static MeshData MeshOf(TSolid b) => MeshCache.GetValue(b, x => Tessellator.Tessellate(x));
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TSolid, MeshData> MeshCache = new();

    /// <summary>k162: точки рёбер тела (для быстрого вида при повороте), кэш на тело.</summary>
    public static List<List<Vector3d>> EdgePoints(TSolid b) => EdgeCache.GetValue(b, x => x.Edges.Where(e => e.Curve != null).Select(e => Sample(e, 0.2)).ToList());
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TSolid, List<List<Vector3d>>> EdgeCache = new();

    /// <summary>Ребро гладкого стыка: нормали граней у трёх точек ребра совпадают (угол меньше ~1°).</summary>
    static bool Smooth(TEdge e, TFace a, TFace b)
    {
        for (int k = 1; k <= 3; k++)
        {
            var p = e.Curve!.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 4.0);
            Vector3d na, nb;
            try { na = a.NormalAt(p); nb = b.NormalAt(p); } catch { return false; }
            if (na.LengthSquared < 1e-24 || nb.LengthSquared < 1e-24) return false;
            if (Vector3d.Dot(na.Normalized(), nb.Normalized()) < 0.99985) return false;
        }
        return true;
    }

    /// <summary>Ребро на очерке: нормаль грани в трёх точках ребра поперёк взгляда.</summary>
    static bool OnOutline(TEdge e, TFace f, Vector3d w)
    {
        for (int k = 1; k <= 3; k++)
        {
            var p = e.Curve!.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 4.0);
            Vector3d n;
            try { n = f.NormalAt(p); } catch { return false; }
            if (n.LengthSquared < 1e-24 || Math.Abs(Vector3d.Dot(n.Normalized(), w)) > 2e-3) return false;
        }
        return true;
    }

    /// <summary>Точки ребра: прямая — концы; кривая — деление пополам, пока отход середины хорды больше chord.</summary>
    static List<Vector3d> Sample(TEdge e, double chord)
    {
        var c = e.Curve!;
        var pts = new List<Vector3d> { c.PointAt(c.T0) };
        void Rec(double t0, Vector3d p0, double t1, Vector3d p1, int depth)
        {
            double tm = (t0 + t1) / 2; var pm = c.PointAt(tm);
            var mid = (p0 + p1) / 2;
            if (depth < 12 && ((pm - mid).Length > chord || depth < 2 && c is not Kernel.Curves.LineCurve3))
            { Rec(t0, p0, tm, pm, depth + 1); Rec(tm, pm, t1, p1, depth + 1); }
            else pts.Add(p1);
        }
        Rec(c.T0, pts[0], c.T1, c.PointAt(c.T1), 0);
        return pts;
    }

    /// <summary>Ломаная → видимые и невидимые участки: пробы через шаг не больше 1/64 габарита, граница — делением пополам.</summary>
    static void Split(Scene sc, ViewFrame view, List<Vector3d> pts3, uint fa, uint fb, ViewLineKind kind, uint id, List<ViewLine> res, List<Vector3d>? normals = null)
    {
        var pv = pts3.Select(p => ToView(view, p)).ToList();
        // k166 (замечание 05.11, 15:03, п. 1, 5): ОЧЕРК — ПРОВЕРКА СНАРУЖИ. Очерк по нормалям вершин лежит на точной поверхности,
        // а очертание сетки — снаружи от него на прогиб граней; передние грани той же поверхности по сетке «закрывали» очерк
        // (труба R92, грубая сетка: на глубину 12.6 мм — на пороге правила своей грани), и очерки изометрии шли с разрывами.
        // Точка очерка проверяется сдвинутой наружу по нормали поверхности (она поперёк взгляда — в плоскости вида) на
        // 6·Eps (0.1 % габарита): за очертанием сетки своей поверхности, под чужими гранями — как прежде. Мутация
        // P3D_HLRSILOUT=0 — без сдвига.
        var nv = normals != null && !SilOutOff ? normals.Select(n => { var q = new Vector3d(Vector3d.Dot(n, view.Right), Vector3d.Dot(n, view.Up), 0); return q.LengthSquared > 1e-24 ? q.Normalized() * (6 * sc.Eps) : Vector3d.Zero; }).ToList() : null;
        double step = Math.Max(sc.Cell / 2, 1e-3);
        var dense = new List<Vector3d> { pv[0] };
        var off = new List<Vector3d> { nv?[0] ?? Vector3d.Zero };
        for (int i = 1; i < pv.Count; i++)
        {
            double len = new Vector2d(pv[i].X - pv[i - 1].X, pv[i].Y - pv[i - 1].Y).Length;
            int n = Math.Clamp((int)Math.Ceiling(len / step), 1, 512);
            for (int k = 1; k <= n; k++) { dense.Add(pv[i - 1] + (pv[i] - pv[i - 1]) * (k / (double)n)); off.Add(nv == null ? Vector3d.Zero : nv[i - 1] + (nv[i] - nv[i - 1]) * (k / (double)n)); }
        }
        // сдвиг точки отрезка [i−1, i] — по ближайшей пробе (деление пополам идёт внутри одного отрезка)
        Vector3d curOff = Vector3d.Zero;
        bool Hid(Vector3d p) => sc.Hidden(p, fa, fb, curOff);
        var cur = new List<Vector2d> { dense[0].Xy };
        // видимость участка — по его середине (у концов ребро касается соседних граней)
        curOff = (off[0] + off[Math.Min(1, off.Count - 1)]) / 2;
        bool vis = !Hid((dense[0] + dense[Math.Min(1, dense.Count - 1)]) / 2);
        for (int i = 1; i < dense.Count; i++)
        {
            curOff = (off[i - 1] + off[i]) / 2;
            bool v = !Hid((dense[i - 1] + dense[i]) / 2);
            if (v != vis)
            {
                // граница внутри [i−1, i]: деление пополам по видимости
                Vector3d a = dense[i - 1], b = dense[i];
                for (int it = 0; it < 20; it++) { var m = (a + b) / 2; if (!Hid(m) == vis) a = m; else b = m; }
                var cut = ((a + b) / 2).Xy;
                cur.Add(cut);
                if (Length(cur) > 1e-6) res.Add(new ViewLine(cur, vis, kind, id));
                cur = new List<Vector2d> { cut };
                vis = v;
            }
            cur.Add(dense[i].Xy);
        }
        if (Length(cur) > 1e-6) res.Add(new ViewLine(Simplify(cur), vis, kind, id));
    }

    /// <summary>
    /// Невидимая линия, лежащая на видимой (заднее ребро за передним, нижняя окружность под верхней), на чертеже не рисуется;
    /// линии перехода и очерки, совпадающие с рёбрами, — тоже. Покрытие — все пробы линии ближе eps к видимому ребру.
    /// </summary>
    static List<ViewLine> DropCovered(List<ViewLine> lines, double eps)
    {
        // видимые рёбра — от длинных к коротким; ребро, целиком лежащее на уже принятом видимом (нижняя кромка сквозного
        // отверстия под верхней), — повтор
        var vis = new List<ViewLine>();
        bool Covered(ViewLine l)
        {
            for (int i = 0; i + 1 < l.Points.Count; i++)
                for (int k = 0; k <= 4; k++)
                {
                    var q = l.Points[i] + (l.Points[i + 1] - l.Points[i]) * (k / 4.0);
                    if (!vis.Any(v => DistTo(v.Points, q) < eps)) return false;
                }
            return true;
        }
        foreach (var l in lines.Where(l => l.Visible && l.Kind == ViewLineKind.Edge).OrderByDescending(l => Length(l.Points)))
            if (DedupOff || !Covered(l)) vis.Add(l);
        var res = new List<ViewLine>(vis);
        foreach (var l in lines)
        {
            if (l.Visible && l.Kind == ViewLineKind.Edge) continue;
            if (!Covered(l)) res.Add(l);
        }
        return res;
    }

    static double DistTo(List<Vector2d> pl, Vector2d q)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pl.Count; i++)
        {
            var a = pl[i]; var ab = pl[i + 1] - a;
            double t = ab.LengthSquared < 1e-24 ? 0 : Math.Clamp(Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
            best = Math.Min(best, (a + ab * t - q).Length);
        }
        if (pl.Count == 1) best = (pl[0] - q).Length;
        return best;
    }

    static double Length(List<Vector2d> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length; return s; }

    /// <summary>Убрать промежуточные точки на прямых участках.</summary>
    public static List<Vector2d> Simplify(List<Vector2d> p)
    {
        if (p.Count < 3) return p;
        var r = new List<Vector2d> { p[0] };
        for (int i = 1; i < p.Count - 1; i++)
        {
            var a = r[^1]; var b = p[i]; var c = p[i + 1];
            var ab = b - a; var ac = c - a;
            double cross = Math.Abs(ab.X * ac.Y - ab.Y * ac.X);
            // k163 (замечание 05.11, 09:22, п. 2): точка поворота назад (дуга в плоскости, видимой «с ребра»: её проекция идёт
            // вверх и возвращается по той же прямой) — сохраняется; прежде проверялось направление a→c, и если c не дальше
            // a, вершина b выпадала — от дуги оставался отрезок нулевой длины, край вида (торец трубы) рисовался не до
            // конца. Мутация P3D_HLRSIMPLIFY=0 — прежнее.
            bool back = SimplifyOff ? Vector2d.Dot(ab, ac) < 0 : Vector2d.Dot(ab, c - b) < 0;
            if (cross > 1e-9 * Math.Max(1, ac.LengthSquared) || back) r.Add(b);
        }
        r.Add(p[^1]);
        return r;
    }

    /// <summary>
    /// Очерки кривых граней — линия нуля функции g = n·w (n — нормаль поверхности в вершинах сетки, она точная, w — к
    /// наблюдателю): в каждом треугольнике грани, где g меняет знак, — отрезок между точками нуля на его сторонах (линейная
    /// интерполяция). У цилиндра и конуса, чья образующая — сторона сетки, очерк точный. Отрезки собираются в цепочки по
    /// общим концам; плоские грани (нормаль постоянна) очерка не дают.
    /// </summary>
    static void Silhouettes(MeshData mesh, ViewFrame view, Scene sc, List<ViewLine> res)
    {
        var w = view.Toward;
        var byFace = new Dictionary<uint, List<(Vector3d A, Vector3d B)>>();
        var normalAt = new Dictionary<(long, long, long), Vector3d>();
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            var v0 = mesh.Vertices[mesh.Indices[t]]; var v1 = mesh.Vertices[mesh.Indices[t + 1]]; var v2 = mesh.Vertices[mesh.Indices[t + 2]];
            var n0 = (Vector3d)v0.N; var n1 = (Vector3d)v1.N; var n2 = (Vector3d)v2.N;
            if ((n0 - n1).LengthSquared < 1e-12 && (n0 - n2).LengthSquared < 1e-12) continue;   // плоский участок
            // участок, видимый «с ребра» (нормаль поперёк взгляда во всех вершинах: стенка отверстия вдоль взгляда), — его
            // проекция вырождена в линию, совпадающую с рёбрами; очерка нет
            if (Math.Max(Math.Abs(Vector3d.Dot(n0, w)), Math.Max(Math.Abs(Vector3d.Dot(n1, w)), Math.Abs(Vector3d.Dot(n2, w)))) < 1e-6) continue;
            double g0 = G(n0), g1 = G(n1), g2 = G(n2);
            double G(Vector3d n) { double g = Vector3d.Dot(n, w); return Math.Abs(g) < 1e-12 ? 1e-12 : g; }
            var P = new[] { (Vector3d)v0.P, (Vector3d)v1.P, (Vector3d)v2.P }; var g = new[] { g0, g1, g2 };
            var N = new[] { n0, n1, n2 };
            var cross = new List<Vector3d>(2);
            for (int k = 0; k < 3; k++)
            {
                int m = (k + 1) % 3;
                if (g[k] * g[m] < 0) { double u = g[k] / (g[k] - g[m]); var cp = P[k] + (P[m] - P[k]) * u; cross.Add(cp); normalAt.TryAdd(Key(cp), N[k] + (N[m] - N[k]) * u); }
            }
            if (cross.Count != 2 || (cross[0] - cross[1]).LengthSquared < 1e-18) continue;
            (byFace.TryGetValue(v0.FaceId, out var l) ? l : byFace[v0.FaceId] = new()).Add((cross[0], cross[1]));
        }
        static (long, long, long) Key(Vector3d p) => ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
        foreach (var (face, segs) in byFace)
        {
            var at = new Dictionary<(long, long, long), List<int>>();
            for (int i = 0; i < segs.Count; i++)
                foreach (var q in new[] { segs[i].A, segs[i].B }) { var k = Key(q); (at.TryGetValue(k, out var l) ? l : at[k] = new()).Add(i); }
            var used = new bool[segs.Count];
            for (int s0 = 0; s0 < segs.Count; s0++)
            {
                if (used[s0]) continue;
                used[s0] = true;
                var chain = new LinkedList<Vector3d>(); chain.AddLast(segs[s0].A); chain.AddLast(segs[s0].B);
                foreach (bool fwd in new[] { true, false })
                    while (true)
                    {
                        var end = fwd ? chain.Last!.Value : chain.First!.Value;
                        int next = -1;
                        if (at.TryGetValue(Key(end), out var cand)) foreach (int c in cand) if (!used[c]) { next = c; break; }
                        if (next < 0) break;
                        used[next] = true;
                        var other = Key(segs[next].A) == Key(end) ? segs[next].B : segs[next].A;
                        if (fwd) chain.AddLast(other); else chain.AddFirst(other);
                    }
                var cl = chain.ToList();
                Split(sc, view, cl, face, uint.MaxValue, ViewLineKind.Silhouette, face, res, cl.Select(q => normalAt.TryGetValue(Key(q), out var nn) ? nn : Vector3d.Zero).ToList());
            }
        }
    }
}
