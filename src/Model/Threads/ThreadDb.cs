using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;

namespace Parametric3D.Model.Threads;

/// <summary>
/// ТИП РЕЗЬБЫ из таблицы (threads_db.json / threads_table.csv): обозначение, шаг, диаметры и профиль впадины для
/// винтового выреза — замкнутый многоугольник (z — вдоль оси от середины впадины, r — абсолютный радиус, мм) для наружной
/// и для внутренней резьбы. Профиль выходит за поверхность на 0.15P (перерез — без остаточной плёнки).
/// </summary>
public sealed class ThreadSpec
{
    public string Family = "", Profile = "", Designation = "", Series = "";
    public double DMajor, P, Lead;
    public int Starts = 1;
    /// <summary>Левая по стандарту (hand = LH).</summary>
    public bool StdLeft;
    public double? TapDrill, D1, D3;
    /// <summary>Коническая (1:16): половина угла конуса, градусы.</summary>
    public bool Taper;
    public double HalfConeDeg;
    /// <summary>Профили впадины: X — z вдоль оси, Y — радиус.</summary>
    public Vector2d[] ExtZr = Array.Empty<Vector2d>(), IntZr = Array.Empty<Vector2d>();
    public double ExtStartR, IntStartR;

    /// <summary>Устойчивый ключ типа (пишется в документ).</summary>
    public string Id => Family + "|" + Designation + "|" + Series;
    /// <summary>Подпись в списке: обозначение и серия.</summary>
    public string Display => Series.Length == 0 || Designation.Contains(Series, StringComparison.OrdinalIgnoreCase) ? Designation : Designation + " " + Series;
    /// <summary>Диаметр впадин наружной резьбы (дно профиля).</summary>
    public double ExtRootD => ExtZr.Length > 0 ? 2 * ExtZr.Min(p => p.Y) : D3 ?? DMajor - 1.2269 * P;
    /// <summary>Малый диаметр внутренней резьбы (поверхность отверстия).</summary>
    public double IntMinorD => IntStartR > 0 ? 2 * IntStartR : D1 ?? DMajor - 1.0825 * P;
    public override string ToString() => Display;
}

/// <summary>
/// ТАБЛИЦА РЕЗЬБ: читается один раз из threads_db.json (основная) и threads_table.csv (строки, которых нет в JSON, —
/// профиль трапецией по ширинам и глубине). Файлы ищутся в папке P3D_THREADS, у программы и выше по папкам (корень
/// репозитория при запуске из исходников).
/// </summary>
public static partial class ThreadDb
{
    public const string JsonFile = "threads_db.json", CsvFile = "threads_table.csv";
    private sealed record Catalog(IReadOnlyList<ThreadSpec> All,
        IReadOnlyDictionary<string, ThreadSpec> ById, string? Error);
    // Publish the list, index and diagnostic together, once. No independently mutable
    // static fields or racing index initialization during parallel document rebuilds.
    private static readonly Lazy<Catalog> _catalog = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Все типы (порядок таблицы).</summary>
    public static IReadOnlyList<ThreadSpec> All => _catalog.Value.All;
    /// <summary>Причина, по которой таблица пуста (null — прочитана).</summary>
    public static string? LoadError => _catalog.Value.Error;

    public static ThreadSpec? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _catalog.Value.ById.TryGetValue(id, out var t) ? t : null;
    }

    /// <summary>
    /// Типы для грани диаметра faceD: малый диаметр резьбы (у вала — диаметр впадин, у отверстия — малый диаметр гайки,
    /// −0.05 мм: отверстие под резьбу и есть малый диаметр) меньше диаметра грани. Порядок — лучшие первыми: у вала
    /// ближайший к грани номинал, у отверстия — ближайшее сверло; при равенстве крупный шаг (coarse/UNC) раньше мелкого.
    /// </summary>
    public static List<ThreadSpec> Candidates(double faceD, bool inner)
    {
        const double tol = 0.05;
        IEnumerable<ThreadSpec> q = inner
            ? All.Where(t => t.IntMinorD - tol < faceD)
            : All.Where(t => t.ExtRootD < faceD);
        static int Rank(ThreadSpec t) => t.Series is "coarse" or "UNC" or "parallel" ? 0 : 1;
        return inner
            ? q.OrderBy(t => Math.Round(Math.Abs(faceD - (t.TapDrill ?? t.IntMinorD)), 3)).ThenBy(Rank).ToList()
            : q.OrderBy(t => Math.Round(Math.Abs(faceD - t.DMajor), 3)).ThenBy(Rank).ToList();
    }

    /// <summary>Путь файла таблицы или null.</summary>
    public static string? Locate(string file)
    {
        var dirs = new List<string?> { Environment.GetEnvironmentVariable("P3D_THREADS") };
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && dirs.Count < 9; d = d.Parent) dirs.Add(d.FullName);
        dirs.Add(Environment.CurrentDirectory);
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d)) continue;
            var p = Path.Combine(d, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static Catalog Load()
    {
        var res = new List<ThreadSpec>();
        var errors = new List<string>();
        var json = Locate(JsonFile);
        if (json != null)
            try { res.AddRange(ParseJson(File.ReadAllText(json, Encoding.UTF8))); }
            catch (Exception ex) { errors.Add(JsonFile + ": " + ex.Message); }
        else errors.Add(JsonFile + " not found");
        var csv = Locate(CsvFile);
        if (csv != null)
            try
            {
                var have = new HashSet<string>(res.Select(t => t.Id));
                foreach (var t in ParseCsv(File.ReadAllLines(csv, Encoding.UTF8)))
                    if (have.Add(t.Id)) res.Add(t);
            }
            catch (Exception ex) { errors.Add(CsvFile + ": " + ex.Message); }
        else errors.Add(CsvFile + " not found");
        var error = res.Count == 0 ? "Thread table: " + string.Join("; ", errors) : null;
        var index = res.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        return new Catalog(res.AsReadOnly(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, ThreadSpec>(index), error);
    }
}
