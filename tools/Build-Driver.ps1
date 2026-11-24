param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$TestSign)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$wdkInstance = & $locator -latest -products '*' -requires Component.Microsoft.Windows.DriverKit -property installationPath | Select-Object -First 1
if (!$wdkInstance) {
    throw 'Windows Driver Kit (WDK) is not installed for Visual Studio. Open Visual Studio Installer, add the "Windows Driver Kit (WDK)" component (or import tools\driver-build.vsconfig), then rerun this script.'
}
$msbuildPath = & $locator -latest -products '*' -requires Component.Microsoft.Windows.DriverKit -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (!$msbuildPath) { throw 'Visual Studio MSBuild with the WDK component was not found.' }
$nugetPath = Join-Path $PSScriptRoot 'nuget.exe'
if (!(Test-Path $nugetPath)) {
    Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nugetPath
}
& $nugetPath restore (Join-Path $repoRoot 'VirtualSerial\packages.config') -PackagesDirectory (Join-Path $repoRoot 'VirtualSerial\packages') -NonInteractive -Source 'https://api.nuget.org/v3/index.json'
if ($LASTEXITCODE -ne 0) { throw 'WDK NuGet restore failed.' }
& $msbuildPath (Join-Path $repoRoot 'VirtualSerial\VirtualSerial2um.vcxproj') "/p:Configuration=$Configuration" /p:Platform=x64 /p:PreferredToolArchitecture=x64 /p:SignMode=Off /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Driver build failed.' }

if ($TestSign) {
    & (Join-Path $PSScriptRoot 'Sign-Driver.ps1') -Configuration $Configuration
}
