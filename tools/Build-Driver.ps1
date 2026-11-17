param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$TestSign)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuildPath = & $locator -latest -products '*' -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (!$msbuildPath) { throw 'Visual Studio MSBuild was not found.' }
$nugetPath = Join-Path $PSScriptRoot 'nuget.exe'
if (!(Test-Path $nugetPath)) {
    Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nugetPath
}
& $nugetPath restore (Join-Path $repoRoot 'VirtualSerial\packages.config') -PackagesDirectory (Join-Path $repoRoot 'VirtualSerial\packages') -NonInteractive -Source 'https://api.nuget.org/v3/index.json'
if ($LASTEXITCODE -ne 0) { throw 'WDK NuGet restore failed.' }
$signMode = if ($TestSign) { 'TestSign' } else { 'Off' }
& $msbuildPath (Join-Path $repoRoot 'VirtualSerial\VirtualSerial2um.vcxproj') "/p:Configuration=$Configuration" /p:Platform=x64 /p:PreferredToolArchitecture=x64 "/p:SignMode=$signMode" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Driver build failed.' }
