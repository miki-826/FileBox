# AGENTS.md — FileBox で作業する AI エージェント（ChatGPT / Codex など）向けの手引き

## このリポジトリ
Windows 用のファイル管理アプリ「FileBox」（エクスプローラー代替を目指す開発プレビュー）。
仕様は `docs/` の設計書（00〜06）が正。実装状況と、設計から変えた点は `docs/06_開発ロードマップ.md` の末尾にある。

## 環境
- Windows 10/11 x64、.NET 8 SDK（WinUI 3 / Windows App SDK 1.6 のため **Windows でのみビルド可**）
- `src/FileBox.App` はソリューション（`FileBox.sln`）に含めていない。単体でビルドする

## コマンド
```powershell
dotnet test FileBox.sln                         # Core / Indexer / Data / Contracts のテスト（xUnit）
dotnet build src/FileBox.App -p:Platform=x64    # アプリ。ビルド後に Indexer を出力先の indexer\ へ同梱する
```
- 変更したら必ず `dotnet test FileBox.sln` を通すこと。性能テストは環境変数 `CI` があると時間判定を省略する
- アプリ実行中はビルドの出力がロックされる。`FileBox` / `FileBox.Indexer` のプロセスを止めてからビルドする

## 構成と責務
- `FileBox.Core`（依存なし）: 検索クエリ（`Search/`）、保存先提案（`Suggest/`）、リンク・ファイルID（`Links/`）、権限・容量判定（`Security/`）、ZIP（`Files/`）。**ロジックはここに置き、テストを書く**
- `FileBox.Indexer`: 名前索引（`Index/`）、ボリューム走査/MFT（`Volumes/`）、権限・容量・監査の判定役（`Policy/`）。名前付きパイプ `FileBox.Index` のサーバ
- `FileBox.Data`: `UserStore`（%LOCALAPPDATA%\FileBox\filebox.db）、`SystemStore`（%ProgramData%\FileBox\system\system.db）。スキーマ変更は各クラスの `Migrations` 配列に**追記**する（既存の要素は書き換えない）
- `FileBox.Contracts`: App ⇔ Indexer のメッセージ。権限・容量・管理は `IndexRequestType.Call` ＋ `PolicyMethods` の操作名で呼ぶ
- `FileBox.App`: WinUI 3 の画面（MVVM: CommunityToolkit.Mvvm）。区画は `ViewModels/*PaneViewModel` と `Views/*PaneView`、`Views/PaneHost.cs` が配置する

## 約束事
- コメント・UI の文言は日本語。既存のコードの書き方（命名・コメントの密度）に合わせる
- FileBox 経由の書き込み（保存・コピー・移動・削除・名前変更）は `Services/Guard.cs` を通して権限・容量を確認し、監査ログに残す
- 権限の判定は Indexer 側が最終判断（パイプの接続元を OS から特定している）。App 側の判定は表示と事前確認のため
- 利用者の環境を変える処理（レジストリ・NTFS のアクセス権・ディスククォータ）は既定でオフ。変更前の値を記録し、オフで元に戻せるようにする
- 実行ファイルをリンクから起動しない（`filebox://` は場所を開いて選択するだけ）
- これは開発プレビュー。リリースは GitHub Actions の「preview」プレリリースのみで、製品版として公開しない
