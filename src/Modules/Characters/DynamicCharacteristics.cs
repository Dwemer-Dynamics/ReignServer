using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly HashSet<string> DynamicCharacteristicCategories = new HashSet<string>(new[]
        {
            "personal_history", "formative_experience", "habit", "preference", "aversion",
            "local_connection", "skill_practice", "personal_value", "social_tendency", "aspiration"
        }, StringComparer.OrdinalIgnoreCase);

        private const string DynamicCharacteristicsPromptPolicy =
            "Dynamic Characteristics are generated personal history: low-impact details the current NPC creates about their own past, habits, preferences, formative experiences, local ties, practiced skills, values, social tendencies, or aspirations. " +
            "They are soft character canon, not objective proof about the campaign world or another person. Emit exactly one dynamicCharacteristicWrites item whenever the visible reply actually introduces or materially develops a stable compatible self-detail; otherwise emit []. Do not omit the write merely because the detail appears in natural dialogue rather than a formal biography. " +
            "Do not create one merely because the player asks a recall question. Never use this lane for current location, current ownership, troop counts, titles, clan or kingdom membership, named relatives, marriages, deaths, crimes by another person, wars, conquests, rescues, gifts, or other externally verifiable events. " +
            "A turn-specific stance, accusation, conversational tally, bargaining metaphor or poetic self-description is not a stable biographical detail. Do not turn apologies into debts or repeated dialogue framing into a new personality trait. " +
            "Use a stable topicKey such as upbringing_place, market_morning_habit, woodworking_training, or fear_of_deep_water. If an existing entry covers that topic, develop it consistently instead of writing a duplicate or contradiction. Authoritative native and world-history evidence always overrides soft canon. " +
            "A skill_practice detail may explain where or how the NPC practiced, but it never changes or outranks the supplied native skill value and proficiency description; do not turn low proficiency into factual expertise.";

        private const string DynamicCharacteristicsOutputContract =
            "Required JSON field: dynamicCharacteristicWrites is an array containing at most one object with text, category, topicKey, confidence, importance, and sourceBasis. Before returning JSON, inspect the visible reply you wrote: if it establishes or materially develops a stable compatible self-detail, this array must contain that detail; use [] only when the reply contains no such detail. " +
            "text is a concise third-person fact about the speaking NPC which was actually expressed in the visible reply. category must be personal_history, formative_experience, habit, preference, aversion, local_connection, skill_practice, personal_value, social_tendency, or aspiration. " +
            "sourceBasis must be npc_generated_personal_history. Use [] when no compatible new personal detail was introduced." + NarrativeDevelopmentContract;

        private static void EnsureDynamicCharacteristicsSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS dynamic_characteristics (
characteristic_id TEXT PRIMARY KEY,
owner_id TEXT NOT NULL,
category TEXT NOT NULL DEFAULT 'personal_history',
topic_key TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL,
normalized_key TEXT NOT NULL,
status TEXT NOT NULL DEFAULT 'active',
confidence REAL NOT NULL DEFAULT 0.65,
importance REAL NOT NULL DEFAULT 0.5,
provenance TEXT NOT NULL DEFAULT 'npc_generated_personal_history',
compatibility_status TEXT NOT NULL DEFAULT 'compatible_soft_canon',
rejection_reason TEXT NOT NULL DEFAULT '',
source_event_id TEXT NOT NULL DEFAULT '',
source_session_id TEXT NOT NULL DEFAULT '',
source_exchange_id TEXT NOT NULL DEFAULT '',
source_turn_ids_json TEXT NOT NULL DEFAULT '[]',
source_mode TEXT NOT NULL DEFAULT '',
location_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL DEFAULT 0,
known_by_json TEXT NOT NULL DEFAULT '[]',
first_ts INTEGER NOT NULL,
last_ts INTEGER NOT NULL,
repeat_count INTEGER NOT NULL DEFAULT 1,
payload_json TEXT NOT NULL DEFAULT '{}');");
            ExecuteSql(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_dynamic_characteristic_owner_key ON dynamic_characteristics(owner_id,normalized_key);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_dynamic_characteristic_owner_status ON dynamic_characteristics(owner_id,status,last_ts DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_dynamic_characteristic_topic ON dynamic_characteristics(owner_id,topic_key,status);");
        }

        private static List<Dictionary<string, object>> NormalizeDynamicCharacteristicWrites(
            Dictionary<string, object> parsed,
            string heroId,
            Dictionary<string, object> profile,
            Dictionary<string, object> payload,
            string visibleReply)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            if (parsed == null || string.IsNullOrWhiteSpace(heroId) || string.IsNullOrWhiteSpace(visibleReply)) return result;
            List<Dictionary<string, object>> raw = ReadDictionaryList(parsed, "dynamicCharacteristicWrites")
                .Concat(ReadDictionaryList(parsed, "dynamic_characteristic_writes")).Take(1).ToList();
            if (raw.Count == 0)
            {
                Dictionary<string, object> inferred = InferExplicitDynamicPreferenceOrAversion(profile, visibleReply);
                if (inferred == null)
                    inferred = InferExplicitDynamicHabit(profile, visibleReply);
                if (inferred == null)
                    inferred = InferExplicitDynamicKeptObject(profile, visibleReply);
                if (inferred != null) raw.Add(inferred);
            }
            foreach (Dictionary<string, object> item in raw)
            {
                string text = LimitText(ReadFirstString(item, "text", "fact", "characteristic", "summary").Trim(), 500);
                string category = NormalizeLookup(ReadString(item, "category", "personal_history")).Replace(' ', '_').Replace('-', '_');
                if (!DynamicCharacteristicCategories.Contains(category)) category = "personal_history";
                string topicKey = NormalizeDynamicTopicKey(ReadFirstString(item, "topicKey", "topic_key"), category, text);
                string sourceBasis = NormalizeLookup(ReadFirstString(item, "sourceBasis", "source_basis"));
                if (string.IsNullOrWhiteSpace(sourceBasis)) sourceBasis = "npc_generated_personal_history";
                string rejection = DynamicCharacteristicRejectionReason(text, heroId, profile, payload, visibleReply);
                var development = NormalizeNarrativeDevelopmentProposal(ReadDictionary(item, "narrativeDevelopment"), visibleReply);
                if (development != null)
                    development["compatibilityValidated"] = string.IsNullOrWhiteSpace(rejection)
                        && string.IsNullOrWhiteSpace(DynamicCharacteristicRejectionReason(ReadString(development, "description", ""), heroId, profile, payload, visibleReply))
                        && string.IsNullOrWhiteSpace(DynamicCharacteristicRejectionReason(ReadString(development, "personalMeaning", ""), heroId, profile, payload, visibleReply));
                result.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["text"] = text,
                    ["category"] = category,
                    ["topicKey"] = topicKey,
                    ["confidence"] = ClampDouble(ReadDouble(item, "confidence", 0.65d), 0.35d, 0.9d),
                    ["importance"] = ClampDouble(ReadDouble(item, "importance", 0.5d), 0.15d, 0.85d),
                    ["sourceBasis"] = sourceBasis,
                    ["narrativeDevelopment"] = development,
                    ["status"] = string.IsNullOrWhiteSpace(rejection) ? "active" : "rejected",
                    ["compatibilityStatus"] = string.IsNullOrWhiteSpace(rejection) ? "compatible_soft_canon" : "rejected_conflict_or_scope",
                    ["rejectionReason"] = rejection
                });
            }
            return result;
        }

        private static Dictionary<string, object> InferExplicitDynamicPreferenceOrAversion(
            Dictionary<string, object> profile,
            string visibleReply)
        {
            // The main model owns characterization. This narrow fallback only
            // preserves an explicit first-person stable preference/aversion that
            // is already present verbatim in the visible reply when the model
            // omitted the accompanying structured write.
            string reply = visibleReply ?? "";
            Match match = Regex.Match(reply,
                @"\bI\s+(cannot\s+stand|can't\s+stand|dislike|detest|hate|avoid|fear|prefer|like|enjoy|favor|favour|value|admire)\s+([^.!?\r\n]{3,280})",
                RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string sourceVerb = Regex.Replace(match.Groups[1].Value.Trim().ToLowerInvariant(), @"\s+", " ");
            string detail = Regex.Replace(match.Groups[2].Value.Trim(), @"\s+", " ").Trim(' ', ',', ';', ':', '-', '\u2014');
            if (detail.Length < 3 || DynamicCharacteristicMatchIsMetalinguistic(reply, match, detail)) return null;
            bool aversion = new[] { "cannot stand", "can't stand", "dislike", "detest", "hate", "avoid", "fear" }
                .Contains(sourceVerb, StringComparer.OrdinalIgnoreCase);
            string thirdPersonVerb;
            switch (sourceVerb)
            {
                case "prefer": thirdPersonVerb = "prefers"; break;
                case "like": thirdPersonVerb = "likes"; break;
                case "enjoy": thirdPersonVerb = "enjoys"; break;
                case "favor": thirdPersonVerb = "favors"; break;
                case "favour": thirdPersonVerb = "favours"; break;
                case "value": thirdPersonVerb = "values"; break;
                case "admire": thirdPersonVerb = "admires"; break;
                case "dislike": thirdPersonVerb = "dislikes"; break;
                case "detest": thirdPersonVerb = "detests"; break;
                case "hate": thirdPersonVerb = "hates"; break;
                case "avoid": thirdPersonVerb = "avoids"; break;
                case "fear": thirdPersonVerb = "fears"; break;
                default: thirdPersonVerb = "cannot stand"; break;
            }
            string name = FirstNonEmpty(ReadString(profile, "name", ""), "The speaking character");
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = LimitText(name + " " + thirdPersonVerb + " " + detail + ".", 500),
                ["category"] = aversion ? "aversion" : sourceVerb.Equals("value", StringComparison.OrdinalIgnoreCase)
                    || sourceVerb.Equals("admire", StringComparison.OrdinalIgnoreCase) ? "personal_value" : "preference",
                ["topicKey"] = "",
                ["confidence"] = 0.7d,
                ["importance"] = 0.45d,
                ["sourceBasis"] = "npc_generated_personal_history",
                ["inference"] = "deterministic_explicit_visible_reply"
            };
        }

        private static Dictionary<string, object> InferExplicitDynamicHabit(
            Dictionary<string, object> profile,
            string visibleReply)
        {
            // Preserve only a stable habit the speaker states explicitly. This is a
            // narrow companion-write repair, not a free-form characterization pass.
            string reply = visibleReply ?? "";
            Match match = Regex.Match(reply,
                @"\bI\s+(always|usually|still)\s+([^.!?\r\n]{3,220})",
                RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string frequency = match.Groups[1].Value.Trim().ToLowerInvariant();
            string detail = Regex.Replace(match.Groups[2].Value.Trim(), @"\s+", " ")
                .Trim(' ', ',', ';', ':', '-', '\u2014');
            string normalized = NormalizeLookup(detail);
            if (detail.Length < 3
                || DynamicCharacteristicMatchIsMetalinguistic(reply, match, detail)
                || Regex.IsMatch(normalized,
                    @"^(?:do|did|feel|find|keep|have|remember|think|believe|want|mean)\s+(?:it|this|that|so)\b",
                    RegexOptions.IgnoreCase)
                || MemoryQueryTerms(detail).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                return null;
            string name = FirstNonEmpty(ReadString(profile, "name", ""), "The speaking character");
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = LimitText(name + " " + frequency + " " + detail + ".", 500),
                ["category"] = "habit",
                ["topicKey"] = "",
                ["confidence"] = 0.7d,
                ["importance"] = 0.45d,
                ["sourceBasis"] = "npc_generated_personal_history",
                ["inference"] = "deterministic_explicit_visible_reply"
            };
        }

        private static Dictionary<string, object> InferExplicitDynamicKeptObject(
            Dictionary<string, object> profile,
            string visibleReply)
        {
            // Preserve an explicitly stated, low-impact personal keepsake when the
            // model omits its companion write. This records only the visible fact;
            // it does not invent the object's origin or promote it to world canon.
            string reply = visibleReply ?? "";
            foreach (Match match in Regex.Matches(reply,
                @"\bI\s+keep\s+(a|an|my)\s+([^.!?\r\n]{2,160})",
                RegexOptions.IgnoreCase))
            {
                int prefixStart = Math.Max(0, match.Index - 100);
                string prefix = reply.Substring(prefixStart, match.Index - prefixStart);
                if (Regex.IsMatch(prefix,
                    @"(?:you\s+(?:said|told\s+me|wrote)|quot(?:e|ing)|according\s+to\s+you)[^.!?\r\n]{0,80}$",
                    RegexOptions.IgnoreCase))
                    continue;

                string article = match.Groups[1].Value.Trim().ToLowerInvariant();
                string detail = Regex.Replace(match.Groups[2].Value.Trim(), @"\s+", " ")
                    .Trim(' ', ',', ';', ':', '-', '\u2014');
                string normalized = NormalizeLookup(detail);
                if (detail.Length < 3
                    || DynamicCharacteristicMatchIsMetalinguistic(reply, match, detail)
                    || MemoryQueryTerms(detail).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 1
                    || Regex.IsMatch(normalized,
                        @"^(?:record|records|ledger|list|account|accounts|track|watch|guard|troops?|soldiers?|gold|denars?|prisoners?|inventory)\b",
                        RegexOptions.IgnoreCase))
                    continue;

                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    // Keep the minimal first-person clause verbatim. Besides avoiding
                    // invention, this lets the normal grounding check prove even a
                    // one-word object ("I keep a die") without depending on the
                    // visible prose to repeat the speaker's full name.
                    ["text"] = LimitText("I keep " + article + " " + detail + ".", 500),
                    ["category"] = "personal_history",
                    ["topicKey"] = "",
                    ["confidence"] = 0.7d,
                    ["importance"] = 0.45d,
                    ["sourceBasis"] = "npc_generated_personal_history",
                    ["inference"] = "deterministic_explicit_visible_reply"
                };
            }
            return null;
        }

        private static bool DynamicCharacteristicMatchIsMetalinguistic(string reply, Match match, string detail)
        {
            if (Regex.IsMatch(detail ?? "", @"^(?:and|or|but|nor)\b", RegexOptions.IgnoreCase))
                return true;
            int prefixStart = Math.Max(0, match.Index - 120);
            string prefix = (reply ?? "").Substring(prefixStart, match.Index - prefixStart);
            return Regex.IsMatch(prefix, @"\b(?:what|whether)\s*$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(prefix, @"\b(?:as\s+though|as\s+if)\s*$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(prefix,
                    @"\b(?:never|did\s+not|didn't|do\s+not|don't|will\s+not|won't|refuse\s+to)\s+(?:[^.!?\r\n]{0,80}\s+)?(?:say|said|mention|mentioned|tell|share|explain)\s+(?:[^.!?\r\n]{0,40}\s+)?$",
                    RegexOptions.IgnoreCase);
        }

        private static bool ReplyLikelyIntroducesDynamicCharacteristic(string visibleReply)
        {
            string reply = visibleReply ?? "";
            if (InferExplicitDynamicPreferenceOrAversion(new Dictionary<string, object>(), reply) != null) return true;
            Match match = Regex.Match(reply,
                @"\b(?:when\s+i\s+was(?:\s+young)?|as\s+a\s+child|i\s+learned\s+to|i\s+keep\s+(?:a|an|my)|i\s+(?:always|usually|still)\s+\w+|i\s+practice\b|i\s+hope\s+to|i\s+aspire\s+to)\b",
                RegexOptions.IgnoreCase);
            return match.Success && !DynamicCharacteristicMatchIsMetalinguistic(reply, match, "");
        }

        private static string NormalizeDynamicTopicKey(string supplied, string category, string text)
        {
            string value = Regex.Replace(NormalizeLookup(supplied), @"[^a-z0-9]+", "_").Trim('_');
            if (!string.IsNullOrWhiteSpace(value)) return LimitText(value, 80);
            List<string> terms = MemoryQueryTerms(text).Take(5).ToList();
            return LimitText((category ?? "personal_history") + "_" + string.Join("_", terms), 80);
        }

        private static string DynamicCharacteristicRejectionReason(string text, string heroId,
            Dictionary<string, object> profile, Dictionary<string, object> payload, string visibleReply)
        {
            string clean = (text ?? "").Trim();
            if (clean.Length < 12) return "too_short";
            if (string.IsNullOrWhiteSpace(visibleReply)
                || visibleReply.IndexOf(FirstDynamicEvidencePhrase(clean), StringComparison.OrdinalIgnoreCase) < 0
                    && !HasDynamicTextEvidence(clean, visibleReply))
                return "not_expressed_in_visible_reply";
            if (IsConversationCharacteristicRhetoric(clean)) return "conversation_rhetoric_is_not_stable_characterization";
            string q = NormalizeLookup(clean);
            if (!DynamicCharacteristicIsAboutSpeaker(clean, profile))
                return "not_about_speaking_character";
            if (Regex.IsMatch(q, @"\b(?:we|i)\s+(?:am|are|'m|'re)\s+(?:currently|presently|now)\s+(?:in|at)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(q, @"\bcurrently\s+(?:owns?|rules?|commands?|has)\b", RegexOptions.IgnoreCase))
                return "current_world_state_is_not_dynamic_characterization";
            if (Regex.IsMatch(clean, @"\b(?i:my|his|her|their)\s+(?i:wife|husband|spouse|father|mother|parent|son|daughter|child|brother|sister)\s+(?i:is|was|named|called)\s+(?!(?i:a|an|the)\b)[A-Z][\p{L}'-]+")
                || Regex.IsMatch(clean, @"\b[A-Z][\p{L}'-]+(?:'s|\u2019s)\s+(?i:wife|husband|spouse|father|mother|parent|son|daughter|child|brother|sister)\s+(?i:is|was|named|called)\s+(?!(?i:a|an|the)\b)[A-Z][\p{L}'-]+")
                || Regex.IsMatch(q, @"\b(?:i am|i'm|he is|he's|she is|she's|they are|they're)\s+(?:unmarried|married|widowed)\b", RegexOptions.IgnoreCase))
                return "family_links_require_native_evidence";
            if (ContainsAny(q, "conquered ", "captured ", "declared war", "made peace", "became king", "became queen",
                    "rules the kingdom", "owns the town", "owns the castle", "murdered ", "assassinated ", "saved your family"))
                return "externally_verifiable_event_requires_world_history";
            if (Regex.IsMatch(q, @"\b\d+\s+(?:troops|men|soldiers|denars|gold)\b", RegexOptions.IgnoreCase))
                return "current_quantity_requires_native_evidence";
            return "";
        }

        private static string FirstDynamicEvidencePhrase(string text)
        {
            return string.Join(" ", Regex.Split(text ?? "", @"\s+").Where(x => x.Length > 2).Take(3));
        }

        private static double DynamicTextOverlap(string left, string right)
        {
            HashSet<string> a = new HashSet<string>(MemoryQueryTerms(left), StringComparer.OrdinalIgnoreCase);
            HashSet<string> b = new HashSet<string>(MemoryQueryTerms(right), StringComparer.OrdinalIgnoreCase);
            if (a.Count == 0) return 0d;
            return a.Count(x => b.Contains(x)) / (double)a.Count;
        }

        private static bool HasDynamicTextEvidence(string summary, string visibleReply)
        {
            // Grounding must inspect the complete visible reply. MemoryQueryTerms intentionally
            // keeps only the first 32 distinct terms for retrieval, which made valid details late
            // in a longer roleplay response look absent.
            HashSet<string> summaryTerms = DynamicEvidenceTerms(summary);
            HashSet<string> replyTerms = DynamicEvidenceTerms(visibleReply);
            int shared = summaryTerms.Count(x => replyTerms.Contains(x));
            if (summaryTerms.Count == 0) return false;
            // Three distinctive content anchors are sufficient even when the stored
            // characteristic is a long provenance-rich sentence and the visible
            // reply is a terse paraphrase. Requiring the reply to repeat a fixed
            // percentage of every source word incorrectly rejects grounded recall
            // such as "my saddle strap was loose; I was not getting on."
            return shared >= Math.Min(3, summaryTerms.Count);
        }

        private static HashSet<string> DynamicEvidenceTerms(string text)
        {
            HashSet<string> stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "you", "your", "for", "with", "that", "this", "from", "have", "will",
                "what", "when", "where", "who", "whose", "into", "before", "after", "while", "then",
                "was", "were", "are", "his", "her", "their", "him", "she", "they", "any"
                , "person", "thing", "things", "own", "life", "year", "years", "age",
                "child", "childhood", "habit", "preference", "past", "history", "character",
                "formed", "ordinary", "exact", "detail"
            };
            return new HashSet<string>(
                Regex.Split((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}_-]+")
                    .Where(x => x.Length >= 3 && !stopWords.Contains(x))
                    .Select(NormalizeDynamicEvidenceToken)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(256),
                StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeDynamicEvidenceToken(string token)
        {
            string value = token ?? "";
            if (value.Length > 7 && value.EndsWith("ness", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 4);
            else if (value.Length > 6 && value.EndsWith("ing", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 3);
            else if (value.Length > 5 && value.EndsWith("ed", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 2);
            else if (value.Length > 5 && value.EndsWith("ly", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 2);
            if (value.Length > 4 && value.EndsWith("s", StringComparison.Ordinal)
                && !value.EndsWith("ss", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 1);
            return value;
        }

        private static bool DynamicCharacteristicIsAboutSpeaker(string text, Dictionary<string, object> profile)
        {
            string normalized = NormalizeLookup(text);
            if (Regex.IsMatch(normalized, @"\b(i|my|me|mine|we|our|this character|the speaker)\b", RegexOptions.IgnoreCase)) return true;
            string fullName = ReadString(profile, "name", "").Trim();
            if (!string.IsNullOrWhiteSpace(fullName) && text.IndexOf(fullName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            List<string> distinctiveNameTerms = Regex.Split(fullName, @"[^\p{L}\p{Nd}]+")
                // Bannerlord generates valid short given names (for example, Col).
                // Requiring four characters made a natural third-person companion
                // write fail ownership validation even though it named its speaker.
                .Where(x => x.Length >= 2)
                .Where(x => !new[] { "of", "the", "lady", "lord" }.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return distinctiveNameTerms.Any(term => Regex.IsMatch(text, @"\b" + Regex.Escape(term) + @"\b", RegexOptions.IgnoreCase));
        }

        private static Dictionary<string, object> StoreDynamicCharacteristicWrites(
            string campaignId,
            string heroId,
            List<Dictionary<string, object>> writes,
            Dictionary<string, object> payload,
            Dictionary<string, object> conversationExchange,
            string sourceEventId,
            string sourceMode,
            string locationId,
            long ts,
            IEnumerable<string> audienceIds)
        {
            List<Dictionary<string, object>> stored = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            if (writes == null || writes.Count == 0)
                return new Dictionary<string, object> { ["ok"] = true, ["stored"] = stored, ["rejected"] = rejected, ["storedCount"] = 0, ["rejectedCount"] = 0 };

            string sessionId = ReadFirstString(conversationExchange, "sessionId", "session_id");
            string exchangeId = ReadFirstString(conversationExchange, "exchangeId", "exchange_id");
            List<string> turnIds = ReadStringList(conversationExchange, "turnIds");
            List<string> knownBy = MergeStringLists(audienceIds, new[] { heroId });
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            lock (NarrativeStateLock)
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                foreach (Dictionary<string, object> write in writes.Take(1))
                {
                    var development = ReadDictionary(write, "narrativeDevelopment");
                    if (development != null && development.Count > 0)
                    {
                        var result = StoreNarrativeDevelopment(connection, campaignId, heroId, development, sessionId, exchangeId, sourceEventId, worldDay, ts);
                        if (ReadString(result, "status", "") == "active") stored.Add(result); else rejected.Add(result);
                        continue;
                    }
                    string text = ReadString(write, "text", "").Trim();
                    string normalized = NormalizeLookup(text);
                    if (string.IsNullOrWhiteSpace(normalized)) continue;
                    string id = "dynamic_" + PromptHash(heroId + "|" + normalized).Substring(0, 24).ToLowerInvariant();
                    string status = ReadString(write, "status", "rejected");
                    string topicKey = ReadString(write, "topicKey", "");
                    string rejection = ReadString(write, "rejectionReason", "");
                    Dictionary<string, object> existing = QuerySql(connection,
                        "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner AND normalized_key=$normalized LIMIT 1;",
                        new Dictionary<string, object> { ["owner"] = heroId, ["normalized"] = normalized }).FirstOrDefault();
                    if (existing != null)
                    {
                        knownBy = MergeStringLists(knownBy, TextListFromJson(ReadString(existing, "known_by_json", "[]")));
                        turnIds = MergeStringLists(turnIds, TextListFromJson(ReadString(existing, "source_turn_ids_json", "[]")));
                    }
                    if (existing == null && status.Equals("active", StringComparison.OrdinalIgnoreCase))
                    {
                        // Preserve the original and record the new rejected paraphrase with its own
                        // lineage. Changing a topic key must not create a second active self-story.
                        var equivalent = QuerySql(connection,
                            "SELECT characteristic_id,text FROM dynamic_characteristics WHERE owner_id=$owner AND status='active' AND category<>'narrative_development' ORDER BY last_ts DESC LIMIT 250;",
                            new Dictionary<string, object> { ["owner"] = heroId })
                            .FirstOrDefault(row => DynamicCharacteristicsEquivalent(ReadString(row, "text", ""), text));
                        if (equivalent != null)
                        {
                            status = "rejected";
                            rejection = "duplicate_characteristic:" + ReadString(equivalent, "characteristic_id", "");
                            write["status"] = status;
                            write["compatibilityStatus"] = "rejected_duplicate";
                            write["rejectionReason"] = rejection;
                        }
                    }
                    if (status.Equals("active", StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> conflict = QuerySql(connection, @"SELECT characteristic_id,text FROM dynamic_characteristics
WHERE owner_id=$owner AND topic_key=$topic AND status='active' AND normalized_key<>$normalized ORDER BY last_ts DESC LIMIT 1;",
                            new Dictionary<string, object> { ["owner"] = heroId, ["topic"] = topicKey, ["normalized"] = normalized }).FirstOrDefault();
                        if (conflict != null)
                        {
                            status = "rejected";
                            rejection = "topic_conflicts_with_active_characteristic:" + ReadString(conflict, "characteristic_id", "");
                            write["status"] = status;
                            write["compatibilityStatus"] = "rejected_conflict_or_scope";
                            write["rejectionReason"] = rejection;
                        }
                    }

                    Dictionary<string, object> rowPayload = new Dictionary<string, object>
                    {
                        ["sourceBasis"] = ReadString(write, "sourceBasis", "npc_generated_personal_history"),
                        ["sourceCorrelationId"] = ReadFirstString(payload, "correlationId", "requestId"),
                        ["audienceIds"] = knownBy
                    };
                    ExecuteSql(connection, @"INSERT INTO dynamic_characteristics(
characteristic_id,owner_id,category,topic_key,text,normalized_key,status,confidence,importance,provenance,compatibility_status,rejection_reason,
source_event_id,source_session_id,source_exchange_id,source_turn_ids_json,source_mode,location_id,world_day,known_by_json,first_ts,last_ts,repeat_count,payload_json)
VALUES($id,$owner,$category,$topic,$text,$normalized,$status,$confidence,$importance,'npc_generated_personal_history',$compatibility,$rejection,
$event,$session,$exchange,$turns,$mode,$location,$day,$known,$ts,$ts,1,$payload)
ON CONFLICT(owner_id,normalized_key) DO UPDATE SET
last_ts=$ts,
repeat_count=dynamic_characteristics.repeat_count+1,
known_by_json=$known,
source_turn_ids_json=$turns,
status=CASE WHEN dynamic_characteristics.status='active' THEN 'active' ELSE excluded.status END,
compatibility_status=CASE WHEN dynamic_characteristics.status='active' THEN dynamic_characteristics.compatibility_status ELSE excluded.compatibility_status END,
rejection_reason=CASE WHEN dynamic_characteristics.status='active' THEN dynamic_characteristics.rejection_reason ELSE excluded.rejection_reason END,
payload_json=$payload;",
                        new Dictionary<string, object>
                        {
                            ["id"] = id, ["owner"] = heroId, ["category"] = ReadString(write, "category", "personal_history"), ["topic"] = topicKey,
                            ["text"] = text, ["normalized"] = normalized, ["status"] = status,
                            ["confidence"] = ReadDouble(write, "confidence", 0.65d), ["importance"] = ReadDouble(write, "importance", 0.5d),
                            ["compatibility"] = ReadString(write, "compatibilityStatus", "compatible_soft_canon"), ["rejection"] = rejection,
                            ["event"] = sourceEventId ?? "", ["session"] = sessionId, ["exchange"] = exchangeId,
                            ["turns"] = Json.Serialize(turnIds), ["mode"] = sourceMode ?? "", ["location"] = locationId ?? "",
                            ["day"] = worldDay, ["known"] = Json.Serialize(knownBy), ["ts"] = ts, ["payload"] = Json.Serialize(rowPayload)
                        });
                    Dictionary<string, object> saved = QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE characteristic_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = id }).FirstOrDefault() ?? new Dictionary<string, object>();
                    if (ReadString(saved, "status", status).Equals("active", StringComparison.OrdinalIgnoreCase)) stored.Add(saved); else rejected.Add(saved);
                }
                ProjectDynamicCharacteristicsFile(campaignId, heroId, connection);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["stored"] = stored, ["rejected"] = rejected,
                ["storedCount"] = stored.Count, ["rejectedCount"] = rejected.Count
            };
        }

        private static void ProjectDynamicCharacteristicsFile(string campaignId, string heroId, ReignDbConnection connection)
        {
            List<Dictionary<string, object>> rows = QuerySql(connection,
                "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'superseded' THEN 1 ELSE 2 END,importance DESC,last_ts DESC LIMIT 250;",
                new Dictionary<string, object> { ["owner"] = heroId });
            // Current narrative overrides must not disappear when ordinary memories fill
            // the bounded display projection. History remains in the temporal database.
            var narrativeRows = QuerySql(connection,
                "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner AND category='narrative_development' AND status='active';",
                new Dictionary<string, object> { ["owner"] = heroId });
            rows = rows.Concat(narrativeRows).GroupBy(x => ReadString(x, "characteristic_id", "")).Select(x => x.First()).ToList();
            WriteJsonObject(CharacterFile(campaignId, heroId, "dynamic_characteristics.json"), new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["sectionName"] = "Dynamic Characteristics",
                ["heroStringId"] = heroId,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["active"] = rows.Where(x => ReadString(x, "status", "") == "active").ToList(),
                ["rejected"] = rows.Where(x => ReadString(x, "status", "") == "rejected").ToList(),
                ["superseded"] = rows.Where(x => ReadString(x, "status", "") == "superseded").ToList()
            });
        }

        private static string FormatDynamicCharacteristicsForPrompt(Dictionary<string, object> document)
        {
            return FormatDynamicCharacteristicsForPrompt(document, "");
        }

        private static string FormatDynamicCharacteristicsForPrompt(
            Dictionary<string, object> document,
            string relevanceQuery)
        {
            List<Dictionary<string, object>> active = SelectNaturalDynamicCharacteristics(document, relevanceQuery);
            if (active.Count == 0) return "";
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("These compatible details were generated through prior roleplay and are now soft personal canon.");
            builder.AppendLine("Use a detail only when it helps this exchange; it is not an instruction to repeat its wording, metaphor or attitude. Authoritative native or world-history evidence overrides them. They prove only this character's own established self-story, not external claims about other people or world events.");
            foreach (Dictionary<string, object> row in active)
            {
                builder.Append("- [").Append(ReadString(row, "category", "personal_history")).Append("] ")
                    .AppendLine(LimitText(ReadString(row, "text", ""), 360));
            }
            return LimitText(builder.ToString().Trim(), 5200);
        }

        private static List<Dictionary<string, object>> RunDynamicCharacteristicsSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, pass, summary, data) => rows.Add(new Dictionary<string, object>
            { ["id"] = "dynamic_characteristics_" + id, ["suite"] = "dynamic_characteristics", ["passed"] = pass, ["summary"] = summary, ["data"] = data });
            string campaign = "dynamic_characteristics_test_" + Guid.NewGuid().ToString("N");
            string hero = "npc_dynamic";
            Dictionary<string, object> profile = new Dictionary<string, object>
            { ["heroStringId"] = hero, ["name"] = "Purios", ["currentSettlementName"] = "Danustica", ["currentSettlementId"] = "town_ES1" };
            UpsertCharacterProfile(campaign, profile);
            Dictionary<string, object> payload = new Dictionary<string, object>
            { ["worldDay"] = 42d, ["playerHeroStringId"] = "main_hero", ["locationId"] = "town_ES1", ["mode"] = "dialogue" };
            Dictionary<string, object> parsed = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["text"] = "I learned the market lanes of Danustica while carrying baskets for merchants as a youth.",
                        ["category"] = "formative_experience", ["topicKey"] = "market_youth", ["confidence"] = 0.7d,
                        ["sourceBasis"] = "npc_generated_personal_history"
                    }
                }
            };
            List<Dictionary<string, object>> accepted = NormalizeDynamicCharacteristicWrites(parsed, hero, profile, payload,
                "I learned the market lanes of Danustica while carrying baskets for merchants as a youth.");
            Dictionary<string, object> stored = StoreDynamicCharacteristicWrites(campaign, hero, accepted, payload,
                new Dictionary<string, object> { ["sessionId"] = "session_a", ["exchangeId"] = "exchange_a", ["turnIds"] = new List<string> { "turn_a" } },
                "event_a", "dialogue", "town_ES1", 100, new[] { hero, "main_hero" });
            add("compatible_personal_history_persists", ReadInt(stored, "storedCount", 0) == 1,
                "A compatible self-created personal detail becomes durable soft canon with source lineage.", stored);
            Dictionary<string, object> projected = ReadJsonObject(CharacterFile(campaign, hero, "dynamic_characteristics.json"));
            string prompt = FormatDynamicCharacteristicsForPrompt(projected);
            add("active_entries_reach_prompt", prompt.Contains("market lanes", StringComparison.OrdinalIgnoreCase)
                && prompt.Contains("soft personal canon", StringComparison.OrdinalIgnoreCase),
                "Active Dynamic Characteristics are explicitly included in the stable character prompt.", prompt);
            List<Dictionary<string, object>> overflowRows = new List<Dictionary<string, object>>();
            overflowRows.Add(new Dictionary<string, object>
            {
                ["status"] = "active", ["category"] = "habit", ["topic_key"] = "first_lamp_habit",
                ["text"] = "The speaker's earliest established habit was counting blue lamps before entering a room.",
                ["importance"] = 0.1d, ["first_ts"] = 1L, ["last_ts"] = 1L
            });
            for (int index = 1; index < 22; index++)
            {
                overflowRows.Add(new Dictionary<string, object>
                {
                    ["status"] = "active", ["category"] = "preference", ["topic_key"] = "newer_topic_" + index,
                    ["text"] = "Newer unrelated preference number " + index + " concerns an ordinary cup.",
                    ["importance"] = 0.9d, ["first_ts"] = 100L + index, ["last_ts"] = 100L + index
                });
            }
            string overflowPrompt = FormatDynamicCharacteristicsForPrompt(
                new Dictionary<string, object> { ["active"] = overflowRows },
                "Return to the first small habit you shared at our earliest meeting.");
            add("relevance_selects_older_entry_beyond_sixteen",
                overflowPrompt.Contains("counting blue lamps", StringComparison.OrdinalIgnoreCase)
                && Regex.Matches(overflowPrompt, @"(?m)^- \[").Count == 1,
                "Per-turn relevance can retrieve an old matching characteristic after more than sixteen active entries without filling the prompt with unrelated entries (at most six relevant details).",
                overflowPrompt);
            StoreDynamicCharacteristicWrites(campaign, hero, accepted, payload,
                new Dictionary<string, object> { ["sessionId"] = "session_b", ["exchangeId"] = "exchange_b", ["turnIds"] = new List<string> { "turn_b" } },
                "event_b", "party_chat", "town_ES1", 110, new[] { hero, "main_hero", "npc_witness" });
            StoreDynamicCharacteristicWrites(campaign, hero, accepted, payload,
                new Dictionary<string, object> { ["sessionId"] = "session_c", ["exchangeId"] = "exchange_c", ["turnIds"] = new List<string> { "turn_c" } },
                "event_c", "dialogue", "town_ES1", 115, new[] { hero, "main_hero" });
            using (ReignDbConnection connection = OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> duplicate = QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner AND status='active';",
                    new Dictionary<string, object> { ["owner"] = hero }).Single();
                add("duplicate_is_exactly_once", ReadInt(duplicate, "repeat_count", 0) == 3
                    && TextListFromJson(ReadString(duplicate, "known_by_json", "[]")).Contains("npc_witness", StringComparer.OrdinalIgnoreCase),
                    "Repeated details update one record, expand its audience, and never forget an earlier group witness.", duplicate);
            }
            Dictionary<string, object> badParsed = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["text"] = "I am currently in Vostrum with exactly 200 troops.", ["category"] = "personal_history", ["topicKey"] = "current_location" }
                }
            };
            List<Dictionary<string, object>> rejected = NormalizeDynamicCharacteristicWrites(badParsed, hero, profile, payload,
                "I am currently in Vostrum with exactly 200 troops.");
            Dictionary<string, object> rejectedStore = StoreDynamicCharacteristicWrites(campaign, hero, rejected, payload,
                new Dictionary<string, object> { ["sessionId"] = "session_d", ["exchangeId"] = "exchange_d" },
                "event_d", "dialogue", "town_ES1", 120, new[] { hero, "main_hero" });
            add("world_state_claim_is_rejected", ReadInt(rejectedStore, "rejectedCount", 0) == 1,
                "Current location and troop assertions cannot be promoted into Dynamic Characteristics.", rejectedStore);
            Dictionary<string, object> parentAnecdote = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["text"] = "Menor's father taught him to tap cask staves and listen for hidden cracks when he was eight.",
                        ["category"] = "formative_experience", ["topicKey"] = "cask_listening"
                    }
                }
            };
            List<Dictionary<string, object>> parentAccepted = NormalizeDynamicCharacteristicWrites(parentAnecdote, "npc_menor",
                new Dictionary<string, object> { ["name"] = "Menor" }, payload,
                "My father taught me to tap every cask in the cellar and listen for a hidden crack. I was eight when I began.");
            add("unnamed_parent_anecdote_is_soft_canon", parentAccepted.Count == 1 && ReadString(parentAccepted[0], "status", "") == "active",
                "An unnamed parent can be part of generated personal history without asserting a native family identity.", parentAccepted);
            Dictionary<string, object> namedFamily = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["text"] = "My father is Caladog.", ["category"] = "personal_history", ["topicKey"] = "father_identity"
                    }
                }
            };
            List<Dictionary<string, object>> namedFamilyRejected = NormalizeDynamicCharacteristicWrites(namedFamily, hero, profile, payload, "My father is Caladog.");
            add("named_family_requires_native_evidence", namedFamilyRejected.Count == 1 && ReadString(namedFamilyRejected[0], "status", "") == "rejected",
                "An explicit named family identity is not promoted into soft canon.", namedFamilyRejected);
            Dictionary<string, object> paraphrasedHabit = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["text"] = "Purios privately recites the names of ordinary people in his district — children, widows, laborers — because he believes knowing someone's name is the foundation of belonging and authority.",
                        ["category"] = "habit", ["topicKey"] = "name_memorization_habit"
                    }
                }
            };
            List<Dictionary<string, object>> paraphraseAccepted = NormalizeDynamicCharacteristicWrites(paraphrasedHabit, "npc_purios",
                new Dictionary<string, object> { ["name"] = "Purios the Viper" }, payload,
                "When no one's watching, I recite names. The ordinary ones—the widow, the fishmonger's daughter, the temple boy. I keep their names in my head because the day I cannot greet someone by name is the day I have stopped belonging here.");
            add("faithful_paraphrase_is_grounded", paraphraseAccepted.Count == 1 && ReadString(paraphraseAccepted[0], "status", "") == "active",
                "A concise third-person summary can be grounded by several shared content terms without copying the reply verbatim.", paraphraseAccepted);
            Dictionary<string, object> shortNameKeepsake = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["text"] = "Col keeps a smooth dark grey river stone found behind his family's stead as a reminder of permanence.",
                        ["category"] = "preference", ["topicKey"] = "keepsake_river_stone"
                    }
                }
            };
            List<Dictionary<string, object>> shortNameAccepted = NormalizeDynamicCharacteristicWrites(shortNameKeepsake, "npc_col",
                new Dictionary<string, object> { ["name"] = "Col of Diantogmail" }, payload,
                "I keep a smooth dark grey river stone I found behind my family's stead. It reminds me that some things endure.");
            add("short_given_name_is_valid_speaker_evidence",
                shortNameAccepted.Count == 1 && ReadString(shortNameAccepted[0], "status", "") == "active",
                "A generated third-person history using the speaker's valid three-letter given name remains attributable to that speaker.",
                shortNameAccepted);
            List<Dictionary<string, object>> shortNameThirdPartyRejected = NormalizeDynamicCharacteristicWrites(shortNameKeepsake, "npc_ceroc",
                new Dictionary<string, object> { ["name"] = "Ceroc of Fen Uvain" }, payload,
                "Col keeps a smooth dark grey river stone found behind his family's stead as a reminder of permanence.");
            add("short_name_does_not_override_different_speaker",
                shortNameThirdPartyRejected.Count == 1 && ReadString(shortNameThirdPartyRejected[0], "rejectionReason", "") == "not_about_speaking_character",
                "Recognizing short names does not attach a named third party's personal history to a different speaker.",
                shortNameThirdPartyRejected);
            Dictionary<string, object> lateReplyHabit = new Dictionary<string, object>
            {
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["text"] = "Purios knocks twice on any doorframe before entering a room where people wait — once for the living, once for whoever built the wall — a habit taught by an old woman in the fishmonger's district when he was young.",
                        ["category"] = "habit", ["topicKey"] = "doorframe_knocking_superstition"
                    }
                }
            };
            string longReply = "He adjusts the brim of his merchant hat and speaks at length about Rhovarion's cup, reciprocity, suspicion, names, bargains, markets, merchants, roads, tools, guards, patience, trust, habit, and keeping promises. "
                + "Mine. Before I walk through any doorframe where people are waiting for me, I knock twice. Once for the living, once for whoever built the wall. An old woman in the fishmonger's district taught me that when I was still running errands.";
            List<Dictionary<string, object>> lateReplyAccepted = NormalizeDynamicCharacteristicWrites(lateReplyHabit, "npc_purios",
                new Dictionary<string, object> { ["name"] = "Purios the Viper" }, payload, longReply);
            add("late_reply_detail_is_grounded", lateReplyAccepted.Count == 1 && ReadString(lateReplyAccepted[0], "status", "") == "active",
                "A characteristic expressed after the retrieval tokenizer's first 32 terms remains eligible for Dynamic Characteristics.", lateReplyAccepted);
            string explicitAversionReply = "Fine. I cannot stand courtiers who talk around a thing instead of through it. State your purpose plainly.";
            List<Dictionary<string, object>> inferredAversion = NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object> { ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>() },
                "npc_aevenes",
                new Dictionary<string, object> { ["name"] = "Aevenes Damaranion" },
                payload,
                explicitAversionReply);
            add("explicit_aversion_fallback_is_grounded",
                inferredAversion.Count == 1
                && ReadString(inferredAversion[0], "status", "") == "active"
                && ReadString(inferredAversion[0], "category", "") == "aversion"
                && ReadString(inferredAversion[0], "text", "").Contains("courtiers", StringComparison.OrdinalIgnoreCase),
                "An explicit stable first-person preference or aversion is preserved when the model omits only its structured companion write.",
                inferredAversion);
            string explicitHabitReply = "When I was a boy I counted the boundary stones each morning. I still count things whenever I enter a new camp.";
            List<Dictionary<string, object>> inferredHabit = NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object> { ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>() },
                "npc_acarion",
                new Dictionary<string, object> { ["name"] = "Acarion of Atrion" },
                payload,
                explicitHabitReply);
            add("explicit_habit_fallback_is_grounded",
                inferredHabit.Count == 1
                && ReadString(inferredHabit[0], "status", "") == "active"
                && ReadString(inferredHabit[0], "category", "") == "habit"
                && ReadString(inferredHabit[0], "text", "").Contains("count things", StringComparison.OrdinalIgnoreCase),
                "An explicit stable first-person habit is preserved when the model omits only its structured companion write.",
                inferredHabit);
            string malformedKeptObjectReply =
                "You keep a blue glass token beneath a cedar box. I will remember that. "
                + "I keep a die. A single bone die, chipped on one corner, worn smooth on the others. "
                + "It was my father's, and it sits in a locked drawer in my chamber.";
            List<Dictionary<string, object>> inferredKeptObject = NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object>
                {
                    ["dynamicCharacteristicWrites"] = new List<object> { 0, 0, 0 }
                },
                "npc_aevonion",
                new Dictionary<string, object> { ["name"] = "Aevonion Aurelerides" },
                payload,
                malformedKeptObjectReply);
            add("malformed_collection_kept_object_fallback_is_grounded",
                inferredKeptObject.Count == 1
                && ReadString(inferredKeptObject[0], "status", "") == "active"
                && ReadString(inferredKeptObject[0], "text", "").Contains("keep a die", StringComparison.OrdinalIgnoreCase),
                "A malformed companion-write array cannot suppress an explicit stable keepsake already grounded in the visible reply.",
                inferredKeptObject);
            return rows;
        }
    }
}
