# Consume and verify stdin before the command can change controls. exec preserves its exit status.
$script:ReignDistroInputReceiver = @'
import hashlib, os, sys, tempfile

data = sys.stdin.buffer.read()
if hashlib.sha256(data).hexdigest() != sys.argv[1]:
    print("Incomplete or altered deployment input.", file=sys.stderr)
    sys.exit(65)
with tempfile.TemporaryFile() as source:
    source.write(data)
    source.seek(0)
    os.dup2(source.fileno(), 0)
    os.execv(sys.argv[2], sys.argv[2:])
'@

function Complete-ReignInputProcess([Diagnostics.Process]$Process, [byte[]]$Bytes) {
    $stream = $Process.StandardInput.BaseStream
    try {
        try { $stream.Write($Bytes, 0, $Bytes.Length) }
        finally { $stream.Close() }
    } catch [IO.IOException] {
        # A buffered write can fail at close. The receiver rejects incomplete input
        # before exec, so its status distinguishes rejection from a completed command.
        Write-Verbose $_.Exception.Message
    }
    $Process.WaitForExit()
    return $Process.ExitCode
}

function Invoke-ReignWslWithInput([string]$Program, [string[]]$Command, [string]$User = 'root') {
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $bytes = $utf8.GetBytes($Program)
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\wsl.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.StandardInputEncoding = $utf8
    $receiver = @('-d', $Distro, '-u', $User, '--exec', '/usr/bin/python3', '-I', '-c', $script:ReignDistroInputReceiver, $digest)
    foreach ($argument in $receiver + $Command) { $start.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::Start($start)
    try {
        Complete-ReignInputProcess $process $bytes
    } finally {
        $process.Dispose()
    }
}

function Invoke-ReignDistroShell([string]$Program, [string[]]$Arguments) {
    Invoke-ReignWslWithInput $Program (@('/bin/sh', '-s', '--') + $Arguments)
}

# LF-normalize once, before activation. The brace group makes sh parse the whole
# program before running any of it, so no child command can read its remaining text.
function Get-ReignDistroShellProgram([string]$Path) {
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)).Replace("`r`n", "`n")
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    return "{`n$text}`n"
}
