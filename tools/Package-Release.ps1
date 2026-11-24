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
$testClientRoot = Join-Path $appRoot 'TestClient'
[string[]]$restoreOption = @()
if ($NoRestore) { $restoreOption = @('--no-restore') }

if (!$SkipDriverBuild) {
    & (Join-Path $PSScriptRoot 'Build-Driver.ps1') -Configuration Debug -TestSign
}

$driverOutput = Join-Path $repoRoot 'VirtualSerial\x64\Debug\VirtualSerial2um'
$catalogPath = Join-Path $driverOutput 'wudf.cat'
$certificatePath = Join-Path $repoRoot 'VirtualSerial\x64\Debug\VirtualSerial2um.cer'
if (!(Test-Path -LiteralPath $catalogPath)) {
    throw "Driver catalog is missing: $catalogPath"
}
$signature = Get-AuthenticodeSignature -LiteralPath $catalogPath
if (!$signature.SignerCertificate) {
    throw 'Driver catalog is not signed. Run .\tools\Build-Driver.ps1 -Configuration Debug -TestSign first.'
}
if (!(Test-Path -LiteralPath $certificatePath)) {
    throw "Driver certificate is missing: $certificatePath"
}
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path -LiteralPath $certificatePath).Path)
if ($certificate.Thumbprint -ne $signature.SignerCertificate.Thumbprint) {
    throw 'Driver catalog signer does not match the packaged certificate.'
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
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
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
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=true `
    -p:InvariantGlobalization=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $managerRoot
if ($LASTEXITCODE -ne 0) { throw 'Device manager publish failed.' }

dotnet publish (Join-Path $repoRoot 'TestClient\SerialPortSimulator.TestClient.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    @restoreOption `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $testClientRoot
if ($LASTEXITCODE -ne 0) { throw 'Test client publish failed.' }

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.zh-CN.md') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.zh-CN.md') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.zh-CN.md') -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $appRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'VirtualSerial\LICENSE.microsoft.txt') -Destination $appRoot -Force
$virtualSerialLicenseDir = Join-Path $appRoot 'VirtualSerial'
New-Item -ItemType Directory -Path $virtualSerialLicenseDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'VirtualSerial\LICENSE.microsoft.txt') `
    -Destination (Join-Path $virtualSerialLicenseDir 'LICENSE.microsoft.txt') -Force

$zipPath = Join-Path $distRoot "$packageName.zip"
Compress-Archive -Path $appRoot -DestinationPath $zipPath -Force
Write-Output "Package: $zipPath"
