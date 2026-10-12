namespace Parametric3D.UI;

/// <summary>
/// Разметка окна ссылочной геометрии.
///
/// Устройство то же, что у остальных форм: корневой TableLayoutPanel,
/// строка содержимого и строка кнопок. Абсолютные координаты при
/// фиксированном ClientSize верны лишь при том шрифте, при котором их
/// расставили, а AutoSizeMode = GrowAndShrink у формы сплющивает её
/// в полоску — оба способа уже проверены на практике и оба негодны.
/// </summary>
partial class SketchReferencesForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private TableLayoutPanel root;
    private ListView list;
    private ColumnHeader colKind;
    private ColumnHeader colSource;
    private ColumnHeader colId;
    private Label hintLabel;
    private FlowLayoutPanel buttons;
    private Button restoreButton;
    private Button deleteButton;
    private Button closeButton;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        root = new TableLayoutPanel();
        list = new ListView();
        colKind = new ColumnHeader();
        colSource = new ColumnHeader();
        colId = new ColumnHeader();
        hintLabel = new Label();
        buttons = new FlowLayoutPanel();
        restoreButton = new Button();
        deleteButton = new Button();
        closeButton = new Button();

        root.SuspendLayout();
        buttons.SuspendLayout();
        SuspendLayout();

        // -------------------------------------------------------- корень
        root.ColumnCount = 1;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.Dock = DockStyle.Fill;
        root.Location = new Point(0, 0);
        root.Name = "root";
        root.Padding = new Padding(12, 12, 12, 0);
        root.RowCount = 3;
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.TabIndex = 0;
        root.Controls.Add(list, 0, 0);
        root.Controls.Add(hintLabel, 0, 1);
        root.Controls.Add(buttons, 0, 2);

        // --------------------------------------------------------- список
        colKind.Text = "Type";
        colKind.Width = 110;

        colSource.Text = "Source";
        colSource.Width = 250;

        colId.Text = "ID";
        colId.Width = 60;

        list.Columns.AddRange(new ColumnHeader[] { colKind, colSource, colId });
        list.Dock = DockStyle.Fill;
        list.FullRowSelect = true;
        list.GridLines = true;
        list.HideSelection = false;
        list.Margin = new Padding(0, 0, 0, 6);
        list.MultiSelect = true;
        list.Name = "list";
        list.TabIndex = 0;
        list.View = View.Details;
        list.SelectedIndexChanged += list_SelectedIndexChanged;

        hintLabel.AutoSize = true;
        hintLabel.ForeColor = SystemColors.GrayText;
        hintLabel.Margin = new Padding(0, 0, 0, 4);
        hintLabel.Name = "hintLabel";
        hintLabel.Text = "Projected geometry used as reference.";

        // --------------------------------------------------------- кнопки
        buttons.AutoSize = true;
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Margin = new Padding(0);
        buttons.Name = "buttons";
        buttons.Padding = new Padding(0, 4, 0, 12);
        buttons.TabIndex = 1;
        buttons.WrapContents = false;
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(deleteButton);
        buttons.Controls.Add(restoreButton);

        closeButton.Name = "closeButton";
        closeButton.Size = new Size(96, 28);
        closeButton.TabIndex = 0;
        closeButton.Text = "Close";
        closeButton.Click += closeButton_Click;

        deleteButton.Enabled = false;
        deleteButton.Name = "deleteButton";
        deleteButton.Size = new Size(96, 28);
        deleteButton.TabIndex = 1;
        deleteButton.Text = "Delete";
        deleteButton.Click += deleteButton_Click;

        restoreButton.Enabled = false;
        restoreButton.Name = "restoreButton";
        restoreButton.Size = new Size(120, 28);
        restoreButton.TabIndex = 2;
        restoreButton.Text = "Restore basis";
        restoreButton.Click += restoreButton_Click;

        // --------------------------------------------- SketchReferencesForm
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 400);
        Controls.Add(root);
        MinimumSize = new Size(480, 340);
        Name = "SketchReferencesForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Sketch references";
        root.ResumeLayout(false);
        root.PerformLayout();
        buttons.ResumeLayout(false);
        buttons.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
