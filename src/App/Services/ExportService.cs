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
        // m.x2: IGES — обрезанные поверхности (точная геометрия граней); проверка — чтением обратно (объём)
        if (ext is ".igs" or ".iges")
        {
            var w = new List<string>();
            var bodies = parts.SelectMany(p => p.Bodies).ToList();
            string text = Parametric3D.Kernel.Export.IgesSolid.Write(bodies, w);
            File.WriteAllText(path, text);
            double v0 = parts.Sum(p => p.Volume), vb = double.NaN;
            try
            {
                var back = Parametric3D.Kernel.Export.IgesImport.Read(text, new List<string>());
                if (back != null)
                {
                    if (back.TopologyConvention < Parametric3D.Kernel.Topology.TopologyConventions.Version) Parametric3D.Kernel.Topology.TopologyConventions.Normalize(back);
                    vb = Parametric3D.Kernel.Topology.AnalyticVolume.Of(back) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back));
                }
            }
            catch (Exception) { }
            string fileI = Path.GetFileName(path);
            bool same = Math.Abs(vb - v0) <= 1e-6 * Math.Max(1, Math.Abs(v0));
            return same ? $"Экспортирован IGES ({bodies.Count} тел, {bodies.Sum(b => b.Faces.Count())} граней): {fileI}; прочитан обратно, объём сходится."
                        : $"Экспортирован IGES: {fileI}, но объём прочитанного обратно ({vb:0.###}) не сходится с деталью ({v0:0.###})" + (w.Count > 0 ? ": " + string.Join("; ", w.Take(2)) : ".");
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
