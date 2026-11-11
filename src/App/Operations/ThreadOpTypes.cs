using Parametric3D.Kernel.Ops;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Threads;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// Резьба: список типов по выбранной грани, справка о типе, поле длины («0 — вся грань» показывается длиной грани).
public sealed partial class CosmeticThreadOp
{
    private (double D, bool Inner)? _dia;

    /// <summary>Поле длины: 0 («вся грань») показывается длиной, найденной при перестроении.</summary>
    private ValueRef ShownLength() => F.Length.Formula == null && !(F.Length.Value > 0) && F.ThreadLen > 0 ? new ValueRef(Math.Round(F.ThreadLen, 3)) : F.Length;

    private void SetLength(ValueRef v)
    {
        var shown = ShownLength();
        // показанное значение «вся грань» не правили — остаётся 0 (длина следует за гранью)
        if (v.Formula == null && F.Length.Formula == null && !(F.Length.Value > 0) && Math.Abs(v.Value - shown.Value) < 1e-9) return;
        F.Length = v;
    }

    private string TypeInfo()
    {
        if (ThreadDb.All.Count == 0) return L("Таблица резьб не найдена: {0}", ThreadDb.LoadError ?? "");
        var face = FaceDia() is { } fd ? L(fd.Inner ? "Отверстие Ø{0} мм" : "Вал Ø{0} мм", fd.D.ToString("0.###")) : "";
        if (F.Spec is not { } s) return face;
        var t = L("Шаг {0} мм, Ø {1} мм", s.P.ToString("0.####"), s.DMajor.ToString("0.###"));
        if (s.Starts > 1) t += ", " + L("заходов {0}", s.Starts);
        if (s.Taper) t += ", " + L("конусная");
        return face.Length > 0 ? face + " · " + t : t;
    }

    /// <summary>Диаметр выбранной грани и отверстие ли это: по грани выбора, иначе — с последнего перестроения.</summary>
    private (double D, bool Inner)? FaceDia()
    {
        if (_face.Items.Count > 0 && _face.Items[0].Owner is Part p && p.FindFace(_face.Items[0].Id) is { } tf
            && DirectEdit.RadiusAfter(tf, 0) is { } r0 && DirectEdit.RadiusAfter(tf, 1) is { } r1)
            return _dia = (2 * r0, r1 < r0);
        if (_face.Items.Count == 0 && _face.Touched) return _dia = null;
        if (_dia == null && F.Diameter > 0 && F.Error == null) _dia = (F.Diameter, F.Internal);
        return _dia;
    }

    /// <summary>Типы в списке: без грани — все; с гранью — подходящие (лучшие первыми); выбранный — всегда.</summary>
    private List<ThreadSpec> Types()
    {
        var list = FaceDia() is { } fd ? ThreadDb.Candidates(fd.D, fd.Inner) : ThreadDb.All.ToList();
        if (F.Spec is { } cur && !list.Contains(cur)) list.Insert(0, cur);
        return list;
    }
}
