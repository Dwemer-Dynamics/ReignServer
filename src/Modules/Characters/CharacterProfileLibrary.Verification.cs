using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunCharacterProfileLibrarySelfTests(bool includeCourt = true)
        {
            Dictionary<string, Dictionary<string, object>> profiles = LoadCharacterProfileLibrary();
            if (!includeCourt)
            {
                profiles = profiles.Where(pair => !ReadString(pair.Value, "source", "").Equals("reign_court", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }
            Dictionary<string, object> validation = ValidateCharacterProfileLibrary(includeCourt);
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            results.Add(ProfileSelfTest("catalog_manifest", ReadBool(validation, "ok", false), "Profile catalog count, IDs, traits, locked mappings, prose, and privacy validate.", validation));

            List<int> personalityLengths = profiles.Values
                .Select(profile => ReadString(ReadDictionary(profile, "traits"), "basePersonalitySummary", "").Length)
                .ToList();
            bool roundedPortraitsReady = profiles.Count > 0
                && profiles.Values.All(profile => PersonalityPortraitReady(ReadDictionary(profile, "traits")))
                && personalityLengths.All(length => length >= PersonalitySummaryMinimumCharacters && length <= PersonalitySummaryMaximumCharacters);
            results.Add(ProfileSelfTest("rounded_personality_portraits", roundedPortraitsReady,
                "Every shipped character has a complete nine-part personality portrait and a prompt-ready 1200-1600 character synthesis.",
                new Dictionary<string, object>
                {
                    ["profileCount"] = profiles.Count,
                    ["minimumCharacters"] = personalityLengths.Count == 0 ? 0 : personalityLengths.Min(),
                    ["maximumCharacters"] = personalityLengths.Count == 0 ? 0 : personalityLengths.Max(),
                    ["averageCharacters"] = personalityLengths.Count == 0 ? 0 : Math.Round(personalityLengths.Average(), 2)
                }));

            bool ulbosReady = profiles.TryGetValue("lord_1_47", out Dictionary<string, object> ulbos)
                && ReadString(ReadDictionary(ulbos, "sourceFacts"), "name", "") == "Ulbos"
                && (ReadDictionary(ReadDictionary(ulbos, "traits"), "foundationTraits") ?? new Dictionary<string, object>()).Count == CoreTraitKeys.Length
                && !string.IsNullOrWhiteSpace(ReadString(ReadDictionary(ulbos, "background"), "summary", ""));
            results.Add(ProfileSelfTest("ulbos_fixed_profile", ulbosReady, "Ulbos resolves to the correct fixed ID with a complete personality and background.", null));

            List<Dictionary<string, object>> household = includeCourt
                ? profiles.Values.Where(x => ReadString(ReadDictionary(x, "sourceFacts"), "householdId", "") == "reign_court_castle_EN1").ToList()
                : new List<Dictionary<string, object>>();
            if (includeCourt)
            {
                bool householdReady = household.Count == 6
                    && household.Select(x => ReadString(ReadDictionary(x, "sourceFacts"), "householdRole", "")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6
                    && household.Select(x => ReadString(ReadDictionary(x, "traits"), "basePersonalitySummary", "")).Distinct(StringComparer.Ordinal).Count() == 6;
                results.Add(ProfileSelfTest("court_household_distinctiveness", householdReady, "A complete court household has six linked roles with distinct personality summaries.", new Dictionary<string, object> { ["count"] = household.Count }));
            }

            Dictionary<string, object> hookProfile = profiles.Values.FirstOrDefault(x => ReadString(x, "source", "") == "native_sandbox") ?? new Dictionary<string, object>();
            if (ReadString(CharacterProfileLibraryRoot, "packVersion", "") == NarrativePack)
                hookProfile = ReadDictionaryList(Program.ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", "profile_catalog_v4.json")), "profiles")
                    .FirstOrDefault(x => ReadString(x, "source", "") == "native_sandbox") ?? hookProfile;
            Dictionary<string, object> pools = ReadDictionary(hookProfile, "hookPools") ?? new Dictionary<string, object>();
            string heroId = FirstNonEmpty(ReadString(hookProfile, "heroStringId", ""), "missing");
            string first = SelectCampaignHook(pools, "currentTemptations", "campaign_a", heroId, "initial");
            string repeat = SelectCampaignHook(pools, "currentTemptations", "campaign_a", heroId, "initial");
            bool hooksStable = !string.IsNullOrWhiteSpace(first) && first == repeat;
            results.Add(ProfileSelfTest("campaign_hook_stability", hooksStable, "Campaign hook selection is stable for the same campaign and character.", new Dictionary<string, object> { ["selected"] = first }));

            Dictionary<string, object> unknown = new Dictionary<string, object>
            {
                ["heroStringId"] = "profile_self_test_unknown",
                ["name"] = "Profile Test Unknown",
                ["occupation"] = "Lord",
                ["isLord"] = true,
                ["age"] = 30,
                ["traits"] = new Dictionary<string, object> { ["valor"] = 1, ["generosity"] = 0, ["honor"] = 1, ["mercy"] = 0, ["calculating"] = 0 },
                ["skills"] = new Dictionary<string, object> { ["charm"] = 100, ["leadership"] = 120, ["steward"] = 100 }
            };
            Dictionary<string, object> baselineTraits = BuildTraitDocument(unknown);
            Dictionary<string, object> baselineGenerated = CharactersService.DefaultGeneratedCharacter(unknown, baselineTraits);
            bool baselineReady = CharactersService.TraitDocumentReady(baselineTraits)
                && ReadObjectList(ReadDictionary(baselineGenerated, "motivations"), "dreams").Count > 0
                && !string.IsNullOrWhiteSpace(ReadString(ReadDictionary(baselineGenerated, "hiddenHistory"), "privateBackstory", ""))
                && PersonalityPortraitReady(baselineTraits);
            results.Add(ProfileSelfTest("unknown_baseline", baselineReady, "Unknown characters receive a deterministic foundation for the first response before background enrichment.", null));

            string policyCampaign = "__character_policy_test_" + Guid.NewGuid().ToString("N");
            try
            {
                string dynamicHeroId = "profile_self_test_dynamic";
                unknown["heroStringId"] = dynamicHeroId;
                Dictionary<string, object> passiveStack = LoadPassiveCharacterStack(policyCampaign, dynamicHeroId, unknown);
                bool passiveDidNotMaterialize = !Directory.Exists(Program.CharacterDirectory(policyCampaign, dynamicHeroId))
                    && string.Equals(ReadString(passiveStack, "profileSource", ""), "live_runtime_unconstructed", StringComparison.OrdinalIgnoreCase)
                    && ReadBool(passiveStack, "constructionDeferredUntilFirstInteraction", false);
                results.Add(ProfileSelfTest("passive_event_does_not_create_dynamic_profile", passiveDidNotMaterialize,
                    "Passive event planning uses live baseline facts without creating or constructing a dynamic character profile.", null));

                MaterializeCharacterProfile(policyCampaign, dynamicHeroId, unknown, false, false);
                Dictionary<string, object> dynamicConstructed = Program.ReadJsonObject(Program.CharacterFile(policyCampaign, dynamicHeroId, "constructed.json"));
                Dictionary<string, object> dynamicEnrichment = Program.ReadJsonObject(Program.CharacterFile(policyCampaign, dynamicHeroId, "enrichment.json"));
                bool dynamicDeferred = string.Equals(ReadString(dynamicConstructed, "status", ""), "first_contact_pending", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(dynamicEnrichment, "status", ""), "deferred_until_interaction", StringComparison.OrdinalIgnoreCase)
                    && RequiresFirstContactConstruction(policyCampaign, dynamicHeroId, dynamicConstructed);
                results.Add(ProfileSelfTest("dynamic_profile_waits_for_first_contact", dynamicDeferred,
                    "An uncatalogued dynamic character remains unmaterialized until its first interaction requests a deterministic scaffold.",
                    new Dictionary<string, object>
                    {
                        ["constructedStatus"] = ReadString(dynamicConstructed, "status", ""),
                        ["enrichmentStatus"] = ReadString(dynamicEnrichment, "status", "")
                    }));

                string canonicalHeroId = profiles.Keys.FirstOrDefault() ?? "";
                Dictionary<string, object> canonicalRuntime = new Dictionary<string, object>
                {
                    ["heroStringId"] = canonicalHeroId,
                    ["name"] = ReadString(ReadDictionary(profiles[canonicalHeroId], "sourceFacts"), "name", canonicalHeroId),
                    ["isLord"] = true
                };
                MaterializeCharacterProfile(policyCampaign, canonicalHeroId, canonicalRuntime, false, false);
                Dictionary<string, object> canonicalConstructed = Program.ReadJsonObject(Program.CharacterFile(policyCampaign, canonicalHeroId, "constructed.json"));
                Dictionary<string, object> canonicalEnrichment = Program.ReadJsonObject(Program.CharacterFile(policyCampaign, canonicalHeroId, "enrichment.json"));
                bool canonicalReady = string.Equals(ReadString(canonicalConstructed, "status", ""), "canonical_ready", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(canonicalEnrichment, "status", ""), "not_required", StringComparison.OrdinalIgnoreCase)
                    && !RequiresFirstContactConstruction(policyCampaign, canonicalHeroId, canonicalConstructed);
                results.Add(ProfileSelfTest("canonical_profile_never_needs_llm", canonicalReady,
                    "A shipped character profile materializes without an LLM construction call.",
                    new Dictionary<string, object>
                    {
                        ["constructedStatus"] = ReadString(canonicalConstructed, "status", ""),
                        ["enrichmentStatus"] = ReadString(canonicalEnrichment, "status", "")
                    }));

                Dictionary<string, object> scaffold = CharactersService.ConstructCharacterFiles(policyCampaign, dynamicHeroId, unknown, true, false, "background_enrichment");
                results.Add(ProfileSelfTest("background_enrichment_fallback_safe",
                    CharactersService.IsCharacterConstructionReady(scaffold) && !ReadBool(scaffold, "llmUsed", true),
                    "Background enrichment retains a usable deterministic scaffold when no provider result is available.", scaffold));
            }
            finally
            {
                try
                {
                    string policyCampaignDir = Program.CampaignDirectory(policyCampaign);
                    if (Directory.Exists(policyCampaignDir))
                    {
                        ReignPostgreSqlStorage.ClearAllPools();
                        Directory.Delete(policyCampaignDir, true);
                    }
                }
                catch { }
            }

            Dictionary<string, object> sparseNativeTraits = BuildTraitDocument(new Dictionary<string, object>
            {
                ["heroStringId"] = "profile_self_test_sparse", ["name"] = "Sparse Native Profile"
            });
            results.Add(ProfileSelfTest("sparse_native_traits_readiness", CharactersService.TraitDocumentReady(sparseNativeTraits),
                "Characters whose native trait payload is unavailable still become stable after their complete 43-trait foundation is built.", null));

            Dictionary<string, object> settings = Program.DefaultSettings();
            settings["dialogueModel"] = "dialogue-test-model";
            settings["characterConstructionModel"] = "construction-test-model";
            results.Add(ProfileSelfTest("dedicated_construction_model", Program.ModelForRequest(settings, "character_construction") == "construction-test-model", "First-contact character construction routes through the dedicated construction model setting.", null));
            bool adaptiveRetryBudget = CharactersService.CharacterConstructionMaxTokensForAttempt(3600, 1) == 3600
                && CharactersService.CharacterConstructionMaxTokensForAttempt(3600, 2) == 7200
                && CharactersService.CharacterConstructionMaxTokensForAttempt(30000, 2) == 50000
                && CharactersService.CharacterConstructionMaxTokensForAttempt(50000, 1) == 50000
                && CharactersService.CharacterConstructionMaxTokensForAttempt(int.MaxValue, 2) == 50000
                && ReadInt(Program.DefaultSettings(), "characterConstructionMaxTokens", 0) == 50000;
            results.Add(ProfileSelfTest("adaptive_construction_token_retry", adaptiveRetryBudget,
                "Construction defaults to a 50,000-token ceiling; a deliberately smaller setting can grow on retry but never exceeds 50,000.",
                new Dictionary<string, object>
                {
                    ["firstAttemptTokens"] = CharactersService.CharacterConstructionMaxTokensForAttempt(3600, 1),
                    ["retryTokens"] = CharactersService.CharacterConstructionMaxTokensForAttempt(3600, 2)
                }));

            var oldBudget = new Dictionary<string, object> { ["characterConstructionMaxTokens"] = 3600, ["maxTokens"] = 1200 };
            bool budgetMigrated = NormalizeCharacterConstructionBudget(oldBudget);
            bool budgetStable = !NormalizeCharacterConstructionBudget(oldBudget);
            bool budgetDefault = ReadInt(oldBudget, "characterConstructionMaxTokens", 0) == 50000;
            oldBudget["characterConstructionMaxTokens"] = 42000;
            bool deliberateBudgetKept = !NormalizeCharacterConstructionBudget(oldBudget)
                && ReadInt(oldBudget, "characterConstructionMaxTokens", 0) == 42000;
            oldBudget["characterConstructionMaxTokens"] = 90000;
            results.Add(ProfileSelfTest("construction_budget_migration", budgetMigrated && budgetStable && budgetDefault
                && deliberateBudgetKept && NormalizeCharacterConstructionBudget(oldBudget)
                && ReadInt(oldBudget, "characterConstructionMaxTokens", 0) == 50000 && ReadInt(oldBudget, "maxTokens", 0) == 1200,
                "Existing small construction budgets migrate once to 50,000; later explicit lower choices and unrelated dialogue limits are preserved, and larger values are capped.", null));

            string compactRetryPrompt = CharactersService.BuildCharacterConstructionPromptForAttempt("campaign", "hero", unknown, baselineTraits, 2);
            results.Add(ProfileSelfTest("compact_construction_retry_prompt",
                compactRetryPrompt.Contains("no more than 12,000 characters total")
                    && compactRetryPrompt.Contains("Close every object and array"),
                "The adaptive retry explicitly prioritizes a complete compact JSON object.", null));

            List<Dictionary<string, object>> lengthAttempts = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["parsed"] = false,
                    ["finishReason"] = "length",
                    ["error"] = "Character construction response hit the max token limit before valid JSON completed."
                }
            };
            results.Add(ProfileSelfTest("construction_token_limit_diagnostic",
                CharactersService.LikelyCharacterConstructionIssue("Character construction response hit the max token limit before valid JSON completed.", lengthAttempts)
                    .Contains("cut off by the character construction token limit"),
                "A local output-budget cutoff is diagnosed as token truncation rather than provider rate limiting.", null));

            Dictionary<string, object> legacyParseFailure = new Dictionary<string, object>
            {
                ["status"] = "failed",
                ["failureKind"] = "parse",
                ["constructionEngineVersion"] = Program.CharacterConstructionEngineVersion - 1,
                ["settingsRevision"] = ReadString(settings, "settingsRevision", ""),
                ["settingsFingerprint"] = Program.SettingsFingerprint(settings),
                ["failedUtc"] = DateTime.UtcNow.AddMinutes(5).ToString("o")
            };
            results.Add(ProfileSelfTest("legacy_truncation_auto_retry",
                CharactersService.ShouldRetryFailedConstruction(legacyParseFailure, settings),
                "A construction that failed under the pre-adaptive engine is retried once without requiring a settings edit.", null));
            var exhaustedBudget = DeepCloneProfileDictionary(legacyParseFailure);
            exhaustedBudget["failureKind"] = "output_limit";
            exhaustedBudget["constructionEngineVersion"] = Program.CharacterConstructionEngineVersion;
            var changedBudgetSettings = DeepCloneProfileDictionary(settings);
            changedBudgetSettings["characterConstructionMaxTokens"] = 42000;
            results.Add(ProfileSelfTest("construction_output_limit_retry_policy",
                !CharactersService.ShouldRetryFailedConstruction(exhaustedBudget, settings)
                    && CharactersService.ShouldRetryFailedConstruction(exhaustedBudget, changedBudgetSettings),
                "An exhausted current creation budget does not trigger another automatic charge; a deliberate configuration change permits retry.", null));
            results.Add(ProfileSelfTest("background_character_worker_disabled", !ReadBool(settings, "backgroundCharacterEnrichmentEnabled", true), "Automatic background character construction is disabled by default.", null));
            results.AddRange(RunTownNotableProfileSelfTests());
            return results;
        }
#endif

        internal static Dictionary<string, object> ProfileSelfTest(string caseId, bool passed, string summary, Dictionary<string, object> data)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = passed,
                ["suite"] = "character_profiles",
                ["caseId"] = caseId,
                ["summary"] = summary,
                ["data"] = data ?? new Dictionary<string, object>()
            };
        }
    }
}
