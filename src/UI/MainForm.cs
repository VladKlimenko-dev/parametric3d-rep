using OpenTK.Mathematics;
using Parametric3D.App;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using Parametric3D.Render;
using Parametric3D.Views;

namespace Parametric3D.UI;

/// <summary>
/// Главное окно: только логика.
///
/// Разметка живёт в MainForm.Designer.cs и правится конструктором форм.
/// Правило простое: добавили кнопку в конструкторе — привяжите её Click
/// к методу из этого файла; создавать что-либо в коде не нужно.
/// </summary>
public partial class MainForm : Form
{
    private Document _doc = Document.NewSinglePart();

    /// <summary>
    /// Какой инструмент эскиза стоит за какой кнопкой. Связывает разметку
    /// с логикой, не заставляя конструктор форм разбираться в типах.
    /// </summary>
    private readonly Dictionary<ToolStripButton, Func<ISketchTool>> _toolFactories = new();

    private bool _syncingHover;

    public MainForm()
    {
        InitializeComponent();

        ApplyIcons(this);
        ApplyFonts();
        BuildToolMap();

        // ДИАГНОСТИКА 3D-ВИДА. Проблема OpenGL — коротко в строке состояния,
        // сводка — в подсказке и в меню View. Подробности пишутся в журнал
        // (см. GLHealth). События приходят в потоке интерфейса: из кадра,
        // из таймеров, из событий окна.
        glView.Health.Problem += msg =>
        {
            if (IsDisposed) return;
            statusInfoLabel.Text = "3D-вид: " + msg;
            statusInfoLabel.ToolTipText = glView.Health.Report();
        };

        var glDiagnosticsMenuItem = new ToolStripMenuItem("GL diagnostics…");
        glDiagnosticsMenuItem.Click += (_, _) =>
            MessageBox.Show(this, glView.Health.Report(), "Диагностика 3D-вида",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        viewMenu.DropDownItems.Insert(viewMenu.DropDownItems.IndexOf(viewMenuSep1), glDiagnosticsMenuItem);

        // Отсутствие ядра показывает строка состояния (см. UpdateStatus):
        // коротко в самой строке, полностью — в её подсказке. Окно на старте
        // здесь ни к чему: работать без ядра можно.

        projectTree.ItemHover += projectTree_ItemHover;
        projectTree.ItemSelected += projectTree_ItemSelected;
        projectTree.ItemActivated += projectTree_ItemActivated;
        projectTree.StageChanged += projectTree_StageChanged;
        projectTree.VisibilityChanged += projectTree_VisibilityChanged;

        glView.ModeChanged += glView_ModeChanged;
        glView.SelectionChanged += glView_SelectionChanged;
        glView.ToolChanged += UpdateToolButtons;
        glView.CommandMessage += glView_CommandMessage;
        glView.DimensionEditRequested += glView_DimensionEditRequested;
        glView.DimensionMenuRequested += glView_DimensionMenuRequested;
        glView.ConstraintConflictFound += glView_ConstraintConflictFound;
        glView.AxisPicked += glView_AxisPicked;
        glView.SketchPicked += glView_SketchPicked;
        glView.PlanePicked += glView_PlanePicked;
        glView.OriginPicked += glView_OriginPicked;
        glView.PickModeChanged += glView_PickModeChanged;

        // Сетка меняется при масштабировании, поэтому строку состояния
        // обновляем после колеса — отложенно, чтобы кадр успел пересчитать шаг.
        glView.MouseWheel += (_, _) => BeginInvoke(new Action(UpdateStatus));

        LoadDocument(_doc);
    }

    // -------------------------------------------------------------- подготовка

    /// <summary>
    /// Подставляет картинки кнопкам. Путь берётся из Tag, файл — из папки
    /// Resources рядом с исполняемым. Файла нет — кнопка остаётся текстовой,
    /// это не ошибка.
    /// </summary>
    private static void ApplyIcons(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ToolStrip ts) ApplyIcons(ts.Items);
            ApplyIcons(c);
        }
        if (root is Form f && f.MainMenuStrip != null) ApplyIcons(f.MainMenuStrip.Items);
    }

    private static void ApplyIcons(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is string file && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                Image? img = Icons.Get(file);
                if (img != null) item.Image = img;
                else if (item is ToolStripButton b && b.DisplayStyle == ToolStripItemDisplayStyle.Image)
                    b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            }

            if (item is ToolStripDropDownItem dd) ApplyIcons(dd.DropDownItems);
        }
    }

    /// <summary>
    /// Шрифты интерфейса из настроек. Размётка задаёт их по умолчанию,
    /// здесь они перекрываются тем, что выбрано в настройках документа.
    /// </summary>
    private void ApplyFonts()
    {
        var c = Core.Settings.Current;
        Font = c.MakeFont(c.UiFontFamily, c.UiFontSize);
        projectTree.Font = c.TreeFont();
        statusStrip.Font = c.StatusFont();
        statusModeLabel.Font = c.StatusModeFont();
    }

    private void BuildToolMap()
    {
        _toolFactories[sketchPointButton] = () => new PointTool();
        _toolFactories[sketchLineButton] = () => new LineTool(true);
        _toolFactories[sketchCenterlineButton] = () => new LineTool(false) { DrawRole = SkRole.Centerline };
        _toolFactories[sketchRectangleButton] = () => new RectangleTool();
        _toolFactories[sketchCircleButton] = () => new CircleTool(false);
        _toolFactories[sketchCircle3pButton] = () => new CircleTool(true);
        _toolFactories[sketchEllipseButton] = () => new EllipseTool();
        _toolFactories[sketchArcCppButton] = () => new ArcTool(false);
        _toolFactories[sketchArcPppButton] = () => new ArcTool(true);
        _toolFactories[sketchCurveButton] = () => new SplineTool();
    }

    private void LoadDocument(Document doc)
    {
        _doc = doc;
        glView.SetDocument(doc);
        projectTree.SetDocument(doc);
        _doc.Invalidated += () => { projectTree.Rebuild(); UpdateStatus(); };
        _doc.BuildStarted += () => SetBuilding(true);
        _doc.BuildFinished += () => SetBuilding(false);

        // ДОКУМЕНТ С ОДНОЙ ДЕТАЛЬЮ ОТКРЫВАЕТСЯ НА УРОВНЕ ДЕТАЛИ.
        //
        // На уровне сборки щелчок по грани, ребру или вершине означает
        // выбор ДЕТАЛИ ЦЕЛИКОМ — так и задумано: там оперируют деталями,
        // а не их гранями. Пока документ открывался пустым, это никого
        // не задевало: выбирать было нечего, и первая же команда уводила
        // внутрь детали.
        //
        // С появлением тела при запуске то же поведение стало выглядеть
        // поломкой выбора: щелчок по ребру подсвечивал весь куб. Сборки
        // из одной детали не существует, и начинать в ней незачем.
        var single = _doc.AllParts().ToList();
        if (single.Count == 1) glView.EnterPart(single[0]);

        UpdateStatus();
    }

    private Part CurrentPart =>
        glView.ActivePart ?? _doc.AllParts().FirstOrDefault() ?? _doc.Root.AddPart("Part1");

    private DatumPlane? SelectedPlane()
    {
        foreach (var i in glView.Selection.Items)
            if (i.Target is DatumPlane dp) return dp;
        return null;
    }

    /// <summary>
    /// Плоскость для нового эскиза: базовая плоскость ИЛИ плоская грань тела.
    ///
    /// ДОБАВЛЕНО. Раньше смотрели только на DatumPlane, и выбор грани куба
    /// не считался выбором плоскости — операция открывала обычный диалог,
    /// как будто ничего не выбрано. Между тем грань детали это самое частое,
    /// на чём строят следующий эскиз.
    ///
    /// Грань ищется ПО НОМЕРУ: ScenePicker кладёт в ссылку только номер
    /// и владельца, а сам топологический объект оставляет пустым —
    /// искать его для каждого кандидата под курсором значит делать
    /// линейный поиск внутри цикла по всей сетке на каждое движение мыши.
    ///
    /// Возвращает false, если ничего подходящего не выбрано.
    /// </summary>
    private bool SelectedSketchPlane(Part part, out Frame3 frame, out uint sourceId, out string name)
    {
        frame = default;
        sourceId = 0;
        name = "";

        if (SelectedPlane() is { } dp)
        {
            frame = dp.Frame;
            sourceId = dp.Id;
            name = dp.Name;
            return true;
        }

        foreach (var i in glView.Selection.Items)
        {
            if (i.Kind != PickKind.Face) continue;

            foreach (var body in part.Bodies)
            {
                var face = body.Faces.FirstOrDefault(fc => fc.Id == i.Id);
                if (face == null) continue;

                // Эскиз кладётся только на плоскую грань: у кривой нет
                // единой плоскости, и «положить на неё эскиз» ничего
                // не значит. Плоскость — свойство поверхности, а не её
                // класса: боковая грань призмы несёт ExtrudedSurface
                // и при этом плоская.
                if (!face.Surface.IsPlanar) continue;

                var corners = face.Edges.SelectMany(ed => new[] { ed.V0.P, ed.V1.P }).ToList();
                if (corners.Count == 0) continue;

                Vector3d centre = corners.Aggregate(Vector3d.Zero, (a, v) => a + v) / corners.Count;

                Vector3d n = face.NormalAt(centre);
                if (n.LengthSquared < 1e-18) continue;

                frame = Frame3.FromNormal(centre, n.Normalized());
                sourceId = face.Id;
                name = $"Face #{face.Id}";
                return true;
            }
        }

        return false;
    }

    // -------------------------------------------------------------- файл, меню

    private void newFileButton_Click(object? sender, EventArgs e) => LoadDocument(Document.NewSinglePart());

    private void openMenuItem_Click(object? sender, EventArgs e) => NotImplementedYet("Opening files");

    private void saveButton_Click(object? sender, EventArgs e) => NotImplementedYet("Saving");

    /// <summary>
    /// ЭКСПОРТ СЕТКИ: STL (двоичный или текстовый) и OBJ, в миллиметрах.
    ///
    /// В режиме детали — текущая деталь в своих координатах; иначе — все детали
    /// сборки на своих местах. Перед записью сетка проверяется на замкнутость —
    /// о дырах в файле человек узнаёт сразу, а не в программе, которая его откроет.
    /// </summary>
    private void exportMenuItem_Click(object? sender, EventArgs e)
    {
        if (_doc.IsBuilding) return;

        var parts = new List<(Part Part, OpenTK.Mathematics.Matrix4 World)>();
        if (glView.Mode != EditMode.Assembly && CurrentPart is { } one)
            parts.Add((one, OpenTK.Mathematics.Matrix4.Identity));
        else
            _doc.Root.CollectParts(parts, OpenTK.Mathematics.Matrix4.Identity);

        parts.RemoveAll(x => x.Part.Bodies.Count == 0);
        if (parts.Count == 0)
        {
            MessageBox.Show(this, "There is no body to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Export",
            Filter = "STL, binary (*.stl)|*.stl|STL, text (*.stl)|*.stl|Wavefront OBJ (*.obj)|*.obj|STEP, exact B-rep (*.step;*.stp)|*.step;*.stp|STEP, faceted (*.step;*.stp)|*.step;*.stp",
            FilterIndex = 4,
            FileName = parts.Count == 1 ? parts[0].Part.Name : _doc.Title,
            AddExtension = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // KRN-09 (g.11): ТОЧНЫЙ STEP — принятая геометрия (поверхности и топология), а не сетка. Не пишется точно (тело
        // многогранное по устройству, масштаб в размещении) — файл не создаётся, человек видит причину и может выбрать
        // гранёный STEP сам. Прежде интерфейс умел только гранёный STEP.
        if (dlg.FilterIndex == 4)
        {
            try
            {
                var exactParts = parts.Select(x => (x.Part, x.World == OpenTK.Mathematics.Matrix4.Identity ? (OpenTK.Mathematics.Matrix4d?)null : ToDouble(x.World))).ToList();
                var sw = new StringWriter();
                var rep = Parametric3D.Model.Exchange.WriteStep(sw, exactParts, Path.GetFileNameWithoutExtension(dlg.FileName));
                if (!rep.Exact)
                {
                    MessageBox.Show(this, "The exact STEP was not written: " + rep.Summary + "\nChoose \"STEP, faceted\" to export the mesh instead.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                File.WriteAllText(dlg.FileName, sw.ToString());
                Report($"Exported {rep.Summary} to {Path.GetFileName(dlg.FileName)}.");
            }
            catch (Exception ex)
            {
                Core.AppLog.Write("export", ex);
                MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        try
        {
            // По телу на оболочку STEP; для сеточных форматов — все треугольники подряд.
            var shells = new List<IReadOnlyList<Kernel.Export.ExportTriangle>>();
            foreach (var (part, world) in parts)
            {
                OpenTK.Mathematics.Matrix4d? m = world == OpenTK.Mathematics.Matrix4.Identity ? null : ToDouble(world);
                foreach (var body in part.Bodies) shells.Add(Kernel.Export.MeshExport.Triangles(new[] { body }, m));
            }
            var tris = shells.SelectMany(x => x).ToList();

            string name = Path.GetFileNameWithoutExtension(dlg.FileName);
            switch (dlg.FilterIndex)
            {
                case 1:
                    using (var fs = File.Create(dlg.FileName)) Kernel.Export.MeshExport.WriteStlBinary(fs, tris, name);
                    break;
                case 2:
                    using (var sw = new StreamWriter(dlg.FileName)) Kernel.Export.MeshExport.WriteStlAscii(sw, tris, name);
                    break;
                case 3:
                    using (var sw = new StreamWriter(dlg.FileName)) Kernel.Export.MeshExport.WriteObj(sw, tris, name);
                    break;
                default:
                    using (var sw = new StreamWriter(dlg.FileName)) Kernel.Export.MeshExport.WriteStepFaceted(sw, shells, name);
                    break;
            }

            // Проверяется то, что реально записано: у STL — float, у OBJ — double.
            Kernel.Export.MeshExport.MeshCheck check;
            if (dlg.FilterIndex <= 2)
            {
                var ms = new MemoryStream();
                Kernel.Export.MeshExport.WriteStlBinary(ms, tris, name);
                ms.Position = 0;
                check = Kernel.Export.MeshExport.Check(Kernel.Export.MeshExport.ReadStlBinary(ms));
            }
            else if (dlg.FilterIndex == 3) check = Kernel.Export.MeshExport.Check(tris.Select(t => (t.A, t.B, t.C)).ToList());
            else check = Kernel.Export.MeshExport.CheckStep(File.ReadAllText(dlg.FileName));
            string file = Path.GetFileName(dlg.FileName);
            Report(check.Holes > 0
                ? $"Exported {check.Triangles} triangles to {file}, but the mesh has {check.Holes} open edges."
                : check.Touching > 0
                    ? $"Exported {check.Triangles} triangles to {file}; closed, but the body touches itself along {check.Touching} edges."
                    : $"Exported {check.Triangles} triangles to {file} (closed, volume {check.Volume:0.###} mm\u00b3).");
        }
        catch (Exception ex)
        {
            Core.AppLog.Write("export", ex);
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static OpenTK.Mathematics.Matrix4d ToDouble(OpenTK.Mathematics.Matrix4 f) => new(
            new OpenTK.Mathematics.Vector4d(f.M11, f.M12, f.M13, f.M14),
            new OpenTK.Mathematics.Vector4d(f.M21, f.M22, f.M23, f.M24),
            new OpenTK.Mathematics.Vector4d(f.M31, f.M32, f.M33, f.M34),
            new OpenTK.Mathematics.Vector4d(f.M41, f.M42, f.M43, f.M44));
    }

    private void propertiesMenuItem_Click(object? sender, EventArgs e) => NotImplementedYet("Document properties");

    private void exitMenuItem_Click(object? sender, EventArgs e) => Close();

    private void undoButton_Click(object? sender, EventArgs e) => NotImplementedYet("Undo/redo");

    private void redoButton_Click(object? sender, EventArgs e) => NotImplementedYet("Undo/redo");

    private void settingsButton_Click(object? sender, EventArgs e) => NotImplementedYet("Settings");

    /// <summary>
    /// ТЕСТОВАЯ ФИГУРА: куб 100 в начале координат детали.
    ///
    /// Нужна не для красоты, а для повторяемости. Проверка операции
    /// начинается с того, что кто-то рисует квадрат мышью, и каждый раз
    /// он получается чуть другой: другие размеры, другие ограничения,
    /// другое положение относительно начала. Расхождение результата потом
    /// не с чем сравнить.
    ///
    /// Куб строится ТЕМ ЖЕ ПУТЁМ, каким его построил бы человек: эскиз
    /// на плоскости XY, четыре отрезка, выдавливание. Построить тело мимо
    /// дерева было бы проще, но тогда проверялось бы не то, чем пользуются.
    ///
    /// Симметрично относительно плоскости эскиза: центр куба попадает
    /// в начало координат, и числа в проверках получаются круглые —
    /// грани при ±50, объём 1 000 000.
    /// </summary>
    /// <summary>
    /// Вставляет ту же тестовую фигуру, что документ получает при создании.
    /// Построение живёт в Model/TestGeometry.cs: повторять его здесь
    /// значило бы завести вторую версию, расходящуюся с первой.
    /// </summary>
    private void testCubeMenuItem_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        if (glView.Mode == EditMode.Assembly) glView.EnterPart(part);

        TestGeometry.AddCube(part, 100);

        glView.MarkDirty();
        glView.ZoomToFit(glView.CurrentBounds());
        projectTree.Rebuild();
        ReportBuild(part);
        UpdateStatus();
    }

    private void helpButton_Click(object? sender, EventArgs e) =>
        MessageBox.Show(this,
            "Parametric3D\nParametric CAD built on OpenTK 4.\n\n" +
            "Middle mouse button rotates, right button pans, wheel zooms to the cursor.\n" +
            "Double click steps into a part or a sketch, Escape steps back out.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void parametersButton_Click(object? sender, EventArgs e)
    {
        using var f = new ParametersForm(_doc);
        f.ShowDialog(this);
        glView.MarkDirty();
    }

    // --------------------------------------------------------------------- вид

    private void gridMenuItem_CheckedChanged(object? sender, EventArgs e)
    {
        glView.ShowGrid = gridMenuItem.Checked;
        glView.Invalidate();
    }

    private void datumPlanesMenuItem_CheckedChanged(object? sender, EventArgs e) =>
        glView.ShowDatumPlanes = datumPlanesMenuItem.Checked;

    /// <summary>
    /// Включает сверку булевых операций с объёмной моделью.
    ///
    /// Деталь после этого пересчитывается целиком: сверка живёт внутри
    /// операции, и без нового построения её выводу взяться неоткуда —
    /// галочка выглядела бы нажатой, а в предупреждениях было бы пусто.
    /// </summary>
    /// <summary>
    /// Замер скорости на текущей детали. Скорость — самое слабое место ядра, и мерить её надо
    /// на живой модели и на своей машине: в среде разработки одно ядро, и многопоточность там
    /// не проверить.
    /// </summary>
    private void speedReportMenuItem_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;

        // Замер идёт двенадцать перестроений (прогрев, пять на один поток, пять на все ядра и
        // один с засечками): на тяжёлой детали это минуты, и предупредить надо заранее.
        if (MessageBox.Show(this,
                "Замер перестроит деталь около двенадцати раз. На тяжёлой детали это займёт минуты.\n\nПродолжить?",
                "Скорость перестроения", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
            != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        string report;
        try { report = SpeedReport.Measure(part, _doc.Parameters); }
        finally { Cursor = Cursors.Default; }

        using var dlg = new Form
        {
            Text = "Скорость перестроения",
            Width = 720,
            Height = 560,
            StartPosition = FormStartPosition.CenterParent
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            Text = report.Replace("\n", Environment.NewLine)
        };

        dlg.Controls.Add(box);
        dlg.ShowDialog(this);
    }

    private void auditBooleansMenuItem_CheckedChanged(object? sender, EventArgs e)
    {
        Kernel.Ops.BooleanOp.Audit = auditBooleansMenuItem.Checked;

        _doc.InvalidateAll();
        glView.MarkDirty();
        UpdateStatus();
    }

    private void showDimensionsMenuItem_CheckedChanged(object? sender, EventArgs e)
    {
        Core.Settings.Current.ShowSelectedItemDimensions = showDimensionsMenuItem.Checked;
        glView.Invalidate();
    }

    private void fitAllMenuItem_Click(object? sender, EventArgs e) =>
        glView.ZoomToFit(glView.CurrentBounds());

    private void viewSwitchButton_Click(object? sender, EventArgs e) =>
        glView.Projection = glView.Projection == ProjectionMode.Perspective
            ? ProjectionMode.Orthographic
            : ProjectionMode.Perspective;

    private void styleShadedMenuItem_Click(object? sender, EventArgs e) =>
        SetBodyMode(BodyViewMode.Shaded);

    private void styleShadedEdgesMenuItem_Click(object? sender, EventArgs e) =>
        SetBodyMode(BodyViewMode.ShadedWithEdges);

    private void styleTransparentMenuItem_Click(object? sender, EventArgs e) =>
        SetBodyMode(BodyViewMode.Transparent);

    private void styleMeshMenuItem_Click(object? sender, EventArgs e) =>
        SetBodyMode(BodyViewMode.Mesh);

    private void styleVolumeMeshMenuItem_Click(object? sender, EventArgs e) =>
        SetBodyMode(BodyViewMode.VolumeMesh);


    /// <summary>Переключает стиль отображения и отмечает выбранный пункт списка.</summary>
    private void SetBodyMode(BodyViewMode mode)
    {
        glView.BodyMode = mode;
        styleShadedMenuItem.Checked = mode == BodyViewMode.Shaded;
        styleShadedEdgesMenuItem.Checked = mode == BodyViewMode.ShadedWithEdges;
        styleTransparentMenuItem.Checked = mode == BodyViewMode.Transparent;
        styleMeshMenuItem.Checked = mode == BodyViewMode.Mesh;
        styleVolumeMeshMenuItem.Checked = mode == BodyViewMode.VolumeMesh;

        // Вид заполнения говорит числами: сколько кубов, какая вилка
        // объёма и сошлась ли она с объёмом по оболочке. В строке
        // состояния это появляется сразу после переключения.
        UpdateStatus();
    }

    private void sketchViewButton_Click(object? sender, EventArgs e) => glView.LookAtActivePlane();

    private void viewFrontMenuItem_Click(object? sender, EventArgs e) => glView.AnimateTo(ViewOrientation.Front);
    private void viewBackMenuItem_Click(object? sender, EventArgs e) => glView.AnimateTo(ViewOrientation.Back);
    private void viewLeftMenuItem_Click(object? sender, EventArgs e) => glView.AnimateTo(ViewOrientation.Left);
    private void viewRightMenuItem_Click(object? sender, EventArgs e) => glView.AnimateTo(ViewOrientation.Right);
    private void viewTopMenuItem_Click(object? sender, EventArgs e) => glView.AnimateTo(ViewOrientation.Top);
    private void viewStandardMenuItem_Click(object? sender, EventArgs e) => glView.AnimateTo(ViewOrientation.Isometric);

    // -------------------------------------------------------------- построение

    /// <summary>
    /// Ручной пересчёт всех операций до полоски.
    ///
    /// Нужен, когда пересчёт не запустился сам: например изменился параметр
    /// или материал, а связь с деталью неочевидна.
    /// </summary>
    private async void rebuildButton_Click(object? sender, EventArgs e)
    {
        foreach (var part in _doc.Root.Descendants().OfType<Part>()) part.Invalidate();

        await _doc.RebuildDirtyAsync();
        glView.MarkDirty();
        projectTree.Rebuild();
        ReportBuild(CurrentPart);
    }

    /// <summary>
    /// Выход из режима вставки.
    ///
    /// Спрашиваем, что делать с операциями ниже полоски: вернуть их в расчёт
    /// или оставить вне модели. Тот же выбор предлагает Creo при отмене
    /// режима вставки, и он не праздный — иногда полоску поднимали именно
    /// затем, чтобы отложить хвост построения.
    /// </summary>
    private async void cancelInsertMenuItem_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        if (!part.InInsertMode) return;

        int pending = part.Features.Count - part.EffectiveStage;

        var answer = MessageBox.Show(this,
            $"Resume {pending} feature(s) below the build bar?\n\n" +
            "Yes - build them again.\n" +
            "No - keep them out of the model as suppressed.",
            "Cancel insert mode",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (answer == DialogResult.Cancel) return;

        part.EndInsertMode(answer == DialogResult.Yes);

        await _doc.RebuildDirtyAsync();
        glView.MarkDirty();
        projectTree.Rebuild();
        UpdateStatus();
        ReportBuild(part);
    }

    /// <summary>
    /// Полоску расчёта перетащили: деталь перестраивается до нового места.
    /// </summary>
    private async void projectTree_StageChanged(Part part, int stage)
    {
        part.Invalidate();
        await _doc.RebuildDirtyAsync();
        glView.MarkDirty();
        projectTree.Rebuild();

        UpdateStatus();

        int total = part.Features.Count;
        int done = part.EffectiveStage;
        Report(done >= total
            ? $"{part.Name}: all {total} feature(s) built."
            : $"{part.Name}: built {done} of {total} feature(s).");
    }

    private void newPartButton_Click(object? sender, EventArgs e)
    {
        var p = _doc.Root.AddPart($"Part{_doc.AllParts().Count() + 1}");
        projectTree.Rebuild();
        glView.EnterPart(p);
    }

    private void newSketchButton_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        var plane = SelectedPlane() ?? part.PlaneXY;
        if (glView.Mode == EditMode.Assembly) glView.EnterPart(part);
        glView.NewSketchOn(plane);
        projectTree.Rebuild();
    }

    private void newPlaneButton_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        var basePlane = SelectedPlane() ?? part.PlaneXY;

        using var dlg = new ValueInputForm("New plane", "Offset:", 20);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var dp = new DatumPlane($"Plane{part.Planes.Count() + 1}", basePlane.Frame.OffsetAlongNormal(dlg.Value1))
        {
            Mode = DatumPlaneMode.OffsetFromFace,
            Offset = dlg.Value1
        };
        dp.References.Add(basePlane.Id);
        part.Add(dp);
        glView.MarkDirty();
        projectTree.Rebuild();
    }

    private void newPointsButton_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        int n = part.Children.OfType<DatumPoint>().Count();
        part.Add(new DatumPoint($"Point{n}", OpenTK.Mathematics.Vector3d.Zero));
        glView.MarkDirty();
        projectTree.Rebuild();
    }

    private void newLineButton_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        int n = part.Children.OfType<Line3DItem>().Count() + 1;
        part.Add(new Line3DItem($"Line{n}",
            OpenTK.Mathematics.Vector3d.Zero, new OpenTK.Mathematics.Vector3d(50, 0, 0)));
        glView.MarkDirty();
        projectTree.Rebuild();
    }

    private void extrudeButton_Click(object? sender, EventArgs e) => AddFeature(new ExtrudeFeature());
    private void revolveButton_Click(object? sender, EventArgs e) => AddFeature(new RevolveFeature());
    private void sweepButton_Click(object? sender, EventArgs e) => AddFeature(new SweepFeature());
    private void roundButton_Click(object? sender, EventArgs e) => AddFeature(new RoundFeature());
    private void chamferButton_Click(object? sender, EventArgs e) => AddFeature(new ChamferFeature());

    // Операция, для которой сейчас рисуется эскиз. Она ещё не добавлена
    // в деталь: попадёт туда только после настройки.
    private Feature? _pendingFeature;
    private Part? _pendingFeaturePart;
    private Sketch? _pendingFeatureSketch;

    /// <summary>
    /// Начинает операцию «с чистого листа»: создаёт эскиз на выбранной
    /// плоскости и открывает его на черчение.
    ///
    /// Так экономится обычная последовательность из трёх команд — создать
    /// эскиз, начертить, выдавить. Признак того, что пользователь хочет
    /// именно этого, — выбранная плоскость: строить по ней больше нечего.
    /// </summary>
    private bool StartFeatureWithNewSketch(Feature f, Part part)
    {
        if (!SupportsSketchFirst(f)) return false;

        if (!SelectedSketchPlane(part, out Frame3 frame, out uint sourceId, out string name))
            return false;

        NewSketchForFeature(f, part, frame, sourceId, name);
        return true;
    }

    /// <summary>
    /// Создаёт эскиз под операцию и открывает его на черчение.
    ///
    /// Плоскость необязательна: кнопка «New sketch» в форме операции
    /// работает и без выделения — тогда берётся первая базовая плоскость
    /// детали, а сменить её можно в настройках эскиза.
    /// </summary>
    private void NewSketchForFeature(Feature f, Part part, DatumPlane? plane)
    {
        plane ??= part.PlaneXY;
        NewSketchForFeature(f, part, plane.Frame, plane.Id, plane.Name);
    }

    /// <summary>
    /// То же, но по готовой плоскости: она может прийти и от грани тела,
    /// у которой никакого DatumPlane нет.
    /// </summary>
    private void NewSketchForFeature(Feature f, Part part, Frame3 frame, uint sourceId, string name)
    {
        var sketch = new Sketch($"Sketch{part.Sketches.Count() + 1}", frame);
        sketch.SetBasePlane(frame, sourceId, name);
        Parametric3D.Model.Features.SketchFaceRef.AttachById(sketch, part.Bodies, sourceId);
        part.Add(sketch);

        _pendingFeature = f;
        _pendingFeaturePart = part;
        _pendingFeatureSketch = sketch;

        projectTree.Rebuild();
        glView.EnterSketch(sketch);
        UpdateStatus();
    }

    private static bool SupportsSketchFirst(Feature f) =>
        f is ExtrudeFeature or RevolveFeature;

    /// <summary>Эскиз, выбранный сейчас в виде или в дереве.</summary>
    private Sketch? SelectedSketch()
    {
        foreach (var i in glView.Selection.Items)
        {
            if (i.Target is Sketch s) return s;
            if (i.Owner is Sketch o) return o;
        }
        return null;
    }

    /// <summary>Эскиз закончен: настраиваем и добавляем отложенную операцию.</summary>
    private void FinishPendingFeature(Sketch? sketch)
    {
        var f = _pendingFeature;
        var part = _pendingFeaturePart;
        if (f == null || part == null || sketch == null) return;

        _pendingFeature = null;
        _pendingFeaturePart = null;
        _pendingFeatureSketch = null;

        AssignSketch(f, sketch);

        if (!ShowFeatureDialog(f, part))
        {
            // От операции отказались — эскиз остаётся: он нарисован
            // и может пригодиться. Удалять чужую работу без спроса нельзя.
            return;
        }

        part.AddFeature(f);
        part.Invalidate();
        glView.MarkDirty();
        projectTree.Rebuild();
        ReportBuild(part);
    }

    /// <summary>Отмена эскиза отменяет и операцию, ради которой он создавался.</summary>
    private void CancelPendingFeature(Sketch? sketch)
    {
        if (_pendingFeature == null) return;

        var part = _pendingFeaturePart;
        var created = _pendingFeatureSketch;

        _pendingFeature = null;
        _pendingFeaturePart = null;
        _pendingFeatureSketch = null;

        // Эскиз создавался под эту операцию — вместе с ней и уходит.
        if (part != null && created != null && ReferenceEquals(created, sketch))
            part.Remove(created);

        projectTree.Rebuild();
        glView.MarkDirty();
    }

    /// <summary>
    /// Выбранные рёбра как ссылки ПО ГЕОМЕТРИИ.
    ///
    /// Номера рёбер живут до следующей перестройки, а операция должна
    /// пережить их все. Поэтому в неё уходят концевые точки, а номера
    /// нужны только здесь — чтобы найти ребро в текущем теле.
    /// </summary>
    private List<EdgeRef> SelectedEdgeRefs(Part part)
    {
        var ids = new HashSet<uint>(glView.Selection.IdsOfKind(PickKind.Edge));
        var refs = new List<EdgeRef>();
        if (ids.Count == 0) return refs;

        foreach (var body in part.Bodies)
            foreach (var edge in body.Edges)
                if (ids.Contains(edge.Id))
                    refs.Add(EdgeRef.Of(edge));

        return refs;
    }

    private static void AssignSketch(Feature f, Sketch sketch)
    {
        switch (f)
        {
            case ExtrudeFeature ex: ex.SketchId = sketch.Id; break;
            case RevolveFeature rv: rv.SketchId = sketch.Id; break;
            case SweepFeature sw: sw.SectionSketchId = sketch.Id; break;
        }
    }

    private void AddFeature(Feature f)
    {
        var part = CurrentPart;
        if (glView.Mode == EditMode.Assembly) glView.EnterPart(part);

        // Выбран эскиз — он и подставится в форму операции.
        if (SelectedSketch() is { } chosen) AssignSketch(f, chosen);

        // Выбрана плоскость — начинаем с эскиза на ней.
        if (StartFeatureWithNewSketch(f, part)) return;

        // Вращение может уйти на выбор оси и вернуться позже: помечаем,
        // что операция ещё не добавлена в деталь.
        _pendingRevolveIsNew = f is RevolveFeature;

        if (!ShowFeatureDialog(f, part)) return;
        _pendingRevolveIsNew = false;

        part.AddFeature(f);
        part.Invalidate();
        glView.MarkDirty();
        projectTree.Rebuild();
        ReportBuild(part);
    }


    private bool ShowFeatureDialog(Feature f, Part part) => f switch
    {
        ExtrudeFeature ex => ShowExtrudeDialog(ex, part),
        RevolveFeature rv => ShowRevolveDialog(rv, part),
        SweepFeature sw => ShowSweepDialog(sw, part),
        RoundFeature rd => new RoundDialog(rd, _doc.Parameters,
            SelectedEdgeRefs(part)).ShowDialog(this) == DialogResult.OK,
        ChamferFeature ch => new ChamferDialog(ch, _doc.Parameters,
            SelectedEdgeRefs(part)).ShowDialog(this) == DialogResult.OK,
        _ => false
    };

    // Операция вращения, ожидающая выбора оси на эскизе. Диалог на это
    // время закрыт: модальное окно не отдало бы виду ни одного события мыши.
    private RevolveFeature? _pendingRevolve;
    private Part? _pendingRevolvePart;
    private bool _pendingRevolveIsNew;

    /// <summary>
    /// Показывает диалог вращения. Возвращает false, если пользователь ушёл
    /// выбирать ось: операция будет достроена после выбора.
    /// </summary>
    private bool ShowExtrudeDialog(ExtrudeFeature ex, Part part)
    {
        using var dlg = new ExtrudeForm(ex, part, _doc.Parameters);
        var result = dlg.ShowDialog(this);

        if (result == DialogResult.OK) return true;
        if (dlg.NewSketchRequested) NewSketchForFeature(ex, part, SelectedPlane());
        return false;
    }

    private bool ShowSweepDialog(SweepFeature sw, Part part)
    {
        using var dlg = new SweepForm(sw, part, _doc.Parameters);
        var result = dlg.ShowDialog(this);

        if (result == DialogResult.OK) return true;

        if (dlg.NewSketchRequested) NewSketchForFeature(sw, part, SelectedPlane());
        else if (dlg.PickPath) BeginSweepPathPick(sw, part);

        return false;
    }

    /// <summary>
    /// Путь протягивания указывается щелчком по эскизу в трёхмерном виде:
    /// в списке эскизы легко перепутать, а на модели видно, какой где.
    /// </summary>
    private void BeginSweepPathPick(SweepFeature sw, Part part)
    {
        _pendingSweep = sw;
        _pendingSweepPart = part;
        glView.EnterPart(part);
        glView.BeginSketchPick();
    }

    private SweepFeature? _pendingSweep;
    private Part? _pendingSweepPart;

    private void glView_SketchPicked(Sketch sketch)
    {
        var sw = _pendingSweep;
        var part = _pendingSweepPart;
        _pendingSweep = null;
        _pendingSweepPart = null;

        if (sw == null || part == null) return;

        sw.PathSketchId = sketch.Id;

        if (!ShowSweepDialog(sw, part)) return;

        part.AddFeature(sw);
        part.Invalidate();
        glView.MarkDirty();
        projectTree.Rebuild();
        ReportBuild(part);
    }

    private bool ShowRevolveDialog(RevolveFeature rv, Part part)
    {
        using var dlg = new RevolveForm(rv, part, _doc.Parameters);
        var result = dlg.ShowDialog(this);

        if (result == DialogResult.OK) return true;

        if (dlg.NewSketchRequested)
        {
            NewSketchForFeature(rv, part, SelectedPlane());
            return false;
        }

        if (dlg.PickAxis && dlg.ChosenSketch is { } sketch)
        {
            _pendingRevolve = rv;
            _pendingRevolvePart = part;

            glView.EnterPart(part);
            glView.BeginAxisPick(sketch);
        }

        return false;
    }

    /// <summary>Ось указана: возвращаем диалог с заполненным полем.</summary>
    private void glView_AxisPicked(Sketch sketch, SkLine line)
    {
        var rv = _pendingRevolve;
        var part = _pendingRevolvePart;
        _pendingRevolve = null;
        _pendingRevolvePart = null;

        if (rv == null || part == null) return;

        rv.AxisEntityId = line.Id;

        if (!ShowRevolveDialog(rv, part)) return;

        if (_pendingRevolveIsNew)
        {
            _pendingRevolveIsNew = false;
            part.AddFeature(rv);
        }

        part.Invalidate();
        glView.MarkDirty();
        projectTree.Rebuild();
        ReportBuild(part);
    }

    private void EditFeature(Feature f, Part part)
    {
        if (!ShowFeatureDialog(f, part)) return;
        part.Invalidate();
        glView.MarkDirty();
        projectTree.Rebuild();
        ReportBuild(part);
    }

    private void materialButton_Click(object? sender, EventArgs e)
    {
        var part = CurrentPart;
        using var dlg = new MaterialForm(part, _doc.Materials);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        glView.MarkDirty();
        projectTree.Rebuild();
        UpdateStatus();
    }

    // ------------------------------------------------------------------- эскиз

    private void sketchSetupButton_Click(object? sender, EventArgs e)
    {
        if (glView.ActiveSketch == null) { Report("No active sketch."); return; }
        ShowSketchSetup(glView.ActiveSketch);
    }

    /// <summary>
    /// Настройки эскиза. Окно НЕМОДАЛЬНОЕ: во время выбора плоскости
    /// и нулевой точки оно остаётся открытым, а вид принимает мышь.
    ///
    /// Модальное окно забирало бы все события мыши, и подсветить плоскость
    /// под курсором было бы нечем.
    /// </summary>
    private void ShowSketchSetup(Sketch sketch)
    {
        // Второе окно не открываем: настройки одни на эскиз.
        if (_setupForm != null && !_setupForm.IsDisposed)
        {
            _setupForm.Activate();
            return;
        }

        _setupSketch = sketch;
        glView.ShowViewArrowFor(sketch);

        var f = new SketchSetupForm(sketch);
        _setupForm = f;

        f.PlanePickRequested += sk => glView.BeginBasePlanePick(sk);
        f.OriginPickRequested += sk => glView.BeginOriginPick(sk);

        f.Flipped += _ =>
        {
            glView.MarkDirty();
            UpdateStatus();
        };

        f.UpChanged += sk =>
        {
            // Оси эскиза повернулись: решаем заново и перестраиваем деталь —
            // тело, построенное по эскизу, встанет в новое положение.
            sk.Solve(_doc.Solver);
            if (sk.Parent is Part part) part.Invalidate();

            // И нацеливаем камеру по новым осям, иначе сетка с чертежом
            // окажутся повёрнутыми относительно экрана.
            glView.RealignSketchCamera();

            glView.MarkDirty();
            projectTree.Rebuild();
            UpdateStatus();
        };

        f.Applied += sk =>
        {
            sk.Solve(_doc.Solver);
            glView.MarkDirty();
            projectTree.Rebuild();
            UpdateStatus();
        };

        f.Dismissed += () =>
        {
            _setupForm = null;
            _setupSketch = null;
            glView.ShowViewArrowFor(null);
            glView.EndBasePick();
            glView.MarkDirty();
            UpdateStatus();
        };

        f.Show(this);
    }

    /// <summary>Открытое окно настроек. null — закрыто.</summary>
    private SketchSetupForm? _setupForm;

    /// <summary>
    /// Эскиз, настройки которого сейчас правятся.
    ///
    /// Хранится отдельно от окна: выбор плоскости и нулевой точки приходит
    /// событием от вида, и получателю нужно знать, к какому эскизу его
    /// отнести. Само окно при этом остаётся открытым.
    /// </summary>
    private Sketch? _setupSketch;

    private void glView_PlanePicked(Frame3 frame, uint sourceId, string description)
    {
        var sketch = _setupSketch;
        if (sketch == null) return;

        sketch.SetBasePlane(frame, sourceId, description);
        // KRN-08: грань тела — эскиз запоминает её имя и идёт за ней при перестроении
        Parametric3D.Model.Features.SketchFaceRef.AttachById(sketch, CurrentPart.Bodies, sourceId);
        glView.MarkDirty();
        projectTree.Rebuild();

        // Окно открыто — просто обновляем в нём поля.
        _setupForm?.RefreshFromSketch();
        UpdateStatus();
    }

    private void glView_OriginPicked(Vector3d point, string description)
    {
        var sketch = _setupSketch;
        if (sketch == null) return;

        sketch.SetOrigin(point, description);
        glView.MarkDirty();

        _setupForm?.RefreshFromSketch();
        UpdateStatus();
    }

    /// <summary>Список ссылочной геометрии активного эскиза.</summary>
    private void sketchReferencesButton_Click(object? sender, EventArgs e)
    {
        var sk = glView.ActiveSketch;
        if (sk == null) { Report("No active sketch."); return; }

        using var f = new SketchReferencesForm(sk);
        f.ShowDialog(this);

        if (!f.Changed) return;

        sk.Solve(_doc.Solver);
        glView.MarkDirty();
        UpdateStatus();
    }
    private void sketchConstructionButton_Click(object? sender, EventArgs e) => SetRole(SkRole.Construction);

    private void SetRole(SkRole role)
    {
        if (glView.ActiveSketch == null) { Report("No active sketch."); return; }
        SketchCommands.SetRole(glView, role);
    }

    /// <summary>Общий обработчик всех кнопок инструментов эскиза.</summary>
    private void sketchTool_Click(object? sender, EventArgs e)
    {
        if (sender is not ToolStripButton b) return;
        if (!_toolFactories.TryGetValue(b, out var make)) return;
        glView.SetTool(b.Checked ? make() : null);
    }

    private void sketchFilletButton_Click(object? sender, EventArgs e)
    {
        using var dlg = new ValueInputForm("Sketch fillet", "Radius:", 5);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Report(SketchCommands.Fillet(glView, dlg.Value1));
    }

    private void sketchChamferButton_Click(object? sender, EventArgs e)
    {
        using var dlg = new ValueInputForm("Sketch chamfer", "Distance 1:", 3, "Distance 2:", 3);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Report(SketchCommands.Chamfer(glView, dlg.Value1, dlg.Value2));
    }

    /// <summary>
    /// Размер и ограничения работают режимом набора: кнопка остаётся нажатой,
    /// пользователь щёлкает элементы эскиза, команда применяется, как только
    /// их набралось достаточно. Escape или повторное нажатие выключает режим.
    /// </summary>
    private void sketchDimButton_Click(object? sender, EventArgs e)
    {
        if (sketchDimButton.Checked)
        {
            ClearPickButtons(sketchDimButton);
            glView.BeginDimensionMode();
        }
        else glView.EndPickMode();
    }

    private void sketchSnapGridButton_Click(object? sender, EventArgs e)
    {
        if (glView.ActiveSketch == null) return;
        glView.ActiveSketch.SnapToGrid = sketchSnapGridButton.Checked;
    }

    private void sketchSnapItemsButton_Click(object? sender, EventArgs e) =>
        glView.SnapToItems = sketchSnapItemsButton.Checked;

    private void sketchOkButton_Click(object? sender, EventArgs e)
    {
        var sketch = glView.ActiveSketch;
        glView.FinishSketch();

        // Эскиз рисовался под операцию — самое время её настроить.
        FinishPendingFeature(sketch);
        UpdateStatus();
    }

    /// <summary>
    /// Отмена в эскизе: правки откатываются, приложение выходит
    /// в трёхмерный вид. Инструмент отменяется клавишей Escape,
    /// поэтому кнопка занимается именно эскизом.
    /// </summary>
    private void sketchCancelButton_Click(object? sender, EventArgs e)
    {
        var sketch = glView.ActiveSketch;
        glView.CancelSketch();

        CancelPendingFeature(sketch);
        UpdateStatus();
    }

    private void constVerticalButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Vertical);
    private void constHorizontalButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Horizontal);
    private void constPerpendicularButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Perpendicular);
    private void constTangentButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Tangent);
    private void constMidButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.MidPoint);
    private void constCoincidentButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Coincident);
    private void constSymmetricButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Symmetric);
    private void constEqualButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Equal);
    private void constParallelButton_Click(object? sender, EventArgs e) => Constrain(ConstraintType.Parallel);

    private void Constrain(ConstraintType t)
    {
        var button = ConstraintButton(t);
        if (button != null && button.Checked)
        {
            ClearPickButtons(button);
            glView.BeginConstraintMode(t);
        }
        else glView.EndPickMode();
    }

    private ToolStripButton? ConstraintButton(ConstraintType t) => t switch
    {
        ConstraintType.Vertical => constVerticalButton,
        ConstraintType.Horizontal => constHorizontalButton,
        ConstraintType.Perpendicular => constPerpendicularButton,
        ConstraintType.Parallel => constParallelButton,
        ConstraintType.Tangent => constTangentButton,
        ConstraintType.Equal => constEqualButton,
        ConstraintType.Coincident => constCoincidentButton,
        ConstraintType.MidPoint => constMidButton,
        ConstraintType.Symmetric => constSymmetricButton,
        _ => null
    };

    /// <summary>Одновременно может быть нажата только одна кнопка режима.</summary>
    private void ClearPickButtons(ToolStripButton? keep)
    {
        foreach (var b in new[]
        {
            constVerticalButton, constHorizontalButton, constPerpendicularButton, constParallelButton,
            constTangentButton, constEqualButton, constCoincidentButton, constMidButton,
            constSymmetricButton, sketchDimButton
        })
        {
            if (!ReferenceEquals(b, keep) && b.Checked) b.Checked = false;
        }
    }

    private void glView_CommandMessage(string? text)
    {
        _pickHint = text;
        UpdateStatus();
    }

    private void glView_PickModeChanged()
    {
        if (!glView.InPickMode) ClearPickButtons(null);
        UpdateStatus();
    }

    /// <summary>Подсказка активного режима набора для строки состояния.</summary>
    private string? _pickHint;

    // ------------------------------------------------------------- размеры

    /// <summary>Двойной щелчок по подписи размера — ввод нового значения.</summary>
    private void glView_DimensionEditRequested(SkDimension d) => ModifyDimension(d);

    private void ModifyDimension(SkDimension d)
    {
        string title = d.IsAngular ? "Angle" : "Dimension";

        // Пользователь видит и вводит величину без знака. Знак координатного
        // размера — это сторона от нуля, ею управляет перетаскивание точки,
        // а не поле ввода.
        double shown = d.IsAngular ? d.Value : Math.Abs(d.Value);

        using var dlg = new ValueInputForm(title, d.IsAngular ? "Angle, \u00b0:" : "Value:", shown);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        double entered = d.IsAngular
            ? dlg.Value1
            : Math.Abs(dlg.Value1) * (d.Value < 0 ? -1 : 1);

        Report(SketchCommands.ModifyDimension(glView, d, entered));
    }

    // Контекстное меню размера создаётся ОДИН раз и живёт вместе с формой.
    //
    // Раньше меню строилось на каждый щелчок и уничтожалось в обработчике
    // Closed. Это и вызывало ObjectDisposedException: WinForms сначала
    // закрывает меню, а потом продолжает обрабатывать нажатый пункт —
    // обновляет отметку, перерисовывает полосу. К этому моменту объект
    // уже уничтожен. Уничтожать меню изнутри его же события нельзя.
    private ContextMenuStrip? _dimensionMenu;
    private ToolStripMenuItem? _dimModifyItem;
    private ToolStripMenuItem? _dimLockItem;
    private ToolStripMenuItem? _dimReferenceItem;
    private ToolStripMenuItem? _dimDeleteItem;
    private ToolStripMenuItem? _dimRadiusItem;
    private ToolStripMenuItem? _dimArrowsItem;

    /// <summary>Размер, для которого открыто меню.</summary>
    private SkDimension? _menuDimension;

    private void EnsureDimensionMenu()
    {
        if (_dimensionMenu != null) return;

        // Контейнер создаётся здесь, если его ещё нет.
        //
        // В разметке он заводится в InitializeComponent, но конструктор форм
        // при перезаписи файла эту строку выбрасывает, когда ни один элемент
        // контейнера не требует. Полагаться на неё нельзя: получали
        // ArgumentNullException при первом же вызове меню.
        //
        // Dispose в разметке проверяет components на null, поэтому созданный
        // тут контейнер корректно уничтожится вместе с формой.
        components ??= new System.ComponentModel.Container();
        _dimensionMenu = new ContextMenuStrip(components);

        _dimModifyItem = new ToolStripMenuItem("Modify...");
        _dimModifyItem.Click += dimensionModify_Click;

        _dimLockItem = new ToolStripMenuItem("Lock");
        _dimLockItem.Click += dimensionLock_Click;

        _dimReferenceItem = new ToolStripMenuItem("Reference");
        _dimReferenceItem.Click += dimensionReference_Click;

        _dimRadiusItem = new ToolStripMenuItem("Show as diameter");
        _dimRadiusItem.Click += dimensionRadiusDiameter_Click;

        _dimArrowsItem = new ToolStripMenuItem("Arrows outside");
        _dimArrowsItem.Click += dimensionArrows_Click;

        _dimDeleteItem = new ToolStripMenuItem("Delete");
        _dimDeleteItem.Click += dimensionDelete_Click;

        _dimensionMenu.Items.AddRange(new ToolStripItem[]
        {
            _dimModifyItem, _dimLockItem, _dimReferenceItem,
            new ToolStripSeparator(), _dimRadiusItem, _dimArrowsItem,
            new ToolStripSeparator(), _dimDeleteItem
        });
    }

    /// <summary>Щелчок правой кнопкой по подписи размера.</summary>
    private void glView_DimensionMenuRequested(SkDimension d, Point at)
    {
        EnsureDimensionMenu();

        _menuDimension = d;
        // Lock и есть «сделать управляющим»: заблокированное значение
        // решатель обязан выдержать, незаблокированное — только измеряет.
        _dimLockItem!.Text = d.Locked ? "Unlock (measure only)" : "Lock (drive geometry)";
        _dimReferenceItem!.Checked = d.Reference;

        // Переключение радиус/диаметр имеет смысл только у круглых.
        bool circular = d.Type is DimensionType.Radius or DimensionType.Diameter;
        _dimRadiusItem!.Visible = circular;
        _dimRadiusItem.Text = d.Type == DimensionType.Radius ? "Show as diameter" : "Show as radius";

        _dimArrowsItem!.Checked = d.Arrows == ArrowPlacement.Outside;


        _dimensionMenu!.Show(glView, at);
    }

    private void dimensionModify_Click(object? sender, EventArgs e)
    {
        var d = _menuDimension;
        if (d == null) return;

        // Диалог открывается ПОСЛЕ того, как меню свернётся: модальное окно
        // прямо из обработчика пункта запускает вложенный цикл сообщений
        // поверх ещё не закрытого меню.
        BeginInvoke(new Action(() => ModifyDimension(d)));
    }

    private void dimensionLock_Click(object? sender, EventArgs e)
    {
        if (_menuDimension == null) return;

        _menuDimension.Locked = !_menuDimension.Locked;

        // Заблокированный размер перестаёт быть «по умолчанию»: пересборка
        // набора его больше не тронет.
        if (_menuDimension.Locked) _menuDimension.IsDefault = false;

        glView.ActiveSketch?.Solve(_doc.Solver);
        glView.MarkDirty();
        UpdateStatus();
    }

    private void dimensionReference_Click(object? sender, EventArgs e)
    {
        if (_menuDimension == null) return;

        // Отметку ставим сами, без CheckOnClick: тот меняет состояние
        // пункта уже во время закрытия меню.
        _menuDimension.Reference = !_menuDimension.Reference;
        glView.MarkDirty();
        UpdateStatus();
    }

    /// <summary>
    /// Радиус в диаметр и обратно. Меняется и тип, и величина: диаметр
    /// вдвое больше радиуса, и решатель работает с тем, что записано.
    /// </summary>
    private void dimensionRadiusDiameter_Click(object? sender, EventArgs e)
    {
        var d = _menuDimension;
        if (d == null) return;

        if (d.Type == DimensionType.Radius)
        {
            d.Type = DimensionType.Diameter;
            d.Value *= 2;
        }
        else if (d.Type == DimensionType.Diameter)
        {
            d.Type = DimensionType.Radius;
            d.Value *= 0.5;
        }

        glView.ActiveSketch?.Solve(_doc.Solver);
        glView.MarkDirty();
    }

    private void dimensionArrows_Click(object? sender, EventArgs e)
    {
        var d = _menuDimension;
        if (d == null) return;

        d.Arrows = d.Arrows == ArrowPlacement.Outside
            ? ArrowPlacement.Inside
            : ArrowPlacement.Outside;

        glView.MarkDirty();
    }

    private void dimensionDelete_Click(object? sender, EventArgs e)
    {
        var d = _menuDimension;
        if (d == null) return;

        _menuDimension = null;
        SketchCommands.DeleteDimension(glView, d);
        UpdateStatus();
    }

    // ---------------------------------------------------------------- удаление

    private void clearSelectionMenuItem_Click(object? sender, EventArgs e) => glView.Selection.Clear();

    /// <summary>
    /// Удаление работает во всех режимах: что именно удаляется, решает вид,
    /// исходя из уровня работы и типа выбранного объекта.
    /// </summary>
    private void deleteButton_Click(object? sender, EventArgs e)
    {
        if (glView.Selection.IsEmpty)
        {
            Report("Nothing is selected.");
            return;
        }

        string what = glView.Selection.Describe();
        int n = glView.DeleteSelected();

        projectTree.Rebuild();
        UpdateStatus();

        if (n == 0)
            MessageBox.Show(this,
                what + " cannot be deleted here.\n" +
                "Base planes and the origin are part of every part and assembly.",
                "Delete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Конфликт ограничений: показываем список и делаем то, что выбрал
    /// пользователь. Если он ничего не удалил, спорящие помечаются
    /// конфликтующими — красными и исключёнными из решателя.
    /// </summary>
    private void glView_ConstraintConflictFound(SkConstraint added, List<Conflict> conflicts)
    {
        var sk = glView.ActiveSketch;
        if (sk == null) return;

        using var dlg = new ConstraintConflictForm(added, conflicts);
        dlg.ShowDialog(this);

        switch (dlg.ToRemove)
        {
            case SkConstraint c: sk.Constraints.Remove(c); break;
            case SkDimension d: sk.Dimensions.Remove(d); break;

            // Ничего не убрали — так и оставляем. Признаки конфликта
            // расставит пересчёт при решении: вручную их держать не нужно,
            // иначе они переживают удаление второго спорщика.
        }

        sk.Solve(_doc.Solver);
        glView.MarkDirty();
        UpdateStatus();
    }


    // ---------------------------------------------------------- дерево и выбор

    /// <summary>Курсор над строкой дерева — тот же объект подсвечивается во вьюпорте.</summary>
    private void projectTree_ItemHover(ModelItem? item)
    {
        if (_syncingHover) return;
        _syncingHover = true;
        glView.HighlightFromTree(item);
        _syncingHover = false;
    }

    private void projectTree_ItemSelected(ModelItem item)
    {
        glView.SelectFromTree(item);
        UpdateStatus();
    }

    private void projectTree_ItemActivated(ModelItem item)
    {
        switch (item)
        {
            case Sketch sk:
                if (sk.Parent is Part sp) glView.EnterPart(sp);
                glView.EnterSketch(sk);
                break;
            case Part p:
                glView.EnterPart(p);
                break;
            case Feature f when f.Parent is Part fp:
                EditFeature(f, fp);
                break;
            case DatumPlane dp:
                glView.NewSketchOn(dp);
                projectTree.Rebuild();
                break;
        }
    }

    private void projectTree_VisibilityChanged(ModelItem item, bool visible) => glView.MarkDirty();

    /// <summary>Наведение во вьюпорте подсвечивает строку дерева.</summary>
    private void glView_SelectionChanged()
    {
        if (!_syncingHover)
        {
            _syncingHover = true;
            projectTree.HighlightItem(OwnerId(glView.Selection.Hover));
            uint sel = OwnerId(glView.Selection.Primary);
            if (sel != 0) projectTree.SelectItem(sel, scrollTo: false);
            _syncingHover = false;
        }
        UpdateStatus();
    }

    /// <summary>Топологический элемент сам в дереве не показан — подсвечиваем его владельца.</summary>
    private static uint OwnerId(PickRef r)
    {
        if (r.IsEmpty) return 0;
        if (r.Target is ModelItem mi) return mi.Id;
        if (r.Owner is ModelItem owner) return owner.Id;
        return 0;
    }

    /// <summary>
    /// Фильтр выбираемой геометрии. Пункты названы как в исходной форме,
    /// поэтому нескольким из них соответствует один и тот же тип: и Curve,
    /// и Centerline, и Sketch — это кривые эскиза.
    /// </summary>
    private void selectFilterMenuItem_CheckedChanged(object? sender, EventArgs e)
    {
        PickFilter f = PickFilter.None;

        if (quiltSelectMenuItem.Checked) f |= PickFilter.Faces;
        if (edgeSelectMenuItem.Checked) f |= PickFilter.Edges;
        if (vertexSelectMenuItem.Checked) f |= PickFilter.Vertices;
        if (curveSelectMenuItem.Checked || centerlineSelectMenuItem.Checked || sketchSelectMenuItem.Checked)
            f |= PickFilter.SketchCurves | PickFilter.SketchPoints;
        if (planeSelectMenuItem.Checked || axisSelectMenuItem.Checked) f |= PickFilter.Datums;
        if (partSelectMenuItem.Checked || featureSelectMenuItem.Checked) f |= PickFilter.Parts;

        glView.Selection.Filter = f;
    }

    // ----------------------------------------------------------- режим, статус

    private void mainToolbar_SelectedIndexChanged(object? sender, EventArgs e) => UpdateSketchTabEnabled();

    private void glView_ModeChanged()
    {
        bool sketching = glView.Mode == EditMode.Sketch;
        mainToolbar.SelectedTab = sketching ? sketchTab : modelTab;
        UpdateSketchTabEnabled();

        if (!sketching) glView.SetTool(null);
        if (glView.ActiveSketch != null) sketchSnapGridButton.Checked = glView.ActiveSketch.SnapToGrid;

        projectTree.Rebuild();
        UpdateStatus();
    }

    private void UpdateSketchTabEnabled()
    {
        bool sketching = glView.Mode == EditMode.Sketch;
        foreach (Control c in sketchTab.Controls) c.Enabled = sketching;
    }

    private void UpdateToolButtons()
    {
        var active = glView.Tool;
        foreach (var pair in _toolFactories)
            pair.Key.Checked = active != null && SameTool(pair.Value, active);
        UpdateStatus();
    }

    /// <summary>
    /// Сравнение по заголовку: создать пробный инструмент дешевле, чем
    /// сверять типы, и это различает LineTool(true) и LineTool(false),
    /// у которых тип один и тот же.
    /// </summary>
    private static bool SameTool(Func<ISketchTool> factory, ISketchTool active)
    {
        try { return factory().Title == active.Title; }
        catch { return false; }
    }

    private void UpdateStatus()
    {
        statusModeLabel.Text = glView.Mode switch
        {
            EditMode.Assembly => "Mode: Assembly",
            EditMode.Part => "Mode: Part - " + (glView.ActivePart?.Name ?? "-"),
            _ => "Mode: Sketch - " + (glView.ActiveSketch?.Name ?? "-")
        };

        // Что под курсором важнее того, что выбрано: пока курсор на чём-то
        // стоит, строка показывает это. Проекция стороннего элемента —
        // такой же случай, только объект чужой, поэтому и подпись другая.
        string hover = glView.Selection.DescribeHover();
        string projection = glView.HoverProjectionName;

        statusSelectionLabel.Text =
            hover.Length > 0 && glView.Selection.IsEmpty ? hover
            : projection.Length > 0 && glView.Selection.IsEmpty ? "Over: " + projection
            : glView.Selection.Describe();

        // Тот же активный уровень выделяется жирным в дереве.
        projectTree.SetActiveItem(glView.ActiveItem);

        var parts = new List<string>();

        // Режим вставки виден в строке состояния: о нём легко забыть,
        // а модель при этом построена не до конца.
        var insertPart = CurrentPart;
        cancelInsertMenuItem.Enabled = insertPart.InInsertMode;
        if (insertPart.InInsertMode)
            parts.Add($"INSERT MODE: {insertPart.EffectiveStage} of {insertPart.Features.Count}");

        if (_pickHint != null && glView.InPickMode) parts.Add(_pickHint);
        if (glView.Tool != null) parts.Add("Tool: " + glView.Tool.Title);

        string grid = glView.GridInfo;
        if (grid.Length > 0) parts.Add(grid);

        if (glView.ActiveSketch is { } sk)
            parts.Add(sk.LastSolve switch
            {
                SolveStatus.Ok => "Fully constrained",
                SolveStatus.Underconstrained => $"DOF: {sk.DegreesOfFreedom}",
                SolveStatus.Overconstrained => "Over-constrained",
                SolveStatus.DidNotConverge => "Solver did not converge",
                SolveStatus.Singular => "Singular (degenerate geometry)",
                _ => "Inconsistent constraints"
            });

        var part = glView.ActivePart;
        if (part is { Mesh: not null })
            parts.Add($"V = {part.Volume / 1000.0:0.##} cm\u00b3   A = {part.Area / 100.0:0.##} cm\u00b2   m = {part.Mass:0.###} kg");

        statusInfoLabel.Text = string.Join("     |     ", parts);
    }

    // ----------------------------------------------------------------- прочее

    /// <summary>
    /// ИДЁТ ФОНОВАЯ ПЕРЕСБОРКА: источники команд отключены, курсор — ожидание.
    /// Вид остаётся живым: его можно вращать, сдвигать и масштабировать.
    /// </summary>
    private void SetBuilding(bool on)
    {
        mainMenu.Enabled = !on;
        mainToolStrip.Enabled = !on;
        mainToolbar.Enabled = !on;
        projectTree.Enabled = !on;
        UseWaitCursor = on;
        if (on) statusInfoLabel.Text = "Rebuilding the model…";
        else UpdateStatus();
    }

    /// <summary>Пересборка — в фоне; отчёт о ней — когда она закончится.</summary>
    private async void ReportBuild(Part part)
    {
        // ОТЧЁТ ОБЯЗАН ОТНОСИТЬСЯ К ТЕКУЩЕМУ ДЕРЕВУ, А НЕ К ПРОШЛОМУ.
        //
        // ИСПРАВЛЕНО. Команды добавления операции делали так:
        //     part.AddFeature(f);
        //     part.Invalidate();      // только ПОМЕТИЛИ грязной
        //     projectTree.Rebuild();
        //     ReportBuild(part);      // а считать ещё не начинали
        //
        // Сам расчёт идёт позже, в OnPaint, через Document.RebuildDirty().
        // Поэтому окно показывало предупреждения от ПРЕДЫДУЩЕЙ пересборки,
        // то есть от дерева без только что добавленной операции. Дерево
        // при этом обновлялось сразу и показывало её — отсюда и расхождение:
        // в дереве фаска есть, в окне от неё ни строки, а на модели она
        // построена (расчёт прошёл, просто после того, как окно закрыли).
        //
        // Новая операция всегда оказывалась немой, а вместо неё звучала
        // предыдущая. Именно это мы и принимали за «фаска не отчитывается».
        try { await _doc.RebuildDirtyAsync(); }
        catch (Exception ex) { Core.AppLog.Write("rebuild", ex); }

        glView.MarkDirty();
        projectTree.Rebuild();
        UpdateStatus();

        var msgs = new List<string>();

        // СНАЧАЛА — КТО ВООБЩЕ СЧИТАЛСЯ.
        //
        // ДОБАВЛЕНО. Операция ниже полоски вставки и подавленная операция
        // не выполняются и потому МОЛЧАТ: ни предупреждения, ни ошибки.
        // На экране это неотличимо от операции, которая отработала
        // без замечаний, и однажды стоило целого круга поисков — искали
        // ошибку в фаске, которая не запускалась.
        //
        // Строка выводится, только когда есть о чём сказать: при полностью
        // посчитанном дереве она бы лишь занимала место.
        var states = new List<string>();

        for (int i = 0; i < part.Features.Count; i++)
        {
            Feature f = part.Features[i];

            string state =
                f.Suppressed ? "suppressed" :
                i >= part.EffectiveStage ? "below the insert bar" :
                f.Error != null ? "failed" :
                "calculated";

            states.Add($"{i + 1}. {f.Name} — {state}");
        }

        if (states.Count > 0)
            msgs.Add("Features: " + string.Join("; ", states) + ".");

        foreach (var f in part.Features)
        {
            if (f.Error == null) continue;

            // Операция часто сама начинает сообщение со своего имени;
            // второй раз его не добавляем — выходило «Round: Round: …».
            msgs.Add(f.Error.StartsWith(f.Name + ":", StringComparison.Ordinal)
                ? f.Error
                : $"{f.Name}: {f.Error}");
        }
        msgs.AddRange(part.Warnings.Distinct());

        UpdateStatus();
        if (msgs.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, msgs),
                "Build", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Report(string? error)
    {
        UpdateStatus();
        if (error != null)
            MessageBox.Show(this, error, "Sketch", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void NotImplementedYet(string what) =>
        MessageBox.Show(this, what + " is not implemented yet.", "Parametric3D",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_doc.IsBuilding) return;   // во время фоновой пересборки команды заперты
        if (e.KeyCode == Keys.Delete) { deleteButton_Click(this, EventArgs.Empty); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { glView.ZoomToFit(glView.CurrentBounds()); e.Handled = true; }
    }
}
