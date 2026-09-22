using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly object CampaignCommandDraftLock = new object();
        private const double CampaignCommandDraftLifetimeDays = 1d;

        private static List<Dictionary<string, object>> ApplyCampaignCommandDialogueDraftGate(
            string campaignId,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            List<Dictionary<string, object>> candidates)
        {
            candidates = candidates ?? new List<Dictionary<string, object>>();
            string commanderId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadFirstString(hero, "heroStringId", "stringId", "id"));
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(commanderId))
                return candidates;

            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> passthrough = candidates.Where(x =>
                !string.Equals(CanonicalCommand(ReadString(x, "command", "")),
                    "issue_campaign_order", StringComparison.OrdinalIgnoreCase)).ToList();
            Dictionary<string, object> incoming = candidates.FirstOrDefault(x =>
                string.Equals(CanonicalCommand(ReadString(x, "command", "")),
                    "issue_campaign_order", StringComparison.OrdinalIgnoreCase));

            lock (CampaignCommandDraftLock)
            {
                Dictionary<string, object> state = ReadCampaignCommandDraftState(campaignId);
                List<Dictionary<string, object>> drafts = ReadDictionaryList(state, "drafts");
                drafts.RemoveAll(x => DraftExpired(x, worldDay));
                Dictionary<string, object> draft = drafts.LastOrDefault(x =>
                    string.Equals(ReadString(x, "commanderHeroStringId", ""), commanderId,
                        StringComparison.OrdinalIgnoreCase));

                if (draft != null && IsCampaignCommandDraftCancellation(playerText))
                {
                    drafts.Remove(draft);
                    WriteCampaignCommandDraftState(campaignId, state, drafts);
                    payload["campaignCommandDraftReply"] = "The proposed campaign order has been discarded. No action was queued.";
                    return passthrough;
                }

                string phase = ReadString(draft, "phase", "");
                if (draft != null && incoming == null && IsUnqualifiedCampaignCommandConfirmation(playerText))
                {
                    if (string.Equals(phase, "awaiting_first_confirmation", StringComparison.OrdinalIgnoreCase))
                    {
                        draft["phase"] = "awaiting_final_confirmation";
                        draft["updatedWorldDay"] = worldDay;
                        draft["updatedUtc"] = DateTime.UtcNow.ToString("o");
                        WriteCampaignCommandDraftState(campaignId, state, drafts);
                        payload["campaignCommandDraftReply"] = BuildCampaignCommandReadback(draft, true);
                        return passthrough;
                    }
                    if (string.Equals(phase, "awaiting_final_confirmation", StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> accepted = CampaignCommandCloneDictionary(
                            ReadDictionary(draft, "candidate") ?? new Dictionary<string, object>());
                        Dictionary<string, object> terms = ReadDictionary(accepted, "terms")
                            ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        terms["draftId"] = ReadString(draft, "draftId", "");
                        terms["planHash"] = ReadString(draft, "planHash", "");
                        terms["confirmationPhase"] = "final";
                        accepted["terms"] = terms;
                        accepted["requiresAcceptance"] = false;
                        accepted["accepted"] = true;
                        accepted["source"] = "dialogue_campaign_command_confirmed";
                        accepted["reason"] = "The commander repeated the immutable plan and the player gave final execution confirmation.";
                        drafts.Remove(draft);
                        WriteCampaignCommandDraftState(campaignId, state, drafts);
                        payload["campaignCommandDraftReply"] = "The order is confirmed. I will begin the agreed sequence now and report any material obstruction.";
                        passthrough.Add(accepted);
                        return passthrough;
                    }
                }

                if (incoming == null)
                {
                    if (draft != null)
                    {
                        payload["campaignCommandDraftReply"] = string.Equals(phase, "collecting", StringComparison.OrdinalIgnoreCase)
                            ? BuildCampaignCommandClarification(draft)
                            : BuildCampaignCommandReadback(draft,
                                string.Equals(phase, "awaiting_final_confirmation", StringComparison.OrdinalIgnoreCase));
                    }
                    WriteCampaignCommandDraftState(campaignId, state, drafts);
                    return passthrough;
                }

                Dictionary<string, object> normalized = NormalizeCampaignCommandDraftCandidate(
                    incoming, commanderId, playerText);
                List<string> missing = CampaignCommandMissingDetails(normalized, playerText);
                if (draft != null) drafts.Remove(draft);
                draft = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draftId"] = Guid.NewGuid().ToString("N"),
                    ["commanderHeroStringId"] = commanderId,
                    ["candidate"] = normalized,
                    ["missing"] = missing,
                    ["phase"] = missing.Count == 0 ? "awaiting_first_confirmation" : "collecting",
                    ["planHash"] = CampaignCommandPlanHash(normalized),
                    ["createdWorldDay"] = worldDay,
                    ["updatedWorldDay"] = worldDay,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                };
                drafts.Add(draft);
                WriteCampaignCommandDraftState(campaignId, state, drafts);
                payload["campaignCommandDraftReply"] = missing.Count == 0
                    ? BuildCampaignCommandReadback(draft, false)
                    : BuildCampaignCommandClarification(draft);
                return passthrough;
            }
        }

        private static bool ShouldRouteCampaignCommandDraft(string campaignId,
            Dictionary<string, object> payload, Dictionary<string, object> hero, string playerText)
        {
            if (SpeakerInPlayerParty(payload, hero)) return false;
            if (HasDirectCampaignOrderRequest(playerText)) return true;
            string commanderId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadFirstString(hero, "heroStringId", "stringId", "id"));
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(commanderId)) return false;
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            lock (CampaignCommandDraftLock)
            {
                Dictionary<string, object> state = ReadCampaignCommandDraftState(campaignId);
                return ReadDictionaryList(state, "drafts").Any(x => !DraftExpired(x, worldDay)
                    && string.Equals(ReadString(x, "commanderHeroStringId", ""), commanderId,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        private static string BuildCampaignCommandDraftRoutingText(string campaignId,
            Dictionary<string, object> payload, Dictionary<string, object> hero, string playerText)
        {
            string commanderId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadFirstString(hero, "heroStringId", "stringId", "id"));
            lock (CampaignCommandDraftLock)
            {
                Dictionary<string, object> state = ReadCampaignCommandDraftState(campaignId);
                Dictionary<string, object> draft = ReadDictionaryList(state, "drafts").LastOrDefault(x =>
                    string.Equals(ReadString(x, "commanderHeroStringId", ""), commanderId,
                        StringComparison.OrdinalIgnoreCase));
                if (draft == null) return playerText ?? string.Empty;
                return "ACTIVE CAMPAIGN ORDER DRAFT (hidden structured context):\n"
                    + Json.Serialize(ReadDictionary(draft, "candidate") ?? new Dictionary<string, object>())
                    + "\nPLAYER'S LATEST CLARIFICATION OR AMENDMENT:\n" + (playerText ?? string.Empty)
                    + "\nReturn the complete amended issue_campaign_order candidate, preserving unchanged steps."
                    + " Do not execute it; the deterministic draft gate controls confirmation.";
            }
        }

        private static Dictionary<string, object> NormalizeCampaignCommandDraftCandidate(
            Dictionary<string, object> source, string commanderId, string playerText)
        {
            Dictionary<string, object> result = CampaignCommandCloneDictionary(source);
            result["command"] = "issue_campaign_order";
            result["actorHeroStringId"] = commanderId;
            result["requiresAcceptance"] = true;
            result["source"] = "dialogue_campaign_command_draft";
            Dictionary<string, object> terms = ReadDictionary(result, "terms")
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!terms.ContainsKey("steps"))
            {
                string objective = ReadString(terms, "objective", "");
                if (!string.IsNullOrWhiteSpace(objective))
                {
                    Dictionary<string, object> step = new Dictionary<string, object>(terms,
                        StringComparer.OrdinalIgnoreCase) { ["objective"] = objective };
                    string settlement = ReadFirstString(result, "targetSettlementStringId", "targetSettlementId");
                    if (!string.IsNullOrWhiteSpace(settlement)) step["targetSettlementStringId"] = settlement;
                    terms["steps"] = new List<Dictionary<string, object>> { step };
                }
            }
            string lower = (playerText ?? string.Empty).ToLowerInvariant();
            terms["forceKindExplicit"] = ContainsAnyNormalized(lower, "army", "call the lords",
                "bring the lords", "summon the lords", "gather the clans", "gather parties",
                "multiple commanders", "party", "warband", "company", "personal force",
                "your own men", "your men", "your own troops", "your troops", "your own band");
            result["terms"] = terms;
            return result;
        }

        private static List<string> CampaignCommandMissingDetails(Dictionary<string, object> candidate,
            string playerText)
        {
            List<string> missing = new List<string>();
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> steps = ReadDictionaryList(terms, "steps");
            if (steps.Count == 0) missing.Add("the ordered sequence of actions");
            string lower = (playerText ?? string.Empty).ToLowerInvariant();
            if (ContainsAnyNormalized(lower, "raise forces", "gather forces", "recruit forces",
                    "raise men", "get some men together", "get your men together",
                    "build up your numbers", "bring in more troops")
                && !ReadBool(terms, "forceKindExplicit", false))
                missing.Add("whether you mean the commander's personal party or a multi-party army");
            foreach (Dictionary<string, object> step in steps)
            {
                string objective = CanonicalCampaignCommandObjective(ReadString(step, "objective", ""));
                if (string.IsNullOrWhiteSpace(objective))
                {
                    missing.Add("an objective for every plan step");
                    continue;
                }
                if (string.Equals(objective, "recruit_resupply", StringComparison.OrdinalIgnoreCase))
                {
                    int total = ReadInt(step, "minimumTroops", 0);
                    int roles = ReadInt(step, "minimumInfantry", 0) + ReadInt(step, "minimumArchers", 0)
                        + ReadInt(step, "minimumCavalry", 0);
                    if (total <= 0 && roles <= 0)
                        missing.Add("a recruitment goal, such as a total party size or infantry/archer/cavalry minimums");
                }
                if (CampaignCommandObjectiveNeedsSettlement(objective)
                    && string.IsNullOrWhiteSpace(ReadFirstString(step, "targetSettlementStringId", "targetSettlementId"))
                    && string.IsNullOrWhiteSpace(ReadString(step, "region", "")))
                    missing.Add("a settlement or region for " + objective.Replace('_', ' '));
                if (CampaignCommandObjectiveNeedsParty(objective)
                    && string.IsNullOrWhiteSpace(ReadFirstString(step, "targetPartyId", "targetHeroStringId", "targetHeroId")))
                    missing.Add("the party or commander targeted by " + objective.Replace('_', ' '));
                if ((objective == "timed_hold" || objective == "scout_report")
                    && ReadDouble(step, "durationHours", 0d) <= 0d)
                    missing.Add("a duration for " + objective.Replace('_', ' '));
            }
            return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool CampaignCommandObjectiveNeedsSettlement(string objective)
        {
            return new[] { "move", "patrol", "scout_report", "form_army", "raid", "besiege_capture",
                "defend", "relieve_siege" }.Contains(objective, StringComparer.OrdinalIgnoreCase);
        }

        private static bool CampaignCommandObjectiveNeedsParty(string objective)
        {
            return new[] { "escort", "join_army", "engage_party" }.Contains(objective,
                StringComparer.OrdinalIgnoreCase);
        }

        private static string CanonicalCampaignCommandObjective(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            switch (normalized)
            {
                case "recruit":
                case "resupply": return "recruit_resupply";
                case "siege":
                case "besiege":
                case "capture": return "besiege_capture";
                case "hunt": return "hunt_enemy_parties";
                default: return normalized;
            }
        }

        private static string BuildCampaignCommandClarification(Dictionary<string, object> draft)
        {
            List<string> missing = ReadStringList(draft, "missing");
            return "I understand the broad intent, but I still need "
                + string.Join("; ", missing.DefaultIfEmpty("the remaining details"))
                + ". Once you clarify that, I will repeat the whole order back to you before I act.";
        }

        private static string BuildCampaignCommandReadback(Dictionary<string, object> draft, bool final)
        {
            Dictionary<string, object> candidate = ReadDictionary(draft, "candidate")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> steps = ReadDictionaryList(terms, "steps");
            List<string> lines = new List<string>();
            for (int i = 0; i < steps.Count; i++)
            {
                Dictionary<string, object> step = steps[i];
                string objective = CanonicalCampaignCommandObjective(ReadString(step, "objective", ""));
                List<string> details = new List<string>();
                string settlement = FirstNonEmpty(
                    ReadFirstString(step, "targetSettlementName", "settlementName", "targetName"),
                    CampaignCommandVisibleReference(ReadFirstString(step,
                        "targetSettlementStringId", "targetSettlementId"), "the selected settlement"));
                string region = ReadString(step, "region", "");
                string party = FirstNonEmpty(
                    ReadFirstString(step, "targetPartyName", "targetHeroName", "commanderName"),
                    CampaignCommandVisibleReference(ReadFirstString(step,
                        "targetPartyId", "targetHeroStringId", "targetHeroId"), "the selected party"));
                if (!string.IsNullOrWhiteSpace(settlement)) details.Add("at " + settlement);
                else if (!string.IsNullOrWhiteSpace(region)) details.Add("across " + region.Replace('_', ' '));
                if (!string.IsNullOrWhiteSpace(party)) details.Add("with " + party);
                int total = ReadInt(step, "minimumTroops", 0);
                int infantry = ReadInt(step, "minimumInfantry", 0);
                int archers = ReadInt(step, "minimumArchers", 0);
                int cavalry = ReadInt(step, "minimumCavalry", 0);
                if (total > 0) details.Add("at least " + total + " regular troops");
                if (infantry + archers + cavalry > 0)
                    details.Add(infantry + " infantry, " + archers + " archers, " + cavalry + " cavalry");
                double duration = ReadDouble(step, "durationHours", 0d);
                if (duration > 0d) details.Add(duration.ToString("0.##", CultureInfo.InvariantCulture) + " campaign hours");
                string transition = i == 0 ? "First" : i == steps.Count - 1 ? "Finally" : "Then";
                lines.Add(transition + ", " + CampaignCommandObjectiveReadback(objective)
                    + (details.Count == 0 ? "" : " " + string.Join(", ", details)) + ".");
            }
            string heading = final ? "One last time, this is the order I will carry out:" : "Let me make sure I have this right:";
            string instruction = final
                ? "If that is still right, tell me to go ahead. If you change anything, I will repeat the revised order before acting."
                : "If that is right, say so. I will repeat it once more before I begin.";
            return heading + "\n" + string.Join("\n", lines) + "\n" + instruction;
        }

        private static string CampaignCommandVisibleReference(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.Trim();
            if (normalized.IndexOf('_') >= 0 || normalized.StartsWith("town", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("castle", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("village", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("_party", StringComparison.OrdinalIgnoreCase))
                return fallback;
            return normalized;
        }

        private static string CampaignCommandObjectiveReadback(string objective)
        {
            switch (objective)
            {
                case "establish_party": return "raise your own party";
                case "move": return "take your party";
                case "hold_position": return "hold your present position";
                case "timed_hold": return "wait with your party";
                case "patrol": return "patrol with your party";
                case "scout_report": return "scout the area and report back";
                case "escort": return "escort the other party";
                case "recruit_resupply": return "recruit and provision your party";
                case "form_army": return "call an army together and let it assemble";
                case "join_army": return "join the army";
                case "leave_army": return "leave the army with its commander's agreement";
                case "disband_army": return "disband the army";
                case "raid": return "raid the village";
                case "besiege_capture": return "besiege and capture the stronghold";
                case "defend": return "defend the area";
                case "relieve_siege": return "try to break the siege";
                case "hunt_enemy_parties": return "find and engage enemy parties";
                case "engage_party": return "engage the selected enemy party";
                case "withdraw": return "fall back to safety";
                case "return_home": return "return home";
                default: return "carry out the next part of the order";
            }
        }

        private static bool IsUnqualifiedCampaignCommandConfirmation(string playerText)
        {
            string normalized = NormalizeCampaignCommandControlPhrase(playerText);
            return new[] { "yes", "yes thats right", "yes thats correct", "yes go", "yes go ahead",
                "thats right", "that is right", "sounds right", "correct", "confirmed", "exactly",
                "go", "go ahead", "alright go", "alright go ahead", "okay go", "okay go ahead",
                "proceed", "do it", "do it then", "execute", "execute it", "make it so", "agreed" }
                .Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsCampaignCommandDraftCancellation(string playerText)
        {
            string normalized = NormalizeCampaignCommandControlPhrase(playerText);
            return new[] { "cancel", "cancel it", "never mind", "nevermind", "forget it",
                "scratch that", "drop it", "discard the order", "dont do that" }
                .Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeCampaignCommandControlPhrase(string playerText)
        {
            string source = (playerText ?? string.Empty).Trim().ToLowerInvariant()
                .Replace('’', '\'').Replace('‘', '\'');
            StringBuilder result = new StringBuilder(source.Length);
            bool pendingSpace = false;
            foreach (char ch in source)
            {
                if (char.IsLetterOrDigit(ch) || ch == '\'')
                {
                    if (pendingSpace && result.Length > 0) result.Append(' ');
                    result.Append(ch);
                    pendingSpace = false;
                }
                else pendingSpace = true;
            }
            return result.ToString().Replace("'", string.Empty).Trim();
        }

        private static bool DraftExpired(Dictionary<string, object> draft, double worldDay)
        {
            double updated = ReadDouble(draft, "updatedWorldDay", 0d);
            if (worldDay > 0d && updated > 0d) return worldDay - updated > CampaignCommandDraftLifetimeDays;
            if (DateTime.TryParse(ReadString(draft, "updatedUtc", ""), null,
                DateTimeStyles.RoundtripKind, out DateTime utc)) return DateTime.UtcNow - utc.ToUniversalTime() > TimeSpan.FromHours(24);
            return false;
        }

        private static string CampaignCommandPlanHash(Dictionary<string, object> candidate)
        {
            string canonical = Json.Serialize(ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>());
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Select(x => x.ToString("x2")));
        }

        private static Dictionary<string, object> CampaignCommandCloneDictionary(Dictionary<string, object> value)
        {
            return TryParseJsonObject(Json.Serialize(value ?? new Dictionary<string, object>()))
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static string CampaignCommandDraftFile(string campaignId)
        {
            return CampaignFile(campaignId, "actions", "campaign-command-dialogue-drafts.json");
        }

        private static Dictionary<string, object> ReadCampaignCommandDraftState(string campaignId)
        {
            Dictionary<string, object> state = ReadJsonObject(CampaignCommandDraftFile(campaignId));
            if (!state.ContainsKey("schema")) state["schema"] = "reign-campaign-command-dialogue-drafts-v1";
            if (!state.ContainsKey("drafts")) state["drafts"] = new List<Dictionary<string, object>>();
            return state;
        }

        private static void WriteCampaignCommandDraftState(string campaignId,
            Dictionary<string, object> state, List<Dictionary<string, object>> drafts)
        {
            state["schema"] = "reign-campaign-command-dialogue-drafts-v1";
            state["drafts"] = drafts;
            state["updatedUtc"] = DateTime.UtcNow.ToString("o");
            string path = CampaignCommandDraftFile(campaignId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteJsonObject(path, state);
        }
    }
}
