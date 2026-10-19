# 検出されたダウンローダーの作成を停止する。名前を変えた再配布もしない。
$ErrorActionPreference = 'Stop'
throw 'FileBox-Setup.exe の作成・配布は停止しました。Package-Preview.ps1 と Build-Installer.ps1 で本体同梱のインストーラーを作成してください。'
