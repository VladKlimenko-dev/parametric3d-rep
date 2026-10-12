using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model;

/// <summary>
/// K06. ЗАВИСИМОСТИ ОПЕРАЦИИ ОТ ВХОДОВ ВНЕ ДЕРЕВА: параметров документа и эскизов.
///
/// Последовательная цепочка тел сохраняет обязательный порядок: операция зависит от всех
/// предыдущих. Здесь определяется другое — с какой операции начать, когда изменилось что-то
/// снаружи: значение параметра или эскиз. Операции до неё не перевыполняются.
///
/// Правило осторожное: формула со ссылкой, которую нельзя понять как имя параметра (функция
/// без скобок, ссылка на геометрию другой детали), считается зависящей от любого изменения.
/// </summary>
public static class Dependencies
{
    private static readonly Regex Ident = new(@"[A-Za-z_][A-Za-z0-9_\.]*", RegexOptions.Compiled);

    /// <summary>Имена, на которые ссылается формула; null — формулу нельзя разобрать осторожно.</summary>
    public static HashSet<string>? NamesIn(string formula, ParameterTable table)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Ident.Matches(formula))
        {
            int after = m.Index + m.Length;
            while (after < formula.Length && char.IsWhiteSpace(formula[after])) after++;
            bool call = after < formula.Length && formula[after] == '(';
            if (call) continue;                                  // имя функции
            if (table.Find(m.Value) == null)
            {
                if (m.Value is "pi" or "PI" or "e" or "E" or "true" or "false") continue;
                return null;                                     // неизвестная ссылка — осторожно
            }
            names.Add(m.Value);
        }
        return names;
    }

    /// <summary>Значения всех параметров документа — для сравнения между расчётами.</summary>
    public static Dictionary<string, double> Snapshot(ParameterTable? table)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        if (table == null) return d;
        foreach (var p in table.All)
            d[p.Name] = table.TryEvaluate(p.Name, out double v, out _) ? v : double.NaN;
        return d;
    }

    public static HashSet<string> Changed(Dictionary<string, double> before, Dictionary<string, double> now)
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (k, v) in now)
            if (!before.TryGetValue(k, out double o) || BitConverter.DoubleToInt64Bits(o) != BitConverter.DoubleToInt64Bits(v)) c.Add(k);
        foreach (var k in before.Keys) if (!now.ContainsKey(k)) c.Add(k);
        return c;
    }

    /// <summary>Формулы операции: все поля ValueRef, в том числе во вложенных объектах и списках.</summary>
    public static IEnumerable<string> FormulasOf(object feature)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var result = new List<string>();
        void Walk(object? o, int depth)
        {
            if (o == null || depth > 3 || o is string || o.GetType().IsPrimitive) return;
            if (!o.GetType().IsValueType && !seen.Add(o)) return;
            if (o is ValueRef vr) { if (!string.IsNullOrWhiteSpace(vr.Formula)) result.Add(vr.Formula!); return; }
            if (o is IEnumerable en && o is not IDictionary)
            {
                foreach (var x in en) if (x is ValueRef or not null && x.GetType().Namespace?.StartsWith("Parametric3D.Model") == true) Walk(x, depth + 1);
                return;
            }
            // Сама операция просматривается всегда (в том числе подклассы вне сборки модели);
            // вложенные объекты — только из модели, чтобы не уходить в геометрию и кэши.
            if (depth > 0 && o.GetType().Namespace?.StartsWith("Parametric3D.Model") != true) return;
            foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType == typeof(ValueRef)) { var v = (ValueRef)f.GetValue(o)!; if (!string.IsNullOrWhiteSpace(v.Formula)) result.Add(v.Formula!); }
                else if (!f.FieldType.IsPrimitive && f.FieldType != typeof(string) && f.Name != "Parent" && f.Name != "Children")
                    Walk(f.GetValue(o), depth + 1);
            }
        }
        Walk(feature, 0);
        return result;
    }

    /// <summary>Эскизы, которые операция читает: заявленные и поля с номером эскиза.</summary>
    public static HashSet<uint> SketchesOf(Feature f)
    {
        var ids = new HashSet<uint>(f.ConsumedSketchIds());
        foreach (var fi in f.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!fi.Name.Contains("Sketch", StringComparison.OrdinalIgnoreCase)) continue;
            if (fi.FieldType == typeof(uint) && (uint)fi.GetValue(f)! is uint id && id != 0) ids.Add(id);
            else if (fi.GetValue(f) is IEnumerable<uint> many) foreach (var x in many) if (x != 0) ids.Add(x);
        }
        foreach (var pi in f.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (pi.Name.Contains("Sketch", StringComparison.OrdinalIgnoreCase) && pi.PropertyType == typeof(uint) && pi.GetIndexParameters().Length == 0
                && pi.GetValue(f) is uint id2 && id2 != 0) ids.Add(id2);
        return ids;
    }

    /// <summary>Зависит ли операция (или её эскизы) от изменившихся параметров.</summary>
    public static bool DependsOn(Feature f, Part part, HashSet<string> changed, ParameterTable table)
    {
        if (changed.Count == 0) return false;
        IEnumerable<string> formulas = FormulasOf(f);
        var sketches = SketchesOf(f);
        foreach (var s in part.Sketches)
            if (sketches.Contains(s.Id))
                formulas = formulas.Concat(s.Dimensions.Select(d => d.Formula).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
        foreach (var formula in formulas)
        {
            var names = NamesIn(formula, table);
            if (names == null || names.Overlaps(changed)) return true;
        }
        return false;
    }

    /// <summary>
    /// K06. Зависит ли операция от элемента детали (датум-плоскости, оси, точки): поле с его
    /// номером или именем (в том числе во вложенных объектах и списках), либо эскиз операции
    /// построен на этой плоскости. Поиск осторожный: совпадение номера или имени — зависимость.
    /// </summary>
    public static bool DependsOnItem(Feature f, Part part, ModelItem item)
    {
        foreach (var sid in SketchesOf(f))
            foreach (var sk in part.Sketches)
                if (sk.Id == sid && sk.BasePlaneId == item.Id) return true;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool Walk(object? o, int depth)
        {
            if (o == null || depth > 3) return false;
            if (o is uint u) return u == item.Id;
            if (o is string str) return !string.IsNullOrEmpty(item.Name) && str == item.Name;
            if (o.GetType().IsPrimitive) return false;
            if (!o.GetType().IsValueType && !seen.Add(o)) return false;
            if (o is System.Collections.IEnumerable en)
            {
                foreach (var x in en) if (Walk(x, depth + 1)) return true;
                return false;
            }
            if (depth > 0 && o.GetType().Namespace?.StartsWith("Parametric3D.Model") != true) return false;
            foreach (var fi in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (fi.Name is "Parent" or "Children" or "Name" or "Key") continue;
                if (fi.Name == "<Id>k__BackingField" || fi.Name == "Id") continue;
                if (Walk(fi.GetValue(o), depth + 1)) return true;
            }
            return false;
        }
        return Walk(f, 0);
    }
}
