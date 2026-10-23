using Parametric3D.Core;

namespace Parametric3D.Model.Params;

public enum ParamKind { Real = 0, Integer = 1, String = 2, Formula = 3, Boolean = 4 }

public sealed class Parameter
{
    public string Name = "param";
    public ParamKind Kind = ParamKind.Real;
    public double Number;
    public string Text = string.Empty;
    public bool Flag;
    /// <summary>Текст формулы для Kind == Formula.</summary>
    public string Formula = string.Empty;
    public string Unit = string.Empty;
    public string Comment = string.Empty;
    /// <summary>Ошибка вычисления (null — всё хорошо).</summary>
    public string? Error;

    internal Expr? Compiled;

    /// <summary>m.x4: размерность по единице показа (Unit); пустая единица — число без размерности (как прежде).</summary>
    public Dimension Dim => string.IsNullOrWhiteSpace(Unit) ? Dimension.None : Units.Parse(Unit).Dim;
    /// <summary>Величина параметра: значение во внутренних единицах (мм, кг, с, градус) и размерность.</summary>
    public Quantity Quantity => string.IsNullOrWhiteSpace(Unit) ? Quantity.Number(Value) : new Quantity(Value, Dim);

    public double Value => Kind switch
    {
        ParamKind.Integer => Math.Round(Number),
        ParamKind.Boolean => Flag ? 1 : 0,
        _ => Number
    };

    public override string ToString() =>
        Kind == ParamKind.Formula ? $"{Name} = {Formula} -> {Number}" : $"{Name} = {Number}";
}

/// <summary>
/// Таблица параметров документа.
///
/// Пересчёт — топологической сортировкой по графу зависимостей, с явным
/// обнаружением циклов. Формула, которая ссылается сама на себя (прямо или
/// через цепочку), помечается ошибкой, а не роняет приложение.
/// </summary>
public sealed class ParameterTable : IValueResolver
{
    private readonly Dictionary<string, Parameter> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Parameter> _order = new();

    public IGeometryResolver? Geometry;
    public event Action? Changed;

    /// <summary>
    /// UI-ПАРАМЕТРЫ ПО УРОВНЯМ (как в Creo): таблица детали или сборки ссылается на таблицу уровня выше (сборка, документ).
    /// Имя, которого нет в своей таблице, ищется выше; своё имя закрывает такое же имя уровня выше.
    /// </summary>
    public ParameterTable? Parent;

    /// <summary>
    /// k102: значения по путям к размерам (s"Эскиз".d0, f"Операция".d1, p"Деталь".…, a"Сборка".…) — от элемента, которому
    /// принадлежит таблица (ModelRefs.Resolve). null — путь не найден.
    /// </summary>
    public Func<string, double?>? External;

    /// <summary>Есть ли имя в этой таблице или выше.</summary>
    public bool HasScoped(string name) => _map.ContainsKey(name) || (ModelRefs.IsPath(name) && External?.Invoke(name) != null) || (Parent?.HasScoped(name) ?? false);

    /// <summary>k102: выражение с заданным источником значений (свои размеры эскиза и т. п.).</summary>
    public static bool TryEvaluateWith(string formula, IValueResolver vals, IGeometryResolver? geom, out double value, out string? error)
    {
        value = 0; error = null;
        if (!ExprParser.TryParse(formula, out Expr? e, out error) || e == null) return false;
        try { value = e.Eval(vals, geom); if (double.IsNaN(value) || double.IsInfinity(value)) { error = "The result is not a number."; return false; } return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Параметр по имени — свой или уровня выше.</summary>
    public Parameter? FindScoped(string name) => Find(name) ?? Parent?.FindScoped(name);

    private Quantity? QuantityOf(string n) =>
        _map.TryGetValue(n, out var p) ? (p.Error == null ? p.Quantity : null)
        : ModelRefs.IsPath(n) && External?.Invoke(n) is { } ev ? Quantity.Number(ev)
        : Parent?.QuantityOf(n);
    /// <summary>m.x4: система единиц документа — число без единицы в формуле с величинами берёт её единицу.</summary>
    public UnitSystem Units = UnitSystem.Metric;
    static readonly bool UnitCheckOff = Environment.GetEnvironmentVariable("P3D_PARAMUNITS") == "0";

    public IReadOnlyList<Parameter> All => _order;
    public int Count => _order.Count;

    public Parameter? Find(string name) => _map.TryGetValue(name, out var p) ? p : null;

    public Parameter Add(string name, double value, ParamKind kind = ParamKind.Real, string unit = "")
    {
        name = UniqueName(name);
        var p = new Parameter { Name = name, Number = value, Kind = kind, Unit = unit };
        _map[name] = p;
        _order.Add(p);
        Recalculate();
        return p;
    }

    public Parameter AddFormula(string name, string formula, string unit = "")
    {
        name = UniqueName(name);
        var p = new Parameter { Name = name, Kind = ParamKind.Formula, Formula = formula, Unit = unit };
        _map[name] = p;
        _order.Add(p);
        Recalculate();
        return p;
    }

    public bool Remove(string name)
    {
        var p = Find(name);
        if (p == null) return false;
        _map.Remove(p.Name);
        _order.Remove(p);
        Recalculate();
        return true;
    }

    public bool Rename(Parameter p, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        if (_map.ContainsKey(newName) && !ReferenceEquals(_map[newName], p)) return false;
        _map.Remove(p.Name);
        p.Name = newName;
        _map[newName] = p;
        Recalculate();
        return true;
    }

    private string UniqueName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "param";
        if (!_map.ContainsKey(baseName)) return baseName;
        for (int i = 1; ; i++)
        {
            string n = baseName + i;
            if (!_map.ContainsKey(n)) return n;
        }
    }

    public bool TryValue(string name, out double value)
    {
        if (_map.TryGetValue(name, out var p)) { value = p.Value; return p.Error == null; }
        if (ModelRefs.IsPath(name) && External?.Invoke(name) is { } ext) { value = ext; return true; }
        if (Parent != null) return Parent.TryValue(name, out value);
        value = 0;
        return false;
    }

    /// <summary>Разовое вычисление произвольного выражения в контексте таблицы.</summary>
    public bool TryEvaluate(string formula, out double value, out string? error)
    {
        value = 0; error = null;
        if (!ExprParser.TryParse(formula, out Expr? e, out error) || e == null) return false;
        try { value = e.Eval(this, Geometry); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>m.x4: вычисление выражения с единицами — значение во внутренних единицах и размерность.</summary>
    public bool TryEvaluateQuantity(string formula, out Quantity value, out string? error)
    {
        value = default; error = null;
        if (!ExprParser.TryParse(formula, out Expr? e, out error) || e == null) return false;
        try { value = ExprUnits.Evaluate(e, QuantityOf, Units, Geometry); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>m.x4: параметр из записи с единицей («2 in», «7.85 g/cm^3»): значение — во внутренних единицах, единица показа — из записи.</summary>
    public Parameter AddQuantity(string name, string text)
    {
        if (!ExprParser.TryParse(text, out var e, out var err) || e == null) throw new P3DException(err ?? "bad value");
        var q = ExprUnits.Evaluate(e, n => _map.TryGetValue(n, out var p) ? p.Quantity : null, Units, Geometry);
        string unit = e is QtyExpr qe ? qe.Unit.Symbol : q.Dim.IsNone ? "" : Units.UnitFor(q.Dim).Symbol;
        return Add(name, q.Value, ParamKind.Real, unit);
    }

    /// <summary>Полный пересчёт всех формул в порядке зависимостей.</summary>
    public void Recalculate()
    {
        // 1. Компиляция
        foreach (var p in _order)
        {
            p.Error = null;
            if (p.Kind != ParamKind.Formula) { p.Compiled = null; continue; }
            if (!ExprParser.TryParse(p.Formula, out Expr? e, out string? err))
            {
                p.Compiled = null;
                p.Error = err;
            }
            else p.Compiled = e;
        }

        // 2. Граф зависимостей только между формулами
        var deps = new Dictionary<Parameter, List<Parameter>>();
        foreach (var p in _order)
        {
            var list = new List<Parameter>();
            if (p.Compiled != null)
            {
                var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                p.Compiled.CollectRefs(refs);
                foreach (var r in refs)
                    if (_map.TryGetValue(r, out var dep))
                    {
                        if (!ReferenceEquals(dep, p)) list.Add(dep);
                        else p.Error = "Formula refers to itself.";
                    }
                    else if (!(ModelRefs.IsPath(r) && External?.Invoke(r) != null) && !(Parent?.HasScoped(r) ?? false))
                        p.Error = $"Unknown parameter '{r}'.";
            }
            deps[p] = list;
        }

        // 3. Топологическая сортировка (обход в глубину с пометками)
        var state = new Dictionary<Parameter, int>();   // 0 не посещён, 1 в работе, 2 готов
        foreach (var p in _order) state[p] = 0;

        void Visit(Parameter p)
        {
            if (state[p] == 2) return;
            if (state[p] == 1) { p.Error = "Circular dependency between formulas."; return; }
            state[p] = 1;
            foreach (var d in deps[p]) Visit(d);
            state[p] = 2;

            if (p.Kind == ParamKind.Formula && p.Error == null && p.Compiled != null)
            {
                try
                {
                    p.Number = p.Compiled.Eval(this, Geometry);
                    // m.x4: у параметра с единицей — размерность формулы; число без единицы — в единице параметра
                    if (!UnitCheckOff && !string.IsNullOrWhiteSpace(p.Unit))
                    {
                        var unit = Parametric3D.Core.Units.Parse(p.Unit);
                        var q = ExprUnits.Evaluate(p.Compiled, QuantityOf, Units, Geometry);
                        if (q.Bare && q.Dim.IsNone) p.Number = unit.ToInternal(q.Value);
                        else if (q.Dim != unit.Dim) p.Error = $"The formula gives {q.Dim}, the parameter is {unit.Dim} ({p.Unit}).";
                        else p.Number = q.Value;
                    }
                }
                catch (Exception ex) { p.Error = ex.Message; }
            }
        }

        foreach (var p in _order) Visit(p);
        Changed?.Invoke();
    }

    /// <summary>Кто ссылается на данный параметр (для предупреждения при удалении).</summary>
    public List<Parameter> UsagesOf(Parameter target)
    {
        var res = new List<Parameter>();
        foreach (var p in _order)
        {
            if (p.Compiled == null) continue;
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            p.Compiled.CollectRefs(refs);
            if (refs.Contains(target.Name)) res.Add(p);
        }
        return res;
    }
}
