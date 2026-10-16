using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Parametric3D.App.Views;

// Создано tools/gen_avalonia_names.py из MainWindow.axaml — не править вручную.
public partial class MainWindow
{
    private StackPanel FilePanel = null!;
    private TextBlock DocTitle = null!;
    private Border EnvPill = null!;
    private TextBlock EnvText = null!;
    private StackPanel EditPanel = null!;
    private Button SearchButton = null!;
    private TextBlock SearchText = null!;
    private StackPanel RightPanel = null!;
    private Grid WorkArea = null!;
    private Border TreePanel = null!;
    private TextBlock TreeHeader = null!;
    private StackPanel TreeTools = null!;
    private TreeView Tree = null!;
    private GridSplitter TreeSplitter = null!;
    private Grid ViewArea = null!;
    private Parametric3D.App.Viewport.ModelViewport View = null!;
    private Border CommandBar = null!;
    private StackPanel CommandStack = null!;
    private StackPanel ViewTools = null!;
    private Parametric3D.App.Viewport.ViewCube Cube = null!;
    private Border ContextBar = null!;
    private Border SelChip = null!;
    private TextBlock SelChipText = null!;
    private StackPanel ContextOps = null!;
    private Border HintBox = null!;
    private TextBlock HintText = null!;
    private Border Toast = null!;
    private TextBlock ToastText = null!;
    private Border PropsPanel = null!;
    private TextBlock PropsTitle = null!;
    private StackPanel PropsBody = null!;
    private StackPanel PropsButtons = null!;
    private TextBlock StatusMode = null!;
    private TextBlock StatusSel = null!;
    private TextBlock StatusBuild = null!;
    private TextBlock StatusUnits = null!;
    private TextBlock StatusGl = null!;
    private Border PaletteShade = null!;
    private Border PaletteBox = null!;
    private TextBox PaletteInput = null!;
    private ListBox PaletteList = null!;
    private TextBlock PaletteHelp = null!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        FilePanel = this.FindControl<StackPanel>("FilePanel")!;
        DocTitle = this.FindControl<TextBlock>("DocTitle")!;
        EnvPill = this.FindControl<Border>("EnvPill")!;
        EnvText = this.FindControl<TextBlock>("EnvText")!;
        EditPanel = this.FindControl<StackPanel>("EditPanel")!;
        SearchButton = this.FindControl<Button>("SearchButton")!;
        SearchText = this.FindControl<TextBlock>("SearchText")!;
        RightPanel = this.FindControl<StackPanel>("RightPanel")!;
        WorkArea = this.FindControl<Grid>("WorkArea")!;
        TreePanel = this.FindControl<Border>("TreePanel")!;
        TreeHeader = this.FindControl<TextBlock>("TreeHeader")!;
        TreeTools = this.FindControl<StackPanel>("TreeTools")!;
        Tree = this.FindControl<TreeView>("Tree")!;
        TreeSplitter = this.FindControl<GridSplitter>("TreeSplitter")!;
        ViewArea = this.FindControl<Grid>("ViewArea")!;
        View = this.FindControl<Parametric3D.App.Viewport.ModelViewport>("View")!;
        CommandBar = this.FindControl<Border>("CommandBar")!;
        CommandStack = this.FindControl<StackPanel>("CommandStack")!;
        ViewTools = this.FindControl<StackPanel>("ViewTools")!;
        Cube = this.FindControl<Parametric3D.App.Viewport.ViewCube>("Cube")!;
        ContextBar = this.FindControl<Border>("ContextBar")!;
        SelChip = this.FindControl<Border>("SelChip")!;
        SelChipText = this.FindControl<TextBlock>("SelChipText")!;
        ContextOps = this.FindControl<StackPanel>("ContextOps")!;
        HintBox = this.FindControl<Border>("HintBox")!;
        HintText = this.FindControl<TextBlock>("HintText")!;
        Toast = this.FindControl<Border>("Toast")!;
        ToastText = this.FindControl<TextBlock>("ToastText")!;
        PropsPanel = this.FindControl<Border>("PropsPanel")!;
        PropsTitle = this.FindControl<TextBlock>("PropsTitle")!;
        PropsBody = this.FindControl<StackPanel>("PropsBody")!;
        PropsButtons = this.FindControl<StackPanel>("PropsButtons")!;
        StatusMode = this.FindControl<TextBlock>("StatusMode")!;
        StatusSel = this.FindControl<TextBlock>("StatusSel")!;
        StatusBuild = this.FindControl<TextBlock>("StatusBuild")!;
        StatusUnits = this.FindControl<TextBlock>("StatusUnits")!;
        StatusGl = this.FindControl<TextBlock>("StatusGl")!;
        PaletteShade = this.FindControl<Border>("PaletteShade")!;
        PaletteBox = this.FindControl<Border>("PaletteBox")!;
        PaletteInput = this.FindControl<TextBox>("PaletteInput")!;
        PaletteList = this.FindControl<ListBox>("PaletteList")!;
        PaletteHelp = this.FindControl<TextBlock>("PaletteHelp")!;
    }
}
