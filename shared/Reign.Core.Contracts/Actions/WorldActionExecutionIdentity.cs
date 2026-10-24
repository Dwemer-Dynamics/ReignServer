using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reign.Core.Contracts.Actions
{
    /// <summary>Stable native operation identities, including saved pre-digest operations.</summary>
    public static class WorldActionExecutionIdentity
    {
        public static string Create(string agreementId, string clauseId, int installment)
        {
            Validate(agreementId, clauseId, installment);
            return Prefix(agreementId, clauseId) + installment.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryGetInstallment(string? actionId, string agreementId, string clauseId, out int installment)
        {
            installment = -1;
            if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(agreementId)
                || string.IsNullOrWhiteSpace(clauseId)) return false;
            return TryParse(actionId!, Prefix(agreementId, clauseId), out installment)
                || TryParse(actionId!, LegacyPrefix(agreementId, clauseId), out installment);
        }

        // The snapshot is captured for installment zero and reused for later deliveries.
        // Keep its identity format so a saved native operation is observed, never reissued.
        public static string RebindCaptured(string? capturedId, string agreementId, string clauseId, int installment)
        {
            Validate(agreementId, clauseId, installment);
            if (capturedId != null && TryParse(capturedId, Prefix(agreementId, clauseId), out _))
                return Create(agreementId, clauseId, installment);
            if (capturedId != null && TryParse(capturedId, LegacyPrefix(agreementId, clauseId), out _))
                return LegacyPrefix(agreementId, clauseId) + installment.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException("The captured native action identity does not belong to this agreement step.");
        }

        private static string Prefix(string agreementId, string clauseId)
        {
            // Length prefixes make embedded separators and Unicode unambiguous.
            string tuple = agreementId.Length.ToString(CultureInfo.InvariantCulture) + ":" + agreementId
                + clauseId.Length.ToString(CultureInfo.InvariantCulture) + ":" + clauseId;
            using (var sha = SHA256.Create())
                return "sa_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(tuple)))
                    .Replace("-", "").ToLowerInvariant() + "_";
        }

        private static string LegacyPrefix(string agreementId, string clauseId) => "sa_" + agreementId + "_"
            + clauseId.Substring(0, Math.Min(16, clauseId.Length)) + "_";

        private static bool TryParse(string actionId, string prefix, out int installment)
        {
            installment = -1;
            if (!actionId.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string suffix = actionId.Substring(prefix.Length);
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed < 0 || suffix != parsed.ToString(CultureInfo.InvariantCulture)) return false;
            installment = parsed; return true;
        }

        private static void Validate(string agreementId, string clauseId, int installment)
        {
            if (string.IsNullOrWhiteSpace(agreementId) || string.IsNullOrWhiteSpace(clauseId))
                throw new ArgumentException("Exact agreement and clause identities are required.");
            if (installment < 0) throw new ArgumentOutOfRangeException(nameof(installment));
        }
    }
}
