[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][string]$BannerlordRoot,
    [Parameter(Mandatory=$true)][string]$ProgramRoot,
    [Parameter(Mandatory=$true)][string]$DataRoot,
    [string]$SourcesFile,
    [string]$InstallationFile = (Join-Path $env:LOCALAPPDATA 'Bannerlord Reign\installation.json')
)
. (Join-Path $PSScriptRoot 'Reign-Installer.ps1')
$journal = $null
$stage = $null
$receipt = $null
$committed = $false
$moves = New-Object 'System.Collections.Generic.List[object]'
try {
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 22000) { throw 'This release requires Windows 11 x64.' }
    Assert-ReignStopped
    $game = Assert-ReignLocalPath $BannerlordRoot
    $program = Assert-ReignLocalPath $ProgramRoot
    $data = Assert-ReignLocalPath $DataRoot
    $payloads = [IO.Path]::GetFullPath($PayloadDirectory)
    $recordPath = Assert-ReignLocalPath $InstallationFile
    foreach ($pair in @(@($game,$program),@($game,$data),@($program,$data))) {
        if ((Test-ReignWithin $pair[0] $pair[1]) -or (Test-ReignWithin $pair[1] $pair[0])) { throw 'Choose separate Bannerlord, ReignServer program, and Reign data folders.' }
    }
    [xml]$native = Get-Content -LiteralPath (Join-Path $game 'Modules\Native\SubModule.xml') -Raw
    if ($native.Module.Version.value -ne 'v1.4.8' -or -not [IO.File]::Exists((Join-Path $game 'bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll'))) { throw 'Select a complete Steam Bannerlord v1.4.8 installation.' }
    $edge = @((Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'), (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'))
    if (@($edge | Where-Object { [IO.File]::Exists($_) }).Count -eq 0) { throw 'Microsoft Edge is required for the dedicated Reign Control Center.' }
    $release = Read-ReignJson $Manifest
    if ($release.schema -ne 'reign-package-v1' -or $release.protocolVersion -ne 1 -or $release.payloads.Count -ne 5) { throw 'Unsupported or incomplete setup manifest.' }
    if ($release.version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$' -or $release.sourceFingerprint -notmatch '^[a-f0-9]{64}$') { throw 'Invalid release identity.' }
    $previousRecord = $null
    $previousReceipt = $null
    $recordBytes = $null
    $receiptBytes = $null
    if ([IO.File]::Exists($recordPath)) {
        $recordBytes = [IO.File]::ReadAllBytes($recordPath)
        $previousRecord = Read-ReignJson $recordPath
        $previousReceipt = Read-ReignJson (Join-Path $previousRecord.dataRoot 'installed-package.json')
        $receiptBytes = [IO.File]::ReadAllBytes((Join-Path $previousRecord.dataRoot 'installed-package.json'))
        if ($previousReceipt.schema -ne 'reign-installed-package-v1') { throw 'Previous installation ownership cannot be verified.' }
        if ([long]$previousReceipt.releaseSequence -gt [long]$release.releaseSequence) { throw 'Downgrades require an explicit database migration and are not performed by setup.' }
        if ($previousRecord.dataRoot -ne $data -or $previousRecord.bannerlordRoot -ne $game -or $previousReceipt.programRoot -ne $program) { throw 'Use the existing installation folders for repair or update. Moving an existing database requires a separate migration.' }
    }
    [long]$expanded = ($release.payloads | Measure-Object expandedBytes -Sum).Sum
    [long]$downloads = Get-ReignDownloadBytes $payloads $release.payloads
    Assert-ReignSpace @(@{path=$program;bytes=$expanded},@{path=$data;bytes=$expanded + 512MB},@{path=$game;bytes=$expanded},@{path=$payloads;bytes=$downloads})
    [IO.Directory]::CreateDirectory($program) | Out-Null
    [IO.Directory]::CreateDirectory($data) | Out-Null
    $runId = [guid]::NewGuid().ToString('N')
    $journal = Join-Path $data ('setup\' + $runId + '.json')
    $stage = Join-Path $program ('.setup-' + $runId)
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    Write-ReignJson (Join-Path $stage '.reign-staging.json') @{schema='reign-setup-staging-v1';runId=$runId}
    $indices = @{}
    foreach ($id in @('client','server','runtime','dependencies','portraits')) {
        $matches = @($release.payloads | Where-Object id -eq $id)
        if ($matches.Count -ne 1) { throw "Setup requires exactly one $id payload." }
        Write-Host "Verifying and extracting $id..."
        $archive = Get-ReignPayload $payloads $matches[0] $SourcesFile
        $indices[$id] = Expand-ReignPayload $archive (Join-Path $stage $id) $matches[0]
    }
    $serverStage = Join-Path $stage 'server'
    foreach ($directory in @('runtime','models')) {
        [IO.Directory]::Move((Join-Path $stage ('runtime\' + $directory)), (Join-Path $serverStage $directory))
    }
    $serverDestination = Join-Path $program ('versions\' + $release.version + '-' + $release.sourceFingerprint.Substring(0,12))
    Assert-ReignLocalPath $serverDestination | Out-Null
    $receipt = [ordered]@{schema='reign-installed-package-v1';version=$release.version;releaseSequence=$release.releaseSequence;programRoot=$program;sourceFingerprint=$release.sourceFingerprint;serverRoot=$serverDestination;moduleRoot=(Join-Path $game 'Modules\ReignBeta');serverFiles=@($indices.server.files)+@($indices.runtime.files);clientFiles=@($indices.client.files);dependencyFiles=@($indices.dependencies.files);contentFiles=@();preservedPortraits=@()}
    # Claim only the exact destination directories recorded by an earlier setup.
    if ([IO.Directory]::Exists($serverDestination) -and ($null -eq $previousRecord -or $previousRecord.serverRoot -ne $serverDestination)) { throw 'The target server version directory already exists and is not owned by this installation.' }
    $module = Join-Path $game 'Modules\ReignBeta'
    if ([IO.Directory]::Exists($module) -and ($null -eq $previousRecord -or $previousRecord.moduleRoot -ne $module)) { throw 'An existing ReignBeta module is not managed by this setup. Preserve or migrate it before installing.' }
    $dependencyNames = @('Bannerlord.Harmony','Bannerlord.UIExtenderEx','Bannerlord.ButterLib','Bannerlord.MBOptionScreen')
    $replaceDependencies = @()
    foreach ($name in $dependencyNames) {
        $target = Join-Path $game ('Modules\' + $name)
        $entries = @($indices.dependencies.files | Where-Object { $_.path.StartsWith('Modules/' + $name + '/') })
        if ($entries.Count -eq 0) { throw "Dependency payload is incomplete: $name" }
        if ([IO.Directory]::Exists($target)) {
            $differences = @($entries | Where-Object { -not (Test-ReignShippedFile (Get-ReignSafeTarget $game $_.path) $_) })
            if ($differences.Count -gt 0) { throw "Existing $name differs from this release. Preserve it and resolve the mod version before continuing." }
        } else { $replaceDependencies += $name }
    }
    # Only this Microsoft prerequisite may request elevation. The installer and
    # database provisioning continue as the signed-in user (DPAPI CurrentUser).
    $vc = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
    # The bundled database, Codex and vector components were verified on this
    # runtime floor. Reuse it instead of forcing an unnecessary machine update.
    if ($null -eq $vc -or $vc.Installed -ne 1 -or [version]($vc.Version.TrimStart('v')) -lt [version]'14.50.35719.0') {
        $redistributable = Join-Path $serverStage 'runtime\prerequisites\vc_redist.x64.exe'
        if ((Get-AuthenticodeSignature -LiteralPath $redistributable).Status -ne 'Valid') { throw 'Microsoft runtime signature is invalid.' }
        $process = Start-Process -FilePath $redistributable -ArgumentList '/install /passive /norestart' -Verb RunAs -PassThru -Wait
        if ($process.ExitCode -notin @(0,3010)) { throw 'Microsoft Visual C++ runtime installation did not complete.' }
        if ($process.ExitCode -eq 3010) { throw 'Windows must restart after installing the Microsoft runtime. Restart, then run Reign setup again.' }
    }
    Assert-ReignStopped
    function Install-Directory([string]$Source, [string]$Target) {
        Assert-ReignLocalPath $Source | Out-Null
        Assert-ReignLocalPath $Target | Out-Null
        $new = $Target + '.reign-new-' + $runId
        $old = $Target + '.reign-old-' + $runId
        if ([IO.Directory]::Exists($new) -or [IO.Directory]::Exists($old)) { throw 'Setup transaction directory already exists.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Target)) | Out-Null
        Copy-Item -LiteralPath $Source -Destination $new -Recurse
        $hadPrevious = [IO.Directory]::Exists($Target)
        if ($hadPrevious) { [IO.Directory]::Move($Target, $old) }
        try { [IO.Directory]::Move($new, $Target) } catch { if ($hadPrevious) { [IO.Directory]::Move($old, $Target) }; throw }
        $moves.Add([pscustomobject]@{target=$Target;old=$old;hadPrevious=$hadPrevious})
    }
    Write-ReignJson $journal @{schema='reign-setup-journal-v1';state='installing';stage=$stage;version=$release.version}
    Install-Directory $serverStage $serverDestination
    Install-Directory (Join-Path $stage 'client\Modules\ReignBeta') $module
    foreach ($name in $replaceDependencies) { Install-Directory (Join-Path $stage ('dependencies\Modules\' + $name)) (Join-Path $game ('Modules\' + $name)) }
    & (Join-Path $PSScriptRoot 'Initialize-PostgreSql.ps1') -BinDirectory (Join-Path $serverDestination 'runtime\postgresql\bin') -StateDirectory (Join-Path $data 'postgresql')
    $content = Join-Path $data 'Content'
    [IO.Directory]::CreateDirectory($content) | Out-Null
    $previousContent = @()
    if ($null -ne $previousReceipt) { $previousContent = @($previousReceipt.contentFiles) }
    $merged = Merge-ReignContent (Join-Path $stage 'portraits') $content $indices.portraits $previousContent
    $receipt.contentFiles = $merged.files
    $receipt.preservedPortraits = $merged.preservedPortraits
    $record = [ordered]@{schema='reign-installation-v1';version=$release.version;protocolVersion=$release.protocolVersion;contentVersion=$release.contentVersion;serverRoot=$serverDestination;contentRoot=$content;dataRoot=$data;bannerlordRoot=$game;moduleRoot=$module;postgresBin=(Join-Path $serverDestination 'runtime\postgresql\bin');postgresPort=55432}
    Write-ReignJson (Join-Path $data 'installed-package.json') $receipt
    Write-ReignJson $recordPath $record
    $committed = $true
    Write-ReignJson $journal @{schema='reign-setup-journal-v1';state='complete';version=$release.version;stage=$stage;preservedPortraits=$merged.preservedPortraits;recoveryDirectories=@($moves | Where-Object hadPrevious | ForEach-Object old)}
    Write-Host 'Reign installed. Start ReignServer from its shortcut, configure your own provider account, then enable the required modules in Bannerlord.'
    # Delete only this exact verified staging tree; retained previous program and
    # module directories are recorded above for recovery.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ((Test-ReignWithin $program $resolvedStage) -and (Split-Path $resolvedStage -Leaf) -eq ('.setup-' + $runId) -and (Read-ReignJson (Join-Path $resolvedStage '.reign-staging.json')).runId -eq $runId) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
    exit 0
} catch {
    $failure = $_.Exception.Message
    if (-not $committed) {
        for ($i=$moves.Count-1; $i -ge 0; $i--) {
            $move = $moves[$i]
            # Keep the failed new installation for diagnosis; restore the exact
            # previous directory without recursive deletion.
            $failed = $move.target + '.reign-failed-' + $runId
            Assert-ReignLocalPath $move.target | Out-Null
            Assert-ReignLocalPath $failed | Out-Null
            if ([IO.Directory]::Exists($move.target)) { [IO.Directory]::Move($move.target, $failed) }
            if ($move.hadPrevious -and [IO.Directory]::Exists($move.old)) { [IO.Directory]::Move($move.old, $move.target) }
        }
        if ($null -ne $receipt) {
            $receiptPath = Join-Path $data 'installed-package.json'
            if ($null -ne $receiptBytes) { [IO.File]::WriteAllBytes($receiptPath, $receiptBytes) }
            elseif ([IO.File]::Exists($receiptPath)) { [IO.File]::Delete($receiptPath) }
            if ($null -ne $recordBytes) { [IO.File]::WriteAllBytes($recordPath, $recordBytes) }
            elseif ([IO.File]::Exists($recordPath)) { [IO.File]::Delete($recordPath) }
        }
    }
    if ($null -ne $journal) { Write-ReignJson $journal @{schema='reign-setup-journal-v1';state='failed';error=$failure;stage=$stage} }
    Write-Error $failure -ErrorAction Continue
    exit 1
}
