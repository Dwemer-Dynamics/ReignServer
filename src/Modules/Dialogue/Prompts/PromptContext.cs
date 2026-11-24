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
    internal static partial class DialogueService
    {
        internal static string BuildEventJsonForPrompt(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> compact = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[]
            {
                "eventId", "templateId", "displayName", "settlementId", "hostHeroStringId",
                "speakerHeroStringId", "phaseId", "phaseTitle", "phaseDescription", "eventType",
                "turnId", "isDancePhase", "canInvitePlayerToDance",
                "groupTurnResponses", "groupSpeakerIndex",
                "mustAccountForPriorSpeaker"
            })
            {
                if (payload.ContainsKey(key))
                {
                    compact[key] = payload[key];
                }
            }

            if (payload.ContainsKey("activeHeroIds"))
            {
                compact["activeHeroIds"] = payload["activeHeroIds"];
            }

            HashSet<string> activeHeroIds = new HashSet<string>(ReadStringList(payload, "activeHeroIds"), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> promptAttendees = ReadDictionaryList(payload, "attendees")
                .Select((attendee, index) => new { Attendee = attendee, Index = index })
                .OrderByDescending(x => activeHeroIds.Contains(CampaignFileService.CharacterIdFrom(x.Attendee)))
                .ThenBy(x => x.Index)
                .Select(x => x.Attendee)
                .Take(25)
                .ToList();
            List<Dictionary<string, object>> compactAttendees = new List<Dictionary<string, object>>();
            string currentSpeakerId = ReadFirstString(
                payload, "speakerHeroStringId", "heroStringId");
            Dictionary<string, Dictionary<string, object>>
                participantIdentityViews = ReadDictionaryList(
                        payload,
                        "observerParticipantIdentityViews")
                    .Where(row => !string.IsNullOrWhiteSpace(
                        ReadFirstString(
                            row,
                            "subjectHeroStringId",
                            "subjectId")))
                    .GroupBy(
                        row => ReadFirstString(
                            row,
                            "subjectHeroStringId",
                            "subjectId"),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => ReadDictionary(
                                group.First(),
                                "identityView")
                            ?? new Dictionary<string, object>(),
                        StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> attendee in promptAttendees)
            {
                string attendeeId = CampaignFileService.CharacterIdFrom(attendee);
                bool self = attendeeId.Equals(
                    currentSpeakerId,
                    StringComparison.OrdinalIgnoreCase);
                Dictionary<string, object> identityView =
                    participantIdentityViews.TryGetValue(
                        attendeeId,
                        out Dictionary<string, object> knownView)
                            ? knownView
                            : new Dictionary<string, object>();
                bool verified = self || ReadBool(
                    identityView,
                    "canonicalNameAllowed", false);
                Dictionary<string, object> compactAttendee =
                    new Dictionary<string, object>
                {
                    ["heroStringId"] = attendeeId,
                    ["observerIdentityState"] = self
                        ? "self"
                        : ReadString(
                            identityView,
                            "identityState",
                            "encountered_unknown"),
                    ["name"] = verified
                        ? ReadString(attendee, "name", "")
                        : FirstNonEmpty(
                            ReadString(
                                identityView,
                                "usableName", ""),
                            ReadString(
                                identityView,
                                "safeLabel", ""),
                            "an unidentified participant")
                };
                if (verified)
                {
                    compactAttendee["clanId"] =
                        ReadString(attendee, "clanId", "");
                    compactAttendee["clanName"] =
                        ReadString(attendee, "clanName", "");
                    compactAttendee["kingdomId"] =
                        ReadString(attendee, "kingdomId", "");
                    compactAttendee["kingdomName"] =
                        ReadString(attendee, "kingdomName", "");
                    compactAttendee["occupation"] =
                        ReadString(attendee, "occupation", "");
                    compactAttendee["isFemale"] =
                        ReadBool(attendee, "isFemale", false);
                    compactAttendee["isRuler"] =
                        ReadBool(attendee, "isRuler", false);
                    compactAttendee["relationToPlayer"] =
                        ReadInt(attendee, "relationToPlayer", 0);
                    compactAttendee["spouseId"] =
                        ReadString(attendee, "spouseId", "");
                    compactAttendee["spouseName"] =
                        ReadString(attendee, "spouseName", "");
                    compactAttendee["fatherId"] =
                        ReadString(attendee, "fatherId", "");
                    compactAttendee["fatherName"] =
                        ReadString(attendee, "fatherName", "");
                    compactAttendee["motherId"] =
                        ReadString(attendee, "motherId", "");
                    compactAttendee["motherName"] =
                        ReadString(attendee, "motherName", "");
                    compactAttendee["childrenIds"] =
                        ReadStringList(attendee, "childrenIds");
                    compactAttendee["childrenNames"] =
                        ReadStringList(attendee, "childrenNames");
                    compactAttendee["sovereignHeroStringId"] =
                        ReadString(attendee, "sovereignHeroStringId", "");
                    compactAttendee["sovereignName"] =
                        ReadString(attendee, "sovereignName", "");
                    compactAttendee["recognizedRoles"] =
                        ReadStringList(
                            ReadDictionary(
                                identityView,
                                "authorityView")
                            ?? new Dictionary<string, object>(),
                            "recognizedRoles");
                }
                compactAttendees.Add(compactAttendee);
            }
            compact["attendees"] = compactAttendees;
            compact["resolverNote"] = "The full action resolution index is intentionally server-side and is not part of the roleplay prompt.";

            string json = Json.Serialize(compact);
            if (json.Length <= 48000)
            {
                return json;
            }

            return Json.Serialize(new Dictionary<string, object>
            {
                ["eventId"] = ReadFirstString(payload, "eventId", "socialEventId"),
                ["templateId"] = ReadString(payload, "templateId", ""),
                ["displayName"] = ReadString(payload, "displayName", ""),
                ["phaseId"] = ReadString(payload, "phaseId", ""),
                ["phaseTitle"] = ReadString(payload, "phaseTitle", ""),
                ["turnId"] = ReadString(payload, "turnId", ""),
                ["attendeeCount"] = compactAttendees.Count,
                ["note"] = "Event metadata was reduced to remain within the prompt budget."
            });
        }

        internal static string LlmFailureForDisplay(Dictionary<string, object> llm)
        {
            if (ReadBool(llm, "approachOpeningFailed", false))
                return "The NPC's approach could not be verified. Please try again.";
            var codexFailure = ReadDictionary(llm, "codexFailure");
            if (ReadBool(codexFailure, "usageLimitExceeded", false))
                return "Codex reports that its usage limit was reached. Your message is saved; retry after the allowance resets.";
            if (ReadInt(codexFailure, "httpStatusCode", 0) == 429)
                return "Codex temporarily limited this request (429). Your message is saved; wait a moment, then select Retry.";
            string raw = ReadString(llm, "error", "");
            if (raw.IndexOf("context_length_exceeded", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("prompt exceeds", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "The AI request contained too much context. Bannerlord Reign stopped the response before any action was taken; check the server logs for the prompt-size breakdown.";
            }

            if (raw.IndexOf("missing_api_key", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("invalid authentication", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("invalid_api_key", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "API key missing or rejected. Open Server Diagnostics, save the key, then run Test LLM Key.";
            }

            return "The AI provider request failed. Open Server Diagnostics and the Audit Viewer for details.";
        }

        internal static string SocialEventTranscriptFile(string campaignId, string eventId)
        {
            return Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "events", "social", CampaignFileService.SafePathSegment(eventId, "unknown"), "transcript.jsonl");
        }

        internal static string PartyChatSessionTranscriptFile(string campaignId, Dictionary<string, object> payload)
        {
            string sessionId = ReadFirstString(payload, "conversationSessionId", "sessionId");
            return Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "events", "party_chat",
                CampaignFileService.SafePathSegment(sessionId, "unknown_session"), "transcript.jsonl");
        }

        internal static void AttachAcceptedTranscriptSourceIds(
            Dictionary<string, object> playerLine, Dictionary<string, object> npcLine,
            Dictionary<string, object> acceptedExchange)
        {
            // StoreGroupedConversationExchange returns the actual persisted player
            // and NPC turn keys in this order. Never derive a key from a shared
            // event beat, speaker name or matching words. Keep the JSONL row id.
            if (!ReadBool(acceptedExchange, "ok", false)
                || ReadString(acceptedExchange, "sessionId", "").Length == 0
                || ReadString(acceptedExchange, "exchangeId", "").Length == 0) return;
            var ids = ReadStringList(acceptedExchange, "turnIds");
            if (ids.Count != 2 || ids.Any(string.IsNullOrWhiteSpace)
                || string.Equals(ids[0], ids[1], StringComparison.Ordinal)) return;
            void Attach(Dictionary<string, object> line, string role, string id)
            {
                if (line == null || !string.Equals(ReadString(line, "role", ""), role, StringComparison.Ordinal)) return;
                string existing = ReadString(line, "sourceTurnId", "");
                if (existing.Length > 0 && !string.Equals(existing, id, StringComparison.Ordinal)) return;
                line["sourceTurnId"] = id;
                line["sessionId"] = ReadString(acceptedExchange, "sessionId", "");
                line["exchangeId"] = ReadString(acceptedExchange, "exchangeId", "");
            }
            Attach(playerLine, "player", ids[0]);
            Attach(npcLine, "npc", ids[1]);
        }

        internal static Dictionary<string, object> EventTranscriptLine(string role, string speaker, string text, Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["role"] = role ?? "",
                ["speaker"] = speaker ?? "",
                ["speakerHeroStringId"] = string.Equals(role, "player", StringComparison.OrdinalIgnoreCase)
                    ? ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId")
                    : string.Equals(role, "npc", StringComparison.OrdinalIgnoreCase)
                        ? ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                        : "",
                ["text"] = string.Equals(role, "npc", StringComparison.OrdinalIgnoreCase) ? ConversationResponseService.SanitizeVisibleReply(text ?? "") : (text ?? ""),
                ["eventId"] = ReadFirstString(payload, "eventId", "socialEventId"),
                ["templateId"] = ReadString(payload, "templateId", ""),
                ["phaseId"] = ReadString(payload, "phaseId", ""),
                ["phaseTitle"] = ReadString(payload, "phaseTitle", ""),
                ["turnId"] = ReadString(payload, "turnId", ""),
                ["sessionId"] = ReadFirstString(payload, "conversationSessionId", "sessionId"),
                ["exchangeId"] = ReadFirstString(payload, "sceneTurnId", "turnId", "exchangeId")
            };
        }

        internal static void AppendEventLineForAttendees(string campaignId, List<Dictionary<string, object>> attendees, Dictionary<string, object> row)
        {
            foreach (Dictionary<string, object> attendee in attendees ?? new List<Dictionary<string, object>>())
            {
                string heroId = CampaignFileService.CharacterIdFrom(attendee);
                if (!string.IsNullOrWhiteSpace(heroId))
                {
                    CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "events.jsonl"), row);
                }
            }
        }

        internal static string FormatEventLinesForPrompt(List<Dictionary<string, object>> eventLines)
        {
            if (eventLines == null || eventLines.Count == 0)
            {
                return "none";
            }

            StringBuilder builder = new StringBuilder();
            foreach (Dictionary<string, object> line in eventLines)
            {
                string speaker = ReadString(line, "speaker", "Event");
                string text = ReadString(line, "text", "");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string speakerId = ReadFirstString(line, "speakerHeroStringId", "speaker_id", "heroStringId");
                    string role = ReadString(line, "role", "");
                    string attribution = speaker;
                    if (!string.IsNullOrWhiteSpace(speakerId)) attribution += " [id=" + speakerId + "]";
                    if (!string.IsNullOrWhiteSpace(role)) attribution += " [role=" + role + "]";
                    builder.AppendLine("- " + attribution + ": " + text);
                }
            }

            return builder.Length == 0 ? "none" : builder.ToString().TrimEnd();
        }

        internal static List<Dictionary<string, object>> BuildPartyChatPromptTranscript(
            Dictionary<string, object> payload,
            string currentPlayerText)
        {
            payload = payload ?? new Dictionary<string, object>();
            string sessionId = ReadFirstString(payload, "conversationSessionId", "sessionId");
            string exchangeId = ReadFirstString(payload, "sceneTurnId", "turnId", "exchangeId");
            string playerName = ReadString(payload, "playerName", "Player");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> raw in ReadDictionaryList(payload, "groupTranscript"))
            {
                string rowSessionId = ReadFirstString(raw, "sessionId", "session_id");
                if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(rowSessionId)
                    && !string.Equals(sessionId, rowSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string text = ReadString(raw, "text", "").Trim();
                string speaker = ReadString(raw, "speaker", "Unknown").Trim();
                string role = ReadString(raw, "role", "").Trim().ToLowerInvariant();
                string rowExchangeId = ReadFirstString(raw, "exchangeId", "exchange_id");
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.Add(new Dictionary<string, object>
                {
                    ["id"] = ReadFirstString(raw, "id", "turn_id", "contributionId"),
                    ["turnId"] = ReadString(raw, "turnId", ""),
                    ["sequence"] = ReadInt(raw, "sequence", result.Count + 1),
                    ["sessionId"] = rowSessionId,
                    ["exchangeId"] = rowExchangeId,
                    ["speakerHeroStringId"] = ReadFirstString(raw, "speakerHeroStringId", "speaker_id", "heroStringId"),
                    ["speaker"] = string.IsNullOrWhiteSpace(speaker) ? "Unknown" : speaker,
                    ["role"] = role == "player" || role == "npc" || role == "system" ? role : "npc",
                    ["text"] = text
                });
            }

            if (result.Count == 0)
            {
                foreach (string legacy in ReadStringList(payload, "transcript"))
                {
                    int separator = (legacy ?? "").IndexOf(':');
                    string speaker = separator > 0 ? legacy.Substring(0, separator).Trim() : "Unknown";
                    string text = separator > 0 ? legacy.Substring(separator + 1).Trim() : (legacy ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    result.Add(new Dictionary<string, object>
                    {
                        ["sequence"] = result.Count + 1,
                        ["sessionId"] = sessionId,
                        ["exchangeId"] = "",
                        ["speakerHeroStringId"] = "",
                        ["speaker"] = speaker,
                        ["role"] = string.Equals(speaker, playerName, StringComparison.OrdinalIgnoreCase) ? "player" : "npc",
                        ["text"] = text
                    });
                }
            }

            List<Dictionary<string, object>> ordered = result.OrderBy(row => ReadInt(row, "sequence", 0)).ToList();
            int limit = Math.Max(2, Math.Min(100, ReadInt(SettingsService.LoadSettings(), "recentRawConversationTurns", 30)));
            return CanonicalizePromptTranscript(ordered, limit);
        }

        internal static List<Dictionary<string, object>> BuildSocialEventPromptTranscript(
            IEnumerable<Dictionary<string, object>> source, int configuredLimit)
        {
            // OrderBy is stable: keep append order within a second, including the
            // player and sequential NPCs. Reversing a descending sort reversed ties.
            return CanonicalizePromptTranscript(
                (source ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Where(row => row != null)
                    .OrderBy(row => ReadLong(row, "ts", 0)), configuredLimit);
        }

        private readonly record struct SocialEventPromptIndexRow(long Timestamp, long Ordinal, string Exchange, bool Unbound);

        internal static List<Dictionary<string, object>> BuildIndexedSocialEventPromptTranscript(
            Func<HashSet<long>, IEnumerable<(long Ordinal, Dictionary<string, object> Row)>> readRows, int configuredLimit)
        {
            int limit = Math.Max(2, Math.Min(100, configuredLimit));
            var winners = new Dictionary<string, SocialEventPromptIndexRow>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<SocialEventPromptIndexRow>();

            // Read and parse every row before selecting the window. Only compact
            // metadata survives this pass, including the earliest chronological row
            // for each identity and physical order as the timestamp tie-break.
            foreach (var (ordinal, row) in readRows(null))
            {
                if (row == null || string.IsNullOrWhiteSpace(ReadString(row, "text", ""))) continue;
                string identity = PromptTranscriptIdentity(row);
                long timestamp = ReadLong(row, "ts", 0);
                if (identity.Length > 0 && winners.TryGetValue(identity, out var previous)
                    && (timestamp > previous.Timestamp || (timestamp == previous.Timestamp && ordinal > previous.Ordinal)))
                    continue;

                var entry = new SocialEventPromptIndexRow(timestamp, ordinal,
                    ReadFirstString(row, "exchangeId", "exchange_id", "sceneTurnId"), identity.Length == 0);
                if (entry.Unbound) ordered.Add(entry);
                else winners[identity] = entry;
            }

            ordered.AddRange(winners.Values);
            winners.Clear();
            if (ordered.Count == 0) return new List<Dictionary<string, object>>();
            ordered.Sort((left, right) =>
            {
                int timestampOrder = left.Timestamp.CompareTo(right.Timestamp);
                return timestampOrder == 0 ? left.Ordinal.CompareTo(right.Ordinal) : timestampOrder;
            });

            int start = Math.Max(0, ordered.Count - limit);
            string boundaryExchange = ordered[start].Exchange;
            if (!string.IsNullOrWhiteSpace(boundaryExchange))
            {
                while (start > 0 && string.Equals(ordered[start - 1].Exchange, boundaryExchange, StringComparison.OrdinalIgnoreCase))
                    start--;
            }

            var slots = new Dictionary<long, int>(ordered.Count - start);
            for (int rank = start; rank < ordered.Count; rank++)
                slots.Add(ordered[rank].Ordinal, rank);
            var result = new Dictionary<string, object>[slots.Count];
            foreach (var (ordinal, row) in readRows(slots.Keys.ToHashSet()))
            {
                int rank = slots[ordinal];
                if (row == null)
                    throw new InvalidOperationException("Event transcript changed during the indexed read.");
                if (ordered[rank].Unbound)
                    BindUnidentifiedPromptTranscriptRow(row, rank);
                result[rank - start] = row;
            }

            if (result.Any(row => row == null))
                throw new InvalidOperationException("Event transcript changed during the indexed read.");
            return result.ToList();
        }

        private static void BindUnidentifiedPromptTranscriptRow(Dictionary<string, object> row, int rank)
        {
            if (ReadFirstString(row, "id", "turn_id", "turnId").Length == 0)
                row["promptLocalSourceId"] = "unbound_" + rank + "_" + PromptHash(ReadString(row, "text", "")).Substring(0, 8);
        }

        internal static List<Dictionary<string, object>> CanonicalizePromptTranscript(
            IEnumerable<Dictionary<string, object>> source, int configuredLimit)
        {
            int limit = Math.Max(2, Math.Min(100, configuredLimit));
            List<Dictionary<string, object>> unique = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in source ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                if (row == null || string.IsNullOrWhiteSpace(ReadString(row, "text", ""))) continue;
                string identity = DialogueService.PromptTranscriptIdentity(row);
                if (identity.Length > 0 && !seen.Add(identity)) continue;
                if (identity.Length == 0)
                    BindUnidentifiedPromptTranscriptRow(row, unique.Count);
                unique.Add(row);
            }
            if (unique.Count <= limit) return unique;

            int start = unique.Count - limit;
            string boundaryExchange = ReadFirstString(unique[start], "exchangeId", "exchange_id", "sceneTurnId");
            if (!string.IsNullOrWhiteSpace(boundaryExchange))
            {
                while (start > 0 && string.Equals(ReadFirstString(unique[start - 1], "exchangeId", "exchange_id", "sceneTurnId"), boundaryExchange, StringComparison.OrdinalIgnoreCase))
                    start--;
            }
            return unique.Skip(start).ToList();
        }

        internal static string ResolveHeroIdByCharacterObjectId(string campaignId, string characterObjectId)
        {
            if (string.IsNullOrWhiteSpace(characterObjectId))
            {
                return string.Empty;
            }

            ArrayList index = CampaignFileService.ReadJsonArray(Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "characters", "index.json"));
            foreach (object item in index)
            {
                Dictionary<string, object> entry = item as Dictionary<string, object>;
                string heroId = CampaignFileService.CharacterIdFrom(entry);
                if (string.IsNullOrWhiteSpace(heroId))
                {
                    continue;
                }

                Dictionary<string, object> profile = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "profile.json"));
                if (string.Equals(ReadString(profile, "characterObjectId", ""), characterObjectId, StringComparison.OrdinalIgnoreCase))
                {
                    return heroId;
                }
            }

            string charactersDir = Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "characters");
            if (!Directory.Exists(charactersDir))
            {
                return string.Empty;
            }

            foreach (string dir in Directory.GetDirectories(charactersDir))
            {
                Dictionary<string, object> profile = CampaignFileService.ReadJsonObject(Path.Combine(dir, "profile.json"));
                if (string.Equals(ReadString(profile, "characterObjectId", ""), characterObjectId, StringComparison.OrdinalIgnoreCase))
                {
                    return CampaignFileService.CharacterIdFrom(profile);
                }
            }

            return string.Empty;
        }

        internal static string BuildMemoryPacketForPrompt(string campaignId, string heroId, Dictionary<string, object> profile,
            string playerText, string sceneContext, Dictionary<string, object> interactionContext, string interactionMode,
            Dictionary<string, object> promptTiming = null)
        {
            try
            {
                Dictionary<string, object> npcContext = profile == null
                    ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, object>(profile, StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> pair in interactionContext ?? new Dictionary<string, object>())
                    npcContext[pair.Key] = pair.Value;
                npcContext["interactionMode"] = DialogueService.NormalizeMemoryCategory(interactionMode);
                npcContext["deferContinuityAllocation"] = true;
                npcContext["memoryQueryText"] = playerText ?? "";
                string playerId = ReadFirstString(npcContext, "mainHeroStringId", "playerHeroStringId");
                string locationId = FirstNonEmpty(ReadFirstString(npcContext, "locationId", "currentSettlementId", "settlementId"), "");
                string topic = (playerText ?? "").Trim();
                Dictionary<string, object> packet = BuildNpcMemoryPacket(campaignId, heroId, playerId, locationId, topic, 2500, npcContext);
                if (interactionContext != null) interactionContext["memoryPrecisionEvidence"] = ReadDictionary(packet, "precisionEvidence");
                if (promptTiming != null)
                {
                    promptTiming["memoryRecall"] = ReadDictionary(packet, "recallAudit") ?? ReadDictionary(packet, "precisionEvidence");
                    promptTiming["memoryPacketTiming"] =
                        ReadDictionary(packet, "timingPhases") ?? new Dictionary<string, object>();
                    promptTiming["memoryPacketSemanticMs"] = ReadLong(
                        ReadDictionary(packet, "semanticRetrieval") ?? new Dictionary<string, object>(),
                        "durationMs", 0);
                }
                string text = ReadString(packet, "memoryPacket", "");
                Dictionary<string, object> accordContext = ReadDictionary(npcContext, "clanAccords");
                if (accordContext != null)
                    text += "\n" + DiplomacyService.BuildClanAccordsPrompt(campaignId, ReadString(npcContext, "timelineId", "main"), heroId,
                        ReadString(accordContext, "partnerClanId", ""));
                return string.IsNullOrWhiteSpace(text) ? "" : "REIGN KNOWLEDGE PACKET\n" + text;
            }
            catch (Exception ex)
            {
                OperationsService.LogOperational("memory.packet_prompt_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["error"] = ex.Message
                });
                throw new InvalidOperationException("Conversation memory could not be prepared; retry this turn after the logged retrieval failure is resolved.", ex);
            }
        }

        internal static string BuildNoblePromptBlock(Dictionary<string, object> profile)
        {
            if (!ReadBool(profile, "isLord", false))
            {
                return string.Empty;
            }

            string prompt = PromptService.LoadPromptTemplate("noble_prompt.txt").Trim();
            return string.IsNullOrWhiteSpace(prompt)
                ? string.Empty
                : "NOBLE PROMPT - CONDITIONAL CHARACTER LAYER\n" + prompt;
        }

        internal static string BuildCharacterFoundationText(string campaignId, string heroId, string heroName, Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> state)
        {
            profile = profile ?? new Dictionary<string, object>();
            characteristics = characteristics ?? new Dictionary<string, object>();
            state = state ?? new Dictionary<string, object>();

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Use this section every time. It is the character's response foundation; do not reveal trait labels in visible dialogue.");
            builder.AppendLine(CharactersService.BuildCommonerPromptBlock(profile));
            builder.AppendLine();
            builder.AppendLine("Identity and position:");
            AppendPromptLine(builder, "Name", FirstNonEmpty(ReadString(profile, "name", ""), heroName, ReadString(profile, "heroStringId", "")));
            AppendPromptLine(builder, "Role", FirstNonEmpty(ReadString(profile, "occupation", ""), CharactersService.CharacterArchetype(profile)));
            AppendPromptLine(builder, "Culture", FirstNonEmpty(ReadString(profile, "cultureName", ""), ReadString(profile, "cultureId", "")));
            AppendPromptLine(builder, "Clan", FirstNonEmpty(ReadString(profile, "clanName", ""), ReadString(profile, "clanId", "")));
            AppendPromptLine(builder, "Kingdom", FirstNonEmpty(ReadString(profile, "kingdomName", ""), ReadString(profile, "kingdomId", "")));
            AppendPromptLine(builder, "Current place", FirstNonEmpty(ReadString(profile, "currentSettlementName", ""), ReadString(profile, "currentSettlementId", ""), ReadString(state, "lastKnownSettlementId", "")));
            AppendPromptLine(builder, "Status", CharacterStatusText(profile));
            AppendPromptLine(builder, "Player relation", RelationshipDescriptor(ReadInt(profile, "relationToPlayer", 0)));

            builder.AppendLine();
            Dictionary<string, object> traitDocument = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> foundationTraits = ReadDictionary(traitDocument, "foundationTraits") ?? ReadDictionary(traitDocument, "hiddenReignTraits") ?? new Dictionary<string, object>();
            string mbtiBlock = RelationshipsService.BuildCharacterMbtiPromptBlock(campaignId, heroId, profile, traitDocument);
            if (!string.IsNullOrWhiteSpace(mbtiBlock))
            {
                builder.AppendLine(mbtiBlock);
                builder.AppendLine();
            }
            string basePersonality = ReadString(traitDocument, "basePersonalitySummary", "");
            if (string.IsNullOrWhiteSpace(basePersonality))
            {
                basePersonality = CharactersService.BuildRuleBasedPersonalitySummary(FirstNonEmpty(ReadString(profile, "name", ""), heroName), foundationTraits);
            }
            builder.AppendLine("Base personality - always apply this rounded synthesis of the complete trait foundation:");
            builder.AppendLine(basePersonality);

            string coreSkills = CharactersService.BuildCoreSkillAwarenessPrompt(profile, characteristics);
            if (!string.IsNullOrWhiteSpace(coreSkills))
            {
                builder.AppendLine();
                builder.AppendLine(coreSkills);
            }

            Dictionary<string, object> appearance = ReadDictionary(characteristics, "appearance") ?? new Dictionary<string, object>();
            string appearanceText = FormatNamedObjectForPrompt(appearance, new[] { "firstView", "visibleStatusLabel", "trueStatusLabel", "presentationEffect", "statusMismatch", "civilianEquipmentValue" });
            if (!string.IsNullOrWhiteSpace(appearanceText))
            {
                builder.AppendLine();
                builder.AppendLine("First visible impression and apparent status:");
                builder.AppendLine(appearanceText);
                builder.AppendLine("- Social reading rule: NPCs initially react to visibleStatusLabel and firstView, not perfect truth. Worldly, suspicious, or observant NPCs may detect mismatch; naive or appearance-sensitive NPCs may be fooled.");
            }

            Dictionary<string, object> wealth = ReadDictionary(characteristics, "wealth") ?? new Dictionary<string, object>();
            string wealthText = FormatNamedObjectForPrompt(DialogueService.PromptWealthFacts(wealth), DialogueService.PromptWealthKeys);
            if (!string.IsNullOrWhiteSpace(wealthText))
            {
                builder.AppendLine();
                builder.AppendLine("Money, clan backing, and material position:");
                builder.AppendLine(wealthText);
                builder.AppendLine("- Clan backing rule: personal poverty does not erase clan reputation. Characters may invoke, borrow against, or posture from clan wealth and renown if canLeanOnClanReputation is true.");
            }

            Dictionary<string, object> inventory = ReadDictionary(characteristics, "inventory") ?? new Dictionary<string, object>();
            string inventoryText = FormatInventorySummaryForPrompt(inventory);
            if (!string.IsNullOrWhiteSpace(inventoryText))
            {
                builder.AppendLine();
                builder.AppendLine("Carried resources and notable items:");
                builder.AppendLine(inventoryText);
            }

            Dictionary<string, object> voice = ReadDictionary(characteristics, "voice") ?? new Dictionary<string, object>();
            string voiceText = FormatNamedObjectForPrompt(voice, new[] { "speechStyle", "socialMask", "tells" });
            if (!string.IsNullOrWhiteSpace(voiceText))
            {
                builder.AppendLine();
                builder.AppendLine("Voice and social mask:");
                builder.AppendLine(voiceText);
            }

            Dictionary<string, object> motivations = ReadDictionary(characteristics, "motivations") ?? new Dictionary<string, object>();
            string motivationText = FormatNamedObjectForPrompt(motivations, new[] { "dreams", "fears", "desires", "linesTheyWillNotCross" });
            if (!string.IsNullOrWhiteSpace(motivationText))
            {
                builder.AppendLine();
                builder.AppendLine("Core motives that may bend almost any response:");
                builder.AppendLine(motivationText);
            }

            string stateText = FormatNamedObjectForPrompt(state, new[] { "mood", "currentPlan", "currentCrisis", "lastKnownSettlementId" });
            if (!string.IsNullOrWhiteSpace(stateText))
            {
                builder.AppendLine();
                builder.AppendLine("Current state:");
                builder.AppendLine(stateText);
            }

            return builder.ToString().TrimEnd();
        }

        internal static string BuildContextPullText(
            string playerText,
            string sceneContext,
            Dictionary<string, object> characteristics,
            Dictionary<string, object> relationships,
            Dictionary<string, object> summary,
            List<Dictionary<string, object>> memories,
            List<Dictionary<string, object>> recentLines,
            List<Dictionary<string, object>> selectedPulls,
            List<Dictionary<string, object>> contextBundles,
            bool eventMode,
            Dictionary<string, object> identityView = null)
        {
            characteristics = characteristics ?? new Dictionary<string, object>();
            relationships = relationships ?? new Dictionary<string, object>();
            summary = summary ?? new Dictionary<string, object>();
            memories = memories ?? new List<Dictionary<string, object>>();
            recentLines = recentLines ?? new List<Dictionary<string, object>>();
            selectedPulls = NormalizeSelectedContextPulls(selectedPulls ?? new List<Dictionary<string, object>>(), Program.ContextPullIds.ToList());
            contextBundles = contextBundles ?? new List<Dictionary<string, object>>();

            Dictionary<string, List<Dictionary<string, object>>> bundlesById = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> bundle in contextBundles)
            {
                string bundleId = ReadFirstString(bundle, "id", "pullId", "contextPullId");
                if (string.IsNullOrWhiteSpace(bundleId))
                {
                    continue;
                }

                bundleId = bundleId.Trim();
                if (!bundlesById.ContainsKey(bundleId))
                {
                    bundlesById[bundleId] = new List<Dictionary<string, object>>();
                }

                bundlesById[bundleId].Add(bundle);
            }

            List<string> orderedIds = selectedPulls
                .Select(x => ReadFirstString(x, "id", "pullId", "contextPullId"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string bundleId in bundlesById.Keys.OrderBy(x => x))
            {
                if (!orderedIds.Any(x => string.Equals(x, bundleId, StringComparison.OrdinalIgnoreCase)))
                {
                    orderedIds.Add(bundleId);
                }
            }

            if (orderedIds.Count == 0)
            {
                return "No context pulls selected. Use only the character foundation, immediate scene context, and recent conversation. Do not invent unavailable map, inventory, settlement, or diplomacy facts.";
            }

            string query = ((playerText ?? "") + " " + (sceneContext ?? "")).ToLowerInvariant();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Use these labeled context pulls as selected live context. If a pull is missing or says unavailable, treat that fact as unknown.");
            builder.AppendLine("Do not pretend unavailable map, settlement, party, inventory, wealth, or diplomacy data exists.");

            foreach (string id in orderedIds)
            {
                Dictionary<string, object> selected = selectedPulls.FirstOrDefault(x => string.Equals(ReadFirstString(x, "id", "pullId", "contextPullId"), id, StringComparison.OrdinalIgnoreCase))
                    ?? new Dictionary<string, object> { ["id"] = id };

                builder.AppendLine();
                builder.AppendLine("CONTEXT PULL: " + HumanizeKey(id));
                string reason = ReadString(selected, "reason", "");
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    builder.AppendLine("- Selector reason: " + LimitText(reason, 260));
                }

                Dictionary<string, object> arguments = ReadDictionary(selected, "arguments") ?? new Dictionary<string, object>();
                if (arguments.Count > 0)
                {
                    builder.AppendLine("- Arguments: " + LimitText(Json.Serialize(arguments), 700));
                }

                if (bundlesById.TryGetValue(id, out List<Dictionary<string, object>> bundles) && bundles.Count > 0)
                {
                    foreach (Dictionary<string, object> bundle in bundles)
                    {
                        bool ok = ReadBool(bundle, "ok", true);
                        string source = ReadString(bundle, "source", "game_client");
                        long durationMs = ReadLong(bundle, "durationMs", 0);
                        builder.AppendLine("- Bundle source: " + source + "; ok: " + ok + "; durationMs: " + durationMs);
                        string error = ReadString(bundle, "error", "");
                        if (!ok && !string.IsNullOrWhiteSpace(error))
                        {
                            builder.AppendLine("- Bundle error: " + LimitText(error, 500));
                        }

                        Dictionary<string, object> data = ReadDictionary(bundle, "data") ?? new Dictionary<string, object>();
                        if (data.Count > 0)
                        {
                            Dictionary<string, object> promptData = ContextBundleDataForPrompt(id, data, identityView);
                            AppendContextBundleInterpretationRules(builder, id, promptData);
                            builder.AppendLine(LimitText(Json.Serialize(promptData), 3500));
                        }
                        else
                        {
                            string summaryText = ReadString(bundle, "summary", "");
                            builder.AppendLine(string.IsNullOrWhiteSpace(summaryText) ? "No compact data returned." : LimitText(summaryText, 1200));
                        }
                    }

                    continue;
                }

                AppendServerContextFallback(builder, id, characteristics, relationships, summary, memories, recentLines, query, eventMode);
            }

            return builder.ToString().TrimEnd();
        }

        private static Dictionary<string, object> ContextBundleDataForPrompt(
            string id,
            Dictionary<string, object> data,
            Dictionary<string, object> identityView = null)
        {
            data = data ?? new Dictionary<string, object>();
            bool playerAppearance = string.Equals(id, "check_player_appearance_status", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> subject = ReadDictionary(data, "subject") ?? new Dictionary<string, object>();
            bool playerSubject = ReadString(subject, "heroStringId", "")
                .Equals("main_hero", StringComparison.OrdinalIgnoreCase);
            bool playerInventoryAppearance = playerSubject
                && string.Equals(id, "check_inventory_appearance", StringComparison.OrdinalIgnoreCase);
            bool identityKnown = ReadBool(identityView ?? new Dictionary<string, object>(), "knowsIdentity", false);
            bool hasPlayerBranch = ReadDictionary(data, "player") != null;
            if (!playerAppearance && !playerInventoryAppearance && (identityKnown || !hasPlayerBranch))
                return data;

            // The raw bundle remains intact in audit/report evidence. The model receives
            // only what an observer can actually see. A claimed or even verified name
            // does not reveal the coins in the player's purse, party inventory value,
            // clan treasury, exact renown, or other private native-state fields.
            if (!playerAppearance && !playerInventoryAppearance)
            {
                Dictionary<string, object> safeBundle = new Dictionary<string, object>(data, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> player = ReadDictionary(data, "player") ?? new Dictionary<string, object>();
                Dictionary<string, object> safePlayer = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["subject"] = new Dictionary<string, object> { ["role"] = "player" },
                    ["knowledgeBoundary"] =
                        "The observer has not verified this player's identity. Their canonical name, clan, title, exact gold, inventory, renown, fiefs, influence, workshops, and other private native state are unknown. Use only separately supplied visible appearance or facts explicitly stated in the current conversation."
                };
                Dictionary<string, object> playerAppearanceData = ReadDictionary(player, "appearance") ?? new Dictionary<string, object>();
                Dictionary<string, object> playerVisible = VisiblePlayerAppearanceForPrompt(playerAppearanceData);
                if (playerVisible.Count > 0)
                    safePlayer["appearance"] = playerVisible;
                safeBundle["player"] = safePlayer;
                safeBundle.Remove("ownedWorkshopsInvolved");
                return safeBundle;
            }

            Dictionary<string, object> safe = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["subject"] = new Dictionary<string, object> { ["role"] = "player" }
            };
            Dictionary<string, object> appearance = ReadDictionary(data, "appearance")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> visible = VisiblePlayerAppearanceForPrompt(appearance);
            if (visible.Count > 0)
                safe["appearance"] = visible;
            safe["knowledgeBoundary"] =
                "This is visual evidence only. It does not reveal the player's canonical identity, clan membership or tier, exact gold, clan treasury, inventory value, renown, fiefs, hidden equipment, or true wealth. State those facts only when separately supplied by an appropriate identity- and knowledge-safe context source.";
            return safe;
        }

        private static Dictionary<string, object> VisiblePlayerAppearanceForPrompt(
            Dictionary<string, object> appearance)
        {
            appearance = appearance ?? new Dictionary<string, object>();
            Dictionary<string, object> visible = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string visibleStatus = ReadString(appearance, "visibleStatusLabel", "");
            if (!string.IsNullOrWhiteSpace(visibleStatus))
                visible["visibleStatusLabel"] = visibleStatus;
            string visibleDetails = ExtractVisibleAppearanceDetails(ReadString(appearance, "firstView", ""));
            if (!string.IsNullOrWhiteSpace(visibleDetails))
                visible["visibleDetails"] = visibleDetails;
            return visible;
        }

        private static string ExtractVisibleAppearanceDetails(string firstView)
        {
            if (string.IsNullOrWhiteSpace(firstView))
                return "";
            const string marker = "Visible details:";
            int start = firstView.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return "";
            string details = firstView.Substring(start + marker.Length).Trim();
            int presentation = details.IndexOf("Their presentation", StringComparison.OrdinalIgnoreCase);
            if (presentation >= 0)
                details = details.Substring(0, presentation).Trim();
            return details.Trim().TrimEnd('.');
        }

        private static void AppendContextBundleInterpretationRules(StringBuilder builder, string id, Dictionary<string, object> data)
        {
            if (string.Equals(id, "check_player_appearance_status", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(id, "check_inventory_appearance", StringComparison.OrdinalIgnoreCase)
                    && ReadString(ReadDictionary(data, "subject") ?? new Dictionary<string, object>(), "role", "")
                        .Equals("player", StringComparison.OrdinalIgnoreCase)))
            {
                builder.AppendLine("- Visual knowledge rule: describe only the supplied visible clothing, equipment, and apparent status. Never infer an exact purse balance, clan treasury, inventory value, renown, fief count, clan tier, canonical identity, or undisplayed wealth from appearance.");
            }
            if (!string.Equals(id, "nearby_lord_parties", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, "nearby_bandit_parties", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, "nearby_settlements", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            builder.AppendLine("- Native spatial evidence rule: a numeric distance field is only a raw game-map distance scalar. You may compare or quote it as a raw distance, but do not convert it into travel time, hours, days, marches, or route length.");
            builder.AppendLine("- Native spatial evidence rule: do not state or imply north, south, east, west, a bearing, a direction, or a route unless that specific field is present in this bundle. List position-independent facts without adding geography.");
        }

        private static void AppendServerContextFallback(
            StringBuilder builder,
            string id,
            Dictionary<string, object> characteristics,
            Dictionary<string, object> relationships,
            Dictionary<string, object> summary,
            List<Dictionary<string, object>> memories,
            List<Dictionary<string, object>> recentLines,
            string query,
            bool eventMode)
        {
            switch ((id ?? "").ToLowerInvariant())
            {
                case "verify_world_history":
                    builder.AppendLine("No authoritative world-history verification bundle was returned. Treat the claim as unverified and do not accuse anyone from missing evidence.");
                    break;
                case "check_inventory_appearance":
                    AppendSelectedObject(builder, "Appearance, equipment, and social presentation", ReadDictionary(characteristics, "appearance"), new[] { "firstView", "visibleStatusLabel", "trueStatusLabel", "presentationEffect", "statusMismatch", "civilianEquipmentValue", "battleEquipmentValue", "civilianEquipment" });
                    {
                        string inventoryText = FormatInventorySummaryForPrompt(ReadDictionary(characteristics, "inventory"));
                        builder.AppendLine(string.IsNullOrWhiteSpace(inventoryText) ? "No inventory snapshot is available from the server cache." : inventoryText);
                    }
                    break;
                case "clan_wealth_and_influence":
                    AppendSelectedObject(builder, "Wealth, clan credit, and money", DialogueService.PromptWealthFacts(ReadDictionary(characteristics, "wealth")), DialogueService.PromptWealthKeys);
                    break;
                case "relevant_memory":
                    AppendSelectedObject(builder, "Backstory and public reputation", ReadDictionary(characteristics, "background"), new[] { "summary", "centralWound", "publicReputation", "canonBuiltFrom" });
                    AppendSelectedObject(builder, "Hidden history and private wounds", ReadDictionary(characteristics, "hiddenHistory"), new[] { "privateBackstory", "secretWounds", "privateEntanglements", "knownOnlyTo" });
                    AppendSelectedObject(builder, "Save quirks and secrets", ReadDictionary(characteristics, "saveQuirks"), new[] { "secretInterest", "jealousyTargetType", "currentTemptation", "secretInterests", "jealousyTargets", "temptations" });
                    if (summary.Count > 0)
                    {
                        AppendSelectedObject(builder, "Rolling memory summary", summary, summary.Keys.Take(10).ToArray());
                    }

                    {
                        List<Dictionary<string, object>> selectedMemories = SelectMemoriesForPrompt(memories, query, 8);
                        string memoryText = FormatMemoriesForPrompt(selectedMemories);
                        builder.AppendLine(string.Equals(memoryText, "none", StringComparison.OrdinalIgnoreCase) ? "No matching long-term memories were selected." : memoryText);
                    }
                    break;
                case "relationship_history":
                    if (relationships.Count > 0)
                    {
                        AppendSelectedObject(builder, "Relationship memory", relationships, relationships.Keys.Where(x => !x.Equals("version", StringComparison.OrdinalIgnoreCase)).Take(16).ToArray());
                    }
                    else
                    {
                        builder.AppendLine("No relationship ledger is available.");
                    }

                    {
                        string recentText = eventMode ? FormatEventLinesForPrompt(recentLines) : FormatDialogueForPrompt(recentLines);
                        builder.AppendLine(string.Equals(recentText, "none", StringComparison.OrdinalIgnoreCase) ? "No recent transcript is available." : recentText);
                    }
                    break;
                default:
                    builder.AppendLine("No client bundle was returned for this live game pull. Treat this fact as unknown and do not infer it from stale character memory.");
                    break;
            }
        }

        internal static string TraitValueDescriptor(string key, int value)
        {
            if (CharactersService.TraitSentenceDescriptors().TryGetValue(key, out string[] descriptors))
            {
                int index = CharactersService.ClampTrait(value) + 2;
                if (index >= 0 && index < descriptors.Length)
                {
                    return descriptors[index];
                }
            }

            switch (value)
            {
                case -2:
                    return "strongly opposite or defining inverse";
                case -1:
                    return "leans opposite";
                case 1:
                    return "clearly present";
                case 2:
                    return "defining trait";
                default:
                    return "ordinary, balanced, or situational";
            }
        }

        internal static string RelationshipDescriptor(int relation)
        {
            if (relation <= -50)
            {
                return "hates or deeply distrusts the player";
            }

            if (relation <= -20)
            {
                return "hostile or resentful toward the player";
            }

            if (relation < 0)
            {
                return "wary of the player";
            }

            if (relation >= 50)
            {
                return "strongly favors or trusts the player";
            }

            if (relation >= 20)
            {
                return "warm toward the player";
            }

            if (relation > 0)
            {
                return "slightly favorable toward the player";
            }

            return "no strong established feeling toward the player";
        }

        internal static string CharacterStatusText(Dictionary<string, object> profile)
        {
            List<string> parts = new List<string>();
            if (ReadBool(profile, "isLord", false))
            {
                parts.Add("lord/lady");
            }

            if (ReadBool(profile, "isNotable", false))
            {
                parts.Add("notable");
            }

            if (ReadBool(profile, "isWanderer", false))
            {
                parts.Add("wanderer");
            }

            if (ReadBool(profile, "isPrisoner", false))
            {
                parts.Add("prisoner");
            }

            if (ReadBool(profile, "isFemale", false))
            {
                parts.Add("female");
            }

            return parts.Count == 0 ? "ordinary campaign character" : string.Join(", ", parts.ToArray());
        }

        internal static string WealthTier(int value)
        {
            if (value >= 200000)
            {
                return "great wealth";
            }

            if (value >= 80000)
            {
                return "wealthy";
            }

            if (value >= 25000)
            {
                return "comfortable";
            }

            if (value >= 5000)
            {
                return "modest";
            }

            if (value >= 1000)
            {
                return "poor";
            }

            return "destitute";
        }

        internal static string ClanSocialCredit(int clanGold, int clanTier, double clanRenown, int clanFiefCount, bool isLord)
        {
            int score = 0;
            if (isLord)
            {
                score += 1;
            }

            if (clanGold >= 200000)
            {
                score += 4;
            }
            else if (clanGold >= 80000)
            {
                score += 3;
            }
            else if (clanGold >= 25000)
            {
                score += 2;
            }
            else if (clanGold >= 5000)
            {
                score += 1;
            }

            if (clanTier >= 5)
            {
                score += 3;
            }
            else if (clanTier >= 3)
            {
                score += 2;
            }
            else if (clanTier >= 1)
            {
                score += 1;
            }

            if (clanRenown >= 600d)
            {
                score += 2;
            }
            else if (clanRenown >= 200d)
            {
                score += 1;
            }

            if (clanFiefCount >= 3)
            {
                score += 2;
            }
            else if (clanFiefCount >= 1)
            {
                score += 1;
            }

            if (score >= 8)
            {
                return "great-house backing";
            }

            if (score >= 5)
            {
                return "strong clan backing";
            }

            if (score >= 3)
            {
                return "credible clan backing";
            }

            if (score >= 1)
            {
                return "minor clan backing";
            }

            return "little clan backing";
        }

        internal static void AppendPromptLine(StringBuilder builder, string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder.AppendLine("- " + label + ": " + value);
            }
        }

        private static void AppendSelectedObject(StringBuilder builder, string title, Dictionary<string, object> source, string[] keys)
        {
            string text = FormatNamedObjectForPrompt(source, keys);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine(title + ":");
            builder.AppendLine(text);
        }

        internal static string FormatNamedObjectForPrompt(Dictionary<string, object> source, string[] keys)
        {
            if (source == null || source.Count == 0 || keys == null || keys.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            foreach (string key in keys)
            {
                if (string.IsNullOrWhiteSpace(key) || !source.TryGetValue(key, out object value))
                {
                    continue;
                }

                string text = PromptObjectText(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine("- " + HumanizeKey(key) + ": " + text);
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static string FormatInventorySummaryForPrompt(Dictionary<string, object> inventory)
        {
            if (inventory == null || inventory.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            AppendPromptLine(builder, "Inventory source", ReadString(inventory, "source", ""));
            AppendPromptLine(builder, "Party id", ReadString(inventory, "partyId", ""));
            AppendPromptLine(builder, "Total item value", ReadInt(inventory, "totalValue", 0).ToString());
            AppendPromptLine(builder, "Trade goods value", ReadInt(inventory, "tradeGoodsValue", 0).ToString());
            AppendPromptLine(builder, "Food", ReadInt(inventory, "totalFood", 0).ToString());
            AppendPromptLine(builder, "Mounts", ReadInt(inventory, "mounts", 0).ToString());

            List<string> itemTexts = new List<string>();
            object rawItems;
            if (inventory.TryGetValue("topItems", out rawItems))
            {
                foreach (object raw in ActionPipelineService.ObjectsFromValue(rawItems).Take(10))
                {
                    Dictionary<string, object> item = raw as Dictionary<string, object>;
                    if (item == null)
                    {
                        continue;
                    }

                    string name = ReadString(item, "name", ReadString(item, "itemId", ""));
                    int amount = ReadInt(item, "amount", 1);
                    int totalValue = ReadInt(item, "totalValue", ReadInt(item, "value", 0) * Math.Max(1, amount));
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        itemTexts.Add(name + " x" + amount + " (value " + totalValue + ")");
                    }
                }
            }

            if (itemTexts.Count > 0)
            {
                AppendPromptLine(builder, "Notable items", string.Join("; ", itemTexts.ToArray()));
            }

            return builder.ToString().TrimEnd();
        }

        internal static string PromptObjectText(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is string text)
            {
                return text.Trim();
            }

            if (value is System.Collections.IEnumerable list && !(value is System.Collections.IDictionary))
            {
                List<string> parts = new List<string>();
                foreach (object item in list)
                {
                    string itemText = PromptObjectText(item);
                    if (!string.IsNullOrWhiteSpace(itemText))
                    {
                        parts.Add(itemText);
                    }
                }

                return string.Join("; ", parts);
            }

            if (value is Dictionary<string, object> dict)
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    string itemText = PromptObjectText(pair.Value);
                    if (!string.IsNullOrWhiteSpace(itemText))
                    {
                        parts.Add(HumanizeKey(pair.Key) + ": " + itemText);
                    }
                }

                return string.Join("; ", parts);
            }

            return Convert.ToString(value);
        }

        internal static string HumanizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < key.Length; index++)
            {
                char c = key[index];
                if (index > 0 && char.IsUpper(c) && !char.IsWhiteSpace(key[index - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(c == '_' || c == '-' ? ' ' : c);
            }

            return builder.ToString().Trim().ToLowerInvariant();
        }

        internal static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(text) || needles == null)
            {
                return false;
            }

            foreach (string needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle) && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool ContainsAnyTerm(string text, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(text) || terms == null)
            {
                return false;
            }

            foreach (string term in terms)
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    continue;
                }

                string pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(term.Trim()) + @"(?![A-Za-z0-9])";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<Dictionary<string, object>> SelectMemoriesForPrompt(List<Dictionary<string, object>> memories, string query, int limit)
        {
            if (memories == null || memories.Count == 0)
            {
                return new List<Dictionary<string, object>>();
            }

            List<string> queryTags = CampaignFileService.SimpleTags(query ?? "");
            IEnumerable<Dictionary<string, object>> ranked = memories
                .Select(memory => new Dictionary<string, object>
                {
                    ["memory"] = memory,
                    ["score"] = MemoryPromptScore(memory, query, queryTags)
                })
                .OrderByDescending(x => ReadInt(x, "score", 0))
                .ThenByDescending(x => ReadLong(ReadDictionary(x, "memory"), "ts", 0))
                .Take(Math.Max(1, limit))
                .Select(x => ReadDictionary(x, "memory"))
                .Where(x => x != null);

            return ranked.ToList();
        }

        private static int MemoryPromptScore(Dictionary<string, object> memory, string query, List<string> queryTags)
        {
            int score = ReadInt(memory, "importance", 1);
            string text = ReadString(memory, "text", "").ToLowerInvariant();
            foreach (string tag in ReadStringList(memory, "tags"))
            {
                if (queryTags.Contains(tag))
                {
                    score += 4;
                }
            }

            foreach (string tag in queryTags)
            {
                if (!string.IsNullOrWhiteSpace(tag) && text.Contains(tag))
                {
                    score += 2;
                }
            }

            return score;
        }

        internal static string ApplyTemplate(string template, Dictionary<string, string> values)
        {
            string result = template ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in values ?? new Dictionary<string, string>())
            {
                result = result.Replace("{" + pair.Key + "}", pair.Value ?? string.Empty);
            }

            return result;
        }

        private static string FormatMemoriesForPrompt(List<Dictionary<string, object>> memories)
        {
            if (memories == null || memories.Count == 0)
            {
                return "none";
            }

            StringBuilder builder = new StringBuilder();
            foreach (Dictionary<string, object> memory in memories)
            {
                string text = ReadString(memory, "text", "");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine("- " + text);
                }
            }

            return builder.Length == 0 ? "none" : builder.ToString().TrimEnd();
        }

        internal static string FormatDialogueForPrompt(List<Dictionary<string, object>> priorLines)
        {
            if (priorLines == null || priorLines.Count == 0)
            {
                return "none";
            }

            StringBuilder builder = new StringBuilder();
            foreach (Dictionary<string, object> line in DialogueService.CompactRoleplayTranscriptForPrompt(priorLines))
            {
                string speaker = ReadString(line, "speaker", "Unknown");
                string text = ReadString(line, "text", "");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string channel = ReadString(line, "channel", "in_person");
                    string prefix = channel.Equals("correspondence", StringComparison.OrdinalIgnoreCase)
                        ? "[Letter " + ReadString(line, "direction", "exchanged") + ", Day " + ReadDouble(line, "worldDay", 0d).ToString("0.##", CultureInfo.InvariantCulture) + "] "
                        : "[In person] ";
                    builder.AppendLine("- " + prefix + speaker + ": " + text);
                    if (ReadString(line, "role", "") == "npc") builder.AppendLine(DialogueService.RecallStatementSourceCaveat(text));
                }
            }

            return builder.Length == 0 ? "none" : builder.ToString().TrimEnd();
        }

        internal static Dictionary<string, object> DialogueLine(long ts, string role, string speaker, string text)
        {
            return new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["ts"] = ts,
                ["role"] = role ?? "",
                ["speaker"] = speaker ?? "",
                ["channel"] = "in_person",
                ["text"] = string.Equals(role, "npc", StringComparison.OrdinalIgnoreCase) ? ConversationResponseService.SanitizeVisibleReply(text ?? "") : (text ?? "")
            };
        }

        internal static List<Dictionary<string, object>> ReadDialogueLines(string campaignId, string heroId, int limit)
        {
            string path = CampaignFileService.CharacterFile(campaignId, heroId, "history", "dialogue.jsonl");
            List<Dictionary<string, object>> all = Program.ReadJsonLinesFromPath(path);
            int start = Math.Max(0, all.Count - Math.Max(1, limit));
            List<Dictionary<string, object>> lines = all.Skip(start).ToList();
            foreach (Dictionary<string, object> line in lines)
            {
                if (string.Equals(ReadString(line, "role", ""), "npc", StringComparison.OrdinalIgnoreCase))
                {
                    line["text"] = ConversationResponseService.SanitizeVisibleReply(ReadString(line, "text", ""));
                }
            }

            return lines;
        }
    }
}
