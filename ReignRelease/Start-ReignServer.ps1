[CmdletBinding()]
param([string]$InstallationFile = $env:REIGN_INSTALLATION_FILE)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallationFile)) { $InstallationFile = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.reign\installation.json' }
if (-not (Test-Path -LiteralPath $InstallationFile)) { throw 'Install ReignServer using the supplied setup package before starting Reign.' }
$record = Get-Content -LiteralPath $InstallationFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($record.schema -ne 'reign-installation-v1' -or $record.protocolVersion -ne 1) { throw 'Run the matching ReignServer setup to repair the installation record.' }
$server = Join-Path $record.serverRoot 'app\ReignBetaServer.exe'
if (-not (Test-Path -LiteralPath $server)) { throw 'ReignServer is incomplete. Run setup to repair it.' }
$env:REIGN_INSTALLATION_FILE = [IO.Path]::GetFullPath($InstallationFile)
$env:REIGN_DATA_ROOT = [IO.Path]::GetFullPath($record.dataRoot)
$env:REIGN_SAVE_SYNC_ROOT = Join-Path $env:REIGN_DATA_ROOT 'save-sync'
$env:REIGN_BANNERLORD_PATH = [IO.Path]::GetFullPath($record.bannerlordRoot)
$env:BANNERLORD_GAME_PATH = $env:REIGN_BANNERLORD_PATH
$env:REIGN_BANNERLORD_MODULES = Join-Path $env:REIGN_BANNERLORD_PATH 'Modules'
$env:REIGN_VECTOR_MODEL_DIR = Join-Path $record.serverRoot 'models\embeddings'
$env:HF_HUB_OFFLINE = '1'
$env:CODEX_HOME = Join-Path $env:REIGN_DATA_ROOT 'codex'
$env:PATH = (Join-Path $record.serverRoot 'runtime\codex\bin') + ';' + $env:PATH
$env:REIGN_VALIDATION_MODE = '0'
& $server --activate-only --port 5101 --terminal-window-name BannerlordReignServer --terminal-tab-index 0
if ($LASTEXITCODE -eq 0) { exit 0 }
& $server --port 5101 --terminal-window-name BannerlordReignServer --terminal-tab-index 0
exit $LASTEXITCODE
