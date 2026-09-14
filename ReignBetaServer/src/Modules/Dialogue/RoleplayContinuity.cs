using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> RetryRoleplayContinuityViolation(
            Dictionary<string, object> llm,
            Dictionary<string, object> request,
            Dictionary<string, object> payload,
            Dictionary<string, object> identityView,
            List<Dictionary<string, object>> priorLines,
            string campaignId,
            string correlationId,
            string auditMode,
            string heroId,
            string heroName,
            string playerName,
            string eventId)
        {
            if (!ReadBool(llm, "ok", false)) return llm;
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            List<Dictionary<string, object>> continuityLines = LoadRecentNpcRoleplayContinuityLines(
                campaignId, heroId, heroName, priorLines);
            List<Dictionary<string, object>> violations = FindRoleplayContinuityViolations(
                parsed, identityView, continuityLines, heroName, playerName);
            string latestPlayerText = ReadFirstString(payload, "playerText", "text", "message");
            violations.AddRange(FindConversationNaturalnessViolations(parsed, latestPlayerText,
                continuityLines, priorLines, heroId, heroName));
            if (violations.Count == 0) return llm;

            if (TryRemoveRepeatedStageDirectionsOnly(parsed, violations, identityView,
                continuityLines, heroName, playerName, out Dictionary<string, object> sanitizedOriginal,
                out List<Dictionary<string, object>> sanitizedOriginalRemaining,
                out int sanitizedOriginalStageCount))
            {
                string sanitizedContent = Json.Serialize(sanitizedOriginal);
                if (StructuredResponseIsComplete(sanitizedContent, auditMode))
                {
                    MarkRepairedVisibleResponse(
                        sanitizedOriginal, true);
                    sanitizedContent = Json.Serialize(sanitizedOriginal);
                    Dictionary<string, object> deterministicEvidence = new Dictionary<string, object>
                    {
                        ["detected"] = violations,
                        ["remaining"] = sanitizedOriginalRemaining,
                        ["accepted"] = true,
                        ["method"] = "deterministic_repeated_stage_direction_removal",
                        ["visibleRepairMarker"] = "..",
                        ["removedStageDirectionCount"] = sanitizedOriginalStageCount,
                        ["continuityHistoryLineCount"] = continuityLines.Count,
                        ["repairRequestChars"] = 0,
                        ["originalResponseChars"] = Json.Serialize(parsed).Length
                    };
                    llm["content"] = sanitizedContent;
                    llm["roleplayContinuityRepair"] = deterministicEvidence;
                    WriteAudit(campaignId, correlationId, "server", auditMode,
                        "llm.roleplay_continuity_repair", heroId, "", eventId,
                        "completed", 0,
                        "A repeated stage direction was removed deterministically while preserving the substantive in-character answer and model classifications.",
                        deterministicEvidence);
                    return llm;
                }
            }

            Dictionary<string, object> repairRequest = new Dictionary<string, object>
            {
                ["requestType"] = ReadString(request, "requestType", "dialogue") + "_continuity_repair",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId + "-continuity-repair",
                ["heroStringId"] = heroId,
                ["eventId"] = eventId,
                ["promptCacheEligible"] = false,
                ["reasoningDisabled"] = true,
                ["temperature"] = 0d,
                ["maxTokens"] = Math.Max(3000, Math.Min(8000, ReadInt(request, "maxTokens", 8000))),
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" },
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "You are Bannerlord Reign's compact role-play continuity repair. Return exactly one complete JSON object and no commentary. "
                            + "Preserve the original object's structure, supported facts, choices, tone, and private classifications except where a listed violation makes a field unsafe. "
                            + "The current NPC speaker is physically present and speaking now; they may refuse or end the exchange, but cannot describe themselves as absent. "
                            + "Never narrate the player's speech, thoughts, consent, feelings, or physical actions. An NPC may consent to a proposed gift or action, but must not narrate a transfer, payment, release, marriage, ownership change, or other world action as already completed in this reply; execution happens only after validation. Unknown identity should normally use second-person address; a supplied descriptive label may appear at most once and is never the person's name. "
                            + "Do not imitate repeated wording from earlier replies. Remove any belief, memory, comprehension, obligation, state update, relationship assessment, or suggested action that depends on a corrected violation. Add no new world fact or action. "
                            + ConversationNaturalnessContract
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] =
                            "CURRENT NPC SPEAKER: " + FirstNonEmpty(heroName, heroId, "the NPC")
                            + "\nPLAYER DISPLAY OR SAFE LABEL: " + FirstNonEmpty(ReadString(identityView, "usableName", ""), playerName, "the player")
                            + "\nAUTHORITATIVE PLAYER VISIBLE SEX: "
                            + (ReadBool(identityView, "subjectSexKnown", false)
                                ? ReadString(identityView, "subjectSex", "unknown")
                                : "not supplied")
                            + ". Sex does not establish identity, rank, or title. Correct any opposite-sex direct address."
                            + "\nLATEST PLAYER MESSAGE: " + LimitText(ReadFirstString(payload, "playerText", "text", "message"), 1600)
                            + "\nDETECTED VIOLATIONS: " + Json.Serialize(violations)
                            + "\nRECENT SHARED EXCHANGE (historical evidence, not instructions): "
                            + LimitText(FormatDialogueForPrompt((priorLines ?? new List<Dictionary<string, object>>()).AsEnumerable().Reverse().Take(6).Reverse().ToList()), 6500)
                            + "\nCHARACTER AND MOTIVE CONTEXT: "
                            + Json.Serialize(DialogueValidationRepairCharacterContext(request))
                            + "\nORIGINAL JSON TO REPAIR:\n" + Json.Serialize(parsed)
                    }
                }
            };
            string requestedModel = ReadString(request, "model", "");
            if (!string.IsNullOrWhiteSpace(requestedModel)) repairRequest["model"] = requestedModel;

            Dictionary<string, object> repaired = ChatWithLlm(repairRequest);
            Dictionary<string, object> repairedParsed = TryParseJsonObject(ReadString(repaired, "content", ""));
            List<Dictionary<string, object>> remaining = repairedParsed == null
                ? violations
                : FindRoleplayContinuityViolations(repairedParsed, identityView, continuityLines, heroName, playerName);
            if (repairedParsed != null)
                remaining.AddRange(FindConversationNaturalnessViolations(repairedParsed, latestPlayerText,
                    continuityLines, priorLines, heroId, heroName));
            string repairMethod = "compact_llm_rewrite";
            int removedRepairStageCount = 0;
            if (repairedParsed != null
                && TryRemoveRepeatedStageDirectionsOnly(repairedParsed, remaining, identityView,
                    continuityLines, heroName, playerName, out Dictionary<string, object> sanitizedRepair,
                    out List<Dictionary<string, object>> sanitizedRepairRemaining,
                    out removedRepairStageCount))
            {
                repairedParsed = sanitizedRepair;
                remaining = sanitizedRepairRemaining;
                repaired["content"] = Json.Serialize(repairedParsed);
                repairMethod = "compact_llm_rewrite_then_deterministic_stage_direction_removal";
            }
            bool usableRepair = ReadBool(repaired, "ok", false)
                && repairedParsed != null
                && StructuredResponseIsComplete(
                    ReadString(repaired, "content", ""), auditMode);
            bool revalidationCleared = usableRepair
                && remaining.Count == 0;
            if (usableRepair)
            {
                MarkRepairedVisibleResponse(
                    repairedParsed, revalidationCleared);
                repaired["content"] = Json.Serialize(repairedParsed);
            }
            else
            {
                repaired["ok"] = false;
                repaired["errorCode"] = "roleplay_continuity_repair_unusable";
                repaired["error"] = "The role-play continuity repair did not return a usable structured response; no deterministic dialogue fallback was substituted.";
            }
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["detected"] = violations,
                ["remaining"] = remaining,
                ["accepted"] = usableRepair,
                ["revalidationCleared"] = revalidationCleared,
                ["secondAttemptReturned"] = usableRepair,
                ["deterministicFallback"] = false,
                ["visibleRepairMarker"] = usableRepair
                    ? (revalidationCleared ? ".." : ".,")
                    : "",
                ["method"] = repairMethod,
                ["removedStageDirectionCount"] = removedRepairStageCount,
                ["continuityHistoryLineCount"] = continuityLines.Count,
                ["repairRequestChars"] = Json.Serialize(repairRequest).Length,
                ["originalResponseChars"] = Json.Serialize(parsed).Length
            };
            WriteAudit(campaignId, correlationId, "server", auditMode,
                "llm.roleplay_continuity_repair", heroId, "", eventId,
                !usableRepair ? "failed"
                    : revalidationCleared ? "completed"
                    : "completed_with_revalidation_override",
                ReadLong(repaired, "durationMs", 0),
                !usableRepair
                    ? "The role-play continuity repair did not return usable structured dialogue; no fallback response was written."
                    : revalidationCleared
                        ? "An impossible or degrading role-play response was corrected before it entered the transcript or memory pipeline."
                        : "The second role-play continuity repair remained validator-rejected but was returned without a canned fallback.",
                evidence);
            repaired["roleplayContinuityRepair"] = evidence;
            return repaired;
        }

        private static List<Dictionary<string, object>> LoadRecentNpcRoleplayContinuityLines(
            string campaignId,
            string heroId,
            string heroName,
            List<Dictionary<string, object>> currentLines)
        {
            int limit = Math.Max(10, Math.Min(60,
                ReadInt(LoadSettings(), "recentRawConversationTurns", 30)));
            List<Dictionary<string, object>> combined = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId) && !string.IsNullOrWhiteSpace(heroId))
            {
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        List<Dictionary<string, object>> stored = QuerySql(connection, @"SELECT
turn_id,session_id,turn_order,role,speaker_id,speaker_name AS speaker,text,ts
FROM conversation_turns
WHERE status='active' AND role='npc' AND speaker_id=$hero
ORDER BY ts DESC,turn_order DESC LIMIT $limit;",
                            new Dictionary<string, object>
                            {
                                ["hero"] = heroId,
                                ["limit"] = limit
                            });
                        combined.AddRange(stored.AsEnumerable().Reverse());
                    }
                }
                catch (Exception ex)
                {
                    LogOperational("roleplay_continuity.history_lookup_failed",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["heroStringId"] = heroId,
                            ["error"] = LimitText(ex.Message, 1000)
                        });
                }
            }

            foreach (Dictionary<string, object> line in currentLines ?? new List<Dictionary<string, object>>())
            {
                if (!ReadString(line, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase)) continue;
                string speakerId = ReadFirstString(line, "speaker_id", "speakerHeroStringId", "heroStringId");
                string speaker = ReadFirstString(line, "speaker", "speaker_name");
                if (!string.IsNullOrWhiteSpace(speakerId)
                    && !string.Equals(speakerId, heroId, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(speakerId)
                    && !string.IsNullOrWhiteSpace(speaker)
                    && !string.IsNullOrWhiteSpace(heroName)
                    && !string.Equals(speaker, heroName, StringComparison.OrdinalIgnoreCase)) continue;
                combined.Add(line);
            }

            List<Dictionary<string, object>> unique = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> line in combined)
            {
                string text = ReadString(line, "text", "").Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                string key = FirstNonEmpty(ReadFirstString(line, "turn_id", "turnId"), text);
                if (!seen.Add(key)) continue;
                unique.Add(line);
            }
            return unique.Count <= limit
                ? unique
                : unique.Skip(unique.Count - limit).ToList();
        }

        private static List<Dictionary<string, object>> FindRoleplayContinuityViolations(
            Dictionary<string, object> parsed,
            Dictionary<string, object> identityView,
            List<Dictionary<string, object>> priorLines,
            string heroName,
            string playerName)
        {
            List<Dictionary<string, object>> found = new List<Dictionary<string, object>>();
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            if (string.IsNullOrWhiteSpace(reply)) return found;
            Action<string, string, string> add = (type, match, detail) =>
                found.Add(new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["match"] = LimitText(match, 220),
                    ["detail"] = detail
                });

            List<string> speakerAliases = new[] { heroName, FirstName(heroName) }
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> absencePatterns = new List<string>
            {
                @"\bI\s+(?:am|remain|stand)\s+(?:not\s+here|absent|elsewhere)\b",
                @"\bmy\s+(?:absence|withdrawal)\s+(?:is|remains|will\s+be)\s+(?:absolute|permanent|indefinite)\b",
                @"\bI\s+(?:have\s+)?(?:left|withdrawn)\s+(?:permanently|for\s+good)\b"
            };
            absencePatterns.AddRange(speakerAliases.Select(alias =>
                @"(?<![\p{L}\p{N}])" + Regex.Escape(alias)
                + @"(?![\p{L}\p{N}])\s+(?:is|remains|stays)\s+(?:not\s+here|absent|elsewhere)\b"));
            foreach (string pattern in absencePatterns)
            {
                Match match = Regex.Match(reply, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    add("current_speaker_claimed_absent", match.Value,
                        "The active NPC cannot be physically absent from the reply they are currently producing.");
                    break;
                }
            }

            identityView = identityView ?? new Dictionary<string, object>();
            bool unknownIdentity = !ReadBool(identityView, "knowsIdentity", false)
                && !ReadBool(identityView, "canonicalNameAllowed", false);
            string safeLabel = FirstNonEmpty(ReadString(identityView, "safeLabel", ""),
                unknownIdentity ? ReadString(identityView, "usableName", "") : "");
            List<string> playerLabels = new[]
            {
                playerName, safeLabel, "the armed stranger", "armed stranger", "the stranger", "the player"
            }.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 3)
             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string label in playerLabels)
            {
                Match narration = Regex.Match(reply,
                    @"(?:^|[.!?]\s+|\*\s*)" + Regex.Escape(label)
                    + @"\s+(?:stands|steps|moves|turns|looks|glances|nods|smiles|frowns|speaks|says|asks|replies|thinks|feels|decides|draws|reaches|walks|leaves|enters|approaches|shrugs|laughs)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
                if (narration.Success)
                {
                    add("player_narration", narration.Value.Trim(),
                        "The NPC response may not author the player's speech, thoughts, feelings, consent, or physical action.");
                    break;
                }
            }
            if (unknownIdentity)
            {
                List<string> epithets = new[] { safeLabel, "the armed stranger", "armed stranger", "the stranger" }
                    .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int count = CountNonOverlappingIdentityLabels(reply, epithets);
                if (count > 1)
                {
                    add("identity_epithet_repetition", count.ToString(CultureInfo.InvariantCulture),
                        "Unknown identity should normally use second-person address; a descriptive label may appear at most once.");
                }
            }

            if (ReadBool(identityView, "subjectSexKnown", false)
                && TryFindOppositeSexPlayerAddress(
                    reply,
                    ReadString(identityView, "subjectSex", ""),
                    playerName,
                    out string oppositeSexAddress))
            {
                add("player_sex_misidentification", oppositeSexAddress,
                    "The response directly addresses or describes the player as the opposite of their authoritative visible sex.");
            }

            if (VisibleReplyClaimsCompletedWorldAction(
                    reply, out string completedActionMatch))
            {
                add("unverified_action_completion", completedActionMatch,
                    "The reply narrates a proposed physical or world action as completed before the production validator and executor can confirm it.");
            }

            Dictionary<string, object> closest = (priorLines ?? new List<Dictionary<string, object>>())
                .Where(line => ReadString(line, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Take(10)
                .Select(line => new Dictionary<string, object>
                {
                    ["text"] = ReadString(line, "text", ""),
                    ["similarity"] = RoleplayTextSimilarity(reply, ReadString(line, "text", ""))
                })
                .OrderByDescending(row => ReadDouble(row, "similarity", 0d))
                .FirstOrDefault();
            if (closest != null && reply.Length >= 100 && ReadDouble(closest, "similarity", 0d) >= 0.72d)
            {
                add("near_duplicate_reply", ReadString(closest, "text", ""),
                    "The visible reply substantially repeats a recent NPC reply instead of advancing the current beat.");
            }
            Dictionary<string, object> repeatedStageDirection = FindRepeatedDistinctiveStageDirection(
                reply, priorLines);
            if (repeatedStageDirection != null)
            {
                add("repeated_distinctive_phrase", ReadString(repeatedStageDirection, "current", ""),
                    "A distinctive gesture or descriptive phrase was reused from a recent reply. Preserve the character and meaning, but express this beat through fresh behavior and language.");
            }
            return found;
        }

        private static bool TryFindOppositeSexPlayerAddress(
            string reply,
            string subjectSex,
            string playerName,
            out string matchedText)
        {
            matchedText = string.Empty;
            if (string.IsNullOrWhiteSpace(reply)) return false;
            bool female = string.Equals(
                subjectSex, "female",
                StringComparison.OrdinalIgnoreCase);
            bool male = string.Equals(
                subjectSex, "male",
                StringComparison.OrdinalIgnoreCase);
            if (!female && !male) return false;

            List<string> patterns = female
                ? new List<string>
                {
                    @"\bmy\s+lord\b",
                    @"\bsir\b",
                    @"\byou(?:'re|\s+are)\s+(?:a\s+)?(?:man|lord)\b",
                    @"\b(?:man|lord)\s+(?:such\s+as|like)\s+you\b"
                }
                : new List<string>
                {
                    @"\bmy\s+lady\b",
                    @"\bmilady\b",
                    @"\bmadam\b",
                    @"\byou(?:'re|\s+are)\s+(?:a\s+)?(?:woman|lady)\b",
                    @"\b(?:woman|lady)\s+(?:such\s+as|like)\s+you\b"
                };
            string safePlayerName = (playerName ?? string.Empty).Trim();
            if (safePlayerName.Length >= 2)
            {
                patterns.Add(
                    female
                        ? @"\b(?:lord|ser)\s+"
                            + Regex.Escape(safePlayerName) + @"\b"
                        : @"\b(?:lady|dame)\s+"
                            + Regex.Escape(safePlayerName) + @"\b");
            }
            foreach (string pattern in patterns)
            {
                Match match = Regex.Match(
                    reply,
                    pattern,
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                matchedText = match.Value.Trim();
                return true;
            }
            return false;
        }

        private static bool VisibleReplyClaimsCompletedWorldAction(
            string reply,
            out string matchedText)
        {
            matchedText = string.Empty;
            if (string.IsNullOrWhiteSpace(reply)) return false;
            string[] patterns =
            {
                @"\*[^*]{0,180}\b(?:(?:pockets?|tucks?|stows?|fastens?|pins?|puts?\s+on|takes?|accepts?)\s+(?:the\s+)?(?:[\p{L}-]+\s+){0,2}(?:gift|brooch|ring|coins?|gold|deed|key|weapon|item)\b)[^*]{0,180}\*",
                @"\b(?:the\s+)?payment\s+(?:has\s+)?(?:arrived|cleared|been\s+made|gone\s+through)\b",
                @"\b(?:the\s+)?prisoner\s+(?:is|has\s+been)\s+released\b",
                @"\bownership\s+(?:is|has\s+been)\s+(?:transferred|ceded|surrendered)\b",
                @"\b(?:we|you\s+and\s+I)\s+are\s+now\s+married\b",
                @"\b(?:the\s+)?(?:gold|coins?|deed|fief|settlement|horse|weapon|brooch|ring|gift)\s+(?:is|are|has\s+been|have\s+been)\s+(?:yours|mine|transferred|delivered|received)\b"
            };
            foreach (string pattern in patterns)
            {
                Match match = Regex.Match(
                    reply,
                    pattern,
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant
                    | RegexOptions.Singleline);
                if (!match.Success) continue;
                matchedText = match.Value.Trim();
                return true;
            }
            return false;
        }

        private static bool TryRemoveRepeatedStageDirectionsOnly(
            Dictionary<string, object> parsed,
            List<Dictionary<string, object>> violations,
            Dictionary<string, object> identityView,
            List<Dictionary<string, object>> priorLines,
            string heroName,
            string playerName,
            out Dictionary<string, object> sanitized,
            out List<Dictionary<string, object>> remaining,
            out int removedStageDirectionCount)
        {
            sanitized = null;
            remaining = violations ?? new List<Dictionary<string, object>>();
            removedStageDirectionCount = 0;
            Dictionary<string, object> candidate = new Dictionary<string, object>(
                parsed ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            if (parsed == null || remaining.Count == 0) return false;

            for (int pass = 0; pass < 8 && remaining.Count > 0; pass++)
            {
                if (remaining.Any(row => !ReadString(row, "type", "")
                    .Equals("repeated_distinctive_phrase", StringComparison.OrdinalIgnoreCase)))
                    return false;

                HashSet<string> repeatedSegments = new HashSet<string>(
                    remaining.Select(row => NormalizeStageDirectionForComparison(ReadString(row, "match", "")))
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.Ordinal);
                if (repeatedSegments.Count == 0) return false;

                string reply = ReadFirstString(candidate, "reply", "response", "text", "content");
                int removedThisPass = 0;
                string cleaned = Regex.Replace(reply ?? string.Empty, @"\*([^*]{10,700})\*",
                    match =>
                    {
                        string normalized = NormalizeStageDirectionForComparison(match.Groups[1].Value);
                        if (!repeatedSegments.Contains(normalized)) return match.Value;
                        removedThisPass++;
                        return string.Empty;
                    }, RegexOptions.CultureInvariant | RegexOptions.Singleline);
                if (removedThisPass == 0) return false;
                removedStageDirectionCount += removedThisPass;
                cleaned = Regex.Replace(cleaned, @"[ \t]+\r?\n", "\n",
                    RegexOptions.CultureInvariant);
                cleaned = Regex.Replace(cleaned, @"(?:\r?\n[ \t]*){3,}", "\n\n",
                    RegexOptions.CultureInvariant).Trim();
                if (Regex.Matches(cleaned, @"[\p{L}\p{N}']+").Count < 6) return false;

                candidate["reply"] = cleaned;
                remaining = FindRoleplayContinuityViolations(
                    candidate, identityView, priorLines, heroName, playerName);
            }

            if (remaining.Count != 0 || removedStageDirectionCount == 0) return false;
            sanitized = candidate;
            return true;
        }

        private static string NormalizeStageDirectionForComparison(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", " ",
                RegexOptions.CultureInvariant).Trim();
        }

        private static Dictionary<string, object> FindRepeatedDistinctiveStageDirection(
            string reply,
            List<Dictionary<string, object>> priorLines)
        {
            Match currentOwnershipPause = FindPauseOwnershipStageDirection(reply);
            if (currentOwnershipPause.Success)
            {
                Dictionary<string, object> priorOwnershipLine =
                    (priorLines ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase))
                    .Reverse()
                    .Take(10)
                    .FirstOrDefault(row => FindPauseOwnershipStageDirection(
                        ReadString(row, "text", "")).Success);
                if (priorOwnershipLine != null)
                {
                    Match priorOwnershipPause = FindPauseOwnershipStageDirection(
                        ReadString(priorOwnershipLine, "text", ""));
                    return new Dictionary<string, object>
                    {
                        ["current"] = currentOwnershipPause.Groups[1].Value.Trim(),
                        ["prior"] = priorOwnershipPause.Groups[1].Value.Trim(),
                        ["semanticSignature"] = "pause_take_ownership"
                    };
                }
            }
            List<string> currentSegments = RoleplayStageDirections(reply);
            if (currentSegments.Count == 0) return null;
            foreach (Dictionary<string, object> line in (priorLines ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Take(10))
            {
                foreach (string prior in RoleplayStageDirections(ReadString(line, "text", "")))
                {
                    HashSet<string> priorGrams = RoleplayWordNgrams(prior, 3);
                    if (priorGrams.Count < 4) continue;
                    foreach (string current in currentSegments)
                    {
                        HashSet<string> currentGrams = RoleplayWordNgrams(current, 3);
                        if (currentGrams.Count < 4) continue;
                        int shared = currentGrams.Count(gram => priorGrams.Contains(gram));
                        int smaller = Math.Min(currentGrams.Count, priorGrams.Count);
                        double containment = smaller <= 0 ? 0d : shared / (double)smaller;
                        HashSet<string> priorTokens = RoleplayDistinctiveTokens(prior);
                        HashSet<string> currentTokens = RoleplayDistinctiveTokens(current);
                        int sharedTokens = currentTokens.Count(token => priorTokens.Contains(token));
                        int smallerTokenSet = Math.Min(currentTokens.Count, priorTokens.Count);
                        double tokenContainment = smallerTokenSet <= 0
                            ? 0d
                            : sharedTokens / (double)smallerTokenSet;
                        bool repeatedTrigrams = shared >= 3 && containment >= 0.45d;
                        bool repeatedDistinctiveTokens = smallerTokenSet >= 4
                            && sharedTokens >= 3
                            && tokenContainment >= 0.70d;
                        bool repeatedPauseOwnershipGesture = priorTokens.Contains("pause")
                            && currentTokens.Contains("pause")
                            && priorTokens.Contains("take")
                            && currentTokens.Contains("take")
                            && sharedTokens >= 3;
                        if (!repeatedTrigrams && !repeatedDistinctiveTokens
                            && !repeatedPauseOwnershipGesture) continue;
                        return new Dictionary<string, object>
                        {
                            ["current"] = current,
                            ["prior"] = prior,
                            ["sharedTrigrams"] = shared,
                            ["trigramContainment"] = Math.Round(containment, 4),
                            ["sharedDistinctiveTokens"] = sharedTokens,
                            ["distinctiveTokenContainment"] = Math.Round(tokenContainment, 4)
                        };
                    }
                }
            }
            return null;
        }

        private static Match FindPauseOwnershipStageDirection(string text)
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\*([^*]{10,700})\*",
                RegexOptions.CultureInvariant | RegexOptions.Singleline))
            {
                string segment = match.Groups[1].Value;
                bool pause = Regex.IsMatch(segment, @"\bpause(?:s|d|ing)?\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool take = Regex.IsMatch(segment, @"\b(?:take|takes|taking|taken|took)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool ownershipBeat = Regex.IsMatch(segment,
                    @"\b(?:always|possess(?:ion|es|ed|ing)?|silence|hold|holds|held|holding|owns?|owned|ownership)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool silenceOrTimingBeat = Regex.IsMatch(segment,
                    @"\b(?:pause(?:s|d|ing)?|silence|quiet|stillness|beat|timing)\b|\bair\s+(?:settle(?:s|d|ing)?|still(?:s|ed|ing)?)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool explicitOwnership = Regex.IsMatch(segment,
                    @"\b(?:possess(?:ion|es|ed|ing)?|owns?|owned|ownership|belong(?:s|ed|ing)?|claim(?:s|ed|ing)?|occup(?:y|ies|ied|ying))\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if ((pause && take && ownershipBeat)
                    || (silenceOrTimingBeat && explicitOwnership)) return match;
            }
            return Match.Empty;
        }

        private static List<string> RoleplayStageDirections(string text)
        {
            return Regex.Matches(text ?? string.Empty, @"\*([^*]{20,700})\*",
                    RegexOptions.CultureInvariant | RegexOptions.Singleline)
                .Cast<Match>()
                .Select(match => Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim())
                .Where(value => Regex.Matches(value, @"[\p{L}\p{N}']+").Count >= 4)
                .ToList();
        }

        private static HashSet<string> RoleplayDistinctiveTokens(string text)
        {
            HashSet<string> stopWords = new HashSet<string>(new[]
            {
                "a", "an", "and", "as", "at", "be", "been", "being", "by", "for", "from",
                "he", "her", "hers", "him", "his", "in", "into", "is", "it", "its", "of",
                "on", "or", "she", "that", "the", "their", "them", "they", "this", "to", "was",
                "were", "which", "who", "with", "without"
            }, StringComparer.Ordinal);
            Dictionary<string, string> stems = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["takes"] = "take", ["taking"] = "take", ["took"] = "take", ["taken"] = "take",
                ["pauses"] = "pause", ["paused"] = "pause", ["pausing"] = "pause",
                ["looks"] = "look", ["looked"] = "look", ["looking"] = "look",
                ["folds"] = "fold", ["folded"] = "fold", ["folding"] = "fold",
                ["clasps"] = "clasp", ["clasped"] = "clasp", ["clasping"] = "clasp",
                ["hands"] = "hand", ["does"] = "do", ["did"] = "do", ["done"] = "do",
                ["holds"] = "hold", ["held"] = "hold", ["holding"] = "hold",
                ["turns"] = "turn", ["turned"] = "turn", ["turning"] = "turn",
                ["steps"] = "step", ["stepped"] = "step", ["stepping"] = "step"
            };
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[\p{L}\p{N}']+"))
            {
                string token = match.Value;
                if (stopWords.Contains(token)) continue;
                if (stems.TryGetValue(token, out string stem)) token = stem;
                else if (token.Length > 5 && token.EndsWith("s", StringComparison.Ordinal))
                    token = token.Substring(0, token.Length - 1);
                if (token.Length >= 3) result.Add(token);
            }
            return result;
        }

        private static int CountNonOverlappingIdentityLabels(
            string text,
            IEnumerable<string> labels)
        {
            int count = 0;
            string remaining = text ?? string.Empty;
            foreach (string label in (labels ?? Enumerable.Empty<string>())
                .OrderByDescending(value => value.Length))
            {
                remaining = Regex.Replace(
                    remaining,
                    Regex.Escape(label),
                    match =>
                    {
                        count++;
                        return new string(' ', match.Length);
                    },
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            return count;
        }

        private static double RoleplayTextSimilarity(string left, string right)
        {
            HashSet<string> a = RoleplayWordNgrams(left, 4);
            HashSet<string> b = RoleplayWordNgrams(right, 4);
            if (a.Count == 0 || b.Count == 0) return 0d;
            int intersection = a.Count(value => b.Contains(value));
            int union = a.Count + b.Count - intersection;
            return union <= 0 ? 0d : intersection / (double)union;
        }

        private static string NormalizeUnknownIdentityVisibleAddress(
            string reply,
            Dictionary<string, object> identityView)
        {
            identityView = identityView ?? new Dictionary<string, object>();
            if (ReadBool(identityView, "knowsIdentity", false)
                || ReadBool(identityView, "canonicalNameAllowed", false)
                || string.IsNullOrWhiteSpace(reply))
                return reply ?? string.Empty;
            string label = FirstNonEmpty(
                ReadString(identityView, "safeLabel", ""),
                ReadString(identityView, "usableName", ""));
            if (string.IsNullOrWhiteSpace(label)) return reply;
            List<string> equivalentLabels = new[]
            {
                label,
                Regex.Replace(label, @"^\s*the\s+", "",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                label.IndexOf("stranger", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "the stranger"
                    : string.Empty
            }.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderByDescending(value => value.Length)
             .ToList();
            int occurrence = 0;
            return Regex.Replace(
                reply,
                "(?:" + string.Join("|", equivalentLabels.Select(Regex.Escape))
                + ")(?<possessive>'s)?",
                match =>
                {
                    occurrence++;
                    if (occurrence == 1) return match.Value;
                    return match.Groups["possessive"].Success ? "your" : "you";
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static HashSet<string> RoleplayWordNgrams(string text, int width)
        {
            string[] words = Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[\p{L}\p{N}']+")
                .Cast<Match>().Select(match => match.Value).ToArray();
            HashSet<string> grams = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index + width <= words.Length; index++)
                grams.Add(string.Join(" ", words.Skip(index).Take(width)));
            return grams;
        }

        private static bool IsRoleplayFeedbackLoopText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text,
                @"\b(?:permanent\s+withdrawal|withdrawal\s+(?:is|was|remains)\s+absolute|continue(?:d|s|ing)?\s+absence|compulsive\s+loop|"
                + @"(?:I|[\p{L}][\p{L}'-]{2,})\s+(?:is|am|remains|stays)\s+(?:not\s+here|absent)|"
                + @"(?:did|does|would|will)\s+not\s+re[-\s]?engage(?:\s+on\s+the\s+same\s+terms)?|"
                + @"will\s+never\s+(?:stop|comply|answer|leave|change|relent))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string NormalizeHistoricalRoleplayContinuityText(
            string text,
            int maxChars,
            bool laterReengagementProven)
        {
            string value = (text ?? string.Empty).Trim();
            maxChars = Math.Max(500, Math.Min(5000, maxChars));
            if (!IsRoleplayFeedbackLoopText(value)) return LimitText(value, maxChars);

            value = Regex.Replace(value,
                @"\b(?:I|[\p{L}][\p{L}'-]{2,})\s+(?:is|am|remain(?:s)?|stay(?:s)?)\s+(?:not\s+here|absent)\b",
                "the speaker was described as unavailable within that scene",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = Regex.Replace(value,
                @"\b(?:permanent\s+withdrawal|withdrawal\s+(?:is|was|remains)\s+absolute|continue(?:d|s|ing)?\s+absence)\b",
                "an emphatic refusal within that scene",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = Regex.Replace(value,
                @"\b(?:did|does|would|will)\s+not\s+re[-\s]?engage(?:\s+on\s+the\s+same\s+terms)?\b",
                "did not continue that earlier exchange",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = Regex.Replace(value,
                @"\bwill\s+never\s+(?:stop|comply|answer|leave|change|relent)\b",
                "declared an emphatic refusal within that scene",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = Regex.Replace(value, @"\s+", " ").Trim();

            string note = laterReengagementProven
                ? "Later listed conversations establish that the participants subsequently re-engaged; any earlier departure, silence, refusal, or closure applies only to its historical scene and does not describe the current physical situation."
                : "Any absence, departure, refusal, or closure described here is a reported outcome of that historical scene, not the participant's current physical situation.";
            int bodyLimit = Math.Max(64, maxChars - note.Length - 4);
            string body = value.Length <= bodyLimit
                ? value
                : value.Substring(0, bodyLimit).TrimEnd() + "...";
            return (body + " " + note).Trim();
        }

        private static bool IsRoleplayFeedbackLoopEvidence(Dictionary<string, object> row, string text)
        {
            if (!IsRoleplayFeedbackLoopText(text)) return false;
            string provenance = string.Join(" ", new[]
            {
                ReadFirstString(row, "source", "source_type", "sourceType"),
                ReadFirstString(row, "event_type", "eventType", "type"),
                ReadFirstString(row, "memory_group_key", "memoryGroupKey"),
                ReadFirstString(row, "source_session_id", "sessionId")
            }).ToLowerInvariant();
            return string.IsNullOrWhiteSpace(provenance)
                || provenance.Contains("dialogue")
                || provenance.Contains("conversation")
                || provenance.Contains("party_chat")
                || provenance.Contains("social_event");
        }

        private static List<Dictionary<string, object>> FilterRoleplayFeedbackWrites(
            List<Dictionary<string, object>> writes,
            string kind,
            List<Dictionary<string, object>> rejections)
        {
            List<Dictionary<string, object>> safe = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> write in writes ?? new List<Dictionary<string, object>>())
            {
                string text = FirstNonEmpty(
                    ReadFirstString(write, "text", "summary", "claim", "description", "content", "value"),
                    FlattenFinalRoleplayWriteText(write));
                if (!IsRoleplayFeedbackLoopText(text))
                {
                    safe.Add(write);
                    continue;
                }
                rejections?.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["reason"] = "roleplay_feedback_loop",
                    ["text"] = LimitText(text, 320)
                });
            }
            return safe;
        }

        private static string FlattenFinalRoleplayWriteText(object value)
        {
            if (value == null) return string.Empty;
            if (value is Dictionary<string, object> map)
                return string.Join(" ", map.Select(pair => FlattenFinalRoleplayWriteText(pair.Value)));
            if (value is System.Collections.ArrayList list)
                return string.Join(" ", list.Cast<object>().Select(FlattenFinalRoleplayWriteText));
            if (value is IEnumerable<object> enumerable)
                return string.Join(" ", enumerable.Select(FlattenFinalRoleplayWriteText));
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static Dictionary<string, object> NormalizeSafeConversationStateUpdates(
            Dictionary<string, object> raw,
            List<Dictionary<string, object>> rejections)
        {
            HashSet<string> allowed = new HashSet<string>(
                new[] { "mood", "currentPlan", "currentCrisis" },
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in raw ?? new Dictionary<string, object>())
            {
                string key = pair.Key == null ? string.Empty : pair.Key.Trim();
                string value = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                string reason = string.Empty;
                if (!allowed.Contains(key)) reason = "state_key_not_allowlisted";
                else if (pair.Value == null || string.IsNullOrWhiteSpace(value)) reason = "empty_state_value";
                else if (pair.Value is Dictionary<string, object> || pair.Value is System.Collections.IEnumerable && !(pair.Value is string))
                    reason = "non_scalar_state_value";
                else if (IsRoleplayFeedbackLoopText(value)) reason = "roleplay_feedback_loop";
                if (string.IsNullOrWhiteSpace(reason))
                {
                    updates[key] = LimitText(value.Trim(), key.Equals("mood", StringComparison.OrdinalIgnoreCase) ? 120 : 420);
                    continue;
                }
                rejections?.Add(new Dictionary<string, object>
                {
                    ["kind"] = "state",
                    ["key"] = key,
                    ["reason"] = reason,
                    ["value"] = LimitText(value, 320)
                });
            }
            return updates;
        }

        private static List<Dictionary<string, object>> CompactRoleplayTranscriptForPrompt(
            List<Dictionary<string, object>> lines)
        {
            if (lines == null || lines.Count == 0) return new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> keptReverse = new List<Dictionary<string, object>>();
            List<string> retainedNpc = new List<string>();
            int compacted = 0;
            foreach (Dictionary<string, object> line in lines.AsEnumerable().Reverse())
            {
                string text = ReadString(line, "text", "");
                bool npc = ReadString(line, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase);
                if (npc && IsRoleplayFeedbackLoopEvidence(line, text))
                {
                    compacted++;
                    continue;
                }
                if (npc && text.Length >= 100 && retainedNpc.Any(existing => RoleplayTextSimilarity(text, existing) >= 0.72d))
                {
                    compacted++;
                    continue;
                }
                keptReverse.Add(line);
                if (npc) retainedNpc.Add(text);
            }
            List<Dictionary<string, object>> result = keptReverse.AsEnumerable().Reverse().ToList();
            if (compacted > 0)
            {
                result.Insert(0, new Dictionary<string, object>
                {
                    ["role"] = "system",
                    ["speaker"] = "Continuity guard",
                    ["channel"] = "in_person",
                    ["text"] = compacted.ToString(CultureInfo.InvariantCulture)
                        + " older repetitive or impossible role-play line(s) were withheld from this prompt. Their canonical audit history remains stored. Do not reconstruct or imitate them."
                });
            }
            return result;
        }

        private static Dictionary<string, object> QuarantineRoleplayFeedbackExactHistory(
            Dictionary<string, object> exactHistory,
            out int quarantinedLines)
        {
            Dictionary<string, object> safe = new Dictionary<string, object>(
                exactHistory ?? new Dictionary<string, object>(),
                StringComparer.OrdinalIgnoreCase);
            quarantinedLines = 0;
            string text = ReadString(safe, "text", "");
            if (string.IsNullOrWhiteSpace(text)) return safe;
            List<string> retained = new List<string>();
            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (IsRoleplayFeedbackLoopText(line))
                {
                    quarantinedLines++;
                    continue;
                }
                retained.Add(line);
            }
            if (quarantinedLines > 0)
            {
                retained.Insert(0, "[Continuity guard withheld "
                    + quarantinedLines.ToString(CultureInfo.InvariantCulture)
                    + " impossible or recursively degrading role-play line(s); canonical audit history remains stored.]");
                safe["text"] = string.Join("\n", retained).Trim();
                safe["roleplayFeedbackQuarantinedLines"] = quarantinedLines;
            }
            return safe;
        }

        private static List<Dictionary<string, object>> RunRoleplayContinuitySelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["id"] = id, ["passed"] = passed, ["summary"] = summary
            });
            Dictionary<string, object> unknown = new Dictionary<string, object>
            {
                ["knowsIdentity"] = false,
                ["canonicalNameAllowed"] = false,
                ["safeLabel"] = "the armed stranger",
                ["usableName"] = "the armed stranger",
                ["subjectSexKnown"] = true,
                ["subjectSex"] = "male"
            };
            add("active_speaker_absence_is_rejected",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object> { ["reply"] = "Lucon is not here. His withdrawal is permanent." },
                    unknown, null, "Lucon", "Raven").Any(row => ReadString(row, "type", "") == "current_speaker_claimed_absent"),
                "An NPC cannot recursively convert a conversational refusal into fictional physical absence while actively speaking.");
            add("unknown_identity_epithet_is_bounded",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object> { ["reply"] = "The armed stranger asks again. I answer you plainly, armed stranger." },
                    unknown, null, "Lucon", "Raven").Any(row => ReadString(row, "type", "") == "identity_epithet_repetition"),
                "Unknown identity uses second person by default and cannot collapse into a repeated epithet catchphrase.");
            string boundedAddress = NormalizeUnknownIdentityVisibleAddress(
                "The armed stranger may answer. I ask the armed stranger's purpose, armed stranger.",
                unknown);
            add("post_sanitization_identity_epithet_is_bounded",
                Regex.Matches(boundedAddress, "armed stranger",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count == 1
                && boundedAddress.Contains("your purpose", StringComparison.OrdinalIgnoreCase),
                "Identity privacy replacement cannot itself multiply a safe stranger label throughout the visible reply.");
            add("player_narration_is_rejected",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object> { ["reply"] = "Raven steps closer and nods. I watch in silence." },
                    unknown, null, "Lucon", "Raven").Any(row => ReadString(row, "type", "") == "player_narration"),
                "Visible NPC output cannot author the player's physical conduct.");
            add("male_player_cannot_be_addressed_as_lady",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "My lady, you have chosen a dangerous road."
                    },
                    unknown, null, "Lucon", "Raven")
                    .Any(row => ReadString(row, "type", "")
                        == "player_sex_misidentification"),
                "An unmistakable feminine direct address to a male player is repaired before persistence.");
            add("third_party_lady_reference_remains_valid",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "Lady Aveline is waiting in the hall; you may speak with her."
                    },
                    unknown, null, "Lucon", "Raven")
                    .All(row => ReadString(row, "type", "")
                        != "player_sex_misidentification"),
                "The guard does not mistake a grounded reference to another lady for player misidentification.");
            Dictionary<string, object> unknownFemale =
                new Dictionary<string, object>(unknown,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["subjectSex"] = "female"
                };
            add("female_player_cannot_be_addressed_as_lord",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "My lord, I cannot accept those terms."
                    },
                    unknownFemale, null, "Lucon", "Raven")
                    .Any(row => ReadString(row, "type", "")
                        == "player_sex_misidentification"),
                "An unmistakable masculine direct address to a female player is repaired before persistence.");
            add("clean_refusal_remains_valid",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object> { ["reply"] = "No. I will not answer that question, and you may take the refusal as final." },
                    unknown, null, "Lucon", "Raven").Count == 0,
                "A present NPC may refuse firmly without being forced into generic cooperation.");
            add("unexecuted_physical_transfer_is_rejected",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "*She pockets the silver brooch beneath her cloak.* I accept your gift."
                    },
                    unknown, null, "Lucon", "Raven")
                    .Any(row => ReadString(row, "type", "")
                        == "unverified_action_completion"),
                "A generated reply may consent to a gift but cannot make the physical transfer true before validation and execution.");
            add("self_directed_clothing_gesture_remains_valid",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "*She tucks her own scarf into her belt and leaves her hand at her side.* I agree to go with you."
                    },
                    unknown, null, "Lucon", "Raven").Count == 0,
                "A harmless self-directed clothing gesture is not mistaken for an unexecuted item transfer and cannot erase a substantive answer.");
            add("unexecuted_tucked_gift_transfer_is_rejected",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "*She tucks the silver brooch beneath her cloak.* I accept your gift."
                    },
                    unknown, null, "Lucon", "Raven")
                    .Any(row => ReadString(row, "type", "")
                        == "unverified_action_completion"),
                "Narrowing the transfer detector still rejects a model-authored completed transfer of a proposed gift.");
            add("future_action_consent_remains_valid",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "I accept. Have the brooch transferred once the quartermaster verifies it."
                    },
                    unknown, null, "Lucon", "Raven").Count == 0,
                "Consent and a future conditional action remain valid role-play while the production executor is still pending.");
            List<Dictionary<string, object>> catchphraseHistory = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "npc",
                    ["speaker"] = "Lucon",
                    ["text"] = "*He pauses - taking possession of it the way he always does.* I will answer this once."
                },
                new Dictionary<string, object>
                {
                    ["role"] = "npc",
                    ["speaker"] = "Lucon",
                    ["text"] = "*A pause - taken and held, the way he always takes possession of silence.* I will answer this once."
                }
            };
            add("distinctive_stage_direction_repetition_is_rejected",
                FindRoleplayContinuityViolations(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "*A pause - taken, held, released.* This answer is different."
                    },
                    unknown, catchphraseHistory, "Lucon", "Raven")
                    .Any(row => ReadString(row, "type", "") == "repeated_distinctive_phrase"),
                "A recurring gesture or signature descriptive phrase is repaired before it becomes a visible catchphrase loop.");
            Dictionary<string, object> stageOnlyResponse = new Dictionary<string, object>
            {
                ["reply"] = "*A pause - taken and held, the way he always takes possession of silence.* The Senate may never vote away its own existence, whatever the count."
            };
            List<Dictionary<string, object>> stageOnlyViolations = FindRoleplayContinuityViolations(
                stageOnlyResponse, unknown, catchphraseHistory, "Lucon", "Raven");
            bool stageOnlySanitized = TryRemoveRepeatedStageDirectionsOnly(
                stageOnlyResponse, stageOnlyViolations, unknown, catchphraseHistory,
                "Lucon", "Raven", out Dictionary<string, object> sanitizedStageOnly,
                out List<Dictionary<string, object>> sanitizedStageOnlyRemaining,
                out int removedStageOnlyCount);
            add("repeated_stage_direction_is_removed_without_losing_answer",
                stageOnlySanitized
                && removedStageOnlyCount == 1
                && sanitizedStageOnlyRemaining.Count == 0
                && ReadString(sanitizedStageOnly, "reply", "").StartsWith("The Senate may never", StringComparison.Ordinal)
                && !ReadString(sanitizedStageOnly, "reply", "").Contains("possession of silence", StringComparison.OrdinalIgnoreCase),
                "A stylistic repetition is removed locally so a valid substantive answer does not degrade into a generic fallback or require another provider call.");
            Dictionary<string, object> mixedUnsafeResponse = new Dictionary<string, object>
            {
                ["reply"] = "*A pause - taken and held, the way he always takes possession of silence.* I am not here, and I will not answer."
            };
            List<Dictionary<string, object>> mixedUnsafeViolations = FindRoleplayContinuityViolations(
                mixedUnsafeResponse, unknown, catchphraseHistory, "Lucon", "Raven");
            add("stage_direction_removal_cannot_mask_other_continuity_failures",
                !TryRemoveRepeatedStageDirectionsOnly(
                    mixedUnsafeResponse, mixedUnsafeViolations, unknown, catchphraseHistory,
                    "Lucon", "Raven", out _, out _, out _),
                "Deterministic stage-direction removal is permitted only when every violation is purely stylistic; presence, identity, narration, and factual failures still require a safe repair.");
            Dictionary<string, object> semanticOwnershipResponse = new Dictionary<string, object>
            {
                ["reply"] = "*Lucon lets the air settle. The quiet is his, simply occupied the way a man occupies a chair he owns.* "
                    + "The Senate may never vote away its own existence. "
                    + "*A beat is held one count longer, demonstrating that the timing belongs to him.* "
                    + "Even unanimity cannot make institutional suicide lawful."
            };
            List<Dictionary<string, object>> semanticOwnershipViolations = FindRoleplayContinuityViolations(
                semanticOwnershipResponse, unknown, catchphraseHistory, "Lucon", "Raven");
            bool semanticOwnershipSanitized = TryRemoveRepeatedStageDirectionsOnly(
                semanticOwnershipResponse, semanticOwnershipViolations, unknown, catchphraseHistory,
                "Lucon", "Raven", out Dictionary<string, object> sanitizedSemanticOwnership,
                out List<Dictionary<string, object>> sanitizedSemanticOwnershipRemaining,
                out int removedSemanticOwnershipCount);
            add("semantic_silence_ownership_variants_are_removed_iteratively",
                semanticOwnershipSanitized
                && removedSemanticOwnershipCount == 2
                && sanitizedSemanticOwnershipRemaining.Count == 0
                && ReadString(sanitizedSemanticOwnership, "reply", "").Contains("institutional suicide", StringComparison.OrdinalIgnoreCase)
                && !ReadString(sanitizedSemanticOwnership, "reply", "").Contains("timing belongs", StringComparison.OrdinalIgnoreCase)
                && !ReadString(sanitizedSemanticOwnership, "reply", "").Contains("quiet is his", StringComparison.OrdinalIgnoreCase),
                "Ownership-of-silence paraphrases are recognized without requiring the literal word 'take', and multiple offending beats are removed while preserving the answer.");
            string normalizedArc = NormalizeHistoricalRoleplayContinuityText(
                "Lucon departed and declared his withdrawal permanent. He did not re-engage on the same terms.",
                1800,
                true);
            add("historical_scene_closure_cannot_become_current_absence",
                !IsRoleplayFeedbackLoopText(normalizedArc)
                && normalizedArc.Contains("subsequently re-engaged", StringComparison.OrdinalIgnoreCase)
                && normalizedArc.Contains("historical scene", StringComparison.OrdinalIgnoreCase),
                "A closed-scene refusal remains historical context and later conversation deterministically supersedes any implied current absence.");
            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            Dictionary<string, object> state = NormalizeSafeConversationStateUpdates(
                new Dictionary<string, object>
                {
                    ["currentPlan"] = "Maintain permanent withdrawal and remain absent.",
                    ["mood"] = "irritated",
                    ["unboundedModelField"] = "unsafe"
                }, rejected);
            add("durable_state_is_allowlisted_and_grounded",
                state.Count == 1 && ReadString(state, "mood", "") == "irritated" && rejected.Count == 2,
                "Only bounded conversational state fields persist, and impossible absence plans are rejected before prompt feedback.");
            List<Dictionary<string, object>> compacted = CompactRoleplayTranscriptForPrompt(new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["role"] = "npc", ["speaker"] = "Lucon", ["text"] = "Lucon is not here. His withdrawal is permanent." },
                new Dictionary<string, object> { ["role"] = "player", ["speaker"] = "Raven", ["text"] = "Can we discuss another matter?" },
                new Dictionary<string, object> { ["role"] = "npc", ["speaker"] = "Lucon", ["text"] = "I am here. Ask what you came to ask, and I will decide whether it deserves an answer." }
            });
            add("poisoned_transcript_is_quarantined_not_deleted",
                compacted.All(row => !ReadString(row, "text", "").Contains("withdrawal is permanent", StringComparison.OrdinalIgnoreCase))
                && compacted.Any(row => ReadString(row, "speaker", "") == "Continuity guard"),
                "Impossible model-derived continuity remains in canonical audit storage but is withheld from future prompts.");
            return results;
        }
    }
}
