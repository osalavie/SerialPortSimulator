$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

$device = Get-PnpDevice -PresentOnly |
    Where-Object { $_.HardwareID -contains 'ROOT\SerialPortSimulator' } |
    Select-Object -First 1
if (!$device) {
    throw 'No Serial Port Simulator device is installed.'
}

dotnet run --project (Join-Path $repoRoot 'DriverSmoke\DriverSmoke.csproj')
if ($LASTEXITCODE -ne 0) {
    throw 'Virtual serial end-to-end test failed.'
}
