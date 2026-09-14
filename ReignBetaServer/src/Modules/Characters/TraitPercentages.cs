using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int TraitPercentageModelVersion = 2;
        private const string TraitPercentageModel = "reign_trait_percentages_v2";
        private const int CourtVirtueModelVersion = 3;
        private const double CourtVirtueNeutralDistance = 15d;
        private const double CourtVirtueTailMultiplier = 3d;
        private const int CourtVirtueNegativeExtremeMaximum = 20;
        private const int CourtVirtuePositiveExtremeMinimum = 80;

        private static readonly string[] CourtVirtueKeys =
        {
            "compassion", "boldness", "honor", "loyalty", "responsibility", "courage", "judgment"
        };

        private sealed class CourtVirtueWeight
        {
            public readonly string TraitKey;
            public readonly int Weight;
            public readonly bool Inverse;
            public CourtVirtueWeight(string traitKey, int weight, bool inverse = false)
            {
                TraitKey = traitKey; Weight = weight; Inverse = inverse;
            }
        }

        private static CourtVirtueWeight V(string traitKey, int weight, bool inverse = false)
        {
            return new CourtVirtueWeight(traitKey, weight, inverse);
        }

        private static readonly Dictionary<string, CourtVirtueWeight[]> CourtVirtueWeights =
            new Dictionary<string, CourtVirtueWeight[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["compassion"] = new[] { V("compassion",3), V("empathy",3), V("mercy",3), V("tact",1), V("generosity",1), V("aggression",1,true), V("vengefulness",2,true), V("irritability",1,true) },
                ["boldness"] = new[] { V("assertiveness",4), V("confidence",3), V("sociability",2), V("flirtatiousness",1), V("shame",1,true) },
                ["honor"] = new[] { V("honesty",5), V("dutyMotivation",2), V("discipline",1), V("generosity",1), V("greed",3,true), V("envy",1,true) },
                ["loyalty"] = new[] { V("loyalty",5), V("dutyMotivation",2) },
                ["responsibility"] = new[] { V("discipline",3), V("dutyMotivation",3), V("patience",2), V("pragmatism",2), V("impulsiveness",2,true), V("irritability",1,true) },
                ["courage"] = new[] { V("courage",5), V("emotionalStability",2), V("confidence",1), V("fearfulness",4,true) },
                ["judgment"] = new[] { V("patience",3), V("emotionalStability",3), V("discipline",2), V("pragmatism",2), V("curiosity",1), V("knowledgeMotivation",2), V("tact",1), V("impulsiveness",3,true), V("irritability",2,true), V("jealousy",1,true), V("envy",1,true) }
            };

        private static Dictionary<string, object> TraitPercentageBands()
        {
            return new Dictionary<string, object>
            {
                ["-2"] = new Dictionary<string, object>{{"min",0},{"max",20}},
                ["-1"] = new Dictionary<string, object>{{"min",21},{"max",40}},
                ["0"] = new Dictionary<string, object>{{"min",41},{"max",60}},
                ["1"] = new Dictionary<string, object>{{"min",61},{"max",80}},
                ["2"] = new Dictionary<string, object>{{"min",81},{"max",100}}
            };
        }

        private static void TraitPercentageBand(int modifier, out int minimum, out int maximum)
        {
            switch (ClampTrait(modifier))
            {
                case -2: minimum=0; maximum=20; break;
                case -1: minimum=21; maximum=40; break;
                case 0: minimum=41; maximum=60; break;
                case 1: minimum=61; maximum=80; break;
                default: minimum=81; maximum=100; break;
            }
        }

        private static int StableTraitPercentage(string heroId, string traitKey, int modifier)
        {
            TraitPercentageBand(modifier, out int minimum, out int maximum);
            int width=maximum-minimum+1;
            unchecked
            {
                uint hash=2166136261;
                foreach(char c in (heroId??"unknown")+"|"+(traitKey??"")+"|"+TraitPercentageModel){hash^=c;hash*=16777619;}
                return minimum+(int)(hash%(uint)width);
            }
        }

        private static int RemapTraitPercentage(int percentage, int oldModifier, int newModifier)
        {
            TraitPercentageBand(oldModifier,out int oldMinimum,out int oldMaximum);
            TraitPercentageBand(newModifier,out int newMinimum,out int newMaximum);
            int clamped=Math.Max(oldMinimum,Math.Min(oldMaximum,percentage));
            double position=(clamped-oldMinimum)/(double)(oldMaximum-oldMinimum);
            return Math.Max(newMinimum,Math.Min(newMaximum,(int)Math.Round(newMinimum+position*(newMaximum-newMinimum),MidpointRounding.AwayFromZero)));
        }

        private static bool EnsureTraitPercentageData(Dictionary<string, object> traits, string heroId)
        {
            if(traits==null)return false;
            Dictionary<string,object> foundation=ReadDictionary(traits,"foundationTraits")??ReadDictionary(traits,"hiddenReignTraits");
            if(foundation==null||foundation.Count==0)return false;
            bool currentFoundation=FoundationTraitModelCurrent(traits);
            Dictionary<string,object> percentages=ReadDictionary(traits,"traitPercentages")??new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string,object> snapshot=ReadDictionary(traits,"traitPercentageModifierSnapshot")??new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            bool changed=false;
            Dictionary<string,object> existingPercentageModel=ReadDictionary(traits,"traitPercentageModel")??new Dictionary<string,object>();
            if(ReadInt(existingPercentageModel,"version",0)!=TraitPercentageModelVersion
                ||!ReadString(existingPercentageModel,"id","").Equals(TraitPercentageModel,StringComparison.OrdinalIgnoreCase))changed=true;
            Dictionary<string,object> existingVirtues=ReadDictionary(traits,"courtVirtues")??new Dictionary<string,object>();
            Dictionary<string,object> existingVirtueModel=ReadDictionary(traits,"courtVirtueModel")??new Dictionary<string,object>();
            if(existingVirtues.Count!=CourtVirtueKeys.Length||CourtVirtueKeys.Any(x=>!existingVirtues.ContainsKey(x))||ReadInt(existingVirtueModel,"version",0)!=CourtVirtueModelVersion)changed=true;
            foreach(string key in CoreTraitKeys)
            {
                int modifier=ClampTrait(TraitInt(foundation,key));
                TraitPercentageBand(modifier,out int minimum,out int maximum);
                bool has=percentages.ContainsKey(key);int percentage=ReadInt(percentages,key,int.MinValue);
                int prior=snapshot.ContainsKey(key)?ClampTrait(ReadInt(snapshot,key,modifier)):modifier;
                if(!has||percentage==int.MinValue){percentage=StableTraitPercentage(heroId,key,modifier);changed=true;}
                else if(prior!=modifier){percentage=RemapTraitPercentage(percentage,prior,modifier);changed=true;}
                else if(percentage<minimum||percentage>maximum){percentage=Math.Max(minimum,Math.Min(maximum,percentage));changed=true;}
                if(!has||ReadInt(percentages,key,-1)!=percentage)changed=true;
                percentages[key]=percentage;snapshot[key]=modifier;
            }
            foreach(string extra in percentages.Keys.Where(x=>!CoreTraitKeys.Contains(x,StringComparer.OrdinalIgnoreCase)).ToList()){percentages.Remove(extra);changed=true;}
            foreach(string extra in snapshot.Keys.Where(x=>!CoreTraitKeys.Contains(x,StringComparer.OrdinalIgnoreCase)).ToList()){snapshot.Remove(extra);changed=true;}
            if(currentFoundation)
            {
                if(ReadInt(traits,"version",0)!=CoreTraitDocumentVersion
                    ||!ReadString(traits,"model","").Equals("reign_core_personality_43_percent_v2",StringComparison.OrdinalIgnoreCase))changed=true;
                traits["version"]=CoreTraitDocumentVersion;
                traits["model"]="reign_core_personality_43_percent_v2";
            }
            traits["traitPercentageModel"]=new Dictionary<string,object>{{"version",TraitPercentageModelVersion},{"id",TraitPercentageModel},{"generation","stable hero identity + trait key hash within the current foundation band"},{"editorRemap","preserve relative position when an editor override changes the foundation level"},{"migration","generated profiles are recalculated from the active foundation model; editor-authored percentages retain their relative band position"}};
            traits["traitPercentageScale"]="integer 0..100";
            traits["traitPercentageBands"]=TraitPercentageBands();
            traits["traitPercentages"]=percentages;
            traits["traitPercentageModifierSnapshot"]=snapshot;
            Dictionary<string,object> calculatedVirtues=CalculateCourtVirtues(percentages);
            if(existingVirtues.Count!=calculatedVirtues.Count||CourtVirtueKeys.Any(x=>ReadInt(existingVirtues,x,int.MinValue)!=ReadInt(calculatedVirtues,x,int.MaxValue)))changed=true;
            traits["courtVirtues"]=calculatedVirtues;
            traits["courtVirtueDefinitions"]=CourtVirtueDefinitions();
            traits["courtVirtueModel"]=CourtVirtueModelDefinition();
            return changed;
        }

        private static Dictionary<string,object> CalculateCourtVirtues(Dictionary<string,object> percentages)
        {
            percentages=percentages??new Dictionary<string,object>();Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(string virtue in CourtVirtueKeys)
            {
                result[virtue]=CalibrateCourtVirtueScore(CalculateRawCourtVirtueScore(percentages,virtue));
            }
            return result;
        }

        private static double CalculateRawCourtVirtueScore(Dictionary<string,object> percentages,string virtue)
        {
            percentages=percentages??new Dictionary<string,object>();CourtVirtueWeight[] weights=CourtVirtueWeights[virtue];int total=weights.Sum(x=>x.Weight);
            int weighted=weights.Sum(x=>(x.Inverse?100-ReadInt(percentages,x.TraitKey,50):ReadInt(percentages,x.TraitKey,50))*x.Weight);
            return weighted/(double)total;
        }

        private static int CalibrateCourtVirtueScore(double raw)
        {
            raw=Math.Max(0d,Math.Min(100d,raw));double distance=raw-50d,magnitude=Math.Abs(distance),calibrated=raw;
            if(magnitude>CourtVirtueNeutralDistance)
                calibrated=50d+Math.Sign(distance)*(CourtVirtueNeutralDistance+CourtVirtueTailMultiplier*(magnitude-CourtVirtueNeutralDistance));
            return Math.Max(0,Math.Min(100,(int)Math.Round(calibrated,MidpointRounding.AwayFromZero)));
        }

        private static Dictionary<string,object> CourtVirtueDefinitions()
        {
            return new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase)
            {
                ["compassion"]=VirtueDefinition("Cruel, callous, punitive, humiliating, or indifferent to suffering and dignity.","Kind, compassionate, merciful, empathetic, and protective of vulnerable people."),
                ["boldness"]=VirtueDefinition("Passive, hesitant, submissive, or reluctant to initiate while safe.","Forceful, outspoken, socially daring, and willing to approach or act first while comfortable."),
                ["honor"]=VirtueDefinition("Dishonest, corrupt, unjust, exploitative, or willing to cheat.","Honest, principled, fair, and resistant to bribery or exploitation."),
                ["loyalty"]=VirtueDefinition("Treacherous, faithless, unreliable, or willing to abandon allegiance.","Faithful, dependable, and committed to chosen oaths and allegiances."),
                ["responsibility"]=VirtueDefinition("Lazy, neglectful, careless with authority, or unwilling to perform duties.","Dutiful, reliable, attentive, and willing to fulfill difficult obligations."),
                ["courage"]=VirtueDefinition("Cowardly, easily intimidated, or unwilling to act when a tangible threat is present.","Brave and willing to act despite a tangible threat of violence, captivity, death, or destructive retaliation."),
                ["judgment"]=VirtueDefinition("Impulsive, reckless, emotionally driven, poorly informed, or unable to weigh consequences.","Patient, disciplined, informed, practical, emotionally controlled, and willing to reconsider.")
            };
        }

        private static Dictionary<string,object> CourtVirtueModelDefinition()
        {
            Dictionary<string,object> groups=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(string key in CourtVirtueKeys)
            {
                groups[key]=CourtVirtueWeights[key].Select(x=>(object)new Dictionary<string,object>
                {
                    ["traitKey"]=x.TraitKey,["weight"]=x.Weight,["inverse"]=x.Inverse
                }).ToList();
            }
            return new Dictionary<string,object>
            {
                ["version"]=CourtVirtueModelVersion,["orientation"]="0 is the negative pole; 100 is the positive pole",
                ["calculation"]="unrounded weighted average; inverse contributors use 100 - trait percentage; calibrate the tails; clamp to 0..100; round the final result away from zero",
                ["calibration"]=new Dictionary<string,object>
                {
                    ["neutralMinimum"]=35,["neutralMaximum"]=65,["neutralRule"]="raw scores from 35 through 65 remain unchanged",
                    ["tailMultiplier"]=CourtVirtueTailMultiplier,["tailFormula"]="50 + sign(raw - 50) * (15 + 3 * (abs(raw - 50) - 15))",
                    ["clampMinimum"]=0,["clampMaximum"]=100,["rounding"]="MidpointRounding.AwayFromZero after calibration"
                },
                ["extremeBands"]=new Dictionary<string,object>
                {
                    ["negative"]=new Dictionary<string,object>{{"min",0},{"max",CourtVirtueNegativeExtremeMaximum}},
                    ["positive"]=new Dictionary<string,object>{{"min",CourtVirtuePositiveExtremeMinimum},{"max",100}},
                    ["quotaRule"]="evidence-driven; no group is forced to populate either extreme band"
                },
                ["initiativeRule"]="Use boldness when the NPC is comfortable and must initiate. Use courage when a tangible threat is already present. If the NPC must initiate before facing a tangible threat, roll boldness first and courage second.",
                ["threatDefinition"]="A tangible threat is credible violence, captivity, death, or destructive retaliation, not ordinary embarrassment, rejection, disagreement, or attention.",
                ["groups"]=groups
            };
        }

        private static Dictionary<string,object> VirtueDefinition(string low,string high){return new Dictionary<string,object>{{"lowNegative",low},{"highPositive",high}};}

        private static bool TraitPercentageDocumentReady(Dictionary<string,object> traits)
        {
            Dictionary<string,object> foundation=ReadDictionary(traits,"foundationTraits")??ReadDictionary(traits,"hiddenReignTraits")??new Dictionary<string,object>();
            Dictionary<string,object> percentages=ReadDictionary(traits,"traitPercentages")??new Dictionary<string,object>();
            Dictionary<string,object> virtues=ReadDictionary(traits,"courtVirtues")??new Dictionary<string,object>();
            Dictionary<string,object> percentageModel=ReadDictionary(traits,"traitPercentageModel")??new Dictionary<string,object>();
            Dictionary<string,object> virtueModel=ReadDictionary(traits,"courtVirtueModel")??new Dictionary<string,object>();
            if(!FoundationTraitModelCurrent(traits)
                ||ReadInt(traits,"version",0)!=CoreTraitDocumentVersion
                ||ReadInt(percentageModel,"version",0)!=TraitPercentageModelVersion
                ||!ReadString(percentageModel,"id","").Equals(TraitPercentageModel,StringComparison.OrdinalIgnoreCase)
                ||ReadInt(virtueModel,"version",0)!=CourtVirtueModelVersion
                ||percentages.Count!=CoreTraitKeys.Length||virtues.Count!=CourtVirtueKeys.Length)return false;
            foreach(string key in CoreTraitKeys)
            {
                if(!foundation.ContainsKey(key)||!percentages.ContainsKey(key))return false;
                TraitPercentageBand(TraitInt(foundation,key),out int minimum,out int maximum);int percentage=ReadInt(percentages,key,-1);
                if(percentage<minimum||percentage>maximum)return false;
            }
            Dictionary<string,object> calculated=CalculateCourtVirtues(percentages);
            return CourtVirtueKeys.All(key=>ReadInt(virtues,key,int.MinValue)==ReadInt(calculated,key,int.MaxValue));
        }

        private static int MotiveAdjustment(int percentage,int maximumInfluence,int direction)
        {
            double centered=(Math.Max(0,Math.Min(100,percentage))-50d)/50d;
            return (int)Math.Round(centered*Math.Abs(maximumInfluence)*(direction<0?-1:1),MidpointRounding.AwayFromZero);
        }

        private static Dictionary<string,object> EvaluateCourtActionPersonality(Dictionary<string,object> traits,Dictionary<string,object> request)
        {
            request=request??new Dictionary<string,object>();Dictionary<string,object> percentages=ReadDictionary(traits,"traitPercentages")??new Dictionary<string,object>();Dictionary<string,object> virtues=CalculateCourtVirtues(percentages);
            string virtue=ReadString(request,"virtue","judgment");if(!CourtVirtueWeights.ContainsKey(virtue))throw new InvalidOperationException("Unknown court virtue: "+virtue);
            bool negative=string.Equals(ReadString(request,"polarity","positive"),"negative",StringComparison.OrdinalIgnoreCase);int score=ReadInt(virtues,virtue,50);int baseChance=Math.Max(0,Math.Min(100,ReadInt(request,"baseChance",50)));
            int motiveTotal=0;List<Dictionary<string,object>> audit=new List<Dictionary<string,object>>();
            foreach(Dictionary<string,object> motive in ReadDictionaryList(request,"motives"))
            {
                string key=ReadString(motive,"traitKey","");if(!CoreTraitKeys.Contains(key,StringComparer.OrdinalIgnoreCase))continue;int percentage=ReadInt(percentages,key,50);int influence=Math.Max(0,Math.Min(100,ReadInt(motive,"maximumInfluence",0)));int direction=ReadInt(motive,"direction",1)<0?-1:1;int adjustment=MotiveAdjustment(percentage,influence,direction);motiveTotal+=adjustment;
                audit.Add(new Dictionary<string,object>{{"traitKey",key},{"percentage",percentage},{"maximumInfluence",influence},{"direction",direction},{"adjustment",adjustment}});
            }
            int situational=ReadInt(request,"situationalModifier",0),pressure=baseChance-50+motiveTotal+situational;
            int threshold=Math.Max(0,Math.Min(100,negative?score-pressure:score+pressure));int roll=request.ContainsKey("roll")?Math.Max(1,Math.Min(100,ReadInt(request,"roll",1))):RollCourtD100();bool passed=negative?roll>threshold:roll<=threshold;
            return new Dictionary<string,object>{{"virtue",virtue},{"virtuePercentage",score},{"polarity",negative?"negative":"positive"},{"baseChance",baseChance},{"baseAdjustment",baseChance-50},{"motiveAdjustments",audit},{"motiveAdjustmentTotal",motiveTotal},{"situationalModifier",situational},{"actionPressure",pressure},{"threshold",threshold},{"roll",roll},{"passed",passed}};
        }

        private static Dictionary<string,object> EvaluateCourtInitiativePersonality(Dictionary<string,object> traits,Dictionary<string,object> request)
        {
            request=request??new Dictionary<string,object>();
            bool tangibleThreat=ReadBool(request,"tangibleThreat",false),initiationRequired=ReadBool(request,"initiationRequired",true);
            List<Dictionary<string,object>> stages=new List<Dictionary<string,object>>();
            Func<string,string,Dictionary<string,object>> evaluate=(virtue,rollKey)=>
            {
                Dictionary<string,object> stage=new Dictionary<string,object>(request,StringComparer.OrdinalIgnoreCase){{"virtue",virtue},{"polarity","positive"}};
                if(request.ContainsKey(rollKey))stage["roll"]=ReadInt(request,rollKey,1);else stage.Remove("roll");
                Dictionary<string,object> result=EvaluateCourtActionPersonality(traits,stage);result["stage"]=virtue;return result;
            };
            if(initiationRequired)
            {
                Dictionary<string,object> boldness=evaluate("boldness","boldnessRoll");stages.Add(boldness);
                if(!ReadBool(boldness,"passed",false))return InitiativeResult(tangibleThreat?"initiate_then_face_threat":"comfortable_initiative",tangibleThreat,true,stages,false,"boldness");
            }
            if(tangibleThreat)
            {
                Dictionary<string,object> courage=evaluate("courage","courageRoll");stages.Add(courage);
                if(!ReadBool(courage,"passed",false))return InitiativeResult(initiationRequired?"initiate_then_face_threat":"threat_already_present",true,initiationRequired,stages,false,"courage");
            }
            return InitiativeResult(tangibleThreat?(initiationRequired?"initiate_then_face_threat":"threat_already_present"):"comfortable_initiative",tangibleThreat,initiationRequired,stages,true,"");
        }

        private static Dictionary<string,object> InitiativeResult(string mode,bool tangibleThreat,bool initiationRequired,List<Dictionary<string,object>> stages,bool passed,string failedStage)
        {
            return new Dictionary<string,object>{{"mode",mode},{"tangibleThreat",tangibleThreat},{"initiationRequired",initiationRequired},{"stages",stages},{"passed",passed},{"failedStage",failedStage}};
        }

        private static int RollCourtD100(){byte[] bytes=new byte[4];using(RandomNumberGenerator rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);return 1+(int)(BitConverter.ToUInt32(bytes,0)%100u);}

        private static List<Dictionary<string,object>> RunTraitPercentageSelfTests()
        {
            List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();Action<string,bool,string,object> add=(id,pass,summary,data)=>rows.Add(new Dictionary<string,object>{{"id","trait_percentages_"+id},{"suite","trait_percentages"},{"passed",pass},{"summary",summary},{"data",data??new Dictionary<string,object>()}});
            Dictionary<string,object> foundation=CoreTraitKeys.ToDictionary(x=>x,x=>(object)0,StringComparer.OrdinalIgnoreCase);foundation["honesty"]=-2;foundation["courage"]=2;foundation["loyalty"]=-1;
            Dictionary<string,object> doc=new Dictionary<string,object>{{"foundationTraits",foundation},{"hiddenReignTraits",new Dictionary<string,object>(foundation,StringComparer.OrdinalIgnoreCase)}};EnsureTraitPercentageData(doc,"trait_test_hero");Dictionary<string,object> percentages=ReadDictionary(doc,"traitPercentages");
            add("bands",ReadInt(percentages,"honesty",-1)<=20&&ReadInt(percentages,"courage",-1)>=81&&ReadInt(percentages,"loyalty",-1)>=21&&ReadInt(percentages,"loyalty",-1)<=40,"Percentages remain inside their inclusive modifier bands.",percentages);
            Dictionary<string,object> repeat=new Dictionary<string,object>{{"foundationTraits",new Dictionary<string,object>(foundation,StringComparer.OrdinalIgnoreCase)}};EnsureTraitPercentageData(repeat,"trait_test_hero");add("deterministic",Json.Serialize(ReadDictionary(repeat,"traitPercentages"))==Json.Serialize(percentages),"Stable inputs produce stable percentages.",null);
            Dictionary<string,object> legacyFoundation=CoreTraitKeys.ToDictionary(x=>x,x=>(object)0,StringComparer.OrdinalIgnoreCase);
            Dictionary<string,object> legacyDocument=new Dictionary<string,object>{{"version",3},{"model","reign_core_personality_43_percent_v1"},{"visibleBannerlordTraits",new Dictionary<string,object>()},{"foundationTraits",legacyFoundation},{"hiddenReignTraits",new Dictionary<string,object>(legacyFoundation,StringComparer.OrdinalIgnoreCase)},{"definitions",TraitDefinitions()},{"basePersonalitySummary","Legacy foundation."}};
            EnsureTraitPercentageData(legacyDocument,"legacy_trait_hero");
            add("legacy_foundation_not_relabelled",ReadInt(legacyDocument,"version",0)==3&&ReadString(legacyDocument,"model","")=="reign_core_personality_43_percent_v1"&&!TraitPercentageDocumentReady(legacyDocument),"A complete legacy score set cannot be relabeled as the active foundation model merely by recalculating downstream fields.",new Dictionary<string,object>{{"version",ReadInt(legacyDocument,"version",0)},{"model",ReadString(legacyDocument,"model","")}});
            Dictionary<string,object> activeDocument=BuildTraitDocument(FoundationTraitRangeVNextTestHero("active_trait_percentage_model"));
            add("active_dependency_chain",TraitPercentageDocumentReady(activeDocument)&&ReadInt(ReadDictionary(activeDocument,"traitPercentageModel"),"version",0)==TraitPercentageModelVersion&&ReadInt(ReadDictionary(activeDocument,"courtVirtueModel"),"version",0)==CourtVirtueModelVersion,"The active foundation, percentage, and court-group versions form one validated dependency chain.",new Dictionary<string,object>{{"foundation",ReadDictionary(activeDocument,"foundationTraitModel")},{"percentage",ReadDictionary(activeDocument,"traitPercentageModel")},{"court",ReadDictionary(activeDocument,"courtVirtueModel")}});
            add("relative_remap",RemapTraitPercentage(10,-2,2)==91,"Modifier edits preserve relative band position.",RemapTraitPercentage(10,-2,2));
            Dictionary<string,object> virtues=ReadDictionary(doc,"courtVirtues");add("seven_virtues",virtues!=null&&virtues.Count==7&&CourtVirtueKeys.All(virtues.ContainsKey)&&!virtues.ContainsKey("humanity"),"Compassion and Boldness replace Humanity, producing seven positive-facing virtues.",virtues);
            Func<string,string> weightSignature=virtue=>string.Join(",",CourtVirtueWeights[virtue].Select(x=>x.TraitKey+":"+x.Weight+":"+(x.Inverse?"inverse":"direct")));
            string honorWeights=weightSignature("honor"),loyaltyWeights=weightSignature("loyalty"),judgmentWeights=weightSignature("judgment");
            add("honor_v3_weights",honorWeights=="honesty:5:direct,dutyMotivation:2:direct,discipline:1:direct,generosity:1:direct,greed:3:inverse,envy:1:inverse","Honor is anchored in practical integrity without Mercy or Jealousy.",honorWeights);
            add("loyalty_v3_weights",loyaltyWeights=="loyalty:5:direct,dutyMotivation:2:direct","Loyalty is anchored in allegiance and duty without Social Trust.",loyaltyWeights);
            add("judgment_v3_weights",judgmentWeights=="patience:3:direct,emotionalStability:3:direct,discipline:2:direct,pragmatism:2:direct,curiosity:1:direct,knowledgeMotivation:2:direct,tact:1:direct,impulsiveness:3:inverse,irritability:2:inverse,jealousy:1:inverse,envy:1:inverse","Judgment emphasizes knowledge and practical control without Fearfulness.",judgmentWeights);
            Dictionary<string,object> calibrationExamples=new Dictionary<string,object>{{"25",CalibrateCourtVirtueScore(25d)},{"30",CalibrateCourtVirtueScore(30d)},{"35",CalibrateCourtVirtueScore(35d)},{"50",CalibrateCourtVirtueScore(50d)},{"65",CalibrateCourtVirtueScore(65d)},{"70",CalibrateCourtVirtueScore(70d)},{"75",CalibrateCourtVirtueScore(75d)},{"20",CalibrateCourtVirtueScore(20d)},{"80",CalibrateCourtVirtueScore(80d)}};
            add("tail_calibration",ReadInt(calibrationExamples,"25",-1)==5&&ReadInt(calibrationExamples,"30",-1)==20&&ReadInt(calibrationExamples,"35",-1)==35&&ReadInt(calibrationExamples,"50",-1)==50&&ReadInt(calibrationExamples,"65",-1)==65&&ReadInt(calibrationExamples,"70",-1)==80&&ReadInt(calibrationExamples,"75",-1)==95&&ReadInt(calibrationExamples,"20",-1)==0&&ReadInt(calibrationExamples,"80",-1)==100,"The shared tail curve preserves 35..65 and expands aligned scores toward clamped poles.",calibrationExamples);
            add("calibration_rounding",CalibrateCourtVirtueScore(64.5d)==65&&CalibrateCourtVirtueScore(65.5d)==67,"Calibration rounds only the final score away from zero.",new Dictionary<string,object>{{"64.5",CalibrateCourtVirtueScore(64.5d)},{"65.5",CalibrateCourtVirtueScore(65.5d)}});
            Dictionary<string,object> neutral=CoreTraitKeys.ToDictionary(x=>x,x=>(object)50,StringComparer.OrdinalIgnoreCase);
            Dictionary<string,object> judgmentLowFear=new Dictionary<string,object>(neutral,StringComparer.OrdinalIgnoreCase),judgmentHighFear=new Dictionary<string,object>(neutral,StringComparer.OrdinalIgnoreCase);judgmentLowFear["fearfulness"]=0;judgmentHighFear["fearfulness"]=100;
            add("judgment_ignores_fearfulness",ReadInt(CalculateCourtVirtues(judgmentLowFear),"judgment",-1)==ReadInt(CalculateCourtVirtues(judgmentHighFear),"judgment",-2),"Fearfulness no longer changes Judgment.",new Dictionary<string,object>{{"lowFear",CalculateCourtVirtues(judgmentLowFear)},{"highFear",CalculateCourtVirtues(judgmentHighFear)}});
            Dictionary<string,object> loyaltyLowTrust=new Dictionary<string,object>(neutral,StringComparer.OrdinalIgnoreCase),loyaltyHighTrust=new Dictionary<string,object>(neutral,StringComparer.OrdinalIgnoreCase);loyaltyLowTrust["socialTrust"]=0;loyaltyHighTrust["socialTrust"]=100;
            add("loyalty_ignores_social_trust",ReadInt(CalculateCourtVirtues(loyaltyLowTrust),"loyalty",-1)==ReadInt(CalculateCourtVirtues(loyaltyHighTrust),"loyalty",-2),"Social Trust no longer changes Loyalty.",new Dictionary<string,object>{{"lowTrust",CalculateCourtVirtues(loyaltyLowTrust)},{"highTrust",CalculateCourtVirtues(loyaltyHighTrust)}});
            Dictionary<string,object> honorLowRemoved=new Dictionary<string,object>(neutral,StringComparer.OrdinalIgnoreCase),honorHighRemoved=new Dictionary<string,object>(neutral,StringComparer.OrdinalIgnoreCase);honorLowRemoved["mercy"]=0;honorLowRemoved["jealousy"]=0;honorHighRemoved["mercy"]=100;honorHighRemoved["jealousy"]=100;
            add("honor_ignores_mercy_and_jealousy",ReadInt(CalculateCourtVirtues(honorLowRemoved),"honor",-1)==ReadInt(CalculateCourtVirtues(honorHighRemoved),"honor",-2),"Mercy and Jealousy no longer change Honor.",new Dictionary<string,object>{{"lowRemoved",CalculateCourtVirtues(honorLowRemoved)},{"highRemoved",CalculateCourtVirtues(honorHighRemoved)}});
            Dictionary<string,object> migrationFoundation=CoreTraitKeys.ToDictionary(x=>x,x=>(object)0,StringComparer.OrdinalIgnoreCase),migrationDoc=new Dictionary<string,object>{{"foundationTraits",migrationFoundation},{"hiddenReignTraits",new Dictionary<string,object>(migrationFoundation,StringComparer.OrdinalIgnoreCase)}};EnsureTraitPercentageData(migrationDoc,"trait_migration_hero");string migrationPercentages=Json.Serialize(ReadDictionary(migrationDoc,"traitPercentages"));ReadDictionary(migrationDoc,"courtVirtueModel")["version"]=2;migrationDoc["courtVirtues"]=CourtVirtueKeys.ToDictionary(x=>x,x=>(object)1,StringComparer.OrdinalIgnoreCase);bool migrated=EnsureTraitPercentageData(migrationDoc,"trait_migration_hero");
            add("v2_migrates_in_place",migrated&&ReadInt(ReadDictionary(migrationDoc,"courtVirtueModel"),"version",0)==CourtVirtueModelVersion&&Json.Serialize(ReadDictionary(migrationDoc,"traitPercentages"))==migrationPercentages&&CourtVirtueKeys.Any(x=>ReadInt(ReadDictionary(migrationDoc,"courtVirtues"),x,1)!=1)&&!migrationDoc.ContainsKey("courtVirtueRawScores"),"Model-v2 profiles replace only derived data and preserve foundation percentages without persisting a raw score.",migrationDoc);
            Dictionary<string,object> positive=EvaluateCourtActionPersonality(doc,new Dictionary<string,object>{{"virtue","courage"},{"polarity","positive"},{"baseChance",50},{"roll",ReadInt(virtues,"courage",50)}});add("positive_boundary",ReadBool(positive,"passed",false)&&ReadInt(positive,"threshold",-1)==ReadInt(virtues,"courage",50),"Positive rolls pass at or below the raw score when base chance is 50.",positive);
            Dictionary<string,object> low=EvaluateCourtActionPersonality(doc,new Dictionary<string,object>{{"virtue","honor"},{"polarity","negative"},{"baseChance",25},{"roll",100}}),high=EvaluateCourtActionPersonality(doc,new Dictionary<string,object>{{"virtue","honor"},{"polarity","negative"},{"baseChance",75},{"roll",100}});add("negative_base_direction",ReadInt(high,"threshold",101)<ReadInt(low,"threshold",-1),"Higher negative-action base chance lowers its pass-above threshold.",new Dictionary<string,object>{{"low",low},{"high",high}});
            Dictionary<string,object> motive=EvaluateCourtActionPersonality(doc,new Dictionary<string,object>{{"virtue","loyalty"},{"polarity","negative"},{"baseChance",50},{"roll",100},{"motives",new ArrayList{new Dictionary<string,object>{{"traitKey","ambition"},{"maximumInfluence",20},{"direction",1}}}}});add("motive_audit",ReadDictionaryList(motive,"motiveAdjustments").Count==1,"Neutral motives contribute bounded, auditable pressure.",motive);
            Dictionary<string,object> safeInitiative=EvaluateCourtInitiativePersonality(doc,new Dictionary<string,object>{{"tangibleThreat",false},{"initiationRequired",true},{"baseChance",50},{"boldnessRoll",1}});List<Dictionary<string,object>> safeStages=ReadDictionaryList(safeInitiative,"stages");add("safe_initiative_uses_boldness",ReadBool(safeInitiative,"passed",false)&&safeStages.Count==1&&ReadString(safeStages[0],"virtue","")=="boldness","Comfortable initiation uses Boldness without a Courage check.",safeInitiative);
            Dictionary<string,object> dangerousInitiative=EvaluateCourtInitiativePersonality(doc,new Dictionary<string,object>{{"tangibleThreat",true},{"initiationRequired",true},{"baseChance",50},{"boldnessRoll",1},{"courageRoll",1}});List<Dictionary<string,object>> dangerousStages=ReadDictionaryList(dangerousInitiative,"stages");add("dangerous_initiative_uses_both",ReadBool(dangerousInitiative,"passed",false)&&dangerousStages.Count==2&&ReadString(dangerousStages[0],"virtue","")=="boldness"&&ReadString(dangerousStages[1],"virtue","")=="courage","Initiating and following through under a tangible threat checks Boldness and then Courage.",dangerousInitiative);
            Dictionary<string,object> existingThreat=EvaluateCourtInitiativePersonality(doc,new Dictionary<string,object>{{"tangibleThreat",true},{"initiationRequired",false},{"baseChance",50},{"courageRoll",1}});List<Dictionary<string,object>> threatStages=ReadDictionaryList(existingThreat,"stages");add("existing_threat_uses_courage",ReadBool(existingThreat,"passed",false)&&threatStages.Count==1&&ReadString(threatStages[0],"virtue","")=="courage","Acting when a tangible threat already exists uses Courage without a Boldness check.",existingThreat);
            Dictionary<string,object> noApproach=EvaluateSocialEventApproach(60,1), passingApproach=EvaluateSocialEventApproach(80,19), failingApproach=EvaluateSocialEventApproach(80,20);
            add("social_event_boldness_approach",!ReadBool(noApproach,"passed",true)&&ReadInt(noApproach,"chance",1)==0&&ReadBool(passingApproach,"passed",false)&&!ReadBool(failingApproach,"passed",true),"Social-event approaches use Boldness minus 60 and pass only when a 1d100 roll is strictly below that chance.",new Dictionary<string,object>{{"none",noApproach},{"passing",passingApproach},{"failing",failingApproach}});
            List<string> rankedApproaches=SelectSocialEventApproachIds(new List<Dictionary<string,object>>{new Dictionary<string,object>{{"heroStringId","low"},{"approachScore",65},{"candidateOrder",0}},new Dictionary<string,object>{{"heroStringId","high"},{"approachScore",90},{"candidateOrder",2}},new Dictionary<string,object>{{"heroStringId","middle"},{"approachScore",80},{"candidateOrder",1}}},RollSocialEventD4(2));
            add("social_event_d4_quota_and_rank",rankedApproaches.SequenceEqual(new[]{"high","middle"}),"A d4 limits social-event approaches to one through four successful NPCs, selected in descending Boldness score order.",rankedApproaches);
            Dictionary<string,object> normalJoin=EvaluateSocialEventJoin(80,60,19,19),failedJoin=EvaluateSocialEventJoin(80,60,19,20),danceJoin=EvaluateSocialEventJoin(80,40,39,39);
            add("social_event_double_join_roll",ReadBool(normalJoin,"passed",false)&&!ReadBool(failedJoin,"passed",true)&&ReadBool(danceJoin,"passed",false),"Mid-conversation joins require two strict Boldness rolls and support the opposite-sex dance threshold.",new Dictionary<string,object>{{"normal",normalJoin},{"failed",failedJoin},{"dance",danceJoin}});
            return rows;
        }
    }
}
