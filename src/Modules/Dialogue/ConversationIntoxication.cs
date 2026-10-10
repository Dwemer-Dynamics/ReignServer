using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ReignBeta.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string DrinkingOutputContract = @"DRINKING — PRIVATE ACTIONS, CHARACTER-AUTHORED PROSE
Choose drinking as part of this ordinary response and describe it naturally in your character's own style. Optional observable narration uses single *asterisks*, speech outside. A lingering taste, a hurried mouthful or an absent-minded sip may flow within a longer paragraph; no fixed phrase, separate action sentence or visible action menu is required. Keep these private fields out of the visible reply.
drinkActions is the sole drinking record: [{id:'sip_1',operation:'consume',holderHeroId:'speaking NPC id',vesselId:'supplied id or local cup reference',contentsVersion:0,beverage:'wine',kind:'cup',quantity:'unknown',serving:'sip',count:1,completed:true}]. Use [] when no drinking or cup change occurs. Include each actual consumption exactly once alongside your prose; a sip and its swallowing are one action. Separate completed sips need different ids. Maximum 16 actions, applied in listed order. No automatic drinking on every turn and no between-turn drinking. The same response carries prose and actions; no drinking judge, repair or extra provider call is requested.
consume belongs only to the speaking NPC. Offers, refusals, other participants drinking, history, quotations, intentions, hypotheticals and uncertainty are not completed consumption. An offer never forces acceptance. serving sip/mouthful/pull/swig/gulp/draught uses 'sip' (0.25 gameplay serving), 'half'=0.5, 'drink'=1 ordinary serving, 'finish'=known remainder or a conservative partial estimate when unknown. count=1..20. A known empty cup cannot be consumed. Bulk containers never imply an ordinary full serving.
Always identify the consumed beverage when known, even if a cup id is missing. The engine classifies alcohol; wine/ale/beer/mead and catalogue variants retain their identity, water adds no intoxication, unspecified kvass/boza remains unresolved. Do not invent alcohol percentages or volume. Copy known vessel ids; local references may establish a missing cup without a prose quote or exact wording. contentsVersion=0 means unspecified; old revisions are diagnostic and do not request a rewrite. A missing cup record does not cancel an explicitly identified completed drink.
Other operations are establish/refill/transfer/discard. Establish separate cups for completed environmental service to each present recipient; these operations never consume. holderHeroId is the recipient/current holder (transfer destination). Use the supplied id or a stable local reference; kind is cup/goblet/mug/etc. quantity unknown/full/half/quarter/empty describes actual contents; ordinary full cups hold one gameplay serving, bulk capacity stays unknown. Refill replaces contents and increments the revision; transfer retains identity and remainder. List a transfer or accepted offered cup before consuming from it. Do not complete another participant's choices. Leave legacy drinkingEvents, drinkConsumptions and drinkSceneUpdates empty; they are ignored.
Engine owns intoxication, recovery and tolerance; stateUpdates cannot change them. Obey no-more-alcohol until released and express impairment/refusal in character. Drinking metadata problems only produce local diagnostics; they never block this reply or request a repair.";
        private sealed class IntoxicationState
        {
            public double Drinks;
            public double Hour;
            public bool HasClock;
            public bool AlcoholBlocked;
            public double Threshold;
            public DrinkVesselState Vessel = new DrinkVesselState();
            public long ConsumptionEvents, AlcoholConsumptionEvents;
            public bool IncompleteEventCount;
            public double EventCountSinceHour;
            public double Ratio => Drinks / Threshold;
            public string Stage => Drinks <= 0 ? "sober" : Ratio < .25 ? "mildly affected"
                : Ratio < .5 ? "tipsy" : Ratio < .75 ? "very intoxicated"
                : Ratio < 1 ? "severely intoxicated" : "overwhelmed";
        }

        private static double DrinkingThreshold(double endurance) => Math.Max(5d, FiniteNonnegative(endurance) * 2d);
        private static double FiniteNonnegative(double n) => double.IsNaN(n) || double.IsInfinity(n) ? 0 : Math.Max(0, n);

        private static IntoxicationState RecoverIntoxication(Dictionary<string, object> stored, double endurance, double hour)
        {
            stored = stored ?? new Dictionary<string, object>();
            bool validClock = !double.IsNaN(hour) && !double.IsInfinity(hour) && hour >= 0;
            double previousHour = ReadDouble(stored, "hour", hour);
            bool hasClock = ReadBool(stored, "hasClock", false) && !double.IsNaN(previousHour) && !double.IsInfinity(previousHour);
            var state = new IntoxicationState
            {
                Drinks = FiniteNonnegative(ReadDouble(stored, "drinks", 0)),
                Threshold = DrinkingThreshold(endurance),
                Hour = validClock ? Math.Max(hasClock ? previousHour : hour, hour) : (hasClock ? previousHour : 0),
                HasClock = validClock || hasClock,
                AlcoholBlocked = ReadBool(stored, "alcoholBlocked", false),
                Vessel = ReadDrinkVessel(ReadDictionary(stored, "vessel")),
                ConsumptionEvents = Math.Max(0, ReadLong(stored, "consumptionEvents", 0)),
                AlcoholConsumptionEvents = Math.Max(0, ReadLong(stored, "alcoholConsumptionEvents", 0)),
                IncompleteEventCount = ReadBool(stored, "incompleteEventCount", false),
                EventCountSinceHour = ReadDouble(stored, "eventCountSinceHour", validClock ? hour : 0)
            };
            if (validClock && hasClock) state.Drinks = Math.Max(0, state.Drinks - Math.Max(0, hour - previousHour) / 2d);
            state.Drinks = Math.Round(state.Drinks, 8, MidpointRounding.AwayFromZero);
            if (state.Drinks < state.Threshold / 2d) state.AlcoholBlocked = false;
            if (state.Drinks >= state.Threshold) state.AlcoholBlocked = true;
            return state;
        }

        private static Dictionary<string, object> IntoxicationRecord(IntoxicationState state) => new Dictionary<string, object>
        {
            ["schema"] = "reign-conversation-intoxication-v1", ["drinks"] = state.Drinks,
            ["hour"] = state.Hour, ["hasClock"] = state.HasClock,
            ["threshold"] = state.Threshold, ["ratio"] = state.Ratio,
            ["stage"] = state.Stage, ["alcoholBlocked"] = state.AlcoholBlocked,
            ["vessel"] = DrinkVesselRecord(state.Vessel),
            ["consumptionEvents"] = state.ConsumptionEvents, ["alcoholConsumptionEvents"] = state.AlcoholConsumptionEvents,
            ["incompleteEventCount"] = state.IncompleteEventCount, ["eventCountSinceHour"] = state.EventCountSinceHour,
            ["drinksUnit"] = "gameplay_serving_equivalent"
        };

        private static Dictionary<string, object> BindAcceptedDrinkEvents(List<Dictionary<string, object>> evidence,
            string hero, string turn, bool stale, bool suppressed, double consumed)
        {
            int events = 0, alcoholEvents = 0;
            bool complete = !(suppressed && consumed > 0);
            foreach (var detail in evidence)
            {
                string reason = ReadString(detail, "reason", "");
                int count = Math.Clamp(ReadInt(detail, "consumptionEvents", 0), 0, 20);
                bool accepted = !stale && !suppressed && count > 0 && reason != "vessel_empty"
                    && ReadDouble(detail, "remainingBefore", double.NaN) != 0;
                detail["drinkerId"] = hero;
                detail["eventId"] = "drink_" + PromptHash(hero + "|" + turn + "|" + ReadInt(detail, "actionIndex", -1)
                    + "|" + ReadInt(detail, "occurrenceIndex", -1)).Substring(0, 28);
                detail["acceptedConsumptionEvents"] = accepted ? count : 0;
                detail["eventCountStatus"] = complete ? "measured_from_accepted_actions" : "unmeasured_after_engine_replacement";
                detail["amountUnit"] = "gameplay_serving_equivalent";
                // Narrative serving estimates are not physical volume or a known
                // alcohol dose. Keep these explicitly unavailable.
                detail["alcoholVolumeMl"] = null;
                detail["standardDrinkCount"] = null;
                if (!accepted) continue;
                events += count;
                if (ReadString(detail, "alcohol", "") == DrinkAlcohol.Yes.ToString()) alcoholEvents += count;
            }
            return TestDict("consumptionEvents", events, "alcoholConsumptionEvents", alcoholEvents,
                "eventCountComplete", complete, "amountUnit", "gameplay_serving_equivalent",
                "estimatedAlcoholServings", consumed, "alcoholVolumeMl", null, "standardDrinkCount", null);
        }

        private static double IntoxicationWorldHour(Dictionary<string, object> payload)
        {
            double day = ReadDouble(payload, "worldDay", double.NaN);
            return day >= 0 && !double.IsInfinity(day) ? day * 24d : double.NaN;
        }

        private static void EnsureIntoxicationSchema(ReignDbConnection connection)
        {
            // Campaign schemas and their tables are cloned atomically by Save Sync.
            // Restored state seeds a new history branch instead of resetting sobriety.
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_intoxication (
hero_id TEXT PRIMARY KEY, state_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE IF NOT EXISTS conversation_drinking_turns (
hero_id TEXT NOT NULL, turn_key TEXT NOT NULL, receipt_json TEXT NOT NULL,
PRIMARY KEY(hero_id,turn_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_drink_scene (
scene_id TEXT PRIMARY KEY, state_json TEXT NOT NULL DEFAULT '{}');");
        }

        private static double IntoxicationEndurance(Dictionary<string, object> profile, Dictionary<string, object> characteristics)
            => ReadDouble(ReadDictionary(profile, "attributes")
                ?? ReadDictionary(ReadDictionary(profile, "sourceFacts"), "attributes"), "endurance", 0);

        private static string BuildIntoxicationPrompt(string campaignId, string heroId, Dictionary<string, object> payload,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            Dictionary<string, object> stored;
            DrinkScene scene;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureIntoxicationSchema(connection);
                var row = QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero;",
                    new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
                stored = TryParseJsonObject(ReadString(row, "state_json", "{}"));
                scene = LoadDrinkScene(connection, payload, IntoxicationWorldHour(payload));
            }
            var state = RecoverIntoxication(stored, IntoxicationEndurance(profile, characteristics), IntoxicationWorldHour(payload));
            state.Vessel = scene.Scope.Length > 0 ? SelectSceneDrink(scene, heroId, preferredId: state.Vessel.Id)
                : CurrentDrinkVessel(state.Vessel, payload, IntoxicationWorldHour(payload));
            state.Vessel.IdentitySeed = heroId + "|" + ReadString(payload, "timelineId", "main") + ":" + ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId");
            IdentifyDrinkVessel(state.Vessel, "legacy_record");
            var offer = CurrentPlayerDrinkOffer(payload, state.Vessel,
                profile != null && profile.ContainsKey("isFemale") ? (bool?)ReadBool(profile, "isFemale", false) : null);
            payload["intoxicationContext"] = IntoxicationRecord(state);
            if (offer != null) ReadDictionary(payload, "intoxicationContext")["availableDrink"] = DrinkVesselRecord(offer.Vessel);
            if (scene.Scope.Length > 0) ReadDictionary(payload, "intoxicationContext")["drinkScene"] = DrinkSceneRecord(scene);
            // The independently budgeted drink packet projects the shared scene
            // once. This mandatory physical block always retains the held/offer cup.
            return IntoxicationPrompt(state, offer?.Vessel);
        }

        private static string IntoxicationPrompt(IntoxicationState state, DrinkVesselState available = null)
        {
            string symptoms = state.Stage == "sober" ? "No alcohol impairment."
                : state.Ratio < .25 ? "Subtle warmth and relaxed restraint; still attentive and coordinated."
                : state.Ratio < .5 ? "Noticeably expressive and less restrained, occasional coordination or wording mistakes."
                : state.Ratio < .75 ? "VERY INTOXICATED: poor judgment, wandering attention, obvious physical tells and some slurring."
                : state.Ratio < 1 ? "SEVERELY INTOXICATED: fragmented speech, confused attention, struggling to stay upright and awake."
                : "OVERWHELMED: very sick, drifting in and out of consciousness. Only a brief mumble or physical reaction, no sustained coherent speech or finalized commitments.";
            return "AUTHORITATIVE CURRENT INTOXICATION (private engine state; never speak numbers or skill levels): "
                + Json.Serialize(IntoxicationRecord(state)) + "\n" + symptoms
                + " Strengthen these symptoms as the exact ratio rises, even within the same stage. Preserve personality, motives and boundaries. Do not turn everyone into a caricature, repeat identical gestures, require vomiting every turn, or spell every word as a slur. Chat turns do not sober you."
                + " The stored drink count is for this NPC only; it is not a measured count for the player or other characters."
                + " The vessel block retains identified drinks across chat reopening. Respect known custody and empty cups; record accepted transfers before consumption. A null remaining amount is unknown, not a full cup. Missing cup bookkeeping does not erase an explicitly identified private consume action. Refills change contentsVersion without consumption; finishing a known cup uses its remainder."
                + (available == null ? "" : "\nAVAILABLE DRINK offered by the player (choice remains yours): " + Json.Serialize(DrinkVesselRecord(available)))
                + (state.AlcoholBlocked ? " NO MORE ALCOHOL: unable to consume any, including sips, until the engine releases this restriction. Refuse or physically fail any offer."
                    : " Drinking remains a character choice; an offer never forces acceptance.");
        }


        private static void RebindDrinkingEventsAfterCleanup(Dictionary<string, object> parsed, string before, string after, string heroName)
        {
            var original = ReignActionText.Parse(before).Where(s => s.IsAction).Select(s => s.Text.Trim()).ToList();
            var final = ReignActionText.Parse(after).Where(s => s.IsAction).Select(s => s.Text.Trim()).ToList();
            var rebound = new List<Dictionary<string, object>>();
            foreach (var item in ReadDictionaryList(parsed, "drinkingEvents").Take(16))
            {
                int oldIndex = ReadInt(item, "actionIndex", -1);
                string quote = ReadString(item, "actionQuote", "").Trim().Trim('*').Trim();
                if (quote.Length == 0 && oldIndex >= 0 && oldIndex < original.Count) quote = original[oldIndex];
                var indices = final.Select((text, index) => new { text, index }).Where(row => row.text == quote).ToList();
                if (quote.Length == 0 || indices.Count != 1) continue;
                var copy = new Dictionary<string, object>(item) { ["actionIndex"] = indices[0].index };
                if (ReadNarratedDrink(quote, heroName) != null || !string.IsNullOrWhiteSpace(ReadString(item, "actionQuote", "")))
                    copy["actionQuote"] = quote;
                rebound.Add(copy);
            }
            parsed["drinkingEvents"] = rebound;
        }

        private static string OverwhelmedReply(bool justDrank) => justDrank
            ? "*After drinking, they slump forward, too sick to remain upright. Their eyes drift shut.* \"No more...\""
            : "*Their eyes briefly open before closing again. Too sick to sit upright, they stir and mumble.* \"No more...\"";

        private static void SuppressIntoxicatedCommitments(Dictionary<string, object> parsed)
        {
            if (parsed == null) return;
            parsed.Clear();
            parsed["intent"] = "intoxicated_reaction";
            parsed["actionGate"] = new Dictionary<string, object> { ["needed"] = false };
            parsed["drinkingEvents"] = new List<Dictionary<string, object>>();
            parsed["drinkConsumptions"] = new List<Dictionary<string, object>>();
        }

        // Historical parser fixtures only. Live conversation routes use private drinkActions.
        private static string ApplyLegacyConversationDrinking(string campaignId, string heroId, Dictionary<string, object> payload,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> parsed, string reply,
            bool persist = true)
        {
            // A reused request may carry an earlier draft's preview. Clear it even
            // when the accepted response is silent, malformed or isolated.
            if (!persist && payload != null) payload.Remove("intoxicationReceipt");
            // No state mutation on malformed/quiet or certification-isolated turns.
            if (parsed == null || string.IsNullOrWhiteSpace(reply) || reply.StartsWith("[Bannerlord Reign:", StringComparison.Ordinal)
                || ReadBool(payload, "governmentCertificationMemoryIsolation", false)) return reply;
            string identity = ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId");
            if (string.IsNullOrWhiteSpace(identity)) return reply;
            string key = ReadString(payload, "timelineId", "main") + ":" + identity;
            double hour = IntoxicationWorldHour(payload);
            bool missingClock = double.IsNaN(hour) || double.IsInfinity(hour);
            string proposedReply = reply;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureIntoxicationSchema(connection);
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    var args = new Dictionary<string, object> { ["hero"] = heroId, ["turn"] = key };
                    ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_intoxication(hero_id,state_json) VALUES($hero,'{}');", args);
                    var row = QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero FOR UPDATE;", args).First();
                    var previous = QuerySql(connection, "SELECT receipt_json FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;", args).FirstOrDefault();
                    if (previous != null)
                    {
                        var receipt = TryParseJsonObject(ReadString(previous, "receipt_json", "{}"));
                        payload["intoxicationReceipt"] = receipt;
                        if (ReadBool(receipt, "suppressedCommitments", false)) SuppressIntoxicatedCommitments(parsed);
                        if (persist) transaction.Commit(); else transaction.Rollback();
                        return ReadString(receipt, "reply", reply);
                    }
                    var stored = TryParseJsonObject(ReadString(row, "state_json", "{}"));
                    var state = RecoverIntoxication(stored, IntoxicationEndurance(profile, characteristics), hour);
                    var evidence = new List<Dictionary<string, object>>();
                    var scene = LoadDrinkScene(connection, payload, hour);
                    var proposedVessel = CurrentDrinkVessel(state.Vessel, payload, hour);
                    if (scene.Scope.Length > 0) proposedVessel = SelectSceneDrink(scene, heroId, parsed, state.Vessel.Id);
                    proposedVessel.IdentitySeed = heroId + "|" + key;
                    IdentifyDrinkVessel(proposedVessel, "legacy_record");
                    var offer = CurrentPlayerDrinkOffer(payload, proposedVessel,
                        profile != null && profile.ContainsKey("isFemale") ? (bool?)ReadBool(profile, "isFemale", false) : null);
                    var offeredBeverage = CurrentPlayerOfferedBeverage(payload);
                    var sceneObservations = scene.Scope.Length == 0 ? new List<DrinkSceneObservation>() : DrinkSceneObservations(parsed, reply, payload, heroId,
                        profile != null && profile.ContainsKey("isFemale") ? (bool?)ReadBool(profile, "isFemale", false) : null);
                    // Invalid observation metadata must not suppress independently
                    // supported legacy refills/handoffs in the same paragraph.
                    var observationCheckScene = new DrinkScene { Scope = scene.Scope, Hour = scene.Hour,
                        Vessels = scene.Vessels.Select(CloneDictionary).ToList() };
                    sceneObservations = sceneObservations.Where(o => ApplyDrinkSceneObservation(observationCheckScene, o,
                        payload, heroId, key, new List<Dictionary<string, object>>())).ToList();
                    var coveredSceneObservations = new List<DrinkSceneObservation>();
                    int nextObservation = 0;
                    string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                    string selectedSceneId = proposedVessel.Id;
                    Action<int> advanceScene = position =>
                    {
                        while (nextObservation < sceneObservations.Count && sceneObservations[nextObservation].End <= position)
                        {
                            var observation = sceneObservations[nextObservation++];
                            StoreSceneDrink(scene, proposedVessel, selectedSceneId, heroId, playerId);
                            string preferredId = proposedVessel.Id;
                            if (ApplyDrinkSceneObservation(scene, observation, payload, heroId, key, evidence))
                            {
                                coveredSceneObservations.Add(observation);
                                if (ReadString(observation.Item, "holderHeroId", "") == heroId
                                    && scene.Aliases.TryGetValue(ReadString(observation.Item, "vesselId", ""), out string established)) preferredId = established;
                            }
                            CopyDrinkVessel(proposedVessel, SelectSceneDrink(scene, heroId, parsed, preferredId));
                            selectedSceneId = proposedVessel.Id;
                        }
                    };
                    Func<int, bool> sceneCovered = position => coveredSceneObservations.Any(o => position >= o.Start && position <= o.End)
                        || sceneObservations.Skip(nextObservation).Any(o => position >= o.Start && position <= o.End);
                    double proposed = ValidatedDrinkAmount(parsed, reply, evidence, ReadFirstString(profile, "name", "heroName"),
                        profile != null && profile.ContainsKey("isFemale") ? (bool?)ReadBool(profile, "isFemale", false) : null,
                        proposedVessel, offeredBeverage, offer, scene.Scope.Length == 0 ? null : advanceScene, scene.Scope.Length == 0 ? null : sceneCovered);
                    if (scene.Scope.Length > 0) advanceScene(reply.Length);
                    var consumptionCandidate = CloneDictionary(parsed);
                    foreach (var item in ReadDictionaryList(consumptionCandidate, "drinkConsumptions"))
                        if (scene.Aliases.TryGetValue(ReadString(item, "vesselId", ""), out string id)) item["vesselId"] = id;
                    proposed += ValidateQuietDrinkConsumption(consumptionCandidate, reply, heroId, proposedVessel, offer, evidence);
                    bool staleClock = missingClock || ReadBool(stored, "hasClock", false) && hour < ReadDouble(stored, "hour", 0) - .000001
                        || scene.Scope.Length > 0 && hour < ReadDouble(scene.Stored, "hour", double.NaN) - .000001;
                    double consumed = staleClock || state.AlcoholBlocked ? 0 : Math.Min(proposed, Math.Max(0, state.Threshold - state.Drinks));
                    state.Drinks += consumed;
                    bool suppress = state.Ratio >= 1 || ((state.AlcoholBlocked || staleClock) && proposed > 0);
                    if (state.Ratio >= 1)
                    {
                        state.AlcoholBlocked = true;
                        reply = OverwhelmedReply(consumed > 0);
                    }
                    else if ((state.AlcoholBlocked || staleClock) && proposed > 0)
                        reply = "*They push the drink away, leaving it untouched.* \"No more.\"";
                    // The entire proposed narration is replaced when the engine refuses
                    // it. Never persist unseen refills, transfers, or future/stale cups.
                    if (!staleClock && !suppress) state.Vessel = proposedVessel;
                    else if (!staleClock && consumed > 0) state.Vessel = new DrinkVesselState();
                    double unassigned = consumed;
                    foreach (var detail in evidence)
                    {
                        double accepted = Math.Min(unassigned, ReadDouble(detail, "amount", 0));
                        detail["acceptedAmount"] = accepted;
                        detail["vesselChangesAccepted"] = !staleClock && !suppress;
                        unassigned = Math.Max(0, unassigned - accepted);
                    }
                    if (suppress) SuppressIntoxicatedCommitments(parsed);
                    var eventAccounting = BindAcceptedDrinkEvents(evidence, heroId, key, staleClock, suppress, consumed);
                    state.ConsumptionEvents += ReadInt(eventAccounting, "consumptionEvents", 0);
                    state.AlcoholConsumptionEvents += ReadInt(eventAccounting, "alcoholConsumptionEvents", 0);
                    state.IncompleteEventCount |= !ReadBool(eventAccounting, "eventCountComplete", true);
                    parsed["reply"] = reply;
                    var receiptNew = new Dictionary<string, object>
                    {
                        ["schema"] = "reign-drinking-receipt-v1", ["accountingMode"] = "legacy_narration_v1", ["heroStringId"] = heroId, ["turnKey"] = key,
                        ["proposed"] = proposed, ["consumed"] = consumed, ["staleClock"] = staleClock,
                        ["evidence"] = evidence, ["state"] = IntoxicationRecord(state), ["eventAccounting"] = eventAccounting,
                        ["suppressedCommitments"] = suppress, ["reply"] = reply,
                        ["baseStateHash"] = PromptHash(CanonicalJson(stored)),
                        ["proposedReplyHash"] = PromptHash(proposedReply),
                        ["consumptionMetadataHash"] = PromptHash(CanonicalJson(ReadDictionaryList(parsed, "drinkConsumptions"))),
                        ["sceneMetadataHash"] = DrinkSceneMetadataHash(parsed),
                        ["commitState"] = persist ? "committed" : "prepared"
                    };
                    if (scene.Scope.Length > 0 && !staleClock && !suppress)
                    {
                        StoreSceneDrink(scene, proposedVessel, selectedSceneId, heroId, playerId);
                        // An accepted move ends this physical scene; discarded drafts
                        // must never empty the shared scene or resurrect its cups.
                        if (DrinkSceneDeparture(payload, parsed, heroId, reply))
                            scene.Vessels.Clear();
                        receiptNew["drinkScene"] = DrinkSceneRecord(scene);
                        receiptNew["baseDrinkSceneHash"] = scene.BaseHash;
                    }
                    args["state"] = Json.Serialize(IntoxicationRecord(state));
                    args["receipt"] = Json.Serialize(receiptNew);
                    if (persist)
                    {
                        CommitDrinkScene(connection, receiptNew);
                        ExecuteSql(connection, "UPDATE conversation_intoxication SET state_json=$state WHERE hero_id=$hero;", args);
                        ExecuteSql(connection, "INSERT INTO conversation_drinking_turns(hero_id,turn_key,receipt_json) VALUES($hero,$turn,$receipt);", args);
                        transaction.Commit();
                    }
                    else transaction.Rollback();
                    payload["intoxicationReceipt"] = receiptNew;
                    if (persist && evidence.Count > 0)
                        WriteAudit(campaignId, ReadString(payload, "correlationId", identity), "server",
                            ReadString(payload, "mode", "dialogue"), "drinking.receipt", heroId, "", "", "completed", 0,
                            "Narrated drinking adjudicated against the accepted reply.", receiptNew);
                    return reply;
                }
            }
        }
    }
}
