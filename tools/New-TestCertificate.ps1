param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.certs')
)

$ErrorActionPreference = 'Stop'

$pfxPath = Join-Path $OutputDirectory 'SerialPortSimulatorTest.pfx'
$cerPath = Join-Path $OutputDirectory 'SerialPortSimulatorTest.cer'
$passwordPath = Join-Path $OutputDirectory 'password.txt'

if (Test-Path -LiteralPath $pfxPath) {
    if (!(Test-Path -LiteralPath $passwordPath)) {
        throw "Test certificate exists but its password file is missing: $passwordPath"
    }
    if (!(Test-Path -LiteralPath $cerPath)) {
        $password = (Get-Content -LiteralPath $passwordPath -Raw).Trim()
        $certificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
            $pfxPath, $password)
        [System.IO.File]::WriteAllBytes(
            $cerPath,
            $certificate.Export(
                [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    Write-Host "Using test certificate: $pfxPath"
    return
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$password = [Convert]::ToBase64String($bytes)
[System.IO.File]::WriteAllText($passwordPath, $password)

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=SerialPortSimulator Test Driver',
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
            $false, $false, 0, $true))
    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $eku = [System.Security.Cryptography.OidCollection]::new()
    [void]$eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
            $request.PublicKey, $false))

    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddDays(-1),
        [DateTimeOffset]::UtcNow.AddYears(10))
    [System.IO.File]::WriteAllBytes(
        $pfxPath,
        $certificate.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
            $password))
    [System.IO.File]::WriteAllBytes(
        $cerPath,
        $certificate.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "Created test certificate: $cerPath ($($certificate.Thumbprint))"
}
finally {
    $rsa.Dispose()
}
