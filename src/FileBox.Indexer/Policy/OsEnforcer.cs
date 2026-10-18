using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using FileBox.Core.Security;

namespace FileBox.Indexer.Policy;

/// <summary>
/// OS 強制モード（02_機能設計 §5.2・§6.3）。FileBox の設定を Windows 側にも反映し、エクスプローラーなど他の経路でも効かせる。
///  - フォルダ権限の「拒否」→ そのユーザーの SID に対する NTFS の拒否エントリ（配下へ継承）
///  - ドライブ全体のユーザー別ゾーン → NTFS ディスククォータ（fsutil quota）
/// 付けたエントリは呼び出し側が記録し、解除時に同じものだけを外す。管理者権限が必要。
/// </summary>
public static class OsEnforcer
{
    public static FileSystemRights RightsFor(Permission deny)
    {
        FileSystemRights r = 0;
        if (deny.HasFlag(Permission.Read)) r |= FileSystemRights.ReadData | FileSystemRights.ExecuteFile | FileSystemRights.ReadExtendedAttributes;
        if (deny.HasFlag(Permission.Create)) r |= FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;
        if (deny.HasFlag(Permission.Modify)) r |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        if (deny.HasFlag(Permission.Delete)) r |= FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
        return r;
    }

    static FileSystemAccessRule DenyRule(string sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);

    /// <summary>path に sid の拒否エントリを付ける。</summary>
    public static void ApplyDeny(string path, string sid, FileSystemRights rights)
    {
        if (rights == 0 || !Directory.Exists(path)) return;
        var dir = new DirectoryInfo(path);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(DenyRule(sid, rights));
        dir.SetAccessControl(acl);
    }

    /// <summary>ApplyDeny で付けたエントリだけを外す（他の設定には触れない）。</summary>
    public static void RemoveDeny(string path, string sid, FileSystemRights rights)
    {
        if (rights == 0 || !Directory.Exists(path)) return;
        var dir = new DirectoryInfo(path);
        var acl = dir.GetAccessControl();
        acl.RemoveAccessRuleSpecific(DenyRule(sid, rights));
        dir.SetAccessControl(acl);
    }

    /// <summary>そのフォルダに sid の拒否エントリ（指定の権利を含む）があるか。</summary>
    public static bool HasDeny(string path, string sid, FileSystemRights rights)
    {
        var acl = new DirectoryInfo(path).GetAccessControl();
        return acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Value == sid && (r.FileSystemRights & rights) == rights);
    }

    /// <summary>ドライブ（例 "D:\"）でユーザーのディスククォータを設定する。limit が null なら解除（無制限）。</summary>
    public static void SetVolumeQuota(string volumeRoot, string account, long? limit, int warnPercent)
    {
        string drive = volumeRoot.TrimEnd('\\');
        if (drive.Length != 2 || drive[1] != ':') throw new ArgumentException("ディスククォータはドライブ全体にだけ設定できます");
        if (limit is long bytes)
        {
            RunFsutil($"quota enforce {drive}");
            RunFsutil($"quota modify {drive} {bytes * Math.Clamp(warnPercent, 1, 100) / 100} {bytes} \"{account}\"");
        }
        else
        {
            // 0xFFFFFFFFFFFFFFFF（-1）は「制限なし」
            RunFsutil($"quota modify {drive} 18446744073709551615 18446744073709551615 \"{account}\"");
        }
    }

    static void RunFsutil(string args)
    {
        var psi = new ProcessStartInfo("fsutil.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"fsutil {args.Split(' ')[0]} に失敗しました: {output.Trim()}");
    }
}
