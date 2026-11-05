using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// Вид чертежа. Name — уникальное имя (front, top, left, right, bottom, back, iso; у разреза и выносного элемента — «А–А», «Б»).
/// Dir — направление взгляда (пусто — по имени). Разрез: Parent — вид, на котором стоит линия разреза, CutDepth — глубина
/// секущей плоскости по взгляду разреза (мм модели), Label — буква. Выносной элемент: Parent, центр и радиус круга на виде
/// Parent (мм модели), свой масштаб.
/// </summary>
public sealed class DrawingView
{
    public string Name { get; set; } = "front";
    public string Dir { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Масштаб вида; 0 — масштаб листа.</summary>
    public double Scale { get; set; }
    public bool ShowHidden { get; set; } = true;
    public bool ShowTangent { get; set; }
    /// <summary>Осевые линии у очерков тел вращения (оси отверстий и валов).</summary>
    public bool CenterLines { get; set; } = true;
    public bool IsSection { get; set; }
    public bool IsDetail { get; set; }
    public string? Parent { get; set; }
    public string Label { get; set; } = "";
    public double CutDepth { get; set; }
    public double DetailX { get; set; }
    public double DetailY { get; set; }
    public double DetailR { get; set; }
    /// <summary>
    /// k158: вспомогательный вид ГОСТ 2.305 п. 6 (взгляд перпендикулярно наклонной линии вида-родителя): Dir — своя рамка
    /// «v:…» (ViewFrame.Encode), на виде-родителе — стрелка с буквой Label, над видом — буква.
    /// </summary>
    public bool IsAux { get; set; }
    /// <summary>k158: вид с разрывом ГОСТ 2.305 п. 9.3: полоса [BreakA, BreakB] по X вида (мм модели) не показывается, правая часть
    /// придвигается (промежуток BreakGap мм листа), на краях — тонкие волнистые линии обрыва. Размеры — по модели (полная длина).</summary>
    public double BreakA { get; set; }
    public double BreakB { get; set; }
    public double BreakGap { get; set; } = 8;
    /// <summary>k159: поворот вида на листе (градусы, против часовой) вокруг центра вида.</summary>
    public double Angle { get; set; }
    /// <summary>
    /// k161: вид отображения (п. 9): "" — линии (невидимые — по ShowHidden), "hidden" — с невидимыми штриховыми, "wire" — каркас
    /// (все рёбра сплошные), "shaded" — тона без линий, "shadedlines" — тона и видимые линии, "material" — цвет материала и
    /// видимые линии.
    /// </summary>
    public string Style { get; set; } = "";
    [JsonIgnore] public bool HasBreak => BreakB > BreakA;
}

/// <summary>
/// k156 (D3): РАЗМЕР ЧЕРТЕЖА (ГОСТ 2.307). Kind: h — горизонтальный, v — вертикальный, al — параллельный отрезку, dia —
/// диаметр, rad — радиус. Линейный: точки (X1, Y1), (X2, Y2) в координатах вида (мм модели), Offset — отступ размерной линии от
/// дальней точки (мм листа, знак — сторона). Диаметр и радиус: центр (X1, Y1), радиус X2, угол полки Y2 (градусы).
/// Значение — по геометрии модели (не листа). Auto — из «Автоматических размеров», Model — из эскизов («Размеры модели»).
/// </summary>
public sealed class DrawingDim
{
    public string View { get; set; } = "front";
    public string Kind { get; set; } = "h";
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Offset { get; set; } = 10;
    /// <summary>Текст вместо значения (пусто — значение).</summary>
    public string Text { get; set; } = "";
    /// <summary>Допуск или посадка: «±0,1», «H7», «+0,2/-0,1» (прежние файлы; k167 — TolUp, TolLo).</summary>
    public string Tol { get; set; } = "";
    /// <summary>
    /// k167 (замечание 05.11, 16:17, п. 1): верхнее и нижнее отклонения (числа в единицах чертежа; пусто — нет). Разные — одно
    /// над другим мелким шрифтом («+» сверху, «−» снизу); одно — на своём месте; равные по величине («+a», «−a») — «±a» тем же
    /// шрифтом через пробел. Нижнее без знака — со знаком «−».
    /// </summary>
    public string TolUp { get; set; } = "";
    public string TolLo { get; set; } = "";
    /// <summary>Число одинаковых элементов («4 отв.»).</summary>
    public int Count { get; set; } = 1;
    public bool Auto { get; set; }
    public bool Model { get; set; }
    public bool Hidden { get; set; }
    /// <summary>k159: отступ размерной линии — от габарита вида (а не от точек): габаритные размеры и цепочки за контуром.</summary>
    public bool FromBox { get; set; }
    /// <summary>k159: место выбирается автоматически (ряды за контуром, угол выноски без наложения текстов); перенос мышью — false.</summary>
    public bool Arrange { get; set; }
    /// <summary>k161: сдвиг текста вдоль размерной линии от середины (мм листа); за концом — размерная линия продлевается под текст.</summary>
    public double TextShift { get; set; }
    /// <summary>k161: стрелки: 0 — по месту (снаружи, если не помещаются), 1 — внутри, 2 — снаружи. У диаметра большой
    /// окружности: 2 — выноска с полкой вместо линии через центр.</summary>
    public int Arrows { get; set; }
    /// <summary>k163: назначение автоматического размера: overall — габарит, circle — диаметр окружности, centre — положение
    /// центра, step — ступень вдоль вида; пусто — прочие.</summary>
    public string Role { get; set; } = "";

    [JsonIgnore] public double Value => Kind switch
    {
        "h" => Math.Abs(X2 - X1),
        "v" => Math.Abs(Y2 - Y1),
        "dia" => 2 * X2,
        "rad" => X2,
        "ang" => ((Y2 - X2) % 360 + 360) % 360,   // k163: угол — от направления X2 до Y2 против часовой (градусы вида)
        _ => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)),
    };

    /// <summary>Подпись: unit — мм в единице длины чертежа (1 — мм, 25.4 — дюйм), fmt — формат числа.</summary>
    public string Caption(bool comma, double unit = 1, string fmt = "0.##")
    {
        if (Text.Length > 0) return Text;
        string v = (Kind == "ang" ? Value : Value / unit).ToString(Kind == "ang" ? "0.##" : fmt, CultureInfo.InvariantCulture) + (Kind == "ang" ? "°" : "");
        if (comma) v = v.Replace('.', ',');
        string pre = Kind == "dia" ? DrawingDocument.Dia : Kind == "rad" ? "R" : "";
        string cnt = Count > 1 ? $"{Count} отв. " : "";
        return cnt + pre + v + Tol;
    }
}

/// <summary>
/// k156 (D4): ОБОЗНАЧЕНИЕ ЧЕРТЕЖА. Kind: text — текст (технические требования), rough — шероховатость ГОСТ 2.309 (Value —
/// «Ra 3,2»; без цели — неуказанная шероховатость в правом верхнем углу «… (√)»), gdt — рамка допуска ГОСТ 2.308 / ISO 1101
/// (Symbol, Value, Datums), datum — знак базы (Text — буква), balloon — позиция ГОСТ 2.109 (Position), holes — таблица отверстий.
/// X, Y — место на листе (мм); цель (TX, TY) — точка на виде View (мм модели), к ней — выноска.
/// </summary>
/// <summary>
/// k165: подпись центра «℄N»: центр (вид, координаты вида — мм модели), место выноски — смещение конца выноски от центра (мм
/// листа); Placed — место подобрано или задано мышью.
/// </summary>
public sealed class CentreMark
{
    public string View { get; set; } = "";
    public double CX { get; set; }
    public double CY { get; set; }
    public double DX { get; set; }
    public double DY { get; set; }
    public bool Placed { get; set; }
}

public sealed class DrawingNote
{
    public string Kind { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public string? View { get; set; }
    public bool HasTarget { get; set; }
    public double TX { get; set; }
    public double TY { get; set; }
    public string Text { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Value { get; set; } = "";
    public string Datums { get; set; } = "";
    public int Position { get; set; }
    public double Height { get; set; } = 3.5;
    public bool Auto { get; set; }
}

/// <summary>Проекции видов чертежа: линии, оси, дуги, фигуры сечения (координаты вида, мм модели) и номера деталей тел.</summary>
public sealed class DrawingScene
{
    public readonly Dictionary<string, List<ViewLine>> Lines = new();
    public readonly Dictionary<string, List<ViewAxis>> Axes = new();
    public readonly Dictionary<string, List<ViewArc>> Arcs = new();
    public readonly Dictionary<string, List<(Vector2d A, Vector2d B)>> SectionBoundary = new();
    /// <summary>Имя детали (позиции) каждого тела; пусто — одна деталь.</summary>
    public List<string> BodyItems = new();
    public readonly List<string> Problems = new();
    /// <summary>k161: тона граней вида (закраска): треугольники в координатах вида от дальних к ближним, яркость 0…1, тело.</summary>
    public readonly Dictionary<string, List<(Vector2d A, Vector2d B, Vector2d C, float Shade, int Body)>> Shades = new();
    /// <summary>k161: цвет материала каждого тела (R, G, B 0…1) — стиль «Материал».</summary>
    public List<(double R, double G, double B)> BodyColors = new();

    /// <summary>Разбор: видов спроецировано.</summary>
    public static long ViewsProjected;

    /// <summary>Виды документа: обычные и разрезы — из тел, выносные элементы — из линий вида-родителя внутри круга.</summary>
    public static DrawingScene Build(DrawingDocument d, IReadOnlyList<TSolid> bodies, IReadOnlyList<string>? items = null, IReadOnlyList<(double, double, double)>? colors = null)
    {
        var sc = new DrawingScene { BodyItems = items?.ToList() ?? new(), BodyColors = colors?.ToList() ?? new() };
        foreach (var v in d.Views.Where(v => !v.IsDetail)) sc.BuildView(d, v, bodies);
        sc.BuildDetails(d);
        return sc;
    }

    /// <summary>
    /// k167: построить только эти виды (новый разрез, выносной, включённый вид) в отдельной сцене — в фоне; progress — доля и
    /// этап. Merge — перенести их в сцену чертежа (и выносные элементы заново).
    /// </summary>
    public static DrawingScene BuildSome(DrawingDocument d, IReadOnlyList<TSolid> bodies, IReadOnlyList<DrawingView> views, Action<double, string>? progress)
    {
        var sc = new DrawingScene();
        var list = views.Where(v => !v.IsDetail).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            int k = i;
            sc.BuildView(d, list[i], bodies, 0.02, (f, st) => progress?.Invoke((k + f) / Math.Max(1, list.Count), st));
        }
        progress?.Invoke(1, "готово");
        return sc;
    }

    public void Merge(DrawingDocument d, DrawingScene o)
    {
        foreach (var (k, v) in o.Lines) Lines[k] = v;
        foreach (var (k, v) in o.Axes) Axes[k] = v;
        foreach (var (k, v) in o.Arcs) Arcs[k] = v;
        foreach (var (k, v) in o.SectionBoundary) SectionBoundary[k] = v;
        foreach (var (k, v) in o.Shades) Shades[k] = v;
        Problems.AddRange(o.Problems);
        BuildDetails(d);
    }

    /// <summary>
    /// k162: быстрый вид при повороте мышью: рёбра тел (точки рёбер — из кэша) в новой проекции без скрытия линий, тона — из
    /// кэша сетки. Полный вид (скрытие, очерки, оси) — Rebuild после отпускания мыши. Прежде каждый шаг поворота строил вид
    /// полностью (около 350 мс на детали пользователя).
    /// </summary>
    public void RebuildFast(DrawingDocument d, string view, IReadOnlyList<TSolid> bodies)
    {
        var v = d.Views.FirstOrDefault(x => x.Name == view);
        if (v == null || v.IsDetail || v.IsSection) return;
        var frame = ViewFrame.Of(d.DirOf(v));
        var lines = new List<ViewLine>();
        for (int bi = 0; bi < bodies.Count; bi++)
            foreach (var pts in ViewProjection.EdgePoints(bodies[bi]))
                lines.Add(new ViewLine(pts.Select(frame.Project).ToList(), true, ViewLineKind.Edge, 0, bi));
        Lines[view] = lines;
        Axes[view] = new(); Arcs[view] = new();
        if (v.Style is "shaded" or "shadedlines" or "material") Shades[view] = ShadeTriangles(bodies, frame, fast: true);   // при повороте — без дробления
    }

    /// <summary>k161: пересчитать один вид (поворот изометрии, стиль): линии, оси, дуги, тона; выносные элементы — заново.</summary>
    public void Rebuild(DrawingDocument d, string view, IReadOnlyList<TSolid> bodies, double chord = 0.02)
    {
        var v = d.Views.FirstOrDefault(x => x.Name == view);
        if (v == null || v.IsDetail) return;
        BuildView(d, v, bodies, chord);
        BuildDetails(d);
    }

    void BuildView(DrawingDocument d, DrawingView v, IReadOnlyList<TSolid> bodies, double chord = 0.02, Action<double, string>? progress = null)
    {
        var sc = this;
        var frame = ViewFrame.Of(d.DirOf(v));
        IReadOnlyList<TSolid> src = bodies;
        if (v.IsSection)
        {
            progress?.Invoke(0.05, "сечение");
            var (cut, boundary, problems) = SectionCut.Cut(bodies, frame, v.CutDepth);
            src = cut; sc.SectionBoundary[v.Name] = boundary; sc.Problems.AddRange(problems.Select(p => $"{v.Name}: {p}"));
        }
        progress?.Invoke(v.IsSection ? 0.45 : 0.1, "скрытие линий");
        sc.Lines[v.Name] = ViewProjection.ProjectMany(src, frame, chord);
        progress?.Invoke(0.85, "оси и дуги");
        bool tilted = v.Name == "iso" || d.DirOf(v).StartsWith("v:");
        sc.Axes[v.Name] = src.SelectMany(b => ViewProjection.Axes(b, frame, tilted && !DrawingDocument.IsoAxesOff)).ToList();
        sc.Arcs[v.Name] = ViewProjection.Arcs(src, frame);
        if (v.Style is "shaded" or "shadedlines" or "material") sc.Shades[v.Name] = ShadeTriangles(src, frame);
        else sc.Shades.Remove(v.Name);
        Interlocked.Increment(ref ViewsProjected);
    }

    /// <summary>
    /// k161: закраска вида: треугольники сетки тел, обращённые к наблюдателю, тон по нормали (свет от наблюдателя, сверху
    /// справа), порядок — от дальних к ближним (рисуются по порядку — ближние закрывают дальние).
    /// </summary>
    static readonly bool ShadeVnOff = Environment.GetEnvironmentVariable("P3D_SHADEVN") == "0";
    static List<(Vector2d, Vector2d, Vector2d, float, int)> ShadeTriangles(IReadOnlyList<TSolid> bodies, ViewFrame f, bool fast = false)
    {
        var res = new List<(Vector2d, Vector2d, Vector2d, float, int, double)>();
        var light = (f.Toward * 0.8 + f.Up * 0.45 + f.Right * 0.25).Normalized();
        double Lit(Vector3d nn) => 0.35 + 0.65 * Math.Max(0, Vector3d.Dot(nn, light));
        // k166 (замечание 05.11, 15:03, п. 2–4): ГЛАДКИЙ ТОН. Прежде тон треугольника — по его нормали, один на треугольник: у
        // тела из файла (многогранные грани, большие треугольники — до 700 мм² на трубе R92) соседние треугольники одной
        // поверхности заметно различались — на видах «Тона», «Тона с линиями», «Материал» была видна сетка. Теперь:
        // - нормали в вершинах — точные нормали поверхности (цилиндр, тело вращения, выдавливание, плоскость — в том числе
        //   источник многогранной грани), иначе — сглаженные по треугольникам той же точки (излом больше 25° остаётся);
        // - треугольник больше 1 % габарита с заметно разным тоном в вершинах (больше 2 %) дробится пополам по длинной
        //   стороне, тон — по нормалям в его вершинах;
        // - отбрасывается треугольник, обращённый назад и по своей, и по сглаженной нормали (тонкий перевёрнутый треугольник
        //   сетки не оставляет дыры).
        // При повороте мышью (fast) — без дробления. Мутация P3D_SHADEVN=0 — по нормали треугольника (прежнее).
        double size = 1;
        foreach (var b in bodies) { var bb = b.Bounds; size = Math.Max(size, (bb.Max - bb.Min).Length); }
        double maxEdge = fast ? double.MaxValue : 0.01 * size;
        for (int bi = 0; bi < bodies.Count; bi++)
        {
            var m = ViewProjection.MeshOf(bodies[bi]);
            static (long, long, long) K(Vector3d p) => ((long)Math.Round(p.X * 1e5), (long)Math.Round(p.Y * 1e5), (long)Math.Round(p.Z * 1e5));
            var acc = new Dictionary<(long, long, long), List<Vector3d>>();
            double cos25 = Math.Cos(25 * Math.PI / 180);
            var faceSrf = new Dictionary<uint, Kernel.Surfaces.ISurface3?>();
            if (!ShadeVnOff)
            {
                for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                {
                    Vector3d a = (Vector3d)m.Vertices[m.Indices[t]].P, b = (Vector3d)m.Vertices[m.Indices[t + 1]].P, c = (Vector3d)m.Vertices[m.Indices[t + 2]].P;
                    var cr = Vector3d.Cross(b - a, c - a);   // длина — удвоенная площадь
                    if (cr.LengthSquared < 1e-24) continue;
                    foreach (var p in new[] { a, b, c }) { var k = K(p); (acc.TryGetValue(k, out var l) ? l : acc[k] = new()).Add(cr); }
                }
                foreach (var fc in bodies[bi].Faces)
                    faceSrf[fc.Id] = Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc) is var srf && srf is Kernel.Surfaces.CylinderSurface or Kernel.Surfaces.RevolvedSurface or Kernel.Surfaces.ExtrudedSurface or Kernel.Surfaces.PlaneSurface ? srf : null;
            }
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                var va = m.Vertices[m.Indices[t]];
                Vector3d a = (Vector3d)va.P, b = (Vector3d)m.Vertices[m.Indices[t + 1]].P, c = (Vector3d)m.Vertices[m.Indices[t + 2]].P;
                var n = Vector3d.Cross(b - a, c - a); double nl = n.Length; if (nl < 1e-12) continue; n /= nl;
                if (ShadeVnOff)
                {
                    if (Vector3d.Dot(n, f.Toward) <= 0) continue;
                    res.Add((f.Project(a), f.Project(b), f.Project(c), (float)Lit(n), bi, (f.Depth(a) + f.Depth(b) + f.Depth(c)) / 3));
                    continue;
                }
                var srfT = faceSrf.GetValueOrDefault(va.FaceId);
                Vector3d Exact(Vector3d p)
                {
                    if (srfT == null) return Vector3d.Zero;
                    var ne = srfT.NormalAt(srfT.ProjectToUV(p)); double l = ne.Length;
                    if (!(l > 1e-12)) return Vector3d.Zero;
                    ne /= l; if (Vector3d.Dot(ne, n) < 0) ne = -ne;
                    return Vector3d.Dot(ne, n) > 0.5 ? ne : Vector3d.Zero;
                }
                Vector3d S(Vector3d p)
                {
                    var ne = Exact(p); if (ne != Vector3d.Zero) return ne;
                    var v = Vector3d.Zero;
                    if (acc.TryGetValue(K(p), out var l)) foreach (var cr in l) if (Vector3d.Dot(cr, n) > cos25 * cr.Length) v += cr;
                    double vl = v.Length; return vl > 1e-18 ? v / vl : n;
                }
                var na = S(a); var nb = S(b); var nc = S(c);
                if (Vector3d.Dot(n, f.Toward) <= 0 && Vector3d.Dot(na + nb + nc, f.Toward) <= 0) continue;
                void Emit(Vector3d p0, Vector3d p1, Vector3d p2, Vector3d n0, Vector3d n1, Vector3d n2, int depth)
                {
                    double l01 = (p1 - p0).Length, l12 = (p2 - p1).Length, l20 = (p0 - p2).Length;
                    double lm = Math.Max(l01, Math.Max(l12, l20));
                    // дробится, только если тон по вершинам заметно разный (больше 2 %) и треугольник большой
                    double t0 = Lit(n0), t1 = Lit(n1), t2 = Lit(n2);
                    if (lm > maxEdge && depth < 10 && Math.Max(Math.Abs(t0 - t1), Math.Max(Math.Abs(t1 - t2), Math.Abs(t2 - t0))) > 0.02)
                    {
                        // пополам по длинной стороне; нормаль середины — точная или средняя
                        Vector3d Mid(Vector3d x, Vector3d y, Vector3d nx, Vector3d ny) { var mp = (x + y) / 2; var ne = Exact(mp); return ne != Vector3d.Zero ? ne : (nx + ny).Normalized(); }
                        if (lm == l01) { var mp = (p0 + p1) / 2; var nm = Mid(p0, p1, n0, n1); Emit(p0, mp, p2, n0, nm, n2, depth + 1); Emit(mp, p1, p2, nm, n1, n2, depth + 1); }
                        else if (lm == l12) { var mp = (p1 + p2) / 2; var nm = Mid(p1, p2, n1, n2); Emit(p0, p1, mp, n0, n1, nm, depth + 1); Emit(p0, mp, p2, n0, nm, n2, depth + 1); }
                        else { var mp = (p2 + p0) / 2; var nm = Mid(p2, p0, n2, n0); Emit(p0, p1, mp, n0, n1, nm, depth + 1); Emit(mp, p1, p2, nm, n1, n2, depth + 1); }
                        return;
                    }
                    res.Add((f.Project(p0), f.Project(p1), f.Project(p2), (float)((t0 + t1 + t2) / 3), bi, (f.Depth(p0) + f.Depth(p1) + f.Depth(p2)) / 3));
                }
                Emit(a, b, c, na, nb, nc, 0);
            }
        }
        return res.OrderBy(x => x.Item6).Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)).ToList();
    }

    void BuildDetails(DrawingDocument d)
    {
        var sc = this;
        foreach (var v in d.Views.Where(v => v.IsDetail))
        {
            if (v.Parent == null || !sc.Lines.TryGetValue(v.Parent, out var pl)) { sc.Lines[v.Name] = new(); continue; }
            var c = new Vector2d(v.DetailX, v.DetailY); double r = v.DetailR;
            sc.Lines[v.Name] = pl.SelectMany(l => ClipToCircle(l, c, r)).ToList();
            sc.Axes[v.Name] = sc.Axes[v.Parent].Where(a => (a.Center - c).Length < r).ToList();
            sc.Arcs[v.Name] = sc.Arcs[v.Parent].Where(a => (a.Center - c).Length < r).ToList();
            if (sc.SectionBoundary.TryGetValue(v.Parent, out var sb)) sc.SectionBoundary[v.Name] = sb.SelectMany(s => ClipSegment(s.A, s.B, c, r)).ToList();
        }
    }

    /// <summary>Ломаная внутри круга: части отрезков, лежащие в круге (точки входа и выхода — пересечение с окружностью).</summary>
    public static IEnumerable<ViewLine> ClipToCircle(ViewLine l, Vector2d c, double r)
    {
        var cur = new List<Vector2d>();
        for (int i = 0; i + 1 < l.Points.Count; i++)
            foreach (var (a, b) in ClipSegment(l.Points[i], l.Points[i + 1], c, r))
            {
                if (cur.Count > 0 && (cur[^1] - a).Length > 1e-9) { if (cur.Count > 1) yield return l with { Points = cur }; cur = new(); }
                if (cur.Count == 0) cur.Add(a);
                cur.Add(b);
            }
        if (cur.Count > 1) yield return l with { Points = cur };
    }

    public static IEnumerable<(Vector2d A, Vector2d B)> ClipSegment(Vector2d a, Vector2d b, Vector2d c, double r)
    {
        var d = b - a; var f = a - c;
        double A = Vector2d.Dot(d, d), B = 2 * Vector2d.Dot(f, d), C = Vector2d.Dot(f, f) - r * r;
        if (A < 1e-24) { if (C <= 0) yield return (a, b); yield break; }
        double disc = B * B - 4 * A * C;
        if (disc <= 0) yield break;
        double sq = Math.Sqrt(disc);
        double t0 = Math.Max(0, (-B - sq) / (2 * A)), t1 = Math.Min(1, (-B + sq) / (2 * A));
        if (t1 - t0 > 1e-12) yield return (a + d * t0, a + d * t1);
    }
}

/// <summary>
/// k155–k156 (D2–D5): ЧЕРТЁЖ (.p3drw) — деталь или сборка из файла (или открытая), шаблон листа, виды, размеры, обозначения.
/// Масштаб листа — из ряда ГОСТ 2.302; раскладка видов по ГОСТ 2.305 (метод первого угла): главный вид, вид сверху — под ним,
/// вид слева — справа от него, изометрия — в свободном углу; разрезы, выносные элементы и прочие виды — колонкой справа.
/// Линии видов — проекция со скрытием (ViewProjection): видимый контур — сплошная толстая, невидимый — штриховая, линии
/// перехода — тонкая (по выбору). Размеры — ГОСТ 2.307, обозначения — ГОСТ 2.308, 2.309, 2.109.
/// </summary>
public sealed class DrawingDocument
{
    public int Version { get; set; } = 2;
    /// <summary>Файл детали (.p3d) или сборки (.p3a); пусто — открытый документ.</summary>
    public string? SourcePath { get; set; }
    public SheetTemplate Template { get; set; } = SheetLayout.Default("A3", true);
    /// <summary>Масштаб листа (1 — 1:1, 0.5 — 1:2, 2 — 2:1); 0 — подобрать из ряда ГОСТ 2.302.</summary>
    public double Scale { get; set; }
    public List<DrawingView> Views { get; set; } = new();
    public List<DrawingDim> Dims { get; set; } = new();
    public List<DrawingNote> Notes { get; set; } = new();
    public bool ShowDims { get; set; } = true;
    /// <summary>
    /// k169 (замечание 05.11, 16:17, п. 15–16): следующие листы чертежа (у каждого — свой шаблон, виды, размеры, обозначения,
    /// модель: как отдельный документ). Лист 1 — сам документ.
    /// </summary>
    public List<DrawingDocument> MoreSheets { get; set; } = new();
    /// <summary>k169: лист без видов по умолчанию (новый лист, аннотация).</summary>
    public bool NoDefaultViews { get; set; }
    /// <summary>Слой допусков формы и расположения (рамки и базы) — вкл/выкл.</summary>
    public bool ShowGdt { get; set; } = true;
    /// <summary>k164 (замечание 05.11, 10:33, п. 5): номера центров «℄N» у центров и под полкой диаметров и радиусов.</summary>
    public bool CenterNumbers { get; set; } = true;
    /// <summary>k164: осевые линии на листе (мм листа) и центры по видам после Compose — приоритет осевых над выносными.</summary>
    [JsonIgnore] public Dictionary<string, List<(Vector2d A, Vector2d B)>> CenterSegs { get; } = new();
    /// <summary>k170: видимые отрезки контура вида (мм модели, координаты вида) — для выносных линий углового размера.</summary>
    [JsonIgnore] public Dictionary<string, List<(Vector2d A, Vector2d B)>> ViewSegs { get; } = new();
    [JsonIgnore] public Dictionary<string, List<Vector2d>> CenterPts { get; } = new();
    /// <summary>k164: номера центров после Compose: вид, точка листа, номер.</summary>
    [JsonIgnore] public List<(string View, Vector2d P, int No, Vector2d PV)> CenterNos { get; } = new();
    /// <summary>k165: центры вида в координатах вида (мм модели) — в том же порядке, что CenterPts.</summary>
    [JsonIgnore] public Dictionary<string, List<Vector2d>> CenterPtsView { get; } = new();
    /// <summary>
    /// k165 (замечание 05.11, 13:27, п. 1–2): места подписей центров «℄N» — подобраны один раз («Автоматические размеры»
    /// подбирают заново) и переносятся мышью; в файле.
    /// </summary>
    public List<CentreMark> CentreMarks { get; set; } = new();
    /// <summary>Габариты видов на листе после Compose (мм листа) — для выбора и переноса вида мышью.</summary>
    [JsonIgnore] public Dictionary<string, (double X0, double Y0, double X1, double Y1)> Boxes { get; } = new();
    /// <summary>Преобразование вида «модель → лист» после Compose: середина проекции, масштаб, центр на листе.</summary>
    [JsonIgnore] public Dictionary<string, (Vector2d Mid, double S, double X, double Y)> Xf { get; } = new();

    /// <summary>
    /// k159: единица длины чертежа: пусто — как у модели (система единиц детали), иначе mm, cm, m, in, ft. Значения размеров
    /// пересчитываются из мм модели.
    /// </summary>
    public string Units { get; set; } = "";
    /// <summary>k161 (п. 4): толщина сплошной толстой основной линии S, мм (ГОСТ 2.303: 0.5…1.4; прежде 0.7).</summary>
    public double LineS { get; set; } = 0.5;
    /// <summary>Единица длины модели (из детали; задаёт окно чертежа).</summary>
    [JsonIgnore] public string ModelUnits { get; set; } = "mm";
    static readonly bool UnitsOff = Environment.GetEnvironmentVariable("P3D_DRAWUNITS") == "0";
    public string EffectiveUnits => UnitsOff ? "mm" : Units.Length > 0 ? Units : ModelUnits;
    public static double MmPer(string u) => u switch { "cm" => 10, "m" => 1000, "in" => 25.4, "ft" => 304.8, _ => 1 };
    static string UnitFormat(string u) => u switch { "cm" => "0.###", "m" => "0.####", "in" => "0.###", "ft" => "0.####", _ => "0.##" };
    /// <summary>Подпись размера в единицах чертежа.</summary>
    public string CaptionOf(DrawingDim d) => d.Caption(Comma, MmPer(EffectiveUnits), UnitFormat(EffectiveUnits));
    /// <summary>k170: длина (мм) в единицах чертежа, как подпись размера (без знака единицы).</summary>
    public string FormatLength(double mm) { var t = (mm / MmPer(EffectiveUnits)).ToString(UnitFormat(EffectiveUnits), CultureInfo.InvariantCulture); return Comma ? t.Replace('.', ',') : t; }
    [JsonIgnore] public bool UsesComma => Comma;
    /// <summary>k170 (п. 7.3): размер — серый показ на листе (недостающий размер под курсором в списке проверки); не в Dims.</summary>
    [JsonIgnore] public DrawingDim? Ghost { get; set; }
    [JsonIgnore] public List<(double X1, double Y1, double X2, double Y2)>? GhostSegs { get; private set; }
    /// <summary>k159: положения и свойства выключенных основных видов — при включении вид встаёт туда же.</summary>
    public Dictionary<string, double[]> Parked { get; set; } = new();
    /// <summary>Масштаб листа последнего Compose (подобранный или заданный).</summary>
    [JsonIgnore] public double LastScale { get; private set; }
    /// <summary>
    /// k159: геометрия элементов на листе после Compose — отрезки (мм листа) размеров и обозначений: подсветка под курсором,
    /// выделение, перенос.
    /// </summary>
    [JsonIgnore] public Dictionary<object, List<(double X1, double Y1, double X2, double Y2)>> Hit { get; } = new(ReferenceEqualityComparer.Instance);
    [JsonIgnore] public Dictionary<object, List<Vector2d[]>> HitTexts { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Знак диаметра: в шрифте ГОСТ тип А (GOST_A.TTF) он на месте U+2205 (на месте «Ø» — буква «Ш»).</summary>
    public const string Dia = "\u2205";

    public string DirOf(DrawingView v) =>
        v.IsDetail && v.Parent != null && Views.FirstOrDefault(x => x.Name == v.Parent) is { } p ? DirOf(p) : string.IsNullOrEmpty(v.Dir) ? v.Name : v.Dir;

    /// <summary>Вид → лист.</summary>
    public (double X, double Y) ToSheet(string view, Vector2d p)
    {
        var (mid, s, x, y) = Xf[view];
        var v = Views.FirstOrDefault(q => q.Name == view);
        return MapF(x, y, v?.Angle ?? 0, mid, s, new Vector2d(Fold(v, p.X, s), p.Y));
    }
    /// <summary>Лист → вид.</summary>
    public Vector2d ToView(string view, double sx, double sy)
    {
        var (mid, s, x, y) = Xf[view];
        var v = Views.FirstOrDefault(q => q.Name == view);
        var l = Rot(new Vector2d(sx - x, sy - y), -(v?.Angle ?? 0));
        return new Vector2d(Unfold(v, mid.X + l.X / s, s), mid.Y + l.Y / s);
    }
    /// <summary>k159: точка вида (X уже с разрывом) → лист: масштаб, поворот вокруг центра вида, перенос.</summary>
    static (double X, double Y) MapF(double x, double y, double angle, Vector2d mid, double s, Vector2d p)
    {
        var r = Rot((p - mid) * s, angle);
        return (x + r.X, y + r.Y);
    }
    public static Vector2d Rot(Vector2d p, double deg)
    {
        if (deg == 0) return p;
        double a = deg * Math.PI / 180, c = Math.Cos(a), sn = Math.Sin(a);
        return new Vector2d(p.X * c - p.Y * sn, p.X * sn + p.Y * c);
    }
    /// <summary>Направление вида → направление на листе (поворот вида).</summary>
    public Vector2d DirToSheet(string view, Vector2d d) => Rot(d, Views.FirstOrDefault(q => q.Name == view)?.Angle ?? 0);

    /// <summary>k158: мутация «вид с разрывом»: P3D_VIEWBREAK=0 — разрыв не применяется (вид целиком).</summary>
    static readonly bool BreakOff = Environment.GetEnvironmentVariable("P3D_VIEWBREAK") == "0";
    /// <summary>X вида → X вида с разрывом (правая часть придвинута; точки внутри полосы — к её левому краю).</summary>
    public static double Fold(DrawingView? v, double x, double s)
    {
        if (v == null || !v.HasBreak || BreakOff) return x;
        if (x <= v.BreakA) return x;
        if (x >= v.BreakB) return x - (v.BreakB - v.BreakA) + v.BreakGap / s;
        return v.BreakA;
    }
    public static double Unfold(DrawingView? v, double x, double s)
    {
        if (v == null || !v.HasBreak || BreakOff) return x;
        if (x <= v.BreakA) return x;
        double g = v.BreakGap / s;
        if (x >= v.BreakA + g) return x + (v.BreakB - v.BreakA) - g;
        return x - v.BreakA < g / 2 ? v.BreakA : v.BreakB;
    }
    /// <summary>Ломаная без полосы разрыва (части слева и справа от неё).</summary>
    public static IEnumerable<List<Vector2d>> ClipBreak(List<Vector2d> pts, double a, double b)
    {
        var cur = new List<Vector2d>();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = pts[i]; var q = pts[i + 1];
            var parts = new List<(double T0, double T1)>();
            foreach (var (lo, hi) in new[] { (double.NegativeInfinity, a), (b, double.PositiveInfinity) })
            {
                // часть отрезка с lo <= x <= hi
                double t0 = 0, t1 = 1, dx = q.X - p.X;
                if (Math.Abs(dx) < 1e-15) { if (p.X < lo || p.X > hi) continue; }
                else
                {
                    double ta = (lo - p.X) / dx, tb = (hi - p.X) / dx;
                    if (ta > tb) (ta, tb) = (tb, ta);
                    t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
                    if (t1 - t0 < 1e-12) continue;
                }
                parts.Add((t0, t1));
            }
            foreach (var (t0, t1) in parts.OrderBy(x => x.T0))
            {
                var A = p + (q - p) * t0; var B = p + (q - p) * t1;
                if (cur.Count > 0 && (cur[^1] - A).Length > 1e-9) { if (cur.Count > 1) yield return cur; cur = new(); }
                if (cur.Count == 0) cur.Add(A);
                cur.Add(B);
            }
        }
        if (cur.Count > 1) yield return cur;
    }

    /// <summary>
    /// Перенос вида с проекционной связью ГОСТ 2.305: главный — вместе с проекционными (их смещение то же); вид сверху и снизу —
    /// только по вертикали; слева, справа и сзади — только по горизонтали; изометрия, разрезы и выносные — свободно.
    /// </summary>
    public void MoveView(string name, double dx, double dy)
    {
        var v = Views.FirstOrDefault(x => x.Name == name);
        if (v == null) return;
        // k161 (замечание 05.11, 01:23, п. 6): виды переносятся свободно (у повёрнутого вида проекционная связь теряет смысл);
        // выравнивание с другими видами — притяжением при переносе (SnapView). Мутация P3D_VIEWLINK=0 — прежняя связь.
        if (!LinkOff || v.IsSection || v.IsDetail || v.IsAux) { v.X += dx; v.Y += dy; return; }
        switch (name)
        {
            case "front":
                foreach (var x in Views.Where(x => !x.IsSection && !x.IsDetail && x.Name is "front" or "top" or "left" or "right" or "bottom" or "back")) { x.X += dx; x.Y += dy; }
                break;
            case "top" or "bottom": v.Y += dy; break;
            case "left" or "right" or "back": v.X += dx; break;
            default: v.X += dx; v.Y += dy; break;
        }
    }
    static readonly bool LinkOff = Environment.GetEnvironmentVariable("P3D_VIEWLINK") == "0";

    /// <summary>
    /// k161: притяжение переносимого вида: центр по X или Y ближе tol мм к центру другого вида — выравнивается (проекционная
    /// связь по выбору пользователя). Возвращает линии-подсказки (мм листа) для показа.
    /// </summary>
    public List<(double X1, double Y1, double X2, double Y2)> SnapView(string name, double tol = 3)
    {
        var res = new List<(double, double, double, double)>();
        var v = Views.FirstOrDefault(x => x.Name == name);
        if (v == null || !Boxes.ContainsKey(name)) return res;
        var others = Views.Where(x => x != v && Boxes.ContainsKey(x.Name)).ToList();
        var bx = others.Where(o => Math.Abs(o.X - v.X) < tol).OrderBy(o => Math.Abs(o.X - v.X)).FirstOrDefault();
        var by = others.Where(o => Math.Abs(o.Y - v.Y) < tol).OrderBy(o => Math.Abs(o.Y - v.Y)).FirstOrDefault();
        if (bx != null) { v.X = bx.X; res.Add((v.X, Math.Min(v.Y, bx.Y), v.X, Math.Max(v.Y, bx.Y))); }
        if (by != null) { v.Y = by.Y; res.Add((Math.Min(v.X, by.X), v.Y, Math.Max(v.X, by.X), v.Y)); }
        return res;
    }

    /// <summary>Вид под точкой листа (по габариту).</summary>
    public string? ViewAt(double x, double y) =>
        Boxes.Where(b => x >= b.Value.X0 - 2 && x <= b.Value.X1 + 2 && y >= b.Value.Y0 - 2 && y <= b.Value.Y1 + 2)
             .OrderBy(b => (b.Value.X1 - b.Value.X0) * (b.Value.Y1 - b.Value.Y0)).Select(b => b.Key).FirstOrDefault();

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static DrawingDocument FromJson(string json) => JsonSerializer.Deserialize<DrawingDocument>(json, Opts) ?? throw new InvalidDataException("not a drawing");

    /// <summary>Ряд масштабов ГОСТ 2.302 (уменьшения и увеличения).</summary>
    public static readonly double[] GostScales = { 50, 40, 20, 10, 5, 4, 2.5, 2, 1, 0.5, 0.4, 0.25, 0.2, 0.1, 0.05, 0.04, 0.025, 0.02, 0.01 };
    public static string ScaleText(double s) => s >= 1 ? $"{s.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))}:1" : $"1:{(1 / s).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))}";

    /// <summary>Оси тел вращения на виде.</summary>
    public static List<ViewAxis> AxesOf(IEnumerable<TSolid> bodies, string view) => bodies.SelectMany(b => ViewProjection.Axes(b, ViewFrame.Of(view))).ToList();

    /// <summary>Проекции тел на вид (мм модели) — общая сцена скрытия, масштаб не влияет.</summary>
    public static List<ViewLine> ProjectBodies(IEnumerable<TSolid> bodies, string view, double chord = 0.02) =>
        ViewProjection.ProjectMany(bodies.ToList(), ViewFrame.Of(view), chord);

    static (Vector2d Min, Vector2d Max) Box(List<ViewLine> lines)
    {
        var mn = new Vector2d(double.MaxValue); var mx = new Vector2d(double.MinValue);
        foreach (var l in lines) foreach (var p in l.Points) { mn = Vector2d.ComponentMin(mn, p); mx = Vector2d.ComponentMax(mx, p); }
        if (mn.X > mx.X) { mn = Vector2d.Zero; mx = Vector2d.Zero; }
        return (mn, mx);
    }

    /// <summary>Стандартный набор видов, если их нет: главный, сверху, слева, изометрия.</summary>
    public void EnsureViews()
    {
        if (Views.Count > 0 || NoDefaultViews) return;
        // аксонометрия — без невидимых линий (ГОСТ 2.317: невидимый контур на аксонометрии не показывают)
        foreach (var n in new[] { "front", "top", "left", "iso" }) Views.Add(new DrawingView { Name = n, ShowHidden = n != "iso" });
    }

    /// <summary>Следующая свободная буква обозначения (ГОСТ 2.316: кириллица без Ё, З, Й, О, Х, Ъ, Ы, Ь).</summary>
    public string NextLetter()
    {
        const string letters = "АБВГДЕЖИКЛМНПРСТУФЦЧШЩЭЮЯ";
        var used = Views.Select(v => v.Label).Concat(Notes.Where(n => n.Kind == "datum").Select(n => n.Text)).ToHashSet();
        foreach (char c in letters) if (!used.Contains(c.ToString())) return c.ToString();
        return "А1";
    }

    static readonly string[] Grid = { "front", "top", "left", "iso" };
    static readonly bool ArrangeOffV = Environment.GetEnvironmentVariable("P3D_VIEWARRANGE") == "0";

    /// <summary>Габарит для раскладки: у вида с разрывом — без полосы (промежуток не учитывается).</summary>
    static (Vector2d, Vector2d) BreakBox(DrawingView v, (Vector2d Min, Vector2d Max) b)
    {
        if (!v.HasBreak || BreakOff || v.IsDetail) return b;
        double cut = Math.Max(0, Math.Min(b.Max.X, v.BreakB) - Math.Max(b.Min.X, v.BreakA));
        return (b.Min, new Vector2d(b.Max.X - cut, b.Max.Y));
    }

    /// <summary>
    /// Раскладка: масштаб листа (если 0 — наибольший из ряда, при котором виды с промежутками 20 мм помещаются в рамку над
    /// основной надписью) и центры видов по ГОСТ 2.305 (у видов с X = Y = 0). Свой масштаб вида учитывается. Прочие виды
    /// (справа, снизу, сзади, разрезы, выносные) — колонкой справа от сетки. Возвращает выбранный масштаб.
    /// </summary>
    public double Layout(Dictionary<string, List<ViewLine>> proj)
    {
        var (x0, y0, x1, y1) = SheetLayout.FrameRect(Template);
        double tbH = SheetLayout.TitleHeight(Template.TitleBlock);
        var box = Views.ToDictionary(v => v.Name, v => v.IsDetail ? (new Vector2d(v.DetailX - v.DetailR, v.DetailY - v.DetailR), new Vector2d(v.DetailX + v.DetailR, v.DetailY + v.DetailR))
                                                     : proj.TryGetValue(v.Name, out var l) ? BreakBox(v, Box(l)) : (Vector2d.Zero, Vector2d.Zero));
        DrawingView? V(string n) => Views.FirstOrDefault(v => v.Name == n);
        // размер вида на листе при масштабе листа s (свой масштаб вида — постоянен)
        Vector2d Size(string n, double s)
        {
            if (V(n) is not { } v) return Vector2d.Zero;
            var e = (box[n].Item2 - box[n].Item1) * (v.Scale > 0 ? v.Scale : s);
            if (v.Angle == 0) return e;
            // k159: повёрнутый вид — габарит повёрнутого прямоугольника
            double a = v.Angle * Math.PI / 180, c = Math.Abs(Math.Cos(a)), sn = Math.Abs(Math.Sin(a));
            return new Vector2d(e.X * c + e.Y * sn, e.X * sn + e.Y * c);
        }
        var extras = Views.Where(v => !Grid.Contains(v.Name)).ToList();
        const double gap = 20;
        double availW = x1 - x0 - 2 * gap, availH = y1 - y0 - tbH - 2 * gap;
        // k161 (замечание 05.11, 01:23, п. 2): два варианта сетки — «вид слева — слева от главного, вид сверху — под главным,
        // изометрия — слева внизу» (просьба пользователя) и ГОСТ 2.305 (вид слева — справа, изометрия — справа внизу); берётся
        // тот, при котором масштаб листа крупнее (равный — первый). Мутация P3D_VIEWARRANGE=0 — только ГОСТ (прежнее).
        string[][] arrA = { new[] { "left", "iso" }, new[] { "front", "top" } }, arrB = { new[] { "front", "top" }, new[] { "left", "iso" } };
        (double wl, double wr, double ht, double hb, double we, double he) Dims(double s, string[][] arr)
        {
            double wl = arr[0].Max(n => Size(n, s).X), wr = arr[1].Max(n => Size(n, s).X);
            double ht = Math.Max(Size("front", s).Y, Size("left", s).Y), hb = Math.Max(Size("top", s).Y, Size("iso", s).Y);
            double we = extras.Count == 0 ? 0 : extras.Max(v => Size(v.Name, s).X) + 10;   // +10 — подписи
            double he = extras.Sum(v => Size(v.Name, s).Y + 12) + Math.Max(0, extras.Count - 1) * 8;
            return (wl, wr, ht, hb, we, he);
        }
        double Fit(double s, string[][] arr)
        {
            var (wl, wr, ht, hb, we, he) = Dims(s, arr);
            double w = wl + (wl > 0 && wr > 0 ? gap : 0) + wr + (we > 0 ? gap + we : 0), h = Math.Max(ht + (hb > 0 ? gap + hb : 0), he);
            return Math.Max(w / availW, h / availH);
        }
        double Pick(string[][] arr) { foreach (var s in GostScales) if (Fit(s, arr) <= 1) return s; return GostScales.Last(); }
        var arrangement = ArrangeOffV ? arrB : Pick(arrA) >= Pick(arrB) ? arrA : arrB;
        double scale = Scale;
        if (scale <= 0) scale = Pick(arrangement);
        var (wl2, wr2, ht2, hb2, we2, he2) = Dims(scale, arrangement);
        double g01 = wl2 > 0 && wr2 > 0 ? gap : 0;
        double totalW = wl2 + g01 + wr2 + (we2 > 0 ? gap + we2 : 0), totalH = Math.Max(ht2 + (hb2 > 0 ? gap + hb2 : 0), he2);
        double left = x0 + gap + Math.Max(0, (availW - totalW) / 2), top = y1 - gap - Math.Max(0, (availH - totalH) / 2);
        var centre = new Dictionary<string, (double X, double Y)>();
        for (int col = 0; col < 2; col++)
            for (int row = 0; row < 2; row++)
                centre[arrangement[col][row]] = (col == 0 ? left + wl2 / 2 : left + wl2 + g01 + wr2 / 2, row == 0 ? top - ht2 / 2 : top - ht2 - gap - hb2 / 2);
        var (wl2b, wr2b) = (wl2, wr2 + g01);
        double ex = left + wl2b + wr2b + gap + we2 / 2, ey = top;
        // k161: виды уже стоят (пользователь мог их перенести, сетка прежняя) — новый прочий вид ставится справа от стоящих
        // видов сетки и ниже стоящих прочих (прежде — по сетке заново: при новом виде сетка сдвигалась, и он ложился на главный)
        var placedGrid = Views.Where(v => Grid.Contains(v.Name) && (v.X != 0 || v.Y != 0)).ToList();
        if (placedGrid.Count > 0)
        {
            ex = placedGrid.Max(v => v.X + Size(v.Name, scale).X / 2) + gap + we2 / 2;
            ey = placedGrid.Max(v => v.Y + Size(v.Name, scale).Y / 2);
            var placedExtra = extras.Where(v => v.X != 0 || v.Y != 0).ToList();
            if (placedExtra.Count > 0) ey = placedExtra.Min(v => v.Y - Size(v.Name, scale).Y / 2) - 8;
        }
        foreach (var v in extras.Where(v => placedGrid.Count == 0 || v.X == 0 && v.Y == 0))
        {
            var sz = Size(v.Name, scale);
            ey -= 12;   // подпись над видом
            centre[v.Name] = (ex, ey - sz.Y / 2);
            ey -= sz.Y + 8;
        }
        // k159: вид без линий (модель ещё не пришла) места не получает — иначе раскладка по пустым видам закреплялась бы
        foreach (var v in Views)
            if (v.X == 0 && v.Y == 0 && centre.TryGetValue(v.Name, out var c) && (v.IsDetail || proj.TryGetValue(v.Name, out var pl) && pl.Count > 0)) { v.X = c.X; v.Y = c.Y; }
        return scale;
    }

    /// <summary>Совместимость (k155): лист по линиям и осям без дуг и сечений.</summary>
    public SheetGraphics Compose(Dictionary<string, List<ViewLine>> proj, FieldSource fields, Dictionary<string, List<ViewAxis>>? axes = null)
    {
        var sc = new DrawingScene();
        foreach (var (k, v) in proj) sc.Lines[k] = v;
        if (axes != null) foreach (var (k, v) in axes) sc.Axes[k] = v;
        return Compose(sc, fields);
    }

    bool Comma => Template.Standard == SheetStandard.Eskd;

    /// <summary>
    /// Лист чертежа: шаблон (надпись: масштаб листа, поля детали), линии видов, штриховка разрезов, линии разреза и круги
    /// выносных элементов на видах-родителях, подписи видов, размеры, обозначения.
    /// </summary>
    public SheetGraphics Compose(DrawingScene scene, FieldSource fields)
    {
        Boxes.Clear(); Xf.Clear();
        var proj = scene.Lines;
        double scale = Layout(proj);
        LastScale = scale; Hit.Clear(); HitTexts.Clear(); CenterSegs.Clear(); ViewSegs.Clear(); GhostSegs = null; CenterPts.Clear(); CenterNos.Clear(); CenterPtsView.Clear();
        fields.Auto[Fields.Scale] = ScaleText(scale);
        var g = SheetLayout.Compose(Template, fields);
        if (!LineSOff) g.ThickW = Math.Clamp(LineS, 0.1, 1.4);   // k165 (п. 5): от 0.1 мм
        foreach (var v in Views)
        {
            if (!proj.TryGetValue(v.Name, out var lines)) continue;
            double s = v.Scale > 0 ? v.Scale : scale;
            var (mn, mx) = Box(lines);
            Vector2d mid;
            // выносной элемент — центр круга в середине вида
            if (v.IsDetail) { mid = new Vector2d(v.DetailX, v.DetailY); mn = mid - new Vector2d(v.DetailR); mx = mid + new Vector2d(v.DetailR); }
            else
            {
                // k158: вид с разрывом — габарит по придвинутой правой части
                mn.X = Fold(v, mn.X, s); mx.X = Fold(v, mx.X, s);
                mid = (mn + mx) / 2;
            }
            Xf[v.Name] = (mid, s, v.X, v.Y);
            bool brk = v.HasBreak && !BreakOff && !v.IsDetail;
            (double, double) T(Vector2d p) => MapF(v.X, v.Y, v.Angle, mid, s, new Vector2d(Fold(v, p.X, s), p.Y));
            (double X, double Y) TF(Vector2d p) => MapF(v.X, v.Y, v.Angle, mid, s, p);   // X уже с разрывом
            {
                var cs = new[] { TF(mn), TF(new Vector2d(mx.X, mn.Y)), TF(mx), TF(new Vector2d(mn.X, mx.Y)) };
                Boxes[v.Name] = (cs.Min(q => q.X), cs.Min(q => q.Y), cs.Max(q => q.X), cs.Max(q => q.Y));
            }
            // k161: тона граней (под линиями)
            if (scene.Shades.TryGetValue(v.Name, out var shades) && v.Style is "shaded" or "shadedlines" or "material")
                foreach (var (a, b, c, sh, bi) in shades)
                {
                    var (r, gg, bb) = v.Style == "material" && bi < scene.BodyColors.Count ? scene.BodyColors[bi] : (0.78, 0.8, 0.84);
                    uint argb = 0xFF000000u | (uint)Math.Clamp(r * sh * 255, 0, 255) << 16 | (uint)Math.Clamp(gg * sh * 255, 0, 255) << 8 | (uint)Math.Clamp(bb * sh * 255, 0, 255);
                    g.Shades.Add((new List<(double, double)> { T(a), T(b), T(c) }, argb));
                }
            {
                var vs = new List<(Vector2d, Vector2d)>();
                foreach (var l in lines) if (l.Visible) for (int k = 0; k + 1 < l.Points.Count; k++) vs.Add((l.Points[k], l.Points[k + 1]));
                ViewSegs[v.Name] = vs;
            }
            bool showHidden = v.Style switch { "hidden" or "wire" => true, "shaded" or "shadedlines" or "material" => false, _ => v.ShowHidden };
            foreach (var l in lines)
            {
                if (v.Style == "shaded") break;
                if (!l.Visible && !showHidden) continue;
                if (l.Kind == ViewLineKind.Tangent && !v.ShowTangent) continue;
                var kind = !l.Visible ? (v.Style == "wire" ? LineKind.Thick : LineKind.Hidden) : l.Kind == ViewLineKind.Tangent ? LineKind.Thin : LineKind.Thick;
                if (brk) { foreach (var part in ClipBreak(l.Points, v.BreakA, v.BreakB)) g.Polylines.Add(new SheetPolyline(part.Select(T).ToList(), kind)); }
                else g.Polylines.Add(new SheetPolyline(l.Points.Select(T).ToList(), kind));
            }
            if (brk) BreakLines(g, v, s, mn.Y, mx.Y, TF);
            // штриховка фигуры сечения ГОСТ 2.306: 45°, шаг 3 мм листа
            if (scene.SectionBoundary.TryGetValue(v.Name, out var sb) && sb.Count > 0)
                foreach (var (a, b) in SectionCut.Hatch(sb, 45 - v.Angle, 3 / s))
                    foreach (var part in brk ? ClipBreak(new() { a, b }, v.BreakA, v.BreakB) : new[] { new List<Vector2d> { a, b } })
                        g.Polylines.Add(new SheetPolyline(part.Select(T).ToList(), LineKind.Thin));
            int cl0 = g.Lines.Count, cp0 = g.Polylines.Count;
            // k167 (п. 7): осевые и на изометрии (оси тел вращения в проекции)
            if (v.CenterLines && (v.Name != "iso" || !IsoAxesOff) && scene.Axes.TryGetValue(v.Name, out var ax)) AxisLines(g, ax, T, s);
            // k163 (замечание 05.11, 09:22, п. 3.1): центры дуг, не отмеченные осями тел вращения (дуги профиля, скругления
            // паза), — перекрестие тонкими штрихпунктирными: полудлина — радиус дуги + 3 мм листа, но не больше 15 мм листа.
            // Мутация P3D_ARCCENTERS=0 — только оси тел вращения (прежнее).
            if (v.CenterLines && v.Name != "iso" && !ArcCentersOff && scene.Arcs.TryGetValue(v.Name, out var arcsV))
            {
                var marked = (scene.Axes.GetValueOrDefault(v.Name) ?? new()).Where(a => a.Cross).Select(a => a.Center).ToList();
                var done = new List<Vector2d>();
                foreach (var a in arcsV.OrderByDescending(a => a.Radius))
                {
                    if (marked.Any(c => (c - a.Center).Length < 1e-3) || done.Any(c => (c - a.Center).Length < 1e-3)) continue;
                    done.Add(a.Center);
                    double hl = Math.Min(a.Radius * s + 3, 15) / s;
                    var c0 = a.Center;
                    foreach (var (p, q) in new[] { (c0 - Vector2d.UnitX * hl, c0 + Vector2d.UnitX * hl), (c0 - Vector2d.UnitY * hl, c0 + Vector2d.UnitY * hl) })
                    {
                        // k164 (замечание 05.11, 10:33, п. 3): ломаной — отрезки листа (Lines) рисовались без штрихов: осевая
                        // выходила сплошной
                        g.Polylines.Add(new SheetPolyline(new() { T(p), T(q) }, LineKind.Center));
                    }
                }
            }
            // k164: осевые линии и центры вида (лист) — для приоритета осевых над выносными и для номеров центров
            {
                var segs = new List<(Vector2d, Vector2d)>();
                for (int i = cl0; i < g.Lines.Count; i++) if (g.Lines[i].Kind == LineKind.Center) segs.Add((new(g.Lines[i].X1, g.Lines[i].Y1), new(g.Lines[i].X2, g.Lines[i].Y2)));
                for (int i = cp0; i < g.Polylines.Count; i++)
                    if (g.Polylines[i].Kind == LineKind.Center)
                        for (int k = 0; k + 1 < g.Polylines[i].Points.Count; k++) { var (ax0, ay0) = g.Polylines[i].Points[k]; var (ax1, ay1) = g.Polylines[i].Points[k + 1]; segs.Add((new(ax0, ay0), new(ax1, ay1))); }
                CenterSegs[v.Name] = segs;
                var pts = new List<Vector2d>(); var ptsV = new List<Vector2d>();
                if (v.Name != "iso")
                {
                    foreach (var a in scene.Axes.GetValueOrDefault(v.Name) ?? new()) if (a.Cross) { var (px, py) = T(a.Center); pts.Add(new(px, py)); ptsV.Add(a.Center); }
                    foreach (var a in scene.Arcs.GetValueOrDefault(v.Name) ?? new()) { var (px, py) = T(a.Center); var pp = new Vector2d(px, py); if (!pts.Any(q => (q - pp).Length < 0.05)) { pts.Add(pp); ptsV.Add(a.Center); } }
                }
                CenterPts[v.Name] = pts; CenterPtsView[v.Name] = ptsV;
            }
            // подписи: «А–А», «Б (2:1)», свой масштаб «(1:2)»
            string caption = v.IsSection ? $"{v.Label}-{v.Label}" : v.IsDetail || v.IsAux ? v.Label : "";
            if (v.Scale > 0 && Math.Abs(v.Scale - scale) > 1e-12) caption = (caption + $" ({ScaleText(v.Scale)})").Trim();
            if (caption.Length > 0) g.Text(v.X, Boxes[v.Name].Y1 + 5, v.IsSection || v.IsDetail || v.IsAux ? 7 : SheetLayout.ValueH, caption, TextAlign.Center);
        }
        // линии разреза и круги выносных элементов — на виде-родителе
        foreach (var v in Views.Where(v => (v.IsSection || v.IsDetail || v.IsAux) && v.Parent != null && Xf.ContainsKey(v.Parent!)))
        {
            if (v.IsSection) CutLine(g, v);
            else if (v.IsAux) AuxArrow(g, v);
            else
            {
                var (cx, cy) = ToSheet(v.Parent!, new Vector2d(v.DetailX, v.DetailY));
                double r = v.DetailR * Xf[v.Parent!].S;
                g.Circles.Add((cx, cy, r, LineKind.Thin));
                // буква на полке выноски от круга (ГОСТ 2.305 п. 7.1)
                double a = Math.PI / 4;
                var p0 = (cx + r * Math.Cos(a), cy + r * Math.Sin(a)); var p1 = (p0.Item1 + 5, p0.Item2 + 5);
                g.Poly(LineKind.Thin, p0, p1, (p1.Item1 + 8, p1.Item2));
                g.Text(p1.Item1 + 4, p1.Item2 + 1, 7, v.Label, TextAlign.Center);
            }
        }
        // k159: сначала линейные размеры, затем диаметры и радиусы — у них угол выноски подбирается без наложения текстов
        if (ShowDims && CenterNumbers && !CenterNosOff) NumberCentres();
        if (ShowDims)
        {
            var rows = new Dictionary<(string, string, int), int>();
            foreach (var d in Dims.Where(d => !d.Hidden && Xf.ContainsKey(d.View)).OrderBy(d => d.Kind is "dia" or "rad" ? 1 : 0).ThenBy(d => d.Arrange && d.Kind is "h" or "v" ? d.Value : 0))
            {
                if (d.Arrange && !ArrangeOff)
                {
                    if (d.Kind is "dia" or "rad") PlaceRadial(g, d);
                    else if (d.Kind is "h" or "v") PlaceRow(g, d, rows);
                    // k161 (замечание 05.11, 01:23, п. 3): место выбирается один раз — после «Автоматических размеров» или
                    // «Размеров модели»; дальше размер стоит, где поставлен (прежде раскладка шла при каждом выводе листа:
                    // перенос размера мышью тормозил). Мутация P3D_ARRANGEONCE=0 — при каждом выводе.
                    if (!ArrangeOnceOff) d.Arrange = false;
                }
                Capture(g, d, () => DrawDim(g, d));
            }
            CentreLabels(g);
            if (Ghost is { } gh && Xf.ContainsKey(gh.View))
            {
                var gg = new SheetGraphics { Width = g.Width, Height = g.Height, ThickW = g.ThickW };
                if (gh.Arrange && gh.Kind is "dia" or "rad") PlaceRadial(gg, gh);
                Capture(gg, gh, () => DrawDim(gg, gh));
                GhostSegs = Hit[gh]; Hit.Remove(gh); HitTexts.Remove(gh);
                g.Ghost = gg;
            }
        }
        foreach (var n in Notes)
        {
            if (!ShowGdt && n.Kind is "gdt" or "datum") continue;
            Capture(g, n, () => DrawNote(g, n, scene));
        }
        return g;
    }

    /// <summary>k159: мутация P3D_DIMARRANGE=0 — размеры без раскладки (ряды и углы выносок как заданы).</summary>
    static readonly bool ArrangeOff = Environment.GetEnvironmentVariable("P3D_DIMARRANGE") == "0";
    static readonly bool LineSOff = Environment.GetEnvironmentVariable("P3D_LINES") == "0";
    static readonly bool ArrangeOnceOff = Environment.GetEnvironmentVariable("P3D_ARRANGEONCE") == "0";

    /// <summary>Отрезки примитивов, добавленных draw(), — геометрия элемента для выбора мышью.</summary>
    void Capture(SheetGraphics g, object key, Action draw)
    {
        int nl = g.Lines.Count, np = g.Polylines.Count, nt = g.Texts.Count, nf = g.Fills.Count, nc = g.Circles.Count;
        draw();
        var segs = new List<(double, double, double, double)>();
        for (int i = nl; i < g.Lines.Count; i++) { var l = g.Lines[i]; segs.Add((l.X1, l.Y1, l.X2, l.Y2)); }
        for (int i = np; i < g.Polylines.Count; i++) { var q = g.Polylines[i].Points; for (int k = 0; k + 1 < q.Count; k++) segs.Add((q[k].X, q[k].Y, q[k + 1].X, q[k + 1].Y)); }
        for (int i = nf; i < g.Fills.Count; i++) { var q = g.Fills[i]; for (int k = 0; k < q.Count; k++) { var b = q[(k + 1) % q.Count]; segs.Add((q[k].X, q[k].Y, b.X, b.Y)); } }
        for (int i = nc; i < g.Circles.Count; i++) { var (cx, cy, r, _) = g.Circles[i]; for (int k = 0; k < 16; k++) { double a0 = k * Math.PI / 8, a1 = (k + 1) * Math.PI / 8; segs.Add((cx + r * Math.Cos(a0), cy + r * Math.Sin(a0), cx + r * Math.Cos(a1), cy + r * Math.Sin(a1))); } }
        // k161 (п. 11): текст — рамкой без диагоналей; попадание в текст — по прямоугольнику (HitTexts)
        var rects = new List<Vector2d[]>();
        for (int i = nt; i < g.Texts.Count; i++)
        {
            var c = TextCorners(g.Texts[i]);
            for (int k = 0; k < 4; k++) segs.Add((c[k].X, c[k].Y, c[(k + 1) % 4].X, c[(k + 1) % 4].Y));
            rects.Add(c);
        }
        Hit[key] = segs;
        HitTexts[key] = rects;
    }

    /// <summary>Углы прямоугольника текста на листе (ширина — оценка по ГОСТ тип А).</summary>
    public static Vector2d[] TextCorners(SheetText t)
    {
        double w = TextW(t.Text, t.Height), h = t.Height;
        double x0 = t.Align == TextAlign.Center ? -w / 2 : t.Align == TextAlign.Right ? -w : 0;
        var o = new Vector2d(t.X, t.Y);
        return new[] { new Vector2d(x0, 0), new Vector2d(x0 + w, 0), new Vector2d(x0 + w, h), new Vector2d(x0, h) }.Select(q => o + Rot(q, t.Angle)).ToArray();
    }
    static (double X0, double Y0, double X1, double Y1) TextBox(SheetText t)
    {
        var c = TextCorners(t);
        return (c.Min(q => q.X), c.Min(q => q.Y), c.Max(q => q.X), c.Max(q => q.Y));
    }
    static bool Overlap((double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b, double gap = 0.5) =>
        a.X0 < b.X1 + gap && b.X0 < a.X1 + gap && a.Y0 < b.Y1 + gap && b.Y0 < a.Y1 + gap;

    /// <summary>Элемент (размер, обозначение) под точкой листа: ближайший отрезок его геометрии не дальше tol мм.</summary>
    public object? HitAt(double x, double y, double tol = 1.5)
    {
        object? best = null; double bd = tol;
        // внутри прямоугольника текста — попадание
        foreach (var (k, rs) in HitTexts)
            foreach (var c in rs)
            {
                var e1 = c[1] - c[0]; var e2 = c[3] - c[0]; var q = new Vector2d(x, y) - c[0];
                double a = Vector2d.Dot(q, e1) / Math.Max(1e-12, e1.LengthSquared), b = Vector2d.Dot(q, e2) / Math.Max(1e-12, e2.LengthSquared);
                if (a >= 0 && a <= 1 && b >= 0 && b <= 1) return k;
            }
        foreach (var (k, segs) in Hit)
            foreach (var (x1, y1, x2, y2) in segs)
            {
                double dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-18 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / l2, 0, 1);
                double d = Math.Sqrt(Math.Pow(x1 + dx * t - x, 2) + Math.Pow(y1 + dy * t - y, 2));
                if (d < bd) { bd = d; best = k; }
            }
        return best;
    }

    /// <summary>
    /// k159: ряд линейного размера: на своей стороне вида — следующий ряд (10, 17, 24 … мм от габарита, меньшие размеры ближе);
    /// если текст ложится на текст другого размера или на соседний вид (между видами 20 мм — там же размеры соседа), — на
    /// противоположную сторону.
    /// </summary>
    void PlaceRow(SheetGraphics g, DrawingDim d, Dictionary<(string, string, int), int> rows)
    {
        var occupied = g.Texts.Where(t => t.Text.Length > 0).Select(TextBox).ToList();
        var others = Boxes.Where(b => b.Key != d.View).Select(b => b.Value).ToList();
        var (fx0, fy0, fx1, fy1) = SheetLayout.FrameRect(Template);
        int pref = d.Offset < 0 ? -1 : 1;
        d.FromBox = true;
        foreach (int side in new[] { pref, -pref })
        {
            int k = rows.GetValueOrDefault((d.View, d.Kind, side));
            d.Offset = side * (10 + 7 * k);
            var g2 = new SheetGraphics { Width = g.Width, Height = g.Height };
            DrawDim(g2, d);
            var rects = g2.Texts.Select(TextBox).ToList();
            bool ok = rects.All(r => r.X0 >= fx0 && r.X1 <= fx1 && r.Y0 >= fy0 && r.Y1 <= fy1)
                      && !rects.Any(r => occupied.Any(o => Overlap(r, o)) || others.Any(o => Overlap(r, o, 0)));
            if (ok) { rows[(d.View, d.Kind, side)] = k + 1; return; }
        }
        int k0 = rows.GetValueOrDefault((d.View, d.Kind, pref));
        d.Offset = pref * (10 + 7 * k0); rows[(d.View, d.Kind, pref)] = k0 + 1;
    }

    /// <summary>
    /// k159: угол выноски диаметра и радиуса — первый из ряда (45°, 135°, 315°, 225°, 30°, 150°, 60°, 120°, …), при котором текст не
    /// накладывается на тексты листа и других размеров, не заходит на чужие виды и остаётся в рамке.
    /// </summary>
    void PlaceRadial(SheetGraphics g, DrawingDim d)
    {
        var occupied = g.Texts.Where(t => t.Text.Length > 0).Select(TextBox).ToList();
        var others = Boxes.Where(b => b.Key != d.View).Select(b => b.Value).ToList();
        var (fx0, fy0, fx1, fy1) = SheetLayout.FrameRect(Template);
        double keep = d.Y2, keepOff = d.Offset;
        double[] angles = { 45, 135, 315, 225, 30, 150, 330, 210, 60, 120, 300, 240, 15, 165, 345, 195, 75, 105, 285, 255 };
        var own = Boxes.TryGetValue(d.View, out var ob) ? ob : default;
        // проход 1: текст и за контуром своего вида (не по линиям детали); проход 2 — допускается на своём виде
        foreach (bool outside in new[] { true, false })
            foreach (var off in new[] { keepOff, 20, 30, 45, 60 })
                foreach (var a in off == keepOff ? angles.Prepend(keep) : angles)
                {
                    d.Y2 = a; d.Offset = off;
                    var g2 = new SheetGraphics { Width = g.Width, Height = g.Height };
                    DrawRadial(g2, d, CaptionOf(d));
                    var rects = g2.Texts.Select(TextBox).ToList();
                    bool ok = rects.All(r => r.X0 >= fx0 && r.X1 <= fx1 && r.Y0 >= fy0 && r.Y1 <= fy1)
                              && !rects.Any(r => occupied.Any(o => Overlap(r, o)) || others.Any(o => Overlap(r, o, 0)) || outside && Overlap(r, own, 0));
                    if (ok) return;
                }
        d.Y2 = keep; d.Offset = keepOff;
    }

    /// <summary>k158: линии обрыва ГОСТ 2.303 — тонкие волнистые на краях полосы разрыва (за контур вида на 2 мм).</summary>
    static void BreakLines(SheetGraphics g, DrawingView v, double s, double y0, double y1, Func<Vector2d, (double X, double Y)> tf)
    {
        // в координатах вида с разрывом (мм модели): края промежутка, за контур на 2 мм листа, волна 1.2 мм с шагом 12 мм листа
        foreach (double x in new[] { v.BreakA, v.BreakA + v.BreakGap / s })
        {
            var pts = new List<(double X, double Y)>();
            double ya = y0 - 2 / s, yb = y1 + 2 / s; int n = Math.Max(8, (int)((yb - ya) * s / 1.5));
            for (int i = 0; i <= n; i++) { double y = ya + (yb - ya) * i / n; pts.Add(tf(new Vector2d(x + 1.2 / s * Math.Sin((y - ya) * s / 6 * Math.PI), y))); }
            g.Polylines.Add(new SheetPolyline(pts, LineKind.Thin));
        }
    }

    /// <summary>Направление вспомогательного вида на виде-родителе (лист): от наклонной линии к наблюдателю.</summary>
    public Vector2d AuxDir(DrawingView aux)
    {
        var pf = ViewFrame.Of(DirOf(Views.First(x => x.Name == aux.Parent))); var af = ViewFrame.Of(DirOf(aux));
        var n2 = new Vector2d(Vector3d.Dot(af.Toward, pf.Right), Vector3d.Dot(af.Toward, pf.Up));
        return n2.Length < 1e-9 ? Vector2d.UnitY : n2.Normalized();
    }

    /// <summary>k158: стрелка вспомогательного вида ГОСТ 2.305 п. 6.2: перпендикулярно наклонной линии, к ней; буква у стрелки.</summary>
    void AuxArrow(SheetGraphics g, DrawingView aux)
    {
        var n2 = DirToSheet(aux.Parent!, AuxDir(aux));
        var (mx, my) = ToSheet(aux.Parent!, new Vector2d(aux.DetailX, aux.DetailY));
        var tip = (mx + n2.X * 6, my + n2.Y * 6); var tail = (mx + n2.X * 20, my + n2.Y * 20);
        g.Line(tail.Item1, tail.Item2, tip.Item1 + n2.X * ArrowL, tip.Item2 + n2.Y * ArrowL);
        Arrow(g, tip, (-n2.X, -n2.Y));
        var side = new Vector2d(-n2.Y, n2.X);
        g.Text(tail.Item1 + side.X * 4, tail.Item2 + side.Y * 4 - 3.5, 7, aux.Label, TextAlign.Center);
    }

    /// <summary>Линия разреза ГОСТ 2.305: разомкнутая (толстые штрихи 10 мм за контуром вида), стрелки по направлению взгляда, буквы.</summary>
    void CutLine(SheetGraphics g, DrawingView sec)
    {
        var parent = Views.First(x => x.Name == sec.Parent);
        var pf = ViewFrame.Of(DirOf(parent)); var sf = ViewFrame.Of(DirOf(sec));
        var n2 = new Vector2d(Vector3d.Dot(sf.Toward, pf.Right), Vector3d.Dot(sf.Toward, pf.Up));   // нормаль плоскости на виде-родителе
        if (n2.Length < 1e-9) return;
        double nl = n2.Length; n2 /= nl;
        double c = sec.CutDepth / nl;
        var dir = new Vector2d(-n2.Y, n2.X);
        var (bx0, by0, bx1, by1) = Boxes[parent.Name];
        var (mid, s, X, Y) = Xf[parent.Name];
        // концы линии — по габариту вида (лист), выход 3 мм
        var lineAt = n2 * c;
        var corners = new[] { ToView(parent.Name, bx0, by0), ToView(parent.Name, bx1, by0), ToView(parent.Name, bx1, by1), ToView(parent.Name, bx0, by1) };
        double t0 = corners.Min(q => Vector2d.Dot(q - lineAt, dir)) - 3 / s, t1 = corners.Max(q => Vector2d.Dot(q - lineAt, dir)) + 3 / s;
        var vdir = dir;
        { var rn = DirToSheet(parent.Name, n2); var rd = DirToSheet(parent.Name, dir); n2 = rn; dir = rd; }
        foreach (var (te, sgn) in new[] { (t0, 1.0), (t1, -1.0) })
        {
            var e0 = ToSheet(parent.Name, lineAt + vdir * te); var e1 = ToSheet(parent.Name, lineAt + vdir * (te + sgn * 10 / s));
            g.Line(e0.X, e0.Y, e1.X, e1.Y, LineKind.Thick);
            // стрелка у внешнего конца: перпендикулярно, по взгляду разреза (от наблюдателя): -n2
            var tip = (e0.X - n2.X * 8, e0.Y - n2.Y * 8);
            var basePt = (e0.X - n2.X * 2, e0.Y - n2.Y * 2);
            g.Line(e0.X - n2.X * 0.5, e0.Y - n2.Y * 0.5, basePt.Item1, basePt.Item2);
            g.Line(basePt.Item1, basePt.Item2, tip.Item1, tip.Item2);
            Arrow(g, tip, (-n2.X, -n2.Y));
            g.Text(tip.Item1 + dir.X * sgn * -4, tip.Item2 + dir.Y * sgn * -4 - 3.5, 7, sec.Label, TextAlign.Center);
        }
    }

    // ------------------------------------------------------------------ размеры ГОСТ 2.307

    const double ArrowL = 3, ArrowW = 0.5, TextH = 3.5;
    static double TextW(string t, double h) => t.Length * h * 0.62;

    /// <summary>Залитая стрелка: остриё в tip, направление острия dir (единичный вектор листа).</summary>
    static void Arrow(SheetGraphics g, (double X, double Y) tip, (double X, double Y) dir)
    {
        double l = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y); if (l < 1e-12) return;
        double ux = dir.X / l, uy = dir.Y / l, nx = -uy, ny = ux;
        double bx = tip.X - ux * ArrowL, by = tip.Y - uy * ArrowL;
        g.Fills.Add(new() { tip, (bx + nx * ArrowW, by + ny * ArrowW), (bx - nx * ArrowW, by - ny * ArrowW) });
    }

    // ------------------------------------------------------------------ k167: допуски размера

    static readonly bool TolStackOff = Environment.GetEnvironmentVariable("P3D_TOLSTACK") == "0";

    static double? TolNum(string s) => double.TryParse(s.Trim().Replace(',', '.').Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Отклонения размера: (±-часть к тексту, верхнее, нижнее) — подписи со знаками; пусто — нет.</summary>
    (string Pm, string? Up, string? Lo) TolParts(DrawingDim d)
    {
        if (TolStackOff) return ("", null, null);
        double? up = d.TolUp.Trim().Length > 0 ? TolNum(d.TolUp) : null, lo = d.TolLo.Trim().Length > 0 ? TolNum(d.TolLo) : null;
        // нижнее без знака — отклонение вниз
        if (lo is { } l0 && !d.TolLo.Trim().StartsWith("+") && !d.TolLo.Trim().StartsWith("-") && !d.TolLo.Trim().StartsWith("−")) lo = -Math.Abs(l0);
        string F(double v) { var t = Math.Abs(v).ToString("0.####", CultureInfo.InvariantCulture); if (Comma) t = t.Replace('.', ','); return t; }
        string S(double v) => v == 0 ? "0" : (v > 0 ? "+" : "−") + F(v);
        if (up is { } u && lo is { } l && Math.Abs(u + l) < 1e-12 && u > 0) return (" ±" + F(u), null, null);
        return ("", up is { } uu ? S(uu) : null, lo is { } ll ? S(ll) : null);
    }

    /// <summary>k167: ширина подписи размера с отклонениями (мм листа).</summary>
    double DimTextW(DrawingDim d, string text)
    {
        var (pm, up, lo) = TolParts(d);
        double w = TextW(text + pm, TextH);
        if (up != null || lo != null) w += 0.6 + TextW((up ?? "").Length >= (lo ?? "").Length ? up ?? "" : lo ?? "", TolH);
        return w;
    }
    const double TolH = 2.5;
    static readonly bool TolGapOff = Environment.GetEnvironmentVariable("P3D_TOLGAP") == "0";

    /// <summary>
    /// k167: подпись размера с отклонениями: m — середина подписи (над размерной линией), ang — угол текста (градусы листа).
    /// Верхнее отклонение — над серединой высоты основного текста, нижнее — у его основания (ГОСТ 2.307 п. 1.20).
    /// </summary>
    void DimText(SheetGraphics g, DrawingDim d, Vector2d m, double ang, string text)
    {
        var (pm, up, lo) = TolParts(d);
        string main = text + pm;
        if (up == null && lo == null) { g.Text(m.X, m.Y, TextH, main, TextAlign.Center, null, Math.Abs(ang) < 1e-9 ? 0 : ang); return; }
        double a = ang * Math.PI / 180; var along = new Vector2d(Math.Cos(a), Math.Sin(a)); var upv = new Vector2d(-along.Y, along.X);
        double w = TextW(main, TextH), tw = TextW((up ?? "").Length >= (lo ?? "").Length ? up ?? "" : lo ?? "", TolH), total = w + 0.6 + tw;
        var cm = m - along * (total / 2 - w / 2);
        g.Text(cm.X, cm.Y, TextH, main, TextAlign.Center, null, Math.Abs(ang) < 1e-9 ? 0 : ang);
        var t0 = cm + along * (w / 2 + 0.6);
        double ta = Math.Abs(ang) < 1e-9 ? 0 : ang;
        // k170 (замечание 05.11, 18:34, п. 4): нижнее — на линии основания основного текста (прежде на 0.15 мм ниже — касалось
        // полки), верхнее — над нижним с промежутком 0.8 мм (прежде на 0.55 высоты текста — строки пересекались). Мутация
        // P3D_TOLGAP=0 — прежнее.
        double upY = TolGapOff ? TextH * 0.55 : TolH + 0.8, loY = TolGapOff ? -0.15 : 0;
        if (up != null) { var p = t0 + upv * upY; g.Text(p.X, p.Y, TolH, up, TextAlign.Left, null, ta); }
        if (lo != null) { var p = t0 + upv * loY; g.Text(p.X, p.Y, TolH, lo, TextAlign.Left, null, ta); }
    }

    void DrawDim(SheetGraphics g, DrawingDim d)
    {
        string text = CaptionOf(d);
        if (d.Kind is "dia" or "rad") { DrawRadial(g, d, text); return; }
        if (d.Kind == "ang") { DrawAngle(g, d, text); return; }
        var (P1, P2, u, n, h1, h2, L) = DimFrame(d);
        var q1 = P1 + n * (L - h1); var q2 = P2 + n * (L - h2);
        double sg = Math.Sign(d.Offset == 0 ? 1 : d.Offset);
        // выносные линии: от точки до размерной + 2 мм за неё
        // k164 (замечание 05.11, 10:33, п. 3): выносная от центра — продолжение осевой (штрихпунктир от центра), участки, лежащие
        // на осевой, не рисуются: осевая видна, не сливается со сплошной
        ExtLine(g, d.View, P1, n * sg, q1 + n * sg * 2, Math.Abs(L - h1) > 0.5);
        ExtLine(g, d.View, P2, n * sg, q2 + n * sg * 2, Math.Abs(L - h2) > 0.5);
        double len = (q2 - q1).Length;
        var along = len > 1e-9 ? (q2 - q1) / len : u;
        bool outside = d.Arrows == 1 ? false : d.Arrows == 2 || len < 2 * ArrowL + 4;
        if (outside)
        {
            SolidLine(g, d.View, q1 - along * 8, q2 + along * 8, LineKind.Thin);
            Arrow(g, (q1.X, q1.Y), (along.X, along.Y)); Arrow(g, (q2.X, q2.Y), (-along.X, -along.Y));
        }
        else
        {
            SolidLine(g, d.View, q1, q2, LineKind.Thin);
            Arrow(g, (q1.X, q1.Y), (-along.X, -along.Y)); Arrow(g, (q2.X, q2.Y), (along.X, along.Y));
        }
        // текст над размерной линией, по её направлению (читаемо: угол в (−90°, 90°])
        double ang = Math.Atan2(along.Y, along.X) * 180 / Math.PI;
        var up = new Vector2d(-along.Y, along.X);
        if (ang > 90.0001 || ang <= -90) { ang += ang > 0 ? -180 : 180; up = -up; }
        var m = (q1 + q2) / 2 + up * 1;
        if (d.TextShift == 0 && outside && len < DimTextW(d, text)) m += (q2 - q1).Length > 1e-9 ? along * (len / 2 + 8 + DimTextW(d, text) / 2) : Vector2d.Zero;
        if (d.TextShift != 0)
        {
            // k161: текст сдвинут вдоль линии; за концом — продление размерной линии под текст (полка)
            double t = d.TextShift * (Vector2d.Dot(along, u) >= 0 ? 1 : -1);
            m += along * t;
            double tw = DimTextW(d, text);
            var mid = (q1 + q2) / 2;
            if (Math.Abs(t) + tw / 2 > len / 2)
            {
                var end = t > 0 ? q2 : q1;
                var far = mid + along * (t + Math.Sign(t) * (tw / 2 + 1));
                g.Line(end.X, end.Y, far.X, far.Y);
            }
        }
        DimText(g, d, m, ang, text);
    }

    /// <summary>
    /// Линейный размер на листе: точки, направление размерной линии u (h/v — по осям вида, с поворотом), нормаль n, высоты точек
    /// и положение размерной линии L по нормали. Отступ — от дальней точки или (FromBox) от габарита вида.
    /// </summary>
    public (Vector2d P1, Vector2d P2, Vector2d U, Vector2d N, double H1, double H2, double L) DimFrame(DrawingDim d)
    {
        var p1 = ToSheet(d.View, new Vector2d(d.X1, d.Y1)); var p2 = ToSheet(d.View, new Vector2d(d.X2, d.Y2));
        Vector2d P1 = new(p1.X, p1.Y), P2 = new(p2.X, p2.Y);
        Vector2d u = d.Kind == "h" ? DirToSheet(d.View, Vector2d.UnitX) : d.Kind == "v" ? DirToSheet(d.View, Vector2d.UnitY) : (P2 - P1).Length > 1e-9 ? (P2 - P1).Normalized() : Vector2d.UnitX;
        var n = new Vector2d(-u.Y, u.X);
        double h1 = Vector2d.Dot(P1, n), h2 = Vector2d.Dot(P2, n);
        double hi = Math.Max(h1, h2), lo = Math.Min(h1, h2);
        if (d.FromBox && Boxes.TryGetValue(d.View, out var b))
        {
            var hs = new[] { new Vector2d(b.X0, b.Y0), new Vector2d(b.X1, b.Y0), new Vector2d(b.X1, b.Y1), new Vector2d(b.X0, b.Y1) }.Select(q => Vector2d.Dot(q, n)).ToList();
            hi = hs.Max(); lo = hs.Min();
        }
        double L = (d.Offset >= 0 ? hi : lo) + d.Offset;
        return (P1, P2, u, n, h1, h2, L);
    }

    /// <summary>k159: перенос размера мышью на (dx, dy) мм листа: линейный — размерная линия по нормали, диаметр и радиус — угол выноски.</summary>
    public void MoveDim(DrawingDim d, double dx, double dy, double sx, double sy)
    {
        d.Arrange = false;
        if (d.Kind == "ang")
        {
            // k163: угловой — радиус дуги до курсора; k170 (замечание 05.11, 18:34, п. 6.3): и угол подписи — к курсору (подпись
            // в любом месте листа: за сторонами угла дуга продлевается до подписи). TextShift — поворот подписи от середины дуги,
            // градусы. Мутация P3D_ANGTEXTFREE=0 — только радиус (подпись над серединой дуги).
            var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
            d.Offset = Math.Max(5, Math.Sqrt((sx - c.X) * (sx - c.X) + (sy - c.Y) * (sy - c.Y)));
            if (!AngTextFreeOff)
            {
                double rot = Views.FirstOrDefault(v => v.Name == d.View)?.Angle ?? 0;
                double mid = d.X2 + rot + d.Value / 2, at = Math.Atan2(sy - c.Y, sx - c.X) * 180 / Math.PI;
                d.TextShift = ((at - mid) % 360 + 540) % 360 - 180;
                if (Math.Abs(d.TextShift) < d.Value / 2 * 0.15) d.TextShift = 0;   // у середины — на середину
            }
            return;
        }
        if (d.Kind is "dia" or "rad")
        {
            var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
            d.Y2 = Math.Atan2(sy - c.Y, sx - c.X) * 180 / Math.PI;
            // k161: длина выноски — до курсора (без ограничения): Offset = выноска / 0.6
            double r = d.X2 * Xf[d.View].S, dist = Math.Sqrt((sx - c.X) * (sx - c.X) + (sy - c.Y) * (sy - c.Y));
            if (dist > r + 1) d.Offset = Math.Max(2, dist - r) / 0.6;
            return;
        }
        var (_, _, u0, n, h1, h2, L) = DimFrame(d);
        // k161: вдоль размерной линии — сдвиг текста (в том числе за концы — сбоку от размера)
        d.TextShift += dx * u0.X + dy * u0.Y;
        double Ln = L + dx * n.X + dy * n.Y;
        double hi = Math.Max(h1, h2), lo = Math.Min(h1, h2);
        if (d.FromBox && Boxes.TryGetValue(d.View, out var b))
        {
            var hs = new[] { new Vector2d(b.X0, b.Y0), new Vector2d(b.X1, b.Y0), new Vector2d(b.X1, b.Y1), new Vector2d(b.X0, b.Y1) }.Select(q => Vector2d.Dot(q, n)).ToList();
            hi = hs.Max(); lo = hs.Min();
        }
        if (Ln >= hi) d.Offset = Ln - hi;
        else if (Ln <= lo) d.Offset = Ln - lo;
        else if (d.FromBox) { d.FromBox = false; d.Offset = Ln >= Math.Max(h1, h2) ? Ln - Math.Max(h1, h2) : Ln <= Math.Min(h1, h2) ? Ln - Math.Min(h1, h2) : (d.Offset >= 0 ? 0 : -0.0001); }
        else d.Offset = d.Offset >= 0 ? 0 : -0.0001;
    }

    /// <summary>
    /// k163: угловой размер ГОСТ 2.307 п. 2.4: дуга с центром в вершине угла радиусом Offset мм листа (по умолчанию 15),
    /// стрелки на концах дуги, текст — над серединой дуги (k170 — или там, куда перенесён: TextShift, градусы от середины).
    /// k170 (замечание 05.11, 18:34, п. 6.1): ВЫНОСНЫЕ ЛИНИИ — ОТ СТОРОН УГЛА, А НЕ ОТ ВЕРШИНЫ: от ближнего к дуге конца
    /// отрезка контура на этой стороне (отрезки вида на луче стороны) до дуги + 2 мм; дуга между концами отрезка — выносной нет.
    /// Прежде линии шли от вершины через всю деталь и выходили по ту сторону измеряемых линий. Сторона без отрезка контура
    /// (продолжение линии за вершину, «смежный» угол) — от ближнего конца отрезка на продолжении через вершину. Мутация
    /// P3D_ANGEXT=0 — от вершины (прежнее).
    /// </summary>
    void DrawAngle(SheetGraphics g, DrawingDim d, string text)
    {
        var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
        double rot = Views.FirstOrDefault(v => v.Name == d.View)?.Angle ?? 0;
        double a0 = (d.X2 + rot) * Math.PI / 180, sweep = d.Value * Math.PI / 180;
        double r = d.Offset > 0 ? d.Offset : 15;
        double s = Xf.TryGetValue(d.View, out var xf) ? xf.S : 1;
        (double, double) At(double a, double rr) => (c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a));
        for (int side = 0; side < 2; side++)
        {
            double a = side == 0 ? a0 : a0 + sweep, va = (side == 0 ? d.X2 : d.X2 + d.Value) * Math.PI / 180;
            var range = AngExtOff ? null : RayRange(d.View, new Vector2d(d.X1, d.Y1), new Vector2d(Math.Cos(va), Math.Sin(va)));
            if (range is not { } rg) { var (x, y) = At(a, r + 2); g.Line(c.X, c.Y, x, y); continue; }
            double t0 = rg.Min * s, t1 = rg.Max * s;
            if (r > t1) { var (x0, y0) = At(a, t1); var (x, y) = At(a, r + 2); g.Line(x0, y0, x, y); }
            else if (r < t0) { var (x0, y0) = At(a, Math.Max(0, r - 2)); var (x, y) = At(a, t0); g.Line(x0, y0, x, y); }
        }
        // подпись: середина дуги или перенесённая (TextShift — градусы от середины); за сторонами — дуга продлевается
        double am = a0 + sweep / 2 + (AngTextFreeOff ? 0 : d.TextShift * Math.PI / 180);
        double e0 = Math.Min(a0, am - 0.06), e1 = Math.Max(a0 + sweep, am + 0.06);
        if (AngTextFreeOff || Math.Abs(d.TextShift) < 1e-9) { e0 = a0; e1 = a0 + sweep; }
        int n = Math.Max(8, (int)((e1 - e0) * r / 1.5));
        var pts = Enumerable.Range(0, n + 1).Select(k => At(e0 + (e1 - e0) * k / n, r)).ToList();
        g.Polylines.Add(new SheetPolyline(pts, LineKind.Thin));
        Arrow(g, At(a0, r), (Math.Sin(a0), -Math.Cos(a0)));
        Arrow(g, At(a0 + sweep, r), (-Math.Sin(a0 + sweep), Math.Cos(a0 + sweep)));
        var (tx, ty) = At(am, r + 1.5);
        double ta = am * 180 / Math.PI - 90; ta = ((ta % 360) + 360) % 360; if (ta > 90 && ta < 270) ta -= 180;
        DimText(g, d, new Vector2d(tx, ty), ta, text);
    }
    static readonly bool AngExtOff = Environment.GetEnvironmentVariable("P3D_ANGEXT") == "0";
    static readonly bool AngTextFreeOff = Environment.GetEnvironmentVariable("P3D_ANGTEXTFREE") == "0";

    /// <summary>
    /// k170: отрезки контура вида на прямой «вершина + t·u» (до 0.02 мм модели от прямой): промежуток t (мм модели), ближайший к
    /// стороне угла (t ≥ 0) — иначе на продолжении за вершину; нет отрезков — null.
    /// </summary>
    public (double Min, double Max)? RayRange(string view, Vector2d vx, Vector2d u)
    {
        if (!ViewSegs.TryGetValue(view, out var segs)) return null;
        double tol = 0.02;
        var pos = new List<(double, double)>(); var neg = new List<(double, double)>();
        foreach (var (A, B) in segs)
        {
            var ra = A - vx; var rb = B - vx;
            if (Math.Abs(ra.X * u.Y - ra.Y * u.X) > tol || Math.Abs(rb.X * u.Y - rb.Y * u.X) > tol) continue;
            double ta = Vector2d.Dot(ra, u), tb = Vector2d.Dot(rb, u);
            if (Math.Abs(ta - tb) < 1e-9) continue;
            var iv = (Math.Min(ta, tb), Math.Max(ta, tb));
            if (iv.Item2 > 1e-6) pos.Add(iv); else neg.Add(iv);
        }
        var use = pos.Count > 0 ? pos : neg;
        if (use.Count == 0) return null;
        return (use.Min(q => q.Item1), use.Max(q => q.Item2));
    }

    /// <summary>
    /// k170 (замечание 05.11, 18:34, п. 6.2): угловой размер — внутренний ↔ внешний (360° − угол, дуга с другой стороны вершины)
    /// и смежный угол (180° − угол: сторона — продолжение за вершину; два раза подряд — вертикальный угол).
    /// </summary>
    public static void AngleOuter(DrawingDim d) { if (d.Kind != "ang") return; (d.X2, d.Y2) = (d.Y2, d.X2); d.TextShift = 0; }
    public static void AngleAdjacent(DrawingDim d) { if (d.Kind != "ang") return; (d.X2, d.Y2) = (d.Y2, d.X2 + 180); d.TextShift = 0; }

    // ------------------------------------------------------------------ k164: осевые линии в приоритете, номера центров

    static readonly bool CenterPrioOff = Environment.GetEnvironmentVariable("P3D_CENTERPRIO") == "0";
    static readonly bool CenterNosOff = Environment.GetEnvironmentVariable("P3D_CENTERNOS") == "0";
    static readonly bool MarksOnceOff = Environment.GetEnvironmentVariable("P3D_CENTERMARKSONCE") == "0";

    bool IsCentre(string view, Vector2d p) => CenterPts.TryGetValue(view, out var pts) && pts.Any(q => (q - p).Length < 0.05);

    /// <summary>
    /// k164: выносная линия от точки P (смещение gap — отступ от точки) до end. От центра (окружности, дуги) — штрихпунктирная от
    /// самого центра: продолжение осевой (ГОСТ 2.307 п. 1.6: размер от оси — выносная — продолжение осевой). Участки на осевых
    /// линии не рисуются. Мутация P3D_CENTERPRIO=0 — сплошная поверх осевой (прежнее).
    /// </summary>
    void ExtLine(SheetGraphics g, string view, Vector2d P, Vector2d gap, Vector2d end, bool draw)
    {
        if (CenterPrioOff) { if (draw) g.Line(P.X + gap.X, P.Y + gap.Y, end.X, end.Y); return; }
        bool centre = IsCentre(view, P);
        if (!draw && !centre) return;
        if (centre) SolidLine(g, view, P, end, LineKind.Center);
        else SolidLine(g, view, P + gap, end, LineKind.Thin);
    }

    /// <summary>k164: отрезок a–b без участков, лежащих на осевых линиях вида (там видна осевая).</summary>
    void SolidLine(SheetGraphics g, string view, Vector2d a, Vector2d b, LineKind kind)
    {
        var ab = b - a; double len = ab.Length;
        if (len < 1e-9) return;
        if (CenterPrioOff) { g.Line(a.X, a.Y, b.X, b.Y); return; }
        var cs = CenterSegs.GetValueOrDefault(view) ?? new();
        var u = ab / len;
        var cover = new List<(double S, double E)>();
        foreach (var (p, q) in cs)
        {
            var pq = q - p; double l = pq.Length;
            if (l < 1e-9) continue;
            var v = pq / l;
            if (Math.Abs(u.X * v.Y - u.Y * v.X) > 1e-3) continue;
            var ap = p - a;
            if (Math.Abs(ap.X * u.Y - ap.Y * u.X) > 0.05) continue;
            double t1 = Vector2d.Dot(p - a, u), t2 = Vector2d.Dot(q - a, u);
            cover.Add((Math.Min(t1, t2), Math.Max(t1, t2)));
        }
        double t = 0;
        foreach (var (cs0, ce) in cover.OrderBy(c => c.S))
        {
            if (ce <= t) continue;
            if (cs0 > t + 0.3) { var x0 = a + u * t; var x1 = a + u * Math.Min(cs0, len); if (Math.Min(cs0, len) > t) Seg(x0, x1); }
            t = Math.Max(t, ce);
            if (t >= len) return;
        }
        if (len - t > 0.3) Seg(a + u * t, b);
        void Seg(Vector2d x0, Vector2d x1)
        {
            if (kind == LineKind.Center) g.Polylines.Add(new SheetPolyline(new() { (x0.X, x0.Y), (x1.X, x1.Y) }, LineKind.Center));
            else g.Line(x0.X, x0.Y, x1.X, x1.Y, kind);
        }
    }

    /// <summary>
    /// k164 (замечание 05.11, 10:33, п. 5): номера центров, к которым относятся размеры: центры диаметров и радиусов и концы
    /// линейных размеров в центрах. k165 (замечание 05.11, 13:27, п. 7): номер — у ОСИ В ПРОСТРАНСТВЕ (прямая через центр
    /// вдоль взгляда вида), один на всех видах и разрезах: прежде нумерация шла по видам, и у одного центра на виде спереди и
    /// на разрезе А–А номера были разные (1 и 7). Порядок: сначала основные виды по списку, затем разрезы и выносные; на
    /// первом виде — главный центр (у наибольшей окружности или дуги), затем сверху вниз, слева направо. Мутация
    /// P3D_CENTERNOS3D=0 — по видам (прежнее).
    /// </summary>
    void NumberCentres()
    {
        var keys = new List<(Vector3d D, Vector3d Foot)>();
        double tol = 1e-4 * Math.Max(1, Boxes.Values.Select(b => Math.Max(b.X1 - b.X0, b.Y1 - b.Y0)).DefaultIfEmpty(1).Max());
        int Key(DrawingView v, Vector2d pv)
        {
            var f = ViewFrame.Of(DirOf(v));
            var d = f.Toward;
            if (d.X < -1e-9 || Math.Abs(d.X) <= 1e-9 && (d.Y < -1e-9 || Math.Abs(d.Y) <= 1e-9 && d.Z < 0)) d = -d;
            var P = f.Right * pv.X + f.Up * pv.Y;
            var foot = P - d * Vector3d.Dot(P, d);
            for (int i = 0; i < keys.Count; i++)
                if (Vector3d.Cross(keys[i].D, d).Length < 1e-6 && (keys[i].Foot - foot).Length < tol) return i;
            keys.Add((d, foot)); return keys.Count - 1;
        }
        var all = new List<(string View, Vector2d P, Vector2d PV, int Key)>();
        int byView = 0;
        foreach (var v in Views.OrderBy(v => v.IsSection || v.IsDetail || v.IsAux ? 1 : 0))
        {
            if (!CenterPts.TryGetValue(v.Name, out var pts) || pts.Count == 0) continue;
            var ptsV = CenterPtsView[v.Name];
            var used = new List<(Vector2d P, Vector2d PV, double R)>();
            void Use(Vector2d p, double r)
            {
                int ci = pts.FindIndex(q => (q - p).Length < 0.05);
                if (ci < 0) return;
                var c = pts[ci];
                int i = used.FindIndex(u => (u.P - c).Length < 0.05);
                if (i < 0) used.Add((c, ptsV[ci], r)); else if (r > used[i].R) used[i] = (c, ptsV[ci], r);
            }
            foreach (var d in Dims.Where(d => !d.Hidden && d.View == v.Name))
            {
                if (d.Kind is "dia" or "rad") { var (x, y) = ToSheet(d.View, new Vector2d(d.X1, d.Y1)); Use(new(x, y), d.X2); }
                else if (d.Kind is "h" or "v" or "al")
                {
                    var (x1, y1) = ToSheet(d.View, new Vector2d(d.X1, d.Y1)); var (x2, y2) = ToSheet(d.View, new Vector2d(d.X2, d.Y2));
                    Use(new(x1, y1), 0); Use(new(x2, y2), 0);
                }
            }
            if (used.Count == 0) continue;
            var main = used.OrderByDescending(u => u.R).First();
            foreach (var u in new[] { main }.Concat(used.Where(u => u != main).OrderByDescending(u => Math.Round(u.P.Y, 2)).ThenBy(u => u.P.X)))
                all.Add((v.Name, u.P, u.PV, CenterNos3dOff ? byView++ : Key(v, u.PV)));
        }
        if (all.Select(a => a.Key).Distinct().Count() < 2) return;   // один центр — нумеровать нечего
        foreach (var a in all) CenterNos.Add((a.View, a.P, a.Key + 1, a.PV));
    }
    static readonly bool CenterNos3dOff = Environment.GetEnvironmentVariable("P3D_CENTERNOS3D") == "0";

    int? CentreNo(string view, Vector2d p)
    {
        foreach (var c in CenterNos) if (c.View == view && (c.P - p).Length < 0.05) return c.No;
        return null;
    }

    static double SignW(int no, double h) => 1.0 * h + TextW(no.ToString(CultureInfo.InvariantCulture), h);

    /// <summary>k164: знак центра «℄» (буквы C и L одна на другой) и номер: x, y — левый нижний угол, h — высота шрифта.</summary>
    static void CenterSign(SheetGraphics g, double x, double y, double h, int no)
    {
        double cx = x + 0.42 * h, cy = y + 0.5 * h, r = 0.42 * h;
        var arc = Enumerable.Range(0, 17).Select(k => { double a = (50 + 260.0 * k / 16) * Math.PI / 180; return (cx + r * Math.Cos(a), cy + r * Math.Sin(a)); }).ToList();
        g.Polylines.Add(new SheetPolyline(arc, LineKind.Thin));
        g.Line(cx, y + h, cx, y); g.Line(cx, y, cx + 0.5 * h, y);
        g.Text(x + 1.0 * h, y, h, no.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// k164: подписи центров «℄N» с выноской от центра: направление из ряда 315°, 225°, 45°, 135°, 330°, 210° … (не по осям),
    /// длина 5…14 мм — первое место, где подпись не ложится на тексты листа и на другие подписи.
    /// </summary>
    void CentreLabels(SheetGraphics g)
    {
        double h = TextH;
        var boxes = g.Texts.Select(t => TextCorners(t)).Select(c => (X0: c.Min(q => q.X), Y0: c.Min(q => q.Y), X1: c.Max(q => q.X), Y1: c.Max(q => q.Y))).ToList();
        // линии листа (кроме осевых): подпись не ставится на линию контура или размера
        var segs = new List<(double X1, double Y1, double X2, double Y2)>();
        foreach (var pl in g.Polylines) if (pl.Kind != LineKind.Center) for (int i = 0; i + 1 < pl.Points.Count; i++) segs.Add((pl.Points[i].X, pl.Points[i].Y, pl.Points[i + 1].X, pl.Points[i + 1].Y));
        foreach (var l in g.Lines) if (l.Kind != LineKind.Center) segs.Add((l.X1, l.Y1, l.X2, l.Y2));
        static bool Cross((double X1, double Y1, double X2, double Y2) sg, (double, double, double, double) b)
        {
            // отсечение Лианга — Барски
            double t0 = 0, t1 = 1, dx = sg.X2 - sg.X1, dy = sg.Y2 - sg.Y1;
            foreach (var (pp, qq) in new[] { (-dx, sg.X1 - b.Item1), (dx, b.Item3 - sg.X1), (-dy, sg.Y1 - b.Item2), (dy, b.Item4 - sg.Y1) })
            {
                if (Math.Abs(pp) < 1e-12) { if (qq < 0) return false; continue; }
                double r = qq / pp;
                if (pp < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return true;
        }
        foreach (var (view, p, no, pv) in CenterNos)
        {
            double w = SignW(no, h);
            (double, double, double, double)? best = null; (Vector2d, Vector2d) lead = default; double bestScore = double.MaxValue;
            // k165 (замечание 05.11, 13:27, п. 1): место подписи — подобранное раньше или перенесённое мышью; подбор — один раз
            double mtol = 1e-6 * Math.Max(1, pv.Length);
            var mark = CentreMarks.FirstOrDefault(m => m.View == view && Math.Abs(m.CX - pv.X) < mtol && Math.Abs(m.CY - pv.Y) < mtol);
            if (mark is { Placed: true } && !MarksOnceOff)
            {
                var e = p + new Vector2d(mark.DX, mark.DY);
                double x0 = mark.DX >= 0 ? e.X : e.X - w, y0 = e.Y + 0.6;
                best = (x0, y0, x0 + w, y0 + h); lead = (p, e); bestScore = 0;
            }
            else
            // место с наименьшим числом наложений: текст — 100, пересечённая линия — 1, длина выноски — 0.05 на мм; свободное
            // место ближе к центру — сразу
            foreach (double len in new[] { 5.0, 8.0, 11.0, 14.0, 18.0, 24.0 })
            {
                foreach (double deg in new[] { -45.0, -135, 45, 135, -30, -150, 30, 150, -60, -120, 60, 120 })
                {
                    double a = deg * Math.PI / 180;
                    var e = p + new Vector2d(Math.Cos(a), Math.Sin(a)) * len;
                    bool right = Math.Cos(a) >= -1e-9;
                    double x0 = right ? e.X : e.X - w, y0 = e.Y + 0.6;
                    var bx = (x0 - 0.3, y0 - 0.3, x0 + w + 0.3, y0 + h + 0.3);
                    int tx = boxes.Count(b => b.X0 < bx.Item3 && bx.Item1 < b.X1 && b.Y0 < bx.Item4 && bx.Item2 < b.Y1);
                    int sx = segs.Count(sg => Math.Max(sg.X1, sg.X2) >= bx.Item1 && Math.Min(sg.X1, sg.X2) <= bx.Item3 && Math.Max(sg.Y1, sg.Y2) >= bx.Item2 && Math.Min(sg.Y1, sg.Y2) <= bx.Item4 && Cross(sg, bx));
                    double score = 100 * tx + sx + 0.05 * len;
                    if (score < bestScore) { bestScore = score; best = (x0, y0, x0 + w, y0 + h); lead = (p, e); }
                }
                if (bestScore < 1) break;
            }
            if (mark == null) CentreMarks.Add(mark = new CentreMark { View = view, CX = pv.X, CY = pv.Y });
            mark.DX = lead.Item2.X - p.X; mark.DY = lead.Item2.Y - p.Y; mark.Placed = true;
            var (bx0, by0, bx1, by1) = best!.Value;
            var leadF = lead;
            Capture(g, mark, () =>
            {
                SolidLine(g, view, leadF.Item1, leadF.Item2, LineKind.Thin);
                // полка под подписью — выноска читается (как в ASME Y14.5: «℄N» на полке)
                g.Line(leadF.Item2.X, leadF.Item2.Y, Math.Abs(leadF.Item2.X - bx0) < 1e-9 ? bx1 : bx0, leadF.Item2.Y);
                CenterSign(g, bx0, by0, h, no);
            });
            boxes.Add((bx0, by0, bx1, by1));
        }
    }

    void DrawRadial(SheetGraphics g, DrawingDim d, string text)
    {
        var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
        double r = d.X2 * Xf[d.View].S, a = d.Y2 * Math.PI / 180;
        var dir = (Math.Cos(a), Math.Sin(a));
        var p = (c.X + r * dir.Item1, c.Y + r * dir.Item2);
        double tw = DimTextW(d, text);
        // k165: у пронумерованного центра диаметр — на полке с «℄N» (как на чертеже-образце), а не линией через центр
        bool numbered = !CenterNosOff && CentreNo(d.View, new Vector2d(c.X, c.Y)) != null;
        if (d.Kind == "dia" && r > 12 && d.Arrows != 2 && !numbered)
        {
            // большая окружность: размерная линия через центр, стрелки изнутри на окружность
            var p2 = (c.X - r * dir.Item1, c.Y - r * dir.Item2);
            g.Line(p.Item1, p.Item2, p2.Item1, p2.Item2);
            Arrow(g, p, dir); Arrow(g, p2, (-dir.Item1, -dir.Item2));
            double ang = a * 180 / Math.PI; var up = (-dir.Item2, dir.Item1);
            ang = ((ang + 180) % 360 + 360) % 360 - 180;   // k164: угол выноски 315° → −45° (прежде текст выходил вверх ногами)
            if (ang > 90.0001 || ang <= -90) { ang += ang > 0 ? -180 : 180; up = (-up.Item1, -up.Item2); }
            // k164: текст — посередине между центром и стрелкой (прежде — над центром: диаметры одного центра ложились друг на друга)
            double tf = CenterPrioOff ? 0 : 0.5;
            DimText(g, d, new Vector2d(c.X + dir.Item1 * r * tf + up.Item1, c.Y + dir.Item2 * r * tf + up.Item2), ang, text);
            return;
        }
        // выноска с полкой: от окружности наружу 6 мм (k159: у диаметра и радиуса Offset — длина выноски: 0.6·Offset, по
        // умолчанию 10 → 6 мм; раскладка удлиняет выноску, если ни один угол не свободен), полка по длине текста
        double lead = d.Offset > 0 ? 0.6 * d.Offset : 6;
        var k = (p.Item1 + dir.Item1 * lead, p.Item2 + dir.Item2 * lead);
        double side = dir.Item1 >= 0 ? 1 : -1;
        // k164 (п. 5): номер центра «℄N» под полкой
        int? cno = CentreNo(d.View, new Vector2d(c.X, c.Y));
        if (cno is { } no) tw = Math.Max(tw, SignW(no, TextH));
        var e = (k.Item1 + side * (tw + 2), k.Item2);
        if (cno is { } no2) CenterSign(g, (k.Item1 + e.Item1) / 2 - SignW(no2, TextH) / 2, k.Item2 - 1.5 - TextH, TextH, no2);
        if (d.Kind == "rad")
        {
            // радиус: линия от центра через дугу, стрелка на дуге (остриём наружу)
            g.Poly(LineKind.Thin, (c.X, c.Y), p, k, e);
            Arrow(g, p, dir);
        }
        else
        {
            g.Poly(LineKind.Thin, p, k, e);
            Arrow(g, p, (-dir.Item1, -dir.Item2));
        }
        DimText(g, d, new Vector2d((k.Item1 + e.Item1) / 2, k.Item2 + 1), 0, text);
    }

    // ------------------------------------------------------------------ обозначения

    void DrawNote(SheetGraphics g, DrawingNote n, DrawingScene scene)
    {
        (double X, double Y)? target = n.HasTarget && n.View != null && Xf.ContainsKey(n.View) ? ToSheet(n.View, new Vector2d(n.TX, n.TY)) : null;
        switch (n.Kind)
        {
            case "rough":
                {
                    if (target is { } t)
                    {
                        // знак на поверхности: вершина в точке цели, полка вправо, значение над полкой
                        RoughSign(g, t.X, t.Y, n.Value, n.Height);
                    }
                    else
                    {
                        // неуказанная шероховатость — правый верхний угол рамки: «Ra 6,3 (√)»
                        var (_, _, fx1, fy1) = SheetLayout.FrameRect(Template);
                        double x = n.X != 0 ? n.X : fx1 - 30, y = n.Y != 0 ? n.Y : fy1 - 12;
                        RoughSign(g, x, y, n.Value, n.Height);
                        g.Text(x + 14, y + 1, n.Height, "(", TextAlign.Left);
                        RoughSign(g, x + 18, y + 1, "", n.Height * 0.8);
                        g.Text(x + 25, y + 1, n.Height, ")", TextAlign.Left);
                    }
                    break;
                }
            case "gdt":
                {
                    double h = 7, x = n.X, y = n.Y;
                    var cells = new List<(double W, string T, string? Sym)> { (h, "", n.Symbol) };
                    if (n.Value.Length > 0) cells.Add((Math.Max(h, TextW(n.Value, TextH) + 3), n.Value, null));
                    foreach (var dt in n.Datums.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) cells.Add((h, dt, null));
                    double cx = x;
                    foreach (var (w, t, sym) in cells)
                    {
                        g.Rect(cx, y, w, h);
                        if (sym != null) GdtSymbol(g, sym, cx + w / 2, y + h / 2, h * 0.32);
                        else g.CellText(cx, y, w, h, TextH, t);
                        cx += w;
                    }
                    if (target is { } t2) { g.Poly(LineKind.Thin, (x, y + h / 2), (x - 5, y + h / 2), t2); Arrow(g, t2, (t2.X - (x - 5), t2.Y - (y + h / 2))); }
                    break;
                }
            case "datum":
                {
                    double h = 7, x = n.X, y = n.Y;
                    g.Rect(x - h / 2, y, h, h);
                    g.CellText(x - h / 2, y, h, h, TextH + 1, n.Text);
                    if (target is { } t)
                    {
                        g.Line(x, y, t.X, t.Y + 2.6);
                        g.Fills.Add(new() { (t.X - 2.5, t.Y), (t.X + 2.5, t.Y), (t.X, t.Y + 3.2) });   // зачернённый треугольник базы
                    }
                    break;
                }
            case "balloon":
                {
                    string num = n.Position.ToString(CultureInfo.InvariantCulture);
                    if (Template.Standard == SheetStandard.Iso)
                    {
                        double r = 5;
                        g.Circles.Add((n.X, n.Y, r, LineKind.Thin));
                        g.Text(n.X, n.Y - n.Height / 2 - 0.5, 5, num, TextAlign.Center);
                        if (target is { } t)
                        {
                            double dx = t.X - n.X, dy = t.Y - n.Y, l = Math.Sqrt(dx * dx + dy * dy);
                            if (l > r) g.Line(n.X + dx / l * r, n.Y + dy / l * r, t.X, t.Y);
                            Dot(g, t.X, t.Y);
                        }
                    }
                    else
                    {
                        // ГОСТ 2.109: полка с номером позиции, выноска с точкой на детали; шрифт на 1–2 размера больше размерных
                        double w = TextW(num, 5) + 4;
                        g.Line(n.X, n.Y, n.X + w, n.Y);
                        g.Text(n.X + w / 2, n.Y + 1, 5, num, TextAlign.Center);
                        if (target is { } t) { g.Line(n.X, n.Y, t.X, t.Y); Dot(g, t.X, t.Y); }
                    }
                    break;
                }
            case "holes": HoleTable(g, n, scene); break;
            default:
                {
                    var lines = n.Text.Replace("\r", "").Split('\n');
                    for (int i = 0; i < lines.Length; i++) g.Text(n.X, n.Y - i * n.Height * 1.6, n.Height, lines[i]);
                    if (target is { } t) { g.Poly(LineKind.Thin, (n.X - 1, n.Y + n.Height / 2), t); Arrow(g, t, (t.X - n.X + 1, t.Y - n.Y - n.Height / 2)); }
                    break;
                }
        }
    }

    static void Dot(SheetGraphics g, double x, double y)
    {
        var pts = new List<(double, double)>();
        for (int k = 0; k < 10; k++) pts.Add((x + 0.6 * Math.Cos(k * Math.PI / 5), y + 0.6 * Math.Sin(k * Math.PI / 5)));
        g.Fills.Add(pts);
    }

    /// <summary>Знак шероховатости ГОСТ 2.309 (без удаления слоя не различается): вершина (x, y), полка — над длинной стороной.</summary>
    static void RoughSign(SheetGraphics g, double x, double y, string value, double h)
    {
        double H = h * 1.6;
        var left = (x - H * 0.58, y + H); var top = (x + H * 1.15, y + 2 * H);
        g.Poly(LineKind.Thin, left, (x, y), top);
        if (value.Length > 0)
        {
            double w = TextW(value, h) + 1;
            g.Line(top.Item1, top.Item2, top.Item1 + w, top.Item2);
            g.Text(top.Item1 + w / 2 + 0.5, top.Item2 + 1, h, value, TextAlign.Center);
        }
    }

    /// <summary>Знаки допусков формы и расположения ГОСТ 2.308 — линиями (шрифт листа их не содержит).</summary>
    public static readonly (string Key, string Name)[] GdtSymbols =
    {
        ("str", "Прямолинейность"), ("flat", "Плоскостность"), ("circ", "Круглость"), ("cyl", "Цилиндричность"),
        ("par", "Параллельность"), ("perp", "Перпендикулярность"), ("ang", "Наклон"), ("pos", "Позиционный"),
        ("conc", "Соосность"), ("sym", "Симметричность"), ("run", "Радиальное биение"),
    };

    static void GdtSymbol(SheetGraphics g, string sym, double cx, double cy, double r)
    {
        void C(double rr) => g.Circles.Add((cx, cy, rr, LineKind.Thin));
        switch (sym)
        {
            case "str": g.Line(cx - r, cy, cx + r, cy); break;
            case "flat": g.Poly(LineKind.Thin, (cx - r, cy - r * 0.5), (cx + r * 0.6, cy - r * 0.5), (cx + r, cy + r * 0.5), (cx - r * 0.6, cy + r * 0.5), (cx - r, cy - r * 0.5)); break;
            case "circ": C(r); break;
            case "cyl": C(r * 0.75); g.Line(cx - r * 0.6, cy - r, cx + r * 0.2, cy + r); g.Line(cx - r * 0.2, cy - r, cx + r * 0.6, cy + r); break;
            case "par": g.Line(cx - r * 0.7, cy - r, cx - r * 0.1, cy + r); g.Line(cx + r * 0.1, cy - r, cx + r * 0.7, cy + r); break;
            case "perp": g.Line(cx - r, cy - r, cx + r, cy - r); g.Line(cx, cy - r, cx, cy + r); break;
            case "ang": g.Line(cx - r, cy - r, cx + r, cy - r); g.Line(cx - r, cy - r, cx + r, cy + r * 0.3); break;
            case "pos": C(r * 0.6); g.Line(cx - r, cy, cx + r, cy); g.Line(cx, cy - r, cx, cy + r); break;
            case "conc": C(r * 0.45); C(r); break;
            case "sym": g.Line(cx - r, cy, cx + r, cy); g.Line(cx - r * 0.6, cy + r * 0.5, cx + r * 0.6, cy + r * 0.5); g.Line(cx - r * 0.6, cy - r * 0.5, cx + r * 0.6, cy - r * 0.5); break;
            case "run": g.Line(cx - r * 0.5, cy - r, cx + r * 0.5, cy + r); g.Line(cx + r * 0.5, cy + r, cx + r * 0.1, cy + r * 0.4); break;
            default: g.Text(cx, cy - 1.75, 3.5, sym, TextAlign.Center); break;
        }
    }

    // ------------------------------------------------------------------ таблица отверстий (D5)

    /// <summary>Отверстия вида (перекрестия — оси вдоль взгляда) от левого нижнего угла габарита: номер, X, Y, диаметр.</summary>
    public static List<(int No, double X, double Y, double D)> Holes(DrawingScene scene, string view)
    {
        if (!scene.Lines.TryGetValue(view, out var lines) || !scene.Axes.TryGetValue(view, out var axes)) return new();
        var (mn, _) = Box(lines);
        return axes.Where(a => a.Cross).OrderByDescending(a => a.Center.Y).ThenBy(a => a.Center.X)
                   .Select((a, i) => (i + 1, a.Center.X - mn.X, a.Center.Y - mn.Y, 2 * a.Radius)).ToList();
    }

    void HoleTable(SheetGraphics g, DrawingNote n, DrawingScene scene)
    {
        string view = n.View ?? "top";
        var holes = Holes(scene, view);
        if (holes.Count == 0) return;
        double[] cols = { 12, 20, 20, 16 }; string[] heads = { "Отв.", "X", "Y", Dia };
        double rh = 7, x = n.X, top = n.Y, w = cols.Sum();
        int rows = holes.Count + 1;
        g.Rect(x, top - rows * rh, w, rows * rh, LineKind.Thick);
        for (int r = 1; r < rows; r++) g.Line(x, top - r * rh, x + w, top - r * rh, r == 1 ? LineKind.Thick : LineKind.Thin);
        double cx = x;
        for (int c = 0; c < cols.Length; c++)
        {
            if (c > 0) g.Line(cx, top, cx, top - rows * rh, LineKind.Thick);
            g.CellText(cx, top - rh, cols[c], rh, TextH, heads[c]);
            for (int r = 0; r < holes.Count; r++)
            {
                var h = holes[r];
                string t = c switch { 0 => h.No.ToString(CultureInfo.InvariantCulture), 1 => Num(h.X), 2 => Num(h.Y), _ => Num(h.D) };
                g.CellText(cx, top - (r + 2) * rh, cols[c], rh, TextH, t);
            }
            cx += cols[c];
        }
        // номера отверстий на виде
        if (Xf.ContainsKey(view) && scene.Lines.TryGetValue(view, out var vl))
        {
            var (mn, _) = Box(vl);
            foreach (var h in holes)
            {
                var p = ToSheet(view, new Vector2d(mn.X + h.X, mn.Y + h.Y));
                g.Text(p.X + 2 + h.D * Xf[view].S / 2, p.Y + 1, 2.5, h.No.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    string Num(double v) { var t = v.ToString("0.##", CultureInfo.InvariantCulture); return Comma ? t.Replace('.', ',') : t; }

    /// <summary>
    /// Осевые линии ГОСТ 2.303 (штрихпунктирная тонкая, выход за контур 3 мм): ось вдоль взгляда — перекрестие центра окружности,
    /// поперёк взгляда — ось по длине поверхности вращения.
    /// </summary>
    static void AxisLines(SheetGraphics g, List<ViewAxis> axes, Func<Vector2d, (double X, double Y)> T, double s)
    {
        double ext = 3 / s;
        foreach (var a in axes)
        {
            if (a.Cross)
            {
                double r = a.Radius + ext;
                g.Polylines.Add(new SheetPolyline(new() { T(a.Center - Vector2d.UnitX * r), T(a.Center + Vector2d.UnitX * r) }, LineKind.Center));
                g.Polylines.Add(new SheetPolyline(new() { T(a.Center - Vector2d.UnitY * r), T(a.Center + Vector2d.UnitY * r) }, LineKind.Center));
            }
            else g.Polylines.Add(new SheetPolyline(new() { T(a.Center + a.Dir * (a.Lo - ext)), T(a.Center + a.Dir * (a.Hi + ext)) }, LineKind.Center));
        }
    }

    // ------------------------------------------------------------------ автоматические размеры и позиции

    /// <summary>
    /// D3: АВТОМАТИЧЕСКИЕ РАЗМЕРЫ. Габаритные: на главном виде — ширина (снизу) и высота (справа), на виде сверху — глубина
    /// (слева); диаметры отверстий — по одному на размер на виде, где оси вдоль взгляда («4 отв. ⌀6»); радиусы скруглений
    /// (дуги не полного оборота) — по одному на значение. Прежние автоматические размеры заменяются; скрытые пользователем
    /// остаются скрытыми.
    /// </summary>
    /// <summary>k158: мутация P3D_VDIMSIDE=0 — вертикальные габаритные размеры внутри вида (прежнее, ошибка k156).</summary>
    static readonly bool VDimSideOff = Environment.GetEnvironmentVariable("P3D_VDIMSIDE") == "0";

    /// <summary>k167 (замечание 05.11, 16:17, п. 6): onlyView — размеры только этого вида (выбранного), остальные не трогаются.</summary>
    public void AutoDimensions(DrawingScene scene, string? onlyView = null)
    {
        if (OneViewOff) onlyView = null;
        bool In(string v) => onlyView == null || v == onlyView;
        var hidden = Dims.Where(d => d.Auto && d.Hidden && In(d.View)).Select(Key).ToHashSet();
        Dims.RemoveAll(d => d.Auto && In(d.View));
        void Add(DrawingDim d) { if (!In(d.View)) return; d.Auto = true; d.Arrange = true; d.Hidden = hidden.Contains(Key(d)); Dims.Add(d); }
        foreach (var v in Views.Where(v => !v.IsDetail && v.Name != "iso" && In(v.Name)))
        {
            if (!scene.Lines.TryGetValue(v.Name, out var lines) || lines.Count == 0) continue;
            var (mn, mx) = Box(lines);
            // k159: концы габаритных размеров — крайние точки контура (у ступенчатой детали угол габарита пуст: выносные линии
            // шли бы от пустого места). Мутация P3D_DIMEXTREME=0 — углы габарита (прежнее).
            var pts = lines.Where(l => l.Visible).SelectMany(l => l.Points).ToList();
            if (pts.Count == 0) pts = lines.SelectMany(l => l.Points).ToList();
            Vector2d Ext(Func<Vector2d, double> key, Func<Vector2d, double> tie, Vector2d corner)
            {
                if (ExtremeOff) return corner;
                double k = pts.Min(key);
                return pts.Where(q => key(q) <= k + 1e-6).OrderBy(tie).First();
            }
            if (v.Name == "front" || v.IsSection && !Views.Any(x => x.Name == "front"))
            {
                // k158: у вертикального размера нормаль размерной линии — влево (−X листа): справа от вида — отступ < 0
                var a = Ext(q => q.X, q => q.Y, mn); var b = Ext(q => -q.X, q => q.Y, new Vector2d(mx.X, mn.Y));
                Add(new DrawingDim { View = v.Name, Kind = "h", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = -10, FromBox = !ExtremeOff, Role = "overall" });
                var c = Ext(q => q.Y, q => -q.X, new Vector2d(mx.X, mn.Y)); var e = Ext(q => -q.Y, q => -q.X, mx);
                Add(new DrawingDim { View = v.Name, Kind = "v", X1 = c.X, Y1 = c.Y, X2 = e.X, Y2 = e.Y, Offset = VDimSideOff ? 10 : -10, FromBox = !ExtremeOff, Role = "overall" });
            }
            if (v.Name == "top")
            {
                var c = Ext(q => q.Y, q => q.X, mn); var e = Ext(q => -q.Y, q => q.X, new Vector2d(mn.X, mx.Y));
                Add(new DrawingDim { View = v.Name, Kind = "v", X1 = c.X, Y1 = c.Y, X2 = e.X, Y2 = e.Y, Offset = VDimSideOff ? -10 : 10, FromBox = !ExtremeOff, Role = "overall" });
            }
            if (v.Name == "left" && !Views.Any(x => x.Name == "top"))
            {
                var a = Ext(q => q.X, q => q.Y, mn); var b = Ext(q => -q.X, q => q.Y, new Vector2d(mx.X, mn.Y));
                Add(new DrawingDim { View = v.Name, Kind = "h", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = -10, FromBox = !ExtremeOff, Role = "overall" });
            }
        }
        // диаметры отверстий: вид с наибольшим числом перекрестий
        var holeView = Views.Where(v => !v.IsDetail && scene.Axes.ContainsKey(v.Name) && In(v.Name)).OrderByDescending(v => scene.Axes[v.Name].Count(a => a.Cross)).FirstOrDefault();
        if (holeView != null)
            foreach (var grp in scene.Axes[holeView.Name].Where(a => a.Cross).GroupBy(a => Math.Round(a.Radius, 4)))
            {
                var a = grp.OrderByDescending(x => x.Center.Y).ThenByDescending(x => x.Center.X).First();
                Add(new DrawingDim { View = holeView.Name, Kind = "dia", X1 = a.Center.X, Y1 = a.Center.Y, X2 = a.Radius, Y2 = 45, Count = grp.Count() });
            }
        // радиусы: дуги не полного оборота, по одному на значение
        var seen = new HashSet<double>();
        foreach (var v in Views.Where(v => !v.IsDetail && v.Name != "iso" && scene.Arcs.ContainsKey(v.Name) && In(v.Name)))
            foreach (var arc in scene.Arcs[v.Name].Where(a => !a.Full))
            {
                double r = Math.Round(arc.Radius, 4);
                // k164: дуга окружности, у которой уже есть диаметр (перекрестие того же центра и радиуса), — не радиус
                if ((scene.Axes.GetValueOrDefault(v.Name) ?? new()).Any(a => a.Cross && (a.Center - arc.Center).Length < 1e-6 * Math.Max(1, arc.Radius) && Math.Abs(a.Radius - arc.Radius) < 1e-6 * Math.Max(1, arc.Radius))) continue;
                if (!seen.Add(r)) continue;
                double am = (arc.A0 + arc.A1) / 2; if (arc.A1 < arc.A0) am += Math.PI;
                Add(new DrawingDim { View = v.Name, Kind = "rad", X1 = arc.Center.X, Y1 = arc.Center.Y, X2 = arc.Radius, Y2 = am * 180 / Math.PI });
            }
        if (!MoreDimsOff) MoreDimensions(scene, Add, onlyView);
        // k165: места подписей центров — подобрать заново (только по «Автоматическим размерам»)
        CentreMarks.RemoveAll(m => In(m.View));
        ArrangeDims(scene);
    }

    static readonly bool ExtremeOff = Environment.GetEnvironmentVariable("P3D_DIMEXTREME") == "0";
    static readonly bool MoreDimsOff = Environment.GetEnvironmentVariable("P3D_MOREDIMS") == "0";
    internal static readonly bool IsoAxesOff = Environment.GetEnvironmentVariable("P3D_ISOAXES") == "0";
    static readonly bool OneViewOff = Environment.GetEnvironmentVariable("P3D_AUTODIMSVIEW") == "0";
    static readonly bool ModelAngOff = Environment.GetEnvironmentVariable("P3D_MODELDIMS163") == "0";
    static readonly bool ArcCentersOff = Environment.GetEnvironmentVariable("P3D_ARCCENTERS") == "0";

    /// <summary>
    /// k163 (замечание 05.11, 09:22, п. 3.2): ещё автоматические размеры, без которых деталь по чертежу не воспроизвести:
    /// - диаметры полных окружностей вида (не только отверстий-перекрестий), по одному на значение;
    /// - положение центров дуг относительно центра наибольшей окружности вида — по горизонтали и вертикали (смещения осей,
    ///   как ℄1…℄6 на чертеже пользователя);
    /// - ступени на виде вдоль оси детали: положение вертикальных видимых рёбер от левого края контура (не больше 8; ближе
    ///   0.5 мм к краю или друг к другу — один).
    /// Мутация P3D_MOREDIMS=0 — без них (прежнее).
    /// </summary>
    void MoreDimensions(DrawingScene scene, Action<DrawingDim> add, string? onlyView = null)
    {
        var have = new HashSet<string>(Dims.Where(d => d.Kind is "dia" or "rad").Select(d => $"{d.View}|{Math.Round(d.X2, 3)}"));
        foreach (var v in Views.Where(v => (onlyView == null || v.Name == onlyView) && !v.IsDetail && v.Name != "iso" && scene.Arcs.ContainsKey(v.Name)))
        {
            var arcs = scene.Arcs[v.Name];
            foreach (var grp in arcs.Where(a => a.Full).GroupBy(a => Math.Round(a.Radius, 3)))
            {
                if (have.Contains($"{v.Name}|{grp.Key}") || Dims.Any(d => d.Kind is "dia" or "rad" && Math.Abs(d.X2 - grp.Key) < 1e-3)) continue;
                var a = grp.First();
                add(new DrawingDim { View = v.Name, Kind = "dia", X1 = a.Center.X, Y1 = a.Center.Y, X2 = a.Radius, Y2 = 45, Count = grp.Count(), Role = "circle" });
                have.Add($"{v.Name}|{grp.Key}");
            }
            // центры дуг относительно центра наибольшей окружности
            // главный центр — ось наибольшей окружности тела вращения (перекрестие), иначе центр наибольшей дуги
            var cross = (scene.Axes.GetValueOrDefault(v.Name) ?? new()).Where(a => a.Cross).OrderByDescending(a => a.Radius).FirstOrDefault();
            if (arcs.Count < (cross != null ? 1 : 2)) continue;
            var main = cross != null && (arcs.Count == 0 || cross.Radius >= arcs.Max(a => a.Radius) - 1e-6) ? cross.Center : arcs.OrderByDescending(a => a.Radius).First().Center;
            var centres = new List<Vector2d>();
            foreach (var a in arcs.OrderByDescending(a => a.Radius))
                if ((a.Center - main).Length > 1e-3 && !centres.Any(c => (c - a.Center).Length < 1e-3)) centres.Add(a.Center);
            var dx = new HashSet<double>(); var dy = new HashSet<double>();
            foreach (var c in centres.Take(8))
            {
                if (Math.Abs(c.X - main.X) > 1e-3 && dx.Add(Math.Round(c.X - main.X, 3)))
                    add(new DrawingDim { View = v.Name, Kind = "h", X1 = main.X, Y1 = main.Y, X2 = c.X, Y2 = c.Y, Offset = -10, Role = "centre" });
                if (Math.Abs(c.Y - main.Y) > 1e-3 && dy.Add(Math.Round(c.Y - main.Y, 3)))
                    add(new DrawingDim { View = v.Name, Kind = "v", X1 = main.X, Y1 = main.Y, X2 = c.X, Y2 = c.Y, Offset = 10, Role = "centre" });
            }
        }
        // ступени вдоль вида: вертикальные видимые рёбра
        foreach (var v in Views.Where(v => (onlyView == null || v.Name == onlyView) && !v.IsDetail && v.Name != "iso" && scene.Lines.ContainsKey(v.Name)))
        {
            var lines = scene.Lines[v.Name];
            var vis = lines.Where(l => l.Visible && l.Kind == ViewLineKind.Edge).ToList();
            if (vis.Count == 0) continue;
            var (mn, mx) = Box(lines);
            double w = mx.X - mn.X, h = mx.Y - mn.Y;
            if (w < 1.5 * h) continue;   // ступени — у вытянутого вдоль листа вида (вал, труба)
            var xs = new List<(double X, double Y)>();
            foreach (var l in vis)
                for (int i = 0; i + 1 < l.Points.Count; i++)
                {
                    var a = l.Points[i]; var b = l.Points[i + 1];
                    if (Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) > 0.05 * h) xs.Add((a.X, Math.Min(a.Y, b.Y)));
                }
            var leftPt = lines.Where(l => l.Visible).SelectMany(l => l.Points).OrderBy(q => q.X).ThenBy(q => q.Y).First();
            var steps = new List<double>();
            foreach (var (x, y) in xs.OrderBy(q => q.X))
            {
                if (x - mn.X < 0.5 || mx.X - x < 0.5 || steps.Any(sx => Math.Abs(sx - x) < 0.5)) continue;
                steps.Add(x);
                if (steps.Count > 8) break;
                double yb = xs.Where(q => Math.Abs(q.X - x) < 1e-6).Min(q => q.Y);
                add(new DrawingDim { View = v.Name, Kind = "h", X1 = leftPt.X, Y1 = leftPt.Y, X2 = x, Y2 = yb, Offset = -10, Role = "step" });
            }
        }
    }

    /// <summary>
    /// k159: РАСКЛАДКА РАЗМЕРОВ (ГОСТ 2.307 п. 1.12: первая размерная линия — не ближе 10 мм к контуру, параллельные — через 7 мм,
    /// меньший размер ближе к контуру). Линейные размеры с автоматическим местом (Arrange) — по видам и сторонам: сторона — как
    /// задана (габаритные) или ближайшая к точкам (размеры модели); ряды 10, 17, 24 … мм от габарита вида; совпадающие размеры
    /// (вид, вид размера, значение) — один. Угол выносок диаметров и радиусов — при выводе листа (PlaceRadial).
    /// </summary>
    public void ArrangeDims(DrawingScene scene)
    {
        if (ArrangeOff) return;
        // совпадающие: одинаковые вид, вид размера и значение — остаётся первый (автоматические — раньше размеров модели)
        var seen = new HashSet<string>();
        foreach (var d in Dims.Where(d => d.Arrange && d.Kind is "h" or "v").OrderBy(d => d.Auto ? 0 : 1).ToList())
            if (!seen.Add($"{d.View}|{d.Kind}|{Math.Round(d.Value, 4)}")) Dims.Remove(d);
        foreach (var grp in Dims.Where(d => d.Arrange && !d.Hidden && d.Kind is "h" or "v").GroupBy(d => d.View))
        {
            if (!scene.Lines.TryGetValue(grp.Key, out var lines) || lines.Count == 0) continue;
            var (mn, mx) = Box(lines); var c = (mn + mx) / 2;
            foreach (var d in grp)
            {
                d.FromBox = true;
                if (d.Auto) continue;   // сторона габаритных задана
                // размер модели — к ближней стороне: h — сверху или снизу (n = +Y), v — справа или слева (n = −X)
                d.Offset = d.Kind == "h" ? ((d.Y1 + d.Y2) / 2 >= c.Y ? 1 : -1) : ((d.X1 + d.X2) / 2 >= c.X ? -1 : 1);
            }
            foreach (var side in grp.GroupBy(d => (d.Kind, Math.Sign(d.Offset))))
            {
                int k = 0;
                foreach (var d in side.OrderBy(d => d.Value)) d.Offset = side.Key.Item2 * (10 + 7 * k++);
            }
        }
    }

    static string Key(DrawingDim d) => $"{d.View}|{d.Kind}|{Math.Round(d.X1, 3)}|{Math.Round(d.Y1, 3)}|{Math.Round(d.X2, 3)}|{Math.Round(d.Y2, 3)}";

    /// <summary>
    /// D3: РАЗМЕРЫ МОДЕЛИ — размеры эскизов детали на виде, плоскость которого параллельна плоскости эскиза (ГОСТ: размер
    /// — в натуральную величину). Расстояния (точка–точка, по горизонтали и вертикали эскиза), радиусы и диаметры; отключённые
    /// и справочные не переносятся.
    /// </summary>
    public int ModelDimensions(Part part)
    {
        Dims.RemoveAll(d => d.Model);
        int added = 0;
        foreach (var sk in part.Sketches)
            foreach (var d in sk.Dimensions)
            {
                if (!d.Enabled || d.Reference) continue;
                var dd = FromSketchDim(sk, d, added);
                if (dd == null) continue;
                dd.Model = true; dd.Arrange = true;
                Dims.Add(dd); added++;
            }
        return added;
    }

    /// <summary>
    /// k170: размер чертежа по размеру эскиза — на виде, плоскость которого параллельна плоскости эскиза (нет такого вида или
    /// вид размера не переносится — null). Общий для «Размеров модели» и недостающих размеров проверки полноты (п. 7.3).
    /// </summary>
    public DrawingDim? FromSketchDim(Sketching.Sketch sk, Sketching.SkDimension d, int added = 0)
    {
        var w = sk.Plane.W.Normalized();
        var view = Views.Where(v => !v.IsDetail && !v.IsSection && v.Name != "iso")
                        .FirstOrDefault(v => Math.Abs(Math.Abs(Vector3d.Dot(ViewFrame.Of(DirOf(v)).Toward, w)) - 1) < 1e-6);
        if (view == null) return null;
        var f = ViewFrame.Of(DirOf(view));
        Vector2d P(Vector2d skp) => f.Project(sk.Plane.ToWorld(skp));
            DrawingDim? dd = null;
            Vector2d[] pts = d.Items.SelectMany(PointsOf).ToArray();
            switch (d.Type)
            {
                case Sketching.DimensionType.DistancePointPoint when pts.Length >= 2:
                case Sketching.DimensionType.DistanceHorizontal when pts.Length >= 2:
                case Sketching.DimensionType.DistanceVertical when pts.Length >= 2:
                    {
                        var a = P(pts[0]); var b = P(pts[^1]);
                        // ось U эскиза на виде: идёт ли она по горизонтали листа
                        var uOnView = f.Project(sk.Plane.ToWorld(new Vector2d(1, 0))) - f.Project(sk.Plane.ToWorld(Vector2d.Zero));
                        bool uIsH = Math.Abs(uOnView.X) > 0.5;
                        string kind = d.Type switch
                        {
                            Sketching.DimensionType.DistanceHorizontal => uIsH ? "h" : "v",
                            Sketching.DimensionType.DistanceVertical => uIsH ? "v" : "h",
                            _ => Math.Abs(a.Y - b.Y) < 1e-9 ? "h" : Math.Abs(a.X - b.X) < 1e-9 ? "v" : "al",
                        };
                        dd = new DrawingDim { View = view.Name, Kind = kind, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = 8 + 7 * (added % 3) };
                        break;
                    }
                case Sketching.DimensionType.AngleLineLine when !ModelAngOff && d.Items.Count >= 2 && d.Items[0] is Sketching.SkLine la && d.Items[1] is Sketching.SkLine lb:
                    {
                        // k163: угол двух отрезков эскиза — вершина в точке пересечения прямых, стороны — к дальним концам;
                        // из четырёх пар направлений — та, у которой угол против часовой равен размеру
                        Vector2d A0 = P(la.A.P), A1 = P(la.B.P), B0 = P(lb.A.P), B1 = P(lb.B.P);
                        var ua = A1 - A0; var ub = B1 - B0;
                        double den = ua.X * ub.Y - ua.Y * ub.X;
                        if (Math.Abs(den) < 1e-12) break;
                        double t = ((B0.X - A0.X) * ub.Y - (B0.Y - A0.Y) * ub.X) / den;
                        var vx = A0 + ua * t;
                        Vector2d Far(Vector2d p0, Vector2d p1) => (p0 - vx).Length > (p1 - vx).Length ? p0 - vx : p1 - vx;
                        var da = Far(A0, A1).Normalized(); var db = Far(B0, B1).Normalized();
                        double best = double.MaxValue; (double, double) pick = (0, 0);
                        foreach (var sa in new[] { 1.0, -1.0 })
                            foreach (var sb in new[] { 1.0, -1.0 })
                                foreach (var (p0, p1) in new[] { (da * sa, db * sb), (db * sb, da * sa) })
                                {
                                    double a0 = Math.Atan2(p0.Y, p0.X) * 180 / Math.PI, a1 = Math.Atan2(p1.Y, p1.X) * 180 / Math.PI;
                                    double sw = ((a1 - a0) % 360 + 360) % 360;
                                    double err = Math.Abs(sw - Math.Abs(d.Value)) + (sa < 0 ? 0.01 : 0) + (sb < 0 ? 0.01 : 0);
                                    if (err < best) { best = err; pick = (a0, a1); }
                                }
                        dd = new DrawingDim { View = view.Name, Kind = "ang", X1 = vx.X, Y1 = vx.Y, X2 = pick.Item1, Y2 = pick.Item2, Offset = 15 };
                        break;
                    }
                case Sketching.DimensionType.DistancePointLine or Sketching.DimensionType.DistanceLineLine when !ModelAngOff:
                    {
                        // k163: расстояние точки (или отрезка) до прямой — от точки до её проекции на прямую
                        var line = d.Items.OfType<Sketching.SkLine>().LastOrDefault();
                        var other = d.Items.FirstOrDefault(x => x != line);
                        if (line == null || other == null) break;
                        Vector2d q = other switch { Sketching.SkPoint sp => sp.P, Sketching.SkLine ol => ol.A.P, Sketching.SkCircle oc => oc.Center.P, Sketching.SkArc oa => oa.Center.P, _ => line.A.P };
                        var u = (line.B.P - line.A.P); if (u.Length < 1e-12) break; u.Normalize();
                        var foot = line.A.P + u * Vector2d.Dot(q - line.A.P, u);
                        var a = P(q); var b = P(foot);
                        string kind = Math.Abs(a.Y - b.Y) < 1e-9 ? "h" : Math.Abs(a.X - b.X) < 1e-9 ? "v" : "al";
                        dd = new DrawingDim { View = view.Name, Kind = kind, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = 8 + 7 * (added % 3) };
                        break;
                    }
                case Sketching.DimensionType.CoordX or Sketching.DimensionType.CoordY when !ModelAngOff && pts.Length >= 1:
                    {
                        // k163: координата точки от начала эскиза
                        var q = pts[0];
                        var o = d.Type == Sketching.DimensionType.CoordX ? new Vector2d(0, q.Y) : new Vector2d(q.X, 0);
                        var a = P(o); var b = P(q);
                        string kind = Math.Abs(a.Y - b.Y) < 1e-9 ? "h" : Math.Abs(a.X - b.X) < 1e-9 ? "v" : "al";
                        dd = new DrawingDim { View = view.Name, Kind = kind, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = 8 + 7 * (added % 3) };
                        break;
                    }
                case Sketching.DimensionType.Radius or Sketching.DimensionType.Diameter:
                    {
                        var e = d.Items.FirstOrDefault(x => x is Sketching.SkCircle or Sketching.SkArc);
                        if (e is Sketching.SkCircle ci) dd = new DrawingDim { View = view.Name, Kind = d.Type == Sketching.DimensionType.Radius ? "rad" : "dia", X1 = P(ci.Center.P).X, Y1 = P(ci.Center.P).Y, X2 = ci.Radius, Y2 = 45 };
                        else if (e is Sketching.SkArc ar) dd = new DrawingDim { View = view.Name, Kind = d.Type == Sketching.DimensionType.Radius ? "rad" : "dia", X1 = P(ar.Center.P).X, Y1 = P(ar.Center.P).Y, X2 = ar.Radius, Y2 = 45 };
                        break;
                    }
            }
        return dd;
    }

    static IEnumerable<Vector2d> PointsOf(Sketching.SkEntity e) => e switch
    {
        Sketching.SkPoint p => new[] { p.P },
        Sketching.SkLine l => new[] { l.A.P, l.B.P },
        Sketching.SkCircle c => new[] { c.Center.P },
        Sketching.SkArc a => new[] { a.Center.P },
        _ => Array.Empty<Vector2d>(),
    };

    /// <summary>
    /// D4: ПОЗИЦИИ СБОРКИ (ГОСТ 2.109) на главном виде: по одной на деталь (номер — из перечня по имени, иначе по порядку),
    /// выноска к середине самой длинной видимой линии детали, полки — столбцом справа от вида с шагом 9 мм (без наложений).
    /// </summary>
    public void AutoBalloons(DrawingScene scene, IReadOnlyDictionary<string, int>? positions = null, string view = "front")
    {
        Notes.RemoveAll(n => n.Kind == "balloon" && n.Auto);
        if (!scene.Lines.TryGetValue(view, out var lines) || !Boxes.ContainsKey(view) || scene.BodyItems.Count == 0) return;
        var items = scene.BodyItems.Select((it, i) => (it, i)).GroupBy(x => x.it).ToList();
        var (bx0, by0, bx1, by1) = Boxes[view];
        var list = new List<(double TY, DrawingNote N)>();
        int next = 1;
        foreach (var grp in items)
        {
            var bodies = grp.Select(x => x.i).ToHashSet();
            var best = lines.Where(l => l.Visible && bodies.Contains(l.Body) && l.Points.Count >= 2).OrderByDescending(l => Len(l.Points)).FirstOrDefault();
            if (best == null) continue;
            var m = MidOf(best.Points);
            int pos = positions != null && positions.TryGetValue(grp.Key, out var pp) ? pp : next;
            next = Math.Max(next, pos) + 1;
            var t = ToSheet(view, m);
            list.Add((t.Y, new DrawingNote { Kind = "balloon", View = view, HasTarget = true, TX = m.X, TY = m.Y, Position = pos, Text = grp.Key, Auto = true, X = bx1 + 12 }));
        }
        // столбец полок: порядок — по высоте цели, шаг не меньше 9 мм
        double y = double.MaxValue;
        foreach (var (ty, n) in list.OrderByDescending(x => x.TY))
        {
            y = Math.Min(Math.Min(ty, by1), y - 9);
            n.Y = y;
            Notes.Add(n);
        }
    }

    static double Len(List<Vector2d> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length; return s; }
    static Vector2d MidOf(List<Vector2d> p)
    {
        double half = Len(p) / 2, acc = 0;
        for (int i = 1; i < p.Count; i++) { double l = (p[i] - p[i - 1]).Length; if (acc + l >= half) return p[i - 1] + (p[i] - p[i - 1]) * ((half - acc) / Math.Max(l, 1e-12)); acc += l; }
        return p[^1];
    }

    /// <summary>D5: разрез по виду-родителю: плоскость через середину габарита вида-родителя, взгляд — dir.</summary>
    public DrawingView AddSection(DrawingScene scene, string parent, string dir, double? depth = null)
    {
        var letter = NextLetter();
        var f = ViewFrame.Of(dir);
        double d = depth ?? 0;
        if (depth == null && scene.Lines.TryGetValue(parent, out var pl) && pl.Count > 0)
        {
            // середина габарита родителя по направлению нормали разреза на виде-родителе
            var pf = ViewFrame.Of(DirOf(Views.First(x => x.Name == parent)));
            var n2 = new Vector2d(Vector3d.Dot(f.Toward, pf.Right), Vector3d.Dot(f.Toward, pf.Up));
            var (mn, mx) = Box(pl);
            d = Vector2d.Dot((mn + mx) / 2, n2);
        }
        var v = new DrawingView { Name = $"{letter}-{letter}", Dir = dir, IsSection = true, Parent = parent, Label = letter, CutDepth = d, ShowHidden = false };
        Views.Add(v);
        return v;
    }

    /// <summary>
    /// k158: вспомогательный вид: взгляд перпендикулярно линии (a, b) вида-родителя (координаты вида, мм модели) — наклонная
    /// грань видна в натуральную величину. Наблюдатель — с внешней стороны линии (от середины габарита вида-родителя).
    /// </summary>
    public DrawingView AddAuxiliary(DrawingScene scene, string parent, Vector2d a, Vector2d b)
    {
        var pv = Views.First(x => x.Name == parent);
        var pf = ViewFrame.Of(DirOf(pv));
        var t2 = b - a; if (t2.Length < 1e-9) throw new ArgumentException("aux: zero line");
        t2.Normalize();
        var n2 = new Vector2d(-t2.Y, t2.X);
        var m = (a + b) / 2;
        if (scene.Lines.TryGetValue(parent, out var pl) && pl.Count > 0)
        {
            var (mn, mx) = Box(pl);
            if (Vector2d.Dot(m - (mn + mx) / 2, n2) < 0) n2 = -n2;
        }
        var T3 = (pf.Right * t2.X + pf.Up * t2.Y).Normalized();
        var N3 = (pf.Right * n2.X + pf.Up * n2.Y).Normalized();
        // мутация P3D_AUXFRAME=0 — вид смотрит как вид-родитель (без своей рамки)
        var frame = AuxFrameOff ? pf : new ViewFrame(T3, Vector3d.Cross(N3, T3).Normalized());
        var letter = NextLetter();
        var v = new DrawingView { Name = letter, Dir = frame.Encode(), IsAux = true, Parent = parent, Label = letter, DetailX = m.X, DetailY = m.Y, ShowHidden = false };
        Views.Add(v);
        return v;
    }

    static readonly bool AuxFrameOff = Environment.GetEnvironmentVariable("P3D_AUXFRAME") == "0";

    /// <summary>k158: разрыв вида: полоса [a, b] по X вида (мм модели) не показывается.</summary>
    public void AddBreak(string view, double a, double b)
    {
        var v = Views.First(x => x.Name == view);
        v.BreakA = Math.Min(a, b); v.BreakB = Math.Max(a, b);
    }

    /// <summary>D5: выносной элемент: круг на виде-родителе (центр и радиус — мм модели), масштаб — в 2 раза крупнее листа.</summary>
    public DrawingView AddDetail(string parent, Vector2d centre, double radius, double scale)
    {
        var letter = NextLetter();
        var v = new DrawingView { Name = letter, IsDetail = true, Parent = parent, Label = letter, DetailX = centre.X, DetailY = centre.Y, DetailR = radius, Scale = scale, ShowHidden = false };
        Views.Add(v);
        return v;
    }
}
