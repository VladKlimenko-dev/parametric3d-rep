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
        object cur = context;
        int at = 0;
        Match m;
        while ((m = Seg.Match(path, at)).Success && m.Index == at)
        {
            string kind = m.Groups[1].Value, name = m.Groups[2].Value;
            at = m.Index + m.Length;
            var root = RootOf(context);
            object? next = kind switch
            {
                "a" => All(root).OfType<Assembly>().FirstOrDefault(x => x.Name == name),
                "p" => All(cur as ModelItem is Assembly ca ? ca : root).OfType<Part>().FirstOrDefault(x => x.Name == name),
                "s" => (cur as Part ?? context as Part)?.Sketches.FirstOrDefault(x => x.Name == name),
                "f" => (cur as Part ?? context as Part)?.Features.FirstOrDefault(x => x.Name == name),
                _ => null,
            };
            if (next == null) return null;
            cur = next;
        }
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

    /// <summary>Номер поля в размерах операции (по владельцу и имени поля); -1 — нет.</summary>
    public static int IndexOf(Feature f, object owner, string field) => Of(f).FindIndex(s => ReferenceEquals(s.Owner, owner) && s.Field.Name == field);
}
