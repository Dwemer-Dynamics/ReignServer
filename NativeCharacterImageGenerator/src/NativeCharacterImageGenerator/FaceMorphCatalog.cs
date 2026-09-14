namespace Bannerlord.NativeCharacterImageGenerator;

internal static class FaceMorphCatalog
{
    private static readonly string[] Names =
    [
        "Basis", "FaceWidth", "FaceDepth", "FaceRatio", "FaceWeight", "CheekboneHeight",
        "CheekboneWidth", "CheekboneDepth", "TopMouthSize", "NoseAngle", "CenterHeight",
        "JawLine", "FaceSharpness", "TempleWidth", "EyeDepth", "EyeShape", "EyeToEyeDistance",
        "EyeSize", "EyelidHeight", "MonolidEyes", "EyeOuterHeight", "EyeInnerHeight",
        "EyebrowDepth", "EyePosition", "BrowOuterHeight", "BrowMiddleHeight", "BrowInnerHeight",
        "NoseLength", "NoseBridge", "NoseTipHeight", "NoseSize", "NoseWidth", "NostrilHeight",
        "NoseDefinition", "NostrilSize", "MouthWidth", "MouthPosition", "NoseBump", "ChinForward",
        "ChinShape", "ChinLength", "FrownSmile", "LipThickness", "MouthForward", "BottomLipShape",
        "NoseAsymmetry", "HeadScaling", "FaceAsymmetry", "EyeAsymmetry", "JawHeight", "JawShape",
        "HideEars", "EarShape", "EyeSocketSize", "NoseShape", "LipsConcaveConvex", "EarSize",
        "OldFace", "KidFace", "EyeBump"
    ];

    private static readonly Dictionary<string, int> IndexByName = Names
        .Select((name, index) => KeyValuePair.Create(name, index))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    static FaceMorphCatalog()
    {
        IndexByName["Cheeks"] = 4;
        IndexByName["NeckSlope"] = 50;
        IndexByName["EyeDistance"] = 16;
        IndexByName["LipsFrown"] = 41;
    }

    public static bool TryGetIndex(string name, out int index) => IndexByName.TryGetValue(name, out index);

    public static IEnumerable<(int Index, string Name)> All => Names.Select((name, index) => (index, name));
}
