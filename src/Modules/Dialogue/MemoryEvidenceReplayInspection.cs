using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string ReplayQuestionLinkInspectionError(Dictionary<string, object> writer)
        {
            if (writer == null || !writer.ContainsKey("questionLinkInspection")) return "";
            var inspection = ReadDictionary(writer, "questionLinkInspection");
            int entries = 0;
            if (inspection == null || !inspection.Keys.ToHashSet().SetEquals(new[] { "reply", "writes" })
                || inspection.GetValueOrDefault("reply") is not string reply || reply.Length is < 1 or > 24000
                || !IsConversationObjectArray(inspection.GetValueOrDefault("writes"))
                || ReadDictionaryList(inspection, "writes").Count > 32
                || ReadDictionaryList(inspection, "writes").Any(w => ReadString(w, "kind", "") != "question")
                || !BoundedReplayNativeValue(inspection, 0, ref entries) || Json.Serialize(inspection).Length > 96000)
                return "invalid_question_link_inspection";
            if (!writer.ContainsKey("initialNativeContext")
                || new[] { "response", "repairResponses", "continuation", "nextPlanResponse", "nextAssemblyResponse", "nextReviewResponse" }.Any(writer.ContainsKey))
                return "question_link_inspection_capture_only";
            return "";
        }

        // Exercise the actual source resolver on a proposed question link without
        // accepting prose, writing a memory, or granting a publication receipt.
        private static Dictionary<string, object> InspectReplayQuestionLinks(string campaign,
            Dictionary<string, object> writer, Dictionary<string, object> payload, string observer, string peer)
        {
            string error = ReplayQuestionLinkInspectionError(writer);
            if (error.Length > 0) throw new InvalidOperationException(error);
            if (!ReignPostgreSqlStorage.Options.IsValidation || ActiveServerPort > 0
                || string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("question_link_inspection_requires_isolated_validation");
            var inspection = ReadDictionary(writer, "questionLinkInspection");
            string reply = ReadString(inspection, "reply", "");
            var rows = new List<Dictionary<string, object>>();
            using var connection = OpenCampaignConnection(campaign);
            using var transaction = connection.BeginTransaction();
            ExecuteSql(connection, "SET TRANSACTION READ ONLY;");
            if (ReadString(QuerySql(connection, "SELECT current_setting('transaction_read_only') AS mode;").Single(), "mode", "") != "on")
                throw new InvalidOperationException("question_link_inspection_read_only_required");
            int index = 0;
            foreach (var supplied in ReadDictionaryList(inspection, "writes"))
            {
                var write = CloneDictionary(supplied);
                string reason = PrepareQuestionContinuityWrite(connection, write, CloneDictionary(payload), observer, peer,
                    observer + ":question_link_inspection", reply);
                rows.Add(TestDict("index", index++, "status", reason.Length == 0 ? "source_link_verified" : "source_link_rejected",
                    "reason", reason, "suppliedWriteSha256", PromptHash(CanonicalJson(supplied)),
                    "normalizedQuestionState", reason.Length == 0 ? ReadDictionary(write, "questionState") : null));
            }
            transaction.Rollback();
            return TestDict("schema", "reign-question-link-inspection-v1", "checks", rows,
                "replySha256", PromptHash(reply), "databaseReadOnly", true, "providerCalls", 0,
                "publicationAllowed", false, "certificateEligible", false,
                "scope", "question_source_binding_and_order_only_not_semantic_answer_coverage_or_full_write_admission");
        }
    }
}
