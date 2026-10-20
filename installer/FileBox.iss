; FileBox 開発プレビューのインストーラー（Inno Setup 6）。
; 本体を同梱し、現在のユーザーにだけ入れる（管理者権限は不要）。
; scripts/Build-Installer.ps1 から /DAppVersion /DSourceDir /DOutputDir を渡してコンパイルする。

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #error SourceDir（本体の出力フォルダ）を指定してください
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6B1F0C52-8E0D-4C8B-9A57-3F0E2B7D9C41}
AppName=FileBox 開発プレビュー
AppVersion={#AppVersion}
AppVerName=FileBox 開発プレビュー {#AppVersion}
AppPublisher=FileBox
AppPublisherURL=https://github.com/miki-826/FileBox
AppSupportURL=https://github.com/miki-826/FileBox/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\FileBox\installed
DefaultGroupName=FileBox
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=FileBox-Installer-preview-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\FileBox.exe
SetupIconFile=..\src\FileBox.App\Assets\FileBox.ico
UninstallDisplayName=FileBox 開発プレビュー
; 起動中の FileBox / Indexer は再起動マネージャーで終了を確認してから上書きする
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
; 既定でオン（インストール後すぐデスクトップから起動できるように）。不要なら画面でチェックを外せる
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加のショートカット:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FileBox"; Filename: "{app}\FileBox.exe"
Name: "{autodesktop}\FileBox"; Filename: "{app}\FileBox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FileBox.exe"; Description: "FileBox を起動する"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; エクスプローラーの置き換え（フォルダを開く・Win + E）を変更前の値に戻す。消すと開けなくなるので、ファイルより先に行う
Filename: "{app}\FileBox.exe"; Parameters: "--unregister-shell"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterShell"
; 実行中だとファイルを消せないため、アンインストール前に終了させる
Filename: "{sys}\taskkill.exe"; Parameters: "/IM FileBox.exe /F"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM FileBox.Indexer.exe /F"; Flags: runhidden; RunOnceId: "StopIndexer"

[Registry]
; 本体の設定で任意登録した独自動詞だけを解除する。
Root: HKCU; Subkey: "Software\Classes\*\shell\FileBox.Tools"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FileBox.Tools"; Flags: uninsdeletekey dontcreatekey

; ユーザー設定（%LOCALAPPDATA%\FileBox）と共有データ（%ProgramData%\FileBox）はアンインストールで消さない
