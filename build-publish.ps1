# BladeCtl reproducible build.
#   .\build-publish.ps1                    -> framework-dependent single file into publish-fd (needs .NET 8 Desktop Runtime)
#   .\build-publish.ps1 -SelfContained     -> self-contained into publish (runs on a clean PC)
#   .\build-publish.ps1 -Out <dir>         -> publish somewhere else (used while the live copy is running and locked)
param([switch]$SelfContained, [string]$Out)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\BladeCtl.Tray\BladeCtl.Tray.csproj'
$probe = Join-Path $root 'src\BladeProbe\BladeProbe.csproj'

if (-not $Out) { $Out = if ($SelfContained) { Join-Path $root 'publish' } else { Join-Path $root 'publish-fd' } }

$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish $proj -c Release -r win-x64 -p:SelfContained=$sc -p:PublishSingleFile=true -o $Out
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

# Emergency / verification console tool rides along in tools\.
$tools = Join-Path $Out 'tools'
dotnet publish $probe -c Release -r win-x64 -p:SelfContained=false -o $tools
if ($LASTEXITCODE -ne 0) { throw "probe publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'README.md') (Join-Path $Out 'README.md') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'analog-test.cmd') (Join-Path $Out 'analog-test.cmd') -Force -ErrorAction SilentlyContinue

$exe = Join-Path $Out 'BladeCtl.exe'
$mb = [math]::Round((Get-Item $exe).Length/1MB, 2)
Write-Host "Built $exe ($mb MB)" -ForegroundColor Green
