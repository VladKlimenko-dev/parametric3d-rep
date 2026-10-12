namespace Parametric3D.UI;

/// <summary>
/// Разметка окна выдавливания.
///
/// Элементы живут в раскладочных контейнерах, окно подбирает размер под
/// содержимое: абсолютные координаты при фиксированном ClientSize верны
/// лишь при том шрифте, при котором их расставили, и на другом шрифте
/// кнопки уезжают за край.
/// </summary>
partial class ExtrudeForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private TableLayoutPanel root;
    private TableLayoutPanel headGrid;
    private Label sketchLabel;
    private ComboBox sketchCombo;
    private Button newSketchButton;
    private CheckBox cutCheck;
    private FlowLayoutPanel sideFlags;
    private CheckBox twoSidedCheck;
    private CheckBox symmetricCheck;

    private GroupBox side1Group;
    private TableLayoutPanel side1Grid;
    private Label end1Label;
    private ComboBox end1Combo;
    private Label depth1Label;
    private TextBox depth1Box;
    private CheckBox flip1Check;

    private GroupBox side2Group;
    private TableLayoutPanel side2Grid;
    private Label end2Label;
    private ComboBox end2Combo;
    private Label depth2Label;
    private TextBox depth2Box;
    private CheckBox flip2Check;

    private FlowLayoutPanel buttons;
    private Button okButton;
    private Button cancelButton;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        root = new TableLayoutPanel();
        headGrid = new TableLayoutPanel();
        sketchLabel = new Label();
        sketchCombo = new ComboBox();
        newSketchButton = new Button();
        cutCheck = new CheckBox();
        sideFlags = new FlowLayoutPanel();
        twoSidedCheck = new CheckBox();
        symmetricCheck = new CheckBox();

        side1Group = new GroupBox();
        side1Grid = new TableLayoutPanel();
        end1Label = new Label();
        end1Combo = new ComboBox();
        depth1Label = new Label();
        depth1Box = new TextBox();
        flip1Check = new CheckBox();

        side2Group = new GroupBox();
        side2Grid = new TableLayoutPanel();
        end2Label = new Label();
        end2Combo = new ComboBox();
        depth2Label = new Label();
        depth2Box = new TextBox();
        flip2Check = new CheckBox();

        buttons = new FlowLayoutPanel();
        okButton = new Button();
        cancelButton = new Button();

        root.SuspendLayout();
        headGrid.SuspendLayout();
        sideFlags.SuspendLayout();
        side1Group.SuspendLayout();
        side1Grid.SuspendLayout();
        side2Group.SuspendLayout();
        side2Grid.SuspendLayout();
        buttons.SuspendLayout();
        SuspendLayout();

        // -------------------------------------------------------- корень
        root.ColumnCount = 1;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.Dock = DockStyle.Fill;
        root.Location = new Point(0, 0);
        root.Name = "root";
        root.RowCount = 4;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.TabIndex = 0;
        root.Controls.Add(headGrid, 0, 0);
        root.Controls.Add(side1Group, 0, 1);
        root.Controls.Add(side2Group, 0, 2);
        root.Controls.Add(buttons, 0, 3);

        // -------------------------------------------------------- заголовок
        headGrid.AutoSize = true;
        headGrid.ColumnCount = 3;
        headGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        headGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headGrid.Dock = DockStyle.Fill;
        headGrid.Name = "headGrid";
        headGrid.Padding = new Padding(12, 12, 12, 0);
        headGrid.RowCount = 3;
        headGrid.TabIndex = 0;

        headGrid.Controls.Add(sketchLabel, 0, 0);
        headGrid.Controls.Add(sketchCombo, 1, 0);
        headGrid.Controls.Add(newSketchButton, 2, 0);
        headGrid.Controls.Add(cutCheck, 1, 1);
        headGrid.Controls.Add(sideFlags, 1, 2);

        sketchLabel.Anchor = AnchorStyles.Left;
        sketchLabel.AutoSize = true;
        sketchLabel.Name = "sketchLabel";
        sketchLabel.Text = "Sketch:";

        sketchCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        sketchCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        sketchCombo.Name = "sketchCombo";
        sketchCombo.TabIndex = 0;

        newSketchButton.Anchor = AnchorStyles.Left;
        newSketchButton.AutoSize = true;
        newSketchButton.Name = "newSketchButton";
        newSketchButton.TabIndex = 1;
        newSketchButton.Text = "New sketch";
        newSketchButton.Click += newSketchButton_Click;

        cutCheck.Anchor = AnchorStyles.Left;
        cutCheck.AutoSize = true;
        cutCheck.Name = "cutCheck";
        cutCheck.TabIndex = 2;
        cutCheck.Text = "Remove material (cut)";

        sideFlags.AutoSize = true;
        sideFlags.WrapContents = false;
        sideFlags.Margin = new Padding(0);
        sideFlags.Name = "sideFlags";
        sideFlags.TabIndex = 3;
        sideFlags.Controls.Add(twoSidedCheck);
        sideFlags.Controls.Add(symmetricCheck);

        twoSidedCheck.AutoSize = true;
        twoSidedCheck.Name = "twoSidedCheck";
        twoSidedCheck.TabIndex = 0;
        twoSidedCheck.Text = "Two sides";
        twoSidedCheck.CheckedChanged += sidesChanged;

        symmetricCheck.AutoSize = true;
        symmetricCheck.Name = "symmetricCheck";
        symmetricCheck.TabIndex = 1;
        symmetricCheck.Text = "Symmetric";
        symmetricCheck.CheckedChanged += sidesChanged;

        // ---------------------------------------------------- первая сторона
        side1Group.AutoSize = true;
        side1Group.Controls.Add(side1Grid);
        side1Group.Dock = DockStyle.Fill;
        side1Group.Margin = new Padding(12, 6, 12, 0);
        side1Group.Name = "side1Group";
        side1Group.Padding = new Padding(8);
        side1Group.TabIndex = 1;
        side1Group.TabStop = false;
        side1Group.Text = "Side 1";

        side1Grid.AutoSize = true;
        side1Grid.ColumnCount = 2;
        side1Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        side1Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        side1Grid.Dock = DockStyle.Fill;
        side1Grid.Name = "side1Grid";
        side1Grid.RowCount = 3;
        side1Grid.TabIndex = 0;

        side1Grid.Controls.Add(end1Label, 0, 0);
        side1Grid.Controls.Add(end1Combo, 1, 0);
        side1Grid.Controls.Add(depth1Label, 0, 1);
        side1Grid.Controls.Add(depth1Box, 1, 1);
        side1Grid.Controls.Add(flip1Check, 1, 2);

        end1Label.Anchor = AnchorStyles.Left;
        end1Label.AutoSize = true;
        end1Label.Name = "end1Label";
        end1Label.Text = "End condition:";

        end1Combo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        end1Combo.DropDownStyle = ComboBoxStyle.DropDownList;
        end1Combo.Name = "end1Combo";
        end1Combo.TabIndex = 0;

        depth1Label.Anchor = AnchorStyles.Left;
        depth1Label.AutoSize = true;
        depth1Label.Name = "depth1Label";
        depth1Label.Text = "Depth:";

        depth1Box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        depth1Box.Name = "depth1Box";
        depth1Box.TabIndex = 1;

        flip1Check.Anchor = AnchorStyles.Left;
        flip1Check.AutoSize = true;
        flip1Check.Name = "flip1Check";
        flip1Check.TabIndex = 2;
        flip1Check.Text = "Flip direction";

        // ---------------------------------------------------- вторая сторона
        side2Group.AutoSize = true;
        side2Group.Controls.Add(side2Grid);
        side2Group.Dock = DockStyle.Fill;
        side2Group.Margin = new Padding(12, 6, 12, 0);
        side2Group.Name = "side2Group";
        side2Group.Padding = new Padding(8);
        side2Group.TabIndex = 2;
        side2Group.TabStop = false;
        side2Group.Text = "Side 2";

        side2Grid.AutoSize = true;
        side2Grid.ColumnCount = 2;
        side2Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        side2Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        side2Grid.Dock = DockStyle.Fill;
        side2Grid.Name = "side2Grid";
        side2Grid.RowCount = 3;
        side2Grid.TabIndex = 0;

        side2Grid.Controls.Add(end2Label, 0, 0);
        side2Grid.Controls.Add(end2Combo, 1, 0);
        side2Grid.Controls.Add(depth2Label, 0, 1);
        side2Grid.Controls.Add(depth2Box, 1, 1);
        side2Grid.Controls.Add(flip2Check, 1, 2);

        end2Label.Anchor = AnchorStyles.Left;
        end2Label.AutoSize = true;
        end2Label.Name = "end2Label";
        end2Label.Text = "End condition:";

        end2Combo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        end2Combo.DropDownStyle = ComboBoxStyle.DropDownList;
        end2Combo.Name = "end2Combo";
        end2Combo.TabIndex = 0;

        depth2Label.Anchor = AnchorStyles.Left;
        depth2Label.AutoSize = true;
        depth2Label.Name = "depth2Label";
        depth2Label.Text = "Depth:";

        depth2Box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        depth2Box.Name = "depth2Box";
        depth2Box.TabIndex = 1;

        flip2Check.Anchor = AnchorStyles.Left;
        flip2Check.AutoSize = true;
        flip2Check.Name = "flip2Check";
        flip2Check.TabIndex = 2;
        flip2Check.Text = "Flip direction";

        // ------------------------------------------------------------ кнопки
        buttons.AutoSize = true;
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Name = "buttons";
        buttons.Padding = new Padding(12, 8, 12, 12);
        buttons.TabIndex = 3;
        buttons.WrapContents = false;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        okButton.DialogResult = DialogResult.OK;
        okButton.Size = new Size(96, 28);
        okButton.Name = "okButton";
        okButton.TabIndex = 0;
        okButton.Text = "OK";
        okButton.Click += okButton_Click;

        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Size = new Size(96, 28);
        cancelButton.Name = "cancelButton";
        cancelButton.TabIndex = 1;
        cancelButton.Text = "Cancel";

        // -------------------------------------------------------- ExtrudeForm
        //
        // Порядок добавления обратный порядку на экране: контейнеры
        // пристыкованы сверху и снизу, и добавленный последним оказывается
        // ближе к своему краю.
        AcceptButton = okButton;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        CancelButton = cancelButton;
        ClientSize = new Size(460, 440);
        Controls.Add(root);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "ExtrudeForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Extrude";
        root.ResumeLayout(false);
        root.PerformLayout();
        headGrid.ResumeLayout(false);
        headGrid.PerformLayout();
        sideFlags.ResumeLayout(false);
        sideFlags.PerformLayout();
        side1Group.ResumeLayout(false);
        side1Grid.ResumeLayout(false);
        side1Grid.PerformLayout();
        side2Group.ResumeLayout(false);
        side2Grid.ResumeLayout(false);
        side2Grid.PerformLayout();
        buttons.ResumeLayout(false);
        buttons.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
