using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Maintenance only. A reviewed manifest names exact derived rows and their
        // source preconditions. Transcripts, native actions and relationship scores
        // are deliberately outside the mutation vocabulary.
        private static Dictionary<string, object> RepairConversationContinuity(ReignDbConnection connection,
            Dictionary<string, object> manifest)
        {
            if (ReadString(manifest, "schema", "") != "reign-continuity-repair-v1") throw new InvalidOperationException("Unknown continuity repair manifest.");
            string campaign = ReadString(manifest, "campaignId", ""), timeline = ReadString(manifest, "timelineId", "");
            if (campaign.Length == 0 || timeline.Length == 0) throw new InvalidOperationException("Exact campaign and timeline are required.");
            EnsureConversationContinuitySchema(connection);
            DialogueSchema.CreateConversationContinuityRepairs(connection);
            string hash = PromptHash(CanonicalJson(manifest));
            var existing = DialogueStore.SelectConversationContinuityRepairs(connection, new Dictionary<string, object> { ["hash"] = hash }).FirstOrDefault();
            if (existing != null) return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["manifestHash"] = hash };
            if (manifest.ContainsKey("expectedRestoreGeneration"))
            {
                string expectedGeneration = ReadString(manifest, "expectedRestoreGeneration", "");
                if (expectedGeneration == "uninitialized" || expectedGeneration != ReadString(ReadMemoryPrecisionState(connection), "restore_generation", ""))
                    throw new InvalidOperationException("Staging predates the active memory generation; refresh the preview after deployment before applying.");
            }
            var changes = new List<Dictionary<string, object>>();
            foreach (var operation in ReadDictionaryList(manifest, "operations"))
            {
                string kind = ReadString(operation, "kind", ""), id = ReadString(operation, "id", "");
                if (id.Length == 0 || string.IsNullOrWhiteSpace(ReadString(operation, "reviewReason", ""))) throw new InvalidOperationException("Every repair needs an exact source id and review reason.");
                if (kind == "restore_original_input")
                {
                    changes.Add(RestoreReviewedOriginalInput(connection, manifest, operation));
                    continue;
                }
                if (kind == "rebuild_scene_memory")
                {
                    changes.Add(ApplyStagedSceneMemory(connection, manifest, operation));
                    continue;
                }
                if (kind == "set_memory_mode")
                {
                    var beforeState = ReadMemoryPrecisionState(connection);
                    if (id != "current" || CanonicalJson(beforeState) != CanonicalJson(ReadDictionary(operation,"expectedState")))
                        throw new InvalidOperationException("Campaign memory state changed; review a fresh mode transition.");
                    string mode = ReadString(operation,"mode","");
                    if (!new[] { "legacy", "shadow", "precision" }.Contains(mode)) throw new InvalidOperationException("Unknown memory mode.");
                    DialogueStore.UpdateMemoryPrecisionState(connection, TestDict("mode",mode,"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    DialogueStore.DeleteMemoryProjections(connection);
                    changes.Add(TestDict("kind",kind,"before",beforeState,"after",ReadMemoryPrecisionState(connection),"databaseRewound",false));
                    continue;
                }
                string table, key;
                switch (kind)
                {
                    case "scope_private_assertion": table = "temporal_knowledge_assertions"; key = "assertion_id"; break;
                    case "recover_agenda": table = "conversation_turns"; key = "turn_id"; break;
                    case "quarantine_memory": table = "memories"; key = "memory_id"; break;
                    case "quarantine_summary": table = "summaries"; key = "summary_id"; break;
                    case "quarantine_belief": table = "beliefs"; key = "belief_id"; break;
                    case "quarantine_comprehension": table = "comprehension"; key = "comprehension_id"; break;
                    default: throw new InvalidOperationException("Unsupported continuity repair operation.");
                }
                var before = DialogueStore.SelectDynamic2(connection, table, key,TestDict("id", id)).SingleOrDefault();
                if (before == null) throw new InvalidOperationException("Repair source is missing: " + id);
                var expected = ReadDictionary(operation, "expected") ?? new Dictionary<string, object>();
                string requiredText = kind == "recover_agenda" ? "text" : kind == "scope_private_assertion" || kind == "quarantine_belief" ? "claim" : kind == "quarantine_memory" || kind == "quarantine_summary" ? "summary" : "text";
                if (!expected.ContainsKey(requiredText) || expected.Count < 3) throw new InvalidOperationException("Repair preconditions must include exact source text and provenance.");
                foreach (var field in expected)
                {
                    if (!before.TryGetValue(field.Key, out var actual)
                        || !string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), Convert.ToString(field.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal))
                        throw new InvalidOperationException("Repair source changed: " + id + " / " + field.Key);
                }
                if (kind == "scope_private_assertion")
                {
                    string owner = ReadString(before, "perspective_owner_id", ""), mentalKind = ReadString(before, "assertion_kind", "");
                    if (!IsPrivateMentalLayer(mentalKind) || owner.Length == 0 || ReadString(before, "campaign_id", "") != campaign)
                        throw new InvalidOperationException("Only a proven owner's private mental assertion can be re-scoped.");
                    DialogueStore.UpdateTemporalKnowledgeAssertions(connection, TestDict("known", Json.Serialize(new[] { owner }), "truth", mentalKind == "belief" ? "believed" : "interpretation", "id", id));
                }
                else if (kind == "recover_agenda")
                {
                    string owner = ReadString(operation, "ownerId", "");
                    if (ReadString(before, "role", "") != "npc" || ReadString(before, "speaker_id", "") != owner || ReadString(before, "status", "") != "active")
                        throw new InvalidOperationException("An agenda can only be recovered from that NPC's own accepted turn.");
                    string latest = ReadString(DialogueStore.SelectConversationTurns19(connection, TestDict("owner", owner)).FirstOrDefault(), "turn_id", "");
                    if (ReadString(operation, "expectedOwnerLatestTurnId", "") != latest)
                        throw new InvalidOperationException("The NPC has spoken since the agenda review; inspect later resolution or changed intent before repair.");
                    var write = ReadDictionary(operation, "write") ?? new Dictionary<string, object>();
                    if (ReadString(write, "kind", "") != "agenda" || ContinuityClosed(ReadString(write, "status", "open")))
                        throw new InvalidOperationException("Recovery only creates an unresolved personal agenda; it cannot manufacture completion.");
                    var receipt = StoreAcceptedConversationContinuity(connection,
                        TestDict("timelineId", timeline, "sceneTurnId", id, "worldDay", ReadDouble(before, "world_day", 0d)), owner,
                        ReadString(operation, "peerId", ""), "repair_" + hash, ReadString(before, "text", ""),
                        new List<Dictionary<string, object>> { write }, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (ReadDictionaryList(receipt, "rejected").Count > 0) throw new InvalidOperationException("Recovered agenda failed accepted-source validation: " + Json.Serialize(receipt));
                    changes.Add(TestDict("kind", kind, "sourceId", id, "before", before, "after", receipt));
                    continue;
                }
                else
                {
                    // Retain the row and its original text. Retrieval quarantines the
                    // reviewed derivative without rewriting what anybody actually said.
                    var body = TryParseJsonObject(ReadString(before, "payload_json", "{}")) ?? new Dictionary<string, object>();
                    body["continuityQuarantined"] = true;
                    body["continuityRepair"] = TestDict("manifestHash", hash, "reason", ReadString(operation, "reviewReason", ""),
                        "evidenceSourceIds", ReadStringList(operation, "evidenceSourceIds"));
                    if (ReadStringList(operation, "evidenceSourceIds").Count == 0) throw new InvalidOperationException("Quarantine requires reviewed contrary source identifiers.");
                    foreach (string sourceId in ReadStringList(operation, "evidenceSourceIds"))
                        if (DialogueStore.SelectConversationTurns20(connection, TestDict("id", sourceId)).Count == 0)
                            throw new InvalidOperationException("Quarantine's reviewed accepted source is missing: " + sourceId);
                    DialogueStore.UpdateDynamic(connection, table, key,TestDict("payload", Json.Serialize(body), "id", id));
                }
                var after = DialogueStore.SelectDynamic2(connection, table, key,TestDict("id", id)).Single();
                changes.Add(TestDict("kind", kind, "sourceId", id, "before", before, "after", after));
            }
            var result = TestDict("ok", true, "manifestHash", hash, "campaignId", campaign, "timelineId", timeline,
                "changes", changes, "nativeActionsReplayed", false, "relationshipScoresChanged", false, "transcriptsChanged", false);
            DialogueStore.InsertConversationContinuityRepairs(connection, TestDict("hash", hash, "campaign", campaign, "timeline", timeline, "receipt", Json.Serialize(result), "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return result;
        }

        private static string ContinuityFileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        internal static Dictionary<string, object> RunConversationContinuityRepairCli(string[] args)
        {
            string path = Path.GetFullPath(ArgValue(args, "--manifest", ""));
            var manifest = CampaignFileService.ReadJsonObject(path);
            bool apply = Program.HasArg(args, "--apply");
            string campaign = ReadString(manifest, "campaignId", ""), timeline = ReadString(manifest, "timelineId", "");
            string hash = ContinuityFileHash(path);
            if (apply)
            {
                if (Environment.GetEnvironmentVariable("REIGN_CONTINUITY_MAINTENANCE") != "1"
                    || !hash.Equals(ArgValue(args, "--confirmed-manifest-sha256", ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Application requires maintenance mode and the explicitly reviewed manifest hash.");
                string backup = ArgValue(args, "--backup", "");
                if (!File.Exists(backup) || !ContinuityFileHash(backup).Equals(ArgValue(args, "--backup-sha256", ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A verified campaign backup is required before repair.");
                // The offline wrapper directs incidental files to evidence. Read the stopped
                // managed service's journals before opening its existing database.
                string campaignsRoot = ReignPostgreSqlStorage.Options.IsValidation
                    ? CampaignFileService.CampaignsRoot() : "/var/www/html/ReignServer/data/campaigns";
                PersistenceService.RequireOfflineSaveSyncCampaignAvailable(campaign, campaignsRoot);
            }
            // Open an existing registered schema directly. Do not run server startup,
            // schema migration, background work, provider calls or campaign registration.
            using (var connection = new Npgsql.NpgsqlConnection(ReignPostgreSqlStorage.Options.BuildConnectionString()))
            {
                connection.Open();
                string schema = ReadString(DialogueStore.SelectCampaignRegistry(connection, TestDict("campaign", campaign)).SingleOrDefault(), "schema_name", "");
                if (schema != ReignPostgreSqlStorage.CampaignSchemaName(campaign)) throw new InvalidOperationException("Campaign registration is missing or mismatched.");
                using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable))
                {
                    if (Program.HasArg(args, "--stage")) DialogueStore.Set(connection);
                    DialogueStore.Set2(connection, schema);
                    var active = DialogueStore.SelectWorldHistoryTimelines(connection, TestDict("campaign", campaign));
                    if (active.Count != 1 || ReadString(active[0], "timeline_id", "") != timeline) throw new InvalidOperationException("Active timeline changed; refresh the reviewed manifest.");
                    bool stage = Program.HasArg(args, "--stage");
                    if (stage && apply) throw new InvalidOperationException("Staging and applying are separate operations.");
                    bool originalsAudit = ReadString(manifest, "schema", "") == "reign-conversation-original-audit-v1";
                    if (stage && !originalsAudit && ReadString(manifest, "schema", "") != "reign-memory-stage-request-v1") throw new InvalidOperationException("A memory or original-input staging request is required.");
                    var result = stage ? originalsAudit ? BuildConversationOriginalAudit(connection, manifest) : BuildMemoryStagingManifest(connection, manifest) : RepairConversationContinuity(connection, manifest);
                    result["mode"] = stage ? "staged_read_only" : apply ? "applied" : "preview_rolled_back";
                    result["manifestFileSha256"] = hash;
                    string output = Path.GetFullPath(ArgValue(args, "--report", path + ".receipt.json"));
                    if (apply) transaction.Commit(); else transaction.Rollback();
                    File.WriteAllText(output, Json.Serialize(result));
                    return TestDict("ok", true, "mode", result["mode"], "report", output, "manifestFileSha256", hash);
                }
            }
        }
    }
}
