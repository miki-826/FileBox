namespace FileBox.Core.Files;

/// <summary>利用者に見せるエラーの説明。Cause は何が起きたか、Action はどうすればよいか。</summary>
public sealed record ErrorExplanation(string Cause, string Action, int Code)
{
    public bool IsInUse => Code is 32 or 33;
    public override string ToString() => Action.Length > 0 ? $"{Cause}。{Action}" : Cause;
}

/// <summary>
/// 例外・Win32 エラーを、原因と対処が分かる日本語にする（例外のメッセージをそのまま見せない）。
/// 使用中のときに「どのアプリが使っているか」を足すのは App 側（Restart Manager）。
/// </summary>
public static class FriendlyError
{
    public static ErrorExplanation Explain(Exception ex)
    {
        int code = ex.HResult & 0xFFFF;
        bool win32 = (ex.HResult & 0xFFFF0000) == 0x80070000;
        return ex switch
        {
            UnauthorizedAccessException => FromWin32(5),
            PathTooLongException => FromWin32(206),
            DirectoryNotFoundException => FromWin32(3),
            FileNotFoundException => FromWin32(2),
            OperationCanceledException => new("操作を取り消しました", "", 1223),
            _ when win32 && Known(code) => FromWin32(code),
            IOException => new("ファイルの読み書きでエラーが起きました", "もう一度試すか、エクスプローラーで同じ操作ができるか確かめてください", code),
            _ => new("予期しないエラーが起きました", "もう一度試してください。続く場合は「不具合を報告」から報告してください", code),
        };
    }

    public static ErrorExplanation FromWin32(int code) => code switch
    {
        2 => new("ファイルが見つかりません", "別の場所へ移動・削除された可能性があります。一覧を更新（F5）してください", code),
        3 => new("フォルダーが見つかりません", "別の場所へ移動・削除された可能性があります。一覧を更新（F5）してください", code),
        5 => new("アクセスが拒否されました", "このファイル・フォルダーを変更する権限がありません。管理者に確認するか、読み取り専用でないか確かめてください", code),
        19 => new("書き込み禁止のドライブです", "USB メモリやSD カードの書き込み禁止スイッチを確かめてください", code),
        21 => new("ドライブの準備ができていません", "ディスクやメディアが入っているか、接続されているか確かめてください", code),
        32 => new("ほかのアプリがこのファイルを使っています", "使っているアプリでファイルを閉じてから、もう一度試してください", code),
        33 => new("ほかのアプリがファイルの一部をロックしています", "使っているアプリでファイルを閉じてから、もう一度試してください", code),
        39 or 112 => new("ディスクの空き容量が足りません", "不要なファイルを削除するか、別のドライブを選んでください（容量の画面で大きいものを探せます）", code),
        53 => new("ネットワーク上のコンピューターが見つかりません", "コンピューター名が正しいか、同じネットワークにつながっているか、相手の電源が入っているか確かめてください", code),
        67 => new("共有フォルダーが見つかりません", "共有の名前が正しいか、相手のコンピューターで共有が有効か確かめてください", code),
        80 or 183 => new("同じ名前のファイルが既にあります", "別の名前にするか、既存のファイルを移動してください", code),
        86 or 1326 => new("ユーザー名またはパスワードが違います", "共有フォルダーに接続するアカウントを確かめてください", code),
        123 => new("名前に使えない文字が含まれています", "\\ / : * ? \" < > | は使えません", code),
        145 => new("フォルダーが空ではありません", "中のファイルを先に移動・削除してください", code),
        206 => new("パスが長すぎます", "フォルダーの階層を浅くするか、名前を短くしてください", code),
        1219 => new("同じサーバーに別のアカウントで接続しています", "既存の接続を切断してから、もう一度接続してください", code),
        1223 => new("操作を取り消しました", "", code),
        1231 or 1232 => new("ネットワークにつながりません", "ネットワークの接続（Wi-Fi・VPN）を確かめてください", code),
        _ => new($"エラーが起きました（コード {code}）", "もう一度試してください", code),
    };

    static bool Known(int code) => code is 2 or 3 or 5 or 19 or 21 or 32 or 33 or 39 or 112 or 53 or 67 or 80 or 183 or 86 or 1326 or 123 or 145 or 206 or 1219 or 1223 or 1231 or 1232;
}
