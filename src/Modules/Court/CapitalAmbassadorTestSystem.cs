#if !REIGN_EXCLUDE_COURT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CapitalAmbassadorServerTestConfirmation = "run Reign capital ambassador server matrix";

        private static Dictionary<string, object> CapitalAmbassadorTestApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string confirmation = ReadString(payload, "confirmation", "");
            if (!string.Equals(confirmation, CapitalAmbassadorServerTestConfirmation, StringComparison.Ordinal))
                return CourtError("The exact capital/ambassador server-test confirmation is required.");
            string runId = ReadString(payload, "runId", "").Trim();
            if (string.IsNullOrWhiteSpace(runId)) return CourtError("runId is required.");

            string campaignId = "capital_ambassador_test_" + AmbassadorHash(runId).Substring(0, 24);
            string timelineId = "fixture_" + AmbassadorHash(runId + "|timeline").Substring(0, 16);
            List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
            bool cleanupComplete = false;
            CleanupCapitalAmbassadorServerFixture(campaignId);
            try
            {
                Dictionary<string, object> capitalOpen = CourtSessionOpenApi(new Dictionary<string, object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","capital_open"},{"expectedRevision",0L},
                    {"sessionId","capital_session"},{"authority","royal"},{"playerIsKingdomRuler",true},
                    {"hostSettlementStringId","town_capital"},{"capitalSettlementStringId","town_capital"},{"scope","capital"},
                    {"worldDay",20d},{"playerHeroStringId","player"}
                });
                Dictionary<string, object> localOpen = CourtSessionOpenApi(new Dictionary<string, object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","local_open"},{"expectedRevision",0L},
                    {"sessionId","local_session"},{"authority","royal"},{"playerIsKingdomRuler",true},
                    {"hostSettlementStringId","castle_local"},{"capitalSettlementStringId","town_capital"},{"scope","local"},
                    {"worldDay",20d},{"playerHeroStringId","player"}
                });
                Dictionary<string, object> localDenied = RequireCapitalCourtScope(new Dictionary<string, object>
                    {{"campaignId",campaignId},{"timelineId",timelineId},{"sessionId","local_session"}});
                Dictionary<string, object> capitalAllowed = RequireCapitalCourtScope(new Dictionary<string, object>
                    {{"campaignId",campaignId},{"timelineId",timelineId},{"sessionId","capital_session"}});
                AddCapitalAmbassadorTestAssertion(assertions, "server_capital_scope_boundary",
                    ReadBool(capitalOpen,"ok",false) && ReadBool(localOpen,"ok",false) && localDenied != null && capitalAllowed == null,
                    "Local Rule Mode is rejected for royal commands while the current capital session is accepted.",
                    new Dictionary<string,object>{{"capital",capitalOpen},{"local",localOpen},{"localRejection",localDenied??new Dictionary<string,object>()}});

                List<object> candidates = new List<object>
                {
                    ValidCapitalAmbassadorCandidate("envoy_c", 260, 40, 9),
                    ValidCapitalAmbassadorCandidate("envoy_a", 200, -50, 0),
                    ValidCapitalAmbassadorCandidate("envoy_b", 225, 15, 2),
                    InvalidCapitalAmbassadorCandidate("charm_49", "charm"),
                    InvalidCapitalAmbassadorCandidate("child", "adult"),
                    InvalidCapitalAmbassadorCandidate("dead", "alive"),
                    InvalidCapitalAmbassadorCandidate("inactive", "active"),
                    InvalidCapitalAmbassadorCandidate("prisoner", "prisoner"),
                    InvalidCapitalAmbassadorCandidate("ruler", "ruler"),
                    InvalidCapitalAmbassadorCandidate("governor", "governor"),
                    InvalidCapitalAmbassadorCandidate("party_leader", "party"),
                    InvalidCapitalAmbassadorCandidate("assigned", "envoy"),
                    InvalidCapitalAmbassadorCandidate("not_lord", "lord")
                };
                Dictionary<string, object> establishBase = new Dictionary<string, object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"sessionId","capital_session"},{"courtScope","capital"},
                    {"hostSettlementStringId","town_capital"},{"originKingdomStringId","foreign_kingdom"},{"originKingdomName","Foreign Kingdom"},
                    {"originRulerHeroStringId","ruler_a"},{"originRulerName","Ruler A"},{"playerKingdomStringId","player_kingdom"},
                    {"capitalSettlementStringId","town_capital"},{"relationWithRuler",-30},{"atWar",false},{"worldDay",20d},{"candidates",candidates}
                };

                Dictionary<string, object> hostile = new Dictionary<string, object>(establishBase, StringComparer.OrdinalIgnoreCase);
                hostile["commandId"] = "relation_minus_31";
                hostile["relationWithRuler"] = -31;
                Dictionary<string, object> war = new Dictionary<string, object>(establishBase, StringComparer.OrdinalIgnoreCase);
                war["commandId"] = "war_rejected";
                war["atWar"] = true;
                Dictionary<string, object> hostileResult = CapitalCourtApi(hostile, ForeignAmbassadorEstablishApi);
                Dictionary<string, object> warResult = CapitalCourtApi(war, ForeignAmbassadorEstablishApi);
                AddCapitalAmbassadorTestAssertion(assertions, "server_establishment_relation_and_war_boundaries",
                    !ReadBool(hostileResult,"ok",true) && !ReadBool(warResult,"ok",true),
                    "Relation -30 is the exact lower boundary and active war rejects establishment.",
                    new Dictionary<string,object>{{"relationMinus31",hostileResult},{"war",warResult}});

                bool exclusions = candidates.Skip(3).Cast<Dictionary<string,object>>().All(x => !IsValidForeignAmbassadorCandidate(x));
                AddCapitalAmbassadorTestAssertion(assertions, "server_candidate_exclusion_matrix", exclusions,
                    "Charm 49, child, dead, inactive, prisoner, ruler, governor, party leader, active envoy, and non-lord candidates are all excluded.", null);

                Dictionary<string, object> establish = new Dictionary<string, object>(establishBase, StringComparer.OrdinalIgnoreCase)
                    {{"commandId","establish"}};
                Dictionary<string, object> established = CapitalCourtApi(establish, ForeignAmbassadorEstablishApi);
                Dictionary<string, object> duplicate = new Dictionary<string, object>(establishBase, StringComparer.OrdinalIgnoreCase)
                    {{"commandId","establish_duplicate"}};
                Dictionary<string, object> duplicateResult = CapitalCourtApi(duplicate, ForeignAmbassadorEstablishApi);
                List<Dictionary<string,object>> validOrdered = candidates.Cast<Dictionary<string,object>>()
                    .Where(IsValidForeignAmbassadorCandidate).OrderBy(x => ReadString(x,"heroStringId",""),StringComparer.OrdinalIgnoreCase).ToList();
                string selectionSeed = campaignId + "|" + timelineId + "|foreign_kingdom|town_capital|resident_ambassador";
                Dictionary<string,object> expectedCandidate = validOrdered[StableAmbassadorInt(selectionSeed, validOrdered.Count)];
                int expectedTravel = Math.Max(1,Math.Min(3,ReadInt(expectedCandidate,"travelDays",3)));
                string postingId = ReadString(established,"recordId","");
                AddCapitalAmbassadorTestAssertion(assertions, "server_deterministic_selection_travel_and_duplicate",
                    ReadBool(established,"ok",false) && ReadString(established,"heroStringId","")==ReadString(expectedCandidate,"heroStringId","")
                    && Math.Abs(ReadDouble(established,"arrivalDay",0d)-(20d+expectedTravel))<0.001d
                    && ReadString(duplicateResult,"recordId","")==postingId && ReadString(duplicateResult,"heroStringId","")==ReadString(established,"heroStringId",""),
                    "Stable selection, one-to-three-day travel clamping, and one active posting per origin are exact and idempotent.",
                    new Dictionary<string,object>{{"expected",expectedCandidate},{"established",established},{"duplicate",duplicateResult}});

                Dictionary<string, object> resident = ForeignAmbassadorLocationApi(new Dictionary<string, object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","arrive"},{"expectedRevision",ReadLong(established,"revision",0)},
                    {"postingId",postingId},{"status","resident"},{"capitalSettlementStringId","town_capital"},{"shelterSettlementStringId",""},{"worldDay",23d}
                });
                AddCapitalAmbassadorTestAssertion(assertions, "server_resident_location_revision",
                    ReadBool(resident,"ok",false) && ReadString(resident,"status","")=="resident" && ReadLong(resident,"revision",0)>ReadLong(established,"revision",0),
                    "Arrival persists resident location and advances the optimistic-concurrency revision.", resident);

                Dictionary<string, object> informalPayload = new Dictionary<string, object>
                {
                    {"timelineId",timelineId},{"conversationMode","castle_chat"}
                };
                string informalRole = BuildAmbassadorRolePrompt(campaignId,
                    ReadString(established,"heroStringId",""), "Test Envoy", informalPayload);
                AddCapitalAmbassadorTestAssertion(assertions, "server_ambassador_identity_cross_setting",
                    informalRole.Contains("CURRENT OFFICIAL POSTING AND IDENTITY", StringComparison.Ordinal)
                    && informalRole.Contains("CURRENT SETTING IS NOT AN OFFICIAL AMBASSADOR AUDIENCE", StringComparison.Ordinal)
                    && informalRole.Contains("Never forget or contradict this posting", StringComparison.Ordinal),
                    "An active resident ambassador receives explicit posting identity in castle and other non-official conversation without gaining official-channel authority.", informalRole);

                Dictionary<string, object> lowCharter = BuildAmbassadorAuthorityCharter(campaignId,timelineId,postingId,1,200,-50,"ruler_a");
                Dictionary<string, object> highCharter = BuildAmbassadorAuthorityCharter(campaignId,timelineId,postingId,1,300,50,"ruler_a");
                Dictionary<string,object> lowTrade = ReadDictionaryList(lowCharter,"permissions").First(x=>ReadString(x,"actionId","")=="trade_agreement");
                Dictionary<string,object> highTrade = ReadDictionaryList(highCharter,"permissions").First(x=>ReadString(x,"actionId","")=="trade_agreement");
                AddCapitalAmbassadorTestAssertion(assertions, "server_authority_formula_boundaries",
                    ReadInt(lowTrade,"permissionChance",0)==35 && ReadInt(highTrade,"permissionChance",0)==75
                    && ReadDictionaryList(lowCharter,"permissions").Count==5 && ReadStringList(highCharter,"referralOnlyActions").Count==9,
                    "Charm and ruler-trust modifiers clamp correctly around the five commercial permissions and nine mandatory referral categories.",
                    new Dictionary<string,object>{{"low",lowCharter},{"high",highCharter}});

                long charterRevision = 2;
                Dictionary<string, object> grantedCharter = null;
                Dictionary<string, object> grantedPermission = null;
                for (; charterRevision < 200 && grantedPermission == null; charterRevision++)
                {
                    grantedCharter = BuildAmbassadorAuthorityCharter(campaignId,timelineId,postingId,charterRevision,300,50,"ruler_a");
                    grantedPermission = ReadDictionaryList(grantedCharter,"permissions").FirstOrDefault(x=>ReadBool(x,"granted",false));
                }
                charterRevision--;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    ExecuteSql(connection,"UPDATE foreign_ambassador_postings SET authority_revision=$revision,authority_charter_json=$charter WHERE posting_id=$posting;",
                        new Dictionary<string,object>{{"revision",charterRevision},{"charter",Json.Serialize(grantedCharter)},{"posting",postingId}});
                }
                Dictionary<string,object> context = new Dictionary<string,object>
                {
                    {"postingId",postingId},{"representedKingdomId","foreign_kingdom"},{"representedRulerId","ruler_a"},
                    {"authorityRevision",charterRevision},{"authorityCharter",grantedCharter},{"hostKingdomName","Player Kingdom"}
                };
                Dictionary<string,object> officialPayload = new Dictionary<string,object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"heroStringId",ReadString(established,"heroStringId","")},
                    {"playerHeroStringId","player"},{"worldDay",23d},{"ambassadorContext",context}
                };
                Dictionary<string,object> validContext = ValidateOfficialAmbassadorContext(campaignId,officialPayload);
                Dictionary<string,object> stalePayload = new Dictionary<string,object>(officialPayload,StringComparer.OrdinalIgnoreCase);
                Dictionary<string,object> staleAmbassadorContext = new Dictionary<string,object>(context,StringComparer.OrdinalIgnoreCase);
                staleAmbassadorContext["authorityRevision"] = charterRevision - 1;
                stalePayload["ambassadorContext"] = staleAmbassadorContext;
                Dictionary<string,object> staleContext = ValidateOfficialAmbassadorContext(campaignId,stalePayload);
                AddCapitalAmbassadorTestAssertion(assertions, "server_immutable_official_context",
                    validContext==null && staleContext!=null,
                    "The resident envoy and exact immutable authority revision are accepted; a stale snapshot is rejected.", staleContext);

                string grantedAction = ReadString(grantedPermission,"actionId","");
                Dictionary<string,object> grantedCandidate = new Dictionary<string,object>
                    {{"command","sign_"+grantedAction},{"terms",new Dictionary<string,object>{{"durationDays",90},{"tariff",4}}}};
                List<Dictionary<string,object>> allowed = FilterOfficialAmbassadorActionCandidates(campaignId,officialPayload,
                    new List<Dictionary<string,object>>{grantedCandidate},new Dictionary<string,object>(),"","",out Dictionary<string,object> grantedDecision);
                Dictionary<string,object> allianceCandidate = new Dictionary<string,object>
                    {{"command","sign_alliance"},{"terms",new Dictionary<string,object>{{"durationDays",120}}}};
                List<Dictionary<string,object>> referred = FilterOfficialAmbassadorActionCandidates(campaignId,officialPayload,
                    new List<Dictionary<string,object>>{allianceCandidate},new Dictionary<string,object>(),"","",out Dictionary<string,object> referralDecision);
                Dictionary<string,object> unknownCandidate = new Dictionary<string,object>
                    {{"command","cede_all_command_authority"},{"terms",new Dictionary<string,object>()}};
                List<Dictionary<string,object>> prohibited = FilterOfficialAmbassadorActionCandidates(campaignId,officialPayload,
                    new List<Dictionary<string,object>>{unknownCandidate},new Dictionary<string,object>(),"","",out Dictionary<string,object> prohibitedDecision);
                AddCapitalAmbassadorTestAssertion(assertions, "server_permission_referral_prohibition_matrix",
                    allowed.Count==1 && ReadString(grantedDecision,"authorityResult","")=="granted"
                    && referred.Count==0 && ReadString(referralDecision,"authorityResult","")=="referral_required"
                    && prohibited.Count==0 && ReadString(prohibitedDecision,"authorityResult","")=="prohibited",
                    "Granted commercial authority passes, alliance terms are referred, and unknown authority is refused.",
                    new Dictionary<string,object>{{"granted",grantedDecision},{"referred",referralDecision},{"prohibited",prohibitedDecision}});

                string referralId = ReadString(referralDecision,"referralId","");
                string referralHash = ReadString(referralDecision,"exactTermsHash","");
                Dictionary<string,object> referralRow;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    referralRow = QuerySql(connection,"SELECT * FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;",
                        new Dictionary<string,object>{{"id",referralId}}).FirstOrDefault();
                    ExecuteSql(connection,"UPDATE ambassador_referrals SET status='countered',counter_terms_json=$terms WHERE referral_id=$id;",
                        new Dictionary<string,object>{{"terms",ReadString(referralRow,"exact_terms_json","{}")},{"id",referralId}});
                }
                Dictionary<string,object> staleCounter = ForeignAmbassadorReferralRespondApi(new Dictionary<string,object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"sessionId","capital_session"},{"referralId",referralId},
                    {"termsHash","stale"},{"accept",false},{"worldDay",24d}
                });
                Dictionary<string,object> refusedCounter = ForeignAmbassadorReferralRespondApi(new Dictionary<string,object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"sessionId","capital_session"},{"referralId",referralId},
                    {"termsHash",referralHash},{"accept",false},{"worldDay",24d}
                });
                AddCapitalAmbassadorTestAssertion(assertions, "server_exact_terms_and_counteroffer_decision",
                    !ReadBool(staleCounter,"ok",true) && ReadBool(refusedCounter,"ok",false)
                    && ReadString(refusedCounter,"status","")=="counter_refused"
                    && ReadDouble(referralRow,"due_day",0d)>=24d && ReadDouble(referralRow,"due_day",0d)<=26d,
                    "Referral terms are hash-bound, due in one to three days, stale responses fail, and counteroffers require an explicit decision.",
                    new Dictionary<string,object>{{"referral",referralRow},{"stale",staleCounter},{"refused",refusedCounter}});

                Dictionary<string,object> playerLine = new Dictionary<string,object>{{"id","turn_player"},{"turnId","turn_player"},{"role","user"},{"text","We seek mutual peace and respectful cooperation."}};
                Dictionary<string,object> envoyLine = new Dictionary<string,object>{{"id","turn_envoy"},{"turnId","turn_envoy"},{"role","assistant"},{"text","I will relay these official terms in full."}};
                Dictionary<string,object> exchange = new Dictionary<string,object>{{"sessionId","official_session"},{"exchangeId","official_exchange"},{"turnIds",new List<string>{"turn_player","turn_envoy"}}};
                RecordOfficialAmbassadorExchange(campaignId,officialPayload,ReadString(established,"heroStringId",""),"player",playerLine,envoyLine,exchange,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                officialPayload["ambassadorDecision"] = referralDecision;
                Dictionary<string,object> persistedDecision = PersistOfficialAmbassadorDecision(campaignId,officialPayload,exchange,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                List<Dictionary<string,object>> archive = ReadOfficialAmbassadorArchiveLines(campaignId,officialPayload,20);
                Dictionary<string,object> decisionRow;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    decisionRow = QuerySql(connection,"SELECT * FROM ambassador_decisions WHERE decision_id=$id LIMIT 1;",
                        new Dictionary<string,object>{{"id",ReadString(persistedDecision,"decisionId","")}}).FirstOrDefault();
                }
                AddCapitalAmbassadorTestAssertion(assertions, "server_official_archive_and_decision_persistence",
                    archive.Count==2 && archive.All(x=>ReadString(x,"channel","")=="ambassador_official")
                    && ReadString(decisionRow,"terms_hash","")==referralHash && ReadString(decisionRow,"outcome","")=="refer",
                    "Both official turns and the validated, explainable AmbassadorDecision persist immediately with exact hashes.",
                    new Dictionary<string,object>{{"archive",archive},{"decision",decisionRow}});

                Dictionary<string,object> authorityRefresh = ForeignAmbassadorAuthorityApi(new Dictionary<string,object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","successor_refresh"},
                    {"expectedRevision",ReadLong(resident,"revision",0)},{"postingId",postingId},{"originRulerHeroStringId","ruler_successor"},
                    {"originRulerName","Successor"},{"charm",ReadInt(established,"charm",200)},{"rulerTrust",10},{"worldDay",24d}
                });
                List<Dictionary<string,object>> successorHistory = ReadJsonLinesFromPath(CharacterFile(campaignId,"ruler_successor","history","dialogue.jsonl"));
                AddCapitalAmbassadorTestAssertion(assertions, "server_successor_archive_and_charter_refresh",
                    ReadBool(authorityRefresh,"ok",false) && ReadLong(authorityRefresh,"authorityRevision",0)>charterRevision
                    && successorHistory.Count(x=>ReadString(x,"channel","")=="ambassador_official")>=2,
                    "A successor ruler inherits the official archive and receives a regenerated authority charter.",
                    new Dictionary<string,object>{{"refresh",authorityRefresh},{"historyCount",successorHistory.Count}});

                Dictionary<string,object> scenePayload = new Dictionary<string,object>(officialPayload,StringComparer.OrdinalIgnoreCase);
                Dictionary<string,object> successorContext = new Dictionary<string,object>(context,StringComparer.OrdinalIgnoreCase);
                successorContext["representedRulerId"] = "ruler_successor";
                scenePayload["ambassadorContext"] = successorContext;
                Dictionary<string,object> sceneSession = new Dictionary<string,object>
                {
                    {"channel","ambassador_official"},{"payload_json",Json.Serialize(scenePayload)},{"player_id","player"}
                };
                List<Dictionary<string,object>> hostileTurns = new List<Dictionary<string,object>>
                {
                    new Dictionary<string,object>{{"turn_id","pressure_a"},{"role","user"},{"text","A test threat."}}
                };
                sceneSession["session_id"]="pressure_one";
                Dictionary<string,object> pressureOne = FinalizeOfficialAmbassadorScene(campaignId,sceneSession,hostileTurns,new Dictionary<string,object>(),25d,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"strongly hostile");
                sceneSession["session_id"]="pressure_two";
                Dictionary<string,object> pressureTwo = FinalizeOfficialAmbassadorScene(campaignId,sceneSession,hostileTurns,new Dictionary<string,object>(),25d,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+1,"hostile");
                sceneSession["session_id"]="pressure_three";
                Dictionary<string,object> pressureThree = FinalizeOfficialAmbassadorScene(campaignId,sceneSession,hostileTurns,new Dictionary<string,object>(),25d,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2,"strongly conciliatory");
                AddCapitalAmbassadorTestAssertion(assertions, "server_pressure_values_cap_and_offset",
                    ReadInt(pressureOne,"appliedPressureDelta",0)==6 && ReadInt(pressureTwo,"appliedPressureDelta",99)==0
                    && ReadInt(pressureThree,"appliedPressureDelta",0)==-6 && ReadInt(pressureThree,"dailyAfter",99)==0,
                    "Official tone applies only discrete pressure, caps net daily change at +/-6, and permits later offset without a native relation mutation.",
                    new Dictionary<string,object>{{"strongHostile",pressureOne},{"cappedHostile",pressureTwo},{"offset",pressureThree}});

                Dictionary<string,object> providerTimeout = ClassifySyntheticProviderFault("timeout");
                AddCapitalAmbassadorTestAssertion(assertions, "server_provider_failure_class_once",
                    ReadBool(providerTimeout,"retryable",false) && !ReadBool(providerTimeout,"terminal",true)
                    && ReadInt(providerTimeout,"maxAttempts",0)==2,
                    "The representative provider-timeout class is bounded and retryable without implying an ambassador decision.", providerTimeout);

                Dictionary<string,object> transportWrite = ClassifySyntheticProviderFault("interrupted_write");
                AddCapitalAmbassadorTestAssertion(assertions, "server_transport_failure_class_once",
                    !ReadBool(transportWrite,"retryable",true) && ReadBool(transportWrite,"terminal",false)
                    && ReadString(transportWrite,"failureKind","")=="invalid_provider_response",
                    "The representative interrupted-transport class fails closed as an invalid response.", transportWrite);

                Dictionary<string,object> retryLimit = ClassifySyntheticProviderFault("retry_limit");
                AddCapitalAmbassadorTestAssertion(assertions, "server_retry_exhaustion_class_once",
                    !ReadBool(retryLimit,"retryable",true) && ReadBool(retryLimit,"terminal",false)
                    && ReadInt(retryLimit,"maxAttempts",0)==1,
                    "The representative exhausted-retry class remains terminal and cannot duplicate or fabricate an official decision.", retryLimit);

                Dictionary<string,object> pending = CreateAmbassadorReferral(campaignId,timelineId,postingId,"peace",
                    new Dictionary<string,object>{{"command","make_peace"}},"war_cancel_hash",25d);
                Dictionary<string,object> ended = ForeignAmbassadorEndApi(new Dictionary<string,object>
                {
                    {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","war_end"},{"expectedRevision",ReadLong(authorityRefresh,"revision",0)},
                    {"postingId",postingId},{"reason","war_recalled"},{"worldDay",25.5d}
                });
                string pendingStatus;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    pendingStatus = ReadString(QuerySql(connection,"SELECT status FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;",
                        new Dictionary<string,object>{{"id",ReadString(pending,"referralId","")}}).FirstOrDefault(),"status","");
                AddCapitalAmbassadorTestAssertion(assertions, "server_war_recall_and_referral_cancellation",
                    ReadBool(ended,"ok",false) && ReadString(ended,"status","")=="ended" && pendingStatus=="cancelled_war",
                    "War recall ends the posting and cancels pending ruler referrals.",
                    new Dictionary<string,object>{{"ended",ended},{"pendingReferralStatus",pendingStatus}});
            }
            catch (Exception ex)
            {
                AddCapitalAmbassadorTestAssertion(assertions,"server_matrix_exception",false,
                    "The isolated capital/ambassador server matrix completed without an exception.",
                    new Dictionary<string,object>{{"type",ex.GetType().FullName},{"message",ex.Message},{"stack",ex.StackTrace??""}});
            }
            finally
            {
                cleanupComplete = CleanupCapitalAmbassadorServerFixture(campaignId);
            }
            AddCapitalAmbassadorTestAssertion(assertions,"server_fixture_cleanup",cleanupComplete,
                "The synthetic campaign directory and PostgreSQL schema were removed after the matrix.",
                new Dictionary<string,object>{{"campaignId",campaignId}});
            int passed = assertions.Count(x=>ReadBool(x,"passed",false));
            return new Dictionary<string,object>
            {
                {"ok",passed==assertions.Count},{"profile","capital_ambassador_server_matrix"},{"runId",runId},
                {"passedCount",passed},{"failedCount",assertions.Count-passed},{"totalCount",assertions.Count},
                {"assertions",assertions},{"cleanupComplete",cleanupComplete},{"syntheticCampaignId",campaignId}
            };
        }

        private static Dictionary<string,object> ValidCapitalAmbassadorCandidate(string id, int charm, int trust, int travelDays)
        {
            return new Dictionary<string,object>
            {
                {"heroStringId",id},{"charm",charm},{"rulerTrust",trust},{"travelDays",travelDays},
                {"isAdult",true},{"isAlive",true},{"isActive",true},{"isLordOrLady",true},
                {"isPrisoner",false},{"isRuler",false},{"isGovernor",false},{"isPartyLeader",false},{"isActiveEnvoy",false}
            };
        }

        private static Dictionary<string,object> InvalidCapitalAmbassadorCandidate(string id, string reason)
        {
            Dictionary<string,object> value = ValidCapitalAmbassadorCandidate(id,200,0,1);
            switch (reason)
            {
                case "charm": value["charm"]=49; break;
                case "adult": value["isAdult"]=false; break;
                case "alive": value["isAlive"]=false; break;
                case "active": value["isActive"]=false; break;
                case "prisoner": value["isPrisoner"]=true; break;
                case "ruler": value["isRuler"]=true; break;
                case "governor": value["isGovernor"]=true; break;
                case "party": value["isPartyLeader"]=true; break;
                case "envoy": value["isActiveEnvoy"]=true; break;
                case "lord": value["isLordOrLady"]=false; break;
            }
            return value;
        }

        private static void AddCapitalAmbassadorTestAssertion(List<Dictionary<string,object>> assertions,
            string id, bool passed, string summary, object data)
        {
            assertions.Add(new Dictionary<string,object>
            {
                {"caseId",id},{"passed",passed},{"summary",summary},{"data",data??new Dictionary<string,object>()}
            });
        }

        private static bool CleanupCapitalAmbassadorServerFixture(string campaignId)
        {
            try
            {
                InvalidateCampaignSchemaCaches(campaignId);
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                TryDeleteDirectory(CampaignDirectory(campaignId));
                return !ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && !Directory.Exists(CampaignDirectory(campaignId));
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
