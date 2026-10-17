using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ОБОЛОЧКА: тело становится полым, со стенкой заданной толщины.
///
/// Первый вариант — для тел, у которых все грани плоские. Полость строится как большой брус,
/// из которого вычитаются полупространства: для каждой грани берётся её плоскость, сдвинутая
/// внутрь на толщину стенки. Грани, выбранные открытыми, не сдвигаются — через них полость
/// выходит наружу, и получается отверстие.
///
/// Чего пока нет: кривых граней (цилиндры, скругления), разной толщины по граням, вогнутых
/// тел со стенкой толще радиуса вогнутости.
/// </summary>
public static class ShellOp
{
    /// <summary>ДИАГНОСТИКА: построение полости.</summary>
    public static Action<string>? ShellDebug;

    /// <param name="open">Точки на гранях, которые остаются открытыми (могут отсутствовать).</param>
    /// <param name="faceThickness">KRN-07 (07.5): своя толщина граней (точка на грани, толщина того же знака).</param>
    public static bool Hollow(List<TSolid> bodies, double thickness, IReadOnlyList<Vector3d>? open,
                              List<string> warnings, IReadOnlyList<(Vector3d At, double T)>? faceThickness = null)
    {
        if (bodies.Count == 0) { warnings.Add("Shell: no body."); return false; }
        bool outward = thickness < 0 && !TweakOff;
        if (!outward && thickness <= Tol.Weld) { warnings.Add("Shell: the wall thickness must be greater than zero."); return false; }
        if (faceThickness != null && faceThickness.Any(x => Math.Sign(x.T) != Math.Sign(thickness) || Math.Abs(x.T) <= Tol.Weld))
        { warnings.Add("Shell: a face thickness must be non-zero and of the same sign as the wall thickness."); return false; }

        TSolid body = bodies[0];
        // u12: точка открытой грани — с сетки показа (центр треугольника): у кривой грани она отходит от поверхности на
        // прогиб хорды, и проверка «точка на грани» (1e-6) её не узнавала — лента скругления не открывалась. Точка
        // переносится на точную поверхность ближайшей грани (если там же и её треугольники). Мутация P3D_SHELLSNAP=0.
        if (open != null && !SnapOff) open = open.Select(q => SnapToFace(body, q)).ToList();
        // KRN-07 (07.5): ТЕЛО С КРИВЫМИ ГРАНЯМИ (шар, тор, скругления, цилиндры с прямыми рёбрами) — полость смещением всех
        // граней без смены топологии (DirectRound.OffsetSolid): закрытые грани внутрь на толщину, открытые (только плоские,
        // все соседи — плоские) — наружу. Мутация P3D_OFFSETSOLID=0.
        string? whyOffset = null;
        // стенка наружу смещением всех граней (DirectRound.OffsetSolid): наружное — все грани наружу (открытые — на месте),
        // внутреннее — тело с открытыми гранями, вынесенными наружу. null — не вышло (причина в whyOffset).
        bool? OutwardByOffset(HashSet<TFace> openOut)
        {
            double t = -thickness;
            var outerO = DirectRound.OffsetSolid(body, f => openOut.Contains(f) ? 0 : t, null, ref whyOffset);
            var innerO = outerO == null ? null : openOut.Count == 0 ? body : DirectRound.OffsetSolid(body, f => openOut.Contains(f) ? t : 0, null, ref whyOffset);
            if (outerO == null || innerO == null) return null;
            foreach (var f in outerO.Faces) if (f.Tag.Length > 0 && !f.Tag.StartsWith("shell:")) f.Tag = "shell:outer:" + EdgeRef.Bare(f.Tag);
            var res = new List<TSolid> { outerO };
            BooleanOp.Subtract(res, innerO, warnings);
            if (res.Count == 0) { warnings.Add("Shell: the outward wall is empty."); return false; }
            bodies.RemoveAt(0); bodies.InsertRange(0, res);
            warnings.Add($"Shell: wall {t:0.###} outward, by offsetting all faces (exact).");
            return true;
        }
        if (!TweakOff && outward && faceThickness == null && !body.Faces.All(f => PlaneOfFace(f) != null || (!CylTweakOff && CylOfFace(f) != null))
            && OffsetOpenFaces(body, open) is { } openOut)
        {
            if (OutwardByOffset(openOut) is { } done) return done;
            warnings.Add($"Shell: an outward wall by offsetting all faces declined: {whyOffset}."); return false;
        }
        if (!TweakOff && !outward && (faceThickness == null || !CurvedThickOff))
        {
            bool planarOnly = body.Faces.All(f => PlaneOfFace(f) != null || (!CylTweakOff && CylOfFace(f) != null));
            if (OffsetOpenFaces(body, open) is { } openFaces && !planarOnly)
            {
                // KRN-07 (07.5): СВОЯ ТОЛЩИНА ГРАНЕЙ У ТЕЛА С КРИВЫМИ ГРАНЯМИ — каждая грань смещается на свою толщину (точка на
                // грани); касательные соседи с разной толщиной теряют касание — смещение без смены топологии честно
                // отказывает. Мутация P3D_SHELLCURVEDTHICK=0 — своя толщина только у тел из плоскостей (прежде).
                Dictionary<TFace, double>? own = null;
                if (faceThickness != null)
                {
                    own = new Dictionary<TFace, double>();
                    foreach (var (at, tt) in faceThickness)
                    {
                        var hit = body.Faces.FirstOrDefault(f => OnAnyFace(f, at));
                        if (hit == null) { warnings.Add("Shell: a face-thickness point is not on a face of the body."); return false; }
                        own[hit] = Math.Abs(tt);
                    }
                }
                double TT(TFace f) => own != null && own.TryGetValue(f, out var v) ? v : thickness;
                var cavO = DirectRound.OffsetSolid(body, f => openFaces.Contains(f) ? TT(f) : -TT(f), "shell:inner", ref whyOffset);
                if (cavO != null)
                {
                    BooleanOp.Subtract(bodies, cavO, warnings);
                    warnings.Add($"Shell: wall {thickness:0.###}" + (own != null ? $", {own.Count} face(s) with own thickness" : "") + ", cavity by offsetting all faces (exact).");
                    return true;
                }
                ShellDebug?.Invoke("offset solid declined: " + whyOffset);
                if (own != null) { warnings.Add($"Shell: own face thickness on a body with curved faces — offsetting all faces declined: {whyOffset}."); return false; }
                // KRN-07 (07.5): ОТКРЫТАЯ ГРАНЬ У СОПРЯЖЕНИЯ — грань, касательная к скруглениям, не сдвигается наружу без потери
                // касания. Оболочка собирается прямо (DirectRound.ShellOpenAtBlends): наружные грани без открытой, смещённые
                // внутрь — обращённые, по рёбрам открытой грани — кромки высотой t до образов рёбер (как у распространённых CAD).
                // Мутация P3D_SHELLOPENBLEND=0.
                if (openFaces.Count > 0)
                {
                    string? whyB = null;
                    var shB = DirectRound.ShellOpenAtBlends(body, openFaces, thickness, ref whyB);
                    if (shB != null)
                    {
                        bodies[0] = shB; Interlocked.Increment(ref OpenAtBlendShells);
                        warnings.Add($"Shell: wall {thickness:0.###}, open face(s) at blends — offset faces and lips assembled directly (exact).");
                        return true;
                    }
                    ShellDebug?.Invoke("open face at blends declined: " + whyB);
                }
            }
        }
        // KRN-07 (07.5): тело из плоских граней — полость сдвигом плоскостей (верно и для вогнутых тел); стенка наружу
        // (толщина < 0) и своя толщина граней — только здесь
        if (!TweakOff && body.Faces.All(f => PlaneOfFace(f) != null || (!CylTweakOff && CylOfFace(f) != null)))
        {
            string? why = null;
            double T(TFace f, Geometry.Ssi.Plane pl) => Math.Abs(faceThickness?.FirstOrDefault(x => OnFace(f, pl, x.At)).T is double v && v != 0 ? v : thickness);
            if (!outward)
            {
                var cavT = OffsetBody(body, (f, pl, isOpen) => isOpen ? T(f, pl) : -T(f, pl), open, "shell:inner", ref why, -thickness);
                // цилиндры с прямыми рёбрами (скруглённые вертикальные рёбра) — смещение всех граней (07.5)
                bool byOffset = false;
                if (cavT == null && (faceThickness == null || !CurvedThickOff) && OffsetOpenFaces(body, open) is { } of2)
                {
                    byOffset = true;
                    // своя толщина граней — по точкам (07.5, как в ветви тел с кривыми гранями)
                    var own2 = new Dictionary<TFace, double>();
                    foreach (var (at, tt) in faceThickness ?? Array.Empty<(Vector3d, double)>())
                        if (body.Faces.FirstOrDefault(f => OnAnyFace(f, at)) is { } hit2) own2[hit2] = Math.Abs(tt);
                    double TT2(TFace f) => own2.TryGetValue(f, out var v) ? v : thickness;
                    cavT = DirectRound.OffsetSolid(body, f => of2.Contains(f) ? TT2(f) : -TT2(f), "shell:inner", ref whyOffset);
                    if (cavT == null) ShellDebug?.Invoke("offset solid declined: " + whyOffset + "; plane offset: " + why);
                }
                if (cavT != null)
                {
                    BooleanOp.Subtract(bodies, cavT, warnings);
                    warnings.Add(byOffset ? $"Shell: wall {thickness:0.###}, cavity by offsetting all faces (exact)." : $"Shell: wall {thickness:0.###}, cavity by plane offset (exact).");
                    return true;
                }
            }
            else
            {
                // стенка наружу: наружное тело — грани сдвинуты наружу (открытые — на месте), внутреннее — само тело с
                // открытыми гранями, вынесенными наружу (полость выходит сквозь них)
                var outer = OffsetBody(body, (f, pl, isOpen) => isOpen ? 0 : T(f, pl), open, null, ref why, -thickness);
                var inner = outer == null ? null : open == null || open.Count == 0 ? body : OffsetBody(body, (f, pl, isOpen) => isOpen ? T(f, pl) : 0, open, null, ref why, 0);
                if (outer != null && inner != null)
                {
                    foreach (var f in outer.Faces) if (f.Tag.Length > 0 && !f.Tag.StartsWith("shell:")) f.Tag = "shell:outer:" + EdgeRef.Bare(f.Tag);
                    var res = new List<TSolid> { outer };
                    BooleanOp.Subtract(res, inner, warnings);
                    if (res.Count == 0) { warnings.Add("Shell: the outward wall is empty."); return false; }
                    bodies.RemoveAt(0); bodies.InsertRange(0, res);
                    warnings.Add($"Shell: wall {-thickness:0.###} outward, by plane offset (exact).");
                    return true;
                }
                // u10: плоскости и неполные цилиндры (скругления рёбер) — смещением всех граней, как у тел с кривыми гранями.
                // Прежде — отказ «цилиндр не ограничен полными окружностями»: стенка наружу у куба со скруглением не строилась.
                if (!OutwardCylOff && faceThickness == null && OffsetOpenFaces(body, open) is { } openOut2 && OutwardByOffset(openOut2) is { } done2) return done2;
                warnings.Add($"Shell: an outward wall is built only by plane offset, which declined: {why}" + (whyOffset != null ? $"; offsetting all faces: {whyOffset}" : "") + "."); return false;
            }
            if (!Convex(body)) { warnings.Add($"Shell: a concave body is hollowed only by plane offset, which declined: {why}."); return false; }
        }
        else if (outward || faceThickness != null)
        {
            warnings.Add("Shell: an outward wall and per-face thickness need a body of planar faces.");
            return false;
        }
        else if (!ConvexOff && !Convex(body))
        {
            warnings.Add("Shell: the body is concave; its cavity cannot be built as an intersection of half-spaces.");
            return false;
        }
        // u12: путь пересечения полупространств своей толщины граней не знает — при ней честный отказ (прежде сюда с ней не
        // доходили: весь цилиндр скругления отсекал полость, и путь отказывал сам)
        if (faceThickness != null && faceThickness.Count > 0)
        {
            warnings.Add("Shell: per-face thickness could not be kept by offsetting the faces (tangent neighbours with different thickness).");
            return false;
        }
        var planes = new List<(Vector3d P, Vector3d N, bool Open)>();
        // цилиндр грани: точка оси, ось, радиус и точка на грани (подсказка для стороны)
        var cylinders = new List<(TFace F, Vector3d Centre, Vector3d Axis, double R, Vector3d OnFace)>();

        foreach (TFace f in body.Faces)
        {
            ISurface3? src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (src is ExtrudedSurface cyl && cyl.Base is ArcCurve3 arc)
            {
                Vector3d ax = cyl.Dir.LengthSquared > 1e-18 ? cyl.Dir.Normalized() : arc.Frame.W.Normalized();
                cylinders.Add((f, arc.Frame.Origin, ax, arc.Radius, arc.PointAt(0.5)));
                continue;
            }
            // u10: цилиндр как поверхность (грань скругления ребра) — так же, как цилиндр выдавливания дуги
            if (!CylSurfOff && src is not null && !src.IsPlanar && CylOfFace(f) is { } cs)
            {
                var hint = f.Edges.Where(e => e.Curve != null).Select(e => e.Curve!.PointAt(0.5 * (e.Curve.T0 + e.Curve.T1))).FirstOrDefault();
                cylinders.Add((f, cs.P, cs.Axis.Normalized(), cs.R, hint));
                continue;
            }
            if (src == null || !src.IsPlanar)
            {
                warnings.Add($"Shell: a {src?.GetType().Name ?? "unknown"} face cannot be hollowed yet " +
                             "(planar, cylindrical and spherical faces are supported).");
                return false;
            }

            // Точка и нормаль — из гранения самой грани: плоская грань бывает записана и как
            // плоскость, и как выдавливание отрезка.
            var mb = new Tess.MeshBuilder();
            f.Surface.TessellateFace(f, mb);
            var md = new Tess.MeshData();
            mb.FillInto(md);
            double area = 0; Vector3d acc = Vector3d.Zero, nsum = Vector3d.Zero;
            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            {
                Vector3d x = mb.PositionsD[md.Indices[i]], y = mb.PositionsD[md.Indices[i + 1]], z = mb.PositionsD[md.Indices[i + 2]];
                Vector3d cr = Vector3d.Cross(y - x, z - x);
                double ar = cr.Length / 2;
                acc += (x + y + z) / 3 * ar; nsum += cr; area += ar;
            }
            if (area <= Tol.Weld || nsum.LengthSquared < 1e-18) continue;   // щепка — не влияет на полость
            Vector3d c = acc / area;
            Vector3d n = nsum.Normalized();

            // K05: открыта ли грань — по расстоянию от точки до САМОЙ грани (её треугольников).
            // Прежде — до центра грани в пределах «размера», который у не гранёной грани (тело
            // из выдавливания) считался нулём: открывалась грань, только если точка попадала
            // точно в её центр, и после правки размера оболочка тихо выходила закрытой.
            bool isOpen = open != null && open.Any(q =>
            {
                if (Math.Abs(Vector3d.Dot(q - c, n)) >= Tol.Chord) return false;
                for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                {
                    Vector3d x = mb.PositionsD[md.Indices[i]], y = mb.PositionsD[md.Indices[i + 1]], z = mb.PositionsD[md.Indices[i + 2]];
                    if (InTriangle(q - n * Vector3d.Dot(q - x, n), x, y, z)) return true;
                }
                return false;
            });
            planes.Add((c, n, isOpen));
        }

        // Полость: большой брус минус полупространства снаружи сдвинутых плоскостей.
        var (lo, hi) = Bounds(body);
        double pad = Math.Max(10 * thickness, (hi - lo).Length);
        TSolid cavity = Box(lo - new Vector3d(pad, pad, pad), hi + new Vector3d(pad, pad, pad));

        // Вычитание кладёт результат в список тел, а не меняет переданное тело: после каждой
        // операции полость берётся из списка заново.
        var cav = new List<TSolid> { cavity };
        foreach (var (p, n, isOpen) in planes)
        {
            Vector3d at = isOpen ? p : p - n * thickness;
            TSolid half = HalfSpace(at, n, (hi - lo).Length + 4 * pad);
            BooleanOp.Subtract(cav, half, warnings);
            if (ShellDebug != null && cav.Count > 0)
                ShellDebug($"после плоскости ({n.X:0.##},{n.Y:0.##},{n.Z:0.##}) полость {Math.Abs(Tess.MassProps.SignedVolume(Tess.Tessellator.Tessellate(cav[0]))):0.#}");
            if (cav.Count == 0) { warnings.Add("Shell: the wall is thicker than the body."); return false; }
        }
        // Цилиндрические грани: у выпуклой (материал внутри) полость ограничена цилиндром
        // меньшего радиуса, у вогнутой (стенка отверстия) — большего. Вычитается всё, что вне
        // нужного цилиндра, — для выпуклой это брус минус цилиндр, для вогнутой сам цилиндр.
        // СФЕРИЧЕСКИЕ ГРАНИ — ПРОБОВАЛ, ОТКАТИЛ. Смещение сферы выражается точно (сфера
        // другого радиуса), и ветка по образцу цилиндрической была написана, но полый шар
        // выходил объёмом 112 855 вместо расчётных 39 475: полость не вычиталась. Причина не в
        // смещении, а в том, как у сферы определяется сторона грани и как полость обрезается —
        // разбирать это надо отдельно.

        int openCyl = 0;
        foreach (var (f, centre, axis, radius, hintOnFace) in cylinders)
        {
            Vector3d onFace = hintOnFace;
            Vector3d rel = onFace - centre;
            Vector3d radial = rel - axis * Vector3d.Dot(rel, axis);
            if (radial.LengthSquared < 1e-18) continue;
            // Нормаль — по ближайшему треугольнику самой грани: у гранёной поверхности
            // NormalAt берёт знак по одному запомненному треугольнику и у стенки отверстия
            // выходила наружу — вогнутая грань принималась за выпуклую.
            var mbc = new Tess.MeshBuilder();
            f.Surface.TessellateFace(f, mbc);
            var mdc = new Tess.MeshData();
            mbc.FillInto(mdc);
            Vector3d nOut = Vector3d.Zero;
            double bestD = double.MaxValue;
            for (int i = 0; i + 2 < mdc.Indices.Length; i += 3)
            {
                Vector3d x = mbc.PositionsD[mdc.Indices[i]], y = mbc.PositionsD[mdc.Indices[i + 1]], z = mbc.PositionsD[mdc.Indices[i + 2]];
                Vector3d cr = Vector3d.Cross(y - x, z - x);
                if (cr.LengthSquared < 1e-24) continue;
                double d = ((x + y + z) / 3 - onFace).LengthSquared;
                if (d < bestD) { bestD = d; nOut = cr.Normalized(); onFace = (x + y + z) / 3; }
            }
            if (nOut.LengthSquared < 1e-18) continue;
            rel = onFace - centre;
            radial = rel - axis * Vector3d.Dot(rel, axis);
            if (radial.LengthSquared < 1e-18) continue;
            bool convex = Vector3d.Dot(nOut, radial.Normalized()) > 0;

            bool isOpen = open != null && open.Any(q => Math.Abs((q - centre - axis * Vector3d.Dot(q - centre, axis)).Length - radius) < Tol.Chord && OnAnyFace(f, q));
            // u12 (замечание 8): НЕПОЛНЫЙ ВЫПУКЛЫЙ ЦИЛИНДР (лента скругления) ОГРАНИЧИВАЕТ ПОЛОСТЬ ТОЛЬКО В СВОЁМ СЕКТОРЕ. Прежде
            // весь цилиндр R − t отсекал полость везде (куб 100 со скруглением R20, стенка 2: полость 116 000 вместо 884 000 мм³),
            // а открытая лента скругления не открывалась (точка открытия сверялась только с плоскостями). Сектор — между
            // радиусами крайних точек грани; открытая лента полость не ограничивает (полость выходит сквозь неё, стенки соседних
            // граней кончаются на её поверхности). Мутация P3D_SHELLSECTOR=0.
            (Vector3d D1, Vector3d D2)? sector = !SectorOff && convex ? Sector(f, centre, axis, radial.Normalized()) : null;
            if (isOpen) openCyl++;
            if (isOpen && sector != null) { ShellDebug?.Invoke($"открытая лента R{radius:0.##} — полость сквозь неё"); continue; }
            double r = convex ? radius - (isOpen ? 0 : thickness) : radius + (isOpen ? 0 : thickness);
            // u10: ВЫПУКЛОЕ СКРУГЛЕНИЕ ТОНЬШЕ СТЕНКИ — его ограничение лишнее. Полость — точки не ближе толщины к наружной стороне;
            // у выпуклого ребра со скруглением радиуса R ≤ t эту границу дают сдвинутые плоскости соседних граней (острое
            // ребро полости), а цилиндр «радиуса R − t ≤ 0» ничего не отсекает. Прежде — отказ «стенка толще тела» уже при
            // t = R (куб 100 со скруглением R10: оболочка 10 мм не строилась). Мутация P3D_SHELLTHINROUND=0.
            if (convex && r <= Tol.Weld && !ThinRoundOff) { ShellDebug?.Invoke($"скругление R{radius:0.##} тоньше стенки {thickness:0.##} — не ограничивает полость"); continue; }
            if (r <= Tol.Weld) { warnings.Add("Shell: the wall is thicker than the body."); return false; }

            double len = (hi - lo).Length + 4 * pad;
            TSolid tube = Cylinder(centre - axis * len, axis * (2 * len), r);
            if (convex && sector is { } sc)
            {
                // сектор ленты: брус без полупространств по ту сторону двух плоскостей через ось и крайние радиусы, минус цилиндр
                var wedge = new List<TSolid> { Box(lo - new Vector3d(pad, pad, pad), hi + new Vector3d(pad, pad, pad)) };
                foreach (var (d, other) in new[] { (sc.D1, sc.D2), (sc.D2, sc.D1) })
                {
                    var m = Vector3d.Cross(axis, d).Normalized();
                    if (Vector3d.Dot(m, other) > 0) m = -m;
                    BooleanOp.Subtract(wedge, HalfSpace(centre, m, len), warnings);
                    if (wedge.Count == 0) break;
                }
                if (wedge.Count == 0) continue;
                BooleanOp.Subtract(wedge, tube, warnings);
                if (wedge.Count == 0) continue;
                foreach (var piece in wedge.ToList()) BooleanOp.Subtract(cav, piece, warnings);
            }
            else if (convex)
            {
                // Вне цилиндра: большой брус минус цилиндр.
                var outside = new List<TSolid> { Box(lo - new Vector3d(pad, pad, pad), hi + new Vector3d(pad, pad, pad)) };
                BooleanOp.Subtract(outside, tube, warnings);
                if (outside.Count == 0) continue;
                BooleanOp.Subtract(cav, outside[0], warnings);
            }
            else BooleanOp.Subtract(cav, tube, warnings);
            if (ShellDebug != null && cav.Count > 0)
                ShellDebug($"после цилиндра R{r:0.##} ({(convex ? "выпуклый" : "вогнутый")}) полость {Math.Abs(Tess.MassProps.SignedVolume(Tess.Tessellator.Tessellate(cav[0]))):0.#}");
            if (cav.Count == 0) { warnings.Add("Shell: the wall is thicker than the body."); return false; }
        }

        cavity = cav[0];

        double cavityVol = Math.Abs(Tess.MassProps.SignedVolume(Tess.Tessellator.Tessellate(cavity)));
        warnings.Add($"Shell: cavity volume {cavityVol:0.###}.");
        if (cavityVol <= Tol.Weld)
        {
            warnings.Add("Shell: the wall is thicker than the body.");
            return false;
        }

        BooleanOp.Subtract(bodies, cavity, warnings);
        warnings.Add($"Shell: wall {thickness:0.###}, {planes.Count(x => x.Open) + openCyl} face(s) left open.");
        return true;
    }

    static readonly bool SnapOff = Environment.GetEnvironmentVariable("P3D_SHELLSNAP") == "0";
    static Vector3d SnapToFace(TSolid body, Vector3d q)
    {
        Vector3d best = q; double bd = Math.Max(1e-2, 3 * Tol.Chord);
        foreach (var f in body.Faces)
        {
            var su = GeometryAudit.AuthoritativeSurface(f);
            if (su == null) continue;
            var p = su.PointAt(su.ProjectToUV(q));
            double d = (p - q).Length;
            if (d < 1e-9) return q;
            if (d < bd && OnAnyFace(f, p)) { bd = d; best = p; }
        }
        return best;
    }
    static readonly bool SectorOff = Environment.GetEnvironmentVariable("P3D_SHELLSECTOR") == "0";

    /// <summary>
    /// u12: угловой сектор неполной цилиндрической грани — крайние радиальные направления её точек (по рёбрам) от середины;
    /// null — грань охватывает полуокружность и больше (тогда ограничивает весь цилиндр).
    /// </summary>
    static (Vector3d D1, Vector3d D2)? Sector(TFace f, Vector3d centre, Vector3d axis, Vector3d midRadial)
    {
        double amin = 0, amax = 0; bool any = false;
        var perp = Vector3d.Cross(axis, midRadial);
        foreach (var e in f.Edges)
        {
            if (e.Curve == null) continue;
            for (int i = 0; i <= 16; i++)
            {
                var q = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 16.0) - centre;
                q -= axis * Vector3d.Dot(q, axis);
                if (q.LengthSquared < 1e-18) continue;
                double a = Math.Atan2(Vector3d.Dot(q, perp), Vector3d.Dot(q, midRadial));
                if (!any) { amin = amax = a; any = true; } else { amin = Math.Min(amin, a); amax = Math.Max(amax, a); }
            }
        }
        if (!any || amax - amin >= Math.PI - 1e-6 || amax - amin < 1e-9) return null;
        Vector3d Dir(double a) => midRadial * Math.Cos(a) + perp * Math.Sin(a);
        return (Dir(amin), Dir(amax));
    }

    /// <summary>Открытые грани по точкам — только плоские (null — точка не на плоской грани).</summary>
    static HashSet<TFace>? OffsetOpenFaces(TSolid body, IReadOnlyList<Vector3d>? open)
    {
        var res = new HashSet<TFace>();
        if (open != null)
            foreach (var q in open)
            {
                var hit = body.Faces.FirstOrDefault(f => PlaneOfFace(f) is { } pl && OnFace(f, pl, q));
                if (hit == null) return null;
                res.Add(hit);
            }
        return res;
    }

    /// <summary>Мутация P3D_SHELLTWEAK=0: полость только пересечением полупространств (прежде).</summary>
    static readonly bool TweakOff = Environment.GetEnvironmentVariable("P3D_SHELLTWEAK") == "0";
    /// <summary>Мутация P3D_SHELLCONVEX=0: прежний путь не проверяет выпуклость (вогнутое тело — неверная полость молча).</summary>
    static readonly bool ConvexOff = Environment.GetEnvironmentVariable("P3D_SHELLCONVEX") == "0";
    /// <summary>u10: мутации — цилиндр-поверхность в пути полупространств (P3D_SHELLCYLSURF=0) и тонкое скругление (P3D_SHELLTHINROUND=0).</summary>
    static readonly bool CylSurfOff = Environment.GetEnvironmentVariable("P3D_SHELLCYLSURF") == "0";
    static readonly bool ThinRoundOff = Environment.GetEnvironmentVariable("P3D_SHELLTHINROUND") == "0";
    /// <summary>u10: мутация P3D_SHELLOUTCYL=0 — стенка наружу у тела из плоскостей и неполных цилиндров только сдвигом плоскостей (прежде).</summary>
    static readonly bool OutwardCylOff = Environment.GetEnvironmentVariable("P3D_SHELLOUTCYL") == "0";

    public static long OpenAtBlendShells;

    static readonly bool CurvedThickOff = Environment.GetEnvironmentVariable("P3D_SHELLCURVEDTHICK") == "0";

    /// <summary>Точка лежит на грани любой поверхности: на поверхности (до допуска гранения) и у её треугольников.</summary>
    static bool OnAnyFace(TFace f, Vector3d q)
    {
        var su = GeometryAudit.AuthoritativeSurface(f);
        if (su == null || (su.PointAt(su.ProjectToUV(q)) - q).Length > 1e-6) return false;
        var mb = new Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Tess.MeshData(); mb.FillInto(md);
        double best = double.MaxValue;
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3d x = mb.PositionsD[md.Indices[i]], y = mb.PositionsD[md.Indices[i + 1]], z = mb.PositionsD[md.Indices[i + 2]];
            var n = Vector3d.Cross(y - x, z - x); if (n.LengthSquared < 1e-24) continue; n.Normalize();
            double d = Vector3d.Dot(q - x, n); var qp = q - n * d;
            if (InTriangle(qp, x, y, z)) best = Math.Min(best, Math.Abs(d));
        }
        return best < Math.Max(1e-2, 3 * Tol.Chord);
    }

    static Geometry.Ssi.Plane? PlaneOfFace(TFace f) => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) as Geometry.Ssi.Plane;

    /// <summary>
    /// KRN-07 (07.5): ВЫПУКЛОСТЬ ПО ПЛОСКИМ ГРАНЯМ. Прежний путь строит полость как пересечение полупространств сдвинутых
    /// плоскостей — это верно только у выпуклого (по плоским граням) тела: у L-профиля полость выходила кубом без ножки
    /// (объём стенки 30384 вместо 14544), и результат принимался как точный. Тело выпукло, если все его вершины лежат не
    /// снаружи плоскости каждой плоской грани.
    /// </summary>
    static bool Convex(TSolid body)
    {
        var verts = body.Vertices.Select(v => v.P).ToList();
        double scale = Math.Max(1, verts.Count == 0 ? 1 : verts.Max(p => p.Length));
        foreach (var f in body.Faces)
        {
            if (PlaneOfFace(f) is not { } pl) continue;
            if (PlanarTweak.OutwardNormal(f) is not { } nOut) return false;
            if (verts.Any(p => Vector3d.Dot(p - pl.P, nOut) > 1e-7 * scale)) return false;
        }
        return true;
    }

    /// <summary>
    /// KRN-07 (07.5): СДВИГ ПЛОСКОСТЕЙ. Каждая плоская грань сдвигается вдоль наружной нормали на offset(грань, плоскость,
    /// открыта ли); вершины — пересечения сдвинутых плоскостей (PlanarTweak). Полость оболочки — то же тело со сдвигом
    /// внутрь (открытые грани — наружу): тот же граф, что тело, поэтому верно и у вогнутого тела (уступ L-профиля
    /// сдвигается так же). Стенка толще элемента (ребро исчезло бы) — отказ.
    /// </summary>
    /// <summary>Мутация P3D_SHELLCYL=0: сдвиг плоскостей только у тел из плоских граней (цилиндры — прежним путём).</summary>
    static readonly bool CylTweakOff = Environment.GetEnvironmentVariable("P3D_SHELLCYL") == "0";
    static Geometry.Ssi.Cylinder? CylOfFace(TFace f) => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) as Geometry.Ssi.Cylinder;

    /// <param name="cylOffset">Сдвиг цилиндрических граней вдоль наружной нормали (у выпуклой — радиус больше, у вогнутой — меньше).</param>
    static TSolid? OffsetBody(TSolid body, Func<TFace, Geometry.Ssi.Plane, bool, double> offset, IReadOnlyList<Vector3d>? open, string? tag, ref string? why, double cylOffset)
    {
        string? openCyl = null;
        var res = PlanarTweak.Apply(body, (i, f) =>
        {
            if (PlaneOfFace(f) is not { } pl) return null;
            var nOut = PlanarTweak.OutwardNormal(f)!.Value;
            double d = offset(f, pl, open != null && open.Any(q => OnFace(f, pl, q)));
            return d == 0 ? null : (pl.P + nOut * d, nOut);
        }, ref why, (i, f) =>
        {
            if (cylOffset == 0 || CylOfFace(f) is not { } cy || PlanarTweak.CylConvex(f) is not bool convex) return null;
            if (open != null && open.Any(q => Math.Abs((q - cy.P - cy.Axis.Normalized() * Vector3d.Dot(q - cy.P, cy.Axis.Normalized())).Length - cy.R) < Tol.Chord)) { openCyl = "an open cylindrical face"; return null; }
            return cy.R + (convex ? cylOffset : -cylOffset);
        });
        if (openCyl != null) { why = openCyl; return null; }
        if (res == null) return null;
        if (tag != null) foreach (var f in res.Faces) f.Tag = tag;
        return res;
    }

    /// <summary>Точка q на плоской грани f: на её плоскости (до Tol.Chord) и внутри её треугольников.</summary>
    static bool OnFace(TFace f, Geometry.Ssi.Plane pl, Vector3d q)
    {
        var n = pl.N.Normalized();
        if (Math.Abs(Vector3d.Dot(q - pl.P, n)) >= Tol.Chord) return false;
        var mb = new Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Tess.MeshData(); mb.FillInto(md);
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3d x = mb.PositionsD[md.Indices[i]], y = mb.PositionsD[md.Indices[i + 1]], z = mb.PositionsD[md.Indices[i + 2]];
            if (InTriangle(q - n * Vector3d.Dot(q - x, n), x, y, z)) return true;
        }
        return false;
    }

    private static bool InTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        var n = Vector3d.Cross(b - a, c - a);
        double tol = -1e-9 * n.LengthSquared;
        return Vector3d.Dot(Vector3d.Cross(b - a, p - a), n) >= tol && Vector3d.Dot(Vector3d.Cross(c - b, p - b), n) >= tol
            && Vector3d.Dot(Vector3d.Cross(a - c, p - c), n) >= tol;
    }

    private static double Extent(TFace f)
    {
        double r = 0;
        if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            Vector3d c = fs.Facets[0].A;
            foreach (var (x, y, z) in fs.Facets)
                foreach (var q in new[] { x, y, z }) r = Math.Max(r, (q - c).Length);
        }
        return r;
    }

    private static (Vector3d Lo, Vector3d Hi) Bounds(TSolid s)
    {
        // По гранению: у тела, ещё не прошедшего булеву, гранёных поверхностей нет, и габарит
        // по ним выходил бесконечным.
        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        var md = Tess.Tessellator.Tessellate(s);
        foreach (var v in md.Vertices)
        {
            var q = new Vector3d(v.P.X, v.P.Y, v.P.Z);
            lo = Vector3d.ComponentMin(lo, q);
            hi = Vector3d.ComponentMax(hi, q);
        }
        return (lo, hi);
    }

    /// <summary>Полупространство {x : (x − at)·n > 0} — призма, заведомо больше тела.</summary>
    private static TSolid HalfSpace(Vector3d at, Vector3d n, double size)
    {
        Vector3d u = Math.Abs(n.X) < 0.9 ? Vector3d.Cross(n, Vector3d.UnitX) : Vector3d.Cross(n, Vector3d.UnitY);
        u = u.Normalized();
        Vector3d v = Vector3d.Cross(n, u).Normalized();
        double r = size;

        var frame = new Frame3(at, u, v);
        var pts = new[]
        {
            at - u * r - v * r, at + u * r - v * r, at + u * r + v * r, at - u * r + v * r
        };
        var curves = new List<ICurve3>
        {
            new LineCurve3(pts[0], pts[1]), new LineCurve3(pts[1], pts[2]),
            new LineCurve3(pts[2], pts[3]), new LineCurve3(pts[3], pts[0])
        };
        var profile = ProfileBuilder.Build(frame, curves);
        return ExtrudeOp.Build(profile, n * r);
    }

    /// <summary>Цилиндр: окружность радиуса r в начале, выдавленная вдоль dir.</summary>
    private static TSolid Cylinder(Vector3d at, Vector3d dir, double r)
    {
        Vector3d n = dir.Normalized();
        Vector3d u = Math.Abs(n.X) < 0.9 ? Vector3d.Cross(n, Vector3d.UnitX).Normalized() : Vector3d.Cross(n, Vector3d.UnitY).Normalized();
        Vector3d v = Vector3d.Cross(n, u).Normalized();
        var frame = new Frame3(at, u, v);
        var circle = new ArcCurve3(frame, r, 0, 2 * Math.PI);
        return ExtrudeOp.Build(ProfileBuilder.Build(frame, new List<ICurve3> { circle }), dir);
    }

    private static TSolid Box(Vector3d lo, Vector3d hi)
    {
        var frame = new Frame3(new Vector3d(lo.X, lo.Y, lo.Z), Vector3d.UnitX, Vector3d.UnitY);
        var pts = new[]
        {
            new Vector3d(lo.X, lo.Y, lo.Z), new Vector3d(hi.X, lo.Y, lo.Z),
            new Vector3d(hi.X, hi.Y, lo.Z), new Vector3d(lo.X, hi.Y, lo.Z)
        };
        var curves = new List<ICurve3>
        {
            new LineCurve3(pts[0], pts[1]), new LineCurve3(pts[1], pts[2]),
            new LineCurve3(pts[2], pts[3]), new LineCurve3(pts[3], pts[0])
        };
        return ExtrudeOp.Build(ProfileBuilder.Build(frame, curves), new Vector3d(0, 0, hi.Z - lo.Z));
    }
}
