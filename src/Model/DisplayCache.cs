using System.IO.Compression;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Model;

/// <summary>
/// k114: СЕТКА ПОКАЗА В ФАЙЛЕ. Двоичная запись MeshData (вершины: позиция, нормаль, грань; треугольники; диапазоны граней;
/// ломаные рёбер; вершины тела; габарит), сжатая deflate. Вид 1.
/// </summary>
public static class DisplayCache
{
    const int Magic = 0x50334450, Ver = 1;   // «P3DP»

    public static byte[] Pack(MeshData md)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(z))
        {
            w.Write(Magic); w.Write(Ver);
            w.Write(md.Vertices.Length);
            foreach (var v in md.Vertices) { V(w, v.P); V(w, v.N); w.Write(v.FaceId); }
            w.Write(md.Indices.Length);
            foreach (int i in md.Indices) w.Write(i);
            w.Write(md.FaceRanges.Count);
            foreach (var (f, r) in md.FaceRanges) { w.Write(f); w.Write(r.VertexStart); w.Write(r.VertexCount); w.Write(r.IndexStart); w.Write(r.IndexCount); }
            w.Write(md.EdgePolylines.Count);
            foreach (var (id, pts) in md.EdgePolylines) { w.Write(id); w.Write(pts.Length); foreach (var p in pts) V(w, p); }
            w.Write(md.Points.Count);
            foreach (var (id, p) in md.Points) { w.Write(id); V(w, p); }
            w.Write(md.Bounds.IsEmpty);
            D(w, md.Bounds.Min); D(w, md.Bounds.Max);
        }
        return ms.ToArray();
    }

    /// <summary>null — не та запись (вид, метка) или числа вне границ.</summary>
    public static MeshData? Unpack(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var z = new DeflateStream(ms, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        if (r.ReadInt32() != Magic || r.ReadInt32() != Ver) return null;
        var md = new MeshData();
        int nv = Count(r);
        md.Vertices = new MeshVertex[nv];
        for (int i = 0; i < nv; i++) md.Vertices[i] = new MeshVertex { P = V(r), N = V(r), FaceId = r.ReadUInt32() };
        int ni = Count(r);
        md.Indices = new int[ni];
        for (int i = 0; i < ni; i++) { int k = r.ReadInt32(); if (k < 0 || k >= nv) return null; md.Indices[i] = k; }
        int nr = Count(r);
        for (int i = 0; i < nr; i++) { uint f = r.ReadUInt32(); md.FaceRanges[f] = new FaceRange { VertexStart = r.ReadInt32(), VertexCount = r.ReadInt32(), IndexStart = r.ReadInt32(), IndexCount = r.ReadInt32() }; }
        int ne = Count(r);
        for (int i = 0; i < ne; i++) { uint id = r.ReadUInt32(); int n = Count(r); var pts = new Vector3[n]; for (int k = 0; k < n; k++) pts[k] = V(r); md.EdgePolylines.Add((id, pts)); }
        int np = Count(r);
        for (int i = 0; i < np; i++) { uint id = r.ReadUInt32(); md.Points.Add((id, V(r))); }
        bool empty = r.ReadBoolean();
        md.Bounds = new Bounds3 { IsEmpty = empty, Min = Dd(r), Max = Dd(r) };
        return md;
    }

    static int Count(BinaryReader r) { int n = r.ReadInt32(); if (n < 0 || n > 200_000_000) throw new InvalidDataException("display cache: bad count"); return n; }
    static void V(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vector3 V(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static void D(BinaryWriter w, Vector3d v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vector3d Dd(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
}
