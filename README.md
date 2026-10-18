# FileBox（ファイルボックス）

> **開発プレビュー版です。製品版ではありません。**
> 動作は保証されません。大切なデータで使う前にバックアップを取ってください。

Windows のエクスプローラーの代わりに使うことを目指したファイル管理アプリです（C# / WinUI 3 / .NET 8）。

## できること
- **保存先の提案**: ファイル名から「もしかしてこのフォルダ？」を提案。なければ新しいフォルダ名も提案。選んだ場所を学習します
  - FileBox 内の「最適な場所へ移動」、ダウンロード等を見張る「受け取りボックス」、他のアプリの「名前を付けて保存」画面の横にも表示
- **すばやい検索**: PC 全体のファイル名を常駐の索引から検索（100 万件で 100ms 未満が目標）。前方一致・完全一致・ワイルドカード・正規表現・ローマ字かな揺れ・フォルダ名だけ・拡張子/サイズ/日付の絞り込み
- **複数のファイルを同時に表示**: タブ・最大 4 分割・プレビュー（画像/テキスト/PDF/動画）・ワークスペースの保存
- **リンクでフォルダへジャンプ**: `filebox://` のリンクを予定表などに貼ると、そこからフォルダを開けます（移動・名前変更しても切れない）
- **容量制限**: フォルダごとの上限（クォータ）と、容量の内訳・大きいファイルの表示
- **アカウント・権限**: Windows のサインインをそのまま使用（ログイン画面なし）。ロール・フォルダ権限・ユーザー別容量・監査ログ

## ダウンロード（開発プレビュー）

### [⬇ FileBox-Setup.exe をダウンロード](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-Setup.exe)

1. **FileBox-Setup.exe** を実行すると、最新版のダウンロードが始まります。
2. 進捗画面で取得・展開が完了するまで待ちます。
3. **「FileBox を起動」**を押してください。

Windows 10 1809 以降 / Windows 11（x64）に対応。必要な .NET 8 と Windows App SDK は本体に同梱しているため、別途インストールする必要はありません。

- [配布ページ・使い方](https://github.com/miki-826/FileBox/releases/tag/preview)
- [本体 ZIP を直接ダウンロード](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-preview-win-x64.zip)（手動で展開する方向け）
- [EXE の SHA-256](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-Setup.exe.sha256) / [ZIP の SHA-256](https://github.com/miki-826/FileBox/releases/download/preview/FileBox-preview-win-x64.zip.sha256)

EXE は GitHub から本体を取得し、`%LOCALAPPDATA%\Programs\FileBox\preview-…\app` に展開します。「保存先を開く」で場所を確認できます。通信エラーは「再試行」でやり直せます。既存の本体とユーザー設定は上書きしません。

ZIP 版はすべて展開して `FileBox.exe` を起動してください。`indexer` フォルダも必要です。上書き更新する場合は `FileBox` と `FileBox.Indexer` を終了してください。署名していないため、Windows が発行元を確認できない旨を表示する場合があります。

main への push または main 上での手動実行で、[GitHub Actions](https://github.com/miki-826/FileBox/actions/workflows/build.yml) がテスト・ビルド・検証後に **preview プレリリース**を更新します。上記のダウンロードリンクは固定です。Actions の実行結果にもダウンロード案内を表示します。

Actions で手動作成する場合は、上のリンクで **Run workflow → main → Run workflow** を選びます。**「テスト・Windows 版の作成」→「インストーラー EXE の作成」→「ダウンロードページの更新」**の順に実行します。実行ページの Artifacts にある **FileBox-Installer-Windows-x64** からも取得できます（成果物のダウンロードには GitHub ログインが必要です）。一般配布には上記の直接ダウンロードリンクを使ってください。

配布用ファイルを手元で作るには `./scripts/Package-Preview.ps1` を実行します。`dist/` にダウンロード用 EXE、本体 ZIP、それぞれの SHA-256 を出力します。

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
