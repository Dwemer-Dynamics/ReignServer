[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [string]$StagedModule
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path -LiteralPath $Workspace).Path
$sourceModule = Join-Path $workspaceRoot 'ReignBeta'
$shipped = @('GUI','ModuleData','EventArt','TavernArt','Videos','PortraitCache','TavernHousePortraits')
$authoring = @('artwork','docs','src','tools')
$known = @($shipped) + @($authoring)
$unknown = @(Get-ChildItem -LiteralPath $sourceModule -Directory | Where-Object { $_.Name -notin $known -and $_.Name -notin @('bin','obj','staging','server') })
if ($unknown.Count) { throw "Unclassified ReignBeta directories: $($unknown.Name -join ', ')" }
foreach ($directory in $shipped) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceModule $directory) -PathType Container)) {
        throw "Required shipped ReignBeta directory is missing: $directory"
    }
}

$cast = Get-Content -LiteralPath (Join-Path $sourceModule 'ModuleData/reign_tavern_cast.json') -Raw | ConvertFrom-Json
$castIds = @($cast.Towns | ForEach-Object { $_.People } | ForEach-Object { $_.Id })
if ($castIds.Count -ne 284 -or @($castIds | Sort-Object -Unique).Count -ne 284) { throw 'Authored tavern cast is incomplete.' }
$inventory = Get-Content -LiteralPath (Join-Path $sourceModule 'TavernHousePortraits/portrait-inventory.json') -Raw | ConvertFrom-Json
if ($inventory.schema -ne 'reign-tavern-portrait-inventory-v1' -or $inventory.root -ne 'TavernHousePortraits' -or
    $inventory.castCount -ne 284 -or @($inventory.files).Count -ne 1704) { throw 'Tavern portrait inventory is incomplete.' }
$names = @('.portrait_derivatives.json','portrait.png','portrait_chest.png','thumbnail.png','thumbnail_wide.png','zoom.png')
$expected = @($castIds | ForEach-Object { $id=$_; $names | ForEach-Object { "$id/$_" } } | Sort-Object)
$listed = @($inventory.files | ForEach-Object { $_.path } | Sort-Object)
if (@(Compare-Object $expected $listed).Count) { throw 'Tavern portrait inventory does not match the authored cast.' }
$pack = Join-Path $sourceModule 'TavernHousePortraits'
$actual = @(Get-ChildItem -LiteralPath $pack -File -Recurse | ForEach-Object {
    [IO.Path]::GetRelativePath($pack, $_.FullName).Replace('\','/')
} | Where-Object { $_ -ne 'portrait-inventory.json' } | Sort-Object)
if (@(Compare-Object $expected $actual).Count) { throw 'Tavern portrait pack has missing or extra files.' }
foreach ($entry in $inventory.files) {
    $file = Join-Path $pack ($entry.path.Replace('/','\'))
    if ((Get-Item -LiteralPath $file).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Tavern portrait differs from its inventory: $($entry.path)"
    }
}

$tracked = @(& git -C $workspaceRoot ls-files -- 'ReignBeta/GUI' 'ReignBeta/ModuleData' 'ReignBeta/EventArt' 'ReignBeta/TavernArt' 'ReignBeta/Videos' 'ReignBeta/PortraitCache' 'ReignBeta/TavernHousePortraits')
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect tracked shipped client content.' }
$trackedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in $tracked) { [void]$trackedSet.Add($path.Replace('\','/')) }
foreach ($directory in $shipped) {
    $source = Join-Path $sourceModule $directory
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = 'ReignBeta/' + [IO.Path]::GetRelativePath($sourceModule,$file.FullName).Replace('\','/')
        if (-not $trackedSet.Contains($relative)) { throw "Shipped client file is not tracked: $relative" }
    }
}
foreach ($path in $trackedSet) {
    $relative = $path.Substring('ReignBeta/'.Length).Replace('/','\')
    $source = Join-Path $sourceModule $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Tracked client content is missing: $path" }
    if ($StagedModule) {
        $target = Join-Path $StagedModule $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-Item -LiteralPath $target).Length -ne (Get-Item -LiteralPath $source).Length -or
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) {
            throw "Staged client content differs from source: $path"
        }
    }
}
if ($StagedModule) {
    $stageFiles = @(foreach ($directory in $shipped) {
        Get-ChildItem -LiteralPath (Join-Path $StagedModule $directory) -File -Recurse | ForEach-Object {
            'ReignBeta/' + [IO.Path]::GetRelativePath($StagedModule,$_.FullName).Replace('\','/')
        }
    })
    foreach ($path in $stageFiles) { if (-not $trackedSet.Contains($path)) { throw "Unexpected staged client content: $path" } }
}
Write-Output "Required client content verified: $($trackedSet.Count) tracked files; 284 tavern cast members."
