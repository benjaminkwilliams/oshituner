param(
    [Parameter(Mandatory)] [ValidateSet('Check', 'Suspend', 'Resume')] [string] $Mode,
    [Parameter(Mandatory)] [string] $Executable
)

$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($Executable)
$browsers = @(
    @{ Name = 'Edge'; Key = 'Software\Microsoft\Edge\NativeMessagingHosts\com.oshituner.edge'; Origin = 'chrome-extension://cagcflabpbciehhnjchjipdipmgplipl' },
    @{ Name = 'Chrome'; Key = 'Software\Google\Chrome\NativeMessagingHosts\com.oshituner.chrome'; Origin = 'chrome-extension://hnomfkgbacjeiocjcgbnnoaaloleefjf' }
)
$pausedValue = 'OshiTunerUpgradeManifest'

function Test-OwnedManifest([string] $Path) {
    if (!$Path -or !(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    return $manifest.path -is [string] -and [IO.Path]::GetFullPath($manifest.path) -eq $exe
}

function Restore-Pairing($key) {
    $paused = $key.GetValue($pausedValue)
    if (Test-OwnedManifest $paused) {
        if ($null -eq $key.GetValue('')) {
            $key.SetValue('', $paused, [Microsoft.Win32.RegistryValueKind]::String)
        }
        $key.DeleteValue($pausedValue, $false)
    }
}

if ($Mode -eq 'Check') {
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { exit 0 }
    try {
        $probe = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    } catch [IO.IOException] {
        Write-Output "error OSHI001: Cannot replace $exe. Close Oshi Tuner. If Edge or Chrome is paired with this build, exit the paired browser (including its background processes) or unpair it, then rebuild. To launch an already-built copy, use dotnet run --no-build."
        exit 1
    }
    exit 0
}

foreach ($browser in $browsers) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($browser.Key, $true)
    if (!$key) { continue }
    try {
        if ($Mode -eq 'Resume') {
            Restore-Pairing $key
            continue
        }

        $manifestPath = $key.GetValue('')
        if (Test-OwnedManifest $manifestPath) {
            $key.SetValue($pausedValue, $manifestPath, [Microsoft.Win32.RegistryValueKind]::String)
            $key.DeleteValue('')
        }
    } finally { $key.Dispose() }
}

if ($Mode -eq 'Suspend') {
    # The absent registrations prevent either browser from restarting its helper during setup.
    foreach ($process in Get-CimInstance Win32_Process -Filter "Name='oshituner.exe'") {
        if ($process.ExecutablePath -ne $exe) { continue }
        $commandLine = $process.CommandLine
        if (!($browsers | Where-Object { $commandLine -match ('(?:^|[\s"])' + [regex]::Escape($_.Origin) + '/?(?=[\s"]|$)') })) { continue }
        $hostProcess = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
        if (!$hostProcess -or [Math]::Abs(($hostProcess.StartTime - $process.CreationDate).TotalMilliseconds) -gt 2) { continue }
        Stop-Process -InputObject $hostProcess
        if (!$hostProcess.WaitForExit(5000)) { throw 'The Oshi Tuner browser helper did not exit.' }
    }
}
