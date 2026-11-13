$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$testProject = Join-Path $projectRoot 'tests\Configurator.Security.Tests\Configurator.Security.Tests.csproj'
$appProject = Join-Path $projectRoot 'Configurator.csproj'
$resultsDir = Join-Path $projectRoot 'test-results'
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$transcriptPath = Join-Path $resultsDir "run_$stamp.log"
Start-Transcript -Path $transcriptPath -Force | Out-Null
try {
    Write-Host "Configurator test run: $stamp"
    dotnet --info
    if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE" }

    Write-Host "`n=== RESTORE ==="
    dotnet restore $appProject
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    Write-Host "`n=== RELEASE BUILD ==="
    dotnet build $appProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    Write-Host "`n=== SECURITY / REGRESSION TESTS ==="
    $trxPath = Join-Path $resultsDir "tests_$stamp.trx"
    dotnet test $testProject -c Release --logger "console;verbosity=normal" --logger "trx;LogFileName=$trxPath"
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }

    Write-Host "`nRESULT: restore, Release build and test command completed successfully."
    Write-Host "Transcript: $transcriptPath"
    Write-Host "TRX: $trxPath"
}
catch {
    Write-Error $_
    Write-Host "RESULT: FAILED. See transcript: $transcriptPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
