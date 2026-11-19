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
        private static void RecordRelationshipSimulationRomanceFunnel(RelationshipSimulationRun run,RelationshipSimulationEdge edge,
            Dictionary<string,object> postureA,Dictionary<string,object> postureB,int day)
        {
            run.RomanceFunnelPairs["evaluated"].Add(edge.PairKey);
            double baseA=Math.Max(ReadDouble(postureA,"baseGenuineInterest",0d),ReadDouble(postureA,"baseStrategicInterest",0d));
            double baseB=Math.Max(ReadDouble(postureB,"baseGenuineInterest",0d),ReadDouble(postureB,"baseStrategicInterest",0d));
            if(baseA>=25d&&baseB>=25d)run.RomanceFunnelPairs["baseInterest25"].Add(edge.PairKey);
            if(ReadDouble(postureA,"receptivity",0d)>=40d&&ReadDouble(postureB,"receptivity",0d)>=40d)run.RomanceFunnelPairs["receptivity40"].Add(edge.PairKey);
            if(ReadDouble(postureA,"receptivity",0d)>=60d&&ReadDouble(postureB,"receptivity",0d)>=60d)run.RomanceFunnelPairs["receptivity60"].Add(edge.PairKey);
            double thread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"romance",day,true);
            if(thread>=25d)run.RomanceFunnelPairs["thread25"].Add(edge.PairKey);
            if(thread>=50d)run.RomanceFunnelPairs["thread50"].Add(edge.PairKey);
            if(thread>=75d)run.RomanceFunnelPairs["thread75"].Add(edge.PairKey);
            if(thread>=50d&&!edge.CoLocated)run.RomanceBlockedCoLocation++;
            if(thread>=50d&&(ReadDouble(postureA,"receptivity",0d)<60d||ReadDouble(postureB,"receptivity",0d)<60d))run.RomanceBlockedReceptivity++;
            foreach(Dictionary<string,object> posture in new[]{postureA,postureB})
            {
                Dictionary<string,object> continuity=ReadDictionary(posture,"continuity")??new Dictionary<string,object>();
                if(ReadBool(continuity,"eligible",false))
                {
                    run.RomanceContinuityApplications++;
                    run.RomanceContinuityGenuineBonus+=ReadDouble(continuity,"genuineBonus",0d);
                    run.RomanceContinuityStrategicBonus+=ReadDouble(continuity,"strategicBonus",0d);
                    run.RomanceContinuityReceptivityBonus+=ReadDouble(continuity,"receptivityBonus",0d);
                }
                TrackRelationshipSimulationRomanceMetric(run,"genuineInterest",ReadDouble(posture,"genuineInterest",0d));
                TrackRelationshipSimulationRomanceMetric(run,"strategicInterest",ReadDouble(posture,"strategicInterest",0d));
                TrackRelationshipSimulationRomanceMetric(run,"receptivity",ReadDouble(posture,"receptivity",0d));
            }
        }

        private static void TrackRelationshipSimulationRomanceMetric(RelationshipSimulationRun run,string key,double value)
        {
            double prior;
            if(!run.RomanceMetricMinimum.TryGetValue(key,out prior)||value<prior)run.RomanceMetricMinimum[key]=value;
            if(!run.RomanceMetricMaximum.TryGetValue(key,out prior)||value>prior)run.RomanceMetricMaximum[key]=value;
        }

        private static Dictionary<string, object> RelationshipSimulationRomanticPosture(RelationshipSimulationRun run, string actorId, string targetId, int day)
        {
            RelationshipSimulationHero actor = run.Heroes[actorId], target = run.Heroes[targetId];
            RelationshipSimulationRelation relationship = EnsureRelationshipSimulationRelation(run, actorId, targetId);
            string spouseId = ReadString(actor.Data, "spouseId", "");
            Dictionary<string, object> spouseFacets = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(spouseId) && run.Heroes.ContainsKey(spouseId))
                spouseFacets = EnsureRelationshipSimulationRelation(run, actorId, spouseId).Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> characteristics = new Dictionary<string, object> { ["traits"] = actor.Traits };
            Dictionary<string, object> opportunityPayload = new Dictionary<string, object>
            {
                ["opportunitySnapshot"] = new Dictionary<string, object>
                {
                    ["identityKnown"] = true,
                    ["observer"] = actor.Data,
                    ["target"] = target.Data
                }
            };
            Dictionary<string, object> opportunity = DialogueService.CalculatePerceivedOpportunity(actor.Data, characteristics, opportunityPayload, new Dictionary<string, object> { ["identityState"] = "known" });
            bool closeKin = RelationshipSimulationCloseKin(actor, target);
            bool suitable = ReadBool(actor.Data, "isFemale", false) != ReadBool(target.Data, "isFemale", false) && !closeKin;
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["worldDay"] = day,
                ["skipCooldownQuery"] = true,
                ["opportunitySnapshot"] = new Dictionary<string, object>
                {
                    ["identityKnown"] = true,
                    ["observer"] = actor.Data,
                    ["target"] = target.Data,
                    ["suitability"] = new Dictionary<string, object> { ["adults"] = true, ["nativeSuitable"] = suitable, ["closeKin"] = closeKin }
                },
                ["sceneOpportunity"] = new Dictionary<string, object> { ["private"] = true, ["exposure"] = 0.12d, ["coercive"] = false, ["witnessIds"] = new List<string>() }
            };
            RelationshipSimulationStoryThread romanceThread = RelationshipSimulationStory(run, actorId, targetId, "romance", day, false);
            Dictionary<string, object> relationshipContext = new Dictionary<string, object>
            {
                ["facets"] = relationship.Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase)
            };
            if (romanceThread != null)
            {
                double effective = EffectiveStoryIntensity(romanceThread.Intensity, "romance", romanceThread.Route, romanceThread.LastEventDay, day);
                if (effective > 0d) relationshipContext["romanceThread"] = new Dictionary<string, object>
                {
                    ["kind"] = "romance", ["route"] = romanceThread.Route, ["motiveChannel"] = romanceThread.MotiveChannel,
                    ["status"] = "active", ["intensity"] = effective, ["stage"] = StoryStage("romance", effective),
                    ["lastEventDay"] = romanceThread.LastEventDay
                };
            }
            return DialogueService.CalculateRomanticPosture("simulation:" + run.RunId, actorId, targetId, actor.Data, characteristics,
                relationshipContext,
                new Dictionary<string, object> { ["facets"] = spouseFacets }, opportunity, payload, "autonomous_relationship", "A private autonomous social opportunity.");
        }

        private static string ChooseRelationshipSimulationEventKind(RelationshipSimulationRun run, RelationshipSimulationEdge edge, Dictionary<string, object> postureA, Dictionary<string, object> postureB, int day)
        {
            double ambition = Math.Max(0d, (TraitFromProfile(run.Heroes[edge.A].Data, "ambition") + TraitFromProfile(run.Heroes[edge.B].Data, "ambition")
                + TraitFromProfile(run.Heroes[edge.A].Data, "pride") + TraitFromProfile(run.Heroes[edge.B].Data, "pride")) / 8d);
            double favorMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"favor_debt",day);
            double secretMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"shared_secret",day);
            double rivalryMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"rivalry",day);
            double maritalMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"marital_conflict",day);
            double romanceMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"romance",day);
            List<KeyValuePair<string, double>> eligible = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("practical_favor", (24d + 14d * Math.Max(0d, edge.AmbientAffinity) + 8d * Math.Min(1d, edge.AmbientExposure / 30d))*favorMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"favors","bargaining")),
                new KeyValuePair<string, double>("shared_confidence", (18d + 24d * Math.Max(0d, edge.AmbientAffinity) + 12d * Math.Min(1d, edge.AmbientExposure / 30d))*secretMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"secrets","information"))
            };
            if (edge.SameClan || edge.Family) eligible.Add(new KeyValuePair<string, double>("family_duty_cooperation", (20d + 18d * Math.Max(0d, edge.AmbientAffinity))*CourtService.CourtCharacterEventWeight(postureA,postureB,"protection","favors")));
            if (edge.Spouses) eligible.Add(new KeyValuePair<string, double>("private_spousal_confidence", (18d + 22d * Math.Max(0d, edge.AmbientAffinity))*CourtService.CourtCharacterEventWeight(postureA,postureB,"secrets","mediation")));
            RelationshipSimulationRelation ab = EnsureRelationshipSimulationRelation(run, edge.A, edge.B);
            RelationshipSimulationRelation ba = EnsureRelationshipSimulationRelation(run, edge.B, edge.A);
            double rivalryThread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"rivalry",day,false);
            double maritalThread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"marital_conflict",day,false);
            double romanceThread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"romance",day,true);
            double competition = Math.Max(Math.Max(ab.Facets["rivalry"], ba.Facets["rivalry"]),
                Math.Max(Math.Max(ab.Facets["envy"], ba.Facets["envy"]), Math.Max(ab.Facets["resentment"], ba.Facets["resentment"]))) / 100d;
            if (edge.AmbientTension >= 0.025d || ambition >= 0.25d || competition >= 0.10d || rivalryThread>0d)
                eligible.Add(new KeyValuePair<string, double>(rivalryThread>=50d?"political_obstruction":"political_rivalry_argument",
                    (10d + 45d * Math.Max(edge.AmbientTension, competition) + 20d * ambition)*rivalryMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"rivalry","threats","schemes")));
            if(edge.Spouses&&(edge.AmbientTension>=0.025d||competition>=0.10d||maritalThread>0d))
                eligible.Add(new KeyValuePair<string,double>("marital_argument",(8d+35d*Math.Max(edge.AmbientTension,competition))*maritalMultiplier));
            if (edge.Spouses && (maritalThread>=60d || ab.Facets["resentment"] >= 25d || ba.Facets["resentment"] >= 25d || edge.NativeRelation <= -15
                || ((ab.Facets["resentment"] >= 15d || ba.Facets["resentment"] >= 15d) && (ab.Facets["affection"] <= 0d || ba.Facets["affection"] <= 0d))))
                eligible.Add(new KeyValuePair<string, double>("marital_separation", ((maritalThread>=80d?45d:20d) + 45d * Math.Max(0d, edge.AmbientTension))*maritalMultiplier));
            bool flirt = edge.CoLocated && DirectorRomanceEligible(postureA, 40d) && DirectorRomanceEligible(postureB, 40d);
            if (flirt) eligible.Add(new KeyValuePair<string, double>("mutual_romantic_flirtation", (16d + 12d * Math.Min(1d, edge.AmbientExposure / 30d))*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction")));
            bool confidence=DirectorRomanceEligible(postureA,40d)&&DirectorRomanceEligible(postureB,40d);
            if(confidence&&romanceThread>=25d)eligible.Add(new KeyValuePair<string,double>("romantic_confidence",(14d+10d*Math.Min(1d,edge.AmbientExposure/30d))*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction","secrets")));
            string spouseA=ReadString(run.Heroes[edge.A].Data,"spouseId",""),spouseB=ReadString(run.Heroes[edge.B].Data,"spouseId","");
            bool outsideMarriage=(!string.IsNullOrWhiteSpace(spouseA)&&!spouseA.Equals(edge.B,StringComparison.OrdinalIgnoreCase))
                ||(!string.IsNullOrWhiteSpace(spouseB)&&!spouseB.Equals(edge.A,StringComparison.OrdinalIgnoreCase));
            bool bothUnmarried=string.IsNullOrWhiteSpace(spouseA)&&string.IsNullOrWhiteSpace(spouseB);
            if(StoryPhysicalRomanceEligible(edge.CoLocated,bothUnmarried,romanceThread,50d,postureA,postureB))eligible.Add(new KeyValuePair<string,double>("romantic_intimacy",(12d+8d*Math.Min(1d,edge.AmbientExposure/30d))*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"strategic_seduction","flirtation")));
            if (StoryPhysicalRomanceEligible(edge.CoLocated,outsideMarriage,romanceThread,75d,postureA,postureB)) eligible.Add(new KeyValuePair<string, double>("secret_affair_intimacy", (10d + 8d * Math.Min(1d, edge.AmbientExposure / 30d))*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"strategic_seduction")));
            return SelectWeightedPassiveEvent(eligible, run.Seed.ToString(CultureInfo.InvariantCulture) + "|" + edge.PairKey + "|" + Math.Floor(day / DirectorRunIntervalDays));
        }

        private static void ApplyRelationshipSimulationEventDirection(RelationshipSimulationRun run, string subjectId, string targetId, string eventType, string summary,
            Dictionary<string, object> posture,double preEventRomanceIntensity)
        {
            RelationshipSimulationRelation relation = EnsureRelationshipSimulationRelation(run, subjectId, targetId);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["motiveDecision"] = new Dictionary<string, object> { ["romance"] = posture },
                ["preEventRomanceIntensity"] = preEventRomanceIntensity
            };
            Dictionary<string, object> evaluation = DeterministicRelationshipEvaluation(eventType, summary, payload,
                relation.Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase));
            string impact = ReadString(evaluation, "impactLevel", "ordinary").ToLowerInvariant();
            int maxDelta = impact == "transformative" ? 60 : impact == "major" ? 30 : 10;
            Dictionary<string, object> raw = ReadDictionary(evaluation, "facetDeltas") ?? new Dictionary<string, object>();
            foreach (string facet in raw.Keys.Where(x => RelationshipFacetKeys.Contains(x, StringComparer.OrdinalIgnoreCase) && Math.Abs(ReadDouble(raw, x, 0d)) > 0.001d).Take(8))
                relation.Facets[facet] = ClampRelationship(relation.Facets[facet] + DialogueService.ClampDouble(ReadDouble(raw, facet, 0d), -maxDelta, maxDelta));
            int nativeDelta = ReadInt(evaluation, "nativeRelationDelta", 0);
            if (nativeDelta != 0)
            {
                string pair = AmbientPairKey(subjectId, targetId);
                int prior;
                run.NativeRelations.TryGetValue(pair, out prior);
                run.NativeRelations[pair] = Clamp(prior + Clamp(nativeDelta, -2, 2), -100, 100);
            }
        }

        private static void RunRelationshipSimulationMarriageSystem(RelationshipSimulationRun run, int day, Dictionary<string, RelationshipSimulationEdge> present)
        {
            foreach (RelationshipSimulationEdge edge in present.Values)
            {
                if (edge.Spouses || edge.Family) continue;
                RelationshipSimulationHero a = run.Heroes[edge.A], b = run.Heroes[edge.B];
                if (!RelationshipSimulationMarriageEligible(a, b)) continue;
                if (RomanticMarriageReady(RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(run, edge.A, edge.B)),
                    RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(run, edge.B, edge.A))))
                    MarryRelationshipSimulationHeroes(run, a, b, "romantic",day);
            }

            int period = (int)Math.Floor(day / 7d);
            foreach (Dictionary<string, object> clan in run.Clans.Values.OrderBy(x => ReadString(x, "clanId", ""), StringComparer.OrdinalIgnoreCase))
            {
                string clanId = ReadString(clan, "clanId", ""), leaderId = ReadString(clan, "leaderId", "");
                string rollKey = period + "|" + leaderId;
                if (string.IsNullOrWhiteSpace(leaderId) || run.MarriageRolls.Contains(rollKey) || !run.Heroes.ContainsKey(leaderId)) continue;
                run.MarriageRolls.Add(rollKey);
                RelationshipSimulationHero leader = run.Heroes[leaderId];
                List<RelationshipSimulationHero> members = run.Heroes.Values.Where(x => ReadString(x.Data, "clanId", "").Equals(clanId, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ReadString(x.Data, "spouseId", ""))).ToList();
                double urgency = MarriageUrgency(clan, leader.Data, members.Select(x => x.Data).ToList());
                double chance = DialogueService.ClampDouble(0.01d + 0.04d * urgency, 0.01d, 0.05d);
                if (members.Count == 0 || StableUnit(run.Seed.ToString(CultureInfo.InvariantCulture) + "|marriage|" + period + "|" + leaderId) >= chance) continue;
                RelationshipSimulationHero best = null, other = null;
                Dictionary<string, object> bestOtherClan = null;
                double bestScore = double.MinValue;
                foreach (RelationshipSimulationHero member in members)
                foreach (RelationshipSimulationHero candidate in run.Heroes.Values.Where(x => !ReadString(x.Data, "clanId", "").Equals(clanId, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ReadString(x.Data, "spouseId", ""))))
                {
                    if (!RelationshipSimulationMarriageEligible(member, candidate)) continue;
                    Dictionary<string, object> candidateClan;
                    if (!run.Clans.TryGetValue(ReadString(candidate.Data, "clanId", ""), out candidateClan)) continue;
                    double ownApproval=DynasticMarriageScore(clan,candidateClan,leader.Data,member.Data,candidate.Data)*RelationshipSimulationArrangedCourtshipMultiplier(run,member.Id,candidate.Id,day);
                    if(ownApproval<25)continue;
                    var counterpart=run.Heroes.TryGetValue(ReadString(candidateClan,"leaderId",""),out var cl)?cl.Data:new Dictionary<string,object>();
                    double otherApproval=DynasticMarriageScore(candidateClan,clan,counterpart,candidate.Data,member.Data)*RelationshipSimulationArrangedCourtshipMultiplier(run,member.Id,candidate.Id,day);
                    int nativeYear=ReadInt(member.Data,"nativeYear",-1);
                    bool royalA=ReadBool(member.Data,"isRulingClanMember",false),royalB=ReadBool(candidate.Data,"isRulingClanMember",false);
                    if(nativeYear<0&&((royalA&&ReadInt(candidateClan,"fiefCount",0)==0)||(royalB&&ReadInt(clan,"fiefCount",0)==0)))continue;
                    if(!Reign.Relationships.ArrangedMarriagePolicy.RoyalAllowed(run.CampaignId,member.Id,royalA,ReadInt(candidateClan,"fiefCount",0),nativeYear)
                        ||!Reign.Relationships.ArrangedMarriagePolicy.RoyalAllowed(run.CampaignId,candidate.Id,royalB,ReadInt(clan,"fiefCount",0),nativeYear))continue;
                    double score=Reign.Relationships.ArrangedMarriagePolicy.Rank(ownApproval,otherApproval,ReadInt(clan,"tier",0),ReadInt(candidateClan,"tier",0),ReadInt(clan,"fiefCount",0),ReadInt(candidateClan,"fiefCount",0));
                    if (score > bestScore) { bestScore = score; best = member; other = candidate; bestOtherClan = candidateClan; }
                }
                if (best != null && other != null && bestOtherClan != null && bestScore >= 25d)
                {
                    RelationshipSimulationHero otherLeader;
                    double otherApproval = run.Heroes.TryGetValue(ReadString(bestOtherClan, "leaderId", ""), out otherLeader)
                        ? DynasticMarriageScore(bestOtherClan, clan, otherLeader.Data, other.Data, best.Data)
                            *RelationshipSimulationArrangedCourtshipMultiplier(run,best.Id,other.Id,day) : 0d;
                    int fee=Reign.Relationships.ArrangedMarriagePolicy.Fee(ReadInt(clan,"tier",0),ReadInt(bestOtherClan,"tier",0));
                    var payer=ReadInt(clan,"tier",0)<ReadInt(bestOtherClan,"tier",0)?clan:bestOtherClan;
                    var payee=ReferenceEquals(payer,clan)?bestOtherClan:clan;
                    if(otherApproval>=25 && ReadDouble(payer,"gold",0)>=fee)
                    {
                        payer["gold"]=ReadDouble(payer,"gold",0)-fee;payee["gold"]=ReadDouble(payee,"gold",0)+fee;
                        MarryRelationshipSimulationHeroes(run,best,other,"arranged",day);
                    }
                }
            }
        }

        private static double RelationshipSimulationThirdPartyCourtshipMultiplier(RelationshipSimulationRun run,string heroId,string proposedSpouseId,int day)
        {
            double strongest=run.StoryThreads.Values.Where(x=>x.SubjectId.Equals(heroId,StringComparison.OrdinalIgnoreCase)
                    &&x.Kind=="romance"&&x.Route=="courtship"&&!x.TargetId.Equals(proposedSpouseId,StringComparison.OrdinalIgnoreCase))
                .Select(x=>RelationshipSimulationPairStoryIntensity(run,heroId,x.TargetId,"romance",day,true)).DefaultIfEmpty(0d).Max();
            return strongest>=75d?0.50d:strongest>=50d?0.75d:1d;
        }

        private static double RelationshipSimulationArrangedCourtshipMultiplier(RelationshipSimulationRun run,string a,string b,int day)
        {
            return Math.Min(RelationshipSimulationThirdPartyCourtshipMultiplier(run,a,b,day),
                RelationshipSimulationThirdPartyCourtshipMultiplier(run,b,a,day));
        }

        private static int ConvertRelationshipSimulationDisplacedCourtships(RelationshipSimulationRun run,string newlywed,string spouse,int day)
        {
            List<string> partners=run.StoryThreads.Values.Where(x=>x.SubjectId.Equals(newlywed,StringComparison.OrdinalIgnoreCase)
                    &&x.Kind=="romance"&&x.Route=="courtship"&&!x.TargetId.Equals(spouse,StringComparison.OrdinalIgnoreCase))
                .Select(x=>x.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int converted=0;
            foreach(string partner in partners)
            {
                if(RelationshipSimulationPairStoryIntensity(run,newlywed,partner,"romance",day,true)<50d)continue;
                RelationshipSimulationStoryThread a=RelationshipSimulationStory(run,newlywed,partner,"romance",day,false);
                RelationshipSimulationStoryThread b=RelationshipSimulationStory(run,partner,newlywed,"romance",day,false);
                if(a!=null){a.Route="temptation";a.LastEventType="arranged_marriage_displacement";a.LastEventDay=day;}
                if(b!=null){b.Route="temptation";b.LastEventType="arranged_marriage_displacement";b.LastEventDay=day;}
                converted++;
            }
            return converted;
        }

        private static void MarryRelationshipSimulationHeroes(RelationshipSimulationRun run, RelationshipSimulationHero a, RelationshipSimulationHero b, string route,int day)
        {
            if (!RelationshipSimulationMarriageEligible(a, b)) return;
            a.Data["spouseId"] = b.Id;
            b.Data["spouseId"] = a.Id;
            run.EventCounts["marriages"] = ReadSimulationCount(run.EventCounts, "marriages") + 1;
            run.EventCounts[route + "_marriages"] = ReadSimulationCount(run.EventCounts, route + "_marriages") + 1;
            EnsureRelationshipSimulationRelation(run, a.Id, b.Id);
            EnsureRelationshipSimulationRelation(run, b.Id, a.Id);
            if(route=="arranged")
            {
                int displaced=ConvertRelationshipSimulationDisplacedCourtships(run,a.Id,b.Id,day)
                    +ConvertRelationshipSimulationDisplacedCourtships(run,b.Id,a.Id,day);
                run.CourtshipDisplacements+=displaced;
                if(displaced>0)run.EventCounts["courtship_displacements"]=ReadSimulationCount(run.EventCounts,"courtship_displacements")+displaced;
            }
        }

        private static void HandleRelationshipSimulationSeparation(RelationshipSimulationRun run, RelationshipSimulationEdge edge, int day)
        {
            if (!edge.Spouses) return;
            double conflict=Math.Max(60d,RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"marital_conflict",day,false));
            RelationshipSimulationReadiness readiness;
            if (!run.DivorceReadiness.TryGetValue(edge.PairKey, out readiness))
            {
                readiness = new RelationshipSimulationReadiness { FirstDay = day, Score = conflict };
                run.DivorceReadiness[edge.PairKey] = readiness;
            }
            else readiness.Score = conflict;
            if (day - readiness.FirstDay < 30 || readiness.Score < 80d || readiness.Triggered) return;
            run.Heroes[edge.A].Data["spouseId"] = "";
            run.Heroes[edge.B].Data["spouseId"] = "";
            readiness.Triggered = true;
            run.EventCounts["divorces"] = ReadSimulationCount(run.EventCounts, "divorces") + 1;
        }

        private static void TryRelationshipSimulationAffairConception(RelationshipSimulationRun run, string aId, string bId, int day)
        {
            RelationshipSimulationHero a = run.Heroes[aId], b = run.Heroes[bId];
            RelationshipSimulationHero mother = ReadBool(a.Data, "isFemale", false) && !ReadBool(b.Data, "isFemale", false) ? a
                : ReadBool(b.Data, "isFemale", false) && !ReadBool(a.Data, "isFemale", false) ? b : null;
            RelationshipSimulationHero father = mother == a ? b : mother == b ? a : null;
            if (mother == null || father == null || ReadBool(mother.Data, "isPregnant", false)) return;
            double age = ReadDouble(mother.Data, "age", 0d);
            if (age < 18d || age > 45d) return;
            double chance = NpcConceptionChance(age,
                ReadInt(mother.Data, "childrenCount", 0));
            int affairIndex = ReadSimulationCount(run.EventCounts, "secret_affair_intimacy");
            if (StableUnit(run.Seed.ToString(CultureInfo.InvariantCulture) + "|affair_conception|" + day + "|" + mother.Id + "|" + father.Id + "|" + affairIndex) < chance)
            {
                mother.Data["isPregnant"] = true;
                run.EventCounts["affair_conceptions"] = ReadSimulationCount(run.EventCounts, "affair_conceptions") + 1;
            }
        }

        private static bool RelationshipSimulationMarriageEligible(RelationshipSimulationHero a, RelationshipSimulationHero b)
        {
            if (a == null || b == null || a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) return false;
            if (!ReadBool(a.Data, "isAlive", true) || !ReadBool(b.Data, "isAlive", true) || ReadDouble(a.Data, "age", 0d) < 18d || ReadDouble(b.Data, "age", 0d) < 18d) return false;
            if (ReadBool(a.Data, "isFemale", false) == ReadBool(b.Data, "isFemale", false)) return false;
            if (!string.IsNullOrWhiteSpace(ReadString(a.Data, "spouseId", "")) || !string.IsNullOrWhiteSpace(ReadString(b.Data, "spouseId", ""))) return false;
            return !RelationshipSimulationCloseKin(a, b);
        }

        private static bool RelationshipSimulationCloseKin(RelationshipSimulationHero a, RelationshipSimulationHero b)
        {
            if (ReadString(a.Data, "fatherId", "").Equals(b.Id, StringComparison.OrdinalIgnoreCase) || ReadString(a.Data, "motherId", "").Equals(b.Id, StringComparison.OrdinalIgnoreCase)
                || ReadString(b.Data, "fatherId", "").Equals(a.Id, StringComparison.OrdinalIgnoreCase) || ReadString(b.Data, "motherId", "").Equals(a.Id, StringComparison.OrdinalIgnoreCase)) return true;
            string af = ReadString(a.Data, "fatherId", ""), bf = ReadString(b.Data, "fatherId", ""), am = ReadString(a.Data, "motherId", ""), bm = ReadString(b.Data, "motherId", "");
            return (!string.IsNullOrWhiteSpace(af) && af.Equals(bf, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(am) && am.Equals(bm, StringComparison.OrdinalIgnoreCase));
        }

        private static bool RelationshipSimulationFamily(RelationshipSimulationHero a, RelationshipSimulationHero b)
        {
            return ReadString(a.Data, "spouseId", "").Equals(b.Id, StringComparison.OrdinalIgnoreCase)
                || ReadString(b.Data, "spouseId", "").Equals(a.Id, StringComparison.OrdinalIgnoreCase)
                || RelationshipSimulationCloseKin(a, b);
        }

        private static bool SameSimulationValue(Dictionary<string, object> a, Dictionary<string, object> b, string key)
        {
            string value = ReadString(a, key, "");
            return !string.IsNullOrWhiteSpace(value) && value.Equals(ReadString(b, key, ""), StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> RelationshipSimulationFacetObject(RelationshipSimulationRelation relation)
        {
            return relation.Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static int ReadSimulationCount(Dictionary<string, int> counts, string key)
        {
            int value;
            return counts.TryGetValue(key, out value) ? value : 0;
        }
    }
}
