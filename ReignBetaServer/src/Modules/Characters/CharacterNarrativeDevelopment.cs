using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string NarrativeDevelopmentContract = " A defining personal concern is stable. Do not change it because of a question, player assertion, mood, repetition in one conversation or a casual new preference. " +
            "Only when the NPC's visible reply actually expresses a lasting change, a dynamicCharacteristicWrites item may include " +
            "narrativeDevelopment:{itemId,influence,description,personalMeaning,supportingQuote,evidenceEventId}. " +
            "Use an existing private itemId supplied in the personal-interest context; never expose IDs or ratings in speech. " +
            "Quote the NPC's exact lasting personal response, not the player's claim. evidenceEventId must name a supplied completed native historical event personally relevant to that concern, or be empty. " +
            "The server independently requires a relevant major event plus lasting response, or matching evidence from three separate encounters spanning thirty campaign days. A proposal is not an accepted change.";

        private static bool NarrativeLastingQuote(string quote)
        {
            return Regex.IsMatch(quote ?? "", @"\b(I|my|me)\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(quote ?? "", @"\b(no longer|from now on|since then|over these|over the (weeks|months|years)|changed (me|my)|for the rest of|learned to|learnt to|have come to|has become|have stopped|never again|ever since|resolved to|means more to me|matters less to me|will always|have grown|have outgrown)\b", RegexOptions.IgnoreCase);
        }

        private static Dictionary<string, object> NormalizeNarrativeDevelopmentProposal(Dictionary<string, object> proposed, string visibleReply)
        {
            if (proposed == null || proposed.Count == 0) return null;
            var result = new Dictionary<string, object>();
            foreach (string key in new[] { "itemId", "description", "personalMeaning", "supportingQuote", "evidenceEventId" })
                result[key] = LimitText(ReadString(proposed, key, ""), key == "itemId" || key == "evidenceEventId" ? 160 : 600);
            result["influence"] = ReadInt(proposed, "influence", 0);
            string quote = ReadString(result, "supportingQuote", "");
            result["quoteValidated"] = quote.Length >= 20 && (visibleReply ?? "").IndexOf(quote, StringComparison.Ordinal) >= 0;
            return result;
        }

        private static Dictionary<string, object> EvaluateNarrativeDevelopment(Dictionary<string, object> item,
            Dictionary<string, object> proposal, string sessionId, double worldDay,
            List<Dictionary<string, object>> observations, bool verifiedRelevantMajorEvent)
        {
            var result = DeepCloneProfileDictionary(proposal);
            result["decision"] = "pending";
            result["sessionId"] = sessionId;
            result["worldDay"] = worldDay;
            int before = ReadInt(item, "influence", 0), after = ReadInt(proposal, "influence", 0);
            int direction = Math.Sign(after - before);
            result["direction"] = direction;
            bool valid = item != null && before >= 1 && after >= 1 && after <= 10
                && !string.IsNullOrWhiteSpace(sessionId) && worldDay >= 0
                && ReadBool(proposal, "quoteValidated", false) && ReadBool(proposal, "compatibilityValidated", false)
                && NarrativeLastingQuote(ReadString(proposal, "supportingQuote", ""))
                && ReadString(proposal, "description", "").Length >= 20 && ReadString(proposal, "personalMeaning", "").Length >= 20;
            if (!valid) { result["decision"] = "rejected"; result["reason"] = "missing_credible_lasting_personal_response"; return result; }
            var coherent = observations.Where(x => ReadString(x, "itemId", "") == ReadString(proposal, "itemId", "")
                    && ReadInt(x, "direction", 0) == direction && ReadBool(x, "quoteValidated", false)
                    && ReadBool(x, "compatibilityValidated", false)
                    && HasDynamicTextEvidence(ReadString(proposal, "description", "") + " " + ReadString(proposal, "personalMeaning", ""),
                        ReadString(x, "description", "") + " " + ReadString(x, "personalMeaning", ""))
                    && ReadString(x, "decision", "") != "rejected" && ReadDouble(x, "worldDay", worldDay + 1) <= worldDay)
                .Concat(new[] { result }).GroupBy(x => ReadString(x, "sessionId", ""))
                .Where(x => x.Key.Length > 0).Select(x => x.OrderBy(i => ReadDouble(i, "worldDay", 0)).First()).ToList();
            double span = coherent.Count == 0 ? 0 : coherent.Max(x => ReadDouble(x, "worldDay", 0)) - coherent.Min(x => ReadDouble(x, "worldDay", 0));
            bool sustained = coherent.Count >= 3 && span >= 30d;
            result["encounterCount"] = coherent.Count;
            result["daySpan"] = span;
            result["verifiedMajorEvent"] = verifiedRelevantMajorEvent;
            result["reason"] = verifiedRelevantMajorEvent ? "major_event_and_lasting_response" : sustained ? "sustained_personal_development" : "awaiting_substantial_evidence";
            if (verifiedRelevantMajorEvent || sustained) result["decision"] = "accepted";
            return result;
        }

        private static Dictionary<string, object> StoreNarrativeDevelopment(ReignDbConnection connection,
            string campaignId, string heroId, Dictionary<string, object> proposal, string sessionId, string exchangeId,
            string sourceEventId, double worldDay, long ts)
        {
            if (string.IsNullOrWhiteSpace(exchangeId) || string.IsNullOrWhiteSpace(sessionId))
                return new Dictionary<string, object> { ["status"] = "rejected", ["reason"] = "missing_conversation_evidence_identity" };
            var basis = ReadJsonObject(CharacterFile(campaignId, heroId, "narrative.json"));
            var rows = QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero AND category='narrative_development' ORDER BY first_ts;",
                new Dictionary<string, object> { ["hero"] = heroId });
            string id = "narrative_development_" + PromptHash(heroId + "|" + ReadString(proposal, "itemId", "") + "|" + exchangeId).Substring(0, 24);
            var replay = rows.FirstOrDefault(x => ReadString(x, "characteristic_id", "") == id);
            if (replay != null) return replay;
            var effective = ResolveEffectiveNarrative(new Dictionary<string, object> { ["narrative"] = basis,
                ["dynamicCharacteristics"] = new Dictionary<string, object> { ["active"] = rows.Where(x => ReadString(x, "status", "") == "active").ToList() } });
            var item = ReadDictionaryList(effective, "items").FirstOrDefault(x => ReadString(x, "id", "") == ReadString(proposal, "itemId", ""));
            var baseItem = ReadDictionaryList(basis, "items").FirstOrDefault(x => ReadString(x, "id", "") == ReadString(proposal, "itemId", ""));
            string basisFingerprint = NarrativeItemFingerprint(baseItem);
            long lastAccepted = rows.Where(x => ReadString(x, "status", "") == "active" && ReadString(x, "topic_key", "") == "narrative/" + ReadString(proposal, "itemId", ""))
                .Select(x => ReadLong(x, "first_ts", 0)).DefaultIfEmpty(0).Max();
            var observations = rows.Where(x => ReadLong(x, "first_ts", 0) > lastAccepted).Select(x => ReadDictionary(TryParseJsonObject(ReadString(x, "payload_json", "{}")), "narrativeDevelopment"))
                .Where(x => x != null && ReadString(x, "baseItemFingerprint", "") == basisFingerprint).ToList();
            string evidenceId = ReadString(proposal, "evidenceEventId", "");
            bool major = false;
            if (evidenceId.Length > 0 && item != null)
            {
                var history = QuerySql(connection, @"SELECT e.* FROM world_history_events e
WHERE e.event_id=$event AND e.campaign_id=$campaign AND e.source='native'
AND e.is_complete=1 AND e.phase='completed' AND e.world_day<=$day
AND e.timeline_id=(SELECT timeline_id FROM world_history_timelines WHERE is_active=1 AND campaign_id=$campaign ORDER BY updated_utc DESC LIMIT 1) LIMIT 1;",
                    new Dictionary<string, object> { ["event"] = evidenceId, ["campaign"] = campaignId, ["day"] = worldDay }).FirstOrDefault();
                var participants = QuerySql(connection, "SELECT entity_id FROM world_history_entities WHERE event_id=$event;",
                    new Dictionary<string, object> { ["event"] = evidenceId }).Select(x => ReadString(x, "entity_id", "")).ToList();
                var profile = ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json"));
                var family = ReadStringList(profile, "childrenIds").Concat(new[] { ReadString(profile, "fatherId", ""),
                    ReadString(profile, "motherId", ""), ReadString(profile, "spouseId", "") }).Where(x => x.Length > 0).ToList();
                major = NarrativeMajorEventRelevant(item, history, heroId, family, participants, ReadString(proposal, "supportingQuote", ""));
            }
            var decision = EvaluateNarrativeDevelopment(item, proposal, sessionId, worldDay, observations, major);
            decision["baseItemFingerprint"] = basisFingerprint;
            string state = ReadString(decision, "decision", "") == "accepted" ? "active" : "rejected";
            var payload = new Dictionary<string, object> { ["narrativeDevelopment"] = decision };
            ExecuteSql(connection, @"INSERT INTO dynamic_characteristics
(characteristic_id,owner_id,category,topic_key,text,normalized_key,status,confidence,importance,provenance,compatibility_status,rejection_reason,
source_event_id,source_session_id,source_exchange_id,world_day,first_ts,last_ts,payload_json)
VALUES($id,$hero,'narrative_development',$topic,$text,$id,$state,1,1,'evidence_gated_narrative_development',$decision,$reason,$event,$session,$exchange,$day,$ts,$ts,$payload)
ON CONFLICT(owner_id,normalized_key) DO NOTHING;", new Dictionary<string, object> { ["id"] = id, ["hero"] = heroId,
                ["topic"] = "narrative/" + ReadString(proposal, "itemId", ""), ["text"] = ReadString(proposal, "description", ""),
                ["state"] = state, ["decision"] = ReadString(decision, "decision", ""), ["reason"] = ReadString(decision, "reason", ""),
                ["event"] = sourceEventId, ["session"] = sessionId, ["exchange"] = exchangeId, ["day"] = worldDay, ["ts"] = ts,
                ["payload"] = Json.Serialize(payload) });
            if (state == "active")
            {
                ExecuteSql(connection, "UPDATE dynamic_characteristics SET status='superseded' WHERE owner_id=$hero AND category='narrative_development' AND topic_key=$topic AND characteristic_id<>$id AND status='active';",
                    new Dictionary<string, object> { ["hero"] = heroId, ["topic"] = "narrative/" + ReadString(proposal, "itemId", ""), ["id"] = id });
                InvalidatePromptRuntimeCache();
            }
            EnsureCharacterEditorSchema(connection);
            ExecuteSql(connection, "UPDATE character_editor_heads SET revision_token=$token,updated_ts=$ts WHERE hero_id=$hero;",
                new Dictionary<string, object> { ["token"] = PromptHash(id + "|" + ts), ["ts"] = ts, ["hero"] = heroId });
            return new Dictionary<string, object> { ["characteristic_id"] = id, ["status"] = state, ["narrativeDevelopment"] = decision };
        }

        private static bool NarrativeMajorEventRelevant(Dictionary<string, object> item, Dictionary<string, object> history,
            string heroId, List<string> nativeFamilyIds, List<string> participantIds, string quote)
        {
            string kind = ReadString(history, "event_type", "");
            if (ReadString(history, "source", "") != "native" || ReadInt(history, "is_complete", 0) != 1
                || ReadString(history, "phase", "") != "completed"
                || !Regex.IsMatch(kind, "death|kill|captur|rescue|marriage|birth|battle|siege|betray|succession|fief|conquest", RegexOptions.IgnoreCase)) return false;
            bool personal = participantIds.Contains(heroId);
            bool closeFamily = nativeFamilyIds.Any(participantIds.Contains);
            if (!personal && !closeFamily) return false;
            string concern = ReadString(item, "title", "") + " " + ReadString(item, "description", "") + " " + ReadString(item, "personalMeaning", "");
            string account = ReadString(history, "summary", "") + " " + ReadString(history, "semantic_text", "");
            var specific = DynamicEvidenceTerms(ReadString(item, "title", ""));
            specific.ExceptWith(new[] { "people", "someone", "being", "having", "about", "their", "losing", "unable", "making" });
            var eventTerms = DynamicEvidenceTerms(account);
            // Direct matching needs two content anchors across the concern and response.
            if (specific.Count(x => eventTerms.Contains(x)) >= 2 && HasDynamicTextEvidence(account, quote)) return true;
            // These subject groups cover native vocabulary differences (e.g. died/outliving)
            // without treating mere temporal proximity as personal relevance.
            var subjects = new[] {
                new[] { "death|kill", @"\b(death|dying|died|dead|outliving|bereave|grief|loved|family|parent|mother|father|spouse|child)\b", @"\b(death|died|dead|lost|loss|grief|gone|bereave)\b" },
                new[] { "captur|rescue", @"\b(captive|captivity|trapped|confined|escape|rescue|freedom|dependen|protect)\w*", @"\b(captive|captur|prison|rescue|freedom|escape|release)\w*" },
                new[] { "marriage|birth", @"\b(family|kin|child|parent|mother|father|spouse|marri|home|belong|affection)\w*", @"\b(birth|born|child|marri|family|parent)\w*" },
                new[] { "battle|siege|conquest", @"\b(battle|war|fighting|violence|courage|defeat|victory|defen|protect|household|home)\w*", @"\b(battle|siege|war|fighting|defeat|victory|defen|attack)\w*" },
                new[] { "betray", @"\b(trust|loyal|betray|confidence|friendship)\w*", @"\b(trust|betray|loyal|deceiv)\w*" },
                new[] { "succession|fief", @"\b(inherit|family|rank|authority|household|power|home|land|duty)\w*", @"\b(inherit|succeed|succession|fief|land|authority|rule)\w*" }
            };
            return subjects.Any(x => Regex.IsMatch(kind, x[0], RegexOptions.IgnoreCase)
                && Regex.IsMatch(concern, x[1], RegexOptions.IgnoreCase) && Regex.IsMatch(quote ?? "", x[2], RegexOptions.IgnoreCase));
        }
    }
}
