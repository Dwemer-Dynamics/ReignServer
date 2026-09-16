using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        internal static void AppendFamilyVisitSelfTests(List<Dictionary<string, object>> results)
        {
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object> {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "court_system", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["data"] = new Dictionary<string, object>(), ["durationMs"] = 0 });
            add("family_attention_patience_boundaries", ReignFamilyVisitRules.DismissalTolerance(0) == 0
                && !ReignFamilyVisitRules.IsNeglected(0, 0) && ReignFamilyVisitRules.IsNeglected(1, 0)
                && ReignFamilyVisitRules.DismissalTolerance(1) == 1 && ReignFamilyVisitRules.DismissalTolerance(10) == 1
                && ReignFamilyVisitRules.DismissalTolerance(11) == 2 && ReignFamilyVisitRules.DismissalTolerance(40) == 4
                && ReignFamilyVisitRules.DismissalTolerance(41) == 5 && ReignFamilyVisitRules.DismissalTolerance(50) == 5
                && ReignFamilyVisitRules.DismissalTolerance(100) == 10,
                "Patience 0, 1, 10, 11, 40, 41, 50 and 100 uses ceiling percentage divided by ten; zero patience requires a real dismissal.");
            add("family_attention_calendar_and_decay", ReignFamilyVisitRules.CourtDay(10.32d) == 9
                && ReignFamilyVisitRules.CourtDay(10d + 8d / 24d) == 10
                && ReignFamilyVisitRules.DecayDelta(3, 5) == -3 && ReignFamilyVisitRules.DecayDelta(-2, 10) == 0
                && ReignFamilyVisitRules.DecayDelta(0, 10) == 0, "Morning boundaries and directional decay preserve zero and negative relations.");
            add("family_effective_relation_offset_boundaries", ReignFamilyVisitRules.PersonalAffinityForEffectiveRelation(0, 20) == -20
                && ReignFamilyVisitRules.PersonalAffinityForEffectiveRelation(0, -20) == 20
                && ReignFamilyVisitRules.PersonalAffinityForEffectiveRelation(99, 20) == 79,
                "The desired effective relation is preserved under positive, negative and upper-clamped public-standing offsets.");
            add("favor_contact_clock_boundaries", !ReignFamilyVisitRules.FavorExpired(5, 34.999)
                && ReignFamilyVisitRules.FavorExpired(5, 35) && ReignFamilyVisitRules.FavorJealousyWeight(true, true) == 0.5
                && ReignFamilyVisitRules.FavorJealousyWeight(true, false) == 1, "Exactly thirty days expires favor; shared current favor halves only attention jealousy weight.");
            var accepted = new Dictionary<string, object> { ["accepted"] = true, ["playerQuote"] = "Please forgive my neglect", ["npcQuote"] = "I forgive you" };
            Func<bool, string, Dictionary<string, object>> courtTurn = (actual, phase) => new Dictionary<string, object> {
                ["payload_json"] = Json.Serialize(new Dictionary<string, object> { ["conversationMode"] = "court_life", ["actualPlayerTurn"] = actual, ["courtLifePhase"] = phase }) };
            add("favor_real_speech_not_scene_cues", IsQualifyingFavorConversationTurn(courtTurn(true, "conversation"))
                && !IsQualifyingFavorConversationTurn(courtTurn(false, "opening")) && !IsQualifyingFavorConversationTurn(courtTurn(true, "closing"))
                && IsPersonalFavorLetter(new Dictionary<string, object> { ["source"] = "npc_reply" })
                && !IsPersonalFavorLetter(new Dictionary<string, object> { ["source"] = "campaign_command_urgent_report" }),
                "Completed court conversation and personal replies qualify; scene cues, closings and automatic reports do not.");
            add("family_reconciliation_bound_evidence", FamilyReconciliationEvidenceAccepted(accepted, "Please forgive my neglect.", "I forgive you.")
                && !FamilyReconciliationEvidenceAccepted(accepted, "A gift for you.", "Thank you.")
                && !FamilyReconciliationEvidenceAccepted(new Dictionary<string, object> { ["accepted"] = false, ["playerQuote"] = "forgive", ["npcQuote"] = "perhaps" }, "forgive me", "perhaps later"),
                "Reconciliation requires accepted semantic evidence bound to the actual latest utterances; gift prose alone cannot trigger it.");
            string campaign = "__family_visits_" + Guid.NewGuid().ToString("N"), timeline = "family_contract";
            var child = new Dictionary<string, object> { ["heroStringId"] = "child", ["name"] = "Child", ["age"] = 4d,
                ["isChild"] = true, ["isAlive"] = true, ["isPrisoner"] = false, ["isLocal"] = true, ["fatherId"] = "ruler", ["relationToPlayer"] = 3 };
            Func<string, double, string, Dictionary<string, object>> command = (action, day, id) => new Dictionary<string, object> {
                ["campaignId"] = campaign, ["timelineId"] = timeline, ["playerId"] = "ruler", ["action"] = action,
                ["worldDay"] = day, ["visitId"] = id, ["hero"] = child, ["familyProfiles"] = new List<object> { child } };
            Func<string, double, string, Dictionary<string, object>> run = (action, day, id) => FamilyVisitDispatch(command(action, day, id));
            Func<Dictionary<string, object>, Dictionary<string, object>> member = result => ReadDictionaryList(result, "members").FirstOrDefault();
            try
            {
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsureSocialReputationSchema(c); EnsureFamilyVisitSchema(c);
                    ExecuteSql(c, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult,is_player,is_ruler,kingdom_id) VALUES('ruler','Ruler',1,1,1,1,'realm'),('child','Child',1,0,0,0,'realm');");
                }
                var underAge = new Dictionary<string, object>(child) { ["age"] = 3.999d };
                add("family_visit_exact_age_and_locality", FamilyVisitProfileEligible(child, "ruler") && !FamilyVisitProfileEligible(underAge, "ruler")
                    && !FamilyVisitProfileEligible(new Dictionary<string, object>(child) { ["isLocal"] = false }, "ruler")
                    && !FamilyChildhoodRetainedAtAdulthood(4d) && FamilyChildhoodRetainedAtAdulthood(5d),
                    "Fourth birthday and local availability gate solo docket visits; younger children remain in Family Chambers, and adulthood memory retention separately begins at age five.");
                var first = run("register", 10.4, "visit_one");
                add("family_attention_missing_patience_defaults_to_fifty", FamilyPatiencePercent(campaign, "child") == 50,
                    "The production trait reader defaults a missing patience trait to fifty percent.");
                var turn = command("turn", 10.5, "visit_one"); turn["turnId"] = "one";
                FamilyVisitDispatch(turn); FamilyVisitDispatch(turn);
                var missed = run("tick", 11.34, "");
                add("family_visit_one_turn_and_retry_dismissal", ReadBool(first, "ok", false) && ReadInt(member(missed), "dismissals", -1) == 1
                    && ReadInt(ReadDictionaryList(missed, "visits").FirstOrDefault(), "player_turns", -1) == 1,
                    "A duplicated player turn remains one turn and produces exactly one dismissal at the morning deadline.");
                run("register", 11.4, "visit_two");
                turn = command("turn", 11.5, "visit_two"); turn["turnId"] = "two-a"; FamilyVisitDispatch(turn);
                turn["turnId"] = "two-b"; FamilyVisitDispatch(turn); var attended = FamilyVisitDispatch(turn);
                add("family_visit_two_turns_repair_once", ReadInt(member(attended), "dismissals", -1) == 0,
                    "Two distinct successful turns remove one earlier dismissal; a repeated callback cannot remove another.");
                run("register", 12.4, "technical"); run("invalidate", 12.5, "technical");
                var invalid = run("tick", 13.34, "");
                add("family_visit_technical_invalidation", ReadInt(member(invalid), "dismissals", -1) == 0, "Technical invalidation never becomes dismissal.");
                Dictionary<string, object> neglected = null;
                for (int i = 0; i < 5; i++) { run("register", 14.4 + i, "missed_" + i); neglected = run("tick", 15.34 + i, ""); }
                add("family_neglect_threshold_and_stop", ReadInt(member(neglected), "neglected", 0) == 1
                    && ReadInt(member(neglected), "directional_relation", -1) == 3 && !ReadBool(run("register", 19.5, "forbidden"), "ok", true),
                    "Five neutral-patience dismissals stop future visits; relation decay starts on the following morning.");
                var decayed = run("tick", 20.34, ""); var sameDay = run("tick", 20.8, ""); var atZero = run("tick", 24.34, "");
                add("family_register_retry_after_neglect", ReadBool(run("register", 18.4, "missed_4"), "ok", false),
                    "Retrying an already accepted registration remains idempotent after that visit triggers neglect.");
                add("family_directional_daily_idempotency", ReadInt(member(decayed), "directional_relation", -1) == 2
                    && ReadInt(member(sameDay), "directional_relation", -1) == 2 && ReadInt(member(atZero), "directional_relation", -1) == 0,
                    "The child's actual family-response directional state decreases once per campaign day and catches up only to zero.");
                using (var c = OpenCampaignConnection(campaign))
                {
                    string context = BuildFamilyAttentionContext(c, campaign, timeline, "child", "ruler", 24.34);
                    add("family_child_neglect_prompt_reads_state", context.Contains("deeply neglected") && context.Contains("family attachment") && !context.Contains("dismissals=5"),
                        "Every child response reads neglect and its directional bond without adult romance or hidden numeric leakage.");
                    ApplyFamilyReconciliation(c, campaign, timeline, "ruler", "child", "reconciled", 24.5, accepted);
                    ApplyFamilyReconciliation(c, campaign, timeline, "ruler", "child", "reconciled", 24.5, accepted);
                }
                var reconciled = run("context", 24.6, "");
                add("family_reconciliation_preserves_relation", ReadInt(member(reconciled), "neglected", 1) == 0
                    && ReadInt(member(reconciled), "dismissals", -1) == 0 && ReadInt(member(reconciled), "directional_relation", -1) == 0,
                    "Accepted reconciliation clears neglect and dismissals exactly once without refunding lost relation.");
                var branch = command("context", 24.6, ""); branch["timelineId"] = "other_timeline"; branch["familyProfiles"] = new List<object>();
                add("family_attention_timeline_isolation", ReadDictionaryList(FamilyVisitDispatch(branch), "members").Count == 0, "A sibling campaign timeline cannot observe another timeline's family state.");
                branch["timelineId"] = timeline; branch["playerId"] = "successor";
                var succession = FamilyVisitDispatch(branch);
                add("family_attention_ruler_snapshot_isolation", ReadDictionaryList(succession, "members").Count == 0
                    && ReadString(succession, "campaignId", "") == campaign && ReadString(succession, "timelineId", "") == timeline
                    && ReadString(succession, "playerId", "") == "successor",
                    "A successor receives a separately scoped attention snapshot; client caches can reject the prior ruler's data.");
                AppendRulerFavorContactSelfTests(campaign, timeline, add);
                var catchup = command("register", 30.4, "late_tick"); catchup["timelineId"] = "catchup";
                FamilyVisitDispatch(catchup);
                using (var c = OpenCampaignConnection(campaign))
                    ExecuteSql(c, "UPDATE court_family_attention SET dismissals=4 WHERE timeline_id='catchup';");
                catchup["action"] = "tick"; catchup["worldDay"] = 34.34; catchup["visitIds"] = new List<string> { "late_tick" };
                var caughtUp = FamilyVisitDispatch(catchup);
                add("family_delayed_tick_preserves_elapsed_decay", ReadInt(member(caughtUp), "neglected", 0) == 1
                    && ReadInt(member(caughtUp), "directional_relation", -1) == 0
                    && ReadDictionaryList(caughtUp, "visits").Any(v => ReadString(v, "visit_id", "") == "late_tick" && ReadString(v, "status", "") == "dismissed"),
                    "Late delivery anchors neglect to the original morning and returns old pending visit status for native recovery.");
                AppendFamilyEffectiveRelationSelfTests(campaign, add);
            }
            catch (Exception ex) { add("family_visit_contract_exception", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }

        private static void AppendRulerFavorContactSelfTests(string campaign, string timeline, Action<string, bool, string> add)
        {
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsureSocialReputationSchema(c);
                ExecuteSql(c, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult,is_player,is_ruler,kingdom_id) VALUES('favorite','Favorite',1,1,0,0,'realm'),('foreign_ruler','Foreign Ruler',1,1,0,1,'foreign');");
                RecordRulerFavorContact(c, campaign, timeline, "ruler", "favorite", 1, "spoken-one");
                RecordRulerFavorContact(c, campaign, timeline, "ruler", "favorite", 29, "spoken-one");
                var contact = QuerySql(c, "SELECT * FROM court_ruler_favor_contact WHERE ruler_id='ruler' AND favorite_id='favorite';").First();
                add("favor_actual_contact_receipt_idempotency", ReadDouble(contact, "last_contact_day", -1) == 1, "Replaying an old completed exchange cannot refresh the favor clock.");
                var occurrence = new Dictionary<string, object> { ["timelineId"] = timeline, ["archetypeId"] = "ruler_favoring_dialogue", ["threadKey"] = "test_favor", ["sourceEventId"] = "favor-event",
                    ["worldDay"] = 1d, ["forceExposure"] = true, ["forcePromotion"] = true,
                    ["participants"] = new List<object> { new Dictionary<string, object> { ["subjectId"] = "ruler", ["role"] = "ruler", ["isPlayer"] = true,
                        ["isRuler"] = true, ["isAdult"] = true, ["isAlive"] = true, ["clanTier"] = 6, ["linkedHeroId"] = "favorite", ["linkedHeroName"] = "Favorite" } } };
                var registered = RegisterSocialOccurrence(c, campaign, occurrence);
                ExpireSocialRumors(c, campaign, timeline, 30.999);
                bool before = CurrentRulerFavorites(c, campaign, timeline, "ruler", 30.999).Contains("favorite");
                ExpireSocialRumors(c, campaign, timeline, 31);
                bool after = CurrentRulerFavorites(c, campaign, timeline, "ruler", 31).Contains("favorite");
                add("favor_actual_expiration_and_stale_acquisition", ReadBool(registered, "ok", false) && before && !after
                    && RulerFavorRequiresFreshContact(c, campaign, timeline, "ruler", "favorite"), "Established favor expires exactly at thirty days and stale proximity cannot reacquire it.");
                RecordRulerFavorContact(c, campaign, timeline, "ruler", "favorite", 2, "late-historical-exchange");
                add("favor_late_historical_speech_stays_forgotten", RulerFavorRequiresFreshContact(c, campaign, timeline, "ruler", "favorite"),
                    "A previously undelivered old exchange cannot remove the forgotten-favor barrier.");
                RecordRulerFavorContact(c, campaign, timeline, "favorite", "ruler", 31, "fresh-return");
                RecordRulerFavorContact(c, campaign, timeline, "favorite", "foreign_ruler", 31, "npc-exchange");
                add("favor_player_npc_ruler_bidirectional_contact", !RulerFavorRequiresFreshContact(c, campaign, timeline, "ruler", "favorite")
                    && QuerySql(c, "SELECT 1 FROM court_ruler_favor_contact WHERE ruler_id='foreign_ruler' AND favorite_id='favorite';").Any(),
                    "Fresh speech resumes favor eligibility and either side of a completed exchange may be an NPC ruler.");
                var npcOccurrence = new Dictionary<string, object>(occurrence) { ["worldDay"] = 31d, ["threadKey"] = "npc_favor", ["sourceEventId"] = "npc-favor-event",
                    ["participants"] = new List<object> { new Dictionary<string, object> { ["subjectId"] = "foreign_ruler", ["role"] = "ruler", ["isPlayer"] = false,
                        ["isRuler"] = true, ["isAdult"] = true, ["isAlive"] = true, ["clanTier"] = 6, ["linkedHeroId"] = "favorite", ["linkedHeroName"] = "Favorite" } } };
                var npcRegistered = RegisterSocialOccurrence(c, campaign, npcOccurrence);
                bool npcBefore = CurrentRulerFavorites(c, campaign, timeline, "foreign_ruler", 60.999).Contains("favorite");
                ExpireSocialRumors(c, campaign, timeline, 61);
                add("favor_npc_ruler_expiration", ReadBool(npcRegistered, "ok", false) && npcBefore
                    && !CurrentRulerFavorites(c, campaign, timeline, "foreign_ruler", 61).Contains("favorite"),
                    "An NPC ruler's parameterized favor expires at the same thirty-day contact boundary.");
                ExecuteSql(c, @"UPDATE character_reputations SET status='active' WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id='foreign_ruler';
DELETE FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id='foreign_ruler';",
                    new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline });
                ExpireSocialRumors(c, campaign, timeline, 100);
                ExpireSocialRumors(c, campaign, timeline, 129.999);
                bool graceActive = CurrentRulerFavorites(c, campaign, timeline, "foreign_ruler", 129.999).Contains("favorite");
                ExpireSocialRumors(c, campaign, timeline, 130);
                add("favor_legacy_unknown_contact_one_time_grace", graceActive
                    && !CurrentRulerFavorites(c, campaign, timeline, "foreign_ruler", 130).Contains("favorite"),
                    "Legacy favor without reliable contact receives one thirty-day grace period; later reads cannot move its anchor.");
            }
        }

        private static void AppendFamilyEffectiveRelationSelfTests(string campaign, Action<string, bool, string> add)
        {
            const string timeline = "effective_relation";
            using (var c = OpenCampaignConnection(campaign))
            {
                var profile = new Dictionary<string, object> { ["heroStringId"] = "adult_family", ["name"] = "Family", ["age"] = 30d,
                    ["isChild"] = false, ["isAlive"] = true, ["isPrisoner"] = false, ["isLocal"] = true, ["spouseId"] = "ruler", ["relationToPlayer"] = 5 };
                ExecuteSql(c, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult) VALUES('adult_family','Family',1,1);");
                ExecuteSql(c, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES('adult_family|ruler','adult_family','ruler','ESTJ','ISFJ',2,17,10,10,0);");
                UpsertFamilyAttention(c, campaign, timeline, "ruler", profile);
                var p = new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["offset"] = 3, ["affinity"] = 2 };
                Action<int, int> reset = (affinity, offset) => {
                    p["affinity"] = affinity; p["offset"] = offset;
                    ExecuteSql(c, @"INSERT INTO character_public_standing(campaign_id,timeline_id,subject_id,standing_value,updated_ts)
VALUES($campaign,$timeline,'ruler',$offset,0) ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET standing_value=$offset;
UPDATE relationship_pair_chemistry SET affinity_a_to_b=$affinity WHERE pair_key='adult_family|ruler';
UPDATE court_family_attention SET neglected=1,decay_finished=0,last_decay_day=10 WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id='adult_family';", p);
                };
                Func<int> effective = () => ReadInt(ResolveEffectiveAttitude(c, campaign, timeline, "adult_family", "ruler"), "effectiveAttitude", -999);
                reset(2, 3); AdvanceFamilyAttention(c, campaign, timeline, "ruler", 11.34, false);
                int positiveFirst = effective(); AdvanceFamilyAttention(c, campaign, timeline, "ruler", 16.34, false);
                var positivePair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key='adult_family|ruler';").First();
                add("family_neglect_positive_standing_reaches_actual_zero", positiveFirst == 4 && effective() == 0
                    && ReadInt(positivePair, "affinity_a_to_b", 999) == -3 && ReadInt(positivePair, "affinity_b_to_a", 999) == 17
                    && PublicStandingValue(c, campaign, timeline, "ruler") == 3,
                    "Actual NPC-to-ruler relation drops one per day to zero; positive standing and reverse relation remain unchanged.");
                reset(5, -3); AdvanceFamilyAttention(c, campaign, timeline, "ruler", 11.34, false);
                int negativeFirst = effective(); AdvanceFamilyAttention(c, campaign, timeline, "ruler", 16.34, false);
                add("family_neglect_negative_standing_stops_at_actual_zero", negativeFirst == 1 && effective() == 0
                    && ReadInt(QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key='adult_family|ruler';").First(), "affinity_a_to_b", 999) == 3
                    && PublicStandingValue(c, campaign, timeline, "ruler") == -3,
                    "Negative standing stops decay at actual zero while personal affinity remains positive.");
                reset(100, 20); AdvanceFamilyAttention(c, campaign, timeline, "ruler", 11.34, false);
                add("family_neglect_upper_clamp_drops_one_actual_point", effective() == 99 && PublicStandingValue(c, campaign, timeline, "ruler") == 20,
                    "A relation clamped at 100 still falls to 99 on the first day without changing public standing.");
                reset(-2, -3); AdvanceFamilyAttention(c, campaign, timeline, "ruler", 14.34, false);
                add("family_neglect_preserves_already_negative_relation", effective() == -5
                    && ReadInt(ReadFamilyAttention(c, campaign, timeline, "ruler", "adult_family"), "decay_finished", 0) == 1,
                    "Already negative actual relation remains unchanged and stops the neglect-decay episode.");
                // This fixture stands after normal adulthood construction; the persistent child-owned flag survives the updated profile.
                p["offset"] = 3;
                ExecuteSql(c, @"UPDATE character_public_standing SET standing_value=3 WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id='ruler';
UPDATE court_family_attention SET child_relation_owned=1,adult_migrated=0,directional_relation=0 WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id='adult_family';", p);
                MigrateFamilyChildAttentionRelation(c, campaign, timeline, "adult_family", 18.34);
                MigrateFamilyChildAttentionRelation(c, campaign, timeline, "adult_family", 18.34);
                add("family_child_maturity_preserves_actual_zero_with_standing", effective() == 0
                    && ReadInt(ReadFamilyAttention(c, campaign, timeline, "ruler", "adult_family"), "adult_migrated", 0) == 1
                    && PublicStandingValue(c, campaign, timeline, "ruler") == 3,
                    "Child attention migrates once to the same actual adult directional relation, including zero under public standing.");
            }
        }
    }
}
