using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

public enum EndType
{
    Distance = 0,   // на заданную глубину
    ThroughAll = 1, // насквозь
    ToNext = 2,     // до следующей поверхности
    ToSelected = 3  // до выбранной грани/ребра/точки
}

public sealed class ExtrudeSide
{
    public EndType End = EndType.Distance;
    public ValueRef Distance = new(20);
    /// <summary>Для ToSelected: id грани/ребра/точки, до которой строим.</summary>
    public uint TargetId;
    public bool Flip;
}

/// <summary>Выдавливание эскиза.</summary>
public sealed class ExtrudeFeature : Feature
{
    public uint SketchId;
    /// <summary>Две стороны от плоскости эскиза.</summary>
    public bool TwoSided;
    /// <summary>Симметрично: обе стороны на половину общей глубины.</summary>
    public bool Symmetric;

    public readonly ExtrudeSide Side1 = new();
    public readonly ExtrudeSide Side2 = new();

    /// <summary>u13: тонкое выдавливание — стенка толщиной Thin по контуру (торцы открыты); null — сплошное.</summary>
    public ValueRef? Thin;
    /// <summary>Стенка тонкого выдавливания — наружу от контура (иначе внутрь).</summary>
    public bool ThinOutside;

    /// <summary>Габарит сцены — им заменяется «насквозь».</summary>
    public double ThroughAllSize = 10000;

    public ExtrudeFeature(string name = "Extrude") { Name = name; }

    public override string Summary =>
        $"{(Cut ? "Cut" : "Extrude")} {Side1.Distance}" + (TwoSided ? $" / {Side2.Distance}" : "");

    /// <summary>
    /// Резать вырез-окружность точно, без гранения резца. Отключается переключателем P3D_QEXT.
    /// </summary>
    private static bool _gExactCircularCuts = Environment.GetEnvironmentVariable("P3D_QEXT") != "0";
    /// <summary>Поставка 2: значение снимка модели в контексте (ModelSettings.ExactCircularCuts), если он есть; иначе общее по умолчанию.</summary>
    public static bool ExactCircularCuts
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.ExactCircularCuts : _gExactCircularCuts;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactCircularCuts, value, "ExtrudeFeature.ExactCircularCuts");
    }

    /// <summary>
    /// Насколько тело простирается от точки входа вдоль направления: если резец короче, выйдет
    /// карман, если длиннее — сквозной вырез.
    /// </summary>
    /// <summary>
    /// Насколько тело простирается от точки ВДОЛЬ направления резца. KRN-08: прежде бралась проекция в обратную сторону
    /// (минимум) — у эскиза на грани она нулевая, и любой точный круглый карман на глубину уходил в сквозной вырез
    /// (измерено: куб 100, карман r=10 на 20 снимал π·100·100 вместо π·100·20). Мутация P3D_POCKETREACH=0.
    /// </summary>
    private static readonly bool ReachOld = Environment.GetEnvironmentVariable("P3D_POCKETREACH") == "0";
    private static double Reach(Kernel.Topology.TSolid body, Vector3d from, Vector3d dir)
    {
        double at = Vector3d.Dot(from, dir);
        double far = at;
        if (ReachOld)
        {
            foreach (var v in body.Vertices) far = Math.Min(far, Vector3d.Dot(v.P, dir));
            return at - far;
        }
        foreach (var v in body.Vertices) far = Math.Max(far, Vector3d.Dot(v.P, dir));
        return far - at;
    }

    /// <summary>
    /// Один ли круг в контуре: единственный замкнутый контур из одной дуги полного оборота.
    /// Возвращает центр и радиус, иначе ничего — и тогда работает обычный путь.
    /// </summary>
    private static (Vector3d Centre, double Radius)? CircleOf(Profile profile)
    {
        if (profile.Loops.Count != 1) return null;

        var loop = profile.Loops[0];
        if (loop.Curves.Count != 1) return null;
        if (loop.Curves[0] is not Kernel.Curves.ArcCurve3 arc) return null;
        if (Math.Abs(Math.Abs(arc.Sweep) - 2 * Math.PI) > 1e-6) return null;

        return (arc.Frame.Origin, arc.Radius);
    }

    /// <summary>u12: преобразование резца (зеркало/массив операций); null — обычное выполнение.</summary>
    private Kernel.Ops.TransformOp.Xform? _x;
    public override void ExecuteTransformed(BuildContext ctx, Kernel.Ops.TransformOp.Xform x)
    {
        _x = x;
        try { Execute(ctx); } finally { _x = null; }
    }

    string? _toolKey; Kernel.Topology.TSolid? _tool;
    static readonly bool ToolReuseOff = Environment.GetEnvironmentVariable("P3D_TOOLREUSE") == "0";
    /// <summary>Разбор: сколько резцов выдавливания взято из памяти.</summary>
    public static long ToolsReused;

    string? ToolKey(Profile shifted, Vector3d dir, BuildContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        static string V(Vector3d v) => $"{BitConverter.DoubleToInt64Bits(v.X):X}/{BitConverter.DoubleToInt64Bits(v.Y):X}/{BitConverter.DoubleToInt64Bits(v.Z):X}";
        sb.Append(V(dir)).Append('|').Append(V(shifted.Plane.Origin)).Append(V(shifted.Plane.W)).Append(V(shifted.Plane.U));
        foreach (var l in shifted.Loops)
        {
            sb.Append(l.IsOuter ? "|O" : "|I");
            foreach (var c in l.Curves)
            {
                // ключ — полное описание кривой; неизвестный вид — без памяти (по точкам кривую не опознать)
                switch (c)
                {
                    case Kernel.Curves.LineCurve3 ln: sb.Append(";L").Append(V(ln.A)).Append(V(ln.B)); break;
                    case Kernel.Curves.ArcCurve3 a: sb.Append(";A").Append(V(a.Frame.Origin)).Append(V(a.Frame.U)).Append(V(a.Frame.W)).Append(BitConverter.DoubleToInt64Bits(a.Radius)).Append('/').Append(BitConverter.DoubleToInt64Bits(a.A0)).Append('/').Append(BitConverter.DoubleToInt64Bits(a.A1)); break;
                    case Kernel.Curves.EllipseCurve3 el: sb.Append(";E").Append(V(el.Frame.Origin)).Append(V(el.Frame.U)).Append(V(el.Frame.W)).Append(BitConverter.DoubleToInt64Bits(el.RU)).Append('/').Append(BitConverter.DoubleToInt64Bits(el.RV)).Append('/').Append(BitConverter.DoubleToInt64Bits(el.A0)).Append('/').Append(BitConverter.DoubleToInt64Bits(el.A1)); break;
                    case Kernel.Curves.SplineCurve3 sp:
                        sb.Append(";S").Append(sp.Degree);
                        foreach (var q in sp.ControlPoints) sb.Append(V(q));
                        if (sp.Weights != null) foreach (var w in sp.Weights) sb.Append('w').Append(BitConverter.DoubleToInt64Bits(w));
                        if (sp.Knots != null) foreach (var k in sp.Knots) sb.Append('k').Append(BitConverter.DoubleToInt64Bits(k));
                        break;
                    default: return null;
                }
            }
        }
        if (Thin is { } th) sb.Append("|thin").Append(BitConverter.DoubleToInt64Bits(th.Resolve(ctx.Parameters))).Append(ThinOutside);
        sb.Append('|').Append(Kernel.KernelSettings.Capture() with { Preview = false, LivePreview = false });
        return sb.ToString();
    }

    public override void Execute(BuildContext ctx)
    {
        var sketch = ctx.FindSketch(SketchId)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: sketch not found.");

        sketch.UpdateDimensionsFromParameters(ctx.Parameters!);
        sketch.Solve(ctx.Solver);
        sketch.RequireSolved(Name);

        Profile profile = sketch.ToSolidProfile("Extrude");
        if (profile.IsEmpty)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: the sketch has no closed loop.");

        Vector3d n = sketch.Plane.W;

        double d1 = Depth(Side1, ctx);
        double d2 = TwoSided ? Depth(Side2, ctx) : 0;

        if (Symmetric)
        {
            double total = d1;
            d1 = total * 0.5;
            d2 = total * 0.5;
        }

        Vector3d from = -n * (TwoSided || Symmetric ? d2 : 0);
        Vector3d dir = n * (d1 + ((TwoSided || Symmetric) ? d2 : 0));

        if (dir.Length < Tol.Weld)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: zero depth.");

        var shifted = new Profile { Plane = profile.Plane.Translated(from) };
        foreach (var loop in profile.Loops)
        {
            var nl = new ProfileLoop { IsOuter = loop.IsOuter, Area = loop.Area };
            foreach (var c in loop.Curves) nl.Curves.Add(ExtrudeOp.Translate(c, from));
            shifted.Loops.Add(nl);
        }

        // KRN-10 (10.4): РЕЗЕЦ НЕИЗМЕННОЙ ОПЕРАЦИИ — ИЗ ПАМЯТИ. После правки предыдущей операции эта перестраивается заново,
        // но её резец (выдавливание профиля, тонкая стенка) от тела не зависит: ключ — профиль (кривые побитно), сдвиг,
        // направление, тонкая стенка. Совпал — копия запомненного резца (поверхности общие, топология своя). Мутация P3D_TOOLREUSE=0.
        string? key = _x == null && !ToolReuseOff ? ToolKey(shifted, dir, ctx) : null;
        bool reused = key != null && key == _toolKey && _tool != null;
        var solid = reused ? Kernel.Topology.TopologyClone.Deep(_tool!) : ExtrudeOp.Build(shifted, dir);
        if (reused) System.Threading.Interlocked.Increment(ref ToolsReused);
        (Vector3d Centre, double Radius)? circ = CircleOf(shifted);
        if (_x is { } xf)
        {
            // копия операции: резец и окружность точного пути — под преобразованием
            solid = Kernel.Topology.TopologyTransform.Apply(solid, xf) ?? Kernel.Ops.TransformOp.Copy(solid, xf);
            if (circ is { } cc) circ = (xf.Point(cc.Centre), cc.Radius);
            dir = xf.Direction(dir);
        }

        if (Thin is { } thinRef && !reused)
        {
            // u13: ТОНКОЕ ВЫДАВЛИВАНИЕ — полость резца с открытыми торцами (плоские грани поперёк направления): стенка по контуру
            double t = Math.Abs(thinRef.Resolve(ctx.Parameters));
            if (t <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: the thin wall must be thicker than zero.");
            var axisN = dir.Normalized();
            var caps = new List<Vector3d>();
            foreach (var f in solid.Faces)
            {
                var srf = Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
                if (srf is not { IsPlanar: true } || f.Loops.Count == 0 || f.Loops[0].CoEdges.Count == 0) continue;
                var p0 = f.Loops[0].CoEdges[0].Start.P;
                if (Math.Abs(Vector3d.Dot(f.NormalAt(p0).Normalized(), axisN)) > 1 - 1e-9) caps.Add(Kernel.Geometry.ExactCylinderCut.InteriorPoint(f, srf));
            }
            var tool = new List<Kernel.Topology.TSolid> { solid };
            if (!ShellOp.Hollow(tool, ThinOutside ? -t : t, caps, ctx.Warnings))
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Extrude: the thin wall could not be built.");
            solid = tool[0];
            circ = null;
        }
        if (Thin != null) circ = null;
        if (key != null && !reused) { _tool = Kernel.Topology.TopologyClone.Deep(solid); _toolKey = key; }

        if (Cut)
        {
            // ВЫРЕЗ ПО ОКРУЖНОСТИ — ТОЧНЫМ ПУТЁМ. Здесь известен замысел: эскиз из одной
            // окружности, выдавленный насквозь или на глубину, — это отверстие, и резец не
            // обязан граниться. Именно знание замысла и позволяет резать точно: во всяком
            // вычитании цилиндра, случившемся внутри другой операции, такого знания нет
            // (проверено на оболочке: там внутренняя труба уходила в точный путь и ошибалась).
            if (ExactCircularCuts && ctx.Bodies.Count == 1 && circ is { } circle)
            {
                var (centre, radius) = circle;
                Vector3d axis = dir.LengthSquared < 1e-18 ? Vector3d.UnitZ : dir.Normalized();

                // СКВОЗНОЙ ВЫРЕЗ ИЛИ КАРМАН — решает глубина выдавливания против тела. Резец,
                // не доходящий до дальней грани, оставляет карман с плоским дном; прежде такой
                // случай уходил обычным путём.
                double depth = dir.Length;
                double reach = Reach(ctx.Bodies[0], centre, axis);

                var exact = depth >= reach - Core.Tol.Chord
                    ? Kernel.Ops.QuadricCut.Cut(ctx.Bodies[0], centre, radius, ctx.Warnings, axis)
                    : Kernel.Ops.QuadricCut.CutBlind(ctx.Bodies[0], centre, radius, depth, ctx.Warnings, axis);

                if (exact != null) { ctx.Bodies[0] = exact; return; }
            }

            BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        }
        else
        {
            // БОБЫШКА ПО ОКРУЖНОСТИ — ТОЧНЫМ ПУТЁМ. Замысел тот же, что у выреза, только материал
            // добавляется: эскиз из одной окружности, выдавленный от грани, — цилиндрический
            // прилив, и резец не обязан граниться.
            if (ExactCircularCuts && ctx.Bodies.Count == 1 && circ is { } boss)
            {
                var (centre, radius) = boss;
                Vector3d axis = dir.LengthSquared < 1e-18 ? Vector3d.UnitZ : dir.Normalized();

                var grown = Kernel.Ops.QuadricCut.AddBoss(ctx.Bodies[0], centre, radius,
                                                          dir.Length, ctx.Warnings, axis);
                if (grown != null) { ctx.Bodies[0] = grown; return; }
            }

            BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
        }
    }

    private double Depth(ExtrudeSide s, BuildContext ctx) => s.End switch
    {
        EndType.ThroughAll => ThroughAllSize,
        EndType.ToNext => ThroughAllSize,      // до реализации булевых — как «насквозь»
        EndType.ToSelected => ThroughAllSize,
        _ => Math.Abs(s.Distance.Resolve(ctx.Parameters)) * (s.Flip ? -1 : 1)
    };
    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }

}
