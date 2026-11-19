using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class VerificationService
    {
        internal static Dictionary<string, object> TestCasesApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string suiteFilter = query.TryGetValue("suite", out string suite) ? suite : "";
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            string casesRoot = Path.Combine(Program.TestsDir, "cases");
            Directory.CreateDirectory(casesRoot);
            foreach (string path in Directory.GetFiles(casesRoot, "*.json", SearchOption.AllDirectories).OrderBy(x => x))
            {
                Dictionary<string, object> testCase = CampaignFileService.ReadJsonObject(path);
                string caseSuite = ReadString(testCase, "suite", new DirectoryInfo(Path.GetDirectoryName(path)).Name);
                if (!string.IsNullOrWhiteSpace(suiteFilter) && !string.Equals(caseSuite, suiteFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                cases.Add(TestCaseSummary(testCase, path));
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["cases"] = cases,
                ["count"] = cases.Count,
                ["testsDir"] = Program.TestsDir
            };
        }

        internal static Dictionary<string, object> SaveTestCaseApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> testCase = ReadDictionary(payload, "case") ?? payload;
            NormalizeTestCaseIdentity(testCase);
            string suite = ReadString(testCase, "suite", "general");
            string caseId = ReadString(testCase, "caseId", Guid.NewGuid().ToString("N"));
            string path = TestCasePath(suite, caseId);
            CampaignFileService.WriteJsonObject(path, testCase);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["path"] = path,
                ["case"] = TestCaseSummary(testCase, path)
            };
        }

        internal static Dictionary<string, object> RunTestApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> testCase = ReadDictionary(payload, "case");
            if (testCase == null || testCase.Count == 0)
            {
                testCase = LoadTestCase(ReadString(payload, "suite", ""), ReadFirstString(payload, "caseId", "id"));
            }

            if (testCase == null || testCase.Count == 0)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Test case was not found." };
            }

            string requestedMode = TestRunMode(payload, testCase);
            Dictionary<string, object> result = RunPipelineTestCase(testCase, requestedMode);
            SaveTestRun(result);
            return result;
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static Dictionary<string, object> RunTestSuiteApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string suite = ReadString(payload, "suite", "");
            bool failFast = ReadBool(payload, "failFast", false);
            string requestedMode = ReadBool(payload, "live", false) ? "live" : ReadBool(payload, "canned", false) ? "canned" : ReadString(payload, "llmMode", "canned");
            List<Dictionary<string, object>> cases = LoadTestCases(suite);
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Stopwatch timer = Stopwatch.StartNew();
            foreach (Dictionary<string, object> testCase in cases)
            {
                Dictionary<string, object> result = RunPipelineTestCase(testCase, requestedMode);
                SaveTestRun(result);
                results.Add(result);
                if (failFast && !ReadBool(result, "passed", false))
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("relationship_system", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(RelationshipsService.RunCurrentRelationshipSelfTests());
                results.AddRange(RelationshipsService.RunNotableMbtiSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("motive_conversation", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DialogueService.RunMotiveAwareConversationSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("rumor_system", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(ReputationService.RunRumorSubsystemSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("character_editor", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(CharactersService.RunCharacterEditorSubsystemSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("categorized_memory", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DialogueService.RunCategorizedMemorySubsystemSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("npc_dialogue_audit", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DialogueService.RunNpcDialogueAuditSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("party_dialogue_audit", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DialogueService.RunPartyDialogueAuditSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("semantic_memory", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DialogueService.RunSemanticMemorySelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("character_profiles", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(CharactersService.RunCharacterProfileLibrarySelfTests());
                results.AddRange(CharactersService.RunTraitPercentageSelfTests());
                results.AddRange(CourtService.RunCourtCharacterSelfTests());
                results.AddRange(CharactersService.RunFoundationTraitRangeVNextSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("identity_system", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(CharactersService.RunIdentitySubsystemSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("campaign_backups", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(PersistenceService.RunCampaignBackupSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("save_sync", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(PersistenceService.RunSaveSyncSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("prompt_caching", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DialogueService.RunPromptCachingSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("server_activation", StringComparison.OrdinalIgnoreCase))
            {
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("interaction_architecture", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(RunInteractionArchitectureSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("spymaster", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(SpymasterService.RunSpymasterSystemSelfTests());
            }
            if (string.IsNullOrWhiteSpace(suite) || suite.Equals("campaign_command", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(WorldSimulationService.RunCampaignCommandSystemSelfTests());
            }

            timer.Stop();
            int passed = results.Count(x => ReadBool(x, "passed", false));
            Dictionary<string, object> suiteResult = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = "suite-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ["suite"] = suite,
                ["llmMode"] = requestedMode,
                ["passed"] = passed == results.Count,
                ["passedCount"] = passed,
                ["failedCount"] = results.Count - passed,
                ["totalCount"] = results.Count,
                ["durationMs"] = timer.ElapsedMilliseconds,
                ["results"] = results
            };
            SaveTestRun(suiteResult);
            return suiteResult;
        }
#endif

        internal static Dictionary<string, object> CaptureTestFromCorrelationApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", CampaignFileService.ResolveLogCampaignId(""));
            string correlationId = ReadString(payload, "correlationId", "");
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "correlationId is required." };
            }

            Dictionary<string, object> snapshot = LoadTestSnapshot(campaignId, correlationId);
            if (snapshot.Count == 0)
            {
                snapshot = BuildSnapshotFromAudit(campaignId, correlationId);
            }

            if (snapshot.Count == 0)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No snapshot or audit entries were found for that correlation id." };
            }

            string suite = ReadString(payload, "suite", ReadString(snapshot, "mode", "captured"));
            string caseId = ReadString(payload, "caseId", "captured_" + CampaignFileService.SafePathSegment(correlationId, "case"));
            string capturedCannedContent = ExtractCannedContentFromSnapshot(snapshot);
            Dictionary<string, object> testCase = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["suite"] = suite,
                ["caseId"] = caseId,
                ["name"] = ReadString(payload, "name", "Captured " + correlationId),
                ["description"] = "Captured from audit/snapshot correlation " + correlationId + ".",
                ["mode"] = ReadString(snapshot, "mode", "dialogue"),
                ["input"] = SnapshotInputFromSnapshot(snapshot),
                ["snapshot"] = new Dictionary<string, object>
                {
                    ["path"] = SnapshotPath(campaignId, correlationId),
                    ["campaignId"] = campaignId,
                    ["correlationId"] = correlationId
                },
                ["llm"] = new Dictionary<string, object>
                {
                    ["mode"] = "canned",
                    ["cannedContent"] = capturedCannedContent
                },
                ["replayable"] = !string.IsNullOrWhiteSpace(capturedCannedContent),
                ["expected"] = new Dictionary<string, object>
                {
                    ["validationSuccess"] = true
                },
                ["assertions"] = new Dictionary<string, object>
                {
                    ["strict"] = false
                }
            };

            bool save = ReadBool(payload, "save", false);
            string path = "";
            if (save)
            {
                path = TestCasePath(suite, caseId);
                CampaignFileService.WriteJsonObject(path, testCase);
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["snapshot"] = snapshot,
                ["case"] = testCase,
                ["saved"] = save,
                ["path"] = path
            };
        }

        internal static Dictionary<string, object> TestRunsApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsed) ? parsed : 50;
            limit = Math.Max(1, Math.Min(500, limit));
            string runsRoot = Path.Combine(Program.TestsDir, "runs");
            Directory.CreateDirectory(runsRoot);
            List<Dictionary<string, object>> runs = Directory.GetFiles(runsRoot, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(limit)
                .Select(CampaignFileService.ReadJsonObject)
                .ToList();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runs"] = runs,
                ["count"] = runs.Count
            };
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static Dictionary<string, object> RunTestsCli(string[] args)
        {
            string suite = ArgValue(args, "--suite", "");
            string caseId = ArgValue(args, "--case", "");
            bool failFast = Program.HasArg(args, "--fail-fast");
            string requestedMode = Program.HasArg(args, "--live") ? "live" : Program.HasArg(args, "--canned") ? "canned" : "canned";
            if (!string.IsNullOrWhiteSpace(caseId))
            {
                Dictionary<string, object> testCase = LoadTestCase(suite, caseId);
                if (testCase == null || testCase.Count == 0)
                {
                    return new Dictionary<string, object> { ["ok"] = false, ["passed"] = false, ["error"] = "Test case was not found." };
                }

                Dictionary<string, object> result = RunPipelineTestCase(testCase, requestedMode);
                SaveTestRun(result);
                return result;
            }

            return RunTestSuiteApi(new Dictionary<string, object>
            {
                ["suite"] = suite,
                ["llmMode"] = requestedMode,
                ["failFast"] = failFast
            });
        }
#endif

        internal static string FormatTestCliResult(Dictionary<string, object> result)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Bannerlord Reign Test Lab");
            builder.AppendLine("Passed: " + ReadBool(result, "passed", false));
            builder.AppendLine("Run id: " + ReadString(result, "runId", ""));
            if (!string.IsNullOrWhiteSpace(ReadString(result, "suite", "")))
            {
                builder.AppendLine("Suite: " + ReadString(result, "suite", ""));
            }

            builder.AppendLine("Summary: " + ReadString(result, "summary", ""));
            if (result.TryGetValue("phases", out object phasesValue))
            {
                foreach (Dictionary<string, object> phase in ActionPipelineService.ObjectsFromValue(phasesValue).OfType<Dictionary<string, object>>())
                {
                    builder.AppendLine("- " + ReadString(phase, "phase", "") + ": " + ReadString(phase, "status", "") + " - " + ReadString(phase, "summary", ""));
                }
            }
            else if (result.TryGetValue("results", out object resultsValue))
            {
                foreach (Dictionary<string, object> item in ActionPipelineService.ObjectsFromValue(resultsValue).OfType<Dictionary<string, object>>())
                {
                    builder.AppendLine("- " + ReadString(item, "suite", "") + "/" + ReadString(item, "caseId", "") + ": " + (ReadBool(item, "passed", false) ? "PASS" : "FAIL") + " - " + ReadString(item, "summary", ""));
                }
            }

            return builder.ToString();
        }

        private static Dictionary<string, object> RunPipelineTestCase(Dictionary<string, object> originalCase, string requestedMode)
        {
            Stopwatch total = Stopwatch.StartNew();
            Dictionary<string, object> testCase = CloneDictionary(originalCase);
            NormalizeTestCaseIdentity(testCase);
            string runId = "test-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string suite = ReadString(testCase, "suite", "general");
            string caseId = ReadString(testCase, "caseId", "case");
            string mode = ReadString(testCase, "mode", "action_only");
            string llmMode = string.IsNullOrWhiteSpace(requestedMode) ? TestCaseDefaultLlmMode(testCase) : requestedMode;
            List<Dictionary<string, object>> phases = new List<Dictionary<string, object>>();
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = runId,
                ["suite"] = suite,
                ["caseId"] = caseId,
                ["name"] = ReadString(testCase, "name", caseId),
                ["mode"] = mode,
                ["llmMode"] = llmMode,
                ["dryRun"] = true,
                ["startedUtc"] = DateTime.UtcNow.ToString("o"),
                ["phases"] = phases
            };

            Dictionary<string, object> snapshot = ResolveTestSnapshot(testCase);
            Dictionary<string, object> input = ReadDictionary(testCase, "input") ?? new Dictionary<string, object>();
            Dictionary<string, object> payload = BuildTestPayload(testCase, snapshot, input);
            string campaignId = ReadString(payload, "campaignId", ReadString(snapshot, "campaignId", "test_campaign"));
            payload["campaignId"] = campaignId;
            payload["correlationId"] = runId;
            AddTestPhase(phases, "snapshot", snapshot.Count > 0 ? "passed" : "warning", snapshot.Count > 0 ? "Snapshot loaded." : "No snapshot was supplied; using input-only payload.", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["payloadKeys"] = payload.Keys.OrderBy(x => x).ToList()
            });

            List<Dictionary<string, object>> selectedPulls = DialogueService.NormalizeSelectedContextPulls(ReadDictionaryList(payload, "selectedContextPulls"), Program.ContextPullIds.ToList());
            if (selectedPulls.Count == 0)
            {
                selectedPulls = DialogueService.DeterministicContextPullSelection(mode, ReadFirstString(input, "playerText", "text", "message"), ReadString(payload, "sceneContext", ""), Program.ContextPullIds.ToList());
            }

            List<Dictionary<string, object>> contextBundles = ReadDictionaryList(payload, "contextBundles");
            AddTestPhase(phases, "context", "passed", "Context pulls prepared.", new Dictionary<string, object>
            {
                ["selectedPulls"] = selectedPulls,
                ["contextBundleCount"] = contextBundles.Count
            });

            string prompt = "";
            List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
            Dictionary<string, object> promptEnvelopeDiagnostics = new Dictionary<string, object>();
            if (!mode.Equals("action_only", StringComparison.OrdinalIgnoreCase) && !mode.Equals("resolver_only", StringComparison.OrdinalIgnoreCase) && !mode.Equals("action_router", StringComparison.OrdinalIgnoreCase) && !mode.Equals("action_gate", StringComparison.OrdinalIgnoreCase))
            {
                messages = BuildTestMessages(testCase, payload, snapshot, selectedPulls, contextBundles, mode, out prompt, out promptEnvelopeDiagnostics);
                DialogueService.CapturePromptEvidence(campaignId, runId, mode, "test_lab_assembly", new Dictionary<string, object> { ["messages"] = messages },
                    promptEnvelopeDiagnostics, "", "test_lab_assembly");
                AddTestPhase(phases, "prompt", messages.Count > 0 ? "passed" : "warning", messages.Count > 0 ? "Prompt assembled." : "No prompt was assembled for this mode.", new Dictionary<string, object>
                {
                    ["promptChars"] = prompt.Length,
                    ["promptEnvelope"] = promptEnvelopeDiagnostics,
                    ["messages"] = messages
                });
            }

            string modelContent = "";
            Dictionary<string, object> llmResult = new Dictionary<string, object>();
            if (mode.Equals("action_only", StringComparison.OrdinalIgnoreCase) || mode.Equals("resolver_only", StringComparison.OrdinalIgnoreCase) || mode.Equals("action_router", StringComparison.OrdinalIgnoreCase) || mode.Equals("action_gate", StringComparison.OrdinalIgnoreCase))
            {
                modelContent = CannedContentFromCase(testCase);
                AddTestPhase(phases, "llm", "skipped", "LLM skipped for action/resolver/router/gate-only test.", new Dictionary<string, object>());
            }
            else if (llmMode.Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                llmResult = LlmProviderService.ChatWithLlm(new Dictionary<string, object>
                {
                    ["requestType"] = mode.Equals("social_event", StringComparison.OrdinalIgnoreCase) || mode.Equals("party_chat", StringComparison.OrdinalIgnoreCase) ? "social_event" : "dialogue",
                    ["campaignId"] = campaignId,
                    ["correlationId"] = runId,
                    ["heroStringId"] = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                    ["eventId"] = ReadFirstString(payload, "eventId", "socialEventId"),
                    ["messages"] = messages,
                    ["promptEnvelope"] = promptEnvelopeDiagnostics,
                    ["promptCacheEligible"] = true,
                    ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
                });
                modelContent = ReadString(llmResult, "content", "");
                AddTestPhase(phases, ReadBool(llmResult, "ok", false) ? "llm" : "llm", ReadBool(llmResult, "ok", false) ? "passed" : "failed", ReadBool(llmResult, "ok", false) ? "Live LLM response received." : "Live LLM response failed.", llmResult);
            }
            else
            {
                modelContent = CannedContentFromCase(testCase);
                AddTestPhase(phases, "llm", string.IsNullOrWhiteSpace(modelContent) ? "warning" : "passed", string.IsNullOrWhiteSpace(modelContent) ? "No canned LLM output was supplied." : "Canned LLM output loaded.", new Dictionary<string, object>
                {
                    ["content"] = modelContent
                });
            }

            Dictionary<string, object> parsed = TryParseJsonObject(modelContent);
            List<Dictionary<string, object>> actions = ActionsFromTestCase(testCase, parsed, payload);
            AddTestPhase(phases, "parse", parsed != null || actions.Count > 0 ? "passed" : "failed", parsed != null ? "Model JSON parsed." : actions.Count > 0 ? "Actions supplied directly by test input." : "No parseable model JSON or direct action was found.", new Dictionary<string, object>
            {
                ["rawContent"] = modelContent,
                ["parsed"] = parsed ?? new Dictionary<string, object>(),
                ["actionCount"] = actions.Count,
                ["actions"] = actions
            });

            List<Dictionary<string, object>> normalized = new List<Dictionary<string, object>>();
            List<string> allErrors = new List<string>();
            foreach (Dictionary<string, object> action in actions)
            {
                Dictionary<string, object> actionCopy = CloneDictionary(action);
                actionCopy["source"] = ReadString(actionCopy, "source", "test_lab");
                Dictionary<string, object> record = ActionPipelineService.NormalizeActionCommand(actionCopy, campaignId, out List<string> errors, payload, ReadFirstString(input, "playerText", "text", "message") + "\n" + ReadString(input, "npcReplyText", ""));
                if (record != null)
                {
                    normalized.Add(record);
                }

                allErrors.AddRange(errors);
            }

            AddTestPhase(phases, "normalize_validate", allErrors.Count == 0 && normalized.Count > 0 ? "passed" : "failed", allErrors.Count == 0 && normalized.Count > 0 ? "Action normalized and validated." : "Action normalization or validation failed.", new Dictionary<string, object>
            {
                ["normalizedActions"] = normalized,
                ["errors"] = allErrors
            });

            Dictionary<string, object> assertion = EvaluateTestAssertions(testCase, normalized, allErrors);
            AddTestPhase(phases, "assertions", ReadBool(assertion, "passed", false) ? "passed" : "failed", ReadString(assertion, "summary", ""), assertion);

            total.Stop();
            bool passed = phases.All(x =>
            {
                string status = ReadString(x, "status", "");
                return status.Equals("passed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("warning", StringComparison.OrdinalIgnoreCase);
            }) && ReadBool(assertion, "passed", false);

            result["passed"] = passed;
            result["durationMs"] = total.ElapsedMilliseconds;
            result["summary"] = passed ? "Pipeline test passed." : "Pipeline test failed at " + FirstFailingPhase(phases) + ".";
            result["normalizedActions"] = normalized;
            result["errors"] = allErrors;
            result["assertion"] = assertion;
            result["completedUtc"] = DateTime.UtcNow.ToString("o");
            return result;
        }

        private static void AddTestPhase(List<Dictionary<string, object>> phases, string phase, string status, string summary, Dictionary<string, object> data)
        {
            phases.Add(new Dictionary<string, object>
            {
                ["phase"] = phase,
                ["status"] = status,
                ["summary"] = summary,
                ["data"] = SanitizeTestValue(data ?? new Dictionary<string, object>(), 0)
            });
        }

        internal static Dictionary<string, object> BuildTestPayload(Dictionary<string, object> testCase, Dictionary<string, object> snapshot, Dictionary<string, object> input)
        {
            Dictionary<string, object> snapshotPayload = ReadDictionary(snapshot, "payload") ?? ReadDictionary(snapshot, "request") ?? snapshot;
            Dictionary<string, object> payload = CloneDictionary(snapshotPayload);
            foreach (KeyValuePair<string, object> pair in input ?? new Dictionary<string, object>())
            {
                if (pair.Key.Equals("action", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("actions", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                payload[pair.Key] = pair.Value;
            }

            CopyTestIfMissing(payload, "campaignId", ReadString(testCase, "campaignId", ReadString(snapshot, "campaignId", "test_campaign")));
            CopyTestIfMissing(payload, "mode", ReadString(testCase, "mode", "action_only"));
            if (!payload.ContainsKey("actionResolutionIndex") && snapshot.ContainsKey("actionResolutionIndex"))
            {
                payload["actionResolutionIndex"] = snapshot["actionResolutionIndex"];
            }

            BackfillPayloadIdentityFromResolver(payload);
            return payload;
        }

        private static void BackfillPayloadIdentityFromResolver(Dictionary<string, object> payload)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            if (index.Count == 0)
            {
                return;
            }

            CopyTestIfMissing(payload, "playerHeroStringId", ActionPipelineService.ResolveAlias(index, "hero", "player"));
            CopyTestIfMissing(payload, "speakerHeroStringId", ActionPipelineService.ResolveAlias(index, "hero", "speaker"));
            CopyTestIfMissing(payload, "playerKingdomId", ActionPipelineService.ResolveAlias(index, "kingdom", "player_kingdom"));
            CopyTestIfMissing(payload, "speakerKingdomId", ActionPipelineService.ResolveAlias(index, "kingdom", "speaker_kingdom"));
            string playerHero = ReadString(payload, "playerHeroStringId", "");
            string speakerHero = ReadString(payload, "speakerHeroStringId", "");
            foreach (Dictionary<string, object> hero in ReadDictionaryList(index, "heroes"))
            {
                string id = ReadString(hero, "heroStringId", "");
                if (string.Equals(id, playerHero, StringComparison.OrdinalIgnoreCase))
                {
                    CopyTestIfMissing(payload, "playerClanId", ReadString(hero, "clanId", ""));
                    CopyTestIfMissing(payload, "playerKingdomId", ReadString(hero, "kingdomId", ""));
                }
                else if (string.Equals(id, speakerHero, StringComparison.OrdinalIgnoreCase))
                {
                    CopyTestIfMissing(payload, "speakerClanId", ReadString(hero, "clanId", ""));
                    CopyTestIfMissing(payload, "speakerKingdomId", ReadString(hero, "kingdomId", ""));
                    if (!payload.ContainsKey("hero"))
                    {
                        payload["hero"] = hero;
                    }
                }
            }
        }

        internal static List<Dictionary<string, object>> BuildTestMessages(Dictionary<string, object> testCase, Dictionary<string, object> payload, Dictionary<string, object> snapshot, List<Dictionary<string, object>> selectedPulls, List<Dictionary<string, object>> contextBundles, string mode, out string prompt, out Dictionary<string, object> promptEnvelopeDiagnostics)
        {
            string campaignId = ReadString(payload, "campaignId", "test_campaign");
            string heroId = FirstNonEmpty(ReadFirstString(payload, "heroStringId", "heroId", "characterId"), ReadFirstString(payload, "speakerHeroStringId"));
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker") ?? ReadDictionary(snapshot, "hero") ?? new Dictionary<string, object>();
            string heroName = ReadString(hero, "name", FirstNonEmpty(heroId, "NPC"));
            string playerName = ReadString(payload, "playerName", "Player");
            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            string sceneContext = ReadString(payload, "sceneContext", "");
            Dictionary<string, object> profile = ReadDictionary(snapshot, "profile") ?? CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "profile.json"));
            Dictionary<string, object> characteristics = ReadDictionary(snapshot, "characteristics") ?? CharactersService.LoadCharacterStack(campaignId, heroId);
            Dictionary<string, object> state = ReadDictionary(snapshot, "state") ?? CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "state.json"));
            Dictionary<string, object> relationships = ReadDictionary(snapshot, "relationships") ?? CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "relationships.json"));
            Dictionary<string, object> summary = ReadDictionary(snapshot, "memorySummary") ?? CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "summary.json"));
            List<Dictionary<string, object>> memories = ReadDictionaryList(snapshot, "memories");
            List<Dictionary<string, object>> priorLines = ReadDictionaryList(snapshot, "priorLines");
            List<Dictionary<string, object>> compactActionCatalog = ActionPipelineService.CompactActionCatalog(ActionPipelineService.ActionCatalog());
            Dictionary<string, object> identityView = ReadDictionary(payload, "identityView") ?? new Dictionary<string, object>
            {
                ["identityState"] = "verified",
                ["usableName"] = playerName,
                ["canonicalNameAllowed"] = true,
                ["instruction"] = "The current NPC may use the supplied player display name."
            };

            if (mode.Equals("social_event", StringComparison.OrdinalIgnoreCase) || mode.Equals("party_chat", StringComparison.OrdinalIgnoreCase))
            {
                string eventId = ReadFirstString(payload, "eventId", "socialEventId");
                DialogueService.PromptEnvelope eventEnvelope = DialogueService.BuildEventPromptEnvelope(campaignId, eventId, heroId, heroName, playerName, playerName, playerText, sceneContext, payload, profile, characteristics, state, relationships, summary, priorLines, selectedPulls, contextBundles, compactActionCatalog, identityView);
                prompt = string.Join("\n\n", eventEnvelope.Messages.Select(x => ReadString(x, "content", "")));
                promptEnvelopeDiagnostics = eventEnvelope.Diagnostics;
                return eventEnvelope.Messages;
            }

            DialogueService.PromptEnvelope dialogueEnvelope = DialogueService.BuildDialoguePromptEnvelope(campaignId, heroId, heroName, playerName, playerName, playerText, sceneContext, profile, characteristics, state, relationships, summary, priorLines, selectedPulls, contextBundles, compactActionCatalog, identityView, payload);
            prompt = string.Join("\n\n", dialogueEnvelope.Messages.Select(x => ReadString(x, "content", "")));
            promptEnvelopeDiagnostics = dialogueEnvelope.Diagnostics;
            return dialogueEnvelope.Messages;
        }

        private static List<Dictionary<string, object>> ActionsFromTestCase(Dictionary<string, object> testCase, Dictionary<string, object> parsed, Dictionary<string, object> payload)
        {
            Dictionary<string, object> input = ReadDictionary(testCase, "input") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> actions = ReadDictionaryList(input, "actions");
            Dictionary<string, object> single = ReadDictionary(input, "action");
            if (single != null)
            {
                actions.Add(single);
            }

            if (actions.Count > 0)
            {
                return actions;
            }

            string mode = ReadString(testCase, "mode", "");
            if (mode.Equals("action_router", StringComparison.OrdinalIgnoreCase))
            {
                string playerText = ReadFirstString(input, "playerText", "text", "message");
                string npcReply = ReadFirstString(input, "npcReplyText", "visibleReply", "reply", "response");
                Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>
                {
                    ["heroStringId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId")
                };
                Dictionary<string, object> route = ActionPipelineService.RouteActionTurn(ReadString(payload, "campaignId", "test_campaign"), payload, hero, playerText, npcReply, new List<Dictionary<string, object>>(), "test_lab_router", "Test Lab router", false);
                actions.AddRange(ReadDictionaryList(route, "candidates"));
                return actions;
            }

            if (mode.Equals("action_gate", StringComparison.OrdinalIgnoreCase))
            {
                string playerText = ReadFirstString(input, "playerText", "text", "message");
                string npcReply = ReadFirstString(input, "npcReplyText", "visibleReply", "reply", "response");
                Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>
                {
                    ["heroStringId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId")
                };
                Dictionary<string, object> actionGate = ActionPipelineService.NormalizeActionGate(ReadDictionary(input, "actionGate"), playerText, npcReply);
                if (parsed != null && ActionPipelineService.ActionGateShouldPlan(actionGate))
                {
                    Dictionary<string, object> settings = SettingsService.LoadSettings();
                    List<Dictionary<string, object>> allowedActions = ActionPipelineService.BuildAllowedActionPlannerChoices(settings, payload, hero, actionGate, playerText, npcReply, Math.Max(3, ReadInt(settings, "actionRouterCandidateCount", 10)));
                    HashSet<string> allowed = new HashSet<string>(allowedActions.Select(x => ActionPipelineService.CanonicalCommand(ReadString(x, "command", ""))), StringComparer.OrdinalIgnoreCase);
                    string resolutionText = ActionPipelineService.BuildRouterResolutionText(playerText + "\n" + ReadString(actionGate, "intent", ""), npcReply);
                    actions.AddRange(ActionPipelineService.NormalizeRouterActions(parsed, allowed, payload, hero, resolutionText, "test_lab_action_gate", "Test Lab action gate"));
                }

                return actions;
            }

            List<Dictionary<string, object>> compactActionCatalog = ActionPipelineService.CompactActionCatalog(ActionPipelineService.ActionCatalog());
            if (parsed != null)
            {
                actions = ActionPipelineService.NormalizeSuggestedActions(parsed.ContainsKey("suggestedActions") ? parsed["suggestedActions"] : parsed.ContainsKey("actions") ? parsed["actions"] : null, compactActionCatalog);
                if (actions.Count > 0)
                {
                    return actions;
                }
            }

            Dictionary<string, object> expectedAction = ReadDictionary(ReadDictionary(testCase, "expected"), "action");
            if (expectedAction != null)
            {
                actions.Add(expectedAction);
            }

            if (actions.Count == 0)
            {
                string playerText = ReadFirstString(input, "playerText", "text", "message");
                if (!string.IsNullOrWhiteSpace(playerText))
                {
                    string directive = ActionPipelineService.TryExtractTestDirective(playerText, true, out string extracted) ? extracted : playerText;
                    Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>
                    {
                        ["heroStringId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId")
                    };
                    List<string> deterministicErrors = new List<string>();
                    actions.AddRange(ActionPipelineService.BuildDeterministicActionCandidates(payload, hero, directive, "test_lab_extractor", "Test Lab action extraction", deterministicErrors));
                }
            }

            return actions;
        }

        private static Dictionary<string, object> EvaluateTestAssertions(Dictionary<string, object> testCase, List<Dictionary<string, object>> normalized, List<string> errors)
        {
            Dictionary<string, object> expected = ReadDictionary(testCase, "expected") ?? new Dictionary<string, object>();
            List<string> failures = new List<string>();
            bool expectsValidation = expected.ContainsKey("validationSuccess");
            bool expectedValidation = ReadBool(expected, "validationSuccess", true);
            bool actualValidation = errors.Count == 0 && normalized.Count > 0;
            if (expectsValidation && expectedValidation != actualValidation)
            {
                failures.Add("validationSuccess expected " + expectedValidation + " but was " + actualValidation + ".");
            }

            Dictionary<string, object> first = normalized.FirstOrDefault() ?? new Dictionary<string, object>();
            string expectedCommand = FirstNonEmpty(ReadString(expected, "command", ""), ReadString(expected, "action", ""));
            List<string> allowed = ReadStringList(expected, "allowedCommands");
            if (allowed.Count == 0 && !string.IsNullOrWhiteSpace(expectedCommand))
            {
                allowed.Add(expectedCommand);
            }

            if (allowed.Count > 0 && !allowed.Any(x => string.Equals(ActionPipelineService.CanonicalCommand(x), ActionPipelineService.CanonicalCommand(ReadString(first, "command", "")), StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("command expected one of [" + string.Join(", ", allowed) + "] but was " + ReadString(first, "command", "<none>") + ".");
            }

            Dictionary<string, object> expectedResolution = ReadDictionary(testCase, "expectedResolution") ?? ReadDictionary(expected, "resolution") ?? new Dictionary<string, object>();
            foreach (string field in new[] { "actorHeroStringId", "targetHeroStringId", "actorKingdomStringId", "targetKingdomStringId", "actorClanStringId", "targetClanStringId", "targetSettlementStringId" })
            {
                string expectedValue = FirstNonEmpty(ReadString(expected, field, ""), ReadString(expectedResolution, field, ""));
                if (!string.IsNullOrWhiteSpace(expectedValue) && !string.Equals(ReadString(first, field, ""), expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(field + " expected " + expectedValue + " but was " + ReadString(first, field, "<empty>") + ".");
                }
            }

            foreach (string expectedError in ReadStringList(expected, "expectedErrors"))
            {
                if (!errors.Any(x => x.IndexOf(expectedError, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    failures.Add("expected error not found: " + expectedError);
                }
            }

            string termsJson = ReadString(first, "termsJson", string.Empty);
            foreach (string expectedFragment in ReadStringList(expected, "termsJsonContains"))
            {
                if (termsJson.IndexOf(expectedFragment, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    failures.Add("termsJson did not contain expected fragment: " + expectedFragment + ".");
                }
            }

            return new Dictionary<string, object>
            {
                ["passed"] = failures.Count == 0,
                ["summary"] = failures.Count == 0 ? "Assertions passed." : string.Join(" ", failures),
                ["failures"] = failures,
                ["normalizedAction"] = first,
                ["errors"] = errors
            };
        }

        private static string FirstFailingPhase(List<Dictionary<string, object>> phases)
        {
            Dictionary<string, object> failed = phases.FirstOrDefault(x => string.Equals(ReadString(x, "status", ""), "failed", StringComparison.OrdinalIgnoreCase));
            return failed == null ? "assertions" : ReadString(failed, "phase", "unknown");
        }

        internal static Dictionary<string, object> ResolveTestSnapshot(Dictionary<string, object> testCase)
        {
            Dictionary<string, object> snapshot = ReadDictionary(testCase, "snapshot") ?? new Dictionary<string, object>();
            string path = ReadString(snapshot, "path", ReadString(snapshot, "snapshotPath", ""));
            if (!string.IsNullOrWhiteSpace(path))
            {
                string resolved = Path.IsPathRooted(path) ? path : Path.Combine(Program.TestsDir, path.TrimStart('\\', '/'));
                Dictionary<string, object> loaded = CampaignFileService.ReadJsonObject(resolved);
                if (loaded.Count > 0)
                {
                    return loaded;
                }
            }

            return snapshot;
        }

        private static Dictionary<string, object> LoadTestSnapshot(string campaignId, string correlationId)
        {
            return CampaignFileService.ReadJsonObject(SnapshotPath(campaignId, correlationId));
        }

        private static Dictionary<string, object> BuildSnapshotFromAudit(string campaignId, string correlationId)
        {
            List<Dictionary<string, object>> entries = Program.ReadJsonLinesFromPath(CampaignFileService.CampaignFile(campaignId, "audit", "audit.jsonl"))
                .Where(x => string.Equals(ReadString(x, "correlationId", ""), correlationId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0)
            {
                return new Dictionary<string, object>();
            }

            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["mode"] = ReadString(entries.FirstOrDefault(), "mode", "dialogue"),
                ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
                ["source"] = "audit_fallback",
                ["auditEntries"] = entries
            };
        }

        private static Dictionary<string, object> SnapshotInputFromSnapshot(Dictionary<string, object> snapshot)
        {
            Dictionary<string, object> payload = ReadDictionary(snapshot, "payload") ?? snapshot;
            return new Dictionary<string, object>
            {
                ["playerText"] = ReadFirstString(payload, "playerText", "text", "message"),
                ["heroStringId"] = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                ["sceneContext"] = ReadString(payload, "sceneContext", "")
            };
        }

        private static string ExtractCannedContentFromSnapshot(Dictionary<string, object> snapshot)
        {
            foreach (Dictionary<string, object> entry in ReadDictionaryList(snapshot, "auditEntries"))
            {
                if (!string.Equals(ReadString(entry, "phase", ""), "llm.response", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Dictionary<string, object> data = ReadDictionary(entry, "data") ?? new Dictionary<string, object>();
                string content = ReadString(data, "content", "");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }

            return "";
        }

        private static string CannedContentFromCase(Dictionary<string, object> testCase)
        {
            Dictionary<string, object> llm = ReadDictionary(testCase, "llm") ?? new Dictionary<string, object>();
            return FirstNonEmpty(ReadString(llm, "cannedContent", ""), ReadString(llm, "content", ""), ReadString(llm, "raw", ""));
        }

        private static string TestRunMode(Dictionary<string, object> payload, Dictionary<string, object> testCase)
        {
            if (ReadBool(payload, "live", false))
            {
                return "live";
            }

            if (ReadBool(payload, "canned", false))
            {
                return "canned";
            }

            return ReadString(payload, "llmMode", TestCaseDefaultLlmMode(testCase));
        }

        private static string TestCaseDefaultLlmMode(Dictionary<string, object> testCase)
        {
            Dictionary<string, object> llm = ReadDictionary(testCase, "llm") ?? new Dictionary<string, object>();
            string mode = ReadString(llm, "mode", ReadString(llm, "type", "canned"));
            return mode.Equals("both", StringComparison.OrdinalIgnoreCase) ? "canned" : mode;
        }

        internal static List<Dictionary<string, object>> LoadTestCases(string suite)
        {
            string casesRoot = Path.Combine(Program.TestsDir, "cases");
            Directory.CreateDirectory(casesRoot);
            return Directory.GetFiles(casesRoot, "*.json", SearchOption.AllDirectories)
                .Select(CampaignFileService.ReadJsonObject)
                .Where(x => x.Count > 0)
                .Where(x => string.IsNullOrWhiteSpace(suite) || string.Equals(ReadString(x, "suite", ""), suite, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => ReadString(x, "suite", ""))
                .ThenBy(x => ReadString(x, "caseId", ""))
                .ToList();
        }

        private static Dictionary<string, object> LoadTestCase(string suite, string caseId)
        {
            if (string.IsNullOrWhiteSpace(caseId))
            {
                return new Dictionary<string, object>();
            }

            if (!string.IsNullOrWhiteSpace(suite))
            {
                return CampaignFileService.ReadJsonObject(TestCasePath(suite, caseId));
            }

            return LoadTestCases("").FirstOrDefault(x => string.Equals(ReadString(x, "caseId", ""), caseId, StringComparison.OrdinalIgnoreCase)) ?? new Dictionary<string, object>();
        }

        private static void SaveTestRun(Dictionary<string, object> result)
        {
            string runId = ReadString(result, "runId", Guid.NewGuid().ToString("N"));
            CampaignFileService.WriteJsonObject(Path.Combine(Program.TestsDir, "runs", CampaignFileService.SafePathSegment(runId, "run") + ".json"), result);
            PruneTestRuns(100);
        }

        internal static void PruneTestRuns(int retain)
        {
            string runsRoot = Path.Combine(Program.TestsDir, "runs");
            if (!Directory.Exists(runsRoot)) return;
            foreach (FileInfo file in new DirectoryInfo(runsRoot).GetFiles("*.json")
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .Skip(Math.Max(1, retain)))
            {
                try { file.Delete(); } catch { }
            }
        }

        private static Dictionary<string, object> TestCaseSummary(Dictionary<string, object> testCase, string path)
        {
            return new Dictionary<string, object>
            {
                ["suite"] = ReadString(testCase, "suite", ""),
                ["caseId"] = ReadString(testCase, "caseId", ""),
                ["name"] = ReadString(testCase, "name", ""),
                ["description"] = ReadString(testCase, "description", ""),
                ["mode"] = ReadString(testCase, "mode", ""),
                ["llmMode"] = TestCaseDefaultLlmMode(testCase),
                ["path"] = path,
                ["case"] = testCase
            };
        }

        private static void NormalizeTestCaseIdentity(Dictionary<string, object> testCase)
        {
            if (!testCase.ContainsKey("version"))
            {
                testCase["version"] = 1;
            }

            string suite = ReadString(testCase, "suite", "general");
            string caseId = FirstNonEmpty(ReadString(testCase, "caseId", ""), ReadString(testCase, "id", ""), CampaignFileService.SafePathSegment(ReadString(testCase, "name", "case"), "case"));
            testCase["suite"] = CampaignFileService.SafePathSegment(suite, "general");
            testCase["caseId"] = CampaignFileService.SafePathSegment(caseId, "case");
        }

        private static string TestCasePath(string suite, string caseId)
        {
            return Path.Combine(Program.TestsDir, "cases", CampaignFileService.SafePathSegment(suite, "general"), CampaignFileService.SafePathSegment(caseId, "case") + ".json");
        }

        private static string SnapshotPath(string campaignId, string correlationId)
        {
            return Path.Combine(Program.TestsDir, "snapshots", CampaignFileService.SafePathSegment(campaignId, "default"), CampaignFileService.SafePathSegment(correlationId, "snapshot") + ".json");
        }
    }
}
