using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileBox.App.ViewModels;

/// <summary>
/// エクスプローラーに任せていた特殊な場所（ごみ箱・ネットワーク）を FileBox の中で表示する区画。
/// Location は "recycle" / "network"、ネットワークでコンピューターを開いているときは "\\サーバー"。
/// </summary>
public sealed partial class ShellPaneViewModel : PaneViewModel
{
    public const string Recycle = "recycle";
    public const string Network = "network";

    public override PaneKind Kind => PaneKind.Shell;

    [ObservableProperty]
    string _location;

    public bool IsRecycleBin => Location == Recycle;

    public ShellPaneViewModel(string location, string? id = null)
    {
        if (id != null) Id = id;
        _location = location;
        UpdateTitle();
    }

    partial void OnLocationChanged(string value) => UpdateTitle();

    void UpdateTitle() => Title = Location switch
    {
        Recycle => "ごみ箱",
        Network => "ネットワーク",
        _ => Location.TrimStart('\\'),
    };

    public override JsonObject Serialize() => new() { ["pane"] = "shell", ["id"] = Id, ["location"] = Location };
}
