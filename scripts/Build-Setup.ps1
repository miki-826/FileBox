# Windows 標準の .NET Framework で動く小さなダウンローダーを作る。
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework の C# コンパイラが見つかりません。' }
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$output = Join-Path $dist 'FileBox-Setup.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
    (Join-Path $repoRoot 'src/FileBox.Setup/Program.cs') (Join-Path $repoRoot 'src/FileBox.Setup/Package.cs')
if ($LASTEXITCODE -ne 0) { throw 'ダウンローダーのビルドに失敗しました。' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$output.sha256" -Value "$hash  FileBox-Setup.exe" -Encoding ascii
Write-Host "ダウンローダー: $output"
