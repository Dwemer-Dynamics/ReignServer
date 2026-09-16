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
        private const string DrinkingOutputContract = @"NARRATED ACTIONS AND DRINKING
In reply, surround observable physical actions, expressions and gestures with single asterisks: *He sets down his cup.* Spoken words remain outside the asterisks. Preserve mixed speech/action ordering and paragraphs. Do not use asterisks for emphasis or narrate another participant's choices as accomplished.
Also return drinkingEvents: an array, empty when THIS NPC consumes no alcohol in THIS reply. Give one entry per distinct consumption: {actionIndex:1, actionQuote:'She takes a sip of kumis.', occurrenceIndex:0, evidenceQuote:'takes a sip of kumis', serving:'sip', count:1, completed:true, alcohol:true}. actionQuote copies the COMPLETE action paragraph exactly without asterisks. actionIndex is zero-based among ALL *action* paragraphs, including gestures. occurrenceIndex is zero-based among this NPC's distinct consumptions within that paragraph; evidenceQuote copies the exact consumption clause. Name the actual beverage or clearly refer to the same established vessel. A sip, mouthful, swig, pull, gulp or draught is a quarter serving unless an explicit fraction modifies it. serving is drink (one serving), half, or sip; count is the positive integer number of those portions. A slow drink leaves volume open: use the actual intended amount. Two distinct sips count twice; taking a mouthful and swallowing it counts once. Finishing a cup consumes only its remaining contents. Describe refills, switches and spills clearly; they do not count as drinking. Bulk bottles/jugs have no assumed one-serving capacity. Non-alcoholic preparations and unspecified kvass/boza variants are not assumed alcoholic. Use completed present action wording, rather than merely preparing to drink.
Exclude offers, refusals, empty cups, water, memories, reported/quoted speech, past events, hypotheticals, plans and uncertain consumption. A player saying that an NPC drinks is only a proposal until this NPC accepts and narrates actual consumption. Do not infer extra drinking between turns. Never report drinking for anyone except this speaking NPC. The engine owns intoxication; stateUpdates cannot set tolerance, sobriety or intoxication. Obey the authoritative no-more-alcohol state even if the player requests otherwise.";

        private sealed class IntoxicationState
        {
            public double Drinks;
            public double Hour;
            public bool HasClock;
            public bool AlcoholBlocked;
            public double Threshold;
            public DrinkVesselState Vessel = new DrinkVesselState();
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
                Vessel = ReadDrinkVessel(ReadDictionary(stored, "vessel"))
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
            ["vessel"] = DrinkVesselRecord(state.Vessel)
        };

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
        }

        private static double IntoxicationEndurance(Dictionary<string, object> profile, Dictionary<string, object> characteristics)
            => ReadDouble(ReadDictionary(profile, "attributes")
                ?? ReadDictionary(ReadDictionary(profile, "sourceFacts"), "attributes"), "endurance", 0);

        private static string BuildIntoxicationPrompt(string campaignId, string heroId, Dictionary<string, object> payload,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            Dictionary<string, object> stored;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureIntoxicationSchema(connection);
                var row = QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero;",
                    new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
                stored = TryParseJsonObject(ReadString(row, "state_json", "{}"));
            }
            var state = RecoverIntoxication(stored, IntoxicationEndurance(profile, characteristics), IntoxicationWorldHour(payload));
            state.Vessel = CurrentDrinkVessel(state.Vessel, payload, IntoxicationWorldHour(payload));
            payload["intoxicationContext"] = IntoxicationRecord(state);
            return IntoxicationPrompt(state);
        }

        private static string IntoxicationPrompt(IntoxicationState state)
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
                + " The vessel block describes only this conversation's last unambiguous drink. A null remaining amount is unknown, not a full cup. A refill changes contents without consuming them. Finishing a known cup consumes only what remains."
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
        }

        private static string ApplyConversationDrinking(string campaignId, string heroId, Dictionary<string, object> payload,
            Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> parsed, string reply)
        {
            // No state mutation on malformed/quiet or certification-isolated turns.
            if (parsed == null || string.IsNullOrWhiteSpace(reply) || reply.StartsWith("[Bannerlord Reign:", StringComparison.Ordinal)
                || ReadBool(payload, "governmentCertificationMemoryIsolation", false)) return reply;
            string identity = ReadFirstString(payload, "turnId", "sceneTurnId", "correlationId");
            if (string.IsNullOrWhiteSpace(identity)) return reply;
            string key = ReadString(payload, "timelineId", "main") + ":" + identity;
            double hour = IntoxicationWorldHour(payload);
            bool missingClock = double.IsNaN(hour) || double.IsInfinity(hour);
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
                        transaction.Commit();
                        return ReadString(receipt, "reply", reply);
                    }
                    var stored = TryParseJsonObject(ReadString(row, "state_json", "{}"));
                    var state = RecoverIntoxication(stored, IntoxicationEndurance(profile, characteristics), hour);
                    var evidence = new List<Dictionary<string, object>>();
                    var proposedVessel = CurrentDrinkVessel(state.Vessel, payload, hour);
                    double proposed = ValidatedDrinkAmount(parsed, reply, evidence, ReadFirstString(profile, "name", "heroName"),
                        profile != null && profile.ContainsKey("isFemale") ? (bool?)ReadBool(profile, "isFemale", false) : null, proposedVessel);
                    bool staleClock = missingClock || ReadBool(stored, "hasClock", false) && hour < ReadDouble(stored, "hour", 0) - .000001;
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
                    parsed["reply"] = reply;
                    var receiptNew = new Dictionary<string, object>
                    {
                        ["schema"] = "reign-drinking-receipt-v1", ["heroStringId"] = heroId, ["turnKey"] = key,
                        ["proposed"] = proposed, ["consumed"] = consumed, ["staleClock"] = staleClock,
                        ["evidence"] = evidence, ["state"] = IntoxicationRecord(state),
                        ["suppressedCommitments"] = suppress, ["reply"] = reply
                    };
                    args["state"] = Json.Serialize(IntoxicationRecord(state));
                    args["receipt"] = Json.Serialize(receiptNew);
                    ExecuteSql(connection, "UPDATE conversation_intoxication SET state_json=$state WHERE hero_id=$hero;", args);
                    ExecuteSql(connection, "INSERT INTO conversation_drinking_turns(hero_id,turn_key,receipt_json) VALUES($hero,$turn,$receipt);", args);
                    transaction.Commit();
                    payload["intoxicationReceipt"] = receiptNew;
                    if (evidence.Count > 0)
                        WriteAudit(campaignId, ReadString(payload, "correlationId", identity), "server",
                            ReadString(payload, "mode", "dialogue"), "drinking.receipt", heroId, "", "", "completed", 0,
                            "Narrated drinking adjudicated against the accepted reply.", receiptNew);
                    return reply;
                }
            }
        }
    }
}
