using System.Globalization;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ЧТЕНИЕ IGES. Разбор строится на том же устройстве, что и запись: строки по 80 знаков, буква
/// раздела в 73-м знаке, данные сущности — в разделе P, а её вид и ссылка на данные — в разделе D.
///
/// Разбор нарочно терпимый к мелочам, которыми файлы разных систем отличаются: лишние пробелы,
/// разделители в конце строки, перевод строки с возвратом каретки. Но НЕ терпимый к сути: если
/// вид сущности не разобран, она возвращается как есть, а не подменяется чем-то похожим.
/// </summary>
public sealed class IgesParser
{
    /// <summary>Разобранная сущность: вид, её числа и имя из раздела описаний.</summary>
    public readonly record struct Entity(int Type, IReadOnlyList<double> Data, string Label = "", int Matrix = 0);

    /// <summary>
    /// Старые файлы Parametric3D (до K10) ссылались порядковыми номерами сущностей, по стандарту
    /// же указатель — номер строки описания (2i − 1). Признак старого файла — его приветствие.
    /// </summary>
    public bool LegacyPointers;

    /// <summary>Сущность по указателю из файла.</summary>
    public Entity? At(int pointer)
    {
        int i = LegacyPointers ? pointer : (pointer + 1) / 2;
        return i >= 1 && i <= Entities.Count ? Entities[i - 1] : null;
    }

    public readonly List<Entity> Entities = new();

    public static IgesParser Parse(string text)
    {
        var res = new IgesParser();

        var dir = new List<string>();
        var par = new List<string>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 73) continue;

            char section = line[72];
            if (section == 'D') dir.Add(line);
            else if (section == 'P') par.Add(line);
            else if (section == 'S' && line.Contains("Parametric3D exact geometry") && !line.Contains("DE pointers")) res.LegacyPointers = true;
        }

        // ИМЕНА БЕРУТСЯ ИЗ РАЗДЕЛА ОПИСАНИЙ. У каждой сущности там две строки, и имя лежит в
        // восьми знаках предпоследнего поля второй строки. Без этого подписи граней терялись бы
        // при чтении, хотя в файле они есть.
        var labels = new List<string>();
        var matrices = new List<int>();
        for (int i = 1; i < dir.Count; i += 2)
        {
            string line = dir[i];
            string label = line.Length >= 64 ? line.Substring(56, 8).Trim() : "";
            labels.Add(label);
            // Поле 7 первой строки описания — указатель матрицы преобразования (124).
            string first = dir[i - 1];
            matrices.Add(first.Length >= 56 && int.TryParse(first.Substring(48, 8).Trim(), out int mp) ? mp : 0);
        }

        // Данные сущностей идут подряд и разделены точкой с запятой.
        string all = string.Concat(par.Select(l => l.Length >= 64 ? l.Substring(0, 64) : l));

        foreach (string chunk in all.Split(';'))
        {
            string body = chunk.Trim();
            if (body.Length == 0) continue;

            var parts = body.Split(',');
            if (!int.TryParse(parts[0].Trim(), out int type)) continue;

            var data = new List<double>();
            for (int i = 1; i < parts.Length; i++)
            {
                string v = parts[i].Trim();
                if (v.Length == 0) continue;
                // Фортран-запись показателя «1.0D3» — у многих систем; приводится к «E».
                v = v.Replace('D', 'E').Replace('d', 'e');
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) data.Add(d);
            }

            string label = res.Entities.Count < labels.Count ? labels[res.Entities.Count] : "";
            int matrix = res.Entities.Count < matrices.Count ? matrices[res.Entities.Count] : 0;
            res.Entities.Add(new Entity(type, data, label, matrix));
        }

        return res;
    }

    /// <summary>Отрезок по сущности 110.</summary>
    public static (Vector3d A, Vector3d B)? Line(Entity e) =>
        e.Type == IgesFile.TypeLine && e.Data.Count >= 6
            ? (new Vector3d(e.Data[0], e.Data[1], e.Data[2]), new Vector3d(e.Data[3], e.Data[4], e.Data[5]))
            : null;

    /// <summary>
    /// Дуга по сущности 100. В файле она лежит в плоскости z = const, и это отражено прямо:
    /// первое число — высота плоскости, дальше центр и концы в её координатах.
    /// </summary>
    public static ArcCurve3? Arc(Entity e)
    {
        if (e.Type != IgesFile.TypeArc || e.Data.Count < 7) return null;

        double z = e.Data[0];
        var centre = new Vector3d(e.Data[1], e.Data[2], z);
        var start = new Vector3d(e.Data[3], e.Data[4], z);
        var end = new Vector3d(e.Data[5], e.Data[6], z);

        double radius = (start - centre).Length;
        if (radius <= Core.Tol.Weld) return null;

        var frame = new Frame3(centre, Vector3d.UnitX, Vector3d.UnitY);
        double a0 = Math.Atan2(start.Y - centre.Y, start.X - centre.X);
        double a1 = Math.Atan2(end.Y - centre.Y, end.X - centre.X);

        // Дуга в IGES идёт против часовой стрелки; замкнутая записана совпадающими концами.
        if (a1 <= a0 + 1e-12) a1 += 2 * Math.PI;

        return new ArcCurve3(frame, radius, a0, a1);
    }

    /// <summary>Плоскость по сущности 108: нормаль и точка на ней.</summary>
    public static (Vector3d Normal, double D)? Plane(Entity e) =>
        e.Type == IgesFile.TypePlane && e.Data.Count >= 4
            ? (new Vector3d(e.Data[0], e.Data[1], e.Data[2]), e.Data[3])
            : null;

    /// <summary>Поверхность выдавливания по сущности 122: номер образующей и вектор переноса.</summary>
    public static (int BaseCurve, Vector3d Dir)? Extruded(Entity e) =>
        e.Type == IgesFile.TypeTabulatedCylinder && e.Data.Count >= 4
            ? ((int)e.Data[0], new Vector3d(e.Data[1], e.Data[2], e.Data[3]))
            : null;

    /// <summary>Поверхность вращения по сущности 120.</summary>
    public static (int Axis, int Generatrix, double A0, double A1)? Revolved(Entity e) =>
        e.Type == IgesFile.TypeSurfaceOfRevolution && e.Data.Count >= 4
            ? ((int)e.Data[0], (int)e.Data[1], e.Data[2], e.Data[3])
            : null;

    /// <summary>
    /// Сплайновая поверхность по сущности 128. Порядок чисел тот же, что при записи: степени,
    /// число точек, признаки, узлы по обоим направлениям, веса, сетка точек.
    /// </summary>
    public static Surfaces.NurbsSurface? SplineSurface(Entity e)
    {
        if (e.Type != IgesFile.TypeSplineSurface || e.Data.Count < 12) return null;

        int k1 = (int)e.Data[0], k2 = (int)e.Data[1];
        int m1 = (int)e.Data[2], m2 = (int)e.Data[3];

        int nu = k1 + 1, nv = k2 + 1;
        int knotsU = k1 + m1 + 2, knotsV = k2 + m2 + 2;

        int at = 9;
        if (e.Data.Count < at + knotsU + knotsV + nu * nv * 4) return null;

        var ku = new double[knotsU];
        for (int i = 0; i < knotsU; i++) ku[i] = e.Data[at++];

        var kv = new double[knotsV];
        for (int i = 0; i < knotsV; i++) kv[i] = e.Data[at++];

        var w = new double[nu, nv];
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++) w[i, j] = e.Data[at++];

        var cp = new Vector3d[nu, nv];
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                cp[i, j] = new Vector3d(e.Data[at], e.Data[at + 1], e.Data[at + 2]);
                at += 3;
            }

        // Узлы приводятся к 0…1: наша поверхность работает в этих пределах.
        static double[] Norm(double[] k)
        {
            double lo = k[0], hi = k[^1];
            return hi - lo <= 0 ? k : k.Select(x => (x - lo) / (hi - lo)).ToArray();
        }

        try { return new Surfaces.NurbsSurface(cp, m1, m2, w, Norm(ku), Norm(kv)); }
        catch { return null; }
    }

    /// <summary>Составной контур по сущности 102: номера кривых, идущих подряд.</summary>
    public static IReadOnlyList<int>? Composite(Entity e)
    {
        if (e.Type != IgesFile.TypeComposite || e.Data.Count < 1) return null;

        int n = (int)e.Data[0];
        var res = new List<int>();
        for (int i = 0; i < n && 1 + i < e.Data.Count; i++) res.Add((int)e.Data[1 + i]);
        return res;
    }

    /// <summary>Обрезанная поверхность по сущности 144: что обрезано и чем.</summary>
    public static (int Surface, int Outer, IReadOnlyList<int> Holes)? Trimmed(Entity e)
    {
        if (e.Type != IgesFile.TypeTrimmedSurface || e.Data.Count < 4) return null;

        int surface = (int)e.Data[0];
        int holeCount = (int)e.Data[2];
        int outer = (int)e.Data[3];

        var holes = new List<int>();
        for (int i = 0; i < holeCount && 4 + i < e.Data.Count; i++) holes.Add((int)e.Data[4 + i]);

        return (surface, outer, holes);
    }

    /// <summary>Сплайн по сущности 126.</summary>
    public static SplineCurve3? Spline(Entity e)
    {
        if (e.Type != IgesFile.TypeSpline || e.Data.Count < 10) return null;

        int n = (int)e.Data[0];
        int k = (int)e.Data[1];
        int points = n + 1;
        int knots = n + k + 2;

        int at = 6;
        if (e.Data.Count < at + knots + points + points * 3) return null;

        var knotValues = new List<double>();
        for (int i = 0; i < knots; i++) knotValues.Add(e.Data[at++]);

        var weights = new List<double>();
        for (int i = 0; i < points; i++) weights.Add(e.Data[at++]);

        var cp = new List<Vector3d>();
        for (int i = 0; i < points; i++)
        {
            cp.Add(new Vector3d(e.Data[at], e.Data[at + 1], e.Data[at + 2]));
            at += 3;
        }

        try { return new SplineCurve3(cp, k, weights, knotValues); }
        catch { return null; }
    }
}
