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
    internal static partial class Program
    {
        private const string CharacterProfilePackVersion = "reign_profiles_v4";
        private static readonly string CharacterProfileCatalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProfileLibrary", "profile_catalog.json");
        private static readonly object CharacterProfileLibraryLock = new object();
        private static Dictionary<string, object> CharacterProfileLibraryRoot;
        private static Dictionary<string, Dictionary<string, object>> CharacterProfilesById;
        private static Dictionary<string, Dictionary<string, object>> LegacyCharacterProfilesById;
        private static readonly object CharacterEditorAuthorityCacheLock = new object();
        private static readonly Dictionary<string, HashSet<string>> CharacterEditorAuthorityByCampaign = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static void InitializeCharacterProfileLibrary()
        {
            LoadCharacterProfileLibrary();
            DeferLegacyBackgroundEnrichmentQueues();
        }

        private static Dictionary<string, Dictionary<string, object>> LoadCharacterProfileLibrary()
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

                LogOperational("character_profile_library.loaded", new Dictionary<string, object>
                {
                    ["path"] = CharacterProfileCatalogPath,
                    ["exists"] = File.Exists(CharacterProfileCatalogPath),
                    ["profileCount"] = CharacterProfilesById.Count,
                    ["packVersion"] = ReadString(CharacterProfileLibraryRoot, "packVersion", "")
                });
                return CharacterProfilesById;
            }
        }

        private static Dictionary<string, object> MaterializeCharacterProfile(string campaignId, string heroId, Dictionary<string, object> runtimeProfile, bool forceCanonical, bool rerollHooks)
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

        private static Dictionary<string, Dictionary<string, object>> CharacterProfileLibraryFor(Dictionary<string, object> runtimeProfile)
        {
            var library = LoadCharacterProfileLibrary();
            if (ReadInt(runtimeProfile, "narrativeVersion", 0) < 5 && ReadString(CharacterProfileLibraryRoot, "packVersion", "") == NarrativePack)
            {
                lock (CharacterProfileLibraryLock)
                {
                    if (LegacyCharacterProfilesById == null)
                        LegacyCharacterProfilesById = ReadDictionaryList(ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "ProfileLibrary", "profile_catalog_v4.json")), "profiles").ToDictionary(x => ReadString(x, "heroStringId", ""), StringComparer.OrdinalIgnoreCase);
                    library = LegacyCharacterProfilesById;
                }
            }
            return library;
        }

        private static Dictionary<string, object> WithNarrativeCampaignVersion(string campaignId, Dictionary<string, object> profile)
        {
            if (profile != null && profile.ContainsKey("narrativeVersion")) return profile;
            var result = new Dictionary<string, object>(profile ?? new Dictionary<string, object>());
            result["narrativeVersion"] = ReadInt(ReadJsonObject(CampaignFile(campaignId, "campaign.json")), "narrativeVersion", 0);
            return result;
        }

        private static Dictionary<string, object> MigrateCharacterProfilesToCurrentModels(string[] args)
        {
            string requestedCampaign = ProfileArg(args, "--campaign-id", "");
            string campaignsRoot = CampaignsRoot();
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
                    Dictionary<string, object> runtimeProfile = ReadJsonObject(Path.Combine(characterDirectory, "profile.json"));
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
                            Dictionary<string, object> dynamicTraits = ReadJsonObject(Path.Combine(characterDirectory, "traits.json"));
                            if ((ReadDictionary(dynamicTraits, "foundationTraits") ?? new Dictionary<string, object>()).Count == CoreTraitKeys.Length
                                && FoundationTraitModelCurrent(dynamicTraits))
                            {
                                EnsureTraitPercentageData(dynamicTraits, heroId);
                                RebuildPersonalityPortrait(dynamicTraits, ReadString(runtimeProfile, "name", heroId), "", "migrated_dynamic_profile");
                                dynamicTraits["definitions"] = TraitDefinitions();
                                dynamicTraits["assignmentSources"] = CoreTraitAssignmentSources();
                                WriteJsonObject(Path.Combine(characterDirectory, "traits.json"), dynamicTraits);
                                result = new Dictionary<string, object>
                                {
                                    ["ok"] = true, ["status"] = "dynamic_personality_regenerated", ["profileSource"] = "dynamic",
                                    ["packVersion"] = "", ["preservedGeneratedBackground"] = true
                                };
                            }
                            else result = MaterializeCharacterProfile(campaignId, heroId, runtimeProfile, false, false);
                        }
                        else result = MaterializeCharacterProfile(campaignId, heroId, runtimeProfile, false, false);
                        Dictionary<string, object> migratedTraits = ReadJsonObject(Path.Combine(characterDirectory, "traits.json"));
                        Dictionary<string, object> migratedBackground = ReadJsonObject(Path.Combine(characterDirectory, "background.json"));
                        string nativeText = ReadString(runtimeProfile, "nativeEncyclopediaText", "").Trim();
                        bool nativePreserved = string.IsNullOrWhiteSpace(nativeText)
                            || (ReadString(migratedBackground, "summary", "").StartsWith(nativeText, StringComparison.Ordinal)
                                && ReadString(migratedBackground, "encyclopediaText", "").StartsWith(nativeText, StringComparison.Ordinal));
                        bool ready = TraitDocumentReady(migratedTraits) && nativePreserved;
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
                ["courtCharacterModelVersion"] = CourtCharacterModelVersion,
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
            Dictionary<string,object> schema=ReadJsonObject(Path.Combine(characterDirectory,"schema_manifest.json"));
            Dictionary<string,object> summary=DeepCloneProfileDictionary(ReadDictionary(schema,"profileSummary"));
            Dictionary<string,object> existingTraits=ReadJsonObject(Path.Combine(characterDirectory,"traits.json"));
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
            string archetype=ReadString(ReadJsonObject(Path.Combine(characterDirectory,"template.json")),"archetype","");
            summary["isLord"]=archetype.Equals("lord",StringComparison.OrdinalIgnoreCase);
            summary["isNotable"]=archetype.Equals("notable",StringComparison.OrdinalIgnoreCase);
            summary["isWanderer"]=archetype.Equals("wanderer",StringComparison.OrdinalIgnoreCase);
            return summary;
        }

        private static Dictionary<string, object> MaterializeCanonicalProfile(string campaignId, string heroId, Dictionary<string, object> runtimeProfile, Dictionary<string, object> shipped, bool forceCanonical, bool rerollHooks)
        {
            var narrative = ReadDictionary(shipped, "narrative");
            bool richNarrative = ReadInt(runtimeProfile, "narrativeVersion", 0) >= 5 && ReadString(narrative, "schema", "") == NarrativeSchema;
            string packVersion = richNarrative ? NarrativePack : CharacterProfilePackVersion;
            if (richNarrative && rerollHooks)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "This character's interests are stable. Edit an interest explicitly or let substantial personal development change it." };
            string metadataPath = CharacterFile(campaignId, heroId, "profile_library.json");
            Dictionary<string, object> currentMetadata = ReadJsonObject(metadataPath);
            bool editorOwned = CharacterHasEditorCorrections(campaignId, heroId);
            bool current = string.Equals(ReadString(currentMetadata, "packVersion", ""), packVersion, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(ReadJsonObject(CharacterFile(campaignId, heroId, "constructed.json")), "status", ""), "canonical_ready", StringComparison.OrdinalIgnoreCase)
                && TraitDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json")))
                && (!richNarrative || NarrativeDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "narrative.json"))));

            if (editorOwned && !forceCanonical)
            {
                Dictionary<string, object> editorTraits = ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
                if (editorTraits.Count > 0)
                {
                    MarkEditorFoundationTraitsCurrent(editorTraits);
                    editorTraits["definitions"] = TraitDefinitions();
                    editorTraits["assignmentSources"] = CoreTraitAssignmentSources();
                    EnsureTraitPercentageData(editorTraits, heroId);
                    EnsurePersonalityPortraitData(editorTraits, ReadString(runtimeProfile, "name", heroId), false);
                    WriteJsonObject(CharacterFile(campaignId, heroId, "traits.json"), editorTraits);
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
                WriteJsonObject(metadataPath, preserved);
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
            EnsureEncyclopediaText(background, runtimeProfile, traits);
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
                WriteJsonObject(CharacterFile(campaignId, heroId, "campaign_profile_overlay.json"), overlay);
                WriteJsonObject(CharacterFile(campaignId, heroId, "narrative.json"), narrative);
            }

            WriteJsonObject(CharacterFile(campaignId, heroId, "traits.json"), traits);
            WriteJsonObject(CharacterFile(campaignId, heroId, "background.json"), background);
            WriteJsonObject(CharacterFile(campaignId, heroId, "voice.json"), voice);
            WriteJsonObject(CharacterFile(campaignId, heroId, "motivations.json"), motivations);
            WriteJsonObject(CharacterFile(campaignId, heroId, "template.json"), template);
            WriteJsonObject(CharacterFile(campaignId, heroId, "save_quirks.json"), saveQuirks);
            WriteJsonObject(CharacterFile(campaignId, heroId, "hidden_history.json"), stableHidden);
            WriteJsonObject(CharacterFile(campaignId, heroId, "secrets.json"), secrets);
            WriteJsonObject(CharacterFile(campaignId, heroId, "pressure.json"), pressure);

            Dictionary<string, object> constructed = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["version"] = 2,
                ["status"] = "canonical_ready",
                ["schemaVersion"] = CharacterSchemaVersion,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["source"] = "shipped_profile_library",
                ["profileSource"] = ReadString(shipped, "source", "shipped"),
                ["packVersion"] = packVersion,
                ["llmUsed"] = false,
                ["constructedUtc"] = DateTime.UtcNow.ToString("o")
            };
            WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            WriteJsonObject(CharacterFile(campaignId, heroId, "enrichment.json"), new Dictionary<string, object>
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
                ["campaignOverlayPath"] = CharacterFile(campaignId, heroId, "campaign_profile_overlay.json"),
                ["selectedHooks"] = overlay,
                ["editorCorrectionsPreserved"] = false,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            WriteJsonObject(metadataPath, metadata);
            return metadata;
        }

        private static Dictionary<string, object> EnsureDeterministicBaseline(string campaignId, string heroId, Dictionary<string, object> runtimeProfile)
        {
            Dictionary<string, object> constructed = ReadJsonObject(CharacterFile(campaignId, heroId, "constructed.json"));
            string status = ReadString(constructed, "status", "");
            bool generatedByLlm = ReadBool(constructed, "llmUsed", false)
                && (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "enriched", StringComparison.OrdinalIgnoreCase));
            if ((!IsUsableCharacterStatus(status) && !string.Equals(status, "first_contact_pending", StringComparison.OrdinalIgnoreCase))
                || !TraitDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"))))
            {
                constructed = ConstructCharacterFiles(campaignId, heroId, runtimeProfile, true, false, "deterministic_baseline");
                constructed["ok"] = true;
                constructed["status"] = "first_contact_pending";
                constructed["source"] = "deterministic_baseline";
                constructed["requiresFirstContactConstruction"] = true;
                constructed["usableForNonInteractiveSystems"] = true;
                constructed["updatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            }
            else if (!generatedByLlm && (string.Equals(status, "baseline_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "fallback_ready", StringComparison.OrdinalIgnoreCase)))
            {
                constructed["requiresFirstContactConstruction"] = true;
                constructed["usableForNonInteractiveSystems"] = true;
                WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            }

            Dictionary<string, object> deferred = ReadJsonObject(CharacterFile(campaignId, heroId, "enrichment.json"));
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
                WriteJsonObject(CharacterFile(campaignId, heroId, "enrichment.json"), deferred);
            }
            Dictionary<string, object> metadata = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = generatedByLlm ? status : "first_contact_pending",
                ["profileSource"] = "deterministic_runtime_baseline",
                ["packVersion"] = "",
                ["enrichment"] = ReadJsonObject(CharacterFile(campaignId, heroId, "enrichment.json"))
            };
            WriteJsonObject(CharacterFile(campaignId, heroId, "profile_library.json"), metadata);
            return metadata;
        }

        private static Dictionary<string, object> LoadOrCreateCampaignProfileOverlay(string campaignId, string heroId, Dictionary<string, object> shipped, bool reroll)
        {
            string path = CharacterFile(campaignId, heroId, "campaign_profile_overlay.json");
            if (!reroll)
            {
                Dictionary<string, object> existing = ReadJsonObject(path);
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
                ? DefaultPressure(ReadDictionary(shipped, "sourceFacts"))
                : DeepCloneProfileDictionary(pressureProfiles[StableIndex(campaignId + "|" + heroId + "|pressure|" + nonce, pressureProfiles.Count)]);
            WriteJsonObject(path, overlay);
            return overlay;
        }

        private static string SelectCampaignHook(Dictionary<string, object> pools, string key, string campaignId, string heroId, string nonce)
        {
            List<string> values = ReadStringList(pools, key).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return values.Count == 0 ? "" : values[StableIndex(campaignId + "|" + heroId + "|" + key + "|" + nonce, values.Count)];
        }

        private static int StableIndex(string text, int count)
        {
            if (count <= 1) return 0;
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text ?? "") { hash ^= c; hash *= 16777619; }
                return (int)(hash % (uint)count);
            }
        }

        private static Dictionary<string, object> CharacterProfileLibraryStatusApi()
        {
            Dictionary<string, object> validation = ValidateCharacterProfileLibrary();
            validation["path"] = CharacterProfileCatalogPath;
            validation["manifest"] = ReadDictionary(CharacterProfileLibraryRoot, "manifest") ?? new Dictionary<string, object>();
            return validation;
        }

        private static Dictionary<string, object> CharacterProfileLibraryActionApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string action = ReadString(payload, "action", "").Trim().ToLowerInvariant();
            Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json"));
            if (string.IsNullOrWhiteSpace(heroId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            switch (action)
            {
                case "reapply_canonical": return MaterializeCharacterProfile(campaignId, heroId, profile, true, false);
                case "reroll_hooks": return MaterializeCharacterProfile(campaignId, heroId, profile, true, true);
                case "request_enrichment":
                    Dictionary<string, object> generated = ConstructCharacterFiles(campaignId, heroId, profile, true, true, "manual_character_editor");
                    if (IsCharacterConstructionReady(generated)) MarkCharacterConstructionCompletedOnInteraction(campaignId, heroId, "manual_character_editor");
                    return generated;
                default: return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Unknown profile-library action." };
            }
        }

        private static bool CharacterHasEditorCorrections(string campaignId, string heroId)
        {
            if (File.Exists(CharacterFile(campaignId, heroId, "editor_authority.json"))) return true;
            lock (CharacterEditorAuthorityCacheLock)
            {
                if (!CharacterEditorAuthorityByCampaign.TryGetValue(campaignId, out HashSet<string> corrected))
                {
                    corrected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                            foreach (Dictionary<string, object> row in QuerySql(connection, "SELECT DISTINCT hero_id FROM character_editor_revisions;"))
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
            string campaignsRoot = CampaignsRoot();
            if (!Directory.Exists(campaignsRoot)) return;

            int scanned = 0;
            int deferred = 0;
            Dictionary<string, int> previousStatuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(campaignsRoot, "enrichment.json", SearchOption.AllDirectories))
            {
                scanned++;
                Dictionary<string, object> item = ReadJsonObject(path);
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
                WriteJsonObject(path, item);
                deferred++;
            }

            LogOperational("character_enrichment.background_disabled", new Dictionary<string, object>
            {
                ["workerStarted"] = false,
                ["scannedRecords"] = scanned,
                ["deferredRecords"] = deferred,
                ["previousStatuses"] = previousStatuses,
                ["policy"] = "first_interaction_only"
            });
        }

        private static bool RequiresFirstContactConstruction(string campaignId, string heroId, Dictionary<string, object> constructed)
        {
            if (LoadCharacterProfileLibrary().ContainsKey(heroId)) return false;
            if (CharacterHasEditorCorrections(campaignId, heroId)) return false;

            if (ReadInt(ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json")), "narrativeVersion", 0) >= 5)
                return !NarrativeDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "narrative.json")));

            string status = ReadString(constructed, "status", "");
            if (ReadBool(constructed, "llmUsed", false)
                && (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "enriched", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string source = ReadString(constructed, "source", "");
            Dictionary<string, object> metadata = ReadJsonObject(CharacterFile(campaignId, heroId, "profile_library.json"));
            return string.Equals(status, "first_contact_pending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "baseline_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "fallback_ready", StringComparison.OrdinalIgnoreCase)
                || source.IndexOf("deterministic_baseline", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(ReadString(metadata, "profileSource", ""), "deterministic_runtime_baseline", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> LoadPassiveCharacterStack(string campaignId, string heroId, Dictionary<string, object> runtimeProfile)
        {
            runtimeProfile = WithNarrativeCampaignVersion(campaignId, runtimeProfile);
            string characterDir = CharacterDirectory(campaignId, heroId);
            if (Directory.Exists(characterDir)
                && (File.Exists(CharacterFile(campaignId, heroId, "constructed.json"))
                    || File.Exists(CharacterFile(campaignId, heroId, "profile.json"))))
            {
                return LoadCharacterStack(campaignId, heroId);
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

        private static void MarkCharacterConstructionCompletedOnInteraction(string campaignId, string heroId, string source)
        {
            WriteJsonObject(CharacterFile(campaignId, heroId, "enrichment.json"), new Dictionary<string, object>
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

        private static Dictionary<string, object> CharacterConstructionPolicyStatus()
        {
            Dictionary<string, int> statuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string campaignsRoot = CampaignsRoot();
            if (Directory.Exists(campaignsRoot))
            {
                foreach (string path in Directory.GetFiles(campaignsRoot, "enrichment.json", SearchOption.AllDirectories))
                {
                    string status = ReadString(ReadJsonObject(path), "status", "missing");
                    statuses[status] = statuses.TryGetValue(status, out int count) ? count + 1 : 1;
                }
            }

            return new Dictionary<string, object>
            {
                ["policy"] = "deterministic_first_response_then_background_enrichment",
                ["backgroundWorkerStarted"] = true,
                ["automaticProfileUploadsEnabled"] = false,
                ["legacyQueueStatuses"] = statuses,
                ["pendingBackgroundCalls"] = PendingCharacterEnrichmentCount()
            };
        }

        private static Dictionary<string, object> ValidateCharacterProfileLibrary(bool includeCourt = true)
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
                if (!MbtiDefinitions.ContainsKey(immutableMbtiType))
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
                if(ReadInt(courtCharacterModel,"version",0)!=CourtCharacterModelVersion) errors.Add(pair.Key+" does not use Court Character model v"+CourtCharacterModelVersion+".");
                Dictionary<string,object> expectedCourtCharacter=BuildCourtCharacterData(traitDocument,ReadDictionary(pair.Value,"sourceFacts")??pair.Value);
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
                if (TraitInt(traits, "courage") != (int)NativeFoundationSignalVNext(TraitInt(native, "valor"))) errors.Add(pair.Key + " does not expand native Valor into Courage.");
                if (TraitInt(traits, "mercy") != (int)NativeFoundationSignalVNext(TraitInt(native, "mercy"))) errors.Add(pair.Key + " does not expand native Mercy.");
                if (TraitInt(traits, "generosity") != (int)NativeFoundationSignalVNext(TraitInt(native, "generosity"))) errors.Add(pair.Key + " does not expand native Generosity.");
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
                ["courtCharacterModelVersion"] = CourtCharacterModelVersion,
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

        private static Dictionary<string, object> GenerateCharacterProfileLibrary(string[] args)
        {
            string sandbox = ProfileArg(args, "--sandbox-data", "");
            string reign = ProfileArg(args, "--reign-data", "");
            string courtManifestPath = ProfileArg(args, "--court-manifest", "");
            string output = ProfileArg(args, "--output", CharacterProfileCatalogPath);
            if (ReadString(ReadJsonObject(output), "packVersion", "") == NarrativePack)
                throw new InvalidOperationException("The legacy foundation generator cannot overwrite a complete narrative catalog. Generate foundations to a separate path and use the guarded narrative authoring workflow.");
            if (!Directory.Exists(sandbox)) throw new DirectoryNotFoundException("Sandbox ModuleData was not found: " + sandbox);
            if (!Directory.Exists(reign)) throw new DirectoryNotFoundException("Reign ModuleData was not found: " + reign);

            XDocument nativeLords = XDocument.Load(Path.Combine(sandbox, "lords.xml"), LoadOptions.PreserveWhitespace);
            XDocument nativeHeroes = XDocument.Load(Path.Combine(sandbox, "heroes.xml"), LoadOptions.PreserveWhitespace);
            XDocument courtLords = XDocument.Load(Path.Combine(reign, "reign_court_lords.xml"), LoadOptions.PreserveWhitespace);
            XDocument courtHeroes = XDocument.Load(Path.Combine(reign, "reign_court_heroes.xml"), LoadOptions.PreserveWhitespace);
            XDocument skills = XDocument.Load(Path.Combine(sandbox, "sandbox_skill_sets.xml"), LoadOptions.PreserveWhitespace);
            XDocument clans = XDocument.Load(Path.Combine(sandbox, "spclans.xml"), LoadOptions.PreserveWhitespace);
            XDocument courtClans = XDocument.Load(Path.Combine(reign, "reign_court_clans.xml"), LoadOptions.PreserveWhitespace);
            Dictionary<string, object> courtManifest = ReadJsonObject(courtManifestPath);
            Dictionary<string, Dictionary<string, object>> courtRecords = ReadDictionaryList(courtManifest, "records").ToDictionary(x => ReadString(x, "id", ""), x => x, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, XElement> skillSets = skills.Descendants("SkillSet").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, XElement> clanNodes = clans.Descendants("Faction").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (XElement clan in courtClans.Descendants("Faction").Where(x => x.Attribute("id") != null))
                clanNodes[(string)clan.Attribute("id")] = clan;

            List<Tuple<XElement, XElement, string>> sources = new List<Tuple<XElement, XElement, string>>();
            Dictionary<string, XElement> nativeHeroById = nativeHeroes.Descendants("Hero").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (XElement npc in nativeLords.Descendants("NPCCharacter").Where(x => string.Equals((string)x.Attribute("occupation"), "Lord", StringComparison.OrdinalIgnoreCase) && string.Equals((string)x.Attribute("is_hero"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                string id = (string)npc.Attribute("id");
                if (nativeHeroById.TryGetValue(id, out XElement hero)) sources.Add(Tuple.Create(npc, hero, "native_sandbox"));
            }
            Dictionary<string, XElement> courtHeroById = courtHeroes.Descendants("Hero").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (XElement npc in courtLords.Descendants("NPCCharacter").Where(x => string.Equals((string)x.Attribute("occupation"), "Lord", StringComparison.OrdinalIgnoreCase)))
            {
                string id = (string)npc.Attribute("id");
                if (courtHeroById.TryGetValue(id, out XElement hero)) sources.Add(Tuple.Create(npc, hero, "reign_court"));
            }

            Dictionary<string, string> nameById = sources.ToDictionary(x => (string)x.Item1.Attribute("id"), x => CleanGameText((string)x.Item1.Attribute("name")), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> profiles = new List<Dictionary<string, object>>();
            foreach (Tuple<XElement, XElement, string> source in sources.OrderBy(x => (string)x.Item1.Attribute("id"), StringComparer.OrdinalIgnoreCase))
            {
                string id = (string)source.Item1.Attribute("id");
                courtRecords.TryGetValue(id, out Dictionary<string, object> courtRecord);
                Dictionary<string, object> facts = BuildCatalogSourceFacts(source.Item1, source.Item2, source.Item3, courtRecord, skillSets, clanNodes);
                profiles.Add(BuildCatalogProfile(facts, source.Item3, courtRecord, nameById));
            }

            Dictionary<string, object> root = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["packVersion"] = CharacterProfilePackVersion,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["manifest"] = new Dictionary<string, object>
                {
                    ["expectedProfileCount"] = profiles.Count,
                    ["nativeSandboxCount"] = profiles.Count(x => ReadString(x, "source", "") == "native_sandbox"),
                    ["reignCourtCount"] = profiles.Count(x => ReadString(x, "source", "") == "reign_court"),
                    ["courtHouseholdCount"] = ReadInt(courtManifest, "household_count", 0),
                    ["traitCount"] = CoreTraitKeys.Length,
                    ["courtCharacterModelVersion"] = CourtCharacterModelVersion,
                    ["sandboxSourceHash"] = HashFiles(Path.Combine(sandbox, "heroes.xml"), Path.Combine(sandbox, "lords.xml"), Path.Combine(sandbox, "sandbox_skill_sets.xml"), Path.Combine(sandbox, "spclans.xml")),
                    ["reignSourceHash"] = HashFiles(Path.Combine(reign, "reign_court_heroes.xml"), Path.Combine(reign, "reign_court_lords.xml"), Path.Combine(reign, "reign_court_clans.xml"), courtManifestPath)
                },
                ["traitDefinitions"] = TraitDefinitions(),
                ["assignmentSources"] = CoreTraitAssignmentSources(),
                ["profiles"] = profiles
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, Json.Serialize(root), new UTF8Encoding(false));
            CharacterProfileLibraryRoot = null;
            CharacterProfilesById = null;
            Dictionary<string, object> validation = ValidateProfileLibraryObject(root);
            validation["output"] = Path.GetFullPath(output);
            return validation;
        }

        private static Dictionary<string, object> BuildCatalogSourceFacts(XElement npc, XElement hero, string source, Dictionary<string, object> courtRecord, Dictionary<string, XElement> skillSets, Dictionary<string, XElement> clans)
        {
            string id = (string)npc.Attribute("id");
            string faction = RemoveObjectPrefix((string)hero.Attribute("faction"));
            clans.TryGetValue(faction, out XElement clan);
            Dictionary<string, object> nativeTraits = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "valor", "generosity", "honor", "mercy", "calculating" }) nativeTraits[key] = 0;
            foreach (XElement trait in npc.Descendants("Trait"))
            {
                string key = ((string)trait.Attribute("id") ?? "").ToLowerInvariant();
                if (nativeTraits.ContainsKey(key)) nativeTraits[key] = ClampTrait(ParseInt((string)trait.Attribute("value")));
            }
            string templateId = RemoveObjectPrefix((string)npc.Attribute("skill_template"));
            Dictionary<string, object> nativeSkills = ResolveCatalogNativeSkills(npc, skillSets);
            string description = CleanGameText((string)hero.Attribute("text"));
            Dictionary<string, object> facts = new Dictionary<string, object>
            {
                ["heroStringId"] = id,
                ["characterObjectId"] = id,
                ["name"] = CleanGameText((string)npc.Attribute("name")),
                ["occupation"] = "Lord",
                ["isLord"] = true,
                ["isNotable"] = false,
                ["isWanderer"] = false,
                ["isAlive"] = !string.Equals((string)hero.Attribute("alive"), "false", StringComparison.OrdinalIgnoreCase),
                ["isFemale"] = string.Equals((string)npc.Attribute("is_female"), "true", StringComparison.OrdinalIgnoreCase),
                ["age"] = ParseDouble((string)npc.Attribute("age")),
                ["cultureId"] = RemoveObjectPrefix((string)npc.Attribute("culture")),
                ["clanId"] = faction,
                ["clanTier"] = ParseInt((string)clan?.Attribute("tier")),
                ["kingdomId"] = RemoveObjectPrefix((string)clan?.Attribute("super_faction")),
                ["spouseId"] = RemoveObjectPrefix((string)hero.Attribute("spouse")),
                ["fatherId"] = RemoveObjectPrefix((string)hero.Attribute("father")),
                ["motherId"] = RemoveObjectPrefix((string)hero.Attribute("mother")),
                ["voiceId"] = (string)npc.Attribute("voice") ?? "",
                ["skillTemplateId"] = templateId,
                ["nativeEncyclopediaText"] = description,
                ["encyclopediaText"] = description,
                ["traits"] = nativeTraits,
                ["skills"] = nativeSkills,
                ["source"] = source
            };
            if (courtRecord != null)
            {
                facts["householdId"] = ReadString(courtRecord, "household_id", "");
                facts["houseName"] = ReadString(courtRecord, "house_name", "");
                facts["householdRole"] = ReadString(courtRecord, "role", "");
                facts["roleLabel"] = ReadString(courtRecord, "role_label", "");
                facts["homeSettlementId"] = ReadString(courtRecord, "home_settlement_id", "");
                facts["homeSettlementName"] = ReadString(courtRecord, "home_name", "");
                facts["sourceTemplateId"] = ReadString(courtRecord, "source_template_id", "");
            }
            return facts;
        }

        private static Dictionary<string, object> ResolveCatalogNativeSkills(XElement npc, Dictionary<string, XElement> skillSets)
        {
            Dictionary<string, object> nativeSkills = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string templateId = RemoveObjectPrefix((string)npc?.Attribute("skill_template"));
            if (!string.IsNullOrWhiteSpace(templateId) && skillSets != null && skillSets.TryGetValue(templateId, out XElement skillSet))
            {
                foreach (XElement skill in skillSet.Elements("skill"))
                    nativeSkills[NormalizeSkillKey((string)skill.Attribute("id"))] = ParseInt((string)skill.Attribute("value"));
            }
            XElement directSkills = npc?.Element("skills");
            foreach (XElement skill in directSkills == null ? Enumerable.Empty<XElement>() : directSkills.Elements("skill"))
                nativeSkills[NormalizeSkillKey((string)skill.Attribute("id"))] = ParseInt((string)skill.Attribute("value"));
            return nativeSkills;
        }

        private static Dictionary<string, object> BuildCatalogProfile(Dictionary<string, object> facts, string source, Dictionary<string, object> courtRecord, Dictionary<string, string> names)
        {
            string id = ReadString(facts, "heroStringId", "");
            string name = ReadString(facts, "name", id);
            Dictionary<string, object> traits = BuildTraitDocument(facts);
            ApplyCatalogTraitEvidence(traits, facts, courtRecord);
            EnsureTraitPercentageData(traits, id);
            EnsureCourtCharacterData(traits, facts, id);
            Dictionary<string, object> derivedMbti = DeriveMbtiAxes(traits);
            string mbtiType = ReadString(derivedMbti, "type", "XXXX");
            if (!MbtiDefinitions.TryGetValue(mbtiType, out MbtiDefinition mbtiDefinition))
                throw new InvalidOperationException("Pregenerated noble " + id + " did not produce a valid MBTI type.");
            Dictionary<string, object> mbtiProfile = new Dictionary<string, object>
            {
                ["type"] = mbtiType,
                ["title"] = mbtiDefinition.Title,
                ["description"] = mbtiDefinition.Description,
                ["source"] = "pregenerated_noble_catalog",
                ["templateVersion"] = NotableMbtiTemplateVersion,
                ["assignedDay"] = -1d,
                ["immutable"] = true
            };
            traits["mbtiProfile"] = mbtiProfile;
            RebuildPersonalityPortrait(traits, name, CatalogDistinctiveTension(facts, ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>()), "shipped_profile_library");
            traits.Remove("definitions");
            traits.Remove("assignmentSources");
            traits.Remove("assignmentProtocol");
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>();
            string culture = HumanizeKey(ReadString(facts, "cultureId", "Calradian"));
            string role = ReadString(facts, "roleLabel", "noble");
            string home = ReadString(facts, "homeSettlementName", "their clan's holdings");
            string house = ReadString(facts, "houseName", "their clan");
            string nativeText = source == "native_sandbox" ? ReadString(facts, "nativeEncyclopediaText", "") : "";
            string roleSentence = CatalogRoleSentence(ReadString(facts, "householdRole", ""), house, home);
            string personality = ReadString(traits, "basePersonalitySummary", BuildRuleBasedPersonalitySummary(name, foundation));
            string inferredSummary = name + " is a " + culture + " " + role + " whose place in " + house + " ties them to " + home + ". " + roleSentence + " " + personality;
            string summary = !string.IsNullOrWhiteSpace(nativeText)
                ? nativeText.Trim() + " " + inferredSummary
                : inferredSummary;
            string publicText = !string.IsNullOrWhiteSpace(nativeText)
                ? nativeText
                : name + " is a " + culture + " noble associated with " + house + " and " + home + ". " + roleSentence;

            Dictionary<string, object> background = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["summary"] = !string.IsNullOrWhiteSpace(nativeText) ? summary : LimitText(summary, 1400),
                ["centralWound"] = CatalogCentralWound(facts, foundation),
                ["publicReputation"] = CatalogPublicReputation(foundation),
                ["canonBuiltFrom"] = CatalogEvidenceList(facts),
                ["encyclopediaText"] = !string.IsNullOrWhiteSpace(nativeText) ? nativeText : LimitText(publicText, 900),
                ["nativeEncyclopediaText"] = nativeText,
                ["nativeTextPreserved"] = !string.IsNullOrWhiteSpace(nativeText)
            };
            Dictionary<string, object> voice = BuildCatalogVoice(facts, foundation);
            Dictionary<string, object> motivations = BuildCatalogMotivations(facts, foundation);
            Dictionary<string, object> hidden = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["privateBackstory"] = CatalogPrivateBackstory(facts, foundation),
                ["secretWounds"] = new List<object> { CatalogCentralWound(facts, foundation) },
                ["privateEntanglements"] = new List<object>(),
                ["knownOnlyTo"] = new List<object> { "The character and anyone who independently discovers it." }
            };
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["packVersion"] = CharacterProfilePackVersion,
                ["heroStringId"] = id,
                ["source"] = source,
                ["sourceFacts"] = facts,
                ["mbtiProfile"] = mbtiProfile,
                ["traits"] = traits,
                ["background"] = background,
                ["voice"] = voice,
                ["motivations"] = motivations,
                ["hiddenHistory"] = hidden,
                ["template"] = new Dictionary<string, object>
                {
                    ["version"] = 2,
                    ["source"] = "shipped_profile_library",
                    ["archetype"] = "lord",
                    ["occupation"] = "Lord",
                    ["profilePackVersion"] = CharacterProfilePackVersion,
                    ["notes"] = "Canonical core personality with campaign-seeded private hooks."
                },
                ["hookPools"] = BuildCatalogHookPools(facts, foundation, names)
            };
        }

        private static void ApplyCatalogTraitEvidence(Dictionary<string, object> traits, Dictionary<string, object> facts, Dictionary<string, object> courtRecord)
        {
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            Action<string, int, string> adjust = (key, delta, reason) =>
            {
                if (!foundation.ContainsKey(key) || NativeLockedCoreTraits.Contains(key)) return;
                int before = TraitInt(foundation, key);
                int after = ClampTrait(before + delta);
                if (after == before) return;
                foundation[key] = after;
                evidence.Add(new Dictionary<string, object> { ["trait"] = key, ["prior"] = before, ["value"] = after, ["evidence"] = reason });
            };
            string description = ReadString(facts, "nativeEncyclopediaText", "").ToLowerInvariant();
            if (description.Contains("ambiti") || description.Contains("claimant") || description.Contains("throne")) adjust("ambition", 1, "The native biography describes a pursuit of advancement or rule.");
            if (description.Contains("conservative") || description.Contains("tradition")) adjust("traditionalism", 1, "The native biography explicitly emphasizes conservatism or tradition.");
            if (description.Contains("cautious")) { adjust("riskTolerance", -1, "The native biography explicitly describes caution."); adjust("patience", 1, "The native biography explicitly describes caution."); }
            if (description.Contains("law") || description.Contains("duty")) adjust("dutyMotivation", 1, "The native biography emphasizes law or duty.");
            if (description.Contains("scheme") || description.Contains("intrigue")) { adjust("tact", 1, "The native biography connects the character to intrigue."); adjust("socialTrust", -1, "The native biography connects the character to intrigue."); }
            if (description.Contains("cruel") || description.Contains("ruthless")) adjust("compassion", -1, "The native biography explicitly describes harsh conduct.");

            traits["foundationTraits"] = foundation;
            traits["hiddenReignTraits"] = new Dictionary<string, object>(foundation, StringComparer.OrdinalIgnoreCase);
            traits["assignmentOverrides"] = evidence;
            // The complete personality portrait is rebuilt after percentage calibration in BuildCatalogProfile.
        }

        private static Dictionary<string, object> BuildCatalogVoice(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            string nativeVoice = ReadString(facts, "voiceId", "earnest");
            int tact = TraitInt(traits, "tact"), assertiveness = TraitInt(traits, "assertiveness"), stability = TraitInt(traits, "emotionalStability"), pride = TraitInt(traits, "pride");
            string speech = nativeVoice == "curt" ? "Uses clipped, economical statements" : nativeVoice == "softspoken" ? "Speaks quietly and makes listeners lean in" : nativeVoice == "ironic" ? "Uses dry implication and carefully placed irony" : "Speaks earnestly and prefers clear declarations";
            speech += tact >= 1 ? ", cushioning dangerous points with courtly phrasing." : tact <= -1 ? ", often letting blunt truth land without protection." : ", balancing directness with the needs of the room.";
            return new Dictionary<string, object>
            {
                ["version"] = 2,
                ["nativeVoice"] = nativeVoice,
                ["speechStyle"] = speech,
                ["socialMask"] = stability >= 1 ? "Maintains a controlled public composure." : pride >= 1 ? "Projects dignity even when composure is strained." : "Adapts their public manner to the immediate balance of power.",
                ["tells"] = new List<object> { assertiveness >= 1 ? "Takes possession of pauses before answering." : "Waits to see who will press first.", TraitInt(traits, "irritability") >= 1 ? "Impatience appears in shortened replies." : "Frustration is usually kept behind small changes in posture." }
            };
        }

        private static Dictionary<string, object> BuildCatalogMotivations(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            List<string> motives = new[] { "wealthMotivation", "powerMotivation", "familyMotivation", "fameMotivation", "knowledgeMotivation", "religionMotivation", "revengeMotivation", "dutyMotivation", "survivalMotivation", "legacyMotivation" }.OrderByDescending(x => TraitInt(traits, x)).ThenBy(x => StableIndex(ReadString(facts, "heroStringId", "") + x, 1000)).Take(3).ToList();
            Dictionary<string, string> dreams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["wealthMotivation"] = "Build enough wealth that neither creditors nor patrons can dictate the household's choices.", ["powerMotivation"] = "Gain durable influence over the decisions that shape their clan and realm.", ["familyMotivation"] = "Secure the safety and advancement of the people they recognize as family.", ["fameMotivation"] = "Earn a reputation that cannot be dismissed as inherited station.", ["knowledgeMotivation"] = "Understand the people, records, and systems others use as leverage.", ["religionMotivation"] = "Live in a way that gives sacred order practical force.", ["revengeMotivation"] = "Settle a grievance that still defines what justice means to them.", ["dutyMotivation"] = "Fulfill the obligations of station without becoming another noble remembered for neglect.", ["survivalMotivation"] = "Create enough security to survive the next reversal of war or politics.", ["legacyMotivation"] = "Leave heirs, works, or a reputation that outlasts immediate victories."
            };
            List<object> dreamList = motives.Select(x => (object)dreams[x]).ToList();
            return new Dictionary<string, object>
            {
                ["version"] = 2,
                ["dreams"] = dreamList,
                ["fears"] = new List<object> { CatalogFear(traits), "Being made irrelevant while rivals decide the future." },
                ["desires"] = new List<object> { dreams[motives[0]], "To be treated as a person with independent interests rather than a convenient extension of clan policy." },
                ["linesTheyWillNotCross"] = CatalogLimits(traits)
            };
        }

        private static Dictionary<string, object> BuildCatalogHookPools(Dictionary<string, object> facts, Dictionary<string, object> traits, Dictionary<string, string> names)
        {
            string role = ReadString(facts, "roleLabel", "noble"), home = ReadString(facts, "homeSettlementName", "the household's holding"), house = ReadString(facts, "houseName", "the clan"), spouse = ResolveCatalogName(names, ReadString(facts, "spouseId", ""), "their spouse"), father = ResolveCatalogName(names, ReadString(facts, "fatherId", ""), "their father"), mother = ResolveCatalogName(names, ReadString(facts, "motherId", ""), "their mother");
            int basePressure = 20 + Math.Max(0, TraitInt(traits, "ambition")) * 12 + Math.Max(0, TraitInt(traits, "pride")) * 8;
            return new Dictionary<string, object>
            {
                ["secretInterests"] = new List<string> { "Private correspondence with someone outside the expected social circle.", "Collecting accounts of old disputes that powerful people would rather forget.", "Testing whether a commercial venture could create independence from clan money.", "A fascination with a rival culture's customs and political methods." },
                ["jealousyTargetTypes"] = new List<string> { "A relative who receives more praise for less work.", "A politically favored outsider welcomed too quickly by " + house + ".", "Someone whose freedom exposes the constraints of being a " + role + ".", "A rival whose influence at " + home + " is growing." },
                ["currentTemptations"] = new List<string> { "Use private information to force recognition.", "Accept a dangerous favor that promises independence.", "Turn a family disagreement into an alliance with an outsider.", "Risk reputation for an attraction that feels personally chosen." },
                ["privateBackstories"] = new List<string> { "A past failure at " + home + " was quietly blamed on someone else, and the character has never decided whether relief or guilt matters more.", "They once protected a household secret at real personal cost and now question whether the sacrifice was deserved.", "An early promise about their future was withdrawn when clan needs changed, leaving a private grievance beneath outward duty." },
                ["secretWounds"] = new List<string> { "They fear affection is conditional on usefulness.", "Public comparison with relatives still feels like a verdict on their worth.", "They remember the first time clan duty overruled a choice they believed was their own." },
                ["privateEntanglements"] = new List<string> { "An unresolved favor links them to a household rival.", "A discreet correspondent expects repayment for old information.", "A promise made during a moment of fear conflicts with present duty.", "Someone at " + home + " knows enough to damage their public reputation." },
                ["rumors"] = new List<string> { "Some servants believe the character is preparing to challenge a household decision.", "A visitor claims the character has been asking questions beyond their proper responsibilities.", "People at " + home + " disagree over whether recent generosity was sincere or strategic." },
                ["privateTruths"] = new List<string> { "They have imagined leaving the role chosen for them.", "They keep a private measure of every time the household chose another person's interests over theirs.", "They would break with custom if convinced the alternative secured a more meaningful legacy." },
                ["pressureProfiles"] = new List<Dictionary<string, object>>
                {
                    PressureProfile(basePressure, 20, 25, 15, 20),
                    PressureProfile(25, basePressure, 35, 20, 30),
                    PressureProfile(30, 25, basePressure, 30, 25),
                    PressureProfile(20, 30, 25, basePressure, 40)
                },
                ["familyContext"] = new Dictionary<string, object> { ["spouse"] = spouse, ["father"] = father, ["mother"] = mother }
            };
        }

        private static Dictionary<string, object> PressureProfile(int recognition, int resentment, int loyalty, int rebellion, int social)
        {
            return new Dictionary<string, object> { ["needForRecognition"] = Math.Min(100, recognition), ["resentment"] = Math.Min(100, resentment), ["loyaltyConflict"] = Math.Min(100, loyalty), ["rebellionTemptation"] = Math.Min(100, rebellion), ["romanticOrSocialRisk"] = Math.Min(100, social) };
        }

        private static string CatalogRoleSentence(string role, string house, string home)
        {
            switch (role)
            {
                case "father": return "As household head, they are expected to protect the family's standing while preparing successors who may eventually displace their authority.";
                case "mother": return "As household matriarch, they hold together kinship, reputation, and private negotiations that formal titles rarely acknowledge.";
                case "child_1": return "As the eldest child, inheritance expectations make every success and failure part of the household's judgment of succession.";
                case "child_2": return "As the second child, they must build influence without assuming the household's first claim will ever be theirs.";
                case "child_3": return "As a younger child, they are pushed toward an independent reputation, useful marriage, or service beyond the household.";
                case "child_4": return "As the youngest adult child, they have the least settled place and the greatest tension between family protection and personal freedom.";
                default: return "Their station ties personal choices to clan reputation and the shifting demands of Calradian politics.";
            }
        }

        private static string CatalogCentralWound(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            string role = ReadString(facts, "householdRole", "");
            if (role == "child_1") return "They fear being prepared for responsibility yet still judged unworthy when succession matters.";
            if (role.StartsWith("child_", StringComparison.Ordinal)) return "They have learned that family affection and family usefulness are not always the same thing.";
            if (role == "father" || role == "mother") return "They carry the knowledge that protecting a household can require choices their children may never forgive.";
            if (TraitInt(traits, "pride") >= 1) return "Humiliation and public dismissal cut more deeply than they admit.";
            if (TraitInt(traits, "socialTrust") <= -1) return "Past disappointments taught them that dependence gives other people leverage.";
            return "They fear becoming useful to everyone while remaining truly known by no one.";
        }

        private static string CatalogPrivateBackstory(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            return "Behind their public station, " + ReadString(facts, "name", "the character") + " keeps a private account of obligations, slights, and sacrifices. " + CatalogCentralWound(facts, traits);
        }

        private static string CatalogDistinctiveTension(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            string[] tensions =
            {
                "When duty and recognition conflict, they notice who receives credit and who bears the cost.",
                "They are most volatile when family expectations deny them an outcome they believe they earned.",
                "Their restraint weakens when another person tries to make a permanent decision on their behalf.",
                "They can endure hardship more easily than public dismissal or private irrelevance.",
                "They measure loyalty through remembered conduct and rarely treat a broken promise as isolated.",
                "They are vulnerable to opportunities that promise both independence and proof of personal worth.",
                "Their strongest choices emerge when personal attachment collides with the security of their station.",
                "They prefer controlled bargains, but accumulated humiliation can make a dangerous risk feel necessary."
            };
            return tensions[StableIndex(ReadString(facts, "heroStringId", "") + "|distinctive_tension|" + TraitInt(traits, "pride"), tensions.Length)];
        }

        private static string CatalogPublicReputation(Dictionary<string, object> traits)
        {
            if (TraitInt(traits, "tact") >= 1 && TraitInt(traits, "confidence") >= 1) return "Considered politically composed and difficult to corner in public.";
            if (TraitInt(traits, "aggression") >= 1) return "Known for answering resistance directly and sometimes too forcefully.";
            if (TraitInt(traits, "compassion") >= 1) return "Regarded as more attentive to dependents and casualties than many nobles.";
            if (TraitInt(traits, "socialTrust") <= -1) return "Regarded as guarded, observant, and slow to accept assurances.";
            return "Seen as a capable noble whose deeper priorities are not yet obvious.";
        }

        private static string CatalogFear(Dictionary<string, object> traits)
        {
            if (TraitInt(traits, "fearfulness") >= 1) return "A sudden reversal that leaves them exposed before preparations are complete.";
            if (TraitInt(traits, "shame") >= 1) return "Public disgrace that permanently changes how their family sees them.";
            if (TraitInt(traits, "familyMotivation") >= 1) return "Watching family loyalty fracture under pressure they failed to anticipate.";
            return "Losing control of a consequential choice to someone who does not understand its cost.";
        }

        private static List<object> CatalogLimits(Dictionary<string, object> traits)
        {
            List<object> limits = new List<object>();
            if (TraitInt(traits, "honesty") >= 1) limits.Add("Will not knowingly build a lasting agreement on a direct lie.");
            if (TraitInt(traits, "compassion") >= 1 || TraitInt(traits, "mercy") >= 1) limits.Add("Will not casually punish helpless people for another person's offense.");
            if (TraitInt(traits, "loyalty") >= 1) limits.Add("Will not abandon a chosen ally without warning when alternatives remain.");
            if (limits.Count == 0) limits.Add("Will not accept a bargain that leaves them permanently powerless.");
            return limits;
        }

        private static List<object> CatalogEvidenceList(Dictionary<string, object> facts)
        {
            List<object> result = new List<object> { "Native traits", "Native skills", "Age and noble station", "Clan and family links" };
            if (ReadString(facts, "source", "") == "native_sandbox" && !string.IsNullOrWhiteSpace(ReadString(facts, "nativeEncyclopediaText", ""))) result.Insert(0, "Authored native encyclopedia description");
            if (!string.IsNullOrWhiteSpace(ReadString(facts, "householdId", ""))) result.Add("Reign court-household role and home holding");
            return result;
        }

        private static string ResolveCatalogName(Dictionary<string, string> names, string id, string fallback)
        {
            return !string.IsNullOrWhiteSpace(id) && names.TryGetValue(id, out string name) && !string.IsNullOrWhiteSpace(name) ? name : fallback;
        }

        private static string ProfileArg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < (args ?? new string[0]).Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }

        private static string CleanGameText(string value)
        {
            string text = value ?? "";
            if (text.StartsWith("{=", StringComparison.Ordinal))
            {
                int end = text.IndexOf('}');
                if (end >= 0 && end + 1 < text.Length) text = text.Substring(end + 1);
            }
            return text.Trim();
        }

        private static string RemoveObjectPrefix(string value)
        {
            string text = value ?? "";
            int dot = text.IndexOf('.');
            return dot >= 0 ? text.Substring(dot + 1) : text;
        }

        private static string NormalizeSkillKey(string value)
        {
            string key = (value ?? "").Trim();
            if (key == "Crafting") return "smithing";
            return key.Length == 0 ? "" : char.ToLowerInvariant(key[0]) + key.Substring(1);
        }

        private static int ParseInt(string value) { return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0; }
        private static double ParseDouble(string value) { return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0d; }

        private static string HashFiles(params string[] paths)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string path in paths.Where(File.Exists).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static Dictionary<string, object> DeepCloneProfileDictionary(Dictionary<string, object> value)
        {
            return value == null ? new Dictionary<string, object>() : TryParseJsonObject(Json.Serialize(value)) ?? new Dictionary<string, object>();
        }

        private static List<object> ReadObjectList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return new List<object>();
            if (value is ArrayList array) return array.Cast<object>().ToList();
            if (value is IEnumerable<object> objects) return objects.ToList();
            return new List<object>();
        }

        private static List<object> MergeLists(List<object> values, string extra)
        {
            List<object> result = values ?? new List<object>();
            if (!string.IsNullOrWhiteSpace(extra)) result.Add(extra);
            return result;
        }

        private static Dictionary<string, object> ValidateProfileLibraryObject(Dictionary<string, object> root)
        {
            List<Dictionary<string, object>> profiles = ReadDictionaryList(root, "profiles");
            int invalid = profiles.Count(x =>
            {
                Dictionary<string, object> document = ReadDictionary(x, "traits") ?? new Dictionary<string, object>();
                return (ReadDictionary(document, "foundationTraits") ?? new Dictionary<string, object>()).Count != CoreTraitKeys.Length
                    || !TraitPercentageDocumentReady(document)
                    || !PersonalityPortraitReady(document);
            });
            int expected = ReadInt(ReadDictionary(root, "manifest"), "expectedProfileCount", profiles.Count);
            return new Dictionary<string, object>
            {
                ["ok"] = profiles.Count == expected && invalid == 0,
                ["packVersion"] = ReadString(root, "packVersion", ""),
                ["profileCount"] = profiles.Count,
                ["nativeSandboxCount"] = profiles.Count(x => ReadString(x, "source", "") == "native_sandbox"),
                ["reignCourtCount"] = profiles.Count(x => ReadString(x, "source", "") == "reign_court"),
                ["invalidTraitDocuments"] = invalid
            };
        }

        private static List<Dictionary<string, object>> RunCharacterProfileLibrarySelfTests(bool includeCourt = true)
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
                hookProfile = ReadDictionaryList(ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProfileLibrary", "profile_catalog_v4.json")), "profiles")
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
            Dictionary<string, object> baselineGenerated = DefaultGeneratedCharacter(unknown, baselineTraits);
            bool baselineReady = TraitDocumentReady(baselineTraits)
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
                bool passiveDidNotMaterialize = !Directory.Exists(CharacterDirectory(policyCampaign, dynamicHeroId))
                    && string.Equals(ReadString(passiveStack, "profileSource", ""), "live_runtime_unconstructed", StringComparison.OrdinalIgnoreCase)
                    && ReadBool(passiveStack, "constructionDeferredUntilFirstInteraction", false);
                results.Add(ProfileSelfTest("passive_event_does_not_create_dynamic_profile", passiveDidNotMaterialize,
                    "Passive event planning uses live baseline facts without creating or constructing a dynamic character profile.", null));

                MaterializeCharacterProfile(policyCampaign, dynamicHeroId, unknown, false, false);
                Dictionary<string, object> dynamicConstructed = ReadJsonObject(CharacterFile(policyCampaign, dynamicHeroId, "constructed.json"));
                Dictionary<string, object> dynamicEnrichment = ReadJsonObject(CharacterFile(policyCampaign, dynamicHeroId, "enrichment.json"));
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
                Dictionary<string, object> canonicalConstructed = ReadJsonObject(CharacterFile(policyCampaign, canonicalHeroId, "constructed.json"));
                Dictionary<string, object> canonicalEnrichment = ReadJsonObject(CharacterFile(policyCampaign, canonicalHeroId, "enrichment.json"));
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

                Dictionary<string, object> scaffold = ConstructCharacterFiles(policyCampaign, dynamicHeroId, unknown, true, false, "background_enrichment");
                results.Add(ProfileSelfTest("background_enrichment_fallback_safe",
                    IsCharacterConstructionReady(scaffold) && !ReadBool(scaffold, "llmUsed", true),
                    "Background enrichment retains a usable deterministic scaffold when no provider result is available.", scaffold));
            }
            finally
            {
                try
                {
                    string policyCampaignDir = CampaignDirectory(policyCampaign);
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
            results.Add(ProfileSelfTest("sparse_native_traits_readiness", TraitDocumentReady(sparseNativeTraits),
                "Characters whose native trait payload is unavailable still become stable after their complete 43-trait foundation is built.", null));

            Dictionary<string, object> settings = DefaultSettings();
            settings["dialogueModel"] = "dialogue-test-model";
            settings["characterConstructionModel"] = "construction-test-model";
            results.Add(ProfileSelfTest("dedicated_construction_model", ModelForRequest(settings, "character_construction") == "construction-test-model", "First-contact character construction routes through the dedicated construction model setting.", null));
            bool adaptiveRetryBudget = CharacterConstructionMaxTokensForAttempt(3600, 1) == 3600
                && CharacterConstructionMaxTokensForAttempt(3600, 2) == 7200
                && CharacterConstructionMaxTokensForAttempt(30000, 2) == 50000
                && CharacterConstructionMaxTokensForAttempt(50000, 1) == 50000
                && CharacterConstructionMaxTokensForAttempt(int.MaxValue, 2) == 50000
                && ReadInt(DefaultSettings(), "characterConstructionMaxTokens", 0) == 50000;
            results.Add(ProfileSelfTest("adaptive_construction_token_retry", adaptiveRetryBudget,
                "Construction defaults to a 50,000-token ceiling; a deliberately smaller setting can grow on retry but never exceeds 50,000.",
                new Dictionary<string, object>
                {
                    ["firstAttemptTokens"] = CharacterConstructionMaxTokensForAttempt(3600, 1),
                    ["retryTokens"] = CharacterConstructionMaxTokensForAttempt(3600, 2)
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

            string compactRetryPrompt = BuildCharacterConstructionPromptForAttempt("campaign", "hero", unknown, baselineTraits, 2);
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
                LikelyCharacterConstructionIssue("Character construction response hit the max token limit before valid JSON completed.", lengthAttempts)
                    .Contains("cut off by the character construction token limit"),
                "A local output-budget cutoff is diagnosed as token truncation rather than provider rate limiting.", null));

            Dictionary<string, object> legacyParseFailure = new Dictionary<string, object>
            {
                ["status"] = "failed",
                ["failureKind"] = "parse",
                ["constructionEngineVersion"] = CharacterConstructionEngineVersion - 1,
                ["settingsRevision"] = ReadString(settings, "settingsRevision", ""),
                ["settingsFingerprint"] = SettingsFingerprint(settings),
                ["failedUtc"] = DateTime.UtcNow.AddMinutes(5).ToString("o")
            };
            results.Add(ProfileSelfTest("legacy_truncation_auto_retry",
                ShouldRetryFailedConstruction(legacyParseFailure, settings),
                "A construction that failed under the pre-adaptive engine is retried once without requiring a settings edit.", null));
            var exhaustedBudget = DeepCloneProfileDictionary(legacyParseFailure);
            exhaustedBudget["failureKind"] = "output_limit";
            exhaustedBudget["constructionEngineVersion"] = CharacterConstructionEngineVersion;
            var changedBudgetSettings = DeepCloneProfileDictionary(settings);
            changedBudgetSettings["characterConstructionMaxTokens"] = 42000;
            results.Add(ProfileSelfTest("construction_output_limit_retry_policy",
                !ShouldRetryFailedConstruction(exhaustedBudget, settings)
                    && ShouldRetryFailedConstruction(exhaustedBudget, changedBudgetSettings),
                "An exhausted current creation budget does not trigger another automatic charge; a deliberate configuration change permits retry.", null));
            results.Add(ProfileSelfTest("background_character_worker_disabled", !ReadBool(settings, "backgroundCharacterEnrichmentEnabled", true), "Automatic background character construction is disabled by default.", null));
            return results;
        }

        private static Dictionary<string, object> ProfileSelfTest(string caseId, bool passed, string summary, Dictionary<string, object> data)
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
