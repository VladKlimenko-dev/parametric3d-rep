using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Params;

namespace Parametric3D.Model.Features;

// u13h: FREEFORM-ТЕЛО (по образцу Plasticity, FREEFORM_STAGE.md). Узел FreeForm в дереве детали (FreeFormFeature) и его шаги —
// операции с ключом узла в поле Feature.FreeForm. Шаг правит одно тело FreeForm по имени (FFBody) или создаёт новое тело;
// тела FreeForm — раздельные тела детали с именами («Тело 1», «Тело 2»…), булевы между ними — отдельные шаги. Шаг фиксируется
// при завершении команды (Frozen): параметры больше не правятся, вернуть — «отменить до этого шага». При встраивании в
// параметрическую деталь (конец узла) пересекающиеся тела объединяются автоматически, исходные шаги остаются как были.

/// <summary>Шаг FreeForm, который сам работает со всеми телами по именам (заготовки, булевы, перенос, массивы…).</summary>
public interface IFreeFormNative { }

public static class FreeFormExec
{
    /// <summary>Выполнить шаг FreeForm: обычная операция дерева — на своём теле (или с пустым списком — новое тело).</summary>
    public static void Run(Feature f, BuildContext ctx)
    {
        if (f is IFreeFormNative) { f.Execute(ctx); return; }
        var targets = new List<TSolid>();
        if (f.FFBody != null)
            targets.Add(ctx.BodyByName(f.FFBody) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{f.Name}: FreeForm body «{f.FFBody}» is not found."));
        else if (f.Cut) targets.AddRange(ctx.Bodies);
        var sub = Fork(ctx, targets);
        f.Execute(sub);
        Join(ctx, sub, targets);
    }

    /// <summary>Контекст шага: те же деталь, параметры и опорные элементы, тела — только названные.</summary>
    public static BuildContext Fork(BuildContext ctx, List<TSolid> bodies)
    {
        var s = new BuildContext { Part = ctx.Part, Parameters = ctx.Parameters, Solver = ctx.Solver };
        s.Bodies.AddRange(bodies);
        foreach (var kv in ctx.Planes) s.Planes[kv.Key] = kv.Value;
        foreach (var kv in ctx.Axes) s.Axes[kv.Key] = kv.Value;
        foreach (var kv in ctx.Points) s.Points[kv.Key] = kv.Value;
        s.Quilts.AddRange(ctx.Quilts);
        foreach (var b in bodies) s.BodyNames[b] = ctx.NameOf(b);
        return s;
    }

    /// <summary>Вернуть результат шага: тела — на место правленых (с их именами), новые — с новыми именами.</summary>
    public static void Join(BuildContext ctx, BuildContext sub, List<TSolid> targets)
    {
        var names = targets.Select(ctx.NameOf).ToList();
        // тело изменено обычной операцией — оно больше не SubD (клетка не описывает его форму)
        foreach (var n in names) ctx.Cages.Remove(n);
        int at = ctx.Bodies.Count;
        foreach (var t in targets) { int i = ctx.Bodies.IndexOf(t); if (i >= 0) at = Math.Min(at, i); }
        foreach (var t in targets) ctx.Bodies.Remove(t);
        at = Math.Min(at, ctx.Bodies.Count);
        bool sameCount = targets.Count > 1 && sub.Bodies.Count == targets.Count;
        int fresh = 0;
        for (int i = 0; i < sub.Bodies.Count; i++)
        {
            var b = sub.Bodies[i];
            string name;
            if (sub.BodyNames.TryGetValue(b, out var n)) name = n;
            else if (sameCount) name = names[i];
            else if (targets.Count == 1) name = fresh == 0 ? names[0] : $"{names[0]}.{fresh + 1}";
            else name = "";
            if (!sub.BodyNames.ContainsKey(b)) fresh++;
            if (name.Length == 0) name = ctx.NewBodyName();      // до вставки: безымянное тело иначе само заняло бы «Тело N»
            ctx.Bodies.Insert(at++, b);
            ctx.BodyNames[b] = name;
        }
        ctx.Warnings.AddRange(sub.Warnings);
        ctx.Quilts.Clear(); ctx.Quilts.AddRange(sub.Quilts);
        foreach (var kv in sub.Planes) ctx.Planes[kv.Key] = kv.Value;
        foreach (var kv in sub.Axes) ctx.Axes[kv.Key] = kv.Value;
        foreach (var kv in sub.Points) ctx.Points[kv.Key] = kv.Value;
    }

    /// <summary>
    /// ВСТРАИВАНИЕ (решение 2026-10-13): все пересекающиеся тела FreeForm объединяются в одно — вычисляемый результат, исходные
    /// шаги не меняются. Не пересекающиеся остаются раздельными телами.
    /// </summary>
    public static void AutoUnion(BuildContext ctx)
    {
        if (ctx.Bodies.Count < 2) return;
        var all = ctx.Bodies.ToList();
        var first = all.Select(ctx.NameOf).ToList();
        ctx.Cages.Clear();
        ctx.Bodies.Clear();
        foreach (var b in all) BooleanOp.Unite(ctx.Bodies, b, ctx.Warnings);
        if (ctx.Bodies.Count < all.Count) ctx.Warnings.Add($"FreeForm: {all.Count} bodies merged into {ctx.Bodies.Count} (intersecting bodies are united on embedding).");
        for (int i = 0; i < ctx.Bodies.Count; i++)
            if (!ctx.BodyNames.ContainsKey(ctx.Bodies[i])) ctx.BodyNames[ctx.Bodies[i]] = i < first.Count ? first[i] : "Тело " + (i + 1);
    }

    /// <summary>Подпись формы тел (не зависит от номеров граней): число граней и вершины, округлённые до 1e-5 мм.</summary>
    public static string Signature(IEnumerable<TSolid> bodies)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in bodies)
        {
            sb.Append(b.Faces.Count()).Append('/').Append(b.Edges.Count()).Append(':');
            foreach (var p in b.Vertices.Select(v => (Math.Round(v.P.X, 5), Math.Round(v.P.Y, 5), Math.Round(v.P.Z, 5))).OrderBy(t => t))
                sb.Append(p.Item1).Append(',').Append(p.Item2).Append(',').Append(p.Item3).Append(';');
            sb.Append('|');
        }
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }

    public static TSolid Body(BuildContext ctx, string name, string what) =>
        ctx.BodyByName(name) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{what}: FreeForm body «{name}» is not found.");

    /// <summary>Переместить тело по отображению: точно (граф B-rep), иначе — пересборкой.</summary>
    public static TSolid Map(TSolid b, TransformOp.Xform x) => TopologyTransform.Apply(b, x) ?? TransformOp.Copy(b, x);

    /// <summary>
    /// 13:09: ЛОКАЛЬНАЯ РАМКА ТЕЛА (переключатель Local/Global гизмо): преобладающие направления тела — оси цилиндров и
    /// вращения (с весом их длины), прямые рёбра (по длине), нормали плоских граней; первое направление — самое весомое,
    /// второе — самое весомое поперёк него (|cos| &lt; 0.1). Оси переставляются и обращаются, чтобы стоять ближе всего к
    /// осям детали (повёрнутый брусок: X — ребро, ближайшее к X детали). Ни одного направления (шар) — оси детали.
    /// </summary>
    public static (Vector3d X, Vector3d Y, Vector3d Z) LocalFrame(TSolid body)
    {
        var dirs = new List<(Vector3d D, double W)>();
        void Add(Vector3d d, double w)
        {
            if (!(w > 0) || d.LengthSquared < 1e-18) return;
            d = d.Normalized();
            for (int i = 0; i < dirs.Count; i++)
                if (Math.Abs(Vector3d.Dot(dirs[i].D, d)) > 0.9995) { dirs[i] = (dirs[i].D, dirs[i].W + w); return; }
            dirs.Add((d, w));
        }
        var bb = Bounds3.Empty;
        foreach (var v in body.Vertices) bb.Add(v.P);
        double size = bb.IsEmpty ? 1 : Math.Max(1e-6, (bb.Max - bb.Min).Length);
        foreach (var f in body.Faces)
            switch (f.Surface)
            {
                case Parametric3D.Kernel.Surfaces.CylinderSurface c: Add(c.Axis.W, 2 * size); break;
                case Parametric3D.Kernel.Surfaces.RevolvedSurface r: Add(r.Axis.W, 2 * size); break;
                case Parametric3D.Kernel.Surfaces.PlaneSurface pl: Add(pl.F.W, 0.25 * size); break;
            }
        foreach (var e in body.Edges)
            if (e.Curve is LineCurve3 ln) Add(ln.B - ln.A, (ln.B - ln.A).Length);
        var ux = Vector3d.UnitX; var uy = Vector3d.UnitY; var uz = Vector3d.UnitZ;
        if (dirs.Count == 0) return (ux, uy, uz);
        dirs.Sort((a, b) => b.W.CompareTo(a.W));
        var a1 = dirs[0].D;
        Vector3d? a2 = null;
        foreach (var (d, _) in dirs.Skip(1)) if (Math.Abs(Vector3d.Dot(d, a1)) < 0.1) { a2 = d; break; }
        if (a2 == null)
        {
            // одно направление (цилиндр): второе — ось детали, дальше всего от первого
            var g = new[] { ux, uy, uz }.OrderBy(v => Math.Abs(Vector3d.Dot(v, a1))).First();
            a2 = g;
        }
        var b2 = (a2.Value - a1 * Vector3d.Dot(a2.Value, a1)).Normalized();
        var b3 = Vector3d.Cross(a1, b2).Normalized();
        // перестановка и знаки: ближе всего к осям детали, правая тройка
        var axes = new[] { a1, b2, b3 };
        Vector3d best0 = ux, best1 = uy; double bestScore = double.NegativeInfinity;
        foreach (var perm in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } })
        {
            var x = axes[perm[0]]; var y = axes[perm[1]];
            if (Vector3d.Dot(x, ux) < 0) x = -x;
            if (Vector3d.Dot(y, uy) < 0) y = -y;
            var z = Vector3d.Cross(x, y);
            double score = Vector3d.Dot(x, ux) + Vector3d.Dot(y, uy) + Vector3d.Dot(z, uz);
            if (score > bestScore + 1e-12) { bestScore = score; best0 = x; best1 = y; }
        }
        return (best0, best1, Vector3d.Cross(best0, best1).Normalized());
    }

    /// <summary>13:09: габарит тел в рамке (X, Y, Z — единичные, взаимно перпендикулярные): min и max координат по осям, по сетке тел.</summary>
    public static (Vector3d Min, Vector3d Max)? BoundsIn(IEnumerable<TSolid> bodies, Vector3d x, Vector3d y, Vector3d z)
    {
        var mn = new Vector3d(double.MaxValue); var mx = new Vector3d(double.MinValue);
        bool any = false;
        foreach (var b in bodies)
        {
            IEnumerable<Vector3d> pts;
            try { pts = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b).Vertices.Select(v => new Vector3d(v.P.X, v.P.Y, v.P.Z)); }
            catch (Exception) { pts = b.Vertices.Select(v => v.P); }
            foreach (var p in pts.Concat(b.Vertices.Select(v => v.P)))
            {
                var q = new Vector3d(Vector3d.Dot(p, x), Vector3d.Dot(p, y), Vector3d.Dot(p, z));
                mn = Vector3d.ComponentMin(mn, q); mx = Vector3d.ComponentMax(mx, q);
                any = true;
            }
        }
        return any ? (mn, mx) : null;
    }

    public static double Size(IEnumerable<TSolid> bodies)
    {
        var bb = Bounds3.Empty;
        foreach (var b in bodies) foreach (var v in b.Vertices) bb.Add(v.P);
        return bb.IsEmpty ? 100 : Math.Max(1, (bb.Max - bb.Min).Length);
    }

    /// <summary>Разрез тела плоскостью: (часть по нормали, часть против нормали); пустая часть — null.</summary>
    public static (List<TSolid> Plus, List<TSolid> Minus) Split(TSolid b, Vector3d at, Vector3d n, List<string> warnings)
    {
        double size = 4 * Size(new[] { b }) + (at.Length * 2);
        var plus = new List<TSolid> { b }; BooleanOp.Intersect(plus, QuiltUtil.HalfSpace(at, n, size), warnings);
        var minus = new List<TSolid> { b }; BooleanOp.Subtract(minus, QuiltUtil.HalfSpace(at, n, size), warnings);
        return (plus, minus);
    }
}

/// <summary>
/// УЗЕЛ FREEFORM в дереве детали. Без входа (отдельный документ FreeForm) — тела строят его шаги. «Править в FreeForm» —
/// вход: тела детали до узла; их подпись и копия (STEP) запоминаются. Изменилось тело выше в дереве — завершённые шаги не
/// перестраиваются по новой форме (решение 2026-10-14): узел помечается «ссылки изменились», шаги идут по запомненной копии;
/// «Обновить ссылки» берёт новую форму.
/// </summary>
public sealed class FreeFormFeature : Feature, IFreeFormNative
{
    public string InputStep = "";
    public string InputSig = "";
    /// <summary>Последний расчёт: вход отличается от запомненного.</summary>
    public bool RefsChanged;
    private List<TSolid>? _cache;
    private string? _cacheText;

    public FreeFormFeature(string name = "FreeForm") { Name = name; FreeForm = Key; }
    public override string Summary => InputSig.Length == 0 ? "FreeForm body" : RefsChanged ? "FreeForm (references changed)" : "FreeForm (edit of the part body)";

    /// <summary>Запомнить вход: тела детали перед узлом.</summary>
    public void Capture(IReadOnlyList<TSolid> bodies)
    {
        InputSig = bodies.Count == 0 ? "" : FreeFormExec.Signature(bodies);
        if (bodies.Count == 0) { InputStep = ""; return; }
        var tp = new Part("FreeForm input");
        tp.Bodies.AddRange(bodies);
        try { InputStep = Exchange.Write(tp, Exchange.Format.Step, new List<string>()); }
        catch { InputStep = ""; }
        _cache = null;
    }

    public override void Execute(BuildContext ctx)
    {
        RefsChanged = false;
        if (InputSig.Length > 0 && FreeFormExec.Signature(ctx.Bodies) != InputSig)
        {
            RefsChanged = true;
            ctx.Warnings.Add($"{Name}: references changed — the body before the FreeForm node is different; the FreeForm steps use the stored input (Update references takes the new body).");
            if (InputStep.Length > 0)
            {
                if (_cache == null || !ReferenceEquals(_cacheText, InputStep))
                {
                    var p = Exchange.Read(InputStep, out _, ctx.Warnings);
                    _cache = p?.Bodies.ToList() ?? new List<TSolid>(); _cacheText = InputStep;
                }
                ctx.Bodies.Clear(); ctx.Bodies.AddRange(_cache);
            }
        }
        int i = 0;
        foreach (var b in ctx.Bodies)
            if (!ctx.BodyNames.ContainsKey(b)) ctx.BodyNames[b] = ctx.Bodies.Count == 1 ? "Исходное тело" : $"Исходное тело {++i}";
    }
}

public enum FFPrimitive { Box = 0, Cylinder = 1, Cone = 2, Sphere = 3, Torus = 4 }

/// <summary>ЗАГОТОВКА FreeForm на плоскости построения: брусок, цилиндр, конус, шар, тор — новое тело (вырез — из всех тел).</summary>
public sealed class FFPrimitiveFeature : Feature, IFreeFormNative
{
    public FFPrimitive Shape;
    public Vector3d Origin;
    public Vector3d U = Vector3d.UnitX, V = Vector3d.UnitY;
    /// <summary>Брусок: ширина, глубина, высота; цилиндр: R, H; конус: R1, R2, H; шар: R; тор: R кольца, R трубы.</summary>
    public ValueRef A = new(40), B = new(30), C = new(20);
    public FFPrimitiveFeature(string name = "Primitive") { Name = name; }
    public Frame3 Frame => new(Origin, U, V);
    public override string Summary => Shape switch
    {
        FFPrimitive.Box => $"Box {A}×{B}×{C}",
        FFPrimitive.Cylinder => $"Cylinder R{A} H{B}",
        FFPrimitive.Cone => $"Cone R{A}/R{B} H{C}",
        FFPrimitive.Sphere => $"Sphere R{A}",
        _ => $"Torus R{A} r{B}",
    };

    public static TSolid Make(FFPrimitive shape, Frame3 fr, double a, double b, double c)
    {
        void Pos(double v, string what) { if (!(v > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, $"{shape}: {what} must be greater than zero."); }
        if (shape == FFPrimitive.Box)
        {
            Pos(a, "the width"); Pos(b, "the depth"); Pos(c, "the height");
            double x = a / 2, y = b / 2;
            var rect = new List<ICurve3>
            {
                new LineCurve3(fr.ToWorld(-x, -y), fr.ToWorld(x, -y)), new LineCurve3(fr.ToWorld(x, -y), fr.ToWorld(x, y)),
                new LineCurve3(fr.ToWorld(x, y), fr.ToWorld(-x, y)), new LineCurve3(fr.ToWorld(-x, y), fr.ToWorld(-x, -y)),
            };
            return ExtrudeOp.Build(ProfileBuilder.Build(fr, rect), fr.W * c);
        }
        TSolid local = shape switch
        {
            FFPrimitive.Cylinder => PrimitiveOp.Cylinder(Vector3d.Zero, a, b),
            FFPrimitive.Cone => PrimitiveOp.Cone(Vector3d.Zero, a, b, c),
            FFPrimitive.Sphere => PrimitiveOp.Sphere(Vector3d.Zero, a),
            _ => PrimitiveOp.Torus(Vector3d.Zero, a, b),
        };
        var x2 = new TransformOp.Xform(p => fr.ToWorld(p), d => fr.U * d.X + fr.V * d.Y + fr.W * d.Z, false);
        return FreeFormExec.Map(local, x2);
    }

    public override void Execute(BuildContext ctx)
    {
        var prm = ctx.Parameters;
        var solid = Make(Shape, Frame, A.Resolve(prm), B.Resolve(prm), C.Resolve(prm));
        if (Cut)
        {
            if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: nothing to cut (no FreeForm body).");
            var names = ctx.Bodies.Select(ctx.NameOf).ToList();
            BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
            for (int i = 0; i < ctx.Bodies.Count && i < names.Count; i++) if (!ctx.BodyNames.ContainsKey(ctx.Bodies[i])) ctx.BodyNames[ctx.Bodies[i]] = names[i];
            return;
        }
        ctx.BodyNames[solid] = ctx.NewBodyName();
        ctx.Bodies.Add(solid);
    }
}

public enum FFBool { Union = 0, Subtract = 1, Intersect = 2 }

/// <summary>БУЛЕВА МЕЖДУ ТЕЛАМИ FreeForm (только в FreeForm, решение 2026-10-13): целевое тело и тела-инструменты по именам.</summary>
public sealed class FFBooleanFeature : Feature, IFreeFormNative
{
    public FFBool Op;
    public string Target = "";
    public readonly List<string> Tools = new();
    public bool KeepTools;
    public FFBooleanFeature(string name = "Boolean") { Name = name; }
    public override string Summary => $"{Op} «{Target}» with {string.Join(", ", Tools.Select(t => "«" + t + "»"))}";

    public override void Execute(BuildContext ctx)
    {
        if (Tools.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no tool bodies.");
        var target = FreeFormExec.Body(ctx, Target, Name);
        var tools = Tools.Select(t => FreeFormExec.Body(ctx, t, Name)).ToList();
        if (tools.Any(t => ReferenceEquals(t, target))) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the target body is also a tool.");
        var list = new List<TSolid> { target };
        foreach (var t in tools)
            switch (Op)
            {
                case FFBool.Subtract: BooleanOp.Subtract(list, t, ctx.Warnings); break;
                case FFBool.Intersect: BooleanOp.Intersect(list, t, ctx.Warnings); break;
                default: BooleanOp.Unite(list, t, ctx.Warnings); break;
            }
        if (list.Count == 0 && Op != FFBool.Subtract) throw new RebuildException(OperationOutcome.Empty, $"{Name}: the bodies do not intersect — the result is empty.");
        int at = ctx.Bodies.IndexOf(target);
        string name = ctx.NameOf(target);
        ctx.Cages.Remove(name);
        ctx.Bodies.Remove(target);
        if (!KeepTools) foreach (var t in tools) ctx.Bodies.Remove(t);
        at = Math.Clamp(at, 0, ctx.Bodies.Count);
        for (int i = 0; i < list.Count; i++)
        {
            ctx.Bodies.Insert(at + i, list[i]);
            ctx.BodyNames[list[i]] = i == 0 ? name : $"{name}.{i + 1}";
        }
    }
}

public enum FFXform { Move = 0, Rotate = 1, Scale = 2 }

/// <summary>ПЕРЕНОС, ПОВОРОТ, МАСШТАБ тел FreeForm (манипулятор пишет сюда числа); копия — новое тело.</summary>
public sealed class FFTransformFeature : Feature, IFreeFormNative
{
    public FFXform Mode;
    public readonly List<string> BodyNames = new();
    public ValueRef Dx = new(0), Dy = new(0), Dz = new(0), Angle = new(0), Factor = new(1);
    /// <summary>
    /// 13:09 (гизмо): масштаб по осям рамки (умножается на Factor) и РАМКА операции — направления осей X, Y (Z = X × Y):
    /// глобальная (по умолчанию — оси детали) или локальная (рамка тела). Сдвиг Dx, Dy, Dz — по осям этой рамки.
    /// </summary>
    public ValueRef Sx = new(1), Sy = new(1), Sz = new(1);
    public Vector3d FrameX = Vector3d.UnitX, FrameY = Vector3d.UnitY;
    public Vector3d FrameZ => Vector3d.Cross(FrameX, FrameY).Normalized();
    public Vector3d AxisAt;
    public Vector3d AxisDir = Vector3d.UnitZ;
    /// <summary>Сдвиг в мировых координатах (Dx, Dy, Dz — по осям рамки).</summary>
    public Vector3d Shift(Parametric3D.Model.Params.ParameterTable? prm) => FrameX.Normalized() * Dx.Resolve(prm) + FrameY.Normalized() * Dy.Resolve(prm) + FrameZ * Dz.Resolve(prm);
    public bool Copy;
    public FFTransformFeature(string name = "Transform") { Name = name; }
    public override string Summary => Mode switch
    {
        FFXform.Rotate => $"Rotate {Angle}° ({BodyNames.Count} bodies){(Copy ? ", copy" : "")}",
        FFXform.Scale => (Sx.Value != 1 || Sy.Value != 1 || Sz.Value != 1 ? $"Scale ×{Factor} ({Sx}, {Sy}, {Sz})" : $"Scale ×{Factor}") + $" ({BodyNames.Count} bodies){(Copy ? ", copy" : "")}",
        _ => $"Move ({Dx}, {Dy}, {Dz}) ({BodyNames.Count} bodies){(Copy ? ", copy" : "")}",
    };

    // 13:09 (гизмо): операция открывается с нулевым сдвигом / углом 0 / масштабом 1 и тянется гизмо — тождественное
    // преобразование без копии оставляет тело как есть, без ошибки (прежде — отказ «сдвиг нулевой»). Копия тождеством —
    // по-прежнему отказ (тело поверх тела). Мутация P3D_XFORMIDENTITY=0.
    public static readonly bool IdentityPass = Environment.GetEnvironmentVariable("P3D_XFORMIDENTITY") != "0";

    /// <summary>Преобразование тождественное (при текущих значениях).</summary>
    public bool IsIdentity(Parametric3D.Model.Params.ParameterTable? prm)
    {
        switch (Mode)
        {
            case FFXform.Scale:
            {
                double k = Factor.Resolve(prm);
                return Math.Abs(k * Sx.Resolve(prm) - 1) < 1e-12 && Math.Abs(k * Sy.Resolve(prm) - 1) < 1e-12 && Math.Abs(k * Sz.Resolve(prm) - 1) < 1e-12;
            }
            case FFXform.Rotate: return Math.Abs(Angle.Resolve(prm)) < 1e-9;
            default: return Shift(prm).Length < Tol.Weld;
        }
    }

    public override void Execute(BuildContext ctx)
    {
        if (BodyNames.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no bodies selected.");
        var prm = ctx.Parameters;
        if (IdentityPass && !Copy && IsIdentity(prm))
        {
            foreach (var n in BodyNames) FreeFormExec.Body(ctx, n, Name);
            return;
        }
        foreach (var n in BodyNames)
        {
            var b = FreeFormExec.Body(ctx, n, Name);
            TSolid moved;
            switch (Mode)
            {
                case FFXform.Scale:
                {
                    double k = Factor.Resolve(prm);
                    double sx = k * Sx.Resolve(prm), sy = k * Sy.Resolve(prm), sz = k * Sz.Resolve(prm);
                    if (!(sx > 1e-6 && sy > 1e-6 && sz > 1e-6)) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the scale factor must be positive.");
                    if (Math.Abs(sx - 1) < 1e-12 && Math.Abs(sy - 1) < 1e-12 && Math.Abs(sz - 1) < 1e-12) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the scale factor is 1.");
                    if (Math.Abs(sx - sy) < 1e-15 && Math.Abs(sy - sz) < 1e-15)
                        moved = TopologyTransform.Scaled(b, AxisAt, sx) ?? throw new RebuildException(OperationOutcome.Unsupported, $"{Name}: the body «{n}» cannot be scaled exactly (procedural surfaces).");
                    else
                    {
                        // по осям: точно, где форма это допускает; иначе — перенос по треугольникам (тело гранёное)
                        var X = FrameX.Normalized(); var Z = FrameZ; var Y = Vector3d.Cross(Z, X);
                        Vector3d Lm(Vector3d d) => X * (sx * Vector3d.Dot(X, d)) + Y * (sy * Vector3d.Dot(Y, d)) + Z * (sz * Vector3d.Dot(Z, d));
                        moved = TopologyTransform.ScaledAxes(b, AxisAt, X, Y, Z, sx, sy, sz)
                                ?? TransformOp.Copy(b, new TransformOp.Xform(p => AxisAt + Lm(p - AxisAt), Lm, false));
                    }
                    break;
                }
                case FFXform.Rotate:
                {
                    double deg = Angle.Resolve(prm);
                    if (Math.Abs(deg) < 1e-9) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the angle is zero.");
                    if (AxisDir.LengthSquared < 1e-18) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no axis.");
                    moved = FreeFormExec.Map(b, TransformOp.Rotate(AxisAt, AxisDir.Normalized(), deg));
                    break;
                }
                default:
                {
                    var by = Shift(prm);
                    if (by.Length < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the shift is zero.");
                    moved = FreeFormExec.Map(b, TransformOp.Move(by));
                    break;
                }
            }
            // SubD-клетка переносится тем же отображением (подразделение аффинно-инвариантно)
            SubDCage? cage = null;
            if (ctx.Cages.TryGetValue(n, out var c0))
            {
                var prm2 = ctx.Parameters;
                Func<Vector3d, Vector3d> map = Mode switch
                {
                    FFXform.Scale => p =>
                    {
                        var X = FrameX.Normalized(); var Z = FrameZ; var Y = Vector3d.Cross(Z, X); double k2 = Factor.Resolve(prm2); var d = p - AxisAt;
                        return AxisAt + X * (k2 * Sx.Resolve(prm2) * Vector3d.Dot(X, d)) + Y * (k2 * Sy.Resolve(prm2) * Vector3d.Dot(Y, d)) + Z * (k2 * Sz.Resolve(prm2) * Vector3d.Dot(Z, d));
                    },
                    FFXform.Rotate => TransformOp.Rotate(AxisAt, AxisDir.Normalized(), Angle.Resolve(prm2)).Point,
                    _ => TransformOp.Move(Shift(prm2)).Point,
                };
                cage = c0.Mapped(map, false);
            }
            if (Copy) { var nn = ctx.NewBodyName(); ctx.BodyNames[moved] = nn; ctx.Bodies.Add(moved); if (cage != null) ctx.Cages[nn] = cage; }
            else { ctx.Bodies[ctx.Bodies.IndexOf(b)] = moved; ctx.BodyNames[moved] = n; if (cage != null) ctx.Cages[n] = cage; }
        }
    }
}

/// <summary>РАЗРЕЗ тела плоскостью (плоскостью построения, опорной плоскостью или плоской гранью): два тела.</summary>
public sealed class FFSplitFeature : Feature, IFreeFormNative
{
    public string Body = "";
    public Vector3d PlaneAt;
    public Vector3d Normal = Vector3d.UnitZ;
    public FFSplitFeature(string name = "Split") { Name = name; }
    public override string Summary => $"Split «{Body}» by a plane";

    public override void Execute(BuildContext ctx)
    {
        var b = FreeFormExec.Body(ctx, Body, Name);
        if (Normal.LengthSquared < 1e-18) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no plane.");
        var (plus, minus) = FreeFormExec.Split(b, PlaneAt, Normal, ctx.Warnings);
        if (plus.Count == 0 || minus.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the plane does not cross the body «{Body}».");
        int at = ctx.Bodies.IndexOf(b);
        ctx.Bodies.RemoveAt(at);
        ctx.Cages.Remove(Body);
        var parts = plus.Concat(minus).ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            ctx.Bodies.Insert(at + i, parts[i]);
            ctx.BodyNames[parts[i]] = i == 0 ? Body : $"{Body}.{i + 1}";
        }
    }
}

public enum FFMirrorMode { Copy = 0, Symmetry = 1 }

/// <summary>
/// ЗЕРКАЛО тел FreeForm: копия — новое отражённое тело; СИММЕТРИЯ (живая половина) — тело режется плоскостью, остаётся
/// половина по нормали, и к ней пристраивается её отражение: правка половины даёт симметричное тело.
/// </summary>
public sealed class FFMirrorFeature : Feature, IFreeFormNative
{
    public FFMirrorMode Mode;
    public readonly List<string> BodyNames = new();
    public Vector3d PlaneAt;
    public Vector3d Normal = Vector3d.UnitX;
    public FFMirrorFeature(string name = "Mirror") { Name = name; }
    public override string Summary => Mode == FFMirrorMode.Symmetry ? $"Symmetry ({BodyNames.Count} bodies)" : $"Mirror copy ({BodyNames.Count} bodies)";

    public override void Execute(BuildContext ctx)
    {
        if (BodyNames.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no bodies selected.");
        if (Normal.LengthSquared < 1e-18) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no plane.");
        var x = TransformOp.Mirror(PlaneAt, Normal);
        foreach (var n in BodyNames)
        {
            var b = FreeFormExec.Body(ctx, n, Name);
            if (Mode == FFMirrorMode.Copy)
            {
                var m = FreeFormExec.Map(b, x);
                var mn = ctx.NewBodyName(); ctx.BodyNames[m] = mn; ctx.Bodies.Add(m);
                if (ctx.Cages.TryGetValue(n, out var mc)) ctx.Cages[mn] = mc.Mapped(x.Point, true);
                continue;
            }
            // половина по нормали: тело, целиком лежащее с одной стороны, не режется
            var (plus, minus) = FreeFormExec.Split(b, PlaneAt, Normal, ctx.Warnings);
            if (plus.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the body «{n}» has no part on the kept side of the plane (flip the plane).");
            var list = plus.ToList();
            foreach (var h in plus.ToList()) BooleanOp.Unite(list, FreeFormExec.Map(h, x), ctx.Warnings);
            int at = ctx.Bodies.IndexOf(b);
            ctx.Bodies.RemoveAt(at);
            ctx.Cages.Remove(n);
            for (int i = 0; i < list.Count; i++)
            {
                ctx.Bodies.Insert(at + i, list[i]);
                ctx.BodyNames[list[i]] = i == 0 ? n : $"{n}.{i + 1}";
            }
        }
    }
}

public enum FFArrayKind { Linear = 0, Circular = 1, Curve = 2 }

/// <summary>МАССИВ тел FreeForm: прямоугольный (одно или два направления), круговой, вдоль кривой — копии отдельными телами.</summary>
public sealed class FFArrayFeature : Feature, IFreeFormNative
{
    public FFArrayKind Kind;
    public readonly List<string> BodyNames = new();
    public ValueRef Count = new(3), Count2 = new(1), Angle = new(360);
    public Vector3d Step = new(30, 0, 0), Step2 = new(0, 30, 0);
    public Vector3d AxisAt;
    public Vector3d AxisDir = Vector3d.UnitZ;
    /// <summary>Кривая массива (опорная кривая по имени).</summary>
    public string CurveName = "";
    /// <summary>Вдоль кривой: копии поворачиваются по касательной.</summary>
    public bool Align;
    public FFArrayFeature(string name = "Array") { Name = name; }
    public override string Summary => Kind switch
    {
        FFArrayKind.Circular => $"Circular array ×{Count}, {Angle}°",
        FFArrayKind.Curve => $"Array along «{CurveName}» ×{Count}",
        _ => $"Linear array ×{Count}{(Count2.Value > 1 ? $" × {Count2}" : "")}",
    };

    public List<TransformOp.Xform> Placements(BuildContext ctx)
    {
        int n = (int)Math.Round(Count.Resolve(ctx.Parameters));
        if (n < 2) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the count must be at least 2.");
        var res = new List<TransformOp.Xform>();
        switch (Kind)
        {
            case FFArrayKind.Circular:
            {
                double total = Angle.Resolve(ctx.Parameters);
                if (AxisDir.LengthSquared < 1e-18) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no axis.");
                bool full = Math.Abs(Math.Abs(total) - 360) < 1e-9;
                double step = full ? total / n : total / (n - 1);
                for (int i = 1; i < n; i++) res.Add(TransformOp.Rotate(AxisAt, AxisDir.Normalized(), step * i));
                break;
            }
            case FFArrayKind.Curve:
            {
                var c = ctx.Part.Descendants().OfType<DatumCurve>().FirstOrDefault(x => x.Name == CurveName)
                    ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the curve «{CurveName}» is not found.");
                if (c.Points.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the curve is empty.");
                double len = c.Length;
                var (p0, t0) = At(c, 0);
                for (int i = 1; i < n; i++)
                {
                    var (p, t) = At(c, len * i / (n - 1));
                    var by = p - p0;
                    if (!Align || Vector3d.Cross(t0, t).Length < 1e-9) { res.Add(TransformOp.Move(by)); continue; }
                    var ax = Vector3d.Cross(t0, t).Normalized();
                    double deg = Math.Acos(Math.Clamp(Vector3d.Dot(t0, t), -1, 1)) * 180 / Math.PI;
                    var r = TransformOp.Rotate(p0, ax, deg); var m = TransformOp.Move(by);
                    res.Add(new TransformOp.Xform(q => m.Point(r.Point(q)), d => m.Direction(r.Direction(d)), false));
                }
                break;
            }
            default:
            {
                int n2 = Math.Max(1, (int)Math.Round(Count2.Resolve(ctx.Parameters)));
                if (Step.Length < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the step is zero.");
                for (int j = 0; j < n2; j++)
                    for (int i = 0; i < n; i++)
                        if (i + j > 0) res.Add(TransformOp.Move(Step * i + Step2 * j));
                break;
            }
        }
        return res;
    }

    /// <summary>Точка и касательная кривой на длине s.</summary>
    public static (Vector3d P, Vector3d T) At(DatumCurve c, double s)
    {
        var pts = c.Points;
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double l = (pts[i] - pts[i - 1]).Length;
            if (acc + l >= s - 1e-12 || i == pts.Count - 1)
            {
                double t = l < 1e-15 ? 0 : Math.Clamp((s - acc) / l, 0, 1);
                var dir = l < 1e-15 ? Vector3d.UnitX : (pts[i] - pts[i - 1]) / l;
                return (pts[i - 1] + (pts[i] - pts[i - 1]) * t, dir);
            }
            acc += l;
        }
        return (pts[^1], Vector3d.UnitX);
    }

    public override void Execute(BuildContext ctx)
    {
        if (BodyNames.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no bodies selected.");
        var xs = Placements(ctx);
        foreach (var n in BodyNames)
        {
            var b = FreeFormExec.Body(ctx, n, Name);
            int k = 2;
            foreach (var x in xs)
            {
                var c = FreeFormExec.Map(b, x);
                var an = $"{n} [{k++}]";
                ctx.Bodies.Add(c); ctx.BodyNames[c] = an;
                if (ctx.Cages.TryGetValue(n, out var ac)) ctx.Cages[an] = ac.Mapped(x.Point, x.Flips);
            }
        }
    }
}

/// <summary>
/// ВЫРОВНЯТЬ ГРАНЬ (FreeForm): плоские грани тела переходят в заданную плоскость (грань другого тела, опорная плоскость,
/// плоскость построения), соседние грани продлеваются или обрезаются до неё. Сторона грани сохраняется.
/// </summary>
public sealed class FFAlignFaceFeature : Feature
{
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public Vector3d PlaneAt;
    public Vector3d Normal = Vector3d.UnitZ;
    public ValueRef Offset = new(0);
    public FFAlignFaceFeature(string name = "Align face") { Name = name; }
    public override string Summary => $"Align {OnFaces.Count} face(s) to a plane{(Math.Abs(Offset.Value) > 0 ? $" + {Offset}" : "")}";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no body.");
        if (OnFaces.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no faces selected.");
        if (Normal.LengthSquared < 1e-18) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no target plane.");
        var body = ctx.Bodies[0];
        var faces = FacePick.Resolve(body, OnFaces, OnFaceNames, Name).ToHashSet();
        var n0 = Normal.Normalized();
        double off = Offset.Resolve(ctx.Parameters);
        string? why = null;
        var res = PlanarTweak.Apply(body, (i, f) =>
        {
            if (!faces.Contains(f)) return null;
            var on = PlanarTweak.OutwardNormal(f) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: a selected face is not planar.");
            var n = Vector3d.Dot(on, n0) >= 0 ? n0 : -n0;
            return (PlaneAt + n * off, n);
        }, ref why);
        ctx.Bodies[0] = res ?? throw new RebuildException(OperationOutcome.Unsupported, $"{Name}: the faces could not be aligned ({why}).");
    }
}

/// <summary>Кривая операции FreeForm: опорная кривая по имени или кривые эскиза (сшитые по концам).</summary>
public static class FFCurveSource
{
    public static List<Vector3d> Points(BuildContext ctx, string curveName, uint sketchId, string what)
    {
        if (curveName.Length > 0)
        {
            var dc = ctx.Part.Descendants().OfType<DatumCurve>().FirstOrDefault(x => x.Name == curveName)
                     ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{what}: the curve «{curveName}» is not found.");
            return dc.Points.ToList();
        }
        if (sketchId != 0 && ctx.FindSketch(sketchId) is { } sk)
        {
            var parts = new List<IReadOnlyList<Vector3d>>();
            foreach (var e in sk.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint))
            {
                var l = new List<Vector3d>(); e.ToCurve3(sk.Plane).Tessellate(l, true); if (l.Count >= 2) parts.Add(l);
            }
            if (parts.Count == 1) return parts[0].ToList();
            if (parts.Count > 1) return FFCurves.Join(parts, 1e-4);
        }
        throw new RebuildException(OperationOutcome.InvalidInput, $"{what}: no curve.");
    }

    public static ICurve3 Curve(BuildContext ctx, string curveName, uint sketchId, string what)
    {
        // прямая (все точки на отрезке концов) — точным отрезком: труба вдоль неё — точный цилиндр
        var pts0 = FFCurves.Dedup(Points(ctx, curveName, sketchId, what));
        if (pts0.Count >= 2)
        {
            var a = pts0[0]; var b = pts0[^1];
            if ((b - a).Length > Tol.Weld && pts0.All(p => (FFCurves.Closest(a, b, p) - p).Length < 1e-9 * Math.Max(1, (b - a).Length))) return new LineCurve3(a, b);
        }
        if (curveName.Length > 0 && ctx.Part.Descendants().OfType<DatumCurve>().FirstOrDefault(x => x.Name == curveName) is { } dc) return dc.Curve3();
        return new PolylineCurve3(FFCurves.Dedup(Points(ctx, curveName, sketchId, what)) is var q && q.Count >= 2 ? (IEnumerable<Vector3d>)q : throw new RebuildException(OperationOutcome.InvalidInput, $"{what}: the curve is too short."));
    }
}

/// <summary>ТРУБА вдоль кривой: круг радиуса R (и отверстие r) протягивается по кривой — новое тело (вырез — из тел).</summary>
public sealed class FFPipeFeature : Feature
{
    public string CurveName = "";
    public uint SketchId;
    public ValueRef Radius = new(3), Inner = new(0);
    public FFPipeFeature(string name = "Pipe") { Name = name; }
    public override string Summary => $"Pipe R{Radius}{(Inner.Value > 0 ? $" / r{Inner}" : "")} along «{(CurveName.Length > 0 ? CurveName : "sketch")}»";

    public override void Execute(BuildContext ctx)
    {
        double r = Radius.Resolve(ctx.Parameters), ri = Inner.Resolve(ctx.Parameters);
        if (!(r > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the radius must be positive.");
        if (ri < 0 || ri >= r - Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the inner radius must be less than the radius.");
        var path = FFCurveSource.Curve(ctx, CurveName, SketchId, Name);
        var p0 = path.PointAt(path.T0); var t0 = path.TangentAt(path.T0).Normalized();
        var fr = Frame3.FromNormal(p0, t0);
        var loops = new List<ICurve3> { new ArcCurve3(fr, r, 0, 2 * Math.PI) };
        if (ri > Tol.Weld) loops.Add(new ArcCurve3(fr, ri, 0, 2 * Math.PI));
        var solid = SweepOp.Build(ProfileBuilder.Build(fr, loops), path);
        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }
}

/// <summary>
/// ОТПЕЧАТОК кривой на плоской грани: грань делится по кривой (форма тела не меняется). Замкнутая кривая — область внутри
/// неё; незамкнутая — поперёк грани (область по одну сторону, до края грани). Делается вычитанием и возвратом тонкой
/// призмы области: внутренние грани взаимно уничтожаются, на грани остаётся новое ребро.
/// </summary>
public sealed class FFImprintFeature : Feature
{
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public string CurveName = "";
    public uint SketchId;
    public FFImprintFeature(string name = "Imprint") { Name = name; }
    public override string Summary => $"Imprint «{(CurveName.Length > 0 ? CurveName : "sketch")}» on a face";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no body.");
        var body = ctx.Bodies[0];
        var face = FacePick.Resolve(body, OnFaces, OnFaceNames, Name).FirstOrDefault() ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no face.");
        var pts = FFCurveSource.Points(ctx, CurveName, SketchId, Name);
        // гладкая опорная кривая, лежащая в плоскости грани, — одним ребром-сплайном
        SplineCurve3? smooth = null;
        if (CurveName.Length > 0 && ctx.Part.Descendants().OfType<DatumCurve>().FirstOrDefault(x => x.Name == CurveName) is { IsNurbs: true } dc
            && SketchFaceRef.PlaneOf(face) is { } pl && dc.Control!.All(c => Math.Abs(Vector3d.Dot(c - pl.P, pl.N.Normalized())) < 1e-9))
            smooth = dc.Spline;
        string? why = null;
        var res = ImprintOp.Apply(body, face, pts, smooth, ref why);
        ctx.Bodies[0] = res ?? throw new RebuildException(OperationOutcome.Unsupported, $"{Name}: the curve cannot be imprinted ({why}).");
    }
}


/// <summary>
/// SUBD-ТЕЛО FREEFORM: клетка (геометрия шага без параметров) и уровень сглаживания; правка клетки — новый шаг с полной
/// клеткой того же тела (Body). «В NURBS» — тело из NURBS-лоскутов, клетка больше не хранится.
/// </summary>
public sealed class FFSubDFeature : Feature, IFreeFormNative
{
    public SubDCage Cage = new();
    public string? Body;
    public bool ToNurbs;
    public FFSubDFeature(string name = "SubD") { Name = name; }
    public override string Summary => ToNurbs ? $"SubD → NURBS ({Cage.F.Count} cage faces)" : $"SubD cage {Cage.V.Count} vertices, {Cage.F.Count} faces, level {Cage.Level}";

    public override void Execute(BuildContext ctx)
    {
        if (Cage.F.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the cage is empty.");
        var solid = ToNurbs ? Cage.ToNurbs(Math.Max(2, Cage.Level), out _) : Cage.ToSolid(Cage.Level);
        string name;
        if (Body != null && ctx.BodyByName(Body) is { } old)
        {
            name = Body;
            ctx.Bodies[ctx.Bodies.IndexOf(old)] = solid;
        }
        else
        {
            name = Body ?? ctx.NewBodyName();
            ctx.Bodies.Add(solid);
        }
        ctx.BodyNames[solid] = name;
        if (ToNurbs) ctx.Cages.Remove(name); else ctx.Cages[name] = Cage;
    }
}
