using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>Вид отверстия.</summary>
public enum HoleKind
{
    /// <summary>Простое: цилиндр заданного диаметра.</summary>
    Simple,
    /// <summary>С цекованием: сверху цилиндрическое углубление большего диаметра.</summary>
    Counterbore,
    /// <summary>С зенковкой: сверху конус под головку винта.</summary>
    Countersink
}

/// <summary>
/// ОТВЕРСТИЕ — самая частая операция в деталях; прежде её приходилось делать вырезом
/// выдавливания, задавая окружность эскизом и повторяя это для каждого отверстия.
///
/// Положения берутся из эскиза (его точки) или задаются прямо. Глубина считается от плоскости
/// эскиза вдоль направления; сквозное отверстие режет насквозь с запасом, чтобы не оставлять
/// плёнку в доли микрона.
/// </summary>
public sealed class HoleFeature : Feature
{
    public override bool InstantPreviewSafe => true;
    public override bool InstantReplaySafe => true;
    /// <summary>Эскиз, точки которого задают положения отверстий.</summary>
    public uint SketchId;

    /// <summary>Положения, заданные прямо (в дополнение к точкам эскиза).</summary>
    public readonly List<Vector3d> Points = new();

    /// <summary>
    /// KRN-08 (ворота g.09): имя плоской грани входа для каждой точки (null — не привязана). Прежде точки отверстия были
    /// неподвижными точками мира: правка высоты бруска оставляла глухое отверстие висеть над гранью или уходить вглубь —
    /// молча. Теперь при первом построении точка, лежащая на одной плоской грани, запоминает её имя, и дальше берётся
    /// проекция точки на текущую грань с этим именем. Грани нет — явная ошибка; одноимённые грани на разных плоскостях —
    /// неоднозначность. Мутация P3D_HOLEFACE=0.
    /// </summary>
    public readonly List<string?> PointFaces = new();
    static readonly bool HoleFaceOff = Environment.GetEnvironmentVariable("P3D_HOLEFACE") == "0";

    private List<Vector3d> FollowFaces(BuildContext ctx)
    {
        var res = new List<Vector3d>(Points);
        if (HoleFaceOff) return res;
        while (PointFaces.Count < Points.Count) PointFaces.Add(null);
        var faces = ctx.Bodies.Select(Kernel.Ops.InstantPreview.Query).SelectMany(b => b.Faces).ToList();   // k113: у носителя — тело до операции
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (PointFaces[i] is { } name)
            {
                var named = faces.Where(f => f.Tag == name).Select(f => (F: f, Pl: SketchFaceRef.PlaneOf(f))).Where(x => x.Pl != null).ToList();
                if (named.Count == 0)
                    throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Hole: the entry face '{Kernel.Topology.EdgeRef.Bare(name)}' no longer exists.");
                var (p0, n0) = named[0].Pl!.Value; n0 = n0.Normalized();
                if (named.Any(x => Math.Abs(Math.Abs(Vector3d.Dot(x.Pl!.Value.N.Normalized(), n0)) - 1) > 1e-9 || Math.Abs(Vector3d.Dot(x.Pl!.Value.P - p0, n0)) > 1e-6))
                    throw new AmbiguousReferenceException($"Hole «{Name}»", named.Select(x => (x.F.Tag, x.Pl!.Value.P)).ToList(),
                        $"{named.Count} faces named '{Kernel.Topology.EdgeRef.Bare(name)}' lie on different planes");
                res[i] = p - n0 * Vector3d.Dot(p - p0, n0);
            }
            else
            {
                var on = faces.Where(f => SketchFaceRef.PlaneOf(f) != null && !string.IsNullOrEmpty(f.Tag) && FaceRefs.Distance(f, p) <= 1e-3).ToList();
                if (on.Count > 0 && on.All(f => f.Tag == on[0].Tag)) PointFaces[i] = on[0].Tag;
            }
        }
        return res;
    }

    /// <summary>Направление сверления (от точки внутрь тела).</summary>
    public Vector3d Direction = -Vector3d.UnitZ;

    /// <summary>
    /// Вид отверстия. Имя намеренно то же, что у общего свойства операции, поэтому оно
    /// объявлено через new: здесь речь о виде ОТВЕРСТИЯ, а не о виде операции.
    /// </summary>
    public new HoleKind Kind = HoleKind.Simple;

    public ValueRef Diameter = new(6);

    /// <summary>Глубина; при сквозном не используется.</summary>
    public ValueRef Depth = new(20);
    public bool ThroughAll;

    /// <summary>Цекование: диаметр и глубина. Зенковка: диаметр и угол при вершине.</summary>
    public ValueRef HeadDiameter = new(12);
    public ValueRef HeadDepth = new(4);
    public ValueRef HeadAngle = new(90);

    /// <summary>
    /// Строить подходящие отверстия точно, без гранения резца. По умолчанию включено: путь
    /// проверен тестами, а при неподходящем случае сам отказывается, и работает обычный.
    /// </summary>
    private static bool _gCompositeTool = Environment.GetEnvironmentVariable("P3D_COMPOSITE") == "1";
    /// <summary>
    /// Поставка 4: цекование и зенковка в общей булевой — одним составным телом вращения (сверло и головка).
    /// ВЫКЛЮЧЕНО ПО УМОЛЧАНИЮ ПО ЗАМЕРУ. С обновления 18 составной инструмент годен и точен для обоих видов (объём
    /// против формулы 2.9e-11 мм³, как у двух вычитаний; сетка не хуже), но МЕДЛЕННЕЕ: +11 % медианы полного
    /// перестроения реалистичной детали (414.9 против 374.1 мс) — соосные цилиндры двух вычитаний идут быстрым прямым
    /// вводом (A04), тело вращения — общим. Корпус на двух вычитаниях не ломается — выигрыша нет. P3D_COMPOSITE=1.
    /// </summary>
    public static bool CompositeTool
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.CompositeTool : _gCompositeTool;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gCompositeTool, value, "HoleFeature.CompositeTool");
    }

    private static bool _gBlindEdgeCut = Environment.GetEnvironmentVariable("P3D_BLINDEDGE") != "0";
    /// <summary>
    /// Поставка 4: глухое отверстие, кромка которого задевает край грани, — точно через ExactCylinderCut с дном
    /// (маршрут exact-cut-edge-blind). Значение снимка модели (ModelSettings.BlindEdgeCut); P3D_BLINDEDGE=0.
    /// </summary>
    public static bool BlindEdgeCut
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.BlindEdgeCut : _gBlindEdgeCut;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gBlindEdgeCut, value, "HoleFeature.BlindEdgeCut");
    }

    private static bool _gExactHoles = Environment.GetEnvironmentVariable("P3D_QHOLE") != "0";
    /// <summary>Поставка 2: значение снимка модели в контексте (ModelSettings.ExactHoles), если он есть; иначе общее по умолчанию.</summary>
    public static bool ExactHoles
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.ExactHoles : _gExactHoles;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactHoles, value, "HoleFeature.ExactHoles");
    }

    public HoleFeature(string name = "Hole") { Name = name; }

    public override string Summary =>
        $"{Kind} hole \u2300{Diameter}" + (ThroughAll ? " through" : $" \u2193{Depth}") + $" \u00d7{Count}";

    private int Count => Points.Count;

    /// <summary>u12: преобразование точек и направления (зеркало/массив операций); null — обычное выполнение.</summary>
    private Kernel.Ops.TransformOp.Xform? _x;
    public override void ExecuteTransformed(BuildContext ctx, Kernel.Ops.TransformOp.Xform x)
    {
        _x = x;
        try { Execute(ctx); } finally { _x = null; }
    }
    private Vector3d DirW => (_x is { } xf ? xf.Direction(Direction) : Direction).Normalized();

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: no body.");

        double d = Diameter.Resolve(ctx.Parameters);
        if (d <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: the diameter must be greater than zero.");
        if (Direction.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: the direction is not set.");

        var at = FollowFaces(ctx);
        if (SketchId != 0 && ctx.FindSketch(SketchId) is { } sk)
            foreach (var p in sk.Entities.OfType<Sketching.SkPoint>())
                at.Add(sk.Plane.ToWorld(new Vector2d(p.X, p.Y)));

        if (at.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: no positions given.");
        if (_x is { } xp) at = at.Select(xp.Point).ToList();

        Vector3d dir = DirW;
        double depth = ThroughAll ? Extent(ctx.Bodies.Select(Kernel.Ops.InstantPreview.Query).ToList()) * 3 : Depth.Resolve(ctx.Parameters);
        if (depth <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: the depth must be greater than zero.");

        // KRN-11 (11.2): ОТВЕРСТИЕ МИМО ТЕЛА. Точка вне тела прежде давала «успешную» операцию без снятого материала
        // (KRN11-QUAL-NEG-HOLE-MISSES). Резец (ось от точки на глубину, радиус головки) не задевает даже габарит тела с запасом
        // 10 % — такая точка пропускается с предупреждением; все точки мимо — отказ InvalidInput. Мутация P3D_HOLEMISS=0.
        if (!HoleMissOff)
        {
            var bb = Kernel.Ops.InstantPreview.Query(ctx.Bodies[0]).Bounds; bb = bb.Expanded(0.1 * bb.Diagonal + Tol.Weld);
            double rr = Math.Max(d, Kind == HoleKind.Simple ? 0 : HeadDiameter.Resolve(ctx.Parameters)) / 2;
            var dn = DirW;
            bool Misses(Vector3d q)
            {
                var a0 = q - dn * rr; var a1 = q + dn * depth;
                var lo = Vector3d.ComponentMin(a0, a1) - new Vector3d(rr); var hi = Vector3d.ComponentMax(a0, a1) + new Vector3d(rr);
                return hi.X < bb.Min.X || hi.Y < bb.Min.Y || hi.Z < bb.Min.Z || lo.X > bb.Max.X || lo.Y > bb.Max.Y || lo.Z > bb.Max.Z;
            }
            var miss = at.Where(Misses).ToList();
            if (miss.Count == at.Count)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput,
                    at.Count == 1 ? "Hole: the position is outside the body." : "Hole: every position is outside the body.");
            foreach (var q in miss) ctx.Warnings.Add($"Hole: the position {q.X:0.###}, {q.Y:0.###}, {q.Z:0.###} is outside the body and was skipped.");
            if (miss.Count > 0) at = at.Where(q => !miss.Contains(q)).ToList();
        }

        var routes = new List<List<HoleRouteDecision>>();
        LastRoutes = routes;
        foreach (var p in at)
        {
            // ТОЧНЫЙ ПУТЬ, КОГДА СЛУЧАЙ ПОДХОДИТ. Простое отверстие в теле из плоских граней
            // строится без гранения резца: стенка остаётся цилиндром, а не набором лоскутов.
            // Для цекования и зенковки, а также для тел с кривыми гранями это пока не сделано,
            // и там работает обычный путь — подменять его неготовым нельзя.
            // Глухое отверстие прошло тот же путь, что и сквозное. Оно долго давало неверный
            // объём (−33.4 мм³, затем 484.5 при расчётных 251.3), и причина оказалась не в
            // отсчёте глубины, а в РАЗВОРОТЕ ДОНЫШКА: грань смотрела внутрь тела, и её вклад в
            // знаковый объём шёл с обратным знаком.
            // ПОСТАВКА 4. ЯВНЫЙ КОНТРАКТ ПРИМЕНИМОСТИ ТОЧНЫХ ПУТЕЙ. Каждый маршрут — предусловие («применим /
            // неприменим: причина»), попытка («принят / отказался: причина») и свой контракт проверки результата
            // (негодный отвергается в пользу следующего). Порядок и проверки — прежние; решение по каждому
            // маршруту — в трассе операции (LastRoutes), предупреждения — те же, что прежде.
            var trace = new List<HoleRouteDecision>();
            routes.Add(trace);
            TSolid? exact = null;
            var body = ctx.Bodies[0];
            string? off = Kernel.Ops.InstantPreview.Active ? "instant preview (mesh)" : !ExactHoles ? "exact holes switched off (HoleFeature.ExactHoles)" : ctx.Bodies.Count != 1 ? "the part has more than one body" : null;
            bool lastInvalid = false;
            bool Route(string name, string? notApplicable, Func<TSolid?> attempt, bool fullValidation, string? invalidWarning)
            {
                if (exact != null) return true;
                lastInvalid = false;
                if (notApplicable != null) { trace.Add(new(name, HoleRouteVerdict.NotApplicable, notApplicable)); return false; }
                int w0 = ctx.Warnings.Count;
                var r = attempt();
                if (r == null)
                {
                    string why = ctx.Warnings.Count > w0 ? ctx.Warnings[^1] : "declined without a reason";
                    trace.Add(new(name, HoleRouteVerdict.Declined, why)); return false;
                }
                var v = fullValidation ? Kernel.Topology.SolidValidator.Validate(r) : Kernel.Topology.SolidValidator.ValidateBRep(r);
                if (!v.IsValid)
                {
                    if (invalidWarning != null) ctx.Warnings.Add(invalidWarning);
                    trace.Add(new(name, HoleRouteVerdict.Invalid, v.Summary())); lastInvalid = true; return false;
                }
                // KRN-07 (07.2): особый путь объявляет уровень гарантии, как общая булева (квадрики и их стыки — точные).
                if (!HoleGuarOff && r.Guarantee == Parametric3D.Kernel.GuaranteeLevel.Unspecified) r.Guarantee = Parametric3D.Kernel.GuaranteeLevel.ExactQuadric;
                // KRN-01 (01.5): особый путь карту не пишет — собирается по картам промежуточных тел и тождеству поверхностей
                if (!HoleProvOff && !Kernel.Topology.ProvenanceBySurface.Covers(r.Provenance, r, new[] { body })) r.SetProvenance(Kernel.Topology.ProvenanceBySurface.Compose("Hole/" + name, new[] { body }, r));
                trace.Add(new(name, HoleRouteVerdict.Accepted, null)); exact = r; return true;
            }

            // 1. Квадрики соосно (QuadricCut): простое отверстие, а цекование и зенковка — только сквозные.
            //    Цекование — два СООСНЫХ цилиндра: пересечения между ними нет, поэтому и здесь всё строится точно.
            string quadricName = Kind == HoleKind.Counterbore ? "quadric-counterbore" : Kind == HoleKind.Countersink ? "quadric-countersink"
                               : ThroughAll ? "quadric-through" : "quadric-blind";
            string? quadricNa = off ?? (Kind != HoleKind.Simple && !ThroughAll ? "counterbore and countersink are exact only when through" : null);
            Route(quadricName, quadricNa, () => Kind == HoleKind.Counterbore
                    ? Kernel.Ops.QuadricCut.CutCounterbore(body, p, d / 2, HeadDiameter.Resolve(ctx.Parameters) / 2, HeadDepth.Resolve(ctx.Parameters), ctx.Warnings, dir)
                    : Kind == HoleKind.Countersink
                    ? Kernel.Ops.QuadricCut.CutCountersink(body, p, d / 2, HeadDiameter.Resolve(ctx.Parameters) / 2, HeadAngle.Resolve(ctx.Parameters), ctx.Warnings, dir)
                    : ThroughAll ? Kernel.Ops.QuadricCut.Cut(body, p, d / 2, ctx.Warnings, dir)
                                 : Kernel.Ops.QuadricCut.CutBlind(body, p, d / 2, depth, ctx.Warnings, dir),
                  // K08: точный путь отверстия идёт мимо BooleanOp.Evaluate и его проверки — проверяется здесь.
                  fullValidation: false, "Hole: the exact quadric path produced an invalid body; the general Boolean path was used.");

            // 2. Точная булева по пересечениям поверхностей (G4) — простое отверстие в теле из плоских граней;
            //    НАКЛОННОЕ (ось не по нормали грани) — кромки эллипсы, стенка цилиндр. У вала точный объём по
            //    прослеженным кромкам считается ломаной, и прослеживание медленнее особого пути CrossHole.
            string? planarNa = off ?? (Kind != HoleKind.Simple ? "only a simple hole" :
                !body.Faces.All(fc => Kernel.Geometry.ExactEdges.ShapeOf(Kernel.Topology.GeometryAudit.AuthoritativeSurface(fc)) is Kernel.Geometry.Ssi.Plane)
                    ? "the body has non-planar faces" : null);
            double over0 = Math.Max(Tol.Weld * 100, d * 0.01);
            var cyl = new Kernel.Geometry.Ssi.Cylinder(p - dir * over0, dir, d / 2);
            const string invalidExact = "Hole: the exact boolean produced an invalid body; the general Boolean path was used.";
            if (ThroughAll)
            {
                Route("exact-through", planarNa, () => Kernel.Geometry.ExactBoolean.SubtractThroughCylinder(body, cyl, ctx.Warnings), true, invalidExact);
                // Кромка задевает край грани (отверстие у края, через угол) — с делением граней. Как прежде, только
                // если предыдущий маршрут ОТКАЗАЛСЯ (null), а не выдал негодное тело.
                Route("exact-cut-edge", planarNa ?? (lastInvalid ? "exact-through produced an invalid body" : null),
                      () => Kernel.Geometry.ExactCylinderCut.Cut(body, cyl, ctx.Warnings), true, invalidExact);
            }
            else
            {
                Route("exact-blind", planarNa, () => Kernel.Geometry.ExactBoolean.SubtractBlindCylinder(body, cyl, p + dir * depth, ctx.Warnings), true, invalidExact);
                // Поставка 4: ГЛУХОЕ отверстие, кромка которого задевает край грани, — ExactCylinderCut с дном.
                Route("exact-cut-edge-blind", planarNa ?? (BlindEdgeCut ? null : "blind ExactCylinderCut switched off (HoleFeature.BlindEdgeCut)"),
                      () => Kernel.Geometry.ExactCylinderCut.Cut(body, cyl, p + dir * depth, ctx.Warnings), true, invalidExact);
            }

            // 3. ПОПЕРЕЧНОЕ ОТВЕРСТИЕ В ВАЛУ: тело ограничено не плоскостями, а цилиндром, и стык отверстия с ним —
            //    кривая четвёртой степени; её ход ведётся уточнением с машинной точностью. K08: негодный результат
            //    (грани, не обрезанные кромками) отвергается в пользу общей булевой.
            var shaft = exact == null && Kind == HoleKind.Simple && ThroughAll && off == null ? ShaftOf(body) : null;
            Route("cross-hole", off ?? (Kind != HoleKind.Simple || !ThroughAll ? "only a simple through hole" : shaft == null ? "the body is not a plain shaft" : null),
                  () => InheritShaftTags(body, Kernel.Ops.CrossHole.Cut(shaft!.Value.At, shaft.Value.Axis, shaft.Value.Radius, shaft.Value.Length, p, dir, d / 2, ctx.Warnings), shaft.Value.At, shaft.Value.Axis),
                  // Поставка 4: полная проверка (B-rep и сетка) — годное B-rep с негодной сеткой ломало бы деталь.
                  true, "Hole: the cross-hole path produced an invalid body; the general Boolean path was used.");

            if (exact != null)
            {
                ctx.Bodies[0] = exact;
                continue;
            }
            trace.Add(new("general-boolean", HoleRouteVerdict.Accepted, null));

            // Сверло начинается чуть выше поверхности: иначе в месте входа остаётся плёнка
            // толщиной в доли микрона, и тело перестаёт быть замкнутым.
            double over = Math.Max(Tol.Weld * 100, d * 0.01);
            // Наклонное отверстие: плоский торец сверла должен быть выше ВСЕЙ кромки входа, а не
            // только её центра. Прежде у отверстия под 25° в кубе торец оставался в материале с
            // одной стороны, и у входа оставался клин (+18 мм³ против точной булевой и интеграла).
            if (Math.Abs(Vector3d.Dot(dir, Vector3d.UnitZ)) < 1 - 1e-9 && Math.Abs(Vector3d.Dot(dir, Vector3d.UnitX)) < 1 - 1e-9
                && Math.Abs(Vector3d.Dot(dir, Vector3d.UnitY)) < 1 - 1e-9)
                over += d * 2;
            Vector3d start = p - dir * over;

            // ПОСТАВКА 4: СОСТАВНОЙ ИНСТРУМЕНТ. Цекование и зенковка — ОДНО тело вращения (сверло и головка), одно
            // вычитание вместо двух соосных резцов (совпадающие поверхности между операциями, верх конуса зенковки —
            // ровно на грани тела). Головка зенковки продолжена конусом выше поверхности на тот же запас, что у сверла
            // (снаружи тела — форма результата та же). Где профиль не сводится к одному телу — прежний путь.
            if (Kind != HoleKind.Simple && CompositeTool && CompositeProfile(d, depth + over, over, ctx) is { } prof)
            {
                trace[^1] = new("general-boolean", HoleRouteVerdict.Accepted, "composite tool");
                BooleanOp.Subtract(ctx.Bodies, Revolved(start, dir, prof), ctx.Warnings);
                continue;
            }

            var drill = Cylinder(start, dir, d / 2, depth + over);
            BooleanOp.Subtract(ctx.Bodies, drill, ctx.Warnings);

            switch (Kind)
            {
                case HoleKind.Counterbore:
                {
                    double hd = HeadDiameter.Resolve(ctx.Parameters);
                    double hh = HeadDepth.Resolve(ctx.Parameters);
                    if (hd <= d || hh <= Tol.Weld)
                        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: the counterbore must be wider than the hole and deeper than zero.");
                    BooleanOp.Subtract(ctx.Bodies, Cylinder(start, dir, hd / 2, hh + over), ctx.Warnings);
                    break;
                }

                case HoleKind.Countersink:
                {
                    double hd = HeadDiameter.Resolve(ctx.Parameters);
                    double angle = HeadAngle.Resolve(ctx.Parameters);
                    if (hd <= d) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: the countersink must be wider than the hole.");
                    if (angle <= 0 || angle >= 180) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Hole: the countersink angle must be between 0 and 180 degrees.");

                    // Высота конуса от кромки зенковки до диаметра самого отверстия.
                    double h = (hd - d) / 2 / Math.Tan(angle * Math.PI / 360);
                    BooleanOp.Subtract(ctx.Bodies, Cone(p, dir, hd / 2, d / 2, h), ctx.Warnings);
                    break;
                }
            }
        }
        CanonicalRoles(ctx, at, dir, d);
    }

    /// <summary>Мутация P3D_HOLEROLES=0: имена граней отверстия — по маршруту («quadric:hole», «exact:hole-wall», грани резца), как прежде.</summary>
    static readonly bool HoleRolesOff = Environment.GetEnvironmentVariable("P3D_HOLEROLES") == "0";
    /// <summary>Мутация P3D_HOLEGUAR=0: особый путь не объявляет уровень гарантии (Unspecified), как прежде.</summary>
    static readonly bool HoleGuarOff = Environment.GetEnvironmentVariable("P3D_HOLEGUAR") == "0";
    /// <summary>Мутация P3D_HOLEPROV=0: результат особого пути отверстия без карты происхождения.</summary>
    static readonly bool HoleProvOff = Environment.GetEnvironmentVariable("P3D_HOLEPROV") == "0";

    /// <summary>Роли граней отверстия — одни на все маршруты.</summary>
    public const string RoleWall = "hole:wall", RoleBottom = "hole:bottom", RoleCounterbore = "hole:counterbore",
                        RoleCounterboreBottom = "hole:counterbore-bottom", RoleCountersink = "hole:countersink", RoleOther = "hole:face";

    /// <summary>
    /// KRN-07 (07.2): ЕДИНЫЙ КОНТРАКТ ИМЁН. Прежде имя грани отверстия зависело от маршрута: «quadric:hole» у соосного пути,
    /// «exact:hole-wall» у наклонного и у края, «cross:hole» у вала, имена граней резца («extrude[0]:side[0.0]») у общей
    /// булевой. Правка, переводящая отверстие с одного маршрута на другой (наклон оси, отверстие у края, отключение точных
    /// путей), меняла имена — и рвала ссылки на кромки и грани отверстия. Теперь новые грани операции получают роль по
    /// ГЕОМЕТРИИ, одинаково для всех маршрутов: стенка (цилиндр диаметра отверстия), дно (плоскость поперёк оси на глубине),
    /// цекование и его дно, зенковка (конус). Внутренние метки пути (квадрика — для разметки) остаются до конца операции.
    /// </summary>
    void CanonicalRoles(BuildContext ctx, List<Vector3d> at, Vector3d dir, double d)
    {
        if (HoleRolesOff) return;
        double hd = Kind != HoleKind.Simple ? HeadDiameter.Resolve(ctx.Parameters) : 0;
        double hh = Kind == HoleKind.Counterbore ? HeadDepth.Resolve(ctx.Parameters) : double.NaN;
        double depth = ThroughAll ? double.NaN : Depth.Resolve(ctx.Parameters);
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b));
        bool AtDepth(Vector3d q, double h) => !double.IsNaN(h) && at.Any(p => Near(Vector3d.Dot(q - p, dir), h));
        foreach (var b in ctx.Bodies)
            foreach (var f in b.Faces)
            {
                if (f.Tag.Length > 0 && f.Tag[0] == '#') continue;
                var sh = Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
                bool along(Vector3d a) => Math.Abs(Math.Abs(Vector3d.Dot(a.Normalized(), dir)) - 1) < 1e-9;
                string role = sh switch
                {
                    Kernel.Geometry.Ssi.Cylinder c when along(c.Axis) && Near(c.R, d / 2) => RoleWall,
                    Kernel.Geometry.Ssi.Cylinder c when along(c.Axis) && Kind == HoleKind.Counterbore && Near(c.R, hd / 2) => RoleCounterbore,
                    Kernel.Geometry.Ssi.Cone c when along(c.Axis) => RoleCountersink,
                    Kernel.Geometry.Ssi.Plane pl when along(pl.N) && AtDepth(pl.P, hh) => RoleCounterboreBottom,
                    Kernel.Geometry.Ssi.Plane pl when along(pl.N) && AtDepth(pl.P, depth) => RoleBottom,
                    _ => RoleOther,
                };
                f.Tag = role;
            }
    }

    /// <summary>
    /// Поставка 4: трасса выбора пути последнего выполнения — по каждой точке отверстия решения по маршрутам в
    /// порядке перебора (неприменим / отказался / негоден / принят, с причиной). Последним идёт принятый маршрут.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<HoleRouteDecision>> LastRoutes { get; private set; } = Array.Empty<IReadOnlyList<HoleRouteDecision>>();

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }

    /// <summary>
    /// Вал ли это: тело из одной цилиндрической грани и двух торцов. Возвращает ось, радиус и
    /// длину, иначе ничего — поперечное отверстие умеется пока только в таком теле.
    /// </summary>
    /// <summary>Мутация P3D_CROSSTAGS=0: грани вала после поперечного отверстия — с именами отверстия (прежде).</summary>
    static readonly bool CrossTagsOld = Environment.GetEnvironmentVariable("P3D_CROSSTAGS") == "0";

    /// <summary>
    /// KRN-08: ГРАНИ ВАЛА СОХРАНЯЮТ СВОИ ИМЕНА. Особый путь поперечного отверстия собирает тело заново, и все его грани
    /// получали имена отверстия («#H/cross:shaft», торцы — «#H/cross:cap-…»): селектор кромок отверстия (Rim — ребро между
    /// гранью отверстия и гранью не его) не находил ни одного ребра, а ссылки на боковую грань и торцы вала рвались. Теперь
    /// боковая грань и торцы берут имена и атрибуты исходных граней вала (по поверхности: цилиндр — боковая, плоскость —
    /// торец по положению вдоль оси); имя отверстия — только у стенки.
    /// </summary>
    private static TSolid? InheritShaftTags(TSolid body, TSolid? res, Vector3d at, Vector3d axis)
    {
        if (res == null || CrossTagsOld) return res;
        var w = axis.Normalized();
        TFace? origSide = null; var origCaps = new List<(double H, TFace F)>();
        foreach (var f in body.Faces)
        {
            var src = Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
            if (src is Kernel.Surfaces.ExtrudedSurface { Base: Kernel.Curves.ArcCurve3 }) origSide = f;
            else if (f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P is Vector3d q) origCaps.Add((Vector3d.Dot(q - at, w), f));
        }
        var prov = new Kernel.Topology.Provenance { Operation = "Hole/cross-hole" };
        foreach (var f in res.Faces)
        {
            TFace? from = null;
            if (f.Tag == "cross:shaft") from = origSide;
            else if (f.Tag is "cross:cap-low" or "cross:cap-high" && f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P is Vector3d q && origCaps.Count > 0)
            {
                double h = Vector3d.Dot(q - at, w);
                from = origCaps.OrderBy(c => Math.Abs(c.H - h)).First().F;
            }
            prov.Faces.Add(new Kernel.Topology.FaceProvenance(f, from, from == null ? Kernel.Topology.FaceOrigin.Generated : Kernel.Topology.FaceOrigin.Modified));
            if (from == null || from.Tag.Length == 0) continue;
            f.Tag = from.Tag; f.Attr = from.Attr; f.MaterialSlot = from.MaterialSlot;
        }
        // KRN-01 (01.5): происхождение — грани вала из граней вала, стенка отверстия новая
        if (!HoleProvOff) res.SetProvenance(prov);
        return res;
    }

    private static (Vector3d At, Vector3d Axis, double Radius, double Length)? ShaftOf(TSolid body)
    {
        Kernel.Surfaces.ExtrudedSurface? side = null;
        int planes = 0;

        foreach (var f in body.Faces)
        {
            var src = f.Surface is Kernel.Surfaces.FacetedSurface fs ? fs.Source : f.Surface;
            if (src is Kernel.Surfaces.ExtrudedSurface ex && ex.Base is Kernel.Curves.ArcCurve3)
            {
                if (side != null) return null;          // цилиндрических граней больше одной
                side = ex;
            }
            else if (src is Kernel.Surfaces.PlaneSurface
                     || (src is Kernel.Surfaces.ExtrudedSurface exl && exl.Base is Kernel.Curves.LineCurve3)) planes++;
            else return null;
        }

        if (side == null || planes != 2) return null;
        if (side.Base is not Kernel.Curves.ArcCurve3 arc) return null;
        if (Math.Abs(arc.Sweep) < 2 * Math.PI - 1e-6) return null;   // не полный цилиндр

        Vector3d dirAxis = side.Dir;
        if (dirAxis.LengthSquared < 1e-18) return null;

        return (arc.Frame.Origin, dirAxis.Normalized(), arc.Radius, dirAxis.Length);
    }

    /// <summary>Цилиндр от точки вдоль направления.</summary>
    private static TSolid Cylinder(Vector3d at, Vector3d dir, double r, double h)
    {
        // K07. СВЕРЛО ВДОЛЬ −Z СТРОИТСЯ КАК ТОТ ЖЕ ЦИЛИНДР ВДОЛЬ +Z от нижнего конца. Тело то же
        // самое; но в таком виде его принимает прямой точный ввод цилиндра (A04) — без гранения,
        // склейки точек и слияния треугольников. Повёрнутый поворотом на 180° цилиндр этот путь
        // не принимал, и каждое сверло шло общим путём (25 мс и 10 МБ выделений на сверло в
        // реалистичных деталях). Подписи торцов меняются местами, чтобы имена граней остались
        // прежними: торец у дна отверстия называется так же, как раньше.
        var d = dir.Normalized();
        if (Math.Abs(d.X) < 1e-15 && Math.Abs(d.Y) < 1e-15 && d.Z < 0)
        {
            var straight = PrimitiveOp.Cylinder(at - Vector3d.UnitZ * h, r, h);
            var caps = straight.Faces.Where(f => f.Surface is Kernel.Surfaces.PlaneSurface).ToList();
            if (caps.Count == 2) (caps[0].Tag, caps[1].Tag) = (caps[1].Tag, caps[0].Tag);
            return straight;
        }
        var body = PrimitiveOp.Cylinder(Vector3d.Zero, r, h);
        return Place(body, at, dir);
    }

    /// <summary>Конус от точки вдоль направления: r0 у начала, r1 у конца.</summary>
    /// <summary>
    /// Профиль составного инструмента (радиус, расстояние вдоль оси от начала сверла): ось → головка → сход → сверло
    /// → ось. null — профиль не сводится к одному телу (глухое отверстие не глубже головки) или неверные размеры
    /// (тогда прежний путь и его сообщения об ошибке).
    /// </summary>
    private List<Vector2d>? CompositeProfile(double d, double length, double over, BuildContext ctx)
    {
        double r = d / 2, hd = HeadDiameter.Resolve(ctx.Parameters), R = hd / 2;
        if (hd <= d) return null;
        if (Kind == HoleKind.Counterbore)
        {
            double hh = HeadDepth.Resolve(ctx.Parameters);
            if (hh <= Tol.Weld || hh + over >= length - Tol.Weld) return null;
            return new() { new(0, 0), new(R, 0), new(R, hh + over), new(r, hh + over), new(r, length), new(0, length) };
        }
        double angle = HeadAngle.Resolve(ctx.Parameters);
        if (angle <= 0 || angle >= 180) return null;
        double t = Math.Tan(angle * Math.PI / 360);
        double h = (hd - d) / 2 / t;                      // высота конуса от кромки зенковки до диаметра отверстия
        if (over + h >= length - Tol.Weld) return null;
        return new() { new(0, 0), new(R + over * t, 0), new(r, over + h), new(r, length), new(0, length) };
    }

    /// <summary>Тело вращения профиля (радиус, расстояние вдоль оси) вокруг оси dir из точки at.</summary>
    private static TSolid Revolved(Vector3d at, Vector3d dir, List<Vector2d> prof)
    {
        var plane = new Kernel.Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        var pts = prof.Select(q => new Vector3d(q.X, 0, q.Y)).ToList();
        var curves = new List<Kernel.Curves.ICurve3>();
        for (int i = 0; i < pts.Count; i++) curves.Add(new Kernel.Curves.LineCurve3(pts[i], pts[(i + 1) % pts.Count]));
        var body = Kernel.Ops.RevolveOp.Build(Kernel.Ops.ProfileBuilder.Build(plane, curves), new Kernel.Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
        return Place(body, at, dir);
    }

    private static TSolid Cone(Vector3d at, Vector3d dir, double r0, double r1, double h)
    {
        var body = PrimitiveOp.Cone(Vector3d.Zero, r0, r1, h);
        return Place(body, at, dir);
    }

    /// <summary>Перенос заготовки из «вдоль Z от начала» в «вдоль dir от точки».</summary>
    private static TSolid Place(TSolid body, Vector3d at, Vector3d dir)
    {
        Vector3d z = Vector3d.UnitZ;
        Vector3d axis = Vector3d.Cross(z, dir);
        double dot = Math.Clamp(Vector3d.Dot(z, dir), -1, 1);

        Vector3d Rot(Vector3d v)
        {
            if (axis.LengthSquared < 1e-18) return dot > 0 ? v : new Vector3d(v.X, -v.Y, -v.Z);
            Vector3d k = axis.Normalized();
            double a = Math.Acos(dot);
            return v * Math.Cos(a) + Vector3d.Cross(k, v) * Math.Sin(a) + k * Vector3d.Dot(k, v) * (1 - Math.Cos(a));
        }

        var x = new TransformOp.Xform(p => Rot(p) + at, Rot, false);
        return TransformOp.Copy(body, x);
    }

    static readonly bool HoleMissOff = Environment.GetEnvironmentVariable("P3D_HOLEMISS") == "0";

    private static double Extent(List<TSolid> bodies)
    {
        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        foreach (var b in bodies)
            foreach (var v in b.Vertices) { lo = Vector3d.ComponentMin(lo, v.P); hi = Vector3d.ComponentMax(hi, v.P); }
        return hi.X < lo.X ? 100 : (hi - lo).Length;
    }
}

/// <summary>Решение по маршруту построения отверстия (поставка 4, контракт применимости).</summary>
public enum HoleRouteVerdict { NotApplicable, Declined, Invalid, Accepted }

/// <summary>Маршрут, решение и причина (для неприменимого, отказавшегося и негодного).</summary>
public readonly record struct HoleRouteDecision(string Route, HoleRouteVerdict Verdict, string? Reason)
{
    public override string ToString() => Reason == null ? $"{Route}: {Verdict}" : $"{Route}: {Verdict} ({Reason})";
}
