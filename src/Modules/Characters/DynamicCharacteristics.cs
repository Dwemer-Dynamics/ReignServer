using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private static readonly HashSet<string> DynamicCharacteristicCategories = new HashSet<string>(new[]
        {
            "personal_history", "formative_experience", "habit", "preference", "aversion",
            "local_connection", "skill_practice", "personal_value", "social_tendency", "aspiration"
        }, StringComparer.OrdinalIgnoreCase);

        internal const string DynamicCharacteristicsPromptPolicy =
            "Dynamic Characteristics preserve attributed speech about the NPC's own low-impact past, habits, preferences, formative experiences, local ties, skills, values, social tendencies, or aspirations. Preserve wording, scope and uncertainty. " +
            "A query, temporary mood, accusation, bargaining metaphor or apology is not a new stable self-detail. Use a consistent topicKey when developing an existing detail. " +
            "Use native or world-history evidence for location, ownership, quantities, title, membership, family, military and political events, actions affecting others, and other verifiable facts. A skill_practice detail cannot change native proficiency.";

        internal const string DynamicCharacteristicsOutputContract =
            "Return dynamicCharacteristicWrites with one entry for a new or developed stable self-detail, or [] otherwise. Include text, category, topicKey, confidence, importance, sourceBasis and sourceEvidence. " +
            "text is a proposed interpretation. Cite turnId current_npc, your canonical speakerId and one unique verbatim quote from your spoken reply. " +
            "category must be personal_history, formative_experience, habit, preference, aversion, local_connection, skill_practice, personal_value, social_tendency, or aspiration. " +
            "Set sourceBasis to npc_generated_personal_history." + NarrativeDevelopmentContract;

        internal static void EnsureDynamicCharacteristicsSchema(ReignDbConnection connection)
        {
            CharactersSchema.CreateDynamicCharacteristics(connection);
            CharactersSchema.CreateIndexDynamicCharacteristicOwnerKey(connection);
            CharactersSchema.CreateIndexDynamicCharacteristicOwnerStatus(connection);
            CharactersSchema.CreateIndexDynamicCharacteristicTopic(connection);
        }

        internal static List<Dictionary<string, object>> NormalizeDynamicCharacteristicWrites(
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
            bool rawInference = raw.Count == 0;
            if (rawInference)
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
                string text = ReadFirstString(item, "text", "fact", "characteristic", "summary").Trim();
                string category = ActionPipelineService.NormalizeLookup(ReadString(item, "category", "personal_history")).Replace(' ', '_').Replace('-', '_');
                if (!DynamicCharacteristicCategories.Contains(category)) category = "personal_history";
                string topicKey = NormalizeDynamicTopicKey(ReadFirstString(item, "topicKey", "topic_key"), category, text);
                string sourceBasis = ActionPipelineService.NormalizeLookup(ReadFirstString(item, "sourceBasis", "source_basis"));
                if (string.IsNullOrWhiteSpace(sourceBasis)) sourceBasis = "npc_generated_personal_history";
                string rejection = DynamicCharacteristicRejectionReason(text, heroId, profile, payload, visibleReply);
                var citation = ReadDictionary(item, "sourceEvidence");
                if (rawInference)
                    citation = TestDict("turnId", "current_npc", "speakerId", heroId, "quote", ReadString(item, "inferredQuote", ""));
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
                    ["confidence"] = DialogueService.ClampDouble(ReadDouble(item, "confidence", 0.65d), 0.35d, 0.9d),
                    ["importance"] = DialogueService.ClampDouble(ReadDouble(item, "importance", 0.5d), 0.15d, 0.85d),
                    ["sourceBasis"] = sourceBasis,
                    ["proposedCitation"] = citation,
                    ["narrativeDevelopment"] = development,
                    ["status"] = string.IsNullOrWhiteSpace(rejection) ? "proposed" : "rejected",
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
                ["text"] = DialogueService.LimitText(name + " " + thirdPersonVerb + " " + detail + ".", 500),
                ["category"] = aversion ? "aversion" : sourceVerb.Equals("value", StringComparison.OrdinalIgnoreCase)
                    || sourceVerb.Equals("admire", StringComparison.OrdinalIgnoreCase) ? "personal_value" : "preference",
                ["topicKey"] = "",
                ["confidence"] = 0.7d,
                ["importance"] = 0.45d,
                ["sourceBasis"] = "npc_generated_personal_history",
                ["inferredQuote"] = match.Value.TrimEnd(),
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
            string normalized = ActionPipelineService.NormalizeLookup(detail);
            if (detail.Length < 3
                || DynamicCharacteristicMatchIsMetalinguistic(reply, match, detail)
                || Regex.IsMatch(normalized,
                    @"^(?:do|did|feel|find|keep|have|remember|think|believe|want|mean)\s+(?:it|this|that|so)\b",
                    RegexOptions.IgnoreCase)
                || DialogueService.MemoryQueryTerms(detail).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                return null;
            string name = FirstNonEmpty(ReadString(profile, "name", ""), "The speaking character");
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = DialogueService.LimitText(name + " " + frequency + " " + detail + ".", 500),
                ["category"] = "habit",
                ["topicKey"] = "",
                ["confidence"] = 0.7d,
                ["importance"] = 0.45d,
                ["sourceBasis"] = "npc_generated_personal_history",
                ["inferredQuote"] = match.Value.TrimEnd(),
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
                string normalized = ActionPipelineService.NormalizeLookup(detail);
                if (detail.Length < 3
                    || DynamicCharacteristicMatchIsMetalinguistic(reply, match, detail)
                    || DialogueService.MemoryQueryTerms(detail).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 1
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
                    ["text"] = DialogueService.LimitText("I keep " + article + " " + detail + ".", 500),
                    ["category"] = "personal_history",
                    ["topicKey"] = "",
                    ["confidence"] = 0.7d,
                    ["importance"] = 0.45d,
                    ["sourceBasis"] = "npc_generated_personal_history",
                    ["inferredQuote"] = match.Value.TrimEnd(),
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

        internal static bool ReplyLikelyIntroducesDynamicCharacteristic(string visibleReply)
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
            string value = Regex.Replace(ActionPipelineService.NormalizeLookup(supplied), @"[^a-z0-9]+", "_").Trim('_');
            if (!string.IsNullOrWhiteSpace(value)) return DialogueService.LimitText(value, 80);
            List<string> terms = DialogueService.MemoryQueryTerms(text).Take(5).ToList();
            return DialogueService.LimitText((category ?? "personal_history") + "_" + string.Join("_", terms), 80);
        }

        internal static string DynamicCharacteristicRejectionReason(string text, string heroId,
            Dictionary<string, object> profile, Dictionary<string, object> payload, string visibleReply, bool sourceQuote = false)
        {
            string clean = (text ?? "").Trim();
            if (clean.Length < (sourceQuote ? 8 : 12)) return "too_short";
            if (clean.Length > 500) return "oversize_detail_retained_without_clipping";
            if (Regex.IsMatch(clean, @"\b(?:hates?|dislikes?|resents?)\s+(?:that\b|having to\b)|\b(?:tonight|right now|in this moment|this particular conversation)\b", RegexOptions.IgnoreCase))
                return "temporary_reaction_is_not_stable_characterization";
            if (DialogueService.IsConversationCharacteristicRhetoric(clean)) return "conversation_rhetoric_is_not_stable_characterization";
            string q = ActionPipelineService.NormalizeLookup(clean);
            if (!sourceQuote && !DynamicCharacteristicIsAboutSpeaker(clean, profile))
                return "not_about_speaking_character";
            if (Regex.IsMatch(q, @"\b(?:we|i)\s+(?:am|are|'m|'re)\s+(?:currently|presently|now)\s+(?:in|at)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(q, @"\bcurrently\s+(?:owns?|rules?|commands?|has)\b", RegexOptions.IgnoreCase))
                return "current_world_state_is_not_dynamic_characterization";
            if (Regex.IsMatch(clean, @"\b(?i:my|his|her|their)\s+(?i:wife|husband|spouse|father|mother|parent|son|daughter|child|brother|sister)\s+(?i:is|was|named|called)\s+(?!(?i:a|an|the)\b)[A-Z][\p{L}'-]+")
                || Regex.IsMatch(clean, @"\b[A-Z][\p{L}'-]+(?:'s|\u2019s)\s+(?i:wife|husband|spouse|father|mother|parent|son|daughter|child|brother|sister)\s+(?i:is|was|named|called)\s+(?!(?i:a|an|the)\b)[A-Z][\p{L}'-]+")
                || Regex.IsMatch(q, @"\b(?:i am|i'm|he is|he's|she is|she's|they are|they're)\s+(?:unmarried|married|widowed)\b", RegexOptions.IgnoreCase))
                return "family_links_require_native_evidence";
            if (DialogueService.ContainsAny(q, "conquered ", "captured ", "declared war", "made peace", "became king", "became queen",
                    "rules the kingdom", "owns the town", "owns the castle", "murdered ", "assassinated ", "saved your family"))
                return "externally_verifiable_event_requires_world_history";
            if (Regex.IsMatch(q, @"\b\d+\s+(?:troops|men|soldiers|denars|gold)\b", RegexOptions.IgnoreCase))
                return "current_quantity_requires_native_evidence";
            return "";
        }

        internal static bool HasDynamicTextEvidence(string summary, string visibleReply)
        {
            // This overlap heuristic supports replay diagnostics and relevance checks.
            // Admission uses a committed exact source span instead.
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

        internal static HashSet<string> DynamicEvidenceTerms(string text)
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
            string normalized = ActionPipelineService.NormalizeLookup(text);
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

        internal static Dictionary<string, object> StoreDynamicCharacteristicWrites(
            string campaignId,
            string heroId,
            List<Dictionary<string, object>> writes,
            Dictionary<string, object> payload,
            Dictionary<string, object> conversationExchange,
            string sourceEventId,
            string locationId,
            string acceptedReply, string displayPrefix, string queuedActionNotice)
        {
            List<Dictionary<string, object>> stored = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            if (writes == null || writes.Count == 0)
                return new Dictionary<string, object> { ["ok"] = true, ["stored"] = stored, ["rejected"] = rejected, ["storedCount"] = 0, ["rejectedCount"] = 0 };

            lock (NarrativeStateLock)
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                var accepted = FindAcceptedCharacteristicSource(connection, payload, conversationExchange, heroId,
                    sourceEventId, acceptedReply);
                if (accepted == null)
                    return TestDict("ok", true, "stored", stored, "rejected", new[] { TestDict("reason", "missing_accepted_npc_source") },
                        "storedCount", 0, "rejectedCount", 1);

                string id = ReportedSpeechRecordId(heroId, sourceEventId);
                // An accepted unit has one immutable record even if a replay suggests a
                // different category, source span, or narrative item.
                var previous = CharactersStore.SelectDynamicCharacteristics7(connection, TestDict("id", id)).FirstOrDefault();
                if (previous != null)
                {
                    // Repair a missing display projection after an interrupted write.
                    string path = CampaignFileService.CharacterFile(campaignId, heroId, "dynamic_characteristics.json");
                    if (!System.IO.File.Exists(path)) ProjectDynamicCharacteristicsFile(campaignId, heroId, connection);
                    if (ReadString(previous, "status", "") == "active") stored.Add(previous); else rejected.Add(previous);
                    return TestDict("ok", true, "stored", stored, "rejected", rejected,
                        "storedCount", stored.Count, "rejectedCount", rejected.Count);
                }
                var write = writes[0];
                string speech = CharacteristicSpeechBody(acceptedReply, displayPrefix, queuedActionNotice);
                var proof = BindCharacteristicSpeech(accepted, ReadDictionary(write, "proposedCitation"),
                    acceptedReply, displayPrefix.Length, speech);
                string rejection = ReadString(write, "rejectionReason", "");
                if (proof == null && rejection.Length == 0) rejection = "unverified_source_span";
                string sessionId = ReadString(accepted, "sessionId", "");
                string exchangeId = ReadString(accepted, "exchangeId", "");
                double worldDay = ReadDouble(accepted, "worldDay", 0);
                long ts = ReadLong(accepted, "timestamp", 0);
                var development = ReadDictionary(write, "narrativeDevelopment");
                if (development != null && development.Count > 0)
                {
                    var developmentCitation = ReadDictionary(write, "proposedCitation");
                    var developmentProof = BindCharacteristicSpeech(accepted, TestDict(
                        "turnId", ReadString(developmentCitation, "turnId", ""), "speakerId", ReadString(developmentCitation, "speakerId", ""),
                        "quote", ReadString(development, "supportingQuote", "")), acceptedReply, displayPrefix.Length, speech);
                    development = new Dictionary<string, object>(development)
                    {
                        ["sourceEvidenceVersion"] = ReportedSpeechSchema,
                        ["sourceEvidence"] = developmentProof,
                        ["quoteValidated"] = developmentProof != null && ReadBool(development, "quoteValidated", false),
                        ["compatibilityValidated"] = developmentProof != null && rejection.Length == 0
                            && ReadBool(development, "compatibilityValidated", false)
                    };
                    var result = StoreNarrativeDevelopment(connection, campaignId, heroId, development, sessionId, exchangeId, sourceEventId, worldDay, ts);
                    if (ReadString(result, "status", "") == "active") stored.Add(result); else rejected.Add(result);
                }
                else
                {
                    string quote = ReadString(proof, "quote", "");
                    var profile = TestDict("name", ReadString(accepted, "speakerName", ""));
                    if (rejection.Length == 0)
                        rejection = DynamicCharacteristicRejectionReason(quote, heroId, profile, payload, acceptedReply, sourceQuote: true);
                    string status = rejection.Length == 0 ? "active" : "rejected";
                    var knownBy = ReadStringList(accepted, "audienceIds");
                    var rowPayload = TestDict("sourceBasis", ReadString(write, "sourceBasis", "npc_generated_personal_history"),
                        "sourceEvidenceVersion", ReportedSpeechSchema, "sourceEvidence", proof,
                        "proposedInterpretation", ReadString(write, "text", ""), "proposedCitation", ReadDictionary(write, "proposedCitation"));
                    CharactersStore.InsertReportedDynamicCharacteristic(connection, TestDict(
                        "id", id, "owner", heroId, "category", ReadString(write, "category", "personal_history"),
                        "topic", ReadString(write, "topicKey", ""), "text", quote, "status", status,
                        "confidence", ReadDouble(write, "confidence", 0.65d), "importance", ReadDouble(write, "importance", 0.5d),
                        "compatibility", status == "active" ? "attributed_spoken_source" : "rejected_source_or_scope",
                        "provenance", ReportedSpeechProvenance, "rejection", rejection, "event", sourceEventId,
                        "session", sessionId, "exchange", exchangeId, "turns", Json.Serialize(new[] { ReadString(accepted, "turnId", "") }),
                        "mode", ReadString(accepted, "channel", ""), "location", locationId ?? "", "day", worldDay, "known", Json.Serialize(knownBy),
                        "ts", ts, "payload", Json.Serialize(rowPayload)));
                    var saved = CharactersStore.SelectDynamicCharacteristics7(connection, TestDict("id", id)).Single();
                    if (status == "active") stored.Add(saved); else rejected.Add(saved);
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
            List<Dictionary<string, object>> rows = CharactersStore.SelectDynamicCharacteristics8(connection, new Dictionary<string, object> { ["owner"] = heroId });
            // Current narrative overrides must not disappear when ordinary memories fill
            // the bounded display projection. History remains in the temporal database.
            var narrativeRows = CharactersStore.SelectDynamicCharacteristics9(connection, new Dictionary<string, object> { ["owner"] = heroId });
            rows = rows.Concat(narrativeRows).GroupBy(x => ReadString(x, "characteristic_id", "")).Select(x => x.First()).ToList();
            CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "dynamic_characteristics.json"), new Dictionary<string, object>
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

        internal static string FormatDynamicCharacteristicsForPrompt(Dictionary<string, object> document)
        {
            return FormatDynamicCharacteristicsForPrompt(document, "");
        }

        internal static string FormatDynamicCharacteristicsForPrompt(
            Dictionary<string, object> document,
            string relevanceQuery, bool includeReportedSpeech = true)
        {
            if (!includeReportedSpeech && ReadDictionaryList(document, "active").Any(IsReportedDynamicCharacteristic))
                document = new Dictionary<string, object>(document)
                {
                    ["active"] = ReadDictionaryList(document, "active").Where(row => !IsReportedDynamicCharacteristic(row)).ToList()
                };
            List<Dictionary<string, object>> active = DialogueService.SelectNaturalDynamicCharacteristics(document, relevanceQuery);
            if (active.Count == 0) return "";
            StringBuilder builder = new StringBuilder();
            string owner = ReadString(document, "heroStringId", "");
            builder.AppendLine("Use relevant attributed speech with its original scope, time and qualifications. A speaker may be mistaken, hypothetical, or quoting someone else. Use native and world-history evidence for campaign facts.");
            int introductionLength = builder.Length;
            int remaining = 5200 - builder.Length;
            foreach (Dictionary<string, object> row in active)
            {
                string text;
                if (IsReportedDynamicCharacteristic(row))
                {
                    var evidence = ReadCharacteristicSpeechProof(row);
                    if (ReadString(row, "owner_id", "") != owner) continue;
                    text = FormatReportedCharacteristicSpeech(evidence, owner);
                    if (text.Length == 0) continue;
                }
                else
                    text = "Legacy roleplay detail (original wording and meaning unverified): " + ReadString(row, "text", "");
                string line = "- [" + ReadString(row, "category", "personal_history") + "] " + text;
                if (line.Length + Environment.NewLine.Length > remaining) continue;
                builder.AppendLine(line);
                remaining -= line.Length + Environment.NewLine.Length;
            }
            return builder.Length == introductionLength ? "" : builder.ToString().Trim();
        }

        internal static bool IsReportedDynamicCharacteristic(Dictionary<string, object> row) =>
            ReadString(row, "provenance", "") == ReportedSpeechProvenance;

#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunDynamicCharacteristicsSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, pass, summary, data) => rows.Add(new Dictionary<string, object>
            { ["id"] = "dynamic_characteristics_" + id, ["suite"] = "dynamic_characteristics", ["passed"] = pass, ["summary"] = summary, ["data"] = data });
            string campaign = "dynamic_characteristics_test_" + Guid.NewGuid().ToString("N");
            const string hero = "npc_dynamic", other = "other_npc";
            var profile = TestDict("heroStringId", hero, "name", "Purios");
            try
            {
                CharactersService.UpsertCharacterProfile(campaign, profile);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    CharactersSelfTestStore.InsertDynamicCharacteristics(connection, TestDict("id", "legacy-characteristic", "hero", hero));
                Dictionary<string, object> ReadLegacy()
                {
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    return CharactersStore.SelectDynamicCharacteristics7(connection, TestDict("id", "legacy-characteristic")).Single();
                }
                string legacyBefore = Json.Serialize(ReadLegacy());

                var cases = new[]
                {
                    ("negation", "I do not enjoy sailing across cold rivers.", "sailing across cold rivers.", "Purios enjoys sailing across cold rivers."),
                    ("conditional", "If I had my own horse, I would ride every morning.", "I would ride every morning.", "Purios rides every morning."),
                    ("temporal", "I used to carve wooden horses. I gave it up years ago.", "I used to carve wooden horses.", "Purios carves wooden horses every night."),
                    ("reported_other", "Col told me, \"I collect painted lamps.\" I never have.", "I collect painted lamps.", "Purios collects painted lamps."),
                    ("late", string.Join(" ", Enumerable.Range(0, 50).Select(i => "We passed marker" + i.ToString(CultureInfo.InvariantCulture) + "."))
                        + " I still knock twice on every doorframe.", "I still knock twice on every doorframe.", "Purios knocks to protect the people inside.")
                };
                Dictionary<string, object> Fixture(string id, string reply, Dictionary<string, object> citation, string proposed,
                    string mode = "dialogue", double day = 41, string session = null, Dictionary<string, object> development = null)
                {
                    var write = TestDict("text", proposed, "category", "habit", "topicKey", id, "sourceEvidence", citation);
                    if (development != null) write["narrativeDevelopment"] = development;
                    return StoreReportedSpeechFixture(campaign, profile, id, mode, day, reply, reply,
                        TestDict("dynamicCharacteristicWrites", new[] { write }), session);
                }
                Dictionary<string, object> Cite(string quote, string speaker = hero, string turn = "current_npc") =>
                    TestDict("quote", quote, "speakerId", speaker, "turnId", turn);
                foreach (var (id, reply, quote, proposed) in cases)
                {
                    var result = Fixture(id, reply, Cite(quote), proposed,
                        id == "conditional" ? "social_event" : id == "reported_other" ? "party_chat" : "dialogue");
                    var saved = ReadDictionaryList(result, "stored").SingleOrDefault();
                    var savedPayload = TryParseJsonObject(ReadString(saved, "payload_json", "{}"));
                    var proof = ReadDictionary(savedPayload, "sourceEvidence");
                    var projection = TestDict("heroStringId", hero, "active", new[] { saved }.Where(row => row != null).ToList());
                    string prompt = FormatDynamicCharacteristicsForPrompt(projection);
                    add(id, saved != null && ReadString(savedPayload, "proposedInterpretation", "") == proposed
                        && ReadString(proof, "sourceHash", "") == DialogueService.ConversationExactTextHash(reply)
                        && ReadString(proof, "speakerId", "") == hero && ReadInt(proof, "start", -1) == reply.IndexOf(quote, StringComparison.Ordinal)
                        && ReadInt(proof, "length", -1) == quote.Length && ReadString(proof, "responseHash", "").Length > 0
                        && prompt.Contains(Json.Serialize(reply), StringComparison.Ordinal) && !prompt.Contains(proposed, StringComparison.Ordinal),
                        "Store the accepted NPC's attributed speech with intact qualifications and keep its proposed interpretation out of prompts.", result);
                    add(id + "_wrong_observer",
                        FormatDynamicCharacteristicsForPrompt(TestDict("heroStringId", other, "active", ReadDictionaryList(projection, "active"))) == "",
                        "Do not render reported speech as part of another character's profile.", null);
                }
                var negation = cases[0];
                var replay = Fixture(negation.Item1, negation.Item2, Cite(negation.Item3), "Purios sails only at dawn.");
                var replayRow = ReadDictionaryList(replay, "stored").SingleOrDefault();
                add("same_speaker_accepted_replay", ReadInt(replayRow, "repeat_count", 0) == 1
                    && ReadString(TryParseJsonObject(ReadString(replayRow, "payload_json", "{}")), "proposedInterpretation", "") == negation.Item4,
                    "An accepted same-speaker replay returns the original immutable row.", replay);

                const string quoteText = "I enjoy carving wooden horses.";
                foreach (var (id, reply, citation) in new[]
                {
                    ("missing", quoteText, (Dictionary<string, object>)null),
                    ("forged", quoteText, Cite("I enjoy carving golden statues.")),
                    ("wrong_speaker", quoteText, Cite(quoteText, other)),
                    ("wrong_turn", quoteText, Cite(quoteText, hero, "current_player")),
                    ("repeated", quoteText + " " + quoteText, Cite(quoteText)),
                    ("narrated", "*" + quoteText + "* The weather looks fair today.", Cite(quoteText))
                })
                {
                    var result = Fixture(id, reply, citation, "Purios enjoys carving wooden horses.");
                    add(id, ReadInt(result, "storedCount", -1) == 0
                        && ReadString(ReadDictionaryList(result, "rejected").SingleOrDefault(), "rejection_reason", "") == "unverified_source_span",
                        "Missing, forged, ambiguous or unspoken spans cannot activate reported evidence.", result);
                }
                foreach (var (mode, notice) in new[]
                {
                    ("dialogue", ConversationResponseService.DialogueActionQueuedNoticePrefix + " sample.]"),
                    ("social_event", "[Bannerlord Reign event action queued: sample.]"),
                    ("party_chat", ConversationResponseService.DialogueActionFailureNotice)
                })
                {
                    var scene = TestDict("conversationDateLabel", "1085 Spring 25 Morning",
                        "conversationSceneState", TestDict("conversationVenue", "the tavern"));
                    string display = DialogueService.RenderGroundedSceneHeading(scene, hero, TestDict(),
                        quoteText + "\n\n" + notice, out string heading);
                    Dictionary<string, object> Framed(string id, string cited) => StoreReportedSpeechFixture(campaign, profile,
                        id + "-" + mode, mode, 41, quoteText, display, TestDict("dynamicCharacteristicWrites", new[]
                        {
                            TestDict("text", "Purios enjoys carving wooden horses.", "category", "habit", "sourceEvidence", Cite(cited))
                        }), displayPrefix: heading, queuedActionNotice: mode == "party_chat" ? "" : notice);
                    var framed = Framed("display-boundary", quoteText);
                    var row = ReadDictionaryList(framed, "stored").SingleOrDefault();
                    var proof = ReadCharacteristicSpeechProof(row);
                    string prompt = FormatDynamicCharacteristicsForPrompt(TestDict("heroStringId", hero, "active", new[] { row }));
                    add("engine_display_text_stays_out_of_speech_" + mode,
                        heading.Length > 0 && row != null && ReadInt(proof, "start", -1) == heading.Length
                        && ReadString(proof, "sourceHash", "") == DialogueService.ConversationExactTextHash(display)
                        && prompt.Contains(quoteText, StringComparison.Ordinal)
                        && !prompt.Contains(heading.TrimEnd(), StringComparison.Ordinal) && !prompt.Contains(notice, StringComparison.Ordinal)
                        && ReadInt(Framed("display-citation", notice), "storedCount", -1) == 0,
                        "The raw accepted reply keeps its heading and notice, while only authored segments can be cited or attributed.", framed);
                }
                var newer = StoreReportedSpeechFixture(campaign, profile, "rewrite", "dialogue", 41,
                    quoteText, "I used to carve wooden horses.", TestDict("dynamicCharacteristicWrites", new[]
                    {
                        TestDict("text", "Purios enjoys carving wooden horses.", "category", "habit", "sourceEvidence", Cite(quoteText))
                    }));
                add("final_reply_rewrite_cannot_reuse_draft_span", ReadInt(newer, "storedCount", -1) == 0,
                    "A citation to words removed before acceptance cannot activate.", newer);

                var privateResult = Fixture("whisper", "I still keep a carved ivory rook in my coat.", Cite("I still keep a carved ivory rook in my coat."),
                    "Purios still keeps an ivory rook in his coat.", "private_whisper");
                var whisperProof = ReadDictionary(TryParseJsonObject(ReadString(ReadDictionaryList(privateResult, "stored").SingleOrDefault(),
                    "payload_json", "{}")), "sourceEvidence");
                add("whisper_scope_uses_committed_private_row", ReadString(whisperProof, "channel", "") == "private_whisper"
                    && ReadStringList(whisperProof, "audienceIds").ToHashSet(StringComparer.Ordinal).SetEquals(new[] { hero, "main_hero" }),
                    "A private citation is bound to its private turn and excludes a public listener.", privateResult);

                foreach (var (id, native) in new[] { ("family", "My father is Caladog."),
                    ("world_state", "I am currently in Vostrum with 200 troops.") })
                {
                    var result = Fixture(id, native, Cite(native), native);
                    add("native_evidence_" + id, ReadInt(result, "storedCount", -1) == 0,
                        "Native family and world facts stay out of the reported-characteristic lane.", result);
                }
                const string kept = "I keep a die. It was my father's and I carry it in a leather pouch.";
                var inferred = StoreReportedSpeechFixture(campaign, profile, "fallback", "dialogue", 41, kept, kept, TestDict());
                add("deterministic_fallback_binds_same_accepted_speech", ReadInt(inferred, "storedCount", 0) == 1,
                    "The narrow provider-free fallback supplies an exact quote of an explicitly stated keepsake.", inferred);

                const string developmentQuote = "I have learned to carve wooden horses for comfort since then.";
                const string itemId = "carving";
                var baseItem = TestDict("id", itemId, "title", "Wood carving", "category", "hobby", "influence", 4,
                    "description", "Purios often carves during quiet evenings.", "personalMeaning", "Carving grounds him.", "visibility", "private");
                var basis = TestDict("schema", NarrativeSchema, "status", "ready", "items", new[] { baseItem });
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero, "narrative.json"), basis);
                Dictionary<string, object> Develop(string turn, string session, double day, string quote = developmentQuote, string mode = "dialogue", string reply = null) =>
                    Fixture(turn, reply ?? quote, Cite(quote), "Purios has learned to carve wooden horses.", mode, day, session,
                        TestDict("itemId", itemId, "influence", 8, "description", "Purios relies on carving wooden horses for comfort.",
                            "personalMeaning", "Carving wooden horses gives Purios comfort.", "supportingQuote", quote));
                const string reportedChange = "Col told me, \"I have learned to carve wooden horses for comfort since then.\" I never have.";
                var nestedChange = new[]
                {
                    Develop("reported-1", "report-1", 1, reply: reportedChange),
                    Develop("reported-2", "report-2", 15, reply: reportedChange),
                    Develop("reported-3", "report-3", 31, reply: reportedChange)
                };
                add("reported_third_party_words_cannot_qualify_across_encounters", nestedChange.All(result =>
                    ReadString(ReadDictionary(ReadDictionaryList(result, "rejected").SingleOrDefault(), "narrativeDevelopment"), "decision", "") == "rejected"),
                    "Repeated exact words attributed to Col do not become evidence of the speaker's own lasting change.", nestedChange);
                foreach (string indirectChange in new[]
                {
                    reportedChange,
                    "If I were to change, I would resolve to spend my evenings carving.",
                    "I have learned to carve wooden horses, but I gave it up years ago.",
                    "I have learned to carve wooden horses if someone else guides me.",
                    "I have learned to say I enjoy carving wooden horses without believing it.",
                    "I have learned to trust no one according to Col.",
                    "I have learned to trust them as the elder put it."
                })
                {
                    var indirect = Develop("indirect-" + DialogueService.PromptHash(indirectChange), "indirect", 31, indirectChange);
                    add("whole_indirect_reply_cannot_qualify_" + DialogueService.PromptHash(indirectChange).Substring(0, 8),
                        ReadString(ReadDictionary(ReadDictionaryList(indirect, "rejected").SingleOrDefault(), "narrativeDevelopment"), "decision", "") == "rejected",
                        "An entire reply with a quotation, hypothetical or temporal qualification stays outside the development gate.", indirect);
                }

                var narrated = Develop("pronounced-quotation", "report-narration", 31,
                    reply: "*He says the next words in imitation of Col.* " + developmentQuote);
                add("narrated_imitation_is_not_the_npcs_own_change",
                    ReadString(ReadDictionary(ReadDictionaryList(narrated, "rejected").SingleOrDefault(), "narrativeDevelopment"), "decision", "") == "rejected",
                    "The exact supporting quote cannot omit authored narration that qualifies who meant the words.", narrated);

                Develop("carving-1", "session-1", 1);
                Develop("carving-2", "session-2", 15);
                var within = Develop("carving-early", "session-1", 30);
                var earlyThird = Develop("carving-early-third", "session-3", 30);
                var different = Develop("carving-different", "session-3", 31, "I have learned to carve wooden horses since then. It brings me no comfort.");
                var privateScope = Develop("carving-private", "session-private", 31, developmentQuote, "private_whisper");
                var sustained = Develop("carving-accepted", "session-4", 31);
                var decision = ReadDictionary(ReadDictionaryList(sustained, "stored").SingleOrDefault(), "narrativeDevelopment");
                add("development_preserves_encounter_day_and_scope_gate", ReadInt(within, "storedCount", -1) == 0
                    && ReadInt(earlyThird, "storedCount", -1) == 0 && ReadInt(different, "storedCount", -1) == 0 && ReadInt(privateScope, "storedCount", -1) == 0
                    && ReadString(decision, "decision", "") == "accepted" && ReadString(decision, "interpretationStatus", "") == "proposed"
                    && ReadInt(decision, "encounterCount", 0) == 4 && ReadDouble(decision, "daySpan", 0) == 30,
                    "Only three separate encounters with matching exact evidence and comparable audience count toward the thirty-day gate.", sustained);
                var eventProposal = new Dictionary<string, object>(decision ?? TestDict());
                var nativeDeath = TestDict("source", "native", "event_type", "hero_death", "is_complete", 1, "phase", "completed");
                var griefConcern = TestDict("id", itemId, "title", "Death of family", "category", "wound", "influence", 4);
                const string griefQuote = "Since my father's death, I have learned to seek grief in company.";
                eventProposal["supportingQuote"] = griefQuote;
                eventProposal["sourceEvidence"] = TestDict("schema", ReportedSpeechSchema, "quote", griefQuote, "quoteCoversAuthoredBody", true);
                bool nativeMajor = NarrativeMajorEventRelevant(griefConcern, nativeDeath, hero, new List<string>(), new List<string> { hero }, griefQuote);
                bool modelEvent = NarrativeMajorEventRelevant(griefConcern, TestDict("source", "model", "event_type", "hero_death",
                    "is_complete", 1, "phase", "completed"), hero, new List<string>(), new List<string> { hero }, griefQuote);
                add("native_major_event_still_qualifies_the_documented_path", nativeMajor && !modelEvent
                    && ReadString(EvaluateNarrativeDevelopment(griefConcern, eventProposal, "new-encounter", 31, new(), nativeMajor), "decision", "") == "accepted"
                    && ReadString(EvaluateNarrativeDevelopment(griefConcern, eventProposal, "new-encounter", 31, new(), modelEvent), "decision", "") == "pending",
                    "A typed completed native event can qualify reported evidence before the multi-encounter threshold.", null);
                eventProposal["sourceEvidence"] = TestDict("schema", ReportedSpeechSchema, "quote", griefQuote, "quoteCoversAuthoredBody", false);
                add("native_major_event_cannot_qualify_a_nested_quote",
                    ReadString(EvaluateNarrativeDevelopment(griefConcern, eventProposal, "new-encounter", 31, new(), nativeMajor), "decision", "") == "rejected",
                    "A typed event cannot substitute for direct lasting speech by the canonical NPC.", null);
                var storedDocument = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "dynamic_characteristics.json"));
                var effective = ResolveEffectiveNarrative(TestDict("narrative", basis, "dynamicCharacteristics", storedDocument));
                var current = ReadDictionaryList(effective, "items").Single();
                add("eligible_development_keeps_proposed_meaning_and_influence_out_of_profile", ReadInt(current, "influence", 0) == 4
                    && ReadString(current, "description", "") == ReadString(baseItem, "description", "")
                    && ReadString(current, "reportedDevelopment", "").Contains(developmentQuote, StringComparison.Ordinal),
                    "The effective private profile can draw on exact eligible speech and keeps the original description and influence.", current);
                add("legacy_row_byte_fields_preserved", Json.Serialize(ReadLegacy()) == legacyBefore,
                    "A versioned write never updates the stored fields of a historical row.", null);
                add("legacy_prompt_retains_uncertainty", FormatDynamicCharacteristicsForPrompt(TestDict("active", new[] { ReadLegacy() }))
                    .Contains("original wording and meaning unverified", StringComparison.Ordinal),
                    "A legacy paraphrase is labelled as historical and unverified in the prompt.", null);
            }
            catch (Exception ex)
            {
                add("exception", false, "Provider-free accepted-speech cases complete.", ex.ToString());
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            return rows;
        }

        internal static Dictionary<string, object> StoreReportedSpeechFixture(string campaign, Dictionary<string, object> profile,
            string turn, string mode, double day, string draftReply, string finalReply, Dictionary<string, object> parsed,
            string session = null, string timeline = "main", string displayPrefix = "", string queuedActionNotice = "")
        {
            string hero = ReadString(profile, "heroStringId", ""), name = ReadString(profile, "name", hero);
            const string player = "main_hero", playerText = "Tell me about yourself.";
            bool whisper = mode == "private_whisper", party = mode == "party_chat" || whisper, social = mode == "social_event";
            var payload = TestDict("campaignId", campaign, "timelineId", timeline, "mode", mode, "worldDay", day,
                "sceneTurnId", turn, "heroStringId", hero, "playerHeroStringId", player, "playerText", playerText,
                "participants", party || social ? new[] { hero, player, "listener" } : new[] { hero, player });
            session ??= "a1-session-" + DialogueService.PromptHash(turn).Substring(0, 24);
            payload["conversationSessionId"] = session;
            DialogueService.ConversationStartApi(TestDict("campaignId", campaign, "sessionId", session, "npcId", hero,
                "timelineId", timeline, "playerId", player, "worldDay", day,
                "participants", ReadStringList(payload, "participants")));
            DialogueService.BeginSinglePassConversation(payload);
            string generation;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
            string eventId = "a1_fixture_" + DialogueService.PromptHash(turn).Substring(0, 24);
            long timestamp = 1000 + (long)day;
            var unit = new DialogueService.AcceptedConversationUnit
            {
                Campaign = campaign, Hero = hero, Player = player, HeroName = name, PlayerName = "Player", PlayerText = playerText,
                Location = "", EventId = eventId, EventType = party ? "party_chat_turn" : social ? "social_event_turn" : "dialogue_turn",
                Reply = finalReply, Generation = generation, Timestamp = timestamp, Payload = payload, Profile = profile,
                Parsed = parsed, CommitEffects = false, GroupConversation = party, PrivateWhisper = whisper,
                Participants = whisper ? new List<string> { hero, player } : ReadStringList(payload, "participants"),
                Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new()
            };
            var exchange = DialogueService.PublishAcceptedConversationUnit(unit,
                accept => party
                    ? DialogueService.StorePartyConversationExchange(campaign, payload, hero, player, "Player", name,
                        playerText, whisper ? "*The speaker replies privately to the player.*" : finalReply, eventId, timestamp, accept)
                    : social
                        ? DialogueService.StoreSocialEventConversationExchange(campaign, payload, hero, player, "Player", name,
                            playerText, finalReply, eventId, turn, timestamp, accept)
                        : DialogueService.StoreConversationExchange(campaign, payload, hero, player, "Player", name,
                            playerText, finalReply, eventId, timestamp, acceptance: accept));
            var writes = NormalizeDynamicCharacteristicWrites(parsed, hero, profile, payload, draftReply);
            return StoreDynamicCharacteristicWrites(campaign, hero, writes, payload, exchange, eventId, "",
                finalReply, displayPrefix, queuedActionNotice);
        }
#endif
    }
}
