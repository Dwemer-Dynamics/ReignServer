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
        private const string CharacterProfilePackVersion = "reign_profiles_v4";
        private static readonly string CharacterProfileCatalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", "profile_catalog.json");
        internal static readonly object CharacterProfileLibraryLock = new object();
        private static Dictionary<string, object> CharacterProfileLibraryRoot;
        private static Dictionary<string, Dictionary<string, object>> CharacterProfilesById;
        private static Dictionary<string, Dictionary<string, object>> LegacyCharacterProfilesById;
        internal static readonly object CharacterEditorAuthorityCacheLock = new object();
        internal static readonly Dictionary<string, HashSet<string>> CharacterEditorAuthorityByCampaign = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        internal static void InitializeCharacterProfileLibrary()
        {
            LoadCharacterProfileLibrary();
            DeferLegacyBackgroundEnrichmentQueues();
        }

        internal static Dictionary<string, Dictionary<string, object>> LoadCharacterProfileLibrary()
        {
            lock (CharacterProfileLibraryLock)
            {
                if (CharacterProfilesById != null) return CharacterProfilesById;
                CharacterProfileLibraryRoot = ReadShippedNarrativeCatalog();
                CharacterProfilesById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> profile in ReadDictionaryList(CharacterProfileLibraryRoot, "profiles"))
                {
                    string id = ReadFirstString(profile, "heroStringId", "heroId", "id");
                    if (!string.IsNullOrWhiteSpace(id) && !CharacterProfilesById.ContainsKey(id)) CharacterProfilesById[id] = profile;
                }

                Program.LogOperational("character_profile_library.loaded", new Dictionary<string, object>
                {
                    ["path"] = CharacterProfileCatalogPath,
                    ["exists"] = File.Exists(CharacterProfileCatalogPath),
                    ["profileCount"] = CharacterProfilesById.Count,
                    ["packVersion"] = ReadString(CharacterProfileLibraryRoot, "packVersion", "")
                });
                return CharacterProfilesById;
            }
        }

        internal static Dictionary<string, object> MaterializeCharacterProfile(string campaignId, string heroId, Dictionary<string, object> runtimeProfile, bool forceCanonical, bool rerollHooks)
        {
            if (string.IsNullOrWhiteSpace(heroId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            runtimeProfile = WithNarrativeCampaignVersion(campaignId, runtimeProfile);
            Dictionary<string, Dictionary<string, object>> library = CharacterProfileLibraryFor(runtimeProfile);
            if (library.TryGetValue(heroId, out Dictionary<string, object> shipped))
            {
                if (ReadInt(runtimeProfile, "narrativeVersion", 0) >= 5 && !NarrativeDocumentReady(ReadDictionary(shipped, "narrative")))
                    return new Dictionary<string, object> { ["ok"] = false, ["status"] = "narrative_catalog_unavailable",
                        ["error"] = "The complete expanded character catalog is required for this new campaign." };
                return MaterializeCanonicalProfile(campaignId, heroId, runtimeProfile, shipped, forceCanonical, rerollHooks);
            }
            return EnsureDeterministicBaseline(campaignId, heroId, runtimeProfile);
        }

        internal static Dictionary<string, Dictionary<string, object>> CharacterProfileLibraryFor(Dictionary<string, object> runtimeProfile)
        {
            var library = LoadCharacterProfileLibrary();
            if (ReadInt(runtimeProfile, "narrativeVersion", 0) < 5 && ReadString(CharacterProfileLibraryRoot, "packVersion", "") == NarrativePack)
            {
                lock (CharacterProfileLibraryLock)
                {
                    if (LegacyCharacterProfilesById == null)
                        LegacyCharacterProfilesById = ReadDictionaryList(Program.ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "profiles", "profile_catalog_v4.json")), "profiles").ToDictionary(x => ReadString(x, "heroStringId", ""), StringComparer.OrdinalIgnoreCase);
                    library = LegacyCharacterProfilesById;
                }
            }
            return library;
        }

        internal static Dictionary<string, object> WithNarrativeCampaignVersion(string campaignId, Dictionary<string, object> profile)
        {
            if (profile != null && profile.ContainsKey("narrativeVersion")) return profile;
            var result = new Dictionary<string, object>(profile ?? new Dictionary<string, object>());
            result["narrativeVersion"] = ReadInt(Program.ReadJsonObject(Program.CampaignFile(campaignId, "campaign.json")), "narrativeVersion", 0);
            return result;
        }

        internal static Dictionary<string, object> MigrateCharacterProfilesToCurrentModels(string[] args)
        {
            string requestedCampaign = ProfileArg(args, "--campaign-id", "");
            string campaignsRoot = Program.CampaignsRoot();
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            if (!Directory.Exists(campaignsRoot))
                return new Dictionary<string, object>{{"ok",true},{"campaignCount",0},{"characterCount",0},{"migratedCount",0},{"skippedCount",0},{"failedCount",0},{"results",results}};

            IEnumerable<string> campaignDirectories = Directory.GetDirectories(campaignsRoot)
                .Where(path => string.IsNullOrWhiteSpace(requestedCampaign)
                    || Path.GetFileName(path).Equals(requestedCampaign, StringComparison.OrdinalIgnoreCase));
            int campaignCount = 0, characterCount = 0, migratedCount = 0, skippedCount = 0, failedCount = 0;
            Dictionary<string,Dictionary<string,object>> library=LoadCharacterProfileLibrary();
            foreach (string campaignDirectory in campaignDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                campaignCount++;
                string campaignId = Path.GetFileName(campaignDirectory);
                string charactersRoot = Path.Combine(campaignDirectory, "characters");
                if (!Directory.Exists(charactersRoot)) continue;
                foreach (string characterDirectory in Directory.GetDirectories(charactersRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    characterCount++;
                    string heroId = Path.GetFileName(characterDirectory);
                    Dictionary<string, object> runtimeProfile = Program.ReadJsonObject(Path.Combine(characterDirectory, "profile.json"));
                    if(runtimeProfile.Count==0&&library.TryGetValue(heroId,out Dictionary<string,object> shippedProfile))
                        runtimeProfile=DeepCloneProfileDictionary(ReadDictionary(shippedProfile,"sourceFacts"));
                    if(runtimeProfile.Count==0)
                        runtimeProfile=BuildMigrationProfileFromCharacterDirectory(characterDirectory,heroId);
                    if (runtimeProfile.Count == 0)
                    {
                        skippedCount++;
                        results.Add(new Dictionary<string, object>{{"campaignId",campaignId},{"heroStringId",heroId},{"status","skipped"},{"reason","profile.json is unavailable"}});
                        continue;
                    }
                    try
                    {
                        Dictionary<string, object> result;
                        if (!library.ContainsKey(heroId))
                        {
                            Dictionary<string, object> dynamicTraits = Program.ReadJsonObject(Path.Combine(characterDirectory, "traits.json"));
                            if ((ReadDictionary(dynamicTraits, "foundationTraits") ?? new Dictionary<string, object>()).Count == CoreTraitKeys.Length
                                && FoundationTraitModelCurrent(dynamicTraits))
                            {
                                EnsureTraitPercentageData(dynamicTraits, heroId);
                                RebuildPersonalityPortrait(dynamicTraits, ReadString(runtimeProfile, "name", heroId), "", "migrated_dynamic_profile");
                                dynamicTraits["definitions"] = TraitDefinitions();
                                dynamicTraits["assignmentSources"] = CoreTraitAssignmentSources();
                                Program.WriteJsonObject(Path.Combine(characterDirectory, "traits.json"), dynamicTraits);
                                result = new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["status"] = "dynamic_personality_regenerated", ["profileSource"] = "dynamic",
                                    ["packVersion"] = "", ["preservedGeneratedBackground"] = true
                                };
                            }
                            else result = MaterializeCharacterProfile(campaignId, heroId, runtimeProfile, false, false);
                        }
                        else result = MaterializeCharacterProfile(campaignId, heroId, runtimeProfile, false, false);
                        Dictionary<string, object> migratedTraits = Program.ReadJsonObject(Path.Combine(characterDirectory, "traits.json"));
                        Dictionary<string, object> migratedBackground = Program.ReadJsonObject(Path.Combine(characterDirectory, "background.json"));
                        string nativeText = ReadString(runtimeProfile, "nativeEncyclopediaText", "").Trim();
                        bool nativePreserved = string.IsNullOrWhiteSpace(nativeText)
                            || (ReadString(migratedBackground, "summary", "").StartsWith(nativeText, StringComparison.Ordinal)
                                && ReadString(migratedBackground, "encyclopediaText", "").StartsWith(nativeText, StringComparison.Ordinal));
                        bool ready = Program.TraitDocumentReady(migratedTraits) && nativePreserved;
                        if (!ready) failedCount++; else migratedCount++;
                        results.Add(new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["heroStringId"] = heroId,
                            ["status"] = ready ? ReadString(result, "status", "migrated") : "failed_validation",
                            ["profileSource"] = ReadString(result, "profileSource", "dynamic"),
                            ["packVersion"] = ReadString(result, "packVersion", ""),
                            ["traitDocumentVersion"] = ReadInt(migratedTraits, "version", 0),
                            ["foundationModel"] = ReadString(ReadDictionary(migratedTraits, "foundationTraitModel"), "id", ""),
                            ["nativeBackgroundPreserved"] = nativePreserved
                        });
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        results.Add(new Dictionary<string, object>{{"campaignId",campaignId},{"heroStringId",heroId},{"status","failed"},{"error",ex.Message}});
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = failedCount == 0,
                ["foundationModelVersion"] = FoundationTraitModelVersion,
                ["traitPercentageModelVersion"] = TraitPercentageModelVersion,
                ["courtVirtueModelVersion"] = CourtVirtueModelVersion,
                ["courtCharacterModelVersion"] = CourtService.CourtCharacterModelVersion,
                ["profilePackVersion"] = CharacterProfilePackVersion,
                ["campaignCount"] = campaignCount,
                ["characterCount"] = characterCount,
                ["migratedCount"] = migratedCount,
                ["skippedCount"] = skippedCount,
                ["failedCount"] = failedCount,
                ["results"] = results
            };
        }

        private static Dictionary<string,object> BuildMigrationProfileFromCharacterDirectory(string characterDirectory,string heroId)
        {
            Dictionary<string,object> schema=Program.ReadJsonObject(Path.Combine(characterDirectory,"schema_manifest.json"));
            Dictionary<string,object> summary=DeepCloneProfileDictionary(ReadDictionary(schema,"profileSummary"));
            Dictionary<string,object> existingTraits=Program.ReadJsonObject(Path.Combine(characterDirectory,"traits.json"));
            Dictionary<string,object> native=DeepCloneProfileDictionary(ReadDictionary(existingTraits,"visibleBannerlordTraits"));
            Dictionary<string,object> skills=DeepCloneProfileDictionary(ReadDictionary(existingTraits,"nativeSkills"));
            if(summary.Count==0&&native.Count==0&&skills.Count==0)return new Dictionary<string,object>();
            summary["heroStringId"]=heroId;
            summary["characterObjectId"]=heroId;
            summary["name"]=ReadString(summary,"name",heroId);
            summary["traits"]=native;
            summary["skills"]=skills;
            string nativeDescription=ReadString(existingTraits,"nativeDescriptionEvidence","");
            if(!string.IsNullOrWhiteSpace(nativeDescription))summary["nativeEncyclopediaText"]=nativeDescription;
            string archetype=ReadString(Program.ReadJsonObject(Path.Combine(characterDirectory,"template.json")),"archetype","");
            summary["isLord"]=archetype.Equals("lord",StringComparison.OrdinalIgnoreCase);
            summary["isNotable"]=archetype.Equals("notable",StringComparison.OrdinalIgnoreCase);
            summary["isWanderer"]=archetype.Equals("wanderer",StringComparison.OrdinalIgnoreCase);
            return summary;
        }

        internal static Dictionary<string, object> MaterializeCanonicalProfile(string campaignId, string heroId, Dictionary<string, object> runtimeProfile, Dictionary<string, object> shipped, bool forceCanonical, bool rerollHooks)
        {
            var narrative = ReadDictionary(shipped, "narrative");
            bool richNarrative = ReadInt(runtimeProfile, "narrativeVersion", 0) >= 5 && ReadString(narrative, "schema", "") == NarrativeSchema;
            string packVersion = richNarrative ? NarrativePack : CharacterProfilePackVersion;
            if (richNarrative && rerollHooks)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "This character's interests are stable. Edit an interest explicitly or let substantial personal development change it." };
            string metadataPath = Program.CharacterFile(campaignId, heroId, "profile_library.json");
            Dictionary<string, object> currentMetadata = Program.ReadJsonObject(metadataPath);
            bool editorOwned = CharacterHasEditorCorrections(campaignId, heroId);
            bool current = string.Equals(ReadString(currentMetadata, "packVersion", ""), packVersion, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "constructed.json")), "status", ""), "canonical_ready", StringComparison.OrdinalIgnoreCase)
                && Program.TraitDocumentReady(Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json")))
                && (!richNarrative || NarrativeDocumentReady(Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "narrative.json"))));

            if (editorOwned && !forceCanonical)
            {
                Dictionary<string, object> editorTraits = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"));
                if (editorTraits.Count > 0)
                {
                    MarkEditorFoundationTraitsCurrent(editorTraits);
                    editorTraits["definitions"] = TraitDefinitions();
                    editorTraits["assignmentSources"] = CoreTraitAssignmentSources();
                    EnsureTraitPercentageData(editorTraits, heroId);
                    EnsurePersonalityPortraitData(editorTraits, ReadString(runtimeProfile, "name", heroId), false);
                    Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"), editorTraits);
                }
                Dictionary<string, object> preserved = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "editor_authoritative",
                    ["profileSource"] = ReadString(shipped, "source", "shipped"),
                    ["packVersion"] = packVersion,
                    ["editorCorrectionsPreserved"] = true,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                };
                Program.WriteJsonObject(metadataPath, preserved);
                return preserved;
            }

            if (current && !forceCanonical && !rerollHooks)
            {
                currentMetadata["ok"] = true;
                currentMetadata["alreadyCurrent"] = true;
                return currentMetadata;
            }

            Dictionary<string, object> traits = DeepCloneProfileDictionary(ReadDictionary(shipped, "traits"));
            traits["definitions"] = TraitDefinitions();
            traits["assignmentSources"] = CoreTraitAssignmentSources();
            if (!traits.ContainsKey("assignmentProtocol"))
            {
                traits["assignmentProtocol"] = new Dictionary<string, object>
                {
                    ["precedence"] = new ArrayList { "locked native trait", "explicit native description", "weighted native personality traits", "signed native skills", "bounded age and station context" },
                    ["familyRule"] = "Having family is not trait evidence.",
                    ["lockedMappings"] = new Dictionary<string, object> { ["courage"] = "expanded native Valor", ["mercy"] = "expanded native Mercy", ["generosity"] = "expanded native Generosity" }
                };
            }
            Dictionary<string, object> background = DeepCloneProfileDictionary(ReadDictionary(shipped, "background"));
            Program.EnsureEncyclopediaText(background, runtimeProfile, traits);
            Dictionary<string, object> voice = DeepCloneProfileDictionary(ReadDictionary(shipped, "voice"));
            Dictionary<string, object> motivations = DeepCloneProfileDictionary(ReadDictionary(shipped, "motivations"));
            Dictionary<string, object> template = DeepCloneProfileDictionary(ReadDictionary(shipped, "template"));
            Dictionary<string, object> stableHidden = DeepCloneProfileDictionary(ReadDictionary(shipped, "hiddenHistory"));
            Dictionary<string, object> overlay = richNarrative
                ? new Dictionary<string, object>() : LoadOrCreateCampaignProfileOverlay(campaignId, heroId, shipped, rerollHooks);

            Dictionary<string, object> saveQuirks = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["source"] = "campaign_profile_overlay",
                ["secretInterest"] = ReadString(overlay, "secretInterest", ""),
                ["jealousyTargetType"] = ReadString(overlay, "jealousyTargetType", ""),
                ["currentTemptation"] = ReadString(overlay, "currentTemptation", "")
            };
            stableHidden["version"] = 2;
            stableHidden["source"] = "canonical_profile_plus_campaign_overlay";
            stableHidden["campaignPrivateBackstory"] = ReadString(overlay, "privateBackstory", "");
            stableHidden["secretWounds"] = MergeLists(ReadObjectList(stableHidden, "secretWounds"), ReadString(overlay, "secretWound", ""));
            stableHidden["privateEntanglements"] = MergeLists(ReadObjectList(stableHidden, "privateEntanglements"), ReadString(overlay, "privateEntanglement", ""));

            Dictionary<string, object> secrets = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["source"] = "campaign_profile_overlay",
                ["rumors"] = MergeLists(new List<object>(), ReadString(overlay, "rumor", "")),
                ["privateTruths"] = MergeLists(new List<object>(), ReadString(overlay, "privateTruth", ""))
            };
            Dictionary<string, object> pressure = DeepCloneProfileDictionary(ReadDictionary(overlay, "pressure"));
            pressure["version"] = 2;
            pressure["scale"] = "0..100";
            pressure["source"] = "campaign_profile_overlay";

            if (richNarrative)
            {
                var errors = ValidateCharacterNarrative(narrative, true);
                if (errors.Count > 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = string.Join(" ", errors) };
                var projection = new Dictionary<string, object> { ["narrative"] = narrative, ["background"] = background };
                ApplyNarrativePromptProjection(projection);
                background = ReadDictionary(projection, "background");
                voice = ReadDictionary(projection, "voice");
                motivations = ReadDictionary(projection, "motivations");
                saveQuirks = ReadDictionary(projection, "saveQuirks");
                stableHidden = ReadDictionary(projection, "hiddenHistory");
                secrets = ReadDictionary(projection, "secrets");
                overlay = new Dictionary<string, object> { ["source"] = "stable_canonical_narrative", ["rerolled"] = false };
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "campaign_profile_overlay.json"), overlay);
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "narrative.json"), narrative);
            }

            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"), traits);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "background.json"), background);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "voice.json"), voice);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "motivations.json"), motivations);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "template.json"), template);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "save_quirks.json"), saveQuirks);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "hidden_history.json"), stableHidden);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "secrets.json"), secrets);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "pressure.json"), pressure);

            Dictionary<string, object> constructed = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["version"] = 2,
                ["status"] = "canonical_ready",
                ["schemaVersion"] = Program.CharacterSchemaVersion,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["source"] = "shipped_profile_library",
                ["profileSource"] = ReadString(shipped, "source", "shipped"),
                ["packVersion"] = packVersion,
                ["llmUsed"] = false,
                ["constructedUtc"] = DateTime.UtcNow.ToString("o")
            };
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "enrichment.json"), new Dictionary<string, object>
            {
                ["version"] = 1,
                ["status"] = "not_required",
                ["reason"] = "A shipped canonical profile exists.",
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            });

            Dictionary<string, object> metadata = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = "canonical_ready",
                ["profileSource"] = ReadString(shipped, "source", "shipped"),
                ["packVersion"] = packVersion,
                ["catalogHeroId"] = heroId,
                ["campaignOverlayPath"] = Program.CharacterFile(campaignId, heroId, "campaign_profile_overlay.json"),
                ["selectedHooks"] = overlay,
                ["editorCorrectionsPreserved"] = false,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            Program.WriteJsonObject(metadataPath, metadata);
            return metadata;
        }

        private static Dictionary<string, object> EnsureDeterministicBaseline(string campaignId, string heroId, Dictionary<string, object> runtimeProfile)
        {
            Dictionary<string, object> constructed = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "constructed.json"));
            string status = ReadString(constructed, "status", "");
            bool generatedByLlm = ReadBool(constructed, "llmUsed", false)
                && (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "enriched", StringComparison.OrdinalIgnoreCase));
            if ((!Program.IsUsableCharacterStatus(status) && !string.Equals(status, "first_contact_pending", StringComparison.OrdinalIgnoreCase))
                || !Program.TraitDocumentReady(Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"))))
            {
                constructed = Program.ConstructCharacterFiles(campaignId, heroId, runtimeProfile, true, false, "deterministic_baseline");
                constructed["ok"] = true;
                constructed["status"] = "first_contact_pending";
                constructed["source"] = "deterministic_baseline";
                constructed["requiresFirstContactConstruction"] = true;
                constructed["usableForNonInteractiveSystems"] = true;
                constructed["updatedUtc"] = DateTime.UtcNow.ToString("o");
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            }
            else if (!generatedByLlm && (string.Equals(status, "baseline_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "fallback_ready", StringComparison.OrdinalIgnoreCase)))
            {
                constructed["requiresFirstContactConstruction"] = true;
                constructed["usableForNonInteractiveSystems"] = true;
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            }

            Dictionary<string, object> deferred = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "enrichment.json"));
            if (!string.Equals(ReadString(deferred, "status", ""), "completed_on_first_contact", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ReadString(deferred, "status", ""), "completed", StringComparison.OrdinalIgnoreCase))
            {
                deferred = new Dictionary<string, object>
                {
                    ["version"] = 2,
                    ["status"] = "deferred_until_interaction",
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["reason"] = "Dynamic character construction runs synchronously on first meaningful interaction.",
                    ["backgroundProcessingEnabled"] = false,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                };
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "enrichment.json"), deferred);
            }
            Dictionary<string, object> metadata = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = generatedByLlm ? status : "first_contact_pending",
                ["profileSource"] = "deterministic_runtime_baseline",
                ["packVersion"] = "",
                ["enrichment"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "enrichment.json"))
            };
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "profile_library.json"), metadata);
            return metadata;
        }

        private static Dictionary<string, object> LoadOrCreateCampaignProfileOverlay(string campaignId, string heroId, Dictionary<string, object> shipped, bool reroll)
        {
            string path = Program.CharacterFile(campaignId, heroId, "campaign_profile_overlay.json");
            if (!reroll)
            {
                Dictionary<string, object> existing = Program.ReadJsonObject(path);
                if (existing.Count > 0) return existing;
            }

            Dictionary<string, object> pools = ReadDictionary(shipped, "hookPools") ?? new Dictionary<string, object>();
            string nonce = reroll ? Guid.NewGuid().ToString("N") : "initial";
            Dictionary<string, object> overlay = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["packVersion"] = ReadString(shipped, "packVersion", CharacterProfilePackVersion),
                ["selectionNonce"] = nonce,
                ["secretInterest"] = SelectCampaignHook(pools, "secretInterests", campaignId, heroId, nonce),
                ["jealousyTargetType"] = SelectCampaignHook(pools, "jealousyTargetTypes", campaignId, heroId, nonce),
                ["currentTemptation"] = SelectCampaignHook(pools, "currentTemptations", campaignId, heroId, nonce),
                ["privateBackstory"] = SelectCampaignHook(pools, "privateBackstories", campaignId, heroId, nonce),
                ["secretWound"] = SelectCampaignHook(pools, "secretWounds", campaignId, heroId, nonce),
                ["privateEntanglement"] = SelectCampaignHook(pools, "privateEntanglements", campaignId, heroId, nonce),
                ["rumor"] = SelectCampaignHook(pools, "rumors", campaignId, heroId, nonce),
                ["privateTruth"] = SelectCampaignHook(pools, "privateTruths", campaignId, heroId, nonce),
                ["createdUtc"] = DateTime.UtcNow.ToString("o")
            };
            List<Dictionary<string, object>> pressureProfiles = ReadDictionaryList(pools, "pressureProfiles");
            overlay["pressure"] = pressureProfiles.Count == 0
                ? Program.DefaultPressure(ReadDictionary(shipped, "sourceFacts"))
                : DeepCloneProfileDictionary(pressureProfiles[StableIndex(campaignId + "|" + heroId + "|pressure|" + nonce, pressureProfiles.Count)]);
            Program.WriteJsonObject(path, overlay);
            return overlay;
        }

        private static string SelectCampaignHook(Dictionary<string, object> pools, string key, string campaignId, string heroId, string nonce)
        {
            List<string> values = ReadStringList(pools, key).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return values.Count == 0 ? "" : values[StableIndex(campaignId + "|" + heroId + "|" + key + "|" + nonce, values.Count)];
        }

        internal static int StableIndex(string text, int count)
        {
            if (count <= 1) return 0;
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text ?? "") { hash ^= c; hash *= 16777619; }
                return (int)(hash % (uint)count);
            }
        }

        internal static Dictionary<string, object> CharacterProfileLibraryStatusApi()
        {
            Dictionary<string, object> validation = ValidateCharacterProfileLibrary();
            validation["path"] = CharacterProfileCatalogPath;
            validation["manifest"] = ReadDictionary(CharacterProfileLibraryRoot, "manifest") ?? new Dictionary<string, object>();
            return validation;
        }

        internal static Dictionary<string, object> CharacterProfileLibraryActionApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string action = ReadString(payload, "action", "").Trim().ToLowerInvariant();
            Dictionary<string, object> profile = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "profile.json"));
            if (string.IsNullOrWhiteSpace(heroId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            switch (action)
            {
                case "reapply_canonical": return MaterializeCharacterProfile(campaignId, heroId, profile, true, false);
                case "reroll_hooks": return MaterializeCharacterProfile(campaignId, heroId, profile, true, true);
                case "request_enrichment":
                    Dictionary<string, object> generated = Program.ConstructCharacterFiles(campaignId, heroId, profile, true, true, "manual_character_editor");
                    if (Program.IsCharacterConstructionReady(generated)) MarkCharacterConstructionCompletedOnInteraction(campaignId, heroId, "manual_character_editor");
                    return generated;
                default: return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Unknown profile-library action." };
            }
        }

        internal static bool CharacterHasEditorCorrections(string campaignId, string heroId)
        {
            if (File.Exists(Program.CharacterFile(campaignId, heroId, "editor_authority.json"))) return true;
            lock (CharacterEditorAuthorityCacheLock)
            {
                if (!CharacterEditorAuthorityByCampaign.TryGetValue(campaignId, out HashSet<string> corrected))
                {
                    corrected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                            foreach (Dictionary<string, object> row in ReignDatabase.QuerySql(connection, "SELECT DISTINCT hero_id FROM character_editor_revisions;"))
                                corrected.Add(ReadString(row, "hero_id", ""));
                    }
                    catch { }
                    CharacterEditorAuthorityByCampaign[campaignId] = corrected;
                }
                return corrected.Contains(heroId);
            }
        }

        private static void DeferLegacyBackgroundEnrichmentQueues()
        {
            string campaignsRoot = Program.CampaignsRoot();
            if (!Directory.Exists(campaignsRoot)) return;

            int scanned = 0;
            int deferred = 0;
            Dictionary<string, int> previousStatuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(campaignsRoot, "enrichment.json", SearchOption.AllDirectories))
            {
                scanned++;
                Dictionary<string, object> item = Program.ReadJsonObject(path);
                string status = ReadString(item, "status", "");
                if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                previousStatuses[status] = previousStatuses.TryGetValue(status, out int count) ? count + 1 : 1;
                item["version"] = 2;
                item["status"] = "deferred_until_interaction";
                item["previousStatus"] = status;
                item["reason"] = "Automatic background enrichment was retired. Construction will run only before this character's first interactive response.";
                item["backgroundProcessingEnabled"] = false;
                item["migratedUtc"] = DateTime.UtcNow.ToString("o");
                item["updatedUtc"] = DateTime.UtcNow.ToString("o");
                Program.WriteJsonObject(path, item);
                deferred++;
            }

            Program.LogOperational("character_enrichment.background_disabled", new Dictionary<string, object>
            {
                ["workerStarted"] = false,
                ["scannedRecords"] = scanned,
                ["deferredRecords"] = deferred,
                ["previousStatuses"] = previousStatuses,
                ["policy"] = "first_interaction_only"
            });
        }

        internal static bool RequiresFirstContactConstruction(string campaignId, string heroId, Dictionary<string, object> constructed)
        {
            if (LoadCharacterProfileLibrary().ContainsKey(heroId)) return false;
            if (CharacterHasEditorCorrections(campaignId, heroId)) return false;

            if (ReadInt(Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "profile.json")), "narrativeVersion", 0) >= 5)
                return !NarrativeDocumentReady(Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "narrative.json")));

            string status = ReadString(constructed, "status", "");
            if (ReadBool(constructed, "llmUsed", false)
                && (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "enriched", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string source = ReadString(constructed, "source", "");
            Dictionary<string, object> metadata = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "profile_library.json"));
            return string.Equals(status, "first_contact_pending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "baseline_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "fallback_ready", StringComparison.OrdinalIgnoreCase)
                || source.IndexOf("deterministic_baseline", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(ReadString(metadata, "profileSource", ""), "deterministic_runtime_baseline", StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> LoadPassiveCharacterStack(string campaignId, string heroId, Dictionary<string, object> runtimeProfile)
        {
            runtimeProfile = WithNarrativeCampaignVersion(campaignId, runtimeProfile);
            string characterDir = Program.CharacterDirectory(campaignId, heroId);
            if (Directory.Exists(characterDir)
                && (File.Exists(Program.CharacterFile(campaignId, heroId, "constructed.json"))
                    || File.Exists(Program.CharacterFile(campaignId, heroId, "profile.json"))))
            {
                return Program.LoadCharacterStack(campaignId, heroId);
            }

            Dictionary<string, Dictionary<string, object>> library = CharacterProfileLibraryFor(runtimeProfile);
            if (library.TryGetValue(heroId ?? string.Empty, out Dictionary<string, object> shipped))
            {
                return new Dictionary<string, object>
                {
                    ["profileSource"] = "shipped_catalog_read_only",
                    ["narrative"] = DeepCloneProfileDictionary(ReadDictionary(shipped, "narrative")),
                    ["traits"] = DeepCloneProfileDictionary(ReadDictionary(shipped, "traits")),
                    ["background"] = DeepCloneProfileDictionary(ReadDictionary(shipped, "background")),
                    ["voice"] = DeepCloneProfileDictionary(ReadDictionary(shipped, "voice")),
                    ["motivations"] = DeepCloneProfileDictionary(ReadDictionary(shipped, "motivations")),
                    ["hiddenHistory"] = DeepCloneProfileDictionary(ReadDictionary(shipped, "hiddenHistory"))
                };
            }

            runtimeProfile = runtimeProfile ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["profileSource"] = "live_runtime_unconstructed",
                ["constructionDeferredUntilFirstInteraction"] = true,
                ["name"] = ReadString(runtimeProfile, "name", heroId),
                ["occupation"] = ReadString(runtimeProfile, "occupation", "person"),
                ["culture"] = ReadFirstString(runtimeProfile, "cultureName", "culture"),
                ["clan"] = ReadFirstString(runtimeProfile, "clanName", "clan"),
                ["kingdom"] = ReadFirstString(runtimeProfile, "kingdomName", "kingdom"),
                ["nativeTraits"] = DeepCloneProfileDictionary(ReadDictionary(runtimeProfile, "traits")),
                ["nativeBiography"] = ReadFirstString(runtimeProfile, "biography", "nativeBiography", "description")
            };
        }

        internal static void MarkCharacterConstructionCompletedOnInteraction(string campaignId, string heroId, string source)
        {
            Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "enrichment.json"), new Dictionary<string, object>
            {
                ["version"] = 2,
                ["status"] = "completed_on_first_contact",
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["source"] = source ?? "interactive",
                ["backgroundProcessingEnabled"] = false,
                ["completedUtc"] = DateTime.UtcNow.ToString("o"),
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            });
        }

        internal static Dictionary<string, object> CharacterConstructionPolicyStatus()
        {
            Dictionary<string, int> statuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string campaignsRoot = Program.CampaignsRoot();
            if (Directory.Exists(campaignsRoot))
            {
                foreach (string path in Directory.GetFiles(campaignsRoot, "enrichment.json", SearchOption.AllDirectories))
                {
                    string status = ReadString(Program.ReadJsonObject(path), "status", "missing");
                    statuses[status] = statuses.TryGetValue(status, out int count) ? count + 1 : 1;
                }
            }

            return new Dictionary<string, object>
            {
                ["policy"] = "town_entry_full_profile_prewarm_with_first_contact_fallback",
                ["backgroundWorkerStarted"] = TownProfiles.Count > 0,
                ["automaticProfileUploadsEnabled"] = false,
                ["legacyQueueStatuses"] = statuses,
                ["pendingBackgroundCalls"] = Program.PendingCharacterEnrichmentCount()
            };
        }

        internal static Dictionary<string, object> ValidateCharacterProfileLibrary(bool includeCourt = true)
        {
            Dictionary<string, Dictionary<string, object>> profiles = LoadCharacterProfileLibrary();
            List<Dictionary<string, object>> profileRows = ReadDictionaryList(CharacterProfileLibraryRoot, "profiles");
            if (!includeCourt)
            {
                profiles = profiles.Where(pair => !ReadString(pair.Value, "source", "").Equals("reign_court", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                profileRows = profileRows.Where(row => !ReadString(row, "source", "").Equals("reign_court", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            List<string> errors = new List<string>();
            Dictionary<string,List<int>> courtVirtueScores=CourtVirtueKeys.ToDictionary(x=>x,x=>new List<int>(),StringComparer.OrdinalIgnoreCase);
            int duplicateIds = profileRows.GroupBy(x => ReadString(x, "heroStringId", ""), StringComparer.OrdinalIgnoreCase).Count(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1);
            if (duplicateIds > 0) errors.Add("Catalog contains " + duplicateIds + " blank or duplicate hero IDs.");
            foreach (KeyValuePair<string, Dictionary<string, object>> pair in profiles)
            {
                Dictionary<string, object> traitDocument = ReadDictionary(pair.Value, "traits") ?? new Dictionary<string, object>();
                Dictionary<string, object> traits = ReadDictionary(traitDocument, "foundationTraits") ?? new Dictionary<string, object>();
                Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages") ?? new Dictionary<string, object>();
                Dictionary<string, object> profileMbti = ReadDictionary(pair.Value, "mbtiProfile")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> traitMbti = ReadDictionary(traitDocument, "mbtiProfile")
                    ?? new Dictionary<string, object>();
                string immutableMbtiType = ReadString(profileMbti, "type", "XXXX");
                if (!RelationshipsService.MbtiDefinitions.ContainsKey(immutableMbtiType))
                    errors.Add(pair.Key + " has no valid immutable pregenerated MBTI type.");
                if (!immutableMbtiType.Equals(ReadString(traitMbti, "type", "XXXX"), StringComparison.OrdinalIgnoreCase))
                    errors.Add(pair.Key + " has mismatched root and trait MBTI assignments.");
                if (!ReadBool(profileMbti, "immutable", false)
                    || !ReadString(profileMbti, "source", "").Equals("pregenerated_noble_catalog", StringComparison.OrdinalIgnoreCase))
                    errors.Add(pair.Key + " does not identify its MBTI as immutable pregenerated catalog data.");
                if (traits.Count != CoreTraitKeys.Length) errors.Add(pair.Key + " has " + traits.Count + " traits; expected " + CoreTraitKeys.Length + ".");
                foreach (string key in CoreTraitKeys)
                {
                    if (!traits.ContainsKey(key)) errors.Add(pair.Key + " is missing " + key + ".");
                    else if (ReadInt(traits, key, 99) < -2 || ReadInt(traits, key, 99) > 2) errors.Add(pair.Key + " has an invalid " + key + " value.");
                    if (!percentages.ContainsKey(key)) errors.Add(pair.Key + " is missing the " + key + " percentage.");
                    else
                    {
                        TraitPercentageBand(ReadInt(traits, key, 0), out int minimum, out int maximum);
                        int percentage = ReadInt(percentages, key, -1);
                        if (percentage < minimum || percentage > maximum) errors.Add(pair.Key + " has an out-of-band " + key + " percentage.");
                    }
                }
                Dictionary<string,object> courtVirtues=ReadDictionary(traitDocument,"courtVirtues")??new Dictionary<string,object>();
                Dictionary<string,object> courtVirtueModel=ReadDictionary(traitDocument,"courtVirtueModel")??new Dictionary<string,object>();
                Dictionary<string,object> courtCharacter=ReadDictionary(traitDocument,"courtCharacter")??new Dictionary<string,object>();
                Dictionary<string,object> courtCharacterModel=ReadDictionary(traitDocument,"courtCharacterModel")??new Dictionary<string,object>();
                if(courtVirtues.Count!=CourtVirtueKeys.Length||CourtVirtueKeys.Any(x=>!courtVirtues.ContainsKey(x))) errors.Add(pair.Key+" does not contain all seven court virtues.");
                if(ReadInt(courtVirtueModel,"version",0)!=CourtVirtueModelVersion) errors.Add(pair.Key+" does not use court virtue model v"+CourtVirtueModelVersion+".");
                if(ReadInt(courtCharacterModel,"version",0)!=CourtService.CourtCharacterModelVersion) errors.Add(pair.Key+" does not use Court Character model v"+CourtService.CourtCharacterModelVersion+".");
                Dictionary<string,object> expectedCourtCharacter=CourtService.BuildCourtCharacterData(traitDocument,ReadDictionary(pair.Value,"sourceFacts")??pair.Value);
                if(!CanonicalJson(courtCharacter).Equals(CanonicalJson(expectedCourtCharacter),StringComparison.Ordinal)) errors.Add(pair.Key+" Court Character does not match its fixed Honor-Boldness cell.");
                if(traitDocument.ContainsKey("courtVirtueRawScores")) errors.Add(pair.Key+" persists a deprecated raw court virtue score field.");
                Dictionary<string,object> calculatedVirtues=CalculateCourtVirtues(percentages);
                foreach(string virtue in CourtVirtueKeys)
                {
                    if(!courtVirtues.ContainsKey(virtue))continue;int score=ReadInt(courtVirtues,virtue,-1);
                    if(score<0||score>100)errors.Add(pair.Key+" has an out-of-range "+virtue+" court virtue score.");
                    else courtVirtueScores[virtue].Add(score);
                    if(score!=ReadInt(calculatedVirtues,virtue,int.MinValue))errors.Add(pair.Key+" has a stale or incorrect "+virtue+" court virtue score.");
                }
                Dictionary<string, object> native = ReadDictionary(ReadDictionary(pair.Value, "sourceFacts"), "traits") ?? new Dictionary<string, object>();
                if (Program.TraitInt(traits, "courage") != (int)NativeFoundationSignalVNext(Program.TraitInt(native, "valor"))) errors.Add(pair.Key + " does not expand native Valor into Courage.");
                if (Program.TraitInt(traits, "mercy") != (int)NativeFoundationSignalVNext(Program.TraitInt(native, "mercy"))) errors.Add(pair.Key + " does not expand native Mercy.");
                if (Program.TraitInt(traits, "generosity") != (int)NativeFoundationSignalVNext(Program.TraitInt(native, "generosity"))) errors.Add(pair.Key + " does not expand native Generosity.");
                if (!FoundationTraitModelCurrent(traitDocument)) errors.Add(pair.Key + " does not use the active foundation trait model.");
                Dictionary<string,object> percentageModel=ReadDictionary(traitDocument,"traitPercentageModel")??new Dictionary<string,object>();
                if(ReadInt(percentageModel,"version",0)!=TraitPercentageModelVersion) errors.Add(pair.Key+" does not use trait percentage model v"+TraitPercentageModelVersion+".");
                if(!PersonalityPortraitReady(traitDocument)) errors.Add(pair.Key+" does not have a current rounded personality portrait.");
                if (string.IsNullOrWhiteSpace(ReadString(ReadDictionary(pair.Value, "background"), "summary", ""))) errors.Add(pair.Key + " has no background summary.");
                if (string.IsNullOrWhiteSpace(ReadString(ReadDictionary(pair.Value, "voice"), "speechStyle", ""))) errors.Add(pair.Key + " has no speech style.");
                if (ReadObjectList(ReadDictionary(pair.Value, "motivations"), "dreams").Count == 0) errors.Add(pair.Key + " has no dreams.");
                string publicText = ReadString(ReadDictionary(pair.Value, "background"), "encyclopediaText", "").ToLowerInvariant();
                if (new[] { "secret wound", "private truth", "jealousy target", "pressure meter", "blackmail" }.Any(publicText.Contains)) errors.Add(pair.Key + " leaks private profile data into encyclopedia text.");
                string nativeText=ReadString(ReadDictionary(pair.Value,"sourceFacts"),"nativeEncyclopediaText","");
                Dictionary<string,object> profileBackground=ReadDictionary(pair.Value,"background")??new Dictionary<string,object>();
                string encyclopediaText=ReadString(profileBackground,"encyclopediaText","");
                string backgroundSummary=ReadString(profileBackground,"summary","");
                if(ReadString(pair.Value,"source","").Equals("native_sandbox",StringComparison.OrdinalIgnoreCase)
                    &&!string.IsNullOrWhiteSpace(nativeText)
                    &&(!PreservesNativeBackgroundPrefix(encyclopediaText,nativeText)
                        ||!PreservesNativeBackgroundPrefix(backgroundSummary,nativeText)))
                    errors.Add(pair.Key+" does not preserve its authored native background verbatim before generated material.");
            }
            Dictionary<string, object> manifest = ReadDictionary(CharacterProfileLibraryRoot, "manifest") ?? new Dictionary<string, object>();
            int expected = includeCourt
                ? ReadInt(manifest, "expectedProfileCount", profiles.Count)
                : ReadInt(manifest, "nativeSandboxCount", profiles.Count);
            if (profiles.Count != expected) errors.Insert(0, "Catalog contains " + profiles.Count + " profiles; manifest expects " + expected + ".");
            int nativeExpected = ReadInt(manifest, "nativeSandboxCount", 0), courtExpected = ReadInt(manifest, "reignCourtCount", 0);
            int nativeCount = profiles.Values.Count(x => ReadString(x, "source", "") == "native_sandbox");
            int courtCount = profiles.Values.Count(x => ReadString(x, "source", "") == "reign_court");
            if (nativeCount != nativeExpected) errors.Add("Expected " + nativeExpected + " native Sandbox nobles; found " + nativeCount + ".");
            if (includeCourt && courtCount != courtExpected) errors.Add("Expected " + courtExpected + " Reign court nobles; found " + courtCount + ".");
            if (includeCourt) foreach (string role in new[] { "father", "mother", "child_1", "child_2", "child_3", "child_4" })
            {
                int count = profiles.Values.Count(x => ReadString(ReadDictionary(x, "sourceFacts"), "householdRole", "") == role);
                int roleExpected = ReadInt(manifest, "courtHouseholdCount", 0);
                if (count != roleExpected) errors.Add("Expected " + roleExpected + " court profiles for role " + role + "; found " + count + ".");
            }
            int duplicateSummaries = profiles.Values.GroupBy(x => ReadString(ReadDictionary(x, "traits"), "basePersonalitySummary", ""), StringComparer.Ordinal).Count(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1);
            if (duplicateSummaries > 0) errors.Add("Catalog contains " + duplicateSummaries + " blank or cloned base-personality summaries.");
            Dictionary<string,object> courtVirtueDistribution=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);int globalNegativeExtreme=0,globalPositiveExtreme=0;
            foreach(string virtue in CourtVirtueKeys)
            {
                List<int> scores=courtVirtueScores[virtue];int negative=scores.Count(x=>x<=CourtVirtueNegativeExtremeMaximum),positive=scores.Count(x=>x>=CourtVirtuePositiveExtremeMinimum),middle=scores.Count(x=>x>CourtVirtueNegativeExtremeMaximum&&x<CourtVirtuePositiveExtremeMinimum);globalNegativeExtreme+=negative;globalPositiveExtreme+=positive;
                courtVirtueDistribution[virtue]=new Dictionary<string,object>{{"count",scores.Count},{"minimum",scores.Count==0?-1:scores.Min()},{"maximum",scores.Count==0?-1:scores.Max()},{"middleCount",middle},{"middlePercent",scores.Count==0?0d:Math.Round(100d*middle/scores.Count,2)},{"negativeExtremeCount",negative},{"positiveExtremeCount",positive}};
                if(includeCourt&&scores.Count>0&&middle<=scores.Count*0.50d)errors.Add(virtue+" does not leave a majority of catalog characters in the 21..79 middle range.");
            }
            if(includeCourt&&globalNegativeExtreme==0)errors.Add("Catalog contains no evidence-driven court virtue score in the 0..20 negative extreme band.");
            if(includeCourt&&globalPositiveExtreme==0)errors.Add("Catalog contains no evidence-driven court virtue score in the 80..100 positive extreme band.");
            IEnumerable<string> goldenIds = new[] { "lord_5_3", "lord_1_47", "dead_lord_2_1" };
            if (includeCourt) goldenIds = goldenIds.Concat(new[] { "reign_court_castle_EN1_father", "reign_court_castle_EN1_mother", "reign_court_castle_EN1_child_1", "reign_court_castle_EN1_child_4" });
            foreach (string goldenId in goldenIds)
                if (!profiles.ContainsKey(goldenId)) errors.Add("Golden profile is missing: " + goldenId + ".");
            return new Dictionary<string, object>
            {
                ["ok"] = errors.Count == 0,
                ["packVersion"] = ReadString(CharacterProfileLibraryRoot, "packVersion", ""),
                ["profileCount"] = profiles.Count,
                ["expectedProfileCount"] = expected,
                ["courtExcluded"] = !includeCourt,
                ["traitCount"] = CoreTraitKeys.Length,
                ["courtVirtueModelVersion"] = CourtVirtueModelVersion,
                ["courtCharacterModelVersion"] = CourtService.CourtCharacterModelVersion,
                ["courtVirtueDistribution"] = courtVirtueDistribution,
                ["negativeExtremeCount"] = globalNegativeExtreme,
                ["positiveExtremeCount"] = globalPositiveExtreme,
                ["errors"] = errors.Take(200).ToList(),
                ["errorCount"] = errors.Count
            };
        }

        private static bool PreservesNativeBackgroundPrefix(string text,string nativeText)
        {
            string prefix=(nativeText??"").Trim();
            if(string.IsNullOrWhiteSpace(prefix)||string.IsNullOrWhiteSpace(text)
                ||!text.StartsWith(prefix,StringComparison.Ordinal))return false;
            return text.Length==prefix.Length||char.IsWhiteSpace(text[prefix.Length]);
        }
    }
}
