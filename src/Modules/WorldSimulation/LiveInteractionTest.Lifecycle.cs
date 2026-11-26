using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static bool TransitionHistoryWasAppended(HashSet<int> ordinals)
        {
            if (ordinals == null || ordinals.Count < 2) return false;
            int newest = ordinals.Max();
            return newest >= 2 && ordinals.Any(value => value < newest);
        }

        internal static List<Dictionary<string, object>> CollectLiveTestCommandAuditEvidence(
            Dictionary<string, object> run, Dictionary<string, object> command)
        {
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string correlation in ReadStringList(command, "correlationIds")
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                Dictionary<string, object> audit = OperationsService.AuditQuery(new Dictionary<string, string>
                {
                    ["campaignId"] = ReadString(run, "campaignId", ""),
                    ["correlationId"] = correlation,
                    ["limit"] = "500",
                    ["oldestFirst"] = "true"
                });
                foreach (Dictionary<string, object> row in ReadDictionaryList(audit, "entries"))
                {
                    string auditId = ReadString(row, "auditId", "");
                    if (string.IsNullOrWhiteSpace(auditId) || seen.Add(auditId)) evidence.Add(row);
                }
            }
            return evidence;
        }

        internal static bool LiveTestRepliesShowGroupAwareness(List<Dictionary<string, object>> replies)
        {
            if (replies == null || replies.Count < 2) return false;
            for (int index = 1; index < replies.Count; index++)
            {
                Dictionary<string, object> reply = replies[index];
                string speakerId = ReadFirstString(reply, "heroId", "speakerHeroStringId");
                List<Dictionary<string, object>> earlierReplies =
                    replies.Take(index).ToList();
                HashSet<string> earlierIds = new HashSet<string>(
                    earlierReplies.Select(earlier =>
                            ReadFirstString(
                                earlier, "heroId",
                                "speakerHeroStringId"))
                        .Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);
                string target = ReadFirstString(reply, "reactionTargetHeroStringId", "reactionTargetHeroId");
                if (!string.IsNullOrWhiteSpace(target) && earlierIds.Contains(target)
                    && !target.Equals(speakerId, StringComparison.OrdinalIgnoreCase)) return true;
                if (LiveTestReplyRelationshipAssessments(reply).Any(
                    assessment =>
                    {
                        string assessedTarget = ReadFirstString(
                            assessment, "targetHeroStringId",
                            "targetId");
                        return !string.IsNullOrWhiteSpace(
                                assessedTarget)
                            && earlierIds.Contains(assessedTarget)
                            && !assessedTarget.Equals(
                                speakerId,
                                StringComparison.OrdinalIgnoreCase);
                    }))
                    return true;
                string text = ReadString(reply, "text", "");
                Dictionary<string, List<string>> earlierNames =
                    earlierReplies
                        .Select(earlier => ReadFirstString(
                            earlier, "heroName", "speakerName"))
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => new
                        {
                            Full = name,
                            First = name.Trim().Split(
                                new[] { ' ' },
                                StringSplitOptions.RemoveEmptyEntries)
                                .FirstOrDefault() ?? ""
                        })
                        .GroupBy(
                            name => name.First,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Select(name => name.Full).ToList(),
                            StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<string>> nameGroup in earlierNames)
                {
                    if (nameGroup.Value.Any(name =>
                            text.IndexOf(
                                name,
                                StringComparison.OrdinalIgnoreCase) >= 0))
                        return true;
                    if (nameGroup.Key.Length >= 4
                        && nameGroup.Value.Count == 1
                        && Regex.IsMatch(
                            text,
                            @"(?<![\p{L}\p{N}])"
                                + Regex.Escape(nameGroup.Key)
                                + @"(?![\p{L}\p{N}])",
                            RegexOptions.IgnoreCase
                                | RegexOptions.CultureInvariant))
                        return true;
                }
            }
            return false;
        }

        private static bool LiveTestRepliesShowDistinctAgency(
            List<Dictionary<string, object>> replies,
            out string evidence)
        {
            List<Dictionary<string, object>> active = (replies
                    ?? new List<Dictionary<string, object>>())
                .Where(reply => !string.IsNullOrWhiteSpace(
                    ReadString(reply, "text", "")))
                .ToList();
            if (active.Count < 2)
            {
                evidence = "Fewer than two speaking NPC replies were available.";
                return false;
            }
            for (int left = 0; left < active.Count; left++)
            {
                for (int right = left + 1;
                    right < active.Count; right++)
                {
                    string leftText = ReadString(active[left], "text", "");
                    string rightText = ReadString(active[right], "text", "");
                    double similarity = DialogueService.RoleplayTextSimilarity(
                        leftText, rightText);
                    if (similarity < 0.72d) continue;
                    evidence = FirstNonEmpty(
                            ReadString(active[left], "heroName", ""),
                            ReadString(active[left], "heroId", ""))
                        + " and " + FirstNonEmpty(
                            ReadString(active[right], "heroName", ""),
                            ReadString(active[right], "heroId", ""))
                        + " converged at similarity "
                        + similarity.ToString("0.00", CultureInfo.InvariantCulture)
                        + ".";
                    return false;
                }
            }
            int distinctAgency = active
                .Select(reply =>
                {
                    Dictionary<string, object> brief =
                        ReadDictionary(reply, "decisionBrief")
                        ?? ReadDictionary(reply, "decision_brief")
                        ?? new Dictionary<string, object>();
                    string signature = string.Join(" ",
                        new[]
                        {
                            ReadString(brief, "decision", ""),
                            string.Join(" ", ReadStringList(
                                brief, "goals")),
                            string.Join(" ", ReadStringList(
                                brief, "constraints")),
                            ReadFirstString(
                                reply,
                                "reactionTargetHeroStringId",
                                "reactionTargetHeroId")
                        });
                    if (string.IsNullOrWhiteSpace(signature))
                        signature = ReadString(reply, "text", "");
                    return Regex.Replace(
                        signature.ToLowerInvariant(),
                        @"\s+", " ").Trim();
                })
                .Distinct(StringComparer.Ordinal)
                .Count();
            evidence = distinctAgency >= 2
                ? active.Count + " speakers advanced the beat with distinct wording and agency evidence."
                : "The speaking NPCs exposed no distinct decision, goal, constraint, target, or substantive wording.";
            return distinctAgency >= 2;
        }

        private static bool StructuredActionEnrollmentValid(Dictionary<string, object> enrollment, string campaignId)
        {
            if (enrollment == null) return false;
            string current = ReadString(enrollment, "disposableSaveName", "");
            string baseline = ReadString(enrollment, "protectedBaselineSaveName", "");
            return ReadString(enrollment, "source", "") == "reign_mcp_structured_actions"
                && ReadString(enrollment, "campaignId", "") == campaignId && campaignId.Length > 0
                && ReadString(enrollment, "campaignTestRunId", "").Length > 0
                && ReadString(enrollment, "timelineId", "").Length > 0
                && current.Length > 0 && baseline.Length > 0 && !string.Equals(current, baseline, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool NpcLivesEnrollmentValid(Dictionary<string, object> enrollment, string campaignId)
        {
            if (enrollment == null || ReadString(enrollment, "source", "") != "reign_mcp_npc_lives"
                || !ReadBool(enrollment, "recoveryCheckpointVerified", false)) return false;
            var contract = CloneDictionary(enrollment); contract["source"] = "reign_mcp_structured_actions";
            return StructuredActionEnrollmentValid(contract, campaignId);
        }

        internal static bool PersonalGossipEnrollmentValid(Dictionary<string, object> enrollment, string campaignId)
        {
            if (enrollment == null || ReadString(enrollment, "source", "") != "reign_mcp_personal_gossip"
                || !ReadBool(enrollment, "recoveryCheckpointVerified", false)) return false;
            var contract = CloneDictionary(enrollment); contract["source"] = "reign_mcp_structured_actions";
            return StructuredActionEnrollmentValid(contract, campaignId);
        }

        internal static Dictionary<string, object> NormalizeLiveTestCommand(Dictionary<string, object> run, Dictionary<string, object> input)
        {
            input = input ?? new Dictionary<string, object>();
            if (input.ContainsKey("schemaVersion") && !new[] { 1, LiveTestSchemaVersion }.Contains(ReadInt(input, "schemaVersion", LiveTestSchemaVersion))) return null;
            string operation = ReadString(input, "operation", ReadString(input, "op", "")).Trim().ToLowerInvariant();
            if (!LiveTestOperations.Contains(operation)) return null;
            string mode = NormalizeLiveTestMode(ReadString(input, "mode", ReadString(run, "mode", "individual_chat")));
            if (!LiveTestModes.Contains(mode)) return null;
            if (operation == "conversation_clock_probe")
            {
                var enrollment = ReadDictionary(run, "enrollment");
                if (mode != "individual_chat" || ReadString(run, "mode", "") != "individual_chat"
                    || enrollment == null || ReadString(enrollment, "source", "") != "reign_mcp_conversation_clock"
                    || !ReadBool(enrollment, "recoveryCheckpointVerified", false)
                    || !new[] { "observe", "pulse" }.Contains(ReadString(input, "profile", ""))
                    || ReadString(input, "confirmation", "") != "probe Reign conversation clock on disposable save"
                    || string.IsNullOrWhiteSpace(ReadString(input, "probeId", ""))
                    || ReadString(input, "probeId", "").Length > 128
                    || ReadString(input, "expectedSaveName", "") != ReadString(enrollment, "disposableSaveName", "")) return null;
                var scope = CloneDictionary(enrollment); scope["source"] = "reign_mcp_structured_actions";
                if (!StructuredActionEnrollmentValid(scope, ReadString(run, "campaignId", ""))) return null;
                // No caller-selected duration, raw clock value or action is admitted.
                if (input.ContainsKey("minutes") || input.ContainsKey("days") || input.ContainsKey("ticks")) return null;
            }
            if (operation == "world_economy_test")
            {
                string profile = ReadString(input, "profile", "");
                if (profile != "cohort_control" || ReadString(input, "source", "") != "cohort_disable") try
                {
                    foreach (string key in new[] { "cohortDurationDays", "cohortWallHours", "cohortSampleIntervalDays" })
                        if (input.TryGetValue(key, out var raw) && !(raw is byte || raw is short || raw is int || raw is long || raw is float || raw is double || raw is decimal)) return null;
                    foreach (string key in new[] { "cohortBlockOtherWars", "cohortSuppressRebellions" })
                        if (input.TryGetValue(key, out var raw) && !(raw is bool)) return null;
                    foreach (string key in new[] { "peaceKingdomIds", "warPairs" })
                        if (input.TryGetValue(key, out var raw) && !(raw is string)) return null;
                    var cohortPlan = new Reign.Core.Contracts.Economy.WarEconomyCohortPlan(
                        ReadDouble(input, "cohortDurationDays", 126), ReadDouble(input, "cohortWallHours", 6),
                        ReadDouble(input, "cohortSampleIntervalDays", 1), ReadBool(input, "cohortBlockOtherWars", true),
                        ReadBool(input, "cohortSuppressRebellions", true), ReadString(input, "peaceKingdomIds", "auto"), ReadString(input, "warPairs", "auto"));
                    if (profile != "cohort_control" && (cohortPlan.DurationDays != 126 || cohortPlan.WallHours != 6 || cohortPlan.SampleIntervalDays != 1
                        || !cohortPlan.BlockOtherWars || !cohortPlan.SuppressRebellions || cohortPlan.PeaceKingdomIds != "auto" || cohortPlan.WarPairs != "auto")) return null;
                }
                catch (ArgumentException) { return null; }
                if(profile=="raid_partial_setup")
                {
                    if(input.TryGetValue("damagePercent",out var rawDamage)
                        && !(rawDamage is byte || rawDamage is short || rawDamage is int || rawDamage is long || rawDamage is float || rawDamage is double || rawDamage is decimal))return null;
                    if(!new double[]{0,4,6,10,24,25,40}.Contains(ReadDouble(input,"damagePercent",10)))return null;
                }
                if (mode != "passive_world" || ReadString(run, "mode", "") != "passive_world"
                    || !new[] { "snapshot", "npc_caravan_probe", "household_stipend_probe", "recruitment", "production_probe", "relief_probe", "funding_probe", "market_food_probe", "household_probe", "raid_food_probe", "raid_partial_setup", "raid_setup", "readiness_probe", "replacement_setup", "funding_trace", "civilian_crew_probe", "ship_funding_probe", "cohort_control", "save_prepare", "save_verify" }.Contains(profile)
                    || string.IsNullOrWhiteSpace(ReadString(input, "campaignTestRunId", ""))
                    || string.IsNullOrWhiteSpace(ReadString(input, "expectedSaveName", ""))
                    || ((profile is "npc_caravan_probe" or "household_stipend_probe")
                        && (string.IsNullOrWhiteSpace(ReadString(input, "partyId", ""))
                            || string.IsNullOrWhiteSpace(ReadString(input, "settlementId", ""))
                            || !string.IsNullOrEmpty(ReadString(input, "source", ""))))
                    || (profile == "recruitment" && (!new[] { "ai_volunteer", "native_auto_garrison", "native_base_garrison", "native_naval_garrison" }.Contains(ReadString(input, "source", ""))
                        || string.IsNullOrWhiteSpace(ReadString(input, "partyId", "")) || string.IsNullOrWhiteSpace(ReadString(input, "settlementId", ""))))
                    || ((profile is "ship_funding_probe" or "civilian_crew_probe" or "production_probe" or "relief_probe" or "funding_probe" or "market_food_probe" or "household_probe" or "raid_food_probe" or "raid_partial_setup" or "raid_setup"
                        || (profile == "funding_trace" && ReadString(input, "source", "") == "trace_begin"))
                        && string.IsNullOrWhiteSpace(ReadString(input, "settlementId", "")))
                    || (profile == "funding_probe" && (string.IsNullOrWhiteSpace(ReadString(input, "partyId", ""))
                        || !new[] { "lord_purse", "kingdom_support", "market_balance", "loot_sale", "horse_sale", "prisoner_sale", "gold_barter", "market_purchase" }.Contains(ReadString(input, "source", ""))))
                    || (profile == "funding_trace" && !new[] { "trace_begin", "trace_observe", "trace_end" }.Contains(ReadString(input, "source", "")))
                    || (profile == "cohort_control" && !new[] { "cohort_preview", "cohort_enable", "cohort_disable" }.Contains(ReadString(input, "source", "")))
                    || ((profile is "ship_funding_probe" or "raid_partial_setup" or "raid_setup" or "readiness_probe" or "replacement_setup") && string.IsNullOrWhiteSpace(ReadString(input, "partyId", "")))
                    || ((profile is "ship_funding_probe" or "civilian_crew_probe" or "household_probe" or "market_food_probe" or "raid_setup" or "readiness_probe" or "replacement_setup") && !string.IsNullOrEmpty(ReadString(input, "source", "")))
                    || ReadString(input, "fixtureRunId", "").Length > 128
                    || ReadString(input, "operationId", "").Length > 128) return null;
            }
            if (operation == "clan_accords_test")
            {
                // This fixed diagnostic runs only isolated math/JSON contracts.
                // Do not turn its registration into a general accord mutation route.
                if (mode != "individual_chat" || ReadString(run, "mode", "") != "individual_chat"
                    || !string.Equals(ReadString(input, "profile", "contracts"), "contracts", StringComparison.OrdinalIgnoreCase)
                    || ReadString(input, "fixtureRunId", "").Length > 128) return null;
            }
            if (operation == "personal_gossip_test" || mode == "personal_gossip")
            {
                if (mode != "personal_gossip" || ReadString(run, "mode", "") != "personal_gossip" || operation != "personal_gossip_test"
                    || !PersonalGossipEnrollmentValid(ReadDictionary(run, "enrollment"), ReadString(run, "campaignId", ""))) return null;
            }
            if (operation == "npc_lives_test" || mode == "npc_lives")
            {
                if (mode != "npc_lives" || ReadString(run, "mode", "") != "npc_lives" || operation != "npc_lives_test"
                    || !NpcLivesEnrollmentValid(ReadDictionary(run, "enrollment"), ReadString(run, "campaignId", ""))) return null;
            }
            if (operation == "structured_action_test" || mode == "structured_actions")
            {
                if (mode != "structured_actions" || ReadString(run, "mode", "") != "structured_actions" || operation != "structured_action_test"
                    || !StructuredActionEnrollmentValid(ReadDictionary(run, "enrollment"), ReadString(run, "campaignId", ""))) return null;
            }
            string commandId = FirstNonEmpty(ReadString(input, "commandId", ""), ReadString(run, "runId", "run") + "-cmd-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            int maximumTimeoutSeconds = mode.Equals("passive_world", StringComparison.OrdinalIgnoreCase)
                && operation.Equals("world_advance", StringComparison.OrdinalIgnoreCase)
                    ? 21600
                    : mode.Equals("social_balance", StringComparison.OrdinalIgnoreCase)
                        && operation.Equals("social_reputation_profile", StringComparison.OrdinalIgnoreCase)
                            ? 3600
                            : 1800;
            Dictionary<string, object> command = new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase)
            {
                ["schemaVersion"] = LiveTestSchemaVersion,
                ["commandId"] = commandId,
                ["runId"] = ReadString(run, "runId", ""),
                ["campaignId"] = ReadString(run, "campaignId", ""),
                ["gameInstanceId"] = ReadString(run, "gameInstanceId", ""),
                ["mode"] = mode,
                ["operation"] = operation,
                ["presentation"] = NormalizeLiveTestPresentation(ReadString(input, "presentation", ReadString(run, "presentation", "headless"))),
                ["effects"] = NormalizeLiveTestEffects(ReadString(input, "effects", ReadString(run, "effects", "guarded"))),
                ["status"] = "queued",
                ["timeoutSeconds"] = Math.Max(10, Math.Min(maximumTimeoutSeconds, ReadInt(input, "timeoutSeconds", 300))),
                ["queuedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            // Execution evidence belongs to the server and cannot be copied from queued input.
            command.Remove("executionObservationVersion");
            command.Remove("executionRetryGeneration");
            command.Remove("issuedExecutionObservations");
            command.Remove("executionEvidence");
            command.Remove("executionObservation");
            command.Remove("executionStartIdentity");
            command.Remove("partyAgencyAcceptedSendExecution");
            if (IsPaidCourtFixtureCommand(command))
            {
                foreach (string key in command.Keys.Where(key => key.StartsWith("paidCourt", StringComparison.OrdinalIgnoreCase)).ToArray())
                    command.Remove(key);
                foreach (string key in new[] { "leaseId", "leaseStartedUtc", "leaseExpiresUtc", "attempt",
                    "completedUtc", "result", "error", "message", "pauseReason", "lastFailureUtc", "correlationIds" })
                    command.Remove(key);
            }
            if (mode.Equals("social_balance", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> enrollment = ReadDictionary(input, "enrollment");
                if (enrollment == null || enrollment.Count == 0)
                    enrollment = ReadDictionary(run, "enrollment");
                if (enrollment == null || enrollment.Count == 0) return null;
                command["enrollment"] = enrollment;
            }
            if (operation == "send" && string.IsNullOrWhiteSpace(ReadString(command, "text", ""))) return null;
            if (operation == "structured_action_test") command["enrollment"] = ReadDictionary(run, "enrollment");
            if (operation == "conversation_clock_probe")
            {
                command["enrollment"] = ReadDictionary(run, "enrollment");
                command["timeoutSeconds"] = 30;
            }
            if (operation == "npc_lives_test" || operation == "personal_gossip_test") command["enrollment"] = ReadDictionary(run, "enrollment");
            if (operation == "clan_accords_test")
            {
                command["profile"] = "contracts";
                command["timeoutSeconds"] = Math.Min(120, ReadInt(command, "timeoutSeconds", 120));
            }
            return command;
        }

        internal static List<Dictionary<string, object>> NormalizeLiveTestScenarioCommands(
            Dictionary<string, object> run, Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> commands = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> step in ReadDictionaryList(payload, "steps"))
            {
                Dictionary<string, object> command = NormalizeLiveTestCommand(run, step);
                if (command != null) commands.Add(command);
            }
            return commands;
        }

        private static void CompleteLiveTestRunIfAppropriate(Dictionary<string, object> run, Dictionary<string, object> completedCommand)
        {
            string operation = ReadString(completedCommand, "operation", "");
            List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
            bool pending = commands.Any(x => !LiveTestTerminalCommandStates.Contains(ReadString(x, "status", "")) && !ReadString(x, "status", "").Equals("needs_input", StringComparison.OrdinalIgnoreCase));
            bool failedSetupWithoutRecoveryStep = operation == "open"
                && ReadString(completedCommand, "status", "").Equals("failed", StringComparison.OrdinalIgnoreCase)
                && !pending;
            bool shouldComplete = failedSetupWithoutRecoveryStep
                || (operation == "close" && !pending)
                || (ReadBool(run, "autoCompleteWhenIdle", false) && !pending);
            if (!shouldComplete) return;
            run["status"] = ReadDictionaryList(run, "failures").Count == 0 ? "completed" : "failed";
            run["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
        }

        private static void InterruptLiveTestRun(Dictionary<string, object> run, string reason)
        {
            if (run == null || run.Count == 0) return;
            run["status"] = "interrupted";
            run["interruptedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            run["message"] = reason ?? "Run interrupted.";
            foreach (Dictionary<string, object> command in ReadDictionaryList(run, "commands").Where(x => !LiveTestTerminalCommandStates.Contains(ReadString(x, "status", ""))))
            {
                command["status"] = "interrupted";
                command["error"] = reason ?? "Run interrupted.";
            }
            SaveLiveTestRun(run);
        }

        private static Dictionary<string, object> LiveTestRunSummary(Dictionary<string, object> run, bool includeCommands)
        {
            List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = ReadString(run, "runId", ""),
                ["campaignId"] = ReadString(run, "campaignId", ""),
                ["gameInstanceId"] = ReadString(run, "gameInstanceId", ""),
                ["mode"] = ReadString(run, "mode", ""),
                ["status"] = ReadString(run, "status", ""),
                ["presentation"] = ReadString(run, "presentation", "headless"),
                ["effects"] = ReadString(run, "effects", "guarded"),
                ["enrollment"] = ReadDictionary(run, "enrollment")
                    ?? new Dictionary<string, object>(),
                ["startedUtc"] = ReadString(run, "startedUtc", ""),
                ["updatedUtc"] = ReadString(run, "updatedUtc", ""),
                ["completedUtc"] = ReadString(run, "completedUtc", ""),
                ["commandCount"] = commands.Count,
                ["completedCommands"] = commands.Count(x => ReadString(x, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase)),
                ["failedCommands"] = commands.Count(x => ReadString(x, "status", "").Equals("failed", StringComparison.OrdinalIgnoreCase)),
                ["reportPath"] = LiveTestRunPath(ReadString(run, "campaignId", ""), ReadString(run, "runId", ""))
            };
            if (includeCommands)
            {
                result["commands"] = commands;
                result["assertions"] = ReadDictionaryList(run, "assertions");
                result["failures"] = ReadDictionaryList(run, "failures");
                if (IsLiveTestExecutionEvidenceRun(run))
                {
                    result["executionObservationVersion"] = LiveTestExecutionObservationVersion;
                    result["executionStartIdentity"] = ReadDictionary(run, "executionStartIdentity");
                }
            }
            return result;
        }

        internal static List<Dictionary<string, object>> CollectLiveTestAuditEvidence(Dictionary<string, object> run)
        {
            string campaignId = ReadString(run, "campaignId", "");
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string correlation in ReadDictionaryList(run, "commands").SelectMany(x => ReadStringList(x, "correlationIds")).Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                Dictionary<string, object> audit = OperationsService.AuditQuery(new Dictionary<string, string> { ["campaignId"] = campaignId, ["correlationId"] = correlation, ["limit"] = "500", ["oldestFirst"] = "true" });
                foreach (Dictionary<string, object> row in ReadDictionaryList(audit, "entries"))
                {
                    if (seen.Add(ReadString(row, "auditId", Guid.NewGuid().ToString("N")))) evidence.Add(row);
                }
            }
            return evidence;
        }
    }
}
