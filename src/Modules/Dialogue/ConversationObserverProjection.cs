using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PrivateNarrativePattern = @"\b(?:remember\w*|recall\w*|recollect\w*|recognition|realiz\w*|realise\w*|knows?|knew|think\w*|thought\w*|mind|memor(?:y|ies)|privat\w*|secret\w*|unspoken|understood|misunderst\w*|wonder\w*|ponder\w*|assum\w*|suspect\w*|imagin\w*|dream\w*|notic\w*|intends?|intended|wishes?|wished|hopes?|hoped|feels?|felt)\b|\b(?:see|sees|saw)\s+that\b|\b(?:doesn['’]t|does not|didn['’]t|did not)\s+(?:name|expect|mean)|\b(?:meant|means?)\s+(?:more|only|nothing|something)\b|\b(?:conversation|night|moment)\s+(?:they|we)\s+(?:had|shared)\b";
        private const string ObservableActionPattern = @"\b(?:smiles?|smiled|nods?|nodded|glances?|glanced|looks?|looked|turns?|turned|holds?|held|offers?|offered|reaches?|reached|touch\w*|takes?|took|gives?|gave|puts?|placed|places?|sits?|sat|stands?|stood|steps?|stepped|walks?|walked|shifts?|shifted|straightens?|straightened|watches?|watched|studies|studied|brush\w*|tilts?|tilted|laugh\w*|sigh\w*|frowns?|frowned|raises?|raised|lowers?|lowered|drops?|dropped|moves?|moved|meets?|meeting|met|says?|said|asks?|asked|whispers?|whispered|voice|gaze|cheeks|brows?|mouth|eyes|hands?|fingers)\b";

        private static bool HasPrivateNarrativeMeaning(string text) => Regex.IsMatch(text ?? "", PrivateNarrativePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Open vocabulary: a present actor's unfamiliar gesture is not discarded
        // because a developer did not enumerate its verb. The semantic input
        // interpreter handles mixed clauses; this local path remains available
        // for historical projection and provider-independent recovery.
        private static bool HasObservableNarrationClause(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || HasPrivateNarrativeMeaning(text)) return false;
            if (Regex.IsMatch(text, @"^(?:I|he|she|we|they|the player|the speaker)\s+(?:will|shall|would|could|might|may|should|plan|intend|wish|hope)\b|\b(?:if|unless|used to|years? ago|days? ago)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            if (Regex.IsMatch(text, @"^(?:I|he|she|we|they)\s+(?:(?:did|do|does)\s+not\b|pretend\w*\s+to\s+(?:return|arrive|depart|leave)\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            return Regex.IsMatch(text, @"^(?:I|he|she|we|they|the player|the speaker)\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(text, ObservableActionPattern + "|" + AdditionalObservableActionPattern + "|" + ProgressiveObservableActionPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private const string AdditionalObservableActionPattern = @"\b(?:sips?|sipped|drinks?|drank|drinking|pulls?|pulled|swallows?|swallowed|refills?|refilled|pours?|poured|pass(?:es|ed)?|hands?|handed|kiss(?:es|ed)?|hugs?|hugged|embraces?|embraced|caress(?:es|ed)?|eats?|ate|chews?|chewed|leans?|leaned|rests?|rested|lays?|laid|lies|lying|closes?|closed|opens?|opened|draws?|drew|shakes?|shook|removes?|removed|unfastens?|unfastened|wraps?|wrapped|press(?:es|ed)?|knocks?|knocked|runs?|ran|kneels?|knelt|writes?|wrote|reads?|read|points?|pointed|waits?|waited|pauses?|paused|shrugs?|shrugged|laughs?|laughed|breathes?|breathed|blush(?:es|ed)?|flinch(?:es|ed)?|chuckles?|chuckled|giggles?|giggled|grins?|grinned|answers?|answered|replies|replied|reply)\b";
        private const string ProgressiveObservableActionPattern = @"\b(?:watch(?:es|ed|ing)?|walking|standing|sitting|smiling|nodding|glancing|looking|turning|holding|offering|reaching|taking|giving|putting|placing|stepping|shifting|straightening|studying|tilting|frowning|raising|lowering|dropping|moving|saying|asking|whispering|sipping|pulling|swallowing|refilling|pouring|passing|handing|kissing|hugging|embracing|caressing|eating|chewing|leaning|resting|closing|opening|drawing|shaking|removing|unfastening|wrapping|pressing|knocking|running|kneeling|writing|reading|pointing|waiting|pausing|shrugging|breathing|blushing|flinching|chuckling|giggling|grinning|answering|replying)\b";

        private static bool HasObservableMovementNarration(string text, out bool unperformedMovement)
        {
            // Preserve a directly narrated arrival/return, including its original
            // temporal qualification. This remains a source account, never a
            // native travel or clock receipt. Do not infer motion from a noun,
            // a private plan, a conditional or a recollection of an earlier trip.
            const string actorMovement = @"^(?:I|he|she|we|they|the player|the speaker)\s+(?:(?:am|is|are|was|were|have|has)\s+)?(?:returns?|returned|returning|arrives?|arrived|arriving|departs?|departed|departing|leaves?|left|leaving)\b";
            const string nonActual = @"\b(?:will|shall|would|could|might|may|should|if|unless|not|never|pretend\w*|rehears\w*|used\s+to|(?:years?|months?|seasons?|days?|nights?|hours?)\s+ago)\b|[\""“”]";
            bool movement = Regex.IsMatch(text ?? "", actorMovement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            unperformedMovement = movement && (Regex.IsMatch(text ?? "", nonActual, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || HasPrivateNarrativeMeaning(text));
            return movement && !unperformedMovement;
        }

        // Player narration is complete story context after explicit audience
        // scoping. NPC-authored private narration retains its observer boundary.
        private static Dictionary<string, object> ProjectConversationNarration(string source, string speakerId, string observerId, bool playerNarration = false)
        {
            source ??= "";
            if (playerNarration)
                return TestDict("schema", "reign-observer-projection-v3", "sourceSpeakerId", speakerId, "observerId", observerId,
                    "sourceHash", PromptHash(source), "visibleText", source, "sourceLength", source.Length, "sourcePreserved", true,
                    "spans", new[] { TestDict("start", 0, "length", source.Length, "kind", "player_narrative_context", "text", source) },
                    "coverageSpans", new[] { TestDict("start", 0, "length", source.Length, "disposition", "delivered", "kind", "player_narrative_context") },
                    "coverage", "complete_player_narration_not_native_receipt");
            speakerId ??= "";
            observerId ??= "";
            var spans = new List<Dictionary<string, object>>();
            var visible = new StringBuilder();
            bool own = speakerId.Length > 0 && string.Equals(speakerId, observerId, StringComparison.OrdinalIgnoreCase);
            int cursor = 0;
            void Keep(int start, int length, string kind, bool narrated)
            {
                if (length <= 0) return;
                string text = source.Substring(start, length);
                spans.Add(TestDict("start", start, "length", length, "kind", kind, "text", text));
                visible.Append(narrated ? "*" + text.Trim() + "*" : text);
            }
            foreach (Match action in Regex.Matches(source, @"\*([^*]+)\*", RegexOptions.Singleline))
            {
                Keep(cursor, action.Index - cursor, "spoken_statement", false);
                if (own) Keep(action.Groups[1].Index, action.Groups[1].Length, "author_narration", true);
                else
                {
                    foreach (Match sentence in Regex.Matches(action.Groups[1].Value, @"[^.!?]+(?:[.!?]+|$)", RegexOptions.Singleline))
                    {
                        string text = sentence.Value;
                        if (Regex.IsMatch(text.Trim(), @"^(?:The player whispers privately(?: to [^.]+)?|The speaker replies privately to the player|A private reply was exchanged)[.!]?$", RegexOptions.IgnoreCase))
                        { Keep(action.Groups[1].Index + sentence.Index, text.Length, "observable_narration", true); continue; }
                        var mental = Regex.Match(text, PrivateNarrativePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        int count = text.Length;
                        if (mental.Success)
                        {
                            // A recalled/fantasized action never becomes a new
                            // visible act merely by removing its qualification.
                            if (Regex.IsMatch(text, @"\b(?:imagin\w*|dream\w*|would|could|if|had\s+(?:once|previously))\b", RegexOptions.IgnoreCase)) continue;
                            var boundary = Regex.Matches(text.Substring(0, mental.Index), @"\s+[—–]\s+|[,;]|\b(?:as|while|because|when|the way|the kind)\b", RegexOptions.IgnoreCase)
                                .Cast<Match>().LastOrDefault();
                            if (boundary == null) continue;
                            count = boundary.Index;
                        }
                        string prefix = text.Substring(0, count).Trim();
                        bool movement = HasObservableMovementNarration(prefix, out bool unperformedMovement);
                        if (prefix.Length < 4 || HasPrivateNarrativeMeaning(prefix) || unperformedMovement
                            || !(HasObservableNarrationClause(prefix) || movement)) continue;
                        Keep(action.Groups[1].Index + sentence.Index, count, "observable_narration", true);
                    }
                }
                cursor = action.Index + action.Length;
            }
            int openTail = source.IndexOf('*', cursor);
            Keep(cursor, !own && openTail >= cursor ? openTail - cursor : source.Length - cursor, "spoken_statement", false);
            var accounting = new List<Dictionary<string, object>>();
            int accounted = 0;
            foreach (var span in spans.OrderBy(s => ReadInt(s, "start", 0)))
            {
                int start = ReadInt(span, "start", 0), length = ReadInt(span, "length", 0);
                if (start > accounted) accounting.Add(TestDict("start", accounted, "length", start - accounted,
                    "disposition", "format_private_or_unresolved", "reason", "not_observer_evidence"));
                accounting.Add(TestDict("start", start, "length", length, "disposition", "delivered",
                    "kind", ReadString(span, "kind", "")));
                accounted = start + length;
            }
            if (accounted < source.Length) accounting.Add(TestDict("start", accounted, "length", source.Length - accounted,
                "disposition", "format_private_or_unresolved", "reason", "not_observer_evidence"));
            return TestDict("schema", "reign-observer-projection-v2", "sourceSpeakerId", speakerId, "observerId", observerId,
                "sourceHash", PromptHash(source), "visibleText", own ? source : visible.ToString().Trim(), "spans", spans,
                "coverageSpans", accounting, "sourceLength", source.Length, "sourcePreserved", true,
                "coverage", own ? "author_source" : "spoken_and_locally_supported_observable_spans");
        }

        private static string ObserverConversationText(string text, string speaker, string observer, bool playerNarration = false) =>
            ReadString(ProjectConversationNarration(text, speaker ?? "", observer ?? "", playerNarration || speaker == "__player_source__"), "visibleText", "");

        // Only canonical accepted player rows carry this server-owned binding.
        // Callers must still enforce source audience, timeline and as-of eligibility.
        // Never apply this shortcut to client-supplied transcript dictionaries.
        private static bool HasValidatedPlayerSourceProjection(Dictionary<string, object> source)
        {
            var metadata = TryParseJsonObject(ReadString(source, "payload_json", "{}"));
            return ReadString(source, "status", "active") == "active" && ReadString(source, "role", "") == "player"
                && ReadInt(metadata, "playerNarrationProjectionVersion", 0) == 2
                && ReadString(metadata, "inputSourceId", "").Length > 0;
        }

        private static string ObserverConversationSourceText(Dictionary<string, object> source, string observer)
        {
            if (ReadString(source, "status", "active") != "active" || ReadString(source, "role", "") == "player_source") return "";
            string text = ConversationTurnTextForRecall(source);
            return ReadString(source, "role", "") == "player" || HasValidatedPlayerSourceProjection(source) ? text
                : ObserverConversationText(text, ReadString(source, "speaker_id", ""), observer);
        }

        private static List<Dictionary<string, object>> ProjectConversationLinesForObserver(
            List<Dictionary<string, object>> lines, string observer, Dictionary<string, object> payload)
        {
            var result = new List<Dictionary<string, object>>();
            var profiles = MergedInteractionParticipantProfiles(payload ?? TestDict());
            foreach (var line in lines ?? new List<Dictionary<string, object>>())
            {
                if (string.IsNullOrWhiteSpace(ConversationTurnTextForRecall(line))) continue;
                var copy = ProjectConversationTurnForRecall(line);
                string speaker = ReadFirstString(line, "speakerId", "speaker_id", "speakerHeroStringId", "heroStringId");
                if (speaker.Length == 0 && ReadString(line, "role", "") == "player")
                    speaker = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                if (speaker.Length == 0)
                {
                    string name = ReadFirstString(line, "speaker", "speakerName", "speaker_name", "name");
                    var matching = profiles.Where(p => ReadString(p, "name", "") == name).ToList();
                    if (matching.Count == 1) speaker = CharacterIdFrom(matching[0]);
                }
                string text = ReadString(copy, "text", "");
                string audience = ReadString(copy, "audienceHeroStringId", "");
                if (audience.Length > 0 && audience != observer && speaker != observer) text = "*A private reply was exchanged.*";
                else if (ReadString(copy, "role", "") == "player")
                    text = ProjectPlayerInputForAudience(text, observer, profiles, out _, out _);
                var projection = ProjectConversationNarration(text, speaker, observer, ReadString(copy, "role", "") == "player");
                copy["text"] = ReadString(projection, "visibleText", "");
                var metadata = TryParseJsonObject(ReadString(copy, "payload_json", "{}"));
                if (ReadString(ReadDictionary(copy, "conversationGrounding") ?? ReadDictionary(metadata, "conversationGrounding"), "status", "") == "unresolved"
                    && ReadString(copy, "role", "") == "npc")
                    copy["text"] = "[Unverified NPC statement; not independent factual evidence.] " + ReadString(copy, "text", "");
                copy["observerProjection"] = projection;
                result.Add(copy);
            }
            return result;
        }

        // Model-authored paraphrases may mix an observation and an internal
        // interpretation. Keep that complete interpretation with its author;
        // publish a separate exact observable account for the actual audience.
        private static List<Dictionary<string, object>> ScopeConversationMemoryWrites(
            List<Dictionary<string, object>> writes, string hero, string player, string playerText, string reply,
            string eventId, List<string> participants)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var original in writes ?? new List<Dictionary<string, object>>())
            {
                var copy = new Dictionary<string, object>(original, StringComparer.OrdinalIgnoreCase);
                copy["sourceParticipants"] = participants;
                copy["ownerId"] = hero; copy["owner_id"] = hero;
                copy["known_by"] = new[] { hero }; copy["knownBy"] = new[] { hero };
                copy["participants"] = new[] { hero }; copy["witnesses"] = new string[0];
                copy["hidden_from"] = participants.Where(id => id != hero).ToList();
                copy["heard_as_rumor_by"] = new string[0]; copy["heardAsRumorBy"] = new string[0];
                copy["visibility"] = "private"; copy["observerScope"] = "author_interpretation";
                result.Add(copy);
            }
            if (result.Count > 0)
            {
                string publicPlayer = playerText, publicNpc = ObserverConversationText(reply, hero, "");
                string account = (publicPlayer.Length > 0 ? "Player said or visibly did: " + publicPlayer + "\n" : "")
                    + (publicNpc.Length > 0 ? "Speaker " + hero + " said or visibly did: " + publicNpc : "");
                if (account.Trim().Length > 0) result.Add(TestDict("memoryId", "observed_" + PromptHash(eventId + "|" + hero).Substring(0, 28),
                    "text", account.Trim(), "ownerId", hero, "participants", participants, "known_by", participants,
                    "visibility", "private", "observerScope", "observable_source_spans", "evidenceType", "observation",
                    "sourceValidation", "supported_observable_spans", "observerProjection", TestDict(
                        "player", ProjectConversationNarration(playerText, player, "", true), "npc", ProjectConversationNarration(reply, hero, "")),
                    "importance", result.Max(r => MemoryImportance01(r, .55d))));
            }
            return result;
        }

        private static List<Dictionary<string, object>> ProjectRetrievedConversationMemories(ReignDbConnection connection,
            List<Dictionary<string, object>> rows, KnowledgeAccessContext knowledge)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var row in rows ?? new List<Dictionary<string, object>>())
            {
                if (ReadString(row, "owner_id", "") == knowledge.NpcId || !HasPrivateNarrativeMeaning(ReadString(row, "summary", "")))
                { result.Add(row); continue; }
                string eventId = ReadString(row, "event_id", "");
                var originals = eventId.Length == 0 ? new List<Dictionary<string, object>>() : QuerySql(connection,
                    "SELECT * FROM conversation_turns WHERE event_id=$event AND status='active' ORDER BY accepted_sequence;", TestDict("event", eventId))
                    .Where(r => PrecisionTurnVisible(r, knowledge.NpcId, knowledge, connection)).ToList();
                // A mixed-scope legacy paraphrase without an eligible original is
                // not a license to reveal another character's mental state.
                if (originals.Count == 0) continue;
                var copy = new Dictionary<string, object>(row);
                copy["summary"] = string.Join("\n", originals.Select(r => RenderExactHistoryTurn(r, knowledge)));
                copy["observerProjection"] = "original_observable_sources";
                result.Add(copy);
            }
            return result;
        }
    }
}
