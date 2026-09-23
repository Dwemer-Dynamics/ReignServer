[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [string]$ValidationReport,
    [string]$LinuxBuildReport,
    [ValidateSet('ReignServer')][string]$Distro = 'ReignServer',
    [string]$BannerlordPath = $env:REIGN_BANNERLORD_PATH,
    [switch]$SkipClient,
    [switch]$SkipServer
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ClientDeploymentStorage.ps1')
if ($SkipClient -and $SkipServer) { throw 'Select at least one component.' }
if ($Distro -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') { throw 'Invalid WSL distribution name.' }
$workspaceRoot = (Resolve-Path -LiteralPath $Workspace).Path
$release = Get-Content (Join-Path $PSScriptRoot '../release/release.json') -Raw | ConvertFrom-Json
$recordPath = Join-Path $env:USERPROFILE '.reign\installation.json'
$previousRecord = if (Test-Path -LiteralPath $recordPath) { Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json } else { $null }
if (-not $SkipClient -and $previousRecord -and $previousRecord.serverMode -ne 'dwemerdistro-wsl') {
    throw 'A standalone installation exists. Preserve and migrate its data before replacing the client installation record.'
}
if (-not $SkipClient -and $previousRecord -and $previousRecord.wslDistro -ne $Distro) { throw 'The existing installation belongs to another distro.' }
if (-not $SkipServer) {
    $linux = Get-Content -LiteralPath $LinuxBuildReport -Raw | ConvertFrom-Json
    if (-not $linux.Ok -or $linux.Schema -ne 'reign-linux-build-v1') { throw 'Server deployment requires a successful canonical Linux component report.' }
    & (Join-Path $PSScriptRoot 'Test-ReignServerContent.ps1') -SourceRoot (Resolve-Path (Join-Path $PSScriptRoot '..')).Path -ArtifactRoot $linux.ArtifactRoot
}

# Keep argument boundaries intact when mapping the selected validated artifacts into WSL.
function ConvertTo-WslPath([string]$Path) {
    $mapped = & wsl.exe -d $Distro -- wslpath -u ([IO.Path]::GetFullPath($Path).Replace('\','/'))
    if ($LASTEXITCODE -ne 0 -or -not $mapped) { throw 'Could not map deployment path into WSL.' }
    return ($mapped -join '').Trim()
}

function Assert-BannerlordClosed([string]$GameRoot) {
    $gamePrefix = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\') + '\'
    $knownNames = @('Bannerlord','Bannerlord.Native','Bannerlord.BLSE.Standalone','Bannerlord.BLSE.Launcher','TaleWorlds.MountAndBlade.Launcher')
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in $knownNames -or
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase))
    })
    if ($running.Count -gt 0) { throw 'Close Bannerlord and its launcher before deploying its module.' }
}

if (-not $SkipClient) {
    $report = Get-Content -LiteralPath $ValidationReport -Raw | ConvertFrom-Json
    if (-not $report.Ok -or $report.Plan.Profile -notin @('all','product')) { throw 'Client deployment requires a successful canonical product or all validation report.' }
    $game = (Resolve-Path -LiteralPath $BannerlordPath).Path.TrimEnd('\')
    Assert-BannerlordClosed $game
    if (-not (Test-Path -LiteralPath (Join-Path $game 'bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll'))) { throw 'Bannerlord installation is incomplete.' }
    $module = Join-Path $game 'Modules\ReignBeta'
    $legacyArena = Join-Path $game 'Modules\ArenaOverhaul'
    if ((Test-Path -LiteralPath $module) -and (-not $previousRecord -or $previousRecord.moduleRoot -ne $module)) { throw 'Existing ReignBeta is not owned by this deployment; it was preserved.' }
    $runId = $report.RunId
    if ($runId -notmatch '^[A-Za-z0-9-]+$') { throw 'Invalid validation run ID.' }
    $client = Join-Path $report.ArtifactRoot 'client\out'
    $native = Join-Path $report.ArtifactRoot 'client\out\native-portrait-generator'
    $dll = Join-Path $client 'ReignBeta.dll'
    $arenaDll = Join-Path $client 'ArenaOverhaul.1.4.6.dll'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne "$($release.version).0") { throw 'Client version does not match the release manifest.' }
    if (-not (Test-Path -LiteralPath $arenaDll -PathType Leaf)) { throw 'Validated client is missing the bundled Arena Overhaul assembly.' }
    if (-not (Test-Path -LiteralPath (Join-Path $native 'Bannerlord.NativeCharacterImageGenerator.App.exe'))) { throw 'Validated Windows portrait helper is missing.' }
    # Refuse missing or unhydrated shipped assets before touching the installed module.
    $portraitRoot = Join-Path $workspaceRoot 'ReignBeta\PortraitCache\_shared'
    $inventory = Get-Content (Join-Path $workspaceRoot 'ReignBeta\PortraitCache\shared-portrait-inventory.json') -Raw | ConvertFrom-Json
    foreach ($entry in $inventory.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $portraitRoot $entry.path))
        if (-not $path.StartsWith($portraitRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw 'Shipped portrait inventory is incomplete. Hydrate the tracked Git LFS assets before deployment.'
        }
    }
    & (Join-Path $PSScriptRoot 'Test-ReignClientContent.ps1') -Workspace $workspaceRoot
    $deploymentId = "$runId-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    # Bannerlord discovers SubModule.xml even in hidden folders under Modules.
    # Keep staging and recoverable backups outside that search root, on the same volume.
    $deploymentRoot = Join-Path $game '.reign-deployment'
    if ((Test-Path -LiteralPath $deploymentRoot) -and ((Get-Item -LiteralPath $deploymentRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected deployment storage is not supported.' }
    $stageSources = @('GUI','ModuleData','EventArt','TavernArt','Videos','PortraitCache','TavernHousePortraits') | ForEach-Object { Join-Path $workspaceRoot "ReignBeta\$_" }
    $stageSources += @((Join-Path $workspaceRoot 'ReignBeta\SubModule.xml'), $client)
    Assert-ReignDeploymentHeadroom -GameRoot $game -StageSources $stageSources | Out-Null
    $helper = Join-Path $env:USERPROFILE "Reign\Tools\$deploymentId\native-portrait-generator"
    Assert-ReignDeploymentHeadroom -GameRoot $helper -StageSources @($native) | Out-Null
    New-Item -ItemType Directory -Path $deploymentRoot -Force | Out-Null
    $stage = Join-Path $deploymentRoot "stage-$deploymentId"
    if (Test-Path -LiteralPath $stage) { throw 'The deployment staging path already exists.' }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Write-ReignStorageMarker -Root $deploymentRoot -Kind stage -DeploymentId $deploymentId
    foreach ($directory in @('GUI','ModuleData','EventArt','TavernArt','Videos','PortraitCache','TavernHousePortraits')) {
        Copy-Item -LiteralPath (Join-Path $workspaceRoot "ReignBeta\$directory") -Destination (Join-Path $stage $directory) -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'ReignBeta\SubModule.xml') -Destination $stage
    foreach ($versionFile in @('.version.txt', '.version_number.txt')) {
        Copy-Item -LiteralPath (Join-Path $client $versionFile) -Destination $stage
    }
    $bin = Join-Path $stage 'bin\Win64_Shipping_Client'
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $client -File -Filter '*.dll') {
        if ($file.Name.StartsWith('TaleWorlds.')) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination $bin
        if ((Get-FileHash -LiteralPath (Join-Path $bin $file.Name)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Client copy checksum failed.' }
    }
    & (Join-Path $PSScriptRoot 'Test-ReignClientContent.ps1') -Workspace $workspaceRoot -StagedModule $stage
    if (Test-Path -LiteralPath $helper) { throw 'The helper deployment path already exists.' }
    New-Item -ItemType Directory -Path (Split-Path $helper) -Force | Out-Null
    Copy-Item -LiteralPath $native -Destination $helper -Recurse
    $backup = Join-Path $deploymentRoot "before-$deploymentId"
    $legacyArenaBackup = Join-Path $deploymentRoot "arena-overhaul-before-$deploymentId"
    # Fixed children of the verified game directory; backups remain outside Modules.
    $gameRoot = [IO.Path]::GetFullPath($game) + '\'
    foreach ($path in @($module,$stage,$backup,$legacyArena,$legacyArenaBackup)) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($gameRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Deployment path escaped the game directory.' }
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected module paths are not supported.' }
    }
    if (Test-Path -LiteralPath $legacyArena) {
        $legacyManifest = Join-Path $legacyArena 'SubModule.xml'
        if (-not (Test-Path -LiteralPath $legacyManifest) -or
            -not ((Get-Content -LiteralPath $legacyManifest -Raw) -match '<Id\s+value="ArenaOverhaul"')) {
            throw 'The existing ArenaOverhaul folder has an unexpected module identity; it was preserved.'
        }
    }
    # Directory.Move is atomic on this volume. PowerShell Move-Item can move
    # children one by one when a live game has a file open, leaving portraits
    # absent even though the module folder still exists.
    Assert-BannerlordClosed $game
    if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $legacyArenaBackup)) { throw 'Deployment backup path already exists.' }
    $activation = Invoke-ReignClientActivation -Module $module -Stage $stage -Backup $backup -LegacyArena $legacyArena -LegacyArenaBackup $legacyArenaBackup -DeploymentRoot $deploymentRoot -DeploymentId $deploymentId -Verify {
        param($activatedModule)
        & (Join-Path $PSScriptRoot 'Test-ReignClientContent.ps1') -Workspace $workspaceRoot -StagedModule $activatedModule
    }
    New-Item -ItemType Directory -Path (Split-Path $recordPath) -Force | Out-Null
    if (Test-Path -LiteralPath $recordPath) { Copy-Item -LiteralPath $recordPath -Destination "$recordPath.before-$runId" }
    $prefix = "\\wsl.localhost\$Distro"
    $record = [ordered]@{
        schema='reign-installation-v1';version=$release.version;protocolVersion=$release.protocolVersion;contentVersion=$release.contentVersion
        serverMode='dwemerdistro-wsl';wslDistro=$Distro;serverRoot="$prefix\var\www\html\ReignServer\runtime\current"
        contentRoot=$module;dataRoot="$prefix\var\www\html\ReignServer\data"
        bannerlordRoot=$game;moduleRoot=$module;postgresBin='';postgresPort=5432;nativeGeneratorRoot=$helper
    }
    [IO.File]::WriteAllText("$recordPath.next",($record | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath "$recordPath.next" -Destination $recordPath -Force
    $generator = ConvertTo-WslPath (Join-Path $helper 'Bannerlord.NativeCharacterImageGenerator.App.exe')
    $nativeSaveRoot = ConvertTo-WslPath (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Game Saves')
    $writeBridge = 'import json,pathlib,sys; p=pathlib.Path("/var/www/html/ReignServer/data/windows-bridge.json"); d=json.loads(p.read_text()) if p.is_file() else {"schema":"reign-windows-bridge-v1"}; d["nativeGenerator"]=sys.argv[1]; d["nativeSaveRoot"]=sys.argv[2]; t=p.with_suffix(".next"); t.write_text(json.dumps(d)); t.chmod(0o600); t.replace(p)'
    & wsl.exe -d $Distro -u reign -- python3 -c $writeBridge $generator $nativeSaveRoot
    if ($LASTEXITCODE -ne 0) { throw 'The client deployed, but its WSL portrait bridge record could not be written.' }
    # Retention starts only after the installed client and bridge are verified.
    # Any failure here leaves the current rollback untouched and is reported for manual review.
    try {
        if ($activation.ModuleMoved) { Write-ReignStorageMarker -Root $deploymentRoot -Kind before -DeploymentId $deploymentId }
        Remove-Item -LiteralPath (Join-Path $deploymentRoot "stage-$deploymentId.reign-owned.json") -Force
        Invoke-ReignStorageRetention -Root $deploymentRoot -KeepBackups 2
    } catch {
        Write-Warning "Client deployed, but deployment storage retention did not finish: $($_.Exception.Message)"
    }
    Write-Output "Reign $($release.version) client deployed to $module."
}

if (-not $SkipServer) {
    $linux = Get-Content -LiteralPath $LinuxBuildReport -Raw | ConvertFrom-Json
    if (-not $linux.Ok -or $linux.Schema -ne 'reign-linux-build-v1') { throw 'Server deployment requires a successful canonical Linux component report.' }
    $control = ConvertTo-WslPath (Join-Path $PSScriptRoot 'reignctl')
    $versions = ConvertTo-WslPath (Join-Path $PSScriptRoot 'reign_versions.py')
    $service = ConvertTo-WslPath (Join-Path $PSScriptRoot 'reignserver.service')
    $installer = ConvertTo-WslPath (Join-Path $PSScriptRoot 'install-reign-control.sh')
    & wsl.exe -d $Distro -u root -- /bin/sh $installer $control $versions $service
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the dedicated ReignServer service controls.' }
    $artifact = ConvertTo-WslPath $linux.ArtifactRoot
    & wsl.exe -d $Distro -u root -- systemctl start postgresql apache2
    if ($LASTEXITCODE -ne 0) { throw 'ReignServer PostgreSQL or Apache could not start.' }
    $gameModules = Join-Path (Resolve-Path -LiteralPath $BannerlordPath).Path 'Modules'
    if (-not (Test-Path -LiteralPath (Join-Path $gameModules 'SandBox\ModuleData') -PathType Container)) {
        throw 'The selected Bannerlord modules directory is incomplete.'
    }
    $modulesWsl = ConvertTo-WslPath $gameModules
    $writeModulesBridge = 'import json,pathlib,sys; p=pathlib.Path("/var/www/html/ReignServer/data/windows-bridge.json"); d=json.loads(p.read_text()) if p.is_file() else {"schema":"reign-windows-bridge-v1"}; d["bannerlordModules"]=sys.argv[1]; t=p.with_suffix(".next"); t.write_text(json.dumps(d)); t.chmod(0o600); t.replace(p)'
    & wsl.exe -d $Distro -u reign -- python3 -c $writeModulesBridge $modulesWsl
    if ($LASTEXITCODE -ne 0) { throw 'Could not record the installed Bannerlord modules path in ReignServer.' }
    & wsl.exe -d $Distro -u root -- systemctl is-active --quiet reignserver
    $wasManagedRunning = $LASTEXITCODE -eq 0
    $previousVersion = (& wsl.exe -d $Distro -u root -- cat /var/www/html/ReignServer/runtime/current-version 2>$null | Out-String).Trim()
    & wsl.exe -d $Distro -u root -- systemctl stop reignserver
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the managed ReignServer service before activation.' }
    & wsl.exe -d $Distro -u root -- /usr/local/bin/reignctl activate $artifact
    if ($LASTEXITCODE -ne 0) {
        if ($wasManagedRunning) { & wsl.exe -d $Distro -u root -- systemctl start reignserver }
        throw 'Linux activation failed; inspect the retained runtime and logs.'
    }
    $verifyRuntime = 'import hashlib,json,pathlib,sys; source=pathlib.Path(sys.argv[1]); current=pathlib.Path("/var/www/html/ReignServer/runtime/current"); manifest=json.loads((source/"reign-linux-artifact.json").read_text()); expected=manifest["files"]; actual={p.relative_to(current).as_posix() for p in current.rglob("*") if p.is_file() and p.name!="reign-linux-artifact.json"}; assert set(expected)==actual, "runtime inventory mismatch"; assert not any(p.is_symlink() for p in current.rglob("*")), "runtime symlink"; assert all(hashlib.sha256((current/name).read_bytes()).hexdigest()==digest for name,digest in expected.items()), "runtime checksum mismatch"'
    & wsl.exe -d $Distro -u root -- python3 -c $verifyRuntime $artifact
    if ($LASTEXITCODE -ne 0) {
        if ($previousVersion -match '^[0-9]{14}-[0-9]+$') {
            & wsl.exe -d $Distro -u root -- /usr/local/bin/reignctl rollback $previousVersion
            if ($wasManagedRunning) { & wsl.exe -d $Distro -u root -- systemctl start reignserver }
        }
        throw 'Installed ReignServer files differ from the validated Linux artifact; rollback was attempted when available.'
    }
    & wsl.exe -d $Distro -u root -- systemctl start reignserver
    if ($LASTEXITCODE -ne 0) { throw 'The managed ReignServer service did not start.' }
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8089/health' -TimeoutSec 10
    if (-not $health.ok -or $health.serverVersion -ne $release.version) { throw 'Deployed server version check failed.' }
    Write-Output "ReignServer $($health.serverVersion) is healthy inside $Distro through port 8089."
}
Write-Output 'No game was started. In-game validation remains manual.'
