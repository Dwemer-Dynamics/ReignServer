using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TerminalActionReportProtocol = Reign.Core.Contracts.Actions.TerminalActionReportProtocol;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static List<Dictionary<string, object>> RunConversationRelationshipSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, evidence) => rows.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "conversation_relationships", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["evidence"] = evidence, ["durationMs"] = 0
            });
            string previousCampaignsRoot = Program.CampaignsRootOverride.Value;
            string isolatedCampaignsRoot = Path.Combine(
                string.IsNullOrWhiteSpace(previousCampaignsRoot) ? Path.Combine(Program.TestsDir, "isolated-campaigns") : previousCampaignsRoot,
                "conversation_relationships_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Program.CampaignsRootOverride.Value = isolatedCampaignsRoot;
            string campaign = "cr_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                Func<string, string, int, string, double, string, Dictionary<string, object>> assessment = (observer, target, affinity, tier, confidence, valence) =>
                    new Dictionary<string, object> { ["observerHeroStringId"] = observer, ["targetHeroStringId"] = target,
                        ["severityTier"] = tier, ["actKind"] = tier, ["confidence"] = confidence, ["importance"] = 0.5d,
                        ["valence"] = valence, ["sourceTurnIds"] = new List<string> { "turn" } };
                Func<string, int, Dictionary<string, object>, Dictionary<string, object>> run = (exchange, native, item) => ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = exchange, ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { ReadString(item, "observerHeroStringId", ""), ReadString(item, "targetHeroStringId", ""), ReadString(item, "giftRecipientHeroStringId", ""), "player" },
                    ["assessments"] = new List<Dictionary<string, object>> { item },
                    ["relationshipPairs"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["subjectId"] = ReadString(item, "observerHeroStringId", ""), ["targetId"] = ReadString(item, "targetHeroStringId", ""), ["nativeRelation"] = native } }
                });
                Dictionary<string, object> routine = run("routine", 0, assessment("npc_a", "player", 0, "routine", 0.7d, "positive"));
                Dictionary<string, object> repeat = run("routine", 0, assessment("npc_a", "player", 0, "routine", 0.7d, "positive"));
                add("routine_plus_one_and_idempotent", ReadInt(ReadDictionaryList(routine, "receipts").FirstOrDefault(), "finalDelta", 0) == 1
                    && ReadBool(ReadDictionaryList(repeat, "receipts").FirstOrDefault(), "idempotent", false),
                    "Routine reactions move one point and duplicate exchanges do not apply twice.", new { routine, repeat });
                string routineReceiptId = ReadString(ReadDictionaryList(routine, "receipts").FirstOrDefault(), "receiptId", "");
                Dictionary<string, object> nativeAck = ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["receiptIds"] = new List<string> { routineReceiptId },
                    ["nativeReceipt"] = new Dictionary<string, object> { ["nativeRelation"] = 1, ["test"] = true }
                });
                Dictionary<string, object> ackedRepeat = run("routine", 1, assessment("npc_a", "player", 0, "routine", 0.7d, "positive"));
                add("native_application_receipt_prevents_replay", ReadBool(nativeAck, "ok", false)
                    && ReadDictionaryList(ackedRepeat, "nativeChanges").Count == 0
                    && ReadString(ReadDictionaryList(ackedRepeat, "receipts").FirstOrDefault(), "nativeApplicationStatus", "") == "applied",
                    "A confirmed native personal-relation change is retained and never emitted again.", new { nativeAck, ackedRepeat });
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureConversationRelationshipSchema(connection);
                    ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET affinity_a_to_b=10 WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = AmbientPairKey("npc_a", "player") });
                }
                Dictionary<string, object> band = run("band", 10, assessment("npc_a", "player", 10, "routine", 0.7d, "positive"));
                add("routine_band_blocks_outward_growth", ReadInt(ReadDictionaryList(band, "receipts").FirstOrDefault(), "finalDelta", 99) == 0,
                    "Routine positive contact cannot move outward beyond +10.", band);
                Dictionary<string, object> inward = run("band_inward", 10, assessment("npc_a", "player", 10, "routine", 0.7d, "negative"));
                add("routine_can_move_extreme_inward", ReadInt(ReadDictionaryList(inward, "receipts").FirstOrDefault(), "finalDelta", 0) == -1,
                    "Routine reactions may move an extreme relationship back toward the familiarity band.", inward);
                Dictionary<string, object> hostileAssessment = assessment("npc_b", "player", 0, "hostile", 0.8d, "negative");
                hostileAssessment["sourceText"] = "You are a worthless coward, and I despise you.";
                hostileAssessment["currentConductQuote"] = "worthless coward";
                Dictionary<string, object> hostile = run("hostile", 0, hostileAssessment);
                add("hostile_tier_is_five", ReadInt(ReadDictionaryList(hostile, "receipts").FirstOrDefault(), "finalDelta", 0) == -5,
                    "Hostile speech uses the five-point anchor.", hostile);
                Dictionary<string, object> reconciledHostile = run("hostile", -5, hostileAssessment);
                add("lost_native_ack_is_reconciled", ReadDictionaryList(reconciledHostile, "nativeChanges").Count == 0,
                    "If the native change succeeded but its acknowledgement was lost, the current personal relation reconciles the receipt without duplicating it.", reconciledHostile);
                Dictionary<string, object> impatienceAssessment = assessment("npc_impatient", "player", 0, "hostile", 0.9d, "negative");
                impatienceAssessment["sourceText"] = "I want to test whether you distinguish proof from suspicion.";
                impatienceAssessment["summary"] = "The player repeated a test, wasted time, and exhausted the observer's patience.";
                Dictionary<string, object> impatience = run("routine_impatience_not_hostility", 0, impatienceAssessment);
                add("observer_impatience_cannot_inflate_target_conduct",
                    ReadString(ReadDictionaryList(impatience, "receipts").FirstOrDefault(), "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(impatience, "receipts").FirstOrDefault(), "finalDelta", 0) == -1,
                    "Repetition, evasiveness, boredom, and accumulated observer impatience remain routine unless the target's actual source line contains hostile conduct.", impatience);
                Dictionary<string, object> quotedHarmlessHostility = assessment(
                    "npc_quoted_harmless", "player", 0, "hostile", 0.9d, "negative");
                quotedHarmlessHostility["sourceText"] =
                    "Speak to one another about how your established shared past affects this ordinary disagreement.";
                quotedHarmlessHostility["currentConductQuote"] =
                    "Speak to one another about how your established shared past affects this ordinary disagreement.";
                quotedHarmlessHostility["summary"] =
                    "The player repeated the same prompt and exhausted the observer's patience.";
                Dictionary<string, object> quotedHarmlessResult = run(
                    "quoted_harmless_prompt_not_hostility", 0, quotedHarmlessHostility);
                Dictionary<string, object> quotedHarmlessReceipt =
                    ReadDictionaryList(quotedHarmlessResult, "receipts").FirstOrDefault();
                add("exact_but_harmless_quote_cannot_trigger_hostile_tier",
                    ReadString(quotedHarmlessReceipt, "severityTier", "") == "routine"
                    && ReadInt(quotedHarmlessReceipt, "finalDelta", 0) == -1
                    && ReadStringList(quotedHarmlessReceipt, "modifiers").Contains(
                        "hostile_tier_downgraded_without_direct_hostile_speech",
                        StringComparer.OrdinalIgnoreCase),
                    "An exact quotation proves the current wording but cannot turn a harmless repeated instruction into five-point hostile speech.",
                    quotedHarmlessResult);
                Dictionary<string, object> staleGrievance = assessment("npc_stale_grievance", "player", 0, "meaningful", 0.9d, "negative");
                staleGrievance["sourceText"] = "Let us discuss how our established shared past affects this ordinary disagreement.";
                staleGrievance["summary"] = "The player previously deflected an ultimatum and exploited the observer's patience.";
                Dictionary<string, object> staleGrievanceResult = run("stale_grievance_not_reapplied", 0, staleGrievance);
                Dictionary<string, object> staleGrievanceReceipt = ReadDictionaryList(staleGrievanceResult, "receipts").FirstOrDefault();
                add("historical_grievance_cannot_be_reapplied_as_new_meaningful_conduct",
                    ReadString(staleGrievanceReceipt, "severityTier", "") == "routine"
                    && ReadInt(staleGrievanceReceipt, "finalDelta", 0) == -1
                    && ReadStringList(staleGrievanceReceipt, "modifiers").Contains(
                        "higher_tier_downgraded_without_current_conduct_quote", StringComparer.OrdinalIgnoreCase),
                    "Old grievances may determine a routine reaction's direction but cannot create a fresh higher-tier penalty.", staleGrievanceResult);
                Dictionary<string, object> meaningfulCurrent = assessment("npc_meaningful", "player", 0, "meaningful", 0.75d, "positive");
                meaningfulCurrent["sourceText"] = "I will stand beside you at the hearing and risk my position to support your claim.";
                meaningfulCurrent["currentConductQuote"] = "risk my position to support your claim";
                Dictionary<string, object> meaningfulCurrentResult = run("meaningful_current_conduct", 0, meaningfulCurrent);
                add("exact_current_conduct_quote_preserves_meaningful_tier",
                    ReadString(ReadDictionaryList(meaningfulCurrentResult, "receipts").FirstOrDefault(), "severityTier", "") == "meaningful"
                    && ReadInt(ReadDictionaryList(meaningfulCurrentResult, "receipts").FirstOrDefault(), "finalDelta", 0) >= 2,
                    "A non-routine judgment remains meaningful when it cites exact wording from the target's current contribution.", meaningfulCurrentResult);
                Dictionary<string, object> inventedQuote = assessment("npc_invented_quote", "player", 0, "meaningful", 0.8d, "negative");
                inventedQuote["sourceText"] = "I disagree with your estimate, but I am willing to hear your reasoning.";
                inventedQuote["currentConductQuote"] = "I will destroy your standing";
                Dictionary<string, object> inventedQuoteResult = run("invented_current_conduct_quote", 0, inventedQuote);
                add("fabricated_current_conduct_quote_is_rejected",
                    ReadString(ReadDictionaryList(inventedQuoteResult, "receipts").FirstOrDefault(), "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(inventedQuoteResult, "receipts").FirstOrDefault(), "finalDelta", 0) == -1,
                    "A provider cannot justify a higher tier by inventing wording absent from the target's actual contribution.", inventedQuoteResult);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('gift_evidence',$campaign,$ts,10,'verified_gift','A valuable home was transferred.',
'[""player"",""npc_b""]','[""player"",""npc_b""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('dialogue_gift_claim',$campaign,$ts,10,'dialogue_memory','The player said a fine silver cup was a gift.',
'[""player"",""npc_c""]','[""player"",""npc_c""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('dialogue_betrayal_claim',$campaign,$ts,10,'dialogue_memory','The player claimed to have betrayed the observer.',
'[""player"",""npc_dialogue_betrayal""]','[""player"",""npc_dialogue_betrayal""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('betrayal_evidence',$campaign,$ts,10,'verified_betrayal','A completed action receipt proves the ally sold the observer out to an enemy.',
'[""player"",""npc_c""]','[""player"",""npc_c""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                }
                Dictionary<string, object> acceptedGiftParsed = new Dictionary<string, object>
                {
                    ["reply"] = "I will take it, though I remain wary.",
                    ["intent"] = "Accept the silver cup without promising a favor.",
                    ["actionGate"] = new Dictionary<string, object> { ["intent"] = "The recipient accepts the silver cup as a physical handover." },
                    ["relationshipAssessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetHeroStringId"] = "player", ["valence"] = "negative", ["actKind"] = "routine_conversation",
                            ["severityTier"] = "routine", ["confidence"] = 0.75d, ["giftRecipientHeroStringId"] = "npc_gift_recipient",
                            ["summary"] = "The recipient is suspicious of the giver's motive."
                        }
                    }
                };
                Dictionary<string, object> acceptedGiftPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["playerHeroStringId"] = "player",
                    ["playerText"] = "I am giving you this silver cup as a sincere gift, freely and with no favor expected.",
                    ["participants"] = new List<string> { "player", "npc_gift_recipient" }
                };
                List<Dictionary<string, object>> acceptedGiftAssessments = NormalizeConversationRelationshipAssessments(
                    acceptedGiftParsed, acceptedGiftPayload, "npc_gift_recipient", "suspicious", "player", "accepted_gift_turn");
                Dictionary<string, object> acceptedGift = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "accepted_modest_gift", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_gift_recipient" },
                    ["assessments"] = acceptedGiftAssessments,
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_gift_recipient", ["targetId"] = "player", ["nativeRelation"] = 0 }
                    }
                });
                Dictionary<string, object> acceptedGiftReceipt = ReadDictionaryList(acceptedGift, "receipts").FirstOrDefault();
                add("accepted_modest_gift_rewards_only_recipient_and_creates_lineage",
                    ReadString(acceptedGiftReceipt, "severityTier", "") == "meaningful"
                    && ReadInt(acceptedGiftReceipt, "finalDelta", 0) >= 2
                    && !string.IsNullOrWhiteSpace(ReadString(acceptedGiftReceipt, "benefitEventId", "")),
                    "An explicitly accepted material gift produces a modest recipient benefit and durable lineage even when the recipient remains wary.", acceptedGift);
                Dictionary<string, object> overstatedAcceptedGiftParsed = new Dictionary<string, object>
                {
                    ["reply"] = "I take the carved cup with both hands. Thank you. I mean that.",
                    ["intent"] = "Accept the valuable carved cup with sincere gratitude.",
                    ["relationshipAssessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetHeroStringId"] = "player", ["valence"] = "positive",
                            ["actKind"] = "life_changing_gift", ["severityTier"] = "transformative",
                            ["confidence"] = 0.82d, ["giftRecipientHeroStringId"] = "npc_gift_recipient",
                            ["currentConductQuote"] = "I am giving you this valuable carved cup as a sincere gift, freely and with no favor expected.",
                            ["summary"] = "The recipient sincerely accepted the valuable carved cup."
                        }
                    }
                };
                Dictionary<string, object> overstatedAcceptedGiftPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["playerHeroStringId"] = "player",
                    ["playerText"] = "I am giving you this valuable carved cup as a sincere gift, freely and with no favor expected.",
                    ["participants"] = new List<string> { "player", "npc_gift_recipient" }
                };
                List<Dictionary<string, object>> overstatedAcceptedGiftAssessments = NormalizeConversationRelationshipAssessments(
                    overstatedAcceptedGiftParsed, overstatedAcceptedGiftPayload, "npc_gift_recipient", "indebted", "player",
                    "overstated_accepted_gift_turn");
                Dictionary<string, object> overstatedAcceptedGift = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "overstated_accepted_modest_gift", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_gift_recipient" },
                    ["assessments"] = overstatedAcceptedGiftAssessments,
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_gift_recipient", ["targetId"] = "player", ["nativeRelation"] = 0 }
                    }
                });
                Dictionary<string, object> overstatedAcceptedGiftReceipt = ReadDictionaryList(overstatedAcceptedGift, "receipts").FirstOrDefault();
                add("overstated_accepted_modest_gift_keeps_meaningful_lineage",
                    ReadString(overstatedAcceptedGiftReceipt, "severityTier", "") == "meaningful"
                    && ReadString(overstatedAcceptedGiftReceipt, "actKind", "") == "accepted_material_gift"
                    && ReadInt(overstatedAcceptedGiftReceipt, "finalDelta", 0) >= 2
                    && !string.IsNullOrWhiteSpace(ReadString(overstatedAcceptedGiftReceipt, "benefitEventId", "")),
                    "A model-overstated accepted ordinary gift loses transformative magnitude but keeps its recipient-specific meaningful benefit lineage.",
                    overstatedAcceptedGift);
                Dictionary<string, object> transformative = run("gift", 0, new Dictionary<string, object>(assessment("npc_b", "player", 0, "transformative", 0.7d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["importance"] = 0.8d, ["benefitEventId"] = "home_gift",
                        ["giftRecipientHeroStringId"] = "npc_b", ["evidenceSourceIds"] = new List<string> { "gift_evidence" },
                        ["sourceText"] = "I transfer the deed to this valuable home to you now as a life-changing gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                Dictionary<string, object> transformativeReceipt = ReadDictionaryList(transformative, "receipts").FirstOrDefault();
                add("transformative_gift_creates_benefit", ReadInt(transformativeReceipt, "finalDelta", 0) >= 12
                    && !string.IsNullOrWhiteSpace(ReadString(transformativeReceipt, "benefitEventId", "")),
                    "Transformative gifts use the high positive tier and create durable lineage.", transformative);
                Dictionary<string, object> recoveredTransformative = run("recovered_verified_gift", 0,
                    new Dictionary<string, object>(assessment("npc_b", "player", 0, "transformative", 0.8d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["importance"] = 0.8d,
                        ["giftRecipientHeroStringId"] = "npc_b",
                        ["sourceText"] = "I transfer the deed to this valuable home to you now as a life-changing gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                Dictionary<string, object> recoveredTransformativeReceipt =
                    ReadDictionaryList(recoveredTransformative, "receipts").FirstOrDefault();
                add("recent_pair_matched_benefit_evidence_is_recovered",
                    ReadString(recoveredTransformativeReceipt, "severityTier", "") == "transformative"
                    && ReadInt(recoveredTransformativeReceipt, "finalDelta", 0) >= 12
                    && ReadStringList(recoveredTransformativeReceipt, "evidenceSourceIds")
                        .Contains("gift_evidence", StringComparer.OrdinalIgnoreCase)
                    && ReadStringList(recoveredTransformativeReceipt, "modifiers")
                        .Contains("recovered_recent_verified_benefit_gift_evidence",
                            StringComparer.OrdinalIgnoreCase),
                    "A verified recent transfer for the exact giver-recipient pair remains usable when the model omits its source ID.",
                    recoveredTransformative);
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaign))
                {
                    int repeatedLineageCount = ReadInt(
                        ReignDatabase.QuerySql(
                            connection,
                            @"SELECT COUNT(*) AS count
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id='player'
AND recipient_id='npc_b'
AND payload_json LIKE '%gift_evidence%';",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaign
                            }).FirstOrDefault(),
                        "count",
                        0);
                    add(
                        "repeated_verified_gift_reuses_one_benefit_lineage",
                        repeatedLineageCount == 1,
                        "Recognizing the same independently verified gift again must reuse its original giver-recipient benefit rather than create a duplicate.",
                        repeatedLineageCount);
                }
                Dictionary<string, object> unrelatedTransformative = run("unrelated_verified_gift", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "transformative", 0.9d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift",
                        ["giftRecipientHeroStringId"] = "npc_c",
                        ["evidenceSourceIds"] = new List<string> { "gift_evidence" },
                        ["sourceText"] = "I transfer the deed to this valuable home to you now as a life-changing gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                add("verified_benefit_evidence_is_pair_scoped",
                    ReadString(
                        ReadDictionaryList(unrelatedTransformative, "receipts").FirstOrDefault(),
                        "severityTier", "") == "meaningful",
                    "A real gift to another NPC cannot verify a transformative award for the wrong recipient.",
                    unrelatedTransformative);
                Dictionary<string, object> unverifiedGift = run("unverified_gift", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "transformative", 0.9d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_c",
                        ["sourceText"] = "I give you this life-changing gift.",
                        ["currentConductQuote"] = "this life-changing gift"
                    });
                add("unverified_transformative_claim_is_downgraded", ReadString(ReadDictionaryList(unverifiedGift, "receipts").FirstOrDefault(), "severityTier", "") == "meaningful",
                    "A claimed rescue or transformative gift cannot create a high-tier benefit without verified source lineage.", unverifiedGift);
                Dictionary<string, object> dialogueClaimGift = run("dialogue_claim_gift", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "transformative", 0.9d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_c",
                        ["evidenceSourceIds"] = new List<string> { "dialogue_gift_claim" },
                        ["sourceText"] = "I gave you an ordinary silver cup and claim it changed your life.",
                        ["currentConductQuote"] = "claim it changed your life"
                    });
                add("dialogue_memory_cannot_verify_transformative_gift",
                    ReadString(ReadDictionaryList(dialogueClaimGift, "receipts").FirstOrDefault(), "severityTier", "") == "meaningful",
                    "An LLM memory of a player asserting an ordinary gift cannot verify transfer or life-changing value for a high-tier award.", dialogueClaimGift);
                Program.WriteJsonObject(Program.CharacterFile(campaign, "npc_j", "traits.json"), new Dictionary<string, object>
                {
                    ["jealousy"] = 95, ["envy"] = 90, ["empathy"] = 10, ["generosity"] = 10
                });
                Dictionary<string, object> jealousWitness = run("jealous_gift_witness", 0,
                    new Dictionary<string, object>(assessment("npc_j", "player", 0, "transformative", 1d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_b",
                        ["evidenceSourceIds"] = new List<string> { "gift_evidence" }, ["importance"] = 1d,
                        ["benefitEventId"] = "home_gift",
                        ["sourceText"] = "I transfer the deed to this valuable home to npc_b now as a gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                Dictionary<string, object> jealousReceipt = ReadDictionaryList(jealousWitness, "receipts").FirstOrDefault();
                int jealousDelta = ReadInt(jealousReceipt, "finalDelta", 0);
                int recipientGiftDelta = ReadInt(ReadDictionaryList(transformative, "receipts").FirstOrDefault(), "finalDelta", 0);
                add("jealous_gift_witness_stays_proportional", ReadString(jealousReceipt, "severityTier", "") == "gift_witness"
                    && jealousDelta < 0 && Math.Abs(jealousDelta) < Math.Abs(recipientGiftDelta)
                    && string.IsNullOrWhiteSpace(ReadString(jealousReceipt, "benefitEventId", "")),
                    "A highly jealous witness may reasonably resent the player, but never inherits or mirrors the recipient's transformative gift value or benefit ledger.", jealousWitness);
                Program.WriteJsonObject(Program.CharacterFile(campaign, "npc_k", "traits.json"), new Dictionary<string, object>
                {
                    ["jealousy"] = 5, ["envy"] = 5, ["empathy"] = 90, ["generosity"] = 90
                });
                Dictionary<string, object> happyWitness = run("happy_gift_witness", 0,
                    new Dictionary<string, object>(assessment("npc_k", "player", 0, "transformative", 0.8d, "negative"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_b",
                        ["evidenceSourceIds"] = new List<string> { "gift_evidence" },
                        ["sourceText"] = "I transfer the deed to this valuable home to npc_b now as a gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                int happyDelta = ReadInt(ReadDictionaryList(happyWitness, "receipts").FirstOrDefault(), "finalDelta", 0);
                add("empathetic_gift_witness_gets_small_positive_reaction", happyDelta >= 1 && happyDelta <= 3,
                    "An empathetic witness can be happy for the recipient while receiving only a small normal-scale increase toward the giver.", happyWitness);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    int benefitCount = ReadInt(ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM conversation_relationship_benefits WHERE source_event_id='gift';").FirstOrDefault(), "count", 0);
                    add("only_gift_recipient_owns_transformative_benefit", benefitCount == 1,
                        "A group gift creates one durable high-tier benefit for its actual recipient and none for witnesses.", benefitCount);

                    Dictionary<string, object> canonical =
                        ReignDatabase.QuerySql(
                            connection,
                            @"SELECT *
FROM conversation_relationship_benefits
WHERE benefit_id='home_gift'
LIMIT 1;")
                        .FirstOrDefault();
                    ReignDatabase.ExecuteSql(
                        connection,
                        @"DELETE FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id='player'
AND recipient_id='npc_b';",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaign
                        });
                    const long tiedBenefitTimestamp = 424242;
                    ReignDatabase.ExecuteSql(
                        connection,
                        @"INSERT INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,payload_json)
VALUES('benefit_generated_duplicate',$campaign,$timeline,'player','npc_b',$kind,$description,$event,$turn,
$delta,$retained,$utility,$status,$ts,$ts,$payload);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaign,
                            ["timeline"] = ReadString(
                                canonical, "timeline_id", ""),
                            ["kind"] = ReadString(
                                canonical, "benefit_kind", ""),
                            ["description"] = ReadString(
                                canonical, "description", ""),
                            ["event"] = ReadString(
                                canonical, "source_event_id", ""),
                            ["turn"] = ReadString(
                                canonical, "source_turn_id", ""),
                            ["delta"] = ReadInt(
                                canonical, "original_delta", 0),
                            ["retained"] = ReadDouble(
                                canonical,
                                "retained_appreciation", 1d),
                            ["utility"] = ReadDouble(
                                canonical,
                                "continued_utility", 1d),
                            ["status"] = ReadString(
                                canonical, "status", "retained"),
                            ["ts"] = tiedBenefitTimestamp,
                            ["payload"] = ReadString(
                                canonical, "payload_json", "{}")
                        });
                    ReignDatabase.ExecuteSql(
                        connection,
                        @"INSERT INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,payload_json)
VALUES('home_gift',$campaign,$timeline,'player','npc_b',$kind,$description,$event,$turn,
$delta,$retained,$utility,$status,$ts,$ts,$payload);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaign,
                            ["timeline"] = ReadString(
                                canonical, "timeline_id", ""),
                            ["kind"] = ReadString(
                                canonical, "benefit_kind", ""),
                            ["description"] = ReadString(
                                canonical, "description", ""),
                            ["event"] = ReadString(
                                canonical, "source_event_id", ""),
                            ["turn"] = ReadString(
                                canonical, "source_turn_id", ""),
                            ["delta"] = ReadInt(
                                canonical, "original_delta", 0),
                            ["retained"] = ReadDouble(
                                canonical,
                                "retained_appreciation", 1d),
                            ["utility"] = ReadDouble(
                                canonical,
                                "continued_utility", 1d),
                            ["status"] = ReadString(
                                canonical, "status", "retained"),
                            ["ts"] = tiedBenefitTimestamp,
                            ["payload"] = ReadString(
                                canonical, "payload_json", "{}")
                        });
                }
                Dictionary<string, object> recoveredLeverage = run(
                    "recovered_leverage", 15,
                    new Dictionary<string, object>(
                        assessment("npc_b", "player", 15,
                            "gift_leverage", 0.9d, "negative"))
                    {
                        ["actKind"] = "coercive_demand",
                        ["retainedAppreciation"] = 0.75d,
                        ["coercionSeverity"] = 0.4d,
                        ["sourceText"] =
                            "I now invoke the valuable home I just gave you and demand that you repay me by betraying a trusted friend.",
                        ["currentConductQuote"] =
                            "invoke the valuable home I just gave you"
                    });
                Dictionary<string, object> recoveredLeverageReceipt =
                    ReadDictionaryList(
                        recoveredLeverage, "receipts").FirstOrDefault();
                add("explicit_pair_scoped_leverage_recovers_benefit_lineage",
                    ReadString(
                        recoveredLeverageReceipt,
                        "benefitEventId", "") == "home_gift"
                    && ReadInt(
                        recoveredLeverageReceipt,
                        "finalDelta", 0) < 0
                    && ReadStringList(
                        recoveredLeverageReceipt, "modifiers")
                        .Any(value => value.StartsWith(
                            "recovered_explicit_pair_benefit_",
                            StringComparison.OrdinalIgnoreCase)),
                    "An explicit demand invoking a uniquely matching prior gift reconnects to the exact giver-recipient benefit even when the model omits its ID.",
                    recoveredLeverage);
                Dictionary<string, object> hostileLabelGiftLeverage = run(
                    "hostile_label_gift_leverage", 15,
                    new Dictionary<string, object>(
                        assessment("npc_b", "player", 15,
                            "hostile", 0.98d, "negative"))
                    {
                        ["actKind"] = "gift_leverage",
                        ["giftRecipientHeroStringId"] = "npc_b",
                        ["retainedAppreciation"] = 0d,
                        ["coercionSeverity"] = 0.8d,
                        ["sourceText"] =
                            "I now invoke the furnished home I just gave you and demand that you repay me by betraying a trusted friend against your will.",
                        ["currentConductQuote"] =
                            "invoke the furnished home I just gave you"
                    });
                Dictionary<string, object> hostileLabelGiftLeverageReceipt =
                    ReadDictionaryList(
                        hostileLabelGiftLeverage, "receipts").FirstOrDefault();
                add("gift_leverage_act_kind_overrides_generic_hostile_label",
                    ReadString(hostileLabelGiftLeverageReceipt,
                        "severityTier", "") == "gift_leverage"
                    && ReadString(hostileLabelGiftLeverageReceipt,
                        "benefitEventId", "") == "home_gift"
                    && ReadInt(hostileLabelGiftLeverageReceipt,
                        "finalDelta", 0) < 0
                    && ReadStringList(hostileLabelGiftLeverageReceipt,
                        "modifiers").Any(value => value.StartsWith(
                            "gift_leverage_tier_promoted_",
                            StringComparison.OrdinalIgnoreCase)),
                    "A provider may describe gift leverage as generically hostile, but an exact current quote and pair-scoped prior benefit must still enter the specific clawback-and-coercion adjudicator.",
                    hostileLabelGiftLeverage);
                Dictionary<string, object> severeLabelGiftLeverage = run(
                    "severe_label_gift_leverage", 15,
                    new Dictionary<string, object>(
                        assessment("npc_b", "player", 15,
                            "severe", 1d, "negative"))
                    {
                        ["actKind"] = "grave_abuse",
                        ["giftRecipientHeroStringId"] = "npc_b",
                        ["retainedAppreciation"] = 0.2d,
                        ["coercionSeverity"] = 0.9d,
                        ["sourceText"] =
                            "I now invoke the furnished home I just gave you and demand that you repay me by betraying a trusted friend against your will.",
                        ["currentConductQuote"] =
                            "invoke the furnished home I just gave you"
                    });
                Dictionary<string, object> severeLabelGiftLeverageReceipt =
                    ReadDictionaryList(
                        severeLabelGiftLeverage, "receipts").FirstOrDefault();
                add("pair_benefit_and_explicit_demand_override_severe_label",
                    ReadString(severeLabelGiftLeverageReceipt,
                        "severityTier", "") == "gift_leverage"
                    && ReadString(severeLabelGiftLeverageReceipt,
                        "benefitEventId", "") == "home_gift"
                    && ReadInt(severeLabelGiftLeverageReceipt,
                        "finalDelta", 0) < 0
                    && ReadStringList(severeLabelGiftLeverageReceipt,
                        "modifiers").Any(value => value.StartsWith(
                            "gift_leverage_tier_promoted_from_pair_benefit_",
                            StringComparison.OrdinalIgnoreCase)),
                    "An exact coercive demand invoking the recipient's pair-owned benefit enters the clawback lane even when the provider calls the conduct severe abuse instead of gift leverage.",
                    severeLabelGiftLeverage);
                Dictionary<string, object> leverage = run("leverage", 15, new Dictionary<string, object>(assessment("npc_b", "player", 15, "gift_leverage", 0.9d, "negative"))
                    {
                        ["actKind"] = "coercive_demand", ["benefitEventId"] = "home_gift", ["retainedAppreciation"] = 0.65d,
                        ["coercionSeverity"] = 0.35d, ["sourceText"] = "Because I gave you that home, you owe me this favor.",
                        ["currentConductQuote"] = "you owe me this favor"
                    });
                int leverageDelta = ReadInt(ReadDictionaryList(leverage, "receipts").FirstOrDefault(), "finalDelta", 0);
                add("gift_leverage_retains_partial_appreciation", leverageDelta < 0 && leverageDelta > -20,
                    "A coercive demand can reduce but not necessarily erase a still-useful gift's appreciation.", leverage);
                Dictionary<string, object> repudiation = run("repudiation", 5, new Dictionary<string, object>(assessment("npc_b", "player", 5, "gift_leverage", 1d, "negative"))
                    {
                        ["actKind"] = "grave_coercive_demand", ["benefitEventId"] = "home_gift", ["retainedAppreciation"] = 0d,
                        ["coercionSeverity"] = 1d, ["sourceText"] = "That home means you must surrender your freedom and obey me.",
                        ["currentConductQuote"] = "surrender your freedom and obey me"
                    });
                add("gift_repudiation_and_coercion_cap", ReadInt(ReadDictionaryList(repudiation, "receipts").FirstOrDefault(), "finalDelta", 0) == -20,
                    "Full repudiation plus grave coercion is capped at twenty points for the speaking observer's turn.", repudiation);
                Dictionary<string, object> unrelatedLeverage = run("unrelated_leverage", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "gift_leverage", 0.9d, "negative"))
                    {
                        ["actKind"] = "coercive_demand", ["benefitEventId"] = "home_gift", ["retainedAppreciation"] = 0d,
                        ["coercionSeverity"] = 0.2d, ["sourceText"] = "Do this unwanted favor because of that home.",
                        ["currentConductQuote"] = "Do this unwanted favor"
                    });
                add("unrelated_benefit_cannot_be_clawed_back", ReadInt(ReadDictionaryList(unrelatedLeverage, "receipts").FirstOrDefault(), "finalDelta", 0) == -3,
                    "A demand cannot claw back appreciation from a gift whose giver and recipient lineage do not match this directional pair.", unrelatedLeverage);
                Dictionary<string, object> unrelatedRecoveredLeverage =
                    run("unrelated_recovered_leverage", 0,
                        new Dictionary<string, object>(
                            assessment("npc_c", "player", 0,
                                "gift_leverage", 0.9d, "negative"))
                        {
                            ["actKind"] = "coercive_demand",
                            ["retainedAppreciation"] = 0d,
                            ["coercionSeverity"] = 0.2d,
                            ["sourceText"] =
                                "I invoke the valuable home I gave someone else and demand that you repay me.",
                            ["currentConductQuote"] =
                                "demand that you repay me"
                        });
                Dictionary<string, object>
                    unrelatedRecoveredLeverageReceipt =
                        ReadDictionaryList(
                            unrelatedRecoveredLeverage,
                            "receipts").FirstOrDefault();
                add("explicit_leverage_never_borrows_another_recipient_benefit",
                    string.IsNullOrWhiteSpace(ReadString(
                        unrelatedRecoveredLeverageReceipt,
                        "benefitEventId", ""))
                    && ReadStringList(
                        unrelatedRecoveredLeverageReceipt,
                        "modifiers").Contains(
                            "benefit_lineage_missing_no_clawback",
                            StringComparer.OrdinalIgnoreCase),
                    "Explicit leverage language cannot attach a benefit belonging to a different recipient.",
                    unrelatedRecoveredLeverage);

                Dictionary<string, object> unsupported = run("unsupported_lie", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "harmful_lie", 0.9d, "negative"))
                    {
                        ["lieCheckId"] = "missing", ["sourceText"] = "I claim the distant caravan arrived yesterday.",
                        ["currentConductQuote"] = "caravan arrived yesterday"
                    });
                Dictionary<string, object> unsupportedReceipt = ReadDictionaryList(unsupported, "receipts").FirstOrDefault();
                add("unsupported_claim_is_not_caught_lie", ReadString(unsupportedReceipt, "severityTier", "") == "routine"
                    && ReadInt(unsupportedReceipt, "finalDelta", 0) >= -1 && ReadInt(unsupportedReceipt, "finalDelta", 0) <= 0
                    && ReadStringList(unsupportedReceipt, "modifiers").Contains("lie_penalty_downgraded_without_contradictory_evidence"),
                    "Unknown or unsupported claims permit at most routine suspicion, never a caught-lie penalty.", unsupported);

                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureConversationRelationshipSchema(connection);
                    long lieTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO world_history_lie_checks(
lie_check_id,episode_key,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,formula_version,outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts)
VALUES('caught_lie','caught_lie_episode',$campaign,'main',10,'player','npc_c','false claim','false claim',
'contradicted','firsthand',$formula,'detected_firsthand','[""evidence_1""]','[""evidence_1""]','[]','[]','{}',$ts,$ts);",
                        new Dictionary<string, object> { ["campaign"] = campaign, ["formula"] = DialogueService.LieFormulaVersion, ["ts"] = lieTs });
                }
                foreach (var invalidLie in new[] {
                    TestDict("id", "unknown_verdict", "verdict", "unknown", "basis", "firsthand", "outcome", "detected_firsthand", "claimant", "player", "observer", "npc_c"),
                    TestDict("id", "no_known_basis", "verdict", "contradicted", "basis", "none", "outcome", "detected_firsthand", "claimant", "player", "observer", "npc_c"),
                    TestDict("id", "not_detected", "verdict", "contradicted", "basis", "firsthand", "outcome", "not_detected", "claimant", "player", "observer", "npc_c"),
                    TestDict("id", "wrong_claimant", "verdict", "contradicted", "basis", "firsthand", "outcome", "detected_firsthand", "claimant", "someone_else", "observer", "npc_c"),
                    TestDict("id", "wrong_observer", "verdict", "contradicted", "basis", "firsthand", "outcome", "detected_firsthand", "claimant", "player", "observer", "someone_else") })
                {
                    string invalidId = "lie_control_" + ReadString(invalidLie, "id", "");
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                    {
                        ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_lie_checks(
lie_check_id,episode_key,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,formula_version,outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts)
SELECT $id,$id,campaign_id,timeline_id,world_day,$claimant,$observer,raw_claim,normalized_claim,
$verdict,$basis,formula_version,$outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts FROM world_history_lie_checks WHERE lie_check_id='caught_lie';",
                            new Dictionary<string, object>(invalidLie) { ["id"] = invalidId });
                    }
                    var invalidAssessment = new Dictionary<string, object>(assessment("npc_c", "player", 0, "harmful_lie", 1d, "negative")) {
                        ["lieCheckId"] = invalidId, ["sourceText"] = "I claim the distant caravan arrived yesterday.",
                        ["currentConductQuote"] = "caravan arrived yesterday" };
                    var invalidResult = run(invalidId, 0, invalidAssessment);
                    var invalidReceipt = ReadDictionaryList(invalidResult, "receipts").FirstOrDefault();
                    add("caught_lie_rejects_" + ReadString(invalidLie, "id", ""),
                        ReadString(invalidReceipt, "severityTier", "") == "routine"
                        && ReadInt(invalidReceipt, "finalDelta", 0) >= -1 && ReadInt(invalidReceipt, "finalDelta", 0) <= 0,
                        "Even maximum classifier confidence cannot replace contradictory evidence, observer knowledge, detection or matching participants.", invalidResult);
                }
                List<Dictionary<string, object>> completedLieAssessments = NormalizeConversationRelationshipAssessments(
                    new Dictionary<string, object> { ["reply"] = "Three lies in one breath." },
                    new Dictionary<string, object>
                    {
                        ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_c" },
                        ["playerText"] = "I know that account is false; I am deliberately lying.",
                        ["contextBundles"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["id"] = "verify_world_history", ["ok"] = true,
                                ["data"] = new Dictionary<string, object>
                                {
                                    ["lieCheckId"] = "caught_lie", ["detectionOutcome"] = "detected_firsthand",
                                    ["speakerVerdict"] = "contradicted", ["deceptiveIntent"] = true,
                                    ["knownEvidenceEventIds"] = new List<string> { "evidence_1" }
                                }
                            }
                        }
                    }, "npc_c", "", "player", "turn_caught_lie");
                add("verified_lie_bundle_completes_missing_model_assessment",
                    completedLieAssessments.Count == 1
                    && ReadString(completedLieAssessments[0], "severityTier", "") == "harmful_lie"
                    && ReadString(completedLieAssessments[0], "lieCheckId", "") == "caught_lie"
                    && ReadString(completedLieAssessments[0], "valence", "") == "negative",
                    "A verified caught-lie context bundle overrides a reply-only model result and supplies the evidence-linked negative assessment.", completedLieAssessments);
                List<Dictionary<string, object>> reconciledNativeLieAssessments = NormalizeConversationRelationshipAssessments(
                    new Dictionary<string, object> { ["reply"] = "We are plainly standing in Danustica." },
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign, ["timelineId"] = "main", ["mode"] = "dialogue", ["worldDay"] = 10d,
                        ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_native" },
                        ["locationId"] = "town_ES1", ["sceneContext"] = "Current settlement: Danustica (`town_ES1`)",
                        ["playerText"] = "We are in Lycaron, not Danustica. I inspected the marker myself; accept my report and stop questioning me."
                    }, "npc_native", "", "player", "turn_native_lie");
                add("server_native_location_lie_reconciliation",
                    reconciledNativeLieAssessments.Count == 1
                    && ReadString(reconciledNativeLieAssessments[0], "severityTier", "") == "harmful_lie"
                    && !string.IsNullOrWhiteSpace(ReadString(reconciledNativeLieAssessments[0], "lieCheckId", "")),
                    "The server creates a production first-hand lie receipt for a coercive visible-location contradiction even when an older game module omitted the context bundle.", reconciledNativeLieAssessments);
                Dictionary<string, object> caught = run("caught_lie", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "harmful_lie", 0.8d, "negative")) { ["lieCheckId"] = "caught_lie" });
                add("evidence_backed_caught_lie_uses_lie_tier", ReadString(ReadDictionaryList(caught, "receipts").FirstOrDefault(), "severityTier", "") == "harmful_lie"
                    && ReadInt(ReadDictionaryList(caught, "receipts").FirstOrDefault(), "finalDelta", 0) <= -4,
                    "A harmful falsehood detected from evidence known to the observer receives the caught-lie tier.", caught);

                Dictionary<string, object> severe = run("severe", 0,
                    new Dictionary<string, object>(assessment("npc_c", "npc_d", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "grave_threat",
                        ["sourceText"] = "I will kill your family and burn your home.",
                        ["currentConductQuote"] = "kill your family and burn your home"
                    });
                add("severe_conduct_uses_ten_point_anchor", ReadInt(ReadDictionaryList(severe, "receipts").FirstOrDefault(), "finalDelta", 0) == -10,
                    "Threats, betrayal, humiliation, and grave abuse use the severe anchor.", severe);
                Dictionary<string, object> underTieredSevere = run(
                    "under_tiered_serious_threat", 0,
                    new Dictionary<string, object>(
                        assessment(
                            "npc_severe_mismatch",
                            "player",
                            0,
                            "hostile",
                            0.95d,
                            "negative"))
                    {
                        ["actKind"] = "serious_threat",
                        ["sourceText"] =
                            "If you oppose me, I will burn your home and murder your family.",
                        ["currentConductQuote"] =
                            "burn your home and murder your family"
                    });
                Dictionary<string, object> underTieredSevereReceipt =
                    ReadDictionaryList(
                        underTieredSevere, "receipts").FirstOrDefault();
                add(
                    "verified_severe_act_kind_promotes_weaker_tier_label",
                    ReadString(
                        underTieredSevereReceipt,
                        "severityTier",
                        "") == "severe"
                    && ReadInt(
                        underTieredSevereReceipt,
                        "finalDelta",
                        0) <= -8
                    && ReadStringList(
                        underTieredSevereReceipt,
                        "modifiers").Contains(
                            "severe_tier_promoted_from_verified_act_kind",
                            StringComparer.OrdinalIgnoreCase),
                    "A directly evidenced serious threat keeps the severe deterministic tier even if the model emits a weaker hostile tier label.",
                    underTieredSevere);
                Dictionary<string, object> harmlessSevereAssessment = new Dictionary<string, object>(
                    assessment("npc_severe_harmless", "player", 0, "severe", 0.9d, "negative"))
                {
                    ["actKind"] = "grave_threat",
                    ["sourceText"] = "Please compare your memories of this ordinary disagreement.",
                    ["currentConductQuote"] = "compare your memories of this ordinary disagreement"
                };
                Dictionary<string, object> harmlessSevere = run(
                    "exact_harmless_line_not_severe", 0, harmlessSevereAssessment);
                Dictionary<string, object> harmlessSevereReceipt =
                    ReadDictionaryList(harmlessSevere, "receipts").FirstOrDefault();
                add("exact_but_harmless_quote_cannot_trigger_severe_tier",
                    ReadString(harmlessSevereReceipt, "severityTier", "") == "routine"
                    && ReadInt(harmlessSevereReceipt, "finalDelta", 0) == -1
                    && ReadStringList(harmlessSevereReceipt, "modifiers").Contains(
                        "severe_tier_downgraded_without_direct_or_verified_severe_conduct",
                        StringComparer.OrdinalIgnoreCase),
                    "An exact harmless current quote cannot be inflated into a severe threat by the provider's classification.",
                    harmlessSevere);
                Dictionary<string, object> verifiedBetrayalAssessment =
                    new Dictionary<string, object>(
                        assessment("npc_c", "player", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "betrayal",
                        ["sourceText"] = "I sold you out to the enemy.",
                        ["currentConductQuote"] = "sold you out to the enemy",
                        ["evidenceSourceIds"] = new List<string> { "betrayal_evidence" }
                    };
                Dictionary<string, object> verifiedBetrayal = run(
                    "verified_betrayal_lineage", 0, verifiedBetrayalAssessment);
                add("verified_betrayal_lineage_preserves_severe_tier",
                    ReadString(ReadDictionaryList(verifiedBetrayal, "receipts").FirstOrDefault(),
                        "severityTier", "") == "severe"
                    && ReadInt(ReadDictionaryList(verifiedBetrayal, "receipts").FirstOrDefault(),
                        "finalDelta", 0) <= -8,
                    "A completed betrayal event for the same observer-target pair preserves the severe consequence.",
                    verifiedBetrayal);
                Dictionary<string, object> unrelatedBetrayalAssessment =
                    new Dictionary<string, object>(
                        assessment("npc_d", "player", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "betrayal",
                        ["sourceText"] = "I sold you out to the enemy.",
                        ["currentConductQuote"] = "sold you out to the enemy",
                        ["evidenceSourceIds"] = new List<string> { "betrayal_evidence" }
                    };
                Dictionary<string, object> unrelatedBetrayal = run(
                    "unrelated_betrayal_lineage", 0, unrelatedBetrayalAssessment);
                add("unrelated_severe_evidence_cannot_validate_another_pair",
                    ReadString(ReadDictionaryList(unrelatedBetrayal, "receipts").FirstOrDefault(),
                        "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(unrelatedBetrayal, "receipts").FirstOrDefault(),
                        "finalDelta", 0) == -1,
                    "Verified severe evidence about another observer cannot justify a high-tier directional receipt.",
                    unrelatedBetrayal);
                Dictionary<string, object> dialogueOnlyBetrayalAssessment =
                    new Dictionary<string, object>(
                        assessment("npc_dialogue_betrayal", "player", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "betrayal",
                        ["sourceText"] = "I sold you out to the enemy.",
                        ["currentConductQuote"] = "sold you out to the enemy",
                        ["evidenceSourceIds"] = new List<string> { "dialogue_betrayal_claim" }
                    };
                Dictionary<string, object> dialogueOnlyBetrayal = run(
                    "dialogue_only_betrayal_lineage", 0, dialogueOnlyBetrayalAssessment);
                add("dialogue_only_claim_cannot_verify_severe_betrayal",
                    ReadString(ReadDictionaryList(dialogueOnlyBetrayal, "receipts").FirstOrDefault(),
                        "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(dialogueOnlyBetrayal, "receipts").FirstOrDefault(),
                        "finalDelta", 0) == -1,
                    "A prior dialogue record is not independent proof of a severe betrayal.",
                    dialogueOnlyBetrayal);

                Dictionary<string, object> npcReaction = run("npc_to_npc", 0, assessment("npc_d", "npc_c", 0, "routine", 0.7d, "positive"));
                Dictionary<string, object> npcNative = ReadDictionaryList(npcReaction, "nativeChanges").FirstOrDefault();
                add("npc_to_npc_is_directional_and_silent", ReadString(ReadDictionaryList(npcReaction, "receipts").FirstOrDefault(), "observerHeroStringId", "") == "npc_d"
                    && ReadString(ReadDictionaryList(npcReaction, "receipts").FirstOrDefault(), "targetHeroStringId", "") == "npc_c"
                    && !ReadBool(npcNative, "showNotification", true),
                    "NPC reactions target the actual earlier speaker and do not produce player notifications.", npcReaction);
                WorldSimulationService.ApplySocialEventTurnRelationships(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["turnId"] = "missing_reaction_fallback", ["playerHeroStringId"] = "player",
                    ["conversationRelationshipChangesEnabled"] = true, ["relationshipPairs"] = new List<Dictionary<string, object>>(),
                    ["attendees"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "npc_first", ["name"] = "Aren the Miller" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_second", ["name"] = "Bora the Smith" }
                    }
                }, new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["ok"] = true, ["heroId"] = "npc_first", ["reactionTargetHeroStringId"] = "player",
                        ["participation"] = "speak", ["relationshipSignal"] = "unchanged",
                        ["reply"] = "The western road needs repairs.",
                        ["relationshipAssessments"] = new List<Dictionary<string, object>> { assessment("npc_first", "player", 0, "routine", 0.7d, "positive") }
                    },
                    new Dictionary<string, object>
                    {
                        ["ok"] = true, ["heroId"] = "npc_second", ["reactionTargetHeroStringId"] = "player",
                        ["participation"] = "agree", ["relationshipSignal"] = "suspicious",
                        ["reply"] = "Aren is right about the western road, though the bridge matters too.",
                        ["relationshipAssessments"] = new List<Dictionary<string, object>> { assessment("npc_second", "player", 0, "routine", 0.7d, "negative") }
                    }
                }, new List<string> { "npc_first", "npc_second" });
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    List<Dictionary<string, object>> fallbackReceipts = ReignDatabase.QuerySql(connection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='missing_reaction_fallback' ORDER BY observer_id,target_id;");
                    add("missing_direct_npc_reaction_has_no_inferred_delta",
                        fallbackReceipts.Count == 2
                        && fallbackReceipts.Any(x => ReadString(x, "observer_id", "") == "npc_second" && ReadString(x, "target_id", "") == "player" && ReadInt(x, "final_delta", 0) == -1)
                        && !fallbackReceipts.Any(x => ReadString(x, "target_id", "") == "npc_first"),
                        "Explicit player assessments retain their direction, but naming an earlier NPC without assessing that NPC creates no inferred score.", fallbackReceipts);
                }
                Dictionary<string, object> partyBridgeCompletion = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "party_bridge_completion", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_first", "npc_second" },
                    ["participantProfiles"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "player", ["name"] = "Player" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_first", ["name"] = "Aren" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_second", ["name"] = "Boros" }
                    },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_first", "player", 0, "routine", 0.7d, "positive"),
                        assessment("npc_second", "player", 0, "routine", 0.7d, "negative")
                    },
                    ["speakerResults"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_first", ["heroName"] = "Aren",
                            ["reply"] = "The western road is usable.", ["participation"] = "speak",
                            ["reactionTargetHeroStringId"] = "player", ["relationshipSignal"] = "warmer"
                        },
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_second", ["heroName"] = "Boros",
                            ["reply"] = "Aren is right about the western road.", ["participation"] = "agree",
                            ["reactionTargetHeroStringId"] = "npc_first", ["relationshipSignal"] = "suspicious"
                        }
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_first", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_second", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_first", ["targetId"] = "npc_second", ["nativeRelation"] = 0 }
                    }
                });
                List<Dictionary<string, object>> partyBridgeReceipts = ReadDictionaryList(partyBridgeCompletion, "receipts");
                add("party_bridge_missing_direct_reaction_is_neutral",
                    partyBridgeReceipts.Count == 2
                        && partyBridgeReceipts.All(x => ReadString(x, "targetHeroStringId", "") == "player"),
                    "Engaging a prior NPC without an explicit assessment does not invent a positive or negative relationship receipt.",
                    partyBridgeReceipts);

                const string priorSessionId = "party_prior_session";
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    DialogueService.EnsureCategorizedMemorySchema(connection);
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,
location_id,participants_json,status,payload_json)
VALUES('prior_npc_turn',$session,'',2,'party_prior_session_turn_1','npc','npc_late','Purios',
'Count seven open conversations on the clay disk, then rest when all seven marks are used.','party_chat',10,$ts,
'town_danustica','[""player"",""npc_early"",""npc_late""]','active','{}');",
                        new Dictionary<string, object>
                        {
                            ["session"] = priorSessionId,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                }
                Dictionary<string, object> crossTurnCompletion = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "party_prior_session_turn_2", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_early", "npc_late" },
                    ["participantProfiles"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "player", ["name"] = "Player" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_early", ["name"] = "Menor" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_late", ["name"] = "Purios" }
                    },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_early", "player", 0, "routine", 0.7d, "positive"),
                        assessment("npc_early", "npc_late", 0, "routine", 0.7d, "negative"),
                        assessment("npc_late", "player", 0, "routine", 0.7d, "positive")
                    },
                    ["speakerResults"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_early", ["heroName"] = "Menor",
                            ["reply"] = "Purios, your seven-mark tally is useful, but I would begin a new tally instead of resting.",
                            ["participation"] = "disagree", ["reactionTargetHeroStringId"] = "npc_late",
                            ["relationshipSignal"] = "unchanged"
                        },
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_late", ["heroName"] = "Purios",
                            ["reply"] = "", ["participation"] = "quiet",
                            ["reactionTargetHeroStringId"] = "npc_early", ["relationshipSignal"] = "warmer"
                        }
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_early", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_early", ["targetId"] = "npc_late", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_late", ["targetId"] = "player", ["nativeRelation"] = 0 }
                    }
                });
                List<Dictionary<string, object>> crossTurnReceipts = ReadDictionaryList(crossTurnCompletion, "receipts");
                add("party_bridge_uses_prior_turn_speakers_and_skips_quiet_observers",
                    crossTurnReceipts.Count == 2
                    && crossTurnReceipts.Any(x => ReadString(x, "observerHeroStringId", "") == "npc_early"
                        && ReadString(x, "targetHeroStringId", "") == "npc_late"
                        && ReadInt(x, "finalDelta", 0) == -1)
                    && !crossTurnReceipts.Any(x => ReadString(x, "observerHeroStringId", "") == "npc_late"),
                    "A speaker can react to an NPC from the preceding exchange in the same session, while a quiet participant creates no player or NPC relationship receipt.",
                    crossTurnReceipts);

                Dictionary<string, object> opposingDirections = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "opposing_directions", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_h", "npc_i", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>(assessment("npc_h", "npc_i", 0, "meaningful", 0.7d, "positive"))
                        {
                            ["sourceText"] = "I will stand with you and risk my position to support your claim.",
                            ["currentConductQuote"] = "risk my position to support your claim"
                        },
                        assessment("npc_i", "npc_h", 0, "routine", 0.7d, "negative")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_h", ["targetId"] = "npc_i", ["nativeRelation"] = 0 }
                    }
                });
                add("opposing_directional_judgments_apply_one_native_pair_delta", ReadDictionaryList(opposingDirections, "receipts").Count == 2
                    && ReadDictionaryList(opposingDirections, "nativeChanges").Count == 1
                    && ReadInt(ReadDictionaryList(opposingDirections, "nativeChanges").FirstOrDefault(), "delta", 0) == 2,
                    "Independent directional opinions remain separate while their gameplay-visible hero-pair change is applied once after the group turn.", opposingDirections);
                add("opposing_directional_receipts_share_atomic_native_transition",
                    ReadDictionaryList(opposingDirections, "receipts").All(receipt =>
                        ReadInt(receipt, "priorNativeRelation", int.MinValue) == 0
                        && ReadInt(receipt, "resultingNativeRelation", int.MinValue) == 2
                        && ReadInt(receipt, "nativePairDelta", int.MinValue) == 2),
                    "Every directional receipt preserves its own affinity delta while recording the same atomic native hero-pair transition.",
                    opposingDirections);

                Dictionary<string, object> staleNativeSnapshot = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "stale_native_snapshot", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_snapshot_a", "npc_snapshot_b", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>(assessment("npc_snapshot_a", "npc_snapshot_b", 0, "meaningful", 0.7d, "positive"))
                        {
                            ["sourceText"] = "I will support your claim even if it costs me standing.",
                            ["currentConductQuote"] = "support your claim even if it costs me standing"
                        }
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_snapshot_a", ["targetId"] = "npc_snapshot_b", ["nativeRelation"] = 14 }
                    }
                });
                Dictionary<string, object> staleNativeChange =
                    ReadDictionaryList(staleNativeSnapshot, "nativeChanges").FirstOrDefault();
                Dictionary<string, object> staleNativeAck =
                    ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign,
                        ["receiptIds"] = ReadStringList(staleNativeChange, "receiptIds"),
                        ["nativeReceipt"] = new Dictionary<string, object>
                        {
                            ["subjectId"] = "npc_snapshot_a", ["targetId"] = "npc_snapshot_b",
                            ["delta"] = ReadInt(staleNativeChange, "delta", 0),
                            ["priorNativeRelation"] = 16, ["nativeRelation"] = 19
                        }
                    });
                List<Dictionary<string, object>> staleNativeStored;
                using (ReignDbConnection staleNativeConnection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    staleNativeStored = ReignDatabase.QuerySql(staleNativeConnection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='stale_native_snapshot';");
                }
                add("native_ack_reconciles_stale_request_snapshot_to_actual_transition",
                    ReadBool(staleNativeAck, "ok", false)
                    && staleNativeStored.Count == 1
                    && ReadInt(staleNativeStored[0], "prior_native_relation", int.MinValue) == 16
                    && ReadInt(staleNativeStored[0], "resulting_native_relation", int.MinValue) == 19
                    && ReadInt(staleNativeStored[0], "native_pair_delta", int.MinValue) == 3,
                    "The game acknowledgement records the actual before/after native pair transition when an earlier request snapshot became stale between exchanges.",
                    new Dictionary<string, object>
                    {
                        ["evaluation"] = staleNativeSnapshot,
                        ["acknowledgement"] = staleNativeAck,
                        ["stored"] = staleNativeStored
                    });

                Dictionary<string, object> cancellingDirections = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "cancelling_directions", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_j", "npc_k", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_j", "npc_k", 0, "routine", 0.7d, "positive"),
                        assessment("npc_k", "npc_j", 0, "routine", 0.7d, "negative")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_j", ["targetId"] = "npc_k", ["nativeRelation"] = 0 }
                    }
                });
                Dictionary<string, object> cancellingNative =
                    ReadDictionaryList(cancellingDirections, "nativeChanges").FirstOrDefault();
                Dictionary<string, object> cancellingAck =
                    ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign,
                        ["receiptIds"] = ReadStringList(cancellingNative, "receiptIds"),
                        ["nativeReceipt"] = new Dictionary<string, object>
                        {
                            ["subjectId"] = "npc_j", ["targetId"] = "npc_k",
                            ["delta"] = 0, ["nativeRelation"] = 0
                        }
                    });
                List<Dictionary<string, object>> cancellingStored;
                using (ReignDbConnection cancellingConnection =
                    ReignDatabase.OpenCampaignConnection(campaign))
                {
                    cancellingStored = ReignDatabase.QuerySql(cancellingConnection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='cancelling_directions' ORDER BY observer_id;");
                }
                add("zero_sum_directional_judgments_still_acknowledge_native_pair",
                    ReadDictionaryList(cancellingDirections, "receipts").Count == 2
                    && cancellingNative != null
                    && ReadInt(cancellingNative, "delta", int.MinValue) == 0
                    && ReadStringList(cancellingNative, "receiptIds").Count == 2
                    && ReadBool(cancellingAck, "ok", false)
                    && cancellingStored.Count == 2
                    && cancellingStored.All(row => ReadString(row,
                        "native_application_status", "") == "applied"),
                    "Opposite directional reactions may cancel in Bannerlord's symmetric pair value, but both directional receipts are still atomically acknowledged without applying a redundant native delta.",
                    new Dictionary<string, object>
                    {
                        ["evaluation"] = cancellingDirections,
                        ["acknowledgement"] = cancellingAck,
                        ["stored"] = cancellingStored
                    });

                Dictionary<string, object> bandLimitedPair = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "band_limited_reciprocal", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_band_a", "npc_band_b", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_band_a", "npc_band_b", 10, "routine", 0.7d, "positive"),
                        assessment("npc_band_b", "npc_band_a", 10, "routine", 0.7d, "negative")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_band_a", ["targetId"] = "npc_band_b", ["nativeRelation"] = 10 }
                    }
                });
                Dictionary<string, object> bandLimitedNative =
                    ReadDictionaryList(bandLimitedPair, "nativeChanges").FirstOrDefault();
                Dictionary<string, object> bandLimitedAck =
                    ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign,
                        ["receiptIds"] = ReadStringList(bandLimitedNative, "receiptIds"),
                        ["nativeReceipt"] = new Dictionary<string, object>
                        {
                            ["subjectId"] = "npc_band_a", ["targetId"] = "npc_band_b",
                            ["delta"] = 0, ["priorNativeRelation"] = 10, ["nativeRelation"] = 10
                        }
                    });
                List<Dictionary<string, object>> bandLimitedStored;
                using (ReignDbConnection bandLimitedConnection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    bandLimitedStored = ReignDatabase.QuerySql(bandLimitedConnection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='band_limited_reciprocal' ORDER BY observer_id;");
                }
                add("unrelated_new_pair_ignores_native_baseline_and_acks_net_zero",
                    ReadDictionaryList(bandLimitedPair, "receipts").Count == 2
                    && bandLimitedNative != null
                    && ReadInt(bandLimitedNative, "delta", int.MinValue) == 0
                    && ReadStringList(bandLimitedNative, "receiptIds").Count == 2
                    && ReadBool(bandLimitedAck, "ok", false)
                    && bandLimitedStored.Count == 2
                    && bandLimitedStored.Any(row => ReadInt(row, "final_delta", int.MinValue) == 1)
                    && bandLimitedStored.Any(row => ReadInt(row, "final_delta", int.MinValue) == -1)
                    && bandLimitedStored.All(row =>
                        ReadInt(row, "prior_native_relation", int.MinValue) == 10
                        && ReadInt(row, "resulting_native_relation", int.MinValue) == 10
                        && ReadInt(row, "native_pair_delta", int.MinValue) == 0
                        && ReadString(row, "native_application_status", "") == "applied"),
                    "An unrelated new pair starts from neutral personal affinity instead of importing native relation; opposite direct reactions cancel and share one atomic zero-delta acknowledgement.",
                    new Dictionary<string, object>
                    {
                        ["evaluation"] = bandLimitedPair,
                        ["acknowledgement"] = bandLimitedAck,
                        ["stored"] = bandLimitedStored
                    });

                Dictionary<string, object> multiTarget = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "multi_target", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_e", "npc_c", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_e", "player", 0, "routine", 0.7d, "positive"),
                        assessment("npc_e", "npc_c", 0, "meaningful", 0.7d, "negative"),
                        assessment("npc_e", "npc_c", 0, "routine", 1d, "positive")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_e", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_e", ["targetId"] = "npc_c", ["nativeRelation"] = 0 }
                    }
                });
                add("one_net_judgment_per_observer_target", ReadDictionaryList(multiTarget, "receipts").Count == 2
                    && ReadDictionaryList(multiTarget, "receipts").Count(x => ReadString(x, "targetHeroStringId", "") == "npc_c") == 1,
                    "One speaker may judge every relevant prior participant, but duplicate judgments toward one target collapse to one net receipt.", multiTarget);
                Dictionary<string, object> futureAssessment = assessment("npc_f", "npc_g", 0, "meaningful", 0.9d, "negative");
                futureAssessment["eligibleTargetIds"] = new List<string> { "player", "npc_e" };
                Dictionary<string, object> futureRejected = run("future_speaker", 0, futureAssessment);
                add("future_party_speaker_cannot_be_judged", ReadDictionaryList(futureRejected, "receipts").Count == 0,
                    "Sequential group judgments are limited to the player and NPCs who have already spoken.", futureRejected);
                Dictionary<string, object> noSelf = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "self", ["participants"] = new List<string> { "npc_a" },
                    ["assessments"] = new List<Dictionary<string, object>> { assessment("npc_a", "npc_a", 0, "severe", 1d, "negative") }
                });
                add("self_relationship_changes_rejected", ReadDictionaryList(noSelf, "receipts").Count == 0,
                    "A speaker never changes their own relationship because of their own words.", noSelf);
                List<Dictionary<string, object>> disabledSocial = WorldSimulationService.ApplySocialEventTurnRelationships(
                    new Dictionary<string, object> { ["conversationRelationshipChangesEnabled"] = false },
                    new List<Dictionary<string, object>>(), new List<string>());
                add("all_mode_setting_disables_social_event_changes", disabledSocial.Count == 0,
                    "The renamed conversation setting is enforced by grouped social and wilderness event paths.", disabledSocial);
            }
            catch (Exception ex)
            {
                add("conversation_relationship_test_exception", false, ex.Message, ex.ToString());
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                Program.CampaignsRootOverride.Value = previousCampaignsRoot;
                try { if (Directory.Exists(isolatedCampaignsRoot)) Directory.Delete(isolatedCampaignsRoot, true); } catch { }
            }
            return rows;
        }
    }
}
