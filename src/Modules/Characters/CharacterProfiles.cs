using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        internal static Dictionary<string, object> UpsertCharacterProfile(string campaignId, Dictionary<string, object> hero)
        {
            if (hero == null)
            {
                throw new InvalidOperationException("Hero profile is empty.");
            }

            string heroId = Program.CharacterIdFrom(hero);
            if (string.IsNullOrWhiteSpace(heroId))
            {
                throw new InvalidOperationException("heroStringId is required.");
            }

            string dir = Program.CharacterDirectory(campaignId, heroId);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "memory"));
            Directory.CreateDirectory(Path.Combine(dir, "history"));
            Directory.CreateDirectory(Path.Combine(dir, "portraits"));
            string profilePath = Path.Combine(dir, "profile.json");
            bool created = !File.Exists(profilePath);
            Dictionary<string, object> existingProfile = created ? new Dictionary<string, object>() : Program.ReadJsonObject(profilePath);
            Dictionary<string, object> profile = new Dictionary<string, object>(hero, StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            string preservedNativeDescription = FirstNonEmpty(
                ReadString(existingProfile, "nativeEncyclopediaText", ""),
                ReadString(hero, "nativeEncyclopediaText", ""),
                created ? ReadString(hero, "encyclopediaText", "") : "");
            if (!string.IsNullOrWhiteSpace(preservedNativeDescription))
            {
                profile["nativeEncyclopediaText"] = preservedNativeDescription;
            }
            if (created)
            {
                profile["createdUtc"] = DateTime.UtcNow.ToString("o");
            }
            else if (existingProfile.ContainsKey("createdUtc"))
            {
                profile["createdUtc"] = existingProfile["createdUtc"];
            }

            CharactersService.PreserveCommonerHousehold(profile, existingProfile);
            if (!profile.ContainsKey("narrativeVersion") && existingProfile.ContainsKey("narrativeVersion"))
                profile["narrativeVersion"] = existingProfile["narrativeVersion"];
            profile = CharactersService.WithNarrativeCampaignVersion(campaignId, profile);
            CharactersService.PreserveWandererIdentity(profile, existingProfile);
            lock (Program.FileLock)
            {
                PortraitsService.PreserveConfirmedOutfit(campaignId, heroId, profile);
                Program.WriteJsonObject(profilePath, profile);
            }
            Program.WriteCampaignMetadata(campaignId, profile);
            Program.EnsureJsonObjectFile(Path.Combine(dir, "template.json"), LoadCharacterTemplate(heroId, profile));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "characteristics.json"), Program.DefaultCharacteristics(hero));
            Dictionary<string, object> suppliedWealth = ReadDictionary(profile, "wealth");
            string wealthPath = Path.Combine(dir, "wealth.json");
            if (suppliedWealth != null && suppliedWealth.Count > 0)
            {
                Program.WriteJsonObject(wealthPath, CharactersService.NormalizeObservedWealth(suppliedWealth));
            }
            else
            {
                Dictionary<string, object> existingWealth = Program.ReadJsonObject(wealthPath);
                if (!CharactersService.IsObservedWealth(existingWealth))
                    Program.WriteJsonObject(wealthPath, CharactersService.UnknownWealth(hero));
            }
            Program.WriteJsonObject(Path.Combine(dir, "inventory.json"), DictionaryOrDefault(profile, "inventory", Program.DefaultInventory(hero)));
            Program.WriteJsonObject(Path.Combine(dir, "appearance.json"), DictionaryOrDefault(profile, "appearance", Program.DefaultAppearance(hero)));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "motivations.json"), DefaultMotivations(hero));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "secrets.json"), DefaultSecrets(hero));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "state.json"), Program.DefaultCharacterState(hero));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "pressure.json"), DefaultPressure(hero));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "memory", "summary.json"), new Dictionary<string, object>
            {
                ["version"] = 1,
                ["summary"] = "",
                ["updatedUtc"] = ""
            });
            Program.EnsureJsonObjectFile(Path.Combine(dir, "portraits", "portrait_meta.json"), new Dictionary<string, object>
            {
                ["version"] = 1,
                ["status"] = "not_generated"
            });
            Program.EnsureFile(Path.Combine(dir, "memory", "memories.jsonl"));
            Program.EnsureJsonObjectFile(Path.Combine(dir, "memory", "relationships.json"), new Dictionary<string, object> { ["version"] = 1 });
            Program.EnsureFile(Path.Combine(dir, "history", "dialogue.jsonl"));
            Program.EnsureFile(Path.Combine(dir, "history", "events.jsonl"));
            Program.EnsureFile(Path.Combine(dir, "history", "decisions.jsonl"));
            Program.WriteJsonObject(Path.Combine(dir, "schema_manifest.json"), CharacterSchemaManifest(campaignId, heroId, profile));
            Program.UpdateCharacterIndex(campaignId, profile);
            Dictionary<string, object> foundation = CharactersService.MaterializeCharacterProfile(campaignId, heroId, profile, false, false);

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["created"] = created,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["path"] = dir,
                ["foundation"] = foundation
            };
        }

        internal static Dictionary<string, object> CharacterSchemaApi()
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schemaVersion"] = Program.CharacterSchemaVersion,
                ["scale"] = new Dictionary<string, object>
                {
                    ["range"] = "-2..2",
                    ["-2"] = "strongly opposite or defining inverse",
                    ["-1"] = "leans opposite",
                    ["0"] = "ordinary, balanced, or situational",
                    ["1"] = "clearly present",
                    ["2"] = "defining trait"
                },
                ["traitCount"] = CharactersService.CoreTraitKeys.Length,
                ["traitKeys"] = CharactersService.CoreTraitKeys.ToList(),
                ["folderLayout"] = CharacterSchemaFiles(),
                ["traitDefinitions"] = CharactersService.TraitDefinitions(),
                ["assignmentProtocol"] = new Dictionary<string, object>
                {
                    ["precedence"] = new List<string> { "locked native mapping", "explicit native description", "native personality traits", "selected native skills", "stable biography and station", "stable hero-id tie breaker" },
                    ["lockedMappings"] = new Dictionary<string, object> { ["courage"] = "valor", ["mercy"] = "mercy", ["generosity"] = "generosity" },
                    ["nativeDescription"] = "Clear native description evidence may correct non-locked rule assignments during character construction.",
                    ["skills"] = "All native skills are retained as evidence, but only defensible selected skills have limited weight and skills do not independently determine morality."
                },
                ["assignmentSources"] = CharactersService.CoreTraitAssignmentSources(),
                ["promptBehavior"] = new List<string>
                {
                    "Always include the rounded basePersonalitySummary synthesized from the complete trait foundation.",
                    "Ask Minime to select zero to five traits relevant to the current exchange, preferably fewer.",
                    "Repeat only the selected traits' fixed level descriptions under explicit personality emphasis instructions."
                },
                ["promptStack"] = new List<string>
                {
                    "Every character response loads a concise base personality synthesized from all foundation traits, plus Minime-selected descriptions for up to five traits relevant to the current exchange.",
                    "Backstory, hidden history, secrets, pressure, relationships, memories, and prior lines are selected only when the player text or scene makes them relevant.",
                    "Visible replies must never expose hidden history, raw trait labels, prompt rules, JSON, or internal thoughts.",
                    "Execution actions remain suggestions until the action validation layer accepts known IDs."
                },
                ["constructionRules"] = new List<string>
                {
                    "Shipped fixed-character profiles materialize during campaign synchronization before first contact.",
                    "Unknown dynamic characters receive a deterministic baseline only when first contacted.",
                    "Their full profile is constructed synchronously before the first NPC response; no background character LLM worker runs.",
                    "Core game traits remain fixed; save-specific hooks vary per campaign without changing the character's defining core."
                }
            };
        }

        private static Dictionary<string, object> CharacterSchemaManifest(string campaignId, string heroId, Dictionary<string, object> profile)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = Program.CharacterSchemaVersion,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["purpose"] = "Versioned file contract for one Bannerlord Reign character in one campaign save.",
                ["profileSummary"] = new Dictionary<string, object>
                {
                    ["name"] = ReadString(profile, "name", heroId),
                    ["archetype"] = CharacterArchetype(profile),
                    ["clanId"] = ReadString(profile, "clanId", ""),
                    ["kingdomId"] = ReadString(profile, "kingdomId", ""),
                    ["cultureId"] = ReadString(profile, "cultureId", "")
                },
                ["files"] = CharacterSchemaFiles(),
                ["privacy"] = new Dictionary<string, object>
                {
                    ["playerReadable"] = new List<string> { "profile.json", "appearance.json:firstView", "background.json:encyclopediaText", "portraits/custom.png", "portraits/portrait.png" },
                    ["contextualKnowledge"] = new List<string> { "wealth.json", "inventory.json", "appearance.json:equipment" },
                    ["serverPrivate"] = new List<string> { "narrative.json", "hidden_history.json", "secrets.json", "save_quirks.json", "pressure.json", "memory/*", "history/decisions.jsonl" }
                }
            };
        }

        private static List<Dictionary<string, object>> CharacterSchemaFiles()
        {
            return new List<Dictionary<string, object>>
            {
                CharacterSchemaFile("schema_manifest.json", "json", "Folder contract and privacy map for this character.", true, "upsert"),
                CharacterSchemaFile("profile.json", "json", "Raw Bannerlord facts and campaign identity. This is authoritative for IDs.", true, "upsert"),
                CharacterSchemaFile("template.json", "json", "Optional authored baseline for canon lords; generated placeholder for notables and wanderers.", true, "upsert"),
                CharacterSchemaFile("characteristics.json", "json", "Legacy/general traits surface. Kept for compatibility while the stack is split into focused files.", true, "upsert"),
                CharacterSchemaFile("wealth.json", "json", "Current gold, clan money, clan wealth tier, clan social credit, item value, and visible wealth tier.", true, "upsert"),
                CharacterSchemaFile("inventory.json", "json", "Party-carried item roster summary and top valuable/useful items.", true, "upsert"),
                CharacterSchemaFile("appearance.json", "json", "Civilian/battle equipment, first-view text, true status, apparent status, and presentation mismatch.", true, "upsert"),
                CharacterSchemaFile("traits.json", "json", "The 43-trait Reign personality foundation, persistent 0..100 percentages, six positive-facing court virtues, fixed -2..2 descriptions, native inputs, assignment evidence, concise base summary, and attractiveness.", true, "construction"),
                CharacterSchemaFile("background.json", "json", "Public and private-safe background, including encyclopediaText.", true, "construction"),
                CharacterSchemaFile("voice.json", "json", "Speech style, social mask, and visible tells.", true, "construction"),
                CharacterSchemaFile("narrative.json", "json", "Complete private narrative, hobbies, concerns, importance and stable defining trio; required for v5 characters.", false, "construction"),
                CharacterSchemaFile("motivations.json", "json", "Dreams, fears, desires, and hard limits.", true, "construction"),
                CharacterSchemaFile("save_quirks.json", "json", "Campaign-specific variation such as secret interests, jealousy target type, and temptation.", true, "construction"),
                CharacterSchemaFile("hidden_history.json", "json", "Private campaign-only backstory and wounds. Not shown in encyclopedia.", true, "construction"),
                CharacterSchemaFile("secrets.json", "json", "Rumors and private truths for intrigue/event/court hooks.", true, "construction"),
                CharacterSchemaFile("pressure.json", "json", "Instability meters for recognition, resentment, loyalty conflict, rebellion temptation, and social risk.", true, "construction"),
                CharacterSchemaFile("state.json", "json", "Mutable current mood, plan, crisis, and last known settlement.", true, "runtime"),
                CharacterSchemaFile("constructed.json", "json", "Construction state, attempts, settings fingerprint, and failure diagnostics.", true, "construction"),
                CharacterSchemaFile("memory/summary.json", "json", "Rolling memory summary.", true, "runtime"),
                CharacterSchemaFile("memory/relationships.json", "json", "Relationship-specific memory and signals.", true, "runtime"),
                CharacterSchemaFile("memory/memories.jsonl", "jsonl", "Durable memory rows selected by prompt relevance.", true, "runtime"),
                CharacterSchemaFile("history/dialogue.jsonl", "jsonl", "Visible player/NPC dialogue turns.", true, "runtime"),
                CharacterSchemaFile("history/events.jsonl", "jsonl", "Event and court transcript rows involving this character.", true, "runtime"),
                CharacterSchemaFile("history/decisions.jsonl", "jsonl", "Private construction, internal thought, action, and troubleshooting records.", true, "runtime"),
                CharacterSchemaFile("portraits/", "folder", "Generated, custom, source, and prompt portrait assets.", true, "portrait")
            };
        }

        private static Dictionary<string, object> CharacterSchemaFile(string path, string type, string purpose, bool required, string stage)
        {
            return new Dictionary<string, object>
            {
                ["path"] = path,
                ["type"] = type,
                ["purpose"] = purpose,
                ["required"] = required,
                ["stage"] = stage
            };
        }

        internal static Dictionary<string, object> CharacterStructureDiagnostics(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            string heroId = FirstNonEmpty(ReadString(payload, "heroStringId", ""), ReadString(payload, "heroId", ""));
            if (!string.IsNullOrWhiteSpace(heroId))
            {
                Dictionary<string, object> hero = DiagnoseCharacterFolder(campaignId, heroId);
                Program.LogOperational("characters.diagnose_hero", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["issues"] = hero.ContainsKey("issues") ? hero["issues"] : new ArrayList()
                });
                return hero;
            }

            Dictionary<string, object> campaign = DiagnoseCampaignCharacters(campaignId);
            Program.LogOperational("characters.diagnose_campaign", campaign);
            return campaign;
        }

        private static Dictionary<string, object> DiagnoseCampaignCharacters(string campaignId)
        {
            string charactersDir = Path.Combine(Program.CampaignDirectory(campaignId), "characters");
            List<Dictionary<string, object>> problemCharacters = new List<Dictionary<string, object>>();
            Dictionary<string, int> statuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            int ready = 0;
            int missingRequired = 0;
            int needsConstruction = 0;

            if (Directory.Exists(charactersDir))
            {
                foreach (string dir in Directory.GetDirectories(charactersDir))
                {
                    string heroId = Path.GetFileName(dir);
                    if (string.Equals(heroId, "index.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Dictionary<string, object> diag = DiagnoseCharacterFolder(campaignId, heroId);
                    total++;
                    string status = ReadString(ReadDictionary(diag, "constructed"), "status", "missing");
                    statuses[status] = statuses.ContainsKey(status) ? statuses[status] + 1 : 1;
                    if (ReadBool(diag, "fullCharacterReady", false))
                    {
                        ready++;
                    }

                    if (ReadInt(diag, "missingRequiredCount", 0) > 0)
                    {
                        missingRequired++;
                    }

                    if (ReadBool(diag, "needsConstruction", false))
                    {
                        needsConstruction++;
                    }

                    ArrayList issues = diag.ContainsKey("issues") ? diag["issues"] as ArrayList ?? new ArrayList() : new ArrayList();
                    if (issues.Count > 0)
                    {
                        problemCharacters.Add(new Dictionary<string, object>
                        {
                            ["heroStringId"] = heroId,
                            ["name"] = ReadString(ReadDictionary(diag, "profile"), "name", heroId),
                            ["constructedStatus"] = status,
                            ["issueCount"] = issues.Count,
                            ["issues"] = issues.Cast<object>().Take(8).ToList()
                        });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schemaVersion"] = Program.CharacterSchemaVersion,
                ["campaignId"] = campaignId,
                ["charactersDir"] = charactersDir,
                ["charactersDirExists"] = Directory.Exists(charactersDir),
                ["totalCharacters"] = total,
                ["fullCharacterReadyCount"] = ready,
                ["needsConstructionCount"] = needsConstruction,
                ["missingRequiredFileCharacters"] = missingRequired,
                ["constructedStatuses"] = statuses,
                ["problemCharacters"] = problemCharacters.OrderByDescending(x => ReadInt(x, "issueCount", 0)).Take(80).ToList()
            };
        }

        private static Dictionary<string, object> DiagnoseCharacterFolder(string campaignId, string heroId)
        {
            string dir = Program.CharacterDirectory(campaignId, heroId);
            Dictionary<string, object> profile = Program.ReadJsonObject(Path.Combine(dir, "profile.json"));
            Dictionary<string, object> wealth = Program.ReadJsonObject(Path.Combine(dir, "wealth.json"));
            Dictionary<string, object> inventory = Program.ReadJsonObject(Path.Combine(dir, "inventory.json"));
            Dictionary<string, object> appearance = Program.ReadJsonObject(Path.Combine(dir, "appearance.json"));
            Dictionary<string, object> traits = Program.ReadJsonObject(Path.Combine(dir, "traits.json"));
            Dictionary<string, object> background = Program.ReadJsonObject(Path.Combine(dir, "background.json"));
            Dictionary<string, object> voice = Program.ReadJsonObject(Path.Combine(dir, "voice.json"));
            Dictionary<string, object> motivations = Program.ReadJsonObject(Path.Combine(dir, "motivations.json"));
            Dictionary<string, object> saveQuirks = Program.ReadJsonObject(Path.Combine(dir, "save_quirks.json"));
            Dictionary<string, object> hiddenHistory = Program.ReadJsonObject(Path.Combine(dir, "hidden_history.json"));
            Dictionary<string, object> secrets = Program.ReadJsonObject(Path.Combine(dir, "secrets.json"));
            Dictionary<string, object> pressure = Program.ReadJsonObject(Path.Combine(dir, "pressure.json"));
            Dictionary<string, object> state = Program.ReadJsonObject(Path.Combine(dir, "state.json"));
            Dictionary<string, object> constructed = Program.ReadJsonObject(Path.Combine(dir, "constructed.json"));

            List<Dictionary<string, object>> files = new List<Dictionary<string, object>>();
            ArrayList issues = new ArrayList();
            int missingRequired = 0;
            foreach (Dictionary<string, object> spec in CharacterSchemaFiles())
            {
                string relative = ReadString(spec, "path", "");
                string type = ReadString(spec, "type", "");
                bool required = ReadBool(spec, "required", false);
                string path = CharacterRelativePath(dir, relative);
                bool exists = type == "folder" ? Directory.Exists(path) : File.Exists(path);
                long bytes = exists && type != "folder" ? new FileInfo(path).Length : 0;
                bool valid = true;
                long rows = 0;
                if (exists && type == "json")
                {
                    valid = Program.ReadJsonObject(path).Count > 0 || bytes == 0;
                }
                else if (exists && type == "jsonl")
                {
                    rows = CountJsonLines(path);
                }

                if (required && !exists)
                {
                    missingRequired++;
                    issues.Add("Missing required " + type + ": " + relative);
                }
                else if (exists && !valid)
                {
                    issues.Add("Invalid JSON: " + relative);
                }

                files.Add(new Dictionary<string, object>
                {
                    ["path"] = relative,
                    ["type"] = type,
                    ["exists"] = exists,
                    ["bytes"] = bytes,
                    ["rows"] = rows,
                    ["valid"] = valid,
                    ["stage"] = ReadString(spec, "stage", "")
                });
            }

            bool profileReady = profile.Count > 0 && !string.IsNullOrWhiteSpace(Program.CharacterIdFrom(profile));
            bool wealthReady = wealth.Count > 0;
            bool inventoryReady = inventory.Count > 0;
            bool appearanceReady = appearance.Count > 0 && !string.IsNullOrWhiteSpace(ReadString(appearance, "firstView", ""));
            bool traitDescriptorsReady = TraitDocumentReady(traits);
            bool backgroundReady = !string.IsNullOrWhiteSpace(ReadString(background, "encyclopediaText", "")) || !string.IsNullOrWhiteSpace(ReadString(background, "summary", ""));
            bool hiddenReady = ObjectHasAnyContent(hiddenHistory, "privateBackstory", "secretWounds", "privateEntanglements", "knownOnlyTo");
            bool voiceReady = ObjectHasAnyContent(voice, "speechStyle", "socialMask", "tells");
            bool motivationsReady = ObjectHasAnyContent(motivations, "dreams", "fears", "desires", "linesTheyWillNotCross");
            bool saveQuirksReady = ObjectHasAnyContent(saveQuirks, "secretInterest", "jealousyTargetType", "currentTemptation");
            bool secretsReady = ObjectHasAnyContent(secrets, "rumors", "privateTruths");
            bool requiresNarrative = ReadInt(profile, "narrativeVersion", 0) >= 5;
            var narrative = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "narrative.json"));
            bool narrativeReady = ReadString(narrative, "status", "") == "ready" && CharactersService.ValidateCharacterNarrative(narrative, true).Count == 0;
            if (requiresNarrative && !narrativeReady) issues.Add("Expanded narrative is not complete.");
            if (narrativeReady) { saveQuirksReady = true; secretsReady = true; }
            bool pressureReady = pressure.Count > 0;
            bool stateReady = state.Count > 0;
            string constructedStatus = ReadString(constructed, "status", "");
            bool constructedReady = IsUsableCharacterStatus(constructedStatus);
            bool needsConstruction = !constructedReady;

            if (!profileReady) issues.Add("Profile is missing hero identity.");
            if (!wealthReady) issues.Add("Wealth file is missing or empty; bargaining, tribute, and social status cues will be weaker.");
            if (!inventoryReady) issues.Add("Inventory file is missing or empty; item-aware dialogue and economic judgement will be weaker.");
            if (!appearanceReady) issues.Add("Appearance file is missing first-view/equipment data; status disguise and visual first impressions will be weak.");
            if (!traitDescriptorsReady) issues.Add("Traits are missing descriptors or hidden Reign traits.");
            if (!backgroundReady) issues.Add("Public background / encyclopediaText is missing.");
            if (!hiddenReady) issues.Add("Hidden history is empty; intrigue/court hooks will be thin.");
            if (!voiceReady) issues.Add("Voice file is empty; dialogue will lose distinct speech posture.");
            if (!motivationsReady) issues.Add("Motivations are empty; dreams/fears/desires are not ready.");
            if (!saveQuirksReady) issues.Add("Save quirks are empty; this campaign has little per-save variation for this character.");
            if (!secretsReady) issues.Add("Secrets are empty; intrigue/event hooks will be thin.");
            if (!pressureReady) issues.Add("Pressure file is missing or empty.");
            if (!stateReady) issues.Add("State file is missing or empty.");
            if (string.Equals(constructedStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("Character construction failed: " + DialogueService.LimitText(ReadString(constructed, "diagnostic", ReadString(constructed, "error", "")), 500));
            }
            else if (!constructedReady)
            {
                issues.Add("Character has not completed construction yet.");
            }

            bool promptStackReady = profileReady && traitDescriptorsReady && voiceReady && stateReady && wealthReady && appearanceReady;
            bool fullReady = constructedReady && promptStackReady && backgroundReady && hiddenReady && motivationsReady && pressureReady && (!requiresNarrative || narrativeReady);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schemaVersion"] = Program.CharacterSchemaVersion,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["characterDir"] = dir,
                ["characterDirExists"] = Directory.Exists(dir),
                ["profile"] = new Dictionary<string, object>
                {
                    ["name"] = ReadString(profile, "name", heroId),
                    ["heroStringId"] = Program.CharacterIdFrom(profile),
                    ["archetype"] = CharacterArchetype(profile),
                    ["clanId"] = ReadString(profile, "clanId", ""),
                    ["kingdomId"] = ReadString(profile, "kingdomId", "")
                },
                ["constructed"] = constructed.Count == 0 ? new Dictionary<string, object> { ["status"] = "missing" } : constructed,
                ["readiness"] = new Dictionary<string, object>
                {
                    ["profileReady"] = profileReady,
                    ["wealthReady"] = wealthReady,
                    ["inventoryReady"] = inventoryReady,
                    ["appearanceReady"] = appearanceReady,
                    ["traitDescriptorsReady"] = traitDescriptorsReady,
                    ["backgroundReady"] = backgroundReady,
                    ["hiddenHistoryReady"] = hiddenReady,
                    ["voiceReady"] = voiceReady,
                    ["motivationsReady"] = motivationsReady,
                    ["saveQuirksReady"] = saveQuirksReady,
                    ["secretsReady"] = secretsReady,
                    ["pressureReady"] = pressureReady,
                    ["stateReady"] = stateReady,
                    ["promptStackReady"] = promptStackReady,
                    ["constructedReady"] = constructedReady
                },
                ["needsConstruction"] = needsConstruction,
                ["fullCharacterReady"] = fullReady,
                ["missingRequiredCount"] = missingRequired,
                ["files"] = files,
                ["issues"] = issues
            };
        }

        private static string CharacterRelativePath(string characterDir, string relative)
        {
            string clean = (relative ?? "").Trim().TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(clean))
            {
                return characterDir;
            }

            return Path.Combine(characterDir, clean.Replace("/", Path.DirectorySeparatorChar.ToString()));
        }

        internal static bool TraitDocumentReady(Dictionary<string, object> traits)
        {
            if (traits == null || traits.Count == 0)
            {
                return false;
            }

            Dictionary<string, object> visible = ReadDictionary(traits, "visibleBannerlordTraits") ?? new Dictionary<string, object>();
            Dictionary<string, object> hidden = ReadDictionary(traits, "foundationTraits") ?? ReadDictionary(traits, "hiddenReignTraits") ?? new Dictionary<string, object>();
            Dictionary<string, object> definitions = ReadDictionary(traits, "definitions") ?? new Dictionary<string, object>();
            return ReadInt(traits, "version", 0) >= CharactersService.CoreTraitDocumentVersion
                && CharactersService.FoundationTraitModelCurrent(traits)
                && traits.ContainsKey("visibleBannerlordTraits")
                && hidden.Count == CharactersService.CoreTraitKeys.Length
                && definitions.Count == CharactersService.CoreTraitKeys.Length
                && CharactersService.TraitPercentageDocumentReady(traits)
                && CharactersService.PersonalityPortraitReady(traits)
                && !string.IsNullOrWhiteSpace(ReadString(traits, "basePersonalitySummary", ""));
        }

        private static bool ObjectHasAnyContent(Dictionary<string, object> source, params string[] keys)
        {
            if (source == null || source.Count == 0)
            {
                return false;
            }

            foreach (string key in keys ?? new string[0])
            {
                if (!source.TryGetValue(key, out object value) || value == null)
                {
                    continue;
                }

                if (value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }

                if (value is ArrayList list && list.Count > 0)
                {
                    return true;
                }

                if (value is Dictionary<string, object> dict && dict.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static long CountJsonLines(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return 0;
                }

                return File.ReadLines(path, Encoding.UTF8).LongCount(line => !string.IsNullOrWhiteSpace(line));
            }
            catch
            {
                return 0;
            }
        }

        internal static Dictionary<string, object> MergeProfileForConstruction(string campaignId, string heroId, Dictionary<string, object> hero)
        {
            Dictionary<string, object> profile = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "profile.json"));
            foreach (KeyValuePair<string, object> pair in hero ?? new Dictionary<string, object>())
            {
                profile[pair.Key] = pair.Value;
            }

            profile["campaignId"] = campaignId;
            profile["heroStringId"] = heroId;
            return CharactersService.WithNarrativeCampaignVersion(campaignId, profile);
        }

        private static Dictionary<string, object> LoadCharacterTemplate(string heroId, Dictionary<string, object> profile)
        {
            string heroFile = Path.Combine(Program.CharacterTemplatesDir, Program.SafePathSegment(heroId, "unknown") + ".json");
            string heroDirFile = Path.Combine(Program.CharacterTemplatesDir, Program.SafePathSegment(heroId, "unknown"), "template.json");
            Dictionary<string, object> template = Program.ReadJsonObject(heroFile);
            if (template.Count == 0)
            {
                template = Program.ReadJsonObject(heroDirFile);
            }

            if (template.Count == 0)
            {
                template = new Dictionary<string, object>
                {
                    ["version"] = 1,
                    ["source"] = "generated_from_game_profile",
                    ["archetype"] = CharacterArchetype(profile),
                    ["notes"] = "No authored template found. Bannerlord Reign will construct this character from campaign facts, traits, and encyclopedia text."
                };
            }

            return template;
        }

        internal static Dictionary<string, object> BuildAttractiveness(Dictionary<string, object> hero)
        {
            double weight = NormalizeBodyPercent(ReadDouble(hero, "bodyWeight", ReadDouble(hero, "weight", 50)));
            double build = NormalizeBodyPercent(ReadDouble(hero, "bodyBuild", ReadDouble(hero, "build", 50)));
            bool isFemale = ReadBool(hero, "isFemale", false);
            int score;
            if (isFemale)
            {
                score = weight <= 25 ? 2 : weight <= 50 ? 1 : weight <= 75 ? 0 : -1;
            }
            else
            {
                int weightScore = weight <= 50 ? 1 : 0;
                int buildScore = build <= 25 ? -1 : build <= 50 ? 0 : 1;
                score = weightScore + buildScore;
            }

            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["score"] = Clamp(score, -2, 2),
                ["weight"] = Math.Round(weight, 2),
                ["build"] = Math.Round(build, 2),
                ["rule"] = isFemale ? "female_weight_scale" : "male_weight_plus_build_scale"
            };
        }

        private static double NormalizeBodyPercent(double value)
        {
            if (value >= 0 && value <= 1)
            {
                value *= 100d;
            }

            return Math.Max(0d, Math.Min(100d, value));
        }

        internal static int TraitInt(Dictionary<string, object> traits, string key)
        {
            return ClampTrait(ReadInt(traits, key, 0));
        }

        internal static int ClampTrait(int value)
        {
            return Clamp(value, -2, 2);
        }

        internal static string BuildCharacterConstructionPrompt(string campaignId, string heroId, Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            string noblePromptBlock = DialogueService.BuildNoblePromptBlock(hero);
            if (CharactersService.UsesCommonerRolePrompt(hero))
                noblePromptBlock = "WORLD TONE\n" + Program.LoadPromptTemplate("world_tone.txt") + "\n\n" + CharactersService.BuildCommonerPromptBlock(hero);
            bool notableMbti = ReadBool(hero, "isNotable", false)
                && ReadString(ReadDictionary(traits, "mbtiProfile"), "source", "")
                    .Equals("notable_mbti_template", StringComparison.OrdinalIgnoreCase);
            string foundationRule = notableMbti
                ? @"This notable has a permanent campaign MBTI assignment. The supplied 43 Reign traits and five native Bannerlord trait targets were generated from that assignment and are authoritative. Do not return traitAssignments and do not revise, reinterpret, or replace any supplied trait value. Use the MBTI type, description, and exact trait foundation to create compatible narrative material."
                : @"The rule-generated traits are the default foundation. Courage, Mercy, and Generosity are locked to the full-range conversion of their native Bannerlord values and must never change.
If nativeEncyclopediaText or encyclopediaText clearly describes this established character, make the other trait assignments match that description as closely as possible. Return only evidence-backed corrections; do not adjust traits merely for variety.";
            if (CharactersService.IsEncounteredResidentProfile(hero))
                foundationRule += "\nThis is a newly encountered ordinary person. Retain the exact supplied first and last name; never rename them. "
                    + "Their native occupation and home are their real work and residence. Missing native family links are not evidence of being single or childless: the supplied PERSONAL COMMONER HOUSEHOLD is authoritative biographical canon. "
                    + "skillsTemplateId supplies mechanics only: do not borrow that template's name, biography, appearance, clothes or relationships. "
                    + "Create an individual grounded life story, private history, voice, motives, quirks, secrets and complete personality appropriate to this person. "
                    + "Integrate the saved marital status, named partner and children into their story without changing that roster. Do not invent noble rank, political membership, native Hero family links or access to private royal/clan information. "
                    + "Keep implementation details and template identifiers out of all narrative fields.";
            return @"Construct this Bannerlord Reign character for one campaign save.

" + foundationRule + @"
Build on game-provided encyclopedia or canon text. Native descriptions and game facts are authoritative.
When nativeEncyclopediaText exists, preserve that text verbatim at the beginning of both background.summary and background.encyclopediaText, then build additional compatible material around it.
Write basePersonalitySummary as a rounded natural-language synthesis of the complete finalized trait set. It must describe the whole person rather than list trait labels or numbers. Use 1,200-1,600 characters and cover temperament, social behavior, attachment, status, decisions, emotional triggers, moral limits, pressure behavior, and contradictions.
Write personalityPortrait as the structured behavioral source beneath that summary. Each field should contain 1-3 precise sentences that explain how interacting traits shape conduct, not merely repeat adjectives.
The public encyclopediaText must be readable by the player and must not reveal private secrets, hidden schemes, jealousy targets, or pressure meters.
Private sections may include fears, desires, interests, jealousies, and volatile court hooks.
Keep the response compact enough to finish valid JSON:
- background.summary: 1-2 short paragraphs, under 900 characters total.
- encyclopediaText: 1-2 short paragraphs, under 700 characters total.
- hiddenHistory.privateBackstory: under 700 characters.
- arrays: maximum 3 items each, under 120 characters per item.
- all other string fields: one concise sentence unless the schema says otherwise.
- no markdown, no commentary, no trailing prose after the JSON object.

Campaign id:
" + campaignId + @"

Hero id:
" + heroId + @"

Game profile:
" + Json.Serialize(hero ?? new Dictionary<string, object>()) + @"

" + noblePromptBlock + @"

Rule-generated traits, fixed level descriptions, and attractiveness:
" + Json.Serialize(traits ?? new Dictionary<string, object>()) + @"

Return exactly this JSON shape:
{
  ""basePersonalitySummary"": ""rounded synthesis of the complete personality, 1200-1600 characters, no trait labels or numbers"",
  ""personalityPortrait"": {
    ""coreTemperament"": ""baseline courage, stability, patience, and outlook"",
    ""socialStyle"": ""conversation, assertion, tact, and social approach"",
    ""trustAndAttachment"": ""trust, loyalty, empathy, jealousy, and bonds"",
    ""powerAndStatus"": ""rank, pride, ambition, authority, and recognition"",
    ""decisionStyle"": ""discipline, curiosity, pragmatism, risk, and impulse"",
    ""emotionalPattern"": ""triggers, fear, anger, shame, and coping"",
    ""moralBoundaries"": ""honesty, compassion, mercy, aggression, and limits"",
    ""underPressure"": ""how this person changes when threatened or cornered"",
    ""internalContradictions"": ""at least two interacting needs that can pull conduct in different directions""
  },
  ""traitAssignments"": [
    { ""trait"": ""non-locked foundation trait key"", ""value"": 0, ""evidence"": ""specific support from the native character description"" }
  ],
  ""background"": {
    ""summary"": ""private full background in 1-3 paragraphs"",
    ""centralWound"": ""core wound or formative pressure"",
    ""publicReputation"": ""what others tend to think"",
    ""canonBuiltFrom"": [""short canon/game facts used""],
    ""encyclopediaText"": ""public readable encyclopedia backstory, 1-3 paragraphs, no private secrets""
  },
  ""voice"": {
    ""speechStyle"": ""how they speak"",
    ""socialMask"": ""how they present in public"",
    ""tells"": [""small behaviors""]
  },
  ""motivations"": {
    ""dreams"": [""long-term dreams""],
    ""fears"": [""private fears""],
    ""desires"": [""active desires""],
    ""linesTheyWillNotCross"": [""limits""]
  },
  ""saveQuirks"": {
    ""secretInterest"": ""save-specific interest"",
    ""jealousyTargetType"": ""type of person they envy or resent"",
    ""currentTemptation"": ""what could pull them into trouble""
  },
  ""hiddenHistory"": {
    ""privateBackstory"": ""campaign-specific private history the player does not automatically know"",
    ""secretWounds"": [""hidden wounds from this campaign seed""],
    ""privateEntanglements"": [""debts, scandals, attractions, blackmail, rivalries, or obligations""],
    ""knownOnlyTo"": [""who might know, if anyone""]
  },
  ""secrets"": {
    ""rumors"": [""rumors, uncertain""],
    ""privateTruths"": [""private truths, not player-facing""]
  },
  ""pressure"": {
    ""needForRecognition"": 0,
    ""resentment"": 0,
    ""loyaltyConflict"": 0,
    ""rebellionTemptation"": 0,
    ""romanticOrSocialRisk"": 0
  }
}";
        }

        internal static Dictionary<string, object> DictionaryOrDefault(Dictionary<string, object> source, string key, Dictionary<string, object> fallback)
        {
            Dictionary<string, object> value = ReadDictionary(source, key);
            return value == null || value.Count == 0 ? fallback : value;
        }

        internal static Dictionary<string, object> DefaultGeneratedCharacter(Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            return new Dictionary<string, object>
            {
                ["background"] = DefaultBackground(hero, traits),
                ["voice"] = DefaultVoice(hero, traits),
                ["motivations"] = DefaultMotivations(hero, traits),
                ["saveQuirks"] = DefaultSaveQuirks(hero, traits),
                ["hiddenHistory"] = DefaultHiddenHistory(hero),
                ["secrets"] = DefaultSecrets(hero),
                ["pressure"] = DefaultPressure(hero)
            };
        }

        private static Dictionary<string, object> DefaultBackground(Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            string name = ReadString(hero, "name", Program.CharacterIdFrom(hero));
            string role = CharacterArchetype(hero);
            string culture = ReadString(hero, "cultureId", "unknown culture");
            string text = name + " is a " + role + " shaped by " + culture + " customs, current loyalties, and the pressures of this campaign. " + ReadString(traits, "basePersonalitySummary", "Their behavior remains grounded in the native profile available to Reign.") + " This deterministic foundation is replaced by a complete campaign profile before the character's first interactive response.";
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["summary"] = text,
                ["centralWound"] = "",
                ["publicReputation"] = "",
                ["canonBuiltFrom"] = new ArrayList(),
                ["encyclopediaText"] = text
            };
        }

        private static Dictionary<string, object> DefaultVoice(Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["speechStyle"] = "Grounded, direct, and shaped by station.",
                ["socialMask"] = "Keeps enough composure to survive Calradian politics.",
                ["tells"] = new ArrayList()
            };
        }

        private static Dictionary<string, object> DefaultMotivations(Dictionary<string, object> hero)
        {
            return DefaultMotivations(hero, null);
        }

        private static Dictionary<string, object> DefaultMotivations(Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>();
            bool family = !string.IsNullOrWhiteSpace(ReadFirstString(hero, "spouseId", "fatherId", "motherId"));
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["dreams"] = new ArrayList { family ? "Secure a future in which family obligations do not erase personal agency." : "Build a place in the world that does not depend entirely on another person's favor." },
                ["fears"] = new ArrayList { TraitInt(foundation, "shame") >= 1 ? "Public disgrace that permanently changes how others judge them." : "Losing control of an important choice before they can prepare." },
                ["desires"] = new ArrayList { TraitInt(foundation, "ambition") >= 1 ? "Gain meaningful influence over the decisions around them." : "Protect enough independence to choose which obligations deserve loyalty." },
                ["linesTheyWillNotCross"] = new ArrayList { TraitInt(foundation, "mercy") >= 1 ? "Will not casually punish helpless people for another person's offense." : "Will not accept a bargain that leaves them permanently powerless." }
            };
        }

        private static Dictionary<string, object> DefaultSaveQuirks(Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["secretInterest"] = "A private interest that offers identity beyond their public occupation.",
                ["jealousyTargetType"] = "Someone who receives freedom, affection, or recognition they feel denied.",
                ["currentTemptation"] = "A risky opportunity to exchange obligation for independence."
            };
        }

        private static Dictionary<string, object> DefaultSecrets(Dictionary<string, object> hero)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["rumors"] = new ArrayList(),
                ["privateTruths"] = new ArrayList { "They privately question at least one obligation they continue to honor in public." }
            };
        }

        private static Dictionary<string, object> DefaultHiddenHistory(Dictionary<string, object> hero)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["privateBackstory"] = "Their public role conceals a private history of compromises, deferred choices, and obligations that have not been emotionally settled.",
                ["secretWounds"] = new ArrayList { "They fear being valued only while useful." },
                ["privateEntanglements"] = new ArrayList { "An unresolved favor or promise can still pull them away from their preferred course." },
                ["knownOnlyTo"] = new ArrayList { "The character and anyone who independently discovers it." }
            };
        }

        internal static Dictionary<string, object> DefaultPressure(Dictionary<string, object> hero)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["scale"] = "0..100",
                ["needForRecognition"] = 20,
                ["resentment"] = Math.Max(0, -ReadInt(hero, "relationToPlayer", 0)),
                ["loyaltyConflict"] = 0,
                ["rebellionTemptation"] = 0,
                ["romanticOrSocialRisk"] = 0
            };
        }

        internal static void EnsureEncyclopediaText(Dictionary<string, object> background, Dictionary<string, object> hero, Dictionary<string, object> traits)
        {
            if (background == null)
            {
                return;
            }

            string nativeText = ReadString(hero, "nativeEncyclopediaText", "").Trim();
            if (!string.IsNullOrWhiteSpace(nativeText))
            {
                string generatedSummary = ReadString(background, "summary", "").Trim();
                string generatedPublic = ReadString(background, "encyclopediaText", "").Trim();
                string priorNative = ReadString(background, "nativeEncyclopediaText", "").Trim();
                generatedSummary = RemoveLeadingNativeBackground(priorNative, generatedSummary);
                generatedPublic = RemoveLeadingNativeBackground(priorNative, generatedPublic);
                background["nativeEncyclopediaText"] = nativeText;
                background["nativeTextPreserved"] = true;
                background["summary"] = BuildAroundNativeBackground(nativeText, generatedSummary);
                background["encyclopediaText"] = BuildAroundNativeBackground(nativeText, generatedPublic);
                return;
            }

            if (!string.IsNullOrWhiteSpace(ReadString(background, "encyclopediaText", "")))
            {
                return;
            }

            background["encyclopediaText"] = ReadString(background, "summary", ReadString(DefaultBackground(hero, traits), "summary", ""));
        }

        internal static string BuildAroundNativeBackground(string nativeText, string generatedText)
        {
            nativeText = (nativeText ?? "").Trim();
            generatedText = (generatedText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nativeText)) return generatedText;
            if (string.IsNullOrWhiteSpace(generatedText) || generatedText.Equals(nativeText, StringComparison.Ordinal)) return nativeText;
            if (generatedText.StartsWith(nativeText, StringComparison.Ordinal)) return generatedText;
            return nativeText + "\n\n" + generatedText;
        }

        private static string RemoveLeadingNativeBackground(string nativeText, string generatedText)
        {
            nativeText = (nativeText ?? "").Trim();
            generatedText = (generatedText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nativeText) || !generatedText.StartsWith(nativeText, StringComparison.Ordinal)) return generatedText;
            return generatedText.Substring(nativeText.Length).TrimStart('\r', '\n', ' ');
        }

        internal static string BuildEncyclopediaBackstoryText(string campaignId, string heroId)
        {
            Dictionary<string, object> background = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "background.json"));
            return ReadString(background, "encyclopediaText", ReadString(background, "summary", ""));
        }

        internal static Dictionary<string, object> LoadCharacterStack(string campaignId, string heroId)
        {
            Dictionary<string, object> stack = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "characteristics.json"));
            Dictionary<string, object> profile = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "profile.json"));
            Dictionary<string, object> traits = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"));
            if (traits.Count > 0)
            {
                bool changed = CharactersService.EnsureTraitPercentageData(traits, heroId);
                changed = CourtService.EnsureCourtCharacterData(traits, profile, heroId) || changed;
                if (changed) Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"), traits);
            }
            stack["traits"] = traits;
            if (profile.Count > 0) stack["politicalProfile"] = TryParseJsonObject(System.Text.Json.JsonSerializer.Serialize(CharactersService.EnsurePoliticalProfile(campaignId, heroId, profile, traits)));
            stack["wealth"] = CharactersService.ResolveWealthDocument(campaignId, heroId, profile);
            stack["inventory"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "inventory.json"));
            stack["appearance"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "appearance.json"));
            stack["background"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "background.json"));
            stack["voice"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "voice.json"));
            stack["motivations"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "motivations.json"));
            stack["saveQuirks"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "save_quirks.json"));
            stack["hiddenHistory"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "hidden_history.json"));
            stack["secrets"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "secrets.json"));
            stack["pressure"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "pressure.json"));
            stack["dynamicCharacteristics"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "dynamic_characteristics.json"));
            stack["narrative"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "narrative.json"));
            CharactersService.ApplyNarrativePromptProjection(stack);
            stack["constructed"] = Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "constructed.json"));
            return stack;
        }

        internal static string CharacterArchetype(Dictionary<string, object> hero)
        {
            if (ReadBool(hero, "isLord", false))
            {
                return "lord";
            }

            if (ReadBool(hero, "isNotable", false))
            {
                return "notable";
            }

            if (ReadBool(hero, "isWanderer", false))
            {
                return "wanderer";
            }

            return ReadString(hero, "occupation", "person");
        }
    }
}
