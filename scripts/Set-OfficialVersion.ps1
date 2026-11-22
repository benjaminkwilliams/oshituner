param(
    [string] $OfficialRef = 'origin/main',
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectPath = 'src/oshituner/Version.props'
$git = Get-Command git -ErrorAction SilentlyContinue
$gitExe = if ($git) { $git.Source } else { Join-Path $env:ProgramFiles 'Git/cmd/git.exe' }
$officialContents = & $gitExe -C $root show "${OfficialRef}:$projectPath"
if ($LASTEXITCODE -ne 0) { throw "Cannot read the official version at $OfficialRef." }
$official = ([xml]($officialContents -join "`n")).Project.PropertyGroup
& $gitExe -C $root diff --quiet $OfficialRef -- 'src/oshituner/Twitch*.cs'
$twitchChanged = $LASTEXITCODE -eq 1
if ($LASTEXITCODE -gt 1) { throw 'Cannot compare Twitch changes with the official ref.' }

function Next-Build([version] $current, [bool] $advance) {
    if (-not $advance) { return $current }
    if ($current.Revision -ge 65534) { throw 'Advance the official version prefix before integrating this PR.' }
    return [version]::new($current.Major, $current.Minor, $current.Build, $current.Revision + 1)
}
$next = [ordered]@{
    OshiOfficialVersion = Next-Build ([version]$official.OshiOfficialVersion) $true
    OshiOfficialTwitchVersion = Next-Build ([version]$official.OshiOfficialTwitchVersion) $twitchChanged
}
$path = Join-Path $root $projectPath
$text = [IO.File]::ReadAllText($path)
foreach ($property in $next.Keys) {
    $pattern = "(?<=<$property>)\d+\.\d+\.\d+\.\d+(?=</$property>)"
    if ([regex]::Matches($text, $pattern).Count -ne 1) { throw "Expected exactly one $property." }
    $expected = [string]$next[$property]
    if ($Check) {
        if ([regex]::Match($text, $pattern).Value -ne $expected) {
            throw "Expected $property $expected based on $OfficialRef. Run scripts/Set-OfficialVersion.ps1 and commit Version.props."
        }
    } else {
        $text = [regex]::Replace($text, $pattern, $expected)
    }
    "$property $expected"
}
if (-not $Check) { [IO.File]::WriteAllText($path, $text) }
