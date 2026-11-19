using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class RebellionService
    {
        internal static Dictionary<string, object> RebellionQueryApi(Dictionary<string, string> query)
        {
            string campaignId = RebellionQueryValue(query, "campaignId", "default");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                string movementId = RebellionQueryValue(query, "movementId", "");
                List<Dictionary<string, object>> movements = string.IsNullOrWhiteSpace(movementId)
                    ? RebellionStore.SelectRebellionMovements(connection, new Dictionary<string, object> { ["campaign"] = campaignId })
                    : RebellionStore.SelectRebellionMovements2(connection, new Dictionary<string, object> { ["id"] = movementId });
                foreach (Dictionary<string, object> movement in movements)
                {
                    movement["memberships"] = RebellionStore.SelectRebellionMemberships(connection, new Dictionary<string, object> { ["id"] = ReadString(movement, "movement_id", "") });
                    movement["transitions"] = RebellionStore.SelectRebellionTransitions(connection, new Dictionary<string, object> { ["id"] = ReadString(movement, "movement_id", "") });
                    movement["backingRequest"] = RebellionStore.SelectRebellionBackingRequests(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["id"] = ReadString(movement, "movement_id", "") }).FirstOrDefault();
                    movement["recognitionAttempts"] = RebellionStore.SelectRebellionRecognitionAttempts(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = ReadString(movement, "timeline_id", "main"),
                        ["id"] = ReadString(movement, "movement_id", "")
                    });
                }
                return new Dictionary<string, object> { ["ok"] = true, ["movements"] = movements };
            }
        }

        internal static Dictionary<string, object> NegotiationDraftApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double day = ReadDouble(payload, "worldDay", 0d);
            string id = ReadString(payload, "negotiationId", "");
            if (string.IsNullOrWhiteSpace(id)) id = "neg_" + Guid.NewGuid().ToString("N");
            Dictionary<string, object> terms = ReadDictionary(payload, "terms") ?? new Dictionary<string, object>();
            string politicalResult = ReadString(payload, "politicalResult", ReadString(terms, "politicalResult", ""));
            string command = ReadString(payload, "command", "diplomatic_package");
            if (string.IsNullOrWhiteSpace(ReadString(payload, "firstKingdomId", "")) || string.IsNullOrWhiteSpace(ReadString(payload, "secondKingdomId", ""))
                || string.IsNullOrWhiteSpace(ReadString(payload, "firstRulerId", "")) || string.IsNullOrWhiteSpace(ReadString(payload, "secondRulerId", "")))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A negotiation requires two exact kingdoms and their current rulers." };
            if (string.Equals(command, "resolve_civil_war", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(politicalResult, "recognized_independence",
                    StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Civil wars may be negotiated only through safeguarded recognition of rebel independence; ordinary peace and reunification packages are disabled."
                };
            if (!string.IsNullOrWhiteSpace(politicalResult)) terms["politicalResult"] = politicalResult;
            string termsJson = CanonicalTermsJson(terms);
            string hash = StableSha256(command + "|" + termsJson);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                Dictionary<string, object> old = RebellionStore.SelectNegotiatedActionDrafts(connection, new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
                if (old != null && !string.Equals(ReadString(old, "terms_hash", ""), hash, StringComparison.OrdinalIgnoreCase))
                    RebellionStore.DeleteNegotiatedActionApprovals(connection, new Dictionary<string, object> { ["id"] = id });
                RebellionStore.UpsertNegotiatedActionDrafts(connection, new Dictionary<string, object>
                    {
                        ["id"] = id, ["campaign"] = campaignId, ["timeline"] = ReadString(payload, "timelineId", "main"),
                        ["broker"] = ReadString(payload, "brokerHeroId", ""), ["firstKingdom"] = ReadString(payload, "firstKingdomId", ""),
                        ["secondKingdom"] = ReadString(payload, "secondKingdomId", ""), ["firstRuler"] = ReadString(payload, "firstRulerId", ""),
                        ["secondRuler"] = ReadString(payload, "secondRulerId", ""), ["command"] = command,
                        ["result"] = politicalResult, ["terms"] = termsJson, ["hash"] = hash, ["day"] = day,
                        ["expires"] = day + NegotiationLifetimeDays, ["payload"] = Json.Serialize(payload)
                    });
                return LoadNegotiation(connection, id, day);
            }
        }

        internal static Dictionary<string, object> NegotiationRespondApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string id = ReadString(payload, "negotiationId", "");
            string rulerId = ReadString(payload, "rulerHeroId", "");
            string kingdomId = ReadString(payload, "kingdomId", "");
            string response = ReadString(payload, "response", "approve").ToLowerInvariant();
            double day = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                Dictionary<string, object> draft = RebellionStore.SelectNegotiatedActionDrafts2(connection, new Dictionary<string, object> { ["id"] = id, ["campaign"] = campaignId }).FirstOrDefault();
                if (draft == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Negotiation draft was not found." };
                if (day > ReadDouble(draft, "expires_day", 0d))
                {
                    RebellionStore.UpdateNegotiatedActionDrafts(connection, new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Negotiation approval expired." };
                }
                bool authorized = string.Equals(rulerId, ReadString(draft, "first_ruler_id", ""), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rulerId, ReadString(draft, "second_ruler_id", ""), StringComparison.OrdinalIgnoreCase);
                if (!authorized) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Only a named current ruler may answer this draft." };
                string status = response == "reject" ? "rejected" : "approved";
                RebellionStore.UpsertNegotiatedActionApprovals(connection, new Dictionary<string, object>
                {
                    ["id"] = id, ["ruler"] = rulerId, ["kingdom"] = kingdomId, ["hash"] = ReadString(draft, "terms_hash", ""),
                    ["status"] = status, ["day"] = day, ["reason"] = ReadString(payload, "reason", "")
                });
                if (status == "rejected") RebellionStore.UpdateNegotiatedActionDrafts2(connection, new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                else TryQueueApprovedNegotiation(connection, draft, day);
                return LoadNegotiation(connection, id, day);
            }
        }

        private static void TryQueueApprovedNegotiation(ReignDbConnection connection, Dictionary<string, object> draft, double day)
        {
            string id = ReadString(draft, "negotiation_id", "");
            string hash = ReadString(draft, "terms_hash", "");
            List<Dictionary<string, object>> approvals = RebellionStore.SelectNegotiatedActionApprovals(connection, new Dictionary<string, object> { ["id"] = id, ["hash"] = hash });
            HashSet<string> approved = new HashSet<string>(approvals.Select(x => ReadString(x, "ruler_hero_id", "")), StringComparer.OrdinalIgnoreCase);
            if (!approved.Contains(ReadString(draft, "first_ruler_id", "")) || !approved.Contains(ReadString(draft, "second_ruler_id", "")))
            {
                RebellionStore.UpdateNegotiatedActionDrafts3(connection, new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                return;
            }
            if (!string.IsNullOrWhiteSpace(ReadString(draft, "queued_action_id", ""))) return;
            Dictionary<string, object> terms = Json.Deserialize<Dictionary<string, object>>(ReadString(draft, "terms_json", "{}")) ?? new Dictionary<string, object>();
            terms["authorizationMode"] = "negotiated";
            terms["negotiationId"] = id;
            terms["termsHash"] = hash;
            Dictionary<string, object> draftPayload = TryParseJsonObject(
                ReadString(draft, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            string source = ReadString(draftPayload, "source",
                "negotiated_player_broker");
            if (string.IsNullOrWhiteSpace(source))
                source = "negotiated_player_broker";
            Dictionary<string, object> raw = new Dictionary<string, object>
            {
                ["command"] = ReadString(draft, "command", "diplomatic_package"), ["campaignId"] = ReadString(draft, "campaign_id", "default"),
                ["actorKingdomId"] = ReadString(draft, "first_kingdom_id", ""), ["targetKingdomId"] = ReadString(draft, "second_kingdom_id", ""),
                ["actorHeroId"] = ReadString(draft, "first_ruler_id", ""), ["targetHeroId"] = ReadString(draft, "second_ruler_id", ""),
                ["source"] = source, ["reason"] = "Both rulers approved negotiated package " + id + ".",
                ["requiresAcceptance"] = false, ["authorizationMode"] = "negotiated", ["negotiationId"] = id, ["termsHash"] = hash,
                ["terms"] = terms
            };
            Dictionary<string, object> normalized = Program.NormalizeActionCommand(raw, ReadString(draft, "campaign_id", "default"), out List<string> errors, raw);
            if (errors.Count > 0 || normalized == null)
            {
                RebellionStore.UpdateNegotiatedActionDrafts4(connection, new Dictionary<string, object> { ["day"] = day, ["payload"] = Json.Serialize(errors), ["id"] = id });
                return;
            }
            Dictionary<string, object> queued = Program.QueueAction(ReadString(draft, "campaign_id", "default"), normalized);
            RebellionStore.UpdateNegotiatedActionDrafts5(connection, new Dictionary<string, object> { ["action"] = ReadString(queued, "actionId", ReadString(normalized, "actionId", "")), ["day"] = day, ["id"] = id });
        }

        internal static Dictionary<string, object> NegotiationQueryApi(Dictionary<string, string> query)
        {
            string campaignId = RebellionQueryValue(query, "campaignId", "default");
            double day = double.TryParse(RebellionQueryValue(query, "worldDay", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0d;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                string id = RebellionQueryValue(query, "negotiationId", "");
                if (!string.IsNullOrWhiteSpace(id)) return LoadNegotiation(connection, id, day);
                List<Dictionary<string, object>> rows = RebellionStore.SelectNegotiatedActionDrafts3(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                foreach (Dictionary<string, object> row in rows) row["approvals"] = RebellionStore.SelectNegotiatedActionApprovals2(connection, new Dictionary<string, object> { ["id"] = ReadString(row, "negotiation_id", "") });
                return new Dictionary<string, object> { ["ok"] = true, ["negotiations"] = rows };
            }
        }

        private static Dictionary<string, object> LoadNegotiation(ReignDbConnection connection, string id, double day)
        {
            Dictionary<string, object> row = RebellionStore.SelectNegotiatedActionDrafts4(connection, new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
            if (row == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Negotiation draft was not found." };
            if (day > ReadDouble(row, "expires_day", double.MaxValue) && !DialogueService.ContainsAny(Program.NormalizeLookup(ReadString(row, "status", "")), "queued", "completed", "rejected", "failed"))
            {
                RebellionStore.UpdateNegotiatedActionDrafts(connection, new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                row["status"] = "expired";
            }
            row["approvals"] = RebellionStore.SelectNegotiatedActionApprovals3(connection, new Dictionary<string, object> { ["id"] = id });
            row["ok"] = true;
            return row;
        }

        internal static void UpdateNegotiationFromActionReport(string campaignId, string actionId, string status, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(actionId)) return;
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureRebellionSchema(connection);
                    string normalized = (status ?? string.Empty).ToLowerInvariant();
                    string draftStatus = normalized == "completed" ? "completed" : normalized == "failed" ? "failed" : normalized == "retry" ? "queued" : "executing";
                    RebellionStore.UpdateNegotiatedActionDrafts6(connection, new Dictionary<string, object>
                    {
                        ["status"] = draftStatus, ["day"] = ReadDouble(payload, "worldDay", 0d), ["payload"] = Json.Serialize(payload),
                        ["campaign"] = campaignId, ["action"] = actionId
                    });
                }
            }
            catch (Exception ex)
            {
                Program.LogOperational("negotiation.action_report_update_failed", new Dictionary<string, object> { ["campaignId"] = campaignId, ["actionId"] = actionId, ["error"] = ex.Message });
            }
        }

        internal static void UpdateRebellionBackingFromActionReport(string campaignId,
            string actionId, string status, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(actionId)) return;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                string normalized = Program.NormalizeLookup(status).Replace(' ', '_');
                string backingStatus = normalized == "completed" ? "completed"
                    : DialogueService.ContainsAny(normalized, "failed", "invalid", "expired", "cancel") ? "invalidated" : "accepted";
                RebellionStore.UpdateRebellionBackingRequests(connection, new Dictionary<string, object>
                {
                    ["status"] = backingStatus, ["terminal"] = backingStatus == "invalidated" ? 1 : 0,
                    ["reason"] = ReadFirstString(payload, "message", "reason", "error"),
                    ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()),
                    ["campaign"] = campaignId, ["action"] = actionId
                });
            }
        }

        internal static bool LooksLikeRebellionPreparationRequest(string text)
        {
            string normalized = Program.NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)
                || DialogueService.ContainsAny(normalized, "hypothetically", "someday", "what if"))
                return false;
            bool rebellion = DialogueService.ContainsAny(normalized, "rebellion", "rebel", "rise against",
                "challenge the ruler", "overthrow the ruler", "depose the ruler");
            bool support = DialogueService.ContainsAny(normalized, "pledge", "promise", "support me", "stand with me",
                "support my rebellion", "support my planned rebellion", "join me", "loyalty",
                "back me", "follow me", "refuse", "report this conspiracy", "report my",
                "report me", "warn the ruler", "name me as the author", "name me as its author");
            return rebellion && support;
        }

        private static string ClassifyRebellionPreparationReply(string text)
        {
            string normalized = Program.NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            bool negatesReport = DialogueService.ContainsAny(normalized, "not report", "won't report",
                "will not report", "never report", "no report");
            if (!negatesReport && DialogueService.ContainsAny(normalized, "report this", "report it", "report you",
                "report the plot", "report your plot", "report the conspiracy", "report the rebellion",
                "tell the ruler", "inform the ruler", "warn the ruler", "warning the ruler",
                "send word to the ruler",
                "expose your plot", "expose this plot", "name you as the author",
                "name you as its author", "treason must be reported"))
                return "report";
            bool negatesPledge = DialogueService.ContainsAny(normalized, "not pledge", "won't pledge",
                "wont pledge", "won t pledge", "will not pledge", "cannot pledge", "can't pledge",
                "cant pledge", "can t pledge", "do not pledge", "don't pledge", "dont pledge",
                "don t pledge",
                "never pledge", "no pledge", "not stand with you",
                "not support your rebellion", "will not join your rebellion");
            if (!negatesPledge
                && DialogueService.ContainsAny(normalized, "pledge", "stand with you", "stands with you",
                    "rides with you",
                    "support your rebellion", "join your rebellion", "my clan stands",
                    "you have my loyalty", "back your claim"))
                return "pledge";
            if (normalized == "no" || normalized.StartsWith("no ", StringComparison.Ordinal)
                || DialogueService.ContainsAny(normalized, "i refuse", "we refuse", "no pledge",
                "cannot pledge", "can't pledge", "cant pledge", "can t pledge",
                "no support", "cannot support",
                "will not support", "won't support",
                "will not join", "won't join", "will not throw my",
                "stands aside", "stand aside", "decline"))
                return "refuse";
            return string.Empty;
        }

        internal static string ClassifyRebellionSummonsReply(string text)
        {
            string normalized = Program.NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            if (DialogueService.ContainsAny(normalized, "renounce", "recant", "abandon the plot",
                "submit to your judgment", "submit to your judgement")) return "renounce";
            if (DialogueService.ContainsAny(normalized, "defy", "declare rebellion", "rebel against you",
                "challenge your rule", "take the throne")) return "defy";
            return string.Empty;
        }

        internal static string ClassifyRebellionRulerVerdict(string text)
        {
            string normalized = Program.NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            if (DialogueService.ContainsAny(normalized, "execute", "execution", "put you to death", "death sentence"))
                return "execution";
            if (DialogueService.ContainsAny(normalized, "banish", "exile", "leave my realm")) return "exile";
            if (DialogueService.ContainsAny(normalized, "imprison", "prison", "dungeon")) return "imprisonment";
            if (DialogueService.ContainsAny(normalized, "pardon", "forgive", "grant mercy", "go free")) return "pardon";
            return string.Empty;
        }

        internal static List<Dictionary<string, object>> BuildRebellionPreparationDialogueCandidates(
            Dictionary<string, object> payload, Dictionary<string, object> hero,
            string playerText, string npcText, Dictionary<string, object> actionGate)
        {
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            if (!LooksLikeRebellionPreparationRequest(playerText)) return candidates;
            string commitment = Program.NormalizeLookup(ReadString(actionGate, "commitment", ""));
            bool actionNeeded = ReadBool(actionGate, "needed", false);
            string visibleDecision = ClassifyRebellionPreparationReply(npcText ?? string.Empty);
            // A concealed report may arrive either as the dedicated structured commitment or
            // as explicit private intent alongside a visible refusal. The latter is a normal
            // provider encoding: the reply refuses support while the unshown action reports it.
            // Never infer it from a negated private intent, and always apply eligibility below.
            string privateIntent = Program.NormalizeLookup(ReadString(actionGate, "intent", ""));
            bool privateIntentNegated = DialogueService.ContainsAny(privateIntent,
                "not privately report", "does not privately report", "will not privately report",
                "never privately report", "no private report");
            bool explicitPrivateReport = !privateIntentNegated && DialogueService.ContainsAny(privateIntent,
                "privately report", "privately reports", "private report");
            string privateDecision = commitment == "final private report" || explicitPrivateReport
                ? "report"
                : ClassifyRebellionPreparationReply(privateIntent);
            Dictionary<string, object> reportEvidence =
                BuildRebellionPrivateReportEvidence(payload, hero);
            bool privateReport = privateDecision == "report"
                && ReadBool(reportEvidence, "eligible", false)
                && (explicitPrivateReport
                    ? actionNeeded
                    : Program.ActionGateShouldPlan(actionGate));
            bool visibleReport = visibleDecision == "report"
                && ReadBool(reportEvidence, "eligible", false)
                && Program.ActionGateShouldPlan(actionGate);
            string decision = privateReport || visibleReport
                ? "report"
                : commitment == "refused" || visibleDecision == "refuse"
                    || privateDecision == "report" || visibleDecision == "report"
                    ? "refuse"
                    : visibleDecision;
            if (string.IsNullOrWhiteSpace(decision)) return candidates;
            bool finalDecision = decision == "refuse"
                ? actionNeeded && commitment == "refused"
                : decision == "report" && explicitPrivateReport
                    ? actionNeeded
                : Program.ActionGateShouldPlan(actionGate);
            if (!finalDecision) return candidates;
            string actorId = ReadFirstString(payload, "playerHeroStringId", "playerId", "actorHeroId");
            string targetId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            string clanId = ReadFirstString(hero, "clanId", "clanStringId");
            candidates.Add(new Dictionary<string, object>
            {
                ["command"] = "resolve_rebellion_pledge",
                ["actorHeroId"] = actorId,
                ["actorKingdomId"] = ReadFirstString(payload, "playerKingdomId", "actorKingdomId"),
                ["actorClanId"] = ReadFirstString(payload, "playerClanId", "actorClanId"),
                ["targetHeroId"] = targetId,
                ["targetClanId"] = clanId,
                ["reason"] = "Character-authored secret rebellion preparation decision.",
                ["terms"] = new Dictionary<string, object>
                {
                    ["decision"] = decision,
                    ["decisionReason"] = npcText ?? string.Empty,
                    ["requestChannel"] = NormalizeRebellionPreparationChannel(payload),
                    ["privateReport"] = decision == "report",
                    ["reportEligibility"] = reportEvidence
                }
            });
            return candidates;
        }

        private static Dictionary<string, object> BuildRebellionPrivateReportEvidence(
            Dictionary<string, object> payload,
            Dictionary<string, object> hero)
        {
            payload = payload ?? new Dictionary<string, object>();
            hero = hero ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            Dictionary<string, object> characteristics = string.IsNullOrWhiteSpace(heroId)
                ? new Dictionary<string, object>()
                : CharactersService.LoadCharacterStack(campaignId, heroId);
            Dictionary<string, object> reignTraits = DialogueService.TraitPercentageSnapshot(
                ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>());
            Dictionary<string, object> nativeTraits =
                ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
            bool sameSovereign = ReadBool(hero, "sharesPlayerSovereign", false);
            int sovereignRelation = ReadInt(hero, "relationToSovereign", 0);
            int playerRelation = ReadInt(hero, "relationToPlayer", 0);
            int loyalty = ReadInt(reignTraits, "loyalty", 50);
            int honor = ReadInt(nativeTraits, "honor", 0);
            bool eligible = RebellionPrivateReportEligible(
                sameSovereign, sovereignRelation, loyalty, honor);
            return new Dictionary<string, object>
            {
                ["eligible"] = eligible,
                ["sharesPlayerSovereign"] = sameSovereign,
                ["sovereignHeroStringId"] = ReadString(hero, "sovereignHeroStringId", ""),
                ["sovereignName"] = ReadString(hero, "sovereignName", ""),
                ["relationToSovereign"] = sovereignRelation,
                ["relationToPlayer"] = playerRelation,
                ["loyalty"] = loyalty,
                ["honor"] = honor,
                ["minimumSovereignRelation"] = 10,
                ["minimumLoyaltyWithoutPositiveHonor"] = 61
            };
        }

        internal static bool RebellionPrivateReportEligible(bool sameSovereign,
            int sovereignRelation, int loyalty, int honor)
        {
            return sameSovereign && sovereignRelation >= 10
                && (loyalty >= 61 || honor >= 1);
        }

        private static string NormalizeRebellionPreparationChannel(Dictionary<string, object> payload)
        {
            string channel = Program.NormalizeLookup(ReadFirstString(payload,
                "interactionMode", "conversationMode", "channel", "mode"));
            if (DialogueService.ContainsAny(channel, "correspondence", "letter", "mail"))
                return "correspondence";
            if (string.IsNullOrWhiteSpace(channel)
                || DialogueService.ContainsAny(channel, "individual_chat", "individual chat", "in_person",
                    "in person", "conversation", "dialogue"))
                return "individual_chat";
            return channel.Replace(' ', '_');
        }
    }
}
