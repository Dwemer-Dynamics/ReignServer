$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\ClientDeploymentStorage.ps1')

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("reign-storage-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $source = Join-Path $root 'source.bin'
    [IO.File]::WriteAllBytes($source, [byte[]](1..100))
    $failed = $false
    try { Assert-ReignDeploymentHeadroom -GameRoot $root -StageSources @($source) -AvailableBytes 100 | Out-Null } catch { $failed = $_.Exception.Message -like '*Insufficient free space*' }
    Assert $failed 'Low disk preflight did not fail clearly.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'stage-test'))) 'Preflight touched staging.'
    Assert ((Assert-ReignDeploymentHeadroom -GameRoot $root -StageSources @($source) -AvailableBytes 2GB) -ge 1GB) 'Headroom calculation omitted reserve.'

    $ids = @('run-00000001','run-00000002','run-00000003')
    foreach ($id in $ids) {
        $name = "before-$id"
        $dir = Join-Path $root $name
        New-Item -ItemType Directory -Path (Join-Path $dir 'bin\Win64_Shipping_Client') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'SubModule.xml') -Value '<Id value="ReignBeta" />'
        Set-Content -LiteralPath (Join-Path $dir 'bin\Win64_Shipping_Client\ReignBeta.dll') -Value 'test'
        Write-ReignStorageMarker -Root $root -Kind before -DeploymentId $id
        Start-Sleep -Milliseconds 25
    }
    $unknown = Join-Path $root 'before-unknown-00000004'
    New-Item -ItemType Directory -Path $unknown | Out-Null
    Set-Content -LiteralPath (Join-Path $unknown 'native-crash-evidence.txt') -Value 'preserve'
    $failedModule = Join-Path $root 'failed-run-00000007'
    New-Item -ItemType Directory -Path $failedModule | Out-Null
    Set-Content -LiteralPath (Join-Path $failedModule 'crash.txt') -Value 'preserve'
    $malformed = Join-Path $root 'before-bad-00000008'
    New-Item -ItemType Directory -Path $malformed | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'before-bad-00000008.reign-owned.json') -Value '{}'
    $stale = Join-Path $root 'stage-run-00000005'
    New-Item -ItemType Directory -Path $stale | Out-Null
    Write-ReignStorageMarker -Root $root -Kind stage -DeploymentId 'run-00000005'
    $marker = Join-Path $root 'stage-run-00000005.reign-owned.json'
    $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    $record.createdUtc = [DateTimeOffset]::UtcNow.AddDays(-2).ToString('o')
    Set-Content -LiteralPath $marker -Value ($record | ConvertTo-Json -Compress)
    $recent = Join-Path $root 'stage-run-00000006'
    New-Item -ItemType Directory -Path $recent | Out-Null
    Write-ReignStorageMarker -Root $root -Kind stage -DeploymentId 'run-00000006'

    # Failed activation never calls retention; the previous rollback remains.
    Assert (Test-Path -LiteralPath (Join-Path $root 'before-run-00000001')) 'Rollback disappeared before successful activation.'
    Invoke-ReignStorageRetention -Root $root -KeepBackups 2
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'before-run-00000001'))) 'Old marked backup was retained.'
    foreach ($id in $ids[1..2]) { Assert (Test-Path -LiteralPath (Join-Path $root "before-$id")) 'Newest rollback backup was deleted.' }
    Assert (Test-Path -LiteralPath $unknown) 'Unmarked backup or crash evidence was deleted.'
    Assert (Test-Path -LiteralPath $failedModule) 'Failed activation evidence was deleted.'
    Assert (Test-Path -LiteralPath $malformed) 'Malformed marker directory was deleted.'
    Assert (-not (Test-Path -LiteralPath $stale)) 'Stale owned stage was retained.'
    Assert (Test-Path -LiteralPath $recent) 'Recent stage was deleted.'

    $activationRoot = Join-Path $root 'activation'
    $module = Join-Path $activationRoot 'ReignBeta'
    $arena = Join-Path $activationRoot 'ArenaOverhaul'
    $candidate = Join-Path $activationRoot 'stage-activation-00000009'
    $backup = Join-Path $activationRoot 'before-activation-00000009'
    $arenaBackup = Join-Path $activationRoot 'arena-overhaul-before-activation-00000009'
    foreach ($dir in @($module,$arena,$candidate)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -LiteralPath (Join-Path $module 'old.txt') -Value 'old client'
    Set-Content -LiteralPath (Join-Path $arena 'old.txt') -Value 'old arena'
    Set-Content -LiteralPath (Join-Path $candidate 'new.txt') -Value 'new client'
    $failed = $false
    try {
        Invoke-ReignClientActivation -Module $module -Stage $candidate -Backup $backup -LegacyArena $arena -LegacyArenaBackup $arenaBackup -DeploymentRoot $activationRoot -DeploymentId 'activation-00000009' -Verify { throw 'verification failed' } | Out-Null
    } catch { $failed = $_.Exception.Message -like '*verification failed*' }
    Assert $failed 'Failed activation did not report the verification failure.'
    Assert (Test-Path -LiteralPath (Join-Path $module 'old.txt')) 'Rollback did not restore the previous client.'
    Assert (Test-Path -LiteralPath (Join-Path $arena 'old.txt')) 'Rollback did not restore standalone Arena Overhaul.'
    Assert (Test-Path -LiteralPath (Join-Path $activationRoot 'failed-activation-00000009/new.txt')) 'Failed candidate was not retained for inspection.'
    Assert (-not (Test-Path -LiteralPath $backup)) 'Rollback left the previous client outside Modules.'

    $candidate = Join-Path $activationRoot 'stage-activation-00000010'
    New-Item -ItemType Directory -Path $candidate | Out-Null
    Set-Content -LiteralPath (Join-Path $candidate 'new.txt') -Value 'verified client'
    $result = Invoke-ReignClientActivation -Module $module -Stage $candidate -Backup $backup -LegacyArena $arena -LegacyArenaBackup $arenaBackup -DeploymentRoot $activationRoot -DeploymentId 'activation-00000010' -Verify {
        param($activatedModule)
        if (-not (Test-Path -LiteralPath (Join-Path $activatedModule 'new.txt'))) { throw 'candidate missing' }
        Write-Output 'verified'
    }
    Assert ($result -is [pscustomobject]) 'Verifier output polluted activation result.'
    Assert $result.ModuleMoved 'Successful activation did not record its rollback backup.'
    Assert (Test-Path -LiteralPath (Join-Path $backup 'old.txt')) 'Successful activation lost the previous client.'
    Write-Output 'Client deployment storage tests passed.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
