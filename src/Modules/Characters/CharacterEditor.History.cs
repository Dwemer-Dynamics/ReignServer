using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string EditorHistoryProjectionSource = "character_editor_rebuild";
        private static readonly string[] EditorHistoryGroups = { "dialogue", "events", "decisions" };
        private static readonly UTF8Encoding EditorHistoryUtf8 = new UTF8Encoding(false, true);

        private sealed record EditorHistoryLine(int Start, int TextEnd, int End, Dictionary<string, object> Row);
        private sealed record EditorHistorySource(string Event, string Turn, string Timeline, string Speaker,
            string Role, string SpeakerName, long Timestamp, double Day, string Location, HashSet<string> Audience);

        private static Dictionary<string, object> ReadEditorHistory(string directory)
        {
            lock (Program.FileLock)
                return EditorHistoryGroups.ToDictionary(group => group, group => (object)ReadEditorHistoryLines(
                    Path.Combine(directory, group + ".jsonl"), out _, out _)
                    .Where(line => line.Row != null).Select(line => line.Row).ToList());
        }

        private static List<EditorHistoryLine> ReadEditorHistoryLines(string path, out byte[] bytes, out int preamble)
        {
            bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            preamble = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()) ? 3 : 0;
            var lines = new List<EditorHistoryLine>();
            for (int start = preamble; start < bytes.Length;)
            {
                int newline = Array.IndexOf(bytes, (byte)'\n', start);
                int textEnd = newline < 0 ? bytes.Length : newline;
                int end = newline < 0 ? bytes.Length : newline + 1;
                if (textEnd > start && bytes[textEnd - 1] == '\r') textEnd--;
                Dictionary<string, object> row = null;
                try { row = TryParseJsonObject(EditorHistoryUtf8.GetString(bytes, start, textEnd - start)); }
                catch (DecoderFallbackException) { } // Keep opaque bytes out of the edit model.
                lines.Add(new EditorHistoryLine(start, textEnd, end, row));
                start = end;
            }
            return lines;
        }

        private static string EditorHistoryRowId(Dictionary<string, object> row) => ReadString(row, "id", "");

        private static bool SameEditorHistoryRow(Dictionary<string, object> left, Dictionary<string, object> right) =>
            CanonicalJson(left) == CanonicalJson(right);

        private static string EditorHistoryIssueMessage(List<Dictionary<string, object>> issues) =>
            issues.Count == 0 ? "" : " Some history changes were skipped because their source or current version could not be verified.";

        internal static List<Dictionary<string, object>> ApplyEditorHistory(string campaign, string hero,
            ReignDbConnection connection, Dictionary<string, object> history, Dictionary<string, object> revisionAfter = null)
        {
            var issues = new List<Dictionary<string, object>>();
            if (history == null) return issues;

            using var transaction = connection.BeginTransaction();
            string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
            var processedTurns = new HashSet<string>(StringComparer.Ordinal);
            lock (Program.FileLock)
            {
                foreach (string group in EditorHistoryGroups)
                {
                    if (!history.TryGetValue(group, out object groupValue) || (revisionAfter != null && !revisionAfter.ContainsKey(group))) continue;
                    if (groupValue is not IList rowList || rowList.Cast<object>().Any(row => row is not Dictionary<string, object>))
                    {
                        issues.Add(new Dictionary<string, object> { ["group"] = group, ["rowId"] = "", ["reason"] = "invalid_history_collection" });
                        continue;
                    }
                    string path = CampaignFileService.CharacterFile(campaign, hero, "history", group + ".jsonl");
                    var lines = ReadEditorHistoryLines(path, out byte[] bytes, out int preamble);
                    var current = lines.Where(line => line.Row != null).ToLookup(line => EditorHistoryRowId(line.Row), StringComparer.Ordinal);
                    var incoming = ReadDictionaryList(history, group).ToLookup(EditorHistoryRowId, StringComparer.Ordinal);
                    var expected = revisionAfter == null
                        ? current.SelectMany(rows => rows.Select(line => line.Row)).ToLookup(EditorHistoryRowId, StringComparer.Ordinal)
                        : ReadDictionaryList(revisionAfter, group).ToLookup(EditorHistoryRowId, StringComparer.Ordinal);
                    var patches = new Dictionary<int, Dictionary<string, object>>();
                    var restored = new List<Dictionary<string, object>>();
                    void Issue(string id, string reason) => issues.Add(new Dictionary<string, object>
                        { ["group"] = group, ["rowId"] = id, ["reason"] = reason });

                    foreach (string id in expected.Select(rows => rows.Key).Union(incoming.Select(rows => rows.Key), StringComparer.Ordinal))
                    {
                        var oldRows = expected[id].ToList();
                        var newRows = incoming[id].ToList();
                        if (oldRows.Count == newRows.Count && oldRows.Zip(newRows, SameEditorHistoryRow).All(equal => equal)) continue;
                        if (string.IsNullOrWhiteSpace(id) || oldRows.Count > 1 || newRows.Count > 1 || current[id].Count() > 1)
                        {
                            Issue(id, "ambiguous_history_row_id");
                            continue;
                        }

                        var oldRow = oldRows.SingleOrDefault();
                        var desired = newRows.SingleOrDefault();
                        var line = current[id].SingleOrDefault();
                        if (revisionAfter != null && !SameEditorHistoryRow(line?.Row, oldRow))
                        {
                            Issue(id, "history_row_changed_after_revision");
                            continue;
                        }
                        // A new row can only come from a retained server revision.
                        var original = line?.Row ?? (revisionAfter != null ? desired : null);
                        if (original == null)
                        {
                            Issue(id, "unknown_history_row");
                            continue;
                        }

                        string textKey = new[] { "text", "content", "message", "reply", "body" }
                            .FirstOrDefault(key => original.TryGetValue(key, out object value) && value != null) ?? "text";
                        Dictionary<string, object> replacement = null;
                        if (desired != null)
                        {
                            replacement = new Dictionary<string, object>(original, original.Comparer);
                            if (desired.TryGetValue(textKey, out object value) && value is string text) replacement[textKey] = text;
                            if (!SameEditorHistoryRow(replacement, desired))
                            {
                                Issue(id, "unsupported_history_metadata_change");
                                continue;
                            }
                        }

                        if (group != "decisions")
                        {
                            var source = ResolveEditorHistorySource(connection, campaign, hero, timeline, group, original);
                            if (source == null || !processedTurns.Add(source.Turn))
                            {
                                Issue(id, "unverified_history_source");
                                continue;
                            }
                            RebuildEditedHistoryProjection(connection, hero, source,
                                replacement == null ? "" : ReadString(replacement, textKey, ""));
                        }

                        if (line != null) patches.Add(line.Start, replacement);
                        else restored.Add(replacement);
                    }
                    if (patches.Count > 0 || restored.Count > 0)
                        WriteEditorHistoryLines(path, bytes, preamble, lines, patches, restored);
                }
            }
            transaction.Commit();
            return issues;
        }

        private static void WriteEditorHistoryLines(string path, byte[] bytes, int preamble, List<EditorHistoryLine> lines,
            Dictionary<int, Dictionary<string, object>> patches, List<Dictionary<string, object>> restored)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string staged = path + ".editor-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.Write(bytes, 0, preamble);
                    bool needsSeparator = false;
                    foreach (var line in lines)
                    {
                        if (!patches.TryGetValue(line.Start, out var replacement))
                            stream.Write(bytes, line.Start, line.End - line.Start);
                        else if (replacement != null)
                        {
                            stream.Write(EditorHistoryUtf8.GetBytes(Json.Serialize(replacement)));
                            stream.Write(bytes, line.TextEnd, line.End - line.TextEnd);
                        }
                        else continue;

                        needsSeparator = bytes[line.End - 1] != '\n';
                    }
                    foreach (var row in restored)
                    {
                        if (needsSeparator) stream.WriteByte((byte)'\n');
                        stream.Write(EditorHistoryUtf8.GetBytes(Json.Serialize(row) + "\n"));
                        needsSeparator = false;
                    }
                }
                File.Move(staged, path, true);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
        }

        private static EditorHistorySource ResolveEditorHistorySource(ReignDbConnection connection, string campaign,
            string hero, string timeline, string group, Dictionary<string, object> row)
        {
            string turn = ReadString(row, "sourceTurnId", "");
            string session = ReadString(row, "sessionId", "");
            string exchange = ReadString(row, "exchangeId", "");
            string role = ReadString(row, "role", "");
            string rowTimeline = ReadString(row, "timelineId", "");
            // Older individual dialogue carries the exact session and accepted exchange.
            // A social event/beat identifier can belong to several speakers and turns.
            if ((turn.Length == 0 && group != "dialogue") || session.Length == 0 || exchange.Length == 0
                || (role != "player" && role != "npc") || (rowTimeline.Length > 0 && rowTimeline != timeline)) return null;

            var sources = CharactersStore.SelectEditorHistorySources(connection, new Dictionary<string, object>
                { ["campaign"] = campaign, ["hero"] = hero, ["turn"] = turn, ["session"] = session, ["exchange"] = exchange, ["role"] = role });
            if (sources.Count != 1) return null;
            var source = sources[0];
            var turnPayload = TryParseJsonObject(ReadString(source, "payload_json", ""));
            var eventPayload = TryParseJsonObject(ReadString(source, "event_payload_json", ""));
            var ownerPayload = TryParseJsonObject(ReadString(source, "owner_payload_json", ""));
            string eventTimeline = ReadString(eventPayload, "timelineId", "");
            string speaker = ReadString(source, "speaker_id", "");
            string expectedSpeaker = ReadString(row, "speakerHeroStringId", "");
            if (turnPayload == null || eventPayload == null || ownerPayload == null
                || DialogueService.ContinuityTimeline(turnPayload) != timeline || DialogueService.ContinuityTimeline(ownerPayload) != timeline
                || (eventTimeline.Length > 0 && eventTimeline != timeline) || speaker.Length == 0
                || (expectedSpeaker.Length > 0 && expectedSpeaker != speaker)) return null;

            var audience = EditorHistoryAudience(source, "participants_json");
            var eventAudience = EditorHistoryAudience(source, "event_participants_json", "event_witnesses_json");
            audience.IntersectWith(eventAudience);
            audience.ExceptWith(EditorHistoryAudience(source, "event_hidden_from_json"));
            if (!audience.Contains(hero)) return null;
            return new EditorHistorySource(ReadString(source, "event_id", ""), ReadString(source, "turn_id", ""), timeline,
                speaker, role, ReadString(source, "speaker_name", ""), ReadLong(source, "ts", 0),
                ReadDouble(source, "world_day", 0d), ReadString(source, "location_id", ""), audience);
        }

        private static HashSet<string> EditorHistoryAudience(Dictionary<string, object> row, params string[] fields) =>
            fields.SelectMany(field => DialogueService.TextListFromJson(ReadString(row, field, "[]")))
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);

        private static void RebuildEditedHistoryProjection(ReignDbConnection connection, string hero, EditorHistorySource source, string text)
        {
            var observers = new HashSet<string>(StringComparer.Ordinal) { hero };
            foreach (var memory in CharactersStore.SelectEditorHistoryMemories(connection,
                new Dictionary<string, object> { ["event"] = source.Event }))
            {
                var payload = TryParseJsonObject(ReadString(memory, "payload_json", ""));
                if (ReadString(payload, "timelineId", "") != source.Timeline
                    || ReadString(payload, "sourceEventId", "") != source.Event) continue;
                string owner = ReadString(memory, "owner_id", "");
                var audience = EditorHistoryAudience(memory, "participants_json", "known_by_json", "witnesses_json", "heard_as_rumor_by_json");
                audience.Add(owner);
                audience.ExceptWith(EditorHistoryAudience(memory, "hidden_from_json"));
                string scope = ReadString(payload, "observerScope", "");
                if (ReadString(memory, "source", "") == EditorHistoryProjectionSource)
                {
                    if (ReadString(payload, "editorOwnerId", "") != hero || ReadString(payload, "sourceTurnId", "") != source.Turn
                        || audience.Count != 1 || !audience.Contains(owner)) continue;
                }
                else
                {
                    var evidence = ReadDictionary(payload, "sourceEvidence");
                    string evidenceTurn = ReadFirstString(evidence, "turnId", "sourceTurnId");
                    string evidenceEvent = ReadString(evidence, "sourceEventId", "");
                    // StoreTurnMemoryLayers records current-player/NPC evidence under
                    // role aliases scoped by the accepted event and exact speaker.
                    bool currentTurnAlias = evidenceTurn == "current_" + source.Role
                        && evidenceEvent == source.Event
                        && ReadString(evidence, "speakerId", "") == source.Speaker
                        && ReadString(evidence, "status", "") == "supported_span";
                    string memoryTurn = ReadString(payload, "sourceTurnId", "");
                    if ((evidenceEvent.Length > 0 && evidenceEvent != source.Event)
                        || (evidenceTurn.Length > 0 && evidenceTurn != source.Turn && !currentTurnAlias)
                        || (memoryTurn.Length > 0 && memoryTurn != source.Turn)
                        || !audience.Contains(owner)
                        || (scope != "observable_source_spans" && (owner != hero || !audience.SetEquals(new[] { hero })))) continue;
                }

                if (!audience.IsSubsetOf(source.Audience)) continue;
                observers.UnionWith(audience);
                CharactersStore.SupersedeEditorHistoryMemory(connection,
                    new Dictionary<string, object> { ["id"] = ReadString(memory, "memory_id", ""), ["event"] = source.Event });
            }

            foreach (string observer in observers)
            {
                string visible = DialogueService.ObserverConversationText(text, source.Speaker, observer, source.Role == "player");
                if (string.IsNullOrWhiteSpace(visible)) continue;
                var payload = new Dictionary<string, object>
                {
                    ["editorOwnerId"] = hero, ["timelineId"] = source.Timeline, ["sourceEventId"] = source.Event,
                    ["sourceTurnId"] = source.Turn, ["evidenceClass"] = "editor_annotation"
                };
                string summary = "Character Editor correction to " + source.SpeakerName + "'s history: " + visible;
                var audience = new List<string> { observer };
                DialogueService.InsertMemoryRow(connection, "mem_editor_" + Guid.NewGuid().ToString("N"), source.Event,
                    observer, "interpersonal_experience", source.Timestamp, source.Day, source.Location, summary,
                    audience, new List<string>(), "[]", audience, new List<string>(), new List<string>(), "private",
                    0.6d, 0d, 1d, new List<string> { "conversation", "character_editor" }, "active", EditorHistoryProjectionSource,
                    Json.Serialize(payload), "", "not_indexed");
            }
        }
    }
}
