function Get-ReignTreeBytes([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Deployment source is missing: $Path" }
    $item = Get-Item -LiteralPath $Path
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected deployment source is unsupported: $Path" }
    if (-not $item.PSIsContainer) { return [long]$item.Length }
    $bytes = [long]0
    foreach ($child in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected deployment source is unsupported: $($child.FullName)" }
        if (-not $child.PSIsContainer) { $bytes += [long]$child.Length }
    }
    return $bytes
}

function Assert-ReignDeploymentHeadroom([string]$GameRoot, [string[]]$StageSources, [long]$AvailableBytes = -1) {
    $needed = [long]0
    foreach ($source in $StageSources) { $needed += Get-ReignTreeBytes $source }
    # Copies, filesystem overhead, and an emergency reserve must all fit before staging begins.
    $reserve = [long][Math]::Max(1GB, [Math]::Ceiling($needed * 0.1))
    $required = $needed + $reserve
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($GameRoot)))
    $available = if ($AvailableBytes -ge 0) { $AvailableBytes } else { $drive.AvailableFreeSpace }
    if ($available -lt $required) {
        throw "Insufficient free space for Reign client staging on $($drive.Name): need at least $required bytes ($needed copy + $reserve reserve); available $available bytes. No installed module was changed."
    }
    return $required
}

function Write-ReignStorageMarker([string]$Root, [string]$Kind, [string]$DeploymentId) {
    if ($Kind -notin @('before','stage')) { throw 'Invalid deployment marker kind.' }
    $name = "$Kind-$DeploymentId"
    $marker = Join-Path $Root "$name.reign-owned.json"
    if (Test-Path -LiteralPath $marker) { throw "Deployment marker already exists: $marker" }
    $data = [ordered]@{ schema='reign-client-storage-v1'; kind=$Kind; name=$name; createdUtc=[DateTime]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText($marker, ($data | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}

function Get-ReignMarkedDirectory([string]$Root, [string]$Kind) {
    foreach ($marker in Get-ChildItem -LiteralPath $Root -File -Filter "$Kind-*.reign-owned.json") {
        if ($marker.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($marker.Name -notmatch "^$Kind-[A-Za-z0-9-]+-[a-f0-9]{8}\.reign-owned\.json$") { continue }
        try { $data = Get-Content -LiteralPath $marker.FullName -Raw | ConvertFrom-Json -DateKind String } catch { continue }
        if ($data.schema -ne 'reign-client-storage-v1' -or $data.kind -ne $Kind -or $data.name -ne $marker.Name.Replace('.reign-owned.json','') -or -not $data.createdUtc) { continue }
        $created = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($data.createdUtc, [ref]$created)) { continue }
        $path = Join-Path $Root $data.name
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        $item = Get-Item -LiteralPath $path
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $redirected = @(Get-ChildItem -LiteralPath $path -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($redirected.Count) { continue }
        [pscustomobject]@{ Path=$path; Marker=$marker.FullName; CreatedUtc=$created }
    }
}

function Invoke-ReignStorageRetention([string]$Root, [int]$KeepBackups = 2, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow) {
    $backups = @(Get-ReignMarkedDirectory $Root 'before' | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.Path 'SubModule.xml') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.Path 'bin\Win64_Shipping_Client\ReignBeta.dll') -PathType Leaf) -and
        ((Get-Content -LiteralPath (Join-Path $_.Path 'SubModule.xml') -Raw) -match '<Id\s+value="ReignBeta"')
    } | Sort-Object CreatedUtc -Descending)
    foreach ($entry in @($backups | Select-Object -Skip $KeepBackups)) {
        Remove-Item -LiteralPath $entry.Path -Recurse -Force
        Remove-Item -LiteralPath $entry.Marker -Force
    }
    foreach ($entry in @(Get-ReignMarkedDirectory $Root 'stage')) {
        if ($entry.CreatedUtc -gt $Now.AddDays(-1)) { continue }
        Remove-Item -LiteralPath $entry.Path -Recurse -Force
        Remove-Item -LiteralPath $entry.Marker -Force
    }
}

function Invoke-ReignClientActivation(
    [string]$Module, [string]$Stage, [string]$Backup,
    [string]$LegacyArena, [string]$LegacyArenaBackup,
    [string]$DeploymentRoot, [string]$DeploymentId,
    [scriptblock]$Verify
) {
    $moduleMoved = $false
    $arenaMoved = $false
    $stageActivated = $false
    try {
        if (Test-Path -LiteralPath $Module) { [IO.Directory]::Move($Module, $Backup); $moduleMoved = $true }
        if (Test-Path -LiteralPath $LegacyArena) { [IO.Directory]::Move($LegacyArena, $LegacyArenaBackup); $arenaMoved = $true }
        [IO.Directory]::Move($Stage, $Module)
        $stageActivated = $true
        & $Verify $Module | Out-Null
    } catch {
        $activationFailure = $_
        try {
            if ($stageActivated) { [IO.Directory]::Move($Module, (Join-Path $DeploymentRoot "failed-$DeploymentId")) }
            if ($moduleMoved) { [IO.Directory]::Move($Backup, $Module) }
            if ($arenaMoved) { [IO.Directory]::Move($LegacyArenaBackup, $LegacyArena) }
        } catch {
            throw "Client activation failed: $($activationFailure.Exception.Message). Rollback also failed: $($_.Exception.Message). Inspect the retained module and backup folders."
        }
        throw $activationFailure
    }
    return [pscustomobject]@{ ModuleMoved=$moduleMoved; ArenaMoved=$arenaMoved }
}
