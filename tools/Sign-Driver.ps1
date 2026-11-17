param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$CertificateDirectory = (Join-Path $PSScriptRoot '.certs')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$driverOutput = Join-Path $repoRoot "VirtualSerial\x64\$Configuration\VirtualSerial2um"
$catalogPath = Join-Path $driverOutput 'wudf.cat'
$embeddedCertificatePath = Join-Path $driverOutput 'VirtualSerial2um.cer'
$projectCertificatePath = Join-Path (Split-Path $driverOutput -Parent) 'VirtualSerial2um.cer'

if (!(Test-Path -LiteralPath $catalogPath)) {
    throw "Driver catalog was not generated: $catalogPath"
}

& (Join-Path $PSScriptRoot 'New-TestCertificate.ps1') -OutputDirectory $CertificateDirectory
$pfxPath = Join-Path $CertificateDirectory 'SerialPortSimulatorTest.pfx'
$certificatePath = Join-Path $CertificateDirectory 'SerialPortSimulatorTest.cer'
$passwordPath = Join-Path $CertificateDirectory 'password.txt'
$password = (Get-Content -LiteralPath $passwordPath -Raw).Trim()

$signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') `
    -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1
if (!$signtool) {
    $signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') `
        -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
}
if (!$signtool) {
    throw 'signtool.exe was not found in the Windows Kits installation.'
}

& $signtool.FullName sign /f $pfxPath /p $password /fd SHA256 $catalogPath
if ($LASTEXITCODE -ne 0) {
    throw "Catalog signing failed with exit code $LASTEXITCODE."
}

$signature = Get-AuthenticodeSignature -LiteralPath $catalogPath
if (!$signature.SignerCertificate) {
    throw "The catalog is still unsigned: $catalogPath"
}
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path -LiteralPath $certificatePath).Path)
if ($certificate.Thumbprint -ne $signature.SignerCertificate.Thumbprint) {
    throw 'The catalog signer does not match the certificate that will be packaged.'
}

Copy-Item -LiteralPath $certificatePath -Destination $embeddedCertificatePath -Force
Copy-Item -LiteralPath $certificatePath -Destination $projectCertificatePath -Force
Write-Host "Signed catalog: $catalogPath"
Write-Host "Embedded certificate: $embeddedCertificatePath ($($signature.SignerCertificate.Thumbprint))"
