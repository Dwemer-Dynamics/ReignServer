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
        private static bool IsRelationshipNativeTargetId(string id)
        {
            return !string.IsNullOrWhiteSpace(id)
                && id.StartsWith("native_target:", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> ReportRelationshipNativeTarget(
            ReignDbConnection connection, Dictionary<string, object> payload)
        {
            string id = ReadFirstString(payload, "directorActionId", "director_action_id");
            string pairKey = id.Substring("native_target:".Length);
            string status = ActionPipelineService.NormalizeLookup(ReadString(payload, "status", "completed"));
            string error = ReadString(payload, "error", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> target = RelationshipsStore.SelectRelationshipNativeTargets8(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            if (target == null)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["directorActionId"] = id,
                    ["status"] = "already_resolved", ["idempotent"] = true
                };
            }

            if (status == "completed")
            {
                int applied = ReadInt(target, "target_relation", 0);
                RelationshipsStore.UpdateRelationshipPairChemistry12(connection, new Dictionary<string, object> { ["day"] = day, ["ts"] = ts, ["pair"] = pairKey });
                RelationshipsStore.DeleteRelationshipNativeTargets2(connection, new Dictionary<string, object> { ["pair"] = pairKey });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["directorActionId"] = id,
                    ["status"] = "completed", ["appliedNativeRelation"] = applied
                };
            }

            if (status == "obsolete" || status == "skipped")
            {
                RelationshipsStore.UpdateRelationshipPairChemistry13(connection, new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
                RelationshipsStore.DeleteRelationshipNativeTargets2(connection, new Dictionary<string, object> { ["pair"] = pairKey });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["directorActionId"] = id,
                    ["status"] = "obsolete", ["reason"] = error
                };
            }

            RelationshipsStore.UpdateRelationshipNativeTargets5(connection, new Dictionary<string, object>
                {
                    ["error"] = FirstNonEmpty(error, "Native relationship application failed."),
                    ["ts"] = ts, ["pair"] = pairKey
                });
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["directorActionId"] = id,
                ["status"] = "failed", ["error"] = error
            };
        }

        private static bool ApplyRelationshipCommitmentAffinityDelta(
            ReignDbConnection connection, string campaignId, string pairKey,
            int delta, double worldDay)
        {
            Dictionary<string, object> pair = RelationshipsStore.SelectRelationshipPairChemistry8(connection, new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault();
            if (pair == null) return false;
            int affinityAB = Clamp(ReadInt(pair, "affinity_a_to_b", 0)
                + delta, -100, 100);
            int affinityBA = Clamp(ReadInt(pair, "affinity_b_to_a", 0)
                + delta, -100, 100);
            int socialAB = ReadInt(pair, "social_modifier_a_to_b", 0);
            int socialBA = ReadInt(pair, "social_modifier_b_to_a", 0);
            int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
            int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
            string heroA = ReadString(pair, "hero_a_id", "");
            string heroB = ReadString(pair, "hero_b_id", "");
            int projected = ProjectNativeRelation(connection, heroA, heroB,
                affinityAB, affinityBA, effectiveAB, effectiveBA);
            EnsureRelationshipNativeTargetSchema(connection);
            Dictionary<string, object> nativeTarget = RelationshipsStore.SelectRelationshipNativeTargets(connection, new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault();
            int observed = nativeTarget != null
                ? ReadInt(nativeTarget, "observed_relation", 0)
                : ReadInt(pair, "projected_native_relation", 0);
            bool observationRequired = RelationshipNativeObservationRequired(pair, nativeTarget);
            bool pending = projected != observed || observationRequired;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            RelationshipsStore.UpdateRelationshipPairChemistry14(connection, new Dictionary<string, object>
                {
                    ["ab"] = affinityAB, ["ba"] = affinityBA,
                    ["effectiveAB"] = effectiveAB,
                    ["effectiveBA"] = effectiveBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "a_to_b", effectiveAB, effectiveBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "b_to_a", effectiveBA, effectiveAB),
                    ["projected"] = projected, ["pending"] = pending ? 1 : 0,
                    ["action"] = pending ? "native_target:" + pairKey : "",
                    ["ts"] = ts, ["pair"] = pairKey
                });
            if (pending)
                RelationshipsStore.UpsertRelationshipNativeTargets2(connection, new Dictionary<string, object>
                {
                    ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                    ["target"] = projected, ["observed"] = observed,
                    ["requiresObservation"] = observationRequired ? 1 : 0,
                    ["day"] = worldDay, ["ts"] = ts
                });
            else
                RelationshipsStore.DeleteRelationshipNativeTargets2(connection, new Dictionary<string, object> { ["pair"] = pairKey });
            return true;
        }

        private static void ResolvePregnancyCommitment(
            ReignDbConnection connection, string campaignId,
            Dictionary<string, object> actionPayload, string actionStatus,
            double worldDay)
        {
            string conceptionId = ReadString(actionPayload, "conceptionId", "");
            Dictionary<string, object> commitment = RelationshipsStore.SelectRelationshipConceptionCommitments(connection, new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            if (commitment == null) return;
            if (!ReadString(commitment, "status", "")
                .Equals("awaiting_conception",
                    StringComparison.OrdinalIgnoreCase)) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (actionStatus != "completed")
            {
                RelationshipsStore.UpdateRelationshipConceptionCommitments(connection, new Dictionary<string, object> { ["ts"] = ts, ["id"] = conceptionId });
                return;
            }
            string pairKey = ReadString(commitment, "pair_key", "");
            string heroA = ReadString(commitment, "hero_a_id", "");
            string heroB = ReadString(commitment, "hero_b_id", "");
            bool bothPassed = ReadInt(commitment, "passed_a", 0) == 1
                && ReadInt(commitment, "passed_b", 0) == 1;
            AmbientPairContext pair = new AmbientPairContext
            {
                PairKey = pairKey, HeroAId = heroA, HeroBId = heroB
            };
            if (bothPassed)
            {
                string marriageAction = ReadString(commitment,
                    "marriage_action_id", "");
                if (string.IsNullOrWhiteSpace(marriageAction))
                    marriageAction = QueueDirectorAction(connection,
                        "marriage", worldDay, heroA, heroB,
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["pairKey"] = pairKey,
                            ["route"] = "pregnancy_commitment",
                            ["conceptionId"] = conceptionId,
                            ["timelineId"] = ReadString(actionPayload,
                                "timelineId", "main"), ["silent"] = true
                        });
                RelationshipsStore.UpdateRelationshipConceptionCommitments2(connection, new Dictionary<string, object>
                    {
                        ["status"] = string.IsNullOrWhiteSpace(marriageAction)
                            ? "marriage_reservation_blocked" : "awaiting_marriage",
                        ["action"] = marriageAction, ["ts"] = ts,
                        ["id"] = conceptionId
                    });
                string existingMarriageStatus = ReadString(RelationshipsStore.SelectRelationshipDirectorActions5(connection, new Dictionary<string, object>
                    {
                        ["id"] = marriageAction
                    }).FirstOrDefault(), "status", "");
                if (existingMarriageStatus == "completed")
                    CompletePregnancyCommitmentMarriage(connection,
                        campaignId, conceptionId, worldDay);
                else if (!string.IsNullOrWhiteSpace(existingMarriageStatus)
                    && existingMarriageStatus != "pending"
                    && existingMarriageStatus != "claimed")
                    RelationshipsStore.UpdateRelationshipConceptionCommitments3(connection, new Dictionary<string, object>
                        {
                            ["ts"] = ts, ["id"] = conceptionId
                        });
                RecordRelationshipIncident(connection, pair,
                    "pregnancy_commitment_accepted", worldDay,
                    "Both unmarried lovers passed their Honor commitment rolls and agreed to marry.",
                    new Dictionary<string, object>
                    {
                        ["conceptionId"] = conceptionId,
                        ["marriageActionId"] = marriageAction
                    });
                return;
            }
            string canceledMarriageAction = ReadString(commitment,
                "marriage_action_id", "");
            if (!string.IsNullOrWhiteSpace(canceledMarriageAction))
            {
                Dictionary<string, object> cancellationResult =
                    new Dictionary<string, object>
                    {
                        ["status"] = "invalid",
                        ["error"] = "The unmarried pregnancy commitment failed before marriage could proceed.",
                        ["source"] = "pregnancy_commitment",
                        ["conceptionId"] = conceptionId,
                        ["worldDay"] = worldDay
                    };
                RelationshipsStore.UpdateRelationshipDirectorActions4(connection, new Dictionary<string, object>
                    {
                        ["ts"] = ts,
                        ["result"] = Json.Serialize(cancellationResult),
                        ["id"] = canceledMarriageAction
                    });
                RelationshipsStore.DeleteRelationshipMarriageReservations2(connection, new Dictionary<string, object>
                    { ["id"] = canceledMarriageAction });
            }
            ApplyRelationshipCommitmentAffinityDelta(connection, campaignId,
                pairKey, PregnancyCommitmentBreakupAffinityPenalty, worldDay);
            RelationshipsStore.UpdateRelationshipPairLifecycle(connection, new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
            Dictionary<string, object> conception = RelationshipsStore.SelectConceptions(connection, new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            Dictionary<string, object> conceptionPayload = TryParseJsonObject(
                ReadString(conception, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            conceptionPayload["isIllegitimate"] = true;
            conceptionPayload["unmarriedCommitmentPending"] = false;
            conceptionPayload["unmarriedCommitmentOutcome"] = "breakup";
            RelationshipsStore.UpdateConceptions(connection, new Dictionary<string, object>
                {
                    ["payload"] = Json.Serialize(conceptionPayload),
                    ["ts"] = ts, ["id"] = conceptionId
                });
            RelationshipsStore.UpdateRelationshipConceptionCommitments4(connection, new Dictionary<string, object>
                {
                    ["delta"] = PregnancyCommitmentBreakupAffinityPenalty,
                    ["ts"] = ts, ["id"] = conceptionId
                });
            RecordRelationshipIncident(connection, pair,
                "pregnancy_commitment_failed", worldDay,
                "At least one unmarried lover failed the Honor commitment roll; the lovers separated.",
                new Dictionary<string, object>
                {
                    ["conceptionId"] = conceptionId,
                    ["affinityDelta"] = PregnancyCommitmentBreakupAffinityPenalty
                });
        }

        private static void CompletePregnancyCommitmentMarriage(
            ReignDbConnection connection, string campaignId,
            string conceptionId, double worldDay)
        {
            Dictionary<string, object> commitment = RelationshipsStore.SelectRelationshipConceptionCommitments2(connection, new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            if (commitment == null || ReadString(commitment, "status", "")
                != "awaiting_marriage") return;
            string pairKey = ReadString(commitment, "pair_key", "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ApplyRelationshipCommitmentAffinityDelta(connection, campaignId,
                pairKey, PregnancyCommitmentMarriageAffinityBonus, worldDay);
            RelationshipsStore.UpdateRelationshipConceptionCommitments5(connection, new Dictionary<string, object>
                {
                    ["delta"] = PregnancyCommitmentMarriageAffinityBonus,
                    ["ts"] = ts, ["id"] = conceptionId
                });
            Dictionary<string, object> conceptionRow = RelationshipsStore.SelectConceptions(connection, new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            Dictionary<string, object> completedPayload = TryParseJsonObject(
                ReadString(conceptionRow, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            completedPayload["unmarriedCommitmentPending"] = false;
            completedPayload["unmarriedCommitmentOutcome"] = "married";
            RelationshipsStore.UpdateConceptions2(connection, new Dictionary<string, object>
                {
                    ["payload"] = Json.Serialize(completedPayload),
                    ["ts"] = ts, ["id"] = conceptionId
                });
        }

        private static void AdvanceAffairMarriageCommitment(
            ReignDbConnection connection, string campaignId,
            Dictionary<string, object> action, string actionStatus,
            double worldDay)
        {
            string actionId = ReadString(action, "director_action_id", "");
			Dictionary<string, object> actionPayload = TryParseJsonObject(
				ReadString(action, "payload_json", "{}"))
				?? new Dictionary<string, object>();
			string timelineId = FirstNonEmpty(ReadString(actionPayload,
				"timelineId", ""), "main");
            Dictionary<string, object> commitment = RelationshipsStore.SelectRelationshipAffairCommitments(connection, new Dictionary<string, object> { ["id"] = actionId })
                .FirstOrDefault();
            if (commitment == null) return;
            if (!ReadString(commitment, "status", "")
                .Equals("awaiting_divorce",
                    StringComparison.OrdinalIgnoreCase)) return;
            string pairKey = ReadString(commitment, "pair_key", "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool isDivorceA = actionId.Equals(ReadString(commitment,
                "divorce_action_a_id", ""),
                StringComparison.OrdinalIgnoreCase);
            bool isDivorceB = actionId.Equals(ReadString(commitment,
                "divorce_action_b_id", ""),
                StringComparison.OrdinalIgnoreCase);
            if ((isDivorceA && ReadInt(commitment,
                    "divorce_a_completed", 0) == 1)
                || (isDivorceB && ReadInt(commitment,
                    "divorce_b_completed", 0) == 1)) return;
            if (actionStatus != "completed")
            {
                RelationshipsStore.UpdateRelationshipAffairCommitments(connection, new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
                return;
            }
            if (isDivorceA)
            {
                RelationshipsStore.UpdateRelationshipAffairCommitments2(connection, new Dictionary<string, object>
                    {
                        ["ts"] = ts, ["pair"] = pairKey
                    });
                commitment["divorce_a_completed"] = 1;
            }
            if (isDivorceB)
            {
                RelationshipsStore.UpdateRelationshipAffairCommitments3(connection, new Dictionary<string, object>
                    {
                        ["ts"] = ts, ["pair"] = pairKey
                    });
                commitment["divorce_b_completed"] = 1;
            }
            string divorcedHero = ReadString(action, "actor_id", "");
            string formerSpouse = ReadString(action, "target_id", "");
            string divorcedPairKey = AmbientPairKey(divorcedHero,
                formerSpouse);
            AmbientPairContext divorcedPair = new AmbientPairContext
            {
                PairKey = divorcedPairKey, HeroAId = divorcedHero,
                HeroBId = formerSpouse
            };
            Dictionary<string, object> rumor = ReputationService.RegisterSocialOccurrence(
                connection, campaignId, ReputationService.BuildRelationshipSocialOccurrence(
                    divorcedPair, (int)Math.Floor(worldDay), "divorce",
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = divorcedHero,
                        ["name"] = divorcedHero
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = formerSpouse,
                        ["name"] = formerSpouse
                    }, formerSpouse, divorcedHero, true, true,
					"The marriage ended when one spouse chose an affair partner.",
					timelineId));
            RelationshipsStore.UpdateRelationshipPairLifecycle2(connection, new Dictionary<string, object>
                {
                    ["day"] = worldDay,
                    ["rumor"] = ReadString(rumor, "occurrenceId", ""),
                    ["ts"] = ts, ["pair"] = divorcedPairKey
                });
            RelationshipsStore.UpdateRelationshipPairChemistry15(connection, new Dictionary<string, object>
                {
                    ["ts"] = ts, ["pair"] = divorcedPairKey
                });
            RecordRelationshipIncident(connection, divorcedPair, "divorce",
                worldDay,
                "The marriage ended when one spouse chose an affair partner.",
                new Dictionary<string, object>
                {
                    ["directorActionId"] = actionId,
                    ["affairPairKey"] = pairKey
                }, ReadString(rumor, "occurrenceId", ""));
			FinalizeDivorceProjections(connection, campaignId, timelineId,
				divorcedPair, divorcedHero, formerSpouse, worldDay, actionId,
				"The marriage ended when one spouse chose an affair partner.");
            bool divorceAComplete = string.IsNullOrWhiteSpace(ReadString(
                    commitment, "divorce_action_a_id", ""))
                || ReadInt(commitment, "divorce_a_completed", 0) == 1;
            bool divorceBComplete = string.IsNullOrWhiteSpace(ReadString(
                    commitment, "divorce_action_b_id", ""))
                || ReadInt(commitment, "divorce_b_completed", 0) == 1;
            if (!divorceAComplete || !divorceBComplete)
                return;
            string heroA = ReadString(commitment, "hero_a_id", "");
            string heroB = ReadString(commitment, "hero_b_id", "");
            string marriageAction = QueueDirectorAction(connection, "marriage",
                worldDay, heroA, heroB, new Dictionary<string, object>
                {
                    ["source"] = "mbti_relationship_lifecycle",
                    ["pairKey"] = pairKey,
                    ["route"] = "affair_commitment",
                    ["silent"] = true
                });
            RelationshipsStore.UpdateRelationshipAffairCommitments4(connection, new Dictionary<string, object>
                {
                    ["status"] = string.IsNullOrWhiteSpace(marriageAction)
                        ? "marriage_reservation_blocked" : "awaiting_marriage",
                    ["action"] = marriageAction, ["ts"] = ts,
                    ["pair"] = pairKey
                });
        }

        internal static Dictionary<string, object> RelationshipDirectorReportActionApi(Dictionary<string, object> payload)
            => RelationshipDirectorReportAction(payload, null);

        private static Dictionary<string, object> RelationshipDirectorReportAction(
            Dictionary<string, object> payload, Dictionary<string, object> owner)
        {
            bool typed = owner != null;
            string campaignId = typed ? (string)owner["campaignId"] : ReadString(payload, "campaignId", "default");
            string id = typed ? (string)payload["directorActionId"] : ReadFirstString(payload, "directorActionId", "director_action_id");
            string status = typed ? (string)payload["status"] : ReadString(payload, "status", "completed");
            bool nativeTarget = IsRelationshipNativeTargetId(id);
            if (typed && nativeTarget) return RelationshipDirectorOwnerError();
            // A completed divorce can ingest world history under relationship ownership.
            using var timelineScope = typed || status == "completed" && !nativeTarget
                ? WorldSimulationService.EnterWorldHistoryTimelineScope(campaignId) : null;
            using var ownership = PersistenceService.EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaignId));
            if (nativeTarget)
            {
                using (ReignDbConnection targetConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureRelationshipDirectorSchema(targetConnection);
                    return ReportRelationshipNativeTarget(targetConnection, payload);
                }
            }
            using(ReignDbConnection connection = typed
                ? ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaignId, trackCampaignMutations: true)
                : ReignDatabase.OpenCampaignConnection(campaignId))
            {
                string ownerTimeline = typed ? (string)owner["timelineId"] : "";
                // Keep the timeline scope across admission and the legacy writer.
                // Its World History and memory projections may open their own SQL connections.
                if (typed && (connection == null
                    || !WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, null, owner, campaignId, ownerTimeline)
                    || !WorldSimulationService.TableExists(connection, "relationship_director_actions")))
                    return RelationshipDirectorOwnerError();
                if (!typed) EnsureRelationshipDirectorSchema(connection);
                var action = RelationshipsStore.SelectRelationshipDirectorActions6(connection, TestDict("id", id)).FirstOrDefault();
                string actionJson = ReadString(action, "payload_json", "{}");
                var actionPayload = typed ? TryParseStrictJsonObject(actionJson)
                    : TryParseJsonObject(actionJson) ?? new Dictionary<string, object>();
                if (typed && (action == null || !RelationshipDirectorTimelineMatches(actionPayload, ownerTimeline)))
                    return RelationshipDirectorOwnerError();
                if (typed) EnsureRelationshipDirectorSchema(connection);
                action ??= new Dictionary<string, object>();
                bool arrangedReport=ReadString(actionPayload,"route","")=="arranged";
                string completedMarriageEvaluation="";
                using(var reportTransaction=arrangedReport?connection.BeginTransaction():null)
                {

                if(ReadString(action,"status","")=="completed") return new Dictionary<string,object>{{"ok",true},{"idempotent",true}};
                string actionType = ReadString(action, "action_type", "");
                string expectedTimeline = ReadString(actionPayload, "timelineId", "main");
                if ((status == "wedding_pending" || actionType == "player_marriage_offer" || ReadString(action, "status", "") == "wedding_pending")
                    && ReadString(payload, "timelineId", "main") != expectedTimeline)
                    return TestDict("ok", false, "error", "The wedding receipt belongs to another timeline.");
                if (status == "wedding_pending" && actionType == "marriage"
                    || status == "wedding_offer_pending" && actionType == "player_marriage_offer")
                {
                    RelationshipsStore.UpdateRelationshipDirectorActions5(connection, TestDict("id", id, "status", status, "result", Json.Serialize(payload)));
                    if (status == "wedding_pending")
                        RelationshipsStore.UpdateMarriageEvaluations2(connection, TestDict("id", ReadString(actionPayload, "evaluationId", "")));
                    reportTransaction?.Commit();
                    return TestDict("ok", true);
                }
                if (actionType == "player_marriage_offer")
                {
                    if (status == "completed" && (!ReadBool(payload, "marriageVerified", false) || !ReadBool(payload, "paymentVerified", false)))
                        return TestDict("ok", false, "error", "Verified native marriage and consideration are required.");
                    string outcome = status == "completed" ? "executed" : status == "player_refused" ? "rejected_by_player" : status;
                    RelationshipsStore.UpdateMarriageEvaluations3(connection, TestDict("id", ReadString(actionPayload, "evaluationId", ""), "status", outcome));
                }
                if(status=="deferred") { RelationshipsStore.UpdateRelationshipDirectorActions6(connection, new Dictionary<string,object>{{"id",id}}); reportTransaction?.Commit(); return new Dictionary<string,object>{{"ok",true}}; }
                if(status=="completed"&&ReadString(actionPayload,"route","")=="arranged"
                    &&(!ReadBool(payload,"marriageVerified",false)||!ReadBool(payload,"paymentVerified",false)))
                    return new Dictionary<string,object>{{"ok",false},{"error","Verified native marriage and payment required."}};
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                RelationshipsStore.UpdateRelationshipDirectorActions7(connection, new Dictionary<string, object>{{"status",status},{"ts",ts},{"result",Json.Serialize(payload)},{"id",id}});
                if(status!="recovery"&&ReadString(action,"action_type","").Equals("marriage",StringComparison.OrdinalIgnoreCase))
                    RelationshipsStore.DeleteRelationshipMarriageReservations2(connection, new Dictionary<string, object>{{"id",id}});
                if(ReadString(action,"action_type","")=="divorce")
                {
                    string a=ReadString(action,"actor_id",""),b=ReadString(action,"target_id","");string lo=string.Compare(a,b,StringComparison.OrdinalIgnoreCase)<=0?a:b,hi=lo==a?b:a;
                    if(ReadString(actionPayload,"source","")=="mbti_relationship_lifecycle")
                    {
                        string pairKey=ReadString(actionPayload,"pairKey",AmbientPairKey(a,b));
                        if(status=="completed")
                        {
                            AmbientPairContext pair=new AmbientPairContext{PairKey=pairKey,HeroAId=a,HeroBId=b};
                            double divorceWorldDay=ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d));
							string divorceTimeline=FirstNonEmpty(ReadString(payload,"timelineId",""),
								ReadString(actionPayload,"timelineId",""),"main");
                            Dictionary<string, object> rumor=ReputationService.RegisterSocialOccurrence(connection,campaignId,
                                ReputationService.BuildRelationshipSocialOccurrence(pair,(int)Math.Floor(divorceWorldDay),"divorce",
                                    new Dictionary<string, object>{{"heroStringId",a},{"name",a}},
                                    new Dictionary<string, object>{{"heroStringId",b},{"name",b}},b,a,true,true,
									"The marriage ended in divorce.",divorceTimeline));
                            RelationshipsStore.UpdateRelationshipPairLifecycle3(connection, new Dictionary<string, object>{{"day",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d))},
                                    {"rumor",ReadString(rumor,"occurrenceId","")},{"ts",ts},{"pair",pairKey}});
                            RelationshipsStore.UpdateRelationshipPairChemistry16(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                            RecordRelationshipIncident(connection,pair,"divorce",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)),
                                "The marriage ended in divorce.",new Dictionary<string, object>{{"directorActionId",id}},ReadString(rumor,"occurrenceId",""));
							FinalizeDivorceProjections(connection,campaignId,divorceTimeline,
								pair,a,b,divorceWorldDay,id,"The marriage ended in divorce.");
                        }
                        else RelationshipsStore.UpdateRelationshipPairLifecycle4(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    }
                    else if(ReadString(actionPayload,"source","")
                        =="relationship_affair_commitment")
                        AdvanceAffairMarriageCommitment(connection, campaignId,
                            action, status, ReadDouble(payload,"worldDay",
                                ReadDouble(action,"world_day",0d)));
                }
                else if(ReadString(action,"action_type","")=="marriage")
                {
                    string evaluationId=ReadString(actionPayload,"evaluationId","");
                    string outcomeRoute=WorldSimulationService.WorldTestMarriageOutcomeRoute(
                        actionPayload);
                    string outcomeTimeline=ReadString(actionPayload,"timelineId","main");
                    if(!string.IsNullOrWhiteSpace(evaluationId))
                    {
                        Dictionary<string, object> evaluation=RelationshipsStore.SelectMarriageEvaluations7(connection, new Dictionary<string, object>{{"id",evaluationId}}).FirstOrDefault()
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> evaluationPayload=TryParseJsonObject(
                            ReadString(evaluation,"payload_json","{}"))??new Dictionary<string, object>();
                        outcomeRoute=ReadBool(evaluationPayload,"diplomaticPackageRequired",false)
                            ?"diplomatic":"arranged";
                        outcomeTimeline=ReadString(evaluation,"timeline_id",outcomeTimeline);
                        string evaluationStatus=status=="completed"?"executed":status=="recovery"?"recovery":status=="invalid"?"invalid_native":status=="obsolete"?"obsolete":"failed";
                        RelationshipsStore.UpdateMarriageEvaluations4(connection, new Dictionary<string, object>{{"status",evaluationStatus},{"ts",ts},{"id",evaluationId}});
                        if(status=="completed")
                        {
                            FinalizeMarriage(connection,campaignId,evaluationId,ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)),!arrangedReport);
                            if(arrangedReport)completedMarriageEvaluation=evaluationId;
                        }
                    }
                    int outcomeDay=(int)Math.Floor(ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)));
                    WorldSimulationService.RecordWorldTestCounter(connection,campaignId,outcomeTimeline,outcomeDay,"marriages",
                        "outcome_"+FirstNonEmpty(evaluationId,id),
                        WorldSimulationService.BuildWorldTestMarriageOutcomeCounters(outcomeRoute,status));
                    if(ReadString(actionPayload,"source","")=="mbti_relationship_lifecycle")
                    {
                        string a=ReadString(action,"actor_id",""),b=ReadString(action,"target_id",""),
                            pairKey=ReadString(actionPayload,"pairKey",AmbientPairKey(a,b));
                        if(status=="completed")
                        {
                            RelationshipsStore.UpdateRelationshipPairLifecycle5(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                            RelationshipsStore.UpdateRelationshipPairChemistry17(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                            AmbientPairContext pair=new AmbientPairContext{PairKey=pairKey,HeroAId=a,HeroBId=b};
                            string lifecycleMarriageRoute=ReadString(
                                actionPayload,"route","mutual_affinity");
                            bool pregnancyCommitmentRoute=lifecycleMarriageRoute
                                =="pregnancy_commitment";
                            RecordRelationshipIncident(connection,pair,"marriage",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)),
                                pregnancyCommitmentRoute
                                    ?"The pair married after jointly accepting responsibility for a conception."
                                    :"The pair married after both directional affinities reached seventy or above.",
                                new Dictionary<string, object>{{"directorActionId",id},{"route",lifecycleMarriageRoute}});
                            DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},
                                {"eventId","marriage_"+id},{"eventType","world_event"},{"eventSubtype",pregnancyCommitmentRoute?"pregnancy_commitment_marriage":"mutual_affinity_marriage"},
                                {"worldDay",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d))},
                                {"summary",a+" and "+b+" married."},{"participants",new[]{a,b}},
                                {"visibility","public"},{"importance",0.9d}},"relationship_lifecycle_marriage");
                        }
                        else if(status=="invalid")
                            RelationshipsStore.UpdateRelationshipPairLifecycle6(connection, new Dictionary<string, object>
                            {
                                {"reason",DialogueService.LimitText(FirstNonEmpty(ReadString(payload,"error",""),
                                    "Native marriage eligibility rejected this couple."),500)},
                                {"ts",ts},{"pair",pairKey}
                            });
                        else RelationshipsStore.UpdateRelationshipPairLifecycle7(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    }
                    string conceptionCommitmentId=FirstNonEmpty(
                        ReadString(actionPayload,"conceptionId",""),
                        ReadString(RelationshipsStore.SelectRelationshipConceptionCommitments3(connection, new Dictionary<string, object>{{"action",id}}).FirstOrDefault(),
                            "conception_id",""));
                    if(!string.IsNullOrWhiteSpace(conceptionCommitmentId))
                    {
                        string commitmentStatus=ReadString(RelationshipsStore.SelectRelationshipConceptionCommitments4(connection, new Dictionary<string, object>{{"id",conceptionCommitmentId}}).FirstOrDefault(),"status","");
                        if(commitmentStatus=="awaiting_marriage"&&status=="completed")
                            CompletePregnancyCommitmentMarriage(connection,
                                campaignId,conceptionCommitmentId,
                                ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)));
                        else if(commitmentStatus=="awaiting_marriage") RelationshipsStore.UpdateRelationshipConceptionCommitments6(connection, new Dictionary<string, object>{{"ts",ts},{"id",conceptionCommitmentId}});
                    }
                    string affairCommitmentPair=ReadString(actionPayload,"route","")
                        =="affair_commitment"?ReadString(actionPayload,"pairKey",""):"";
                    if(!string.IsNullOrWhiteSpace(affairCommitmentPair))
                        RelationshipsStore.UpdateRelationshipAffairCommitments5(connection, new Dictionary<string, object>{{"status",status=="completed"?"married":"marriage_failed"},{"ts",ts},{"pair",affairCommitmentPair}});
                }
                else if(ReadString(action,"action_type","")=="start_conception"
                    && (ReadString(actionPayload,"source","")=="mbti_relationship_lifecycle"
                        || ReadString(actionPayload,"source","")=="relationship_fling"))
                {
                    bool flingConception=ReadString(actionPayload,"source","")
                        =="relationship_fling";
                    string pairKey=ReadString(actionPayload,"pairKey","");
                    if(!flingConception)
                        RelationshipsStore.UpdateRelationshipPairLifecycle8(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    string conceptionStatus=status=="completed"?"confirmed_game":
                        status=="obsolete"?"obsolete_game":"failed_game";
                    RelationshipsStore.UpdateConceptions3(connection, new Dictionary<string, object>{{"status",conceptionStatus},{"ts",ts},
                            {"id",ReadString(actionPayload,"conceptionId","")}});
                    if(!flingConception)
                        ResolvePregnancyCommitment(connection, campaignId,
                            actionPayload, status, ReadDouble(payload,"worldDay",
                                ReadDouble(action,"world_day",0d)));
                }
                else if(ReadString(action,"action_type","")=="native_relation"
                    && (ReadString(actionPayload,"source","")=="ambient_relationship_drift"
                        || ReadString(actionPayload,"source","")=="mbti_relationship_chemistry"
                        || ReadString(actionPayload,"source","")=="relationship_affair_discovery"
                        || ReadString(actionPayload,"source","")=="relationship_correspondence"
                        || ReadString(actionPayload,"source","")=="relationship_interaction"))
                {
                    string pairKey=ReadString(actionPayload,"pairKey",AmbientPairKey(ReadString(action,"actor_id",""),ReadString(action,"target_id","")));
                    int delta=Clamp(ReadInt(actionPayload,"delta",0),-100,100);
                    if(status=="completed")
                    {
                        if(ReadString(actionPayload,"source","")=="ambient_relationship_drift"||ReadString(actionPayload,"source","")=="mbti_relationship_chemistry")
                            RelationshipsStore.UpdateRelationshipPairChemistry18(connection, new Dictionary<string, object>{{"day",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d))},{"ts",ts},{"pair",pairKey}});
                        if(ReadString(actionPayload,"source","")=="mbti_relationship_chemistry")
                            RelationshipsStore.DeleteRelationshipDirectorActions(connection, new Dictionary<string, object>{{"id",id}});
                    }
                    else if(ReadString(actionPayload,"source","")=="ambient_relationship_drift"||ReadString(actionPayload,"source","")=="mbti_relationship_chemistry")
                    {
                        RelationshipsStore.UpdateRelationshipPairChemistry19(connection, new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    }
                }
                reportTransaction?.Commit();
                if(completedMarriageEvaluation!="")StoreMarriageMemory(connection,campaignId,completedMarriageEvaluation,ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)));
                }
            }
            return new Dictionary<string, object>{{"ok",true},{"directorActionId",id},{"status",status}};
        }

		private static void FinalizeDivorceProjections(ReignDbConnection connection,
			string campaignId, string timelineId, AmbientPairContext pair,
			string heroA, string heroB, double worldDay, string actionId,
			string summary)
		{
			timelineId = string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId;
			long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			Dictionary<string, object> args = new Dictionary<string, object>
			{
				["campaign"] = campaignId, ["timeline"] = timelineId,
				["a"] = heroA, ["b"] = heroB, ["pair"] = pair.PairKey,
				["ts"] = ts
			};
			RelationshipsStore.UpdateCharacterReputations(connection, args);
			RelationshipsStore.UpdateRumorSubjectTags(connection, args);
			RelationshipsStore.UpdateRumorOccurrences(connection, args);
			ReputationService.ReconcileSocialRelationshipsForSubjects(connection, campaignId,
				timelineId, new[] { heroA, heroB }, worldDay);
			DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
			{
				["campaignId"] = campaignId,
				["timelineId"] = timelineId,
				["eventId"] = "divorce_" + actionId,
				["eventType"] = "world_event",
				["eventSubtype"] = "divorce",
				["worldDay"] = worldDay,
				["summary"] = summary,
				["participants"] = new[] { heroA, heroB },
				["visibility"] = "public",
				["importance"] = 0.85d
			}, "relationship_lifecycle_divorce");
			WorldSimulationService.WorldHistoryIngestBatchApi(new Dictionary<string, object>
			{
				["campaignId"] = campaignId,
				["timelineId"] = timelineId,
				["clientId"] = "reign_relationship_lifecycle",
				["historyCompleteFromWorldDay"] = worldDay,
				["events"] = new List<Dictionary<string, object>>
				{
					new Dictionary<string, object>
					{
						["eventId"] = "divorce_" + actionId,
						["sequence"] = 0L,
						["worldDay"] = worldDay,
						["eventType"] = "divorce",
						["category"] = "relationship",
						["phase"] = "completed",
						["disseminationClass"] = "major_world",
						["summary"] = summary,
						["source"] = "relationship_lifecycle_divorce",
						["entities"] = new List<Dictionary<string, object>>
						{
							new Dictionary<string, object>{{"entityId",heroA},{"entityType","hero"},{"role","former_spouse"}},
							new Dictionary<string, object>{{"entityId",heroB},{"entityType","hero"},{"role","former_spouse"}}
						}
					}
				}
			});
		}

        private static Dictionary<string, object> ReportOwnedRelationshipDirectorActions(Dictionary<string, object> payload)
        {
            var owner = ReadRelationshipDirectorOwner(payload);
            if (owner == null) return RelationshipDirectorOwnerError();
            string campaign = (string)owner["campaignId"];
            string timeline = (string)owner["timelineId"];
            if (!payload.TryGetValue("receipts", out object raw) || !(raw is IList receiptValues)
                || receiptValues.Count == 0 || receiptValues.Count > 500)
                return RelationshipDirectorOwnerError();
            var receipts = ReadDictionaryList(payload, "receipts");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (receipts.Count != receiptValues.Count || receipts.Any(receipt =>
                !receipt.TryGetValue("directorActionId", out object idValue) || !(idValue is string id)
                || string.IsNullOrWhiteSpace(id) || !ids.Add(id) || IsRelationshipNativeTargetId(id)
                || !receipt.TryGetValue("timelineId", out object timelineValue)
                || !(timelineValue is string receiptTimeline) || receiptTimeline != timeline
                || !receipt.TryGetValue("status", out object statusValue)
                || !(statusValue is string status) || string.IsNullOrWhiteSpace(status)))
                return RelationshipDirectorOwnerError();

            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            // Check once before echoing an owner and again at each row admission.
            using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
            {
                if (connection == null || !WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, null, owner, campaign, timeline))
                    return RelationshipDirectorOwnerError();
            }

            var results = new List<Dictionary<string, object>>(receipts.Count);
            foreach (var receipt in receipts)
            {
                var single = new Dictionary<string, object>(receipt, StringComparer.OrdinalIgnoreCase)
                    { ["campaignId"] = campaign };
                var result = RelationshipDirectorReportAction(single, owner);
                result["directorActionId"] = receipt["directorActionId"];
                results.Add(result);
            }
            return TestDict("ok", results.All(result => ReadBool(result, "ok", false)),
                "ownership", owner, "reportedCount", results.Count, "results", results);
        }

        internal static Dictionary<string, object> RelationshipDirectorReportActionsApi(Dictionary<string, object> payload)
        {
            if (payload?.ContainsKey("ownership") == true) return ReportOwnedRelationshipDirectorActions(payload);
            string campaignId = ReadString(payload, "campaignId", "default");
            List<Dictionary<string, object>> receipts = ReadDictionaryList(payload, "receipts").Take(500).ToList();
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> nativeTargets = receipts
                .Where(x => IsRelationshipNativeTargetId(
                    ReadFirstString(x, "directorActionId", "director_action_id"))).ToList();
            if (nativeTargets.Count > 0)
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    EnsureRelationshipDirectorSchema(connection);
                    foreach (Dictionary<string, object> receipt in nativeTargets)
                        results.Add(ReportRelationshipNativeTarget(connection, receipt));
                    transaction.Commit();
                }
            }
            foreach (Dictionary<string, object> receipt in receipts.Except(nativeTargets))
            {
                Dictionary<string, object> single =
                    new Dictionary<string, object>(receipt, StringComparer.OrdinalIgnoreCase)
                    {
                        ["campaignId"] = campaignId
                    };
                results.Add(RelationshipDirectorReportActionApi(single));
            }
            return new Dictionary<string, object>
            {
                ["ok"] = results.All(x => ReadBool(x, "ok", false)),
                ["reportedCount"] = results.Count,
                ["results"] = results
            };
        }

        internal static void FinalizeMarriage(ReignDbConnection connection,string campaignId,string evaluationId,double day,bool storeMemory=true)
        {
            Dictionary<string, object> row=RelationshipsStore.SelectMarriageEvaluations8(connection, new Dictionary<string, object>{{"id",evaluationId}}).FirstOrDefault();
            if(row==null)return;
            string a=ReadString(row,"hero_a_id",""),b=ReadString(row,"hero_b_id",""),route=ReadString(row,"route","");
            string eventId="marriage_"+evaluationId;
            if(route=="arranged")
            {
                ApplyMarriageLeaderEffect(connection,campaignId,ReadString(row,"timeline_id","main"),evaluationId,ReadString(row,"leader_a_id",""),ReadString(row,"leader_b_id",""),20,day);
                ApplyMarriageLeaderEffect(connection,campaignId,ReadString(row,"timeline_id","main"),evaluationId,ReadString(row,"leader_b_id",""),ReadString(row,"leader_a_id",""),20,day);
            }
            if(route=="arranged")
            {
                ApplyCompelledMarriageConsequence(connection,campaignId,ReadString(row,"timeline_id","main"),eventId,a,b,ReadString(row,"leader_a_id",""),ReadDouble(row,"resentment_a",0d),day);
                ApplyCompelledMarriageConsequence(connection,campaignId,ReadString(row,"timeline_id","main"),eventId,b,a,ReadString(row,"leader_b_id",""),ReadDouble(row,"resentment_b",0d),day);
            }
            if(storeMemory)StoreMarriageMemory(connection,campaignId,evaluationId,day);
        }

        internal static void StoreMarriageMemory(ReignDbConnection connection,string campaignId,string evaluationId,double day)
        {
            var row=RelationshipsStore.SelectMarriageEvaluations9(connection, new Dictionary<string,object>{{"id",evaluationId}}).FirstOrDefault();
            if(row==null)return;
            string a=ReadString(row,"hero_a_id",""),b=ReadString(row,"hero_b_id",""),route=ReadString(row,"route",""),eventId="marriage_"+evaluationId;
            DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},{"eventId",eventId},{"eventType","world_event"},{"eventSubtype",route=="arranged"?"arranged_marriage":"romantic_marriage"},{"worldDay",day},{"summary",a+" and "+b+" married"+(route=="arranged"?" by agreement of their clan leaders.":" after developing mutual trust, affection, and attraction.")},{"participants",new[]{a,b,ReadString(row,"leader_a_id",""),ReadString(row,"leader_b_id","")}},{"visibility","public"},{"importance",0.9d}},"relationship_director_marriage");
        }

        private static void ApplyCompelledMarriageConsequence(ReignDbConnection connection,string campaignId,string timelineId,string eventId,string spouse,string match,string leader,double resentment,double day)
        {
            if(resentment<=0d)return;
            long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int spouseDelta=-Clamp((int)Math.Round(Math.Max(1d,resentment/10d),MidpointRounding.AwayFromZero),1,6);
            ApplyAuthoritativeRelationshipDelta(connection,campaignId,spouse,match,spouseDelta,day,
                "compelled_political_marriage",timelineId);
            if(!string.IsNullOrWhiteSpace(leader)&&leader!=spouse)
            {
                int leaderDelta=-Clamp((int)Math.Round(Math.Max(1d,resentment/8d),MidpointRounding.AwayFromZero),1,8);
                ApplyAuthoritativeRelationshipDelta(connection,campaignId,spouse,leader,leaderDelta,day,
                    "compelled_political_marriage",timelineId);
            }
            string text=resentment>=40d?"They feel deeply wronged that their clan leader compelled this marriage and expect the grievance to shape their future.":resentment>=20d?"They resent having no choice in the arranged marriage, even while recognizing its dynastic purpose.":"They accept the clan leader's authority but retain private unease about the arranged marriage.";
            RelationshipsStore.InsertOrIgnoreComprehension(connection, new Dictionary<string, object>{{"id","comprehension_"+evaluationIdSafe(eventId,spouse)},{"event",eventId},{"owner",spouse},{"text",text},{"known",Json.Serialize(new[]{spouse})},{"about",Json.Serialize(new[]{match,leader})},{"ts",ts},{"payload",Json.Serialize(new Dictionary<string, object>{{"resentment",resentment},{"compelledMarriage",true}})}});
        }

        private static string evaluationIdSafe(string eventId,string owner)
        {
            return Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(eventId+"|"+owner))).Replace("/","_").Replace("+","-").TrimEnd('=').Substring(0,24);
        }

        internal static Dictionary<string, object> RelationshipDirectorStatusApi(Dictionary<string, object> payload)
        {
            string campaignId=ReadString(payload,"campaignId","default"),timelineId=ReadString(payload,"timelineId","main");using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaignId)){EnsureRelationshipDirectorSchema(connection);return new Dictionary<string, object>{{"ok",true},{"relationshipModel","directional_mbti_native_relation"},{"passiveMechanicalEventsEnabled",false},{"relationshipLlmCalls",0},{"directorPolicy","political_marriage_only"},{"lastRun",RelationshipsStore.SelectRelationshipDirectorRuns(connection).FirstOrDefault()??new Dictionary<string, object>()},{"pendingActions",ReadInt(RelationshipsStore.CountRelationshipDirectorActions3(connection).FirstOrDefault(),"count",0)},{"marriageEvaluations",RelationshipsStore.SelectMarriageEvaluations10(connection, new Dictionary<string, object>{{"timeline",timelineId}})},{"marriageRolls",RelationshipsStore.SelectMarriageLeaderRolls4(connection, new Dictionary<string, object>{{"timeline",timelineId}})},{"ambient",MbtiRelationshipStatusApi(payload)}};}
        }

        internal static bool IsExplicitConceptionCapablePlayerText(string text)
        {
            string value=(text??"").ToLowerInvariant();
            bool completed=DialogueService.ContainsAny(value,"we made love","i made love","slept together","lay together","finished inside","came inside","inseminat");
            bool conceive=DialogueService.ContainsAny(value,"try for a child","tried for a child","attempted to have a child","make a child","conceive","get you pregnant","become pregnant");
            return (completed&&conceive)||DialogueService.ContainsAny(value,"finished inside","came inside","inseminat");
        }

        private static double PlayerConceptionChance(double age){if(age<18d||age>45d)return 0d;return (15d-(age-18d)*(12d/27d))/100d;}
        internal static string NormalizePregnancyChoiceToken(string value){return (value??"").Trim().ToLowerInvariant().Replace("-","_").Replace(" ","_");}
        internal static double StableUnit(string seed){using(SHA256 sha=SHA256.Create()){byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(seed??""));ulong value=BitConverter.ToUInt64(hash,0);return value/(double)ulong.MaxValue;}}
        private static double TraitFromProfile(Dictionary<string, object> profile,string key){Dictionary<string, object> direct=ReadDictionary(profile,"foundationTraits")??ReadDictionary(ReadDictionary(profile,"traits"),"foundationTraits")??ReadDictionary(profile,"traits")??new Dictionary<string, object>();return ReadDouble(direct,key,0d);}
        private static double Trait01(Dictionary<string, object> profile,string key){return DialogueService.ClampDouble((TraitFromProfile(profile,key)+2d)/4d,0d,1d);}
    }
}
