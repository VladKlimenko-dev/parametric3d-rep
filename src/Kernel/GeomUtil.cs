using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Kernel;

/// <summary>Мелкие геометрические утилиты, используемые везде.</summary>
public static class GeomUtil
{
    public static Vector3 ToF(this Vector3d v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    public static Vector2 ToF(this Vector2d v) => new Vector2((float)v.X, (float)v.Y);
    public static Vector3d ToD(this Vector3 v) => new Vector3d(v.X, v.Y, v.Z);
    public static Matrix4 ToF(this Matrix4d m)
    {
        return new Matrix4(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);
    }

    /// <summary>Знаковая площадь замкнутого 2D-контура (>0 — против часовой стрелки).</summary>
    public static double SignedArea(IReadOnlyList<Vector2d> pts)
    {
        double a = 0;
        int n = pts.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
            a += (pts[j].X * pts[i].Y - pts[i].X * pts[j].Y);
        return a * 0.5;
    }

    /// <summary>Точка внутри 2D-полигона (ray casting).</summary>
    /// <summary>
    /// Есть ли у замкнутой ломаной самопересечения.
    ///
    /// Соседние звенья пропускаются: у них общая точка, и это не пересечение.
    /// Проверка прямая, за O(n²) — контуры эскиза после дискретизации
    /// исчисляются десятками звеньев, ускорять тут нечего.
    /// </summary>
    public static bool SelfIntersects(IReadOnlyList<Vector2d> poly)
    {
        int n = poly.Count;
        if (n < 4) return false;

        for (int i = 0; i < n; i++)
        {
            Vector2d a1 = poly[i], a2 = poly[(i + 1) % n];

            for (int j = i + 1; j < n; j++)
            {
                // Соседние звенья и пара «первое-последнее» имеют общий конец.
                if (j == i || j == (i + 1) % n || (j + 1) % n == i) continue;

                Vector2d b1 = poly[j], b2 = poly[(j + 1) % n];
                if (SegmentsCross(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    /// <summary>Строгое пересечение двух отрезков во внутренних точках.</summary>
    private static bool SegmentsCross(Vector2d a1, Vector2d a2, Vector2d b1, Vector2d b2)
    {
        if (ExactPred) return Robust.Predicates.SegmentsCrossProperly(a1, a2, b1, b2);
        double d1 = Cross2(b2 - b1, a1 - b1);
        double d2 = Cross2(b2 - b1, a2 - b1);
        double d3 = Cross2(a2 - a1, b1 - a1);
        double d4 = Cross2(a2 - a1, b2 - a1);

        // Знаки строго разные с обеих сторон — отрезки пересекаются внутри.
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross2(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
    static readonly bool ExactPred = Environment.GetEnvironmentVariable("P3D_PREDICATES") != "0";

    public static bool PointInPolygon(IReadOnlyList<Vector2d> poly, Vector2d p)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2d a = poly[i], b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y))
            {
                // KRN-02 (02.4): сторона точки от звена — точным предикатом (звено снизу вверх: слева — пересечение луча)
                if (ExactPred)
                {
                    var (lo, hi) = a.Y < b.Y ? (a, b) : (b, a);
                    if (Robust.Predicates.Orient2D(lo, hi, p) > 0) inside = !inside;
                    continue;
                }
                double x = (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X;
                if (p.X < x) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Пересечение двух 2D-прямых, заданных точкой и направлением. null — параллельны.</summary>
    public static Vector2d? LineLine2D(Vector2d p1, Vector2d d1, Vector2d p2, Vector2d d2)
    {
        double den = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(den) < 1e-12) return null;
        Vector2d r = p2 - p1;
        double t = (r.X * d2.Y - r.Y * d2.X) / den;
        return p1 + d1 * t;
    }

    /// <summary>Ближайшая точка на отрезке и параметр t в [0,1].</summary>
    public static Vector3d ClosestOnSegment(Vector3d a, Vector3d b, Vector3d p, out double t)
    {
        Vector3d ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < Tol.Linear * Tol.Linear) { t = 0; return a; }
        t = Vector3d.Dot(p - a, ab) / len2;
        t = Math.Clamp(t, 0.0, 1.0);
        return a + ab * t;
    }

    /// <summary>Пересечение луча с плоскостью. false — луч параллелен плоскости.</summary>
    public static bool RayPlane(Vector3d ro, Vector3d rd, Vector3d po, Vector3d pn, out double t)
    {
        double den = Vector3d.Dot(rd, pn);
        t = 0;
        if (Math.Abs(den) < 1e-12) return false;
        t = Vector3d.Dot(po - ro, pn) / den;
        return true;
    }

    /// <summary>Нормализация угла в [0, 2pi).</summary>
    public static double Norm2Pi(double a)
    {
        const double tau = Math.PI * 2;
        a %= tau;
        if (a < 0) a += tau;
        return a;
    }

    /// <summary>Число сегментов дуги по допуску на стрелку прогиба.</summary>
    /// <summary>Считать станции пути, а не точки сечения: угловое правило тогда не применяется.</summary>
    [ThreadStatic] public static bool PathMode;

    public static int ArcSegments(double radius, double sweepAngle)
    {
        sweepAngle = Math.Abs(sweepAngle);
        if (radius <= Tol.Linear || sweepAngle <= Tol.Angular) return 1;
        double byChord = sweepAngle / (2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, 1.0 - Tol.Chord / radius))));

        // УГЛОВОЕ ПРАВИЛО НЕ ПРИМЕНЯЕТСЯ К ПУТИ ПРОТЯЖКИ. Угол задаёт гладкость СЕЧЕНИЯ резца;
        // станции же вдоль пути достаточно ставить по прогибу. Раньше правила были общими, и
        // при угле 1.5° резец раздувался с 2 190 до 25 911 многоугольников — сечения и станции
        // мельчали разом, число росло как произведение.
        double byAngle = PathMode ? 0 : sweepAngle / Tol.ChordAngle;
        // ОКРУГЛЕНИЕ ВВЕРХ — С ДОПУСКОМ НА ПОСЛЕДНИЙ ЗНАК.
        //
        // ИСПРАВЛЕНО. Шаг 7.5° делит нацело частые углы — 90°, 60°, 45°, 30°:
        // дуга в 60° даёт ровно 8.0. Торец резца и его цилиндрическая сторона
        // строят свои копии одной дуги, и развёртки у них различаются в последнем
        // знаке: одна копия получала 7.9999999 → 8, другая 8.0000001 → 9. Стороны
        // резца не сходились, резец выходил дырявым, и вычитание им рвало тело.
        // Это нашёл проверочный прогон (Parametric3D.Verify). Значение в пределах
        // 1e-9 от целого считается этим целым — у копий одной дуги число отрезков
        // одно.
        double need = Math.Max(byChord, byAngle);
        int n = (int)Math.Ceiling(need - 1e-9 * Math.Max(1, need));
        return Math.Clamp(n, 2, 720);
    }
}
