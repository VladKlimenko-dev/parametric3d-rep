using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// КОНТЕЙНЕР SOLIDWORKS 2015+ (.SLDPRT, .SLDASM). Записи ищутся по метке 14 00 06 00 08 00: за ней тип, контрольная сумма,
/// размер сжатых данных, размер исходных, длина имени, имя (каждый байт — с переставленными полубайтами) и данные в сыром
/// deflate. Повторные записи в конце файла (каталог) данных не несут. Геометрия детали — поток «Contents/Config-N-Partition»:
/// разделы [длина][16 байт метки][исходный размер][сжатый размер][zlib], раздел 0 — Parasolid XT (нейтральный двоичный,
/// «TRANSMIT FILE (partition)»), раздел 1 — изменения (не нужны). Файлы до 2015 г. — составной документ OLE2 (D0 CF 11 E0),
/// они пока не читаются. Устройство восстановлено по файлам (так же его описывает sldprt-re, github.com/RonClemens/sldprt-re).
/// </summary>
public static class SldprtFile
{
    static readonly byte[] Mark = { 0x14, 0x00, 0x06, 0x00, 0x08, 0x00 };

    /// <summary>Все потоки контейнера по именам (первая запись с данными на каждое имя).</summary>
    public static Dictionary<string, byte[]> Streams(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0)
            throw new InvalidDataException("SLDPRT: a SolidWorks file older than 2015 (OLE2 container) is not supported yet — save it in SolidWorks 2015 or newer, or export Parasolid (.x_b/.x_t) or STEP.");
        var res = new Dictionary<string, byte[]>();
        int i = 0;
        while ((i = IndexOf(b, Mark, i)) >= 0)
        {
            if (i + 26 > b.Length) break;
            int csize = BitConverter.ToInt32(b, i + 14), usize = BitConverter.ToInt32(b, i + 18), nl = BitConverter.ToInt32(b, i + 22);
            if (nl > 0 && nl < 300 && csize > 0 && i + 26 + nl + csize <= b.Length && usize >= 0)
            {
                var nameB = new byte[nl];
                for (int k = 0; k < nl; k++) { byte x = b[i + 26 + k]; nameB[k] = (byte)((x >> 4) | (x << 4)); }
                string name = Encoding.Latin1.GetString(nameB);
                if (!res.ContainsKey(name) && name.All(ch => ch >= 32 && ch < 127))
                {
                    try
                    {
                        using var ds = new DeflateStream(new MemoryStream(b, i + 26 + nl, csize), CompressionMode.Decompress);
                        using var ms = new MemoryStream();
                        ds.CopyTo(ms);
                        res[name] = ms.ToArray();
                    }
                    catch (InvalidDataException) { }
                }
            }
            i++;
        }
        return res;
    }

    /// <summary>
    /// Поток Parasolid XT детали: раздел 0 потока «Contents/Config-N-Partition» — конфигурации, отмеченной последней
    /// (swMostRecentConfiguration в swXmlContents/Features), иначе первой найденной.
    /// </summary>
    public static byte[] PartitionXt(byte[] file, List<string>? warnings = null)
    {
        var st = Streams(file);
        var parts = st.Keys.Where(k => Regex.IsMatch(k, @"^Contents/Config-\d+-Partition$")).ToList();
        if (parts.Count == 0) throw new InvalidDataException("SLDPRT: the file has no Parasolid geometry stream (Contents/Config-N-Partition) — is it a part file?");
        string pick = parts[0];
        if (st.TryGetValue("swXmlContents/Features", out var xml))
        {
            var t = Encoding.UTF8.GetString(xml);
            var m = Regex.Match(t, "<swConfiguration [^>]*swID=\"(\\d+)\"[^>]*swMostRecentConfiguration=\"YES\"");
            if (m.Success && parts.Contains($"Contents/Config-{m.Groups[1].Value}-Partition")) pick = $"Contents/Config-{m.Groups[1].Value}-Partition";
        }
        if (parts.Count > 1) warnings?.Add($"SLDPRT: {parts.Count} configurations stored; read {pick}.");
        var u = st[pick];
        if (u.Length < 28) throw new InvalidDataException("SLDPRT: the geometry stream is empty.");
        int csz = BitConverter.ToInt32(u, 24);
        using var zs = new ZLibStream(new MemoryStream(u, 28, Math.Min(csz, u.Length - 28)), CompressionMode.Decompress);
        using var o = new MemoryStream();
        try { zs.CopyTo(o); }
        catch (InvalidDataException) { warnings?.Add("SLDPRT: the geometry stream is cut short; reading what is complete."); }
        return o.ToArray();
    }

    static int IndexOf(byte[] b, byte[] pat, int from)
    {
        for (int i = from; i <= b.Length - pat.Length; i++)
        {
            if (b[i] != pat[0]) { int j = Array.IndexOf(b, pat[0], i + 1); if (j < 0) return -1; i = j - 1; continue; }
            bool ok = true;
            for (int k = 1; k < pat.Length; k++) if (b[i + k] != pat[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
