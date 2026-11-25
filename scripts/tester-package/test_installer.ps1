[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-Reign.ps1') -LibraryOnly
$root=[IO.Path]::GetFullPath($FixtureRoot)
if(Test-Path -LiteralPath $root){throw 'Fixture root must be new.'}
New-Item -ItemType Directory -Path $root | Out-Null
$cases=0
function Require([bool]$Condition,[string]$Name){if(-not $Condition){throw "Failed contract: $Name"};$script:cases++;Write-Output "contract:$Name"}
function Refuse([scriptblock]$Action,[string]$Name){$failed=$false;try{& $Action | Out-Null}catch{$failed=$true};Require $failed $Name}
foreach($name in @('../escape','/absolute','a\b','C:/escape','x/../y','x//y','x/CON.txt','x/a.','x/a ')){
    Refuse {Resolve-ReignMember $root $name} "unsafe:$name"
}
Require ((Resolve-ReignMember $root 'safe/a.txt') -eq (Join-Path $root 'safe\a.txt')) 'member inside root'
Refuse {Assert-ReignLocalPath '\\remote\share\x'} 'UNC refused'
Assert-ReignWindowsAccount 'fixture-user' @('fixture-user');Require $true 'same Windows account'
Refuse {Assert-ReignWindowsAccount 'other-admin' @('fixture-user')} 'different administrator refused'
$package=Join-Path $root 'package';New-Item -ItemType Directory -Path $package | Out-Null
$names=@('reign-server-image.tar','server/reign-linux-artifact.json','ReignBeta/SubModule.xml',
    'ReignBeta/bin/Win64_Shipping_Client/ReignBeta.dll','portrait-helper/Bannerlord.NativeCharacterImageGenerator.App.exe',
    'initialize-server.sh','Start-Reign.ps1','server-support/scripts/Start-ReignServer-Wsl.ps1',
    'server-support/scripts/reign-recover.py','server-support/release/release.json','image-receipt.json','INSTALL.html','Reign User Guide.html')
$entries=@()
foreach($name in $names){
    $path=Resolve-ReignMember $package $name;New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    [IO.File]::WriteAllText($path,"fixture:$name",[Text.UTF8Encoding]::new($false))
    $entries+=@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-ReignSha $path)}
}
$manifest=@{schema='reign-tester-package-v1';protocolVersion=1;files=$entries}
$manifestPath=Join-Path $package 'package-manifest.json';Write-ReignJson $manifestPath $manifest
Require ((Assert-ReignPackage $package).files.Count -eq $names.Count) 'intact full fixture'
$before=Get-ReignSha $manifestPath;Assert-ReignPackage $package | Out-Null
Require ((Get-ReignSha $manifestPath) -eq $before) 'integrity read nonmutation'
$path=Join-Path $package 'INSTALL.html';$original=[IO.File]::ReadAllBytes($path)
[IO.File]::WriteAllText($path,'altered');Refuse {Assert-ReignPackage $package} 'changed bytes refused';[IO.File]::WriteAllBytes($path,$original)
$extra=Join-Path $package 'unexpected.txt';[IO.File]::WriteAllText($extra,'unexpected');Refuse {Assert-ReignPackage $package} 'extra member refused';Remove-Item -LiteralPath $extra
Move-Item -LiteralPath $path -Destination "$path.moved";Refuse {Assert-ReignPackage $package} 'missing member refused';Move-Item -LiteralPath "$path.moved" -Destination $path
$manifest.files+=@{path='install.HTML';bytes=$original.Length;sha256=(Get-ReignSha $path)};Write-ReignJson $manifestPath $manifest
Refuse {Assert-ReignPackage $package} 'case duplicate refused';$manifest.files=$entries;Write-ReignJson $manifestPath $manifest
$stage=Join-Path $root 'stage';Copy-ReignPayload $package 'ReignBeta/' $stage $manifest
Assert-ReignCopiedPayload 'ReignBeta/' $stage $manifest;Require $true 'copy and verify'
$stageDll=Join-Path $stage 'bin\Win64_Shipping_Client\ReignBeta.dll'
Set-Content -LiteralPath $stageDll -Stream Zone.Identifier -Value '[ZoneTransfer]'
Copy-ReignPayload $package 'ReignBeta/' $stage $manifest
Require (-not (Get-Item -LiteralPath $stageDll -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'verified binary unblocked'
[IO.File]::WriteAllText((Join-Path $stage 'unknown.txt'),'keep');Refuse {Assert-ReignCopiedPayload 'ReignBeta/' $stage $manifest} 'unknown installed file preserved'
Require (Test-Path -LiteralPath (Join-Path $stage 'unknown.txt')) 'refusal does not delete unknown file'
$journal=Join-Path $root 'transaction.json';Write-ReignJson $journal @{phase='first'};Write-ReignJson $journal @{phase='second'}
Require ((Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json).phase -eq 'second') 'atomic journal replacement'
Require (-not (Test-Path -LiteralPath "$journal.next")) 'no partial journal remains'
$imageLock=(Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\release\release.json') -Raw -Encoding UTF8 | ConvertFrom-Json).serverImage
$imageMarker=[pscustomobject]@{schema=$imageLock.schema;debian=$imageLock.os.versionId;postgresMajor=$imageLock.postgresMajor;python=$imageLock.python+'.5';icuPackage=$imageLock.icuPackage;baseManifestDigest=$imageLock.base.manifestDigest}
Assert-ReignImageIdentity $imageMarker $imageLock @('17');Require $true 'current server image accepted'
$refreshed=$imageMarker.PSObject.Copy();$refreshed.baseManifestDigest='sha256:'+('0'*64)
Assert-ReignImageIdentity $refreshed $imageLock @('17');Require $true 'same-major refreshed base accepted for update'
foreach($field in @(@('python','3.11.2'),@('icuPackage','libicu72'),@('postgresMajor',15),@('debian','12'))){
    $changed=$imageMarker.PSObject.Copy();$changed.($field[0])=$field[1]
    Refuse {Assert-ReignImageIdentity $changed $imageLock @('17')} "incompatible image $($field[0]) refused"
}
Refuse {Assert-ReignImageIdentity $imageMarker $imageLock @('15','17')} 'legacy PostgreSQL cluster refused'
Refuse {Assert-ReignImageIdentity ([pscustomobject]@{schema='reign-tester-image-v1';debian='12';postgresMajor=15}) $imageLock @()} 'Debian 12 image marker refused'
Refuse {Assert-ReignImageIdentity $null $imageLock @('17')} 'missing image marker refused'
$tokens=$null;$errors=$null
[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-Reign.ps1'),[ref]$tokens,[ref]$errors)|Out-Null
Require ($errors.Count -eq 0) 'PowerShell 5 installer parse'
[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Start-Reign.ps1'),[ref]$tokens,[ref]$errors)|Out-Null
Require ($errors.Count -eq 0) 'PowerShell 5 launcher parse'
@{ok=$true;cases=$cases;nativeCalls=0;fixture=$root}|ConvertTo-Json -Compress
