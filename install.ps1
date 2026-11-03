<#
.SYNOPSIS
  Installs SessionKeeper for the current user (no admin rights needed).

.DESCRIPTION
  1. Builds and publishes the app to -Destination (default %LOCALAPPDATA%\Programs\SessionKeeper).
  2. Adds a Start menu shortcut (skip with -NoStartMenu).
  3. Installs the Claude Code skill to <Claude config dir>\skills\session-keeper (skip with -NoSkill).
     The Claude config dir is %CLAUDE_CONFIG_DIR% if set, otherwise %USERPROFILE%\.claude.

  "Start with Windows" is not turned on here; tick it in the app's Settings if you want it.
  Requires the .NET 8 SDK (dotnet) to build. Stops at the first error.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1 -Destination D:\Tools\SessionKeeper -NoStartMenu
#>
param(
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Programs\SessionKeeper'),
    [switch] $NoStartMenu,
    [switch] $NoSkill
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src\SessionKeeper\SessionKeeper.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw "Can't find $project. Run this script from the SessionKeeper repository." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 8 SDK (dotnet) is required: https://dotnet.microsoft.com/download/dotnet/8.0' }

$Destination = [System.IO.Path]::GetFullPath($Destination)
$exe = Join-Path $Destination 'SessionKeeper.exe'

# A running copy locks its files; ask instead of killing it. That includes a copy started from this
# repository's own build output, which the build has to overwrite.
$buildOutput = [System.IO.Path]::GetFullPath((Join-Path $repo 'src\SessionKeeper\bin')) + '\'
$running = Get-Process SessionKeeper -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and (([System.IO.Path]::GetFullPath($_.Path) -ieq $exe) -or [System.IO.Path]::GetFullPath($_.Path).StartsWith($buildOutput, [StringComparison]::OrdinalIgnoreCase)) } catch { $false }
}
if ($running) {
    $where = ($running | ForEach-Object { $_.Path } | Select-Object -Unique) -join ', '
    throw "SessionKeeper is running from $where. Close it (including its tray icon) and run this again. " +
          "(If Start with Windows starts a copy from the repository's bin folder, re-tick it in the installed copy's Settings.)"
}

Write-Host "Publishing SessionKeeper to $Destination ..."
& dotnet publish $project -c Release -o $Destination --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish finished but $exe is missing." }

if (-not $NoStartMenu) {
    $out = & $exe --install-shortcut | ForEach-Object { $_ }
    if ($LASTEXITCODE -ne 0) { throw "Creating the Start menu shortcut failed (exit $LASTEXITCODE)." }
    Write-Host $out
}

if (-not $NoSkill) {
    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
    $skillSrc = Join-Path $repo 'skills\session-keeper'
    $skillDst = Join-Path $claudeDir 'skills\session-keeper'
    New-Item -ItemType Directory -Force -Path $skillDst | Out-Null
    foreach ($f in 'SKILL.md', 'sessionkeeper.ps1') {
        Copy-Item -LiteralPath (Join-Path $skillSrc $f) -Destination (Join-Path $skillDst $f) -Force
    }
    Write-Host "Claude Code skill: $skillDst"
}

$version = & $exe --version | ForEach-Object { $_ }
if ($LASTEXITCODE -ne 0) { throw "Installed exe failed to run (exit $LASTEXITCODE)." }
Write-Host "Installed $version at $exe"
if ($Destination -ine (Join-Path $env:LOCALAPPDATA 'Programs\SessionKeeper') -and -not $NoSkill) {
    Write-Host "Note: the skill looks in %LOCALAPPDATA%\Programs\SessionKeeper by default. Set SESSIONKEEPER_EXE=$exe so it can find this copy."
}
