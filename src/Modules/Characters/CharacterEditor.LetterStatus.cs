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
        private const string EditorLetterStatusDeltaKey = "letterStatusDeltaV1";
        private sealed record EditorLetterStatusContext(string Campaign, string Hero, string Revision, string Generation);
        private sealed record EditorLetterStatusEdit(string Id, string Expected, string Value);

        private static Dictionary<string, object> IssueEditorLetterStatusBasis(Dictionary<string, object> records, EditorLetterStatusContext context)
            => ReadDictionaryList(records, "letters").GroupBy(row => ReadString(row, "letter_id", ""), StringComparer.Ordinal)
                .Where(rows => !string.IsNullOrWhiteSpace(rows.Key) && rows.Count() == 1 && rows.Single().GetValueOrDefault("status") is string)
                .ToDictionary(rows => rows.Key, rows => (object)Convert.ToHexString(SignEditorLetterStatus(
                    context, rows.Key, (string)rows.Single()["status"])), StringComparer.Ordinal);

        private static byte[] SignEditorLetterStatus(EditorLetterStatusContext context, string id, string status) =>
            HMACSHA256.HashData(EditorProfileBasisKey, Encoding.UTF8.GetBytes(Json.Serialize(new[]
            {
                "letter-status-edit-v1", context.Campaign, context.Hero, context.Revision, context.Generation, id, status
            })));

        private static bool TryReadEditorLetterStatusEdits(Dictionary<string, object> payload, EditorLetterStatusContext context,
            out List<EditorLetterStatusEdit> edits)
        {
            edits = new List<EditorLetterStatusEdit>();
            if (!payload.TryGetValue("letterStatusEdits", out object value)) return true;
            if (value is not IList rows) return false;
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
                        SignEditorLetterStatus(context, id, expected))) return false;
                }
                catch (FormatException) { return false; }
                if (!string.Equals(expected, desired, StringComparison.Ordinal))
                    edits.Add(new EditorLetterStatusEdit(id, expected, desired));
            }
            return true;
        }

        private static bool TryReadEditorLetterStatusRevision(Dictionary<string, object> before, Dictionary<string, object> after,
            out List<EditorLetterStatusEdit> edits)
        {
            edits = new List<EditorLetterStatusEdit>();
            if (before?.ContainsKey(EditorLetterStatusDeltaKey) == true || after?.ContainsKey(EditorLetterStatusDeltaKey) == true)
            {
                var desired = ReadDictionary(before, EditorLetterStatusDeltaKey);
                var expected = ReadDictionary(after, EditorLetterStatusDeltaKey);
                if (desired == null || expected == null || desired.Count != expected.Count) return false;
                foreach (var row in desired)
                {
                    if (string.IsNullOrWhiteSpace(row.Key) || row.Value is not string value
                        || !expected.TryGetValue(row.Key, out object postimage) || postimage is not string status) return false;
                    edits.Add(new EditorLetterStatusEdit(row.Key, status, value));
                }
                return true;
            }

            // Legacy revisions predate the per-cell receipt. Compare their retained postimage at the writer.
            var previous = ReadDictionaryList(ReadDictionary(before, "records"), "letters")
                .ToLookup(row => ReadString(row, "letter_id", ""), StringComparer.Ordinal);
            var current = ReadDictionaryList(ReadDictionary(after, "records"), "letters")
                .ToLookup(row => ReadString(row, "letter_id", ""), StringComparer.Ordinal);
            foreach (var rows in previous)
            {
                string id = rows.Key;
                if (string.IsNullOrWhiteSpace(id) || rows.Count() != 1 || current[id].Count() != 1
                    || rows.Single().GetValueOrDefault("status") is not string value
                    || current[id].Single().GetValueOrDefault("status") is not string status || value == status) continue;
                edits.Add(new EditorLetterStatusEdit(id, status, value));
            }
            return true;
        }

        private static List<EditorLetterStatusEdit> ApplyEditorLetterStatusEdits(ReignDbConnection connection, string hero,
            List<EditorLetterStatusEdit> edits, List<Dictionary<string, object>> issues, string conflictReason)
        {
            var applied = new List<EditorLetterStatusEdit>();
            if (edits.Count == 0) return applied;
            using var transaction = connection.BeginTransaction();
            foreach (var edit in edits.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                if (string.Equals(edit.Expected, edit.Value, StringComparison.Ordinal)) continue;
                var updated = CharactersStore.UpdateEditorLetterStatus(connection, new Dictionary<string, object>
                {
                    ["id"] = edit.Id, ["hero"] = hero, ["expected"] = edit.Expected, ["value"] = edit.Value
                }, transaction).SingleOrDefault();
                if (updated != null)
                    applied.Add(edit with { Value = (string)updated["status"] });
                else
                    issues.Add(EditorLetterStatusIssue(edit.Id, conflictReason));
            }
            transaction.Commit();
            return applied;
        }

        private static Dictionary<string, object> EditorLetterStatusIssue(string id, string reason)
            => new() { ["group"] = "letters", ["rowId"] = id, ["column"] = "status", ["reason"] = reason };

        private static string EditorSqlIssueMessage(List<Dictionary<string, object>> issues)
            => issues.Count == 0 ? "" : " Some letter statuses were skipped because they changed or their edit basis could not be verified.";
    }
}
