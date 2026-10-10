using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCharacterLifeStateSelfTests()
        {
            if(string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Life-state contracts require isolated Verification Lab.");
            var results=new List<Dictionary<string,object>>();
            var cacheRows = new[] {
                TestDict("sourceId", "wounded", "sourceKind", "native_confirmed", "worldDay", 20d, "status", "completed_life_event", "subject", "Arytha was wounded."),
                TestDict("sourceId", "prisoners", "sourceKind", "native_confirmed", "worldDay", 10d, "status", "completed_life_event", "subject", "Muinser sold prisoners.") };
            string stableLifePrompt = RenderCharacterLifePromptRows(cacheRows, "npc", new List<string>());
            results.Add(TestDict("caseId", "character_life_stable_render_preserves_selected_records", "passed",
                stableLifePrompt == RenderCharacterLifePromptRows(cacheRows.Reverse(), "npc", new List<string>())
                && stableLifePrompt.Contains("Arytha was wounded.") && stableLifePrompt.Contains("Muinser sold prisoners.")
                && stableLifePrompt.Split(new[] { NativeLifeHistoryPromptPolicy }, StringSplitOptions.None).Length == 2,
                "summary", "Selected life records retain their content and source IDs in deterministic chronological order; shared policy occurs once."));
            void Check(string id,bool passed,object data=null)=>results.Add(TestDict("caseId","character_life_"+id,"passed",passed,"summary",id,"data",data));
            string campaign="character_life_test_"+Guid.NewGuid().ToString("N");
            var access=new KnowledgeAccessContext { NpcId="npc",PlayerId="player",TimelineId="main",WorldDay=30 };
            var context=TestDict("campaignId",campaign,"timelineId","main","worldDay",30d,"playerHeroStringId","player","playerText","Tell me about the weather.");
            const string reply="I will follow you in building an independent nation, provided we buy land peacefully and keep the caravans neutral.";
            Dictionary<string,object> Decision(int revision,string words,string disposition="accepted")=>TestDict("id","pledge","topic","allegiance","stakes","life_changing",
                "subject","Build an independent nation together","revision",revision,"worldDay",10d+revision,"disposition",disposition,
                "terms",TestDict("conditions","Buy land peacefully; keep caravans neutral.","durationDays",0d),
                "reason","I share that goal.","objections",new[]{"risk"},"reconsideration","I may leave if we conquer unwilling people.",
                "playerQuote","Would you follow me in building an independent nation?","reply",words,"replyQuote",words);
            void Receipt(ReignDbConnection connection,string turn,Dictionary<string,object> decision,string owner="npc",string commit="committed")=>ExecuteSql(connection,
                "INSERT INTO conversation_negotiation_turns(receipt_id,timeline_id,owner_id,peer_id,turn_id,payload_json) VALUES($id,'main',$owner,'player',$turn,$payload);",
                TestDict("id","receipt_"+turn+owner,"owner",owner,"turn",turn,"payload",Json.Serialize(TestDict("ok",true,"commitState",commit,"updates",new[]{decision}))));
            try
            {
                using(var connection=OpenCampaignConnection(campaign))
                {
                    EnsureConversationAgencySchema(connection);
                    SpecialistFixtureTurn(connection,campaign,"pledge_npc","npc",reply,day:11,ts:11);
                    Receipt(connection,"pledge",Decision(1,reply));
                    var rows=ReadCharacterDecisionTransitions(connection,"npc",access);
                    Check("accepted_life_decision_links_original",rows.Count==1&&ReadString(rows[0],"sourceId","")=="pledge_npc"
                        &&ReadString(rows[0],"sourceHash","").Length>0&&!ReadBool(rows[0],"executionConfirmed",true));
                    string prompt=BuildCharacterLifeStatePrompt(connection,"npc","player",context,new());
                    Check("unrelated_turn_retains_plan_conditions_and_autonomy",prompt.Contains("independent nation")&&prompt.Contains("keep caravans neutral")
                        &&prompt.Contains("conquer unwilling people")&&prompt.Contains("right to disagree"));
                    Check("prompt_read_has_no_projection_write",ReadInt(QuerySql(connection,"SELECT COUNT(*) n FROM dynamic_characteristics WHERE category='life_transition';").Single(),"n",-1)==0);
                    Check("ordinary_favor_is_not_life_change",!IsLastingCharacterDecision(TestDict("topic","favor","stakes","ordinary","disposition","accepted")));
                    Check("ordinary_intimacy_is_not_permanent_relationship",!IsLastingCharacterDecision(TestDict("topic","intimacy","stakes","ordinary","disposition","accepted")));
                    Check("explicit_life_change_not_limited_to_allegiance",IsLastingCharacterDecision(TestDict("topic","travel","stakes","life_changing","disposition","accepted")));
                    Check("refused_life_change_does_not_create_commitment",!IsLastingCharacterDecision(TestDict("topic","allegiance","stakes","life_changing","disposition","personal_refusal")));
                    var coerced=CharacterDecisionTransition(Decision(1,reply,"accepted_under_duress"),"npc","player","source","hash","main");
                    Check("coercion_is_not_willing_loyalty",FormatCharacterLifeTransition(coerced).Contains("coerced compliance"));
                    Check("group_listener_does_not_inherit_speakers_pledge",ReadCharacterDecisionTransitions(connection,"listener",access).Count==0);
                    Check("official_firewall_holds_private_decision",!BuildCharacterLifeStatePrompt(connection,"npc","player",new(context){["officialMemoryFirewall"]=true},new()).Contains("independent nation"));
                    Check("asof_excludes_future_acceptance",ReadCharacterDecisionTransitions(connection,"npc",new KnowledgeAccessContext{NpcId="npc",TimelineId="main",WorldDay=10}).Count==0);
                    Check("branch_does_not_inherit_unrestored_decision",ReadCharacterDecisionTransitions(connection,"npc",new KnowledgeAccessContext{NpcId="npc",TimelineId="branch",WorldDay=30}).Count==0);
                    Receipt(connection,"missing",Decision(1,reply));
                    SpecialistFixtureTurn(connection,campaign,"uncommitted_npc","npc",reply,day:11,ts:11);
                    Receipt(connection,"uncommitted",Decision(1,reply),commit:"pending");
                    Check("no_source_or_pending_receipt_never_promoted",ReadCharacterDecisionTransitions(connection,"npc",access).Count==1);
                    SpecialistFixtureTurn(connection,campaign,"badquote_npc","npc",reply,day:11,ts:11);
                    var badQuote = Decision(1,reply); badQuote["replyQuote"]="I will obey you unconditionally.";
                    Receipt(connection,"badquote",badQuote);
                    Check("unsupported_decision_quote_is_not_dynamic_character",ReadCharacterDecisionTransitions(connection,"npc",access).Count==1);
                    string withdrawal="I can no longer support this plan. I will not follow you.";
                    SpecialistFixtureTurn(connection,campaign,"withdraw_npc","npc",withdrawal,day:12,ts:12);
                    Receipt(connection,"withdraw",Decision(2,withdrawal,"personal_refusal"));
                    var history=ReadCharacterDecisionTransitions(connection,"npc",access);
                    var current=CurrentCharacterLifeState(history);
                    Check("later_refusal_supersedes_acceptance_retains_history",history.Count==2&&current.Count==1&&ReadString(current[0],"status","")=="reconsidered");
                    Check("withdrawal_prompt_does_not_reaffirm_old_pledge",!FormatCharacterLifeTransition(current[0]).Contains("remains part of your present commitments")
                        &&FormatCharacterLifeTransition(current[0]).Contains("supersedes the earlier acceptance"));
                    Check("earlier_asof_retains_original_commitment",ReadString(CurrentCharacterLifeState(ReadCharacterDecisionTransitions(connection,"npc",
                        new KnowledgeAccessContext{NpcId="npc",TimelineId="main",WorldDay=11,AsOfTimestamp=11})).Single(),"status","")=="active_commitment");
                    string letter=ReadString(InsertLetter(connection,"npc","Ira","player","Michael",reply,"npc_initiated","plan","","a","b",14,16,TestDict()),"letterId","");
                    var letterDecision=Decision(1,reply);letterDecision["id"]="letter_pledge";letterDecision["worldDay"]=14d;
                    Receipt(connection,letter,letterDecision);
                    Check("authored_letter_acceptance_is_part_of_character",ReadCharacterDecisionTransitions(connection,"npc",access).Any(t=>ReadString(t,"sourceId","")==letter));
                    // Confirmed personal events are retained separately from any
                    // emotional interpretation or generated relationship history.
                    EnsureWorldHistorySchema(connection);
                    ExecuteSql(connection,@"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,source,created_utc)
VALUES('marriage',$campaign,'main',1,9,'heroes_married','Ira married Maxax.','bannerlord_native','1970-01-01T00:00:09Z');
INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,role) VALUES('marriage',0,'npc','hero','spouse');
INSERT INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day) VALUES('marriage_npc','marriage','entity','npc',9);",TestDict("campaign",campaign));
                    var native=ReadCharacterNativeLifeTransitions(connection,"npc",access);
                    Check("native_marriage_is_personal_history_without_invented_love",native.Count==1&&ReadBool(native[0],"executionConfirmed",false)
                        &&ReadString(native[0],"personalMeaning","")=="not_inferred"&&!FormatCharacterLifeTransition(native[0]).Contains("in love"));
                    Check("native_same_day_later_ingest_not_visible",ReadCharacterNativeLifeTransitions(connection,"npc",
                        new KnowledgeAccessContext{NpcId="npc",TimelineId="main",WorldDay=30,AsOfTimestamp=8}).Count==0);
                    Check("native_event_does_not_become_listeners_life",ReadCharacterNativeLifeTransitions(connection,"listener",access).Count==0);
                    ExecuteSql(connection,"UPDATE world_history_entities SET role='observer' WHERE event_id='marriage';");
                    Check("witness_is_not_a_party_to_native_life_change",ReadCharacterNativeLifeTransitions(connection,"npc",access).Count==0);
                    ExecuteSql(connection,"UPDATE world_history_entities SET role='' WHERE event_id='marriage';");
                    Check("missing_native_actor_role_does_not_guess",ReadCharacterNativeLifeTransitions(connection,"npc",access).Count==0);
                    ExecuteSql(connection,"UPDATE world_history_entities SET role='spouse' WHERE event_id='marriage';");
                }
                RefreshCharacterLifeProjection(campaign,"npc",access);
                using(var reopened=OpenCampaignConnection(campaign))
                {
                    Check("source_projection_survives_connection_reload",QuerySql(reopened,"SELECT * FROM dynamic_characteristics WHERE owner_id='npc' AND category='life_transition';").Count>=3);
                    Check("historical_acceptance_marked_superseded",ReadString(QuerySql(reopened,"SELECT * FROM dynamic_characteristics WHERE source_event_id='pledge_npc';").Single(),"status","")=="superseded");
                    var dynamics=TestDict("active",new[]{TestDict("category","life_transition","text",reply,"status","active"),
                        TestDict("category","preference","text","I like quiet gardens.","status","active")});
                    Check("life_state_does_not_crowd_out_soft_traits",SelectNaturalDynamicCharacteristics(dynamics,"").Count==1);
                }
                foreach(string mode in new[]{"in_person","party_chat","social_event","tavern_house","correspondence"})
                {
                    var payload=new Dictionary<string,object>(context){["mode"]=mode};
                    Check("shared_protected_context_"+mode,BuildProtectedConversationContinuity(campaign,"npc",payload,TestDict()).Contains("CURRENT DYNAMIC CHARACTER"));
                }
            }
            catch(Exception ex){results.Add(TestDict("caseId","character_life_fixture_completed","passed",false,"summary",LimitText(ex.ToString(),2500)));}
            finally{ReignPostgreSqlStorage.DropCampaign(campaign);}
            return results;
        }
    }
}
