using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        internal static bool CastleOpeningReplyImpliesPlayerSpeech(string reply)
        {
            string value = (reply ?? string.Empty).Trim();
            if (value.Length == 0) return false;

            string[] patterns =
            {
                @"\b(?:Your\s+Grace|Your\s+Majesty|Sire|My\s+Lord|My\s+Lady|Regent|King|Queen)\b[^.!?\r\n]{0,80}\bI\s+(?:can\s+)?hear\s+you\b",
                @"\b(?:Your\s+Grace|Your\s+Majesty|Sire|My\s+Lord|My\s+Lady|Regent|King|Queen)\b[^.!?\r\n]{0,80}\b(?:as\s+you|you)\s+(?:say|said|ask|asked|request|requested|order|ordered|command|commanded)\b",
                @"\b(?:Your\s+Grace|Your\s+Majesty|Sire|My\s+Lord|My\s+Lady|Regent|King|Queen)\b[^.!?\r\n]{0,80}\byour\s+(?:words?|question|request|concern|proposal|command|order|petition)\b",
                @"\bthe\s+(?:ruler|regent|king|queen)'s\s+(?:words?|question|request|concern|proposal|command|order|petition)\b"
            };
            foreach (string pattern in patterns)
            {
                if (Regex.IsMatch(value, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }

            return false;
        }

        private static Dictionary<string, object> RetryCastleOpeningContradictoryResponse(
            Dictionary<string, object> llm,
            Dictionary<string, object> request,
            bool castleOpening,
            string campaignId,
            string correlationId,
            string auditMode,
            string heroId,
            string eventId)
        {
            if (!castleOpening || !ReadBool(llm, "ok", false)) return llm;
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            if (!CastleOpeningReplyImpliesPlayerSpeech(reply)) return llm;

            Dictionary<string, object> repairRequest = new Dictionary<string, object>
            {
                ["requestType"] = "castle_opening_repair",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId + "-castle-opening-repair",
                ["heroStringId"] = heroId,
                ["eventId"] = eventId,
                ["promptCacheEligible"] = false,
                ["reasoningDisabled"] = true,
                ["temperature"] = 0d,
                ["maxTokens"] = Math.Max(3000, Math.Min(8000,
                    ReadInt(request, "maxTokens", 8000))),
                ["response_format"] = new Dictionary<string, object>
                    { ["type"] = "json_object" },
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "You repair one Bannerlord Reign Castle Chat opening. Return exactly one complete JSON object and no commentary. Preserve the original structure, supported facts, personality, tone, and private classifications. The ruler has only entered the room and has not spoken, asked, ordered, proposed, or expressed anything. Rewrite the visible reply as an NPC-initiated greeting, observation, introduction, or request for attention. Never acknowledge nonexistent player words or recast a prior NPC's labeled speech as the player's petition. Remove any relationship assessment, memory, belief, obligation, state update, or action that depends on invented player conduct."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] =
                            "CONTRADICTION: The visible reply implies that the silent ruler already spoke.\nCHARACTER AND MOTIVE CONTEXT:\n"
                            + Json.Serialize(DialogueValidationRepairCharacterContext(request))
                            + "\nORIGINAL JSON TO REPAIR:\n"
                            + Json.Serialize(parsed)
                    }
                }
            };
            string requestedModel = ReadString(request, "model", "");
            if (!string.IsNullOrWhiteSpace(requestedModel))
                repairRequest["model"] = requestedModel;

            Dictionary<string, object> repaired = ChatWithLlm(repairRequest);
            Dictionary<string, object> repairedParsed = TryParseJsonObject(
                ReadString(repaired, "content", ""));
            string repairedReply = repairedParsed == null
                ? string.Empty
                : ReadFirstString(repairedParsed,
                    "reply", "response", "text", "content");
            bool usableRepair = ReadBool(repaired, "ok", false)
                && repairedParsed != null
                && StructuredResponseIsComplete(
                    ReadString(repaired, "content", ""), auditMode);
            bool revalidationCleared = usableRepair
                && !CastleOpeningReplyImpliesPlayerSpeech(repairedReply);
            if (usableRepair)
            {
                MarkRepairedVisibleResponse(
                    repairedParsed, revalidationCleared);
                repaired["content"] = Json.Serialize(repairedParsed);
            }
            else
            {
                repaired["ok"] = false;
                repaired["errorCode"] = "castle_opening_repair_unusable";
                repaired["error"] = "The Castle Chat opening repair did not return a usable structured response; no deterministic greeting was substituted.";
            }
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["detected"] = true,
                ["accepted"] = usableRepair,
                ["revalidationCleared"] = revalidationCleared,
                ["secondAttemptReturned"] = usableRepair,
                ["deterministicFallback"] = false,
                ["visibleRepairMarker"] = usableRepair
                    ? (revalidationCleared ? ".." : ".,")
                    : "",
                ["method"] = usableRepair
                    ? "compact_llm_rewrite_returned"
                    : "unusable_repair_no_dialogue_returned",
                ["originalReply"] = LimitText(reply, 600),
                ["repairedReply"] = LimitText(repairedReply, 600)
            };
            WriteAudit(campaignId, correlationId, "server", auditMode,
                "llm.castle_opening_repair", heroId, "", eventId,
                !usableRepair ? "failed"
                    : revalidationCleared ? "completed"
                    : "completed_with_revalidation_override",
                ReadLong(repaired, "durationMs", 0),
                !usableRepair
                    ? "The Castle Chat opening repair did not return usable structured dialogue; no fallback response was written."
                    : revalidationCleared
                        ? "A Castle Chat opening that implied player speech was corrected before transcript storage."
                        : "The second Castle Chat opening repair still implied silent-ruler speech but was returned without a canned fallback.",
                evidence);
            repaired["castleOpeningRepair"] = evidence;
            return repaired;
        }
    }
}
