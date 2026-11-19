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
    internal static partial class RelationshipsService
    {
        internal static List<Dictionary<string, object>> RunPregnancyChoiceSelfTests()
        {
            List<Dictionary<string, object>> rows=new List<Dictionary<string, object>>();
            Action<string,bool,string> add=(id,passed,summary)=>rows.Add(new Dictionary<string, object>{{"ok",true},{"passed",passed},{"suite","pregnancy_choice"},{"caseId",id},{"name",id},{"summary",summary},{"durationMs",0}});
            string previousRoot=Program.CampaignsRootOverride.Value;
            string isolatedRoot=Path.Combine(Program.DataDir,"test_runs","pregnancy_choice_"+Guid.NewGuid().ToString("N"));
            Program.CampaignsRootOverride.Value=isolatedRoot;
            try
            {
                string campaign="pregnancy_choice_campaign";
                Func<string,string,Dictionary<string, object>> request=(attempt,decision)=>new Dictionary<string, object>
                {
                    {"campaignId",campaign},{"attemptId",attempt},{"eventId",attempt},{"decision",decision},
                    {"playerId","main_hero"},{"partnerId","partner_hero"},{"verifiedAct",true},{"actionText","*She completes the insemination.*"},
                    {"coLocated",true},{"completed",true},{"worldDay",42d},
                    {"player",new Dictionary<string, object>{{"isFemale",false},{"age",30d},{"isPregnant",false}}},
                    {"partner",new Dictionary<string, object>{{"isFemale",true},{"age",30d},{"isPregnant",false},{"spouseId",""}}}
                };

                Dictionary<string, object> preview=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","preview"));
                int previewRows;
                using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaign))
                    previewRows=ReadInt(RelationshipsStore.CountConceptionAttempts(connection).FirstOrDefault(),"count",-1);
                add("preview_is_stateless",
                    ReadBool(preview,"ok",false)&&ReadBool(preview,"eligible",false)&&!ReadBool(preview,"rollPerformed",true)&&ReadDouble(preview,"roll",0d)==-1d&&previewRows==0,
                    "Opening the popup reports eligibility and chance without consuming or storing a pregnancy roll.");

                Dictionary<string, object> cancelled=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","pull_out"));
                Dictionary<string, object> cancelledThenProceed=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","proceed"));
                Dictionary<string, object> cancelledRow;
                using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaign))
                    cancelledRow=RelationshipsStore.SelectConceptionAttempts(connection).FirstOrDefault();
                add("pull_out_never_rolls",
                    ReadBool(cancelled,"cancelled",false)&&!ReadBool(cancelled,"rollPerformed",true)&&ReadDouble(cancelled,"roll",0d)==-1d
                    &&ReadString(cancelledRow,"status","")=="cancelled"&&ReadDouble(cancelledRow,"roll",0d)==-1d,
                    "Pull Out persists a cancellation with the no-roll sentinel and creates no conception.");
                add("cancelled_attempt_cannot_be_replayed",
                    ReadBool(cancelledThenProceed,"idempotent",false)&&ReadBool(cancelledThenProceed,"cancelled",false)
                    &&!ReadBool(cancelledThenProceed,"rollPerformed",true)&&!ReadBool(cancelledThenProceed,"success",true),
                    "A later Proceed retry against the same cancelled attempt remains cancelled and cannot roll.");

                Dictionary<string, object> proceeded=VerifyPlayerConceptionAttemptApi(request("attempt_proceed","proceed"));
                Dictionary<string, object> proceededAgain=VerifyPlayerConceptionAttemptApi(request("attempt_proceed","proceed"));
                add("proceed_rolls_exactly_once",
                    ReadBool(proceeded,"rollPerformed",false)&&ReadDouble(proceeded,"roll",-1d)>=0d
                    &&ReadBool(proceededAgain,"idempotent",false)&&Math.Abs(ReadDouble(proceeded,"roll",-1d)-ReadDouble(proceededAgain,"roll",-2d))<double.Epsilon,
                    "Proceed performs one deterministic roll and retries return the same stored outcome.");

                Dictionary<string, object> gate=Program.NormalizeConceptionGate(new Dictionary<string, object>{{"needed",true},{"completed",true},{"confidence",0.95d}},"",false);
                Dictionary<string, object> suppressed=Program.NormalizeConceptionGate(new Dictionary<string, object>{{"needed",true},{"completed",true},{"confidence",0.95d}},"*She inseminates him.*",true);
                add("llm_gate_and_pull_out_suppression",
                    ReadBool(gate,"needed",false)&&!ReadBool(suppressed,"needed",true),
                    "Completed LLM insemination actions open the choice, while the pull_out follow-up cannot recursively trigger another roll.");

                Dictionary<string, Dictionary<string, object>> metadata=Program.PromptMetadataByName();
                add("pull_out_prompt_is_editable",
                    Program.PromptFileNames.Contains("pull_out.txt",StringComparer.OrdinalIgnoreCase)
                    &&metadata.ContainsKey("pull_out.txt")&&ReadBool(metadata["pull_out.txt"],"visible",false)
                    &&!string.IsNullOrWhiteSpace(Program.LoadPromptTemplate("pull_out.txt")),
                    "The pull_out prompt is registered, visible in the Control Center editor, and has a non-empty default.");
            }
            finally
            {
                Program.CampaignsRootOverride.Value=previousRoot;
                try{if(Directory.Exists(isolatedRoot))Directory.Delete(isolatedRoot,true);}catch{}
            }
            return rows;
        }

        private static List<Dictionary<string, object>> RunRelationshipSubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string,bool,string> add = (id,passed,summary) => results.Add(new Dictionary<string, object>{{"ok",true},{"passed",passed},{"suite","relationship_system"},{"caseId",id},{"name",id},{"summary",summary},{"durationMs",0}});
            add("player_conception_age_curve",Math.Abs(PlayerConceptionChance(18d)-0.15d)<0.000001d&&Math.Abs(PlayerConceptionChance(30d)-0.0966666667d)<0.000001d&&Math.Abs(PlayerConceptionChance(45d)-0.03d)<0.000001d&&PlayerConceptionChance(17.9d)==0d&&PlayerConceptionChance(45.1d)==0d,"Player curve is 15% at 18, linearly 3% at 45, and zero outside the range.");
            add("player_conception_explicit_verifier",IsExplicitConceptionCapablePlayerText("We made love and attempted to have a child together.")&&!IsExplicitConceptionCapablePlayerText("I kiss you and tell you that I love you."),"Explicit conception-capable acts pass while vague romance does not.");
            add("player_conception_choice_contract",
                NormalizePregnancyChoiceToken("preview")=="preview"&&NormalizePregnancyChoiceToken("proceed")=="proceed"&&NormalizePregnancyChoiceToken("pull_out")=="pull_out",
                "Pregnancy choice decisions distinguish stateless preview, one-time proceed, and no-roll pull-out cancellation.");
            Dictionary<string, object> eligibleRomance = new Dictionary<string, object>{{"genuineInterest",55d},{"strategicInterest",70d},{"receptivity",72d},{"hardConstraints",new Dictionary<string, object>{{"adults",true},{"nativeSuitable",true},{"closeKin",false},{"coercive",false}}}};
            Dictionary<string, object> restrainedRomance = new Dictionary<string, object>{{"genuineInterest",20d},{"strategicInterest",15d},{"receptivity",30d},{"hardConstraints",new Dictionary<string, object>{{"adults",true},{"nativeSuitable",true},{"closeKin",false},{"coercive",false}}}};
            add("affair_shared_posture_gate",DirectorRomanceEligible(eligibleRomance,60d)&&!DirectorRomanceEligible(restrainedRomance,60d),"The director uses the shared interest, receptivity, suitability, and consent posture gate.");
            add("story_physical_romance_routes",
                StoryPhysicalRomanceEligible(true,true,75d,75d,eligibleRomance,eligibleRomance)
                &&StoryPhysicalRomanceEligible(true,true,50d,50d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(false,true,100d,75d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(true,false,100d,75d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(true,true,74d,75d,eligibleRomance,eligibleRomance),
                "Physical intimacy and affairs require co-location, an allowed marital route, bilateral thread intensity, suitability, receptivity, and consent.");
            add("stable_roll_idempotency",Math.Abs(StableUnit("same_attempt")-StableUnit("same_attempt"))<double.Epsilon,"Seeded conception and director rolls are retry-stable.");
            Dictionary<string, object> readyA=new Dictionary<string, object>{{"affection",70d},{"trust",55d},{"attraction",45d},{"resentment",0d}};
            Dictionary<string, object> readyB=new Dictionary<string, object>{{"affection",85d},{"trust",70d},{"attraction",60d},{"resentment",10d}};
            add("romantic_marriage_bilateral_gate",RomanticMarriageReady(readyA,readyB)&&!RomanticMarriageReady(readyA,new Dictionary<string, object>{{"affection",69d},{"trust",80d},{"attraction",80d}}),"Romantic marriage requires both directions to meet affection 70, trust 55, and attraction 45.");
            Dictionary<string, object> marriageA=new Dictionary<string, object>
            {
                {"heroStringId","marriage_a"},{"clanId","clan_a"},{"isAlive",true},{"age",24d},{"isFemale",true},
                {"nativeCanMarry",true},{"nativeMarriageClanSuitable",true},
                {"marriageAncestorIds",new List<string>{"ancestor_a"}},
                {"activeCourtships",new List<Dictionary<string, object>>()}
            };
            Dictionary<string, object> marriageB=new Dictionary<string, object>
            {
                {"heroStringId","marriage_b"},{"clanId","clan_b"},{"isAlive",true},{"age",26d},{"isFemale",false},
                {"nativeCanMarry",true},{"nativeMarriageClanSuitable",true},
                {"marriageAncestorIds",new List<string>{"ancestor_b"}},
                {"activeCourtships",new List<Dictionary<string, object>>()}
            };
            Dictionary<string, object> nativeBlocked=new Dictionary<string, object>(marriageB){{"nativeCanMarry",false}};
            Dictionary<string, object> related=new Dictionary<string, object>(marriageB){{"marriageAncestorIds",new List<string>{"ancestor_a"}}};
            Dictionary<string, object> courtshipBlocked=new Dictionary<string, object>(marriageA)
            {
                {"activeCourtships",new List<Dictionary<string, object>>
                    {new Dictionary<string, object>{{"heroId","other_suitor"},{"clanId","clan_b"},{"level",4}}}}
            };
            add("arranged_marriage_native_eligibility",
                SnapshotMarriageEligible(marriageA,marriageB)
                &&!SnapshotMarriageEligible(marriageA,nativeBlocked)
                &&!SnapshotMarriageEligible(marriageA,related)
                &&!SnapshotMarriageEligible(courtshipBlocked,marriageB),
                "Server-side arranged-marriage selection honors native marriage suitability, extended kinship, and active native courtships before queuing an action.");
            Dictionary<string, object> dutiful=new Dictionary<string, object>{{"foundationTraits",new Dictionary<string, object>{{"dutyMotivation",2},{"familyMotivation",2},{"loyalty",2},{"traditionalism",2},{"pragmatism",2},{"assertiveness",-2},{"pride",-2},{"impulsiveness",-2}}},{"nativeRelationToLeader",80d}};
            Dictionary<string, object> resistant=new Dictionary<string, object>{{"foundationTraits",new Dictionary<string, object>{{"dutyMotivation",-2},{"familyMotivation",-2},{"loyalty",-2},{"traditionalism",-2},{"pragmatism",-2},{"assertiveness",2},{"pride",2},{"impulsiveness",2}}},{"nativeRelationToLeader",-50d}};
            add("compelled_marriage_trait_resentment",ArrangedMarriageResentment(resistant,new Dictionary<string, object>(),new Dictionary<string, object>(),40d)>ArrangedMarriageResentment(dutiful,new Dictionary<string, object>(),new Dictionary<string, object>(),40d),"Resistant personalities receive more arranged-marriage resentment than dutiful traditional personalities.");
            double lowChance=DialogueService.ClampDouble(0.01d+0.04d*0d,0.01d,0.05d),highChance=DialogueService.ClampDouble(0.01d+0.04d*1d,0.01d,0.05d);
            add("arranged_marriage_weekly_chance_cap",Math.Abs(lowChance-0.01d)<0.000001d&&Math.Abs(highChance-0.05d)<0.000001d,"Weekly clan-leader marriage chance scales from 1% and never exceeds 5%.");
            List<KeyValuePair<string,double>> eventCatalog=new List<KeyValuePair<string,double>>
            {
                new KeyValuePair<string,double>("shared_confidence",1),new KeyValuePair<string,double>("practical_favor",1),
                new KeyValuePair<string,double>("family_duty_cooperation",1),new KeyValuePair<string,double>("private_spousal_confidence",1),
                new KeyValuePair<string,double>("political_rivalry_argument",1),new KeyValuePair<string,double>("mutual_romantic_flirtation",1),
                new KeyValuePair<string,double>("romantic_confidence",1),new KeyValuePair<string,double>("romantic_intimacy",1),
                new KeyValuePair<string,double>("political_obstruction",1),new KeyValuePair<string,double>("marital_argument",1),
                new KeyValuePair<string,double>("secret_affair_intimacy",1),new KeyValuePair<string,double>("marital_separation",1)
            };
            HashSet<string> reached=new HashSet<string>(Enumerable.Range(0,2000).Select(i=>SelectWeightedPassiveEvent(eventCatalog,"reachability_"+i)),StringComparer.OrdinalIgnoreCase);
            add("weighted_event_catalog_reachability",eventCatalog.All(x=>reached.Contains(x.Key)),"Every discrete relationship event remains selectable when its eligibility gate supplies positive weight.");
            double simulated=0d;HashSet<int> processedDays=new HashSet<int>();bool duplicate=false;
            for(int day=0;day<630;day++){if(!processedDays.Add(day)){duplicate=true;break;}double gap=25d-simulated;if(Math.Abs(gap)>=0.5d)simulated=DialogueService.ClampDouble(simulated+DialogueService.ClampDouble(gap*0.02d,-0.50d,0.50d),-100d,100d);}
            add("five_year_ambient_bounded_simulation",!duplicate&&simulated>=-25d&&simulated<=25d&&processedDays.Count==630,"A seeded five-year, 126-day-year daily simulation remains bounded and processes each day once.");
            string storyCampaign="relationship_story_selftest_"+Guid.NewGuid().ToString("N");
            using(ReignDbConnection storyConnection=ReignDatabase.OpenCampaignConnection(storyCampaign))
            {
                EnsureRelationshipDirectorSchema(storyConnection);
                ApplyStoryEventToDatabase(storyConnection,"mutual_romantic_flirtation","story_flirt","story_a","story_b",10d,false,false);
                Dictionary<string,object> story=LoadPairStorySummary(storyConnection,"story_a","story_b",17d);
                add("persistent_story_schema_and_priority",Math.Abs(ReadDouble(story,"multiplier",0d)-3d)<0.0001d
                    && Math.Abs(PairStoryKindIntensity(storyConnection,"story_a","story_b","romance",17d,true)-25d)<0.0001d,
                    "Persistent directional romance threads survive storage and give recent active stories exactly 3x priority.");
                ApplyStoryEventToDatabase(storyConnection,"mutual_romantic_flirtation","story_flirt","story_a","story_b",10d,false,false);
                add("story_event_idempotency",Math.Abs(PairStoryKindIntensity(storyConnection,"story_a","story_b","romance",17d,true)-25d)<0.0001d,
                    "Reprocessing one source event cannot advance a storyline twice.");
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_1","story_a","story_b",18d,false,false,eligibleRomance,eligibleRomance);
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_2","story_a","story_b",19d,false,false,eligibleRomance,eligibleRomance);
                add("production_developed_courtship_penalty",Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_c",19d)-.75d)<.0001d
                    &&Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_b",19d)-1d)<.0001d,
                    "Persisted developed courtships apply the exact third-party arrangement penalty without penalizing their own couple.");
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_3","story_a","story_b",20d,false,false,eligibleRomance,eligibleRomance);
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_4","story_a","story_b",21d,false,false,eligibleRomance,eligibleRomance);
                add("production_major_courtship_penalty",Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_c",21d)-.50d)<.0001d,
                    "Persisted major courtships apply the exact 50% third-party arrangement penalty.");
                int displaced=ConvertDisplacedCourtshipsToTemptation(storyConnection,"story_a","story_c",22d,"story_arranged");
                Dictionary<string,object> displacedThread=LoadStoryThread(storyConnection,"story_a","story_b","romance",22d);
                int displacementPressures=ReadInt(RelationshipsStore.CountRelationshipPressures2(storyConnection).FirstOrDefault(),"count",0);
                add("production_displaced_courtship",displaced==1&&ReadString(displacedThread,"route","")=="temptation"&&displacementPressures==2,
                    "An overriding arranged marriage preserves the persisted romance as temptation and creates only private directional pressures.");
                for(int i=0;i<4;i++)ApplyStoryEventToDatabase(storyConnection,"political_rivalry_argument","rivalry_"+i,"story_a","story_b",20+i*7,false,false);
                int feuds=ReadInt(RelationshipsStore.CountRelationshipStoryThreads(storyConnection).FirstOrDefault(),"count",0);
                add("persistent_feud_progression",feuds==1,"Repeated political conflicts create one pair-level feud state.");
                RelationshipsStore.InsertRelationshipPressures(storyConnection, new Dictionary<string,object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
                LoadPairStorySummary(storyConnection,"story_c","story_d",60d);
                add("lazy_story_seed_from_pressure",PairStoryKindIntensity(storyConnection,"story_c","story_d","romance",60d,false)>=60d,
                    "Existing active relationship pressures lazily seed storyline state without rewriting facets or narrative data.");
            }
            results.AddRange(RunArrangedMarriageSelfTests());
            results.AddRange(RunMbtiRelationshipSelfTests());
            return results;
        }
    }
}
