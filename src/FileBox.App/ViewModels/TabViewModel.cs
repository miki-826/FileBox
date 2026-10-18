using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.ViewModels;

/// <summary>分割ペインの木構造。葉は区画、節は2分割。</summary>
public abstract class PaneNode
{
    public SplitNode? Parent { get; set; }
}

public sealed class LeafNode(PaneViewModel pane) : PaneNode
{
    public PaneViewModel Pane { get; } = pane;
}

public sealed class SplitNode(Orientation orientation, PaneNode first, PaneNode second, double ratio = 0.5) : PaneNode
{
    public Orientation Orientation { get; set; } = orientation;
    public PaneNode First { get; set; } = first;
    public PaneNode Second { get; set; } = second;
    public double Ratio { get; set; } = ratio;
}

/// <summary>タブ1つ分。最大4区画まで分割できる（02_機能設計 §2.1）。</summary>
public sealed partial class TabViewModel : ObservableObject
{
    public const int MaxPanes = 4;

    [ObservableProperty]
    string _title = "";

    [ObservableProperty]
    PaneViewModel _activePane;

    PaneViewModel? _lastSourcePane;

    public PaneNode Root { get; private set; }

    /// <summary>区画の追加・削除・入れ替えで再描画が必要になった。</summary>
    public event Action? LayoutChanged;

    public TabViewModel(PaneViewModel first)
    {
        Root = new LeafNode(first);
        _activePane = first;
        Hook(first);
        UpdateTitle();
    }

    TabViewModel(PaneNode root, PaneViewModel active)
    {
        Root = root;
        _activePane = active;
        foreach (var p in Panes) Hook(p);
        UpdateTitle();
    }

    public IEnumerable<PaneViewModel> Panes => Leaves(Root).Select(l => l.Pane);
    public int PaneCount => Leaves(Root).Count();
    public bool CanSplit => PaneCount < MaxPanes;

    public FolderPaneViewModel? ActiveFolder =>
        ActivePane as FolderPaneViewModel ?? Panes.OfType<FolderPaneViewModel>().FirstOrDefault();

    partial void OnActivePaneChanged(PaneViewModel value) => UpdateTitle();

    void Hook(PaneViewModel pane)
    {
        pane.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.Title) && ReferenceEquals(s, ActivePane)) UpdateTitle();
        };
        pane.SelectionChanged += OnPaneSelectionChanged;
    }

    void UpdateTitle()
    {
        var source = ActivePane is PreviewPaneViewModel ? (Panes.FirstOrDefault(p => p is not PreviewPaneViewModel) ?? ActivePane) : ActivePane;
        Title = PaneCount > 1 ? $"{source.Title} +{PaneCount - 1}" : source.Title;
    }

    // 連動プレビュー: 選択が変わった区画に紐づくプレビューへ通知する
    void OnPaneSelectionChanged(PaneViewModel source, FileItem? item)
    {
        _lastSourcePane = source;
        foreach (var preview in Panes.OfType<PreviewPaneViewModel>())
        {
            bool linked = preview.LinkedTo == null || preview.LinkedTo == source.Id || Panes.All(p => p.Id != preview.LinkedTo);
            if (linked && item != null) preview.Item = item;
        }
    }

    /// <summary>区画を分割して新しい区画を追加する。上限に達していたら false。</summary>
    public bool Split(PaneViewModel target, PaneViewModel newPane, Orientation orientation, double ratio = 0.5)
    {
        if (!CanSplit) return false;
        var leaf = FindLeaf(target);
        if (leaf == null) return false;
        var parent = leaf.Parent;
        var newLeaf = new LeafNode(newPane);
        var split = new SplitNode(orientation, leaf, newLeaf, ratio) { Parent = parent };
        leaf.Parent = split;
        newLeaf.Parent = split;
        Replace(parent, leaf, split);
        Hook(newPane);
        ActivePane = newPane;
        UpdateTitle();
        LayoutChanged?.Invoke();
        return true;
    }

    /// <summary>区画を閉じる。最後の1区画は閉じない（false を返す → タブごと閉じる）。</summary>
    public bool Close(PaneViewModel pane)
    {
        var leaf = FindLeaf(pane);
        if (leaf?.Parent is not SplitNode parent) return false;
        var sibling = ReferenceEquals(parent.First, leaf) ? parent.Second : parent.First;
        sibling.Parent = parent.Parent;
        Replace(parent.Parent, parent, sibling);
        pane.SelectionChanged -= OnPaneSelectionChanged;
        pane.Dispose();
        if (ReferenceEquals(ActivePane, pane)) ActivePane = Leaves(sibling).First().Pane;
        UpdateTitle();
        LayoutChanged?.Invoke();
        return true;
    }

    public void ReplacePane(PaneViewModel oldPane, PaneViewModel newPane)
    {
        var leaf = FindLeaf(oldPane);
        if (leaf == null) return;
        var newLeaf = new LeafNode(newPane) { Parent = leaf.Parent };
        Replace(leaf.Parent, leaf, newLeaf);
        oldPane.SelectionChanged -= OnPaneSelectionChanged;
        oldPane.Dispose();
        Hook(newPane);
        ActivePane = newPane;
        UpdateTitle();
        LayoutChanged?.Invoke();
    }

    void Replace(SplitNode? parent, PaneNode oldNode, PaneNode newNode)
    {
        if (parent == null) Root = newNode;
        else if (ReferenceEquals(parent.First, oldNode)) parent.First = newNode;
        else parent.Second = newNode;
    }

    LeafNode? FindLeaf(PaneViewModel pane) => Leaves(Root).FirstOrDefault(l => ReferenceEquals(l.Pane, pane));

    static IEnumerable<LeafNode> Leaves(PaneNode node)
    {
        if (node is LeafNode leaf) { yield return leaf; yield break; }
        var split = (SplitNode)node;
        foreach (var l in Leaves(split.First)) yield return l;
        foreach (var l in Leaves(split.Second)) yield return l;
    }

    /// <summary>連動元を持たないプレビューに紐づけるべき区画（直近で選択操作した区画）。</summary>
    public PaneViewModel? LastSourcePane => _lastSourcePane;

    public void Dispose()
    {
        foreach (var p in Panes) p.Dispose();
    }

    // ---------------------------------------------------------------- 保存形式（03_データ設計 workspaces.layout_json）

    public JsonObject Serialize() => new()
    {
        ["title"] = Title,
        ["active"] = ActivePane.Id,
        ["root"] = SerializeNode(Root),
    };

    static JsonObject SerializeNode(PaneNode node) => node switch
    {
        LeafNode l => l.Pane.Serialize(),
        SplitNode s => new JsonObject
        {
            ["split"] = s.Orientation == Orientation.Horizontal ? "horizontal" : "vertical",
            ["ratio"] = s.Ratio,
            ["a"] = SerializeNode(s.First),
            ["b"] = SerializeNode(s.Second),
        },
        _ => throw new InvalidOperationException(),
    };

    public static TabViewModel? Deserialize(JsonObject json)
    {
        if (json["root"] is not JsonObject rootJson) return null;
        var root = DeserializeNode(rootJson, null);
        if (root == null) return null;
        var panes = Leaves(root).Select(l => l.Pane).ToList();
        string? activeId = (string?)json["active"];
        var active = panes.FirstOrDefault(p => p.Id == activeId) ?? panes[0];
        return new TabViewModel(root, active);
    }

    static PaneNode? DeserializeNode(JsonObject json, SplitNode? parent)
    {
        if (json["split"] is JsonNode splitValue)
        {
            var orientation = (string?)splitValue == "vertical" ? Orientation.Vertical : Orientation.Horizontal;
            double ratio = (double?)json["ratio"] ?? 0.5;
            var split = new SplitNode(orientation, null!, null!, Math.Clamp(ratio, 0.1, 0.9)) { Parent = parent };
            var a = json["a"] is JsonObject ja ? DeserializeNode(ja, split) : null;
            var b = json["b"] is JsonObject jb ? DeserializeNode(jb, split) : null;
            if (a == null || b == null)
            {
                var only = a ?? b;
                if (only != null) only.Parent = parent;
                return only;
            }
            split.First = a;
            split.Second = b;
            return split;
        }
        var pane = PaneViewModel.Deserialize(json);
        return pane == null ? null : new LeafNode(pane) { Parent = parent };
    }
}
