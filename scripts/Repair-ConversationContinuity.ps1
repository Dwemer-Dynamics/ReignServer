[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Manifest,
    [ValidateSet('Preview','Apply')][string]$Mode = 'Preview',
    [string]$Distro = 'ReignServer',
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExecutableSha256,
    [string]$ConfirmedManifestSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$document = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($document.schema -ne 'reign-continuity-repair-v1' -or $document.campaignId -notmatch '^[A-Za-z0-9_-]{1,128}$') {
    throw 'A reviewed continuity repair manifest with an exact campaign id is required.'
}
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null
function Invoke-ReignWsl([string[]]$Arguments) {
    $result = & wsl.exe -d $Distro -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed with exit code $LASTEXITCODE." }
    return $result
}
function Convert-ReignWslPath([string]$Path) {
    return ((Invoke-ReignWsl @('wslpath','-a','-u',$Path)) -join '').Trim()
}
if ($Executable -notmatch '^/' -or $Executable -match '[\r\n]') { throw 'Executable must be the absolute Linux path of the validated server artifact.' }
$actualExecutableHash = ((Invoke-ReignWsl @('sha256sum','--',$Executable)) -join ' ').Split(' ')[0]
if ($actualExecutableHash -ne $ExecutableSha256.ToLowerInvariant()) { throw 'Server artifact hash differs from the reviewed validation artifact.' }
$linuxManifest = Convert-ReignWslPath $manifestPath
$report = Join-Path $evidencePath ($manifestHash + '-' + $Mode.ToLowerInvariant() + '.json')
$linuxReport = Convert-ReignWslPath $report
$arguments = @($Executable,'--continuity-repair','--manifest',$linuxManifest,'--report',$linuxReport)
if ($Mode -eq 'Apply') {
    if ($ConfirmedManifestSha256 -ne $manifestHash) { throw 'Apply requires the explicitly reviewed manifest SHA256.' }
    if (Get-Process -Name 'Bannerlord','Bannerlord.Native' -ErrorAction SilentlyContinue) { throw 'Close Bannerlord before applying the reviewed repair.' }
    & wsl.exe -d $Distro -- systemctl is-active --quiet reignserver.service
    if ($LASTEXITCODE -eq 0) { throw 'Stop ReignServer through its launcher before applying the repair.' }
    if ($LASTEXITCODE -ne 3) { throw 'Could not prove that reignserver.service is inactive.' }
    $campaign = [string]$document.campaignId
    $schema = ((Invoke-ReignWsl @('sudo','-n','-u','postgres','psql','-X','-d','reign','-At','-v','ON_ERROR_STOP=1','-c',"SELECT schema_name FROM reign_meta.campaign_registry WHERE campaign_id='$campaign';")) -join '').Trim()
    if ($schema -notmatch '^reign_campaign_[a-f0-9]{32}$') { throw 'Campaign registry returned an invalid schema.' }
    $backup = Join-Path $evidencePath ($campaign + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '.dump')
    $linuxBackup = Convert-ReignWslPath $backup
    Invoke-ReignWsl @('sudo','-n','-u','postgres','pg_dump','--format=custom',"--schema=$schema",'--file',$linuxBackup,'reign') | Out-Null
    if (!(Test-Path -LiteralPath $backup) -or (Get-Item -LiteralPath $backup).Length -eq 0) { throw 'Campaign backup was not created.' }
    Invoke-ReignWsl @('pg_restore','--list',$linuxBackup) | Out-Null
    $backupHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToLowerInvariant()
    $arguments = @('env','REIGN_CONTINUITY_MAINTENANCE=1') + $arguments + @('--apply','--confirmed-manifest-sha256',$manifestHash,'--backup',$linuxBackup,'--backup-sha256',$backupHash)
    [ordered]@{ schema='reign-continuity-repair-backup-v1'; campaignId=$campaign; databaseSchema=$schema; backup=$backup; sha256=$backupHash; manifestSha256=$manifestHash; executableSha256=$actualExecutableHash } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($backup + '.receipt.json') -Encoding utf8NoBOM
}
Invoke-ReignWsl $arguments
Write-Output "Continuity repair $Mode report: $report"
