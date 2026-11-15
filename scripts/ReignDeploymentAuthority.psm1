#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Get-ReignAuthorityPath { Join-Path $env:USERPROFILE '.reign/deployment-source.json' }
function Get-ReignFileSha([string]$Path) {
    $s=[IO.File]::OpenRead($Path)
    try {[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($s)).ToLowerInvariant()} finally {$s.Dispose()}
}
function Get-ReignJsonSha($Value) {
    $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 32 -Compress))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
function Test-ReignSamePath([string]$Left,[string]$Right) {
    [IO.Path]::GetFullPath($Left).TrimEnd('\','/').Equals([IO.Path]::GetFullPath($Right).TrimEnd('\','/'),[StringComparison]::OrdinalIgnoreCase)
}
function Get-ReignPhysicalDirectory([string]$Path) {
    $directory=[IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path))
    $target=$directory.ResolveLinkTarget($true)
    if($target){$target.FullName}else{$directory.FullName}
}
function Invoke-ReignInventoryGit([string]$Root) {
    $git=Join-Path $env:ProgramFiles 'Git/cmd/git.exe'
    if(-not (Test-Path -LiteralPath $git)){$git=(Get-Command git -ErrorAction Stop).Source}
    $info=[Diagnostics.ProcessStartInfo]::new($git)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.StandardOutputEncoding=[Text.Encoding]::UTF8
    foreach($arg in @('-C',$Root,'ls-files','-co','--exclude-standard','-z')){[void]$info.ArgumentList.Add($arg)}
    foreach($key in @($info.Environment.Keys | Where-Object {$_ -like 'GIT_*'})){$info.Environment.Remove($key)|Out-Null}
    $info.Environment['GIT_TERMINAL_PROMPT']='0'
    $p=[Diagnostics.Process]::Start($info); $p.StandardInput.Close()
    $stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
    if(-not $p.WaitForExit(60000)){$p.Kill($true);throw 'Source inventory Git timed out.'}
    $text=$stdout.GetAwaiter().GetResult(); $errorText=$stderr.GetAwaiter().GetResult()
    if($p.ExitCode -ne 0){throw ('Source inventory Git failed: '+$errorText)}
    $text.Split([char]0,[StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -CaseSensitive -Unique
}
function Get-ReignSourceInventory([string]$Workspace,[string]$ServerRoot) {
    $rows=[Collections.Generic.List[object]]::new()
    foreach($repo in @(@{component='client';root=$Workspace},@{component='server';root=$ServerRoot})){
        foreach($relative in @(Invoke-ReignInventoryGit $repo.root)){
            $parts=$relative.Replace('\','/').Split('/')
            if($parts[0] -in @('.git','data','runtime') -or @($parts | Where-Object {$_ -in @('obj','bin','node_modules','.codex-build','.venv')}).Count){continue}
            if([IO.Path]::IsPathRooted($relative) -or $parts -contains '..'){throw 'Source inventory escaped its root.'}
            $file=Join-Path $repo.root $relative
            # A tracked working-tree deletion must appear as a reviewed removal,
            # rather than making checkpoint preparation impossible.
            if(-not (Test-Path -LiteralPath $file)){continue}
            if(-not (Test-Path -LiteralPath $file -PathType Leaf)){throw "Source inventory requires a regular file: $relative"}
            $parent=[IO.FileInfo]::new($file)
            while($parent -and -not (Test-ReignSamePath $parent.FullName $repo.root)){
                if($parent.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Redirected source path: $relative"}
                $parent=if($parent -is [IO.FileInfo]){$parent.Directory}else{$parent.Parent}
            }
            $rows.Add([ordered]@{key=$repo.component+'/'+$relative.Replace('\','/');sha256=Get-ReignFileSha $file})
        }
    }
    @($rows | Sort-Object -Property {$_.key} -CaseSensitive)
}
function Get-ReignInventoryDiff($Before,$After) {
    $old=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($row in @($Before|Where-Object {$null -ne $_})){$old[$row.key]=$row.sha256}
    $new=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($row in @($After|Where-Object {$null -ne $_})){$new[$row.key]=$row.sha256}
    foreach($key in @(@($old.Keys)+@($new.Keys) | Sort-Object -CaseSensitive -Unique)){
        $left=$null;$right=$null
        [void]$old.TryGetValue($key,[ref]$left);[void]$new.TryGetValue($key,[ref]$right)
        if($left -ne $right){[ordered]@{path=$key;before=$left;after=$right}}
    }
}
function Assert-ReignInventoryEqual($Before,$After) {
    $diff=@(Get-ReignInventoryDiff $Before $After)
    if($diff.Count){throw ('Source changed since the reviewed checkpoint: '+(($diff | Select-Object -First 8 -ExpandProperty path) -join ', ')+'. Prepare and review a new checkpoint; do not select an older checkout.')}
}
function Test-ReignStringSet($Expected,$Actual) {
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($value in @($Expected)){if($null -ne $value){[void]$set.Add([string]$value)}}
    $set.SetEquals([string[]]@($Actual|Where-Object {$null -ne $_}))
}
function Get-ReignOriginalReport([string]$Path,[string]$Workspace,[string]$Kind) {
    $report=Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if(-not $report.Ok -or $report.RunId -notmatch '^\d{8}-\d{6}-[0-9a-f]{8}$'){throw 'An original successful canonical report is required.'}
    $name=if($Kind -eq 'validation'){'validation-report.json'}else{'linux-build-report.json'}
    $root=Join-Path $Workspace ('.codex-build/reign-mcp/'+$Kind+'/'+$report.RunId)
    $artifact=if($Kind -eq 'validation'){$root}else{Join-Path $root 'publish'}
    if(-not (Test-ReignSamePath $Path (Join-Path $root $name)) -or -not (Test-ReignSamePath $report.ReportPath $Path) -or -not (Test-ReignSamePath $report.ArtifactRoot $artifact)){throw 'Foreign/copied report or artifact root is not a deployment source.'}
    $report
}
function Invoke-ReignSourceVerifier($Verifier,[string]$Workspace,[string]$ValidationReport='', [string]$LinuxBuildReport='') {
    foreach($row in @($Verifier.files)){
        if((Get-ReignFileSha (Join-Path $Verifier.root $row.path)) -ne $row.sha256){throw 'Qualified source verifier changed.'}
    }
    $info=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -ErrorAction Stop).Source)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['REIGN_WORKSPACE_ROOT']=$Workspace
    $info.Environment.Remove('REIGN_MCP_BUILD_ROOT')|Out-Null
    foreach($arg in @((Join-Path $Verifier.root 'Reign.Mcp.Server.dll'),'--deployment-source-check-cli','--validation-report',$ValidationReport,'--linux-build-report',$LinuxBuildReport)){[void]$info.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($info);$p.StandardInput.Close()
    $output=$p.StandardOutput.ReadToEndAsync();$errorOutput=$p.StandardError.ReadToEndAsync()
    if(-not $p.WaitForExit(180000)){$p.Kill($true);throw 'Read-only deployment source check timed out.'}
    $text=$output.GetAwaiter().GetResult();[void]$errorOutput.GetAwaiter().GetResult()
    if($p.ExitCode -ne 0){throw ('Deployment source check rejected the reports: '+$text)}
    $check=$text|ConvertFrom-Json
    if($check.Schema -ne 'reign-deployment-source-check-v1' -or -not $check.Ok){throw 'No successful canonical deployment source check.'}
    $check
}
function Assert-ReignAuthorityRoots($Authority,[string]$Workspace,[string]$ServerRoot) {
    if(-not (Test-ReignSamePath (Get-ReignPhysicalDirectory $Workspace) $Authority.WorkspaceRoot) -or
        -not (Test-ReignSamePath (Get-ReignPhysicalDirectory $ServerRoot) $Authority.ServerRoot) -or
        -not (Test-ReignSamePath (Get-ReignPhysicalDirectory (Join-Path $Workspace 'ReignServer')) $Authority.ServerRoot)){
        throw 'Deployment is restricted to the reviewed physical client/server checkouts. Retained worktrees are recovery-only.'
    }
}
function Get-ReignClientSourceHashes($Authority) {
    $hashes=@{};foreach($row in $Authority.SourceFiles){if($row.key.StartsWith('client/')){$hashes[$row.key.Substring(7)]=$row.sha256}}
    $hashes
}
function Read-ReignDeploymentAuthority([string]$Path=(Get-ReignAuthorityPath)) {
    $pointer=Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json
    if($pointer.Schema -ne 'reign-deployment-authority-v1' -or
        (Get-ReignFileSha $pointer.CandidatePath) -ne $pointer.CandidateSha256 -or
        (Get-ReignFileSha $pointer.ReviewPath) -ne $pointer.ReviewSha256){throw 'Deployment authority or its durable review evidence changed.'}
    $content=Get-Content -LiteralPath $pointer.CandidatePath -Raw|ConvertFrom-Json
    $review=Get-Content -LiteralPath $pointer.ReviewPath -Raw|ConvertFrom-Json
    if($content.Schema -ne 'reign-deployment-authority-content-v1' -or $review.CandidateSha256 -ne $pointer.CandidateSha256 -or
        $review.DiffSha256 -ne $content.DiffSha256 -or (Get-ReignJsonSha $content.SourceFiles) -ne $content.SourceInventorySha256){throw 'Invalid deployment checkpoint/review binding.'}
    Assert-ReignCheckpointReview $content $review $pointer.CandidatePath
    $content
}
function Assert-ReignCheckpointReview($Content,$Review,[string]$CandidatePath) {
    if($Content.Schema -ne 'reign-deployment-authority-content-v1' -or $Review.CandidateSha256 -ne (Get-ReignFileSha $CandidatePath) -or
        $Review.PriorAuthoritySha256 -ne $Content.PriorAuthoritySha256 -or $Review.DiffSha256 -ne $Content.DiffSha256 -or
        (Get-ReignJsonSha $Content.Diff) -ne $Content.DiffSha256 -or [string]::IsNullOrWhiteSpace($Review.Reason) -or $Review.Reason.Length -lt 15){throw 'Review receipt is stale, incomplete, or does not bind this exact candidate/diff.'}
    $expected=@($Content.Diff|ForEach-Object path);$reviewed=@($Review.ReviewedPaths)
    if(-not (Test-ReignStringSet $expected $reviewed)){throw 'Every added, modified and removed path needs explicit source review.'}
}
function Assert-ReignCheckpointDelta($Prior,$Content) {
    $before=if($Prior){$Prior.SourceFiles}else{@()}
    $derived=@(Get-ReignInventoryDiff $before $Content.SourceFiles)
    if((Get-ReignJsonSha $Content.SourceFiles) -ne $Content.SourceInventorySha256 -or
        (Get-ReignJsonSha $derived) -ne $Content.DiffSha256){throw 'Candidate diff does not match the independently derived baseline-to-source delta.'}
}
function Assert-ReignSelectedReportHashes($Content) {
    foreach($pair in @(@{path=$Content.ValidationReportSelection;record=$Content.ClientReport},@{path=$Content.LinuxBuildReportSelection;record=$Content.LinuxReport})){
        if($pair.path -and (-not $pair.record -or -not (Test-ReignSamePath $pair.path $pair.record.path) -or
            (Get-ReignFileSha $pair.path) -ne $pair.record.sha256)){throw 'Selected qualification report changed after checkpoint preparation.'}
    }
}
function Assert-ReignDeploymentAuthority([string]$Workspace,[string]$ServerRoot,[string]$ValidationReport='', [string]$LinuxBuildReport='', [string]$AuthorityPath=(Get-ReignAuthorityPath)) {
    if(-not (Test-Path -LiteralPath $AuthorityPath -PathType Leaf)){throw 'Deployment source authority is missing. Initialize a reviewed checkpoint with Prepare-DeploymentCheckpoint.ps1 and Promote-DeploymentCheckpoint.ps1; do not remove old sources.'}
    $authority=Read-ReignDeploymentAuthority $AuthorityPath
    Assert-ReignAuthorityRoots $authority $Workspace $ServerRoot
    foreach($pair in @(@{path=$ValidationReport;record=$authority.ClientReport},@{path=$LinuxBuildReport;record=$authority.LinuxReport})){
        if($pair.path -and (-not $pair.record -or -not (Test-ReignSamePath $pair.path $pair.record.path) -or (Get-ReignFileSha $pair.path) -ne $pair.record.sha256)){throw 'Report is not the approved deployment checkpoint. Review/promote the intended source first.'}
    }
    Assert-ReignInventoryEqual $authority.SourceFiles (Get-ReignSourceInventory $Workspace $ServerRoot)
    $check=Invoke-ReignSourceVerifier $authority.Verifier $Workspace $ValidationReport $LinuxBuildReport
    Assert-ReignAuthorityRoots $authority $check.WorkspaceRoot $check.ServerRoot
    $authority
}
function Assert-ReignStagedClient($Authority,[string]$Stage,[string]$Helper) {
    $artifacts=@($Authority.ClientArtifacts|ForEach-Object {[pscustomobject]@{path=$_.path.Replace('\','/');sha256=$_.sha256}})
    $dlls=@($artifacts|Where-Object {$_.path -notlike '*/*' -and $_.path.EndsWith('.dll') -and -not $_.path.StartsWith('TaleWorlds.')}|ForEach-Object path|Sort-Object)
    $actualDlls=@(Get-ChildItem -LiteralPath (Join-Path $Stage 'bin/Win64_Shipping_Client') -File|ForEach-Object Name|Sort-Object)
    if(-not (Test-ReignStringSet $dlls $actualDlls)){throw 'Staged client binary inventory differs from the qualified artifact.'}
    $expectedHelper=@($artifacts|Where-Object {$_.path.StartsWith('native-portrait-generator/')}|ForEach-Object {$_.path.Substring(26)})
    $actualHelper=@(Get-ChildItem -LiteralPath $Helper -File -Recurse|ForEach-Object {[IO.Path]::GetRelativePath($Helper,$_.FullName).Replace('\','/')})
    if(-not (Test-ReignStringSet $expectedHelper $actualHelper)){throw 'Staged portrait-helper inventory differs from the qualified artifact.'}
    foreach($row in $artifacts){
        $relative=$row.path.Replace('\','/')
        if($relative.StartsWith('native-portrait-generator/')){$target=Join-Path $Helper $relative.Substring(26)}
        elseif($relative -in @('.version.txt','.version_number.txt')){$target=Join-Path $Stage $relative}
        elseif($relative -notlike '*/*' -and $relative.EndsWith('.dll') -and -not $relative.StartsWith('TaleWorlds.')){$target=Join-Path $Stage ('bin/Win64_Shipping_Client/'+$relative)}
        else{continue}
        if((Get-ReignFileSha $target) -ne $row.sha256){throw "Staged qualified client artifact changed: $relative"}
    }
}
Export-ModuleMember -Function *-Reign*
