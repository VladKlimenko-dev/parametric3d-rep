using System.Text;

namespace Parametric3D.Model;

/// <summary>
/// K10. ЗАПИСЬ И ЧТЕНИЕ ДОКУМЕНТА НА ДИСК.
///
/// Запись атомарна: текст пишется во временный файл рядом, сбрасывается на диск, и только затем
/// заменяет прежний (прежний остаётся как .bak). Обрыв записи (сбой, нехватка места, отключение
/// питания) оставляет либо старый файл, либо новый — но не половину. Чтение ограничено по размеру
/// и строгое (DocumentFile.Load): повреждённый или неполный файл не даёт частично принятой модели.
/// </summary>
public static class DocumentStorage
{
    /// <summary>Предел размера документа, байт.</summary>
    public static long MaxBytes = 256L * 1024 * 1024;

    /// <summary>k114: файл — с поверхностями (сетка показа): открывается сразу, перестроение — в фоне.</summary>
    public static void Save(string path, Part part, Params.ParameterTable? parameters = null) =>
        WriteAtomic(path, DocumentFile.Save(part, parameters, withDisplay: true));

    public static (Part Part, Params.ParameterTable Parameters) Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("document not found", path);
        if (info.Length > MaxBytes) throw new InvalidDataException($"document is larger than {MaxBytes} bytes");
        return DocumentFile.Load(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Атомарная запись текста; при ошибке прежний файл не тронут, временный удалён.</summary>
    public static void WriteAtomic(string path, string text)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(full)) File.Replace(tmp, full, full + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, full);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
        }
    }
}
