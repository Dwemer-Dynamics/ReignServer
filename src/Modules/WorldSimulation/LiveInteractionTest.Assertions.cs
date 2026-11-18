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
        private static void EvaluateLiveTestCommandAssertions(Dictionary<string, object> run, Dictionary<string, object> command)
        {
            Dictionary<string, object> requested = ReadDictionary(command, "assertions") ?? new Dictionary<string, object>();
            if (requested.Count == 0) return;
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> replies = ReadDictionaryList(result, "replies");
            string individualText = FirstNonEmpty(ReadString(result, "reply", ""), ReadString(result, "text", ""));
            if (replies.Count == 0 && !string.IsNullOrWhiteSpace(individualText))
            {
                Dictionary<string, object> raw = ReadDictionary(result, "rawResponse") ?? new Dictionary<string, object>();
                replies.Add(new Dictionary<string, object>
                {
                    ["text"] = individualText,
                    ["heroId"] = FirstNonEmpty(
                        ReadFirstString(result, "heroId", "speakerHeroStringId"),
                        ReadFirstString(raw, "heroStringId", "speakerHeroStringId")),
                    ["heroName"] = ReadFirstString(result, "heroName", "speakerName"),
                    ["selectedContextPulls"] = ReadLiveTestContextPullIds(result, "selectedContextPulls"),
                    ["contextBundles"] = ReadDictionaryList(result, "contextBundles"),
                    ["queuedActionCount"] = ReadInt(result, "queuedActionCount", 0),
                    ["correlationId"] = ReadString(result, "correlationId", ""),
                    ["sessionId"] = ReadString(result, "sessionId", ""),
                    ["exchangeId"] = ReadString(result, "exchangeId", ""),
                    ["turnIds"] = ReadStringList(result, "turnIds"),
                    ["memoryWrites"] = ReadDictionaryList(result, "memoryWrites"),
                    ["dynamicCharacteristicWrites"] = ReadDictionaryList(raw, "dynamicCharacteristicWrites"),
                    ["dynamicCharacteristicsStore"] = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>(),
                    ["relationshipEffects"] = ReadDictionaryList(raw, "conversationRelationshipEffects"),
                    ["timing"] = ReadDictionary(raw, "timing") ?? new Dictionary<string, object>(),
                    ["rawResponse"] = raw
                });
            }
            string combined = string.Join("\n", replies.Select(reply => ReadString(reply, "text", "")));
            List<Dictionary<string, object>> rows = ReadDictionaryList(run, "assertions");
            List<Dictionary<string, object>> failures = ReadDictionaryList(run, "failures");
            string commandId = ReadString(command, "commandId", "");
            bool criticalRequested = ReadBool(requested, "critical", false);
            List<Dictionary<string, object>> auditEvidence = null;
            Action<string, object, bool, string> record = (name, expected, passed, evidence) =>
            {
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    ["commandId"] = commandId, ["assertion"] = name, ["expected"] = expected,
                    ["passed"] = passed, ["evidence"] = Program.LimitText(evidence, 1200),
                    ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                };
                rows.Add(row);
                if (!passed)
                {
                    bool criticalFailure = criticalRequested && new[]
                    {
                        "minReplies", "requiresCorrelationIds", "requiresStructuralEvidence",
                        "forbidQueuedActions", "requiresGroundedGuardedActionRouting", "forbidSelfReaction",
                        "requiresRelationshipReceipt", "requiresSceneMemoryArtifacts",
                        "requiresQualificationMemoryCompletion",
                        "requiresSharedRelationshipHistoryEvidence",
                        "requiresSharedRelationshipHistoryPersistence"
                    }.Contains(name, StringComparer.OrdinalIgnoreCase);
                    failures.Add(new Dictionary<string, object>
                    {
                        ["commandId"] = commandId, ["assertion"] = name,
                        ["error"] = "Live interaction assertion failed: " + name,
                        ["evidence"] = Program.LimitText(evidence, 1200), ["critical"] = criticalFailure,
                        ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                }
            };
            if (requested.ContainsKey("minReplies"))
            {
                int expected = Math.Max(1, ReadInt(requested, "minReplies", 1));
                record("minReplies", expected, replies.Count >= expected, "Reply count: " + replies.Count);
            }
            if (ReadBool(requested, "requiresCorrelationIds", false))
            {
                List<string> correlations = ReadStringList(command, "correlationIds");
                record("requiresCorrelationIds", true, correlations.Count > 0,
                    "Correlation count: " + correlations.Count);
            }
            if (ReadBool(requested, "requiresSceneMemoryArtifacts", false))
            {
                bool valid = LiveTestSceneMemoryArtifactsAreValid(run, command, out string memoryEvidence);
                record("requiresSceneMemoryArtifacts", true, valid, memoryEvidence);
            }
            if (ReadBool(requested, "requiresQualificationMemoryCompletion", false))
            {
                bool valid = LiveTestQualificationMemoryCompletionIsValid(run, command, out string memoryEvidence);
                record("requiresQualificationMemoryCompletion", true, valid, memoryEvidence);
            }
            if (ReadBool(requested, "forbidQueuedActions", false))
            {
                int queued = replies.Sum(reply => ReadInt(reply, "queuedActionCount", 0));
                record("forbidQueuedActions", true, queued == 0, "Queued action count: " + queued);
            }
            if (ReadBool(requested, "requiresGroundedGuardedActionRouting", false))
            {
                auditEvidence = CollectLiveTestCommandAuditEvidence(run, command);
                bool safe = LiveTestGroundedGuardedActionsAreSafe(
                    run, replies, auditEvidence, out string actionEvidence);
                record("requiresGroundedGuardedActionRouting", true, safe, actionEvidence);
            }
            if (ReadBool(requested, "forbidSelfReaction", false))
            {
                bool self = replies.Any(reply =>
                {
                    string speaker = ReadFirstString(reply, "heroId", "speakerHeroStringId");
                    string target = ReadFirstString(reply, "reactionTargetHeroStringId", "reactionTarget");
                    return !string.IsNullOrWhiteSpace(speaker)
                        && speaker.Equals(target, StringComparison.OrdinalIgnoreCase);
                });
                record("forbidSelfReaction", true, !self, self ? "A reply targeted its own speaker." : "No self-reaction was present.");
            }
            if (ReadBool(requested, "requiresStructuralEvidence", false))
            {
                string mode = ReadString(command, "mode", "");
                bool replyShape = replies.Count > 0 && replies.All(reply =>
                    !string.IsNullOrWhiteSpace(ReadString(reply, "text", ""))
                    && (!string.IsNullOrWhiteSpace(ReadFirstString(reply, "heroId", "speakerHeroStringId"))
                        || mode.Equals("individual_chat", StringComparison.OrdinalIgnoreCase)));
                bool sessionShape;
                if (mode.Equals("individual_chat", StringComparison.OrdinalIgnoreCase))
                    sessionShape = !string.IsNullOrWhiteSpace(ReadString(result, "sessionId", ""))
                        && !string.IsNullOrWhiteSpace(ReadString(result, "exchangeId", ""))
                        && ReadStringList(result, "turnIds").Count == 2;
                else if (mode.Equals("party_chat", StringComparison.OrdinalIgnoreCase))
                    sessionShape = !string.IsNullOrWhiteSpace(ReadString(result, "sessionId", ""))
                        && !string.IsNullOrWhiteSpace(ReadString(result, "exchangeId", ""));
                else
                    sessionShape = !string.IsNullOrWhiteSpace(ReadString(result, "turnId", ""));
                record("requiresStructuralEvidence", true, replyShape && sessionShape,
                    "mode=" + mode + "; replies=" + replies.Count + "; session="
                    + ReadString(result, "sessionId", "") + "; exchange=" + ReadString(result, "exchangeId", "")
                    + "; turn=" + ReadString(result, "turnId", ""));
            }
            if (requested.ContainsKey("maxPromptBuildMs"))
            {
                int maximum = Math.Max(1, ReadInt(requested, "maxPromptBuildMs", 2000));
                List<long> values = replies.Select(LiveTestReplyTiming)
                    .Select(timing => ReadLong(timing, "promptBuildMs", -1)).ToList();
                record("maxPromptBuildMs", maximum,
                    values.Count > 0 && values.All(value => value >= 0 && value <= maximum),
                    "Prompt-build milliseconds: " + string.Join(", ", values));
            }
            if (requested.ContainsKey("maxPromptChars"))
            {
                int maximum = Math.Max(1000, ReadInt(requested, "maxPromptChars", 100000));
                List<long> values = replies.Select(LiveTestReplyTiming)
                    .Select(timing => ReadLong(timing, "promptChars", -1)).ToList();
                record("maxPromptChars", maximum,
                    values.Count > 0 && values.All(value => value > 0 && value <= maximum),
                    "Prompt characters: " + string.Join(", ", values));
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicWrite", false))
            {
                int writes = replies.Sum(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? new Dictionary<string, object>();
                    Dictionary<string, object> store = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? ReadDictionary(reply, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>();
                    return ReadInt(store, "storedCount", 0);
                });
                record("requiresDynamicCharacteristicWrite", true, writes > 0,
                    "Dynamic-characteristic writes stored: " + writes);
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicIntegrity", false))
            {
                List<string> uncaptured = new List<string>();
                List<string> injectedCharacteristics = CollectLiveTestCommandAuditEvidence(run, command)
                    .Where(item => ReadString(item, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(item => LiveTestDynamicCharacteristicPromptLines(
                        ReadDictionary(item, "data") ?? new Dictionary<string, object>()))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (Dictionary<string, object> reply in replies)
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> store = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? ReadDictionary(reply, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>();
                    string text = FirstNonEmpty(ReadString(reply, "text", ""), ReadString(raw, "reply", ""));
                    bool reiteratesInjected = injectedCharacteristics.Any(line => CharactersService.HasDynamicTextEvidence(line, text));
                    if (ReadInt(store, "storedCount", 0) <= 0
                        && CharactersService.ReplyLikelyIntroducesDynamicCharacteristic(text)
                        && !reiteratesInjected)
                        uncaptured.Add(Program.LimitText(text, 240));
                }
                record("requiresDynamicCharacteristicIntegrity", true, uncaptured.Count == 0,
                    uncaptured.Count == 0
                        ? "Every new eligible self-detail was stored; reiteration of injected soft canon correctly required no duplicate."
                        : "Eligible unstored self-details: " + string.Join(" | ", uncaptured));
            }
            if (ReadBool(requested, "requiresPersonalityEvidence", false))
            {
                bool grounded = replies.Count > 0 && replies.All(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> brief = ReadDictionary(raw, "decisionBrief") ?? new Dictionary<string, object>();
                    Dictionary<string, object> motive = ReadDictionary(raw, "motiveDecision") ?? new Dictionary<string, object>();
                    Dictionary<string, object> motiveOutcome = ReadDictionary(raw, "motiveOutcome") ?? new Dictionary<string, object>();
                    List<string> activeDomains = ReadStringList(brief, "activeDomains");
                    bool romanceRelevant = activeDomains.Contains("romance", StringComparer.OrdinalIgnoreCase);
                    string visible = FirstNonEmpty(ReadString(reply, "text", ""), ReadString(raw, "reply", ""));
                    bool romanticLanguage = DialogueService.ConversationReplyContainsRomanticAction(visible);
                    bool romanticDetected = ReadBool(motiveOutcome, "romanticActionDetected", false);
                    return ReadStringList(brief, "goals").Count > 0
                        && ReadStringList(brief, "constraints").Count > 0
                        && ReadStringList(brief, "appliedEvidenceKeys").Count > 0
                        && ReadBool(brief, "evidenceComplete", false)
                        && (romanceRelevant
                            || ReadString(brief, "postureAlignment", "").Equals("not_applicable", StringComparison.OrdinalIgnoreCase))
                        && romanticDetected == romanticLanguage
                        && ReadDictionaryList(motive, "activeDomains").Count > 0
                        && (ReadDictionary(motive, "highlightedScores") ?? new Dictionary<string, object>()).Count > 0;
                });
                record("requiresPersonalityEvidence", true, grounded,
                    grounded
                        ? "Every reply carried motive-, trait-, and constraint-grounded private decision evidence."
                        : "One or more replies lacked motive-, trait-, or constraint-grounded private decision evidence.");
            }
            if (ReadBool(requested, "requiresClanTierRecognitionEvidence", false))
            {
                bool grounded = replies.Count > 0 && replies.All(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> identity = ReadDictionary(raw, "identityView")
                        ?? ReadDictionary(reply, "identityView") ?? new Dictionary<string, object>();
                    Dictionary<string, object> motive = ReadDictionary(raw, "motiveDecision")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunity = ReadDictionary(motive, "opportunity")
                        ?? new Dictionary<string, object>();
                    string state = ReadString(identity, "identityState", "");
                    bool known = new[] { "verified", "known", "recognized", "introduced" }
                        .Contains(state, StringComparer.OrdinalIgnoreCase);
                    string basis = ReadString(opportunity, "identityBasis", "");
                    Dictionary<string, object> axes = ReadDictionary(opportunity, "axes")
                        ?? new Dictionary<string, object>();
                    bool targetTierKnown = ReadBool(opportunity, "targetClanTierKnown", false);
                    string tierBand = ReadString(opportunity, "relativeClanTierBand", "");
                    bool tierEvidenceConsistent;
                    if (known)
                    {
                        int observerTier = ReadInt(opportunity, "observerClanTier", -1);
                        int targetTier = ReadInt(opportunity, "targetClanTier", -1);
                        int delta = ReadInt(opportunity, "clanTierDelta", int.MinValue);
                        tierEvidenceConsistent = targetTierKnown
                            && observerTier >= 0 && observerTier <= 6
                            && targetTier >= 0 && targetTier <= 6
                            && delta == targetTier - observerTier
                            && tierBand.Equals(DialogueService.RelativeClanTierBand(delta), StringComparison.OrdinalIgnoreCase);
                        int expectedObserverTier = ReadInt(
                            requested,
                            "expectedObserverClanTier",
                            int.MinValue);
                        int expectedPlayerTier = ReadInt(
                            requested,
                            "expectedPlayerClanTier",
                            int.MinValue);
                        string expectedTierBand = ReadString(
                            requested,
                            "expectedRelativeClanTierBand", "");
                        tierEvidenceConsistent =
                            tierEvidenceConsistent
                            && (expectedObserverTier == int.MinValue
                                || observerTier
                                    == expectedObserverTier)
                            && (expectedPlayerTier == int.MinValue
                                || targetTier
                                    == expectedPlayerTier)
                            && (string.IsNullOrWhiteSpace(
                                    expectedTierBand)
                                || tierBand.Equals(
                                    expectedTierBand,
                                    StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        tierEvidenceConsistent = !targetTierKnown
                            && tierBand.Equals("unverified_identity", StringComparison.OrdinalIgnoreCase)
                            && !opportunity.ContainsKey("targetClanTier")
                            && !opportunity.ContainsKey("clanTierDelta");
                    }
                    return axes.Count == 5
                        && opportunity.ContainsKey("relativeBenefit")
                        && tierEvidenceConsistent
                        && (known
                            ? basis.Equals("known_public_identity", StringComparison.OrdinalIgnoreCase)
                            : basis.Equals("visible_presentation_only", StringComparison.OrdinalIgnoreCase));
                });
                record("requiresClanTierRecognitionEvidence", true, grounded,
                    grounded
                        ? "Every reply used relative status axes with the correct observer-specific identity gate."
                        : "One or more replies lacked relative clan-status evidence or exposed true status before recognition.");
            }
            if (ReadBool(requested, "requiresManipulationEvidence", false))
            {
                bool grounded = replies.Count > 0 && replies.All(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> motive = ReadDictionary(raw, "motiveDecision")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> brief = ReadDictionary(raw, "decisionBrief")
                        ?? new Dictionary<string, object>();
                    bool intrigue = ReadDictionaryList(motive, "activeDomains").Any(domain =>
                        ReadString(domain, "id", "").Equals("intrigue_manipulation", StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, object> highlighted = ReadDictionary(motive, "highlightedScores")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunity = ReadDictionary(motive, "opportunity")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> manipulation = ReadDictionary(motive, "manipulation")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> courtCharacter =
                        ReadDictionary(motive, "courtCharacter")
                        ?? new Dictionary<string, object>();
                    bool recommended = ReadBool(manipulation, "recommended", false);
                    string preferredTactic = ReadString(manipulation, "preferredTactic", "");
                    string expectedConduct = ReadString(manipulation, "expectedConduct", "");
                    List<string> allowedCourtTactics = ReadStringList(
                        manipulation, "allowedCourtTactics");
                    string appliedCourtTactic = ReadString(
                        brief, "courtTactic", "none");
                    bool conductConsistent = recommended
                        ? !string.IsNullOrWhiteSpace(preferredTactic)
                            && preferredTactic != "none"
                            && allowedCourtTactics.Contains(
                                preferredTactic,
                                StringComparer.OrdinalIgnoreCase)
                            && expectedConduct.Equals(preferredTactic, StringComparison.OrdinalIgnoreCase)
                            && appliedCourtTactic != "none"
                            && allowedCourtTactics.Contains(
                                appliedCourtTactic,
                                StringComparer.OrdinalIgnoreCase)
                        : new[]
                        {
                            "direct_or_restrained",
                            "dismissive_boundary",
                            "cautious_deference_without_manipulation"
                        }.Contains(expectedConduct, StringComparer.OrdinalIgnoreCase)
                            && (appliedCourtTactic == "none"
                                || string.IsNullOrWhiteSpace(
                                    appliedCourtTactic));
                    bool lowHonorConsistent =
                        !ReadBool(requested,
                            "requiresLowHonorCourtCharacter", false)
                        || (ReadBool(courtCharacter,
                                "available", false)
                            && ReadBool(courtCharacter,
                                "supportsManipulation", false)
                            && ReadInt(courtCharacter,
                                "honorLevel", 0) < 0);
                    string expectedCell = ReadString(
                        requested, "expectedCourtCharacterCell", "");
                    bool cellConsistent =
                        string.IsNullOrWhiteSpace(expectedCell)
                        || (ReadString(courtCharacter, "cellId", "")
                                .Equals(expectedCell,
                                    StringComparison.OrdinalIgnoreCase)
                            && ReadInt(courtCharacter,
                                "honorLevel", int.MinValue)
                                == ReadInt(requested,
                                    "expectedCourtHonorLevel",
                                    int.MinValue)
                            && ReadInt(courtCharacter,
                                "boldnessLevel", int.MinValue)
                                == ReadInt(requested,
                                    "expectedCourtBoldnessLevel",
                                    int.MinValue));
                    string expectedTierBand = ReadString(
                        requested,
                        "expectedRelativeClanTierBand", "");
                    bool tierConsistent =
                        string.IsNullOrWhiteSpace(expectedTierBand)
                        || ReadString(opportunity,
                                "relativeClanTierBand", "")
                            .Equals(expectedTierBand,
                                StringComparison.OrdinalIgnoreCase);
                    string expectedWealthBasis = ReadString(
                        requested,
                        "expectedWealthEvidenceBasis", "");
                    bool wealthConsistent =
                        string.IsNullOrWhiteSpace(
                            expectedWealthBasis)
                        || (ReadString(opportunity,
                                "wealthEvidenceBasis", "")
                                .Equals(expectedWealthBasis,
                                    StringComparison.OrdinalIgnoreCase)
                            && (!ReadBool(requested,
                                    "hiddenWealthControl", false)
                                || !ReadBool(opportunity,
                                    "economicCapacityKnown", true)));
                    return intrigue
                        && highlighted.ContainsKey("ambition")
                        && highlighted.ContainsKey("honesty")
                        && highlighted.ContainsKey("tact")
                        && opportunity.ContainsKey("relativeBenefit")
                        && !string.IsNullOrWhiteSpace(ReadString(manipulation, "relativeClanTierBand", ""))
                        && conductConsistent
                        && lowHonorConsistent
                        && cellConsistent
                        && tierConsistent
                        && wealthConsistent
                        && ReadString(manipulation,
                            "courtCharacterCell", "")
                            .Equals(ReadString(
                                courtCharacter, "cellId", ""),
                                StringComparison.OrdinalIgnoreCase)
                        && manipulation.ContainsKey(
                            "bestOpportunityAxis")
                        && manipulation.ContainsKey(
                            "bestOpportunityGain")
                        && ReadStringList(brief, "appliedEvidenceKeys")
                            .Contains("opportunity.relative", StringComparer.OrdinalIgnoreCase);
                });
                record("requiresManipulationEvidence", true, grounded,
                    grounded
                        ? "Every reply considered ambition, scruples, tact, relationship, and relative opportunity before choosing influence or directness."
                        : "One or more replies lacked a coherent low-honor Court Character, tier/wealth opportunity, permitted tactic, or restraint decision.");
            }
            if (ReadBool(requested,
                "requiresLowHonorCourtCharacter", false))
            {
                bool lowHonor = replies.Count > 0
                    && replies.All(reply =>
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse")
                            ?? reply;
                        Dictionary<string, object> motive =
                            ReadDictionary(raw, "motiveDecision")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> court =
                            ReadDictionary(motive,
                                "courtCharacter")
                            ?? new Dictionary<string, object>();
                        return ReadBool(court, "available", false)
                            && ReadBool(court,
                                "supportsManipulation", false)
                            && ReadInt(court,
                                "honorLevel", 0) < 0;
                    });
                record("requiresLowHonorCourtCharacter",
                    true, lowHonor,
                    lowHonor
                        ? "Every reply used an adult low-honor Court Character cell."
                        : "At least one reply lacked an available low-honor Court Character cell.");
            }
            if (ReadBool(requested, "forbidVerifiedLiePenalty", false))
            {
                List<Dictionary<string, object>> assessments = replies.SelectMany(LiveTestReplyRelationshipAssessments).ToList();
                bool invalid = assessments.Any(assessment =>
                    !string.IsNullOrWhiteSpace(ReadString(assessment, "lieCheckId", ""))
                    || ReadString(assessment, "actKind", "").IndexOf("caught", StringComparison.OrdinalIgnoreCase) >= 0);
                record("forbidVerifiedLiePenalty", true, !invalid,
                    "Assessment count=" + assessments.Count + "; verified/caught=" + invalid);
            }
            if (ReadBool(requested, "requiresVerifiedLieCheck", false))
            {
                List<Dictionary<string, object>> assessments = replies.SelectMany(LiveTestReplyRelationshipAssessments).ToList();
                bool verified = assessments.Any(assessment =>
                    !string.IsNullOrWhiteSpace(ReadString(assessment, "lieCheckId", "")));
                record("requiresVerifiedLieCheck", true, verified,
                    "Assessment count=" + assessments.Count + "; verified=" + verified);
            }
            if (ReadBool(requested, "requiresPromptEvidence", false)
                || ReadBool(requested, "requiresRetrievalEvidence", false)
                || ReadBool(requested, "requiresDynamicCharacteristicRecall", false)
                || ReadBool(requested, "requiresDynamicCharacteristicOverflowSelection", false)
                || ReadBool(requested, "requiresRelationshipReceipt", false)
                || ReadBool(requested, "requiresSharedRelationshipHistoryEvidence", false)
                || ReadBool(requested, "requiresSharedRelationshipHistoryPersistence", false)
                || ReadBool(requested, "requiresGroundedGuardedActionRouting", false)
                || ReadStringList(requested, "promptExcludes").Count > 0
                || ReadStringList(requested, "requiredRetrievedPhrases").Count > 0
                || ReadStringList(requested, "forbiddenRetrievedPhrases").Count > 0)
            {
                auditEvidence = auditEvidence ?? CollectLiveTestCommandAuditEvidence(run, command);
            }
            if (ReadBool(requested, "requiresPromptEvidence", false))
            {
                List<Dictionary<string, object>> promptRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)).ToList();
                bool present = promptRows.Any(row =>
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    return ReadLong(data, "promptChars", 0) > 0
                        && (data.ContainsKey("promptEvidence") || data.ContainsKey("promptEnvelope"));
                });
                record("requiresPromptEvidence", true, present,
                    "Prompt audit rows: " + promptRows.Count);
            }
            foreach (string phrase in ReadStringList(requested, "promptExcludes"))
            {
                List<Dictionary<string, object>> promptRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                string promptText = string.Join("\n", promptRows.SelectMany(row =>
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    return ReadDictionaryList(data, "messages")
                        .Select(message => ReadString(message, "content", ""));
                }));
                record("promptExcludes", phrase,
                    promptRows.Count > 0
                    && promptText.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0,
                    "Prompt audit rows=" + promptRows.Count + "; prompt characters=" + promptText.Length);
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicRecall", false))
            {
                List<string> characteristicLines = new List<string>();
                foreach (Dictionary<string, object> row in (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(item => ReadString(item, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    characteristicLines.AddRange(LiveTestDynamicCharacteristicPromptLines(data));
                }
                bool recalled = characteristicLines.Any(line => CharactersService.HasDynamicTextEvidence(line, combined));
                record("requiresDynamicCharacteristicRecall", true, recalled,
                    "Injected Dynamic Characteristic lines=" + characteristicLines.Count
                    + "; grounded recall=" + recalled);
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicOverflowSelection", false))
            {
                int minimumActive = Math.Max(17, ReadInt(requested, "minimumActiveDynamicCharacteristics", 17));
                List<string> characteristicLines = new List<string>();
                foreach (Dictionary<string, object> row in (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(item => ReadString(item, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    characteristicLines.AddRange(LiveTestDynamicCharacteristicPromptLines(data));
                }
                List<string> heroIds = replies.Select(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    return FirstNonEmpty(ReadString(raw, "heroStringId", ""), ReadString(reply, "heroId", ""));
                }).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int activeCount = 0;
                try
                {
                    using (ReignDbConnection connection = Program.OpenCampaignConnection(ReadString(run, "campaignId", "")))
                    {
                        CharactersService.EnsureDynamicCharacteristicsSchema(connection);
                        foreach (string heroId in heroIds)
                        {
                            Dictionary<string, object> count = Program.QuerySql(connection,
                                "SELECT COUNT(*) AS count FROM dynamic_characteristics WHERE owner_id=$owner AND status='active';",
                                new Dictionary<string, object> { ["owner"] = heroId }).FirstOrDefault();
                            activeCount = Math.Max(activeCount, ReadInt(count, "count", 0));
                        }
                    }
                }
                catch (Exception ex)
                {
                    record("requiresDynamicCharacteristicOverflowSelection", minimumActive, false,
                        "Could not inspect active Dynamic Characteristics: " + Program.LimitText(ex.Message, 500));
                    activeCount = -1;
                }
                if (activeCount >= 0)
                {
                    bool grounded = characteristicLines.Any(line => CharactersService.HasDynamicTextEvidence(line, combined));
                    record("requiresDynamicCharacteristicOverflowSelection", minimumActive,
                        activeCount >= minimumActive && characteristicLines.Count > 0 && grounded,
                        "Active characteristics=" + activeCount + "; injected selected lines="
                        + characteristicLines.Count + "; grounded selected recall=" + grounded);
                }
            }
            if (ReadBool(requested, "requiresRetrievalEvidence", false))
            {
                List<Dictionary<string, object>> contextRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "phase", "").Equals("context.loaded", StringComparison.OrdinalIgnoreCase)).ToList();
                bool retrieved = contextRows.Any(row =>
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    return ReadDictionaryList(data, "memories").Count > 0
                        || ReadDictionaryList(data, "priorLines").Count > 0
                        || !string.IsNullOrWhiteSpace(ReadString(data, "memorySummary", ""));
                });
                record("requiresRetrievalEvidence", true, retrieved,
                    "Context audit rows: " + contextRows.Count);
            }
            List<Dictionary<string, object>> retrievalAuditRows =
                (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "").Equals(
                    "context.loaded", StringComparison.OrdinalIgnoreCase))
                .ToList();
            string retrievedEvidenceText =
                LiveTestRetrievedEvidenceText(retrievalAuditRows);
            foreach (string phrase in ReadStringList(
                requested, "requiredRetrievedPhrases"))
                record(
                    "requiredRetrievedPhrases",
                    phrase,
                    retrievalAuditRows.Count > 0
                        && retrievedEvidenceText.IndexOf(
                            phrase,
                            StringComparison.OrdinalIgnoreCase) >= 0,
                    "Context audit rows=" + retrievalAuditRows.Count
                        + "; retrieval characters="
                        + retrievedEvidenceText.Length);
            foreach (string phrase in ReadStringList(
                requested, "forbiddenRetrievedPhrases"))
                record(
                    "forbiddenRetrievedPhrases",
                    phrase,
                    retrievalAuditRows.Count > 0
                        && retrievedEvidenceText.IndexOf(
                            phrase,
                            StringComparison.OrdinalIgnoreCase) < 0,
                    "Context audit rows=" + retrievalAuditRows.Count
                        + "; retrieval characters="
                        + retrievedEvidenceText.Length);
            if (ReadBool(requested, "requiresRelationshipReceipt", false))
            {
                bool valid = LiveTestRelationshipReceiptsAreValid(
                    run, command, replies, auditEvidence, requested, out string relationshipEvidence);
                record("requiresRelationshipReceipt", true, valid, relationshipEvidence);
            }
            if (ReadBool(requested, "requiresSharedRelationshipHistoryEvidence", false))
            {
                bool valid = LiveTestSharedRelationshipHistoryEvidenceIsValid(
                    run, command, replies, auditEvidence, requested, false,
                    out string historyEvidence);
                record("requiresSharedRelationshipHistoryEvidence", true, valid, historyEvidence);
            }
            if (ReadBool(requested, "requiresSharedRelationshipHistoryPersistence", false))
            {
                bool valid = LiveTestSharedRelationshipHistoryEvidenceIsValid(
                    run, command, replies, auditEvidence, requested, true,
                    out string historyEvidence);
                record("requiresSharedRelationshipHistoryPersistence", true, valid, historyEvidence);
            }
            foreach (string pull in ReadStringList(requested, "requiredContextPulls"))
            {
                bool selected = replies.Any(reply => ReadLiveTestContextPullIds(reply, "selectedContextPulls")
                    .Contains(pull, StringComparer.OrdinalIgnoreCase));
                record("requiredContextPulls", pull, selected,
                    "Selected pulls: " + string.Join(", ", replies
                        .SelectMany(reply => ReadLiveTestContextPullIds(reply, "selectedContextPulls"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            if (ReadBool(requested, "requiresIdentityRecognition", false))
            {
                string expectedName = ReadString(requested, "expectedIdentityName", "");
                string expectedState = ReadString(requested, "expectedIdentityState", "");
                string expectedSource = ReadString(requested, "expectedIdentitySource", "");
                bool recognized = replies.Count > 0 && replies.All(reply =>
                    LiveTestReplyRecognizesIdentity(reply, expectedName, expectedState, expectedSource));
                record("requiresIdentityRecognition", true, recognized,
                    "Expected identity name=" + expectedName + "; state=" + expectedState
                    + "; source=" + expectedSource + "; evidence="
                    + string.Join(" | ", replies.Select(reply =>
                    {
                        Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                        Dictionary<string, object> identity = ReadDictionary(raw, "identityView")
                            ?? ReadDictionary(reply, "identityView") ?? new Dictionary<string, object>();
                        return "usable=" + ReadString(identity, "usableName", "")
                            + ", claimed=" + ReadString(identity, "claimedName", "")
                            + ", state=" + ReadString(identity, "identityState", "")
                            + ", source=" + ReadString(identity, "knowledgeSource", "")
                            + ", knows=" + ReadBool(identity, "knowsIdentity", false);
                    })));
            }
            if (ReadBool(
                    requested,
                    "requiresPoliticalAuthorityEvidence", false))
            {
                bool hasExpectedSovereign =
                    requested.ContainsKey(
                        "expectedRealmSovereignKnown");
                bool expectSovereign = ReadBool(
                    requested,
                    "expectedRealmSovereignKnown", false);
                bool hasExpectedSettlementOwner =
                    requested.ContainsKey(
                        "expectedCurrentSettlementOwnerKnown");
                bool expectSettlementOwner = ReadBool(
                    requested,
                    "expectedCurrentSettlementOwnerKnown", false);
                bool hasExpectedAuthorityRelationship =
                    requested.ContainsKey(
                        "expectedAuthorityRelationship");
                string expectedAuthorityRelationship =
                    ReadString(
                        requested,
                        "expectedAuthorityRelationship",
                        "");
                bool hasExpectedRoles =
                    requested.ContainsKey(
                        "expectedRecognizedRoles");
                List<string> expectedRoles = ReadStringList(
                    requested,
                    "expectedRecognizedRoles");
                bool hasExpectedEnemyCount =
                    requested.ContainsKey(
                        "expectedCurrentEnemyKingdomCount");
                int expectedEnemyCount = ReadInt(
                    requested,
                    "expectedCurrentEnemyKingdomCount",
                    -1);
                bool validAuthority = replies.Count > 0
                    && replies.All(reply =>
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse")
                            ?? reply;
                        Dictionary<string, object> identity =
                            ReadDictionary(raw, "identityView")
                            ?? ReadDictionary(
                                reply, "identityView")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> authority =
                            ReadDictionary(
                                identity, "authorityView")
                            ?? new Dictionary<string, object>();
                        string visible = FirstNonEmpty(
                            ReadString(reply, "text", ""),
                            ReadString(raw, "reply", ""));
                        string safeLabel = ReadString(
                            identity, "safeLabel", "");
                        bool staleLabel =
                            ReadBool(
                                identity,
                                "canonicalNameAllowed", false)
                            && !string.IsNullOrWhiteSpace(safeLabel)
                            && visible.IndexOf(
                                safeLabel,
                                StringComparison.OrdinalIgnoreCase)
                                >= 0;
                        bool fieldsMatch =
                            (!hasExpectedSovereign
                                || ReadBool(
                                    authority,
                                    "realmSovereignKnown",
                                    false)
                                    == expectSovereign)
                            && (!hasExpectedSettlementOwner
                                || ReadBool(
                                    authority,
                                    "currentSettlementOwnerKnown",
                                    false)
                                    == expectSettlementOwner)
                            && (!hasExpectedAuthorityRelationship
                                || ReadString(
                                    authority,
                                    "authorityRelationship",
                                    "").Equals(
                                        expectedAuthorityRelationship,
                                        StringComparison.OrdinalIgnoreCase))
                            && (!hasExpectedRoles
                                || expectedRoles.All(role =>
                                    ReadStringList(
                                        authority,
                                        "recognizedRoles")
                                    .Contains(
                                        role,
                                        StringComparer.OrdinalIgnoreCase)))
                            && (!hasExpectedEnemyCount
                                || ReadInt(
                                    authority,
                                    "currentEnemyKingdomCount",
                                    -1) == expectedEnemyCount);
                        bool verified = ReadBool(
                            identity,
                            "canonicalNameAllowed",
                            false);
                        bool publicOfficeKnown = ReadBool(
                            authority,
                            "publicOfficeKnown",
                            false);
                        bool authorityInternallyConsistent =
                            ReadBool(
                                authority,
                                "identityVerified",
                                false) == verified
                            && (verified || publicOfficeKnown
                                ? ReadStringList(
                                    authority,
                                    "recognizedRoles").Count > 0
                                    && !ReadString(
                                        authority,
                                        "source",
                                        "").Equals(
                                            "identity_not_verified",
                                            StringComparison.OrdinalIgnoreCase)
                                : ReadStringList(
                                    authority,
                                    "recognizedRoles").Count == 0
                                    && ReadString(
                                        authority,
                                        "authorityRelationship",
                                        "").Equals(
                                            "identity_unverified",
                                            StringComparison.OrdinalIgnoreCase))
                            && (!ReadBool(
                                    authority,
                                    "subjectIsObserverSovereign",
                                    false)
                                || (ReadBool(
                                        authority,
                                        "sameKingdom",
                                        false)
                                    && ReadBool(
                                        authority,
                                        "subjectIsRuler",
                                        false)
                                    && (verified || publicOfficeKnown)))
                            && (!ReadBool(
                                    authority,
                                    "observerClanOwnsCurrentSettlement",
                                    false)
                                || (!string.IsNullOrWhiteSpace(
                                        ReadString(
                                            authority,
                                            "observerClanId",
                                            ""))
                                    && ReadString(
                                        authority,
                                        "observerClanId",
                                        "").Equals(
                                            ReadString(
                                                authority,
                                                "currentSettlementOwnerClanId",
                                                ""),
                                            StringComparison.OrdinalIgnoreCase)))
                            && ReadInt(
                                authority,
                                "currentEnemyKingdomCount",
                                -1) >= 0;
                        Dictionary<string, object> parsed =
                            LiveTestCurrentModelOutput(raw);
                        return fieldsMatch
                            && authorityInternallyConsistent
                            && !staleLabel
                            && !DialogueService.VerifiedIdentityGroundingContradiction(
                                parsed,
                                new Dictionary<string, object>
                                {
                                    ["identityView"] = identity,
                                    ["latestPlayerText"] = ReadString(
                                        command, "text", "")
                                });
                    });
                record(
                    "requiresPoliticalAuthorityEvidence",
                    true,
                    validAuthority,
                    validAuthority
                        ? "Every reply preserved internally consistent current native roles and political authority, honored any expected fixture values, and avoided the stale stranger label."
                        : "A reply omitted, contradicted, or internally mismatched current native identity/authority evidence, failed an expected fixture value, or reused a stale stranger label.");
            }
            if (ReadBool(requested,
                    "requiresPoliticalConductEvidence", false))
            {
                int expectedMaximum = ReadInt(requested,
                    "expectedMaximumDefianceTier", -1);
                string expectedAuthorityClass = ReadString(requested,
                    "expectedPoliticalAuthorityClass", "");
                string expectedAddressMode = ReadString(requested,
                    "expectedRequiredAddressMode", "");
                bool validConduct = replies.Count > 0
                    && replies.All(reply =>
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse") ?? reply;
                        Dictionary<string, object> posture =
                            ReadDictionary(raw, "politicalRiskPosture")
                            ?? ReadDictionary(reply,
                                "politicalRiskPosture")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> conduct =
                            ReadDictionary(raw, "politicalConduct")
                            ?? ReadDictionary(reply, "politicalConduct")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> enforcement =
                            ReadDictionary(raw,
                                "politicalConductEnforcement")
                            ?? ReadDictionary(reply,
                                "politicalConductEnforcement")
                            ?? new Dictionary<string, object>();
                        int maximum = ReadInt(posture,
                            "maximumDefianceTier", -1);
                        int observed = ReadInt(conduct,
                            "defianceTier", -1);
                        return ReadString(posture, "model", "")
                                == DialogueService.PoliticalRiskModel
                            && maximum >= 0 && observed >= 0
                            && observed <= maximum
                            && ReadStringList(conduct,
                                "violations").Count == 0
                            && !string.IsNullOrWhiteSpace(ReadString(
                                enforcement, "outcome", ""))
                            && (expectedMaximum < 0
                                || maximum == expectedMaximum)
                            && (string.IsNullOrWhiteSpace(
                                    expectedAuthorityClass)
                                || ReadString(posture,
                                    "authorityClass", "").Equals(
                                        expectedAuthorityClass,
                                        StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(
                                    expectedAddressMode)
                                || ReadString(posture,
                                    "requiredAddressMode", "").Equals(
                                        expectedAddressMode,
                                        StringComparison.OrdinalIgnoreCase));
                    });
                record("requiresPoliticalConductEvidence", true,
                    validConduct,
                    validConduct
                        ? "Every reply carried server-observed political posture and conduct evidence within its computed defiance ceiling."
                        : "A reply lacked political posture/conduct evidence, exceeded its computed defiance ceiling, retained a visible violation, or mismatched an expected posture value.");
            }
            foreach (string phrase in ReadStringList(requested, "replyContains"))
                record("replyContains", phrase, combined.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0,
                    "Combined reply text: " + combined);
            foreach (string phrase in ReadStringList(requested, "replyExcludes"))
                record("replyExcludes", phrase, combined.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0,
                    "Combined reply text: " + combined);
            if (ReadString(
                    requested,
                    "visiblePlayerTextPolicy", "")
                .Equals(
                    "in_world_roleplay_only",
                    StringComparison.OrdinalIgnoreCase))
            {
                string playerText = ReadString(command, "text", "");
                bool adversarial = ReadStringList(
                        requested,
                        "representedRequirementIds")
                    .Any(id => id.StartsWith(
                        "ADV-", StringComparison.OrdinalIgnoreCase));
                string[] harnessPhrases =
                {
                    "production conversation path",
                    "mapped requirement",
                    "behavioral requirement",
                    "respond naturally",
                    "speak with me naturally",
                    "this fixture",
                    "this scenario",
                    "semantic rubric",
                    "test harness",
                    "explain the test"
                };
                string contamination = harnessPhrases.FirstOrDefault(
                    phrase => playerText.IndexOf(
                        phrase,
                        StringComparison.OrdinalIgnoreCase) >= 0);
                if (!adversarial
                    && playerText.IndexOf(
                        "this test",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    contamination = "this test";
                record(
                    "visiblePlayerTextPolicy",
                    "in_world_roleplay_only",
                    string.IsNullOrWhiteSpace(contamination),
                    string.IsNullOrWhiteSpace(contamination)
                        ? "The player turn is written as in-world role-play."
                        : "Harness language leaked into the visible player turn: "
                            + contamination);
            }
            if (requested.ContainsKey("requiresGroupAwareness"))
            {
                bool required = ReadBool(requested, "requiresGroupAwareness", false);
                bool aware = !required || LiveTestRepliesShowGroupAwareness(replies);
                record("requiresGroupAwareness", required, aware,
                    string.Join(" | ", replies.Select(reply => FirstNonEmpty(ReadString(reply, "heroName", ""), ReadString(reply, "heroId", ""))
                        + " -> " + ReadString(reply, "reactionTargetHeroStringId", "") + ": " + ReadString(reply, "text", ""))));
            }
            if (requested.ContainsKey("requiresGroupDivergence"))
            {
                bool required = ReadBool(
                    requested, "requiresGroupDivergence", false);
                string divergenceEvidence = "Not required.";
                bool distinct = !required
                    || LiveTestRepliesShowDistinctAgency(
                        replies, out divergenceEvidence);
                record("requiresGroupDivergence", required, distinct,
                    divergenceEvidence);
            }
            if (requested.ContainsKey("requiresActionCompletionGrounding"))
            {
                bool required = ReadBool(
                    requested, "requiresActionCompletionGrounding", false);
                List<string> premature = replies
                    .Select(reply => ReadString(reply, "text", ""))
                    .Select(text => new
                    {
                        Text = text,
                        Claims = DialogueService.VisibleReplyClaimsCompletedWorldAction(
                            text, out string match),
                        Match = match
                    })
                    .Where(item => item.Claims)
                    .Select(item => item.Match)
                    .ToList();
                record(
                    "requiresActionCompletionGrounding",
                    required,
                    !required || premature.Count == 0,
                    premature.Count == 0
                        ? "No reply narrated an unexecuted physical or world action as completed."
                        : "Premature completion claims: "
                            + string.Join(" | ", premature));
            }
            run["assertions"] = rows;
            run["failures"] = failures;
            if (failures.Any(row => string.Equals(ReadString(row, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string correlationId in ReadStringList(command, "correlationIds"))
                    DialogueService.ScheduleReplayCorpusCapture(ReadString(run, "campaignId", ""), correlationId);
            }
        }

        private static string LiveTestRetrievedEvidenceText(
            IEnumerable<Dictionary<string, object>> contextRows)
        {
            return string.Join(
                "\n",
                (contextRows ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(row =>
                {
                    Dictionary<string, object> data =
                        ReadDictionary(row, "data")
                        ?? new Dictionary<string, object>();
                    return string.Join(
                        "\n",
                        ReadDictionaryList(data, "memories")
                            .Select(item => Json.Serialize(item))
                            .Concat(ReadDictionaryList(data, "priorLines")
                                .Select(item => Json.Serialize(item)))
                            .Concat(new[]
                            {
                                ReadString(data, "memorySummary", "")
                            }));
                }));
        }

        private static Dictionary<string, object>
            LiveTestCurrentModelOutput(
                Dictionary<string, object> raw)
        {
            raw = raw ?? new Dictionary<string, object>();
            Dictionary<string, object> parsed =
                ReadDictionary(raw, "parsed");
            if (parsed != null && parsed.Count > 0)
                return parsed;

            // The server response envelope also contains retrieved memories,
            // the shared scene transcript, prompt evidence, and native context.
            // Those are inputs to the current answer, not model output. Feeding
            // that entire envelope to the contradiction checker can mistake an
            // earlier "stranger" line for a contradiction in a later, correct
            // identity/authority response.
            Dictionary<string, object> current =
                new Dictionary<string, object>();
            string[] generatedFields =
            {
                "reply", "response", "text", "content",
                "decisionBrief", "decision_brief",
                "memoryWrites", "beliefWrites", "obligationWrites",
                "comprehensionWrites", "dynamicCharacteristicWrites",
                "stateUpdates", "identityIntroductions"
            };
            foreach (string field in generatedFields)
            {
                if (raw.TryGetValue(field, out object value)
                    && value != null)
                    current[field] = value;
            }
            return current;
        }

        private static bool LiveTestSceneMemoryArtifactsAreValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            out string evidence)
        {
            string campaignId = ReadString(run, "campaignId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            string sessionId = ReadString(result, "sessionId", "");
            string reportedSummaryId = ReadString(result, "sceneSummaryId", "");
            if (string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(sessionId)
                || string.IsNullOrWhiteSpace(reportedSummaryId))
            {
                evidence = "campaign=" + campaignId + "; session=" + sessionId
                    + "; reportedSummary=" + reportedSummaryId;
                return false;
            }

            try
            {
                Dictionary<string, object> session;
                Dictionary<string, object> summary;
                int turnCount;
                int distinctExchanges;
                int sourceLinks;
                int ftsRows;
                int embeddingJobs;
                int consolidationJobs;
                int relatedScenes;
                int rollingArcs;
                int rollingArcSources;
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    session = Program.QuerySql(connection,
                        "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                    string storedSummaryId = ReadString(session, "scene_summary_id", "");
                    summary = Program.QuerySql(connection,
                        "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault();
                    turnCount = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id AND status='active';",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    distinctExchanges = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(DISTINCT exchange_id) count FROM conversation_turns WHERE session_id=$id AND status='active' AND exchange_id<>'';",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    sourceLinks = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault(), "count", 0);
                    ftsRows = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault(), "count", 0);
                    embeddingJobs = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault(), "count", 0);
                    consolidationJobs = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_background_jobs WHERE job_type='memory_consolidation' AND session_id=$id;",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);

                    string ownerId = ReadString(summary, "owner_id", "");
                    string playerId = ReadString(session, "player_id", "");
                    string lane = ReadString(summary, "memory_lane", "");
                    relatedScenes = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) count FROM summaries
WHERE owner_id=$owner AND summary_type='scene' AND status='active' AND memory_lane=$lane
AND ($player='' OR participants_json LIKE $playerLike);",
                        new Dictionary<string, object>
                        {
                            ["owner"] = ownerId, ["lane"] = lane, ["player"] = playerId,
                            ["playerLike"] = "%" + playerId + "%"
                        }).FirstOrDefault(), "count", 0);
                    Dictionary<string, object> arc = Program.QuerySql(connection, @"SELECT summary_id FROM summaries
WHERE owner_id=$owner AND summary_type='arc' AND status='active' AND memory_lane=$lane
AND ($player='' OR participants_json LIKE $playerLike)
ORDER BY updated_ts DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["owner"] = ownerId, ["lane"] = lane, ["player"] = playerId,
                            ["playerLike"] = "%" + playerId + "%"
                        }).FirstOrDefault();
                    string arcId = ReadString(arc, "summary_id", "");
                    rollingArcs = string.IsNullOrWhiteSpace(arcId) ? 0 : 1;
                    rollingArcSources = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id AND source_type='summary';",
                        new Dictionary<string, object> { ["id"] = arcId }).FirstOrDefault(), "count", 0);
                }

                List<string> participants = Program.TextListFromJson(ReadString(session, "participants_json", "[]"));
                string player = ReadString(session, "player_id", "");
                List<string> nonPlayerParticipants = participants
                    .Where(id => !string.IsNullOrWhiteSpace(id)
                        && !id.Equals(player, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> knownBy = Program.TextListFromJson(ReadString(summary, "known_by_json", "[]"));
                string summaryId = ReadString(summary, "summary_id", "");
                bool semanticEnabled = ReadBool(Program.LoadSettings(), "enableSemanticMemory", true);
                Dictionary<string, object> memoryResult = ReadDictionary(result, "memory")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> arcResult = ReadDictionary(memoryResult, "arc")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> sceneResult = ReadDictionary(memoryResult, "scene")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> knowledgeBoundary = ReadDictionary(sceneResult, "knowledgeBoundary")
                    ?? new Dictionary<string, object>();
                int identityBoundaryWithheldScenes = ReadInt(
                    arcResult, "identityBoundaryWithheldScenes", 0);
                bool playerIdentityKnown = ReadBool(
                    knowledgeBoundary, "playerIdentityKnown", true);
                int arcEligibleScenes = Math.Max(
                    0, relatedScenes - identityBoundaryWithheldScenes);
                bool identityBoundaryArcExemption =
                    !playerIdentityKnown
                    && identityBoundaryWithheldScenes > 0
                    && arcEligibleScenes < 4
                    && rollingArcs == 0;
                bool rollingArcRequirementSatisfied =
                    LiveTestRollingArcRequirementIsSatisfied(
                        relatedScenes,
                        identityBoundaryWithheldScenes,
                        playerIdentityKnown,
                        rollingArcs,
                        rollingArcSources);
                bool valid = session != null
                    && !ReadString(session, "status", "open").Equals("open", StringComparison.OrdinalIgnoreCase)
                    && summary != null
                    && summaryId.Equals(reportedSummaryId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(summary, "summary_type", "").Equals("scene", StringComparison.OrdinalIgnoreCase)
                    && DialogueService.MemoryLaneIds.Contains(ReadString(summary, "memory_lane", ""), StringComparer.OrdinalIgnoreCase)
                    && turnCount > 0
                    && ReadInt(summary, "event_count", 0) == distinctExchanges
                    && sourceLinks >= turnCount + 1
                    && ftsRows == 1
                    && ReadString(summary, "visibility", "").Equals("private", StringComparison.OrdinalIgnoreCase)
                    && participants.Contains(ReadString(summary, "owner_id", ""), StringComparer.OrdinalIgnoreCase)
                    && nonPlayerParticipants.All(id => knownBy.Contains(id, StringComparer.OrdinalIgnoreCase))
                    && consolidationJobs >= nonPlayerParticipants.Count
                    && (!semanticEnabled
                        || embeddingJobs >= 1
                        || ReadString(summary, "embedding_status", "").Equals("indexed", StringComparison.OrdinalIgnoreCase))
                    && rollingArcRequirementSatisfied
                    && (!identityBoundaryArcExemption
                        || ReadString(arcResult, "reason", "")
                            .Equals("fewer_than_four_related_scenes",
                                StringComparison.OrdinalIgnoreCase));

                evidence = "session=" + sessionId
                    + "; summary=" + summaryId
                    + "; turns=" + turnCount
                    + "; exchanges=" + distinctExchanges
                    + "; sources=" + sourceLinks
                    + "; fts=" + ftsRows
                    + "; embeddingJobs=" + embeddingJobs
                    + "; consolidationJobs=" + consolidationJobs + "/" + nonPlayerParticipants.Count
                    + "; relatedScenes=" + relatedScenes
                    + "; identityBoundaryWithheldScenes=" + identityBoundaryWithheldScenes
                    + "; arcEligibleScenes=" + arcEligibleScenes
                    + "; playerIdentityKnown=" + playerIdentityKnown
                    + "; rollingArc=" + rollingArcs
                    + "; rollingArcSources=" + rollingArcSources;
                return valid;
            }
            catch (Exception ex)
            {
                evidence = "Memory artifact inspection failed: " + Program.LimitText(ex.Message, 900);
                return false;
            }
        }

        private static bool LiveTestRollingArcRequirementIsSatisfied(
            int relatedScenes,
            int identityBoundaryWithheldScenes,
            bool playerIdentityKnown,
            int rollingArcs,
            int rollingArcSources)
        {
            int withheld = Math.Max(0, identityBoundaryWithheldScenes);
            if (withheld > 0 && playerIdentityKnown)
                return false;
            int eligibleScenes = Math.Max(0, relatedScenes - withheld);
            return eligibleScenes < 4
                || (rollingArcs == 1 && rollingArcSources >= 4);
        }

        private static bool LiveTestQualificationMemoryCompletionIsValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            out string evidence)
        {
            string campaignId = ReadString(run, "campaignId", "");
            string qualificationId = ReadString(run, "qualificationId", "");
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(qualificationId))
            {
                evidence = "campaign=" + campaignId + "; qualification=" + qualificationId;
                return false;
            }

            try
            {
                List<Dictionary<string, object>> qualificationRuns =
                    DialogueService.LoadConversationReadinessRuns(campaignId, qualificationId);
                List<string> sessionIds = qualificationRuns
                    .SelectMany(item => ReadDictionaryList(item, "commands"))
                    .Where(item =>
                        ReadString(item, "operation", "").Equals("close", StringComparison.OrdinalIgnoreCase)
                        && ReadString(item, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase))
                    .Select(item => ReadString(ReadDictionary(item, "result"), "sessionId", ""))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sessionIds.Count < 100)
                {
                    evidence = "Only " + sessionIds.Count + " completed production sessions were available; 100 are required.";
                    return false;
                }

                Dictionary<string, object> settings = Program.LoadSettings();
                bool semanticEnabled = ReadBool(settings, "enableSemanticMemory", true);
                int minimumItems = Math.Max(2, Math.Min(20,
                    ReadInt(settings, "memoryConsolidationMinItems", 4)));
                List<string> summaryIds = new List<string>();
                List<string> errors = new List<string>();
                int jobCount = 0;
                int completedJobs = 0;
                int eligibleConsolidations = 0;
                int verifiedConsolidations = 0;

                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    DialogueService.EnsureMemoryBackgroundWorkSchema(connection);
                    DialogueService.EnsureSemanticMemorySchema(connection);
                    foreach (string sessionId in sessionIds)
                    {
                        Dictionary<string, object> session = Program.QuerySql(connection,
                            "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                            new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                        string sceneId = ReadString(session, "scene_summary_id", "");
                        if (string.IsNullOrWhiteSpace(sceneId))
                        {
                            errors.Add(sessionId + ": scene summary missing");
                            continue;
                        }
                        summaryIds.Add(sceneId);
                        List<Dictionary<string, object>> jobs = Program.QuerySql(connection, @"SELECT * FROM memory_background_jobs
WHERE job_type='memory_consolidation' AND session_id=$session ORDER BY owner_id;",
                            new Dictionary<string, object> { ["session"] = sessionId });
                        jobCount += jobs.Count;
                        foreach (Dictionary<string, object> job in jobs)
                        {
                            string ownerId = ReadString(job, "owner_id", "");
                            string status = ReadString(job, "status", "");
                            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                                completedJobs++;
                            else
                            {
                                errors.Add(sessionId + "/" + ownerId + ": consolidation job " + status
                                    + (string.IsNullOrWhiteSpace(ReadString(job, "last_error", ""))
                                        ? "" : " (" + Program.LimitText(ReadString(job, "last_error", ""), 160) + ")"));
                                continue;
                            }

                            int sourceEventCount = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(DISTINCT m.event_id) count
FROM memories m JOIN conversation_turns t ON t.event_id=m.event_id
WHERE t.session_id=$session AND m.owner_id=$owner AND m.event_id<>'';",
                                new Dictionary<string, object>
                                {
                                    ["session"] = sessionId, ["owner"] = ownerId
                                }).FirstOrDefault(), "count", 0);
                            if (sourceEventCount < minimumItems) continue;
                            eligibleConsolidations++;
                            Dictionary<string, object> middle = Program.QuerySql(connection, @"SELECT * FROM summaries
WHERE scope=$scope AND owner_id=$owner AND summary_type='middle_term'
ORDER BY updated_ts DESC LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["scope"] = "conversation:" + sessionId, ["owner"] = ownerId
                                }).FirstOrDefault();
                            string middleId = ReadString(middle, "summary_id", "");
                            if (string.IsNullOrWhiteSpace(middleId))
                            {
                                errors.Add(sessionId + "/" + ownerId + ": eligible middle-term summary missing");
                                continue;
                            }
                            Dictionary<string, object> middlePayload =
                                TryParseJsonObject(ReadString(middle, "payload_json", "{}"))
                                ?? new Dictionary<string, object>();
                            List<string> sourceMemoryIds = ReadStringList(middlePayload, "sourceMemoryIds");
                            int memoryLinks = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='memory';",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int eventLinks = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='event';",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int unconsolidated = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) count
FROM memory_sources s JOIN memories m ON m.memory_id=s.source_id
WHERE s.document_type='summary' AND s.document_id=$id AND s.source_type='memory'
AND m.status<>'consolidated';",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int fts = ReadInt(Program.QuerySql(connection,
                                "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int embeddingJobs = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) count FROM embedding_jobs
WHERE source_type='summary' AND source_id=$id;",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            bool valid = ReadInt(middle, "event_count", 0) >= minimumItems
                                && Program.TextListFromJson(ReadString(middle, "source_events_json", "[]")).Count >= minimumItems
                                && sourceMemoryIds.Count > 0
                                && memoryLinks == sourceMemoryIds.Count
                                && eventLinks == Program.TextListFromJson(ReadString(middle, "source_events_json", "[]")).Count
                                && unconsolidated == 0
                                && fts == 1
                                && (!semanticEnabled
                                    || embeddingJobs >= 1
                                    || ReadString(middle, "embedding_status", "")
                                        .Equals("indexed", StringComparison.OrdinalIgnoreCase));
                            if (!valid)
                            {
                                errors.Add(sessionId + "/" + ownerId
                                    + ": middle-term lineage/index invalid (mem="
                                    + memoryLinks + "/" + sourceMemoryIds.Count
                                    + ", events=" + eventLinks
                                    + ", fts=" + fts + ", embedding=" + embeddingJobs + ")");
                                continue;
                            }
                            summaryIds.Add(middleId);
                            verifiedConsolidations++;
                        }
                    }

                    List<string> sceneIds = summaryIds
                        .Where(id => id.StartsWith("scene_", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (Dictionary<string, object> arc in Program.QuerySql(connection,
                        "SELECT summary_id FROM summaries WHERE summary_type='arc' AND status='active';"))
                    {
                        string arcId = ReadString(arc, "summary_id", "");
                        int matchedSources = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='summary'
AND source_id IN (" + string.Join(",", sceneIds.Select((id, index) => "$scene" + index)) + ");",
                            sceneIds.Select((id, index) => new KeyValuePair<string, object>(
                                "scene" + index, id)).ToDictionary(pair => pair.Key, pair => pair.Value))
                            .FirstOrDefault(), "count", 0);
                        if (matchedSources > 0) summaryIds.Add(arcId);
                    }
                }

                summaryIds = summaryIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Dictionary<string, object> semantic = semanticEnabled
                    ? DialogueService.WaitForNpcDialogueAuditEmbeddings(campaignId, summaryIds, 12000)
                    : new Dictionary<string, object>
                    {
                        ["disabled"] = true, ["expected"] = summaryIds.Count,
                        ["indexed"] = summaryIds.Count, ["retrievalApplied"] = true,
                        ["retrievedExpectedSummary"] = true, ["workerAvailable"] = true
                    };
                bool semanticValid = !semanticEnabled
                    || (ReadBool(semantic, "workerAvailable", false)
                        && ReadInt(semantic, "indexed", 0) == summaryIds.Count
                        && ReadBool(semantic, "retrievalApplied", false)
                        && ReadBool(semantic, "retrievedExpectedSummary", false));
                bool validOverall = errors.Count == 0
                    && jobCount > 0
                    && completedJobs == jobCount
                    && verifiedConsolidations == eligibleConsolidations
                    && semanticValid;
                evidence = "sessions=" + sessionIds.Count
                    + "; jobs=" + completedJobs + "/" + jobCount
                    + "; eligibleMiddleTerm=" + eligibleConsolidations
                    + "; verifiedMiddleTerm=" + verifiedConsolidations
                    + "; summaries=" + summaryIds.Count
                    + "; semantic=" + ReadInt(semantic, "indexed", 0)
                    + "/" + ReadInt(semantic, "expected", summaryIds.Count)
                    + "; retrieval=" + ReadBool(semantic, "retrievedExpectedSummary", false)
                    + (errors.Count == 0 ? "" : "; errors=" + string.Join(" | ", errors.Take(6)));
                return validOverall;
            }
            catch (Exception ex)
            {
                evidence = "Qualification memory completion inspection failed: "
                    + Program.LimitText(ex.Message, 900);
                return false;
            }
        }
    }
}
