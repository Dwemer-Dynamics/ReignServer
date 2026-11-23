using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static List<Dictionary<string, object>> RunRelationshipSimulationSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["summary"] = summary });
            Dictionary<string, object> traits = CharactersService.CoreTraitKeys.ToDictionary(x => x, x => (object)0, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>();
            for (int i = 0; i < 12; i++)
            {
                bool first = i < 8;
                heroes.Add(new Dictionary<string, object>
                {
                    ["heroStringId"] = "sim_hero_" + i, ["name"] = "Hero " + i, ["age"] = 24 + i, ["isAlive"] = true,
                    ["isFemale"] = i % 2 == 0, ["isLord"] = true, ["kingdomId"] = first ? "kingdom_a" : "kingdom_b",
                    ["clanId"] = (first ? "clan_a_" : "clan_b_") + (i / 2), ["spouseId"] = "",
                    ["foundationTraits"] = traits
                });
            }
            Dictionary<string, object> census = new Dictionary<string, object>
            {
                ["campaignId"] = "relationship_simulation_self_test", ["worldDay"] = 100d, ["heroes"] = heroes,
                ["kingdoms"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["kingdomId"] = "kingdom_a", ["name"] = "Kingdom A", ["cityIds"] = new List<string> { "a1", "a2", "a3" } },
                    new Dictionary<string, object> { ["kingdomId"] = "kingdom_b", ["name"] = "Kingdom B", ["cityIds"] = new List<string> { "b1" } }
                },
                ["clans"] = new List<Dictionary<string, object>>()
            };
            RelationshipSimulationRun one = CreateRelationshipSimulation(census, 28, 77, false);
            RelationshipSimulationRun two = CreateRelationshipSimulation(census, 28, 77, false);
            for (int offset = 0; offset < 28; offset++)
            {
                if (offset > 0 && offset % 7 == 0) { AssignRelationshipSimulationLocations(one, offset / 7); AssignRelationshipSimulationLocations(two, offset / 7); }
                RunRelationshipSimulationDay(one, one.StartDay + offset);
                RunRelationshipSimulationDay(two, two.StartDay + offset);
                one.CompletedDays = two.CompletedDays = offset + 1;
            }
            add("kingdom_city_grouping", one.RealCityCounts["kingdom_a"] == 3 && one.RealCityCounts["kingdom_b"] == 1 && one.Heroes.Count == 12,
                "All eligible NPCs are grouped by kingdom and each kingdom retains its real city count.");
            add("seven_day_relocation", one.RelocationHistory.Count == 4 && one.RelocationHistory.All(x => (ReadInt(x, "day", 0) - one.StartDay) % 7 == 0)
                && one.Heroes.Values.All(x => one.LocationsByKingdom[x.KingdomId].Contains(x.LocationId, StringComparer.OrdinalIgnoreCase)),
                "Locations reshuffle only on seven-day boundaries and never cross kingdom membership.");
            string fingerprintOne = RelationshipSimulationFingerprint(one), fingerprintTwo = RelationshipSimulationFingerprint(two);
            add("seed_determinism", fingerprintOne == fingerprintTwo, "Identical census and seed produce identical relationships, relocations, and event totals.");
            Dictionary<string, object> status = RelationshipSimulationStatusDictionary(one, "trust", "");
            Dictionary<string, object> selectedDistribution = ReadDictionary(status, "selectedDistribution") ?? new Dictionary<string, object>();
            add("exact_and_banded_distributions", ReadDictionaryList(selectedDistribution, "exactLevels").Count > 0
                && ReadDictionaryList(selectedDistribution, "bands").Count == 9,
                "The monitor exposes exact integer levels and readable -100..100 bands.");
            add("all_relationship_facets", RelationshipFacetKeys.All(x => one.Relationships.Values.All(r => r.Facets.ContainsKey(x))),
                "Every directional relationship tracks every production relationship facet.");
            add("no_llm", ReadInt(status, "llmCallCount", -1) == 0 && ReadBool(status, "noLlmConfirmed", false),
                "Simulation event generation and directional evaluation make no LLM calls.");
            add("random_event_evaluator", ReadDictionary(DeterministicRelationshipEvaluation("political_rivalry_argument", "Two nobles argued over status.", new Dictionary<string, object>(), new Dictionary<string, object>()), "facetDeltas") != null,
                "Random simulation events use the production deterministic relationship evaluator.");
            Dictionary<string,object> sincereRomance=new Dictionary<string,object>{{"presentation","sincere"},{"genuineInterest",65d},{"strategicInterest",20d},
                {"hardConstraints",new Dictionary<string,object>{{"coercive",false}}}};
            Dictionary<string,object> exactFlirt=DeterministicRelationshipEvaluation("mutual_romantic_flirtation","They exchanged mutual flirtation.",
                new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",sincereRomance}}},{"preEventRomanceIntensity",25d}},new Dictionary<string,object>());
            Dictionary<string,object> exactFlirtDeltas=ReadDictionary(exactFlirt,"facetDeltas")??new Dictionary<string,object>();
            add("explicit_romance_delta_precedence",ReadDouble(exactFlirtDeltas,"attraction",0d)==5d&&ReadDouble(exactFlirtDeltas,"affection",0d)==3d,
                "Registered flirtation keeps its exact +5 attraction and +3 affection instead of being overwritten by generic wording.");
            Dictionary<string,double[]> exactRomanceOutcomes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["mutual_romantic_flirtation"]=new[]{5d,3d,0d},["romantic_confidence"]=new[]{3d,4d,4d},
                ["romantic_intimacy"]=new[]{8d,7d,3d},["secret_affair_intimacy"]=new[]{10d,8d,3d}
            };
            bool exactOutcomes=exactRomanceOutcomes.All(expected=>
            {
                Dictionary<string,object> deltas=ReadDictionary(DeterministicRelationshipEvaluation(expected.Key,expected.Key,
                    new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",sincereRomance}}},{"preEventRomanceIntensity",25d}},
                    new Dictionary<string,object>()),"facetDeltas")??new Dictionary<string,object>();
                return ReadDouble(deltas,"attraction",0d)==expected.Value[0]&&ReadDouble(deltas,"affection",0d)==expected.Value[1]
                    &&ReadDouble(deltas,"trust",0d)==expected.Value[2];
            });
            add("all_explicit_romance_outcomes",exactOutcomes,"All four registered romantic events use their exact shared attraction, affection, and trust deltas.");
            Dictionary<string,object> developedFlirt=DeterministicRelationshipEvaluation("mutual_romantic_flirtation","They exchanged mutual flirtation.",
                new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",sincereRomance}}},{"preEventRomanceIntensity",50d}},new Dictionary<string,object>());
            Dictionary<string,object> developedFlirtDeltas=ReadDictionary(developedFlirt,"facetDeltas")??new Dictionary<string,object>();
            add("developed_romance_bond_bonus",ReadDouble(developedFlirtDeltas,"attraction",0d)==7d&&ReadDouble(developedFlirtDeltas,"affection",0d)==5d,
                "Developed sincere romance adds exactly +2 attraction and +2 affection.");
            Dictionary<string,object> calculatedRomance=new Dictionary<string,object>{{"presentation","calculated"},{"genuineInterest",20d},{"strategicInterest",70d},
                {"hardConstraints",new Dictionary<string,object>{{"coercive",false}}}};
            Dictionary<string,object> calculatedFlirt=DeterministicRelationshipEvaluation("mutual_romantic_flirtation","They exchanged mutual flirtation.",
                new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",calculatedRomance}}},{"preEventRomanceIntensity",75d}},new Dictionary<string,object>());
            Dictionary<string,object> calculatedFlirtDeltas=ReadDictionary(calculatedFlirt,"facetDeltas")??new Dictionary<string,object>();
            add("calculated_romance_no_fabricated_attraction",ReadDouble(calculatedFlirtDeltas,"attraction",1d)==0d
                &&ReadDouble(calculatedFlirtDeltas,"interest_alignment",0d)>=5d&&ReadDouble(calculatedFlirtDeltas,"dependence",0d)>=2d,
                "Calculated romance receives no attraction or stage bonus and redirects the mechanical effect into strategic facets.");
            add("census_isolation", ReadDictionaryList(census, "heroes").All(x => string.IsNullOrWhiteSpace(ReadString(x, "simulationLocationId", ""))),
                "Simulation state is cloned and does not alter the source census or live campaign.");
            Dictionary<string, object> installed = BuildRelationshipSimulationStaticCensus();
            Dictionary<string, int> installedModules = ReadDictionaryList(installed, "sourceModules")
                .ToDictionary(x => ReadString(x, "moduleId", ""), x => ReadInt(x, "npcCount", 0), StringComparer.OrdinalIgnoreCase);
            add("installed_all_campaign_roster", string.IsNullOrWhiteSpace(ReadString(installed, "error", ""))
                && installedModules.ContainsKey("SandBox") && installedModules["SandBox"] > 0
                && installedModules.ContainsKey("NavalDLC") && installedModules["NavalDLC"] > 0
                && installedModules.ContainsKey("ReignBeta") && installedModules["ReignBeta"] > 0,
                "The default roster merges fixed adult NPCs from Native/Sandbox, War Sails, and Reign module data.");
            add("campaign_independent_roster", ReadString(installed, "campaignId", "") == "installed_module_roster"
                && !ReadDictionaryList(installed, "heroes").Any(x => ReadString(x, "heroStringId", "").Equals("main_hero", StringComparison.OrdinalIgnoreCase)),
                "The simulator no longer requires a save selection and excludes the player template from the NPC pool.");
            add("pair_specific_first_contact_chemistry", one.Relationships.Values.Select(x => Math.Round(x.Facets["attraction"], 2)).Distinct().Count() > 5
                && one.Relationships.Values.Any(x => x.Facets["rivalry"] >= 10d)
                && one.Relationships.Values.Select(x => Math.Round(x.Facets["jealousy"], 2)).Distinct().Count() > 5,
                "First contact deterministically varies attraction, rivalry, jealousy, and other directional chemistry by pair.");
            Dictionary<string, object> neutralTraits = new Dictionary<string, object> { ["foundationTraits"] = traits };
            Dictionary<string, object> observer = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = false, ["clanTier"] = 2, ["fiefCount"] = 0 };
            Dictionary<string, object> lowStatusTarget = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["clanTier"] = 1, ["fiefCount"] = 0 };
            Dictionary<string, object> highStatusTarget = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["clanTier"] = 6, ["fiefCount"] = 5, ["isRuler"] = true, ["renown"] = 2000, ["influence"] = 1000 };
            Dictionary<string, double> lowStatusChemistry = BuildInitialRelationshipFacets("observer", "target", observer, lowStatusTarget, neutralTraits, neutralTraits, 0, false, false, false, true);
            Dictionary<string, double> highStatusChemistry = BuildInitialRelationshipFacets("observer", "target", observer, highStatusTarget, neutralTraits, neutralTraits, 0, false, false, false, true);
            add("target_relative_status_chemistry", highStatusChemistry["fear"] > lowStatusChemistry["fear"] + 10d
                && highStatusChemistry["envy"] > lowStatusChemistry["envy"],
                "Target power and status now change fear and envy while holding the deterministic pair roll constant.");
            Dictionary<string, object> normalized = RelationshipSimulationStatusDictionary(one, "trust", "");
            add("one_guaranteed_event_per_kingdom", ReadString(normalized, "eventPolicy", "") == "one_guaranteed_event_per_eligible_kingdom_every_two_days"
                && one.EventCountsByKingdom.Keys.Contains("kingdom_a", StringComparer.OrdinalIgnoreCase)
                && one.EventCountsByKingdom.Keys.Contains("kingdom_b", StringComparer.OrdinalIgnoreCase)
                && one.EventCountsByKingdom.Values.All(x => x <= 14),
                "Each eligible kingdom receives one guaranteed consequential event every two days.");
            add("normalized_simulation_reporting", normalized.ContainsKey("eventParticipationsPerNpc")
                && normalized.ContainsKey("positiveNativeRelationChanges") && normalized.ContainsKey("negativeNativeRelationChanges")
                && normalized.ContainsKey("threadCounts") && normalized.ContainsKey("romanceFunnel") && normalized.ContainsKey("passivePositiveDriftByFacet")
                && ReadDictionaryList(normalized, "cityCountCohorts").All(x => x.ContainsKey("selectedFacetMean") && x.ContainsKey("eventsPer100Npcs")),
                "Status reports normalized event participation, storyline stages, passive drift, signed native changes, and relationship outcomes by city-count cohort.");
            RelationshipSimulationRun storyRun=CreateRelationshipSimulation(census,365,91,false);
            string storyA=storyRun.Heroes.Keys.First(),storyB=storyRun.Heroes.Keys.Skip(1).First();
            ApplyRelationshipSimulationStoryEvent(storyRun,"mutual_romantic_flirtation",storyA,storyB,storyRun.StartDay);
            double recentMultiplier=RelationshipSimulationStoryMultiplier(storyRun,storyA,storyB,storyRun.StartDay+7);
            double monthMultiplier=RelationshipSimulationStoryMultiplier(storyRun,storyA,storyB,storyRun.StartDay+20);
            double laterMultiplier=RelationshipSimulationStoryMultiplier(storyRun,storyA,storyB,storyRun.StartDay+45);
            add("story_continuation_multipliers",Math.Abs(recentMultiplier-3d)<0.0001d&&Math.Abs(monthMultiplier-2.5d)<0.0001d&&Math.Abs(laterMultiplier-2d)<0.0001d,
                "Active storylines receive exact 3x, 2.5x, and 2x recency multipliers without stacking.");
            AssignRelationshipSimulationLocations(storyRun,1);
            add("story_survives_rotation",RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"romance",storyRun.StartDay+7,true)==25d,
                "Location rotation does not erase a developing relationship storyline.");
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+8,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+9,sincereRomance,sincereRomance);
            RelationshipSimulationHero storyHero=storyRun.Heroes[storyA],storyPartner=storyRun.Heroes[storyB];
            RelationshipSimulationHero politicalMatch=storyRun.Heroes.Values.First(x=>x.Id!=storyA&&x.Id!=storyB
                &&ReadBool(x.Data,"isFemale",false)!=ReadBool(storyHero.Data,"isFemale",false));
            add("developed_courtship_arrangement_penalty",Math.Abs(RelationshipSimulationArrangedCourtshipMultiplier(storyRun,storyA,politicalMatch.Id,storyRun.StartDay+9)-.75d)<.0001d
                &&Math.Abs(RelationshipSimulationArrangedCourtshipMultiplier(storyRun,storyA,storyB,storyRun.StartDay+9)-1d)<.0001d,
                "A developed courtship applies a 25% third-party arrangement penalty but never penalizes its own couple.");
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+10,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+11,sincereRomance,sincereRomance);
            add("major_courtship_arrangement_penalty",Math.Abs(RelationshipSimulationArrangedCourtshipMultiplier(storyRun,storyA,politicalMatch.Id,storyRun.StartDay+11)-.50d)<.0001d,
                "A major courtship applies the exact 50% third-party arrangement penalty without becoming an absolute block.");
            foreach(RelationshipSimulationRelation relation in new[]{EnsureRelationshipSimulationRelation(storyRun,storyA,storyB),EnsureRelationshipSimulationRelation(storyRun,storyB,storyA)})
            {
                relation.Facets["attraction"]=45d;relation.Facets["affection"]=70d;relation.Facets["trust"]=55d;relation.Facets["resentment"]=0d;
            }
            Dictionary<string,object> storyPostureA=RelationshipSimulationRomanticPosture(storyRun,storyA,storyB,storyRun.StartDay+11);
            Dictionary<string,object> storyPostureB=RelationshipSimulationRomanticPosture(storyRun,storyB,storyA,storyRun.StartDay+11);
            double storyIntensity=RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"romance",storyRun.StartDay+11,true);
            add("targeted_sincere_progression",StoryPhysicalRomanceEligible(true,true,storyIntensity,50d,storyPostureA,storyPostureB)
                &&RomanticMarriageReady(RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(storyRun,storyA,storyB)),
                    RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(storyRun,storyB,storyA))),
                "A suitable co-located bilateral courtship can cross both the intimacy posture gate and the unchanged romantic-marriage facet gate.");
            MarryRelationshipSimulationHeroes(storyRun,storyHero,politicalMatch,"arranged",storyRun.StartDay+12);
            add("arranged_marriage_preserves_displaced_romance",storyRun.CourtshipDisplacements==1
                &&RelationshipSimulationStory(storyRun,storyA,storyB,"romance",storyRun.StartDay+12,false).Route=="temptation"
                &&RelationshipSimulationStory(storyRun,storyB,storyA,"romance",storyRun.StartDay+12,false).Route=="temptation",
                "An overriding political marriage preserves the bilateral romance as a private temptation.");
            Dictionary<string,object> affairPostureA=RelationshipSimulationRomanticPosture(storyRun,storyA,storyB,storyRun.StartDay+12);
            Dictionary<string,object> affairPostureB=RelationshipSimulationRomanticPosture(storyRun,storyB,storyA,storyRun.StartDay+12);
            add("targeted_affair_gate",StoryPhysicalRomanceEligible(true,true,storyIntensity,75d,affairPostureA,affairPostureB),
                "An established bilateral temptation can cross the unchanged affair posture gate when at least one participant is married.");
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_rejection",storyA,storyB,storyRun.StartDay+13,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_boundary",storyA,storyB,storyRun.StartDay+14,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_boundary",storyA,storyB,storyRun.StartDay+15,sincereRomance,sincereRomance);
            add("rejection_removes_continuity",RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"romance",storyRun.StartDay+15,true)==0d,
                "Repeated rejection or boundary events reduce romance by 30 and can remove its continuity advantage entirely.");
            for(int i=0;i<4;i++)ApplyRelationshipSimulationStoryEvent(storyRun,"political_rivalry_argument",storyA,storyB,storyRun.StartDay+i*7);
            add("repeated_rivalry_creates_feud",ReadSimulationCount(storyRun.EventCounts,"feuds_started")==1
                && RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"rivalry",storyRun.StartDay+28,false)>=75d,
                "Repeated qualifying rivalry events create one feud milestone.");
            return results;
        }
    }
}
