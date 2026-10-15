using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Parametric3D.App.Views;

// Поля именованных элементов разметки (генератор Avalonia для x:Name требует Roslyn 4.14 — в .NET 8 SDK его нет).
public partial class MainWindow
{
    private StackPanel FilePanel = null!;
    private TextBlock DocTitle = null!;
    private Border EnvPill = null!;
    private TextBlock EnvText = null!;
    private StackPanel EditPanel = null!;
    private Button SearchButton = null!;
    private StackPanel RightPanel = null!;
    private Grid WorkArea = null!;
    private Border TreePanel = null!;
    private StackPanel TreeTools = null!;
    private TreeView Tree = null!;
    private GridSplitter TreeSplitter = null!;
    private Grid ViewArea = null!;
    private Viewport.ModelViewport View = null!;
    private StackPanel ViewTools = null!;
    private Viewport.ViewCube Cube = null!;
    private Border HintBox = null!;
    private TextBlock HintText = null!;
    private Border Toast = null!;
    private TextBlock ToastText = null!;
    private Border PropsPanel = null!;
    private TextBlock PropsTitle = null!;
    private Button PropsClose = null!;
    private StackPanel PropsBody = null!;
    private TextBlock StatusMode = null!;
    private TextBlock StatusSel = null!;
    private TextBlock StatusBuild = null!;
    private TextBlock StatusUnits = null!;
    private TextBlock StatusGl = null!;
    private Border PaletteShade = null!;
    private Border PaletteBox = null!;
    private TextBox PaletteInput = null!;
    private ListBox PaletteList = null!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        FilePanel = this.FindControl<StackPanel>("FilePanel")!;
        DocTitle = this.FindControl<TextBlock>("DocTitle")!;
        EnvPill = this.FindControl<Border>("EnvPill")!;
        EnvText = this.FindControl<TextBlock>("EnvText")!;
        EditPanel = this.FindControl<StackPanel>("EditPanel")!;
        SearchButton = this.FindControl<Button>("SearchButton")!;
        RightPanel = this.FindControl<StackPanel>("RightPanel")!;
        WorkArea = this.FindControl<Grid>("WorkArea")!;
        TreePanel = this.FindControl<Border>("TreePanel")!;
        TreeTools = this.FindControl<StackPanel>("TreeTools")!;
        Tree = this.FindControl<TreeView>("Tree")!;
        TreeSplitter = this.FindControl<GridSplitter>("TreeSplitter")!;
        ViewArea = this.FindControl<Grid>("ViewArea")!;
        View = this.FindControl<Viewport.ModelViewport>("View")!;
        ViewTools = this.FindControl<StackPanel>("ViewTools")!;
        Cube = this.FindControl<Viewport.ViewCube>("Cube")!;
        HintBox = this.FindControl<Border>("HintBox")!;
        HintText = this.FindControl<TextBlock>("HintText")!;
        Toast = this.FindControl<Border>("Toast")!;
        ToastText = this.FindControl<TextBlock>("ToastText")!;
        PropsPanel = this.FindControl<Border>("PropsPanel")!;
        PropsTitle = this.FindControl<TextBlock>("PropsTitle")!;
        PropsClose = this.FindControl<Button>("PropsClose")!;
        PropsBody = this.FindControl<StackPanel>("PropsBody")!;
        StatusMode = this.FindControl<TextBlock>("StatusMode")!;
        StatusSel = this.FindControl<TextBlock>("StatusSel")!;
        StatusBuild = this.FindControl<TextBlock>("StatusBuild")!;
        StatusUnits = this.FindControl<TextBlock>("StatusUnits")!;
        StatusGl = this.FindControl<TextBlock>("StatusGl")!;
        PaletteShade = this.FindControl<Border>("PaletteShade")!;
        PaletteBox = this.FindControl<Border>("PaletteBox")!;
        PaletteInput = this.FindControl<TextBox>("PaletteInput")!;
        PaletteList = this.FindControl<ListBox>("PaletteList")!;
    }
}
