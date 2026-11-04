<#
.SYNOPSIS
  Installs or updates SessionKeeper for the current user (no admin rights needed).

.DESCRIPTION
  The easy way is to double-click install.cmd, which runs this script with -Interactive.

  1. Builds and publishes the app to -Destination (default %LOCALAPPDATA%\Programs\SessionKeeper).
  2. Adds a Start menu shortcut (skip with -NoStartMenu).
  3. Installs the Claude Code skill to <Claude config dir>\skills\session-keeper (skip with -NoSkill).
     The Claude config dir is %CLAUDE_CONFIG_DIR% if set, otherwise %USERPROFILE%\.claude.

  With -Interactive (what install.cmd uses) it also:
  - offers to close a running SessionKeeper cleanly first (it asks; it never kills it),
  - offers to point "Start with Windows" at the installed copy if it currently starts a different one,
  - offers to open the installed copy at the end.
  Without -Interactive it stops with a message if SessionKeeper is running, and changes nothing else.

  "Start with Windows" is never turned on here; tick it in the app's Settings if you want it.
  Installing under Program Files is refused: it needs admin rights, and updates would fail.
  Requires the .NET 8 SDK (dotnet) to build. Stops at the first error.

.EXAMPLE
  install.cmd
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1 -Destination D:\Tools\SessionKeeper -NoStartMenu
#>
param(
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Programs\SessionKeeper'),
    [switch] $NoStartMenu,
    [switch] $NoSkill,
    [switch] $Interactive
)

$ErrorActionPreference = 'Stop'
# Show problems as one plain message (install.cmd pauses so it can be read), not PowerShell's error dump.
trap {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
$repo = $PSScriptRoot
$project = Join-Path $repo 'src\SessionKeeper\SessionKeeper.csproj'
$defaultDestination = Join-Path $env:LOCALAPPDATA 'Programs\SessionKeeper'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Ask([string] $question) {
    # Y/N prompt for -Interactive runs. No answer (e.g. input closed) counts as No.
    for ($i = 0; $i -lt 3; $i++) {
        $answer = Read-Host "$question [Y/N]"
        if ($null -eq $answer) { return $false }
        if ($answer -match '^\s*(y|yes)\s*$') { return $true }
        if ($answer -match '^\s*(n|no)\s*$') { return $false }
    }
    return $false
}

function Get-StartupCommand {
    $p = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object Name -eq 'SessionKeeper'
    if ($p) { return [string]$p.Value } else { return $null }
}

if (-not (Test-Path -LiteralPath $project)) { throw "Can't find $project. Run this from the SessionKeeper repository folder." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 8 SDK (dotnet) is required: https://dotnet.microsoft.com/download/dotnet/8.0' }

$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')
foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { $_ }) {
    if (($Destination + '\').StartsWith(([System.IO.Path]::GetFullPath($pf).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Don't install SessionKeeper under Program Files ($Destination): that needs admin rights and updates would fail. " +
              "Use the default per-user folder ($defaultDestination) or another folder you own."
    }
}
$exe = Join-Path $Destination 'SessionKeeper.exe'

Write-Host ''
Write-Host "SessionKeeper installer"
Write-Host "  from: $repo"
Write-Host "  to:   $Destination"
Write-Host ''

# ---------------------------------------------------------------- a running copy locks its files
# That includes a copy started from this repository's own build output, which the build overwrites.
$buildOutput = [System.IO.Path]::GetFullPath((Join-Path $repo 'src\SessionKeeper\bin')) + '\'
# MCP connectors (SessionKeeper.exe --mcp, started by Claude Desktop or Claude Code) are handled separately below.
function Get-ConnectorIds {
    @(Get-CimInstance Win32_Process -Filter "Name='SessionKeeper.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '(^|\s)--mcp(\s|$)' } | ForEach-Object { [int]$_.ProcessId })
}
function Get-LockingProcesses([bool] $connectors) {
    $mcp = Get-ConnectorIds
    Get-Process SessionKeeper -ErrorAction SilentlyContinue | Where-Object {
        try {
            $path = [System.IO.Path]::GetFullPath($_.Path)
            (($mcp -contains $_.Id) -eq $connectors) -and (($path -ieq $exe) -or $path.StartsWith($buildOutput, [StringComparison]::OrdinalIgnoreCase))
        } catch { $false }
    }
}
function Get-BlockingCopies { Get-LockingProcesses $false }
function Get-BlockingConnectors { Get-LockingProcesses $true }

$running = @(Get-BlockingCopies)
if ($running.Count -gt 0) {
    $where = ($running | ForEach-Object { $_.Path } | Select-Object -Unique) -join ', '
    Write-Host "SessionKeeper is running from $where."
    if (-not $Interactive) {
        throw "Close SessionKeeper (including its tray icon) and run this again, or double-click install.cmd, which can close it for you."
    }
    if (-not (Ask 'Close it now so the install can continue?')) {
        throw 'Install cancelled. Close SessionKeeper (including its tray icon) and run install.cmd again.'
    }
    # Ask it to exit cleanly: current versions listen for this signal (works from the tray too);
    # older ones are asked to close their window. Nothing is ever killed.
    try {
        $signal = [System.Threading.EventWaitHandle]::OpenExisting('Local\SessionKeeper.Exit')
        [void]$signal.Set()
        $signal.Dispose()
    } catch { }
    foreach ($p in $running) { try { if (-not $p.HasExited) { [void]$p.CloseMainWindow() } } catch { } }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-BlockingCopies).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-BlockingCopies).Count -gt 0) {
        throw "SessionKeeper didn't close (it may be showing a dialog, or it's an older version in the tray). " +
              "Close it yourself (right-click its tray icon > Exit) and run install.cmd again."
    }
    Write-Host 'Closed SessionKeeper.'
    Write-Host ''
}

# MCP connectors lock the exe too. They have their own stop signal (separate from the app's): while it's set, running
# ones exit cleanly and new ones exit straight away. It's cleared once the new version is in place.
$mcpStop = $null
$connectors = @(Get-BlockingConnectors)
if ($connectors.Count -gt 0) {
    Write-Host "Pausing $($connectors.Count) SessionKeeper MCP connector(s) started by Claude Desktop or Claude Code, for the update."
    $mcpStop = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'Local\SessionKeeper.McpStop')
    [void]$mcpStop.Set()
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-BlockingConnectors).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-BlockingConnectors).Count -gt 0) {
        [void]$mcpStop.Reset()
        throw "The SessionKeeper MCP connector didn't stop (it may be an older version). Close Claude Desktop and Claude Code, then run install.cmd again."
    }
    Write-Host 'Paused. Afterwards, restart Claude Desktop (or run /mcp in Claude Code) to reconnect.'
    Write-Host ''
}

# ---------------------------------------------------------------- build and install
Write-Host "[1/3] Building and installing the app (this can take a minute) ..."
& dotnet publish $project -c Release -o $Destination --nologo -v quiet
$publishExit = $LASTEXITCODE
if ($mcpStop) { [void]$mcpStop.Reset(); $mcpStop.Dispose(); $mcpStop = $null } # connectors may start again
if ($publishExit -ne 0) { throw "dotnet publish failed (exit $publishExit). See the messages above." }
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish finished but $exe is missing." }
# Marks this folder as an installed copy (the app's Settings tell people when they run a copy that isn't).
$marker = [ordered]@{ installedAt = (Get-Date).ToString('o'); sourceRepo = $repo } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $Destination '.sessionkeeper-install'), $marker, (New-Object System.Text.UTF8Encoding $false))

if ($NoStartMenu) {
    Write-Host '[2/3] Start menu shortcut: skipped (-NoStartMenu).'
} else {
    $out = & $exe --install-shortcut | ForEach-Object { $_ }
    if ($LASTEXITCODE -ne 0) { throw "Creating the Start menu shortcut failed (exit $LASTEXITCODE)." }
    Write-Host "[2/3] $out"
}

if ($NoSkill) {
    Write-Host '[3/3] Claude Code skill: skipped (-NoSkill).'
} else {
    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
    $skillSrc = Join-Path $repo 'plugin\skills\session-keeper'
    $skillDst = Join-Path $claudeDir 'skills\session-keeper'
    New-Item -ItemType Directory -Force -Path $skillDst | Out-Null
    foreach ($f in 'SKILL.md', 'sessionkeeper.ps1') {
        Copy-Item -LiteralPath (Join-Path $skillSrc $f) -Destination (Join-Path $skillDst $f) -Force
    }
    Write-Host "[3/3] Claude Code skill: $skillDst"
}

$version = & $exe --version | ForEach-Object { $_ }
if ($LASTEXITCODE -ne 0) { throw "Installed exe failed to run (exit $LASTEXITCODE)." }
Write-Host ''
Write-Host "Installed $version"
Write-Host "  at $exe"
if ($Destination -ine $defaultDestination -and -not $NoSkill) {
    Write-Host "Note: the skill looks in $defaultDestination by default. Set SESSIONKEEPER_EXE=$exe so it can find this copy."
}

# ---------------------------------------------------------------- Start with Windows pointing elsewhere
$startup = Get-StartupCommand
$wanted = '"' + $exe + '" --startup'
if ($startup -and $startup -ine $wanted) {
    Write-Host ''
    Write-Host "Start with Windows currently starts a different copy: $startup"
    if ($Interactive -and (Ask 'Point it at the installed copy instead?')) {
        Set-ItemProperty -Path $runKey -Name 'SessionKeeper' -Value $wanted -Type String
        Write-Host 'Start with Windows now starts the installed copy.'
    } else {
        Write-Host 'To change it, open SessionKeeper from the Start menu > Settings and re-tick Start with Windows.'
    }
}

# ---------------------------------------------------------------- offer to open it
if ($Interactive) {
    Write-Host ''
    if (Ask 'Open SessionKeeper now?') {
        Start-Process -FilePath $exe -WorkingDirectory $Destination
        Write-Host 'Opened SessionKeeper.'
    }
}
