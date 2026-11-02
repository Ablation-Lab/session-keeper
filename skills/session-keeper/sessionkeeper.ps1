<#
.SYNOPSIS
  Runs SessionKeeper for the session-keeper Claude Code skill.

.DESCRIPTION
  "open" starts the app (or brings the running window to the front) and returns immediately.
  Anything else is passed to SessionKeeper.exe as command-line options (--list, --recent,
  --snapshots, --snapshot "note", --help ...); its output is printed and its exit code returned.

  SessionKeeper.exe is looked up in this order:
    1. the SESSIONKEEPER_EXE environment variable (full path to the exe)
    2. %LOCALAPPDATA%\Programs\SessionKeeper\SessionKeeper.exe (where install.ps1 puts it)
    3. SessionKeeper.exe on PATH
  Exit code 3 means it could not be found.
#>
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Rest)

$ErrorActionPreference = 'Stop'

function Find-SessionKeeper {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:SESSIONKEEPER_EXE) { $candidates.Add($env:SESSIONKEEPER_EXE) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\SessionKeeper\SessionKeeper.exe')) }
    $onPath = Get-Command 'SessionKeeper.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Source) }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return (Resolve-Path -LiteralPath $c).Path }
    }
    return $null
}

$exe = Find-SessionKeeper
if (-not $exe) {
    [Console]::Error.WriteLine('SessionKeeper.exe was not found. Install it with install.ps1 from the SessionKeeper repository (see its README), or set SESSIONKEEPER_EXE to the full path of SessionKeeper.exe.')
    exit 3
}

if (-not $Rest -or $Rest.Count -eq 0 -or ($Rest.Count -eq 1 -and $Rest[0] -eq 'open')) {
    Start-Process -FilePath $exe | Out-Null
    Write-Output "Opened SessionKeeper ($exe)."
    exit 0
}

# SessionKeeper writes UTF-8; decode it as UTF-8 (Windows PowerShell defaults to the OEM code page).
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
# Piping makes PowerShell wait for the GUI-subsystem exe and capture its output.
& $exe @Rest | ForEach-Object { $_ }
exit $LASTEXITCODE
