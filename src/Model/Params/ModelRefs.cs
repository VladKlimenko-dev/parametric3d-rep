using System.Reflection;
using System.Text.RegularExpressions;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Params;

/// <summary>
/// k102: ИДЕНТИФИКАТОРЫ РАЗМЕРОВ И ПУТИ К НИМ. Размер эскиза — d0, d1… (нумерация в каждом эскизе с нуля); размер операции —
/// d0, d1… по её числовым полям (по порядку объявления, пустое поле не сдвигает номера). Из формулы вне эскиза — путём:
/// s"Эскиз1".d0, f"Выдавливание".d1, p"Деталь1".s"Эскиз1".d0, a"Сборка".p"Деталь1".s"Эскиз1".d0, p"Деталь1".x (параметр
/// детали). Имена — в кавычках. Путь без a/p — от детали, в которой вычисляется формула.
/// </summary>
public static class ModelRefs
{
    static readonly Regex Seg = new("\\G([apsf])\"([^\"]*)\"\\.", RegexOptions.Compiled);
    static readonly Regex DimId = new("^d(\\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsPath(string name) => name.Contains('"');

    /// <summary>Номер размера по идентификатору «dN»; -1 — не идентификатор.</summary>
    public static int DimIndex(string name) => DimId.Match(name) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) ? n : -1;

    /// <summary>Значение по пути относительно элемента (детали или сборки); null — не найдено.</summary>
    public static double? Resolve(ModelItem context, string path)
    {
        if (Walk(context, path, out int at) is not { } cur) return null;
        string last = path[at..];
        switch (cur)
        {
            case Sketch sk when DimIndex(last) is var i && i >= 0:
                return sk.Dimensions.FirstOrDefault(d => d.Index == i)?.Value;
            case Feature f when DimIndex(last) is var j && j >= 0:
            {
                var slots = FeatureDims.Of(f);
                return j < slots.Count && slots[j].Get() is { } v ? v.Value : null;
            }
            case Part p when at > 0:
                return p.LocalParameters.Find(last) is { Error: null } pp ? pp.Value : null;
            case Assembly a when at > 0:
                return a.LocalParameters.Find(last) is { Error: null } ap ? ap.Value : null;
        }
        return null;
    }

    static readonly Regex Chain = new("(?:[ap]\"[^\"]*\"\\.?)+", RegexOptions.Compiled);
    static readonly Regex Ident = new(@"(?<![\w""\.])([A-Za-z_\p{L}][\w]*)(?![\w""(])", RegexOptions.Compiled);
    public static readonly bool DepsOff = Environment.GetEnvironmentVariable("P3D_PARTDEPS") == "0";

    /// <summary>
    /// k118 (замечание 08:54, п.11): ОТ ЧЕГО ЗАВИСИТ ДЕТАЛЬ — другие детали и сборки, на размеры и параметры которых
    /// ссылаются её формулы (параметры детали, размеры эскизов, поля операций): пути a"…".p"…"… и имена параметров
    /// сборок над деталью, которых нет в самой детали. Элемент — и где ссылка (первая).
    /// </summary>
    public static List<(ModelItem Item, string Where)> DependenciesOf(Part part)
    {
        var res = new List<(ModelItem, string)>();
        if (DepsOff) return res;
        var formulas = new List<(string Where, string Text)>();
        foreach (var p in part.LocalParameters.All) if (!string.IsNullOrEmpty(p.Formula)) formulas.Add((p.Name, p.Formula));
        foreach (var sk in part.Sketches) foreach (var d in sk.Dimensions) if (!string.IsNullOrEmpty(d.Formula)) formulas.Add((PathOf(sk, d), d.Formula!));
        foreach (var f in part.Features)
        {
            var slots = FeatureDims.Of(f);
            for (int i = 0; i < slots.Count; i++) if (slots[i].Get() is { Formula: { Length: > 0 } fx }) formulas.Add((PathOf(f, i), fx));
        }
        void AddDep(ModelItem it, string where) { if (!ReferenceEquals(it, part) && !res.Any(r => ReferenceEquals(r.Item1, it))) res.Add((it, where)); }
        foreach (var (where, text) in formulas)
        {
            foreach (Match m in Chain.Matches(text))
            {
                object cur = part;
                foreach (Match sg in Quoted.Matches(m.Value))
                {
                    if (Step(part, cur, sg.Groups[1].Value, sg.Groups[2].Value) is not ModelItem nx) break;
                    cur = nx;
                }
                if (cur is Part or Assembly) AddDep((ModelItem)cur, where);
            }
            // имена параметров сборок над деталью (своих в детали нет)
            foreach (Match m in Ident.Matches(text))
            {
                string n = m.Groups[1].Value;
                if (part.LocalParameters.Find(n) != null) continue;
                for (var a = part.Parent; a != null; a = a.Parent)
                    if (a is Assembly asm && asm.LocalParameters.Find(n) != null) { AddDep(asm, where); break; }
            }
        }
        return res;
    }

    /// <summary>Шаг пути: элемент по виду (a, p, s, f) и имени от текущего; null — нет такого.</summary>
    static object? Step(ModelItem context, object cur, string kind, string name)
    {
        var root = RootOf(context);
        return kind switch
        {
            "a" => All(root).OfType<Assembly>().FirstOrDefault(x => x.Name == name),
            "p" => All(cur as ModelItem is Assembly ca ? ca : root).OfType<Part>().FirstOrDefault(x => x.Name == name),
            "s" => (cur as Part ?? context as Part)?.Sketches.FirstOrDefault(x => x.Name == name),
            "f" => (cur as Part ?? context as Part)?.Features.FirstOrDefault(x => x.Name == name),
            _ => null,
        };
    }

    /// <summary>Пройти сегменты пути (a"…". p"…". s"…". f"…".); at — где кончились сегменты; null — сегмент не найден.</summary>
    static object? Walk(ModelItem context, string path, out int at)
    {
        object cur = context;
        at = 0;
        Match m;
        while ((m = Seg.Match(path, at)).Success && m.Index == at)
        {
            at = m.Index + m.Length;
            if (Step(context, cur, m.Groups[1].Value, m.Groups[2].Value) is not { } next) return null;
            cur = next;
        }
        return cur;
    }

    static readonly Regex Quoted = new("([apsf])\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// k103 (замечание 4): ИМЕНА В КАВЫЧКАХ в тексте формулы — для подсветки: начало и длина (с буквой вида и кавычками),
    /// найдено ли (по цепочке a"…".p"…".s"…" от элемента context). Незакрытая кавычка не в счёт.
    /// </summary>
    public static List<(int Start, int Length, bool Ok)> QuotedNames(ModelItem context, string text)
    {
        var res = new List<(int, int, bool)>();
        object? cur = context; int chainEnd = -1;
        foreach (Match m in Quoted.Matches(text))
        {
            // буква вида не продолжение имени (x_s"…" — не путь)
            if (m.Index > 0 && (char.IsLetterOrDigit(text[m.Index - 1]) || text[m.Index - 1] == '_')) continue;
            bool chained = m.Index == chainEnd + 1 && chainEnd >= 0 && text[chainEnd] == '.';
            if (!chained) cur = context;
            object? next = cur == null ? null : Step(context, cur, m.Groups[1].Value, m.Groups[2].Value);
            res.Add((m.Index, m.Length, next != null));
            cur = next;
            chainEnd = m.Index + m.Length;
            if (chainEnd >= text.Length || text[chainEnd] != '.') chainEnd = -1;
        }
        return res;
    }

    static readonly Regex DimRef = new("(?<![\\w\"])((?:[apsf]\"[^\"]*\"\\.)*)(d\\d+)(?![\\w\"])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// k103 (18:52): ИДЕНТИФИКАТОРЫ РАЗМЕРОВ в тексте формулы (d0 своего эскиза, s"Эскиз".d1, f"Операция".d0…) — для
    /// подсветки: начало и длина «dN», найден ли размер. Без пути — в эскизе local (нет эскиза — не найден).
    /// </summary>
    public static List<(int Start, int Length, bool Ok)> DimNames(ModelItem context, Sketch? local, string text)
    {
        var res = new List<(int, int, bool)>();
        foreach (Match m in DimRef.Matches(text))
        {
            var g = m.Groups[2];
            bool ok = m.Groups[1].Length == 0 ? local?.DimensionByName(g.Value) != null : Resolve(context, m.Value) != null;
            res.Add((g.Index, g.Length, ok));
        }
        return res;
    }

    /// <summary>
    /// k103 (замечание 3): ПОДСКАЗКА ПОСЛЕ ТОЧКИ. prefix — текст пути до точки (s"Эскиз1", p"Деталь1", a"Сборка".p"Деталь1"; пусто —
    /// начало имени); local — эскиз, в формуле размера которого пишут (его d0… первыми). Вставка и подпись «d1 = 51.3».
    /// </summary>
    public static List<(string Insert, string Label)> Suggest(ModelItem context, Sketch? local, string prefix)
    {
        var res = new List<(string, string)>();
        static string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        void Dims(Sketch sk) { sk.EnsureDimIds(); foreach (var d in sk.Dimensions.OrderBy(x => x.Index)) res.Add((d.Name, $"{d.Name} = {N(d.Value)}")); }
        void FDims(Feature f) { var sl = FeatureDims.Of(f); for (int i = 0; i < sl.Count; i++) if (sl[i].Get() is { } v) res.Add(($"d{i}", $"d{i} = {N(v.Value)} ({sl[i].Name})")); }
        void Items(Part p)
        {
            foreach (var sk in p.Sketches) res.Add(($"s\"{sk.Name}\"", $"s\"{sk.Name}\""));
            foreach (var f in p.Features) res.Add(($"f\"{f.Name}\"", $"f\"{f.Name}\""));
            foreach (var pp in p.LocalParameters.All) res.Add((pp.Name, $"{pp.Name} = {N(pp.Value)}"));
        }
        if (string.IsNullOrEmpty(prefix))
        {
            if (local != null) Dims(local);
            if (context is Part cp) Items(cp);
            else if (context is Assembly ca) { foreach (var pp in ca.LocalParameters.All) res.Add((pp.Name, $"{pp.Name} = {N(pp.Value)}")); }
            var root = RootOf(context);
            foreach (var a in All(root).OfType<Assembly>().Where(x => !ReferenceEquals(x, context))) res.Add(($"a\"{a.Name}\"", $"a\"{a.Name}\""));
            foreach (var p in All(root).OfType<Part>().Where(x => !ReferenceEquals(x, context))) res.Add(($"p\"{p.Name}\"", $"p\"{p.Name}\""));
            return res;
        }
        switch (Walk(context, prefix + ".", out int at))
        {
            case Sketch sk when at == prefix.Length + 1: Dims(sk); break;
            case Feature f when at == prefix.Length + 1: FDims(f); break;
            case Part p when at == prefix.Length + 1: Items(p); break;
            case Assembly a when at == prefix.Length + 1:
                foreach (var p in All(a).OfType<Part>()) res.Add(($"p\"{p.Name}\"", $"p\"{p.Name}\""));
                foreach (var pp in a.LocalParameters.All) res.Add((pp.Name, $"{pp.Name} = {N(pp.Value)}"));
                break;
        }
        return res;
    }

    static ModelItem RootOf(ModelItem x) { while (x.Parent != null) x = x.Parent; return x; }
    static IEnumerable<ModelItem> All(ModelItem x) { yield return x; foreach (var c in x.Children) foreach (var d in All(c)) yield return d; }

    /// <summary>Путь к размеру эскиза для формулы в детали: s"Эскиз".dN.</summary>
    public static string PathOf(Sketch sk, SkDimension d) => $"s\"{sk.Name}\".d{d.Index}";
    /// <summary>Путь к размеру операции: f"Операция".dN.</summary>
    public static string PathOf(Feature f, int index) => $"f\"{f.Name}\".d{index}";
}

/// <summary>Размеры операции: её числовые поля (ValueRef, в т. ч. во вложенных объектах), по порядку объявления.</summary>
public static class FeatureDims
{
    public sealed class Slot
    {
        public required object Owner;
        public required FieldInfo Field;
        public required string Name;
        public ValueRef? Get() => Field.GetValue(Owner) is ValueRef v ? v : null;
        public void Set(ValueRef v) => Field.SetValue(Owner, Field.FieldType == typeof(ValueRef?) ? (ValueRef?)v : v);
    }

    public static List<Slot> Of(Feature f)
    {
        var res = new List<Slot>();
        void Walk(object o, string prefix, int depth)
        {
            if (depth > 2) return;
            foreach (var fi in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(x => x.MetadataToken))
            {
                if (fi.FieldType == typeof(ValueRef) || fi.FieldType == typeof(ValueRef?)) res.Add(new Slot { Owner = o, Field = fi, Name = prefix + fi.Name });
                else if (!fi.FieldType.IsPrimitive && !fi.FieldType.IsEnum && fi.FieldType != typeof(string) && fi.FieldType.Namespace == typeof(Feature).Namespace
                         && !typeof(System.Collections.IEnumerable).IsAssignableFrom(fi.FieldType) && fi.GetValue(o) is { } sub && fi.FieldType.IsClass)
                    Walk(sub, prefix + fi.Name + ".", depth + 1);
            }
        }
        Walk(f, "", 0);
        return res;
    }

    /// <summary>
    /// k128: вид величины поля операции по имени (как UnitConversion.IsLengthField): углы (Angle, Twist, Draft…), числа
    /// (Count, Factor, Fraction, Turns, масштабы Sx/Sy/Sz…) и длины — все прочие.
    /// </summary>
    public static sbyte KindOf(string fieldName)
    {
        string n = fieldName.Contains('.') ? fieldName[(fieldName.LastIndexOf('.') + 1)..] : fieldName;
        if (n.Contains("Angle") || n.Contains("Twist") || n.Contains("Draft") || n.Contains("Degree")) return ValueRef.KindAngle;
        if (n is "Sx" or "Sy" or "Sz" || !UnitConversion.IsLengthField(n)) return ValueRef.KindNumber;
        return ValueRef.KindLength;
    }

    /// <summary>k128: проставить вид величины всем полям операции (перед расчётом, после чтения файла).</summary>
    public static void StampKinds(Feature f)
    {
        foreach (var sl in Of(f))
            if (sl.Get() is { } v) { var k = KindOf(sl.Field.Name); if (v.Kind != k) { v.Kind = k; sl.Set(v); } }
    }

    /// <summary>Номер поля в размерах операции (по владельцу и имени поля); -1 — нет.</summary>
    public static int IndexOf(Feature f, object owner, string field) => Of(f).FindIndex(s => ReferenceEquals(s.Owner, owner) && s.Field.Name == field);
}
