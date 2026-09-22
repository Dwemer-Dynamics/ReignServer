[CmdletBinding()]
param([switch]$OpenControlCenter)
$ErrorActionPreference = 'Stop'

$recordPath = Join-Path $env:USERPROFILE '.reign\installation.json'
if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw 'The Reign installation record is missing.' }
$record = Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($record.schema -ne 'reign-installation-v1' -or $record.protocolVersion -ne 1 -or
    $record.serverMode -ne 'dwemerdistro-wsl' -or $record.wslDistro -cne 'ReignServer') {
    throw 'The Reign installation record must select the dedicated ReignServer WSL distro.'
}

$registrations = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' | ForEach-Object { Get-ItemProperty $_.PSPath })
$selected = @($registrations | Where-Object { $_.DistributionName -ceq 'ReignServer' })
if ($selected.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $selected[0].BasePath 'ext4.vhdx') -PathType Leaf)) {
    throw 'The separate ReignServer WSL registration or its virtual disk is missing.'
}
if (@($registrations | Where-Object { $_.DistributionName -cne 'ReignServer' -and
    [IO.Path]::GetFullPath($_.BasePath) -eq [IO.Path]::GetFullPath($selected[0].BasePath) }).Count -ne 0) {
    throw 'ReignServer shares a WSL storage root with another distro.'
}

# WSL can power down a systemd distro after the final Windows WSL client exits.
# Keep one hidden session attached; flock makes repeated shortcut launches harmless.
$keepAlive = @('-d','ReignServer','-u','reign','--','/usr/bin/flock','-n',
    '/var/www/html/ReignServer/data/launcher-keepalive.lock','/bin/sleep','infinity')
Start-Process -FilePath "$env:SystemRoot\System32\wsl.exe" -ArgumentList $keepAlive -WindowStyle Hidden

& wsl.exe -d ReignServer -u root -- systemctl start postgresql apache2 reignserver
if ($LASTEXITCODE -ne 0) { throw 'The dedicated ReignServer services did not start.' }
$health = Invoke-RestMethod -Uri 'http://127.0.0.1:8089/health' -TimeoutSec 10
if (-not $health.ok -or $health.serverVersion -ne $record.version -or $health.databaseName -ne 'reign') {
    throw 'The dedicated ReignServer health response does not match the installed version and database.'
}
if ($OpenControlCenter) { Start-Process 'http://127.0.0.1:8089/' }
Write-Output "ReignServer $($health.serverVersion) is running in its own WSL distro."
