using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PrivateDrinkActionMode = "private_actions_v1";
        private const int MaxPrivateDrinkActions = 16;

        private static string PrivateDrinkText(Dictionary<string, object> item, string key, int max = 128)
            => item != null && item.TryGetValue(key, out var value) && value is string text && text.Length <= max
                ? text.Trim() : "";

        private static string PrivateDrinkActionHash(Dictionary<string, object> parsed)
            => PromptHash(CanonicalJson(parsed != null && parsed.TryGetValue("drinkActions", out var value) ? value : null));

        private static List<Dictionary<string, object>> PrivateDrinkItems(Dictionary<string, object> parsed,
            List<Dictionary<string, object>> diagnostics)
        {
            var result = new List<Dictionary<string, object>>();
            if (parsed == null || !parsed.TryGetValue("drinkActions", out var raw))
            {
                diagnostics.Add(TestDict("reason", "missing_private_actions"));
                return result;
            }
            if (raw is not IEnumerable sequence || raw is string || raw is IDictionary)
            {
                diagnostics.Add(TestDict("reason", "malformed_private_actions"));
                return result;
            }
            int index = 0;
            foreach (var entry in sequence)
            {
                if (index++ == MaxPrivateDrinkActions)
                {
                    diagnostics.Add(TestDict("reason", "private_action_limit", "limit", MaxPrivateDrinkActions));
                    break;
                }
                if (entry is Dictionary<string, object> item) result.Add(item);
                else diagnostics.Add(TestDict("reason", "malformed_private_action", "index", index - 1));
            }
            return result;
        }

        private static Dictionary<string, object> FindPrivateDrinkVessel(DrinkScene scene, string reference, string hero)
        {
            if (scene.Aliases.TryGetValue(reference, out var actual)) reference = actual;
            if (reference.Length > 0)
                return scene.Vessels.FirstOrDefault(v => ReadString(v, "id", "") == reference)
                    ?? scene.Vessels.FirstOrDefault(v => ReadString(v, "localRef", "") == reference
                        && ReadString(v, "holderHeroId", "") == hero);
            var held = scene.Vessels.Where(v => ReadString(v, "holderHeroId", "") == hero).ToList();
            return held.Count == 1 ? held[0] : null;
        }

        private static double? PrivateDrinkQuantity(string quantity)
            => quantity == "full" ? 1d : quantity == "half" ? .5d : quantity == "quarter" ? .25d
                : quantity == "empty" ? 0d : (double?)null;

        private static Dictionary<string, object> NewPrivateDrinkVessel(DrinkScene scene, string reference,
            string holder, string player, BeverageIdentity beverage, string kind, string turn)
        {
            if (scene.Vessels.Count >= 16 || scene.Scope.Length == 0) return null;
            string id = "vessel_" + PromptHash(scene.Scope + "|" + turn + "|" + holder + "|" + reference).Substring(0, 28);
            var existing = scene.Vessels.FirstOrDefault(v => ReadString(v, "id", "") == id);
            if (existing != null) return existing;
            var record = DrinkVesselRecord(new DrinkVesselState {
                Id = id, Scope = scene.Scope, Hour = scene.Hour, Kind = kind.Length > 0 ? kind : "cup",
                Holder = holder == player ? "player" : "npc", ContentsVersion = 1, Beverage = beverage.Copy() });
            record["holderHeroId"] = holder;
            record["localRef"] = reference;
            scene.Vessels.Add(record);
            if (reference.Length > 0) scene.Aliases[reference] = id;
            return record;
        }

        // This reducer consumes typed choices, never the visible prose. Bad drink data
        // produces bounded diagnostics, not a dialogue rejection or a provider call.
        private static Dictionary<string, object> PreparePrivateDrinkReceipt(ReignDbConnection connection,
            string hero, Dictionary<string, object> payload, Dictionary<string, object> profile,
            Dictionary<string, object> characteristics, Dictionary<string, object> parsed, string reply,
            bool lockScene = false)
        {
            string turn = ReadString(payload, "timelineId", "main") + ":" + ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId");
            var stored = TryParseJsonObject(ReadString(QuerySql(connection,
                "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero;", TestDict("hero", hero)).SingleOrDefault(), "state_json", "{}"));
            double hour = IntoxicationWorldHour(payload);
            var state = RecoverIntoxication(stored, IntoxicationEndurance(profile, characteristics), hour);
            var scene = LoadDrinkScene(connection, payload, hour, lockScene);
            // Older/custom scenes can still retain one current cup. Native prepared
            // scenes use their shared physical identity across chat reopening.
            if (scene.Scope.Length == 0)
            {
                var legacyCup = CurrentDrinkVessel(state.Vessel, payload, hour);
                scene.Scope = legacyCup.Scope;
                if (legacyCup.Id.Length > 0) {
                    var record = DrinkVesselRecord(legacyCup); record["holderHeroId"] = hero; scene.Vessels.Add(record);
                }
            }
            bool stale = !FiniteDrinkNumber(hour) || ReadBool(stored, "hasClock", false)
                && hour < ReadDouble(stored, "hour", 0) - .000001
                || hour < ReadDouble(scene.Stored, "hour", double.NaN) - .000001;
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            var participants = DrinkSceneParticipants(payload, hero);
            var evidence = new List<Dictionary<string, object>>();
            var diagnostics = new List<Dictionary<string, object>>();
            var items = PrivateDrinkItems(parsed, diagnostics);
            if (ReadDictionaryList(parsed, "drinkingEvents").Count > 0 || ReadDictionaryList(parsed, "drinkConsumptions").Count > 0
                || ReadDictionaryList(parsed, "drinkSceneUpdates").Count > 0)
                diagnostics.Add(TestDict("reason", "legacy_drink_metadata_ignored"));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            double proposed = 0, consumed = 0;
            int events = 0, alcoholEvents = 0;
            bool complete = diagnostics.Count == 0;
            foreach (var item in items)
            {
                string localId = PrivateDrinkText(item, "id");
                if (localId.Length == 0) {
                    localId = "inferred_" + PromptHash(CanonicalJson(item)).Substring(0, 24);
                    diagnostics.Add(TestDict("reason", "private_action_id_inferred", "id", localId));
                }
                if (!seen.Add(localId)) {
                    diagnostics.Add(TestDict("reason", "duplicate_private_action", "id", localId)); continue;
                }
                string operation = PrivateDrinkText(item, "operation"), reference = PrivateDrinkText(item, "vesselId");
                string holder = PrivateDrinkText(item, "holderHeroId");
                if (holder.Length == 0) holder = hero;
                var detail = TestDict("source", "private_drink_action", "actionId", localId,
                    "eventId", "drink_" + PromptHash(hero + "|" + turn + "|" + localId).Substring(0, 28),
                    "operation", operation, "drinkerId", hero, "holderHeroId", holder, "vesselId", reference,
                    "amount", 0d, "acceptedAmount", 0d, "consumptionEvents", 0, "acceptedConsumptionEvents", 0,
                    "amountUnit", "gameplay_serving_equivalent", "alcoholVolumeMl", null, "standardDrinkCount", null,
                    "binding", "private_action", "reason", "");
                evidence.Add(detail);
                void Skip(string reason, bool incomplete = false) {
                    detail["reason"] = reason; diagnostics.Add(TestDict("id", localId, "reason", reason));
                    if (incomplete) complete = false;
                }
                if (!ReadBool(item, "completed", false)) { Skip("action_not_completed"); continue; }
                if (stale) { Skip("stale_or_missing_clock", true); continue; }
                if (!participants.Contains(holder) || operation == "consume" && holder != hero) { Skip("wrong_drink_participant"); continue; }
                if (operation != "consume" && operation != "establish" && operation != "refill"
                    && operation != "transfer" && operation != "discard") { Skip("unknown_drink_operation", true); continue; }
                var record = FindPrivateDrinkVessel(scene, reference, holder);
                string previousHolder = ReadString(record, "holderHeroId", "");
                if (operation == "consume" && record != null && previousHolder != hero) { Skip("cup_held_by_other_participant"); continue; }
                string beverageName = PrivateDrinkText(item, "beverage", 200);
                bool malformedBeverage = item.TryGetValue("beverage", out var rawBeverage) && rawBeverage != null
                    && (rawBeverage is not string || ((string)rawBeverage).Length > 200);
                var beverage = malformedBeverage ? new BeverageIdentity()
                    : beverageName.Length > 0 ? ResolveUniqueDrinkBeverage(beverageName) : ReadDrinkVessel(record).Beverage;
                if (malformedBeverage) { diagnostics.Add(TestDict("id", localId, "reason", "malformed_beverage")); complete = false; }
                // A declared preparation may include nonalcoholic ingredients;
                // an ambiguous alternative such as 'wine or water' stays unknown.
                if (beverage.Alcohol == DrinkAlcohol.Unknown && beverageName.Length > 0
                    && DrinkHas(beverageName, @"\b(?:mixed|spiked|laced|diluted|watered|fortified|blended)\b|\b(?:wine|brandy|rum)\s+(?:and|with)\s+water\b|\bwater\s+(?:and|with)\s+wine\b"))
                    beverage = ResolveDrinkBeverage(beverageName);
                if (beverage.Name.Length == 0 && beverageName.Length > 0) beverage.Name = beverageName;
                string kind = DrinkVesselKind(PrivateDrinkText(item, "kind"));
                if (record != null && operation != "transfer" && operation != "discard" && previousHolder != holder)
                { Skip("cup_custody_mismatch"); continue; }
                if (record != null && item.ContainsKey("contentsVersion") && ReadDouble(item, "contentsVersion", 0) > 0
                    && ReadDouble(item, "contentsVersion", 0) != ReadInt(record, "contentsVersion", 0))
                    diagnostics.Add(TestDict("id", localId, "reason", "stale_cup_revision_observed"));
                if (operation == "discard" || operation == "transfer")
                {
                    if (record == null) { Skip("cup_record_unavailable"); continue; }
                    if (operation == "discard" ? previousHolder != hero : previousHolder != hero && holder != hero)
                    { Skip("other_participant_custody"); continue; }
                    if (operation == "discard") scene.Vessels.Remove(record);
                    else {
                        record["holderHeroId"] = holder; record["holder"] = holder == player ? "player" : "npc";
                        record["sharedWithPlayer"] = holder == player || previousHolder == player;
                        if (holder == player || previousHolder == player) record["sharedWithHeroId"] = holder == player ? previousHolder : holder;
                    }
                    detail["reason"] = "scene_" + operation;
                    continue;
                }
                if (operation != "consume")
                {
                    if (beverage.Name.Length == 0 || beverage.Alcohol == DrinkAlcohol.Unknown || beverage.Alcohol == DrinkAlcohol.VariantRequired)
                    { diagnostics.Add(TestDict("id", localId, "reason", "scene_contents_unresolved")); complete = false; }
                    bool created = record == null;
                    if (created) record = NewPrivateDrinkVessel(scene, reference, holder, player, beverage, kind, turn);
                    if (record == null) { Skip("scene_capacity_or_identity_unavailable"); continue; }
                    if (!created && operation == "establish") { detail["reason"] = "cup_already_known"; continue; }
                    record["beverage"] = beverage.Name; record["family"] = beverage.Family; record["alcohol"] = beverage.Alcohol.ToString();
                    if (!created) record["contentsVersion"] = ReadInt(record, "contentsVersion", 0) + 1;
                    record["remaining"] = OrdinaryDrinkVessel(ReadString(record, "kind", ""))
                        ? PrivateDrinkQuantity(PrivateDrinkText(item, "quantity")) : null;
                    detail["vesselId"] = ReadString(record, "id", "");
                    detail["reason"] = "scene_" + operation;
                    continue;
                }
                string serving = PrivateDrinkText(item, "serving");
                double quantity = DrinkingUnit(serving);
                double count = item.TryGetValue("count", out var rawCount) && rawCount != null ? ReadDouble(item, "count", double.NaN) : 1;
                if (!FiniteDrinkNumber(count) || count < 1 || count != Math.Floor(count)) { Skip("invalid_consumption_count", true); continue; }
                if (count > 20) diagnostics.Add(TestDict("id", localId, "reason", "consumption_count_capped", "limit", 20));
                count = Math.Min(count, 20);
                double? remaining = record == null ? null : ReadDrinkVessel(record).Remaining;
                if (record != null && beverageName.Length > 0 && remaining != 0
                    && (ReadString(record, "family", "") != beverage.Family || ReadString(record, "alcohol", "") != beverage.Alcohol.ToString()))
                {
                    // A changed explicit identity supersedes incomplete bookkeeping;
                    // the old liquid's measured remainder cannot describe this one.
                    remaining = null;
                    diagnostics.Add(TestDict("id", localId, "reason", "contents_changed_without_refill_record"));
                }
                if (serving == "finish") {
                    quantity = remaining ?? .25d;
                    count = 1;
                    if (!remaining.HasValue) diagnostics.Add(TestDict("id", localId, "reason", "finish_volume_estimated"));
                }
                if (quantity <= 0 && remaining != 0) { Skip("invalid_consumption_portion", true); continue; }
                if (remaining == 0) { Skip("vessel_empty"); continue; }
                double amount = remaining.HasValue ? Math.Min(quantity * count, remaining.Value) : quantity * count;
                detail["beverage"] = beverage.Name; detail["family"] = beverage.Family; detail["alcohol"] = beverage.Alcohol.ToString();
                detail["quantitySource"] = serving == "finish" ? "remaining_or_estimated_partial" : "private_portion";
                detail["measurementQuality"] = "estimated_gameplay_portion";
                detail["remainingBefore"] = remaining;
                if (beverage.Alcohol == DrinkAlcohol.Unknown || beverage.Alcohol == DrinkAlcohol.VariantRequired)
                {
                    detail["reason"] = "consumption_contents_unresolved";
                    diagnostics.Add(TestDict("id", localId, "reason", "consumption_contents_unresolved")); complete = false;
                }
                if (beverage.Alcohol == DrinkAlcohol.Yes && state.AlcoholBlocked) { Skip("alcohol_limit"); continue; }
                double accepted = beverage.Alcohol == DrinkAlcohol.Yes ? Math.Min(amount, Math.Max(0, state.Threshold - state.Drinks)) : amount;
                detail["consumptionEvents"] = (int)count; detail["acceptedConsumptionEvents"] = (int)count;
                events += (int)count;
                if (beverage.Alcohol == DrinkAlcohol.Yes) {
                    proposed += amount; consumed += accepted; state.Drinks += accepted; alcoholEvents += (int)count;
                    detail["amount"] = amount; detail["acceptedAmount"] = accepted;
                    if (state.Drinks >= state.Threshold) state.AlcoholBlocked = true;
                    if (accepted < amount) diagnostics.Add(TestDict("id", localId, "reason", "portion_capped_at_tolerance"));
                }
                else if (beverage.Alcohol == DrinkAlcohol.No) detail["reason"] = "nonalcoholic";
                // An identified completed drink can repair missing cup bookkeeping.
                // Its identity comes from the action, not another scan of the prose.
                if (record == null) record = NewPrivateDrinkVessel(scene, reference.Length > 0 ? reference : "held_" + hero,
                    hero, player, beverage, kind, turn);
                if (record != null) {
                    if (ReadString(record, "beverage", "") != beverage.Name) record["contentsVersion"] = ReadInt(record, "contentsVersion", 0) + 1;
                    record["beverage"] = beverage.Name; record["family"] = beverage.Family; record["alcohol"] = beverage.Alcohol.ToString();
                    record["remaining"] = remaining.HasValue ? Math.Max(0, remaining.Value - accepted) : null;
                    detail["vesselId"] = ReadString(record, "id", ""); detail["remainingAfter"] = record["remaining"];
                }
            }
            state.Drinks = Math.Round(state.Drinks, 8);
            state.ConsumptionEvents += events; state.AlcoholConsumptionEvents += alcoholEvents;
            state.IncompleteEventCount |= !complete;
            if (!stale && DrinkSceneDeparture(payload, parsed, hero, reply)) scene.Vessels.Clear();
            state.Vessel = SelectSceneDrink(scene, hero);
            var receipt = TestDict("schema", "reign-drinking-receipt-v1", "accountingMode", PrivateDrinkActionMode,
                "heroStringId", hero, "turnKey", turn, "proposed", Math.Round(proposed, 8), "consumed", Math.Round(consumed, 8),
                "staleClock", stale, "state", IntoxicationRecord(state), "evidence", evidence, "diagnostics", diagnostics,
                "eventAccounting", TestDict("consumptionEvents", events, "alcoholConsumptionEvents", alcoholEvents,
                    "eventCountComplete", complete, "amountUnit", "gameplay_serving_equivalent", "estimatedAlcoholServings", consumed,
                    "alcoholVolumeMl", null, "standardDrinkCount", null),
                "reply", reply, "suppressedCommitments", state.Ratio >= 1, "baseStateHash", PromptHash(CanonicalJson(stored)),
                "proposedReplyHash", PromptHash(reply), "actionMetadataHash", PrivateDrinkActionHash(parsed),
                "consumptionMetadataHash", PromptHash(CanonicalJson(ReadDictionaryList(parsed, "drinkConsumptions"))),
                "sceneMetadataHash", DrinkSceneMetadataHash(parsed), "commitState", "prepared", "additionalProviderCalls", 0,
                "dialogueBlocked", false);
            if (scene.Scope.Length > 0 && !stale) {
                receipt["drinkScene"] = DrinkSceneRecord(scene); receipt["baseDrinkSceneHash"] = scene.BaseHash;
            }
            return receipt;
        }

        private static void SuppressPrivateDrinkCommitments(Dictionary<string, object> parsed)
        {
            // Keep the character's authored reply and private drink choices. Only
            // executable commitments are disabled by the existing physical limit.
            parsed["actionGate"] = TestDict("needed", false, "commitment", "roleplay_only", "intent", "intoxicated_reaction");
            parsed["suggestedActions"] = new List<Dictionary<string, object>>();
            parsed["proposalDecisions"] = new List<Dictionary<string, object>>();
            parsed.Remove("structuredActionDecision");
        }

        private static string ApplyConversationDrinking(string campaign, string hero, Dictionary<string, object> payload,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> parsed,
            string reply, bool persist = true)
        {
            if (payload != null) payload.Remove("intoxicationReceipt");
            if (parsed == null || payload == null || string.IsNullOrWhiteSpace(reply)
                || reply.StartsWith("[Bannerlord Reign:", StringComparison.Ordinal)
                || ReadBool(payload, "governmentCertificationMemoryIsolation", false)
                || ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId").Length == 0) return reply;
            lock (CampaignRelationshipWriteLock(campaign))
            using (var connection = OpenCampaignConnection(campaign))
            {
                EnsureIntoxicationSchema(connection);
                using var transaction = connection.BeginTransaction();
                var args = TestDict("hero", hero, "turn", ReadString(payload, "timelineId", "main") + ":"
                    + ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId"));
                ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_intoxication(hero_id,state_json) VALUES($hero,'{}');", args);
                QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero FOR UPDATE;", args);
                var previous = QuerySql(connection, "SELECT receipt_json FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;", args).SingleOrDefault();
                var receipt = previous != null ? TryParseJsonObject(ReadString(previous, "receipt_json", "{}"))
                    : PreparePrivateDrinkReceipt(connection, hero, payload, profile, characteristics, parsed, reply, lockScene: persist);
                if (ReadBool(receipt, "suppressedCommitments", false)) SuppressPrivateDrinkCommitments(parsed);
                if (persist && previous == null) {
                    receipt["commitState"] = "committed"; receipt["acceptedReplyHash"] = PromptHash(reply);
                    args["state"] = Json.Serialize(ReadDictionary(receipt, "state")); args["receipt"] = Json.Serialize(receipt);
                    CommitDrinkScene(connection, receipt);
                    ExecuteSql(connection, "UPDATE conversation_intoxication SET state_json=$state WHERE hero_id=$hero;", args);
                    ExecuteSql(connection, "INSERT INTO conversation_drinking_turns(hero_id,turn_key,receipt_json) VALUES($hero,$turn,$receipt);", args);
                }
                if (persist) transaction.Commit(); else transaction.Rollback();
                payload["intoxicationReceipt"] = receipt;
            }
            // No canned refusal, prose repair or regeneration belongs to drinking.
            return reply;
        }
    }
}
