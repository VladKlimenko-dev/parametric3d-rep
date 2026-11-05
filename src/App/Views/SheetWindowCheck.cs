using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.Model.Drawing;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k170 (замечание 05.11, 18:34, п. 7): ПРОВЕРКА ПОЛНОТЫ РАЗМЕРОВ ЧЕРТЕЖА — список недостающих размеров под кнопкой (в единицах
/// чертежа): курсор над пунктом — пункт подсвечен, размер показан на листе серым; щелчок — размер поставлен, как если бы его
/// поставил пользователь. Красные линии (кнопка включена) — элементы детали, у которых не указан размер: курсор над красной
/// линией — подсказка, какого размера не хватает. Мутация P3D_CHECKLIST=0 — прежняя подсказка-список (k163).
/// </summary>
public sealed partial class SheetWindow
{
    internal static readonly bool CheckListOff = Environment.GetEnvironmentVariable("P3D_CHECKLIST") == "0";
    Popup? _checkPop;
    readonly StackPanel _checkList = new() { Spacing = 1 };
    readonly List<(DrawingCheck.Item It, Border Row, DrawingDim? Dim)> _checkRows = new();
    bool _overBtn, _overPop;
    /// <summary>Пункты списка недостающих размеров (для проверок): подпись, предлагаемый размер.</summary>
    public IReadOnlyList<(DrawingCheck.Item It, Border Row, DrawingDim? Dim)> CheckRows => _checkRows;
    public bool CheckListOpen => _checkPop?.IsOpen == true;
    static readonly IBrush CheckRowHover = new SolidColorBrush(Color.FromArgb(90, 30, 140, 255));

    void InitCheckList(Button btn)
    {
        if (CheckListOff) return;
        var border = new Border
        {
            Child = new ScrollViewer { Content = _checkList, MaxHeight = 420 }, Padding = new Thickness(8), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), MinWidth = 300, MaxWidth = 520,
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("P3.PanelBg"));
        _checkPop = new Popup { Child = border, PlacementTarget = btn, Placement = PlacementMode.Bottom, IsLightDismissEnabled = false, Name = "dr_check_list" };
        ((ISetLogicalParent)_checkPop).SetParent(btn);
        btn.PointerEntered += (_, _) => { _overBtn = true; if (_checkRows.Count > 0 || _checkList.Children.Count > 0) _checkPop.IsOpen = true; };
        btn.PointerExited += (_, _) => { _overBtn = false; CloseCheckLater(); };
        border.PointerEntered += (_, _) => _overPop = true;
        border.PointerExited += (_, _) => { _overPop = false; SetGhost(null); CloseCheckLater(); };
    }
    void CloseCheckLater() => Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (!_overBtn && !_overPop && _checkPop != null) { _checkPop.IsOpen = false; SetGhost(null); } }, TimeSpan.FromMilliseconds(350));

    /// <summary>Список недостающих размеров: заголовок, пункты; у поля операции (глубина, угол…) — «укажите вручную».</summary>
    void FillCheckList(List<DrawingCheck.Item> miss)
    {
        if (_checkPop == null) return;
        // тот же набор недостающих (и те же единицы) — список прежний: пункт под курсором и серый размер не пересоздаются
        string sig = Drawing!.EffectiveUnits + "|" + CheckItems.Count + "|" + string.Join("\n", miss.Select(i => i.Label));
        if (sig == _checkSig) { for (int k = 0; k < _checkRows.Count && k < miss.Count; k++) _checkRows[k] = (miss[k], _checkRows[k].Row, _checkRows[k].Dim); if (_ghostItem != null) _ghostItem = _checkRows.FirstOrDefault(r => r.It.Label == _ghostItem.Label).It; return; }
        _checkSig = sig;
        var keep = _ghostItem?.Label;
        _checkList.Children.Clear(); _checkRows.Clear();
        _checkList.Children.Add(new TextBlock
        {
            Text = miss.Count == 0 ? L("Размеры чертежа полные: указаны все {0} размеров детали.", CheckItems.Count)
                                   : L("Размеры чертежа неполные: указано {0} из {1}. Не указаны:", CheckItems.Count - miss.Count, CheckItems.Count),
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var it in miss.Take(60))
        {
            var dim = DrawingCheck.Suggest(Drawing!, it);
            var tb = new TextBlock { Text = "• " + it.Label + (dim == null ? "  — " + L("укажите вручную") : ""), TextWrapping = TextWrapping.Wrap, Opacity = dim == null ? 0.65 : 1 };
            var row = new Border { Child = tb, Padding = new Thickness(4, 2), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent };
            var item = it;
            row.PointerEntered += (_, _) => HoverCheckRow(item);
            row.PointerExited += (_, _) => { if (_ghostItem?.Label == item.Label) HoverCheckRow(null); };
            row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { AddCheckDim(item); e.Handled = true; } };
            if (dim != null) ToolTip.SetTip(row, L("Щелчок — поставить размер на чертёж"));
            _checkRows.Add((it, row, dim));
            _checkList.Children.Add(row);
        }
        if (miss.Count > 60) _checkList.Children.Add(new TextBlock { Text = "…", Opacity = 0.7 });
        _checkList.Children.Add(new TextBlock
        {
            Text = L("Щелчок по кнопке — красным на листе элементы детали, у которых не указан размер; курсор на красной линии — какого размера не хватает."),
            Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        if (keep != null && _checkRows.FirstOrDefault(r => r.It.Label == keep) is { Row: not null } kr) HoverCheckRow(kr.It); else if (_ghostItem != null) SetGhost(null);
    }

    DrawingCheck.Item? _ghostItem;
    string? _checkSig;
    /// <summary>Пункт под курсором: подсветка пункта и серый размер на листе.</summary>
    public void HoverCheckRow(DrawingCheck.Item? it)
    {
        _ghostItem = it;
        foreach (var (i, row, _) in _checkRows) row.Background = i.Label == it?.Label ? CheckRowHover : Brushes.Transparent;
        SetGhost(it == null ? null : _checkRows.FirstOrDefault(r => r.It.Label == it.Label).Dim);
    }
    void SetGhost(DrawingDim? dim)
    {
        if (Drawing == null || ReferenceEquals(Drawing.Ghost, dim)) return;
        Drawing.Ghost = dim;
        RequestRefresh();
    }
    /// <summary>Щелчок по пункту: размер — на чертёж (как поставленный пользователем: место подбирается один раз, затем — мышью).</summary>
    public void AddCheckDim(DrawingCheck.Item it)
    {
        var dim = _checkRows.FirstOrDefault(r => r.It.Label == it.Label).Dim;
        if (dim == null || Drawing == null) { _status.Text = L("Этот размер ставится вручную: {0}", it.Label); return; }
        var d = Drawing;
        _ghostItem = null; d.Ghost = null;
        var real = new DrawingDim { View = dim.View, Kind = dim.Kind, X1 = dim.X1, Y1 = dim.Y1, X2 = dim.X2, Y2 = dim.Y2, Offset = dim.Offset, Arrange = true };
        Change(() => d.Dims.Add(real));
        _sel = real; UpdateOverlay();
        _status.Text = L("Размер {0} поставлен", d.CaptionOf(real));
    }

    // ------------------------------------------------------------------ красные линии (п. 7.1, 7.4)

    readonly List<(DrawingCheck.Item It, List<(double, double, double, double)> Segs)> _missingGeo = new();
    DrawingCheck.Item? _missingHot;
    void MissingOverlay()
    {
        _missingGeo.Clear();
        if (!ShowMissing || Drawing == null) return;
        foreach (var it in CheckItems.Where(i => !i.Covered))
            if (DrawingCheck.Geometry(Drawing, it, _input?.Part) is { } geo && Drawing.Xf.ContainsKey(geo.View))
            {
                var segs = geo.Lines.SelectMany(l => SheetSegs(geo.View, l)).ToList();
                _missingGeo.Add((it, segs));
                _canvas.Overlay.Add((segs, MissingColor, ReferenceEquals(it, _missingHot) ? 4.5f : 2.5f));
            }
    }
    /// <summary>Недостающий размер, чья красная линия под курсором (1 мм листа).</summary>
    public DrawingCheck.Item? MissingAt(double x, double y)
    {
        if (!ShowMissing) return null;
        DrawingCheck.Item? best = null; double bd = 1.0;
        foreach (var (it, segs) in _missingGeo)
            foreach (var (x1, y1, x2, y2) in segs)
            {
                double dd = SegDist(x, y, x1, y1, x2, y2);
                if (dd < bd) { bd = dd; best = it; }
            }
        return best;
    }
    string MissingTip(DrawingCheck.Item it)
    {
        var dim = DrawingCheck.Suggest(Drawing!, it);
        return L("Не указан размер {0}", it.Label) + (dim != null ? " — " + L("щелчок в списке у кнопки проверки ставит его") : "");
    }
    void ShowMissingTip(DrawingCheck.Item? it)
    {
        if (ReferenceEquals(it, _missingHot)) return;
        _missingHot = it;
        if (it == null) { ToolTip.SetIsOpen(_canvas, false); ToolTip.SetTip(_canvas, null); return; }
        ToolTip.SetTip(_canvas, MissingTip(it));
        ToolTip.SetPlacement(_canvas, PlacementMode.Pointer);
        ToolTip.SetIsOpen(_canvas, true);
    }
    /// <summary>Подсказка над красной линией (для проверок).</summary>
    public string? CanvasTip => ToolTip.GetTip(_canvas) as string;
}
