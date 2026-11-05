using OpenTK.Mathematics;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// k163 (замечание 05.11, 09:22, п. 3): ПРОВЕРКА ПОЛНОТЫ РАЗМЕРОВ ЧЕРТЕЖА. Деталь воспроизводится по своим размерам: размерам
/// эскизов действующих операций (заданным и слабым, кроме справочных и отключённых) и числовым полям операций (глубина
/// выдавливания, угол вращения, размер фаски, угол уклона… — те же, что в окне «Параметры»). Размер считается указанным,
/// если на чертеже есть размер той же величины: линейный — линейный, радиус — радиус или диаметр вдвое больше, диаметр —
/// диаметр или радиус вдвое меньше, угол — угловой (угловых размеров на листе пока нет — углы всегда в списке). Сравнение — по
/// значению (допуск 1e-3 мм или 1e-6 относительно): это правило «размер есть на листе», а не привязка к ребру.
/// </summary>
public static class DrawingCheck
{
    /// <summary>Размер детали: что это (для подсказки), величина (мм или °), вид величины, источник, указан ли на листе.</summary>
    public sealed record Item(string Label, double Value, char Kind, Sketch? Sketch, SkDimension? Dim, Feature? Feature, bool Covered);

    /// <summary>Вид величины: L — длина, R — радиус, D — диаметр, A — угол.</summary>
    public static List<Item> Run(DrawingDocument d, Part part)
    {
        var res = new List<Item>();
        var live = part.Features.Where(f => !f.Suppressed).ToList();
        var sketchIds = new HashSet<uint>(live.SelectMany(f => f.ConsumedSketchIds()));
        foreach (var sk in part.Sketches.Where(s => sketchIds.Contains(s.Id)))
            foreach (var dm in sk.Dimensions.Where(x => x.Enabled && !x.Reference))
            {
                char kind = dm.Type switch
                {
                    DimensionType.Radius => 'R',
                    DimensionType.Diameter => 'D',
                    DimensionType.AngleLineLine => 'A',
                    _ => 'L',
                };
                if (Math.Abs(dm.Value) < 1e-9) continue;   // нулевой размер — совпадение, не размер
                string unit = kind == 'A' ? "°" : "";
                string pre = kind switch { 'R' => "R", 'D' => DrawingDocument.Dia, _ => "" };
                res.Add(new Item($"{sk.Name}: {pre}{F(d, Math.Abs(dm.Value), kind)}{unit}", Math.Abs(dm.Value), kind, sk, dm, null, Covered(d, Math.Abs(dm.Value), kind)));
            }
        foreach (var f in live)
            foreach (var sl in Params.FeatureDims.Of(f))
            {
                if (sl.Get() is not { } v || Math.Abs(v.Value) < 1e-9) continue;
                // сторона выдавливания не на глубину (насквозь, до грани) или вторая сторона одностороннего — не размер
                if (sl.Owner is ExtrudeSide side && (side.End != EndType.Distance || f is ExtrudeFeature ef && side == ef.Side2 && !ef.TwoSided)) continue;
                sbyte k = Params.FeatureDims.KindOf(sl.Field.Name);
                if (k == ValueRef.KindNumber) continue;   // число (количество, коэффициент) — не размер
                char kind = k == ValueRef.KindAngle ? 'A' : 'L';
                if (kind == 'A' && Math.Abs(Math.Abs(v.Value) - 360) < 1e-9) continue;   // полный оборот — не размер
                res.Add(new Item($"{f.Name}: {sl.Name} {F(d, Math.Abs(v.Value), kind)}{(kind == 'A' ? "°" : "")}", Math.Abs(v.Value), kind, null, null, f, Covered(d, Math.Abs(v.Value), kind)));
            }
        return res;
    }

    static string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>
    /// k170 (замечание 05.11, 18:34, п. 7.2): величина в единицах чертежа (мм, дюймы…) и с его запятой — как подписи размеров на
    /// листе; прежде — всегда мм. Мутация P3D_CHECKUNITS=0.
    /// </summary>
    static string F(DrawingDocument d, double v, char kind)
    {
        if (UnitsOff) return Fmt(v);
        if (kind == 'A') return new DrawingDim { Kind = "ang", X2 = 0, Y2 = v }.Caption(d.UsesComma).TrimEnd('°');
        return d.FormatLength(v);
    }
    static readonly bool UnitsOff = Environment.GetEnvironmentVariable("P3D_CHECKUNITS") == "0";

    /// <summary>
    /// k170 (п. 7.3): недостающий размер как размер чертежа (для серого показа и постановки щелчком): размер эскиза — на виде,
    /// параллельном эскизу (как «Размеры модели»); поле операции (глубина, угол…) — null (ставится вручную).
    /// </summary>
    public static DrawingDim? Suggest(DrawingDocument d, Item it)
    {
        if (it.Sketch is not { } sk || it.Dim is not { } dm) return null;
        var dd = d.FromSketchDim(sk, dm);
        if (dd != null) dd.Arrange = true;
        return dd;
    }

    static bool Close(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-3, 1e-6 * Math.Abs(b));

    /// <summary>Есть ли на листе размер этой величины (скрытые размеры не считаются).</summary>
    public static bool Covered(DrawingDocument d, double v, char kind)
    {
        foreach (var dm in d.Dims.Where(x => !x.Hidden))
        {
            double val = dm.Kind switch { "rad" or "dia" => dm.X2, _ => dm.Value };   // у радиальных X2 — радиус
            switch (kind)
            {
                case 'L' when dm.Kind is "h" or "v" or "al" && Close(dm.Value, v): return true;
                case 'L' when dm.Kind is "dia" && Close(2 * val, v): return true;     // длина = диаметру (ширина паза по окружности)
                case 'R' when dm.Kind is "rad" or "dia" && Close(val, v): return true;
                case 'D' when dm.Kind is "rad" or "dia" && Close(2 * val, v): return true;
                case 'A' when dm.Kind == "ang" && Close(dm.Value, v): return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Отрезки подсветки неуказанного размера на виде, параллельном плоскости эскиза (координаты вида, мм модели): у размера
    /// эскиза — элементы, к которым он относится; у поля операции (глубина, угол…) — контур её эскизов (какая операция без
    /// размера). Нет такого вида — пусто.
    /// </summary>
    public static (string View, List<List<Vector2d>> Lines)? Geometry(DrawingDocument d, Item it, Part? part = null)
    {
        IEnumerable<(Sketch Sk, IEnumerable<SkEntity> Ents)> src;
        if (it.Sketch is { } sk0 && it.Dim is { } dm0) src = new[] { (sk0, (IEnumerable<SkEntity>)dm0.Items) };
        else if (it.Feature is { } f0 && part != null)
        {
            var ids = new HashSet<uint>(f0.ConsumedSketchIds());
            src = part.Sketches.Where(s => ids.Contains(s.Id)).Select(s => (s, s.Entities.Where(e => e.Role == SkRole.Object))).ToList();
        }
        else return null;
        string? viewName = null;
        var lines = new List<List<Vector2d>>();
        foreach (var (sk, ents) in src)
        {
            var w = sk.Plane.W.Normalized();
            var view = d.Views.Where(v => !v.IsDetail && !v.IsSection && v.Name != "iso" && (viewName == null || v.Name == viewName))
                              .FirstOrDefault(v => Math.Abs(Math.Abs(Vector3d.Dot(ViewFrame.Of(d.DirOf(v)).Toward, w)) - 1) < 1e-6);
            if (view == null) continue;
            viewName = view.Name;
            var f = ViewFrame.Of(d.DirOf(view));
            Vector2d P(Vector2d q) => f.Project(sk.Plane.ToWorld(q));
            foreach (var e in ents)
                switch (e)
                {
                    case SkLine l: lines.Add(new() { P(l.A.P), P(l.B.P) }); break;
                    case SkPoint pt: { var c = P(pt.P); lines.Add(new() { c + new Vector2d(-1.5, -1.5), c + new Vector2d(1.5, 1.5) }); lines.Add(new() { c + new Vector2d(-1.5, 1.5), c + new Vector2d(1.5, -1.5) }); break; }
                    case SkCircle ci: lines.Add(Enumerable.Range(0, 49).Select(k => P(ci.Center.P + ci.Radius * new Vector2d(Math.Cos(k * Math.PI / 24), Math.Sin(k * Math.PI / 24)))).ToList()); break;
                    case SkArc ar:
                        {
                            double a0 = ar.A0, a1 = ar.A1;
                            lines.Add(Enumerable.Range(0, 33).Select(k => P(ar.Center.P + ar.Radius * new Vector2d(Math.Cos(a0 + (a1 - a0) * k / 32), Math.Sin(a0 + (a1 - a0) * k / 32)))).ToList());
                            break;
                        }
                }
        }
        return viewName == null || lines.Count == 0 ? null : (viewName, lines);
    }
}
