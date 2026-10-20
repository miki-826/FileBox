# ローカルと GitHub Actions で同じ開発プレビュー ZIP を作る。
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        dotnet test FileBox.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'テストに失敗しました。' }
        dotnet build src/FileBox.App -c Release -p:Platform=x64 -r win-x64 -p:SelfContained=true
        if ($LASTEXITCODE -ne 0) { throw 'アプリのビルドに失敗しました。' }
    }

    $appOutput = Join-Path $repoRoot 'src/FileBox.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64'
    $requiredFiles = @(
        'FileBox.exe', 'FileBox.dll', 'FileBox.deps.json', 'FileBox.runtimeconfig.json',
        'Microsoft.UI.Xaml.dll', 'resources.pri', 'Assets/Strings/en.json',
        'FileBox.Tools.dll', 'PdfSharp.dll', 'System.Security.Cryptography.Pkcs.dll', 'THIRD-PARTY-NOTICES.txt',
        'indexer/FileBox.Indexer.exe', 'indexer/FileBox.Indexer.dll',
        'indexer/FileBox.Indexer.deps.json', 'indexer/FileBox.Indexer.runtimeconfig.json'
        'hostfxr.dll', 'coreclr.dll', 'indexer/hostfxr.dll', 'indexer/coreclr.dll'
    )
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $appOutput $file) -PathType Leaf)) {
            throw "配布に必要なファイルがありません: $file"
        }
    }
    $revision = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git rev-parse HEAD) }
    $notice = @"
FileBox 開発プレビュー（ビルド元: $revision）
開発中の試作版です。製品版ではありません。

使い方:
1. ZIP の中身をすべて同じフォルダに展開してください。
2. FileBox.exe を起動してください。indexer フォルダも必要です。
簡単な導入: リリースページの FileBox-Installer-preview-win-x64.exe は本体同梱のインストーラーです。
旧 FileBox-Setup.exe の作成・配布は停止しました。検出されたファイルの復元・許可はしないでください。

動作環境: Windows 10 1809 以降 / Windows 11（x64）
.NET 8 と Windows App SDK は同梱されています。別途インストールする必要はありません。

初回起動時に検索用インデックスを作成します。
エクスプローラー置き換えは既定でオフです。
ファイルの変換・整理は一覧の操作バーから開きます。外部エンジンは設定で指定してください。
FFmpeg・ImageMagick・7-Zip・Java/CFR は同梱していません。PDF 結合・Windows 標準の OCR と PDF 画像化・データ変換は別途のエンジン指定なしで利用できます。
更新するときは FileBox と FileBox.Indexer を終了してから展開してください。
"@
    Set-Content -LiteralPath (Join-Path $appOutput 'PREVIEW.txt') -Value $notice -Encoding utf8
    $dist = Join-Path $repoRoot 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist 'FileBox-preview-win-x64.zip'
    Compress-Archive -Path "$appOutput/*" -DestinationPath $zip -Force

    # ZIP の中にも実行に必要なファイルが入っていることを確認する。
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($file in ($requiredFiles + 'PREVIEW.txt')) {
            if ($file -notin $entries) { throw "ZIP に必要なファイルがありません: $file" }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  FileBox-preview-win-x64.zip" -Encoding ascii
    Write-Host "配布用 ZIP: $zip"
} finally { Pop-Location }
