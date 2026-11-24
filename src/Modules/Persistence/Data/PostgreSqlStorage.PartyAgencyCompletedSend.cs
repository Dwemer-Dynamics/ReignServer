using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class ReignPostgreSqlStorage
    {
        internal const int PartyAgencyAcceptedReceiptReadLimit = 128;
        internal sealed record PartyAgencyAcceptedSendRow(string UnitId, string ResponseHash,
            string RestoreGeneration, string ProofJson);

        internal static List<PartyAgencyAcceptedSendRow> ReadRegisteredPartyAgencyAcceptedReceipts(
            string campaignId, string[] unitIds)
        {
            var rows = new List<PartyAgencyAcceptedSendRow>();
            if (string.IsNullOrWhiteSpace(campaignId) || !string.Equals(campaignId, NormalizeCampaignId(campaignId), StringComparison.Ordinal)
                || unitIds == null || unitIds.Length == 0 || unitIds.Length > PartyAgencyAcceptedReceiptReadLimit)
                return rows;

            PersistenceService.ThrowIfCampaignRequestReplaced();
            PersistenceService.RequireSaveSyncCampaignAvailable(campaignId);
            string schemaName = CampaignSchemaName(campaignId);
            string schema = QuoteIdentifier(schemaName);
            // This report reader never ensures infrastructure, registers a schema, or relies on search_path.
            using var connection = OpenDatabaseConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY;", connection, transaction))
            {
                readOnly.CommandTimeout = 5;
                readOnly.ExecuteNonQuery();
            }

            using (var registered = new NpgsqlCommand(@"
SELECT EXISTS (
    SELECT 1 FROM reign_meta.campaign_registry r
    INNER JOIN pg_catalog.pg_namespace n ON n.nspname = r.schema_name
    WHERE r.campaign_id = @campaign AND r.schema_name = @schema);", connection, transaction))
            {
                registered.CommandTimeout = 5;
                registered.Parameters.AddWithValue("campaign", campaignId);
                registered.Parameters.AddWithValue("schema", schemaName);
                if (!(registered.ExecuteScalar() is bool exists) || !exists) return rows;
            }

            using (var select = new NpgsqlCommand($@"
WITH selected AS MATERIALIZED (
    SELECT unit_id, response_hash, restore_generation, receipt_json::jsonb AS receipt
    FROM {schema}.conversation_accepted_units
    WHERE unit_id = ANY(@ids) AND octet_length(receipt_json) <= 262144
)
SELECT u.unit_id, u.response_hash, u.restore_generation,
       jsonb_build_object('ok', u.receipt->'ok', 'status', u.receipt->'status',
           'unitId', u.receipt->'unitId', 'responseHash', u.receipt->'responseHash',
           'acceptedReplyHash', u.receipt->'acceptedReplyHash',
           'restoreGeneration', u.receipt->'restoreGeneration',
           'partyAgencySendOrigin', u.receipt->'partyAgencySendOrigin',
           'partyAgencyServingProviderAttempt', u.receipt->'partyAgencyServingProviderAttempt')::text
FROM selected u
INNER JOIN {schema}.memory_precision_state active
    ON active.state_id = 'current' AND active.restore_generation = u.restore_generation
WHERE jsonb_typeof(u.receipt->'partyAgencySendOrigin') = 'object'
    AND jsonb_typeof(u.receipt->'partyAgencyServingProviderAttempt') = 'object'
    AND octet_length((u.receipt->'partyAgencySendOrigin')::text) <= 8192
    AND octet_length((u.receipt->'partyAgencyServingProviderAttempt')::text) <= 8192;", connection, transaction))
            {
                select.CommandTimeout = 5;
                select.Parameters.AddWithValue("ids", unitIds);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    rows.Add(new PartyAgencyAcceptedSendRow(reader.GetString(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3)));
            }
            transaction.Commit();
            return rows;
        }
    }
}
