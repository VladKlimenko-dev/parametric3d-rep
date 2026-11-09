using System.IO.Compression;

namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// k185: СОСТАВНОЙ ДОКУМЕНТ OLE2 (Compound File Binary, MS-CFB) — контейнер SolidWorks до 2015 г. Читаются все потоки по
/// путям «Хранилище/Поток» (как имена записей контейнера 2015+): FAT и DIFAT, каталог, мини-поток для малых потоков
/// (меньше порога, обычно 4096 байт). Потоки «…__ZLB» — [16 байт метки][исходный размер][сжатый размер][zlib]: отдаются
/// распакованными под именем без суффикса. Геометрия — «Contents/Config-N-Partition» того же устройства, что и в 2015+
/// (разделы [длина][метка][исходный][сжатый][zlib]), поэтому дальше работает общий путь SldprtFile.
/// Повреждённый файл даёт InvalidDataException, а не зацикливание: длина каждой цепочки ограничена числом секторов.
/// </summary>
public static class Ole2File
{
    public static bool IsOle2(byte[] b) =>
        b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 && b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1;

    const uint MaxReg = 0xFFFFFFFA, NoStream = 0xFFFFFFFF;

    public static Dictionary<string, byte[]> Streams(byte[] b)
    {
        if (!IsOle2(b) || b.Length < 512) throw new InvalidDataException("OLE2: not a compound file.");
        int sShift = BitConverter.ToUInt16(b, 0x1E), mShift = BitConverter.ToUInt16(b, 0x20);
        if (sShift is not (9 or 12) || mShift != 6) throw new InvalidDataException("OLE2: unsupported sector size.");
        int ss = 1 << sShift, mss = 1 << mShift;
        uint nFat = BitConverter.ToUInt32(b, 0x2C), dir0 = BitConverter.ToUInt32(b, 0x30), cutoff = BitConverter.ToUInt32(b, 0x38);
        uint miniFat0 = BitConverter.ToUInt32(b, 0x3C), nMini = BitConverter.ToUInt32(b, 0x40), difat0 = BitConverter.ToUInt32(b, 0x44), nDifat = BitConverter.ToUInt32(b, 0x48);
        long nSect = (b.Length + ss - 1) / ss;   // верхняя граница длины любой цепочки

        int Off(uint s)
        {
            long o = ((long)s + 1) * ss;
            if (s >= MaxReg || o + ss > b.Length + ss - 1 || o >= b.Length) throw new InvalidDataException("OLE2: sector out of range.");
            return (int)o;
        }
        // DIFAT: 109 записей в заголовке, остальное — цепочка секторов DIFAT
        var difat = new List<uint>();
        for (int k = 0; k < 109; k++) difat.Add(BitConverter.ToUInt32(b, 0x4C + 4 * k));
        uint d = difat0;
        for (uint k = 0; k < nDifat && d < MaxReg; k++)
        {
            int o = Off(d), per = ss / 4;
            for (int j = 0; j < per - 1; j++) difat.Add(BitConverter.ToUInt32(b, o + 4 * j));
            d = BitConverter.ToUInt32(b, o + 4 * (per - 1));
        }
        if (nFat > nSect) throw new InvalidDataException("OLE2: FAT larger than the file.");
        var fat = new List<uint>((int)nFat * ss / 4);
        for (int k = 0; k < nFat && k < difat.Count; k++)
        {
            uint fs = difat[k];
            if (fs >= MaxReg) break;
            int o = Off(fs);
            for (int j = 0; j < ss / 4 && o + 4 * j + 4 <= b.Length; j++) fat.Add(BitConverter.ToUInt32(b, o + 4 * j));
        }
        byte[] Chain(uint s, long limit)
        {
            using var ms = new MemoryStream();
            long n = 0;
            while (s < MaxReg)
            {
                if (++n > nSect || s >= fat.Count) throw new InvalidDataException("OLE2: broken sector chain.");
                int o = Off(s);
                ms.Write(b, o, Math.Min(ss, b.Length - o));
                if (limit >= 0 && ms.Length >= limit) break;
                s = fat[(int)s];
            }
            return ms.ToArray();
        }
        var dir = Chain(dir0, -1);
        int nEnt = dir.Length / 128;
        if (nEnt == 0) throw new InvalidDataException("OLE2: empty directory.");
        var names = new string[nEnt]; var type = new byte[nEnt]; var left = new uint[nEnt]; var right = new uint[nEnt]; var child = new uint[nEnt];
        var start = new uint[nEnt]; var size = new long[nEnt];
        for (int k = 0; k < nEnt; k++)
        {
            int o = k * 128;
            int nl = BitConverter.ToUInt16(dir, o + 64);
            names[k] = System.Text.Encoding.Unicode.GetString(dir, o, Math.Clamp(nl - 2, 0, 62));
            type[k] = dir[o + 66];
            left[k] = BitConverter.ToUInt32(dir, o + 68); right[k] = BitConverter.ToUInt32(dir, o + 72); child[k] = BitConverter.ToUInt32(dir, o + 76);
            start[k] = BitConverter.ToUInt32(dir, o + 116);
            size[k] = sShift == 9 ? BitConverter.ToUInt32(dir, o + 120) : (long)BitConverter.ToUInt64(dir, o + 120);
        }
        // мини-поток: данные — в цепочке корня, размещение — в мини-FAT
        byte[] mini = start[0] < MaxReg ? Chain(start[0], size[0]) : Array.Empty<byte>();
        var miniFat = new List<uint>();
        if (nMini > 0 && miniFat0 < MaxReg)
        {
            var mf = Chain(miniFat0, -1);
            for (int j = 0; j + 4 <= mf.Length; j += 4) miniFat.Add(BitConverter.ToUInt32(mf, j));
        }
        byte[] Content(int k)
        {
            long sz = size[k];
            if (sz < 0 || sz > b.Length * 2L) throw new InvalidDataException("OLE2: stream size out of range.");
            if (sz < cutoff)
            {
                var r = new byte[sz]; long got = 0; uint s = start[k]; long n = 0;
                while (s < MaxReg && got < sz)
                {
                    if (++n > miniFat.Count + 1 || s >= miniFat.Count) throw new InvalidDataException("OLE2: broken mini chain.");
                    long o = (long)s * mss;
                    if (o + mss > mini.Length) throw new InvalidDataException("OLE2: mini sector out of range.");
                    int c = (int)Math.Min(mss, sz - got);
                    Buffer.BlockCopy(mini, (int)o, r, (int)got, c); got += c;
                    s = miniFat[(int)s];
                }
                return r;
            }
            var all = Chain(start[k], sz);
            return all.Length > sz ? all.AsSpan(0, (int)sz).ToArray() : all;
        }
        var res = new Dictionary<string, byte[]>();
        var seen = new HashSet<uint>();
        void Walk(uint i, string prefix)
        {
            if (i == NoStream || i >= nEnt || !seen.Add(i)) return;
            Walk(left[i], prefix); Walk(right[i], prefix);
            string p = prefix.Length == 0 ? names[i] : prefix + "/" + names[i];
            if (type[i] == 1) Walk(child[i], p);
            else if (type[i] == 2)
            {
                var data = Content((int)i);
                if (p.EndsWith("__ZLB", StringComparison.Ordinal))
                {
                    var un = Zlb(data);
                    if (un != null) res.TryAdd(p[..^5], un);
                }
                res.TryAdd(p, data);
            }
        }
        Walk(child[0], "");
        return res;
    }

    /// <summary>Поток «…__ZLB»: [16 байт метки][исходный размер][сжатый размер][zlib]; не того устройства — null.</summary>
    static byte[]? Zlb(byte[] z)
    {
        if (z.Length < 26) return z.Length >= 24 && BitConverter.ToInt32(z, 16) == 0 ? Array.Empty<byte>() : null;
        int us = BitConverter.ToInt32(z, 16), cs = BitConverter.ToInt32(z, 20);
        if (us < 0 || cs <= 0 || 24 + cs > z.Length) return null;
        try
        {
            using var zs = new ZLibStream(new MemoryStream(z, 24, cs), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            zs.CopyTo(ms);
            return ms.Length == us ? ms.ToArray() : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
