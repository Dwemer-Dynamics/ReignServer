using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        internal static Dictionary<string, object> ResolvePromptIdentity(Dictionary<string, object> payload,
            string observerId, string mode, string fallbackEncounterId,
            IEnumerable<Dictionary<string, object>> canonicalTranscript = null,
            Func<Dictionary<string, object>, Dictionary<string, object>> introductionResponder = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> suppliedSubject =
                ReadDictionary(payload, "playerIdentity");
            Dictionary<string, object> subject = suppliedSubject == null
                ? new Dictionary<string, object>
                {
                    ["heroStringId"] = ReadFirstString(payload,
                        "playerHeroStringId", "mainHeroStringId", "playerId"),
                    ["name"] = ReadString(payload, "playerName", "Player")
                }
                : new Dictionary<string, object>(suppliedSubject,
                    StringComparer.OrdinalIgnoreCase);
            if (!subject.ContainsKey("isFemale")
                && payload.ContainsKey("playerIsFemale"))
                subject["isFemale"] = ReadBool(
                    payload, "playerIsFemale", false);
            if (!subject.ContainsKey("sex")
                && subject.ContainsKey("isFemale"))
                subject["sex"] = ReadBool(
                    subject, "isFemale", false)
                    ? "female"
                    : "male";
            string subjectId = IdentityHeroId(subject);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId))
            {
                return BuildIdentityView(null,
                    new Dictionary<string, object>
                    {
                        ["mode"] = mode ?? "",
                        ["canonicalName"] = ReadString(
                            subject, "name", ""),
                        ["subject"] = subject
                    });
            }

            string encounterId = FirstNonEmpty(
                ReadFirstString(payload, "conversationSessionId", "sessionId", "eventId", "socialEventId"),
                fallbackEncounterId,
                "encounter_" + observerId + "_" + subjectId);
            Dictionary<string, object> encounter = new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["correlationId"] = Program.EnsureCorrelationId(payload),
                ["observerHeroStringId"] = observerId,
                ["subjectHeroStringId"] = subjectId,
                ["encounterId"] = encounterId,
                ["mode"] = mode ?? "dialogue",
                ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                ["playerText"] = ReadFirstString(payload, "playerText", "text", "message"),
                ["canonicalName"] = ReadString(subject, "name", ReadString(payload, "playerName", "Player")),
                ["isPrisoner"] = ReadBool(subject, "isPrisoner", false),
                ["appearance"] = ReadDictionary(subject, "appearance") ?? new Dictionary<string, object>(),
                ["observer"] = ReadDictionary(payload, "speaker") ?? ReadDictionary(payload, "hero") ?? new Dictionary<string, object> { ["heroStringId"] = observerId },
                ["subject"] = subject
            };
            foreach (string key in new[]
            {
                "transcript", "groupTranscript", "attendees", "participantProfiles", "sceneParticipants",
                "conversationSceneState", "playerName", "mainHeroName",
                "sceneContext", "selectedContextPulls", "contextBundles",
                "nativePoliticalContext", "courtLifeContext", "conversationMode", "timelineId"
            })
            {
                if (payload.ContainsKey(key)) encounter[key] = payload[key];
            }
            // Dialogue/event adapters supply their already scoped, authoritative
            // history. It replaces client transcript copies for introduction checks.
            if (canonicalTranscript != null)
            {
                encounter["transcript"] = canonicalTranscript.ToList();
                encounter.Remove("groupTranscript");
            }
            Dictionary<string, object> response = IdentityEncounterApi(encounter);
            ContextualSelfIntroductionDecision contextual = ResolveContextualSelfIntroduction(
                encounter, response, canonicalTranscript, introductionResponder);
            if (contextual != null)
            {
                PersistenceService.ThrowIfCampaignRequestReplaced();
                response = IdentityEncounterApi(encounter, contextual);
            }
            return ReadDictionary(response, "identityView") ?? BuildIdentityView(null, encounter);
        }

        internal static List<Dictionary<string, object>>
            EnsureObserverParticipantIdentityViews(
                string campaignId,
                Dictionary<string, object> payload,
                string observerId)
        {
            payload = payload
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> existing =
                ReadDictionaryList(
                    payload,
                    "observerParticipantIdentityViews");
            if (existing.Count > 0
                && existing.All(row =>
                    ReadFirstString(
                            row,
                            "observerHeroStringId",
                            "observerId")
                        .Equals(
                            observerId ?? "",
                            StringComparison.OrdinalIgnoreCase)))
                return existing;

            List<Dictionary<string, object>> profiles =
                DialogueService.MergedInteractionParticipantProfiles(payload);
            string playerId = ReadFirstString(
                payload,
                "playerHeroStringId",
                "mainHeroStringId",
                "playerId");
            Dictionary<string, object> observer =
                profiles.FirstOrDefault(profile =>
                    Program.CharacterIdFrom(profile).Equals(
                        observerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                ?? ReadDictionary(payload, "speaker")
                ?? ReadDictionary(payload, "hero")
                ?? new Dictionary<string, object>
                {
                    ["heroStringId"] = observerId ?? ""
                };
            string encounterId = FirstNonEmpty(
                DialogueService.GroupConversationSessionId(payload),
                ReadFirstString(
                    payload,
                    "conversationSessionId",
                    "sessionId",
                    "eventId"),
                "group_identity_"
                    + Program.EnsureCorrelationId(payload));
            Dictionary<string, object> nativeBase =
                ReadDictionary(
                    payload,
                    "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> views =
                new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> subject in profiles)
            {
                string subjectId = Program.CharacterIdFrom(subject);
                if (string.IsNullOrWhiteSpace(subjectId)
                    || subjectId.Equals(
                        observerId ?? "",
                        StringComparison.OrdinalIgnoreCase)
                    || subjectId.Equals(
                        playerId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                Dictionary<string, object> pairNative =
                    new Dictionary<string, object>(
                        nativeBase,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["observer"] = observer,
                        ["subject"] = subject,
                        // The always-on diplomacy packet is player-kingdom
                        // state. Do not misapply it to another NPC subject.
                        ["diplomacy"] =
                            new Dictionary<string, object>()
                    };
                Dictionary<string, object> response =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] =
                                campaignId ?? "default",
                            ["correlationId"] =
                                Program.EnsureCorrelationId(payload),
                            ["observerHeroStringId"] =
                                observerId ?? "",
                            ["subjectHeroStringId"] =
                                subjectId,
                            ["observer"] = observer,
                            ["subject"] = subject,
                            ["canonicalName"] =
                                ReadString(subject, "name", ""),
                            ["encounterId"] = encounterId,
                            ["worldDay"] = ReadDouble(
                                payload, "worldDay", 0d),
                            ["mode"] = ReadString(
                                payload,
                                "mode",
                                "group_conversation"),
                            ["nativePoliticalContext"] =
                                pairNative,
                            ["appearance"] =
                                ReadDictionary(
                                    subject,
                                    "appearance")
                                ?? new Dictionary<string, object>()
                        });
                views.Add(
                    new Dictionary<string, object>
                    {
                        ["observerHeroStringId"] =
                            observerId ?? "",
                        ["subjectHeroStringId"] =
                            subjectId,
                        ["identityView"] =
                            ReadDictionary(
                                response,
                                "identityView")
                            ?? new Dictionary<string, object>(),
                        ["recognition"] =
                            ReadDictionary(
                                response,
                                "recognition")
                            ?? new Dictionary<string, object>()
                    });
            }
            payload["observerParticipantIdentityViews"] = views;
            return views;
        }

        private static Dictionary<string, object>
            ObserverParticipantIdentityView(
                Dictionary<string, object> payload,
                string subjectId)
        {
            return ReadDictionaryList(
                    payload
                        ?? new Dictionary<string, object>(),
                    "observerParticipantIdentityViews")
                .Where(row => ReadFirstString(
                        row,
                        "subjectHeroStringId",
                        "subjectId")
                    .Equals(
                        subjectId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadDictionary(
                        row,
                        "identityView")
                    ?? new Dictionary<string, object>())
                .FirstOrDefault()
                ?? new Dictionary<string, object>();
        }

        internal static string ObserverSafeParticipantName(
            Dictionary<string, object> payload,
            string observerId,
            string subjectId,
            string canonicalFallback)
        {
            if (string.IsNullOrWhiteSpace(subjectId)
                || subjectId.Equals(
                    observerId ?? "",
                    StringComparison.OrdinalIgnoreCase))
                return FirstNonEmpty(
                    canonicalFallback,
                    subjectId,
                    "the current speaker");
            Dictionary<string, object> view =
                ObserverParticipantIdentityView(payload, subjectId);
            return FirstNonEmpty(
                ReadString(view, "usableName", ""),
                ReadString(view, "safeLabel", ""),
                "an unidentified participant");
        }

        internal static string FormatEventLinesForObserver(
            List<Dictionary<string, object>> eventLines,
            Dictionary<string, object> payload,
            string observerId,
            Dictionary<string, object> playerIdentityView)
        {
            if (eventLines == null || eventLines.Count == 0)
                return "none";
            string playerId = ReadFirstString(
                payload
                    ?? new Dictionary<string, object>(),
                "playerHeroStringId",
                "mainHeroStringId",
                "playerId");
            StringBuilder builder = new StringBuilder();
            foreach (Dictionary<string, object> line in eventLines)
            {
                string text = ReadString(line, "text", "");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                string speakerId = ReadFirstString(
                    line,
                    "speakerHeroStringId",
                    "speaker_id",
                    "heroStringId");
                string role = ReadString(line, "role", "");
                string canonical = ReadString(
                    line,
                    "speaker",
                    "Event");
                string attribution = canonical;
                bool identified = true;
                if (role.Equals(
                        "player",
                        StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(playerId)
                        && speakerId.Equals(
                            playerId,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    attribution = FirstNonEmpty(
                        ReadString(
                            playerIdentityView
                                ?? new Dictionary<string, object>(),
                            "usableName",
                            ""),
                        ReadString(
                            playerIdentityView
                                ?? new Dictionary<string, object>(),
                            "safeLabel",
                            ""),
                        "the player");
                    identified = ReadBool(
                        playerIdentityView
                            ?? new Dictionary<string, object>(),
                        "canonicalNameAllowed",
                        false);
                }
                else if (!string.IsNullOrWhiteSpace(speakerId)
                    && !speakerId.Equals(
                        observerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> pairView =
                        ObserverParticipantIdentityView(
                            payload,
                            speakerId);
                    attribution = ObserverSafeParticipantName(
                        payload,
                        observerId,
                        speakerId,
                        canonical);
                    identified = ReadBool(
                        pairView,
                        "canonicalNameAllowed",
                        false);
                }
                if (!string.IsNullOrWhiteSpace(speakerId)
                    && (identified
                        || speakerId.Equals(
                            observerId ?? "",
                            StringComparison.OrdinalIgnoreCase)))
                    attribution += " [id=" + speakerId + "]";
                if (!string.IsNullOrWhiteSpace(role))
                    attribution += " [role=" + role + "]";
                builder.AppendLine(
                    "- " + attribution + ": " + text);
            }
            return builder.Length == 0
                ? "none"
                : builder.ToString().TrimEnd();
        }

        internal static void RecordPresentNpcSelfIntroduction(
            string campaignId,
            Dictionary<string, object> payload,
            string speakerId,
            string speakerName,
            string reply,
            string encounterId)
        {
            string introduced = ExtractSelfIntroducedName(
                reply ?? "",
                false);
            if (string.IsNullOrWhiteSpace(introduced)
                || !NormalizeIdentityName(introduced).Equals(
                    NormalizeIdentityName(speakerName),
                    StringComparison.OrdinalIgnoreCase))
                return;
            List<Dictionary<string, object>> profiles =
                DialogueService.MergedInteractionParticipantProfiles(payload);
            string playerId = ReadFirstString(
                payload,
                "playerHeroStringId",
                "mainHeroStringId",
                "playerId");
            List<string> observers = profiles
                .Select(Program.CharacterIdFrom)
                .Concat(new[] { playerId })
                .Where(id => !string.IsNullOrWhiteSpace(id)
                    && !id.Equals(
                        speakerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, object> subject =
                profiles.FirstOrDefault(profile =>
                    Program.CharacterIdFrom(profile).Equals(
                        speakerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                ?? new Dictionary<string, object>
                {
                    ["heroStringId"] = speakerId ?? "",
                    ["name"] = speakerName ?? ""
                };
            foreach (string observerId in observers)
            {
                IdentityEncounterApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId ?? "default",
                        ["correlationId"] =
                            Program.EnsureCorrelationId(payload),
                        ["observerHeroStringId"] = observerId,
                        ["subjectHeroStringId"] =
                            speakerId ?? "",
                        ["observer"] =
                            profiles.FirstOrDefault(profile =>
                                Program.CharacterIdFrom(profile).Equals(
                                    observerId,
                                    StringComparison.OrdinalIgnoreCase))
                            ?? new Dictionary<string, object>
                            {
                                ["heroStringId"] = observerId
                            },
                        ["subject"] = subject,
                        ["canonicalName"] =
                            speakerName ?? "",
                        ["encounterId"] =
                            encounterId ?? "",
                        ["worldDay"] = ReadDouble(
                            payload,
                            "worldDay",
                            0d),
                        ["mode"] = ReadString(
                            payload,
                            "mode",
                            "group_conversation"),
                        ["playerText"] = reply ?? ""
                    });
            }
        }

        internal static string BuildIdentityPromptBlock(Dictionary<string, object> identityView)
        {
            identityView = identityView ?? new Dictionary<string, object>();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("IDENTITY KNOWLEDGE - OBSERVER-SPECIFIC AND AUTHORITATIVE");
            builder.AppendLine("Identity state: " + ReadString(identityView, "identityState", "encountered_unknown"));
            builder.AppendLine("Usable name or label: " + ReadString(identityView, "usableName", "the stranger"));
            if (ReadBool(identityView, "subjectSexKnown", false))
            {
                string subjectSex = ReadString(
                    identityView, "subjectSex", "unknown");
                builder.AppendLine(
                    "Subject visible sex: " + subjectSex
                    + " (authoritative current native fact, independent of whether the observer knows the subject's name or rank)");
                builder.AppendLine(
                    "VISIBLE-SEX ADDRESS RULE: never describe or directly address this person as the opposite sex. "
                    + "This fact establishes sex only; it does not reveal or prove a name, noble title, clan, occupation, or political office.");
            }
            if (!ReadBool(identityView, "knowsIdentity", false)
                && !ReadBool(identityView, "canonicalNameAllowed", false))
            {
                builder.AppendLine(
                    "UNKNOWN-IDENTITY ADDRESS STYLE: speak to this person as 'you' by default. "
                    + "The usable descriptive label is optional, may appear at most once in a reply, "
                    + "is not a personal name, and must not become a repeated epithet or a prerequisite for ordinary conversation.");
            }
            string claimed = ReadString(identityView, "claimedName", "");
            if (!string.IsNullOrWhiteSpace(claimed))
            {
                string usable = ReadString(identityView, "usableName", "");
                bool canonicalVerified = ReadBool(
                    identityView, "canonicalNameAllowed", false);
                bool conflictsWithVerifiedIdentity = canonicalVerified
                    && !NormalizeIdentityName(claimed).Equals(
                        NormalizeIdentityName(usable),
                        StringComparison.OrdinalIgnoreCase);
                if (conflictsWithVerifiedIdentity)
                {
                    builder.AppendLine(
                        "Earlier conflicting claimed name: " + claimed
                        + ". This does not replace the verified identity "
                        + usable + ". Never address the person as the earlier "
                        + "claim or deny their verified identity because of it; "
                        + "mention it only when explicitly discussing that old claim.");
                }
                else
                {
                    builder.AppendLine(
                        "Claimed name: " + claimed
                        + " (not independently verified unless this exact name "
                        + "is also the verified usable identity)");
                    builder.AppendLine(
                        "The preceding Claimed name field is the exact text of "
                        + "the name they supplied. The usable stranger label is "
                        + "only a description of uncertainty and is not part of "
                        + "that name. Never say they claimed the whole "
                        + "descriptive label as their name.");
                }
            }
            builder.AppendLine(ReadString(identityView, "instruction", "Do not use a canonical name that the observer does not know."));
            Dictionary<string, object> authority =
                ReadDictionary(identityView, "authorityView")
                ?? new Dictionary<string, object>();
            builder.AppendLine(
                "PUBLIC IDENTITY AND POLITICAL AUTHORITY - AUTHORITATIVE");
            builder.AppendLine(
                "Authority source: "
                + ReadString(authority, "source", "none"));
            builder.AppendLine(
                "Recognized current roles: "
                + (ReadStringList(authority, "recognizedRoles").Count == 0
                    ? "none"
                    : string.Join(", ",
                        ReadStringList(
                            authority, "recognizedRoles"))));
            builder.AppendLine(
                "PUBLIC ROLES ARE CUMULATIVE, NOT MUTUALLY EXCLUSIVE. Every "
                + "role in the recognized-current-roles list is true at the "
                + "same time. The primary role is only the most specific title. "
                + "For example, a king who is also listed as lord remains both "
                + "king and lord; never say that becoming ruler makes the person "
                + "'not a lord' or erases any other listed office.");
            builder.AppendLine(
                "Subject primary current role: "
                + ReadString(
                    authority,
                    "subjectPrimaryRole",
                    "identity_unverified"));
            builder.AppendLine(
                "Observer primary current role: "
                + ReadString(
                    authority,
                    "observerPrimaryRole",
                    "commoner_or_unaffiliated"));
            builder.AppendLine(
                "Observer-relative authority relationship: "
                + ReadString(
                    authority,
                    "authorityRelationship",
                    "identity_unverified"));
            builder.AppendLine(
                "DIRECT AUTHORITY-QUESTION RULE: when the latest player line "
                + "asks who they are politically, what office or role they "
                + "hold, whether they are this observer's sovereign, whose "
                + "sovereign they are, who owns or governs the current "
                + "settlement, or what the current diplomacy is, answer every "
                + "requested point directly from this authoritative block. "
                + "A terse or hostile character may answer bluntly, but must "
                + "not evade, redirect, or omit the answer. State 'none', "
                + "'not your sovereign', 'no verified governorship', or "
                + "equivalent grounded wording when the authoritative value "
                + "is absent or false; never invent an office to fill the gap.");
            if (ReadBool(
                    authority, "realmSovereignKnown", false))
            {
                builder.AppendLine(
                    "The observer knows that "
                    + ReadString(identityView, "usableName", "this person")
                    + " is the current sovereign of "
                    + FirstNonEmpty(
                        ReadString(
                            authority,
                            "subjectKingdomName", ""),
                        "their current realm")
                    + ". This is a verified public office, not a claim. "
                    + "Use the public realm name in dialogue; never speak a "
                    + "raw native StringId such as `new_kingdom`.");
                builder.AppendLine(
                    "Valid formal addresses: "
                    + string.Join(", ", ReadStringList(
                        authority, "validFormalAddresses"))
                    + ". Personal identity knowledge is "
                    + (ReadBool(authority, "personalIdentityKnown", false)
                        ? "known; supplied names may be used subject to etiquette."
                        : "not known; do not reveal or use the canonical name."));
            }
            if (ReadBool(
                    authority,
                    "subjectIsObserverSovereign", false))
            {
                builder.AppendLine(
                    "ALLEGIANCE RELATION: this person is the observer's own "
                    + "current sovereign, and the observer is their subject. "
                    + "The observer may be loyal, ambitious, manipulative, "
                    + "resentful, oppositional, or deliberately defiant, but "
                    + "must not accidentally frame their own sovereign as a "
                    + "foreign equal, ordinary visitor, or unaccountable stranger.");
            }
            else if (ReadBool(
                    authority,
                    "observerIsSubjectSovereign", false))
            {
                builder.AppendLine(
                    "ALLEGIANCE RELATION: the observer is this known person's "
                    + "own current sovereign. The observer knows the person is "
                    + "their subject.");
            }
            if (ReadBool(
                    authority,
                    "currentSettlementOwnerKnown", false))
            {
                builder.AppendLine(
                    "The observer knows that "
                    + ReadString(identityView, "usableName", "this person")
                    + "'s clan currently owns "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "the current settlement")
                    + " (`"
                    + ReadString(
                        authority,
                        "currentSettlementId", "")
                    + "`). This is current native ownership, not a claim.");
            }
            if (!string.IsNullOrWhiteSpace(
                    ReadString(
                        authority,
                        "currentSettlementId", "")))
            {
                builder.AppendLine(
                    "CURRENT CONVERSATION SETTLEMENT: "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "the current settlement")
                    + " (`"
                    + ReadString(
                        authority,
                        "currentSettlementId", "")
                    + "`). This is the active local venue for statements such "
                    + "as 'this city', 'here', 'this hall', and scene headings. "
                    + "An observer's home, clan seat, or native map position "
                    + "elsewhere is background only and must not replace the "
                    + "current conversation settlement.");
                builder.AppendLine(
                    "CURRENT LOCAL AUTHORITY: "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "the current settlement")
                    + " is held by clan "
                    + FirstNonEmpty(
                        ReadString(
                            authority,
                            "currentSettlementOwnerClanName", ""),
                        ReadString(
                            authority,
                            "currentSettlementOwnerClanId", ""),
                        "unknown")
                    + "; governor hero id is "
                    + FirstNonEmpty(
                        ReadString(
                            authority,
                            "currentSettlementGovernorHeroId", ""),
                        "none")
                    + ". The observer may say 'my city', 'my castle', or "
                    + "'my hall' as a personal authority claim only when the "
                    + "native role facts establish owner-clan membership, "
                    + "governorship, or genuine hosting authority. Civic wording "
                    + "such as 'our city' must not be presented as personal ownership.");
            }
            if (ReadBool(
                    authority,
                    "currentDiplomacyKnown", false))
            {
                List<string> enemyNames = ReadStringList(
                    authority,
                    "currentEnemyKingdomNames");
                builder.AppendLine(
                    "VOLATILE CURRENT DIPLOMACY: the subject's kingdom is at "
                    + "war with "
                    + ReadInt(
                        authority,
                        "currentEnemyKingdomCount", 0)
                    + " distinct enemy kingdom(s)"
                    + (enemyNames.Count == 0
                        ? ""
                        : " (" + string.Join(", ", enemyNames) + ")")
                    + ". This current native value supersedes older snapshots. "
                    + "Do not volunteer war counts during unrelated small talk, "
                    + "and never reinterpret hostile clans or minor factions as nations.");
            }
            builder.AppendLine(
                ReadString(
                    authority, "instruction",
                    "Do not infer unsupported political office."));
            builder.AppendLine("Mechanical hero IDs and resolver names are server plumbing, not facts this NPC knows. Never reveal an unknown canonical identity from them.");
            builder.AppendLine(ReputationService.BuildKnownSocialStandingPromptBlock(
                ReadDictionary(identityView, "socialStandingView") ?? new Dictionary<string, object>()));
            return builder.ToString().TrimEnd();
        }

        internal static string SanitizePromptForIdentity(string prompt, string latestPlayerText, string canonicalName, Dictionary<string, object> identityView)
        {
            prompt = prompt ?? string.Empty;
            identityView = identityView ?? new Dictionary<string, object>();
            bool canonicalNameAllowed =
                ReadBool(identityView, "canonicalNameAllowed", false);
            string usableName = ReadString(
                identityView, "usableName", canonicalName ?? "");
            string conflictingClaim = ReadString(
                identityView, "claimedName", "");
            if (canonicalNameAllowed
                && !string.IsNullOrWhiteSpace(usableName)
                && !string.IsNullOrWhiteSpace(conflictingClaim)
                && !NormalizeIdentityName(conflictingClaim).Equals(
                    NormalizeIdentityName(usableName),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Keep one authoritative, attributed record of the old claim in
                // the identity block, but stop stale state, summaries, and
                // transcripts from repeating it as the person's current name.
                // The newest player line remains verbatim evidence.
                string verifiedNonce = Guid.NewGuid().ToString("N");
                string identityMarker =
                    "__REIGN_VERIFIED_IDENTITY_BLOCK_" + verifiedNonce + "__";
                string latestMarker =
                    "__REIGN_LATEST_PLAYER_TEXT_" + verifiedNonce + "__";
                string identityBlock =
                    BuildIdentityPromptBlock(identityView);
                bool protectedIdentity =
                    !string.IsNullOrWhiteSpace(identityBlock)
                    && prompt.Contains(identityBlock);
                bool protectedVerifiedLatest =
                    !string.IsNullOrWhiteSpace(latestPlayerText)
                    && prompt.Contains(latestPlayerText);
                if (protectedIdentity)
                    prompt = prompt.Replace(identityBlock, identityMarker);
                if (protectedVerifiedLatest)
                    prompt = prompt.Replace(latestPlayerText, latestMarker);
                prompt = Regex.Replace(
                    prompt,
                    @"(?<![\p{L}\p{N}])"
                        + Regex.Escape(conflictingClaim)
                        + @"(?![\p{L}\p{N}])",
                    usableName,
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant);
                if (protectedVerifiedLatest)
                    prompt = prompt.Replace(latestMarker, latestPlayerText);
                if (protectedIdentity)
                    prompt = prompt.Replace(identityMarker, identityBlock);
                return ReplaceVerifiedIdentityLabel(
                    prompt, latestPlayerText,
                    identityView, usableName);
            }
            if (canonicalNameAllowed)
                return ReplaceVerifiedIdentityLabel(
                    prompt, latestPlayerText,
                    identityView, usableName);
            if (string.IsNullOrWhiteSpace(canonicalName))
                return prompt;
            string replacement = ReadString(identityView, "usableName", ReadString(identityView, "safeLabel", "the stranger"));
            if (string.Equals(NormalizeIdentityName(replacement), NormalizeIdentityName(canonicalName), StringComparison.OrdinalIgnoreCase)) return prompt;
            string claimedName = ReadString(identityView, "claimedName", "");
            // A name the observer actually heard is safe to repeat as an unverified
            // claim. Replacing it in the final reply changed correct model wording
            // such as "your name was Rhovarion" into the false assertion that the
            // player had spoken the entire descriptive stranger label as a name.
            if (!string.IsNullOrWhiteSpace(claimedName)
                && NormalizeIdentityName(claimedName).Equals(NormalizeIdentityName(canonicalName), StringComparison.OrdinalIgnoreCase))
            {
                return prompt;
            }
            string nonce = Guid.NewGuid().ToString("N");
            string marker = "__REIGN_LATEST_PLAYER_TEXT_" + nonce + "__";
            string replacementMarker = "__REIGN_IDENTITY_LABEL_" + nonce + "__";
            string claimMarker = "__REIGN_IDENTITY_CLAIM_" + nonce + "__";
            bool protectedLatest = !string.IsNullOrWhiteSpace(latestPlayerText) && prompt.Contains(latestPlayerText);
            if (protectedLatest) prompt = prompt.Replace(latestPlayerText, marker);
            // The observer-safe label can itself contain the unverified claimed name
            // (for example, "the stranger claiming to be Rhovarion"). Protect that
            // already-sanitized label before replacing otherwise leaked canonical-name
            // occurrences, or each pass recursively wraps it as "the stranger claiming
            // to be the stranger claiming to be Rhovarion".
            bool protectedReplacement = !string.IsNullOrWhiteSpace(replacement)
                && prompt.IndexOf(replacement, StringComparison.OrdinalIgnoreCase) >= 0;
            if (protectedReplacement)
            {
                prompt = Regex.Replace(prompt, Regex.Escape(replacement), replacementMarker,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            string claimPrefix = string.IsNullOrWhiteSpace(claimedName) ? "" : "Claimed name: " + claimedName;
            bool protectedClaim = !string.IsNullOrWhiteSpace(claimPrefix) && prompt.Contains(claimPrefix);
            if (protectedClaim) prompt = prompt.Replace(claimPrefix, claimMarker);
            prompt = Regex.Replace(prompt, @"(?<![\p{L}\p{N}])" + Regex.Escape(canonicalName) + @"(?![\p{L}\p{N}])", replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (protectedClaim) prompt = prompt.Replace(claimMarker, claimPrefix);
            if (protectedReplacement) prompt = prompt.Replace(replacementMarker, replacement);
            if (protectedLatest) prompt = prompt.Replace(marker, latestPlayerText);
            return prompt;
        }

        private static string ReplaceVerifiedIdentityLabel(
            string prompt,
            string latestPlayerText,
            Dictionary<string, object> identityView,
            string usableName)
        {
            string safeLabel = ReadString(
                identityView, "safeLabel", "");
            if (string.IsNullOrWhiteSpace(prompt)
                || string.IsNullOrWhiteSpace(safeLabel)
                || string.IsNullOrWhiteSpace(usableName)
                || NormalizeIdentityName(safeLabel).Equals(
                    NormalizeIdentityName(usableName),
                    StringComparison.OrdinalIgnoreCase))
                return prompt ?? string.Empty;

            string nonce = Guid.NewGuid().ToString("N");
            string identityBlock =
                BuildIdentityPromptBlock(identityView);
            string identityMarker =
                "__REIGN_VERIFIED_IDENTITY_" + nonce + "__";
            string latestMarker =
                "__REIGN_VERIFIED_LATEST_" + nonce + "__";
            bool protectedIdentity =
                !string.IsNullOrWhiteSpace(identityBlock)
                && prompt.Contains(identityBlock);
            bool protectedLatest =
                !string.IsNullOrWhiteSpace(latestPlayerText)
                && prompt.Contains(latestPlayerText);
            if (protectedIdentity)
                prompt = prompt.Replace(
                    identityBlock, identityMarker);
            if (protectedLatest)
                prompt = prompt.Replace(
                    latestPlayerText, latestMarker);
            prompt = Regex.Replace(
                prompt,
                @"(?<![\p{L}\p{N}])"
                    + Regex.Escape(safeLabel)
                    + @"(?![\p{L}\p{N}])",
                usableName,
                RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);
            if (protectedLatest)
                prompt = prompt.Replace(
                    latestMarker, latestPlayerText);
            if (protectedIdentity)
                prompt = prompt.Replace(
                    identityMarker, identityBlock);
            return prompt;
        }
    }
}
