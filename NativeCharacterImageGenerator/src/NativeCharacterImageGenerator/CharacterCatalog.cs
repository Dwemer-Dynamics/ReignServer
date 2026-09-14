using System.Globalization;
using System.Xml.Linq;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed class CharacterCatalog
{
    private static readonly CharacterSource[] CharacterSources =
    [
        new("Modules/SandBox/ModuleData/lords.xml", "Native", false, false),
        new("Modules/StoryMode/ModuleData/story_mode_characters.xml", "Native", false, false),
        new("Modules/SandBox/ModuleData/spspecialcharacters.xml", "Native", false, false),
        new("Modules/SandBoxCore/ModuleData/spnpccharacters.xml", "Native", false, false),
        new("Modules/NavalDLC/ModuleData/naval_lords.xml", "NavalDLC", true, false),
        new("Modules/NavalDLC/ModuleData/naval_characters.xml", "NavalDLC", true, false),
        new("Modules/ReignBeta/ModuleData/reign_court_lords.xml", "ReignBeta", true, true)
    ];

    private CharacterCatalog(
        IReadOnlyList<NativeCharacterDefinition> characters,
        IReadOnlyDictionary<string, NativeItemDefinition> items,
        NativeAppearanceCatalog appearance)
    {
        Characters = characters;
        Items = items;
        Appearance = appearance;
    }

    public IReadOnlyList<NativeCharacterDefinition> Characters { get; }
    public IReadOnlyDictionary<string, NativeItemDefinition> Items { get; }
    public NativeAppearanceCatalog Appearance { get; }

    public static CharacterCatalog Load(string gameRoot)
    {
        var equipment = EquipmentTemplateIndex.Load(gameRoot);
        var characters = new Dictionary<string, NativeCharacterDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in CharacterSources)
        {
            var path = Path.Combine(gameRoot, source.RelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            var document = XDocument.Load(path, LoadOptions.None);
            foreach (var element in document.Descendants().Where(IsNamed("NPCCharacter")))
            {
                if (ParseBool(Attribute(element, "is_template"))
                    || (source.HeroOnly && !ParseBool(Attribute(element, "is_hero"))))
                {
                    continue;
                }

                var bodyProperties = element
                    .Descendants()
                    .FirstOrDefault(IsNamed("BodyProperties"));
                var faceTemplate = element
                    .Descendants()
                    .FirstOrDefault(IsNamed("face_key_template"));
                var faceTemplateId = faceTemplate is null
                    ? string.Empty
                    : Attribute(faceTemplate, "value").Replace(
                        "BodyProperty.",
                        string.Empty,
                        StringComparison.OrdinalIgnoreCase);
                if (bodyProperties is null
                    && (!source.AllowFaceTemplateWithoutBody || string.IsNullOrWhiteSpace(faceTemplateId)))
                {
                    continue;
                }

                var id = Attribute(element, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var culture = Attribute(element, "culture");
                var inlineCivilian = element
                    .Descendants()
                    .FirstOrDefault(child => IsNamed("EquipmentRoster")(child)
                        && Attribute(child, "civilian").Equals("true", StringComparison.OrdinalIgnoreCase));
                var templateReference = element
                    .Descendants()
                    .FirstOrDefault(child => IsNamed("EquipmentSet")(child)
                        && Attribute(child, "equipmentType").Equals("Civilian", StringComparison.OrdinalIgnoreCase));
                var templateId = templateReference is null ? string.Empty : Attribute(templateReference, "id");
                var inlineAssigned = inlineCivilian is null
                    ? []
                    : ReadEquipment(inlineCivilian);
                // Several authored heroes contain an intentionally empty civilian
                // roster followed by a real civilian EquipmentSet. Treat the empty
                // roster as a placeholder, not as an instruction to render unclothed.
                var assigned = inlineAssigned.Count > 0
                    ? inlineAssigned
                    : equipment.Resolve(templateId, culture, id);

                var name = StripLocalization(Attribute(element, "name"));
                characters[id] = new NativeCharacterDefinition(
                    id,
                    string.IsNullOrWhiteSpace(name) ? id : name,
                    culture,
                    ParseBool(Attribute(element, "is_female")),
                    bodyProperties is null
                        ? ParseFloat(Attribute(element, "age"), 30f)
                        : ParseFloat(Attribute(bodyProperties, "age"), ParseFloat(Attribute(element, "age"), 30f)),
                    NativeUnitAttribute(bodyProperties, "weight"),
                    NativeUnitAttribute(bodyProperties, "build"),
                    bodyProperties is null ? string.Empty : Attribute(bodyProperties, "key"),
                    templateId,
                    assigned,
                    source.RelativePath,
                    FaceTemplateId: faceTemplateId,
                    SourceModule: source.ModuleId,
                    Occupation: Attribute(element, "occupation"),
                    IsHero: ParseBool(Attribute(element, "is_hero")))
                {
                    WeightSource = bodyProperties is null || string.IsNullOrWhiteSpace(Attribute(bodyProperties, "weight")) ? "generator_default_0.5" : "native_xml",
                    BuildSource = bodyProperties is null || string.IsNullOrWhiteSpace(Attribute(bodyProperties, "build")) ? "generator_default_0.5" : "native_xml"
                };
            }
        }

        var ordered = characters.Values
            .OrderBy(character => character.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(character => character.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CharacterCatalog(ordered, LoadItems(gameRoot), NativeAppearanceCatalog.Load(gameRoot));
    }

    private static float NativeUnitAttribute(XElement? body, string name)
    {
        string text = body is null ? "" : Attribute(body, name);
        if (string.IsNullOrWhiteSpace(text)) return .5f;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(value) || value < 0 || value > 1)
            throw new InvalidDataException("Native " + name + " must be a finite 0–1 value.");
        return value;
    }

    private static IReadOnlyDictionary<string, NativeItemDefinition> LoadItems(string gameRoot)
    {
        var items = new Dictionary<string, NativeItemDefinition>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Path.Combine(gameRoot, "Modules", "SandBoxCore", "ModuleData", "items"),
            Path.Combine(gameRoot, "Modules", "StoryMode", "ModuleData"),
            Path.Combine(gameRoot, "Modules", "NavalDLC", "ModuleData")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.xml", SearchOption.TopDirectoryOnly))
            {
                XDocument document;
                try
                {
                    document = XDocument.Load(path, LoadOptions.None);
                }
                catch
                {
                    continue;
                }

                foreach (var element in document.Descendants().Where(IsNamed("Item")))
                {
                    var id = Attribute(element, "id");
                    var mesh = Attribute(element, "mesh");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(mesh))
                    {
                        continue;
                    }

                    items[id] = new NativeItemDefinition(
                        id,
                        StripLocalization(Attribute(element, "name")),
                        mesh,
                        Attribute(element, "culture"),
                        Attribute(element, "Type"));
                }
            }
        }

        return items;
    }

    private static IReadOnlyList<NativeEquipmentAssignment> ReadEquipment(XElement container) =>
        container.Descendants()
            .Where(element => element.Name.LocalName.Equals("equipment", StringComparison.OrdinalIgnoreCase))
            .Select(element => new NativeEquipmentAssignment(
                Attribute(element, "slot"),
                Attribute(element, "id").Replace("Item.", string.Empty, StringComparison.OrdinalIgnoreCase)))
            .Where(assignment => !string.IsNullOrWhiteSpace(assignment.Slot)
                && !string.IsNullOrWhiteSpace(assignment.ItemId))
            .GroupBy(assignment => assignment.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private static Func<XElement, bool> IsNamed(string name) =>
        element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string Attribute(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
        ?? string.Empty;

    private static string StripLocalization(string value)
    {
        if (value.StartsWith("{=", StringComparison.Ordinal))
        {
            var end = value.IndexOf('}');
            if (end >= 0 && end + 1 < value.Length)
            {
                return value[(end + 1)..];
            }
        }

        return value;
    }

    private static bool ParseBool(string value) =>
        bool.TryParse(value, out var result) && result;

    private static float ParseFloat(string value, float fallback) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;

    private sealed class EquipmentTemplateIndex
    {
        private static readonly string[] EquipmentFiles =
        [
            "Modules/SandBoxCore/ModuleData/sandboxcore_equipment_sets.xml",
            "Modules/SandBox/ModuleData/sandbox_equipment_sets.xml",
            "Modules/StoryMode/ModuleData/story_mode_equipments.xml",
            "Modules/NavalDLC/ModuleData/naval_equipment_sets.xml"
        ];

        private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<NativeEquipmentAssignment>>> _byId =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<IReadOnlyList<NativeEquipmentAssignment>>> _byCulture =
            new(StringComparer.OrdinalIgnoreCase);

        public static EquipmentTemplateIndex Load(string gameRoot)
        {
            var result = new EquipmentTemplateIndex();
            foreach (var relativePath in EquipmentFiles)
            {
                var path = Path.Combine(gameRoot, relativePath);
                if (!File.Exists(path))
                {
                    continue;
                }

                var document = XDocument.Load(path, LoadOptions.None);
                foreach (var roster in document.Descendants().Where(IsNamed("EquipmentRoster")))
                {
                    var sets = roster.Elements()
                        .Where(IsNamed("EquipmentSet"))
                        .Where(set => Attribute(set, "equipmentType").Equals("Civilian", StringComparison.OrdinalIgnoreCase))
                        .Select(ReadEquipment)
                        .Where(set => set.Count > 0)
                        .ToArray();
                    if (sets.Length == 0)
                    {
                        continue;
                    }

                    var rosterId = Attribute(roster, "id");
                    if (!string.IsNullOrWhiteSpace(rosterId))
                    {
                        result._byId[rosterId] = sets;
                    }

                    var culture = Attribute(roster, "culture");
                    if (string.IsNullOrWhiteSpace(culture))
                    {
                        continue;
                    }

                    if (!result._byCulture.TryGetValue(culture, out var list))
                    {
                        list = [];
                        result._byCulture[culture] = list;
                    }

                    list.AddRange(sets);
                }
            }

            return result;
        }

        public IReadOnlyList<NativeEquipmentAssignment> Resolve(string templateId, string culture, string characterId)
        {
            if (!string.IsNullOrWhiteSpace(templateId)
                && _byId.TryGetValue(templateId, out var exact)
                && exact.Count > 0)
            {
                var exactSelector = StableHash(characterId);
                return exact[(int)(exactSelector % (uint)exact.Count)];
            }

            if (!_byCulture.TryGetValue(culture, out var candidates) || candidates.Count == 0)
            {
                return [];
            }

            var selector = StableHash($"{templateId}|{characterId}");
            return candidates[(int)(selector % (uint)candidates.Count)];
        }

        private static uint StableHash(string value)
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    private sealed record CharacterSource(
        string RelativePath,
        string ModuleId,
        bool HeroOnly,
        bool AllowFaceTemplateWithoutBody);
}
