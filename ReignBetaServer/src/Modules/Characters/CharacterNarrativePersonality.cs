using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] NarrativePersonalityRuleTraits = {
            "compassion", "honor", "familyMotivation", "knowledgeMotivation", "fameMotivation",
            "authorityRespect", "sociability", "discipline", "emotionalStability", "pride",
            "assertiveness", "wealthMotivation", "greed", "curiosity", "envy", "vengefulness",
            "revengeMotivation", "powerMotivation", "ambition", "dutyMotivation"
        };

        private static void AttachNarrativeDreamRules(List<Dictionary<string, object>> concepts)
        {
            AttachNarrativePersonalityRules(concepts, "dream-compatibility.json", "reign-narrative-dream-compatibility-v1", new[] { "dream" });
            AttachNarrativePersonalityRules(concepts, "concern-compatibility.json", "reign-narrative-concern-compatibility-v1", new[] { "desire", "value" });
            AttachNarrativeFactRules(concepts);
        }

        private static void AttachNarrativeFactRules(List<Dictionary<string, object>> concepts)
        {
            var document = ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "ProfileLibrary", "narrative", "fact-compatibility.json"));
            if (ReadString(document, "schema", "") != "reign-narrative-fact-compatibility-v1")
                throw new InvalidDataException("Narrative fact compatibility policy is missing or unsupported.");
            var rules = ReadDictionaryList(document, "concepts");
            var byId = concepts.ToDictionary(x => ReadString(x, "id", ""), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                string id = ReadString(rule, "id", "");
                Dictionary<string, object> concept;
                if (!seen.Add(id) || !byId.TryGetValue(id, out concept) || ReadString(rule, "title", "") != ReadString(concept, "title", "")
                    || !rule.ContainsKey("requires") || !(rule["requires"] is System.Collections.IList)
                    || ReadStringList(rule, "requires").Any(x => !new[] { "sibling", "olderSibling", "livingParent" }.Contains(x))
                    || ReadInt(rule, "minAge", -1) < 0 || ReadInt(rule, "minAge", -1) > 150
                    || ReadInt(rule, "maxAge", 150) < ReadInt(rule, "minAge", 0) || ReadInt(rule, "maxAge", 150) > 150)
                    throw new InvalidDataException("Invalid or stale narrative fact compatibility rule.");
                concept["factRule"] = DeepCloneProfileDictionary(rule);
            }
        }

        private static bool NarrativeFactsPermit(Dictionary<string, object> hero, Dictionary<string, object> concept)
        {
            var rule = ReadDictionary(concept, "factRule");
            if (rule == null) return true;
            if (ReadDouble(hero, "age", 30) < ReadInt(rule, "minAge", 0)) return false;
            if (ReadDouble(hero, "age", 30) > ReadInt(rule, "maxAge", 150)) return false;
            foreach (string requirement in ReadStringList(rule, "requires"))
            {
                string field = requirement == "sibling" ? "siblingIds" : requirement == "olderSibling" ? "olderSiblingIds" : "livingParentIds";
                if (ReadStringList(hero, field).Count == 0) return false;
            }
            return true;
        }

        private static List<string> IncompatibleNarrativeFactIds(Dictionary<string, object> narrative, Dictionary<string, object> hero)
        {
            var byId = LoadNarrativeLibrary().ToDictionary(x => ReadString(x, "id", ""), StringComparer.Ordinal);
            return ReadDictionaryList(narrative, "items").Where(item => {
                Dictionary<string, object> concept;
                return !byId.TryGetValue(ReadString(item, "id", ""), out concept) || !NarrativeFactsPermit(hero, concept);
            }).Select(x => ReadString(x, "id", "")).ToList();
        }

        private static void AttachNarrativePersonalityRules(List<Dictionary<string, object>> concepts,
            string fileName, string schema, string[] categories)
        {
            var document = ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "ProfileLibrary", "narrative", fileName));
            if (ReadString(document, "schema", "") != schema)
                throw new InvalidDataException("Narrative personality compatibility policy is missing or unsupported: " + fileName);
            var rules = ReadDictionaryList(document, "concepts");
            var dreams = concepts.Where(x => categories.Contains(ReadString(x, "category", ""))
                && !ReadString(x, "family", "").Contains("/child_")).ToList();
            if (rules.Count != dreams.Count || rules.Select(x => ReadString(x, "id", "")).Distinct().Count() != rules.Count)
                throw new InvalidDataException("Every adult dream requires exactly one reviewed personality rule.");
            var byId = rules.ToDictionary(x => ReadString(x, "id", ""), StringComparer.Ordinal);
            foreach (var dream in dreams)
            {
                Dictionary<string, object> rule;
                if (!byId.TryGetValue(ReadString(dream, "id", ""), out rule)
                    || ReadString(rule, "title", "") != ReadString(dream, "title", ""))
                    throw new InvalidDataException("Narrative dream policy does not match its concept.");
                foreach (string field in new[] { "requireAll", "requireAny", "prefer" })
                {
                    if (!rule.ContainsKey(field) || !(rule[field] is System.Collections.IList) || ReadStringList(rule, field).Any(trait =>
                        !NarrativePersonalityRuleTraits.Contains(field == "prefer" ? trait.TrimStart('-') : trait)))
                        throw new InvalidDataException("Unknown trait or missing field in narrative dream policy.");
                }
                if (rule.ContainsKey("requireNotHigh") && (!(rule["requireNotHigh"] is System.Collections.IList)
                    || ReadStringList(rule, "requireNotHigh").Any(trait => !NarrativePersonalityRuleTraits.Contains(trait))))
                    throw new InvalidDataException("Unknown trait or invalid upper-band restriction in narrative personality policy.");
                dream["personalityRule"] = DeepCloneProfileDictionary(rule);
            }
        }

        private static double NarrativePersonalityScore(Dictionary<string, object> personality,
            Dictionary<string, object> hero, string trait)
        {
            // Honor is the existing derived Court virtue. Compassion remains the
            // character's compassion trait, not a substitute inferred from charm.
            var virtues = ReadDictionary(personality, "courtVirtues");
            if (trait == "honor" && virtues != null && virtues.ContainsKey("honor"))
                return Math.Max(0, Math.Min(100, ReadDouble(virtues, "honor", 50)));
            var percentages = ReadDictionary(personality, "traitPercentages");
            if (percentages != null && percentages.ContainsKey(trait))
                return Math.Max(0, Math.Min(100, ReadDouble(percentages, trait, 50)));
            string foundationKey = trait == "honor" ? "honesty" : trait;
            var foundation = ReadDictionary(personality, "foundationTraits");
            if (foundation != null && foundation.ContainsKey(foundationKey))
                return 50 + 20 * Math.Max(-2, Math.Min(2, ReadDouble(foundation, foundationKey, 0)));
            var native = ReadDictionary(hero, "traits");
            string nativeKey = trait == "compassion" ? "mercy" : trait;
            if (native != null && native.ContainsKey(nativeKey))
                return 50 + 40 * Math.Max(-1, Math.Min(1, ReadDouble(native, nativeKey, 0)));
            return 50; // Unknown evidence is neutral, never an invented extreme.
        }

        private static double NarrativeConcernPersonalityWeight(Dictionary<string, object> hero,
            Dictionary<string, object> personality, Dictionary<string, object> concept)
        {
            if (!new[] { "dream", "desire", "value" }.Contains(ReadString(concept, "category", ""))
                || ReadString(concept, "family", "").Contains("/child_")) return 1d;
            var rule = ReadDictionary(concept, "personalityRule");
            if (rule == null) throw new InvalidDataException("Adult dream has no reviewed personality rule.");
            if (ReadStringList(rule, "requireAll").Any(trait => NarrativePersonalityScore(personality, hero, trait) <= 40)) return 0d;
            if (ReadStringList(rule, "requireNotHigh").Any(trait => NarrativePersonalityScore(personality, hero, trait) > 60)) return 0d;
            var any = ReadStringList(rule, "requireAny");
            if (any.Count > 0 && !any.Any(trait => NarrativePersonalityScore(personality, hero, trait) > 40)) return 0d;
            var preferred = ReadStringList(rule, "prefer");
            if (preferred.Count == 0) return 1d;
            double affinity = preferred.Average(trait => {
                double score = NarrativePersonalityScore(personality, hero, trait.TrimStart('-'));
                return trait.StartsWith("-", StringComparison.Ordinal) ? 100 - score : score;
            });
            return 0.35d + 2.65d * affinity / 100d;
        }

        private static List<string> IncompatibleNarrativeDreamIds(Dictionary<string, object> narrative,
            Dictionary<string, object> hero, Dictionary<string, object> personality)
        {
            return IncompatibleNarrativeConcernIds(narrative, hero, personality, true);
        }

        private static List<string> IncompatibleNarrativeConcernIds(Dictionary<string, object> narrative,
            Dictionary<string, object> hero, Dictionary<string, object> personality, bool dreamsOnly = false)
        {
            var categories = dreamsOnly ? new[] { "dream" } : new[] { "dream", "desire", "value" };
            var byId = LoadNarrativeLibrary().Where(x => categories.Contains(ReadString(x, "category", "")))
                .ToDictionary(x => ReadString(x, "id", ""), StringComparer.Ordinal);
            return ReadDictionaryList(narrative, "items").Where(item => {
                if (!categories.Contains(ReadString(item, "category", ""))) return false;
                Dictionary<string, object> concept;
                return !byId.TryGetValue(ReadString(item, "id", ""), out concept)
                    || NarrativeConceptWeight(hero, concept, personality) <= 0;
            }).Select(x => ReadString(x, "id", "")).ToList();
        }
    }
}
