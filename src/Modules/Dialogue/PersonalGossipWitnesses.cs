using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PublicWitnessGossipVersion = "public-witness-gossip-v1";

        private static bool IsOriginalPublicWitnessGossip(Dictionary<string, object> topic) =>
            ReadString(topic, "sourceOrigin", "") == "public_witness" && ReadInt(topic, "hop", 1) == 1
                && !ReadBool(topic, "reply", false);

        // These are separate speech spans. Never join across narration: doing so
        // would manufacture a quotation that no one actually spoke together.
        private static List<string> PublicGossipSpeechSpans(string text)
        {
            text ??= "";
            // Ambiguous bold/nested markup is not an ordinary speech record.
            if (text.Contains("**", StringComparison.Ordinal)) return new List<string>();
            if (Regex.IsMatch(text, @"\b(?:keep\s+(?:this|it)\s+(?:between\s+us|secret|private)|just\s+between\s+us|between\s+you\s+and\s+me|in\s+confidence|confidential|not\s+a\s+word\s+to\s+anyone)\b|\bOOC\s*:|\(\(|\[\[|\[\s*(?:system|meta)\s*:", RegexOptions.IgnoreCase))
                return new List<string>();
            var blocks = InputNarrationBlocks(text);
            // A trailing speech tag can qualify the preceding words. Withhold a
            // mixed private/public turn rather than guess which side was heard.
            if (blocks.Any(block =>
            {
                string action = ReadString(block, "text", "");
                return Regex.IsMatch(action, @"\b(?:whisper(?:s|ed|ing)?|murmur(?:s|ed|ing)?|aside|privately|confid(?:e|es|ed|ing))\b|\bunder\s+(?:his|her|their|my|your)\s+breath\b|\b(?:his|her|their|my|your)\s+ear\b|\bout\s+of\s+earshot\b|\bears\s+only\b|\bonly\s+[^.!?*]+\s+can\s+hear\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(action, @"\b(?:speak(?:s|ing)?|spoke|say(?:s|ing)?|said|tell(?:s|ing)?|told|voice)\b", RegexOptions.IgnoreCase)
                        && Regex.IsMatch(action, @"\b(?:quietly|softly|low|lower(?:s|ing)?|hushed|pitched)\b", RegexOptions.IgnoreCase);
            })) return new List<string>();
            var spans = new List<string>();
            int cursor = 0;
            foreach (var block in blocks)
            {
                int start = ReadInt(block, "outerStart", 0);
                if (start < cursor || start > text.Length || ReadInt(block, "outerLength", -1) < 0
                    || start + ReadInt(block, "outerLength", 0) > text.Length) return new List<string>();
                string words = text.Substring(cursor, start - cursor).Trim();
                if (words.Length > 0) spans.Add(words);
                cursor = start + ReadInt(block, "outerLength", 0);
            }
            string final = text.Substring(cursor).Trim();
            if (final.Length > 0) spans.Add(final);
            return spans;
        }

        private static bool PublicGossipQuoteSupported(string quote, Dictionary<string, object> source) =>
            ReadStringList(source, "speechSpans").Any(span => GossipQuoteSupported(quote, span));

        private static Dictionary<string, object> PublicGossipSource(Dictionary<string, object> turn)
        {
            var spans = PublicGossipSpeechSpans(ReadString(turn, "text", ""));
            return GossipFields("turn_id", ReadString(turn, "turn_id", ""), "speaker_id", ReadString(turn, "speaker_id", ""),
                "speechSpans", spans, "text", string.Join("\n[separate spoken span]\n", spans),
                "projectionHash", PromptHash(Json.Serialize(spans)), "projectionVersion", PublicWitnessGossipVersion);
        }

        private static bool PublicWitnessGossipSourcesActive(Dictionary<string, object> data,
            IEnumerable<Dictionary<string, object>> activeTurns)
        {
            var sources = ReadDictionaryList(data, "publicWitnessSources");
            var turns = activeTurns.ToList();
            string quote = ReadString(data, "sourceQuote", "");
            return sources.Count > 0 && sources.Count <= 2 && sources.All(source =>
            {
                var matching = turns.Where(t => ReadString(t, "turn_id", "") == ReadString(source, "turn_id", "")).Take(2).ToList();
                if (matching.Count != 1 || ReadString(source, "projectionVersion", "") != PublicWitnessGossipVersion) return false;
                var turn = matching[0];
                var current = PublicGossipSource(turn);
                return ReadString(current, "speaker_id", "") == ReadString(source, "speaker_id", "")
                    && ReadString(current, "text", "") == ReadString(source, "text", "")
                    && ReadStringList(current, "speechSpans").SequenceEqual(ReadStringList(source, "speechSpans"), StringComparer.Ordinal)
                    && ReadString(current, "projectionHash", "") == ReadString(source, "projectionHash", "");
            }) && ReadStringList(data, "sourceTurnIds").SequenceEqual(sources.Select(source => ReadString(source, "turn_id", "")), StringComparer.Ordinal)
                && sources.Count(source => PublicGossipQuoteSupported(quote, source)
                    && GossipSameId(ReadString(source, "speaker_id", ""), ReadString(data, "witnessSourceSpeakerId", ""))
                    && GossipSameId(ReadString(source, "speaker_id", ""), ReadString(data, "sourceQuoteSpeakerId", ""))) == 1;
        }

        private static void BindPublicWitnessGossipSources(Dictionary<string, object> data,
            IEnumerable<Dictionary<string, object>> activeTurns)
        {
            data.Remove("sourceEncounterTurns");
            // Verify the frozen projection; never regenerate its authority from a
            // newer source, and never copy the private canonical body into a brief.
            data["publicWitnessSourceVerified"] = PublicWitnessGossipSourcesActive(data, activeTurns);
        }

        private static Dictionary<string, object> PublicWitnessGossipEncounterContext(Dictionary<string, object> data) =>
            GossipFields("kind", "witnessed_public_speech", "turns", ReadBool(data, "publicWitnessSourceVerified", false)
                ? ReadDictionaryList(data, "publicWitnessSources").Select(source => GossipFields(
                    "role", ReadString(source, "speaker_id", "") == ReadString(data, "witnessSourceSpeakerId", "")
                        ? "quoted_participant" : "other_participant", "words", ReadString(source, "text", ""))).ToList()
                : new List<Dictionary<string, object>>(),
                "evidenceBoundary", "The gossip speaker overheard these public words and was not their addressee. The quotation proves what was said, not the truth of a claim, completion of a proposed act, or anyone's private feelings.");

        private static bool PublicWitnessGossipCapacityReached(ReignDbConnection c, Dictionary<string, object> topic)
        {
            var rows = QuerySql(c, @"SELECT m.payload_json FROM personal_gossip_messages m
JOIN personal_gossip_topics t ON t.topic_id=m.topic_id
WHERE m.timeline_id=$timeline AND t.session_id=$session AND m.is_reply=0 AND m.hop=1;",
                GossipFields("timeline", ReadString(topic, "timelineId", "main"), "session", ReadString(topic, "sessionId", "")));
            return rows.Count(row => ReadString(GossipJson(row), "sourceOrigin", "") == "public_witness")
                >= Math.Min(2, ReadInt(ReadDictionary(topic, "policy"), "maximumOriginalsPerConversation", 2));
        }

        private static void AcceptPublicWitnessGossip(ReignDbConnection c, string campaign, Dictionary<string, object> payload,
            Dictionary<string, object> parsed, Dictionary<string, object> episode, string participant, string player,
            List<Dictionary<string, object>> people)
        {
            string mode = ReadString(payload, "mode", "dialogue");
            if (mode == "correspondence" || ReadBool(payload, "officialMemoryFirewall", false)
                || ReadBool(ConversationSceneSnapshot(mode, payload), "private", false)) return;
            string session = ReadString(episode, "sessionId", ""), exchange = ReadString(episode, "exchangeId", "");
            string timeline = GossipTimeline(payload);
            double day = ReadDouble(payload, "worldDay", 0);
            var presence = ReadDictionary(payload, "gossipPresence");
            if (!ReadBool(presence, "rosterComplete", false) || GossipTimeline(presence) != timeline
                || ReadDouble(presence, "worldDay", -1) != day) return;
            var nativeSceneIds = ReadStringList(ConversationSceneSnapshot(mode, payload), "witnessIds");
            var current = ReadDictionaryList(ReadDictionary(payload, "conversationSceneState"), "current");
            if (ReadDictionary(payload, "conversationSceneState")?.ContainsKey("current") == true && current.Count == 0) return;
            var scenePeople = (current.Count > 0 ? current : MergedInteractionParticipantProfiles(payload)).Select(CharacterIdFrom)
                .Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var witnesses = people.Where(person => nativeSceneIds.Contains(GossipHeroId(person), StringComparer.OrdinalIgnoreCase)
                    && scenePeople.Contains(GossipHeroId(person)) && GossipCommunicationAvailable(person)
                    && !GossipSameId(GossipHeroId(person), participant) && !GossipSameId(GossipHeroId(person), player))
                .GroupBy(GossipHeroId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(person => PromptHash(timeline + "|" + session + "|" + Math.Floor(day).ToString(CultureInfo.InvariantCulture) + "|" + GossipHeroId(person)), StringComparer.Ordinal)
                .Take(16).ToList();
            if (session.Length == 0 || exchange.Length == 0 || witnesses.Count == 0) return;
            var main = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), player));
            var other = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), participant));
            // Only the two accepted public parent turns belong to this source.
            // A private child exchange is never supplied here.
            var turns = ReadStringList(episode, "turnIds").Distinct(StringComparer.Ordinal).Take(2)
                .SelectMany(id => QuerySql(c, @"SELECT turn_id,speaker_id,text FROM conversation_turns
WHERE turn_id=$id AND session_id=$session AND exchange_id=$exchange AND status='active' AND world_day=$day;",
                    GossipFields("id", id, "session", session, "exchange", exchange, "day", day)))
                .Where(turn => GossipSameId(ReadString(turn, "speaker_id", ""), player) || GossipSameId(ReadString(turn, "speaker_id", ""), participant))
                .Select(PublicGossipSource).ToList();
            if (turns.Count == 0) return;
            foreach (var signal in ReadDictionaryList(parsed, "publicWitnessSignals").Take(2))
            {
                string category = ReadString(signal, "topic", ""), quote = ReadString(signal, "evidenceQuote", "").Trim();
                int salience = GossipFeeling(signal, "salience");
                if (!new[] { "shared_activity", "relationship", "politics", "gift", "conversation" }.Contains(category)
                    || !(ReadDouble(signal, "confidence", 0) >= .80 && ReadDouble(signal, "confidence", 0) <= 1)
                    || ReadDouble(signal, "salience", -1) != salience || salience == 0
                    || ReadString(signal, "privacy", "") != "public" || ReadString(signal, "existencePrivacy", "") != "public"
                    || !signal.TryGetValue("promisedSecrecy", out object secrecy) || secrecy is not bool promised || promised) continue;
                // Check ambiguity before whole-record budget omission.
                var matches = turns.Where(source => PublicGossipQuoteSupported(quote, source)).ToList();
                if (matches.Count != 1) continue;
                var selected = matches[0];
                if (ReadString(selected, "text", "").Length > GossipSourceContextCharacters) continue;
                string author = ReadString(selected, "speaker_id", "");
                // Literal names must occur within an actual speech span. Do not
                // resolve kinship from a third party's hidden native family links.
                var publicMentions = ReadStringList(selected, "speechSpans").SelectMany(span =>
                    GossipMentionedIds(span, GossipFields(), GossipFields(), people)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var witness in witnesses)
                {
                    string observer = GossipHeroId(witness);
                    if (!GossipLocal(witness, main) || !GossipLocal(witness, other)) continue;
                    string id = GossipTopicId(timeline, observer, player, day, category, participant) + "_witness";
                    var prior = QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", id)).FirstOrDefault();
                    if (prior != null && ReadString(prior, "status", "") == "queued") continue;
                    var reference = GossipPlayerReference(c, observer, player, main, payload);
                    string playerLabel = ReadString(reference, "label", "the traveler");
                    string otherLabel = other != null && GossipKnown(c, observer, participant, witness, other)
                        ? ReadString(other, "name", "another guest") : "another guest";
                    var metadata = GossipFields("topic", category, "confidence", ReadDouble(signal, "confidence", 0),
                        "evidenceQuote", quote, "excitement", 0, "curiosity", 0, "hurt", 0,
                        "privacy", ReadString(signal, "privacy", ""), "existencePrivacy", ReadString(signal, "existencePrivacy", ""), "promisedSecrecy", promised,
                        "ownCompromising", false, "confidenceOwnerIds", Array.Empty<string>(), "targets", Array.Empty<object>());
                    var topic = GossipFields("id", id, "timelineId", timeline, "speakerId", observer, "playerId", player,
                        "sessionId", session, "createdDay", prior == null ? day : ReadDouble(prior, "created_day", day), "worldDay", day,
                        "category", category, "signal", metadata, "speaker", witness, "sourceOrigin", "public_witness",
                        "witnessInterest", salience, "witnessPolicyVersion", PublicWitnessGossipVersion,
                        "publicWitnessSources", new[] { selected }, "publicWitnessSourceVerified", true,
                        "witnessSourceSpeakerId", author, "witnessSourceSpeakerLabel", GossipSameId(author, player) ? playerLabel : otherLabel,
                        "witnessSourceAddresseeLabel", GossipSameId(author, player) ? otherLabel : playerLabel,
                        "sourceQuoteSpeakerId", author, "sourceQuote", quote, "sourceText", ReadString(selected, "text", ""),
                        "sourceTurnIds", new[] { ReadString(selected, "turn_id", "") }, "sourceExchangeId", exchange,
                        "sourceSessionId", session, "sourceEvidenceExchangeId", exchange, "playerReference", reference, "playerName", playerLabel,
                        "mentionedIds", publicMentions, "rootId", id, "hop", 1, "holders", new[] { observer, participant, player },
                        "presenceComplete", true, "policy", prior == null ? PersonalGossipSettings() : ReadDictionary(GossipJson(prior), "policy"));
                    topic = PreservePersonalGossipSource(c, prior == null ? null : GossipJson(prior), topic);
                    if (PublicWitnessGossipCapacityReached(c, topic))
                    { SavePersonalGossipTopic(c, topic, "witness_capacity_reached"); return; }
                    EvaluatePersonalGossip(c, campaign, topic, people);
                }
            }
        }
    }
}
