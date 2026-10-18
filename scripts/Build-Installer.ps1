# 本体を同梱したインストーラー EXE を Inno Setup で作る。
# -SourceDir を省略すると、ビルド済みの本体（Release・x64）を使う。
param([string]$SourceDir)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $SourceDir) {
    $SourceDir = Join-Path $repoRoot 'src/FileBox.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64'
}
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
foreach ($file in 'FileBox.exe', 'indexer/FileBox.Indexer.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $file) -PathType Leaf)) {
        throw "インストーラーに入れる本体が見つかりません: $file"
    }
}

function Find-Iscc {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($root in ${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) {
        if (-not $root) { continue }
        $path = Join-Path $root 'Inno Setup 6/ISCC.exe'
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc -and $env:CI) {
    # GitHub Actions のランナーに無い場合だけ取得する
    choco install innosetup -y --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup のインストールに失敗しました。' }
    $iscc = Find-Iscc
}
if (-not $iscc) { throw 'Inno Setup 6 が見つかりません。winget install JRSoftware.InnoSetup で導入してください。' }

$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$SourceDir" "/DOutputDir=$dist" (Join-Path $repoRoot 'installer/FileBox.iss')
if ($LASTEXITCODE -ne 0) { throw 'インストーラーのコンパイルに失敗しました。' }

$output = Join-Path $dist 'FileBox-Installer-preview-win-x64.exe'
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$output.sha256" -Value "$hash  FileBox-Installer-preview-win-x64.exe" -Encoding ascii
Write-Host "インストーラー: $output"
