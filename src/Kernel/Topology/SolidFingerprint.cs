using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// K03. ОТПЕЧАТОК ТЕЛА — средство доказать, что опубликованное тело не изменилось.
///
/// Снимки и сохранённые состояния дерева разделяют ЭКЗЕМПЛЯРЫ тел, а не копии. Это допустимо
/// только пока опубликованное тело никто не правит на месте. Отпечаток позволяет это проверить:
/// снять до операции, сравнить после — в тестах, а при включённой проверке — и в работе.
///
/// Три уровня, и путать их нельзя:
///   • Authoritative — топология, вершины, кривые рёбер, поверхности и их фасеты, допуски, точное
///     описание и уровень гарантий: то, от чего зависит результат следующей операции;
///   • Derived — производные представления (видимая сетка, ломаные рёбер): законно дописываются
///     позже, но не должны меняться у тела, на которое ссылаются сохранённые состояния;
///   • Geometry — геометрия без номеров элементов: сравнение тел, построенных разными путями
///     (приращением и полным пересчётом), у которых номера элементов неизбежно разные.
/// </summary>
public static class SolidFingerprint
{
    public static string Authoritative(TSolid s)
    {
        using var h = new Hasher();
        h.Add((int)s.Guarantee);
        h.Identity(s.Exact); h.Add(s.Exact?.Solid.Polygons.Count ?? -1);
        h.Identity(s.QExact);
        h.Add(s.Shells.Count);
        foreach (var sh in s.Shells)
        {
            h.Add(sh.Faces.Count); h.Add(sh.IsClosed);
            foreach (var f in sh.Faces)
            {
                h.Add(f.Id); h.Add(f.Reversed); h.Add(f.Tag); h.Add(f.MaterialSlot);
                h.Identity(f.Surface); h.Add(f.Surface.GetType().Name);
                if (f.Surface is FacetedSurface fs)
                {
                    h.Identity(fs.Source); h.Add(fs.Facets.Count);
                    foreach (var (a, b, c) in fs.Facets) { h.Add(a); h.Add(b); h.Add(c); }
                }
                h.Add(f.Loops.Count);
                foreach (var l in f.Loops)
                {
                    h.Add(l.IsOuter); h.Add(l.CoEdges.Count);
                    foreach (var ce in l.CoEdges) { h.Add(ce.Edge.Id); h.Add(ce.Reversed); }
                }
            }
        }
        foreach (var e in s.Edges)
        {
            h.Add(e.Id); h.Add(e.V0.Id); h.Add(e.V1.Id); h.Add(e.Tolerance); h.Add(e.Hidden); h.Add(e.Tag);
            h.Identity(e.Curve);
            h.Add(e.Curve.PointAt(e.Curve.T0)); h.Add(e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2)); h.Add(e.Curve.PointAt(e.Curve.T1));
        }
        foreach (var v in s.Vertices) { h.Add(v.Id); h.Add(v.P); h.Add(v.Tolerance); }
        return h.Done();
    }

    public static string Derived(TSolid s)
    {
        using var h = new Hasher();
        foreach (var f in s.Faces)
            if (f.Surface is FacetedSurface fs)
            {
                h.Identity(fs.Display); h.Add(fs.Display?.Count ?? -1);
                h.Identity(fs.DisplayNormals);
            }
        foreach (var e in s.Edges) { h.Identity(e.PolylineCache); h.Add(e.PolylineCache?.Count ?? -1); }
        return h.Done();
    }

    /// <summary>Геометрия без номеров: треугольники разбиения, упорядоченные канонически, и точный объём.</summary>
    public static string Geometry(TSolid s)
    {
        var mesh = Tess.Tessellator.Tessellate(s, facets: true);
        var tris = new List<string>(mesh.Indices.Length / 3);
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var p = new[] { mesh.Vertices[mesh.Indices[i]].P, mesh.Vertices[mesh.Indices[i + 1]].P, mesh.Vertices[mesh.Indices[i + 2]].P };
            // Каноническое начало обхода: наименьшая вершина; обход сохраняется.
            int k = 0;
            for (int j = 1; j < 3; j++) if (Less(p[j], p[k])) k = j;
            tris.Add(string.Join(";", Enumerable.Range(0, 3).Select(j => { var q = p[(k + j) % 3]; return $"{q.X:R},{q.Y:R},{q.Z:R}"; })));
        }
        tris.Sort(StringComparer.Ordinal);
        using var h = new Hasher();
        foreach (var t in tris) h.Add(t);
        if (s.Exact != null) h.Add(s.Exact.Solid.VolumeMm3().ToString());
        return h.Done();
    }

    private static bool Less(Vector3 a, Vector3 b) =>
        a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    private sealed class Hasher : IDisposable
    {
        private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _b = new byte[8];
        public void Add(long v) { BitConverter.TryWriteBytes(_b, v); _h.AppendData(_b); }
        public void Add(int v) => Add((long)v);
        public void Add(uint v) => Add((long)v);
        public void Add(bool v) => Add(v ? 1L : 0L);
        public void Add(double v) => Add(BitConverter.DoubleToInt64Bits(v));
        public void Add(Vector3d v) { Add(v.X); Add(v.Y); Add(v.Z); }
        public void Add(string? s) { Add(s?.Length ?? -1); if (s != null) _h.AppendData(System.Text.Encoding.UTF8.GetBytes(s)); }
        /// <summary>Тождество объекта: замена его другим (даже равным) считается изменением.</summary>
        public void Identity(object? o) => Add(o == null ? 0 : RuntimeHelpers.GetHashCode(o));
        public string Done() => Convert.ToHexString(_h.GetHashAndReset())[..20];
        public void Dispose() => _h.Dispose();
    }
}
