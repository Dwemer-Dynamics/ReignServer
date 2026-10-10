using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // One active physical scene per campaign, shared by speaking participants.
        // Session IDs and model prose are deliberately not the storage identity.
        private sealed class DrinkScene
        {
            public string Scope = "", BaseHash = "";
            public double Hour;
            public Dictionary<string, object> Stored;
            public List<Dictionary<string, object>> Vessels = new List<Dictionary<string, object>>();
            public Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static string DrinkSceneScope(Dictionary<string, object> payload)
        {
            if (DrinkHas(ReadFirstString(payload, "mode", "channel"), "correspond")) return "";
            var scene = ReadDictionary(payload, "conversationSceneState");
            string venue = ReadString(scene, "conversationVenue", "").Trim();
            string location = ReadFirstString(payload, "locationId", "settlementId");
            var native = ReadDictionary(payload, "localPresence");
            string party = ReadString(payload, "playerPartyId", "");
            bool positionedParty = party.Length > 0 && native != null && native.ContainsKey("partyPositionX") && native.ContainsKey("partyPositionY")
                && FiniteDrinkNumber(ReadDouble(native, "partyPositionX", double.NaN)) && FiniteDrinkNumber(ReadDouble(native, "partyPositionY", double.NaN));
            if (scene == null || venue.Length == 0 || location.Length == 0 && !positionedParty) return "";
            return "scene:" + PromptHash(location + "|" + ReadString(native, "roomId", "") + "|"
                + ReadString(payload, "playerPartyId", "") + "|" + venue.ToLowerInvariant() + "|"
                + ReadDouble(native, "partyPositionX", 0).ToString("0.00", CultureInfo.InvariantCulture) + "|"
                + ReadDouble(native, "partyPositionY", 0).ToString("0.00", CultureInfo.InvariantCulture));
        }

        private static DrinkScene LoadDrinkScene(ReignDbConnection connection, Dictionary<string, object> payload, double hour, bool forUpdate = false)
        {
            if (forUpdate) ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_drink_scene(scene_id,state_json) VALUES('active','{}');");
            var row = QuerySql(connection, "SELECT state_json FROM conversation_drink_scene WHERE scene_id='active'" + (forUpdate ? " FOR UPDATE;" : ";")).SingleOrDefault();
            var stored = TryParseJsonObject(ReadString(row, "state_json", "{}"));
            var scene = new DrinkScene { Scope = DrinkSceneScope(payload), Hour = hour,
                Stored = stored, BaseHash = PromptHash(CanonicalJson(stored)) };
            double previous = ReadDouble(stored, "hour", double.NaN);
            if (scene.Scope.Length > 0 && scene.Scope == ReadString(stored, "scope", "") && FiniteDrinkNumber(hour)
                && FiniteDrinkNumber(previous) && hour >= previous - .000001 && hour - previous <= 1d)
                scene.Vessels = ReadDictionaryList(stored, "vessels").Take(16).Select(CloneDictionary).ToList();
            return scene;
        }

        private static Dictionary<string, object> DrinkSceneRecord(DrinkScene scene) => TestDict(
            "schema", "reign-drink-scene-v1", "scope", scene.Scope, "hour", scene.Hour,
            "vessels", scene.Vessels.Take(16).ToList());

        private static string DrinkSceneMetadataHash(Dictionary<string, object> parsed) => PromptHash(CanonicalJson(TestDict(
            "drinkSceneUpdates", ReadDictionaryList(parsed, "drinkSceneUpdates"),
            "sceneStateUpdates", ReadDictionaryList(parsed, "sceneStateUpdates"))));

        private static HashSet<string> DrinkSceneParticipants(Dictionary<string, object> payload, string hero)
        {
            var ids = ReadDictionaryList(ReadDictionary(payload, "conversationSceneState"), "participants")
                .Select(p => ReadString(p, "heroStringId", "")).Where(id => id.Length > 0).Take(32);
            var result = new HashSet<string>(ids, StringComparer.Ordinal);
            result.Add(hero);
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            if (player.Length > 0) result.Add(player);
            return result;
        }

        private static DrinkVesselState SelectSceneDrink(DrinkScene scene, string hero, Dictionary<string, object> parsed = null, string preferredId = "")
        {
            string chosen = ReadString(ReadDictionaryList(parsed, "drinkConsumptions").FirstOrDefault(), "vesselId", "");
            if (chosen.Length == 0) chosen = preferredId;
            if (scene.Aliases.TryGetValue(chosen, out string actual)) chosen = actual;
            var held = scene.Vessels.Where(v => ReadString(v, "holderHeroId", "") == hero).ToList();
            var record = held.FirstOrDefault(v => ReadString(v, "id", "") == chosen)
                ?? (held.Count == 1 ? held[0] : null);
            if (record == null && held.Count == 0)
            {
                var shared = scene.Vessels.Where(v => ReadString(v, "sharedWithHeroId", "") == hero
                    && ReadString(v, "holder", "") == "player").ToList();
                if (shared.Count == 1) record = shared[0];
            }
            var vessel = ReadDrinkVessel(record);
            vessel.Scope = scene.Scope; vessel.Hour = scene.Hour;
            return vessel;
        }

        private static void StoreSceneDrink(DrinkScene scene, DrinkVesselState vessel, string previousId, string hero, string player)
        {
            if (previousId.Length > 0) scene.Vessels.RemoveAll(v => ReadString(v, "id", "") == previousId);
            if (vessel.Id.Length == 0 || vessel.Kind.Length == 0) return;
            scene.Vessels.RemoveAll(v => ReadString(v, "id", "") == vessel.Id);
            var record = DrinkVesselRecord(vessel);
            record["holderHeroId"] = vessel.Holder == "player" ? player : hero;
            if (vessel.SharedWithPlayer) record["sharedWithHeroId"] = hero;
            scene.Vessels.Add(record);
            if (scene.Vessels.Count > 16) scene.Vessels.RemoveAt(0);
        }

        private sealed class DrinkSceneObservation
        {
            public Dictionary<string, object> Item;
            public int Start, End;
        }

        private const string UnacceptedDrinkSceneEvidence = @"\b(?:if|unless|would|could|might|will|shall|tomorrow|yesterday|remember\w*|recall\w*|imagin\w*|pretend\w*|want|wants|offers?|offering|says?|said|claims?|claimed|reported|heard|reads?)\b|\?|\b(?:not|never|don't|doesn't|didn't)\s+(?:arriv\w*|serv\w*|bring\w*|pour\w*|fill\w*|hold\w*|hand\w*|pass\w*|giv\w*|receiv\w*|discard\w*)\b";

        private static List<DrinkSceneObservation> DrinkSceneObservations(Dictionary<string, object> parsed, string reply,
            Dictionary<string, object> payload, string hero, bool? female)
        {
            var observations = new List<DrinkSceneObservation>();
            foreach (var item in ReadDictionaryList(parsed, "drinkSceneUpdates").Take(16))
            {
                string quote = ReadString(item, "supportingQuote", "").Trim();
                bool fromPlayer = ReadString(item, "source", "reply") == "player";
                string text = fromPlayer ? ReadFirstString(payload, "playerText", "text", "message", "query") : reply;
                int start = quote.Length > 0 ? text.IndexOf(quote, StringComparison.Ordinal) : -1;
                if (start < 0 || quote.Length > 2000 || text.IndexOf(quote, start + 1, StringComparison.Ordinal) >= 0) continue;
                if (MaskDrinkQuotations(text).Substring(start, quote.Length) != quote) continue;
                // Quoting only the service clause cannot strip the surrounding
                // recollection/conditional from the same narration paragraph.
                int left = start > 0 ? text.LastIndexOfAny(new[] { '\n', '*' }, start - 1) : -1;
                if (DrinkHas(text.Substring(left + 1, start - left - 1), UnacceptedDrinkSceneEvidence)) continue;
                // Player speech is an offer/proposal, not an accepted NPC possession.
                if (fromPlayer && !ReignBeta.Dialogue.ReignActionText.Parse(text).Any(s => s.IsAction && s.Text.Contains(quote))) continue;
                if (fromPlayer && ReadString(item, "holderHeroId", "") != ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId")) continue;
                observations.Add(new DrinkSceneObservation { Item = item, Start = fromPlayer ? -1 : start, End = fromPlayer ? -1 : start + quote.Length });
            }
            // Compatibility for the captured ambient service, even if an older/custom
            // provider omits the new observation array. No consumption is inferred.
            if (observations.Count == 0)
            {
                foreach (Match paragraph in Regex.Matches(reply, @"\*([^*]+)\*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    string quote = paragraph.Groups[1].Value;
                    if (MaskDrinkQuotations(quote) != quote) continue;
                    if (!DrinkHas(quote, @"\b(?:wine|ale|beer|mead)\s+arrives\b")
                        || !DrinkHas(quote, @"\btwo\s+(?:clay\s+)?cups\b")
                        || !DrinkHas(quote, @"\b(?:pushes|slides)\s+one\s+toward\s+(?:him|her|you)\b")
                        || !DrinkHas(quote, @"\b(?:her|his)\s+hand\s+around\s+the\s+other\b")) continue;
                    string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                    if (player.Length == 0) continue;
                    foreach (string holder in new[] { hero, player }) observations.Add(new DrinkSceneObservation
                    {
                        Start = paragraph.Index + 1, End = paragraph.Index + paragraph.Length - 1,
                        Item = TestDict("operation", "establish", "vesselId", "ambient_" + holder,
                            "holderHeroId", holder, "beverage", ResolveUniqueDrinkBeverage(quote).Name, "kind", "cup",
                            "quantity", "unknown", "contentsVersion", 0, "source", "reply", "supportingQuote", quote)
                    });
                }
            }
            return observations.OrderBy(o => o.End).ToList();
        }

        private static bool ApplyDrinkSceneObservation(DrinkScene scene, DrinkSceneObservation observation,
            Dictionary<string, object> payload, string hero, string key, List<Dictionary<string, object>> evidence)
        {
            var item = observation.Item;
            string quote = ReadString(item, "supportingQuote", "");
            string operation = ReadString(item, "operation", ""), holder = ReadString(item, "holderHeroId", "");
            string reference = ReadString(item, "vesselId", "");
            if (scene.Scope.Length == 0 || reference.Length == 0 || reference.Length > 128 || !DrinkSceneParticipants(payload, hero).Contains(holder)) return false;
            // Exact current evidence is necessary but not sufficient: promises,
            // questions, recollections and negated service cannot establish liquid.
            if (DrinkHas(quote, UnacceptedDrinkSceneEvidence)) return false;
            string id = scene.Aliases.TryGetValue(reference, out string mapped) ? mapped : reference;
            var existing = scene.Vessels.FirstOrDefault(v => ReadString(v, "id", "") == id);
            var beverage = ResolveUniqueDrinkBeverage(ReadString(item, "beverage", ""));
            var quoted = ResolveUniqueDrinkBeverage(quote);
            string kind = DrinkVesselKind(ReadString(item, "kind", ""));
            bool contents = beverage.Name.Length > 0 && quoted.Name.Length > 0
                && beverage.Family == quoted.Family && beverage.Alcohol == quoted.Alcohol;
            if (operation == "establish")
            {
                if (existing != null || scene.Aliases.ContainsKey(reference) || scene.Vessels.Count >= 16 || !contents || kind.Length == 0
                    || DrinkVesselKind(quote) != kind || !DrinkHas(quote, @"\b(?:arriv\w*|serv\w*|bring\w*|brought|pour\w*|fill\w*|holds?|holding|wraps?|sets?|places?|picks?\s+up|receives?)\b")) return false;
                string observationKey = PromptHash(key + "|" + holder + "|" + kind + "|" + quote);
                if (scene.Vessels.Any(v => ReadString(v, "observationKey", "") == observationKey)) return false;
                id = "vessel_" + PromptHash(scene.Scope + "|" + key + "|" + reference).Substring(0, 28);
                scene.Aliases[reference] = id;
                existing = DrinkVesselRecord(new DrinkVesselState { Id = id, Scope = scene.Scope, Kind = kind,
                    Hour = scene.Hour, ContentsVersion = 1, Beverage = beverage });
                existing["holderHeroId"] = holder;
                existing["observationKey"] = observationKey;
                scene.Vessels.Add(existing);
            }
            else
            {
                if (existing == null || ReadDouble(item, "contentsVersion", -1) != ReadInt(existing, "contentsVersion", 0)) return false;
                if (operation == "discard")
                {
                    if (!DrinkHas(quote, @"\b(?:discard\w*|leaves?|abandon\w*|takes?\s+away|removed|empties|spills?)\b")) return false;
                    scene.Vessels.Remove(existing);
                }
                else if (operation == "transfer")
                {
                    if (!DrinkHas(quote, @"\b(?:hands?|passes?|gives?|pushes?|slides?|takes?|receives?)\b")) return false;
                    string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                    string previousHolder = ReadString(existing, "holderHeroId", "");
                    existing["holderHeroId"] = holder;
                    existing["holder"] = holder == player ? "player" : "npc";
                    if (holder == player || previousHolder == player)
                    {
                        existing["sharedWithPlayer"] = true;
                        existing["sharedWithHeroId"] = holder == player ? previousHolder : holder;
                    }
                }
                else if (operation == "refill")
                {
                    if (!contents || !DrinkHas(quote, @"\b(?:refill\w*|fill\w*|pour\w*|tops?\s+up)\b")) return false;
                    existing["beverage"] = beverage.Name; existing["family"] = beverage.Family; existing["alcohol"] = beverage.Alcohol.ToString();
                    existing["contentsVersion"] = ReadInt(existing, "contentsVersion", 0) + 1;
                }
                else return false;
            }
            if (operation == "establish" || operation == "refill")
            {
                string quantity = ReadString(item, "quantity", "unknown");
                double? remaining = quantity == "empty" && DrinkHas(quote, @"\bempty\b") ? 0d
                    : quantity == "full" && DrinkHas(quote, @"\b(?:full|filled|refilled|refills?)\b") && OrdinaryDrinkVessel(kind.Length > 0 ? kind : ReadString(existing, "kind", "")) ? 1d
                    : quantity == "half" && DrinkHas(quote, @"\bhalf\b") && OrdinaryDrinkVessel(kind) ? .5d
                    : quantity == "quarter" && DrinkHas(quote, @"\bquarter\b") && OrdinaryDrinkVessel(kind) ? .25d : (double?)null;
                existing["remaining"] = remaining;
            }
            evidence.Add(TestDict("source", "scene_observation", "operation", operation, "vesselId", id,
                "holderHeroId", holder, "evidenceQuote", quote, "amount", 0d, "reason", "scene_" + operation));
            return true;
        }

        private static void CommitDrinkScene(ReignDbConnection connection, Dictionary<string, object> receipt)
        {
            var proposed = ReadDictionary(receipt, "drinkScene");
            if (proposed == null) return;
            ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_drink_scene(scene_id,state_json) VALUES('active','{}');");
            var row = QuerySql(connection, "SELECT state_json FROM conversation_drink_scene WHERE scene_id='active' FOR UPDATE;").Single();
            string hash = PromptHash(CanonicalJson(TryParseJsonObject(ReadString(row, "state_json", "{}"))));
            if (hash != ReadString(receipt, "baseDrinkSceneHash", ""))
                throw new InvalidOperationException("The shared drink scene changed while the response was prepared.");
            ExecuteSql(connection, "UPDATE conversation_drink_scene SET state_json=$state WHERE scene_id='active';", TestDict("state", Json.Serialize(proposed)));
        }

        private static bool DrinkSceneDeparture(Dictionary<string, object> payload, Dictionary<string, object> parsed, string hero, string reply)
        {
            string venue = ReadString(ReadDictionary(payload, "conversationSceneState"), "conversationVenue", "").Trim();
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string playerText = ReadFirstString(payload, "playerText", "text", "message", "query");
            foreach (var update in ReadDictionaryList(parsed, "sceneStateUpdates"))
            {
                string location = ReadString(update, "location", "").Trim(), subject = ReadString(update, "heroStringId", "");
                if (location.Length == 0 || location == venue || string.IsNullOrWhiteSpace(reply)) continue;
                // Match the scene owner's acceptance rules: another NPC cannot move
                // this scene; the player's update requires a completed current action.
                if (subject == hero) return true;
                string quote = ReadString(update, "supportingQuote", "");
                if (subject == player && quote.Length >= 8 && quote.Length <= 1000
                    && ReignBeta.Dialogue.ReignActionText.Parse(playerText).Any(s => s.IsAction && s.Text.Contains(quote))
                    && !HasPrivateNarrativeMeaning(quote) && !HasUncertainty(quote)
                    && !DrinkHas(quote, @"\b(?:will|would|could|might|if|yesterday|tomorrow|had|used to|plan|wish)\b")) return true;
            }
            return false;
        }
    }
}
