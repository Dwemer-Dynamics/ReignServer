using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Operator-only evidence and prerequisites. These records never enter an NPC prompt.
        // The native heartbeat keeps a live, exact conversation/probe lease valid while its
        // production provider runs. It cannot resurrect an expired lease or extend
        // the command's original bounded execution deadline.
        private static bool TryRenewPersonalGossipConversationLease(Dictionary<string, object> run,
            Dictionary<string, object> runtime, DateTimeOffset now, bool armed)
        {
            if (!armed || run == null || runtime == null || ReadString(run, "mode", "") != "personal_gossip"
                || ReadString(run, "status", "") != "running" || !ReadBool(runtime, "busy", false)
                || ReadString(runtime, "activeRunId", "") != ReadString(run, "runId", "")
                || !new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "personal_gossip" }.Contains(ReadString(runtime, "activeMode", ""))
                || ReadString(runtime, "gameInstanceId", "") != ReadString(run, "gameInstanceId", "")
                || ReadString(runtime, "campaignId", "") != ReadString(run, "campaignId", "")
                || ParseLiveTestUtc(ReadString(runtime, "receivedUtc", "")) < now.AddSeconds(-LiveTestHeartbeatFreshSeconds)) return false;
            var enrollment = ReadDictionary(run, "enrollment");
            var sync = ReadDictionary(runtime, "saveSync");
            if (!PersonalGossipEnrollmentValid(enrollment, ReadString(run, "campaignId", ""))
                || ReadString(runtime, "activeSaveName", "") != ReadString(enrollment, "disposableSaveName", "")
                || !ReadBool(sync, "ready", false) || ReadBool(sync, "alignmentPending", true)
                || ReadString(sync, "timelineId", "") != ReadString(enrollment, "timelineId", "")
                || ReadBool(ReadDictionary(runtime, "nativeSave"), "isSaving", true)) return false;
            var command = FirstLiveTestCommandInSequence(run);
            if (command == null || ReadString(command, "operation", "") != "personal_gossip_test"
                || !(ReadString(ReadDictionary(command, "testCase"), "phase", "") == "conversation"
                        && new[] { "individual_chat", "party_chat", "social_event", "wilderness_event" }.Contains(ReadString(runtime, "activeMode", ""))
                    || ReadString(ReadDictionary(command, "testCase"), "phase", "") == "composition_probe"
                        && ReadString(runtime, "activeMode", "") == "personal_gossip"
                        && ReadBool(enrollment, "allowProviderCalls", false))
                || !new[] { "accepted", "running" }.Contains(ReadString(command, "status", ""))
                || CanonicalJson(ReadDictionary(command, "enrollment")) != CanonicalJson(enrollment)
                || ReadString(command, "leaseId", "").Length == 0) return false;
            DateTimeOffset expiry = ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", ""));
            DateTimeOffset started = ParseLiveTestUtc(ReadString(command, "leaseStartedUtc", ""));
            int timeout = ReadInt(command, "timeoutSeconds", 0);
            if (expiry <= now || expiry > now.AddSeconds(20) || started == DateTimeOffset.MinValue
                || started > now || timeout < 1 || timeout > 1800) return false;
            DateTimeOffset deadline = started.AddSeconds(timeout);
            DateTimeOffset renewed = now.AddSeconds(LiveTestLeaseSeconds);
            if (renewed > deadline) renewed = deadline;
            if (renewed <= expiry) return false;
            command["leaseExpiresUtc"] = renewed.ToString("o");
            command["leaseRenewedUtc"] = now.ToString("o");
            return true;
        }

        private static bool GossipTestAuthorized(Dictionary<string, object> p)
        {
            string campaign = ReadString(p, "campaignId", ""), instance = ReadString(p, "gameInstanceId", "");
            var run = LoadLiveTestRun(campaign, ReadString(p, "runId", ""));
            var runtime = LoadLiveTestRuntime(campaign); var enrollment = ReadDictionary(run, "enrollment");
            var command = ReadDictionaryList(run, "commands").SingleOrDefault(x => ReadString(x, "commandId", "") == ReadString(p, "commandId", ""));
            var sync = ReadDictionary(runtime, "saveSync");
            return campaign.Length > 0 && instance.Length > 0 && command != null
                && IsLiveTestArmed(ReadJsonObject(LiveTestArmPath())) && IsFreshLiveTestRuntime(runtime)
                && PersonalGossipEnrollmentValid(enrollment, campaign) && ReadString(run, "mode", "") == "personal_gossip"
                && new[] { "queued", "running" }.Contains(ReadString(run, "status", ""))
                && ReadString(run, "campaignId", "") == campaign && ReadString(runtime, "campaignId", "") == campaign
                && ReadString(run, "gameInstanceId", "") == instance && ReadString(runtime, "gameInstanceId", "") == instance
                && ReadString(runtime, "activeSaveName", "") == ReadString(enrollment, "disposableSaveName", "")
                && ReadString(p, "activeSaveName", "") == ReadString(enrollment, "disposableSaveName", "")
                && ReadString(p, "timelineId", "") == ReadString(enrollment, "timelineId", "")
                && ReadBool(sync, "ready", false) && !ReadBool(sync, "alignmentPending", true)
                && ReadString(sync, "timelineId", "") == ReadString(enrollment, "timelineId", "")
                && !ReadBool(ReadDictionary(runtime, "nativeSave"), "isSaving", true)
                && ReadString(p, "timeMode", "") == "Stop" && !ReadBool(p, "nativeSaveInProgress", true)
                && ReadString(command, "operation", "") == "personal_gossip_test"
                && new[] { "leased", "accepted", "running" }.Contains(ReadString(command, "status", ""))
                && ReadString(p, "leaseId", "").Length > 0 && ReadString(command, "leaseId", "") == ReadString(p, "leaseId", "")
                && ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) > DateTimeOffset.UtcNow
                && ReadDictionary(p, "testCase")?.Count > 0
                && CanonicalJson(ReadDictionary(p, "testCase")) == CanonicalJson(ReadDictionary(command, "testCase"));
        }

        private static void EnsureGossipTestSchema(ReignDbConnection c) => ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS personal_gossip_test_fixtures (
fixture_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,speaker_id TEXT NOT NULL,marker TEXT NOT NULL,
created_utc TEXT NOT NULL,consumed_topic TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL);");

        private static Dictionary<string, object> PersonalGossipTestApi(Dictionary<string, object> p)
        {
            if (!GossipTestAuthorized(p)) throw new InvalidOperationException("An exact armed gossip command lease, paused Current and aligned recovery checkpoint are required.");
            string campaign = ReadString(p, "campaignId", ""), timeline = ReadString(p, "timelineId", "");
            var test = ReadDictionary(p, "testCase"); string phase = ReadString(test, "phase", "");
            ValidatePersonalGossipTestCase(test);
            var run = LoadLiveTestRun(campaign, ReadString(p, "runId", ""));
            if (new[] { "conversation", "tick", "composition_probe" }.Contains(phase) && !ReadBool(ReadDictionary(run, "enrollment"), "allowProviderCalls", false))
                throw new InvalidOperationException("This phase needs explicit provider authorization.");
            var presence = ReadDictionary(p, "gossipPresence"); var people = ReadDictionaryList(presence, "people");
            if (ReadString(presence, "schema", "") != "reign-gossip-presence-v1" || !ReadBool(presence, "rosterComplete", false)
                || people.Count > 8192 || ReadString(presence, "timelineId", "") != timeline
                || !double.IsFinite(ReadDouble(p, "worldDay", double.NaN))
                || Math.Abs(ReadDouble(presence, "worldDay", -1) - ReadDouble(p, "worldDay", -2)) > 1d / 1440)
                throw new InvalidOperationException("A complete current native roster is required.");
            if (!new[] { "catalog", "snapshot", "presence_contract", "prepare", "policy_probe", "tick", "conversation", "composition_probe" }.Contains(phase))
                throw new ArgumentException("Unknown gossip test phase.");
            if (phase == "composition_probe") return PersonalGossipCompositionProbe(campaign, timeline, p, test);
            using (EnterCampaignWorkLock(GossipGate(campaign)))
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePersonalGossipSchema(c); EnsureMbtiRelationshipSchema(c);
                if (ActiveWorldHistoryTimeline(c) != timeline) throw new InvalidOperationException("The active timeline changed.");
                var selected = ReadStringList(test, "heroIds");
                string actor = ReadString(test, "actorHeroId", "");
                if (actor.Length > 0 && !selected.Contains(actor)) selected.Add(actor);
                selected = selected.Concat(ReadStringList(test, "participantHeroIds")).Distinct(StringComparer.Ordinal).ToList();
                string search = ReadString(test, "search", "");
                int offset = Clamp(ReadInt(test, "offset", 0), 0, 8192), limit = Clamp(ReadInt(test, "limit", 50), 1, 100);
                var roster = people.Where(h => (selected.Count == 0 || selected.Contains(GossipHeroId(h)))
                    && (search.Length == 0 || ReadString(h, "name", "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                        || GossipHeroId(h).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(GossipHeroId, StringComparer.Ordinal).ToList();
                var result = GossipFields("ok", true, "schema", "reign-personal-gossip-native-evidence-v1",
                    "origin", phase == "policy_probe" ? "operator_policy_probe" : "operator_native_observation",
                    "caseId", ReadString(test, "caseId", ""), "phase", phase, "campaignId", campaign, "timelineId", timeline,
                    "worldDay", ReadDouble(p, "worldDay", 0), "activeSaveName", ReadString(p, "activeSaveName", ""),
                    "rosterComplete", true, "rosterTotal", roster.Count, "offset", offset, "limit", limit,
                    "people", roster.Skip(offset).Take(limit).ToList(), "policy", PersonalGossipSettings());
                if (phase == "conversation") result["conversationScheduling"] = PersonalGossipConversationScheduling(c, timeline,
                    ReadDouble(p, "worldDay", 0), selected);
                if (phase == "presence_contract") result["productionPresenceGuard"] = GossipProductionPresenceContract(c, p);
                if (phase == "prepare") PreparePersonalGossipTest(c, campaign, p, test, people, result);
                if (phase == "policy_probe")
                {
                    var self = people.Single(h => GossipHeroId(h) == actor);
                    var main = people.Single(h => GossipHeroId(h) == ReadString(p, "playerId", ""));
                    string text = ReadString(test, "sourceText", "");
                    // Only accepted literal/kin references can grant remote eligibility, even in a probe.
                    var mentioned = GossipMentionedIds(text, main, self, people);
                    if (ReadStringList(test, "mentionedIds").Any(id => !mentioned.Contains(id)))
                        throw new ArgumentException("Mentioned identities must resolve from the probe's actual words.");
                    var topic = GossipFields("speakerId", actor, "playerId", ReadString(p, "playerId", ""), "speaker", self,
                        "sourceText", text, "mentionedIds", mentioned, "holders", new List<string> { actor },
                        "signal", GossipFields("targets", ReadDictionaryList(test, "targets")));
                    string type = ReadString(test, "topicType", "positive");
                    var audit = BuildPersonalGossipCandidates(c, topic, people, type);
                    var candidates = GossipRecipients(audit, type, GossipTrait(ReadJsonObject(CharacterFile(campaign, actor, "traits.json")), "familyMotivation"), PersonalGossipSettings());
                    double total = candidates.Sum(x => x.Weight);
                    result["candidates"] = candidates.Select(x => { var item = x.Audit(); item["probability"] = x.Weight / total;
                        item["delayDays"] = GossipTransitDays(self, x.Person, PersonalGossipSettings(), x.Local); return item; }).ToList();
                    result["exclusions"] = audit.Where(x => x.Exclusion.Length > 0 && (selected.Contains(x.Id) || x.Local || x.Mentioned)).Select(x => x.Audit()).ToList();
                    result["exclusionCounts"] = audit.Where(x => x.Exclusion.Length > 0).GroupBy(x => x.Exclusion).ToDictionary(g => g.Key, g => g.Count());
                    result["coverage"] = "Recipient math on native presence and canonical affinity only; no source extraction, delivery or conversation quality proof.";
                }
                if (phase == "snapshot")
                {
                    result["communications"] = ReadCommunicationSnapshot(c, timeline, actor, offset, limit);
                    var args = GossipFields("timeline", timeline, "limit", limit, "offset", offset);
                    // Fixed tables, bounded parameterized pages; no operator SQL surface.
                    foreach (string table in new[] { "topics", "messages", "history" })
                    {
                        string heroColumn = table == "history" ? "owner_id" : "speaker_id";
                        string idColumn = table == "topics" ? "topic_id" : table == "messages" ? "message_id" : "receipt_id";
                        string filter = "timeline_id=$timeline";
                        if (selected.Count > 0)
                        {
                            var parts = new List<string>();
                            for (int i = 0; i < selected.Count; i++) { args["hero" + i] = selected[i]; parts.Add("$hero" + i); }
                            filter += " AND (" + heroColumn + " IN (" + string.Join(",", parts) + ")"
                                + (table == "messages" ? " OR recipient_id IN (" + string.Join(",", parts) + ")" : "") + ")";
                        }
                        result[table] = QuerySql(c, "SELECT * FROM personal_gossip_" + table + " WHERE " + filter + " ORDER BY " + idColumn + " LIMIT $limit OFFSET $offset;", args);
                        result[table + "Count"] = ReadInt(QuerySql(c, "SELECT COUNT(*) AS n FROM personal_gossip_" + table + " WHERE " + filter + ";", args).First(), "n", 0);
                    }
                    result["affinities"] = selected.SelectMany(a => selected.Where(b => a != b).Select(b => GossipFields("observerId", a, "subjectId", b,
                        "affinity", GossipAffinity(c, a, b, people.Single(h => GossipHeroId(h) == a), people.Single(h => GossipHeroId(h) == b))))).ToList();
                }
                if (phase == "tick")
                {
                    if (!GossipAcceptPresence(c, p)) throw new InvalidOperationException("Native clock/presence was rejected.");
                    result["delivered"] = DeliverPersonalGossip(c, campaign, timeline, ReadDouble(p, "worldDay", 0));
                    SchedulePersonalGossip(campaign);
                }
                return result;
            }
        }

        // Calls the production validator only, inside the exact authorized native command.
        // Never schedules work or delivers anything, and rolls back even if a guard regresses.
        private static Dictionary<string, object> GossipProductionPresenceContract(ReignDbConnection c, Dictionary<string, object> payload)
        {
            string timeline = GossipTimeline(payload);
            string original = Json.Serialize(GossipClock(c, timeline));
            bool control, incomplete, missing, overCap, unchanged;
            using (var transaction = c.BeginTransaction())
            {
                control = GossipAcceptPresence(c, payload);
                string accepted = Json.Serialize(GossipClock(c, timeline));
                var source = ReadDictionary(payload, "gossipPresence");
                var invalidPresence = new Dictionary<string, object>(source);
                var invalid = new Dictionary<string, object>(payload) { ["gossipPresence"] = invalidPresence };
                invalidPresence["rosterComplete"] = false;
                incomplete = !GossipAcceptPresence(c, invalid);
                unchanged = Json.Serialize(GossipClock(c, timeline)) == accepted;
                invalidPresence.Remove("rosterComplete");
                missing = !GossipAcceptPresence(c, invalid);
                unchanged &= Json.Serialize(GossipClock(c, timeline)) == accepted;
                invalidPresence["rosterComplete"] = true;
                invalidPresence["people"] = Enumerable.Repeat(ReadDictionaryList(source, "people").First(), 8193).ToList();
                overCap = !GossipAcceptPresence(c, invalid);
                unchanged &= Json.Serialize(GossipClock(c, timeline)) == accepted;
                transaction.Rollback();
            }
            return GossipFields("schema", "reign-gossip-production-presence-guard-v1", "controlAccepted", control,
                "incompleteRejected", incomplete, "missingCompletenessRejected", missing, "overCapRejected", overCap,
                "clockUnchangedOnRejection", unchanged, "rollbackRestoredClock", Json.Serialize(GossipClock(c, timeline)) == original,
                "coverage", "Production presence acceptance only, fixed invalid variants within a rolled-back transaction; no delivery, scheduling or provider dispatch.");
        }

        private static Dictionary<string, object> PersonalGossipConversationScheduling(ReignDbConnection c, string timeline,
            double day, List<string> participants)
        {
            if (participants.Count < 1 || participants.Count > 8) throw new ArgumentException("Conversation observations need one through eight exact participants.");
            var args = GossipFields("timeline", timeline, "day", day, "since", day - 1);
            var slots = participants.Select((id, i) => { args["participant" + i] = id; return "$participant" + i; }).ToList();
            string owners = string.Join(",", slots);
            string topicFilter = "timeline_id=$timeline AND created_day>$since AND created_day<=$day AND speaker_id IN (" + owners + ")";
            string messageFilter = "timeline_id=$timeline AND created_day>$since AND created_day<=$day AND (speaker_id IN (" + owners + ") OR recipient_id IN (" + owners + "))";
            var topics = QuerySql(c, "SELECT topic_id,speaker_id,status,created_day,payload_json FROM personal_gossip_topics WHERE "
                + topicFilter + " ORDER BY created_day DESC,topic_id LIMIT 64;", args).Select(row => {
                    var payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? GossipFields();
                    return GossipFields("topicId", ReadString(row, "topic_id", ""), "speakerId", ReadString(row, "speaker_id", ""),
                        "status", ReadString(row, "status", ""), "createdDay", ReadDouble(row, "created_day", 0),
                        "sourceExchangeId", ReadString(payload, "sourceExchangeId", ""), "sourceTurnIds", ReadStringList(payload, "sourceTurnIds"),
                        "sessionId", ReadString(payload, "sessionId", ""), "category", ReadString(payload, "category", ""),
                        "recipientId", ReadString(payload, "recipientId", ""), "savedRolls", ReadDictionary(payload, "savedRolls"));
                }).ToList();
            var messages = QuerySql(c, "SELECT message_id,topic_id,root_id,speaker_id,recipient_id,status,is_reply,hop,created_day,due_day,expires_day,attempts FROM personal_gossip_messages WHERE "
                + messageFilter + " ORDER BY created_day DESC,message_id LIMIT 64;", args);
            int topicCount = ReadInt(QuerySql(c, "SELECT COUNT(*) AS n FROM personal_gossip_topics WHERE " + topicFilter + ";", args).Single(), "n", 0);
            int messageCount = ReadInt(QuerySql(c, "SELECT COUNT(*) AS n FROM personal_gossip_messages WHERE " + messageFilter + ";", args).Single(), "n", 0);
            return GossipFields("schema", "reign-gossip-open-conversation-observation-v1", "origin", "operator_readonly_observation",
                "worldDay", day, "participants", participants, "topics", topics, "messages", messages,
                "topicCount", topicCount, "messageCount", messageCount, "truncated", topicCount > 64 || messageCount > 64,
                "communications", participants.Select(hero => TestDict("heroId", hero, "snapshot", ReadCommunicationSnapshot(c, timeline, hero, 0, 50))).ToList(),
                "providerCalls", 0, "npcKnowledgeAuthority", false);
        }

        private static void ValidatePersonalGossipTestCase(Dictionary<string, object> test)
        {
            if (test == null || Json.Serialize(test).Length > 65536) throw new ArgumentException("Missing or oversized gossip case.");
            string phase = ReadString(test, "phase", "");
            var fields = new List<string> { "caseId", "phase" };
            if (phase == "catalog") fields.AddRange(new[] { "search", "heroIds", "offset", "limit" });
            if (phase == "snapshot") fields.AddRange(new[] { "heroIds", "actorHeroId", "offset", "limit" });
            if (phase == "prepare") fields.AddRange(new[] { "actorHeroId", "sourceMarker", "placements", "relationships", "traitProfiles", "draws", "compositionMode", "activateResidents" });
            if (phase == "policy_probe") fields.AddRange(new[] { "actorHeroId", "heroIds", "sourceText", "mentionedIds", "targets", "topicType" });
            if (phase == "conversation") fields.AddRange(new[] { "actorHeroId", "playerTurns", "conversationMode", "participantHeroIds" });
            if (phase == "composition_probe") fields.AddRange(new[] { "actorHeroId", "messageId" });
            if (test.Keys.Any(k => !fields.Contains(k))) throw new ArgumentException("Unknown or wrong-phase gossip field.");
            if (phase == "composition_probe" && (ReadString(test, "actorHeroId", "").Length == 0 || ReadString(test, "messageId", "").Length == 0))
                throw new ArgumentException("A composition probe needs exact actorHeroId and messageId.");
            foreach (string key in new[] { "heroIds", "mentionedIds", "placements", "traitProfiles", "targets" })
                if ((key.EndsWith("Ids", StringComparison.Ordinal) ? ReadStringList(test, key).Count : ReadDictionaryList(test, key).Count) > 16) throw new ArgumentException("At most sixteen participants per field.");
            if (ReadDictionaryList(test, "relationships").Count > 64) throw new ArgumentException("At most 64 relationship rows.");
            var group = ReadStringList(test, "participantHeroIds");
            if (phase == "conversation" && ReadString(test, "conversationMode", "") == "party_chat" && !test.ContainsKey("participantHeroIds"))
                throw new ArgumentException("Party chat needs explicit exact participants.");
            if (test.ContainsKey("participantHeroIds") && (group.Count < 2 || group.Count > 8
                || group.Distinct(StringComparer.Ordinal).Count() != group.Count
                || !group.Contains(ReadString(test, "actorHeroId", ""))
                || !new[] { "party_chat", "social_event", "wilderness_event" }.Contains(ReadString(test, "conversationMode", ""))))
                throw new ArgumentException("A group needs two through eight distinct exact participants including its actor and a group mode.");
            var activations = ReadStringList(test, "activateResidents");
            if (test.ContainsKey("activateResidents") && (activations.Count < 1 || activations.Count > 4
                || activations.Distinct(StringComparer.Ordinal).Count() != activations.Count
                || activations.Any(id => ReadDictionaryList(test, "placements").Count(row => ReadString(row, "heroId", "") == id) != 1)))
                throw new ArgumentException("At most four distinct activated residents each need exactly one explicit placement.");
            var draws = ReadDictionary(test, "draws");
            if (draws != null)
                foreach (string key in draws.Keys)
                {
                    double n = ReadDouble(draws, key, double.NaN);
                    bool recipient = key.EndsWith("_recipient", StringComparison.Ordinal);
                    if (!new[] { "share", "negative", "disclosure", "distortion", "severity", "negative_recipient", "friendly_recipient", "raise", "reply_disclosure" }.Contains(key)
                        || !double.IsFinite(n) || (recipient ? n < 0 || n >= 1 : n < 1 || n > 100 || n != Math.Truncate(n)))
                        throw new ArgumentException("Invalid bounded saved draw.");
                }
            if (draws != null || test.ContainsKey("compositionMode"))
            {
                string marker = ReadString(test, "sourceMarker", "");
                if (phase != "prepare" || marker.Length < 8 || marker.Length > 100) throw new ArgumentException("Controls need an 8-100 character natural source marker before the first source.");
            }
            if (test.ContainsKey("compositionMode") && !new[] { "production", "fail_once", "fail_always", "delay_once", "delay_past_watch_once" }.Contains(ReadString(test, "compositionMode", "")))
                throw new ArgumentException("Unknown bounded fault.");
        }

        private static void PreparePersonalGossipTest(ReignDbConnection c, string campaign, Dictionary<string, object> p,
            Dictionary<string, object> test, List<Dictionary<string, object>> people, Dictionary<string, object> result)
        {
            string actor = ReadString(test, "actorHeroId", ""), marker = ReadString(test, "sourceMarker", "");
            string player = ReadString(p, "playerId", "");
            var ids = ReadDictionaryList(test, "placements").Concat(ReadDictionaryList(test, "traitProfiles")).Select(x => ReadString(x, "heroId", ""))
                .Concat(ReadDictionaryList(test, "relationships").SelectMany(x => new[] { ReadString(x, "observerId", ""), ReadString(x, "subjectId", "") })).Append(actor).Distinct().ToList();
            if (ids.Count > 16 || ids.Any(id => !people.Any(h => GossipHeroId(h) == id && ReadBool(h, "isAlive", false)))
                || actor == player || actor.Length == 0) throw new ArgumentException("Prepare needs at most sixteen actual living participants and a non-player speaker.");
            var activationIds = ReadStringList(test, "activateResidents");
            var activationStates = ReadDictionaryList(p, "nativeActivationStates");
            if (activationIds.Count > 0)
            {
                var actorPerson = people.Single(h => GossipHeroId(h) == actor);
                if (activationStates.Count != activationIds.Count || activationStates.Any(state => !activationIds.Contains(ReadString(state, "heroId", "")))
                    || activationStates.Select(state => ReadString(state, "heroId", "")).Distinct(StringComparer.Ordinal).Count() != activationIds.Count)
                    throw new ArgumentException("Activation needs exact native before-state evidence for every resident.");
                foreach (string id in activationIds)
                {
                    var person = people.Single(h => GossipHeroId(h) == id);
                    var state = activationStates.Single(row => ReadString(row, "heroId", "") == id);
                    if (id == player || id != actor && !GossipFamily(actorPerson, person) || ReadBool(state, "isActive", true)
                        || !ReadBool(state, "isAlive", false) || ReadBool(state, "isPrisoner", true)
                        || ReadDouble(state, "age", 0) < 18 || ReadString(state, "partyId", "").Length > 0
                        || ReadBool(state, "hasPrisonerParty", true) || ReadString(state, "governorOfSettlementId", "").Length > 0
                        || ReadBool(state, "isTraveling", true) || ReadBool(state, "settlementUnderSiege", true) || ReadBool(state, "hasQuestDuty", true)
                        || ReadString(state, "heroState", "").Length == 0)
                        throw new ArgumentException("Activation evidence must identify an inactive free living adult speaker or immediate family/spouse.");
                }
            }
            var profiles = ReadDictionaryList(test, "traitProfiles");
            if (profiles.Any(x => !new[] { "excitable", "reserved", "honest_loyal", "vengeful" }.Contains(ReadString(x, "profile", ""))))
                throw new ArgumentException("Unknown fixed trait profile.");
            foreach (var row in ReadDictionaryList(test, "relationships"))
                if (ReadInt(row, "affinity", 101) < -100 || ReadInt(row, "affinity", 101) > 100
                    || ReadString(row, "observerId", "") == ReadString(row, "subjectId", "")) throw new ArgumentException("Invalid directional affinity.");
            EnsureGossipTestSchema(c);
            EnsureMemoryPrecisionSchema(c);
            string fixture = ReadString(p, "runId", "") + ":" + ReadString(p, "commandId", "");
            if (QuerySql(c, "SELECT fixture_id FROM personal_gossip_test_fixtures WHERE fixture_id=$id;", GossipFields("id", fixture)).Count > 0)
                throw new InvalidOperationException("This fixture already ran. Observe it or restore the retained Current before retrying.");
            var journal = GossipFields("testCase", test, "gameInstanceId", ReadString(p, "gameInstanceId", ""),
                "restoreGeneration", Interlocked.Read(ref CampaignReplacementGeneration), "activeSaveName", ReadString(p, "activeSaveName", ""),
                "memoryRestoreGeneration", ReadString(ReadMemoryPrecisionState(c), "restore_generation", ""),
                "beforePresence", people.Where(x => ids.Contains(GossipHeroId(x))).ToList(),
                "beforeNativeActivationStates", activationStates,
                "beforeTraits", profiles.ToDictionary(x => ReadString(x, "heroId", ""), x => (object)ReadJsonObject(CharacterFile(campaign, ReadString(x, "heroId", ""), "traits.json"))),
                "beforeEditorAuthority", profiles.ToDictionary(x => ReadString(x, "heroId", ""), x => (object)ReadJsonObject(CharacterFile(campaign, ReadString(x, "heroId", ""), "editor_authority.json"))),
                "beforeRelationships", ReadDictionaryList(test, "relationships").SelectMany(x => QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                    GossipFields("pair", AmbientPairKey(ReadString(x, "observerId", ""), ReadString(x, "subjectId", ""))))).ToList());
            // Persist recovery evidence BEFORE any fixture mutation. Current's retained save restores both stores.
            ExecuteSql(c, "INSERT INTO personal_gossip_test_fixtures(fixture_id,timeline_id,speaker_id,marker,created_utc,payload_json) VALUES($id,$timeline,$actor,$marker,$utc,$json);",
                GossipFields("id", fixture, "timeline", ReadString(p, "timelineId", ""), "actor", actor, "marker", marker,
                    "utc", DateTimeOffset.UtcNow.ToString("o"), "json", Json.Serialize(journal)));
            foreach (var row in ReadDictionaryList(test, "relationships"))
            {
                string a = ReadString(row, "observerId", ""), b = ReadString(row, "subjectId", ""), pair = AmbientPairKey(a, b);
                var old = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", GossipFields("pair", pair)).FirstOrDefault();
                if (old == null)
                    ExecuteSql(c, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,'','',$affinity,0,$day,$day,$ts);", GossipFields("pair", pair, "a", a, "b", b,
                        "affinity", ReadInt(row, "affinity", 0), "day", ReadDouble(p, "worldDay", 0), "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                else ExecuteSql(c, "UPDATE relationship_pair_chemistry SET " + (GossipSameId(ReadString(old, "hero_a_id", ""), a) ? "affinity_a_to_b" : "affinity_b_to_a")
                    + "=$value WHERE pair_key=$pair;", GossipFields("value", ReadInt(row, "affinity", 0), "pair", pair));
            }
            foreach (var item in profiles)
            {
                string hero = ReadString(item, "heroId", ""), profile = ReadString(item, "profile", "");
                var traits = ReadJsonObject(CharacterFile(campaign, hero, "traits.json"));
                ApplyPersonalGossipTraitProfile(traits, profile);
                // Use the normal editor reconciliation, including derived personality and cache invalidation.
                SavePersonalGossipTraitProfile(c, campaign, hero, traits);
            }
            result["fixtureId"] = fixture; result["recovery"] = "Reload retained campaign-test Current; do not save prerequisites over its recovery state.";
        }

        private static void SavePersonalGossipTraitProfile(ReignDbConnection c, string campaign, string hero, Dictionary<string, object> traits)
        {
            // The complete editor save establishes authority as well as legal values.
            // Otherwise ordinary Open may rematerialize a shipped profile over the fixture.
            EnsureCharacterEditorSchema(c);
            // Match an actual edit: load the complete profile before granting editor
            // authority. A traits-only save on sparse legacy storage would otherwise
            // preserve a missing narrative and make the subsequent conversation fail.
            var loaded = CharacterEditorLoadApi(GossipFields("campaignId", campaign, "heroStringId", hero));
            if (!ReadBool(loaded, "ok", false))
                throw new InvalidOperationException("The guarded trait prerequisite could not load its ordinary editor profile.");
            var head = QuerySql(c, "SELECT revision_token FROM character_editor_heads WHERE hero_id=$hero LIMIT 1;", GossipFields("hero", hero)).FirstOrDefault();
            var saved = CharacterEditorSaveApi(GossipFields("campaignId", campaign, "heroStringId", hero,
                "section", "traits", "revisionToken", ReadString(head, "revision_token", ""),
                "description", "Guarded disposable personal gossip trait prerequisite", "documents", GossipFields("traits", traits)));
            if (!ReadBool(saved, "ok", false) || ReadBool(saved, "nativeQueued", true))
                throw new InvalidOperationException("The guarded trait prerequisite could not be saved through the ordinary editor.");
        }

        private static void ApplyPersonalGossipTraitProfile(Dictionary<string, object> traits, string profile)
        {
            var values = ReadDictionary(traits, "traitPercentages") ?? new Dictionary<string, object>();
            var foundation = ReadDictionary(traits, "foundationTraits") ?? ReadDictionary(traits, "hiddenReignTraits") ?? new Dictionary<string, object>();
            var snapshot = ReadDictionary(traits, "traitPercentageModifierSnapshot") ?? new Dictionary<string, object>();
            // Native-locked courage, mercy and generosity are deliberately absent.
            foreach (string key in new[] { "sociability", "curiosity", "impulsiveness", "pride", "vengefulness", "honesty", "compassion", "loyalty", "tact", "discipline", "empathy", "shame", "familyMotivation" })
            {
                int value = profile == "reserved" ? (new[] { "tact", "discipline", "empathy", "honesty", "loyalty" }.Contains(key) ? 100 : 0)
                    : profile == "honest_loyal" ? (new[] { "honesty", "loyalty", "tact", "discipline", "empathy", "compassion" }.Contains(key) ? 100 : 75)
                    : profile == "vengeful" ? (new[] { "honesty", "compassion", "loyalty", "tact", "discipline", "empathy", "shame" }.Contains(key) ? 0 : 100)
                    : (new[] { "sociability", "curiosity", "impulsiveness", "pride", "familyMotivation" }.Contains(key) ? 100 : 50);
                values[key] = value;
                foundation[key] = snapshot[key] = value <= 20 ? -2 : value <= 40 ? -1 : value <= 60 ? 0 : value <= 80 ? 1 : 2;
            }
            traits["traitPercentages"] = values; traits["foundationTraits"] = foundation;
            traits["hiddenReignTraits"] = new Dictionary<string, object>(foundation);
            traits["traitPercentageModifierSnapshot"] = snapshot;
        }

        private static void CapturePersonalGossipTestControls(ReignDbConnection c, string campaign, Dictionary<string, object> topic, Dictionary<string, object> prior)
        {
            if (prior != null)
            {
                var previous = ReadDictionary(GossipJson(prior), "operatorTestControls");
                if (previous?.Count > 0) topic["operatorTestControls"] = previous;
                return; // Never change an existing saved decision.
            }
            if (!TableExists(c, "personal_gossip_test_fixtures")) return;
            var runtime = LoadLiveTestRuntime(campaign);
            foreach (var row in QuerySql(c, "SELECT * FROM personal_gossip_test_fixtures WHERE timeline_id=$timeline AND speaker_id=$speaker AND consumed_topic='' ORDER BY created_utc LIMIT 32;",
                GossipFields("timeline", ReadString(topic, "timelineId", ""), "speaker", ReadString(topic, "speakerId", ""))))
            {
                var journal = GossipJson(row); var test = ReadDictionary(journal, "testCase"); string marker = ReadString(row, "marker", "");
                if (marker.Length < 8 || ReadString(topic, "sourceText", "").IndexOf(marker, StringComparison.Ordinal) < 0
                    || !GossipTestControlsMatch(journal, runtime, Interlocked.Read(ref CampaignReplacementGeneration),
                        ReadString(ReadMemoryPrecisionState(c), "restore_generation", ""),
                        ParseLiveTestUtc(ReadString(row, "created_utc", "")), DateTimeOffset.UtcNow)) continue;
                topic["operatorTestControls"] = GossipFields("fixtureId", ReadString(row, "fixture_id", ""), "draws", ReadDictionary(test, "draws"),
                    "compositionMode", ReadString(test, "compositionMode", "production"));
                ExecuteSql(c, "UPDATE personal_gossip_test_fixtures SET consumed_topic=$topic WHERE fixture_id=$id AND consumed_topic='';",
                    GossipFields("topic", ReadString(topic, "id", ""), "id", ReadString(row, "fixture_id", "")));
                break;
            }
        }
        private static bool GossipTestControlsMatch(Dictionary<string, object> journal, Dictionary<string, object> runtime,
            long processGeneration, string memoryGeneration, DateTimeOffset created, DateTimeOffset now) =>
            ReadString(runtime, "activeSaveName", "").Length > 0
            && ReadString(runtime, "gameInstanceId", "").Length > 0
            && ReadString(journal, "activeSaveName", "") == ReadString(runtime, "activeSaveName", "")
            && ReadString(journal, "gameInstanceId", "") == ReadString(runtime, "gameInstanceId", "")
            && ReadLong(journal, "restoreGeneration", -1) == processGeneration && processGeneration >= 0
            && !string.IsNullOrEmpty(memoryGeneration) && ReadString(journal, "memoryRestoreGeneration", "") == memoryGeneration
            && created > now.AddHours(-12) && created <= now.AddMinutes(1);
        private static double GossipDecisionDraw(Dictionary<string, object> topic, string id, string stage)
        {
            var draws = ReadDictionary(ReadDictionary(topic, "operatorTestControls"), "draws");
            double n = ReadDouble(draws, stage, double.NaN);
            return double.IsFinite(n) && stage.EndsWith("_recipient", StringComparison.Ordinal) && n >= 0 && n < 1 ? n : GossipDraw(id, stage);
        }
        private static int GossipDecisionRoll(Dictionary<string, object> topic, string id, string stage)
        {
            double n = ReadDouble(ReadDictionary(ReadDictionary(topic, "operatorTestControls"), "draws"), stage, double.NaN);
            return double.IsFinite(n) && n == Math.Truncate(n) && n >= 1 && n <= 100 ? (int)n : GossipRoll(id, stage);
        }
    }
}
