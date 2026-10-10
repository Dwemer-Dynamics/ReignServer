using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool SameAcceptedDrinkNarration(Dictionary<string, object> receipt, string reply)
        {
            string prepared = ReadString(receipt, "reply", "");
            if (ReadBool(receipt, "suppressedCommitments", false)
                || ReadDictionary(receipt, "drinkScene") != null
                || ReadDictionaryList(receipt, "evidence").Any(e => ReadString(e, "source", "") == "structured_consumption"))
                return reply == prepared || reply.StartsWith(prepared + "\n\n[", StringComparison.Ordinal);
            return Regex.Matches(prepared, @"\*[^*]*\*").Cast<Match>().Select(m => m.Value)
                .SequenceEqual(Regex.Matches(reply ?? "", @"\*[^*]*\*").Cast<Match>().Select(m => m.Value), StringComparer.Ordinal);
        }

        // This operation owns negotiation and drinking. Main conversation routes
        // enlist it in their source/memory transaction; standalone callers retain
        // a local transaction. Social/scene/native projections have other owners.
        private static Dictionary<string, object> CommitFinalConversationEffects(string campaign, string hero,
            Dictionary<string, object> payload, Dictionary<string, object> motive,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics,
            Dictionary<string, object> parsed, string reply, string expectedGeneration,
            Action<string> boundary = null, ReignDbConnection existingConnection = null)
        {
            if (string.IsNullOrWhiteSpace(reply))
            {
                payload.Remove("intoxicationReceipt");
                var discarded = TestDict("ok", true, "commitState", "discarded", "reason", "no_accepted_npc_response",
                    "updates", new List<Dictionary<string, object>>());
                payload["conversationAgencyReceipt"] = discarded;
                if (parsed != null) parsed["conversationAgencyReceipt"] = discarded;
                var silent = TestDict("ok", true, "status", "unspoken_effects_discarded", "scope", "negotiation_and_drinking");
                payload["conversationEffectAcceptance"] = silent;
                return silent;
            }
            var candidate = CloneDictionary(parsed);
            candidate["reply"] = reply;
            if (candidate.ContainsKey("body")) candidate["body"] = reply;
            var drink = ReadDictionary(payload, "intoxicationReceipt");
            bool privateDrink = ReadString(drink, "accountingMode", "") == PrivateDrinkActionMode;
            string consumptionHash = ReadString(drink, "consumptionMetadataHash", "");
            bool changedConsumption = consumptionHash.Length > 0
                && consumptionHash != PromptHash(CanonicalJson(ReadDictionaryList(candidate, "drinkConsumptions")));
            bool changedScene = ReadString(drink, "sceneMetadataHash", "").Length > 0
                && ReadString(drink, "sceneMetadataHash", "") != DrinkSceneMetadataHash(candidate);
            if (!privateDrink && ReadString(drink, "commitState", "") == "prepared" && (!SameAcceptedDrinkNarration(drink, reply) || changedConsumption || changedScene))
            {
                // A later engine correction removed/replaced the narrated action.
                // Recalculate locally from that final text, never from the old draft.
                string reconsidered = ApplyLegacyConversationDrinking(campaign, hero, payload, profile, characteristics, candidate, reply, persist: false);
                if (reconsidered != reply)
                    throw new InvalidOperationException("The final response requires a new intoxication decision.");
                drink = ReadDictionary(payload, "intoxicationReceipt");
            }
            var context = ReadDictionary(motive, "conversationAgency");
            // Typed action turns use their own consent protocol, just as during
            // response enforcement. Legacy proposal metadata cannot gate their
            // publication; drinking still belongs to this acceptance transaction.
            bool hasAgency = !UsesStructuredActions(payload)
                && context != null && ReadString(context, "peerId", "").Length > 0;
            bool hasDrink = ReadString(drink, "commitState", "") == "prepared";
            if (!hasAgency && !hasDrink)
                return TestDict("ok", true, "status", "no_prepared_effects", "scope", "negotiation_and_drinking");
            if (string.IsNullOrWhiteSpace(expectedGeneration))
                throw new InvalidOperationException("Final conversation effects require the captured campaign generation.");
            Dictionary<string, object> agency = null, acceptedDrink = null;
            Dictionary<string, object> privateDrinkArgs = null;
            bool privateDrinkReplay = false;
            string acceptedHash = PromptHash(reply ?? "");
            lock (CampaignRelationshipWriteLock(campaign))
            using (ReignDbConnection ownedConnection = existingConnection == null ? OpenCampaignConnection(campaign) : null)
            {
                var connection = existingConnection ?? ownedConnection;
                if (hasAgency) EnsureConversationAgencySchema(connection);
                if (hasDrink) EnsureIntoxicationSchema(connection);
                using (var transaction = existingConnection == null ? connection.BeginTransaction() : null)
                {
                    string generation = ReadString(QuerySql(connection,
                        "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single(), "restore_generation", "");
                    if (generation != expectedGeneration)
                        throw new InvalidOperationException("The campaign changed before final conversation acceptance.");
                    if (hasDrink && privateDrink)
                    {
                        // Rebase the final private actions under the same locks as
                        // publication. Cup revisions, draft text and missing metadata
                        // never request a new model decision or reject this reply.
                        privateDrinkArgs = TestDict("hero", hero, "turn", ReadString(payload, "timelineId", "main") + ":"
                            + ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId"));
                        ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_intoxication(hero_id,state_json) VALUES($hero,'{}');", privateDrinkArgs);
                        QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero FOR UPDATE;", privateDrinkArgs);
                        var prior = QuerySql(connection, "SELECT receipt_json FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;", privateDrinkArgs).SingleOrDefault();
                        privateDrinkReplay = prior != null;
                        acceptedDrink = privateDrinkReplay ? TryParseJsonObject(ReadString(prior, "receipt_json", "{}"))
                            : PreparePrivateDrinkReceipt(connection, hero, payload, profile, characteristics, candidate, reply, lockScene: true);
                        if (!privateDrinkReplay)
                        {
                            acceptedDrink["locallyRebased"] = ReadString(acceptedDrink, "baseStateHash", "") != ReadString(drink, "baseStateHash", "")
                                || ReadString(acceptedDrink, "baseDrinkSceneHash", "") != ReadString(drink, "baseDrinkSceneHash", "")
                                || ReadString(acceptedDrink, "actionMetadataHash", "") != ReadString(drink, "actionMetadataHash", "");
                            acceptedDrink["commitState"] = "committed";
                            acceptedDrink["acceptedReplyHash"] = acceptedHash;
                        }
                        if (ReadBool(acceptedDrink, "suppressedCommitments", false))
                        {
                            SuppressPrivateDrinkCommitments(candidate);
                            SuppressPrivateDrinkCommitments(parsed);
                        }
                    }
                    if (hasAgency)
                    {
                        var evaluation = EvaluateConversationAgency(candidate, context);
                        if (!ReadBool(evaluation, "ok", false))
                            throw new InvalidOperationException("The final response no longer supports its negotiation metadata.");
                        if (ReadDictionaryList(evaluation, "updates").Count > 0)
                        {
                            string turn = FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), EnsureCorrelationId(payload));
                            agency = CommitConversationAgency(connection, candidate, context, turn, ownsTransaction: false);
                            if (!ReadBool(agency, "ok", false) || PromptHash(ReadString(agency, "reply", "")) != acceptedHash)
                                throw new InvalidOperationException("The negotiation changed or this turn already has a different accepted response.");
                        }
                        else agency = evaluation;
                        agency["commitState"] = "committed";
                        agency["acceptedReplyHash"] = acceptedHash;
                    }
                    boundary?.Invoke("after_agency");
                    if (hasDrink && privateDrink && !privateDrinkReplay)
                    {
                        privateDrinkArgs["state"] = Json.Serialize(ReadDictionary(acceptedDrink, "state"));
                        privateDrinkArgs["receipt"] = Json.Serialize(acceptedDrink);
                        CommitDrinkScene(connection, acceptedDrink);
                        ExecuteSql(connection, "UPDATE conversation_intoxication SET state_json=$state WHERE hero_id=$hero;", privateDrinkArgs);
                        ExecuteSql(connection, "INSERT INTO conversation_drinking_turns(hero_id,turn_key,receipt_json) VALUES($hero,$turn,$receipt);", privateDrinkArgs);
                    }
                    else if (hasDrink && !privateDrink)
                    {
                        var args = TestDict("hero", hero, "turn", ReadString(drink, "turnKey", ""));
                        if (ReadString(drink, "heroStringId", "") != hero || ReadString(drink, "turnKey", "").Length == 0)
                            throw new InvalidOperationException("The prepared drink belongs to a different participant or turn.");
                        ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_intoxication(hero_id,state_json) VALUES($hero,'{}');", args);
                        var stateRow = QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero FOR UPDATE;", args).Single();
                        var prior = QuerySql(connection, "SELECT receipt_json FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;", args).SingleOrDefault();
                        if (prior != null)
                        {
                            acceptedDrink = TryParseJsonObject(ReadString(prior, "receipt_json", "{}"));
                            if (ReadString(acceptedDrink, "acceptedReplyHash", PromptHash(ReadString(acceptedDrink, "reply", ""))) != acceptedHash)
                                throw new InvalidOperationException("This drinking turn already belongs to another accepted response.");
                        }
                        else
                        {
                            string currentState = PromptHash(CanonicalJson(TryParseJsonObject(ReadString(stateRow, "state_json", "{}"))));
                            if (ReadString(drink, "baseStateHash", "") != currentState)
                                throw new InvalidOperationException("The drink state changed while the response was being prepared.");
                            acceptedDrink = CloneDictionary(drink);
                            acceptedDrink["commitState"] = "committed";
                            acceptedDrink["acceptedReplyHash"] = acceptedHash;
                            acceptedDrink["reply"] = reply;
                            args["state"] = Json.Serialize(ReadDictionary(acceptedDrink, "state"));
                            args["receipt"] = Json.Serialize(acceptedDrink);
                            CommitDrinkScene(connection, acceptedDrink);
                            ExecuteSql(connection, "UPDATE conversation_intoxication SET state_json=$state WHERE hero_id=$hero;", args);
                            ExecuteSql(connection, "INSERT INTO conversation_drinking_turns(hero_id,turn_key,receipt_json) VALUES($hero,$turn,$receipt);", args);
                        }
                    }
                    boundary?.Invoke("before_commit");
                    transaction?.Commit();
                }
            }
            if (agency != null) { parsed["conversationAgencyReceipt"] = agency; payload["conversationAgencyReceipt"] = agency; }
            if (acceptedDrink != null) payload["intoxicationReceipt"] = acceptedDrink;
            var result = TestDict("ok", true, "status", "committed", "scope", "negotiation_and_drinking",
                "acceptedReplyHash", acceptedHash, "agency", agency ?? TestDict(), "drinking", acceptedDrink ?? TestDict());
            payload["conversationEffectAcceptance"] = result;
            return result;
        }

        private static Dictionary<string, object> TryCommitFinalConversationEffects(string campaign, string hero,
            Dictionary<string, object> payload, Dictionary<string, object> motive,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics,
            Dictionary<string, object> parsed, string reply)
        {
            try { return CommitFinalConversationEffects(campaign, hero, payload, motive, profile, characteristics, parsed, reply, _playerActionGeneration); }
            catch (Exception ex) when (!(ex is CampaignRequestReplacedException) && !(ex is OperationCanceledException))
            {
                LogOperational("dialogue.final_effect_acceptance_failed", TestDict("campaignId", campaign,
                    "heroStringId", hero, "correlationId", EnsureCorrelationId(payload), "error", ex.GetType().Name));
                return TestDict("ok", false, "stage", "final_effect_acceptance", "errorCode", "conversation_effects_stale_or_unsupported",
                    "error", "The final response could not be accepted against the current negotiation and drink state. No new negotiation, drinking or NPC action was confirmed.");
            }
        }
    }
}
