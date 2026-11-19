[CmdletBinding()]
param(
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$testProject = Join-Path $projectRoot 'tests\Configurator.Security.Tests\Configurator.Security.Tests.csproj'
$solution = Join-Path $projectRoot 'Configurator.slnx'
$quick = $PSBoundParameters.ContainsKey('Filter')
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$testTemp = $null
$transcriptStarted = $false
$exitCode = 0

Push-Location -LiteralPath $projectRoot
try {
    $tempRoot = Join-Path $projectRoot 'artifacts'
    $runId = [guid]::NewGuid().ToString('N')
    $tempPath = Join-Path $tempRoot "quality-temp-$runId"
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    if ((Get-Item -LiteralPath $tempRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Test artifacts directory is a reparse point: $tempRoot"
    }
    $testTemp = (New-Item -ItemType Directory -Path $tempPath).FullName
    $env:TEMP = $testTemp
    $env:TMP = $testTemp

    $testArgs = @('test', $testProject, '-c', 'Release')
    if ($quick) {
        $testArgs += @('--filter', $Filter, '--logger', 'console;verbosity=minimal', '-v', 'quiet')
    }
    else {
        $resultsDir = Join-Path $projectRoot 'test-results'
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
        $stamp = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + $runId.Substring(0, 8)
        $transcriptPath = Join-Path $resultsDir "run_$stamp.log"
        Start-Transcript -LiteralPath $transcriptPath | Out-Null
        $transcriptStarted = $true

        dotnet --info
        if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE" }

        dotnet restore $solution
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

        $trxName = "tests_$stamp.trx"
        $testArgs += @('--no-restore', '--results-directory', $resultsDir,
            '--logger', 'console;verbosity=normal', '--logger', "trx;LogFileName=$trxName")
    }

    dotnet @testArgs -- RunConfiguration.TreatNoTestsAsError=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
    if (-not $quick) {
        $trxPath = Join-Path $resultsDir $trxName
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw "Missing test report: $trxPath" }
        Write-Host "TRX: $trxPath"
    }
}
catch {
    $exitCode = 1
    Write-Error $_ -ErrorAction Continue
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    Pop-Location
    try {
        if ($testTemp -and (Test-Path -LiteralPath $testTemp)) {
            $expectedPath = [IO.Path]::GetFullPath($tempPath)
            $tempDirectory = Get-Item -LiteralPath $testTemp
            if ($tempDirectory.FullName -ne $expectedPath -or
                ($tempDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                ((Get-Item -LiteralPath $tempRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unexpected temporary directory: $testTemp"
            }
            $tempDirectory.Delete($true)
        }
    }
    catch {
        $exitCode = 1
        Write-Error $_ -ErrorAction Continue
    }

    if ($exitCode -eq 0) { Write-Host 'RESULT: PASSED' }
    else { Write-Host 'RESULT: FAILED' }
    if ($transcriptStarted) {
        Write-Host "Transcript: $transcriptPath"
        Stop-Transcript | Out-Null
    }
}
exit $exitCode
