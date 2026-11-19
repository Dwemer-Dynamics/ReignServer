using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static string ReplayReferenceGraphModeError(Dictionary<string, object> fixture) =>
            fixture?.TryGetValue("referenceGraphMode", out var value) == true
                && (value is not string mode || mode is not ("off" or "capture" or "enabled"))
                ? "invalid_replay_reference_graph_mode" : "";

        // A fixture option, never a production setting. Scope the entire case,
        // including retrieval, publication and continuations, and restore on throw.
        private sealed class EvidenceReplayReferenceModeScope : IDisposable
        {
            private readonly string previous = ConversationReferenceValidationMode.Value;
            public Dictionary<string, object> Audit { get; }

            public EvidenceReplayReferenceModeScope(Dictionary<string, object> fixture)
            {
                string error = ReplayReferenceGraphModeError(fixture);
                if (error.Length > 0) throw new InvalidOperationException(error);
                bool supplied = fixture?.ContainsKey("referenceGraphMode") == true;
                if (supplied)
                {
                    if (!ReignPostgreSqlStorage.Options.IsValidation || Program.ActiveServerPort > 0
                        || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                        throw new InvalidOperationException("Reference graph replay requires non-listening isolated validation.");
                    ConversationReferenceValidationMode.Value = ReadString(fixture, "referenceGraphMode", "");
                }
                try
                {
                    var settings = SettingsService.LoadSettings();
                    Audit = TestDict("mode", ConversationReferenceMode(settings), "source", supplied ? "fixture"
                        : previous != null ? "inherited_fixture" : settings.ContainsKey("memoryReferenceGraphMode") ? "settings" : "default",
                        "historyBackfilled", false, "scope", "isolated_fixture_not_native_campaign");
                    Audit["auditSha256"] = PromptHash(CanonicalJson(Audit));
                }
                catch { ConversationReferenceValidationMode.Value = previous; throw; }
            }

            public void Dispose() => ConversationReferenceValidationMode.Value = previous;
        }

        private static string ReplayGenerationSettingsError(Dictionary<string, object> owner)
        {
            if (!owner.ContainsKey("generationSettings")) return "";
            var generation = ReadDictionary(owner, "generationSettings");
            if (generation == null || generation.Keys.Any(k => !new[] { "reasoningMode", "reasoningEffort", "temperature" }.Contains(k))
                || generation.ContainsKey("reasoningMode") && !new[] { "off", "selective", "all" }.Contains(ReadString(generation, "reasoningMode", ""))
                || generation.ContainsKey("reasoningEffort") && !new[] { "minimal", "low", "medium", "high", "xhigh" }.Contains(ReadString(generation, "reasoningEffort", "")))
                return "invalid_replay_generation_settings";
            if (generation.TryGetValue("temperature", out var raw))
            {
                double temperature = ReadDouble(generation, "temperature", double.NaN);
                if (raw == null || raw is string or bool || !double.IsFinite(temperature) || temperature < 0 || temperature > 2)
                    return "invalid_replay_generation_settings";
            }
            return "";
        }

        private static void ApplyReplayGenerationSettings(Dictionary<string, object> owner, Dictionary<string, object> settings)
        {
            string error = ReplayGenerationSettingsError(owner);
            if (error.Length > 0) throw new InvalidOperationException(error);
            foreach (var setting in ReadDictionary(owner, "generationSettings") ?? TestDict()) settings[setting.Key] = setting.Value;
        }

        private static bool BoundedReplayNativeValue(object value, int depth, ref int entries)
        {
            if (depth > 16 || ++entries > 10000) return false;
            if (value is string text) return text.Length <= 24000;
            if (value is Dictionary<string, object> dictionary)
            {
                foreach (var pair in dictionary)
                    if (pair.Key.Length > 160 || !BoundedReplayNativeValue(pair.Value, depth + 1, ref entries)) return false;
                return true;
            }
            if (value is IEnumerable<object> list)
            {
                foreach (object child in list) if (!BoundedReplayNativeValue(child, depth + 1, ref entries)) return false;
                return true;
            }
            return value == null || value is bool or byte or short or int or long or decimal
                || value is double number && double.IsFinite(number);
        }

        private static string ReplayInitialNativeContextError(Dictionary<string, object> item, Dictionary<string, object> writer)
        {
            if (writer == null || !writer.ContainsKey("initialNativeContext")) return "";
            var native = ReadDictionary(writer, "initialNativeContext");
            int entries = 0;
            if (native == null || native.Count is < 1 or > 2
                || native.Keys.Any(k => k is not ("actionResolutionIndex" or "identityView"))
                || native.Keys.Any(k => ReadDictionary(native, k) == null)
                || !BoundedReplayNativeValue(native, 0, ref entries) || Json.Serialize(native).Length > 128000)
                return "invalid_initial_native_context";
            // Characterization only: no captured reply can be published and no
            // stale native snapshot can enter a repair or subsequent turn.
            if (ReadString(item, "route", "dialogue") != "dialogue"
                || new[] { "response", "repairResponses", "continuation", "nextPlanResponse", "nextAssemblyResponse", "nextReviewResponse" }.Any(writer.ContainsKey)
                || new[] { "publishedPartyViewSha256", "durableReadReplay", "reviewedFactReplay", "reviewedRecoveryReplay" }.Any(item.ContainsKey))
                return "initial_native_context_capture_only";
            return ReplayInitialPoliticalProfileError(item, writer);
        }

        private static string ReplayInitialPoliticalProfileError(Dictionary<string, object> item, Dictionary<string, object> writer)
        {
            var profile = ReadDictionary(writer, "profile");
            if (profile == null || !profile.ContainsKey("appliedPoliticalProfile")) return "";
            var applied = ReadDictionary(profile, "appliedPoliticalProfile");
            try
            {
                var value = applied == null ? null : System.Text.Json.JsonSerializer.Deserialize<GovernmentPoliticalProfile>(Json.Serialize(applied));
                if (!GovernmentPoliticalBeliefs.Valid(value) || value.Revision != 1 || value.Changes.Count != 0
                    || value.SeenEvidence.Count != 0 || value.IntervalBudgets == null || value.IntervalBudgets.Count != 0
                    || value.CampaignId != ReadString(profile, "campaignId", "")
                    || value.HeroId != ReadString(profile, "heroStringId", "") || value.HeroId != ReadString(item, "observerId", "")
                    || string.IsNullOrEmpty(value.TimelineId)
                    || value.TimelineId != ReadString(profile, "politicalTimelineId", "")
                    || CanonicalJson(applied) != CanonicalJson(TryParseJsonObject(System.Text.Json.JsonSerializer.Serialize(value))))
                    return "invalid_initial_native_political_profile";
                var characteristics = ReadDictionary(writer, "characteristics");
                if (characteristics?.ContainsKey("politicalProfile") == true
                    && CanonicalJson(ReadDictionary(characteristics, "politicalProfile")) != CanonicalJson(applied))
                    return "initial_native_political_snapshots_disagree";
                var content = VerificationService.CloneDictionary(applied);
                foreach (string key in new[] { "CampaignId", "HeroId", "TimelineId" }) content.Remove(key);
                // Mature or nested cross-world history needs its own receipt; this
                // initial characterization supports only an unchanged genesis.
                string serialized = CanonicalJson(content);
                if (new[] { value.CampaignId, value.HeroId, value.TimelineId }.Any(id => serialized.Contains(id, StringComparison.Ordinal)))
                    return "initial_native_political_nested_identity";
                return "";
            }
            catch (System.Text.Json.JsonException) { return "invalid_initial_native_political_profile"; }
        }

        private static Dictionary<string, object> BindInitialReplayPoliticalProfile(Dictionary<string, object> item,
            Dictionary<string, object> fixture, Dictionary<string, object> profile, Dictionary<string, object> characteristics,
            string campaign, string observer, string timeline)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || Program.ActiveServerPort > 0
                || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)
                || !Program.HasArg(Environment.GetCommandLineArgs(), "--run-verification")
                || campaign.Length != "evidence_replay_".Length + 32
                || !campaign.StartsWith("evidence_replay_", StringComparison.Ordinal)
                || !campaign.Skip("evidence_replay_".Length).All(Uri.IsHexDigit)
                || !timeline.StartsWith("replay_", StringComparison.Ordinal)
                || !int.TryParse(timeline.AsSpan("replay_".Length), out int scope) || scope < 0 || timeline != "replay_" + scope)
                throw new InvalidOperationException("Initial political binding requires isolated evidence replay.");
            string error = ReplayInitialNativeContextError(item, fixture);
            if (!fixture.ContainsKey("initialNativeContext") || error.Length > 0)
                throw new InvalidOperationException("Initial political binding is capture-only: " + error);
            var source = ReadDictionary(ReadDictionary(fixture, "profile"), "appliedPoliticalProfile");
            if (source == null) return null;
            if (campaign == ReadString(source, "CampaignId", "") || observer == ReadString(source, "HeroId", "")
                || timeline == ReadString(source, "TimelineId", ""))
                throw new InvalidOperationException("The replay political namespace must differ from the source.");
            var bound = VerificationService.CloneDictionary(source);
            bound["CampaignId"] = campaign; bound["HeroId"] = observer; bound["TimelineId"] = timeline;
            var value = System.Text.Json.JsonSerializer.Deserialize<GovernmentPoliticalProfile>(Json.Serialize(bound));
            string path = CampaignFileService.CharacterFile(campaign, observer, "politics", "revision-1-" + SafeMemoryKey(timeline) + ".json");
            lock (CharactersService.PoliticalProfileLock)
            {
                var existing = CampaignFileService.ReadJsonObject(path);
                if (existing.Count > 0 && CanonicalJson(existing) != CanonicalJson(bound))
                    throw new InvalidOperationException("The isolated political revision already has different content.");
                CharactersService.WritePoliticalRevision(campaign, observer, value);
                if (value.Revision != 1 || CanonicalJson(CampaignFileService.ReadJsonObject(path)) != CanonicalJson(bound))
                    throw new InvalidOperationException("The isolated political revision did not preserve its captured content.");
            }
            profile["appliedPoliticalProfile"] = VerificationService.CloneDictionary(bound);
            characteristics["politicalProfile"] = VerificationService.CloneDictionary(bound);
            return TestDict("status", "fixture_rebound_not_native_issued", "sourceSha256", PromptHash(CanonicalJson(source)),
                "boundSha256", PromptHash(CanonicalJson(bound)), "writtenSha256", PromptHash(CanonicalJson(CampaignFileService.ReadJsonObject(path))),
                "changedPaths", new[] { "CampaignId", "HeroId", "TimelineId" }, "revisionPreserved", true,
                "limits", "Only revision1 with empty change/evidence/interval history is supported. Values are preserved; this is an isolated namespace clone, not native issuance or restore acceptance.");
        }

        private static Dictionary<string, object> MapReplayInitialNativeContext(Dictionary<string, object> fixture,
            string originalObserver, string originalPeer, string observer, string peer)
        {
            var native = ReadDictionary(fixture, "initialNativeContext");
            if (native == null) return null;
            object MapValue(object value)
            {
                if (value is string text) return text == originalObserver ? observer : text == originalPeer ? peer : text;
                if (value is Dictionary<string, object> dictionary) return dictionary.ToDictionary(p => p.Key, p => MapValue(p.Value));
                if (value is IEnumerable<object> list) return list.Select(MapValue).ToList();
                return value;
            }
            return (Dictionary<string, object>)MapValue(native);
        }

        // Offline initial-body capture only. The provider, credentials, headers,
        // network, queue, retries and live cooldown state are outside this slice.
        // Use the production body/policy/cache/reasoning builders, not a second
        // JSON implementation in a private evaluation script.
        internal static Dictionary<string, object> CaptureMemoryReplayProviderRequest(Dictionary<string, object> request,
            Dictionary<string, object> fixtureSettings)
        {
            var captured = VerificationService.CloneDictionary(request);
            var payload = VerificationService.CloneDictionary(request);
            var settings = VerificationService.CloneDictionary(fixtureSettings);
            string purpose = ReadString(payload, "requestType", "dialogue");
            string model = ReadString(payload, "model", "");
            string error = "";
            if (LlmProviderService.IsRetiredMemoryRequest(purpose)) error = "memory_llm_stage_retired";
            if (model.Length == 0 && error.Length == 0) error = "missing_fixture_model";
            var wire = TestDict("schema", "reign-memory-provider-wire-capture-v1", "status", error.Length == 0 ? "captured" : "blocked",
                "requestType", purpose, "model", model, "provider", LlmProviderService.NanoGptProvider, "apiUrl", PortraitsService.NanoGptChatCompletionsUrl,
                "providerCalls", 0, "certificateEligible", false, "scope", "initial_json_shared_builders_nano_fixture",
                "excluded", new[] { "credentials_and_http_headers", "http_dispatch", "runtime_queue_and_cooldowns", "retries", "other_providers", "native_and_semantic_acceptance" });
            captured["providerWireCapture"] = wire;
            if (error.Length > 0) { wire["errorCode"] = error; return captured; }
            payload["model"] = model;
            var messages = ReadDictionaryList(payload, "messages");
            if (messages.Count == 0)
            {
                messages.Add(TestDict("role", "system", "content", ReadString(payload, "system", "You are Bannerlord Reign, a Bannerlord AI world assistant.")));
                messages.Add(TestDict("role", "user", "content", ReadString(payload, "prompt", ReadString(payload, "text", ""))));
            }
            if (EffectiveReasoningEffort(settings, payload, purpose) != "none") ApplyPrivateReasoningPolicyToMessages(messages);
            var body = LlmProviderService.BuildChatRequestBody(settings, payload, messages, model);
            ConfigurePromptCacheRouting(settings, payload, body, PortraitsService.NanoGptChatCompletionsUrl, model, purpose);
            wire["reasoningRouting"] = ConfigureReasoningRouting(settings, payload, body, PortraitsService.NanoGptChatCompletionsUrl, model, purpose);
            string json = Json.Serialize(body);
            wire["requestJson"] = json;
            wire["requestSha256"] = PromptHash(json);
            var continuityPreflight = ReadDictionary(ReadDictionary(payload, "promptEnvelope"), "continuityPreflight") != null
                || purpose.IndexOf("continuity_repair", StringComparison.OrdinalIgnoreCase) >= 0
                ? BuildContinuityProviderPreflight(settings, payload, body, PortraitsService.NanoGptChatCompletionsUrl, ReadString(body, "model", model)) : null;
            int hardCharacterLimit = ConversationPromptHardCharacterLimit(settings, continuityPreflight);
            bool fits = json.Length <= hardCharacterLimit && (continuityPreflight == null || ReadBool(continuityPreflight, "fits", false));
            wire["providerPreflight"] = TestDict("fits", fits, "requestCharacters", json.Length,
                "hardCharacterLimit", hardCharacterLimit, "continuity", continuityPreflight,
                "estimatePolicy", "advisory_after_best_effort_preparation",
                "dispatchAllowed", ConversationProviderPreflightAllowsDispatch(continuityPreflight));
            if (!ConversationProviderPreflightAllowsDispatch(continuityPreflight))
            { wire["status"] = "blocked"; wire["errorCode"] = "prompt_required_source_missing"; }
            wire["generationSettings"] = TestDict("reasoningMode", ReadString(settings, "reasoningMode", "selective"),
                "reasoningEffort", ReadString(settings, "reasoningEffort", "medium"), "temperature", ReadDouble(body, "temperature", 0.7));
            return captured;
        }

        private const int EvidenceWriterReplayMaxContinuations = 3;
        private const int EvidenceWriterReplayMaxRepairs = 4;
        // Reserve allocation room for subsequent cases, seeded rows and continuations in the same fixture.
        private const long EvidenceWriterReplayMaxImportedSequence = long.MaxValue - 1_000_000L;
        private sealed class EvidenceReplayRepairPending : Exception { }

        private static string EvidenceReplayCampaignIdentity(Dictionary<string, object> input)
        {
            // Response captures and follow-up requests must not change the seed
            // of the initial world. Campaign identity also seeds relationship
            // labels and source tie-breaks in the production prompt builders.
            var sources = ReadDictionaryList(input, "cases").Select(item => TestDict(
                "caseId", ReadString(item, "caseId", ""), "observer", ReadString(item, "observerId", ""),
                "peer", ReadString(item, "peerId", ""), "route", ReadString(item, "route", "dialogue"),
                "worldDay", ReadDouble(item, "worldDay", 0), "asOfSequence", ReadLong(item, "asOfSequence", 0),
                "query", ReadString(item, "query", ""), "sources", ReadDictionaryList(item, "sources"))).ToList();
            return "evidence_replay_" + PromptHash(CanonicalJson(TestDict("policy", "stable_replay_world_v1", "cases", sources))).Substring(0, 32);
        }

        private sealed class EvidenceReplayCampaignLease : IDisposable
        {
            internal readonly string Campaign;
            private ReignDbConnection connection;
            private bool locked;
            private bool ownsCampaign;

            internal EvidenceReplayCampaignLease(Dictionary<string, object> input)
            {
                if (!ReignPostgreSqlStorage.Options.IsValidation)
                    throw new InvalidOperationException("Repeatable replay requires the isolated validation database.");
                Campaign = EvidenceReplayCampaignIdentity(input);
                connection = ReignPostgreSqlStorage.OpenMetadataConnection();
                try
                {
                    locked = ReadBool(DialogueStore.SelectStatement11(connection, TestDict("owner", "memory-evidence-replay:" + Campaign)).Single(), "acquired", false);
                    if (!locked) throw new InvalidOperationException("This evidence replay world is already owned by another run.");
                    // CampaignExists joins the registry to pg_namespace and
                    // therefore cannot establish absence of orphaned state.
                    if (ReadBool(DialogueStore.SelectPgNamespace(connection, TestDict("schema", ReignPostgreSqlStorage.CampaignSchemaName(Campaign), "campaign", Campaign)).Single(), "present", true))
                        throw new InvalidOperationException("A prior evidence replay world still exists; refusing to reuse or delete unowned state.");
                    ownsCampaign = true;
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                if (connection == null) return;
                try { if (ownsCampaign) ReignPostgreSqlStorage.DropCampaign(Campaign); }
                finally
                {
                    try
                    {
                        if (locked) DialogueStore.SelectStatement12(connection, TestDict("owner", "memory-evidence-replay:" + Campaign));
                    }
                    finally { connection.Dispose(); connection = null; }
                }
            }
        }

        private static Dictionary<string, object> EvidenceWriterContinuationFixture(Dictionary<string, object> fixture,
            Dictionary<string, object> continuation)
        {
            var next = VerificationService.CloneDictionary(fixture);
            foreach (string key in new[] { "response", "repairResponses", "nextContribution", "nextPlanResponse", "nextAssemblyResponse", "nextReviewResponse", "continuation" })
                next.Remove(key);
            foreach (var pair in VerificationService.CloneDictionary(continuation)) next[pair.Key] = pair.Value;
            return next;
        }

        private static bool EvidenceWriterReplayComplete(Dictionary<string, object> result) =>
            ReadString(result, "status", "") == "captured_writer_stored_next_read_prepared"
            && ReadString(ReadDictionary(result, "nextWriterWireCapture"), "status", "") == "captured"
            && ReadBool(ReadDictionary(ReadDictionary(result, "nextWriterWireCapture"), "providerPreflight"), "dispatchAllowed", false)
            && (!result.ContainsKey("durableRead") || ReadString(ReadDictionary(result, "durableRead"), "status", "") == "captured")
            && (!result.ContainsKey("continuation") || EvidenceWriterReplayComplete(ReadDictionary(result, "continuation")));

        private static string EvidenceReplaySemanticInputError(Dictionary<string, object> item)
        {
            if (item.TryGetValue("captureSemanticDocuments", out var capture) && capture is not bool)
                return "invalid_semantic_document_capture";
            return item.ContainsKey("capturedSemanticSearches") ? "retired_analyst_search_capture" : "";
        }

        private static List<Dictionary<string, object>> CaptureReplaySemanticDocuments(ReignDbConnection connection,
            string campaign, string observer, string peer, Dictionary<string, object> context,
            Dictionary<string, object> settings, IEnumerable<string> sourceIds)
        {
            var access = DialogueService.BuildKnowledgeAccessContext(observer, peer, "", context);
            var result = new List<Dictionary<string, object>>();
            foreach (string id in sourceIds)
            {
                var row = LoadEmbeddingSourceRow(connection, "conversation_turn", id);
                if (row == null || !PrecisionTurnVisible(row, observer, access, connection)) continue;
                string text = BuildCanonicalEmbeddingText("conversation_turn", row);
                var document = BuildEmbeddingDocument(campaign, "conversation_turn", id, row, text, settings);
                result.Add(TestDict("sourceId", id, "sourceTextSha256", PromptHash(ReadString(row, "text", "")),
                    "documentContentHash", document.ContentHash, "document", TestDict("id", document.VectorId,
                        "text", document.Text, "payload", document.Payload)));
            }
            return result;
        }

        private static List<Dictionary<string, object>> EvidenceReplayRecentSources(Dictionary<string, object> item,
            List<Dictionary<string, object>> sources)
        {
            var visible = sources.Where(s => ReadStringList(s, "audience").Contains(ReadString(item, "observerId", "")))
                .OrderBy(s => ReadLong(s, "sequence", 0));
            if (!item.ContainsKey("recentSourceIds")) return visible.TakeLast(30).ToList();
            var selected = ReadStringList(item, "recentSourceIds").ToHashSet(StringComparer.Ordinal);
            return visible.Where(s => selected.Contains(ReadString(s, "sourceId", ""))).ToList();
        }

        private static Dictionary<string, object> EvidenceReplayMemoryContext(Dictionary<string, object> item, string timeline, string peer) =>
            TestDict("timelineId", timeline, "worldDay", ReadDouble(item, "worldDay", 0), "playerHeroStringId", peer,
                "interactionMode", ReadString(item, "route", "dialogue") == "event" ? "social_event" : ReadString(item, "route", "dialogue"),
                "memoryAsOfAcceptedSequence", ReadLong(item, "asOfSequence", 0), "memoryQueryText", ReadString(item, "query", ""),
                // A writer replay uses production's shared whole-record allocator.
                // Applying the standalone packet's excerpt budget first can lose
                // the very original the final-writer replay is meant to verify.
                "deferContinuityAllocation", item.ContainsKey("writerReplay"));

        private static Dictionary<string, object> EvidenceReplayNextMemoryContext(Dictionary<string, object> previous,
            string contribution, string session, string turnId, long asOfSequence)
        {
            // Match a fresh production HTTP turn: keep campaign identity, but
            // never inherit old source hints, query, allocation or receipts.
            var next = TestDict();
            foreach (string key in new[] { "campaignId", "timelineId", "worldDay", "interactionMode", "mode", "heroStringId",
                "mainHeroStringId", "playerHeroStringId", "mainHeroName", "playerName", "participants" })
                if (previous.ContainsKey(key)) next[key] = previous[key];
            next["sceneTurnId"] = turnId;
            next["playerText"] = contribution;
            next["conversationSessionId"] = session;
            next["memoryAsOfAcceptedSequence"] = asOfSequence;
            next["memoryQueryText"] = contribution;
            next["deferContinuityAllocation"] = true;
            return next;
        }

        private static bool EvidenceReplayAcceptedTranscriptTurn(Dictionary<string, object> row) =>
            ReadString(row, "status", "active") == "active"
                && new[] { "player", "npc" }.Contains(ReadString(row, "role", ""));

        private static string EvidenceReplayPlayerName(Dictionary<string, object> item,
            List<Dictionary<string, object>> transcript, string mappedPeer)
        {
            string peer = ReadString(item, "peerId", ""), observer = ReadString(item, "observerId", "");
            var original = ReadDictionaryList(item, "sources").Where(source => ReadString(source, "role", "") == "player"
                && ReadString(source, "speakerId", "") == peer && ReadStringList(source, "audience").Contains(observer)
                && ReadLong(source, "sequence", 0) <= ReadLong(item, "asOfSequence", 0)
                && ReadDouble(source, "worldDay", 0) <= ReadDouble(item, "worldDay", 0))
                .OrderBy(source => ReadLong(source, "sequence", 0)).LastOrDefault();
            // Empty recentSourceIds models a fresh conversation, not a renamed
            // interlocutor. Keep the known peer's eligible source attribution.
            return FirstNonEmpty(ReadString(transcript.LastOrDefault(turn => ReadString(turn, "speakerId", "") == mappedPeer), "speaker", ""),
                ReadString(original, "speakerName", ""),
                ReadString(ReadDictionary(item, "writerReplay"), "peerDisplayName", ""), "Player");
        }

        // Fixture capacity only: production prompt budgets and all other replay modes retain their own limits.
        private const int MemoryEvidenceReplayMaxSources = 2048;
        private const int MemoryEvidenceReplayMaxCaseCharacters = 4 * 1024 * 1024;

        private static int MemoryEvidenceReplayCaseCharacters(Dictionary<string, object> item) =>
            Json.Serialize(item.Where(p => p.Key != "capturedMemoryExtractions").ToDictionary(p => p.Key, p => p.Value)).Length;

        private static Dictionary<string, object> MemoryEvidenceReplayInputBounds(Dictionary<string, object> item) =>
            TestDict("sourceCount", ReadDictionaryList(item, "sources").Count, "sourceLimit", MemoryEvidenceReplayMaxSources,
                "serializedCharacters", MemoryEvidenceReplayCaseCharacters(item), "serializedCharacterLimit", MemoryEvidenceReplayMaxCaseCharacters,
                "measurement", "serialized_JSON_UTF16_characters_not_provider_tokens", "authority", "isolated_fixture_capacity_only");

        private static string MemoryEvidenceReplayInputError(Dictionary<string, object> item, int continuationDepth = 0)
        {
            var sources = ReadDictionaryList(item, "sources");
            if (ReadString(item, "caseId", "").Length == 0 || ReadString(item, "query", "").Length == 0
                || ReadString(item, "observerId", "").Length == 0 || ReadString(item, "peerId", "").Length == 0
                || !item.ContainsKey("asOfSequence") || !item.ContainsKey("worldDay") || sources.Count == 0 || sources.Count > MemoryEvidenceReplayMaxSources
                || MemoryEvidenceReplayCaseCharacters(item) > MemoryEvidenceReplayMaxCaseCharacters)
                return "missing_or_unbounded_case";
            if (item.ContainsKey("requiredEvidence") || item.ContainsKey("semanticExpectation")) return "labels_must_not_enter_candidate";
            string semanticError = EvidenceReplaySemanticInputError(item);
            if (semanticError.Length > 0) return semanticError;
            if (sources.Select(s => ReadString(s, "sourceId", "")).Distinct(StringComparer.Ordinal).Count() != sources.Count)
                return "duplicate_source";
            foreach (var source in sources)
            {
                if (ReadString(source, "sourceId", "").Length == 0 || ReadString(source, "speakerId", "").Length == 0
                    || ReadString(source, "speakerName", "").Length > 160
                    || ReadString(source, "text", "").Length == 0 || ReadString(source, "text", "").Length > 24000
                    || !new[] { "player", "npc" }.Contains(ReadString(source, "role", ""))
                    || !source.ContainsKey("sequence") || !source.ContainsKey("worldDay")
                    || ReadStringList(source, "audience").Count == 0) return "invalid_source";
                if (ReadLong(source, "sequence", long.MaxValue) > ReadLong(item, "asOfSequence", 0)
                    || ReadDouble(source, "worldDay", double.MaxValue) > ReadDouble(item, "worldDay", 0)) return "future_source";
            }
            if (item.TryGetValue("recentSourceIds", out var recentValue))
            {
                if (recentValue is not IEnumerable<object> recent || recent.Any(v => v is not string)
                    || recent.Count() > 30) return "invalid_recent_source_ids";
                var ids = recent.Cast<string>().ToList();
                if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                    || ids.Any(id => !sources.Any(s => ReadString(s, "sourceId", "") == id
                        && ReadStringList(s, "audience").Contains(ReadString(item, "observerId", "")))))
                    return "invalid_recent_source_ids";
            }
            if (item.ContainsKey("writerReplay"))
            {
                var writer = ReadDictionary(item, "writerReplay");
                string referenceModeError = ReplayReferenceGraphModeError(writer);
                if (referenceModeError.Length > 0) return referenceModeError;
                string initialNativeError = ReplayInitialNativeContextError(item, writer);
                if (initialNativeError.Length > 0) return initialNativeError;
                string inspectionError = ReplayQuestionLinkInspectionError(writer);
                if (inspectionError.Length > 0) return inspectionError;
                if (writer == null || !new[] { "dialogue", "party_chat", "event" }.Contains(ReadString(item, "route", "dialogue"))
                    || ReadString(writer, "model", "").Length is < 1 or > 200
                    || ReadString(writer, "nextContribution", "").Length is < 1 or > 24000
                    || !writer.ContainsKey("snapshotAsOfSequence")
                    || ReadLong(writer, "snapshotAsOfSequence", long.MaxValue) > ReadLong(item, "asOfSequence", 0)
                    || ReadLong(item, "asOfSequence", long.MaxValue) > EvidenceWriterReplayMaxImportedSequence
                    || ReadLong(writer, "snapshotAsOfSequence", -1) < 0
                    || Json.Serialize(writer).Length > 256000) return "invalid_writer_replay";
                var allowed = new[] { "model", "snapshotAsOfSequence", "profile", "peerDisplayName", "characteristics", "state", "relationships",
                    "sceneContext", "response", "repairResponses", "nextContribution", "nextPlanResponse", "nextAssemblyResponse", "nextReviewResponse", "contextCapacity", "finalCheck", "generationSettings", "continuation", "initialNativeContext", "referenceGraphMode", "questionLinkInspection" };
                if (writer.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) return "unknown_writer_replay_field";
                if (writer.TryGetValue("peerDisplayName", out var peerDisplayName)
                    && (peerDisplayName is not string name || name.Length is < 1 or > 160 || name != name.Trim()
                        || name.Any(c => char.IsControl(c) || c == '\u2028' || c == '\u2029')))
                    return "invalid_writer_peer_display_name";
                if (writer.ContainsKey("repairResponses"))
                {
                    if (!IsConversationObjectArray(writer["repairResponses"])) return "invalid_writer_repairs";
                    var repairs = ReadDictionaryList(writer, "repairResponses");
                    if (repairs.Count > EvidenceWriterReplayMaxRepairs || repairs.Any(r => r.Keys.Any(k => k != "requestSha256" && k != "response")
                        || ReadString(r, "requestSha256", "").Length != 64 || ReadString(r, "requestSha256", "").Any(c => !Uri.IsHexDigit(c))
                        || ReadDictionary(r, "response") == null || ReadString(ReadDictionary(r, "response"), "content", "").Length > 96000))
                        return "invalid_writer_repairs";
                }
                string generationError = ReplayGenerationSettingsError(writer);
                if (generationError.Length > 0) return generationError;
                foreach (string key in new[] { "profile", "characteristics", "state", "relationships", "response", "nextPlanResponse", "nextAssemblyResponse", "nextReviewResponse" })
                    if (writer.ContainsKey(key) && ReadDictionary(writer, key) == null) return "invalid_writer_replay_object";
                if (ReadDictionary(writer, "response") is { } capturedResponse
                    && capturedResponse.TryGetValue("requestSha256", out var requestHash)
                    && (requestHash is not string responseHash || responseHash.Length != 64 || responseHash.Any(c => !Uri.IsHexDigit(c))))
                    return "invalid_writer_response_request_sha256";
                // Disabled legacy fixtures remain readable; retired stages can never run.
                if (writer.ContainsKey("finalCheck") && (ReadDictionary(writer, "finalCheck") is not { } oldCheck
                    || oldCheck.GetValueOrDefault("enabled") is not bool || ReadBool(oldCheck, "enabled", false)))
                    return "memory_llm_stage_retired";
                if (writer.ContainsKey("contextCapacity"))
                {
                    var capacity = ReadDictionary(writer, "contextCapacity");
                    var keys = new[] { "model", "contextTokens", "maxOutputTokens", "source", "sourceHash", "checkedAtUtc", "validUntilUtc" };
                    string source = ReadString(capacity, "source", ""), hash = ReadString(capacity, "sourceHash", "");
                    if (capacity == null || capacity.Keys.Any(k => !keys.Contains(k, StringComparer.Ordinal))
                        || ReadString(capacity, "model", "") != ReadString(writer, "model", "")
                        || ReadInt(capacity, "contextTokens", 0) is < 1 or > 2000000
                        || ReadInt(capacity, "maxOutputTokens", 0) is < 1 or > 262144
                        || ReadInt(capacity, "maxOutputTokens", 0) > ReadInt(capacity, "contextTokens", 0)
                        || !(source == NanoConversationModelsUrl || source == "https://nano-gpt.com/api/subscription/v1/models?detailed=true")
                        || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))
                        || !DateTimeOffset.TryParse(ReadString(capacity, "checkedAtUtc", ""), out var checkedAt)
                        || !DateTimeOffset.TryParse(ReadString(capacity, "validUntilUtc", ""), out var validUntil)
                        || validUntil <= checkedAt || validUntil > checkedAt.AddHours(6)
                        || checkedAt > DateTimeOffset.UtcNow.AddMinutes(1)) return "invalid_writer_capacity_capture";
                }
                if (writer.ContainsKey("continuation"))
                {
                    var continuation = ReadDictionary(writer, "continuation");
                    if (continuationDepth >= EvidenceWriterReplayMaxContinuations || continuation == null
                        || continuation.Keys.Any(k => !new[] { "response", "repairResponses", "nextContribution", "nextPlanResponse", "nextAssemblyResponse",
                            "nextReviewResponse", "finalCheck", "continuation" }.Contains(k, StringComparer.Ordinal)))
                        return "invalid_writer_continuation";
                    var continuedItem = VerificationService.CloneDictionary(item);
                    continuedItem["writerReplay"] = EvidenceWriterContinuationFixture(writer, continuation);
                    string continuationError = MemoryEvidenceReplayInputError(continuedItem, continuationDepth + 1);
                    if (continuationError.Length > 0) return continuationError;
                }
            }
            string reviewError = ReplayReviewedFactInputError(item, sources);
            if (reviewError.Length > 0) return reviewError;
            string recoveryError = ReplayReviewedRecoveryInputError(item);
            return recoveryError.Length > 0 ? recoveryError : DurableReadReplayInputError(item);
        }

        // Stable source identities let an unchanged captured continuation refer
        // to the preceding accepted turn after the disposable database is rebuilt.
        // Only the identity seed is deterministic: storage still uses the unique
        // run-owned campaign. Bind history and prose so changed fixtures cannot
        // inherit earlier references. This helper is private to offline replay.
        private static string EvidenceReplayEventId(Dictionary<string, object> item, string observer,
            Dictionary<string, object> payload, string reply)
        {
            string seed = PromptHash(CanonicalJson(TestDict("caseId", ReadString(item, "caseId", "writer_turn"),
                "route", ReadString(item, "route", "dialogue"), "sources", ReadDictionaryList(item, "sources"),
                "snapshotAsOfSequence", ReadLong(ReadDictionary(item, "writerReplay"), "snapshotAsOfSequence", 0),
                "playerText", ReadString(payload, "playerText", ""), "reply", reply ?? "")));
            return AcceptedConversationEventId("evidence_fixture_" + seed, observer, payload);
        }

        // This is a sliced writer/storage replay, not the live endpoint. It never
        // dispatches a writer, action, relationship assessment or scene effect.
        // Captured prose and proposed memory layers use the production publisher.
        private static Dictionary<string, object> ReplayEvidenceWriter(string campaign, string observer, string peer,
            Dictionary<string, object> item, Dictionary<string, object> context, Dictionary<string, object> settings,
            Dictionary<string, object> packet, List<Dictionary<string, object>> transcript,
            Dictionary<string, object> preparedWriterRequest = null, int continuationDepth = 0, PublishedPartyReplayView publishedParty = null,
            Func<string, string> mapSourceId = null)
        {
            if (continuationDepth > EvidenceWriterReplayMaxContinuations)
                throw new InvalidOperationException("Captured writer continuation depth exceeds its bound.");
            var fixture = ReadDictionary(item, "writerReplay");
            if (fixture == null) return TestDict("status", "not_requested");
            using var referenceMode = new EvidenceReplayReferenceModeScope(fixture);
            string mode = ReadString(item, "route", "dialogue"), model = ReadString(fixture, "model", "");
            string heroName = ReadString(ReadDictionary(fixture, "profile"), "name",
                ReadString(transcript.LastOrDefault(t => ReadString(t, "speakerId", "") == observer), "speaker", "Observer"));
            string playerName = EvidenceReplayPlayerName(item, transcript, peer);
            var profile = VerificationService.CloneDictionary(ReadDictionary(fixture, "profile"));
            profile["name"] = heroName; profile["heroStringId"] = observer;
            profile["campaignId"] = campaign;
            profile["politicalTimelineId"] = ReadString(context, "timelineId", "main");
            var payload = VerificationService.CloneDictionary(context);
            payload["campaignId"] = campaign; payload["heroStringId"] = observer;
            payload["mainHeroStringId"] = peer; payload["playerHeroStringId"] = peer;
            payload["mainHeroName"] = playerName; payload["playerName"] = playerName;
            if (preparedWriterRequest == null)
            {
                payload["conversationSessionId"] = observer + ":writer_replay";
                payload["sceneTurnId"] = observer + ":" + ReadString(item, "caseId", "writer_turn");
            }
            payload["participants"] = publishedParty?.Audience ?? new[] { observer, peer };
            payload["playerText"] = ReadString(item, "query", "");
            payload["mode"] = mode == "event" ? "social_event" : mode;
            payload["interactionMode"] = payload["mode"];
            // LoadSettings inside the real prompt builders must resolve the same
            // writer model as the captured request, not the Lab's default model.
            // Captured capacity metadata is fixture evidence only: no network or
            // production settings write is allowed, and normal limits still apply.
            var writerSettings = VerificationService.CloneDictionary(settings);
            writerSettings["llmProvider"] = LlmProviderService.NanoGptProvider;
            writerSettings["dialogueModel"] = model; writerSettings["eventsModel"] = model;
            writerSettings["apiKey"] = ""; writerSettings["nanoGptApiKey"] = "";
            ApplyReplayGenerationSettings(fixture, writerSettings);
            if (ReadDictionary(fixture, "contextCapacity") is { } capturedCapacity)
            {
                var certificate = VerificationService.CloneDictionary(capturedCapacity);
                certificate["endpoint"] = PortraitsService.NanoGptChatCompletionsUrl;
                string routeKey = LlmProviderService.NanoGptProvider + ":" + model + ":" + PromptHash(PortraitsService.NanoGptChatCompletionsUrl + "|" + CanonicalJson(TestDict())).Substring(0, 20);
                writerSettings["verifiedConversationContextWindows"] = TestDict(routeKey, certificate);
            }
            var empty = TestDict();
            var characteristics = VerificationService.CloneDictionary(ReadDictionary(fixture, "characteristics"));
            var initialNativeContext = MapReplayInitialNativeContext(fixture, ReadString(item, "observerId", ""),
                ReadString(item, "peerId", ""), observer, peer);
            Dictionary<string, object> initialPoliticalBinding = null;
            if (initialNativeContext != null)
            {
                if (preparedWriterRequest != null || continuationDepth != 0)
                    throw new InvalidOperationException("Initial native context cannot override a prepared or continued request.");
                foreach (var pair in initialNativeContext) payload[pair.Key] = VerificationService.CloneDictionary((Dictionary<string, object>)pair.Value);
                initialPoliticalBinding = BindInitialReplayPoliticalProfile(item, fixture, profile, characteristics,
                    campaign, observer, ReadString(context, "timelineId", "main"));
            }
            var nativeIdentity = ReadDictionary(initialNativeContext, "identityView");
            var identity = nativeIdentity == null
                ? TestDict("identityState", "verified", "canonicalNameAllowed", true, "usableName", playerName)
                : VerificationService.CloneDictionary(nativeIdentity);
            Dictionary<string, object> Request(Dictionary<string, object> turn, List<Dictionary<string, object>> prior,
                Dictionary<string, object> memory, PublishedPartyReplayView initialView = null)
            {
                string contribution = ReadString(turn, "playerText", "");
                string scene = ReadString(fixture, "sceneContext", "");
                bool socialEvent = mode != "dialogue";
                // The precomputed text seam bypasses BuildMemoryPacketForPrompt.
                // Carry its companion metadata as production does; otherwise
                // continuity adds the legacy history again and final preflight
                // cannot verify the selected precision sources.
                var memoryEvidence = ReadDictionary(memory, "precisionEvidence");
                turn["memoryPrecisionEvidence"] = memoryEvidence == null ? null : VerificationService.CloneDictionary(memoryEvidence);
                string memoryText = ReadString(memory, "memoryPacket", "");
                if (!string.IsNullOrWhiteSpace(memoryText)) memoryText = "REIGN KNOWLEDGE PACKET\n" + memoryText;
                turn["continuityOutputReserve"] = socialEvent ? DialogueService.StructuredEventResponseMaxTokens(writerSettings) : DialogueService.StructuredDialogueResponseMaxTokens(writerSettings);
                var priorSettings = LlmProviderService.CodexPerformanceSettings.Value;
                try
                {
                    LlmProviderService.CodexPerformanceSettings.Value = writerSettings;
                    PromptEnvelope envelope = socialEvent
                    ? BuildEventPromptEnvelope(campaign, observer + ":event", observer, heroName, playerName, playerName,
                        contribution, scene, turn, profile, characteristics, VerificationService.CloneDictionary(ReadDictionary(fixture, "state")),
                        VerificationService.CloneDictionary(ReadDictionary(fixture, "relationships")), empty, prior, new(), new(), new(), identity,
                        precomputedMemoryPacket: memoryText, precomputedGroupConversationPrompt: "", publishedParty: initialView)
                    : BuildDialoguePromptEnvelope(campaign, observer, heroName, playerName, playerName,
                        contribution, scene, profile, characteristics, VerificationService.CloneDictionary(ReadDictionary(fixture, "state")),
                        VerificationService.CloneDictionary(ReadDictionary(fixture, "relationships")), empty, prior, new(), new(), new(), identity, turn,
                        precomputedMemoryPacket: memoryText, precomputedGroupConversationPrompt: "");
                    // Replay retains production's evidence gate. Estimated size
                    // remains advisory here just as at real provider dispatch.
                    if (!ConversationProviderPreflightAllowsDispatch(ReadDictionary(envelope.Diagnostics, "continuityPreflight")))
                        throw new InvalidOperationException("Captured writer context is missing required original evidence.");
                    var prepared = DialogueService.BuildConversationGenerationRequest(writerSettings, envelope, campaign, "", observer,
                        contribution, socialEvent, observer + ":event");
                    // The non-listening fixture has no native profile file. Bind its
                    // mapped profile as the ordinary wire builder expects, retaining
                    // required political context inside this isolated replay world.
                    prepared["hero"] = VerificationService.CloneDictionary(profile);
                    prepared["model"] = model;
                    prepared["singleProviderAttempt"] = true;
                    return prepared;
                }
                finally { LlmProviderService.CodexPerformanceSettings.Value = priorSettings; }
            }
            Dictionary<string, object> request;
            ReviewedReplayWriterPlan expectedWriterPlan;
            try
            {
                request = preparedWriterRequest == null ? Request(payload, transcript, packet, publishedParty) : VerificationService.CloneDictionary(preparedWriterRequest);
                expectedWriterPlan = PrepareReplayReviewedWriterPlan(request, item, ReadDictionary(packet, "reviewedFactProjection"));
            }
            catch (InvalidOperationException ex)
            {
                return TestDict("status", "writer_preparation_failed", "error", ex.Message, "providerCalls", 0,
                    "certificateEligible", false, "pendingResponses", new[] { "response" },
                    "contextCapacity", ReadDictionary(fixture, "contextCapacity") ?? TestDict());
            }
            var pending = new List<string>();
            var result = TestDict("status", "writer_pending", "request", request,
                "referenceGraphAudit", referenceMode.Audit,
                "requestSha256", PromptHash(CanonicalJson(request)), "pendingResponses", pending,
                "snapshotAsOfSequence", fixture["snapshotAsOfSequence"], "providerCalls", 0,
                "continuationDepth", continuationDepth, "requestOrigin", preparedWriterRequest == null ? "prepared_here" : "preceding_turn_next_request",
                "scope", "production_prompt_memory_publication_and_next_read_only", "certificateEligible", false,
                "sourceIdentityPolicy", "fixture_bound_accepted_turns_v1",
                "excluded", new[] { "live_endpoint_repairs", "action_execution", "relationship_assessment_application",
                    "scene_social_identity_effects", "letter_delivery", "semantic_worker", "native_and_human_acceptance" });
            var providerWire = ReadDictionary(CaptureMemoryReplayProviderRequest(request, writerSettings), "providerWireCapture");
            result["providerWireCapture"] = providerWire;
            if (initialNativeContext != null) result["initialNativeContextAudit"] = TestDict(
                "inputSha256", PromptHash(CanonicalJson(ReadDictionary(fixture, "initialNativeContext"))),
                "mappedSha256", PromptHash(CanonicalJson(initialNativeContext)), "keys", initialNativeContext.Keys.ToArray(),
                "actionIndexRetainedUnchanged", !initialNativeContext.ContainsKey("actionResolutionIndex")
                    || CanonicalJson(ReadDictionary(payload, "actionResolutionIndex")) == CanonicalJson(ReadDictionary(initialNativeContext, "actionResolutionIndex")),
                "captureOnly", true, "publicationAllowed", false, "idMapping", "exact_self_and_peer_values_only",
                "politicalBinding", initialPoliticalBinding,
                "limits", "Embedded IDs in prose are retained; fixture namespace and absent runtime-derived state prevent unqualified native parity claims. Index inclusion in payload does not mean all index bytes are in the writer wire.");
            if (ReadString(providerWire, "status", "") == "blocked")
            {
                result["status"] = "writer_preflight_blocked";
                result["reason"] = "The exact serialized writer request failed production preflight; no response or memory was published.";
                return result;
            }
            if (expectedWriterPlan != null)
            {
                var body = TryParseJsonObject(ReadString(providerWire, "requestJson", ""));
                var expectedFormat = ReadDictionary(request, "response_format");
                if (CanonicalJson(ReadDictionary(body, "response_format")) != CanonicalJson(expectedFormat))
                {
                    result["status"] = "writer_reviewed_plan_schema_missing";
                    return result;
                }
                var planAudit = TestDict("scope", expectedWriterPlan.DeliveryChoices == null
                        ? "isolated_reviewed_facts_and_free_prose" : "isolated_reviewed_delivery",
                    "schemaSha256", PromptHash(CanonicalJson(expectedFormat)), "planConformance", "pending");
                if (expectedWriterPlan.DeliveryChoices != null) planAudit["deliveryChoicesSha256"] = expectedWriterPlan.DeliveryChoicesSha256;
                result["reviewedWriterPlan"] = planAudit;
            }
            if (publishedParty != null)
            {
                result["publishedPartyView"] = publishedParty.Audit;
                if (!publishedParty.WirePreservesHistory(providerWire))
                {
                    result["status"] = "writer_published_party_view_blocked";
                    result["reason"] = "published_history_changed_on_serialized_wire";
                    return result;
                }
            }
            if (ReadDictionary(packet, "reviewedFactProjection") is { } projection)
            {
                result["reviewedFactProjection"] = projection;
                string wireError = ReplayReviewedFactWireError(providerWire, item, transcript, observer, mapSourceId);
                if (wireError.Length > 0)
                {
                    result["status"] = "writer_reviewed_fact_projection_blocked";
                    result["reason"] = wireError;
                    return result;
                }
            }
            var response = ReadDictionary(fixture, "response");
            if (fixture.ContainsKey("questionLinkInspection"))
                result["questionLinkInspection"] = InspectReplayQuestionLinks(campaign, fixture, payload, observer, peer);
            if (response == null) { pending.Add("response"); return result; }
            if (response.TryGetValue("requestSha256", out var requestHash))
            {
                string expected = ReadString(providerWire, "requestSha256", "");
                bool matched = expected.Length == 64 && requestHash is string supplied
                    && string.Equals(supplied, expected, StringComparison.OrdinalIgnoreCase);
                result["responseRequestBinding"] = TestDict("status", matched ? "verified" : "mismatched", "requestSha256", expected);
                if (!matched)
                {
                    result["status"] = "writer_response_request_mismatch";
                    result["reason"] = "The supplied response does not match the exact serialized writer request.";
                    return result;
                }
            }
            else result["responseRequestBinding"] = TestDict("status", "legacy_unbound");
            if (preparedWriterRequest != null)
            {
                // Request(next, ...) already bound the complete production
                // evidence, including recent rows deduplicated from retrieval.
                // Cloning JSON loses the server-only receipt; preserve that
                // receipt with its observer/turn/query fences instead of
                // reconstructing a narrower historical-only approximation.
                CopyDeliveredMemoryRecallEvidence(context, payload, observer);
            }
            if (item.ContainsKey("reviewedRecoveryReplay"))
            {
                response = ReplayReviewedRecovery(campaign, observer, peer, item, context, transcript, request,
                    providerWire, response, result, writerSettings, pending);
                if (response == null) return result;
            }
            else if (fixture.ContainsKey("repairResponses"))
            {
                bool deterministicOnly = item.ContainsKey("reviewedFactReplay");
                var captures = ReadDictionaryList(fixture, "repairResponses");
                var repairRequests = new List<Dictionary<string, object>>();
                result["repairRequests"] = repairRequests;
                result["initialCapturedProse"] = ReadFirstString(TryParseJsonObject(ReadString(response, "content", "")), "reply", "body");
                result["excluded"] = ReadStringList(result, "excluded").Where(x => x != "live_endpoint_repairs")
                    .Append("other_endpoint_repairs_and_live_transport").ToArray();
                Dictionary<string, object> CapturedRepair(Dictionary<string, object> repairRequest)
                {
                    if (deterministicOnly)
                    {
                        result["status"] = "writer_reviewed_deterministic_repair_requires_provider";
                        result["reason"] = "Native cleanup required a provider repair. This compact replay ended before exporting a repair request or publishing.";
                        throw new EvidenceReplayRepairPending();
                    }
                    int index = repairRequests.Count;
                    if (index >= EvidenceWriterReplayMaxRepairs) throw new InvalidOperationException("Conversation repair capture exceeded its bound.");
                    // Repairs have their own request dictionary; retain the same
                    // isolated native profile through the ordinary political guard.
                    repairRequest["hero"] = VerificationService.CloneDictionary(profile);
                    var wire = ReadDictionary(CaptureMemoryReplayProviderRequest(repairRequest, writerSettings), "providerWireCapture");
                    repairRequests.Add(TestDict("index", index, "requestType", ReadString(repairRequest, "requestType", ""),
                        "request", repairRequest, "providerWireCapture", wire));
                    if (ReadString(wire, "status", "") == "blocked")
                    {
                        result["status"] = "writer_preflight_blocked";
                        result["reason"] = "The exact serialized repair request failed production preflight; no response or memory was published.";
                        throw new EvidenceReplayRepairPending();
                    }
                    if (index >= captures.Count)
                    {
                        result["status"] = "writer_repair_pending"; pending.Add("repairResponses[" + index + "]");
                        throw new EvidenceReplayRepairPending();
                    }
                    if (!string.Equals(ReadString(captures[index], "requestSha256", ""), ReadString(wire, "requestSha256", ""), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Captured conversation repair does not match its exact production request.");
                    result["repairResponsesConsumed"] = index + 1;
                    return VerificationService.CloneDictionary(ReadDictionary(captures[index], "response"));
                }
                try
                {
                    string auditMode = mode == "dialogue" ? "dialogue" : "party_chat";
                    response = ConversationResponseService.RetryMalformedStructuredResponse(VerificationService.CloneDictionary(response), request, campaign,
                        "captured-writer", auditMode, observer, "", CapturedRepair);
                    response = RetryRoleplayContinuityViolation(response, request, payload, identity, transcript, campaign,
                        "captured-writer", auditMode, observer, heroName, playerName, "", CapturedRepair);
                    result["conversationRepairEvidence"] = TestDict("format", ReadDictionary(response, "formatRetry"),
                        "continuity", ReadDictionary(response, "roleplayContinuityRepair"));
                }
                catch (EvidenceReplayRepairPending) { return result; }
            }
            string finish = ReadString(response, "finishReason", "");
            var parsed = TryParseJsonObject(ReadString(response, "content", ""));
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            if (expectedWriterPlan != null)
            {
                var planAudit = ReadDictionary(result, "reviewedWriterPlan");
                if (expectedWriterPlan.DeliveryChoices != null
                    && !string.Equals(expectedWriterPlan.DeliveryChoicesSha256, ReadString(response, "reviewedDeliverySha256", ""), StringComparison.OrdinalIgnoreCase))
                {
                    planAudit["planConformance"] = "mismatch";
                    result["status"] = "writer_reviewed_delivery_receipt_mismatch";
                    return result;
                }
                bool verified = ApplyReplayReviewedWriterPlan(expectedWriterPlan, parsed, out string delivery);
                planAudit["planConformance"] = verified ? "verified" : "mismatch";
                if (!verified)
                {
                    result["status"] = "writer_reviewed_plan_response_mismatch";
                    return result;
                }
                if (delivery.Length > 0)
                {
                    reply = ReadString(parsed, "reply", "");
                    planAudit["delivery"] = delivery;
                    planAudit["capturedResponseSha256"] = PromptHash(ReadString(response, "content", ""));
                    planAudit["approvedReplySha256"] = PromptHash(reply);
                }
            }
            bool quiet = mode != "dialogue" && parsed != null && WorldSimulationService.NormalizeSocialEventParticipation(parsed) == "quiet";
            var checkedResponse = ConversationResponseService.RejectProviderReservedSpeech(response, parsed);
            if (!ReferenceEquals(checkedResponse, response))
            {
                result["status"] = "writer_response_rejected";
                result["reason"] = ReadString(checkedResponse, "error", "");
                return result;
            }
            if (!ReadBool(response, "ok", false) || parsed == null || !quiet && string.IsNullOrWhiteSpace(reply)
                || reply.Length > 24000 || IsTechnicalConversationNotice(reply)
                || new[] { "memoryWrites", "beliefWrites", "obligationWrites", "comprehensionWrites", "continuityWrites" }
                    .Any(key => ReadDictionaryList(parsed, key).Count > 32)
                || new[] { "length", "max_tokens", "content_filter", "refusal" }.Contains(finish.Trim().ToLowerInvariant()))
            {
                result["status"] = "writer_response_rejected";
                result["reason"] = "A successful complete captured prose response is required; no source or memory was published.";
                return result;
            }
            if (quiet)
            {
                result["status"] = "writer_quiet";
                result["participation"] = "quiet";
                result["acceptedProse"] = "";
                result["reason"] = "Intentional silence is distinct from malformed output. Quiet-turn publication and its next read are outside this speech replay; no source or memory was published.";
                result["excluded"] = ReadStringList(result, "excluded").Append("quiet_turn_publication_and_next_read").ToArray();
                return result;
            }
            // Production renders the final scene heading and checks those exact
            // bytes before publication. Captured replay omits remote repairs,
            // but must retain the same unresolved-grounding suppression rather
            // than publishing unchecked metadata or bypassing the source guard.
            result["capturedProse"] = reply;
            reply = RenderGroundedSceneHeading(payload, observer, parsed, reply);
            result["groundingAccepted"] = FinalizeConversationGrounding(payload, observer, parsed, ref reply);
            parsed["reply"] = reply;
            result["grounding"] = VerificationService.CloneDictionary(ReadDictionary(payload, "conversationGrounding"));
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string generation;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            var rejections = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> Writes(string key, string kind)
            {
                var values = kind == "memory" ? ActionPipelineService.NormalizeMemoryWrites(parsed.GetValueOrDefault(key), campaign, observer, timestamp)
                    : ReadDictionaryList(parsed, key);
                return FilterRoleplayFeedbackWrites(ActionPipelineService.FilterConversationRoleMisattributions(values, payload,
                    ReadString(payload, "playerText", ""), kind, rejections), kind, rejections, payload, observer, campaign);
            }
            var unit = new AcceptedConversationUnit {
                Campaign = campaign, Hero = observer, Player = peer, PlayerName = playerName, HeroName = heroName,
                Location = "", EventId = EvidenceReplayEventId(item, observer, payload, reply),
                EventType = mode == "dialogue" ? "dialogue_turn" : "party_chat_turn", GroupConversation = mode != "dialogue",
                PlayerText = ReadString(payload, "playerText", ""), Reply = reply, Timestamp = timestamp, Generation = generation,
                Payload = payload, Motive = TestDict(), Profile = profile, Characteristics = characteristics, Parsed = parsed,
                CommitEffects = false, PublishScene = false, PublishSocial = false, PublishIntroductions = false,
                RetrievalEligible = !IsPureConversationRecallRequest(ReadString(payload, "playerText", "")),
                Memories = Writes("memoryWrites", "memory"), Beliefs = Writes("beliefWrites", "belief"), Relationships = new(),
                Obligations = Writes("obligationWrites", "obligation"), Comprehensions = Writes("comprehensionWrites", "comprehension"),
                Continuity = ReadDictionaryList(parsed, "continuityWrites") };
            if (mapSourceId != null || item.ContainsKey("durableReadReplay"))
                AdvanceEvidenceReplaySequence(campaign, ReadLong(item, "asOfSequence", 0));
            var publication = PublishAcceptedConversationUnit(unit, accept => mode == "dialogue"
                ? StoreConversationExchange(campaign, payload, observer, peer, playerName, heroName, unit.PlayerText, reply, unit.EventId, timestamp, acceptance: accept)
                : StorePartyConversationExchange(campaign, payload, observer, peer, playerName, heroName, unit.PlayerText, reply, unit.EventId, timestamp, accept));
            result["publication"] = publication; result["writeRejections"] = rejections;
            result["acceptedProse"] = reply;
            var stored = TestDict();
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                foreach (string table in new[] { "conversation_turns", "memories", "beliefs", "obligations", "comprehension" })
                    if (WorldSimulationService.TableExists(connection, table)) stored[table] = DialogueStore.SelectDynamic11(connection, table,TestDict("event", unit.EventId));
                if (WorldSimulationService.TableExists(connection, "conversation_continuity")) stored["continuity"] = DialogueStore.SelectConversationContinuity14(connection, TestDict("owner", observer, "timeline", ContinuityTimeline(payload)));
            }
            result["stored"] = stored;
            result["memoryUpdateComplete"] = ReadBool(ReadDictionary(publication, "acceptedUnit"), "memoryUpdateComplete", false);
            if (!ReadBool(ReadDictionary(publication, "acceptedUnit"), "ok", false))
            {
                result["status"] = "writer_publication_incomplete";
                return result;
            }
            // A new HTTP turn does not inherit the prior request's preparation,
            // cutoff, source hints or acceptance receipts. Reconstruct that boundary.
            // Pin a repeated replay to this accepted turn. A later continuation
            // already stored by an earlier run cannot leak into its predecessor.
            var acceptedTurns = ReadDictionaryList(stored, "conversation_turns").Where(EvidenceReplayAcceptedTranscriptTurn).ToList();
            var next = EvidenceReplayNextMemoryContext(payload, ReadString(fixture, "nextContribution", ""),
                ReadString(publication, "sessionId", ReadString(payload, "conversationSessionId", "")), observer + ":next_turn:" + unit.EventId,
                acceptedTurns.Select(row => ReadLong(row, "accepted_sequence", 0)).DefaultIfEmpty(0).Max());
            result["nextAsOfAcceptedSequence"] = next["memoryAsOfAcceptedSequence"];
            var nextTranscript = new List<Dictionary<string, object>>(transcript);
            foreach (var row in acceptedTurns.OrderBy(r => ReadLong(r, "accepted_sequence", 0)))
                nextTranscript.Add(TestDict("turnId", ReadString(row, "turn_id", ""), "speakerId", ReadString(row, "speaker_id", ""),
                    "speaker", ReadString(row, "speaker_name", ""), "role", ReadString(row, "role", ""), "text", ReadString(row, "text", ""),
                    "payload_json", ReadString(row, "payload_json", "{}"), "participants_json", ReadString(row, "participants_json", "[]")));
            nextTranscript = nextTranscript.TakeLast(30).ToList();
            SetMemoryTranscriptContext(next, nextTranscript);
            {
                var nextPacket = DialogueService.BuildNpcMemoryPacket(campaign, observer, peer, "", ReadString(next, "playerText", ""), 4000, next, settings);
                result["nextPacket"] = nextPacket;
                try
                {
                    var nextWriter = Request(next, nextTranscript, nextPacket);
                    result["nextWriterRequest"] = nextWriter;
                    result["nextWriterWireCapture"] = ReadDictionary(CaptureMemoryReplayProviderRequest(nextWriter, writerSettings), "providerWireCapture");
                    if (ReadString(ReadDictionary(result, "nextWriterWireCapture"), "status", "") == "blocked")
                    {
                        result["status"] = "next_writer_preflight_blocked";
                        result["reason"] = "The prior accepted turn remains stored, but its prepared follow-up failed production preflight.";
                        result["allRequestedTurnsReplayed"] = false;
                        return result;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    result["status"] = "next_writer_preparation_failed";
                    result["error"] = ex.Message;
                    return result;
                }
                result["status"] = pending.Count == 0 ? "captured_writer_stored_next_read_prepared" : "next_evidence_pending";
                if (mapSourceId != null && publishedParty == null && item.ContainsKey("reviewedFactReplay")
                    && !item.ContainsKey("reviewedRecoveryReplay") && !item.ContainsKey("durableReadReplay"))
                    result["reviewedFactFollowUp"] = CaptureReplayReviewedFollowUp(campaign, observer, peer, item, VerificationService.CloneDictionary(next),
                        nextTranscript, nextPacket, mapSourceId, writerSettings, (turn, lines, memory) => Request(turn, lines, memory));
                if (item.ContainsKey("durableReadReplay"))
                    result["durableRead"] = ReplayLaterDurableRead(campaign, observer, peer, item, payload, writerSettings,
                        acceptedTurns, (turn, lines, memory) => Request(turn, lines, memory));
                if (ReadDictionary(fixture, "continuation") is { } continuation)
                {
                    if (pending.Count > 0)
                        result["continuation"] = TestDict("status", "waiting_for_prior_evidence", "providerCalls", 0, "certificateEligible", false);
                    else
                    {
                        var continuedItem = VerificationService.CloneDictionary(item);
                        continuedItem["caseId"] = ReadString(item, "caseId", "writer_turn") + ":continuation";
                        continuedItem["query"] = next["playerText"];
                        continuedItem["asOfSequence"] = next["memoryAsOfAcceptedSequence"];
                        continuedItem["writerReplay"] = EvidenceWriterContinuationFixture(fixture, continuation);
                        result["continuation"] = ReplayEvidenceWriter(campaign, observer, peer, continuedItem, next, settings,
                            nextPacket, nextTranscript, ReadDictionary(result, "nextWriterRequest"), continuationDepth + 1);
                    }
                }
                result["allRequestedTurnsReplayed"] = EvidenceWriterReplayComplete(result);
            }
            return result;
        }

        // An explicit, non-listening replay route. Captured responses never grant
        // permission to contact a provider, access a live campaign or run actions.
        private static string MemoryExtractionReplayInputError(Dictionary<string, object> item, string modelHash)
        {
            if (!item.ContainsKey("capturedMemoryExtractions")) return "missing_memory_extraction_captures";
            if (!System.Text.RegularExpressions.Regex.IsMatch(modelHash ?? "", @"\A[0-9a-f]{64}\z")
                || !LateInteractionObjectList(item, "capturedMemoryExtractions")) return "invalid_memory_extraction_captures";
            var sources = ReadDictionaryList(item, "sources");
            var captures = ReadDictionaryList(item, "capturedMemoryExtractions");
            if (sources.Count > 512 || captures.Count != sources.Count || Json.Serialize(captures).Length > 8 * 1024 * 1024)
                return "incomplete_memory_extraction_captures";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capture in captures)
            {
                string id = ReadString(capture, "sourceId", "");
                var source = sources.SingleOrDefault(s => ReadString(s, "sourceId", "") == id);
                if (source == null || !seen.Add(id) || ReadString(capture, "textHash", "") != SemanticSha256Hex(ReadString(source, "text", "")))
                    return "invalid_memory_extraction_source";
                var result = ReadDictionary(capture, "result");
                if (result == null || !result.TryGetValue("ok", out var ok) || ok is not bool)
                    return "invalid_memory_extraction_result";
                if (!ReadBool(result, "ok", false)) continue; // Retain real failures; ordinary indexing still runs.
                if (ReadString(result, "modelHash", "") != modelHash || ReadString(result, "sourceId", "") != id
                    || ReadString(result, "textHash", "") != ReadString(capture, "textHash", "")
                    || ReadString(result, "schema", "") != MemorySpecialistSchema || ReadString(result, "offsetEncoding", "") != "utf16"
                    || !LateInteractionObjectList(result, "entities") || ReadDictionaryList(result, "entities").Count > 128)
                    return "invalid_memory_extraction_identity";
            }
            return "";
        }

        private static Dictionary<string, object> RebindMemoryExtractionReplay(Dictionary<string, object> capture, string mappedSourceId, string modelHash)
        {
            var captured = ReadDictionary(capture, "result");
            // Match the runtime's model gate, including failures without a model identity.
            if (ReadString(captured, "modelHash", "") != modelHash) return null;
            var result = VerificationService.CloneDictionary(captured);
            result["sourceId"] = mappedSourceId;
            return result;
        }

        private static int EvidenceReplayOrdinaryTokens(Dictionary<string, object> input)
        {
            if (!input.TryGetValue("memoryPrecisionOrdinaryTokens", out var raw)) return 4000;
            if (raw is not (int or long) || ReadLong(input, "memoryPrecisionOrdinaryTokens", 0) is < 2000 or > 8000)
                throw new InvalidOperationException("Replay memoryPrecisionOrdinaryTokens must be an integer from 2000 through 8000.");
            return ReadInt(input, "memoryPrecisionOrdinaryTokens", 4000);
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static Dictionary<string, object> RunMemoryEvidenceReplay()
        {
            if (Program.ActiveServerPort > 0 || !Program.HasArg(Environment.GetCommandLineArgs(), "--run-verification")
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Evidence replay requires the isolated artifact-bound Verification Lab CLI.");
            string path = MemoryReplayInputPath("evidence-input.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new InvalidOperationException("Stage bounded memory-accuracy/evidence-input.json beside the validated artifact.");
            var input = CampaignFileService.ReadJsonObject(path);
            if (ReadString(input, "schema", "") == DialogueAttributionReplaySchema)
                return RunDialogueAttributionReplay(input, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            if (ReadString(input, "schema", "") == "reign-native-archive-contracts-v1")
                return RunValidationNativeArchiveContracts(input, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            var publication = ReadString(input, "schema", "") != SceneFormationReplaySchema
                && ReadDictionaryList(input, "cases").Any(c => c.ContainsKey("publishedPartyViewSha256"))
                ? ReadPublishedPartyReplayFile(path) : null;
            var report = ReplayMemoryEvidenceFixture(input, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), publication);
            string resultPath = Path.Combine(Program.TestsDir, "memory-accuracy", "evidence-result-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            CampaignFileService.WriteJsonObject(resultPath, report);
            return TestDict("ok", report["ok"], "resultPath", resultPath, "completedCases", report["completedCases"],
                "fullyReplayedCases", report["fullyReplayedCases"], "inputSha256", report["inputSha256"], "providerCalls", 0, "certificateEligible", false);
        }
#endif

#if !REIGN_EXCLUDE_VERIFICATION
        private static Dictionary<string, object> ReplayMemoryEvidenceFixture(Dictionary<string, object> input, string inputHash,
            byte[] publication = null)
        {
            if (Program.ActiveServerPort > 0 || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Fixture replay requires non-listening isolated Verification Lab state.");
            bool formationOnly = ReadString(input, "schema", "") == SceneFormationReplaySchema;
            if (formationOnly) input = SceneFormationEvidenceManifest(input);
            int ordinaryTokens = EvidenceReplayOrdinaryTokens(input);
            string denseError = DenseReplayConfigError(input);
            if (denseError.Length > 0) throw new InvalidOperationException(denseError);
            var denseContracts = DenseReplayContracts();
            if (denseContracts.Any(c => !ReadBool(c, "passed", false)))
                throw new InvalidOperationException("Dense replay isolation contracts failed.");
            foreach (string flag in new[] { "analystEnabled", "indexSpecialistSources", "captureLateInteraction", "enableLocalMemoryExtractor", "captureMemorySelection", "enableMemoryEvidenceCompression" })
                if (input.TryGetValue(flag, out var value) && value is not bool)
                    throw new InvalidOperationException("Replay " + flag + " must be a boolean.");
            if (input.TryGetValue("memorySelectionBackend", out var selectionBackend)
                && (selectionBackend is not string || !new[] { "cpu", "cuda" }.Contains((string)selectionBackend)))
                throw new InvalidOperationException("Replay memorySelectionBackend must be cpu or cuda.");
            if (ReadBool(input, "analystEnabled", false))
                throw new InvalidOperationException("Remote memory analysis is retired. Use local evidence replay.");
            bool indexSpecialists = ReadBool(input, "indexSpecialistSources", false);
            bool captureLateInteraction = ReadBool(input, "captureLateInteraction", false);
            bool extractorEnabled = ReadBool(input, "enableLocalMemoryExtractor", false);
            bool captureSelection = ReadBool(input, "captureMemorySelection", false);
            string extractorModelHash = ReadString(input, "extractorModelHash", "");
            if (extractorEnabled && !indexSpecialists) throw new InvalidOperationException("Extractor replay requires indexSpecialistSources.");
            string precisionMode = ReadString(input, "precisionMode", "shadow");
            if (!new[] { "shadow", "legacy", "precision" }.Contains(precisionMode))
                throw new InvalidOperationException("Invalid replay precisionMode.");
            var cases = ReadDictionaryList(input, "cases");
            if (ReadString(input, "schema", "") != "reign-memory-evidence-replay-input-v1" || cases.Count < 1 || cases.Count > 256
                || cases.Select(c => ReadString(c, "caseId", "")).Distinct(StringComparer.Ordinal).Count() != cases.Count)
                throw new InvalidOperationException("Invalid evidence replay manifest.");
            bool published = cases.Any(c => c.ContainsKey("publishedPartyViewSha256"));
            if (published != (publication != null) || published && (cases.Count != 1 || precisionMode != "precision"
                || input.ContainsKey("denseWorkerReplay") || captureLateInteraction || captureSelection || indexSpecialists))
                throw new InvalidOperationException("Published party view requires one initial precision case and its detached receipt.");
            if (cases.Any(c => c.ContainsKey("durableReadReplay")) && (cases.Count != 1 || precisionMode != "precision"
                || input.ContainsKey("denseWorkerReplay") || captureLateInteraction || captureSelection || indexSpecialists))
                throw new InvalidOperationException("Durable read replay requires one isolated precision case.");
            foreach (var item in cases)
            {
                string error = MemoryEvidenceReplayInputError(item);
                if (error.Length > 0) throw new InvalidOperationException(ReadString(item, "caseId", "") + ": " + error);
                if (item.ContainsKey("capturedMemorySelections") && !captureSelection
                    || ReadDictionaryList(item, "capturedMemorySelections").Count > 16)
                    throw new InvalidOperationException("Local selection captures require captureMemorySelection and at most 16 exact requests per case.");
                error = LateInteractionReplayInputError(item);
                if (error.Length > 0 || item.ContainsKey("capturedLateInteractionSearches") && !captureLateInteraction)
                    throw new InvalidOperationException(ReadString(item,"caseId","")+": "+FirstNonEmpty(error,"colbert_capture_not_enabled"));
                error = extractorEnabled ? MemoryExtractionReplayInputError(item, extractorModelHash)
                    : item.ContainsKey("capturedMemoryExtractions") ? "memory_extractor_capture_not_enabled" : "";
                if (error.Length > 0) throw new InvalidOperationException(ReadString(item, "caseId", "") + ": " + error);
            }
            using var campaignLease = new EvidenceReplayCampaignLease(input);
            string campaign = campaignLease.Campaign;
            var denseWorker = input.ContainsKey("denseWorkerReplay") ? new DenseReplayWorker(input) : null;
            var priorLate = LateInteractionReplay.Value;
            var priorLateAudit = LateInteractionReplayAudit.Value;
            var priorSelection = MemorySelectionReplay.Value;
            var results = new List<Dictionary<string, object>>();
            var denseHistoryScopes = new Dictionary<string, int>();
            try
            {
                for (int index = 0; index < cases.Count && !VerificationService.ShouldCancelVerification(); index++)
                {
                    var item = cases[index];
                    using var referenceMode = new EvidenceReplayReferenceModeScope(ReadDictionary(item, "writerReplay"));
                    int sourceScopeIndex = index;
                    bool seedHistory = true;
                    if (denseWorker != null)
                    {
                        string key = DenseReplayScopeKey(item);
                        if (denseHistoryScopes.TryGetValue(key, out int priorScope))
                        { sourceScopeIndex = priorScope; seedHistory = false; }
                        else denseHistoryScopes[key] = index;
                    }
                    string timeline = "replay_" + sourceScopeIndex;
                    string Map(string value) => sourceScopeIndex + ":" + value;
                    string observer = Map(ReadString(item, "observerId", "")), peer = Map(ReadString(item, "peerId", ""));
                    var sources = ReadDictionaryList(item, "sources").OrderBy(s => ReadLong(s, "sequence", 0)).ToList();
                    var context = EvidenceReplayMemoryContext(item, timeline, peer);
                    if (formationOnly) context.Remove("memoryQueryText");
                    var publishedParty = published ? ReadPublishedPartyReplayView(publication, item, sources, campaign, timeline, Map) : null;
                    var indexedSources = new List<Dictionary<string, object>>();
                    var extractionCaptures = ReadDictionaryList(item, "capturedMemoryExtractions")
                        .ToDictionary(c => ReadString(c, "sourceId", ""), StringComparer.Ordinal);
                    var lateViews = new List<Dictionary<string, object>>();
                    var indexingClock = System.Diagnostics.Stopwatch.StartNew();
                    if (seedHistory)
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var group in sources.GroupBy(s => ReadString(s, "sessionId", "history")))
                        {
                            string session = Map(group.Key);
                            var participants = group.SelectMany(s => ReadStringList(s, "audience")).Append(ReadString(item, "observerId", ""))
                                .Distinct().Select(Map).ToList();
                            // Turn timestamps already use the supplied sequence.
                            // Preserve those bounds for session chronology too;
                            // constant session times make every archive equally
                            // recent and leave selection dependent on row order.
                            long startTimestamp = group.Min(s => ReadLong(s, "sequence", 0));
                            long endTimestamp = group.Max(s => ReadLong(s, "sequence", 0));
                            DialogueStore.InsertConversationSessions2(connection, TestDict("session", session, "campaign", campaign,
                                "npc", observer, "player", peer, "start", startTimestamp, "end", endTimestamp,
                                "participants", Json.Serialize(participants), "payload", Json.Serialize(context)));
                            int ordinal = 0;
                            foreach (var source in group)
                            {
                                string id = Map(ReadString(source, "sourceId", ""));
                                InsertConversationTurn(connection, id, session, id, ordinal++, Map(ReadString(source, "exchangeId", ReadString(source, "sourceId", ""))),
                                    ReadString(source, "role", ""), Map(ReadString(source, "speakerId", "")), ReadString(source, "speakerName", ReadString(source, "speakerId", "")),
                                    ReadString(source, "text", ""), published ? "party_chat" : "in_person", ReadDouble(source, "worldDay", 0),
                                    ReadLong(source, "sequence", 0), "", ReadStringList(source, "audience").Select(Map).ToList(), context);
                                DialogueStore.UpdateConversationTurns4(connection, TestDict("sequence", ReadLong(source, "sequence", 0), "id", id));
                                if (indexSpecialists)
                                {
                                    var original = DialogueStore.SelectConversationTurns6(connection, TestDict("id", id)).Single();
                                    var captured = extractorEnabled ? extractionCaptures[ReadString(source, "sourceId", "")] : null;
                                    var extraction = captured == null ? null : RebindMemoryExtractionReplay(captured, id, extractorModelHash);
                                    StoreMemorySpecialistDocument(connection, original, extraction);
                                    var indexed = TestDict("sourceId", id, "sourceHash", MemorySpecialistSourceHash(original));
                                    if (extractorEnabled)
                                    {
                                        var stored = DialogueStore.SelectMemorySpecialistSources2(connection, TestDict("id", id)).Single();
                                        indexed["document"] = TryParseJsonObject(ReadString(stored, "payload_json", "{}"));
                                        indexed["publicTopics"] = DialogueService.TextListFromJson(ReadString(stored, "public_topics_json", "[]"));
                                        indexed["publicEntitiesJson"] = ReadString(stored, "public_entities_json", "[]");
                                        indexed["captureSha256"] = PromptHash(CanonicalJson(captured));
                                        indexed["captureOk"] = ReadBool(extraction, "ok", false);
                                    }
                                    indexedSources.Add(indexed);
                                }
                                if(captureLateInteraction)
                                {
                                    var original=DialogueStore.SelectConversationTurns57(connection, TestDict("id",id)).Single();
                                    foreach(var view in new[]{"self","public"}.Select(v=>LateInteractionView(original,v)).Where(v=>v!=null))
                                    {
                                        lateViews.Add(view);
                                        StoreLateInteractionReceipt(connection,original,view,"isolated-capture");
                                    }
                                }
                            }
                        }
                        transaction.Commit();
                    }
                    indexingClock.Stop();
                    if (formationOnly)
                    {
                        results.Add(CaptureSceneFormation(campaign, ReadString(item, "caseId", ""), sourceScopeIndex,
                            sources.Select(source => Map(ReadString(source, "sessionId", ""))).Distinct(StringComparer.Ordinal)));
                        continue;
                    }
                    var requests = new List<Dictionary<string, object>>();
                    var pending = new List<string>();
                    var lateCaptures = new List<Dictionary<string, object>>();
                    var selectionCaptures = new List<Dictionary<string, object>>();
                    MemorySelectionReplay.Value = captureSelection ? request => {
                        string hash = PromptHash(CanonicalJson(request));
                        var capture = ReadDictionaryList(item, "capturedMemorySelections")
                            .SingleOrDefault(c => ReadString(c, "requestSha256", "") == hash);
                        var response = ReadDictionary(capture, "response");
                        selectionCaptures.Add(TestDict("requestSha256", hash, "request", request, "responseSupplied", response != null));
                        if (response == null) pending.Add("selection:" + ReadString(request, "operation", "") + ":" + hash);
                        return response ?? TestDict("ok", false, "status", "captured_selection_pending");
                    } : null;
                    LateInteractionReplayAudit.Value = new();
                    LateInteractionReplay.Value = captureLateInteraction
                        ? request => ReplayLateInteractionSearch(item,request,Map,lateViews,pending,lateCaptures) : null;
                    var semanticDocuments = new List<Dictionary<string, object>>();
                    var settings = TestDict("enableSemanticMemory", false, "enableMinime", false,
                        "memoryPrecisionMode", precisionMode, "memoryPrecisionOrdinaryTokens", ordinaryTokens,
                        "memorySelectionMode", captureSelection ? "replay" : "off",
                        "memorySelectionTimeoutMs", Math.Clamp(ReadInt(input, "memorySelectionTimeoutMs", 15000), 500, 30000),
                        "memorySelectionBackend", ReadString(input, "memorySelectionBackend", "cpu"),
                        "enableMemoryEvidenceCompression", ReadBool(input, "enableMemoryEvidenceCompression", false));
                    Dictionary<string, object> denseIndex = null;
                    if (denseWorker != null)
                    {
                        settings["enableSemanticMemory"] = true;
                        settings["vectorWorkerUrl"] = denseWorker.Endpoint;
                        settings["vectorProvider"] = "local";
                        settings["vectorSearchTopK"] = 96;
                        settings["vectorQueryTimeoutMs"] = 10000;
                        settings["enableMinimeMemoryWorker"] = true;
                        settings["enableMinimeMemoryReranking"] = false;
                        settings["vectorScoreWeight"] = 30;
                        settings["minimeRerankWeight"] = 35;
                        denseIndex = denseWorker.Index(campaign, observer, peer, context, sources, sourceScopeIndex);
                    }
                    if (ReadBool(item, "captureSemanticDocuments", false))
                    {
                        using var captureConnection = ReignDatabase.OpenCampaignConnection(campaign);
                        semanticDocuments = CaptureReplaySemanticDocuments(captureConnection, campaign, observer, peer,
                            context, settings, sources.Select(s => Map(ReadString(s, "sourceId", ""))));
                    }
                    publishedParty?.VerifyStoredSources(context);
                    var transcript = publishedParty?.Transcript() ?? EvidenceReplayRecentSources(item, sources).Select(s => TestDict("turnId", Map(ReadString(s, "sourceId", "")),
                            "speakerId", Map(ReadString(s, "speakerId", "")), "speaker", ReadString(s, "speakerName", ReadString(s, "speakerId", "")),
                            "role", ReadString(s, "role", ""), "text", ObserverConversationText(ReadString(s, "text", ""),
                                Map(ReadString(s, "speakerId", "")), observer, ReadString(s, "role", "") == "player"))).ToList();
                    SetMemoryTranscriptContext(context, transcript);
                    Dictionary<string, object> packet = null, writer = null;
                    var denseRounds = new List<Dictionary<string, object>>();
                    for (int round = 0; round < (denseWorker?.Rounds ?? 1); round++)
                    {
                        var queryClock = System.Diagnostics.Stopwatch.StartNew();
                        string query = ReadString(item, "query", "");
                        var roundPacket = publishedParty == null ? DialogueService.BuildNpcMemoryPacket(campaign, observer, peer, "", query, 4000, context, settings)
                            : BuildPrecisionMemoryPacket(campaign, observer, peer, "", query, context, settings, false, publishedParty);
                        long packetMs = queryClock.ElapsedMilliseconds;
                        var writerPacket = item.ContainsKey("reviewedFactReplay")
                            ? ProjectReplayReviewedFacts(campaign, observer, peer, item, context, Map, roundPacket, publishedParty)
                            : roundPacket;
                        var roundWriter = ReplayEvidenceWriter(campaign, observer, peer, item, context, settings, writerPacket, transcript,
                            publishedParty: publishedParty, mapSourceId: Map);
                        if (denseWorker != null)
                        {
                            var semantic = ReadDictionary(roundPacket, "semanticRetrieval");
                            if (!ReadBool(semantic, "applied", false))
                                throw new InvalidOperationException("Dense pipeline did not apply actual semantic search: " + ReadString(semantic, "reason", "missing"));
                            long queryMs = queryClock.ElapsedMilliseconds;
                            var workerSample = denseWorker.Post("/vectors/status", TestDict());
                            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                            denseRounds.Add(TestDict("round", round, "cacheCondition", round == 0 ? "first_query_after_index" : "repeated_query",
                                "packetMs", packetMs, "queryAndWriterMs", queryMs,
                                "workerMemory", ReadDictionary(ReadDictionary(workerSample, "denseReplay"), "memory"),
                                "serverResidentBytes", currentProcess.WorkingSet64, "serverPeakBytes", currentProcess.PeakWorkingSet64,
                                "semantic", semantic, "messageHash", PromptHash(CanonicalJson(ReadDictionaryList(ReadDictionary(roundWriter, "request"), "messages")))));
                        }
                        if (round == 0) { packet = roundPacket; writer = roundWriter; }
                    }
                    bool writerComplete = !item.ContainsKey("writerReplay")
                        || EvidenceWriterReplayComplete(writer);
                    results.Add(TestDict("caseId", ReadString(item, "caseId", ""), "sourceScopeIndex", sourceScopeIndex,
                        "inputBounds", MemoryEvidenceReplayInputBounds(item),
                        "referenceGraphAudit", referenceMode.Audit,
                        "requests", requests, "packet", packet, "writerReplay", writer,
                        "specialistIndex", TestDict("enabled", indexSpecialists, "sources", indexedSources,
                            "fixtureAndIndexMs", indexingClock.ElapsedMilliseconds, "localModelCalls", 0,
                            "extractorEnabled", extractorEnabled, "capturedExtractorModelHash", extractorEnabled ? extractorModelHash : ""),
                        "semanticDocuments", semanticDocuments,
                        "lateInteractionReplay",lateCaptures,"lateInteractionAudit",LateInteractionReplayAudit.Value,"precisionMode",precisionMode,
                        "memorySelectionReplay", selectionCaptures, "memoryPrecisionOrdinaryTokens", ordinaryTokens,
                        "commonTranscript", "CURRENT CONTRIBUTION: " + ReadString(item, "query", "") + "\nRECENT DIALOGUE:\n" + DialogueService.FormatEventLinesForPrompt(transcript),
                        "recentTranscriptSourceIds", transcript.Select(t => ReadString(t, "turnId", "")).ToList(),
                        "sourceSetHash", PromptHash(CanonicalJson(sources)), "pendingResponses", pending,
                        "densePipeline", denseWorker == null ? null : TestDict("index", denseIndex, "rounds", denseRounds),
                        "fullyReplayed", pending.Count == 0 && writerComplete));
                }
            }
            finally
            {
                LateInteractionReplay.Value = priorLate; LateInteractionReplayAudit.Value = priorLateAudit;
                MemorySelectionReplay.Value = priorSelection;
            }
            bool completed = results.Count == cases.Count;
            var report = TestDict("schema", "reign-memory-evidence-replay-result-v1", "ok", completed,
                "memoryPrecisionOrdinaryTokens", ordinaryTokens,
                "inputSha256", inputHash, "cases", results,
                "completedCases", results.Count, "fullyReplayedCases", results.Count(r => ReadBool(r, "fullyReplayed", false)),
                "providerCalls", 0, "certificateEligible", false, "replayWorldPolicy", "stable_replay_world_v1", "coverageLimits", new[] {
                    "The isolated world identity is derived from source/query fixtures, excluding captured responses and future contributions. A database advisory lease rejects overlap and any pre-existing world; cleanup removes only the world owned by this invocation. Exact provider request hashes remain authoritative across capture, repair and continuation replays.",
                    "Production local SQL retrieval, source eligibility, ranked allocation and actual writer envelopes; captured writer responses only.",
                    "Optional memoryPrecisionOrdinaryTokens is an integer from2000 through8000, default4000, for isolated precision allocation only. It does not raise production settings, override protected evidence or establish better ranking. Report final source coverage and prompt growth together.",
                    "Optional recentSourceIds names at most30 visible originals in the current transcript; empty models a new conversation. Omitted retains the latest30 visible-source default. Archived eligible sources remain searchable; original chronology and cutoffs are enforced.",
                    "Optional indexSpecialistSources creates source metadata only in the disposable fixture. enableLocalMemoryExtractor imports exact model/source-bound captured extraction results through production validation and storage; it does not dispatch a model, grant qualification or change live settings. Remote analyst and checker stages are retired; legacy analystEnabled=false remains compatible.",
                    "captureLateInteraction exports exact observer-projected documents and requests; supplied pinned local ranks bind to identical query/view/text hashes. Source IDs are rebound to the disposable fixture. No model inference or certification occurs inside this replay. precisionMode may select legacy, shadow or precision for the fixture only.",
                    "captureMemorySelection retains exact source-bound local rank/compression requests. capturedMemorySelections must match the complete canonical request hash; incomplete, changed, malformed or unqualified captures cannot become a production certificate. enableMemoryEvidenceCompression is fixture-only. No local model is dispatched by this replay; run the pinned worker adapter separately and retain its timings, failures and hashes.",
                    "Without denseWorkerReplay no semantic worker executes. Explicit denseWorkerReplay uses only a nonce-scoped high-port loopback worker with verified isolated model identity and source-linked verbatim memory projections; these are not reconstructed saved derivatives. No live provider, full live endpoint repairs, native effects or human review execute. Canonical source eligibility remains authoritative; analyst-only search captures are rejected.",
                    "Optional semanticDocuments use the production document builders on eligible fixture originals, not live campaign data. Separate pinned local model/chunk/index execution is required before claiming semantic search quality.",
                     "Optional writerReplay carries both memory text and its precisionEvidence metadata through production envelopes and final source preflight, then publishes supplied prose/memory layers only in the isolated campaign; no actions or relationship assessments apply. Older replay captures omitting that metadata mixed precision packets with legacy continuity and are not production-parity evidence.",
                     "Up to three optional writer continuations reuse the preceding captured request and persist in the same isolated campaign; missing stages stop that chain and do not certify subsequent recall.",
                    "providerWireCapture retains initial NanoGPT JSON from shared body/policy/cache/reasoning builders using fixture settings. It does not dispatch HTTP, load credentials, reproduce live cooldowns or authorize subscription/paid routing.",
                    "Missing responses remain pending. Independently review semantic claims; execution success is not accuracy." });
            if (formationOnly)
            {
                report["mode"] = "scene_formation_baseline";
                report["coverageLimits"] = new[] { "Native scene summaries from caller-scoped archived sources with generation disabled. The two budgets share exact source options.",
                    "The replay commits and reopens each summary, source link and lexical row inside its disposable world. No retrieval, writer, model, label or provider is used." };
            }
            report["denseReplayContracts"] = denseContracts;
            if (denseWorker != null) report["denseWorker"] = TestDict("initial", denseWorker.InitialStatus,
                "final", denseWorker.Post("/vectors/status", TestDict()), "model", denseWorker.Model,
                "modelTreeSha256", denseWorker.ModelTreeHash);
            return report;
        }
#endif
    }
}
