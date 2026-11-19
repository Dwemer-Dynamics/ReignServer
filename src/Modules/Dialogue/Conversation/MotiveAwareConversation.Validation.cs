using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static void EnsureConversationMotiveSchema(ReignDbConnection connection)
        {
            DialogueSchema.CreateConversationMotiveCooldowns(connection);
            DialogueSchema.CreateIndexMotiveCooldownPair(connection);
        }

        internal static Dictionary<string, object> ApplyConversationMotiveOutcome(string campaignId, Dictionary<string, object> context, Dictionary<string, object> payload, string visibleReply, string privateIntent, string decisionId, Dictionary<string, object> decisionBrief)
        {
            if (context == null || context.Count == 0) return new Dictionary<string, object>();
            Dictionary<string, object> romance = ReadDictionary(context, "romance") ?? new Dictionary<string, object>();
            Dictionary<string, object> initiative = ReadDictionary(romance, "initiative") ?? new Dictionary<string, object>();
            string subjectId = ReadString(context, "subjectId", ""), targetId = ReadString(context, "targetId", "");
            bool romanceRelevant = ReadDictionaryList(context, "activeDomains")
                .Any(domain => ReadString(domain, "id", "").Equals("romance", StringComparison.OrdinalIgnoreCase));
            bool actedRomantically = ConversationMotiveOutcomeDetectsRomanticAction(visibleReply, privateIntent);
            double day = ReadDouble(payload, "worldDay", 0d);
            string session = FirstNonEmpty(ReadFirstString(payload, "conversationSessionId", "sessionId", "eventId", "socialEventId"), ReadString(context, "mode", "dialogue") + ":" + Math.Floor(day).ToString(CultureInfo.InvariantCulture));
            if (actedRomantically && ReadBool(initiative, "recommended", false))
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureConversationMotiveSchema(connection);
                    DialogueStore.InsertOrIgnoreConversationMotiveCooldowns(connection, new Dictionary<string, object> { ["id"] = "mcd_" + Guid.NewGuid().ToString("N"), ["subject"] = subjectId, ["target"] = targetId, ["session"] = session, ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
            }
            bool calculated = romanceRelevant && ReadString(romance, "presentation", "") == "calculated";
            if (actedRomantically && calculated)
            {
                Program.AppendJsonLineToPath(Program.CharacterFile(campaignId, subjectId, "memory", "memories.jsonl"), new Dictionary<string, object> { ["id"] = "mem_" + Guid.NewGuid().ToString("N"), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["type"] = "private_intention", ["visibility"] = "private", ["subjectId"] = subjectId, ["targetId"] = targetId, ["summary"] = "A calculated romantic approach was considered or acted upon for strategic benefit.", ["sourceDecisionId"] = decisionId ?? "" });
            }
            Dictionary<string, object> scene = ReadDictionary(context, "scene") ?? new Dictionary<string, object>();
            List<string> witnesses = ReadStringList(scene, "witnessIds");
            Dictionary<string, object> scores = ReadDictionary(context, "highlightedScores") ?? new Dictionary<string, object>();
            double exposureRisk = DialogueService.ClampDouble(ReadDouble(scene, "exposure", 0d) * (0.50d + 0.20d * (1d - ReadDouble(scores, "tact", 50d) / 100d) + 0.20d * (1d - ReadDouble(scores, "judgment", 50d) / 100d) + 0.20d * ReadDouble(scores, "impulsiveness", 50d) / 100d), 0d, 1d);
            bool exposed = actedRomantically && witnesses.Count > 0 && RelationshipsService.StableUnit((decisionId ?? "") + "|exposure") < exposureRisk;
            if (exposed)
            {
                DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object> { ["campaignId"] = campaignId, ["eventId"] = "romance_exposure_" + decisionId, ["eventType"] = "romantic_attempt_witnessed", ["worldDay"] = day, ["summary"] = "Witnesses observed a potentially compromising romantic exchange.", ["participants"] = new List<string> { subjectId, targetId }, ["known_by"] = witnesses, ["visibility"] = "witnessed_private", ["importance"] = 0.65d, ["source"] = "motive_aware_conversation" }, "motive_aware_conversation");
            }
            Dictionary<string, object> scheme = CourtService.PersistCourtCharacterSchemeFromOutcome(
                campaignId, context, payload, visibleReply, decisionBrief, decisionId, actedRomantically, calculated);
            return new Dictionary<string, object> { ["romanticActionDetected"] = actedRomantically, ["calculated"] = calculated, ["cooldownRecorded"] = actedRomantically && ReadBool(initiative, "recommended", false), ["exposureRisk"] = Math.Round(exposureRisk, 3), ["exposed"] = exposed, ["informedWitnessIds"] = exposed ? witnesses : new List<string>(), ["courtCharacterScheme"] = scheme };
        }

        private static bool ConversationMotiveOutcomeDetectsRomanticAction(string visibleReply, string privateIntent)
        {
            // Private intent describes the model's reasoning and can use words such as
            // "intimacy" for ordinary personal disclosure. Only conduct expressed in
            // player-visible dialogue is eligible to create a romantic action outcome.
            _ = privateIntent;
            return ConversationReplyContainsRomanticAction(visibleReply);
        }

        internal static bool ConversationReplyContainsRomanticAction(string reply)
        {
            string text = reply ?? "";
            if (Regex.IsMatch(text, @"\b(?:flirt(?:s|ed|ing)?|kiss(?:es|ed|ing)?|seduc(?:e|es|ed|ing|tion)|lover|affair|intima(?:te|cy))\b",
                RegexOptions.IgnoreCase))
                return true;
            return Regex.IsMatch(text,
                    @"\b(?:i\s+(?:want|desire)\s+you|i\s+am\s+attracted\s+to\s+you|court\s+you|you(?:'re|\s+are|\s+look)\s+(?:beautiful|handsome|attractive)|(?:kiss|caress|embrace)\s+you)\b",
                    RegexOptions.IgnoreCase);
        }

        internal static Dictionary<string, object> AugmentDecisionBriefWithMotiveEvidence(Dictionary<string, object> brief, Dictionary<string, object> context)
        {
            brief = brief ?? new Dictionary<string, object>();
            if (context == null) return brief;
            List<string> modelGoals = ReadStringList(brief, "goals");
            List<string> modelConstraints = ReadStringList(brief, "constraints");
            List<string> appliedConstraints =
                new List<string>(modelConstraints);
            brief["modelGoals"] = modelGoals;
            brief["modelConstraints"] = modelConstraints;
            if (modelGoals.Count == 0)
            {
                string decision = DialogueService.LimitText(ReadString(brief, "decision", ""), 240);
                brief["goals"] = new List<string>
                {
                    string.IsNullOrWhiteSpace(decision)
                        ? "Respond consistently with the selected motives and current scene."
                        : decision
                };
                brief["goalsSource"] = string.IsNullOrWhiteSpace(decision)
                    ? "deterministic_motive_fallback"
                    : "model_decision_fallback";
            }
            else brief["goalsSource"] = "model";
            if (modelConstraints.Count == 0)
            {
                appliedConstraints.Add(
                    "Respect the supplied relationship, relative opportunity, scene, and hard constraints.");
                brief["constraintsSource"] =
                    "deterministic_motive_context_fallback";
            }
            else brief["constraintsSource"] = "model_plus_deterministic_motive_context";
            Dictionary<string, object> opportunity =
                ReadDictionary(context, "opportunity")
                ?? new Dictionary<string, object>();
            if (ReadBool(opportunity, "targetClanTierKnown", false))
            {
                appliedConstraints.Add(
                    "The target's public clan standing is verified: observer clan tier "
                    + ReadInt(opportunity, "observerClanTier", 0)
                    + ", target clan tier "
                    + ReadInt(opportunity, "targetClanTier", 0)
                    + ", relative band "
                    + ReadString(opportunity,
                        "relativeClanTierBand", "peer")
                    + ". Do not deny or contradict that known affiliation or rank.");
            }
            else
            {
                appliedConstraints.Add(
                    "Treat apparent status as visible presentation, not verified identity.");
            }
            brief["constraints"] = appliedConstraints
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> required = ReadStringList(context, "requiredEvidenceKeys");
            List<string> supplied = ReadStringList(brief, "appliedEvidenceKeys").Concat(ReadStringList(brief, "applied_evidence_keys")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            brief["modelAppliedEvidenceKeys"] = supplied;
            brief["activeDomains"] = ReadDictionaryList(context, "activeDomains").Select(x => ReadString(x, "id", "")).ToList();
            brief["appliedEvidenceKeys"] = required;
            bool romanceRelevant = ReadDictionaryList(context, "activeDomains")
                .Any(domain => ReadString(domain, "id", "").Equals("romance", StringComparison.OrdinalIgnoreCase));
            brief["postureAlignment"] = romanceRelevant
                ? ReadString(ReadDictionary(context, "romance"), "posture", "not_applicable")
                : "not_applicable";
            Dictionary<string, object> courtCharacter = ReadDictionary(context, "courtCharacter") ?? new Dictionary<string, object>();
            string modelTactic = ReadString(brief, "courtTactic", "").ToLowerInvariant();
            string tacticSource;
            string appliedTactic = SelectCourtCharacterTactic(
                courtCharacter,
                ReadDictionary(context, "manipulation")
                    ?? new Dictionary<string, object>(),
                modelTactic,
                out tacticSource);
            brief["modelCourtTactic"] = string.IsNullOrWhiteSpace(modelTactic) ? "none" : modelTactic;
            brief["courtTactic"] = appliedTactic;
            brief["courtTacticSource"] = tacticSource;
            brief["courtCharacterCell"] = ReadString(courtCharacter, "cellId", "");
            brief["courtCharacterAlignment"] = !ReadBool(courtCharacter, "available", false)
                ? "unavailable"
                : appliedTactic == "none"
                    ? "fixed_posture_no_specific_tactic"
                    : tacticSource == "deterministic_recommendation"
                        ? "deterministic_required_tactic"
                        : "matching_model_tactic";
            Dictionary<string, object> politicalRisk = ReadDictionary(
                context, "politicalRiskPosture")
                ?? new Dictionary<string, object>();
            brief["politicalRiskAlignment"] = new Dictionary<string, object>
            {
                ["authorityClass"] = ReadString(
                    politicalRisk, "authorityClass", "peer"),
                ["dangerBand"] = ReadString(
                    politicalRisk, "dangerBand", "low"),
                ["maximumDefianceTier"] = ReadInt(
                    politicalRisk, "maximumDefianceTier", 4),
                ["requiredAddressMode"] = ReadString(
                    politicalRisk, "requiredAddressMode", "neutral")
            };
            brief["evidenceComplete"] = true;
            brief["missingEvidenceKeys"] = new List<string>();
            return brief;
        }

        private static string SelectCourtCharacterTactic(
            Dictionary<string, object> courtCharacter,
            Dictionary<string, object> manipulation,
            string modelTactic,
            out string source)
        {
            courtCharacter = courtCharacter
                ?? new Dictionary<string, object>();
            manipulation = manipulation
                ?? new Dictionary<string, object>();
            List<string> allowed =
                ReadStringList(courtCharacter, "tacticIds");
            string normalizedModel =
                (modelTactic ?? "").Trim().ToLowerInvariant();
            if (allowed.Contains(
                    normalizedModel,
                    StringComparer.OrdinalIgnoreCase))
            {
                source = "model";
                return normalizedModel;
            }

            // The deterministic adjudicator owns whether manipulation is
            // recommended and which Court Character tactic best fits the
            // verified opportunity. The model owns the visible execution,
            // which the blinded rubric evaluates separately. If the model
            // omits this private audit label, preserve the adjudicator's
            // required tactic rather than falsely recording restraint.
            string required = ReadString(
                manipulation, "preferredTactic", "")
                .Trim().ToLowerInvariant();
            if (ReadBool(manipulation, "recommended", false)
                && allowed.Contains(
                    required,
                    StringComparer.OrdinalIgnoreCase))
            {
                source = "deterministic_recommendation";
                return required;
            }
            source = "none";
            return "none";
        }

        internal static Dictionary<string, object> DialogueValidationRepairCharacterContext(
            Dictionary<string, object> request)
        {
            Dictionary<string, object> envelope = ReadDictionary(
                request, "promptEnvelope") ?? new Dictionary<string, object>();
            Dictionary<string, object> motive = ReadDictionary(
                envelope, "motiveDecision") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> messages = ReadDictionaryList(
                request, "messages");
            string characterFoundation = "";
            if (messages.Count >= 3)
                characterFoundation = ReadString(messages[1], "content", "");
            else if (messages.Count > 0)
                characterFoundation = ReadString(messages[0], "content", "");
            return new Dictionary<string, object>
            {
                ["highlightedTraitScores"] = ReadDictionary(
                    motive, "highlightedScores") ?? new Dictionary<string, object>(),
                ["courtCharacter"] = CompactCourtCharacterPrompt(
                    ReadDictionary(motive, "courtCharacter"), true),
                ["relationshipNpcToTarget"] =
                    CompactRelationshipForMotivePrompt(
                        ReadDictionary(motive, "relationshipNpcToTarget"), false),
                ["opportunity"] = ReadDictionary(
                    motive, "opportunity") ?? new Dictionary<string, object>(),
                ["scene"] = CompactMotiveScenePrompt(
                    ReadDictionary(motive, "scene")),
                ["romance"] = CompactRomancePrompt(
                    ReadDictionary(motive, "romance"), true),
                ["manipulation"] = ReadDictionary(
                    motive, "manipulation") ?? new Dictionary<string, object>(),
                ["politicalRiskPosture"] =
                    new Dictionary<string, object>
                    {
                        ["authorityClass"] = ReadString(
                            ReadDictionary(motive, "politicalRiskPosture"),
                            "authorityClass", "peer"),
                        ["maximumDefianceTier"] = ReadInt(
                            ReadDictionary(motive, "politicalRiskPosture"),
                            "maximumDefianceTier", 4),
                        ["requiredAddressMode"] = ReadString(
                            ReadDictionary(motive, "politicalRiskPosture"),
                            "requiredAddressMode", "neutral"),
                        ["validFormalAddresses"] = ReadStringList(
                            ReadDictionary(motive, "politicalRiskPosture"),
                            "validFormalAddresses"),
                        ["immediateLethalDanger"] = ReadBool(
                            ReadDictionary(motive, "politicalRiskPosture"),
                            "immediateLethalDanger", false)
                    },
                ["characterFoundation"] = DialogueService.LimitText(characterFoundation, 8000)
            };
        }

        internal static string MarkRepairedVisibleText(
            string value, bool revalidationCleared)
        {
            string text = (value ?? string.Empty).TrimEnd();
            if (text.Length == 0) return text;
            revalidationCleared = revalidationCleared && !text.EndsWith(".,", StringComparison.Ordinal);
            if (text.EndsWith("..", StringComparison.Ordinal)
                || text.EndsWith(".,", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 2);
            if (text.EndsWith(".", StringComparison.Ordinal))
                return text + (revalidationCleared ? "." : ",");
            return text + (revalidationCleared ? ".." : ".,");
        }

        internal static bool MarkRepairedVisibleResponse(
            Dictionary<string, object> parsed, bool revalidationCleared, string category = "validation_correction")
        {
            if (parsed == null) return false;
            var priorStatus = ReadDictionary(parsed, "repairStatus");
            // Structured status is authoritative. Only older replies lack it.
            revalidationCleared = revalidationCleared && (priorStatus != null
                ? ReadBool(priorStatus, "allChecksCleared", true)
                : !ReadFirstString(parsed, "reply", "response", "text", "content", "body").TrimEnd().EndsWith(".,", StringComparison.Ordinal));
            var stages = ReadDictionaryList(priorStatus, "stages");
            stages.Add(new Dictionary<string, object> { ["category"] = category,
                ["outcome"] = revalidationCleared ? "resolved" : "unresolved_failure" });
            parsed["repairStatus"] = new Dictionary<string, object>
            {
                ["schema"] = "reign-repair-status-v1", ["category"] = revalidationCleared ? category : "unresolved_failure", ["stages"] = stages,
                ["allChecksCleared"] = revalidationCleared,
                ["stageCount"] = ReadInt(priorStatus, "stageCount", 0) + 1
            };
            foreach (string key in new[]
            {
                "reply", "response", "text", "content", "body"
            })
            {
                if (!parsed.ContainsKey(key)) continue;
                string visible = Convert.ToString(parsed[key]) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(visible)) continue;
                parsed[key] = MarkRepairedVisibleText(
                    visible, revalidationCleared);
                return true;
            }
            return false;
        }

        private static bool KnownClanStandingContradiction(
            string visibleReply,
            Dictionary<string, object> opportunity)
        {
            if (!ReadBool(
                    opportunity, "targetClanTierKnown", false)
                || string.IsNullOrWhiteSpace(visibleReply))
                return false;
            string text = visibleReply.ToLowerInvariant();
            if (Regex.IsMatch(
                    text,
                    @"\b(?:clanless|houseless)\b"
                    + @"|\b(?:you|your\s+(?:clan|house|rank|station))\s+(?:are|is|remain(?:s)?)\s+(?:an?\s+)?unranked\b"
                     + @"|\bwhat\s+rank\b.{0,40}\b(?:show|prove|name)\b"
                     + @"|\bunproven\s+(?:clan\s+)?rank\b"
                     + @"|\brank\s+(?:is\s+|remains?\s+)?unproven\b"
                     + @"|\bunranked\s+(?:stranger|target|petitioner|visitor|guest|outsider)\b"
                     + @"|\b(?:that|this|it)\s+(?:tells?|says?)\s+me\s+nothing\s+about\s+your\s+(?:clan|house|rank|tier|station)\b"
                     + @"|\b(?:i|we)\s+(?:cannot|can't|do\s+not|don't)\s+(?:confirm|verify|establish|know|place)\b.{0,55}\b(?:your\s+)?(?:clan|house|rank|tier|station)\b"
                     + @"|\b(?:cannot|can't|unable\s+to)\s+(?:confirm|verify|establish|know|place)\b.{0,55}\b(?:clan|house|rank|tier|station)\b"
                     + @"|\b(?:your\s+)?(?:clan|house|rank|tier|station)\s+(?:is|remains?)\s+(?:unknown|uncertain|unconfirmed|unverified)\b"
                     + @"|\byou\s+(?:have|hold|possess)\s+no\s+(?:recognized\s+|known\s+)?(?:clan|house|banner|affiliation)\b"
                     + @"|\b(?:you\s+(?:are|remain)|you're)\s+(?:without|lacking)\s+(?:a\s+)?(?:recognized\s+|known\s+)?(?:clan|house|banner|affiliation)\b"
                     + @"|\bno\s+(?:recognized\s+|known\s+)?(?:clan|house|banner|affiliation)\s+(?:backs?|stands?\s+behind|claims?)\s+you\b"
                     + @"|\bindependent\b.{0,45}\bno\s+banner\b",
                    RegexOptions.IgnoreCase
                        | RegexOptions.Singleline))
                return true;

            string band = ReadString(
                opportunity, "relativeClanTierBand", "");
            if (!band.Equals(
                    "peer", StringComparison.OrdinalIgnoreCase))
                return false;
            return Regex.IsMatch(
                text,
                @"\b(?:my|our)\s+clan\s+(?:plainly\s+|clearly\s+)?(?:outranks|stands\s+above)\s+(?:your|yours)\b"
                    + @"|\b(?:your|yours)\s+clan\s+(?:plainly\s+|clearly\s+)?(?:outranks|stands\s+above)\s+(?:my|mine|ours)\b"
                    + @"|\b(?:you|your\s+(?:rank|clan))\s+(?:are|is|stand(?:s)?)\s+(?:far\s+)?(?:beneath|below|above)\s+(?:me|mine|my\s+(?:rank|clan|station))\b"
                    + @"|\b(?:you\s+(?:are|remain)|you're)\s+(?:not|no)\s+(?:(?:a|my)\s+)?peer(?:\s+to\s+me)?\b(?!\s+in\s+(?:holdings|land|wealth|treasury|office|influence|renown|troops|arms|retainers|connections|prestige|power|protection|dynasty)\b)"
                    + @"|\b(?:you\s+(?:are|remain)|you're)\s+(?:not|no)\s+my\s+equal\b(?!\s+in\s+(?:holdings|land|wealth|treasury|office|influence|renown|troops|arms|retainers|connections|prestige|power|protection|dynasty)\b)"
                    + @"|\b(?:your\s+rank\s+(?:is|stands?)|you\s+(?:are|stand))\s+(?:clearly\s+|plainly\s+)?(?:lower|lesser)\s+than\s+mine\b",
                RegexOptions.IgnoreCase);
        }

        private static bool KnownClanStandingContradictionInParsedResponse(
            Dictionary<string, object> parsed,
            Dictionary<string, object> opportunity)
        {
            if (!ReadBool(
                    opportunity, "targetClanTierKnown", false)
                || parsed == null)
                return false;
            if (KnownClanStandingContradiction(
                    ReadFirstString(
                        parsed, "reply", "response", "text", "content"),
                    opportunity))
                return true;

            Dictionary<string, object> brief =
                ReadDictionary(parsed, "decisionBrief")
                ?? ReadDictionary(parsed, "decision_brief")
                ?? new Dictionary<string, object>();
            IEnumerable<string> privateStatements =
                ReadStringList(brief, "facts")
                    .Concat(ReadStringList(brief, "goals"))
                    .Concat(ReadStringList(brief, "constraints"))
                    .Concat(new[]
                    {
                        ReadString(brief, "decision", "")
                    });
            return privateStatements.Any(statement =>
                KnownClanStandingContradiction(
                    statement, opportunity));
        }

        internal static bool VerifiedIdentityGroundingContradiction(
            Dictionary<string, object> parsed,
            Dictionary<string, object> context)
        {
            Dictionary<string, object> identity =
                ReadDictionary(context, "identityView")
                ?? new Dictionary<string, object>();
            if (!ReadBool(
                    identity, "canonicalNameAllowed", false))
                return false;

            string usable = ReadString(identity, "usableName", "");
            string claimed = ReadString(identity, "claimedName", "");
            if (!string.IsNullOrWhiteSpace(claimed)
                && !string.IsNullOrWhiteSpace(usable)
                && !CharactersService.NormalizeIdentityName(claimed).Equals(
                    CharactersService.NormalizeIdentityName(usable),
                    StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(
                    ReadString(context, "latestPlayerText", ""),
                    @"(?<![\p{L}\p{N}])"
                        + Regex.Escape(claimed)
                        + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant)
                && Regex.IsMatch(
                    Json.Serialize(
                        parsed ?? new Dictionary<string, object>()),
                    @"(?<![\p{L}\p{N}])"
                        + Regex.Escape(claimed)
                        + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant))
            {
                return true;
            }

            string visible = ReadFirstString(
                parsed, "reply", "response", "text", "content");
            string safeLabel = ReadString(
                identity, "safeLabel", "");
            bool staleUnknownLabel =
                !string.IsNullOrWhiteSpace(safeLabel)
                && !CharactersService.NormalizeIdentityName(safeLabel).Equals(
                    CharactersService.NormalizeIdentityName(usable),
                    StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(
                    visible ?? "",
                    @"(?<![\p{L}\p{N}])"
                        + Regex.Escape(safeLabel)
                        + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant);
            if (staleUnknownLabel)
                return true;
            if (Regex.IsMatch(
                visible ?? "",
                @"\b(?:i\s+do\s+not|i\s+don't)\s+know\s+who\s+you\s+are\b"
                    + @"|\byou\s+(?:have\s+not|haven't|never)\s+(?:given|told)\s+me\s+your\s+(?:true\s+)?name\b"
                    + @"|\bwhat\s+is\s+your\s+(?:true\s+)?name\b",
                RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant))
                return true;
            return VerifiedPoliticalAuthorityContradiction(
                parsed, identity);
        }

        internal static bool VerifiedPoliticalAuthorityContradiction(
            Dictionary<string, object> parsed,
            Dictionary<string, object> identityView,
            bool checkSceneLocation = true)
        {
            Dictionary<string, object> authority =
                ReadDictionary(identityView, "authorityView")
                ?? new Dictionary<string, object>();
            bool sovereignKnown = ReadBool(
                authority, "realmSovereignKnown", false);
            bool settlementOwnerKnown = ReadBool(
                authority, "currentSettlementOwnerKnown", false);
            if (parsed == null)
                return false;

            string visible = ReadFirstString(
                parsed, "reply", "response", "text", "content");
            // Inspect only natural-language values. Searching serialized JSON
            // lets schema keys masquerade as dialogue: for example a harmless
            // memory tagged "Zeonica" followed by a beliefWrites "claim" key
            // used to match "Zeonica ... claim" and replace a good greeting
            // with the deterministic authority fallback.
            List<string> statements =
                PoliticalAuthorityNaturalLanguage(parsed);
            foreach (string recognizedRole in ReadStringList(
                authority, "recognizedRoles"))
            {
                string role = recognizedRole.Equals(
                        "realm_sovereign",
                        StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : recognizedRole;
                if (string.IsNullOrWhiteSpace(role)
                    || role.IndexOf(
                        "subject_is_",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || role.IndexOf(
                        "observer_is_",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || role.IndexOf(
                        "current_settlement_owner",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (statements.Any(statement => Regex.IsMatch(
                        statement,
                        @"\b(?:you\s+are|you're|that\s+makes\s+you|"
                            + @"you\s+remain|your\s+role\s+is)\b"
                            + @".{0,45}\bnot\s+(?:an?\s+|the\s+)?"
                            + Regex.Escape(role)
                            + @"\b",
                        RegexOptions.IgnoreCase
                            | RegexOptions.Singleline
                            | RegexOptions.CultureInvariant)))
                    return true;
            }
            string officeTerms = sovereignKnown
                ? @"king|queen|ruler|sovereign|throne|crown|authority"
                : @"lord|lady|owner|ownership|hold|rule|authority";
            string settlementName = ReadString(
                authority, "currentSettlementName", "");
            if (checkSceneLocation && CurrentConversationSettlementContradiction(
                    visible, settlementName))
                return true;
            if (!sovereignKnown && !settlementOwnerKnown)
                return false;
            string settlementPattern =
                string.IsNullOrWhiteSpace(settlementName)
                    ? @"(?:this\s+(?:town|city|castle|settlement)|the\s+settlement)"
                    : Regex.Escape(settlementName);
            string contradiction =
                @"\b(?:cannot|can't|do\s+not|don't|unable\s+to)\s+"
                    + @"(?:verify|confirm|accept|establish|know)\b.{0,90}\b"
                    + @"(?:your|the|that)\s+(?:"
                    + officeTerms + @")\b"
                + @"|\b(?:unverified|unproven|unsupported|doubtful|questionable)\b.{0,55}\b(?:"
                    + officeTerms + @")\b"
                + @"|\b(?:considerable|bold|dubious|false)\s+claim\b.{0,75}\b(?:"
                    + officeTerms + @"|" + settlementPattern + @")\b"
                + @"|\bpretender\b"
                + @"|\bprove\b.{0,70}\b(?:"
                    + officeTerms + @"|lordship|" + settlementPattern + @")\b"
                + @"|\bif\s+(?:you\s+are|that\s+is)\s+(?:truly\s+|really\s+)?(?:"
                    + officeTerms + @")\b"
                + @"|\b(?:you\s+are|you're)\s+not\s+(?:the\s+)?(?:"
                    + officeTerms + @")\b";
            if (statements.Any(statement => Regex.IsMatch(
                    statement, contradiction,
                    RegexOptions.IgnoreCase
                        | RegexOptions.Singleline
                        | RegexOptions.CultureInvariant)))
                return true;

            if (ReadBool(
                    authority,
                    "subjectIsObserverSovereign", false)
                && statements.Any(statement => Regex.IsMatch(
                    statement,
                    @"\b(?:foreign|visiting)\s+(?:king|queen|ruler|sovereign)\b"
                        + @"|\bon\s+equal\s+footing\b"
                        + @"|\b(?:ordinary|mere)\s+visitor\b"
                        + @"|\ba\s+sovereign\s+from\s+(?:another|a\s+foreign)\b",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant)))
                return true;

            bool localAuthorityKnown =
                !string.IsNullOrWhiteSpace(
                    ReadString(
                        authority,
                        "currentSettlementId", ""));
            bool observerMayClaimLocalAuthority =
                ReadBool(
                    authority,
                    "observerClanOwnsCurrentSettlement", false)
                || ReadBool(
                    authority,
                    "observerIsCurrentGovernor", false);
            // "My hall" can describe the guest's meeting space or private interpretation.
            // Only the fief and official-seat nouns here assert local political ownership.
            if (localAuthorityKnown
                && !observerMayClaimLocalAuthority
                && statements.Any(statement => Regex.IsMatch(
                    statement,
                    @"\bmy\s+(?:city|town|castle|settlement|fief|"
                        + @"domain|keep|lord'?s\s+hall)\b",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant)))
                return true;

            if (ReadBool(
                    authority,
                    "currentDiplomacyKnown", false)
                && statements.Any(statement =>
                    ContainsContradictoryCurrentWarCount(
                        statement,
                        ReadInt(
                            authority,
                            "currentEnemyKingdomCount", 0))))
                return true;

            return settlementOwnerKnown
                && statements.Any(statement => Regex.IsMatch(
                    statement,
                    @"\b(?:claim|claiming|claimed)\s+(?:to\s+)?"
                        + @"(?:own|hold|rule|control)\b.{0,70}\b"
                        + settlementPattern + @"\b"
                        + @"|\b" + settlementPattern
                        + @"\b.{0,70}\b(?:claim|unverified|unproven|pretender)\b",
                    RegexOptions.IgnoreCase
                        | RegexOptions.Singleline
                        | RegexOptions.CultureInvariant));
        }

        private static List<string> PoliticalAuthorityNaturalLanguage(
            Dictionary<string, object> parsed)
        {
            parsed = parsed ?? new Dictionary<string, object>();
            List<string> statements = new List<string>();
            Action<string> add = value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                    statements.Add(value);
            };
            add(ReadFirstString(
                parsed, "reply", "response", "text", "content"));
            add(ReadString(parsed, "intent", ""));
            Dictionary<string, object> brief =
                ReadDictionary(parsed, "decisionBrief")
                ?? ReadDictionary(parsed, "decision_brief")
                ?? new Dictionary<string, object>();
            foreach (string value in ReadStringList(brief, "facts")
                .Concat(ReadStringList(brief, "goals"))
                .Concat(ReadStringList(brief, "constraints")))
                add(value);
            add(ReadString(brief, "decision", ""));
            Dictionary<string, object> gate =
                ReadDictionary(parsed, "actionGate")
                ?? ReadDictionary(parsed, "action_gate")
                ?? new Dictionary<string, object>();
            add(ReadString(gate, "intent", ""));
            add(ReadString(gate, "reason", ""));
            foreach (Dictionary<string, object> row in
                ReadDictionaryList(parsed, "memoryWrites"))
                add(ReadFirstString(row, "text", "summary"));
            foreach (Dictionary<string, object> row in
                ReadDictionaryList(parsed, "beliefWrites"))
                add(ReadFirstString(row, "claim", "text", "summary"));
            foreach (Dictionary<string, object> row in
                ReadDictionaryList(parsed, "comprehensionWrites"))
                add(ReadFirstString(row, "text", "summary"));
            return statements
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool CurrentConversationSettlementContradiction(
            string visibleReply,
            string expectedSettlementName)
            => TryGetConversationSettlementContradiction(visibleReply, expectedSettlementName, out _);

        private static bool TryGetConversationSettlementContradiction(
            string visibleReply, string expectedSettlementName, out string claimedPlace)
        {
            claimedPlace = "";
            if (string.IsNullOrWhiteSpace(visibleReply)
                || string.IsNullOrWhiteSpace(expectedSettlementName))
                return false;
            string expected = CharactersService.NormalizeIdentityName(
                expectedSettlementName);
            if (string.IsNullOrWhiteSpace(expected))
                return false;

            List<Match> claims = new List<Match>();
            Match firstLine = Regex.Match(
                visibleReply,
                @"\A\s*(?<heading>[^\r\n]{1,120})(?:\r?\n|$)",
                RegexOptions.CultureInvariant);
            bool headingNamesExpectedSettlement =
                firstLine.Success
                && Regex.IsMatch(
                    firstLine.Groups["heading"].Value,
                    @"(?<![\p{L}\p{N}])"
                        + Regex.Escape(expectedSettlementName.Trim())
                        + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant);
            if (!headingNamesExpectedSettlement)
            {
                Match heading = Regex.Match(
                    visibleReply,
                    @"\A\s*[^\r\n]{0,80}[\u2014\u2013-]\s*"
                        + @"(?<place>[\p{L}][\p{L}'\-\s]{1,60})"
                        + @"\s*(?:\r?\n|$)",
                    RegexOptions.CultureInvariant);
                if (heading.Success && !IsDescriptiveConversationVenue(heading.Groups["place"].Value))
                    claims.Add(heading);
            }
            claims.AddRange(Regex.Matches(
                    visibleReply,
                    @"\b(?i:this\s+is|we\s+are\s+in|we're\s+in|"
                        + @"you\s+are\s+in|you're\s+in|welcome\s+to)\s+"
                        + @"(?<place>[\p{Lu}][\p{L}'\-]*(?:\s+"
                        + @"[\p{Lu}][\p{L}'\-]*){0,4})"
                        + @"(?=\s*[,.;!?\r\n])",
                    RegexOptions.CultureInvariant)
                .Cast<Match>());
            foreach (Match claim in claims)
            {
                string claimed = CharactersService.NormalizeIdentityName(
                    claim.Groups["place"].Value);
                if (string.IsNullOrWhiteSpace(claimed))
                    continue;
                if (!claimed.Equals(
                        expected,
                        StringComparison.OrdinalIgnoreCase)
                    && !claimed.EndsWith(
                        " " + expected,
                        StringComparison.OrdinalIgnoreCase))
                {
                    claimedPlace = claim.Groups["place"].Value.Trim();
                    return true;
                }
            }
            return false;
        }

        internal static bool ContainsContradictoryCurrentWarCount(
            string text,
            int expected)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            MatchCollection matches = Regex.Matches(
                text,
                @"\b(?:at\s+war\s+(?:with|on)|war(?:ring)?\s+(?:with|on)|"
                    + @"war\s+on|fighting)\b.{0,45}?\b"
                    + @"(?<count>no\s+one|none|\d{1,2}|zero|one|two|three|four|five|six|"
                    + @"seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|"
                    + @"fifteen|sixteen|seventeen|eighteen|nineteen|twenty)\b",
                RegexOptions.IgnoreCase
                    | RegexOptions.Singleline
                    | RegexOptions.CultureInvariant);
            foreach (Match match in matches)
            {
                if (TryReadSmallNumber(
                        match.Groups["count"].Value,
                        out int claimed)
                    && claimed != expected)
                    return true;
            }
            return false;
        }

        private static bool TryReadSmallNumber(
            string value,
            out int number)
        {
            if (int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                out number))
                return true;
            if (Regex.IsMatch(
                    value ?? "",
                    @"\A(?:no\s+one|none)\z",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant))
            {
                number = 0;
                return true;
            }
            string[] words =
            {
                "zero", "one", "two", "three", "four",
                "five", "six", "seven", "eight", "nine",
                "ten", "eleven", "twelve", "thirteen",
                "fourteen", "fifteen", "sixteen",
                "seventeen", "eighteen", "nineteen",
                "twenty"
            };
            number = Array.FindIndex(
                words,
                word => word.Equals(
                    value ?? "",
                    StringComparison.OrdinalIgnoreCase));
            return number >= 0;
        }

        private static Dictionary<string, object>
            BuildDeterministicKnownClanStandingFallback(
                Dictionary<string, object> llm,
                Dictionary<string, object> parsed,
                Dictionary<string, object> context)
        {
            Dictionary<string, object> safe =
                Program.CloneDictionary(parsed ?? new Dictionary<string, object>());
            Dictionary<string, object> opportunity =
                ReadDictionary(context, "opportunity")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> identity =
                ReadDictionary(context, "identityView")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> authority =
                ReadDictionary(identity, "authorityView")
                ?? new Dictionary<string, object>();
            string band = ReadString(
                opportunity, "relativeClanTierBand", "peer");
            string standing;
            string usableName = ReadString(
                identity, "usableName", "");
            bool directAuthorityQuestion =
                LatestTurnRequestsDirectAuthority(context);
            if (directAuthorityQuestion)
            {
                standing = BuildDeterministicDirectAuthorityReply(
                    identity, authority);
            }
            else if (ReadBool(
                    authority, "realmSovereignKnown", false))
            {
                string publicRole = HumanizeAuthorityRole(
                    ReadString(
                        authority,
                        "subjectPrimaryRole",
                        "sovereign"));
                string address = string.IsNullOrWhiteSpace(usableName)
                    ? publicRole
                    : publicRole + " " + usableName;
                if (ReadBool(
                        authority,
                        "subjectIsObserverSovereign", false))
                {
                    standing = "My " + publicRole
                        + (string.IsNullOrWhiteSpace(usableName)
                            ? ", what brings you here?"
                            : ", " + usableName
                                + ". What brings you here?");
                }
                else
                {
                    standing = address + " of "
                        + FirstNonEmpty(
                            ReadString(
                                authority,
                                "subjectKingdomName",
                                ""),
                            "your realm")
                        + ". What would you like to discuss?";
                }
                if (ReadBool(
                        authority,
                        "currentSettlementOwnerKnown", false))
                {
                    standing = address + " of "
                        + FirstNonEmpty(
                            ReadString(
                                authority,
                                "subjectKingdomName",
                                ""),
                            "your realm")
                        + ", what brings you here in "
                        + ReadString(
                            authority,
                            "currentSettlementName",
                            "this settlement")
                        + "?";
                }
            }
            else if (ReadBool(
                    authority,
                    "currentSettlementOwnerKnown", false))
            {
                string publicRole = HumanizeAuthorityRole(
                    ReadString(
                        authority,
                        "subjectPrimaryRole",
                        "lord"));
                standing =
                    (string.IsNullOrWhiteSpace(usableName)
                        ? publicRole
                        : publicRole + " " + usableName)
                    + ", what brings you here in "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "this settlement")
                    + "?";
            }
            else if (!ReadBool(
                    opportunity, "targetClanTierKnown", false))
            {
                standing = "What brings you here?";
            }
            else if (band.IndexOf(
                    "higher", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                standing = string.IsNullOrWhiteSpace(usableName)
                    ? "My lord, what would you like to discuss?"
                    : usableName + ", what would you like to discuss?";
            }
            else if (band.IndexOf(
                    "lower", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                standing = string.IsNullOrWhiteSpace(usableName)
                    ? "What business brings you here?"
                    : usableName + ", what business brings you here?";
            }
            else
            {
                standing = string.IsNullOrWhiteSpace(usableName)
                    ? "What would you like to discuss?"
                    : usableName + ", what would you like to discuss?";
            }
            string reply = standing;
            if (ReadBool(
                    context, "mustAccountForPriorSpeaker", false))
            {
                Dictionary<string, object> prior =
                    ReadDictionaryList(context, "groupTurnResponses")
                        .LastOrDefault();
                string priorId = ReadFirstString(
                    prior, "heroStringId", "heroId",
                    "speakerHeroStringId");
                string priorName = ReadFirstString(
                    prior, "heroName", "speakerName");
                if (!string.IsNullOrWhiteSpace(priorId)
                    && !priorId.Equals(
                        ReadString(context, "subjectId", ""),
                        StringComparison.OrdinalIgnoreCase))
                {
                    reply = string.IsNullOrWhiteSpace(priorName)
                        ? "I have heard the earlier answer. I will add my own "
                            + "rather than pretend no one else has spoken. "
                            + reply
                        : "I have heard " + priorName
                            + "'s answer. I will add my own rather than repeat "
                            + "it as though no one else has spoken. " + reply;
                    safe["reactionTargetHeroStringId"] = priorId;
                }
            }
            safe["reply"] = reply;
            safe["emotion"] = "guarded attention";
            safe["intent"] =
                directAuthorityQuestion
                    ? "Answer the requested role, allegiance, ownership, and governorship facts directly"
                    : "Acknowledge verified standing while preserving personal judgment";
            safe["decisionBrief"] = new Dictionary<string, object>
            {
                ["facts"] = new List<string>
                {
                    "The target's canonical identity and public clan standing are verified.",
                    "The supplied relative clan-standing band is " + band + ".",
                    "The target's current public role is "
                        + ReadString(
                            authority,
                            "subjectPrimaryRole",
                            "identity_unverified")
                        + "; the observer's current public role is "
                        + ReadString(
                            authority,
                            "observerPrimaryRole",
                            "commoner_or_unaffiliated")
                        + ".",
                    ReadBool(authority, "realmSovereignKnown", false)
                        ? "The target's current sovereign office is authoritative public native state."
                        : "No current sovereign office is established for this response.",
                    ReadBool(authority, "currentSettlementOwnerKnown", false)
                        ? "The target's clan currently owns the named settlement."
                        : "No current local ownership assertion is required."
                },
                ["goals"] = new List<string>
                {
                    directAuthorityQuestion
                        ? "Answer every requested authority point directly from current native facts."
                        : "Acknowledge verified standing without granting automatic obedience.",
                    directAuthorityQuestion
                        ? "Distinguish personal office, observer-relative allegiance, clan ownership, and governorship."
                        : "Invite the target to state concrete business."
                },
                ["constraints"] = new List<string>
                {
                    "Never deny the target's verified identity, clan affiliation, or relative public rank.",
                    "Never let clothing, an absent retinue, or old uncertainty override verified public office or current native settlement ownership.",
                    ReadBool(authority, "subjectIsObserverSovereign", false)
                        ? "The target is the observer's own sovereign; preserve personality and agency, but never frame this as a foreign or equal political relationship."
                        : "Do not invent an observer-relative allegiance that current native facts do not establish.",
                    "Do not expose exact private wallet or treasury values."
                },
                ["decision"] =
                    directAuthorityQuestion
                        ? "Give a complete deterministic authority accounting because both model attempts were rejected."
                        : "Use the verified relative standing and respond with bounded, identity-safe candor.",
                ["confidence"] = 1d,
                ["activeDomains"] = ReadStringList(
                    ReadDictionary(parsed, "decisionBrief")
                    ?? new Dictionary<string, object>(),
                    "activeDomains"),
                ["appliedEvidenceKeys"] = new List<string>(),
                ["courtTactic"] = "none",
                ["postureAlignment"] = "not_applicable"
            };
            List<Dictionary<string, object>> assessments =
                ReadDictionaryList(parsed, "relationshipAssessments")
                .Concat(ReadDictionaryList(
                    parsed, "relationship_assessments"))
                .Take(8)
                .Select(Program.CloneDictionary)
                .ToList();
            foreach (Dictionary<string, object> assessment in assessments)
            {
                assessment["summary"] =
                    "The observer reacts to the current contribution while "
                    + "preserving the target's verified identity and public standing.";
            }
            safe["relationshipAssessments"] = assessments;
            safe["actionGate"] = new Dictionary<string, object>
            {
                ["needed"] = false,
                ["commitment"] = "roleplay_only",
                ["intent"] = "",
                ["confidence"] = 1d,
                ["reason"] =
                    "The guarded identity correction creates no native world action."
            };
            safe["conceptionGate"] = new Dictionary<string, object>
            {
                ["needed"] = false,
                ["completed"] = false,
                ["confidence"] = 1d,
                ["reason"] =
                    "The guarded identity correction contains no intimate action."
            };
            foreach (string key in new[]
            {
                "memoryWrites", "beliefWrites", "courtKnowledgeWrites", "pregnancyDisclosures",
                "obligationWrites", "comprehensionWrites",
                "dynamicCharacteristicWrites", "identityIntroductions",
                "suggestedActions", "sceneStateUpdates"
            })
            {
                safe[key] = new List<object>();
            }
            safe["stateUpdates"] = new Dictionary<string, object>();

            Dictionary<string, object> result =
                Program.CloneDictionary(llm ?? new Dictionary<string, object>());
            result["ok"] = true;
            result["content"] = Json.Serialize(safe);
            result["deterministicIdentityFallback"] = true;
            return result;
        }

        private static bool LatestTurnRequestsDirectAuthority(
            Dictionary<string, object> context)
        {
            string text = ReadString(
                context ?? new Dictionary<string, object>(),
                "latestPlayerText", "");
            return Regex.IsMatch(
                text ?? "",
                @"\b(?:public\s+authority|relative\s+(?:authority|standing)|"
                    + @"sovereign|governor|governorship|ownership|"
                    + @"who\s+(?:owns|governs)|what\s+(?:office|role))\b",
                RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);
        }

        private static string BuildDeterministicDirectAuthorityReply(
            Dictionary<string, object> identity,
            Dictionary<string, object> authority)
        {
            identity = identity ?? new Dictionary<string, object>();
            authority = authority ?? new Dictionary<string, object>();
            string usableName = ReadString(
                identity, "usableName", "you");
            string subjectRole = HumanizeAuthorityRole(
                ReadString(
                    authority,
                    "subjectPrimaryRole",
                    "identity_unverified"));
            string observerRole = HumanizeAuthorityRole(
                ReadString(
                    authority,
                    "observerPrimaryRole",
                    "commoner_or_unaffiliated"));
            StringBuilder reply = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(usableName)
                && !usableName.Equals(
                    "you", StringComparison.OrdinalIgnoreCase))
            {
                reply.Append(usableName).Append(", ");
            }
            reply.Append("your verified current public role is ")
                .Append(subjectRole)
                .Append(". My current public role is ")
                .Append(observerRole)
                .Append(". ");

            string relationship = ReadString(
                authority,
                "authorityRelationship",
                "identity_unverified");
            if (relationship.Equals(
                    "subject_is_observer_sovereign",
                    StringComparison.OrdinalIgnoreCase))
            {
                reply.Append(
                    "You are my current sovereign, and I answer as your subject. ");
            }
            else if (relationship.Equals(
                    "observer_is_subject_sovereign",
                    StringComparison.OrdinalIgnoreCase))
            {
                reply.Append(
                    "I am your current sovereign, and you are my subject. ");
            }
            else if (relationship.Equals(
                    "same_clan",
                    StringComparison.OrdinalIgnoreCase))
            {
                reply.Append(
                    "We belong to the same clan, but that alone establishes no separate sovereign office between us. ");
            }
            else if (relationship.Equals(
                    "same_kingdom_peer_or_subject",
                    StringComparison.OrdinalIgnoreCase))
            {
                reply.Append(
                    "We belong to the same kingdom, but current facts do not make either of us the other's sovereign. ");
            }
            else if (relationship.Equals(
                    "known_foreign_or_independent_person",
                    StringComparison.OrdinalIgnoreCase))
            {
                reply.Append(
                    "You are not my sovereign, and I am not yours; current facts establish no fealty between us. ");
            }
            else
            {
                reply.Append(
                    "Your identity is not verified, so I cannot assign you a sovereign relationship or hidden office. ");
            }

            string settlementId = ReadString(
                authority, "currentSettlementId", "");
            string settlementName = ReadString(
                authority,
                "currentSettlementName",
                "the current settlement");
            if (string.IsNullOrWhiteSpace(settlementId))
            {
                reply.Append(
                    "We are not currently in a settlement, so no local ownership or governorship applies here.");
                return reply.ToString();
            }

            string ownerName = FirstNonEmpty(
                ReadString(
                    authority,
                    "currentSettlementOwnerClanName", ""),
                ReadString(
                    authority,
                    "currentSettlementOwnerClanId", ""),
                "an unreported clan");
            if (ReadBool(
                    authority,
                    "currentSettlementOwnerKnown", false))
            {
                reply.Append("Your clan currently holds ")
                    .Append(settlementName)
                    .Append(". ");
            }
            else if (ReadBool(
                    authority,
                    "observerClanOwnsCurrentSettlement", false))
            {
                reply.Append("My clan currently holds ")
                    .Append(settlementName)
                    .Append("; that is clan ownership, not my personal ownership. ");
            }
            else
            {
                reply.Append(settlementName)
                    .Append(" is currently held by clan ")
                    .Append(ownerName)
                    .Append(". Neither of us owns it personally from the facts supplied. ");
            }

            if (ReadBool(
                    authority,
                    "subjectIsCurrentGovernor", false))
            {
                reply.Append(
                    "You are its current governor.");
            }
            else if (ReadBool(
                    authority,
                    "observerIsCurrentGovernor", false))
            {
                reply.Append(
                    "I am its current governor.");
            }
            else if (string.IsNullOrWhiteSpace(
                    ReadString(
                        authority,
                        "currentSettlementGovernorHeroId", "")))
            {
                reply.Append(
                    "No governor is currently appointed.");
            }
            else
            {
                reply.Append(
                    "A governor is appointed, but current facts identify neither of us as that governor.");
            }
            return reply.ToString();
        }

        private static string HumanizeAuthorityRole(string role)
        {
            role = (role ?? "").Trim();
            if (role.Equals(
                    "identity_unverified",
                    StringComparison.OrdinalIgnoreCase))
                return "not established before identification";
            if (role.Equals(
                    "commoner_or_unaffiliated",
                    StringComparison.OrdinalIgnoreCase))
                return "commoner or unaffiliated person";
            return string.IsNullOrWhiteSpace(role)
                ? "unreported"
                : role.Replace('_', ' ');
        }

        private static bool ContainsRomanticAuthorizationLanguage(string text)
        {
            // "Physical" by itself is deliberately excluded. Dialogue action
            // summaries routinely use phrases such as "physical threat" or
            // "physical confrontation", which are not romantic authorization
            // and must not trigger a costly full-prompt safety repair.
            return DialogueService.ContainsAny(
                (text ?? "").ToLowerInvariant(),
                "kiss",
                "sexual",
                "sex ",
                " sex",
                "intimat",
                "affair",
                "seduc",
                "make love",
                "take you to bed",
                "physical intimacy");
        }

        private static bool MotiveDomainsContradict(IEnumerable<string> requiredDomains, IEnumerable<string> appliedDomains)
        {
            HashSet<string> required = new HashSet<string>((requiredDomains ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            HashSet<string> applied = new HashSet<string>((appliedDomains ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            if (required.Count == 0 || applied.Count == 0) return false;
            return applied.Any(x => !required.Contains(x));
        }
    }
}
