using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReignBeta.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool FiniteDrinkNumber(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        // One unambiguous vessel shared by this NPC and the player in this conversation.
        // Handing it back changes custody, not its identity or contents.
        private sealed class DrinkVesselState
        {
            public string Scope = "", Kind = "";
            public string Id = "", Holder = "npc", IdentitySeed = "";
            public int ContentsVersion;
            public bool SharedWithPlayer;
            public double Hour;
            public double? Remaining;
            public BeverageIdentity Beverage = new BeverageIdentity();
            public DrinkVesselState Copy() => new DrinkVesselState
            { Scope = Scope, Kind = Kind, Hour = Hour, Remaining = Remaining, Beverage = Beverage.Copy(),
                Id = Id, Holder = Holder, ContentsVersion = ContentsVersion, SharedWithPlayer = SharedWithPlayer, IdentitySeed = IdentitySeed };
            public void Clear() { Kind = ""; Remaining = null; Beverage = new BeverageIdentity();
                Id = ""; Holder = "npc"; ContentsVersion = 0; SharedWithPlayer = false; }
        }

        private static void IdentifyDrinkVessel(DrinkVesselState vessel, string observation)
        {
            if (vessel.Id.Length == 0 && vessel.Kind.Length > 0)
                vessel.Id = "vessel_" + PromptHash(vessel.Scope + "|" + vessel.IdentitySeed + "|" + observation).Substring(0, 28);
        }

        private static void CopyDrinkVessel(DrinkVesselState target, DrinkVesselState source)
        {
            target.Kind = source.Kind; target.Remaining = source.Remaining; target.Beverage = source.Beverage.Copy();
            target.Id = source.Id; target.Holder = source.Holder; target.ContentsVersion = source.ContentsVersion;
            target.SharedWithPlayer = source.SharedWithPlayer;
        }

        private static Dictionary<string, object> DrinkVesselRecord(DrinkVesselState vessel) => new Dictionary<string, object>
        {
            ["scope"] = vessel.Scope, ["kind"] = vessel.Kind, ["hour"] = vessel.Hour,
            ["id"] = vessel.Id, ["holder"] = vessel.Holder, ["contentsVersion"] = vessel.ContentsVersion,
            ["sharedWithPlayer"] = vessel.SharedWithPlayer,
            ["remaining"] = vessel.Remaining, ["beverage"] = vessel.Beverage.Name,
            ["family"] = vessel.Beverage.Family, ["alcohol"] = vessel.Beverage.Alcohol.ToString(),
            ["catalogue"] = BeverageCatalogueVersion
        };

        private static DrinkVesselState ReadDrinkVessel(Dictionary<string, object> record)
        {
            if (record == null || ReadString(record, "catalogue", "") != BeverageCatalogueVersion) return new DrinkVesselState();
            double remaining = ReadDouble(record, "remaining", double.NaN);
            double hour = ReadDouble(record, "hour", double.NaN);
            if (!FiniteDrinkNumber(hour) || hour < 0) return new DrinkVesselState();
            return new DrinkVesselState
            {
                Scope = ReadString(record, "scope", ""), Kind = ReadString(record, "kind", ""), Hour = hour,
                Id = ReadString(record, "id", ""), Holder = ReadString(record, "holder", "npc") == "player" ? "player" : "npc",
                ContentsVersion = Math.Max(0, ReadInt(record, "contentsVersion", 0)), SharedWithPlayer = ReadBool(record, "sharedWithPlayer", false),
                Remaining = FiniteDrinkNumber(remaining) && remaining >= 0 && remaining <= 20 ? (double?)remaining : null,
                Beverage = new BeverageIdentity { Name = ReadString(record, "beverage", ""), Family = ReadString(record, "family", ""),
                    Alcohol = Enum.TryParse(ReadString(record, "alcohol", ""), out DrinkAlcohol alcohol) ? alcohol : DrinkAlcohol.Unknown }
            };
        }

        private static DrinkVesselState CurrentDrinkVessel(DrinkVesselState stored, Dictionary<string, object> payload, double hour)
        {
            string session = ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId", "eventId", "socialEventId");
            string scope = session.Length == 0 ? "" : session + "|" + ReadFirstString(payload, "locationId", "settlementId");
            // A per-turn sceneTurnId is deliberately not a session. Missing identity can
            // still resolve references inside this reply, but cannot borrow an old cup.
            var vessel = stored?.Copy() ?? new DrinkVesselState();
            if (scope.Length == 0 || scope.Length > 512 || !FiniteDrinkNumber(hour) || vessel.Scope != scope
                || hour < vessel.Hour - .000001 || hour - vessel.Hour > 1d)
                vessel.Clear();
            vessel.Scope = scope.Length <= 512 ? scope : "";
            vessel.Hour = FiniteDrinkNumber(hour) && hour >= 0 ? hour : 0;
            return vessel;
        }

        private sealed class PlayerDrinkOffer
        {
            public DrinkVesselState Vessel;
            public string Evidence;
        }

        private static PlayerDrinkOffer CurrentPlayerDrinkOffer(Dictionary<string, object> payload, DrinkVesselState current, bool? isFemale = null)
        {
            var actions = CurrentPlayerDrinkOfferActions(payload, true);
            // A short spoken invitation can offer the identified shared drink without
            // requiring the player to narrate a physical handoff.
            if (actions.Count == 0 && current.Id.Length > 0 && current.Holder == "player" && current.SharedWithPlayer)
            {
                string invitation = ReadFirstString(payload, "playerText", "text", "message", "query").Trim().Trim('"');
                if (invitation.Length <= 200 && DrinkHas(invitation, @"^(?:here[, ]+)?(?:have|take|want|do\s+you\s+want|would\s+you\s+like)\s+(?:a|another|some|more)\s+(?:sip|drink)\b")
                    && !DrinkHas(invitation, @"\b(?:if|unless|tomorrow|later|not|never|imagine|remember|recall|new|different)\b"))
                {
                    var invited = current.Copy(); invited.Holder = "offered";
                    return new PlayerDrinkOffer { Vessel = invited, Evidence = invitation };
                }
            }
            if (actions.Count != 1) return null;
            string action = actions[0];
            if (DrinkHas(action, @"\b(?:to|for)\s+(?:the\s+)?(?:waitress|waiter|server|someone|another)\b")) return null;
            if (DrinkHas(action, @"\b(?:two|three|both|several|multiple)\s+(?:(?:full|empty|wine|ale|beer)\s+)?(?:cups|glasses|goblets|mugs|tankards|bottles|skins)\b")) return null;
            if (isFemale.HasValue && DrinkHas(action, isFemale.Value ? @"\b(?:to|for)\s+him\b" : @"\b(?:to|for)\s+her\b")) return null;
            string kind = DrinkVesselKind(action);
            var beverage = ResolveUniqueDrinkBeverage(action);
            bool named = ResolveDrinkBeverages(action).Count > 0;
            bool changed = DrinkHas(action, @"\b(?:new|fresh|another|different)\s+(?:(?:full|empty|wine|ale|beer|whiskey)\s+){0,2}(?:" + DrinkVesselWords + @")\b|\b(?:refill\w*|fill\w*|pour\w*|spill\w*|empty)\b");
            bool same = current.Id.Length > 0 && current.Holder == "player" && current.SharedWithPlayer
                && !changed && (kind.Length == 0 || kind == current.Kind)
                && (!named || beverage.Name.Length > 0 && beverage.Family == current.Beverage.Family && beverage.Alcohol == current.Beverage.Alcohol)
                && !DrinkHas(action, @"\b(?:unknown|mystery|preparation|mixture)\b");
            // A bare pronoun cannot create a vessel, and an unidentified preparation
            // cannot borrow an old alcoholic drink's contents.
            if (!same && kind.Length == 0) return null;
            var offered = same ? current.Copy() : new DrinkVesselState
            { Scope = current.Scope, Hour = current.Hour, IdentitySeed = current.IdentitySeed, Kind = kind, Beverage = beverage };
            offered.SharedWithPlayer = true;
            offered.Holder = "offered";
            if (!same && OrdinaryDrinkVessel(kind) && DrinkHas(action, @"\b(?:full|filled|entire|whole)\b")) offered.Remaining = 1;
            // Player consumption is not this NPC's counter. It also makes a previously
            // measured remainder unreliable; do not silently give the liquid back.
            if (ReadNarratedDrinks(action, "I").Count > 0 && offered.Remaining != 0) offered.Remaining = null;
            IdentifyDrinkVessel(offered, "player_offer|" + action);
            return new PlayerDrinkOffer { Vessel = offered, Evidence = action };
        }

        private static string DrinkVesselKind(string text)
        {
            string kind = DrinkMatch(text, @"\b(?:" + DrinkVesselWords + @")(?:s|es)?\b").Value.ToLowerInvariant();
            if (kind == "glasses") return "glass";
            return kind.EndsWith("s", StringComparison.Ordinal) && kind != "glass" ? kind.Substring(0, kind.Length - 1) : kind;
        }

        private static bool OrdinaryDrinkVessel(string kind) => DrinkHas(kind, @"^(?:cup|glass|goblet|mug|tankard|horn|drinking horn|chalice)$");
        private const string DrinkVesselOperationPattern = @"\b(?:refills?|fills?|tops?\s+up|pours?|spills?|empties|discards?|hands?|handing|passes?|passing|gives?|giving|switches?\s+to|takes?|accepts?|receives?|picks?\s+up)\b";

        private static string DrinkSentencePrefix(string text, int index)
        {
            int start = text.LastIndexOfAny(new[] { '.', '!', '?', ';' }, Math.Max(0, index - 1));
            return text.Substring(start + 1, index - start - 1);
        }

        private static void ApplyDrinkVesselOperation(string action, Match operation, int end, DrinkVesselState vessel,
            string heroName, bool? isFemale, List<Dictionary<string, object>> evidence, int actionIndex, PlayerDrinkOffer offer = null)
        {
            string prefix = DrinkSentencePrefix(action, operation.Index);
            string text = action.Substring(operation.Index, Math.Max(0, end - operation.Index)).Trim();
            bool ownHandReturn = vessel.Holder == "npc" && vessel.SharedWithPlayer && DrinkHas(operation.Value, @"^(?:hand|pass|giv)")
                && DrinkHas(prefix, isFemale == false ? @"^\s*his\s+(?:thumb|fingers|hand)\b" : @"^\s*her\s+(?:thumb|fingers|hand)\b")
                && !DrinkHas(prefix, @"\b(?:not|never|he|she|they|you|would|could|might|will|shall)\b")
                && DrinkHas(text, @"^(?:handing|passing|giving)\s+it\s+back\b");
            if (!CurrentDrinkSubject(prefix, heroName, isFemale, out _) && !ownHandReturn) return;
            if (DrinkHas(text, @"\b(?:if|unless|would|could|might|tomorrow)\b")) return;
            string target = DrinkMatch(text, @"\b(?:into|in)\s+(.+)").Groups[1].Value;
            string kind = DrinkVesselKind(target.Length > 0 ? target : text);
            var beverage = ResolveDrinkBeverage(text);
            bool heldReference = DrinkHas(text, @"^(?:refills?|fills?|tops?\s+up|pours?|spills?|empties|discards?|hands?|handing|passes?|passing|gives?|giving|switches?\s+to|takes?|accepts?|receives?|picks?\s+up)\s+(?:it\b|(?:her|his|their|the)\s+drink\b)")
                && (vessel.Kind.Length > 0 || vessel.Beverage.Name.Length > 0 || offer?.Vessel.Kind.Length > 0);
            if (kind.Length == 0 && beverage.Name.Length == 0 && !heldReference) return;
            bool acquiring = DrinkHas(operation.Value, @"^(?:take|accept|receiv|pick)");
            if (DrinkHas(text, @"\b(?:him|them|you|waitress|waiter|server)\b")
                && !acquiring && !DrinkHas(operation.Value, @"^(?:hand|pass|give)")) return;
            if (!acquiring && isFemale.HasValue && DrinkHas(text, isFemale.Value ? @"\b(?:his|their)\s+(?:own\s+)?(?:cup|glass|mug|tankard)\b" : @"\b(?:her|their)\s+(?:own\s+)?(?:cup|glass|mug|tankard)\b")) return;
            string before = Json.Serialize(DrinkVesselRecord(vessel));
            string reason;
            if (DrinkHas(operation.Value, @"^(?:hand|pass|giv|discard)"))
            {
                bool player = !DrinkHas(operation.Value, @"^discard")
                    && (DrinkHas(text, @"\bto\s+(?:you|me)\b") || vessel.SharedWithPlayer && DrinkHas(text, @"\bback\b"))
                    && !DrinkHas(text, @"\b(?:waitress|waiter|server|someone|another)\b");
                if (player && vessel.Kind.Length > 0)
                { vessel.Holder = "player"; vessel.SharedWithPlayer = true; IdentifyDrinkVessel(vessel, "handoff|" + actionIndex + "|" + operation.Index); reason = "vessel_returned_to_player"; }
                else { vessel.Clear(); reason = "vessel_relinquished"; }
            }
            else if (DrinkHas(operation.Value, @"^(?:spill|empt)"))
            {
                if (DrinkHas(text, @"\b(?:half|quarter)\b")) vessel.Remaining *= DrinkHas(text, @"\bhalf\b") ? .5 : .75;
                else if (DrinkHas(text, @"\b(?:all|entire|whole)\b") || DrinkHas(operation.Value, @"^empt")) vessel.Remaining = 0;
                else vessel.Remaining = null;
                reason = "vessel_spilled";
            }
            else
            {
                bool filled = DrinkHas(operation.Value, @"^(?:refill|fill|top)");
                bool poured = DrinkHas(operation.Value, @"^pour");
                bool replacement = DrinkHas(text, @"\b(?:new|fresh|another|different)\b") || DrinkHas(operation.Value, @"^(?:accept|receive|switch)");
                bool acceptedOffer = acquiring && offer != null && !DrinkHas(text, @"\b(?:new|another|different)\b")
                    && (kind == offer.Vessel.Kind || heldReference && kind.Length == 0)
                    && (beverage.Name.Length == 0 || beverage.Family == offer.Vessel.Beverage.Family && beverage.Alcohol == offer.Vessel.Beverage.Alcohol);
                bool returned = acquiring && vessel.Holder == "player" && vessel.SharedWithPlayer
                    && !replacement && kind == vessel.Kind && DrinkHas(text, @"\b(?:back|from\s+(?:you|me))\b");
                if (acceptedOffer)
                {
                    if (vessel.Id != offer.Vessel.Id || vessel.Holder != "npc") CopyDrinkVessel(vessel, offer.Vessel);
                    vessel.Holder = "npc"; replacement = false;
                }
                else if (returned) vessel.Holder = "npc";
                else if (acquiring && vessel.Holder != "npc") replacement = true;
                bool differentKind = kind.Length > 0 && vessel.Kind.Length > 0 && kind != vessel.Kind;
                if (!filled && !poured && !replacement && !differentKind && beverage.Alcohol == DrinkAlcohol.VariantRequired
                    && beverage.Family == vessel.Beverage.Family)
                    beverage.Alcohol = vessel.Beverage.Alcohol;
                bool different = kind.Length > 0 && vessel.Kind.Length > 0 && kind != vessel.Kind
                    || beverage.Name.Length > 0 && (beverage.Family != vessel.Beverage.Family || beverage.Alcohol != vessel.Beverage.Alcohol);
                string previousKind = vessel.Kind;
                if (replacement || differentKind || different && !filled && !poured) vessel.Clear();
                if (kind.Length > 0) vessel.Kind = kind;
                else if (heldReference && !replacement) vessel.Kind = previousKind;
                if (beverage.Name.Length > 0) vessel.Beverage = beverage;
                vessel.Holder = "npc";
                if (filled || poured) vessel.ContentsVersion++;
                // Receiving/picking up an unspecified cup is not proof that it is full.
                if (DrinkHas(text, @"\bempty\b")) vessel.Remaining = 0;
                else if (OrdinaryDrinkVessel(vessel.Kind) && (filled || DrinkHas(text, @"\b(?:full|filled|refilled|freshly\s+filled|entire|whole)\b")))
                    vessel.Remaining = DrinkHas(text, @"\b(?:half|halfway)\b") ? .5 : DrinkHas(text, @"\bquarter\b") ? .25 : 1;
                else if (poured)
                {
                    if (DrinkHas(text, @"\bhalf\s+(?:a\s+)?cup\b") && vessel.Remaining.HasValue && OrdinaryDrinkVessel(vessel.Kind))
                        vessel.Remaining = Math.Min(1, vessel.Remaining.Value + .5);
                    else vessel.Remaining = null;
                }
                reason = filled || poured ? "vessel_filled" : "vessel_observed";
                IdentifyDrinkVessel(vessel, "observation|" + actionIndex + "|" + operation.Index);
            }
            if (before != Json.Serialize(DrinkVesselRecord(vessel)))
                evidence.Add(new Dictionary<string, object> { ["source"] = "vessel_action", ["actionIndex"] = actionIndex,
                    ["action"] = action, ["evidenceQuote"] = text, ["reason"] = reason, ["amount"] = 0d,
                    ["vesselAfter"] = DrinkVesselRecord(vessel) });
        }

        private sealed class BoundDrinkEvent
        {
            public Dictionary<string, object> Item;
            public int DeclaredIndex;
            public string Binding;
        }

        private static Dictionary<NarratedDrink, BoundDrinkEvent> BindDrinkEvents(Dictionary<string, object> parsed,
            List<string> actions, List<NarratedDrink> candidates, List<Dictionary<string, object>> evidence)
        {
            var bound = new Dictionary<NarratedDrink, BoundDrinkEvent>();
            foreach (var item in ReadDictionaryList(parsed, "drinkingEvents").Take(16))
            {
                int declared = ReadInt(item, "actionIndex", -1), index = declared;
                string quote = ReadString(item, "actionQuote", "").Trim().Trim('*').Trim();
                string binding = "index", reason = "";
                if (quote.Length > 0)
                {
                    var indices = actions.Select((a, i) => new { a, i }).Where(a => a.a.Trim() == quote).ToList();
                    if (indices.Count == 1) { index = indices[0].i; binding = "exact_quote"; }
                    else { index = -1; reason = "quote_not_unique_or_missing"; }
                }
                var possible = candidates.Where(c => c.Index == index).ToList();
                string span = ReadString(item, "evidenceQuote", "").Trim();
                int occurrence = ReadInt(item, "occurrenceIndex", -1);
                if (span.Length > 0)
                {
                    var exact = possible.Where(c => c.Evidence.Trim().Equals(span, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exact.Count == 1) possible = exact;
                    else
                    {
                        var contained = possible.Where(c => c.Evidence.IndexOf(span, StringComparison.OrdinalIgnoreCase) >= 0
                            || span.IndexOf(c.Evidence.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        possible = contained.Count == 1 ? contained : new List<NarratedDrink>();
                        if (possible.Count == 1) binding = "unique_evidence_span";
                    }
                }
                if (occurrence >= 0) possible = possible.Where(c => c.Occurrence == occurrence).ToList();
                if (possible.Count == 0 && reason.Length == 0 && quote.Length == 0 && span.Length == 0 && occurrence < 0)
                {
                    possible = candidates.Where(c => !bound.ContainsKey(c) && !c.NonAlcohol).ToList();
                    binding = "unique_consumption";
                }
                NarratedDrink candidate = possible.Count == 1 ? possible[0] : null;
                if (reason.Length == 0) reason = candidate == null ? "missing_or_ambiguous_consumption" : bound.ContainsKey(candidate) ? "duplicate_occurrence" : "";
                if (reason.Length == 0) bound.Add(candidate, new BoundDrinkEvent { Item = item, DeclaredIndex = declared, Binding = binding });
                else evidence.Add(new Dictionary<string, object> { ["source"] = "model_event", ["binding"] = binding,
                    ["declaredActionIndex"] = declared, ["actionIndex"] = index, ["amount"] = 0d, ["reason"] = reason });
            }
            return bound;
        }

        private static double ConsumeNarratedDrink(NarratedDrink candidate, BoundDrinkEvent metadata,
            DrinkVesselState vessel, BeverageIdentity local, BeverageIdentity shared, BeverageIdentity offered,
            List<Dictionary<string, object>> evidence, PlayerDrinkOffer offer = null)
        {
            string text = candidate.ObjectText;
            string kind = candidate.Vessel.Length > 0 ? candidate.Vessel : DrinkVesselKind(text);
            var beverage = candidate.Beverage.Copy();
            string beverageSource = "action";
            bool replacement = DrinkHas(text, @"\b(?:new|fresh|another|different)\s+(?:(?:full|freshly\s+filled)\s+)?(?:" + DrinkVesselWords + @")\b")
                || DrinkHas(text, @"\bfresh\s+(?:wine|ale|beer|mead|kumis)\b");
            replacement = replacement || beverage.Name.Length > 0 && DrinkHas(text, @"\b(?:fresh|new|different)\s+(?:the\s+)?" + Regex.Escape(beverage.Name) + @"\b");
            if (!replacement && (kind.Length == 0 || vessel.Kind.Length == 0 || kind == vessel.Kind)
                && beverage.Alcohol == DrinkAlcohol.VariantRequired && beverage.Family == vessel.Beverage.Family)
            { beverage.Alcohol = vessel.Beverage.Alcohol; beverageSource = "same_vessel_variant"; }
            bool incompatible = kind.Length > 0 && vessel.Kind.Length > 0 && vessel.Kind != kind
                || beverage.Name.Length > 0 && vessel.Beverage.Name.Length > 0
                    && (beverage.Family != vessel.Beverage.Family || beverage.Alcohol != vessel.Beverage.Alcohol);
            bool newServing = candidate.QuantitySource == "explicit_serving"
                && DrinkHas(text, @"\b(?:a|an|" + DrinkNumberWords + @"|\d+)\s+(?:(?:full|new|freshly\s+filled)\s+){0,3}(?:cup|glass|goblet|mug|tankard)(?:s|es)?\b")
                && !DrinkHas(text, @"\bfrom\s+(?:her|his|their|the|this|that|same)\s+(?:own\s+)?(?:" + DrinkVesselWords + @")\b");
            bool offeredReference = offer != null && !replacement && !newServing
                && (kind == offer.Vessel.Kind || kind.Length == 0
                    && (DrinkHas(text, @"\b(?:it|contents|rest|remainder)\b")
                        || DrinkHas(text, @"^takes?\s+(?:a|another)\s+(?:(?:real|solid|slow|measured)\s+)?drink\s*$")
                        || DrinkHas(text, @"\b(?:" + DrinkPortionWords + @")s?\s*$")))
                && (beverage.Name.Length == 0 || beverage.Family == offer.Vessel.Beverage.Family && beverage.Alcohol == offer.Vessel.Beverage.Alcohol)
                && !DrinkHas(text, @"\b(?:unknown|mystery|preparation|mixture)\b");
            if (offeredReference && vessel.Id != offer.Vessel.Id) CopyDrinkVessel(vessel, offer.Vessel);
            if (offeredReference)
            {
                vessel.Holder = "npc"; incompatible = false;
                if (beverage.Name.Length == 0) { beverage = offer.Vessel.Beverage.Copy(); beverageSource = "persistent_player_offer"; }
            }
            // Custody matters: a bare reference cannot drink the skin still held by
            // the player. An explicit new beverage starts independent context.
            if (vessel.Holder != "npc") vessel.Clear();
            if (replacement || incompatible || newServing) vessel.Clear();
            // A named but unknown preparation never borrows the identity of the old cup.
            bool reference = kind.Length > 0 || DrinkHas(text, @"\b(?:it|contents|rest|remainder|what\s+remains|(?:her|his|their|the|same)\s+drink)\b")
                || vessel.Kind.Length > 0 && DrinkHas(text, @"^takes?\s+(?:a|another)\s+(?:(?:real|solid|slow|measured)\s+)?drink\s*$")
                || DrinkHas(text.Trim(), @"\b(?:" + DrinkPortionWords + @")s?\s*$")
                || DrinkHas(text, @"\b(?:a|another|one|two|three)\s+(?:(?:small|slow|measured|real|solid|long|deep|single)\s+){0,3}(?:" + DrinkPortionWords + @")s?\b(?!\s+of\b)");
            if (beverage.Name.Length == 0 && !reference) vessel.Clear();
            if (beverage.Name.Length == 0 && reference && local.Alcohol == DrinkAlcohol.Yes)
            { beverage = local.Copy(); beverageSource = "accepted_reply_context"; }
            else if (beverage.Name.Length == 0 && reference && vessel.Beverage.Name.Length > 0)
            { beverage = vessel.Beverage.Copy(); beverageSource = "same_vessel"; }
            else if (beverage.Name.Length == 0 && reference && shared.Alcohol == DrinkAlcohol.Yes)
            { beverage = shared.Copy(); beverageSource = "current_shared_fact"; }
            else if (beverage.Name.Length == 0 && reference && offered.Alcohol == DrinkAlcohol.Yes)
            { beverage = offered.Copy(); beverageSource = "accepted_player_offer"; }
            if (kind.Length > 0) vessel.Kind = kind;
            if (beverage.Name.Length > 0) vessel.Beverage = beverage.Copy();
            IdentifyDrinkVessel(vessel, "consumption|" + candidate.Index + "|" + candidate.Occurrence);
            bool full = OrdinaryDrinkVessel(kind) && DrinkHas(text, @"\b(?:full|freshly\s+filled)\b");
            if (full && !vessel.Remaining.HasValue) vessel.Remaining = candidate.Count;
            var item = metadata?.Item;
            double modelCount = ReadDouble(item, "count", 0), modelUnit = DrinkingUnit(ReadString(item, "serving", ""));
            bool validModel = item != null && ReadBool(item, "completed", false) && ReadBool(item, "alcohol", false)
                && modelUnit > 0 && FiniteDrinkNumber(modelCount) && modelCount >= 1 && modelCount <= 20
                && modelCount == Math.Floor(modelCount) && modelCount == candidate.Count;
            double amount = candidate.ExplicitAmount ?? (validModel ? modelUnit * modelCount : .25);
            string quantitySource = candidate.ExplicitAmount.HasValue ? candidate.QuantitySource : validModel ? "bounded_model_amount" : "conservative_partial_default";
            string reason = "";
            double? before = vessel.Remaining;
            if (candidate.RemainderFraction)
            {
                amount = before.HasValue ? before.Value * candidate.Fraction : 0;
                quantitySource = "remaining_fraction";
                if (!before.HasValue) reason = "remaining_volume_unknown";
            }
            else if (candidate.Completion)
            {
                // A complete ordinary cup is one serving if no prior amount was known.
                // A jug/bottle or an unspecified remainder has no assumed capacity.
                amount = before ?? (kind.Length == 0 && !DrinkHas(text, @"\b(?:rest|remainder|it|contents)\b")
                    || OrdinaryDrinkVessel(kind) ? 1d : 0d);
                quantitySource = "vessel_completion";
                if (amount == 0 && !before.HasValue) reason = "remaining_volume_unknown";
            }
            else if (kind.Length > 0 && !OrdinaryDrinkVessel(kind) && candidate.QuantitySource == "explicit_fraction" && !before.HasValue)
            { amount = 0; reason = "remaining_volume_unknown"; }
            if (before.HasValue) amount = Math.Min(amount, before.Value);
            // Even nonalcoholic liquid consumes vessel volume. The alcohol counter is
            // separate, and an empty vessel remains empty until a real refill/replacement.
            if (before.HasValue) vessel.Remaining = Math.Max(0, Math.Round(before.Value - amount, 8));
            else if (candidate.Completion || candidate.QuantitySource == "explicit_serving") vessel.Remaining = 0;
            if (beverage.Alcohol != DrinkAlcohol.Yes)
            { amount = 0; reason = beverage.Alcohol == DrinkAlcohol.No ? "nonalcoholic" : beverage.Alcohol == DrinkAlcohol.VariantRequired ? "beverage_variant_required" : "beverage_unknown"; }
            else if (before == 0) reason = "vessel_empty";
            evidence.Add(new Dictionary<string, object>
            {
                ["actionIndex"] = candidate.Index, ["occurrenceIndex"] = candidate.Occurrence,
                ["evidenceStart"] = candidate.Start, ["evidenceLength"] = candidate.Length, ["evidenceQuote"] = candidate.Evidence,
                ["action"] = candidate.Action, ["source"] = item == null ? "narrated_consumption" : "model_event",
                ["binding"] = metadata?.Binding ?? "final_reply", ["declaredActionIndex"] = metadata?.DeclaredIndex ?? -1,
                ["amount"] = amount, ["reason"] = reason, ["quantitySource"] = quantitySource,
                ["consumptionEvents"] = candidate.Count,
                ["estimatedPortion"] = candidate.QuantitySource == "explicit_portion" || candidate.QuantitySource == "default_partial",
                ["measurementQuality"] = reason == "remaining_volume_unknown"
                    || candidate.Completion && !before.HasValue && !OrdinaryDrinkVessel(kind)
                    ? "unquantified_consumption" : candidate.QuantitySource == "explicit_portion" || candidate.QuantitySource == "default_partial"
                        ? "estimated_partial_consumption" : "quantified",
                ["quantityNormalized"] = item != null && (!validModel || Math.Abs(amount - modelUnit * modelCount) > .000001),
                ["beverage"] = beverage.Name, ["family"] = beverage.Family, ["alcohol"] = beverage.Alcohol.ToString(),
                ["beverageSource"] = beverageSource, ["catalogue"] = BeverageCatalogueVersion,
                ["vesselKind"] = vessel.Kind, ["vesselScope"] = vessel.Scope,
                ["vesselId"] = vessel.Id, ["contentsVersion"] = vessel.ContentsVersion, ["holder"] = vessel.Holder,
                ["remainingBefore"] = before, ["remainingAfter"] = vessel.Remaining
            });
            return amount;
        }

        private static double ValidatedDrinkAmount(Dictionary<string, object> parsed, string reply,
            List<Dictionary<string, object>> evidence, string heroName = "", bool? isFemale = null, DrinkVesselState vessel = null,
            BeverageIdentity offered = null, PlayerDrinkOffer offer = null, Action<int> advanceScene = null, Func<int, bool> sceneCovered = null)
        {
            vessel = vessel ?? new DrinkVesselState();
            var actions = ReignActionText.Parse(reply).Where(s => s.IsAction).Select(s => s.Text).Take(64).ToList();
            var candidates = actions.SelectMany((text, i) => ReadNarratedDrinks(text, heroName, isFemale)
                .Select(c => { c.Index = i; return c; })).Take(32).ToList();
            var bound = BindDrinkEvents(parsed, actions, candidates, evidence);
            var shared = CurrentTurnSharedBeverage(parsed);
            offered = offered ?? new BeverageIdentity();
            double total = 0;
            int actionSearch = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                string action = MaskDrinkQuotations(actions[i]);
                int actionStart = reply.IndexOf(actions[i], actionSearch, StringComparison.Ordinal);
                if (actionStart < 0) actionStart = actionSearch;
                actionSearch = actionStart + actions[i].Length;
                advanceScene?.Invoke(actionStart);
                if (action.Length > 4000) continue;
                var local = ResolveUniqueDrinkBeverage(action);
                bool ambiguousVessels = DrinkHas(action, @"\b(?:two|three|both|several|multiple)\s+(?:(?:full|empty|wine|ale|beer)\s+)?(?:cups|glasses|goblets|mugs|tankards|bottles)\b")
                    && CurrentDrinkSubject(action, heroName, isFemale, out _);
                bool observedScene = sceneCovered?.Invoke(actionStart) == true;
                if (ambiguousVessels && !observedScene) vessel.Clear();
                if (DrinkHas(action, @"\bfrom\s+(?:(?:her|his|their|an?|the)\s+)?empty\s+(?:(?:wine|ale|beer|mead)\s+)?(?:cup|glass|goblet|mug|tankard)\b")
                    && DrinkHas(action, DrinkVerbPattern)
                    && CurrentDrinkSubject(action, heroName, isFemale, out _)) vessel.Remaining = 0;
                var drinks = candidates.Where(c => c.Index == i).ToList();
                var operations = Regex.Matches(action, DrinkVesselOperationPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)).Cast<Match>().Where(o => !drinks.Any(c => o.Index >= c.Start && o.Index < c.Start + c.Length)).ToList();
                foreach (int position in drinks.Select(c => c.Start).Concat(operations.Select(o => o.Index)).Distinct().OrderBy(x => x))
                {
                    advanceScene?.Invoke(actionStart + position);
                    var drink = drinks.FirstOrDefault(c => c.Start == position);
                    if (drink != null)
                    { bound.TryGetValue(drink, out var metadata); total += ConsumeNarratedDrink(drink, metadata, vessel, local, shared, offered, evidence, offer); }
                    else
                    {
                        if (sceneCovered?.Invoke(actionStart + position) == true) continue;
                        var operation = operations.First(o => o.Index == position);
                        int end = drinks.Select(c => c.Start).Concat(operations.Select(o => o.Index)).Where(p => p > position).DefaultIfEmpty(action.Length).Min();
                        ApplyDrinkVesselOperation(action, operation, end, vessel, heroName, isFemale, evidence, i, offer);
                    }
                }
                if (ambiguousVessels && !observedScene) vessel.Clear();
            }
            return Math.Round(total, 8);
        }

        private static double ValidateQuietDrinkConsumption(Dictionary<string, object> parsed, string reply,
            string heroId, DrinkVesselState vessel, PlayerDrinkOffer offer, List<Dictionary<string, object>> evidence)
        {
            var items = ReadDictionaryList(parsed, "drinkConsumptions").Take(8).ToList();
            if (items.Count == 0) return 0;
            string reason = "";
            // Hidden metadata describes the same choice as any narrated consumption.
            // Narration wins; never count both channels or repeated metadata twice.
            if (evidence.Any(e => ReadInt(e, "consumptionEvents", 0) > 0)) reason = "quiet_consumption_already_narrated";
            else if (items.Count != 1) reason = "quiet_consumption_not_unique";
            else if (DrinkHas(reply, @"\bI\s+(?:do\s+not|don't|did\s+not|didn't|will\s+not|won't|cannot|can't|refuse\s+to)\s+(?:drink|sip|swallow)\b|\bnot\s+drinking\b|^\s*(?:no\s+more|no\s+thanks)[.!]?\s*$"))
                reason = "quiet_consumption_contradicts_refusal";
            var item = items[0];
            string id = ReadString(item, "vesselId", "");
            var source = vessel.Id == id && vessel.Holder == "npc" ? vessel : offer?.Vessel.Id == id ? offer.Vessel : null;
            if (source == offer?.Vessel && evidence.Any(e => ReadString(e, "source", "") == "vessel_action")) source = null;
            double unit = DrinkingUnit(ReadString(item, "serving", ""));
            if (reason.Length == 0 && (source == null || id.Length == 0 || source.Kind.Length == 0)) reason = "quiet_consumption_vessel_unavailable";
            else if (reason.Length == 0 && (ReadString(item, "drinkerId", heroId) != heroId
                || ReadDouble(item, "contentsVersion", double.NaN) != source.ContentsVersion)) reason = "quiet_consumption_identity_mismatch";
            else if (reason.Length == 0 && (!ReadBool(item, "completed", false) || ReadDouble(item, "count", 0) != 1 || unit <= 0))
                reason = "quiet_consumption_invalid_portion";
            else if (reason.Length == 0 && source.Beverage.Alcohol != DrinkAlcohol.Yes && source.Beverage.Alcohol != DrinkAlcohol.No)
                reason = "quiet_consumption_contents_unknown";
            else if (reason.Length == 0 && source.Remaining == 0) reason = "vessel_empty";
            else if (reason.Length == 0 && !source.Remaining.HasValue && unit > .25) reason = "quiet_consumption_volume_unknown";
            if (reason.Length > 0)
            {
                evidence.Add(TestDict("source", "structured_consumption", "vesselId", id, "reason", reason, "amount", 0d));
                return 0;
            }
            if (!ReferenceEquals(source, vessel)) CopyDrinkVessel(vessel, source);
            vessel.Holder = "npc";
            double? before = vessel.Remaining;
            double amount = before.HasValue ? Math.Min(unit, before.Value) : unit;
            if (before.HasValue) vessel.Remaining = Math.Max(0, Math.Round(before.Value - amount, 8));
            bool alcohol = vessel.Beverage.Alcohol == DrinkAlcohol.Yes;
            evidence.Add(TestDict("source", "structured_consumption", "binding", "validated_vessel_record", "actionIndex", -1, "occurrenceIndex", 0,
                "vesselId", vessel.Id, "vesselKind", vessel.Kind, "vesselScope", vessel.Scope, "contentsVersion", vessel.ContentsVersion,
                "holder", vessel.Holder, "beverage", vessel.Beverage.Name, "family", vessel.Beverage.Family, "alcohol", vessel.Beverage.Alcohol.ToString(),
                "consumptionEvents", 1, "amount", alcohol ? amount : 0d, "reason", alcohol ? "" : "nonalcoholic",
                "quantitySource", "bounded_structured_portion", "measurementQuality", "estimated_gameplay_portion",
                "remainingBefore", before, "remainingAfter", vessel.Remaining));
            return alcohol ? amount : 0;
        }
    }
}
