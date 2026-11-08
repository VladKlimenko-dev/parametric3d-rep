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
        var xt = Section(u, 4, warnings);
        // деталь из импортированного тела (элемент «Imported»): в разделе — пустая модель, тело — в потоке
        // «Config-N-FeatureBodies/LocalBodies» (заголовок с именем элемента, затем раздел того же устройства)
        return xt;
    }

    /// <summary>
    /// Все потоки XT детали: раздел конфигурации; если в нём нет граней (деталь из импортированных тел) — каждое тело
    /// потока «Config-N-FeatureBodies/LocalBodies» (корпус 7812K81: 21 тело; прежде читалось только первое).
    /// </summary>
    public static List<byte[]> PartsXt(byte[] file, List<string>? warnings = null)
    {
        var xt = PartitionXt(file, warnings);
        if (HasFaces(xt)) return new List<byte[]> { xt };
        var st = Streams(file);
        var res = new List<byte[]>();
        foreach (var k in st.Keys.Where(k => k.EndsWith("-FeatureBodies/LocalBodies")).OrderBy(k => k, StringComparer.Ordinal))
        {
            var lb = st[k];
            for (int mk = IndexOf(lb, SectionMagic, 0); mk >= 4; mk = IndexOf(lb, SectionMagic, mk + 16))
            {
                var x2 = Section(lb, mk, warnings);
                if (x2.Length > 0 && HasFaces(x2)) res.Add(x2);
                if (LocalAllOff) break;
            }
            if (res.Count > 0) break;
        }
        if (res.Count == 0) return new List<byte[]> { xt };
        warnings?.Add($"SLDPRT: the part is made of {res.Count} imported feature bod{(res.Count == 1 ? "y" : "ies")} (LocalBodies).");
        return res;
    }
    static readonly bool LocalAllOff = Environment.GetEnvironmentVariable("P3D_SLDLOCALALL") == "0";

    static readonly byte[] SectionMagic = { 0x23, 0x1d, 0xd5, 0x71, 0xda, 0x81, 0x48, 0xa2, 0xa8, 0x58, 0x98, 0xb2, 0x1b, 0x89, 0xef, 0x99 };

    /// <summary>
    /// Раздел [длина][16 байт метки][куски: исходный размер, сжатый размер, zlib]… до куска нулевого размера. Большой раздел
    /// (больше 1 МиБ исходных данных) пишется кусками: первый на 1 МиБ, дальше по 4 КиБ — прежде читался только первый, и
    /// поток XT обрывался (клапан: 3 977 граней).
    /// </summary>
    static byte[] Section(byte[] u, int magicAt, List<string>? warnings)
    {
        using var o = new MemoryStream();
        int p = magicAt + 16;
        while (p + 8 <= u.Length)
        {
            int us = BitConverter.ToInt32(u, p), cs = BitConverter.ToInt32(u, p + 4);
            if (us <= 0 || cs <= 0 || p + 8 + cs > u.Length) break;
            try
            {
                using var zs = new ZLibStream(new MemoryStream(u, p + 8, cs), CompressionMode.Decompress);
                zs.CopyTo(o);
            }
            catch (InvalidDataException) { warnings?.Add("SLDPRT: the geometry stream is cut short; reading what is complete."); break; }
            p += 8 + cs;
            if (ChunkedOff) break;
        }
        return o.ToArray();
    }
    static readonly bool ChunkedOff = Environment.GetEnvironmentVariable("P3D_SLDCHUNKS") == "0";

    static bool HasFaces(byte[] xt)
    {
        try { return XtReader.Read(xt).OfType("FACE").Any(); } catch { return false; }
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
