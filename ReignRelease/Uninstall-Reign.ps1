[CmdletBinding()]
param([string]$InstallationFile = (Join-Path $env:LOCALAPPDATA 'Bannerlord Reign\installation.json'))
. (Join-Path $PSScriptRoot 'Reign-Installer.ps1')
try {
    Assert-ReignStopped
    if (-not [IO.File]::Exists($InstallationFile)) { exit 0 }
    $recordPath = Assert-ReignLocalPath $InstallationFile
    $record = Read-ReignJson $recordPath
    $receipt = Read-ReignJson (Join-Path $record.dataRoot 'installed-package.json')
    if ($record.schema -ne 'reign-installation-v1' -or $receipt.schema -ne 'reign-installed-package-v1' -or $record.serverRoot -ne $receipt.serverRoot -or $record.moduleRoot -ne $receipt.moduleRoot) { throw 'Installation ownership cannot be verified.' }
    foreach ($group in @(@{root=$record.serverRoot;files=$receipt.serverFiles},@{root=$record.bannerlordRoot;files=$receipt.clientFiles})) {
        Remove-ReignShippedFiles $group.root $group.files ($group.root -eq $record.serverRoot)
    }
    # Dependency mods may now be used by other modules, so retain them. Database,
    # portraits, provider settings, Save Sync and previous-version recovery stay.
    [IO.File]::Move($recordPath, $recordPath + '.uninstalled-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
    Write-Host 'Reign was removed. Your Reign data, portraits, backups, dependency mods and recovery versions remain.'
    exit 0
} catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
