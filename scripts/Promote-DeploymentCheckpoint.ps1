#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Candidate,[Parameter(Mandatory)][string]$ReviewReceipt,[switch]$Initialize,
    [string]$AuthorityPath=(Join-Path $env:USERPROFILE '.reign/deployment-source.json'),[switch]$MigrateRoots)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ReignDeploymentAuthority.psm1') -Force
$candidateHash=Get-ReignFileSha $Candidate
$reviewHash=Get-ReignFileSha $ReviewReceipt
$content=Get-Content -LiteralPath $Candidate -Raw|ConvertFrom-Json
$review=Get-Content -LiteralPath $ReviewReceipt -Raw|ConvertFrom-Json
Assert-ReignCheckpointReview $content $review $Candidate
if($MigrateRoots.IsPresent -ne ($null -ne $content.PSObject.Properties['RootMigration']) -or ($MigrateRoots -and $Initialize)){
    throw 'Root migration requires a marked candidate and -MigrateRoots at promotion, with an existing authority.'
}
$exists=Test-Path -LiteralPath $AuthorityPath
if(($Initialize -and $exists) -or (-not $Initialize -and -not $exists)){throw 'Initialize only the first reviewed source authority; normal promotion requires its prior receipt.'}
if($exists -and (Get-ReignFileSha $AuthorityPath) -ne $content.PriorAuthoritySha256){throw 'Another checkpoint replaced the reviewed baseline; prepare a fresh diff.'}
if(-not $exists -and $content.PriorAuthoritySha256){throw 'Missing reviewed prior authority.'}
$prior=$null
if($exists){$prior=Read-ReignDeploymentAuthority $AuthorityPath}
if($MigrateRoots){
    Assert-ReignRootMigration $prior $content $Candidate
    $client=Get-ReignOriginalReport $content.ValidationReportSelection $content.WorkspaceRoot 'validation'
    $linux=Get-ReignOriginalReport $content.LinuxBuildReportSelection $content.WorkspaceRoot 'linux-server'
    $verifier=Get-ReignReportVerifier $client -MigrateRoots
    if((Get-ReignJsonSha $verifier) -ne (Get-ReignJsonSha $content.Verifier) -or
        (Get-ReignJsonSha @(Get-ReignClientArtifacts $client)) -ne (Get-ReignJsonSha @($content.ClientArtifacts)) -or
        $linux.ArtifactManifestSha256 -ne $content.LinuxManifestSha256){throw 'Root migration must bind both new reports and their qualified artifacts.'}
}elseif($prior){Assert-ReignAuthorityRoots $prior $content.WorkspaceRoot $content.ServerRoot}
Assert-ReignSelectedReportHashes $content
$check=Invoke-ReignSourceVerifier $content.Verifier $content.WorkspaceRoot $content.ValidationReportSelection $content.LinuxBuildReportSelection
Assert-ReignAuthorityRoots $content $check.WorkspaceRoot $check.ServerRoot
Assert-ReignInventoryEqual $content.SourceFiles (Get-ReignSourceInventory $content.WorkspaceRoot $content.ServerRoot)
Assert-ReignCheckpointDelta $prior $content
$parent=Split-Path -Parent ([IO.Path]::GetFullPath($AuthorityPath));[IO.Directory]::CreateDirectory($parent)|Out-Null
$lock=[IO.File]::Open(($AuthorityPath+'.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    # Recheck under the writer lock; deployment itself never writes this pointer.
    if($exists -and (Get-ReignFileSha $AuthorityPath) -ne $content.PriorAuthoritySha256){throw 'Deployment authority changed during promotion.'}
    if(-not $exists -and (Test-Path -LiteralPath $AuthorityPath)){throw 'Deployment authority was initialized concurrently.'}
    if((Get-ReignFileSha $Candidate) -ne $candidateHash -or (Get-ReignFileSha $ReviewReceipt) -ne $reviewHash){
        throw 'Deployment candidate or review changed during promotion.'
    }
    Assert-ReignSelectedReportHashes $content
    if($MigrateRoots){Assert-ReignRootMigration $prior $content $Candidate}
    $pointer=[ordered]@{Schema='reign-deployment-authority-v1';CandidatePath=[IO.Path]::GetFullPath($Candidate);CandidateSha256=$candidateHash;ReviewPath=[IO.Path]::GetFullPath($ReviewReceipt);ReviewSha256=$reviewHash}
    $temporary=$AuthorityPath+'.'+[Guid]::NewGuid().ToString('N')+'.next'
    [IO.File]::WriteAllText($temporary,($pointer|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$AuthorityPath,$true)
}finally{$lock.Dispose()}
[ordered]@{promoted=$true;authority=$AuthorityPath;authoritySha256=Get-ReignFileSha $AuthorityPath;sourceFiles=$content.SourceFiles.Count}|ConvertTo-Json
