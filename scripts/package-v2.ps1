param([Parameter(Mandatory)][string]$ReportDirectory, [string]$PreviousPackage = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'artifacts/v2-ui-publish'
$reportDirectoryFull = [IO.Path]::GetFullPath($ReportDirectory)
$report = Get-Content -LiteralPath (Join-Path $reportDirectoryFull 'report.json') -Raw | ConvertFrom-Json
$events = Get-Content -LiteralPath (Join-Path $reportDirectoryFull 'events.log') -Raw
$dll = Join-Path $publish 'GlassBogo.dll'
$fingerprint = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
if ($report.assemblySha256 -ne $fingerprint) { throw '发布 DLL 与报告指纹不一致，不能打包。' }
if ($report.idleSeconds -lt 120 -or $report.stressSeconds -lt 1800) { throw '必须先完成本构建两分钟待机和三十分钟耐久。' }
if ($report.assertions -lt 32 -or $report.baselineWindows -ne $report.finalWindows -or $report.cleanupWindows -ne 0 -or $events -match '\bFAIL\b' -or $events -notmatch 'PASS completed') { throw '隔离验证未通过。' }
$frozen = Join-Path $repo 'artifacts/v2-frozen-source-hashes.txt'
foreach ($line in (Get-Content -LiteralPath $frozen)) {
    if ($line -notmatch '^(.*) ([A-F0-9]{64})$') { throw '源码校验记录无效。' }
    $sourcePath = Join-Path $repo $Matches[1]
    $sourceHash = $Matches[2]
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $sourceHash) { throw "测试后源码已改变：$sourcePath" }
}
$destination = Join-Path $repo ('artifacts/delivery-v2-' + (Get-Date -Format yyyyMMdd-HHmmss))
$program = Join-Path $destination 'program'
$validation = Join-Path $destination 'validation'
New-Item -ItemType Directory -Path $program,$validation | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $program -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'README.md'),(Join-Path $repo 'THIRD-PARTY-NOTICES.txt') -Destination $program -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md'),(Join-Path $PSScriptRoot 'install.ps1') -Destination $destination
Copy-Item -LiteralPath (Join-Path $reportDirectoryFull 'report.json'),(Join-Path $reportDirectoryFull 'events.log') -Destination $validation
Copy-Item -LiteralPath (Join-Path $repo 'artifacts/v2-service-tests.log') -Destination $validation
Copy-Item -LiteralPath (Join-Path $repo 'artifacts/v2-solution-build.log') -Destination $validation
Copy-Item -LiteralPath $frozen -Destination $validation
if (Test-Path -LiteralPath (Join-Path $repo 'artifacts/v2-packaging-checks.log')) { Copy-Item -LiteralPath (Join-Path $repo 'artifacts/v2-packaging-checks.log') -Destination $validation }
$layouts = Join-Path $validation 'layout-only'
New-Item -ItemType Directory -Path $layouts | Out-Null
foreach ($name in @('dock','category','entries','categories','settings')) {
    Copy-Item -LiteralPath (Join-Path $reportDirectoryFull ($name + '.png')) -Destination $layouts
}
Set-Content -LiteralPath (Join-Path $layouts 'README.txt') -Encoding utf8 -Value 'Generated fixture layout snapshots with opaque material. These images are not live desktop captures or evidence of final glass visual acceptance.'
if (Test-Path -LiteralPath (Join-Path $reportDirectoryFull 'native-metrics')) { Copy-Item -LiteralPath (Join-Path $reportDirectoryFull 'native-metrics') -Destination $validation -Recurse }
if ($PreviousPackage) {
    $previous = [IO.Path]::GetFullPath($PreviousPackage)
    $previousInfo = Get-Content -LiteralPath ($previous + '.json') -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash -ne $previousInfo.zipSha256) { throw '上一版本包指纹校验失败。' }
    $rollback = Join-Path $destination 'rollback'
    New-Item -ItemType Directory -Path $rollback | Out-Null
    Copy-Item -LiteralPath $previous,($previous + '.json') -Destination $rollback
}
Set-Content -LiteralPath (Join-Path $destination 'Install-GlassBogo.ps1') -Encoding utf8 -Value @'
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'install.ps1') -PublishDirectory (Join-Path $PSScriptRoot 'program')
'@
Push-Location $repo
try {
    $head = & git rev-parse HEAD
    $status = & git status --porcelain
    $sources = & git ls-files --cached --others --exclude-standard
    $sourceManifest = foreach ($file in $sources) {
        $path = Join-Path $repo $file
        if (Test-Path -LiteralPath $path -PathType Leaf) { [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } }
    }
} finally { Pop-Location }
$sourceManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $validation 'source-manifest.json') -Encoding utf8
$metadata = [ordered]@{
    product = 'GlassBogo 2.0 phase 1 preview'
    status = 'MANUAL_ACCEPTANCE_PENDING'
    assemblySha256 = $fingerprint
    gitHead = $head
    workingTreeDirty = [bool]$status
    createdUtc = [DateTime]::UtcNow.ToString('O')
    validation = 'Generated data and synthetic GPU source, isolated desktop; not a live capture performance acceptance.'
    remaining = @('Real Win+D/taskbar show-desktop and Explorer restart', 'New product glass on static/dynamic backgrounds and readability', 'Physical DPI/resolution changes and search focus restoration', 'Real capture performance and budget review')
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'build.json') -Encoding utf8
$files = Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName
$manifest = foreach ($file in $files) { [ordered]@{ path = [IO.Path]::GetRelativePath($destination,$file.FullName); bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash } }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'files.json') -Encoding utf8
Write-Output "预览交付目录：$destination"
Write-Output "DLL SHA256：$fingerprint"
