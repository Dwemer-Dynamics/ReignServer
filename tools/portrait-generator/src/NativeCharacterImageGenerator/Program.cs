namespace Bannerlord.NativeCharacterImageGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (options.ListMorphs)
            {
                foreach (var (index, name) in FaceMorphCatalog.All)
                {
                    Console.WriteLine($"{index,2}  {name}");
                }

                return 0;
            }

            if (options.VerifyAiCacheCatalog)
            {
                return VerifyAiCacheCatalog(options.GamePath);
            }

            var outputPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (!string.IsNullOrWhiteSpace(options.CharacterId))
            {
                var gameRoot = GameInstallation.ResolveRoot(options.GamePath);
                Console.WriteLine($"Reading native character catalog: {gameRoot}");
                var catalog = CharacterCatalog.Load(gameRoot);
                var character = catalog.Characters.FirstOrDefault(candidate =>
                    candidate.Id.Equals(options.CharacterId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Native character '{options.CharacterId}' was not found.");
                var assets = NativeAssetRepository.Load(gameRoot, new Progress<string>(Console.WriteLine));
                var output = new CharacterRenderService(catalog, assets).Render(
                    character,
                    options.Width,
                    options.Height,
                    options.YawDegrees,
                    options.Zoom);
                PngWriter.Write(outputPath, output.Image.Width, output.Image.Height, output.Image.Rgba);
                Console.WriteLine(
                    $"Rendered {output.Image.TriangleCount:N0} native triangles for " +
                    $"'{character.Name}' ({character.Id}) to {outputPath}");
                Console.WriteLine("Bannerlord was not launched and no TaleWorlds engine assemblies were loaded.");
                return 0;
            }

            var packagePath = GameInstallation.ResolvePackage(options.GamePath, options.PackagePath);

            Console.WriteLine($"Reading native assets: {packagePath}");
            var result = NativeAssetRenderer.Render(packagePath, options);
            PngWriter.Write(outputPath, result.Width, result.Height, result.Rgba);

            Console.WriteLine(
                $"Rendered {result.TriangleCount:N0} native triangles from " +
                $"'{string.Join("', '", options.AssetNames)}' to {outputPath}");
            Console.WriteLine("Bannerlord was not launched and no TaleWorlds engine assemblies were loaded.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Bannerlord Native Character Image Generator

            Reads native assets from an installed copy of Bannerlord and renders a PNG.
            It does not start the game or load the TaleWorlds engine.

            Usage:
              dotnet run --project src/NativeCharacterImageGenerator -- render [options]

            Options:
              --game PATH          Bannerlord installation root (auto-detected when omitted)
              --package PATH       Read a specific TPAC package instead of core_game.tpac
              --character ID       Assemble a native roster character with civilian clothing
              --asset NAME         Native metamesh; repeat to assemble parts (default: head_male_a)
              --output PATH        PNG output path (default: artifacts/head_male_a.png)
              --width PIXELS       Image width, 64-4096 (default: 768)
              --height PIXELS      Image height, 64-4096 (default: 1024)
              --yaw DEGREES        Turn around the vertical axis (-180 to 180)
              --pitch DEGREES      Look up/down (-89 to 89)
              --zoom VALUE         Character framing scale, 0.65-4 (default: 2.9, waist-up)
              --morph NAME=VALUE   Apply a native face morph with a weight from -3 to 3
              --skin-color RRGGBB  Tint the native skin texture (default: B87857)
              --transparent        Use a transparent background
              --list-morphs        Print accepted facial morph names and indices
              --verify-ai-cache-catalog
                                   Validate the canonical 1,260-character AI cache contract
              --help               Show this help

            Example:
              dotnet run --project src/NativeCharacterImageGenerator -- render \
                --game "D:\\Games\\Mount & Blade II Bannerlord" \
                --morph FaceWidth=0.35 --yaw -12 --output artifacts/character.png
            """);
    }

    private static int VerifyAiCacheCatalog(string? gamePath)
    {
        var gameRoot = GameInstallation.ResolveRoot(gamePath);
        var native = CharacterCatalog.Load(gameRoot);
        var catalog = CanonicalCharacterCatalog.Load(gameRoot, native.Characters);
        var nativeLords = catalog.Characters.Count(record =>
            record.Character.SourceModule.Equals("Native", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(record.Character.SourceFile).Equals("lords.xml", StringComparison.OrdinalIgnoreCase));
        var nativeSpecial = catalog.Characters.Count(record =>
            record.Character.SourceModule.Equals("Native", StringComparison.OrdinalIgnoreCase)) - nativeLords;
        var warSails = catalog.Characters.Count(record =>
            record.Character.SourceModule.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase));
        var reign = catalog.Characters.Count(record =>
            record.Character.SourceModule.Equals("ReignBeta", StringComparison.OrdinalIgnoreCase));
        Require(nativeLords == 397, $"Expected 397 Native lords; found {nativeLords}.");
        Require(nativeSpecial == 78, $"Expected 78 Native story/special characters; found {nativeSpecial}.");
        Require(warSails == 65, $"Expected 65 War Sails heroes; found {warSails}.");
        var unresolvedWarSailsRank = catalog.Characters.Where(record =>
                record.Character.SourceModule.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase)
                && !record.ClanTier.HasValue)
            .Select(record => record.HeroStringId)
            .ToArray();
        Require(unresolvedWarSailsRank.Length == 9,
            "Expected nine explicitly unranked War Sails story characters; found: " +
            string.Join(", ", unresolvedWarSailsRank));
        Require(catalog.Characters.Where(record =>
                    record.Character.SourceModule.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase)
                    && record.ClanTier.HasValue)
                .All(record => record.ClanProvenance == "module_heroes_xml_and_clans_xml"),
            "A ranked War Sails hero did not resolve through heroes.xml and clans.xml.");
        Require(reign == 720, $"Expected 720 Reign court characters; found {reign}.");
        var eligibleAdults = catalog.Characters.Count(record =>
            record.AgeYears >= AiSourceCacheContract.MinimumAiPortraitAge);
        Require(eligibleAdults == 1228,
            $"Expected 1,228 adult AI portrait subjects; found {eligibleAdults}.");
        Require(catalog.Characters.Select(record => record.CacheKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1260,
            "The canonical cache keys are not unique.");
        Require(catalog.Characters.Count(record =>
                record.PhysicalConfidence.Source.Equals("neutral_fallback", StringComparison.Ordinal)) == 143,
            "Expected exactly 143 neutral physical-confidence fallbacks.");
        Require(catalog.Characters.Where(record =>
                    record.PhysicalConfidence.Source.Equals("neutral_fallback", StringComparison.Ordinal))
                .All(record => record.PhysicalConfidence.Score == 50
                    && record.PhysicalConfidence.FlirtatiousnessPercentage == 50
                    && record.PhysicalConfidence.ConfidencePercentage == 50),
            "A neutral physical-confidence fallback did not retain Reign's explicit 50/50 values.");
        Require(catalog.Characters.Where(record =>
                    record.Character.SourceModule.Equals("Native", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(record.Character.SourceFile).Equals(
                        "lords.xml",
                        StringComparison.OrdinalIgnoreCase))
                .All(record => record.Occupation.Equals(
                    string.IsNullOrWhiteSpace(record.Character.Occupation)
                        ? "NotAssigned"
                        : record.Character.Occupation,
                    StringComparison.OrdinalIgnoreCase)),
            "A Native story/special occupation was incorrectly promoted to noble metadata.");
        Require(catalog.Characters.All(record =>
                record.AgeYears > 0
                && !string.IsNullOrWhiteSpace(record.Gender)
                && !string.IsNullOrWhiteSpace(record.CultureId)
                && !string.IsNullOrWhiteSpace(record.CultureName)
                && !string.IsNullOrWhiteSpace(record.ClanId)
                && !string.IsNullOrWhiteSpace(record.Occupation)
                && !string.IsNullOrWhiteSpace(record.SocialStation)
                && !string.IsNullOrWhiteSpace(record.Archetype)
                && !string.IsNullOrWhiteSpace(record.CharacterObjectId)),
            "One or more characters have incomplete canonical metadata.");
        Require(catalog.Characters.All(record =>
                CanonicalCharacterCatalog.CreateInput(record).CivilianEquipment.All(item =>
                    item.Slot is "Body" or "Leg" or "Gloves" or "Cape")),
            "A suppressed head, weapon, horse, or harness slot leaked into visible equipment metadata.");
        Require(catalog.Characters
                .Where(record => record.AgeYears >= AiSourceCacheContract.MinimumAiPortraitAge)
                .All(record => record.Character.CivilianEquipment.Count > 0),
            "An adult AI portrait subject has no resolved civilian equipment.");

        Console.WriteLine("AI source-cache catalog contract validated.");
        Console.WriteLine($"Native lords: {nativeLords:N0}");
        Console.WriteLine($"Native story/special: {nativeSpecial:N0}");
        Console.WriteLine($"War Sails: {warSails:N0}");
        Console.WriteLine($"Reign court: {reign:N0}");
        Console.WriteLine($"Unique cache keys: {catalog.Characters.Count:N0}");
        Console.WriteLine($"Adult AI portrait subjects: {eligibleAdults:N0}; underage excluded: {catalog.Characters.Count - eligibleAdults:N0}");
        Console.WriteLine($"Profile matches: {catalog.Characters.Count - 143:N0}; neutral fallbacks: 143");
        Console.WriteLine($"Catalog SHA-256: {catalog.CatalogHash}");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
