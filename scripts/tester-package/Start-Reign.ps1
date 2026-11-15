[CmdletBinding()]
param([string]$InstallationFile=(Join-Path $env:USERPROFILE '.reign\installation.json'),[switch]$OpenControlCenter)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Start-ReignServer-Wsl.ps1') -LibraryOnly
$record=Get-Content -LiteralPath $InstallationFile -Raw -Encoding UTF8 | ConvertFrom-Json
if($record.schema -cne 'reign-installation-v1' -or $record.serverMode -cne 'dwemerdistro-wsl' -or $record.wslDistro -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$'){throw 'Invalid Reign installation record.'}
$distro=$record.wslDistro
$selected=@(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' | ForEach-Object {Get-ItemProperty $_.PSPath} | Where-Object {$_.DistributionName -ceq $distro})
if($selected.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $selected[0].BasePath 'ext4.vhdx'))){throw 'The selected dedicated Reign distro is missing.'}
$wsl="$env:SystemRoot\System32\wsl.exe"
Start-Process -FilePath $wsl -WindowStyle Hidden -ArgumentList @('-d',$distro,'-u','reign','--exec','/usr/bin/flock','-n','/var/www/html/ReignServer/data/launcher-keepalive.lock','/bin/sleep','infinity')
& $wsl -d $distro -u root --exec /usr/bin/systemctl start postgresql apache2
if($LASTEXITCODE -ne 0){throw 'Reign database or proxy could not start.'}
function Health {
    try {
        $h=Invoke-RestMethod http://127.0.0.1:8089/health -TimeoutSec 3
        $c=Invoke-RestMethod http://127.0.0.1:8089/reign-control/status -TimeoutSec 3
        $selectedPid=(& $wsl -d $distro -u root --exec /usr/bin/systemctl show reignserver.service -p MainPID --value | Out-String).Trim()
        if($LASTEXITCODE -eq 0 -and $selectedPid -match '^[1-9][0-9]*$' -and [string]$h.processId -ceq $selectedPid -and (Test-ReignManagedHealth -Health $h -Control $c -Version $record.version)){return $h}
    } catch {}
    return $null
}
Ensure-ReignServer -Health {Health} -Recover {
    & $wsl -d $distro -u root --exec /usr/bin/systemctl stop reignserver.service
    $recovery=(& $wsl -d $distro -u root --exec /usr/bin/wslpath -u (Join-Path $PSScriptRoot 'reign-recover.py').Replace('\','/')).Trim()
    if($LASTEXITCODE -ne 0 -or -not $recovery.StartsWith('/mnt/')){throw 'Reign recovery path unavailable.'}
    & $wsl -d $distro -u root --exec /usr/bin/python3 -B $recovery
    if($LASTEXITCODE -ne 0){throw 'Exact-owner Reign recovery failed.'}
    & $wsl -d $distro -u root --exec /usr/bin/systemctl reset-failed reignserver.service
} -StartServer {
    & $wsl -d $distro -u root --exec /usr/bin/systemctl start reignserver.service
    if($LASTEXITCODE -ne 0){throw 'Reign managed startup failed.'}
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    while(-not (Health)){if([DateTime]::UtcNow -ge $deadline){throw 'Reign did not become healthy.'};Start-Sleep -Milliseconds 500}
} -OpenBrowser {if($OpenControlCenter){Show-ReignControlCenter}} | Out-Null
Write-Output 'Reign is running. The Control Center manages server start and stop.'
