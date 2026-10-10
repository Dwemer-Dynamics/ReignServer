param([Parameter(Mandatory=$true)][string]$Launcher)
$ErrorActionPreference = 'Stop'
. $Launcher -LibraryOnly
function Assert-Calls([string]$expected) {
    $actual = $script:calls -join ','
    if ($actual -ne $expected) { throw "Expected $expected; observed $actual" }
}
function Run-Case([string]$case) {
    $script:calls = New-Object 'Collections.Generic.List[string]'
    $script:healthCount = 0
    $script:currentCase = $case
    Ensure-ReignServer -Health {
        $script:healthCount++
        if ($script:currentCase -eq 'healthy') { return $true }
        if ($script:currentCase -eq 'never-ready') { return $false }
        return $script:healthCount -gt 1
    } -Recover {
        $script:calls.Add('recover')
        if ($script:currentCase -eq 'recovery-failure') { throw 'recovery failed' }
    } -StartServer {
        $script:calls.Add('start')
        if ($script:currentCase -eq 'start-failure') { throw 'startup failed' }
    } -OpenBrowser { $script:calls.Add('browser') }
}
Run-Case 'healthy' | Out-Null; Assert-Calls 'browser'
Run-Case 'unhealthy' | Out-Null; Assert-Calls 'recover,start,browser'
foreach ($case in @('recovery-failure','start-failure','never-ready')) {
    $failed = $false
    try { Run-Case $case | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "Expected failure: $case" }
    if ($case -eq 'recovery-failure') { Assert-Calls 'recover' }
    else { Assert-Calls 'recover,start' }
}
# Exercise the real Windows -> WSL argument boundary without reading a file,
# starting a service/listener, or touching installation/campaign state.
foreach ($fixture in @(
    @('C:\Users\speed\.reign\setup\reign-recover.py', '/mnt/c/Users/speed/.reign/setup/reign-recover.py'),
    @('C:\reign launcher fixture\helper.py', '/mnt/c/reign launcher fixture/helper.py')
)) {
    $resolved = Resolve-ReignRecoveryPath $fixture[0]
    if ($resolved -cne $fixture[1]) { throw "WSL path conversion failed: $resolved" }
}
Initialize-ReignWindowInterop # Compile production adapters without inspecting/controlling desktop UI.
foreach ($name in @('REIGN', 'REIGN - Memory usage - 31.3 MB')) {
    if (-not [ReignLauncherWindow]::IsReignTab($name)) { throw 'Observed Chrome Reign tab name was rejected.' }
}
foreach ($name in @('OTHER', 'REIGN fans')) {
    if ([ReignLauncherWindow]::IsReignTab($name)) { throw 'Unrelated browser tab name was accepted.' }
}
foreach ($existing in @($true, $false)) {
    $script:existingUi = $existing
    $script:calls.Clear()
    Show-ReignControlCenter -FindExisting { if ($script:existingUi) { [IntPtr]123 } else { [IntPtr]::Zero } } `
        -Activate { param($window) if ($window -ne [IntPtr]123) { throw 'Wrong existing window.' }; $script:calls.Add('activate') } `
        -OpenNew { $script:calls.Add('new') }
    if ($existing) { Assert-Calls 'activate' } else { Assert-Calls 'new' }
}
$script:calls.Clear()
$activationFailed = $false
try {
    Show-ReignControlCenter -FindExisting { [IntPtr]123 } -Activate { throw 'activation failed' } -OpenNew { $script:calls.Add('new') }
} catch { $activationFailed = $true }
if (-not $activationFailed) { throw 'Expected existing-window activation failure.' }
Assert-Calls '' # Failed activation cannot create an extra Reign tab.
foreach ($failedUnit in @($true, $false)) {
    $script:failedUnit = $failedUnit
    $script:calls.Clear()
    Reset-ReignFailureIfNeeded -IsFailed { $script:failedUnit } -Reset { $script:calls.Add('reset') }
    if ($failedUnit) { Assert-Calls 'reset' } else { Assert-Calls '' }
}
$resetFailed = $false
try { Reset-ReignFailureIfNeeded -IsFailed { $true } -Reset { throw 'reset failed' } } catch { $resetFailed = $true }
if (-not $resetFailed) { throw 'Expected failure when a failed unit cannot be reset.' }
Write-Output 'reign-launcher-startup-contract-v1: 5 startup, 2 WSL path, 3 existing-UI, 4 tab-name and 3 failed-unit cases passed'
