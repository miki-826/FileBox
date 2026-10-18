using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileBox.Contracts;
using FileBox.Indexer.Index;
using Microsoft.Win32.SafeHandles;
using static FileBox.Indexer.Volumes.Native;

namespace FileBox.Indexer.Volumes;

/// <summary>
/// NTFS ボリュームを MFT 列挙で一括構築し、USN ジャーナルで変更を追従する（Everything 方式）。
/// ボリュームハンドルを開くため管理者権限（または LocalSystem サービス）が必要。
/// </summary>
internal sealed class NtfsVolumeSource : IVolumeSource
{
    readonly FileIndex _index;
    readonly byte _vol;
    readonly string _root;        // "C:\"
    readonly SafeFileHandle _volume;
    readonly uint _serial;
    readonly ulong _rootFrn;
    ulong _journalId;
    long _nextUsn;
    int _rootId = -1;

    public VolumeStatus Status { get; }

    NtfsVolumeSource(FileIndex index, byte vol, string root, SafeFileHandle volume, uint serial, ulong rootFrn)
    {
        _index = index;
        _vol = vol;
        _root = root;
        _volume = volume;
        _serial = serial;
        _rootFrn = rootFrn;
        Status = new VolumeStatus { Root = root, Method = "MFT" };
    }

    /// <summary>ボリュームを開ける場合のみ作成する（権限不足・非NTFS・ジャーナル無効なら null）。</summary>
    public static NtfsVolumeSource? TryCreate(FileIndex index, byte vol, string root)
    {
        string letter = root.TrimEnd('\\');
        var volume = CreateFileW(@"\\.\" + letter, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (volume.IsInvalid)
        {
            Log.Info($"{root}: ボリュームを開けません (Win32 {Marshal.GetLastWin32Error()})。走査方式に切り替えます");
            return null;
        }
        using var rootHandle = CreateFileW(root, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (rootHandle.IsInvalid || !GetFileInformationByHandle(rootHandle, out var info))
        {
            volume.Dispose();
            return null;
        }
        ulong rootFrn = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var source = new NtfsVolumeSource(index, vol, root, volume, info.VolumeSerialNumber, rootFrn);
        if (!source.QueryJournal(out _))
        {
            source.Dispose();
            return null;
        }
        return source;
    }

    unsafe bool QueryJournal(out USN_JOURNAL_DATA_V0 data)
    {
        USN_JOURNAL_DATA_V0 d;
        bool ok = DeviceIoControl(_volume, FSCTL_QUERY_USN_JOURNAL, null, 0, &d, sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero);
        data = d;
        if (!ok) Log.Info($"{_root}: USN ジャーナルを取得できません (Win32 {Marshal.GetLastWin32Error()})");
        return ok;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await Task.Run(() => Build(), ct).ConfigureAwait(false);
        Status.Ready = true;
        Status.BuildSeconds = sw.Elapsed.TotalSeconds;
        Status.Entries = _index.CountVolume(_vol);
        Log.Info($"{_root}: 構築完了 方式={Status.Method} 件数={Status.Entries} {Status.BuildSeconds:F1}秒");

        _ = Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try { MetadataFiller.Fill(_index, _vol, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error($"{_root}: メタ情報取得失敗", ex); }
        }, ct);

        await WatchAsync(ct).ConfigureAwait(false);
    }

    void Build()
    {
        QueryJournal(out var journal);
        _journalId = journal.UsnJournalID;

        // スナップショットが有効なら読み込んで差分だけ追従
        var snap = Snapshot.TryLoad(Snapshot.PathFor(_serial));
        if (snap is { } s && s.Header.JournalId == journal.UsnJournalID && s.Header.NextUsn >= journal.FirstUsn && s.Header.RootFrn == _rootFrn)
        {
            using var w = _index.Write();
            _rootId = w.AddRoot(_vol, _root.TrimEnd('\\'), _rootFrn);
            w.BulkLoad(_vol, _rootId, s.Entries);
            _nextUsn = s.Header.NextUsn;
            Status.Method = "Snapshot";
            return;
        }

        var entries = EnumerateMft(journal.NextUsn);
        using (var w = _index.Write())
        {
            _rootId = w.AddRoot(_vol, _root.TrimEnd('\\'), _rootFrn);
            w.BulkLoad(_vol, _rootId, entries);
        }
        _nextUsn = journal.NextUsn;
        Status.Method = "MFT";
    }

    unsafe List<RawEntry> EnumerateMft(long highUsn)
    {
        var list = new List<RawEntry>(1 << 20);
        const int bufferSize = 1 << 20;
        byte* buffer = (byte*)NativeMemory.Alloc(bufferSize);
        try
        {
            var med = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = highUsn };
            while (true)
            {
                if (!DeviceIoControl(_volume, FSCTL_ENUM_USN_DATA, &med, sizeof(MFT_ENUM_DATA_V0), buffer, bufferSize, out int returned, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_HANDLE_EOF) break;
                    throw new Win32Exception(err, "FSCTL_ENUM_USN_DATA");
                }
                med.StartFileReferenceNumber = *(ulong*)buffer;
                int offset = 8;
                while (offset < returned)
                {
                    byte* rec = buffer + offset;
                    int length = *(int*)rec;
                    if (length <= 0) break;
                    if (*(ushort*)(rec + UsnRecord.MajorVersion) == 2)
                    {
                        list.Add(new RawEntry
                        {
                            Frn = *(ulong*)(rec + UsnRecord.FileReferenceNumber),
                            ParentFrn = *(ulong*)(rec + UsnRecord.ParentFileReferenceNumber),
                            Name = ReadName(rec),
                            Flags = ToFlags(*(uint*)(rec + UsnRecord.FileAttributes)) | EntryFlags.MetaMissing,
                        });
                    }
                    offset += length;
                }
            }
        }
        finally { NativeMemory.Free(buffer); }
        return list;
    }

    async Task WatchAsync(CancellationToken ct)
    {
        byte[] managed = new byte[1 << 16];
        while (!ct.IsCancellationRequested)
        {
            int error = ReadBatch(managed, out long next, out int returned);
            if (error is ERROR_JOURNAL_ENTRY_DELETED or ERROR_JOURNAL_DELETE_IN_PROGRESS or ERROR_JOURNAL_NOT_ACTIVE)
            {
                Log.Info($"{_root}: ジャーナルが失われたため再構築します (Win32 {error})");
                Rebuild();
                continue;
            }
            if (error != 0)
            {
                Log.Error($"{_root}: USN 読み取り失敗 (Win32 {error})");
                await Task.Delay(5000, ct).ConfigureAwait(false);
                continue;
            }
            bool more = next != _nextUsn && returned > 8;
            _nextUsn = next;
            if (!more) await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>USN ジャーナルを1回読み、変更をインデックスへ反映する。戻り値は Win32 エラー（0=成功）。</summary>
    unsafe int ReadBatch(byte[] managed, out long next, out int returned)
    {
        int error = 0;
        fixed (byte* buffer = managed)
        {
            var rd = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = _nextUsn,
                ReasonMask = 0xFFFFFFFF,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = _journalId,
            };
            if (!DeviceIoControl(_volume, FSCTL_READ_USN_JOURNAL, &rd, sizeof(READ_USN_JOURNAL_DATA_V0), buffer, managed.Length, out returned, IntPtr.Zero))
                error = Marshal.GetLastWin32Error();
            next = returned >= 8 ? *(long*)buffer : _nextUsn;
            if (error == 0 && returned > 8) Apply(buffer, returned);
        }
        return error;
    }

    unsafe void Apply(byte* buffer, int returned)
    {
        using var w = _index.Write();
        int offset = 8;
        while (offset < returned)
        {
            byte* rec = buffer + offset;
            int length = *(int*)rec;
            if (length <= 0) break;
            offset += length;
            if (*(ushort*)(rec + UsnRecord.MajorVersion) != 2) continue;

            uint reason = *(uint*)(rec + UsnRecord.Reason);
            ulong frn = *(ulong*)(rec + UsnRecord.FileReferenceNumber);
            if ((reason & USN_REASON_FILE_DELETE) != 0)
            {
                w.RemoveByFrn(_vol, frn);
            }
            else if ((reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) != 0)
            {
                ulong parent = *(ulong*)(rec + UsnRecord.ParentFileReferenceNumber);
                w.UpsertByFrn(_vol, frn, parent, ReadName(rec), ToFlags(*(uint*)(rec + UsnRecord.FileAttributes)));
            }
            else if ((reason & (USN_REASON_DATA_OVERWRITE | USN_REASON_DATA_EXTEND | USN_REASON_DATA_TRUNCATION | USN_REASON_BASIC_INFO_CHANGE)) != 0)
            {
                int id = w.ByFrn(_vol, frn);
                if (id >= 0) w.MarkMetaMissing(id);
            }
        }
    }

    void Rebuild()
    {
        using (var w = _index.Write()) w.ClearVolume(_vol);
        QueryJournal(out var journal);
        _journalId = journal.UsnJournalID;
        var entries = EnumerateMft(journal.NextUsn);
        using (var w = _index.Write())
        {
            _rootId = w.AddRoot(_vol, _root.TrimEnd('\\'), _rootFrn);
            w.BulkLoad(_vol, _rootId, entries);
        }
        _nextUsn = journal.NextUsn;
    }

    public void SaveSnapshot()
    {
        if (!Status.Ready) return;
        try
        {
            long usn = _nextUsn;
            var entries = _index.ExportVolume(_vol);
            Snapshot.Save(Snapshot.PathFor(_serial), new Snapshot.Header(_serial, _journalId, usn, _rootFrn), entries);
            Log.Info($"{_root}: スナップショット保存 {entries.Count} 件");
        }
        catch (Exception ex) { Log.Error($"{_root}: スナップショット保存失敗", ex); }
    }

    static unsafe string ReadName(byte* rec)
    {
        int len = *(ushort*)(rec + UsnRecord.FileNameLength);
        int off = *(ushort*)(rec + UsnRecord.FileNameOffset);
        return new string((char*)(rec + off), 0, len / 2);
    }

    static EntryFlags ToFlags(uint attributes)
    {
        var f = EntryFlags.None;
        if ((attributes & 0x10) != 0) f |= EntryFlags.Directory;
        if ((attributes & 0x2) != 0) f |= EntryFlags.Hidden;
        if ((attributes & 0x4) != 0) f |= EntryFlags.System;
        return f;
    }

    public void Dispose() => _volume.Dispose();
}
