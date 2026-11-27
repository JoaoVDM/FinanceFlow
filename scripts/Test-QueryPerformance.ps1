# Dedicated disposable SQL Server + SDK only. Never uses Compose, existing DBs or volumes.
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$results = Join-Path $workspaceRoot ('.local/7c-performance-results/' + $runId)
New-Item -ItemType Directory -Path $results -Force | Out-Null
$networkName = 'financeflow-7c-net-' + $runId.ToLowerInvariant()
$sqlName = 'financeflow-7c-sql-' + $runId.ToLowerInvariant()
$sdkName = 'financeflow-7c-sdk-' + $runId.ToLowerInvariant()
$networkCreated = $false
$sqlCreated = $false
$sdkCreated = $false
$exitCode = 1
$state = $null
$previousPassword = $env:PERF_SQL_PASSWORD
$previousSqlPassword = $env:MSSQL_SA_PASSWORD
$previousServer = $env:PERF_SQL_SERVER
$previousDisposable = $env:PERF_DISPOSABLE
try {
    # Native stderr is diagnostic text in Windows PowerShell. Check exit codes explicitly.
    $ErrorActionPreference = 'Continue'
    & docker version --format '{{.Server.Version}}' *> (Join-Path $results 'docker-version.log')
    if ($LASTEXITCODE -ne 0) { throw "Docker version failed, exit $LASTEXITCODE. Log: $(Join-Path $results 'docker-version.log')" }
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $env:PERF_SQL_PASSWORD = [Convert]::ToBase64String($bytes) + 'Aa1!'
    $env:MSSQL_SA_PASSWORD = $env:PERF_SQL_PASSWORD
    $env:PERF_SQL_SERVER = $sqlName
    $env:PERF_DISPOSABLE = '1'
    & docker network create $networkName *> (Join-Path $results 'network.log')
    if ($LASTEXITCODE -ne 0) { throw "Network creation failed, exit $LASTEXITCODE. Log: $(Join-Path $results 'network.log')" }
    $networkCreated = $true
    & docker create --name $sqlName --network $networkName -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD mcr.microsoft.com/mssql/server:2022-CU22-GDR1-ubuntu-22.04 *> (Join-Path $results 'sql-create.log')
    if ($LASTEXITCODE -ne 0) { throw "SQL creation failed, exit $LASTEXITCODE. Log: $(Join-Path $results 'sql-create.log')" }
    $sqlCreated = $true
    & docker start $sqlName *> (Join-Path $results 'sql-start.log')
    if ($LASTEXITCODE -ne 0) { throw "SQL start failed, exit $LASTEXITCODE. Log: $(Join-Path $results 'sql-start.log')" }
    $command = @'
set -u
tar -C /src --exclude=.git --exclude=.local --exclude=bin --exclude=obj --exclude=.env --exclude='.env.*' --exclude=.aws --exclude=.codex --exclude=.agents -cf /work/source.tar .
rc=$?
printf '%s\n' $rc > /results/source-copy.exitcode
if [ $rc -ne 0 ]; then exit $rc; fi
tar -C /work -xf /work/source.tar
rc=$?
printf '%s\n' $rc > /results/extract.exitcode
if [ $rc -ne 0 ]; then exit $rc; fi
dotnet restore tools/FinanceFlow.QueryAnalysis/FinanceFlow.QueryAnalysis.csproj --configfile NuGet.Config
rc=$?
printf '%s\n' $rc > /results/restore.exitcode
if [ $rc -ne 0 ]; then exit $rc; fi
dotnet build tools/FinanceFlow.QueryAnalysis/FinanceFlow.QueryAnalysis.csproj --configuration Release --no-restore
rc=$?
printf '%s\n' $rc > /results/build.exitcode
if [ $rc -ne 0 ]; then exit $rc; fi
dotnet run --project tools/FinanceFlow.QueryAnalysis/FinanceFlow.QueryAnalysis.csproj --configuration Release --no-build --no-restore -- --measure /results
rc=$?
printf '%s\n' $rc > /results/measurement.exitcode
exit $rc
'@
    $command = $command.Replace("`r`n", "`n")
    & docker create --name $sdkName --network $networkName --mount "type=bind,source=$workspaceRoot,target=/src,readonly" --mount "type=bind,source=$results,target=/results" -e PERF_SQL_PASSWORD -e PERF_SQL_SERVER -e PERF_DISPOSABLE -w /work mcr.microsoft.com/dotnet/sdk:10.0 sh -c $command *> (Join-Path $results 'sdk-create.log')
    if ($LASTEXITCODE -ne 0) { throw "SDK creation failed, exit $LASTEXITCODE. Log: $(Join-Path $results 'sdk-create.log')" }
    $sdkCreated = $true
    & docker start --attach $sdkName *> (Join-Path $results 'console.log')
    $attachExitCode = $LASTEXITCODE
    [System.IO.File]::WriteAllText((Join-Path $results 'attach.exitcode'), [string]$attachExitCode)
    $stateJson = & docker inspect --format '{{json .State}}' $sdkName 2> (Join-Path $results 'sdk-inspect.stderr.log')
    $inspectExitCode = $LASTEXITCODE
    [System.IO.File]::WriteAllText((Join-Path $results 'inspect.exitcode'), [string]$inspectExitCode)
    [System.IO.File]::WriteAllText((Join-Path $results 'sdk-state.json'), ($stateJson -join "`n"))
    if ($inspectExitCode -ne 0) { throw "SDK inspection failed, exit $inspectExitCode (attach exit $attachExitCode). Log: $(Join-Path $results 'sdk-inspect.stderr.log')" }
    $state = ($stateJson -join "`n") | ConvertFrom-Json -ErrorAction Stop
    if ($state.Status -ne 'exited') { throw "SDK did not finish (state $($state.Status), attach exit $attachExitCode). Log: $(Join-Path $results 'console.log')" }
    $exitCode = [int]$state.ExitCode
    [System.IO.File]::WriteAllText((Join-Path $results 'docker.exitcode'), [string]$exitCode)
    if ($exitCode -ne 0) {
        $stage = 'SDK execution'
        foreach ($step in @('source-copy','extract','restore','build','measurement')) {
            $stepFile = Join-Path $results ($step + '.exitcode')
            if ((Test-Path -LiteralPath $stepFile) -and [int]([System.IO.File]::ReadAllText($stepFile).Trim()) -ne 0) { $stage = $step; break }
        }
        $failureFile = Join-Path $results 'failure.json'
        if (Test-Path -LiteralPath $failureFile) { $stage = ([System.IO.File]::ReadAllText($failureFile) | ConvertFrom-Json -ErrorAction Stop).stage }
        Write-Output "Performance stage $stage failed, container exit $exitCode (attach exit $attachExitCode). Log: $(Join-Path $results 'console.log')"
    }
    elseif ($attachExitCode -ne 0) {
        $exitCode = $attachExitCode
        Write-Output "SDK attach failed, exit $attachExitCode, despite container exit 0. Log: $(Join-Path $results 'console.log')"
    }
    else {
        foreach ($step in @('source-copy','extract','restore','build','measurement')) {
            $stepFile = Join-Path $results ($step + '.exitcode')
            if (-not (Test-Path -LiteralPath $stepFile) -or [int]([System.IO.File]::ReadAllText($stepFile).Trim()) -ne 0) {
                throw "Incomplete stage $step; no successful measurement can be inferred. Log: $(Join-Path $results 'console.log')"
            }
        }
        $completionFile = Join-Path $results 'completion.json'
        if (-not (Test-Path -LiteralPath $completionFile)) { throw "Missing completion.json; measurement incomplete. Log: $(Join-Path $results 'console.log')" }
        $completion = [System.IO.File]::ReadAllText($completionFile) | ConvertFrom-Json -ErrorAction Stop
        if ($completion.success -ne $true -or $completion.scenarios -ne 32 -or $completion.warmups -ne 2 -or $completion.repetitions -ne 5 -or
            @($completion.scales).Count -ne 2 -or 200 -notin $completion.scales -or 40000 -notin $completion.scales) {
            throw "Incomplete scales/scenarios; no successful measurement can be inferred. Log: $completionFile"
        }
    }
}
catch {
    if ($exitCode -eq 0) { $exitCode = 1 }
    Write-Output "Performance procedure failed: $($_.Exception.Message)"
    Write-Output "Artifacts: $results"
}
finally {
    # Names were generated locally; only successfully created resources are removed.
    # No volume flags, Compose, existing-resource enumeration or pool/cache clearing.
    if ($sdkCreated) {
        & docker rm -f $sdkName *> (Join-Path $results 'sdk-cleanup.log')
        [System.IO.File]::WriteAllText((Join-Path $results 'sdk-cleanup.exitcode'), [string]$LASTEXITCODE)
        if ($LASTEXITCODE -ne 0) { $exitCode = 3 }
    }
    if ($sqlCreated) {
        & docker rm -f $sqlName *> (Join-Path $results 'sql-cleanup.log')
        [System.IO.File]::WriteAllText((Join-Path $results 'sql-cleanup.exitcode'), [string]$LASTEXITCODE)
        if ($LASTEXITCODE -ne 0) { $exitCode = 3 }
    }
    if ($networkCreated) {
        & docker network rm $networkName *> (Join-Path $results 'network-cleanup.log')
        [System.IO.File]::WriteAllText((Join-Path $results 'network-cleanup.exitcode'), [string]$LASTEXITCODE)
        if ($LASTEXITCODE -ne 0) { $exitCode = 3 }
    }
    $env:PERF_SQL_PASSWORD = $previousPassword
    $env:MSSQL_SA_PASSWORD = $previousSqlPassword
    $env:PERF_SQL_SERVER = $previousServer
    $env:PERF_DISPOSABLE = $previousDisposable
    $ErrorActionPreference = 'Stop'
}
Write-Output "Performance procedure exit code: $exitCode"
if ($null -ne $state) { Write-Output "SDK/container exit code: $($state.ExitCode)" }
Write-Output "Artifacts: $results"
[System.IO.File]::WriteAllText((Join-Path $results 'procedure.exitcode'), [string]$exitCode)
exit $exitCode
