[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [string]$ValidationReport,
    [string]$LinuxBuildReport,
    [string]$Distro = 'DwemerAI4Skyrim3',
    [string]$BannerlordPath = $env:REIGN_BANNERLORD_PATH,
    [switch]$SkipClient,
    [switch]$SkipServer
)
$ErrorActionPreference = 'Stop'
if ($SkipClient -and $SkipServer) { throw 'Select at least one component.' }
if ($Distro -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') { throw 'Invalid WSL distribution name.' }
$workspaceRoot = (Resolve-Path -LiteralPath $Workspace).Path
$release = Get-Content (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json
$recordPath = Join-Path $env:USERPROFILE '.reign\installation.json'
$previousRecord = if (Test-Path -LiteralPath $recordPath) { Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json } else { $null }
if ($previousRecord -and $previousRecord.serverMode -ne 'dwemerdistro-wsl') {
    throw 'A standalone installation exists. Preserve and migrate its data before selecting WSL.'
}
if ($previousRecord -and $previousRecord.wslDistro -ne $Distro) { throw 'The existing installation belongs to another distro.' }
if (-not $SkipServer) {
    $linux = Get-Content -LiteralPath $LinuxBuildReport -Raw | ConvertFrom-Json
    if (-not $linux.Ok -or $linux.Schema -ne 'reign-linux-build-v1') { throw 'Server deployment requires a successful canonical Linux component report.' }
}

# Keep argument boundaries intact when mapping the selected validated artifacts into WSL.
function ConvertTo-WslPath([string]$Path) {
    $mapped = & wsl.exe -d $Distro -- wslpath -u ([IO.Path]::GetFullPath($Path).Replace('\','/'))
    if ($LASTEXITCODE -ne 0 -or -not $mapped) { throw 'Could not map deployment path into WSL.' }
    return ($mapped -join '').Trim()
}

if (-not $SkipClient) {
    if (Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue) { throw 'Close Bannerlord before deploying its module.' }
    $report = Get-Content -LiteralPath $ValidationReport -Raw | ConvertFrom-Json
    if (-not $report.Ok -or $report.Plan.Profile -notin @('all','product')) { throw 'Client deployment requires a successful canonical product or all validation report.' }
    $game = (Resolve-Path -LiteralPath $BannerlordPath).Path.TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $game 'bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll'))) { throw 'Bannerlord installation is incomplete.' }
    $module = Join-Path $game 'Modules\ReignBeta'
    if ((Test-Path -LiteralPath $module) -and (-not $previousRecord -or $previousRecord.moduleRoot -ne $module)) { throw 'Existing ReignBeta is not owned by this deployment; it was preserved.' }
    $runId = $report.RunId
    if ($runId -notmatch '^[A-Za-z0-9-]+$') { throw 'Invalid validation run ID.' }
    $client = Join-Path $report.ArtifactRoot 'client\out'
    $native = Join-Path $report.ArtifactRoot 'server\out\native-portrait-generator'
    $dll = Join-Path $client 'ReignBeta.dll'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne "$($release.version).0") { throw 'Client version does not match the release manifest.' }
    if (-not (Test-Path -LiteralPath (Join-Path $native 'Bannerlord.NativeCharacterImageGenerator.App.exe'))) { throw 'Validated Windows portrait helper is missing.' }
    $deploymentId = "$runId-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    $stage = Join-Path $game "Modules\.reign-stage-$deploymentId"
    if (Test-Path -LiteralPath $stage) { throw 'The deployment staging path already exists.' }
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($directory in @('GUI','ModuleData','EventArt','TavernArt','Videos')) {
        Copy-Item -LiteralPath (Join-Path $workspaceRoot "ReignBeta\$directory") -Destination (Join-Path $stage $directory) -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'ReignBeta\SubModule.xml') -Destination $stage
    $bin = Join-Path $stage 'bin\Win64_Shipping_Client'
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $client -File -Filter '*.dll') {
        if ($file.Name.StartsWith('TaleWorlds.')) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination $bin
        if ((Get-FileHash -LiteralPath (Join-Path $bin $file.Name)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Client copy checksum failed.' }
    }
    $helper = Join-Path $env:USERPROFILE "Reign\Tools\$deploymentId\native-portrait-generator"
    if (Test-Path -LiteralPath $helper) { throw 'The helper deployment path already exists.' }
    New-Item -ItemType Directory -Path (Split-Path $helper) -Force | Out-Null
    Copy-Item -LiteralPath $native -Destination $helper -Recurse
    $backup = Join-Path $game ('Modules\.reign-before-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
    # These are fixed children of the verified game Modules directory; old files remain recoverable.
    $modulesRoot = [IO.Path]::GetFullPath((Join-Path $game 'Modules')) + '\'
    foreach ($path in @($module,$stage,$backup)) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($modulesRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Deployment path escaped the game Modules directory.' }
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected module paths are not supported.' }
    }
    if (Test-Path -LiteralPath $module) { Move-Item -LiteralPath $module -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $module }
    catch { if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $module }; throw }
    New-Item -ItemType Directory -Path (Split-Path $recordPath) -Force | Out-Null
    if (Test-Path -LiteralPath $recordPath) { Copy-Item -LiteralPath $recordPath -Destination "$recordPath.before-$runId" }
    $prefix = "\\wsl.localhost\$Distro"
    $record = [ordered]@{
        schema='reign-installation-v1';version=$release.version;protocolVersion=$release.protocolVersion;contentVersion=$release.contentVersion
        serverMode='dwemerdistro-wsl';wslDistro=$Distro;serverRoot="$prefix\opt\dwemerdistro\reign\current"
        contentRoot="$prefix\var\lib\dwemerdistro\reign";dataRoot="$prefix\var\lib\dwemerdistro\reign"
        bannerlordRoot=$game;moduleRoot=$module;postgresBin='';postgresPort=5432;nativeGeneratorRoot=$helper
    }
    [IO.File]::WriteAllText("$recordPath.next",($record | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath "$recordPath.next" -Destination $recordPath -Force
    $generator = ConvertTo-WslPath (Join-Path $helper 'Bannerlord.NativeCharacterImageGenerator.App.exe')
    $nativeSaveRoot = ConvertTo-WslPath (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Game Saves')
    $writeBridge = 'import json,pathlib,sys; p=pathlib.Path("/var/lib/dwemerdistro/reign/windows-bridge.json"); t=p.with_suffix(".next"); t.write_text(json.dumps({"schema":"reign-windows-bridge-v1","nativeGenerator":sys.argv[1],"nativeSaveRoot":sys.argv[2]})); t.replace(p)'
    & wsl.exe -d $Distro -u dwemer -- python3 -c $writeBridge $generator $nativeSaveRoot
    if ($LASTEXITCODE -ne 0) { throw 'The client deployed, but its WSL portrait bridge record could not be written.' }
    Write-Output "Reign $($release.version) client deployed to $module; previous files retained."
}

if (-not $SkipServer) {
    $linux = Get-Content -LiteralPath $LinuxBuildReport -Raw | ConvertFrom-Json
    if (-not $linux.Ok -or $linux.Schema -ne 'reign-linux-build-v1') { throw 'Server deployment requires a successful canonical Linux component report.' }
    $artifact = ConvertTo-WslPath $linux.ArtifactRoot
    & wsl.exe -d $Distro -u root -- /usr/local/bin/ddistro_reign activate $artifact
    if ($LASTEXITCODE -ne 0) { throw 'Linux activation failed; inspect the retained runtime and logs.' }
    & wsl.exe -d $Distro -u root -- /usr/local/bin/ddistro_reign start
    if ($LASTEXITCODE -ne 0) { throw 'Reign did not start.' }
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8089/health' -TimeoutSec 10
    if (-not $health.ok -or $health.serverVersion -ne $release.version) { throw 'Deployed server version check failed.' }
    Write-Output "ReignServer $($health.serverVersion) is healthy inside $Distro through port 8089."
}
Write-Output 'No game was started. In-game validation remains manual.'
