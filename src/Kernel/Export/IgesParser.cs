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

    /// <summary>
    /// m.x4: МНОЖИТЕЛЬ ДЛИНЫ ФАЙЛА К ММ по общему разделу: признак единиц (поле 14: 1 — дюйм, 2 — мм, 3 — по имени, 4 — фут,
    /// 5 — миля, 6 — м, 7 — км, 8 — мил, 9 — мкм, 10 — см, 11 — микродюйм), имя (поле 15) и масштаб модели (поле 13: модель =
    /// мир × масштаб). Прежде единицы не читались: файл в дюймах давал деталь в 25.4 раза меньше. Мутация P3D_IGESUNITS=0.
    /// </summary>
    public double LengthScale = 1;
    public string? LengthUnitNote;
    static readonly bool UnitsOff = Environment.GetEnvironmentVariable("P3D_IGESUNITS") == "0";

    static List<string> GlobalFields(string g)
    {
        // поля общего раздела: разделитель и конец записи — первые два поля («1H,», «1H;»); строки — Холлерит nH…
        char delim = ',', end = ';';
        var res = new List<string>(); int i = 0;
        string Read()
        {
            int j = i; while (j < g.Length && char.IsDigit(g[j])) j++;
            if (j < g.Length && j > i && (g[j] == 'H' || g[j] == 'h'))
            {
                int n = int.Parse(g[i..j]); string str = g.Substring(j + 1, Math.Min(n, g.Length - j - 1)); i = j + 1 + n; return str;
            }
            int k = i; while (k < g.Length && g[k] != delim && g[k] != end) k++;
            string v = g[i..k].Trim(); i = k; return v;
        }
        for (int f = 0; i < g.Length && f < 30; f++)
        {
            string v = Read();
            if (f == 0 && v.Length == 1) delim = v[0];
            if (f == 1 && v.Length == 1) end = v[0];
            res.Add(v);
            if (i < g.Length && g[i] == end) break;
            if (i < g.Length && g[i] == delim) i++;
        }
        return res;
    }

    static double? UnitByName(string n) => n.Trim().ToUpperInvariant() switch
    {
        "IN" or "INCH" => 25.4, "MM" => 1, "FT" => 304.8, "MI" => 1609344, "M" => 1000, "KM" => 1e6, "MIL" => 0.0254,
        "UM" => 1e-3, "CM" => 10, "UIN" => 2.54e-5, _ => null,
    };

    static void ScaleLengths(int type, List<double> d, double k)
    {
        void S(int from, int count) { for (int i = from; i < from + count && i < d.Count; i++) d[i] *= k; }
        switch (type)
        {
            case IgesFile.TypeLine: S(0, 6); break;
            case IgesFile.TypeArc: S(0, 7); break;
            case 116: S(0, 3); break;                                           // точка
            case IgesFile.TypePlane: S(3, 1); break;                            // A·x + B·y + C·z = D
            case IgesFile.TypeTabulatedCylinder: S(1, 3); break;
            case 124: S(3, 1); S(7, 1); S(11, 1); break;                        // перенос матрицы
            case IgesFile.TypeSpline:
                if (d.Count >= 2) { int n = (int)d[0], kk = (int)d[1], pts = n + 1, knots = n + kk + 2; S(6 + knots + pts, pts * 3); }
                break;
            case IgesFile.TypeSplineSurface:
                if (d.Count >= 4) { int k1 = (int)d[0], k2 = (int)d[1], m1 = (int)d[2], m2 = (int)d[3], nu = k1 + 1, nv = k2 + 1; S(9 + (k1 + m1 + 2) + (k2 + m2 + 2) + nu * nv, nu * nv * 3); }
                break;
        }
    }

    public static IgesParser Parse(string text)
    {
        var res = new IgesParser();

        var dir = new List<string>();
        var par = new List<string>();
        var glob = new List<string>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 73) continue;

            char section = line[72];
            if (section == 'D') dir.Add(line);
            else if (section == 'P') par.Add(line);
            else if (section == 'G') glob.Add(line.Substring(0, 72));
            else if (section == 'S' && line.Contains("Parametric3D exact geometry") && !line.Contains("DE pointers")) res.LegacyPointers = true;
        }

        if (!UnitsOff && glob.Count > 0)
        {
            var gf = GlobalFields(string.Concat(glob));
            double scale = gf.Count > 12 && double.TryParse(gf[12].Replace('D', 'E'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) && sc > 0 ? sc : 1;
            int flag = gf.Count > 13 && int.TryParse(gf[13], out var fl) ? fl : 2;
            string uname = gf.Count > 14 ? gf[14] : "";
            double? unit = flag switch { 1 => 25.4, 2 => 1, 3 => UnitByName(uname), 4 => 304.8, 5 => 1609344, 6 => 1000, 7 => 1e6, 8 => 0.0254, 9 => 1e-3, 10 => 10, 11 => 2.54e-5, _ => null };
            if (unit == null) res.LengthUnitNote = $"IGES: unknown units (flag {flag}, name '{uname}') — read as millimetres";
            else
            {
                res.LengthScale = unit.Value / scale;
                if (res.LengthScale != 1) res.LengthUnitNote = $"IGES: units {(uname.Length > 0 ? uname : "flag " + flag)}, model scale {scale} — lengths × {res.LengthScale} to mm";
            }
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

            if (res.LengthScale != 1) ScaleLengths(type, data, res.LengthScale);
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
