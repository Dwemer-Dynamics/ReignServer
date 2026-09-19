[CmdletBinding()]
param([string]$ReignRoot = (Join-Path $PSScriptRoot '..\..\Reign'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$serverRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$clientRoot = [IO.Path]::GetFullPath($ReignRoot).TrimEnd('\')
if ((Split-Path $clientRoot -Leaf) -cne 'Reign' -or (Split-Path $serverRoot -Leaf) -cne 'ReignServer' -or
    (Split-Path $clientRoot -Parent) -ne (Split-Path $serverRoot -Parent)) {
    throw 'Use sibling local checkouts named exactly Reign and ReignServer.'
}
$expected = @(@($clientRoot, 'https://github.com/Dwemer-Dynamics/Reign.git'), @($serverRoot, 'https://github.com/Dwemer-Dynamics/ReignServer.git'))
foreach ($repository in $expected) {
    $origin = & git -C $repository[0] remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $origin -cne $repository[1]) { throw "Incorrect origin in $($repository[0])." }
    $pushUrl = & git -C $repository[0] remote get-url --push origin
    if ($LASTEXITCODE -ne 0 -or $pushUrl -cne $repository[1]) { throw "Incorrect push destination in $($repository[0])." }
}
$manifest = Get-Content -LiteralPath (Join-Path $clientRoot 'reign.repositories.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'reign-paired-repositories-v1' -or $manifest.mode -ne 'paired') { throw 'Paired repository manifest is missing.' }
$directories = @('ReignServer')
if (@(Compare-Object $directories $manifest.serverSourceDirectories).Count -ne 0) { throw 'Unexpected source projection in manifest.' }
foreach ($directory in $directories) {
    $target = $serverRoot
    $link = Join-Path $clientRoot $directory
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "Server source is missing: $directory" }
    if (Test-Path -LiteralPath $link) {
        $existing = Get-Item -LiteralPath $link -Force
        if (-not ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            [IO.Path]::GetFullPath([string]@($existing.Target)[0]).TrimEnd('\') -ne $target) {
            throw "Refusing to replace existing directory or different junction: $link"
        }
    } else {
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    }
}
[ordered]@{schema='reign-paired-local-workspace-v1'; Reign=$clientRoot; ReignServer=$serverRoot; projections=$directories; copiedSource=$false} | ConvertTo-Json
