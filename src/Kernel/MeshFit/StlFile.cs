using System.Globalization;
using System.Text;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.MeshFit;

/// <summary>
/// ЧТЕНИЕ STL (k119): двоичный и текстовый вид. Вид определяется по содержимому, а не по слову «solid» в начале:
/// многие программы пишут «solid» в заголовок и двоичного файла. Двоичный узнаётся по длине: 84 + 50·n байт.
/// Координаты — как в файле (единиц в STL нет; масштаб задаёт импорт).
/// </summary>
public static class StlFile
{
    public static List<(Vector3d A, Vector3d B, Vector3d C)> Read(byte[] data)
    {
        if (data.Length >= 84)
        {
            uint n = BitConverter.ToUInt32(data, 80);
            if (84L + 50L * n == data.Length) return ReadBinary(data, n);
        }
        return ReadAscii(Encoding.ASCII.GetString(data));
    }

    public static List<(Vector3d A, Vector3d B, Vector3d C)> ReadFile(string path) => Read(File.ReadAllBytes(path));

    static List<(Vector3d, Vector3d, Vector3d)> ReadBinary(byte[] d, uint n)
    {
        var list = new List<(Vector3d, Vector3d, Vector3d)>((int)n);
        int at = 84;
        Vector3d R(int o) => new(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
        for (uint i = 0; i < n; i++, at += 50) list.Add((R(at + 12), R(at + 24), R(at + 36)));
        return list;
    }

    static List<(Vector3d, Vector3d, Vector3d)> ReadAscii(string text)
    {
        var list = new List<(Vector3d, Vector3d, Vector3d)>();
        var pts = new List<Vector3d>(3);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    pts.Add(new Vector3d(x, y, z));
            }
            else if (line.StartsWith("endloop", StringComparison.OrdinalIgnoreCase))
            {
                if (pts.Count >= 3) for (int k = 1; k + 1 < pts.Count; k++) list.Add((pts[0], pts[k], pts[k + 1]));
                pts.Clear();
            }
        }
        return list;
    }

    /// <summary>Двоичный STL из треугольников (для проверок и вывода).</summary>
    public static byte[] WriteBinary(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new byte[80]);
        w.Write((uint)tris.Count);
        void P(Vector3d v) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
        foreach (var (a, b, c) in tris)
        {
            var n = Vector3d.Cross(b - a, c - a);
            P(n.LengthSquared > 0 ? n.Normalized() : Vector3d.Zero); P(a); P(b); P(c);
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }
}
