using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;

namespace Parametric3D.App.Viewport;

/// <summary>
/// Выбор объекта под курсором — расчётом на процессоре.
///
/// ПОЧЕМУ НЕ ЧЕРЕЗ КАДРОВЫЙ БУФЕР. Изначально id объекта писался вторым
/// вложением кадрового буфера (R32UI) и читался через glReadPixels. Схема
/// красивая, но на практике возвращала ноль, и отладить её без запуска
/// приложения оказалось невозможно: любая мелочь в состоянии драйвера —
/// буферы вывода, целочисленный формат, порядок привязок — молча даёт
/// пустое чтение. Здесь нет ни одной такой зависимости: обычная геометрия,
/// обычная арифметика, поведение полностью предсказуемо.
///
/// Приоритет соответствует ожиданиям в CAD: точку поймать легче, чем
/// ребро, ребро — легче, чем грань. Поэтому точки перебивают кривые,
/// кривые перебивают поверхности, даже если поверхность ближе к камере.
/// </summary>
public sealed class ScenePicker
{
    /// <summary>Допуск захвата для точек и кривых, в пикселях экрана.</summary>
    public float TolerancePx = Core.Settings.Current.PickTolerancePx;

    public readonly struct Hit
    {
        public readonly PickRef Ref;
        /// <summary>3 — точка, 2 — кривая или ребро, 1 — поверхность.</summary>
        public readonly int Priority;
        /// <summary>Экранное расстояние (для точек и кривых) либо глубина (для граней).</summary>
        public readonly float Score;
        /// <summary>Глубина по лучу взгляда (мировые единицы): по ней точки и рёбра за гранями тела отсекаются.</summary>
        public readonly float Depth;

        public Hit(PickRef r, int priority, float score, float depth = float.NaN) { Ref = r; Priority = priority; Score = score; Depth = depth; }
    }

    private Matrix4 _viewProj;
    private Vector2 _viewport;
    private Vector2 _mouse;
    private Vector3 _rayO, _rayD;

    private readonly List<Hit> _hits = new();

    /// <summary>
    /// Активный владелец: сборка в режиме сборки, текущая деталь в режиме детали.
    /// Попадания в базовые элементы чужого владельца (и, в режиме детали, в тела других
    /// деталей) идут с пониженным приоритетом: выбираются, только если под курсором нет
    /// ничего своего. Null — все равны.
    /// </summary>
    public ModelItem? ActiveOwner { get; set; }

    private const int ForeignPenalty = 10;

    private void Demote(int from)
    {
        for (int i = from; i < _hits.Count; i++)
        {
            Hit h = _hits[i];
            _hits[i] = new Hit(h.Ref, h.Priority - ForeignPenalty, h.Score, h.Depth);
        }
    }

    /// <summary>Все кандидаты под курсором, лучший первым.</summary>
    public IReadOnlyList<Hit> PickAll(Document doc, Camera cam, OpenTK.Mathematics.Vector2i mouse, bool datumPlanesVisible,
                                      Sketch? activeSketch)
    {
        _hits.Clear();

        _viewProj = cam.ViewMatrix * cam.ProjMatrix;
        _viewport = new Vector2(cam.ViewportWidth, cam.ViewportHeight);
        _mouse = new Vector2(mouse.X, mouse.Y);
        cam.ScreenRay(mouse.X, mouse.Y, out _rayO, out _rayD);

        Walk(doc.Root, Matrix4.Identity, datumPlanesVisible, activeSketch);

        // ЗАКРЫТОЕ ТЕЛОМ НЕ ВЫБИРАЕТСЯ. Точка, ребро или плоскость за ближайшей гранью тела под курсором не ловятся:
        // начало координат внутри бруска перехватывало выбор его грани (точки важнее граней по приоритету). Рёбра на
        // самой грани остаются (допуск — 3 пикселя в мировых единицах и 0.1 % глубины).
        if (!OcclusionOff)
        {
            float tBody = float.PositiveInfinity;
            foreach (var h in _hits) if (h.Ref.Kind == PickKind.Face && h.Depth < tBody) tBody = h.Depth;
            if (!float.IsPositiveInfinity(tBody))
            {
                float tol = MathF.Max(3f / MathF.Max(1e-6f, cam.WorldToPx), 1e-3f * MathF.Abs(tBody));
                _hits.RemoveAll(h => h.Ref.Kind != PickKind.Face && !float.IsNaN(h.Depth) && h.Depth > tBody + tol);
            }
        }

        _hits.Sort((a, b) =>
        {
            int c = b.Priority.CompareTo(a.Priority);
            return c != 0 ? c : a.Score.CompareTo(b.Score);
        });
        return _hits;
    }

    // ------------------------------------------------------------ обход модели

    private void Walk(ModelItem item, Matrix4 parent, bool datumPlanes, Sketch? activeSketch)
    {
        Matrix4 xf = item switch
        {
            Part p => p.LocalTransform * parent,
            Assembly a => a.LocalTransform * parent,
            _ => parent
        };

        if (item is Part part && part.Visible)
        {
            int before = _hits.Count;
            AddPart(part, xf);
            // В режиме детали тела других деталей — чужой контекст.
            if (ActiveOwner is Part && !ReferenceEquals(part, ActiveOwner)) Demote(before);
        }

        bool foreign = ActiveOwner != null && !ReferenceEquals(item, ActiveOwner);

        foreach (var c in item.Children)
        {
            if (!c.Visible && c != activeSketch) { continue; }

            int beforeChild = _hits.Count;
            switch (c)
            {
                case DatumPlane dp when datumPlanes: AddDatumPlane(dp, xf); break;
                case DatumPoint pt: AddPoint(pt.P, xf, new PickRef(pt.Id, PickKind.DatumPoint, item, pt)); break;
                case DatumAxis ax: AddAxis(ax, xf); break;
                case Line3DItem ln:
                    AddSegment3(ln.A, ln.B, xf, new PickRef(ln.Id, PickKind.DatumAxis, item, ln));
                    break;
                case DatumCsys cs: AddPoint(cs.Frame.Origin, xf, new PickRef(cs.Id, PickKind.DatumPoint, item, cs)); break;
                case DatumCurve dc:
                    for (int k = 1; k < dc.Points.Count; k++) AddSegment3(dc.Points[k - 1], dc.Points[k], xf, new PickRef(dc.Id, PickKind.DatumAxis, item, dc));
                    break;
                case Sketch sk: AddSketch(sk, xf, sk == activeSketch); break;
            }

            // Базовые элементы чужого владельца — после своих.
            if (foreign && c is DatumPlane or DatumPoint or DatumAxis or Line3DItem or DatumCsys or DatumCurve) Demote(beforeChild);

            Walk(c, xf, datumPlanes, activeSketch);
        }
    }

    /// <summary>Выбирать ли вершины тела. Пока выключено (см. AddPart).</summary>
    public static bool PickBodyVertices = true;

    private void AddPart(Part part, Matrix4 xf)
    {
        var md = part.Mesh;
        if (md == null) return;

        // Вершины, рёбра и грани адресуются одними только id: искать
        // топологический объект для КАЖДОГО кандидата — это линейный
        // поиск внутри цикла по всей сетке, то есть квадратичная работа
        // на каждое движение мыши. Владелец (деталь) в ссылке есть,
        // а сам объект при необходимости достаётся по id уже потом.
        // ВЕРШИНЫ ТЕЛА ПОКА НЕ ВЫБИРАЮТСЯ.
        //
        // Выбор вершины нужен только в особых операциях — задать положение
        // плоскости, точки, оси по углу тела, — и только угловых вершин,
        // а не точек на дугах. Пока таких операций нет, вершина под курсором
        // лишь мешает выбирать рёбра. Включается флагом, когда понадобится.
        if (PickBodyVertices)
            foreach (var (vid, p) in md.Points)
                AddPoint(new Vector3d(p.X, p.Y, p.Z), xf,
                         new PickRef(vid, PickKind.Vertex, part, null));

        foreach (var (edgeId, pts) in md.EdgePolylines)
        {
            var reference = new PickRef(edgeId, PickKind.Edge, part, null);
            for (int i = 1; i < pts.Length; i++)
                AddSegment(pts[i - 1], pts[i], xf, reference);
        }

        // Грани
        var verts = md.Vertices;
        var idx = md.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            Vector3 a = Vector3.TransformPosition(verts[idx[i]].P, xf);
            Vector3 b = Vector3.TransformPosition(verts[idx[i + 1]].P, xf);
            Vector3 c = Vector3.TransformPosition(verts[idx[i + 2]].P, xf);
            if (!RayTriangle(a, b, c, out float t)) continue;

            uint faceId = verts[idx[i]].FaceId;
            _hits.Add(new Hit(new PickRef(faceId, PickKind.Face, part, null), 1, t, t));
        }
    }

    private void AddDatumPlane(DatumPlane dp, Matrix4 xf)
    {
        Vector3[] c = dp.DisplayCorners();
        for (int i = 0; i < 4; i++) c[i] = Vector3.TransformPosition(c[i], xf);

        var reference = new PickRef(dp.Id, PickKind.DatumPlane, dp.Parent, dp);

        // Рамка ловится как кривая — по ней в плоскость попасть проще всего.
        for (int i = 0; i < 4; i++)
            AddSegmentWorld(c[i], c[(i + 1) % 4], reference, PickKind.DatumPlane);

        // Сама поверхность.
        if (RayTriangle(c[0], c[1], c[2], out float t1)) _hits.Add(new Hit(reference, 1, t1, t1));
        else if (RayTriangle(c[0], c[2], c[3], out float t2)) _hits.Add(new Hit(reference, 1, t2, t2));
    }

    private void AddAxis(DatumAxis ax, Matrix4 xf)
    {
        Vector3d d = ax.Direction * ax.DisplayLength;
        AddSegment3(ax.Origin - d, ax.Origin + d, xf,
                    new PickRef(ax.Id, PickKind.DatumAxis, ax.Parent, ax));
    }

    private void AddSketch(Sketch sk, Matrix4 xf, bool active)
    {
        Frame3 plane = sk.Plane;

        foreach (var (entity, pts, closed) in sk.Polylines())
        {
            var reference = new PickRef(entity.Id, PickKind.SketchCurve, sk, entity);
            int segs = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < segs; i++)
            {
                Vector3d a = plane.ToWorld(pts[i]);
                Vector3d b = plane.ToWorld(pts[(i + 1) % pts.Count]);
                AddSegment3(a, b, xf, reference);
            }
        }

        // Точки эскиза ловятся только в активном эскизе — там же, где они рисуются.
        if (!active) return;
        foreach (var p in sk.AllPoints())
            AddPoint(plane.ToWorld(p.P), xf, new PickRef(p.Id, PickKind.SketchPoint, sk, p));

        AddDimensions(sk, plane, xf);
        AddConstraintGlyphs(sk, plane, xf);
    }

    /// <summary>
    /// Значки ограничений ловятся квадратом вокруг знака, в пикселях.
    /// У одного ограничения значков может быть несколько — попадание
    /// в любой означает выбор самого ограничения.
    /// </summary>
    private void AddConstraintGlyphs(Sketch sk, Frame3 plane, Matrix4 xf)
    {
        foreach (var c in sk.Constraints)
        {
            if (!c.Enabled || c.GlyphPositions.Count == 0) continue;

            float box = (float)(Render.ConstraintLayout.SizePx(c.Type) * 0.5) + 3f;

            foreach (var g in c.GlyphPositions)
            {
                Vector3d w3 = plane.ToWorld(g);
                Vector3 w = Vector3.TransformPosition(
                    new Vector3((float)w3.X, (float)w3.Y, (float)w3.Z), xf);

                if (!Project(w, out Vector2 s, out _)) continue;

                Vector2 delta = s - _mouse;
                if (Math.Abs(delta.X) > box || Math.Abs(delta.Y) > box) continue;

                _hits.Add(new Hit(new PickRef(c.Id, PickKind.SketchConstraint, sk, c), 3, delta.Length));
                break;
            }
        }
    }

    /// <summary>
    /// Подписи размеров ловятся прямоугольником вокруг текста.
    ///
    /// Размер прямоугольника считается в ПИКСЕЛЯХ, как и сам текст:
    /// в координатах эскиза он менялся бы с масштабом, и по крупной
    /// цифре попасть было бы легче, чем по мелкой.
    /// </summary>
    private void AddDimensions(Sketch sk, Frame3 plane, Matrix4 xf)
    {
        foreach (var d in sk.Dimensions)
        {
            if (!d.Enabled) continue;

            Vector3d w3 = plane.ToWorld(d.TextCenter);
            Vector3 w = Vector3.TransformPosition(
                new Vector3((float)w3.X, (float)w3.Y, (float)w3.Z), xf);

            if (!Project(w, out Vector2 s, out _)) continue;

            const double TextHeightPx = 12;
            float halfW = (float)(Render.StrokeFont.Width(d.Value.ToString("0.##")) * TextHeightPx * 0.5) + 4f;
            float halfH = (float)(TextHeightPx * 0.5) + 4f;

            Vector2 delta = s - _mouse;
            if (Math.Abs(delta.X) > halfW || Math.Abs(delta.Y) > halfH) continue;

            _hits.Add(new Hit(new PickRef(d.Id, PickKind.SketchDimension, sk, d), 3, delta.Length));
        }
    }

    // ------------------------------------------------------------ примитивы

    private void AddPoint(Vector3d world, Matrix4 xf, PickRef reference)
    {
        Vector3 w = Vector3.TransformPosition(new Vector3((float)world.X, (float)world.Y, (float)world.Z), xf);
        if (!Project(w, out Vector2 s, out float depth)) return;
        float d = (s - _mouse).Length;
        if (d > TolerancePx) return;
        _hits.Add(new Hit(reference, 3, d, Vector3.Dot(w - _rayO, _rayD)));
    }

    private void AddSegment(Vector3 a, Vector3 b, Matrix4 xf, PickRef reference) =>
        AddSegmentWorld(Vector3.TransformPosition(a, xf), Vector3.TransformPosition(b, xf), reference, reference.Kind);

    private void AddSegment3(Vector3d a, Vector3d b, Matrix4 xf, PickRef reference) =>
        AddSegment(new Vector3((float)a.X, (float)a.Y, (float)a.Z),
                   new Vector3((float)b.X, (float)b.Y, (float)b.Z), xf, reference);

    private void AddSegmentWorld(Vector3 a, Vector3 b, PickRef reference, PickKind kind)
    {
        if (!Project(a, out Vector2 sa, out _)) return;
        if (!Project(b, out Vector2 sb, out _)) return;

        float d = DistancePointSegment(_mouse, sa, sb, out float u);
        if (d > TolerancePx) return;
        // глубина — у точки отрезка под курсором (параметр по экрану ≈ по отрезку для коротких звеньев)
        var wp = a + (b - a) * u;
        _hits.Add(new Hit(reference, 2, d, Vector3.Dot(wp - _rayO, _rayD)));
    }

    // ------------------------------------------------------------ математика

    /// <summary>Мир -&gt; оконные координаты (Y вниз), как их даёт мышь.</summary>
    private bool Project(Vector3 w, out Vector2 screen, out float depth)
    {
        screen = Vector2.Zero;
        depth = 0;

        Vector4 clip = new Vector4(w, 1f) * _viewProj;
        if (clip.W <= 1e-6f) return false;

        Vector3 ndc = clip.Xyz / clip.W;
        screen = new Vector2((ndc.X * 0.5f + 0.5f) * _viewport.X,
                             (1f - (ndc.Y * 0.5f + 0.5f)) * _viewport.Y);
        depth = ndc.Z;
        return true;
    }

    private static float DistancePointSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        Vector2 ab = b - a;
        float len2 = ab.LengthSquared;
        t = 0;
        if (len2 < 1e-9f) return (p - a).Length;
        t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Мутация P3D_PICKOCCLUDE=0: точки и рёбра за гранями тела выбираются (прежнее поведение).</summary>
    public static bool OcclusionOff = Environment.GetEnvironmentVariable("P3D_PICKOCCLUDE") == "0";

    /// <summary>Пересечение луча с треугольником, алгоритм Мёллера — Трумбора.</summary>
    private bool RayTriangle(Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0;
        const float eps = 1e-7f;

        Vector3 e1 = v1 - v0, e2 = v2 - v0;
        Vector3 p = Vector3.Cross(_rayD, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < eps) return false;          // луч в плоскости треугольника

        float inv = 1f / det;
        Vector3 s = _rayO - v0;
        float u = Vector3.Dot(s, p) * inv;
        if (u < -eps || u > 1f + eps) return false;

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(_rayD, q) * inv;
        if (v < -eps || u + v > 1f + eps) return false;

        t = Vector3.Dot(e2, q) * inv;
        return t > eps;
    }
}
