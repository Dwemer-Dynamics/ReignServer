using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        // POST /arrests/test/contracts (registered in Program.WorldSimulationRoutes.cs).
        internal static Dictionary<string, object> ArrestContractReportApi()
        {
            List<Dictionary<string, object>> results = RunArrestSystemSelfTests();
            return new Dictionary<string, object>
            {
                ["ok"] = results.All(item => ReadBool(item, "passed", false)),
                ["schema"] = "reign-arrest-contract-report-v2",
                ["caseCount"] = results.Count,
                ["languageCaseCount"] = results.Count(item =>
                    ReadString(item, "name", "").StartsWith("arrest_language_",
                        StringComparison.OrdinalIgnoreCase)),
                ["results"] = results
            };
        }
#endif

        internal static Dictionary<string, object> ArrestEvidenceEvaluateApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string timelineId = ReadString(payload, "timelineId", "");
            string chargeCategory = ReadString(payload, "chargeCategory", "");
            string accusedId = ReadString(payload, "accusedHeroId", "").Trim();
            if (accusedId.Length == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "accusedHeroId is required."
                };
            List<string> evidenceIds = new List<string>();
            // A documented exposure establishes espionage. World History participates in
            // other game systems, but its generic roles and flags do not establish a charge.
            if (chargeCategory == "espionage" && !string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(timelineId))
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaignId);
                if (TableExists(connection, "foreign_agent_assignments"))
                {
                    evidenceIds = WorldSimulationStore.SelectExposedForeignAgentArrestEvidence(connection,
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["accused"] = accusedId
                        }).Select(row => ArrestExposedAgentEvidenceId(row, accusedId))
                        .Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["accusedHeroId"] = accusedId,
                ["causeEstablished"] = evidenceIds.Count > 0,
                ["evidenceStatus"] = evidenceIds.Count > 0 ? "established" : "unsupported",
                ["evidenceIds"] = evidenceIds,
                ["hiddenAgentInformationRevealed"] = false
            };
        }

        internal static Dictionary<string, object> ArrestCaseApplyApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string operation = ReadString(payload, "operation", "apply")
                .Trim().ToLowerInvariant();
            string caseId = ReadString(payload, "caseId", "").Trim();
            string accusedId = ReadString(payload, "accusedHeroId", "").Trim();
            string playerId = ReadString(payload, "playerHeroId", "").Trim();
            string tagId = ReadString(payload, "reputationTagId", "").Trim();
            if (caseId.Length == 0 || accusedId.Length == 0 || playerId.Length == 0
                || tagId.Length == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "caseId, accusedHeroId, playerHeroId, and reputationTagId are required."
                };
            if (operation != "apply" && operation != "rescind")
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "operation must be apply or rescind."
                };

            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int desiredDelta = Clamp(ReadInt(payload, "relationshipDelta", 0), -100, 100);
            int reputationValue = Clamp(ReadInt(payload, "reputationValue", 0), -100, 100);
            string description = DialogueService.LimitText(ReadString(payload, "description", ""), 1000);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                ReputationService.EnsureSocialReputationSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                WorldSimulationStore.Begin(connection);
                try
                {
                    Dictionary<string, object> existing = WorldSimulationStore.SelectCharacterReputations(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["subject"] = accusedId, ["tag"] = tagId
                        }).FirstOrDefault();
                    Dictionary<string, object> priorSnapshot = TryParseJsonObject(
                        ReadString(existing, "snapshot_json", "{}"));
                    int priorDelta = ReadInt(priorSnapshot,
                        "arrestAppliedRelationshipDelta", 0);
                    int appliedDelta = priorDelta;
                    if (operation == "apply")
                    {
                        int correction = ArrestRelationshipCorrection(operation,
                            desiredDelta, priorDelta);
                        if (correction != 0)
                        {
                            string pairKey = RelationshipsService.AmbientPairKey(accusedId, playerId);
                            string heroA = pairKey.StartsWith(accusedId + "|",
                                StringComparison.OrdinalIgnoreCase) ? accusedId : playerId;
                            string heroB = string.Equals(heroA, accusedId,
                                StringComparison.OrdinalIgnoreCase) ? playerId : accusedId;
                            RelationshipsService.EnsurePoliticalRelationshipPair(connection, campaignId,
                                timelineId, pairKey, heroA, heroB, worldDay);
                            Dictionary<string, object> pair =
                                RelationshipsService.ApplyAtomicDirectionalRelationshipDelta(connection,
                                    campaignId, accusedId, playerId, correction,
                                    worldDay, "arrest_case:" + caseId, timelineId);
                            if (pair == null)
                                throw new InvalidOperationException(
                                    "The accused-to-player directional relationship could not be updated.");
                            appliedDelta = desiredDelta;
                        }

                        Dictionary<string, object> snapshot = new Dictionary<string, object>
                        {
                            ["label"] = "Arrest accusation",
                            ["description"] = description,
                            ["arrestCaseId"] = caseId,
                            ["accuserHeroId"] = playerId,
                            ["accusedHeroId"] = accusedId,
                            ["chargeCategory"] = ReadString(payload, "chargeCategory", "other"),
                            ["severity"] = ReadString(payload, "severity", "serious"),
                            ["causeEstablished"] = ReadBool(payload, "causeEstablished", false),
                            ["evidenceStatus"] = ReadString(payload, "evidenceStatus", "unsupported"),
                            ["evidenceIds"] = ReadString(payload, "evidenceIds", ""),
                            ["arrestAppliedRelationshipDelta"] = appliedDelta,
                            ["playerOriginated"] = true
                        };
                        WorldSimulationStore.UpsertCharacterReputations(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = accusedId, ["tag"] = tagId,
                                ["archetype"] = "arrest_accusation",
                                ["description"] = description, ["value"] = reputationValue,
                                ["day"] = worldDay, ["snapshot"] = Json.Serialize(snapshot),
                                ["ts"] = ts
                            });
                    }
                    else
                    {
                        int correction = ArrestRelationshipCorrection(operation,
                            desiredDelta, priorDelta);
                        if (correction != 0)
                        {
                            string pairKey = RelationshipsService.AmbientPairKey(accusedId, playerId);
                            string heroA = pairKey.StartsWith(accusedId + "|",
                                StringComparison.OrdinalIgnoreCase) ? accusedId : playerId;
                            string heroB = string.Equals(heroA, accusedId,
                                StringComparison.OrdinalIgnoreCase) ? playerId : accusedId;
                            RelationshipsService.EnsurePoliticalRelationshipPair(connection, campaignId,
                                timelineId, pairKey, heroA, heroB, worldDay);
                            Dictionary<string, object> pair =
                                RelationshipsService.ApplyAtomicDirectionalRelationshipDelta(connection,
                                    campaignId, accusedId, playerId, correction,
                                    worldDay, "arrest_rescinded:" + caseId, timelineId);
                            if (pair == null)
                                throw new InvalidOperationException(
                                    "The rescinded arrest relationship effect could not be removed.");
                            appliedDelta = 0;
                        }
                        WorldSimulationStore.UpdateCharacterReputations(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = accusedId, ["tag"] = tagId, ["ts"] = ts
                            });
                    }
                    WorldSimulationStore.Commit(connection);
                    RelationshipsService.InvalidateRelationshipPairStateCache(campaignId);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["operation"] = operation,
                        ["caseId"] = caseId, ["reputationTagId"] = tagId,
                        ["appliedRelationshipDelta"] = appliedDelta,
                        ["idempotent"] = operation == "apply" && priorDelta == desiredDelta
                    };
                }
                catch (Exception ex)
                {
                    try { WorldSimulationStore.Rollback(connection); } catch { }
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["caseId"] = caseId, ["error"] = ex.Message
                    };
                }
            }
        }

        internal static List<Dictionary<string, object>> BuildArrestDialogueCandidates(
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Dictionary<string, object> context = ReadDictionary(payload, "arrestContext")
                ?? new Dictionary<string, object>();
            string normalized = ActionPipelineService.NormalizeLookup(playerText);
            if (string.IsNullOrWhiteSpace(normalized) || ArrestTextIsNonDirective(normalized))
                return results;
            string playerId = ReadFirstString(payload, "playerHeroStringId", "playerId");
            string speakerId = FirstNonEmpty(
                ReadFirstString(payload, "heroStringId", "speakerHeroStringId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(speakerId))
                return results;
            if (ArrestNamesAbsentThirdParty(payload, hero, normalized, speakerId))
                return results;

            string caseId = ReadString(context, "activeCaseId", "");
            string phase = ReadString(context, "phase", "").ToLowerInvariant();
            bool available = ReadBool(context, "available", false);
            bool held = ReadBool(context, "heldByPlayer", false);

            if ((held || !string.IsNullOrWhiteSpace(caseId)) && LooksLikeRescission(normalized))
            {
                results.Add(ArrestCandidate("rescind_arrest_accusation", playerId,
                    speakerId, caseId, "The player rescinded the accusation and cleared the accused's name."));
                return results;
            }
            if (held && LooksLikeRelease(normalized))
            {
                results.Add(ArrestCandidate("release_arrested_character", playerId,
                    speakerId, caseId, "The player ordered the accused released while retaining the record."));
                return results;
            }
            if (phase == "refused" && LooksLikePlayerPartyAttack(normalized))
            {
                results.Add(ArrestCandidate("player_attack_party", playerId,
                    speakerId, caseId, "The player ordered their party to attack after arrest was refused."));
                return results;
            }
            if (phase == "refused" && LooksLikeDuel(normalized)
                && ArrestReplyAccepts(visibleReply))
            {
                Dictionary<string, object> duel = new Dictionary<string, object>
                {
                    ["command"] = "duel_player", ["actorHeroId"] = speakerId,
                    ["TargetHero"] = playerId,
                    ["reason"] = "A nonlethal duel will decide the refused arrest.",
                    ["source"] = "arrest_dialogue", ["requiresAcceptance"] = false,
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["duelMode"] = "training", ["lethal"] = false,
                        ["duelPurpose"] = "arrest_capture", ["caseId"] = caseId
                    }
                };
                results.Add(duel);
                return results;
            }
            if ((phase == "awaitingconfirmation" || phase == "awaiting_confirmation"
                || phase == "refused") && LooksLikeArrestConfirmation(normalized))
            {
                results.Add(ArrestCandidate("confirm_arrest", playerId, speakerId,
                    caseId, "The player confirmed the prepared arrest."));
                return results;
            }
            if (!available || !LooksLikeArrestOrder(normalized)) return results;

            string accusation = ExtractArrestReason(playerText);
            if (string.IsNullOrWhiteSpace(accusation)) return results;
            string severity = ClassifyArrestSeverity(accusation);
            string category = ClassifyArrestCharge(accusation);
            bool partyEncounter = string.Equals(ReadString(context, "contextKind", ""),
                "party_encounter", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> prepare = ArrestCandidate("prepare_arrest",
                playerId, speakerId, string.Empty, accusation);
            prepare["reason"] = accusation;
            prepare["terms"] = new Dictionary<string, object>
            {
                ["accusation"] = accusation,
                ["severity"] = severity,
                ["chargeCategory"] = category,
                ["surrenderAccepted"] = !partyEncounter || ArrestReplyAccepts(visibleReply),
                ["reactionSnapshot"] = DialogueService.LimitText(visibleReply ?? string.Empty, 1000)
            };
            results.Add(prepare);
            return results;
        }

        private static Dictionary<string, object> ArrestCandidate(string command,
            string playerId, string speakerId, string caseId, string reason)
        {
            Dictionary<string, object> terms = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(caseId)) terms["caseId"] = caseId;
            return new Dictionary<string, object>
            {
                ["command"] = command,
                ["actorHeroId"] = playerId,
                ["TargetHero"] = speakerId,
                ["reason"] = reason ?? "Conversation arrest action.",
                ["source"] = "arrest_dialogue",
                ["requiresAcceptance"] = false,
                ["terms"] = terms
            };
        }

        internal static string BuildArrestDialoguePromptBlock(
            Dictionary<string, object> turnPayload)
        {
            Dictionary<string, object> context = ReadDictionary(turnPayload, "arrestContext");
            if (context == null || !ReadBool(context, "available", false)
                && string.IsNullOrWhiteSpace(ReadString(context, "activeCaseId", "")))
                return string.Empty;
            return @"CONVERSATION ARREST STATE (private live-game authority)
" + Json.Serialize(context) + @"
- Names and gendered or neutral pronouns in an arrest order resolve only to the current NPC speaker. Do not accept an order to arrest an absent third party.
- If the player orders this NPC arrested but gives no reason or charge, ask what the charge is. Do not act as if an arrest case exists yet.
- On a first reasoned arrest order, show only this NPC's immediate spoken/visible reaction as guards move into position. Do not narrate the player, guards completing custody, or a dungeon transfer.
- Custody begins only after a later explicit player command to take the NPC away. Treat the supplied phase as authoritative and never skip that second turn.
- During a field-party encounter the NPC may surrender or refuse according to personality, strength, relationship, and circumstances. Refusal may be followed by a nonlethal duel or native party attack.
- Never reveal hidden spy status or secret evidence. Only causeEstablished/evidenceStatus supplied here may be treated as disclosed proof.
- A release preserves the accusation; rescinding/clearing the accusation also releases the NPC when this case is the custody basis.";
        }

        private static bool ArrestTextIsNonDirective(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "do not arrest", "dont arrest", "do not take him", "do not take her",
                "not arrest", "if i arrest", "if we arrest", "would arrest",
                "would you arrest", "could arrest", "should i arrest", "should i put",
                "should we put", "gave no order", "give no order", "no arrest order",
                "talk about arrest", "rumor of arrest");
        }

        private static bool LooksLikeArrestOrder(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "arrest", "guards take", "guard take", "take him to the dungeon",
                "take her to the dungeon", "take them to the dungeon",
                "take him away", "take her away", "take them away", "take you away",
                "take him to prison", "take her to prison", "take them to prison",
                "seize him", "seize her", "seize them", "seize this lord", "seize this lady",
                "place him in custody",
                "place her in custody", "place them in custody", "have him detained",
                "have her detained", "have them detained", "lock him up", "lock her up",
                "lock them up", "take you into custody", "taking you into custody", "detain him",
                "detain her", "detain them", "placed in custody")
                || (normalized.Contains("lock ") && normalized.Contains(" up"))
                || (ActionPipelineService.ContainsAnyNormalized(normalized, "dungeon", "prison", "custody", "detained")
                    && ActionPipelineService.ContainsAnyNormalized(normalized, "guards", "guard", "take", "send", "lock", "put", "place"));
        }

        private static bool ArrestNamesAbsentThirdParty(
            Dictionary<string, object> payload,
            Dictionary<string, object> speaker,
            string normalizedText,
            string speakerId)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex")
                ?? new Dictionary<string, object>();
            string playerId = ReadFirstString(payload, "playerHeroStringId", "playerId");
            List<Dictionary<string, object>> heroes = ReadDictionaryList(index, "heroes");
            foreach (Dictionary<string, object> hero in heroes)
            {
                string id = ReadFirstString(hero, "heroStringId", "heroId", "id");
                if (string.IsNullOrWhiteSpace(id)
                    || string.Equals(id, speakerId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(id, playerId, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string name in new[] { ReadString(hero, "name", ""), id })
                {
                    string candidate = ActionPipelineService.NormalizeLookup(name);
                    if (candidate.Length >= 3 && ActionPipelineService.NormalizedTextContainsPhrase(normalizedText, candidate))
                        return true;
                }
            }
            return false;
        }

        private static bool LooksLikeArrestConfirmation(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "take him away", "take her away", "take them away", "take you away",
                "guards take him", "guards take her", "guards take them", "guards take you",
                "to the dungeon", "to prison", "do it guards", "proceed with the arrest",
                "carry out the arrest", "the arrest stands", "take the prisoner away");
        }

        private static bool LooksLikeRescission(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "rescind the accusation", "withdraw the accusation", "clear his name",
                "clear her name", "clear their name", "clear your name", "charges are dropped",
                "drop the charges", "pardon and clear", "accusation was false", "exonerate");
        }

        private static bool LooksLikeRelease(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "release him", "release her", "release them", "release you",
                "free him", "free her", "free them", "free you", "let him out",
                "let her out", "let them out", "leave the dungeon");
        }

        private static bool LooksLikeDuel(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "nonlethal duel", "non lethal duel", "duel for your surrender",
                "duel for the arrest", "fight me for your freedom", "settle this by duel");
        }

        private static bool LooksLikePlayerPartyAttack(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "order my men to attack", "order our men to attack", "my men attack",
                "our men attack", "troops attack", "party attack", "attack their party",
                "attack his party", "attack her party", "men seize them by force");
        }

        private static bool ArrestReplyAccepts(string reply)
        {
            string normalized = ActionPipelineService.NormalizeLookup(reply);
            if (ActionPipelineService.ContainsAnyNormalized(normalized, "refuse", "will not", "wont", "never surrender",
                "fight you", "resist", "defy", "not surrender")) return false;
            return ActionPipelineService.ContainsAnyNormalized(normalized, "surrender", "submit", "yield", "i accept",
                "very well", "i will go", "take me", "i comply", "agreed", "duel", "accept your challenge");
        }

        private static string ExtractArrestReason(string playerText)
        {
            string text = (playerText ?? string.Empty).Trim();
            if (text.Length == 0) return string.Empty;
            string lower = text.ToLowerInvariant();
            foreach (string marker in new[]
            {
                " because ", " for the crime of ", " for crimes of ", " for ",
                " on the charge of ", " on charges of ", " accused of ", " accusation of "
            })
            {
                int index = lower.LastIndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    string reason = text.Substring(index + marker.Length).Trim(' ', '.', ',', ';', ':', '!', '?');
                    if (reason.Length >= 3) return DialogueService.LimitText(reason, 500);
                }
            }
            foreach (string charge in new[]
            {
                "treason", "espionage", "spying", "spy", "murder", "assassination",
                "sabotage", "rebellion", "corruption", "fraud", "theft", "assault",
                "conspiracy", "conspiring", "stole", "disorder", "trespass", "insult"
            })
            {
                int index = lower.IndexOf(charge, StringComparison.Ordinal);
                if (index >= 0)
                    return DialogueService.LimitText(text.Substring(index).Trim(' ', '.', ',', ';', ':', '!', '?'), 500);
            }
            return string.Empty;
        }

        private static string ClassifyArrestSeverity(string accusation)
        {
            string value = ActionPipelineService.NormalizeLookup(accusation);
            if (ActionPipelineService.ContainsAnyNormalized(value, "treason", "regicide", "betray the realm", "wartime betrayal"))
                return "capital";
            if (ActionPipelineService.ContainsAnyNormalized(value, "murder", "assassination", "espionage", "spying",
                "foreign spy", "sabotage", "rebellion")) return "grave";
            if (ActionPipelineService.ContainsAnyNormalized(value, "theft", "fraud", "corruption", "assault",
                "stole", "conspiracy", "conspiring", "bribery", "kidnapping")) return "serious";
            return "minor";
        }

        private static string ClassifyArrestCharge(string accusation)
        {
            string value = ActionPipelineService.NormalizeLookup(accusation);
            if (ActionPipelineService.ContainsAnyNormalized(value, "treason", "regicide")) return "treason";
            if (ActionPipelineService.ContainsAnyNormalized(value, "espionage", "spying", "foreign spy", "spy")) return "espionage";
            if (ActionPipelineService.ContainsAnyNormalized(value, "murder", "assassination")) return "murder";
            if (ActionPipelineService.ContainsAnyNormalized(value, "sabotage")) return "sabotage";
            if (ActionPipelineService.ContainsAnyNormalized(value, "rebellion")) return "rebellion";
            if (ActionPipelineService.ContainsAnyNormalized(value, "theft", "stole", "fraud", "corruption", "bribery")) return "property_corruption";
            if (ActionPipelineService.ContainsAnyNormalized(value, "assault", "kidnapping")) return "violence";
            if (ActionPipelineService.ContainsAnyNormalized(value, "conspiracy", "conspiring")) return "conspiracy";
            return "other";
        }

        private static string ArrestExposedAgentEvidenceId(Dictionary<string, object> assignment, string accusedId)
        {
            if (ReadString(assignment, "agent_hero_id", "") != accusedId
                || ReadString(assignment, "status", "") != "exposed")
                return null;
            return "foreign_agent:" + accusedId + ":" + ReadString(assignment, "sponsor_kingdom_id", "unknown");
        }

        private static int ArrestRelationshipCorrection(string operation,
            int desiredDelta, int priorDelta)
        {
            return string.Equals(operation, "rescind", StringComparison.OrdinalIgnoreCase)
                ? -priorDelta : desiredDelta - priorDelta;
        }

        private static List<Dictionary<string, object>> RunArrestSystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => results.Add(
                new Dictionary<string, object>
                {
                    ["name"] = "arrest_" + name, ["passed"] = passed, ["detail"] = detail
                });
            Dictionary<string, object> context = new Dictionary<string, object>
            {
                ["available"] = true, ["contextKind"] = "player_settlement",
                ["activeCaseId"] = "", ["phase"] = "", ["heldByPlayer"] = false
            };
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["playerHeroStringId"] = "player", ["heroStringId"] = "npc",
                ["arrestContext"] = context
            };
            Dictionary<string, object> hero = new Dictionary<string, object>
            {
                ["heroStringId"] = "npc", ["name"] = "Raganvad"
            };
            payload["actionResolutionIndex"] = new Dictionary<string, object>
            {
                ["heroes"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "npc", ["name"] = "Raganvad"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "third_party", ["name"] = "Caladog"
                    }
                }
            };
            Action<string, string, string, string, bool> language =
                (id, phase, playerText, expectedCommand, heldByPlayer) =>
                {
                    context["activeCaseId"] = string.IsNullOrWhiteSpace(phase)
                        ? string.Empty : "case_1";
                    context["phase"] = phase ?? string.Empty;
                    context["heldByPlayer"] = heldByPlayer;
                    string visibleReply = id == "AR-LANG-027"
                        ? "Very well, I accept your challenge."
                        : id == "AR-LANG-028"
                            ? "I refuse and will not accept your challenge."
                            : "You will regret this.";
                    List<Dictionary<string, object>> candidates =
                        BuildArrestDialogueCandidates(payload, hero, playerText, visibleReply);
                    string actual = ReadString(candidates.FirstOrDefault(), "command", "");
                    bool passed = string.IsNullOrWhiteSpace(expectedCommand)
                        ? candidates.Count == 0
                        : candidates.Count == 1
                            && string.Equals(actual, expectedCommand,
                                StringComparison.OrdinalIgnoreCase);
                    add("language_" + id, passed, playerText + " expected="
                        + expectedCommand + " actual=" + actual);
                };

            language("AR-LANG-001", "", "Guards, arrest her for treason.", "prepare_arrest", false);
            language("AR-LANG-002", "", "Captain, please place him in custody for corruption.", "prepare_arrest", false);
            language("AR-LANG-003", "", "Have the guards take him to the dungeon because he is a foreign spy.", "prepare_arrest", false);
            language("AR-LANG-004", "", "Put her in prison on charges of sabotage.", "prepare_arrest", false);
            language("AR-LANG-005", "", "Take them away for conspiring against the crown.", "prepare_arrest", false);
            language("AR-LANG-006", "", "Seize this lord for rebellion against the realm.", "prepare_arrest", false);
            language("AR-LANG-007", "", "Lock Raganvad up for fraud against the treasury.", "prepare_arrest", false);
            language("AR-LANG-008", "", "I am taking you into custody for murder.", "prepare_arrest", false);
            language("AR-LANG-009", "", "I want the guard to detain her on the charge of bribery.", "prepare_arrest", false);
            language("AR-LANG-010", "", "Lock him up; he stole from the guild.", "prepare_arrest", false);
            language("AR-LANG-011", "awaiting_confirmation", "Guards, take her away.", "confirm_arrest", false);
            language("AR-LANG-012", "awaiting_confirmation", "Take him to the dungeon now.", "confirm_arrest", false);
            language("AR-LANG-013", "awaiting_confirmation", "Proceed with the arrest and put them in prison.", "confirm_arrest", false);
            language("AR-LANG-014", "awaiting_confirmation", "The arrest stands; carry it out.", "confirm_arrest", false);
            language("AR-LANG-015", "awaiting_confirmation", "Do it, guards.", "confirm_arrest", false);
            language("AR-LANG-016", "", "Do not arrest her for treason.", "", false);
            language("AR-LANG-017", "", "Don't take him to prison.", "", false);
            language("AR-LANG-018", "", "If I arrest him, what happens?", "", false);
            language("AR-LANG-019", "", "Would you arrest someone for treason?", "", false);
            language("AR-LANG-020", "", "Should I put her in the dungeon?", "", false);
            language("AR-LANG-021", "", "He said, 'arrest her for spying,' but I gave no order.", "", false);
            language("AR-LANG-022", "", "Guards, arrest her.", "", false);
            language("AR-LANG-023", "", "Guards, arrest Caladog for treason.", "", false);
            language("AR-LANG-024", "awaiting_confirmation", "No, cancel that arrest order.", "", false);
            language("AR-LANG-025", "", "Not her—arrest Caladog for treason instead.", "", false);
            language("AR-LANG-026", "refused", "I order my men to attack his party and take him alive.", "player_attack_party", false);
            language("AR-LANG-027", "refused", "Settle this by a nonlethal duel for your surrender.", "duel_player", false);
            language("AR-LANG-028", "refused", "Settle this by a nonlethal duel for your surrender.", "", false);
            language("AR-LANG-029", "captured", "Release her from the dungeon.", "release_arrested_character", true);
            language("AR-LANG-030", "captured", "Let him out, but leave the accusation on record.", "release_arrested_character", true);
            language("AR-LANG-031", "captured", "Free them from custody.", "release_arrested_character", true);
            language("AR-LANG-032", "captured", "I rescind the accusation and clear her name.", "rescind_arrest_accusation", true);
            language("AR-LANG-033", "captured", "Drop the charges and release him.", "rescind_arrest_accusation", true);
            language("AR-LANG-034", "captured", "The accusation was false; exonerate them.", "rescind_arrest_accusation", true);
            language("AR-LANG-035", "", "Have them detained for assault.", "prepare_arrest", false);
            language("AR-LANG-036", "", "The lady should be placed in custody for conspiracy; see it done.", "prepare_arrest", false);

            add("severity_table", ClassifyArrestSeverity("treason") == "capital"
                && ClassifyArrestSeverity("foreign espionage") == "grave"
                && ClassifyArrestSeverity("corruption") == "serious"
                && ClassifyArrestSeverity("trespass") == "minor",
                "All four charge tiers are deterministic.");
            add("relationship_first_apply", ArrestRelationshipCorrection("apply", -30, 0) == -30,
                "The first policy application applies the complete selected cell.");
            add("relationship_retry_idempotent", ArrestRelationshipCorrection("apply", -30, -30) == 0,
                "Repeating one case does not duplicate its relationship effect.");
            add("relationship_reclassification", ArrestRelationshipCorrection("apply", 0, -30) == 30,
                "Later evidence changes only the difference between policy cells.");
            add("relationship_rescission", ArrestRelationshipCorrection("rescind", 0, -30) == 30,
                "Rescission removes the arrest case's prior relationship effect.");
            var exposure = new Dictionary<string, object>
            {
                ["agent_hero_id"] = "npc", ["sponsor_kingdom_id"] = "enemy", ["status"] = "exposed"
            };
            add("evidence_exact_exposed_agent_accepted",
                ArrestExposedAgentEvidenceId(exposure, "npc") == "foreign_agent:npc:enemy",
                "A retained exposure for the exact agent supplies a public espionage evidence ID.");
            add("evidence_wrong_actor_or_unexposed_rejected",
                ArrestExposedAgentEvidenceId(exposure, "victim") == null
                && ArrestExposedAgentEvidenceId(exposure, "NPC") == null
                && ArrestExposedAgentEvidenceId(new Dictionary<string, object>(exposure) { ["status"] = "active" }, "npc") == null,
                "Another hero, a case alias or an active hidden assignment cannot establish cause.");
            var unsupportedEvidence = new Dictionary<string, object>
            {
                ["campaignId"] = "fixture", ["timelineId"] = "main", ["accusedHeroId"] = "npc",
                ["chargeCategory"] = "murder", ["officialFinding"] = true, ["witnessedActorHeroId"] = "npc"
            };
            add("evidence_wrong_or_legacy_charge_unsupported",
                !ReadBool(ArrestEvidenceEvaluateApi(unsupportedEvidence), "causeEstablished", true)
                && ReadString(ArrestEvidenceEvaluateApi(new Dictionary<string, object>(unsupportedEvidence)
                    { ["chargeCategory"] = "" }), "evidenceStatus", "") == "unsupported",
                "Generic history flags cannot supply a charge. Missing categories retain the unsupported result.");
            add("evidence_espionage_requires_explicit_scope",
                ReadString(ArrestEvidenceEvaluateApi(new Dictionary<string, object>(unsupportedEvidence)
                    { ["chargeCategory"] = "espionage", ["timelineId"] = "" }), "evidenceStatus", "") == "unsupported",
                "An espionage request without its timeline cannot read a fallback campaign scope.");
            return results;
        }
    }
}
