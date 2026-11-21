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
    internal static partial class ActionPipelineService
    {
        private const string DiplomaticMarriageCompletionField = "diplomaticMarriageCompletion";
        private static readonly ConcurrentDictionary<string, byte> DiplomaticMarriageRecoveryWorkers = new(StringComparer.Ordinal);

        internal static Dictionary<string, object> ProposeAction(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            VerificationService.SaveTestSnapshotFromPayload(campaignId, correlationId, "action", "action.propose", payload);
            List<Dictionary<string, object>> inputs = ReadDictionaryList(payload, "actions");
            Dictionary<string, object> singleAction = ReadDictionary(payload, "action");
            if (singleAction != null)
            {
                inputs.Add(singleAction);
            }

            if (inputs.Count == 0)
            {
                inputs.Add(payload);
            }

            List<Dictionary<string, object>> queued = new List<Dictionary<string, object>>();
            List<string> errors = new List<string>();
            foreach (Dictionary<string, object> input in inputs)
            {
                if (!input.ContainsKey("correlationId"))
                {
                    input["correlationId"] = correlationId;
                }

                Dictionary<string, object> normalized = NormalizeActionCommand(input, campaignId, out List<string> actionErrors, payload);
                if (actionErrors.Count > 0)
                {
                    errors.AddRange(actionErrors);
                    continue;
                }

                queued.Add(QueueAction(campaignId, normalized));
            }

            if (errors.Count > 0)
            {
                OperationsService.LogOperational("action.reject", new Dictionary<string, object> { ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["errors"] = errors });
                OperationsService.WriteAudit(campaignId, correlationId, "server", "action", "action.validation_failed", "", "", "", "failed", 0, "Action proposal failed validation.", new Dictionary<string, object>
                {
                    ["inputs"] = inputs,
                    ["errors"] = errors
                });
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["errors"] = errors,
                    ["catalog"] = ActionCatalog()
                };
            }

            OperationsService.LogOperational("action.propose", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["count"] = queued.Count
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", "action", "action.proposed", "", "", "", "queued", 0, "Action proposal queued.", new Dictionary<string, object>
            {
                ["queued"] = queued,
                ["count"] = queued.Count
            });

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["correlationId"] = correlationId,
                ["queuedActions"] = queued,
                ["count"] = queued.Count
            };
        }

        internal static Dictionary<string, object> DecideActions(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> context = DialogueService.BuildContext(payload);
            object memories = context.ContainsKey("memories") ? context["memories"] : new List<object>();
            object worldState = payload.ContainsKey("worldState") ? payload["worldState"] : payload;
            string requestType = ReadString(payload, "requestType", "strategy");

            Dictionary<string, object> catalog = ActionCatalog();
            List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "system",
                    ["content"] = "You are Bannerlord Reign, a Bannerlord AI world director. Return strict JSON only. Use only the supplied action catalog. Prefer believable character and kingdom motives. Never invent IDs; use only IDs present in the world state."
                },
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = "Action catalog:\n" + Json.Serialize(catalog["commands"]) + "\n\nRelevant memories:\n" + Json.Serialize(memories) + "\n\nWorld state/request:\n" + Json.Serialize(worldState) + "\n\nReturn JSON like {\"actions\":[{\"command\":\"capture_settlement_plan\",\"actorHeroId\":\"...\",\"targetSettlementId\":\"...\",\"reason\":\"...\"}]}"
                }
            };

            Dictionary<string, object> llmPayload = new Dictionary<string, object>
            {
                ["requestType"] = requestType,
                ["messages"] = messages,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };

            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(llmPayload);
            if (!ReadBool(llm, "ok", false))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["stage"] = "llm", ["llm"] = llm };
            }

            string content = ReadString(llm, "content", "");
            Dictionary<string, object> parsed = TryParseJsonObject(content);
            if (parsed == null)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["stage"] = "parse", ["content"] = content };
            }

            Dictionary<string, object> proposePayload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["actions"] = ReadDictionaryList(parsed, "actions")
            };

            Dictionary<string, object> proposed = ProposeAction(proposePayload);
            proposed["llm"] = new Dictionary<string, object>
            {
                ["requestType"] = requestType,
                ["model"] = ReadString(llm, "model", ""),
                ["content"] = content
            };
            return proposed;
        }

        internal static Dictionary<string, object> QueueAction(string campaignId, Dictionary<string, object> record)
        {
            if (DialogueService._playerActionTurn != null && IsDialogueActionSource(ReadString(record, "source", ""))
                && !ReadString(record, "serverActionId", "").StartsWith("pa_", StringComparison.Ordinal))
            {
                string turn = FirstNonEmpty(ReadFirstString(DialogueService._playerActionTurn, "sceneTurnId", "turnId", "requestId"), OperationsService.EnsureCorrelationId(DialogueService._playerActionTurn));
                record["serverActionId"] = "da_" + ReignBeta.Shared.Dialogue.PlayerActionRules.Hash(campaignId + "|" + DialogueService.ContinuityTimeline(DialogueService._playerActionTurn)
                    + "|" + turn + "|" + ReadString(record, "command", "") + "|" + ReadString(record, "actorHeroStringId", "") + "|" + ReadString(record, "targetHeroStringId", "")).Substring(0, 48);
                record["actionId"] = record["serverActionId"];
            }
            string id = ReadString(record, "serverActionId", Guid.NewGuid().ToString("N"));
            string correlationId = ReadString(record, "correlationId", "");
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["id"] = id,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["status"] = "queued",
                ["command"] = ReadString(record, "command", ""),
                ["record"] = record
            };

            if (DialogueService._stagedTurnActions != null)
            {
                var prior = DialogueService.ReadDurableConversationAction(campaignId, id);
                if (prior != null)
                {
                    if (ReadString(ReadDictionary(prior, "record"), "termsHash", "") != ReadString(record, "termsHash", ""))
                        throw new InvalidOperationException("This turn already has different immutable action terms.");
                    return prior;
                }
                DialogueService._stagedTurnActions.Add(row); return row;
            }
            lock (Program.FileLock)
            {
                Dictionary<string, object> durable = DialogueService.ReadDurableConversationAction(campaignId, id);
                if (durable != null)
                {
                    if (ReadString(ReadDictionary(durable, "record"), "termsHash", "") != ReadString(record, "termsHash", ""))
                        throw new InvalidOperationException("This action ID already has different immutable terms.");
                    return durable;
                }
                DialogueService.SaveDurableConversationAction(campaignId, row);
                List<Dictionary<string, object>> queue = ReadActionQueueUnlocked(campaignId);
                var pendingMarriage = queue.FirstOrDefault(existing => ReadString(existing, "id", "") == id
                    && PendingDiplomaticMarriage(existing) != null);
                if (pendingMarriage != null) return pendingMarriage;
                queue.RemoveAll(x => ReadString(x, "id", "") == id);
                queue.Add(row);
                if (queue.Count > 500)
                {
                    queue = queue.Where((existing, index) => index >= queue.Count - 500
                        || PendingDiplomaticMarriage(existing) != null).ToList();
                }

                WriteActionQueueUnlocked(campaignId, queue);
                CampaignFileService.AppendJsonLineToPath(CampaignFileService.CampaignFile(campaignId, "actions", "actions.jsonl"), row);
            }

            OperationsService.WriteAudit(campaignId, correlationId, "server", "action", "action.queued", ReadFirstString(record, "actorHeroStringId", "targetHeroStringId"), id, "", "queued", 0, "Action queued for game execution.", new Dictionary<string, object>
            {
                ["row"] = row
            });
            return row;
        }

        internal static Dictionary<string, object> NextActions(Dictionary<string, object> payload, Dictionary<string, string> query)
        {
            payload = payload ?? new Dictionary<string, object>();
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaignQuery) ? campaignQuery : ReadString(payload, "campaignId", "default");
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsedLimit) ? parsedLimit : ReadInt(payload, "limit", 10);
            limit = Math.Max(1, Math.Min(50, limit));
            bool recoverDiplomaticMarriages;
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const long deliveryMaxAgeSeconds = 6 * 60 * 60;

            List<Dictionary<string, object>> enteredBackoff = new List<Dictionary<string, object>>();

            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadActionQueueUnlocked(campaignId);
                recoverDiplomaticMarriages = queue.Any(row => DiplomaticMarriageRecoveryDue(row, nowTs));
                // Read at most once per poll, and only when a durable row needs it.
                string deliveryGeneration = null;
                Func<string> generation = () => deliveryGeneration ?? (deliveryGeneration = DialogueService.ActionDeliveryGeneration(campaignId));
                bool changed = false;
                foreach (Dictionary<string, object> row in queue)
                {
                    string status = ReadString(row, "status", "queued");
                    if (selected.Count >= limit)
                    {
                        break;
                    }

                    if (!ReadString(row, "campaignId", "default").Equals(campaignId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Durable rows without a terminal report stay deliverable forever; repeated
                    // redeliveries back off (capped) instead of costing a rewrite every 30 s.
                    bool durable = DialogueService.DurableConversationAction(ReadString(row, "id", ""));
                    bool retryDelivery = durable
                        && !IsTerminalActionReportStatus(status)
                        && DialogueService.DurableRedeliveryDue(row, nowTs, generation);
                    if (!retryDelivery && !status.Equals("queued", StringComparison.OrdinalIgnoreCase) && !status.Equals("proposed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long queuedTs = ReadLong(row, "ts", 0);
                    if (!DialogueService.DurableConversationAction(ReadString(row, "id", "")) && queuedTs > 0 && nowTs - queuedTs > deliveryMaxAgeSeconds)
                    {
                        row["status"] = "expired";
                        row["expiredTs"] = nowTs;
                        row["expiryReason"] = "Undelivered action exceeded the six-hour delivery window.";
                        DialogueService.SaveDurableConversationAction(campaignId, row);
                        changed = true;
                        continue;
                    }

                    // A redelivery never regresses a later reported status (for example executing);
                    // the rejected regression used to discard deliveredTs and redeliver every poll.
                    if (!durable || ShouldAcceptActionStatusTransition(status, "delivered"))
                    {
                        row["status"] = "delivered";
                    }
                    row["deliveredTs"] = nowTs;
                    if (durable)
                    {
                        // A new client session (restore generation or verified load) restarts the
                        // backoff so its first redeliveries use the base cadence again.
                        string currentGeneration = generation();
                        long priorCount = ReadString(row, "deliveryGeneration", "") == currentGeneration ? ReadLong(row, "deliveryCount", 0) : 0;
                        long deliveryCount = priorCount + 1;
                        row["deliveryCount"] = deliveryCount;
                        row["deliveryGeneration"] = currentGeneration;
                        if (deliveryCount == DialogueService.ActionRedeliveryFreeDeliveries + 1)
                        {
                            enteredBackoff.Add(row);
                        }
                    }
                    DialogueService.SaveDurableConversationAction(campaignId, row);
                    selected.Add(row);
                    changed = true;
                }

                // An idle poll leaves the queue cache untouched.
                if (changed || !File.Exists(CampaignFileService.CampaignFile(campaignId, "actions", "action-queue.json")))
                {
                    WriteActionQueueUnlocked(campaignId, queue);
                }
            }

            foreach (Dictionary<string, object> row in enteredBackoff)
            {
                OperationsService.LogOperational("action.redelivery_backoff", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["actionId"] = ReadString(row, "id", ""),
                    ["status"] = ReadString(row, "status", ""),
                    ["deliveryCount"] = ReadLong(row, "deliveryCount", 0),
                    ["nextDelaySeconds"] = DialogueService.ActionRedeliveryDelaySeconds(ReadLong(row, "deliveryCount", 0))
                });
            }

            List<object> records = selected.Select(x => x.ContainsKey("record") ? x["record"] : x).ToList();
            OperationsService.LogOperational("action.next", new Dictionary<string, object> { ["campaignId"] = campaignId, ["count"] = records.Count });
            foreach (Dictionary<string, object> row in selected)
            {
                Dictionary<string, object> record = ReadDictionary(row, "record") ?? row;
                OperationsService.WriteAudit(campaignId, FirstNonEmpty(ReadString(row, "correlationId", ""), ReadString(record, "correlationId", "")), "server", "action", "action.delivered", ReadFirstString(record, "actorHeroStringId", "targetHeroStringId"), ReadString(row, "id", ReadString(record, "serverActionId", "")), "", "delivered", 0, "Action delivered to game client.", new Dictionary<string, object>
                {
                    ["row"] = row
                });
            }
            if (recoverDiplomaticMarriages) ScheduleDiplomaticMarriageRecovery(campaignId);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["count"] = records.Count,
                ["actions"] = records
            };
        }

        internal static Dictionary<string, object> ReportAction(Dictionary<string, object> payload)
        {
            return ReportAction(payload, null, null);
        }

        internal static Dictionary<string, object> ReportAction(Dictionary<string, object> payload,
            Action beforeMarriageAdmissionCommit, Action beforeMarriageEffectCommit)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string id = ReadFirstString(payload, "serverActionId", "actionId", "id");
            string reportedStatus = ReadString(payload, "status", "reported");
            string status = reportedStatus;
            string correlationId = ReadFirstString(payload, "correlationId", "requestId", "clientRequestId");
            Dictionary<string, object> matchedRecord = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(id))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "serverActionId/actionId is required." };
            }
            bool nativeMarriage = reportedStatus == "completed" && RelationshipsService.ReportsNativeDiplomaticMarriageSettlement(payload);
            bool scopedMarriage = nativeMarriage || (reportedStatus == "completed" && RelationshipsService.ReportsLegacyDiplomaticMarriage(payload));
            using var timelineScope = scopedMarriage ? WorldSimulationService.EnterWorldHistoryTimelineScope(campaignId) : null;
            var marriageScope = scopedMarriage
                ? RelationshipsService.CaptureDiplomaticMarriageReportScope(campaignId, id, payload, nativeMarriage) : null;
            if (nativeMarriage && marriageScope == null)
                return TestDict("ok", false, "error", "The diplomatic marriage report belongs to a different campaign load or timeline.");

            bool found = false;
            bool ignoredStatusRegression = false;
            // A protocol terminal report (TerminalActionReportApi) owns its row: the legacy
            // report returns that canonical status and writes no evidence, audit or projection.
            bool protocolOwned = false;
            string authoritativeStatus = status;
            Dictionary<string, object> admittedDiplomaticMarriage = null;
            bool admittedFirstMarriage = false;
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadActionQueueUnlocked(campaignId);
                foreach (Dictionary<string, object> row in queue)
                {
                    if (ReadString(row, "id", "") == id)
                    {
                        matchedRecord = ReadDictionary(row, "record") ?? new Dictionary<string, object>();
                        correlationId = FirstNonEmpty(correlationId, ReadString(row, "correlationId", ""), ReadString(matchedRecord, "correlationId", ""));
                        found = true;
                        if (DialogueService.IsProtocolOwnedTerminalAction(row))
                        {
                            protocolOwned = true;
                            authoritativeStatus = status = ReadString(row, "status", "");
                            break;
                        }
                        status = NormalizeActionReportStatus(matchedRecord, payload,
                            reportedStatus);
                        authoritativeStatus = status;
                        if (IsResolvedNpcGovernmentRefusal(matchedRecord, payload, reportedStatus))
                        {
                            payload = new Dictionary<string, object>(payload,
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["status"] = status,
                                ["reportedStatus"] = reportedStatus,
                                ["statusNormalization"] =
                                    "resolved_npc_government_refusal"
                            };
                        }
                        string currentStatus = ReadString(row, "status", "");
                        bool legacyRefusalCorrection = IsLegacyGovernmentRefusalCorrection(
                            row, matchedRecord, payload, reportedStatus);
                        if (legacyRefusalCorrection)
                            payload["statusCorrection"] = "legacy_government_refusal";
                        if (ShouldAcceptActionStatusTransition(currentStatus, status)
                            || legacyRefusalCorrection)
                        {
                            // Freeze explicit marriage evidence with first acceptance so a
                            // retry can finish its effects without reopening historical reports.
                            if (!IsTerminalActionReportStatus(currentStatus))
                            {
                                var completion = RelationshipsService.DiplomaticMarriageCompletion(id, status, matchedRecord, payload, marriageScope);
                                if (completion != null)
                                {
                                    row[DiplomaticMarriageCompletionField] = completion;
                                    admittedFirstMarriage = true;
                                }
                            }
                            row["status"] = status;
                            row["reportTs"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            row["report"] = payload;
                        }
                        else
                        {
                            authoritativeStatus = currentStatus;
                            ignoredStatusRegression = true;
                        }
                        DialogueService.SaveDurableConversationAction(campaignId, row);
                        // The save refreshes row from a protocol winner that committed after the queue read.
                        if (DialogueService.IsProtocolOwnedTerminalAction(row))
                        {
                            protocolOwned = true;
                            authoritativeStatus = status = ReadString(row, "status", "");
                        }
                        else admittedDiplomaticMarriage = PendingDiplomaticMarriage(row);
                        break;
                    }
                }

                if (!protocolOwned)
                {
                    if (admittedFirstMarriage) beforeMarriageAdmissionCommit?.Invoke();
                    WriteActionQueueUnlocked(campaignId, queue);
                    CampaignFileService.AppendJsonLineToPath(CampaignFileService.CampaignFile(campaignId, "actions", "actions.jsonl"), new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["campaignId"] = campaignId,
                        ["status"] = status,
                        ["report"] = payload
                    });
                }
            }
            timelineScope?.Dispose();

            correlationId = FirstNonEmpty(correlationId, ReadFirstString(ReadDictionary(payload, "result"), "correlationId"));
            OperationsService.LogOperational("action.report", new Dictionary<string, object> { ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["id"] = id, ["status"] = status, ["reportedStatus"] = reportedStatus, ["found"] = found, ["protocolOwned"] = protocolOwned });
            if (protocolOwned)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["correlationId"] = correlationId,
                    ["id"] = id,
                    ["status"] = authoritativeStatus,
                    ["reportedStatus"] = reportedStatus,
                    ["found"] = true,
                    ["ignoredStatusRegression"] = true,
                    ["terminalReportOwner"] = "protocol"
                };
            }
            Dictionary<string, object> result = ReadDictionary(payload, "result") ?? new Dictionary<string, object>();
            List<string> reportErrors = new List<string>();
            string debugMessage = ReadFirstString(result, "debugMessage", "DebugMessage", "message", "Message");
            if (!string.IsNullOrWhiteSpace(debugMessage))
            {
                reportErrors.Add(debugMessage);
            }

            OperationsService.WriteAudit(campaignId, correlationId, "game_client", "action", "action.execution_report", ReadFirstString(payload, "actorHeroStringId", "targetHeroStringId"), id, "", status, 0, "Game client reported action execution result.", new Dictionary<string, object>
            {
                ["found"] = found,
                ["authoritativeStatus"] = authoritativeStatus,
                ["reportedStatus"] = reportedStatus,
                ["ignoredStatusRegression"] = ignoredStatusRegression,
                ["record"] = matchedRecord,
                ["report"] = payload,
                ["result"] = result,
                ["failureClass"] = status.Equals("failed", StringComparison.OrdinalIgnoreCase) ? ClassifyActionFailure(1, reportErrors) : ""
            });
            if (ignoredStatusRegression)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["correlationId"] = correlationId,
                    ["id"] = id,
                    ["status"] = authoritativeStatus,
                    ["reportedStatus"] = reportedStatus,
                    ["found"] = found,
                    ["ignoredStatusRegression"] = true
                };
            }
            if (found) DialogueService.RecordSharedActionReceipt(campaignId, payload, matchedRecord, authoritativeStatus);
            if (admittedDiplomaticMarriage != null)
                CompletePendingDiplomaticMarriage(campaignId, id, matchedRecord, admittedDiplomaticMarriage, beforeMarriageEffectCommit);
            Dictionary<string, object> memoryStore = DialogueService.StoreActionResultMemory(payload, "actions_report");
            List<Dictionary<string, object>> nativeRelationChanges = RelationshipsService.EvaluateCompletedActionRelationships(payload);
            DiplomacyService.UpdateDiplomaticEventForAction(campaignId, id, status, payload);
            RebellionService.UpdateNegotiationFromActionReport(campaignId, id, status, payload);
            RebellionService.UpdateRebellionBackingFromActionReport(campaignId, id, status, payload);
            Dictionary<string, object> ambassadorReceiptPayload = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
            {
                ["record"] = matchedRecord ?? new Dictionary<string, object>()
            };
            CourtService.UpdateAmbassadorReferralActionReceipt(campaignId, ambassadorReceiptPayload, status);
            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["id"] = id, ["status"] = status, ["reportedStatus"] = reportedStatus, ["found"] = found, ["memoryStore"] = memoryStore, ["nativeRelationChanges"] = nativeRelationChanges };
        }

        private static Dictionary<string, object> PendingDiplomaticMarriage(Dictionary<string, object> row)
        {
            var completion = ReadDictionary(row, DiplomaticMarriageCompletionField);
            return ReadString(row, "status", "") == "completed" && completion != null
                && ReadString(completion, "outcome", "").Length == 0 ? completion : null;
        }

        private static bool DiplomaticMarriageRecoveryDue(Dictionary<string, object> row, long now)
        {
            var pending = PendingDiplomaticMarriage(row);
            return pending != null && ReadLong(pending, "retryAfterUtc", 0) <= now;
        }

        private static void ScheduleDiplomaticMarriageRecovery(string campaignId)
        {
            if (!DiplomaticMarriageRecoveryWorkers.TryAdd(campaignId, 0)) return;
            _ = Task.Run(() =>
            {
                Program.CampaignDataGate.EnterReadLock();
                try
                {
                    List<Dictionary<string, object>> pending;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    lock (Program.FileLock)
                        pending = ReadActionQueueUnlocked(campaignId)
                            .Where(row => DiplomaticMarriageRecoveryDue(row, now)).Take(4).ToList();
                    foreach (var row in pending)
                    {
                        try
                        {
                            CompletePendingDiplomaticMarriage(campaignId, ReadString(row, "id", ""),
                                ReadDictionary(row, "record"), PendingDiplomaticMarriage(row));
                        }
                        catch (Exception error)
                        {
                            Console.Error.WriteLine("Reign diplomatic marriage recovery: " + error.GetType().Name);
                        }
                    }
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("Reign diplomatic marriage recovery read: " + error.GetType().Name);
                }
                finally
                {
                    Program.CampaignDataGate.ExitReadLock();
                    DiplomaticMarriageRecoveryWorkers.TryRemove(campaignId, out _);
                }
            });
        }

        private static void CompletePendingDiplomaticMarriage(string campaignId, string id,
            Dictionary<string, object> record, Dictionary<string, object> admission, Action beforeEffectCommit = null)
        {
            bool applied = RelationshipsService.FinalizeDiplomaticMarriageEffects(campaignId, id, record, admission, beforeEffectCommit);
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadActionQueueUnlocked(campaignId);
                var row = queue.FirstOrDefault(existing => ReadString(existing, "id", "") == id);
                var pending = PendingDiplomaticMarriage(row);
                if (pending == null || Json.Serialize(pending) != Json.Serialize(admission)) return;
                if (applied) pending["outcome"] = "applied";
                // A concurrent timeline switch or missing snapshot lineage does not invalidate
                // first admission. Preserve it and let later polls recheck without blocking siblings.
                else pending["retryAfterUtc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30;
                WriteActionQueueUnlocked(campaignId, queue);
            }
        }

        internal static string NormalizeActionReportStatus(
            Dictionary<string, object> record,
            Dictionary<string, object> payload,
            string reportedStatus)
        {
            return IsResolvedNpcGovernmentRefusal(record, payload, reportedStatus)
                ? "rejected" : reportedStatus;
        }

        private static bool IsResolvedNpcGovernmentRefusal(
            Dictionary<string, object> record,
            Dictionary<string, object> payload,
            string reportedStatus)
        {
            if (!string.Equals(reportedStatus, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(reportedStatus, "rejected", StringComparison.OrdinalIgnoreCase)) return false;
            string source = FirstNonEmpty(ReadString(record, "source", ""),
                ReadString(payload, "source", ""));
            if (!source.Equals("world_diplomacy_director",
                StringComparison.OrdinalIgnoreCase)) return false;
            Dictionary<string, object> result = ReadDictionary(payload, "result")
                ?? new Dictionary<string, object>();
            string resultCode = ReadFirstString(result, "resultCode", "ResultCode");
            string failureKind = ReadFirstString(result, "failureKind", "FailureKind");
            return failureKind.Equals("government_authority",
                       StringComparison.OrdinalIgnoreCase)
                && result.Keys.Any(key => key.Equals("retryable",
                    StringComparison.OrdinalIgnoreCase))
                && !ReadBool(result, "retryable", true)
                && (IsRecordedGovernmentRefusalResult(result)
                    || resultCode.Equals("government_pressure_prevailed",
                        StringComparison.OrdinalIgnoreCase)
                    || resultCode.Equals("government_level_five_block",
                        StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsRecordedGovernmentRefusalResult(Dictionary<string, object> result)
        {
            var diagnostics = ReadDictionaryList(result, "diagnostics");
            Func<string, string> value = key => ReadString(diagnostics.FirstOrDefault(x =>
                ReadString(x, "key", "").Equals(key, StringComparison.Ordinal)), "value", "");
            return ReadString(result, "resultCode", "") == "government_authorization_refused"
                && !ReadBool(result, "success", true) && ReadBool(result, "completed", false)
                && !ReadBool(result, "retryable", true)
                && ReadDictionaryList(result, "effects").Count == 0
                && ReadDictionaryList(result, "changedEntities").Count == 0
                && !string.IsNullOrWhiteSpace(value("governmentBusinessId"))
                && Reign.Core.Contracts.Government.GovernmentBusinessRules.IsRecordedAuthorizationRefusal(
                    value("governmentStatus"), value("governmentExecutedOptionId"));
        }

        internal static bool IsLegacyGovernmentRefusalCorrection(Dictionary<string, object> row,
            Dictionary<string, object> record, Dictionary<string, object> payload, string reportedStatus)
        {
            var oldResult = ReadDictionary(ReadDictionary(row, "report"), "result")
                ?? new Dictionary<string, object>();
            var result = ReadDictionary(payload, "result") ?? new Dictionary<string, object>();
            return ReadString(row, "status", "") == "failed"
                && reportedStatus.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                && ReadString(oldResult, "resultCode", "") == "government_refused"
                && ReadString(oldResult, "failureKind", "") == "government_authority"
                && !ReadBool(oldResult, "success", true) && !ReadBool(oldResult, "completed", true)
                && ReadDictionaryList(oldResult, "effects").Count == 0
                && ReadDictionaryList(oldResult, "changedEntities").Count == 0
                && IsRecordedGovernmentRefusalResult(result)
                && IsResolvedNpcGovernmentRefusal(record, payload, reportedStatus);
        }

        internal static bool ShouldAcceptActionStatusTransition(string currentStatus, string incomingStatus)
        {
            string current = NormalizeLookup(currentStatus).Replace(' ', '_');
            string incoming = NormalizeLookup(incomingStatus).Replace(' ', '_');
            if (string.IsNullOrWhiteSpace(current)) return true;
            if (string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsTerminalActionReportStatus(current)) return false;
            return ActionReportStatusRank(incoming) >= ActionReportStatusRank(current);
        }

        internal static bool IsTerminalActionReportStatus(string status)
        {
            string normalized = NormalizeLookup(status).Replace(' ', '_');
            return new[]
            {
                "completed", "failed", "rejected", "cancelled", "canceled",
                "expired", "blocked", "invalid", "obsolete"
            }.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }

        private static int ActionReportStatusRank(string status)
        {
            string normalized = NormalizeLookup(status).Replace(' ', '_');
            if (IsTerminalActionReportStatus(normalized)) return 3;
            if (new[] { "executing", "retry", "in_progress" }
                .Contains(normalized, StringComparer.OrdinalIgnoreCase)) return 2;
            if (new[]
                {
                    "accepted", "delivered", "enqueued", "enqueued_immediate",
                    "queued", "pending", "proposed"
                }.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        internal static Dictionary<string, object> ActionFailures(Dictionary<string, object> payload, Dictionary<string, string> query)
        {
            payload = payload ?? new Dictionary<string, object>();
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaignQuery) ? campaignQuery : ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsedLimit) ? parsedLimit : ReadInt(payload, "limit", 50);
            limit = Math.Max(1, Math.Min(200, limit));
            List<Dictionary<string, object>> rows = Program.ReadJsonLinesFromPath(CampaignFileService.CampaignFile(campaignId, "audit", "audit.jsonl"))
                .Where(x =>
                {
                    string phase = ReadString(x, "phase", "");
                    string status = ReadString(x, "status", "");
                    return phase.IndexOf("action.", StringComparison.OrdinalIgnoreCase) >= 0
                        && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                            || phase.Equals("action.validation_failed", StringComparison.OrdinalIgnoreCase));
                })
                .Reverse()
                .Take(limit)
                .Select(x =>
                {
                    Dictionary<string, object> data = ReadDictionary(x, "data") ?? new Dictionary<string, object>();
                    List<string> errors = ReadStringList(data, "errors");
                    if (errors.Count == 0)
                    {
                        errors = ReadStringList(ReadDictionary(data, "report"), "errors");
                    }

                    Dictionary<string, object> record = ReadDictionary(data, "normalizedRecord") ?? ReadDictionary(data, "record") ?? new Dictionary<string, object>();
                    string command = FirstNonEmpty(ReadString(data, "command", ""), ReadString(record, "command", ""));
                    return new Dictionary<string, object>
                    {
                        ["timestamp"] = ReadString(x, "timestamp", ""),
                        ["correlationId"] = ReadString(x, "correlationId", ""),
                        ["phase"] = ReadString(x, "phase", ""),
                        ["status"] = ReadString(x, "status", ""),
                        ["command"] = command,
                        ["capability"] = ActionCapabilityForCommand(command),
                        ["failureClass"] = FirstNonEmpty(ReadString(data, "failureClass", ""), ClassifyActionFailure(1, errors)),
                        ["summary"] = ReadString(x, "summary", ""),
                        ["errors"] = errors,
                        ["trace"] = ReadDictionaryList(data, "trace"),
                        ["rawAction"] = ReadDictionary(data, "rawAction") ?? new Dictionary<string, object>(),
                        ["normalizedRecord"] = record
                    };
                })
                .ToList();

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["count"] = rows.Count,
                ["failures"] = rows,
                ["unresolvedDeliveries"] = DialogueService.UnresolvedActionDeliverySummary(campaignId)
            };
        }

        internal static List<Dictionary<string, object>> ReadActionQueueUnlocked(string campaignId)
        {
            string path = CampaignFileService.CampaignFile(campaignId, "actions", "action-queue.json");
            var durable = DialogueService.ReadDurableConversationActions(campaignId);
            if (durable.Count > 0 || File.Exists(path))
            {
                var legacy = File.Exists(path) ? ReadActionQueueFromPath(path) : new List<Dictionary<string, object>>();
                // Save Sync restores the database. A newer JSON cache must never resurrect an undone handover.
                // Ordinary legacy actions still need their repaired terminal state
                // persisted to both the queue and linked diplomacy event.
                var ordinary = ReconcileActionQueueTerminalReceiptsUnlocked(campaignId,
                    legacy.Where(r => !DialogueService.DurableConversationAction(ReadString(r, "id", ""))).ToList(), true);
                return durable.Concat(ordinary).OrderBy(r => ReadLong(r, "ts", 0))
                    .ThenBy(r => ReadString(r, "id", "").StartsWith("pa_", StringComparison.Ordinal) ? 0 : 1).ToList();
            }
            if (!File.Exists(path))
            {
                string legacyPath = Path.Combine(Program.DataDir, "action-queue.json");
                if (!File.Exists(legacyPath))
                {
                    return new List<Dictionary<string, object>>();
                }

                return ReconcileActionQueueTerminalReceiptsUnlocked(campaignId,
                    ReadActionQueueFromPath(legacyPath)
                    .Where(x => ReadString(x, "campaignId", "default").Equals(campaignId, StringComparison.OrdinalIgnoreCase))
                    .ToList(), false);
            }

            return ReconcileActionQueueTerminalReceiptsUnlocked(
                campaignId, ReadActionQueueFromPath(path), true);
        }

        private static List<Dictionary<string, object>> ReconcileActionQueueTerminalReceiptsUnlocked(
            string campaignId,
            List<Dictionary<string, object>> queue,
            bool persistQueue)
        {
            queue = queue ?? new List<Dictionary<string, object>>();
            if (!queue.Any(row => !IsTerminalActionReportStatus(ReadString(row, "status", ""))))
                return queue;

            string historyPath = CampaignFileService.CampaignFile(campaignId, "actions", "actions.jsonl");
            if (!File.Exists(historyPath)) return queue;
            Dictionary<string, Dictionary<string, object>> terminalById =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> historyRow in Program.ReadJsonLinesFromPath(historyPath))
            {
                string historyStatus = ReadString(historyRow, "status", "");
                string historyId = ReadString(historyRow, "id",
                    ReadFirstString(ReadDictionary(historyRow, "report"), "serverActionId", "actionId"));
                if (!string.IsNullOrWhiteSpace(historyId)
                    && IsTerminalActionReportStatus(historyStatus))
                    terminalById[historyId] = historyRow;
            }

            bool changed = false;
            List<Tuple<string, string, Dictionary<string, object>>> healed =
                new List<Tuple<string, string, Dictionary<string, object>>>();
            foreach (Dictionary<string, object> row in queue)
            {
                string id = ReadString(row, "id", "");
                string currentStatus = ReadString(row, "status", "");
                if (IsTerminalActionReportStatus(currentStatus)
                    || !terminalById.TryGetValue(id, out Dictionary<string, object> terminal))
                    continue;
                string terminalStatus = ReadString(terminal, "status", "");
                Dictionary<string, object> terminalReport =
                    ReadDictionary(terminal, "report") ?? new Dictionary<string, object>();
                row["status"] = terminalStatus;
                row["reportTs"] = ReadLong(terminal, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                row["report"] = terminalReport;
                healed.Add(Tuple.Create(id, terminalStatus, terminalReport));
                changed = true;
            }
            if (changed && persistQueue)
            {
                WriteActionQueueUnlocked(campaignId, queue);
                foreach (Tuple<string, string, Dictionary<string, object>> item in healed)
                    DiplomacyService.UpdateDiplomaticEventForAction(campaignId, item.Item1, item.Item2, item.Item3);
            }
            return queue;
        }

        internal static List<Dictionary<string, object>> ReadActionQueueFromPath(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            try
            {
                List<Dictionary<string, object>> typed = Json.Deserialize<List<Dictionary<string, object>>>(json);
                if (typed != null)
                {
                    return typed;
                }
            }
            catch
            {
            }

            try
            {
                object value = Json.DeserializeObject(json);
                if (value is ArrayList array)
                {
                    return array.Cast<object>().OfType<Dictionary<string, object>>().ToList();
                }

                if (value is IEnumerable<object> enumerable)
                {
                    return enumerable.OfType<Dictionary<string, object>>().ToList();
                }
            }
            catch
            {
            }

            return new List<Dictionary<string, object>>();
        }

        internal static void WriteActionQueueUnlocked(string campaignId, List<Dictionary<string, object>> queue)
        {
            string path = CampaignFileService.CampaignFile(campaignId, "actions", "action-queue.json");
            CampaignFileService.WriteJsonObject(path, queue ?? new List<Dictionary<string, object>>());
        }

        internal static string ModelForRequest(Dictionary<string, object> settings, string requestType)
        {
            string key;
            switch ((requestType ?? "").ToLowerInvariant())
            {
                case "diplomacy": key = "diplomacyModel"; break;
                case "events": key = "eventsModel"; break;
                case "event":
                case "social_event": key = "eventsModel"; break;
                case "political_pressure_trait_selector":
                case "political_pressure_narration": key = "fastModel"; break;
                case "memory": key = "memoryModel"; break;
                case "character_construction": key = "characterConstructionModel"; break;
                case "relationship": key = "relationshipModel"; break;
                case "correspondence": key = "correspondenceModel"; break;
                case "action_planner": key = "actionPlannerModel"; break;
                case "action_router": key = "actionRouterModel"; break;
                case "action_repair": key = "actionRouterModel"; break;
                case "strategy":
                case "action":
                case "actions": key = "strategyModel"; break;
                default: key = "dialogueModel"; break;
            }

            string model = ReadString(settings, key, "");
            if (string.IsNullOrWhiteSpace(model)
                && key.Equals("fastModel", StringComparison.OrdinalIgnoreCase))
                model = ReadString(settings, "eventsModel", "");
            return string.IsNullOrWhiteSpace(model) ? ReadString(settings, "dialogueModel", "") : model;
        }

        internal static string CanonicalCommand(string command)
        {
            string value = (command ?? "").Trim();
            if (value.Length == 0)
            {
                return "";
            }

            value = value.Replace("-", "_").Replace(" ", "_");
            switch (value.ToLowerInvariant())
            {
                case "diplomacydeclarewar":
                case "diplomacy_declare_war":
                case "declarewar": return "declare_war";
                case "diplomacymakepeace":
                case "diplomacy_make_peace":
                case "makepeace": return "make_peace";
                case "diplomacyoffertributepeace":
                case "offertributepeace": return "offer_tribute_peace";
                case "diplomacyrecordpromise":
                case "recordpromise": return "record_promise";
                case "diplomacydemandreparationspeace":
                case "demandreparationspeace": return "demand_reparations_peace";
                case "diplomacydemandsettlementpeace":
                case "demandsettlementpeace": return "demand_settlement_peace";
                case "diplomacydemandsurrenderpeace":
                case "demandsurrenderpeace": return "demand_surrender_peace";
                case "diplomacysigntradeagreement":
                case "signtradeagreement": return "sign_trade_agreement";
                case "diplomacysignnonaggressionpact":
                case "signnonaggressionpact": return "sign_non_aggression_pact";
                case "diplomacysignalliance":
                case "signalliance": return "sign_alliance";
                case "diplomacysigndefensivepact":
                case "signdefensivepact": return "sign_defensive_pact";
                case "diplomacybreaktreaty":
                case "breaktreaty": return "break_treaty";
                case "diplomacyexchangeprisoners":
                case "exchangeprisoners": return "exchange_prisoners";
                case "diplomacyransompackage":
                case "ransompackage": return "ransom_package";
                case "diplomacywarindemnity":
                case "warindemnity": return "war_indemnity";
                case "diplomaticpackage": return "diplomatic_package";
                case "regulartradepackage":
                case "tradepackage":
                case "barterpackage":
                case "barter_package": return "trade_package";
                case "strategyrecruitandrecover": return "recruit_and_recover";
                case "strategyformarmy": return "form_army";
                case "strategyattacksettlement": return "attack_settlement";
                case "strategycapturesettlement":
                case "capture_settlement": return "capture_settlement_plan";
                case "politicsrecruitlordtorebellion":
                case "recruitlordtorebellion": return "recruit_lord_to_rebellion";
                case "politicsjoinrebellion":
                case "joinrebellion": return "join_rebellion";
                case "politicssurrenderrebellion":
                case "surrenderrebellion": return "surrender_rebellion";
                case "politicsconsentgovernmentreduction":
                case "consentgovernmentreduction": return "consent_government_reduction";
                case "regularfollowonmap":
                case "followonmap":
                case "follow_on_map":
                case "regularfollowinscene":
                case "followinscene":
                case "follow_in_scene": return "follow";
                case "regularstopfollowing":
                case "stopfollowing": return "stop_following";
                case "regulargotosettlement":
                case "gotosettlement": return "go_to_settlement";
                case "regularpatrolaroundsettlement":
                case "patrolaroundsettlement": return "patrol_around_settlement";
                case "regularwaitnearsettlement":
                case "waitnearsettlement": return "wait_near_settlement";
                case "regularraidvillage":
                case "raidvillage": return "raid_village";
                case "regularbesiegesettlement":
                case "besiegesettlement": return "besiege_settlement";
                case "regularcreateparty":
                case "createparty": return "create_party";
                case "regularshowtheway":
                case "showtheway": return "show_the_way";
                case "regularattackparty":
                case "attackparty": return "attack_party";
                case "regularattackplayerparty":
                case "attackplayerparty": return "attack_player_party";
                case "regularsurrendertoplayer":
                case "surrendertoplayer": return "surrender_to_player";
                case "regularleaveplayeralone":
                case "leaveplayeralone": return "leave_player_alone";
                case "regularkillcharacter":
                case "killcharacter": return "kill_character";
                case "regularduelplayer":
                case "duelplayer": return "duel_player";
                case "regularpreparearrest":
                case "preparearrest": return "prepare_arrest";
                case "regularconfirmarrest":
                case "confirmarrest": return "confirm_arrest";
                case "regularreleasearrestedcharacter":
                case "releasearrestedcharacter": return "release_arrested_character";
                case "regularrescindarrestaccusation":
                case "rescindarrestaccusation": return "rescind_arrest_accusation";
                case "regularplayerattackparty":
                case "playerattackparty": return "player_attack_party";
                case "regulargivegoldtoplayer":
                case "givegoldtoplayer": return "give_gold_to_player";
                case "regulartransfergold":
                case "transfergold": return "transfer_gold";
                case "regulartransferitem":
                case "transferitem": return "transfer_item";
                case "regulartransferworkshop":
                case "transferworkshop": return "transfer_workshop";
                case "regulartransferprisoner":
                case "transferprisoner": return "transfer_prisoner";
                case "regularhireplayerasmercenary":
                case "hireplayerasmercenary": return "hire_player_as_mercenary";
                case "regulardismissplayermercenary":
                case "dismissplayermercenary": return "dismiss_player_mercenary";
                case "regularofferplayervassalage":
                case "offerplayervassalage": return "offer_player_vassalage";
                case "regulardismissplayervassal":
                case "dismissplayervassal": return "dismiss_player_vassal";
                case "regularjoinclan":
                case "joinclan": return "join_clan";
                case "regularleaveclan":
                case "leaveclan": return "leave_clan";
                case "regularjoinkingdom":
                case "joinkingdom": return "join_kingdom";
                case "regularleavekingdom":
                case "leavekingdom": return "leave_kingdom";
                case "regularhiremercenaryclan":
                case "hiremercenaryclan": return "hire_mercenary_clan";
                case "regularaccepttemporarypartyguest":
                case "accepttemporarypartyguest": return "accept_temporary_party_guest";
                case "regularrecruitencounteredresident":
                case "recruitencounteredresident": return "recruit_encountered_resident";
                case "regularcreateclanaccord":
                case "createclanaccord": return "create_clan_accord";
                case "regularcancelclanaccord":
                case "cancelclanaccord": return "cancel_clan_accord";
                case "regularreleaseresidentfromduty":
                case "releaseresidentfromduty": return "release_resident_from_duty";
                case "regularrenewtemporarypartyguest":
                case "renewtemporarypartyguest": return "renew_temporary_party_guest";
                case "regularendtemporarypartyguest":
                case "endtemporarypartyguest": return "end_temporary_party_guest";
                case "regularacknowledgeownfactioncombatrisk":
                case "acknowledgeownfactioncombatrisk": return "acknowledge_own_faction_combat_risk";
                default: return value.ToLowerInvariant();
            }
        }

        internal static string MapCommandToActionType(string command)
        {
            switch (CanonicalCommand(command))
            {
                case "create_clan_accord": return "RegularCreateClanAccord";
                case "cancel_clan_accord": return "RegularCancelClanAccord";
                case "declare_war": return "DiplomacyDeclareWar";
                case "make_peace": return "DiplomacyMakePeace";
                case "offer_tribute_peace": return "DiplomacyOfferTributePeace";
                case "record_promise": return "DiplomacyRecordPromise";
                case "demand_reparations_peace": return "DiplomacyDemandReparationsPeace";
                case "demand_settlement_peace": return "DiplomacyDemandSettlementPeace";
                case "demand_surrender_peace": return "DiplomacyDemandSurrenderPeace";
                case "sign_trade_agreement": return "DiplomacySignTradeAgreement";
                case "sign_non_aggression_pact": return "DiplomacySignNonAggressionPact";
                case "sign_alliance": return "DiplomacySignAlliance";
                case "sign_defensive_pact": return "DiplomacySignDefensivePact";
                case "break_treaty": return "DiplomacyBreakTreaty";
                case "exchange_prisoners": return "DiplomacyExchangePrisoners";
                case "ransom_package": return "DiplomacyRansomPackage";
                case "hostage_guarantee": return "DiplomacyHostageGuarantee";
                case "war_indemnity": return "DiplomacyWarIndemnity";
                case "recognize_conquest": return "DiplomacyRecognizeConquest";
                case "return_occupied_settlement": return "DiplomacyReturnOccupiedSettlement";
                case "demilitarized_border": return "DiplomacyDemilitarizedBorder";
                case "caravan_protection_agreement": return "DiplomacyCaravanProtectionAgreement";
                case "supply_agreement": return "DiplomacySupplyAgreement";
                case "loan_or_subsidy": return "DiplomacyLoanOrSubsidy";
                case "pay_to_stay_neutral": return "DiplomacyPayToStayNeutral";
                case "pay_to_join_war": return "DiplomacyPayToJoinWar";
                case "guarantee_independence": return "DiplomacyGuaranteeIndependence";
                case "protectorate_or_vassalage": return "DiplomacyProtectorateOrVassalage";
                case "diplomatic_package": return "DiplomacyPackage";
                case "back_rebellion": return "DiplomacyBackRebellion";
                case "trade_package": return "RegularTradePackage";
                case "recruit_and_recover": return "StrategyRecruitAndRecover";
                case "form_army": return "StrategyFormArmy";
                case "attack_settlement": return "StrategyAttackSettlement";
                case "capture_settlement_plan": return "StrategyCaptureSettlement";
                case "start_ruling_clan_rebellion": return "PoliticsStartRulingClanRebellion";
                case "recruit_lord_to_rebellion": return "PoliticsRecruitLordToRebellion";
                case "join_rebellion": return "PoliticsJoinRebellion";
                case "surrender_rebellion": return "PoliticsSurrenderRebellion";
                case "resolve_rebellion_pledge": return "PoliticsResolveRebellionPledge";
                case "resolve_rebellion_summons": return "PoliticsResolveRebellionSummons";
                case "consent_government_reduction": return "PoliticsConsentGovernmentReduction";
                case "install_ruling_clan": return "PoliticsInstallRulingClan";
                case "marriage_alliance": return "PoliticsMarriageAlliance";
                case "support_claimant": return "PoliticsSupportClaimant";
                case "encourage_clan_defection": return "PoliticsEncourageClanDefection";
                case "exile_clan": return "PoliticsExileClan";
                case "restore_exiled_clan": return "PoliticsRestoreExiledClan";
                case "mediate_clan_dispute": return "PoliticsMediateClanDispute";
                case "resolve_civil_war": return "PoliticsResolveCivilWar";
                case "follow": return "RegularFollowOnMap";
                case "stop_following": return "RegularStopFollowing";
                case "go_to_settlement": return "RegularGoToSettlement";
                case "patrol_around_settlement": return "RegularPatrolAroundSettlement";
                case "wait_near_settlement": return "RegularWaitNearSettlement";
                case "raid_village": return "RegularRaidVillage";
                case "besiege_settlement": return "RegularBesiegeSettlement";
                case "create_party": return "RegularCreateParty";
                case "show_the_way": return "RegularShowTheWay";
                case "attack_party": return "RegularAttackParty";
                case "attack_player_party": return "RegularAttackPlayerParty";
                case "surrender_to_player": return "RegularSurrenderToPlayer";
                case "leave_player_alone": return "RegularLeavePlayerAlone";
                case "kill_character": return "RegularKillCharacter";
                case "duel_player": return "RegularDuelPlayer";
                case "prepare_arrest": return "RegularPrepareArrest";
                case "confirm_arrest": return "RegularConfirmArrest";
                case "release_arrested_character": return "RegularReleaseArrestedCharacter";
                case "rescind_arrest_accusation": return "RegularRescindArrestAccusation";
                case "player_attack_party": return "RegularPlayerAttackParty";
                case "issue_campaign_order": return "RegularIssueCampaignOrder";
                case "revise_campaign_order": return "RegularReviseCampaignOrder";
                case "respond_to_order_report": return "RegularRespondToOrderReport";
                case "cancel_campaign_order": return "RegularCancelCampaignOrder";
                case "accept_temporary_party_guest": return "RegularAcceptTemporaryPartyGuest";
                case "recruit_encountered_resident": return "RegularRecruitEncounteredResident";
                case "release_resident_from_duty": return "RegularReleaseResidentFromDuty";
                case "renew_temporary_party_guest": return "RegularRenewTemporaryPartyGuest";
                case "end_temporary_party_guest": return "RegularEndTemporaryPartyGuest";
                case "acknowledge_own_faction_combat_risk": return "RegularAcknowledgeOwnFactionCombatRisk";
                case "give_gold_to_player": return "RegularGiveGoldToPlayer";
                case "transfer_gold": return "RegularTransferGold";
                case "transfer_item": return "RegularTransferItem";
                case "transfer_workshop": return "RegularTransferWorkshop";
                case "transfer_prisoner": return "RegularTransferPrisoner";
                case "hire_player_as_mercenary": return "RegularHirePlayerAsMercenary";
                case "dismiss_player_mercenary": return "RegularDismissPlayerMercenary";
                case "offer_player_vassalage": return "RegularOfferPlayerVassalage";
                case "dismiss_player_vassal": return "RegularDismissPlayerVassal";
                case "join_clan": return "RegularJoinClan";
                case "leave_clan": return "RegularLeaveClan";
                case "join_kingdom": return "RegularJoinKingdom";
                case "leave_kingdom": return "RegularLeaveKingdom";
                case "hire_mercenary_clan": return "RegularHireMercenaryClan";
                default: return "";
            }
        }

        private static string ReadSupporterClanIds(Dictionary<string, object> raw, Dictionary<string, object> terms)
        {
            List<string> ids = ReadStringList(raw, "supporterClanIds");
            ids.AddRange(ReadStringList(terms, "supporterClanIds"));
            return string.Join(",", ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        }

        private static string FirstStringFromTerms(Dictionary<string, object> terms, string key)
        {
            return ReadStringList(terms, key).FirstOrDefault() ?? string.Empty;
        }

        private static void Require(Dictionary<string, object> record, string key, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(ReadString(record, key, "")))
            {
                errors.Add(key + " is required.");
            }
        }

        internal static void CopyOptional(Dictionary<string, object> source, Dictionary<string, object> target, string key)
        {
            if (source != null && source.TryGetValue(key, out object value) && value != null)
            {
                target[key] = value;
            }
        }
    }
}
