using Parametric3D.Kernel.Export;
using Parametric3D.Model;

namespace Parametric3D.App.Services;

/// <summary>
/// ЭКСПОРТ ДЕТАЛИ по расширению файла: .step/.stp — точный B-rep (если тело не пишется точно, файл не создаётся и
/// причина показывается); .stl — двоичный STL; .obj — OBJ. Записанная сетка проверяется на замкнутость.
/// Возвращает строку для пользователя.
/// </summary>
public static class ExportService
{
    public static string Export(DocumentSession s, string path)
    {
        var parts = s.Document.AllParts().Where(p => p.Bodies.Count > 0).ToList();
        if (parts.Count == 0) return "Нечего экспортировать: в детали нет тела.";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(path);
        if (ext is ".step" or ".stp")
        {
            var sw = new StringWriter();
            var rep = Exchange.WriteStep(sw, parts.Select(p => (p, (OpenTK.Mathematics.Matrix4d?)null)), name);
            if (!rep.Exact) return "Не записан точный STEP: " + string.Join("; ", rep.Warnings);
            File.WriteAllText(path, sw.ToString());
            return $"Экспортирован точный STEP ({rep.Bodies} тел): {Path.GetFileName(path)}";
        }
        var tris = parts.SelectMany(p => MeshExport.Triangles(p.Bodies)).ToList();
        MeshExport.MeshCheck check;
        if (ext == ".obj")
        {
            using (var w = new StreamWriter(path)) MeshExport.WriteObj(w, tris, name);
            check = MeshExport.Check(tris.Select(t => (t.A, t.B, t.C)).ToList());
        }
        else
        {
            using (var fs = File.Create(path)) MeshExport.WriteStlBinary(fs, tris, name);
            using var rs = File.OpenRead(path);
            check = MeshExport.Check(MeshExport.ReadStlBinary(rs));
        }
        string file = Path.GetFileName(path);
        return check.Holes > 0
            ? $"Экспортировано {check.Triangles} треугольников в {file}, но в сетке {check.Holes} открытых рёбер."
            : $"Экспортировано {check.Triangles} треугольников в {file} (сетка замкнута, объём {check.Volume:0.###} мм³).";
    }
}
