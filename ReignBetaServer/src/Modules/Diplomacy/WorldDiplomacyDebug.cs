using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> QueueRandomDiplomacyDebugEvent(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadBool(payload, "debugMcmEnabled", false))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The MCM debug gate is not enabled." };
            }

            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string commandId = ReadFirstString(payload, "commandId", "debugCommandId");
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "commandId is required." };
            }

            Dictionary<string, object> existingEvent;
            Dictionary<string, object> existingRecord;
            lock (FileLock)
            {
                existingEvent = ReadDiplomaticEventQueue(campaignId).FirstOrDefault(x =>
                    string.Equals(ReadString(x, "debugCommandId", ""), commandId, StringComparison.OrdinalIgnoreCase));
                string existingActionId = ReadString(existingEvent, "actionId", "");
                Dictionary<string, object> actionRow = ReadActionQueueUnlocked(campaignId).FirstOrDefault(x =>
                    string.Equals(ReadString(x, "id", ""), existingActionId, StringComparison.OrdinalIgnoreCase));
                existingRecord = ReadDictionary(actionRow, "record");
            }
            if (existingEvent != null && existingRecord != null)
            {
                return RandomDiplomacyDebugResponse(existingEvent, existingRecord, true);
            }

            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string playerKingdomId = ReadString(payload, "playerKingdomId", "");
            List<Dictionary<string, object>> npcKingdoms = ReadDictionaryList(payload, "kingdoms")
                .Where(x => !ReadBool(x, "isPlayerKingdom", false))
                .Where(x => !string.Equals(ReadString(x, "kingdomId", ""), playerKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "leaderHeroId", "")))
                .Select(x => new Dictionary<string, object>(x, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (npcKingdoms.Count < 2)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "At least two living non-player kingdoms are required for the diplomacy initiative test."
                };
            }

            HashSet<string> npcKingdomIds = new HashSet<string>(npcKingdoms.Select(x => ReadString(x, "kingdomId", "")), StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> kingdom in npcKingdoms)
            {
                kingdom["enemies"] = new ArrayList(ReadStringList(kingdom, "enemies").Where(npcKingdomIds.Contains).ToList());
            }
            payload["kingdoms"] = new ArrayList(npcKingdoms);
            payload["wars"] = new ArrayList(ReadDictionaryList(payload, "wars").Where(x =>
                npcKingdomIds.Contains(ReadString(x, "kingdomAId", "")) && npcKingdomIds.Contains(ReadString(x, "kingdomBId", ""))).ToList());
            payload["agreements"] = new ArrayList(ReadDictionaryList(payload, "agreements").Where(x =>
                npcKingdomIds.Contains(ReadString(x, "actorKingdomId", "")) && npcKingdomIds.Contains(ReadString(x, "targetKingdomId", ""))).ToList());
            payload["relations"] = new ArrayList(ReadDictionaryList(payload, "relations").Where(x =>
                npcKingdomIds.Contains(ReadString(x, "kingdomAId", "")) && npcKingdomIds.Contains(ReadString(x, "kingdomBId", ""))).ToList());
            payload["settlements"] = new ArrayList(ReadDictionaryList(payload, "settlements").Where(x =>
                npcKingdomIds.Contains(ReadString(x, "ownerKingdomId", ""))).ToList());
            payload["allianceMarriageOptions"] = new ArrayList(ReadDictionaryList(payload, "allianceMarriageOptions").Where(x =>
                npcKingdomIds.Contains(ReadString(x, "kingdomAId", "")) && npcKingdomIds.Contains(ReadString(x, "kingdomBId", ""))).ToList());

            Dictionary<string, object> state = ReadDirectorState(campaignId);
            List<Dictionary<string, object>> eligibleRulers = npcKingdoms
                .Where(x => worldDay >= ReadDouble(GetDirectorRulerState(state, x, worldDay), "cooldownUntilDay", 0d))
                .ToList();
            if (eligibleRulers.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "no_ruler_eligible",
                    ["error"] = "Every non-player ruler is currently on the normal diplomacy cooldown."
                };
            }

            Dictionary<string, object> selectedRuler = eligibleRulers[DebugRandomIndex(eligibleRulers.Count)];
            payload["debugInitiativeTest"] = true;
            payload["debugSelectedRulerId"] = ReadString(selectedRuler, "leaderHeroId", "");
            Dictionary<string, object> result = EvaluateWorldDiplomacy(payload);
            result["debugSelectedRulerId"] = ReadString(selectedRuler, "leaderHeroId", "");
            result["rulerName"] = ReadString(result, "rulerName", ReadString(selectedRuler, "leaderName", ""));
            result["actorKingdomName"] = ReadString(result, "actorKingdomName", ReadString(selectedRuler, "name", ""));
            return result;
        }

        private static int DebugRandomIndex(int count)
        {
            if (count <= 1) return 0;
            byte[] bytes = Guid.NewGuid().ToByteArray();
            uint value = BitConverter.ToUInt32(bytes, 0);
            return (int)(value % (uint)count);
        }

        private static Dictionary<string, object> RandomDiplomacyDebugResponse(Dictionary<string, object> diplomaticEvent, Dictionary<string, object> record, bool idempotent)
        {
            string command = ReadString(diplomaticEvent, "command", ReadString(record, "command", ""));
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = "action_queued",
                ["idempotent"] = idempotent,
                ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                ["actionId"] = ReadString(diplomaticEvent, "actionId", ReadFirstString(record, "actionId", "serverActionId")),
                ["command"] = command,
                ["actionLabel"] = DirectorEventTitle(command, true),
                ["actorKingdomName"] = ReadString(diplomaticEvent, "actorKingdomName", ""),
                ["targetKingdomName"] = ReadString(diplomaticEvent, "targetKingdomName", ""),
                ["record"] = record
            };
        }

        private static void AddRandomDiplomacyDebugSelfTests(Action<string, bool, string> add)
        {
            Dictionary<string, object> testedRuler = TestDirectorKingdom("tested", false);
            Dictionary<string, object> otherRuler = TestDirectorKingdom("other", false);
            Dictionary<string, object> world = new Dictionary<string, object>
            {
                ["playerKingdomId"] = "player",
                ["kingdoms"] = new ArrayList { testedRuler, otherRuler },
                ["settlements"] = new ArrayList(),
                ["agreements"] = new ArrayList(),
                ["relations"] = new ArrayList()
            };
            Dictionary<string, object> state = new Dictionary<string, object>();
            SelectDirectorRuler("debug_test", 10, 10d, new List<Dictionary<string, object>> { testedRuler }, world, state, "command-a", out List<Dictionary<string, object>> firstAttempts);
            SelectDirectorRuler("debug_test", 10, 10d, new List<Dictionary<string, object>> { testedRuler }, world, state, "command-a", out List<Dictionary<string, object>> repeatedAttempts);
            add("debug_roll_tests_one_ruler", firstAttempts.Count == 3 && firstAttempts.All(x => ReadString(x, "rulerId", "") == "tested_ruler"), "one selected ruler receives Expansion, Prosperity, and Security attempts");
            bool normalChance = firstAttempts.All(x => Math.Abs(ReadDouble(x, "finalChance", -1d)
                - BlendDirectorChance(ReadDouble(x, "personalityBaseline", 0d), DirectorOpportunityCeiling, ReadDouble(x, "opportunity", 0d))) < 0.0000001d);
            add("debug_roll_uses_normal_chance", normalChance, "debug attempts use the same baseline-to-opportunity curve as production");
            bool deterministicRetry = firstAttempts.Count == repeatedAttempts.Count
                && firstAttempts.Zip(repeatedAttempts, (first, second) => Math.Abs(ReadDouble(first, "roll", -1d) - ReadDouble(second, "roll", -2d)) < double.Epsilon).All(x => x);
            add("debug_roll_is_deterministic_per_command", deterministicRetry, "the same command id produces the same roll for safe retries");
            bool foundNaturalFailure = false;
            for (int index = 0; index < 100 && !foundNaturalFailure; index++)
            {
                Dictionary<string, object> selected = SelectDirectorRuler("debug_test", 10, 10d,
                    new List<Dictionary<string, object>> { testedRuler }, world, new Dictionary<string, object>(), "failure-" + index,
                    out List<Dictionary<string, object>> failureAttempts);
                foundNaturalFailure = selected == null && failureAttempts.Count == 3 && failureAttempts.All(x => !ReadBool(x, "passed", true));
            }
            add("debug_roll_can_produce_no_event", foundNaturalFailure, "the MCM route does not force a successful initiative");
        }
    }
}
