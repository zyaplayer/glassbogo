param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$TestReportDirectory,
    [Parameter(Mandatory)][string]$WallpaperDirectory,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/releases')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$reportPath = Join-Path (Resolve-Path -LiteralPath $TestReportDirectory).Path 'glass-lab-report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath (Join-Path $publish 'GlassBogo.dll') -Algorithm SHA256).Hash
if ($report.status -ne 'PASS' -or $report.assemblySha256 -ne $hash) { throw '实验包与通过隔离检查的构建不一致。' }
if ($report.stage -ne 'P0MaterialPrototype' -or $report.visualGate -ne 'NOT_PASSED_NO_REAL_DESKTOP_EVIDENCE') { throw '该脚本仅打包尚待人工验收的 P0 原型。' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = 'GlassBogo-V2-MaterialLab-P0-' + $hash.Substring(0,12)
$archive = Join-Path $output ($name + '.zip')
$manifest = Join-Path $output ($name + '.json')
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $manifest)) { throw '同一原型已打包，请勿覆盖。' }
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.txt') -Destination $publish
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $publish -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $publish -Force
New-Item -ItemType Directory -Path (Join-Path $publish 'wallpapers') -Force | Out-Null
foreach ($file in @('light.png','dark.png','saturated.png')) {
    Copy-Item -LiteralPath (Join-Path $WallpaperDirectory $file) -Destination (Join-Path $publish 'wallpapers') -Force
}
@'
@echo off
start "" "%~dp0GlassBogo.exe" --glass-lab
'@ | Set-Content -LiteralPath (Join-Path $publish 'Open Glass Lab.cmd') -Encoding ascii
@'
@echo off
start "" "%~dp0GlassBogo.exe" --glass-lab --compat
'@ | Set-Content -LiteralPath (Join-Path $publish 'Open Compatibility Lab.cmd') -Encoding ascii
Copy-Item -LiteralPath $reportPath -Destination (Join-Path $publish 'isolated-prototype-report.json') -Force
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $publish -Force | Select-Object -ExpandProperty FullName) -DestinationPath $archive -CompressionLevel Optimal
[ordered]@{
    stage = 'P0 material lab only; production V2 integration pending'
    dllSha256 = $hash
    archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    checks = $report.checks.Count
    materialConfigurations = $report.matrix.Count
    visualGate = $report.visualGate
    productionPerformanceGate = 'Not measured for final V2; old V1 metrics not reused'
    automaticDesktopInteraction = 'Isolated DesktopHarness only'
} | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
Write-Output $archive
Write-Output $manifest
