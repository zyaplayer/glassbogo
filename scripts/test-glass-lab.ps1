param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $stamp = Get-Date -Format yyyyMMdd-HHmmss
    $publish = Join-Path $repo "artifacts/glass-lab-$stamp"
    $output = Join-Path $repo "artifacts/glass-lab-qa-$stamp"
    & $DotNetPath publish src/GlassBogo.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -warnaserror -o $publish
    if ($LASTEXITCODE -ne 0) { throw '材质实验构建失败' }
    & $DotNetPath build tests/GlassBogo.DesktopHarness -c Release -o artifacts/glass-lab-harness
    if ($LASTEXITCODE -ne 0) { throw '隔离桌面宿主构建失败' }
    & $DotNetPath artifacts/glass-lab-harness/GlassBogo.DesktopHarness.dll $publish/GlassBogo.exe $output 10 --glass-lab-qa
    if ($LASTEXITCODE -ne 0) { throw "材质实验自动检查失败：$output" }
    $report = Get-Content -LiteralPath "$output/glass-lab-report.json" -Raw | ConvertFrom-Json
    $hash = (Get-FileHash -LiteralPath "$publish/GlassBogo.dll" -Algorithm SHA256).Hash
    if ($report.status -ne 'PASS' -or $report.assemblySha256 -ne $hash) { throw '测试状态或构建指纹不匹配' }
    Write-Output "实验程序：$publish/GlassBogo.exe --glass-lab"
    Write-Output "隔离检查：$output/glass-lab-report.json"
    Write-Output '当前仅完成自动检查；没有通过真实桌面视觉验收。'
} finally { Pop-Location }
