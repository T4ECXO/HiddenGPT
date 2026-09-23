$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'HiddenGPT.csproj'
$outputPath = Join-Path $PSScriptRoot 'artifacts\HiddenGPT-win-x64'

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $outputPath

Write-Host "Published HiddenGPT to $outputPath"
