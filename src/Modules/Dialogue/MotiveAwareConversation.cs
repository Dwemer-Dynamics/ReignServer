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
        private const int MotiveStableCharacterCap = 1000;
        private const int MotiveLiveContextCap = 1800;
        private const string MotiveDecisionModel = "reign_motive_decision_v5";

        internal sealed class MotiveDomainDefinition
        {
            public string Id;
            public string[] Cues;
            public string[] TraitKeys;
            public string[] RelationshipKeys;
            public string Instruction;
        }

        internal static readonly MotiveDomainDefinition[] MotiveDomainDefinitions =
        {
            MotiveDomain("romance", new[] { "flirt", "seduc", "kiss", "court me", "attract", "desire", "lover", "affair", "romance", "beautiful", "handsome", "intimat", "marry" }, new[] { "flirtatiousness", "sociability", "pragmatism", "tact", "confidence", "riskTolerance", "impulsiveness", "loyalty", "dutyMotivation", "discipline", "traditionalism", "shame", "religionMotivation", "familyMotivation" }, new[] { "nativeRelation", "directionalAffinity", "lifecycleTags" }, "Use current standing and explicit lifecycle tags. Do not invent hidden attraction or relationship facets."),
            MotiveDomain("wealth_patronage", new[] { "gold", "coin", "wealth", "rich", "bribe", "pay", "gift", "patron", "fund", "debt", "trade" }, new[] { "wealthMotivation", "greed", "pragmatism", "honesty", "honor" }, new[] { "nativeRelation", "directionalAffinity" }, "Judge patronage and material advantage from perceived wealth, conversation history, and current standing."),
            MotiveDomain("power_status", new[] { "power", "rank", "status", "title", "throne", "crown", "clan", "fief", "influence", "office", "court", "rule" }, new[] { "powerMotivation", "ambition", "pragmatism", "authorityRespect", "pride", "envy" }, new[] { "nativeRelation", "directionalAffinity" }, "Judge political access and rank relative to this NPC's own station."),
            MotiveDomain("intrigue_manipulation", new[] { "manipulat", "leverage", "advantage", "scheme", "intrigue", "useful to your ambition", "political circle", "flatter", "conceal", "selective disclosure", "withhold", "evasive", "persuas", "bargain", "obligation", "calculated warmth", "strategic courtship", "patronage", "get the most" }, new[] { "ambition", "powerMotivation", "wealthMotivation", "fameMotivation", "pragmatism", "tact", "confidence", "honesty", "empathy", "loyalty", "dutyMotivation", "shame" }, new[] { "nativeRelation", "directionalAffinity", "recentIncidents" }, "Decide whether this NPC would actively shape the player for personal advantage. High ambition, opportunistic motives, tact, and useful relative status may support flattery, selective disclosure, calculated courtship, bargaining, obligation, or pressure. Honesty, empathy, loyalty, duty, poor opportunity, exposure, or risk may require directness or restraint. Never force manipulation or romance merely because the player has rank."),
            MotiveDomain("protection_survival", new[] { "protect", "safe", "safety", "surviv", "threat", "kill", "war", "army", "guard", "prison", "captive", "rescue", "danger" }, new[] { "survivalMotivation", "fearfulness", "pragmatism", "familyMotivation", "courage" }, new[] { "nativeRelation", "directionalAffinity" }, "Separate freely chosen protection from coercion without storing a relationship fear score."),
            MotiveDomain("family_legacy", new[] { "family", "child", "heir", "dynasty", "bloodline", "succession", "legacy", "marriage", "spouse", "son", "daughter" }, new[] { "legacyMotivation", "familyMotivation", "dutyMotivation", "traditionalism", "ambition" }, new[] { "nativeRelation", "directionalAffinity", "lifecycleTags" }, "Consider lineage, current marriage, and explicit relationship tags."),
            MotiveDomain("loyalty_betrayal", new[] { "loyal", "oath", "allegiance", "faithful", "betray", "treason", "traitor", "desert", "abandon" }, new[] { "loyalty", "dutyMotivation", "honesty", "vengefulness" }, new[] { "nativeRelation", "directionalAffinity", "recentIncidents" }, "Use current standing, known incidents, world history, and conversation evidence."),
            MotiveDomain("revenge", new[] { "revenge", "avenge", "grudge", "retaliat", "payback", "vengeance", "wronged" }, new[] { "revengeMotivation", "vengefulness", "aggression", "patience", "judgment" }, new[] { "nativeRelation", "directionalAffinity", "recentIncidents" }, "Let known grievance, restraint, and consequence judgment shape retaliation."),
            MotiveDomain("mercy_justice", new[] { "mercy", "justice", "punish", "execute", "forgive", "sentence", "trial", "cruel", "spare" }, new[] { "mercy", "compassion", "empathy", "honor", "judgment", "vengefulness" }, new[] { "nativeRelation", "directionalAffinity", "recentIncidents" }, "Balance mercy, principle, punishment, and known history rather than defaulting to kindness."),
            MotiveDomain("secrets_knowledge", new[] { "secret", "rumor", "information", "know", "learn", "spy", "blackmail", "evidence", "truth", "discover" }, new[] { "knowledgeMotivation", "curiosity", "tact", "honesty", "pragmatism" }, new[] { "nativeRelation", "directionalAffinity", "recentIncidents" }, "Reveal, trade, conceal, or test information according to known evidence and current standing."),
            MotiveDomain("faith_tradition", new[] { "faith", "religion", "sacred", "god", "gods", "ritual", "custom", "tradition", "heresy", "pious" }, new[] { "religionMotivation", "traditionalism", "authorityRespect", "honor", "dutyMotivation" }, new[] { "nativeRelation", "directionalAffinity" }, "Treat faith and tradition as motives only when the scene supplies relevant evidence.")
        };

        private static MotiveDomainDefinition MotiveDomain(string id, string[] cues, string[] traits, string[] relationships, string instruction)
        {
            return new MotiveDomainDefinition { Id = id, Cues = cues, TraitKeys = traits, RelationshipKeys = relationships, Instruction = instruction };
        }

        private static string BuildStableMotiveVector(Dictionary<string, object> characteristics)
        {
            Dictionary<string, object> traits = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> percentages = TraitPercentageSnapshot(traits);
            Dictionary<string, object> virtues = ReadDictionary(traits, "courtVirtues") ?? new Dictionary<string, object>();
            Dictionary<string, object> courtCharacter = ReadDictionary(traits, "courtCharacter") ?? new Dictionary<string, object>();
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["t"] = CharactersService.CoreTraitKeys.ToDictionary(key => key, key => (object)Clamp(ReadInt(percentages, key, 50), 0, 100), StringComparer.OrdinalIgnoreCase),
                ["g"] = CharactersService.CourtVirtueKeys.ToDictionary(key => key, key => (object)(virtues.ContainsKey(key) ? Clamp(ReadInt(virtues, key, 50), 0, 100) : -1), StringComparer.OrdinalIgnoreCase),
                ["cc"] = ReadBool(courtCharacter, "available", false) ? ReadString(courtCharacter, "cellId", "") : "unavailable"
            };
            string value = "PRIVATE MOTIVE VECTOR 0..100 (t=43 foundation traits,g=7 court groups,cc=fixed Court Character; -1=unavailable):" + CanonicalJson(compact);
            return value.Length <= MotiveStableCharacterCap ? value : Program.LimitText(value, MotiveStableCharacterCap);
        }

        internal static Dictionary<string, object> TraitPercentageSnapshot(Dictionary<string, object> traitDocument)
        {
            Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages") ?? new Dictionary<string, object>();
            Dictionary<string, object> foundations = ReadDictionary(traitDocument, "foundationTraits") ?? ReadDictionary(traitDocument, "hiddenReignTraits") ?? new Dictionary<string, object>();
            return CharactersService.CoreTraitKeys.ToDictionary(key => key, key =>
            {
                if (percentages.ContainsKey(key)) return (object)Clamp(ReadInt(percentages, key, 50), 0, 100);
                int level = Clamp(Program.TraitInt(foundations, key), -2, 2);
                return (object)(50 + level * 20);
            }, StringComparer.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> BuildConversationDecisionContext(
            string campaignId,
            string mode,
            string subjectId,
            Dictionary<string, object> profile,
            Dictionary<string, object> characteristics,
            Dictionary<string, object> state,
            string playerText,
            string sceneContext,
            Dictionary<string, object> payload,
            Dictionary<string, object> identityView,
            Dictionary<string, object> suppliedRelationship = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            profile = profile ?? new Dictionary<string, object>();
            state = state ?? new Dictionary<string, object>();
            string targetId = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId", "targetId", "recipientId"), ReadFirstString(profile, "mainHeroStringId", "playerHeroStringId"));
            EnsureServerConversationOpportunitySnapshot(campaignId, subjectId, targetId, profile, characteristics, payload, identityView);
            Dictionary<string, object> relationship = suppliedRelationship;
            if ((relationship == null || !relationship.ContainsKey("relationshipModel")) && !string.IsNullOrWhiteSpace(subjectId) && !string.IsNullOrWhiteSpace(targetId))
            {
                relationship = RelationshipsService.RelationshipContextApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["subjectId"] = subjectId,
                    ["targetId"] = targetId,
                    ["nativeRelation"] = ReadInt(profile, "relationToPlayer", 0),
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d)
                });
            }
            relationship = relationship ?? new Dictionary<string, object>();

            string spouseId = ReadString(profile, "spouseId", "");
            Dictionary<string, object> spouseRelationship = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(spouseId) && !spouseId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            {
                spouseRelationship = RelationshipsService.RelationshipContextApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["subjectId"] = subjectId,
                    ["targetId"] = spouseId,
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d)
                });
            }

            Dictionary<string, object> opportunity = CalculatePerceivedOpportunity(profile, characteristics, payload, identityView);
            Dictionary<string, object> sanitizedState =
                SanitizeConversationStateForVerifiedIdentity(
                    state, identityView, opportunity);
            sanitizedState = ReconcileConversationPlanForPrompt(sanitizedState, playerText, payload);
            Dictionary<string, object> courtCharacter = CourtService.CourtCharacterDecisionSnapshot(characteristics, opportunity);
            courtCharacter["activeScheme"] = CourtService.LoadCourtCharacterScheme(campaignId, subjectId, targetId, ReadDouble(payload, "worldDay", 0d));
            Dictionary<string, object> romance = CalculateRomanticPosture(campaignId, subjectId, targetId, profile, characteristics, relationship, spouseRelationship, opportunity, payload, mode, playerText);
            Dictionary<string, object> percentages = TraitPercentageSnapshot(ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>());
            Dictionary<string, object> scene = ConversationSceneSnapshot(mode, payload);
            Dictionary<string, object> manipulation = CalculateManipulationPosture(characteristics, opportunity, relationship, scene, romance);
            Dictionary<string, object> politicalRiskPosture =
                BuildPoliticalRiskPosture(characteristics, profile,
                    relationship, opportunity, scene, identityView,
                    payload, state, playerText, mode);
            Dictionary<string, object> governmentDecision =
                BuildGovernmentDecisionContext(payload);
            List<Dictionary<string, object>> activeDomains = SelectMotiveDomains(
                playerText, sceneContext, sanitizedState, payload, romance, ReadBool(manipulation, "recommended", false));
            Dictionary<string, object> relationshipSummary = CompactRelationshipEvidence(relationship);
            Dictionary<string, object> spouseRelationshipSummary = CompactRelationshipEvidence(spouseRelationship);
            Dictionary<string, object> highlighted = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (MotiveDomainDefinition domain in activeDomains.Select(item => MotiveDomainDefinitions.FirstOrDefault(x => x.Id == ReadString(item, "id", ""))).Where(x => x != null))
            {
                foreach (string key in domain.TraitKeys) highlighted[key] = ReadInt(percentages, key, 50);
            }
            foreach (string key in new[]
            {
                "boldness", "courage", "authorityRespect", "honor",
                "loyalty", "judgment"
            })
            {
                Dictionary<string, object> groups = ReadDictionary(ReadDictionary(characteristics, "traits"), "courtVirtues") ?? new Dictionary<string, object>();
                if (groups.ContainsKey(key)) highlighted[key] = ReadInt(groups, key, 50);
            }

            List<string> evidenceKeys = activeDomains.SelectMany(domain => ReadStringList(domain, "evidenceKeys"))
                .Concat(highlighted.Keys.Select(key => "trait." + key))
                .Concat(relationshipSummary.Keys.Select(key => "relationship.npc_to_target." + key))
                .Concat(new[]
                {
                    "courtCharacter.cell", "opportunity.relative",
                    "scene.constraints", "politicalRisk.authority",
                    "politicalRisk.danger", "politicalRisk.defianceCeiling",
                    "politicalRisk.addressMode"
                })
                .Concat(governmentDecision.Count == 0
                    ? new string[0]
                    : new[]
                    {
                        "government.authorityLevel",
                        "government.stanceCategory",
                        "government.ownMembership.partyPlanks",
                        "government.ownMembership.personalPowerAtStake"
                    })
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(36).ToList();
            List<Dictionary<string, object>> priorGroupTurns =
                ReadDictionaryList(payload, "groupTurnResponses");
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["model"] = MotiveDecisionModel,
                ["mode"] = mode ?? "dialogue",
                ["subjectId"] = subjectId ?? "",
                ["targetId"] = targetId ?? "",
                ["spouseId"] = spouseId,
                ["activeDomains"] = activeDomains,
                ["highlightedScores"] = highlighted,
                ["relationshipNpcToTarget"] = relationshipSummary,
                ["relationshipNpcToSpouse"] = spouseRelationshipSummary,
                ["opportunity"] = opportunity,
                ["courtCharacter"] = courtCharacter,
                ["manipulation"] = manipulation,
                ["politicalRiskPosture"] = politicalRiskPosture,
                ["governmentDecision"] = governmentDecision,
                ["fulfilledPaymentHistory"] = VerifiedAgreementPaymentHistory(payload, subjectId, targetId),
                ["scene"] = scene,
                ["romance"] = romance,
                ["requiredEvidenceKeys"] = evidenceKeys,
                ["identityView"] = Program.CloneDictionary(
                    identityView ?? new Dictionary<string, object>()),
                ["latestPlayerText"] = playerText ?? "",
                ["sanitizedState"] = sanitizedState,
                ["mustAccountForPriorSpeaker"] = ReadBool(
                    payload, "mustAccountForPriorSpeaker", false),
                ["groupTurnResponses"] = priorGroupTurns
                    .Skip(Math.Max(0, priorGroupTurns.Count - 5))
                    .Select(Program.CloneDictionary)
                    .ToList()
            };
            result["conversationAgency"] = BuildConversationAgencyContext(campaignId, subjectId, targetId, payload, profile, characteristics, result);
            result["prompt"] = BuildConversationDecisionPrompt(result)
                + "\n\n" + ReadString(
                    politicalRiskPosture, "prompt", "");
            return result;
        }

        internal static Dictionary<string, object> BuildGovernmentDecisionContext(
            Dictionary<string, object> payload)
        {
            Dictionary<string, object> political = ReadDictionary(
                payload, "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> government = ReadDictionary(
                political, "observerGovernment")
                ?? new Dictionary<string, object>();
            if (!ReadBool(government, "available", false))
                return new Dictionary<string, object>();

            Dictionary<string, object> membership = ReadDictionary(
                government, "ownMembership")
                ?? new Dictionary<string, object>();
            string partyId = ReadString(membership, "partyId", "");
            Dictionary<string, object> ownParty = ReadDictionaryList(
                    government, "parties")
                .FirstOrDefault(item => ReadString(item, "partyId", "")
                    .Equals(partyId, StringComparison.OrdinalIgnoreCase))
                ?? new Dictionary<string, object>();

            return new Dictionary<string, object>
            {
                ["authoritative"] = ReadBool(government,
                    "authoritative", true),
                ["institution"] = Program.LimitText(ReadString(government,
                    "institution", ""), 48),
                ["authorityLevel"] = ReadInt(government,
                    "authorityLevel", 0),
                ["stanceCategory"] = Program.LimitText(ReadString(government,
                    "stanceCategory", "divided"), 24),
                ["dominantPartyName"] = Program.LimitText(ReadString(government,
                    "dominantPartyName", ""), 48),
                ["ownMembership"] = new Dictionary<string, object>
                {
                    ["isMember"] = ReadBool(membership,
                        "isMember", false),
                    ["seatSource"] = Program.LimitText(ReadString(membership,
                        "seatSource", ""), 36),
                    ["partyName"] = Program.LimitText(ReadString(membership,
                        "partyName", ""), 48),
                    ["partyPlanks"] = ReadStringList(membership,
                            "partyPlanks")
                        .Take(4).Select(item => Program.LimitText(item, 32))
                        .ToList(),
                    ["isPartySpeaker"] = ReadBool(membership,
                        "isPartySpeaker", false),
                    ["personalPowerAtStake"] = ReadBool(membership,
                        "personalPowerAtStake", false),
                    ["partyIsDominant"] = ReadBool(ownParty,
                        "isDominant", false),
                    ["partyIsGoverningCoalition"] = ReadBool(ownParty,
                        "isGoverningCoalition", false)
                }
            };
        }

        private static Dictionary<string, object>
            SanitizeConversationStateForVerifiedIdentity(
                Dictionary<string, object> state,
                Dictionary<string, object> identityView,
                Dictionary<string, object> opportunity)
        {
            Dictionary<string, object> sanitized =
                Program.CloneDictionary(
                    state ?? new Dictionary<string, object>());
            identityView = identityView
                ?? new Dictionary<string, object>();
            if (!ReadBool(
                    identityView, "canonicalNameAllowed", false))
                return sanitized;

            string usable = ReadString(identityView, "usableName", "");
            string claimed = ReadString(
                identityView, "claimedName", "");
            foreach (string key in new[] { "currentPlan", "currentCrisis" })
            {
                string value = ReadString(sanitized, key, "");
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!string.IsNullOrWhiteSpace(usable)
                    && !string.IsNullOrWhiteSpace(claimed)
                    && !CharactersService.NormalizeIdentityName(claimed).Equals(
                        CharactersService.NormalizeIdentityName(usable),
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = Regex.Replace(
                        value,
                        @"(?<![\p{L}\p{N}])"
                            + Regex.Escape(claimed)
                            + @"(?![\p{L}\p{N}])",
                        usable,
                        RegexOptions.IgnoreCase
                            | RegexOptions.CultureInvariant);
                }
                if (KnownClanStandingContradiction(
                        value, opportunity))
                {
                    value = "";
                }
                if (VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] = value
                        },
                        identityView, checkSceneLocation: false))
                {
                    value = "";
                }
                string safeLabel = ReadString(
                    identityView, "safeLabel", "");
                if (!string.IsNullOrWhiteSpace(value)
                    && !string.IsNullOrWhiteSpace(safeLabel)
                    && Regex.IsMatch(
                        value,
                        @"(?<![\p{L}\p{N}])"
                            + Regex.Escape(safeLabel)
                            + @"(?![\p{L}\p{N}])",
                        RegexOptions.IgnoreCase
                            | RegexOptions.CultureInvariant))
                {
                    value = Regex.Replace(
                        value,
                        @"(?<![\p{L}\p{N}])"
                            + Regex.Escape(safeLabel)
                            + @"(?![\p{L}\p{N}])",
                        usable,
                        RegexOptions.IgnoreCase
                            | RegexOptions.CultureInvariant);
                }
                sanitized[key] = value;
            }
            return sanitized;
        }

        private static void EnsureServerConversationOpportunitySnapshot(
            string campaignId, string subjectId, string targetId,
            Dictionary<string, object> subjectProfile, Dictionary<string, object> subjectCharacteristics,
            Dictionary<string, object> payload, Dictionary<string, object> identityView)
        {
            if (payload == null || string.IsNullOrWhiteSpace(targetId)
                || ReadDictionary(payload, "opportunitySnapshot") != null
                || ReadDictionary(payload, "playerOpportunitySnapshot") != null) return;
            Dictionary<string, object> targetProfile = Program.ReadJsonObject(Program.CharacterFile(campaignId, targetId, "profile.json"));
            if (targetProfile.Count == 0
                && CharactersService.LoadCharacterProfileLibrary().TryGetValue(targetId, out Dictionary<string, object> shipped))
                targetProfile = Program.CloneDictionary(ReadDictionary(shipped, "sourceFacts") ?? shipped);
            Dictionary<string, object> targetCharacteristics = Program.LoadCharacterStack(campaignId, targetId);
            Dictionary<string, object> observer = OpportunityProfileSnapshot(subjectProfile, subjectCharacteristics);
            Dictionary<string, object> target = OpportunityProfileSnapshot(targetProfile, targetCharacteristics);
            if (target.Count == 0) return;
            bool known = new[] { "verified", "known", "recognized", "introduced" }
                .Contains(ReadString(identityView, "identityState", ""), StringComparer.OrdinalIgnoreCase);
            payload["opportunitySnapshot"] = new Dictionary<string, object>
            {
                ["model"] = "reign_relative_opportunity_v1",
                ["observer"] = observer,
                ["target"] = target,
                ["identityKnown"] = known
            };
        }

        private static Dictionary<string, object> OpportunityProfileSnapshot(
            Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            Dictionary<string, object> result = Program.CloneDictionary(profile ?? new Dictionary<string, object>());
            characteristics = characteristics ?? new Dictionary<string, object>();
            Dictionary<string, object> wealth = ReadDictionary(characteristics, "wealth");
            Dictionary<string, object> appearance = ReadDictionary(characteristics, "appearance");
            if (wealth != null && wealth.Count > 0) result["wealth"] = Program.CloneDictionary(wealth);
            if (appearance != null && appearance.Count > 0) result["appearance"] = Program.CloneDictionary(appearance);
            return result;
        }

        private static Dictionary<string, object> CompactRelationshipEvidence(Dictionary<string, object> relationship)
        {
            relationship = relationship ?? new Dictionary<string, object>();
            Dictionary<string, object> lifecycle = ReadDictionary(relationship, "lifecycle") ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["nativeRelation"] = ReadInt(relationship, "nativeRelation", 0),
                ["directionalAffinity"] = ReadInt(relationship, "directionalAffinity", ReadInt(relationship, "nativeRelation", 0)),
                ["perceptionTag"] = ReadString(relationship, "perceptionTag", "neutral"),
                ["observerMbti"] = ReadString(relationship, "observerMbti", ""),
                ["targetMbti"] = ReadString(relationship, "targetMbti", ""),
                ["compatibilityChance"] = ReadInt(relationship, "compatibilityChance", 0),
                ["lifecycleTags"] = ReadStringList(lifecycle, "tags"),
                ["recentIncidents"] = ReadDictionaryList(relationship, "incidents").Take(6)
                    .Select(item => new Dictionary<string, object>
                    {
                        ["kind"] = ReadString(item, "kind", ""),
                        ["worldDay"] = ReadDouble(item, "world_day", ReadDouble(item, "worldDay", 0d)),
                        ["summary"] = ReadString(item, "summary", "")
                    }).ToList()
            };
        }

        internal static Dictionary<string, object> CalculatePerceivedOpportunity(Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> payload, Dictionary<string, object> identityView)
        {
            Dictionary<string, object> snapshot = ReadDictionary(payload, "opportunitySnapshot") ?? ReadDictionary(payload, "playerOpportunitySnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(snapshot, "target") ?? ReadDictionary(payload, "playerIdentity") ?? new Dictionary<string, object>();
            Dictionary<string, object> observer = ReadDictionary(snapshot, "observer") ?? profile ?? new Dictionary<string, object>();
            bool identityKnown = ReadBool(snapshot, "identityKnown", false) || new[] { "verified", "known", "recognized", "introduced" }.Contains(ReadString(identityView, "identityState", ""), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> traitDocument = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> p = TraitPercentageSnapshot(traitDocument);

            Dictionary<string, double> targetAxes = AbsoluteOpportunityAxes(target, identityKnown, true);
            Dictionary<string, double> observerAxes = AbsoluteOpportunityAxes(observer, true, false);
            Dictionary<string, double> relative = targetAxes.Keys.ToDictionary(key => key, key => Program.ClampDouble(50d + targetAxes[key] - observerAxes[key], 0d, 100d), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> importance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["wealth"] = 0.15d + 0.65d * Trait01FromPercent(p, "wealthMotivation") + 0.35d * Trait01FromPercent(p, "greed"),
                ["power"] = 0.15d + 0.45d * Trait01FromPercent(p, "powerMotivation") + 0.35d * Trait01FromPercent(p, "ambition") + 0.20d * Trait01FromPercent(p, "pragmatism"),
                ["prestige"] = 0.15d + 0.45d * Trait01FromPercent(p, "fameMotivation") + 0.25d * Trait01FromPercent(p, "pride") + 0.15d * Trait01FromPercent(p, "envy") + 0.15d * Trait01FromPercent(p, "legacyMotivation"),
                ["protection"] = 0.15d + 0.45d * Trait01FromPercent(p, "survivalMotivation") + 0.20d * Trait01FromPercent(p, "fearfulness") + 0.20d * Trait01FromPercent(p, "pragmatism") + 0.15d * Trait01FromPercent(p, "familyMotivation"),
                ["dynasty"] = 0.15d + 0.35d * Trait01FromPercent(p, "legacyMotivation") + 0.30d * Trait01FromPercent(p, "familyMotivation") + 0.15d * Trait01FromPercent(p, "dutyMotivation") + 0.10d * Trait01FromPercent(p, "traditionalism") + 0.10d * Trait01FromPercent(p, "ambition")
            };
            double totalWeight = importance.Values.Sum();
            double benefit = totalWeight <= 0d ? 50d : importance.Sum(pair => pair.Value * relative[pair.Key]) / totalWeight;
            Dictionary<string, double> normalizedImportance = importance
                .ToDictionary(
                    pair => pair.Key,
                    pair => totalWeight <= 0d ? 0.2d : pair.Value / totalWeight,
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> opportunityGains = relative
                .ToDictionary(
                    pair => pair.Key,
                    pair => Program.ClampDouble(
                        Math.Max(0d, pair.Value - 50d) / 50d
                        * Program.ClampDouble(
                            0.45d + 2.75d * normalizedImportance[pair.Key],
                            0.45d, 1d),
                        0d, 1d),
                    StringComparer.OrdinalIgnoreCase);
            KeyValuePair<string, double> bestOpportunity =
                opportunityGains.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .First();
            List<string> reasons = relative.OrderByDescending(pair => Math.Abs(pair.Value - 50d) * importance[pair.Key])
                .Take(2).Select(pair => pair.Key + "=" + Math.Round(pair.Value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)).ToList();
            int observerClanTier = Clamp(ReadInt(observer, "clanTier", 0), 0, 6);
            int targetClanTier = Clamp(ReadInt(target, "clanTier", 0), 0, 6);
            int clanTierDelta = targetClanTier - observerClanTier;
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["identityBasis"] = identityKnown ? "known_public_identity" : "visible_presentation_only",
                ["targetClanTierKnown"] = identityKnown,
                ["observerClanTier"] = observerClanTier,
                ["relativeClanTierBand"] = identityKnown
                    ? RelativeClanTierBand(clanTierDelta)
                    : "unverified_identity",
                ["axes"] = relative.ToDictionary(pair => pair.Key, pair => (object)Math.Round(pair.Value, 1), StringComparer.OrdinalIgnoreCase),
                ["axisWeights"] = normalizedImportance.ToDictionary(
                    pair => pair.Key,
                    pair => (object)Math.Round(pair.Value, 3),
                    StringComparer.OrdinalIgnoreCase),
                ["relativeBenefit"] = Math.Round(benefit, 1),
                ["bestOpportunityAxis"] = bestOpportunity.Key,
                ["bestOpportunityValue"] =
                    Math.Round(relative[bestOpportunity.Key], 1),
                ["bestOpportunityGain"] =
                    Math.Round(bestOpportunity.Value, 3),
                ["wealthEvidenceBasis"] = ReadString(
                    ReadDictionary(target, "wealth"),
                    "wealthEvidenceBasis",
                    identityKnown
                        ? "known_identity_financial_context"
                        : "visible_presentation_only"),
                ["economicCapacityKnown"] = ReadBool(
                    ReadDictionary(target, "wealth"),
                    "economicCapacityKnown",
                    identityKnown),
                ["reasons"] = reasons
            };
            if (identityKnown)
            {
                result["targetClanTier"] = targetClanTier;
                result["clanTierDelta"] = clanTierDelta;
            }
            return result;
        }

        internal static string RelativeClanTierBand(int delta)
        {
            if (delta <= -2) return "substantially_lower";
            if (delta == -1) return "lower";
            if (delta == 0) return "peer";
            if (delta == 1) return "higher";
            return "substantially_higher";
        }

        private static Dictionary<string, double> AbsoluteOpportunityAxes(Dictionary<string, object> data, bool identityKnown, bool target)
        {
            Dictionary<string, object> wealth = ReadDictionary(data, "wealth") ?? data;
            Dictionary<string, object> appearance = ReadDictionary(data, "appearance") ?? data;
            double visibleStatus = ReadDouble(appearance, "visibleStatusScore", ReadDouble(data, "visibleStatusScore", 50d));
            double visibleAttractiveness = ReadDouble(appearance, "attractiveness", ReadDouble(data, "visibleAttractiveness", 50d));
            double gold = Math.Max(0d, ReadDouble(wealth, "gold", 0d));
            double clanGold = Math.Max(0d, ReadDouble(wealth, "clanGold", 0d));
            double clanTier = Program.ClampDouble(ReadDouble(wealth, "clanTier", ReadDouble(data, "clanTier", 0d)), 0d, 6d);
            double renown = Math.Max(0d, ReadDouble(wealth, "clanRenown", ReadDouble(data, "renown", 0d)));
            double fiefs = Math.Max(0d, ReadDouble(wealth, "clanFiefCount", ReadDouble(data, "fiefCount", 0d)));
            double influence = Math.Max(0d, ReadDouble(data, "influence", 0d));
            double leadership = Math.Max(0d, ReadDouble(data, "leadership", 0d));
            double partyStrength = Math.Max(0d, ReadDouble(data, "partyStrength", 0d));
            double clanStrength = Math.Max(0d, ReadDouble(data, "clanStrength", 0d));
            bool ruler = ReadBool(data, "isRuler", false);
            bool economicCapacityKnown = ReadBool(
                wealth, "economicCapacityKnown", identityKnown);

            double publicWealth = Program.ClampDouble(15d * Math.Log10(1d + gold / 250d) + 12d * Math.Log10(1d + clanGold / 2500d), 0d, 100d);
            double publicPower = Program.ClampDouble(clanTier * 11d + Math.Min(22d, fiefs * 7d) + Math.Min(12d, influence / 100d) + (ruler ? 18d : 0d), 0d, 100d);
            double publicPrestige = Program.ClampDouble(clanTier * 8d + Math.Min(40d, renown / 25d) + (ruler ? 15d : 0d), 0d, 100d);
            double publicProtection = Program.ClampDouble(clanTier * 8d + Math.Min(28d, partyStrength / 12d) + Math.Min(28d, clanStrength / 80d) + Math.Min(12d, leadership / 20d), 0d, 100d);
            double publicDynasty = Program.ClampDouble(clanTier * 10d + Math.Min(25d, fiefs * 8d) + Math.Min(15d, leadership / 20d) + (ruler ? 15d : 0d), 0d, 100d);
            if (target && (!identityKnown || !economicCapacityKnown))
            {
                publicWealth = Program.ClampDouble(0.65d * visibleStatus + 0.35d * ReadDouble(wealth, "visibleWealthScore", visibleStatus), 0d, 100d);
                publicPower = visibleStatus;
                publicPrestige = visibleStatus;
                publicProtection = Program.ClampDouble(0.65d * visibleStatus + 0.35d * Math.Min(100d, partyStrength / 10d), 0d, 100d);
                publicDynasty = visibleStatus;
            }
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["wealth"] = publicWealth,
                ["power"] = publicPower,
                ["prestige"] = publicPrestige,
                ["protection"] = publicProtection,
                ["dynasty"] = publicDynasty
            };
        }

        private static Dictionary<string, object> CalculateManipulationPosture(
            Dictionary<string, object> characteristics,
            Dictionary<string, object> opportunity,
            Dictionary<string, object> relationship,
            Dictionary<string, object> scene,
            Dictionary<string, object> romance)
        {
            Dictionary<string, object> traits = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> p = TraitPercentageSnapshot(traits);
            double drive = 100d * (
                0.20d * Trait01FromPercent(p, "ambition")
                + 0.20d * Trait01FromPercent(p, "powerMotivation")
                + 0.10d * Trait01FromPercent(p, "wealthMotivation")
                + 0.10d * Trait01FromPercent(p, "fameMotivation")
                + 0.15d * Trait01FromPercent(p, "pragmatism")
                + 0.15d * Trait01FromPercent(p, "tact")
                + 0.10d * Trait01FromPercent(p, "confidence"));
            double scruple = 100d * (
                0.25d * Trait01FromPercent(p, "honesty")
                + 0.20d * Trait01FromPercent(p, "empathy")
                + 0.15d * Trait01FromPercent(p, "loyalty")
                + 0.15d * Trait01FromPercent(p, "dutyMotivation")
                + 0.10d * Trait01FromPercent(p, "compassion")
                + 0.05d * Trait01FromPercent(p, "shame")
                + 0.10d * CourtGroup01(characteristics, "honor"));
            double benefit = Program.ClampDouble(ReadDouble(opportunity, "relativeBenefit", 50d), 0d, 100d);
            double bestOpportunityGain = Program.ClampDouble(
                ReadDouble(opportunity, "bestOpportunityGain", 0d), 0d, 1d);
            string bestOpportunityAxis = ReadString(
                opportunity, "bestOpportunityAxis", "");
            double exposure = Program.ClampDouble(ReadDouble(scene, "exposure", 0.5d), 0d, 1d);
            double tact = Trait01FromPercent(p, "tact");
            double safety = 100d * Program.ClampDouble(1d - exposure * (1d - 0.65d * tact), 0d, 1d);
            Dictionary<string, object> courtCharacter =
                CourtService.CourtCharacterDecisionSnapshot(characteristics, opportunity);
            double courtPropensity = Program.ClampDouble(ReadDouble(
                courtCharacter, "manipulationPropensity", 0d), 0d, 1d);
            double courtGain = Program.ClampDouble(ReadDouble(
                courtCharacter, "gainStrength", bestOpportunityGain), 0d, 1d);
            double courtFit = courtPropensity * courtGain;
            double score = Program.ClampDouble(
                0.48d * drive
                + 0.14d * benefit
                + 0.10d * safety
                - 0.22d * scruple
                + 30d * courtFit,
                0d, 100d);
            double standing = ReadDouble(relationship, "directionalAffinity", ReadDouble(relationship, "nativeRelation", 0d));
            bool recommended =
                ReadBool(courtCharacter, "supportsManipulation", false)
                && courtGain >= 0.15d
                && score >= 48d
                && standing > -60d;
            string clanTierBand = ReadString(opportunity, "relativeClanTierBand", "unverified_identity");
            List<string> allowedTactics = ReadStringList(
                courtCharacter, "tacticIds");
            List<string> tacticPreference = new List<string>();
            if (ReadString(romance, "presentation", "")
                    .Equals("calculated",
                        StringComparison.OrdinalIgnoreCase)
                && ReadDouble(romance, "receptivity", 0d) >= 60d)
                tacticPreference.Add("strategic_seduction");
            if (bestOpportunityAxis.Equals(
                    "wealth", StringComparison.OrdinalIgnoreCase))
                tacticPreference.AddRange(new[]
                {
                    "bargaining", "favors", "information", "secrets",
                    "schemes", "threats"
                });
            else if (new[] { "power", "prestige", "dynasty" }.Contains(
                    bestOpportunityAxis, StringComparer.OrdinalIgnoreCase))
                tacticPreference.AddRange(new[]
                {
                    "schemes", "bargaining", "flirtation",
                    "strategic_seduction", "secrets", "information",
                    "rivalry", "threats"
                });
            else
                tacticPreference.AddRange(new[]
                {
                    "bargaining", "information", "schemes", "secrets",
                    "favors", "threats", "rumors", "rivalry"
                });
            if (exposure >= 0.70d)
                tacticPreference.InsertRange(0, new[]
                {
                    "information", "secrets", "bargaining", "rumors"
                });
            string tactic = tacticPreference
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(candidate => allowedTactics.Contains(
                    candidate, StringComparer.OrdinalIgnoreCase))
                ?? allowedTactics.FirstOrDefault()
                ?? "none";
            bool dismissiveBoundary = !recommended
                && new[] { "lower", "substantially_lower" }.Contains(clanTierBand, StringComparer.OrdinalIgnoreCase)
                && benefit < 55d
                && (Trait01FromPercent(p, "pride") >= 0.65d
                    || Trait01FromPercent(p, "authorityRespect") >= 0.70d)
                && Trait01FromPercent(p, "sociability") < 0.60d;
            string expectedConduct = recommended
                ? tactic
                : dismissiveBoundary
                    ? "dismissive_boundary"
                    : clanTierBand == "substantially_higher" && benefit >= 60d
                        ? "cautious_deference_without_manipulation"
                        : "direct_or_restrained";
            return new Dictionary<string, object>
            {
                ["score"] = Math.Round(score, 1),
                ["drive"] = Math.Round(drive, 1),
                ["scruple"] = Math.Round(scruple, 1),
                ["relativeBenefit"] = Math.Round(benefit, 1),
                ["bestOpportunityAxis"] = bestOpportunityAxis,
                ["bestOpportunityGain"] =
                    Math.Round(bestOpportunityGain, 3),
                ["wealthEvidenceBasis"] = ReadString(
                    opportunity, "wealthEvidenceBasis", ""),
                ["exposureSafety"] = Math.Round(safety, 1),
                ["recommended"] = recommended,
                ["posture"] = score >= 72d ? "active_manipulation"
                    : score >= 52d ? "strategic_influence"
                    : score >= 42d ? "transactional_or_guarded"
                    : "direct_or_restrained",
                ["preferredTactic"] = tactic,
                ["allowedCourtTactics"] = allowedTactics,
                ["courtCharacterCell"] = ReadString(
                    courtCharacter, "cellId", ""),
                ["courtCharacterTitle"] = ReadString(
                    courtCharacter, "title", ""),
                ["courtHonorLevel"] = ReadInt(
                    courtCharacter, "honorLevel", 0),
                ["courtBoldnessLevel"] = ReadInt(
                    courtCharacter, "boldnessLevel", 0),
                ["courtManipulationPropensity"] =
                    Math.Round(courtPropensity, 3),
                ["courtOpportunityGain"] = Math.Round(courtGain, 3),
                ["courtOpportunityFit"] = Math.Round(courtFit, 3),
                ["relativeClanTierBand"] = clanTierBand,
                ["expectedConduct"] = expectedConduct,
                ["rule"] = "The fixed Court Character supplies manipulation propensity and permitted tactic families; motives, axis-specific opportunity, relationship, and exposure decide whether and how to act."
            };
        }

        internal static Dictionary<string, object> CalculateRomanticPosture(string campaignId, string subjectId, string targetId, Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> relationship, Dictionary<string, object> spouseRelationship, Dictionary<string, object> opportunity, Dictionary<string, object> payload, string mode, string playerText)
        {
            Dictionary<string, object> p = TraitPercentageSnapshot(ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>());
            Dictionary<string, object> snapshot = ReadDictionary(payload, "opportunitySnapshot") ?? ReadDictionary(payload, "playerOpportunitySnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(snapshot, "target") ?? ReadDictionary(payload, "playerIdentity") ?? new Dictionary<string, object>();
            Dictionary<string, object> targetAppearance = ReadDictionary(target, "appearance") ?? target;
            double directional = Program.ClampDouble(ReadDouble(relationship, "directionalAffinity",
                ReadDouble(relationship, "nativeRelation", 0d)), -100d, 100d);
            double relationPositive = Math.Max(0d, directional) / 100d;
            double relationNegative = Math.Max(0d, -directional) / 100d;
            Dictionary<string, object> lifecycle = ReadDictionary(relationship, "lifecycle") ?? new Dictionary<string, object>();
            bool establishedLovers = ReadBool(lifecycle, "lovers", false);
            bool activeAffair = ReadBool(lifecycle, "activeAffair", false);
            double visibleAttractive = Program.ClampDouble(ReadDouble(targetAppearance, "attractiveness", ReadDouble(target, "visibleAttractiveness", 50d)) / 100d, 0d, 1d);
            double genuine = 100d * (0.50d * relationPositive + 0.20d * Trait01FromPercent(p, "flirtatiousness")
                + 0.10d * Trait01FromPercent(p, "sociability") + 0.20d * visibleAttractive);
            if (establishedLovers) genuine += 20d;
            genuine -= 40d * relationNegative;
            genuine = Program.ClampDouble(genuine, 0d, 100d);
            double baseGenuine = genuine;

            double benefit = Program.ClampDouble(ReadDouble(opportunity, "relativeBenefit", 50d) / 100d, 0d, 1d);
            double strategicDisposition = 0.50d * Trait01FromPercent(p, "flirtatiousness") + 0.20d * Trait01FromPercent(p, "pragmatism") + 0.15d * Trait01FromPercent(p, "tact") + 0.10d * Trait01FromPercent(p, "confidence") + 0.05d * Trait01FromPercent(p, "riskTolerance");
            double strategic = Program.ClampDouble(100d * benefit * strategicDisposition, 0d, 100d);
            double baseStrategic = strategic;
            Dictionary<string, object> courtCharacter = CourtService.CourtCharacterDecisionSnapshot(characteristics, opportunity);
            double courtStrategicBoost = ReadDouble(courtCharacter, "strategicBoost", 0d);
            strategic = Program.ClampDouble(strategic + courtStrategicBoost, 0d, 100d);

            bool nativeMarried = !string.IsNullOrWhiteSpace(ReadString(profile, "spouseId", ""));
            string householdStatus = nativeMarried ? "" : CharactersService.CommonerNarrativeMaritalStatus(profile);
            bool married = nativeMarried || householdStatus == "married" || householdStatus == "separated";
            double spouseDirectional = Program.ClampDouble(ReadDouble(spouseRelationship, "directionalAffinity",
                ReadDouble(spouseRelationship, "nativeRelation", 0d)), -100d, 100d);
            Dictionary<string, object> spouseLifecycle = ReadDictionary(spouseRelationship, "lifecycle") ?? new Dictionary<string, object>();
            double maritalDiscontent = married
                ? Program.ClampDouble(Math.Max(0d, -spouseDirectional)
                    + (ReadBool(spouseLifecycle, "estranged", false) || householdStatus == "separated" ? 35d : 0d)
                    + (ReadBool(spouseLifecycle, "activeAffair", false) ? 20d : 0d), 0d, 100d)
                : 0d;
            double socialPressure = RomanticPressure01(characteristics, relationship, payload, playerText);
            double fire = 100d * (0.30d * Trait01FromPercent(p, "flirtatiousness") + 0.20d * Trait01FromPercent(p, "impulsiveness") + 0.15d * Trait01FromPercent(p, "riskTolerance") + 0.10d * Trait01FromPercent(p, "confidence") + 0.10d * socialPressure + 0.15d * (maritalDiscontent / 100d));
            double personalResistance = 100d * (0.18d * Trait01FromPercent(p, "loyalty") + 0.16d * Trait01FromPercent(p, "dutyMotivation") + 0.14d * Trait01FromPercent(p, "discipline") + 0.10d * Trait01FromPercent(p, "traditionalism") + 0.10d * Trait01FromPercent(p, "shame") + 0.10d * CourtGroup01(characteristics, "judgment") + 0.08d * Trait01FromPercent(p, "religionMotivation") + 0.07d * Trait01FromPercent(p, "familyMotivation") + 0.07d * CourtGroup01(characteristics, "honor"));
            Dictionary<string, object> scene = ConversationSceneSnapshot(mode, payload);
            double exposure = Program.ClampDouble(ReadDouble(scene, "exposure", 0.5d), 0d, 1d) * 100d;
            double spouseBond = married ? Program.ClampDouble(Math.Max(0d, spouseDirectional) + 20d, 0d, 100d) : 0d;
            double resistance = married ? 0.50d * personalResistance + 0.35d * spouseBond + 0.15d * exposure : 0.70d * personalResistance + 0.30d * exposure;
            Dictionary<string, object> suitability = ReadDictionary(snapshot, "suitability") ?? new Dictionary<string, object>();
            bool adults = ReadBool(suitability, "adults", ReadDouble(profile, "age", 0d) >= 18d && ReadDouble(target, "age", 0d) >= 18d);
            bool nativeSuitable = ReadBool(suitability, "nativeSuitable", ReadBool(suitability, "suitable", false));
            bool closeKin = ReadBool(suitability, "closeKin", false);
            bool coercive = ReadBool(scene, "coercive", false);

            string motiveChannel = baseGenuine >= baseStrategic + 15d ? "sincere"
                : baseStrategic >= baseGenuine + 15d ? "calculated" : "mixed";
            bool continuityEligible = establishedLovers && adults && !closeKin && !coercive;
            double continuityReceptivityBonus = continuityEligible ? 15d : 0d;

            double stronger = Math.Max(genuine, strategic), weaker = Math.Min(genuine, strategic);
            double existingInterest = stronger + 0.25d * weaker;
            double fireContribution = fire * Program.ClampDouble(existingInterest / 100d, 0d, 1d) * 0.25d;
            double approach = RomanticApproachModifier(playerText);
            double receptivity = Program.ClampDouble(50d + existingInterest + fireContribution + Math.Min(20d, maritalDiscontent * 0.20d)
                + approach - resistance + continuityReceptivityBonus, 0d, 100d);
            string band = receptivity < 25d ? "offended_or_firm_rejection" : receptivity < 45d ? "deflection_or_boundary" : receptivity < 60d ? "ambiguous_testing" : receptivity < 80d ? "receptive_escalation" : "ready_reciprocity";
            string presentation = genuine >= strategic + 15d ? "sincere" : strategic >= genuine + 15d ? "calculated" : "mixed";
            double concealment = 100d * (0.35d * Trait01FromPercent(p, "tact") + 0.25d * Trait01FromPercent(p, "pragmatism") + 0.20d * Trait01FromPercent(p, "confidence") + 0.20d * (1d - Trait01FromPercent(p, "honesty")));
            string disclosure = presentation != "calculated" ? "not_applicable" : concealment >= 67d ? "concealed" : concealment >= 40d ? "leaks_through" : "explicit_bargain";
            double initiativeScore = Program.ClampDouble(0.55d * receptivity + 0.20d * CourtGroup100(characteristics, "boldness") + 0.15d * ReadInt(p, "flirtatiousness", 50) + 0.10d * socialPressure * 100d
                + ReadDouble(courtCharacter, "initiativeBoost", 0d), 0d, 100d);
            double chance = initiativeScore < 45d ? 0d : initiativeScore < 60d ? 0.25d : initiativeScore < 75d ? 0.50d : initiativeScore < 90d ? 0.75d : 0.90d;
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string session = FirstNonEmpty(ReadFirstString(payload, "conversationSessionId", "sessionId", "eventId", "socialEventId"), (mode ?? "dialogue") + ":" + Math.Floor(worldDay).ToString(CultureInfo.InvariantCulture));
            bool mutualContinuity = continuityEligible;
            Dictionary<string, object> cooldown = ReadBool(payload, "skipCooldownQuery", false)
                ? new Dictionary<string, object> { ["blocked"] = false, ["reason"] = "self_test" }
                : ConversationMotiveCooldown(campaignId, subjectId, targetId, session, worldDay, mutualContinuity);
            bool eligible = adults && nativeSuitable && !closeKin && !coercive && Math.Max(genuine, strategic) >= 25d && !ReadBool(cooldown, "blocked", false);
            double roll = RelationshipsService.StableUnit((campaignId ?? "") + "|" + subjectId + "|" + targetId + "|" + session + "|" + Math.Floor(worldDay));
            bool recommended = eligible && chance > 0d && roll < chance;
            return new Dictionary<string, object>
            {
                ["baseGenuineInterest"] = Math.Round(baseGenuine, 1), ["baseStrategicInterest"] = Math.Round(baseStrategic, 1),
                ["genuineInterest"] = Math.Round(genuine, 1), ["strategicInterest"] = Math.Round(strategic, 1),
                ["courtCharacterStrategicBoost"] = Math.Round(courtStrategicBoost, 1),
                ["firePressure"] = Math.Round(fire, 1), ["maritalDiscontent"] = Math.Round(maritalDiscontent, 1),
                ["personalResistance"] = Math.Round(personalResistance, 1), ["spouseBond"] = Math.Round(spouseBond, 1),
                ["resistance"] = Math.Round(resistance, 1), ["receptivity"] = Math.Round(receptivity, 1),
                ["posture"] = band, ["presentation"] = presentation, ["calculationDisclosure"] = disclosure,
                ["continuity"] = new Dictionary<string, object>
                {
                    ["eligible"] = continuityEligible, ["route"] = establishedLovers ? "established_lovers" : "",
                    ["stage"] = activeAffair ? "active_affair" : establishedLovers ? "lovers" : "", ["motiveChannel"] = motiveChannel,
                    ["intensity"] = establishedLovers ? 100d : 0d,
                    ["genuineBonus"] = establishedLovers ? 20d : 0d, ["strategicBonus"] = 0d,
                    ["receptivityBonus"] = Math.Round(continuityReceptivityBonus, 1)
                },
                ["initiative"] = new Dictionary<string, object> { ["score"] = Math.Round(initiativeScore, 1), ["chance"] = chance, ["roll"] = Math.Round(roll, 4), ["eligible"] = eligible, ["recommended"] = recommended, ["cooldown"] = cooldown },
                ["courtCharacter"] = courtCharacter,
                ["married"] = married,
                ["maritalStatus"] = nativeMarried ? "married" : string.IsNullOrEmpty(householdStatus) ? "unrecorded" : householdStatus,
                ["maritalStatusSource"] = nativeMarried ? "native_spouse" : string.IsNullOrEmpty(householdStatus) ? "native_record_absence" : "saved_commoner_household",
                ["hardConstraints"] = new Dictionary<string, object> { ["adults"] = adults, ["nativeSuitable"] = nativeSuitable, ["closeKin"] = closeKin, ["coercive"] = coercive, ["consentRule"] = coercive ? "Placation or feigned interest is not consent and cannot authorize physical intimacy." : "Normal consent and game-action constraints still apply." }
            };
        }

        private static Dictionary<string, object> ConversationSceneSnapshot(string mode, Dictionary<string, object> payload)
        {
            Dictionary<string, object> opportunity = ReadDictionary(payload, "opportunitySnapshot") ?? ReadDictionary(payload, "playerOpportunitySnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> supplied = ReadDictionary(payload, "sceneOpportunity") ?? ReadDictionary(payload, "scene") ?? ReadDictionary(opportunity, "scene") ?? new Dictionary<string, object>();
            List<string> witnesses = ReadStringList(supplied, "witnessIds").Concat(ReadStringList(payload, "witnessHeroIds")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string m = (mode ?? ReadString(payload, "mode", "dialogue")).ToLowerInvariant();
            bool privateScene = ReadBool(supplied, "private", m == "correspondence");
            var present = ReadDictionaryList(ReadDictionary(payload, "conversationSceneState"), "current");
            var presentIds = present.Select(s => ReadString(s, "heroStringId", "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool acceptedPrivate = present.Count > 0 && present.Where(s => ReadString(s, "role", "") != "player")
                .All(s => ReadBool(s, "acceptedPrivateScene", false))
                && witnesses.All(presentIds.Contains) && !ReadBool(CurrentNarrativeClock(payload), "advanced", false);
            if (acceptedPrivate) privateScene = true;
            bool coercive = ReadBool(supplied, "coercive", false) || ReadBool(payload, "coercive", false) || ReadBool(payload, "playerIsCaptive", false) || ReadBool(payload, "speakerIsCaptive", false);
            double defaultExposure = privateScene ? 0.10d : m == "social_event" || m == "party_chat" ? 0.85d : 0.45d;
            double exposure = Program.ClampDouble(ReadDouble(supplied, "exposure", defaultExposure + Math.Min(0.15d, witnesses.Count * 0.03d)), 0d, 1d);
            if (acceptedPrivate) exposure = Math.Min(exposure, .10d + Math.Min(.15d, witnesses.Count * .03d));
            return new Dictionary<string, object> { ["private"] = privateScene, ["exposure"] = Math.Round(exposure, 2), ["witnessIds"] = witnesses, ["witnessCount"] = witnesses.Count, ["coercive"] = coercive };
        }

        internal static List<Dictionary<string, object>> SelectMotiveDomains(string playerText, string sceneContext, Dictionary<string, object> state, Dictionary<string, object> payload, Dictionary<string, object> romance, bool manipulationRecommended = false)
        {
            string deterministic = string.Join(" ", new[] { playerText, sceneContext, ReadString(state, "currentPlan", ""), ReadString(state, "currentCrisis", ""), ReadString(state, "temptation", ""), ReadString(state, "pressure", ""), ReadString(payload, "eventType", ""), ReadString(payload, "phaseId", ""), ReadString(payload, "turnType", "") }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
            List<string> helper = ReadStringList(ReadDictionary(payload, "contextSelection"), "topics")
                .Concat(ReadDictionaryList(payload, "selectedContextPulls").SelectMany(x => new[] { ReadString(x, "id", ""), ReadString(x, "reason", "") }))
                .SelectMany(text => MotiveDomainDefinitions.Where(domain => domain.Cues.Any(cue => (text ?? "").IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0)).Select(domain => domain.Id)).ToList();
            List<MotiveDomainDefinition> selected = MotiveDomainDefinitions.Where(domain => domain.Cues.Any(cue => deterministic.Contains(cue))).ToList();
            bool romanceRelevant = ReadBool(ReadDictionary(romance, "initiative"), "recommended", false)
                || ReadBool(ReadDictionary(romance, "continuity"), "eligible", false);
            if (romanceRelevant)
            {
                MotiveDomainDefinition romanceDomain =
                    MotiveDomainDefinitions.First(x => x.Id == "romance");
                selected.RemoveAll(x => x.Id == "romance");
                selected.Insert(0, romanceDomain);
            }
            if (manipulationRecommended)
            {
                MotiveDomainDefinition intrigueDomain =
                    MotiveDomainDefinitions.First(
                        x => x.Id == "intrigue_manipulation");
                selected.RemoveAll(
                    x => x.Id == "intrigue_manipulation");
                selected.Insert(
                    romanceRelevant ? Math.Min(1, selected.Count) : 0,
                    intrigueDomain);
            }
            foreach (string id in helper)
            {
                MotiveDomainDefinition domain = MotiveDomainDefinitions.FirstOrDefault(x => x.Id == id);
                if (domain != null && selected.All(x => x.Id != id)) selected.Add(domain);
            }
            return selected.Take(3).Select((domain, index) => new Dictionary<string, object>
            {
                ["id"] = domain.Id,
                ["priority"] = index == 0 ? "primary" : "secondary",
                ["instruction"] = domain.Instruction,
                ["evidenceKeys"] = domain.TraitKeys.Select(x => "trait." + x).Concat(domain.RelationshipKeys.Select(x => "relationship.npc_to_target." + x)).ToList()
            }).ToList();
        }

        internal static string BuildConversationDecisionPrompt(Dictionary<string, object> context)
            => BuildConversationDecisionPromptCore(context)
                + AgreementPaymentHistoryPrompt(ReadDictionary(context, "fulfilledPaymentHistory"));

        // Historical native deliveries have their own required allowance. Adding them
        // must not trim the existing personality, relationship or political context.
        private static string BuildConversationDecisionPromptCore(Dictionary<string, object> context)
        {
            List<Dictionary<string, object>> domains = ReadDictionaryList(context, "activeDomains");
            Dictionary<string, object> scores = new Dictionary<string, object>(ReadDictionary(context, "highlightedScores") ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> opportunity = Program.CloneDictionary(ReadDictionary(context, "opportunity") ?? new Dictionary<string, object>());
            Dictionary<string, object> relationship = ReadDictionary(context, "relationshipNpcToTarget") ?? new Dictionary<string, object>();
            Dictionary<string, object> spouseRelationship = ReadDictionary(context, "relationshipNpcToSpouse") ?? new Dictionary<string, object>();
            Dictionary<string, object> romance = ReadDictionary(context, "romance") ?? new Dictionary<string, object>();
            Dictionary<string, object> manipulation = ReadDictionary(context, "manipulation") ?? new Dictionary<string, object>();
            Dictionary<string, object> courtCharacter = ReadDictionary(context, "courtCharacter") ?? new Dictionary<string, object>();
            Dictionary<string, object> governmentDecision = ReadDictionary(
                context, "governmentDecision")
                ?? new Dictionary<string, object>();
            bool romanceRelevant = ReadDictionaryList(context, "activeDomains")
                .Any(domain => ReadString(domain, "id", "").Equals("romance", StringComparison.OrdinalIgnoreCase));
            bool manipulationRelevant = domains.Any(domain =>
                ReadString(domain, "id", "").Equals("intrigue_manipulation", StringComparison.OrdinalIgnoreCase));
            bool hasSpouse = !string.IsNullOrWhiteSpace(ReadString(context, "spouseId", "")) || ReadBool(romance, "married", false);
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["d"] = domains.Select(x => ReadString(x, "id", "")).ToList(),
                ["s"] = scores,
                ["r"] = CompactRelationshipForMotivePrompt(relationship, true),
                ["sr"] = CompactRelationshipForMotivePrompt(spouseRelationship, true),
                ["o"] = opportunity,
                ["cc"] = CompactCourtCharacterPrompt(courtCharacter),
                ["scene"] = CompactMotiveScenePrompt(ReadDictionary(context, "scene") ?? new Dictionary<string, object>()),
                ["rom"] = CompactRomancePrompt(romance, false),
                ["intr"] = new Dictionary<string, object>
                {
                    ["score"] = ReadDouble(manipulation, "score", 0d),
                    ["recommended"] = ReadBool(manipulation, "recommended", false),
                    ["posture"] = Program.LimitText(ReadString(manipulation, "posture", ""), 28),
                    ["preferredTactic"] = Program.LimitText(ReadString(manipulation, "preferredTactic", ""), 40),
                    ["expectedConduct"] = Program.LimitText(ReadString(manipulation, "expectedConduct", ""), 48),
                    ["bestAxis"] = Program.LimitText(ReadString(
                        manipulation, "bestOpportunityAxis", ""), 16),
                    ["bestGain"] = ReadDouble(
                        manipulation, "bestOpportunityGain", 0d),
                    ["wealthBasis"] = Program.LimitText(ReadString(
                        manipulation, "wealthEvidenceBasis", ""), 36),
                    ["courtFit"] = ReadDouble(
                        manipulation, "courtOpportunityFit", 0d)
                }
            };
            if (governmentDecision.Count > 0)
                compact["gov"] = governmentDecision;
            if (!romanceRelevant) compact.Remove("rom");
            if (!manipulationRelevant) compact.Remove("intr");
            if (!hasSpouse) compact.Remove("sr");
            string prefix = romanceRelevant
                ? "PRIVATE DECISION CONTEXT. cc is the fixed default posture; when cc.gain>0 strongly prefer its fitting tactics. Obey hard constraints; strategic desire is not genuine attraction. Record decisionBrief.courtTactic or none. Never expose scores. JSON:"
                : manipulationRelevant
                    ? "PRIVATE DECISION CONTEXT. cc is the fixed default posture; when cc.gain>0 strongly prefer its fitting tactics. Obey constraints and use intrigue only when supported. Record decisionBrief.courtTactic or none. Never expose scores. JSON:"
                    : "PRIVATE DECISION CONTEXT. cc is the fixed default posture; when cc.gain>0 strongly prefer its fitting tactics. Obey constraints. Record decisionBrief.courtTactic or none. Never expose scores. JSON:";
            bool targetClanTierKnown = ReadBool(
                opportunity, "targetClanTierKnown", false);
            prefix += targetClanTierKnown
                ? " VERIFIED-CLAN RULE: o tiers/band are authoritative public standing and override appearance. If asked rank, use the band. Never treat clan, house, rank, tier, or station as unknown, unconfirmed, or unproven; never call the target clanless, independent, without a banner, socially unrecognized, or contrary in relative rank. An exact clan name may remain unknown."
                : " UNVERIFIED-CLAN RULE: apparent presentation does not establish hidden clan affiliation or rank.";
            if (targetClanTierKnown
                && ReadString(
                    opportunity, "relativeClanTierBand", "")
                    .Equals("peer", StringComparison.OrdinalIgnoreCase))
            {
                prefix += " PEER-TIER RULE: equal clan tier is formal parity "
                    + "on that axis. Other supplied public holdings, office, "
                    + "wealth, renown, troops, or connections may create "
                    + "practical advantage, but never invent a clan-tier gap. "
                    + "Rank does not require automatic obedience.";
            }
            if (governmentDecision.Count > 0)
            {
                prefix += " GOVERNMENT RULE: gov is authoritative. "
                    + "Government authority levels are hidden mechanics. Never "
                    + "mention numbered levels or an authority scale in dialogue; "
                    + "describe a change naturally as a small, modest, or limited "
                    + "increase or reduction in government control. "
                    + "If gov.ownMembership.personalPowerAtStake is true, "
                    + "reducing government authority reduces this NPC's own "
                    + "institutional and party power. Weigh that self-interest, "
                    + "party planks, government stance, relationship, promises, "
                    + "and personality. Sovereign rank permits respectful "
                    + "disagreement and never implies consent. Do not invent "
                    + "support or refusal; decide from the supplied facts.";
            }
            string value = prefix + CanonicalJson(compact);
            if (value.Length <= MotiveLiveContextCap) return value;
            opportunity.Remove("reasons");
            value = prefix + CanonicalJson(compact);
            if (value.Length <= MotiveLiveContextCap) return value;
            opportunity.Remove("axisWeights");
            MotiveDomainDefinition primary = MotiveDomainDefinitions.FirstOrDefault(x => x.Id == ReadString(domains.FirstOrDefault(), "id", ""));
            HashSet<string> primaryKeys = new HashSet<string>(primary == null ? new string[0] : primary.TraitKeys, StringComparer.OrdinalIgnoreCase);
            compact["s"] = scores.Where(x => primaryKeys.Contains(x.Key) || new[] { "boldness", "honor", "loyalty", "judgment" }.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            value = prefix + CanonicalJson(compact);
            if (value.Length <= MotiveLiveContextCap) return value;
            compact["r"] = CompactRelationshipForMotivePrompt(relationship, false);
            if (hasSpouse) compact["sr"] = CompactRelationshipForMotivePrompt(spouseRelationship, false);
            if (romanceRelevant) compact["rom"] = CompactRomancePrompt(romance, true);
            value = prefix + CanonicalJson(compact);
            if (value.Length <= MotiveLiveContextCap) return value;
            string minimalPrefix = romanceRelevant
                ? "PRIVATE: cc fixed; prefer fitting cc tactics if gain>0; obey constraints; calculated!=genuine; record courtTactic:"
                : manipulationRelevant
                    ? "PRIVATE: cc fixed; prefer fitting cc tactics if gain>0; intrigue only if supported; record courtTactic:"
                    : "PRIVATE: cc fixed; prefer fitting cc tactics if gain>0; obey constraints; record courtTactic:";
            minimalPrefix += targetClanTierKnown
                ? " known clan tiers/band override appearance; if asked rank use band; never deny or call rank unknown:"
                : " clan rank unverified:";
            if (targetClanTierKnown
                && ReadString(
                    opportunity, "relativeClanTierBand", "")
                    .Equals("peer", StringComparison.OrdinalIgnoreCase))
            {
                minimalPrefix += " peer=same clan tier, not equal assets; "
                    + "other supplied public axes may differ; no invented tier gap:";
            }
            if (governmentDecision.Count > 0)
                minimalPrefix += " gov authoritative; levels hidden; never say "
                    + "numbered levels/scale; describe small/modest/limited control change; "
                    + "own power at stake means "
                    + "a reduction cuts this NPC's party/institutional power; "
                    + "weigh planks, stance, relation, promises, personality; "
                    + "rank never implies consent:";
            value = minimalPrefix + CanonicalJson(compact);
            if (value.Length <= MotiveLiveContextCap) return value;

            // This final schema contains only fixed-size numeric, boolean, enum, and bounded identifier fields.
            // It preserves every decision-critical axis while guaranteeing valid JSON instead of slicing text.
            compact["d"] = domains.Take(3).Select(x => Program.LimitText(ReadString(x, "id", ""), 32)).ToList();
            compact["s"] = ReadDictionary(compact, "s").Take(6)
                .ToDictionary(x => Program.LimitText(x.Key, 32), x => x.Value, StringComparer.OrdinalIgnoreCase);
            compact["r"] = MinimalRelationshipForMotivePrompt(relationship);
            if (hasSpouse) compact["sr"] = MinimalRelationshipForMotivePrompt(spouseRelationship);
            compact["o"] = MinimalOpportunityForMotivePrompt(opportunity);
            compact["cc"] = CompactCourtCharacterPrompt(courtCharacter, true);
            if (romanceRelevant) compact["rom"] = CompactRomancePrompt(romance, true);
            value = minimalPrefix + CanonicalJson(compact);
            if (value.Length <= MotiveLiveContextCap) return value;

            // Never reject a production conversation because optional motive
            // evidence exceeded this sub-packet's budget. Preserve the fixed
            // Court Character, verified opportunity, current relationship,
            // scene constraints, and adjudicated conduct in a final bounded
            // schema. Lower-priority descriptions, schemes, secondary scores,
            // and continuity detail remain available elsewhere in the full
            // prompt and audit record.
            Dictionary<string, object> ultra =
                new Dictionary<string, object>
                {
                    ["d"] = domains.Take(2)
                        .Select(x => Program.LimitText(
                            ReadString(x, "id", ""), 24))
                        .ToList(),
                    ["s"] = ReadDictionary(compact, "s").Take(4)
                        .ToDictionary(
                            x => Program.LimitText(x.Key, 24),
                            x => x.Value,
                            StringComparer.OrdinalIgnoreCase),
                    ["r"] = new Dictionary<string, object>
                    {
                        ["nativeRelation"] = ReadInt(
                            relationship, "nativeRelation", 0),
                        ["directionalAffinity"] = ReadInt(
                            relationship, "directionalAffinity",
                            ReadInt(relationship,
                                "nativeRelation", 0))
                    },
                    ["o"] = new Dictionary<string, object>
                    {
                        ["targetClanTierKnown"] = ReadBool(
                            opportunity,
                            "targetClanTierKnown", false),
                        ["observerClanTier"] = ReadBool(
                            opportunity,
                            "targetClanTierKnown", false)
                                ? ReadInt(opportunity,
                                    "observerClanTier", 0) : 0,
                        ["targetClanTier"] = ReadBool(
                            opportunity,
                            "targetClanTierKnown", false)
                                ? ReadInt(opportunity,
                                    "targetClanTier", 0) : 0,
                        ["clanTierDelta"] = ReadBool(
                            opportunity,
                            "targetClanTierKnown", false)
                                ? ReadInt(opportunity,
                                    "clanTierDelta", 0) : 0,
                        ["relativeClanTierBand"] = Program.LimitText(
                            ReadString(opportunity,
                                "relativeClanTierBand",
                                "unverified_identity"), 28),
                        ["relativeBenefit"] = Math.Round(
                            ReadDouble(opportunity,
                                "relativeBenefit", 0d), 1),
                        ["bestOpportunityAxis"] = Program.LimitText(
                            ReadString(opportunity,
                                "bestOpportunityAxis", ""), 16),
                        ["bestOpportunityGain"] = Math.Round(
                            ReadDouble(opportunity,
                                "bestOpportunityGain", 0d), 3),
                        ["wealthEvidenceBasis"] = Program.LimitText(
                            ReadString(opportunity,
                                "wealthEvidenceBasis", ""), 32),
                        ["economicCapacityKnown"] = ReadBool(
                            opportunity,
                            "economicCapacityKnown", false)
                    },
                    ["cc"] = new Dictionary<string, object>
                    {
                        ["cell"] = Program.LimitText(ReadString(
                            courtCharacter, "cellId", ""), 28),
                        ["h"] = ReadInt(courtCharacter,
                            "honorLevel", 0),
                        ["b"] = ReadInt(courtCharacter,
                            "boldnessLevel", 0),
                        ["gain"] = Math.Round(ReadDouble(
                            courtCharacter,
                            "gainStrength", 0d), 2),
                        ["propensity"] = Math.Round(ReadDouble(
                            courtCharacter,
                            "manipulationPropensity", 0d), 2),
                        ["supportsManipulation"] = ReadBool(
                            courtCharacter,
                            "supportsManipulation", false),
                        ["tactics"] = ReadStringList(
                            courtCharacter, "tacticIds").Take(4)
                            .Select(x => Program.LimitText(x, 24)).ToList()
                    },
                    ["scene"] = CompactMotiveScenePrompt(
                        ReadDictionary(context, "scene")
                            ?? new Dictionary<string, object>())
                };
            if (governmentDecision.Count > 0)
                ultra["gov"] = governmentDecision;
            if (manipulationRelevant)
            {
                ultra["intr"] = new Dictionary<string, object>
                {
                    ["recommended"] = ReadBool(
                        manipulation, "recommended", false),
                    ["preferredTactic"] = Program.LimitText(ReadString(
                        manipulation, "preferredTactic", ""), 32),
                    ["expectedConduct"] = Program.LimitText(ReadString(
                        manipulation, "expectedConduct", ""), 40),
                    ["score"] = Math.Round(ReadDouble(
                        manipulation, "score", 0d), 1)
                };
            }
            if (romanceRelevant)
            {
                Dictionary<string, object> hard =
                    ReadDictionary(romance, "hardConstraints")
                    ?? new Dictionary<string, object>();
                ultra["rom"] = new Dictionary<string, object>
                {
                    ["genuine"] = Math.Round(ReadDouble(
                        romance, "genuineInterest", 0d), 1),
                    ["strategic"] = Math.Round(ReadDouble(
                        romance, "strategicInterest", 0d), 1),
                    ["receptivity"] = Math.Round(ReadDouble(
                        romance, "receptivity", 0d), 1),
                    ["posture"] = Program.LimitText(ReadString(
                        romance, "posture", ""), 28),
                    ["presentation"] = Program.LimitText(ReadString(
                        romance, "presentation", ""), 18),
                    ["hardConstraints"] =
                        new Dictionary<string, object>
                        {
                            ["adults"] = ReadBool(
                                hard, "adults", false),
                            ["nativeSuitable"] = ReadBool(
                                hard, "nativeSuitable", false),
                            ["closeKin"] = ReadBool(
                                hard, "closeKin", false),
                            ["coercive"] = ReadBool(
                                hard, "coercive", false)
                        }
                };
            }
            value = minimalPrefix + CanonicalJson(ultra);
            if (value.Length > MotiveLiveContextCap)
            {
                // Defensive last resort: still return valid structured
                // decision evidence, never an HTTP 500 or a sliced JSON
                // fragment. All values below have fixed numeric/enum bounds.
                Dictionary<string, object> emergency =
                    new Dictionary<string, object>
                    {
                        ["o"] = ultra["o"],
                        ["cc"] = ultra["cc"],
                        ["scene"] = ultra["scene"]
                    };
                if (ultra.ContainsKey("gov"))
                    emergency["gov"] = ultra["gov"];
                if (ultra.ContainsKey("intr"))
                    emergency["intr"] = ultra["intr"];
                if (ultra.ContainsKey("rom"))
                    emergency["rom"] = ultra["rom"];
                value = "PRIVATE decision evidence:"
                    + CanonicalJson(emergency);
            }
            return value;
        }

        private static Dictionary<string, object> CompactCourtCharacterPrompt(Dictionary<string, object> courtCharacter, bool minimal = false)
        {
            courtCharacter = courtCharacter ?? new Dictionary<string, object>();
            if (minimal)
            {
                Dictionary<string, object> minimalCompact = new Dictionary<string, object>
                {
                    ["cell"] = Program.LimitText(ReadString(courtCharacter, "cellId", ""), 28),
                    ["title"] = Program.LimitText(ReadString(courtCharacter, "title", ""), 40),
                    ["gain"] = Math.Round(ReadDouble(courtCharacter, "gainStrength", 0d), 2),
                    ["weight"] = ReadDouble(courtCharacter, "matchingTacticWeight", 1d),
                    ["propensity"] = ReadDouble(
                        courtCharacter, "manipulationPropensity", 0d),
                    ["supportsManipulation"] = ReadBool(
                        courtCharacter, "supportsManipulation", false),
                    ["strategicBoost"] = ReadDouble(courtCharacter, "strategicBoost", 0d),
                    ["tactics"] = ReadStringList(courtCharacter, "tacticIds").Take(4)
                        .Select(x => Program.LimitText(x, 24)).ToList()
                };
                Dictionary<string, object> activeScheme =
                    ReadDictionary(courtCharacter, "activeScheme");
                if (activeScheme != null && activeScheme.Count > 0)
                {
                    minimalCompact["scheme"] =
                        new Dictionary<string, object>
                        {
                            ["id"] = Program.LimitText(ReadFirstString(
                                activeScheme, "schemeId",
                                "plotId", "id"), 40),
                            ["kind"] = Program.LimitText(ReadFirstString(
                                activeScheme, "kind",
                                "type"), 28),
                            ["status"] = Program.LimitText(ReadString(
                                activeScheme, "status", ""), 20)
                        };
                }
                if (!ReadBool(courtCharacter, "available", false))
                {
                    minimalCompact.Clear();
                    minimalCompact["available"] = false;
                }
                return minimalCompact;
            }
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["cell"] = Program.LimitText(ReadString(courtCharacter, "cellId", ""), 28),
                ["title"] = Program.LimitText(ReadString(courtCharacter, "title", ""), 40),
                ["h"] = ReadInt(courtCharacter, "honorLevel", 0),
                ["b"] = ReadInt(courtCharacter, "boldnessLevel", 0),
                ["gain"] = Math.Round(ReadDouble(courtCharacter, "gainStrength", 0d), 2),
                ["weight"] = ReadDouble(courtCharacter, "matchingTacticWeight", 1d),
                ["propensity"] = ReadDouble(
                    courtCharacter, "manipulationPropensity", 0d),
                ["supportsManipulation"] = ReadBool(
                    courtCharacter, "supportsManipulation", false),
                ["strategicBoost"] = ReadDouble(courtCharacter, "strategicBoost", 0d),
                ["tactics"] = ReadStringList(courtCharacter, "tacticIds").Take(8)
                    .Select(x => Program.LimitText(x, 28)).ToList(),
                ["actions"] = ReadStringList(courtCharacter, "preferredActions").Take(4)
                    .Select(x => Program.LimitText(x, 70)).ToList(),
                ["scheme"] = ReadDictionary(courtCharacter, "activeScheme") ?? new Dictionary<string, object>(),
                ["fixed"] = true
            };
            if (!ReadBool(courtCharacter, "available", false))
            {
                compact.Clear();
                compact["available"] = false;
            }
            return compact;
        }

        private static Dictionary<string, object> CompactRelationshipForMotivePrompt(Dictionary<string, object> relationship, bool includeIncidentSummaries)
        {
            relationship = relationship ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> incidents = ReadDictionaryList(relationship, "recentIncidents");
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["nativeRelation"] = ReadInt(relationship, "nativeRelation", 0),
                ["directionalAffinity"] = ReadInt(relationship, "directionalAffinity", ReadInt(relationship, "nativeRelation", 0)),
                ["perceptionTag"] = Program.LimitText(ReadString(relationship, "perceptionTag", "neutral"), 40),
                ["observerMbti"] = Program.LimitText(ReadString(relationship, "observerMbti", ""), 12),
                ["targetMbti"] = Program.LimitText(ReadString(relationship, "targetMbti", ""), 12),
                ["compatibilityChance"] = ReadInt(relationship, "compatibilityChance", 0),
                ["lifecycleTags"] = ReadStringList(relationship, "lifecycleTags").Take(8).Select(x => Program.LimitText(x, 40)).ToList(),
                ["recentIncidentCount"] = incidents.Count
            };
            compact["recentIncidents"] = includeIncidentSummaries
                ? (object)incidents.Take(3).Select(item => new Dictionary<string, object>
                {
                    ["kind"] = Program.LimitText(ReadString(item, "kind", ""), 40),
                    ["worldDay"] = Math.Round(ReadDouble(item, "worldDay", ReadDouble(item, "world_day", 0d)), 2),
                    ["summary"] = Program.LimitText(ReadString(item, "summary", ""), 120)
                }).ToList()
                : incidents.Take(3).Select(item => new Dictionary<string, object>
                {
                    ["kind"] = Program.LimitText(ReadString(item, "kind", ""), 32),
                    ["worldDay"] = Math.Round(ReadDouble(item, "worldDay", ReadDouble(item, "world_day", 0d)), 2)
                }).ToList();
            return compact;
        }

        private static Dictionary<string, object> MinimalRelationshipForMotivePrompt(Dictionary<string, object> relationship)
        {
            relationship = relationship ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["nativeRelation"] = ReadInt(relationship, "nativeRelation", 0),
                ["directionalAffinity"] = ReadInt(relationship, "directionalAffinity", ReadInt(relationship, "nativeRelation", 0)),
                ["lifecycleTags"] = ReadStringList(relationship, "lifecycleTags").Take(3).Select(x => Program.LimitText(x, 20)).ToList()
            };
        }

        private static Dictionary<string, object> CompactMotiveScenePrompt(Dictionary<string, object> scene)
        {
            scene = scene ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["private"] = ReadBool(scene, "private", false),
                ["exposure"] = Math.Round(ReadDouble(scene, "exposure", .5d), 2),
                ["witnessCount"] = ReadInt(scene, "witnessCount", ReadStringList(scene, "witnessIds").Count),
                ["coercive"] = ReadBool(scene, "coercive", false)
            };
        }

        private static Dictionary<string, object> MinimalOpportunityForMotivePrompt(Dictionary<string, object> opportunity)
        {
            opportunity = opportunity ?? new Dictionary<string, object>();
            Dictionary<string, object> axes = ReadDictionary(opportunity, "axes") ?? new Dictionary<string, object>();
            bool targetClanTierKnown = ReadBool(
                opportunity, "targetClanTierKnown", false);
            Dictionary<string, object> compact =
                new Dictionary<string, object>
            {
                ["identityBasis"] = Program.LimitText(ReadString(opportunity, "identityBasis", "visible_presentation_only"), 32),
                ["targetClanTierKnown"] = targetClanTierKnown,
                ["clanTierDelta"] = targetClanTierKnown
                    ? ReadInt(opportunity, "clanTierDelta", 0) : 0,
                ["relativeClanTierBand"] = Program.LimitText(ReadString(
                    opportunity, "relativeClanTierBand",
                    "unverified_identity"), 28),
                ["relativeBenefit"] = Math.Round(ReadDouble(opportunity, "relativeBenefit", 50d), 1),
                ["bestOpportunityAxis"] = Program.LimitText(ReadString(
                    opportunity, "bestOpportunityAxis", ""), 16),
                ["bestOpportunityGain"] = Math.Round(ReadDouble(
                    opportunity, "bestOpportunityGain", 0d), 3),
                ["wealthEvidenceBasis"] = Program.LimitText(ReadString(
                    opportunity, "wealthEvidenceBasis", ""), 36),
                ["economicCapacityKnown"] = ReadBool(
                    opportunity, "economicCapacityKnown", false),
                ["axes"] = axes.Take(5).ToDictionary(x => Program.LimitText(x.Key, 24), x => x.Value, StringComparer.OrdinalIgnoreCase)
            };
            if (targetClanTierKnown)
            {
                compact["observerClanTier"] = ReadInt(
                    opportunity, "observerClanTier", 0);
                compact["targetClanTier"] = ReadInt(
                    opportunity, "targetClanTier", 0);
            }
            return compact;
        }

        private static Dictionary<string, object> CompactRomancePrompt(Dictionary<string, object> romance, bool minimal)
        {
            romance = romance ?? new Dictionary<string, object>();
            Dictionary<string, object> continuity = ReadDictionary(romance, "continuity") ?? new Dictionary<string, object>();
            Dictionary<string, object> initiative = ReadDictionary(romance, "initiative") ?? new Dictionary<string, object>();
            Dictionary<string, object> cooldown = ReadDictionary(initiative, "cooldown") ?? new Dictionary<string, object>();
            Dictionary<string, object> hardConstraints = ReadDictionary(romance, "hardConstraints") ?? new Dictionary<string, object>();
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["genuine"] = ReadDouble(romance, "genuineInterest", 0d), ["strategic"] = ReadDouble(romance, "strategicInterest", 0d),
                ["fire"] = ReadDouble(romance, "firePressure", 0d), ["maritalDiscontent"] = ReadDouble(romance, "maritalDiscontent", 0d),
                ["resistance"] = ReadDouble(romance, "resistance", 0d), ["receptivity"] = ReadDouble(romance, "receptivity", 0d),
                ["posture"] = Program.LimitText(ReadString(romance, "posture", ""), 36), ["presentation"] = Program.LimitText(ReadString(romance, "presentation", ""), 20),
                ["calculationDisclosure"] = Program.LimitText(ReadString(romance, "calculationDisclosure", ""), 24), ["married"] = ReadBool(romance, "married", false),
                ["continuity"] = new Dictionary<string, object>
                {
                    ["eligible"] = ReadBool(continuity, "eligible", false),
                    ["route"] = Program.LimitText(ReadString(continuity, "route", ""), 24),
                    ["stage"] = Program.LimitText(ReadString(continuity, "stage", ""), 24),
                    ["motiveChannel"] = Program.LimitText(ReadString(continuity, "motiveChannel", ""), 16)
                },
                ["initiative"] = new Dictionary<string, object>
                {
                    ["eligible"] = ReadBool(initiative, "eligible", false),
                    ["recommended"] = ReadBool(initiative, "recommended", false),
                    ["cooldownBlocked"] = ReadBool(cooldown, "blocked", false),
                    ["cooldownReason"] = Program.LimitText(ReadString(cooldown, "reason", ""), 36)
                },
                ["hardConstraints"] = new Dictionary<string, object>
                {
                    ["adults"] = ReadBool(hardConstraints, "adults", false),
                    ["nativeSuitable"] = ReadBool(hardConstraints, "nativeSuitable", false),
                    ["closeKin"] = ReadBool(hardConstraints, "closeKin", false),
                    ["coercive"] = ReadBool(hardConstraints, "coercive", false),
                    ["consentRule"] = Program.LimitText(ReadString(hardConstraints, "consentRule", "Normal consent and game-action constraints still apply."), 100)
                }
            };
            if (!minimal)
            {
                Dictionary<string, object> compactContinuity = ReadDictionary(compact, "continuity");
                compactContinuity["intensity"] = ReadDouble(continuity, "intensity", 0d);
                compactContinuity["genuineBonus"] = ReadDouble(continuity, "genuineBonus", 0d);
                compactContinuity["strategicBonus"] = ReadDouble(continuity, "strategicBonus", 0d);
                compactContinuity["receptivityBonus"] = ReadDouble(continuity, "receptivityBonus", 0d);
                Dictionary<string, object> compactInitiative = ReadDictionary(compact, "initiative");
                compactInitiative["score"] = ReadDouble(initiative, "score", 0d);
                compactInitiative["chance"] = ReadDouble(initiative, "chance", 0d);
                compactInitiative["roll"] = ReadDouble(initiative, "roll", 0d);
            }
            return compact;
        }

        private static Dictionary<string, object> ConversationMotiveCooldown(string campaignId, string subjectId, string targetId, string sessionId, double worldDay, bool continuity)
        {
            if (continuity || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(targetId)) return new Dictionary<string, object> { ["blocked"] = false, ["reason"] = continuity ? "mutual_romantic_continuity" : "untracked" };
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                EnsureConversationMotiveSchema(connection);
                Dictionary<string, object> sameSession = Program.QuerySql(connection, "SELECT * FROM conversation_motive_cooldowns WHERE subject_id=$subject AND target_id=$target AND kind='romantic_advance' AND session_id=$session LIMIT 1;", new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId, ["session"] = sessionId ?? "" }).FirstOrDefault();
                Dictionary<string, object> recent = Program.QuerySql(connection, "SELECT * FROM conversation_motive_cooldowns WHERE subject_id=$subject AND target_id=$target AND kind='romantic_advance' ORDER BY world_day DESC LIMIT 1;", new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
                bool sessionBlocked = sameSession != null;
                bool dayBlocked = recent != null && worldDay - ReadDouble(recent, "world_day", 0d) < 3d;
                return new Dictionary<string, object> { ["blocked"] = sessionBlocked || dayBlocked, ["reason"] = sessionBlocked ? "one_unsolicited_advance_per_session" : dayBlocked ? "three_day_target_cooldown" : "available", ["lastDay"] = recent == null ? -1d : ReadDouble(recent, "world_day", 0d) };
            }
        }
    }
}
