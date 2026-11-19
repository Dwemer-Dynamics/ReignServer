using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        internal static void PreserveWandererIdentity(Dictionary<string, object> profile, Dictionary<string, object> existing)
        {
            var incoming = ReadDictionary(profile, "wandererIdentity");
            var saved = ReadDictionary(existing, "wandererIdentity");
            if (ReadString(incoming, "schema", "") != WandererPopulationRules.Version) return;
            string identity = ReadString(incoming, "identityId", "");
            if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("Wanderer identity is missing.");
            if (ReadString(saved, "schema", "") == WandererPopulationRules.Version
                && ReadString(saved, "identityId", "") != identity)
                throw new InvalidOperationException("A different wanderer attempted to reuse an existing character identity.");
            var canonical = new Dictionary<string, object>(incoming, StringComparer.OrdinalIgnoreCase);
            if (ReadString(saved, "identityId", "") == identity)
            {
                foreach (string field in new[] { "name", "biography", "cultureId", "templateId", "generated" })
                    if (saved.ContainsKey(field)) canonical[field] = saved[field];
                if (ReadBool(saved, "protected", false))
                    foreach (string field in new[] { "protected", "contactReceipt", "contactDay" })
                        if (saved.ContainsKey(field)) canonical[field] = saved[field];
            }
            profile["wandererIdentity"] = canonical;
            if (ReadBool(canonical, "generated", false))
            {
                profile["name"] = ReadString(canonical, "name", ReadString(profile, "name", ""));
                profile["cultureId"] = ReadString(canonical, "cultureId", "");
                profile["nativeEncyclopediaText"] = ReadString(canonical, "biography", "");
            }
        }

        // Observational branch of /dialogue/history. No profile construction, provider, or native mutation.
        internal static Dictionary<string, object> WandererContactHistory(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "");
            string timeline = ReadString(payload, "timelineId", "");
            string hero = ReadString(payload, "heroStringId", "");
            string player = ReadString(payload, "playerHeroStringId", "");
            if (new[] { campaign, timeline, hero, player }.Any(string.IsNullOrWhiteSpace) || hero == player)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Exact campaign, timeline, wanderer and player identities are required." };
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                if (WorldSimulationService.ActiveWorldHistoryTimeline(connection) != timeline)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Wanderer history timeline is not active." };
                // An NPC's actual reply paired with the player's turn proves contact, even in group chat.
                // Bystanders who only overheard it do not become permanent.
                var rows = CharactersStore.SelectConversationTurns9(connection, new Dictionary<string, object> { ["hero"] = hero, ["player"] = player, ["campaign"] = campaign });
                var match = rows.FirstOrDefault(row => {
                    var detail = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                    string turnTimeline = ReadString(detail, "timelineId", "");
                    return string.IsNullOrEmpty(turnTimeline) || turnTimeline == timeline;
                });
                // A full bounded page cannot establish absence; retain rotation protection until resolved.
                if (match == null && rows.Count == 256)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Contact history needs a wider timeline-specific audit." };
                string receipt = match == null ? "" : ReadString(match, "turn_id", "");
                bool contact = match != null;
                if (!contact)
                {
                    var legacy = Program.ReadJsonLinesFromPath(Program.CharacterFile(campaign, hero, "history", "dialogue.jsonl"));
                    var relevant = legacy.Where(row => string.IsNullOrEmpty(ReadString(row, "timelineId", "")) || ReadString(row, "timelineId", "") == timeline).ToArray();
                    if (relevant.Any(row => ReadString(row, "role", "") == "player" && !string.IsNullOrWhiteSpace(ReadString(row, "text", "")))
                        && relevant.Any(row => ReadString(row, "role", "") == "npc" && !string.IsNullOrWhiteSpace(ReadString(row, "text", ""))))
                    { contact = true; receipt = "legacy-dialogue:" + hero; }
                }
                var saved = ReadDictionary(Program.ReadJsonObject(Program.CharacterFile(campaign, hero, "profile.json")), "wandererIdentity");
                if (ReadBool(saved, "protected", false)) { contact = true; receipt = ReadString(saved, "contactReceipt", "saved-contact"); }
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaign, ["timelineId"] = timeline,
                    ["heroStringId"] = hero, ["hasContact"] = contact, ["receipt"] = receipt };
            }
        }
    }
}
