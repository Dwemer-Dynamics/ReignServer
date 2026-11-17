[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [string]$ValidationReport,
    [string]$LinuxBuildReport,
    [ValidateSet('ReignServer')][string]$Distro = 'ReignServer',
    [string]$BannerlordPath = $env:REIGN_BANNERLORD_PATH,
    [switch]$SkipClient,
    [switch]$SkipServer,
    [switch]$LeaveStopped
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ClientDeploymentStorage.ps1')
# Connect-GlobalKeys.ps1 records the Global API Keys distro; an update of the same dedicated installation keeps it.
function Get-ReignPreservedGlobalKeysDistro($PreviousRecord, [string]$Distro) {
    if ($PreviousRecord -and $PreviousRecord.schema -ceq 'reign-installation-v1' -and $PreviousRecord.serverMode -ceq 'dwemerdistro-wsl' -and
        $PreviousRecord.wslDistro -ceq $Distro -and $PreviousRecord.PSObject.Properties['globalKeysDistro'] -and
        $PreviousRecord.globalKeysDistro -ceq 'DwemerAI4Skyrim3') { return 'DwemerAI4Skyrim3' }
    return $null
}
if ($SkipClient -and $SkipServer) { throw 'Select at least one component.' }
if ($Distro -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') { throw 'Invalid WSL distribution name.' }
$workspaceRoot = (Resolve-Path -LiteralPath $Workspace).Path
Import-Module (Join-Path $PSScriptRoot 'ReignDeploymentAuthority.psm1') -Force
$selectedClientReport = if (-not $SkipClient) { $ValidationReport } else { '' }
$selectedLinuxReport = if (-not $SkipServer) { $LinuxBuildReport } else { '' }
# Fail before touching the installed module, private data, controls or runtime.
$deploymentAuthority = Assert-ReignDeploymentAuthority -Workspace $workspaceRoot `
    -ServerRoot (Join-Path $PSScriptRoot '..') -ValidationReport $selectedClientReport -LinuxBuildReport $selectedLinuxReport
$approvedClientSources = Get-ReignClientSourceHashes $deploymentAuthority
$release = Get-Content (Join-Path $PSScriptRoot '../release/release.json') -Raw | ConvertFrom-Json
$recordPath = Join-Path $env:USERPROFILE '.reign\installation.json'

# Keep argument boundaries intact: every WSL call names the selected distro and bypasses a Linux shell.
function ConvertTo-WslPath([string]$Path) {
    $mapped = (@(& wsl.exe -d $Distro -u root --exec /usr/bin/wslpath -u ([IO.Path]::GetFullPath($Path).Replace('\','/'))) -join '').Trim()
    if ($LASTEXITCODE -ne 0 -or -not $mapped.StartsWith('/mnt/')) { throw "Could not map a deployment path into $Distro; nothing was changed." }
    return $mapped
}

# Python programs travel on stdin, so they never depend on Windows argument quoting.
function Invoke-ReignDistroPython([string]$User, [string]$Script, [string[]]$Arguments, [string]$Failure) {
    $output = $Script.Replace("`r`n", "`n") | & wsl.exe -d $Distro -u $User --exec /usr/bin/python3 -I - @Arguments
    if ($LASTEXITCODE -ne 0) { throw $Failure }
    return (@($output) -join "`n").Trim()
}

# Reviewed shell programs also travel on stdin, so a CRLF checkout (core.autocrlf)
# runs unchanged. Each argument stays its own argument; the caller reads $LASTEXITCODE.
function Invoke-ReignDistroShell([string]$Program, [string[]]$Arguments) {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $Program | & wsl.exe -d $Distro -u root --exec /bin/sh -s -- @Arguments
}

# LF-normalize once, before activation. The brace group makes sh parse the whole
# program before running any of it, so no child command can read its remaining text.
function Get-ReignDistroShellProgram([string]$Path) {
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)).Replace("`r`n", "`n")
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    return "{`n$text}`n"
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

# The dedicated distro must have exactly one registration and its own virtual disk.
function Assert-ReignDedicatedDistro {
    $base = { param($Path) $value = [string]$Path; if ($value.StartsWith('\\?\')) { $value = $value.Substring(4) }; [IO.Path]::GetFullPath($value).TrimEnd('\') }
    $registrations = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' | ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } |
        Where-Object { $_.PSObject.Properties['DistributionName'] -and $_.PSObject.Properties['BasePath'] -and $_.BasePath })
    $selected = @($registrations | Where-Object { $_.DistributionName -ieq $Distro })
    if ($selected.Count -ne 1 -or $selected[0].DistributionName -cne $Distro -or
        -not (Test-Path -LiteralPath (Join-Path (& $base $selected[0].BasePath) 'ext4.vhdx') -PathType Leaf)) {
        throw "The dedicated $Distro WSL registration or its own virtual disk is missing; nothing was changed."
    }
    $storage = & $base $selected[0].BasePath
    if (@($registrations | Where-Object { $_.DistributionName -cne $Distro -and (& $base $_.BasePath) -ieq $storage }).Count) {
        throw "$Distro shares WSL storage with another distro; nothing was changed."
    }
}

# Read-only root probe of the selected distro: ownership, prior service state and listeners.
# Client-only mode also hashes the installed runtime, since that runtime becomes the client's server.
$preflightProbe = @'
import hashlib, json, os, pwd, re, stat, subprocess, sys
mode, expected = sys.argv[1], sys.argv[2]
base = '/var/www/html/ReignServer'; data = base + '/data'; runtime = base + '/runtime'; current = runtime + '/current'
problems = []
def digest(path):
    with open(path, 'rb') as handle:
        return hashlib.file_digest(handle, 'sha256').hexdigest()
def listeners(port):
    rows = subprocess.run(['ss', '-ltnpH', 'sport = :%d' % port], capture_output=True, text=True, check=True).stdout.splitlines()
    return [pid for row in rows if row.strip() for pid in ([int(p) for p in re.findall(r'pid=(\d+)', row)] or [0])]
def session(pid):
    try: return os.getsid(pid)
    except OSError: return -1
def runtime_process(pid):
    try: return any(os.path.isfile(current + '/' + name) and os.path.samefile('/proc/%d/exe' % pid, current + '/' + name) for name in ('ReignServer', 'ReignBetaServer'))
    except OSError: return False
if open('/proc/1/comm').read().strip() != 'systemd':
    print(json.dumps({'problems': ['The selected distro is not running systemd.']})); sys.exit()
try: owner = pwd.getpwnam('reign').pw_uid
except KeyError: owner = None; problems.append('The Reign service account is missing; prepare the dedicated server first.')
if os.path.lexists(base + '/tester-owner.json'):
    problems.append('This distro belongs to a packaged tester installation; it was not adopted.')
# A legacy symlinked current is converted by activation; client-only mode refuses it below.
for path in (base, data, runtime):
    if os.path.islink(path): problems.append('Redirected Reign roots are not supported: ' + path)
if not os.path.isdir(data) or (owner is not None and os.lstat(data).st_uid != owner):
    problems.append('ReignServer data is missing or not owned by the service account; prepare the server first.')
else:
    bridge = data + '/windows-bridge.json'
    if os.path.lexists(bridge):
        info = os.lstat(bridge)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != owner or info.st_mode & 0o077 or info.st_nlink != 1:
            problems.append('windows-bridge.json has an unexpected type, owner, mode or link count; it was preserved.')
    if any(name.startswith('windows-bridge.json.') for name in os.listdir(data)):
        problems.append('An interrupted deployment left Windows bridge staging for review.')
for leftover in ('current.next', 'current.previous', 'current.aborted', 'activation-pending'):
    if os.path.lexists(runtime + '/' + leftover): problems.append('A previous Reign activation needs review: ' + leftover)
version = open(runtime + '/current-version').read().strip() if os.path.isfile(runtime + '/current-version') else ''
if version and not re.fullmatch(r'[0-9]{14}-[0-9]+', version): problems.append('The active Reign version marker is invalid.')
installed = ''
if mode == 'client':
    if not version or os.path.islink(current) or not os.path.isdir(current):
        problems.append('Client-only deployment needs an installed, Windows-accessible ReignServer runtime.')
    else:
        raw = open(current + '/reign-linux-artifact.json', 'rb').read()
        installed = hashlib.sha256(raw).hexdigest()
        retained = runtime + '/versions/' + version + '/reign-linux-artifact.json'
        if not os.path.isfile(retained) or open(retained, 'rb').read() != raw:
            problems.append('The installed runtime differs from its retained version.')
        if expected != '-' and installed != expected:
            problems.append('The installed runtime is not the approved Linux artifact of this checkpoint.')
        files = json.loads(raw).get('files') or {}
        actual = set()
        for folder, folders, names in os.walk(current):
            for name in folders + names:
                if os.path.islink(os.path.join(folder, name)): problems.append('The installed runtime contains a link.')
            actual.update(os.path.relpath(os.path.join(folder, name), current) for name in names)
        actual.discard('reign-linux-artifact.json')
        if actual != set(files) or any(digest(current + '/' + name) != value for name, value in files.items()):
            problems.append('Installed ReignServer files differ from their manifest.')
show = subprocess.run(['systemctl', 'show', 'reignserver.service', '--property=ActiveState', '--property=MainPID'], capture_output=True, text=True, check=True).stdout
fields = dict(line.split('=', 1) for line in show.splitlines() if '=' in line)
active, main = fields.get('ActiveState', ''), int(fields.get('MainPID') or 0)
try: recorded = int(open('/run/reignserver/server.pid').read().strip())
except (OSError, ValueError): recorded = 0
state = 'unknown'
if active == 'active':
    if main > 1 and recorded == main and runtime_process(main): state = 'running'
    else: problems.append('The managed Reign service process could not be verified.')
elif active in ('inactive', 'failed') and main == 0:
    if recorded > 1 and runtime_process(recorded): problems.append('A Reign process is running outside the managed service. Use the ReignServer launcher recovery first.')
    else: state = 'stopped'
else: problems.append('The managed Reign service is ' + active + '; retry after it settles.')
for port in (5101, 5102):
    for pid in listeners(port):
        if state != 'running' or pid <= 0 or session(pid) != main:
            problems.append('Port %d is used by a process outside the managed Reign service; it was left running.' % port)
for pid in listeners(8089):
    try: name = open('/proc/%d/comm' % pid).read().strip() if pid > 0 else ''
    except OSError: name = ''
    if name != 'apache2': problems.append('Port 8089 is used by a process other than the distro web proxy; it was left running.')
print(json.dumps({'problems': sorted(set(problems)), 'state': state, 'version': version, 'manifestSha256': installed}))
'@

function Assert-ReignDistroReady([string]$Mode, [string]$ExpectedManifest) {
    $text = Invoke-ReignDistroPython root $preflightProbe @($Mode, $ExpectedManifest) "The $Distro preflight could not inspect the installation; nothing was changed."
    $probe = $text | ConvertFrom-Json
    if (@($probe.problems).Count) { throw ("$Distro preflight failed; nothing was changed. " + (@($probe.problems) -join ' ')) }
    return $probe
}

# Bridge updates run as the owning account. The prior file is kept as a hard
# link, so restore returns its exact bytes, owner and mode; unrelated keys stay.
$bridgeProgram = @'
import json, os, re, stat, sys
action, tid = sys.argv[1], sys.argv[2]
if not re.fullmatch(r'[A-Za-z0-9-]{8,96}', tid): raise SystemExit('Invalid deployment transaction.')
folder = '/var/www/html/ReignServer/data'; path = folder + '/windows-bridge.json'
backup = path + '.before-' + tid; staged = path + '.' + tid + '.next'
if action == 'begin':
    if any(name.startswith('windows-bridge.json.') for name in os.listdir(folder)):
        raise SystemExit('An interrupted deployment left Windows bridge staging for review.')
    try: info = os.lstat(path)
    except FileNotFoundError: info = None
    if info:
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077 or info.st_nlink != 1:
            raise SystemExit('windows-bridge.json has an unexpected type, owner, mode or link count; it was preserved.')
        with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW), 'rb') as handle: value = json.loads(handle.read())
        if not isinstance(value, dict): raise SystemExit('windows-bridge.json is not an object; it was preserved.')
    else: value = {'schema': 'reign-windows-bridge-v1'}
    for pair in sys.argv[3:]:
        key, item = pair.split('=', 1)
        if key not in ('nativeGenerator', 'nativeSaveRoot', 'bannerlordModules') or not item.startswith('/mnt/'):
            raise SystemExit('Invalid Windows bridge value.')
        value[key] = item
    mode = stat.S_IMODE(info.st_mode) if info else 0o600
    created = []  # Only names this call created are ever removed; a foreign one fails the exclusive create.
    try:
        if info: os.link(path, backup); created.append(backup)
        descriptor = os.open(staged, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
        created.append(staged)
        os.fchmod(descriptor, mode)
        written = os.fstat(descriptor).st_ino
        with os.fdopen(descriptor, 'w') as handle:
            json.dump(value, handle); handle.flush(); os.fsync(handle.fileno())
        os.replace(staged, path)
    except BaseException:
        for leftover in created:
            if os.path.lexists(leftover): os.unlink(leftover)
        raise
    print(written)
elif action == 'restore':
    # Undo only this deployment's own write. If another writer replaced or removed
    # the record, both it and the prior backup stay for review.
    written = int(sys.argv[3])
    try: info = os.lstat(path)
    except FileNotFoundError: info = None
    if not info or not stat.S_ISREG(info.st_mode) or info.st_ino != written:
        raise SystemExit('windows-bridge.json changed during deployment; it and windows-bridge.json.before-' + tid + ' (if any) were preserved.')
    if os.path.lexists(backup): os.replace(backup, path)
    else: os.unlink(path)
elif action == 'commit':
    if os.path.lexists(backup): os.unlink(backup)
else: raise SystemExit('Unsupported bridge action.')
'@

# Read-only identity of the activated runtime: retained ID, approved manifest
# bytes in both current and the retained copy, and the exact file inventory.
# reignctl hashed every staged file before the atomic swap.
$runtimeIdentity = @'
import hashlib, json, os, sys
runtime = '/var/www/html/ReignServer/runtime'; version, approved = sys.argv[1], sys.argv[2]; current = runtime + '/current'
def digest(path): return hashlib.sha256(open(path, 'rb').read()).hexdigest()
if open(runtime + '/current-version').read().strip() != version: raise SystemExit('The active Reign version is not the activated version.')
if os.path.islink(current) or not os.path.isdir(current): raise SystemExit('The active Reign runtime is missing or redirected.')
for manifest in (current + '/reign-linux-artifact.json', runtime + '/versions/' + version + '/reign-linux-artifact.json'):
    if digest(manifest) != approved: raise SystemExit('The installed runtime manifest is not the approved Linux artifact.')
actual = set()
for folder, folders, names in os.walk(current):
    if any(os.path.islink(os.path.join(folder, name)) for name in folders + names): raise SystemExit('The installed runtime contains a link.')
    actual.update(os.path.relpath(os.path.join(folder, name), current) for name in names)
actual.discard('reign-linux-artifact.json')
if actual != set(json.load(open(current + '/reign-linux-artifact.json'))['files']): raise SystemExit('The installed runtime inventory differs from the approved manifest.')
if not os.path.isfile(runtime + '/activation-pending'): raise SystemExit('The activation is not awaiting this deployment decision.')
print('verified')
'@

# Direct loopback GET on Windows PowerShell 5.1 and PowerShell 7 alike: no
# proxy, no redirect (a 3xx is a failure), bounded time, body never printed.
function Get-ReignLoopbackJson([string]$Uri) {
    $request = [Net.WebRequest]::Create($Uri)
    $request.Proxy = $null
    $request.AllowAutoRedirect = $false
    $request.Timeout = 5000
    $request.ReadWriteTimeout = 5000
    $response = $request.GetResponse()
    try {
        if ([int]$response.StatusCode -ne 200) { throw "$Uri returned HTTP $([int]$response.StatusCode)." }
        $reader = [IO.StreamReader]::new($response.GetResponseStream())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    } finally { $response.Dispose() }
}

# Public origin: Windows -> 8089 -> Apache -> the exact managed process reignctl started.
function Assert-ReignPublicHealth($Receipt) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($true) {
        try {
            $health = Get-ReignLoopbackJson 'http://127.0.0.1:8089/health'
            $control = Get-ReignLoopbackJson 'http://127.0.0.1:8089/reign-control/status'
            $schema = $health.databaseSchemaVersion
            if ($health.ok -eq $true -and $health.service -ceq 'BannerlordReignServer' -and $health.protocolVersion -eq 1 -and
                $health.processId -eq $Receipt.mainPid -and $health.databaseName -ceq 'reign' -and $schema -gt 0 -and
                $schema -eq $health.requiredDatabaseSchemaVersion -and $health.databaseSchemaUpToDate -eq $true -and
                $health.serverVersion -eq $release.version -and $control.ok -eq $true -and $control.state -ceq 'on') { return $health }
            $observed = 'health or managed state did not identify the activated process'
        } catch { $observed = $_.Exception.Message }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Public ReignServer health through port 8089 did not match the activated managed process: $observed" }
        Start-Sleep -Milliseconds 500
    }
}

# Byte identity of a small record; $null means absent. (Convert.ToHexString is unavailable on Windows PowerShell 5.1.)
function Test-ReignSameBytes([byte[]]$Left, [byte[]]$Right) {
    if ($null -eq $Left -or $null -eq $Right) { return ($null -eq $Left) -and ($null -eq $Right) }
    return [BitConverter]::ToString($Left) -ceq [BitConverter]::ToString($Right)
}

function Publish-ReignInstallationRecord([string]$Path, [string]$Json, [string]$Id, [byte[]]$PriorBytes) {
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    $current = if (Test-Path -LiteralPath $Path) { [IO.File]::ReadAllBytes($Path) } else { $null }
    if (-not (Test-ReignSameBytes $current $PriorBytes)) {
        throw 'The installation record changed during deployment; it was preserved.'
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Json)
    $staged = "$Path.$Id.next"
    $backup = "$Path.before-$Id"
    # CreateNew fails on a foreign file of this name, which is then never removed.
    $stream = [IO.File]::Open($staged, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ($null -ne $current) {
            try { [IO.File]::Replace($staged, $Path, $backup) } catch {
                # An interrupted replace can move the record to the backup without installing the new one.
                if (-not (Test-Path -LiteralPath $Path) -and (Test-Path -LiteralPath $backup)) { [IO.File]::Move($backup, $Path) }
                throw
            }
            return [pscustomobject]@{ Backup = $backup; Bytes = $bytes }
        }
        [IO.File]::Move($staged, $Path)
        return [pscustomobject]@{ Backup = $null; Bytes = $bytes }
    } finally { if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force } }
}

# Undo only this deployment's own write. If another writer changed or removed
# the record, both it and the prior backup stay for review.
function Restore-ReignInstallationRecord([string]$Path, $Published) {
    $current = if (Test-Path -LiteralPath $Path -PathType Leaf) { [IO.File]::ReadAllBytes($Path) } else { $null }
    if (-not (Test-ReignSameBytes $current $Published.Bytes)) {
        $kept = if ($Published.Backup) { " and $($Published.Backup)" } else { '' }
        throw "the record changed during deployment; it$kept were preserved"
    }
    if ($Published.Backup) { [IO.File]::Replace($Published.Backup, $Path, $null); return }
    Remove-Item -LiteralPath $Path -Force
}

# Everything that selects the new halves happens here. Any failure restores, in
# order, the record, the Windows bridge and the server's prior state; the caller
# (Invoke-ReignClientActivation) then restores the module, and the deployment's
# outer handler the service controls and proxy. Nothing is committed.
function Invoke-ReignDeploymentTransaction {
    $state = [pscustomobject]@{ Bridge = $null; Server = $null; Record = $null; Health = $null }
    try {
        if ($bridgeValues.Count) {
            $inode = Invoke-ReignDistroPython reign $bridgeProgram (@('begin', $transactionId) + $bridgeValues) 'The Windows bridge record could not be staged; it was preserved.'
            $state.Bridge = [pscustomobject]@{ Inode = $inode }
        }
        if (-not $SkipServer) {
            if ((Get-ReignFileSha (Join-Path $linux.ArtifactRoot 'reign-linux-artifact.json')) -ne $deploymentAuthority.LinuxManifestSha256) { throw 'Approved Linux manifest changed before activation.' }
            # A reviewed deployment states its start policy; a bare reignctl call returns the unit to its prior state.
            [string[]]$activationOptions = if ($LeaveStopped) { @('--leave-stopped') } else { @('--start') }
            $output = @(& wsl.exe -d $Distro -u root --exec /usr/local/bin/reignctl activate $artifact @activationOptions --await-commit "--manifest-sha256=$approvedManifest")
            $activationStatus = $LASTEXITCODE
            $output | Where-Object { $_ -notlike '{*' } | ForEach-Object { Write-Host $_ }
            if ($activationStatus -ne 0) { throw 'Linux activation failed; reignctl restored the prior runtime state or reported the evidence it preserved.' }
            $receipt = $null
            try { $receipt = @($output | Where-Object { $_ -like '{*' } | Select-Object -Last 1) | ConvertFrom-Json } catch { }
            if (-not $receipt -or -not $receipt.version) {
                # Preflight proved no decision was pending before; under the startup mutex this one is ours.
                $pending = ((@(& wsl.exe -d $Distro -u root --exec /bin/cat /var/www/html/ReignServer/runtime/activation-pending) -join ' ').Trim() -split ' ')[0]
                if ($pending -match '^[0-9]{14}-[0-9]+$') { $state.Server = [pscustomobject]@{ version = $pending } }
                throw 'The Linux activation did not return a readable receipt.'
            }
            $state.Server = $receipt
            if ($receipt.schema -cne 'reign-activation-v1' -or $receipt.version -notmatch '^[0-9]{14}-[0-9]+$' -or
                $receipt.manifestSha256 -cne $approvedManifest -or $receipt.awaitingCommit -ne $true -or
                $receipt.state -cne $(if ($LeaveStopped) { 'stopped' } else { 'running' })) { throw 'The Linux activation receipt does not match this deployment.' }
            Invoke-ReignDistroPython root $runtimeIdentity @($receipt.version, $approvedManifest) 'Installed ReignServer files differ from the validated Linux artifact.' | Out-Null
            if ($LeaveStopped) {
                & wsl.exe -d $Distro -u root --exec /usr/bin/systemctl is-active --quiet reignserver.service
                if ($LASTEXITCODE -eq 0) { throw 'The deployment was requested to leave ReignServer stopped.' }
            } else {
                $state.Health = Assert-ReignPublicHealth $receipt
            }
        }
        if (-not $SkipClient) {
            $record = [ordered]@{
                schema='reign-installation-v1';version=$release.version;protocolVersion=$release.protocolVersion;contentVersion=$release.contentVersion
                serverMode='dwemerdistro-wsl';wslDistro=$Distro;serverRoot="\\wsl.localhost\$Distro\var\www\html\ReignServer\runtime\current"
                contentRoot=$module;dataRoot="\\wsl.localhost\$Distro\var\www\html\ReignServer\data"
                bannerlordRoot=$game;moduleRoot=$module;postgresBin='';postgresPort=5432;nativeGeneratorRoot=$helper
            }
            $globalKeysDistro = Get-ReignPreservedGlobalKeysDistro $previousRecord $Distro
            if ($globalKeysDistro) { $record.globalKeysDistro = $globalKeysDistro }
            $state.Record = Publish-ReignInstallationRecord $recordPath ($record | ConvertTo-Json) $transactionId $priorRecordBytes
        }
        return $state
    } catch {
        $failure = $_
        $incomplete = @()
        if ($state.Record) { try { Restore-ReignInstallationRecord $recordPath $state.Record } catch { $incomplete += "installation record ($($_.Exception.Message))" } }
        if ($state.Bridge) { try { Invoke-ReignDistroPython reign $bridgeProgram @('restore', $transactionId, $state.Bridge.Inode) 'restore failed' | Out-Null } catch { $incomplete += 'Windows bridge record (see the message above; windows-bridge.json and any backup were preserved)' } }
        if ($state.Server) {
            & wsl.exe -d $Distro -u root --exec /usr/local/bin/reignctl abort-activation $state.Server.version
            if ($LASTEXITCODE -ne 0) { $incomplete += "server runtime $($state.Server.version) (see reignctl output; the database was not restored)" }
        }
        if ($incomplete.Count) {
            throw "Deployment failed: $($failure.Exception.Message) Recovery did not complete for: $($incomplete -join '; '). Its evidence was preserved for review."
        }
        throw $failure
    }
}

$startupMutex = [Threading.Mutex]::new($false, 'Local\Reign.Server.Startup')
$ownsStartup = $false
$controlsHandle = $false
try {
    # The launcher uses the same mutex; its recovery must never run inside a deployment.
    try { $ownsStartup = $startupMutex.WaitOne(120000) } catch [Threading.AbandonedMutexException] { $ownsStartup = $true }
    if (-not $ownsStartup) { throw 'A Reign launcher or deployment is still running. Wait for it to finish and retry.' }

    # ---- Preflight: every check below is read-only. ----
    Assert-ReignDedicatedDistro
    $priorRecordBytes = if (Test-Path -LiteralPath $recordPath -PathType Leaf) { [IO.File]::ReadAllBytes($recordPath) } else { $null }
    $previousRecord = $null
    if ($null -ne $priorRecordBytes) {
        try { $previousRecord = [Text.UTF8Encoding]::new($false).GetString($priorRecordBytes) | ConvertFrom-Json } catch { throw 'The existing installation record is unreadable; it was preserved.' }
        if ($previousRecord.schema -cne 'reign-installation-v1' -or $previousRecord.serverMode -cne 'dwemerdistro-wsl') {
            throw 'A standalone installation exists. Preserve and migrate its data before replacing the client installation record.'
        }
        if ($previousRecord.wslDistro -cne $Distro) { throw 'The existing installation belongs to another distro.' }
        if ($previousRecord.PSObject.Properties['testerOwnerId']) { throw 'The existing installation is owned by the packaged tester installer; it was preserved.' }
    }
    $bridgeValues = [Collections.Generic.List[string]]::new()
    if (-not $SkipServer) {
        $linux = Get-Content -LiteralPath $LinuxBuildReport -Raw | ConvertFrom-Json
        if (-not $linux.Ok -or $linux.Schema -ne 'reign-linux-build-v1') { throw 'Server deployment requires a successful canonical Linux component report.' }
        & (Join-Path $PSScriptRoot 'Test-ReignServerContent.ps1') -SourceRoot (Resolve-Path (Join-Path $PSScriptRoot '..')).Path -ArtifactRoot $linux.ArtifactRoot
        $approvedManifest = ([string]$deploymentAuthority.LinuxManifestSha256).ToLowerInvariant()
        if ((Get-ReignFileSha (Join-Path $linux.ArtifactRoot 'reign-linux-artifact.json')) -ne $approvedManifest) { throw 'Approved Linux manifest changed before activation.' }
        if (-not $BannerlordPath) { throw 'Server deployment needs the licensed Bannerlord directory (-BannerlordPath or REIGN_BANNERLORD_PATH).' }
        $gameModules = Join-Path (Resolve-Path -LiteralPath $BannerlordPath).Path 'Modules'
        if (-not (Test-Path -LiteralPath (Join-Path $gameModules 'SandBox\ModuleData') -PathType Container)) {
            throw 'The selected Bannerlord modules directory is incomplete.'
        }
        $artifact = ConvertTo-WslPath $linux.ArtifactRoot
        $control = ConvertTo-WslPath (Join-Path $PSScriptRoot 'reignctl')
        $versions = ConvertTo-WslPath (Join-Path $PSScriptRoot 'reign_versions.py')
        $service = ConvertTo-WslPath (Join-Path $PSScriptRoot 'reignserver.service')
        $installer = ConvertTo-WslPath (Join-Path $PSScriptRoot 'install-reign-control.sh')
        $installerProgram = Get-ReignDistroShellProgram (Join-Path $PSScriptRoot 'install-reign-control.sh')
        $bridgeValues.Add('bannerlordModules=' + (ConvertTo-WslPath $gameModules))
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
        if ($previousRecord -and $previousRecord.moduleRoot -and -not (Test-ReignSamePath $previousRecord.moduleRoot $module)) {
            throw 'The existing installation record selects another Bannerlord module; it was preserved.'
        }
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
        $stage = Join-Path $deploymentRoot "stage-$deploymentId"
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
        $bridgeValues.Add('nativeSaveRoot=' + (ConvertTo-WslPath (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Game Saves')))
    }
    $transactionId = if (-not $SkipClient) { $deploymentId } else { "server-$([Guid]::NewGuid().ToString('N'))" }
    $probeMode = if ($SkipServer) { 'client' } elseif ($SkipClient) { 'server' } else { 'combined' }
    # Compatibility: checkpoints without a Linux manifest binding fall back to the runtime's own consistency.
    $clientRuntimeManifest = if ($SkipServer -and $deploymentAuthority.PSObject.Properties['LinuxManifestSha256'] -and $deploymentAuthority.LinuxManifestSha256) {
        ([string]$deploymentAuthority.LinuxManifestSha256).ToLowerInvariant() } else { '-' }
    Assert-ReignDistroReady $probeMode $clientRuntimeManifest | Out-Null

    # ---- Stage the client and install recoverable server controls. The server
    # artifact itself is copied and verified by the activation inside the transaction. ----
    if (-not $SkipClient) {
        New-Item -ItemType Directory -Path $deploymentRoot -Force | Out-Null
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
        & (Join-Path $PSScriptRoot 'Test-ReignClientContent.ps1') -Workspace $workspaceRoot -StagedModule $stage -ApprovedSourceHashes $approvedClientSources
        if (Test-Path -LiteralPath $helper) { throw 'The helper deployment path already exists.' }
        New-Item -ItemType Directory -Path (Split-Path $helper) -Force | Out-Null
        Copy-Item -LiteralPath $native -Destination $helper -Recurse
        Assert-ReignStagedClient $deploymentAuthority $stage $helper
        $bridgeValues.Add('nativeGenerator=' + (ConvertTo-WslPath (Join-Path $helper 'Bannerlord.NativeCharacterImageGenerator.App.exe')))
    }
    if (-not $SkipServer) {
        # Until commit, the installer's handle keeps the exact prior controls, Apache
        # configuration and PostgreSQL/Apache states; it restores them itself if this step fails.
        Invoke-ReignDistroShell $installerProgram @('--begin', $transactionId, $control, $versions, $service)
        if ($LASTEXITCODE -ne 0) { throw 'Could not install the dedicated ReignServer service controls or start PostgreSQL and Apache; the installer restored their prior state or reported the evidence it retained. No runtime or client was selected.' }
        $controlsHandle = $true
        # Recheck ownership and listeners after the controls and proxy changed.
        Assert-ReignDistroReady $probeMode '-' | Out-Null
    }

    # ---- Select: one recovery-aware transaction. ----
    $script:transaction = $null
    if (-not $SkipClient) {
        # Directory.Move is atomic on this volume. PowerShell Move-Item can move
        # children one by one when a live game has a file open, leaving portraits
        # absent even though the module folder still exists.
        Assert-BannerlordClosed $game
        if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $legacyArenaBackup)) { throw 'Deployment backup path already exists.' }
        $activation = Invoke-ReignClientActivation -Module $module -Stage $stage -Backup $backup -LegacyArena $legacyArena -LegacyArenaBackup $legacyArenaBackup -DeploymentRoot $deploymentRoot -DeploymentId $deploymentId -Verify {
            param($activatedModule)
            & (Join-Path $PSScriptRoot 'Test-ReignClientContent.ps1') -Workspace $workspaceRoot -StagedModule $activatedModule -ApprovedSourceHashes $approvedClientSources
            $script:transaction = Invoke-ReignDeploymentTransaction
        }
    } else {
        $script:transaction = Invoke-ReignDeploymentTransaction
    }

    # ---- Commit: only after every selected half verified. Backups go last. ----
    if ($controlsHandle) {
        $controlsHandle = $false
        Invoke-ReignDistroShell $installerProgram @('--commit', $transactionId)
        if ($LASTEXITCODE -ne 0) { Write-Warning "ReignServer is deployed and verified, but control recovery handle /var/lib/reign-control/$transactionId was not released. Run: sed 's/\r`$//' `"$installer`" | sudo sh -s -- --commit $transactionId inside $Distro before the next deployment." }
    }
    if ($script:transaction.Server) {
        & wsl.exe -d $Distro -u root --exec /usr/local/bin/reignctl commit-activation $script:transaction.Server.version
        if ($LASTEXITCODE -ne 0) { Write-Warning "ReignServer is deployed and verified, but its prior runtime backup was not released. Run 'sudo reignctl commit-activation $($script:transaction.Server.version)' inside $Distro before the next deployment." }
    }
    if ($script:transaction.Bridge) {
        try { Invoke-ReignDistroPython reign $bridgeProgram @('commit', $transactionId) 'commit failed' | Out-Null }
        catch { Write-Warning "Deployment verified, but the prior Windows bridge backup windows-bridge.json.before-$transactionId remains for review." }
    }
    if (-not $SkipClient) {
        # Retention starts only after the installed client, bridge and server are verified.
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
        if ($LeaveStopped) {
            Write-Output "ReignServer $($release.version) artifacts verified inside $Distro as runtime $($script:transaction.Server.version); service left stopped. Live health and lifecycle acceptance deferred."
        } else {
            Write-Output "ReignServer $($script:transaction.Health.serverVersion) runtime $($script:transaction.Server.version) is healthy inside $Distro through port 8089 (managed process $($script:transaction.Server.mainPid))."
        }
    }
    Write-Output 'No game was started. In-game validation remains manual.'
} catch {
    $failure = $_
    if ($controlsHandle) {
        # Runs after the transaction restored the record, bridge and runtime and the
        # module was restored: return the exact prior controls, proxy configuration
        # and support service states, without reapplying any control.
        Invoke-ReignDistroShell $installerProgram @('--restore', $transactionId)
        if ($LASTEXITCODE -ne 0) { throw "$($failure.Exception.Message) The prior service controls, proxy configuration and support service states were not fully restored (see the installer output); recovery evidence /var/lib/reign-control/$transactionId was retained for review." }
    }
    throw $failure
} finally {
    if ($ownsStartup) { $startupMutex.ReleaseMutex() }
    $startupMutex.Dispose()
}
