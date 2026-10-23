using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// Ссылка на ребро тела ПО ГЕОМЕТРИИ: две концевые точки.
///
/// Номера рёбер выдаются заново при каждой перестройке — тело строится
/// с нуля, и уцелевшее на вид ребро получает другой номер. Концы остаются
/// на месте, пока на месте само ребро, поэтому операция, запомнившая номер,
/// разваливается при первом изменении эскиза, а запомнившая концы — нет.
///
/// СОВПАДЕНИЕ КОНЦОВ — НЕ ЕДИНСТВЕННЫЙ СПОСОБ НАЙТИ РЕБРО. Соседняя фаска
/// укорачивает ребро, не делая его другим ребром, и тогда ищут по опорной
/// прямой с перекрытием (см. FilletOp.Resolve). Здесь лежит только точное
/// сравнение концов — остальное зависит от операции.
/// </summary>
public readonly struct EdgeRef
{
    public readonly Vector3d Start, End;

    /// <summary>
    /// Средняя точка ребра — необязательна. Нужна, когда у нескольких рёбер одни и те же
    /// концы: две дуги одной окружности, край отверстия, разрезанный гранью в двух точках.
    /// Прежде ссылка хранила только концы, и поиск брал первое подходящее ребро — фаска
    /// ложилась не на выбранную кромку, а на соседнюю (нашёл проверочный прогон).
    /// </summary>
    public readonly Vector3d? Mid;

    /// <summary>
    /// ПОДПИСИ ДВУХ ГРАНЕЙ РЕБРА — устойчивое имя. Координаты уезжают при первом же изменении
    /// размеров эскиза, а пара «чем созданы соседние грани» не меняется: боковая грань от k-й
    /// кривой контура остаётся ею при любом размере. Поиск сперва идёт по подписям, координаты
    /// остаются запасным путём (и единственным для старых ссылок, где подписей нет).
    /// </summary>
    public readonly string? FaceA, FaceB;

    /// <summary>
    /// РАЗЛИЧИТЕЛЬ КОНЦОВ. Пара граней часто отвечает нескольким рёбрам сразу (измерено: 48%
    /// пар), и выбор падал обратно на координаты. Набор подписей граней, сходящихся в конце
    /// ребра, различает куски одного имени — и сам не зависит от размеров.
    /// </summary>
    public readonly string? End0, End1;

    /// <summary>
    /// ИМЯ КУСКА: «пара подписей граней#номер». Куски одного исходного ребра различаются только
    /// им — наборы граней в их концах одинаковы (проверено измерением). Номер верен, пока число
    /// кусков не изменилось, поэтому служит предпочтением, а не условием.
    /// </summary>
    public readonly string? Piece;

    /// <summary>Подписи граней вокруг вершины — по алфавиту, через точку с запятой.</summary>
    static readonly bool EndKeyUnknownOff = Environment.GetEnvironmentVariable("P3D_ENDKEYUNKNOWN") == "0";
    public static string EndKey(TVertex v)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in v.Edges)
        {
            if (e.FaceA is { Tag.Length: > 0 } fa) tags.Add(fa.Tag);
            if (e.FaceB is { Tag.Length: > 0 } fb) tags.Add(fb.Tag);
        }
        return string.Join(";", tags);
    }

    /// <summary>Ссылка на ребро: концы, середина и подписи соседних граней.</summary>
    private static string BareKey(string key) =>
        string.Join(";", new SortedSet<string>(key.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Bare), StringComparer.Ordinal));

    public static EdgeRef Of(TEdge e) =>
        new(e.V0.P, e.V1.P, e.Curve.PointAt(0.5), e.FaceA?.Tag, e.FaceB?.Tag, EndKey(e.V0), EndKey(e.V1), e.Tag);

    public EdgeRef(Vector3d start, Vector3d end, Vector3d? mid = null, string? faceA = null, string? faceB = null,
                   string? end0 = null, string? end1 = null, string? piece = null)
    {
        Piece = string.IsNullOrEmpty(piece) ? null : piece;
        Start = start; End = end; Mid = mid;
        FaceA = string.IsNullOrEmpty(faceA) ? null : Canon(faceA);
        FaceB = string.IsNullOrEmpty(faceB) ? null : Canon(faceB);
        End0 = string.IsNullOrEmpty(end0) ? null : CanonKey(end0);
        End1 = string.IsNullOrEmpty(end1) ? null : CanonKey(end1);
    }

    /// <summary>Мутация P3D_HOLEALIAS=0: ссылки из документов с прежними именами граней отверстия не переводятся.</summary>
    static readonly bool HoleAliasOff = Environment.GetEnvironmentVariable("P3D_HOLEALIAS") == "0";
    /// <summary>
    /// KRN-07 (07.2): прежние имена граней отверстия по маршруту → единые роли («quadric:hole», «exact:hole-wall»,
    /// «cross:hole» → «hole:wall» и т.д.). Ссылки, сохранённые в документах до единого контракта, находят те же грани.
    /// Имена граней резца общей булевой («extrude[0]:side[0.0]») не переводятся: по ним не отличить отверстие от выдавливания.
    /// </summary>
    static readonly Dictionary<string, string> HoleAliases = new(StringComparer.Ordinal)
    {
        ["quadric:hole"] = "hole:wall", ["exact:hole-wall"] = "hole:wall", ["cross:hole"] = "hole:wall",
        ["quadric:hole-bottom"] = "hole:bottom", ["exact:hole-bottom"] = "hole:bottom",
        ["quadric:counterbore"] = "hole:counterbore", ["quadric:counterbore-bottom"] = "hole:counterbore-bottom",
        ["quadric:countersink"] = "hole:countersink",
    };
    public static string Canon(string tag)
    {
        if (HoleAliasOff) return tag;
        string bare = Bare(tag);
        if (!HoleAliases.TryGetValue(bare, out var c)) return tag;
        return tag.Length > bare.Length ? tag[..(tag.Length - bare.Length)] + c : c;
    }
    static string CanonKey(string key) => HoleAliasOff || !key.Contains(':') ? key
        : string.Join(";", new SortedSet<string>(key.Split(';').Select(Canon), StringComparer.Ordinal));

    /// <summary>Подписи известны и у ссылки, и у ребра — и совпадают (в любом порядке).</summary>
    /// <summary>
    /// K05. Подпись без префикса операции «#ключ/». Ссылки из документов до K05 хранят подписи
    /// без префикса; они сравниваются с подписями граней без него (поведение прежних версий).
    /// </summary>
    public static string Bare(string tag)
    {
        if (tag.Length == 0 || tag[0] != '#') return tag;
        int slash = tag.IndexOf('/');
        return slash < 0 ? tag : tag[(slash + 1)..];
    }

    private bool Legacy => FaceA != null && FaceA[0] != '#';

    /// <summary>Мутация P3D_TAGROLES=0: подписи граней сравниваются как есть (без ролей путей построения).</summary>
    static readonly bool RolesOff = Environment.GetEnvironmentVariable("P3D_TAGROLES") == "0";
    /// <summary>
    /// k104: РОЛЬ ГРАНИ НЕ ЗАВИСИТ ОТ ПУТИ ПОСТРОЕНИЯ. Выдавливание по окружности строится точным путём (стенка «quadric:hole» /
    /// «quadric:boss», дно «quadric:hole-bottom», торец «quadric:boss-top») или общим (стенка «extrude[0]:side[0.0]», торец
    /// «extrude[0]:top») — смотря по тому, что было в теле до неё. После перестановки операций в дереве путь менялся, и
    /// скругление или фаска на кромке бобышки/отверстия теряли ребро («the edge … no longer exists»). Подписи одной операции
    /// (одинаковый префикс «#ключ/») сравниваются по роли: стенка и торец. У одной операции из одной окружности не бывает
    /// и стенки «quadric», и боковой «extrude[0]:side[0.0]» сразу, поэтому смешения нет.
    /// </summary>
    public static string Role(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return tag ?? "";
        if (RolesOff) return tag;
        string c = Canon(tag);
        string bare = Bare(c);
        string prefix = c.Length > bare.Length ? c[..(c.Length - bare.Length)] : "";
        string? role = bare switch
        {
            "hole:wall" or "quadric:hole" or "quadric:boss" or "extrude[0]:side[0.0]" => "@wall",
            "hole:bottom" or "quadric:hole-bottom" or "quadric:boss-top" or "extrude[0]:top" => "@cap",
            _ => null,
        };
        return role == null ? c : prefix + role;
    }
    static string RoleKey(string key) => string.Join(";", new SortedSet<string>(key.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Role), StringComparer.Ordinal));

    /// <summary>Только грани ребра (без концов).</summary>
    public bool FacesMatch(TEdge e)
    {
        if (FaceA == null || FaceB == null) return false;
        string? a = e.FaceA?.Tag, b = e.FaceB?.Tag;
        if (Legacy) { a = a == null ? null : Bare(a); b = b == null ? null : Bare(b); }
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return Same(a, b);
    }

    /// <summary>Грани ребра a, b — те же, что у ссылки (по ролям, в любом порядке).</summary>
    private bool Same(string a, string b)
    {
        if ((FaceA == a && FaceB == b) || (FaceA == b && FaceB == a)) return true;
        string ra = Role(FaceA), rb = Role(FaceB), ea = Role(a), eb = Role(b);
        return (ra == ea && rb == eb) || (ra == eb && rb == ea);
    }

    public bool NamesMatch(TEdge e)
    {
        if (FaceA == null || FaceB == null) return false;
        string? a = e.FaceA?.Tag, b = e.FaceB?.Tag;
        if (Legacy) { a = a == null ? null : Bare(a); b = b == null ? null : Bare(b); }
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (!Same(a, b)) return false;
        // НОМЕР КУСКА ЗДЕСЬ НЕ ПРОВЕРЯЕТСЯ. Он сдвигается, если новая операция разрежет ребро
        // ещё раз: измерено, что ссылка на кусок при x=80 после второго реза уверенно находила
        // кусок при x=30. Номер используется как ПРЕДПОЧТЕНИЕ при выборе (см. FilletOp.Resolve),
        // а не как условие совпадения.
        if (End0 == null || End1 == null) return true;          // старая ссылка: только по граням

        string e0 = EndKey(e.V0), e1 = EndKey(e.V1);
        // g.09: смежность «вершина → рёбра» ведут не все построители тела (скругление, булевы): пустой ключ конца — «не
        // известно», а не «другие грани»; совпадение — по граням ребра (выбор между кусками — по точке). Прежде фаска ребра у
        // противоположного угла теряла ссылку после скругления другого угла («the edge ... no longer exists»,
        // KRN08-LONG-HISTORY-EDITS). Мутация P3D_ENDKEYUNKNOWN=0.
        if (!EndKeyUnknownOff && (e0.Length == 0 || e1.Length == 0)) return true;
        if (Legacy) { e0 = BareKey(e0); e1 = BareKey(e1); }
        if ((End0 == e0 && End1 == e1) || (End0 == e1 && End1 == e0)) return true;
        if (RolesOff) return false;
        string r0 = RoleKey(End0), r1 = RoleKey(End1), k0 = RoleKey(e0), k1 = RoleKey(e1);
        return (r0 == k0 && r1 == k1) || (r0 == k1 && r1 == k0);
    }

    /// <summary>Те же концы, в любом порядке: направление ребра не хранится.</summary>
    public bool Matches(Vector3d a, Vector3d b, double tol)
    {
        double t2 = tol * tol;

        return ((Start - a).LengthSquared <= t2 && (End - b).LengthSquared <= t2)
            || ((Start - b).LengthSquared <= t2 && (End - a).LengthSquared <= t2);
    }
}
