using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string BanterRules = "Write a brief medieval party exchange. JSONL only, no fences: {\"speakerId\":\"supplied id\",\"text\":\"spoken words\",\"reactions\":[]}. All selected characters speak before anyone repeats; vary speakers. Start with a personal opinion or question, not a recital of the topic. Give even an abstract topic a concrete preference or practical point. Each reply picks up something the other actually said and adds a question, objection, admission or tease. Prefer 1-2 short sentences, sometimes just a few words; caps are ceilings, not targets. Vary rhythm and voice from the supplied personality; avoid chains of metaphors, maxims, polished speeches, repeated paraphrases and automatic agreement. Joke, argue, comfort, flirt or be cutting as personality and directed feelings support; liking, hostility and attraction need not be mutual. Rank affects confidence, deference and boundaries without constant titles. Usually omit direct address. Never say my lord, my lady, milord or milady. If needed use knownAs only when nameKnown; formalAddress is an occasional ruler address, not a line prefix. Friendship does not grant unknown names or secrets. Only supplied shared facts support past events; unsupplied details must stay preferences or hypotheticals. No narrator, player speech, tools, mechanical actions, violence, invented events, promises or possessions. Finish naturally within both caps. Optional reactions: {\"observer\":\"id\",\"target\":\"id\",\"source\":1,\"quote\":\"exact target words\",\"valence\":\"positive|negative\",\"tier\":\"routine|meaningful|hostile\",\"act\":\"kindness|humor|affection|insult|threat\",\"reason\":\"brief actual reaction\"}. source is the one-based earlier/current line spoken by target. Only a clear change in observer's feelings earns a reaction; neutral mention, ordinary disagreement and participation earn nothing. Never punish a kind target for observer's own hostility. At most one reaction per ordered NPC pair; no numeric affinity changes.";
        private const string BanterTopicSeeds = @"travel|the road's mud
travel|a stubborn headwind
travel|choosing a dry campsite
travel|the rhythm of marching
travel|getting lost without admitting it
camp|the last good meal
camp|who makes the worst stew
camp|sleeping beside snoring companions
camp|a favorite simple comfort
camp|the morning fire
skills|a skill worth learning
skills|horses and their tempers
skills|mending worn equipment
skills|the patience of craftspeople
skills|a trade admired or disdained
battle|courage versus recklessness
battle|what makes a reliable comrade
battle|preparing before a fight
battle|the value of retreat
battle|a soldier's superstition
duty|what a leader owes followers
duty|orders that are difficult to hear
duty|duty versus personal happiness
duty|keeping one's word
duty|who should take the night watch
status|what wealth can never buy
status|manners on the road
status|nobles and common comforts
status|whether pride needs a title
status|the cost of looking respectable
culture|a custom from home
culture|food that outsiders misunderstand
culture|a festival worth attending
culture|luck, fate and prayer
culture|a song from childhood
home|what makes a place feel like home
home|what children learn from adults
home|family expectations
home|a humble domestic ambition
home|what is missed while traveling
friendship|an irritating habit
friendship|trust earned in small ways
friendship|friendly competition
friendship|a compliment that is hard to accept
friendship|whether rivals can be friends
love|how one recognizes affection
love|a clumsy attempt at charm
love|jealousy without naming absent people
love|love versus social expectations
love|what makes someone attractive
ambition|an unlikely future comfort
ambition|fear of being forgotten
ambition|an ambition worth its cost
ambition|a fear one can admit
ambition|what success would feel like
events|the supplied shared encounter
events|what the witnessed words reveal about trust
events|a lesson from the supplied shared encounter
events|a disagreement about the supplied shared encounter
events|something amusing about the supplied shared encounter";

        private sealed class BanterJob { internal readonly CancellationTokenSource Cancellation = new(); }
        private static readonly ConcurrentDictionary<string, BanterJob> BanterJobs = new(StringComparer.Ordinal);
        // Injected only by isolated self-tests; production always uses the canonical provider adapter.
        private static Func<Dictionary<string, object>, Action<string>, Func<bool>, Dictionary<string, object>> BanterTestProvider;

        private static void EnsureBanterSchema(ReignDbConnection c) => ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS conversation_banter_episodes (
session_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,payload_json TEXT NOT NULL,updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_banter_timeline ON conversation_banter_episodes(timeline_id,updated_ts);");

        private static string BanterGeneration(ReignDbConnection c) => ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
        private static Dictionary<string, object> ReadBanter(ReignDbConnection c, string id) => TryParseJsonObject(ReadString(
            QuerySql(c, "SELECT payload_json FROM conversation_banter_episodes WHERE session_id=$id;", TestDict("id", id)).FirstOrDefault(), "payload_json", "{}")) ?? TestDict();
        private static void WriteBanter(ReignDbConnection c, Dictionary<string, object> e) => ExecuteSql(c, @"INSERT INTO conversation_banter_episodes(session_id,timeline_id,payload_json,updated_ts)
VALUES($id,$timeline,$payload,$ts) ON CONFLICT(session_id) DO UPDATE SET payload_json=EXCLUDED.payload_json,updated_ts=EXCLUDED.updated_ts;",
            TestDict("id", ReadString(e, "sessionId", ""), "timeline", ReadString(e, "timelineId", ""), "payload", Json.Serialize(e), "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        private static List<Dictionary<string, object>> RecentBanter(ReignDbConnection c, string timeline) => QuerySql(c,
            "SELECT payload_json FROM conversation_banter_episodes WHERE timeline_id=$timeline ORDER BY COALESCE((payload_json::jsonb->>'createdUtc')::bigint,updated_ts) DESC,updated_ts DESC LIMIT 64;", TestDict("timeline", timeline))
            .Select(row => TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? TestDict()).ToList();
        private static bool BanterActive(Dictionary<string, object> e) => new[] { "generating", "ready", "delivering" }.Contains(ReadString(e, "state", ""));
        private static string BanterJobKey(string campaign, string id) => campaign + ":" + id;
        private static void CancelBanterJob(string campaign, string id) { if (BanterJobs.TryGetValue(BanterJobKey(campaign, id), out var job)) { try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { } } }

        private static Dictionary<string, object> PartyBanterApi(string operation, Dictionary<string, object> p)
        {
            p ??= TestDict(); string campaign = ReadString(p, "campaignId", ""), timeline = ReadString(p, "timelineId", "");
            double day = ReadDouble(p, "worldDay", -1);
            if (campaign.Length == 0 || timeline.Length == 0 || !double.IsFinite(day) || day < 0) return TestDict("ok", false, "error", "Banter requires a current campaign, timeline and clock.");
            using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
            using var c = OpenCampaignConnection(campaign); EnsureBanterSchema(c);
            string generation = BanterGeneration(c);
            if (operation == "context")
            {
                var recent = RecentBanter(c, timeline);
                foreach (var restored in recent.Where(x => ReadString(x, "generation", "") != generation
                    || ReadString(x, "state", "") == "generating" && !BanterJobs.ContainsKey(BanterJobKey(campaign, ReadString(x, "sessionId", "")))))
                {
                    // Only an explicit fresh context request can adopt restored drafts.
                    // Old generation callbacks can never mutate this adopted state.
                    CancelBanterJob(campaign, ReadString(restored, "sessionId", ""));
                    restored["generation"] = generation; restored["revision"] = ReadInt(restored, "revision", 0) + 1;
                    restored["generationDone"] = true;
                    if (!BanterActive(restored)) WriteBanter(c, restored);
                    else if (ReadDictionaryList(restored, "drafts").Count > 0) { restored["state"] = "ready"; WriteBanter(c, restored); }
                    else FinishBanter(c, campaign, restored, day, "generation_interrupted", p);
                }
                var active = recent.FirstOrDefault(BanterActive);
                var latest = active ?? recent.FirstOrDefault();
                return TestDict("ok", true, "generation", generation, "options", BanterOptions(LoadSettings()), "episode", latest == null ? null : BanterPublic(campaign, latest),
                    "endedDay", recent.Where(x => !BanterActive(x)).Select(x => ReadDouble(x, "endedDay", -1)).DefaultIfEmpty(-1).Max());
            }
            if (ReadString(p, "generation", "") != generation) return TestDict("ok", false, "errorCode", "banter_stale_generation");
            if (operation == "start") return StartBanter(c, campaign, timeline, generation, p);
            string id = ReadString(p, "sessionId", ""); var e = ReadBanter(c, id);
            if (e.Count == 0 || ReadString(e, "timelineId", "") != timeline || ReadString(e, "generation", "") != generation)
                return TestDict("ok", false, "errorCode", "banter_missing_or_stale");
            if (operation == "status")
            {
                if (BanterActive(e) && !ReadBool(LoadSettings(), "banterEnabled", true)) FinishBanter(c, campaign, e, day, "disabled", p);
                return BanterPublic(campaign, e);
            }
            if (operation == "accept") return AcceptBanter(c, campaign, e, p);
            if (operation == "finish" || operation == "handoff")
            {
                bool exitCleanup = operation == "finish" && ReadString(p, "reason", "") == "campaign_exit";
                if (!exitCleanup && ReadInt(p, "expectedRevision", -1) != ReadInt(e, "revision", 0)) return TestDict("ok", false, "errorCode", "banter_revision", "episode", BanterPublic(campaign, e));
                if (operation == "handoff") return HandoffBanter(c, campaign, e, p);
                FinishBanter(c, campaign, e, day, ReadString(p, "reason", "interrupted"), p);
                return BanterPublic(campaign, e);
            }
            return TestDict("ok", false, "error", "Unknown banter operation.");
        }

        private static List<Dictionary<string, object>> BanterRoster(Dictionary<string, object> p)
        {
            string player = ReadString(p, "playerHeroStringId", ""), party = ReadString(p, "partyId", "");
            return ReadDictionaryList(p, "heroes").Where(h => CharacterIdFrom(h).Length > 0 && CharacterIdFrom(h) != player
                && !string.IsNullOrWhiteSpace(ReadString(h, "name", "")) && ReadBool(h, "isAlive", false)
                && !ReadBool(h, "isChild", false) && ReadDouble(h, "age", 0) >= 18 && !ReadBool(h, "isPrisoner", false)
                && party.Length > 0 && ReadFirstString(h, "currentPartyId", "partyId") == party)
                .GroupBy(CharacterIdFrom).Select(g => g.First()).Take(64).ToList();
        }

        private static Dictionary<string, object> StartBanter(ReignDbConnection c, string campaign, string timeline, string generation, Dictionary<string, object> p)
        {
            var options = BanterOptions(LoadSettings()); double day = ReadDouble(p, "worldDay", 0);
            var recent = RecentBanter(c, timeline); string requestedId = ReadString(p, "sessionId", "");
            var replay = recent.FirstOrDefault(x => ReadString(x, "sessionId", "") == requestedId);
            if (replay != null) return BanterPublic(campaign, replay);
            var active = recent.FirstOrDefault(BanterActive); if (active != null) return BanterPublic(campaign, active);
            double ended = recent.Select(x => ReadDouble(x, "endedDay", -1)).DefaultIfEmpty(-1).Max();
            var roster = BanterRoster(p);
            long retryAfter = recent.Where(x => ReadString(x, "state", "") == "failed").Select(x => ReadLong(x, "retryAfterUtc", 0)).DefaultIfEmpty(0).Max();
            if (!PartyBanterPolicy.CanStart(ReadBool(options, "enabled", true), ReadBool(p, "traveling", false), false, ReadBool(p, "chatOpen", false),
                roster.Count, day, ended, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), retryAfter)) return TestDict("ok", true, "eligible", false, "options", options);
            // Fairness is the first key; affinity strength/asymmetry breaks ties.
            int Used(string id) => recent.Count(x => ReadStringList(x, "heroIds").Contains(id));
            var candidates = roster.Where(h => ReadString(ReadJsonObject(CharacterFile(campaign, CharacterIdFrom(h), "traits.json")), "basePersonalitySummary", "").Length >= PersonalitySummaryMinimumCharacters)
                .OrderBy(h => Used(CharacterIdFrom(h))).ThenBy(h => PromptHash(requestedId + CharacterIdFrom(h))).ToList();
            if (candidates.Count < 2) return TestDict("ok", true, "eligible", false, "reason", "character_foundations_not_ready");
            var chosen = new List<Dictionary<string, object>> { candidates[0] };
            var first = candidates[0];
            var second = candidates.Skip(1).OrderBy(h => Used(CharacterIdFrom(h))).ThenByDescending(h => BanterPairWeight(c, first, h)).First(); chosen.Add(second);
            if (ReadInt(options, "maxResponses", 6) >= 3 && candidates.Count > 2 && (Convert.ToInt32(PromptHash(requestedId).Substring(0, 2), 16) % 3 == 0))
                chosen.Add(candidates.First(h => h != first && h != second));
            string topicsText = LoadPromptTemplate("party_banter_topics.txt"); if (string.IsNullOrWhiteSpace(topicsText)) topicsText = BanterTopicSeeds;
            var topics = topicsText.Split('\n').Select(x => x.Trim()).Where(x => x.Contains('|') && x.Length <= 160).Distinct().Take(500).ToList();
            var shared = BanterSharedExcerpt(c, chosen.Select(CharacterIdFrom).ToList(), p);
            var topicPool = topics.Where(x => shared.Count > 0 || !x.StartsWith("events|", StringComparison.Ordinal)).ToList();
            var lastTopics = recent.Take(8).Select(x => ReadString(x, "topic", "")).ToHashSet();
            var fresh = topicPool.Where(x => !lastTopics.Contains(x)).ToList(); if (fresh.Count > 0) topicPool = fresh;
            if (topicPool.Count == 0) topicPool = BanterTopicSeeds.Split('\n').Take(55).Select(x => x.Trim()).ToList();
            string topic = topicPool[Convert.ToInt32(PromptHash(requestedId).Substring(0, 6), 16) % topicPool.Count];
            string prompt = BuildBanterPrompt(c, campaign, chosen, topic, options, shared, p, includeOptional: true);
            if (prompt.Length + BanterRules.Length > 6000) prompt = BuildBanterPrompt(c, campaign, chosen, topic, options, new(), p, includeOptional: false);
            if (prompt.Length + BanterRules.Length > 8800 && chosen.Count > 2) { chosen.RemoveAt(2); prompt = BuildBanterPrompt(c, campaign, chosen, topic, options, new(), p, includeOptional: false); }
            if (prompt.Length + BanterRules.Length > 8800) return TestDict("ok", true, "eligible", false, "reason", "protected_prompt_floor_exceeds_budget");
            if (!Regex.IsMatch(requestedId, "^banter_[a-f0-9]{32}$")) return TestDict("ok", false, "error", "A stable banter request ID is required.");
            string player = ReadString(p, "playerHeroStringId", ""); var ids = chosen.Select(CharacterIdFrom).ToList();
            var start = ConversationStartApi(TestDict("campaignId", campaign, "timelineId", timeline, "sessionId", requestedId, "npcId", ids[0],
                "playerId", player, "participants", ids.Concat(new[] { player }).ToList(), "channel", "party_chat", "worldDay", day, "locationId", ReadString(p, "locationId", "")));
            if (!ReadBool(start, "ok", false)) return start;
            var e = TestDict("sessionId", requestedId, "timelineId", timeline, "generation", generation, "state", "generating", "revision", 1,
                "heroIds", ids, "heroes", chosen, "playerHeroStringId", player, "playerName", ReadString(p, "playerName", "Player"), "options", options,
                "topic", topic, "drafts", new List<Dictionary<string, object>>(), "accepted", 0, "generationDone", false, "startedDay", day, "endedDay", -1d,
                "createdUtc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "relationshipPairs", ReadDictionaryList(p, "relationshipPairs"), "locationId", ReadString(p, "locationId", ""));
            WriteBanter(c, e);
            var job = new BanterJob(); BanterJobs[BanterJobKey(campaign, requestedId)] = job;
            _ = Task.Run(() => GenerateBanter(campaign, e, prompt, job));
            return BanterPublic(campaign, e);
        }

        private static int BanterPairWeight(ReignDbConnection c, Dictionary<string, object> a, Dictionary<string, object> b)
        {
            EnsureMbtiRelationshipSchema(c);
            var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", AmbientPairKey(CharacterIdFrom(a), CharacterIdFrom(b)))).FirstOrDefault();
            int ab = ReadInt(pair, "affinity_a_to_b", 0), ba = ReadInt(pair, "affinity_b_to_a", 0);
            return Math.Abs(ab - ba) + Math.Max(Math.Abs(ab), Math.Abs(ba));
        }
        private static List<Dictionary<string, object>> BanterSharedExcerpt(ReignDbConnection c, List<string> ids, Dictionary<string, object> p)
        {
            if (ids.Count < 2) return new();
            var access = ids.Select(id => BuildKnowledgeAccessContext(id, ReadString(p, "playerHeroStringId", ""), "", p)).ToList();
            var first = access[0];
            var rows = QuerySql(c, @"SELECT t.* FROM conversation_turns t
WHERE t.participants_json::jsonb @> $audience::jsonb AND t.role='npc' AND t.status='active' AND length(t.text)<=400
AND t.world_day<=$day AND t.accepted_sequence<=$sequence AND t.ts<=$timestamp AND " + MemorySourceTimelineSql("conversation_turns", "t", access: first) + @"
ORDER BY t.accepted_sequence DESC,t.ts DESC,t.turn_order DESC LIMIT 16;", TestDict("audience", Json.Serialize(ids),
                "timeline", first.TimelineId, "day", first.WorldDay, "sequence", first.AsOfAcceptedSequence, "timestamp", first.AsOfTimestamp));
            return rows.Where(row => access.All(a => PrecisionTurnVisible(row, a.NpcId, a, c))).Take(2)
                .Select(x => TestDict("sourceId", ReadString(x, "turn_id", ""), "speakerId", ReadString(x, "speaker_id", ""), "quote", ConversationTurnTextForRecall(x))).ToList();
        }
        private static string BuildBanterPrompt(ReignDbConnection c, string campaign, List<Dictionary<string, object>> heroes, string topic,
            Dictionary<string, object> options, List<Dictionary<string, object>> shared, Dictionary<string, object> p, bool includeOptional)
        {
            EnsureIdentitySchema(c); EnsureMbtiRelationshipSchema(c);
            var b = new StringBuilder(); b.AppendLine("Topic: " + (topic.StartsWith("events|", StringComparison.Ordinal) && shared.Count == 0 ? "the road's weather" : topic.Split('|').Last()));
            b.AppendLine("Maximum " + ReadInt(options, "maxResponses", 6) + " individual spoken responses; " + ReadInt(options, "maxCharacters", 360) + " characters each. Player is listening silently.");
            b.AppendLine("Current commitments remain active unless explicitly fulfilled, withdrawn or superseded. This joint prompt includes only commitment details known to every speaking character. Excluded private details do not mean a person has no commitments. Do not invent new promises or expose another person's private obligations.");
            foreach (var h in heroes)
            {
                string id = CharacterIdFrom(h); var traits = ReadJsonObject(CharacterFile(campaign, id, "traits.json"));
                var identity = TestDict("id", id, "name", ReadString(h, "name", ""), "pronouns", ReadBool(h, "isFemale", false) ? "she/her" : "he/him",
                    "rank", ReadBool(h, "isRuler", false) ? (ReadBool(h, "isFemale", false) ? "queen" : "king") : ReadBool(h, "isLord", false) ? "noble" : "commoner",
                    "culture", ReadString(h, "cultureId", ""), "clan", ReadString(h, "clanName", ""), "kingdom", ReadString(h, "kingdomName", ""),
                    "occupation", ReadString(h, "occupation", ""), "partyRole", ReadString(h, "banterPartyRole", "fellow traveler"));
                b.AppendLine(Json.Serialize(identity));
                b.AppendLine(ReadString(traits, "basePersonalitySummary", "")); // Protected complete core, never sliced.
                var voice = ReadDictionary(ReadJsonObject(CharacterFile(campaign, id, "narrative.json")), "voice") ?? ReadJsonObject(CharacterFile(campaign, id, "voice.json"));
                b.AppendLine("Voice: " + LimitText(ReadString(voice, "speechStyle", ""), 140));
                var persistentSources = new List<string>();
                var otherObservers = heroes.Where(other => other != h).Select(CharacterIdFrom).ToList();
                var persistentPayload = CloneDictionary(p);
                persistentPayload["memoryActiveTopic"] = topic;
                b.AppendLine(BuildCharacterCommitmentPrompt(c, id, ReadString(p, "playerHeroStringId", ""), persistentPayload, persistentSources, otherObservers));
                b.AppendLine(BuildPlayerPerformancePrompt(c, id, persistentPayload, persistentSources, otherObservers));
                foreach (var other in heroes.Where(x => x != h))
                {
                    string target = CharacterIdFrom(other); var acquaintance = ReadAcquaintance(c, id, target);
                    var view = BuildIdentityView(acquaintance, TestDict("mode", "party_chat", "canonicalName", ReadString(other, "name", ""), "subject", other, "appearance", TestDict()));
                    var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", AmbientPairKey(id, target))).FirstOrDefault();
                    int native = ReadInt(FindRelationshipPair(ReadDictionaryList(p, "relationshipPairs"), id, target), "nativeRelation", 0);
                    int affinity = pair == null ? native : EffectiveDirectionalAffinity(pair, ReadString(pair, "hero_a_id", "") == id, native);
                    bool known = IdentityStateVerified(acquaintance), ruler = ReadBool(other, "isRuler", false), noble = ReadBool(other, "isLord", false);
                    string formalAddress = ruler ? string.Join("/", SovereignFormalAddresses(other)) : "";
                    string authority = ruler ? (ReadString(h, "sovereignHeroStringId", "") == target ? "own sovereign" : "foreign sovereign")
                        : noble && !ReadBool(h, "isLord", false) ? "social superior" : noble ? "noble peer" : "commoner";
                    b.AppendLine(Json.Serialize(TestDict("toward", target, "knownAs", ReadString(view, "usableName", "the other traveler"), "nameKnown", known,
                        "authority", authority, "address", "usually omit", "formalAddress", formalAddress, "affinity", affinity, "feeling", RelationshipBand(Clamp(affinity, -100, 100)),
                        "tags", ReadStringList(RelationshipLifecycleView(c, id, target), "tags").Where(IsPromptedRelationshipLifecycleTag).ToList())));
                }
                if (includeOptional)
                {
                    var relevant = ReadDictionary(traits, "traits") ?? traits;
                    b.AppendLine("Relevant conduct: " + string.Join(", ", new[] { "tact", "sociability", "honor" }.Where(relevant.ContainsKey).Select(k => k + "=" + Convert.ToString(relevant[k], CultureInfo.InvariantCulture))));
                }
            }
            if (shared.Count > 0) b.AppendLine("Shared witnessed sources: " + Json.Serialize(shared));
            return b.ToString();
        }

        private static void GenerateBanter(string campaign, Dictionary<string, object> episode, string prompt, BanterJob job)
        {
            string id = ReadString(episode, "sessionId", ""), generation = ReadString(episode, "generation", "");
            var buffered = new StringBuilder(); int parsedLines = 0;
            void Fragment(string fragment)
            {
                buffered.Append(fragment); if (buffered.Length > 200000) throw new InvalidOperationException("Banter output exceeded its bound.");
                string content = buffered.ToString(); int last = content.LastIndexOf('\n'); if (last < 0) return;
                var rows = content.Substring(0, last).Split('\n');
                while (parsedLines < rows.Length) { AppendBanterDraft(campaign, id, generation, rows[parsedLines++], job); }
            }
            try
            {
                var options = ReadDictionary(episode, "options");
                var request = TestDict("campaignId", campaign, "timelineId", ReadString(episode, "timelineId", ""), "requestType", "party_banter", "eventId", id,
                    "correlationId", id, "singleProviderAttempt", true, "reasoningDisabled", true, "reasoningEffort", "none", "reasoningMode", "off", "temperature", .75d,
                    "maxTokens", PartyBanterPolicy.OutputTokens(ReadInt(options, "maxResponses", 6), ReadInt(options, "maxCharacters", 360)),
                    "messages", new[] { TestDict("role", "system", "content", BanterRules), TestDict("role", "user", "content", prompt) });
                // This background job holds no campaign lease during a provider wait.
                // Each streamed publication independently takes a short lease and a
                // restore-generation fence; Save Sync never waits for the network.
                var response = BanterTestProvider != null ? BanterTestProvider(request, Fragment, () => job.Cancellation.IsCancellationRequested)
                    : RunInteractiveRequest(() => ChatWithLlm(request, Fragment, () => job.Cancellation.IsCancellationRequested));
                if (!ReadBool(response, "ok", false)) throw new InvalidOperationException(ReadString(response, "error", "Banter generation failed."));
                string content = ReadString(response, "content", "");
                if (buffered.Length == 0) { buffered.Append(content); parsedLines = 0; }
                foreach (string row in buffered.ToString().Split('\n').Skip(parsedLines)) AppendBanterDraft(campaign, id, generation, row, job);
                UpdateGeneratedBanter(campaign, id, generation, job, "");
            }
            catch (Exception ex) { UpdateGeneratedBanter(campaign, id, generation, job, LimitText(ex.Message, 400)); }
            finally { BanterJobs.TryRemove(BanterJobKey(campaign, id), out _); job.Cancellation.Dispose(); }
        }
        private static void AppendBanterDraft(string campaign, string id, string generation, string json, BanterJob job)
        {
            if (job.Cancellation.IsCancellationRequested || string.IsNullOrWhiteSpace(json)) return;
            var access = EnterCampaignRequestAccess("/party-banter/draft");
            try
            {
                using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
                using var c = OpenCampaignConnection(campaign); var e = ReadBanter(c, id);
                if (!BanterActive(e) || BanterGeneration(c) != generation || ReadString(e, "generation", "") != generation) return;
                var drafts = ReadDictionaryList(e, "drafts"); var row = ValidateBanterDraft(json, ReadStringList(e, "heroIds"), ReadDictionary(e, "options"), drafts);
                if (row == null) throw new InvalidOperationException("Banter returned an invalid spoken line.");
                if (drafts.Count >= ReadInt(ReadDictionary(e, "options"), "maxResponses", 6)) return;
                drafts.Add(row); e["drafts"] = drafts;
                if (!e.ContainsKey("firstDraftMs")) e["firstDraftMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ReadLong(e, "createdUtc", 0);
                WriteBanter(c, e); // Draft arrival does not change the acceptance revision.
            }
            finally { ExitCampaignRequestAccess(access); }
        }
        private static Dictionary<string, object> ValidateBanterDraft(string json, List<string> ids, Dictionary<string, object> options, List<Dictionary<string, object>> prior)
        {
            var row = TryParseJsonObject(json.Trim()); if (row == null) return null;
            string speaker = ReadString(row, "speakerId", ""), text = ReadString(row, "text", "").Trim();
            if (!ids.Contains(speaker) || !PartyBanterPolicy.Fits(text, ReadInt(options, "maxCharacters", 360))
                || text.Contains('\n') || text.Contains('\r') || text.Contains('<') || text.Contains('*')
                || BanterUsesForbiddenHonorific(text)) return null;
            if (prior.Count > 0 && ReadString(prior.Last(), "speakerId", "") == speaker) return null;
            var spoken = prior.Select(x => ReadString(x, "speakerId", "")).Distinct().ToList();
            if (spoken.Count < ids.Count && spoken.Contains(speaker)) return null;
            return TestDict("speakerId", speaker, "text", text, "reactions", ReadDictionaryList(row, "reactions").Take(6).ToList());
        }
        private static bool BanterUsesForbiddenHonorific(string text) => Regex.IsMatch(text,
            @"\b(?:my\s+(?:lord|lady)|milord|milady)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static void UpdateGeneratedBanter(string campaign, string id, string generation, BanterJob job, string error)
        {
            if (job.Cancellation.IsCancellationRequested) return;
            var access = EnterCampaignRequestAccess("/party-banter/draft");
            try
            {
                using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)); using var c = OpenCampaignConnection(campaign); var e = ReadBanter(c, id);
                if (!BanterActive(e) || BanterGeneration(c) != generation || ReadString(e, "generation", "") != generation) return;
                e["generationDone"] = true; e["generationMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ReadLong(e, "createdUtc", 0); e["error"] = error;
                if (ReadDictionaryList(e, "drafts").Count == 0) FinishBanter(c, campaign, e, ReadDouble(e, "startedDay", 0), "generation_failed", TestDict());
                else if (ReadInt(e, "accepted", 0) == 0) e["state"] = "ready";
                WriteBanter(c, e);
                LogOperational("party_banter.generated", TestDict("campaignId", campaign, "sessionId", id, "firstDraftMs", e.GetValueOrDefault("firstDraftMs"), "generationMs", e["generationMs"], "draftCount", ReadDictionaryList(e, "drafts").Count, "error", error));
            }
            finally { ExitCampaignRequestAccess(access); }
        }

        private static Dictionary<string, object> AcceptBanter(ReignDbConnection c, string campaign, Dictionary<string, object> e, Dictionary<string, object> p)
        {
            int sequence = ReadInt(p, "sequence", 0), accepted = ReadInt(e, "accepted", 0);
            if (sequence <= accepted && sequence > 0) return BanterPublic(campaign, e); // transport retry after accepted source commit
            if (!BanterActive(e) || ReadInt(p, "expectedRevision", -1) != ReadInt(e, "revision", 0) || sequence != accepted + 1)
                return TestDict("ok", false, "errorCode", "banter_revision", "episode", BanterPublic(campaign, e));
            var available = BanterRoster(p).Select(CharacterIdFrom).ToHashSet();
            if (!ReadBool(LoadSettings(), "banterEnabled", true) || ReadStringList(e, "heroIds").Any(id => !available.Contains(id)))
            { FinishBanter(c, campaign, e, ReadDouble(p, "worldDay", 0), "interrupted", p); return BanterPublic(campaign, e); }
            var drafts = ReadDictionaryList(e, "drafts");
            if (accepted >= drafts.Count) return BanterPublic(campaign, e);
            var draft = drafts[accepted]; string hero = ReadString(draft, "speakerId", ""), text = ReadString(draft, "text", ""), id = ReadString(e, "sessionId", "");
            // Drafts generated by an older installed prompt may survive restart/restore.
            // Recheck only unspoken text; the accepted prefix remains immutable.
            if (BanterUsesForbiddenHonorific(text))
            {
                e["error"] = "Stored banter draft contains a disabled form of address.";
                FinishBanter(c, campaign, e, ReadDouble(p, "worldDay", 0), "obsolete_address_draft", p);
                return BanterPublic(campaign, e);
            }
            var profile = ReadDictionaryList(e, "heroes").First(h => CharacterIdFrom(h) == hero);
            string exchange = id + "_banter_" + sequence;
            var payload = TestDict("campaignId", campaign, "timelineId", ReadString(e, "timelineId", ""), "worldDay", ReadDouble(p, "worldDay", 0),
                "conversationSessionId", id, "sceneTurnId", exchange, "channel", "party_chat", "conversationMode", "party_banter",
                "participants", ReadStringList(e, "heroIds").Concat(new[] { ReadString(e, "playerHeroStringId", "") }).ToList(), "activeHeroIds", ReadStringList(e, "heroIds"),
                "playerHeroStringId", ReadString(e, "playerHeroStringId", ""), "locationId", ReadString(e, "locationId", ""));
            var unit = new AcceptedConversationUnit { Campaign = campaign, Hero = hero, Player = ReadString(e, "playerHeroStringId", ""),
                HeroName = ReadString(profile, "name", ""), PlayerName = ReadString(e, "playerName", "Player"), Location = ReadString(e, "locationId", ""),
                EventId = AcceptedConversationEventId(campaign, hero, payload), EventType = "party_chat_turn", PlayerText = "", Reply = text,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Generation = ReadString(e, "generation", ""), Payload = payload, Parsed = TestDict(), Motive = TestDict(),
                Profile = profile, Characteristics = TestDict(), CommitEffects = false, GroupConversation = true, Participants = ReadStringList(payload, "participants"),
                Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
            PublishAcceptedConversationUnit(unit, accept => StorePartyConversationExchange(campaign, payload, hero, unit.Player, unit.PlayerName,
                unit.HeroName, "", text, unit.EventId, unit.Timestamp, (connection, sources) =>
                {
                    var receipt = accept(connection, sources);
                    e["accepted"] = sequence; e["revision"] = ReadInt(e, "revision", 0) + 1; e["state"] = "delivering";
                    WriteBanter(connection, e); // Accepted index and canonical sources are one transaction.
                    return receipt;
                }));
            return BanterPublic(campaign, e);
        }
        private static Dictionary<string, object> BanterPublic(string campaign, Dictionary<string, object> e)
        {
            string id = ReadString(e, "sessionId", "");
            var lines = ReadCurrentSessionDialogueLines(campaign, id, 30);
            return TestDict("ok", true, "sessionId", id, "generation", ReadString(e, "generation", ""), "state", ReadString(e, "state", ""),
                "revision", ReadInt(e, "revision", 0), "heroIds", ReadStringList(e, "heroIds"), "accepted", ReadInt(e, "accepted", 0),
                "draftCount", ReadDictionaryList(e, "drafts").Count, "generationDone", ReadBool(e, "generationDone", false), "options", ReadDictionary(e, "options"),
                "transcript", lines, "endedDay", ReadDouble(e, "endedDay", -1), "handoffSessionId", ReadString(e, "handoffSessionId", ""),
                "relationshipEffects", ReadDictionary(e, "relationshipEffects"));
        }
        private static List<Dictionary<string, object>> BanterAssessments(Dictionary<string, object> e)
        {
            var drafts = ReadDictionaryList(e, "drafts").Take(ReadInt(e, "accepted", 0)).ToList(); var ids = ReadStringList(e, "heroIds");
            var result = new List<Dictionary<string, object>>(); string session = ReadString(e, "sessionId", "");
            for (int i = 0; i < drafts.Count; i++) foreach (var reaction in ReadDictionaryList(drafts[i], "reactions"))
            {
                string observer = ReadString(reaction, "observer", ""), target = ReadString(reaction, "target", ""), quote = ReadString(reaction, "quote", "");
                int source = ReadInt(reaction, "source", 0); string valence = ReadString(reaction, "valence", ""), tier = ReadString(reaction, "tier", "routine"), act = ReadString(reaction, "act", "");
                if (!ids.Contains(observer) || !ids.Contains(target) || observer == target || source < 1 || source > i + 1
                    || target != ReadString(drafts[source - 1], "speakerId", "") || quote.Length < 3
                    || !ReadString(drafts[source - 1], "text", "").Contains(quote, StringComparison.Ordinal)
                    || !new[] { "positive", "negative" }.Contains(valence) || !new[] { "routine", "meaningful", "hostile" }.Contains(tier)
                    || !new[] { "kindness", "humor", "affection", "insult", "threat" }.Contains(act)
                    || valence == "negative" && !new[] { "insult", "threat" }.Contains(act)) continue;
                result.Add(TestDict("observerHeroStringId", observer, "targetHeroStringId", target, "eligibleTargetIds", ids,
                    "valence", valence, "severityTier", tier, "actKind", act, "confidence", .8d, "importance", .5d,
                    "currentConductQuote", quote, "sourceText", ReadString(drafts[source - 1], "text", ""),
                    "sourceTurnIds", new[] { session + "_banter_" + source + "_npc_" + SafeMemoryKey(target) }, "summary", LimitText(ReadString(reaction, "reason", ""), 300)));
            }
            return result.GroupBy(x => ReadString(x, "observerHeroStringId", "") + "->" + ReadString(x, "targetHeroStringId", ""))
                .Select(g => SelectDominantConversationAssessment(g.ToList())).ToList();
        }
        private static void FinishBanter(ReignDbConnection c, string campaign, Dictionary<string, object> e, double day, string reason, Dictionary<string, object> p)
        {
            if (ReadBool(e, "effectsFinalized", false)) return;
            string id = ReadString(e, "sessionId", ""); CancelBanterJob(campaign, id);
            bool spoken = ReadInt(e, "accepted", 0) > 0;
            if (spoken)
            {
                var effects = ConversationRelationshipEvaluateApi(TestDict("campaignId", campaign, "timelineId", ReadString(e, "timelineId", ""), "exchangeId", id + "_relations",
                    "mode", "party_banter", "worldDay", day, "participants", ReadStringList(e, "heroIds"), "assessments", BanterAssessments(e),
                    "relationshipPairs", ReadDictionaryList(p, "relationshipPairs").Count > 0 ? ReadDictionaryList(p, "relationshipPairs") : ReadDictionaryList(e, "relationshipPairs"),
                    "nativeReceiptProtocol", "reign-native-relationship-receipt-v2"));
                if (!ReadBool(effects, "ok", false)) throw new InvalidOperationException("Banter relationship finalization failed.");
                e["relationshipEffects"] = effects;
            }
            e["state"] = spoken || reason == "takeover" ? "finished" : "failed"; e["endedDay"] = spoken || reason == "takeover" ? day : -1d; e["reason"] = reason;
            e["effectsFinalized"] = true; e["generationDone"] = true; e["revision"] = ReadInt(e, "revision", 0) + 1;
            if (!spoken) e["retryAfterUtc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
            e["drafts"] = ReadDictionaryList(e, "drafts").Take(ReadInt(e, "accepted", 0)).ToList();
            // Keep an explicit continuation-compatible close boundary. NPC-only
            // history remains party_chat and can be adopted without republishing.
            ExecuteSql(c, "UPDATE conversation_sessions SET status='closed',close_reason='ui_continuation',end_world_day=$day WHERE session_id=$id;", TestDict("id", id, "day", day));
            WriteBanter(c, e);
        }
        private static Dictionary<string, object> HandoffBanter(ReignDbConnection c, string campaign, Dictionary<string, object> e, Dictionary<string, object> p)
        {
            var present = BanterRoster(p).Select(CharacterIdFrom).ToHashSet(); bool unavailable = ReadStringList(e, "heroIds").Any(id => !present.Contains(id));
            bool wasActive = BanterActive(e); FinishBanter(c, campaign, e, ReadDouble(p, "worldDay", 0), "takeover", p);
            string id = ReadString(e, "sessionId", "");
            if (!unavailable && ReadString(e, "handoffSessionId", "").Length == 0)
            {
                string handoff = wasActive ? id : id + "_joined";
                if (wasActive) ExecuteSql(c, "UPDATE conversation_sessions SET status='open',close_reason='' WHERE session_id=$id;", TestDict("id", id));
                else
                {
                    var start = ConversationStartApi(TestDict("campaignId", campaign, "timelineId", ReadString(e, "timelineId", ""), "sessionId", handoff,
                        "npcId", ReadStringList(e, "heroIds").First(), "playerId", ReadString(e, "playerHeroStringId", ""), "channel", "party_chat",
                        "participants", ReadStringList(e, "heroIds").Concat(new[] { ReadString(e, "playerHeroStringId", "") }).ToList(),
                        "continuedFromSessionId", id, "continuationReason", "ui_continuation", "worldDay", ReadDouble(p, "worldDay", 0)));
                    if (!ReadBool(start, "ok", false)) return start;
                }
                e["handoffSessionId"] = handoff; WriteBanter(c, e);
            }
            string adopted = ReadString(e, "handoffSessionId", "");
            bool closed = adopted.Length > 0 && ReadString(QuerySql(c, "SELECT status FROM conversation_sessions WHERE session_id=$id;", TestDict("id", adopted)).FirstOrDefault(), "status", "closed") != "open";
            var result = BanterPublic(campaign, e); result["readOnly"] = unavailable || closed; return result;
        }
    }
}
