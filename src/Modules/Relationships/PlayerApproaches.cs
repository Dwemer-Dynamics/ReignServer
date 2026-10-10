using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string JealousyApproachPrompt = @"You requested this meeting because the player has neglected you while giving particular other people attention. You speak FIRST; the player has accepted an invitation, not uttered a greeting or apology. In your first spoken reply, explain that you feel overlooked and NAME the primary rival in the authoritative brief, connecting their repeated attention to your own unmet wish for time. Use the supplied intensity: slight means a gentle request; moderate means clear disappointment; strong means pointed hurt or irritation; huge means deep resentment, still governed by your patience, pride, tact and existing relationship. Use only the individually witnessed days supplied for each rival. Never pool everyone into one rival, invent an affair, romance, promise, private conversation or motive. Do not read numerical scores aloud. Subsequent replies respond to the player without repeating the opening accusation. Return approachOpeningEvidence containing primaryRivalId, severityLevel and rivalDays (a map of any mentioned rival IDs to their exact witnessed day counts).";

        private static void EnsurePlayerApproachSchema(ReignDbConnection c)
        {
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS player_attention_events (
sequence_id BIGSERIAL PRIMARY KEY,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,
observer_id TEXT NOT NULL,target_id TEXT NOT NULL,calendar_day INTEGER NOT NULL,
target_name TEXT NOT NULL,location_id TEXT NOT NULL,exchange_id TEXT NOT NULL,source_id TEXT NOT NULL,event_text TEXT NOT NULL,created_ts BIGINT NOT NULL DEFAULT 0,
UNIQUE(timeline_id,player_id,observer_id,target_id,calendar_day));");
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS player_approach_state (
timeline_id TEXT NOT NULL,state_key TEXT NOT NULL,value_json TEXT NOT NULL,
PRIMARY KEY(timeline_id,state_key));");
        }

        private static Dictionary<string, object> ApproachState(ReignDbConnection c, string timeline, string key) =>
            TryParseJsonObject(ReadString(QuerySql(c, "SELECT value_json FROM player_approach_state WHERE timeline_id=$timeline AND state_key=$key;",
                TestDict("timeline", timeline, "key", key)).FirstOrDefault(), "value_json", "{}")) ?? TestDict();

        private static void PutApproachState(ReignDbConnection c, string timeline, string key, Dictionary<string, object> value) =>
            ExecuteSql(c, @"INSERT INTO player_approach_state(timeline_id,state_key,value_json) VALUES($timeline,$key,$value)
ON CONFLICT(timeline_id,state_key) DO UPDATE SET value_json=EXCLUDED.value_json;",
                TestDict("timeline", timeline, "key", key, "value", Json.Serialize(value)));

        // Runs on the accepted-source transaction: observations and contact resets
        // can never outlive a rolled-back reply. Presence is captured on the game thread.
        private static Dictionary<string, object> RecordAcceptedPlayerAttention(ReignDbConnection c, AcceptedConversationUnit unit,
            List<Dictionary<string, object>> sourceRows, PlayerApproachOptions options = null)
        {
            string mode = ReadFirstString(unit.Payload, "channel", "conversationMode");
            if (mode.Contains("letter") || mode.Contains("correspondence") || IsTechnicalConversationNotice(unit.Reply))
                return TestDict("status", "excluded");
            var presence = ReadDictionaryList(unit.Payload, "playerAttentionPresence");
            var speaker = presence.FirstOrDefault(p => ReadString(p, "heroId", "") == unit.Hero);
            if (speaker == null || unit.Hero == unit.Player) return TestDict("status", "no_native_presence");
            EnsurePlayerApproachSchema(c);
            string timeline = ContinuityTimeline(unit.Payload);
            int day = (int)Math.Floor(ReadDouble(unit.Payload, "worldDay", 0d));
            string exchange = ReadFirstString(unit.Payload, "sceneTurnId", "exchangeId", "turnId");
            string source = ReadString(sourceRows.LastOrDefault(r => ReadString(r, "speaker_id", "") == unit.Hero), "turn_id", "");
            if (source.Length == 0) throw new InvalidOperationException("Attention requires accepted NPC source evidence.");
            // Individual requests need no group sceneTurnId. Empty exchange keys
            // would exclude every later individual conversation after contact.
            exchange = FirstNonEmpty(exchange, unit.EventId, source);
            var scopes = ReadStringList(speaker, "scopes");
            options ??= ReadPlayerApproachOptions();
            int observed = 0;
            foreach (var observer in presence.Take(512).Where(p => options.TrackAttention && ReadBool(p, "alive", false)))
            {
                string id = ReadString(observer, "heroId", "");
                if (id.Length == 0 || id == unit.Hero || id == unit.Player || !ReadStringList(observer, "scopes").Intersect(scopes).Any()) continue;
                ExecuteSql(c, @"INSERT INTO player_attention_events(timeline_id,player_id,observer_id,target_id,calendar_day,target_name,location_id,exchange_id,source_id,event_text,created_ts)
VALUES($timeline,$player,$observer,$target,$day,$name,$location,$exchange,$source,$text,$ts) ON CONFLICT DO NOTHING;",
                    TestDict("timeline", timeline, "player", unit.Player, "observer", id, "target", unit.Hero, "day", day,
                        "name", unit.HeroName, "location", unit.Location ?? "", "exchange", exchange, "source", source,
                        "ts", unit.Timestamp, "text", "On day " + day + ", the player spent time talking to " + unit.HeroName + " at " + unit.Location + "."));
                observed++;
            }
            long sequence = ReadLong(QuerySql(c, "SELECT COALESCE(MAX(sequence_id),0) AS sequence FROM player_attention_events;").First(), "sequence", 0);
            PutApproachState(c, timeline, "contact:" + unit.Player + ":" + unit.Hero,
                TestDict("sequence", sequence, "exchange", exchange, "day", day));
            PutApproachState(c, timeline, "cadence:" + unit.Player + ":" + unit.Hero, TestDict("day", day, "sharedDays", 0));
            string requestId = ReadString(unit.Payload, "approachRequestId", "");
            if (requestId.Length > 0)
            {
                var request = ApproachState(c, timeline, "request:" + requestId);
                if (ReadString(request, "status", "") == "accepted")
                {
                    var heard = ReadStringList(request, "heard");
                    if (!heard.Contains(unit.Hero)) heard.Add(unit.Hero);
                    request["heard"] = heard;
                    PutApproachState(c, timeline, "request:" + requestId, request);
                }
            }
            return TestDict("status", "committed", "observers", observed, "sourceId", source, "trackingDisabled", !options.TrackAttention);
        }

        private static Dictionary<string, object> PlayerApproachEvaluateApi(Dictionary<string, object> p, PlayerApproachOptions options = null)
        {
            string campaign = ReadString(p, "campaignId", "default"), timeline = ContinuityTimeline(p), player = ReadString(p, "playerId", "");
            string kind = ReadString(p, "kind", "shared");
            double worldDay = ReadDouble(p, "worldDay", -1);
            if (player.Length == 0 || worldDay < 0 || double.IsNaN(worldDay) || double.IsInfinity(worldDay)
                || !new[] { "shared", "keep", "patrol" }.Contains(kind)) return TestDict("ok", false, "error", "Invalid approach scope.");
            options ??= ReadPlayerApproachOptions();
            if (!options.Allows(kind)) return TestDict("ok", true, "status", "disabled", "requests", new List<Dictionary<string, object>>());
            int day = (int)Math.Floor(worldDay);
            lock (CampaignRelationshipWriteLock(campaign))
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePlayerApproachSchema(c); EnsureMbtiRelationshipSchema(c);
                ExecuteSql(c, "BEGIN;");
                try
                {
                    var winners = new List<Dictionary<string, object>>();
                    var diagnostics = new List<Dictionary<string, object>>();
                    var random = new Random();
                    if (kind == "keep" && ApproachState(c, timeline, "keep-group:" + player + ":" + day).Count > 0)
                        return FinishApproachEvaluation(c, TestDict("ok", true, "requests", winners, "status", "daily_group_used"));
                    if (kind == "patrol" && ApproachState(c, timeline, "patrol-start:" + player + ":" + day).Count > 0)
                        return FinishApproachEvaluation(c, TestDict("ok", true, "requests", winners, "status", "daily_pursuit_used"));
                    var candidates = ReadDictionaryList(p, "candidates").Take(512).GroupBy(x => ReadString(x, "heroId", "")).Select(g => g.First());
                    foreach (var candidate in candidates)
                    {
                        string hero = ReadString(candidate, "heroId", "");
                        if (hero.Length == 0 || hero == player || !ReadBool(candidate, "alive", false) || !ReadBool(candidate, "adult", false)
                            || !ReadBool(candidate, "free", false) || !ReadBool(candidate, "coPresent", false)) continue;
                        if (kind == "shared" && !ReadBool(candidate, "known", false)) continue;
                        if (kind != "shared" && !ReadBool(candidate, "isLord", false)) continue;
                        if (kind == "patrol" && (!ReadBool(candidate, "patrolling", false) || ReadBool(candidate, "hostile", true)
                            || ReadBool(candidate, "protected", true))) continue;
                        string check = "check:" + kind + ":" + player + ":" + hero + ":" + day;
                        if (ApproachState(c, timeline, check).Count > 0) continue;
                        var traits = ReadJsonObject(CharacterFile(campaign, hero, "traits.json"));
                        if (traits == null || traits.Count == 0) traits = BuildTraitDocument(ReadDictionary(candidate, "profile"));
                        double jealousy = RelationshipTraitPercent(traits, "jealousy"), patience = RelationshipTraitPercent(traits, "patience");
                        double boldness = RelationshipTraitPercent(traits, "boldness"), pride = RelationshipTraitPercent(traits, "pride"), irritability = RelationshipTraitPercent(traits, "irritability");
                        var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", AmbientPairKey(hero, player))).FirstOrDefault();
                        int affinity = pair == null ? ReadInt(candidate, "nativeRelation", 0)
                            : ReadInt(pair, ReadString(pair, "hero_a_id", "") == hero ? "affinity_a_to_b" : "affinity_b_to_a", 0);
                        var contact = ApproachState(c, timeline, "contact:" + player + ":" + hero);
                        var rivals = QuerySql(c, @"SELECT target_id,MAX(target_name) AS target_name,COUNT(*) AS days,
MIN(calendar_day) AS first_day,MAX(calendar_day) AS last_day,
array_to_json((array_agg(source_id ORDER BY sequence_id DESC))[1:64])::text AS source_ids_json FROM player_attention_events
WHERE timeline_id=$timeline AND player_id=$player AND observer_id=$hero AND sequence_id>$sequence AND exchange_id<>$exchange
GROUP BY target_id;", TestDict("timeline", timeline, "player", player, "hero", hero,
                            "sequence", ReadLong(contact, "sequence", 0), "exchange", ReadString(contact, "exchange", "")))
                            .Select(r => TestDict("heroId", ReadString(r, "target_id", ""), "name", ReadString(r, "target_name", ""), "days", ReadInt(r, "days", 0),
                                "score", ReadInt(r, "days", 0) * PlayerApproachPolicy.Sensitivity(jealousy, patience),
                                "firstDay", ReadInt(r, "first_day", 0), "lastDay", ReadInt(r, "last_day", 0),
                                "sourceIds", TextListFromJson(ReadString(r, "source_ids_json", "[]")), "sourceCoverage", "latest_64_sources_full_day_count"))
                            .OrderByDescending(r => ReadDouble(r, "score", 0)).ThenBy(r => ReadString(r, "heroId", ""), StringComparer.Ordinal).ToList();
                        double score = PlayerApproachPolicy.NeglectScore(rivals.Select(r => ReadInt(r, "days", 0)), jealousy, patience);
                        int level = PlayerApproachPolicy.NeglectLevel(score), interval = PlayerApproachPolicy.SharedDayInterval(jealousy, patience);
                        if (kind == "shared")
                        {
                            var cadence = ApproachState(c, timeline, "cadence:" + player + ":" + hero);
                            int shared = ReadInt(cadence, "sharedDays", 0) + (ReadInt(cadence, "day", -1) == day ? 0 : 1);
                            bool due = shared >= interval;
                            PutApproachState(c, timeline, "cadence:" + player + ":" + hero, TestDict("day", day, "sharedDays", due ? 0 : shared));
                            if (!due) continue;
                        }
                        if (kind == "patrol" && worldDay - ReadDouble(ApproachState(c, timeline, "patrol-last:" + hero), "day", -1000) < PlayerApproachPolicy.PatrolCooldownDays) continue;
                        int chance = kind == "shared" ? PlayerApproachPolicy.NeglectApproachChance(jealousy, patience, boldness, level, affinity)
                            : kind == "keep" ? PlayerApproachPolicy.KeepApproachChance(affinity, ReadDouble(p, "playerRenown", 0))
                            : PlayerApproachPolicy.PatrolApproachChance(ReadInt(p, "healthyPartyMembers", 0));
                        int roll = random.Next(1, 101);
                        var result = TestDict("heroId", hero, "chance", chance, "roll", roll, "day", day);
                        PutApproachState(c, timeline, check, result); diagnostics.Add(result);
                        if (roll > chance) continue;
                        winners.Add(TestDict("heroId", hero, "name", ReadString(candidate, "name", hero), "rivals", rivals,
                            "primaryRival", rivals.FirstOrDefault() ?? TestDict(), "neglectScore", score, "severityLevel", level,
                            "severity", PlayerApproachPolicy.NeglectLabel(level), "lastContact", contact, "jealousy", jealousy, "patience", patience,
                            "boldness", boldness, "pride", pride, "irritability", irritability, "affinity", affinity,
                            "location", ReadString(candidate, "location", ""), "partyId", ReadString(candidate, "partyId", ""),
                            "distance", ReadDouble(candidate, "distance", 0), "known", ReadBool(candidate, "known", false),
                            "penalty", kind == "shared" ? PlayerApproachPolicy.NeglectRefusalPenalty(level, jealousy, patience)
                                : kind == "keep" ? random.Next(1, 6) : PlayerApproachPolicy.PassingPenalty(pride, irritability, patience)));
                    }
                    if (kind == "patrol") winners = winners.OrderBy(w => ReadDouble(w, "distance", 0)).ThenBy(w => ReadString(w, "partyId", ""), StringComparer.Ordinal).Take(1).ToList();
                    if (kind == "keep")
                    {
                        // Uniform selection without replacement, including tied input order.
                        for (int i = winners.Count - 1; i > 0; i--)
                        {
                            int selected = random.Next(i + 1);
                            var value = winners[i]; winners[i] = winners[selected]; winners[selected] = value;
                        }
                        winners = winners.Take(PlayerApproachPolicy.MaximumKeepSpeakers).ToList();
                    }
                    var requests = new List<Dictionary<string, object>>();
                    foreach (var group in kind == "shared" ? winners.Select(w => new List<Dictionary<string, object>> { w })
                        : winners.Count > 0 ? new[] { winners }.AsEnumerable() : Enumerable.Empty<List<Dictionary<string, object>>>())
                    {
                        string id = Guid.NewGuid().ToString("N");
                        var request = TestDict("requestId", id, "kind", kind, "playerId", player, "worldDay", worldDay, "calendarDay", day,
                            "heroes", group, "status", "pending", "heard", new List<string>(), "policy", PlayerApproachPolicy.Version,
                            "playerRenown", ReadDouble(p, "playerRenown", 0));
                        PutApproachState(c, timeline, "request:" + id, request); requests.Add(request);
                        if (kind == "keep") PutApproachState(c, timeline, "keep-group:" + player + ":" + day, TestDict("requestId", id));
                        if (kind == "patrol")
                        {
                            PutApproachState(c, timeline, "patrol-start:" + player + ":" + day, TestDict("requestId", id));
                            PutApproachState(c, timeline, "patrol-last:" + ReadString(group[0], "heroId", ""), TestDict("day", worldDay));
                        }
                    }
                    return FinishApproachEvaluation(c, TestDict("ok", true, "requests", requests, "checks", diagnostics));
                }
                catch { ExecuteSql(c, "ROLLBACK;"); throw; }
            }
        }

        private static Dictionary<string, object> FinishApproachEvaluation(ReignDbConnection c, Dictionary<string, object> result)
        { ExecuteSql(c, "COMMIT;"); return result; }

        private static Dictionary<string, object> PlayerApproachDecisionApi(Dictionary<string, object> p, PlayerApproachOptions options = null)
        {
            string campaign = ReadString(p, "campaignId", "default"), timeline = ContinuityTimeline(p), id = ReadString(p, "requestId", "");
            string choice = ReadString(p, "choice", "");
            if (id.Length == 0 || !new[] { "accept", "refuse", "cancel", "acknowledge" }.Contains(choice)) return TestDict("ok", false, "error", "Invalid approach decision.");
            lock (CampaignRelationshipWriteLock(campaign))
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePlayerApproachSchema(c); EnsureMbtiRelationshipSchema(c);
                ExecuteSql(c, "BEGIN;");
                try
                {
                    var request = ApproachState(c, timeline, "request:" + id);
                    if (request.Count == 0 || ReadString(request, "playerId", "") != ReadString(p, "playerId", ""))
                        return FinishApproachEvaluation(c, TestDict("ok", false, "error", "Unknown approach request."));
                    if (choice == "acknowledge")
                    {
                        var observations = ReadDictionaryList(request, "nativeObservations");
                        var effects = ReadDictionaryList(request, "receipts");
                        var incoming = ReadDictionaryList(p, "nativeObservations");
                        if (ReadString(request, "status", "") != "refused" || incoming.Count == 0 || incoming.Count > PlayerApproachPolicy.MaximumKeepSpeakers)
                            return FinishApproachEvaluation(c, TestDict("ok", false, "error", "No declined approach authorizes these native observations."));
                        foreach (var observation in incoming)
                        {
                            string receiptId = ReadString(observation, "receiptId", "");
                            var effect = effects.SingleOrDefault(r => ReadString(r, "receiptId", "") == receiptId);
                            int before = ReadInt(observation, "priorNativeRelation", int.MinValue), after = ReadInt(observation, "nativeRelation", int.MinValue);
                            if (effect == null || ReadString(observation, "subjectId", "") != ReadString(effect, "heroId", "")
                                || ReadString(observation, "targetId", "") != ReadString(request, "playerId", "")
                                || ReadInt(observation, "requestedDelta", int.MinValue) != ReadInt(effect, "nativeRelationDelta", 0)
                                || before < -100 || before > 100 || after < -100 || after > 100
                                || ReadInt(observation, "observedDelta", int.MinValue) != after - before
                                || ReadString(observation, "measurementStatus", "") != "observed_before_and_after")
                                return FinishApproachEvaluation(c, TestDict("ok", false, "error", "The native observation does not match its exact approach consequence."));
                            var prior = observations.SingleOrDefault(r => ReadString(r, "receiptId", "") == receiptId);
                            if (prior != null && new[] { "priorNativeRelation", "nativeRelation", "requestedDelta" }.Any(key => ReadInt(prior, key, 0) != ReadInt(observation, key, 0)))
                                return FinishApproachEvaluation(c, TestDict("ok", false, "error", "An acknowledged native observation cannot change."));
                            if (prior == null) observations.Add(CloneDictionary(observation));
                        }
                        request["nativeObservations"] = observations;
                        PutApproachState(c, timeline, "request:" + id, request);
                        return FinishApproachEvaluation(c, TestDict("ok", true, "request", request));
                    }
                    if (ReadString(request, "status", "") != "pending")
                        return FinishApproachEvaluation(c, TestDict("ok", true, "idempotent", true, "request", request));
                    // Only unanswered invitations are cancelled. Committed decisions
                    // and native receipt acknowledgments retain their exact-once route.
                    options ??= ReadPlayerApproachOptions();
                    if (!options.Allows(ReadString(request, "kind", ""))) choice = "cancel";
                    if (ReadString(request, "kind", "") == "shared" && ReadDictionaryList(request, "heroes").Any(h =>
                        CanonicalJson(ApproachState(c, timeline, "contact:" + ReadString(request, "playerId", "") + ":" + ReadString(h, "heroId", "")))
                            != CanonicalJson(ReadDictionary(h, "lastContact") ?? TestDict()))) choice = "cancel";
                    var receipts = new List<Dictionary<string, object>>();
                    if (choice == "refuse") foreach (var hero in ReadDictionaryList(request, "heroes"))
                    {
                        string subject = ReadString(hero, "heroId", ""), player = ReadString(request, "playerId", "");
                        int delta = -ReadInt(hero, "penalty", 0);
                        ApplyAuthoritativeRelationshipDelta(c, campaign, subject, player, delta, ReadDouble(p, "worldDay", ReadDouble(request, "worldDay", 0)), "approach_refusal", timeline, ensurePair: true);
                        receipts.Add(TestDict("receiptId", "approach:" + id + ":" + subject, "heroId", subject, "nativeRelationDelta", delta,
                            "reason", "The player declined my request to speak."));
                    }
                    request["status"] = choice == "accept" ? "accepted" : choice == "refuse" ? "refused" : "cancelled";
                    request["receipts"] = receipts;
                    PutApproachState(c, timeline, "request:" + id, request);
                    return FinishApproachEvaluation(c, TestDict("ok", true, "request", request));
                }
                catch { ExecuteSql(c, "ROLLBACK;"); throw; }
            }
        }

        private static List<Dictionary<string, object>> PlayerAttentionHistory(string campaign, string hero, Dictionary<string, object> payload)
        {
            string player = ReadFirstString(payload, "playerHeroStringId", "playerId");
            if (player.Length == 0) return new List<Dictionary<string, object>>();
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePlayerApproachSchema(c);
                return QuerySql(c, @"SELECT event_text,source_id,calendar_day,created_ts FROM player_attention_events
WHERE timeline_id=$timeline AND observer_id=$hero AND player_id=$player ORDER BY sequence_id DESC LIMIT 80;",
                    TestDict("timeline", ContinuityTimeline(payload), "hero", hero, "player", player)).Select(r => TestDict("role", "system", "speaker", "Bannerlord Reign",
                        "text", ReadString(r, "event_text", ""), "channel", "witnessed_player_attention", "sourceId", ReadString(r, "source_id", ""),
                        "worldDay", ReadInt(r, "calendar_day", 0), "ts", ReadLong(r, "created_ts", 0))).Reverse().ToList();
            }
        }

        private const string IndividualNpcOpeningCue = "[MEETING CUE: The player accepted your request to speak and has not said anything. Speak first in character about the supplied authoritative approach or rescue evidence. Do not invent player speech, consent or a completed action.]";

        private static bool PrepareIndividualNpcOpening(string campaign, string hero, Dictionary<string, object> payload)
        {
            // Recompute authority from the saved invitation, never from caller proof flags.
            payload.Remove("authoritativeApproachOpening");
            payload.Remove("authoritativeApproachBrief");
            payload.Remove("authoritativeApproachKind");
            if (!string.IsNullOrWhiteSpace(ReadFirstString(payload, "playerText", "text", "message"))
                || !payload.ContainsKey("playerAttentionPresence")
                || !ReadBool(payload, "approachOpening", false) && !ReadBool(payload, "rescueOpening", false)) return false;
            PlayerAttentionPrompt(campaign, hero, payload);
            bool opening = ReadBool(payload, "authoritativeApproachOpening", false)
                || ReadBool(payload, "rescueOpening", false) && ReadDictionary(payload, "rescueContext")?.Count > 0;
            if (!opening) return false;
            payload["turnType"] = "npc_approach_opening";
            payload["suppressPlayerTranscript"] = true;
            payload["suppressConceptionGate"] = true;
            return true;
        }

        private static string PlayerAttentionPrompt(string campaign, string hero, Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("playerAttentionPresence")) return "";
            string timeline = ContinuityTimeline(payload), player = ReadFirstString(payload, "playerHeroStringId", "playerId");
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePlayerApproachSchema(c);
                var events = QuerySql(c, @"SELECT event_text,source_id FROM player_attention_events WHERE timeline_id=$timeline
AND observer_id=$hero AND player_id=$player ORDER BY calendar_day DESC,sequence_id DESC LIMIT 64;",
                    TestDict("timeline", timeline, "hero", hero, "player", player));
                string context = events.Count == 0 ? "" : "\n\nWITNESSED PLAYER ATTENTION (generic public history only)\n" + Json.Serialize(events);
                var rescue = ReadDictionary(payload, "rescueContext");
                if (rescue != null && rescue.Count > 0)
                {
                    if (ReadString(rescue, "heroId", "") != hero || ReadString(rescue, "playerId", "") != player
                        || !new[] { "battle_rescue", "prison_rescue" }.Contains(ReadString(rescue, "kind", "")))
                        throw new InvalidOperationException("The rescue evidence does not match this conversation.");
                    context += "\n\nNATIVE RESCUE EVIDENCE\n" + Json.Serialize(rescue) + "\n"
                        + (ReadBool(payload, "rescueOpening", false) ? LoadPromptTemplate("rescue_opening.txt")
                            : "The rescue opening has already been heard. Respond to the player's current contribution; do not restart your thanks or invent further rescue facts.");
                }
                string id = ReadString(payload, "approachRequestId", "");
                if (id.Length == 0) return context;
                var request = ApproachState(c, timeline, "request:" + id);
                var brief = ReadDictionaryList(request, "heroes").FirstOrDefault(h => ReadString(h, "heroId", "") == hero);
                if (brief == null || ReadString(request, "status", "") != "accepted" || ReadString(request, "playerId", "") != player)
                    throw new InvalidOperationException("This accepted approach does not authorize the current speaker.");
                bool opening = ReadBool(payload, "approachOpening", false) && !ReadStringList(request, "heard").Contains(hero);
                payload["authoritativeApproachBrief"] = CloneDictionary(brief);
                payload["authoritativeApproachOpening"] = opening;
                string kind = ReadString(request, "kind", "");
                payload["authoritativeApproachKind"] = kind;
                context += "\n\nAUTHORITATIVE SAVED APPROACH BRIEF\n" + Json.Serialize(brief) + "\nOpening required: " + opening;
                if (!opening) context += "\nYour invitation has already been heard. This frozen brief preserves its original reason; answer the player's current contribution and let the grievance evolve or resolve without repeating your opening.";
                else if (kind == "shared") context += "\n" + LoadPromptTemplate("jealousy_approach.txt")
                    + "\nEvery named rival in spoken dialogue must have an exact rivalDays entry. If mentioning a number of days, use one separate sentence per rival. Never pool their days.";
                else context += "\nYou initiated this meeting. Speak first and explain why you wanted their attention, shaped by your actual traits and political interests. "
                    + (kind == "keep" ? "You noticed the player's public standing (clan renown " + ReadDouble(request, "playerRenown", 0) + "). Flattery or an invitation to support your cause must fit existing facts. Introduce yourself if not known." : "Your friendly patrolling party stopped to speak with the player. Explain your social or political reason. Do not claim an attack or demand combat.");
                return context;
            }
        }

        private static List<string> ApproachOpeningViolations(Dictionary<string, object> parsed, Dictionary<string, object> payload)
        {
            var errors = new List<string>();
            if (ReadBool(payload, "rescueOpening", false))
            {
                var rescue = ReadDictionary(payload, "rescueContext");
                var proof = ReadDictionary(parsed, "rescueOpeningEvidence");
                string spoken = ReadFirstString(parsed, "reply", "response", "text");
                if (ReadString(proof, "eventId", "") != ReadString(rescue, "eventId", "")
                    || ReadString(proof, "kind", "") != ReadString(rescue, "kind", "")) errors.Add("Bind the spoken rescue opening to the supplied native event.");
                if (!Regex.IsMatch(spoken, ReadString(rescue, "kind", "") == "prison_rescue"
                    ? @"(?i)\b(prison\w*|captiv\w*|escape\w*|free\w*|dungeon)\b" : @"(?i)\b(battle|fighting|fight|field|victory|outmatched|reinforc\w*)\b")) errors.Add("Refer to the specific rescue in your spoken opening.");
            }
            if (!ReadBool(payload, "authoritativeApproachOpening", false)
                || ReadString(payload, "authoritativeApproachKind", "shared") != "shared") return errors;
            var brief = ReadDictionary(payload, "authoritativeApproachBrief");
            if (ReadInt(brief, "severityLevel", 0) <= 0) return errors;
            var primary = ReadDictionary(brief, "primaryRival");
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content"), name = ReadString(primary, "name", "");
            if (name.Length == 0 || reply.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) errors.Add("Name the primary rival in the spoken opening.");
            if (!Regex.IsMatch(reply, @"(?i)\b(neglect\w*|overlook\w*|forgot\w*|attention|time|ignored?|left out|spare|company|miss\w*|unimportant|abandon\w*)\b")) errors.Add("Explain your unmet wish for the player's attention.");
            var evidence = ReadDictionary(parsed, "approachOpeningEvidence");
            if (ReadString(evidence, "primaryRivalId", "") != ReadString(primary, "heroId", "")
                || ReadInt(evidence, "severityLevel", -1) != ReadInt(brief, "severityLevel", 0)) errors.Add("Use the authoritative rival and severity in opening evidence.");
            var counts = ReadDictionary(evidence, "rivalDays") ?? TestDict();
            if (!counts.ContainsKey(ReadString(primary, "heroId", ""))) errors.Add("Supply the exact primary rival day count in evidence.");
            var rivals = ReadDictionaryList(brief, "rivals");
            foreach (var rival in rivals.Where(r => ReadString(r, "name", "").Length > 0 && reply.IndexOf(ReadString(r, "name", ""), StringComparison.OrdinalIgnoreCase) >= 0))
                if (!counts.ContainsKey(ReadString(rival, "heroId", ""))) errors.Add("Bind every spoken rival to their individual day count.");
            foreach (string sentence in Regex.Split(reply, @"[.!?\n]+"))
            {
                var named = rivals.Where(r => ReadString(r, "name", "").Length > 0 && sentence.IndexOf(ReadString(r, "name", ""), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (Match asserted in Regex.Matches(sentence, @"(?i)\b(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:whole\s+)?days?\b"))
                {
                    string value = asserted.Groups[1].Value.ToLowerInvariant();
                    int number = int.TryParse(value, out int numeric) ? numeric : Array.IndexOf(new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" }, value);
                    if (named.Count > 1 || (named.Count == 1 && number != ReadInt(named[0], "days", 0))) errors.Add("Spoken day counts must match one individual rival.");
                }
            }
            foreach (var count in counts)
            {
                var rival = ReadDictionaryList(brief, "rivals").FirstOrDefault(r => ReadString(r, "heroId", "") == count.Key);
                if (rival == null || ReadInt(counts, count.Key, -1) != ReadInt(rival, "days", 0)) errors.Add("Unsupported individual rival day count.");
            }
            if (Regex.IsMatch(reply, @"(?i)\b(affair|cheating|unfaithful|adultery)\b")) errors.Add("Attention is not evidence of an affair.");
            return errors;
        }

        private static Dictionary<string, object> EnforceApproachOpening(Dictionary<string, object> llm, Dictionary<string, object> request,
            Dictionary<string, object> payload, Func<Dictionary<string, object>, Dictionary<string, object>> responder = null,
            Func<Dictionary<string, object>, bool> validateRepair = null)
        {
            if (!ReadBool(llm, "ok", false)) return llm;
            var parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            var errors = ApproachOpeningViolations(parsed, payload);
            if (errors.Count == 0) return llm;
            var repair = CloneDictionary(request);
            var messages = ReadDictionaryList(request, "messages").Select(CloneDictionary).ToList();
            messages.Add(TestDict("role", "user", "content", "Repair this opening using the authoritative approach brief. Preserve the complete output schema. "
                + Json.Serialize(errors) + "\nBRIEF: " + Json.Serialize(ReadDictionary(payload, "authoritativeApproachBrief")) + "\nINVALID RESPONSE: " + ReadString(llm, "content", "")));
            repair["messages"] = messages; repair["temperature"] = 0d;
            var result = (responder ?? ChatWithLlm)(repair);
            var repaired = TryParseJsonObject(ReadString(result, "content", ""));
            if (ReadBool(result, "ok", false) && repaired != null && ApproachOpeningViolations(repaired, payload).Count == 0
                && (validateRepair == null || validateRepair(repaired))) return result;
            return TestDict("ok", false, "error", "The approach opening did not match its witnessed attention history. You may retry.", "approachOpeningFailed", true);
        }
    }
}
