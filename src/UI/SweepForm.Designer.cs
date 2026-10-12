namespace Parametric3D.UI;

/// <summary>
/// Разметка окна протягивания. Устройство то же, что у остальных форм
/// операции: корневой TableLayoutPanel, строка содержимого и строка кнопок,
/// у формы AutoSizeMode = GrowOnly.
/// </summary>
partial class SweepForm
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
    private Label sectionLabel;
    private ComboBox sectionCombo;
    private Button newSectionButton;
    private Label pathLabel;
    private ComboBox pathCombo;
    private Button pathSelectButton;
    private CheckBox cutCheck;
    private Label lengthLabel;
    private TextBox lengthBox;
    private FlowLayoutPanel buttons;
    private Button okButton;
    private Button cancelButton;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        root = new TableLayoutPanel();
        grid = new TableLayoutPanel();
        sectionLabel = new Label();
        sectionCombo = new ComboBox();
        newSectionButton = new Button();
        pathLabel = new Label();
        pathCombo = new ComboBox();
        pathSelectButton = new Button();
        cutCheck = new CheckBox();
        lengthLabel = new Label();
        lengthBox = new TextBox();
        buttons = new FlowLayoutPanel();
        okButton = new Button();
        cancelButton = new Button();

        root.SuspendLayout();
        grid.SuspendLayout();
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
        grid.RowCount = 5;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        grid.TabIndex = 0;

        grid.Controls.Add(sectionLabel, 0, 0);
        grid.Controls.Add(sectionCombo, 1, 0);
        grid.Controls.Add(newSectionButton, 2, 0);
        grid.Controls.Add(pathLabel, 0, 1);
        grid.Controls.Add(pathCombo, 1, 1);
        grid.Controls.Add(pathSelectButton, 2, 1);
        grid.Controls.Add(cutCheck, 1, 2);
        grid.Controls.Add(lengthLabel, 0, 3);
        grid.Controls.Add(lengthBox, 1, 3);

        sectionLabel.Anchor = AnchorStyles.Left;
        sectionLabel.AutoSize = true;
        sectionLabel.Margin = new Padding(3, 6, 8, 6);
        sectionLabel.Name = "sectionLabel";
        sectionLabel.Text = "Section:";

        sectionCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        sectionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        sectionCombo.Margin = new Padding(3, 4, 3, 4);
        sectionCombo.Name = "sectionCombo";
        sectionCombo.TabIndex = 0;

        newSectionButton.Anchor = AnchorStyles.Left;
        newSectionButton.AutoSize = true;
        newSectionButton.Margin = new Padding(8, 3, 3, 3);
        newSectionButton.Name = "newSectionButton";
        newSectionButton.TabIndex = 1;
        newSectionButton.Text = "New sketch";
        newSectionButton.Click += newSectionButton_Click;

        pathLabel.Anchor = AnchorStyles.Left;
        pathLabel.AutoSize = true;
        pathLabel.Margin = new Padding(3, 6, 8, 6);
        pathLabel.Name = "pathLabel";
        pathLabel.Text = "Path:";

        pathCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        pathCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        pathCombo.Margin = new Padding(3, 4, 3, 4);
        pathCombo.Name = "pathCombo";
        pathCombo.TabIndex = 2;

        pathSelectButton.Anchor = AnchorStyles.Left;
        pathSelectButton.AutoSize = true;
        pathSelectButton.Margin = new Padding(8, 3, 3, 3);
        pathSelectButton.Name = "pathSelectButton";
        pathSelectButton.TabIndex = 3;
        pathSelectButton.Text = "Select";
        pathSelectButton.Click += pathSelectButton_Click;

        cutCheck.Anchor = AnchorStyles.Left;
        cutCheck.AutoSize = true;
        cutCheck.Margin = new Padding(3, 6, 3, 6);
        cutCheck.Name = "cutCheck";
        cutCheck.TabIndex = 4;
        cutCheck.Text = "Remove material (cut)";

        lengthLabel.Anchor = AnchorStyles.Left;
        lengthLabel.AutoSize = true;
        lengthLabel.Margin = new Padding(3, 6, 8, 6);
        lengthLabel.Name = "lengthLabel";
        lengthLabel.Text = "Length fraction:";

        lengthBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        lengthBox.Margin = new Padding(3, 4, 3, 4);
        lengthBox.Name = "lengthBox";
        lengthBox.TabIndex = 5;

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

        AcceptButton = okButton;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        CancelButton = cancelButton;
        ClientSize = new Size(460, 220);
        Controls.Add(root);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "SweepForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Sweep";
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
