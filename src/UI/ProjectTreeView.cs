using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>
/// Дерево проекта.
///
/// В старом коде дерево и модель были одним и тем же объектом: GLItem
/// подписывался на события TreeView и хранил ссылку на свой узел. Из-за
/// этого модель нельзя было ни сохранить, ни протестировать без формы.
///
/// Здесь дерево — обычное представление: строится из документа, ничего
/// в нём не хранит, а о выборе сообщает событием.
/// </summary>
public sealed class ProjectTreeView : TreeView
{
    private Document? _doc;
    private bool _suppress;

    public event Action<ModelItem>? ItemSelected;
    public event Action<ModelItem>? ItemActivated;
    public event Action<ModelItem, bool>? VisibilityToggled;

    public ProjectTreeView()
    {
        HideSelection = false;
        FullRowSelect = true;
        ShowLines = true;
        Indent = 18;
        ItemHeight = 20;
        CheckBoxes = true;
    }

    public void SetDocument(Document doc)
    {
        _doc = doc;
        Rebuild();
    }

    public void Rebuild()
    {
        if (_doc == null) return;
        _suppress = true;
        BeginUpdate();

        var expanded = CollectExpanded();
        uint selectedId = (SelectedNode?.Tag as ModelItem)?.Id ?? 0;

        Nodes.Clear();
        TreeNode root = AddItem(Nodes, _doc.Root);
        RestoreExpanded(expanded);
        SelectById(selectedId);
        root.Expand();

        EndUpdate();
        _suppress = false;
    }

    private TreeNode AddItem(TreeNodeCollection parent, ModelItem item)
    {
        var node = parent.Add(Label(item));
        node.Tag = item;
        node.Checked = item.Visible;
        if (item.Error != null) node.ForeColor = Color.Firebrick;
        else if (item.Suppressed) node.ForeColor = Color.Gray;

        foreach (var c in item.Children)
        {
            // Фичи показываем в порядке дерева построения, а не в порядке Children.
            if (c is Feature) continue;
            AddItem(node.Nodes, c);
        }

        if (item is Part p)
        {
            var fnode = node.Nodes.Add("Дерево построения");
            fnode.Tag = null;
            fnode.Checked = true;
            foreach (var f in p.Features) AddItem(fnode.Nodes, f);
            fnode.Expand();
        }

        return node;
    }

    private static string Label(ModelItem i)
    {
        string extra = i switch
        {
            Part p when p.Mesh != null => $"  [V={p.Volume / 1000.0:0.##} см³]",
            Sketch s => $"  [DOF {s.DegreesOfFreedom}]",
            Feature f => "",
            _ => ""
        };
        string err = i.Error != null ? "  ⚠" : "";
        string name = i is Feature ff ? ff.Summary : i.Name;
        return name + extra + err;
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        if (_suppress) return;
        if (e.Node?.Tag is ModelItem m) ItemSelected?.Invoke(m);
    }

    protected override void OnNodeMouseDoubleClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseDoubleClick(e);
        if (e.Node.Tag is ModelItem m) ItemActivated?.Invoke(m);
    }

    protected override void OnAfterCheck(TreeViewEventArgs e)
    {
        base.OnAfterCheck(e);
        if (_suppress || e.Node?.Tag is not ModelItem m) return;
        m.Visible = e.Node.Checked;
        VisibilityToggled?.Invoke(m, e.Node.Checked);
    }

    public void SelectById(uint id)
    {
        if (id == 0) return;
        foreach (TreeNode n in AllNodes())
            if (n.Tag is ModelItem m && m.Id == id) { SelectedNode = n; return; }
    }

    private IEnumerable<TreeNode> AllNodes()
    {
        var stack = new Stack<TreeNode>();
        foreach (TreeNode n in Nodes) stack.Push(n);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (TreeNode c in n.Nodes) stack.Push(c);
        }
    }

    private HashSet<uint> CollectExpanded()
    {
        var set = new HashSet<uint>();
        foreach (var n in AllNodes())
            if (n.IsExpanded && n.Tag is ModelItem m) set.Add(m.Id);
        return set;
    }

    private void RestoreExpanded(HashSet<uint> set)
    {
        foreach (var n in AllNodes())
            if (n.Tag is ModelItem m && set.Contains(m.Id)) n.Expand();
    }
}
