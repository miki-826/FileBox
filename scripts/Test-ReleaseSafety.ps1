# 配布対象を Defender で検査し、検出・検査不能なら公開を止める。
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [switch]$UpdateSignatures
)

$ErrorActionPreference = 'Stop'
$reportFile = [IO.Path]::GetFullPath($ReportPath)
# 前回の成功記録を再利用させない。
if (Test-Path -LiteralPath $reportFile) { Remove-Item -LiteralPath $reportFile -Force }
$targets = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_ -ErrorAction Stop).Path })
$status = Get-MpComputerStatus -ErrorAction Stop
if (-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled) {
    throw 'Microsoft Defender の検査を利用できないため、配布を中止します。'
}
$platform = Join-Path $env:ProgramData 'Microsoft/Windows Defender/Platform'
$scanner = Get-ChildItem -LiteralPath $platform -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $scanner) { $scanner = Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe' }
if (-not (Test-Path -LiteralPath $scanner -PathType Leaf)) { throw 'Defender の検査プログラムが見つかりません。' }
if ($UpdateSignatures) {
    & $scanner -SignatureUpdate
    if ($LASTEXITCODE -ne 0) { throw 'Defender の定義を更新できないため、配布を中止します。' }
    $status = Get-MpComputerStatus -ErrorAction Stop
}
$results = @()
foreach ($target in $targets) {
    Write-Host "Defender で検査: $target"
    # この検査では削除せず結果を取得する。除外設定を無視し、アーカイブ内も検査する。
    # リアルタイム保護・クラウド保護などの設定は変更しない。
    & $scanner -Scan -ScanType 3 -File $target -DisableRemediation
    if ($LASTEXITCODE -ne 0) { throw "検出またはスキャンエラーのため、配布を中止します: $target" }
    $entry = Get-Item -LiteralPath $target -ErrorAction Stop
    $hash = if (-not $entry.PSIsContainer) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    $results += [ordered]@{ name = $entry.Name; sha256 = $hash; result = 'no-threats-detected' }
}
$report = [ordered]@{
    scannedAtUtc = [DateTime]::UtcNow.ToString('o')
    commit = $env:GITHUB_SHA
    scanner = 'Microsoft Defender Antivirus'
    signatureVersion = $status.AntivirusSignatureVersion
    signatureUpdatedAtUtc = $status.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o')
    targets = $results
    note = '検査時点の結果です。将来の検出や他の保護製品による判定を保証しません。'
}
New-Item -ItemType Directory -Force -Path (Split-Path $reportFile -Parent) | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportFile -Encoding utf8
Write-Host "配布前スキャンに成功しました: $reportFile"
