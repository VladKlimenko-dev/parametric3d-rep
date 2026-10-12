namespace Parametric3D.UI;

/// <summary>
/// Разметка окна вращения.
///
/// РАЗМЕТКА, А НЕ КООРДИНАТЫ. Абсолютные координаты при фиксированном
/// ClientSize верны только при том шрифте, при котором их расставили:
/// крупнее шрифт — подписи растут, кнопки остаются на месте, правый край
/// уезжает за границу окна.
///
/// AutoSizeMode у формы — ТОЛЬКО GrowOnly. С GrowAndShrink форма сжимается
/// до минимума, нужного пристыкованным контейнерам, а ширину они не задают,
/// и окно сплющивается в полоску.
///
/// WrapContents = false у полосы кнопок обязателен: иначе при нехватке
/// ширины кнопки переносятся в столбик.
/// </summary>
partial class RevolveForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private TableLayoutPanel root;
    private TableLayoutPanel grid;
    private Label sketchLabel;
    private ComboBox sketchCombo;
    private Button newSketchButton;
    private CheckBox cutCheck;
    private Label axisLabel;
    private Label axisValue;
    private Button axisSelectButton;
    private Label startLabel;
    private TextBox startBox;
    private Label endLabel;
    private TextBox endBox;
    private FlowLayoutPanel buttons;
    private Button okButton;
    private Button cancelButton;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        root = new TableLayoutPanel();
        grid = new TableLayoutPanel();
        sketchLabel = new Label();
        sketchCombo = new ComboBox();
        newSketchButton = new Button();
        cutCheck = new CheckBox();
        axisLabel = new Label();
        axisValue = new Label();
        axisSelectButton = new Button();
        startLabel = new Label();
        startBox = new TextBox();
        endLabel = new Label();
        endBox = new TextBox();
        buttons = new FlowLayoutPanel();
        okButton = new Button();
        cancelButton = new Button();

        root.SuspendLayout();
        grid.SuspendLayout();
        buttons.SuspendLayout();
        SuspendLayout();

        // -------------------------------------------------------- корень
        root.ColumnCount = 1;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.Dock = DockStyle.Fill;
        root.Location = new Point(0, 0);
        root.Name = "root";
        root.RowCount = 2;
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.TabIndex = 0;
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        // ------------------------------------------------------ содержимое
        grid.AutoSize = true;
        grid.ColumnCount = 3;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Dock = DockStyle.Fill;
        grid.Name = "grid";
        grid.Padding = new Padding(12, 12, 12, 0);
        grid.RowCount = 6;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        grid.TabIndex = 0;

        grid.Controls.Add(sketchLabel, 0, 0);
        grid.Controls.Add(sketchCombo, 1, 0);
        grid.Controls.Add(newSketchButton, 2, 0);
        grid.Controls.Add(cutCheck, 1, 1);
        grid.Controls.Add(axisLabel, 0, 2);
        grid.Controls.Add(axisValue, 1, 2);
        grid.Controls.Add(axisSelectButton, 2, 2);
        grid.Controls.Add(startLabel, 0, 3);
        grid.Controls.Add(startBox, 1, 3);
        grid.Controls.Add(endLabel, 0, 4);
        grid.Controls.Add(endBox, 1, 4);

        sketchLabel.Anchor = AnchorStyles.Left;
        sketchLabel.AutoSize = true;
        sketchLabel.Margin = new Padding(3, 6, 8, 6);
        sketchLabel.Name = "sketchLabel";
        sketchLabel.Text = "Sketch:";

        sketchCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        sketchCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        sketchCombo.Margin = new Padding(3, 4, 3, 4);
        sketchCombo.Name = "sketchCombo";
        sketchCombo.TabIndex = 0;

        newSketchButton.Anchor = AnchorStyles.Left;
        newSketchButton.AutoSize = true;
        newSketchButton.Margin = new Padding(8, 3, 3, 3);
        newSketchButton.Name = "newSketchButton";
        newSketchButton.TabIndex = 1;
        newSketchButton.Text = "New sketch";
        newSketchButton.Click += newSketchButton_Click;

        cutCheck.Anchor = AnchorStyles.Left;
        cutCheck.AutoSize = true;
        cutCheck.Margin = new Padding(3, 6, 3, 6);
        cutCheck.Name = "cutCheck";
        cutCheck.TabIndex = 2;
        cutCheck.Text = "Remove material (cut)";

        axisLabel.Anchor = AnchorStyles.Left;
        axisLabel.AutoSize = true;
        axisLabel.Margin = new Padding(3, 6, 8, 6);
        axisLabel.Name = "axisLabel";
        axisLabel.Text = "Axis:";

        axisValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        axisValue.AutoEllipsis = true;
        axisValue.AutoSize = false;
        axisValue.BorderStyle = BorderStyle.FixedSingle;
        axisValue.Height = 23;
        axisValue.Margin = new Padding(3, 4, 3, 4);
        axisValue.Name = "axisValue";
        axisValue.TextAlign = ContentAlignment.MiddleLeft;

        axisSelectButton.Anchor = AnchorStyles.Left;
        axisSelectButton.AutoSize = true;
        axisSelectButton.Margin = new Padding(8, 3, 3, 3);
        axisSelectButton.Name = "axisSelectButton";
        axisSelectButton.TabIndex = 3;
        axisSelectButton.Text = "Select";
        axisSelectButton.Click += axisSelectButton_Click;

        startLabel.Anchor = AnchorStyles.Left;
        startLabel.AutoSize = true;
        startLabel.Margin = new Padding(3, 6, 8, 6);
        startLabel.Name = "startLabel";
        startLabel.Text = "Start angle, °:";

        startBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        startBox.Margin = new Padding(3, 4, 3, 4);
        startBox.Name = "startBox";
        startBox.TabIndex = 4;

        endLabel.Anchor = AnchorStyles.Left;
        endLabel.AutoSize = true;
        endLabel.Margin = new Padding(3, 6, 8, 6);
        endLabel.Name = "endLabel";
        endLabel.Text = "End angle, °:";

        endBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        endBox.Margin = new Padding(3, 4, 3, 4);
        endBox.Name = "endBox";
        endBox.TabIndex = 5;

        // ---------------------------------------------------------- кнопки
        buttons.AutoSize = true;
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Name = "buttons";
        buttons.Padding = new Padding(12, 8, 12, 12);
        buttons.TabIndex = 1;
        buttons.WrapContents = false;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        okButton.DialogResult = DialogResult.OK;
        okButton.Name = "okButton";
        okButton.Size = new Size(96, 28);
        okButton.TabIndex = 0;
        okButton.Text = "OK";
        okButton.Click += okButton_Click;

        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Name = "cancelButton";
        cancelButton.Size = new Size(96, 28);
        cancelButton.TabIndex = 1;
        cancelButton.Text = "Cancel";

        // ----------------------------------------------------- RevolveForm
        AcceptButton = okButton;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        CancelButton = cancelButton;
        ClientSize = new Size(460, 250);
        Controls.Add(root);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "RevolveForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Revolve";
        root.ResumeLayout(false);
        root.PerformLayout();
        grid.ResumeLayout(false);
        grid.PerformLayout();
        buttons.ResumeLayout(false);
        buttons.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
