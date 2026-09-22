param(
    [string]$GamePath = $env:BANNERLORD_GAME_PATH,
    [string]$Output = (Join-Path $PSScriptRoot "..\artifacts\male_base.png")
)

$project = Join-Path $PSScriptRoot "..\src\NativeCharacterImageGenerator\NativeCharacterImageGenerator.csproj"
$arguments = @(
    "run", "--project", $project, "--", "render",
    "--asset", "body_male_a",
    "--asset", "head_male_a",
    "--asset", "hands_male_a",
    "--asset", "feet_male_a",
    "--output", $Output
)

if ($GamePath) {
    $arguments += @("--game", $GamePath)
}

& dotnet $arguments
exit $LASTEXITCODE
