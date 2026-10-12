using Parametric3D.Views;

namespace Parametric3D.UI;

/// <summary>
/// Разметка главного окна. Лента собрана группами с подписями внизу —
/// так же, как панель Sketch в Creo: Setup, Datum, Sketching, Editing,
/// Constrain, Dimension, Utilities, Close.
///
/// ВАЖНО ПРО ЭТОТ ФАЙЛ. Здесь нет ни одного вызова вспомогательного
/// метода — только литеральные присваивания свойств. Конструктор форм
/// разбирает InitializeComponent сам и всё, что не понимает, при
/// сохранении ВЫБРАСЫВАЕТ. Ранее настройки кнопок были вынесены
/// в методы вида InitGridButton(...): стоило один раз открыть форму
/// в дизайнере — и он переписывал InitializeComponent без них,
/// после чего кнопки оставались без имени, размера, текста и иконки,
/// то есть попросту исчезали. Поэтому файл длинный и однообразный:
/// такова цена за то, чтобы дизайнер его не портил.
///
/// Иконки здесь НЕ загружаются. В Tag кнопки лежит путь к картинке
/// внутри встроенных ресурсов, а подставляет её ApplyIcons() уже после
/// InitializeComponent. Загрузку изображения дизайнер бы тоже не понял.
/// </summary>
partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private MenuStrip mainMenu;
    private ToolStripMenuItem fileMenu;
    private ToolStripMenuItem newMenuItem;
    private ToolStripMenuItem openMenuItem;
    private ToolStripMenuItem saveMenuItem;
    private ToolStripMenuItem exportMenuItem;
    private ToolStripMenuItem propertiesMenuItem;
    private ToolStripMenuItem exitMenuItem;
    private ToolStripMenuItem editMenu;
    private ToolStripMenuItem deleteMenuItem;
    private ToolStripMenuItem clearSelectionMenuItem;
    private ToolStripMenuItem viewMenu;
    private ToolStripMenuItem gridMenuItem;
    private ToolStripMenuItem datumPlanesMenuItem;
    private ToolStripMenuItem showDimensionsMenuItem;
    private ToolStripMenuItem auditBooleansMenuItem;
    private ToolStripMenuItem speedReportMenuItem;
    private ToolStripMenuItem testCubeMenuItem;
    private ToolStripMenuItem cancelInsertMenuItem;
    private ToolStripMenuItem fitAllMenuItem;
    private ToolStripSeparator fileMenuSep1;
    private ToolStripSeparator fileMenuSep2;
    private ToolStripSeparator viewMenuSep1;
    private ToolStrip mainToolStrip;
    private ToolStripButton newFileButton;
    private ToolStripButton saveButton;
    private ToolStripButton undoButton;
    private ToolStripButton redoButton;
    private ToolStripButton settingsButton;
    private ToolStripButton helpButton;
    private ToolStripSeparator toolStripSeparator1;
    private ToolStripSeparator toolStripSeparator2;
    private TabControl mainToolbar;
    private TabPage modelTab;
    private TabPage sketchTab;
    private Panel modelSetupPanel;
    private ToolStrip modelSetupStrip;
    private Label modelSetupLabel;
    private ToolStripButton newPartButton;
    private ToolStripButton rebuildButton;
    private Panel modelDatumPanel;
    private ToolStrip modelDatumStrip;
    private Label modelDatumLabel;
    private ToolStripButton newSketchButton;
    private ToolStripButton newPlaneButton;
    private ToolStripButton newPointsButton;
    private ToolStripButton newLineButton;
    private Panel modelShapePanel;
    private ToolStrip modelShapeStrip;
    private Label modelShapeLabel;
    private ToolStripButton extrudeButton;
    private ToolStripButton revolveButton;
    private ToolStripButton sweepButton;
    private Panel modelEngPanel;
    private ToolStrip modelEngStrip;
    private Label modelEngLabel;
    private ToolStripButton roundButton;
    private ToolStripButton chamferButton;
    private Panel modelEditPanel;
    private ToolStrip modelEditStrip;
    private Label modelEditLabel;
    private ToolStripButton deleteButton;
    private Panel modelDisplayPanel;
    private ToolStrip modelDisplayStrip;
    private Label modelDisplayLabel;
    private ToolStripButton viewSwitchButton;
    private ToolStripDropDownButton displayStyleButton;
    private ToolStripMenuItem styleShadedMenuItem;
    private ToolStripMenuItem styleShadedEdgesMenuItem;
    private ToolStripMenuItem styleTransparentMenuItem;
    private ToolStripMenuItem styleMeshMenuItem;
    private ToolStripMenuItem styleVolumeMeshMenuItem;
    private Panel modelPropPanel;
    private ToolStrip modelPropStrip;
    private Label modelPropLabel;
    private ToolStripButton parametersButton;
    private ToolStripButton materialButton;
    private Panel modelSep1;
    private Panel modelSep2;
    private Panel modelSep3;
    private Panel modelSep4;
    private Panel modelSep5;
    private Panel modelSep6;
    private Panel sketchSetupPanel;
    private ToolStrip sketchSetupStrip;
    private Label sketchSetupLabel;
    private ToolStripButton sketchSetupButton;
    private ToolStripButton sketchReferencesButton;
    private ToolStripButton sketchViewButton;
    private Panel sketchDatumPanel;
    private ToolStrip sketchDatumStrip;
    private Label sketchDatumLabel;
    private ToolStripButton sketchCenterlineButton;
    private ToolStripButton sketchPointButton;
    private ToolStripButton sketchConstructionButton;
    private Panel sketchingPanel;
    private ToolStrip sketchingStrip;
    private Label sketchingLabel;
    private ToolStripButton sketchLineButton;
    private ToolStripButton sketchRectangleButton;
    private ToolStripButton sketchCircleButton;
    private ToolStripButton sketchArcCppButton;
    private ToolStripButton sketchArcPppButton;
    private ToolStripButton sketchEllipseButton;
    private ToolStripButton sketchCircle3pButton;
    private ToolStripButton sketchCurveButton;
    private ToolStripButton sketchPolygonButton;
    private Panel sketchEditPanel;
    private ToolStrip sketchEditStrip;
    private Label sketchEditLabel;
    private ToolStripButton sketchFilletButton;
    private ToolStripButton sketchChamferButton;
    private ToolStripButton sketchDelSegmentButton;
    private Panel sketchConstrainPanel;
    private ToolStrip sketchConstrainStrip;
    private Label sketchConstrainLabel;
    private ToolStripButton constVerticalButton;
    private ToolStripButton constTangentButton;
    private ToolStripButton constSymmetricButton;
    private ToolStripButton constHorizontalButton;
    private ToolStripButton constMidButton;
    private ToolStripButton constEqualButton;
    private ToolStripButton constPerpendicularButton;
    private ToolStripButton constCoincidentButton;
    private ToolStripButton constParallelButton;
    private Panel sketchDimPanel;
    private ToolStrip sketchDimStrip;
    private Label sketchDimLabel;
    private ToolStripButton sketchDimButton;
    private Panel sketchUtilPanel;
    private ToolStrip sketchUtilStrip;
    private Label sketchUtilLabel;
    private ToolStripButton sketchSnapGridButton;
    private ToolStripButton sketchSnapItemsButton;
    private ToolStripButton sketchParamsButton;
    private Panel sketchClosePanel;
    private ToolStrip sketchCloseStrip;
    private Label sketchCloseLabel;
    private ToolStripButton sketchOkButton;
    private ToolStripButton sketchCancelButton;
    private Panel sketchSep1;
    private Panel sketchSep2;
    private Panel sketchSep3;
    private Panel sketchSep4;
    private Panel sketchSep5;
    private Panel sketchSep6;
    private Panel sketchSep7;
    private ToolStrip viewTools;
    private ToolStripButton perspViewButton;
    private ToolStripDropDownButton viewOrientButton;
    private ToolStripMenuItem viewFrontMenuItem;
    private ToolStripMenuItem viewBackMenuItem;
    private ToolStripMenuItem viewLeftMenuItem;
    private ToolStripMenuItem viewRightMenuItem;
    private ToolStripMenuItem viewTopMenuItem;
    private ToolStripMenuItem viewStandardMenuItem;
    private ToolStripMenuItem viewNormalMenuItem;
    private ToolStripSeparator viewOrientSep;
    private SplitContainer mainSplit;
    private ProjectTree projectTree;
    private GL3DView glView;
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusModeLabel;
    private ToolStripStatusLabel statusSelectionLabel;
    private ToolStripStatusLabel statusInfoLabel;
    private ToolStripDropDownButton selectGeometryButton;
    private ToolStripMenuItem planeSelectMenuItem;
    private ToolStripMenuItem quiltSelectMenuItem;
    private ToolStripMenuItem curveSelectMenuItem;
    private ToolStripMenuItem edgeSelectMenuItem;
    private ToolStripMenuItem centerlineSelectMenuItem;
    private ToolStripMenuItem axisSelectMenuItem;
    private ToolStripMenuItem sketchSelectMenuItem;
    private ToolStripMenuItem partSelectMenuItem;
    private ToolStripMenuItem featureSelectMenuItem;
    private ToolStripMenuItem vertexSelectMenuItem;
    private ToolStripMenuItem arrowSelectMenuItem;
    private ToolStripMenuItem dimensionSelectMenuItem;
    private ToolStripMenuItem constraintSelectMenuItem;

	private void InitializeComponent()
	{
		mainMenu = new MenuStrip();
		fileMenu = new ToolStripMenuItem();
		newMenuItem = new ToolStripMenuItem();
		openMenuItem = new ToolStripMenuItem();
		saveMenuItem = new ToolStripMenuItem();
		exportMenuItem = new ToolStripMenuItem();
		fileMenuSep1 = new ToolStripSeparator();
		propertiesMenuItem = new ToolStripMenuItem();
		fileMenuSep2 = new ToolStripSeparator();
		exitMenuItem = new ToolStripMenuItem();
		editMenu = new ToolStripMenuItem();
		deleteMenuItem = new ToolStripMenuItem();
		clearSelectionMenuItem = new ToolStripMenuItem();
		cancelInsertMenuItem = new ToolStripMenuItem();
		viewMenu = new ToolStripMenuItem();
		gridMenuItem = new ToolStripMenuItem();
		datumPlanesMenuItem = new ToolStripMenuItem();
		showDimensionsMenuItem = new ToolStripMenuItem();
		auditBooleansMenuItem = new ToolStripMenuItem();
		speedReportMenuItem = new ToolStripMenuItem();
		testCubeMenuItem = new ToolStripMenuItem();
		viewMenuSep1 = new ToolStripSeparator();
		fitAllMenuItem = new ToolStripMenuItem();
		mainToolStrip = new ToolStrip();
		newFileButton = new ToolStripButton();
		saveButton = new ToolStripButton();
		toolStripSeparator1 = new ToolStripSeparator();
		undoButton = new ToolStripButton();
		redoButton = new ToolStripButton();
		toolStripSeparator2 = new ToolStripSeparator();
		settingsButton = new ToolStripButton();
		helpButton = new ToolStripButton();
		mainToolbar = new TabControl();
		modelTab = new TabPage();
		modelPropPanel = new Panel();
		modelPropStrip = new ToolStrip();
		parametersButton = new ToolStripButton();
		materialButton = new ToolStripButton();
		modelPropLabel = new Label();
		modelSep6 = new Panel();
		modelDisplayPanel = new Panel();
		modelDisplayStrip = new ToolStrip();
		viewSwitchButton = new ToolStripButton();
		displayStyleButton = new ToolStripDropDownButton();
		styleShadedMenuItem = new ToolStripMenuItem();
		styleShadedEdgesMenuItem = new ToolStripMenuItem();
		styleTransparentMenuItem = new ToolStripMenuItem();
		styleMeshMenuItem = new ToolStripMenuItem();
		styleVolumeMeshMenuItem = new ToolStripMenuItem();
		modelDisplayLabel = new Label();
		modelSep5 = new Panel();
		modelEditPanel = new Panel();
		modelEditStrip = new ToolStrip();
		deleteButton = new ToolStripButton();
		modelEditLabel = new Label();
		modelSep4 = new Panel();
		modelEngPanel = new Panel();
		modelEngStrip = new ToolStrip();
		roundButton = new ToolStripButton();
		chamferButton = new ToolStripButton();
		modelEngLabel = new Label();
		modelSep3 = new Panel();
		modelShapePanel = new Panel();
		modelShapeStrip = new ToolStrip();
		extrudeButton = new ToolStripButton();
		revolveButton = new ToolStripButton();
		sweepButton = new ToolStripButton();
		modelShapeLabel = new Label();
		modelSep2 = new Panel();
		modelDatumPanel = new Panel();
		modelDatumStrip = new ToolStrip();
		newSketchButton = new ToolStripButton();
		newPlaneButton = new ToolStripButton();
		newPointsButton = new ToolStripButton();
		newLineButton = new ToolStripButton();
		modelDatumLabel = new Label();
		modelSep1 = new Panel();
		modelSetupPanel = new Panel();
		modelSetupStrip = new ToolStrip();
		newPartButton = new ToolStripButton();
		rebuildButton = new ToolStripButton();
		modelSetupLabel = new Label();
		sketchTab = new TabPage();
		sketchClosePanel = new Panel();
		sketchCloseStrip = new ToolStrip();
		sketchOkButton = new ToolStripButton();
		sketchCancelButton = new ToolStripButton();
		sketchCloseLabel = new Label();
		sketchSep7 = new Panel();
		sketchUtilPanel = new Panel();
		sketchUtilStrip = new ToolStrip();
		sketchSnapGridButton = new ToolStripButton();
		sketchSnapItemsButton = new ToolStripButton();
		sketchParamsButton = new ToolStripButton();
		sketchUtilLabel = new Label();
		sketchSep6 = new Panel();
		sketchDimPanel = new Panel();
		sketchDimStrip = new ToolStrip();
		sketchDimButton = new ToolStripButton();
		sketchDimLabel = new Label();
		sketchSep5 = new Panel();
		sketchConstrainPanel = new Panel();
		sketchConstrainStrip = new ToolStrip();
		constVerticalButton = new ToolStripButton();
		constTangentButton = new ToolStripButton();
		constSymmetricButton = new ToolStripButton();
		constHorizontalButton = new ToolStripButton();
		constMidButton = new ToolStripButton();
		constEqualButton = new ToolStripButton();
		constPerpendicularButton = new ToolStripButton();
		constCoincidentButton = new ToolStripButton();
		constParallelButton = new ToolStripButton();
		sketchConstrainLabel = new Label();
		sketchSep4 = new Panel();
		sketchEditPanel = new Panel();
		sketchEditStrip = new ToolStrip();
		sketchFilletButton = new ToolStripButton();
		sketchChamferButton = new ToolStripButton();
		sketchDelSegmentButton = new ToolStripButton();
		sketchEditLabel = new Label();
		sketchSep3 = new Panel();
		sketchingPanel = new Panel();
		sketchingStrip = new ToolStrip();
		sketchLineButton = new ToolStripButton();
		sketchRectangleButton = new ToolStripButton();
		sketchCircleButton = new ToolStripButton();
		sketchArcCppButton = new ToolStripButton();
		sketchArcPppButton = new ToolStripButton();
		sketchEllipseButton = new ToolStripButton();
		sketchCircle3pButton = new ToolStripButton();
		sketchCurveButton = new ToolStripButton();
		sketchPolygonButton = new ToolStripButton();
		sketchingLabel = new Label();
		sketchSep2 = new Panel();
		sketchDatumPanel = new Panel();
		sketchDatumStrip = new ToolStrip();
		sketchCenterlineButton = new ToolStripButton();
		sketchPointButton = new ToolStripButton();
		sketchConstructionButton = new ToolStripButton();
		sketchDatumLabel = new Label();
		sketchSep1 = new Panel();
		sketchSetupPanel = new Panel();
		sketchSetupStrip = new ToolStrip();
		sketchSetupButton = new ToolStripButton();
		sketchReferencesButton = new ToolStripButton();
		sketchViewButton = new ToolStripButton();
		sketchSetupLabel = new Label();
		viewTools = new ToolStrip();
		perspViewButton = new ToolStripButton();
		viewOrientButton = new ToolStripDropDownButton();
		viewFrontMenuItem = new ToolStripMenuItem();
		viewBackMenuItem = new ToolStripMenuItem();
		viewLeftMenuItem = new ToolStripMenuItem();
		viewRightMenuItem = new ToolStripMenuItem();
		viewTopMenuItem = new ToolStripMenuItem();
		viewOrientSep = new ToolStripSeparator();
		viewStandardMenuItem = new ToolStripMenuItem();
		viewNormalMenuItem = new ToolStripMenuItem();
		mainSplit = new SplitContainer();
		projectTree = new ProjectTree();
		glView = new GL3DView();
		statusStrip = new StatusStrip();
		statusModeLabel = new ToolStripStatusLabel();
		statusSelectionLabel = new ToolStripStatusLabel();
		statusInfoLabel = new ToolStripStatusLabel();
		selectGeometryButton = new ToolStripDropDownButton();
		planeSelectMenuItem = new ToolStripMenuItem();
		quiltSelectMenuItem = new ToolStripMenuItem();
		curveSelectMenuItem = new ToolStripMenuItem();
		edgeSelectMenuItem = new ToolStripMenuItem();
		centerlineSelectMenuItem = new ToolStripMenuItem();
		axisSelectMenuItem = new ToolStripMenuItem();
		sketchSelectMenuItem = new ToolStripMenuItem();
		partSelectMenuItem = new ToolStripMenuItem();
		featureSelectMenuItem = new ToolStripMenuItem();
		vertexSelectMenuItem = new ToolStripMenuItem();
		arrowSelectMenuItem = new ToolStripMenuItem();
		dimensionSelectMenuItem = new ToolStripMenuItem();
		constraintSelectMenuItem = new ToolStripMenuItem();
		mainMenu.SuspendLayout();
		mainToolStrip.SuspendLayout();
		mainToolbar.SuspendLayout();
		modelTab.SuspendLayout();
		modelPropPanel.SuspendLayout();
		modelPropStrip.SuspendLayout();
		modelDisplayPanel.SuspendLayout();
		modelDisplayStrip.SuspendLayout();
		modelEditPanel.SuspendLayout();
		modelEditStrip.SuspendLayout();
		modelEngPanel.SuspendLayout();
		modelEngStrip.SuspendLayout();
		modelShapePanel.SuspendLayout();
		modelShapeStrip.SuspendLayout();
		modelDatumPanel.SuspendLayout();
		modelDatumStrip.SuspendLayout();
		modelSetupPanel.SuspendLayout();
		modelSetupStrip.SuspendLayout();
		sketchTab.SuspendLayout();
		sketchClosePanel.SuspendLayout();
		sketchCloseStrip.SuspendLayout();
		sketchUtilPanel.SuspendLayout();
		sketchUtilStrip.SuspendLayout();
		sketchDimPanel.SuspendLayout();
		sketchDimStrip.SuspendLayout();
		sketchConstrainPanel.SuspendLayout();
		sketchConstrainStrip.SuspendLayout();
		sketchEditPanel.SuspendLayout();
		sketchEditStrip.SuspendLayout();
		sketchingPanel.SuspendLayout();
		sketchingStrip.SuspendLayout();
		sketchDatumPanel.SuspendLayout();
		sketchDatumStrip.SuspendLayout();
		sketchSetupPanel.SuspendLayout();
		sketchSetupStrip.SuspendLayout();
		viewTools.SuspendLayout();
		((System.ComponentModel.ISupportInitialize)mainSplit).BeginInit();
		mainSplit.Panel1.SuspendLayout();
		mainSplit.Panel2.SuspendLayout();
		mainSplit.SuspendLayout();
		statusStrip.SuspendLayout();
		SuspendLayout();
		// 
		// mainMenu
		// 
		mainMenu.GripMargin = new Padding(2);
		mainMenu.ImageScalingSize = new Size(20, 20);
		mainMenu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, viewMenu });
		mainMenu.Location = new Point(0, 0);
		mainMenu.Name = "mainMenu";
		mainMenu.Padding = new Padding(9, 3, 0, 3);
		mainMenu.Size = new Size(1790, 30);
		mainMenu.TabIndex = 0;
		// 
		// fileMenu
		// 
		fileMenu.DropDownItems.AddRange(new ToolStripItem[] { newMenuItem, openMenuItem, saveMenuItem, exportMenuItem, fileMenuSep1, propertiesMenuItem, fileMenuSep2, exitMenuItem });
		fileMenu.Name = "fileMenu";
		fileMenu.Size = new Size(39, 21);
		fileMenu.Text = "File";
		// 
		// newMenuItem
		// 
		newMenuItem.Name = "newMenuItem";
		newMenuItem.Size = new Size(136, 22);
		newMenuItem.Text = "New";
		newMenuItem.Click += newFileButton_Click;
		// 
		// openMenuItem
		// 
		openMenuItem.Name = "openMenuItem";
		openMenuItem.Size = new Size(136, 22);
		openMenuItem.Text = "Open";
		openMenuItem.Click += openMenuItem_Click;
		// 
		// saveMenuItem
		// 
		saveMenuItem.Name = "saveMenuItem";
		saveMenuItem.Size = new Size(136, 22);
		saveMenuItem.Text = "Save";
		saveMenuItem.Click += saveButton_Click;
		// 
		// exportMenuItem
		// 
		exportMenuItem.Name = "exportMenuItem";
		exportMenuItem.Size = new Size(136, 22);
		exportMenuItem.Text = "Export";
		exportMenuItem.Click += exportMenuItem_Click;
		// 
		// fileMenuSep1
		// 
		fileMenuSep1.Name = "fileMenuSep1";
		fileMenuSep1.Size = new Size(133, 6);
		// 
		// propertiesMenuItem
		// 
		propertiesMenuItem.Name = "propertiesMenuItem";
		propertiesMenuItem.Size = new Size(136, 22);
		propertiesMenuItem.Text = "Properties";
		propertiesMenuItem.Click += propertiesMenuItem_Click;
		// 
		// fileMenuSep2
		// 
		fileMenuSep2.Name = "fileMenuSep2";
		fileMenuSep2.Size = new Size(133, 6);
		// 
		// exitMenuItem
		// 
		exitMenuItem.Name = "exitMenuItem";
		exitMenuItem.Size = new Size(136, 22);
		exitMenuItem.Text = "Exit";
		exitMenuItem.Click += exitMenuItem_Click;
		// 
		// editMenu
		// 
		editMenu.DropDownItems.AddRange(new ToolStripItem[] { deleteMenuItem, clearSelectionMenuItem, cancelInsertMenuItem });
		editMenu.Name = "editMenu";
		editMenu.Size = new Size(42, 21);
		editMenu.Text = "Edit";
		// 
		// deleteMenuItem
		// 
		deleteMenuItem.Name = "deleteMenuItem";
		deleteMenuItem.ShortcutKeys = Keys.Delete;
		deleteMenuItem.Size = new Size(188, 22);
		deleteMenuItem.Text = "Delete";
		deleteMenuItem.Click += deleteButton_Click;
		// 
		// clearSelectionMenuItem
		// 
		clearSelectionMenuItem.Name = "clearSelectionMenuItem";
		clearSelectionMenuItem.Size = new Size(188, 22);
		clearSelectionMenuItem.Text = "Clear selection";
		clearSelectionMenuItem.Click += clearSelectionMenuItem_Click;
		// 
		// cancelInsertMenuItem
		// 
		cancelInsertMenuItem.Enabled = false;
		cancelInsertMenuItem.Name = "cancelInsertMenuItem";
		cancelInsertMenuItem.Size = new Size(188, 22);
		cancelInsertMenuItem.Text = "Cancel insert mode";
		cancelInsertMenuItem.ToolTipText = "Move the build bar back to the end of the feature list";
		cancelInsertMenuItem.Click += cancelInsertMenuItem_Click;
		// 
		// viewMenu
		// 
		viewMenu.DropDownItems.AddRange(new ToolStripItem[] { gridMenuItem, datumPlanesMenuItem, showDimensionsMenuItem, auditBooleansMenuItem, speedReportMenuItem, testCubeMenuItem, viewMenuSep1, fitAllMenuItem });
		viewMenu.Name = "viewMenu";
		viewMenu.Size = new Size(47, 21);
		viewMenu.Text = "View";
		// 
		// gridMenuItem
		// 
		gridMenuItem.Checked = true;
		gridMenuItem.CheckOnClick = true;
		gridMenuItem.CheckState = CheckState.Checked;
		gridMenuItem.Name = "gridMenuItem";
		gridMenuItem.Size = new Size(258, 22);
		gridMenuItem.Text = "Grid (sketch mode)";
		gridMenuItem.CheckedChanged += gridMenuItem_CheckedChanged;
		// 
		// datumPlanesMenuItem
		// 
		datumPlanesMenuItem.Checked = true;
		datumPlanesMenuItem.CheckOnClick = true;
		datumPlanesMenuItem.CheckState = CheckState.Checked;
		datumPlanesMenuItem.Name = "datumPlanesMenuItem";
		datumPlanesMenuItem.Size = new Size(258, 22);
		datumPlanesMenuItem.Text = "Datum planes";
		datumPlanesMenuItem.CheckedChanged += datumPlanesMenuItem_CheckedChanged;
		// 
		// showDimensionsMenuItem
		// 
		showDimensionsMenuItem.CheckOnClick = true;
		showDimensionsMenuItem.Name = "showDimensionsMenuItem";
		showDimensionsMenuItem.Size = new Size(258, 22);
		showDimensionsMenuItem.Text = "Show selected item dimensions";
		showDimensionsMenuItem.CheckedChanged += showDimensionsMenuItem_CheckedChanged;
		// 
		// auditBooleansMenuItem
		// 
		auditBooleansMenuItem.CheckOnClick = true;
		auditBooleansMenuItem.Name = "auditBooleansMenuItem";
		auditBooleansMenuItem.Size = new Size(258, 22);
		auditBooleansMenuItem.Text = "Audit booleans against the volumetric model";
		auditBooleansMenuItem.ToolTipText = "Checks every union and cut cell by cell. Slow; rebuilds the part.";
		auditBooleansMenuItem.CheckedChanged += auditBooleansMenuItem_CheckedChanged;
		// 
		// speedReportMenuItem
		// 
		speedReportMenuItem.Name = "speedReportMenuItem";
		speedReportMenuItem.Size = new Size(258, 22);
		speedReportMenuItem.Text = "Speed report";
		speedReportMenuItem.ToolTipText = "Rebuild timing: one thread versus all cores, and a breakdown by stage.";
		speedReportMenuItem.Click += speedReportMenuItem_Click;
		// 
		// testCubeMenuItem
		// 
		testCubeMenuItem.Name = "testCubeMenuItem";
		testCubeMenuItem.Size = new Size(258, 22);
		testCubeMenuItem.Text = "Insert test cube 100";
		testCubeMenuItem.ToolTipText = "Sketch plus extrude, symmetric about XY: faces at plus and minus 50";
		testCubeMenuItem.Click += testCubeMenuItem_Click;
		// 
		// viewMenuSep1
		// 
		viewMenuSep1.Name = "viewMenuSep1";
		viewMenuSep1.Size = new Size(255, 6);
		// 
		// fitAllMenuItem
		// 
		fitAllMenuItem.Name = "fitAllMenuItem";
		fitAllMenuItem.ShortcutKeys = Keys.Control | Keys.F;
		fitAllMenuItem.Size = new Size(258, 22);
		fitAllMenuItem.Text = "Fit all";
		fitAllMenuItem.Click += fitAllMenuItem_Click;
		// 
		// mainToolStrip
		// 
		mainToolStrip.ImageScalingSize = new Size(20, 20);
		mainToolStrip.Items.AddRange(new ToolStripItem[] { newFileButton, saveButton, toolStripSeparator1, undoButton, redoButton, toolStripSeparator2, settingsButton, helpButton });
		mainToolStrip.Location = new Point(0, 30);
		mainToolStrip.Name = "mainToolStrip";
		mainToolStrip.RenderMode = ToolStripRenderMode.System;
		mainToolStrip.Size = new Size(1790, 31);
		mainToolStrip.TabIndex = 1;
		// 
		// newFileButton
		// 
		newFileButton.Name = "newFileButton";
		newFileButton.Size = new Size(38, 22);
		newFileButton.Tag = "new.png";
		newFileButton.Text = "New";
		newFileButton.ToolTipText = "New file";
		newFileButton.Click += newFileButton_Click;
		// 
		// saveButton
		// 
		saveButton.Name = "saveButton";
		saveButton.Size = new Size(39, 22);
		saveButton.Tag = "save.png";
		saveButton.Text = "Save";
		saveButton.ToolTipText = "Save file";
		saveButton.Click += saveButton_Click;
		// 
		// toolStripSeparator1
		// 
		toolStripSeparator1.Name = "toolStripSeparator1";
		toolStripSeparator1.Size = new Size(6, 25);
		// 
		// undoButton
		// 
		undoButton.Name = "undoButton";
		undoButton.Size = new Size(44, 22);
		undoButton.Tag = "undo.png";
		undoButton.Text = "Undo";
		undoButton.ToolTipText = "Undo action";
		undoButton.Click += undoButton_Click;
		// 
		// redoButton
		// 
		redoButton.Name = "redoButton";
		redoButton.Size = new Size(43, 22);
		redoButton.Tag = "redo.png";
		redoButton.Text = "Redo";
		redoButton.ToolTipText = "Redo action";
		redoButton.Click += redoButton_Click;
		// 
		// toolStripSeparator2
		// 
		toolStripSeparator2.Name = "toolStripSeparator2";
		toolStripSeparator2.Size = new Size(6, 25);
		// 
		// settingsButton
		// 
		settingsButton.Name = "settingsButton";
		settingsButton.Size = new Size(58, 22);
		settingsButton.Tag = "Settings.png";
		settingsButton.Text = "Settings";
		settingsButton.ToolTipText = "Application settings";
		settingsButton.Click += settingsButton_Click;
		// 
		// helpButton
		// 
		helpButton.Name = "helpButton";
		helpButton.Size = new Size(39, 22);
		helpButton.Tag = "help.png";
		helpButton.Text = "Help";
		helpButton.ToolTipText = "Show application help";
		helpButton.Click += helpButton_Click;
		// 
		// mainToolbar
		// 
		mainToolbar.Controls.Add(modelTab);
		mainToolbar.Controls.Add(sketchTab);
		mainToolbar.Dock = DockStyle.Top;
		mainToolbar.Location = new Point(0, 61);
		mainToolbar.Margin = new Padding(3, 4, 3, 4);
		mainToolbar.Name = "mainToolbar";
		mainToolbar.SelectedIndex = 0;
		mainToolbar.Size = new Size(1790, 220);
		mainToolbar.TabIndex = 2;
		mainToolbar.SelectedIndexChanged += mainToolbar_SelectedIndexChanged;
		// 
		// modelTab
		// 
		modelTab.BackColor = SystemColors.Control;
		modelTab.Controls.Add(modelPropPanel);
		modelTab.Controls.Add(modelSep6);
		modelTab.Controls.Add(modelDisplayPanel);
		modelTab.Controls.Add(modelSep5);
		modelTab.Controls.Add(modelEditPanel);
		modelTab.Controls.Add(modelSep4);
		modelTab.Controls.Add(modelEngPanel);
		modelTab.Controls.Add(modelSep3);
		modelTab.Controls.Add(modelShapePanel);
		modelTab.Controls.Add(modelSep2);
		modelTab.Controls.Add(modelDatumPanel);
		modelTab.Controls.Add(modelSep1);
		modelTab.Controls.Add(modelSetupPanel);
		modelTab.Location = new Point(4, 29);
		modelTab.Margin = new Padding(3, 4, 3, 4);
		modelTab.Name = "modelTab";
		modelTab.Padding = new Padding(2, 3, 2, 3);
		modelTab.Size = new Size(1782, 187);
		modelTab.TabIndex = 0;
		modelTab.Text = "Model";
		// 
		// modelPropPanel
		// 
		modelPropPanel.Controls.Add(modelPropStrip);
		modelPropPanel.Controls.Add(modelPropLabel);
		modelPropPanel.Dock = DockStyle.Left;
		modelPropPanel.Location = new Point(941, 3);
		modelPropPanel.Margin = new Padding(3, 4, 3, 4);
		modelPropPanel.Name = "modelPropPanel";
		modelPropPanel.Padding = new Padding(2, 3, 2, 0);
		modelPropPanel.Size = new Size(139, 181);
		modelPropPanel.TabIndex = 0;
		// 
		// modelPropStrip
		// 
		modelPropStrip.AutoSize = false;
		modelPropStrip.Dock = DockStyle.Fill;
		modelPropStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelPropStrip.ImageScalingSize = new Size(20, 20);
		modelPropStrip.Items.AddRange(new ToolStripItem[] { parametersButton, materialButton });
		modelPropStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelPropStrip.Location = new Point(2, 3);
		modelPropStrip.Name = "modelPropStrip";
		modelPropStrip.Padding = new Padding(0);
		modelPropStrip.RenderMode = ToolStripRenderMode.System;
		modelPropStrip.Size = new Size(135, 154);
		modelPropStrip.TabIndex = 0;
		// 
		// parametersButton
		// 
		parametersButton.AutoSize = false;
		parametersButton.ImageAlign = ContentAlignment.MiddleLeft;
		parametersButton.Margin = new Padding(1);
		parametersButton.Name = "parametersButton";
		parametersButton.Size = new Size(116, 34);
		parametersButton.Tag = "Toolbar/parameters.png";
		parametersButton.Text = "Parameters";
		parametersButton.TextAlign = ContentAlignment.MiddleLeft;
		parametersButton.ToolTipText = "Parameter table";
		parametersButton.Click += parametersButton_Click;
		// 
		// materialButton
		// 
		materialButton.AutoSize = false;
		materialButton.ImageAlign = ContentAlignment.MiddleLeft;
		materialButton.Margin = new Padding(1);
		materialButton.Name = "materialButton";
		materialButton.Size = new Size(116, 34);
		materialButton.Tag = "Toolbar/material.png";
		materialButton.Text = "Material";
		materialButton.TextAlign = ContentAlignment.MiddleLeft;
		materialButton.ToolTipText = "Part material";
		materialButton.Click += materialButton_Click;
		// 
		// modelPropLabel
		// 
		modelPropLabel.Dock = DockStyle.Bottom;
		modelPropLabel.ForeColor = SystemColors.GrayText;
		modelPropLabel.Location = new Point(2, 157);
		modelPropLabel.Name = "modelPropLabel";
		modelPropLabel.Size = new Size(135, 24);
		modelPropLabel.TabIndex = 1;
		modelPropLabel.Text = "Properties";
		modelPropLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// modelSep6
		// 
		modelSep6.BackColor = SystemColors.ControlDark;
		modelSep6.Dock = DockStyle.Left;
		modelSep6.Location = new Point(940, 3);
		modelSep6.Margin = new Padding(0);
		modelSep6.Name = "modelSep6";
		modelSep6.Size = new Size(1, 181);
		modelSep6.TabIndex = 0;
		// 
		// modelDisplayPanel
		// 
		modelDisplayPanel.Controls.Add(modelDisplayStrip);
		modelDisplayPanel.Controls.Add(modelDisplayLabel);
		modelDisplayPanel.Dock = DockStyle.Left;
		modelDisplayPanel.Location = new Point(801, 3);
		modelDisplayPanel.Margin = new Padding(3, 4, 3, 4);
		modelDisplayPanel.Name = "modelDisplayPanel";
		modelDisplayPanel.Padding = new Padding(2, 3, 2, 0);
		modelDisplayPanel.Size = new Size(139, 181);
		modelDisplayPanel.TabIndex = 0;
		// 
		// modelDisplayStrip
		// 
		modelDisplayStrip.AutoSize = false;
		modelDisplayStrip.Dock = DockStyle.Fill;
		modelDisplayStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelDisplayStrip.ImageScalingSize = new Size(20, 20);
		modelDisplayStrip.Items.AddRange(new ToolStripItem[] { viewSwitchButton, displayStyleButton });
		modelDisplayStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelDisplayStrip.Location = new Point(2, 3);
		modelDisplayStrip.Name = "modelDisplayStrip";
		modelDisplayStrip.Padding = new Padding(0);
		modelDisplayStrip.RenderMode = ToolStripRenderMode.System;
		modelDisplayStrip.Size = new Size(135, 154);
		modelDisplayStrip.TabIndex = 0;
		// 
		// viewSwitchButton
		// 
		viewSwitchButton.AutoSize = false;
		viewSwitchButton.ImageAlign = ContentAlignment.MiddleLeft;
		viewSwitchButton.Margin = new Padding(1);
		viewSwitchButton.Name = "viewSwitchButton";
		viewSwitchButton.Size = new Size(116, 34);
		viewSwitchButton.Tag = "Toolbar/view_switch.png";
		viewSwitchButton.Text = "Projection";
		viewSwitchButton.TextAlign = ContentAlignment.MiddleLeft;
		viewSwitchButton.ToolTipText = "Toggle perspective / orthographic";
		viewSwitchButton.Click += viewSwitchButton_Click;
		// 
		// displayStyleButton
		// 
		displayStyleButton.AutoSize = false;
		displayStyleButton.DropDownItems.AddRange(new ToolStripItem[] { styleShadedMenuItem, styleShadedEdgesMenuItem, styleTransparentMenuItem, styleMeshMenuItem, styleVolumeMeshMenuItem });
		displayStyleButton.ImageAlign = ContentAlignment.MiddleLeft;
		displayStyleButton.Margin = new Padding(1);
		displayStyleButton.Name = "displayStyleButton";
		displayStyleButton.Size = new Size(116, 34);
		displayStyleButton.Tag = "Toolbar/display_style.png";
		displayStyleButton.Text = "Style";
		displayStyleButton.TextAlign = ContentAlignment.MiddleLeft;
		displayStyleButton.ToolTipText = "Display style";
		// 
		// styleShadedMenuItem
		// 
		styleShadedMenuItem.Name = "styleShadedMenuItem";
		styleShadedMenuItem.Size = new Size(261, 22);
		styleShadedMenuItem.Text = "Shaded";
		styleShadedMenuItem.Click += styleShadedMenuItem_Click;
		// 
		// styleShadedEdgesMenuItem
		// 
		styleShadedEdgesMenuItem.Checked = true;
		styleShadedEdgesMenuItem.CheckState = CheckState.Checked;
		styleShadedEdgesMenuItem.Name = "styleShadedEdgesMenuItem";
		styleShadedEdgesMenuItem.Size = new Size(261, 22);
		styleShadedEdgesMenuItem.Text = "Shaded with edges";
		styleShadedEdgesMenuItem.Click += styleShadedEdgesMenuItem_Click;
		// 
		// styleTransparentMenuItem
		// 
		styleTransparentMenuItem.Name = "styleTransparentMenuItem";
		styleTransparentMenuItem.Size = new Size(261, 22);
		styleTransparentMenuItem.Text = "Transparent (dashed back lines)";
		styleTransparentMenuItem.Click += styleTransparentMenuItem_Click;
		// 
		// styleMeshMenuItem
		// 
		styleMeshMenuItem.Name = "styleMeshMenuItem";
		styleMeshMenuItem.Size = new Size(261, 22);
		styleMeshMenuItem.Text = "Mesh";
		styleMeshMenuItem.Click += styleMeshMenuItem_Click;
		//
		// styleVolumeMeshMenuItem
		//
		styleVolumeMeshMenuItem.Name = "styleVolumeMeshMenuItem";
		styleVolumeMeshMenuItem.Size = new Size(261, 22);
		styleVolumeMeshMenuItem.Text = "Volume Mesh";
		styleVolumeMeshMenuItem.Click += styleVolumeMeshMenuItem_Click;
		// 
		// modelDisplayLabel
		// 
		modelDisplayLabel.Dock = DockStyle.Bottom;
		modelDisplayLabel.ForeColor = SystemColors.GrayText;
		modelDisplayLabel.Location = new Point(2, 157);
		modelDisplayLabel.Name = "modelDisplayLabel";
		modelDisplayLabel.Size = new Size(135, 24);
		modelDisplayLabel.TabIndex = 1;
		modelDisplayLabel.Text = "Model Display";
		modelDisplayLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// modelSep5
		// 
		modelSep5.BackColor = SystemColors.ControlDark;
		modelSep5.Dock = DockStyle.Left;
		modelSep5.Location = new Point(800, 3);
		modelSep5.Margin = new Padding(0);
		modelSep5.Name = "modelSep5";
		modelSep5.Size = new Size(1, 181);
		modelSep5.TabIndex = 0;
		// 
		// modelEditPanel
		// 
		modelEditPanel.Controls.Add(modelEditStrip);
		modelEditPanel.Controls.Add(modelEditLabel);
		modelEditPanel.Dock = DockStyle.Left;
		modelEditPanel.Location = new Point(661, 3);
		modelEditPanel.Margin = new Padding(3, 4, 3, 4);
		modelEditPanel.Name = "modelEditPanel";
		modelEditPanel.Padding = new Padding(2, 3, 2, 0);
		modelEditPanel.Size = new Size(139, 181);
		modelEditPanel.TabIndex = 0;
		// 
		// modelEditStrip
		// 
		modelEditStrip.AutoSize = false;
		modelEditStrip.Dock = DockStyle.Fill;
		modelEditStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelEditStrip.ImageScalingSize = new Size(20, 20);
		modelEditStrip.Items.AddRange(new ToolStripItem[] { deleteButton });
		modelEditStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelEditStrip.Location = new Point(2, 3);
		modelEditStrip.Name = "modelEditStrip";
		modelEditStrip.Padding = new Padding(0);
		modelEditStrip.RenderMode = ToolStripRenderMode.System;
		modelEditStrip.Size = new Size(135, 154);
		modelEditStrip.TabIndex = 0;
		// 
		// deleteButton
		// 
		deleteButton.AutoSize = false;
		deleteButton.ImageAlign = ContentAlignment.MiddleLeft;
		deleteButton.Margin = new Padding(1);
		deleteButton.Name = "deleteButton";
		deleteButton.Size = new Size(116, 34);
		deleteButton.Tag = "Toolbar/delete_segment.png";
		deleteButton.Text = "Delete";
		deleteButton.TextAlign = ContentAlignment.MiddleLeft;
		deleteButton.ToolTipText = "Delete the selected item (Del)";
		deleteButton.Click += deleteButton_Click;
		// 
		// modelEditLabel
		// 
		modelEditLabel.Dock = DockStyle.Bottom;
		modelEditLabel.ForeColor = SystemColors.GrayText;
		modelEditLabel.Location = new Point(2, 157);
		modelEditLabel.Name = "modelEditLabel";
		modelEditLabel.Size = new Size(135, 24);
		modelEditLabel.TabIndex = 1;
		modelEditLabel.Text = "Editing";
		modelEditLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// modelSep4
		// 
		modelSep4.BackColor = SystemColors.ControlDark;
		modelSep4.Dock = DockStyle.Left;
		modelSep4.Location = new Point(660, 3);
		modelSep4.Margin = new Padding(0);
		modelSep4.Name = "modelSep4";
		modelSep4.Size = new Size(1, 181);
		modelSep4.TabIndex = 0;
		// 
		// modelEngPanel
		// 
		modelEngPanel.Controls.Add(modelEngStrip);
		modelEngPanel.Controls.Add(modelEngLabel);
		modelEngPanel.Dock = DockStyle.Left;
		modelEngPanel.Location = new Point(521, 3);
		modelEngPanel.Margin = new Padding(3, 4, 3, 4);
		modelEngPanel.Name = "modelEngPanel";
		modelEngPanel.Padding = new Padding(2, 3, 2, 0);
		modelEngPanel.Size = new Size(139, 181);
		modelEngPanel.TabIndex = 0;
		// 
		// modelEngStrip
		// 
		modelEngStrip.AutoSize = false;
		modelEngStrip.Dock = DockStyle.Fill;
		modelEngStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelEngStrip.ImageScalingSize = new Size(20, 20);
		modelEngStrip.Items.AddRange(new ToolStripItem[] { roundButton, chamferButton });
		modelEngStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelEngStrip.Location = new Point(2, 3);
		modelEngStrip.Name = "modelEngStrip";
		modelEngStrip.Padding = new Padding(0);
		modelEngStrip.RenderMode = ToolStripRenderMode.System;
		modelEngStrip.Size = new Size(135, 154);
		modelEngStrip.TabIndex = 0;
		// 
		// roundButton
		// 
		roundButton.AutoSize = false;
		roundButton.ImageAlign = ContentAlignment.MiddleLeft;
		roundButton.Margin = new Padding(1);
		roundButton.Name = "roundButton";
		roundButton.Size = new Size(116, 34);
		roundButton.Tag = "Toolbar/fillet.png";
		roundButton.Text = "Round";
		roundButton.TextAlign = ContentAlignment.MiddleLeft;
		roundButton.ToolTipText = "Round selected edges";
		roundButton.Click += roundButton_Click;
		// 
		// chamferButton
		// 
		chamferButton.AutoSize = false;
		chamferButton.ImageAlign = ContentAlignment.MiddleLeft;
		chamferButton.Margin = new Padding(1);
		chamferButton.Name = "chamferButton";
		chamferButton.Size = new Size(116, 34);
		chamferButton.Tag = "Toolbar/chamfer.png";
		chamferButton.Text = "Chamfer";
		chamferButton.TextAlign = ContentAlignment.MiddleLeft;
		chamferButton.ToolTipText = "Chamfer selected edges";
		chamferButton.Click += chamferButton_Click;
		// 
		// modelEngLabel
		// 
		modelEngLabel.Dock = DockStyle.Bottom;
		modelEngLabel.ForeColor = SystemColors.GrayText;
		modelEngLabel.Location = new Point(2, 157);
		modelEngLabel.Name = "modelEngLabel";
		modelEngLabel.Size = new Size(135, 24);
		modelEngLabel.TabIndex = 1;
		modelEngLabel.Text = "Engineering";
		modelEngLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// modelSep3
		// 
		modelSep3.BackColor = SystemColors.ControlDark;
		modelSep3.Dock = DockStyle.Left;
		modelSep3.Location = new Point(520, 3);
		modelSep3.Margin = new Padding(0);
		modelSep3.Name = "modelSep3";
		modelSep3.Size = new Size(1, 181);
		modelSep3.TabIndex = 0;
		// 
		// modelShapePanel
		// 
		modelShapePanel.Controls.Add(modelShapeStrip);
		modelShapePanel.Controls.Add(modelShapeLabel);
		modelShapePanel.Dock = DockStyle.Left;
		modelShapePanel.Location = new Point(381, 3);
		modelShapePanel.Margin = new Padding(3, 4, 3, 4);
		modelShapePanel.Name = "modelShapePanel";
		modelShapePanel.Padding = new Padding(2, 3, 2, 0);
		modelShapePanel.Size = new Size(139, 181);
		modelShapePanel.TabIndex = 0;
		// 
		// modelShapeStrip
		// 
		modelShapeStrip.AutoSize = false;
		modelShapeStrip.Dock = DockStyle.Fill;
		modelShapeStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelShapeStrip.ImageScalingSize = new Size(20, 20);
		modelShapeStrip.Items.AddRange(new ToolStripItem[] { extrudeButton, revolveButton, sweepButton });
		modelShapeStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelShapeStrip.Location = new Point(2, 3);
		modelShapeStrip.Name = "modelShapeStrip";
		modelShapeStrip.Padding = new Padding(0);
		modelShapeStrip.RenderMode = ToolStripRenderMode.System;
		modelShapeStrip.Size = new Size(135, 154);
		modelShapeStrip.TabIndex = 0;
		// 
		// extrudeButton
		// 
		extrudeButton.AutoSize = false;
		extrudeButton.ImageAlign = ContentAlignment.MiddleLeft;
		extrudeButton.Margin = new Padding(1);
		extrudeButton.Name = "extrudeButton";
		extrudeButton.Size = new Size(116, 34);
		extrudeButton.Tag = "Toolbar/feature.png";
		extrudeButton.Text = "Extrude";
		extrudeButton.TextAlign = ContentAlignment.MiddleLeft;
		extrudeButton.ToolTipText = "Extrude a sketch";
		extrudeButton.Click += extrudeButton_Click;
		// 
		// revolveButton
		// 
		revolveButton.AutoSize = false;
		revolveButton.ImageAlign = ContentAlignment.MiddleLeft;
		revolveButton.Margin = new Padding(1);
		revolveButton.Name = "revolveButton";
		revolveButton.Size = new Size(116, 34);
		revolveButton.Tag = "Toolbar/feature.png";
		revolveButton.Text = "Revolve";
		revolveButton.TextAlign = ContentAlignment.MiddleLeft;
		revolveButton.ToolTipText = "Revolve a sketch about an axis";
		revolveButton.Click += revolveButton_Click;
		// 
		// sweepButton
		// 
		sweepButton.AutoSize = false;
		sweepButton.ImageAlign = ContentAlignment.MiddleLeft;
		sweepButton.Margin = new Padding(1);
		sweepButton.Name = "sweepButton";
		sweepButton.Size = new Size(116, 34);
		sweepButton.Tag = "Toolbar/feature.png";
		sweepButton.Text = "Sweep";
		sweepButton.TextAlign = ContentAlignment.MiddleLeft;
		sweepButton.ToolTipText = "Sweep a section along a path";
		sweepButton.Click += sweepButton_Click;
		// 
		// modelShapeLabel
		// 
		modelShapeLabel.Dock = DockStyle.Bottom;
		modelShapeLabel.ForeColor = SystemColors.GrayText;
		modelShapeLabel.Location = new Point(2, 157);
		modelShapeLabel.Name = "modelShapeLabel";
		modelShapeLabel.Size = new Size(135, 24);
		modelShapeLabel.TabIndex = 1;
		modelShapeLabel.Text = "Shapes";
		modelShapeLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// modelSep2
		// 
		modelSep2.BackColor = SystemColors.ControlDark;
		modelSep2.Dock = DockStyle.Left;
		modelSep2.Location = new Point(380, 3);
		modelSep2.Margin = new Padding(0);
		modelSep2.Name = "modelSep2";
		modelSep2.Size = new Size(1, 181);
		modelSep2.TabIndex = 0;
		// 
		// modelDatumPanel
		// 
		modelDatumPanel.Controls.Add(modelDatumStrip);
		modelDatumPanel.Controls.Add(modelDatumLabel);
		modelDatumPanel.Dock = DockStyle.Left;
		modelDatumPanel.Location = new Point(106, 3);
		modelDatumPanel.Margin = new Padding(3, 4, 3, 4);
		modelDatumPanel.Name = "modelDatumPanel";
		modelDatumPanel.Padding = new Padding(2, 3, 2, 0);
		modelDatumPanel.Size = new Size(274, 181);
		modelDatumPanel.TabIndex = 0;
		// 
		// modelDatumStrip
		// 
		modelDatumStrip.AutoSize = false;
		modelDatumStrip.Dock = DockStyle.Fill;
		modelDatumStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelDatumStrip.ImageScalingSize = new Size(20, 20);
		modelDatumStrip.Items.AddRange(new ToolStripItem[] { newSketchButton, newPlaneButton, newPointsButton, newLineButton });
		modelDatumStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelDatumStrip.Location = new Point(2, 3);
		modelDatumStrip.Name = "modelDatumStrip";
		modelDatumStrip.Padding = new Padding(0);
		modelDatumStrip.RenderMode = ToolStripRenderMode.System;
		modelDatumStrip.Size = new Size(270, 154);
		modelDatumStrip.TabIndex = 0;
		// 
		// newSketchButton
		// 
		newSketchButton.AutoSize = false;
		newSketchButton.ImageAlign = ContentAlignment.MiddleLeft;
		newSketchButton.Margin = new Padding(1);
		newSketchButton.Name = "newSketchButton";
		newSketchButton.Size = new Size(116, 34);
		newSketchButton.Tag = "Toolbar/new_sketch.png";
		newSketchButton.Text = "Sketch";
		newSketchButton.TextAlign = ContentAlignment.MiddleLeft;
		newSketchButton.ToolTipText = "New sketch on the selected plane";
		newSketchButton.Click += newSketchButton_Click;
		// 
		// newPlaneButton
		// 
		newPlaneButton.AutoSize = false;
		newPlaneButton.ImageAlign = ContentAlignment.MiddleLeft;
		newPlaneButton.Margin = new Padding(1);
		newPlaneButton.Name = "newPlaneButton";
		newPlaneButton.Size = new Size(116, 34);
		newPlaneButton.Tag = "Toolbar/plane.png";
		newPlaneButton.Text = "Plane";
		newPlaneButton.TextAlign = ContentAlignment.MiddleLeft;
		newPlaneButton.ToolTipText = "New datum plane";
		newPlaneButton.Click += newPlaneButton_Click;
		// 
		// newPointsButton
		// 
		newPointsButton.AutoSize = false;
		newPointsButton.ImageAlign = ContentAlignment.MiddleLeft;
		newPointsButton.Margin = new Padding(1);
		newPointsButton.Name = "newPointsButton";
		newPointsButton.Size = new Size(116, 34);
		newPointsButton.Tag = "Toolbar/points.png";
		newPointsButton.Text = "Point";
		newPointsButton.TextAlign = ContentAlignment.MiddleLeft;
		newPointsButton.ToolTipText = "New datum point";
		newPointsButton.Click += newPointsButton_Click;
		// 
		// newLineButton
		// 
		newLineButton.AutoSize = false;
		newLineButton.ImageAlign = ContentAlignment.MiddleLeft;
		newLineButton.Margin = new Padding(1);
		newLineButton.Name = "newLineButton";
		newLineButton.Size = new Size(116, 34);
		newLineButton.Tag = "Toolbar/line3d.png";
		newLineButton.Text = "Line";
		newLineButton.TextAlign = ContentAlignment.MiddleLeft;
		newLineButton.ToolTipText = "New 3D line";
		newLineButton.Click += newLineButton_Click;
		// 
		// modelDatumLabel
		// 
		modelDatumLabel.Dock = DockStyle.Bottom;
		modelDatumLabel.ForeColor = SystemColors.GrayText;
		modelDatumLabel.Location = new Point(2, 157);
		modelDatumLabel.Name = "modelDatumLabel";
		modelDatumLabel.Size = new Size(270, 24);
		modelDatumLabel.TabIndex = 1;
		modelDatumLabel.Text = "Datum";
		modelDatumLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// modelSep1
		// 
		modelSep1.BackColor = SystemColors.ControlDark;
		modelSep1.Dock = DockStyle.Left;
		modelSep1.Location = new Point(105, 3);
		modelSep1.Margin = new Padding(0);
		modelSep1.Name = "modelSep1";
		modelSep1.Size = new Size(1, 181);
		modelSep1.TabIndex = 0;
		// 
		// modelSetupPanel
		// 
		modelSetupPanel.Controls.Add(modelSetupStrip);
		modelSetupPanel.Controls.Add(modelSetupLabel);
		modelSetupPanel.Dock = DockStyle.Left;
		modelSetupPanel.Location = new Point(2, 3);
		modelSetupPanel.Margin = new Padding(3, 4, 3, 4);
		modelSetupPanel.Name = "modelSetupPanel";
		modelSetupPanel.Padding = new Padding(2, 3, 2, 0);
		modelSetupPanel.Size = new Size(103, 181);
		modelSetupPanel.TabIndex = 0;
		// 
		// modelSetupStrip
		// 
		modelSetupStrip.AutoSize = false;
		modelSetupStrip.Dock = DockStyle.Fill;
		modelSetupStrip.GripStyle = ToolStripGripStyle.Hidden;
		modelSetupStrip.ImageScalingSize = new Size(20, 20);
		modelSetupStrip.Items.AddRange(new ToolStripItem[] { newPartButton, rebuildButton });
		modelSetupStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		modelSetupStrip.Location = new Point(2, 3);
		modelSetupStrip.Name = "modelSetupStrip";
		modelSetupStrip.Padding = new Padding(0);
		modelSetupStrip.RenderMode = ToolStripRenderMode.System;
		modelSetupStrip.Size = new Size(99, 154);
		modelSetupStrip.TabIndex = 0;
		// 
		// newPartButton
		// 
		newPartButton.AutoSize = false;
		newPartButton.ImageAlign = ContentAlignment.TopCenter;
		newPartButton.Margin = new Padding(1);
		newPartButton.Name = "newPartButton";
		newPartButton.Size = new Size(84, 104);
		newPartButton.Tag = "Toolbar/new_part.png";
		newPartButton.Text = "New part";
		newPartButton.TextAlign = ContentAlignment.BottomCenter;
		newPartButton.TextImageRelation = TextImageRelation.ImageAboveText;
		newPartButton.ToolTipText = "Add a new part to the assembly";
		newPartButton.Click += newPartButton_Click;
		// 
		// rebuildButton
		// 
		rebuildButton.AutoSize = false;
		rebuildButton.ImageScaling = ToolStripItemImageScaling.None;
		rebuildButton.Name = "rebuildButton";
		rebuildButton.Size = new Size(110, 32);
		rebuildButton.Text = "Rebuild";
		rebuildButton.TextAlign = ContentAlignment.MiddleLeft;
		rebuildButton.ToolTipText = "Rebuild all features up to the current stage";
		rebuildButton.Click += rebuildButton_Click;
		// 
		// modelSetupLabel
		// 
		modelSetupLabel.Dock = DockStyle.Bottom;
		modelSetupLabel.ForeColor = SystemColors.GrayText;
		modelSetupLabel.Location = new Point(2, 157);
		modelSetupLabel.Name = "modelSetupLabel";
		modelSetupLabel.Size = new Size(99, 24);
		modelSetupLabel.TabIndex = 1;
		modelSetupLabel.Text = "Get Data";
		modelSetupLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchTab
		// 
		sketchTab.BackColor = SystemColors.Control;
		sketchTab.Controls.Add(sketchClosePanel);
		sketchTab.Controls.Add(sketchSep7);
		sketchTab.Controls.Add(sketchUtilPanel);
		sketchTab.Controls.Add(sketchSep6);
		sketchTab.Controls.Add(sketchDimPanel);
		sketchTab.Controls.Add(sketchSep5);
		sketchTab.Controls.Add(sketchConstrainPanel);
		sketchTab.Controls.Add(sketchSep4);
		sketchTab.Controls.Add(sketchEditPanel);
		sketchTab.Controls.Add(sketchSep3);
		sketchTab.Controls.Add(sketchingPanel);
		sketchTab.Controls.Add(sketchSep2);
		sketchTab.Controls.Add(sketchDatumPanel);
		sketchTab.Controls.Add(sketchSep1);
		sketchTab.Controls.Add(sketchSetupPanel);
		sketchTab.Location = new Point(4, 29);
		sketchTab.Margin = new Padding(3, 4, 3, 4);
		sketchTab.Name = "sketchTab";
		sketchTab.Padding = new Padding(2, 3, 2, 3);
		sketchTab.Size = new Size(1779, 187);
		sketchTab.TabIndex = 1;
		sketchTab.Text = "Sketch";
		// 
		// sketchClosePanel
		// 
		sketchClosePanel.Controls.Add(sketchCloseStrip);
		sketchClosePanel.Controls.Add(sketchCloseLabel);
		sketchClosePanel.Dock = DockStyle.Left;
		sketchClosePanel.Location = new Point(1486, 3);
		sketchClosePanel.Margin = new Padding(3, 4, 3, 4);
		sketchClosePanel.Name = "sketchClosePanel";
		sketchClosePanel.Padding = new Padding(2, 3, 2, 0);
		sketchClosePanel.Size = new Size(201, 181);
		sketchClosePanel.TabIndex = 0;
		// 
		// sketchCloseStrip
		// 
		sketchCloseStrip.AutoSize = false;
		sketchCloseStrip.Dock = DockStyle.Fill;
		sketchCloseStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchCloseStrip.ImageScalingSize = new Size(20, 20);
		sketchCloseStrip.Items.AddRange(new ToolStripItem[] { sketchOkButton, sketchCancelButton });
		sketchCloseStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchCloseStrip.Location = new Point(2, 3);
		sketchCloseStrip.Name = "sketchCloseStrip";
		sketchCloseStrip.Padding = new Padding(0);
		sketchCloseStrip.RenderMode = ToolStripRenderMode.System;
		sketchCloseStrip.Size = new Size(197, 154);
		sketchCloseStrip.TabIndex = 0;
		// 
		// sketchOkButton
		// 
		sketchOkButton.AutoSize = false;
		sketchOkButton.ImageAlign = ContentAlignment.TopCenter;
		sketchOkButton.Margin = new Padding(1);
		sketchOkButton.Name = "sketchOkButton";
		sketchOkButton.Size = new Size(84, 104);
		sketchOkButton.Tag = "Toolbar/ok.png";
		sketchOkButton.Text = "OK";
		sketchOkButton.TextAlign = ContentAlignment.BottomCenter;
		sketchOkButton.TextImageRelation = TextImageRelation.ImageAboveText;
		sketchOkButton.ToolTipText = "Finish sketch";
		sketchOkButton.Click += sketchOkButton_Click;
		// 
		// sketchCancelButton
		// 
		sketchCancelButton.AutoSize = false;
		sketchCancelButton.ImageAlign = ContentAlignment.TopCenter;
		sketchCancelButton.Margin = new Padding(1);
		sketchCancelButton.Name = "sketchCancelButton";
		sketchCancelButton.Size = new Size(84, 104);
		sketchCancelButton.Tag = "Toolbar/cancel.png";
		sketchCancelButton.Text = "Cancel";
		sketchCancelButton.TextAlign = ContentAlignment.BottomCenter;
		sketchCancelButton.TextImageRelation = TextImageRelation.ImageAboveText;
		sketchCancelButton.ToolTipText = "Cancel the current tool";
		sketchCancelButton.Click += sketchCancelButton_Click;
		// 
		// sketchCloseLabel
		// 
		sketchCloseLabel.Dock = DockStyle.Bottom;
		sketchCloseLabel.ForeColor = SystemColors.GrayText;
		sketchCloseLabel.Location = new Point(2, 157);
		sketchCloseLabel.Name = "sketchCloseLabel";
		sketchCloseLabel.Size = new Size(197, 24);
		sketchCloseLabel.TabIndex = 1;
		sketchCloseLabel.Text = "Close";
		sketchCloseLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep7
		// 
		sketchSep7.BackColor = SystemColors.ControlDark;
		sketchSep7.Dock = DockStyle.Left;
		sketchSep7.Location = new Point(1485, 3);
		sketchSep7.Margin = new Padding(0);
		sketchSep7.Name = "sketchSep7";
		sketchSep7.Size = new Size(1, 181);
		sketchSep7.TabIndex = 0;
		// 
		// sketchUtilPanel
		// 
		sketchUtilPanel.Controls.Add(sketchUtilStrip);
		sketchUtilPanel.Controls.Add(sketchUtilLabel);
		sketchUtilPanel.Dock = DockStyle.Left;
		sketchUtilPanel.Location = new Point(1346, 3);
		sketchUtilPanel.Margin = new Padding(3, 4, 3, 4);
		sketchUtilPanel.Name = "sketchUtilPanel";
		sketchUtilPanel.Padding = new Padding(2, 3, 2, 0);
		sketchUtilPanel.Size = new Size(139, 181);
		sketchUtilPanel.TabIndex = 0;
		// 
		// sketchUtilStrip
		// 
		sketchUtilStrip.AutoSize = false;
		sketchUtilStrip.Dock = DockStyle.Fill;
		sketchUtilStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchUtilStrip.ImageScalingSize = new Size(20, 20);
		sketchUtilStrip.Items.AddRange(new ToolStripItem[] { sketchSnapGridButton, sketchSnapItemsButton, sketchParamsButton });
		sketchUtilStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchUtilStrip.Location = new Point(2, 3);
		sketchUtilStrip.Name = "sketchUtilStrip";
		sketchUtilStrip.Padding = new Padding(0);
		sketchUtilStrip.RenderMode = ToolStripRenderMode.System;
		sketchUtilStrip.Size = new Size(135, 154);
		sketchUtilStrip.TabIndex = 0;
		// 
		// sketchSnapGridButton
		// 
		sketchSnapGridButton.AutoSize = false;
		sketchSnapGridButton.CheckOnClick = true;
		sketchSnapGridButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchSnapGridButton.Margin = new Padding(1);
		sketchSnapGridButton.Name = "sketchSnapGridButton";
		sketchSnapGridButton.Size = new Size(116, 34);
		sketchSnapGridButton.Tag = "Toolbar/snap_grid.png";
		sketchSnapGridButton.Text = "Snap grid";
		sketchSnapGridButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchSnapGridButton.ToolTipText = "Snap to grid";
		sketchSnapGridButton.Click += sketchSnapGridButton_Click;
		// 
		// sketchSnapItemsButton
		// 
		sketchSnapItemsButton.AutoSize = false;
		sketchSnapItemsButton.Checked = true;
		sketchSnapItemsButton.CheckOnClick = true;
		sketchSnapItemsButton.CheckState = CheckState.Checked;
		sketchSnapItemsButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchSnapItemsButton.Margin = new Padding(1);
		sketchSnapItemsButton.Name = "sketchSnapItemsButton";
		sketchSnapItemsButton.Size = new Size(116, 34);
		sketchSnapItemsButton.Tag = "Toolbar/snap_items.png";
		sketchSnapItemsButton.Text = "Snap items";
		sketchSnapItemsButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchSnapItemsButton.ToolTipText = "Snap to items";
		sketchSnapItemsButton.Click += sketchSnapItemsButton_Click;
		// 
		// sketchParamsButton
		// 
		sketchParamsButton.AutoSize = false;
		sketchParamsButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchParamsButton.Margin = new Padding(1);
		sketchParamsButton.Name = "sketchParamsButton";
		sketchParamsButton.Size = new Size(116, 34);
		sketchParamsButton.Tag = "Toolbar/parameters.png";
		sketchParamsButton.Text = "Parameters";
		sketchParamsButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchParamsButton.ToolTipText = "Parameter table";
		sketchParamsButton.Click += parametersButton_Click;
		// 
		// sketchUtilLabel
		// 
		sketchUtilLabel.Dock = DockStyle.Bottom;
		sketchUtilLabel.ForeColor = SystemColors.GrayText;
		sketchUtilLabel.Location = new Point(2, 157);
		sketchUtilLabel.Name = "sketchUtilLabel";
		sketchUtilLabel.Size = new Size(135, 24);
		sketchUtilLabel.TabIndex = 1;
		sketchUtilLabel.Text = "Utilities";
		sketchUtilLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep6
		// 
		sketchSep6.BackColor = SystemColors.ControlDark;
		sketchSep6.Dock = DockStyle.Left;
		sketchSep6.Location = new Point(1345, 3);
		sketchSep6.Margin = new Padding(0);
		sketchSep6.Name = "sketchSep6";
		sketchSep6.Size = new Size(1, 181);
		sketchSep6.TabIndex = 0;
		// 
		// sketchDimPanel
		// 
		sketchDimPanel.Controls.Add(sketchDimStrip);
		sketchDimPanel.Controls.Add(sketchDimLabel);
		sketchDimPanel.Dock = DockStyle.Left;
		sketchDimPanel.Location = new Point(1242, 3);
		sketchDimPanel.Margin = new Padding(3, 4, 3, 4);
		sketchDimPanel.Name = "sketchDimPanel";
		sketchDimPanel.Padding = new Padding(2, 3, 2, 0);
		sketchDimPanel.Size = new Size(103, 181);
		sketchDimPanel.TabIndex = 0;
		// 
		// sketchDimStrip
		// 
		sketchDimStrip.AutoSize = false;
		sketchDimStrip.Dock = DockStyle.Fill;
		sketchDimStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchDimStrip.ImageScalingSize = new Size(20, 20);
		sketchDimStrip.Items.AddRange(new ToolStripItem[] { sketchDimButton });
		sketchDimStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchDimStrip.Location = new Point(2, 3);
		sketchDimStrip.Name = "sketchDimStrip";
		sketchDimStrip.Padding = new Padding(0);
		sketchDimStrip.RenderMode = ToolStripRenderMode.System;
		sketchDimStrip.Size = new Size(99, 154);
		sketchDimStrip.TabIndex = 0;
		// 
		// sketchDimButton
		// 
		sketchDimButton.AutoSize = false;
		sketchDimButton.CheckOnClick = true;
		sketchDimButton.ImageAlign = ContentAlignment.TopCenter;
		sketchDimButton.Margin = new Padding(1);
		sketchDimButton.Name = "sketchDimButton";
		sketchDimButton.Size = new Size(84, 104);
		sketchDimButton.Tag = "Toolbar/dimension.png";
		sketchDimButton.Text = "Dimension";
		sketchDimButton.TextAlign = ContentAlignment.BottomCenter;
		sketchDimButton.TextImageRelation = TextImageRelation.ImageAboveText;
		sketchDimButton.ToolTipText = "Dimension tool";
		sketchDimButton.Click += sketchDimButton_Click;
		// 
		// sketchDimLabel
		// 
		sketchDimLabel.Dock = DockStyle.Bottom;
		sketchDimLabel.ForeColor = SystemColors.GrayText;
		sketchDimLabel.Location = new Point(2, 157);
		sketchDimLabel.Name = "sketchDimLabel";
		sketchDimLabel.Size = new Size(99, 24);
		sketchDimLabel.TabIndex = 1;
		sketchDimLabel.Text = "Dimension";
		sketchDimLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep5
		// 
		sketchSep5.BackColor = SystemColors.ControlDark;
		sketchSep5.Dock = DockStyle.Left;
		sketchSep5.Location = new Point(1241, 3);
		sketchSep5.Margin = new Padding(0);
		sketchSep5.Name = "sketchSep5";
		sketchSep5.Size = new Size(1, 181);
		sketchSep5.TabIndex = 0;
		// 
		// sketchConstrainPanel
		// 
		sketchConstrainPanel.Controls.Add(sketchConstrainStrip);
		sketchConstrainPanel.Controls.Add(sketchConstrainLabel);
		sketchConstrainPanel.Dock = DockStyle.Left;
		sketchConstrainPanel.Location = new Point(832, 3);
		sketchConstrainPanel.Margin = new Padding(3, 4, 3, 4);
		sketchConstrainPanel.Name = "sketchConstrainPanel";
		sketchConstrainPanel.Padding = new Padding(2, 3, 2, 0);
		sketchConstrainPanel.Size = new Size(409, 181);
		sketchConstrainPanel.TabIndex = 0;
		// 
		// sketchConstrainStrip
		// 
		sketchConstrainStrip.AutoSize = false;
		sketchConstrainStrip.Dock = DockStyle.Fill;
		sketchConstrainStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchConstrainStrip.ImageScalingSize = new Size(20, 20);
		sketchConstrainStrip.Items.AddRange(new ToolStripItem[] { constVerticalButton, constTangentButton, constSymmetricButton, constHorizontalButton, constMidButton, constEqualButton, constPerpendicularButton, constCoincidentButton, constParallelButton });
		sketchConstrainStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchConstrainStrip.Location = new Point(2, 3);
		sketchConstrainStrip.Name = "sketchConstrainStrip";
		sketchConstrainStrip.Padding = new Padding(0);
		sketchConstrainStrip.RenderMode = ToolStripRenderMode.System;
		sketchConstrainStrip.Size = new Size(405, 154);
		sketchConstrainStrip.TabIndex = 0;
		// 
		// constVerticalButton
		// 
		constVerticalButton.AutoSize = false;
		constVerticalButton.CheckOnClick = true;
		constVerticalButton.ImageAlign = ContentAlignment.MiddleLeft;
		constVerticalButton.Margin = new Padding(1);
		constVerticalButton.Name = "constVerticalButton";
		constVerticalButton.Size = new Size(116, 34);
		constVerticalButton.Tag = "Toolbar/vertical.png";
		constVerticalButton.Text = "Vertical";
		constVerticalButton.TextAlign = ContentAlignment.MiddleLeft;
		constVerticalButton.ToolTipText = "Vertical constraint";
		constVerticalButton.Click += constVerticalButton_Click;
		// 
		// constTangentButton
		// 
		constTangentButton.AutoSize = false;
		constTangentButton.CheckOnClick = true;
		constTangentButton.ImageAlign = ContentAlignment.MiddleLeft;
		constTangentButton.Margin = new Padding(1);
		constTangentButton.Name = "constTangentButton";
		constTangentButton.Size = new Size(116, 34);
		constTangentButton.Tag = "Toolbar/tangent.png";
		constTangentButton.Text = "Tangent";
		constTangentButton.TextAlign = ContentAlignment.MiddleLeft;
		constTangentButton.ToolTipText = "Tangent constraint";
		constTangentButton.Click += constTangentButton_Click;
		// 
		// constSymmetricButton
		// 
		constSymmetricButton.AutoSize = false;
		constSymmetricButton.CheckOnClick = true;
		constSymmetricButton.ImageAlign = ContentAlignment.MiddleLeft;
		constSymmetricButton.Margin = new Padding(1);
		constSymmetricButton.Name = "constSymmetricButton";
		constSymmetricButton.Size = new Size(116, 34);
		constSymmetricButton.Tag = "Toolbar/symmetric.png";
		constSymmetricButton.Text = "Symmetric";
		constSymmetricButton.TextAlign = ContentAlignment.MiddleLeft;
		constSymmetricButton.ToolTipText = "Symmetric constraint";
		constSymmetricButton.Click += constSymmetricButton_Click;
		// 
		// constHorizontalButton
		// 
		constHorizontalButton.AutoSize = false;
		constHorizontalButton.CheckOnClick = true;
		constHorizontalButton.ImageAlign = ContentAlignment.MiddleLeft;
		constHorizontalButton.Margin = new Padding(1);
		constHorizontalButton.Name = "constHorizontalButton";
		constHorizontalButton.Size = new Size(116, 34);
		constHorizontalButton.Tag = "Toolbar/horizontal.png";
		constHorizontalButton.Text = "Horizontal";
		constHorizontalButton.TextAlign = ContentAlignment.MiddleLeft;
		constHorizontalButton.ToolTipText = "Horizontal constraint";
		constHorizontalButton.Click += constHorizontalButton_Click;
		// 
		// constMidButton
		// 
		constMidButton.AutoSize = false;
		constMidButton.CheckOnClick = true;
		constMidButton.ImageAlign = ContentAlignment.MiddleLeft;
		constMidButton.Margin = new Padding(1);
		constMidButton.Name = "constMidButton";
		constMidButton.Size = new Size(116, 34);
		constMidButton.Tag = "Toolbar/midpoint.png";
		constMidButton.Text = "Mid-point";
		constMidButton.TextAlign = ContentAlignment.MiddleLeft;
		constMidButton.ToolTipText = "Mid-point constraint";
		constMidButton.Click += constMidButton_Click;
		// 
		// constEqualButton
		// 
		constEqualButton.AutoSize = false;
		constEqualButton.CheckOnClick = true;
		constEqualButton.ImageAlign = ContentAlignment.MiddleLeft;
		constEqualButton.Margin = new Padding(1);
		constEqualButton.Name = "constEqualButton";
		constEqualButton.Size = new Size(116, 34);
		constEqualButton.Tag = "Toolbar/equal.png";
		constEqualButton.Text = "Equal";
		constEqualButton.TextAlign = ContentAlignment.MiddleLeft;
		constEqualButton.ToolTipText = "Equal constraint";
		constEqualButton.Click += constEqualButton_Click;
		// 
		// constPerpendicularButton
		// 
		constPerpendicularButton.AutoSize = false;
		constPerpendicularButton.CheckOnClick = true;
		constPerpendicularButton.ImageAlign = ContentAlignment.MiddleLeft;
		constPerpendicularButton.Margin = new Padding(1);
		constPerpendicularButton.Name = "constPerpendicularButton";
		constPerpendicularButton.Size = new Size(116, 34);
		constPerpendicularButton.Tag = "Toolbar/perpendicular.png";
		constPerpendicularButton.Text = "Perpendicular";
		constPerpendicularButton.TextAlign = ContentAlignment.MiddleLeft;
		constPerpendicularButton.ToolTipText = "Perpendicular constraint";
		constPerpendicularButton.Click += constPerpendicularButton_Click;
		// 
		// constCoincidentButton
		// 
		constCoincidentButton.AutoSize = false;
		constCoincidentButton.CheckOnClick = true;
		constCoincidentButton.ImageAlign = ContentAlignment.MiddleLeft;
		constCoincidentButton.Margin = new Padding(1);
		constCoincidentButton.Name = "constCoincidentButton";
		constCoincidentButton.Size = new Size(116, 34);
		constCoincidentButton.Tag = "Toolbar/coincident.png";
		constCoincidentButton.Text = "Coincident";
		constCoincidentButton.TextAlign = ContentAlignment.MiddleLeft;
		constCoincidentButton.ToolTipText = "Coincident constraint";
		constCoincidentButton.Click += constCoincidentButton_Click;
		// 
		// constParallelButton
		// 
		constParallelButton.AutoSize = false;
		constParallelButton.CheckOnClick = true;
		constParallelButton.ImageAlign = ContentAlignment.MiddleLeft;
		constParallelButton.Margin = new Padding(1);
		constParallelButton.Name = "constParallelButton";
		constParallelButton.Size = new Size(116, 34);
		constParallelButton.Tag = "Toolbar/parallel.png";
		constParallelButton.Text = "Parallel";
		constParallelButton.TextAlign = ContentAlignment.MiddleLeft;
		constParallelButton.ToolTipText = "Parallel constraint";
		constParallelButton.Click += constParallelButton_Click;
		// 
		// sketchConstrainLabel
		// 
		sketchConstrainLabel.Dock = DockStyle.Bottom;
		sketchConstrainLabel.ForeColor = SystemColors.GrayText;
		sketchConstrainLabel.Location = new Point(2, 157);
		sketchConstrainLabel.Name = "sketchConstrainLabel";
		sketchConstrainLabel.Size = new Size(405, 24);
		sketchConstrainLabel.TabIndex = 1;
		sketchConstrainLabel.Text = "Constrain";
		sketchConstrainLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep4
		// 
		sketchSep4.BackColor = SystemColors.ControlDark;
		sketchSep4.Dock = DockStyle.Left;
		sketchSep4.Location = new Point(831, 3);
		sketchSep4.Margin = new Padding(0);
		sketchSep4.Name = "sketchSep4";
		sketchSep4.Size = new Size(1, 181);
		sketchSep4.TabIndex = 0;
		// 
		// sketchEditPanel
		// 
		sketchEditPanel.Controls.Add(sketchEditStrip);
		sketchEditPanel.Controls.Add(sketchEditLabel);
		sketchEditPanel.Dock = DockStyle.Left;
		sketchEditPanel.Location = new Point(692, 3);
		sketchEditPanel.Margin = new Padding(3, 4, 3, 4);
		sketchEditPanel.Name = "sketchEditPanel";
		sketchEditPanel.Padding = new Padding(2, 3, 2, 0);
		sketchEditPanel.Size = new Size(139, 181);
		sketchEditPanel.TabIndex = 0;
		// 
		// sketchEditStrip
		// 
		sketchEditStrip.AutoSize = false;
		sketchEditStrip.Dock = DockStyle.Fill;
		sketchEditStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchEditStrip.ImageScalingSize = new Size(20, 20);
		sketchEditStrip.Items.AddRange(new ToolStripItem[] { sketchFilletButton, sketchChamferButton, sketchDelSegmentButton });
		sketchEditStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchEditStrip.Location = new Point(2, 3);
		sketchEditStrip.Name = "sketchEditStrip";
		sketchEditStrip.Padding = new Padding(0);
		sketchEditStrip.RenderMode = ToolStripRenderMode.System;
		sketchEditStrip.Size = new Size(135, 154);
		sketchEditStrip.TabIndex = 0;
		// 
		// sketchFilletButton
		// 
		sketchFilletButton.AutoSize = false;
		sketchFilletButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchFilletButton.Margin = new Padding(1);
		sketchFilletButton.Name = "sketchFilletButton";
		sketchFilletButton.Size = new Size(116, 34);
		sketchFilletButton.Tag = "Toolbar/fillet.png";
		sketchFilletButton.Text = "Fillet";
		sketchFilletButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchFilletButton.ToolTipText = "Round the corner between two lines";
		sketchFilletButton.Click += sketchFilletButton_Click;
		// 
		// sketchChamferButton
		// 
		sketchChamferButton.AutoSize = false;
		sketchChamferButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchChamferButton.Margin = new Padding(1);
		sketchChamferButton.Name = "sketchChamferButton";
		sketchChamferButton.Size = new Size(116, 34);
		sketchChamferButton.Tag = "Toolbar/chamfer.png";
		sketchChamferButton.Text = "Chamfer";
		sketchChamferButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchChamferButton.ToolTipText = "Chamfer the corner between two lines";
		sketchChamferButton.Click += sketchChamferButton_Click;
		// 
		// sketchDelSegmentButton
		// 
		sketchDelSegmentButton.AutoSize = false;
		sketchDelSegmentButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchDelSegmentButton.Margin = new Padding(1);
		sketchDelSegmentButton.Name = "sketchDelSegmentButton";
		sketchDelSegmentButton.Size = new Size(116, 34);
		sketchDelSegmentButton.Tag = "Toolbar/delete_segment.png";
		sketchDelSegmentButton.Text = "Delete Segment";
		sketchDelSegmentButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchDelSegmentButton.ToolTipText = "Delete the selected sketch items (Del)";
		sketchDelSegmentButton.Click += deleteButton_Click;
		// 
		// sketchEditLabel
		// 
		sketchEditLabel.Dock = DockStyle.Bottom;
		sketchEditLabel.ForeColor = SystemColors.GrayText;
		sketchEditLabel.Location = new Point(2, 157);
		sketchEditLabel.Name = "sketchEditLabel";
		sketchEditLabel.Size = new Size(135, 24);
		sketchEditLabel.TabIndex = 1;
		sketchEditLabel.Text = "Editing";
		sketchEditLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep3
		// 
		sketchSep3.BackColor = SystemColors.ControlDark;
		sketchSep3.Dock = DockStyle.Left;
		sketchSep3.Location = new Point(691, 3);
		sketchSep3.Margin = new Padding(0);
		sketchSep3.Name = "sketchSep3";
		sketchSep3.Size = new Size(1, 181);
		sketchSep3.TabIndex = 0;
		// 
		// sketchingPanel
		// 
		sketchingPanel.Controls.Add(sketchingStrip);
		sketchingPanel.Controls.Add(sketchingLabel);
		sketchingPanel.Dock = DockStyle.Left;
		sketchingPanel.Location = new Point(282, 3);
		sketchingPanel.Margin = new Padding(3, 4, 3, 4);
		sketchingPanel.Name = "sketchingPanel";
		sketchingPanel.Padding = new Padding(2, 3, 2, 0);
		sketchingPanel.Size = new Size(409, 181);
		sketchingPanel.TabIndex = 0;
		// 
		// sketchingStrip
		// 
		sketchingStrip.AutoSize = false;
		sketchingStrip.Dock = DockStyle.Fill;
		sketchingStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchingStrip.ImageScalingSize = new Size(20, 20);
		sketchingStrip.Items.AddRange(new ToolStripItem[] { sketchLineButton, sketchRectangleButton, sketchCircleButton, sketchArcCppButton, sketchArcPppButton, sketchEllipseButton, sketchCircle3pButton, sketchCurveButton, sketchPolygonButton });
		sketchingStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchingStrip.Location = new Point(2, 3);
		sketchingStrip.Name = "sketchingStrip";
		sketchingStrip.Padding = new Padding(0);
		sketchingStrip.RenderMode = ToolStripRenderMode.System;
		sketchingStrip.Size = new Size(405, 154);
		sketchingStrip.TabIndex = 0;
		// 
		// sketchLineButton
		// 
		sketchLineButton.AutoSize = false;
		sketchLineButton.CheckOnClick = true;
		sketchLineButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchLineButton.Margin = new Padding(1);
		sketchLineButton.Name = "sketchLineButton";
		sketchLineButton.Size = new Size(116, 34);
		sketchLineButton.Tag = "Toolbar/line2d.png";
		sketchLineButton.Text = "Line";
		sketchLineButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchLineButton.ToolTipText = "Line chain, Escape finishes";
		sketchLineButton.Click += sketchTool_Click;
		// 
		// sketchRectangleButton
		// 
		sketchRectangleButton.AutoSize = false;
		sketchRectangleButton.CheckOnClick = true;
		sketchRectangleButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchRectangleButton.Margin = new Padding(1);
		sketchRectangleButton.Name = "sketchRectangleButton";
		sketchRectangleButton.Size = new Size(116, 34);
		sketchRectangleButton.Tag = "Toolbar/rectangle.png";
		sketchRectangleButton.Text = "Rectangle";
		sketchRectangleButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchRectangleButton.ToolTipText = "Rectangle by two corners";
		sketchRectangleButton.Click += sketchTool_Click;
		// 
		// sketchCircleButton
		// 
		sketchCircleButton.AutoSize = false;
		sketchCircleButton.CheckOnClick = true;
		sketchCircleButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchCircleButton.Margin = new Padding(1);
		sketchCircleButton.Name = "sketchCircleButton";
		sketchCircleButton.Size = new Size(116, 34);
		sketchCircleButton.Tag = "Toolbar/circle.png";
		sketchCircleButton.Text = "Circle";
		sketchCircleButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchCircleButton.ToolTipText = "Circle by center and point";
		sketchCircleButton.Click += sketchTool_Click;
		// 
		// sketchArcCppButton
		// 
		sketchArcCppButton.AutoSize = false;
		sketchArcCppButton.CheckOnClick = true;
		sketchArcCppButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchArcCppButton.Margin = new Padding(1);
		sketchArcCppButton.Name = "sketchArcCppButton";
		sketchArcCppButton.Size = new Size(116, 34);
		sketchArcCppButton.Tag = "Toolbar/arc_cpp.png";
		sketchArcCppButton.Text = "Arc";
		sketchArcCppButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchArcCppButton.ToolTipText = "Arc by center, start and end";
		sketchArcCppButton.Click += sketchTool_Click;
		// 
		// sketchArcPppButton
		// 
		sketchArcPppButton.AutoSize = false;
		sketchArcPppButton.CheckOnClick = true;
		sketchArcPppButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchArcPppButton.Margin = new Padding(1);
		sketchArcPppButton.Name = "sketchArcPppButton";
		sketchArcPppButton.Size = new Size(116, 34);
		sketchArcPppButton.Tag = "Toolbar/arc_ppp.png";
		sketchArcPppButton.Text = "Arc x 3p";
		sketchArcPppButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchArcPppButton.ToolTipText = "Arc through three points";
		sketchArcPppButton.Click += sketchTool_Click;
		// 
		// sketchEllipseButton
		// 
		sketchEllipseButton.AutoSize = false;
		sketchEllipseButton.CheckOnClick = true;
		sketchEllipseButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchEllipseButton.Margin = new Padding(1);
		sketchEllipseButton.Name = "sketchEllipseButton";
		sketchEllipseButton.Size = new Size(116, 34);
		sketchEllipseButton.Tag = "Toolbar/ellipse.png";
		sketchEllipseButton.Text = "Ellipse";
		sketchEllipseButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchEllipseButton.ToolTipText = "Ellipse by center and two axes";
		sketchEllipseButton.Click += sketchTool_Click;
		// 
		// sketchCircle3pButton
		// 
		sketchCircle3pButton.AutoSize = false;
		sketchCircle3pButton.CheckOnClick = true;
		sketchCircle3pButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchCircle3pButton.Margin = new Padding(1);
		sketchCircle3pButton.Name = "sketchCircle3pButton";
		sketchCircle3pButton.Size = new Size(116, 34);
		sketchCircle3pButton.Tag = "Toolbar/circle_3p.png";
		sketchCircle3pButton.Text = "Circle x 3p";
		sketchCircle3pButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchCircle3pButton.ToolTipText = "Circle through three points";
		sketchCircle3pButton.Click += sketchTool_Click;
		// 
		// sketchCurveButton
		// 
		sketchCurveButton.AutoSize = false;
		sketchCurveButton.CheckOnClick = true;
		sketchCurveButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchCurveButton.Margin = new Padding(1);
		sketchCurveButton.Name = "sketchCurveButton";
		sketchCurveButton.Size = new Size(116, 34);
		sketchCurveButton.Tag = "Toolbar/spline.png";
		sketchCurveButton.Text = "Spline";
		sketchCurveButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchCurveButton.ToolTipText = "Spline, right click finishes";
		sketchCurveButton.Click += sketchTool_Click;
		// 
		// sketchPolygonButton
		// 
		sketchPolygonButton.AutoSize = false;
		sketchPolygonButton.Enabled = false;
		sketchPolygonButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchPolygonButton.Margin = new Padding(1);
		sketchPolygonButton.Name = "sketchPolygonButton";
		sketchPolygonButton.Size = new Size(116, 34);
		sketchPolygonButton.Tag = "Toolbar/polygon.png";
		sketchPolygonButton.Text = "Polygon";
		sketchPolygonButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchPolygonButton.ToolTipText = "Polygon (not implemented yet)";
		// 
		// sketchingLabel
		// 
		sketchingLabel.Dock = DockStyle.Bottom;
		sketchingLabel.ForeColor = SystemColors.GrayText;
		sketchingLabel.Location = new Point(2, 157);
		sketchingLabel.Name = "sketchingLabel";
		sketchingLabel.Size = new Size(405, 24);
		sketchingLabel.TabIndex = 1;
		sketchingLabel.Text = "Sketching";
		sketchingLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep2
		// 
		sketchSep2.BackColor = SystemColors.ControlDark;
		sketchSep2.Dock = DockStyle.Left;
		sketchSep2.Location = new Point(281, 3);
		sketchSep2.Margin = new Padding(0);
		sketchSep2.Name = "sketchSep2";
		sketchSep2.Size = new Size(1, 181);
		sketchSep2.TabIndex = 0;
		// 
		// sketchDatumPanel
		// 
		sketchDatumPanel.Controls.Add(sketchDatumStrip);
		sketchDatumPanel.Controls.Add(sketchDatumLabel);
		sketchDatumPanel.Dock = DockStyle.Left;
		sketchDatumPanel.Location = new Point(142, 3);
		sketchDatumPanel.Margin = new Padding(3, 4, 3, 4);
		sketchDatumPanel.Name = "sketchDatumPanel";
		sketchDatumPanel.Padding = new Padding(2, 3, 2, 0);
		sketchDatumPanel.Size = new Size(139, 181);
		sketchDatumPanel.TabIndex = 0;
		// 
		// sketchDatumStrip
		// 
		sketchDatumStrip.AutoSize = false;
		sketchDatumStrip.Dock = DockStyle.Fill;
		sketchDatumStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchDatumStrip.ImageScalingSize = new Size(20, 20);
		sketchDatumStrip.Items.AddRange(new ToolStripItem[] { sketchCenterlineButton, sketchPointButton, sketchConstructionButton });
		sketchDatumStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchDatumStrip.Location = new Point(2, 3);
		sketchDatumStrip.Name = "sketchDatumStrip";
		sketchDatumStrip.Padding = new Padding(0);
		sketchDatumStrip.RenderMode = ToolStripRenderMode.System;
		sketchDatumStrip.Size = new Size(135, 154);
		sketchDatumStrip.TabIndex = 0;
		// 
		// sketchCenterlineButton
		// 
		sketchCenterlineButton.AutoSize = false;
		sketchCenterlineButton.CheckOnClick = true;
		sketchCenterlineButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchCenterlineButton.Margin = new Padding(1);
		sketchCenterlineButton.Name = "sketchCenterlineButton";
		sketchCenterlineButton.Size = new Size(116, 34);
		sketchCenterlineButton.Tag = "Toolbar/centerline.png";
		sketchCenterlineButton.Text = "Centerline";
		sketchCenterlineButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchCenterlineButton.ToolTipText = "Draw a centerline (revolve axis)";
		sketchCenterlineButton.Click += sketchTool_Click;
		// 
		// sketchPointButton
		// 
		sketchPointButton.AutoSize = false;
		sketchPointButton.CheckOnClick = true;
		sketchPointButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchPointButton.Margin = new Padding(1);
		sketchPointButton.Name = "sketchPointButton";
		sketchPointButton.Size = new Size(116, 34);
		sketchPointButton.Tag = "Toolbar/point2d.png";
		sketchPointButton.Text = "Point";
		sketchPointButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchPointButton.ToolTipText = "Point";
		sketchPointButton.Click += sketchTool_Click;
		// 
		// sketchConstructionButton
		// 
		sketchConstructionButton.AutoSize = false;
		sketchConstructionButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchConstructionButton.Margin = new Padding(1);
		sketchConstructionButton.Name = "sketchConstructionButton";
		sketchConstructionButton.Size = new Size(116, 34);
		sketchConstructionButton.Tag = "Toolbar/construction.png";
		sketchConstructionButton.Text = "Construction";
		sketchConstructionButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchConstructionButton.ToolTipText = "Mark selection as construction geometry";
		sketchConstructionButton.Click += sketchConstructionButton_Click;
		// 
		// sketchDatumLabel
		// 
		sketchDatumLabel.Dock = DockStyle.Bottom;
		sketchDatumLabel.ForeColor = SystemColors.GrayText;
		sketchDatumLabel.Location = new Point(2, 157);
		sketchDatumLabel.Name = "sketchDatumLabel";
		sketchDatumLabel.Size = new Size(135, 24);
		sketchDatumLabel.TabIndex = 1;
		sketchDatumLabel.Text = "Datum";
		sketchDatumLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// sketchSep1
		// 
		sketchSep1.BackColor = SystemColors.ControlDark;
		sketchSep1.Dock = DockStyle.Left;
		sketchSep1.Location = new Point(141, 3);
		sketchSep1.Margin = new Padding(0);
		sketchSep1.Name = "sketchSep1";
		sketchSep1.Size = new Size(1, 181);
		sketchSep1.TabIndex = 0;
		// 
		// sketchSetupPanel
		// 
		sketchSetupPanel.Controls.Add(sketchSetupStrip);
		sketchSetupPanel.Controls.Add(sketchSetupLabel);
		sketchSetupPanel.Dock = DockStyle.Left;
		sketchSetupPanel.Location = new Point(2, 3);
		sketchSetupPanel.Margin = new Padding(3, 4, 3, 4);
		sketchSetupPanel.Name = "sketchSetupPanel";
		sketchSetupPanel.Padding = new Padding(2, 3, 2, 0);
		sketchSetupPanel.Size = new Size(139, 181);
		sketchSetupPanel.TabIndex = 0;
		// 
		// sketchSetupStrip
		// 
		sketchSetupStrip.AutoSize = false;
		sketchSetupStrip.Dock = DockStyle.Fill;
		sketchSetupStrip.GripStyle = ToolStripGripStyle.Hidden;
		sketchSetupStrip.ImageScalingSize = new Size(20, 20);
		sketchSetupStrip.Items.AddRange(new ToolStripItem[] { sketchSetupButton, sketchReferencesButton, sketchViewButton });
		sketchSetupStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
		sketchSetupStrip.Location = new Point(2, 3);
		sketchSetupStrip.Name = "sketchSetupStrip";
		sketchSetupStrip.Padding = new Padding(0);
		sketchSetupStrip.RenderMode = ToolStripRenderMode.System;
		sketchSetupStrip.Size = new Size(135, 154);
		sketchSetupStrip.TabIndex = 0;
		// 
		// sketchSetupButton
		// 
		sketchSetupButton.AutoSize = false;
		sketchSetupButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchSetupButton.Margin = new Padding(1);
		sketchSetupButton.Name = "sketchSetupButton";
		sketchSetupButton.Size = new Size(116, 34);
		sketchSetupButton.Tag = "Toolbar/setup.png";
		sketchSetupButton.Text = "Sketch Setup";
		sketchSetupButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchSetupButton.ToolTipText = "Sketch settings";
		sketchSetupButton.Click += sketchSetupButton_Click;
		// 
		// sketchReferencesButton
		// 
		sketchReferencesButton.AutoSize = false;
		sketchReferencesButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchReferencesButton.Margin = new Padding(1);
		sketchReferencesButton.Name = "sketchReferencesButton";
		sketchReferencesButton.Size = new Size(116, 34);
		sketchReferencesButton.Tag = "Toolbar/references.png";
		sketchReferencesButton.Text = "References";
		sketchReferencesButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchReferencesButton.ToolTipText = "Mark selection as reference geometry";
		sketchReferencesButton.Click += sketchReferencesButton_Click;
		// 
		// sketchViewButton
		// 
		sketchViewButton.AutoSize = false;
		sketchViewButton.ImageAlign = ContentAlignment.MiddleLeft;
		sketchViewButton.Margin = new Padding(1);
		sketchViewButton.Name = "sketchViewButton";
		sketchViewButton.Size = new Size(116, 34);
		sketchViewButton.Tag = "Toolbar/sketch_view.png";
		sketchViewButton.Text = "Sketch View";
		sketchViewButton.TextAlign = ContentAlignment.MiddleLeft;
		sketchViewButton.ToolTipText = "Look normal to the active sketch plane";
		sketchViewButton.Click += sketchViewButton_Click;
		// 
		// sketchSetupLabel
		// 
		sketchSetupLabel.Dock = DockStyle.Bottom;
		sketchSetupLabel.ForeColor = SystemColors.GrayText;
		sketchSetupLabel.Location = new Point(2, 157);
		sketchSetupLabel.Name = "sketchSetupLabel";
		sketchSetupLabel.Size = new Size(135, 24);
		sketchSetupLabel.TabIndex = 1;
		sketchSetupLabel.Text = "Setup";
		sketchSetupLabel.TextAlign = ContentAlignment.MiddleCenter;
		// 
		// viewTools
		// 
		viewTools.GripStyle = ToolStripGripStyle.Hidden;
		viewTools.ImageScalingSize = new Size(20, 20);
		viewTools.Items.AddRange(new ToolStripItem[] { perspViewButton, viewOrientButton });
		viewTools.Location = new Point(0, 0);
		viewTools.Name = "viewTools";
		viewTools.RenderMode = ToolStripRenderMode.System;
		viewTools.Size = new Size(1442, 31);
		viewTools.TabIndex = 0;
		// 
		// perspViewButton
		// 
		perspViewButton.Name = "perspViewButton";
		perspViewButton.Size = new Size(70, 22);
		perspViewButton.Tag = "Toolbar/perspective.png";
		perspViewButton.Text = "Projection";
		perspViewButton.ToolTipText = "Toggle perspective / orthographic";
		perspViewButton.Click += viewSwitchButton_Click;
		// 
		// viewOrientButton
		// 
		viewOrientButton.DropDownItems.AddRange(new ToolStripItem[] { viewFrontMenuItem, viewBackMenuItem, viewLeftMenuItem, viewRightMenuItem, viewTopMenuItem, viewOrientSep, viewStandardMenuItem, viewNormalMenuItem });
		viewOrientButton.Name = "viewOrientButton";
		viewOrientButton.Size = new Size(86, 22);
		viewOrientButton.Tag = "Toolbar/view_orient.png";
		viewOrientButton.Text = "Orientation";
		viewOrientButton.ToolTipText = "View orientation";
		// 
		// viewFrontMenuItem
		// 
		viewFrontMenuItem.Name = "viewFrontMenuItem";
		viewFrontMenuItem.Size = new Size(148, 22);
		viewFrontMenuItem.Tag = "Toolbar/view_front.png";
		viewFrontMenuItem.Text = "Front";
		viewFrontMenuItem.Click += viewFrontMenuItem_Click;
		// 
		// viewBackMenuItem
		// 
		viewBackMenuItem.Name = "viewBackMenuItem";
		viewBackMenuItem.Size = new Size(148, 22);
		viewBackMenuItem.Tag = "Toolbar/view_back.png";
		viewBackMenuItem.Text = "Back";
		viewBackMenuItem.Click += viewBackMenuItem_Click;
		// 
		// viewLeftMenuItem
		// 
		viewLeftMenuItem.Name = "viewLeftMenuItem";
		viewLeftMenuItem.Size = new Size(148, 22);
		viewLeftMenuItem.Tag = "Toolbar/view_left.png";
		viewLeftMenuItem.Text = "Left";
		viewLeftMenuItem.Click += viewLeftMenuItem_Click;
		// 
		// viewRightMenuItem
		// 
		viewRightMenuItem.Name = "viewRightMenuItem";
		viewRightMenuItem.Size = new Size(148, 22);
		viewRightMenuItem.Tag = "Toolbar/view_right.png";
		viewRightMenuItem.Text = "Right";
		viewRightMenuItem.Click += viewRightMenuItem_Click;
		// 
		// viewTopMenuItem
		// 
		viewTopMenuItem.Name = "viewTopMenuItem";
		viewTopMenuItem.Size = new Size(148, 22);
		viewTopMenuItem.Tag = "Toolbar/view_top.png";
		viewTopMenuItem.Text = "Top";
		viewTopMenuItem.Click += viewTopMenuItem_Click;
		// 
		// viewOrientSep
		// 
		viewOrientSep.Name = "viewOrientSep";
		viewOrientSep.Size = new Size(145, 6);
		// 
		// viewStandardMenuItem
		// 
		viewStandardMenuItem.Name = "viewStandardMenuItem";
		viewStandardMenuItem.Size = new Size(148, 22);
		viewStandardMenuItem.Text = "Standard";
		viewStandardMenuItem.Click += viewStandardMenuItem_Click;
		// 
		// viewNormalMenuItem
		// 
		viewNormalMenuItem.Name = "viewNormalMenuItem";
		viewNormalMenuItem.Size = new Size(148, 22);
		viewNormalMenuItem.Tag = "Toolbar/view_normal.png";
		viewNormalMenuItem.Text = "View normal";
		viewNormalMenuItem.Click += sketchViewButton_Click;
		// 
		// mainSplit
		// 
		mainSplit.Dock = DockStyle.Fill;
		mainSplit.FixedPanel = FixedPanel.Panel1;
		mainSplit.Location = new Point(0, 281);
		mainSplit.Margin = new Padding(3, 4, 3, 4);
		mainSplit.Name = "mainSplit";
		// 
		// mainSplit.Panel1
		// 
		mainSplit.Panel1.Controls.Add(projectTree);
		// 
		// mainSplit.Panel2
		// 
		mainSplit.Panel2.Controls.Add(glView);
		mainSplit.Panel2.Controls.Add(viewTools);
		mainSplit.Size = new Size(1790, 829);
		mainSplit.SplitterDistance = 343;
		mainSplit.SplitterWidth = 5;
		mainSplit.TabIndex = 3;
		// 
		// projectTree
		// 
		projectTree.BackColor = Color.FromArgb(250, 250, 252);
		projectTree.Dock = DockStyle.Fill;
		projectTree.Font = new Font("Segoe UI", 9F);
		projectTree.Location = new Point(0, 0);
		projectTree.Margin = new Padding(3, 4, 3, 4);
		projectTree.Name = "projectTree";
		projectTree.Size = new Size(343, 829);
		projectTree.TabIndex = 0;
		// 
		// glView
		// 
		glView.API = OpenTK.Windowing.Common.ContextAPI.OpenGL;
		glView.APIVersion = new Version(3, 3, 0, 0);
		glView.BodyMode = Render.BodyViewMode.ShadedWithEdges;
		glView.Dock = DockStyle.Fill;
		glView.Flags = OpenTK.Windowing.Common.ContextFlags.Default;
		glView.IsEventDriven = true;
		glView.Location = new Point(0, 31);
		glView.Margin = new Padding(3, 4, 3, 4);
		glView.Name = "glView";
		glView.Profile = OpenTK.Windowing.Common.ContextProfile.Core;
		glView.Projection = Render.ProjectionMode.Perspective;
		glView.Samples = 4;
		glView.SharedContext = null;
		glView.ShowDatumPlanes = true;
		glView.Size = new Size(1442, 798);
		glView.TabIndex = 1;
		// 
		// statusStrip
		// 
		statusStrip.ImageScalingSize = new Size(20, 20);
		statusStrip.Items.AddRange(new ToolStripItem[] { statusModeLabel, statusSelectionLabel, statusInfoLabel, selectGeometryButton });
		statusStrip.Location = new Point(0, 1110);
		statusStrip.Name = "statusStrip";
		statusStrip.Padding = new Padding(1, 0, 16, 0);
		statusStrip.Size = new Size(1790, 35);
		statusStrip.TabIndex = 4;
		// 
		// statusModeLabel
		// 
		statusModeLabel.AutoSize = false;
		statusModeLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
		statusModeLabel.Font = new Font("Segoe UI", 7.20000029F, FontStyle.Bold);
		statusModeLabel.Name = "statusModeLabel";
		statusModeLabel.Size = new Size(230, 17);
		statusModeLabel.TextAlign = ContentAlignment.MiddleLeft;
		// 
		// statusSelectionLabel
		// 
		statusSelectionLabel.AutoSize = false;
		statusSelectionLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
		statusSelectionLabel.Name = "statusSelectionLabel";
		statusSelectionLabel.Size = new Size(300, 17);
		statusSelectionLabel.TextAlign = ContentAlignment.MiddleLeft;
		// 
		// statusInfoLabel
		// 
		statusInfoLabel.Name = "statusInfoLabel";
		statusInfoLabel.Size = new Size(906, 17);
		statusInfoLabel.Spring = true;
		statusInfoLabel.TextAlign = ContentAlignment.MiddleLeft;
		// 
		// selectGeometryButton
		// 
		selectGeometryButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
		selectGeometryButton.DropDownItems.AddRange(new ToolStripItem[] { planeSelectMenuItem, quiltSelectMenuItem, curveSelectMenuItem, edgeSelectMenuItem, centerlineSelectMenuItem, axisSelectMenuItem, sketchSelectMenuItem, partSelectMenuItem, featureSelectMenuItem, vertexSelectMenuItem, arrowSelectMenuItem, dimensionSelectMenuItem, constraintSelectMenuItem });
		selectGeometryButton.Name = "selectGeometryButton";
		selectGeometryButton.Size = new Size(115, 21);
		selectGeometryButton.Text = "Select geometry";
		// 
		// planeSelectMenuItem
		// 
		planeSelectMenuItem.Checked = true;
		planeSelectMenuItem.CheckOnClick = true;
		planeSelectMenuItem.CheckState = CheckState.Checked;
		planeSelectMenuItem.Name = "planeSelectMenuItem";
		planeSelectMenuItem.Size = new Size(137, 22);
		planeSelectMenuItem.Text = "Plane";
		planeSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// quiltSelectMenuItem
		// 
		quiltSelectMenuItem.Checked = true;
		quiltSelectMenuItem.CheckOnClick = true;
		quiltSelectMenuItem.CheckState = CheckState.Checked;
		quiltSelectMenuItem.Name = "quiltSelectMenuItem";
		quiltSelectMenuItem.Size = new Size(137, 22);
		quiltSelectMenuItem.Text = "Quilt";
		quiltSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// curveSelectMenuItem
		// 
		curveSelectMenuItem.Checked = true;
		curveSelectMenuItem.CheckOnClick = true;
		curveSelectMenuItem.CheckState = CheckState.Checked;
		curveSelectMenuItem.Name = "curveSelectMenuItem";
		curveSelectMenuItem.Size = new Size(137, 22);
		curveSelectMenuItem.Text = "Curve";
		curveSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// edgeSelectMenuItem
		// 
		edgeSelectMenuItem.Checked = true;
		edgeSelectMenuItem.CheckOnClick = true;
		edgeSelectMenuItem.CheckState = CheckState.Checked;
		edgeSelectMenuItem.Name = "edgeSelectMenuItem";
		edgeSelectMenuItem.Size = new Size(137, 22);
		edgeSelectMenuItem.Text = "Edge";
		edgeSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// centerlineSelectMenuItem
		// 
		centerlineSelectMenuItem.Checked = true;
		centerlineSelectMenuItem.CheckOnClick = true;
		centerlineSelectMenuItem.CheckState = CheckState.Checked;
		centerlineSelectMenuItem.Name = "centerlineSelectMenuItem";
		centerlineSelectMenuItem.Size = new Size(137, 22);
		centerlineSelectMenuItem.Text = "Centerline";
		centerlineSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// axisSelectMenuItem
		// 
		axisSelectMenuItem.Checked = true;
		axisSelectMenuItem.CheckOnClick = true;
		axisSelectMenuItem.CheckState = CheckState.Checked;
		axisSelectMenuItem.Name = "axisSelectMenuItem";
		axisSelectMenuItem.Size = new Size(137, 22);
		axisSelectMenuItem.Text = "Axis";
		axisSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// sketchSelectMenuItem
		// 
		sketchSelectMenuItem.Checked = true;
		sketchSelectMenuItem.CheckOnClick = true;
		sketchSelectMenuItem.CheckState = CheckState.Checked;
		sketchSelectMenuItem.Name = "sketchSelectMenuItem";
		sketchSelectMenuItem.Size = new Size(137, 22);
		sketchSelectMenuItem.Text = "Sketch";
		sketchSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// partSelectMenuItem
		// 
		partSelectMenuItem.Checked = true;
		partSelectMenuItem.CheckOnClick = true;
		partSelectMenuItem.CheckState = CheckState.Checked;
		partSelectMenuItem.Name = "partSelectMenuItem";
		partSelectMenuItem.Size = new Size(137, 22);
		partSelectMenuItem.Text = "Part";
		partSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// featureSelectMenuItem
		// 
		featureSelectMenuItem.Checked = true;
		featureSelectMenuItem.CheckOnClick = true;
		featureSelectMenuItem.CheckState = CheckState.Checked;
		featureSelectMenuItem.Name = "featureSelectMenuItem";
		featureSelectMenuItem.Size = new Size(137, 22);
		featureSelectMenuItem.Text = "Feature";
		featureSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// vertexSelectMenuItem
		// 
		vertexSelectMenuItem.Checked = true;
		vertexSelectMenuItem.CheckOnClick = true;
		vertexSelectMenuItem.CheckState = CheckState.Checked;
		vertexSelectMenuItem.Name = "vertexSelectMenuItem";
		vertexSelectMenuItem.Size = new Size(137, 22);
		vertexSelectMenuItem.Text = "Vertex";
		vertexSelectMenuItem.CheckedChanged += selectFilterMenuItem_CheckedChanged;
		// 
		// arrowSelectMenuItem
		// 
		arrowSelectMenuItem.Enabled = false;
		arrowSelectMenuItem.Name = "arrowSelectMenuItem";
		arrowSelectMenuItem.Size = new Size(137, 22);
		arrowSelectMenuItem.Text = "Arrow";
		// 
		// dimensionSelectMenuItem
		// 
		dimensionSelectMenuItem.Enabled = false;
		dimensionSelectMenuItem.Name = "dimensionSelectMenuItem";
		dimensionSelectMenuItem.Size = new Size(137, 22);
		dimensionSelectMenuItem.Text = "Dimension";
		// 
		// constraintSelectMenuItem
		// 
		constraintSelectMenuItem.Enabled = false;
		constraintSelectMenuItem.Name = "constraintSelectMenuItem";
		constraintSelectMenuItem.Size = new Size(137, 22);
		constraintSelectMenuItem.Text = "Constraint";
		// 
		// MainForm
		// 
		AutoScaleDimensions = new SizeF(8F, 20F);
		AutoScaleMode = AutoScaleMode.Font;
		ClientSize = new Size(1790, 1145);
		Controls.Add(mainSplit);
		Controls.Add(mainToolbar);
		Controls.Add(mainToolStrip);
		Controls.Add(mainMenu);
		Controls.Add(statusStrip);
		KeyPreview = true;
		MainMenuStrip = mainMenu;
		Margin = new Padding(3, 4, 3, 4);
		Name = "MainForm";
		StartPosition = FormStartPosition.CenterScreen;
		Text = "Parametric3D";
		mainMenu.ResumeLayout(false);
		mainMenu.PerformLayout();
		mainToolStrip.ResumeLayout(false);
		mainToolStrip.PerformLayout();
		mainToolbar.ResumeLayout(false);
		modelTab.ResumeLayout(false);
		modelPropPanel.ResumeLayout(false);
		modelPropStrip.ResumeLayout(false);
		modelPropStrip.PerformLayout();
		modelDisplayPanel.ResumeLayout(false);
		modelDisplayStrip.ResumeLayout(false);
		modelDisplayStrip.PerformLayout();
		modelEditPanel.ResumeLayout(false);
		modelEditStrip.ResumeLayout(false);
		modelEditStrip.PerformLayout();
		modelEngPanel.ResumeLayout(false);
		modelEngStrip.ResumeLayout(false);
		modelEngStrip.PerformLayout();
		modelShapePanel.ResumeLayout(false);
		modelShapeStrip.ResumeLayout(false);
		modelShapeStrip.PerformLayout();
		modelDatumPanel.ResumeLayout(false);
		modelDatumStrip.ResumeLayout(false);
		modelDatumStrip.PerformLayout();
		modelSetupPanel.ResumeLayout(false);
		modelSetupStrip.ResumeLayout(false);
		modelSetupStrip.PerformLayout();
		sketchTab.ResumeLayout(false);
		sketchClosePanel.ResumeLayout(false);
		sketchCloseStrip.ResumeLayout(false);
		sketchCloseStrip.PerformLayout();
		sketchUtilPanel.ResumeLayout(false);
		sketchUtilStrip.ResumeLayout(false);
		sketchUtilStrip.PerformLayout();
		sketchDimPanel.ResumeLayout(false);
		sketchDimStrip.ResumeLayout(false);
		sketchDimStrip.PerformLayout();
		sketchConstrainPanel.ResumeLayout(false);
		sketchConstrainStrip.ResumeLayout(false);
		sketchConstrainStrip.PerformLayout();
		sketchEditPanel.ResumeLayout(false);
		sketchEditStrip.ResumeLayout(false);
		sketchEditStrip.PerformLayout();
		sketchingPanel.ResumeLayout(false);
		sketchingStrip.ResumeLayout(false);
		sketchingStrip.PerformLayout();
		sketchDatumPanel.ResumeLayout(false);
		sketchDatumStrip.ResumeLayout(false);
		sketchDatumStrip.PerformLayout();
		sketchSetupPanel.ResumeLayout(false);
		sketchSetupStrip.ResumeLayout(false);
		sketchSetupStrip.PerformLayout();
		viewTools.ResumeLayout(false);
		viewTools.PerformLayout();
		mainSplit.Panel1.ResumeLayout(false);
		mainSplit.Panel2.ResumeLayout(false);
		mainSplit.Panel2.PerformLayout();
		((System.ComponentModel.ISupportInitialize)mainSplit).EndInit();
		mainSplit.ResumeLayout(false);
		statusStrip.ResumeLayout(false);
		statusStrip.PerformLayout();
		ResumeLayout(false);
		PerformLayout();
	}

	#endregion
}
