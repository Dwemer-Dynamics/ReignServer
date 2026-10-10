[CmdletBinding()]
param([switch]$OpenControlCenter, [switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'

function Ensure-ReignServer {
    param([scriptblock]$Health, [scriptblock]$Recover, [scriptblock]$StartServer, [scriptblock]$OpenBrowser)
    $healthResult = & $Health
    if (-not $healthResult) {
        & $Recover | Out-Null
        & $StartServer | Out-Null
        $healthResult = & $Health
        if (-not $healthResult) { throw 'The restarted ReignServer did not become healthy.' }
    }
    & $OpenBrowser | Out-Null
    return $healthResult
}

function Resolve-ReignRecoveryPath {
    param([string]$Recovery)
    # --exec bypasses Linux shell parsing; slash form also preserves Windows paths.
    $windowsPath = $Recovery.Replace('\', '/')
    $linuxPath = (& wsl.exe -d ReignServer --exec /usr/bin/wslpath -u $windowsPath | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $linuxPath.StartsWith('/mnt/')) {
        throw 'The Reign recovery helper could not be resolved in WSL.'
    }
    return $linuxPath
}

function Reset-ReignFailureIfNeeded {
    param(
        [scriptblock]$IsFailed = {
            & wsl.exe -d ReignServer -u root -- systemctl is-failed --quiet reignserver.service
            return $LASTEXITCODE -eq 0
        },
        [scriptblock]$Reset = {
            & wsl.exe -d ReignServer -u root -- systemctl reset-failed reignserver.service
            if ($LASTEXITCODE -ne 0) { throw 'The Reign service failure state could not be reset.' }
        }
    )
    # An inactive unit may have been unloaded by systemd; it has nothing to reset.
    if (& $IsFailed) { & $Reset | Out-Null }
}

function Initialize-ReignWindowInterop {
    if ('ReignLauncherWindow' -as [type]) { return }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    Add-Type -ReferencedAssemblies 'System.dll', ([Windows.Automation.AutomationElement].Assembly.Location), ([Windows.Automation.ControlType].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
public static class ReignLauncherWindow {
    delegate bool WindowVisitor(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visitor, IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    public static bool IsReignTab(string name) {
        return name == "REIGN" || (name != null && name.StartsWith("REIGN - Memory usage -", StringComparison.Ordinal));
    }
    static bool LocalAddress(AutomationElement window) {
        // Inspect browser chrome only; never read website inputs or credentials.
        var toolbar = window.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar),
                             new PropertyCondition(AutomationElement.AutomationIdProperty, "view_1000")));
        if (toolbar == null) return false;
        var address = toolbar.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "view_1012"));
        object pattern;
        if (address == null || !address.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return false;
        string value = ((ValuePattern)pattern).Current.Value;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
        Uri uri;
        return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "http" &&
            (uri.Host == "127.0.0.1" || uri.Host == "localhost") && uri.Port == 8089 && uri.AbsolutePath == "/";
    }
    public static IntPtr FindExisting() {
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr hwnd, IntPtr state) {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try {
                // Chrome is the selected local default browser. Do not revive Edge.
                if (IsWindowVisible(hwnd) && Process.GetProcessById((int)pid).ProcessName == "chrome") windows.Add(hwnd);
            } catch (ArgumentException) { }
            return true;
        }, IntPtr.Zero);
        foreach (IntPtr hwnd in windows) {
            try {
                var window = AutomationElement.FromHandle(hwnd);
                if (LocalAddress(window)) return hwnd;
                var strip = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "view_21"));
                if (strip == null) continue;
                var tabs = strip.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                foreach (AutomationElement tab in tabs) {
                    if (!IsReignTab(tab.Current.Name)) continue;
                    object pattern;
                    if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) continue;
                    ((SelectionItemPattern)pattern).Select();
                    // Chrome may publish its address-bar update after selection returns.
                    for (int attempt = 0; attempt < 5; attempt++) {
                        if (LocalAddress(window)) return hwnd;
                        System.Threading.Thread.Sleep(100);
                    }
                }
            } catch (ElementNotAvailableException) { }
              catch (InvalidOperationException) { }
        }
        return IntPtr.Zero;
    }
    public static void Activate(IntPtr hwnd) {
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
        SetForegroundWindow(hwnd);
    }
}
'@
}

function Show-ReignControlCenter {
    param(
        [scriptblock]$FindExisting = { Initialize-ReignWindowInterop; [ReignLauncherWindow]::FindExisting() },
        [scriptblock]$Activate = { param($window) [ReignLauncherWindow]::Activate($window) },
        [scriptblock]$OpenNew = { Start-Process 'http://127.0.0.1:8089/' }
    )
    $existingWindow = & $FindExisting
    if ($existingWindow -and $existingWindow -ne [IntPtr]::Zero) {
        & $Activate $existingWindow | Out-Null
    } else {
        & $OpenNew | Out-Null
    }
}

if ($LibraryOnly) { return }

function Get-ReignServerHealth {
    try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8089/health' -TimeoutSec 3 }
    catch { return $null }
    if ($health.ok -and $health.service -eq 'BannerlordReignServer' -and
        $health.protocolVersion -eq 1 -and $health.serverVersion -eq $record.version -and
        $health.databaseName -eq 'reign' -and $health.databaseSchemaUpToDate -eq $true) { return $health }
    return $null
}

$mutex = $null
$hasMutex = $false
try {
$recordPath = Join-Path $env:USERPROFILE '.reign\installation.json'
if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw 'The Reign installation record is missing.' }
$record = Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($record.schema -ne 'reign-installation-v1' -or $record.protocolVersion -ne 1 -or
    $record.serverMode -ne 'dwemerdistro-wsl' -or $record.wslDistro -cne 'ReignServer') {
    throw 'The Reign installation record must select the dedicated ReignServer WSL distro.'
}

$registrations = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' | ForEach-Object { Get-ItemProperty $_.PSPath })
$selected = @($registrations | Where-Object { $_.DistributionName -ceq 'ReignServer' })
if ($selected.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $selected[0].BasePath 'ext4.vhdx') -PathType Leaf)) {
    throw 'The separate ReignServer WSL registration or its virtual disk is missing.'
}
if (@($registrations | Where-Object { $_.DistributionName -cne 'ReignServer' -and
    [IO.Path]::GetFullPath($_.BasePath) -eq [IO.Path]::GetFullPath($selected[0].BasePath) }).Count -ne 0) {
    throw 'ReignServer shares a WSL storage root with another distro.'
}

$mutex = New-Object Threading.Mutex($false, 'Local\Reign.Server.Startup')
try { $hasMutex = $mutex.WaitOne(120000) } catch [Threading.AbandonedMutexException] { $hasMutex = $true }
if (-not $hasMutex) { throw 'Another Reign startup is still running. Wait for it to finish and retry.' }

# WSL can power down a systemd distro after the final Windows WSL client exits.
# Keep one hidden session attached; flock makes repeated shortcut launches harmless.
$keepAlive = @('-d','ReignServer','-u','reign','--','/usr/bin/flock','-n',
    '/var/www/html/ReignServer/data/launcher-keepalive.lock','/bin/sleep','infinity')
Start-Process -FilePath "$env:SystemRoot\System32\wsl.exe" -ArgumentList $keepAlive -WindowStyle Hidden

& wsl.exe -d ReignServer -u root -- systemctl start postgresql apache2
if ($LASTEXITCODE -ne 0) { throw 'ReignServer PostgreSQL or web proxy could not start.' }
$health = Ensure-ReignServer -Health { Get-ReignServerHealth } -Recover {
    $recovery = Join-Path $PSScriptRoot 'reign-recover.py'
    if (-not (Test-Path -LiteralPath $recovery -PathType Leaf)) { throw 'The Reign recovery helper is missing. Repair the launcher installation.' }
    # Stop the managed unit first so its restart policy cannot race recovery.
    & wsl.exe -d ReignServer -u root -- systemctl stop reignserver.service
    # A failed stop of an unhealthy unit is followed by exact owned-process recovery.
    $recoveryWsl = Resolve-ReignRecoveryPath $recovery
    & wsl.exe -d ReignServer -u root --exec /usr/bin/python3 -B $recoveryWsl
    if ($LASTEXITCODE -ne 0) { throw 'Reign process recovery failed; see the launcher error output. No unrelated process was selected.' }
    Reset-ReignFailureIfNeeded
} -StartServer {
    & wsl.exe -d ReignServer -u root -- systemctl start reignserver.service
    if ($LASTEXITCODE -ne 0) { throw 'The dedicated ReignServer service did not start. Check its service log.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Get-ReignServerHealth)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'ReignServer did not become healthy within 60 seconds.' }
        Start-Sleep -Milliseconds 500
    }
} -OpenBrowser { if ($OpenControlCenter) { Show-ReignControlCenter } }
Write-Output "ReignServer $($health.serverVersion) is running in its own WSL distro."
} catch {
    $message = $_.Exception.Message
    Write-Error -Message $message -ErrorAction Continue
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show($message, 'Reign startup failed', 'OK', 'Error')
    exit 1
} finally {
    if ($hasMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
