using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;
using Microsoft.UI.Dispatching;

namespace FileBox.App.ViewModels;

public enum SortColumn { Name, Modified, Type, Size }

/// <summary>フォルダビュー。Path が空文字のときは「PC」（ドライブ一覧）。</summary>
public sealed partial class FolderPaneViewModel : PaneViewModel
{
    readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    readonly List<string> _back = new();
    readonly List<string> _forward = new();
    FileSystemWatcher? _watcher;
    DispatcherQueueTimer? _reloadTimer;
    CancellationTokenSource? _loadCts;
    IReadOnlyList<FileItem> _selection = Array.Empty<FileItem>();

    public override PaneKind Kind => PaneKind.Folder;

    [ObservableProperty]
    string _currentPath = "";

    [ObservableProperty]
    ObservableCollection<FileItem> _items = new();

    [ObservableProperty]
    bool _isLoading;

    [ObservableProperty]
    string? _errorText;

    [ObservableProperty]
    SortColumn _sort = SortColumn.Name;

    [ObservableProperty]
    bool _sortDescending;

    [ObservableProperty]
    string _statusText = "";

    public static bool ShowHidden { get; set; }

    /// <summary>読み込み完了後に選択したい項目（「場所を開く」用）。</summary>
    public string? PendingSelection { get; set; }

    public event Action? ItemsReloaded;

    /// <summary>どこかの区画でフォルダを開いた（「最近使ったフォルダ」用）。</summary>
    public static event Action<string>? Visited;

    public FolderPaneViewModel(string path, string? id = null)
    {
        if (id != null) Id = id;
        _ = NavigateAsync(path, addHistory: false);
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => CurrentPath.Length > 0;
    public bool IsPc => CurrentPath.Length == 0;

    public IReadOnlyList<(string Name, string Path)> Breadcrumbs
    {
        get
        {
            var list = new List<(string, string)> { ("PC", "") };
            if (IsPc) return list;
            string root = Path.GetPathRoot(CurrentPath) ?? CurrentPath;
            list.Add((root.TrimEnd('\\'), root));
            string acc = root;
            foreach (var part in CurrentPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = Path.Combine(acc, part);
                list.Add((part, acc));
            }
            return list;
        }
    }

    public async Task NavigateAsync(string path, bool addHistory = true)
    {
        path = path.Length == 0 ? "" : NormalizeDir(path);
        if (path.Length > 0 && !Directory.Exists(path))
        {
            if (File.Exists(path))
            {
                PendingSelection = path;
                path = Path.GetDirectoryName(path)!;
            }
            else
            {
                ErrorText = $"フォルダが見つかりません: {path}";
                return;
            }
        }
        // FileBox の閲覧権限（02_機能設計 §6.2）。エクスプローラーからは見られる点は §6.3 のとおり
        if (path.Length > 0 && !PolicyService.Instance.Can(Core.Security.Permission.Read, path))
        {
            ErrorText = $"このフォルダを閲覧する権限がありません: {path}";
            PolicyService.Instance.Audit("open", path, null, null, "denied", "閲覧の権限なし");
            return;
        }
        if (addHistory && !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _back.Add(CurrentPath);
            _forward.Clear();
        }
        CurrentPath = path;
        Title = IsPc ? "PC" : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        OnPropertyChanged(nameof(Breadcrumbs));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        Watch();
        if (!IsPc) Visited?.Invoke(path);
        await LoadAsync();
    }

    static string NormalizeDir(string path)
    {
        try { path = System.IO.Path.GetFullPath(path); } catch (Exception) { }
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }

    public Task GoBackAsync()
    {
        if (_back.Count == 0) return Task.CompletedTask;
        string target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentPath);
        return NavigateAsync(target, addHistory: false);
    }

    public Task GoForwardAsync()
    {
        if (_forward.Count == 0) return Task.CompletedTask;
        string target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentPath);
        return NavigateAsync(target, addHistory: false);
    }

    public Task GoUpAsync()
    {
        if (IsPc) return Task.CompletedTask;
        PendingSelection = CurrentPath;
        var parent = Directory.GetParent(CurrentPath);
        return NavigateAsync(parent?.FullName ?? "");
    }

    public Task RefreshAsync() => LoadAsync();

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorText = null;
        string path = CurrentPath;
        var sort = Sort;
        bool desc = SortDescending;
        try
        {
            var list = await Task.Run(() => Enumerate(path, sort, desc), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var folder in list.Where(i => i.IsDirectory && !i.IsDrive)) folder.SetFolderSize(null, state: "集計中…");
            Items = new ObservableCollection<FileItem>(list);
            UpdateStatus(0, 0);
            ItemsReloaded?.Invoke();
            if (path.Length > 0) _ = LoadFolderSizesAsync(path, list, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { ErrorText = "このフォルダにアクセスする権限がありません"; Items = new(); }
        catch (Exception ex) { ErrorText = ex.Message; Items = new(); }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
    }

    async Task LoadFolderSizesAsync(string path, List<FileItem> items, CancellationToken cancellation)
    {
        var folders = items.Where(i => i.IsDirectory && !i.IsDrive).ToList();
        if (folders.Count == 0) return;
        try
        {
            var report = await FolderUsageService.ReadAsync(path, cancellation);
            if (cancellation.IsCancellationRequested) return;
            var sizes = report.Children.Where(i => i.IsDirectory).ToDictionary(i => i.Path.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (sizes.TryGetValue(folder.FullPath.TrimEnd('\\'), out var size)) folder.SetFolderSize(size.Size, size.IsPartial);
                else folder.SetFolderSize(null, state: "未集計");
            }
            OnSelectionChanged(_selection.Where(Items.Contains).ToList());
            if (Sort == SortColumn.Size)
            {
                Items = new ObservableCollection<FileItem>(SortItems(Items, Sort, SortDescending));
                ItemsReloaded?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested) foreach (var folder in folders) folder.SetFolderSize(null, state: "取得不可");
        }
    }

    static List<FileItem> Enumerate(string path, SortColumn sort, bool desc)
    {
        if (path.Length == 0)
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d =>
            {
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? "ローカル ディスク" : d.VolumeLabel;
                double usage = d.TotalSize > 0 ? 1.0 - (double)d.TotalFreeSpace / d.TotalSize : 0;
                return new FileItem
                {
                    Name = $"{label} ({d.Name.TrimEnd('\\')})",
                    FullPath = d.RootDirectory.FullName,
                    IsDirectory = true,
                    IsDrive = true,
                    Size = d.TotalSize,
                    DriveUsage = usage,
                    DriveInfoText = $"空き {Format.HumanSize(d.TotalFreeSpace)} / {Format.HumanSize(d.TotalSize)}",
                };
            }).ToList();
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        var items = new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).Select(FileItem.FromInfo).ToList();
        return SortItems(items, sort, desc);
    }

    public static List<FileItem> SortItems(IEnumerable<FileItem> items, SortColumn sort, bool desc)
    {
        // フォルダは常に先頭（エクスプローラーと同じ）
        var comparer = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true);
        IOrderedEnumerable<FileItem> ordered = items.OrderByDescending(i => i.IsDirectory);
        ordered = sort switch
        {
            SortColumn.Modified => desc ? ordered.ThenByDescending(i => i.Modified) : ordered.ThenBy(i => i.Modified),
            SortColumn.Type => desc ? ordered.ThenByDescending(i => i.Extension) : ordered.ThenBy(i => i.Extension),
            SortColumn.Size => desc ? ordered.ThenByDescending(i => i.SortSize) : ordered.ThenBy(i => i.SortSize),
            _ => desc ? ordered.ThenByDescending(i => i.Name, NaturalComparer.Instance) : ordered.ThenBy(i => i.Name, NaturalComparer.Instance),
        };
        return ordered.ToList();
    }

    public void SetSort(SortColumn column)
    {
        if (Sort == column) SortDescending = !SortDescending;
        else
        {
            Sort = column;
            SortDescending = column is SortColumn.Modified or SortColumn.Size;
        }
        Items = new ObservableCollection<FileItem>(SortItems(Items, Sort, SortDescending));
        ItemsReloaded?.Invoke();
    }

    public void OnSelectionChanged(IReadOnlyList<FileItem> selected)
    {
        _selection = selected.ToList();
        UpdateStatus(selected.Count, selected.Where(s => !s.IsDrive).Sum(s => s.IsDirectory ? s.FolderBytes ?? 0 : s.Size));
        RaiseSelectionChanged(selected.Count == 1 ? selected[0] : null);
    }

    void UpdateStatus(int selectedCount, long selectedBytes)
    {
        StatusText = selectedCount == 0
            ? $"{Items.Count:N0} 個の項目"
            : $"{Items.Count:N0} 個の項目 / {selectedCount:N0} 個選択" + (selectedBytes > 0 ? $" {Format.HumanSize(selectedBytes)}" : "");
    }

    // 表示中のフォルダを監視し、変更があれば自動で再読込（300ms デバウンス）
    void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (IsPc) return;
        try
        {
            _watcher = new FileSystemWatcher(CurrentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler handler = (_, _) => ScheduleReload();
            _watcher.Created += handler;
            _watcher.Deleted += handler;
            _watcher.Changed += handler;
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { _watcher = null; }
    }

    void ScheduleReload()
    {
        _dispatcher.TryEnqueue(() =>
        {
            _reloadTimer ??= CreateReloadTimer();
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    DispatcherQueueTimer CreateReloadTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _ = LoadAsync();
        return timer;
    }

    public override JsonObject Serialize() => new()
    {
        ["pane"] = "folder",
        ["id"] = Id,
        ["path"] = CurrentPath,
        ["view"] = "details",
    };

    public override void Dispose()
    {
        _loadCts?.Cancel();
        _watcher?.Dispose();
        _reloadTimer?.Stop();
    }
}

/// <summary>「ファイル2」が「ファイル10」より前に来る自然順比較（エクスプローラーと同じ）。</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
