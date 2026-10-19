# 旧ダウンローダー（配布停止）

`FileBox-Setup.exe` は Microsoft Defender による検出を受け、2026-10-19 に作成・配布を停止しました。`Program.cs` は旧実装の参照用です。検出されたバイナリを変更・改名して配布する用途には使いません。

導入には `installer/FileBox.iss` による本体同梱のインストーラーを使用します。`Package.cs` は旧パッケージ検証の回帰テストのため残しています。
