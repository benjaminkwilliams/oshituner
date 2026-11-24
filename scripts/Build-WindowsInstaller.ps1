param(
    [string] $OutputDirectory = '.plans/verification-build/installer'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src/oshituner'
$version = ([xml](Get-Content -LiteralPath (Join-Path $src 'Version.props') -Raw)).Project.PropertyGroup.OshiOfficialVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Expected a four-part official application version.' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}

$compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $compiler) {
    $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup 6 is required to build the installer.' }
$sdkVersion = dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the .NET SDK.' }
Write-Host ".NET SDK $sdkVersion"

$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
$publish = Join-Path $output ("publish-" + [Guid]::NewGuid())
try {
    dotnet publish (Join-Path $src 'oshituner.csproj') -c Release -r win-x64 --self-contained true -o $publish -warnaserror -p:OshiUseOfficialVersion=true
    if ($LASTEXITCODE -ne 0) { throw 'The self-contained publish failed.' }

    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publish 'oshituner.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        Write-Host "Bundled runtime $($framework.name) $($framework.version)"
    }
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'oshituner.dll')).Version
    if ($assemblyVersion.ToString() -ne $version) { throw "The published version $assemblyVersion does not match $version." }

    $license = Join-Path $root 'LICENSE'
    & $compiler "/O$output" "/DAppVersion=$version" "/DPublishDir=$publish" "/DAppLicense=$license" (Join-Path $root 'installer/OshiTuner.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed to compile the installer.' }

    $path = Join-Path $output "Oshi-Tuner-$version-win-x64-Setup.exe"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The installer executable is missing.' }
    $path
} finally {
    if (Test-Path -LiteralPath $publish) {
        $resolved = (Resolve-Path -LiteralPath $publish).Path
        if ((Split-Path -Parent $resolved) -eq $output -and (Split-Path -Leaf $resolved) -match '^publish-[a-f0-9-]+$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
