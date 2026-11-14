#Requires -Version 5.1
# One-time, explicit connection of the ReignServer distro to DwemerDistro Global API Keys. Two-phase: DwemerDistro
# stages a new consumer token beside any current one, Reign stores it, and only then is it committed. A failure discards
# the staged token and leaves the previous enrollment working. Nothing is retried. The token goes from the store distro's
# stdout straight into reignctl's stdin; it is never placed in argv, environment, files on Windows or logs.
param(
    [ValidateSet('DwemerAI4Skyrim3')][string]$GlobalKeysDistro = 'DwemerAI4Skyrim3',
    [ValidateSet('ReignServer')][string]$Distro = 'ReignServer'
)
$ErrorActionPreference = 'Stop'
# Every provider a Reign key slot can bind: the fixed slots plus fixed-origin LLM catalog providers a custom API URL may
# match exactly. A provider absent from the live catalog is simply never offered.
$providers = 'openrouter,nano_gpt,atlascloud,custom_openai,openai,google,groq'
$helper = '/usr/local/bin/ddistro_keys_api'
$lxss = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
foreach ($name in @($GlobalKeysDistro, $Distro)) {
    $registered = Get-ChildItem -LiteralPath $lxss -ErrorAction SilentlyContinue |
        Where-Object { (Get-ItemProperty -LiteralPath $_.PSPath).DistributionName -ceq $name }
    if (-not $registered) { throw "WSL distro '$name' is not registered." }
}
$owner = (& wsl.exe -d $GlobalKeysDistro -u root --exec /usr/bin/stat -c '%u %a' $helper | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $owner -notmatch '^0 7[0-5][0-5]$') { throw 'ddistro_keys_api is missing or not root-owned; update DwemerDistro first.' }

$registration = & wsl.exe -d $GlobalKeysDistro -u root --exec $helper register reign --providers $providers | Out-String
if ($LASTEXITCODE -ne 0) {
    # stage_pending: another connection is in progress, or one crashed. Its stage is never overwritten; once it is
    # 10 minutes old, drop it explicitly with: ddistro_keys_api discard reign --abandoned
    throw 'DwemerDistro refused the Reign registration (see the error above); nothing changed.'
}
$stored = $false
$stage = $null
try {
    $parsed = $registration | ConvertFrom-Json
    # Commit and discard name this run's stage, so a concurrent run can never commit or drop another run's token.
    if ([string]$parsed.stage -cmatch '^[0-9a-f]{16}$') { $stage = [string]$parsed.stage } else { throw 'invalid registration' }
    $enrollment = [ordered]@{ client = $parsed.client; token = $parsed.token; socket = $parsed.socket; providers = $providers } | ConvertTo-Json -Compress
    $enrollment | & wsl.exe -d $Distro -u root --exec /usr/local/bin/reignctl global-keys-enroll | Out-Null
    $stored = $LASTEXITCODE -eq 0
} catch { $stored = $false
} finally { $registration = $null; $parsed = $null; $enrollment = $null }
if (-not $stored) {
    if ($stage) { & wsl.exe -d $GlobalKeysDistro -u root --exec $helper discard reign --stage $stage | Out-Null }
    throw 'Reign could not store the new Global API Keys token. The previous enrollment (if any) still works; fix the reported problem and run this script again.'
}
& wsl.exe -d $GlobalKeysDistro -u root --exec $helper commit reign --stage $stage | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Reign stored the token but DwemerDistro did not confirm it; the staged token keeps working. Confirm it with: ddistro_keys_api commit reign --stage $stage" }

& wsl.exe -d $GlobalKeysDistro -u root --exec $helper ensure | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Reign is connected, but the Global API Keys socket did not start.' }
# Record the store distro for the launcher's keep-alive; replace atomically so a failure never corrupts the record.
$recordPath = Join-Path $env:USERPROFILE '.reign\installation.json'
if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
    $record = Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $record | Add-Member -NotePropertyName globalKeysDistro -NotePropertyValue $GlobalKeysDistro -Force
    $temp = "$recordPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($temp, $recordPath, $null)
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
Write-Output "Reign is connected to Global API Keys in $GlobalKeysDistro."
