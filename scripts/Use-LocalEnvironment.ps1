param([ValidateSet('Api', 'DatabaseTools')][string]$Mode = 'Api')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$values = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $projectRoot '.env')) {
    if ($line -match '^(MSSQL_SA_PASSWORD|MIGRATION_DB_PASSWORD|API_DB_PASSWORD|JWT_SIGNING_KEY_BASE64|HTTPS_CERTIFICATE_PASSWORD)=(.+)$') {
        $values[$Matches[1]] = $Matches[2]
    }
}
if ($values.Count -ne 5) { throw 'Generate the local environment before loading it.' }
$env:Database__ConnectionString = "Server=localhost,14333;Database=FinanceFlow;User ID=FinanceFlowApi;Password=$($values.API_DB_PASSWORD);Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"
$env:Jwt__Issuer = 'FinanceFlow.Local'
$env:Jwt__Audience = 'FinanceFlow.Local.Client'
$env:Jwt__SigningKeyBase64 = $values.JWT_SIGNING_KEY_BASE64
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_Kestrel__Certificates__Default__Path = Join-Path $projectRoot '.local/certs/api.pfx'
$env:ASPNETCORE_Kestrel__Certificates__Default__Password = $values.HTTPS_CERTIFICATE_PASSWORD
if ($Mode -eq 'DatabaseTools') {
    $env:Migration__ConnectionString = "Server=localhost,14333;Database=FinanceFlow;User ID=FinanceFlowMigrator;Password=$($values.MIGRATION_DB_PASSWORD);Encrypt=True;TrustServerCertificate=True"
    $env:Provisioning__AdminConnectionString = "Server=localhost,14333;Database=master;User ID=sa;Password=$($values.MSSQL_SA_PASSWORD);Encrypt=True;TrustServerCertificate=True"
} else {
    $env:Migration__ConnectionString = $null
    $env:Provisioning__AdminConnectionString = $null
}
Write-Output "Local process environment loaded for $Mode. No secret values printed."
