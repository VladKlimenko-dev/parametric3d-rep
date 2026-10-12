using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>
/// Дерево проекта — порт вашего ProjectTree на новую модель.
///
/// Сохранено то, ради чего оно писалось: колонки с заголовком и изменяемой
/// шириной, колонка ID, переключатель видимости «глазом» вместо чекбокса,
/// собственная отрисовка и подсветка строки под курсором.
///
/// Что изменилось по сути: узел больше не хранит GLItem и не лезет в
/// статический Project, чтобы подсветить объект во вьюпорте. Дерево —
/// это представление: оно строится из документа и сообщает наружу
/// событиями. Кто на них подписан и что делает — дерево не знает.
/// Благодаря этому одно и то же дерево можно повесить на два вида,
/// а модель — сохранить и протестировать без формы.
///
/// Стрелка раскрытия и «глаз» рисуются векторно, а не символами шрифта:
/// внешний шрифт (fa-sharp-light) больше не нужен, и на машине без него
/// дерево выглядит одинаково.
/// </summary>
public sealed class ProjectTree : UserControl
{
    // ------------------------------------------------------------- колонки

    public sealed class Column
    {
        public string Title = "";
        public int Width = 120;
        public int MinWidth = 28;
        public int MaxWidth = 400;
        public int Left;
        public HorizontalAlignment Align = HorizontalAlignment.Left;
        /// <summary>Колонка растягивается на всё оставшееся место.</summary>
        public bool Fill;
    }

    public readonly List<Column> Columns = new();

    public const int ColName = 0;
    public const int ColId = 1;
    public const int ColVisible = 2;

    // ------------------------------------------------------------- внешний вид

    public int NodeHeight = Core.Settings.Current.TreeNodeHeight;
    public int HeaderHeight = 22;
    public int IndentPerLevel = 14;
    public int ArrowSize = 12;
    public int IconSize = Core.Settings.Current.TreeIconSize;
    public bool ShowHeader = true;
    public bool ResizableColumns = true;

    public Color BackgroundColor = Core.Settings.Current.TreeBackColor;
    public Color LinesColor = Core.Settings.Current.TreeLinesColor;
    public Color HeaderBackColor = Core.Settings.Current.TreeHeaderBackColor;
    public Color HeaderTextColor = Color.FromArgb(70, 74, 82);
    public Color NodeTextColor = Core.Settings.Current.TreeTextColor;
    public Color NodeTextDimColor = Core.Settings.Current.TreeDimTextColor;
    public Color NodeErrorColor = Core.Settings.Current.TreeErrorColor;
    public Color NodeHoverBackColor = Core.Settings.Current.TreeHoverBackColor;
    public Color NodeSelectBackColor = Core.Settings.Current.TreeSelectBackColor;
    public Color ArrowColor = Color.FromArgb(105, 110, 120);
    public Color EyeOnColor = Color.FromArgb(70, 120, 190);
    public Color EyeOffColor = Color.FromArgb(180, 184, 190);
    public Color EyeHoverColor = Color.FromArgb(230, 130, 30);

    // ------------------------------------------------------------- узлы

    public sealed class Node
    {
        public ModelItem? Item;
        public string Text = "";
        public string? IconFile;
        public Node? Parent;
        public readonly List<Node> Children = new();
        public bool Expanded = true;
        /// <summary>Служебная группа («Build tree») — без модели за спиной.</summary>
        public bool IsGroup;
        /// <summary>Можно ли переключать видимость.</summary>
        public bool HasVisibility = true;

        internal Rectangle Row;
        internal Rectangle ArrowRc;
        internal Rectangle EyeRc;

        public IEnumerable<Node> Descendants()
        {
            foreach (var c in Children)
            {
                yield return c;
                foreach (var d in c.Descendants()) yield return d;
            }
        }
    }

    private readonly Node _root = new() { IsGroup = true };
    private readonly List<Node> _visible = new();     // раскрытые строки по порядку

    // ------------------------------------------------- полоска расчёта
    //
    // Она рисуется МЕЖДУ строками, поэтому её положение — это не строка,
    // а зазор: индекс строки, ПЕРЕД которой она проходит. Хранить её как
    // строку было бы неверно — полоска может стоять и над первой операцией,
    // и под последней, где строк нет.

    /// <summary>Цвет и толщина полоски расчёта.</summary>
    public Color StageColor = Color.FromArgb(230, 120, 40);
    public int StageThickness = 3;

    /// <summary>
    /// Заливка области ниже полоски. Затенение нужно, чтобы режим вставки
    /// нельзя было не заметить: у Creo это отдельная проверка ModelCHECK
    /// именно потому, что о забытом режиме вставки вспоминают поздно.
    /// </summary>
    public Color StageShadeColor = Color.FromArgb(28, 230, 120, 40);

    /// <summary>Цвет метки подавленной операции.</summary>
    public Color SuppressedMarkColor = Color.FromArgb(120, 124, 132);

    /// <summary>Насколько близко к полоске надо подвести курсор, пикселей.</summary>
    public int StageGrabPx = 5;

    private bool _stageDrag;
    private Part? _stageDragPart;
    private int _stageDragIndex;
    private readonly VScrollBar _vscroll = new();

    private Node? _hover;
    private Node? _selected;
    private bool _eyeHover;

    private bool _resizeReady, _resizing;
    private int _resizeIndex = -1, _resizeStartWidth;
    private Point _mouseDown;

    private Document? _doc;
    private Font? _boldFont;

    /// <summary>
    /// Узел активного уровня работы: сборка, деталь или эскиз.
    /// Рисуется жирным — это второй, ненавязчивый индикатор режима
    /// в дополнение к статусной строке.
    /// </summary>
    public uint ActiveId { get; private set; }

    public void SetActiveItem(ModelItem? item)
    {
        uint id = item?.Id ?? 0;
        if (id == ActiveId) return;
        ActiveId = id;
        Invalidate();
    }

    // ------------------------------------------------------------- события

    /// <summary>Курсор над строкой (null — ушёл с дерева). Отсюда идёт подсветка в 3D.</summary>
    public event Action<ModelItem?>? ItemHover;
    public event Action<ModelItem>? ItemSelected;
    public event Action<ModelItem>? ItemActivated;
    public event Action<ModelItem, bool>? VisibilityChanged;

    /// <summary>
    /// Полоску расчёта перетащили: деталь и новое число выполняемых операций.
    /// </summary>
    public event Action<Part, int>? StageChanged;

    public ProjectTree()
    {
        Font = Core.Settings.Current.TreeFont();
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = BackgroundColor;
        Padding = new Padding(0);

        Columns.Add(new Column { Title = "Name", Width = 190, MinWidth = 80, Fill = true });
        Columns.Add(new Column { Title = "ID", Width = 52, MinWidth = 34, Align = HorizontalAlignment.Right });
        Columns.Add(new Column { Title = "", Width = 30, MinWidth = 24, MaxWidth = 40, Align = HorizontalAlignment.Center });

        _vscroll.Dock = DockStyle.Right;
        _vscroll.Width = 15;
        _vscroll.Visible = false;
        _vscroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_vscroll);

        MouseWheel += OnWheel;
    }

    private Font BoldFont => _boldFont ??= new Font(Font, FontStyle.Bold);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _boldFont?.Dispose();
        _boldFont = null;
        Invalidate();
    }

    // ------------------------------------------------------------- построение

    public void SetDocument(Document doc)
    {
        _doc = doc;
        Rebuild();
    }

    /// <summary>Перестраивает дерево, сохраняя раскрытие и выделение.</summary>
    public void Rebuild()
    {
        if (_doc == null) return;

        var collapsed = new HashSet<uint>();
        foreach (var n in _root.Descendants())
            if (!n.Expanded && n.Item != null) collapsed.Add(n.Item.Id);

        uint selectedId = _selected?.Item?.Id ?? 0;

        _root.Children.Clear();
        _hover = null;
        _selected = null;

        AddItem(_root, _doc.Root, collapsed);

        LayoutColumns();
        UpdateVisibleList();

        if (selectedId != 0)
            _selected = FindNode(selectedId);

        UpdateScroll();
        Invalidate();
    }

    private Node AddItem(Node parent, ModelItem item, HashSet<uint> collapsed)
    {
        var node = new Node
        {
            Item = item,
            Parent = parent,
            Text = LabelOf(item),
            IconFile = IconOf(item),
            Expanded = !collapsed.Contains(item.Id),
            HasVisibility = item is not Feature
        };
        parent.Children.Add(node);

        // ЕДИНЫЙ порядок — тот, в котором элементы создавались. Операции
        // лежат в Children наравне с остальным, поэтому достаточно одного
        // прохода.
        //
        // ИСПОЛЬЗОВАННЫЕ эскизы в этой последовательности не участвуют:
        // они показываются вложенными в свою операцию. Так устроено дерево
        // в Creo, и это разумно — эскиз, ушедший в тело, самостоятельного
        // места в истории построения не занимает, а нужен как часть операции.
        // Самостоятельные эскизы остаются отдельными строками.
        var consumed = ConsumedSketches(item);

        foreach (var c in item.Children)
        {
            if (c is Sketch sk && consumed.Contains(sk)) continue;

            Node child = AddItem(node, c, collapsed);

            // Эскизы операции — её дети.
            if (c is Feature f && item is Part fp)
                foreach (uint id in f.ConsumedSketchIds())
                    foreach (var s in fp.Sketches)
                        if (s.Id == id) AddItem(child, s, collapsed);
        }

        return node;
    }

    /// <summary>Эскизы, которые уже принадлежат какой-нибудь операции.</summary>
    private static HashSet<Sketch> ConsumedSketches(ModelItem item)
    {
        var set = new HashSet<Sketch>();
        if (item is not Part part) return set;

        foreach (var f in part.Features)
            foreach (uint id in f.ConsumedSketchIds())
                foreach (var s in part.Sketches)
                    if (s.Id == id) set.Add(s);

        return set;
    }

    private static string LabelOf(ModelItem i) => i switch
    {
        Feature f => f.Summary,
        _ => i.Name
    };

    private static string? IconOf(ModelItem i) => i switch
    {
        Assembly => "TreeAssembly.png",
        Part => "TreePart.png",
        Sketch => "Sketch.png",
        DatumPlane => "TreePlane.png",
        DatumAxis => "Axis.png",
        DatumPoint => "TreePoints.png",
        ExtrudeFeature => "extrusion.png",
        RevolveFeature => "revolution.png",
        SweepFeature => "rotation.png",
        _ => null
    };

    public Node? FindNode(uint id)
    {
        foreach (var n in _root.Descendants())
            if (n.Item != null && n.Item.Id == id) return n;
        return null;
    }

    public void SelectItem(uint id, bool scrollTo = true)
    {
        _selected = FindNode(id);
        if (_selected != null && scrollTo) EnsureVisible(_selected);
        Invalidate();
    }

    /// <summary>Подсветить строку под курсором извне — из вьюпорта.</summary>
    public void HighlightItem(uint id)
    {
        Node? n = id == 0 ? null : FindNode(id);
        if (ReferenceEquals(n, _hover)) return;
        _hover = n;
        Invalidate();
    }

    private void EnsureVisible(Node n)
    {
        for (Node? p = n.Parent; p != null; p = p.Parent) p.Expanded = true;
        UpdateVisibleList();
        UpdateScroll();

        int i = _visible.IndexOf(n);
        if (i < 0 || !_vscroll.Visible) return;

        int top = i * NodeHeight;
        int viewH = ViewRect.Height;
        if (top < _vscroll.Value) _vscroll.Value = top;
        else if (top + NodeHeight > _vscroll.Value + viewH)
            _vscroll.Value = Math.Min(_vscroll.Maximum, top + NodeHeight - viewH);
    }

    // ------------------------------------------------------------- геометрия

    private Rectangle ViewRect => new(
        0,
        ShowHeader ? HeaderHeight : 0,
        Math.Max(0, ClientSize.Width - (_vscroll.Visible ? _vscroll.Width : 0)),
        Math.Max(0, ClientSize.Height - (ShowHeader ? HeaderHeight : 0)));

    private void LayoutColumns()
    {
        int avail = Math.Max(60, ClientSize.Width - (_vscroll.Visible ? _vscroll.Width : 0));
        int fixedW = Columns.Where(c => !c.Fill).Sum(c => c.Width);

        foreach (var c in Columns)
            if (c.Fill) c.Width = Math.Max(c.MinWidth, avail - fixedW);

        int left = 0;
        foreach (var c in Columns) { c.Left = left; left += c.Width; }
    }

    private void UpdateVisibleList()
    {
        _visible.Clear();
        Walk(_root);

        void Walk(Node n)
        {
            foreach (var c in n.Children)
            {
                _visible.Add(c);
                if (c.Expanded) Walk(c);
            }
        }
    }

    private void UpdateScroll()
    {
        int content = _visible.Count * NodeHeight;
        int viewH = ClientSize.Height - (ShowHeader ? HeaderHeight : 0);

        bool need = content > viewH && viewH > 0;
        if (need != _vscroll.Visible)
        {
            _vscroll.Visible = need;
            LayoutColumns();
        }
        if (!need) { _vscroll.Value = 0; return; }

        _vscroll.Minimum = 0;
        _vscroll.SmallChange = NodeHeight;
        _vscroll.LargeChange = Math.Max(NodeHeight, viewH);
        _vscroll.Maximum = Math.Max(0, content - viewH + _vscroll.LargeChange - 1);
        if (_vscroll.Value > _vscroll.Maximum) _vscroll.Value = _vscroll.Maximum;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutColumns();
        UpdateScroll();
        Invalidate();
    }

    // ------------------------------------------------------------- отрисовка

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackgroundColor);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        UpdateVisibleList();

        Rectangle view = ViewRect;
        int scroll = _vscroll.Visible ? _vscroll.Value : 0;

        // Строки
        var oldClip = g.Clip;
        g.SetClip(view);

        int first = Math.Max(0, scroll / NodeHeight);
        int last = Math.Min(_visible.Count - 1, (scroll + view.Height) / NodeHeight);

        for (int i = first; i <= last; i++)
        {
            Node n = _visible[i];
            n.Row = new Rectangle(0, view.Y + i * NodeHeight - scroll, view.Width, NodeHeight);
            PaintNode(g, n);
        }
        PaintStageBar(g, view, scroll);
        g.Clip = oldClip;

        // Вертикальные разделители колонок
        using (var pen = new Pen(LinesColor))
            for (int i = 0; i < Columns.Count - 1; i++)
            {
                int x = Columns[i].Left + Columns[i].Width;
                g.DrawLine(pen, x, 0, x, ClientSize.Height);
            }

        // Заголовок
        if (ShowHeader) PaintHeader(g);
    }

    /// <summary>
    /// Строки операций одной детали в порядке расчёта.
    /// Возвращает индексы в списке видимых строк.
    /// </summary>
    private List<int> FeatureRows(Part part)
    {
        var rows = new List<int>();
        for (int i = 0; i < _visible.Count; i++)
            if (_visible[i].Item is Feature f && ReferenceEquals(f.Parent, part))
                rows.Add(i);
        return rows;
    }

    /// <summary>Деталь, чья полоска показывается. Пока поддержана одна.</summary>
    private Part? StagePart()
    {
        foreach (var n in _visible)
            if (n.Item is Part p && p.Features.Count > 0) return p;
        return null;
    }

    /// <summary>
    /// Экранная высота полоски при заданном числе выполняемых операций.
    ///
    /// stage операций выполнено, значит полоска проходит НАД строкой
    /// операции с номером stage. Если выполнены все — под последней.
    /// </summary>
    private int StageY(Part part, int stage, Rectangle view, int scroll)
    {
        var rows = FeatureRows(part);
        if (rows.Count == 0) return view.Y;

        int rowIndex = stage < rows.Count ? rows[stage] : rows[^1] + 1;
        return view.Y + rowIndex * NodeHeight - scroll;
    }

    private void PaintStageBar(Graphics g, Rectangle view, int scroll)
    {
        Part? part = StagePart();
        if (part == null) return;

        int stage = _stageDrag && ReferenceEquals(part, _stageDragPart)
            ? _stageDragIndex
            : part.EffectiveStage;

        int y = StageY(part, stage, view, scroll);

        // Затенение всего, что ниже полоски.
        var rows = FeatureRows(part);
        if (rows.Count > 0 && stage < rows.Count)
        {
            int bottom = view.Y + (rows[^1] + 1) * NodeHeight - scroll;
            int shadeHeight = bottom - y;
            if (shadeHeight > 0)
                using (var shade = new SolidBrush(StageShadeColor))
                    g.FillRectangle(shade, view.X, y, view.Width, shadeHeight);
        }

        using var pen = new Pen(StageColor, StageThickness);
        g.DrawLine(pen, view.X, y, view.X + view.Width, y);

        // Треугольная метка слева: за неё и тянут.
        using var brush = new SolidBrush(StageColor);
        int h = Math.Max(4, NodeHeight / 3);
        g.FillPolygon(brush, new[]
        {
            new Point(view.X, y - h),
            new Point(view.X + h, y),
            new Point(view.X, y + h)
        });
    }

    private void PaintHeader(Graphics g)
    {
        var rc = new Rectangle(0, 0, ClientSize.Width, HeaderHeight);
        using (var b = new SolidBrush(HeaderBackColor)) g.FillRectangle(b, rc);
        using (var p = new Pen(LinesColor)) g.DrawLine(p, 0, HeaderHeight - 1, rc.Width, HeaderHeight - 1);

        using var tb = new SolidBrush(HeaderTextColor);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

        foreach (var c in Columns)
        {
            sf.Alignment = c.Align switch
            {
                HorizontalAlignment.Right => StringAlignment.Far,
                HorizontalAlignment.Center => StringAlignment.Center,
                _ => StringAlignment.Near
            };
            var r = new Rectangle(c.Left + 4, 0, Math.Max(0, c.Width - 8), HeaderHeight);
            g.DrawString(c.Title, Font, tb, r, sf);
        }
    }

    private void PaintNode(Graphics g, Node n)
    {
        bool hovered = ReferenceEquals(n, _hover);
        bool selected = ReferenceEquals(n, _selected);

        if (selected || hovered)
        {
            using var bg = new SolidBrush(selected ? NodeSelectBackColor : NodeHoverBackColor);
            g.FillRectangle(bg, n.Row);
        }

        int level = Level(n);
        int x = 2 + level * IndentPerLevel;
        int midY = n.Row.Y + NodeHeight / 2;

        // Стрелка раскрытия
        n.ArrowRc = Rectangle.Empty;
        if (n.Children.Count > 0)
        {
            n.ArrowRc = new Rectangle(x, midY - ArrowSize / 2, ArrowSize, ArrowSize);
            PaintArrow(g, n.ArrowRc, n.Expanded);
        }
        x += ArrowSize + 2;

        // Иконка
        if (n.IconFile != null)
        {
            Image? img = Icons.Scaled(n.IconFile, IconSize);
            if (img != null)
            {
                g.DrawImage(img, x, midY - IconSize / 2, IconSize, IconSize);
                x += IconSize + 4;
            }
        }

        // Метка подавления: квадратик поверх левого нижнего угла иконки.
        // Именно ею подавленная операция отличается от той, что просто
        // ниже полоски расчёта.
        if (n.Item?.Suppressed ?? false)
        {
            int m = Math.Max(5, IconSize / 2);
            var rc = new Rectangle(x - IconSize - 4, midY + IconSize / 2 - m, m, m);
            using var b = new SolidBrush(SuppressedMarkColor);
            g.FillRectangle(b, rc);
        }

        // Имя
        Color textColor = NodeTextColor;
        if (n.Item?.Error != null) textColor = NodeErrorColor;
        else if (n.IsGroup || (n.Item?.Suppressed ?? false)) textColor = NodeTextDimColor;

        // Ниже полоски расчёта операции не выполняются и тоже показываются
        // погашенными. ОТЛИЧАЮТСЯ они меткой: у подавленной вручную стоит
        // квадратик, у не дошедшей до расчёта — только затенение области.
        //
        // Различать обязательно. В Creo оба состояния помечены одинаково,
        // и это прямо мешает: непонятно, что подавлено насовсем, а что
        // просто ниже полоски.
        else if (n.Item is Feature bf && bf.Parent is Part bp && bp.IsBeyondStage(bf))
            textColor = NodeTextDimColor;
        else if (n.Item is { Visible: false }) textColor = NodeTextDimColor;

        bool active = n.Item != null && n.Item.Id == ActiveId;
        Font font = active ? BoldFont : Font;

        int nameRight = Columns[ColName].Left + Columns[ColName].Width - 4;
        using (var tb = new SolidBrush(textColor))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            var r = new Rectangle(x, n.Row.Y, Math.Max(0, nameRight - x), NodeHeight);
            g.DrawString(n.Text, font, tb, r, sf);
        }

        // ID
        if (n.Item != null)
        {
            var c = Columns[ColId];
            using var tb = new SolidBrush(NodeTextDimColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            g.DrawString(n.Item.Id.ToString(), Font, tb,
                new Rectangle(c.Left + 4, n.Row.Y, Math.Max(0, c.Width - 8), NodeHeight), sf);
        }

        // Глаз видимости
        n.EyeRc = Rectangle.Empty;
        if (n.Item != null && n.HasVisibility)
        {
            var c = Columns[ColVisible];
            int size = 16;
            n.EyeRc = new Rectangle(c.Left + c.Width / 2 - size / 2, midY - size / 2, size, size);
            bool on = n.Item.Visible;
            Color col = (_eyeHover && hovered) ? EyeHoverColor : (on ? EyeOnColor : EyeOffColor);
            PaintEye(g, n.EyeRc, on, col);
        }
    }

    private static int Level(Node n)
    {
        int lvl = 0;
        for (Node? p = n.Parent; p is { Parent: not null }; p = p.Parent) lvl++;
        return lvl;
    }

    private void PaintArrow(Graphics g, Rectangle rc, bool expanded)
    {
        using var b = new SolidBrush(ArrowColor);
        float cx = rc.X + rc.Width * 0.5f, cy = rc.Y + rc.Height * 0.5f;
        float s = rc.Width * 0.30f;

        PointF[] pts = expanded
            ? new[] { new PointF(cx - s, cy - s * 0.6f), new PointF(cx + s, cy - s * 0.6f), new PointF(cx, cy + s * 0.9f) }
            : new[] { new PointF(cx - s * 0.6f, cy - s), new PointF(cx + s * 0.9f, cy), new PointF(cx - s * 0.6f, cy + s) };

        g.FillPolygon(b, pts);
    }

    /// <summary>
    /// «Глаз» вместо чекбокса: открытый — объект показан, перечёркнутый — скрыт.
    /// Рисуется вектором, чтобы не тащить шрифт иконок.
    /// </summary>
    private static void PaintEye(Graphics g, Rectangle rc, bool open, Color color)
    {
        using var pen = new Pen(color, 1.4f);
        using var brush = new SolidBrush(color);

        float cx = rc.X + rc.Width * 0.5f, cy = rc.Y + rc.Height * 0.5f;
        float w = rc.Width * 0.46f, h = rc.Height * 0.28f;

        using (var path = new GraphicsPath())
        {
            path.AddBezier(cx - w, cy, cx - w * 0.45f, cy - h * 1.7f, cx + w * 0.45f, cy - h * 1.7f, cx + w, cy);
            path.AddBezier(cx + w, cy, cx + w * 0.45f, cy + h * 1.7f, cx - w * 0.45f, cy + h * 1.7f, cx - w, cy);
            g.DrawPath(pen, path);
        }

        if (open)
            g.FillEllipse(brush, cx - h * 0.6f, cy - h * 0.6f, h * 1.2f, h * 1.2f);
        else
            g.DrawLine(pen, cx - w * 0.95f, cy + h * 1.3f, cx + w * 0.95f, cy - h * 1.3f);
    }

    // ------------------------------------------------------------- ввод

    private Node? NodeAt(Point p)
    {
        Rectangle view = ViewRect;
        if (!view.Contains(p)) return null;
        int scroll = _vscroll.Visible ? _vscroll.Value : 0;
        int i = (p.Y - view.Y + scroll) / NodeHeight;
        return i >= 0 && i < _visible.Count ? _visible[i] : null;
    }

    private int ResizeHandleAt(Point p)
    {
        if (!ResizableColumns || !ShowHeader || p.Y >= HeaderHeight) return -1;
        for (int i = 0; i < Columns.Count - 1; i++)
        {
            int x = Columns[i].Left + Columns[i].Width;
            if (Math.Abs(p.X - x) <= 3) return i;
        }
        return -1;
    }

    /// <summary>
    /// Число операций, соответствующее положению курсора.
    ///
    /// Полоска встаёт в ближайший зазор между строками операций, поэтому
    /// сравнивается расстояние до КАЖДОГО зазора, а не «в какой строке
    /// курсор»: у зазора нет высоты, и попадание в него надо мерить,
    /// а не проверять.
    /// </summary>
    private int StageFromPoint(Part part, int y)
    {
        var rows = FeatureRows(part);
        if (rows.Count == 0) return 0;

        Rectangle view = ViewRect;
        int scroll = _vscroll.Visible ? _vscroll.Value : 0;

        int best = 0;
        int bestDist = int.MaxValue;

        for (int stage = 0; stage <= rows.Count; stage++)
        {
            int gapY = StageY(part, stage, view, scroll);
            int d = Math.Abs(gapY - y);
            if (d < bestDist) { bestDist = d; best = stage; }
        }

        return best;
    }

    /// <summary>Курсор на полоске расчёта?</summary>
    private bool OverStageBar(Point p, out Part? part)
    {
        part = StagePart();
        if (part == null) return false;

        Rectangle view = ViewRect;
        int scroll = _vscroll.Visible ? _vscroll.Value : 0;
        int y = StageY(part, part.EffectiveStage, view, scroll);

        return Math.Abs(p.Y - y) <= StageGrabPx;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_stageDrag && _stageDragPart != null)
        {
            int wanted = StageFromPoint(_stageDragPart, e.Y);
            int stage = _stageDragPart.ClampStage(wanted);

            // Знак запрета, когда курсор просит недопустимое место.
            Cursor = wanted == stage ? Cursors.SizeNS : Cursors.No;

            if (stage != _stageDragIndex)
            {
                _stageDragIndex = stage;
                Invalidate();
            }
            return;
        }

        if (!_resizing && OverStageBar(e.Location, out _))
        {
            Cursor = Cursors.SizeNS;
            return;
        }

        if (_resizing)
        {
            var c = Columns[_resizeIndex];
            c.Width = Math.Clamp(_resizeStartWidth + (e.X - _mouseDown.X), c.MinWidth, c.MaxWidth);
            LayoutColumns();
            Invalidate();
            return;
        }

        int handle = ResizeHandleAt(e.Location);
        _resizeReady = handle >= 0;
        _resizeIndex = handle;
        Cursor = _resizeReady ? Cursors.SizeWE : Cursors.Default;

        Node? n = NodeAt(e.Location);
        bool eye = n?.EyeRc.Contains(e.Location) ?? false;

        if (!ReferenceEquals(n, _hover) || eye != _eyeHover)
        {
            _hover = n;
            _eyeHover = eye;
            Invalidate();
            ItemHover?.Invoke(n?.Item);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == null) return;
        _hover = null;
        _eyeHover = false;
        Invalidate();
        ItemHover?.Invoke(null);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _mouseDown = e.Location;

        if (_resizeReady && e.Button == MouseButtons.Left)
        {
            _resizing = true;
            _resizeStartWidth = Columns[_resizeIndex].Width;
            return;
        }

        if (e.Button == MouseButtons.Left && OverStageBar(e.Location, out Part? sp) && sp != null)
        {
            _stageDrag = true;
            _stageDragPart = sp;
            _stageDragIndex = sp.ClampStage(sp.EffectiveStage);
            return;
        }

        Node? n = NodeAt(e.Location);
        if (n == null) return;

        // Стрелка раскрытия
        if (n.Children.Count > 0 && n.ArrowRc.Contains(e.Location))
        {
            n.Expanded = !n.Expanded;
            UpdateVisibleList();
            UpdateScroll();
            Invalidate();
            return;
        }

        // Переключатель видимости
        if (n.Item != null && n.HasVisibility && n.EyeRc.Contains(e.Location))
        {
            n.Item.Visible = !n.Item.Visible;

            // С этого момента видимостью распоряжается пользователь,
            // и автоматическое скрытие использованных эскизов её не тронет.
            n.Item.VisibleUserSet = true;
            VisibilityChanged?.Invoke(n.Item, n.Item.Visible);
            Invalidate();
            return;
        }

        _selected = n;
        Invalidate();
        if (n.Item != null) ItemSelected?.Invoke(n.Item);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _boldFont?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _resizing = false;

        if (!_stageDrag) return;

        _stageDrag = false;
        Cursor = Cursors.Default;

        var part = _stageDragPart;
        _stageDragPart = null;

        if (part == null) return;

        // Полоска в самом низу означает «считать всё»: тогда добавленные
        // потом операции тоже попадут в расчёт, а с числом их пришлось бы
        // двигать вручную.
        int stage = _stageDragIndex >= part.Features.Count ? -1 : _stageDragIndex;

        if (stage == part.StageIndex) { Invalidate(); return; }

        part.StageIndex = stage;
        StageChanged?.Invoke(part, stage);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Node? n = NodeAt(e.Location);
        if (n?.Item == null) return;
        if (n.EyeRc.Contains(e.Location) || n.ArrowRc.Contains(e.Location)) return;
        ItemActivated?.Invoke(n.Item);
    }

    private void OnWheel(object? sender, MouseEventArgs e)
    {
        if (!_vscroll.Visible) return;
        int v = _vscroll.Value - Math.Sign(e.Delta) * NodeHeight * 3;
        _vscroll.Value = Math.Clamp(v, _vscroll.Minimum, _vscroll.Maximum);
        Invalidate();
    }
}
