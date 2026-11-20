#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Workspace,[string]$ValidationReport='', [string]$LinuxBuildReport='',
    [Parameter(Mandatory)][string]$OutputPath,[string]$AuthorityPath=(Join-Path $env:USERPROFILE '.reign/deployment-source.json'),[switch]$MigrateRoots)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ReignDeploymentAuthority.psm1') -Force
if(Test-Path -LiteralPath $OutputPath){throw 'Keep existing checkpoint candidates; choose a new output path.'}
$priorHash=if(Test-Path -LiteralPath $AuthorityPath){Get-ReignFileSha $AuthorityPath}else{$null}
$prior=if($priorHash){Read-ReignDeploymentAuthority $AuthorityPath}else{$null}
if($MigrateRoots){
    $serverRoot=Join-Path ([IO.DirectoryInfo]::new($Workspace).Parent.FullName) 'ReignServer'
    $migration=Get-ReignRootMigration $prior $Workspace $serverRoot $ValidationReport $LinuxBuildReport
}
if(-not $ValidationReport -and -not $LinuxBuildReport){throw 'Select at least one canonical report.'}
if(-not $prior -and (-not $ValidationReport -or -not $LinuxBuildReport)){throw 'First adoption needs the reviewed paired client/Linux qualification.'}
$client=if($ValidationReport){Get-ReignOriginalReport $ValidationReport $Workspace 'validation'}else{$null}
$linux=if($LinuxBuildReport){Get-ReignOriginalReport $LinuxBuildReport $Workspace 'linux-server'}else{$null}
$verifier=if($client){Get-ReignReportVerifier $client -MigrateRoots:$MigrateRoots}else{$prior.Verifier}
$check=Invoke-ReignSourceVerifier $verifier $Workspace $ValidationReport $LinuxBuildReport
if(-not $check.ServerRoot){throw 'Paired source check is required.'}
if($MigrateRoots){
    Assert-ReignAuthorityRoots $check $migration.ToWorkspaceRoot $migration.ToServerRoot
}elseif($prior){Assert-ReignAuthorityRoots $prior $check.WorkspaceRoot $check.ServerRoot}
$inventory=@(Get-ReignSourceInventory $check.WorkspaceRoot $check.ServerRoot)
$old=if($prior){$prior.SourceFiles}else{@()}
$diff=@(Get-ReignInventoryDiff $old $inventory)
$candidate=[ordered]@{
    Schema='reign-deployment-authority-content-v1';CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    WorkspaceRoot=$check.WorkspaceRoot;ServerRoot=$check.ServerRoot;Verifier=$verifier
    PriorAuthoritySha256=$priorHash
    ValidationReportSelection=$ValidationReport;LinuxBuildReportSelection=$LinuxBuildReport
    ClientReport=if($client){[ordered]@{path=[IO.Path]::GetFullPath($ValidationReport);sha256=Get-ReignFileSha $ValidationReport}}else{$prior.ClientReport}
    LinuxReport=if($linux){[ordered]@{path=[IO.Path]::GetFullPath($LinuxBuildReport);sha256=Get-ReignFileSha $LinuxBuildReport}}else{$prior.LinuxReport}
    ClientArtifacts=if($client){@(Get-ReignClientArtifacts $client)}else{$prior.ClientArtifacts}
    LinuxManifestSha256=if($linux){$linux.ArtifactManifestSha256}else{$prior.LinuxManifestSha256}
    SourceFiles=$inventory;SourceInventorySha256=Get-ReignJsonSha $inventory
    Diff=$diff;DiffSha256=Get-ReignJsonSha $diff
}
if($MigrateRoots){$candidate.RootMigration=$migration}
$parent=Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath));[IO.Directory]::CreateDirectory($parent)|Out-Null
if($prior){
    $previous=$OutputPath+'.previous-authority.json'
    [IO.File]::Copy($AuthorityPath,$previous,$false)
    if((Get-ReignFileSha $previous) -ne $priorHash){throw 'Prior authority changed while preparing the candidate; choose a fresh output path.'}
}
[IO.File]::WriteAllText($OutputPath,($candidate|ConvertTo-Json -Depth 32),[Text.UTF8Encoding]::new($false))
$result=[ordered]@{candidate=[IO.Path]::GetFullPath($OutputPath);candidateSha256=Get-ReignFileSha $OutputPath;priorAuthoritySha256=$candidate.PriorAuthoritySha256;diffSha256=$candidate.DiffSha256;changedPaths=$diff.Count;sourceFiles=$inventory.Count;promoted=$false}
if($MigrateRoots){$result.RootMigrationSha256=Get-ReignJsonSha $migration}
$result|ConvertTo-Json
