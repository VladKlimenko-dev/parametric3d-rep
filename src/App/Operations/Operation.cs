using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

/// <summary>
/// ОПЕРАЦИЯ В ПАНЕЛИ (как в Plasticity и Creo): правая панель с полями и ссылками, предпросмотр сразу в модели
/// (операция добавлена в деталь, каждое изменение поля перестраивает деталь в фоне), «ОК» — один шаг отмены,
/// «Отмена» — деталь как была. Ссылки берутся из выбора вида: выбранное до команды раскладывается по полям, дальше
/// щелчки по модели идут в активное поле.
/// </summary>
public abstract class Operation
{
    protected MainWindow W = null!;
    protected Part Part = null!;
    public OpPanel? Panel { get; private set; }
    public RefField? ActiveRef { get; private set; }
    /// <summary>Идёт перенос ссылок поля в выбор вида — изменения выбора не возвращать в поле.</summary>
    public bool Syncing { get; private set; }

    /// <summary>Название (русское; на экране — L()).</summary>
    public abstract string Title { get; }
    public virtual string? Icon => null;
    /// <summary>Подпись кнопки подтверждения.</summary>
    public virtual string OkLabel => "ОК";

    public ParameterTable? Parameters => W.Session.Document.ScopeFor(Part);

    /// <summary>Начать: false — операция не нужна или уже сделала всё сама (например, эскиз начат сразу).</summary>
    public bool Begin(MainWindow w, Part part)
    {
        W = w;
        Part = part;
        return Start();
    }

    protected abstract bool Start();
    public abstract void Build(OpPanel p);
    /// <summary>После Build: разложить выбор по полям и показать предпросмотр.</summary>
    public virtual void Prime() { }

    /// <summary>Панель построена: выбрать первое незаполненное поле (или первое).</summary>
    public void AttachPanel(OpPanel p)
    {
        Panel = p;
        foreach (var r in p.Refs) if (r.Text != null) r.Text.Text = r.Summary;
        Activate(p.Refs.FirstOrDefault(r => !r.IsSet && !r.Optional) ?? p.Refs.FirstOrDefault());
        RefreshStatus();
    }

    /// <summary>Разложить выбор до команды по полям: каждое поле берёт то, что ему подходит.</summary>
    protected void Seed(params RefField[] fields)
    {
        var sel = W.ViewPort.Selection.Items.ToList();
        foreach (var f in fields)
        {
            var take = sel.Where(f.Accept).ToList();
            if (take.Count == 0) continue;
            f.Take(take);
            sel.RemoveAll(r => take.Any(t => t.Id == r.Id));
        }
    }

    public void Activate(RefField? f)
    {
        ActiveRef = f;
        if (Panel != null)
            foreach (var r in Panel.Refs)
                if (r.Box != null) r.Box.BorderBrush = ReferenceEquals(r, f) ? W.Res("P3.Accent") : W.Res("P3.Border");
        var sel = W.ViewPort.Selection;
        sel.Filter = f?.Filter ?? PickFilter.All;
        Syncing = true;
        try
        {
            sel.Clear();
            if (f != null) foreach (var r in f.Items) sel.Select(r, additive: true);
        }
        finally { Syncing = false; }
    }

    /// <summary>Выбор вида изменился (щелчок по модели или в дереве) — в активное поле.</summary>
    public void SelectionChanged(IReadOnlyList<PickRef> items)
    {
        if (Syncing || ActiveRef is not { } f) return;
        if (!f.Take(items)) return;
        RefChanged(f);
        // одиночное поле заполнено — дальше следующее пустое
        if (f.Single && f.Items.Count == 1 && Panel?.Refs.FirstOrDefault(r => !r.IsSet && !r.Optional) is { } next) Activate(next);
    }

    public void RefChanged(RefField f)
    {
        if (f.Text != null) f.Text.Text = f.Summary;
        FieldChanged();
    }

    /// <summary>Поле изменено — перенести в модель и перестроить предпросмотр.</summary>
    public void FieldChanged()
    {
        try { Apply(); }
        catch (Exception ex) { AppLog.Write("operation " + Title, ex); W.ShowToast(L(Title) + ": " + ex.Message); }
        RefreshStatus();
    }

    protected abstract void Apply();

    /// <summary>Строка состояния панели: чего не хватает или ошибка перестроения.</summary>
    public virtual void RefreshStatus()
    {
        if (Panel == null) return;
        string? missing = Panel.Refs.FirstOrDefault(r => !r.IsSet && !r.Optional) is { } m ? L("Выберите: {0}", L(m.Label).ToLowerInvariant()) : null;
        string? err = BuildError;
        Panel.Status.Text = missing ?? err ?? "";
        Panel.Status.Foreground = err != null && missing == null ? W.Res("P3.Error") : W.Res("P3.TextDim");
    }

    protected virtual string? BuildError => null;

    /// <summary>«ОК»: true — операция закончена.</summary>
    public abstract bool Commit();
    public abstract void Cancel();
}

/// <summary>Операция дерева построения: Feature добавляется в деталь сразу и правится полями (живой предпросмотр).</summary>
public abstract class FeatureOp<T> : Operation where T : Feature
{
    protected T F = null!;
    private readonly T? _existing;
    protected bool Editing => _existing != null;

    protected FeatureOp(T? existing = null) { _existing = existing; }

    protected abstract T Create();
    /// <summary>Разложить выбор по полям (новая операция).</summary>
    protected abstract void SeedRefs();
    /// <summary>Перенести поля в операцию.</summary>
    protected abstract void Push();

    protected override bool Start()
    {
        W.Session.BeginEdit(Editing ? L("Изменить «{0}»", L(Title)) : L(Title));
        if (_existing != null) F = _existing;
        else
        {
            F = Create();
            F.Name = L(Title);
        }
        return true;
    }

    /// <summary>Вызывается после Build: ссылки из выбора, затем операция — в деталь.</summary>
    public override void Prime()
    {
        if (!Editing) SeedRefs();
        if (Panel != null) foreach (var r in Panel.Refs) if (r.Text != null) r.Text.Text = r.Summary;
        Push();
        if (!Editing) Part.AddFeature(F);
        else Part.NotifyChanged(F);
        RefreshStatus();
    }

    protected override void Apply()
    {
        Push();
        Part.NotifyChanged(F);
    }

    protected override string? BuildError => F?.Error;

    public override bool Commit()
    {
        if (Panel != null && Panel.Refs.FirstOrDefault(r => !r.IsSet && !r.Optional) is { } m)
        {
            W.ShowToast(L("Выберите: {0}", L(m.Label).ToLowerInvariant()));
            return false;
        }
        W.Session.CommitEdit();
        return true;
    }

    public override void Cancel() => W.Session.CancelEdit();
}

/// <summary>Чтение ссылок: плоскость, прямая, точка, эскиз — из того, что выбрано в виде или дереве.</summary>
public static class Refs
{
    public static Sketch? SketchOf(PickRef r) => r.Target as Sketch ?? r.Owner as Sketch;

    public static bool IsSketch(PickRef r) => SketchOf(r) != null;

    public static bool IsPlanar(PickRef r)
    {
        if (r.Kind == PickKind.DatumPlane) return true;
        if (r.Kind != PickKind.Face || r.Owner is not Part p) return false;
        return FacePlane(p, r.Id) != null;
    }

    public static bool IsLinear(PickRef r) => r.Kind == PickKind.DatumAxis
        || r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id) is { } e && (e.Curve?.GetType().Name.Contains("Line") ?? false)
        || r.Target is SkLine;

    /// <summary>Плоскость ссылки: опорная — её система; плоская грань — с началом в точке грани.</summary>
    public static (Frame3 Frame, uint SourceId, string Name)? PlaneOf(PickRef r)
    {
        if (r.Target is DatumPlane dp) return (dp.Frame, dp.Id, dp.Name);
        if (r.Kind == PickKind.Face && r.Owner is Part p && FacePlane(p, r.Id) is { } pn)
            return (Frame3.FromNormal(pn.P, pn.N), r.Id, L("Грань #{0}", r.Id));
        return null;
    }

    /// <summary>
    /// Плоскость грани (точка внутри грани, внешняя нормаль): плоская поверхность — по ядру; иначе — по сетке, если все
    /// нормали грани совпадают (боковые грани выдавливания — «поверхность выдавливания» отрезка, но плоские).
    /// </summary>
    public static (Vector3d P, Vector3d N)? FacePlane(Part p, uint faceId)
    {
        var c = FacePoint(p, faceId);
        if (c == null) return null;
        if (p.FindFace(faceId) is { } f && SketchFaceRef.PlaneOf(f) is { } pn) return (c.Value, pn.N.Normalized());
        var md = p.Mesh;
        if (md == null) return null;
        Vector3? n0 = null;
        foreach (var v in md.Vertices)
        {
            if (v.FaceId != faceId) continue;
            var n = v.N.Normalized();
            if (n0 == null) n0 = n;
            else if (Vector3.Dot(n, n0.Value) < 0.99999f) return null;
        }
        if (n0 is not { } nn) return null;
        // плоскость — и по точкам: отклонение вершин от плоскости через центр
        foreach (var v in md.Vertices)
            if (v.FaceId == faceId && Math.Abs(Vector3.Dot(v.P - new Vector3((float)c.Value.X, (float)c.Value.Y, (float)c.Value.Z), nn)) > 1e-3f) return null;
        return (c.Value, new Vector3d(nn.X, nn.Y, nn.Z).Normalized());
    }

    /// <summary>Прямая ссылки: точка и направление (единичное).</summary>
    public static (Vector3d At, Vector3d Dir)? LineOf(PickRef r)
    {
        switch (r.Target)
        {
            case DatumAxis ax: return (ax.Origin, ax.Direction.Normalized());
            case Line3DItem ln: return (ln.A, (ln.B - ln.A).Normalized());
            case SkLine sl when r.Owner is Sketch sk:
                {
                    var a = sk.Plane.ToWorld(sl.A.P); var b = sk.Plane.ToWorld(sl.B.P);
                    return (a, (b - a).Normalized());
                }
        }
        if (r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id) is { } e && e.V0 != null && e.V1 != null)
        {
            var d = e.V1.P - e.V0.P;
            if (d.LengthSquared > 1e-12) return (e.V0.P, d.Normalized());
        }
        return null;
    }

    /// <summary>Точка ссылки: вершина, точка эскиза, опорная точка, середина грани.</summary>
    public static Vector3d? PointOf(PickRef r)
    {
        switch (r.Target)
        {
            case DatumPoint pt: return pt.P;
            case SkPoint sp when r.Owner is Sketch sk: return sk.Plane.ToWorld(sp.P);
        }
        if (r.Owner is Part p)
        {
            if (r.Kind == PickKind.Vertex && p.FindVertex(r.Id) is { } v) return v.P;
            if (r.Kind == PickKind.Face) return FacePoint(p, r.Id);
        }
        return null;
    }

    /// <summary>Точка внутри грани: центр самого большого её треугольника сетки.</summary>
    public static Vector3d? FacePoint(Part p, uint faceId)
    {
        var md = p.Mesh;
        if (md == null) return null;
        double best = -1; Vector3d res = default;
        var v = md.Vertices; var ix = md.Indices;
        for (int i = 0; i + 2 < ix.Length; i += 3)
        {
            var a = v[ix[i]]; var b = v[ix[i + 1]]; var c = v[ix[i + 2]];
            if (a.FaceId != faceId) continue;
            var ab = b.P - a.P; var ac = c.P - a.P;
            double area = Vector3.Cross(ab, ac).LengthSquared;
            if (area > best)
            {
                best = area;
                var m = (a.P + b.P + c.P) / 3f;
                res = new Vector3d(m.X, m.Y, m.Z);
            }
        }
        return best >= 0 ? res : null;
    }
}
