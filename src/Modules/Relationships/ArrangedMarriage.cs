using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reign.Relationships;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static void EnsureArrangedMarriageSchema(ReignDbConnection c)
        {
            RelationshipsSchema.CreateArrangedMarriagePeriods(c);
            RelationshipsSchema.CreateArrangedMarriageEffects(c);
            RelationshipsSchema.CreateArrangedMarriageRefusals(c);
        }

        private static bool MarriageRoyal(Dictionary<string, object> hero, List<Dictionary<string, object>> kingdoms)
            => ReadBool(hero,"isRulingClanMember",false) || kingdoms.Any(k =>
                ReadString(k,"leaderId","")!="" && (ReadString(k,"leaderId","")==ReadString(hero,"fatherId","")
                    || ReadString(k,"leaderId","")==ReadString(hero,"motherId","")));

        internal static Dictionary<string, object> MarriageQuote(string campaign, Dictionary<string, object> a,
            Dictionary<string, object> b, Dictionary<string, object> ca, Dictionary<string, object> cb,
            List<Dictionary<string, object>> kingdoms, int year)
        {
            int fa = ReadInt(ca,"fiefCount",ReadInt(a,"clanFiefCount",-1));
            int fb = ReadInt(cb,"fiefCount",ReadInt(b,"clanFiefCount",-1));
            bool ra=MarriageRoyal(a,kingdoms), rb=MarriageRoyal(b,kingdoms);
            if(fa<0 || fb<0 || year<0 || !ArrangedMarriagePolicy.RoyalAllowed(campaign,ReadString(a,"heroStringId",""),ra,fb,year)
                || !ArrangedMarriagePolicy.RoyalAllowed(campaign,ReadString(b,"heroStringId",""),rb,fa,year)) return null;
            int ta=ReadInt(ca,"tier",0),tb=ReadInt(cb,"tier",0);
            var lower=ta<tb?ca:cb; var higher=ta<tb?cb:ca;
            return new Dictionary<string,object> {
                {"policyVersion",ArrangedMarriagePolicy.Version},{"nativeYear",year},
                {"fee",ArrangedMarriagePolicy.Fee(ta,tb)},
                {"payerId",ReadString(lower,"leaderId","")},{"payeeId",ReadString(higher,"leaderId","")},
                {"clanAId",ReadString(ca,"clanId","")},{"clanBId",ReadString(cb,"clanId","")},
                {"leaderAId",ReadString(ca,"leaderId","")},{"leaderBId",ReadString(cb,"leaderId","")},
                {"tierA",ta},{"tierB",tb},{"fiefsA",fa},{"fiefsB",fb},
                {"royalA",ra},{"royalB",rb},{"royalException",(ra&&fb==0)||(rb&&fa==0)} };
        }

        private static Dictionary<string,object> ProcessArrangedMarriageSystem(ReignDbConnection c,string campaign,
            Dictionary<string,object> payload,double day)
        {
            var stageTimer=System.Diagnostics.Stopwatch.StartNew();
            long candidatePairs=0,eligiblePairs=0,maxLeaderMs=0;
            var nobles=ReadDictionaryList(payload,"nobles").OrderBy(x=>ReadString(x,"heroStringId","") ,StringComparer.Ordinal).ToList();
            var clans=ReadDictionaryList(payload,"clans").OrderBy(x=>ReadString(x,"clanId","") ,StringComparer.Ordinal).ToList();
            var kingdoms=ReadDictionaryList(payload,"kingdoms");
            string timeline=ReadString(payload,"timelineId","main");
            int year=ReadInt(payload,"nativeYear",-1), proposals=0,rolls=0,refused=0;
            bool playerLetters = ReadBool(payload, "playerMarriageLetters", false);
            long period=ArrangedMarriagePolicy.Period(day),ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach(var clan in clans.Where(x=>ReadString(x,"leaderId","")!="" && (!playerLetters || !ReadBool(x,"isPlayerClan",false))))
            {
                var leaderTimer=System.Diagnostics.Stopwatch.StartNew();
                try
                {
                string leaderId=ReadString(clan,"leaderId",""),clanId=ReadString(clan,"clanId","");
                if(RelationshipsStore.SelectMarriageLeaderRolls(c, new Dictionary<string,object>{{"t",timeline},{"p",period},{"l",leaderId}}).Any())continue;
                var leader=nobles.FirstOrDefault(x=>ReadString(x,"heroStringId","")==leaderId)??new Dictionary<string,object>();
                var members=nobles.Where(x=>ReadString(x,"clanId","")==clanId&&ReadString(x,"spouseId","")=="").ToList();
                double urgency=MarriageUrgency(clan,leader,members),chance=DialogueService.ClampDouble(.01+.04*urgency,.01,.05);
                double roll=StableUnit(campaign+"|"+timeline+"|marriage|"+period+"|"+leaderId);
                string rollStatus=members.Count==0?"no_member":roll<chance?"passed":"failed";
                RelationshipsStore.InsertMarriageLeaderRolls(c, new Dictionary<string,object>{{"id","marriage_roll_"+Guid.NewGuid().ToString("N")},{"t",timeline},{"p",period},{"l",leaderId},{"clan",clanId},{"day",day},{"chance",chance},{"roll",roll},{"u",urgency},{"status",rollStatus},{"ts",ts}});
                rolls++;
                WorldSimulationService.RecordWorldTestCounter(c,campaign,timeline,(int)Math.Floor(day),"marriages","leader_roll_"+period+"_"+leaderId,
                    new Dictionary<string,object>{{"arrangedLeaderRolls",1},{"arrangedRollPassed",rollStatus=="passed"?1:0},{"arrangedRollFailed",rollStatus=="failed"?1:0},{"arrangedNoEligibleMember",rollStatus=="no_member"?1:0}});
                if(rollStatus!="passed")continue;
                var choices=new List<Dictionary<string,object>>();
                foreach(var a in members)
                foreach(var b in nobles.Where(x=>ReadString(x,"clanId","")!=clanId&&ReadString(x,"spouseId","")==""))
                {
                    candidatePairs++;
                    string aid=ReadString(a,"heroStringId",""),bid=ReadString(b,"heroStringId","");
                    if(!SnapshotMarriageEligible(a,b)||MarriagePairAlreadyActive(c,timeline,aid,bid))continue;
                    eligiblePairs++;
                    var other=clans.FirstOrDefault(x=>ReadString(x,"clanId","")==ReadString(b,"clanId","")); if(other==null)continue;
                    var quote=MarriageQuote(campaign,a,b,clan,other,kingdoms,year); if(quote==null)continue;
                    var otherLeader=nobles.FirstOrDefault(x=>ReadString(x,"heroStringId","")==ReadString(other,"leaderId",""))??new Dictionary<string,object>();
                    double multiplier=ArrangedMarriageCourtshipMultiplier(c,aid,bid,day);
                    double approvalA=DynasticMarriageScore(clan,other,leader,a,b)*multiplier;
                    if(approvalA<25)continue;
                    double approvalB=DynasticMarriageScore(other,clan,otherLeader,b,a)*multiplier;
                    int fee=ReadInt(quote,"fee",0); string payer=ReadString(quote,"payerId","");
                    var payerClan=ReadString(clan,"leaderId","")==payer?clan:other;
                    double available=ReadDouble(payerClan,"leaderGold",ReadDouble(payerClan,"gold",0))-ReservedMarriageGold(c,timeline,payer);
                    string refusal=playerLetters && ReadBool(other,"isPlayerClan",false) ? "" : approvalB<25?"approval_too_low":available<fee?"insufficient_funds":"";
                    var refusalTerms=new Dictionary<string,object>(quote);
                    if(!ReadBool(quote,"royalException",false))refusalTerms.Remove("nativeYear");
                    string signature=Json.Serialize(new object[]{timeline,leaderId,ReadString(other,"leaderId",""),aid,bid,refusalTerms,approvalB>=25,available>=fee});
                    string key=Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(signature)));
                    if(refusal!=""&&RelationshipsStore.SelectArrangedMarriageRefusals(c, new Dictionary<string,object>{{"k",key},{"t",timeline}}).Any())continue;
                    if(ReadBool(quote,"royalException",false)&&RoyalMarriageReserved(c,timeline,a,b,year))continue;
                    choices.Add(new Dictionary<string,object>{{"a",a},{"b",b},{"other",other},{"otherLeader",otherLeader},{"quote",quote},
                        {"approvalA",approvalA},{"approvalB",approvalB},{"rank",ArrangedMarriagePolicy.Rank(approvalA,approvalB,ReadInt(clan,"tier",0),ReadInt(other,"tier",0),ReadInt(quote,"fiefsA",0),ReadInt(quote,"fiefsB",0))},
                        {"sum",approvalA+approvalB},{"gap",Math.Abs(ReadInt(clan,"tier",0)-ReadInt(other,"tier",0))},{"ids",aid+"|"+bid},{"refusal",refusal},{"refusalKey",key}});
                }
                var chosen=choices.OrderByDescending(x=>ReadDouble(x,"rank",0)).ThenByDescending(x=>ReadDouble(x,"sum",0)).ThenBy(x=>ReadInt(x,"gap",0)).ThenBy(x=>ReadString(x,"ids",""),StringComparer.Ordinal).FirstOrDefault();
                if(chosen==null)continue;
                var first=ReadDictionary(chosen,"a");var second=ReadDictionary(chosen,"b");var targetClan=ReadDictionary(chosen,"other");
                var terms=ReadDictionary(chosen,"quote");double scoreA=ReadDouble(chosen,"approvalA",0),scoreB=ReadDouble(chosen,"approvalB",0);
                string why=ReadString(chosen,"refusal","");
                bool player=ReadBool(clan,"isPlayerClan",false)||ReadBool(targetClan,"isPlayerClan",false);
                bool diplomatic=ReadString(clan,"kingdomId","")!=ReadString(targetClan,"kingdomId","")&&(ReadBool(clan,"isRulingClan",false)||ReadBool(targetClan,"isRulingClan",false));
                var detail=new Dictionary<string,object>{{"leaderAApproval",scoreA},{"leaderBApproval",scoreB},{"compelled",true},{"marriageTerms",terms},{"refusalReason",why},{"refusalKey",ReadString(chosen,"refusalKey","")},{"diplomaticPackageRequired",diplomatic}};
                CreateMarriageEvaluation(c,campaign,timeline,"arranged",why!=""?"refused":player?(playerLetters?"player_offer_pending":"player_court_pending"):diplomatic?"awaiting_diplomacy":"approved",day,first,second,clan,targetClan,Math.Min(scoreA,scoreB),
                    ArrangedMarriageResentment(first,second,leader,scoreA),ArrangedMarriageResentment(second,first,ReadDictionary(chosen,"otherLeader"),scoreB),detail);
                proposals++; if(why!="")refused++;
                }
                finally { maxLeaderMs=Math.Max(maxLeaderMs,leaderTimer.ElapsedMilliseconds); }
            }
            var performance=TestDict("campaignId",campaign,"timelineId",timeline,"periodIndex",period,
                "durationMs",stageTimer.ElapsedMilliseconds,"maxLeaderMs",maxLeaderMs,
                "candidatePairs",candidatePairs,"eligiblePairs",eligiblePairs,"leaderRolls",rolls);
            // Log measured work, without copying snapshots or candidate details.
            try { OperationsService.LogOperational("relationships.arranged_marriage_performance",performance); } catch { }
            return new Dictionary<string,object>{{"arrangedCreated",proposals},{"leaderRolls",rolls},{"periodIndex",period},{"refused",refused},{"policyVersion",ArrangedMarriagePolicy.Version}};
        }

        private static double ReservedMarriageGold(ReignDbConnection c,string timeline,string payer)
            => RelationshipsStore.SelectMarriageEvaluations(c, new Dictionary<string,object>{{"t",timeline}})
                .Select(x=>ReadDictionary(TryParseJsonObject(ReadString(x,"payload_json","{}")),"marriageTerms"))
                .Where(x=>x!=null&&ReadString(x,"payerId","")==payer).Sum(x=>ReadDouble(x,"fee",0));

        private static bool RoyalMarriageReserved(ReignDbConnection c,string timeline,Dictionary<string,object> a,Dictionary<string,object> b,int year)
            => RelationshipsStore.SelectMarriageEvaluations2(c, new Dictionary<string,object>{{"t",timeline}})
                .Any(row=> {var terms=ReadDictionary(TryParseJsonObject(ReadString(row,"payload_json","{}")),"marriageTerms");return terms!=null&&ReadInt(terms,"nativeYear",-1)==year&&ReadBool(terms,"royalException",false)
                    &&new[]{ReadString(a,"heroStringId",""),ReadString(b,"heroStringId","")}.Any(id=>id==ReadString(row,"hero_a_id","")||id==ReadString(row,"hero_b_id",""));});

        internal static void ApplyMarriageLeaderEffect(ReignDbConnection c,string campaign,string timeline,string proposal,string from,string to,int delta,double day)
        {
            string key=proposal+"|leaders|"+from+"|"+to;
            if(from==""||to==""||from==to||RelationshipsStore.SelectArrangedMarriageEffects(c, new Dictionary<string,object>{{"k",key}}).Any())return;
            ApplyAuthoritativeRelationshipDelta(c,campaign,from,to,delta,day,"arranged_marriage_leaders",timeline,true);
            RelationshipsStore.InsertArrangedMarriageEffects(c, new Dictionary<string,object>{{"k",key},{"t",timeline},{"p",proposal},{"d",delta}});
        }

        private static void RevalidateLegacyMarriageOffers(ReignDbConnection c,string timeline,double day)
        {
            foreach(var row in RelationshipsStore.SelectMarriageEvaluations3(c, new Dictionary<string,object>{{"t",timeline}}))
            {
                var quote=ReadDictionary(TryParseJsonObject(ReadString(row,"payload_json","{}")),"marriageTerms");
                if(ReadInt(quote,"policyVersion",0)==ArrangedMarriagePolicy.Version && day-ReadDouble(row,"world_day",day)<=14d)continue;
                string id=ReadString(row,"evaluation_id","");
                RelationshipsStore.UpdateMarriageEvaluations(c, new Dictionary<string,object>{{"id",id}});
                foreach(var action in RelationshipsStore.SelectRelationshipDirectorActions(c))
                {
                    if(ReadString(TryParseJsonObject(ReadString(action,"payload_json","{}")),"evaluationId","")!=id)continue;
                    string actionId=ReadString(action,"director_action_id","");
                    RelationshipsStore.UpdateRelationshipDirectorActions(c, TestDict("id",actionId));
                    RelationshipsStore.DeleteRelationshipMarriageReservations(c, TestDict("id",actionId));
                }
                if(RelationshipsStore.SelectSqliteMaster(c).Any())
                    RelationshipsStore.UpdateCourtMatters(c, TestDict("t",timeline,"s","marriage_evaluation:"+id));
                // Do not invoice or grandfather an old agreement. Native execution also rejects missing quotes.
            }
        }

        private static Dictionary<string, object> CompletedDiplomaticMarriageTerms(string status, Dictionary<string, object> record)
        {
            if (status != "completed" || ReadString(record, "source", "") != "world_diplomacy_director") return null;
            var terms = ReadDictionary(record, "terms") ?? TryParseJsonObject(ReadString(record, "termsJson", "{}"));
            return ReadInt(ReadDictionary(terms, "marriageTerms"), "policyVersion", 0) == ArrangedMarriagePolicy.Version
                ? terms : null;
        }

        internal static Dictionary<string, object> NativeDiplomaticMarriageCompletion(string actionId, string status,
            Dictionary<string, object> record, Dictionary<string, object> report)
        {
            var terms = CompletedDiplomaticMarriageTerms(status, record);
            if (terms == null) return null;
            var result = ReadDictionary(report, "result");
            if (!ReadBool(result, "success", false) || !ReadBool(result, "completed", false)) return null;

            string first = ReadFirstString(terms, "marriageHero1StringId", "groomHeroStringId");
            string second = ReadFirstString(terms, "marriageHero2StringId", "brideHeroStringId", "spouseHeroStringId");
            if (first.Length == 0 || second.Length == 0 || first == second) return null;

            var spouses = ReadDictionaryList(result, "changedEntities")
                .Where(change => ReadString(change, "entityType", "") == "hero" && ReadString(change, "changeType", "") == "spouse_changed")
                .Select(change => ReadString(change, "id", "")).ToHashSet(StringComparer.Ordinal);
            var effects = ReadDictionaryList(result, "effects");
            if (!spouses.SetEquals(new[] { first, second }) || !effects.Any(effect =>
                ReadString(effect, "effectType", "") == "marriage_completed" && ReadString(effect, "entityType", "") == "hero"
                && spouses.Contains(ReadString(effect, "id", "")))) return null;

            int fee = ReadInt(ReadDictionary(terms, "marriageTerms"), "fee", -1);
            if (fee < 0 || !effects.Any(effect => ReadString(effect, "effectType", "") == "diplomatic_marriage_settled"
                && ReadString(effect, "entityType", "") == "action" && ReadString(effect, "id", "") == actionId
                && ReadInt(effect, "detail", -1) == fee)) return null;

            string timeline = ReadString(report, "timelineId", "");
            string recordedTimeline = ReadString(record, "timelineId", "");
            if (timeline.Length == 0 || (recordedTimeline.Length > 0 && recordedTimeline != timeline)) return null;
            return TestDict("timelineId", timeline, "worldDay", ReadDouble(report, "worldDay", 0d));
        }

        internal static void FinalizeDiplomaticMarriageEffects(string campaign, string id, string status,
            Dictionary<string, object> record, Dictionary<string, object> report, Dictionary<string, object> admittedCompletion = null)
        {
            var terms = CompletedDiplomaticMarriageTerms(status, record);
            if (terms == null) return;
            var quote = ReadDictionary(terms, "marriageTerms");
            if (admittedCompletion == null)
            {
                var result = ReadDictionary(report, "result");
                var effects = ReadDictionaryList(result, "effects");
                if (effects.Count == 0) effects = ReadDictionaryList(result, "Effects");
                if (!effects.Any(effect => ReadString(effect, "type", "") == "marriage_completed")) return;
            }
            // Match the director/worker order: campaign ownership, then DB work.
            using var ownership = PersistenceService.EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
            using var c = ReignDatabase.OpenCampaignConnection(campaign);
            EnsureRelationshipDirectorSchema(c);
            string timeline = admittedCompletion != null ? ReadString(admittedCompletion, "timelineId", "")
                : ReadString(record, "timelineId", WorldSimulationService.ActiveWorldHistoryTimeline(c));
            double day = ReadDouble(admittedCompletion ?? report, "worldDay", 0d);
            string leaderA = ReadString(quote, "leaderAId", ""), leaderB = ReadString(quote, "leaderBId", "");
            using var tx = c.BeginTransaction();
            ApplyMarriageLeaderEffect(c, campaign, timeline, "diplomatic_" + id, leaderA, leaderB, 20, day);
            ApplyMarriageLeaderEffect(c, campaign, timeline, "diplomatic_" + id, leaderB, leaderA, 20, day);
            tx.Commit();
        }

        private static List<Dictionary<string,object>> RunArrangedMarriageSelfTests()
        {
            var results=new List<Dictionary<string,object>>();
            void Check(string id,bool pass)=>results.Add(TestDict("caseId","arranged_"+id,"passed",pass,"summary",id));
            string campaign="__arranged_test_"+Guid.NewGuid().ToString("N");
            Check("fixture_is_internal",CampaignFileService.IsInternalCampaignId(campaign));
            try
            {
                using var c=ReignDatabase.OpenCampaignConnection(campaign);
                EnsureRelationshipDirectorSchema(c);EnsureMbtiRelationshipSchema(c);EnsureRelationshipLifecycleSchema(c);
                EnsureNotableMbtiSchema(c);
                EnsureRelationshipFlingSchemaCore(c);
                foreach (var declaration in RelationshipMaintenanceShapes)
                {
                    var actualColumns = RelationshipsStore.SelectPgAttribute(c, TestDict("table",declaration.Key)).Select(row=>ReadString(row,"name","")).ToHashSet(StringComparer.Ordinal);
                    var actualIndexes = RelationshipsStore.SelectPgIndex(c, TestDict("table",declaration.Key)).Select(row=>ReadString(row,"name","")).ToHashSet(StringComparer.Ordinal);
                    Check("readiness_shape_"+declaration.Key,
                        actualColumns.SetEquals(declaration.Value.Columns.Split(','))
                        && actualIndexes.SetEquals(declaration.Value.Indexes.Split(',',StringSplitOptions.RemoveEmptyEntries)));
                }
                WorldSimulationService.EnsureWorldTestSchema(c); ReputationService.EnsureRumorSchema(c); EnsureClanConflictSchema(c);
                var priorRetryScope=ReignDatabase.CurrentSqlRetrySuppression;
                bool nestedRetryRestored=false;
                using(var independent=ReignDatabase.OpenCampaignConnection(campaign))
                using(var outerRetryScope=new ReignDatabase.SqlStatementRetrySuppression(c))
                {
                    Check("retry_scope_is_connection_specific",ReignDatabase.SqlStatementRetriesSuppressed(c) && !ReignDatabase.SqlStatementRetriesSuppressed(independent));
                    try { using var innerRetryScope=new ReignDatabase.SqlStatementRetrySuppression(c); throw new InvalidOperationException("fixture"); }
                    catch(InvalidOperationException) { nestedRetryRestored=ReferenceEquals(ReignDatabase.CurrentSqlRetrySuppression,outerRetryScope); }
                }
                Check("retry_scope_restores_nested_exception",nestedRetryRestored && ReferenceEquals(ReignDatabase.CurrentSqlRetrySuppression,priorRetryScope));
                Check("world_readiness_does_not_block_data_writers",
                    RelationshipReadinessLeavesWritersUnblocked(campaign,"world_test_native_heartbeats","updated_ts",
                        _ => { if(!ReadBool(WorldSimulationService.BuildWorldTestReadiness(campaign,"main"),"ok",false)) throw new InvalidOperationException("Readiness was unavailable."); }));
                using (var locked=ReignDatabase.OpenCampaignConnection(campaign))
                using (var held=locked.BeginTransaction())
                {
                    RelationshipsStore.Lock(locked);
                    var blocked=System.Diagnostics.Stopwatch.StartNew();
                    bool rejected=false;
                    int statementRetries=0;
                    var priorRetryObserver=ReignDatabase.SqlContentionRetryObserverForTests;
                    try
                    {
                        ReignDatabase.SqlContentionRetryObserverForTests=_=>statementRetries++;
                        try { WorldSimulationService.BuildWorldTestReadiness(campaign,"main"); }
                        catch(Npgsql.PostgresException ex) when(ex.SqlState=="55P03") { rejected=true; }
                    }
                    finally { ReignDatabase.SqlContentionRetryObserverForTests=priorRetryObserver; }
                    Check("world_readiness_lock_deadline",rejected && blocked.ElapsedMilliseconds<1500);
                    Check("world_readiness_does_not_retry_aborted_transaction",statementRetries==0 && ReferenceEquals(ReignDatabase.CurrentSqlRetrySuppression,priorRetryScope));
                    using(var retryConnection=ReignDatabase.OpenCampaignConnection(campaign))
                    {
                        RelationshipsStore.Set(retryConnection);
                        try
                        {
                            ReignDatabase.SqlContentionRetryObserverForTests=_=> { statementRetries++; held.Rollback(); };
                            RelationshipsStore.CountWorldTestNativeHeartbeats(retryConnection);
                            Check("ordinary_autocommit_contention_still_retries",statementRetries==1);
                        }
                        finally
                        {
                            ReignDatabase.SqlContentionRetryObserverForTests=priorRetryObserver;
                            RelationshipsStore.Set2(retryConnection);
                        }
                    }
                }
                Check("world_readiness_clean_after_failure",ReadBool(WorldSimulationService.BuildWorldTestReadiness(campaign,"main"),"ok",false));
                RelationshipsSchema.DropIdxWorldTestHeartbeatDay(c);
                var observedReadiness=WorldSimulationService.BuildWorldTestReadiness(campaign,"main");
                Check("world_readiness_does_not_repair_schema",
                    ReadBool(observedReadiness,"ok",false) && !RelationshipsStore.SelectPgIndexes(c).Any());
                RelationshipsSchema.DropWorldTestNativeHeartbeats(c);
                bool missingReadinessRejected=false;
                try { WorldSimulationService.BuildWorldTestReadiness(campaign,"main"); }
                catch(Npgsql.PostgresException ex) when(ex.SqlState=="42P01") { missingReadinessRejected=true; }
                Check("world_readiness_missing_schema_fails_closed",missingReadinessRejected && !WorldSimulationService.TableExists(c,"world_test_native_heartbeats"));
                WorldSimulationService.EnsureWorldTestSchema(c);
                Check("readiness_probe_detects_redundant_ddl",
                    !RelationshipReadinessLeavesWritersUnblocked(campaign,
                        "relationship_native_targets", "claimed_ts", reader =>
                            RelationshipsSchema.CreateIndexRelationshipNativeTargetsStatus(reader)));
                Check("native_target_readiness_during_writes",
                    RelationshipReadinessLeavesWritersUnblocked(campaign,
                        "relationship_native_targets", "claimed_ts", EnsureRelationshipNativeTargetSchema));
                Check("fling_readiness_during_writes",
                    RelationshipReadinessLeavesWritersUnblocked(campaign,
                        "relationship_fling_rolls", "roll", EnsureRelationshipFlingSchemaCore));
                Check("notable_readiness_during_writes",
                    RelationshipReadinessLeavesWritersUnblocked(campaign,
                        "notable_mbti_profiles", "updated_ts", EnsureNotableMbtiSchema));
                RelationshipsSchema.DropIdxRelationshipNativeTargetsStatus(c);
                using (var migration=c.BeginTransaction())
                {
                    EnsureRelationshipNativeTargetSchema(c);
                    migration.Rollback();
                }
                Check("rolled_back_readiness_not_cached",
                    !PostgreSqlRelationshipTableReady(c,"relationship_native_targets","revision,requires_observation","idx_relationship_native_targets_status"));
                EnsureRelationshipNativeTargetSchema(c);
                Check("missing_index_repaired_after_rollback",
                    PostgreSqlRelationshipTableReady(c,"relationship_native_targets","revision,requires_observation","idx_relationship_native_targets_status"));
                foreach(string hero in new[]{"proposer","respondent"})
                    RelationshipsStore.InsertRelationshipPersonalities(c, TestDict("id",hero));
                Check("lock_probe_detects_inverted_order",
                    !MarriageWorkWaitLeavesDatabaseAvailable(campaign, () =>
                    {
                        using var inverted=ReignDatabase.OpenCampaignConnection(campaign);
                        using var transaction=inverted.BeginTransaction();
                        RelationshipsStore.SelectArrangedMarriageEffects2(inverted);
                        using var ownership=PersistenceService.EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
                        transaction.Commit();
                    }));
                using(var tx=c.BeginTransaction())
                {
                    ApplyMarriageLeaderEffect(c,campaign,"main","refusal","proposer","respondent",-10,7);
                    ApplyMarriageLeaderEffect(c,campaign,"main","refusal","proposer","respondent",-10,7);
                    tx.Commit();
                }
                var pair=RelationshipsStore.SelectRelationshipPairChemistry3(c).First();
                Check("refusal_direction_and_replay",ReadInt(pair,"affinity_a_to_b",0)==-10&&ReadInt(pair,"affinity_b_to_a",0)==0);
                using(var tx=c.BeginTransaction())
                {
                    ApplyMarriageLeaderEffect(c,campaign,"main","success","proposer","respondent",20,14);
                    ApplyMarriageLeaderEffect(c,campaign,"main","success","respondent","proposer",20,14);
                    ApplyMarriageLeaderEffect(c,campaign,"main","success","proposer","respondent",20,14);
                    tx.Commit();
                }
                pair=RelationshipsStore.SelectRelationshipPairChemistry3(c).First();
                Check("success_bilateral_and_replay",ReadInt(pair,"affinity_a_to_b",0)==10&&ReadInt(pair,"affinity_b_to_a",0)==20);
                Check("no_self_effect",RelationshipsStore.SelectArrangedMarriageEffects3(c).Count==3);
                var a=TestDict("heroStringId","royal","isRulingClanMember",true,"clanFiefCount",4);
                var b=TestDict("heroStringId","suitor","isRulingClanMember",false,"clanFiefCount",0);
                var ca=TestDict("clanId","pethros","leaderId","respondent","tier",6,"fiefCount",4);
                var cb=TestDict("clanId","vardicus","leaderId","proposer","tier",1,"fiefCount",0);
                int allowedYear=Enumerable.Range(1000,1000).First(y=>ArrangedMarriagePolicy.RoyalAllowed(campaign,"royal",true,0,y));
                int refusedYear=Enumerable.Range(1000,1000).First(y=>!ArrangedMarriagePolicy.RoyalAllowed(campaign,"royal",true,0,y));
                var quote=MarriageQuote(campaign,a,b,ca,cb,new List<Dictionary<string,object>>(),allowedYear);
                Check("ira_exact_fee_and_payer",ReadInt(quote,"fee",0)==100000&&ReadString(quote,"payerId","")=="proposer");
                Check("royal_gate_no_landless_bypass",MarriageQuote(campaign,a,b,ca,cb,new List<Dictionary<string,object>>(),refusedYear)==null);
                var reverse=MarriageQuote(campaign,b,a,cb,ca,new List<Dictionary<string,object>>(),allowedYear);
                Check("proposer_reversal",ReadInt(reverse,"fee",0)==100000&&ReadString(reverse,"payerId","")=="proposer");
                var payload=TestDict("campaignId",campaign,"timelineId","main","worldDay",7d,"nativeYear",allowedYear,
                    "nobles",new List<Dictionary<string,object>>(),"clans",new List<Dictionary<string,object>>());
                var first=RunRelationshipDirector(payload,false);var replay=RunRelationshipDirector(payload,false);
                payload["worldDay"]=14d;var next=RunRelationshipDirector(payload,false);
                Check("weekly_period_completion_not_skip",ReadBool(first,"ok",false)&&ReadBool(replay,"idempotent",false)&&ReadBool(next,"ok",false));
                Check("periods_saved_independently",RelationshipsStore.SelectArrangedMarriagePeriods(c).Count==2);
                string proposer=Enumerable.Range(0,1000000).Select(i=>"leader"+i).First(h=>StableUnit(campaign+"|fixture|marriage|1|"+h)<.01&&StableUnit(campaign+"|fixture|marriage|2|"+h)<.01);
                string respondent=Enumerable.Range(0,10000).Select(i=>"other"+i).First(h=>StableUnit(campaign+"|fixture|marriage|1|"+h)>.05&&StableUnit(campaign+"|fixture|marriage|2|"+h)>.05);
                foreach(string hero in new[]{proposer,respondent})RelationshipsStore.InsertRelationshipPersonalities(c, TestDict("id",hero));
                Dictionary<string,object> HeroRow(string id,string clan,bool female,bool available)=>TestDict("heroStringId",id,"clanId",clan,"isAlive",true,"age",20d,"isFemale",female,"spouseId",available?"":"existing","nativeCanMarry",true,"nativeMarriageClanSuitable",true,"childrenCount",0,"cultureId","empire");
                var lower=TestDict("clanId","lower","leaderId",proposer,"tier",1,"gold",60000,"leaderGold",60000,"fiefCount",0,"kingdomId","empire","cultureId","empire");
                var upper=TestDict("clanId","upper","leaderId",respondent,"tier",6,"gold",500000,"leaderGold",500000,"fiefCount",4,"kingdomId","empire","cultureId","empire");
                var input=TestDict("campaignId",campaign,"timelineId","fixture","worldDay",7d,"nativeYear",1090,
                    "clans",new List<Dictionary<string,object>>{lower,upper},"nobles",new List<Dictionary<string,object>> {
                        HeroRow(proposer,"lower",false,false),HeroRow(respondent,"upper",false,false),HeroRow("son","lower",false,true),HeroRow("daughter","upper",true,true) });
                WorldSimulationService.EnsureWorldTestTelemetrySchema(c);
                string MarriageVariant(bool compact,bool funded)
                {
                    var variant=new Dictionary<string,object>(input);
                    var variantLower=new Dictionary<string,object>(lower);
                    if(funded) { variantLower["gold"]=200000; variantLower["leaderGold"]=200000; }
                    variant["clans"]=new List<Dictionary<string,object>>{variantLower,upper};
                    variant["heroes"]=compact?new List<Dictionary<string,object>>():new List<Dictionary<string,object>>{TestDict("heroStringId","unused_ambient","isAlive",true)};
                    variant["presenceGroups"]=compact?new List<Dictionary<string,object>>():new List<Dictionary<string,object>>{TestDict("nativeRelations",new[]{TestDict("heroAId","son","heroBId","daughter","value",99)})};
                    if(compact) { variant["snapshotKind"]="political_marriage_only_v1"; variant["ambientHeroCount"]=1; }
                    using var tx=c.BeginTransaction();
                    try
                    {
                    Check("compact_parity_clean_start_"+compact+"_"+funded,
                        !RelationshipsStore.SelectMarriageEvaluations4(c).Any()
                        && !RelationshipsStore.SelectMarriageLeaderRolls2(c).Any()
                        && !RelationshipsStore.SelectArrangedMarriageEffects4(c).Any());
                    var result=ProcessArrangedMarriageSystem(c,campaign,variant,7d);
                    Check("compact_parity_nontrivial_"+compact+"_"+funded,
                        ReadInt(result,"arrangedCreated",0)==1 && ReadInt(result,"leaderRolls",0)==2
                        && (funded
                            ? RelationshipsStore.SelectRelationshipDirectorActions2(c).Any()
                            : RelationshipsStore.SelectArrangedMarriageEffects4(c).Count==1));
                    // Generated receipt IDs/timestamps differ by design; compare all
                    // deterministic decisions, rolls, quotes and mechanical deltas.
                    var evaluations=RelationshipsStore.SelectMarriageEvaluations5(c);
                    foreach(var row in evaluations)
                    {
                        var detail=TryParseJsonObject(ReadString(row,"payload_json","{}"));
                        detail.Remove("evaluationId"); row["payload_json"]=detail;
                    }
                    string outcome=Json.Serialize(TestDict("result",result,"evaluations",evaluations,
                        "rolls",RelationshipsStore.SelectMarriageLeaderRolls3(c),
                        "refusals",RelationshipsStore.SelectArrangedMarriageRefusals2(c),
                        "chemistry",RelationshipsStore.SelectRelationshipPairChemistry4(c)));
                    return outcome;
                    }
                    finally
                    {
                        tx.Rollback();
                        InvalidateRelationshipPairStateCache(campaign);
                    }
                }
                Check("compact_snapshot_mechanical_parity",MarriageVariant(false,false)==MarriageVariant(true,false));
                Check("compact_snapshot_accepted_parity",MarriageVariant(true,true)==MarriageVariant(false,true));
                Check("unknown_snapshot_kind_rejected",!ReadBool(RunRelationshipDirector(TestDict("snapshotKind","unknown_v999"),false),"ok",true));
                input["snapshotKind"]="political_marriage_only_v1"; input["ambientHeroCount"]=1;
                Check("director_wait_does_not_hold_database_locks",
                    MarriageWorkWaitLeavesDatabaseAvailable(campaign,
                        () => RunRelationshipDirector(input,false)));
                var offer=RelationshipsStore.SelectMarriageEvaluations6(c);
                Check("actual_unfunded_offer_is_refused_once",offer.Count==1&&ReadString(offer.First(),"status","")=="refused");
                Check("formal_refusal_has_no_native_action",!RelationshipsStore.SelectRelationshipDirectorActions3(c).Any());
                var responsePair=RelationshipsStore.SelectRelationshipPairChemistry5(c, TestDict("p",AmbientPairKey(proposer,respondent))).First();
                bool forward=string.Compare(proposer,respondent,StringComparison.OrdinalIgnoreCase)<0;
                Check("formal_refusal_delta",ReadInt(responsePair,forward?"affinity_a_to_b":"affinity_b_to_a",0)==-10&&ReadInt(responsePair,forward?"affinity_b_to_a":"affinity_a_to_b",0)==0);
                RunRelationshipDirector(input,false);
                Check("formal_offer_replay",RelationshipsStore.SelectMarriageEvaluations6(c).Count==1);
                input["worldDay"]=14d;RunRelationshipDirector(input,false);
                Check("identical_refusal_next_week_not_sent",RelationshipsStore.SelectMarriageEvaluations6(c).Count==1);
                var son=HeroRow("paid_son","lower",false,true);var daughter=HeroRow("paid_daughter","upper",true,true);
                var paidQuote=MarriageQuote(campaign,son,daughter,lower,upper,new List<Dictionary<string,object>>(),1090);
                CreateMarriageEvaluation(c,campaign,"paid_fixture","arranged","approved",21d,son,daughter,lower,upper,80,0,0,
                    TestDict("marriageTerms",paidQuote,"leaderAApproval",80d,"leaderBApproval",80d));
                var paidAction=RelationshipsStore.SelectRelationshipDirectorActions4(c).First();
                var receipt=TestDict("campaignId",campaign,"timelineId","paid_fixture","directorActionId",ReadString(paidAction,"director_action_id",""),"status","completed","worldDay",21d,"marriageVerified",true,"paymentVerified",true);
                Dictionary<string,object> completion=null;
                Check("receipt_wait_does_not_hold_database_locks",
                    MarriageWorkWaitLeavesDatabaseAvailable(campaign,
                        () => completion=RelationshipDirectorReportActionApi(receipt)));
                var completionReplay=RelationshipDirectorReportActionApi(receipt);
                Check("paid_native_receipt_and_leaders_replay",ReadBool(completion,"ok",false)&&ReadBool(completionReplay,"idempotent",false)
                    &&RelationshipsStore.SelectArrangedMarriageEffects5(c).Count==2);
                var diplomaticTerms=TestDict("marriageTerms",paidQuote);
                Check("diplomatic_wait_does_not_hold_database_locks",
                    MarriageWorkWaitLeavesDatabaseAvailable(campaign,
                        () => FinalizeDiplomaticMarriageEffects(campaign,"lock_fixture","completed",
                            TestDict("source","world_diplomacy_director","terms",diplomaticTerms,"timelineId","diplomatic_fixture"),
                            TestDict("worldDay",21d,"result",TestDict("effects",new[]{TestDict("type","marriage_completed")})))));
                RunNativeDiplomaticMarriageReportSelfTests(c, campaign, paidQuote, Check);
                // An older snapshot must be inspected as it is, with no stale
                // process readiness surviving its restored table shape.
                RelationshipsSchema.AlterRelationshipNativeTargets(c);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign,"native_shape_legacy");
                EnsureRelationshipNativeTargetSchema(c);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign,"native_shape_legacy");
                using (var restored=ReignDatabase.OpenCampaignConnection(campaign))
                {
                    Check("old_snapshot_shape_detected",
                        !PostgreSqlRelationshipTableReady(restored,"relationship_native_targets","requires_observation","idx_relationship_native_targets_status"));
                    EnsureRelationshipNativeTargetSchema(restored);
                    Check("old_snapshot_shape_repaired",
                        PostgreSqlRelationshipTableReady(restored,"relationship_native_targets","requires_observation","idx_relationship_native_targets_status"));
                }


            }
            catch(Exception ex){results.Add(TestDict("caseId","arranged_suite_exception","passed",false,"summary",DialogueService.LimitText(ex.ToString(),2000)));}
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
            return results;
        }

        private static void RunNativeDiplomaticMarriageReportSelfTests(ReignDbConnection connection, string campaign,
            Dictionary<string, object> quote, Action<string, bool> check)
        {
            const string first = "paid_son", second = "paid_daughter", timeline = "diplomatic_fixture";
            string leaderA = ReadString(quote, "leaderAId", ""), leaderB = ReadString(quote, "leaderBId", "");
            var record = TestDict("source", "world_diplomacy_director", "type", "DiplomacyPackage",
                "termsJson", Json.Serialize(TestDict("marriageTerms", quote, "marriageHero1StringId", first, "marriageHero2StringId", second)));
            Dictionary<string, object> Row(string id, string status) => TestDict("id", id, "status", status, "record", record);
            int fee = ReadInt(quote, "fee", -1);
            Dictionary<string, object> NativeReport(string id, string spouse, int? paidFee)
            {
                var effects = new List<Dictionary<string, object>>
                    { TestDict("effectType", "marriage_completed", "entityType", "hero", "id", spouse) };
                if (paidFee.HasValue)
                    effects.Add(TestDict("effectType", "diplomatic_marriage_settled", "entityType", "action", "id", id,
                        "detail", paidFee.Value.ToString(CultureInfo.InvariantCulture)));
                return TestDict("campaignId", campaign, "actionId", id, "status", "completed", "type", "DiplomacyPackage",
                    "message", "Wedding concluded.", "actorHeroStringId", "report_actor", "targetHeroStringId", "report_target",
                    "worldDay", 22d, "timelineId", timeline,
                    "result", TestDict("success", true, "completed", true, "effects", effects,
                        "changedEntities", new[] { TestDict("entityType", "hero", "changeType", "spouse_changed", "id", first),
                            TestDict("entityType", "hero", "changeType", "spouse_changed", "id", spouse) }));
            }
            Dictionary<string, object> Pair(string a, string b)
                => RelationshipsStore.SelectRelationshipPairChemistry5(connection, TestDict("p", AmbientPairKey(a, b))).FirstOrDefault();
            var before = Pair(leaderA, leaderB);
            string spousesBefore = Json.Serialize(Pair(first, second));
            var completed = Row("native_marriage_completed", "queued");
            var currentReport = NativeReport("native_marriage_completed", second, fee);
            const string pendingId = "native_marriage_pending_effect";
            var pendingEffect = Row(pendingId, "completed");
            pendingEffect["diplomaticMarriageCompletion"] =
                NativeDiplomaticMarriageCompletion(pendingId, "completed", record, NativeReport(pendingId, second, fee));
            ActionPipelineService.WriteActionQueueUnlocked(campaign, new List<Dictionary<string, object>> {
                completed, Row("native_marriage_historical", "completed"), Row("native_marriage_wrong_couple", "queued"),
                Row("native_marriage_unsettled", "queued"), Row("native_marriage_wrong_fee", "queued"), pendingEffect
            });

            ActionPipelineService.ReportAction(NativeReport("native_marriage_historical", second, fee));
            check("diplomatic_historical_completion_has_no_effect",
                Json.Serialize(Pair(leaderA, leaderB)) == Json.Serialize(before));
            ActionPipelineService.ReportAction(NativeReport("native_marriage_wrong_couple", "other_spouse", fee));
            // A later same-status report cannot replace evidence on an already completed row.
            ActionPipelineService.ReportAction(NativeReport("native_marriage_wrong_couple", second, fee));
            ActionPipelineService.ReportAction(NativeReport("native_marriage_unsettled", second, null));
            ActionPipelineService.ReportAction(NativeReport("native_marriage_wrong_fee", second, fee + 1));
            check("diplomatic_wrong_couple_and_unverified_settlement_have_no_effect",
                Json.Serialize(Pair(leaderA, leaderB)) == Json.Serialize(before));

            ActionPipelineService.ReportAction(currentReport);
            var after = Pair(leaderA, leaderB);
            int deltaAB = ReadInt(after, "affinity_a_to_b", 0) - ReadInt(before, "affinity_a_to_b", 0);
            int deltaBA = ReadInt(after, "affinity_b_to_a", 0) - ReadInt(before, "affinity_b_to_a", 0);
            ActionPipelineService.ReportAction(currentReport);
            check("diplomatic_native_completion_applies_once_to_quoted_leaders",
                deltaAB == 20 && deltaBA == 20 && Json.Serialize(Pair(leaderA, leaderB)) == Json.Serialize(after)
                && Pair("report_actor", "report_target") == null && Json.Serialize(Pair(first, second)) == spousesBefore);

            var retry = NativeReport(pendingId, "other_spouse", null);
            retry["timelineId"] = "different_retry_timeline";
            retry["worldDay"] = 99d;
            ActionPipelineService.ReportAction(retry);
            var recovered = Pair(leaderA, leaderB);
            ActionPipelineService.ReportAction(retry);
            var savedRecovery = ActionPipelineService.ReadActionQueueUnlocked(campaign)
                .Single(row => ReadString(row, "id", "") == pendingId);
            var savedAdmission = ReadDictionary(savedRecovery, "diplomaticMarriageCompletion");
            check("diplomatic_completion_recovers_from_persisted_admission",
                ReadInt(recovered, "affinity_a_to_b", 0) - ReadInt(after, "affinity_a_to_b", 0) == 20
                && ReadInt(recovered, "affinity_b_to_a", 0) - ReadInt(after, "affinity_b_to_a", 0) == 20
                && ReadString(savedAdmission, "timelineId", "") == timeline && ReadDouble(savedAdmission, "worldDay", 0d) == 22d
                && Json.Serialize(Pair(leaderA, leaderB)) == Json.Serialize(recovered));
        }

        private static bool RelationshipReadinessLeavesWritersUnblocked(string campaign,
            string table, string column, Action<ReignDbConnection> ensure)
        {
            Task task;
            bool completedWhileWriting;
            using var started=new ManualResetEventSlim();
            using (var writer=ReignDatabase.OpenCampaignConnection(campaign))
            using (var transaction=writer.BeginTransaction())
            {
                // Fixed test-owned identifiers; even a zero-row UPDATE holds
                // RowExclusive, conflicting with CREATE INDEX's Share lock.
                RelationshipsStore.UpdateDynamic(writer, table, column, column);
                task=Task.Run(() =>
                {
                    using var reader=ReignDatabase.OpenCampaignConnection(campaign);
                    started.Set();
                    ensure(reader);
                });
                completedWhileWriting=started.Wait(TimeSpan.FromSeconds(5))
                    && task.Wait(TimeSpan.FromSeconds(2));
            }
            if (!task.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Schema readiness did not finish after the isolated writer released its table lock.");
            task.GetAwaiter().GetResult();
            return completedWhileWriting;
        }

        private static bool MarriageWorkWaitLeavesDatabaseAvailable(string campaign, Action work,
            Action<Dictionary<string,object>> observe = null)
        {
            using var reachedWait=new ManualResetEventSlim();
            Task task;
            bool waiting=false, databaseAvailable=false;
            bool advisoryAvailable=false;
            string lockFailure="";
            var probeTimer=System.Diagnostics.Stopwatch.StartNew();
            var lockHolders=new List<Dictionary<string,object>>();
            // Model a worker owning the campaign monitor while an HTTP marriage
            // request arrives. Waiting requests must not retain table locks that
            // would prevent that worker from completing schema readiness.
            using (PersistenceService.EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)))
            {
                // A dedicated test worker makes the handshake independent of
                // unrelated verification work occupying the shared thread pool.
                task=Task.Factory.StartNew(() =>
                {
                    PersistenceService.CampaignWorkLockWaitObserverForTests=gate =>
                    {
                        if (ReferenceEquals(gate,CampaignRelationshipWriteLock(campaign))) reachedWait.Set();
                    };
                    try { work(); }
                    finally { PersistenceService.CampaignWorkLockWaitObserverForTests=null; }
                },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                try
                {
                    waiting=reachedWait.Wait(TimeSpan.FromSeconds(5));
                    using var probe=ReignDatabase.OpenCampaignConnection(campaign);
                    using var transaction=probe.BeginTransaction();
                    using var command=probe.CreateCommand();
                    command.Transaction=transaction;
                    command.CommandTimeout=2;
                    command.CommandText=RelationshipsStore.SelectSql;
                    advisoryAvailable=(bool)command.ExecuteScalar();
                    command.CommandText="LOCK TABLE arranged_marriage_periods, marriage_leader_rolls, arranged_marriage_effects, relationship_pair_chemistry, relationship_pair_provenance, relationship_native_targets IN ACCESS EXCLUSIVE MODE NOWAIT;";
                    command.ExecuteNonQuery();
                    databaseAvailable=advisoryAvailable;
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState=="55P03")
                {
                    databaseAvailable=false;
                    lockFailure=ex.SqlState;
                }
                if (!databaseAvailable)
                {
                    // Fixture-only metadata, without query text or private values.
                    using var diagnostic=ReignDatabase.OpenCampaignConnection(campaign);
                    lockHolders=RelationshipsStore.SelectPgLocks(diagnostic);
                }
            }
            if(!task.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Marriage request did not finish after campaign ownership was released.");
            task.GetAwaiter().GetResult();
            observe?.Invoke(TestDict("waiting",waiting,"databaseAvailable",databaseAvailable,"advisoryAvailable",advisoryAvailable,
                "lockSqlState",lockFailure,"probeElapsedMs",probeTimer.ElapsedMilliseconds,"lockHolders",lockHolders));
            return waiting && databaseAvailable;
        }
    }
}
