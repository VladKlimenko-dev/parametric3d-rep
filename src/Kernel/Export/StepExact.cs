using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ВЫВОД ТОЧНОГО STEP. Гранёный вывод теряет кривизну: цилиндр уходит многогранником, и чужая
/// система получает не деталь, а её приближение. Здесь пишутся сами поверхности и кривые.
///
/// Пишутся плоскости и цилиндры (выдавливание дуги), рёбра — отрезки и дуги. Поверхность, для
/// которой точной записи пока нет, приводит к отказу с названием: молча выдать неверный файл
/// хуже, чем не выдать никакого.
/// </summary>
public static class StepExact
{
    /// <summary>Мутация P3D_STEPBAND=0: лента по формуле и линии касания не пишутся (честный отказ точной записи, прежде).</summary>
    static readonly bool StepBandOff = Environment.GetEnvironmentVariable("P3D_STEPBAND") == "0";

    static readonly bool StepCylOff = Environment.GetEnvironmentVariable("P3D_STEPCYL") == "0";
    /// <summary>Мутация g.14 (P3D_STEPNORMAL=0): признак стороны квадрик — по нормали нашей параметризации, как прежде.</summary>
    /// <summary>Мутация g.14 (P3D_STEPCONE=0): прежняя запись конуса (радиус при z = 0, знаковый полуугол).</summary>
    static readonly bool ConeOld = Environment.GetEnvironmentVariable("P3D_STEPCONE") == "0";
    /// <summary>Мутация g.14 (P3D_STEPVOIDS=0): все грани тела — в один CLOSED_SHELL, как прежде.</summary>
    static readonly bool VoidsOld = Environment.GetEnvironmentVariable("P3D_STEPVOIDS") == "0";
    /// <summary>Мутация g.02 (P3D_STEPNORM=0): тело пишется без нормализации контуров, как прежде.</summary>
    static readonly bool NormalizeOff = Environment.GetEnvironmentVariable("P3D_STEPNORM") == "0";
    /// <summary>Мутация g.02 (P3D_STEPTORUS=0): целый тор пишется прежним контуром.</summary>
    static readonly bool TorusSeamOff = Environment.GetEnvironmentVariable("P3D_STEPTORUS") == "0";
    static readonly bool StepNormalOff = Environment.GetEnvironmentVariable("P3D_STEPNORMAL") == "0";

    /// <summary>G4: писать рёбра точными пересечениями поверхностей (на копии тела).</summary>
    private static bool _gExactEdgesOnExport = true;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.ExactEdgesOnExport), если он задан; иначе общее по умолчанию.</summary>
    public static bool ExactEdgesOnExport
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactEdgesOnExport : _gExactEdgesOnExport;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactEdgesOnExport, value, "StepExact.ExactEdgesOnExport");
    }

    /// <summary>
    /// Смотрит ли поверхность туда же, куда наружная сторона грани: сравниваются нормаль
    /// поверхности и нормаль треугольников грани в одной точке.
    /// </summary>
    private static bool SameSide(TFace f, ISurface3? src, Func<Vector3d, Vector3d>? stepNormal = null)
    {
        if (src == null && stepNormal == null) return true;
        var mb = new Tess.MeshBuilder();
        try { f.Surface.TessellateFace(f, mb); } catch { return !f.Reversed; }
        var md = new Tess.MeshData();
        mb.FillInto(md);
        var pts = mb.PositionsD;
        if (md.Indices.Length < 3) return !f.Reversed;

        Vector3d best = Vector3d.Zero; double bestArea = -1; Vector3d at = Vector3d.Zero;
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3d a = pts[md.Indices[i]], b = pts[md.Indices[i + 1]], c = pts[md.Indices[i + 2]];
            Vector3d n = Vector3d.Cross(b - a, c - a);
            double area = n.Length / 2;
            if (area > bestArea) { bestArea = area; best = n; at = (a + b + c) / 3; }
        }
        if (bestArea <= 0) return !f.Reversed;

        // g.14: у квадрик STEP нормаль задана стандартом (наружу от оси, центра, окружности тора), а не параметризацией
        // нашей поверхности: сравнение с нормалью нашей поверхности давало обратный признак у тора, шара, цилиндра.
        Vector3d sn = stepNormal != null && !StepNormalOff ? stepNormal(at) : src!.NormalAt(src.ProjectToUV(at));
        if (sn.LengthSquared < 1e-24) return !f.Reversed;
        return Vector3d.Dot(best.Normalized(), sn.Normalized()) >= 0;
    }

    public static bool Write(TextWriter w, IEnumerable<TSolid> bodies, string name, List<string>? warnings = null) =>
        Write(w, bodies, name, warnings, null);

    /// <summary>KRN-09 (g.11): с именами тел (по одному на тело, в порядке тел) — в имя MANIFOLD_SOLID_BREP / BREP_WITH_VOIDS.</summary>
    public static bool Write(TextWriter w, IEnumerable<TSolid> bodies, string name, List<string>? warnings, IReadOnlyList<string>? bodyNames)
    {
        // G4: в файл — точные рёбра (пересечения поверхностей) и вершины; на копии тела, чтобы не
        // трогать тело модели (его рёбра опираются на точный многогранник следующих операций).
        if (ExactEdgesOnExport)
            bodies = bodies.Select(b =>
            {
                // Поставка 2: СТРУКТУРНАЯ копия B-rep (TopologyClone.Deep). Прежде — TransformOp.Copy со сдвигом на
                // ноль, а он пересобирает тело из треугольников видимой сетки: в файл шло другое, многогранное тело.
                var copy = Topology.TopologyClone.Deep(b);
                var rep = Geometry.ExactEdges.Apply(copy);
                if (rep.Replaced > 0 || rep.VerticesSnapped > 0)
                {
                    if (Topology.SolidValidator.ValidateBRep(copy).IsValid) { warnings?.Add($"STEP: {rep.Replaced} edge(s) written as exact intersection curves."); return copy; }
                }
                return b;
            }).ToList();
        // g.02/g.14: ТЕЛО ДО СОГЛАШЕНИЯ О КОНТУРАХ (заготовки PrimitiveOp, тела вращения до нормализации): боковая грань конуса —
        // ОДИН контур из двух окружностей; так и уходило в файл — EDGE_LOOP из двух несвязных колец, другое ядро читало
        // негодное тело с отрицательным объёмом. Теперь такие тела пишутся по нормализованной копии (кольцо — свой контур).
        if (!NormalizeOff)
            bodies = bodies.Select(b =>
            {
                if (b.TopologyConvention >= Topology.TopologyConventions.Version) return b;
                var copy = Topology.TopologyClone.Deep(b);
                Topology.TopologyConventions.Normalize(copy);
                return copy;
            }).ToList();
        // K02: уровень гарантий тела доходит до обменного файла. Приближённое тело пишется (это
        // решение вызывающего), но с предупреждением и пометкой в описании файла.
        bool approximate = bodies.Any(b => b.Guarantee == GuaranteeLevel.Approximate);
        if (approximate) warnings?.Add("STEP: an approximate body is exported (built by an approximate route).");
        int id = 0;
        var lines = new List<string>();
        int E(string s) { lines.Add($"#{++id}={s};"); return id; }

        string N(double v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture)
                              is { } t && t.Contains('.') ? t : v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ".";

        var pointOf = new Dictionary<(long, long, long), int>();
        int Point(Vector3d p)
        {
            var k = ((long)Math.Round(p.X / Tol.Weld), (long)Math.Round(p.Y / Tol.Weld), (long)Math.Round(p.Z / Tol.Weld));
            if (pointOf.TryGetValue(k, out int at)) return at;
            return pointOf[k] = E($"CARTESIAN_POINT('',({N(p.X)},{N(p.Y)},{N(p.Z)}))");
        }

        int Dir(Vector3d d) => E($"DIRECTION('',({N(d.X)},{N(d.Y)},{N(d.Z)}))");
        int Axis(Frame3 f) => E($"AXIS2_PLACEMENT_3D('',#{Point(f.Origin)},#{Dir(f.W)},#{Dir(f.U)})");

        var vertexOf = new Dictionary<TVertex, int>();
        int Vertex(TVertex v)
        {
            if (vertexOf.TryGetValue(v, out int at)) return at;
            return vertexOf[v] = E($"VERTEX_POINT('',#{Point(v.P)})");
        }

        // Совпадает ли направление ребра (V0 → V1) с направлением параметра кривой STEP
        // (против часовой стрелки вокруг оси рамки): учитываются знак размаха и то, с какого
        // конца начинается наша кривая.
        static bool SameSense(double sweep, TEdge e)
        {
            var c = e.Curve;
            bool curveFromV0 = ReferenceEquals(e.V0, e.V1) || (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared;
            return (sweep >= 0) == curveFromV0;
        }

        var edgeOf = new Dictionary<TEdge, int>();
        int? Edge(TEdge e)
        {
            if (edgeOf.TryGetValue(e, out int at)) return at;
            int geom;
            switch (e.Curve)
            {
                case LineCurve3 l:
                {
                    Vector3d d = (l.B - l.A).Normalized();
                    int vec = E($"VECTOR('',#{Dir(d)},{N(1)})");
                    geom = E($"LINE('',#{Point(l.A)},#{vec})");
                    break;
                }
                case ArcCurve3 a:
                    geom = E($"CIRCLE('',#{Axis(a.Frame)},{N(a.Radius)})");
                    // K08: направление ребра против направления параметра окружности (против часовой
                    // вокруг W). Прежде всегда писалось .T.: дуга с отрицательным размахом или кривая,
                    // идущая от V1 к V0, выходила в файле обратной — импорт брал дополнительную дугу.
                    return edgeOf[e] = E($"EDGE_CURVE('',#{Vertex(e.V0)},#{Vertex(e.V1)},#{geom},{(SameSense(a.Sweep, e) ? ".T." : ".F.")})");
                case EllipseCurve3 el:
                {
                    // Эллипс (сечение цилиндра наклонной плоскостью, K08): полуось 1 — вдоль U рамки.
                    geom = E($"ELLIPSE('',#{Axis(el.Frame)},{N(el.RU)},{N(el.RV)})");
                    return edgeOf[e] = E($"EDGE_CURVE('',#{Vertex(e.V0)},#{Vertex(e.V1)},#{geom},{(SameSense(el.Sweep, e) ? ".T." : ".F.")})");
                }
                case SplineCurve3 sp:
                {
                    // Сплайн пишется опорными точками и узловым вектором: без него любой эскиз
                    // со свободной кривой уходил бы в файл ломаной.
                    var ids = sp.ControlPoints.Select(Point).ToList();
                    var knots = new List<double>();
                    var mult = new List<int>();
                    foreach (double k in sp.Knots)
                    {
                        if (knots.Count > 0 && Math.Abs(knots[^1] - k) < 1e-12) mult[^1]++;
                        else { knots.Add(k); mult.Add(1); }
                    }
                    geom = E($"B_SPLINE_CURVE_WITH_KNOTS('',{sp.Degree}," +
                             $"({string.Join(",", ids.Select(x => "#" + x))}),.UNSPECIFIED.,.F.,.F.," +
                             $"({string.Join(",", mult)}),({string.Join(",", knots.Select(N))}),.UNSPECIFIED.)");
                    break;
                }

                // KRN-06: линия касания катящегося шара (кривая по формуле) — так же: кубический B-сплайн с проверенным отходом
                case IntersectionCurve3 or ProcCurve3 when e.Curve is IntersectionCurve3 || !StepBandOff:
                {
                    // K10. Кривая пересечения пишется кубическим B-сплайном через её точки с
                    // ПРОВЕРЕННЫМ отклонением (≤ 1e-4 мм). Прежде — ломаной из 64 отрезков.
                    var fit = SplineFit.Fit(e.Curve, ToleranceContext.Current.ExportFit);   // KRN-02: бюджет экспорта
                    if (fit != null)
                    {
                        var (sp2, dev) = fit.Value;
                        var ids2 = sp2.ControlPoints.Select(Point).ToList();
                        var knots2 = new List<double>(); var mult2 = new List<int>();
                        foreach (double k in sp2.Knots)
                        {
                            if (knots2.Count > 0 && Math.Abs(knots2[^1] - k) < 1e-12) mult2[^1]++;
                            else { knots2.Add(k); mult2.Add(1); }
                        }
                        geom = E($"B_SPLINE_CURVE_WITH_KNOTS('',{sp2.Degree},({string.Join(",", ids2.Select(x => "#" + x))}),.UNSPECIFIED.,.F.,.F.,({string.Join(",", mult2)}),({string.Join(",", knots2.Select(N))}),.UNSPECIFIED.)");
                        warnings?.Add($"STEP: an intersection curve is written as a cubic B-spline ({sp2.ControlPoints.Count} poles, deviation {dev:E1} mm).");
                        bool sense = ReferenceEquals(e.V0, e.V1) || (e.Curve.PointAt(e.Curve.T0) - e.V0.P).LengthSquared <= (e.Curve.PointAt(e.Curve.T1) - e.V0.P).LengthSquared;
                        return edgeOf[e] = E($"EDGE_CURVE('',#{Vertex(e.V0)},#{Vertex(e.V1)},#{geom},{(sense ? ".T." : ".F.")})");
                    }
                    var pts = new List<Vector3d>();
                    for (int i = 0; i <= 64; i++) pts.Add(e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 64));
                    if (!ReferenceEquals(e.V0, e.V1) && (pts[0] - e.V0.P).LengthSquared > (pts[^1] - e.V0.P).LengthSquared) pts.Reverse();
                    pts[0] = e.V0.P; pts[^1] = e.V1.P;
                    geom = E($"POLYLINE('',({string.Join(",", pts.Select(q => "#" + Point(q)))}))");
                    warnings?.Add("STEP: an intersection curve is written as a 64-segment polyline (approximate).");
                    break;
                }
                default:
                    warnings?.Add($"STEP: edge curve {e.Curve.GetType().Name} is not written exactly yet.");
                    return null;
            }
            return edgeOf[e] = E($"EDGE_CURVE('',#{Vertex(e.V0)},#{Vertex(e.V1)},#{geom},.T.)");
        }

        var shells = new List<int>();
        var solidIds = new List<int>();
        int bodyIndex = -1;
        foreach (var body in bodies)
        {
          bodyIndex++;
          // g.14 (KRN-03 инвариант оболочек): оболочка 0 — наружная, прочие — полости. Полость пишется BREP_WITH_VOIDS:
          // CLOSED_SHELL полости ориентирован как самостоятельное тело (нормали — из полости в материал; у нас — наоборот,
          // поэтому сторона грани и направление контуров обращены), и входит как ORIENTED_CLOSED_SHELL(.F.). Прежде все грани
          // тела шли в ОДИН CLOSED_SHELL из двух несвязных частей (не оболочка по стандарту).
          var shellSets = VoidsOld || body.Shells.Count(sh => sh.Faces.Count > 0) < 2
              ? new List<(List<TFace> Faces, bool Void)> { (body.Faces.ToList(), false) }
              : body.Shells.Where(sh => sh.Faces.Count > 0).Select((sh, k) => (sh.Faces.ToList(), k > 0)).ToList();
          int outerShell = -1; var voidShells = new List<int>();
          foreach (var (shellFaces, isVoid) in shellSets)
          {
            var faceIds = new List<int>();
            foreach (var f in shellFaces)
            {
                var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
                // Записанная поверхность может отличаться от исходной (кольцо вращения пишется
                // плоскостью), а признак стороны надо считать именно по ЗАПИСАННОЙ.
                ISurface3? written = src;
                Func<Vector3d, Vector3d>? stepNormal = null;     // нормаль записанной квадрики по стандарту STEP
                static Vector3d Radial(Vector3d o, Vector3d w, Vector3d p) { var d = p - o; return d - w * Vector3d.Dot(d, w); }
                int surf;
                switch (src)
                {
                    case PlaneSurface pl:
                        surf = E($"PLANE('',#{Axis(pl.F)})");
                        break;
                    // Выдавливание ОТРЕЗКА — это плоскость: система координат строится по
                    // направлению отрезка и направлению вытяжки.
                    case ExtrudedSurface exl when exl.Base is LineCurve3 ln:
                    {
                        Vector3d u = (ln.B - ln.A).Normalized();
                        Vector3d v = exl.Dir.Normalized();
                        Vector3d n = Vector3d.Cross(u, v);
                        if (n.LengthSquared < 1e-18)
                        {
                            warnings?.Add("STEP: degenerate extruded face skipped.");
                            continue;
                        }
                        var fpl = new Frame3(ln.A, u, Vector3d.Cross(n.Normalized(), u));
                        written = new PlaneSurface(fpl);
                        surf = E($"PLANE('',#{Axis(fpl)})");
                        break;
                    }

                    // Выдавливание СПЛАЙНА (и любой другой кривой) — это протяжка по прямой.
                    case ExtrudedSurface exs when exs.Base is SplineCurve3 sps:
                    {
                        var ids2 = sps.ControlPoints.Select(Point).ToList();
                        var knots2 = new List<double>();
                        var mult2 = new List<int>();
                        foreach (double k in sps.Knots)
                        {
                            if (knots2.Count > 0 && Math.Abs(knots2[^1] - k) < 1e-12) mult2[^1]++;
                            else { knots2.Add(k); mult2.Add(1); }
                        }
                        int curveId = E($"B_SPLINE_CURVE_WITH_KNOTS('',{sps.Degree}," +
                                        $"({string.Join(",", ids2.Select(x => "#" + x))}),.UNSPECIFIED.,.F.,.F.," +
                                        $"({string.Join(",", mult2)}),({string.Join(",", knots2.Select(N))}),.UNSPECIFIED.)");
                        Vector3d dd = exs.Dir;
                        int vec2 = E($"VECTOR('',#{Dir(dd.Normalized())},{N(dd.Length)})");
                        surf = E($"SURFACE_OF_LINEAR_EXTRUSION('',#{curveId},#{vec2})");
                        break;
                    }

                    // KRN-06: точный цилиндр (лента прямого скругления). Мутация P3D_STEPCYL=0.
                    case CylinderSurface cyl when !StepCylOff:
                    {
                        surf = E($"CYLINDRICAL_SURFACE('',#{Axis(cyl.Axis)},{N(cyl.Radius)})");
                        var o = cyl.Axis.Origin; var wa = cyl.Axis.W.Normalized();
                        stepNormal = p => Radial(o, wa, p);
                        break;
                    }
                    case ExtrudedSurface ex when ex.Base is ArcCurve3 arc:
                    {
                        surf = E($"CYLINDRICAL_SURFACE('',#{Axis(arc.Frame)},{N(arc.Radius)})");
                        var o = arc.Frame.Origin; var wa = arc.Frame.W.Normalized();
                        stepNormal = p => Radial(o, wa, p);
                        break;
                    }
                    // ВРАЩЕНИЕ: шар и тор распознаются по образующей. Шар — полукруг, чей центр
                    // лежит на оси; тор — полная окружность, отнесённая от оси. Остальные тела
                    // вращения точной записи пока не имеют, и это говорится вслух.
                    // КОНУС: вращение ПРЯМОЙ образующей. Радиус берётся у начала системы
                    // координат, полуугол — по наклону образующей к оси.
                    case RevolvedSurface rc when rc.Base is LineCurve3 gl:
                    {
                        Vector3d w2 = rc.Axis.W.Normalized();
                        Vector3d a0 = gl.A - rc.Axis.Origin, b0 = gl.B - rc.Axis.Origin;
                        double za = Vector3d.Dot(a0, w2), zb = Vector3d.Dot(b0, w2);
                        double ra = (a0 - w2 * za).Length, rb = (b0 - w2 * zb).Length;
                        // Образующая поперёк оси — это ПЛОСКОЕ КОЛЬЦО (крышка тела вращения).
                        if (Math.Abs(zb - za) < Tol.Weld)
                        {
                            var fp = new Frame3(rc.Axis.Origin + w2 * za, rc.Axis.U, rc.Axis.V);
                            written = new PlaneSurface(fp);
                            surf = E($"PLANE('',#{Axis(fp)})");
                            break;
                        }
                        double ang = Math.Atan((rb - ra) / (zb - za));
                        double r0 = ra - (rb - ra) / (zb - za) * za;     // радиус при z = 0
                        if (Math.Abs(ang) < 1e-9)
                        {
                            // Образующая параллельна оси — это цилиндр.
                            var f2 = new Frame3(rc.Axis.Origin, rc.Axis.U, rc.Axis.V);
                            surf = E($"CYLINDRICAL_SURFACE('',#{Axis(f2)},{N(r0)})");
                            { var o2 = rc.Axis.Origin; stepNormal = p => Radial(o2, w2, p); }
                            break;
                        }
                        if (ConeOld) { surf = E($"CONICAL_SURFACE('',#{Axis(rc.Axis)},{N(Math.Max(r0, 0))},{N(ang)})"); break; }
                        // g.14: CONICAL_SURFACE — радиус > 0 в начале системы координат и полуугол в (0, π/2): конус
                        // раскрывается ВДОЛЬ оси. Прежде — радиус при z = 0 (мог быть отрицательным и обрезался в 0) и
                        // отрицательный полуугол у сужающегося конуса: OpenCascade читал другой конус (объём ×1.5).
                        {
                            bool useA = ra >= rb; double zr = useA ? za : zb, rr = useA ? ra : rb;
                            var U = rc.Axis.U.Normalized(); var Wd = w2;
                            if ((rb - ra) / (zb - za) < 0) Wd = -w2;           // ось — в сторону роста радиуса
                            var Vd = Vector3d.Cross(Wd, U);
                            var fc = new Frame3(rc.Axis.Origin + w2 * zr, U, Vd);
                            surf = E($"CONICAL_SURFACE('',#{Axis(fc)},{N(rr)},{N(Math.Abs(ang))})");
                            var o3 = rc.Axis.Origin; stepNormal = p => Radial(o3, w2, p);
                        }
                        break;
                    }

                    case RevolvedSurface rev when rev.Base is ArcCurve3 gen:
                    {
                        Vector3d axisO = rev.Axis.Origin, axisW = rev.Axis.W.Normalized();
                        Vector3d c = gen.Frame.Origin;
                        double off = (c - axisO - axisW * Vector3d.Dot(c - axisO, axisW)).Length;
                        bool full = Math.Abs(Math.Abs(gen.Sweep) - 2 * Math.PI) < 1e-9;
                        bool half = Math.Abs(Math.Abs(gen.Sweep) - Math.PI) < 1e-9;

                        // K10: поверхность — сфера или тор по образующей ОКРУЖНОСТИ, какой бы ни была
                        // её дуга: грань обрезается контурами. Прежде писались только полукруг (шар) и
                        // полный круг (тор), и скругление кромки отверстия (четверть тора) срывало
                        // запись всего тела.
                        _ = half; _ = full;
                        Vector3d centre = axisO + axisW * Vector3d.Dot(c - axisO, axisW);
                        if (off < Tol.Weld)
                        {
                            surf = E($"SPHERICAL_SURFACE('',#{Axis(new Frame3(c, rev.Axis.U, rev.Axis.V))},{N(gen.Radius)})");
                            stepNormal = p => p - c;
                            break;
                        }
                        if (off > gen.Radius + Tol.Weld)
                        {
                            surf = E($"TOROIDAL_SURFACE('',#{Axis(new Frame3(centre, rev.Axis.U, rev.Axis.V))},{N(off)},{N(gen.Radius)})");
                            stepNormal = p => { var rad = Radial(centre, axisW, p); var ring = centre + rad.Normalized() * off; return p - ring; };
                            break;
                        }
                        warnings?.Add("STEP: this surface of revolution is not written exactly yet.");
                        return false;
                    }

                    // ПОВЕРХНОСТЬ ПО СЕТКЕ ОПОРНЫХ ТОЧЕК. Свободные формы записываются как есть:
                    // сетка, степени и узловые векторы. Без этого их пришлось бы гранить, и
                    // чужая система получила бы многогранник вместо поверхности.
                    // KRN-06: ЛЕНТА КАТЯЩЕГОСЯ ШАРА (поверхность по формуле сечения-дуги) — рациональным B-сплайном на промежутке u
                    // грани (дуга по v точно, по u — интерполяция сечений до 1/10 бюджета экспорта). Мутация P3D_STEPBAND=0.
                    case SectionSurface band when !StepBandOff && band.IsProcedural && Topology.AnalyticVolume.BandRange(f, band) is { } brg
                                                  && BandNurbs.Fit(band, brg.Ua, brg.Ub, ToleranceContext.Current.ExportFit / (Environment.GetEnvironmentVariable("P3D_BANDDIV") is { } bd ? double.Parse(bd) : 10)) is { } bn:
                    {
                        var ns = bn.S;
                        var rows = new List<string>(); var wrows = new List<string>();
                        for (int i = 0; i < ns.CountU; i++)
                        {
                            var cols = new List<string>(); var wc = new List<string>();
                            for (int j = 0; j < ns.CountV; j++) { cols.Add("#" + Point(ns.ControlPoint(i, j))); wc.Add(N(ns.Weight(i, j))); }
                            rows.Add("(" + string.Join(",", cols) + ")"); wrows.Add("(" + string.Join(",", wc) + ")");
                        }
                        static (List<int> Mult, List<double> Vals) PackK(IReadOnlyList<double> knots)
                        {
                            var mult = new List<int>(); var vals = new List<double>();
                            foreach (double k in knots) { if (vals.Count > 0 && Math.Abs(vals[^1] - k) < 1e-12) mult[^1]++; else { vals.Add(k); mult.Add(1); } }
                            return (mult, vals);
                        }
                        var (mu, vu) = PackK(ns.KnotsU); var (mv, vv) = PackK(ns.KnotsV);
                        surf = E($"(BOUNDED_SURFACE()B_SPLINE_SURFACE({ns.DegreeU},{ns.DegreeV},({string.Join(",", rows)}),.UNSPECIFIED.,.F.,.F.,.F.)" +
                                 $"B_SPLINE_SURFACE_WITH_KNOTS(({string.Join(",", mu)}),({string.Join(",", mv)}),({string.Join(",", vu.Select(N))}),({string.Join(",", vv.Select(N))}),.UNSPECIFIED.)" +
                                 $"GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_SURFACE(({string.Join(",", wrows)}))REPRESENTATION_ITEM('')SURFACE())");
                        warnings?.Add($"STEP: a rolling-ball blend face is written as a rational B-spline ({ns.CountU}×{ns.CountV} poles, deviation {bn.Deviation:E1} mm).");
                        written = band;
                        break;
                    }

                    case NurbsSurface ns:
                    {
                        var rows = new List<string>();
                        for (int i = 0; i < ns.CountU; i++)
                        {
                            var cols = new List<string>();
                            for (int j = 0; j < ns.CountV; j++) cols.Add("#" + Point(ns.ControlPoint(i, j)));
                            rows.Add("(" + string.Join(",", cols) + ")");
                        }

                        static (List<int> Mult, List<double> Vals) Pack(IReadOnlyList<double> knots)
                        {
                            var mult = new List<int>();
                            var vals = new List<double>();
                            foreach (double k in knots)
                            {
                                if (vals.Count > 0 && Math.Abs(vals[^1] - k) < 1e-12) mult[^1]++;
                                else { vals.Add(k); mult.Add(1); }
                            }
                            return (mult, vals);
                        }

                        var (mu, vu) = Pack(ns.KnotsU);
                        var (mv, vv) = Pack(ns.KnotsV);

                        surf = E($"B_SPLINE_SURFACE_WITH_KNOTS('',{ns.DegreeU},{ns.DegreeV}," +
                                 $"({string.Join(",", rows)}),.UNSPECIFIED.,.F.,.F.,.F.," +
                                 $"({string.Join(",", mu)}),({string.Join(",", mv)})," +
                                 $"({string.Join(",", vu.Select(N))}),({string.Join(",", vv.Select(N))}),.UNSPECIFIED.)");
                        break;
                    }

                    default:
                        warnings?.Add($"STEP: surface {src?.GetType().Name ?? "unknown"} is not written exactly yet.");
                        return false;
                }

                var bounds = new List<int>();
                // g.02: ЦЕЛЫЙ ТОР (контуры — только швы или их нет): в файле — два шва (меридиан и экватор) через одну вершину,
                // контур «меридиан, экватор, меридиан назад, экватор назад». Прежде писался контур из одного меридиана дважды
                // (или ничего) — другое ядро читало негодную грань с нулевым объёмом.
                var faceCes = f.Loops.SelectMany(l => l.CoEdges).ToList();
                bool seamsOnly = faceCes.All(ce => faceCes.Count(c2 => ReferenceEquals(c2.Edge, ce.Edge)) == 2);
                if (!TorusSeamOff && seamsOnly && src is RevolvedSurface rvt && rvt.Base is ArcCurve3 tg && tg.IsClosed && Math.Abs(Math.Abs(rvt.Sweep) - 2 * Math.PI) < 1e-9)
                {
                    var wt = rvt.Axis.W.Normalized(); var ct = tg.Frame.Origin;
                    var cen = rvt.Axis.Origin + wt * Vector3d.Dot(ct - rvt.Axis.Origin, wt);
                    var radial = ct - cen; double ring = radial.Length;
                    if (ring > tg.Radius + Tol.Weld)
                    {
                        var ru = radial / ring; var rv = Vector3d.Cross(wt, ru);
                        var P = cen + ru * (ring + tg.Radius);
                        int vt = E($"VERTEX_POINT('',#{Point(P)})");
                        int merid = E($"CIRCLE('',#{Axis(new Frame3(ct, ru, wt))},{N(tg.Radius)})");      // в плоскости (ru, w), ось −rv
                        int equat = E($"CIRCLE('',#{Axis(new Frame3(cen, ru, rv))},{N(ring + tg.Radius)})"); // в плоскости (ru, rv), ось w
                        int em = E($"EDGE_CURVE('',#{vt},#{vt},#{merid},.T.)");
                        int eq = E($"EDGE_CURVE('',#{vt},#{vt},#{equat},.T.)");
                        int el3 = E($"EDGE_LOOP('',(#{E($"ORIENTED_EDGE('',*,*,#{em},.T.)")},#{E($"ORIENTED_EDGE('',*,*,#{eq},.T.)")},#{E($"ORIENTED_EDGE('',*,*,#{em},.F.)")},#{E($"ORIENTED_EDGE('',*,*,#{eq},.F.)")}))");
                        bounds.Add(E($"FACE_OUTER_BOUND('',#{el3},{(isVoid ? ".F." : ".T.")})"));
                        goto boundsDone;
                    }
                }
                foreach (var loop in f.Loops)
                {
                    var oriented = new List<int>();
                    foreach (var ce in loop.CoEdges)
                    {
                        int? ec = Edge(ce.Edge);
                        if (ec == null) return false;
                        oriented.Add(E($"ORIENTED_EDGE('',*,*,#{ec.Value},{(ce.Reversed ? ".F." : ".T.")})"));
                    }
                    if (oriented.Count == 0) continue;
                    int el = E($"EDGE_LOOP('',({string.Join(",", oriented.Select(x => "#" + x))}))");
                    bounds.Add(E($"{(loop.IsOuter ? "FACE_OUTER_BOUND" : "FACE_BOUND")}('',#{el},{(isVoid ? ".F." : ".T.")})"));
                }
                // ЗАМКНУТАЯ ГРАНЬ БЕЗ ГРАНИЦЫ. У полного шара или тела вращения контура нет, и
                // грань молча пропускалась — файл выходил пустым. Такая грань описывается ШВОМ:
                // образующая записывается ребром и проходится дважды в разные стороны.
                if (bounds.Count == 0 && src is RevolvedSurface rv2 && rv2.Base is ArcCurve3 seam)
                {
                    Vector3d s0 = seam.PointAt(seam.T0), s1 = seam.PointAt(seam.T1);
                    int vs0 = E($"VERTEX_POINT('',#{Point(s0)})");
                    int vs1 = E($"VERTEX_POINT('',#{Point(s1)})");
                    int circle = E($"CIRCLE('',#{Axis(seam.Frame)},{N(seam.Radius)})");
                    int ec2 = E($"EDGE_CURVE('',#{vs0},#{vs1},#{circle},.T.)");
                    int oe1 = E($"ORIENTED_EDGE('',*,*,#{ec2},.T.)");
                    int oe2 = E($"ORIENTED_EDGE('',*,*,#{ec2},.F.)");
                    int el2 = E($"EDGE_LOOP('',(#{oe1},#{oe2}))");
                    bounds.Add(E($"FACE_OUTER_BOUND('',#{el2},{(isVoid ? ".F." : ".T.")})"));
                }

                boundsDone:
                if (bounds.Count == 0) continue;

                // ПРИЗНАК СТОРОНЫ — ПО НАСТОЯЩЕЙ НОРМАЛИ ГРАНИ. Записанная поверхность может
                // смотреть не туда, куда грань: крышка тела вращения пишется плоскостью с
                // нормалью вдоль оси, а у нижней крышки наружу смотрит противоположная сторона.
                // Прежде всегда стояло .T., и объём прочитанного конуса выходил 58 864 вместо
                // 82 467.
                bool same = SameSide(f, written, stepNormal) ^ isVoid;
                faceIds.Add(E($"ADVANCED_FACE('',({string.Join(",", bounds.Select(x => "#" + x))}),#{surf},{(same ? ".T." : ".F.")})"));
            }

            if (faceIds.Count == 0) continue;
            int shId = E($"CLOSED_SHELL('',({string.Join(",", faceIds.Select(x => "#" + x))}))");
            shells.Add(shId);
            if (isVoid) voidShells.Add(E($"ORIENTED_CLOSED_SHELL('',*,#{shId},.F.)")); else outerShell = shId;
          }
          if (outerShell < 0) continue;
          string bname = bodyNames != null && bodyIndex < bodyNames.Count ? bodyNames[bodyIndex].Replace("'", "''") : "";
          solidIds.Add(voidShells.Count == 0 ? E($"MANIFOLD_SOLID_BREP('{bname}',#{outerShell})")
              : E($"BREP_WITH_VOIDS('{bname}',#{outerShell},({string.Join(",", voidShells.Select(x => "#" + x))}))"));
        }

        if (shells.Count == 0) return false;

        // K10. ПРЕДСТАВЛЕНИЕ, ЕДИНИЦЫ И ИЗДЕЛИЕ ПО AP214. Прежде файл содержал только оболочки:
        // без единиц длины чужая система вправе читать числа в своих единицах, а без
        // ADVANCED_BREP_SHAPE_REPRESENTATION и цепочки PRODUCT многие читатели тело не находят.
        name = name.Replace("'", "''");   // апостроф в имени — удвоением, по правилам STEP
        int uLen = E("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
        int uAng = E("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
        int uSol = E("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
        // G3: точность модели в файле — наибольший допуск её рёбер и вершин (если допуски
        // посчитаны), не меньше сетки сварки. Читатель вправе считать совпадающим всё, что ближе.
        double accuracy = Tol.Weld;
        foreach (var b in bodies) foreach (var e in b.Edges) accuracy = Math.Max(accuracy, Math.Max(e.Tolerance, Math.Max(e.V0.Tolerance, e.V1.Tolerance)));
        int unc = E($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE({N(accuracy)}),#{uLen},'distance_accuracy_value','')");
        int ctx = E($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{unc}))GLOBAL_UNIT_ASSIGNED_CONTEXT((#{uLen},#{uAng},#{uSol}))REPRESENTATION_CONTEXT('',''))");
        var solidsIds = solidIds;
        int origin = Axis(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        int rep = E($"ADVANCED_BREP_SHAPE_REPRESENTATION('{name}',(#{origin},{string.Join(",", solidsIds.Select(x => "#" + x))}),#{ctx})");
        int ac = E("APPLICATION_CONTEXT('automotive design')");
        E($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#{ac})");
        int pc = E($"PRODUCT_CONTEXT('',#{ac},'mechanical')");
        int prod = E($"PRODUCT('{name}','{name}','',(#{pc}))");
        int pdf = E($"PRODUCT_DEFINITION_FORMATION('','',#{prod})");
        int pdc = E($"PRODUCT_DEFINITION_CONTEXT('part definition',#{ac},'design')");
        int pd = E($"PRODUCT_DEFINITION('design','',#{pdf},#{pdc})");
        int pds = E($"PRODUCT_DEFINITION_SHAPE('','',#{pd})");
        E($"SHAPE_DEFINITION_REPRESENTATION(#{pds},#{rep})");

        w.WriteLine("ISO-10303-21;");
        w.WriteLine("HEADER;");
        w.WriteLine(approximate ? $"FILE_DESCRIPTION(('approximate brep'),'2;1');" : $"FILE_DESCRIPTION(('exact brep'),'2;1');");
        w.WriteLine($"FILE_NAME('{name}','',(''),(''),'Parametric3D','','');");
        w.WriteLine("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));");
        w.WriteLine("ENDSEC;");
        w.WriteLine("DATA;");
        foreach (string s in lines) w.WriteLine(s);
        w.WriteLine("ENDSEC;");
        w.WriteLine("END-ISO-10303-21;");
        return true;
    }
}
