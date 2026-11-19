using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const string ReviewedWriterPlanRules = "RECALL RESPONSE PLAN — internal to the response. First fill recallBasis with the exact reviewed recollections. "
            + "Use the named source as the person making each report. The source may name a different actor. "
            + "Then answer the final player contribution in character, with all relevant qualifiers, joint conditions, fallbacks, "
            + "and corrections intact. Give only facts grounded in the plan, current authored speech, or authoritative state. "
            + "Say what remains unknown if an asked-for detail is missing. A new player report stays attributed to them. "
            + "Do not claim to have performed an action or changed a record unless an authorized effect occurred. "
            + "Do not repeat the plan or its labels to the player. The reply wording is yours.";
        private const string ReviewedDeliveryRules = "RECALL RESPONSE DELIVERY — choose direct or conversational delivery for the reviewed recollection. "
            + "The engine supplies the checked speech. Keep the delivery label out of dialogue.";
        private static readonly string[] ReviewedDeliveryStyles = { "direct", "conversational" };

        private sealed record ReviewedReplayWriterPlan(Dictionary<string, object> Expected, Dictionary<string, object> DeliveryChoices,
            string DeliveryChoicesSha256);

        private static bool IsReviewedDelivery(string mode) => mode is "reviewed_bound_delivery" or "reviewed_bound_delivery_with_player_report";
        private static bool IsReviewedPlayerReport(string mode) => mode is "reviewed_basis_with_player_report" or "reviewed_bound_delivery_with_player_report";

        private static bool ReplaySha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

        private static string ReplayReviewedFactInputError(Dictionary<string, object> item, List<Dictionary<string, object>> sources)
        {
            if (!item.ContainsKey("reviewedFactReplay")) return "";
            var review = ReadDictionary(item, "reviewedFactReplay");
            var writer = ReadDictionary(item, "writerReplay");
            var response = ReadDictionary(writer, "response");
            if (review == null || writer == null || writer.ContainsKey("continuation")
                || ReadLong(writer, "snapshotAsOfSequence", -1) != ReadLong(item, "asOfSequence", -2)
                || (response != null && !ReplaySha256(ReadString(response, "requestSha256", "")))
                || review.Keys.Any(k => k is not ("reviewReceiptSha256" or "facts" or "writerPlan" or "deliveryChoices"))
                || !ReplaySha256(ReadString(review, "reviewReceiptSha256", ""))
                || !IsConversationObjectArray(review.GetValueOrDefault("facts"))) return "invalid_reviewed_fact_replay";
            if (writer.TryGetValue("repairResponses", out object localRepairs) && !item.ContainsKey("reviewedRecoveryReplay")
                && (response == null || review.ContainsKey("writerPlan") || item.ContainsKey("publishedPartyViewSha256")
                    || item.ContainsKey("durableReadReplay") || ReadString(item, "route", "dialogue") is not ("dialogue" or "event")
                    || !IsConversationObjectArray(localRepairs) || ReadDictionaryList(writer, "repairResponses").Count != 0))
                return "invalid_reviewed_fact_replay";

            string mode = ReadString(review, "writerPlan", "");
            if (review.TryGetValue("writerPlan", out object writerPlan)
                && (writerPlan is not string || !(IsReviewedDelivery(mode) || mode is "reviewed_basis" or "reviewed_basis_with_player_report")
                    || item.ContainsKey("publishedPartyViewSha256") || item.ContainsKey("reviewedRecoveryReplay") || item.ContainsKey("durableReadReplay")
                    || ReadString(item, "route", "dialogue") is not ("dialogue" or "event")
                    || (IsReviewedPlayerReport(mode) && ReadString(item, "query", "").Length is < 12 or > 1200)))
                return "invalid_reviewed_writer_plan";
            if (IsReviewedDelivery(mode) != review.ContainsKey("deliveryChoices")) return "invalid_reviewed_delivery_choices";
            if (IsReviewedDelivery(mode))
            {
                var choices = ReadDictionary(review, "deliveryChoices");
                if (choices?.Count != ReviewedDeliveryStyles.Length || ReviewedDeliveryStyles.Any(style =>
                    !choices.TryGetValue(style, out var value) || value is not string line || line.Length is < 1 or > 1500
                    || string.IsNullOrWhiteSpace(line) || line.Any(char.IsControl) || line.Contains("[[", StringComparison.Ordinal)))
                    return "invalid_reviewed_delivery_choices";
            }

            string observer = ReadString(item, "observerId", "");
            var expectedRecent = sources.Where(s => ReadStringList(s, "audience").Contains(observer))
                .OrderBy(s => ReadLong(s, "sequence", 0)).TakeLast(29).Select(s => ReadString(s, "sourceId", "")).ToList();
            if (expectedRecent.Count != 29 || !expectedRecent.SequenceEqual(ReadStringList(item, "recentSourceIds")))
                return "reviewed_fact_replay_requires_latest_29";

            var facts = ReadDictionaryList(review, "facts");
            var keys = new[] { "id", "sourceId", "sourceSequence", "sourceTextSha256", "speakerId", "quote", "text" };
            if (facts.Count is < 1 or > 8 || facts.Select(f => ReadString(f, "id", "")).Distinct(StringComparer.Ordinal).Count() != facts.Count
                || facts.Any(f => f.Keys.Any(k => !keys.Contains(k, StringComparer.Ordinal)))) return "invalid_reviewed_fact_replay";
            foreach (var fact in facts)
            {
                string id = ReadString(fact, "id", ""), text = ReadString(fact, "text", "");
                string source = ReadString(fact, "sourceId", "");
                if (id.Length is < 10 or > 80 || !id.StartsWith("fact:", StringComparison.Ordinal)
                    || id.Skip(5).Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                    || text.Length is < 16 or > 480 || text.Any(char.IsControl) || text.Contains("[[", StringComparison.Ordinal)
                    || ReadString(fact, "quote", "").Length is < 12 or > 800
                    || !ReplaySha256(ReadString(fact, "sourceTextSha256", ""))
                    || ReadString(fact, "speakerId", "").Length == 0 || ReadLong(fact, "sourceSequence", -1) < 0
                    || expectedRecent.Contains(source) || !sources.Any(s => ReadString(s, "sourceId", "") == source))
                    return "invalid_reviewed_fact_replay";
            }
            return "";
        }

        private static string ReplayReviewedSourceBody(Dictionary<string, object> row, string observer)
        {
            string speaker = ReadString(row, "speaker_id", "");
            bool player = ReadString(row, "role", "") == "player";
            string source = ConversationTurnTextForRecall(row);
            if (player && speaker != observer)
                source = ProjectCurrentPlayerPromptText(source, observer, TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? TestDict());
            return ObserverConversationText(source, speaker, observer, player);
        }

        // This input is an experimental reviewed-fact treatment. The receipt
        // identifies an independent review, while the original is rechecked here.
        // A matching quote and hash cannot establish semantic entailment.
        private static Dictionary<string, object> ProjectReplayReviewedFacts(string campaign, string observer, string peer,
            Dictionary<string, object> item, Dictionary<string, object> context, Func<string, string> mapSourceId,
            Dictionary<string, object> stockPacket, PublishedPartyReplayView publishedParty = null, bool followUp = false)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var review = ReadDictionary(item, "reviewedFactReplay");
            var facts = ReadDictionaryList(review, "facts");
            var access = Program.BuildKnowledgeAccessContext(observer, peer, "", context);
            var pins = new List<Dictionary<string, object>>();
            var keptFacts = new List<Dictionary<string, object>>();
            bool bindWriterPlan = !followUp && review.ContainsKey("writerPlan");
            bool boundDelivery = IsReviewedDelivery(ReadString(review, "writerPlan", ""));
            if (bindWriterPlan && access.PlayerIdentityUnknown)
                throw new InvalidOperationException("A reviewed writer plan requires verified actor names in the disposable fixture.");
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            foreach (var fact in facts)
            {
                string id = mapSourceId(ReadString(fact, "sourceId", ""));
                var row = LoadEmbeddingSourceRow(connection, "conversation_turn", id);
                if (row == null || !PrecisionTurnVisible(row, observer, access, connection))
                    throw new InvalidOperationException("Reviewed fact source is outside the eligible observer cutoff.");
                string speaker = ReadString(row, "speaker_id", ""), role = ReadString(row, "role", "");
                string projected = publishedParty?.ExactBody(row, access);
                if (projected == null)
                {
                    if (publishedParty != null) throw new InvalidOperationException("Reviewed fact source is outside the published party view.");
                    projected = ReplayReviewedSourceBody(row, observer);
                }
                var audience = Program.TextListFromJson(ReadString(row, "participants_json", "[]"));
                long sequence = ReadLong(row, "accepted_sequence", -1);
                string digest = ConversationExactTextHash(projected);
                if (!audience.Contains(observer) || !audience.Contains(peer) || speaker != mapSourceId(ReadString(fact, "speakerId", ""))
                    || sequence != ReadLong(fact, "sourceSequence", -1)
                    || !digest.Equals(ReadString(fact, "sourceTextSha256", ""), StringComparison.OrdinalIgnoreCase)
                    || !projected.Contains(ReadString(fact, "quote", ""), StringComparison.Ordinal))
                    throw new InvalidOperationException("Reviewed fact source failed its audience, speaker, sequence or exact projected text check.");
                if ((followUp || boundDelivery) && ReviewedRecoverySourceSuperseded(connection, access, id))
                {
                    if (!followUp) throw new InvalidOperationException("Reviewed delivery source was superseded at the writer cutoff.");
                    continue;
                }
                keptFacts.Add(fact);
                var pin = TestDict("factId", ReadString(fact, "id", ""), "sourceId", id, "speakerId", speaker, "role", role,
                    "sourceSequence", sequence, "sourceTextSha256", digest);
                if (bindWriterPlan) pin["speakerName"] = ReadString(row, "speaker_name", "");
                pins.Add(pin);
            }

            var recent = ReadStringList(item, "recentSourceIds").ToHashSet(StringComparer.Ordinal);
            var older = ReadDictionaryList(item, "sources").Select(s => ReadString(s, "sourceId", ""))
                .Where(id => !recent.Contains(id)).Select(mapSourceId).ToHashSet(StringComparer.Ordinal);
            var precision = Program.CloneDictionary(ReadDictionary(stockPacket, "precisionEvidence"));
            if (ReadString(precision, "mode", "") != "precision")
                throw new InvalidOperationException("Reviewed fact replay requires the stock precision packet.");
            string text = ReadString(stockPacket, "memoryPacket", "");
            var kept = new List<Dictionary<string, object>>();
            var removed = new List<string>();
            foreach (var record in ReadDictionaryList(precision, "records"))
            {
                string kind = ReadString(record, "kind", "");
                var ids = ReadDictionaryList(record, "sourceTurns").Select(t => ReadString(t, "id", "")).ToList();
                bool remove = followUp ? kind is "reported_exchange" or "recent_summary"
                    : kind == "reported_exchange" && ids.Count > 0 && ids.All(older.Contains);
                if (!remove) { kept.Add(record); continue; }
                string rendered = ReadString(record, "rendered", "");
                int start = rendered.Length == 0 ? -1 : text.IndexOf(rendered, StringComparison.Ordinal);
                if (start < 0 || text.LastIndexOf(rendered, StringComparison.Ordinal) != start
                    || PromptHash(rendered) != ReadString(record, "textHash", ""))
                    throw new InvalidOperationException("The declared older record does not match one exact stock packet carrier.");
                text = text.Remove(start, rendered.Length);
                removed.Add(ReadString(record, "alias", ""));
            }
            if (keptFacts.Count > 0)
            {
                string recollection = RenderContinuityRecord("scene_recollection", "reviewed_scene_facts", observer, "",
                    access.WorldDay, 99, true, "REVIEWED SCENE RECOLLECTIONS\nPreserve who said what and any condition or uncertainty. "
                        + "Use these as attributed recollections in natural speech.\n"
                        + string.Join("\n", keptFacts.Select(f => "- " + ReadString(f, "text", ""))));
                text = text.TrimEnd() + "\n\n" + recollection;
            }
            precision["records"] = kept;
            precision["dynamicMemoryTokenEstimate"] = EstimateContinuityTokens(text);
            var projectedPacket = Program.CloneDictionary(stockPacket);
            projectedPacket["memoryPacket"] = text;
            projectedPacket["precisionEvidence"] = precision;
            projectedPacket["reviewedFactProjection"] = TestDict("reviewReceiptSha256", ReadString(review, "reviewReceiptSha256", ""),
                "factPins", pins, "removedAliases", removed, "projectionMs", watch.ElapsedMilliseconds,
                "recordKind", "reviewed_scene_facts", "scope", followUp ? "compact_follow_up_capture" : "initial_writer_only");
            return projectedPacket;
        }

        // Isolated response-shape comparison. Free prose still needs grounding.
        // Reviewed delivery exposes only style choices and keeps approved speech local.
        private static ReviewedReplayWriterPlan PrepareReplayReviewedWriterPlan(Dictionary<string, object> request,
            Dictionary<string, object> item, Dictionary<string, object> projection)
        {
            var review = ReadDictionary(item, "reviewedFactReplay");
            string mode = ReadString(review, "writerPlan", "");
            if (mode.Length == 0) return null;
            var deliveryChoices = IsReviewedDelivery(mode) ? Program.CloneDictionary(ReadDictionary(review, "deliveryChoices")) : null;
            var envelope = ReadDictionary(request, "promptEnvelope");
            var messages = ReadDictionaryList(request, "messages");
            if (envelope == null || !ConversationStructureReceipts.TryGetValue(envelope, out var receipt)
                || receipt.Messages.Count != messages.Count || !ConversationStructureMatches(envelope, messages))
                throw new InvalidOperationException("A reviewed writer plan requires an intact native conversation receipt.");
            var reference = messages.SingleOrDefault(message => ReadString(message, "content", "").StartsWith("REFERENCE CONTEXT —", StringComparison.Ordinal));
            string context = ReadString(reference, "content", "");
            var record = ContinuityPromptRecords(context, true).SingleOrDefault(section => section.Kind == "reviewed_scene_facts");
            int start = record == null ? -1 : context.IndexOf(record.Text, StringComparison.Ordinal);
            if (start < 0 || context.LastIndexOf(record.Text, StringComparison.Ordinal) != start
                || !ReadString(messages.LastOrDefault(), "content", "").StartsWith("LATEST PLAYER TURN ", StringComparison.Ordinal))
                throw new InvalidOperationException("A reviewed writer plan requires one complete recollection and the exact final contribution.");

            Dictionary<string, object> ObjectSchema(Dictionary<string, object> properties) => TestDict("type", "object",
                "properties", properties, "required", properties.Keys.ToArray(), "additionalProperties", false);
            Dictionary<string, object> LiteralSchema(object value) => value is Dictionary<string, object> fields
                ? ObjectSchema(fields.ToDictionary(pair => pair.Key, pair => (object)LiteralSchema(pair.Value)))
                : TestDict("type", "string", "enum", new[] { value });
            var expected = TestDict();
            var properties = TestDict();
            string rules;
            if (deliveryChoices != null)
            {
                properties["delivery"] = TestDict("type", "string", "enum", ReviewedDeliveryStyles);
                rules = ReviewedDeliveryRules;
            }
            else
            {
                var basis = TestDict();
                var pins = ReadDictionaryList(projection, "factPins").ToDictionary(pin => ReadString(pin, "factId", ""), StringComparer.Ordinal);
                foreach (var fact in ReadDictionaryList(review, "facts"))
                {
                    string name = ReadString(pins.GetValueOrDefault(ReadString(fact, "id", "")), "speakerName", "");
                    if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
                        throw new InvalidOperationException("A reviewed writer plan requires a verified display speaker.");
                    basis["recollection" + (basis.Count + 1)] = TestDict("reportedBy", name, "meaning", ReadString(fact, "text", ""));
                }
                expected["recallBasis"] = basis;
                properties["recallBasis"] = LiteralSchema(basis);
                rules = ReviewedWriterPlanRules;
            }
            expected["participation"] = "speak";
            if (IsReviewedPlayerReport(mode))
            {
                const string latestPrefix = "LATEST PLAYER TURN ";
                string latest = ReadString(messages.Last(), "content", "");
                int newline = latest.IndexOf('\n');
                string playerName = newline < latestPrefix.Length ? "" : ReadString(
                    TryParseJsonObject(latest.Substring(latestPrefix.Length, newline - latestPrefix.Length)), "speaker", "");
                string current = ReadString(item, "query", "");
                if (playerName.Length is < 1 or > 120 || playerName.Any(char.IsControl) || latest.Substring(newline + 1) != current)
                    throw new InvalidOperationException("A current report plan requires the exact authored player contribution.");
                if (deliveryChoices == null)
                {
                    var report = TestDict("reportedBy", playerName, "authoredContribution", current, "support", "current player report only");
                    expected["currentReport"] = report;
                    properties["currentReport"] = LiteralSchema(report);
                    rules += " Attribute every claim from currentReport to that player and leave independent confirmation and record changes pending.";
                }
            }
            if (deliveryChoices == null) properties["reply"] = TestDict("type", "string", "minLength", 1, "maxLength", 1500);
            properties["participation"] = LiteralSchema("speak");
            if (IsReviewedPlayerReport(mode))
            {
                string current = ReadString(item, "query", "");
                var write = TestDict("text", current, "sourceEvidence", TestDict("turnId", "current_player", "speakerId", "current_player",
                    "evidenceType", "spoken_statement", "quote", current));
                expected["memoryWrites"] = new[] { write };
                properties["memoryWrites"] = TestDict("type", "array", "items", LiteralSchema(write), "minItems", 1, "maxItems", 1);
            }

            int index = messages.IndexOf(reference);
            var moved = messages.Select(Program.CloneDictionary).ToList();
            moved[index]["content"] = context.Remove(start, record.Text.Length);
            moved.InsertRange(moved.Count - 1, new[] { TestDict("role", "user", "content", record.Text),
                TestDict("role", "user", "content", rules) });
            request["messages"] = moved;
            request["response_format"] = TestDict("type", "json_schema", "json_schema", TestDict("name",
                deliveryChoices == null ? "reign_reviewed_recall_plan" : "reign_reviewed_recall_delivery",
                "strict", true, "schema", ObjectSchema(properties)));
            ConversationStructureReceipts.Remove(envelope);
            ConversationStructureReceipts.Add(envelope, new ConversationStructureReceipt {
                Messages = moved.Select(Program.CloneDictionary).ToList(), CanonicalTranscript = receipt.CanonicalTranscript });
            envelope["messageCount"] = moved.Count;
            ReadDictionary(envelope, "conversationMessageStructure")["serializedMessagesHash"] = PromptHash(Json.Serialize(moved));
            string choicesHash = deliveryChoices == null ? "" : PromptHash(CanonicalJson(TestDict(
                "reviewReceiptSha256", ReadString(review, "reviewReceiptSha256", ""), "deliveryChoices", deliveryChoices)));
            return new ReviewedReplayWriterPlan(expected, deliveryChoices, choicesHash);
        }

        private static bool ApplyReplayReviewedWriterPlan(ReviewedReplayWriterPlan plan, Dictionary<string, object> parsed,
            out string delivery)
        {
            delivery = "";
            if (parsed == null || parsed.Count != plan.Expected.Count + 1
                || !plan.Expected.All(pair => parsed.TryGetValue(pair.Key, out var value) && CanonicalJson(value) == CanonicalJson(pair.Value)))
                return false;
            if (plan.DeliveryChoices != null)
            {
                if (!parsed.Keys.SequenceEqual(new[] { "delivery" }.Concat(plan.Expected.Keys))
                    || parsed.GetValueOrDefault("delivery") is not string choice || !plan.DeliveryChoices.ContainsKey(choice))
                    return false;
                delivery = choice;
                parsed.Remove("delivery");
                parsed["reply"] = plan.DeliveryChoices[choice];
                return true;
            }
            var keys = parsed.Keys.ToList();
            int replyIndex = keys.IndexOf("reply");
            if (parsed.GetValueOrDefault("reply") is not string reply || reply.Length is < 1 or > 1500
                || keys.FirstOrDefault() != "recallBasis" || replyIndex < 1
                || (plan.Expected.ContainsKey("currentReport") && (keys.IndexOf("currentReport") != 1 || replyIndex < 2)))
                return false;
            parsed.Remove("recallBasis");
            parsed.Remove("currentReport");
            return true;
        }

        private static bool ReplayReviewedFactWindowMatches(List<Dictionary<string, object>> messages,
            List<Dictionary<string, object>> prior, string observer, string contribution, out List<string> outsideAuthored)
        {
            static bool IsAuthored(Dictionary<string, object> message)
            {
                string content = ReadString(message, "content", "");
                return content.StartsWith("HISTORICAL ", StringComparison.Ordinal) || content.StartsWith("LATEST PLAYER TURN ", StringComparison.Ordinal);
            }
            var authored = messages.Where(IsAuthored).ToList();
            outsideAuthored = messages.Where(message => !IsAuthored(message)).Select(message => ReadString(message, "content", "")).ToList();
            if (prior.Count != 29 || authored.Count != prior.Count + 1) return false;
            foreach (var (line, message) in prior.Zip(authored))
            {
                bool player = ReadString(line, "role", "") == "player";
                bool own = !player && RecallSourceSpeakerId(line) == observer;
                string prefix = player ? "HISTORICAL PLAYER TURN " : own ? "HISTORICAL RESPONDING NPC TURN "
                    : "HISTORICAL OTHER CHARACTER / NARRATOR ";
                string content = ReadString(message, "content", "");
                int newline = content.IndexOf('\n');
                if (ReadString(message, "role", "") != (own ? "assistant" : "user")
                    || !content.StartsWith(prefix, StringComparison.Ordinal) || newline <= prefix.Length) return false;
                string text = ReadString(line, "text", "");
                int attributed = content.IndexOf(": " + text, newline + 1, StringComparison.Ordinal);
                if (text.Length == 0 || attributed < 0) return false;
                int end = attributed + 2 + text.Length;
                if (end < content.Length && content[end] != '\n') return false;
                var header = TryParseJsonObject(content.Substring(prefix.Length, newline - prefix.Length));
                if (ReadString(header, "storedRole", "") != ReadString(line, "role", "")
                    || (ReadFirstString(line, "turnId", "turn_id", "id").Length > 0
                        && ReadString(header, "turnId", "") != ConversationTranscriptSourceId(line))) return false;
                // Leave the attribution and any extra copies available to the older-source audit.
                outsideAuthored.Add(content.Substring(0, attributed + 2));
                outsideAuthored.Add(content.Substring(end));
            }
            var latest = authored.Last();
            string current = ReadString(latest, "content", "");
            int body = current.IndexOf('\n');
            if (body >= 0) outsideAuthored.Add(current.Substring(0, body));
            return ReadString(latest, "role", "") == "user" && current.StartsWith("LATEST PLAYER TURN ", StringComparison.Ordinal)
                && body >= 0 && current.Substring(body + 1) == contribution;
        }

        private static bool ReplayReviewedOlderOriginalDelivered(List<string> outsideAuthored,
            IEnumerable<Dictionary<string, object>> older, Func<string, string> mapSourceId)
        {
            var rows = older.ToList();
            var ids = rows.Select(source => ConversationTranscriptSourceId(
                TestDict("id", mapSourceId(ReadString(source, "sourceId", ""))))).ToHashSet(StringComparer.Ordinal);
            var originals = rows.Select(source => ReadString(source, "text", "").Replace("\r\n", "\n").Trim())
                .Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            foreach (string content in outsideAuthored)
            {
                if (ids.Any(id => content.Contains("[source=" + id + " role=", StringComparison.Ordinal))
                    || (content.Contains("[[continuity source=", StringComparison.Ordinal)
                        && ContinuityPromptRecords(content, false).Any(record =>
                            (record.Kind is "accepted_exchange" or "reported_exchange" or "targeted_historical_exchange")
                            && record.SourceId.Split(',').Any(ids.Contains)))) return true;

                string text = content.Replace("\r\n", "\n");
                var lines = text.Split('\n').Select(line => line.Trim()).ToList();
                foreach (string original in originals)
                {
                    if (original.Length >= 32 && text.Contains(original, StringComparison.Ordinal)) return true;
                    // A bare interjection can occur independently inside another sentence.
                    if (lines.Any(line => line == original || line.EndsWith(": " + original, StringComparison.Ordinal))
                        || text.Contains("\"" + original + "\"", StringComparison.Ordinal)
                        || text.Contains("'" + original + "'", StringComparison.Ordinal)
                        || text.Contains("“" + original + "”", StringComparison.Ordinal)
                        || text.Contains("‘" + original + "’", StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static string ReplayReviewedFactWireError(Dictionary<string, object> wire, Dictionary<string, object> item,
            List<Dictionary<string, object>> prior, string observer, Func<string, string> mapSourceId)
        {
            var messages = ReadDictionaryList(TryParseJsonObject(ReadString(wire, "requestJson", "")), "messages");
            var sources = ReadDictionaryList(item, "sources");
            var originals = EvidenceReplayRecentSources(item, sources);
            if (originals.Count != 29 || !prior.Select(line => (ReadString(line, "role", ""), ReadString(line, "text", "")))
                    .SequenceEqual(originals.Select(line => (ReadString(line, "role", ""), ReadString(line, "text", ""))))
                || !ReplayReviewedFactWindowMatches(messages, prior, observer, ReadString(item, "query", ""), out var outsideAuthored))
                return "exact_recent_window_missing";
            var recent = ReadStringList(item, "recentSourceIds").ToHashSet(StringComparer.Ordinal);
            var contents = messages.Select(m => ReadString(m, "content", "")).ToList();
            bool Delivered(string text) => contents.Any(content => content.Contains(text, StringComparison.Ordinal));
            if (ReplayReviewedOlderOriginalDelivered(outsideAuthored,
                    sources.Where(source => !recent.Contains(ReadString(source, "sourceId", ""))), mapSourceId))
                return "older_original_reached_initial_wire";
            var facts = ReadDictionaryList(ReadDictionary(item, "reviewedFactReplay"), "facts");
            if (facts.Any(f => !Delivered(ReadString(f, "text", "")) || Delivered(ReadString(f, "id", ""))))
                return "reviewed_fact_missing_or_label_exposed";
            return "";
        }

        private static Dictionary<string, object> CaptureReplayReviewedFollowUp(string campaign, string observer, string peer,
            Dictionary<string, object> item, Dictionary<string, object> next, List<Dictionary<string, object>> transcript,
            Dictionary<string, object> stockPacket, Func<string, string> mapSourceId, Dictionary<string, object> settings,
            Func<Dictionary<string, object>, List<Dictionary<string, object>>, Dictionary<string, object>, Dictionary<string, object>> prepare)
        {
            try
            {
                var prior = transcript.TakeLast(29).ToList();
                var projected = ProjectReplayReviewedFacts(campaign, observer, peer, item, next, mapSourceId, stockPacket, followUp: true);
                var projection = ReadDictionary(projected, "reviewedFactProjection");
                var wire = ReadDictionary(CaptureMemoryReplayProviderRequest(prepare(next, prior, projected), settings), "providerWireCapture");
                var messages = ReadDictionaryList(TryParseJsonObject(ReadString(wire, "requestJson", "")), "messages");
                var retained = prior.Select(ConversationTranscriptSourceId).ToHashSet(StringComparer.Ordinal);
                bool Delivered(string text) => messages.Any(message => ReadString(message, "content", "").Contains(text, StringComparison.Ordinal));
                var facts = ReadDictionaryList(ReadDictionary(item, "reviewedFactReplay"), "facts");
                var pinned = ReadDictionaryList(projection, "factPins").Select(pin => ReadString(pin, "factId", "")).ToHashSet(StringComparer.Ordinal);
                string failure = "";
                if (ReadString(wire, "status", "") != "captured") failure = "provider_preflight_blocked";
                else if (!ReplayReviewedFactWindowMatches(messages, prior, observer, ReadString(next, "playerText", ""), out var outsideAuthored))
                    failure = "exact_recent_window_missing";
                else if (ReplayReviewedOlderOriginalDelivered(outsideAuthored, ReadDictionaryList(item, "sources")
                    .Where(source => !retained.Contains(ConversationTranscriptSourceId(
                        TestDict("id", mapSourceId(ReadString(source, "sourceId", "")))))), mapSourceId))
                    failure = "older_original_reached_follow_up_wire";
                else if (facts.Any(fact => Delivered(ReadString(fact, "id", ""))
                    || (pinned.Contains(ReadString(fact, "id", "")) && !Delivered(ReadString(fact, "text", "")))))
                    failure = "reviewed_fact_missing_or_label_exposed";
                return TestDict("status", failure.Length == 0 ? "captured" : "blocked", "reason", failure,
                    "projection", projection, "providerWireCapture", wire, "providerCalls", 0);
            }
            catch (InvalidOperationException ex)
            {
                return TestDict("status", "blocked", "reason", "follow_up_preparation_failed", "error", ex.Message, "providerCalls", 0);
            }
        }
    }
}
