# Shared Windows PowerShell 5.1 installer operations. No action is taken when dot-sourced.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ReignJson([string]$Path) {
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Write-ReignJson([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $temporary = $Path + '.new-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
        # Windows PowerShell 5.1 converts an untyped $null string argument to
        # an empty path. File.Replace requires a real null for no backup file.
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
function Get-ReignHash([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Assert-ReignDatabasePath([string]$Path, [string]$Label) {
    # Pinned PostgreSQL uses narrow Windows paths even with a UTF-8 database.
    # Reject unsupported roots before copying payloads or creating state.
    if ($Path -match '[^\x20-\x7e]') {
        throw "$Label must use ASCII characters (English letters, numbers, spaces and punctuation) throughout its full path. The bundled database cannot use accented or other non-English folder names. Choose another local folder."
    }
}
function Assert-ReignLocalPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Length -lt 5) { throw 'Choose an absolute folder on a local drive.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $cursor = $full
    while ($cursor.Length -gt 3) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked installation folders are not supported: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($full))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -ne 'NTFS') { throw 'Reign requires a local NTFS drive to preserve portrait metadata and database permissions.' }
    return $full
}
function Test-ReignWithin([string]$Parent, [string]$Child) {
    $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or $Child.StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Get-ReignSafeTarget([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or $Relative.StartsWith('/') -or $Relative.Contains(':')) { throw 'Invalid payload path.' }
    foreach ($part in $Relative.Split('/')) {
        if ($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part -match '[<>"|?*\x00-\x1f]' -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe payload path.' }
    }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative.Replace('/', '\')))
    if (-not (Test-ReignWithin $Root $full) -or $full.Equals($Root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload escapes its destination.' }
    Assert-ReignLocalPath $full | Out-Null
    return $full
}
function Assert-ReignFile([string]$Path, $Entry) {
    if (-not [IO.File]::Exists($Path) -or (Get-Item -LiteralPath $Path).Length -ne [long]$Entry.bytes -or (Get-ReignHash $Path) -ne $Entry.sha256) { throw "Payload integrity check failed: $([IO.Path]::GetFileName($Path))" }
}
function Expand-ReignPayload([string]$Archive, [string]$Destination, $Payload) {
    Assert-ReignFile $Archive $Payload
    if (Test-Path -LiteralPath $Destination) { throw 'Payload staging must be a new folder.' }
    Assert-ReignLocalPath $Destination | Out-Null
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $indexEntry = $zip.GetEntry('payload.json')
        if ($null -eq $indexEntry -or $indexEntry.Length -gt 32MB) { throw 'Payload inventory is missing or too large.' }
        $reader = New-Object IO.StreamReader($indexEntry.Open())
        try { $index = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($index.schema -ne 'reign-payload-v1' -or $index.id -ne $Payload.id -or $index.version -ne $Payload.version) { throw 'Payload identity does not match setup.' }
        $expected = @{}
        [long]$expanded = 0
        foreach ($entry in $index.files) {
            Get-ReignSafeTarget $Destination $entry.path | Out-Null
            if ($expected.ContainsKey($entry.path) -or $entry.path -eq 'payload.json' -or $entry.sha256 -notmatch '^[a-f0-9]{64}$' -or [long]$entry.bytes -lt 0) { throw 'Invalid or duplicate payload inventory entry.' }
            $expected[$entry.path] = $entry
            $expanded += [long]$entry.bytes
        }
        if ($expanded -ne [long]$Payload.expandedBytes -or $zip.Entries.Count -ne $expected.Count + 1) { throw 'Payload size or file count does not match setup.' }
        $seen = @{}
        foreach ($item in $zip.Entries) {
            if ($item.FullName -ceq 'payload.json') { continue }
            if (-not $expected.ContainsKey($item.FullName) -or $seen.ContainsKey($item.FullName) -or (($item.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unlisted, duplicated or linked payload entry.' }
            $entry = $expected[$item.FullName]
            if ($item.Length -ne [long]$entry.bytes) { throw 'Expanded payload length mismatch.' }
            $target = Get-ReignSafeTarget $Destination $entry.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $inputStream = $item.Open()
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
            Assert-ReignFile $target $entry
            # NTFS ticks are authoritative for the portrait derivative cache.
            [IO.File]::SetLastWriteTimeUtc($target, (New-Object DateTime([long]$entry.lastWriteUtcTicks, [DateTimeKind]::Utc)))
            $seen[$item.FullName] = $true
        }
        if ($seen.Count -ne $expected.Count) { throw 'Payload extraction was incomplete.' }
        return $index
    } finally { $zip.Dispose() }
}
function Get-ReignPayload([string]$Folder, $Payload, [string]$SourcesFile) {
    if ($Payload.file -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.zip$') { throw 'Invalid payload archive name.' }
    $target = Join-Path $Folder $Payload.file
    if (-not [IO.File]::Exists($target)) {
        if ([string]::IsNullOrWhiteSpace($SourcesFile) -or -not [IO.File]::Exists($SourcesFile)) { throw "Place $($Payload.file) beside setup, or provide your private download-sources.json file." }
        $sources = Read-ReignJson $SourcesFile
        $property = $sources.PSObject.Properties[$Payload.id]
        if ($null -eq $property) { throw "The download file has no location for $($Payload.id)." }
        $uri = [uri]$property.Value
        if ($uri.Scheme -ne 'https') { throw 'Payload downloads require HTTPS.' }
        $temporary = $target + '.download-' + [guid]::NewGuid().ToString('N')
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $client = New-Object Net.WebClient
        try {
            try { $client.DownloadFile($uri, $temporary) } catch { throw "Download failed for $($Payload.id). Check the private link and connection." }
            Assert-ReignFile $temporary $Payload
            [IO.File]::Move($temporary, $target)
        } finally {
            $client.Dispose()
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    }
    Assert-ReignFile $target $Payload
    return $target
}
function Get-ReignDownloadBytes([string]$Folder, $Payloads) {
    [long]$bytes = 0
    foreach ($payload in $Payloads) {
        if (-not [IO.File]::Exists((Join-Path $Folder $payload.file))) { $bytes += [long]$payload.bytes }
    }
    return $bytes
}
function Assert-ReignSpace($Requirements) {
    $drives = @{}
    foreach ($requirement in $Requirements) {
        $drive = [IO.Path]::GetPathRoot($requirement.path)
        if (-not $drives.ContainsKey($drive)) { $drives[$drive] = [long]0 }
        $drives[$drive] += [long]$requirement.bytes
    }
    foreach ($key in $drives.Keys) {
        $disk = New-Object IO.DriveInfo($key)
        [long]$required = $drives[$key] + 2GB
        if ($disk.AvailableFreeSpace -lt $required) { throw ('Drive {0} needs {1:N1} GB free for setup and recovery.' -f $key, ($required / 1GB)) }
    }
}
function Assert-ReignStopped {
    $running = @(Get-Process -Name ReignBetaServer,Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) { throw 'Close Bannerlord and the visible ReignServer / Control Center before installing or repairing.' }
}
function Test-ReignShippedFile([string]$Path, $Entry) {
    [IO.File]::Exists($Path) -and (Get-ReignHash $Path) -eq $Entry.sha256
}

function Remove-ReignShippedFiles([string]$Root, $Files, [bool]$RemoveEmptyRoot) {
    $root = Assert-ReignLocalPath $Root
    $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Files) {
        $path = Get-ReignSafeTarget $root $entry.path
        if (Test-ReignShippedFile $path $entry) { [IO.File]::Delete($path) }
        $parent = [IO.Path]::GetDirectoryName($path)
        while ((Test-ReignWithin $root $parent) -and $parent -ne $root) {
            $directories.Add($parent) | Out-Null
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    foreach ($directory in @($directories | Sort-Object Length -Descending)) {
        if ([IO.Directory]::Exists($directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory) }
    }
    if ($RemoveEmptyRoot -and [IO.Directory]::Exists($root) -and @(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) { [IO.Directory]::Delete($root) }
}
function Merge-ReignContent([string]$Incoming, [string]$Destination, $Index, $PreviousFiles) {
    $previous = @{}
    foreach ($item in $PreviousFiles) { $previous[$item.path] = $item }
    $preserved = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Index.files) {
        $target = Get-ReignSafeTarget $Destination $entry.path
        if ([IO.File]::Exists($target) -and -not (Test-ReignShippedFile $target $entry)) {
            if (-not $previous.ContainsKey($entry.path) -or -not (Test-ReignShippedFile $target $previous[$entry.path])) {
                $preserved.Add(($entry.path -split '/')[2]) | Out-Null
            }
        }
    }
    $installed = @()
    foreach ($entry in $Index.files) {
        if ($preserved.Contains(($entry.path -split '/')[2])) {
            if ($previous.ContainsKey($entry.path)) { $installed += $previous[$entry.path] }
            continue
        }
        $source = Get-ReignSafeTarget $Incoming $entry.path
        $target = Get-ReignSafeTarget $Destination $entry.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($source, $target, $true)
        [IO.File]::SetLastWriteTimeUtc($target, [IO.File]::GetLastWriteTimeUtc($source))
        $installed += $entry
    }
    [pscustomobject]@{ files = $installed; preservedPortraits = @($preserved) }
}
