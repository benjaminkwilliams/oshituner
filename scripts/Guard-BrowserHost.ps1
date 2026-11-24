param(
    [Parameter(Mandatory)] [ValidateSet('Check', 'Suspend', 'Resume')] [string] $Mode,
    [Parameter(Mandatory)] [string] $Executable,
    [switch] $ExitDesktop
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
        $active = $key.GetValue('')
        if ($null -ne $active -and $active -ne $paused) {
            throw 'The browser pairing changed while setup was running.'
        }
        $key.SetValue('', $paused, [Microsoft.Win32.RegistryValueKind]::String)
        $key.DeleteValue($pausedValue, $false)
    }
}

if ($Mode -eq 'Check') {
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { exit 0 }
    try {
        $probe = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    } catch [IO.IOException] {
        Write-Output "error OSHI001: Cannot replace $exe. Exit Oshi Tuner using its system tray menu. If Edge or Chrome is paired with this build, exit the paired browser (including its background processes) or unpair it, then rebuild. To launch an already-built copy, use dotnet run --project src/oshituner/oshituner.csproj --no-build from the repository root."
        exit 1
    }
    exit 0
}

if ($Mode -eq 'Suspend' -and $ExitDesktop) {
    $folder = [IO.Path]::GetDirectoryName($exe).ToUpperInvariant()
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($folder))
        $profileKey = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
    } finally { $hash.Dispose() }
    if (-not ('OshiTuner.UpgradePipePeer' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace OshiTuner {
    public static class UpgradePipePeer {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    }
}
'@
    }
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $record = Join-Path $localData "oshituner\installs\$profileKey\ipc-exit.dat"
    $candidates = @()
    if (Test-Path -LiteralPath $record) {
        Add-Type -AssemblyName System.Security
        try {
            $stream = [IO.File]::Open($record, 'Open', 'Read', ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
            try {
                if ($stream.Length -le 0 -or $stream.Length -gt 1024) { throw 'Invalid shutdown discovery record.' }
                $reader = [IO.BinaryReader]::new($stream)
                $protected = $reader.ReadBytes([int]$stream.Length)
                $peerName = [Text.Encoding]::ASCII.GetString([Security.Cryptography.ProtectedData]::Unprotect(
                    $protected, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
            } finally { $stream.Dispose() }
            if ($peerName -cnotmatch '^oshituner-[0-9a-f]{64}$') { throw 'Invalid shutdown endpoint.' }
            $candidates += @{ Name = $peerName; Duplex = $true }
        } catch [IO.FileNotFoundException] { }
        catch { Write-Warning 'The shutdown discovery record is unavailable. Checking legacy endpoints.' }
    }
    $candidates += @{ Name = "oshituner-exit2-$profileKey"; Duplex = $true }
    $candidates += @{ Name = "oshituner-exit-$profileKey"; Duplex = $false }
    $timeout = [Threading.CancellationTokenSource]::new(8000)
    try {
        foreach ($candidate in $candidates) {
            $duplex = $candidate.Duplex
            $direction = if ($duplex) { [IO.Pipes.PipeDirection]::InOut } else { [IO.Pipes.PipeDirection]::In }
            $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $candidate.Name, $direction, [IO.Pipes.PipeOptions]::Asynchronous)
            try {
                try { $pipe.Connect(650) }
                catch [TimeoutException] { continue }
                catch [UnauthorizedAccessException] { continue }

                [uint32] $serverId = 0
                if (![OshiTuner.UpgradePipePeer]::GetNamedPipeServerProcessId($pipe.SafePipeHandle, [ref] $serverId)) { continue }
                $desktop = Get-Process -Id $serverId -ErrorAction SilentlyContinue
                if (!$desktop) { continue }
                try {
                    # Hold the kernel process handle across the shutdown handshake.
                    $null = $desktop.Handle
                    $peer = Get-CimInstance Win32_Process -Filter "ProcessId=$serverId"
                    if (!$peer -or $peer.ExecutablePath -ne $exe) { continue }
                    $owner = Invoke-CimMethod -InputObject $peer -MethodName GetOwnerSid
                    if ($owner.Sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { continue }

                    if ($duplex) {
                        $pipe.WriteByte(1)
                        $confirmation = [byte[]]::new(1)
                        $read = $pipe.ReadAsync($confirmation, 0, 1, $timeout.Token).GetAwaiter().GetResult()
                        if ($read -ne 1 -or $confirmation[0] -ne 1) { throw 'Oshi Tuner did not confirm its pending exit.' }
                    } else {
                        # Drain the legacy response without trusting its PID.
                        $legacyReply = [byte[]]::new(4)
                        for ($offset = 0; $offset -lt $legacyReply.Length;) {
                            $read = $pipe.ReadAsync($legacyReply, $offset, $legacyReply.Length - $offset, $timeout.Token).GetAwaiter().GetResult()
                            if ($read -eq 0) { break }
                            $offset += $read
                        }
                    }
                    if (!$desktop.WaitForExit(6000)) { throw 'Oshi Tuner desktop did not exit. Exit it from the system tray.' }
                    break
                } catch {
                    if ($duplex -or !$desktop.HasExited) { throw }
                    break
                } finally { $desktop.Dispose() }
            } finally { $pipe.Dispose() }
        }
    } finally { $timeout.Dispose() }
}

$profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'oshituner'
$null = [IO.Directory]::CreateDirectory($profileRoot)
foreach ($browser in $browsers) {
    $gatePath = Join-Path $profileRoot "browser-$($browser.Name.ToLowerInvariant()).lock"
    $gate = $null
    for ($attempt = 0; $attempt -lt 15 -and !$gate; $attempt++) {
        try { $gate = [IO.File]::Open($gatePath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] {
            if ($attempt -eq 14) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    try {
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
    } finally { $gate.Dispose() }
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

if ($Mode -eq 'Suspend' -and $ExitDesktop -and (Test-Path -LiteralPath $exe)) {
    try {
        $probe = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')
        $probe.Dispose()
    } catch [IO.IOException] {
        throw 'The Oshi Tuner executable is still in use. Exit the desktop and paired browser helpers before trying again.'
    }
}
