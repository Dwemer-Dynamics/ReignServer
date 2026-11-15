using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PublishedPartyReplaySchema = "reign-evidence-party-publication-v1";
        private const int PublishedPartyReplayMaxBytes = 256 * 1024;

        private sealed record PublishedPartyReplayRow(string Id, string Exchange, string SpeakerId, string Speaker,
            string Role, string Body, long Sequence, double Day);

        // Only the isolated evidence reader constructs these bindings. The lookup
        // belongs to one stock precision read's access object, never a payload flag.
        private static readonly ConditionalWeakTable<KnowledgeAccessContext, PublishedPartyReplayView> PublishedPartyReplayReads = new();

        private sealed class PublishedPartyReplayView
        {
            private readonly string campaign, observer, peer, timeline, session, query;
            private readonly long cutoff;
            private readonly double day;
            private readonly Dictionary<string, PublishedPartyReplayRow> rows;
            private readonly PublishedPartyReplayRow[] recent;
            private readonly string[] audience;
            private readonly HashSet<string> sharedAudience;
            private readonly string digest, archiveDigest;

            internal PublishedPartyReplayView(string campaign, string observer, string peer, string timeline, string session,
                string query, long cutoff, double day, string[] audience, List<PublishedPartyReplayRow> rows,
                IEnumerable<string> recentIds, string digest, string archiveDigest)
            {
                this.campaign = campaign; this.observer = observer; this.peer = peer; this.timeline = timeline;
                this.session = session; this.query = query; this.cutoff = cutoff; this.day = day; this.audience = audience;
                sharedAudience = audience.ToHashSet(StringComparer.Ordinal);
                this.rows = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
                recent = recentIds.Select(id => this.rows[id]).ToArray();
                this.digest = digest; this.archiveDigest = archiveDigest;
            }

            internal string[] Audience => audience.ToArray();
            internal Dictionary<string, object> Audit => TestDict("status", "verified", "scope", "isolated_initial_party_request",
                "publicationSha256", digest, "archiveReceiptSha256", archiveDigest, "listenerCount", audience.Length,
                "sourceCount", rows.Count, "recentCount", recent.Length);

            internal List<Dictionary<string, object>> Transcript() => recent.Select(row => TestDict("turnId", row.Id,
                "sessionId", session, "exchangeId", row.Exchange, "speakerId", row.SpeakerId, "speaker", row.Speaker,
                "role", row.Role, "text", row.Body)).ToList();

            private bool Matches(KnowledgeAccessContext access) => access != null && access.NpcId == observer
                && access.PlayerId == peer && access.TimelineId == timeline && access.AsOfAcceptedSequence == cutoff
                && access.WorldDay == day && !access.PlayerIdentityUnknown;

            internal void BindRead(string readCampaign, KnowledgeAccessContext access, string readQuery)
            {
                if (readCampaign != campaign || readQuery != query || !Matches(access))
                    throw new InvalidOperationException("Published party view does not authorize this precision read.");
                PublishedPartyReplayReads.Add(access, this);
            }

            internal List<Dictionary<string, object>> WriterTranscript(string writerCampaign, string actor,
                Dictionary<string, object> payload, string contribution)
            {
                if (writerCampaign != campaign || actor != observer || contribution != query
                    || ReadString(payload, "mode", "") != "party_chat"
                    || !Matches(BuildKnowledgeAccessContext(actor, ReadString(payload, "playerHeroStringId", ""), "", payload))
                    || !sharedAudience.SetEquals(ReadStringList(payload, "participants")))
                    throw new InvalidOperationException("Published party view does not authorize this writer request.");
                return Transcript();
            }

            internal string ExactBody(Dictionary<string, object> source, KnowledgeAccessContext access)
            {
                if (!Matches(access) || !rows.TryGetValue(ReadString(source, "turn_id", ""), out var expected)) return null;
                if (!PrecisionTurnVisible(source, observer, access) || ReadString(source, "session_id", "") != session
                    || ReadString(source, "exchange_id", "") != expected.Exchange || ReadString(source, "channel", "") != "party_chat"
                    || ReadString(source, "speaker_id", "") != expected.SpeakerId || ReadString(source, "speaker_name", "") != expected.Speaker
                    || ReadString(source, "role", "") != expected.Role || ReadLong(source, "accepted_sequence", -1) != expected.Sequence
                    || ReadDouble(source, "world_day", -1) != expected.Day || ConversationTurnTextForRecall(source) != expected.Body
                    || !sharedAudience.SetEquals(TextListFromJson(ReadString(source, "participants_json", "[]"))))
                    throw new InvalidOperationException("Published party view source failed the isolated DB identity, audience or body check.");
                return expected.Body;
            }

            internal void VerifyStoredSources(Dictionary<string, object> context)
            {
                using var connection = OpenCampaignConnection(campaign);
                var stored = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session;", TestDict("session", session));
                var access = BuildKnowledgeAccessContext(observer, peer, "", context);
                if (stored.Count != rows.Count || stored.Any(row => ExactBody(row, access) == null))
                    throw new InvalidOperationException("Published party view does not match the seeded source inventory.");
            }

            internal bool WirePreservesHistory(Dictionary<string, object> wire)
            {
                var messages = ReadDictionaryList(TryParseJsonObject(ReadString(wire, "requestJson", "")), "messages");
                // The provider adapter can append authority after the authored contribution.
                var authored = messages.Where(m =>
                {
                    string content = ReadString(m, "content", "");
                    return content.StartsWith("HISTORICAL ", StringComparison.Ordinal)
                        || content.StartsWith("LATEST PLAYER TURN ", StringComparison.Ordinal);
                }).ToList();
                if (authored.Count != recent.Length + 1) return false;
                for (int i = 0; i < recent.Length; i++)
                {
                    var row = recent[i];
                    bool own = row.Role == "npc" && row.SpeakerId == observer;
                    string label = row.Role == "player" ? "HISTORICAL PLAYER TURN " : own ? "HISTORICAL RESPONDING NPC TURN "
                        : "HISTORICAL OTHER CHARACTER / NARRATOR ";
                    string content = ReadString(authored[i], "content", "");
                    int newline = content.IndexOf('\n');
                    if (ReadString(authored[i], "role", "") != (own ? "assistant" : "user")
                        || !content.StartsWith(label, StringComparison.Ordinal) || newline <= label.Length) return false;
                    var attribution = TryParseJsonObject(content.Substring(label.Length, newline - label.Length));
                    string expectedId = ConversationTranscriptSourceId(TestDict("turnId", row.Id));
                    if (ReadString(attribution, "turnId", "") != expectedId || ReadString(attribution, "storedRole", "") != row.Role) return false;
                    string piece = content.Substring(newline + 1);
                    if (piece.StartsWith("[[continuity ", StringComparison.Ordinal))
                    {
                        int headerEnd = piece.IndexOf('\n');
                        if (headerEnd < 0 || !piece.Substring(0, headerEnd).EndsWith("]]", StringComparison.Ordinal)) return false;
                        piece = piece.Substring(headerEnd + 1);
                    }
                    string line = "- " + row.Speaker + " [role=" + row.Role + "]: " + row.Body;
                    if (piece != line && piece != line + "\n" && piece != line + "\n[[/continuity]]") return false;
                }
                var latest = authored[^1];
                string final = ReadString(latest, "content", "");
                int latestNewline = final.IndexOf('\n');
                return ReadString(latest, "role", "") == "user" && final.StartsWith("LATEST PLAYER TURN ", StringComparison.Ordinal)
                    && latestNewline >= 0 && final.Substring(latestNewline + 1) == query;
            }
        }

        private static byte[] ReadPublishedPartyReplayFile(string inputPath)
        {
            string receiptPath = Path.Combine(Path.GetDirectoryName(inputPath)!, "evidence-party-publication.json");
            using var stream = File.OpenRead(receiptPath);
            long length = stream.Length;
            if (length is < 1 or > PublishedPartyReplayMaxBytes)
                throw new InvalidOperationException("Published party view receipt exceeds the isolated bound.");
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidOperationException("Published party view receipt changed while reading.");
            return bytes;
        }

        private static PublishedPartyReplayView ReadPublishedPartyReplayView(byte[] bytes, Dictionary<string, object> item,
            List<Dictionary<string, object>> sources, string campaign, string timeline, Func<string, string> map)
        {
            if (bytes == null || bytes.Length is < 1 or > PublishedPartyReplayMaxBytes
                || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(ReadString(item, "publishedPartyViewSha256", ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Published party view receipt does not match its pinned bytes.");
            var receipt = TryParseJsonObject(new UTF8Encoding(false, true).GetString(bytes));
            var headerKeys = new[] { "schema", "caseId", "observerId", "peerId", "sessionId", "asOfSequence", "asOfUtcSeconds",
                "worldDay", "querySha256", "archiveReceiptSha256", "audience", "recentSourceIds", "rows" };
            var writer = ReadDictionary(item, "writerReplay");
            var response = ReadDictionary(writer, "response");
            string observer = ReadString(item, "observerId", ""), peer = ReadString(item, "peerId", ""), query = ReadString(item, "query", "");
            long cutoff = ReadLong(item, "asOfSequence", -1), time = ReadLong(item, "asOfUtcSeconds", -1);
            double day = ReadDouble(item, "worldDay", double.NaN);
            string session = ReadString(receipt, "sessionId", ""), archive = ReadString(receipt, "archiveReceiptSha256", "");
            var audience = ReadStringList(receipt, "audience");
            var recentIds = ReadStringList(receipt, "recentSourceIds");
            if (receipt == null || !headerKeys.ToHashSet(StringComparer.Ordinal).SetEquals(receipt.Keys)
                || ReadString(receipt, "schema", "") != PublishedPartyReplaySchema || ReadString(item, "route", "") != "party_chat"
                || writer == null || writer.ContainsKey("continuation") || writer.ContainsKey("repairResponses")
                || (response != null && !ReplaySha256(ReadString(response, "requestSha256", "")))
                || cutoff < 1 || time < 1 || !double.IsFinite(day) || session.Length == 0 || !ReplaySha256(archive)
                || ReadString(receipt, "caseId", "") != ReadString(item, "caseId", "") || ReadString(receipt, "observerId", "") != observer
                || ReadString(receipt, "peerId", "") != peer || ReadLong(receipt, "asOfSequence", -1) != cutoff
                || ReadLong(receipt, "asOfUtcSeconds", -1) != time || ReadDouble(receipt, "worldDay", double.NaN) != day
                || ReadLong(writer, "snapshotAsOfSequence", -1) != cutoff || ReadString(receipt, "querySha256", "") != ConversationExactTextHash(query)
                || audience.Count is < 2 or > 24 || audience.Any(string.IsNullOrWhiteSpace) || !audience.Contains(observer) || !audience.Contains(peer)
                || audience.Distinct(StringComparer.OrdinalIgnoreCase).Count() != audience.Count || recentIds.Count != 29
                || !recentIds.SequenceEqual(ReadStringList(item, "recentSourceIds"))
                || !recentIds.SequenceEqual(sources.TakeLast(29).Select(s => ReadString(s, "sourceId", "")))
                || !IsConversationObjectArray(receipt.GetValueOrDefault("rows")))
                throw new InvalidOperationException("Published party view does not match the isolated case, cutoff or audience.");

            var pins = ReadDictionaryList(receipt, "rows");
            var pinKeys = new HashSet<string>(StringComparer.Ordinal) { "sourceId", "publicationId", "exchangeId", "speakerId", "speakerName",
                "role", "sequence", "utcSeconds", "worldDay", "bodySha256" };
            if (pins.Count != sources.Count || pins.Select(pin => ReadString(pin, "publicationId", "")).Distinct(StringComparer.Ordinal).Count() != pins.Count)
                throw new InvalidOperationException("Published party view rows do not match the isolated source inventory.");
            var shared = audience.ToHashSet(StringComparer.Ordinal);
            var rows = new List<PublishedPartyReplayRow>(pins.Count);
            long previous = -1;
            for (int i = 0; i < pins.Count; i++)
            {
                var pin = pins[i]; var source = sources[i];
                string id = ReadString(source, "sourceId", ""), exchange = ReadString(source, "exchangeId", "");
                string speaker = ReadString(source, "speakerId", ""), name = ReadString(source, "speakerName", "");
                string role = ReadString(source, "role", ""), body = ReadString(source, "text", "");
                long seq = ReadLong(source, "sequence", -1), utc = ReadLong(source, "utcSeconds", -1);
                double rowDay = ReadDouble(source, "worldDay", double.NaN);
                if (!pinKeys.SetEquals(pin.Keys) || ReadString(pin, "publicationId", "").Length == 0
                    || ReadString(source, "channel", "") != "party_chat" || ReadString(source, "sessionId", "") != session
                    || seq <= previous || seq > cutoff || utc < 1 || utc > time || !double.IsFinite(rowDay) || rowDay > day
                    || exchange.Length == 0 || name.Length == 0 || !shared.Contains(speaker) || !shared.SetEquals(ReadStringList(source, "audience"))
                    || ReadString(pin, "sourceId", "") != id || ReadString(pin, "exchangeId", "") != exchange || ReadString(pin, "speakerId", "") != speaker
                    || ReadString(pin, "speakerName", "") != name || ReadString(pin, "role", "") != role || ReadLong(pin, "sequence", -1) != seq
                    || ReadLong(pin, "utcSeconds", -1) != utc || ReadDouble(pin, "worldDay", double.NaN) != rowDay
                    || ReadString(pin, "bodySha256", "") != ConversationExactTextHash(body))
                    throw new InvalidOperationException("Published party view row failed published identity, audience, time or exact bytes.");
                rows.Add(new PublishedPartyReplayRow(map(id), map(exchange), map(speaker), name, role, body, seq, rowDay));
                previous = seq;
            }
            return new PublishedPartyReplayView(campaign, map(observer), map(peer), timeline, map(session), query, cutoff, day,
                audience.Select(map).ToArray(), rows, recentIds.Select(map), ReadString(item, "publishedPartyViewSha256", ""), archive);
        }
    }
}
