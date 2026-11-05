<#
.SYNOPSIS
  Installs or updates Stowdeck for the current user (no admin rights needed).

.DESCRIPTION
  The easy way is to double-click install.cmd, which runs this script with -Interactive.

  1. Builds and publishes the app to -Destination (default %LOCALAPPDATA%\Programs\Stowdeck).
  2. Adds a Start menu shortcut (skip with -NoStartMenu).
  3. Installs the Claude Code skill to <Claude config dir>\skills\stowdeck (skip with -NoSkill).
     The Claude config dir is %CLAUDE_CONFIG_DIR% if set, otherwise %USERPROFILE%\.claude.

  With -Interactive (what install.cmd uses) it also:
  - offers to close a running Stowdeck cleanly first (it asks; it never kills it),
  - offers to point "Start with Windows" at the installed copy if it currently starts a different one,
  - offers to open the installed copy at the end.
  Without -Interactive it stops with a message if Stowdeck is running, and changes nothing else.

  Stowdeck used to be called SessionKeeper. When updating from it, this script also:
  - offers (with -Interactive) to close a running SessionKeeper and its MCP connectors; otherwise it carries on and
    says so (the app then moves the old settings once SessionKeeper is closed),
  - replaces SessionKeeper's "Start with Windows" entry with Stowdeck's, only if it was on (not turned off in
    Task Manager); an entry that was off is removed without adding one,
  - removes SessionKeeper's Start menu shortcut, if it points at SessionKeeper's exe,
  - removes the old skill copy in <Claude config dir>\skills\session-keeper (its SKILL.md and sessionkeeper.ps1),
    unless -NoSkill is used, in which case it only says it's there,
  - reports, but never deletes, the old app folder %LOCALAPPDATA%\Programs\SessionKeeper.
  The app's first run copies %APPDATA%\SessionKeeper to %APPDATA%\Stowdeck and keeps the original as a backup.

  "Start with Windows" is never turned on here, except to carry over SessionKeeper's entry as described above;
  otherwise tick it in the app's Settings if you want it.
  Installing under Program Files is refused: it needs admin rights, and updates would fail.
  Requires the .NET 8 SDK (dotnet) to build. Stops at the first error.

.EXAMPLE
  install.cmd
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1 -Destination D:\Tools\Stowdeck -NoStartMenu
#>
param(
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Stowdeck'),
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
$project = Join-Path $repo 'src\Stowdeck\Stowdeck.csproj'
$defaultDestination = Join-Path $env:LOCALAPPDATA 'Programs\Stowdeck'
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
    $p = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object Name -eq 'Stowdeck'
    if ($p) { return [string]$p.Value } else { return $null }
}

if (-not (Test-Path -LiteralPath $project)) { throw "Can't find $project. Run this from the Stowdeck repository folder." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 8 SDK (dotnet) is required: https://dotnet.microsoft.com/download/dotnet/8.0' }

$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')
foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { $_ }) {
    if (($Destination + '\').StartsWith(([System.IO.Path]::GetFullPath($pf).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Don't install Stowdeck under Program Files ($Destination): that needs admin rights and updates would fail. " +
              "Use the default per-user folder ($defaultDestination) or another folder you own."
    }
}
$exe = Join-Path $Destination 'Stowdeck.exe'

Write-Host ''
Write-Host "Stowdeck installer"
Write-Host "  from: $repo"
Write-Host "  to:   $Destination"
Write-Host ''

# ---------------------------------------------------------------- a running copy locks its files
# That includes a copy started from this repository's own build output, which the build overwrites.
$buildOutput = [System.IO.Path]::GetFullPath((Join-Path $repo 'src\Stowdeck\bin')) + '\'
# MCP connectors (Stowdeck.exe --mcp, started by Claude Desktop or Claude Code) are handled separately below.
function Get-ConnectorIds {
    @(Get-CimInstance Win32_Process -Filter "Name='Stowdeck.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '(^|\s)--mcp(\s|$)' } | ForEach-Object { [int]$_.ProcessId })
}
function Get-LockingProcesses([bool] $connectors) {
    $mcp = Get-ConnectorIds
    Get-Process Stowdeck -ErrorAction SilentlyContinue | Where-Object {
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
    Write-Host "Stowdeck is running from $where."
    if (-not $Interactive) {
        throw "Close Stowdeck (including its tray icon) and run this again, or double-click install.cmd, which can close it for you."
    }
    if (-not (Ask 'Close it now so the install can continue?')) {
        throw 'Install cancelled. Close Stowdeck (including its tray icon) and run install.cmd again.'
    }
    # Ask it to exit cleanly: current versions listen for this signal (works from the tray too);
    # older ones are asked to close their window. Nothing is ever killed.
    try {
        $signal = [System.Threading.EventWaitHandle]::OpenExisting('Local\Stowdeck.Exit')
        [void]$signal.Set()
        $signal.Dispose()
    } catch { }
    foreach ($p in $running) { try { if (-not $p.HasExited) { [void]$p.CloseMainWindow() } } catch { } }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-BlockingCopies).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-BlockingCopies).Count -gt 0) {
        throw "Stowdeck didn't close (it may be showing a dialog, or it's an older version in the tray). " +
              "Close it yourself (right-click its tray icon > Exit) and run install.cmd again."
    }
    Write-Host 'Closed Stowdeck.'
    Write-Host ''
}

# MCP connectors lock the exe too. They have their own stop signal (separate from the app's): while it's set, running
# ones exit cleanly and new ones exit straight away. It's cleared once the new version is in place.
$mcpStop = $null
$connectors = @(Get-BlockingConnectors)
if ($connectors.Count -gt 0) {
    Write-Host "Pausing $($connectors.Count) Stowdeck MCP connector(s) started by Claude Desktop or Claude Code, for the update."
    $mcpStop = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'Local\Stowdeck.McpStop')
    [void]$mcpStop.Set()
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-BlockingConnectors).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-BlockingConnectors).Count -gt 0) {
        [void]$mcpStop.Reset()
        throw "The Stowdeck MCP connector didn't stop (it may be an older version). Close Claude Desktop and Claude Code, then run install.cmd again."
    }
    Write-Host 'Paused. Afterwards, restart Claude Desktop (or run /mcp in Claude Code) to reconnect.'
    Write-Host ''
}

# ---------------------------------------------------------------- the app's old name
# Stowdeck used to be called SessionKeeper. While SessionKeeper (its window, or an MCP connector Claude started) still
# runs, it keeps writing to the old settings folder, so the app puts off moving that folder. Offer to close it first.
# Only processes of the old app count: the name alone isn't enough, since another company's product shares it.
function Test-OldExe([string]$Path) {
    if (-not $Path -or [System.IO.Path]::GetFileName($Path) -ne 'SessionKeeper.exe' -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    return ($info.ProductName -eq 'SessionKeeper' -and $info.Comments -eq 'See, save and reopen Claude Code sessions.')
}
function Get-OldAppProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name = 'SessionKeeper.exe'" -ErrorAction SilentlyContinue | Where-Object { Test-OldExe $_.ExecutablePath })
}
$oldRunning = Get-OldAppProcesses
if ($oldRunning.Count -gt 0) {
    $oldConnectors = @($oldRunning | Where-Object { $_.CommandLine -match '--mcp' })
    $oldWindows = $oldRunning.Count - $oldConnectors.Count
    if ($oldWindows -gt 0) { Write-Host 'SessionKeeper (the old name of this app) is running.' }
    if ($oldConnectors.Count -gt 0) { Write-Host "$($oldConnectors.Count) SessionKeeper MCP connector(s) started by Claude Desktop or Claude Code are running." }
    if ($Interactive -and (Ask 'Close them now? Stowdeck replaces them.')) {
        # The old connectors' own stop signal, as its installer used; the window's exit signal; then CloseMainWindow.
        $oldMcpStop = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'Local\SessionKeeper.McpStop')
        [void]$oldMcpStop.Set()
        try {
            $signal = [System.Threading.EventWaitHandle]::OpenExisting('Local\SessionKeeper.Exit')
            [void]$signal.Set()
            $signal.Dispose()
        } catch { }
        foreach ($o in $oldRunning) { try { $p = Get-Process -Id $o.ProcessId -ErrorAction Stop; [void]$p.CloseMainWindow() } catch { } }
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Get-OldAppProcesses).Count -gt 0) { Start-Sleep -Milliseconds 300 }
        $oldMcpStop.Dispose()
        if ((Get-OldAppProcesses).Count -gt 0) {
            Write-Host "SessionKeeper didn't close. The install carries on; close it yourself later (right-click its tray icon > Exit)."
        } else {
            Write-Host 'Closed SessionKeeper.'
        }
    } else {
        Write-Host 'The install carries on. Close SessionKeeper yourself when you can (right-click its tray icon > Exit).'
    }
    if ($oldConnectors.Count -gt 0) {
        Write-Host 'Remove the old connector from Claude Desktop or Claude Code, or they start it again: see "The name" in the README.'
    }
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
[System.IO.File]::WriteAllText((Join-Path $Destination '.stowdeck-install'), $marker, (New-Object System.Text.UTF8Encoding $false))

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
    $skillSrc = Join-Path $repo 'plugin\skills\stowdeck'
    $skillDst = Join-Path $claudeDir 'skills\stowdeck'
    New-Item -ItemType Directory -Force -Path $skillDst | Out-Null
    foreach ($f in 'SKILL.md', 'stowdeck.ps1') {
        Copy-Item -LiteralPath (Join-Path $skillSrc $f) -Destination (Join-Path $skillDst $f) -Force
    }
    Write-Host "[3/3] Claude Code skill: $skillDst"
}

$version = & $exe --version | ForEach-Object { $_ }
if ($LASTEXITCODE -ne 0) { throw "Installed exe failed to run (exit $LASTEXITCODE)." }
Write-Host ''
Write-Host "Installed $version"
Write-Host "  at $exe"
if ($Destination -ine $defaultDestination) {
    Write-Host "Note: the Claude Code skill and plugin look in $defaultDestination, then on PATH. Add $Destination to your PATH so they can find this copy."
}

# ---------------------------------------------------------------- moving over from SessionKeeper
# The first run of Stowdeck.exe above copied %APPDATA%\SessionKeeper to %APPDATA%\Stowdeck (see RenameMigration.cs).
$moveNote = Join-Path $env:APPDATA 'Stowdeck\moved-from-SessionKeeper.txt'
if ((Test-Path -LiteralPath $moveNote) -and (Get-Item -LiteralPath $moveNote).LastWriteTime -gt (Get-Date).AddMinutes(-10)) {
    Write-Host ''
    Write-Host "Moved your settings, session names and snapshots from SessionKeeper to $(Join-Path $env:APPDATA 'Stowdeck')."
    Write-Host "The old folder is kept as a backup: see $moveNote"
}
# SessionKeeper's Start with Windows entry now starts Stowdeck instead: only an entry that starts the old app's exe,
# and only if it was on. Task Manager records entries the user turned off under StartupApproved (odd first byte = off).
$oldStartup = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).PSObject.Properties |
    Where-Object { $_.Name -eq 'SessionKeeper' -and (Test-OldExe ($(if (([string]$_.Value).Trim() -match '^"([^"]+)"') { $Matches[1] } else { (([string]$_.Value).Trim() -split ' ')[0] }))) }
if ($oldStartup) {
    $approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    $oldState = (Get-ItemProperty -Path $approvedKey -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object Name -eq 'SessionKeeper'
    $oldWasOff = $oldState -and ($oldState.Value -is [byte[]]) -and ($oldState.Value.Length -gt 0) -and (($oldState.Value[0] -band 1) -eq 1)
    Remove-ItemProperty -Path $runKey -Name 'SessionKeeper'
    if ($oldState) { Remove-ItemProperty -Path $approvedKey -Name 'SessionKeeper' -ErrorAction SilentlyContinue }
    if ($oldWasOff) {
        Write-Host 'Start with Windows: removed the old SessionKeeper entry, which was turned off.'
    } elseif (-not (Get-StartupCommand)) {
        Set-ItemProperty -Path $runKey -Name 'Stowdeck' -Value ('"' + $exe + '" --startup') -Type String
        Write-Host 'Start with Windows: replaced the SessionKeeper entry, so it now starts Stowdeck.'
    } else {
        Write-Host 'Start with Windows: removed the old SessionKeeper entry.'
    }
}
# SessionKeeper's Start menu shortcut, if it still points at the old app's exe.
$oldShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SessionKeeper.lnk'
if (Test-Path -LiteralPath $oldShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $oldTarget = $shell.CreateShortcut($oldShortcut).TargetPath
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    if (Test-OldExe $oldTarget) {
        Remove-Item -LiteralPath $oldShortcut
        Write-Host 'Start menu: removed the old SessionKeeper shortcut.'
    }
}
# The old skill copy, which would load alongside the new one. Only the two files the old installer copied are removed.
$oldClaudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$oldSkill = Join-Path $oldClaudeDir 'skills\session-keeper'
$oldSkillMd = Join-Path $oldSkill 'SKILL.md'
if ((Test-Path -LiteralPath $oldSkillMd) -and (Select-String -LiteralPath $oldSkillMd -Pattern '^name:\s*session-keeper\s*$' -Quiet)) {
    if ($NoSkill) {
        Write-Host "Claude Code skill: the old SessionKeeper skill is still in $oldSkill. Delete that folder if you use the plugin, or Claude Code loads both."
    } else {
        foreach ($f in 'SKILL.md', 'sessionkeeper.ps1') { Remove-Item -LiteralPath (Join-Path $oldSkill $f) -ErrorAction SilentlyContinue }
        if (-not (Get-ChildItem -LiteralPath $oldSkill -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $oldSkill }
        Write-Host "Claude Code skill: removed the old SessionKeeper skill from $oldSkill."
    }
}
# The old app folder isn't removed: it's only reported.
$oldAppDir = Join-Path $env:LOCALAPPDATA 'Programs\SessionKeeper'
if (Test-Path -LiteralPath (Join-Path $oldAppDir 'SessionKeeper.exe')) {
    Write-Host "The old SessionKeeper app is still in $oldAppDir. Stowdeck doesn't use it; delete that folder once you're happy."
}

# ---------------------------------------------------------------- Start with Windows pointing elsewhere
$startup = Get-StartupCommand
$wanted = '"' + $exe + '" --startup'
if ($startup -and $startup -ine $wanted) {
    Write-Host ''
    Write-Host "Start with Windows currently starts a different copy: $startup"
    if ($Interactive -and (Ask 'Point it at the installed copy instead?')) {
        Set-ItemProperty -Path $runKey -Name 'Stowdeck' -Value $wanted -Type String
        Write-Host 'Start with Windows now starts the installed copy.'
    } else {
        Write-Host 'To change it, open Stowdeck from the Start menu > Settings and re-tick Start with Windows.'
    }
}

# ---------------------------------------------------------------- offer to open it
if ($Interactive) {
    Write-Host ''
    if (Ask 'Open Stowdeck now?') {
        Start-Process -FilePath $exe -WorkingDirectory $Destination
        Write-Host 'Opened Stowdeck.'
    }
}
