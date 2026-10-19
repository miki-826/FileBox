namespace FileBox.Core.Files;

/// <summary>同名のファイル・フォルダがあるときに重ならない名前を作る（エクスプローラーと同じ付け方）。</summary>
public static class UniqueName
{
    /// <summary>path が空いていればそのまま、あれば「名前 (2).ext」「名前 (3).ext」…にずらす。フォルダは「.」以降も名前として扱う。</summary>
    public static string Path(string path, Func<string, bool>? exists = null)
    {
        exists ??= Exists;
        if (!exists(path)) return path;
        var (dir, stem, ext) = Split(path);
        for (int i = 2; ; i++)
        {
            string candidate = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>同じフォルダへのコピー用: 「名前 - コピー.ext」「名前 - コピー (2).ext」…</summary>
    public static string Copy(string path, Func<string, bool>? exists = null)
    {
        exists ??= Exists;
        var (dir, stem, ext) = Split(path);
        string candidate = System.IO.Path.Combine(dir, $"{stem} - コピー{ext}");
        for (int i = 2; exists(candidate); i++)
            candidate = System.IO.Path.Combine(dir, $"{stem} - コピー ({i}){ext}");
        return candidate;
    }

    static (string Dir, string Stem, string Ext) Split(string path)
    {
        path = path.TrimEnd('\\');
        string dir = System.IO.Path.GetDirectoryName(path) ?? "";
        if (Directory.Exists(path)) return (dir, System.IO.Path.GetFileName(path), "");
        return (dir, System.IO.Path.GetFileNameWithoutExtension(path), System.IO.Path.GetExtension(path));
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
