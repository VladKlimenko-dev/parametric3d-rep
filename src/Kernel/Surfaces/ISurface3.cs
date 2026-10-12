using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>Аналитическая поверхность, несущая грань. Знает, как себя тесселировать по контурам грани.</summary>
public interface ISurface3
{
    Vector3d PointAt(Vector2d uv);
    Vector3d NormalAt(Vector2d uv);
    Vector2d ProjectToUV(Vector3d p);
    bool IsPlanar { get; }
    void TessellateFace(TFace face, MeshBuilder mb);
}

/// <summary>Плоскость. Грани на ней триангулируются ear-clipping-ом с учётом отверстий.</summary>
public sealed class PlaneSurface : ISurface3
{
    public readonly Frame3 F;
    public PlaneSurface(Frame3 f) { F = f; }

    public bool IsPlanar => true;
    public Vector3d PointAt(Vector2d uv) => F.ToWorld(uv);
    public Vector3d NormalAt(Vector2d uv) => F.W;
    public Vector2d ProjectToUV(Vector3d p) => F.ToLocal2(p);

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        var outerLoop = face.Outer;
        if (outerLoop == null) return;

        List<Vector2d> outer = outerLoop.TessellatePolyline().Select(F.ToLocal2).ToList();
        var holes = new List<IReadOnlyList<Vector2d>>();
        foreach (var l in face.Inners)
            holes.Add(l.TessellatePolyline().Select(F.ToLocal2).ToList());

        var tris = Triangulator.Triangulate(outer, holes, out var pts);
        if (tris.Count == 0) return;

        Vector3d n = face.Reversed ? -F.W : F.W;
        int baseIdx = mb.VertexCount;
        foreach (var uv in pts) mb.AddVertex(F.ToWorld(uv), n);

        for (int i = 0; i < tris.Count; i += 3)
        {
            int a = baseIdx + tris[i], b = baseIdx + tris[i + 1], c = baseIdx + tris[i + 2];
            if (face.Reversed) mb.AddTriangle(a, c, b);
            else mb.AddTriangle(a, b, c);
        }
    }
}

/// <summary>
/// Поверхность выдавливания: базовая кривая, протянутая вдоль вектора Dir.
/// v в [0,1] — доля вектора; u — параметр базовой кривой.
/// </summary>
public sealed class ExtrudedSurface : ISurface3
{
    static readonly bool ObliqueUvOff = Environment.GetEnvironmentVariable("P3D_OBLIQUEUV") == "0";
    public readonly ICurve3 Base;
    public readonly Vector3d Dir;

    public ExtrudedSurface(ICurve3 baseCurve, Vector3d dir) { Base = baseCurve; Dir = dir; }

    public bool IsPlanar => Base is LineCurve3;

    public Vector3d PointAt(Vector2d uv) => Base.PointAt(uv.X) + Dir * uv.Y;

    public Vector3d NormalAt(Vector2d uv)
    {
        Vector3d t = Base.TangentAt(uv.X);
        Vector3d n = Vector3d.Cross(t, Dir);
        return n.LengthSquared < 1e-24 ? Vector3d.UnitZ : n.Normalized();
    }

    /// <summary>
    /// ТОЧНАЯ ПРОЕКЦИЯ (этап 1). Прежде — перебор по 48 пробам с обрезкой [0, 1]: до 0.16 мм
    /// ошибки у окружности R20. Выдавливание отрезка — нормальные уравнения 2×2; дуги
    /// с направлением поперёк её плоскости — угол плюс сдвиг; прочее — ближайшая точка
    /// профиля с уточнением и сдвиг вдоль направления.
    /// </summary>
    public Vector2d ProjectToUV(Vector3d p)
    {
        double d2 = Dir.LengthSquared;
        if (Base is LineCurve3 line && d2 > 1e-30)
        {
            // min |A + e·u + Dir·v − p|²:  [e·e  e·D][u]   [e·(p−A)]
            //                               [e·D  D·D][v] = [D·(p−A)]
            Vector3d e = line.B - line.A, r = p - line.A;
            double ee = Vector3d.Dot(e, e), ed = Vector3d.Dot(e, Dir), dd = d2;
            double det = ee * dd - ed * ed;
            if (Math.Abs(det) > 1e-30)
                return new Vector2d((Vector3d.Dot(e, r) * dd - ed * Vector3d.Dot(Dir, r)) / det,
                                    (ee * Vector3d.Dot(Dir, r) - ed * Vector3d.Dot(e, r)) / det);
        }
        if (d2 < 1e-30) return new Vector2d(Curves.CurveParam.Closest(Base, p), 0);

        Vector3d dn = Dir / Math.Sqrt(d2);
        if (Base is ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), dn)) - 1) < 1e-12)
        {
            // Направление поперёк плоскости дуги: сдвиг вдоль него точно, угол — в плоскости.
            double u = Curves.CurveParam.Closest(arc, p - dn * Vector3d.Dot(p - arc.Frame.Origin, dn));
            return new Vector2d(u, Vector3d.Dot(p - arc.PointAt(u), Dir) / d2);
        }

        // KRN-07: КОСОЕ ВЫДАВЛИВАНИЕ ДУГИ (эллиптический цилиндр): точка сдвигается в плоскость дуги ВДОЛЬ Dir (не по
        // перпендикуляру) — v = ((p − O)·W)/(Dir·W). Прежде сдвиг шёл на (p − B(0))·dn вдоль dn, и для косого выдавливания
        // отверстия кромка «отходила от поверхности» на 1.44 мм (тело негодно, объёма нет). Мутация P3D_OBLIQUEUV=0.
        if (Base is ArcCurve3 oa && !ObliqueUvOff && Math.Abs(Vector3d.Dot(oa.Frame.W.Normalized(), Dir)) > 1e-12)
        {
            var W = oa.Frame.W.Normalized();
            double v = Vector3d.Dot(p - oa.Frame.Origin, W) / Vector3d.Dot(Dir, W);
            double u = Curves.CurveParam.Closest(oa, p - Dir * v);
            return new Vector2d(u, v);
        }
        // Общий случай: ближайшая точка профиля к точке, сдвинутой на плоскость профиля.
        double uu = Curves.CurveParam.Closest(Base, p - dn * Vector3d.Dot(p - Base.PointAt(0), dn));
        return new Vector2d(uu, Vector3d.Dot(p - Base.PointAt(uu), Dir) / d2);
    }

    // KRN-09 (09.1, ворота g.11): СЕТКА ПОКАЗА — ОДНА ПОЛОСА ПО ВЫСОТЕ. Деления по высоте каждая грань выбирала сама (по
    // своему шагу основания), и у соседних граней с общим ребром образующей они не совпадали: сетка простого выдавливания
    // «стадион» (отрезки и дуги) или стенки 100×5 была незамкнутой (16 ошибок, «стороны без пары»). Образующая линейна —
    // форме деления не нужны; качество элементов нужно расчётной сетке. Одна полоса — только у сетки показа и проверки
    // (Tessellator); прочие потребители (многогранный путь булевой, расчётная сетка) получают прежнее деление: их вход
    // по нему выверен (измерено: многогранный путь детали у 3e5 мм с одной полосой давал грань против нормали).
    // Мутация P3D_DISPLAYONEROW=0 — прежнее деление.
    internal static readonly bool AxialSplitOld = Environment.GetEnvironmentVariable("P3D_DISPLAYONEROW") == "0";
    public void TessellateFace(TFace face, MeshBuilder mb) => TessellateFace(face, mb, subdivideAxially: true);
    /// <summary>Сетка показа (Tessellator): одна полоса по высоте.</summary>
    internal void TessellateDisplay(TFace face, MeshBuilder mb) => TessellateFace(face, mb, subdivideAxially: AxialSplitOld);

    // Boolean-вводу не требуется качество расчётного элемента: образующая
    // линейна. Одна полоса сохраняет прямые общие рёбра при округлении в Exact.
    // Публичный путь для показа/расчётной сетки сохраняет прежнее дробление.
    internal void TessellateFace(TFace face, MeshBuilder mb, bool subdivideAxially)
    {
        // ОБРЕЗКИ ПО КОНТУРАМ ЗДЕСЬ НЕТ — ДВЕ ПОПЫТКИ ИЗМЕРЕНЫ И ОТКАЧЕНЫ. Грань гранится полной полосой, что
        // бы ни было в её контурах. Для целого цилиндра это верно, но у вала с поперечным
        // отверстием сетка накрывает и вырез: объём выходит на 3.6% больше расчётного.
        //
        //   1. Прямая триангуляция развёртки по контурам: −29.7%. У ПОЛНОГО цилиндра внешний
        //      контур в развёртке не замкнутый многоугольник, а две отдельные окружности.
        //   2. Полоса с разрезом по шву (низ слева направо, верх обратно, вырезы отверстиями):
        //      −29.6%, то есть ничем не лучше.
        //
        // Значит, упрощённый перенос приёма не годится: в точной сетке полоса учитывает и шов, и
        // обходы краёв, и вырезы, пересекающие шов, — и всё это вместе. Переносить надо не
        // замысел, а сам построитель (ExactFaceMesher), приспособив его к контурам из кривых
        // пересечения. До тех пор грань гранится полной полосой, и объём вала с поперечным
        // отверстием выходит на 3.6% больше расчётного.

        // ПО ВЫСОТЕ ТОЖЕ НУЖНЫ ДЕЛЕНИЯ. Поверхность линейчатая, и для ФОРМЫ одного деления
        // хватает — но не для качества треугольников: у цилиндра R8 высотой 20 шаг по дуге
        // выходит около 1.3 мм, а по высоте все 20, и треугольники получаются вытянутыми в
        // пятнадцать раз.
        //
        // Измерено по ста телам: на цилиндрических гранях 58 837 плохих треугольников из 84 123
        // всех — больше, чем на всех прочих вместе. Для показа это незаметно, для расчёта
        // негодно: вытянутый элемент даёт неверные напряжения.
        //
        // Делений по высоте берётся столько, чтобы шаг по ней был близок к шагу по дуге.
        var basePts = new List<Vector3d>();
        Base.Tessellate(basePts, true);
        int nu = basePts.Count - 1;
        if (nu < 1) return;

        double along = Dir.Length;
        double step = 0;
        for (int i = 0; i < nu; i++) step = Math.Max(step, (basePts[i + 1] - basePts[i]).Length);

        int nv = step > 1e-12 ? (int)Math.Ceiling(along / step) : 1;
        nv = Math.Clamp(nv, 1, 64);
        if (!subdivideAxially) nv = 1;

        int start = mb.VertexCount;
        for (int i = 0; i <= nu; i++)
        {
            double u = i / (double)nu;
            Vector3d n = NormalAt(new Vector2d(u, 0));
            if (face.Reversed) n = -n;

            for (int j = 0; j <= nv; j++)
                mb.AddVertex(basePts[i] + Dir * (j / (double)nv), n);
        }

        // Обход треугольников должен давать ту же нормаль, что и NormalAt,
        // а она равна cross(du, dv), где du — вдоль кривой, dv — вдоль Dir.
        // Здесь a -> c это du, a -> b это dv, поэтому обход (a, c, b).
        //
        // Раньше стояло (a, b, c), то есть cross(dv, du) — намотка смотрела
        // ВНУТРЬ, хотя нормали вершин смотрели наружу. Само по себе это давало
        // лишь неверное отсечение боковых граней, но у плоского цилиндра
        // перевешивало в знаковом объёме, тот получался отрицательным,
        // и защита «развернуть всё» в тесселяторе выворачивала заодно обе
        // крышки. Отсюда и симптом: верхняя крышка пропадала, нижняя была
        // видна сверху.
        int stride = nv + 1;

        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int a = start + i * stride + j, b = a + 1;
                int c = a + stride, d = c + 1;

                if (face.Reversed) { mb.AddTriangle(a, b, c); mb.AddTriangle(b, d, c); }
                else { mb.AddTriangle(a, c, b); mb.AddTriangle(b, c, d); }
            }
    }
}

/// <summary>Поверхность вращения базовой кривой вокруг оси (Axis.Origin, Axis.W) от A0 до A1.</summary>
public sealed class RevolvedSurface : ISurface3
{
    public readonly ICurve3 Base;
    public readonly Frame3 Axis;
    public readonly double A0, A1;

    public RevolvedSurface(ICurve3 baseCurve, Frame3 axis, double a0, double a1)
    { Base = baseCurve; Axis = axis; A0 = a0; A1 = a1; }

    /// <summary>
    /// Число шагов по углу при разбиении на треугольники; 0 — по своему радиусу.
    ///
    /// ИСПРАВЛЕНО. Каждая грань вращения выбирала число шагов сама, по своему
    /// наибольшему радиусу. Две грани, делящие окружность, но доходящие
    /// до разных радиусов, разбивали её по-разному, и сетка тела оказывалась
    /// незамкнутой. Точная булева получала дырявый вход и снимала вдесятеро
    /// больше, чем весил резец. Тело вращения задаёт одно число на все свои
    /// грани (см. RevolveOp).
    /// </summary>
    public int AngularSegments { get; init; }

    /// <summary>
    /// Явные углы разбиения (по возрастанию, от A0 до A1) — вместо равномерного шага.
    ///
    /// Резец по дуге режет вдоль гранёной стенки, и если его вершины по углу
    /// не совпадают с вершинами стенки, две гранёные версии одной окружности
    /// перемежаются: грань резца прорастает сквозь хорды стенки островками.
    /// Проверочный прогон (Parametric3D.Verify) нашёл так 17 островков у одной грани.
    /// </summary>
    public double[]? Angles { get; init; }

    public bool IsPlanar => false;
    public double Sweep => A1 - A0;

    public static Vector3d RotateAbout(Vector3d p, Vector3d origin, Vector3d axis, double angle)
    {
        Vector3d v = p - origin;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        Vector3d r = v * c + Vector3d.Cross(axis, v) * s + axis * (Vector3d.Dot(axis, v) * (1 - c));
        return origin + r;
    }

    public Vector3d PointAt(Vector2d uv)
        => RotateAbout(Base.PointAt(uv.X), Axis.Origin, Axis.W, A0 + Sweep * uv.Y);

    public Vector3d NormalAt(Vector2d uv)
    {
        Vector3d n = RawNormal(uv);
        if (n.LengthSquared > 0) return n;

        // ТОЧКА НА ОСИ. У образующей, упирающейся в ось, — вершина конуса —
        // нормаль в самой точке не определена, и прежде возвращалось направление
        // оси: вершина затенялась пятном, с ошибкой в 45° у фаски, равной радиусу.
        // Нормаль конуса постоянна вдоль образующей, поэтому предел вдоль неё
        // при том же угле — точный ответ для этого направления.
        foreach (double du in new[] { 1e-6, -1e-6, 1e-4, -1e-4 })
        {
            double u = uv.X + du;
            if (u < 0 || u > 1) continue;
            n = RawNormal(new Vector2d(u, uv.Y));
            if (n.LengthSquared > 0) return n;
        }

        return Axis.W;
    }

    /// <summary>Нормаль по производным; нулевой вектор, если точка на оси.</summary>
    private Vector3d RawNormal(Vector2d uv)
    {
        Vector3d p = Base.PointAt(uv.X);
        double a = A0 + Sweep * uv.Y;
        Vector3d du = RotateAbout(Base.TangentAt(uv.X) + Axis.Origin, Axis.Origin, Axis.W, a) - Axis.Origin;
        Vector3d pr = RotateAbout(p, Axis.Origin, Axis.W, a);
        // Производная по параметру развёртки несёт знак развёртки: при
        // отрицательном угле точка идёт по кругу в обратную сторону. Без этого
        // знака нормаль расходилась с обходом треугольников на 180°.
        Vector3d dv = Vector3d.Cross(Axis.W, pr - Axis.Origin) * (Sweep < 0 ? -1 : 1);
        Vector3d n = Vector3d.Cross(du, dv);
        return n.LengthSquared < 1e-24 ? Vector3d.Zero : n.Normalized();
    }

    /// <summary>
    /// Угол, на котором лежит сама образующая, относительно оси U.
    ///
    /// ИСПРАВЛЕНО. ProjectToUV считал угол точки от оси U и полагал, что
    /// образующая лежит на угле ноль. У вращения пользовательского эскиза
    /// так и есть, и ошибка не проявлялась. А резец конической фаски
    /// строится из профиля на середине дуги, под произвольным углом: нормаль
    /// бралась в точке, повёрнутой на эту разницу, и коническая грань
    /// затенялась с ошибкой до 58° — выглядела вогнутой, с перегибом.
    /// </summary>
    private double BaseAngle()
    {
        if (!double.IsNaN(_baseAngle)) return _baseAngle;

        foreach (double u in new[] { 0.5, 0.0, 1.0, 0.25, 0.75 })
        {
            Vector3d l = Base.PointAt(u) - Axis.Origin;
            Vector3d r = l - Axis.W * Vector3d.Dot(l, Axis.W);
            if (r.LengthSquared > 1e-18)
                return _baseAngle = Math.Atan2(Vector3d.Dot(r, Axis.V), Vector3d.Dot(r, Axis.U));
        }

        return _baseAngle = 0;
    }

    private double _baseAngle = double.NaN;

    public Vector2d ProjectToUV(Vector3d p)
    {
        Vector3d local = p - Axis.Origin;
        double h = Vector3d.Dot(local, Axis.W);
        Vector3d radial = local - Axis.W * h;
        double ang = Math.Atan2(Vector3d.Dot(radial, Axis.V), Vector3d.Dot(radial, Axis.U));

        // Поворот, которым образующая пришла в точку, — от её собственного угла.
        double rel = ang - BaseAngle();

        // При отрицательной развёртке поворот идёт в обратную сторону;
        // прежде параметр выходил отрицательным и обрезался в ноль.
        double v;
        if (Math.Abs(Sweep) < 1e-12) v = 0;
        else
        {
            double span = Math.Abs(Sweep);
            double w = Sweep > 0 ? GeomUtil.Norm2Pi(rel - A0) : GeomUtil.Norm2Pi(A0 - rel);

            // ТОЧКА НА КРАЮ НЕ УЛЕТАЕТ НА ПРОТИВОПОЛОЖНЫЙ.
            //
            // Точка ровно на начальном крае из-за округления получает угол
            // на ничтожную долю меньше начала, и перенос через 2π делает из
            // него почти полный оборот — параметр уходил на дальний край,
            // а нормаль бралась там. Вне развёртки выбирается ближайший
            // по окружности край.
            if (w > span && 2 * Math.PI - w < w - span) w -= 2 * Math.PI;

            v = w / span;
        }

        // Разворачиваем точку назад к образующей и ищем ближайший u,
        // затем уточняем делением шага пополам.
        Vector3d back = RotateAbout(p, Axis.Origin, Axis.W, -(A0 + Sweep * Math.Clamp(v, 0, 1)));

        // Параметр профиля — точно (этап 1): прежде перебор с делением шага.
        return new Vector2d(Curves.CurveParam.Closest(Base, back), v);
    }

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        var basePts = new List<Vector3d>();
        Base.Tessellate(basePts, true);
        int nu = basePts.Count - 1;
        if (nu < 1) return;

        double maxR = 0;
        foreach (var p in basePts)
        {
            Vector3d l = p - Axis.Origin;
            double r = (l - Axis.W * Vector3d.Dot(l, Axis.W)).Length;
            if (r > maxR) maxR = r;
        }
        int nv = Angles is { Length: >= 2 } ? Angles.Length - 1
               : AngularSegments > 0 ? AngularSegments : GeomUtil.ArcSegments(maxR, Sweep);

        int start = mb.VertexCount;
        for (int i = 0; i <= nu; i++)
        {
            double u = i / (double)nu;
            for (int j = 0; j <= nv; j++)
            {
                double v = Angles is { Length: >= 2 } && Math.Abs(Sweep) > 1e-12
                    ? (Angles[j] - A0) / Sweep
                    : j / (double)nv;
                Vector3d n = NormalAt(new Vector2d(u, v));
                if (face.Reversed) n = -n;
                mb.AddVertex(RotateAbout(basePts[i], Axis.Origin, Axis.W, A0 + Sweep * v), n);
            }
        }

        // НА ПОЛЮСЕ ЧЕТЫРЁХУГОЛЬНИК ВЫРОЖДАЕТСЯ В ТРЕУГОЛЬНИК. Там, где образующая подходит к
        // оси, весь пояс точек сходится в одну: обе стороны четырёхугольника схлопываются, и из
        // двух треугольников один выходит нулевой площади.
        //
        // Измерено: у шара и у конуса таких ровно по сто. Объём при этом верен и тело годно —
        // поэтому беда и жила незаметно, — но для расчёта выродившийся элемент негоден: он даёт
        // бесконечные напряжения.
        //
        // Теперь на полюсе ставится ОДИН треугольник вместо двух.
        double sameAt = Math.Max(Core.Tol.Weld, maxR * 1e-9);

        bool Same(int x, int y) => (mb.PositionsD[x] - mb.PositionsD[y]).Length <= sameAt;

        int rowStride = nv + 1;
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int a = start + i * rowStride + j;
                int b = a + 1;
                int c = a + rowStride;
                int d = c + 1;

                bool topSame = Same(a, b);          // пояс i сошёлся в точку
                bool bottomSame = Same(c, d);       // пояс i+1 сошёлся в точку

                if (topSame && bottomSame) continue;             // оба пояса в точке — ничего нет

                if (topSame)
                {
                    if (face.Reversed) mb.AddTriangle(a, d, c);
                    else mb.AddTriangle(a, c, d);
                    continue;
                }

                if (bottomSame)
                {
                    if (face.Reversed) mb.AddTriangle(a, b, c);
                    else mb.AddTriangle(a, c, b);
                    continue;
                }

                if (face.Reversed) { mb.AddTriangle(a, b, c); mb.AddTriangle(b, d, c); }
                else { mb.AddTriangle(a, c, b); mb.AddTriangle(b, c, d); }
            }
    }
}

/// <summary>Цилиндр: ось + радиус. Нужен для скруглений и распознавания отверстий.</summary>
public sealed class CylinderSurface : ISurface3
{
    public readonly Frame3 Axis;   // Origin — точка на оси, W — направление оси
    public readonly double Radius;

    public CylinderSurface(Frame3 axis, double radius) { Axis = axis; Radius = radius; }

    public bool IsPlanar => false;
    public Vector3d PointAt(Vector2d uv)
        => Axis.Origin + Axis.U * (Math.Cos(uv.X) * Radius) + Axis.V * (Math.Sin(uv.X) * Radius) + Axis.W * uv.Y;

    public Vector3d NormalAt(Vector2d uv)
        => (Axis.U * Math.Cos(uv.X) + Axis.V * Math.Sin(uv.X)).Normalized();

    public Vector2d ProjectToUV(Vector3d p)
    {
        Vector3d l = p - Axis.Origin;
        double h = Vector3d.Dot(l, Axis.W);
        Vector3d r = l - Axis.W * h;
        return new Vector2d(Math.Atan2(Vector3d.Dot(r, Axis.V), Vector3d.Dot(r, Axis.U)), h);
    }

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        // Цилиндрическая грань от Round/Chamfer описывается как поверхность вращения.
        var rs = new RevolvedSurface(
            new LineCurve3(Axis.Origin + Axis.U * Radius, Axis.Origin + Axis.U * Radius + Axis.W),
            Axis, 0, Math.PI * 2);
        rs.TessellateFace(face, mb);
    }
}
