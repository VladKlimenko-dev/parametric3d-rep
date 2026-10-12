using Parametric3D.Kernel.Export;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model;

/// <summary>
/// ОБМЕН НА УРОВНЕ ДЕТАЛИ. Прежде вывод и чтение вызывались только из проверок: ядро умело
/// писать STEP и IGES, а модель об этом не знала. Здесь один вход для всех видов обмена, чтобы
/// приложению не требовалось знать устройство каждого формата.
///
/// Тела детали пишутся все: деталь может состоять из нескольких тел, и терять их при выводе
/// нельзя. При чтении, наоборот, получается ОДНА деталь с телами файла — разделять их по
/// замыслу неоткуда, замысла в файле нет.
/// </summary>
public static class Exchange
{
    /// <summary>Виды обмена, которые умеет ядро.</summary>
    public enum Format
    {
        /// <summary>Точный STEP: поверхности и топология.</summary>
        Step,
        /// <summary>IGES: обрезанные поверхности.</summary>
        Iges,
        /// <summary>Гранёный STL.</summary>
        Stl,
        /// <summary>Гранёный OBJ.</summary>
        Obj
    }

    /// <summary>Записать деталь. Возвращает содержимое файла.</summary>
    public static string Write(Part part, Format format, List<string>? warnings = null)
    {
        var bodies = part.Bodies.ToList();

        if (bodies.Count == 0)
        {
            warnings?.Add("Export: the part has no bodies yet.");
            return "";
        }

        switch (format)
        {
            case Format.Step:
            {
                var w = new StringWriter();
                StepExact.Write(w, bodies, part.Name, warnings);
                return w.ToString();
            }

            case Format.Iges:
                // Все тела детали идут в один файл: сущности просто следуют подряд.
                return IgesSolid.Write(bodies, warnings);

            case Format.Stl:
            {
                var w = new StringWriter();
                MeshExport.WriteStlAscii(w, MeshExport.Triangles(bodies), part.Name);
                return w.ToString();
            }

            case Format.Obj:
            {
                var w = new StringWriter();
                MeshExport.WriteObj(w, MeshExport.Triangles(bodies), part.Name);
                return w.ToString();
            }
        }

        warnings?.Add($"Export: the format {format} is not supported.");
        return "";
    }

    /// <summary>Итог записи сборки: точный ли файл, сколько тел, причины отказа точной записи.</summary>
    public sealed class StepReport
    {
        public bool Exact; public int Bodies; public readonly List<string> Warnings = new();
        public string Summary => Exact ? $"exact B-rep STEP, {Bodies} bodies" : $"NOT written exactly: {string.Join("; ", Warnings)}";
    }

    /// <summary>
    /// KRN-09 (g.11): ТОЧНЫЙ STEP СБОРКИ — каждая деталь со своим размещением (сдвиг, поворот, зеркало), все тела, имена
    /// «деталь/тело N», единицы — мм. Размещение переносится в геометрию точно (TopologyTransform); масштаб и сдвиг осей
    /// (неортонормированная матрица) точно не переносятся — отказ с причиной, а не гранёный файл молча. Прежде интерфейс
    /// писал только ГРАНЁНЫЙ STEP: другая система получала многогранник вместо принятой геометрии.
    /// </summary>
    public static StepReport WriteStep(TextWriter w, IEnumerable<(Part Part, OpenTK.Mathematics.Matrix4d? World)> parts, string name)
    {
        var rep = new StepReport();
        var bodies = new List<TSolid>(); var names = new List<string>();
        foreach (var (part, world) in parts)
        {
            int k = 0;
            foreach (var b in part.Bodies)
            {
                k++;
                var body = b;
                if (world is { } m && !IsIdentity(m))
                {
                    if (!Rigid(m, out var x)) { rep.Warnings.Add($"{part.Name}: the placement scales or shears — not transferable exactly"); return rep; }
                    var t = Kernel.Topology.TopologyTransform.Apply(b, x);
                    if (t == null) { rep.Warnings.Add($"{part.Name}/body {k}: the body cannot be moved exactly (polyhedral form or unsupported curve)"); return rep; }
                    body = t;
                }
                bodies.Add(body); names.Add(part.Bodies.Count == 1 ? part.Name : $"{part.Name}/body {k}");
            }
        }
        if (bodies.Count == 0) { rep.Warnings.Add("no bodies"); return rep; }
        var sw = new StringWriter();
        rep.Exact = StepExact.Write(sw, bodies, name, rep.Warnings, names);
        rep.Bodies = bodies.Count;
        if (rep.Exact) w.Write(sw.ToString());
        return rep;
    }

    static bool IsIdentity(OpenTK.Mathematics.Matrix4d m)
    {
        var id = OpenTK.Mathematics.Matrix4d.Identity;
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (Math.Abs(m[i, j] - id[i, j]) > 1e-15) return false;
        return true;
    }

    /// <summary>Матрица OpenTK (строка-вектор: p' = p·M) — жёсткое движение или зеркало (ортонормированная часть 3×3).</summary>
    static bool Rigid(OpenTK.Mathematics.Matrix4d m, out Kernel.Ops.TransformOp.Xform x)
    {
        var r0 = new OpenTK.Mathematics.Vector3d(m.M11, m.M12, m.M13); var r1 = new OpenTK.Mathematics.Vector3d(m.M21, m.M22, m.M23); var r2 = new OpenTK.Mathematics.Vector3d(m.M31, m.M32, m.M33);
        var t = new OpenTK.Mathematics.Vector3d(m.M41, m.M42, m.M43);
        // размещение интерфейса хранится во float: ортонормированность — с допуском float (1e-5), затем строки
        // приводятся к точно ортонормированным (Грам–Шмидт): переносится задуманное вращение, а не его округление
        bool ortho = Math.Abs(r0.Length - 1) < 1e-5 && Math.Abs(r1.Length - 1) < 1e-5 && Math.Abs(r2.Length - 1) < 1e-5
                     && Math.Abs(OpenTK.Mathematics.Vector3d.Dot(r0, r1)) < 1e-5 && Math.Abs(OpenTK.Mathematics.Vector3d.Dot(r0, r2)) < 1e-5 && Math.Abs(OpenTK.Mathematics.Vector3d.Dot(r1, r2)) < 1e-5
                     && Math.Abs(m.M14) < 1e-9 && Math.Abs(m.M24) < 1e-9 && Math.Abs(m.M34) < 1e-9 && Math.Abs(m.M44 - 1) < 1e-9;
        bool flips = OpenTK.Mathematics.Vector3d.Dot(OpenTK.Mathematics.Vector3d.Cross(r0, r1), r2) < 0;
        r0 = r0.Normalized(); r1 = (r1 - r0 * OpenTK.Mathematics.Vector3d.Dot(r1, r0)).Normalized();
        r2 = OpenTK.Mathematics.Vector3d.Cross(r0, r1) * (flips ? -1 : 1);
        OpenTK.Mathematics.Vector3d D(OpenTK.Mathematics.Vector3d v) => r0 * v.X + r1 * v.Y + r2 * v.Z;
        x = new Kernel.Ops.TransformOp.Xform(p => D(p) + t, D, flips);
        return ortho;
    }

    /// <summary>
    /// Прочитать деталь из файла. Вид определяется по содержимому, а не по имени: имя может быть
    /// каким угодно, а вот заголовок STEP и раздел S у IGES ни с чем не спутать.
    /// </summary>
    public static Part? Read(string text, out Format format, List<string>? warnings = null)
    {
        format = Guess(text);

        List<TSolid>? bodies = format switch
        {
            Format.Step => StepImport.ReadExactSolids(text, warnings) is { Count: > 0 } exact
                           ? exact
                           : StepImport.ReadSolids(text, warnings),
            Format.Iges => IgesImport.Read(text, warnings) is { } one ? new List<TSolid> { one } : null,
            _ => null
        };

        if (bodies == null || bodies.Count == 0)
        {
            warnings?.Add("Import: no bodies were read from the file.");
            return null;
        }

        var part = new Part("импорт");
        foreach (var b in bodies) part.Bodies.Add(b);
        return part;
    }

    /// <summary>Какого вида файл перед нами.</summary>
    public static Format Guess(string text)
    {
        string head = text.Length > 4096 ? text.Substring(0, 4096) : text;

        if (head.Contains("ISO-10303")) return Format.Step;

        // У IGES 73-й знак строки — буква раздела; в начале файла это S или G.
        foreach (string line in head.Split('\n').Take(20))
            if (line.Length >= 73 && (line[72] == 'S' || line[72] == 'G')) return Format.Iges;

        if (head.Contains("solid ") || head.Contains("facet normal")) return Format.Stl;
        if (head.Contains("\nv ") || head.StartsWith("v ")) return Format.Obj;

        return Format.Step;
    }
}
