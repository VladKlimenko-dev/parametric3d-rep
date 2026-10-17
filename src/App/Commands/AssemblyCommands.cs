using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using OpenTK.Mathematics;
using Parametric3D.App.Operations;
using Parametric3D.App.Services;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Mates;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13g: СБОРКА — кнопки таблицы иконок: новая сборка, вставить/создать компонент, FreeForm, скелет, открыть, править в
/// контексте, заменить, зафиксировать, массивы (по шагу и по образцу отверстий), зеркало, подсборка, разнесённый вид,
/// спецификация, перетаскивание, перевернуть ограничение; ограничения (совпадение, расстояние, угол, параллельно,
/// перпендикулярно, соосность, касание, вставка, точка на, жёстко) и соединения (жёсткое, шарнир, ползун, цилиндр, шар в
/// цилиндре, плоскостное, шаровое, паз, сварка, шесть степеней).
/// </summary>
public static class AssemblyCommands
{
    public enum GeoKind { Plane, Axis, Point }
    /// <summary>Геометрия ссылки: деталь (null — сборка), точка и направление в координатах детали (или сборки), вид, радиус.</summary>
    public readonly record struct Geo(Part? Part, Vector3d O, Vector3d A, GeoKind Kind, double Radius = 0);

    static bool Asm(MainWindow w) => w.Session?.IsAssembly == true;
    static bool AsmMode(MainWindow w) => Asm(w) && w.Session.ActivePart == null && !w.InSketch;

    public static IEnumerable<Part> Components(MainWindow w) => w.Session.Document.Root.Children.OfType<Part>();

    /// <summary>Выбранные компоненты: тело, грань, ребро, вершина компонента.</summary>
    public static List<Part> SelectedComponents(MainWindow w)
        => w.ViewPort.Selection.Items.Select(r => r.Target as Part ?? r.Owner as Part).OfType<Part>().Where(p => p.Parent == w.Session.Document.Root).Distinct().ToList();

    public static Vector3d ToWorld(Part p, Vector3d local)
    {
        var v = Vector3.Transform(new Vector3((float)local.X, (float)local.Y, (float)local.Z), p.Rotation) + p.Translation;
        return new Vector3d(v.X, v.Y, v.Z);
    }
    public static Vector3d DirToWorld(Part p, Vector3d d)
    {
        var v = Vector3.Transform(new Vector3((float)d.X, (float)d.Y, (float)d.Z), p.Rotation);
        return new Vector3d(v.X, v.Y, v.Z);
    }

    /// <summary>Геометрия выбранного для ограничений: плоская грань, ось (цилиндр, окружность, прямое ребро, опорная ось), точка.</summary>
    public static Geo? GeoOf(PickRef r)
    {
        if (r.Owner is Part p && r.Kind == PickKind.Face)
        {
            if (Refs.FacePlane(p, r.Id) is { } pl) return new Geo(p, pl.P, pl.N, GeoKind.Plane);
            if (p.FindFace(r.Id) is { } f)
                try { var ax = DatumBuilder.AxisOfFace(f); return new Geo(p, ax.At, ax.Dir, GeoKind.Axis, Kernel.Ops.DirectEdit.RadiusAfter(f, 0) ?? 0); } catch { }
            return null;
        }
        if (r.Owner is Part pe && r.Kind == PickKind.Edge && pe.FindEdge(r.Id) is { } e)
        {
            if (e.Curve is ArcCurve3 a) return new Geo(pe, a.Frame.Origin, a.Frame.W.Normalized(), GeoKind.Axis, a.Radius);
            if (e.V0 != null && e.V1 != null && (e.V1.P - e.V0.P).LengthSquared > 1e-12) return new Geo(pe, e.V0.P, (e.V1.P - e.V0.P).Normalized(), GeoKind.Axis);
        }
        if (r.Owner is Part pv && r.Kind == PickKind.Vertex && pv.FindVertex(r.Id) is { } v) return new Geo(pv, v.P, Vector3d.UnitZ, GeoKind.Point);
        // опорные элементы сборки (или детали — в её координатах)
        var owner = r.Owner as Part;
        if (r.Target is DatumPlane dp) return new Geo(owner, dp.Frame.Origin, dp.Frame.W, GeoKind.Plane);
        if (r.Target is DatumAxis dx) return new Geo(owner, dx.Origin, dx.Direction.Normalized(), GeoKind.Axis);
        if (r.Target is DatumPoint dpt) return new Geo(owner, dpt.P, Vector3d.UnitZ, GeoKind.Point);
        return null;
    }

    static List<Geo> SelectedGeos(MainWindow w) => w.ViewPort.Selection.Items.Select(GeoOf).OfType<Geo>().ToList();

    /// <summary>Решить сборку; невыполненное — сообщением.</summary>
    public static List<string> Solve(MainWindow w)
    {
        var stuck = w.Session.Document.Root.SolveMates();
        w.ViewPort.MarkDirty(); w.RebuildTree();
        if (stuck.Count > 0) w.ShowToast(L("Сборка: не выполнено — {0}", string.Join("; ", stuck.Take(3))));
        return stuck;
    }

    // ---------------------------------------------------------------- ограничения

    public enum MateCmd { Coincident, Distance, Angle, Parallel, Perpendicular, Concentric, Tangent, Insert, PointOn, Rigid }

    /// <summary>Порядок пары: двигается деталь первой ссылки; если она зафиксирована (или это сборка) — вторая.</summary>
    static (Geo Moving, Geo Base)? Pair(Geo a, Geo b)
    {
        if (a.Part != null && !a.Part.Fixed) return (a, b);
        if (b.Part != null && !b.Part.Fixed) return (b, a);
        return null;
    }

    /// <summary>Ограничение по двум ссылкам; null — не подходит (сообщение — в why).</summary>
    public static List<(Part Part, AssemblyConstraint C)>? Plan(MateCmd cmd, Geo g1, Geo g2, double value, out string? why)
    {
        why = null;
        if (g1.Part != null && ReferenceEquals(g1.Part, g2.Part)) { why = L("Ссылки — на одном компоненте"); return null; }
        if (Pair(g1, g2) is not { } pr) { why = L("Оба компонента зафиксированы"); return null; }
        var (m, b) = pr;
        AssemblyConstraint C(ConstraintKind k, double offset = 0, double angle = 0, Vector3d? pAxis = null) => new(k)
        {
            PartOrigin = m.O, PartAxis = pAxis ?? m.A, BasePart = b.Part, AssemblyOrigin = b.O, AssemblyAxis = b.A, Offset = offset, Angle = angle,
        };
        var res = new List<(Part, AssemblyConstraint)>();
        switch (cmd)
        {
            case MateCmd.Coincident or MateCmd.Distance when m.Kind == GeoKind.Plane && b.Kind == GeoKind.Plane:
                res.Add((m.Part!, C(ConstraintKind.Mate, cmd == MateCmd.Distance ? value : 0))); break;
            case MateCmd.Coincident or MateCmd.Concentric or MateCmd.Insert when m.Kind == GeoKind.Axis && b.Kind == GeoKind.Axis:
                res.Add((m.Part!, C(ConstraintKind.Insert))); break;
            case MateCmd.Coincident or MateCmd.PointOn or MateCmd.Distance when m.Kind == GeoKind.Point && b.Kind == GeoKind.Plane:
                res.Add((m.Part!, C(ConstraintKind.PointOnSurface, cmd == MateCmd.Distance ? value : 0))); break;
            case MateCmd.Coincident or MateCmd.PointOn when m.Kind == GeoKind.Point && b.Kind == GeoKind.Axis:
                res.Add((m.Part!, C(ConstraintKind.PointOnLine))); break;
            case MateCmd.Coincident or MateCmd.PointOn when m.Kind == GeoKind.Plane && b.Kind == GeoKind.Point:
            {
                // точка базы на плоскости детали: меняются роли — двигается деталь плоскости
                res.Add((m.Part!, new AssemblyConstraint(ConstraintKind.PointOnSurface) { PartOrigin = m.O, PartAxis = m.A, BasePart = b.Part, AssemblyOrigin = b.O, AssemblyAxis = DirOf(b, m) })); break;
            }
            case MateCmd.Angle or MateCmd.Parallel or MateCmd.Perpendicular when m.Kind != GeoKind.Point && b.Kind != GeoKind.Point:
                res.Add((m.Part!, C(ConstraintKind.Orient, 0, cmd switch { MateCmd.Parallel => 0, MateCmd.Perpendicular => 90, _ => value }))); break;
            case MateCmd.Tangent when (m.Kind == GeoKind.Axis && b.Kind == GeoKind.Plane) || (m.Kind == GeoKind.Plane && b.Kind == GeoKind.Axis):
            {
                // плоскость касается цилиндра: расстояние от оси до плоскости — радиус цилиндра
                double r = m.Kind == GeoKind.Axis ? m.Radius : b.Radius;
                if (!(r > 0)) { why = L("Выберите цилиндрическую грань и плоскость"); return null; }
                if (m.Kind == GeoKind.Axis) res.Add((m.Part!, C(ConstraintKind.Tangent, r)));
                else res.Add((m.Part!, new AssemblyConstraint(ConstraintKind.Tangent) { PartOrigin = m.O, PartAxis = m.A, BasePart = b.Part, AssemblyOrigin = b.O, AssemblyAxis = b.A, Offset = r }));
                break;
            }
            case MateCmd.Rigid:
            {
                // жёстко: начало и оси детали — в текущем положении относительно базы (две связи: СК и ориентация X)
                var mp = m.Part!;
                var oW = ToWorld(mp, Vector3d.Zero); var zW = DirToWorld(mp, Vector3d.UnitZ); var xW = DirToWorld(mp, Vector3d.UnitX);
                Vector3d L2(Vector3d w) => b.Part == null ? w : InvPoint(b.Part, w);
                Vector3d LD(Vector3d w) => b.Part == null ? w : InvDir(b.Part, w);
                res.Add((mp, new AssemblyConstraint(ConstraintKind.CoordinateSystem, L("Жёстко")) { PartOrigin = Vector3d.Zero, PartAxis = Vector3d.UnitZ, BasePart = b.Part, AssemblyOrigin = L2(oW), AssemblyAxis = LD(zW) }));
                res.Add((mp, new AssemblyConstraint(ConstraintKind.Orient, L("Жёстко X")) { PartOrigin = Vector3d.Zero, PartAxis = Vector3d.UnitX, BasePart = b.Part, AssemblyOrigin = L2(oW), AssemblyAxis = LD(xW), Angle = 0 }));
                break;
            }
            default:
                why = cmd switch
                {
                    MateCmd.Concentric or MateCmd.Insert => L("Выберите две цилиндрические грани (или окружности, оси)"),
                    MateCmd.Tangent => L("Выберите цилиндрическую грань и плоскость"),
                    MateCmd.PointOn => L("Выберите вершину (точку) и плоскость или ось"),
                    MateCmd.Angle or MateCmd.Parallel or MateCmd.Perpendicular => L("Выберите две плоскости или оси"),
                    _ => L("Выберите две плоские грани (или оси, или точку и плоскость)"),
                };
                return null;
        }
        foreach (var (_, c) in res) if (string.IsNullOrEmpty(c.Name) || c.Name == c.Kind.ToString()) c.Name = L(cmd.ToString() switch
        {
            "Coincident" => "Совпадение", "Distance" => "Расстояние", "Angle" => "Угол", "Parallel" => "Параллельно", "Perpendicular" => "Перпендикулярно",
            "Concentric" => "Соосность", "Tangent" => "Касание", "Insert" => "Вставка", "PointOn" => "Точка на", _ => "Жёстко",
        });
        return res;
    }

    static Vector3d DirOf(Geo b, Geo m) => b.A;
    static Vector3d InvPoint(Part p, Vector3d w)
    {
        var v = new Vector3((float)w.X, (float)w.Y, (float)w.Z) - p.Translation;
        var r = Vector3.Transform(v, Quaternion.Invert(p.Rotation));
        return new Vector3d(r.X, r.Y, r.Z);
    }
    static Vector3d InvDir(Part p, Vector3d w)
    {
        var r = Vector3.Transform(new Vector3((float)w.X, (float)w.Y, (float)w.Z), Quaternion.Invert(p.Rotation));
        return new Vector3d(r.X, r.Y, r.Z);
    }

    public static async void Constrain(MainWindow w, MateCmd cmd)
    {
        var gs = SelectedGeos(w);
        if (gs.Count < 2) { w.ShowToast(L("Выберите две ссылки на разных компонентах (или компонент и плоскость сборки)"), error: false); return; }
        double value = 0;
        if (cmd is MateCmd.Distance or MateCmd.Angle)
        {
            var t = await Dialogs.Prompt(w, L(cmd == MateCmd.Distance ? "Расстояние" : "Угол"), L(cmd == MateCmd.Distance ? "Расстояние, мм:" : "Угол, °:"), cmd == MateCmd.Distance ? "10" : "45");
            if (t == null || !double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return;
        }
        var plan = Plan(cmd, gs[0], gs[1], value, out var why);
        // вставка: две оси и две плоскости — соосность и прижатие
        if (cmd == MateCmd.Insert && gs.Count >= 4 && plan != null)
        {
            var planes = gs.Where(g => g.Kind == GeoKind.Plane).ToList();
            if (planes.Count >= 2 && Plan(MateCmd.Coincident, planes[0], planes[1], 0, out _) is { } p2) plan.AddRange(p2);
        }
        if (plan == null) { w.ShowToast(why ?? L("Не подходит")); return; }
        Apply(w, plan, L("Ограничение: {0}", plan[0].C.Name));
    }

    public static void Apply(MainWindow w, List<(Part Part, AssemblyConstraint C)> plan, string undo)
    {
        w.Session.Edit(undo, () => { foreach (var (p, c) in plan) p.Constraints.Add(c); });
        Solve(w);
    }

    // ---------------------------------------------------------------- соединения

    public static Mate? PlanJoint(MateType type, Geo g1, Geo g2, out Part? moving, out string? why)
    {
        why = null; moving = null;
        if (Pair(g1, g2) is not { } pr) { why = L("Оба компонента зафиксированы"); return null; }
        var (m, b) = pr; moving = m.Part;
        bool axial = type is MateType.Pin or MateType.Slider or MateType.Cylinder or MateType.BallCylinder or MateType.Slot;
        if (axial && (m.Kind != GeoKind.Axis || b.Kind != GeoKind.Axis)) { why = L("Выберите две оси (цилиндрические грани, окружности или прямые рёбра)"); return null; }
        if (type == MateType.Planar && (m.Kind != GeoKind.Plane || b.Kind != GeoKind.Plane)) { why = L("Выберите две плоские грани"); return null; }
        if (type == MateType.Ball && (m.Kind == GeoKind.Plane || b.Kind == GeoKind.Plane)) { why = L("Выберите две точки (вершины, центры окружностей)"); return null; }
        var mate = new Mate(type, L(type switch
        {
            MateType.Pin => "Шарнир", MateType.Slider => "Ползун", MateType.Cylinder => "Цилиндрическое", MateType.Planar => "Плоскостное", MateType.Ball => "Шаровое",
            MateType.BallCylinder => "Шар в цилиндре", MateType.Slot => "Паз", MateType.Free => "Шесть степеней", _ => "Жёсткое",
        }))
        {
            BasePart = b.Part, AssemblyOrigin = b.O, AssemblyAxis = b.A, PartOrigin = m.O,
            // плоскости прижимаются друг к другу: нормаль детали — против нормали базы
            PartAxis = m.Kind == GeoKind.Plane && b.Kind == GeoKind.Plane ? -m.A : m.A,
        };
        if (type == MateType.Slot) mate.SlotNormal = Kernel.Frame3.FromNormal(Vector3d.Zero, b.A).U;
        return mate;
    }

    public static void Joint(MainWindow w, MateType type)
    {
        var gs = SelectedGeos(w);
        if (gs.Count < 2 && type != MateType.Free) { w.ShowToast(L("Выберите ссылку на компоненте и ссылку на базе (другой компонент или плоскость сборки)"), error: false); return; }
        if (type == MateType.Free)
        {
            var comps = SelectedComponents(w);
            if (comps.Count == 0) { w.ShowToast(L("Выберите компонент"), error: false); return; }
            w.Session.Edit(L("Соединение: шесть степеней"), () => { foreach (var c in comps) { c.Mates.Clear(); c.Constraints.Clear(); c.Mates.Add(new Mate(MateType.Free, L("Шесть степеней"))); } });
            Solve(w); return;
        }
        var mate = PlanJoint(type, gs[0], gs[1], out var moving, out var why);
        if (mate == null || moving == null) { w.ShowToast(why ?? L("Не подходит")); return; }
        w.Session.Edit(L("Соединение: {0}", mate.Name), () => { moving.Mates.Insert(0, mate); });
        Solve(w);
    }

    // ---------------------------------------------------------------- компоненты

    public static Part LoadComponent(string path, List<string> warnings)
    {
        Part part;
        if (path.EndsWith(DocumentSession.PartExtension, StringComparison.OrdinalIgnoreCase))
        {
            var s = DocumentSession.Open(path);
            part = s.Part ?? throw new InvalidDataException(L("В файле нет детали"));
            s.Document.Root.Remove(part);
        }
        else part = Exchange.Read(File.ReadAllText(path), out _, warnings) ?? throw new InvalidDataException(string.Join("; ", warnings));
        part.SourcePath = path;
        part.Name = Path.GetFileNameWithoutExtension(path);
        return part;
    }

    /// <summary>Компонент в сборку: справа от уже стоящих (габарит + 20), первый — в начале.</summary>
    public static Part AddComponent(MainWindow w, Part part, string undo)
    {
        var root = w.Session.Document.Root;
        var names = root.Children.Select(c => c.Name).ToHashSet();
        string n = part.Name; for (int i = 2; names.Contains(n); i++) n = $"{part.Name} ({i})";
        part.Name = n;
        double x = 0;
        var placed = Components(w).ToList();
        if (placed.Count > 0)
        {
            var b = w.Session.Document.Bounds;
            if (!b.IsEmpty) x = b.Max.X + 20;
        }
        part.Rebuild(w.Session.Document.ScopeFor(part));
        var pb = part.Bounds;
        if (!pb.IsEmpty && placed.Count > 0) x -= pb.Min.X;
        part.Translation = new Vector3((float)x, 0, 0);
        if (placed.Count == 0) part.Fixed = true;   // первый компонент — основа
        w.Session.Edit(undo, () => root.Add(part));
        w.Session.Document.RebuildDirty();
        w.ViewPort.MarkDirty(); w.RebuildTree(); w.ViewPort.ZoomToFit();
        return part;
    }

    static async Task<string?> PickFile(MainWindow w, string title, params string[] patterns)
    {
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(L("Детали и тела")) { Patterns = patterns } },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task InsertComponent(MainWindow w, bool freeform = false)
    {
        var path = await PickFile(w, L(freeform ? "Вставить FreeForm-тело" : "Вставить компонент"), freeform ? new[] { "*.step", "*.stp", "*.iges", "*.igs", "*" + DocumentSession.PartExtension } : new[] { "*" + DocumentSession.PartExtension, "*.step", "*.stp" });
        if (path == null) return;
        try { AddComponent(w, LoadComponent(path, new List<string>()), L("Вставить компонент")); }
        catch (Exception ex) { w.ShowToast(L("Вставить компонент: {0}", ex.Message)); }
    }

    public static Part CreateComponent(MainWindow w, bool skeleton = false)
    {
        var part = new Part(L(skeleton ? "Скелет" : "Компонент")) { Skeleton = skeleton, Fixed = skeleton };
        var root = w.Session.Document.Root;
        var names = root.Children.Select(c => c.Name).ToHashSet();
        for (int i = 1; names.Contains(part.Name); i++) part.Name = L(skeleton ? "Скелет {0}" : "Компонент {0}", i);
        w.Session.Edit(L(skeleton ? "Скелетная модель" : "Создать компонент"), () => root.Add(part));
        EditInContext(w, part);
        return part;
    }

    /// <summary>Править в контексте: операции детали — над этим компонентом (повторно или пусто — назад к сборке).</summary>
    public static void EditInContext(MainWindow w, Part? part)
    {
        if (w.InSketch) w.FinishSketch();
        w.Session.ActivePart = part == null || ReferenceEquals(w.Session.ActivePart, part) ? null : part;
        w.ViewPort.ActivePart = w.Session.ActivePart;
        w.ViewPort.Selection.Clear();
        w.RebuildUi(); w.ViewPort.MarkDirty();
        w.ShowToast(w.Session.ActivePart != null ? L("Правка в контексте: «{0}» (повторно — назад к сборке)", w.Session.ActivePart.Name) : L("Сборка"), error: false);
    }

    public static async void OpenComponent(MainWindow w)
    {
        var p = SelectedComponents(w).FirstOrDefault(); if (p == null) { w.ShowToast(L("Выберите компонент"), error: false); return; }
        DocumentSession s;
        if (p.SourcePath != null && File.Exists(p.SourcePath)) s = DocumentSession.Open(p.SourcePath);
        else
        {
            var (clone, _) = DocumentFile.Load(DocumentFile.Save(p, w.Session.Document.ScopeFor(p)));
            clone.Name = p.Name;
            s = DocumentSession.FromPart(clone, p.Name);
        }
        var win = new MainWindow(s);
        win.Show();
        await Task.CompletedTask;
    }

    public static async void ReplaceComponent(MainWindow w)
    {
        var old = SelectedComponents(w).FirstOrDefault(); if (old == null) { w.ShowToast(L("Выберите компонент"), error: false); return; }
        var path = await PickFile(w, L("Заменить компонент"), "*" + DocumentSession.PartExtension, "*.step", "*.stp");
        if (path == null) return;
        try { Replace(w, old, LoadComponent(path, new List<string>())); }
        catch (Exception ex) { w.ShowToast(L("Заменить компонент: {0}", ex.Message)); }
    }

    /// <summary>Замена: положение, фиксация, ограничения и ссылки других компонентов переходят к новому.</summary>
    public static void Replace(MainWindow w, Part old, Part fresh)
    {
        var root = w.Session.Document.Root;
        w.Session.Edit(L("Заменить компонент"), () =>
        {
            fresh.Translation = old.Translation; fresh.Rotation = old.Rotation; fresh.Fixed = old.Fixed; fresh.SubAssembly = old.SubAssembly;
            fresh.Constraints.AddRange(old.Constraints); fresh.Mates.AddRange(old.Mates);
            foreach (var p in root.Children.OfType<Part>())
            {
                foreach (var c in p.Constraints) if (ReferenceEquals(c.BasePart, old)) c.BasePart = fresh;
                foreach (var m in p.Mates) if (ReferenceEquals(m.BasePart, old)) m.BasePart = fresh;
            }
            int i = root.Children.IndexOf(old);
            root.Remove(old); root.Add(fresh);
            if (i >= 0) { root.Children.Remove(fresh); root.Children.Insert(Math.Min(i, root.Children.Count), fresh); }
        });
        w.Session.Document.RebuildDirty();
        Solve(w);
    }

    public static void ToggleFix(MainWindow w)
    {
        var cs = SelectedComponents(w); if (cs.Count == 0) { w.ShowToast(L("Выберите компонент"), error: false); return; }
        bool v = !cs.All(c => c.Fixed);
        w.Session.Edit(L(v ? "Зафиксировать" : "Освободить"), () => { foreach (var c in cs) c.Fixed = v; });
        Solve(w);
    }

    static Part Clone(MainWindow w, Part p)
    {
        var (c, _) = DocumentFile.Load(DocumentFile.Save(p, w.Session.Document.ScopeFor(p)));
        c.Name = p.Name; c.SourcePath = p.SourcePath; c.Rotation = p.Rotation; c.Translation = p.Translation; c.SubAssembly = p.SubAssembly;
        c.Rebuild(w.Session.Document.ScopeFor(c));
        return c;
    }

    /// <summary>Массив компонентов: копии со сдвигом i·шаг (i = 1…n−1).</summary>
    public static List<Part> Pattern(MainWindow w, Part src, int count, Vector3d step)
    {
        var made = new List<Part>();
        var root = w.Session.Document.Root;
        w.Session.Edit(L("Массив компонентов"), () =>
        {
            for (int i = 1; i < count; i++)
            {
                var c = Clone(w, src);
                c.Translation = src.Translation + new Vector3((float)(step.X * i), (float)(step.Y * i), (float)(step.Z * i));
                c.Name = $"{src.Name} [{i + 1}]";
                root.Add(c); made.Add(c);
            }
        });
        w.Session.Document.RebuildDirty(); w.ViewPort.MarkDirty(); w.RebuildTree();
        return made;
    }

    public static async void PatternDialog(MainWindow w)
    {
        var src = SelectedComponents(w).FirstOrDefault(); if (src == null) { w.ShowToast(L("Выберите компонент"), error: false); return; }
        var t = await Dialogs.Prompt(w, L("Массив компонентов"), L("Число и шаг: n dx dy dz (через пробел):"), "3 50 0 0");
        if (t == null) return;
        var a = t.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        if (a.Length < 4 || a.Any(double.IsNaN) || a[0] < 2) { w.ShowToast(L("Не число: {0}", t)); return; }
        Pattern(w, src, (int)a[0], new Vector3d(a[1], a[2], a[3]));
    }

    /// <summary>
    /// Массив по образцу: компонент стоит в отверстии (окружность другого компонента) — копии во все отверстия того же
    /// радиуса и направления у этого компонента (сдвиг на разницу центров).
    /// </summary>
    public static List<Part> PatternByReference(MainWindow w, Part src, Part host, Vector3d holeCentreLocal, Vector3d holeAxisLocal, double radius)
    {
        var centres = new List<Vector3d>();
        foreach (var e in host.Bodies.SelectMany(b => b.Edges))
            if (e.Curve is ArcCurve3 a && Math.Abs(a.Radius - radius) < 1e-6 && Math.Abs(Math.Abs(Vector3d.Dot(a.Frame.W.Normalized(), holeAxisLocal.Normalized())) - 1) < 1e-6)
            {
                // окружности одного отверстия (верх и низ) — один центр по оси
                var c = a.Frame.Origin;
                var ax = holeAxisLocal.Normalized();
                bool same(Vector3d q) { var d = q - c; d -= ax * Vector3d.Dot(d, ax); return d.Length < 1e-6; }
                if (!centres.Any(same)) centres.Add(c);
            }
        var c0w = ToWorld(host, holeCentreLocal);
        var made = new List<Part>();
        var root = w.Session.Document.Root;
        w.Session.Edit(L("Массив по образцу"), () =>
        {
            int k = 1;
            foreach (var c in centres)
            {
                var cw = ToWorld(host, c);
                var d = cw - c0w; var ax = DirToWorld(host, holeAxisLocal).Normalized(); d -= ax * Vector3d.Dot(d, ax);
                if (d.Length < 1e-6) continue;
                var cp = Clone(w, src);
                cp.Translation = src.Translation + new Vector3((float)d.X, (float)d.Y, (float)d.Z);
                cp.Name = $"{src.Name} [{++k}]";
                root.Add(cp); made.Add(cp);
            }
        });
        w.Session.Document.RebuildDirty(); w.ViewPort.MarkDirty(); w.RebuildTree();
        return made;
    }

    public static void PatternRefCommand(MainWindow w)
    {
        var comps = SelectedComponents(w);
        var holeRef = w.ViewPort.Selection.Items.FirstOrDefault(r => GeoOf(r) is { Kind: GeoKind.Axis, Radius: > 0 } g && g.Part != null);
        if (comps.Count < 2 || holeRef.IsEmpty) { w.ShowToast(L("Выберите компонент (крепёж) и окружность отверстия другого компонента"), error: false); return; }
        var g = GeoOf(holeRef)!.Value; var host = g.Part!;
        var src = comps.First(c => !ReferenceEquals(c, host));
        var made = PatternByReference(w, src, host, g.O, g.A, g.Radius);
        w.ShowToast(L("Копий: {0}", made.Count), error: false);
    }

    /// <summary>Зеркальный компонент: копия детали с отражённым телом относительно плоскости (в мировых координатах).</summary>
    public static Part Mirror(MainWindow w, Part src, Vector3d at, Vector3d normal)
    {
        var c = Clone(w, src);
        // плоскость — в координатах детали
        var atL = InvPoint(src, at); var nL = InvDir(src, normal).Normalized();
        c.Name = L("{0} (зеркало)", src.Name);
        var mf = new MirrorFeature(L("Зеркало компонента")) { At = atL, Normal = nL, ReplaceBody = true };
        var root = w.Session.Document.Root;
        w.Session.Edit(L("Зеркало компонента"), () => { c.AddFeature(mf); root.Add(c); });
        w.Session.Document.RebuildDirty(); w.ViewPort.MarkDirty(); w.RebuildTree();
        return c;
    }

    public static void MirrorCommand(MainWindow w)
    {
        var src = SelectedComponents(w).FirstOrDefault();
        var pl = w.ViewPort.Selection.Items.Select(r => (r, Refs.PlaneOf(r))).FirstOrDefault(x => x.Item2 != null);
        if (src == null || pl.Item2 is not { } plane) { w.ShowToast(L("Выберите компонент и плоскость зеркала"), error: false); return; }
        var at = plane.Frame.Origin; var n = plane.Frame.W;
        if (pl.r.Owner is Part op && op.Parent == w.Session.Document.Root) { at = ToWorld(op, at); n = DirToWorld(op, n); }
        Mirror(w, src, at, n);
    }

    public static async void SubAssembly(MainWindow w)
    {
        var cs = SelectedComponents(w); if (cs.Count == 0) { w.ShowToast(L("Выберите компоненты"), error: false); return; }
        var name = await Dialogs.Prompt(w, L("Подсборка из выбранных"), L("Имя подсборки:"), L("Подсборка {0}", Components(w).Select(c => c.SubAssembly).Where(x => x != null).Distinct().Count() + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        w.Session.Edit(L("Подсборка"), () => { foreach (var c in cs) c.SubAssembly = name.Trim(); });
        w.RebuildTree();
    }

    // ---------------------------------------------------------------- разнесённый вид

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, Dictionary<Part, Vector3>> _exploded = new();
    public static bool IsExploded(MainWindow w) => _exploded.TryGetValue(w.Session.Document.Root, out _);

    /// <summary>Разнесённый вид: компоненты отходят от центра сборки (шаги вида сохраняются в документе); повторно — собрать.</summary>
    public static void ToggleExplode(MainWindow w)
    {
        var root = w.Session.Document.Root;
        if (_exploded.TryGetValue(root, out var orig))
        {
            foreach (var (p, t) in orig) p.Translation = t;
            _exploded.Remove(root);
            w.ViewPort.MarkDirty(); return;
        }
        var view = root.ExplodedViews.FirstOrDefault();
        var parts = Components(w).ToList();
        if (view == null)
        {
            view = new ExplodedView(L("Разнесённый вид"));
            var b = w.Session.Document.Bounds; var c = b.IsEmpty ? Vector3d.Zero : (b.Min + b.Max) / 2; double size = b.IsEmpty ? 100 : (b.Max - b.Min).Length;
            foreach (var p in parts)
            {
                var pb = p.Bounds; if (pb.IsEmpty) continue;
                var pc = ToWorld(p, (pb.Min + pb.Max) / 2);
                var d = pc - c; if (d.Length < 1e-6) d = Vector3d.UnitZ;
                view.Steps.Add(new ExplodedView.Step { PartName = p.Name, Direction = d.Normalized(), Distance = 0.5 * size, Order = 0 });
            }
            w.Session.Edit(L("Разнесённый вид"), () => root.ExplodedViews.Add(view));
        }
        var saved = new Dictionary<Part, Vector3>();
        foreach (var st in view.Steps)
            if (parts.FirstOrDefault(p => p.Name == st.PartName) is { } p)
            {
                saved[p] = p.Translation;
                var s = st.Shift; p.Translation += new Vector3((float)s.X, (float)s.Y, (float)s.Z);
            }
        _exploded.AddOrUpdate(root, saved);
        w.ViewPort.MarkDirty();
    }

    // ---------------------------------------------------------------- спецификация

    public static List<(int No, string Name, int Count, string Material, double Mass, string? Sub)> Bom(MainWindow w)
    {
        var rows = Components(w).Where(p => !p.Skeleton)
            .GroupBy(p => (Key: p.SourcePath ?? BaseName(p.Name), p.SubAssembly))
            .Select(g => (Name: BaseName(g.First().Name), Count: g.Count(), Material: g.First().Material?.Name ?? "—", Mass: g.First().Mass, Sub: g.Key.SubAssembly))
            .OrderBy(r => r.Sub ?? "").ThenBy(r => r.Name).ToList();
        return rows.Select((r, i) => (i + 1, r.Name, r.Count, r.Material, r.Mass, r.Sub)).ToList();
    }

    static string BaseName(string n) { int i = n.IndexOf(" ["); if (i < 0) i = n.IndexOf(" ("); return i > 0 ? n[..i] : n; }

    public static async void ShowBom(MainWindow w)
    {
        var rows = Bom(w);
        string text = string.Join("\n", rows.Select(r => $"{r.No}\t{r.Name}\t{r.Count}\t{r.Material}\t{r.Mass:0.###}{(r.Sub != null ? "\t" + r.Sub : "")}"));
        var win = new Window { Title = L("Спецификация"), SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("P3.PanelBg"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,220,60,140,90,140"), Margin = new Thickness(4) };
        void Cell(int r, int c, string t, bool head = false) { var tb = new TextBlock { Text = t, Margin = new Thickness(4, 2), FontWeight = head ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal }; Grid.SetRow(tb, r); Grid.SetColumn(tb, c); grid.Children.Add(tb); }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var (h, i) in new[] { "№", L("Наименование"), L("Кол."), L("Материал"), L("Масса, кг"), L("Подсборка") }.Select((h, i) => (h, i))) Cell(0, i, h, true);
        int row = 1;
        foreach (var r in rows) { grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Cell(row, 0, r.No.ToString()); Cell(row, 1, r.Name); Cell(row, 2, r.Count.ToString()); Cell(row, 3, r.Material); Cell(row, 4, r.Mass.ToString("0.###")); Cell(row, 5, r.Sub ?? ""); row++; }
        var copy = new Button { Content = L("Копировать таблицу") };
        copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(win)?.Clipboard is { } cb) await cb.SetTextAsync(text); };
        win.Content = new StackPanel { Margin = new Thickness(14), Spacing = 10, Children = { grid, copy } };
        await win.ShowDialog(w);
    }

    // ---------------------------------------------------------------- перевернуть ограничение

    public static bool FlipConstraint(MainWindow w, Part part, int index)
    {
        if (index < 0) return false;
        if (index < part.Constraints.Count)
        {
            var c = part.Constraints[index];
            w.Session.Edit(L("Перевернуть ограничение"), () =>
            {
                if (c.Kind == ConstraintKind.Mate) c.Kind = ConstraintKind.Align;
                else if (c.Kind == ConstraintKind.Align) c.Kind = ConstraintKind.Mate;
                else c.PartAxis = -c.PartAxis;
            });
        }
        else if (index - part.Constraints.Count < part.Mates.Count)
        {
            var m = part.Mates[index - part.Constraints.Count];
            w.Session.Edit(L("Перевернуть соединение"), () => m.PartAxis = -m.PartAxis);
        }
        else return false;
        Solve(w);
        return true;
    }

    // ---------------------------------------------------------------- регистрация

    public static void Register(CommandRegistry r, MainWindow w)
    {
        const string A = "Сборка", C = "Сборка: ограничения", J = "Сборка: соединения";
        const string F = "Файл";
        r.Add(new AppCommand { Id = "file.new_assembly", Label = "Новая сборка", Group = F, Icon = "new_assembly_but.svg", Synonyms = "new assembly новая сборка",
            Run = async () => { if (await w.ConfirmDiscard()) w.SetSession(DocumentSession.NewAssembly()); } });
        r.Add(new AppCommand { Id = "file.insert_part", Label = "Вставить деталь из файла", Group = F, Icon = "insert_part_but.svg", Synonyms = "insert part file вставить деталь",
            CanRun = () => Asm(w), Run = async () => await InsertComponent(w) });
        bool AM() => AsmMode(w);
        bool Sel() => AsmMode(w) && SelectedComponents(w).Count > 0;
        r.Add(new AppCommand { Id = "as.insert_component", Label = "Вставить компонент", Group = A, Icon = "as_insert_component_but.svg", Synonyms = "insert component вставить компонент", CanRun = AM, Run = async () => await InsertComponent(w) });
        r.Add(new AppCommand { Id = "as.create_component", Label = "Создать компонент (в контексте)", Group = A, Icon = "as_create_component_but.svg", Synonyms = "create component создать компонент", CanRun = AM, Run = () => CreateComponent(w) });
        r.Add(new AppCommand { Id = "as.insert_freeform", Label = "Вставить FreeForm-тело", Group = A, Icon = "as_insert_freeform_but.svg", Synonyms = "insert freeform body вставить тело", CanRun = AM, Run = async () => await InsertComponent(w, freeform: true) });
        r.Add(new AppCommand { Id = "as.skeleton", Label = "Скелетная модель", Group = A, Icon = "as_skeleton_but.svg", Synonyms = "skeleton скелет", CanRun = AM, Run = () => CreateComponent(w, skeleton: true) });
        r.Add(new AppCommand { Id = "as.open_component", Label = "Открыть компонент", Group = A, Icon = "as_open_component_but.svg", Synonyms = "open component открыть компонент", CanRun = Sel, Run = () => OpenComponent(w) });
        r.Add(new AppCommand { Id = "as.edit_in_context", Label = "Править в контексте", Group = A, Icon = "as_edit_in_context_but.svg", Synonyms = "edit in context править в контексте",
            IsChecked = () => w.Session?.ActivePart != null && Asm(w), CanRun = () => Asm(w), Run = () => EditInContext(w, w.Session.ActivePart != null ? null : SelectedComponents(w).FirstOrDefault()) });
        r.Add(new AppCommand { Id = "as.replace", Label = "Заменить компонент", Group = A, Icon = "as_replace_but.svg", Synonyms = "replace component заменить компонент", CanRun = Sel, Run = () => ReplaceComponent(w) });
        r.Add(new AppCommand { Id = "as.fix", Label = "Зафиксировать компонент", Group = A, Icon = "as_fix_but.svg", Synonyms = "fix ground зафиксировать", CanRun = Sel, Run = () => ToggleFix(w) });
        r.Add(new AppCommand { Id = "as.pattern", Label = "Массив компонентов", Group = A, Icon = "as_pattern_but.svg", Synonyms = "pattern components массив компонентов", CanRun = Sel, Run = () => PatternDialog(w) });
        r.Add(new AppCommand { Id = "as.pattern_ref", Label = "Массив по образцу (по отверстиям)", Group = A, Icon = "as_pattern_ref_but.svg", Synonyms = "reference pattern holes массив по образцу отверстия", CanRun = Sel, Run = () => PatternRefCommand(w) });
        r.Add(new AppCommand { Id = "as.mirror", Label = "Зеркало компонента", Group = A, Icon = "as_mirror_but.svg", Synonyms = "mirror component зеркало компонента", CanRun = Sel, Run = () => MirrorCommand(w) });
        r.Add(new AppCommand { Id = "as.subassembly", Label = "Подсборка из выбранных", Group = A, Icon = "as_subassembly_but.svg", Synonyms = "subassembly подсборка", CanRun = Sel, Run = () => SubAssembly(w) });
        r.Add(new AppCommand { Id = "as.explode", Label = "Разнесённый вид", Group = A, Icon = "as_explode_but.svg", Synonyms = "explode разнесённый вид", CanRun = AM, IsChecked = () => Asm(w) && IsExploded(w), Run = () => ToggleExplode(w) });
        r.Add(new AppCommand { Id = "as.bom", Label = "Спецификация", Group = A, Icon = "as_bom_but.svg", Synonyms = "bom bill of materials спецификация", CanRun = () => Asm(w), Run = () => ShowBom(w) });
        r.Add(new AppCommand { Id = "as.drag", Label = "Перетащить компонент (проверка движения)", Group = A, Icon = "as_drag_but.svg", Synonyms = "drag move component перетащить",
            CanRun = AM, IsChecked = () => w.ViewPort.DragComponents, Run = () => { w.ViewPort.DragComponents = !w.ViewPort.DragComponents; w.RefreshToolStates(); w.ShowToast(w.ViewPort.DragComponents ? L("Выберите компонент и тяните левой кнопкой; ограничения возвращают его по свободам") : L("Перетаскивание выключено"), error: false); } });
        r.Add(new AppCommand { Id = "as.flip", Label = "Перевернуть направление ограничения", Group = A, Icon = "as_flip_but.svg", Synonyms = "flip constraint перевернуть ограничение",
            CanRun = () => Asm(w) && w.SelectedTreeConstraint() != null, Run = () => { if (w.SelectedTreeConstraint() is { } t) FlipConstraint(w, t.Part, t.Index); } });
        void M(string id, string label, string icon, MateCmd cmd, string syn) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = C, Icon = icon, Synonyms = syn, CanRun = AM, Run = () => Constrain(w, cmd) });
        M("mate.coincident", "Совпадение", "mate_coincident_but.svg", MateCmd.Coincident, "coincident mate совпадение прижать");
        M("mate.distance", "Расстояние", "mate_distance_but.svg", MateCmd.Distance, "distance offset расстояние");
        M("mate.angle", "Угол", "mate_angle_but.svg", MateCmd.Angle, "angle угол");
        M("mate.parallel", "Параллельно", "mate_parallel_but.svg", MateCmd.Parallel, "parallel параллельно");
        M("mate.perpendicular", "Перпендикулярно", "mate_perpendicular_but.svg", MateCmd.Perpendicular, "perpendicular перпендикулярно");
        M("mate.concentric", "Соосность", "mate_concentric_but.svg", MateCmd.Concentric, "concentric coaxial соосность");
        M("mate.tangent", "Касание", "mate_tangent_but.svg", MateCmd.Tangent, "tangent касание");
        M("mate.insert", "Вставка (болт в отверстие)", "mate_insert_but.svg", MateCmd.Insert, "insert bolt hole вставка болт");
        M("mate.point_on", "Точка на поверхности / прямой", "mate_point_on_but.svg", MateCmd.PointOn, "point on surface line точка на");
        M("mate.rigid", "Жёсткая связь", "mate_rigid_but.svg", MateCmd.Rigid, "rigid жёсткая связь");
        void Jn(string id, string label, string icon, MateType t, string syn) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = J, Icon = icon, Synonyms = syn, CanRun = AM, Run = () => Joint(w, t) });
        Jn("joint.fixed", "Соединение: жёсткое", "joint_fixed_but.svg", MateType.Weld, "fixed joint жёсткое соединение");
        Jn("joint.pin", "Соединение: шарнир", "joint_pin_but.svg", MateType.Pin, "pin revolute шарнир");
        Jn("joint.slider", "Соединение: ползун", "joint_slider_but.svg", MateType.Slider, "slider prismatic ползун");
        Jn("joint.cylindrical", "Соединение: цилиндрическое", "joint_cylindrical_but.svg", MateType.Cylinder, "cylindrical цилиндрическое");
        Jn("joint.bearing", "Соединение: шар в цилиндре", "joint_bearing_but.svg", MateType.BallCylinder, "ball cylinder bearing шар цилиндр");
        Jn("joint.planar", "Соединение: плоскостное", "joint_planar_but.svg", MateType.Planar, "planar плоскостное");
        Jn("joint.ball", "Соединение: шаровое", "joint_ball_but.svg", MateType.Ball, "ball spherical шаровое");
        Jn("joint.slot", "Соединение: паз", "joint_slot_but.svg", MateType.Slot, "slot паз");
        Jn("joint.weld", "Соединение: сварка", "joint_weld_but.svg", MateType.Weld, "weld сварка");
        Jn("joint.gimbal", "Соединение: шесть степеней", "joint_gimbal_but.svg", MateType.Free, "six dof free gimbal шесть степеней свобода");
    }
}
