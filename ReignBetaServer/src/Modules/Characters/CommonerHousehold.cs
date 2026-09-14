using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CommonerHouseholdSchema = "reign-commoner-household-v1";

        // This is narrative canon inside the existing Save Sync profile, never fabricated Hero IDs.
        // A resident UUID (not a reusable native character ID) makes retries deterministic.
        private static Dictionary<string, object> GenerateCommonerHousehold(Dictionary<string, object> profile)
        {
            string residentId = ReadString(ReadDictionary(profile, "encounteredResident"), "residentId", "");
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(CommonerHouseholdSchema + "|" + residentId));
            var random = new Random(BitConverter.ToInt32(digest, 0) & int.MaxValue);
            int age = Math.Max(18, Math.Min(120, (int)Math.Floor(ReadDouble(profile, "age", 30))));
            int roll = random.Next(100);
            string status = roll < 35 ? "single" : roll < 83 ? "married" : roll < 92 ? "widowed" : "separated";
            int chance = status == "single" ? 20 : age < 25 ? 40 : 70;
            int maxChildren = Math.Min(4, Math.Max(0, age - 18));
            int count = maxChildren > 0 && random.Next(100) < chance ? random.Next(1, maxChildren + 1) : 0;
            var usedNames = new List<string> { ReadString(profile, "name", "") };
            string culture = ReadString(profile, "cultureId", "empire");
            string NextName(bool female)
            {
                string name = EncounteredResidentRules.ChooseName(culture, female, Array.Empty<string>(), usedNames, usedNames, random.Next);
                usedNames.Add(name);
                return name;
            }
            Dictionary<string, object> partner = null;
            if (status != "single")
            {
                bool female = !ReadBool(profile, "isFemale", false);
                partner = new Dictionary<string, object> { ["name"] = NextName(female), ["isFemale"] = female,
                    ["relationship"] = status == "married" ? "spouse" : status == "widowed" ? "late spouse" : "estranged spouse" };
            }
            int youngestAge = ReadBool(profile, "isFemale", false) ? Math.Max(0, age - 45) : 0;
            var ages = Enumerable.Range(youngestAge, Math.Max(0, age - 18 - youngestAge))
                .OrderBy(_ => random.Next()).Take(count).OrderByDescending(x => x).ToArray();
            var children = ages.Select(childAge => {
                bool female = random.Next(2) == 0;
                string childName = FirstName(NextName(female)) + " " + ReadString(profile, "name", "").Split(' ').Last();
                return new Dictionary<string, object> { ["name"] = childName.Trim(), ["isFemale"] = female,
                    ["ageAtFirstContact"] = childAge };
            }).ToList();
            return new Dictionary<string, object> { ["schema"] = CommonerHouseholdSchema, ["residentId"] = residentId,
                ["source"] = "resident_first_contact", ["ageAtFirstContact"] = age, ["maritalStatus"] = status,
                ["partner"] = partner, ["children"] = children,
                ["homeSettlementName"] = ReadString(ReadDictionary(profile, "encounteredResident"), "homeSettlementName", "") };
        }

        private static void PreserveCommonerHousehold(Dictionary<string, object> profile, Dictionary<string, object> existing)
        {
            if (!IsEncounteredResidentProfile(profile)) return;
            string residentId = ReadString(ReadDictionary(profile, "encounteredResident"), "residentId", "");
            if (string.IsNullOrWhiteSpace(residentId)) return;
            var household = ReadDictionary(existing, "commonerHousehold");
            bool valid = ReadString(household, "schema", "") == CommonerHouseholdSchema
                && ReadString(household, "residentId", "") == residentId;
            // Incoming native snapshots cannot overwrite previously saved household canon.
            profile["commonerHousehold"] = valid ? household : GenerateCommonerHousehold(profile);
            foreach (string field in new[] { "nativeEncyclopediaText", "encyclopediaText" })
                if (profile.ContainsKey(field)) profile[field] = NormalizeResidentFamilyAbsence(ReadString(profile, field, ""));
        }

        private static string NormalizeResidentFamilyAbsence(string text)
        {
            string result = System.Text.RegularExpressions.Regex.Replace(text ?? "",
                "no known clan or family|no recorded native family links; their personal household is recorded separately",
                "no clan affiliation", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return result.Replace(" Their personal household is recorded separately from native family links.", "");
        }

        private static object NormalizeResidentBackgroundFamilyAbsence(object value)
        {
            if (value is string text) return NormalizeResidentFamilyAbsence(text);
            if (value is Dictionary<string, object> map)
                return map.ToDictionary(x => x.Key, x => NormalizeResidentBackgroundFamilyAbsence(x.Value));
            if (value is IEnumerable rows) return rows.Cast<object>().Select(NormalizeResidentBackgroundFamilyAbsence).ToList();
            return value;
        }

        private static string CommonerNarrativeMaritalStatus(Dictionary<string, object> profile)
        {
            if (!IsEncounteredResidentProfile(profile)) return "";
            var household = ReadDictionary(profile, "commonerHousehold");
            string residentId = ReadString(ReadDictionary(profile, "encounteredResident"), "residentId", "");
            return !string.IsNullOrWhiteSpace(residentId)
                && ReadString(household, "schema", "") == CommonerHouseholdSchema
                && ReadString(household, "residentId", "") == residentId
                ? ReadString(household, "maritalStatus", "") : "";
        }

        private static string BuildCommonerHouseholdPrompt(Dictionary<string, object> profile)
        {
            if (!IsEncounteredResidentProfile(profile)) return "";
            var household = ReadDictionary(profile, "commonerHousehold");
            if (ReadString(household, "schema", "") != CommonerHouseholdSchema) return "";
            var builder = new StringBuilder();
            builder.AppendLine("PERSONAL COMMONER HOUSEHOLD - SAVED BIOGRAPHICAL CANON");
            builder.AppendLine("This person's unmodeled family is real in their life even though those relatives have no native Hero records. Empty spouseId, childrenIds, childrenCount=0, and legacy 'no known family' wording do NOT mean single, childless, orphaned or unattached. This saved household overrides those absences and any generic inference from them. Weave it into the life story and retain it in every conversation, after recruitment and away from home. Do not describe databases, templates or this rule in character.");
            builder.AppendLine("Status at first meeting: " + ReadString(household, "maritalStatus", "single") + ".");
            var partner = ReadDictionary(household, "partner");
            if (partner != null && partner.Count > 0)
                builder.AppendLine("Recorded " + ReadString(partner, "relationship", "partner") + ": " + ReadString(partner, "name", "") + ".");
            var children = ReadDictionaryList(household, "children");
            int elapsedYears = Math.Max(0, (int)Math.Floor(ReadDouble(profile, "age", 30)) - ReadInt(household, "ageAtFirstContact", 30));
            builder.AppendLine("Recorded children: " + (children.Count == 0 ? "none" : string.Join("; ", children.Select(child =>
                ReadString(child, "name", "") + " (" + (ReadInt(child, "ageAtFirstContact", 0) + elapsedYears) + " years old)"))) + ".");
            builder.AppendLine("Household's original home: " + ReadString(household, "homeSettlementName", "") + ". Recruitment moves this person; it does not automatically move or abandon their family. A single parent is possible. Single means unmarried at first meeting; widowed means the recorded spouse died; separated means living apart, not an invented divorce. Elaborate upbringing, attachments and everyday family life consistently with this roster and personality; do not randomly replace names, add spouses or children, or change marital status between turns. Reveal private details only when this person chooses.");
            builder.AppendLine("Actual nonempty native kinship links still identify real Heroes and any later native marriage or birth. A later native spouse governs the current marriage while the saved household remains earlier biography; native children are additional real kin, not evidence that unmodeled children vanished. Never identify a household relative with a player, noble or other Hero by a similar name, fabricate a Hero ID, or claim these relatives have arrived, joined a party or received native family mechanics.");
            string nativeSpouse = ReadString(profile, "spouseId", "");
            if (!string.IsNullOrWhiteSpace(nativeSpouse)) builder.AppendLine("Current native spouse: " + FirstNonEmpty(ReadString(profile, "spouseName", ""), nativeSpouse) + " [" + nativeSpouse + "]. This supersedes the first-meeting marital status for the present.");
            return builder.ToString().Trim();
        }
    }
}
