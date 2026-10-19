# FileBox（ファイルボックス）

> **開発プレビュー版です。製品版ではありません。**
> 動作は保証されません。大切なデータで使う前にバックアップを取ってください。

Windows のエクスプローラーの代わりに使うことを目指したファイル管理アプリです（C# / WinUI 3 / .NET 8）。

## できること
- **保存先の提案**: ファイル名から「もしかしてこのフォルダ？」を提案。なければ新しいフォルダ名も提案。選んだ場所を学習します
  - FileBox 内の「最適な場所へ移動」、ダウンロード内の「整理の提案」、他のアプリの「名前を付けて保存」画面の横にも表示
- **すばやい検索**: PC 全体のファイル名を常駐の索引から検索（100 万件で 100ms 未満が目標）。前方一致・完全一致・ワイルドカード・正規表現・ローマ字かな揺れ・フォルダ名だけ・拡張子/サイズ/日付の絞り込み
- **複数のファイルを同時に表示**: タブ・最大 4 分割・プレビュー（画像/テキスト/PDF/動画）・ワークスペースの保存
- **リンクでフォルダへジャンプ**: `filebox://` のリンクを予定表などに貼ると、そこからフォルダを開けます（移動・名前変更しても切れない）
- **容量制限**: フォルダごとの上限（クォータ）と、容量の内訳・大きいファイルの表示
- **アカウント・権限**: Windows のサインインをそのまま使用（ログイン画面なし）。ロール・フォルダ権限・ユーザー別容量・監査ログ

## ダウンロード（開発プレビュー）

### [⬇ 本体同梱インストーラーをダウンロード](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-Installer-preview-win-x64.exe)

1. **FileBox-Installer-preview-win-x64.exe** をダウンロードします。
2. インストール先とショートカットを確認して導入します。本体は同梱しているため、導入中の追加ダウンロードはありません。
3. 完了画面またはデスクトップの「FileBox」から起動します。

Windows 10 1809 以降 / Windows 11（x64）に対応。.NET 8 と Windows App SDK を同梱しています。管理者権限は不要です。保存先は `%LOCALAPPDATA%\Programs\FileBox\installed` で、Windows の「設定 → アプリ」からアンインストールできます。ユーザー設定は残ります。

**旧 FileBox-Setup.exe は Microsoft Defender の検出を受け、作成・配布を停止しました。** 検出されたファイルは復元・許可せず、保護機能も無効にしないでください。誤検知かどうかは未確定です。本体同梱インストーラーは旧ダウンローダーとは別の既存の導入方式です。

- [配布ページ・使い方](https://github.com/miki-826/FileBox/releases/tag/preview)
- [インストーラーの SHA-256](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-Installer-preview-win-x64.exe.sha256)
- [本体 ZIP](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-preview-win-x64.zip) / [ZIP の SHA-256](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-preview-win-x64.zip.sha256)（手動展開向け）
- [本体・ZIP のスキャン結果](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-app-scan.json) / [インストーラーのスキャン結果](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-installer-scan.json)

ZIP 版はすべて展開して `FileBox.exe` を起動してください。`indexer` フォルダも必要です。上書き更新時は `FileBox` と `FileBox.Indexer` を終了してください。

main への push または main 上の手動実行で、[GitHub Actions](https://github.com/miki-826/FileBox/actions/workflows/build.yml) がテスト・ビルド・同梱検証・Defender スキャン後に **preview プレリリース**を更新します。検出・スキャンエラー・定義更新の失敗があれば公開を止めます。旧ダウンローダーはビルドの成否に関係なく先に配布停止します。

スキャン結果には検査日時・定義バージョン・配布物の SHA-256 を記録します。検査時点の判定であり、将来の再検出を保証するものではありません。コード署名用の証明書は未設定で、発行元の警告が出る場合もあります。検出された場合は実行せず、[Microsoft の解析窓口](https://www.microsoft.com/en-us/wdsi/filesubmission)で審査を受けてください。

Actions で手動作成する場合は **Run workflow → main → Run workflow** を選びます。**「テスト・Windows 版の作成」→「インストーラー EXE の作成」→「ダウンロードページの更新」**の順に実行します。Artifacts の **FileBox-Installer-Windows-x64** にもインストーラーとスキャン結果を保存します。

手元で配布物を作る手順（[Inno Setup 6](https://jrsoftware.org/isinfo.php) と動作中の Microsoft Defender が必要）:

```powershell
./scripts/Package-Preview.ps1
./scripts/Build-Installer.ps1
./scripts/Test-ReleaseSafety.ps1 -Path 'src/FileBox.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64', 'dist/FileBox-preview-win-x64.zip' -ReportPath dist/FileBox-app-scan.json -UpdateSignatures
./scripts/Test-ReleaseSafety.ps1 -Path dist/FileBox-Installer-preview-win-x64.exe -ReportPath dist/FileBox-installer-scan.json -UpdateSignatures
```

### 注意
- エクスプローラーの置き換え（設定）は既定でオフです。オンにすると現在のユーザーのレジストリ（HKCU）を変更し、オフで元に戻ります
- 管理者向けの「OS 強制モード」は Windows のアクセス権を変更します。試すときはテスト用のフォルダで

## 自分でビルドする
```powershell
dotnet test FileBox.sln                         # 単体テスト
dotnet build src/FileBox.App -p:Platform=x64    # アプリ（索引の常駐プロセスも同梱）
# 起動: src/FileBox.App/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/FileBox.exe
```

## 構成
| フォルダ | 内容 |
|---|---|
| `src/FileBox.App` | 画面（WinUI 3） |
| `src/FileBox.Indexer` | 常駐の索引プロセス（MFT/USN または走査）・権限/容量の判定 |
| `src/FileBox.Core` | 検索・保存先提案・リンク・権限判定などのロジック |
| `src/FileBox.Data` | SQLite（利用者ごとの user.db、共通の system.db） |
| `src/FileBox.Contracts` | App ⇔ Indexer の通信（名前付きパイプ） |
| `tests/FileBox.Tests` | 単体テスト（xUnit） |
| `docs/` | 設計書（概要・アーキテクチャ・機能・データ・画面・非機能・ロードマップと実装状況） |

## ChatGPT（Codex）などで開発する場合
リポジトリ直下の [`AGENTS.md`](AGENTS.md) に、構成・ビルド/テスト方法・作業時の約束事をまとめています。

## 状態
開発プレビュー（v0.1.0）。実装状況と未確認の項目は [`docs/06_開発ロードマップ.md`](docs/06_開発ロードマップ.md) を参照してください。ライセンスは未定です。
