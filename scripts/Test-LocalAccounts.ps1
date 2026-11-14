param([uri]$BaseUrl = 'https://localhost:5443')
$ErrorActionPreference = 'Stop'
if ($BaseUrl.Scheme -ne 'https' -or -not $BaseUrl.IsLoopback) { throw 'Use loopback HTTPS.' }
$null = Get-Command curl.exe -ErrorAction Stop
$certificate = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) '.local/certs/api.crt'
function Quote-AccountCurl([string]$Value) { return '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"' }
function Invoke-AccountCheck([string]$Method, [string]$Path, [int]$Expected, [string]$Json = '', [string]$Token = '', [string]$Key = '') {
    $config = @(
        ('url = ' + (Quote-AccountCurl ($BaseUrl.AbsoluteUri.TrimEnd('/') + $Path))),
        ('request = ' + (Quote-AccountCurl $Method))
    )
    if ($Json) { $config += 'header = "Content-Type: application/json"'; $config += 'data = ' + (Quote-AccountCurl $Json) }
    if ($Token) { $config += 'header = ' + (Quote-AccountCurl ('Authorization: Bearer ' + $Token)) }
    if ($Key) { $config += 'header = ' + (Quote-AccountCurl ('Idempotency-Key: ' + $Key)) }
    $lines = ($config -join "`n") | & curl.exe --disable --config - --silent --show-error --cacert $certificate --proto '=https' --connect-timeout 5 --max-time 30 --write-out "`n%{http_code}"
    if ($LASTEXITCODE -ne 0) { throw 'HTTPS transport failed; no credentials or response bodies were printed.' }
    $response = $lines -join "`n"; $separator = $response.LastIndexOf("`n"); $status = [int]$response.Substring($separator + 1)
    if ($status -ne $Expected) { throw "$Method $Path expected $Expected; received $status. Response body withheld." }
    Write-Host "$Method $Path -> $status"
    return $response.Substring(0, $separator)
}
function New-AccountCheckPassword {
    $bytes = New-Object byte[] 32; $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return [Convert]::ToBase64String($bytes) + 'Aa1!' } finally { $rng.Dispose() }
}
$password = New-AccountCheckPassword
$email = 'account-validation-' + [guid]::NewGuid().ToString('N') + '@example.invalid'
$token = $null
try {
    $null = Invoke-AccountCheck GET '/health/ready' 200
    $null = Invoke-AccountCheck GET '/api/v1/accounts' 401
    $register = @{ displayName = 'Fictional Account Validation'; email = $email; password = $password } | ConvertTo-Json -Compress
    $null = Invoke-AccountCheck POST '/api/v1/auth/register' 201 $register
    $loginJson = @{ email = $email; password = $password } | ConvertTo-Json -Compress
    $login = Invoke-AccountCheck POST '/api/v1/auth/login' 200 $loginJson | ConvertFrom-Json
    $token = $login.accessToken
    $key = [guid]::NewGuid().ToString('N')
    $payload = '{"name":"Fictional Account","type":"checking","currency":"BRL","openingBalance":10.000,"openingDate":"2000-01-01"}'
    $first = Invoke-AccountCheck POST '/api/v1/accounts' 201 $payload $token $key
    $again = Invoke-AccountCheck POST '/api/v1/accounts' 201 ($payload.Replace('10.000', '10.00')) $token $key
    if ($first -cne $again) { throw 'Replay changed the original JSON snapshot.' }
    $created = $first | ConvertFrom-Json
    $null = Invoke-AccountCheck GET ('/api/v1/accounts/' + $created.id) 200 -Token $token
    $page = Invoke-AccountCheck GET '/api/v1/accounts?page=1&pageSize=1&type=checking' 200 -Token $token | ConvertFrom-Json
    if ($page.totalCount -ne 1 -or $page.items.Count -ne 1) { throw 'Unexpected owner-scoped page.' }
    $null = Invoke-AccountCheck GET '/api/v1/accounts?pageSize=101' 400 -Token $token
    $null = Invoke-AccountCheck POST '/api/v1/accounts' 409 ($payload.Replace('Fictional Account', 'Different')) $token $key
    $null = Invoke-AccountCheck POST '/api/v1/accounts' 400 ($payload.Replace('10.000', '10.001')) $token ([guid]::NewGuid().ToString('N'))
    $null = Invoke-AccountCheck POST '/api/v1/accounts' 400 ($payload.Replace('10.000', '10.0000000000000000000000000000000000000001')) $token ([guid]::NewGuid().ToString('N'))
    $null = Invoke-AccountCheck POST '/api/v1/accounts' 400 $payload $token
    $null = Invoke-AccountCheck DELETE '/api/v1/users/me' 204 (@{ currentPassword = $password } | ConvertTo-Json -Compress) $token
    Write-Host 'Account HTTPS validation completed; one fictional account and its idempotency record remain attached to an inactive fictional user.'
}
finally { $password = $null; $token = $null; $login = $null; $register = $null; $loginJson = $null }
