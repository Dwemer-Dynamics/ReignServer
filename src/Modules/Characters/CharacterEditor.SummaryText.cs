using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string EditorSummaryTextDeltaKey = "summaryTextDeltaV1";
        private sealed record EditorSummaryTextEdit(string Id, string Expected, string Value);

        private static Dictionary<string, object> IssueEditorSummaryTextBasis(
            Dictionary<string, object> records, EditorHistoryEditBasis context)
            => ReadDictionaryList(records, "summaries")
                .GroupBy(row => ReadString(row, "summary_id", ""), StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1
                    && ReadString(group.Single(), "owner_id", "") == context.Hero
                    && ReadString(group.Single(), "status", "") == "active"
                    && group.Single().GetValueOrDefault("summary") is string)
                .ToDictionary(group => group.Key, group => (object)Convert.ToHexString(SignEditorSummaryText(
                    context, group.Key, (string)group.Single()["summary"])), StringComparer.Ordinal);

        private static byte[] SignEditorSummaryText(EditorHistoryEditBasis context, string id, string text) =>
            HMACSHA256.HashData(EditorProfileBasisKey, Encoding.UTF8.GetBytes(Json.Serialize(new[]
            {
                "summary-text-edit-v1", context.Campaign, context.Hero, context.Revision, context.Generation, id, text
            })));

        private static bool TryReadEditorSummaryTextEdits(Dictionary<string, object> payload, EditorHistoryEditBasis context,
            out List<EditorSummaryTextEdit> edits)
        {
            edits = new List<EditorSummaryTextEdit>();
            if (!payload.TryGetValue("summaryTextEdits", out object value)) return true;
            if (value is not IList rows || rows.Count > 500) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in rows)
            {
                if (entry is not Dictionary<string, object> row || row.Count != 4
                    || row.GetValueOrDefault("id") is not string id || string.IsNullOrWhiteSpace(id) || !ids.Add(id)
                    || row.GetValueOrDefault("expected") is not string expected || row.GetValueOrDefault("value") is not string desired
                    || row.GetValueOrDefault("basis") is not string basis || basis.Length != 64) return false;
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(basis),
                        SignEditorSummaryText(context, id, expected))) return false;
                }
                catch (FormatException) { return false; }
                if (!string.Equals(expected, desired, StringComparison.Ordinal))
                    edits.Add(new EditorSummaryTextEdit(id, expected, desired));
            }
            return true;
        }

        private static bool TryReadEditorSummaryTextUndo(Dictionary<string, object> before, Dictionary<string, object> after,
            out List<EditorSummaryTextEdit> edits)
        {
            edits = new List<EditorSummaryTextEdit>();
            // An old whole-snapshot difference cannot identify a summary the editor actually wrote.
            if (before?.ContainsKey(EditorSummaryTextDeltaKey) != true
                && after?.ContainsKey(EditorSummaryTextDeltaKey) != true) return true;
            var desired = ReadDictionary(before, EditorSummaryTextDeltaKey);
            var expected = ReadDictionary(after, EditorSummaryTextDeltaKey);
            if (desired == null || expected == null || desired.Count != expected.Count) return false;
            foreach (var row in desired)
            {
                if (string.IsNullOrWhiteSpace(row.Key) || row.Value is not string value
                    || !expected.TryGetValue(row.Key, out object postimage) || postimage is not string text) return false;
                if (!string.Equals(text, value, StringComparison.Ordinal))
                    edits.Add(new EditorSummaryTextEdit(row.Key, text, value));
            }
            return true;
        }

        private static List<EditorSummaryTextEdit> ApplyEditorSummaryTextEdits(ReignDbConnection connection, string hero,
            List<EditorSummaryTextEdit> edits, List<Dictionary<string, object>> issues, string conflictReason)
        {
            var applied = new List<EditorSummaryTextEdit>();
            if (edits.Count == 0) return applied;
            using var transaction = connection.BeginTransaction();
            foreach (var edit in edits.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                var updated = CharactersStore.UpdateEditorSummaryText(connection, TestDict(
                    "id", edit.Id, "hero", hero, "expected", edit.Expected, "value", edit.Value), transaction).SingleOrDefault();
                if (updated != null)
                    applied.Add(edit with { Value = (string)updated["summary"] });
                else
                    issues.Add(EditorSummaryTextIssue(edit.Id, conflictReason));
            }
            transaction.Commit();
            return applied;
        }

        private static void RecordEditorSummaryTextDelta(Dictionary<string, object> before, Dictionary<string, object> after,
            List<EditorSummaryTextEdit> applied)
        {
            before[EditorSummaryTextDeltaKey] = applied.ToDictionary(edit => edit.Id, edit => (object)edit.Expected, StringComparer.Ordinal);
            after[EditorSummaryTextDeltaKey] = applied.ToDictionary(edit => edit.Id, edit => (object)edit.Value, StringComparer.Ordinal);
        }

        private static Dictionary<string, object> EditorSummaryTextIssue(string id, string reason)
            => new() { ["group"] = "summaries", ["rowId"] = id, ["column"] = "summary", ["reason"] = reason };

#if !REIGN_EXCLUDE_VERIFICATION
        private static void VerifyEditorSummaryTextCompareAndSwap(Action<string, bool, string> add)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
            {
                add("summary_text_validation_isolation", false, "Summary text fixtures require the isolated validation database.");
                return;
            }
            const string hero = "fixture_summary_hero", ownedId = "owned_summary", otherId = "shared_summary";
            string campaign = "ce_summary_" + Guid.NewGuid().ToString("N");
            try
            {
                using var c = ReignDatabase.OpenCampaignConnection(campaign);
                ReignDatabase.ExecuteSql(c, @"INSERT INTO summaries(summary_id,owner_id,summary,known_by_json,updated_ts)
VALUES($owned,$hero,'Text initially loaded','[]',1),($shared,'fixture_other_hero','Text owned by another',$known,1);",
                    TestDict("owned", ownedId, "shared", otherId, "hero", hero, "known", Json.Serialize(new[] { hero })));

                Dictionary<string, object> Load() => CharacterEditorLoadApi(TestDict("campaignId", campaign, "heroStringId", hero));
                string Text(string id) => ReadString(ReignDatabase.QuerySql(c,
                    "SELECT summary FROM summaries WHERE summary_id=$id;", TestDict("id", id)).Single(), "summary", "");
                void SetLive(string value) => DialogueStore.UpdateSummaries2(c,
                    TestDict("id", ownedId, "summary", value, "payload", "{}", "updated", 2L));
                bool HasIssue(Dictionary<string, object> response, string id, string reason) =>
                    ReadDictionaryList(response, "recordEditIssues").Any(issue => ReadString(issue, "rowId", "") == id
                        && ReadString(issue, "reason", "") == reason);
                Dictionary<string, object> Save(Dictionary<string, object> loaded, string expected, string value) =>
                    CharacterEditorSaveApi(TestDict("campaignId", campaign, "heroStringId", hero,
                        "revisionToken", ReadString(loaded, "revisionToken", ""), "summaryTextEdits", new[] { TestDict(
                            "id", ownedId, "expected", expected, "value", value, "basis",
                            ReadString(ReadDictionary(loaded, "summaryTextEditBasis"), ownedId, "")) }));

                var loaded = Load();
                var basis = ReadDictionary(loaded, "summaryTextEditBasis");
                bool sharedWasVisible = ReadDictionaryList(ReadDictionary(loaded, "records"), "summaries")
                    .Any(row => ReadString(row, "summary_id", "") == otherId);
                SetLive("New live summary");
                var stale = Save(loaded, "Text initially loaded", "Old draft edit");
                bool liveTextPreserved = Text(ownedId) == "New live summary";
                add("summary_text_stale_edit_preserves_live_writer", basis.ContainsKey(ownedId) && sharedWasVisible && !basis.ContainsKey(otherId)
                    && ReadBool(stale, "ok", false) && HasIssue(stale, ownedId, "summary_text_changed_after_load")
                    && liveTextPreserved,
                    "The editor accepts only the owned snapshot basis, and a normal live summary update wins over its stale draft.");

                var current = Load();
                var forged = Save(current, "Forged preimage", "Rejected draft");
                var unsigned = CharacterEditorSaveApi(TestDict("campaignId", campaign, "heroStringId", hero,
                    "revisionToken", ReadString(current, "revisionToken", ""), "records", TestDict("summaries",
                        new[] { TestDict("summary_id", ownedId, "summary", "Unsigned draft") })));
                var edited = Save(Load(), "New live summary", "Editor summary");
                string revision = ReadString(edited, "revisionId", "");
                add("summary_text_requires_exact_basis_and_scope", ReadBool(forged, "conflict", false)
                    && HasIssue(unsigned, ownedId, "summary_text_requires_edit_basis")
                    && ReadBool(edited, "ok", false) && revision.Length > 0
                    && Text(ownedId) == "Editor summary" && Text(otherId) == "Text owned by another",
                    "The obsolete raw cell write and forged preimage cannot bypass the protected edit path.");

                Dictionary<string, object> Undo() => CharacterEditorRollbackApi(TestDict(
                    "campaignId", campaign, "heroStringId", hero, "revisionId", revision));
                var undo = Undo();
                bool exactRestored = ReadBool(undo, "ok", false) && Text(ownedId) == "New live summary";
                SetLive("Summary updated after the revision");
                var staleUndo = Undo();
                add("summary_text_undo_preserves_newer_live_value", exactRestored && ReadBool(staleUndo, "ok", false)
                    && HasIssue(staleUndo, ownedId, "summary_text_changed_after_revision")
                    && Text(ownedId) == "Summary updated after the revision",
                    "A valid undo restores its own preimage, and a repeated stale undo preserves a subsequent live update.");

                var beforeTransfer = Load();
                ReignDatabase.ExecuteSql(c, "UPDATE summaries SET owner_id='fixture_other_hero' WHERE summary_id=$id;",
                    TestDict("id", ownedId));
                var moved = Save(beforeTransfer, "Summary updated after the revision", "Old owner draft");
                add("summary_text_owner_changed_after_load", HasIssue(moved, ownedId, "summary_text_changed_after_load")
                    && Text(ownedId) == "Summary updated after the revision",
                    "A previously valid basis cannot write the same unchanged text after the summary leaves that owner.");
            }
            catch (Exception exception)
            {
                add("summary_text_fixture_exception", false, exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
        }
#endif
    }
}
