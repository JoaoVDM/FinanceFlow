$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$environmentPath = Join-Path $projectRoot '.env'
if (Test-Path -LiteralPath $environmentPath) { throw '.env already exists; no secrets or certificates were overwritten.' }
$certificateDirectory = Join-Path $projectRoot '.local/certs'
$null = New-Item -ItemType Directory -Path $certificateDirectory -Force
if (Test-Path -LiteralPath (Join-Path $certificateDirectory 'api.pfx')) { throw 'Local certificate already exists; inspect it before generating new credentials.' }
function New-RandomValue {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes); return [Convert]::ToBase64String($bytes) } finally { $generator.Dispose() }
}
$certificatePassword = New-RandomValue
# Windows PowerShell 5.1: an ephemeral CNG key, without certificate-store changes.
$rsa = [Security.Cryptography.RSACng]::new(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $names.AddDnsName('localhost')
    $names.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($names.Build())
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(30))
    try {
        [IO.File]::WriteAllBytes((Join-Path $certificateDirectory 'api.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $certificatePassword))
        $pem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert), [Base64FormattingOptions]::InsertLineBreaks) + "`n-----END CERTIFICATE-----`n"
        [IO.File]::WriteAllText((Join-Path $certificateDirectory 'api.crt'), $pem, [Text.UTF8Encoding]::new($false))
    } finally { $certificate.Dispose() }
} finally { $rsa.Dispose() }
$content = @(
    "MSSQL_SA_PASSWORD=$(New-RandomValue)aA1!",
    "MIGRATION_DB_PASSWORD=$(New-RandomValue)aA1!",
    "API_DB_PASSWORD=$(New-RandomValue)aA1!",
    "JWT_SIGNING_KEY_BASE64=$(New-RandomValue)",
    "HTTPS_CERTIFICATE_PASSWORD=$certificatePassword"
) -join "`n"
[IO.File]::WriteAllText($environmentPath, $content + "`n", [Text.UTF8Encoding]::new($false))
Write-Output 'Local secrets and a 30-day localhost certificate generated. No certificate store or trust settings changed.'
