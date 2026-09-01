$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ArcSpace\ArcSpace.csproj'
$output = Join-Path $repoRoot 'dist\win-x64'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $output

Write-Host ""
Write-Host "ArcSpace portable build: $output\ArcSpace.exe"
