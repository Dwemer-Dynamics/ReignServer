using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureFamilyVisitSchema(ReignDbConnection c)
        {
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS court_family_attention (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,hero_id TEXT NOT NULL,
dismissals INTEGER NOT NULL DEFAULT 0,neglected INTEGER NOT NULL DEFAULT 0,decay_finished INTEGER NOT NULL DEFAULT 0,
last_decay_day INTEGER NOT NULL DEFAULT -1,directional_relation INTEGER NOT NULL DEFAULT 0,
adult_migrated INTEGER NOT NULL DEFAULT 0,profile_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(campaign_id,timeline_id,player_id,hero_id));");
            EnsureDatabaseColumn(c, "court_family_attention", "child_relation_owned", "INTEGER NOT NULL DEFAULT 0");
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS court_family_visits (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,visit_id TEXT NOT NULL,player_id TEXT NOT NULL,hero_id TEXT NOT NULL,
created_day REAL NOT NULL,deadline REAL NOT NULL,status TEXT NOT NULL DEFAULT 'pending',player_turns INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(campaign_id,timeline_id,visit_id));");
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS court_family_visit_turns (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,visit_id TEXT NOT NULL,turn_id TEXT NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,visit_id,turn_id));");
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS court_family_reconciliations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,exchange_id TEXT NOT NULL,
player_quote TEXT NOT NULL,npc_quote TEXT NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,hero_id,exchange_id));");
        }

        private static Dictionary<string, object> FamilyVisitDispatch(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaign = ReadString(payload, "campaignId", "default"), timeline = ReadString(payload, "timelineId", "main");
            string player = ReadFirstString(payload, "playerId", "playerHeroStringId");
            double day = ReadDouble(payload, "worldDay", 0d);
            string action = ReadString(payload, "action", "tick");
            if (string.IsNullOrWhiteSpace(player)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "player_required" };
            lock (CampaignRelationshipWriteLock(campaign))
            using (ReignDbConnection c = OpenCampaignConnection(campaign))
            {
                EnsureSocialReputationSchema(c);
                EnsureFamilyVisitSchema(c);
                ExecuteSql(c, "BEGIN IMMEDIATE;");
                try
                {
                    var p = new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["player"] = player,
                        ["visit"] = ReadString(payload, "visitId", ""), ["day"] = day };
                    foreach (var profile in ReadDictionaryList(payload, "familyProfiles")) UpsertFamilyAttention(c, campaign, timeline, player, profile);
                    if (action == "register")
                    {
                        var profile = ReadDictionary(payload, "hero");
                        string hero = CharacterIdFrom(profile);
                        var existing = QuerySql(c, @"SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", p).FirstOrDefault();
                        if (existing != null)
                        {
                            if (ReadString(existing, "hero_id", "") != hero || ReadString(existing, "player_id", "") != player)
                                throw new InvalidOperationException("family_visit_identity_mismatch");
                        }
                        else
                        {
                        if (!FamilyVisitProfileEligible(profile, player)) throw new InvalidOperationException("ineligible_family_visitor");
                        UpsertFamilyAttention(c, campaign, timeline, player, profile);
                        var member = ReadFamilyAttention(c, campaign, timeline, player, hero);
                        if (ReadInt(member, "neglected", 0) != 0) throw new InvalidOperationException("family_visit_unavailable");
                        p["hero"] = hero;
                        p["deadline"] = ReadDouble(payload, "deadline", ReignFamilyVisitRules.NextCourtMorning(day));
                        if (string.IsNullOrWhiteSpace(ReadString(p, "visit", "")) || ReadDouble(p, "deadline", 0d) <= day)
                            throw new InvalidOperationException("family_visit_deadline_invalid");
                        ExecuteSql(c, @"INSERT INTO court_family_visits(campaign_id,timeline_id,visit_id,player_id,hero_id,created_day,deadline)
VALUES($campaign,$timeline,$visit,$player,$hero,$day,$deadline) ON CONFLICT(campaign_id,timeline_id,visit_id) DO NOTHING;", p);
                        }
                    }
                    else if (action == "turn") RecordFamilyAttentionTurn(c, campaign, timeline, player, payload);
                    else if (action == "invalidate")
                        ExecuteSql(c, @"UPDATE court_family_visits SET status='invalidated' WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND visit_id=$visit AND status='pending';", p);
                    else if (action == "tick")
                    {
                        AdvanceFamilyAttention(c, campaign, timeline, player, day, ReadBool(payload, "courtUnavailable", false));
                        ExpireSocialRumors(c, campaign, timeline, day);
                    }
                    else if (action != "context") throw new InvalidOperationException("unknown_family_visit_action");
                    var members = QuerySql(c, "SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player;", p);
                    foreach (var member in members)
                    {
                        string hero = ReadString(member, "hero_id", "");
                        var profile = TryParseJsonObject(ReadString(member, "profile_json", "{}"));
                        member["patiencePercent"] = FamilyPatiencePercent(campaign, hero);
                        member["context"] = BuildFamilyAttentionContext(c, campaign, timeline, hero, player, day);
                        member["jealousy"] = FamilyJealousyEvidence(c, campaign, timeline, hero, player, profile, day);
                    }
                    var requestedVisits = ReadStringList(payload, "visitIds").Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(256).ToList();
                    var visitParameters = new List<string>();
                    for (int i = 0; i < requestedVisits.Count; i++) { p["requestedVisit" + i] = requestedVisits[i]; visitParameters.Add("$requestedVisit" + i); }
                    string requestedClause = visitParameters.Count == 0 ? "" : " OR visit_id IN (" + string.Join(",", visitParameters) + ")";
                    var result = new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaign, ["timelineId"] = timeline, ["playerId"] = player, ["members"] = members,
                        ["visits"] = QuerySql(c, "SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND (deadline>$day-2" + requestedClause + ");", p),
                        ["worldDay"] = day };
                    if (ReadBool(payload, "includeJealousy", false))
                    {
                        result["jealousyCandidates"] = CourtJealousyCandidates(c, campaign, timeline, player, day);
                        result["jealousyDay"] = ReignFamilyVisitRules.CourtDay(day);
                    }
                    ExecuteSql(c, "COMMIT;");
                    return result;
                }
                catch (Exception ex)
                {
                    ExecuteSql(c, "ROLLBACK;");
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
                }
            }
        }

        private static int FamilyPatiencePercent(string campaign, string hero) => (int)Math.Ceiling(
            RelationshipTraitPercent(ReadJsonObject(CharacterFile(campaign, hero, "traits.json")), "patience"));

        private static bool FamilyVisitProfileEligible(Dictionary<string, object> profile, string player)
        {
            if (profile == null || string.IsNullOrWhiteSpace(CharacterIdFrom(profile)) || !ReadBool(profile, "isAlive", false)
                || ReadBool(profile, "isPrisoner", false) || !ReadBool(profile, "isLocal", false) || ReadDouble(profile, "age", 0d) < ReignFamilyVisitRules.MinimumVisitorAge) return false;
            return ReadString(profile, "spouseId", "") == player || ReadString(profile, "fatherId", "") == player || ReadString(profile, "motherId", "") == player;
        }

        private static void UpsertFamilyAttention(ReignDbConnection c, string campaign, string timeline, string player, Dictionary<string, object> profile)
        {
            string hero = CharacterIdFrom(profile);
            if (string.IsNullOrWhiteSpace(hero) || !(ReadString(profile, "spouseId", "") == player
                || ReadString(profile, "fatherId", "") == player || ReadString(profile, "motherId", "") == player)) return;
            ExecuteSql(c, @"INSERT INTO court_family_attention(campaign_id,timeline_id,player_id,hero_id,directional_relation,profile_json,child_relation_owned)
VALUES($campaign,$timeline,$player,$hero,$relation,$profile,$child) ON CONFLICT(campaign_id,timeline_id,player_id,hero_id)
DO UPDATE SET profile_json=$profile;", new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline,
                ["player"] = player, ["hero"] = hero, ["relation"] = Clamp(ReadInt(profile, "relationToPlayer", 0), -100, 100),
                ["child"] = ReadBool(profile, "isChild", false) ? 1 : 0, ["profile"] = Json.Serialize(profile) });
        }

        private static Dictionary<string, object> ReadFamilyAttention(ReignDbConnection c, string campaign, string timeline, string player, string hero)
        {
            EnsureFamilyVisitSchema(c);
            return QuerySql(c, @"SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND hero_id=$hero;", new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline,
                ["player"] = player, ["hero"] = hero }).FirstOrDefault();
        }

        private static void RecordFamilyAttentionTurn(ReignDbConnection c, string campaign, string timeline, string player, Dictionary<string, object> payload)
        {
            string turn = ReadString(payload, "turnId", "");
            if (string.IsNullOrWhiteSpace(turn)) throw new InvalidOperationException("completed_player_turn_required");
            var p = new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["player"] = player,
                ["visit"] = ReadString(payload, "visitId", ""), ["turn"] = turn, ["day"] = ReadDouble(payload, "worldDay", 0d) };
            var visit = QuerySql(c, @"SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND visit_id=$visit;", p).FirstOrDefault();
            if (visit == null) throw new InvalidOperationException("family_visit_not_registered");
            if (ReadString(visit, "status", "") != "pending" || ReadDouble(p, "day", 0d) >= ReadDouble(visit, "deadline", 0d)) return;
            ExecuteSql(c, @"INSERT INTO court_family_visit_turns(campaign_id,timeline_id,visit_id,turn_id,world_day)
VALUES($campaign,$timeline,$visit,$turn,$day) ON CONFLICT(campaign_id,timeline_id,visit_id,turn_id) DO NOTHING;", p);
            int count = ReadInt(QuerySql(c, @"SELECT COUNT(*) AS n FROM court_family_visit_turns WHERE campaign_id=$campaign
AND timeline_id=$timeline AND visit_id=$visit;", p).First(), "n", 0);
            p["count"] = count;
            ExecuteSql(c, @"UPDATE court_family_visits SET player_turns=$count WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", p);
            RecordRulerFavorContact(c, campaign, timeline, player, ReadString(visit, "hero_id", ""), ReadDouble(p, "day", 0d), "family_visit:" + turn);
            if (count < ReignFamilyVisitRules.RequiredPlayerTurns) return;
            p["hero"] = ReadString(visit, "hero_id", "");
            ExecuteSql(c, @"UPDATE court_family_attention SET dismissals=MAX(0,dismissals-1)
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", p);
            ExecuteSql(c, @"UPDATE court_family_visits SET status='attended' WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", p);
        }

        private static void AdvanceFamilyAttention(ReignDbConnection c, string campaign, string timeline, string player, double day, bool courtUnavailable)
        {
            int today = ReignFamilyVisitRules.CourtDay(day);
            var p = new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["player"] = player, ["day"] = day, ["today"] = today };
            foreach (var visit in QuerySql(c, @"SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND status='pending' AND deadline<=$day ORDER BY deadline,visit_id;", p))
            {
                string hero = ReadString(visit, "hero_id", "");
                var member = ReadFamilyAttention(c, campaign, timeline, player, hero);
                var profile = TryParseJsonObject(ReadString(member, "profile_json", "{}"));
                p["hero"] = hero; p["visit"] = ReadString(visit, "visit_id", "");
                bool invalid = courtUnavailable || !FamilyVisitProfileEligible(profile, player);
                p["status"] = invalid ? "invalidated" : "dismissed";
                ExecuteSql(c, @"UPDATE court_family_visits SET status=$status WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", p);
                if (invalid) continue;
                int dismissals = ReadInt(member, "dismissals", 0) + 1;
                bool neglected = ReignFamilyVisitRules.IsNeglected(dismissals, FamilyPatiencePercent(campaign, hero));
                p["dismissals"] = dismissals; p["neglected"] = neglected ? 1 : 0;
                p["neglectStartDay"] = ReignFamilyVisitRules.CourtDay(ReadDouble(visit, "deadline", day));
                ExecuteSql(c, @"UPDATE court_family_attention SET dismissals=$dismissals,neglected=$neglected,
decay_finished=CASE WHEN neglected=0 AND $neglected=1 THEN 0 ELSE decay_finished END,
last_decay_day=CASE WHEN neglected=0 AND $neglected=1 THEN $neglectStartDay ELSE last_decay_day END
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", p);
            }
            foreach (var member in QuerySql(c, @"SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND neglected=1 AND decay_finished=0 AND last_decay_day<$today;", p))
            {
                string hero = ReadString(member, "hero_id", "");
                var profile = TryParseJsonObject(ReadString(member, "profile_json", "{}"));
                if (!ReadBool(profile, "isAlive", false)) continue;
                int relation = ReadInt(member, "directional_relation", 0);
                bool child = ReadBool(profile, "isChild", false) || (ReadInt(member, "child_relation_owned", 0) != 0 && ReadInt(member, "adult_migrated", 0) == 0);
                var pair = child ? null : QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = AmbientPairKey(hero, player) }).FirstOrDefault();
                if (!child)
                {
                    if (pair == null) SetFamilyDirectionalRelation(c, campaign, timeline, hero, player, relation, day, "family_attention_initial_relation");
                    relation = ReadInt(ResolveEffectiveAttitude(c, campaign, timeline, hero, player, "family_neglect"), "effectiveAttitude", relation);
                }
                int delta = ReignFamilyVisitRules.DecayDelta(relation, today - ReadInt(member, "last_decay_day", today));
                if (!child && delta != 0)
                    relation = SetFamilyDirectionalRelation(c, campaign, timeline, hero, player, relation + delta, day, "family_neglect");
                else relation += delta;
                p["hero"] = hero; p["relation"] = relation; p["finished"] = relation <= 0 ? 1 : 0;
                ExecuteSql(c, @"UPDATE court_family_attention SET directional_relation=$relation,last_decay_day=$today,decay_finished=$finished
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", p);
            }
        }

        private static string BuildFamilyAttentionContext(ReignDbConnection c, string campaign, string timeline, string observer, string player, double day)
        {
            var member = ReadFamilyAttention(c, campaign, timeline, player, observer);
            if (member == null) return "";
            var profile = TryParseJsonObject(ReadString(member, "profile_json", "{}"));
            bool child = ReadBool(profile, "isChild", false);
            string context = child ? "Your bond toward your parent is " + RelationshipBand(ReadInt(member, "directional_relation", 0))
                + ". Express affection or distance at your exact age; this is family attachment, never adult romance or court politics. " : "";
            if (ReadInt(member, "neglected", 0) != 0)
                context += "You feel deeply neglected by your parent or spouse after repeated dismissals. This affects how you speak even about other subjects: show hurt, distance, disappointment or a wish for attention according to your age and personality. Do not instantly forgive ordinary politeness or a gift. You can accept a sincere explicit reconciliation when it suits your response. Never mention counters, thresholds, tags, or daily mechanical changes. ";
            var jealousy = FamilyJealousyEvidence(c, campaign, timeline, observer, player, profile, day);
            if (ReadBool(jealousy, "eligible", false)) context += ReadString(jealousy, "context", "");
            return context;
        }

        private static Dictionary<string, object> FamilyJealousyEvidence(ReignDbConnection c, string campaign, string timeline,
            string observer, string player, Dictionary<string, object> profile, double day)
        {
            var result = new Dictionary<string, object> { ["eligible"] = false };
            bool child = ReadBool(profile, "isChild", false), spouse = ReadString(profile, "spouseId", "") == player;
            bool offspring = ReadString(profile, "fatherId", "") == player || ReadString(profile, "motherId", "") == player;
            if (!offspring && !spouse) return result;
            var favorites = CurrentRulerFavorites(c, campaign, timeline, player, day);
            var ruler = QuerySql(c, "SELECT sex FROM identity_roster WHERE hero_id=$id;", new Dictionary<string, object> { ["id"] = player }).FirstOrDefault();
            foreach (string rival in favorites.Where(id => id != observer))
            {
                // Public reputation still requires a known person; private romance is never inferred.
                if (!IdentityStateVerified(ReadAcquaintance(c, observer, rival))) continue;
                var target = QuerySql(c, "SELECT * FROM identity_roster WHERE hero_id=$id;", new Dictionary<string, object> { ["id"] = rival }).FirstOrDefault();
                if (target == null || ReadInt(target, "is_alive", 0) == 0 || ReadInt(target, "is_adult", 0) == 0) continue;
                if (spouse && (ruler == null || ReadString(target, "sex", "") == ReadString(ruler, "sex", ""))) continue;
                double temperament = child ? 0.5d : RelationshipTraitPercent(ReadJsonObject(CharacterFile(campaign, observer, "traits.json")), "jealousy") / 100d;
                double weight = temperament * ReignFamilyVisitRules.FavorJealousyWeight(favorites.Contains(observer), true);
                string name = ReadString(target, "canonical_name", "the courtier");
                result["eligible"] = weight > 0d; result["weight"] = weight; result["rivalId"] = rival; result["rivalName"] = name;
                result["context"] = offspring ? "You notice the attention your parent gives " + name + " and may want some time of your own. Express displaced family attention only, without romantic jealousy."
                    : "You know that " + name + " receives the ruler's favor. This may color your need for reassurance according to your jealousy, trust and tact. Favor and rumors do not prove an affair."
                      + (favorites.Contains(observer) ? " You also receive favor, which softens resentment about attention." : "");
                return result;
            }
            return result;
        }

        private static List<Dictionary<string, object>> CourtJealousyCandidates(ReignDbConnection c, string campaign, string timeline, string player, double day)
        {
            var result = new List<Dictionary<string, object>>();
            var favorites = CurrentRulerFavorites(c, campaign, timeline, player, day);
            if (favorites.Count == 0) return result;
            foreach (var observer in QuerySql(c, @"SELECT * FROM identity_roster WHERE is_alive=1 AND is_adult=1 AND is_player=0 AND is_lord=1
AND kingdom_id=(SELECT kingdom_id FROM identity_roster WHERE hero_id=$player) ORDER BY hero_id;", new Dictionary<string, object> { ["player"] = player }))
            {
                string id = ReadString(observer, "hero_id", "");
                if (ReadInt(ReadFamilyAttention(c, campaign, timeline, player, id), "neglected", 0) != 0) continue;
                string rival = favorites.FirstOrDefault(target => target != id && IdentityStateVerified(ReadAcquaintance(c, id, target)));
                if (rival == null) continue;
                double weight = RelationshipTraitPercent(ReadJsonObject(CharacterFile(campaign, id, "traits.json")), "jealousy") / 100d
                    * ReignFamilyVisitRules.FavorJealousyWeight(favorites.Contains(id), true);
                if (weight <= 0d) continue;
                result.Add(new Dictionary<string, object> { ["heroId"] = id, ["rivalId"] = rival, ["weight"] = weight,
                    ["sharedFavor"] = favorites.Contains(id), ["context"] = "You know another courtier receives the ruler's attention. Seek reassurance, recognition or fair credit according to your jealousy, honor and tact. Do not invent an affair or hidden wrongdoing. "
                        + (favorites.Contains(id) ? "You are also favored, so you have less reason to resent neglect." : "") });
            }
            return result;
        }

        private static void ProcessFamilyReconciliationReply(Dictionary<string, object> payload, Dictionary<string, object> response)
        {
            if (!ReadBool(payload, "familyChambers", false) || ReadBool(payload, "familyVisitDocket", false)
                || !ReadBool(response, "ok", false) || ReadBool(response, "fallback", false)) return;
            string campaign = ReadString(payload, "campaignId", "default"), timeline = ReadString(payload, "timelineId", "main");
            string player = ReadFirstString(payload, "playerHeroStringId", "playerId"), hero = CharacterIdFrom(ReadDictionary(payload, "speaker"));
            string playerText = ReadString(payload, "playerText", ""), reply = ReadString(response, "reply", "");
            string exchange = ReadString(payload, "turnId", "");
            if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(playerText) || string.IsNullOrWhiteSpace(reply)) return;
            using (var c = OpenCampaignConnection(campaign))
            {
                var member = ReadFamilyAttention(c, campaign, timeline, player, hero);
                if (ReadInt(member, "neglected", 0) == 0) return;
                var llm = ChatWithLlm(new Dictionary<string, object> { ["campaignId"] = campaign, ["requestType"] = "family_reconciliation",
                    ["temperature"] = 0d, ["maxTokens"] = 500,
                    ["messages"] = BuildSimplePromptEnvelope("family_reconciliation", "classify",
                        "Classify an actual completed family exchange. Never decide or invent consent. Return JSON only.",
                        "Player: " + playerText + "\nFamily member: " + reply + "\nRecent context: " + Json.Serialize(ReadDictionaryList(payload, "groupTranscript").Skip(Math.Max(0, ReadDictionaryList(payload, "groupTranscript").Count - 8)).ToList())
                        + "\nReturn {accepted:false,playerQuote:'',npcQuote:''}. Classify whether reconciliation is accepted NOW, not whether trust or affection has fully recovered. "
                        + "Require a sincere player attempt to repair neglect and the family member's explicit present acceptance of that apology or reconciliation. "
                        + "A direct request such as 'Will you accept my apology and let us begin again?' is a valid attempt when the recent exchange establishes neglect. "
                        + "Present acceptance such as 'I accept your apology' or 'I can accept your apology, as a beginning' remains acceptance even when rebuilding trust will take time or the NPC asks for future follow-through. "
                        + "Do not mistake conditions on FUTURE TRUST for conditions that withhold PRESENT ACCEPTANCE. This does not refund lost relation. "
                        + "Return false for ordinary politeness, gifts, thanks, affection or promises alone; a hypothetical or sarcastic apology; refusal; or acceptance deferred until a condition is met, such as 'Perhaps I will forgive you if you return tomorrow.' "
                        + "Permission to stay or willingness to consider reconciliation without explicit acceptance is false. "
                        + "Dialogue quotation marks around the NPC's own current speech are normal; retelling someone else's apology or imagining a future exchange is not current reconciliation. "
                        + "Quotes must be exact nonempty spans from the actual latest respective speakers; use recent context only to disambiguate intent, never to substitute an earlier acceptance or reconstruct quotation.").Messages,
                    ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" } });
                if (!ReadBool(llm, "ok", false)) return;
                var evidence = TryParseJsonObject(ReadString(llm, "content", ""));
                if (!FamilyReconciliationEvidenceAccepted(evidence, playerText, reply)) return;
                ApplyFamilyReconciliation(c, campaign, timeline, player, hero, exchange, ReadDouble(payload, "worldDay", 0d), evidence);
            }
        }

        internal static bool FamilyReconciliationEvidenceAccepted(Dictionary<string, object> evidence, string playerText, string reply)
        {
            string playerQuote = ReadString(evidence, "playerQuote", ""), npcQuote = ReadString(evidence, "npcQuote", "");
            return ReadBool(evidence, "accepted", false) && playerQuote.Length >= 3 && npcQuote.Length >= 3
                && (playerText ?? "").IndexOf(playerQuote, StringComparison.Ordinal) >= 0 && (reply ?? "").IndexOf(npcQuote, StringComparison.Ordinal) >= 0;
        }

        private static void ApplyFamilyReconciliation(ReignDbConnection c, string campaign, string timeline, string player, string hero,
            string exchange, double day, Dictionary<string, object> evidence)
        {
            lock (CampaignRelationshipWriteLock(campaign))
            {
                var p = new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["player"] = player,
                    ["hero"] = hero, ["exchange"] = exchange, ["day"] = day,
                    ["playerQuote"] = ReadString(evidence, "playerQuote", ""), ["npcQuote"] = ReadString(evidence, "npcQuote", "") };
                ExecuteSql(c, "BEGIN IMMEDIATE;");
                try
                {
                    if (!QuerySql(c, @"SELECT 1 FROM court_family_reconciliations WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND exchange_id=$exchange;", p).Any())
                    {
                        ExecuteSql(c, @"INSERT INTO court_family_reconciliations(campaign_id,timeline_id,hero_id,exchange_id,player_quote,npc_quote,world_day)
VALUES($campaign,$timeline,$hero,$exchange,$playerQuote,$npcQuote,$day);", p);
                        ExecuteSql(c, @"UPDATE court_family_attention SET dismissals=0,neglected=0,decay_finished=1
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", p);
                    }
                    ExecuteSql(c, "COMMIT;");
                }
                catch { ExecuteSql(c, "ROLLBACK;"); throw; }
            }
        }

        private static void MigrateFamilyChildAttentionRelation(ReignDbConnection c, string campaign, string timeline, string hero, double day)
        {
            EnsureFamilyVisitSchema(c);
            lock (CampaignRelationshipWriteLock(campaign))
            {
            ExecuteSql(c, "BEGIN IMMEDIATE;");
            try
            {
            foreach (var member in QuerySql(c, @"SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND hero_id=$hero AND adult_migrated=0;", new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["hero"] = hero }))
            {
                if (ReadInt(member, "child_relation_owned", 0) == 0) continue;
                string player = ReadString(member, "player_id", "");
                int migrated = SetFamilyDirectionalRelation(c, campaign, timeline, hero, player, ReadInt(member, "directional_relation", 0), day, "family_childhood_relation_migration");
                ExecuteSql(c, @"UPDATE court_family_attention SET adult_migrated=1,directional_relation=$relation WHERE campaign_id=$campaign AND timeline_id=$timeline
AND hero_id=$hero AND player_id=$player;", new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["hero"] = hero, ["player"] = player, ["relation"] = migrated });
            }
            ExecuteSql(c, "COMMIT;");
            }
            catch { ExecuteSql(c, "ROLLBACK;"); throw; }
            }
        }

        private static int SetFamilyDirectionalRelation(ReignDbConnection c, string campaign, string timeline,
            string observer, string player, int desiredEffectiveRelation, double day, string reason)
        {
            var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                new Dictionary<string, object> { ["pair"] = AmbientPairKey(observer, player) }).FirstOrDefault();
            int personal = ReadInt(pair, ReadString(pair, "hero_a_id", "") == observer ? "affinity_a_to_b" : "affinity_b_to_a", 0);
            int standing = ReadInt(ResolveObserverPublicStanding(c, campaign, timeline, observer, player), "value", 0);
            int desiredPersonal = ReignFamilyVisitRules.PersonalAffinityForEffectiveRelation(desiredEffectiveRelation, standing);
            ApplyAuthoritativeRelationshipDelta(c, campaign, observer, player, desiredPersonal - personal, day, reason, timeline, ensurePair: true);
            return ReadInt(ResolveEffectiveAttitude(c, campaign, timeline, observer, player, reason), "effectiveAttitude", desiredEffectiveRelation);
        }
    }
}
