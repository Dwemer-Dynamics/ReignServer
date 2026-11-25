using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Court module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class CourtStore
    {
        internal static List<Dictionary<string, object>> SelectCourtPlots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id='main' AND director_id=$subject AND target_id=$target AND status='active' ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT conception_id,status,conception_day FROM conceptions WHERE mother_id=$mother AND biological_father_id=$father AND conception_day>=$adopted AND status NOT IN ('failed','cancelled') ORDER BY conception_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateCourtPlots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_plots SET payload_json=$payload,progress=0.75,updated_ts=$ts WHERE plot_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlots2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id='main' AND director_id=$subject AND target_id=$target AND status='active' ORDER BY updated_ts DESC;", parameters, transaction);
        }

        internal static void InsertCourtPlots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_plots(
plot_id,campaign_id,timeline_id,plot_type,director_id,target_id,participants_json,known_by_json,hidden_from_json,status,
created_day,due_day,progress,terms_hash,secret_knowledge_id,revision,payload_json,created_ts,updated_ts)
VALUES($id,$campaign,'main','scheme',$subject,$target,$participants,$known,$hidden,'active',$day,-1,0,'','',1,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateCourtPlots2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_plots SET payload_json=$payload,revision=revision+1,updated_ts=$ts WHERE plot_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtLifeTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT player_text,reply_text FROM court_life_turns WHERE turn_id=$id AND timeline_id=$timeline AND matter_id=$matter AND actor_id=$actor;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtLifeTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO court_life_turns(turn_id,timeline_id,matter_id,actor_id,phase,player_text,reply_text,created_ts) VALUES($id,$timeline,$matter,$actor,$phase,$player,$reply,$ts);", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtLifeTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO court_life_turns(turn_id,timeline_id,matter_id,actor_id,phase,player_text,reply_text,created_ts) VALUES($id,$timeline,$matter,$actor,'resolution','',$facts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtLifeTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT reply_text FROM court_life_turns WHERE timeline_id=$timeline AND actor_id=$actor AND phase='resolution' ORDER BY created_ts DESC,turn_id DESC LIMIT 6;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtLifeTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO court_life_turns(turn_id,timeline_id,matter_id,actor_id,phase,player_text,reply_text,created_ts) VALUES($id,$timeline,$matter,'case_scene','scene','',$scene,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtLifeTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT reply_text FROM court_life_turns WHERE turn_id=$id AND timeline_id=$timeline AND matter_id=$matter AND actor_id='case_scene' AND phase='scene';", parameters, transaction);
        }

        internal static void UpdateCourtSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_sessions SET state='closed',interruption_reason='Legacy clan and household courts are no longer supported.',updated_ts=$ts
WHERE LOWER(authority)<>'royal' AND state NOT IN ('inactive','closed');", parameters, transaction);
        }

        internal static void UpdateCourtOfficeHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_office_history SET office='economicadvisor' WHERE LOWER(office)='treasurer';", parameters, transaction);
        }

        internal static void UpdateCourtOfficeHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_office_history SET office='foreignadvisor' WHERE LOWER(office)='chancellor';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_sessions WHERE session_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCourtSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO court_sessions(session_id,campaign_id,timeline_id,authority,host_settlement_id,state,opened_day,closed_day,last_agenda_day,active_matter_id,time_mode,revision,interruption_reason,native_snapshot_json,created_ts,updated_ts,scope,capital_settlement_id)
VALUES($id,$campaign,$timeline,$authority,$host,'sitting',$day,-1,$agenda,'',0,$revision,'',$snapshot,$created,$ts,$scope,$capital);", parameters, transaction);
        }

        internal static void UpdateCourtSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_sessions SET state=$state,active_matter_id=$matter,time_mode=$time,interruption_reason=$reason,native_snapshot_json=$snapshot,scope=$scope,capital_settlement_id=$capital,revision=revision+1,updated_ts=$ts WHERE session_id=$id;", parameters, transaction);
        }

        internal static void UpdateCourtSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_sessions SET state='closed',closed_day=$day,interruption_reason=$reason,revision=revision+1,updated_ts=$ts WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_matters WHERE campaign_id=$campaign AND timeline_id=$timeline
AND state IN ('queued','scheduled') ORDER BY is_critical DESC,CASE WHEN due_day<0 THEN 999999 ELSE due_day END ASC,priority DESC,created_day ASC;", parameters, transaction);
        }

        internal static void DeleteCourtAgenda(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM court_agenda WHERE campaign_id=$campaign AND timeline_id=$timeline AND session_id=$session AND agenda_day=$day;", parameters, transaction);
        }

        internal static void InsertCourtAgenda(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO court_agenda(agenda_item_id,campaign_id,timeline_id,session_id,matter_id,agenda_day,slot,is_urgent,status,created_ts) VALUES($id,$campaign,$timeline,$session,$matter,$day,$slot,0,'scheduled',$ts);", parameters, transaction);
        }

        internal static void UpdateCourtMatters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state='scheduled',scheduled_day=$day,revision=revision+1,updated_ts=$ts WHERE matter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCourtSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_sessions SET last_agenda_day=$day,revision=revision+1,updated_ts=$ts WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtAgenda(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT a.*,m.title,m.summary,m.kind,m.state,m.due_day,m.revision FROM court_agenda a JOIN court_matters m ON m.matter_id=a.matter_id WHERE a.campaign_id=$campaign AND a.timeline_id=$timeline AND a.session_id=$session AND a.agenda_day=$day ORDER BY a.is_urgent DESC,a.slot;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_matters WHERE campaign_id=$campaign AND timeline_id=$timeline AND state NOT IN ('resolved','refused','expired','invalidated') ORDER BY is_critical DESC,due_day ASC,priority DESC LIMIT 80;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline AND status NOT IN ('recalled','replaced') ORDER BY assigned_day DESC LIMIT 3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIntelligenceOperations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM intelligence_operations WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY started_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtOfficeHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_office_history WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' ORDER BY office;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRegentHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_regent_history WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE timeline_id=$timeline AND status NOT IN ('resolved','breached','cancelled') ORDER BY CASE WHEN due_day<0 THEN 999999 ELSE due_day END LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlots3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' ORDER BY due_day,created_day DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_matters WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY created_day DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtOfficeHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_office_history WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY assigned_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE timeline_id=$timeline ORDER BY due_day ASC,created_day DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE timeline_id=$timeline AND status='player_court_pending' ORDER BY world_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlots4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY created_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE timeline_id=$timeline AND status='awaiting_diplomacy' ORDER BY world_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorPostings2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY assigned_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIntelligenceOperations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM intelligence_operations WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY started_day DESC LIMIT 200;", parameters, transaction);
        }

        internal static void UpdateCourtMatters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state='active',revision=revision+1,updated_ts=$ts WHERE matter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCourtMatters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state='awaiting_decision',revision=revision+1,updated_ts=$ts WHERE matter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCourtMatters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state=$state,resolved_day=$day,resolution_json=$receipt,revision=revision+1,updated_ts=$ts WHERE matter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCourtMatters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state=$state,selected_option_id=$option,terms_hash=$hash,resolved_day=$day,resolution_json=$receipt,revision=revision+1,updated_ts=$ts WHERE matter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtOfficeHistory3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_office_history WHERE campaign_id=$campaign AND timeline_id=$timeline AND office=$office AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtOfficeHistory4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT assignment_id FROM court_office_history WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateCourtOfficeHistory3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_office_history SET status='dismissed',dismissed_day=$day,dismissal_reason='replaced',revision=revision+1,updated_ts=$ts WHERE assignment_id=$id;", parameters, transaction);
        }

        internal static void InsertCourtOfficeHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO court_office_history(assignment_id,campaign_id,timeline_id,office,hero_id,status,assigned_day,dismissed_day,dismissal_reason,revision,payload_json,created_ts,updated_ts) VALUES($id,$campaign,$timeline,$office,$hero,'active',$day,-1,'',1,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateCourtOfficeHistory4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_office_history SET status='dismissed',dismissed_day=$day,dismissal_reason=$reason,revision=revision+1,updated_ts=$ts WHERE assignment_id=$id;", parameters, transaction);
        }

        internal static void UpdateCourtRegentHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_regent_history SET status='dismissed',dismissed_day=$day,dismissal_reason='replaced',revision=revision+1,updated_ts=$ts WHERE assignment_id=$id;", parameters, transaction);
        }

        internal static void InsertCourtRegentHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO court_regent_history(assignment_id,campaign_id,timeline_id,hero_id,status,assigned_day,dismissed_day,dismissal_reason,revision,payload_json,created_ts,updated_ts) VALUES($id,$campaign,$timeline,$hero,'active',$day,-1,'',1,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateCourtRegentHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_regent_history SET status='dismissed',dismissed_day=$day,dismissal_reason=$reason,revision=revision+1,updated_ts=$ts WHERE assignment_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorPostings3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT posting_id FROM ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline AND status NOT IN ('recalled','replaced') LIMIT 3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorPostings4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT posting_id FROM ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status NOT IN ('recalled','replaced') LIMIT 1;", parameters, transaction);
        }

        internal static void InsertAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO ambassador_postings(posting_id,campaign_id,timeline_id,hero_id,target_kingdom_id,mission_type,mission_terms_json,status,assigned_day,arrival_day,last_report_day,recalled_day,revision,payload_json,created_ts,updated_ts) VALUES($id,$campaign,$timeline,$hero,$kingdom,$mission,$terms,'traveling',$day,$arrival,-1,-1,1,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_postings SET mission_type=$mission,mission_terms_json=$terms,status='posted',revision=revision+1,updated_ts=$ts WHERE posting_id=$id;", parameters, transaction);
        }

        internal static void UpdateAmbassadorPostings2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_postings SET status='recalled',recalled_day=$day,revision=revision+1,updated_ts=$ts WHERE posting_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtOfficeHistory5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT assignment_id FROM court_office_history WHERE campaign_id=$campaign AND timeline_id=$timeline AND office='spymaster' AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static void InsertIntelligenceOperations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO intelligence_operations(operation_id,campaign_id,timeline_id,operation_type,target_type,target_id,status,gold_cost,influence_cost,started_day,due_day,risk,confidence,source_chain_json,result_json,is_exposed,revision,payload_json,created_ts,updated_ts) VALUES($id,$campaign,$timeline,$type,$targetType,$target,'active',$gold,$influence,$day,$due,$risk,0,'[]','{}',0,1,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateIntelligenceOperations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE intelligence_operations SET status='cancelled',revision=revision+1,updated_ts=$ts WHERE operation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE timeline_id=$timeline ORDER BY CASE WHEN due_day<0 THEN 999999 ELSE due_day END,created_day DESC LIMIT 300;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE obligation_id=$id AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO obligations(obligation_id,event_id,owed_by,owed_to,description,status,created_day,importance,participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,ts,payload_json,obligation_type,due_day,terms_hash,breach_rule_json,resolution_json,timeline_id,revision)
VALUES($id,$event,$by,$to,$description,$status,$created,$importance,$participants,$about,$known,$hidden,$visibility,$ts,$payload,$type,$due,$hash,$breach,$resolution,$timeline,$revision);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM court_sessions WHERE campaign_id=$campaign AND timeline_id=$timeline AND state NOT IN ('inactive','closed') ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET status=$status,resolution_json=$resolution,revision=revision+1,ts=$ts WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlots5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY created_day DESC LIMIT 300;", parameters, transaction);
        }

        internal static void UpsertCourtPlots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO court_plots(plot_id,campaign_id,timeline_id,plot_type,director_id,target_id,participants_json,known_by_json,hidden_from_json,status,created_day,due_day,progress,terms_hash,secret_knowledge_id,revision,payload_json,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$type,$director,$target,$participants,$known,$hidden,$status,$created,$due,$progress,$hash,$secret,$revision,$payload,$createdTs,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_matters WHERE campaign_id=$campaign AND timeline_id=$timeline AND source_key=$source AND state NOT IN ('resolved','refused','expired','invalidated') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT matter_id,timeline_id FROM court_matters WHERE matter_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT matter_id FROM court_matters WHERE matter_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertCourtMatters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_matters(matter_id,campaign_id,timeline_id,session_id,source_key,kind,state,title,summary,confidentiality,participants_json,witnesses_json,known_by_json,hidden_from_json,settlement_id,kingdom_id,created_day,due_day,scheduled_day,resolved_day,priority,is_critical,is_major,is_ambient,options_json,default_outcome_json,selected_option_id,terms_hash,resolution_json,invalid_reason,requires_server,revision,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$session,$source,$kind,'queued',$title,$summary,$confidentiality,$participants,$witnesses,$known,$hidden,$settlement,$kingdom,$created,$due,-1,-1,$priority,$critical,$major,$ambient,$options,$default,'','','{}','',$requires,1,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_matters WHERE campaign_id=$campaign AND timeline_id=$timeline AND state NOT IN ('resolved','refused','expired','invalidated') AND due_day>=0 AND due_day<$day;", parameters, transaction);
        }

        internal static void UpdateCourtMatters6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state='expired',selected_option_id='deadline_expired',resolved_day=$day,resolution_json=default_outcome_json,revision=revision+1,updated_ts=$ts WHERE matter_id=$id;", parameters, transaction);
        }

        internal static void UpdateObligations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET status=$status,resolution_json=$resolution,revision=revision+1,ts=$ts WHERE obligation_id=$id AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM court_sessions WHERE campaign_id=$campaign AND timeline_id=$timeline AND authority='royal' AND state NOT IN ('closed','inactive') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations3(ReignDbConnection connection, object statuses, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE timeline_id=$timeline AND world_day<=$day AND status IN ("+statuses+") ORDER BY world_day DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT matter_id FROM court_matters WHERE campaign_id=$campaign AND timeline_id=$timeline AND source_key=$source AND state NOT IN ('resolved','refused','expired','invalidated') LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status=$status,updated_ts=$ts WHERE evaluation_id=$id AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE evaluation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM court_sessions WHERE campaign_id=$campaign AND timeline_id=$timeline AND authority='royal' AND state NOT IN ('inactive','closed') ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIntelligenceOperations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM intelligence_operations WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' AND due_day<=$day;", parameters, transaction);
        }

        internal static void UpdateIntelligenceOperations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE intelligence_operations SET status=$status,confidence=$confidence,source_chain_json=$sources,result_json=$result,is_exposed=$exposed,revision=revision+1,updated_ts=$ts WHERE operation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorPostings5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline AND status NOT IN ('recalled','replaced');", parameters, transaction);
        }

        internal static void UpdateAmbassadorPostings3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_postings SET status='posted',revision=revision+1,updated_ts=$ts WHERE posting_id=$id;", parameters, transaction);
        }

        internal static void UpdateAmbassadorPostings4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_postings SET last_report_day=$day,revision=revision+1,updated_ts=$ts WHERE posting_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSessions5(ReignDbConnection connection,
            Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null, bool lockForShare = false)
        {
            string sql = "SELECT * FROM court_sessions WHERE session_id=$id AND campaign_id=$campaign AND timeline_id=$timeline AND LOWER(authority)='royal' LIMIT 1";
            return ReignDatabase.QuerySql(connection, sql + (lockForShare ? " FOR SHARE;" : ";"), parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSessions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_sessions WHERE campaign_id=$campaign AND timeline_id=$timeline AND LOWER(authority)='royal' AND state NOT IN ('closed','inactive') ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtMatters9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_matters WHERE matter_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object table, object idColumn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM "+table+" WHERE "+idColumn+"=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT response_json FROM court_commands WHERE campaign_id=$campaign AND timeline_id=$timeline AND command_id=$command LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCourtCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO court_commands(campaign_id,timeline_id,command_id,command_type,response_json,created_ts) VALUES($campaign,$timeline,$command,$type,$response,$ts);", parameters, transaction);
        }

        internal static void InsertCourtAudit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO court_audit(audit_id,campaign_id,timeline_id,command_id,record_type,record_id,action,status,world_day,receipt_json,created_ts) VALUES($id,$campaign,$timeline,$command,$type,$record,$action,$status,$day,$receipt,$ts);", parameters, transaction);
        }

        internal static void UpdateCourtMatters7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state='resolved' WHERE matter_id=$id AND campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtMatters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count,MAX(state) AS state FROM court_matters WHERE matter_id=$id AND campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtMatters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count,COUNT(DISTINCT matter_id) AS ids,COUNT(DISTINCT timeline_id) AS timelines FROM court_matters WHERE campaign_id=$campaign AND source_key='terminal_replay';", parameters, transaction);
        }

        internal static void InsertMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO marriage_evaluations(evaluation_id,route,status,hero_a_id,hero_b_id,clan_a_id,clan_b_id,leader_a_id,leader_b_id,world_day,score,leader_a_approved,leader_b_approved,resentment_a,resentment_b,payload_json,created_ts,updated_ts,timeline_id) VALUES('mar_test','arranged','player_court_pending','hero_a','hero_b','clan_a','clan_b','leader_a','leader_b',10,80,1,1,0,0,'{}',$ts,$ts,$timeline);", parameters, transaction);
        }

        internal static void InsertMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO marriage_leader_rolls(roll_id,timeline_id,period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts) VALUES('roll_a','timeline_a',1,'leader','clan',10,.01,.5,.2,'failed','{}',$ts),('roll_b','older_branch',1,'leader','clan',10,.01,.5,.2,'failed','{}',$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM marriage_leader_rolls WHERE period_index=1 AND leader_id='leader';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChildhoodExperiences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT experience_id,summary,experienced_age,world_day FROM childhood_experiences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active_child' AND world_day<=$day
ORDER BY ts_rank_cd(to_tsvector('simple',summary),plainto_tsquery('simple',$query)) DESC,world_day DESC,experience_id LIMIT 12;", parameters, transaction);
        }

        internal static void InsertOrIgnoreChildhoodExperiences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO childhood_experiences(experience_id,campaign_id,timeline_id,hero_id,session_id,world_day,experienced_age,summary,participants_json,status,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$hero,$session,$day,$age,$summary,$participants,'active_child',$ts,$ts)
ON CONFLICT(experience_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChildhoodMaturityReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM childhood_maturity_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChildhoodExperiences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM childhood_experiences WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active_child' ORDER BY world_day;", parameters, transaction);
        }

        internal static void InsertOrIgnoreSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO summaries(summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,participants_json,known_by_json,visibility,importance,confidence,tags_json,status,source,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$hero,'middle_term',$summary,$events,$ts,$ts,1,$participants,$known,'private',0.65,1.0,$tags,'active','childhood_migration','pending',$ts,$payload,'personal_state');", parameters, transaction);
        }

        internal static void UpdateChildhoodExperiences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE childhood_experiences SET status=CASE WHEN experienced_age>=5 THEN 'adult_retained' ELSE 'adult_archived' END,updated_ts=$ts WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active_child';", parameters, transaction);
        }

        internal static void InsertChildhoodMaturityReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO childhood_maturity_receipts(receipt_id,campaign_id,timeline_id,hero_id,retained_count,archived_count,created_ts) VALUES($id,$campaign,$timeline,$hero,$retained,$archived,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND origin_kingdom_id=$origin AND status IN ('traveling','resident','sheltered')
ORDER BY assigned_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static void InsertForeignAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO foreign_ambassador_postings(posting_id,campaign_id,timeline_id,hero_id,origin_kingdom_id,origin_kingdom_name,
origin_ruler_id,origin_ruler_name,host_kingdom_id,capital_settlement_id,status,assigned_day,arrival_day,charm,ruler_trust,authority_revision,
authority_charter_json,revision,payload_json,created_ts,updated_ts)
VALUES($posting,$campaign,$timeline,$hero,$origin,$originName,$ruler,$rulerName,$host,$capital,'traveling',$assigned,$arrival,$charm,$trust,1,$charter,1,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateForeignAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_ambassador_postings SET status='ended',ended_day=$day,end_reason=$reason,revision=revision+1,updated_ts=$ts WHERE posting_id=$posting;", parameters, transaction);
        }

        internal static void UpdateAmbassadorReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE ambassador_referrals SET status='cancelled_war',outcome='refer',resolved_day=$day,revision=revision+1,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting AND status='pending';", parameters, transaction);
        }

        internal static void UpdateForeignAmbassadorPostings2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_ambassador_postings SET status=$status,capital_settlement_id=$capital,shelter_settlement_id=$shelter,
revision=revision+1,updated_ts=$ts WHERE posting_id=$posting;", parameters, transaction);
        }

        internal static void UpdateForeignAmbassadorPostings3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_ambassador_postings SET origin_ruler_id=$ruler,origin_ruler_name=$name,charm=$charm,ruler_trust=$trust,
authority_revision=$authorityRevision,authority_charter_json=$charter,revision=revision+1,updated_ts=$ts WHERE posting_id=$posting;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero
AND status IN ('traveling','resident','sheltered') ORDER BY assigned_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT posting_id FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting
AND status IN ('traveling','resident','sheltered') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT posting_id FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero
AND status IN ('traveling','resident','sheltered') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline AND status IN ('traveling','resident','sheltered') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero
AND status IN ('traveling','resident','sheltered') ORDER BY assigned_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreAmbassadorReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO ambassador_referrals(referral_id,campaign_id,timeline_id,posting_id,action_id,exact_terms_json,terms_hash,
status,outcome,created_day,due_day,created_ts,updated_ts) VALUES($id,$campaign,$timeline,$posting,$action,$terms,$hash,'pending','refer',$day,$due,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorOfficialArchive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM ambassador_official_archive WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting
ORDER BY created_ts DESC LIMIT $limit;", parameters, transaction);
        }

        internal static void InsertOrIgnoreAmbassadorOfficialArchive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO ambassador_official_archive(archive_id,campaign_id,timeline_id,posting_id,origin_kingdom_id,
ruler_id_at_turn,envoy_id,conversation_session_id,exchange_id,turn_id,speaker_id,speaker_role,text,world_day,created_ts)
VALUES($archive,$campaign,$timeline,$posting,$kingdom,$ruler,$envoy,$session,$exchange,$turn,$speaker,$role,$text,$day,$ts);", parameters, transaction);
        }

        internal static void UpsertAmbassadorDecisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO ambassador_decisions(decision_id,campaign_id,timeline_id,posting_id,conversation_session_id,exchange_id,
requested_action,authority_result,outcome,terms_hash,referral_id,referral_state,authoritative_receipt_json,created_day,payload_json,created_ts)
VALUES($id,$campaign,$timeline,$posting,$session,$exchange,$action,$authority,$outcome,$hash,$referral,$state,$receipt,$day,'{}',$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorPressureDaily(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT net_delta FROM ambassador_pressure_daily WHERE campaign_id=$campaign AND timeline_id=$timeline
AND origin_kingdom_id=$origin AND host_kingdom_id=$host AND day_index=$day LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertAmbassadorPressureDaily(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ambassador_pressure_daily(campaign_id,timeline_id,origin_kingdom_id,host_kingdom_id,day_index,net_delta,updated_ts)
VALUES($campaign,$timeline,$origin,$host,$day,$delta,$ts)
ON CONFLICT(campaign_id,timeline_id,origin_kingdom_id,host_kingdom_id,day_index) DO UPDATE SET net_delta=$delta,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertAmbassadorPressureAudit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO ambassador_pressure_audit(audit_id,campaign_id,timeline_id,posting_id,origin_kingdom_id,
host_kingdom_id,conversation_session_id,classification,requested_delta,applied_delta,before_value,after_value,pressure_channel,evidence_turn_ids_json,world_day,created_ts)
VALUES($id,$campaign,$timeline,$posting,$origin,$host,$session,$classification,$requested,$applied,$before,$after,$channel,$evidence,$day,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM ambassador_referrals WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='pending' AND due_day<=$day ORDER BY due_day,created_ts LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT referral_id,posting_id,action_id,terms_hash,status,outcome,due_day,counter_terms_json,revision
FROM ambassador_referrals WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='countered' ORDER BY updated_ts LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_referrals WHERE referral_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateAmbassadorReferrals2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE ambassador_referrals SET status=$status,outcome=$outcome,resolved_day=CASE WHEN $terminal=1 THEN $day ELSE resolved_day END,
counter_terms_json=$counter,authoritative_receipt_json=$receipt,revision=revision+1,updated_ts=$ts WHERE referral_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateAmbassadorReferrals3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_referrals SET status=$status,authoritative_receipt_json=$receipt,revision=revision+1,updated_ts=$ts WHERE referral_id=$id;", parameters, transaction);
        }

        internal static void UpdateAmbassadorDecisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_decisions SET authoritative_receipt_json=$receipt,referral_state=$status WHERE referral_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorOfficialArchive2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM ambassador_official_archive WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting ORDER BY created_ts;", parameters, transaction);
        }

        internal static void InsertOrIgnoreInternationalCourtQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO international_court_queue
(campaign_id,timeline_id,matter_id,kind,severity_rank,created_day,payload_json,status)
VALUES($campaign,$timeline,$matter,$kind,$severity,$day,$payload,'pending')
ON CONFLICT(campaign_id,timeline_id,matter_id) DO NOTHING;", parameters, transaction);
        }

        internal static bool HasExactInternationalCourtQueueMaterial(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT matter_id FROM international_court_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline AND matter_id=$matter
AND kind=$kind AND severity_rank=$severity AND created_day=$day AND payload_json=$payload
AND status IN ('pending','resolved')
FOR UPDATE;", parameters, transaction).Count == 1;
        }

        internal static List<Dictionary<string, object>> SelectInternationalCourtQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json FROM international_court_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='pending' ORDER BY created_day,matter_id LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectInternationalCourtReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT l.matter_id,r.* FROM international_court_referrals l
JOIN ambassador_referrals r ON l.referral_id=r.referral_id AND l.campaign_id=r.campaign_id AND l.timeline_id=r.timeline_id
WHERE l.campaign_id=$campaign AND l.timeline_id=$timeline ORDER BY r.due_day,r.referral_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectUnavailableInternationalCourtReferrals(
            ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT r.* FROM ambassador_referrals r
WHERE r.campaign_id=$campaign AND r.timeline_id=$timeline AND r.posting_id=$posting AND r.action_id=$action
AND r.status='model_output_unusable' AND r.outcome='format_failure'
AND EXISTS (SELECT 1 FROM international_court_referrals l
    WHERE l.campaign_id=r.campaign_id AND l.timeline_id=r.timeline_id AND l.referral_id=r.referral_id)
ORDER BY r.created_ts,r.referral_id;", parameters);
        }

        internal static void MarkPendingInternationalCourtReferralUnavailable(
            ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE ambassador_referrals
SET status='model_output_unusable', outcome='format_failure', resolved_day=$day,
counter_terms_json='{}', authoritative_receipt_json=$receipt, revision=revision+1, updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND referral_id=$id
AND status='pending' AND revision=$revision;", parameters);
        }

        internal static List<Dictionary<string, object>> SelectInternationalCourtReferrals2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT matter_id FROM international_court_referrals
WHERE campaign_id=$campaign AND timeline_id=$timeline AND referral_id=$referral;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAmbassadorPostings10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting AND status IN ('resident','sheltered');", parameters, transaction);
        }

        internal static void InsertOrIgnoreInternationalCourtReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO international_court_referrals(campaign_id,timeline_id,matter_id,referral_id)
VALUES($campaign,$timeline,$matter,$referral) ON CONFLICT(campaign_id,timeline_id,referral_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectInternationalCourtQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json FROM international_court_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline AND matter_id=$matter;", parameters, transaction);
        }

        internal static void UpdateInternationalCourtQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE international_court_queue SET status='resolved'
WHERE campaign_id=$campaign AND timeline_id=$timeline AND matter_id=$matter;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.event_id,e.event_type,e.summary,e.world_day FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.is_complete=1 AND e.phase='completed' AND e.source='bannerlord_native'
AND e.world_day<=$day AND e.event_type IN ('battle_completed','hero_prisoner_released','hero_killed','settlement_owner_changed','peace_made')
AND EXISTS(SELECT 1 FROM world_history_knowledge_rules k WHERE k.event_id=e.event_id AND k.available_day<=$day
 AND (k.audience_type='global' OR (k.audience_type='kingdom' AND k.audience_id=$kingdom)))
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id AND (n.kingdom_id=$kingdom OR (n.entity_type='kingdom' AND n.entity_id=$kingdom)))
ORDER BY e.sequence DESC LIMIT 200;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyVisits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtFamilyVisits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_family_visits(campaign_id,timeline_id,visit_id,player_id,hero_id,created_day,deadline)
VALUES($campaign,$timeline,$visit,$player,$hero,$day,$deadline) ON CONFLICT(campaign_id,timeline_id,visit_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyVisits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_visits SET status='invalidated' WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND visit_id=$visit AND status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyAttention(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyVisits2(ReignDbConnection connection, object requestedClause, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND (deadline>$day-2" + requestedClause + ");", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static void UpsertCourtFamilyAttention(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_family_attention(campaign_id,timeline_id,player_id,hero_id,directional_relation,profile_json,child_relation_owned)
VALUES($campaign,$timeline,$player,$hero,$relation,$profile,$child) ON CONFLICT(campaign_id,timeline_id,player_id,hero_id)
DO UPDATE SET profile_json=$profile;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyAttention2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyVisits3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND visit_id=$visit;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtFamilyVisitTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_family_visit_turns(campaign_id,timeline_id,visit_id,turn_id,world_day)
VALUES($campaign,$timeline,$visit,$turn,$day) ON CONFLICT(campaign_id,timeline_id,visit_id,turn_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtFamilyVisitTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS n FROM court_family_visit_turns WHERE campaign_id=$campaign
AND timeline_id=$timeline AND visit_id=$visit;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyVisits2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_visits SET player_turns=$count WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyAttention(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_attention SET dismissals=MAX(0,dismissals-1)
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyVisits3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_visits SET status='attended' WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyVisits4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_family_visits WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND status='pending' AND deadline<=$day ORDER BY deadline,visit_id;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyVisits4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_visits SET status=$status WHERE campaign_id=$campaign AND timeline_id=$timeline AND visit_id=$visit;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyAttention2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_attention SET dismissals=$dismissals,neglected=$neglected,
decay_finished=CASE WHEN neglected=0 AND $neglected=1 THEN 0 ELSE decay_finished END,
last_decay_day=CASE WHEN neglected=0 AND $neglected=1 THEN $neglectStartDay ELSE last_decay_day END
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyAttention3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND player_id=$player AND neglected=1 AND decay_finished=0 AND last_decay_day<$today;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyAttention3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_attention SET directional_relation=$relation,last_decay_day=$today,decay_finished=$finished
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT sex FROM identity_roster WHERE hero_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_roster WHERE hero_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM identity_roster WHERE is_alive=1 AND is_adult=1 AND is_player=0 AND is_lord=1
AND kingdom_id=(SELECT kingdom_id FROM identity_roster WHERE hero_id=$player) ORDER BY hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyReconciliations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM court_family_reconciliations WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND exchange_id=$exchange;", parameters, transaction);
        }

        internal static void InsertCourtFamilyReconciliations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_family_reconciliations(campaign_id,timeline_id,hero_id,exchange_id,player_quote,npc_quote,world_day)
VALUES($campaign,$timeline,$hero,$exchange,$playerQuote,$npcQuote,$day);", parameters, transaction);
        }

        internal static void UpdateCourtFamilyAttention4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_attention SET dismissals=0,neglected=0,decay_finished=1
WHERE campaign_id=$campaign AND timeline_id=$timeline AND player_id=$player AND hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyAttention4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND hero_id=$hero AND adult_migrated=0;", parameters, transaction);
        }

        internal static void UpdateCourtFamilyAttention5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_family_attention SET adult_migrated=1,directional_relation=$relation WHERE campaign_id=$campaign AND timeline_id=$timeline
AND hero_id=$hero AND player_id=$player;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRoyalCouncilTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO royal_council_turns(turn_id,campaign_id,timeline_id,session_id,speaker_role,speaker_hero_id,speaker_name,player_text,reply_text,heard_by_json,domain_packet_json,is_opening,created_ts)
VALUES($id,$campaign,$timeline,$session,$role,$hero,$name,$player,$reply,$heard,$packet,$opening,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT world_day,actor_kingdom_id,target_kingdom_id,event_type,polarity,channel,severity,status,before_value,after_value,reason
FROM political_pressure_activity WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY world_day DESC,created_ts DESC LIMIT 20;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRulerPetitionTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO ruler_petition_turns(
turn_id,campaign_id,timeline_id,transcript_id,petition_id,phase,petitioner_hero_id,petitioner_name,petition_kind,severity,outcome,player_text,reply_text,created_ts)
VALUES($turn,$campaign,$timeline,$transcript,$petition,$phase,$hero,$name,$kind,$severity,$outcome,$player,$reply,$ts);", parameters, transaction);
        }
    }
}
