using System.Globalization;
using System.Text;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Views;

/// <summary>
/// k105 (замечание 2): ОТМЕНА И ВОЗВРАТ В ЭСКИЗЕ. Правки эскиза (инструменты, перетаскивание, размеры, связи, удаление)
/// отмечаются снимками содержимого эскиза (SketchState: элементы, связи и размеры с прежними номерами — на них ссылаются
/// операции): после каждой правки, если эскиз изменился, прежний снимок уходит в стек отмены. Ctrl+Z / Ctrl+Y и кнопки
/// «Отменить» / «Вернуть» в эскизе работают по этому стеку; правка после отмены очищает возврат. Вход в эскиз — стек
/// заново (правка эскиза целиком — один шаг отмены документа после выхода). Мутация P3D_SKUNDO=0 — отмены в эскизе нет.
/// </summary>
public partial class MainWindow
{
    public static readonly bool SketchUndoOff = Environment.GetEnvironmentVariable("P3D_SKUNDO") == "0";
    private readonly List<SketchState> _skUndo = new(), _skRedo = new();
    private SketchState? _skLast;
    private string _skLastSig = "";
    private Sketch? _skUndoFor;
    private bool _skApplying;

    private void ResetSketchUndo(Sketch sk)
    {
        _skUndo.Clear(); _skRedo.Clear();
        _skUndoFor = sk;
        _skLast = SketchState.Capture(sk);
        _skLastSig = SketchSig(sk);
    }

    /// <summary>Подпись содержимого эскиза: изменился ли он с последней отметки.</summary>
    private static string SketchSig(Sketch sk) => SketchSig(sk, "R");

    /// <summary>
    /// k116: подпись с координатами до 10⁻⁹ — «эскиз не менялся» при выходе из правки: решатель на пересчёте детали
    /// сдвигает точки на 10⁻¹⁵, и точная подпись видела в этом правку (деталь пересчитывалась).
    /// </summary>
    private static string SketchSigRounded(Sketch sk) => SketchSig(sk, "0.#########");

    private static string SketchSig(Sketch sk, string fmt)
    {
        var b = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        foreach (var e in sk.Entities)
        {
            b.Append(e.Kind).Append(e.Id).Append((int)e.Role).Append(e.Fixed ? 'f' : '-');
            foreach (var h in e.Handles) b.Append(h.Id).Append(':').Append(h.X.ToString(fmt, ci)).Append(',').Append(h.Y.ToString(fmt, ci)).Append(';');
            if (e is SkCircle c) b.Append('r').Append(c.Radius.ToString(fmt, ci));
            if (e is SkArc a) b.Append(a.CCW ? '+' : '-');
            if (e is SkEllipse el) b.Append(el.RU.ToString(fmt, ci)).Append(el.RV.ToString(fmt, ci)).Append(el.Rotation.ToString(fmt, ci));
            b.Append('|');
        }
        foreach (var c in sk.Constraints) { b.Append(c.Type).Append(c.Enabled ? 1 : 0); foreach (var i in c.Items) b.Append(',').Append(i.Id); b.Append('|'); }
        foreach (var d in sk.Dimensions)
        {
            if (fmt != "R" && d.IsDefault) continue;   // k116: размеры по умолчанию (ставит сам редактор) — не правка
            b.Append(d.Type).Append(d.Value.ToString(fmt, ci)).Append(d.Locked ? 'L' : '-').Append(d.Reference ? 'R' : '-').Append(d.IsDefault ? 'D' : '-')
             .Append(d.Formula).Append(d.TextOffset.X.ToString("0.###", ci)).Append(d.TextOffset.Y.ToString("0.###", ci)).Append(d.Arrows);
            foreach (var i in d.Items) b.Append(',').Append(i.Id);
            b.Append('|');
        }
        return b.ToString();
    }

    /// <summary>Отметка после правки эскиза: изменился — прежний снимок в стек отмены, возврат очищается.</summary>
    public void SketchCheckpoint()
    {
        if (SketchUndoOff || _skApplying || View.ActiveSketch is not { } sk) return;
        if (!ReferenceEquals(sk, _skUndoFor) || _skLast == null) { ResetSketchUndo(sk); return; }
        string sig = SketchSig(sk);
        if (sig == _skLastSig) return;
        _skUndo.Add(_skLast);
        if (_skUndo.Count > 300) _skUndo.RemoveAt(0);
        _skRedo.Clear();
        _skLast = SketchState.Capture(sk);
        _skLastSig = sig;
    }

    public bool CanSketchUndo => !SketchUndoOff && View.ActiveSketch is { } sk && ReferenceEquals(sk, _skUndoFor) && (_skUndo.Count > 0 || SketchSig(sk) != _skLastSig);
    public bool CanSketchRedo => !SketchUndoOff && View.ActiveSketch is { } sk && ReferenceEquals(sk, _skUndoFor) && _skRedo.Count > 0 && SketchSig(sk) == _skLastSig;
    public int SketchUndoDepth => _skUndo.Count;

    public void SketchUndo()
    {
        SketchCheckpoint();
        if (View.ActiveSketch is not { } sk || _skUndo.Count == 0 || _skLast == null) return;
        var st = _skUndo[^1]; _skUndo.RemoveAt(_skUndo.Count - 1);
        _skRedo.Add(_skLast);
        ApplySketchState(sk, st);
    }

    public void SketchRedo()
    {
        SketchCheckpoint();
        if (View.ActiveSketch is not { } sk || _skRedo.Count == 0 || _skLast == null) return;
        var st = _skRedo[^1]; _skRedo.RemoveAt(_skRedo.Count - 1);
        _skUndo.Add(_skLast);
        ApplySketchState(sk, st);
    }

    private void ApplySketchState(Sketch sk, SketchState st)
    {
        _skApplying = true;
        try
        {
            CloseDimensionEdit();
            View.Selection.Clear();
            var u0 = sk.Plane.U;
            st.RestoreInto(sk);
            if ((sk.Plane.U - u0).Length > 1e-9) View.AlignToActiveSketch();   // k123: отменён поворот эскиза — и вид
            _skLast = SketchState.Capture(sk);      // снимок из стека теперь «живой» — у отметки своя копия
            _skLastSig = SketchSig(sk);
            if (View.Tool is { } t) View.Tool = t.Restart();
            sk.NotifyChanged(sk);
            View.MarkDirty();
            View.InvalidateOverlay();
        }
        finally { _skApplying = false; }
        ScheduleTree(); UpdateStatus(); UpdateContextBar(); RefreshToolStates();
    }
}
