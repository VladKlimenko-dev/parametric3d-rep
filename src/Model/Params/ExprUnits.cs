using Parametric3D.Core;

namespace Parametric3D.Model.Params;

/// <summary>
/// m.x4: РАЗМЕРНОСТЬ ФОРМУЛЫ. Значение — то же, что Expr.Eval (внутренние единицы); здесь — размерность и проверка
/// согласованности: «10 in + 5 mm» — длина 259 мм, «10 mm + 5 kg» — ошибка с причиной. Число без единицы (Bare) в сумме
/// с величиной берёт единицу документа этой размерности («10 in + 5» в документе мм — 259 мм); в произведении — множитель.
/// Тригонометрия — угол или число (градусы, как в Expr.Eval); обратные функции дают угол.
/// </summary>
public static class ExprUnits
{
    public static Quantity Evaluate(Expr e, Func<string, Quantity?> refs, UnitSystem doc, IGeometryResolver? geom = null)
    {
        switch (e)
        {
            case QtyExpr q: return new Quantity(q.Internal, q.Unit.Dim);
            case NumExpr n: return Quantity.Number(n.V);
            case RefExpr r:
                if (string.Equals(r.Name, "pi", StringComparison.OrdinalIgnoreCase)) return Quantity.Number(Math.PI);
                return refs(r.Name) ?? throw new P3DException($"Unknown parameter '{r.Name}'.");
            case UnaryExpr u:
            {
                var a = Evaluate(u.A, refs, doc, geom);
                return u.Op == '-' ? a with { Value = -a.Value } : a;
            }
            case BinExpr b:
            {
                var a = Evaluate(b.A, refs, doc, geom); var c = Evaluate(b.B, refs, doc, geom);
                switch (b.Op)
                {
                    case "+": case "-":
                    {
                        (a, c) = Align(a, c, doc);
                        if (a.Dim != c.Dim) throw new P3DException($"Cannot {(b.Op == "+" ? "add" : "subtract")} {a.Dim} and {c.Dim}.");
                        return new Quantity(b.Op == "+" ? a.Value + c.Value : a.Value - c.Value, a.Dim, a.Bare && c.Bare);
                    }
                    case "*": return new Quantity(a.Value * c.Value, a.Dim * c.Dim, a.Bare && c.Bare);
                    case "/":
                        if (c.Value == 0) throw new P3DException("Division by zero.");
                        return new Quantity(a.Value / c.Value, a.Dim / c.Dim, a.Bare && c.Bare);
                    case "%": return new Quantity(a.Value / 100 * c.Value, a.Dim * c.Dim, a.Bare && c.Bare);
                    case "<": case ">": case "<=": case ">=": case "==": case "!=":
                    {
                        (a, c) = Align(a, c, doc);
                        if (a.Dim != c.Dim) throw new P3DException($"Cannot compare {a.Dim} and {c.Dim}.");
                        return Quantity.Number(new BinExpr(b.Op, new NumExpr(a.Value), new NumExpr(c.Value)).Eval(NoValues.Instance, geom));
                    }
                    case "&&": case "||": return Quantity.Number(new BinExpr(b.Op, new NumExpr(a.Value), new NumExpr(c.Value)).Eval(NoValues.Instance, geom));
                    case "^":
                    {
                        if (!c.Dim.IsNone) throw new P3DException($"An exponent must be a number, not {c.Dim}.");
                        if (a.Dim.IsNone) return new Quantity(Math.Pow(a.Value, c.Value), a.Dim, a.Bare);
                        double k = Math.Round(c.Value);
                        if (Math.Abs(c.Value - k) > 1e-12) throw new P3DException($"{a.Dim} can be raised only to a whole power.");
                        return new Quantity(Math.Pow(a.Value, k), a.Dim.Pow((int)k));
                    }
                }
                throw new P3DException($"Unknown operator '{b.Op}'.");
            }
            case CallExpr call:
            {
                string n = call.Name.ToLowerInvariant();
                if (n == "if")
                {
                    if (call.Args.Count != 3) throw new P3DException("if(condition, then, else): expected 3 arguments.");
                    return Evaluate(call.Args[0], refs, doc, geom).Value != 0 ? Evaluate(call.Args[1], refs, doc, geom) : Evaluate(call.Args[2], refs, doc, geom);
                }
                if (call.GeomRef != null && geom != null)
                    switch (n)
                    {
                        case "length": if (geom.TryLength(call.GeomRef, out double L)) return new Quantity(L, Dimension.Length); break;
                        case "area": if (geom.TryArea(call.GeomRef, out double A)) return new Quantity(A, Dimension.Area); break;
                        case "volume": if (geom.TryVolume(call.GeomRef, out double V)) return new Quantity(V, Dimension.Volume); break;
                    }
                var a = call.Args.Select(x => Evaluate(x, refs, doc, geom)).ToArray();
                double val = new CallExpr(call.Name, a.Select(x => (Expr)new NumExpr(x.Value)).ToList()).Eval(NoValues.Instance, geom);   // та же функция над значениями
                switch (n)
                {
                    case "sin": case "cos": case "tan": case "tg":
                        if (!(a[0].Dim.IsNone || a[0].Dim == Dimension.Angle)) throw new P3DException($"{call.Name} takes an angle, not {a[0].Dim}.");
                        return Quantity.Number(val);
                    case "asin": case "acos": case "atan": case "atg":
                        if (!a[0].Dim.IsNone) throw new P3DException($"{call.Name} takes a number, not {a[0].Dim}.");
                        return new Quantity(val, Dimension.Angle);
                    case "atan2":
                        (a[0], a[1]) = Align(a[0], a[1], doc);
                        if (a[0].Dim != a[1].Dim) throw new P3DException($"atan2 takes two values of one kind, not {a[0].Dim} and {a[1].Dim}.");
                        return new Quantity(val, Dimension.Angle);
                    case "sqrt":
                    {
                        var d = a[0].Dim.Root(2) ?? throw new P3DException($"Square root of {a[0].Dim} has no unit.");
                        return new Quantity(val, d, a[0].Bare);
                    }
                    case "abs": case "floor": case "ceil": case "round": return a[0] with { Value = val };
                    case "min": case "max": case "bound":
                    {
                        for (int i = 1; i < a.Length; i++) { (a[0], a[i]) = Align(a[0], a[i], doc); if (a[0].Dim != a[i].Dim) throw new P3DException($"{call.Name} of {a[0].Dim} and {a[i].Dim}."); }
                        return new Quantity(val, a[0].Dim, a.All(x => x.Bare));
                    }
                    case "sign": case "trunc": case "mod": return n == "sign" ? Quantity.Number(val) : a[0] with { Value = val };
                    case "length":
                    {
                        for (int i = 1; i < a.Length; i++) { (a[0], a[i]) = Align(a[0], a[i], doc); if (a[0].Dim != a[i].Dim) throw new P3DException("Length of mixed kinds."); }
                        return new Quantity(Math.Sqrt(a.Sum(x => x.Value * x.Value)), a[0].Dim, a.All(x => x.Bare));
                    }
                    case "pi": return Quantity.Number(Math.PI);
                }
                return Quantity.Number(val);
            }
        }
        throw new P3DException($"Unknown expression {e.GetType().Name}.");
    }

    /// <summary>Число без единицы рядом с величиной: берёт единицу документа размерности этой величины.</summary>
    static (Quantity, Quantity) Align(Quantity a, Quantity b, UnitSystem doc)
    {
        if (a.Bare && !b.Bare && !b.Dim.IsNone) a = new Quantity(doc.UnitFor(b.Dim).ToInternal(a.Value), b.Dim);
        else if (b.Bare && !a.Bare && !a.Dim.IsNone) b = new Quantity(doc.UnitFor(a.Dim).ToInternal(b.Value), a.Dim);
        return (a, b);
    }

    /// <summary>Значение результата с ожидаемой размерностью: число без единицы — в единицах документа; иначе проверка размерности.</summary>
    public static double ValueAs(Quantity q, Dimension want, UnitSystem doc)
    {
        if (q.Bare && q.Dim.IsNone) return doc.UnitFor(want).ToInternal(q.Value);
        if (q.Dim != want) throw new P3DException($"Expected {want}, got {q.Dim}.");
        return q.Value;
    }

    sealed class NoValues : IValueResolver
    {
        public static readonly NoValues Instance = new();
        public bool TryValue(string name, out double value) { value = 0; return false; }
    }
}
