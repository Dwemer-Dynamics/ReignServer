[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BinDirectory,
    [Parameter(Mandatory=$true)][string]$StateDirectory,
    [ValidateRange(1024,65535)][int]$Port = 55432,
    [switch]$ValidationOnly,
    [switch]$KeepValidationRunning
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($KeepValidationRunning -and -not $ValidationOnly) { throw 'Only an isolated validation database can remain running after this provisioning command.' }
$bin = [IO.Path]::GetFullPath($BinDirectory)
$state = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\')
if ($bin -match '[^\x20-\x7e]' -or $state -match '[^\x20-\x7e]') {
    throw 'PostgreSQL binary and state folders require ASCII paths (English letters, numbers, spaces and punctuation). Choose folders without accented or other non-English names before provisioning.'
}
if (-not [IO.Path]::IsPathRooted($StateDirectory) -or $state.Length -lt 8) { throw 'Choose an explicit local PostgreSQL state directory.' }
$cluster = Join-Path $state 'cluster'
$keyPath = Join-Path $state 'owner.password'
$database = if ($ValidationOnly) { 'ReignValidation' } else { 'Reign' }
foreach ($tool in @('initdb.exe','pg_ctl.exe','psql.exe','createdb.exe','pg_dump.exe','pg_restore.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $bin $tool) -PathType Leaf)) { throw "PostgreSQL package is incomplete: $tool" }
}
$version = & (Join-Path $bin 'pg_ctl.exe') --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '15\.19\b') { throw 'This release requires the pinned PostgreSQL 15.19 tools.' }
if (Test-Path -LiteralPath (Join-Path $cluster 'PG_VERSION')) {
    if (-not (Test-Path -LiteralPath (Join-Path $state 'reign-cluster.json'))) { throw 'Refusing to adopt an unowned PostgreSQL cluster.' }
    $ownership = Get-Content -LiteralPath (Join-Path $state 'reign-cluster.json') -Raw | ConvertFrom-Json
    if ($ownership.schema -ne 'reign-native-postgres-v1' -or $ownership.database -ne $database -or $ownership.port -ne $Port) { throw 'Existing PostgreSQL ownership or port differs. Use the explicit migration procedure.' }
    if ((Get-Content -LiteralPath (Join-Path $cluster 'PG_VERSION') -Raw).Trim() -ne '15') { throw 'Database major version requires explicit migration.' }
    if (-not (Test-Path -LiteralPath $keyPath)) { throw 'The existing database credential is missing; it cannot be replaced automatically.' }
} elseif (Test-Path -LiteralPath $cluster) {
    throw 'An incomplete or unowned cluster directory exists. Preserve it for recovery before repairing this installation.'
}
[IO.Directory]::CreateDirectory($state) | Out-Null
Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes('Reign.PostgreSQL.v1')
if (-not (Test-Path -LiteralPath $keyPath)) {
    $random = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $password = [Convert]::ToBase64String($random)
    $cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($password), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($keyPath, $cipher)
} else {
    $password = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser))
}
function Protect-PrivateFile([string]$Path) {
    # icacls changes only the DACL; Set-Acl can request SACL privileges on repair
    # under a standard Windows account even when no audit changes are intended.
    $acl = Get-Acl -LiteralPath $Path
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $owner.Value) { throw 'The PostgreSQL credential file belongs to another user.' }
    & icacls.exe $Path /inheritance:r /grant:r ('*' + $owner.Value + ':(F)') '*S-1-5-18:(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect the PostgreSQL credential file.' }
    foreach ($rule in $acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) {
        $sid = $rule.IdentityReference.Value
        if ($sid -ne $owner.Value -and $sid -ne 'S-1-5-18') {
            & icacls.exe $Path /remove:g ('*' + $sid) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove an unrelated credential-file grant.' }
        }
    }
}
Protect-PrivateFile $keyPath
if (-not (Test-Path -LiteralPath (Join-Path $cluster 'PG_VERSION'))) {
    $passwordFile = Join-Path $state ('init-' + [guid]::NewGuid().ToString('N') + '.pw')
    try {
        [IO.File]::WriteAllText($passwordFile, $password, [Text.UTF8Encoding]::new($false))
        Protect-PrivateFile $passwordFile
        & (Join-Path $bin 'initdb.exe') -D $cluster --username=reign "--pwfile=$passwordFile" --encoding=UTF8 --no-locale --auth-host=scram-sha-256 --auth-local=scram-sha-256 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL initialization failed.' }
    } finally {
        if (Test-Path -LiteralPath $passwordFile) { [IO.File]::Delete($passwordFile) }
    }
    [ordered]@{schema='reign-native-postgres-v1'; major=15; port=$Port; database=$database; username='reign'; createdUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $state 'reign-cluster.json') -Encoding UTF8
}
& (Join-Path $bin 'pg_ctl.exe') status -D $cluster *> $null
if ($LASTEXITCODE -eq 0) { throw 'This cluster is already running. Provisioning never takes over another owner.' }
$oldPassword = $env:PGPASSWORD
$started = $false
$proofSucceeded = $false
try {
    $env:PGPASSWORD = $password
    # PowerShell's native pipeline waits for inherited descendant handles when
    # pg_ctl daemonizes. Wait only for pg_ctl; PostgreSQL writes to its own log.
    $controlArguments = 'start -D "' + $cluster + '" -l "' + (Join-Path $state 'postgresql.log') + '" -o "-p ' + $Port + ' -h 127.0.0.1" -w -t 45'
    $control = Start-Process -FilePath (Join-Path $bin 'pg_ctl.exe') -WindowStyle Hidden -ArgumentList $controlArguments -PassThru
    if (-not $control.WaitForExit(60000) -or $control.ExitCode -ne 0) { throw 'PostgreSQL did not start on the selected loopback port.' }
    $started = $true
    $exists = & (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p $Port -U reign -d postgres -X -A -t -v ON_ERROR_STOP=1 -c "SELECT 1 FROM pg_database WHERE datname='$database';"
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the new cluster.' }
    if ([string]::IsNullOrWhiteSpace(($exists -join ''))) {
        & (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p $Port -U reign --owner=reign --encoding=UTF8 --template=template0 $database
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the Reign database.' }
    }
    $identity = & (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p $Port -U reign -d $database -X -A -t -v ON_ERROR_STOP=1 -c "SELECT current_database() || '|' || current_user || '|' || current_setting('server_encoding');"
    if ($LASTEXITCODE -ne 0 -or ($identity -join '').Trim() -ne "$database|reign|UTF8") { throw 'Database identity verification failed.' }
    $proofSucceeded = $true
    [ordered]@{schema='reign-native-postgres-proof-v1'; ok=$true; database=$database; owner='reign'; encoding='UTF8'; port=$Port; stateDirectory=$state; keptRunning=[bool]$KeepValidationRunning; wslRequired=$false} | ConvertTo-Json
} finally {
    if ($started -and (-not $KeepValidationRunning -or -not $proofSucceeded)) {
        & (Join-Path $bin 'pg_ctl.exe') stop -D $cluster -m fast -w -t 30 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning 'PostgreSQL did not confirm shutdown; inspect this exact cluster before retry.' }
    }
    $env:PGPASSWORD = $oldPassword
    $password = $null
}
