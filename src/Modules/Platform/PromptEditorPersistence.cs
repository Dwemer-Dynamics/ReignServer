using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Caller owns FileLock. The directory parameter permits isolated behavioral proof.
        private static Dictionary<string, object> PersistPromptEditorFiles(string directory,
            Dictionary<string, object> files, Dictionary<string, object> expected)
        {
            Dictionary<string, object> Failed(string error) => new Dictionary<string, object>
                { ["ok"] = false, ["error"] = error, ["saved"] = 0 };
            if (files == null || files.Count == 0) return Failed("No prompts were supplied.");
            foreach (var pair in files)
            {
                if (pair.Key.IndexOfAny(new[] { '/', '\\' }) >= 0 || !IsPromptFileNameAllowed(pair.Key)
                    || !(pair.Value is string)) return Failed("Prompt names and text must be valid. Nothing was saved.");
                string target = Path.Combine(directory, pair.Key);
                if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    return Failed("Redirected prompt files cannot be edited.");
                if (expected != null)
                {
                    if (!expected.TryGetValue(pair.Key, out var baseline) || !(baseline is string))
                        return Failed("The saved prompt revision is missing. Reload before saving.");
                    string current = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8) : "";
                    if (current != (string)baseline && current != (string)pair.Value)
                        return Failed(pair.Key + " changed on the server. Reload to review it; your draft has not been saved.");
                }
            }
            var written = new Dictionary<string, object>();
            Directory.CreateDirectory(directory);
            foreach (var pair in files)
            {
                string target = Path.Combine(directory, pair.Key);
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, (string)pair.Value, new UTF8Encoding(false));
                    File.Move(temporary, target, true);
                    written[pair.Key] = File.ReadAllText(target, Encoding.UTF8);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["saved"] = written.Count, ["files"] = written };
        }

        private static List<Dictionary<string, object>> RunPromptEditorPersistenceTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed) => results.Add(new Dictionary<string, object>
                { ["caseId"] = "prompt_editor_" + name, ["suite"] = "interaction_architecture", ["passed"] = passed,
                  ["summary"] = "Prompt editor isolated persistence: " + name });
            string root = Path.Combine(VerificationDir, "prompt-editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            const string name = "tavern_house_portrait_clothing.txt";
            var text = "  Exact user text.\r\nUnicode: é — test\n\n";
            Dictionary<string, object> One(string value) => new Dictionary<string, object> { [name] = value };
            try
            {
                var saved = PersistPromptEditorFiles(root, One(text), One(""));
                Check("exact_text", ReadBool(saved, "ok", false) && ReadInt(saved, "saved", 0) == 1
                    && File.ReadAllText(Path.Combine(root, name)) == text && (string)ReadDictionary(saved, "files")[name] == text);
                var conflict = PersistPromptEditorFiles(root, One("stale overwrite"), One("old text"));
                Check("stale_edit_rejected", !ReadBool(conflict, "ok", true) && File.ReadAllText(Path.Combine(root, name)) == text);
                Check("lost_response_retry", ReadBool(PersistPromptEditorFiles(root, One(text), One("")), "ok", false));
                var invalid = new Dictionary<string, object> { [name] = "would overwrite", ["../escape.txt"] = "escape" };
                Check("invalid_batch_preserved", !ReadBool(PersistPromptEditorFiles(root, invalid, null), "ok", true)
                    && File.ReadAllText(Path.Combine(root, name)) == text);
                Check("windows_traversal_rejected", !ReadBool(PersistPromptEditorFiles(root,
                    new Dictionary<string, object> { ["..\\escape.txt"] = "escape" }, null), "ok", true));
                Check("empty_request_rejected", !ReadBool(PersistPromptEditorFiles(root, new Dictionary<string, object>(), null), "ok", true));
                Check("empty_text_allowed", ReadBool(PersistPromptEditorFiles(root, One(""), One(text)), "ok", false)
                    && File.ReadAllText(Path.Combine(root, name)) == "");
                Check("atomic_file_cleanup", Directory.GetFiles(root).Length == 1);
            }
            finally { Directory.Delete(root, true); }
            return results;
        }
    }
}
