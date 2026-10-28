param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & $DotNetPath publish src/GlassBogo.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw '应用构建失败' }
    & $DotNetPath publish samples/FocusPlugin -c Release -r win-x64 --self-contained true -o artifacts/sample-plugin
    if ($LASTEXITCODE -ne 0) { throw '示例插件构建失败' }
    Copy-Item -LiteralPath samples/FocusPlugin/plugin.json -Destination artifacts/sample-plugin/plugin.json -Force
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.txt -Destination artifacts/publish -Force
    Copy-Item -LiteralPath licenses -Destination artifacts/publish -Recurse -Force
    Copy-Item -LiteralPath README.md -Destination artifacts/publish -Force
    Copy-Item -LiteralPath docs -Destination artifacts/publish -Recurse -Force
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.txt -Destination artifacts/sample-plugin -Force
    Copy-Item -LiteralPath licenses -Destination artifacts/sample-plugin -Recurse -Force
} finally { Pop-Location }
