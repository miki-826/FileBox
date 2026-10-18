using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using FileBox.Contracts;
using FileBox.Indexer.Index;

namespace FileBox.Indexer.Volumes;

/// <summary>
/// フォールバック方式: 並列ディレクトリ走査で構築し、FileSystemWatcher で変更を追従する。
/// 管理者権限がない場合や FAT/exFAT/ネットワークドライブで使う（02_機能設計 §3.1）。
/// </summary>
internal sealed class ScanVolumeSource : IVolumeSource
{
    readonly record struct Item(string Name, EntryFlags Flags, long Size, long MTime);

    readonly FileIndex _index;
    readonly byte _vol;
    readonly string _root;
    readonly ConcurrentQueue<FileSystemEventArgs> _events = new();
    FileSystemWatcher? _watcher;
    int _rootId;
    volatile bool _rescanRequested;

    public VolumeStatus Status { get; }

    public ScanVolumeSource(FileIndex index, byte vol, string root)
    {
        _index = index;
        _vol = vol;
        _root = root;
        Status = new VolumeStatus { Root = root, Method = "Scan" };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        StartWatcher();
        var sw = Stopwatch.StartNew();
        await Task.Run(() => Scan(ct), ct).ConfigureAwait(false);
        Status.Ready = true;
        Status.BuildSeconds = sw.Elapsed.TotalSeconds;
        Status.Entries = _index.CountVolume(_vol);
        Log.Info($"{_root}: 走査完了 件数={Status.Entries} {Status.BuildSeconds:F1}秒");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            if (_rescanRequested)
            {
                _rescanRequested = false;
                Log.Info($"{_root}: 変更通知があふれたため再走査します");
                while (_events.TryDequeue(out _)) { }
                using (var w = _index.Write()) w.ClearVolume(_vol);
                await Task.Run(() => Scan(ct), ct).ConfigureAwait(false);
                continue;
            }
            ApplyEvents();
        }
    }

    void Scan(CancellationToken ct)
    {
        using (var w = _index.Write()) _rootId = w.AddRoot(_vol, _root.TrimEnd('\\'), 0);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var pending = new BlockingCollection<(int Id, string Path)>();
        int outstanding = 1;
        pending.Add((_rootId, _root));

        void Worker()
        {
            foreach (var (dirId, dirPath) in pending.GetConsumingEnumerable(ct))
            {
                try
                {
                    List<Item> items;
                    try
                    {
                        items = new FileSystemEnumerable<Item>(dirPath, (ref FileSystemEntry e) => new Item(
                            e.FileName.ToString(),
                            ToFlags(e.Attributes),
                            e.IsDirectory ? 0 : e.Length,
                            e.LastWriteTimeUtc.UtcTicks), options).ToList();
                    }
                    catch (Exception) { items = new(); }

                    var subdirs = new List<(int, string)>();
                    using (var w = _index.Write())
                    {
                        if (!w.IsLive(dirId)) continue;
                        foreach (var it in items)
                        {
                            int id = w.Add(dirId, it.Name, it.Flags & ~(EntryFlags.MetaMissing | ReparseMarker), it.Size, it.MTime);
                            // ジャンクション・シンボリックリンクは辿らない（循環防止）
                            if ((it.Flags & EntryFlags.Directory) != 0 && !IsReparse(it))
                                subdirs.Add((id, Path.Combine(dirPath, it.Name)));
                        }
                    }
                    foreach (var s in subdirs)
                    {
                        Interlocked.Increment(ref outstanding);
                        pending.Add(s, ct);
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0) pending.CompleteAdding();
                }
            }
        }

        var threads = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8))
            .Select(_ => Task.Factory.StartNew(Worker, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        Task.WaitAll(threads, ct);
    }

    // 走査時に Reparse 属性を Item に残すため、Flags の上位ビットを流用する
    const EntryFlags ReparseMarker = (EntryFlags)128;
    static bool IsReparse(Item it) => (it.Flags & ReparseMarker) != 0;

    static EntryFlags ToFlags(FileAttributes a)
    {
        var f = EntryFlags.None;
        if ((a & FileAttributes.Directory) != 0) f |= EntryFlags.Directory;
        if ((a & FileAttributes.Hidden) != 0) f |= EntryFlags.Hidden;
        if ((a & FileAttributes.System) != 0) f |= EntryFlags.System;
        if ((a & FileAttributes.ReparsePoint) != 0) f |= ReparseMarker;
        return f;
    }

    void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, e) => _events.Enqueue(e);
            _watcher.Deleted += (_, e) => _events.Enqueue(e);
            _watcher.Renamed += (_, e) => _events.Enqueue(e);
            _watcher.Changed += (_, e) => _events.Enqueue(e);
            _watcher.Error += (_, e) => _rescanRequested = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error($"{_root}: 変更監視を開始できません", ex);
        }
    }

    void ApplyEvents()
    {
        if (_events.IsEmpty) return;
        using var w = _index.Write();
        int processed = 0;
        while (processed++ < 10000 && _events.TryDequeue(out var e))
        {
            try
            {
                switch (e.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        AddPath(w, e.FullPath);
                        break;
                    case WatcherChangeTypes.Deleted:
                        int del = w.Resolve(e.FullPath);
                        if (del >= 0) w.Remove(del);
                        break;
                    case WatcherChangeTypes.Renamed when e is RenamedEventArgs r:
                        int id = w.Resolve(r.OldFullPath);
                        int newParent = w.Resolve(Path.GetDirectoryName(r.FullPath) ?? "");
                        if (id >= 0 && newParent >= 0) w.Move(id, newParent, Path.GetFileName(r.FullPath));
                        else AddPath(w, r.FullPath);
                        break;
                    case WatcherChangeTypes.Changed:
                        int ch = w.Resolve(e.FullPath);
                        if (ch >= 0) w.MarkMetaMissing(ch);
                        break;
                }
            }
            catch (Exception ex) { Log.Error($"変更の反映に失敗: {e.FullPath}", ex); }
        }
    }

    /// <summary>新規パスを追加する。ディレクトリなら配下もまとめて追加する（フォルダごとの移動・展開に対応）。</summary>
    static void AddPath(FileIndex.WriteScope w, string fullPath)
    {
        if (w.Resolve(fullPath) >= 0) return;
        int parent = w.Resolve(Path.GetDirectoryName(fullPath) ?? "");
        if (parent < 0) return;
        FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        if (!info.Exists) return;
        w.Add(parent, info.Name, ToFlags(info.Attributes) & ~ReparseMarker,
            info is FileInfo f ? f.Length : 0, info.LastWriteTimeUtc.Ticks);
        if (info is DirectoryInfo d && (d.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            try
            {
                foreach (var child in d.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    AddPath(w, child.FullName);
            }
            catch (Exception) { }
        }
    }

    public void SaveSnapshot() { /* 変更ジャーナルが無いため永続化しない（起動時に再走査） */ }

    public void Dispose() => _watcher?.Dispose();
}
