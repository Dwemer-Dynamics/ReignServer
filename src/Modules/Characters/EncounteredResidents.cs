using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly ConcurrentDictionary<string, object> ResidentConstructionLocks = new ConcurrentDictionary<string, object>();

        private static bool IsEncounteredResidentProfile(Dictionary<string, object> profile)
            => ReadString(ReadDictionary(profile, "encounteredResident"), "schema", "") == "reign-encountered-resident-v1";

        private static Dictionary<string, object> EnsureEncounteredResidentConstructed(string campaignId, string heroId,
            Dictionary<string, object> hero, string source)
        {
            // A resident must have their story/personality before speaking. Normal legacy heroes
            // retain the existing scaffold/enrichment policy. Concurrent first clicks share one lock.
            lock (ResidentConstructionLocks.GetOrAdd(campaignId + "|" + heroId, _ => new object()))
            {
                string profilePath = CharacterFile(campaignId, heroId, "profile.json");
                var savedProfile = ReadJsonObject(profilePath);
                if (savedProfile.Count == 0) savedProfile = new Dictionary<string, object>(hero);
                PreserveCommonerHousehold(hero, savedProfile);
                if (hero.TryGetValue("commonerHousehold", out object household)) savedProfile["commonerHousehold"] = household;
                foreach (string field in new[] { "nativeEncyclopediaText", "encyclopediaText" })
                    if (savedProfile.ContainsKey(field)) savedProfile[field] = NormalizeResidentFamilyAbsence(ReadString(savedProfile, field, ""));
                WriteJsonObject(profilePath, savedProfile);
                string characteristicsPath = CharacterFile(campaignId, heroId, "characteristics.json");
                var characteristics = ReadJsonObject(characteristicsPath);
                if (characteristics.TryGetValue("background", out object background))
                {
                    object normalized = NormalizeResidentBackgroundFamilyAbsence(background);
                    if (Json.Serialize(normalized) != Json.Serialize(background))
                    { characteristics["background"] = normalized; WriteJsonObject(characteristicsPath, characteristics); }
                }
                var existing = ReadJsonObject(CharacterFile(campaignId, heroId, "constructed.json"));
                if (ReadBool(existing, "residentFirstContactComplete", false)
                    && IsCharacterConstructionReady(existing)
                    && TraitDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"))))
                { existing["alreadyReady"] = true; return existing; }
                var result = ConstructCharacterFiles(campaignId, heroId, hero, true, true, source + "_encountered_resident");
                if (!IsCharacterConstructionReady(result) || !ReadBool(result, "llmUsed", false))
                {
                    result["ok"] = false;
                    result["residentFirstContactComplete"] = false;
                    return result;
                }
                result["residentFirstContactComplete"] = true;
                result["backgroundEnrichmentPending"] = false;
                WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), result);
                MarkCharacterConstructionCompletedOnInteraction(campaignId, heroId, source);
                return result;
            }
        }

        private static bool IsResidentLocalAuthority(Dictionary<string, object> observer, string subjectId)
        {
            if (!IsEncounteredResidentProfile(observer) || string.IsNullOrWhiteSpace(subjectId)) return false;
            var resident = ReadDictionary(observer, "encounteredResident");
            return string.Equals(subjectId, ReadString(resident, "homeRulerId", ""), StringComparison.Ordinal)
                || string.Equals(subjectId, ReadString(resident, "homeOwnerClanLeaderId", ""), StringComparison.Ordinal);
        }
    }
}
