# Write the reviewed bytes to wsl.exe directly. A PowerShell text pipeline appends CRLF.
function Invoke-ReignWslWithInput([string]$Program, [string[]]$WslArguments) {
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $bytes = $utf8.GetBytes($Program)
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\wsl.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.StandardInputEncoding = $utf8
    foreach ($argument in $WslArguments) { $start.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::Start($start)
    try {
        $stream = $process.StandardInput.BaseStream
        $writeError = $null
        try { $stream.Write($bytes, 0, $bytes.Length) }
        catch [IO.IOException] { $writeError = $_ }
        finally { $stream.Close() }
        $process.WaitForExit()
        if ($writeError -and $process.ExitCode -eq 0) { throw $writeError }
        return $process.ExitCode
    } finally {
        $process.Dispose()
    }
}

function Invoke-ReignDistroShell([string]$Program, [string[]]$Arguments) {
    Invoke-ReignWslWithInput $Program (@('-d', $Distro, '-u', 'root', '--exec', '/bin/sh', '-s', '--') + $Arguments)
}

# LF-normalize once, before activation. The brace group makes sh parse the whole
# program before running any of it, so no child command can read its remaining text.
function Get-ReignDistroShellProgram([string]$Path) {
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)).Replace("`r`n", "`n")
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    return "{`n$text}`n"
}
