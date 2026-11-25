[CmdletBinding()]
param(
    [string]$BannerlordRoot,
    [string]$InstallRoot = (Join-Path $env:USERPROFILE 'ReignTester'),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$')][string]$Distro = 'ReignServer',
    [string]$InstallationFile = (Join-Path $env:USERPROFILE '.reign\installation.json'),
    [string]$NativeSaveRoot,
    [switch]$VerifyOnly, [switch]$Resume, [switch]$Update, [switch]$LibraryOnly
)
$ErrorActionPreference = 'Stop'
if($PSVersionTable.PSVersion.Major -eq 5){
    $systemModules=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
    $env:PSModulePath=$systemModules+';'+$env:PSModulePath
}

function Get-ReignSha([string]$Path) {
    $algorithm=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($Path)
    try{([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
    finally{$stream.Dispose();$algorithm.Dispose()}
}
function Assert-ReignLocalPath([string]$Path) {
    $Path=$Path.Trim().Trim('"')
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.StartsWith('\\')) { throw 'Choose an absolute local drive path.' }
    $full = [IO.Path]::GetFullPath($Path.Trim().Trim('"')).TrimEnd('\')
    if($full.Length -le 2){throw 'Choose a folder rather than a drive root.'}
    $cursor = $full
    while ($cursor) {
        try {
            $attributes=[IO.File]::GetAttributes($cursor)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installation paths are unsupported.' }
        } catch [IO.FileNotFoundException] {} catch [IO.DirectoryNotFoundException] {}
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
    return $full
}
function Resolve-ReignMember([string]$Root, [string]$Name) {
    if (-not $Name -or $Name -match '[\\:*?\[\]]' -or $Name.StartsWith('/') -or
        @($Name.Split('/') | Where-Object { $_ -in @('','..','.') -or $_ -ne $_.TrimEnd(' ','.') -or $_ -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$' }).Count) {
        throw 'Unsafe package member.'
    }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Name.Replace('/','\')))
    if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package member escaped its root.' }
        return (Assert-ReignLocalPath $path)
}
function Assert-ReignPackage([string]$Root) {
    $Root = Assert-ReignLocalPath $Root
    $manifestPath = Join-Path $Root 'package-manifest.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 32MB) { throw 'Oversized package manifest.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema -cne 'reign-tester-package-v1' -or $manifest.protocolVersion -ne 1 -or -not $manifest.files.Count) { throw 'Unsupported or empty tester package.' }
    $expected = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [void]$expected.Add('package-manifest.json')
    foreach ($entry in $manifest.files) {
        foreach($prefix in @('ReignBeta/','portrait-helper/','server/','server-support/')){
            if($entry.path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and -not $entry.path.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Incorrect payload path case.'}
        }
        $path = Resolve-ReignMember $Root $entry.path
        if (-not $expected.Add($entry.path) -or $entry.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $entry.bytes -lt 0) { throw 'Invalid/duplicate package evidence.' }
        $file = Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer -or $file.Attributes -band [IO.FileAttributes]::ReparsePoint -or
            $file.Length -ne $entry.bytes -or (Get-ReignSha $path) -cne $entry.sha256) { throw "Incomplete or altered package: $($entry.path)" }
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package links are unsupported.' }
        if (-not $item.PSIsContainer) {
            $relative = $item.FullName.Substring($Root.Length + 1).Replace('\','/')
            if (-not $expected.Contains($relative)) { throw "Unexpected package file: $relative" }
        }
    }
    foreach ($required in @('reign-server-image.tar','server/reign-linux-artifact.json','ReignBeta/SubModule.xml',
        'ReignBeta/bin/Win64_Shipping_Client/ReignBeta.dll','portrait-helper/Bannerlord.NativeCharacterImageGenerator.App.exe',
        'initialize-server.sh','Start-Reign.ps1','server-support/scripts/Start-ReignServer-Wsl.ps1',
        'server-support/scripts/reign-recover.py','server-support/release/release.json','image-receipt.json','INSTALL.html','Reign User Guide.html')) {
        if (@($manifest.files | Where-Object {$_.path -ceq $required}).Count -ne 1) { throw "Required payload missing: $required" }
    }
    return $manifest
}
function Assert-ReignImageIdentity($Marker, $Lock, [string[]]$Clusters) {
    # Database majors are never migrated in place; an older owned distro is left untouched.
    $legacy=@($Clusters | Where-Object {$_ -and $_ -cne [string]$Lock.postgresMajor})
    if($legacy.Count){throw "This Reign distro holds a PostgreSQL $($legacy -join ', ') cluster from an older server image. Nothing was changed. Keep it, export the reign database as described in INSTALL.html, then install into a new distro."}
    # Same-major updates keep the installed distro, so compare runtime compatibility, not
    # the base digest: a refreshed base of the same release must not strand player data.
    if(-not $Marker -or $Marker.schema -cne $Lock.schema -or [string]$Marker.debian -cne [string]$Lock.os.versionId -or
        [string]$Marker.postgresMajor -cne [string]$Lock.postgresMajor -or $Marker.icuPackage -cne $Lock.icuPackage -or
        -not ([string]$Marker.python).StartsWith([string]$Lock.python+'.',[StringComparison]::Ordinal)){
        throw 'The installed Reign server image differs from the image this package requires. Nothing was changed; install into a new distro.'
    }
}
function Write-ReignJson([string]$Path, $Value) {
    $Path=Assert-ReignLocalPath $Path
    Assert-ReignLocalPath "$Path.next" | Out-Null
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText("$Path.next",($Value | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace("$Path.next",$Path,[NullString]::Value) }
    else { [IO.File]::Move("$Path.next",$Path) }
}
function Invoke-ReignWsl([string[]]$Arguments) {
    & "$env:SystemRoot\System32\wsl.exe" @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'The selected Reign WSL operation failed. Keep the transaction journal and retry with -Resume.' }
}
function ConvertTo-ReignWsl([string]$Path) {
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    $result = & "$env:SystemRoot\System32\wsl.exe" -d $Distro -u root --exec /usr/bin/wslpath -u $Path.Replace('\','/')
    if ($LASTEXITCODE -ne 0 -or -not ([string]$result).StartsWith('/mnt/')) { throw 'Selected files are not accessible to this WSL distro.' }
    return ([string]$result).Trim()
}
function Assert-ReignGame([string]$Root, [string]$Package) {
    $Root = Assert-ReignLocalPath $Root
    if (-not (Test-Path -LiteralPath "$Root\bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll" -PathType Leaf)) { throw 'Select your complete Steam Bannerlord installation.' }
    $reign=[xml]::new();$reign.XmlResolver=$null;$reign.Load("$Package\ReignBeta\SubModule.xml")
    $required = @($reign.Module.DependedModuleMetadatas.DependedModuleMetadata | Where-Object { $_.optional -ne 'true' })
    $moduleFiles = @()
    foreach ($rootPath in @((Join-Path $Root 'Modules'),(Join-Path (Split-Path (Split-Path $Root)) 'workshop\content\261550'))) {
        if (-not (Test-Path -LiteralPath $rootPath)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $rootPath -Directory) {
            if (Test-Path -LiteralPath (Join-Path $directory.FullName 'SubModule.xml')) { $moduleFiles += Join-Path $directory.FullName 'SubModule.xml' }
            elseif ($rootPath.Contains('workshop')) {
                foreach ($child in Get-ChildItem -LiteralPath $directory.FullName -Directory) {
                    if (Test-Path -LiteralPath (Join-Path $child.FullName 'SubModule.xml')) { $moduleFiles += Join-Path $child.FullName 'SubModule.xml' }
                }
            }
        }
    }
    $modules = @($moduleFiles | ForEach-Object { $xml=[xml]::new();$xml.XmlResolver=$null;try{$xml.Load($_)}catch{throw "Unreadable module manifest: $_"}; [PSCustomObject]@{id=$xml.Module.Id.value;version=$xml.Module.Version.value;path=$_} })
    foreach ($dependency in $required) {
        $foundModules = @($modules | Where-Object { $_.id -ceq $dependency.id })
        if ($foundModules.Count -ne 1) { throw "Install exactly one dependency module: $($dependency.id). Existing modules are preserved." }
        $actual = [version]($foundModules[0].version -replace '^[vVeE]','')
        $minimum = [version]($dependency.version -replace '^[vVeE]','')
        if (($dependency.id -in @('Native','SandBoxCore','Sandbox') -and $actual -ne $minimum) -or $actual -lt $minimum) { throw "Incompatible module version: $($dependency.id)." }
    }
    if (@($modules | Where-Object { $_.id -eq 'ArenaOverhaul' }).Count) { throw 'Remove/disable the separate Arena Overhaul installation before using the bundled Reign version. No module was moved.' }
    if (@($modules | Where-Object { $_.id -eq 'ReignBeta' -and $_.path -ne "$Root\Modules\ReignBeta\SubModule.xml" }).Count) { throw 'A second ReignBeta module was found.' }
    return $Root
}
function Copy-ReignPayload([string]$Package,[string]$Prefix,[string]$Destination,$Manifest) {
    $Destination=Assert-ReignLocalPath $Destination
    foreach ($entry in @($Manifest.files | Where-Object { $_.path.StartsWith($Prefix,[StringComparison]::Ordinal) })) {
        $relative = $entry.path.Substring($Prefix.Length)
        $source = Resolve-ReignMember $Package $entry.path
        $target = Resolve-ReignMember $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        if ((Get-ReignSha $target) -cne $entry.sha256) { throw 'Staged payload checksum mismatch.' }
        if([IO.Path]::GetExtension($target).ToLowerInvariant() -in @('.dll','.exe','.ps1','.psm1','.cmd')){Unblock-File -LiteralPath $target}
    }
}
function Copy-ReignPayloadAtomic([string]$Package,[string]$Prefix,[string]$Destination,$Manifest) {
    if(Test-Path -LiteralPath $Destination){Assert-ReignCopiedPayload $Prefix $Destination $Manifest;return}
    $partial=Assert-ReignLocalPath "$Destination.partial"
    # Only known manifest members of this transaction are recopied. Unknown
    # additions are preserved and cause the complete-inventory check to refuse.
    Copy-ReignPayload $Package $Prefix $partial $Manifest
    Assert-ReignCopiedPayload $Prefix $partial $Manifest
    [IO.Directory]::Move($partial,$Destination)
}
function Copy-ReignVerifiedFile([string]$Package,[string]$Name,[string]$Target,$Manifest) {
    $entry=@($Manifest.files | Where-Object {$_.path -ceq $Name})
    if($entry.Count -ne 1){throw "Missing verified file: $Name"}
    $Target=Assert-ReignLocalPath $Target
    New-Item -ItemType Directory -Path (Split-Path $Target) -Force | Out-Null
    Copy-Item -LiteralPath (Resolve-ReignMember $Package $Name) -Destination "$Target.next" -Force
    if((Get-ReignSha "$Target.next") -cne $entry[0].sha256){throw 'Copied installer input changed.'}
    Unblock-File -LiteralPath "$Target.next"
    if(Test-Path -LiteralPath $Target){[IO.File]::Replace("$Target.next",$Target,[NullString]::Value)}else{[IO.File]::Move("$Target.next",$Target)}
}
function Normalize-ReignBase([string]$Path) {
    if(-not $Path){return $null}
    if($Path.StartsWith('\\?\')){$Path=$Path.Substring(4)}
    [IO.Path]::GetFullPath($Path).TrimEnd('\')
}
function Assert-ReignWindowsAccount([string]$CurrentSid,[string[]]$DesktopSids) {
    if(@($DesktopSids | Where-Object {$_ -and $_ -cne $CurrentSid}).Count){throw 'Run the installer under the Windows account that will play Bannerlord, not a different administrator account.'}
}
function Assert-ReignCopiedPayload([string]$Prefix,[string]$Destination,$Manifest) {
    $expected=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in @($Manifest.files | Where-Object {$_.path.StartsWith($Prefix,[StringComparison]::Ordinal)})) {
        $name=$entry.path.Substring($Prefix.Length); [void]$expected.Add($name)
        $path=Resolve-ReignMember $Destination $name
        $file=Get-Item -LiteralPath $path -Force
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -ne $entry.bytes -or (Get-ReignSha $path) -cne $entry.sha256){throw 'Installed/staged content differs; preserve it for recovery.'}
    }
    foreach($item in Get-ChildItem -LiteralPath $Destination -Recurse -Force) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected payload root.'}
        if(-not $item.PSIsContainer -and -not $expected.Contains($item.FullName.Substring($Destination.Length+1).Replace('\','/'))){throw 'Unknown installed content must be reviewed before replacement.'}
    }
}
if ($LibraryOnly) { return }
try {
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 22000) { throw 'Reign requires Windows 11 x64.' }
    $windowsSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $desktopSids=@(Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Where-Object {$_.SessionId -eq $sessionId} | ForEach-Object {
        $owner=Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid
        if($owner.ReturnValue -eq 0){$owner.Sid}
    })
    Assert-ReignWindowsAccount $windowsSid $desktopSids
    $package = Assert-ReignLocalPath $PSScriptRoot
    Write-Output 'Checking complete package hashes. Large portrait and server files can take several minutes.'
    $manifest = Assert-ReignPackage $package
    $packageId = (Get-ReignSha (Join-Path $package 'package-manifest.json')).Substring(0,16)
    if (-not $BannerlordRoot) { $BannerlordRoot = Read-Host 'Full path to your Steam Mount & Blade II Bannerlord folder' }
    $game = Assert-ReignGame $BannerlordRoot $package
    $InstallRoot = Assert-ReignLocalPath $InstallRoot
    $InstallationFile = Assert-ReignLocalPath $InstallationFile
    if(-not $NativeSaveRoot){$NativeSaveRoot=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Game Saves'}
    $NativeSaveRoot=Assert-ReignLocalPath $NativeSaveRoot
    $distroRoot = Join-Path $InstallRoot 'WSL'
    if ($InstallRoot.StartsWith($game+'\',[StringComparison]::OrdinalIgnoreCase) -or $game.StartsWith($InstallRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep server installation outside the game directory.' }
    $drive = New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($InstallRoot))
    if ($drive.DriveFormat -ne 'NTFS' -or $drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'Install Reign on a fixed local NTFS drive.' }
    $checkPath=$InstallRoot
    while($checkPath){
        if(Test-Path -LiteralPath $checkPath){$volume=Get-Item -LiteralPath $checkPath -Force;if($volume.Attributes -band ([IO.FileAttributes]::Compressed -bor [IO.FileAttributes]::Encrypted)){throw 'The WSL path cannot use NTFS compression or EFS.'}}
        $parent=Split-Path -Parent $checkPath;if($parent -eq $checkPath){break};$checkPath=$parent
    }
    if ($drive.AvailableFreeSpace -lt 24GB) { throw 'Keep at least 24 GB free for the server image, database and recovery.' }
    $moduleBytes=($manifest.files | Where-Object {$_.path.StartsWith('ReignBeta/',[StringComparison]::Ordinal)} | Measure-Object bytes -Sum).Sum
    $gameDrive=New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($game))
    if($gameDrive.AvailableFreeSpace -lt ($moduleBytes+1GB)){throw 'The game drive needs room for the complete staged module plus 1 GB reserve.'}
    $gamePrefix = $game + '\'
    if (@(Get-CimInstance Win32_Process | Where-Object { ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($gamePrefix,[StringComparison]::OrdinalIgnoreCase)) -or (-not $_.ExecutablePath -and $_.Name -match '^(Bannerlord|TaleWorlds.MountAndBlade.Launcher)') }).Count) { throw 'Close the selected Bannerlord installation and its launcher first.' }
    $ownerPath = Join-Path $InstallRoot 'tester-installation.json'
    $journalPath = Join-Path $InstallRoot "transactions\$packageId.json"
    $previous = if (Test-Path -LiteralPath $ownerPath) { Get-Content -LiteralPath $ownerPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $journal = if (Test-Path -LiteralPath $journalPath) { Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    if($journal -and ($journal.schema -cne 'reign-tester-transaction-v1' -or $journal.ownerId -cnotmatch '^[a-f0-9]{32}$' -or $journal.windowsUserSid -cne $windowsSid -or $journal.distro -cne $Distro -or $journal.gameRoot -ine $game -or $journal.installationFile -ine $InstallationFile)){throw 'Recovery journal identity differs from this installation request.'}
    $module = Join-Path $game 'Modules\ReignBeta'
    & "$env:SystemRoot\System32\wsl.exe" --status | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Install/enable WSL 2 and restart Windows before installing Reign.'}
    $registrations = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object {$_.DistributionName -and $_.BasePath})
    $selected = @($registrations | Where-Object { $_.DistributionName -ieq $Distro })
    if ($previous) {
        if (-not $Update -and -not $Resume) { throw 'This tester installation already exists. Use -Update for a newer package.' }
        if ($previous.schema -ne 'reign-tester-installation-v1' -or $previous.windowsUserSid -cne $windowsSid -or $previous.gameRoot -ine $game -or $previous.distro -cne $Distro -or $previous.installationFile -ine $InstallationFile) { throw 'Existing installation identity differs; nothing was replaced.' }
        $oldManifest=Get-Content -LiteralPath (Join-Path $InstallRoot 'installed-package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if([string]::CompareOrdinal($manifest.qualification.validationRun,$oldManifest.qualification.validationRun) -lt 0){throw 'An older package cannot downgrade an installed tester build.'}
        if($Resume -and $journal -and $journal.phase -ceq 'activating-module' -and -not (Test-Path -LiteralPath $module)) {
            Assert-ReignCopiedPayload 'ReignBeta/' (Join-Path $game ".reign-tester\before-$packageId") $oldManifest
        } elseif($Resume -and $journal -and $journal.phase -ceq 'activating-module') {
            try { Assert-ReignCopiedPayload 'ReignBeta/' $module $manifest }
            catch { Assert-ReignCopiedPayload 'ReignBeta/' $module $oldManifest }
        } else { Assert-ReignCopiedPayload 'ReignBeta/' $module $oldManifest }
    } elseif (((Test-Path -LiteralPath $InstallationFile) -or (Test-Path -LiteralPath $module)) -and -not ($Resume -and $journal)) { throw 'An existing Reign installation was preserved. This installer cannot adopt a legacy/developer installation.' }
    if(-not $previous -and $Resume -and $journal -and (Test-Path -LiteralPath $module)){Assert-ReignCopiedPayload 'ReignBeta/' $module $manifest}
    if(Test-Path -LiteralPath $InstallationFile){
        $ownedRecord=Get-Content -LiteralPath $InstallationFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $expectedOwner=if($previous){$previous.ownerId}elseif($journal){$journal.ownerId}else{''}
        if(-not $expectedOwner -or $ownedRecord.testerOwnerId -cne $expectedOwner -or $ownedRecord.moduleRoot -ine $module -or $ownedRecord.wslDistro -cne $Distro){throw 'The current installation record is not owned by this tester installation.'}
    }
    if(-not $previous -and -not $journal -and (Test-Path -LiteralPath $InstallRoot) -and @(Get-ChildItem -LiteralPath $InstallRoot -Force).Count){throw 'The selected server folder is not empty and has no tester ownership receipt.'}
    if ($selected.Count -gt 1 -or ($selected.Count -eq 1 -and -not ($previous -or ($Resume -and $journal)))) { throw 'The selected WSL distro already exists and is not owned by this tester installation.' }
    if ($selected.Count -eq 1 -and (Normalize-ReignBase $selected[0].BasePath) -ine $distroRoot) { throw 'WSL storage does not match the owned installation.' }
    if (@($registrations | Where-Object { $_.DistributionName -ine $Distro -and (Normalize-ReignBase $_.BasePath) -ieq $distroRoot }).Count) { throw 'Another distro uses the selected storage directory.' }
    if($selected.Count -eq 1 -and -not $previous -and -not $journal.importCompleted){throw 'An incomplete distro import needs inspection; its storage is preserved.'}
    if ($journal -and $journal.packageId -cne $packageId) { throw 'Recovery journal belongs to a different package.' }
    if ($previous -and $selected.Count -eq 1) {
        # Read-only: refuse an older image before the journal, snapshot or service stop.
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $imageLock=(Get-Content -LiteralPath (Join-Path $package 'server-support\release\release.json') -Raw -Encoding UTF8 | ConvertFrom-Json).serverImage
        $clusters=@(& "$env:SystemRoot\System32\wsl.exe" -d $Distro -u root --exec /usr/bin/find /etc/postgresql /var/lib/postgresql -mindepth 1 -maxdepth 1 -type d -regex '.*/[0-9]+' -printf '%f\n')
        if($LASTEXITCODE -ne 0){throw 'The installed PostgreSQL clusters could not be inspected; nothing was changed.'}
        $markerText=& "$env:SystemRoot\System32\wsl.exe" -d $Distro -u root --exec /bin/cat /etc/reign-tester-image.json
        if($LASTEXITCODE -ne 0){throw 'The installed Reign server image identity could not be read; nothing was changed.'}
        Assert-ReignImageIdentity (($markerText -join "`n") | ConvertFrom-Json) $imageLock $clusters
    }
    if ($VerifyOnly) { Write-Output 'Package and installation preflight passed. No installation changes made.'; return }
    if($previous -and $previous.packageId -ceq $packageId -and $journal -and $journal.complete){Write-Output 'This exact package is already installed and verified.';return}
    if ($journal -and -not $Resume -and -not $journal.complete) { throw 'An interrupted install exists. Read its journal, then use -Resume.' }
    if (-not $journal) {
        $journal = [ordered]@{schema='reign-tester-transaction-v1';packageId=$packageId;ownerId=$(if($previous){$previous.ownerId}else{[Guid]::NewGuid().ToString('N')});windowsUserSid=$windowsSid;distro=$Distro;gameRoot=$game;installationFile=$InstallationFile;phase='preflight';complete=$false;importCompleted=$false;snapshotPath='';snapshotSha256=''}
        Write-ReignJson $journalPath $journal
    }
    # Stage client content before creating a distro; lack of game-folder write access stops here.
    $deploymentRoot = Assert-ReignLocalPath (Join-Path $game '.reign-tester')
    New-Item -ItemType Directory -Path $deploymentRoot -Force | Out-Null
    $stage = Join-Path $deploymentRoot "stage-$packageId"
    $backup = Join-Path $deploymentRoot "before-$packageId"
    Copy-ReignPayloadAtomic $package 'ReignBeta/' $stage $manifest
    $helper = Join-Path $InstallRoot "helpers\$packageId"
    Copy-ReignPayloadAtomic $package 'portrait-helper/' $helper $manifest
    # Copy only server installation inputs to the chosen local server drive. The
    # original download may be on a drive that this WSL installation cannot mount.
    $serverStage=Join-Path $InstallRoot "server-staging\$packageId"
    foreach($prefix in @('server/','server-support/')) {
        $destination=Join-Path $serverStage $prefix.TrimEnd('/')
        Copy-ReignPayloadAtomic $package $prefix $destination $manifest
    }
    Copy-ReignVerifiedFile $package 'initialize-server.sh' (Join-Path $serverStage 'initialize-server.sh') $manifest
    $inputFiles=@($manifest.files | Where-Object {$_.path.StartsWith('server/',[StringComparison]::Ordinal) -or $_.path.StartsWith('server-support/',[StringComparison]::Ordinal) -or $_.path -ceq 'initialize-server.sh'})
    Write-ReignJson (Join-Path $serverStage 'install-inputs.json') ([ordered]@{schema='reign-tester-inputs-v1';files=$inputFiles})
    $launcher=Join-Path $InstallRoot "launchers\$packageId"
    foreach($name in @('Start-Reign.ps1','Start-ReignServer-Wsl.ps1','reign-recover.py')){
        $member=if($name -eq 'Start-Reign.ps1'){$name}else{"server-support/scripts/$name"}
        Copy-ReignVerifiedFile $package $member (Join-Path $launcher $name) $manifest
    }
    if ($selected.Count -eq 0) {
        if (Test-Path -LiteralPath $distroRoot) { throw 'Unregistered WSL storage already exists; preserve it for recovery.' }
        New-Item -ItemType Directory -Path $distroRoot | Out-Null
        $journal.phase='importing'; Write-ReignJson $journalPath $journal
        $imageEntry=@($manifest.files | Where-Object {$_.path -ceq 'reign-server-image.tar'})[0]
        if((Get-ReignSha (Join-Path $package 'reign-server-image.tar')) -cne $imageEntry.sha256){throw 'Prepared image changed before import.'}
        Invoke-ReignWsl -Arguments @('--import',$Distro,$distroRoot,(Join-Path $package 'reign-server-image.tar'),'--version','2')
        $journal.importCompleted=$true;Write-ReignJson $journalPath $journal
    }
    if($previous -and -not $journal.snapshotPath){
        $nativeOwner=& "$env:SystemRoot\System32\wsl.exe" -d $Distro -u root --exec /bin/cat /var/www/html/ReignServer/tester-owner.json
        if($LASTEXITCODE -ne 0 -or ($nativeOwner | ConvertFrom-Json).ownerId -cne $journal.ownerId){throw 'Linux installation ownership could not be verified.'}
        $journal.phase='snapshotting';Write-ReignJson $journalPath $journal
        Invoke-ReignWsl -Arguments @('-d',$Distro,'-u','root','--exec','/usr/bin/systemctl','stop','reignserver.service','postgresql','apache2')
        Invoke-ReignWsl -Arguments @('--terminate',$Distro)
        $snapshot=Join-Path $InstallRoot "backups\before-$packageId.tar"
        $disk=Get-Item -LiteralPath (Join-Path $distroRoot 'ext4.vhdx')
        if($drive.AvailableFreeSpace -lt ($disk.Length+2GB)){throw 'Not enough free space for the owned pre-update distro snapshot.'}
        New-Item -ItemType Directory -Path (Split-Path $snapshot) -Force | Out-Null
        if(Test-Path -LiteralPath $snapshot){throw 'A prior snapshot exists; review it before retrying.'}
        Invoke-ReignWsl -Arguments @('--export',$Distro,$snapshot)
        $journal.snapshotPath=$snapshot;$journal.snapshotSha256=Get-ReignSha $snapshot;Write-ReignJson $journalPath $journal
    }
    $journal.phase='initializing-server'; Write-ReignJson $journalPath $journal
    $bridge = [ordered]@{schema='reign-windows-bridge-v1';nativeGenerator=(ConvertTo-ReignWsl (Join-Path $helper 'Bannerlord.NativeCharacterImageGenerator.App.exe'));nativeSaveRoot=(ConvertTo-ReignWsl $NativeSaveRoot);bannerlordModules=(ConvertTo-ReignWsl (Join-Path $game 'Modules'))}
    $bridgePath=Join-Path $InstallRoot "transactions\bridge-$packageId.json"; Write-ReignJson $bridgePath $bridge
    $packageWsl=ConvertTo-ReignWsl $serverStage
    $installMode=if($previous){'update'}elseif($Resume){'resume'}else{'fresh'}
    $scriptEntry=@($manifest.files | Where-Object {$_.path -ceq 'initialize-server.sh'})[0]
    Invoke-ReignWsl -Arguments @('-d',$Distro,'-u','root','--exec','/bin/bash',"$packageWsl/initialize-server.sh",$packageWsl,$journal.ownerId,(ConvertTo-ReignWsl $bridgePath),$installMode,(Get-ReignSha (Join-Path $serverStage 'install-inputs.json')),$scriptEntry.sha256)
    $journal.phase='activating-module'; Write-ReignJson $journalPath $journal
    $alreadyActivated=$false
    if($Resume -and (Test-Path -LiteralPath $module)) {
        try { Assert-ReignCopiedPayload 'ReignBeta/' $module $manifest; $alreadyActivated=$true } catch {}
    }
    if ((Test-Path -LiteralPath $module) -and -not $alreadyActivated) {
        if (Test-Path -LiteralPath $backup) { throw 'Existing module backup needs recovery review. It was preserved with the staged/current modules.' }
        if (-not $previous -and -not $Resume) { throw 'Unowned module appeared during installation.' }
        [IO.Directory]::Move($module,$backup)
    }
    if(-not $alreadyActivated){[IO.Directory]::Move($stage,$module)}
    $prefix="\\wsl.localhost\$Distro"
    $record=[ordered]@{schema='reign-installation-v1';version=$manifest.version;protocolVersion=1;contentVersion=$manifest.contentVersion;serverMode='dwemerdistro-wsl';wslDistro=$Distro;serverRoot="$prefix\var\www\html\ReignServer\runtime\current";contentRoot=$module;dataRoot="$prefix\var\www\html\ReignServer\data";bannerlordRoot=$game;moduleRoot=$module;postgresBin='';postgresPort=5432;nativeGeneratorRoot=$helper;testerOwnerId=$journal.ownerId}
    Write-ReignJson $InstallationFile $record
    Copy-Item -LiteralPath (Join-Path $package 'package-manifest.json') -Destination (Join-Path $InstallRoot 'installed-package-manifest.json') -Force
    Write-ReignJson $ownerPath ([ordered]@{schema='reign-tester-installation-v1';ownerId=$journal.ownerId;windowsUserSid=$windowsSid;packageId=$packageId;gameRoot=$game;distro=$Distro;installationFile=$InstallationFile;clientCommit=$manifest.clientCommit;serverCommit=$manifest.serverCommit})
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Start Reign Tester.lnk'
    if (-not (Test-Path -LiteralPath $shortcutPath) -or $previous) {
        $shell=New-Object -ComObject WScript.Shell; $shortcut=$shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $shortcut.Arguments="-NoProfile -ExecutionPolicy Bypass -File `"$launcher\Start-Reign.ps1`" -InstallationFile `"$InstallationFile`" -OpenControlCenter"
        $shortcut.WorkingDirectory=$launcher; $shortcut.Save()
    }
    $journal.phase='complete'; $journal.complete=$true; Write-ReignJson $journalPath $journal
    Write-Output 'Reign installation completed. Services are stopped. Use Start Reign Tester to start the server, then configure your own AI provider.'
} catch {
    if ($journal -and $journalPath) { Write-ReignJson $journalPath $journal }
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
