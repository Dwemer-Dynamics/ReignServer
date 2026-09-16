namespace Bannerlord.NativeCharacterImageGenerator;

internal static class GameInstallation
{
    private const string RelativePackagePath = "Modules/Native/AssetPackages/core_game.tpac";

    public static string ResolvePackage(string? explicitGamePath, string? explicitPackagePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPackagePath))
        {
            return RequireFile(Path.GetFullPath(explicitPackagePath), "TPAC package");
        }

        return Path.Combine(ResolveRoot(explicitGamePath), RelativePackagePath);
    }

    public static string ResolveRoot(string? explicitGamePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitGamePath))
        {
            return RequireRoot(explicitGamePath);
        }

        var environmentPath = Environment.GetEnvironmentVariable("BANNERLORD_GAME_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return RequireRoot(environmentPath);
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            var steamRoot = Path.Combine(
                drive.RootDirectory.FullName,
                "Program Files (x86)",
                "Steam",
                "steamapps",
                "common",
                "Mount & Blade II Bannerlord");
            var candidate = Path.Combine(steamRoot, RelativePackagePath);
            if (File.Exists(candidate))
            {
                return steamRoot;
            }
        }

        throw new FileNotFoundException(
            "Could not find Bannerlord. Pass --game PATH or set BANNERLORD_GAME_PATH. " +
            "The game does not need to be running.");
    }

    private static string RequireRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        RequireFile(Path.Combine(fullRoot, RelativePackagePath), "Bannerlord Native core_game.tpac");
        return fullRoot;
    }

    private static string RequireFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} was not found: {path}", path);
        }

        return path;
    }
}
