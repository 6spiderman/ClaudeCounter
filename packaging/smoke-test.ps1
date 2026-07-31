<#
.SYNOPSIS
    Launches a published ClaudeCounter.exe and checks it survives startup.

.DESCRIPTION
    Unit tests cannot catch a crash in the WinForms wiring - the app has to
    actually run. A first-run crash shipped once because every dialog was
    verified in isolation and the real executable never was.

    Moves any existing ClaudeCounter data aside so the app sees a genuine
    first-run, launches it, and fails if the process died or logged an error.
    Whatever the run created is deleted and the original data moved back.

    Note it cannot be isolated with environment variables: the app resolves its
    folders through Environment.SpecialFolder, which reads the shell's known
    folders and ignores APPDATA/LOCALAPPDATA.

.PARAMETER ExePath
    The published executable. Defaults to dist\win-x64\ClaudeCounter.exe.

.PARAMETER Seconds
    How long to let it run. Default 12 - long enough for the first poll.

.EXAMPLE
    pwsh -File packaging/smoke-test.ps1
#>

[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\win-x64\ClaudeCounter.exe'),
    [int]$Seconds = 12
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "Not found: $ExePath. Publish first." }
$ExePath = (Resolve-Path $ExePath).Path

# ClaudeCounter is single-instance via a named mutex, so a second copy exits
# immediately with code 0 and writes nothing. Without this check that looks
# identical to a silent startup crash.
$already = @(Get-Process ClaudeCounter -ErrorAction SilentlyContinue)
if ($already.Count -gt 0) {
    Write-Host "ClaudeCounter is already running:" -ForegroundColor Yellow
    $already | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.Path)" -ForegroundColor Yellow }
    throw "Close the running instance first - a second one exits on the single-instance mutex and the test cannot tell that apart from a crash."
}

$dataDirs = @("$env:LOCALAPPDATA\ClaudeCounter", "$env:APPDATA\ClaudeCounter")
$stash = Join-Path ([IO.Path]::GetTempPath()) "cc-smoke-stash-$(Get-Random)"
$moved = @{}

# The app registers autostart on first run. Capture whatever is there now so it
# can be put back exactly - otherwise the smoke run leaves the user's autostart
# pointing at a build directory.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueBefore = try { (Get-ItemProperty $runKey -Name ClaudeCounter -ErrorAction Stop).ClaudeCounter } catch { $null }

# Move real data aside so the run is a true first-run and cannot corrupt an
# existing session. Restored in the finally block whatever happens.
foreach ($d in $dataDirs) {
    if (Test-Path $d) {
        $dest = Join-Path $stash (Split-Path $d -Leaf) + "-" + (Split-Path (Split-Path $d -Parent) -Leaf)
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Move-Item $d $dest
        $moved[$d] = $dest
        Write-Host "Stashed $d"
    }
}

Write-Host "Launching: $ExePath"

$exitCode = 0
try {
    $proc = Start-Process -FilePath $ExePath -PassThru

    Write-Host "PID $($proc.Id) - watching for $Seconds seconds..."

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 500
    }

    $log = "$env:LOCALAPPDATA\ClaudeCounter\logs\claudecounter.log"

    if ($proc.HasExited) {
        Write-Host "`nFAIL: the process exited after $([int]((Get-Date) - $proc.StartTime).TotalSeconds)s with code $($proc.ExitCode)" -ForegroundColor Red
        if ($proc.ExitCode -eq 0) {
            Write-Host "Exit code 0 with no log usually means another instance grabbed the single-instance mutex." -ForegroundColor Yellow
        }
        if (Test-Path $log) {
            Write-Host "`n--- log ---" -ForegroundColor DarkGray
            Get-Content $log
        } else {
            Write-Host "No log was written - it died before logging anything." -ForegroundColor Red
        }
        Write-Host "`n--- recent .NET Runtime crash events ---" -ForegroundColor DarkGray
        Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'; StartTime=(Get-Date).AddMinutes(-2)} -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty Message
        $exitCode = 1
    }
    else {
        Write-Host "`nStill running after $Seconds seconds." -ForegroundColor Green
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Milliseconds 500

        if (Test-Path $log) {
            $errors = Select-String -Path $log -Pattern '\[ERROR\]' -ErrorAction SilentlyContinue
            if ($errors) {
                Write-Host "FAIL: errors in the log:" -ForegroundColor Red
                $errors | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
                $exitCode = 1
            } else {
                Write-Host "No errors logged." -ForegroundColor Green
                Write-Host "`n--- log ---" -ForegroundColor DarkGray
                Get-Content $log
            }
        } else {
            Write-Host "FAIL: no log file was created." -ForegroundColor Red
            $exitCode = 1
        }
    }
}
finally {
    Get-Process -Name ClaudeCounter -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $ExePath } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    # Discard whatever this run created, then put the real data back.
    foreach ($d in $dataDirs) {
        Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($original in $moved.Keys) {
        Move-Item $moved[$original] $original -Force -ErrorAction SilentlyContinue
        Write-Host "Restored $original"
    }
    Remove-Item $stash -Recurse -Force -ErrorAction SilentlyContinue

    # Put autostart back exactly as it was, whether that means restoring the
    # previous value or removing one the run created.
    if ($null -eq $runValueBefore) {
        Remove-ItemProperty $runKey -Name ClaudeCounter -ErrorAction SilentlyContinue
        Write-Host "Removed the autostart entry created by this run"
    } else {
        Set-ItemProperty $runKey -Name ClaudeCounter -Value $runValueBefore
        Write-Host "Restored autostart to $runValueBefore"
    }
}

if ($exitCode -ne 0) { throw "Smoke test failed." }
Write-Host "`nSmoke test passed." -ForegroundColor Green
