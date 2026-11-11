$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$files = @(
    (Join-Path $root 'src\AjazzProtocol.cs'),
    (Join-Path $root 'src\BatteryMonitor.cs'),
    (Join-Path $root 'tests\AjazzProtocolTests.cs')
)

Add-Type -Path $files
$exitCode = [AjazzBatteryMonitor.Tests.TestRunner]::RunAll()
exit $exitCode
