param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [Parameter(Mandatory = $true)]
    [UInt64] $Bytes
)

$image = [IO.File]::ReadAllBytes($Path)
$peOffset = [BitConverter]::ToInt32($image, 0x3c)
$optionalHeaderOffset = $peOffset + 24
$magic = [BitConverter]::ToUInt16($image, $optionalHeaderOffset)
$stackReserveOffset = $optionalHeaderOffset + 0x48

switch ($magic) {
    0x10b {
        if ($Bytes -gt [UInt32]::MaxValue) {
            throw "The requested stack reserve is too large for a PE32 executable."
        }
        [BitConverter]::GetBytes([UInt32]$Bytes).CopyTo($image, $stackReserveOffset)
    }
    0x20b {
        [BitConverter]::GetBytes($Bytes).CopyTo($image, $stackReserveOffset)
    }
    default {
        throw "Unsupported PE optional-header magic 0x$($magic.ToString('X'))."
    }
}

[IO.File]::WriteAllBytes($Path, $image)
