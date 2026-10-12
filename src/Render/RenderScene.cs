using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Render;

public enum BodyViewMode
{
    Shaded = 0,
    ShadedWithEdges = 1,
    Transparent = 2,
    /// <summary>Сетка треугольников, по которой тело рисуется, и рёбра модели.</summary>
    Mesh = 3,
    /// <summary>
    /// Объёмная сетка: тетраэдры, на которых считают напряжения. Поверхностная сетка показывает
    /// лишь оболочку, а расчёт идёт по объёму, и увидеть его надо отдельно — особенно там, где
    /// сетка сгущается или, наоборот, не добирает.
    /// </summary>
    VolumeMesh = 4
}

/// <summary>Данные GPU для одной детали.</summary>
internal sealed class PartVisual : IDisposable
{
    public readonly TriBatch Faces = new();
    public readonly LineBatch Edges = new();
    public readonly PointBatch Vertices = new();
    public Matrix4 Transform = Matrix4.Identity;

    /// <summary>
    /// Рёбра треугольников для вида Mesh. Строятся только когда вид включён:
    /// в остальных видах они не нужны, а у большой детали их десятки тысяч.
    /// </summary>
    public readonly LineBatch MeshLines = new();
    public object? MeshLinesToken;

    /// <summary>
    /// Рёбра тетраэдров объёмной сетки. Строятся только когда вид включён: построение объёмной
    /// сетки дороже поверхностной, и делать его впустую нельзя.
    /// </summary>
    public readonly LineBatch VolumeLines = new();
    public object? VolumeLinesToken;

    /// <summary>
    /// Ссылка на сетку, из которой залиты буферы. Rebuild детали создаёт
    /// НОВЫЙ MeshData, поэтому сравнение по ссылке — надёжный признак того,
    /// что деталь пересчитали и буферы устарели. Флаг IsDirty для этого не
    /// годится: к моменту синхронизации он уже сброшен.
    /// </summary>
    public object? MeshToken;

    public void Dispose()
    {
        Faces.Dispose(); Edges.Dispose(); Vertices.Dispose();
        MeshLines.Dispose(); VolumeLines.Dispose();
    }
}

/// <summary>Данные GPU для одного эскиза.</summary>
internal sealed class SketchVisual : IDisposable
{
    public readonly LineBatch Curves = new();
    public readonly PointBatch Points = new();
    /// <summary>Заливка замкнутых областей эскиза.</summary>
    public readonly TriBatch Fill = new();
    /// <summary>Размеры: выносные линии и текст значений.</summary>
    public readonly LineBatch Dims = new();

    public Matrix4 Transform = Matrix4.Identity;

    public void Dispose()
    {
        Curves.Dispose(); Points.Dispose(); Fill.Dispose(); Dims.Dispose();
    }
}

/// <summary>
/// Мост между моделью и GPU.
///
/// Здесь — и только здесь — модель превращается в вершинные буферы.
/// Модель об этом классе не знает; класс перестраивает буферы детали
/// ТОЛЬКО когда деталь помечена как изменённая.
///
/// Здесь же ведётся реестр pick-id: по числу, прочитанному из кадрового
/// буфера, мгновенно находится, что именно под курсором.
/// </summary>
public sealed class RenderScene : IDisposable
{
    private readonly Dictionary<uint, PartVisual> _parts = new();
    private readonly Dictionary<uint, SketchVisual> _sketches = new();
    private readonly TriBatch _datumFaces = new();
    private readonly LineBatch _datumEdges = new();
    private readonly PointBatch _datumPoints = new();

    // ЧУЖОЙ КОНТЕКСТ. Плоскости, оси и точки начала, чей владелец — не активный
    // (в режиме сборки — детали, в режиме детали — сборка и другие детали), рисуются
    // отдельными наборами и бледнее своих.
    private readonly TriBatch _datumFacesDim = new();
    private readonly LineBatch _datumEdgesDim = new();
    private readonly PointBatch _datumPointsDim = new();

    /// <summary>
    /// Активный владелец: сборка в режиме сборки, текущая деталь в режиме детали.
    /// Базовые элементы других владельцев рисуются бледнее. Null — все как свои.
    /// </summary>
    public ModelItem? ActiveOwner { get; set; }

    private bool IsForeign(ModelItem owner) => ActiveOwner != null && !ReferenceEquals(owner, ActiveOwner);

    public BodyViewMode BodyMode = BodyViewMode.ShadedWithEdges;

    public bool ShowDatumPlanes = true;
    /// <summary>
    /// Рисовать ли вершины тела. Пока выключено: их выбор отключён
    /// (ScenePicker.PickBodyVertices), а в покое они засоряют кромки точками.
    /// Вернуться к ним — вместе с операциями, которым нужна угловая вершина.
    /// </summary>
    public bool ShowVertices = false;

    /// <summary>Эскиз, который сейчас редактируется (рисуется поверх, полной яркостью).</summary>
    public Sketch? ActiveSketch;

    /// <summary>
    /// Чьи размеры показывать. 0 — ничьи.
    ///
    /// В режиме эскиза это сам эскиз, вне его — выбранный, и только если
    /// включён показ размеров. Иначе трёхмерный вид зарастает выносными
    /// линиями всех эскизов детали.
    /// </summary>
    public uint DimensionSketchId;

    /// <summary>
    /// Рисовать ТОЛЬКО активный эскиз: ни тел, ни базовых элементов,
    /// ни чужих эскизов.
    ///
    /// В режиме черчения всё остальное только мешает: тело загораживает
    /// линии, чужие эскизы накладываются на текущий, и понять, что
    /// принадлежит чертежу, невозможно.
    /// </summary>
    public bool SketchOnly;

    /// <summary>
    /// Показывать только элементы с идентификатором МЕНЬШЕ этого.
    /// 0 — без ограничения.
    ///
    /// Нужно при выборе плоскости-базиса: эскиз не может опираться
    /// на то, что создано позже него, и показывать такое значит
    /// предлагать невозможное.
    /// </summary>
    public uint VisibilityLimit;

    private bool Allowed(uint id) => VisibilityLimit == 0 || id < VisibilityLimit;

    /// <summary>
    /// Сколько единиц модели приходится на пиксель. Размеры строятся
    /// в пикселях, поэтому при изменении масштаба их надо пересобрать.
    /// </summary>
    private double _pixelSize = 1;

    /// <summary>
    /// Сообщает текущий масштаб. Возвращает true, если он изменился
    /// настолько, что размерную графику пора перестроить.
    ///
    /// Порог в 2 % не даёт перестраивать буферы на каждое движение колеса
    /// с дробным шагом, но и не даёт цифрам заметно «поплыть».
    /// </summary>
    public bool SetPixelSize(double px)
    {
        if (px <= 0) return false;
        if (Math.Abs(px - _pixelSize) < _pixelSize * 0.02) return false;
        _pixelSize = px;
        return true;
    }

    // -------------------------------------------------------------- синхронизация

    public void Sync(Document doc)
    {
        _datumFaces.Unlit = true;
        _datumFacesDim.Unlit = true;

        SyncDatums(doc);

        var alive = new HashSet<uint>();
        foreach (var part in doc.AllParts())
        {
            alive.Add(part.Id);
            if (!_parts.TryGetValue(part.Id, out var pv))
            {
                pv = new PartVisual();
                _parts[part.Id] = pv;
                UploadPart(part, pv);
            }
            else if (!ReferenceEquals(pv.MeshToken, part.Mesh))
            {
                // Деталь пересчитана — перезаливаем её буферы.
                UploadPart(part, pv);
            }
            pv.Transform = part.LocalTransform;

            // Сетка строится ТОЛЬКО когда её смотрят.
            if (BodyMode == BodyViewMode.Mesh) EnsureMeshLines(part, pv);
            if (BodyMode == BodyViewMode.VolumeMesh) EnsureVolumeLines(part, pv);
        }

        foreach (var id in _parts.Keys.ToList())
            if (!alive.Contains(id)) { _parts[id].Dispose(); _parts.Remove(id); }

        var aliveSk = new HashSet<uint>();
        foreach (var item in doc.Root.Descendants())
        {
            if (item is not Sketch sk) continue;
            if (!sk.Visible && sk != ActiveSketch) continue;
            aliveSk.Add(sk.Id);
            if (!_sketches.TryGetValue(sk.Id, out var sv))
            {
                sv = new SketchVisual();
                _sketches[sk.Id] = sv;
            }
            UploadSketch(sk, sv);
            sv.Transform = (sk.Parent as Part)?.LocalTransform ?? Matrix4.Identity;
        }

        foreach (var id in _sketches.Keys.ToList())
            if (!aliveSk.Contains(id)) { _sketches[id].Dispose(); _sketches.Remove(id); }
    }

    /// <summary>Принудительная перезаливка одной детали (после Rebuild).</summary>
    public void Refresh(Part part)
    {
        if (!_parts.TryGetValue(part.Id, out var pv))
        {
            pv = new PartVisual();
            _parts[part.Id] = pv;
        }
        UploadPart(part, pv);
    }

    /// <summary>
    /// Рёбра треугольников детали — для вида Mesh. Признак устаревания тот же,
    /// что у граней: ссылка на сетку. Общие рёбра соседних треугольников
    /// рисуются один раз.
    /// </summary>
    private static void EnsureMeshLines(Part part, PartVisual pv)
    {
        if (ReferenceEquals(pv.MeshLinesToken, part.Mesh)) return;
        pv.MeshLinesToken = part.Mesh;

        pv.MeshLines.Begin();

        var md = part.Mesh;
        if (md != null)
        {
            var seen = new HashSet<(Vector3, Vector3)>();

            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 a = md.Vertices[md.Indices[i + k]].P;
                    Vector3 b = md.Vertices[md.Indices[i + (k + 1) % 3]].P;
                    var key = Before(a, b) ? (a, b) : (b, a);
                    if (seen.Add(key)) pv.MeshLines.AddSegment(a, b, LineStyle.MeshWire, 0);
                }
        }

        pv.MeshLines.End();
    }

    /// <summary>
    /// Рёбра объёмной сетки. Строится она заново при каждом изменении детали, и это дорого,
    /// поэтому — только для этого вида и только когда деталь поменялась.
    ///
    /// РАЗМЕР ЭЛЕМЕНТА ЗДЕСЬ КРУПНЕЕ, ЧЕМ ДЛЯ РАСЧЁТА, и это сделано нарочно. При размере,
    /// который предлагает сам построитель (двадцатая доля габарита), построение бруска 60×40×20
    /// не кончилось и за пять минут: точек выходит столько, что время растёт квадратично. Втрое
    /// крупнее — около трёх секунд на 559 тетраэдров, и это ещё терпимо для показа.
    ///
    /// Для расчёта такая сетка груба, но вид и не для расчёта: он показывает, КАК сетка
    /// ложится — где сгущается, где не добирает. Считать надо тем шагом, который задаст
    /// расчётчик.
    /// </summary>
    private static void EnsureVolumeLines(Part part, PartVisual pv)
    {
        if (ReferenceEquals(pv.VolumeLinesToken, part.Mesh)) return;
        pv.VolumeLinesToken = part.Mesh;

        pv.VolumeLines.Begin();

        foreach (var body in part.Bodies)
        {
            Kernel.Tess.TetMesh mesh;
            try
            {
                double size = Kernel.Tess.AnalysisMesher.SuggestSize(body) * 3;
                mesh = Kernel.Tess.TetMesher.Build(body, size, null);
            }
            catch { continue; }

            var seen = new HashSet<(Vector3, Vector3)>();

            foreach (var t in mesh.Tets)
            {
                var n = new[] { t.A, t.B, t.C, t.D };

                for (int i = 0; i < 4; i++)
                    for (int j = i + 1; j < 4; j++)
                    {
                        Vector3 a = new((float)mesh.Nodes[n[i]].X, (float)mesh.Nodes[n[i]].Y, (float)mesh.Nodes[n[i]].Z);
                        Vector3 b = new((float)mesh.Nodes[n[j]].X, (float)mesh.Nodes[n[j]].Y, (float)mesh.Nodes[n[j]].Z);

                        var key = Before(a, b) ? (a, b) : (b, a);
                        if (seen.Add(key)) pv.VolumeLines.AddSegment(a, b, LineStyle.MeshWire, 0);
                    }
            }
        }

        pv.VolumeLines.End();
    }

    private static bool Before(Vector3 a, Vector3 b) =>
        a.X < b.X || (a.X == b.X && (a.Y < b.Y || (a.Y == b.Y && a.Z < b.Z)));

    private void UploadPart(Part part, PartVisual pv)
    {
        pv.MeshToken = part.Mesh;

        if (part.Mesh == null)
        {
            // Гасим ВСЕ три пакета, включая грани.
            //
            // Раньше очищались только рёбра и вершины, а буфер граней
            // оставался с прежним содержимым — после удаления фичи тело
            // исчезало из дерева, но продолжало висеть на экране.
            pv.Faces.Upload(new Kernel.Tess.MeshData());
            pv.Edges.Begin(); pv.Edges.End();
            pv.Vertices.Begin(); pv.Vertices.End();
            return;
        }

        pv.Faces.Upload(part.Mesh);
        pv.Faces.BaseColor = part.Material.Color;
        pv.Faces.Specular = part.Material.Specular;
        pv.Faces.Shininess = part.Material.Shininess;

        // Те же два цвета, что у линий и точек: оранжевый — под курсором,
        // голубой — выбрано. Без явной установки грань подсвечивалась бы
        // цветом по умолчанию, почти неотличимым от материала.
        pv.Faces.HoverColor = Core.Settings.Current.HoverColor;
        pv.Faces.SelectColor = Core.Settings.Current.SelectColor;

        pv.Edges.Begin();
        foreach (var (edgeId, pts) in part.Mesh.EdgePolylines)
            pv.Edges.AddPolyline(pts, LineStyle.Edge, edgeId);
        pv.Edges.End();

        pv.Vertices.Begin();
        foreach (var (vid, p) in part.Mesh.Points)
            pv.Vertices.Add(p, PointStyle.SolidVertex, vid);
        pv.Vertices.End();
    }

    /// <summary>Цвет заливки замкнутых областей эскиза.</summary>
    public Vector4 SketchFillColor => Core.Settings.Current.SketchFillColor;

    /// <summary>
    /// Строит заливку замкнутых областей эскиза.
    ///
    /// Контуры берутся из того же ProfileBuilder, что и построение тел,
    /// поэтому залитая область — ровно то, что выдавится. Вложенные контуры
    /// становятся отверстиями и не заливаются: это сразу показывает, что
    /// профиль понят правильно, ещё до того, как нажата кнопка выдавливания.
    /// </summary>
    private void UploadSketchFill(Sketch sk, SketchVisual sv)
    {
        var md = new Kernel.Tess.MeshData();
        var mb = new Kernel.Tess.MeshBuilder();

        try
        {
            Kernel.Ops.Profile profile = sk.ToProfile();
            if (!profile.IsEmpty)
            {
                Frame3 plane = profile.Plane;

                // Области берутся у профиля: он один знает, какое отверстие
                // какому контуру принадлежит. Раньше отверстия подбирались
                // здесь по признаку «лежит внутри», и при вложенности глубже
                // двух уровней отверстие доставалось сразу нескольким
                // контурам — заливка расползалась.
                foreach (var region in profile.Regions())
                {
                    if (region.Outer.SelfIntersects) continue;

                    var outer = region.Outer.Sample(plane);
                    if (outer.Count < 3) continue;

                    var holes = new List<IReadOnlyList<Vector2d>>();
                    foreach (var h in region.Holes)
                    {
                        if (h.SelfIntersects) continue;
                        var pts = h.Sample(plane);
                        if (pts.Count >= 3) holes.Add(pts);
                    }

                    var tris = Kernel.Tess.Triangulator.Triangulate(outer, holes, out var verts);
                    if (tris.Count == 0) continue;

                    mb.BeginFace(sk.Id);
                    int baseIdx = mb.VertexCount;
                    foreach (var uv in verts) mb.AddVertex(plane.ToWorld(uv), plane.W);
                    for (int t = 0; t < tris.Count; t += 3)
                        mb.AddTriangle(baseIdx + tris[t], baseIdx + tris[t + 1], baseIdx + tris[t + 2]);
                    mb.EndFace();
                }
            }
        }
        catch
        {
            // Недостроенный контур не должен ронять отрисовку эскиза.
        }

        mb.FillInto(md);
        sv.Fill.Upload(md);
        sv.Fill.Unlit = true;
        sv.Fill.BaseColor = SketchFillColor;
        sv.Fill.HoverColor = SketchFillColor;
        sv.Fill.SelectColor = SketchFillColor;
    }

    private void UploadSketch(Sketch sk, SketchVisual sv)
    {
        var plane = sk.Plane;
        bool active = sk == ActiveSketch;

        UploadSketchFill(sk, sv);

        // Размеры и значки ограничений идут в один пакет: и то и другое —
        // чертёжная разметка, зависящая от масштаба, и перестраивается
        // она вместе.
        sv.Dims.Begin();
        DimensionRenderer.Build(sk, sv.Dims, _pixelSize);
        if (active) ConstraintRenderer.Build(sk, sv.Dims, _pixelSize);
        sv.Dims.End();

        sv.Curves.Begin();
        var buf = new List<Vector3>();

        foreach (var (entity, pts2, closed) in sk.Polylines())
        {
            buf.Clear();
            foreach (var p in pts2)
            {
                Vector3d w = plane.ToWorld(p);
                buf.Add(new Vector3((float)w.X, (float)w.Y, (float)w.Z));
            }
            // Ошибочные элементы рисуются красным поверх своей роли:
            // по такому контуру тело не построится, и знать об этом надо
            // сразу, а не при нажатии кнопки выдавливания.
            LineStyle style = entity.HasError ? LineStyle.SketchError : StyleOf(entity.StyleHint);
            sv.Curves.AddPolyline(buf, style, entity.Id, closed);
        }
        sv.Curves.End();

        sv.Points.Begin();
        if (active)
        {
            foreach (var e in sk.Entities)
            {
                if (e is SkPoint free && free.Owner == null)
                {
                    AddSketchPoint(sv, sk, plane, free,
                                   free.Dangling ? PointStyle.Error : PointStyle.SketchPoint);
                    continue;
                }
                foreach (var h in e.Handles)
                {
                    PointStyle ps = e is SkCircle || e is SkArc || e is SkEllipse
                        ? (ReferenceEquals(h, CenterOf(e)) ? PointStyle.SketchCenter : PointStyle.SketchEndpoint)
                        : PointStyle.SketchEndpoint;

                    // Свободный конец — разрыв контура.
                    if (h.Dangling) ps = PointStyle.Error;
                    AddSketchPoint(sv, sk, plane, h, ps);
                }
            }
        }
        sv.Points.End();
    }

    /// <summary>
    /// Полуразмер базовой плоскости.
    ///
    /// Пока в детали или сборке нет геометрии, плоскость показывается
    /// размером по умолчанию. Как только тела появились, она растягивается
    /// так, чтобы охватить их: берётся габарит владельца, его углы
    /// проецируются на плоскость, и от начала координат плоскости берётся
    /// наибольший отступ. Именно проекция, а не диагональ габарита: у
    /// смещённой плоскости центр не совпадает с центром тела, и по диагонали
    /// она накрывала бы деталь не полностью.
    /// </summary>
    /// <summary>
    /// Подбирает прямоугольник базовой плоскости под содержимое владельца.
    ///
    /// Габарит тел проецируется на плоскость, и берётся ОХВАТЫВАЮЩИЙ
    /// ПРЯМОУГОЛЬНИК плюс небольшой запас, чтобы плоскость слегка выступала
    /// за геометрию.
    ///
    /// Прямоугольник, а не симметричный квадрат. Тело обычно лежит в стороне
    /// от нуля: выдавленный из начала координат кубоид целиком в положительной
    /// четверти. Квадрату, симметричному относительно нуля, приходилось
    /// тянуться до дальнего края И на столько же в обратную сторону, отчего
    /// плоскость выходила вдвое больше тела.
    ///
    /// Ноль плоскости в прямоугольник включается всегда: базовая плоскость
    /// показывает и начало координат, и без него она уехала бы от него
    /// в сторону тела.
    /// </summary>
    private static void FitDatumPlane(DatumPlane dp, ModelItem owner)
    {
        Bounds3 b = OwnerBounds(owner);

        if (b.IsEmpty)
        {
            double d = dp.DisplaySize * (owner is Assembly ? 1.15 : 1.0);
            dp.SetExtent(new Vector2d(-d, -d), new Vector2d(d, d));
            return;
        }

        var min = new Vector2d(0, 0);
        var max = new Vector2d(0, 0);

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(
                (i & 1) == 0 ? b.Min.X : b.Max.X,
                (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                (i & 4) == 0 ? b.Min.Z : b.Max.Z);

            Vector2d uv = dp.Frame.ToLocal2(corner);
            min = Vector2d.ComponentMin(min, uv);
            max = Vector2d.ComponentMax(max, uv);
        }

        // Запас считается от большей стороны, а не от каждой по отдельности:
        // у плоской и вытянутой заготовки запас должен быть одинаковым
        // с обеих сторон, иначе плоскость выглядит перекошенной.
        double side = Math.Max(max.X - min.X, max.Y - min.Y);
        // Плоскости сборки — с запасом больше, чем у деталей: при одной детали в сборке
        // рамки иначе совпадали, и плоскости сборки было не отличить от плоскостей детали.
        double margin = owner is Assembly ? AssemblyPlaneMargin : DatumPlaneMargin;
        double pad = Math.Max(side * margin, dp.DisplaySize * (owner is Assembly ? 0.2 : 0.1));

        dp.SetExtent(min - new Vector2d(pad, pad), max + new Vector2d(pad, pad));
    }

    /// <summary>Насколько плоскость выступает за габарит тел, долей его размера.</summary>
    private const double DatumPlaneMargin = 0.12;

    /// <summary>То же для плоскостей сборки — заметно больше, чем у деталей.</summary>
    private const double AssemblyPlaneMargin = 0.20;

    /// <summary>Наибольший отступ геометрии владельца от его начала координат.</summary>
    private static double OwnerReach(ModelItem owner)
    {
        Bounds3 b = OwnerBounds(owner);
        if (b.IsEmpty) return 0;
        return Math.Max(
            Math.Max(Math.Abs(b.Min.X), Math.Abs(b.Max.X)),
            Math.Max(Math.Max(Math.Abs(b.Min.Y), Math.Abs(b.Max.Y)),
                     Math.Max(Math.Abs(b.Min.Z), Math.Abs(b.Max.Z))));
    }

    /// <summary>Габарит содержимого детали или сборки в её собственных координатах.</summary>
    private static Bounds3 OwnerBounds(ModelItem owner)
    {
        switch (owner)
        {
            case Part p:
                return p.Bounds;

            case Assembly a:
                {
                    var b = Bounds3.Empty;
                    foreach (var item in a.Descendants())
                        if (item is Part part && part.Visible && !part.Bounds.IsEmpty)
                        {
                            // Габарит детали переносится в координаты сборки.
                            Matrix4 m = part.LocalTransform;
                            Vector3 lo = part.Bounds.Min.ToF(), hi = part.Bounds.Max.ToF();
                            for (int i = 0; i < 8; i++)
                            {
                                var corner = new Vector3(
                                    (i & 1) == 0 ? lo.X : hi.X,
                                    (i & 2) == 0 ? lo.Y : hi.Y,
                                    (i & 4) == 0 ? lo.Z : hi.Z);
                                Vector3 w = Vector3.TransformPosition(corner, m);
                                b.Add(new Vector3d(w.X, w.Y, w.Z));
                            }
                        }
                    return b;
                }

            default:
                return Bounds3.Empty;
        }
    }

    private static SkPoint? CenterOf(SkEntity e) => e switch
    {
        SkCircle c => c.Center,
        SkArc a => a.Center,
        SkEllipse el => el.Center,
        _ => null
    };

    private void AddSketchPoint(SketchVisual sv, Sketch sk, Frame3 plane, SkPoint p, PointStyle style)
    {
        Vector3d w = plane.ToWorld(p.P);
        sv.Points.Add(new Vector3((float)w.X, (float)w.Y, (float)w.Z), style, p.Id);
    }

    private static LineStyle StyleOf(LineStyleHint h) => h switch
    {
        LineStyleHint.Construction => LineStyle.SketchConstruction,
        LineStyleHint.Centerline => LineStyle.SketchCenterline,
        LineStyleHint.Reference => LineStyle.SketchReference,
        LineStyleHint.Dimension => LineStyle.Dimension,
        _ => LineStyle.SketchObject
    };

    private void SyncDatums(Document doc)
    {
        _datumEdges.Begin();
        _datumPoints.Begin();
        _datumEdgesDim.Begin();
        _datumPointsDim.Begin();

        var mbOwn = new Kernel.Tess.MeshBuilder();
        var mbDim = new Kernel.Tess.MeshBuilder();

        void AddPlane(DatumPlane dp, Matrix4 xf, ModelItem owner)
        {
            bool dim = IsForeign(owner);
            var mb = dim ? mbDim : mbOwn;
            // Размер запоминается на самой плоскости: им же пользуется
            // процедура выбора, иначе мышь ловила бы прямоугольник
            // не того размера, что нарисован.
            FitDatumPlane(dp, owner);

            Vector3[] c = dp.DisplayCorners();
            for (int i = 0; i < 4; i++) c[i] = Vector3.TransformPosition(c[i], xf);

            mb.BeginFace(dp.Id);
            Vector3 n = Vector3.Cross(c[1] - c[0], c[3] - c[0]).Normalized();
            int b = mb.VertexCount;
            for (int i = 0; i < 4; i++)
                mb.AddVertex(new Vector3d(c[i].X, c[i].Y, c[i].Z), new Vector3d(n.X, n.Y, n.Z));
            mb.AddQuad(b, b + 1, b + 2, b + 3);
            mb.EndFace();

            if (dim) _datumEdgesDim.AddPolyline(c, LineStyle.DatumDim, dp.Id, closed: true);
            else _datumEdges.AddPolyline(c, LineStyle.DatumBorder, dp.Id, closed: true);
        }

        void Walk(ModelItem item, Matrix4 xf)
        {
            Matrix4 local = item switch
            {
                Part p => p.LocalTransform * xf,
                Assembly a => a.LocalTransform * xf,
                _ => xf
            };

            foreach (var c in item.Children)
            {
                switch (c)
                {
                    case DatumPlane dp when dp.Visible && ShowDatumPlanes:
                        AddPlane(dp, local, item);
                        break;
                    case DatumPoint dpt when dpt.Visible:
                        {
                            Vector3 p = Vector3.TransformPosition(
                                new Vector3((float)dpt.P.X, (float)dpt.P.Y, (float)dpt.P.Z), local);
                            if (IsForeign(item)) _datumPointsDim.Add(p, PointStyle.OriginDim, dpt.Id);
                            else _datumPoints.Add(p, PointStyle.Origin, dpt.Id);
                            break;
                        }
                    case Line3DItem ln when ln.Visible:
                        {
                            Vector3 a = Vector3.TransformPosition(
                                new Vector3((float)ln.A.X, (float)ln.A.Y, (float)ln.A.Z), local);
                            Vector3 b = Vector3.TransformPosition(
                                new Vector3((float)ln.B.X, (float)ln.B.Y, (float)ln.B.Z), local);
                            if (IsForeign(item)) _datumEdgesDim.AddPolyline(new[] { a, b }, LineStyle.DatumDim, ln.Id);
                            else _datumEdges.AddPolyline(new[] { a, b }, LineStyle.DatumAxis, ln.Id);
                            break;
                        }

                    case DatumAxis ax when ax.Visible:
                        {
                            Vector3 o = Vector3.TransformPosition(
                                new Vector3((float)ax.Origin.X, (float)ax.Origin.Y, (float)ax.Origin.Z), local);
                            Vector3 d = Vector3.TransformNormal(
                                new Vector3((float)ax.Direction.X, (float)ax.Direction.Y, (float)ax.Direction.Z), local);
                            float L = (float)Math.Max(ax.DisplayLength, OwnerReach(item) * 1.15);
                            if (IsForeign(item)) _datumEdgesDim.AddPolyline(new[] { o - d * L, o + d * L }, LineStyle.DatumDim, ax.Id);
                            else _datumEdges.AddPolyline(new[] { o - d * L, o + d * L }, LineStyle.DatumAxis, ax.Id);
                            break;
                        }
                }
                Walk(c, local);
            }
        }

        Walk(doc.Root, Matrix4.Identity);

        var md = new Kernel.Tess.MeshData();
        mbOwn.FillInto(md);
        _datumFaces.Upload(md);
        _datumFaces.BaseColor = new Vector4(0.45f, 0.58f, 0.80f, 0.14f);
        _datumFaces.HoverColor = new Vector4(0.55f, 0.72f, 0.95f, 0.28f);
        _datumFaces.SelectColor = new Vector4(0.30f, 0.78f, 0.98f, 0.36f);
        _datumFaces.Unlit = true;

        // Чужой контекст — та же заливка, заметно прозрачнее; подсветка при наведении
        // и выборе остаётся различимой.
        var mdDim = new Kernel.Tess.MeshData();
        mbDim.FillInto(mdDim);
        _datumFacesDim.Upload(mdDim);
        _datumFacesDim.BaseColor = new Vector4(0.45f, 0.58f, 0.80f, 0.05f);
        _datumFacesDim.HoverColor = new Vector4(0.55f, 0.72f, 0.95f, 0.18f);
        _datumFacesDim.SelectColor = new Vector4(0.30f, 0.78f, 0.98f, 0.26f);
        _datumFacesDim.Unlit = true;

        _datumEdges.End();
        _datumPoints.End();
        _datumEdgesDim.End();
        _datumPointsDim.End();
    }

    // -------------------------------------------------------------- подсветка

    /// <summary>
    /// Ставит флаги подсветки на объект во ВСЕХ пакетах, где он встречается.
    ///
    /// Ранняя остановка на первом попадании была ошибкой: базовая плоскость
    /// живёт сразу в двух пакетах — заливка в _datumFaces и рамка
    /// в _datumEdges под тем же id. Выход после заливки означал, что рамка
    /// плоскости под курсором не подсвечивалась вовсе.
    /// </summary>
    /// <returns>Нашёлся ли объект хоть где-нибудь.</returns>
    public bool SetFlags(uint id, uint flags)
    {
        if (flags != 0) _applied.Add((AppliedKind.Element, id));
        bool found = false;

        foreach (var pv in _parts.Values)
        {
            found |= pv.Faces.SetFaceFlags(id, flags);
            found |= pv.Edges.SetFlags(id, flags);
            found |= pv.Vertices.SetFlags(id, flags);
        }
        foreach (var sv in _sketches.Values)
        {
            found |= sv.Curves.SetFlags(id, flags);
            found |= sv.Points.SetFlags(id, flags);

            // Размеры и значки ограничений живут в пакете Dims. Раньше его
            // здесь не было, поэтому подсветка до них просто не доходила:
            // выбор мышью срабатывал, а загораться было нечему.
            found |= sv.Dims.SetFlags(id, flags);
        }

        found |= _datumFaces.SetFaceFlags(id, flags);
        found |= _datumEdges.SetFlags(id, flags);
        found |= _datumPoints.SetFlags(id, flags);
        found |= _datumFacesDim.SetFaceFlags(id, flags);
        found |= _datumEdgesDim.SetFlags(id, flags);
        found |= _datumPointsDim.SetFlags(id, flags);
        return found;
    }

    /// <summary>
    /// Подсветка детали целиком — режим сборки, где выбирается не грань,
    /// а деталь. Флаги ставятся сразу всем граням, рёбрам и вершинам,
    /// иначе подсветка «детали» не находила бы ни одного диапазона:
    /// батчи адресуются id топологических элементов, а не id детали.
    /// </summary>
    public void SetPartFlags(uint partId, uint flags)
    {
        if (flags != 0) _applied.Add((AppliedKind.Part, partId));
        if (!_parts.TryGetValue(partId, out var pv)) return;
        pv.Faces.SetAllFlags(flags);
        pv.Edges.SetAllFlags(flags);
        pv.Vertices.SetAllFlags(flags);
    }

    /// <summary>Подсветка всего эскиза целиком — при наведении на него в дереве.</summary>
    public void SetSketchFlags(uint sketchId, uint flags)
    {
        if (flags != 0) _applied.Add((AppliedKind.Sketch, sketchId));
        if (!_sketches.TryGetValue(sketchId, out var sv)) return;
        sv.Curves.SetAllFlags(flags);
        sv.Points.SetAllFlags(flags);
        sv.Dims.SetAllFlags(flags);
    }

    private enum AppliedKind { Element, Part, Sketch }
    private readonly List<(AppliedKind Kind, uint Id)> _applied = new();

    /// <summary>
    /// Снимает подсветку.
    ///
    /// Сбрасываются только те объекты, которым флаги реально ставили.
    /// Полный обход всех пакетов означал бы перезаливку каждого буфера
    /// на КАЖДОЕ движение мыши: подсветка вызывается из обработчика
    /// наведения, а не раз в кадр.
    /// </summary>
    public void ClearFlags()
    {
        if (_applied.Count == 0) return;

        var list = _applied.ToArray();
        _applied.Clear();

        foreach (var (kind, id) in list)
        {
            switch (kind)
            {
                case AppliedKind.Part: SetPartFlags(id, 0); break;
                case AppliedKind.Sketch: SetSketchFlags(id, 0); break;
                default: SetFlags(id, 0); break;
            }
        }
        _applied.Clear();
    }

    /// <summary>Полный сброс флагов во всех пакетах — после пересборки сцены.</summary>
    public void ResetAllFlags()
    {
        _applied.Clear();
        foreach (var pv in _parts.Values)
        {
            pv.Faces.ClearAllFlags();
            pv.Edges.ClearAllFlags();
            pv.Vertices.ClearAllFlags();
        }
        foreach (var sv in _sketches.Values)
        {
            sv.Curves.ClearAllFlags();
            sv.Points.ClearAllFlags();
            sv.Dims.ClearAllFlags();
        }
        _datumFaces.ClearAllFlags();
        _datumEdges.ClearAllFlags();
        _datumPoints.ClearAllFlags();
        _datumFacesDim.ClearAllFlags();
        _datumEdgesDim.ClearAllFlags();
        _datumPointsDim.ClearAllFlags();
    }

    // -------------------------------------------------------------- отрисовка

    public void Draw(RenderContext ctx)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        bool volume = BodyMode == BodyViewMode.VolumeMesh;
        bool showFaces = BodyMode != BodyViewMode.Mesh && !volume;
        bool transparent = BodyMode == BodyViewMode.Transparent;
        bool showEdges = BodyMode != BodyViewMode.Shaded;
        bool mesh = BodyMode == BodyViewMode.Mesh || volume;

        // 1. Базовые плоскости и оси (полупрозрачные, без записи глубины).
        if (ShowDatumPlanes && !SketchOnly && (!_datumFaces.IsEmpty || !_datumFacesDim.IsEmpty))
        {
            ctx.Model = Matrix4.Identity;
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            if (!_datumFacesDim.IsEmpty) _datumFacesDim.Draw(ctx);   // чужие — первыми, под своими
            if (!_datumFaces.IsEmpty) _datumFaces.Draw(ctx);
            GL.DepthMask(true);
        }
        if (!SketchOnly)
        {
            ctx.Model = Matrix4.Identity;
            _datumEdgesDim.Draw(ctx);
            _datumPointsDim.Draw(ctx);
            _datumEdges.Draw(ctx);
            _datumPoints.Draw(ctx);
        }

        // 2. Тела.
        if (showFaces && !SketchOnly)
        {
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);

            // Грани отодвигаются на шаг глубины НАЗАД, чтобы рёбра, лежащие
            // ровно на них, всегда оказывались впереди.
            //
            // Раньше вместо этого рёбра сдвигались к камере на постоянную
            // величину в координатах отсечения. Но связь между этой величиной
            // и расстоянием в мире нелинейна: у перспективы одно и то же
            // смещение вблизи почти не действует, а вдали перекрывает толщину
            // тела — рёбра просвечивали насквозь. glPolygonOffset работает
            // в единицах самого буфера глубины и от масштаба не зависит.
            GL.Enable(EnableCap.PolygonOffsetFill);

            // Сдвиг — с запасом на допуск хорды.
            //
            // ИЗМЕНЕНО с (1, 1). Кромка дуги рисуется по настоящей окружности,
            // а грань — хордами, и на вогнутой поверхности окружность лежит
            // ЗА хордой до Tol.Chord = 0.02 мм. Единица глубины при обычном
            // взгляде на деталь — около 0.009 мм, и сдвига в одну единицу не
            // хватало: куски передней кромки проваливались за грань — в виде
            // с рёбрами пропадали, в прозрачном рисовались штрихом. Восемь
            // единиц — около 0.07 мм: хорду покрывают, а стенок такой толщины
            // у деталей не бывает.
            GL.PolygonOffset(1.5f, 8.0f);

            float saved = ctx.Opacity;
            if (transparent) { ctx.Opacity = Core.Settings.Current.TransparentOpacity; GL.DepthMask(false); }

            foreach (var kv in _parts)
            {
                if (!Allowed(kv.Key)) continue;
                ctx.Model = kv.Value.Transform;
                kv.Value.Faces.Draw(ctx);
            }

            if (transparent) { GL.DepthMask(true); ctx.Opacity = saved; }

            // ГЛУБИНА ГРАНЕЙ ДЛЯ ЗАДНИХ ЛИНИЙ.
            //
            // ИСПРАВЛЕНО. Прозрачные грани рисуются без записи глубины, чтобы
            // просвечивали друг сквозь друга. Но тогда в буфере глубины нет
            // ничего: проход задних рёбер («дальше, чем грань») не находил
            // граней и не рисовал ни одного штриха, а обычный проход рисовал
            // ВСЕ рёбра сплошными — заслонять их было нечему. Здесь глубина
            // ближних граней пишется отдельно, без цвета: вид остаётся прозрачным,
            // а рёбрам есть с чем сравниться.
            if (transparent)
            {
                GL.ColorMask(false, false, false, false);
                foreach (var kv in _parts)
                {
                    if (!Allowed(kv.Key)) continue;
                    ctx.Model = kv.Value.Transform;
                    kv.Value.Faces.Draw(ctx);
                }
                GL.ColorMask(true, true, true, true);
            }

            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.Disable(EnableCap.CullFace);
        }

        // 2б. Сетка треугольников — в виде Mesh вместо граней.
        //
        // ПОДЛОЖКОЙ: БЕЗ ПРОВЕРКИ И ЗАПИСИ ГЛУБИНЫ.
        //
        // ИСПРАВЛЕНО. Стороны треугольников идут и вдоль рёбер модели, и глубину
        // каждая линия считает по своим концам: ребро — один длинный отрезок,
        // сетка вдоль него — несколько коротких. Глубины равны до округления,
        // и разность меняла знак посередине линии: половину ребра рисовало
        // само ребро, половину закрывала серая сетка — линии выходили
        // наполовину серыми, выделенное ребро наполовину тёмным. Граней в этом
        // виде нет, прятать сетке нечего; рёбра модели рисуются поверх неё.
        if (mesh && !SketchOnly)
        {
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);

            foreach (var kv in _parts)
            {
                if (!Allowed(kv.Key)) continue;
                ctx.Model = kv.Value.Transform;
                if (volume) kv.Value.VolumeLines.Draw(ctx);
                else kv.Value.MeshLines.Draw(ctx);
            }

            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
        }

        // 3. Рёбра. В прозрачном режиме невидимые рёбра рисуются штрихом.
        if ((showEdges || transparent) && !SketchOnly)
        {
            if (transparent)
            {
                // Штрих задних рёбер НЕ ПИШЕТ ГЛУБИНУ.
                //
                // ИСПРАВЛЕНО. Прежде писал: в буфере оказывалась глубина самой
                // линии, и следующий проход («ближе или равно») проходил по тем же
                // пикселям при равной глубине и перекрашивал штрихи сплошным
                // цветом рёбер. Задние линии выглядели наполовину штриховыми.
                GL.DepthMask(false);
                GL.DepthFunc(DepthFunction.Greater);
                foreach (var kv in _parts)
                {
                    if (!Allowed(kv.Key)) continue;
                    ctx.Model = kv.Value.Transform;
                    kv.Value.Edges.Draw(ctx, (int)LineStyle.EdgeHidden);
                }
                GL.DepthFunc(DepthFunction.Lequal);
                GL.DepthMask(true);
            }

            foreach (var kv in _parts)
            {
                if (!Allowed(kv.Key)) continue;
                ctx.Model = kv.Value.Transform;
                kv.Value.Edges.Draw(ctx);

                // Вершины. В «Shaded + Edges» — только под курсором и выделенные:
                // в покое они засоряют кромки точками, но выбирать их можно,
                // и подсвеченная вершина должна быть видна.
                if (ShowVertices)
                {
                    kv.Value.Vertices.OnlyFlagged = BodyMode == BodyViewMode.ShadedWithEdges;
                    kv.Value.Vertices.Draw(ctx);
                }
            }
        }

        // 4. Эскизы поверх геометрии.
        foreach (var kv in _sketches)
        {
            // В режиме черчения показывается только активный эскиз.
            if (SketchOnly && ActiveSketch != null && kv.Key != ActiveSketch.Id) continue;

            // При выборе плоскости-базиса — только созданное раньше эскиза.
            if (!Allowed(kv.Key) && (ActiveSketch == null || kv.Key != ActiveSketch.Id)) continue;

            var sv = kv.Value;
            ctx.Model = sv.Transform;
            float saved = ctx.Opacity;
            if (ActiveSketch != null && ActiveSketch.Id != kv.Key) ctx.Opacity = 0.35f;

            // Заливка идёт первой и без записи глубины: она подложка,
            // а не поверхность, и не должна перекрывать ни линии эскиза,
            // ни геометрию за ним.
            if (!sv.Fill.IsEmpty)
            {
                GL.DepthMask(false);
                GL.Disable(EnableCap.CullFace);
                sv.Fill.Draw(ctx);
                GL.DepthMask(true);
            }

            sv.Curves.Draw(ctx);
            sv.Points.Draw(ctx);
            if (kv.Key == DimensionSketchId) sv.Dims.Draw(ctx);
            ctx.Opacity = saved;
        }

        ctx.Model = Matrix4.Identity;
    }

    public void Dispose()
    {
        foreach (var p in _parts.Values) p.Dispose();
        foreach (var s in _sketches.Values) s.Dispose();
        _parts.Clear(); _sketches.Clear();
        _datumFaces.Dispose(); _datumEdges.Dispose(); _datumPoints.Dispose();
        _datumFacesDim.Dispose(); _datumEdgesDim.Dispose(); _datumPointsDim.Dispose();
    }
}
