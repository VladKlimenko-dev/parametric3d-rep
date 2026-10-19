using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Render.Engine;

/// <summary>
/// СБОРКА СЦЕНЫ ИЗ ДОКУМЕНТА (на процессоре): сетки тел, рёбра, вершины, опорные плоскости, оси и точки, эскизы — с
/// цветами темы и номерами выбора. Скрытое (глаз в дереве, скрытая деталь со всем содержимым, список «Опорные») в сцену
/// не попадает и не выбирается.
/// </summary>
public static class SceneBuilder
{
    public static SceneData Build(Document doc, RenderTheme th, ShowOptions show, ModelItem? activeOwner = null, Sketch? activeSketch = null)
    {
        var s = new SceneData();
        _activeSketch = activeSketch;
        try { Walk(doc.Root, Matrix4.Identity, s, th, show, activeOwner, doc.Root.Visible); }
        finally { _activeSketch = null; }
        return s;
    }

    /// <summary>Эскиз в правке: виден всегда (и при скрытых эскизах), его точки выбираются (привязка, размеры).</summary>
    [ThreadStatic] private static Sketch? _activeSketch;

    private static void Walk(ModelItem item, Matrix4 parent, SceneData s, RenderTheme th, ShowOptions show, ModelItem? active, bool visible)
    {
        Matrix4 xf = item switch
        {
            Part p => p.LocalTransform * parent,
            Assembly a => a.LocalTransform * parent,
            _ => parent
        };
        // п. 3: скрытая деталь прячет своё тело; её элементы видны или нет по своему глазу (глаз детали переключает их все,
        // но каждый можно включить отдельно)
        if (visible && item is Part part) AddPart(part, xf, s, th);

        var owner = item;
        var ownerBounds = OwnerBounds(item);
        // u13: компонент сборки (не правимый в контексте) — без своих стандартных плоскостей и начала координат
        bool component = item is Part cp && cp.Parent is Assembly pa && pa.Children.OfType<Part>().Count() > 1 && !ReferenceEquals(active, cp);
        foreach (var c in item.Children)
        {
            if (c is Part or Assembly) { Walk(c, xf, s, th, show, active, c.Visible); continue; }
            // эскиз в правке рисуется всегда — даже если в дереве он скрыт (иначе правят невидимое: видны только размеры)
            if (!c.Visible && !ReferenceEquals(c, _activeSketch)) continue;
            if (component && item is Part cpp && (ReferenceEquals(c, cpp.PlaneXY) || ReferenceEquals(c, cpp.PlaneXZ) || ReferenceEquals(c, cpp.PlaneYZ) || ReferenceEquals(c, cpp.Origin))) continue;
            switch (c)
            {
                case DatumPlane dp when show.Planes:
                    FitDatumPlane(dp, owner, ownerBounds);
                    AddPlane(dp, xf, s, th, OwnerPartId(owner));
                    break;
                case DatumPoint pt:
                    bool isOrigin = owner is Part pp ? ReferenceEquals(pp.Origin, pt) : pt.Name == "Origin";
                    if (isOrigin ? show.Origin : show.Points)
                    {
                        var w = Tr(pt.P, xf);
                        uint pick = s.AddPick(new PickRef(pt.Id, PickKind.DatumPoint, owner, pt), 3, OwnerPartId(owner));
                        if (isOrigin)
                        {
                            // u10: начало координат — тройка стрелок X/Y/Z (рисует вид поверх); здесь — точка выбора без цвета
                            var ox = Vector3.TransformVector(Vector3.UnitX, xf); var oy = Vector3.TransformVector(Vector3.UnitY, xf); var oz = Vector3.TransformVector(Vector3.UnitZ, xf);
                            s.Origins.Add((w, ox.Normalized(), oy.Normalized(), oz.Normalized(), pick));
                            s.Points.Add(new PointInst { P = w, Color = SceneData.Pack(new Vector4(th.Origin.X, th.Origin.Y, th.Origin.Z, 0)), Px = 14, Shape = 0, Pick = pick });
                        }
                        else s.Points.Add(new PointInst { P = w, Color = SceneData.Pack(th.DatumPoint), Px = 7, Shape = 1u, Pick = pick });
                    }
                    break;
                case DatumAxis ax when show.Axes:
                    {
                        var d = ax.Direction.Normalized() * ax.DisplayLength;
                        uint pick = s.AddPick(new PickRef(ax.Id, PickKind.DatumAxis, owner, ax), 2, OwnerPartId(owner));
                        s.Lines.Add(Line(Tr(ax.Origin - d, xf), Tr(ax.Origin + d, xf), th.DatumAxis, th.DatumWidth, 14, pick));
                    }
                    break;
                case Line3DItem ln when show.Lines:
                    {
                        uint pick = s.AddPick(new PickRef(ln.Id, PickKind.DatumAxis, owner, ln), 2, OwnerPartId(owner));
                        s.Lines.Add(Line(Tr(ln.A, xf), Tr(ln.B, xf), th.DatumAxis, th.DatumWidth, 0, pick));
                    }
                    break;
                case DatumCsys cs when show.Axes:
                    {
                        uint pick = s.AddPick(new PickRef(cs.Id, PickKind.DatumPoint, owner, cs), 3, OwnerPartId(owner));
                        var o = cs.Frame.Origin; double L = cs.DisplayLength;
                        s.Lines.Add(Line(Tr(o, xf), Tr(o + cs.Frame.U * L, xf), new Vector4(0.90f, 0.30f, 0.30f, 1), th.DatumWidth * 1.4f, 0, pick));
                        s.Lines.Add(Line(Tr(o, xf), Tr(o + cs.Frame.V * L, xf), new Vector4(0.30f, 0.80f, 0.35f, 1), th.DatumWidth * 1.4f, 0, pick));
                        s.Lines.Add(Line(Tr(o, xf), Tr(o + cs.Frame.W * L, xf), new Vector4(0.35f, 0.55f, 0.95f, 1), th.DatumWidth * 1.4f, 0, pick));
                        s.Points.Add(new PointInst { P = Tr(o, xf), Color = SceneData.Pack(th.DatumPoint), Px = 7, Shape = 1u, Pick = pick });
                    }
                    break;
                case DatumCurve dc when show.Lines && dc.Points.Count >= 2:
                    {
                        uint pick = s.AddPick(new PickRef(dc.Id, PickKind.DatumAxis, owner, dc), 2, OwnerPartId(owner));
                        for (int i = 1; i < dc.Points.Count; i++) s.Lines.Add(Line(Tr(dc.Points[i - 1], xf), Tr(dc.Points[i], xf), th.DatumAxis, th.DatumWidth * 1.2f, 0, pick));
                    }
                    break;
                case Sketch sk when show.Sketches || ReferenceEquals(sk, _activeSketch):
                    AddSketch(sk, xf, s, th, show, OwnerPartId(owner));
                    break;
            }
        }
    }

    private static uint OwnerPartId(ModelItem owner) => owner is Part p ? p.Id : 0;

    private static Vector3 Tr(Vector3d p, Matrix4 xf) => Vector3.TransformPosition(new Vector3((float)p.X, (float)p.Y, (float)p.Z), xf);

    private static LineInst Line(Vector3 a, Vector3 b, Vector4 c, float w, float dash, uint pick) =>
        new() { A = a, B = b, Color = SceneData.Pack(c), Width = w, Dash = dash, Pick = pick };

    // ------------------------------------------------------------------ тело

    private static void AddPart(Part part, Matrix4 xf, SceneData s, RenderTheme th)
    {
        var md = part.Mesh;
        if (md == null || md.Vertices.Length == 0) return;

        var color = th.Body;
        if (part.Material is { } mat && mat.Name != Material.Steel().Name)
        {
            var mc = mat.Color;
            color = new Vector4(mc.X, mc.Y, mc.Z, 1f);
        }

        // номер выбора грани — по id грани
        var facePick = new Dictionary<uint, uint>();
        var item = new MeshItem
        {
            Model = xf, Color = color, PartId = part.Id,
            MatColor = part.Material is { } pm ? new Vector4(pm.Color.X, pm.Color.Y, pm.Color.Z, 1f) : th.Body,
            MatSpec = part.Material?.Specular ?? 0.3f, MatShine = part.Material?.Shininess ?? 48, MatMetal = part.Material?.Metallic == true ? 1 : 0,
            Pos = new Vector3[md.Vertices.Length], Nrm = new Vector3[md.Vertices.Length], Pick = new uint[md.Vertices.Length],
            Idx = md.Indices,
        };
        for (int i = 0; i < md.Vertices.Length; i++)
        {
            var v = md.Vertices[i];
            item.Pos[i] = v.P;
            item.Nrm[i] = v.N;
            if (!facePick.TryGetValue(v.FaceId, out uint fp))
                facePick[v.FaceId] = fp = s.AddPick(new PickRef(v.FaceId, PickKind.Face, part, null), 1, part.Id);
            item.Pick[i] = fp;
        }
        s.Meshes.Add(item);

        foreach (var (edgeId, pts) in md.EdgePolylines)
        {
            uint pick = s.AddPick(new PickRef(edgeId, PickKind.Edge, part, null), 2, part.Id);
            for (int i = 1; i < pts.Length; i++)
                s.BodyEdges.Add(Line(Vector3.TransformPosition(pts[i - 1], xf), Vector3.TransformPosition(pts[i], xf), th.Edge, th.EdgeWidth, 0, pick));
        }
        foreach (var (vid, p) in md.Points)
        {
            uint pick = s.AddPick(new PickRef(vid, PickKind.Vertex, part, null), 3, part.Id);
            s.BodyVertices.Add(new PointInst { P = Vector3.TransformPosition(p, xf), Color = SceneData.Pack(th.Vertex), Px = th.VertexSize, Shape = 0, Pick = pick });
        }

        var b = md.Bounds;
        if (!b.IsEmpty)
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((float)((i & 1) == 0 ? b.Min.X : b.Max.X), (float)((i & 2) == 0 ? b.Min.Y : b.Max.Y), (float)((i & 4) == 0 ? b.Min.Z : b.Max.Z));
                var w = Vector3.TransformPosition(c, xf);
                s.Bounds.Add(new Vector3d(w.X, w.Y, w.Z));
            }
    }

    // ------------------------------------------------------------------ опорные плоскости

    private static void AddPlane(DatumPlane dp, Matrix4 xf, SceneData s, RenderTheme th, uint partId)
    {
        var c = dp.DisplayCorners();
        for (int i = 0; i < 4; i++) c[i] = Vector3.TransformPosition(c[i], xf);
        uint pick = s.AddPick(new PickRef(dp.Id, PickKind.DatumPlane, dp.Parent, dp), 1, partId);
        s.Planes.Add(new PlaneItem { C0 = c[0], C1 = c[1], C2 = c[2], C3 = c[3], Pick = pick, Fill = th.DatumPlane });
        // рамка ловится как линия: по ней плоскость выбрать проще всего
        for (int i = 0; i < 4; i++) s.Lines.Add(Line(c[i], c[(i + 1) % 4], th.DatumBorder, th.DatumWidth, 0, pick));
    }

    private static Bounds3 OwnerBounds(ModelItem owner)
    {
        var b = Bounds3.Empty;
        if (owner is Part p && p.Mesh is { } md && !md.Bounds.IsEmpty) b = md.Bounds;
        else if (owner is Assembly a) a.AccumulateBounds(ref b, Matrix4.Identity);
        return b;
    }

    /// <summary>Плоскость охватывает габарит тел владельца с запасом; ноль плоскости — всегда внутри.</summary>
    private static void FitDatumPlane(DatumPlane dp, ModelItem owner, Bounds3 b)
    {
        if (b.IsEmpty)
        {
            double d = dp.DisplaySize * (owner is Assembly ? 1.15 : 1.0);
            dp.SetExtent(new Vector2d(-d, -d), new Vector2d(d, d));
            return;
        }
        var min = Vector2d.Zero; var max = Vector2d.Zero;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
            var uv = dp.Frame.ToLocal2(corner);
            min = Vector2d.ComponentMin(min, uv); max = Vector2d.ComponentMax(max, uv);
        }
        double side = Math.Max(max.X - min.X, max.Y - min.Y);
        double pad = Math.Max(side * (owner is Assembly ? 0.2 : 0.12), dp.DisplaySize * (owner is Assembly ? 0.2 : 0.1));
        dp.SetExtent(min - new Vector2d(pad, pad), max + new Vector2d(pad, pad));
    }

    // ------------------------------------------------------------------ эскизы

    private static void AddSketch(Sketch sk, Matrix4 xf, SceneData s, RenderTheme th, ShowOptions show, uint partId)
    {
        var plane = sk.Plane;
        foreach (var (e, pts, closed) in sk.Polylines())
        {
            if (e.Role == SkRole.Centerline && !show.Centerlines) continue;
            var (col, w, dash) = e.Role switch
            {
                SkRole.Construction => (th.SketchConstruction, 1.1f, 7f),
                SkRole.Centerline => (th.SketchCenterline, 1.1f, 18f),
                SkRole.Reference => (th.SketchReference, 1.2f, 9f),
                _ => (th.SketchCurve, th.SketchWidth, 0f),
            };
            uint pick = s.AddPick(new PickRef(e.Id, PickKind.SketchCurve, sk, e), 2, partId, sk.Id);
            // весь эскиз выбирается и как целое (из дерева): номер записи эскиза — у его кривых через SketchId
            int segs = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < segs; i++)
                s.Lines.Add(Line(Tr(plane.ToWorld(pts[i]), xf), Tr(plane.ToWorld(pts[(i + 1) % pts.Count]), xf), col, w, dash, pick));
        }
        // точки: отдельные — всегда; концы и центры — у эскиза в правке
        bool active = ReferenceEquals(sk, _activeSketch);
        foreach (var e in sk.Entities)
            foreach (var q in e.Handles)
            {
                if (q.Owner != null && !active) continue;
                uint pick = s.AddPick(new PickRef(q.Id, PickKind.SketchPoint, sk, q), 3, partId, sk.Id);
                s.Points.Add(new PointInst { P = Tr(plane.ToWorld(q.P), xf), Color = SceneData.Pack(th.SketchPoint), Px = q.Owner == null ? 6f : 5f, Shape = 0, Pick = pick });
            }
    }
}
