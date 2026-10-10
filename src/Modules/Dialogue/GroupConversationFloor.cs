using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Provider-free ordering and retry binding only. Every present NPC gets
        // a contextual writer opportunity; personality and scene context guide
        // the writer's speech-or-quiet decision, never a mechanical prefilter.
        private static Dictionary<string, object> GroupConversationFloorApi(Dictionary<string, object> payload)
        {
            string contribution = ReadString(payload, "contributionId", "");
            if (contribution.Length > 0) payload["sceneTurnId"] = contribution;
            var plan = ResolveGroupConversationFloor(payload);
            if (!ReadBool(plan, "ok", false)) return plan;
            payload["conversationSessionId"] = ReadString(plan, "sessionId", "");
            if (!PlayerInputSources.TryGetValue(payload, out var captured) || captured.InputId.Length == 0)
            {
                string campaign = ReadString(plan, "campaignId", ""), generation;
                using (var connection = OpenCampaignConnection(campaign))
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                AdmitConversationInput(campaign, payload, generation);
            }
            // Submitted player speech survives even if everybody listens. No NPC
            // line, summary, relationship score or provider request is invented.
            var source = CloneDictionary(payload);
            CopyPlayerInputSource(payload, source);
            ApplyPlayerInputAudienceToPayload(source, "", allowInterpretation: false);
            string publicText = ReadString(source, "publicPlayerText", ReadString(source, "playerText", ""));
            if (!string.IsNullOrWhiteSpace(publicText) && !ReadBool(payload, "suppressPlayerTranscript", false))
            {
                string session = ReadString(plan, "sessionId", ""), beat = ReadString(plan, "turnId", "");
                source["conversationSessionId"] = session;
                string mode = ReadString(payload, "mode", "party_chat");
                string channel = mode.Contains("event", StringComparison.OrdinalIgnoreCase) ? mode : "party_chat";
                var stored = StoreGroupedConversationExchange(ReadString(plan, "campaignId", ""), source,
                    ReadStringList(plan, "audienceHeroStringIds").First(),
                    ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                    ReadString(payload, "playerName", "Player"), "", publicText, "",
                    "group_input_" + PromptHash(ReadString(plan, "timelineId", "") + "|" + session + "|" + beat).Substring(0, 24),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), channel, session, floorProjection: true);
                if (!ReadBool(stored, "ok", false)) return stored;
            }
            if (payload.TryGetValue("inputReceipt", out var receipt)) plan["inputReceipt"] = receipt;
            return plan;
        }

        private static Dictionary<string, object> ResolveGroupConversationFloor(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "");
            string session = GroupConversationSessionId(payload);
            string beat = ReadFirstString(payload, "sceneTurnId", "turnId", "exchangeId");
            string timeline = ContinuityTimeline(payload);
            string text = ReadString(payload, "playerText", "");
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            var profiles = ReadDictionaryList(payload, "attendees");
            var active = ReadStringList(payload, "activeHeroIds").Where(id => id.Length > 0 && id != player)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (campaign.Length == 0 || session.Length == 0 || beat.Length == 0 || active.Count == 0 || active.Count > 64)
                return TestDict("ok", false, "error", "A group speaking turn requires a campaign, session, turn and bounded audience.");
            if (active.Any(id => !profiles.Any(p => CharacterIdFrom(p).Equals(id, StringComparison.OrdinalIgnoreCase))))
                return TestDict("ok", false, "error", "A present group participant has no profile.");

            bool opening = ReadBool(payload, "castleOpening", false) || ReadBool(payload, "approachOpening", false);
            string actionRecipientId = ReadString(payload, "structuredActionRecipientId", "");
            if (actionRecipientId.Length > 0 && !active.Contains(actionRecipientId, StringComparer.Ordinal))
                return TestDict("ok", false, "error", "The structured action recipient is outside this conversation.");
            var lines = GroupFloorTranscript(payload, profiles);
            string replyFocusId = WildernessConversationReplyFocus(payload, profiles, active, lines);
            string signature = PromptHash(CanonicalJson(TestDict("session", session, "beat", beat, "timeline", timeline,
                "player", player, "text", text, "opening", opening, "approachRequestId", ReadString(payload, "approachRequestId", ""), "replyFocusId", replyFocusId, "actionRecipientId", actionRecipientId, "audience", active.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray())));
            double day = ReadDouble(payload, "worldDay", 0);
            using var connection = OpenCampaignConnection(campaign);
            using var transaction = connection.BeginTransaction();
            ExecuteSql(connection, "SELECT pg_advisory_xact_lock(hashtextextended($key,0));",
                TestDict("key", "group_state|" + campaign + "|" + timeline + "|" + session));
            var row = QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id=$id AND timeline_id=$timeline AND world_day<=$day;",
                TestDict("id", session, "timeline", timeline, "day", day)).FirstOrDefault();
            var state = TryParseJsonObject(ReadString(row, "state_json", "{}"));
            var plans = ReadDictionaryList(state, "speakingFloorPlans");
            var existing = plans.FirstOrDefault(p => ReadString(p, "turnId", "") == beat);
            if (existing != null)
            {
                if (ReadString(existing, "inputHash", "") != signature)
                    return TestDict("ok", false, "error", "This group turn already has a different speaking plan input.");
                transaction.Commit();
                var replay = CloneDictionary(existing); replay["replayed"] = true;
                return replay;
            }
            // Never reroll an old delivered turn after its small plan cache expires.
            if (QuerySql(connection, "SELECT contribution_id FROM group_conversation_contributions WHERE session_id=$session AND timeline_id=$timeline AND scene_turn_id=$beat LIMIT 1;",
                TestDict("session", session, "timeline", timeline, "beat", beat)).Count > 0
                || QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$session AND exchange_id=$beat AND status='active' LIMIT 1;",
                    TestDict("session", session, "beat", beat)).Count > 0)
                return TestDict("ok", false, "error", "The speaking plan for this already delivered turn is unavailable; resume with a new player turn.");

            var roster = new List<GroupTurnParticipant>();
            string publicText = ProjectPlayerInputForAudience(text, "", profiles, out _, out _);
            publicText = ObserverConversationText(publicText, "__player_source__", "");
            foreach (string id in active)
            {
                var profile = profiles.First(p => CharacterIdFrom(p).Equals(id, StringComparison.OrdinalIgnoreCase));
                roster.Add(new GroupTurnParticipant { Id = id, Name = ReadString(profile, "name", "") });
            }
            var selected = GroupConversationTurnOrder.Plan(roster, publicText, campaign + "|" + timeline + "|" + session + "|" + beat, lines, replyFocusId);
            foreach (string id in active)
            {
                ProjectPlayerInputForAudience(text, id, profiles, out _, out bool privatelyAddressed);
                var actionInput = CloneDictionary(payload);
                actionInput["hero"] = profiles.First(p => CharacterIdFrom(p).Equals(id, StringComparison.OrdinalIgnoreCase));
                // The existing exact current handover parser identifies an action
                // recipient. It is a local read; selection executes no transfer.
                bool actionRecipient = id == actionRecipientId || CurrentPlayerTransfers(actionInput).Count > 0;
                if (!privatelyAddressed && !actionRecipient) continue;
                if (!selected.AddressedIds.Contains(id)) selected.AddressedIds.Add(id);
                selected.ResponseOrder.Remove(id);
                selected.ResponseOrder.Insert(0, id);
            }
            string approachId = ReadString(payload, "approachRequestId", "");
            if (opening && approachId.Length > 0)
            {
                EnsurePlayerApproachSchema(connection);
                var invitation = ApproachState(connection, timeline, "request:" + approachId);
                var invited = ReadDictionaryList(invitation, "heroes").Select(h => ReadString(h, "heroId", "")).ToList();
                if (ReadString(invitation, "status", "") != "accepted" || ReadString(invitation, "playerId", "") != player
                    || invited.Count != active.Count || invited.Except(active).Any())
                    return TestDict("ok", false, "error", "The group does not match its accepted keep invitation.");
                selected.ResponseOrder.Clear(); selected.ResponseOrder.AddRange(invited);
            }
            var plan = TestDict("ok", true, "schema", "reign-group-speaking-floor-v1", "policy", "llm_contextual",
                "campaignId", campaign, "sessionId", session, "turnId", beat, "timelineId", timeline,
                "inputHash", signature, "worldDay", day, "responseOrder", selected.ResponseOrder,
                "replyFocusHeroStringId", replyFocusId, "structuredActionRecipientId", actionRecipientId,
                "addressedHeroStringIds", selected.AddressedIds, "audienceHeroStringIds", active,
                "providerCalls", 0, "replayed", false);
            plans.Add(plan);
            state["speakingFloorPlans"] = plans.TakeLast(32).ToList();
            ExecuteSql(connection, @"INSERT INTO group_conversation_state
                (session_id,campaign_id,mode,participant_ids_json,state_json,revision,updated_ts,timeline_id,world_day)
                VALUES($session,$campaign,$mode,$participants,$state,1,$ts,$timeline,$day)
                ON CONFLICT(session_id) DO UPDATE SET state_json=$state,participant_ids_json=$participants,
                revision=group_conversation_state.revision+1,updated_ts=$ts,timeline_id=$timeline,world_day=$day;",
                TestDict("session", session, "campaign", campaign, "mode", ReadString(payload, "mode", "party_chat"),
                    "participants", Json.Serialize(active.Concat(new[] { player }).Where(id => id.Length > 0).Distinct()),
                    "state", Json.Serialize(state), "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "timeline", timeline, "day", day));
            transaction.Commit();
            return plan;
        }

        private static Dictionary<string, object> ApplyGroupConversationFloorToSpeaker(Dictionary<string, object> payload)
        {
            if (ReadStringList(payload, "activeHeroIds").Count == 0) return null;
            var plan = GroupConversationFloorApi(payload);
            if (!ReadBool(plan, "ok", false)) return plan;
            var order = ReadStringList(plan, "responseOrder");
            string speaker = ReadFirstString(payload, "speakerHeroStringId", "heroStringId");
            payload["groupTurnPlan"] = TestDict("responseOrder", order,
                "addressedHeroStringIds", ReadStringList(plan, "addressedHeroStringIds"),
                "policy", ReadString(plan, "policy", ""));
            payload["partySpeakerIndex"] = order.IndexOf(speaker);
            payload["partySpeakerCount"] = order.Count;
            return order.Contains(speaker, StringComparer.OrdinalIgnoreCase) ? null
                : TestDict("ok", false, "error", "This speaker has no response opportunity in the retained turn. Submit a new turn with the current audience.");
        }

        private static List<GroupTurnLine> GroupFloorTranscript(Dictionary<string, object> payload, List<Dictionary<string, object>> profiles)
        {
            var structured = ReadDictionaryList(payload, "groupTranscript");
            if (structured.Count > 0)
                return structured.Select(line => new GroupTurnLine { SpeakerId = ReadFirstString(line, "speakerHeroStringId", "speakerId"),
                    Role = ReadString(line, "role", ""), Text = ReadString(line, "text", ""),
                    AudienceId = ReadFirstString(line, "audienceHeroStringId", "privateAudienceHeroStringId") }).ToList();
            return ReadStringList(payload, "transcript").Select(text => {
                var matches = profiles.Where(p => ReadString(p, "name", "").Length > 0
                    && text.StartsWith(ReadString(p, "name", "") + ":", StringComparison.OrdinalIgnoreCase)).ToList();
                int colon = text.IndexOf(':');
                return new GroupTurnLine { SpeakerId = matches.Count == 1 ? CharacterIdFrom(matches[0]) : "",
                    Role = matches.Count == 1 ? "npc" : "player", Text = colon < 0 ? text : text.Substring(colon + 1).Trim() };
            }).ToList();
        }

        private static string WildernessConversationReplyFocus(Dictionary<string, object> payload,
            List<Dictionary<string, object>> profiles, List<string> active, List<GroupTurnLine> lines)
        {
            if (ReadString(payload, "templateId", "") != "generated_wilderness") return "";
            var previous = lines.ToList();
            if (previous.Count > 0 && previous.Last().Role == "player"
                && previous.Last().Text.Trim() == ReadString(payload, "playerText", "").Trim()) previous.RemoveAt(previous.Count - 1);
            var last = previous.LastOrDefault();
            if (last != null && last.Role == "npc" && last.AudienceId.Length == 0 && active.Contains(last.SpeakerId)) return last.SpeakerId;
            // Before the first actual NPC reply, the incident's initiator owns the
            // invitation. Do not search past a later player boundary for old speech.
            if (previous.Any(line => line.Role == "npc")) return "";
            string initiator = ReadString(payload, "wildernessInitiatorHeroStringId", "");
            if (active.Contains(initiator)) return initiator;
            // Old saves have no field 28. The first named person in the visible
            // approach is a bounded compatibility cue, never private hidden context.
            string approach = ReadString(payload, "wildernessApproachText", "");
            var matches = profiles.Where(p => active.Contains(CharacterIdFrom(p)))
                .Select(p => new { Id = CharacterIdFrom(p), Name = ReadString(p, "name", "") })
                .Where(p => p.Name.Length > 0)
                .Select(p => new { p.Id, Match = System.Text.RegularExpressions.Regex.Match(approach,
                    @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(p.Name) + @"(?![\p{L}\p{N}])", System.Text.RegularExpressions.RegexOptions.IgnoreCase) })
                .Where(p => p.Match.Success).OrderBy(p => p.Match.Index).ToList();
            return matches.Count > 0 && (matches.Count == 1 || matches[0].Match.Index < matches[1].Match.Index) ? matches[0].Id : "";
        }
    }
}
