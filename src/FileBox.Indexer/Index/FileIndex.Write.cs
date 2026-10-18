namespace FileBox.Indexer.Index;

public sealed partial class FileIndex
{
    /// <summary>書き込みロックを保持している間だけ有効な更新用ハンドル。</summary>
    public readonly struct WriteScope : IDisposable
    {
        readonly FileIndex _ix;

        internal WriteScope(FileIndex ix)
        {
            _ix = ix;
            ix._lock.EnterWriteLock();
        }

        public void Dispose() => _ix._lock.ExitWriteLock();

        public int AddRoot(byte vol, string name, ulong frn) => _ix.AddRootCore(vol, name, frn);
        public int Add(int parent, string name, EntryFlags flags, long size, long mtimeUtcTicks, ulong frn = 0)
            => _ix.AddCore(parent, name, flags, size, mtimeUtcTicks, frn);
        public void Remove(int id) => _ix.RemoveCore(id);
        public void Move(int id, int newParent, string newName) => _ix.MoveCore(id, newParent, newName);
        public void SetMeta(int id, long size, long mtimeUtcTicks) => _ix.SetMetaCore(id, size, mtimeUtcTicks);
        public void MarkMetaMissing(int id) => _ix._flags[id] |= EntryFlags.MetaMissing;
        public int FindChild(int parent, string name) => _ix.FindChildCore(parent, name);
        public int Resolve(string path) => _ix.ResolveCore(path);
        public string GetPath(int id) => _ix.GetPathCore(id);
        public bool IsDirectory(int id) => (_ix._flags[id] & EntryFlags.Directory) != 0;
        public bool IsLive(int id) => id >= 0 && id < _ix._count && (_ix._flags[id] & EntryFlags.Deleted) == 0;
        public void ClearVolume(byte vol) => _ix.ClearVolumeCore(vol);

        public IReadOnlyList<int> Children(int dir) =>
            _ix._children.TryGetValue(dir, out var list) ? list.ToArray() : Array.Empty<int>();

        public string Name(int id) => _ix._names[id]!;
        public bool HasMetaMissing(int id) => (_ix._flags[id] & EntryFlags.MetaMissing) != 0;

        // ---- NTFS ファイル参照番号 (FRN) ベースの操作（USN ジャーナル用）

        public int ByFrn(byte vol, ulong frn) =>
            _ix._frnMaps[vol] is { } map && map.TryGetValue(frn, out int id) ? id : -1;

        /// <summary>FRN のエントリを作成または移動/リネームする。親が未知なら -1。</summary>
        public int UpsertByFrn(byte vol, ulong frn, ulong parentFrn, string name, EntryFlags flags)
        {
            int parent = ByFrn(vol, parentFrn);
            if (parent < 0) return -1;
            int id = ByFrn(vol, frn);
            if (id >= 0)
            {
                _ix.MoveCore(id, parent, name);
                _ix._flags[id] |= EntryFlags.MetaMissing;
                return id;
            }
            return _ix.AddCore(parent, name, flags | EntryFlags.MetaMissing, 0, 0, frn);
        }

        public void RemoveByFrn(byte vol, ulong frn)
        {
            int id = ByFrn(vol, frn);
            if (id >= 0) _ix.RemoveCore(id);
        }

        /// <summary>MFT 列挙結果・スナップショットを一括投入する。親が解決できないエントリは捨てる。</summary>
        public int BulkLoad(byte vol, int rootId, List<RawEntry> entries)
        {
            var ix = _ix;
            var map = ix._frnMaps[vol] ??= new Dictionary<ulong, int>();
            map.EnsureCapacity(entries.Count + 1);
            int baseId = ix._count;
            ix.EnsureCapacity(baseId + entries.Count);
            // 1st pass: ID 採番
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                int id = baseId + i;
                ix._names[id] = e.Name;
                ix._flags[id] = e.Flags & ~(EntryFlags.Deleted | EntryFlags.Root);
                ix._sizes[id] = e.Size;
                ix._mtimes[id] = e.MTimeUtcTicks;
                ix._frns[id] = e.Frn;
                ix._vols[id] = vol;
                ix._parents[id] = -1;
                map[e.Frn] = id;
            }
            ix._count = baseId + entries.Count;
            // 2nd pass: 親子付け
            int loaded = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                int id = baseId + i;
                if (!map.TryGetValue(entries[i].ParentFrn, out int parent) || parent == id)
                {
                    ix._flags[id] |= EntryFlags.Deleted;
                    ix._names[id] = "";
                    continue;
                }
                ix._parents[id] = parent;
                ix.AddChild(parent, id);
                loaded++;
            }
            // 祖先がルートに辿り着かない孤立ノード（メタファイル等）を除外
            for (int i = 0; i < entries.Count; i++)
            {
                int id = baseId + i;
                if ((ix._flags[id] & EntryFlags.Deleted) != 0) continue;
                int cur = id, guard = 0;
                while (cur >= 0 && cur != rootId && guard++ < 512) cur = ix._parents[cur];
                if (cur != rootId)
                {
                    ix._flags[id] |= EntryFlags.Deleted;
                    loaded--;
                }
            }
            Interlocked.Add(ref ix._live, loaded);
            return loaded;
        }
    }

    void EnsureCapacity(int needed)
    {
        if (needed <= _names.Length) return;
        int cap = Math.Max(needed, _names.Length * 2);
        Array.Resize(ref _names, cap);
        Array.Resize(ref _parents, cap);
        Array.Resize(ref _flags, cap);
        Array.Resize(ref _sizes, cap);
        Array.Resize(ref _mtimes, cap);
        Array.Resize(ref _frns, cap);
        Array.Resize(ref _vols, cap);
    }

    int AddRootCore(byte vol, string name, ulong frn)
    {
        EnsureCapacity(_count + 1);
        int id = _count++;
        _names[id] = name;
        _parents[id] = -1;
        _flags[id] = EntryFlags.Root | EntryFlags.Directory;
        _frns[id] = frn;
        _vols[id] = vol;
        _children[id] = new List<int>();
        _roots.Add(id);
        if (frn != 0) (_frnMaps[vol] ??= new())[frn] = id;
        return id;
    }

    int AddCore(int parent, string name, EntryFlags flags, long size, long mtime, ulong frn)
    {
        EnsureCapacity(_count + 1);
        int id = _count++;
        byte vol = _vols[parent];
        _names[id] = name;
        _parents[id] = parent;
        _flags[id] = flags & ~(EntryFlags.Deleted | EntryFlags.Root);
        _sizes[id] = size;
        _mtimes[id] = mtime;
        _frns[id] = frn;
        _vols[id] = vol;
        AddChild(parent, id);
        if (frn != 0) (_frnMaps[vol] ??= new())[frn] = id;
        Interlocked.Increment(ref _live);
        return id;
    }

    void AddChild(int parent, int id)
    {
        if (!_children.TryGetValue(parent, out var list))
            _children[parent] = list = new List<int>(4);
        list.Add(id);
    }

    /// <summary>エントリとその配下をすべて削除扱いにする。</summary>
    void RemoveCore(int id)
    {
        if ((_flags[id] & EntryFlags.Deleted) != 0) return;
        int parent = _parents[id];
        if (parent >= 0 && _children.TryGetValue(parent, out var siblings)) siblings.Remove(id);

        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            if ((_flags[cur] & EntryFlags.Deleted) != 0) continue;
            _flags[cur] |= EntryFlags.Deleted;
            if (_frns[cur] != 0) _frnMaps[_vols[cur]]?.Remove(_frns[cur]);
            Interlocked.Decrement(ref _live);
            if (_children.Remove(cur, out var kids))
                foreach (int k in kids) stack.Push(k);
        }
    }

    void MoveCore(int id, int newParent, string newName)
    {
        int old = _parents[id];
        if (old != newParent)
        {
            if (old >= 0 && _children.TryGetValue(old, out var siblings)) siblings.Remove(id);
            _parents[id] = newParent;
            AddChild(newParent, id);
        }
        _names[id] = newName;
    }

    void SetMetaCore(int id, long size, long mtime)
    {
        _sizes[id] = size;
        _mtimes[id] = mtime;
        _flags[id] &= ~EntryFlags.MetaMissing;
    }

    void ClearVolumeCore(byte vol)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_vols[i] != vol || (_flags[i] & EntryFlags.Deleted) != 0) continue;
            _flags[i] |= EntryFlags.Deleted;
            _children.Remove(i);
            if ((_flags[i] & EntryFlags.Root) == 0) Interlocked.Decrement(ref _live);
        }
        _roots.RemoveAll(r => _vols[r] == vol);
        _frnMaps[vol] = null;
    }
}
