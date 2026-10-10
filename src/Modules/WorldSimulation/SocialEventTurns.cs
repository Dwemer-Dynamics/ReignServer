using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int SocialEventMaximumActive = 5;
        // Bounded stripes serialize retries of the same turn without retaining a
        // lock object for every conversation ever seen by the process.
        private static readonly object[] SocialEventTurnLocks =
            Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

        private static Dictionary<string, object> SocialEventTurnApi(Dictionary<string, object> payload)
        {
            return SocialEventTurnWithResponder(payload, SocialEventRespond);
        }

        private static Dictionary<string, object> SocialEventTurnPlanApi(Dictionary<string, object> payload)
        {
            PrepareSocialConversationInput(payload);
            var floorPayload = CloneDictionary(payload);
            CopyPlayerInputSource(payload, floorPayload);
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            floorPayload["campaignId"] = ReadString(payload, "campaignId", LatestCampaignId());
            floorPayload["activeHeroIds"] = ReadStringList(payload, "activeHeroIds").Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(SocialEventMaximumActive).ToList();
            floorPayload["mode"] = ReadString(payload, "templateId", "") == "generated_wilderness" ? "wilderness_event" : "social_event";
            floorPayload["conversationSessionId"] = FirstNonEmpty(ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"), "social_event_" + SafeMemoryKey(eventId));
            var result = GroupConversationFloorApi(floorPayload);
            if (payload.TryGetValue("inputReceipt", out var receipt)) result["inputReceipt"] = receipt;
            return result;
        }

        private static void PrepareSocialConversationInput(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", LatestCampaignId());
            payload["conversationSessionId"] = FirstNonEmpty(ReadFirstString(payload, "conversationSessionId", "sessionId"),
                "social_event_" + SafeMemoryKey(ReadFirstString(payload, "eventId", "socialEventId")));
            string generation;
            using (var connection = OpenCampaignConnection(campaign))
                generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            AdmitConversationInput(campaign, payload, generation);
        }

        private static Dictionary<string, object> SocialEventTurnWithResponder(
            Dictionary<string, object> payload,
            Func<Dictionary<string, object>, Dictionary<string, object>> respond)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            string turnId = ReadString(payload, "turnId", "");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(turnId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "eventId and turnId are required." };
            }

            string cachePath = SocialEventTurnCacheFile(campaignId, eventId, turnId);
            int stripe = (StringComparer.OrdinalIgnoreCase.GetHashCode(cachePath) & int.MaxValue) % SocialEventTurnLocks.Length;
            using (EnterCampaignWorkLock(SocialEventTurnLocks[stripe]))
                return SocialEventTurnCore(payload, cachePath, respond);
        }

        private static Dictionary<string, object> SocialEventTurnCore(
            Dictionary<string, object> payload, string cachePath,
            Func<Dictionary<string, object>, Dictionary<string, object>> respond)
        {
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            string turnId = ReadString(payload, "turnId", "");
            bool progressive = ReadBool(payload, "progressiveReplies", false);
            PrepareSocialConversationInput(payload);
            Dictionary<string, object> cached = ReadJsonObject(cachePath);
            if (payload.TryGetValue("inputReceipt", out var inputReceipt)) cached["inputReceipt"] = inputReceipt;
            List<Dictionary<string, object>> results = ReadDictionaryList(cached, "participantResults")
                .Where(row => ReadBool(row, "ok", false)).ToList();
            int cursor = ReadInt(payload, "replyCursor", 0);
            if (progressive && (cursor < 0 || cursor > results.Count))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The reply cursor is ahead of this conversation turn." };
            if (ReadString(cached, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                List<string> cachedActiveIds = ReadStringList(payload, "activeHeroIds")
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(SocialEventMaximumActive).ToList();
                cached["relationshipReceipts"] = ApplySocialEventTurnRelationships(payload, ReadDictionaryList(cached, "participantResults"), cachedActiveIds);
                WriteJsonObject(cachePath, cached);
                cached["ok"] = true;
                cached["idempotent"] = true;
                return cached;
            }
            // A lost response is replayed before any additional speaker is run.
            if (progressive && cursor < results.Count)
                return SocialEventTurnProgress(cached, results);

            List<string> activeIds = ReadStringList(payload, "activeHeroIds")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(SocialEventMaximumActive)
                .ToList();
            if (activeIds.Count == 0)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least one active participant is required." };
            }

            List<Dictionary<string, object>> attendees = ReadDictionaryList(payload, "attendees");
            Dictionary<string, Dictionary<string, object>> attendeeById = attendees
                .Where(x => !string.IsNullOrWhiteSpace(CharacterIdFrom(x)))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> activeProfiles = activeIds
                .Where(attendeeById.ContainsKey)
                .Select(id => attendeeById[id])
                .ToList();

            bool hasPlan = cached.ContainsKey("responseOrder");
            var floor = hasPlan ? ReadDictionary(cached, "speakingFloor") : SocialEventTurnPlanApi(payload);
            if (!hasPlan && !ReadBool(floor, "ok", false)) return floor;
            List<string> addressedIds = hasPlan ? ReadStringList(cached, "addressedHeroStringIds")
                : ReadStringList(floor, "addressedHeroStringIds");
            List<string> responseOrder = hasPlan ? ReadStringList(cached, "responseOrder")
                : ReadStringList(floor, "responseOrder");
            Dictionary<string, object> nextIdle = hasPlan ? ReadDictionary(cached, "nextIdleCounters")
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<string> wanderedIds = hasPlan ? ReadStringList(cached, "wanderedHeroStringIds") : new List<string>();
            foreach (string heroId in hasPlan ? new List<string>() : activeIds)
            {
                // Listening to the active group is participation. Model-chosen
                // silence must not count as being ignored and expel a listener.
                nextIdle[heroId] = 0;
            }

            EnsureSocialTurnPlayerTranscript(campaignId, eventId, turnId, payload, activeProfiles);

            cached = new Dictionary<string, object>
            {
                ["status"] = "in_progress", ["campaignId"] = campaignId,
                ["eventId"] = eventId, ["turnId"] = turnId,
                ["responseOrder"] = responseOrder, ["addressedHeroStringIds"] = addressedIds,
                ["speakingFloor"] = floor,
                ["nextIdleCounters"] = nextIdle, ["wanderedHeroStringIds"] = wanderedIds,
                ["participantResults"] = results
            };
            if (inputReceipt != null) cached["inputReceipt"] = inputReceipt;
            WriteJsonObject(cachePath, cached);
            List<Dictionary<string, object>> requests = ReadDictionaryList(payload, "participantRequests");
            // Generate in resolved address order, not merely the roster order. Later
            // speakers receive the accumulated results, so this is what makes a
            // directly addressed recipient answer before witnesses and gives those
            // witnesses the recipient's actual contribution to react to.
            foreach (string heroId in responseOrder)
            {
                if (results.Any(x => ReadBool(x, "ok", false) && ReadString(x, "heroStringId", "").Equals(heroId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Dictionary<string, object> request = requests.FirstOrDefault(x => ReadFirstString(x, "speakerHeroStringId", "heroStringId").Equals(heroId, StringComparison.OrdinalIgnoreCase));
                if (request == null)
                {
                    return SocialEventTurnFailure(cachePath, campaignId, eventId, turnId, addressedIds, nextIdle, results, "Missing participant request for " + heroId + ".", cached);
                }

                var spokenResults = results.Where(r => ReadBool(r, "ok", false)
                    && ReadString(r, "participation", "speak") != "quiet"
                    && !string.IsNullOrWhiteSpace(ReadString(r, "reply", ""))).ToList();
                Dictionary<string, object> speakerPayload = new Dictionary<string, object>(request, StringComparer.OrdinalIgnoreCase)
                {
                    ["turnId"] = turnId,
                    ["contributionId"] = ReadString(payload, "contributionId", turnId),
                    ["conversationSessionId"] = ReadString(payload, "conversationSessionId", ""),
                    ["playerText"] = OriginalPlayerInput(payload),
                    ["suppressPlayerTranscript"] = true,
                    ["activeHeroIds"] = activeIds,
                    ["groupTurnPlan"] = floor,
                    ["groupTurnResponses"] = spokenResults.Select(CompactSocialEventTurnResult).ToList(),
                    ["groupSpeakerIndex"] = results.Count,
                    ["mustAccountForPriorSpeaker"] = spokenResults.Count > 0,
                    ["isDancePhase"] = IsSocialEventDancePhase(payload),
                    ["canInvitePlayerToDance"] = CanSocialEventSpeakerInviteToDance(payload, request)
                };
                speakerPayload["sceneContext"] = ReadString(speakerPayload, "sceneContext", "")
                    + "\n" + GroupConversationParticipationGuidance;
                if (spokenResults.Count > 0)
                {
                    speakerPayload["sceneContext"] =
                        ReadString(speakerPayload, "sceneContext", "")
                        + "\nCURRENT GROUP BEAT: One or more attributed NPC contributions are already supplied in groupTurnResponses. "
                        + "Use an earlier contribution when relevant; do not invent a new point or repeat an answer merely to take a turn. Quiet is valid. "
                        + "If you engage it, identify that prior speaker in reactionTargetHeroStringId; never transfer another speaker's words or private knowledge to yourself. "
                        + "A contribution with groundingStatus=unresolved is an attributed statement only; do not adopt its unverified scene, membership or action claims as facts.";
                }
                Dictionary<string, object> response = respond(speakerPayload);
                Dictionary<string, object> participant = CompactSocialEventParticipantResponse(heroId, response);
                participant["heroName"] = attendeeById.TryGetValue(
                    heroId,
                    out Dictionary<string, object> participantProfile)
                    ? ReadString(participantProfile, "name", "")
                    : "";
                results.Add(participant);
                WriteJsonObject(cachePath, cached);

                if (!ReadBool(participant, "ok", false))
                {
                    return SocialEventTurnFailure(cachePath, campaignId, eventId, turnId, addressedIds, nextIdle, results,
                        FirstNonEmpty(ReadString(participant, "error", ""), "A participant response failed."), cached);
                }
                // Also yield the final speaker before group-wide adjudication.
                // The client renders this checkpoint before requesting more work.
                if (progressive) return SocialEventTurnProgress(cached, results);
            }

            List<string> joinedIds = ResolveSocialEventMidTurnJoins(campaignId, payload, attendees, activeIds, wanderedIds, results);
            List<Dictionary<string, object>> relationshipReceipts = ApplySocialEventTurnRelationships(payload, results, activeIds);
            Dictionary<string, object> completed = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = "completed",
                ["campaignId"] = campaignId,
                ["eventId"] = eventId,
                ["turnId"] = turnId,
                ["addressedHeroStringIds"] = addressedIds,
                ["speakingFloor"] = floor,
                ["participantResults"] = results,
                ["joinedHeroStringIds"] = joinedIds,
                ["wanderedHeroStringIds"] = wanderedIds,
                ["nextIdleCounters"] = nextIdle,
                ["relationshipReceipts"] = relationshipReceipts,
                ["completedExchange"] = true
            };
            if (inputReceipt != null) completed["inputReceipt"] = inputReceipt;
            WriteJsonObject(cachePath, completed);
            WriteAudit(campaignId, EnsureCorrelationId(payload), "server", "social_event", "social_event.group_turn", "", "", eventId,
                "completed", 0, "Persistent social-event group turn completed.", completed);
            return completed;
        }

        private static Dictionary<string, object> SocialEventTurnProgress(
            Dictionary<string, object> cached, List<Dictionary<string, object>> results)
        {
            return new Dictionary<string, object>(cached)
            {
                ["ok"] = true, ["status"] = "in_progress", ["hasMore"] = true,
                ["completedExchange"] = false, ["replyCursor"] = results.Count,
                ["participantResults"] = results
            };
        }

        private static Dictionary<string, object> SocialEventTurnFailure(
            string cachePath,
            string campaignId,
            string eventId,
            string turnId,
            List<string> addressedIds,
            Dictionary<string, object> nextIdle,
            List<Dictionary<string, object>> results,
            string error,
            Dictionary<string, object> progress = null)
        {
            Dictionary<string, object> failed = new Dictionary<string, object>(progress ?? new Dictionary<string, object>())
            {
                ["ok"] = false,
                ["status"] = "in_progress",
                ["campaignId"] = campaignId,
                ["eventId"] = eventId,
                ["turnId"] = turnId,
                ["error"] = error ?? "Social-event group turn failed.",
                ["addressedHeroStringIds"] = addressedIds ?? new List<string>(),
                ["nextIdleCounters"] = nextIdle ?? new Dictionary<string, object>(),
                ["participantResults"] = results ?? new List<Dictionary<string, object>>()
            };
            WriteJsonObject(cachePath, failed);
            return failed;
        }

        private static string SocialEventTurnCacheFile(string campaignId, string eventId, string turnId)
        {
            // Native live-test correlations and generated event ids can each be
            // long enough to put the installed .NET Framework path exactly at the
            // legacy MAX_PATH boundary. The full ids remain inside the JSON; stable
            // hashes keep the storage path short without weakening idempotency.
            string eventSegment = PromptHash(eventId ?? "unknown").Substring(0, 16);
            string turnSegment = PromptHash(turnId ?? "turn").Substring(0, 24);
            return Path.Combine(CampaignDirectory(campaignId), "turn_cache", "turn_" + eventSegment + "_" + turnSegment + ".json");
        }

        private static void EnsureSocialTurnPlayerTranscript(
            string campaignId,
            string eventId,
            string turnId,
            Dictionary<string, object> payload,
            List<Dictionary<string, object>> activeProfiles)
        {
            bool alreadyStored = ReadJsonLinesFromPath(SocialEventTranscriptFile(campaignId, eventId)).Any(x =>
                ReadString(x, "turnId", "").Equals(turnId, StringComparison.OrdinalIgnoreCase)
                && ReadString(x, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase));
            if (alreadyStored)
            {
                return;
            }

            Dictionary<string, object> row = EventTranscriptLine("player", ReadString(payload, "playerName", "Player"), ReadString(payload, "playerText", ""), payload);
            AppendJsonLineToPath(SocialEventTranscriptFile(campaignId, eventId), row);
            AppendEventLineForAttendees(campaignId, activeProfiles, row);
        }

        private static List<Reign.Core.Contracts.Dialogue.GroupTurnParticipant> SocialTurnParticipants(List<Dictionary<string, object>> profiles) =>
            (profiles ?? new List<Dictionary<string, object>>()).Select(profile => new Reign.Core.Contracts.Dialogue.GroupTurnParticipant
            { Id = CharacterIdFrom(profile), Name = ReadString(profile, "name", "") }).ToList();

        private static List<string> ResolveSocialEventAddressedHeroes(Dictionary<string, object> payload, List<Dictionary<string, object>> activeProfiles)
        {
            var participants = SocialTurnParticipants(activeProfiles);
            var lines = ReadStringList(payload, "transcript").Select(text => {
                var speaker = participants.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name)
                    && text.StartsWith(p.Name + ":", StringComparison.OrdinalIgnoreCase));
                int colon = text.IndexOf(':');
                return new Reign.Core.Contracts.Dialogue.GroupTurnLine {
                    SpeakerId = speaker?.Id ?? "", Role = speaker == null ? "player" : "npc",
                    Text = colon < 0 ? text : text.Substring(colon + 1).Trim() };
            });
            return Reign.Core.Contracts.Dialogue.GroupConversationTurnOrder.ResolveAddressed(participants,
                ReadString(payload, "playerText", ""), lines);
        }

        private static List<string> BuildSocialEventResponseOrder(
            Dictionary<string, object> payload, List<Dictionary<string, object>> activeProfiles,
            List<string> resolvedAddressed = null)
        {
            return Reign.Core.Contracts.Dialogue.GroupConversationTurnOrder.Order(SocialTurnParticipants(activeProfiles),
                ReadString(payload, "playerText", ""), ReadString(payload, "turnId", ReadString(payload, "sceneTurnId", "social_event_turn")),
                resolvedAddressed ?? ResolveSocialEventAddressedHeroes(payload, activeProfiles));
        }

        private static bool ContainsWholePhrase(string text, string phrase)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phrase)) return false;
            return Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(phrase.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string FirstName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? "" : name.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }

        private static Dictionary<string, object> CompactSocialEventParticipantResponse(string heroId, Dictionary<string, object> response)
        {
            response = response ?? new Dictionary<string, object>();
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["ok"] = ReadBool(response, "ok", false),
                ["heroStringId"] = heroId ?? "",
                ["correlationId"] = ReadString(response, "correlationId", ""),
                ["reply"] = ReadString(response, "reply", ""),
                ["participation"] = ReadString(response, "participation", "speak"),
                ["reactionTargetHeroStringId"] = ReadString(response, "reactionTargetHeroStringId", ""),
                ["relationshipAssessments"] = ReadDictionaryList(response, "relationshipAssessments"),
                ["emotion"] = ReadString(response, "emotion", ""),
                ["intent"] = ReadString(response, "intent", ""),
                ["relationshipSignal"] = ReadString(response, "relationshipSignal", ""),
                ["selectedContextPulls"] = ReadDictionaryList(response, "selectedContextPulls"),
                ["contextBundles"] = ReadDictionaryList(response, "contextBundles"),
                ["encyclopediaText"] = ReadString(response, "encyclopediaText", ""),
                ["error"] = ReadFirstString(response, "error", "stage"),
                ["timing"] = ReadDictionary(response, "timing") ?? new Dictionary<string, object>()
            };
            // These fields are private production evidence, not extra prompt
            // metadata. Retain them through the exactly-once group cache so typed
            // and injected event/wilderness turns can be audited to the same
            // standard as individual and party dialogue.
            foreach (string key in new[]
            {
                "identityView", "decisionBrief", "motiveDecision",
                "motiveOutcome", "actionGate", "memoryWrites", "beliefWrites",
                "relationshipUpdates", "obligationWrites",
                "comprehensionWrites", "dynamicCharacteristicWrites",
                "dynamicCharacteristicsStore", "stateUpdates",
                "conversationSceneState", "conversationSceneResolution",
                "suggestedActions", "queuedActions", "actionErrors",
                "identityIntroductionResults", "conversationExchange",
                "construction", "structuredActionDecision", "repairStatus", "conversationGrounding", "inputReceipt"
            })
            {
                if (response.ContainsKey(key))
                    compact[key] = response[key];
            }
            return compact;
        }

        private static Dictionary<string, object> CompactSocialEventTurnResult(Dictionary<string, object> result)
        {
            return new Dictionary<string, object>
            {
                ["heroStringId"] = ReadString(result, "heroStringId", ""),
                ["heroName"] = ReadString(result, "heroName", ""),
                ["participation"] = ReadString(result, "participation", ""),
                ["reactionTargetHeroStringId"] = ReadString(result, "reactionTargetHeroStringId", ""),
                ["groundingStatus"] = ReadString(ReadDictionary(result, "conversationGrounding"), "status", "unknown"),
                ["reply"] = LimitText(ReadString(result, "reply", ""), 1000)
            };
        }

        private static bool SocialConversationResultAllowsEffects(Dictionary<string, object> result) =>
            !ReadBool(result, "conversationGroundingFailed", false)
            && ReadString(ReadDictionary(result, "conversationGrounding"), "status", "") != "unresolved"
            && ReadBool(ReadDictionary(result, "repairStatus"), "allChecksCleared", true);

        private static List<string> ResolveSocialEventMidTurnJoins(
            string campaignId,
            Dictionary<string, object> payload,
            List<Dictionary<string, object>> attendees,
            List<string> originalActiveIds,
            List<string> wanderedIds,
            List<Dictionary<string, object>> results)
        {
            HashSet<string> active = new HashSet<string>(originalActiveIds, StringComparer.OrdinalIgnoreCase);
            HashSet<string> blocked = new HashSet<string>(ReadStringList(payload, "automaticApproachBlockedHeroIds"), StringComparer.OrdinalIgnoreCase);
            blocked.UnionWith(wanderedIds ?? new List<string>());
            List<string> joined = new List<string>();
            foreach (Dictionary<string, object> result in results.Where(x => ReadBool(x, "ok", false) && SocialConversationResultAllowsEffects(x)
                && !string.IsNullOrWhiteSpace(ReadString(x, "reply", ""))))
            {
                if (active.Count >= SocialEventMaximumActive) break;
                List<Dictionary<string, object>> top = attendees
                    .Where(x => !active.Contains(CharacterIdFrom(x)) && !blocked.Contains(CharacterIdFrom(x)))
                    .Select(x =>
                    {
                        int boldness = SocialEventBoldness(campaignId, x);
                        int playerRelation = Math.Max(-100, Math.Min(100, ReadInt(x, "relationToPlayer", 0)));
                        Dictionary<string, object> score = SocialEventApproachScore(boldness, playerRelation);
                        return new Dictionary<string, object>
                        {
                            ["profile"] = x,
                            ["heroStringId"] = CharacterIdFrom(x),
                            ["boldness"] = boldness,
                            ["playerRelation"] = playerRelation,
                            ["relationshipModifier"] = ReadInt(score, "relationshipModifier", 0),
                            ["approachScore"] = ReadInt(score, "adjustedBoldness", boldness)
                        };
                    })
                    .OrderByDescending(x => ReadInt(x, "approachScore", 0))
                    .ThenBy(x => ReadString(x, "heroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
                if (top.Count == 0) break;
                Dictionary<string, object> selected = top[(RollCourtD100() - 1) % top.Count];
                Dictionary<string, object> profile = ReadDictionary(selected, "profile") ?? new Dictionary<string, object>();
                int threshold = SocialEventApproachThreshold(payload, profile);
                int adjustedBoldness = ReadInt(selected, "approachScore", ReadInt(selected, "boldness", 50));
                bool passed = ReadBool(EvaluateSocialEventJoin(adjustedBoldness, threshold, null, null), "passed", false);
                string id = ReadString(selected, "heroStringId", "");
                blocked.Add(id);
                if (passed)
                {
                    active.Add(id);
                    joined.Add(id);
                }
            }
            return joined;
        }

        private static int SocialEventBoldness(string campaignId, Dictionary<string, object> profile)
        {
            string heroId = CharacterIdFrom(profile);
            Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
            if (!TraitPercentageDocumentReady(traits)) traits = BuildTraitDocument(profile);
            else if (EnsureTraitPercentageData(traits, heroId)) WriteJsonObject(CharacterFile(campaignId, heroId, "traits.json"), traits);
            return Math.Max(0, Math.Min(100, ReadInt(ReadDictionary(traits, "courtVirtues") ?? new Dictionary<string, object>(), "boldness", 50)));
        }

        private static int SocialEventApproachThreshold(Dictionary<string, object> payload, Dictionary<string, object> profile)
        {
            bool oppositeSex = ReadBool(profile, "isFemale", false) != ReadBool(payload, "playerIsFemale", false);
            return IsSocialEventDancePhase(payload) && oppositeSex ? 40 : 60;
        }

        private static bool IsSocialEventDancePhase(Dictionary<string, object> payload)
        {
            string template = ReadString(payload, "templateId", "");
            string phase = ReadString(payload, "phaseId", "");
            return template.StartsWith("dance_", StringComparison.OrdinalIgnoreCase)
                && (phase.Equals("first_dance", StringComparison.OrdinalIgnoreCase)
                    || phase.Equals("refreshments_and_second_dance", StringComparison.OrdinalIgnoreCase));
        }

        private static bool CanSocialEventSpeakerInviteToDance(Dictionary<string, object> payload, Dictionary<string, object> request)
        {
            Dictionary<string, object> speaker = ReadDictionary(request, "speaker") ?? new Dictionary<string, object>();
            return !IsSocialEventDancePhase(payload)
                || ReadBool(speaker, "isFemale", false) != ReadBool(payload, "playerIsFemale", false);
        }

        private static List<Dictionary<string, object>> ApplySocialEventTurnRelationships(
            Dictionary<string, object> payload,
            List<Dictionary<string, object>> results,
            List<string> activeIds)
        {
            if (!ReadBool(payload, "conversationRelationshipChangesEnabled", true)) return new List<Dictionary<string, object>>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string turnId = ReadString(payload, "turnId", "");
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId");
            List<Dictionary<string, object>> assessments = new List<Dictionary<string, object>>();
            HashSet<string> priorSpeakers = new HashSet<string>(new[] { playerId }.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> participantNames = ReadDictionaryList(payload, "attendees")
                .Where(row => !string.IsNullOrWhiteSpace(CharacterIdFrom(row)))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => ReadString(group.First(), "name", ""), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> contributionText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(playerId)) contributionText[playerId] = ReadFirstString(payload, "playerText", "text", "message");
            foreach (Dictionary<string, object> result in results.Where(x => ReadBool(x, "ok", false) && SocialConversationResultAllowsEffects(x)))
            {
                string speakerId = ReadFirstString(result, "heroStringId", "heroId", "speakerHeroStringId");
                List<Dictionary<string, object>> speakerAssessments = ReadDictionaryList(result, "relationshipAssessments");
                Func<string, string, string, Dictionary<string, object>> routineFallback = (targetId, valence, summary) => new Dictionary<string, object>
                {
                    ["observerHeroStringId"] = speakerId,
                    ["targetHeroStringId"] = targetId,
                    ["valence"] = valence,
                    ["actKind"] = "routine_conversation",
                    ["severityTier"] = "routine",
                    ["confidence"] = 0.5d,
                    ["sourceTurnIds"] = new List<string> { turnId },
                    ["lieCheckId"] = "", ["benefitEventId"] = "", ["giftRecipientHeroStringId"] = "",
                    ["summary"] = summary,
                    ["importance"] = 0.25d, ["continuedUtility"] = 1d,
                    ["retainedAppreciation"] = 1d, ["coercionSeverity"] = 0d,
                    ["evidenceSourceIds"] = new List<string>()
                };
                if (!string.IsNullOrWhiteSpace(playerId)
                    && !speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(playerId, StringComparison.OrdinalIgnoreCase)))
                {
                    speakerAssessments.Add(routineFallback(playerId,
                        "neutral",
                        "The player assessment was omitted; no relationship change is inferred."));
                }
                string reactionTargetId = ReadFirstString(result, "reactionTargetHeroStringId", "reactionTargetId");
                if (!string.IsNullOrWhiteSpace(reactionTargetId)
                    && !reactionTargetId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && priorSpeakers.Contains(reactionTargetId)
                    && !speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(reactionTargetId, StringComparison.OrdinalIgnoreCase)))
                {
                    speakerAssessments.Add(routineFallback(reactionTargetId, "neutral",
                        "The speaker engaged this participant but omitted an assessment; no personal reaction is inferred."));
                }
                string replyText = ReadString(result, "reply", "");
                foreach (string namedPriorSpeaker in priorSpeakers.Where(id => !id.Equals(playerId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    if (speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(namedPriorSpeaker, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!participantNames.TryGetValue(namedPriorSpeaker, out string priorName) || string.IsNullOrWhiteSpace(priorName))
                        continue;
                    string priorFirstName = FirstName(priorName);
                    bool uniqueFirstName = priorFirstName.Length >= 4
                        && participantNames.Values.Count(name => FirstName(name).Equals(priorFirstName, StringComparison.OrdinalIgnoreCase)) == 1;
                    bool explicitlyNamed = ContainsWholePhrase(replyText, priorName)
                        || (uniqueFirstName && ContainsWholePhrase(replyText, priorFirstName));
                    if (!explicitlyNamed) continue;
                    speakerAssessments.Add(routineFallback(namedPriorSpeaker,
                        "neutral",
                        "Naming a participant does not establish a positive or negative relationship assessment."));
                }
                foreach (Dictionary<string, object> assessment in speakerAssessments)
                {
                    Dictionary<string, object> staged = new Dictionary<string, object>(assessment)
                    {
                        ["eligibleTargetIds"] = priorSpeakers.ToList()
                    };
                    string targetId = ReadFirstString(staged, "targetHeroStringId", "targetId");
                    if (string.IsNullOrWhiteSpace(ReadString(staged, "sourceText", ""))
                        && contributionText.TryGetValue(targetId, out string targetContribution))
                        staged["sourceText"] = LimitText(targetContribution, 4000);
                    assessments.Add(staged);
                }
                if (!string.IsNullOrWhiteSpace(speakerId))
                {
                    priorSpeakers.Add(speakerId);
                    contributionText[speakerId] = ReadString(result, "reply", "");
                }
            }
            Dictionary<string, object> evaluation = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["exchangeId"] = turnId, ["mode"] = "social_event",
                ["playerHeroStringId"] = playerId, ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                ["participants"] = MergeStringLists(activeIds, new[] { playerId }), ["assessments"] = assessments,
                ["relationshipPairs"] = ReadDictionaryList(payload, "relationshipPairs"),
                ["nativeReceiptProtocol"] = ReadString(payload, "nativeReceiptProtocol", ""),
                ["correlationId"] = ReadString(payload, "correlationId", "")
            });
            return ReadDictionaryList(evaluation, "nativeChanges").Select(change => new Dictionary<string, object>
            {
                ["receiptId"] = "conversation_native_" + PromptHash(turnId + "|" + ReadString(change, "subjectId", "") + "|" + ReadString(change, "targetId", "")).Substring(0, 24).ToLowerInvariant(),
                ["receiptIds"] = ReadStringList(change, "receiptIds"),
                ["subjectId"] = ReadString(change, "subjectId", ""), ["targetId"] = ReadString(change, "targetId", ""),
                ["nativeRelationDelta"] = ReadInt(change, "delta", 0), ["idempotent"] = ReadInt(evaluation, "idempotentCount", 0) > 0,
                ["ok"] = ReadBool(evaluation, "ok", false), ["showNotification"] = ReadBool(change, "showNotification", false)
            }).ToList();
        }

        private static string NormalizeSocialEventParticipation(Dictionary<string, object> parsed)
        {
            string value = ReadFirstString(parsed, "participation", "participationMode", "participation_mode").ToLowerInvariant();
            return value == "agree" || value == "disagree" || value == "quiet" ? value : "speak";
        }

        private static string NormalizeSocialEventReactionTarget(Dictionary<string, object> parsed, Dictionary<string, object> payload, string speakerId)
        {
            string target = ReadFirstString(parsed, "reactionTargetHeroStringId", "reaction_target_hero_string_id", "reactionTargetId");
            HashSet<string> allowed = new HashSet<string>(ReadStringList(payload, "activeHeroIds"), StringComparer.OrdinalIgnoreCase);
            string playerId = ReadFirstString(
                payload, "playerHeroStringId", "mainHeroStringId");
            allowed.Add(playerId);

            HashSet<string> priorSpeakerIds = new HashSet<string>(
                ReadDictionaryList(payload, "groupTurnResponses")
                    .Select(row => ReadFirstString(
                        row, "heroStringId", "heroId",
                        "speakerHeroStringId"))
                    .Where(id => !string.IsNullOrWhiteSpace(id)
                        && !id.Equals(
                            speakerId,
                            StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(target)
                && !target.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                && priorSpeakerIds.Contains(target))
                return target;

            string assessedPrior = ReadDictionaryList(
                    parsed, "relationshipAssessments")
                .Select(row => ReadFirstString(
                    row, "targetHeroStringId", "targetId"))
                .FirstOrDefault(id => priorSpeakerIds.Contains(id));
            if (!string.IsNullOrWhiteSpace(assessedPrior))
                return assessedPrior;

            string visibleReply = ReadFirstString(
                parsed, "reply", "response", "text", "content");
            Dictionary<string, string> priorNames =
                ReadDictionaryList(payload, "attendees")
                    .Where(row => priorSpeakerIds.Contains(
                        CharacterIdFrom(row)))
                    .GroupBy(
                        CharacterIdFrom,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => ReadString(
                            group.First(), "name", ""),
                        StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> prior in priorNames)
            {
                string priorFirstName = FirstName(prior.Value);
                bool uniqueFirstName = priorFirstName.Length >= 4
                    && priorNames.Values.Count(name =>
                        FirstName(name).Equals(
                            priorFirstName,
                            StringComparison.OrdinalIgnoreCase)) == 1;
                if ((!string.IsNullOrWhiteSpace(prior.Value)
                        && ContainsWholePhrase(visibleReply, prior.Value))
                    || (uniqueFirstName
                        && ContainsWholePhrase(
                            visibleReply, priorFirstName)))
                    return prior.Key;
            }
            if (!string.IsNullOrWhiteSpace(target)
                && !target.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                && allowed.Contains(target))
                return target;
            return "";
        }

        private static bool IsForbiddenDanceInvitation(string intent, string reply)
        {
            string text = ((intent ?? "") + " " + (reply ?? "")).ToLowerInvariant();
            return text.Contains("invite_player_to_dance")
                || text.Contains("dance with me")
                || text.Contains("have this dance")
                || text.Contains("join me for the dance")
                || text.Contains("ask you to dance");
        }

    }
}
