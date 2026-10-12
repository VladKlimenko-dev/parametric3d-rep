namespace Parametric3D.UI;

/// <summary>
/// Разметка окна настроек эскиза.
///
/// Файл предназначен для конструктора форм: элементы — обычные поля,
/// свойства присваиваются по одному, обработчики привязаны к именованным
/// методам из SketchSetupForm.cs. Вызовов вспомогательных методов здесь
/// нет — конструктор их не разбирает и при сохранении выбрасывает.
/// </summary>
partial class SketchSetupForm
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
    private FlowLayoutPanel planeButtons;
    private FlowLayoutPanel buttons;
    private Label nameLabel;
    private TextBox nameBox;

    private Label planeLabel;
    private Label planeValue;
    private Button planeSelectButton;
    private Button flipButton;

    private Label originLabel;
    private Label originValue;
    private Button originSelectButton;

    private Label upLabel;
    private FlowLayoutPanel upPanel;
    private NumericUpDown upXBox;
    private NumericUpDown upYBox;
    private CheckBox snapCheck;
    private Label stepLabel;
    private TextBox stepBox;

    private Button okButton;
    private Button cancelButton;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        root = new TableLayoutPanel();
        grid = new TableLayoutPanel();
        nameLabel = new Label();
        nameBox = new TextBox();
        planeLabel = new Label();
        planeValue = new Label();
        planeButtons = new FlowLayoutPanel();
        planeSelectButton = new Button();
        flipButton = new Button();
        originLabel = new Label();
        originValue = new Label();
        originSelectButton = new Button();
        upLabel = new Label();
        upPanel = new FlowLayoutPanel();
        upXBox = new NumericUpDown();
        upYBox = new NumericUpDown();
        snapCheck = new CheckBox();
        stepLabel = new Label();
        stepBox = new TextBox();
        buttons = new FlowLayoutPanel();
        okButton = new Button();
        cancelButton = new Button();

        upPanel.SuspendLayout();
        root.SuspendLayout();
        grid.SuspendLayout();
        planeButtons.SuspendLayout();
        buttons.SuspendLayout();
        SuspendLayout();

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

        grid.AutoSize = true;
        grid.ColumnCount = 3;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Dock = DockStyle.Fill;
        grid.Name = "grid";
        grid.Padding = new Padding(12, 12, 12, 0);
        grid.RowCount = 7;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        grid.TabIndex = 0;

        grid.Controls.Add(nameLabel, 0, 0);
        grid.Controls.Add(nameBox, 1, 0);
        grid.Controls.Add(planeLabel, 0, 1);
        grid.Controls.Add(planeValue, 1, 1);
        grid.Controls.Add(planeButtons, 2, 1);
        grid.Controls.Add(originLabel, 0, 2);
        grid.Controls.Add(originValue, 1, 2);
        grid.Controls.Add(originSelectButton, 2, 2);
        grid.Controls.Add(upLabel, 0, 3);
        grid.Controls.Add(upPanel, 1, 3);
        grid.Controls.Add(snapCheck, 1, 4);
        grid.Controls.Add(stepLabel, 0, 5);
        grid.Controls.Add(stepBox, 1, 5);

        nameLabel.Anchor = AnchorStyles.Left;
        nameLabel.AutoSize = true;
        nameLabel.Margin = new Padding(3, 6, 8, 6);
        nameLabel.Name = "nameLabel";
        nameLabel.Text = "Name:";

        nameBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        nameBox.Margin = new Padding(3, 4, 3, 4);
        nameBox.Name = "nameBox";
        nameBox.TabIndex = 0;

        planeLabel.Anchor = AnchorStyles.Left;
        planeLabel.AutoSize = true;
        planeLabel.Margin = new Padding(3, 6, 8, 6);
        planeLabel.Name = "planeLabel";
        planeLabel.Text = "Base plane:";

        planeValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        planeValue.AutoEllipsis = true;
        planeValue.AutoSize = false;
        planeValue.BorderStyle = BorderStyle.FixedSingle;
        planeValue.Height = 23;
        planeValue.Margin = new Padding(3, 4, 3, 4);
        planeValue.Name = "planeValue";
        planeValue.TextAlign = ContentAlignment.MiddleLeft;

        planeButtons.AutoSize = true;
        planeButtons.WrapContents = false;
        planeButtons.Margin = new Padding(5, 0, 0, 0);
        planeButtons.Name = "planeButtons";
        planeButtons.TabIndex = 1;
        planeButtons.Controls.Add(planeSelectButton);
        planeButtons.Controls.Add(flipButton);

        planeSelectButton.AutoSize = true;
        planeSelectButton.Name = "planeSelectButton";
        planeSelectButton.TabIndex = 0;
        planeSelectButton.Text = "Select";
        planeSelectButton.Click += planeSelectButton_Click;

        flipButton.AutoSize = true;
        flipButton.Name = "flipButton";
        flipButton.TabIndex = 1;
        flipButton.Text = "Flip";
        flipButton.Click += flipButton_Click;

        originLabel.Anchor = AnchorStyles.Left;
        originLabel.AutoSize = true;
        originLabel.Margin = new Padding(3, 6, 8, 6);
        originLabel.Name = "originLabel";
        originLabel.Text = "Origin:";

        originValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        originValue.AutoEllipsis = true;
        originValue.AutoSize = false;
        originValue.BorderStyle = BorderStyle.FixedSingle;
        originValue.Height = 23;
        originValue.Margin = new Padding(3, 4, 3, 4);
        originValue.Name = "originValue";
        originValue.Text = "Geometric center";
        originValue.TextAlign = ContentAlignment.MiddleLeft;

        originSelectButton.Anchor = AnchorStyles.Left;
        originSelectButton.AutoSize = true;
        originSelectButton.Name = "originSelectButton";
        originSelectButton.TabIndex = 2;
        originSelectButton.Text = "Select Origin";
        originSelectButton.Click += originSelectButton_Click;

        upLabel.Anchor = AnchorStyles.Left;
        upLabel.AutoSize = true;
        upLabel.Margin = new Padding(3, 6, 8, 6);
        upLabel.Name = "upLabel";
        upLabel.Text = "Vector up:";

        upPanel.AutoSize = true;
        upPanel.Margin = new Padding(0, 2, 0, 2);
        upPanel.Name = "upPanel";
        upPanel.TabIndex = 5;
        upPanel.WrapContents = false;
        upPanel.Controls.Add(upXBox);
        upPanel.Controls.Add(upYBox);

        upXBox.DecimalPlaces = 3;
        upXBox.Increment = new decimal(new int[] { 1, 0, 0, 65536 });
        upXBox.Maximum = new decimal(new int[] { 1000, 0, 0, 0 });
        upXBox.Minimum = new decimal(new int[] { 1000, 0, 0, -2147483648 });
        upXBox.Name = "upXBox";
        upXBox.Size = new Size(88, 23);
        upXBox.TabIndex = 0;

        upYBox.DecimalPlaces = 3;
        upYBox.Increment = new decimal(new int[] { 1, 0, 0, 65536 });
        upYBox.Maximum = new decimal(new int[] { 1000, 0, 0, 0 });
        upYBox.Minimum = new decimal(new int[] { 1000, 0, 0, -2147483648 });
        upYBox.Name = "upYBox";
        upYBox.Size = new Size(88, 23);
        upYBox.TabIndex = 1;
        upYBox.Value = new decimal(new int[] { 1, 0, 0, 0 });

        snapCheck.Anchor = AnchorStyles.Left;
        snapCheck.AutoSize = true;
        snapCheck.Name = "snapCheck";
        snapCheck.TabIndex = 3;
        snapCheck.Text = "Snap to grid";

        stepLabel.Anchor = AnchorStyles.Left;
        stepLabel.AutoSize = true;
        stepLabel.Margin = new Padding(3, 6, 8, 6);
        stepLabel.Name = "stepLabel";
        stepLabel.Text = "Grid step:";

        stepBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        stepBox.Margin = new Padding(3, 4, 3, 4);
        stepBox.Name = "stepBox";
        stepBox.TabIndex = 4;

        buttons.AutoSize = true;
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Name = "buttons";
        buttons.Padding = new Padding(12, 8, 12, 12);
        buttons.TabIndex = 1;
        buttons.WrapContents = false;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        okButton.Size = new Size(96, 28);
        okButton.Name = "okButton";
        okButton.TabIndex = 0;
        okButton.Text = "OK";
        okButton.Click += okButton_Click;

        cancelButton.Size = new Size(96, 28);
        cancelButton.Name = "cancelButton";
        cancelButton.TabIndex = 1;
        cancelButton.Text = "Cancel";
        cancelButton.Click += cancelButton_Click;

        AcceptButton = okButton;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        CancelButton = cancelButton;
        ClientSize = new Size(480, 300);
        Controls.Add(root);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "SketchSetupForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Sketch settings";
        upPanel.ResumeLayout(false);
        upPanel.PerformLayout();
        root.ResumeLayout(false);
        root.PerformLayout();
        grid.ResumeLayout(false);
        grid.PerformLayout();
        planeButtons.ResumeLayout(false);
        planeButtons.PerformLayout();
        buttons.ResumeLayout(false);
        buttons.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
