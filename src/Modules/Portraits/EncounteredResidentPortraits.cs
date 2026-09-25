using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool PreserveResidentClothing(Dictionary<string, object> payload)
        {
            if (ReadString(payload, "promptPurpose", "portrait") != "portrait") return false;
            var snapshot = ReadDictionary(payload, "nativeCharacterSnapshot");
            var resident = ReadDictionary(snapshot, "encounteredResident");
            return ReadString(snapshot, "schema", "") == "reign-native-portrait-snapshot-v1"
                && ReadString(snapshot, "campaignId", "") == ReadString(payload, "campaignId", "")
                && !string.IsNullOrWhiteSpace(ReadString(snapshot, "heroStringId", ""))
                && ReadString(snapshot, "heroStringId", "") == ReadString(payload, "heroStringId", "")
                && (ReadString(resident, "schema", "") == "reign-encountered-resident-v1"
                    || ReadString(snapshot, "portraitSourceProfile", "") == ResidentOutfitRenderContract);
        }

        private static bool IsTavernHousePortrait(Dictionary<string, object> payload)
        {
            if (!PreserveResidentClothing(payload) || ReadBool(payload, "sharedCacheOutput", false)) return false;
            var snapshot = ReadDictionary(payload, "nativeCharacterSnapshot");
            var tavern = ReadDictionary(snapshot, "tavernHouse");
            string heroId = ReadString(snapshot, "heroStringId", "");
            return ReadInt(tavern, "schema", 0) == 1
                && ReadString(tavern, "heroStringId", "") == heroId
                && Regex.IsMatch(ReadString(tavern, "castId", ""), @"^reign_tavern_[A-Za-z0-9_]+$")
                && ReadDouble(snapshot, "age", 0) >= 18;
        }

        private static string TavernHousePortraitRole(Dictionary<string, object> payload)
            => ReadBool(ReadDictionary(ReadDictionary(payload, "nativeCharacterSnapshot"), "tavernHouse"), "madam", false)
                ? "madam" : "worker";

        private static string BuildResidentPortraitPrompt(Dictionary<string, object> payload)
        {
            // The ordinary identity and exclusion layers ALSO change clothes (status, armor,
            // helmets). None is appended here; an instruction at the end cannot undo a conflict.
            return AppendPortraitPhysique(@"IDENTITY AND ENCOUNTERED OUTFIT
Create a believable photographic portrait of the exact person in the supplied native source image.
Preserve recognizable facial features, apparent age, expression, eyes, hair, skin, scars, facial hair and body proportions.
Retain exactly the clothes the person is wearing in the source: every visible garment, color, material, layer, fastening and accessory.
Retain any encountered armor, helmet, headwear, fur, jewelry and work equipment. Do not remove or replace them or reveal more skin.
Do not select clothing from culture, sex, occupation, clan, wealth, social standing, confidence or attractiveness.
Translate game rendering into authentic skin texture, natural hair, realistic fabric and metal while preserving identity and the complete outfit.
COMPOSITION
Show one upright, centered person from the top of the head through both feet with a small clear margin, a relaxed pose and soft natural daylight.
Use a quiet, softly blurred medieval architectural background. Keep the figure large enough for headshot and full-body use.
OUTPUT
Return only a realistic photographic image. No text, labels, captions, UI, frames, watermarks, modern objects, cartoon, plastic skin or CGI finish.", payload);
        }
    }
}
