# Runs the existing xUnit executables in Linux and keeps complete logs and native XML reports.
# No host assembly execution, credentials, persistent database or security-policy changes.
$ErrorActionPreference = 'Stop'

$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$localRoot = Join-Path $workspaceRoot '.local'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$reportsRoot = Join-Path $localRoot ('6a-docker-results/' + $runId)
New-Item -ItemType Directory -Path $reportsRoot -Force | Out-Null

$logPath = Join-Path $localRoot '6a-docker-tests.log'
$runLogPath = Join-Path $reportsRoot 'console.log'
$encoding = New-Object System.Text.UTF8Encoding($false)
$combinedLog = New-Object System.IO.StreamWriter($logPath, $true, $encoding)
$runLog = New-Object System.IO.StreamWriter($runLogPath, $false, $encoding)

# Use only the native XML reporter already included in xunit.v3 (see -result-xml).
# The project still uses Microsoft.Testing.Platform in global.json and in CI.
# The same three test assemblies run once each, without filters, skips or ignoreFailures.
$containerScript = @'
set -eu
dotnet --version
tar -C /src --exclude=.git --exclude=.local --exclude=bin --exclude=obj --exclude=.env --exclude='.env.*' --exclude=.aws --exclude=.codex --exclude=.agents -cf /work/source.tar .
tar -C /work -xf /work/source.tar
set +e
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
rc=$?
printf '%s\n' $rc > /results/restore.exitcode
if [ $rc -ne 0 ]; then exit $rc; fi
dotnet build FinanceFlow.slnx --configuration Release --no-restore
rc=$?
printf '%s\n' $rc > /results/build.exitcode
if [ $rc -ne 0 ]; then exit $rc; fi
first_failure=0
for project in FinanceFlow.Domain.Tests FinanceFlow.Application.Tests FinanceFlow.IntegrationTests; do
    dotnet /work/tests/$project/bin/Release/net10.0/$project.dll -result-xml /results/$project.xml
    rc=$?
    printf '%s\n' $rc > /results/$project.exitcode
    if [ $first_failure -eq 0 ] && [ $rc -ne 0 ]; then first_failure=$rc; fi
    if [ ! -s /results/$project.xml ] && [ $first_failure -eq 0 ]; then first_failure=3; fi
done
exit $first_failure
'@
$containerScript = $containerScript.Replace("`r`n", "`n")

$dockerArguments = @(
    'run', '--rm',
    '--mount', "type=bind,source=$workspaceRoot,target=/src,readonly",
    '--mount', 'type=bind,source=/var/run/docker.sock.raw,target=/var/run/docker.sock',
    '--mount', "type=bind,source=$reportsRoot,target=/results",
    '-e', 'TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal',
    '-w', '/work', 'mcr.microsoft.com/dotnet/sdk:10.0', 'sh', '-c', $containerScript
)

$dockerExit = $null
$previousPreference = $ErrorActionPreference
try {
    $combinedLog.WriteLine("--- Run $runId ---")
    # Windows PowerShell turns native stderr into ErrorRecords. Do not stop at the
    # first stderr line; retain its text, both streams, and Docker's actual exit code.
    $ErrorActionPreference = 'Continue'
    & docker @dockerArguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $combinedLog.WriteLine($line)
        $runLog.WriteLine($line)
    }
    $dockerExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
    $combinedLog.Dispose()
    $runLog.Dispose()
}

if ($null -eq $dockerExit) {
    throw 'Docker did not return an exit code. No test success can be inferred.'
}
[System.IO.File]::WriteAllText((Join-Path $reportsRoot 'docker.exitcode'), [string]$dockerExit, $encoding)
Write-Output "Docker exit code: $dockerExit"
Write-Output "Complete log (all runs appended): $logPath"
Write-Output "This run's log, XML and per-stage exit codes: $reportsRoot"

$total = 0
$passed = 0
$failed = 0
$skipped = 0
$errors = 0
$reportCount = 0
foreach ($project in @('FinanceFlow.Domain.Tests', 'FinanceFlow.Application.Tests', 'FinanceFlow.IntegrationTests')) {
    $report = Join-Path $reportsRoot ($project + '.xml')
    if (-not (Test-Path -LiteralPath $report)) {
        Write-Output "Missing XML for $project; this module is not approved. Inspect console.log."
        continue
    }
    [xml]$xml = [System.IO.File]::ReadAllText($report)
    foreach ($assembly in $xml.SelectNodes('/assemblies/assembly')) {
        $reportCount++
        $total += [int]$assembly.GetAttribute('total')
        $passed += [int]$assembly.GetAttribute('passed')
        $failed += [int]$assembly.GetAttribute('failed')
        $skipped += [int]$assembly.GetAttribute('skipped')
        $errors += $assembly.SelectNodes('errors/error').Count
        foreach ($failure in $assembly.SelectNodes("collection/test[@result='Fail']")) {
            Write-Output ("FAILED: " + $failure.GetAttribute('name'))
            foreach ($node in $failure.SelectNodes('failure/message | failure/stack-trace')) {
                Write-Output $node.InnerText
            }
        }
        foreach ($errorNode in $assembly.SelectNodes('errors/error')) {
            Write-Output ("RUNNER ERROR: " + $errorNode.GetAttribute('type'))
            foreach ($node in $errorNode.SelectNodes('failure/message | failure/stack-trace')) {
                Write-Output $node.InnerText
            }
        }
    }
}
Write-Output "XML reports: $reportCount/3; total: $total; passed: $passed; failed: $failed; skipped: $skipped; runner errors: $errors"
if (Select-String -LiteralPath $runLogPath -Pattern 'NU1900' -Quiet) {
    Write-Output 'NU1900 present: NuGet vulnerability audit remains inconclusive.'
}
if ($dockerExit -eq 0 -and ($reportCount -ne 3 -or $total -ne 557 -or $failed -ne 0 -or $skipped -ne 0 -or $errors -ne 0)) {
    # Preserve Docker's exit code separately; fail the diagnostic if discovery/reporting is incomplete.
    Write-Output 'Docker returned 0, but the expected 557/557 is not established by complete reports.'
    exit 3
}
exit $dockerExit
