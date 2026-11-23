using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static readonly ConcurrentDictionary<string, object> AutonomousMailLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static void EnsureAutonomousCorrespondenceSchema(ReignDbConnection connection)
        {
            RelationshipsSchema.CreateAutonomousCorrespondenceChecks(connection);
            RelationshipsSchema.CreateAutonomousCorrespondenceCooldowns(connection);
        }

        internal static Dictionary<string, object> AutonomousCorrespondenceCandidatesApi(
            Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string playerId = ReadString(payload, "playerId", "");
            if (string.IsNullOrWhiteSpace(playerId))
                return new Dictionary<string, object> { ["ok"] = false, ["heroIds"] = new List<string>() };
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                var rows = RelationshipsStore.SelectRelationshipPairChemistry6(connection, new Dictionary<string, object> { ["player"] = playerId });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["heroIds"] = rows.Select(row =>
                        ReadString(row, "hero_a_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                            ? ReadString(row, "hero_b_id", "") : ReadString(row, "hero_a_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
            }
        }

        internal static Dictionary<string, object> AutonomousCorrespondenceEvaluateApi(
            Dictionary<string, object> payload,
            Func<string, string, string, Dictionary<string, object>, double, bool, bool,
                Dictionary<string, object>> generate = null, Dictionary<string, object> settings = null)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string playerId = ReadString(payload, "playerId", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            if (string.IsNullOrWhiteSpace(playerId) || day <= 0d)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Player and campaign day are required." };
            settings ??= SettingsService.LoadSettings();
            double frequency = SettingsService.CorrespondenceOption(settings, "correspondenceFrequencyMultiplier");
            if (frequency == 0d) return TestDict("ok", true, "sent", 0, "introductions", 0);
            int slotDay = (int)Math.Floor(day / 3d) * 3;
            object gate = AutonomousMailLocks.GetOrAdd(campaignId, _ => new object());
            lock (gate)
            {
                int sent = 0;
                int introductions = 0;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureRelationshipAndCorrespondenceSchema(connection);
                    EnsureAutonomousCorrespondenceSchema(connection);
                    string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
                    var pairs = RelationshipsStore.SelectRelationshipPairChemistry6(connection, new Dictionary<string, object> { ["player"] = playerId });
                    var scores = pairs.ToDictionary(row =>
                        ReadString(row, "hero_a_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                            ? ReadString(row, "hero_b_id", "") : ReadString(row, "hero_a_id", ""),
                        row => ReadString(row, "hero_a_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                            ? ReadInt(row, "affinity_b_to_a", 0) : ReadInt(row, "affinity_a_to_b", 0),
                        StringComparer.OrdinalIgnoreCase);
                    var candidates = ReadDictionaryList(payload, "candidates")
                        .Where(row => scores.ContainsKey(ReadString(row, "heroId", "")))
                        .OrderBy(_ => Guid.NewGuid()).ToList();
                    var lastIntroduction = RelationshipsStore.SelectLetters(connection, new Dictionary<string, object> { ["prefix"] = "autonomous_relationship:" + timeline + ":%" })
                        .FirstOrDefault();
                    bool introductionSent = lastIntroduction != null
                        && (ReadDouble(lastIntroduction, "dispatch_day", 0d) >= slotDay
                            || day - ReadDouble(lastIntroduction, "dispatch_day", 0d) < 3d);
                    foreach (Dictionary<string, object> candidate in candidates)
                    {
                        string senderId = ReadString(candidate, "heroId", "");
                        int affinity = scores[senderId];
                        int chance = SettingsService.AutonomousLetterChance(affinity, frequency);
                        var introductionsForSender = RelationshipsStore.SelectLetters2(connection, new Dictionary<string, object> { ["sender"] = senderId, ["player"] = playerId });
                        if (introductionsForSender.Any(row => ReadString(row, "status", "") == "in_transit")) continue;
                        bool known = ReadBool(candidate, "known", false) || introductionsForSender.Count > 0;
                        if ((!known && affinity < 20) || (!known && introductionSent)) continue;
                        var cooldown = RelationshipsStore.SelectAutonomousCorrespondenceCooldowns(connection, new Dictionary<string, object> { ["sender"] = senderId }).FirstOrDefault();
                        if (cooldown != null && day < ReadDouble(cooldown, "next_eligible_day", 0d)) continue;
                        var key = new Dictionary<string, object> { ["timeline"] = timeline, ["slot"] = slotDay, ["sender"] = senderId };
                        var check = RelationshipsStore.SelectAutonomousCorrespondenceChecks(connection, key).FirstOrDefault();
                        if (check != null && ReadString(check, "status", "") != "triggered") continue;
                        if (check != null) chance = ReadInt(check, "chance", chance);
                        int roll = check == null ? RandomNumberGenerator.GetInt32(1, 101) : ReadInt(check, "roll", 101);
                        if (check == null)
                        {
                            RelationshipsStore.InsertAutonomousCorrespondenceChecks(connection, new Dictionary<string, object> {
                                    ["timeline"] = timeline, ["slot"] = slotDay, ["sender"] = senderId,
                                    ["chance"] = chance, ["roll"] = roll,
                                    ["status"] = roll <= chance ? "triggered" : "missed",
                                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                        }
                        if (roll > chance) continue;
                        string source = known ? "npc_initiated" : "npc_introduction";
                        string reason = "autonomous_relationship:" + timeline + ":" + slotDay.ToString(CultureInfo.InvariantCulture) + ":" + senderId;
                        var existing = RelationshipsStore.SelectLetters3(connection, new Dictionary<string, object> { ["sender"] = senderId, ["player"] = playerId, ["source"] = source, ["reason"] = reason }).FirstOrDefault();
                        string letterId = existing == null ? "" : ReadString(existing, "letter_id", "");
                        if (existing == null)
                        {
                            try
                            {
                                string senderName = ReadString(candidate, "name", senderId);
                                string playerName = ReadString(payload, "playerName", "Player");
                                var actionPayload = new Dictionary<string, object> { ["recipient"] = ReadDictionary(candidate, "profile") };
                                if (DialogueService.UsesStructuredActions(candidate))
                                {
                                    actionPayload["structuredActionProtocolVersion"] = Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion;
                                    actionPayload["structuredActionContext"] = ReadDictionary(candidate, "structuredActionContext");
                                }
                                var synthetic = new Dictionary<string, object>
                                {
                                    ["sender_name"] = playerName,
                                    ["body"] = known
                                        ? "Composition brief: write an unsolicited personal letter to the player, grounded in your current relationship and memories. No letter was received."
                                        : "Composition brief: introduce yourself by name in a first letter to the player. You have not met. Do not invent shared encounters or private knowledge. No letter was received.",
                                    ["payload_json"] = Json.Serialize(actionPayload),
                                    ["reason"] = reason
                                };
                                Dictionary<string, object> generated = generate == null
                                    ? GenerateNpcLetter(campaignId, senderId, playerId, synthetic, day, true, !known)
                                    : generate(campaignId, senderId, playerId, synthetic, day, true, !known);
                                string body = ReadString(generated, "body", "");
                                if (!ReadBool(generated, "shouldReply", false) || string.IsNullOrWhiteSpace(body))
                                    throw new InvalidOperationException("The correspondence model produced no accepted letter.");
                                double dispatchDay = Math.Max(day, WorldSimulationService.LatestKnownWorldDay(connection, campaignId));
                                double transit = Math.Max(0.25d, ReadDouble(candidate, "transitDays", 3d));
                                var letter = InsertLetter(connection, senderId, senderName, playerId, playerName,
                                    body, source, reason, "", ReadString(candidate, "originId", ""),
                                    ReadString(candidate, "destinationId", ""), dispatchDay, dispatchDay + transit, generated);
                                letterId = ReadString(letter, "letterId", "");
                                sent++;
                                if (!known) { introductionSent = true; introductions++; }
                            }
                            catch (Exception ex)
                            {
                                RelationshipsStore.UpdateAutonomousCorrespondenceChecks(connection, new Dictionary<string, object> { ["timeline"] = timeline, ["slot"] = slotDay,
                                        ["sender"] = senderId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                                OperationsService.LogOperational("correspondence.autonomous_generation_failed",
                                    new Dictionary<string, object> { ["campaignId"] = campaignId,
                                        ["senderId"] = senderId, ["error"] = ex.Message });
                                continue;
                            }
                        }
                        int cooldownDays = RandomNumberGenerator.GetInt32(3, 16);
                        RelationshipsStore.UpsertAutonomousCorrespondenceCooldowns(connection, new Dictionary<string, object> { ["sender"] = senderId, ["day"] = day,
                                ["next"] = day + cooldownDays, ["days"] = cooldownDays,
                                ["letter"] = letterId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                        RelationshipsStore.UpdateAutonomousCorrespondenceChecks2(connection, new Dictionary<string, object> { ["timeline"] = timeline, ["slot"] = slotDay,
                                ["sender"] = senderId, ["letter"] = letterId,
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                    }
                }
                return new Dictionary<string, object> { ["ok"] = true, ["sent"] = sent, ["introductions"] = introductions };
            }
        }

#if !REIGN_EXCLUDE_VERIFICATION
        private static List<Dictionary<string, object>> RunAutonomousCorrespondenceSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            results.AddRange(RunCorrespondenceTransitAssertions());
            results.AddRange(RunCorrespondenceDeliveryAssertions());
            results.AddRange(RunNativeRelationEffectAssertions());
            Action<string, bool, string> add = (id, passed, detail) => results.Add(
                new Dictionary<string, object> { ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "relationship_system", ["caseId"] = id,
                    ["name"] = id, ["summary"] = detail, ["durationMs"] = 0 });
            string campaignId = "autonomous_mail_test_" + Guid.NewGuid().ToString("N");
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureRelationshipAndCorrespondenceSchema(connection);
                    foreach (var pair in new[] { ("known", 100), ("intro_a", 100),
                        ("intro_b", 100), ("unmet_low", 19), ("failing", 100) })
                        RelationshipsStore.InsertRelationshipPairChemistry(connection, new Dictionary<string, object> { ["key"] = pair.Item1 + "|player",
                                ["sender"] = pair.Item1, ["affinity"] = pair.Item2,
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
                var candidates = new List<Dictionary<string, object>>();
                foreach (var sender in new[] { "known", "intro_a", "intro_b", "unmet_low", "failing" })
                    candidates.Add(new Dictionary<string, object> { ["heroId"] = sender,
                        ["name"] = sender, ["known"] = sender == "known" || sender == "failing", ["transitDays"] = 1d,
                        ["profile"] = new Dictionary<string, object> { ["heroStringId"] = sender,
                            ["name"] = sender } });
                var request = new Dictionary<string, object> { ["campaignId"] = campaignId,
                    ["playerId"] = "player", ["playerName"] = "Player", ["worldDay"] = 12d,
                    ["candidates"] = candidates };
                int generatedCount = 0;
                bool noPlaceholderDuringGeneration = true;
                Func<string, string, string, Dictionary<string, object>, double, bool, bool,
                    Dictionary<string, object>> generate = (campaign, sender, player, brief, day,
                        autonomous, introduction) =>
                    {
                        generatedCount++;
                        using (ReignDbConnection inspection = ReignDatabase.OpenCampaignConnection(campaign))
                            noPlaceholderDuringGeneration &= RelationshipsStore.SelectLetters4(inspection, new Dictionary<string, object> { ["sender"] = sender }).Count == 0;
                        if (sender == "failing") throw new InvalidOperationException("Injected model failure.");
                        return new Dictionary<string, object> { ["shouldReply"] = true,
                            ["body"] = introduction ? "I am " + sender + ". I write to introduce myself."
                                : "A letter from " + sender + "." };
                    };
                var disabled = AutonomousCorrespondenceEvaluateApi(request, generate, TestDict("correspondenceFrequencyMultiplier", 0d));
                using (var disabledInspection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureAutonomousCorrespondenceSchema(disabledInspection);
                    add("autonomous_mail_zero_frequency_no_generation", ReadInt(disabled, "sent", -1) == 0 && generatedCount == 0
                        && RelationshipsStore.SelectAutonomousCorrespondenceChecks2(disabledInspection).Count == 0
                        && RelationshipsStore.SelectAutonomousCorrespondenceCooldowns2(disabledInspection).Count == 0,
                        "Zero frequency returns before rolls, generation or cooldown writes; ordinary delivery has its independent path.");
                }
                var first = AutonomousCorrespondenceEvaluateApi(request, generate, SettingsService.DefaultSettings());
                var second = AutonomousCorrespondenceEvaluateApi(request, generate, TestDict("correspondenceFrequencyMultiplier", .25d));
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    var letters = RelationshipsStore.SelectLetters5(connection);
                    var cooldowns = RelationshipsStore.SelectAutonomousCorrespondenceCooldowns2(connection);
                    var low = RelationshipsStore.SelectAutonomousCorrespondenceChecks3(connection);
                    add("autonomous_mail_directional_threshold_and_intro_cap",
                        ReadInt(first, "sent", -1) == 2
                        && ReadInt(first, "introductions", -1) == 1
                        && letters.Count == 2
                        && letters.Count(row => ReadString(row, "source", "") == "npc_introduction") == 1
                        && low.Count == 0,
                        "Known +100 writes, one unmet +100 introduces, and an unmet +19 cannot roll.");
                    add("autonomous_mail_idempotent_complete_dispatch",
                        ReadInt(second, "sent", -1) == 0 && generatedCount == 3
                        && noPlaceholderDuringGeneration
                        && letters.All(row => ReadString(row, "status", "") == "in_transit"
                            && !string.IsNullOrWhiteSpace(ReadString(row, "body", "")))
                        && cooldowns.Count == 2
                        && cooldowns.All(row => ReadInt(row, "cooldown_days", 0) >= 3
                            && ReadInt(row, "cooldown_days", 0) <= 15),
                        "Repeating a slot neither regenerates nor duplicates complete in-transit letters; cooldowns are inclusive 3–15 days.");
                    add("autonomous_mail_failed_generation_has_no_noticeable_mail",
                        letters.All(row => ReadString(row, "sender_id", "") != "failing")
                        && cooldowns.All(row => ReadString(row, "sender_id", "") != "failing")
                        && noPlaceholderDuringGeneration,
                        "Generation begins before any mail row exists; a failed model response stores no letter or cooldown.");
                    var inboxRequest = new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["playerId"] = "player",
                        ["contactIds"] = new List<string> { "known" }
                    };
                    var beforeDelivery = CorrespondenceThreadsApi(inboxRequest);
                    var delivered = CorrespondenceTickApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["playerId"] = "player", ["worldDay"] = 20d
                    });
                    var inbox = CorrespondenceThreadsApi(inboxRequest);
                    add("autonomous_intro_visible_before_native_meeting",
                        ReadDictionaryList(beforeDelivery, "letters").Count == 0
                        && ReadDictionaryList(delivered, "deliveredLetters").Count == 2
                        && ReadDictionaryList(inbox, "letters").Count == 2,
                        "Incoming letters remain hidden during transit, then complete delivered introductions appear even when the sender is not a native known contact.");
                }
            }
            catch (Exception ex)
            {
                add("autonomous_mail_fixture", false, ex.ToString());
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaignId));
            }
            return results;
        }
#endif
    }
}
