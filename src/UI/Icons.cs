using System.Reflection;

namespace Parametric3D.UI;

/// <summary>
/// Иконки тулбаров и дерева.
///
/// Картинки ВШИТЫ В СБОРКУ как встроенные ресурсы. Раньше они лежали
/// рядом с exe отдельными файлами: достаточно было одной неудачной
/// копии при сборке — и кнопки оставались без картинок, причём молча
/// и не всегда. Встроенный ресурс уехать никуда не может.
///
/// Обращение по «пути»: Get("Toolbar/ok.png"). Он переводится в имя
/// ресурса Parametric3D.Resources.Toolbar.ok.png — так называет ресурсы
/// сборщик, заменяя разделители пути на точки.
///
/// Файл рядом с exe остаётся запасным вариантом: если положить PNG
/// в папку Resources, он перекроет встроенный. Это удобно, когда нужно
/// быстро подменить иконку без пересборки.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, Image?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Assembly _asm = typeof(Icons).Assembly;
    private static string[]? _names;

    /// <summary>Папка с необязательными подменными файлами рядом с исполняемым.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>Все имена встроенных ресурсов-картинок — для диагностики.</summary>
    public static IReadOnlyList<string> EmbeddedNames => _names ??= _asm.GetManifestResourceNames();

    public static Image? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        Image? result = LoadFromFile(path) ?? LoadFromResources(path);
        _cache[path] = result;
        return result;
    }

    /// <summary>Подменный файл рядом с exe. Его может не быть — это норма.</summary>
    private static Image? LoadFromFile(string path)
    {
        try
        {
            string full = Path.Combine(Folder, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;

            // Читаем через поток и копируем в память: Image.FromFile держит
            // файл открытым всё время жизни объекта.
            using var fs = File.OpenRead(full);
            using var tmp = Image.FromStream(fs);
            return new Bitmap(tmp);
        }
        catch
        {
            return null;
        }
    }

    private static Image? LoadFromResources(string path)
    {
        string name = ResourceName(path);
        try
        {
            using Stream? s = _asm.GetManifestResourceStream(name);
            if (s != null)
            {
                using var tmp = Image.FromStream(s);
                return new Bitmap(tmp);
            }

            // Запасной поиск по окончанию имени: помогает, если корневое
            // пространство имён проекта когда-нибудь поменяют.
            string tail = "." + path.Replace('\\', '.').Replace('/', '.');
            foreach (string candidate in EmbeddedNames)
            {
                if (!candidate.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
                using Stream? s2 = _asm.GetManifestResourceStream(candidate);
                if (s2 == null) continue;
                using var tmp2 = Image.FromStream(s2);
                return new Bitmap(tmp2);
            }
        }
        catch
        {
            // Повреждённая картинка не должна валить окно.
        }
        return null;
    }

    private static string ResourceName(string path)
    {
        string clean = path.Replace('\\', '/').TrimStart('/');
        return "Parametric3D.Resources." + clean.Replace('/', '.');
    }

    /// <summary>Иконка, ужатая под заданный размер (для дерева и мелких панелей).</summary>
    public static Image? Scaled(string path, int size)
    {
        string key = path + "@" + size;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Image? src = Get(path);
        Image? result = null;
        if (src != null)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            result = bmp;
        }

        _cache[key] = result;
        return result;
    }
}
