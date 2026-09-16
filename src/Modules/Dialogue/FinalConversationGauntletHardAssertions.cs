using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            EvaluateFinalGauntletHardAssertions(
                Dictionary<string, object> fixture,
                Dictionary<string, object> evidence)
        {
            List<Dictionary<string, object>> assertions =
                new List<Dictionary<string, object>>();
            Action<string, bool, bool, string, object> add =
                (id, passed, critical, message, actual) =>
                    assertions.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["passed"] = passed,
                        ["critical"] = critical,
                        ["message"] = message,
                        ["actual"] = actual
                    });
            add(
                "evidence_schema",
                ReadString(evidence, "schema", "")
                    == "reign-final-gauntlet-evidence-v3",
                true,
                "The case uses complete immutable evidence schema v3.",
                ReadString(evidence, "schema", ""));

            Dictionary<string, object> expected =
                ReadDictionary(fixture, "expected")
                ?? new Dictionary<string, object>();
            foreach (Dictionary<string, object> requirement in
                ReadDictionaryList(expected, "hardRequirements"))
            {
                string kind = ReadString(requirement, "kind", "");
                string id = FirstNonEmpty(
                    ReadString(requirement, "id", ""),
                    "hard_" + kind + "_" + assertions.Count);
                if (kind.Equals("path_equals", StringComparison.OrdinalIgnoreCase))
                {
                    string path = ReadString(requirement, "path", "");
                    object actual = FinalGauntletPathValue(evidence, path);
                    object required = requirement.TryGetValue(
                        "expected", out object value) ? value : null;
                    add(
                        id,
                        FinalGauntletCanonical(actual)
                            == FinalGauntletCanonical(required),
                        true,
                        "Authoritative evidence path must equal the fixture value: "
                            + path,
                        actual);
                }
                else if (kind.Equals(
                    "action_target", StringComparison.OrdinalIgnoreCase))
                {
                    string required = ReadString(requirement, "expected", "");
                    List<string> targets = FinalGauntletActionData(evidence)
                        .Select(row => FirstNonEmpty(
                            ReadString(row, "resolvedTargetId", ""),
                            ReadString(row, "targetId", "")))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList();
                    add(
                        id,
                        targets.Count == 1
                            && targets[0].Equals(
                                required, StringComparison.Ordinal),
                        true,
                        "The resolver and executor must target exactly the authorized entity.",
                        targets.Cast<object>().ToList());
                }
            }

            string visible = FinalGauntletVisibleReply(evidence);
            foreach (string forbidden in ReadStringList(
                expected, "forbiddenContent"))
            {
                add(
                    "forbidden_visible_" + assertions.Count,
                    visible.IndexOf(
                        forbidden, StringComparison.OrdinalIgnoreCase) < 0,
                    true,
                    "Visible dialogue must not expose forbidden content.",
                    forbidden);
            }
            foreach (Dictionary<string, object> violation in
                ReadDictionaryList(evidence, "zeroToleranceViolations"))
            {
                add(
                    "zero_tolerance_" + assertions.Count,
                    false,
                    true,
                    FirstNonEmpty(
                        ReadString(violation, "message", ""),
                        "A zero-tolerance violation was recorded."),
                    violation);
            }
            return assertions;
        }

        private static string FinalGauntletVisibleReply(
            Dictionary<string, object> evidence)
        {
            foreach (Dictionary<string, object> entry in
                ReadDictionaryList(evidence, "modelEvidence")
                    .AsEnumerable().Reverse())
            {
                Dictionary<string, object> data =
                    ReadDictionary(entry, "data") ?? entry;
                Dictionary<string, object> parsed =
                    ReadDictionary(data, "parsedResponse")
                    ?? new Dictionary<string, object>();
                string reply = FirstNonEmpty(
                    ReadString(parsed, "reply", ""),
                    ReadString(data, "reply", ""),
                    ReadString(data, "content", ""));
                if (!string.IsNullOrWhiteSpace(reply)) return reply;
            }
            return string.Empty;
        }

        private static List<Dictionary<string, object>>
            FinalGauntletActionData(Dictionary<string, object> evidence)
        {
            return ReadDictionaryList(
                    evidence, "actionResolverValidationEvidence")
                .Select(row => ReadDictionary(row, "data") ?? row)
                .ToList();
        }

        private static object FinalGauntletPathValue(
            object root,
            string path)
        {
            object current = root;
            foreach (string segment in (path ?? string.Empty)
                .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (current is IDictionary dictionary)
                {
                    object next = null;
                    bool found = false;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (!string.Equals(
                            Convert.ToString(entry.Key),
                            segment,
                            StringComparison.Ordinal))
                            continue;
                        next = entry.Value;
                        found = true;
                        break;
                    }
                    if (!found) return null;
                    current = next;
                }
                else return null;
            }
            return current;
        }

        private static string FinalGauntletCanonical(object value)
        {
            return CanonicalJson(value);
        }
    }
}
