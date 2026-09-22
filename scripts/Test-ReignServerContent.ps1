[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$ArtifactRoot
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$artifact = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$manifestPath = Join-Path $artifact 'reign-linux-artifact.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'reign-linux-artifact-v1' -or -not $manifest.files) {
    throw 'The validated Linux artifact inventory is missing or unsupported.'
}
$manifestFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($property in $manifest.files.PSObject.Properties) { [void]$manifestFiles.Add($property.Name) }
$actualArtifact = @(Get-ChildItem -LiteralPath $artifact -File -Recurse | ForEach-Object {
    [IO.Path]::GetRelativePath($artifact,$_.FullName).Replace('\','/')
} | Where-Object { $_ -ne 'reign-linux-artifact.json' })
if (@(Compare-Object ($manifestFiles | Sort-Object) ($actualArtifact | Sort-Object)).Count) {
    throw 'The validated Linux artifact has missing or extra files.'
}
foreach ($relative in $manifestFiles) {
    $file = Join-Path $artifact ($relative.Replace('/','\'))
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $manifest.files.PSObject.Properties[$relative].Value) {
        throw "Linux artifact checksum mismatch: $relative"
    }
}

$mapped = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-ContentTree([string]$folder,[string]$targetFolder,[scriptblock]$include) {
    $path = Join-Path $source $folder
    foreach ($file in Get-ChildItem -LiteralPath $path -File -Recurse | Where-Object $include) {
        $relative = [IO.Path]::GetRelativePath($path,$file.FullName).Replace('\','/')
        $mapped["$targetFolder/$relative"] = $file.FullName
    }
}
Add-ContentTree 'ui' 'ui' { $true }
Add-ContentTree 'profiles' 'profiles' {
    $relative = [IO.Path]::GetRelativePath((Join-Path $source 'profiles'),$_.FullName).Replace('\','/')
    $relative -ne 'README.md' -and -not $relative.StartsWith('narrative/editorial/',[StringComparison]::Ordinal)
}
Add-ContentTree 'services/vector-worker' 'services/vector-worker' {
    $_.DirectoryName -eq (Join-Path $source 'services/vector-worker') -and
        ($_.Name -eq 'worker.py' -or $_.Name -like 'requirements*.txt')
}
foreach ($name in @('.version.txt','.version_number.txt','embedding-model.lock.json')) {
    $mapped[$name] = if ($name -eq 'embedding-model.lock.json') {
        Join-Path $source 'release/embedding-model.lock.json'
    } else { Join-Path $source $name }
}
foreach ($name in @('version-RFB-320.onnx','THIRD_PARTY_NOTICES.md')) {
    $mapped["portrait_models/$name"] = Join-Path $source "resources/portrait-models/$name"
}
$prompts = @(Get-ChildItem -LiteralPath (Join-Path $source 'prompts') -Filter '*.txt' -File -Recurse |
    ForEach-Object { [IO.Path]::GetRelativePath((Join-Path $source 'prompts'),$_.FullName).Replace('\','/') })
$allowedPromptFiles = @('world_tone.txt','noble_prompt.txt','commoner_prompt.txt','ambassador_role.txt')
$unclassifiedPrompts = @($prompts | Where-Object {
    $_ -notin $allowedPromptFiles -and $_ -notmatch '^(CastleChat|TavernHouse)/[^/]+\.txt$'
})
if ($unclassifiedPrompts.Count) { throw "Unclassified server prompts: $($unclassifiedPrompts -join ', ')" }

$tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in & git -C $source ls-files -- ui profiles services/vector-worker release/embedding-model.lock.json resources/portrait-models .version.txt .version_number.txt prompts) {
    [void]$tracked.Add($path.Replace('\','/'))
}
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect tracked server content.' }
foreach ($pair in $mapped.GetEnumerator()) {
    $relative = [IO.Path]::GetRelativePath($source,$pair.Value).Replace('\','/')
    if (-not $tracked.Contains($relative)) { throw "Required server source is not tracked: $relative" }
    if (-not $manifestFiles.Contains($pair.Key)) { throw "Required server content is missing from the artifact: $($pair.Key)" }
    $target = Join-Path $artifact ($pair.Key.Replace('/','\'))
    if ((Get-FileHash -LiteralPath $pair.Value -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw "Server source and artifact differ: $($pair.Key)"
    }
}
$reignCtl = Get-Content -LiteralPath (Join-Path $source 'scripts/reignctl') -Raw
foreach ($requiredLifecycleContract in @(
    'chown root:root "$pidfile"',
    'chmod 0644 "$pidfile"',
    'find -P "$root/versions/$version" -xdev -type d -exec chmod 0755 {} +',
    'find -P "$root/versions/$version" -xdev -type f -exec chmod 0644 {} +',
    'chmod 0755 "$root/versions/$version/$executable"'
)) {
    if (-not $reignCtl.Contains($requiredLifecycleContract,[StringComparison]::Ordinal)) {
        throw "The managed service PID file trust contract is missing: $requiredLifecycleContract"
    }
}
Write-Output "Required server content verified: $($mapped.Count) source files; $($manifestFiles.Count) artifact files; $($prompts.Count) classified embedded prompts."
