using System.Diagnostics;
using Parametric3D.Model;

/// <summary>
/// k114 (замечание 12:12): ФАЙЛ С ПОВЕРХНОСТЯМИ. Деталь, записанная на диск, несёт сетку показа: открытая — сразу показана
/// (сетка, объём, габарит те же), тел ещё нет, деталь ждёт перестроения (в окне — фоновое); перестроение заменяет сетку.
/// Файл, изменённый вне программы, — сетка не берётся. Снимки отмены и копии — без сетки. Мутация P3D_FILEDISPLAY=0.
/// </summary>
static partial class Program
{
    static void FileDisplayChecks()
    {
        Check("KRN-FILE-DISPLAY-K114", () =>
        {
            var p = RealisticPart(3, 20, out _); p.Rebuild(null);
            string path = Path.Combine(Path.GetTempPath(), $"p3d_k114_{Guid.NewGuid():N}.p3d");
            try
            {
                DocumentStorage.Save(path, p);
                long size = new FileInfo(path).Length;
                string text = File.ReadAllText(path);
                Require(text.Contains("\"Display\""), "the file has no display mesh");
                Require(!DocumentFile.Save(p).Contains("\"Display\""), "an in-memory copy (undo snapshot) carries the display mesh");
                var sw = Stopwatch.StartNew();
                var (q, _) = DocumentStorage.Load(path);
                double loadMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  file {size / 1024.0:0} KB, open {loadMs:0} ms; cached {q.ShowsCached}, dirty {q.IsDirty}, bodies {q.Bodies.Count}, triangles {q.Mesh?.Indices.Length / 3} (built {p.Mesh!.Indices.Length / 3})");
                Require(q.ShowsCached && q.IsDirty && q.Bodies.Count == 0, "the opened part does not show the stored surfaces");
                Require(q.Mesh!.Indices.Length == p.Mesh.Indices.Length && q.Mesh.Vertices.Length == p.Mesh.Vertices.Length && q.Mesh.EdgePolylines.Count == p.Mesh.EdgePolylines.Count
                        && q.Mesh.FaceRanges.Count == p.Mesh.FaceRanges.Count, "the stored mesh differs");
                Require(Math.Abs(q.Volume - p.Volume) < 1e-9 && (q.Bounds.Min - p.Bounds.Min).Length < 1e-9 && (q.Bounds.Max - p.Bounds.Max).Length < 1e-9, "volume or bounds differ");
                for (int i = 0; i < q.Mesh.Vertices.Length; i += 97)
                    Require(q.Mesh.Vertices[i].P == p.Mesh.Vertices[i].P && q.Mesh.Vertices[i].FaceId == p.Mesh.Vertices[i].FaceId, "vertex differs");
                sw.Restart(); q.Rebuild(null); double buildMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  rebuild {buildMs:0} ms; cached {q.ShowsCached}, volume {q.Volume:0.###} vs {p.Volume:0.###}");
                Require(!q.ShowsCached && q.Bodies.Count > 0 && Math.Abs(q.Volume - p.Volume) < 1e-6 * p.Volume, "the rebuild did not replace the stored surfaces");
                // файл изменили вне программы — сетка не годится
                long rej0 = DocumentFile.DisplayRejected;
                int at = text.IndexOf("\"Distance\": ", StringComparison.Ordinal);
                Require(at > 0, "no distance in the file");
                int end = text.IndexOfAny(new[] { ',', '\n' }, at + 12);
                string edited = text[..(at + 12)] + "7.25" + text[end..];
                var (e, _) = DocumentFile.Load(edited);
                Require(!e.ShowsCached && e.Mesh == null && DocumentFile.DisplayRejected > rej0, "the stored surfaces were used for an edited file");
                // деталь не посчитана (грязная) — сетка не пишется
                p.MarkDirtyFrom(p.Features[^1]);
                Require(!DocumentFile.Save(p, null, withDisplay: true).Contains("\"Display\""), "a stale display mesh was written");
            }
            finally { try { File.Delete(path); } catch { } }
        });
    }
}
