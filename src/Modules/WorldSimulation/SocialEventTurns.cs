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
        private const int SocialEventIgnoredTurnLimit = 3;
        // Bounded stripes serialize retries of the same turn without retaining a
        // lock object for every conversation ever seen by the process.
        private static readonly object[] SocialEventTurnLocks =
            Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

        private static Dictionary<string, object> SocialEventTurnApi(Dictionary<string, object> payload)
        {
            return SocialEventTurnWithResponder(payload, SocialEventRespond);
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
            Dictionary<string, object> cached = ReadJsonObject(cachePath);
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

            bool hasPlan = ReadStringList(cached, "responseOrder").Count > 0;
            List<string> addressedIds = hasPlan ? ReadStringList(cached, "addressedHeroStringIds")
                : ResolveSocialEventAddressedHeroes(payload, activeProfiles);
            List<string> responseOrder = hasPlan ? ReadStringList(cached, "responseOrder")
                : BuildSocialEventResponseOrder(payload, activeProfiles, addressedIds);
            Dictionary<string, object> incomingIdle = ReadDictionary(payload, "idleCounters") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> nextIdle = hasPlan ? ReadDictionary(cached, "nextIdleCounters")
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<string> wanderedIds = hasPlan ? ReadStringList(cached, "wanderedHeroStringIds") : new List<string>();
            foreach (string heroId in hasPlan ? new List<string>() : responseOrder)
            {
                int idle = addressedIds.Contains(heroId, StringComparer.OrdinalIgnoreCase) ? 0 : ReadInt(incomingIdle, heroId, 0) + 1;
                nextIdle[heroId] = idle;
                if (idle >= SocialEventIgnoredTurnLimit)
                {
                    wanderedIds.Add(heroId);
                }
            }

            EnsureSocialTurnPlayerTranscript(campaignId, eventId, turnId, payload, activeProfiles);

            cached = new Dictionary<string, object>
            {
                ["status"] = "in_progress", ["campaignId"] = campaignId,
                ["eventId"] = eventId, ["turnId"] = turnId,
                ["responseOrder"] = responseOrder, ["addressedHeroStringIds"] = addressedIds,
                ["nextIdleCounters"] = nextIdle, ["wanderedHeroStringIds"] = wanderedIds,
                ["participantResults"] = results
            };
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

                Dictionary<string, object> speakerPayload = new Dictionary<string, object>(request, StringComparer.OrdinalIgnoreCase)
                {
                    ["turnId"] = turnId,
                    ["suppressPlayerTranscript"] = true,
                    ["activeHeroIds"] = activeIds,
                    ["groupTurnResponses"] = results.Select(CompactSocialEventTurnResult).ToList(),
                    ["groupSpeakerIndex"] = results.Count,
                    ["mustAccountForPriorSpeaker"] = results.Count > 0,
                    ["isDancePhase"] = IsSocialEventDancePhase(payload),
                    ["canInvitePlayerToDance"] = CanSocialEventSpeakerInviteToDance(payload, request)
                };
                if (results.Count > 0)
                {
                    speakerPayload["sceneContext"] =
                        ReadString(speakerPayload, "sceneContext", "")
                        + "\nCURRENT GROUP BEAT: One or more attributed NPC contributions are already supplied in groupTurnResponses. "
                        + "Account for at least one relevant earlier contribution while still answering the player. "
                        + "If you engage it, identify that prior speaker in reactionTargetHeroStringId; never transfer another speaker's words or private knowledge to yourself.";
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
                ["participantResults"] = results,
                ["joinedHeroStringIds"] = joinedIds,
                ["wanderedHeroStringIds"] = wanderedIds,
                ["nextIdleCounters"] = nextIdle,
                ["relationshipReceipts"] = relationshipReceipts,
                ["completedExchange"] = true
            };
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

        private static List<string> ResolveSocialEventAddressedHeroes(Dictionary<string, object> payload, List<Dictionary<string, object>> activeProfiles)
        {
            List<string> allIds = activeProfiles.Select(CharacterIdFrom).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (allIds.Count <= 1)
            {
                return allIds;
            }

            string playerText = ReadString(payload, "playerText", "");
            string lower = playerText.ToLowerInvariant();
            string[] groupPhrases = { "everyone", "all of you", "you all", "each of you", "each person", "my lords", "my ladies", "ladies and gentlemen", "friends", "all here", "the whole group" };
            bool groupAddressed = groupPhrases.Any(lower.Contains);

            List<KeyValuePair<string, int>> named = new List<KeyValuePair<string, int>>();
            Dictionary<string, int> firstNameCounts = activeProfiles
                .Select(x => FirstName(ReadString(x, "name", "")))
                .Where(x => x.Length >= 3)
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> profile in activeProfiles)
            {
                string id = CharacterIdFrom(profile);
                string name = ReadString(profile, "name", "").Trim();
                string first = FirstName(name);
                bool fullMatch = ContainsWholePhrase(playerText, name);
                bool uniqueFirstMatch = first.Length >= 3 && firstNameCounts.ContainsKey(first) && firstNameCounts[first] == 1 && ContainsWholePhrase(playerText, first);
                if (fullMatch || uniqueFirstMatch)
                {
                    int fullIndex = fullMatch ? playerText.IndexOf(name, StringComparison.OrdinalIgnoreCase) : int.MaxValue;
                    int firstIndex = uniqueFirstMatch ? playerText.IndexOf(first, StringComparison.OrdinalIgnoreCase) : int.MaxValue;
                    named.Add(new KeyValuePair<string, int>(id, Math.Min(fullIndex < 0 ? int.MaxValue : fullIndex, firstIndex < 0 ? int.MaxValue : firstIndex)));
                }
            }
            if (named.Count > 0)
            {
                List<string> orderedNamed = named.OrderBy(match => match.Value)
                    .ThenBy(match => match.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(match => match.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return groupAddressed
                    ? orderedNamed.Concat(allIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : orderedNamed;
            }

            if (groupAddressed) return allIds;

            foreach (string line in ReadStringList(payload, "transcript").AsEnumerable().Reverse())
            {
                if (!line.Contains("?"))
                {
                    continue;
                }
                Dictionary<string, object> asker = activeProfiles.FirstOrDefault(x => line.StartsWith(ReadString(x, "name", "") + ":", StringComparison.OrdinalIgnoreCase));
                if (asker != null)
                {
                    return new List<string> { CharacterIdFrom(asker) };
                }
            }

            Dictionary<string, object> semantic = ResolveSocialEventSemanticFocus(playerText, payload, activeProfiles);
            double confidence = ReadDouble(semantic, "confidence", 0d);
            if (ReadBool(semantic, "groupAddressed", false) || confidence < 0.65d)
            {
                return allIds;
            }
            HashSet<string> allowed = new HashSet<string>(allIds, StringComparer.OrdinalIgnoreCase);
            List<string> semanticIds = ReadStringList(semantic, "addressedHeroStringIds").Where(allowed.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return semanticIds.Count == 0 ? allIds : semanticIds;
        }

        private static List<string> BuildSocialEventResponseOrder(
            Dictionary<string, object> payload, List<Dictionary<string, object>> activeProfiles,
            List<string> resolvedAddressed = null)
        {
            activeProfiles = activeProfiles ?? new List<Dictionary<string, object>>();
            List<string> allIds = activeProfiles.Select(CharacterIdFrom)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> addressed = (resolvedAddressed ?? ResolveSocialEventAddressedHeroes(payload, activeProfiles))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string playerText = ReadString(payload, "playerText", "");
            Dictionary<string, int> firstNameCounts = activeProfiles
                .Select(profile => FirstName(ReadString(profile, "name", "")))
                .Where(name => name.Length >= 3)
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            string explicitlyNamedFirst = activeProfiles
                .Select(profile =>
                {
                    string id = CharacterIdFrom(profile);
                    string name = ReadString(profile, "name", "").Trim();
                    string first = FirstName(name);
                    int fullIndex = ContainsWholePhrase(playerText, name)
                        ? playerText.IndexOf(name, StringComparison.OrdinalIgnoreCase)
                        : int.MaxValue;
                    int firstIndex = first.Length >= 3
                        && firstNameCounts.TryGetValue(first, out int count)
                        && count == 1
                        && ContainsWholePhrase(playerText, first)
                            ? playerText.IndexOf(first, StringComparison.OrdinalIgnoreCase)
                            : int.MaxValue;
                    return new { Id = id, Index = Math.Min(fullIndex < 0 ? int.MaxValue : fullIndex, firstIndex < 0 ? int.MaxValue : firstIndex) };
                })
                .Where(match => !string.IsNullOrWhiteSpace(match.Id) && match.Index != int.MaxValue)
                .OrderBy(match => match.Index)
                .ThenBy(match => match.Id, StringComparer.OrdinalIgnoreCase)
                .Select(match => match.Id)
                .FirstOrDefault();
            string direct = !string.IsNullOrWhiteSpace(explicitlyNamedFirst)
                ? explicitlyNamedFirst
                : addressed.Count < allIds.Count ? addressed.FirstOrDefault() : null;
            string seed = ReadString(payload, "turnId", ReadString(payload, "sceneTurnId", "social_event_turn"));
            List<string> shuffled = allIds
                .Where(id => string.IsNullOrWhiteSpace(direct) || !id.Equals(direct, StringComparison.OrdinalIgnoreCase))
                .OrderBy(id => PromptHash(seed + "|response-order|" + id), StringComparer.Ordinal)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(direct)) shuffled.Insert(0, direct);
            return shuffled;
        }

        private static Dictionary<string, object> ResolveSocialEventSemanticFocus(string playerText, Dictionary<string, object> payload, List<Dictionary<string, object>> activeProfiles)
        {
            Dictionary<string, object> llm = ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "social_event_focus",
                ["maxTokens"] = 300,
                ["temperature"] = 0d,
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = "Resolve who the player directly addresses in a group conversation. Return strict JSON only. Do not infer focus from status or preference." },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = "Active participants: " + Json.Serialize(activeProfiles.Select(x => new Dictionary<string, object> { ["heroStringId"] = CharacterIdFrom(x), ["name"] = ReadString(x, "name", "") }).ToList()) + "\nRecent transcript: " + Json.Serialize(ReadStringList(payload, "transcript").Skip(Math.Max(0, ReadStringList(payload, "transcript").Count - 12)).ToList()) + "\nPlayer message: " + playerText + "\nReturn {addressedHeroStringIds:[],groupAddressed:false,confidence:0.0}. A semantic reply to one person's statement or question addresses that person. General remarks address the group." }
                },
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });
            return ReadBool(llm, "ok", false)
                ? TryParseJsonObject(ReadString(llm, "content", "")) ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
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
                "construction"
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
                ["reply"] = LimitText(ReadString(result, "reply", ""), 1000)
            };
        }

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
            foreach (Dictionary<string, object> result in results.Where(x => ReadBool(x, "ok", false) && !string.IsNullOrWhiteSpace(ReadString(x, "reply", ""))))
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
            foreach (Dictionary<string, object> result in results.Where(x => ReadBool(x, "ok", false)))
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
                        ConversationValenceFromSignal(ReadString(result, "relationshipSignal", ""), "positive"),
                        "Routine reaction toward the player was required but omitted from the model's private classifications."));
                }
                string reactionTargetId = ReadFirstString(result, "reactionTargetHeroStringId", "reactionTargetId");
                if (!string.IsNullOrWhiteSpace(reactionTargetId)
                    && !reactionTargetId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && priorSpeakers.Contains(reactionTargetId)
                    && !speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(reactionTargetId, StringComparison.OrdinalIgnoreCase)))
                {
                    string participation = ReadString(result, "participation", "speak").ToLowerInvariant();
                    string reactionValence = participation == "disagree" ? "negative" : "positive";
                    speakerAssessments.Add(routineFallback(reactionTargetId, reactionValence,
                        "The speaker directly engaged this prior participant; a routine directional classification was supplied because the model omitted it."));
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
                        InferNamedNpcReactionValence(result, replyText),
                        "The speaker explicitly named and engaged this prior participant; a routine directional classification was supplied because the model omitted it."));
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

        private static string InferNamedNpcReactionValence(Dictionary<string, object> result, string replyText)
        {
            string participation = ReadString(result, "participation", "speak").ToLowerInvariant();
            if (participation == "disagree" || participation == "oppose" || participation == "reject") return "negative";
            if (participation == "agree" || participation == "support") return "positive";
            if (ContainsAny(replyText ?? "", "left out", "is wrong", "was wrong", "mistaken", "I disagree", "do not agree",
                "doesn't understand", "does not understand", "fails to", "failed to", "not enough"))
                return "negative";
            if (ContainsAny(replyText ?? "", "is right", "was right", "I agree", "fair point", "good point", "sound point",
                "correctly", "is correct", "was correct"))
                return "positive";
            return "positive";
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
