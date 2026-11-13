param([uri]$BaseUrl = 'https://localhost:5443')
$ErrorActionPreference = 'Stop'
if ($BaseUrl.Scheme -ne 'https' -or -not $BaseUrl.IsLoopback) { throw 'Validation requires a loopback HTTPS address.' }
$null = Get-Command curl.exe -ErrorAction Stop
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$certificatePath = Join-Path $projectRoot '.local/certs/api.crt'
if (-not (Test-Path -LiteralPath $certificatePath)) { throw 'Generate the local environment first.' }

function ConvertTo-CurlValue([string]$Value) {
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function Send-Api([string]$Method, [string]$Path, [int]$ExpectedStatus, $Body = $null, [string]$Token = '') {
    $configLines = @(
        ('url = ' + (ConvertTo-CurlValue ($BaseUrl.AbsoluteUri.TrimEnd('/') + $Path))),
        ('request = ' + (ConvertTo-CurlValue $Method)),
        'header = "Accept: application/json"'
    )
    if ($Token) { $configLines += 'header = ' + (ConvertTo-CurlValue ('Authorization: Bearer ' + $Token)) }
    if ($null -ne $Body) {
        $configLines += 'header = "Content-Type: application/json"'
        $configLines += 'data = ' + (ConvertTo-CurlValue ($Body | ConvertTo-Json -Compress -Depth 8))
    }
    # Passwords and bearer tokens travel through stdin, not process arguments or files.
    $responseLines = ($configLines -join "`n") | & curl.exe --disable --config - --silent --show-error --cacert $certificatePath --proto '=https' --connect-timeout 5 --max-time 30 --write-out "`n%{http_code}"
    if ($LASTEXITCODE -ne 0) { throw "HTTPS transport failed for $Method $Path; response content was not printed." }
    $response = $responseLines -join "`n"
    $separator = $response.LastIndexOf("`n")
    $status = [int]$response.Substring($separator + 1)
    if ($status -ne $ExpectedStatus) { throw "$Method $Path expected $ExpectedStatus, received $status; response content was not printed." }
    Write-Host "$Method $Path -> $status"
    return $response.Substring(0, $separator)
}

function New-FictionalPassword {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes); return [Convert]::ToBase64String($bytes) + 'Aa1!' } finally { $generator.Dispose() }
}

$email = 'validation-' + [guid]::NewGuid().ToString('N') + '@example.invalid'
$password = New-FictionalPassword
$newPassword = New-FictionalPassword
$token = $null
try {
    foreach ($path in @('/health', '/health/live')) {
        $health = Send-Api GET $path 200 | ConvertFrom-Json
        if ($health.status -ne 'healthy') { throw 'Unexpected liveness body.' }
    }
    $ready = Send-Api GET '/health/ready' 200 | ConvertFrom-Json
    if ($ready.status -ne 'ready') { throw 'Unexpected readiness body.' }
    $openApi = Send-Api GET '/openapi/v1.json' 200 | ConvertFrom-Json
    if (-not $openApi.openapi) { throw 'OpenAPI document is missing its version.' }
    $swagger = Send-Api GET '/swagger/index.html' 200
    if ($swagger -notmatch '(?i)swagger') { throw 'Swagger UI response is invalid.' }
    $null = Send-Api GET '/api/v1/users/me' 401
    $null = Send-Api POST '/api/v1/auth/register' 201 @{ displayName = 'Fictional Validation'; email = $email; password = $password }
    $login = Send-Api POST '/api/v1/auth/login' 200 @{ email = $email; password = $password } | ConvertFrom-Json
    if ($login.expiresIn -ne 900 -or $login.tokenType -ne 'Bearer') { throw 'Unexpected token response contract.' }
    $token = $login.accessToken
    $null = Send-Api GET '/api/v1/users/me' 200 -Token $token
    $null = Send-Api PATCH '/api/v1/users/me' 204 @{ displayName = 'Fictional Updated' } $token
    $profile = Send-Api GET '/api/v1/users/me' 200 -Token $token | ConvertFrom-Json
    if ($profile.displayName -ne 'Fictional Updated') { throw 'Profile update was not persisted.' }
    $parts = $token.Split('.')
    $replacement = if ($parts[2][0] -eq 'A') { 'B' } else { 'A' }
    $tampered = $parts[0] + '.' + $parts[1] + '.' + $replacement + $parts[2].Substring(1)
    $null = Send-Api GET '/api/v1/users/me' 401 -Token $tampered
    $null = Send-Api POST '/api/v1/users/me/change-password' 204 @{ currentPassword = $password; newPassword = $newPassword } $token
    $null = Send-Api GET '/api/v1/users/me' 401 -Token $token
    $null = Send-Api POST '/api/v1/auth/login' 401 @{ email = $email; password = $password }
    $login = Send-Api POST '/api/v1/auth/login' 200 @{ email = $email; password = $newPassword } | ConvertFrom-Json
    $token = $login.accessToken
    $null = Send-Api POST '/api/v1/auth/logout' 204 -Token $token
    $null = Send-Api GET '/api/v1/users/me' 401 -Token $token
    $login = Send-Api POST '/api/v1/auth/login' 200 @{ email = $email; password = $newPassword } | ConvertFrom-Json
    $token = $login.accessToken
    $null = Send-Api DELETE '/api/v1/users/me' 204 @{ currentPassword = $newPassword } $token
    $null = Send-Api GET '/api/v1/users/me' 401 -Token $token
    $null = Send-Api POST '/api/v1/auth/login' 401 @{ email = $email; password = $newPassword }
    Write-Host 'HTTPS checks completed. One fictional user was retained as inactive; no financial data or physical deletions.'
}
finally {
    $password = $null; $newPassword = $null; $token = $null
    $login = $null; $parts = $null; $tampered = $null
}
