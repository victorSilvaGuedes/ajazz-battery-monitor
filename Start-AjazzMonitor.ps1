$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$logDir = Join-Path $root 'logs'
$vbsPath = Join-Path $root 'Start-AjazzMonitor.vbs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'monitor.log'

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\AjazzAJ139ProBatteryMonitor', [ref]$createdNew)
if (-not $createdNew) {
    $mutex.Dispose()
    exit 0
}

try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing

    $files = @(
        (Join-Path $srcDir 'AjazzProtocol.cs'),
        (Join-Path $srcDir 'BatteryMonitor.cs'),
        (Join-Path $srcDir 'HidNative.cs'),
        (Join-Path $srcDir 'ToastNotifier.cs'),
        (Join-Path $srcDir 'TrayApp.cs')
    )

    Add-Type -Path $files -ReferencedAssemblies @('System.Windows.Forms.dll', 'System.Drawing.dll')

    [System.Windows.Forms.Application]::EnableVisualStyles()
    [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

    $ctx = New-Object AjazzBatteryMonitor.TrayAppContext($vbsPath, $logDir)
    [System.Windows.Forms.Application]::Run($ctx)
}
catch {
    $msg = "[{0:yyyy-MM-dd HH:mm:ss}] Erro fatal no launcher: {1}`r`n" -f (Get-Date), $_.Exception.ToString()
    Add-Content -Path $logFile -Value $msg -Encoding UTF8
    throw
}
finally {
    try { $mutex.ReleaseMutex() } catch {}
    $mutex.Dispose()
}
