using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reign.Core.Contracts.Actions
{
    /// <summary>
    /// Opt-in durable terminal action report (v1). The server stores the exact
    /// report string, binds it to one restore generation and stages at most one
    /// native relationship receipt. Acceptance attests that evidence and receipt
    /// preparation only; it does not certify every downstream projection.
    /// </summary>
    public static class TerminalActionReportProtocol
    {
        public const string RequestSchema = "reign-terminal-action-report-v1";
        public const string ResponseSchema = "reign-terminal-action-report-ack-v1";
        public const string MarkerSchema = "reign-terminal-action-report-marker-v1";
        public const string PreflightSchema = "reign-terminal-action-report-preflight-v1";
        // GET (read-only preflight) and POST (terminal report) share this exact path.
        public const string ProposedRoute = "/actions/terminal-report/v1";
        // Additive /health field whose value is RequestSchema once the route is wired.
        public const string HealthCapabilityField = "terminalActionReportProtocol";

        // Opt-in recovery of an exact report captured under an earlier restore generation.
        // The v1 envelope (generation, reportJson and SHA) is unchanged; only schema and the
        // sibling recovery/loadProof objects differ. An older server refuses it as invalid.
        public const string RecoverySchema = "reign-terminal-action-report-recovery-v1";
        public const string RecoveryHealthCapabilityField = "terminalActionReportRecovery";
        public const string RecoveryField = "recovery";
        // {loadSessionId, savePointId, restoreGeneration} echoed by a verified Save Sync load.
        // A sibling of the envelope: never part of reportJson or its SHA.
        public const string LoadProofField = "loadProof";
        // Mutable marker progress for a recovered report: {generation, savePointId, loadSessionId, admittedTs}.
        public const string AdmissionField = "admission";
        public const string LoadProofStale = "load_proof_stale";
        public const string RecoveryUnprovenPrefix = "recovery_unproven:";
        public const string RecoveryIneligiblePrefix = "recovery_ineligible:";
        // Stamped into the restore context by a backup import. It can never equal a valid
        // Save Sync load session (those are letters, digits, '_' and '-'), so every earlier
        // load proof stops matching until a fresh verified load rebinds the context.
        public const string ImportedLoadSession = "imported:";

        public const string Accepted = "accepted";
        public const string Conflict = "conflict";
        public const string Stale = "stale";
        public const string Ineligible = "ineligible";
        // Preflight only: the server has no evidence for this campaign/action. Not a legacy proof.
        public const string Unknown = "unknown";
        public const string Eligible = "eligible";

        // Action receipts live beside conversation receipts but never match
        // their transcript/trust readers (status='applied', non-routine tier).
        public const string ReceiptIdPrefix = "action_relation_";
        public const string ReceiptMode = "completed_action";
        public const string ReceiptStatus = "action_effect";
        public const string ReceiptSeverityTier = "routine";
        public const string MeasuredReceiptSchema = "reign-native-relationship-receipt-v2";

        public const int MaxReportUtf8Bytes = 64 * 1024;
        public const int MaxIdentifierLength = 256;
        // Route-specific ingress cap: reportJson is a JSON string, and escaping can expand
        // one UTF-8 byte to six (\u00XX), plus a bounded envelope of identifiers.
        public const long MaxRequestBodyBytes = MaxReportUtf8Bytes * 6L + 16 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Lowercase SHA-256 over the exact UTF-8 bytes; false for empty, oversized or invalid UTF-16.</summary>
        public static bool TryReportSha256(string? reportJson, out string sha256, out int utf8Bytes)
        {
            sha256 = string.Empty;
            utf8Bytes = 0;
            if (string.IsNullOrEmpty(reportJson)) return false;
            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(reportJson!); }
            catch (EncoderFallbackException) { return false; }
            if (bytes.Length > MaxReportUtf8Bytes) return false;
            utf8Bytes = bytes.Length;
            sha256 = Hex(bytes);
            return true;
        }

        public static bool IsSha256Hex(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>Nonempty, bounded, already trimmed and free of control characters.</summary>
        public static bool IsTrustedIdentifier(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > MaxIdentifierLength || value.Trim().Length != value.Length) return false;
            foreach (char c in value)
                if (char.IsControl(c) || char.IsSurrogate(c)) return false;
            return true;
        }

        /// <summary>Deterministic receipt identity bound to action, timeline, generation and exact report.</summary>
        public static string ReceiptId(string actionId, string timelineId, string generation, string reportSha256)
        {
            if (!IsTrustedIdentifier(actionId) || !IsTrustedIdentifier(timelineId)
                || !IsTrustedIdentifier(generation) || !IsSha256Hex(reportSha256))
                throw new ArgumentException("A terminal action receipt requires trusted action, timeline, generation and report identity.");
            // Length prefixes keep embedded separators unambiguous.
            string tuple = Field(actionId) + Field(timelineId) + Field(generation) + Field(reportSha256);
            return ReceiptIdPrefix + Hex(StrictUtf8.GetBytes(tuple)).Substring(0, 40);
        }

        public static string ExchangeId(string actionId, string generation) => "action:" + actionId + ":" + generation;

        private static string Field(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

        private static string Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
