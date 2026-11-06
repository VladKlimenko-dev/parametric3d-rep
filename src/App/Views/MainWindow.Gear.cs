using System.Globalization;
using Avalonia.Controls;
using Parametric3D.App.Sketching;
using Parametric3D.Core;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k176: ПАНЕЛЬ СВОЙСТВ ШЕСТЕРНИ ЭСКИЗА. При выборе любого элемента шестерни (в эскизе) панель показывает все параметры
/// зубчатого венца: вид профиля (эвольвентный, циклоидальный, прямобочный), венец (наружный, внутренний), число зубьев, модуль
/// (и равноценно — диаметральный шаг, окружной шаг, делительный диаметр), угол профиля, смещение, коэффициенты высоты головки,
/// радиального зазора, переходной кривой, скругления вершины, боковой зазор, производящую окружность циклоиды, центр, поворот,
/// допуск приближения, делительную окружность; ниже — расчётные диаметры и толщина зуба. Правка перестраивает шестерню
/// (шаг отмены эскиза). Мутация P3D_GEARPANEL=0 — обычный список выбранного.
/// </summary>
public partial class MainWindow
{
    public static readonly bool GearPanelOff = Environment.GetEnvironmentVariable("P3D_GEARPANEL") == "0";

    static readonly string[] GearProfiles = { "Эвольвентный", "Циклоидальный", "Прямобочный" };
    static readonly string[] GearRims = { "Наружный", "Внутренний" };
    static readonly string[] YesNo = { "Да", "Нет" };

    /// <summary>Шестерня, все выбранные элементы которой — её элементы; null — выбор не шестерня.</summary>
    public SkGear? SelectedGear()
    {
        if (GearPanelOff || View.ActiveSketch is not { } sk || sk.Gears.Count == 0 || View.Selection.IsEmpty) return null;
        SkGear? g = null;
        foreach (var r in View.Selection.Items)
        {
            if (r.Target is not SkEntity e || sk.GearOf(e) is not { } ge) return null;
            if (g != null && !ReferenceEquals(g, ge)) return null;
            g = ge;
        }
        return g;
    }

    /// <summary>После инструмента «Шестерня»: выбрать построенную шестерню (панель свойств покажет её параметры).</summary>
    private void AfterGearTool()
    {
        if (GearTool.LastCreated is not { } g || View.ActiveSketch is not { } sk) return;
        GearTool.LastCreated = null;
        SelectGear(sk, g);
        if (!_propsOn) ToggleProperties(); else ShowProperties();
    }

    private void SelectGear(Sketch sk, SkGear g)
    {
        View.Selection.Clear();
        var outline = sk.Entities.FirstOrDefault(e => g.Items.Contains(e.Id) && e.Role == SkRole.Object && e is not SkPoint)
                      ?? sk.Entities.FirstOrDefault(e => g.Items.Contains(e.Id));
        if (outline != null) View.Selection.Select(new PickRef(outline.Id, outline is SkPoint ? PickKind.SketchPoint : PickKind.SketchCurve, sk, outline), additive: false);
    }

    static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    static bool TryNum(string s, out double v) => double.TryParse(s.Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private void ShowGearProperties(Sketch sk, SkGear g)
    {
        PropsTitle.Text = L("ШЕСТЕРНЯ");
        var p = g.P;
        // правка: копия параметров → перестроение; не строится — параметры прежние, причина — в панели
        void Apply(Action<GearParams> change)
        {
            var old = p.Clone();
            change(p);
            if (!sk.Regenerate(g, out var why))
            {
                g.P = old; sk.Regenerate(g, out _);
                _gearError = why;
            }
            else { _gearError = null; GearTool.Defaults = g.P.Clone(); }
            SelectGear(sk, g);
            View.MarkDirty(); View.InvalidateOverlay();
            SketchCheckpoint(); ScheduleTree(); UpdateStatus();
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowProperties);
        }
        void NumRow(string label, double v, Action<GearParams, double> set, Func<double, bool>? ok = null)
            => EditRow(label, Num(v), s => { if (TryNum(s, out var x) && (ok == null || ok(x))) Apply(q => set(q, x)); else { _gearError = L("Неверное значение: {0}", s); Avalonia.Threading.Dispatcher.UIThread.Post(ShowProperties); } });

        if ((_gearError ?? g.Error) is { } err) Row("Ошибка", L(err));
        ChoiceRow("Профиль зуба", GearProfiles, GearProfiles[(int)p.Profile], v => Apply(q => q.Profile = (GearProfile)Array.IndexOf(GearProfiles, v)));
        ChoiceRow("Венец", GearRims, GearRims[p.Internal ? 1 : 0], v => Apply(q => q.Internal = v == GearRims[1]));
        NumRow("Число зубьев", p.Teeth, (q, x) => q.Teeth = (int)Math.Round(x), x => x >= 3 && x <= 2000);
        NumRow("Модуль", p.Module, (q, x) => q.Module = x, x => x > 0);
        NumRow("Диаметральный шаг", p.DiametralPitch, (q, x) => q.Module = 25.4 / x, x => x > 0);
        NumRow("Окружной шаг", p.CircularPitch, (q, x) => q.Module = x / Math.PI, x => x > 0);
        NumRow("Делительный диаметр", 2 * p.PitchRadius, (q, x) => q.Module = x / q.Teeth, x => x > 0);
        NumRow(p.Profile == GearProfile.StraightFlank ? "Угол стороны, °" : "Угол профиля, °", p.PressureAngle, (q, x) => q.PressureAngle = x, x => x >= 0 && x < 45);
        NumRow("Смещение x", p.ProfileShift, (q, x) => q.ProfileShift = x);
        NumRow("Высота головки h*a", p.AddendumCoef, (q, x) => q.AddendumCoef = x, x => x >= 0);
        NumRow("Радиальный зазор c*", p.ClearanceCoef, (q, x) => q.ClearanceCoef = x, x => x >= 0);
        NumRow("Переходная кривая ρ*f", p.RootFilletCoef, (q, x) => q.RootFilletCoef = x, x => x >= 0);
        NumRow("Скругление вершины ρ*a", p.TipRoundCoef, (q, x) => q.TipRoundCoef = x, x => x >= 0);
        NumRow("Боковой зазор", p.Backlash, (q, x) => q.Backlash = x);
        if (p.Profile == GearProfile.Cycloidal)
            NumRow("Производящая окружность, модулей (0 — z/4)", p.RollingCircleCoef, (q, x) => q.RollingCircleCoef = x, x => x >= 0);
        NumRow("Центр X", p.CenterX, (q, x) => q.CenterX = x);
        NumRow("Центр Y", p.CenterY, (q, x) => q.CenterY = x);
        NumRow("Поворот, °", p.Rotation, (q, x) => q.Rotation = x);
        NumRow("Допуск профиля", p.Tolerance, (q, x) => q.Tolerance = x, x => x > 0);
        ChoiceRow("Делительная окружность", YesNo, YesNo[p.PitchCircle ? 0 : 1], v => Apply(q => q.PitchCircle = v == YesNo[0]));

        // расчётные величины
        Row("Диаметр вершин da", Num(2 * p.TipRadius));
        Row("Диаметр впадин df", Num(2 * p.RootRadius));
        if (p.Profile == GearProfile.Involute) Row("Основной диаметр db", Num(2 * p.BaseRadius));
        Row("Толщина зуба s", Num(p.ToothThickness));
        if (p.Profile == GearProfile.Involute) Row("Основной шаг pb", Num(p.CircularPitch * Math.Cos(p.Alpha)));
        Row("Элементов", sk.Entities.Count(e => g.Items.Contains(e.Id)).ToString());
    }

    private string? _gearError;
}
