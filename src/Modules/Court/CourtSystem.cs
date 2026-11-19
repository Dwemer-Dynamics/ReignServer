using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class CourtService
    {
        private const int CourtAgendaLimit = 6;

        internal static void EnsureCourtSchema(ReignDbConnection connection)
        {
            CourtSchema.CreateCourtSessions(connection);
            CourtSchema.CreateIndexCourtSessionsScope(connection);
            CourtSchema.EnsureColumnCourtSessionsScope(connection);
            CourtSchema.EnsureColumnCourtSessionsCapitalSettlementId(connection);
            CourtStore.UpdateCourtSessions(connection, new Dictionary<string, object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
            CourtSchema.CreateCourtMatters(connection);
            CourtSchema.CreateIndexCourtMattersScope(connection);
            CourtSchema.CreateIndexCourtMattersSource(connection);
            CourtSchema.CreateCourtAgenda(connection);
            CourtSchema.CreateIndexCourtAgendaDay(connection);
            CourtSchema.CreateCourtOfficeHistory(connection);
            CourtSchema.CreateIndexCourtOfficeActive(connection);
            CourtStore.UpdateCourtOfficeHistory(connection);
            CourtStore.UpdateCourtOfficeHistory2(connection);
            CourtSchema.CreateCourtRegentHistory(connection);
            CourtSchema.CreateIndexCourtRegentActive(connection);
            CourtSchema.CreateAmbassadorPostings(connection);
            CourtSchema.CreateIndexAmbassadorActive(connection);
            CourtSchema.CreateIntelligenceOperations(connection);
            CourtSchema.CreateIndexIntelligenceActive(connection);
            CourtSchema.CreateCourtPlots(connection);
            CourtSchema.CreateIndexCourtPlotsScope(connection);
            CourtSchema.CreateCourtCommands(connection);
            CourtSchema.CreateCourtAudit(connection);

            CourtSchema.EnsureColumnObligationsObligationType(connection);
            CourtSchema.EnsureColumnObligationsDueDay(connection);
            CourtSchema.EnsureColumnObligationsTermsHash(connection);
            CourtSchema.EnsureColumnObligationsBreachRuleJson(connection);
            CourtSchema.EnsureColumnObligationsResolutionJson(connection);
            CourtSchema.EnsureColumnObligationsTimelineId(connection);
            CourtSchema.EnsureColumnObligationsRevision(connection);
            EnsureForeignAmbassadorSchema(connection);
        }

        private sealed class CourtApiContext
        {
            public string CampaignId;
            public string TimelineId;
            public string CommandId;
            public string ViewerId;
            public double WorldDay;
            public long ExpectedRevision;
            public string SessionId;
            public string Scope;
            public string HostSettlementId;
        }

        private static CourtApiContext ReadCourtContext(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            return new CourtApiContext
            {
                CampaignId = ReadString(payload, "campaignId", "default"),
                TimelineId = ReadString(payload, "timelineId", "main"),
                CommandId = ReadFirstString(payload, "commandId", "command_id"),
                ViewerId = ReadFirstString(payload, "viewerHeroStringId", "playerHeroStringId", "playerId"),
                WorldDay = ReadDouble(payload, "worldDay", 0d),
                ExpectedRevision = ReadLong(payload, "expectedRevision", 0L),
                SessionId = ReadFirstString(payload, "sessionId", "courtSessionId"),
                Scope = ReadString(payload, "courtScope", ""),
                HostSettlementId = ReadString(payload, "hostSettlementStringId", "")
            };
        }

        internal static Dictionary<string, object> CourtSessionOpenApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            if (string.IsNullOrWhiteSpace(ctx.CommandId)) return CourtError("commandId is required for session open.");
            string authority = ReadString(payload, "authority", "").Trim().ToLowerInvariant();
            if (!string.Equals(authority, "royal", StringComparison.Ordinal) || !ReadBool(payload, "playerIsKingdomRuler", false))
                return CourtError("Court sessions require the player to be the current ruler of a kingdom.");
            string sessionId = FirstNonEmpty(ReadFirstString(payload, "sessionId"), "court_session_" + Guid.NewGuid().ToString("N"));
            string scope = ReadString(payload, "scope", ReadString(payload, "courtScope", "local")).Trim().ToLowerInvariant();
            string hostSettlementId = ReadString(payload, "hostSettlementStringId", "");
            string capitalSettlementId = ReadString(payload, "capitalSettlementStringId", "");
            if (scope != "local" && scope != "capital") return CourtError("Court scope must be local or capital.");
            if (scope == "capital" && (string.IsNullOrWhiteSpace(capitalSettlementId)
                || !string.Equals(hostSettlementId, capitalSettlementId, StringComparison.OrdinalIgnoreCase)))
                return CourtError("Capital scope requires the active designated capital as the host settlement.");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> existing = CourtStore.SelectCourtSessions(connection, new Dictionary<string, object>{{"id",sessionId},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();
                long revision = existing == null ? 0L : ReadLong(existing, "revision", 0L);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                CourtStore.UpsertCourtSessions(connection, new Dictionary<string, object>
                {
                    {"id",sessionId},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"authority",authority},
                    {"host",hostSettlementId},{"day",ctx.WorldDay},{"agenda",ReadInt(payload,"lastAgendaDay",-1)},
                    {"scope",scope},{"capital",capitalSettlementId},
                    {"revision",revision+1},{"snapshot",Json.Serialize(ReadDictionary(payload,"nativeSnapshot")??new Dictionary<string,object>())},
                    {"created",existing==null?ts:ReadLong(existing,"created_ts",ts)},{"ts",ts}
                });
                Dictionary<string, object> response = CourtMutationResult(ctx, sessionId, revision + 1, "court_session_opened", new ArrayList());
                response["scope"] = scope;
                response["capitalSettlementId"] = capitalSettlementId;
                StoreCourtCommand(connection, ctx, "session_open", response);
                AddCourtAudit(connection, ctx, "session", sessionId, "open", "completed", response);
                return response;
            }
        }

        internal static Dictionary<string, object> CourtSessionTickApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string sessionId = ReadString(payload, "sessionId", "");
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || string.IsNullOrWhiteSpace(sessionId)) return CourtError("commandId and sessionId are required.");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> session = ReadCourtSession(connection, ctx, sessionId);
                if (session == null) return CourtError("Court session was not found in this timeline.");
                long revision = ReadLong(session, "revision", 0L);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                string state = ReadString(payload, "state", ReadString(session, "state", "sitting"));
                if (!CourtSessionStateAllowed(state)) return CourtError("Invalid court session state.");
                string requestedScope=ReadString(payload,"courtScope",ReadString(session,"scope","local")).ToLowerInvariant();
                string requestedCapital=ReadString(payload,"capitalSettlementStringId",ReadString(session,"capital_settlement_id",""));
                if(requestedScope!="local"&&requestedScope!="capital")return CourtError("Court scope must be local or capital.");
                if(requestedScope=="capital"&&!string.Equals(ReadString(session,"host_settlement_id",""),requestedCapital,StringComparison.OrdinalIgnoreCase))return CourtError("Capital scope requires the session host to remain the active capital.");
                CourtStore.UpdateCourtSessions2(connection, new Dictionary<string, object>{{"state",state},{"matter",ReadString(payload,"activeMatterId",ReadString(session,"active_matter_id",""))},{"time",ReadInt(payload,"timeMode",ReadInt(session,"time_mode",0))},
                    {"reason",ReadString(payload,"interruptionReason",ReadString(session,"interruption_reason",""))},{"snapshot",Json.Serialize(ReadDictionary(payload,"nativeSnapshot")??new Dictionary<string,object>())},
                    {"scope",requestedScope},{"capital",requestedCapital},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",sessionId}});
                TickIntelligenceOperations(connection, ctx);
                ArrayList missionActions=TickAmbassadorPostings(connection, ctx, sessionId);
                Dictionary<string, object> response = CourtMutationResult(ctx, sessionId, revision + 1, "court_session_ticked", missionActions);
                StoreCourtCommand(connection, ctx, "session_tick", response);
                return response;
            }
        }

        internal static Dictionary<string, object> CourtSessionCloseApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string sessionId = ReadString(payload, "sessionId", "");
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || string.IsNullOrWhiteSpace(sessionId)) return CourtError("commandId and sessionId are required.");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> session = ReadCourtSession(connection, ctx, sessionId);
                if (session == null) return CourtError("Court session was not found in this timeline.");
                long revision = ReadLong(session, "revision", 0L);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                CourtStore.UpdateCourtSessions3(connection, new Dictionary<string, object>{{"day",ctx.WorldDay},{"reason",ReadString(payload,"reason","")},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",sessionId}});
                Dictionary<string, object> response = CourtMutationResult(ctx, sessionId, revision + 1, "court_session_closed", new ArrayList());
                StoreCourtCommand(connection, ctx, "session_close", response);
                AddCourtAudit(connection, ctx, "session", sessionId, "close", "completed", response);
                return response;
            }
        }

        internal static Dictionary<string, object> CourtAgendaBuildApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string sessionId = ReadString(payload, "sessionId", "");
            int agendaDay = ReadInt(payload, "agendaDay", (int)Math.Floor(ctx.WorldDay));
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || string.IsNullOrWhiteSpace(sessionId)) return CourtError("commandId and sessionId are required.");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> session = ReadCourtSession(connection, ctx, sessionId);
                if (session == null) return CourtError("Court session was not found in this timeline.");
                long revision = ReadLong(session, "revision", 0L);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                foreach (Dictionary<string, object> candidate in ReadDictionaryList(payload, "candidateMatters")) UpsertCourtMatterCandidate(connection, ctx, sessionId, candidate);
                ExpireCourtMatters(connection, ctx, agendaDay);

                List<Dictionary<string, object>> candidates = CourtStore.SelectCourtMatters(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}})
                    .GroupBy(x=>ReadString(x,"source_key",""),StringComparer.OrdinalIgnoreCase).Select(x=>x.First()).ToList();
                if(string.Equals(ReadString(session,"scope","local"),"local",StringComparison.OrdinalIgnoreCase))
                {
                    string localHost=ReadString(session,"host_settlement_id","");
                    candidates=candidates.Where(x=>string.Equals(ReadString(x,"settlement_id",""),localHost,StringComparison.OrdinalIgnoreCase)).ToList();
                }
                List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
                bool major = false, ambient = false;
                foreach (Dictionary<string, object> matter in candidates)
                {
                    bool isMajor = ReadBool(matter,"is_major",false), isAmbient = ReadBool(matter,"is_ambient",false);
                    if ((isMajor&&major)||(isAmbient&&ambient)) continue;
                    selected.Add(matter); major|=isMajor; ambient|=isAmbient;
                    if (selected.Count >= CourtAgendaLimit) break;
                }
                CourtStore.DeleteCourtAgenda(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"session",sessionId},{"day",agendaDay}});
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                for(int i=0;i<selected.Count;i++)
                {
                    string matterId=ReadString(selected[i],"matter_id","");
                    CourtStore.InsertCourtAgenda(connection, new Dictionary<string, object>{{"id","agenda_"+Guid.NewGuid().ToString("N")},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"session",sessionId},{"matter",matterId},{"day",agendaDay},{"slot",i},{"ts",ts}});
                    CourtStore.UpdateCourtMatters(connection, new Dictionary<string, object>{{"day",agendaDay},{"ts",ts},{"id",matterId}});
                }
                CourtStore.UpdateCourtSessions4(connection, new Dictionary<string, object>{{"day",agendaDay},{"ts",ts},{"id",sessionId}});
                Dictionary<string, object> response = CourtMutationResult(ctx, sessionId, revision+1, "court_agenda_built", new ArrayList());
                response["agendaDay"] = agendaDay;
                response["agenda"] = CourtStore.SelectCourtAgenda(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"session",sessionId},{"day",agendaDay}});
                StoreCourtCommand(connection, ctx, "agenda_build", response);
                AddCourtAudit(connection,ctx,"agenda",sessionId,"build","completed",response);
                return response;
            }
        }

        internal static Dictionary<string, object> CourtHomeApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureMarriageCourtMatters(connection, ctx);
                List<Dictionary<string, object>> matters = VisibleCourtRows(connection,ctx,CourtStore.SelectCourtMatters2(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}));
                Dictionary<string, object> activeSession = ReadActiveCourtSession(connection, ctx);
                bool localScope = string.Equals(ReadString(activeSession, "scope", "local"), "local", StringComparison.OrdinalIgnoreCase);
                string localHost = ReadString(activeSession, "host_settlement_id", ctx.HostSettlementId);
                if (localScope)
                    matters = matters.Where(x => string.Equals(ReadString(x, "settlement_id", ""), localHost, StringComparison.OrdinalIgnoreCase)).ToList();
                List<Dictionary<string, object>> rumors = VisibleRumors(connection,ctx).Take(4).ToList();
                return new Dictionary<string, object>
                {
                    {"ok",true},{"campaignId",ctx.CampaignId},{"timelineId",ctx.TimelineId},
                    {"scope",localScope?"local":"capital"},{"hostSettlementId",localHost},
                    {"dailyAgenda",matters.Where(x=>ReadString(x,"state","")=="scheduled").Take(localScope?100:6).ToList()},
                    {"peopleMatters",localScope?new List<Dictionary<string,object>>():matters.Where(x=>Program.ContainsAny(ReadString(x,"kind",""),"obligation","relationship","marriage","private_counsel")).Take(6).ToList()},
                    {"ambassadors",localScope?new List<Dictionary<string,object>>():CourtStore.SelectAmbassadorPostings(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}})},
                    {"operations",localScope?new List<Dictionary<string,object>>():CourtStore.SelectIntelligenceOperations(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}})},
                    {"rumors",localScope?new List<Dictionary<string,object>>():rumors},{"offices",localScope?new List<Dictionary<string,object>>():CourtStore.SelectCourtOfficeHistory(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}})},
                    {"regent",localScope?null:CourtStore.SelectCourtRegentHistory(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault()},
                    {"obligations",localScope?new List<Dictionary<string,object>>():VisibleCourtRows(connection,ctx,CourtStore.SelectObligations(connection, new Dictionary<string,object>{{"timeline",ctx.TimelineId}}))},
                    {"plots",localScope?new List<Dictionary<string,object>>():VisibleCourtRows(connection,ctx,CourtStore.SelectCourtPlots3(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}))},
                    {"serverCapabilities",new ArrayList{"generated_matters","mutations","obligations","plots","intelligence"}}
                };
            }
        }

        internal static Dictionary<string, object> CourtViewApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string tab = ReadString(payload,"tab","court").ToLowerInvariant();
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                Dictionary<string, object> activeSession = ReadActiveCourtSession(connection, ctx);
                bool localScope = string.Equals(ReadString(activeSession, "scope", "local"), "local", StringComparison.OrdinalIgnoreCase);
                string localHost = ReadString(activeSession, "host_settlement_id", ctx.HostSettlementId);
                if (localScope && tab != "court") return CourtError("Only the host-settlement docket is available in local Rule Mode.");
                Dictionary<string, object> result=new Dictionary<string, object>{{"ok",true},{"tab",tab},{"campaignId",ctx.CampaignId},{"timelineId",ctx.TimelineId}};
                Dictionary<string, object> scope=new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}};
                if(tab=="court")
                {
                    List<Dictionary<string,object>> courtMatters=VisibleCourtRows(connection,ctx,CourtStore.SelectCourtMatters3(connection, scope));
                    if(localScope) courtMatters=courtMatters.Where(x=>string.Equals(ReadString(x,"settlement_id",""),localHost,StringComparison.OrdinalIgnoreCase)).ToList();
                    result["matters"]=courtMatters;
                    result["offices"]=localScope?new List<Dictionary<string,object>>():CourtStore.SelectCourtOfficeHistory2(connection, scope);
                    result["obligations"]=localScope?new List<Dictionary<string,object>>():VisibleCourtRows(connection,ctx,CourtStore.SelectObligations2(connection, scope));
                }
                else if(tab=="subjects")
                {
                    result["marriageProposals"]=CourtStore.SelectMarriageEvaluations(connection, scope);
                    result["plots"]=VisibleCourtRows(connection,ctx,CourtStore.SelectCourtPlots4(connection, scope));
                }
                else if(tab=="diplomacy") result["marriageProposals"]=CourtStore.SelectMarriageEvaluations2(connection, scope);
                else if(tab=="ambassadors") result["postings"]=CourtStore.SelectAmbassadorPostings2(connection, scope);
                else if(tab=="spymaster")
                {
                    result["operations"]=CourtStore.SelectIntelligenceOperations2(connection, scope);
                    result["rumors"]=VisibleRumors(connection,ctx).ToList();
                    result["plots"]=VisibleCourtRows(connection,ctx,CourtStore.SelectCourtPlots4(connection, scope));
                }
                else result["serverRecords"]=new ArrayList();
                return result;
            }
        }

        internal static Dictionary<string, object> CourtMatterStartApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string matterId=ReadString(payload,"matterId","");
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(matterId))return CourtError("commandId and matterId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                Dictionary<string,object> matter=ReadCourtMatter(connection,ctx,matterId);if(matter==null)return CourtError("Matter was not found in this timeline.");
                if(!CourtMatterAllowedInActiveScope(connection,ctx,matter))return CourtError("This matter is outside the host-settlement docket available in local Rule Mode.");
                if(!CourtMatterVisibilityAllows(matter,ctx.ViewerId))return CourtError("This matter is not known to the viewer.");
                long revision=ReadLong(matter,"revision",0L);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                string state=ReadString(matter,"state","");if(CourtMatterTerminal(state))return CourtError("The matter is already closed.");
                CourtStore.UpdateCourtMatters2(connection, new Dictionary<string, object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",matterId}});
                Dictionary<string,object> response=CourtMutationResult(ctx,matterId,revision+1,"court_matter_started",new ArrayList());response["matter"]=ReadCourtMatter(connection,ctx,matterId);
                StoreCourtCommand(connection,ctx,"matter_start",response);AddCourtAudit(connection,ctx,"matter",matterId,"start","completed",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtMatterRespondApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string matterId=ReadString(payload,"matterId","");
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(matterId))return CourtError("commandId and matterId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                Dictionary<string,object> matter=ReadCourtMatter(connection,ctx,matterId);if(matter==null||!CourtMatterVisibilityAllows(matter,ctx.ViewerId))return CourtError("Matter was not found or is not known to the viewer.");
                if(!CourtMatterAllowedInActiveScope(connection,ctx,matter))return CourtError("This matter is outside the host-settlement docket available in local Rule Mode.");
                long revision=ReadLong(matter,"revision",0L);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                string state=ReadString(matter,"state","");if(CourtMatterTerminal(state))return CourtError("The matter is already closed.");
                Dictionary<string,object> dialoguePayload=new Dictionary<string, object>(payload??new Dictionary<string, object>(),StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"]="court_audience",["sceneContext"]="Court audience. The model writes dialogue and characterization only. It must not invent, promise, or apply mechanical effects.",
                    ["courtMatterId"]=matterId,["courtMatterTitle"]=ReadString(matter,"title",""),["courtMatterSummary"]=ReadString(matter,"summary",""),
                    ["allowedDecisionOptions"]=new ArrayList(),["disableActionPlanning"]=true
                };
                Dictionary<string,object> reply=Program.DialogueRespond(dialoguePayload);
                if(!ReadBool(reply,"ok",false))return reply;
                reply.Remove("actions");reply.Remove("worldActions");reply.Remove("actionGate");reply["mechanicalEffectsAllowed"]=false;reply["matterId"]=matterId;
                CourtStore.UpdateCourtMatters3(connection, new Dictionary<string, object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",matterId}});
                reply["revision"]=ReadLong(ReadCourtMatter(connection,ctx,matterId),"revision",0L);
                StoreCourtCommand(connection,ctx,"matter_respond",reply);
                AddCourtAudit(connection,ctx,"dialogue",matterId,"respond","completed",new Dictionary<string,object>{{"dialogueOnly",true},{"reply",reply}});return reply;
            }
        }

        internal static Dictionary<string, object> CourtMatterResolveApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string matterId=ReadString(payload,"matterId","");string status=ReadString(payload,"executionStatus","").ToLowerInvariant();
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(matterId))return CourtError("commandId and matterId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                Dictionary<string,object> matter=ReadCourtMatter(connection,ctx,matterId);if(matter==null)return CourtError("Matter was not found in this timeline.");
                if(!CourtMatterAllowedInActiveScope(connection,ctx,matter))return CourtError("This matter is outside the host-settlement docket available in local Rule Mode.");
                long revision=ReadLong(matter,"revision",0L);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                if(status=="completed"||status=="failed"||status=="wedding_cancelled")
                {
                    if(ReadString(matter,"state","")!="resolving")return CourtError("Only a resolving matter may receive a native execution report.");
                    if(status=="completed"&&ReadString(matter,"source_key","").StartsWith("marriage_evaluation:",StringComparison.Ordinal)
                        &&!ReadDictionaryList(ReadDictionary(payload,"nativeReceipt"),"actions").Any(x=>ReadBool(x,"marriageVerified",false)&&ReadBool(x,"paymentVerified",false)))
                        return CourtError("Marriage completion requires reciprocal spouse and payment evidence.");
                    string next=status=="completed"?"resolved":status=="wedding_cancelled"?"invalidated":"awaiting_decision";string receiptJson=Json.Serialize(ReadDictionary(payload,"nativeReceipt")??new Dictionary<string,object>());
                    CourtStore.UpdateCourtMatters4(connection, new Dictionary<string, object>{{"state",next},{"day",status=="completed"?ctx.WorldDay:-1d},{"receipt",receiptJson},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",matterId}});
                    UpdateMarriageEvaluationForMatter(connection,matter,status=="completed"?"executed":status=="wedding_cancelled"?"obsolete":"player_court_pending",ctx.WorldDay,status=="completed");
                    if(status=="completed")UpdateObligationForMatter(connection,matter,"resolved",ctx.WorldDay,new Dictionary<string,object>{{"nativeReceipt",ReadDictionary(payload,"nativeReceipt")??new Dictionary<string,object>()}});
                    Dictionary<string,object> report=CourtMutationResult(ctx,matterId,revision+1,status=="completed"?"court_matter_resolved":"court_native_execution_failed",new ArrayList());
                    StoreCourtCommand(connection,ctx,"matter_report",report);AddCourtAudit(connection,ctx,"matter",matterId,"execution_report",status,report);return report;
                }

                if(CourtMatterTerminal(ReadString(matter,"state","")))return CourtError("The matter is already closed.");
                if(ReadString(matter,"state","")=="resolving")return CourtError("The signed matter is awaiting its native outcome.");
                string optionId=ReadString(payload,"optionId","");Dictionary<string,object> option=CourtJsonList(ReadString(matter,"options_json","[]")).FirstOrDefault(x=>SameCourtText(ReadString(x,"optionId",""),optionId));
                if(option==null)return CourtError("The selected option is not part of the coded matter definition.");
                Dictionary<string,object> consequences=ReadDictionary(option,"consequences")??new Dictionary<string,object>();string canonical=CourtCanonicalJson(consequences);string expectedHash=FirstNonEmpty(ReadString(option,"termsHash",""),CourtHash(canonical));
                string suppliedHash=ReadString(payload,"termsHash","");if(!SameCourtText(expectedHash,suppliedHash))return CourtError("The canonical terms hash does not match the stored decision option.");
                ArrayList nativeActions=ValidateCourtNativeActions(consequences,out string actionError);if(!string.IsNullOrWhiteSpace(actionError))return CourtError(actionError);
                bool refused=SameCourtText(optionId,"refuse");string nextState=nativeActions.Count>0?"resolving":refused?"refused":"resolved";
                Dictionary<string,object> receipt=new Dictionary<string, object>{{"receiptId","court_receipt_"+Guid.NewGuid().ToString("N")},{"commandId",ctx.CommandId},{"matterId",matterId},{"optionId",optionId},{"termsHash",expectedHash},{"nativeActionCount",nativeActions.Count}};
                CourtStore.UpdateCourtMatters5(connection, new Dictionary<string, object>{{"state",nextState},{"option",optionId},{"hash",expectedHash},{"day",nativeActions.Count==0?ctx.WorldDay:-1d},{"receipt",Json.Serialize(receipt)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",matterId}});
                if(ReadString(matter,"source_key","").StartsWith("marriage_evaluation:",StringComparison.OrdinalIgnoreCase))
                {
                    string marriageStatus=nativeActions.Count>0?"court_resolving":SameCourtText(optionId,"counter")?"countered_by_player":"rejected_by_player";
                    UpdateMarriageEvaluationForMatter(connection,matter,marriageStatus,ctx.WorldDay,false);
                }
                if(nativeActions.Count==0&&ReadString(matter,"source_key","").StartsWith("obligation:",StringComparison.OrdinalIgnoreCase))
                    UpdateObligationForMatter(connection,matter,refused?"breached":"resolved",ctx.WorldDay,new Dictionary<string,object>{{"optionId",optionId}});
                Dictionary<string,object> response=CourtMutationResult(ctx,matterId,revision+1,nativeActions.Count>0?"court_native_actions_validated":refused?"court_matter_refused":"court_matter_resolved",nativeActions);response["receipt"]=receipt;
                StoreCourtCommand(connection,ctx,"matter_resolve",response);AddCourtAudit(connection,ctx,"matter",matterId,"resolve",nextState,response);return response;
            }
        }

        internal static Dictionary<string, object> CourtOfficeAssignApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string office=CourtCanonicalOffice(ReadString(payload,"office",""));string heroId=ReadString(payload,"heroStringId","");
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||!CourtOfficeAllowed(office)||string.IsNullOrWhiteSpace(heroId))return CourtError("commandId, a valid major office, and heroStringId are required.");
            Dictionary<string,object> eligibility=ReadDictionary(payload,"eligibility")??new Dictionary<string,object>();
            if(!ReadBool(eligibility,"isAdult",false)||!ReadBool(eligibility,"isFree",false)||!ReadBool(eligibility,"inCourtScope",false))return CourtError("Native eligibility does not permit this appointment.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                Dictionary<string,object> active=CourtStore.SelectCourtOfficeHistory3(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"office",office}}).FirstOrDefault();
                long revision=active==null?0:ReadLong(active,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                if(CourtStore.SelectCourtOfficeHistory4(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"hero",heroId}}).Any())return CourtError("One person may hold only one major office.");
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();if(active!=null)CourtStore.UpdateCourtOfficeHistory3(connection, new Dictionary<string, object>{{"day",ctx.WorldDay},{"ts",ts},{"id",ReadString(active,"assignment_id","")}});
                string id="office_"+Guid.NewGuid().ToString("N");CourtStore.InsertCourtOfficeHistory(connection, new Dictionary<string, object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"office",office},{"hero",heroId},{"day",ctx.WorldDay},{"payload",Json.Serialize(payload)},{"ts",ts}});
                ArrayList actions=new ArrayList{new Dictionary<string,object>{{"type","court_assign_office"},{"assignmentId",id},{"office",office},{"heroStringId",heroId}}};Dictionary<string,object> response=CourtMutationResult(ctx,id,1,"court_office_assigned",actions);
                StoreCourtCommand(connection,ctx,"office_assign",response);AddCourtAudit(connection,ctx,"office",id,"assign","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtOfficeDismissApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string office=CourtCanonicalOffice(ReadString(payload,"office",""));if(string.IsNullOrWhiteSpace(ctx.CommandId)||!CourtOfficeAllowed(office))return CourtError("commandId and a valid major office are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> active=CourtStore.SelectCourtOfficeHistory3(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"office",office}}).FirstOrDefault();if(active==null)return CourtError("That office is already vacant.");
                long revision=ReadLong(active,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);string id=ReadString(active,"assignment_id","");CourtStore.UpdateCourtOfficeHistory4(connection, new Dictionary<string, object>{{"day",ctx.WorldDay},{"reason",ReadString(payload,"reason","dismissed")},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
                ArrayList actions=new ArrayList{new Dictionary<string,object>{{"type","court_dismiss_office"},{"assignmentId",id},{"office",office},{"heroStringId",ReadString(active,"hero_id","")}}};Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_office_dismissed",actions);StoreCourtCommand(connection,ctx,"office_dismiss",response);AddCourtAudit(connection,ctx,"office",id,"dismiss","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtRegentAssignApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string heroId=ReadString(payload,"heroStringId","");Dictionary<string,object> eligibility=ReadDictionary(payload,"eligibility")??new Dictionary<string,object>();
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(heroId))return CourtError("commandId and heroStringId are required.");
            if(!ReadBool(eligibility,"isAdult",false)||!ReadBool(eligibility,"isFree",false)||!ReadBool(eligibility,"inCourtScope",false))return CourtError("Native eligibility does not permit this Regent appointment.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                Dictionary<string,object> active=CourtStore.SelectCourtRegentHistory(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();
                long revision=active==null?0:ReadLong(active,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();if(active!=null)CourtStore.UpdateCourtRegentHistory(connection, new Dictionary<string,object>{{"day",ctx.WorldDay},{"ts",ts},{"id",ReadString(active,"assignment_id","")}});
                string id="regent_"+Guid.NewGuid().ToString("N");CourtStore.InsertCourtRegentHistory(connection, new Dictionary<string,object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"hero",heroId},{"day",ctx.WorldDay},{"payload",Json.Serialize(payload)},{"ts",ts}});
                Dictionary<string,object> response=CourtMutationResult(ctx,id,1,"court_regent_assigned",new ArrayList());response["heroStringId"]=heroId;StoreCourtCommand(connection,ctx,"regent_assign",response);AddCourtAudit(connection,ctx,"regent",id,"assign","completed",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtRegentDismissApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);if(string.IsNullOrWhiteSpace(ctx.CommandId))return CourtError("commandId is required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> active=CourtStore.SelectCourtRegentHistory(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();if(active==null)return CourtError("No Regent is assigned.");
                long revision=ReadLong(active,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);string id=ReadString(active,"assignment_id","");CourtStore.UpdateCourtRegentHistory2(connection, new Dictionary<string,object>{{"day",ctx.WorldDay},{"reason",ReadString(payload,"reason","dismissed_by_ruler")},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
                Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_regent_dismissed",new ArrayList());StoreCourtCommand(connection,ctx,"regent_dismiss",response);AddCourtAudit(connection,ctx,"regent",id,"dismiss","completed",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtAmbassadorAssignApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string heroId=ReadString(payload,"heroStringId","");string kingdomId=ReadString(payload,"targetKingdomStringId","");Dictionary<string,object> eligibility=ReadDictionary(payload,"eligibility")??new Dictionary<string,object>();
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(heroId)||string.IsNullOrWhiteSpace(kingdomId))return CourtError("commandId, heroStringId, and targetKingdomStringId are required.");
            if(!ReadBool(eligibility,"isAdult",false)||!ReadBool(eligibility,"isFree",false)||!ReadBool(eligibility,"inCourtScope",false))return CourtError("Native eligibility does not permit this ambassador posting.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> args=new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}};
                if(CourtStore.SelectAmbassadorPostings3(connection, args).Count>=3)return CourtError("Royal court supports at most three active ambassador postings.");
                args["hero"]=heroId;if(CourtStore.SelectAmbassadorPostings4(connection, args).Any())return CourtError("That hero already has an active ambassador posting.");
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string id="ambassador_"+Guid.NewGuid().ToString("N");double arrival=ReadDouble(payload,"arrivalDay",ctx.WorldDay+Math.Max(1d,ReadDouble(payload,"travelDays",3d)));
                CourtStore.InsertAmbassadorPostings(connection, new Dictionary<string, object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"hero",heroId},{"kingdom",kingdomId},{"mission",ReadString(payload,"missionType","public_information")},{"terms",CourtCanonicalJson(ReadDictionary(payload,"missionTerms")??new Dictionary<string,object>())},{"day",ctx.WorldDay},{"arrival",arrival},{"payload",Json.Serialize(payload)},{"ts",ts}});
                ArrayList actions=new ArrayList{new Dictionary<string,object>{{"type","court_assign_ambassador"},{"postingId",id},{"heroStringId",heroId},{"targetKingdomStringId",kingdomId}}};Dictionary<string,object> response=CourtMutationResult(ctx,id,1,"court_ambassador_assigned",actions);StoreCourtCommand(connection,ctx,"ambassador_assign",response);AddCourtAudit(connection,ctx,"ambassador",id,"assign","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtAmbassadorMissionApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string id=ReadString(payload,"postingId","");if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(id))return CourtError("commandId and postingId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> row=ReadCourtScopedRow(connection,ctx,"ambassador_postings","posting_id",id);if(row==null)return CourtError("Ambassador posting was not found in this timeline.");long revision=ReadLong(row,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);if(Program.ContainsAny(ReadString(row,"status",""),"recalled","replaced"))return CourtError("The posting is no longer active.");
                Dictionary<string,object> terms=ReadDictionary(payload,"missionTerms")??new Dictionary<string,object>();string hash=CourtHash(CourtCanonicalJson(terms));string supplied=ReadString(payload,"termsHash",hash);if(!SameCourtText(hash,supplied))return CourtError("Mission terms hash does not match canonical terms.");
                CourtStore.UpdateAmbassadorPostings(connection, new Dictionary<string, object>{{"mission",ReadString(payload,"missionType","public_information")},{"terms",CourtCanonicalJson(terms)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
                ArrayList actions=new ArrayList{new Dictionary<string,object>{{"type","court_ambassador_mission"},{"postingId",id},{"missionType",ReadString(payload,"missionType","public_information")},{"termsHash",hash}}};Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_ambassador_mission_set",actions);StoreCourtCommand(connection,ctx,"ambassador_mission",response);AddCourtAudit(connection,ctx,"ambassador",id,"mission","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtAmbassadorRecallApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string id=ReadString(payload,"postingId","");if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(id))return CourtError("commandId and postingId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> row=ReadCourtScopedRow(connection,ctx,"ambassador_postings","posting_id",id);if(row==null)return CourtError("Ambassador posting was not found in this timeline.");long revision=ReadLong(row,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                CourtStore.UpdateAmbassadorPostings2(connection, new Dictionary<string, object>{{"day",ctx.WorldDay},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
                ArrayList actions=new ArrayList{new Dictionary<string,object>{{"type","court_recall_ambassador"},{"postingId",id},{"heroStringId",ReadString(row,"hero_id","")}}};Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_ambassador_recalled",actions);StoreCourtCommand(connection,ctx,"ambassador_recall",response);AddCourtAudit(connection,ctx,"ambassador",id,"recall","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtIntelligenceStartApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string type=ReadString(payload,"operationType","").ToLowerInvariant();string targetType=ReadString(payload,"targetType","");string targetId=ReadString(payload,"targetStringId","");
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||!CourtIntelligenceTypeAllowed(type)||string.IsNullOrWhiteSpace(targetType)||string.IsNullOrWhiteSpace(targetId))return CourtError("A valid operation type, target, and commandId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;if(!CourtStore.SelectCourtOfficeHistory5(connection, new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).Any())return CourtError("Advanced intelligence operations require an appointed Spymaster.");
                int gold=Math.Max(0,ReadInt(payload,"goldCost",0));double influence=Math.Max(0d,ReadDouble(payload,"influenceCost",0d));double duration=Math.Max(1d,Math.Min(90d,ReadDouble(payload,"durationDays",7d)));double risk=Program.ClampDouble(ReadDouble(payload,"risk",0.25d),0d,1d);double officerReliability=Program.ClampDouble(ReadDouble(payload,"officerReportReliability",0.5d),0.2d,0.95d);string id=FirstNonEmpty(ReadString(payload,"operationId",""),"intel_"+Guid.NewGuid().ToString("N"));long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                CourtStore.InsertIntelligenceOperations(connection, new Dictionary<string, object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"type",type},{"targetType",targetType},{"target",targetId},{"gold",gold},{"influence",influence},{"day",ctx.WorldDay},{"due",ctx.WorldDay+duration},{"risk",risk},{"payload",Json.Serialize(new Dictionary<string,object>(payload,StringComparer.OrdinalIgnoreCase){{"validatedOfficerReportReliability",officerReliability}})},{"ts",ts}});
                ArrayList actions=new ArrayList();if(gold>0)actions.Add(new Dictionary<string,object>{{"type","spend_player_gold"},{"amount",gold},{"reason","intelligence_operation"}});if(influence>0)actions.Add(new Dictionary<string,object>{{"type","spend_clan_influence"},{"amount",influence},{"reason","intelligence_operation"}});
                Dictionary<string,object> response=CourtMutationResult(ctx,id,1,"court_intelligence_started",actions);StoreCourtCommand(connection,ctx,"intelligence_start",response);AddCourtAudit(connection,ctx,"intelligence",id,"start","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtIntelligenceCancelApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string id=ReadString(payload,"operationId","");if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(id))return CourtError("commandId and operationId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> row=ReadCourtScopedRow(connection,ctx,"intelligence_operations","operation_id",id);if(row==null)return CourtError("Operation was not found in this timeline.");long revision=ReadLong(row,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);if(ReadString(row,"status","")!="active")return CourtError("Only an active operation may be cancelled.");
                CourtStore.UpdateIntelligenceOperations(connection, new Dictionary<string, object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_intelligence_cancelled",new ArrayList());StoreCourtCommand(connection,ctx,"intelligence_cancel",response);AddCourtAudit(connection,ctx,"intelligence",id,"cancel","completed",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtObligationsQueryApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                List<Dictionary<string,object>> rows=CourtStore.SelectObligations3(connection, new Dictionary<string,object>{{"timeline",ctx.TimelineId}});
                rows=VisibleCourtRows(connection,ctx,rows);return new Dictionary<string,object>{{"ok",true},{"obligations",rows}};
            }
        }

        internal static Dictionary<string, object> CourtObligationUpsertApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string id=FirstNonEmpty(ReadString(payload,"obligationId",""),"obligation_"+Guid.NewGuid().ToString("N"));string owedBy=ReadString(payload,"owedBy","");string owedTo=ReadString(payload,"owedTo","");Dictionary<string,object> terms=ReadDictionary(payload,"terms")??new Dictionary<string,object>();string hash=CourtHash(CourtCanonicalJson(terms));
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(owedBy)||string.IsNullOrWhiteSpace(owedTo))return CourtError("commandId, owedBy, and owedTo are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> existing=CourtStore.SelectObligations4(connection, new Dictionary<string,object>{{"id",id},{"timeline",ctx.TimelineId}}).FirstOrDefault();long revision=existing==null?0:ReadLong(existing,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();CourtStore.UpsertObligations(connection, new Dictionary<string,object>
                {
                    {"id",id},{"event",ReadString(payload,"eventId","")},{"by",owedBy},{"to",owedTo},{"description",ReadString(payload,"description","")},{"status",ReadString(payload,"status","unresolved")},
                    {"created",existing==null?ctx.WorldDay:ReadDouble(existing,"created_day",ctx.WorldDay)},{"importance",Program.ClampDouble(ReadDouble(payload,"importance",0.5d),0d,1d)},{"participants",Json.Serialize(ReadStringList(payload,"participants"))},
                    {"about",Json.Serialize(ReadStringList(payload,"aboutEntities"))},{"known",Json.Serialize(ReadStringList(payload,"knownBy"))},{"hidden",Json.Serialize(ReadStringList(payload,"hiddenFrom"))},{"visibility",ReadString(payload,"visibility","private")},
                    {"ts",ts},{"payload",Json.Serialize(payload)},{"type",ReadString(payload,"obligationType","promise")},{"due",ReadDouble(payload,"dueDay",-1d)},{"hash",hash},{"breach",CourtCanonicalJson(ReadDictionary(payload,"breachRule")??new Dictionary<string,object>())},
                    {"resolution",existing==null?"{}":ReadString(existing,"resolution_json","{}")},{"timeline",ctx.TimelineId},{"revision",revision+1}
                });
                int promisedGold=ReadInt(terms,"goldAmount",0);
                if(promisedGold>0&&SameCourtText(owedBy,ctx.ViewerId))
                {
                    string activeSession=ReadString(CourtStore.SelectCourtSessions2(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault(),"session_id","");
                    Dictionary<string,object> fulfill=new Dictionary<string,object>{{"nativeActions",new ArrayList{new Dictionary<string,object>{{"type","transfer_gold"},{"fromHeroStringId",owedBy},{"toHeroStringId",owedTo},{"amount",promisedGold}}}}};
                    UpsertCourtMatterCandidate(connection,ctx,activeSession,new Dictionary<string,object>
                    {
                        {"sourceKey","obligation:"+id},{"kind","obligation"},{"title","A promise awaits fulfillment"},{"summary",ReadString(payload,"description","A promised payment is due.")},
                        {"confidentiality",ReadString(payload,"visibility","private")},{"participants",new ArrayList{owedBy,owedTo}},{"knownBy",ReadStringList(payload,"knownBy")},
                        {"createdDay",ctx.WorldDay},{"dueDay",ReadDouble(payload,"dueDay",-1d)},{"priority",70},{"requiresServer",true},
                        {"options",new List<object>{new Dictionary<string,object>{{"optionId","fulfill"},{"label","Fulfill promise"},{"description","Transfer the exact promised gold, then close the obligation only after a native receipt."},{"consequences",fulfill}},new Dictionary<string,object>{{"optionId","refuse"},{"label","Break promise"},{"description","Record the predefined breach without transferring gold."},{"consequences",new Dictionary<string,object>()}}}},
                        {"defaultOutcome",ReadDictionary(payload,"breachRule")??new Dictionary<string,object>{{"effect","obligation_breached"},{"obligationId",id}}}
                    });
                }
                Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_obligation_upserted",new ArrayList());response["termsHash"]=hash;StoreCourtCommand(connection,ctx,"obligation_upsert",response);AddCourtAudit(connection,ctx,"obligation",id,"upsert","completed",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtObligationResolveApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string id=ReadString(payload,"obligationId","");if(string.IsNullOrWhiteSpace(ctx.CommandId)||string.IsNullOrWhiteSpace(id))return CourtError("commandId and obligationId are required.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> row=CourtStore.SelectObligations4(connection, new Dictionary<string,object>{{"id",id},{"timeline",ctx.TimelineId}}).FirstOrDefault();if(row==null)return CourtError("Obligation was not found in this timeline.");long revision=ReadLong(row,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);if(!SameCourtText(ReadString(row,"terms_hash",""),ReadString(payload,"termsHash","")))return CourtError("The obligation terms hash changed.");
                string status=ReadString(payload,"status","resolved").ToLowerInvariant();if(!new[]{"resolved","breached","forgiven","invalidated"}.Contains(status))return CourtError("Invalid obligation resolution status.");Dictionary<string,object> resolution=ReadDictionary(payload,"resolution")??new Dictionary<string,object>();
                CourtStore.UpdateObligations(connection, new Dictionary<string,object>{{"status",status},{"resolution",CourtCanonicalJson(resolution)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
                Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_obligation_"+status,new ArrayList());StoreCourtCommand(connection,ctx,"obligation_resolve",response);AddCourtAudit(connection,ctx,"obligation",id,"resolve",status,response);return response;
            }
        }

        internal static Dictionary<string, object> CourtPlotsQueryApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                List<Dictionary<string,object>> rows=CourtStore.SelectCourtPlots5(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}});return new Dictionary<string,object>{{"ok",true},{"plots",VisibleCourtRows(connection,ctx,rows)}};
            }
        }

        internal static Dictionary<string, object> CourtPlotUpsertApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);string id=FirstNonEmpty(ReadString(payload,"plotId",""),"plot_"+Guid.NewGuid().ToString("N"));string type=ReadString(payload,"plotType","scheme").ToLowerInvariant();if(string.IsNullOrWhiteSpace(ctx.CommandId)||!new[]{"promise","favor","threat","scheme","blackmail","secret","oath"}.Contains(type))return CourtError("commandId and a supported plot type are required.");
            Dictionary<string,object> terms=ReadDictionary(payload,"terms")??new Dictionary<string,object>();string hash=CourtHash(CourtCanonicalJson(terms));using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;Dictionary<string,object> existing=ReadCourtScopedRow(connection,ctx,"court_plots","plot_id",id);long revision=existing==null?0:ReadLong(existing,"revision",0);if(revision!=ctx.ExpectedRevision)return CourtRevisionError(revision);long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                CourtStore.UpsertCourtPlots(connection, new Dictionary<string,object>
                {
                    {"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"type",type},{"director",ReadString(payload,"directorHeroStringId","")},{"target",ReadString(payload,"targetStringId","")},{"participants",Json.Serialize(ReadStringList(payload,"participants"))},
                    {"known",Json.Serialize(ReadStringList(payload,"knownBy"))},{"hidden",Json.Serialize(ReadStringList(payload,"hiddenFrom"))},{"status",ReadString(payload,"status","active")},{"created",existing==null?ctx.WorldDay:ReadDouble(existing,"created_day",ctx.WorldDay)},
                    {"due",ReadDouble(payload,"dueDay",-1d)},{"progress",Program.ClampDouble(ReadDouble(payload,"progress",0d),0d,1d)},{"hash",hash},{"secret",ReadString(payload,"secretKnowledgeId","")},{"revision",revision+1},{"payload",Json.Serialize(payload)},
                    {"createdTs",existing==null?ts:ReadLong(existing,"created_ts",ts)},{"ts",ts}
                });
                Dictionary<string,object> response=CourtMutationResult(ctx,id,revision+1,"court_plot_upserted",new ArrayList());response["termsHash"]=hash;StoreCourtCommand(connection,ctx,"plot_upsert",response);AddCourtAudit(connection,ctx,"plot",id,"upsert","completed",response);return response;
            }
        }

        private static void UpsertCourtMatterCandidate(ReignDbConnection connection,CourtApiContext ctx,string sessionId,Dictionary<string,object> candidate)
        {
            string sourceKey=ReadString(candidate,"sourceKey","");if(string.IsNullOrWhiteSpace(sourceKey))return;
            Dictionary<string,object> existing=CourtStore.SelectCourtMatters4(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"source",sourceKey}}).FirstOrDefault();if(existing!=null)return;
            List<Dictionary<string,object>> options=ReadDictionaryList(candidate,"options");foreach(Dictionary<string,object> option in options){Dictionary<string,object> consequences=ReadDictionary(option,"consequences")??new Dictionary<string,object>();option["consequences"]=consequences;option["termsHash"]=CourtHash(CourtCanonicalJson(consequences));}
            string id=FirstNonEmpty(ReadString(candidate,"matterId",""),"court_matter_"+Guid.NewGuid().ToString("N"));
            Dictionary<string,object> idOwner=CourtStore.SelectCourtMatters5(connection, new Dictionary<string,object>{{"id",id}}).FirstOrDefault();
            if(idOwner!=null)
            {
                if(SameCourtText(ReadString(idOwner,"timeline_id",""),ctx.TimelineId))return;
                string timelineHash=CourtHash(ctx.TimelineId??"main");
                id=id+"_"+timelineHash.Substring(0,Math.Min(12,timelineHash.Length));
                if(CourtStore.SelectCourtMatters6(connection, new Dictionary<string,object>{{"id",id}}).Any())return;
            }
            long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            CourtStore.InsertCourtMatters(connection, new Dictionary<string,object>
            {
                {"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"session",sessionId},{"source",sourceKey},{"kind",ReadString(candidate,"kind","petition")},{"title",ReadString(candidate,"title","")},{"summary",ReadString(candidate,"summary","")},
                {"confidentiality",ReadString(candidate,"confidentiality","public")},{"participants",Json.Serialize(ReadStringList(candidate,"participants"))},{"witnesses",Json.Serialize(ReadStringList(candidate,"witnesses"))},{"known",Json.Serialize(ReadStringList(candidate,"knownBy"))},{"hidden",Json.Serialize(ReadStringList(candidate,"hiddenFrom"))},
                {"settlement",ReadString(candidate,"settlementStringId","")},{"kingdom",ReadString(candidate,"kingdomStringId","")},{"created",ReadDouble(candidate,"createdDay",ctx.WorldDay)},{"due",ReadDouble(candidate,"dueDay",-1d)},{"priority",ReadInt(candidate,"priority",0)},
                {"critical",ReadBool(candidate,"isCritical",false)?1:0},{"major",ReadBool(candidate,"isMajorRealmEvent",false)?1:0},{"ambient",ReadBool(candidate,"isAmbientRoleplay",false)?1:0},{"options",Json.Serialize(options)},{"default",CourtCanonicalJson(ReadDictionary(candidate,"defaultOutcome")??new Dictionary<string,object>())},{"requires",ReadBool(candidate,"requiresServer",true)?1:0},{"ts",ts}
            });
        }

        private static void ExpireCourtMatters(ReignDbConnection connection,CourtApiContext ctx,int day)
        {
            foreach(Dictionary<string,object> row in CourtStore.SelectCourtMatters7(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"day",day}}))
            {
                CourtStore.UpdateCourtMatters6(connection, new Dictionary<string,object>{{"day",day},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",ReadString(row,"matter_id","")}});
                UpdateMarriageEvaluationForMatter(connection,row,"expired",day,false);
                UpdateObligationForMatter(connection,row,"breached",day,new Dictionary<string,object>{{"reason","deadline_expired"}});
            }
        }

        private static void UpdateObligationForMatter(ReignDbConnection connection,Dictionary<string,object> matter,string status,double day,Dictionary<string,object> resolution)
        {
            string source=ReadString(matter,"source_key","");if(!source.StartsWith("obligation:",StringComparison.OrdinalIgnoreCase))return;string id=source.Substring("obligation:".Length);
            CourtStore.UpdateObligations2(connection, new Dictionary<string,object>{{"status",status},{"resolution",CourtCanonicalJson(resolution??new Dictionary<string,object>())},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id},{"timeline",ReadString(matter,"timeline_id","main")}});
        }

        private static void EnsureMarriageCourtMatters(ReignDbConnection connection, CourtApiContext ctx)
        {
            RelationshipsService.EnsureRelationshipDirectorSchema(connection);
            bool royal = CourtStore.SelectCourtSessions3(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).Any();
            string statuses = royal ? "'player_court_pending','awaiting_diplomacy'" : "'player_court_pending'";
            foreach(Dictionary<string,object> row in CourtStore.SelectMarriageEvaluations3(connection, statuses,new Dictionary<string,object>{{"timeline",ctx.TimelineId},{"day",ctx.WorldDay}}))
            {
                string evaluationId=ReadString(row,"evaluation_id","");
                if(string.IsNullOrWhiteSpace(evaluationId))continue;
                string source="marriage_evaluation:"+evaluationId;
                if(CourtStore.SelectCourtMatters8(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"source",source}}).Any())continue;
                string heroA=ReadString(row,"hero_a_id",""),heroB=ReadString(row,"hero_b_id","");
                Dictionary<string,object> accept=new Dictionary<string,object>{{"nativeActions",new ArrayList{new Dictionary<string,object>{{"type","marriage_response"},{"evaluationId",evaluationId},{"heroAId",heroA},{"heroBId",heroB},{"timelineId",ctx.TimelineId},{"marriageTerms",ReadDictionary(TryParseJsonObject(ReadString(row,"payload_json","{}")),"marriageTerms")}}}}};
                Dictionary<string,object> counter=new Dictionary<string,object>{{"counterTerms",new Dictionary<string,object>{{"delayDays",14},{"requiresRenewedConsent",true}}}};
                Dictionary<string,object> candidate=new Dictionary<string,object>
                {
                    {"matterId","court_marriage_"+CourtHash(ctx.TimelineId).Substring(0,8)+"_"+evaluationId},{"sourceKey",source},{"kind","marriage_proposal"},
                    {"title",ReadString(row,"route","")=="romantic"?"A personal marriage proposal awaits":"A dynastic marriage proposal awaits"},
                    {"summary","A coded proposal concerns "+heroA+" and "+heroB+". Exact native eligibility will be checked again before marriage."},
                    {"confidentiality","private"},{"participants",new ArrayList{heroA,heroB,ctx.ViewerId}},{"knownBy",new ArrayList{ctx.ViewerId}},
                    {"createdDay",ReadDouble(row,"world_day",ctx.WorldDay)},{"dueDay",ReadDouble(row,"world_day",ctx.WorldDay)+14d},{"priority",85},{"requiresServer",true},
                    {"options",new List<object>
                        {
                            new Dictionary<string,object>{{"optionId","accept"},{"label","Accept marriage"},{"description","Apply only if Bannerlord's native marriage model still permits the named couple."},{"consequences",accept}},
                            new Dictionary<string,object>{{"optionId","counter"},{"label","Counter: delay 14 days"},{"description","Record a limited counter requiring renewed consent after a fourteen-day delay."},{"consequences",counter}},
                            new Dictionary<string,object>{{"optionId","refuse"},{"label","Reject marriage"},{"description","Reject without applying a native marriage action."},{"consequences",new Dictionary<string,object>()}}
                        }},
                    {"defaultOutcome",new Dictionary<string,object>{{"effect","proposal_expired"},{"evaluationId",evaluationId}}}
                };
                UpsertCourtMatterCandidate(connection,ctx,string.Empty,candidate);
            }
        }

        private static void UpdateMarriageEvaluationForMatter(ReignDbConnection connection,Dictionary<string,object> matter,string status,double day,bool finalize)
        {
            string source=ReadString(matter,"source_key","");if(!source.StartsWith("marriage_evaluation:",StringComparison.OrdinalIgnoreCase))return;
            string id=source.Substring("marriage_evaluation:".Length);if(string.IsNullOrWhiteSpace(id))return;
            using var marriageTransaction=connection.BeginTransaction();
            CourtStore.UpdateMarriageEvaluations(connection, new Dictionary<string,object>{{"status",status},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id},{"timeline",ReadString(matter,"timeline_id","main")}});
            if(status=="rejected_by_player")
            {
                var evaluation=CourtStore.SelectMarriageEvaluations4(connection, new Dictionary<string,object>{{"id",id}}).FirstOrDefault();
                if(evaluation!=null)RelationshipsService.ApplyMarriageLeaderEffect(connection,ReadString(matter,"campaign_id","default"),ReadString(matter,"timeline_id","main"),id,ReadString(evaluation,"leader_a_id",""),ReadString(evaluation,"leader_b_id",""),-10,day);
            }
            if(finalize)RelationshipsService.FinalizeMarriage(connection,ReadString(matter,"campaign_id","default"),id,day,false);
            marriageTransaction.Commit();
            if(finalize)RelationshipsService.StoreMarriageMemory(connection,ReadString(matter,"campaign_id","default"),id,day);
        }

        internal static Dictionary<string, object> CourtWorldActionValidateApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            Dictionary<string, object> action = ReadDictionary(payload, "worldAction") ?? new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || action.Count == 0) return CourtError("commandId and worldAction are required.");
            string canonical = CourtCanonicalJson(action);
            string supplied = ReadString(payload, "termsHash", "");
            string expected = CourtHash(canonical);
            if (!SameCourtText(supplied, expected)) return CourtError("The submitted world-action hash does not match its canonical payload.");
            string source = ReadString(action, "source", "");
            int typeValue = ReadInt(action, "typeValue", 0);
            string type = ReadFirstString(action, "type", "command");
            if (!source.StartsWith("court", StringComparison.OrdinalIgnoreCase)) return CourtError("Only a court-authored world action may use this endpoint.");
            if (typeValue <= 0 && string.IsNullOrWhiteSpace(type)) return CourtError("The world action has no recognized type.");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                ArrayList actions = new ArrayList { new Dictionary<string, object> { ["type"] = "world_action", ["action"] = action } };
                string recordId = ReadFirstString(action, "actionId", "action_id");
                Dictionary<string, object> response = CourtMutationResult(ctx, recordId, 1, "court_world_action_validated", actions);
                response["termsHash"] = expected;
                StoreCourtCommand(connection, ctx, "world_action_validate", response);
                AddCourtAudit(connection, ctx, "world_action", recordId, "validate", "validated", response);
                return response;
            }
        }

        internal static Dictionary<string, object> CourtDiplomacyProposeApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);Dictionary<string,object> action=ReadDictionary(payload,"worldAction")??new Dictionary<string,object>();Dictionary<string,object> world=ReadDictionary(payload,"worldSnapshot")??new Dictionary<string,object>();
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||action.Count==0||world.Count==0)return CourtError("commandId, worldAction, and worldSnapshot are required.");
            string expected=CourtHash(CourtCanonicalJson(action));if(!SameCourtText(expected,ReadString(payload,"termsHash","")))return CourtError("The diplomatic-package hash does not match its canonical terms.");
            string playerKingdomId=ReadFirstString(world,"playerKingdomId","playerKingdomStringId"),actorKingdomId=ReadFirstString(action,"actorKingdomStringId","actorKingdomId"),targetKingdomId=ReadFirstString(action,"targetKingdomStringId","targetKingdomId");
            Dictionary<string,object> proposer=DiplomacyService.FindDirectorKingdom(world,actorKingdomId),responder=DiplomacyService.FindDirectorKingdom(world,targetKingdomId);
            if(string.IsNullOrWhiteSpace(playerKingdomId)||!SameCourtText(actorKingdomId,playerKingdomId)||proposer==null||responder==null||ReadBool(responder,"isPlayerKingdom",false))return CourtError("Only the player's current royal court may propose to a current foreign NPC realm.");
            string actorHeroId=ReadFirstString(action,"actorHeroStringId","actorHeroId"),targetHeroId=ReadFirstString(action,"targetHeroStringId","targetHeroId");
            if(!SameCourtText(actorHeroId,ReadString(proposer,"leaderHeroId",""))||!SameCourtText(targetHeroId,ReadString(responder,"leaderHeroId","")))return CourtError("One of the named current rulers changed after this package was prepared.");
            string command=Program.CanonicalCommand(ReadFirstString(action,"command","type"));Dictionary<string,object> terms=ReadDictionary(action,"terms")??TryParseJsonObject(ReadString(action,"termsJson","{}"))??new Dictionary<string,object>();
            Dictionary<string,object> candidate=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase)
            {
                {"command",command},{"actorHeroStringId",actorHeroId},{"actorKingdomStringId",actorKingdomId},{"actorClanStringId",ReadFirstString(action,"actorClanStringId","actorClanId")},
                {"targetHeroStringId",targetHeroId},{"targetKingdomId",targetKingdomId},{"targetKingdomStringId",targetKingdomId},{"targetSettlementStringId",ReadFirstString(action,"targetSettlementStringId","targetSettlementId")},
                {"terms",terms},{"publicReason",ReadString(action,"reason","A package submitted from the player's royal court.")},{"reason",ReadString(action,"reason","A royal court proposal.")},{"source","court_player_diplomacy"}
            };
            Dictionary<string,object> normalizedPreview=Program.NormalizeActionCommand(candidate,ctx.CampaignId,out List<string> normalizationErrors,world,ReadString(candidate,"publicReason",""));
            if(normalizedPreview==null||normalizationErrors.Count>0)return CourtError("The diplomatic package is not executable: "+string.Join("; ",normalizationErrors));
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                string diplomacyIntent=DiplomacyService.DirectorIntentForCandidate(candidate,world);
                Dictionary<string,object> model=DiplomacyService.AskRulerToRespond(ctx.CampaignId,world,proposer,responder,candidate,diplomacyIntent,false);if(!ReadBool(model,"ok",false))return CourtError(ReadString(model,"error","The foreign ruler could not answer the proposal."));
                Dictionary<string,object> answer=ReadDictionary(model,"response")??new Dictionary<string,object>();string answerKind=ReadString(answer,"response","refuse").ToLowerInvariant();string publicReason=DiplomacyService.RequirePublicReason(answer,ReadString(responder,"leaderName","The foreign ruler")+" declined the package.");
                bool accepted=answerKind=="accept";
                if(accepted&&DiplomacyService.IsDirectorAllianceProposal(candidate))accepted=DiplomacyService.ResolveAllianceAcceptance(ctx.CampaignId,ctx.WorldDay,world,proposer,responder,candidate,answer,false,out Dictionary<string,object> _);
                ArrayList nativeActions=new ArrayList();string result="court_diplomacy_refused";string counterMatterId="";
                if(accepted)
                {
                    normalizedPreview["source"]="court_negotiated_diplomacy";normalizedPreview["reason"]=publicReason;normalizedPreview["requiresAcceptance"]=false;
                    nativeActions.Add(new Dictionary<string,object>{{"type","world_action"},{"action",normalizedPreview}});result="court_diplomacy_accepted";
                }
                else if(answerKind=="counter")
                {
                    Dictionary<string,object> counter=DiplomacyService.ApplyCounteroffer(candidate,answer);List<string> counterErrors=new List<string>();string counterCommand=Program.CanonicalCommand(ReadString(counter,"command",""));if(!DiplomacyService.DirectorSupportedCommands.Contains(counterCommand))counterErrors.Add("unsupported command "+counterCommand);if(!DiplomacyService.DirectorCommandAllowedForIntent(diplomacyIntent,counterCommand))counterErrors.Add("counteroffer command does not belong to the proposal intent family");DiplomacyService.ValidateDirectorTerms(counter,world,proposer,responder,counterErrors);if(counterErrors.Count>0)return CourtError("The foreign counteroffer was not executable: "+string.Join("; ",counterErrors));Dictionary<string,object> normalizedCounter=Program.NormalizeActionCommand(counter,ctx.CampaignId,out normalizationErrors,world,publicReason);
                    if(normalizedCounter==null||normalizationErrors.Count>0)return CourtError("The foreign counteroffer was not executable: "+string.Join("; ",normalizationErrors));
                    normalizedCounter["source"]="court_negotiated_diplomacy";normalizedCounter["reason"]=publicReason;normalizedCounter["requiresAcceptance"]=false;
                    Dictionary<string,object> counterConsequences=new Dictionary<string,object>{{"nativeActions",new ArrayList{new Dictionary<string,object>{{"type","world_action"},{"action",normalizedCounter}}}}};
                    string sessionId=ReadString(CourtStore.SelectCourtSessions4(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault(),"session_id","");
                    counterMatterId="court_counter_"+Guid.NewGuid().ToString("N");UpsertCourtMatterCandidate(connection,ctx,sessionId,new Dictionary<string,object>
                    {
                        {"matterId",counterMatterId},{"sourceKey","player_diplomacy_counter:"+ctx.CommandId},{"kind","diplomacy_proposal"},{"title",ReadString(responder,"name","A foreign realm")+" offers revised terms"},{"summary",publicReason},
                        {"confidentiality","realm"},{"participants",new ArrayList{actorHeroId,targetHeroId}},{"knownBy",new ArrayList{ctx.ViewerId}},{"kingdomStringId",targetKingdomId},{"createdDay",ctx.WorldDay},{"dueDay",ctx.WorldDay+14d},{"priority",85},{"requiresServer",true},
                        {"options",new List<object>{new Dictionary<string,object>{{"optionId","accept"},{"label","Accept counteroffer"},{"description","Execute the exact revised package after native validation."},{"consequences",counterConsequences}},new Dictionary<string,object>{{"optionId","refuse"},{"label","Reject counteroffer"},{"description","End negotiations without native effects."},{"consequences",new Dictionary<string,object>()}}}},
                        {"defaultOutcome",new Dictionary<string,object>{{"effect","proposal_expired"}}}
                    });result="court_diplomacy_countered";
                }
                Dictionary<string,object> response=CourtMutationResult(ctx,ctx.CommandId,1,result,nativeActions);response["accepted"]=accepted;response["responseKind"]=accepted?"accept":answerKind;response["publicReason"]=publicReason;response["counterMatterId"]=counterMatterId;response["termsHash"]=expected;
                StoreCourtCommand(connection,ctx,"diplomacy_propose",response);AddCourtAudit(connection,ctx,"diplomacy",targetKingdomId,"propose",accepted?"accepted":answerKind,response);return response;
            }
        }

        internal static Dictionary<string, object> CourtSupplyValidateApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);Dictionary<string,object> transfer=ReadDictionary(payload,"transfer")??new Dictionary<string,object>();
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||transfer.Count==0)return CourtError("commandId and transfer are required.");
            string expected=CourtHash(CourtCanonicalJson(transfer));if(!SameCourtText(expected,ReadString(payload,"termsHash","")))return CourtError("The supply-transfer hash does not match its canonical terms.");
            if(string.IsNullOrWhiteSpace(ReadString(transfer,"sourceSettlementStringId",""))||string.IsNullOrWhiteSpace(ReadString(transfer,"targetSettlementStringId",""))||string.IsNullOrWhiteSpace(ReadString(transfer,"itemStringId",""))||ReadInt(transfer,"amount",0)<=0)return CourtError("A source, target, real item, and positive amount are required.");
            if(ReadInt(transfer,"goldAmount",0)<0)return CourtError("Supply transfer gold cannot be negative.");
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                Dictionary<string,object> action=new Dictionary<string,object>(transfer,StringComparer.OrdinalIgnoreCase){{"type","transfer_item"},{"commandId",ctx.CommandId}};
                Dictionary<string,object> response=CourtMutationResult(ctx,ctx.CommandId,1,"court_supply_transfer_validated",new ArrayList{action});response["termsHash"]=expected;
                StoreCourtCommand(connection,ctx,"supply_validate",response);AddCourtAudit(connection,ctx,"supply_transfer",ctx.CommandId,"validate","validated",response);return response;
            }
        }

        internal static Dictionary<string, object> CourtNativeActionValidateApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);Dictionary<string,object> action=ReadDictionary(payload,"nativeAction")??new Dictionary<string,object>();
            if(string.IsNullOrWhiteSpace(ctx.CommandId)||action.Count==0)return CourtError("commandId and nativeAction are required.");
            if(!new[]{"transfer_gold","appoint_governor","fund_construction"}.Contains(ReadString(action,"type",""),StringComparer.OrdinalIgnoreCase))return CourtError("That native action is not available as a direct player court command.");
            string expected=CourtHash(CourtCanonicalJson(action));if(!SameCourtText(expected,ReadString(payload,"termsHash","")))return CourtError("The native-action hash does not match its canonical terms.");
            Dictionary<string,object> consequences=new Dictionary<string,object>{{"nativeActions",new ArrayList{action}}};ArrayList actions=ValidateCourtNativeActions(consequences,out string validationError);
            if(!string.IsNullOrWhiteSpace(validationError)||actions.Count!=1)return CourtError(FirstNonEmpty(validationError,"Exactly one supported native action is required."));
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                if(TryReadCourtCommand(connection,ctx,out Dictionary<string,object> prior))return prior;
                action["commandId"]=ctx.CommandId;
                Dictionary<string,object> response=CourtMutationResult(ctx,ctx.CommandId,1,"court_native_action_validated",new ArrayList{action});response["termsHash"]=expected;
                StoreCourtCommand(connection,ctx,"native_action_validate",response);AddCourtAudit(connection,ctx,"native_action",ctx.CommandId,"validate","validated",response);return response;
            }
        }

        private static void TickIntelligenceOperations(ReignDbConnection connection,CourtApiContext ctx)
        {
            foreach(Dictionary<string,object> row in CourtStore.SelectIntelligenceOperations3(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"day",ctx.WorldDay}}))
            {
                string id=ReadString(row,"operation_id","");double risk=ReadDouble(row,"risk",0d);Dictionary<string,object> operationPayload=TryParseJsonObject(ReadString(row,"payload_json","{}"))??new Dictionary<string,object>();double officerReliability=Program.ClampDouble(ReadDouble(operationPayload,"validatedOfficerReportReliability",0.5d),0.2d,0.95d);double roll=RelationshipsService.StableUnit(ctx.CampaignId+"|"+ctx.TimelineId+"|"+id+"|exposure");bool exposed=roll<risk;double confidence=Program.ClampDouble((0.45d+(1d-risk)*0.45d)*(0.65d+officerReliability*0.35d),0.05d,0.95d);string status=exposed?"exposed":"completed";
                Dictionary<string,object> result=new Dictionary<string,object>{{"reportType",ReadString(row,"operation_type","")},{"targetType",ReadString(row,"target_type","")},{"targetId",ReadString(row,"target_id","")},{"confidence",confidence},{"provenance","appointed_spymaster_operation"},{"note","This report is a source-limited claim, not canonical secret truth."}};
                CourtStore.UpdateIntelligenceOperations2(connection, new Dictionary<string,object>{{"status",status},{"confidence",confidence},{"sources",Json.Serialize(new ArrayList{"court_spymaster","operation:"+id})},{"result",Json.Serialize(result)},{"exposed",exposed?1:0},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
            }
        }

        private static ArrayList TickAmbassadorPostings(ReignDbConnection connection,CourtApiContext ctx,string sessionId)
        {
            ArrayList nativeActions=new ArrayList();
            foreach(Dictionary<string,object> row in CourtStore.SelectAmbassadorPostings5(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}))
            {
                string id=ReadString(row,"posting_id","");string status=ReadString(row,"status","");double arrival=ReadDouble(row,"arrival_day",ctx.WorldDay);double last=ReadDouble(row,"last_report_day",-1d);
                if(status=="traveling"&&arrival<=ctx.WorldDay){status="posted";CourtStore.UpdateAmbassadorPostings3(connection, new Dictionary<string,object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});}
                if(status!="posted"||(last>=0d&&ctx.WorldDay-last<7d))continue;
                string mission=ReadString(row,"mission_type","public_information"),target=ReadString(row,"target_kingdom_id","");
                Dictionary<string,object> report=new Dictionary<string,object>
                {
                    {"sourceKey","ambassador_report:"+id+":"+(int)Math.Floor(ctx.WorldDay/7d)},{"kind","diplomacy_proposal"},{"title","Ambassador report from "+target},
                    {"summary","Mission "+mission.Replace('_',' ')+" reports public diplomatic conditions. Provenance: ambassador posting "+id+"; confidence 65%. No hidden foreign plan is asserted."},
                    {"confidentiality","realm"},{"participants",new ArrayList{ReadString(row,"hero_id","") ,ctx.ViewerId}},{"knownBy",new ArrayList{ctx.ViewerId}},
                    {"kingdomStringId",target},{"createdDay",ctx.WorldDay},{"dueDay",ctx.WorldDay+14d},{"priority",45},{"options",new List<object>()},{"requiresServer",true}
                };
                UpsertCourtMatterCandidate(connection,ctx,sessionId,report);
                if(mission=="improve_relations")nativeActions.Add(new Dictionary<string,object>{{"type","change_relation"},{"fromHeroStringId",ctx.ViewerId},{"toKingdomStringId",target},{"amount",1},{"commandId","ambassador_progress_"+id+"_"+(int)Math.Floor(ctx.WorldDay/7d)}});
                CourtStore.UpdateAmbassadorPostings4(connection, new Dictionary<string,object>{{"day",ctx.WorldDay},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",id}});
            }
            return nativeActions;
        }

        private static ArrayList ValidateCourtNativeActions(Dictionary<string,object> consequences,out string error)
        {
            error="";ArrayList actions=new ArrayList();HashSet<string> allowed=new HashSet<string>(new[]{"transfer_gold","transfer_item","transfer_prisoner","appoint_governor","fund_construction","change_relation","court_assign_office","court_dismiss_office","court_assign_ambassador","court_recall_ambassador","spend_player_gold","spend_clan_influence","world_action","rebellion_response","marriage_response"},StringComparer.OrdinalIgnoreCase);
            foreach(Dictionary<string,object> action in ReadDictionaryList(consequences,"nativeActions")){string type=ReadString(action,"type","");if(!allowed.Contains(type)){error="Decision option contains an unsupported native action type.";return new ArrayList();}actions.Add(action);}return actions;
        }

        private static List<Dictionary<string,object>> VisibleCourtRows(ReignDbConnection connection,CourtApiContext ctx,IEnumerable<Dictionary<string,object>> rows){return (rows??Enumerable.Empty<Dictionary<string,object>>()).Where(x=>CourtMatterVisibilityAllows(x,ctx.ViewerId)).ToList();}
        private static bool CourtMatterVisibilityAllows(Dictionary<string,object> row,string viewer)
        {
            if(row==null)return false;List<string> hidden=CourtJsonStringList(ReadFirstString(row,"hidden_from_json","hiddenFromJson"));if(!string.IsNullOrWhiteSpace(viewer)&&hidden.Contains(viewer,StringComparer.OrdinalIgnoreCase))return false;
            string visibility=ReadFirstString(row,"confidentiality","visibility").ToLowerInvariant();if(visibility=="public"||visibility=="realm"||visibility=="rumor"||string.IsNullOrWhiteSpace(visibility))return true;
            List<string> authorized=new List<string>();authorized.AddRange(CourtJsonStringList(ReadFirstString(row,"known_by_json","knownByJson")));authorized.AddRange(CourtJsonStringList(ReadFirstString(row,"participants_json","participantsJson")));authorized.AddRange(CourtJsonStringList(ReadFirstString(row,"witnesses_json","witnessesJson")));authorized.Add(ReadFirstString(row,"owed_by","owedBy"));authorized.Add(ReadFirstString(row,"owed_to","owedTo"));authorized.Add(ReadFirstString(row,"director_id","directorId"));return !string.IsNullOrWhiteSpace(viewer)&&authorized.Contains(viewer,StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<Dictionary<string,object>> VisibleRumors(ReignDbConnection connection,CourtApiContext ctx)
        {
            return Enumerable.Empty<Dictionary<string,object>>();
        }

        private static Dictionary<string,object> ReadCourtSession(ReignDbConnection connection,CourtApiContext ctx,string id){return CourtStore.SelectCourtSessions5(connection, new Dictionary<string,object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();}
        private static Dictionary<string,object> ReadActiveCourtSession(ReignDbConnection connection,CourtApiContext ctx)
        {
            if(!string.IsNullOrWhiteSpace(ctx.SessionId))
            {
                Dictionary<string,object> exact=ReadCourtSession(connection,ctx,ctx.SessionId);
                if(exact!=null)return exact;
            }
            return CourtStore.SelectCourtSessions6(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();
        }
        private static Dictionary<string,object> RequireCapitalCourtScope(Dictionary<string,object> payload)
        {
            CourtApiContext ctx=ReadCourtContext(payload);
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                Dictionary<string,object> session=ReadActiveCourtSession(connection,ctx);
                if(session==null)return CourtError("An active Rule Mode session is required.");
                if(!string.Equals(ReadString(session,"scope","local"),"capital",StringComparison.OrdinalIgnoreCase))
                    return CourtError("This royal command is available only in capital Rule Mode.");
                string host=ReadString(session,"host_settlement_id","");
                string capital=ReadString(session,"capital_settlement_id","");
                if(string.IsNullOrWhiteSpace(capital)||!string.Equals(host,capital,StringComparison.OrdinalIgnoreCase))
                    return CourtError("The session host is no longer the active designated capital.");
                return null;
            }
        }
        internal static Dictionary<string,object> CapitalCourtApi(Dictionary<string,object> payload,Func<Dictionary<string,object>,Dictionary<string,object>> operation)
        {
            Dictionary<string,object> rejection=RequireCapitalCourtScope(payload);
            return rejection??operation(payload);
        }
        private static bool CourtMatterAllowedInActiveScope(ReignDbConnection connection,CourtApiContext ctx,Dictionary<string,object> matter)
        {
            Dictionary<string,object> session=ReadActiveCourtSession(connection,ctx);
            if(session==null||!string.Equals(ReadString(session,"scope","local"),"local",StringComparison.OrdinalIgnoreCase))return true;
            return string.Equals(ReadString(matter,"settlement_id",""),ReadString(session,"host_settlement_id",""),StringComparison.OrdinalIgnoreCase);
        }
        private static Dictionary<string,object> ReadCourtMatter(ReignDbConnection connection,CourtApiContext ctx,string id){return CourtStore.SelectCourtMatters9(connection, new Dictionary<string,object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();}
        private static Dictionary<string,object> ReadCourtScopedRow(ReignDbConnection connection,CourtApiContext ctx,string table,string idColumn,string id)
        {
            HashSet<string> tables=new HashSet<string>(new[]{"ambassador_postings","intelligence_operations","court_plots"},StringComparer.Ordinal);HashSet<string> columns=new HashSet<string>(new[]{"posting_id","operation_id","plot_id"},StringComparer.Ordinal);if(!tables.Contains(table)||!columns.Contains(idColumn))throw new InvalidOperationException("Unsafe court table lookup.");return CourtStore.SelectDynamic(connection, table, idColumn,new Dictionary<string,object>{{"id",id},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();
        }

        private static bool TryReadCourtCommand(ReignDbConnection connection,CourtApiContext ctx,out Dictionary<string,object> response)
        {
            response=null;if(string.IsNullOrWhiteSpace(ctx.CommandId))return false;Dictionary<string,object> row=CourtStore.SelectCourtCommands(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"command",ctx.CommandId}}).FirstOrDefault();if(row==null)return false;response=TryParseJsonObject(ReadString(row,"response_json","{}"));if(response==null)response=new Dictionary<string,object>();response["idempotentReplay"]=true;return true;
        }
        private static void StoreCourtCommand(ReignDbConnection connection,CourtApiContext ctx,string type,Dictionary<string,object> response){CourtStore.UpsertCourtCommands(connection, new Dictionary<string,object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"command",ctx.CommandId},{"type",type},{"response",Json.Serialize(response)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});}
        private static void AddCourtAudit(ReignDbConnection connection,CourtApiContext ctx,string recordType,string recordId,string action,string status,Dictionary<string,object> receipt){CourtStore.InsertCourtAudit(connection, new Dictionary<string,object>{{"id","court_audit_"+Guid.NewGuid().ToString("N")},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"command",ctx.CommandId??""},{"type",recordType},{"record",recordId??""},{"action",action},{"status",status},{"day",ctx.WorldDay},{"receipt",Json.Serialize(receipt)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});}
        private static Dictionary<string,object> CourtMutationResult(CourtApiContext ctx,string recordId,long revision,string result,ArrayList actions){return new Dictionary<string,object>{{"ok",true},{"campaignId",ctx.CampaignId},{"timelineId",ctx.TimelineId},{"commandId",ctx.CommandId},{"recordId",recordId},{"result",result},{"revision",revision},{"receipt",new Dictionary<string,object>{{"receiptId","court_receipt_"+Guid.NewGuid().ToString("N")},{"commandId",ctx.CommandId},{"result",result}}},{"nativeActions",actions??new ArrayList()}};}
        private static Dictionary<string,object> CourtError(string error){return new Dictionary<string,object>{{"ok",false},{"error",error??"Court request failed."}};}
        private static Dictionary<string,object> CourtRevisionError(long revision){return new Dictionary<string,object>{{"ok",false},{"error","revision_conflict"},{"currentRevision",revision}};}
        private static bool CourtSessionStateAllowed(string state){return new[]{"inactive","sitting","paused_for_matter","in_conversation","resolving","interrupted","closed"}.Contains((state??"").ToLowerInvariant());}
        private static bool CourtMatterTerminal(string state){return new[]{"resolved","refused","expired","invalidated"}.Contains((state??"").ToLowerInvariant());}
        private static string CourtCanonicalOffice(string office)
        {
            string value=(office??"").Trim().ToLowerInvariant().Replace("_","").Replace("-","");
            if(value=="treasurer")return "economicadvisor";
            if(value=="chancellor")return "foreignadvisor";
            return value;
        }
        private static bool CourtOfficeAllowed(string office){return new[]{"steward","economicadvisor","foreignadvisor","marshal","spymaster"}.Contains(CourtCanonicalOffice(office));}
        private static bool CourtIntelligenceTypeAllowed(string type){return new[]{"investigate_person","investigate_claim","trace_rumor","plant_rumor","counter_rumor","recruit_asset","watch_scope","expose_plot","counterintelligence"}.Contains((type??"").ToLowerInvariant());}

        private static List<Dictionary<string,object>> CourtJsonList(string json)
        {
            try{object parsed=Json.DeserializeObject(string.IsNullOrWhiteSpace(json)?"[]":json);IEnumerable sequence=parsed as IEnumerable;if(parsed is string||sequence==null)return new List<Dictionary<string,object>>();return sequence.Cast<object>().Select(CourtObjectDictionary).Where(x=>x!=null).ToList();}catch{return new List<Dictionary<string,object>>();}
        }
        private static Dictionary<string,object> CourtObjectDictionary(object value){if(value is Dictionary<string,object> typed)return typed;if(value is IDictionary dictionary){Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);foreach(DictionaryEntry entry in dictionary)result[Convert.ToString(entry.Key,CultureInfo.InvariantCulture)]=entry.Value;return result;}return null;}
        private static List<string> CourtJsonStringList(string json){try{object parsed=Json.DeserializeObject(string.IsNullOrWhiteSpace(json)?"[]":json);if(parsed is IEnumerable values&&!(parsed is string))return values.Cast<object>().Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture)).Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();}catch{}return new List<string>();}
        private static string CourtCanonicalJson(object value){return Json.Serialize(CourtCanonicalValue(value));}
        private static object CourtCanonicalValue(object value)
        {
            if(value is IDictionary dictionary){SortedDictionary<string,object> sorted=new SortedDictionary<string,object>(StringComparer.Ordinal);foreach(DictionaryEntry entry in dictionary)sorted[Convert.ToString(entry.Key,CultureInfo.InvariantCulture)]=CourtCanonicalValue(entry.Value);return sorted;}
            if(value is IEnumerable sequence&&!(value is string)){ArrayList list=new ArrayList();foreach(object item in sequence)list.Add(CourtCanonicalValue(item));return list;}return value;
        }
        internal static string CourtHash(string canonical)
        {
            using(SHA256 sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical??"{}"))).Replace("-","").ToLowerInvariant();
        }
        private static bool SameCourtText(string a,string b){return string.Equals(a??"",b??"",StringComparison.OrdinalIgnoreCase);}

#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunCourtSelfTests()
        {
            List<Dictionary<string,object>> results=new List<Dictionary<string,object>>();Action<string,bool,string,object> add=(id,passed,summary,data)=>results.Add(new Dictionary<string,object>{{"ok",true},{"passed",passed},{"suite","court_system"},{"caseId",id},{"name",id},{"summary",summary},{"data",data??new Dictionary<string,object>()},{"durationMs",0}});
            string campaign="__court_self_test_"+Guid.NewGuid().ToString("N"),timeline="timeline_a",session="session_test";string path=Program.CampaignDirectory(campaign);
            try
            {
                Dictionary<string,object> denied=CourtSessionOpenApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","denied"},{"expectedRevision",0},{"sessionId","clan_session"},{"authority","clan"},{"playerIsKingdomRuler",false},{"hostSettlementStringId","town_test"},{"worldDay",10d},{"playerHeroStringId","player"}});add("non_royal_session_rejected",!ReadBool(denied,"ok",true),"Clan leadership and settlement ownership cannot open Court.",denied);
                Dictionary<string,object> open=CourtSessionOpenApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","open"},{"expectedRevision",0},{"sessionId",session},{"authority","royal"},{"playerIsKingdomRuler",true},{"hostSettlementStringId","town_test"},{"capitalSettlementStringId","town_test"},{"scope","capital"},{"worldDay",10d},{"playerHeroStringId","player"}});add("session_open",ReadBool(open,"ok",false)&&ReadLong(open,"revision",0)==1&&ReadString(open,"scope","")=="capital","A current kingdom ruler opens a capital-scoped royal session with revision and receipt.",open);
                Dictionary<string,object> option=new Dictionary<string,object>{{"optionId","accept"},{"label","Accept"},{"consequences",new Dictionary<string,object>()}};
                List<object> candidates=new List<object>();
                for(int i=0;i<8;i++) candidates.Add(new Dictionary<string,object>
                {
                    {"sourceKey","source_"+i},{"kind","petition"},{"title","Matter "+i},{"createdDay",10d},{"dueDay",i<2?10d+i:-1d},
                    {"priority",100-i},{"isMajorRealmEvent",i<2},{"isAmbientRoleplay",i>5},{"options",new List<object>{option}}
                });
                candidates.Add(new Dictionary<string,object>{{"sourceKey","source_0"},{"kind","petition"},{"title","Duplicate"},{"options",new List<object>{option}}});
                Dictionary<string,object> agenda=CourtAgendaBuildApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","agenda"},{"expectedRevision",1},{"sessionId",session},{"agendaDay",10},{"worldDay",10d},{"candidateMatters",candidates},{"playerHeroStringId","player"}});List<Dictionary<string,object>> agendaRows=ReadDictionaryList(agenda,"agenda");add("agenda_limit_dedup_caps",ReadBool(agenda,"ok",false)&&agendaRows.Count<=6&&agendaRows.Count(x=>ReadBool(x,"is_major",false))<=1,"Agenda caps six matters, deduplicates sources, and caps major events.",agenda);
                Dictionary<string,object> replay=CourtAgendaBuildApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","agenda"},{"expectedRevision",1},{"sessionId",session}});add("command_idempotency",ReadBool(replay,"idempotentReplay",false),"Duplicate command returns the stored receipt without a second mutation.",replay);
                bool terminalReplayStable=false,crossTimelineStable=false;using(ReignDbConnection c=ReignDatabase.OpenCampaignConnection(campaign)){EnsureCourtSchema(c);CourtApiContext terminalContext=new CourtApiContext{CampaignId=campaign,TimelineId=timeline,SessionId=session,WorldDay=10d};Dictionary<string,object> terminalCandidate=new Dictionary<string,object>{{"matterId","court_terminal_replay"},{"sourceKey","terminal_replay"},{"kind","petition"},{"title","Terminal replay"},{"options",new List<object>{option}}};UpsertCourtMatterCandidate(c,terminalContext,session,terminalCandidate);CourtStore.UpdateCourtMatters7(c, new Dictionary<string,object>{{"id","court_terminal_replay"},{"campaign",campaign},{"timeline",timeline}});UpsertCourtMatterCandidate(c,terminalContext,session,terminalCandidate);Dictionary<string,object> terminalRow=CourtStore.CountCourtMatters(c, new Dictionary<string,object>{{"id","court_terminal_replay"},{"campaign",campaign},{"timeline",timeline}}).FirstOrDefault();terminalReplayStable=ReadInt(terminalRow,"count",0)==1&&ReadString(terminalRow,"state","")=="resolved";CourtApiContext branchContext=new CourtApiContext{CampaignId=campaign,TimelineId="timeline_b",SessionId="session_branch",WorldDay=10d};UpsertCourtMatterCandidate(c,branchContext,"session_branch",terminalCandidate);UpsertCourtMatterCandidate(c,branchContext,"session_branch",terminalCandidate);Dictionary<string,object> branchRows=CourtStore.CountCourtMatters2(c, new Dictionary<string,object>{{"campaign",campaign}}).FirstOrDefault();crossTimelineStable=ReadInt(branchRows,"count",0)==2&&ReadInt(branchRows,"ids",0)==2&&ReadInt(branchRows,"timelines",0)==2;}add("terminal_matter_replay_idempotency",terminalReplayStable,"A restored native candidate cannot duplicate or resurrect an already-terminal matter in the same campaign timeline.",new Dictionary<string,object>{{"stable",terminalReplayStable}});add("cross_timeline_matter_id_namespace",crossTimelineStable,"A stable candidate id is deterministically namespaced when the same campaign replays it on another timeline, preserving both branches without a primary-key collision.",new Dictionary<string,object>{{"stable",crossTimelineStable}});
                Dictionary<string,object> agendaMatter=agendaRows.FirstOrDefault()??new Dictionary<string,object>();string agendaMatterId=ReadString(agendaMatter,"matter_id","");long agendaMatterRevision=ReadLong(agendaMatter,"revision",0);Dictionary<string,object> staleStart=CourtMatterStartApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","start_stale"},{"expectedRevision",agendaMatterRevision+5},{"matterId",agendaMatterId},{"playerHeroStringId","player"}});Dictionary<string,object> goodStart=CourtMatterStartApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","start_good"},{"expectedRevision",agendaMatterRevision},{"matterId",agendaMatterId},{"playerHeroStringId","player"}});Dictionary<string,object> staleRespond=CourtMatterRespondApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","respond_stale"},{"expectedRevision",ReadLong(goodStart,"revision",0)+5},{"matterId",agendaMatterId},{"playerHeroStringId","player"}});add("matter_revision_guards",ReadString(staleStart,"error","")=="revision_conflict"&&ReadBool(goodStart,"ok",false)&&ReadString(staleRespond,"error","")=="revision_conflict","Start and dialogue mutations reject stale revisions before dialogue generation or state changes.",new Dictionary<string,object>{{"staleStart",staleStart},{"goodStart",goodStart},{"staleRespond",staleRespond}});
                Dictionary<string,object> obligation=CourtObligationUpsertApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","obligation"},{"expectedRevision",0},{"obligationId","ob_test"},{"owedBy","player"},{"owedTo","lord"},{"description","Keep the oath"},{"dueDay",20d},{"terms",new Dictionary<string,object>{{"gold",1000}}},{"knownBy",new List<object>{"player","lord"}},{"visibility","private"},{"worldDay",10d},{"playerHeroStringId","player"}});string termsHash=ReadString(obligation,"termsHash","");Dictionary<string,object> wrong=CourtObligationResolveApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","resolve_wrong"},{"expectedRevision",1},{"obligationId","ob_test"},{"termsHash","wrong"},{"status","resolved"},{"worldDay",11d}});Dictionary<string,object> right=CourtObligationResolveApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","resolve_right"},{"expectedRevision",1},{"obligationId","ob_test"},{"termsHash",termsHash},{"status","resolved"},{"worldDay",11d}});add("obligation_terms_hash",!ReadBool(wrong,"ok",true)&&ReadBool(right,"ok",false),"Obligations reject stale terms and resolve only with the canonical hash.",new Dictionary<string,object>{{"wrong",wrong},{"right",right}});
                CourtObligationUpsertApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","gold_promise"},{"expectedRevision",0},{"obligationId","ob_gold"},{"owedBy","player"},{"owedTo","lord"},{"description","Pay the promised gold"},{"dueDay",20d},{"terms",new Dictionary<string,object>{{"goldAmount",1000}}},{"knownBy",new List<object>{"player","lord"}},{"visibility","private"},{"worldDay",10d},{"playerHeroStringId","player"}});Dictionary<string,object> promiseHome=CourtHomeApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"playerHeroStringId","player"},{"worldDay",10d}});Dictionary<string,object> promiseMatter=ReadDictionaryList(promiseHome,"peopleMatters").FirstOrDefault(x=>ReadString(x,"source_key","")=="obligation:ob_gold")??new Dictionary<string,object>();Dictionary<string,object> fulfillOption=CourtJsonList(ReadString(promiseMatter,"options_json","[]")).FirstOrDefault(x=>ReadString(x,"optionId","")=="fulfill")??new Dictionary<string,object>();Dictionary<string,object> fulfill=CourtMatterResolveApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","fulfill_gold"},{"expectedRevision",ReadLong(promiseMatter,"revision",0)},{"matterId",ReadString(promiseMatter,"matter_id","")},{"optionId","fulfill"},{"termsHash",ReadString(fulfillOption,"termsHash","")},{"worldDay",11d},{"playerHeroStringId","player"}});Dictionary<string,object> fulfillReport=CourtMatterResolveApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","fulfill_gold_report"},{"expectedRevision",ReadLong(fulfill,"revision",0)},{"matterId",ReadString(promiseMatter,"matter_id","")},{"executionStatus","completed"},{"nativeReceipt",new Dictionary<string,object>{{"ok",true}}},{"worldDay",11d},{"playerHeroStringId","player"}});Dictionary<string,object> obligationRows=CourtObligationsQueryApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"playerHeroStringId","player"}});add("obligation_fulfillment_receipt",ReadDictionaryList(fulfill,"nativeActions").Any(x=>ReadString(x,"type","")=="transfer_gold")&&ReadBool(fulfillReport,"ok",false)&&ReadDictionaryList(obligationRows,"obligations").Any(x=>ReadString(x,"obligation_id","")=="ob_gold"&&ReadString(x,"status","")=="resolved"),"Gold promises become coded matters and resolve only after a native execution receipt.",new Dictionary<string,object>{{"validation",fulfill},{"report",fulfillReport}});
                CourtPlotUpsertApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","plot"},{"expectedRevision",0},{"plotId","plot_secret"},{"plotType","scheme"},{"directorHeroStringId","lord"},{"targetStringId","player"},{"knownBy",new List<object>{"lord"}},{"hiddenFrom",new List<object>{"player"}},{"visibility","private"},{"worldDay",10d}});Dictionary<string,object> plots=CourtPlotsQueryApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"playerHeroStringId","player"}});add("plot_knowledge_safety",ReadDictionaryList(plots,"plots").Count==0,"Undiscovered plots remain hidden from the player-facing query.",plots);
                Dictionary<string,object> office=CourtOfficeAssignApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","spymaster"},{"expectedRevision",0},{"office","spymaster"},{"heroStringId","spy"},{"worldDay",10d},{"eligibility",new Dictionary<string,object>{{"isAdult",true},{"isFree",true},{"inCourtScope",true}}}});add("office_eligibility_and_receipt",ReadBool(office,"ok",false)&&ReadDictionaryList(office,"nativeActions").Count==1,"Major office assignment validates native eligibility and emits one receipt-backed action.",office);
                Dictionary<string,object> economicLegacy=CourtOfficeAssignApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","economic_legacy"},{"expectedRevision",0},{"office","treasurer"},{"heroStringId","economic_lord"},{"worldDay",10d},{"eligibility",new Dictionary<string,object>{{"isAdult",true},{"isFree",true},{"inCourtScope",true}}}});
                Dictionary<string,object> foreignLegacy=CourtOfficeAssignApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","foreign_legacy"},{"expectedRevision",0},{"office","chancellor"},{"heroStringId","foreign_lord"},{"worldDay",10d},{"eligibility",new Dictionary<string,object>{{"isAdult",true},{"isFree",true},{"inCourtScope",true}}}});
                Dictionary<string,object> duplicateSeat=CourtOfficeAssignApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","duplicate_seat"},{"expectedRevision",1},{"office","economicadvisor"},{"heroStringId","foreign_lord"},{"worldDay",10d},{"eligibility",new Dictionary<string,object>{{"isAdult",true},{"isFree",true},{"inCourtScope",true}}}});
                add("royal_council_office_aliases_and_uniqueness",ReadBool(economicLegacy,"ok",false)&&ReadBool(foreignLegacy,"ok",false)&&!ReadBool(duplicateSeat,"ok",true)&&CourtCanonicalOffice("treasurer")=="economicadvisor"&&CourtCanonicalOffice("chancellor")=="foreignadvisor","Legacy office strings canonicalize without changing the two persisted office identities, and one hero cannot occupy two Royal Council seats.",new Dictionary<string,object>{{"economic",economicLegacy},{"foreign",foreignLegacy},{"duplicate",duplicateSeat}});
                Dictionary<string,object> boundedWar=RoyalCouncilBoundPacket("war",new Dictionary<string,object>{{"wars",new List<object>{"realm_a"}},{"treasury",999},{"operations",new List<object>{"secret"}}});
                Dictionary<string,object> boundedSpy=RoyalCouncilBoundPacket("spymaster",new Dictionary<string,object>{{"operations",new List<object>{"known"}},{"wars",new List<object>{"hidden"}},{"treasury",999}});
                add("royal_council_domain_packets",boundedWar.ContainsKey("wars")&&!boundedWar.ContainsKey("treasury")&&!boundedWar.ContainsKey("operations")&&boundedSpy.ContainsKey("operations")&&!boundedSpy.ContainsKey("wars")&&!boundedSpy.ContainsKey("treasury"),"Each Royal Council advisor packet drops every unauthorized domain before prompting or persistence.",new Dictionary<string,object>{{"war",boundedWar},{"spymaster",boundedSpy}});
                Dictionary<string,object> regent=CourtRegentAssignApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","regent"},{"expectedRevision",0},{"heroStringId","spy"},{"worldDay",10d},{"eligibility",new Dictionary<string,object>{{"isAdult",true},{"isFree",true},{"inCourtScope",true}}}});Dictionary<string,object> homeWithRegent=CourtHomeApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"playerHeroStringId","player"}});add("regent_is_separate_assignment",ReadBool(regent,"ok",false)&&ReadString(ReadDictionary(homeWithRegent,"regent"),"hero_id","")=="spy","A hero may be Regent independently of a major office; the assignment is timeline-aware and visible at court.",new Dictionary<string,object>{{"regent",regent},{"home",homeWithRegent}});
                Dictionary<string,object> intelligence=CourtIntelligenceStartApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","intel"},{"operationType","investigate_claim"},{"targetType","matter"},{"targetStringId","claim"},{"durationDays",1d},{"risk",0.25d},{"worldDay",10d}});add("intelligence_cost_duration_risk",ReadBool(intelligence,"ok",false),"Intelligence operation starts only with an appointed Spymaster and coded duration/risk.",intelligence);
                Dictionary<string,object> ambassador=CourtAmbassadorAssignApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","amb"},{"heroStringId","envoy"},{"targetKingdomStringId","foreign"},{"travelDays",2d},{"worldDay",10d},{"eligibility",new Dictionary<string,object>{{"isAdult",true},{"isFree",true},{"inCourtScope",true}}}});add("ambassador_persistent_posting",ReadBool(ambassador,"ok",false),"Ambassador posting is a persistent abstraction with travel time and no map party.",ambassador);
                Dictionary<string,object> ambassadorMission=CourtAmbassadorMissionApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","amb_mission"},{"expectedRevision",1},{"postingId",ReadString(ambassador,"recordId","")},{"missionType","improve_relations"},{"missionTerms",new Dictionary<string,object>()},{"termsHash",CourtHash(CourtCanonicalJson(new Dictionary<string,object>()))},{"worldDay",10d}});Dictionary<string,object> tick=CourtSessionTickApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","tick13"},{"expectedRevision",2},{"sessionId",session},{"state","sitting"},{"worldDay",13d},{"playerHeroStringId","player"}});Dictionary<string,object> homeAfterTick=CourtHomeApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"playerHeroStringId","player"},{"worldDay",13d}});Dictionary<string,object> operationRow=ReadDictionaryList(homeAfterTick,"operations").FirstOrDefault()??new Dictionary<string,object>();add("intelligence_uncertainty_completion",ReadBool(tick,"ok",false)&&Program.ContainsAny(ReadString(operationRow,"status",""),"completed","exposed")&&ReadDouble(operationRow,"confidence",1d)<1d,"Completed intelligence remains provenance- and confidence-limited and may be exposed.",operationRow);add("ambassador_mission_native_progress",ReadBool(ambassadorMission,"ok",false)&&ReadDictionaryList(tick,"nativeActions").Any(x=>ReadString(x,"type","")=="change_relation"),"Improve-relations postings emit a bounded idempotent native effect on the reporting cadence.",tick);
                Dictionary<string,object> transfer=new Dictionary<string,object>{{"sourceSettlementStringId","a"},{"targetSettlementStringId","b"},{"itemStringId","grain"},{"amount",25},{"goldAmount",0},{"vassalConsentGranted",true}};string transferHash=CourtHash(CourtCanonicalJson(transfer));Dictionary<string,object> badSupply=CourtSupplyValidateApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","supply_bad"},{"termsHash","bad"},{"transfer",transfer}});Dictionary<string,object> goodSupply=CourtSupplyValidateApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","supply_good"},{"termsHash",transferHash},{"transfer",transfer}});add("supply_terms_atomic_command",!ReadBool(badSupply,"ok",true)&&ReadBool(goodSupply,"ok",false)&&ReadDictionaryList(goodSupply,"nativeActions").Count==1,"Supply mutations reject stale terms and emit one atomic native transfer action.",new Dictionary<string,object>{{"bad",badSupply},{"good",goodSupply}});
                Dictionary<string,object> nativeAction=new Dictionary<string,object>{{"type","fund_construction"},{"settlementStringId","town_test"},{"goldAmount",5000}};string nativeHash=CourtHash(CourtCanonicalJson(nativeAction));Dictionary<string,object> nativeValidated=CourtNativeActionValidateApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","native_fund"},{"termsHash",nativeHash},{"nativeAction",nativeAction}});add("native_action_hash_boundary",ReadBool(nativeValidated,"ok",false)&&ReadDictionaryList(nativeValidated,"nativeActions").Count==1,"Governor, gift, and construction actions cross a canonical hash and command-id boundary before native revalidation.",nativeValidated);
                Dictionary<string,object> worldAction=new Dictionary<string,object>{{"actionId","court_plan"},{"typeValue",100},{"source","court_player_plan"},{"actorHeroStringId","commander"},{"targetSettlementStringId","town"}};string worldHash=CourtHash(CourtCanonicalJson(worldAction));Dictionary<string,object> validatedPlan=CourtWorldActionValidateApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","world_plan"},{"termsHash",worldHash},{"worldAction",worldAction}});add("world_action_validation_boundary",ReadBool(validatedPlan,"ok",false)&&ReadDictionaryList(validatedPlan,"nativeActions").Count==1,"Military and diplomatic plans cross a hashed server boundary before the existing native validator/executor.",validatedPlan);
                Dictionary<string,object> diplomacyWorld=new Dictionary<string,object>{{"playerKingdomId","player_kingdom"},{"kingdoms",new List<object>{new Dictionary<string,object>{{"kingdomId","player_kingdom"},{"leaderHeroId","player"},{"isPlayerKingdom",true}},new Dictionary<string,object>{{"kingdomId","foreign"},{"leaderHeroId","foreign_ruler"},{"isPlayerKingdom",false}}}}};Dictionary<string,object> invalidDiplomacyAction=new Dictionary<string,object>{{"command","sign_trade_agreement"},{"actorKingdomStringId","foreign"},{"actorHeroStringId","foreign_ruler"},{"targetKingdomStringId","player_kingdom"},{"targetHeroStringId","player"},{"terms",new Dictionary<string,object>()}};Dictionary<string,object> invalidDiplomacy=CourtDiplomacyProposeApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"commandId","invalid_player_actor"},{"worldAction",invalidDiplomacyAction},{"worldSnapshot",diplomacyWorld},{"termsHash",CourtHash(CourtCanonicalJson(invalidDiplomacyAction))},{"playerHeroStringId","player"}});add("diplomacy_never_answers_for_player",!ReadBool(invalidDiplomacy,"ok",true)&&ReadString(invalidDiplomacy,"error","").Contains("player's current royal court"),"The proposal endpoint rejects any attempt to make the NPC director act or answer for the player ruler.",invalidDiplomacy);
                bool rollTimelines=false;using(ReignDbConnection c=ReignDatabase.OpenCampaignConnection(campaign)){RelationshipsService.EnsureRelationshipDirectorSchema(c);long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();CourtStore.InsertMarriageEvaluations(c, new Dictionary<string,object>{{"ts",ts},{"timeline",timeline}});CourtStore.InsertMarriageLeaderRolls(c, new Dictionary<string,object>{{"ts",ts}});rollTimelines=ReadInt(CourtStore.CountMarriageLeaderRolls(c).FirstOrDefault(),"count",0)==2;}Dictionary<string,object> marriageHome=CourtHomeApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId",timeline},{"playerHeroStringId","player"},{"worldDay",13d}});add("marriage_queue_materialized",ReadDictionaryList(marriageHome,"peopleMatters").Any(x=>ReadString(x,"source_key","")=="marriage_evaluation:mar_test"),"player_court_pending marriage evaluations become private Subjects/court matters with coded choices.",marriageHome);add("marriage_roll_timeline_isolation",rollTimelines,"Marriage director cadence records are isolated by timeline as well as leader and period.",new Dictionary<string,object>{{"isolated",rollTimelines}});
                Dictionary<string,object> otherTimeline=CourtHomeApi(new Dictionary<string,object>{{"campaignId",campaign},{"timelineId","older_branch"},{"playerHeroStringId","player"},{"worldDay",10d}});add("timeline_isolation",ReadDictionaryList(otherTimeline,"dailyAgenda").Count==0,"An older timeline cannot see future court matters.",otherTimeline);
                AppendForeignAmbassadorSelfTests(results,add,campaign,timeline,session);
                foreach(Dictionary<string,object> row in DialogueService.RunDialogueIntegrityAssertions())
                    add(ReadString(row,"name","dialogue_integrity"),ReadBool(row,"passed",false),ReadString(row,"detail","Dialogue integrity assertion."),row);
                foreach(Dictionary<string,object> row in WorldSimulationService.RunDialogueActionAuthorityAssertions())
                    add(ReadString(row,"name","dialogue_action_authority"),ReadBool(row,"passed",false),ReadString(row,"detail","Dialogue action authority assertion."),row);
                CastleChatSelfTests.Append(results);
                CastleSceneImageContractSelfTests.Append(results);
                FamilyChambersSelfTests.Append(results);
                AppendCourtLifeInterpretationTests(results);
                AppendCourtLifeCreatorProfileTests(results, campaign);
                AppendCourtLifeResolutionContinuityTests(results, campaign);
                WarCouncilScrollSelfTests.Append(results);
            }
            catch(Exception ex){add("court_self_test_exception",false,"Court self-test threw: "+ex.Message,ex.ToString());}
            finally{try{string root=Path.GetFullPath(Path.Combine(Program.DataDir,"campaigns"))+Path.DirectorySeparatorChar;string target=Path.GetFullPath(path??"");if(target.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("__court_self_test_",StringComparison.OrdinalIgnoreCase)&&Directory.Exists(target))Directory.Delete(target,true);}catch{}}
            return results;
        }
#endif
    }
}
