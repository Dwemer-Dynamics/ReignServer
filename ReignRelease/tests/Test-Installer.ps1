param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'Reign-Installer.ps1')
$root = Join-Path ([IO.Path]::GetFullPath($EvidenceRoot)) ('installer-contract-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$checks = New-Object 'System.Collections.Generic.List[string]'
function Assert-Contract([bool]$Condition, [string]$Name) { if (-not $Condition) { throw "Installer contract failed: $Name" }; $checks.Add($Name) }
function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Contract $rejected $Name
}
function New-Fixture([string]$Folder, [string]$Relative, [string]$Value) {
    $path = Join-Path $Folder $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Value)
    [IO.File]::SetLastWriteTimeUtc($path, (New-Object DateTime(639249123451234567, [DateTimeKind]::Utc)))
    [pscustomobject]@{path=$Relative.Replace('\','/');bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-ReignHash $path);lastWriteUtcTicks=[IO.File]::GetLastWriteTimeUtc($path).Ticks}
}
function Write-FixtureZip([string]$Path, $Index, $Entries) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $entry = $zip.CreateEntry($name)
            $writer = New-Object IO.StreamWriter($entry.Open())
            try { $writer.Write([string]$Entries[$name]) } finally { $writer.Dispose() }
        }
        $entry = $zip.CreateEntry('payload.json')
        $writer = New-Object IO.StreamWriter($entry.Open())
        try { $writer.Write(($Index | ConvertTo-Json -Depth 20)) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    [pscustomobject]@{id=$Index.id;version=$Index.version;file=[IO.Path]::GetFileName($Path);bytes=(Get-Item -LiteralPath $Path).Length;sha256=(Get-ReignHash $Path);expandedBytes=($Index.files|Measure-Object bytes -Sum).Sum}
}
try {
    $record = Join-Path $root "record O'Brien.json"
    Write-ReignJson $record @{state='installing';version='first'}
    Write-ReignJson $record @{state='complete';version='second'}
    $updated = Read-ReignJson $record
    Assert-Contract ($updated.state -eq 'complete' -and $updated.version -eq 'second') 'replace-existing-install-record-atomically'
    $before = Get-ReignHash $record
    $locked = [IO.File]::Open($record,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try { Assert-Rejected { Write-ReignJson $record @{state='should-not-be-written'} } 'reject-locked-install-record-replacement' }
    finally { $locked.Dispose() }
    Assert-Contract ((Get-ReignHash $record) -eq $before -and @(Get-ChildItem -LiteralPath $root -Filter '*.new-*').Count -eq 0) 'failed-record-replacement-preserves-original-and-cleans-temp'
    # Execute only the launcher's actual record-reading expression. This proves
    # decoding under Windows PowerShell 5.1 without starting a server/listener.
    $InstallationFile = Join-Path $root 'utf8-installation.json'
    $expectedServerRoot = 'D:\' + [string][char]0x6F22 + [char]0x5B57 + '\ReignServer'
    Write-ReignJson $InstallationFile @{schema='reign-installation-v1';serverRoot=$expectedServerRoot}
    $tokens = $null
    $parseErrors = $null
    $launcher = Join-Path (Split-Path $PSScriptRoot -Parent) 'Start-ReignServer.ps1'
    $launcherAst = [Management.Automation.Language.Parser]::ParseFile($launcher,[ref]$tokens,[ref]$parseErrors)
    $readAssignments = @($launcherAst.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$record'},$false))
    Assert-Contract (@($parseErrors).Count -eq 0 -and $readAssignments.Count -eq 1) 'launcher-record-reader-is-bounded'
    $loadedRecord = & ([scriptblock]::Create($readAssignments[0].Right.Extent.Text))
    Assert-Contract ($loadedRecord.serverRoot -eq $expectedServerRoot) 'launcher-reads-utf8-installation-paths'
    $inputRoot = Join-Path $root 'input'
    $relative = "PortraitCache/_shared/Lord O'Brien 漢字/portrait.png"
    $entry = New-Fixture $inputRoot $relative 'original'
    $index = [pscustomobject]@{schema='reign-payload-v1';id='portraits';version='fixture';files=@($entry)}
    $zip = Join-Path $root 'valid.zip'
    $payload = Write-FixtureZip $zip $index @{$relative='original'}
    Assert-Contract ((Get-ReignDownloadBytes $root @($payload)) -eq 0) 'fully-offline-setup-needs-zero-download-bytes'
    $missing = [pscustomobject]@{file='missing.zip';bytes=1234}
    Assert-Contract ((Get-ReignDownloadBytes $root @($payload,$missing)) -eq 1234) 'preflight-counts-only-missing-downloads'
    $destination = Join-Path $root 'extracted'
    $expanded = Expand-ReignPayload $zip $destination $payload
    $extracted = Get-ReignSafeTarget $destination $relative
    Assert-Contract ([IO.File]::ReadAllText($extracted) -eq 'original') 'unicode-apostrophe-space-extraction'
    Assert-Contract ([IO.File]::GetLastWriteTimeUtc($extracted).Ticks -eq $entry.lastWriteUtcTicks) 'exact-ntfs-portrait-ticks'
    Assert-Rejected { Expand-ReignPayload $zip $destination $payload } 'refuse-nonfresh-staging'
    $corrupt = $payload | Select-Object *
    $corrupt.sha256 = '0' * 64
    Assert-Rejected { Expand-ReignPayload $zip (Join-Path $root 'corrupt') $corrupt } 'reject-corrupt-payload'
    foreach ($bad in @('../escape.txt','/absolute.txt','C:/escape.txt','safe/../../escape.txt','safe\escape.txt','safe/CON.txt','safe/file.','safe/a:b')) {
        Assert-Rejected { Get-ReignSafeTarget $root $bad } ('reject-path-' + $bad)
    }
    $badEntry = $entry | Select-Object *
    $badEntry.path = '../escape.txt'
    $badIndex = [pscustomobject]@{schema='reign-payload-v1';id='portraits';version='fixture';files=@($badEntry)}
    $badZip = Join-Path $root 'traversal.zip'
    $badPayload = Write-FixtureZip $badZip $badIndex @{'../escape.txt'='original'}
    Assert-Rejected { Expand-ReignPayload $badZip (Join-Path $root 'traversal') $badPayload } 'reject-verified-zip-traversal'
    Assert-Contract (-not [IO.File]::Exists((Join-Path $root 'escape.txt'))) 'traversal-wrote-no-outside-file'
    $duplicate = $entry | Select-Object *
    $duplicate.path = $relative.ToUpperInvariant()
    $badIndex.files = @($entry,$duplicate)
    $duplicateZip = Join-Path $root 'duplicate.zip'
    $duplicatePayload = Write-FixtureZip $duplicateZip $badIndex @{$relative='original'}
    Assert-Rejected { Expand-ReignPayload $duplicateZip (Join-Path $root 'duplicate') $duplicatePayload } 'reject-case-insensitive-duplicates'
    $userContent = Join-Path $root 'player-content'
    $old = New-Fixture $userContent $relative 'old-shipped'
    $merged = Merge-ReignContent $destination $userContent $expanded @($old)
    Assert-Contract ([IO.File]::ReadAllText((Join-Path $userContent $relative)) -eq 'original') 'update-previously-shipped-content'
    [IO.File]::WriteAllText((Join-Path $userContent $relative), 'user-edited')
    $merged = Merge-ReignContent $destination $userContent $expanded @($entry)
    Assert-Contract ([IO.File]::ReadAllText((Join-Path $userContent $relative)) -eq 'user-edited') 'preserve-user-edited-portrait'
    Assert-Contract ($merged.preservedPortraits.Count -eq 1) 'report-preserved-portrait'
    $versionRoot = Join-Path $root 'uninstall-version'
    $programEntry = New-Fixture $versionRoot 'bin/server.exe' 'shipped-program'
    Remove-ReignShippedFiles $versionRoot @($programEntry) $true
    Assert-Contract (-not [IO.Directory]::Exists($versionRoot)) 'remove-empty-owned-version-for-reinstall'
    $programEntry = New-Fixture $versionRoot 'bin/server.exe' 'shipped-program'
    [IO.File]::WriteAllText((Join-Path $versionRoot 'bin/server.exe'), 'locally-modified')
    $unlisted = New-Fixture $versionRoot 'personal.txt' 'unlisted'
    Remove-ReignShippedFiles $versionRoot @($programEntry) $true
    Assert-Contract ([IO.File]::ReadAllText((Join-Path $versionRoot 'bin/server.exe')) -eq 'locally-modified') 'uninstall-preserves-modified-program'
    Assert-Contract ([IO.File]::ReadAllText((Join-Path $versionRoot 'personal.txt')) -eq 'unlisted') 'uninstall-preserves-unlisted-file'
    $gameRoot = Join-Path $root 'uninstall-game'
    $moduleEntry = New-Fixture $gameRoot 'Modules/ReignBeta/client.dll' 'shipped-module'
    Remove-ReignShippedFiles $gameRoot @($moduleEntry) $false
    Assert-Contract ([IO.Directory]::Exists($gameRoot) -and -not [IO.Directory]::Exists((Join-Path $gameRoot 'Modules/ReignBeta'))) 'uninstall-keeps-game-root'
    Write-ReignJson (Join-Path $root 'proof.json') @{schema='reign-installer-contract-proof-v1';ok=$true;checks=@($checks);listenerStarted=$false;installedIntoGame=$false}
    Write-Host ($checks.Count.ToString() + ' installer contracts passed; ' + (Join-Path $root 'proof.json'))
} catch {
    Write-ReignJson (Join-Path $root 'proof.json') @{schema='reign-installer-contract-proof-v1';ok=$false;checks=@($checks);error=$_.Exception.Message}
    throw
}
