param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd'),
    [switch]$SkipDriverBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$distRoot = Join-Path $repoRoot 'dist'
$packageName = "SerialPortSimulator-$Version-win-x64"
$appRoot = Join-Path $distRoot $packageName
$managerRoot = Join-Path $appRoot 'DeviceManager'
$businessRoot = Join-Path $appRoot 'BusinessClient'
[string[]]$restoreOption = @()
if ($NoRestore) { $restoreOption = @('--no-restore') }

if (!$SkipDriverBuild) {
    & (Join-Path $PSScriptRoot 'Build-Driver.ps1') -Configuration Debug -TestSign
}

if (Test-Path -LiteralPath $appRoot) {
    $resolvedDist = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'dist')).TrimEnd('\') + '\'
    $resolvedApp = (Resolve-Path -LiteralPath $appRoot).Path
    if (!$resolvedApp.StartsWith($resolvedDist, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a package outside dist: $resolvedApp"
    }
    Remove-Item -LiteralPath $resolvedApp -Recurse -Force
}
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null

dotnet publish (Join-Path $repoRoot 'SerialPortSimulator\SerialPortSimulator.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    @restoreOption `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $appRoot
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

dotnet publish (Join-Path $repoRoot 'DeviceManager\SerialPortSimulator.DeviceManager.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    @restoreOption `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=true `
    -p:TrimMode=link `
    -p:InvariantGlobalization=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $managerRoot
if ($LASTEXITCODE -ne 0) { throw 'Device manager publish failed.' }

dotnet publish (Join-Path $repoRoot 'BusinessClient\SerialPortSimulator.BusinessTest.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    @restoreOption `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $businessRoot
if ($LASTEXITCODE -ne 0) { throw 'Business test client publish failed.' }

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $appRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'VirtualSerial\LICENSE.microsoft.txt') -Destination $appRoot -Force

$zipPath = Join-Path $distRoot "$packageName.zip"
Compress-Archive -Path $appRoot -DestinationPath $zipPath -Force
Write-Output "Package: $zipPath"
