using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.CastleChat;

namespace ReignBetaServer
{
    internal static class CastleChatSelfTests
    {
        internal static void Append(List<Dictionary<string, object>> results)
        {
            Action<string, bool, string, object> add = (id, passed, summary, data) =>
                results.Add(new Dictionary<string, object>
                {
                    { "ok", true }, { "passed", passed }, { "suite", "court_system" },
                    { "caseId", id }, { "name", id }, { "summary", summary },
                    { "data", data ?? new Dictionary<string, object>() }, { "durationMs", 0 }
                });

            add("castle_time_boundaries",
                CastleScheduleEngine.GetTimeBlock(4.999) == CastleTimeBlock.Night &&
                CastleScheduleEngine.GetTimeBlock(5) == CastleTimeBlock.Morning &&
                CastleScheduleEngine.GetTimeBlock(10.999) == CastleTimeBlock.Morning &&
                CastleScheduleEngine.GetTimeBlock(11) == CastleTimeBlock.Afternoon &&
                CastleScheduleEngine.GetTimeBlock(16.999) == CastleTimeBlock.Afternoon &&
                CastleScheduleEngine.GetTimeBlock(17) == CastleTimeBlock.Evening &&
                CastleScheduleEngine.GetTimeBlock(20.999) == CastleTimeBlock.Evening &&
                CastleScheduleEngine.GetTimeBlock(21) == CastleTimeBlock.Night,
                "Castle schedules use the four exact daily time boundaries.", null);

            List<CastleScheduleCandidate> candidates = Enumerable.Range(0, 28).Select(i =>
                new CastleScheduleCandidate
                {
                    HeroId = "hero_" + i, IsFemale = i % 2 == 0, IsLord = true,
                    Charm = i * 3, Steward = 120 - i, Tactics = 20 + i,
                    Riding = 50 + i, Scouting = i * 2, Leadership = 80,
                    HonorPercent = i == 0 ? 35 : 60, BoldnessPercent = i == 0 ? 70 : 50,
                    IsSpouse = i == 1
                }).ToList();
            CastleScheduleResult a = CastleScheduleEngine.Build(candidates, null, "seed", 12, CastleTimeBlock.Evening);
            CastleScheduleResult b = CastleScheduleEngine.Build(candidates, null, "seed", 12, CastleTimeBlock.Evening);
            List<string> all = a.Rooms.SelectMany(x => x.Value).ToList();
            add("castle_deterministic_capacity_no_duplicates",
                a.Rooms.All(x => x.Value.SequenceEqual(b.Rooms[x.Key])) &&
                a.Rooms.All(x => x.Value.Count <= CastleScheduleEngine.Capacity(x.Key)) &&
                all.Count == all.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "Scheduling is stable, respects room capacity, and assigns no hero twice after the baths override.",
                a.Rooms.ToDictionary(x => x.Key.ToString(), x => (object)x.Value));

            List<string> bath = a.Rooms[CastleRoom.Baths];
            bool oneSex = bath.Select(id => candidates.First(x => x.HeroId == id).IsFemale).Distinct().Count() <= 1;
            add("castle_baths_same_sex_2_to_4", bath.Count >= 2 && bath.Count <= 4 && oneSex,
                "Baths select one seed plus 1d3 same-sex occupants from the full eligible pool.", bath);

            string firstBath = bath.FirstOrDefault();
            List<CastleBathHistory> history = candidates.Select(x => new CastleBathHistory
            {
                HeroId = x.HeroId, SelectionCount = x.HeroId == firstBath ? 9 : 0,
                LastDay = x.HeroId == firstBath ? 12 : int.MinValue
            }).ToList();
            CastleScheduleResult rotated = CastleScheduleEngine.Build(candidates, history, "seed", 13, CastleTimeBlock.Evening);
            add("castle_bath_fair_rotation", rotated.Rooms[CastleRoom.Baths].FirstOrDefault() != firstBath,
                "Least-used and least-recently selected nobles rotate ahead of repeated bath occupants.",
                rotated.Rooms[CastleRoom.Baths]);

            add("castle_royal_bedroom_eligibility",
                CastleScheduleEngine.IsRoyalBedroomEligible(candidates[0]) &&
                CastleScheduleEngine.IsRoyalBedroomEligible(candidates[1]) &&
                !CastleScheduleEngine.IsRoyalBedroomEligible(candidates[2]),
                "Royal Bedroom eligibility accepts spouse/lover or low-honor high-boldness only.", null);

            var openingPayload = new Dictionary<string, object>
            {
                { "castleOpening", true }, { "castleRoomName", "Training Yard" },
                { "playerText", string.Empty }, { "sceneContext", "base" }
            };
            bool appliedOpening = Program.ApplyCastleChatMode(openingPayload, "room prompt");
            add("castle_opening_uses_npc_initiative_contract",
                appliedOpening
                && Convert.ToBoolean(openingPayload["approachOpening"])
                && string.Equals(Convert.ToString(openingPayload["turnType"]),
                    "npc_approach_opening", StringComparison.Ordinal)
                && string.Equals(Convert.ToString(openingPayload["playerText"]),
                    string.Empty, StringComparison.Ordinal),
                "Castle openings use the established NPC-initiative path and never fabricate a player line.",
                openingPayload);

            add("castle_opening_detects_implied_player_speech",
                Program.CastleOpeningReplyImpliesPlayerSpeech(
                    "Your Grace, I hear you. I ask leave to state my concern.")
                && Program.CastleOpeningReplyImpliesPlayerSpeech(
                    "Your Grace, as you asked, I have brought the dispatches.")
                && Program.CastleOpeningReplyImpliesPlayerSpeech(
                    "The ruler's request will be obeyed.")
                && !Program.CastleOpeningReplyImpliesPlayerSpeech(
                    "Your Grace. I was reviewing correspondence when you entered.")
                && !Program.CastleOpeningReplyImpliesPlayerSpeech(
                    "Lady Asta, I hear you. I will wait until you have finished."),
                "Castle openings detect replies that invent silent-ruler speech without rejecting legitimate NPC initiative or NPC-to-NPC continuity.",
                null);

            var continuedPayload = new Dictionary<string, object>
            {
                { "castleOpening", false }, { "playerText", "Continue." }
            };
            bool appliedContinued = Program.ApplyCastleChatMode(continuedPayload, "room prompt");
            add("castle_continuation_preserves_player_turn_contract",
                appliedContinued
                && !continuedPayload.ContainsKey("approachOpening")
                && string.Equals(Convert.ToString(continuedPayload["playerText"]),
                    "Continue.", StringComparison.Ordinal),
                "Continued Castle Chat retains the normal player-reply contract.",
                continuedPayload);

            var foreignRuntime = new Dictionary<string, object>
            {
                { "settlementName", "Quyaz" },
                { "settlementOwnerName", "Emir Hulyan" },
                { "realmRulerName", "Sultan Unqid" },
                { "playerName", "Arenicos" }
            };
            string foreignLayer = "[KEEP OWNERSHIP MODE: FOREIGN] {{player_name}} visits {{settlement_name}}. "
                + "The holding belongs to {{settlement_owner_name}} and the realm is ruled by {{realm_ruler_name}}.";
            string composedForeign = Program.ComposeForeignCastleChatPrompt(
                "The player is the regent who governs this fortress.", foreignLayer, foreignRuntime);
            add("castle_foreign_keep_prompt_overrides_player_ruler_assumption",
                composedForeign.Contains("[KEEP OWNERSHIP MODE: FOREIGN]")
                && composedForeign.Contains("Arenicos visits Quyaz")
                && composedForeign.Contains("Emir Hulyan")
                && composedForeign.Contains("Sultan Unqid")
                && composedForeign.StartsWith("The player is the regent", StringComparison.Ordinal),
                "A concrete foreign-ownership layer is appended after the room prompt so its later instructions override legacy player-ruler wording.",
                new Dictionary<string, object> { { "prompt", composedForeign } });

            Dictionary<string, string> promptDefaults = Program.CastleChatPromptDefaults();
            string defaultForeignImage = promptDefaults["castle_chat_foreign_keep_image.txt"];
            string defaultForeignDialogue = promptDefaults["castle_chat_foreign_keep_dialogue.txt"];
            int testedOwnershipPromptPairs = 0;
            var ownershipPromptFailures = new List<string>();
            foreach (string culture in Program.CastleChatCultures)
            {
                foreach (string roomName in Program.CastleChatRooms)
                {
                    string imageName = "castle_chat_" + culture + "_" + roomName + "_image.txt";
                    string dialogueName = "castle_chat_" + culture + "_" + roomName + "_dialogue.txt";
                    string playerImage = promptDefaults[imageName];
                    string playerDialogue = promptDefaults[dialogueName];
                    string foreignImage = Program.ComposeForeignCastleChatPrompt(playerImage, defaultForeignImage, foreignRuntime);
                    string foreignDialogue = Program.ComposeForeignCastleChatPrompt(playerDialogue, defaultForeignDialogue, foreignRuntime);
                    testedOwnershipPromptPairs += 2;
                    if (string.IsNullOrWhiteSpace(playerImage)
                        || string.IsNullOrWhiteSpace(playerDialogue)
                        || playerImage.Contains("[KEEP OWNERSHIP MODE: FOREIGN]")
                        || playerDialogue.Contains("[KEEP OWNERSHIP MODE: FOREIGN]")
                        || !foreignImage.Contains("[KEEP OWNERSHIP MODE: FOREIGN]")
                        || !foreignDialogue.Contains("[KEEP OWNERSHIP MODE: FOREIGN]")
                        || !foreignImage.Contains("Quyaz")
                        || !foreignDialogue.Contains("Quyaz")
                        || !foreignImage.Contains("Arenicos")
                        || !foreignDialogue.Contains("Arenicos")
                        || !foreignImage.Contains("Emir Hulyan")
                        || !foreignDialogue.Contains("Emir Hulyan")
                        || !foreignImage.Contains("Sultan Unqid")
                        || !foreignDialogue.Contains("Sultan Unqid"))
                    {
                        ownershipPromptFailures.Add(culture + "/" + roomName);
                    }
                }
            }
            add("castle_all_cultures_rooms_support_player_and_foreign_ownership",
                testedOwnershipPromptPairs == Program.CastleChatCultures.Length * Program.CastleChatRooms.Length * 2
                && ownershipPromptFailures.Count == 0,
                "Every culture and Castle Layout room retains its player-ruled prompt and produces a concrete foreign-visitor image/dialogue override.",
                new Dictionary<string, object>
                {
                    { "testedPromptPairs", testedOwnershipPromptPairs },
                    { "cultureCount", Program.CastleChatCultures.Length },
                    { "roomCount", Program.CastleChatRooms.Length },
                    { "failures", ownershipPromptFailures }
                });
        }
    }
}
