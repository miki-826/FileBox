## FileBox をダウンロード

### [⬇ 本体同梱インストーラーをダウンロード](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-Installer-preview-win-x64.exe)

1. **FileBox-Installer-preview-win-x64.exe** をダウンロードします。
2. 保存先とショートカットを確認してインストールします。導入中の追加ダウンロードはありません。
3. 完了画面またはデスクトップの「FileBox」から起動します。

Windows 10 1809 以降 / Windows 11 の x64 用です。.NET 8 と Windows App SDK は同梱しています。管理者権限は不要です。

保存先は `%LOCALAPPDATA%\Programs\FileBox\installed` です。スタートメニューとデスクトップ（導入画面で外せます）にショートカットを作り、Windows の「設定 → アプリ」からアンインストールできます。ユーザー設定は残ります。

### 配布前の検査

本体フォルダ・本体 ZIP・最終インストーラーを Microsoft Defender で検査し、検出やスキャンエラーがあれば公開を止めます。検査日時・定義バージョン・SHA-256 は次の記録で確認できます。

- [本体・ZIP のスキャン結果](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-app-scan.json)
- [インストーラーのスキャン結果](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-installer-scan.json)
- [インストーラーの SHA-256](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-Installer-preview-win-x64.exe.sha256)

検査時点の判定であり、将来の再検出や他の保護製品による判定は保証できません。コード署名の証明書は未設定です。

### 旧ダウンローダーの配布停止

**旧 FileBox-Setup.exe は Microsoft Defender による検出を受け、作成・配布を停止しました。** 誤検知かどうかは未確定です。検出されたファイルの復元・許可や、保護機能の無効化はしないでください。本体同梱インストーラーは以前からある別の導入方式で、旧 EXE の改名・再配布ではありません。

### ZIP で取得する場合

[本体 ZIP をダウンロード](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-preview-win-x64.zip) / [ZIP の SHA-256](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-preview-win-x64.zip.sha256)

すべて展開し、`FileBox.exe` を起動してください。`indexer` フォルダも必要です。

これは**開発プレビュー**です。製品版ではありません。検出された場合は実行せず、[Microsoft の解析窓口](https://www.microsoft.com/en-us/wdsi/filesubmission)で審査を受けてください。
