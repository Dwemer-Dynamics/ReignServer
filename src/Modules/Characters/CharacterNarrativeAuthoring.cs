using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> NarrativeAuthoringSettings;

        private static Dictionary<string, object> RunNarrativeAuthoringCli(string[] args)
        {
            string root = Path.GetFullPath(ProfileArg(args, "--output-root", "."));
            string authorizedRoot = Environment.GetEnvironmentVariable("REIGN_NARRATIVE_AUTHORING_ROOT") ?? "";
            if (Environment.GetEnvironmentVariable("REIGN_NARRATIVE_PROVIDER_AUTHORIZED") != "1"
                || authorizedRoot.Length == 0 || root != Path.GetFullPath(authorizedRoot)
                || Environment.GetEnvironmentVariable("REIGN_DB_NAME") != "ReignValidation")
                throw new InvalidOperationException("Use the guarded artifact-bound narrative authoring MCP tool.");
            int offset = int.Parse(ProfileArg(args, "--offset", "0")), count = int.Parse(ProfileArg(args, "--count", "25"));
            if (offset < 0 || count < 1 || count > 25) throw new InvalidOperationException("Narrative batches contain 1-25 profiles.");
            string settingsPath = Environment.GetEnvironmentVariable("REIGN_NARRATIVE_AUTHORING_SETTINGS");
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath)) throw new InvalidOperationException("Installed provider settings are unavailable.");
            NarrativeAuthoringSettings = DefaultSettings();
            foreach (var value in ReadJsonObject(settingsPath)) NarrativeAuthoringSettings[value.Key] = value.Value;
            NormalizeLlmProviderSettings(NarrativeAuthoringSettings);
            NormalizeCharacterConstructionBudget(NarrativeAuthoringSettings);
            var library = ReadJsonObject(CharacterProfileCatalogPath);
            var profiles = ReadDictionaryList(library, "profiles");
            string operation = ProfileArg(args, "--operation", "author");
            if (operation == "evaluate") return EvaluateNarrativeProviderBatch(root, profiles, offset, count);
            if (operation != "author") throw new InvalidOperationException("Unknown narrative operation.");
            var results = new List<Dictionary<string, object>>();
            Directory.CreateDirectory(root);
            var resultLock = new object();
            // Three independent profiles at a time; complete generation normally uses one request.
            // The MCP run lease owns publication and serializes competing batch writers.
            Parallel.ForEach(profiles.Skip(offset).Take(count), new ParallelOptions { MaxDegreeOfParallelism = 3 }, profile =>
            {
                string heroId = ReadString(profile, "heroStringId", "");
                string file = Path.Combine(root, PromptHash(heroId).Substring(0, 24) + ".json");
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int providerCalls = 0;
                try
                {
                    var facts = NarrativePremadeFacts(profile, profiles);
                    var personality = NarrativeAuthoringPersonality(profile);
                    var narrative = AuthorCharacterNarrative(facts, personality, "premade/" + heroId, request => {
                        providerCalls++;
                        request["campaignId"] = "narrative_authoring"; request["heroStringId"] = heroId;
                        return ChatWithLlm(request);
                    }, ReadJsonObject(file), value => WriteJsonObject(file, value),
                        ReadInt(LoadSettings(), "characterConstructionMaxTokens", CharacterConstructionTokenCeiling));
                    WriteJsonObject(file, narrative);
                    lock (resultLock) results.Add(new Dictionary<string, object> { ["heroStringId"] = heroId, ["ok"] = true,
                        ["itemCount"] = ReadDictionaryList(narrative, "items").Count, ["path"] = file,
                        ["providerCalls"] = providerCalls, ["elapsedMs"] = timer.ElapsedMilliseconds, ["reused"] = providerCalls == 0 });
                }
                catch (Exception ex)
                {
                    lock (resultLock) results.Add(new Dictionary<string, object> { ["heroStringId"] = heroId, ["ok"] = false, ["error"] = ex.Message,
                        ["providerCalls"] = providerCalls, ["elapsedMs"] = timer.ElapsedMilliseconds });
                }
                lock (resultLock) WriteJsonObject(Path.Combine(root, "batch-" + offset + ".json"), new Dictionary<string, object> {
                    ["schema"] = "reign-narrative-authoring-batch-v1", ["offset"] = offset, ["count"] = count,
                    ["results"] = results, ["completedUtc"] = DateTime.UtcNow.ToString("o") });
            });
            // Assemble only after every profile has a complete validated narrative.
            int ready = 0;
            foreach (var profile in profiles)
            {
                string id = ReadString(profile, "heroStringId", "");
                var narrative = ReadJsonObject(Path.Combine(root, PromptHash(id).Substring(0, 24) + ".json"));
                var facts = NarrativePremadeFacts(profile, profiles);
                if (!NarrativeDocumentReady(narrative) || ReadString(narrative, "heroStringId", "") != id
                    || ReadString(narrative, "inputFingerprint", "") != NarrativeAuthoringFingerprint(facts, NarrativeAuthoringPersonality(profile), "premade/" + id)) continue;
                string narrativeFile = PromptHash(id).Substring(0, 24) + ".json";
                WriteJsonObject(Path.Combine(root, "narrative", "profiles", narrativeFile), narrative);
                foreach (string part in new[] { "narrative", "background", "voice", "motivations", "saveQuirks", "hiddenHistory", "secrets", "hookPools" }) profile.Remove(part);
                profile["narrativeFile"] = narrativeFile;
                profile["packVersion"] = NarrativePack;
                ready++;
            }
            if (ready == profiles.Count && profiles.Count > 0)
            {
                library["packVersion"] = NarrativePack;
                library["profiles"] = profiles;
                library["narrativeSchema"] = NarrativeSchema;
                library["generatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteJsonObject(Path.Combine(root, "profile_catalog.json"), library);
            }
            return new Dictionary<string, object> { ["ok"] = results.Count > 0 && results.All(x => ReadBool(x, "ok", false)),
                ["schema"] = "reign-narrative-authoring-batch-v1", ["results"] = results, ["readyCount"] = ready,
                ["rosterCount"] = profiles.Count, ["assembled"] = ready == profiles.Count && ready > 0,
                ["outputRoot"] = root, ["provider"] = ReadString(NarrativeAuthoringSettings, "llmProvider", ""),
                ["model"] = ModelForRequest(NarrativeAuthoringSettings, "character_construction") };
        }

        private static Dictionary<string, object> NarrativeAuthoringPersonality(Dictionary<string, object> profile)
        {
            var traits = ReadDictionary(profile, "traits");
            return new Dictionary<string, object> {
                ["foundationTraits"] = ReadDictionary(traits, "foundationTraits"),
                ["traitPercentages"] = ReadDictionary(traits, "traitPercentages"),
                ["courtVirtues"] = ReadDictionary(traits, "courtVirtues"),
                ["mbtiProfile"] = ReadDictionary(profile, "mbtiProfile") ?? ReadDictionary(traits, "mbtiProfile"),
                ["nativeDescriptionEvidence"] = ReadDictionary(traits, "nativeDescriptionEvidence") };
        }

        private static Dictionary<string, object> NarrativePremadeFacts(Dictionary<string, object> profile, List<Dictionary<string, object>> profiles)
        {
            var facts = DeepCloneProfileDictionary(ReadDictionary(profile, "sourceFacts"));
            string id = ReadString(profile, "heroStringId", ""); facts["heroStringId"] = id;
            var context = NarrativeNativeContext.Value;
            var clan = ReadDictionary(ReadDictionary(context, "clans"), ReadString(facts, "clanId", ""));
            var kingdom = ReadDictionary(ReadDictionary(context, "kingdoms"), ReadString(facts, "kingdomId", ""));
            if (!string.IsNullOrWhiteSpace(ReadString(clan, "name", ""))) facts["clanName"] = ReadString(clan, "name", "");
            if (!string.IsNullOrWhiteSpace(ReadString(kingdom, "name", ""))) facts["kingdomName"] = ReadString(kingdom, "name", "");
            facts["isRuler"] = ReadString(kingdom, "rulerId", "") == id;
            facts["isClanLeader"] = ReadString(clan, "leaderId", "") == id;
            if (ReadStringList(context, "unspecifiedAgeHeroIds").Contains(id))
            {
                facts.Remove("age");
                facts["ageContext"] = "The native source does not specify an age for this noble. Do not state an exact age or interpret the missing value as infancy; use general life stages.";
            }
            foreach (string relation in new[] { "father", "mother", "spouse" })
            {
                string relatedId = ReadString(facts, relation + "Id", "");
                var related = profiles.FirstOrDefault(x => ReadString(x, "heroStringId", "") == relatedId);
                string name = ReadString(ReadDictionary(related, "sourceFacts"), "name", "");
                if (name.Length > 0) facts[relation + "Name"] = name;
            }
            facts["childrenNames"] = profiles.Where(x => {
                var child = ReadDictionary(x, "sourceFacts");
                return ReadString(child, "fatherId", "") == id || ReadString(child, "motherId", "") == id;
            }).Select(x => ReadString(ReadDictionary(x, "sourceFacts"), "name", "")).Where(x => x.Length > 0).ToList();
            var siblings = profiles.Where(x => ReadString(x, "heroStringId", "") != id && new[] { "fatherId", "motherId" }.Any(parent =>
                ReadString(facts, parent, "").Length > 0 && ReadString(facts, parent, "") == ReadString(ReadDictionary(x, "sourceFacts"), parent, ""))).ToList();
            facts["siblingIds"] = siblings.Select(x => ReadString(x, "heroStringId", "")).ToList();
            facts["olderSiblingIds"] = facts.ContainsKey("age") ? siblings.Where(x => ReadDictionary(x, "sourceFacts").ContainsKey("age")
                && ReadDouble(ReadDictionary(x, "sourceFacts"), "age", -1) > ReadDouble(facts, "age", 150))
                .Select(x => ReadString(x, "heroStringId", "")).ToList() : new List<string>();
            facts["livingParentIds"] = profiles.Where(x => new[] { "fatherId", "motherId" }.Any(parent =>
                ReadString(facts, parent, "").Length > 0 && ReadString(facts, parent, "") == ReadString(x, "heroStringId", ""))
                && ReadBool(ReadDictionary(x, "sourceFacts"), "isAlive", true)).Select(x => ReadString(x, "heroStringId", "")).ToList();
            return facts;
        }

        private static readonly Lazy<Dictionary<string, object>> NarrativeNativeContext = new Lazy<Dictionary<string, object>>(() => {
            var value = ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", "narrative", "native-context.json"));
            if (ReadString(value, "schema", "") != "reign-narrative-native-context-v1")
                throw new InvalidDataException("Verified native display-name context is missing from the authoring artifact.");
            return value;
        });
    }
}
