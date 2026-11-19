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
        internal static Dictionary<string, object> UpsertCharacter(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> hero = ReadDictionary(payload, "hero")
                ?? ReadDictionary(payload, "profile")
                ?? payload;
            string campaignId = ReadString(payload, "campaignId", ReadString(hero, "campaignId", "default"));
            string heroId = CampaignFileService.CharacterIdFrom(hero);
            if (string.IsNullOrWhiteSpace(heroId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            }

            Dictionary<string, object> result = UpsertCharacterProfile(campaignId, hero);
            OperationsService.LogOperational("characters.upsert", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["created"] = ReadBool(result, "created", false)
            });
            return result;
        }

        internal static Dictionary<string, object> UpsertCharacterBatch(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes");
            if (heroes.Count == 0)
            {
                heroes = ReadDictionaryList(payload, "characters");
            }

            int created = 0;
            int updated = 0;
            int failed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> hero in heroes)
            {
                try
                {
                    if (hero != null && !hero.ContainsKey("campaignId"))
                    {
                        hero["campaignId"] = campaignId;
                    }

                    Dictionary<string, object> result = UpsertCharacterProfile(campaignId, hero);
                    if (ReadBool(result, "created", false))
                    {
                        created++;
                    }
                    else
                    {
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add(new Dictionary<string, object>
                    {
                        ["heroStringId"] = CampaignFileService.CharacterIdFrom(hero),
                        ["error"] = ex.Message
                    });
                }
            }

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = failed == 0,
                ["campaignId"] = campaignId,
                ["created"] = created,
                ["updated"] = updated,
                ["failed"] = failed,
                ["total"] = heroes.Count,
                ["failures"] = failures
            };
            OperationsService.LogOperational("characters.upsert_batch", response);
            return response;
        }

        internal static Dictionary<string, object> IdentitySynchronizeWithPortraitRosterUpgradeApi(Dictionary<string, object> payload)
        {
            Dictionary<string, object> response = CharactersService.IdentitySynchronizeApi(payload);
            string campaignId = ReadString(payload, "campaignId", "default");
            string expectedFingerprint = ReadString(payload, "portraitRosterFingerprint", "");
            Dictionary<string, object> savedRoster = CampaignFileService.ReadJsonObject(CampaignFileService.CampaignFile(campaignId, "portrait_roster.json"));
            if (!string.IsNullOrWhiteSpace(expectedFingerprint)
                && ReadInt(savedRoster, "version", 0) < 3)
            {
                response["portraitRosterSyncRequired"] = true;
            }
            return response;
        }

        internal static Dictionary<string, object> UpsertPortraitRoster(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string rosterFingerprint = ReadString(payload, "rosterFingerprint", "");
            List<Dictionary<string, object>> characters = ReadDictionaryList(payload, "characters");
            if (characters.Count == 0)
            {
                characters = ReadDictionaryList(payload, "heroes");
            }

            characters = characters
                .Where(character => !string.IsNullOrWhiteSpace(CampaignFileService.CharacterIdFrom(character)))
                .GroupBy(CampaignFileService.CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Select(CompactPortraitRosterEntry)
                .OrderBy(character => ReadString(character, "name", CampaignFileService.CharacterIdFrom(character)), StringComparer.OrdinalIgnoreCase)
                .ToList();
            string path = CampaignFileService.CampaignFile(campaignId, "portrait_roster.json");
            Dictionary<string, object> existing = CampaignFileService.ReadJsonObject(path);
            if (ReadInt(existing, "version", 0) >= 3
                && !string.IsNullOrWhiteSpace(rosterFingerprint)
                && string.Equals(ReadString(existing, "rosterFingerprint", ""), rosterFingerprint, StringComparison.Ordinal))
            {
                Dictionary<string, object> unchangedResponse = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["unchanged"] = true,
                    ["campaignId"] = campaignId,
                    ["characterCount"] = characters.Count,
                    ["rosterFingerprint"] = rosterFingerprint,
                    ["path"] = path
                };
                OperationsService.LogOperational("portrait_roster.unchanged", unchangedResponse);
                return unchangedResponse;
            }

            Dictionary<string, object> document = new Dictionary<string, object>
            {
                ["version"] = 3,
                ["campaignId"] = campaignId,
                ["campaignLabel"] = ReadString(payload, "campaignLabel", ""),
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["characterCount"] = characters.Count,
                ["rosterFingerprint"] = rosterFingerprint,
                ["characters"] = characters
            };
            CampaignFileService.WriteJsonObject(path, document);
            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["unchanged"] = false,
                ["campaignId"] = campaignId,
                ["characterCount"] = characters.Count,
                ["rosterFingerprint"] = rosterFingerprint,
                ["path"] = path
            };
            OperationsService.LogOperational("portrait_roster.upsert", response);
            return response;
        }

        private static Dictionary<string, object> CompactPortraitRosterEntry(Dictionary<string, object> character)
        {
            character = character ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["heroStringId"] = CampaignFileService.CharacterIdFrom(character),
                ["characterObjectId"] = ReadString(character, "characterObjectId", ""),
                ["name"] = ReadString(character, "name", CampaignFileService.CharacterIdFrom(character)),
                ["cultureId"] = ReadString(character, "cultureId", ""),
                ["occupation"] = ReadString(character, "occupation", ""),
                ["isFemale"] = ReadBool(character, "isFemale", false),
                ["isLord"] = ReadBool(character, "isLord", false),
                ["isNotable"] = ReadBool(character, "isNotable", false),
                ["isWanderer"] = ReadBool(character, "isWanderer", false),
                ["bodyKey"] = ReadString(character, "bodyKey", ""),
                ["civilianEquipment"] = CompactPortraitEquipment(character),
                ["portraitCacheKey"] = ReadString(character, "portraitCacheKey", "")
            };
        }

        private static List<Dictionary<string, object>> CompactPortraitEquipment(Dictionary<string, object> character)
        {
            return ReadDictionaryList(character, "civilianEquipment")
                .Select(item => new Dictionary<string, object>
                {
                    ["slot"] = ReadString(item, "slot", ""),
                    ["itemId"] = ReadString(item, "itemId", "")
                })
                .Where(item => !string.IsNullOrWhiteSpace(ReadString(item, "slot", ""))
                    && !string.IsNullOrWhiteSpace(ReadString(item, "itemId", "")))
                .GroupBy(item => ReadString(item, "slot", ""), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        internal static Dictionary<string, object> ConstructCharacter(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> hero = ReadDictionary(payload, "hero")
                ?? ReadDictionary(payload, "profile")
                ?? payload;
            string campaignId = ReadString(payload, "campaignId", ReadString(hero, "campaignId", "default"));
            if (hero != null && !hero.ContainsKey("campaignId"))
            {
                hero["campaignId"] = campaignId;
            }

            if (hero != null && !string.IsNullOrWhiteSpace(CampaignFileService.CharacterIdFrom(hero)))
            {
                UpsertCharacterProfile(campaignId, hero);
            }

            string heroId = CampaignFileService.CharacterIdFrom(hero);
            if (string.IsNullOrWhiteSpace(heroId))
            {
                heroId = ReadString(payload, "heroStringId", ReadString(payload, "characterId", ""));
            }

            if (string.IsNullOrWhiteSpace(heroId))
            {
                throw new InvalidOperationException("heroStringId is required.");
            }

            bool force = ReadBool(payload, "force", false);
            bool useLlm = ReadBool(payload, "useLlm", ReadBool(SettingsService.LoadSettings(), "constructCharactersWithLlm", true));
            Dictionary<string, object> result = ConstructCharacterFiles(campaignId, heroId, hero, force, useLlm);
            OperationsService.LogOperational("characters.construct", result);
            return result;
        }

        internal static Dictionary<string, object> ConstructCharacterBatch(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes");
            if (heroes.Count == 0)
            {
                heroes = ReadDictionaryList(payload, "characters");
            }

            bool force = ReadBool(payload, "force", false);
            bool useLlm = ReadBool(payload, "useLlm", ReadBool(SettingsService.LoadSettings(), "constructCharactersWithLlm", true));
            int constructed = 0;
            int failed = 0;
            int llmUsed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> hero in heroes)
            {
                Program.WaitForInteractiveRequests();
                try
                {
                    if (hero != null && !hero.ContainsKey("campaignId"))
                    {
                        hero["campaignId"] = campaignId;
                    }

                    UpsertCharacterProfile(campaignId, hero);
                    Dictionary<string, object> result = ConstructCharacterFiles(campaignId, CampaignFileService.CharacterIdFrom(hero), hero, force, useLlm);
                    if (ReadBool(result, "ok", false))
                    {
                        constructed++;
                        if (ReadBool(result, "llmUsed", false))
                        {
                            llmUsed++;
                        }
                    }
                    else
                    {
                        failed++;
                        failures.Add(new Dictionary<string, object>
                        {
                            ["heroStringId"] = CampaignFileService.CharacterIdFrom(hero),
                            ["error"] = ReadString(result, "diagnostic", ReadString(result, "error", "Character generation failed.")),
                            ["result"] = result
                        });
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add(new Dictionary<string, object>
                    {
                        ["heroStringId"] = CampaignFileService.CharacterIdFrom(hero),
                        ["error"] = ex.Message
                    });
                }
            }

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = failed == 0,
                ["campaignId"] = campaignId,
                ["constructed"] = constructed,
                ["failed"] = failed,
                ["total"] = heroes.Count,
                ["llmUsed"] = llmUsed,
                ["failures"] = failures
            };
            OperationsService.LogOperational("characters.construct_batch", response);
            return response;
        }

        internal static Dictionary<string, object> RetryFailedCharacters(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            int retried = 0;
            int recovered = 0;
            int failed = 0;
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();

            List<string> characterDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(heroId))
            {
                characterDirs.Add(CampaignFileService.CharacterDirectory(campaignId, heroId));
            }
            else
            {
                string charactersDir = Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "characters");
                if (Directory.Exists(charactersDir))
                {
                    characterDirs.AddRange(Directory.GetDirectories(charactersDir));
                }
            }

            foreach (string dir in characterDirs)
            {
                Dictionary<string, object> constructed = CampaignFileService.ReadJsonObject(Path.Combine(dir, "constructed.json"));
                if (!string.Equals(ReadString(constructed, "status", ""), "failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string id = Path.GetFileName(dir);
                Dictionary<string, object> profile = CampaignFileService.ReadJsonObject(Path.Combine(dir, "profile.json"));
                if (!profile.ContainsKey("heroStringId"))
                {
                    profile["heroStringId"] = id;
                }

                retried++;
                Dictionary<string, object> result = ConstructCharacterFiles(campaignId, id, profile, force: true, useLlm: true, source: "retry_failed");
                if (IsCharacterConstructionReady(result))
                {
                    recovered++;
                }
                else
                {
                    failed++;
                }

                results.Add(new Dictionary<string, object>
                {
                    ["heroStringId"] = id,
                    ["ok"] = ReadBool(result, "ok", false),
                    ["status"] = ReadString(result, "status", ""),
                    ["diagnostic"] = ReadString(result, "diagnostic", ReadString(result, "error", ""))
                });
            }

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = failed == 0,
                ["campaignId"] = campaignId,
                ["retried"] = retried,
                ["recovered"] = recovered,
                ["failed"] = failed,
                ["results"] = results
            };
            OperationsService.LogOperational("characters.retry_failed", response);
            return response;
        }

        internal static Dictionary<string, object> EnsureCharacterConstructed(string campaignId, string heroId, Dictionary<string, object> hero, string source)
            => CharactersService.WithCharacterConstructionGate(campaignId, heroId, source,
                () => EnsureCharacterConstructedCore(campaignId, heroId, hero, source));

        private static Dictionary<string, object> EnsureCharacterConstructedCore(string campaignId, string heroId, Dictionary<string, object> hero, string source)
        {
            hero = MergeProfileForConstruction(campaignId, heroId, hero);
            if (CharactersService.IsEncounteredResidentProfile(hero))
                return CharactersService.EnsureEncounteredResidentConstructed(campaignId, heroId, hero, source);
            if (ReadBool(hero, "isNotable", false))
            {
                using (ReignDbConnection mbtiConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    RelationshipsService.EnsureNotableMbtiProfile(mbtiConnection, campaignId, ReadDouble(hero, "worldDay", 0d), hero, true);
                }
            }
            if (ReadInt(hero, "narrativeVersion", 0) >= 5)
                return CharactersService.EnsureNarrativeCharacterConstructed(campaignId, heroId, hero, source);
            Dictionary<string, object> status = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "constructed.json"));
            string currentStatus = ReadString(status, "status", "");
            Dictionary<string, object> traits = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"));
            if (traits.Count > 0 && CharactersService.EnsureTraitPercentageData(traits, heroId))
            {
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"), traits);
            }
            bool traitModelReady = TraitDocumentReady(traits);
            bool requiresFirstContactConstruction = CharactersService.RequiresFirstContactConstruction(campaignId, heroId, status);
            if (traitModelReady && IsCharacterEnrichmentPending(campaignId, heroId))
            {
                status["ok"] = true;
                status["status"] = "fallback_ready";
                status["backgroundEnrichmentPending"] = true;
                return status;
            }
            if (IsUsableCharacterStatus(currentStatus) && traitModelReady && !requiresFirstContactConstruction)
            {
                status["ok"] = true;
                status["alreadyReady"] = true;
                return status;
            }

            if (IsUsableCharacterStatus(currentStatus) && !traitModelReady && !requiresFirstContactConstruction)
            {
                Dictionary<string, object> migrated = ConstructCharacterFiles(campaignId, heroId, hero, force: true, useLlm: true, source: "trait_model_v2_migration");
                if (IsCharacterConstructionReady(migrated))
                {
                    return migrated;
                }

                Dictionary<string, object> migratedTraits = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"));
                if (TraitDocumentReady(migratedTraits))
                {
                    status["ok"] = true;
                    status["status"] = "ready";
                    status["schemaVersion"] = Program.CharacterSchemaVersion;
                    status["traitMigrationFallback"] = true;
                    status["traitMigrationError"] = ReadString(migrated, "diagnostic", ReadString(migrated, "error", ""));
                    status["updatedUtc"] = DateTime.UtcNow.ToString("o");
                    CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "constructed.json"), status);
                    return status;
                }

                return migrated;
            }

            if (string.Equals(currentStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> settings = SettingsService.LoadSettings();
                if (ShouldRetryFailedConstruction(status, settings))
                {
                    status["autoRetryAfterSettingsChange"] = true;
                    OperationsService.LogOperational("character_construction.retry_after_settings_change", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = heroId,
                        ["failedRevision"] = ReadString(status, "settingsRevision", ""),
                        ["currentRevision"] = ReadString(settings, "settingsRevision", "")
                    });
                    return ConstructCharacterFiles(campaignId, heroId, hero, force: true, useLlm: true, source: source + "_settings_retry");
                }

                status["ok"] = false;
                status["alreadyFailed"] = true;
                return status;
            }

            OperationsService.LogOperational("character_construction.first_contact_started", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["source"] = source ?? "interactive",
                ["previousStatus"] = currentStatus,
                ["requiresFirstContactConstruction"] = requiresFirstContactConstruction
            });
            Dictionary<string, object> result = ConstructCharacterFiles(campaignId, heroId, hero,
                force: requiresFirstContactConstruction, useLlm: false, source: source + "_first_contact_scaffold");
            if (IsCharacterConstructionReady(result))
            {
                bool needsEnrichment = !CharactersService.LoadCharacterProfileLibrary().ContainsKey(heroId)
                    && !CharactersService.CharacterHasEditorCorrections(campaignId, heroId)
                    && !ReadBool(result, "llmUsed", false);
                if (needsEnrichment)
                {
                    ScheduleCharacterEnrichment(campaignId, heroId, hero, source);
                    result["backgroundEnrichmentPending"] = true;
                }
                OperationsService.LogOperational("character_construction.first_contact_completed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["source"] = source ?? "interactive",
                    ["llmUsed"] = ReadBool(result, "llmUsed", false),
                    ["durationMs"] = ReadLong(result, "durationMs", 0)
                });
            }
            else
            {
                OperationsService.LogOperational("character_construction.first_contact_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["source"] = source ?? "interactive",
                    ["status"] = ReadString(result, "status", ""),
                    ["diagnostic"] = DialogueService.LimitText(ReadString(result, "diagnostic", ReadString(result, "error", "")), 1000)
                });
            }
            return result;
        }

        private static bool IsCharacterEnrichmentPending(string campaignId, string heroId)
        {
            string key = (campaignId ?? "default") + "|" + (heroId ?? "");
            lock (Program.CharacterEnrichmentLock) return Program.PendingCharacterEnrichments.Contains(key);
        }

        internal static int PendingCharacterEnrichmentCount()
        {
            int legacy;
            lock (Program.CharacterEnrichmentLock) legacy = Program.PendingCharacterEnrichments.Count;
            return legacy + CharactersService.TownProfiles.Count;
        }

        private static void ScheduleCharacterEnrichment(string campaignId, string heroId,
            Dictionary<string, object> hero, string interactionSource)
        {
            string key = (campaignId ?? "default") + "|" + (heroId ?? "");
            lock (Program.CharacterEnrichmentLock)
            {
                if (!Program.PendingCharacterEnrichments.Add(key)) return;
            }
            Dictionary<string, object> capturedHero = new Dictionary<string, object>(
                hero ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            // A never-started release (busy, stopping or canceled before start) frees exactly this
            // key; once started, the callback's own finally owns it.
            OperationsService.TryEnqueuePriorityBackgroundWork("character.enrichment", "maintenance", () =>
            {
                try
                {
                    Dictionary<string, object> enriched = ConstructCharacterFiles(campaignId, heroId,
                        capturedHero, force: true, useLlm: true, source: "background_enrichment");
                    if (IsCharacterConstructionReady(enriched) && ReadBool(enriched, "llmUsed", false))
                        CharactersService.MarkCharacterConstructionCompletedOnInteraction(campaignId, heroId,
                            FirstNonEmpty(interactionSource, "background_enrichment"));
                }
                finally
                {
                    lock (Program.CharacterEnrichmentLock) Program.PendingCharacterEnrichments.Remove(key);
                }
            }, _ =>
            {
                lock (Program.CharacterEnrichmentLock) Program.PendingCharacterEnrichments.Remove(key);
            }, out _);
        }

        internal static Dictionary<string, object> ConstructCharacterFiles(string campaignId, string heroId, Dictionary<string, object> hero, bool force, bool useLlm)
        {
            return ConstructCharacterFiles(campaignId, heroId, hero, force, useLlm, "manual");
        }

        internal static Dictionary<string, object> ConstructCharacterFiles(string campaignId, string heroId, Dictionary<string, object> hero, bool force, bool useLlm, string source)
        {
            if (string.IsNullOrWhiteSpace(heroId))
            {
                throw new InvalidOperationException("heroStringId is required.");
            }

            hero = MergeProfileForConstruction(campaignId, heroId, hero);
            if (ReadInt(hero, "narrativeVersion", 0) >= 5)
            {
                if (CharactersService.LoadCharacterProfileLibrary().ContainsKey(heroId))
                    return CharactersService.MaterializeCharacterProfile(campaignId, heroId, hero, force, false);
                if (useLlm) return CharactersService.ConstructNarrativeCharacter(campaignId, heroId, hero, force, source);
            }
            string constructedPath = CampaignFileService.CharacterFile(campaignId, heroId, "constructed.json");
            Dictionary<string, object> existing = CampaignFileService.ReadJsonObject(constructedPath);
            string existingStatus = ReadString(existing, "status", "");
            if (!force && IsUsableCharacterStatus(existingStatus)
                && TraitDocumentReady(CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"))))
            {
                existing["ok"] = true;
                existing["alreadyReady"] = true;
                return existing;
            }

            if (!force && string.Equals(existingStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> settings = SettingsService.LoadSettings();
                if (!ShouldRetryFailedConstruction(existing, settings))
                {
                    existing["ok"] = false;
                    existing["alreadyFailed"] = true;
                    return existing;
                }
            }

            Stopwatch timer = Stopwatch.StartNew();
            Dictionary<string, object> constructionSettings = SettingsService.LoadSettings();
            CampaignFileService.WriteJsonObject(constructedPath, new Dictionary<string, object>
            {
                ["version"] = 1,
                ["constructionEngineVersion"] = Program.CharacterConstructionEngineVersion,
                ["status"] = "constructing",
                ["source"] = source ?? "unknown",
                ["settingsRevision"] = ReadString(constructionSettings, "settingsRevision", ""),
                ["settingsFingerprint"] = SettingsService.SettingsFingerprint(constructionSettings),
                ["startedUtc"] = DateTime.UtcNow.ToString("o")
            });

            Dictionary<string, object> traits;
            if (ReadBool(hero, "isNotable", false))
            {
                using (ReignDbConnection mbtiConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> mbti = RelationshipsService.EnsureNotableMbtiProfile(
                        mbtiConnection, campaignId, ReadDouble(hero, "worldDay", 0d), hero, false);
                    traits = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"));
                    if (traits.Count == 0)
                    {
                        traits = RelationshipsService.BuildNotableMbtiTraitDocument(
                            campaignId, heroId, ReadString(hero, "name", heroId), ReadString(mbti, "type", "INTJ"));
                    }
                }
            }
            else
            {
                traits = CharactersService.BuildTraitDocument(hero);
            }
            CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"), traits);

            Dictionary<string, object> generated = null;
            Dictionary<string, object> llm = null;
            List<Dictionary<string, object>> attempts = new List<Dictionary<string, object>>();
            bool llmUsed = false;
            if (useLlm)
            {
                for (int attempt = 1; attempt <= 2 && generated == null; attempt++)
                {
                    int attemptMaxTokens = CharacterConstructionMaxTokensForAttempt(
                        ReadInt(constructionSettings, "characterConstructionMaxTokens", Program.CharacterConstructionTokenCeiling),
                        attempt);
                    llm = LlmProviderService.ChatWithLlm(new Dictionary<string, object>
                    {
                        ["requestType"] = "character_construction",
                        ["maxTokens"] = attemptMaxTokens,
                        ["temperature"] = attempt == 1 ? 0.75d : 0.25d,
                        ["messages"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["role"] = "system",
                                ["content"] = "You are Bannerlord Reign's character construction engine. Return strict JSON only. Do not wrap it in markdown. Build a grounded Mount & Blade II: Bannerlord character from authoritative game facts. Native character descriptions outrank inferred Reign traits. You may correct non-locked trait assignments only when the native description clearly supports the correction. Do not contradict canon/game facts."
                            },
                            new Dictionary<string, object>
                            {
                                ["role"] = "user",
                                ["content"] = BuildCharacterConstructionPromptForAttempt(campaignId, heroId, hero, traits, attempt)
                            }
                        },
                        ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
                    });

                    Dictionary<string, object> attemptRecord = CharacterConstructionAttemptRecord(attempt, llm);
                    attemptRecord["maxTokens"] = attemptMaxTokens;
                    attemptRecord["adaptiveRetry"] = attempt > 1;
                    if (ReadBool(llm, "ok", false))
                    {
                        string content = ReadString(llm, "content", "");
                        generated = TryParseJsonObject(content);
                        attemptRecord["contentChars"] = content.Length;
                        attemptRecord["parsed"] = generated != null;
                        if (generated == null)
                        {
                            string finishReason = ReadString(llm, "finishReason", "");
                            attemptRecord["error"] = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                                ? "Character construction response hit the max token limit before valid JSON completed."
                                : "Character construction response was not valid JSON.";
                        }
                    }

                    attempts.Add(attemptRecord);
                }

                llmUsed = generated != null;
            }

            if (generated == null)
            {
                if (useLlm && !string.Equals(source, "background_enrichment", StringComparison.OrdinalIgnoreCase))
                {
                    string diagnostic = BuildCharacterConstructionDiagnostic(heroId, hero, attempts, llm);
                    Dictionary<string, object> failed = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["version"] = 1,
                        ["constructionEngineVersion"] = Program.CharacterConstructionEngineVersion,
                        ["status"] = "failed",
                        ["schemaVersion"] = Program.CharacterSchemaVersion,
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = heroId,
                        ["source"] = source ?? "unknown",
                        ["llmUsed"] = false,
                        ["attempts"] = attempts,
                        ["settingsRevision"] = ReadString(constructionSettings, "settingsRevision", ""),
                        ["settingsFingerprint"] = SettingsService.SettingsFingerprint(constructionSettings),
                        ["failureKind"] = CharacterConstructionFailureKind(attempts, llm),
                        ["diagnostic"] = diagnostic,
                        ["error"] = diagnostic,
                        ["failedUtc"] = DateTime.UtcNow.ToString("o"),
                        ["durationMs"] = timer.ElapsedMilliseconds
                    };
                    CampaignFileService.WriteJsonObject(constructedPath, failed);
                    CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "decisions.jsonl"), new Dictionary<string, object>
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["type"] = "character_construction",
                        ["status"] = "failed",
                        ["source"] = source ?? "unknown",
                        ["attempts"] = attempts,
                        ["diagnostic"] = diagnostic,
                        ["durationMs"] = timer.ElapsedMilliseconds
                    });
                    OperationsService.LogOperational("character_construction.failed", failed);
                    return failed;
                }

                generated = DefaultGeneratedCharacter(hero, traits);
            }

            lock (CharactersService.NarrativeStateLock)
            {
                if (source == CharactersService.TownNotableProfileSource && !CharactersService.TownProfilePublicationAllowed(campaignId, heroId))
                    return TestDict("ok", false, "status", "speculative_superseded");
                if (ReadBool(hero, "isNotable", false))
                {
                    if (!CharactersService.CharacterHasEditorCorrections(campaignId, heroId))
                    {
                        traits["assignmentOverrides"] = new ArrayList();
                        CharactersService.EnsureTraitPercentageData(traits, heroId);
                        CharactersService.ApplyGeneratedPersonalityPortrait(traits, generated, ReadString(hero, "name", heroId));
                    }
                }
                else
                {
                    CharactersService.ApplyGeneratedTraitConstruction(traits, generated, hero);
                }
                CourtService.EnsureCourtCharacterData(traits, hero, heroId);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "traits.json"), traits);

                Dictionary<string, object> background = DictionaryOrDefault(generated, "background", DefaultBackground(hero, traits));
                EnsureEncyclopediaText(background, hero, traits);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "background.json"), background);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "voice.json"), DictionaryOrDefault(generated, "voice", DefaultVoice(hero, traits)));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "motivations.json"), DictionaryOrDefault(generated, "motivations", DefaultMotivations(hero)));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "save_quirks.json"), DictionaryOrDefault(generated, "saveQuirks", DictionaryOrDefault(generated, "save_quirks", DefaultSaveQuirks(hero, traits))));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "hidden_history.json"), DictionaryOrDefault(generated, "hiddenHistory", DictionaryOrDefault(generated, "hidden_history", DefaultHiddenHistory(hero))));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "secrets.json"), DictionaryOrDefault(generated, "secrets", DefaultSecrets(hero)));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "pressure.json"), DictionaryOrDefault(generated, "pressure", DefaultPressure(hero)));

                string status = llmUsed ? "ready" : "fallback_ready";
                Dictionary<string, object> constructed = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["version"] = 1,
                    ["constructionEngineVersion"] = Program.CharacterConstructionEngineVersion,
                    ["status"] = status,
                    ["schemaVersion"] = Program.CharacterSchemaVersion,
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["source"] = source ?? "unknown",
                    ["llmUsed"] = llmUsed,
                    ["llmError"] = llm == null ? "" : ReadString(llm, "error", ""),
                    ["attempts"] = attempts,
                    ["settingsRevision"] = ReadString(constructionSettings, "settingsRevision", ""),
                    ["settingsFingerprint"] = SettingsService.SettingsFingerprint(constructionSettings),
                    ["constructedUtc"] = DateTime.UtcNow.ToString("o"),
                    ["durationMs"] = timer.ElapsedMilliseconds
                };
                CampaignFileService.WriteJsonObject(constructedPath, constructed);
                CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "decisions.jsonl"), new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["type"] = "character_construction",
                    ["status"] = status,
                    ["source"] = source ?? "unknown",
                    ["llmUsed"] = llmUsed,
                    ["durationMs"] = timer.ElapsedMilliseconds
                });

                return constructed;
            }
        }

        private static Dictionary<string, object> CharacterConstructionAttemptRecord(int attempt, Dictionary<string, object> llm)
        {
            llm = llm ?? new Dictionary<string, object>();
            Dictionary<string, object> record = new Dictionary<string, object>
            {
                ["attempt"] = attempt,
                ["ok"] = ReadBool(llm, "ok", false),
                ["requestType"] = ReadString(llm, "requestType", "character_construction"),
                ["model"] = ReadString(llm, "model", ""),
                ["durationMs"] = ReadLong(llm, "durationMs", 0),
                ["requestChars"] = ReadLong(llm, "requestChars", 0),
                ["responseChars"] = ReadLong(llm, "responseChars", 0)
            };

            string error = ReadString(llm, "error", "");
            if (!string.IsNullOrWhiteSpace(error))
            {
                record["error"] = error;
            }
            else if (!ReadBool(llm, "ok", false))
            {
                record["error"] = "LLM call failed without a provider error message.";
            }

            string finishReason = ReadString(llm, "finishReason", "");
            if (!string.IsNullOrWhiteSpace(finishReason))
            {
                record["finishReason"] = finishReason;
            }

            return record;
        }

        internal static int CharacterConstructionMaxTokensForAttempt(int configuredMaxTokens, int attempt)
        {
            int configured = Math.Max(1200, Math.Min(Program.CharacterConstructionTokenCeiling, configuredMaxTokens));
            if (attempt <= 1)
            {
                return configured;
            }

            return Math.Min(Program.CharacterConstructionTokenCeiling, Math.Max(configured + 1200, configured * 2));
        }

        internal static string BuildCharacterConstructionPromptForAttempt(
            string campaignId,
            string heroId,
            Dictionary<string, object> hero,
            Dictionary<string, object> traits,
            int attempt)
        {
            string prompt = BuildCharacterConstructionPrompt(campaignId, heroId, hero, traits);
            if (attempt <= 1)
            {
                return prompt;
            }

            return prompt + @"

CRITICAL RETRY COMPACTION:
The previous attempt did not complete a JSON object within its output budget.
- Return the complete JSON object in no more than 12,000 characters total.
- Preserve the required 1,200-1,600 character basePersonalitySummary.
- Use exactly one concise sentence for every other scalar string.
- Use at most two short entries in every array.
- Do not repeat trait definitions, source data, instructions, or schema descriptions.
- Close every object and array. A complete compact object is more important than decorative detail.";
        }

        internal static bool IsCharacterConstructionReady(Dictionary<string, object> construction)
        {
            return ReadBool(construction, "ok", false)
                && IsUsableCharacterStatus(ReadString(construction, "status", ""));
        }

        internal static bool IsUsableCharacterStatus(string status)
        {
            return string.Equals(status, "canonical_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "baseline_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "enriched", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "fallback_ready", StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> CharacterConstructionFailureResponse(string kind, string campaignId, string heroId, string heroName, Dictionary<string, object> construction, Dictionary<string, object> timing, Dictionary<string, object> extra)
        {
            string diagnostic = ReadString(construction, "diagnostic", ReadString(construction, "error", ""));
            if (string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostic = "Character generation has failed for " + FirstNonEmpty(heroName, heroId) + ". No dialogue response was generated. Check the character_construction entries in the LLM log.";
            }

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["stage"] = "character_construction",
                ["kind"] = kind,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["heroName"] = FirstNonEmpty(heroName, heroId),
                ["error"] = diagnostic,
                ["diagnostic"] = diagnostic,
                ["construction"] = construction ?? new Dictionary<string, object>(),
                ["timing"] = timing ?? new Dictionary<string, object>()
            };

            if (extra != null)
            {
                foreach (KeyValuePair<string, object> pair in extra)
                {
                    response[pair.Key] = pair.Value;
                }
            }

            return response;
        }

        private static string BuildCharacterConstructionDiagnostic(string heroId, Dictionary<string, object> hero, List<Dictionary<string, object>> attempts, Dictionary<string, object> lastLlm)
        {
            string heroName = ReadString(hero, "name", heroId);
            string lastError = LastConstructionError(attempts, lastLlm);
            string likelyIssue = LikelyCharacterConstructionIssue(lastError, attempts);
            string errorText = string.IsNullOrWhiteSpace(lastError) ? "No provider error text was returned." : " Last error: " + lastError;
            return "Character generation has failed for " + FirstNonEmpty(heroName, heroId) + " after two attempts. Likely issue: " + likelyIssue + errorText + " No dialogue or event response was generated.";
        }

        private static string LastConstructionError(List<Dictionary<string, object>> attempts, Dictionary<string, object> lastLlm)
        {
            if (attempts != null)
            {
                for (int index = attempts.Count - 1; index >= 0; index--)
                {
                    string error = ReadString(attempts[index], "error", "");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return error;
                    }
                }
            }

            return ReadString(lastLlm, "error", "");
        }

        internal static string LikelyCharacterConstructionIssue(string lastError, List<Dictionary<string, object>> attempts)
        {
            string lowered = (lastError ?? string.Empty).ToLowerInvariant();
            bool parseFailure = attempts != null && attempts.Any(x => x.ContainsKey("parsed") && !ReadBool(x, "parsed", true));
            bool lengthFinish = attempts != null && attempts.Any(x => string.Equals(ReadString(x, "finishReason", ""), "length", StringComparison.OrdinalIgnoreCase));

            if (parseFailure && (lengthFinish || lowered.Contains("max token") || lowered.Contains("token limit")))
            {
                return "the model response was cut off by the character construction token limit before valid JSON completed.";
            }

            if (lowered.Contains("api url is empty") || lowered.Contains("invalid uri") || lowered.Contains("uri format"))
            {
                return "the LLM API URL is missing or malformed in the server settings.";
            }

            if (lowered.Contains("unauthorized") || lowered.Contains("401") || lowered.Contains("api key") || lowered.Contains("invalid key"))
            {
                return "the API key is missing, expired, or rejected by the provider.";
            }

            if (lowered.Contains("forbidden") || lowered.Contains("403"))
            {
                return "the provider rejected this key, endpoint, or model permission.";
            }

            if (lowered.Contains("not found") || lowered.Contains("404") || lowered.Contains("model"))
            {
                return "the configured character construction model or endpoint may be wrong.";
            }

            if (lowered.Contains("rate") || lowered.Contains("quota") || lowered.Contains("429") || lowered.Contains("limit"))
            {
                return "the provider is rate-limiting requests or the account quota is exhausted.";
            }

            if (lowered.Contains("timeout") || lowered.Contains("timed out") || lowered.Contains("task canceled"))
            {
                return "the provider call timed out before character construction finished.";
            }

            if (lowered.Contains("connection") || lowered.Contains("refused") || lowered.Contains("dns") || lowered.Contains("name resolution") || lowered.Contains("ssl") || lowered.Contains("certificate"))
            {
                return "the server could not reach the configured LLM endpoint.";
            }

            if (parseFailure || lowered.Contains("json") || lowered.Contains("parse"))
            {
                return "the model answered, but did not return the strict JSON object required for character construction.";
            }

            return "the character construction LLM call failed. Check the server LLM log for requestType character_construction, plus the API URL, key, and model.";
        }

        private static string CharacterConstructionFailureKind(List<Dictionary<string, object>> attempts, Dictionary<string, object> lastLlm)
        {
            string error = LastConstructionError(attempts, lastLlm).ToLowerInvariant();
            if (error.Contains("api url is empty") || error.Contains("invalid uri") || error.Contains("uri format")
                || error.Contains("unauthorized") || error.Contains("401") || error.Contains("api key")
                || error.Contains("invalid key") || error.Contains("missing_api_key") || error.Contains("forbidden")
                || error.Contains("403") || error.Contains("not found") || error.Contains("404") || error.Contains("model"))
            {
                return "configuration";
            }

            if (error.Contains("rate") || error.Contains("quota") || error.Contains("429"))
            {
                return "provider_limit";
            }

            if (error.Contains("timeout") || error.Contains("timed out") || error.Contains("task canceled")
                || error.Contains("connection") || error.Contains("refused") || error.Contains("dns")
                || error.Contains("name resolution") || error.Contains("ssl") || error.Contains("certificate"))
            {
                return "connection";
            }

            bool parseFailure = attempts != null && attempts.Any(x => x.ContainsKey("parsed") && !ReadBool(x, "parsed", true));
            return parseFailure ? "parse" : "unknown";
        }

        internal static bool ShouldRetryFailedConstruction(Dictionary<string, object> failedStatus, Dictionary<string, object> settings)
        {
            if (failedStatus == null || !string.Equals(ReadString(failedStatus, "status", ""), "failed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string failureKind = ReadString(failedStatus, "failureKind", "");
            if (string.IsNullOrWhiteSpace(failureKind))
            {
                failureKind = CharacterFailureKindFromText(ReadString(failedStatus, "error", ReadString(failedStatus, "diagnostic", "")));
            }
            bool retryableAfterSettingsChange =
                string.Equals(failureKind, "configuration", StringComparison.OrdinalIgnoreCase)
                || string.Equals(failureKind, "connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(failureKind, "parse", StringComparison.OrdinalIgnoreCase)
                || string.Equals(failureKind, "output_limit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(failureKind, "provider_limit", StringComparison.OrdinalIgnoreCase);
            if (!retryableAfterSettingsChange)
            {
                return false;
            }

            if (string.Equals(failureKind, "parse", StringComparison.OrdinalIgnoreCase)
                && ReadInt(failedStatus, "constructionEngineVersion", 0) < Program.CharacterConstructionEngineVersion)
            {
                return true;
            }

            string failedRevision = ReadString(failedStatus, "settingsRevision", "");
            string currentRevision = ReadString(settings, "settingsRevision", "");
            string failedFingerprint = ReadString(failedStatus, "settingsFingerprint", "");
            string currentFingerprint = SettingsService.SettingsFingerprint(settings);
            bool settingsChanged = (!string.IsNullOrWhiteSpace(currentRevision) && !string.Equals(failedRevision, currentRevision, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(currentFingerprint) && !string.Equals(failedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase));
            if (settingsChanged)
            {
                return true;
            }

            if (DateTime.TryParse(ReadString(failedStatus, "failedUtc", ""), out DateTime failedUtc) && File.Exists(Program.SettingsPath))
            {
                return File.GetLastWriteTimeUtc(Program.SettingsPath) > failedUtc.ToUniversalTime();
            }

            return false;
        }

        internal static string CharacterFailureKindFromText(string text)
        {
            string lowered = (text ?? string.Empty).ToLowerInvariant();
            if (lowered.Contains("api key") || lowered.Contains("missing_api_key") || lowered.Contains("invalid authentication")
                || lowered.Contains("unauthorized") || lowered.Contains("401") || lowered.Contains("forbidden")
                || lowered.Contains("403") || lowered.Contains("api url") || lowered.Contains("model") || lowered.Contains("404"))
            {
                return "configuration";
            }

            if (lowered.Contains("timeout") || lowered.Contains("connection") || lowered.Contains("dns") || lowered.Contains("ssl"))
            {
                return "connection";
            }

            if (lowered.Contains("json") || lowered.Contains("parse") || lowered.Contains("token limit") || lowered.Contains("max token"))
            {
                return "parse";
            }

            return "unknown";
        }
    }
}
