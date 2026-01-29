$ErrorActionPreference = "Stop"

$subscriptionId = "fc30746c-e06a-42e3-98ab-f7d74ab3b360"
$rg       = "rg-ara-ai-dev"
$funcName = "ara-mcp-dev-func-8631"

# Root folder of your repo
$solutionRoot = "C:\users\vinicio.flores\source\repos\ComposioProxyFunctions"

# Where to publish
$publishDir = Join-Path $solutionRoot "publish"
$zipPath    = Join-Path $solutionRoot "publish.zip"

# ---------- Prechecks ----------
if (-not (Test-Path $solutionRoot)) {
  throw "solutionRoot not found: $solutionRoot"
}

# Find the first csproj that matches the proxy name (or fallback to any csproj)
$csproj = Get-ChildItem -Path $solutionRoot -Recurse -Filter "*.csproj" |
  Where-Object { $_.Name -match "AraComposioMcpProxy|ComposioMcpProxy|McpProxy" } |
  Select-Object -First 1

if (-not $csproj) {
  $csproj = Get-ChildItem -Path $solutionRoot -Recurse -Filter "*.csproj" | Select-Object -First 1
}

if (-not $csproj) {
  throw "No .csproj found under: $solutionRoot"
}

Write-Host "Using project: $($csproj.FullName)" -ForegroundColor Yellow

# ---------- Azure context ----------
az account set --subscription $subscriptionId | Out-Null

# ---------- dotnet publish ----------
Write-Host "🔧 dotnet publish..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $csproj.FullName -c Release -o $publishDir

if (-not (Test-Path $publishDir)) {
  throw "Publish directory not created: $publishDir"
}

# ---------- ZIP ----------
Write-Host "📦 Creating ZIP..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

# ---------- Deploy ----------
Write-Host "🚀 ZIP deploy to Function App..." -ForegroundColor Cyan
az functionapp deployment source config-zip `
  -g $rg -n $funcName --src $zipPath | Out-Null

Write-Host "🔄 Restarting Function App..." -ForegroundColor Cyan
az functionapp restart -g $rg -n $funcName | Out-Null

Write-Host "✅ ZIP deploy completed." -ForegroundColor Green
Write-Host "Tip: tail logs -> az functionapp log tail -g $rg -n $funcName" -ForegroundColor Gray
