using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
#if !REIGN_EXCLUDE_VERIFICATION
using System.Text.Json.Nodes;
#endif

namespace ReignBetaServer
{
    // Format and comparison only. A match says nothing about the file's owner or how
    // the witness was obtained. No runtime caller may supply this witness from JSON.
    internal static class OperatorCurrentAdmissionEvidence
    {
        internal const string FutureProtectedDirectory = "/var/lib/reignserver/campaign-test-grants";
        private const string Schema = "reign-operator-current-admission-v1";
        private const int MaxRecordBytes = 8192;
        private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(2);
        private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        internal sealed record CurrentScope(
            string TaskId, string CampaignTestRunId, string CampaignId, string TimelineId, string CheckpointAttemptId,
            string BaselineSaveName, string BaselineSha256, string CurrentSaveName, string CurrentSha256, string SavePointId,
            string GameInstanceId, int NativeProcessId, string NativeProcessNonce, string LoadSessionSha256,
            string RestoreGenerationSha256, string Origin, string ServerProcessNonce, string ServerModuleId, string ProviderGuardSha256);

        internal sealed record AssignmentScope(
            string Fixture, string Stage, string CampaignTestRunId, string CampaignId, string GameInstanceId,
            string NativeProcessNonce, string ServerProcessNonce,
            string LiveRunId, string CommandId, string LeaseId, int RetryGeneration);

        internal sealed record CurrentWitness(CurrentScope Scope, bool CommittedCheckpointVerified,
            bool LoadedPausedCurrentVerified, bool ProviderFreeVerified);
        // The accepting authority must retain CurrentAtAcceptance with the lease.
        // Rebuilding it from the live Current would erase the accepted checkpoint boundary.
        internal sealed record LeaseWitness(AssignmentScope Scope, CurrentScope CurrentAtAcceptance,
            bool AcceptedAndStillCurrent, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);

        internal static bool Matches(ReadOnlyMemory<byte> utf8, string grantId, CurrentWitness current,
            LeaseWitness lease, DateTimeOffset nowUtc)
        {
            if (utf8.Length is 0 or > MaxRecordBytes || !Nonce(grantId) || nowUtc.Offset != TimeSpan.Zero
                || current is not { CommittedCheckpointVerified: true, LoadedPausedCurrentVerified: true, ProviderFreeVerified: true }
                || lease is not { AcceptedAndStillCurrent: true } || !ValidCurrent(current.Scope)
                || lease.CurrentAtAcceptance != current.Scope
                || !ValidAssignment(lease.Scope, lease.CurrentAtAcceptance)
                || lease.IssuedUtc.Offset != TimeSpan.Zero || lease.ExpiresUtc.Offset != TimeSpan.Zero
                || lease.IssuedUtc > nowUtc || nowUtc >= lease.ExpiresUtc)
                return false;

            try
            {
                using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 3 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6
                    || !root.TryGetProperty("schema", out var schema) || Text(schema) != Schema
                    || !root.TryGetProperty("grantId", out var id) || Text(id) != grantId
                    || !root.TryGetProperty("issuedUtc", out var issuedNode) || !Utc(issuedNode, out var issued)
                    || !root.TryGetProperty("expiresUtc", out var expiryNode) || !Utc(expiryNode, out var expires)
                    || issued < lease.IssuedUtc || issued > nowUtc || expires > lease.ExpiresUtc
                    || nowUtc >= expires || expires - issued > MaxLifetime
                    || !root.TryGetProperty("current", out var currentNode)
                    || !root.TryGetProperty("assignment", out var assignmentNode))
                    return false;

                // The count and the six required lookups above reject extra and duplicate members.
                return ExactFlatScope(currentNode, JsonSerializer.SerializeToElement(current.Scope, Wire))
                    && ExactFlatScope(assignmentNode, JsonSerializer.SerializeToElement(lease.Scope, Wire));
            }
            catch (JsonException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                // JsonElement can reject malformed UTF-8 or a malformed escaped string when reading it.
                return false;
            }
        }

        private static bool ExactFlatScope(JsonElement actual, JsonElement expected)
        {
            if (actual.ValueKind != JsonValueKind.Object
                || actual.EnumerateObject().Count() != expected.EnumerateObject().Count()) return false;
            foreach (var field in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(field.Name, out var value) || value.ValueKind != field.Value.ValueKind)
                    return false;
                if (value.ValueKind == JsonValueKind.String ? value.GetString() != field.Value.GetString()
                    : value.GetRawText() != field.Value.GetRawText())
                    return false;
            }
            return true;
        }

        private static bool ValidCurrent(CurrentScope proof) =>
            proof != null && Nonce(proof.TaskId) && Identifier(proof.CampaignTestRunId, 128)
            && Identifier(proof.CampaignId, 180) && Identifier(proof.TimelineId, 512)
            && Identifier(proof.CheckpointAttemptId, 128) && Nonce(proof.SavePointId)
            && Identifier(proof.CurrentSaveName, 64) && proof.CurrentSaveName.StartsWith("ReignTest_", StringComparison.Ordinal)
            && proof.CurrentSaveName.EndsWith("_Current", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(proof.BaselineSaveName) && proof.BaselineSaveName.Length <= 120
            && proof.BaselineSaveName == proof.BaselineSaveName.Trim()
            && proof.BaselineSaveName.All(ch => !char.IsControl(ch) && ch is not ('/' or '\\' or ':'))
            && !string.Equals(proof.BaselineSaveName, proof.CurrentSaveName, StringComparison.OrdinalIgnoreCase)
            && Identifier(proof.GameInstanceId, 128) && proof.NativeProcessId > 0 && Nonce(proof.NativeProcessNonce)
            && Digest(proof.BaselineSha256) && Digest(proof.CurrentSha256) && Digest(proof.LoadSessionSha256)
            && Digest(proof.RestoreGenerationSha256) && Digest(proof.ProviderGuardSha256)
            && Nonce(proof.ServerProcessNonce)
            && Guid.TryParseExact(proof.ServerModuleId, "D", out Guid module) && module != Guid.Empty
            && module.ToString("D") == proof.ServerModuleId
            && Uri.TryCreate(proof.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback
            && origin.Scheme == Uri.UriSchemeHttp && origin.UserInfo.Length == 0
            && origin.GetLeftPart(UriPartial.Authority) == proof.Origin;

        private static bool ValidAssignment(AssignmentScope assignment, CurrentScope current) =>
            assignment != null
            && ((assignment.Fixture == "world_political_pressure_recovery_test" && assignment.Stage is "direction" or "committed")
                || (assignment.Fixture == "RB-NATIVE-004" && assignment.Stage is "prepare" or "verify"))
            && assignment.CampaignId == current.CampaignId && assignment.GameInstanceId == current.GameInstanceId
            && assignment.CampaignTestRunId == current.CampaignTestRunId
            && assignment.NativeProcessNonce == current.NativeProcessNonce
            && assignment.ServerProcessNonce == current.ServerProcessNonce
            && Identifier(assignment.LiveRunId, 128) && Identifier(assignment.CommandId, 128)
            && Nonce(assignment.LeaseId) && assignment.RetryGeneration >= 0;

        private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool Utc(JsonElement element, out DateTimeOffset value)
        {
            string text = Text(element);
            return DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
                && value.Offset == TimeSpan.Zero && text == value.ToString("O", CultureInfo.InvariantCulture);
        }

        private static bool Nonce(string value) =>
            Guid.TryParseExact(value, "N", out Guid id) && id != Guid.Empty && id.ToString("N") == value;

        private static bool Identifier(string value, int max) =>
            value is { Length: > 0 } && value.Length <= max && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

        private static bool Digest(string value) =>
            value?.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

#if !REIGN_EXCLUDE_VERIFICATION
        internal static void VerifyFormat(Action<string, bool, string> add)
        {
            const string grant = "0123456789abcdef0123456789abcdef";
            const string nonce = "123456789abcdef0123456789abcdef0";
            string digest = new('a', 64);
            var currentScope = new CurrentScope(TaskId: nonce, CampaignTestRunId: "owned-run", CampaignId: "campaign",
                TimelineId: "main", CheckpointAttemptId: "checkpoint_attempt", BaselineSaveName: "NewTest",
                BaselineSha256: new string('b', 64), CurrentSaveName: "ReignTest_adapter_Current",
                CurrentSha256: digest, SavePointId: nonce, GameInstanceId: "game-current",
                NativeProcessId: 456, NativeProcessNonce: nonce, LoadSessionSha256: digest, RestoreGenerationSha256: digest,
                Origin: "http://127.0.0.1:8089", ServerProcessNonce: nonce,
                ServerModuleId: "12345678-9abc-def0-1234-56789abcdef0", ProviderGuardSha256: digest);
            var assignment = new AssignmentScope("RB-NATIVE-004", "prepare", "owned-run", "campaign", "game-current", nonce, nonce,
                "owned-live-run", "owned-command", nonce, 0);
            var current = new CurrentWitness(currentScope, true, true, true);
            var issued = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            var lease = new LeaseWitness(assignment, currentScope, true, issued, issued.AddMinutes(3));
            var now = issued.AddSeconds(20);

            byte[] Record(AssignmentScope scope, Action<JsonObject> edit = null, CurrentScope checkpoint = null)
            {
                var value = new JsonObject
                {
                    ["schema"] = Schema, ["grantId"] = grant,
                    ["issuedUtc"] = issued.ToString("O", CultureInfo.InvariantCulture),
                    ["expiresUtc"] = issued.AddSeconds(60).ToString("O", CultureInfo.InvariantCulture),
                    ["current"] = JsonSerializer.SerializeToNode(checkpoint ?? currentScope, Wire),
                    ["assignment"] = JsonSerializer.SerializeToNode(scope, Wire)
                };
                edit?.Invoke(value);
                return JsonSerializer.SerializeToUtf8Bytes(value);
            }

            byte[] valid = Record(assignment);
            bool Check(byte[] bytes) => Matches(bytes, grant, current, lease, now);
            var d5 = assignment with { Fixture = "world_political_pressure_recovery_test", Stage = "committed" };
            add("operator_current_evidence_exact_checkpoint_and_lease",
                Check(valid) && Matches(Record(d5), grant, current, lease with { Scope = d5 }, now),
                "Both fixed fixture families accept the same exact committed Current and accepted lease contract.");

            add("operator_current_evidence_requires_independent_current_and_lease",
                !Matches(valid, grant, current with { CommittedCheckpointVerified = false }, lease, now)
                && !Matches(valid, grant, current with { LoadedPausedCurrentVerified = false }, lease, now)
                && !Matches(valid, grant, current with { ProviderFreeVerified = false }, lease, now)
                && !Matches(valid, grant, current, lease with { AcceptedAndStillCurrent = false }, now)
                && !Check(Record(assignment, value => value["current"]["loadSessionSha256"] = new string('c', 64)))
                && !Check(Record(assignment, value => value["current"]["restoreGenerationSha256"] = new string('c', 64)))
                && !Check(Record(assignment, value => value["current"]["currentSha256"] = new string('c', 64)))
                && !Check(Record(assignment, value => value["current"]["savePointId"] = grant))
                && !Check(Record(assignment, value => value["current"]["nativeProcessId"] = 457))
                && !Check(Record(assignment, value => value["current"]["providerGuardSha256"] = new string('c', 64)))
                && !Check(Record(assignment with { RetryGeneration = 1 }))
                && !Check(Record(assignment with { LeaseId = grant }))
                && !Matches(Record(assignment with { CampaignTestRunId = "other-run" }), grant, current,
                    lease with { Scope = assignment with { CampaignTestRunId = "other-run" } }, now)
                && !Matches(Record(assignment with { ServerProcessNonce = grant }), grant, current,
                    lease with { Scope = assignment with { ServerProcessNonce = grant } }, now),
                "Changed checkpoint, restore owner, process, provider evidence and reissued lease each independently hold the record.");

            var replacement = currentScope with
            {
                TimelineId = "replacement-timeline",
                CheckpointAttemptId = "replacement-attempt",
                SavePointId = grant,
                CurrentSha256 = new string('c', 64),
                LoadSessionSha256 = new string('d', 64),
                RestoreGenerationSha256 = new string('e', 64)
            };
            bool OldLeaseRejects(CurrentScope changed) =>
                !Matches(Record(assignment, checkpoint: changed), grant, current with { Scope = changed }, lease, now);
            var freshAssignment = assignment with { LeaseId = grant, RetryGeneration = assignment.RetryGeneration + 1 };
            var freshLease = lease with { Scope = freshAssignment, CurrentAtAcceptance = replacement };
            add("operator_current_evidence_accepted_lease_holds_replaced_current",
                !Matches(valid, grant, current, lease with { CurrentAtAcceptance = null }, now)
                && OldLeaseRejects(currentScope with { TimelineId = replacement.TimelineId })
                && OldLeaseRejects(currentScope with { CheckpointAttemptId = replacement.CheckpointAttemptId })
                && OldLeaseRejects(currentScope with { SavePointId = replacement.SavePointId })
                && OldLeaseRejects(currentScope with { CurrentSha256 = replacement.CurrentSha256 })
                && OldLeaseRejects(currentScope with { LoadSessionSha256 = replacement.LoadSessionSha256 })
                && OldLeaseRejects(currentScope with { RestoreGenerationSha256 = replacement.RestoreGenerationSha256 })
                && OldLeaseRejects(replacement)
                && Matches(Record(freshAssignment, checkpoint: replacement), grant,
                    current with { Scope = replacement }, freshLease, now),
                "A coherent replacement Current in the same game process requires its own accepted checkpoint-bound lease.");

            string serialized = System.Text.Encoding.UTF8.GetString(valid);
            byte[] duplicate = System.Text.Encoding.UTF8.GetBytes(
                serialized.Insert(1, "\"schema\":\"outerDuplicate\","));
            byte[] duplicateScope = System.Text.Encoding.UTF8.GetBytes(
                serialized.Replace("\"current\":{", "\"current\":{\"campaignId\":\"innerDuplicate\",",
                    StringComparison.Ordinal));
            add("operator_current_evidence_refuses_expiry_unknown_shape_and_ids",
                !Matches(valid, grant, current, lease, issued.AddSeconds(60))
                && !Matches(valid, grant, current, lease with { ExpiresUtc = issued.AddSeconds(30) }, now)
                && !Check(Record(assignment, value => value["expiresUtc"] = issued.AddMinutes(3).ToString("O", CultureInfo.InvariantCulture)))
                && !Check(Record(assignment, value => value["issuedUtc"] = now.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture)))
                && !Check(Record(assignment, value => value["grantId"] = "../outside"))
                && !Check(Record(assignment, value => value["current"]["taskId"] = Guid.ParseExact(nonce, "N").ToString("D")))
                && !Check(Record(assignment, value => value["issuedUtc"] = issued.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)))
                && !Check(Record(assignment, value => value["issuedUtc"] = issued.ToOffset(TimeSpan.FromHours(-6)).ToString("O", CultureInfo.InvariantCulture)))
                && !Check(Record(assignment, value => value["current"]["baselineSaveName"] = currentScope.CurrentSaveName))
                && !Check(Record(assignment, value => value["current"]["extra"] = "unknown"))
                && !Check(Record(assignment with { Stage = "arbitrary" }))
                && !Check(duplicate) && !Check(duplicateScope)
                && !Check(System.Text.Encoding.UTF8.GetBytes(serialized.Replace("owned-run", "\\uD800", StringComparison.Ordinal)))
                && !Check(new byte[MaxRecordBytes + 1]),
                "An expired, overlong, forged-shape, baseline-conflicting or noncanonical fixture record cannot match.");
        }
#endif
    }
}
