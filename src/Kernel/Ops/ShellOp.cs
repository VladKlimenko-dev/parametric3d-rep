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
        // KRN-07 (07.5): ТЕЛО С КРИВЫМИ ГРАНЯМИ (шар, тор, скругления, цилиндры с прямыми рёбрами) — полость смещением всех
        // граней без смены топологии (DirectRound.OffsetSolid): закрытые грани внутрь на толщину, открытые (только плоские,
        // все соседи — плоские) — наружу. Мутация P3D_OFFSETSOLID=0.
        string? whyOffset = null;
        if (!TweakOff && outward && faceThickness == null && !body.Faces.All(f => PlaneOfFace(f) != null || (!CylTweakOff && CylOfFace(f) != null))
            && OffsetOpenFaces(body, open) is { } openOut)
        {
            // стенка наружу у тела с кривыми гранями: наружное — все грани наружу (открытые — на месте), внутреннее — тело
            // с открытыми гранями, вынесенными наружу
            double t = -thickness;
            var outerO = DirectRound.OffsetSolid(body, f => openOut.Contains(f) ? 0 : t, null, ref whyOffset);
            var innerO = outerO == null ? null : openOut.Count == 0 ? body : DirectRound.OffsetSolid(body, f => openOut.Contains(f) ? t : 0, null, ref whyOffset);
            if (outerO != null && innerO != null)
            {
                foreach (var f in outerO.Faces) if (f.Tag.Length > 0 && !f.Tag.StartsWith("shell:")) f.Tag = "shell:outer:" + EdgeRef.Bare(f.Tag);
                var res = new List<TSolid> { outerO };
                BooleanOp.Subtract(res, innerO, warnings);
                if (res.Count == 0) { warnings.Add("Shell: the outward wall is empty."); return false; }
                bodies.RemoveAt(0); bodies.InsertRange(0, res);
                warnings.Add($"Shell: wall {t:0.###} outward, by offsetting all faces (exact).");
                return true;
            }
            warnings.Add($"Shell: an outward wall by offsetting all faces declined: {whyOffset}."); return false;
        }
        if (!TweakOff && !outward && faceThickness == null)
        {
            bool planarOnly = body.Faces.All(f => PlaneOfFace(f) != null || (!CylTweakOff && CylOfFace(f) != null));
            if (OffsetOpenFaces(body, open) is { } openFaces && !planarOnly)
            {
                var cavO = DirectRound.OffsetSolid(body, f => openFaces.Contains(f) ? thickness : -thickness, "shell:inner", ref whyOffset);
                if (cavO != null)
                {
                    BooleanOp.Subtract(bodies, cavO, warnings);
                    warnings.Add($"Shell: wall {thickness:0.###}, cavity by offsetting all faces (exact).");
                    return true;
                }
                ShellDebug?.Invoke("offset solid declined: " + whyOffset);
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
                if (cavT == null && faceThickness == null && OffsetOpenFaces(body, open) is { } of2)
                {
                    byOffset = true;
                    cavT = DirectRound.OffsetSolid(body, f => of2.Contains(f) ? thickness : -thickness, "shell:inner", ref whyOffset);
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
                warnings.Add($"Shell: an outward wall is built only by plane offset, which declined: {why}."); return false;
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
        var planes = new List<(Vector3d P, Vector3d N, bool Open)>();
        var cylinders = new List<(TFace F, ArcCurve3 Arc, Vector3d Dir)>();

        foreach (TFace f in body.Faces)
        {
            ISurface3? src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (src is ExtrudedSurface cyl && cyl.Base is ArcCurve3 arc)
            {
                cylinders.Add((f, arc, cyl.Dir));
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

        foreach (var (f, arc, dir) in cylinders)
        {
            Vector3d axis = dir.LengthSquared > 1e-18 ? dir.Normalized() : arc.Frame.W.Normalized();
            Vector3d centre = arc.Frame.Origin;
            Vector3d onFace = arc.PointAt(0.5);
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

            bool isOpen = open != null && open.Any(q => Math.Abs((q - centre - axis * Vector3d.Dot(q - centre, axis)).Length - arc.Radius) < Tol.Chord);
            double r = convex ? arc.Radius - (isOpen ? 0 : thickness) : arc.Radius + (isOpen ? 0 : thickness);
            if (r <= Tol.Weld) { warnings.Add("Shell: the wall is thicker than the body."); return false; }

            double len = (hi - lo).Length + 4 * pad;
            TSolid tube = Cylinder(centre - axis * len, axis * (2 * len), r);
            if (convex)
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
        warnings.Add($"Shell: wall {thickness:0.###}, {planes.Count(x => x.Open)} face(s) left open.");
        return true;
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
