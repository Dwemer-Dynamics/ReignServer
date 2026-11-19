using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static HashSet<string> SearchMemoryFts(ReignDbConnection connection, List<string> terms, int limit, KnowledgeAccessContext knowledge = null) =>
            SearchEligibleMemoryFts(connection, "memories", "memory_id", "memory_fts", terms, limit, knowledge);

        internal static HashSet<string> SearchSummaryFts(ReignDbConnection connection, List<string> terms, int limit, KnowledgeAccessContext knowledge = null) =>
            SearchEligibleMemoryFts(connection, "summaries", "summary_id", "summary_fts", terms, limit, knowledge);

        private static HashSet<string> SearchEligibleMemoryFts(ReignDbConnection connection, string table, string key, string ftsTable,
            List<string> terms, int limit, KnowledgeAccessContext knowledge)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string query = BuildFtsQuery(terms);
            if (string.IsNullOrWhiteSpace(query)) return result;
            int offset = 0;
            do
            {
                var rows = DialogueStore.SelectDynamic10(connection, ftsTable, table, key, key, knowledge == null ? "" : " AND " + DialogueService.MemoryEligibilitySql(table, "m") + " AND " + DialogueService.MemorySourceTimelineSql(table, "m"), key,                    new Dictionary<string, object> { ["query"] = query, ["observer"] = knowledge?.NpcId ?? "", ["offset"] = offset, ["timeline"] = knowledge?.TimelineId ?? "" });
                foreach (var row in rows)
                {
                    if (knowledge != null && !KnowledgeRowVisibleToNpc(table, row, knowledge, connection)) continue;
                    result.Add(ReadString(row, key, ""));
                    if (result.Count >= Math.Max(1, limit)) return result;
                }
                if (rows.Count < 128) return result;
                offset += 128;
            } while (true);
        }

        private static double MemoryPacketScore(Dictionary<string, object> row, List<string> queryTerms, HashSet<string> ftsMemoryIds, string npcId, string playerId, string locationId)
        {
            double score = ReadDouble(row, "importance", 0.5d) * 20d + ReadDouble(row, "emotional_weight", 0d) * 8d;
            string memoryId = ReadString(row, "memory_id", "");
            if (!string.IsNullOrWhiteSpace(memoryId) && ftsMemoryIds.Contains(memoryId))
            {
                score += 20d;
            }

            string text = ReadString(row, "summary", "").ToLowerInvariant();
            foreach (string term in queryTerms ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(term) && text.Contains(term.ToLowerInvariant()))
                {
                    score += 4d;
                }
            }

            string blob = (ReadString(row, "participants_json", "") + " " + ReadString(row, "about_entities_json", "") + " " + ReadString(row, "known_by_json", "")).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(playerId) && blob.Contains(playerId.ToLowerInvariant()))
            {
                score += 12d;
            }

            if (!string.IsNullOrWhiteSpace(npcId) && blob.Contains(npcId.ToLowerInvariant()))
            {
                score += 8d;
            }

            if (!string.IsNullOrWhiteSpace(locationId) && string.Equals(ReadString(row, "location_id", ""), locationId, StringComparison.OrdinalIgnoreCase))
            {
                score += 8d;
            }

            long ageSeconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ReadLong(row, "ts", 0));
            score += Math.Max(0d, 8d - ageSeconds / 86400d);
            return score;
        }

        private static double SummaryPacketScore(Dictionary<string, object> row, List<string> queryTerms, HashSet<string> ftsSummaryIds, string npcId, string playerId, string locationId)
        {
            double score = ReadDouble(row, "importance", 0.5d) * 22d;
            string summaryId = ReadString(row, "summary_id", "");
            if (!string.IsNullOrWhiteSpace(summaryId) && ftsSummaryIds.Contains(summaryId))
            {
                score += 24d;
            }

            string text = ReadString(row, "summary", "").ToLowerInvariant();
            foreach (string term in queryTerms ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(term) && text.Contains(term.ToLowerInvariant()))
                {
                    score += 5d;
                }
            }

            string blob = (ReadString(row, "participants_json", "") + " " + ReadString(row, "about_entities_json", "")).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(playerId) && blob.Contains(playerId.ToLowerInvariant()))
            {
                score += 12d;
            }

            if (!string.IsNullOrWhiteSpace(npcId) && (blob.Contains(npcId.ToLowerInvariant()) || string.Equals(ReadString(row, "owner_id", ""), npcId, StringComparison.OrdinalIgnoreCase)))
            {
                score += 10d;
            }

            if (!string.IsNullOrWhiteSpace(locationId) && string.Equals(ReadString(row, "location_id", ""), locationId, StringComparison.OrdinalIgnoreCase))
            {
                score += 8d;
            }

            long ageSeconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ReadLong(row, "updated_ts", 0));
            score += Math.Max(0d, 6d - ageSeconds / 172800d);
            return score;
        }

        private static bool MemoryRowRelevant(Dictionary<string, object> row, List<string> queryTerms, HashSet<string> ftsMemoryIds)
        {
            if (queryTerms == null || queryTerms.Count == 0)
            {
                return true;
            }

            string memoryId = ReadString(row, "memory_id", "");
            if (!string.IsNullOrWhiteSpace(memoryId) && ftsMemoryIds.Contains(memoryId))
            {
                return true;
            }

            string blob = MemorySearchBlob(row);
            return queryTerms.Any(term => !string.IsNullOrWhiteSpace(term) && blob.Contains(term.ToLowerInvariant()))
                || ReadDouble(row, "importance", 0d) >= 0.9d;
        }

        private static bool SummaryRowRelevant(Dictionary<string, object> row, List<string> queryTerms, HashSet<string> ftsSummaryIds)
        {
            if (queryTerms == null || queryTerms.Count == 0)
            {
                return true;
            }

            string summaryId = ReadString(row, "summary_id", "");
            if (!string.IsNullOrWhiteSpace(summaryId) && ftsSummaryIds.Contains(summaryId))
            {
                return true;
            }

            string blob = MemorySearchBlob(row);
            return queryTerms.Any(term => !string.IsNullOrWhiteSpace(term) && blob.Contains(term.ToLowerInvariant()))
                || ReadDouble(row, "importance", 0d) >= 0.9d;
        }

        private static List<Dictionary<string, object>> FilterStructuredMemoryRows(List<Dictionary<string, object>> rows, List<string> queryTerms, int limit)
        {
            rows = rows ?? new List<Dictionary<string, object>>();
            if (queryTerms == null || queryTerms.Count == 0)
            {
                return rows.Take(Math.Max(1, limit)).ToList();
            }

            return rows
                .Select(row =>
                {
                    string blob = MemorySearchBlob(row);
                    int matches = queryTerms.Count(term => !string.IsNullOrWhiteSpace(term) && blob.Contains(term.ToLowerInvariant()));
                    row["relevanceScore"] = matches * 10d + ReadDouble(row, "confidence", 0.5d) * 2d
                        + Math.Max(0d, ReadDouble(row, "vectorSemanticScore", 0d)) * 30d;
                    return row;
                })
                .Where(row => ReadDouble(row, "relevanceScore", 0d) >= 10d || ReadDouble(row, "confidence", 0d) >= 0.9d
                    || ReadDouble(row, "vectorSemanticScore", 0d) > 0d)
                .OrderByDescending(row => ReadDouble(row, "relevanceScore", 0d))
                .ThenByDescending(row => ReadLong(row, "ts", 0))
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private static string MemorySearchBlob(Dictionary<string, object> row)
        {
            return (ReadFirstString(row, "summary", "claim", "text", "description")
                + " " + ReadString(row, "tags_json", "")
                + " " + ReadString(row, "about_entities_json", "")
                + " " + ReadString(row, "participants_json", "")
                + " " + ReadString(row, "payload_json", "")).ToLowerInvariant();
        }

        internal static bool IsLegacyPlayerNpcRoleCollision(Dictionary<string, object> row, string npcName, string playerName)
        {
            return IsLegacyPlayerNpcRoleCollision(row, new[] { npcName }, playerName);
        }

        internal static bool IsLegacyPlayerNpcRoleCollision(Dictionary<string, object> row, IEnumerable<string> npcNames, string playerName)
        {
            string text = ReadFirstString(row ?? new Dictionary<string, object>(), "summary", "claim", "text", "description");
            if (string.IsNullOrWhiteSpace(text)) return false;
            List<string> fullNames = (npcNames ?? new string[0])
                .Select(name => (name ?? string.Empty).Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)
                    && !CharactersService.NormalizeIdentityName(name).Equals(CharactersService.NormalizeIdentityName(playerName), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, int> firstNameCounts = fullNames.Select(WorldSimulationService.FirstName).Where(name => name.Length >= 3)
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            List<string> protectedNames = fullNames
                .Concat(fullNames.Select(WorldSimulationService.FirstName).Where(name => name.Length >= 3
                    && firstNameCounts.TryGetValue(name, out int count) && count == 1))
                .Where(name => !string.IsNullOrWhiteSpace(name) && name.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return protectedNames.Any(name =>
            {
                string n = Regex.Escape(name);
                return Regex.IsMatch(text,
                    @"\b(?:player|stranger)\s+(?:(?:is|was)\s+)?(?:named\s+|whose\s+name\s+(?:is|was)\s+|(?:claiming|claimed|calling|called)\s+(?:(?:himself|herself|themself)\s+)?(?:to\s+be\s+|as\s+)?)" + n + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            });
        }

        private static string MemoryPacketDedupeKey(Dictionary<string, object> row)
        {
            string eventId = ReadString(row, "event_id", "");
            string summary = ActionPipelineService.NormalizeLookup(ReadString(row, "summary", ""));
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                return eventId + "|" + summary;
            }

            return FirstNonEmpty(ReadString(row, "memory_id", ""), summary, Guid.NewGuid().ToString("N"));
        }

        internal static string BuildMemoryPacketText(
            string npcId,
            string playerId,
            List<Dictionary<string, object>> summaries,
            List<Dictionary<string, object>> memories,
            List<Dictionary<string, object>> relationships,
            List<Dictionary<string, object>> obligations,
            List<Dictionary<string, object>> beliefs,
            List<Dictionary<string, object>> comprehension,
            List<string> warnings,
            Dictionary<string, object> retrievalRoute,
            int tokenBudget, bool deferContinuityAllocation = false)
        {
            int charBudget = Math.Max(1200, tokenBudget * 4);
            StringBuilder header = new StringBuilder();
            header.AppendLine("NPC WORLD MEMORY PACKET");
            header.AppendLine("Knowledge rule: this packet is filtered for what this NPC can know, believe, witness, hear as rumor, or reasonably infer. Provenance labels are authoritative; preserve uncertainty and distinguish firsthand knowledge from reports and rumors.");
            header.AppendLine("Historical deictic rule: words inside a recalled source such as 'we', 'us', 'here', 'present', 'everyone', or 'all three' refer only to that source scene and may never be mapped onto the current group. Source-conversation participant metadata proves only who heard or spoke in the recording conversation; it does NOT prove that those people witnessed an older event described inside that conversation. An older event is firsthand for this NPC only when an explicit source-event witness link or this NPC's own attributed first-person line establishes it. Another NPC's 'we/all three' claim cannot grant firsthand knowledge or override this NPC's own denial. If the underlying witnesses are not identified, say they are unknown.");

            List<string> laneOrder = MemoryPacketLaneOrder(retrievalRoute);
            Dictionary<string, StringBuilder> laneBuilders = laneOrder.ToDictionary(lane => lane, lane => new StringBuilder(), StringComparer.OrdinalIgnoreCase);
            foreach (string lane in laneOrder)
            {
                laneBuilders[lane].AppendLine();
                laneBuilders[lane].AppendLine("MEMORY LANE: " + HumanizeKey(lane));
            }

            string interpersonalLane = laneOrder.Contains("interpersonal_history", StringComparer.OrdinalIgnoreCase)
                ? "interpersonal_history" : laneOrder[0];
            string commitmentLane = laneOrder.Contains("commitments_and_plots", StringComparer.OrdinalIgnoreCase)
                ? "commitments_and_plots" : laneOrder[0];
            string beliefLane = laneOrder.Contains("beliefs_and_rumors", StringComparer.OrdinalIgnoreCase)
                ? "beliefs_and_rumors" : laneOrder[0];
            string personalLane = laneOrder.Contains("personal_state", StringComparer.OrdinalIgnoreCase)
                ? "personal_state" : laneOrder[0];

            AppendMemoryPacketRows(laneBuilders[interpersonalLane], "Relationship state", relationships, row =>
            {
                string other = ReadString(row, "target_id", "");
                Dictionary<string, object> lifecycle = ReadDictionary(row, "lifecycle") ?? new Dictionary<string, object>();
                List<string> tags = ReadStringList(lifecycle, "tags");
                List<string> incidents = ReadDictionaryList(row, "incidents")
                    .Select(item => ReadString(item, "summary", "")).Where(value => !string.IsNullOrWhiteSpace(value)).Take(3).ToList();
                return other + ": native standing " + RelationshipDescriptor(ReadInt(row, "nativeRelation", 0))
                    + "; directional affinity " + ReadInt(row, "directionalAffinity", 0).ToString(CultureInfo.InvariantCulture)
                    + "; perception " + ReadString(row, "perceptionTag", "neutral")
                    + (tags.Count == 0 ? "" : "; tags: " + string.Join(", ", tags.ToArray()))
                    + (incidents.Count == 0 ? "" : "; known incidents: " + string.Join("; ", incidents.ToArray()));
            }, 8);

            AppendMemoryPacketRows(laneBuilders[commitmentLane], "Active plots and obligations", obligations,
                row => FormatMemoryPacketFact(row, ReadString(row, "description", ""), npcId, "obligation"), 8);
            AppendMemoryPacketRows(laneBuilders[personalLane], "Character comprehension", comprehension,
                row => FormatMemoryPacketFact(row, ReadString(row, "text", ""), npcId, "comprehension"), 6);
            AppendMemoryPacketRows(laneBuilders[beliefLane], "Beliefs", beliefs,
                row => FormatMemoryPacketFact(row, ReadString(row, "claim", ""), npcId, "belief"), 8);

            foreach (string lane in laneOrder)
            {
                List<Dictionary<string, object>> laneSummaries = summaries
                    .Where(row => EffectiveMemoryPacketLane(row, laneOrder) == lane).ToList();
                List<Dictionary<string, object>> laneMemories = memories
                    .Where(row => EffectiveMemoryPacketLane(row, laneOrder) == lane).ToList();
                AppendMemoryPacketRows(laneBuilders[lane], "Middle-term memory", laneSummaries,
                    row => FormatMemoryPacketFact(row, ReadString(row, "summary", ""), npcId, "summary"), 8);
                AppendMemoryPacketRows(laneBuilders[lane], "Relevant known events", laneMemories,
                    row => FormatMemoryPacketFact(row, ReadString(row, "summary", ""), npcId, "memory"), 16);
            }

            StringBuilder warningBuilder = new StringBuilder();
            if (warnings != null && warnings.Count > 0)
            {
                warningBuilder.AppendLine();
                warningBuilder.AppendLine("Knowledge warnings:");
                foreach (string warning in warnings.Take(5))
                {
                    warningBuilder.AppendLine("- " + warning);
                }
            }

            string warningText = warningBuilder.ToString();
            int laneCharBudget = Math.Max(400, charBudget - header.Length - warningText.Length);
            Dictionary<string, int> allocatedCharacters = AllocateMemoryLaneCharacters(
                laneOrder, laneBuilders.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                retrievalRoute, laneCharBudget);
            StringBuilder builder = new StringBuilder(header.ToString());
            foreach (string lane in laneOrder)
            {
                string block = laneBuilders[lane].ToString();
                int allocation = allocatedCharacters.ContainsKey(lane) ? allocatedCharacters[lane] : 0;
                if ((deferContinuityAllocation || allocation > 0) && !string.IsNullOrWhiteSpace(block))
                {
                    builder.Append(deferContinuityAllocation ? block : LimitMemoryPacketBlock(block, allocation));
                }
            }
            builder.Append(warningText);
            // Final prompt allocation selects complete evidence records. Cutting
            // this string here used to remove the source of retained conclusions.
            return builder.ToString().TrimEnd();
        }

        private static List<string> MemoryPacketLaneOrder(Dictionary<string, object> retrievalRoute)
        {
            List<string> lanes = ReadStringList(retrievalRoute, "selectedLanes")
                .Select(lane => string.Equals(lane, "exact_history", StringComparison.OrdinalIgnoreCase) ? "interpersonal_history" : DialogueService.NormalizeMemoryCategory(lane))
                .Where(lane => !string.IsNullOrWhiteSpace(lane))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (lanes.Count == 0)
            {
                lanes.Add("personal_state");
            }
            return lanes;
        }

        private static string EffectiveMemoryPacketLane(Dictionary<string, object> row, List<string> laneOrder)
        {
            string lane = DialogueService.NormalizeMemoryCategory(ReadFirstString(row, "memory_lane", "memory_domain"));
            if (string.IsNullOrWhiteSpace(lane))
            {
                lane = DialogueService.ClassifyMemoryDomain(row);
            }
            if (laneOrder.Contains(lane, StringComparer.OrdinalIgnoreCase))
            {
                return lane;
            }
            if (string.Equals(lane, "personal_state", StringComparison.OrdinalIgnoreCase)
                && laneOrder.Contains("interpersonal_history", StringComparer.OrdinalIgnoreCase))
            {
                return "interpersonal_history";
            }
            if (string.Equals(lane, "interpersonal_history", StringComparison.OrdinalIgnoreCase)
                && laneOrder.Contains("personal_state", StringComparer.OrdinalIgnoreCase))
            {
                return "personal_state";
            }
            return laneOrder[0];
        }

        internal static Dictionary<string, int> AllocateMemoryLaneCharacters(List<string> laneOrder,
            Dictionary<string, string> laneBlocks, Dictionary<string, object> retrievalRoute, int totalCharacters)
        {
            Dictionary<string, double> weights = laneOrder.ToDictionary(lane => lane, lane => 1d, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> configured = ReadDictionary(retrievalRoute, "tokenAllocationPercent") ?? new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in configured)
            {
                string lane = string.Equals(pair.Key, "exact_history", StringComparison.OrdinalIgnoreCase)
                    ? "interpersonal_history" : DialogueService.NormalizeMemoryCategory(pair.Key);
                if (!weights.ContainsKey(lane)) continue;
                double value;
                try { value = Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture); }
                catch { value = 0d; }
                weights[lane] += Math.Max(0d, value);
            }

            double totalWeight = Math.Max(1d, weights.Values.Sum());
            Dictionary<string, int> allocations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int assigned = 0;
            foreach (string lane in laneOrder)
            {
                int desired = (int)Math.Floor(totalCharacters * weights[lane] / totalWeight);
                int available = laneBlocks.ContainsKey(lane) ? laneBlocks[lane].Length : 0;
                allocations[lane] = Math.Min(available, Math.Max(0, desired));
                assigned += allocations[lane];
            }

            int remaining = Math.Max(0, totalCharacters - assigned);
            foreach (string lane in laneOrder)
            {
                if (remaining <= 0) break;
                int available = laneBlocks.ContainsKey(lane) ? laneBlocks[lane].Length : 0;
                int extra = Math.Min(remaining, Math.Max(0, available - allocations[lane]));
                allocations[lane] += extra;
                remaining -= extra;
            }
            return allocations;
        }

        private static string LimitMemoryPacketBlock(string block, int maximum)
        {
            if (string.IsNullOrEmpty(block) || maximum <= 0) return "";
            if (block.Length <= maximum) return block;
            StringBuilder builder = new StringBuilder();
            foreach (string line in block.Replace("\r\n", "\n").Split('\n'))
            {
                string addition = line + "\n";
                if (builder.Length + addition.Length > maximum) break;
                builder.Append(addition);
            }
            return builder.Length > 0 ? builder.ToString() : LimitText(block, maximum);
        }

        internal static string FormatMemoryPacketFact(Dictionary<string, object> row, string text, string npcId, string kind)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            row = row ?? new Dictionary<string, object>();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
            List<string> provenance = new List<string>();
            if (ReadString(payload, "sourceValidation", "") == "incomplete")
            {
                var originals = ReadDictionaryList(payload, "originalSources");
                if (originals.Count > 0)
                    text = "Summary incomplete; use the original attributed evidence instead:\n" + string.Join("\n", originals.Select(s =>
                        "source=" + ReadString(s, "turnId", "") + "; speaker=" + ReadString(s, "speakerId", "") + ": " + ReadString(s, "text", "")));
                else provenance.Add("incomplete source validation; do not treat summary as certain");
            }
            var sourceEvidence = ReadDictionary(payload, "sourceEvidence");
            if (sourceEvidence != null)
                provenance.Add("evidence=" + ReadString(sourceEvidence, "evidenceType", "unverified_summary") + "; speaker=" + ReadString(sourceEvidence, "speakerId", "unknown"));
            double worldDay = ReadDouble(row, "world_day", ReadDouble(row, "created_day", ReadDouble(payload, "worldDay", ReadDouble(payload, "world_day", 0d))));
            if (worldDay > 0d)
            {
                provenance.Add("day " + worldDay.ToString("0.##", CultureInfo.InvariantCulture));
            }
            provenance.Add(MemoryAcquisitionLabel(row, npcId, kind));
            if (row.ContainsKey("confidence"))
            {
                provenance.Add("confidence " + ClampDouble(ReadDouble(row, "confidence", 0.5d), 0d, 1d).ToString("0.00", CultureInfo.InvariantCulture));
            }
            if (row.ContainsKey("importance"))
            {
                provenance.Add("importance " + ClampDouble(ReadDouble(row, "importance", 0.5d), 0d, 1d).ToString("0.00", CultureInfo.InvariantCulture));
            }
            string source = ReadFirstString(row, "source");
            if (!string.IsNullOrWhiteSpace(source))
            {
                provenance.Add("source " + source.Replace('_', ' '));
            }
            List<string> sourceParticipants = TextListFromJson(ReadString(row, "participants_json", "[]"));
            if (sourceParticipants.Count == 0)
            {
                sourceParticipants = ReadStringList(payload, "participants")
                    .Concat(ReadStringList(payload, "participantIds"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (sourceParticipants.Count > 0)
            {
                provenance.Add("source-conversation participants (hearing only) " + string.Join(", ", sourceParticipants.Take(8).ToArray()));
            }
            string sourceSessionId = ReadFirstString(row, "session_id", "source_session_id");
            if (!string.IsNullOrWhiteSpace(sourceSessionId))
            {
                provenance.Add("historical session " + sourceSessionId);
            }
            string body = "[" + string.Join("; ", provenance.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()) + "] " + text.Trim();
            string sourceId = ReadFirstString(row, "memory_id", "summary_id", "belief_id", "comprehension_id", "obligation_id", "event_id");
            return DialogueService.RenderContinuityRecord(FirstNonEmpty(sourceId, DialogueService.PromptHash(body).Substring(0, 20)), kind,
                npcId, sourceSessionId, worldDay, kind == "obligation" ? 70 : kind == "memory" ? 60 : 40, false, body);
        }

        private static string MemoryAcquisitionLabel(Dictionary<string, object> row, string npcId, string kind)
        {
            if (string.Equals(kind, "belief", StringComparison.OrdinalIgnoreCase)) return "private belief";
            if (string.Equals(kind, "obligation", StringComparison.OrdinalIgnoreCase)) return "known commitment";
            if (string.Equals(kind, "comprehension", StringComparison.OrdinalIgnoreCase)) return "private interpretation";
            if (string.Equals(kind, "summary", StringComparison.OrdinalIgnoreCase)) return "consolidated personal memory";
            if (KnowledgeListContains(row, "witnesses_json", npcId)) return "firsthand witness";
            if (KnowledgeListContains(row, "participants_json", npcId)) return "firsthand participant";
            if (KnowledgeListContains(row, "heard_as_rumor_by_json", npcId)) return "secondhand rumor";
            if (KnowledgeIdEquals(ReadString(row, "owner_id", ""), npcId)) return "personal recollection";
            if (KnowledgeListContains(row, "known_by_json", npcId)) return "known report";
            string visibility = ReadString(row, "visibility", "");
            if (visibility.StartsWith("public", StringComparison.OrdinalIgnoreCase)
                || visibility.Equals("world", StringComparison.OrdinalIgnoreCase)
                || visibility.Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                return "public knowledge";
            }
            return "knowledge source unspecified";
        }

        private static void AppendMemoryPacketRows(StringBuilder builder, string title, List<Dictionary<string, object>> rows, Func<Dictionary<string, object>, string> formatter, int limit)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine(title + ":");
            foreach (Dictionary<string, object> row in rows.Take(Math.Max(1, limit)))
            {
                string text = formatter(row);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text.StartsWith("[[continuity ", StringComparison.Ordinal) ? text.Trim() : "- " + text.Trim());
                }
            }
        }

        internal static string BuildFtsQuery(List<string> terms)
        {
            List<string> clean = (terms ?? new List<string>())
                .Select(x => Regex.Replace(x ?? "", @"[^\p{L}\p{Nd}_-]+", " ").Trim())
                .Where(x => x.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .Select(x => "\"" + x.Replace("\"", "\"\"") + "\"")
                .ToList();
            return clean.Count == 0 ? "" : string.Join(" OR ", clean.ToArray());
        }

        internal static List<string> MemoryQueryTerms(string text)
        {
            var stop = new HashSet<string> { "the", "and", "you", "your", "for", "with", "that", "this", "from", "have", "will", "what", "when", "where", "then", "there", "just", "would", "could", "should", "them", "they", "been", "into", "about" };
            var terms = Regex.Split((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}_-]+")
                .Where(x => x.Length >= 3)
                .Where(x => !stop.Contains(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Traverse the whole message and reserve both ends. Named entities and
            // a substantive question at the end must survive a long opening action.
            var names = Regex.Matches(text ?? "", @"\b\p{Lu}[\p{L}'-]{2,}\b").Cast<Match>()
                .Select(m => m.Value.ToLowerInvariant()).Where(x => !stop.Contains(x)).Distinct().ToList();
            var titles = Regex.Matches(text ?? "", @"\b(?:khans?|kings?|queens?|emperors?|empress(?:es)?|sovereigns?|fathers?|mothers?|tournaments?)\b", RegexOptions.IgnoreCase)
                .Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            var selected = titles.Concat(names.Take(12)).Concat(terms.AsEnumerable().Reverse().Take(20)).Concat(terms.Take(20))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(48).ToList();
            // Preserve the original term too: a plural or title must not become a
            // hard lane barrier for the same topic's singular form.
            return selected.Concat(selected.Where(t => t.Length > 4 && t.EndsWith("s", StringComparison.Ordinal)
                && !t.EndsWith("ss", StringComparison.Ordinal)).Select(t => t.Substring(0, t.Length - 1)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
        }

        internal static string AppendMinimeTopics(string text, List<string> topics)
        {
            List<string> clean = NormalizeMinimeTopics(topics, 8);
            if (clean.Count == 0)
            {
                return text ?? "";
            }

            return (text ?? "") + "\nMinime topic hints: " + string.Join(", ", clean.ToArray());
        }

        internal static List<string> TryExtractMinimeTopics(Dictionary<string, object> settings, string text, int maxTopics)
        {
            int safeMaxTopics = Math.Max(1, Math.Min(12, maxTopics <= 0 ? 5 : maxTopics));
            // Topic hints are lexical routing metadata, not a semantic inference task.
            // Keeping this local prevents a worker round trip from serializing the foreground path.
            return DialogueService.BuildMemorySummaryTopics(new[] { TestDict("summary", text ?? "") }, safeMaxTopics);
        }

        private static Dictionary<string, object> MinimeRerankStatus(Dictionary<string, object> settings, string reason)
        {
            settings = settings ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["enabled"] = ReadBool(settings, "enableMinimeMemoryWorker", true) && ReadBool(settings, "enableMinimeMemoryReranking", true),
                ["attempted"] = false,
                ["applied"] = false,
                ["method"] = "deterministic",
                ["reason"] = reason ?? "",
                ["candidateCount"] = 0,
                ["scoredCount"] = 0,
                ["durationMs"] = 0
            };
        }

        private static IEnumerable<Dictionary<string, object>> OrderRerankedRows(IEnumerable<Dictionary<string, object>> rows, string timestampColumn)
        {
            return (rows ?? Enumerable.Empty<Dictionary<string, object>>())
                .OrderByDescending(row => ReadDouble(row, "finalPacketScore", ReadDouble(row, "packetScore", ReadDouble(row, "relevanceScore", 0d))))
                .ThenByDescending(row => ReadLong(row, timestampColumn, 0));
        }

        internal static string GetTextFromUrl(string url, int timeoutMs)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json, text/plain, */*";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        internal static string PostJsonToUrl(string url, string requestJson, int timeoutMs)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson ?? "{}");
            request.ContentLength = bytes.Length;
            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(bytes, 0, bytes.Length);
            }

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                string body = "";
                if (ex.Response != null)
                {
                    using (Stream responseStream = ex.Response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                }

                throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? ex.Message : body, ex);
            }
        }

        private static List<string> NormalizeMinimeTopics(IEnumerable<string> topics, int maxTopics)
        {
            List<string> results = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string topic in topics ?? Enumerable.Empty<string>())
            {
                string clean = Regex.Replace(topic ?? "", @"[^\p{L}\p{Nd}_\-\s]+", " ").Trim();
                clean = Regex.Replace(clean, @"\s+", " ");
                if (clean.Length < 2 || clean.Length > 80)
                {
                    continue;
                }

                string key = ActionPipelineService.NormalizeLookup(clean);
                if (string.IsNullOrWhiteSpace(key) || seen.Contains(key))
                {
                    continue;
                }

                seen.Add(key);
                results.Add(clean);
                if (results.Count >= Math.Max(1, maxTopics))
                {
                    break;
                }
            }

            return results;
        }

        private static List<string> ExtractEntityIds(object raw)
        {
            List<string> ids = new List<string>();
            foreach (object item in ActionPipelineService.ObjectsFromValue(raw))
            {
                if (item is Dictionary<string, object> dict)
                {
                    string id = ReadFirstString(dict, "entity_id", "entityId", "id", "heroStringId", "clanId", "kingdomId", "settlementId", "partyId", "armyId", "warId", "marriageId", "actionId", "courtEventId");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
                else
                {
                    string text = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        ids.Add(text.Trim());
                    }
                }
            }

            return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static List<string> ExtractEntityIdsFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                object parsed = Json.DeserializeObject(json);
                return ExtractEntityIds(parsed);
            }
            catch
            {
                return new List<string>();
            }
        }

        internal static List<string> TextListFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                object parsed = Json.DeserializeObject(json);
                return ActionPipelineService.ObjectsFromValue(parsed).Select(Convert.ToString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        internal static List<string> MergeStringLists(IEnumerable<string> left, IEnumerable<string> right)
        {
            return (left ?? new List<string>())
                .Concat(right ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsPrivateEventType(string eventType)
        {
            return ContainsAny(eventType ?? "", "private", "conversation", "trade", "bargain", "secret", "promise", "threat", "blackmail", "oath");
        }

        internal static double ClampDouble(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return min;
            }

            return Math.Max(min, Math.Min(max, value));
        }
    }
}
