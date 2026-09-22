param(
    [string]$GamePath = $env:BANNERLORD_GAME_PATH
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\NativeCharacterImageGenerator\NativeCharacterImageGenerator.csproj"
$output = Join-Path $root "artifacts\smoke-test.png"

& dotnet build $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @(
    "run", "--no-build", "--project", $project, "--", "render",
    "--asset", "head_male_a",
    "--width", "256",
    "--height", "320",
    "--morph", "FaceWidth=0.2",
    "--output", $output
)
if ($GamePath) {
    $arguments += @("--game", $GamePath)
}

& dotnet $arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$bytes = [System.IO.File]::ReadAllBytes($output)
if ($bytes.Length -lt 8 -or [BitConverter]::ToString($bytes, 0, 8) -ne "89-50-4E-47-0D-0A-1A-0A") {
    Write-Error "Renderer output is not a valid PNG signature: $output"
    exit 1
}

Write-Output "Smoke test passed: $output"
