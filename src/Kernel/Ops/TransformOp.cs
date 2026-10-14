using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ПЕРЕНОС ТЕЛА: зеркало, сдвиг, поворот — и построенные на них зеркальное отражение и массивы.
///
/// Копия строится из треугольников граней, но каждой грани переносится и её точная
/// поверхность: плоскость, цилиндр (выдавливание дуги) и другие — по своим опорным элементам.
/// Что перенести не умеем, остаётся без точной поверхности: такая грань гранится по
/// треугольникам, но тело остаётся верным.
/// </summary>
public static class TransformOp
{
    /// <summary>Преобразование точки и признак того, что оно меняет ориентацию (зеркало).</summary>
    public readonly struct Xform
    {
        public readonly Func<Vector3d, Vector3d> Point;
        public readonly Func<Vector3d, Vector3d> Direction;
        public readonly bool Flips;

        public Xform(Func<Vector3d, Vector3d> point, Func<Vector3d, Vector3d> direction, bool flips)
        { Point = point; Direction = direction; Flips = flips; }
    }

    public static Xform Mirror(Vector3d at, Vector3d normal)
    {
        Vector3d n = normal.Normalized();
        return new Xform(p => p - n * (2 * Vector3d.Dot(p - at, n)),
                         d => d - n * (2 * Vector3d.Dot(d, n)), true);
    }

    public static Xform Move(Vector3d by) => new(p => p + by, d => d, false);

    public static Xform Rotate(Vector3d at, Vector3d axis, double degrees)
    {
        Vector3d k = axis.Normalized();
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        Vector3d R(Vector3d v) => v * c + Vector3d.Cross(k, v) * s + k * (Vector3d.Dot(k, v) * (1 - c));
        return new Xform(p => at + R(p - at), R, false);
    }

    /// <summary>Копия тела под преобразованием.</summary>
    static readonly bool CopyOld = Environment.GetEnvironmentVariable("P3D_EXACTCOPY") == "0";
    /// <summary>Разбор: сколько копий сделано точным переносом.</summary>
    public static int ExactCopies;

    public static TSolid Copy(TSolid body, Xform x)
    {
        // KRN-07/g.11: точный перенос графа B-rep (TopologyTransform); не переносится — прежняя пересборка из треугольников.
        // Мутация P3D_EXACTCOPY=0.
        if (!CopyOld && Topology.TopologyTransform.Apply(body, x) is { } exact) { ExactCopies++; return exact; }
        var polys = new List<CsgPolygon>();
        var sources = new Dictionary<uint, ISurface3?>();
        var names = new Dictionary<uint, string>();
        uint tag = 1;

        foreach (TFace f in body.Faces)
        {
            var mb = new Tess.MeshBuilder();
            f.Surface.TessellateFace(f, mb);
            var md = new Tess.MeshData();
            mb.FillInto(md);

            bool any = false;
            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            {
                Vector3d a = x.Point(mb.PositionsD[md.Indices[i]]);
                Vector3d b = x.Point(mb.PositionsD[md.Indices[i + 1]]);
                Vector3d c = x.Point(mb.PositionsD[md.Indices[i + 2]]);
                if (Vector3d.Cross(b - a, c - a).LengthSquared < 1e-24) continue;
                // Зеркало меняет обход: иначе наружная сторона смотрела бы внутрь.
                polys.Add(new CsgPolygon(x.Flips ? new List<Vector3d> { a, c, b } : new List<Vector3d> { a, b, c }, tag));
                any = true;
            }

            if (!any) continue;
            ISurface3? src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            sources[tag] = Surface(src, x);
            // ИМЯ ГРАНИ СОХРАНЯЕТСЯ ПРИ ПЕРЕНОСЕ. Зеркальная копия или элемент массива — это те
            // же грани, только на другом месте; ссылки на них должны работать так же.
            if (f.Tag.Length > 0) names[tag] = f.Tag;
            tag++;
        }

        return BooleanOp.SolidFromPolygons(polys, sources, names);
    }

    /// <summary>Зеркальное отражение тела и объединение с исходным.</summary>
    public static bool MirrorBody(List<TSolid> bodies, Vector3d at, Vector3d normal, List<string> warnings, string? instanceKey = null)
    {
        if (bodies.Count == 0) { warnings.Add("Mirror: no body."); return false; }
        // KRN-07 (07.7): копируются ВСЕ тела детали. Прежде — только первое: у детали из двух тел второе молча оставалось
        // без отражения (измерено: 1500 мм³ вместо 2000). Мутация P3D_PATTERNALL=0.
        foreach (var seed in Seeds(bodies))
            BooleanOp.Unite(bodies, Instance(Copy(seed, Mirror(at, normal)), instanceKey, 1), warnings);
        warnings.Add("Mirror: body mirrored and united.");
        return true;
    }

    // KRN-09 (09.6): цикл копий проверяет отмену — прежде массив из тысяч копий не прерывался. Мутация P3D_PATTERNCANCEL=0.
    static readonly bool CheckpointOff = Environment.GetEnvironmentVariable("P3D_PATTERNCANCEL") == "0";
    static readonly bool AllOff = Environment.GetEnvironmentVariable("P3D_PATTERNALL") == "0";
    /// <summary>Исходные тела для копирования: все тела детали на момент операции (снимок — копии не копируются снова).</summary>
    private static List<TSolid> Seeds(List<TSolid> bodies) => AllOff ? new List<TSolid> { bodies[0] } : bodies.ToList();

    /// <summary>Массив сдвигом: count копий с шагом step (исходное тело считается первым).</summary>
    public static bool LinearPattern(List<TSolid> bodies, Vector3d step, int count, List<string> warnings, string? instanceKey = null)
    {
        if (bodies.Count == 0) { warnings.Add("Pattern: no body."); return false; }
        if (count < 2) { warnings.Add("Pattern: the count must be at least two."); return false; }
        foreach (var seed in Seeds(bodies))
            for (int i = 1; i < count; i++)
            {
                if (!CheckpointOff) KernelContext.Checkpoint("pattern.copy");
                BooleanOp.Unite(bodies, Instance(Copy(seed, Move(step * i)), instanceKey, i), warnings);
            }
        warnings.Add($"Pattern: {count} copies along a line.");
        return true;
    }

    /// <summary>Массив поворотом вокруг оси: count копий на полный оборот.</summary>
    public static bool CircularPattern(List<TSolid> bodies, Vector3d at, Vector3d axis, int count, List<string> warnings, string? instanceKey = null)
    {
        if (bodies.Count == 0) { warnings.Add("Pattern: no body."); return false; }
        if (count < 2) { warnings.Add("Pattern: the count must be at least two."); return false; }
        foreach (var seed in Seeds(bodies))
            for (int i = 1; i < count; i++)
            {
                if (!CheckpointOff) KernelContext.Checkpoint("pattern.copy");
                BooleanOp.Unite(bodies, Instance(Copy(seed, Rotate(at, axis, 360.0 * i / count)), instanceKey, i), warnings);
            }
        warnings.Add($"Pattern: {count} copies around an axis.");
        return true;
    }

    /// <summary>Мутация P3D_INSTNAMES=0: грани копий носят имена исходных граней без номера экземпляра (прежде).</summary>
    static readonly bool InstNamesOff = Environment.GetEnvironmentVariable("P3D_INSTNAMES") == "0";
    /// <summary>Разделитель номера экземпляра в имени грани: «#E/top~P.2» — грань top второй копии массива P.</summary>
    public const char InstanceMark = '~';

    /// <summary>
    /// KRN-07 (07.7): ИДЕНТИЧНОСТЬ ЭКЗЕМПЛЯРОВ. Прежде грани копий носили имена исходных граней, и ссылка на ребро копии
    /// различала экземпляры только точкой: правка числа копий («none of the selected edges could be processed») или шага
    /// массива («ambiguous reference … 14 candidates») рвала её. Теперь грани копии i получают к имени «~ключ.i»: ссылка
    /// находит тот же экземпляр по имени, где бы он ни стоял; исчезнувший экземпляр — явная ошибка ссылки.
    /// </summary>
    static TSolid Instance(TSolid copy, string? key, int i)
    {
        if (key == null || InstNamesOff) return copy;
        foreach (var f in copy.Faces) if (f.Tag.Length > 0) f.Tag = f.Tag + InstanceMark + key + "." + i;
        return copy;
    }

    /// <summary>Имя без номеров экземпляров (роль грани в её операции).</summary>
    public static string WithoutInstance(string tag) { int k = tag.IndexOf(InstanceMark); return k < 0 ? tag : tag[..k]; }

    private static ISurface3? Surface(ISurface3? s, Xform x) => s switch
    {
        PlaneSurface pl => new PlaneSurface(Frame(pl.F, x)),
        ExtrudedSurface ex when Curve(ex.Base, x) is { } b => new ExtrudedSurface(b, x.Direction(ex.Dir)),
        RevolvedSurface rv when Curve(rv.Base, x) is { } b => new RevolvedSurface(b, Frame(rv.Axis, x), rv.A0, rv.A1),
        // Поверхность по сечениям (скругление, фаска): переносятся сами сечения. Формула
        // (процедурный режим) не переносится — она считает проекции на грани исходного тела;
        // копия работает по сечениям, как запасной режим самой поверхности.
        SectionSurface ss => new SectionSurface(ss.Sections.Select(q => Section(q, x)).ToList(), ss.Closed),
        CornerPatchSurface cp => cp.Mapped(x.Point),
        _ => null
    };

    private static SweepSection Section(SweepSection s, Xform x)
    {
        if (!s.IsArc) return SweepSection.Line(x.Point(s.A), x.Point(s.B));
        Vector3d e1 = x.Direction(s.E1).Normalized();
        Vector3d e2 = x.Direction(s.E2).Normalized();
        // Зеркало меняет знак обхода дуги: иначе сечение пойдёт в другую сторону.
        if (x.Flips) e2 = -e2;
        return SweepSection.Arc(x.Point(s.C), e1, e2, s.R, s.Phi);
    }

    private static ICurve3? Curve(ICurve3 c, Xform x) => c switch
    {
        LineCurve3 l => new LineCurve3(x.Point(l.A), x.Point(l.B)),
        ArcCurve3 a => new ArcCurve3(Frame(a.Frame, x), a.Radius, x.Flips ? -a.A0 : a.A0, x.Flips ? -(a.A0 + a.Sweep) : a.A0 + a.Sweep),
        _ => null
    };

    private static Frame3 Frame(Frame3 f, Xform x)
    {
        Vector3d u = x.Direction(f.U).Normalized();
        Vector3d v = x.Direction(f.V).Normalized();
        if (x.Flips) v = -v;                       // зеркало меняет знак обхода в плоскости
        return new Frame3(x.Point(f.Origin), u, v);
    }
}
