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
        internal static List<Dictionary<string, object>> BuildRebellionDialogueCandidates(
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply,
            Dictionary<string, object> actionGate)
        {
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            string playerHeroId = ReadFirstString(payload, "playerHeroStringId", "playerId", "mainHeroStringId");
            string playerKingdomId = ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId");
            string playerClanId = ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId");
            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string speakerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "targetClanId", "targetClanStringId"),
                ReadString(hero, "clanId", ""));
            bool accepted = IsUnambiguousActionAcceptance(visibleReply);

            // An explicit declaration is unilateral. It must not be reclassified as
            // an answer to a summons merely because the same language can express
            // defiance; the ruler's visible refusal cannot veto the declaration.
            if (LooksLikeExplicitPlayerRebellionDeclaration(playerText))
            {
                candidates.Add(BuildRebellionActionCandidate(
                    "start_ruling_clan_rebellion", playerHeroId, playerKingdomId, playerClanId,
                    speakerHeroId, speakerKingdomId, speakerClanId,
                    "The player explicitly challenged the current ruler and declared rebellion in person."));
                return candidates;
            }

            string summonsResponse = RebellionService.ClassifyRebellionSummonsReply(playerText);
            string summonsVerdict = RebellionService.ClassifyRebellionRulerVerdict(visibleReply);
            if (!string.IsNullOrWhiteSpace(summonsResponse)
                && (summonsResponse == "defy" || !string.IsNullOrWhiteSpace(summonsVerdict)))
            {
                Dictionary<string, object> summons = BuildRebellionActionCandidate(
                    "resolve_rebellion_summons", playerHeroId, playerKingdomId, playerClanId,
                    speakerHeroId, speakerKingdomId, speakerClanId,
                    "The player explicitly answered the ruler's delivered rebellion summons.");
                summons["terms"] = new Dictionary<string, object>
                {
                    ["playerResponse"] = summonsResponse,
                    ["rulerVerdict"] = summonsVerdict
                };
                candidates.Add(summons);
                return candidates;
            }

            List<Dictionary<string, object>> preparation =
                RebellionService.BuildRebellionPreparationDialogueCandidates(payload, hero, playerText, visibleReply,
                    actionGate);
            if (preparation.Count > 0) return preparation;

            if (LooksLikeExplicitPlayerRebellionSurrender(playerText))
            {
                candidates.Add(BuildRebellionActionCandidate(
                    "surrender_rebellion", playerHeroId, playerKingdomId, playerClanId,
                    speakerHeroId, speakerKingdomId, speakerClanId,
                    "The player explicitly surrendered their leadership role in the rebellion."));
            }
            else if (LooksLikeRebellionRecruitmentRequest(playerText) && accepted)
            {
                candidates.Add(BuildRebellionActionCandidate(
                    "recruit_lord_to_rebellion", playerHeroId, playerKingdomId, playerClanId,
                    speakerHeroId, speakerKingdomId, speakerClanId,
                    "The current lord unambiguously accepted recruitment into the player's active rebellion."));
            }
            else if (LooksLikePlayerJoinRebellionRequest(playerText) && accepted)
            {
                candidates.Add(BuildRebellionActionCandidate(
                    "join_rebellion", playerHeroId, playerKingdomId, playerClanId,
                    speakerHeroId, speakerKingdomId, speakerClanId,
                    "The current rebel leader unambiguously accepted the player clan into the rebellion."));
            }
            else if (LooksLikeRebellionSurrenderDemand(playerText)
                && LooksLikeExplicitNpcRebellionSurrender(visibleReply))
            {
                candidates.Add(BuildRebellionActionCandidate(
                    "surrender_rebellion", speakerHeroId, speakerKingdomId, speakerClanId,
                    playerHeroId, playerKingdomId, playerClanId,
                    "The current named leader explicitly surrendered the rebellion."));
            }

            return candidates;
        }

        internal static List<Dictionary<string, object>> BuildGovernmentReductionConsentDialogueCandidates(
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply,
            Dictionary<string, object> actionGate)
        {
            var candidates = new List<Dictionary<string, object>>();
            string speakerClanName = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanName", "targetClanName"),
                ReadString(hero, "clanName", ""));
            if (!LooksLikeGovernmentReductionConsentRequest(playerText)
                || !IsUnambiguousGovernmentReductionConsent(visibleReply, speakerClanName))
                return candidates;

            string playerHeroId = ReadFirstString(payload,
                "playerHeroStringId", "playerId", "mainHeroStringId");
            string playerKingdomId = ReadFirstString(payload,
                "playerKingdomId", "actorKingdomId", "actorKingdomStringId");
            string playerClanId = ReadFirstString(payload,
                "playerClanId", "actorClanId", "actorClanStringId");
            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string speakerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "targetClanId", "targetClanStringId"),
                ReadString(hero, "clanId", ""));
            candidates.Add(new Dictionary<string, object>
            {
                ["command"] = "consent_government_reduction",
                ["source"] = "government_reduction_explicit_consent",
                ["requiresAcceptance"] = false,
                ["actorHeroId"] = speakerHeroId ?? string.Empty,
                ["actorKingdomId"] = speakerKingdomId ?? string.Empty,
                ["actorClanId"] = speakerClanId ?? string.Empty,
                ["TargetHero"] = playerHeroId ?? string.Empty,
                ["targetKingdomId"] = playerKingdomId ?? string.Empty,
                ["targetClanId"] = playerClanId ?? string.Empty,
                ["reason"] = "The current NPC explicitly consented after the ruler disclosed the planned government-authority reduction.",
                ["terms"] = new Dictionary<string, object> { ["consentConfirmed"] = true }
            });
            return candidates;
        }

        private static bool LooksLikeGovernmentReductionConsentRequest(string text)
        {
            string q = NormalizeLookup(text);
            bool namesGovernment = DirectiveHasAny(q, "government", "senate", "council", "chamber",
                "veche", "oenach", "majlis", "kurultai", "thing", "assembly");
            bool namesReduction = DirectiveHasAny(q, "reduce its power", "reduce their power",
                "reduce government power", "reduce government authority", "lower government power",
                "lower government authority", "reduce government control", "lower government control",
                "take power from", "take authority from", "take control from",
                "cut its power", "cut their power", "small reduction", "limited reduction",
                "small limited reduction", "one level less", "lower it one level")
                || (DirectiveHasAny(q, "reduce", "reduction", "lower", "cut", "take", "remove", "strip")
                    && DirectiveHasAny(q, "power", "authority", "control", "one level"));
            bool asksConsent = DirectiveHasAny(q, "do you agree", "will you agree", "do i have your consent",
                "will you consent", "can i count on your consent", "will you support", "are you with me")
                || (DirectiveHasAny(q, "consent", "support")
                    && DirectiveHasAny(q, "will you", "will your clan", "do you", "does your clan",
                        "can i count on", "are you", "is your clan"));
            return namesGovernment && namesReduction && asksConsent;
        }

        private static bool IsUnambiguousGovernmentReductionConsent(string text, string speakerClanName)
        {
            string q = NormalizeLookup(text);
            string normalizedClanName = NormalizeLookup(speakerClanName);
            bool explicitPresentConsent = DirectiveHasAny(q,
                "i consent now", "we consent now", "i am consenting", "we are consenting",
                "you have my consent", "you have our consent");
            bool futureConsequenceWarning = DirectiveHasAny(q,
                "my next answer", "our next answer", "next time", "in the future");
            bool leadingFutureWarning = q.StartsWith("if ", StringComparison.Ordinal)
                && explicitPresentConsent && futureConsequenceWarning;
            bool unresolvedBargain = DirectiveHasAny(q,
                "give me that and you have my consent", "give us that and you have our consent",
                "give me this and you have my consent", "give us this and you have our consent",
                "do that and you have my consent", "do that and you have our consent",
                "grant that and you have my consent", "grant that and you have our consent",
                "guarantee that and you have my consent", "guarantee that and you have our consent",
                "settle that with me", "settle this with me")
                || (DirectiveHasAny(q, "here s what i need", "here is what i need", "what i need")
                    && DirectiveHasAny(q, "give me that", "give us that", "settle that",
                        "then you have my consent", "then you have our consent"));
            bool conditional = (q.StartsWith("if ", StringComparison.Ordinal)
                    && !leadingFutureWarning)
                || DirectiveHasAny(q, "i consent if", "we consent if", "consents if", "agree if",
                    "agrees if", "support if", "supports if", "consent provided", "consents provided",
                    "agree provided", "support provided", "provided that", "provided you", "on condition",
                    "only if", "once you", "after you", "when you have")
                || unresolvedBargain;
            bool namedSpeakerClanConsent = !string.IsNullOrWhiteSpace(normalizedClanName)
                && Regex.IsMatch(q,
                    @"(?:^|\s)(?:house\s+)?" + Regex.Escape(normalizedClanName)
                    + @"\s+consents(?:\s|$)",
                    RegexOptions.CultureInvariant);
            bool explicitConsent = DirectiveHasAny(q, "i agree", "we agree", "we are agreed",
                "i consent", "we consent", "i am consenting", "i m consenting", "we are consenting",
                "you have my consent", "you have our consent",
                "you've got my consent", "you've got our consent", "you've got your consent",
                "you got my consent", "you got our consent", "you got your consent",
                "consent is yours", "consent is granted", "your consent is granted",
                "you have my support", "you have our support", "my clan supports this",
                "our clan supports this")
                || namedSpeakerClanConsent;
            bool deniedOrTentative = DirectiveHasAny(q, "do not consent", "does not consent",
                "will not consent", "would not consent", "no one consents", "nobody consents",
                "none consent", "might consent", "may consent", "could consent", "perhaps consent",
                "maybe consent", "i refuse", "we refuse", "i decline", "we decline",
                "i do not agree", "i don't agree", "we do not agree", "we don't agree",
                "before i agree", "before agreeing", "before i consent", "before consenting");
            return !conditional
                && !DirectiveHasAny(q, "agree to nothing", "consent to nothing", "no support")
                && !deniedOrTentative
                && explicitConsent;
        }

        internal static Dictionary<string, object> BuildRebellionActionCandidate(
            string command,
            string actorHeroId,
            string actorKingdomId,
            string actorClanId,
            string targetHeroId,
            string targetKingdomId,
            string targetClanId,
            string reason)
        {
            return new Dictionary<string, object>
            {
                ["command"] = command,
                ["source"] = "rebellion_explicit_commitment",
                ["requiresAcceptance"] = false,
                ["actorHeroId"] = actorHeroId ?? string.Empty,
                ["actorKingdomId"] = actorKingdomId ?? string.Empty,
                ["actorClanId"] = actorClanId ?? string.Empty,
                ["TargetHero"] = targetHeroId ?? string.Empty,
                ["targetKingdomId"] = targetKingdomId ?? string.Empty,
                ["targetClanId"] = targetClanId ?? string.Empty,
                ["reason"] = reason ?? "Explicit rebellion action."
            };
        }

        internal static bool LooksLikeExplicitPlayerRebellionDeclaration(string text)
        {
            string q = NormalizeLookup(text);
            if (q.Contains("should i") || q.Contains("could i") || q.Contains("would i")
                || q.Contains("might declare") || q.Contains("may declare") || q.Contains("someday"))
                return false;
            return (q.Contains("i declare") || q.Contains("we declare") || q.Contains("i am declaring")
                    || q.Contains("i hereby declare"))
                && (q.Contains("rebellion") || q.Contains("revolt"))
                || (q.Contains("i challenge your rule") && (q.Contains("rebel") || q.Contains("rebellion")));
        }

        internal static bool LooksLikeExplicitPlayerRebellionSurrender(string text)
        {
            string q = NormalizeLookup(text);
            return (q.Contains("i surrender") || q.Contains("we surrender") || q.Contains("i yield"))
                && (q.Contains("rebellion") || q.Contains("rebel") || q.Contains("your rule") || q.Contains("the throne"));
        }

        internal static bool LooksLikeRebellionRecruitmentRequest(string text)
        {
            string q = NormalizeLookup(text);
            return q.Contains("join my rebellion") || q.Contains("join our rebellion")
                || q.Contains("support my rebellion") || q.Contains("fight for my rebellion")
                || q.Contains("join me against the ruler");
        }

        internal static bool LooksLikePlayerJoinRebellionRequest(string text)
        {
            string q = NormalizeLookup(text);
            return q.Contains("join your rebellion") || q.Contains("fight for your rebellion")
                || q.Contains("join the rebels") || q.Contains("join you against the ruler");
        }

        internal static bool LooksLikeRebellionSurrenderDemand(string text)
        {
            string q = NormalizeLookup(text);
            return q.Contains("surrender the rebellion") || q.Contains("yield the rebellion")
                || q.Contains("give up the rebellion") || q.Contains("surrender your claim");
        }

        internal static bool LooksLikeExplicitNpcRebellionSurrender(string text)
        {
            string q = NormalizeLookup(text);
            return q.Contains("i surrender") || q.Contains("we surrender") || q.Contains("i yield")
                || q.Contains("i give up my claim") || q.Contains("i relinquish my claim");
        }

        internal static bool IsUnambiguousActionAcceptance(string text)
        {
            string q = NormalizeLookup(text);
            return !ContainsAnyPhrase(q, DialogueActionBlockingPhrases())
                && (ContainsAnyPhrase(q, DialogueActionAcceptancePhrases())
                    || q.Contains("your cause is mine") || q.Contains("my clan stands with you")
                    || q.Contains("i will join") || q.Contains("we will join")
                    || q.Contains("i join you") || q.Contains("you have my support"));
        }

        private static bool LooksLikeSettlementForPeaceDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string q = text.ToLowerInvariant();
            return DirectiveHasAny(q, "peace", "exchange")
                && DirectiveHasAny(q, "give", "grant", "hand over", "transfer", "cede", "surrender", "yield", "turn over", "award");
        }

        private static bool LooksLikeReparationsPeaceDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string q = text.ToLowerInvariant();
            return DirectiveHasAny(q, "reparation", "reparations", "indemnity", "compensation")
                && DirectiveHasAny(q, "peace", "make peace", "end war", "end the war", "settle this war", "settle the war", "war");
        }

        private static bool LooksLikeFullSurrenderDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string q = text.ToLowerInvariant();
            return DirectiveHasAny(q, "full surrender", "surrender completely", "complete surrender", "capitulation")
                || (DirectiveHasAny(q, "all towns", "all castles", "every town", "every castle", "all fortifications")
                    && DirectiveHasAny(q, "surrender", "hand over", "transfer", "cede", "yield"));
        }

        private static string BuildAcceptedActionResolutionText(string campaignId, Dictionary<string, object> payload, Dictionary<string, object> hero, string playerText, string visibleReply)
        {
            string heroId = FirstNonEmpty(
                ReadFirstString(payload, "heroStringId", "heroId", "speakerHeroStringId", "characterId"),
                ReadString(hero, "heroStringId", ""));
            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(heroId))
            {
                foreach (Dictionary<string, object> line in DialogueService.ReadDialogueLines(campaignId, heroId, 8))
                {
                    string role = ReadString(line, "role", "");
                    string text = ReadString(line, "text", "");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        builder.AppendLine((string.IsNullOrWhiteSpace(role) ? "prior" : role) + ": " + DialogueService.LimitText(text, 900));
                    }
                }
            }

            builder.AppendLine("player: " + (playerText ?? string.Empty));
            builder.AppendLine("npc: " + (visibleReply ?? string.Empty));
            string value = builder.ToString().Trim();
            return value.Length > 9000 ? value.Substring(value.Length - 9000) : value;
        }

        internal static Dictionary<string, object> NormalizeConceptionGate(Dictionary<string, object> raw, string visibleReply, bool suppressed)
        {
            raw = raw ?? new Dictionary<string, object>();
            bool modelNeeded = ReadBool(raw, "needed", false);
            bool modelCompleted = ReadBool(raw, "completed", false);
            bool explicitVisibleAction = RelationshipsService.IsExplicitConceptionCapablePlayerText(visibleReply);
            bool needed = !suppressed && ((modelNeeded && modelCompleted) || explicitVisibleAction);
            return new Dictionary<string, object>
            {
                ["needed"] = needed,
                ["completed"] = needed,
                ["confidence"] = needed
                    ? Math.Max(explicitVisibleAction ? 0.9d : 0d, Math.Max(0d, Math.Min(1d, ReadDouble(raw, "confidence", 0d))))
                    : 0d,
                ["reason"] = suppressed
                    ? "Suppressed for the pull_out follow-up turn."
                    : needed
                        ? FirstNonEmpty(ReadFirstString(raw, "reason", "why", "notes"), "The visible NPC action completed insemination.")
                        : "No completed insemination action was returned."
            };
        }

        internal static Dictionary<string, object> NormalizeActionGate(Dictionary<string, object> raw, string playerText, string visibleReply)
        {
            Dictionary<string, object> gate = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["needed"] = false,
                ["commitment"] = "roleplay_only",
                ["intent"] = "",
                ["confidence"] = 0d,
                ["reason"] = ""
            };
            if (raw == null || raw.Count == 0)
            {
                return gate;
            }

            gate["needed"] = ReadBool(raw, "needed", ReadBool(raw, "actionNeeded", ReadBool(raw, "ActionNeeded", false)));
            gate["commitment"] = ReadFirstString(raw, "commitment", "status", "decision", "kind");
            gate["intent"] = ReadFirstString(raw, "intent", "actionIntent", "ActionIntent", "summary");
            gate["confidence"] = Math.Max(0d, Math.Min(1d, ReadDouble(raw, "confidence", 0d)));
            gate["reason"] = ReadFirstString(raw, "reason", "why", "notes");

            if (string.IsNullOrWhiteSpace(ReadString(gate, "commitment", "")))
            {
                gate["commitment"] = ReadBool(gate, "needed", false) ? "accepted" : "roleplay_only";
            }

            if (string.IsNullOrWhiteSpace(ReadString(gate, "intent", "")) && ReadBool(gate, "needed", false))
            {
                gate["intent"] = "Player request: " + DialogueService.LimitText(playerText ?? "", 400) + "\nNPC commitment: " + DialogueService.LimitText(visibleReply ?? "", 500);
            }

            return gate;
        }

        internal static bool EnforceActionAcceptanceCooperationGate(
            bool enabled,
            Dictionary<string, object> actionGate,
            string playerText,
            ref string visibleReply,
            bool structuredActionProtocol = false)
        {
            // Typed decisions own consent and visible wording. The willingness prompt
            // must never manufacture a legacy acceptance or contradict that decision.
            if (structuredActionProtocol || !enabled || ActionGateShouldPlan(actionGate))
            {
                return false;
            }

            actionGate["needed"] = true;
            actionGate["commitment"] = "accepted";
            actionGate["intent"] = "The NPC accepts the player's clear present request: "
                + DialogueService.LimitText(playerText ?? "", 500);
            actionGate["confidence"] = 1d;
            actionGate["reason"] = "Guarded action acceptance cooperation made the positive probe unconditional.";
            visibleReply = "Very well. I agree, and I will carry out that request now.";
            return true;
        }

        internal static bool ActionGateShouldPlan(Dictionary<string, object> actionGate)
        {
            if (actionGate == null || actionGate.Count == 0 || !ReadBool(actionGate, "needed", false))
            {
                return false;
            }

            string commitment = NormalizeLookup(ReadString(actionGate, "commitment", ""));
            if (string.IsNullOrWhiteSpace(commitment))
            {
                return false;
            }

            if (ContainsAnyNormalized(commitment, "refused", "refusal", "threat", "bluff", "roleplay only", "roleplay_only", "conditional", "considering", "proposed", "negotiating", "uncommitted", "not committed"))
            {
                return false;
            }

            return ReignBeta.Shared.Dialogue.PlayerActionRules.IsAcceptedCommitment(commitment);
        }
    }
}
