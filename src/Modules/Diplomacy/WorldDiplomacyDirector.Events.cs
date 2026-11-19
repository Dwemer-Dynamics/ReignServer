using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Diplomacy;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private static Dictionary<string, object> BuildDiplomaticEvent(string campaignId, double worldDay, Dictionary<string, object> actor, Dictionary<string, object> target, Dictionary<string, object> candidate, bool accepted, string outcome, string targetReason, string actionId)
        {
            string command = ReadString(candidate, "command", "");
            string actorName = ReadString(actor, "leaderName", "An unnamed ruler");
            string targetName = ReadString(target, "leaderName", "another ruler");
            string actorKingdom = ReadString(actor, "name", "an unnamed kingdom");
            string targetKingdom = ReadString(target, "name", "another kingdom");
            string summary = BuildDirectorEventSummary(command, actorName, actorKingdom, targetName, targetKingdom, accepted, outcome);
            return new Dictionary<string, object>
            {
                ["eventId"] = Guid.NewGuid().ToString("N"),
                ["campaignId"] = campaignId,
                ["timelineId"] = ReadString(candidate, "timelineId", "main"),
                ["worldDay"] = worldDay,
                ["createdTs"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["actionId"] = actionId,
                ["command"] = command,
                ["accepted"] = accepted,
                ["outcome"] = HumanizeDirectorOutcome(outcome),
                ["title"] = IsDirectorAllianceProposal(candidate) ? (accepted ? "Alliance Package" : "Alliance Package Refused") : DirectorEventTitle(command, accepted),
                ["summary"] = summary,
                ["terms"] = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>(),
                ["termsText"] = BuildDirectorTermsText(ReadDictionary(candidate, "terms")),
                ["actorHeroId"] = ReadString(actor, "leaderHeroId", ""),
                ["actorName"] = actorName,
                ["actorKingdomId"] = ReadString(actor, "kingdomId", ""),
                ["actorKingdomName"] = actorKingdom,
                ["actorPublicReason"] = ReadString(candidate, "publicReason", ""),
                ["targetHeroId"] = ReadString(target, "leaderHeroId", ""),
                ["targetName"] = targetName,
                ["targetKingdomId"] = ReadString(target, "kingdomId", ""),
                ["targetKingdomName"] = targetKingdom,
                ["targetPublicReason"] = targetReason,
                ["privateDecisionFactors"] = candidate.ContainsKey("decisionFactors") ? candidate["decisionFactors"] : new ArrayList(),
                ["initiativeDiagnostics"] = candidate.ContainsKey("initiativeDiagnostics") ? candidate["initiativeDiagnostics"] : new Dictionary<string, object>(),
                ["relationshipOpportunityId"] = ReadString(candidate,
                    "relationshipOpportunityId", ""),
                ["relationshipPolarity"] = ReadString(candidate,
                    "requiredPolarity", ""),
                ["politicalPressureId"] = ReadString(candidate,
                    "politicalPressureId", ""),
                ["politicalPressureValue"] = ReadInt(candidate,
                    "politicalPressureValue", 0),
                ["politicalPressureChannel"] = ReadString(candidate,
                    "politicalPressureChannel", ""),
                ["politicalPressurePolarity"] = ReadString(candidate,
                    "politicalPressureId", "").Length > 0
                        ? ReadString(candidate, "requiredPolarity", "")
                        : "",
                ["correlationId"] = ReadString(candidate, "correlationId", ""),
                ["consequenceTags"] = DirectorConsequenceTags(command, accepted, ReadDictionary(candidate, "terms")),
                ["executionStatus"] = accepted ? "pending" : "not_required",
                ["announcementReady"] = !accepted,
                ["delivered"] = false,
                ["acknowledged"] = false
            };
        }

        internal static void QueueDiplomaticEvent(string campaignId, Dictionary<string, object> diplomaticEvent)
        {
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                queue.Add(diplomaticEvent);
                if (queue.Count > 500) queue = queue.Skip(queue.Count - 500).ToList();
                WriteDiplomaticEventQueue(campaignId, queue);
                Program.AppendJsonLineToPath(Program.CampaignFile(campaignId, "diplomacy", "events.jsonl"), diplomaticEvent);
                if (!ReadBool(diplomaticEvent, "accepted", false)) AppendDiplomaticConsequence(campaignId, diplomaticEvent);
            }
            try
            {
                List<string> participants = new[]
                {
                    ReadString(diplomaticEvent, "actorHeroId", ""),
                    ReadString(diplomaticEvent, "targetHeroId", "")
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                    ["eventType"] = "world_diplomacy",
                    ["eventSubtype"] = ReadFirstString(diplomaticEvent, "command", "actionType", "intent"),
                    ["worldDay"] = ReadDouble(diplomaticEvent, "worldDay", 0d),
                    ["summary"] = ReadString(diplomaticEvent, "summary", ""),
                    ["participants"] = participants,
                    ["aboutEntityIds"] = participants,
                    ["visibility"] = "public",
                    ["importance"] = ReadBool(diplomaticEvent, "accepted", false) ? 0.9d : 0.7d,
                    ["source"] = "world_diplomacy_director"
                }, "world_diplomacy_director");
            }
            catch (Exception ex)
            {
                Program.LogOperational("world_diplomacy.public_history_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                    ["error"] = ex.Message
                });
            }
        }

        internal static Dictionary<string, object> NextDiplomaticEvents(Dictionary<string, object> payload, Dictionary<string, string> query)
        {
            DiplomaticEventsNextRequest request = DiplomaticEventsNextRequest.FromQuery(query, payload);
            string campaignId = request.CampaignId;
            bool publicHistory = request.PublicHistory;
            int limit = request.Limit;
            if (publicHistory)
            {
                List<Dictionary<string, object>> published;
                lock (Program.FileLock) published = PublicDiplomaticAnnouncements(ReadDiplomaticEventQueue(campaignId, requireValid: true), limit);
                return DiplomaticEventsNextResponse.PublicHistoryProjection(campaignId, published).ToDictionary();
            }
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                foreach (Dictionary<string, object> item in queue.Where(x => ReadBool(x, "announcementReady", false) && !ReadBool(x, "acknowledged", false)).Take(limit))
                {
                    item["lastFetchedTs"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    selected.Add(new Dictionary<string, object>(item, StringComparer.OrdinalIgnoreCase));
                }
                WriteDiplomaticEventQueue(campaignId, queue);
            }
            foreach (Dictionary<string, object> item in selected)
            {
                item.Remove("privateDecisionFactors");
                item.Remove("initiativeDiagnostics");
            }
            return DiplomaticEventsNextResponse.Popups(campaignId, selected).ToDictionary();
        }

        private static List<Dictionary<string, object>> PublicDiplomaticAnnouncements(List<Dictionary<string, object>> queue, int limit)
        {
            string[] fields = { "eventId", "actionId", "title", "outcome", "summary", "termsText", "worldDay", "accepted", "acknowledged",
                "actorHeroId", "actorName", "actorKingdomId", "actorKingdomName", "actorPublicReason",
                "targetHeroId", "targetName", "targetKingdomId", "targetKingdomName", "targetPublicReason" };
            // This dedicated director queue already publishes public world-memory
            // events. Only ready announcements qualify; pending proposals do not.
            return queue.Where(item => ReadBool(item, "announcementReady", false)
                    && ReadString(item, "visibility", "public") == "public")
                .Take(Math.Max(1, Math.Min(500, limit))).Select(item => {
                    var result = fields.Where(item.ContainsKey).ToDictionary(key => key, key => item[key]);
                    result["publicAnnouncement"] = true;
                    return result;
                }).ToList();
        }

        internal static Dictionary<string, object> AcknowledgeDiplomaticEvent(Dictionary<string, object> payload)
        {
            DiplomaticEventAcknowledgeRequest request = DiplomaticEventAcknowledgeRequest.FromDictionary(payload);
            string campaignId = request.CampaignId;
            string eventId = request.EventId;
            bool found = false;
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                Dictionary<string, object> item = queue.FirstOrDefault(x => string.Equals(ReadString(x, "eventId", ""), eventId, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    MarkDiplomaticEventAcknowledged(item,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    found = true;
                }
                WriteDiplomaticEventQueue(campaignId, queue);
            }
            return new DiplomaticEventAcknowledgeResponse(found, eventId).ToDictionary();
        }

        private static void MarkDiplomaticEventAcknowledged(
            Dictionary<string, object> item, long acknowledgedTs)
        {
            if (item == null) return;
            item["acknowledged"] = true;
            item["acknowledgedTs"] = acknowledgedTs;
            // An acknowledgement is the strongest available evidence that the
            // game client fetched and surfaced the announcement. Keep the two
            // states separate for in-flight popups, but do not leave an
            // acknowledged event permanently "undelivered".
            item["delivered"] = true;
            item["deliveredTs"] = acknowledgedTs;
        }

        internal static void UpdateDiplomaticEventForAction(string campaignId, string actionId, string status, Dictionary<string, object> report)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return;
            List<Dictionary<string, object>> pendingRelationshipFeedback =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> pendingRulerActivity =
                new List<Dictionary<string, object>>();
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                bool changed = false;
                foreach (Dictionary<string, object> item in queue.Where(x => string.Equals(ReadString(x, "actionId", ""), actionId, StringComparison.OrdinalIgnoreCase)))
                {
                    string currentStatus = ReadString(item, "executionStatus", "");
                    bool legacyRefusalCorrection = currentStatus == "failed" && status == "rejected"
                        && ReadString(report, "statusCorrection", "") == "legacy_government_refusal"
                        && Program.IsRecordedGovernmentRefusalResult(ReadDictionary(report, "result")
                            ?? new Dictionary<string, object>());
                    if (!Program.ShouldAcceptActionStatusTransition(currentStatus, status) && !legacyRefusalCorrection)
                        continue;
                    item["executionStatus"] = status;
                    if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                    {
                        item["announcementReady"] = true;
                        if (!ReadBool(item, "consequenceRecorded", false))
                        {
                            AppendDiplomaticConsequence(campaignId, item);
                            item["consequenceRecorded"] = true;
                        }
                        if (!ReadBool(item, "relationshipFeedbackRecorded", false))
                        {
                            // Relationship feedback takes the per-campaign relationship
                            // writer lock. Never take it while FileLock is held: the
                            // continuous relationship worker legitimately takes those
                            // locks in the opposite direction when materializing profile
                            // data, which otherwise deadlocks every server request.
                            pendingRelationshipFeedback.Add(
                                new Dictionary<string, object>(item,
                                    StringComparer.OrdinalIgnoreCase));
                        }
                        // Activity persistence ensures database schema and takes the
                        // PostgreSQL mutation lock. Defer it with relationship feedback
                        // so FileLock never encloses database or relationship work.
                        pendingRulerActivity.Add(
                            new Dictionary<string, object>(item,
                                StringComparer.OrdinalIgnoreCase));
                    }
                    else if (status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                        && ReadString(report, "statusNormalization", "")
                            .Equals("resolved_npc_government_refusal",
                                StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> result =
                            ReadDictionary(report, "result")
                                ?? new Dictionary<string, object>();
                        string resultCode = ReadFirstString(result,
                            "resultCode", "ResultCode");
                        string message = FirstNonEmpty(
                            ReadString(report, "message", ""),
                            ReadFirstString(result, "debugMessage", "DebugMessage",
                                "message", "Message"),
                            "The ruler yielded to the realm's government and abandoned the proposed action.");
                        string command = ReadString(item, "command", "");
                        item["accepted"] = false;
                        item["outcome"] = HumanizeDirectorOutcome(resultCode);
                        item["title"] = DirectorEventTitle(command, false);
                        item["summary"] = message;
                        item["governmentResolution"] = resultCode;
                        item["governmentResolutionMessage"] = message;
                        item["consequenceTags"] = DirectorConsequenceTags(command,
                            false, ReadDictionary(item, "terms"));
                        item["announcementReady"] = true;
                        item["executionError"] = "";
                    }
                    else if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("blocked", StringComparison.OrdinalIgnoreCase) || status.Equals("expired", StringComparison.OrdinalIgnoreCase))
                    {
                        item["announcementReady"] = false;
                        item["executionError"] = ReadString(report, "message", "Diplomatic action failed in the game client.");
                    }
                    changed = true;
                }
                if (changed) WriteDiplomaticEventQueue(campaignId, queue);
            }

            foreach (Dictionary<string, object> item in pendingRulerActivity)
                RecordRulerDiplomacyEventActivity(campaignId, item,
                    "native_receipt", "completed");

            foreach (Dictionary<string, object> item in pendingRelationshipFeedback)
            {
                ApplyDiplomaticRelationshipFeedback(campaignId, item, actionId);
                MarkDiplomaticRelationshipFeedbackRecorded(campaignId, actionId,
                    ReadString(item, "eventId", ""));
            }
        }

        private static void MarkDiplomaticRelationshipFeedbackRecorded(string campaignId,
            string actionId, string eventId)
        {
            lock (Program.FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                Dictionary<string, object> item = queue.FirstOrDefault(candidate =>
                    string.Equals(ReadString(candidate, "actionId", ""), actionId,
                        StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(eventId)
                        || string.Equals(ReadString(candidate, "eventId", ""), eventId,
                            StringComparison.OrdinalIgnoreCase)));
                if (item == null || ReadBool(item, "relationshipFeedbackRecorded", false))
                    return;
                item["relationshipFeedbackRecorded"] = true;
                WriteDiplomaticEventQueue(campaignId, queue);
            }
        }

        private static void ApplyDirectorCooldowns(Dictionary<string, object> state, Dictionary<string, object> rulerState, Dictionary<string, object> candidate, bool accepted, double worldDay)
        {
            rulerState["lastInitiativeDay"] = worldDay;
            rulerState["cooldownUntilDay"] = worldDay + 15d;
            string targetId = ReadFirstString(candidate, "targetKingdomId", "targetKingdomStringId");
            Dictionary<string, object> pairCooldowns = ReadDictionary(rulerState, "pairCooldowns") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            rulerState["pairCooldowns"] = pairCooldowns;
            if (!accepted) pairCooldowns[targetId] = worldDay + 15d;
            if (accepted && ReadString(candidate, "command", "").Equals("declare_war", StringComparison.OrdinalIgnoreCase)) pairCooldowns[targetId] = worldDay + 30d;
            Dictionary<string, object> rulers = ReadDictionary(state, "rulers") ?? new Dictionary<string, object>();
            if (rulers.TryGetValue(targetId, out object targetValue) && targetValue is Dictionary<string, object> targetState)
            {
                Dictionary<string, object> reverseCooldowns = ReadDictionary(targetState, "pairCooldowns") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                targetState["pairCooldowns"] = reverseCooldowns;
                string actorId = ReadFirstString(candidate, "actorKingdomId", "actorKingdomStringId");
                if (!accepted) reverseCooldowns[actorId] = worldDay + 15d;
                if (accepted && ReadString(candidate, "command", "").Equals("declare_war", StringComparison.OrdinalIgnoreCase)) reverseCooldowns[actorId] = worldDay + 30d;
            }
        }

        private static Dictionary<string, object> GetDirectorRulerState(Dictionary<string, object> state, Dictionary<string, object> ruler, double worldDay)
        {
            Dictionary<string, object> rulers = ReadDictionary(state, "rulers") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            state["rulers"] = rulers;
            string kingdomId = ReadString(ruler, "kingdomId", "");
            string heroId = ReadString(ruler, "leaderHeroId", "");
            if (!(rulers.TryGetValue(kingdomId, out object value) && value is Dictionary<string, object> record))
            {
                record = NewDirectorRulerState(heroId, worldDay);
                rulers[kingdomId] = record;
            }
            else if (!string.Equals(ReadString(record, "rulerHeroId", ""), heroId, StringComparison.OrdinalIgnoreCase))
            {
                record = NewDirectorRulerState(heroId, worldDay);
                rulers[kingdomId] = record;
            }
            string fingerprint = ReadDouble(ruler, "strength", 0d).ToString("0", CultureInfo.InvariantCulture)
                + "|" + ReadInt(ruler, "treasury", 0)
                + "|" + ReadInt(ruler, "fiefCount", 0)
                + "|" + string.Join(",", ReadStringList(ruler, "enemies").OrderBy(x => x))
                + "|" + ReadBool(ruler, "rulerIsPrisoner", false);
            string previousFingerprint = ReadString(record, "strategicFingerprint", "");
            if (!string.IsNullOrWhiteSpace(previousFingerprint) && !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal)) record["agendaNeedsRefresh"] = true;
            record["strategicFingerprint"] = fingerprint;
            return record;
        }

        private static Dictionary<string, object> NewDirectorRulerState(string heroId, double worldDay)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["rulerHeroId"] = heroId,
                ["reignStartedDay"] = worldDay,
                ["agendaUpdatedDay"] = -1000d,
                ["agenda"] = new Dictionary<string, object>(),
                ["pairCooldowns"] = new Dictionary<string, object>()
            };
        }

        private static void UpdateDirectorAgenda(Dictionary<string, object> rulerState, Dictionary<string, object> decision, double worldDay)
        {
            Dictionary<string, object> agenda = ReadDictionary(decision, "agenda");
            if (agenda != null && agenda.Count > 0)
            {
                rulerState["agenda"] = agenda;
                rulerState["agendaUpdatedDay"] = worldDay;
                rulerState["agendaNeedsRefresh"] = false;
            }
        }

        internal static Dictionary<string, object> LoadDirectorTraits(string campaignId, Dictionary<string, object> ruler)
        {
            string heroId = ReadString(ruler, "leaderHeroId", "");
            Dictionary<string, object> document = string.IsNullOrWhiteSpace(heroId) ? new Dictionary<string, object>() : Program.ReadJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"));
            if (document.Count > 0 && CharactersService.EnsureTraitPercentageData(document, heroId))
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "traits.json"), document);
            Dictionary<string, object> persisted = ReadDictionary(document, "courtVirtues");
            if (CharactersService.TraitPercentageDocumentReady(document) && HasCompleteDirectorVirtueProfile(persisted)) return NormalizeDirectorVirtues(persisted);

            if (!string.IsNullOrWhiteSpace(heroId)
                && CharactersService.LoadCharacterProfileLibrary().TryGetValue(heroId, out Dictionary<string, object> shipped))
            {
                Dictionary<string, object> shippedTraits = ReadDictionary(shipped, "traits") ?? new Dictionary<string, object>();
                Dictionary<string, object> shippedVirtues = ReadDictionary(shippedTraits, "courtVirtues");
                if (CharactersService.TraitPercentageDocumentReady(shippedTraits) && HasCompleteDirectorVirtueProfile(shippedVirtues)) return NormalizeDirectorVirtues(shippedVirtues);
            }

            return NeutralDirectorVirtues();
        }

        private static bool HasCompleteDirectorVirtueProfile(Dictionary<string, object> virtues)
        {
            return virtues != null && DirectorCourtVirtueKeys.All(virtues.ContainsKey);
        }

        private static Dictionary<string, object> NormalizeDirectorVirtues(Dictionary<string, object> virtues)
        {
            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in DirectorCourtVirtueKeys) normalized[key] = DirectorVirtue(virtues, key);
            return normalized;
        }

        private static Dictionary<string, object> NeutralDirectorVirtues()
        {
            return DirectorCourtVirtueKeys.ToDictionary(x => x, x => (object)50d, StringComparer.OrdinalIgnoreCase);
        }
    }
}
