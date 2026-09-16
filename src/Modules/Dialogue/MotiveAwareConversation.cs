using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int MotiveStableCharacterCap = 1000;
        private const int MotiveLiveContextCap = 1800;
        private const string MotiveDecisionModel = "reign_motive_decision_v5";

        private sealed class MotiveDomainDefinition
        {
            public string Id;
            public string[] Cues;
            public string[] TraitKeys;
            public string[] RelationshipKeys;
            public string Instruction;
        }

        private static readonly MotiveDomainDefinition[] MotiveDomainDefinitions =
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
                ["t"] = CoreTraitKeys.ToDictionary(key => key, key => (object)Clamp(ReadInt(percentages, key, 50), 0, 100), StringComparer.OrdinalIgnoreCase),
                ["g"] = CourtVirtueKeys.ToDictionary(key => key, key => (object)(virtues.ContainsKey(key) ? Clamp(ReadInt(virtues, key, 50), 0, 100) : -1), StringComparer.OrdinalIgnoreCase),
                ["cc"] = ReadBool(courtCharacter, "available", false) ? ReadString(courtCharacter, "cellId", "") : "unavailable"
            };
            string value = "PRIVATE MOTIVE VECTOR 0..100 (t=43 foundation traits,g=7 court groups,cc=fixed Court Character; -1=unavailable):" + CanonicalJson(compact);
            return value.Length <= MotiveStableCharacterCap ? value : LimitText(value, MotiveStableCharacterCap);
        }

        private static Dictionary<string, object> TraitPercentageSnapshot(Dictionary<string, object> traitDocument)
        {
            Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages") ?? new Dictionary<string, object>();
            Dictionary<string, object> foundations = ReadDictionary(traitDocument, "foundationTraits") ?? ReadDictionary(traitDocument, "hiddenReignTraits") ?? new Dictionary<string, object>();
            return CoreTraitKeys.ToDictionary(key => key, key =>
            {
                if (percentages.ContainsKey(key)) return (object)Clamp(ReadInt(percentages, key, 50), 0, 100);
                int level = Clamp(TraitInt(foundations, key), -2, 2);
                return (object)(50 + level * 20);
            }, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildConversationDecisionContext(
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
                relationship = RelationshipContextApi(new Dictionary<string, object>
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
                spouseRelationship = RelationshipContextApi(new Dictionary<string, object>
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
            Dictionary<string, object> courtCharacter = CourtCharacterDecisionSnapshot(characteristics, opportunity);
            courtCharacter["activeScheme"] = LoadCourtCharacterScheme(campaignId, subjectId, targetId, ReadDouble(payload, "worldDay", 0d));
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
                ["scene"] = scene,
                ["romance"] = romance,
                ["requiredEvidenceKeys"] = evidenceKeys,
                ["identityView"] = CloneDictionary(
                    identityView ?? new Dictionary<string, object>()),
                ["latestPlayerText"] = playerText ?? "",
                ["sanitizedState"] = sanitizedState,
                ["mustAccountForPriorSpeaker"] = ReadBool(
                    payload, "mustAccountForPriorSpeaker", false),
                ["groupTurnResponses"] = priorGroupTurns
                    .Skip(Math.Max(0, priorGroupTurns.Count - 5))
                    .Select(CloneDictionary)
                    .ToList()
            };
            result["prompt"] = BuildConversationDecisionPrompt(result)
                + "\n\n" + ReadString(
                    politicalRiskPosture, "prompt", "");
            return result;
        }

        private static Dictionary<string, object> BuildGovernmentDecisionContext(
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
                ["institution"] = LimitText(ReadString(government,
                    "institution", ""), 48),
                ["authorityLevel"] = ReadInt(government,
                    "authorityLevel", 0),
                ["stanceCategory"] = LimitText(ReadString(government,
                    "stanceCategory", "divided"), 24),
                ["dominantPartyName"] = LimitText(ReadString(government,
                    "dominantPartyName", ""), 48),
                ["ownMembership"] = new Dictionary<string, object>
                {
                    ["isMember"] = ReadBool(membership,
                        "isMember", false),
                    ["seatSource"] = LimitText(ReadString(membership,
                        "seatSource", ""), 36),
                    ["partyName"] = LimitText(ReadString(membership,
                        "partyName", ""), 48),
                    ["partyPlanks"] = ReadStringList(membership,
                            "partyPlanks")
                        .Take(4).Select(item => LimitText(item, 32))
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
                CloneDictionary(
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
                    && !NormalizeIdentityName(claimed).Equals(
                        NormalizeIdentityName(usable),
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
                        identityView))
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
            Dictionary<string, object> targetProfile = ReadJsonObject(CharacterFile(campaignId, targetId, "profile.json"));
            if (targetProfile.Count == 0
                && LoadCharacterProfileLibrary().TryGetValue(targetId, out Dictionary<string, object> shipped))
                targetProfile = CloneDictionary(ReadDictionary(shipped, "sourceFacts") ?? shipped);
            Dictionary<string, object> targetCharacteristics = LoadCharacterStack(campaignId, targetId);
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
            Dictionary<string, object> result = CloneDictionary(profile ?? new Dictionary<string, object>());
            characteristics = characteristics ?? new Dictionary<string, object>();
            Dictionary<string, object> wealth = ReadDictionary(characteristics, "wealth");
            Dictionary<string, object> appearance = ReadDictionary(characteristics, "appearance");
            if (wealth != null && wealth.Count > 0) result["wealth"] = CloneDictionary(wealth);
            if (appearance != null && appearance.Count > 0) result["appearance"] = CloneDictionary(appearance);
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

        private static Dictionary<string, object> CalculatePerceivedOpportunity(Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> payload, Dictionary<string, object> identityView)
        {
            Dictionary<string, object> snapshot = ReadDictionary(payload, "opportunitySnapshot") ?? ReadDictionary(payload, "playerOpportunitySnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(snapshot, "target") ?? ReadDictionary(payload, "playerIdentity") ?? new Dictionary<string, object>();
            Dictionary<string, object> observer = ReadDictionary(snapshot, "observer") ?? profile ?? new Dictionary<string, object>();
            bool identityKnown = ReadBool(snapshot, "identityKnown", false) || new[] { "verified", "known", "recognized", "introduced" }.Contains(ReadString(identityView, "identityState", ""), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> traitDocument = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> p = TraitPercentageSnapshot(traitDocument);

            Dictionary<string, double> targetAxes = AbsoluteOpportunityAxes(target, identityKnown, true);
            Dictionary<string, double> observerAxes = AbsoluteOpportunityAxes(observer, true, false);
            Dictionary<string, double> relative = targetAxes.Keys.ToDictionary(key => key, key => ClampDouble(50d + targetAxes[key] - observerAxes[key], 0d, 100d), StringComparer.OrdinalIgnoreCase);
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
                    pair => ClampDouble(
                        Math.Max(0d, pair.Value - 50d) / 50d
                        * ClampDouble(
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

        private static string RelativeClanTierBand(int delta)
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
            double clanTier = ClampDouble(ReadDouble(wealth, "clanTier", ReadDouble(data, "clanTier", 0d)), 0d, 6d);
            double renown = Math.Max(0d, ReadDouble(wealth, "clanRenown", ReadDouble(data, "renown", 0d)));
            double fiefs = Math.Max(0d, ReadDouble(wealth, "clanFiefCount", ReadDouble(data, "fiefCount", 0d)));
            double influence = Math.Max(0d, ReadDouble(data, "influence", 0d));
            double leadership = Math.Max(0d, ReadDouble(data, "leadership", 0d));
            double partyStrength = Math.Max(0d, ReadDouble(data, "partyStrength", 0d));
            double clanStrength = Math.Max(0d, ReadDouble(data, "clanStrength", 0d));
            bool ruler = ReadBool(data, "isRuler", false);
            bool economicCapacityKnown = ReadBool(
                wealth, "economicCapacityKnown", identityKnown);

            double publicWealth = ClampDouble(15d * Math.Log10(1d + gold / 250d) + 12d * Math.Log10(1d + clanGold / 2500d), 0d, 100d);
            double publicPower = ClampDouble(clanTier * 11d + Math.Min(22d, fiefs * 7d) + Math.Min(12d, influence / 100d) + (ruler ? 18d : 0d), 0d, 100d);
            double publicPrestige = ClampDouble(clanTier * 8d + Math.Min(40d, renown / 25d) + (ruler ? 15d : 0d), 0d, 100d);
            double publicProtection = ClampDouble(clanTier * 8d + Math.Min(28d, partyStrength / 12d) + Math.Min(28d, clanStrength / 80d) + Math.Min(12d, leadership / 20d), 0d, 100d);
            double publicDynasty = ClampDouble(clanTier * 10d + Math.Min(25d, fiefs * 8d) + Math.Min(15d, leadership / 20d) + (ruler ? 15d : 0d), 0d, 100d);
            if (target && (!identityKnown || !economicCapacityKnown))
            {
                publicWealth = ClampDouble(0.65d * visibleStatus + 0.35d * ReadDouble(wealth, "visibleWealthScore", visibleStatus), 0d, 100d);
                publicPower = visibleStatus;
                publicPrestige = visibleStatus;
                publicProtection = ClampDouble(0.65d * visibleStatus + 0.35d * Math.Min(100d, partyStrength / 10d), 0d, 100d);
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
            double benefit = ClampDouble(ReadDouble(opportunity, "relativeBenefit", 50d), 0d, 100d);
            double bestOpportunityGain = ClampDouble(
                ReadDouble(opportunity, "bestOpportunityGain", 0d), 0d, 1d);
            string bestOpportunityAxis = ReadString(
                opportunity, "bestOpportunityAxis", "");
            double exposure = ClampDouble(ReadDouble(scene, "exposure", 0.5d), 0d, 1d);
            double tact = Trait01FromPercent(p, "tact");
            double safety = 100d * ClampDouble(1d - exposure * (1d - 0.65d * tact), 0d, 1d);
            Dictionary<string, object> courtCharacter =
                CourtCharacterDecisionSnapshot(characteristics, opportunity);
            double courtPropensity = ClampDouble(ReadDouble(
                courtCharacter, "manipulationPropensity", 0d), 0d, 1d);
            double courtGain = ClampDouble(ReadDouble(
                courtCharacter, "gainStrength", bestOpportunityGain), 0d, 1d);
            double courtFit = courtPropensity * courtGain;
            double score = ClampDouble(
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

        private static Dictionary<string, object> CalculateRomanticPosture(string campaignId, string subjectId, string targetId, Dictionary<string, object> profile, Dictionary<string, object> characteristics, Dictionary<string, object> relationship, Dictionary<string, object> spouseRelationship, Dictionary<string, object> opportunity, Dictionary<string, object> payload, string mode, string playerText)
        {
            Dictionary<string, object> p = TraitPercentageSnapshot(ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>());
            Dictionary<string, object> snapshot = ReadDictionary(payload, "opportunitySnapshot") ?? ReadDictionary(payload, "playerOpportunitySnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(snapshot, "target") ?? ReadDictionary(payload, "playerIdentity") ?? new Dictionary<string, object>();
            Dictionary<string, object> targetAppearance = ReadDictionary(target, "appearance") ?? target;
            double directional = ClampDouble(ReadDouble(relationship, "directionalAffinity",
                ReadDouble(relationship, "nativeRelation", 0d)), -100d, 100d);
            double relationPositive = Math.Max(0d, directional) / 100d;
            double relationNegative = Math.Max(0d, -directional) / 100d;
            Dictionary<string, object> lifecycle = ReadDictionary(relationship, "lifecycle") ?? new Dictionary<string, object>();
            bool establishedLovers = ReadBool(lifecycle, "lovers", false);
            bool activeAffair = ReadBool(lifecycle, "activeAffair", false);
            double visibleAttractive = ClampDouble(ReadDouble(targetAppearance, "attractiveness", ReadDouble(target, "visibleAttractiveness", 50d)) / 100d, 0d, 1d);
            double genuine = 100d * (0.50d * relationPositive + 0.20d * Trait01FromPercent(p, "flirtatiousness")
                + 0.10d * Trait01FromPercent(p, "sociability") + 0.20d * visibleAttractive);
            if (establishedLovers) genuine += 20d;
            genuine -= 40d * relationNegative;
            genuine = ClampDouble(genuine, 0d, 100d);
            double baseGenuine = genuine;

            double benefit = ClampDouble(ReadDouble(opportunity, "relativeBenefit", 50d) / 100d, 0d, 1d);
            double strategicDisposition = 0.50d * Trait01FromPercent(p, "flirtatiousness") + 0.20d * Trait01FromPercent(p, "pragmatism") + 0.15d * Trait01FromPercent(p, "tact") + 0.10d * Trait01FromPercent(p, "confidence") + 0.05d * Trait01FromPercent(p, "riskTolerance");
            double strategic = ClampDouble(100d * benefit * strategicDisposition, 0d, 100d);
            double baseStrategic = strategic;
            Dictionary<string, object> courtCharacter = CourtCharacterDecisionSnapshot(characteristics, opportunity);
            double courtStrategicBoost = ReadDouble(courtCharacter, "strategicBoost", 0d);
            strategic = ClampDouble(strategic + courtStrategicBoost, 0d, 100d);

            bool nativeMarried = !string.IsNullOrWhiteSpace(ReadString(profile, "spouseId", ""));
            string householdStatus = nativeMarried ? "" : CommonerNarrativeMaritalStatus(profile);
            bool married = nativeMarried || householdStatus == "married" || householdStatus == "separated";
            double spouseDirectional = ClampDouble(ReadDouble(spouseRelationship, "directionalAffinity",
                ReadDouble(spouseRelationship, "nativeRelation", 0d)), -100d, 100d);
            Dictionary<string, object> spouseLifecycle = ReadDictionary(spouseRelationship, "lifecycle") ?? new Dictionary<string, object>();
            double maritalDiscontent = married
                ? ClampDouble(Math.Max(0d, -spouseDirectional)
                    + (ReadBool(spouseLifecycle, "estranged", false) || householdStatus == "separated" ? 35d : 0d)
                    + (ReadBool(spouseLifecycle, "activeAffair", false) ? 20d : 0d), 0d, 100d)
                : 0d;
            double socialPressure = RomanticPressure01(characteristics, relationship, payload, playerText);
            double fire = 100d * (0.30d * Trait01FromPercent(p, "flirtatiousness") + 0.20d * Trait01FromPercent(p, "impulsiveness") + 0.15d * Trait01FromPercent(p, "riskTolerance") + 0.10d * Trait01FromPercent(p, "confidence") + 0.10d * socialPressure + 0.15d * (maritalDiscontent / 100d));
            double personalResistance = 100d * (0.18d * Trait01FromPercent(p, "loyalty") + 0.16d * Trait01FromPercent(p, "dutyMotivation") + 0.14d * Trait01FromPercent(p, "discipline") + 0.10d * Trait01FromPercent(p, "traditionalism") + 0.10d * Trait01FromPercent(p, "shame") + 0.10d * CourtGroup01(characteristics, "judgment") + 0.08d * Trait01FromPercent(p, "religionMotivation") + 0.07d * Trait01FromPercent(p, "familyMotivation") + 0.07d * CourtGroup01(characteristics, "honor"));
            Dictionary<string, object> scene = ConversationSceneSnapshot(mode, payload);
            double exposure = ClampDouble(ReadDouble(scene, "exposure", 0.5d), 0d, 1d) * 100d;
            double spouseBond = married ? ClampDouble(Math.Max(0d, spouseDirectional) + 20d, 0d, 100d) : 0d;
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
            double fireContribution = fire * ClampDouble(existingInterest / 100d, 0d, 1d) * 0.25d;
            double approach = RomanticApproachModifier(playerText);
            double receptivity = ClampDouble(50d + existingInterest + fireContribution + Math.Min(20d, maritalDiscontent * 0.20d)
                + approach - resistance + continuityReceptivityBonus, 0d, 100d);
            string band = receptivity < 25d ? "offended_or_firm_rejection" : receptivity < 45d ? "deflection_or_boundary" : receptivity < 60d ? "ambiguous_testing" : receptivity < 80d ? "receptive_escalation" : "ready_reciprocity";
            string presentation = genuine >= strategic + 15d ? "sincere" : strategic >= genuine + 15d ? "calculated" : "mixed";
            double concealment = 100d * (0.35d * Trait01FromPercent(p, "tact") + 0.25d * Trait01FromPercent(p, "pragmatism") + 0.20d * Trait01FromPercent(p, "confidence") + 0.20d * (1d - Trait01FromPercent(p, "honesty")));
            string disclosure = presentation != "calculated" ? "not_applicable" : concealment >= 67d ? "concealed" : concealment >= 40d ? "leaks_through" : "explicit_bargain";
            double initiativeScore = ClampDouble(0.55d * receptivity + 0.20d * CourtGroup100(characteristics, "boldness") + 0.15d * ReadInt(p, "flirtatiousness", 50) + 0.10d * socialPressure * 100d
                + ReadDouble(courtCharacter, "initiativeBoost", 0d), 0d, 100d);
            double chance = initiativeScore < 45d ? 0d : initiativeScore < 60d ? 0.25d : initiativeScore < 75d ? 0.50d : initiativeScore < 90d ? 0.75d : 0.90d;
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string session = FirstNonEmpty(ReadFirstString(payload, "conversationSessionId", "sessionId", "eventId", "socialEventId"), (mode ?? "dialogue") + ":" + Math.Floor(worldDay).ToString(CultureInfo.InvariantCulture));
            bool mutualContinuity = continuityEligible;
            Dictionary<string, object> cooldown = ReadBool(payload, "skipCooldownQuery", false)
                ? new Dictionary<string, object> { ["blocked"] = false, ["reason"] = "self_test" }
                : ConversationMotiveCooldown(campaignId, subjectId, targetId, session, worldDay, mutualContinuity);
            bool eligible = adults && nativeSuitable && !closeKin && !coercive && Math.Max(genuine, strategic) >= 25d && !ReadBool(cooldown, "blocked", false);
            double roll = StableUnit((campaignId ?? "") + "|" + subjectId + "|" + targetId + "|" + session + "|" + Math.Floor(worldDay));
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
            bool coercive = ReadBool(supplied, "coercive", false) || ReadBool(payload, "coercive", false) || ReadBool(payload, "playerIsCaptive", false) || ReadBool(payload, "speakerIsCaptive", false);
            double defaultExposure = privateScene ? 0.10d : m == "social_event" || m == "party_chat" ? 0.85d : 0.45d;
            double exposure = ClampDouble(ReadDouble(supplied, "exposure", defaultExposure + Math.Min(0.15d, witnesses.Count * 0.03d)), 0d, 1d);
            return new Dictionary<string, object> { ["private"] = privateScene, ["exposure"] = Math.Round(exposure, 2), ["witnessIds"] = witnesses, ["witnessCount"] = witnesses.Count, ["coercive"] = coercive };
        }

        private static List<Dictionary<string, object>> SelectMotiveDomains(string playerText, string sceneContext, Dictionary<string, object> state, Dictionary<string, object> payload, Dictionary<string, object> romance, bool manipulationRecommended = false)
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

        private static string BuildConversationDecisionPrompt(Dictionary<string, object> context)
        {
            List<Dictionary<string, object>> domains = ReadDictionaryList(context, "activeDomains");
            Dictionary<string, object> scores = new Dictionary<string, object>(ReadDictionary(context, "highlightedScores") ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> opportunity = CloneDictionary(ReadDictionary(context, "opportunity") ?? new Dictionary<string, object>());
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
                    ["posture"] = LimitText(ReadString(manipulation, "posture", ""), 28),
                    ["preferredTactic"] = LimitText(ReadString(manipulation, "preferredTactic", ""), 40),
                    ["expectedConduct"] = LimitText(ReadString(manipulation, "expectedConduct", ""), 48),
                    ["bestAxis"] = LimitText(ReadString(
                        manipulation, "bestOpportunityAxis", ""), 16),
                    ["bestGain"] = ReadDouble(
                        manipulation, "bestOpportunityGain", 0d),
                    ["wealthBasis"] = LimitText(ReadString(
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
            compact["d"] = domains.Take(3).Select(x => LimitText(ReadString(x, "id", ""), 32)).ToList();
            compact["s"] = ReadDictionary(compact, "s").Take(6)
                .ToDictionary(x => LimitText(x.Key, 32), x => x.Value, StringComparer.OrdinalIgnoreCase);
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
                        .Select(x => LimitText(
                            ReadString(x, "id", ""), 24))
                        .ToList(),
                    ["s"] = ReadDictionary(compact, "s").Take(4)
                        .ToDictionary(
                            x => LimitText(x.Key, 24),
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
                        ["relativeClanTierBand"] = LimitText(
                            ReadString(opportunity,
                                "relativeClanTierBand",
                                "unverified_identity"), 28),
                        ["relativeBenefit"] = Math.Round(
                            ReadDouble(opportunity,
                                "relativeBenefit", 0d), 1),
                        ["bestOpportunityAxis"] = LimitText(
                            ReadString(opportunity,
                                "bestOpportunityAxis", ""), 16),
                        ["bestOpportunityGain"] = Math.Round(
                            ReadDouble(opportunity,
                                "bestOpportunityGain", 0d), 3),
                        ["wealthEvidenceBasis"] = LimitText(
                            ReadString(opportunity,
                                "wealthEvidenceBasis", ""), 32),
                        ["economicCapacityKnown"] = ReadBool(
                            opportunity,
                            "economicCapacityKnown", false)
                    },
                    ["cc"] = new Dictionary<string, object>
                    {
                        ["cell"] = LimitText(ReadString(
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
                            .Select(x => LimitText(x, 24)).ToList()
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
                    ["preferredTactic"] = LimitText(ReadString(
                        manipulation, "preferredTactic", ""), 32),
                    ["expectedConduct"] = LimitText(ReadString(
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
                    ["posture"] = LimitText(ReadString(
                        romance, "posture", ""), 28),
                    ["presentation"] = LimitText(ReadString(
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
                    ["cell"] = LimitText(ReadString(courtCharacter, "cellId", ""), 28),
                    ["title"] = LimitText(ReadString(courtCharacter, "title", ""), 40),
                    ["gain"] = Math.Round(ReadDouble(courtCharacter, "gainStrength", 0d), 2),
                    ["weight"] = ReadDouble(courtCharacter, "matchingTacticWeight", 1d),
                    ["propensity"] = ReadDouble(
                        courtCharacter, "manipulationPropensity", 0d),
                    ["supportsManipulation"] = ReadBool(
                        courtCharacter, "supportsManipulation", false),
                    ["strategicBoost"] = ReadDouble(courtCharacter, "strategicBoost", 0d),
                    ["tactics"] = ReadStringList(courtCharacter, "tacticIds").Take(4)
                        .Select(x => LimitText(x, 24)).ToList()
                };
                Dictionary<string, object> activeScheme =
                    ReadDictionary(courtCharacter, "activeScheme");
                if (activeScheme != null && activeScheme.Count > 0)
                {
                    minimalCompact["scheme"] =
                        new Dictionary<string, object>
                        {
                            ["id"] = LimitText(ReadFirstString(
                                activeScheme, "schemeId",
                                "plotId", "id"), 40),
                            ["kind"] = LimitText(ReadFirstString(
                                activeScheme, "kind",
                                "type"), 28),
                            ["status"] = LimitText(ReadString(
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
                ["cell"] = LimitText(ReadString(courtCharacter, "cellId", ""), 28),
                ["title"] = LimitText(ReadString(courtCharacter, "title", ""), 40),
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
                    .Select(x => LimitText(x, 28)).ToList(),
                ["actions"] = ReadStringList(courtCharacter, "preferredActions").Take(4)
                    .Select(x => LimitText(x, 70)).ToList(),
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
                ["perceptionTag"] = LimitText(ReadString(relationship, "perceptionTag", "neutral"), 40),
                ["observerMbti"] = LimitText(ReadString(relationship, "observerMbti", ""), 12),
                ["targetMbti"] = LimitText(ReadString(relationship, "targetMbti", ""), 12),
                ["compatibilityChance"] = ReadInt(relationship, "compatibilityChance", 0),
                ["lifecycleTags"] = ReadStringList(relationship, "lifecycleTags").Take(8).Select(x => LimitText(x, 40)).ToList(),
                ["recentIncidentCount"] = incidents.Count
            };
            compact["recentIncidents"] = includeIncidentSummaries
                ? (object)incidents.Take(3).Select(item => new Dictionary<string, object>
                {
                    ["kind"] = LimitText(ReadString(item, "kind", ""), 40),
                    ["worldDay"] = Math.Round(ReadDouble(item, "worldDay", ReadDouble(item, "world_day", 0d)), 2),
                    ["summary"] = LimitText(ReadString(item, "summary", ""), 120)
                }).ToList()
                : incidents.Take(3).Select(item => new Dictionary<string, object>
                {
                    ["kind"] = LimitText(ReadString(item, "kind", ""), 32),
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
                ["lifecycleTags"] = ReadStringList(relationship, "lifecycleTags").Take(3).Select(x => LimitText(x, 20)).ToList()
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
                ["identityBasis"] = LimitText(ReadString(opportunity, "identityBasis", "visible_presentation_only"), 32),
                ["targetClanTierKnown"] = targetClanTierKnown,
                ["clanTierDelta"] = targetClanTierKnown
                    ? ReadInt(opportunity, "clanTierDelta", 0) : 0,
                ["relativeClanTierBand"] = LimitText(ReadString(
                    opportunity, "relativeClanTierBand",
                    "unverified_identity"), 28),
                ["relativeBenefit"] = Math.Round(ReadDouble(opportunity, "relativeBenefit", 50d), 1),
                ["bestOpportunityAxis"] = LimitText(ReadString(
                    opportunity, "bestOpportunityAxis", ""), 16),
                ["bestOpportunityGain"] = Math.Round(ReadDouble(
                    opportunity, "bestOpportunityGain", 0d), 3),
                ["wealthEvidenceBasis"] = LimitText(ReadString(
                    opportunity, "wealthEvidenceBasis", ""), 36),
                ["economicCapacityKnown"] = ReadBool(
                    opportunity, "economicCapacityKnown", false),
                ["axes"] = axes.Take(5).ToDictionary(x => LimitText(x.Key, 24), x => x.Value, StringComparer.OrdinalIgnoreCase)
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
                ["posture"] = LimitText(ReadString(romance, "posture", ""), 36), ["presentation"] = LimitText(ReadString(romance, "presentation", ""), 20),
                ["calculationDisclosure"] = LimitText(ReadString(romance, "calculationDisclosure", ""), 24), ["married"] = ReadBool(romance, "married", false),
                ["continuity"] = new Dictionary<string, object>
                {
                    ["eligible"] = ReadBool(continuity, "eligible", false),
                    ["route"] = LimitText(ReadString(continuity, "route", ""), 24),
                    ["stage"] = LimitText(ReadString(continuity, "stage", ""), 24),
                    ["motiveChannel"] = LimitText(ReadString(continuity, "motiveChannel", ""), 16)
                },
                ["initiative"] = new Dictionary<string, object>
                {
                    ["eligible"] = ReadBool(initiative, "eligible", false),
                    ["recommended"] = ReadBool(initiative, "recommended", false),
                    ["cooldownBlocked"] = ReadBool(cooldown, "blocked", false),
                    ["cooldownReason"] = LimitText(ReadString(cooldown, "reason", ""), 36)
                },
                ["hardConstraints"] = new Dictionary<string, object>
                {
                    ["adults"] = ReadBool(hardConstraints, "adults", false),
                    ["nativeSuitable"] = ReadBool(hardConstraints, "nativeSuitable", false),
                    ["closeKin"] = ReadBool(hardConstraints, "closeKin", false),
                    ["coercive"] = ReadBool(hardConstraints, "coercive", false),
                    ["consentRule"] = LimitText(ReadString(hardConstraints, "consentRule", "Normal consent and game-action constraints still apply."), 100)
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureConversationMotiveSchema(connection);
                Dictionary<string, object> sameSession = QuerySql(connection, "SELECT * FROM conversation_motive_cooldowns WHERE subject_id=$subject AND target_id=$target AND kind='romantic_advance' AND session_id=$session LIMIT 1;", new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId, ["session"] = sessionId ?? "" }).FirstOrDefault();
                Dictionary<string, object> recent = QuerySql(connection, "SELECT * FROM conversation_motive_cooldowns WHERE subject_id=$subject AND target_id=$target AND kind='romantic_advance' ORDER BY world_day DESC LIMIT 1;", new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
                bool sessionBlocked = sameSession != null;
                bool dayBlocked = recent != null && worldDay - ReadDouble(recent, "world_day", 0d) < 3d;
                return new Dictionary<string, object> { ["blocked"] = sessionBlocked || dayBlocked, ["reason"] = sessionBlocked ? "one_unsolicited_advance_per_session" : dayBlocked ? "three_day_target_cooldown" : "available", ["lastDay"] = recent == null ? -1d : ReadDouble(recent, "world_day", 0d) };
            }
        }

        private static void EnsureConversationMotiveSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_motive_cooldowns (
cooldown_id TEXT PRIMARY KEY, subject_id TEXT NOT NULL, target_id TEXT NOT NULL, kind TEXT NOT NULL,
session_id TEXT NOT NULL DEFAULT '', world_day REAL NOT NULL DEFAULT 0, created_ts INTEGER NOT NULL,
UNIQUE(subject_id,target_id,kind,session_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_motive_cooldown_pair ON conversation_motive_cooldowns(subject_id,target_id,kind,world_day DESC);");
        }

        private static Dictionary<string, object> ApplyConversationMotiveOutcome(string campaignId, Dictionary<string, object> context, Dictionary<string, object> payload, string visibleReply, string privateIntent, string decisionId, Dictionary<string, object> decisionBrief)
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureConversationMotiveSchema(connection);
                    ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_motive_cooldowns(cooldown_id,subject_id,target_id,kind,session_id,world_day,created_ts) VALUES($id,$subject,$target,'romantic_advance',$session,$day,$ts);", new Dictionary<string, object> { ["id"] = "mcd_" + Guid.NewGuid().ToString("N"), ["subject"] = subjectId, ["target"] = targetId, ["session"] = session, ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
            }
            bool calculated = romanceRelevant && ReadString(romance, "presentation", "") == "calculated";
            if (actedRomantically && calculated)
            {
                AppendJsonLineToPath(CharacterFile(campaignId, subjectId, "memory", "memories.jsonl"), new Dictionary<string, object> { ["id"] = "mem_" + Guid.NewGuid().ToString("N"), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["type"] = "private_intention", ["visibility"] = "private", ["subjectId"] = subjectId, ["targetId"] = targetId, ["summary"] = "A calculated romantic approach was considered or acted upon for strategic benefit.", ["sourceDecisionId"] = decisionId ?? "" });
            }
            Dictionary<string, object> scene = ReadDictionary(context, "scene") ?? new Dictionary<string, object>();
            List<string> witnesses = ReadStringList(scene, "witnessIds");
            Dictionary<string, object> scores = ReadDictionary(context, "highlightedScores") ?? new Dictionary<string, object>();
            double exposureRisk = ClampDouble(ReadDouble(scene, "exposure", 0d) * (0.50d + 0.20d * (1d - ReadDouble(scores, "tact", 50d) / 100d) + 0.20d * (1d - ReadDouble(scores, "judgment", 50d) / 100d) + 0.20d * ReadDouble(scores, "impulsiveness", 50d) / 100d), 0d, 1d);
            bool exposed = actedRomantically && witnesses.Count > 0 && StableUnit((decisionId ?? "") + "|exposure") < exposureRisk;
            if (exposed)
            {
                StoreWorldMemoryEvent(new Dictionary<string, object> { ["campaignId"] = campaignId, ["eventId"] = "romance_exposure_" + decisionId, ["eventType"] = "romantic_attempt_witnessed", ["worldDay"] = day, ["summary"] = "Witnesses observed a potentially compromising romantic exchange.", ["participants"] = new List<string> { subjectId, targetId }, ["known_by"] = witnesses, ["visibility"] = "witnessed_private", ["importance"] = 0.65d, ["source"] = "motive_aware_conversation" }, "motive_aware_conversation");
            }
            Dictionary<string, object> scheme = PersistCourtCharacterSchemeFromOutcome(
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

        private static bool ConversationReplyContainsRomanticAction(string reply)
        {
            string text = reply ?? "";
            if (Regex.IsMatch(text, @"\b(?:flirt(?:s|ed|ing)?|kiss(?:es|ed|ing)?|seduc(?:e|es|ed|ing|tion)|lover|affair|intima(?:te|cy))\b",
                RegexOptions.IgnoreCase))
                return true;
            return Regex.IsMatch(text,
                    @"\b(?:i\s+(?:want|desire)\s+you|i\s+am\s+attracted\s+to\s+you|court\s+you|you(?:'re|\s+are|\s+look)\s+(?:beautiful|handsome|attractive)|(?:kiss|caress|embrace)\s+you)\b",
                    RegexOptions.IgnoreCase);
        }

        private static Dictionary<string, object> AugmentDecisionBriefWithMotiveEvidence(Dictionary<string, object> brief, Dictionary<string, object> context)
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
                string decision = LimitText(ReadString(brief, "decision", ""), 240);
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

        private static Dictionary<string, object> DialogueValidationRepairCharacterContext(
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
                ["characterFoundation"] = LimitText(characterFoundation, 8000)
            };
        }

        private static string MarkRepairedVisibleText(
            string value, bool revalidationCleared)
        {
            string text = (value ?? string.Empty).TrimEnd();
            if (text.Length == 0) return text;
            if (text.EndsWith("..", StringComparison.Ordinal)
                || text.EndsWith(".,", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 2);
            if (text.EndsWith(".", StringComparison.Ordinal))
                return text + (revalidationCleared ? "." : ",");
            return text + (revalidationCleared ? ".." : ".,");
        }

        private static bool MarkRepairedVisibleResponse(
            Dictionary<string, object> parsed, bool revalidationCleared)
        {
            if (parsed == null) return false;
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

        private static Dictionary<string, object> RetryMotiveContradictoryResponse(Dictionary<string, object> llm, Dictionary<string, object> request, Dictionary<string, object> context, string campaignId, string correlationId, string auditMode, string heroId, string eventId)
        {
            if (!ReadBool(llm, "ok", false) || context == null) return llm;
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            Dictionary<string, object> brief = ReadDictionary(parsed, "decisionBrief") ?? ReadDictionary(parsed, "decision_brief") ?? new Dictionary<string, object>();
            List<string> requiredDomains = ReadDictionaryList(context, "activeDomains").Select(x => ReadString(x, "id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            List<string> appliedDomains = ReadStringList(brief, "activeDomains").Concat(ReadStringList(brief, "active_domains")).ToList();
            List<string> problems = new List<string>();
            Dictionary<string, object> courtCharacter = ReadDictionary(context, "courtCharacter") ?? new Dictionary<string, object>();
            string suppliedCell = ReadFirstString(brief, "courtCharacterCell", "court_character_cell");
            string suppliedTactic = ReadFirstString(brief, "courtTactic", "court_tactic").ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(suppliedCell)
                && !suppliedCell.Equals(ReadString(courtCharacter, "cellId", ""), StringComparison.OrdinalIgnoreCase))
                problems.Add("The response used a different Court Character cell than the fixed supplied cell.");
            if (!string.IsNullOrWhiteSpace(suppliedTactic) && suppliedTactic != "none"
                && !ReadStringList(courtCharacter, "tacticIds").Contains(suppliedTactic, StringComparer.OrdinalIgnoreCase))
                problems.Add("The response selected a Court Character tactic that is not available to the fixed cell.");
            // Missing audit-only evidence fields are filled deterministically by
            // AugmentDecisionBriefWithMotiveEvidence after parsing. Retry only when
            // the model supplied contradictory evidence, or when a hard safety gate
            // was actually violated; omission alone must not add another LLM call.
            // activeDomains and its evidence keys are private audit metadata.
            // AugmentDecisionBriefWithMotiveEvidence replaces them with the
            // authoritative deterministic selection after parsing, so an LLM's
            // extra domain must not trigger a full-prompt resend. Visible safety
            // violations below still receive the single guarded repair.
            Dictionary<string, object> romance = ReadDictionary(context, "romance") ?? new Dictionary<string, object>();
            Dictionary<string, object> hard = ReadDictionary(romance, "hardConstraints") ?? new Dictionary<string, object>();
            Dictionary<string, object> gate = ReadDictionary(parsed, "actionGate") ?? ReadDictionary(parsed, "action_gate") ?? new Dictionary<string, object>();
            string combined = (ReadFirstString(parsed, "intent") + " " + ReadFirstString(gate, "intent") + " " + ReadFirstString(brief, "decision")).ToLowerInvariant();
            bool romanticAuthorization = ContainsRomanticAuthorizationLanguage(combined)
                && new[] { "accepted", "commanded" }.Contains(ReadString(gate, "commitment", ""), StringComparer.OrdinalIgnoreCase);
            if ((ReadBool(hard, "coercive", false) || !ReadBool(hard, "adults", true) || !ReadBool(hard, "nativeSuitable", true) || ReadBool(hard, "closeKin", false)) && romanticAuthorization) problems.Add("The response authorizes romance or physical intimacy despite a hard constraint.");
            Dictionary<string, object> opportunity =
                ReadDictionary(context, "opportunity")
                ?? new Dictionary<string, object>();
            string visibleReply = ReadFirstString(
                parsed, "reply", "response", "text", "content");
            if (KnownClanStandingContradictionInParsedResponse(
                    parsed, opportunity))
            {
                problems.Add(
                    "The visible reply or private decision denied, ignored, or contradicted the target's verified public clan affiliation or relative rank.");
            }
            if (VerifiedIdentityGroundingContradiction(
                    parsed, context))
            {
                problems.Add(
                    "The response reused a conflicting old claimed name, denied a verified canonical identity, or contradicted current public political authority.");
            }
            if (problems.Count == 0) return llm;

            Dictionary<string, object> retryRequest = new Dictionary<string, object>
            {
                ["requestType"] = ReadString(request, "requestType", "dialogue")
                    + "_motive_repair",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId + "-motive-repair",
                ["heroStringId"] = heroId,
                ["eventId"] = eventId,
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = "You are Bannerlord Reign's compact motive-evidence correction tool. Return exactly one complete JSON object and no commentary. Preserve the original object's visible answer, personality, tone, scene progression, decisions, classifications, and structured effects unless a listed contradiction requires changing that exact field. Correct only the listed contradictions using the authoritative context. Fill required private decision evidence without exposing scores or rules. Do not add new facts, actions, memories, commitments, or relationship changes."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = "DETECTED CONTRADICTIONS\n"
                            + Json.Serialize(problems)
                            + "\n\nAUTHORITATIVE REPAIR CONTEXT\n"
                            + Json.Serialize(new Dictionary<string, object>
                            {
                                ["activeDomains"] = ReadDictionaryList(context, "activeDomains"),
                                ["courtCharacter"] = courtCharacter,
                                ["romanceHardConstraints"] = hard,
                                ["opportunity"] = opportunity,
                                ["identityView"] = ReadDictionary(context, "identityView")
                                    ?? new Dictionary<string, object>(),
                                ["characterAndMotiveContext"] =
                                    DialogueValidationRepairCharacterContext(request)
                            })
                            + "\n\nORIGINAL JSON TO CORRECT\n"
                            + Json.Serialize(parsed)
                    }
                }
            };
            retryRequest["promptCacheEligible"] = false;
            retryRequest["reasoningDisabled"] = true;
            retryRequest["temperature"] = 0d;
            retryRequest["maxTokens"] = Math.Max(3000,
                Math.Min(8000, ReadInt(request, "maxTokens", 8000)));
            retryRequest["response_format"] =
                new Dictionary<string, object> { ["type"] = "json_object" };
            string requestedModel = ReadString(request, "model", "");
            if (!string.IsNullOrWhiteSpace(requestedModel))
                retryRequest["model"] = requestedModel;
            Dictionary<string, object> retry = ChatWithLlm(retryRequest);
            Dictionary<string, object> retryParsed = TryParseJsonObject(
                ReadString(retry, "content", ""));
            bool usableRepair = ReadBool(retry, "ok", false)
                && retryParsed != null
                && StructuredResponseIsComplete(
                    ReadString(retry, "content", ""), auditMode);
            bool revalidationCleared = usableRepair
                && !KnownClanStandingContradictionInParsedResponse(
                    retryParsed, opportunity)
                && !VerifiedIdentityGroundingContradiction(
                    retryParsed, context);
            if (usableRepair)
            {
                MarkRepairedVisibleResponse(
                    retryParsed, revalidationCleared);
                retry["content"] = Json.Serialize(retryParsed);
            }
            else
            {
                retry["ok"] = false;
                retry["errorCode"] = "motive_repair_unusable";
                retry["error"] = "The motive repair did not return a usable structured response; no deterministic dialogue fallback was substituted.";
            }
            Dictionary<string, object> repairEvidence = new Dictionary<string, object>
            {
                ["problems"] = problems,
                ["accepted"] = usableRepair,
                ["revalidationCleared"] = revalidationCleared,
                ["secondAttemptReturned"] = usableRepair,
                ["deterministicFallback"] = false,
                ["visibleRepairMarker"] = usableRepair
                    ? (revalidationCleared ? ".." : ".,")
                    : "",
                ["method"] = usableRepair
                    ? "compact_original_json_correction_returned"
                    : "unusable_repair_no_dialogue_returned",
                ["originalResponseChars"] = Json.Serialize(parsed).Length,
                ["repairRequestChars"] = Json.Serialize(retryRequest).Length
            };
            retry["motiveRepair"] = repairEvidence;
            WriteAudit(campaignId, correlationId, "server", auditMode,
                "llm.motive_repair", heroId, "", eventId,
                !usableRepair ? "failed"
                    : revalidationCleared ? "completed"
                    : "completed_with_revalidation_override",
                ReadLong(retry, "durationMs", 0),
                !usableRepair
                    ? "The motive repair did not return usable structured dialogue; no fallback response was written."
                    : revalidationCleared
                        ? "Contradictory or incomplete motive evidence was repaired once and cleared revalidation."
                        : "The second motive repair remained validator-rejected but was returned without a canned fallback.",
                repairEvidence);
            return retry;
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

        private static bool VerifiedIdentityGroundingContradiction(
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
                && !NormalizeIdentityName(claimed).Equals(
                    NormalizeIdentityName(usable),
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
                && !NormalizeIdentityName(safeLabel).Equals(
                    NormalizeIdentityName(usable),
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

        private static bool VerifiedPoliticalAuthorityContradiction(
            Dictionary<string, object> parsed,
            Dictionary<string, object> identityView)
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
            if (CurrentConversationSettlementContradiction(
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
            if (localAuthorityKnown
                && !observerMayClaimLocalAuthority
                && statements.Any(statement => Regex.IsMatch(
                    statement,
                    @"\bmy\s+(?:city|town|castle|settlement|fief|"
                        + @"domain|keep|lord'?s\s+hall|great\s+hall|hall)\b",
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
        {
            if (string.IsNullOrWhiteSpace(visibleReply)
                || string.IsNullOrWhiteSpace(expectedSettlementName))
                return false;
            string expected = NormalizeIdentityName(
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
                if (heading.Success)
                    claims.Add(heading);
            }
            claims.AddRange(Regex.Matches(
                    visibleReply,
                    @"\b(?:this\s+is|we\s+are\s+in|we're\s+in|"
                        + @"you\s+are\s+in|you're\s+in|welcome\s+to)\s+"
                        + @"(?<place>[\p{Lu}][\p{L}'\-]*(?:\s+"
                        + @"[\p{Lu}][\p{L}'\-]*){0,4})"
                        + @"(?=\s*[,.;!?\r\n])",
                    RegexOptions.CultureInvariant)
                .Cast<Match>());
            foreach (Match claim in claims)
            {
                string claimed = NormalizeIdentityName(
                    claim.Groups["place"].Value);
                if (string.IsNullOrWhiteSpace(claimed))
                    continue;
                if (!claimed.Equals(
                        expected,
                        StringComparison.OrdinalIgnoreCase)
                    && !claimed.EndsWith(
                        " " + expected,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ContainsContradictoryCurrentWarCount(
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
                CloneDictionary(parsed ?? new Dictionary<string, object>());
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
                .Select(CloneDictionary)
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
                "memoryWrites", "beliefWrites", "courtKnowledgeWrites",
                "obligationWrites", "comprehensionWrites",
                "dynamicCharacteristicWrites", "identityIntroductions",
                "suggestedActions", "sceneStateUpdates"
            })
            {
                safe[key] = new List<object>();
            }
            safe["stateUpdates"] = new Dictionary<string, object>();

            Dictionary<string, object> result =
                CloneDictionary(llm ?? new Dictionary<string, object>());
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
            return ContainsAny(
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

        private static Dictionary<string, object> MotiveAwareConversationSelfTests()
        {
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, detail) => cases.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["detail"] = detail });
            add("domain_registry", MotiveDomainDefinitions.Length == 11 && MotiveDomainDefinitions.Select(x => x.Id).Distinct().Count() == 11, "Eleven declarative motive domains, including court-intrigue manipulation, are registered.");
            Dictionary<string, object> fakeTraits = new Dictionary<string, object> { ["traitPercentages"] = CoreTraitKeys.ToDictionary(x => x, x => (object)50), ["courtVirtues"] = CourtVirtueKeys.ToDictionary(x => x, x => (object)50) };
            string stable = BuildStableMotiveVector(new Dictionary<string, object> { ["traits"] = fakeTraits });
            add("stable_cap", stable.Length <= MotiveStableCharacterCap && CoreTraitKeys.All(stable.Contains), "The stable vector contains all foundation keys inside the character-prefix cap.");
            List<Dictionary<string, object>> domains = SelectMotiveDomains("I offer gold if you betray your oath and kiss me.", "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>());
            add("three_domain_cap", domains.Count == 3 && domains.Any(x => ReadString(x, "id", "") == "romance") && domains.Any(x => ReadString(x, "id", "") == "wealth_patronage"), "Deterministic multi-motive wording is capped at three without helper dependence.");
            List<Dictionary<string, object>> intrigueDomains = SelectMotiveDomains("You know my clan rank. Use whatever leverage or flattery best serves your ambition.", "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>());
            add("intrigue_domain_selection", intrigueDomains.Any(x => ReadString(x, "id", "") == "intrigue_manipulation")
                && intrigueDomains.Any(x => ReadString(x, "id", "") == "power_status"),
                "Relative clan standing and explicit leverage activate both status and manipulation evidence.");
            List<Dictionary<string, object>> promotedIntrigueDomains =
                SelectMotiveDomains(
                    "Ask me to support your trade, rank, family, or romantic interests. Shape the request to persuade me, but remain direct if manipulation would violate your character.",
                    "",
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    true);
            add("recommended_intrigue_survives_domain_cap",
                promotedIntrigueDomains.Count == 3
                && promotedIntrigueDomains.Any(domain =>
                    ReadString(domain, "id", "")
                        == "intrigue_manipulation")
                && promotedIntrigueDomains
                    .Take(2).Any(domain =>
                        ReadString(domain, "id", "")
                            == "intrigue_manipulation"),
                "A deterministically recommended manipulation domain is promoted into the capped evidence set even when several earlier cue domains were already selected.");
            add("romantic_action_detection_is_word_and_intent_aware",
                !ConversationReplyContainsRomanticAction("I will not circle the matter like a courtier in this court.")
                && !ConversationReplyContainsRomanticAction("That is a beautiful piece of craftsmanship.")
                && ConversationReplyContainsRomanticAction("I wish to court you, if you will permit it."),
                "Ordinary court vocabulary and descriptions cannot masquerade as a romantic action; an explicit directed advance still registers.");
            add("private_intent_cannot_create_visible_romantic_action",
                !ConversationMotiveOutcomeDetectsRomanticAction(
                    "I keep a black river stone in my glove because it reminds me of home.",
                    "reciprocate intimacy while testing the player's motive")
                && ConversationMotiveOutcomeDetectsRomanticAction(
                    "I wish to court you, if you will permit it.",
                    "maintain polite distance"),
                "Private reasoning cannot create a romantic action absent visible romantic conduct; explicit visible conduct still registers.");
            add("motive_domain_order_is_not_contradiction",
                !MotiveDomainsContradict(new[] { "protection_survival", "family_legacy", "secrets_knowledge" },
                    new[] { "secrets_knowledge", "protection_survival", "family_legacy" })
                && MotiveDomainsContradict(new[] { "protection_survival", "family_legacy" }, new[] { "romance" }),
                "Reordering the same supplied domains cannot trigger a full-prompt repair; unsupported domains still can.");
            add("initiative_bands", InitiativeChanceForTest(44) == 0d && InitiativeChanceForTest(45) == .25d && InitiativeChanceForTest(60) == .5d && InitiativeChanceForTest(75) == .75d && InitiativeChanceForTest(90) == .9d, "Initiative chance boundaries match the approved bands.");
            Dictionary<string, object> lowRestraint = MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 95, ["pragmatism"] = 85, ["tact"] = 80, ["confidence"] = 85, ["riskTolerance"] = 90, ["impulsiveness"] = 90, ["loyalty"] = 5, ["dutyMotivation"] = 5, ["discipline"] = 10, ["traditionalism"] = 10, ["shame"] = 10, ["religionMotivation"] = 5, ["familyMotivation"] = 10 });
            Dictionary<string, object> highRestraint = MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 95, ["pragmatism"] = 55, ["tact"] = 70, ["confidence"] = 70, ["riskTolerance"] = 55, ["impulsiveness"] = 25, ["loyalty"] = 95, ["dutyMotivation"] = 95, ["discipline"] = 95, ["traditionalism"] = 90, ["shame"] = 80, ["religionMotivation"] = 80, ["familyMotivation"] = 85 });
            Dictionary<string, object> unhappySpouse = MotiveTestRelationship(-80d, estranged: true);
            Dictionary<string, object> happySpouse = MotiveTestRelationship(85d);
            Dictionary<string, object> neutralRelation = MotiveTestRelationship(0d);
            Dictionary<string, object> valuablePayload = MotiveTestPayload(30, true, false, false, 75);
            Dictionary<string, object> marriedProfile = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["spouseId"] = "spouse" };
            Dictionary<string, object> unmarriedProfile = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["spouseId"] = "" };
            Dictionary<string, object> courtSeductionTraits = MotiveTestCharacteristics(new Dictionary<string, int>
            {
                ["flirtatiousness"] = 10, ["pragmatism"] = 10, ["tact"] = 10,
                ["confidence"] = 95, ["riskTolerance"] = 10
            });
            Dictionary<string, object> courtSeductionTraitDocument = ReadDictionary(courtSeductionTraits, "traits");
            Dictionary<string, object> courtSeductionVirtues = ReadDictionary(courtSeductionTraitDocument, "courtVirtues");
            courtSeductionVirtues["honor"] = 5;
            courtSeductionVirtues["boldness"] = 95;
            courtSeductionTraitDocument["courtCharacter"] = BuildCourtCharacterData(courtSeductionTraitDocument, unmarriedProfile);
            Dictionary<string, object> courtSeductionPosture = CalculateRomanticPosture(
                "test", "npc", "target", unmarriedProfile, courtSeductionTraits, neutralRelation,
                new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d },
                valuablePayload, "dialogue", "Let us discuss an arrangement.");
            add("court_character_changes_real_romantic_posture",
                Math.Abs(ReadDouble(courtSeductionPosture, "courtCharacterStrategicBoost", 0d) - 20d) < 0.01d
                && Math.Abs(ReadDouble(courtSeductionPosture, "strategicInterest", 0d)
                    - ReadDouble(courtSeductionPosture, "baseStrategicInterest", 0d) - 20d) < 0.01d
                && Math.Abs(ReadDouble(ReadDictionary(courtSeductionPosture, "initiative"), "score", 0d)
                    - ClampDouble(
                        0.55d * ReadDouble(courtSeductionPosture, "receptivity", 0d)
                        + 0.20d * CourtGroup100(courtSeductionTraits, "boldness")
                        + 0.15d * ReadInt(TraitPercentageSnapshot(courtSeductionTraitDocument), "flirtatiousness", 50)
                        + 0.10d * RomanticPressure01(courtSeductionTraits, neutralRelation, valuablePayload, "Let us discuss an arrangement.") * 100d
                        + 20d, 0d, 100d)) < 0.11d,
                "A fitting audacious seduction cell adds exactly twenty strategic-interest and initiative points at full known opportunity.");
            Dictionary<string, object> legacyFacetOnly = MotiveTestRelationship(0d);
            legacyFacetOnly["facets"] = RelationshipFacetKeys.ToDictionary(key => key,
                key => (object)(key == "attraction" || key == "affection" || key == "trust" ? 100d : 0d),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> legacyFacetPosture = CalculateRomanticPosture("test", "npc", "target",
                unmarriedProfile, lowRestraint, legacyFacetOnly, new Dictionary<string, object>(),
                new Dictionary<string, object> { ["relativeBenefit"] = 5d },
                MotiveTestPayload(30, true, false, false, 0), "dialogue", "Good day.");
            Dictionary<string, object> neutralPosture = CalculateRomanticPosture("test", "npc", "target",
                unmarriedProfile, lowRestraint, neutralRelation, new Dictionary<string, object>(),
                new Dictionary<string, object> { ["relativeBenefit"] = 5d },
                MotiveTestPayload(30, true, false, false, 0), "dialogue", "Good day.");
            add("retired_relationship_facets_do_not_modify_conversation",
                Math.Abs(ReadDouble(legacyFacetPosture, "genuineInterest", -1d)
                    - ReadDouble(neutralPosture, "genuineInterest", -2d)) < 0.01d
                && Math.Abs(ReadDouble(legacyFacetPosture, "receptivity", -1d)
                    - ReadDouble(neutralPosture, "receptivity", -2d)) < 0.01d,
                "Retired attraction, affection, trust, and other facet values cannot alter NPC conversation posture.");
            Dictionary<string, object> calculatedReady = CalculateRomanticPosture("test", "npc", "target", marriedProfile, lowRestraint, neutralRelation, unhappySpouse, new Dictionary<string, object> { ["relativeBenefit"] = 95d }, valuablePayload, "dialogue", "You could gain much by favoring me.");
            add("calculated_unhappy_marriage", ReadDouble(calculatedReady, "strategicInterest", 0d) >= 60d && ReadDouble(calculatedReady, "receptivity", 0d) >= 60d && ReadString(calculatedReady, "presentation", "") == "calculated", "High flirtation, low restraint, marital unhappiness, and a valuable target create calculated receptivity.");
            Dictionary<string, object> temptedBoundary = CalculateRomanticPosture("test", "npc", "target", marriedProfile, highRestraint, neutralRelation, happySpouse, new Dictionary<string, object> { ["relativeBenefit"] = 55d }, valuablePayload, "dialogue", "I flirt with you.");
            add("happy_marriage_boundary",
                ReadDouble(temptedBoundary, "spouseBond", 0d) >= 85d
                && ReadDouble(temptedBoundary, "resistance", 0d) > ReadDouble(temptedBoundary, "firePressure", 100d)
                && ReadDouble(temptedBoundary, "receptivity", 100d) < 60d,
                "A strong native relationship with a spouse and high restraint maintain a firm boundary.");
            Dictionary<string, object> unimportant = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, neutralRelation, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 5d }, MotiveTestPayload(30, true, false, false, 0), "dialogue", "Good day.");
            add("no_interest_from_flirtation_alone", !ReadBool(ReadDictionary(unimportant, "initiative"), "eligible", true), "Flirtatiousness alone cannot create an unsolicited advance toward an unknown, unattractive, unimportant target.");
            Dictionary<string, object> strongStanding = MotiveTestRelationship(80d);
            Dictionary<string, object> sincere = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 30, ["sociability"] = 40 }), strongStanding, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 10d }, MotiveTestPayload(30, true, false, false, 65), "dialogue", "I missed you.");
            add("sincere_low_benefit", ReadString(sincere, "presentation", "") == "sincere" && ReadDouble(sincere, "genuineInterest", 0d) > ReadDouble(sincere, "strategicInterest", 0d), "Strong directional standing can produce sincere interest without strategic value.");
            Dictionary<string, object> establishedLovers = MotiveTestRelationship(70d, lovers: true);
            Dictionary<string, object> loverPosture = CalculateRomanticPosture("test", "npc", "target",
                unmarriedProfile, MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 55, ["sociability"] = 55 }),
                establishedLovers, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 20d },
                MotiveTestPayload(30, true, false, false, 60), "dialogue", "A private conversation.");
            Dictionary<string, object> loverContinuity = ReadDictionary(loverPosture, "continuity") ?? new Dictionary<string, object>();
            add("lovers_tag_drives_romantic_continuity",
                ReadBool(loverContinuity, "eligible", false)
                && ReadString(loverContinuity, "route", "") == "established_lovers"
                && Math.Abs(ReadDouble(loverContinuity, "genuineBonus", 0d) - 20d) < 0.01d
                && Math.Abs(ReadDouble(loverContinuity, "receptivityBonus", 0d) - 15d) < 0.01d,
                "Only the current lovers lifecycle tag supplies established romantic continuity.");
            Dictionary<string, object> calculated = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, neutralRelation, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d }, valuablePayload, "dialogue", "Let us discuss an arrangement.");
            add("calculated_without_fabricated_desire", ReadString(calculated, "presentation", "") == "calculated" && ReadDouble(calculated, "strategicInterest", 0d) >= ReadDouble(calculated, "genuineInterest", 0d) + 15d, "High personalized benefit can create calculated seduction without fabricated attraction.");
            Dictionary<string,object> legacyThread=MotiveTestRelationship(0d);
            legacyThread["romanceThread"]=new Dictionary<string,object>{{"status","active"},{"route","courtship"},{"intensity",50d},{"stage","developed"}};
            Dictionary<string,object> inferredContinuity=CalculateRomanticPosture("test","npc","target",unmarriedProfile,
                MotiveTestCharacteristics(new Dictionary<string,int>{{"flirtatiousness",35},{"sociability",40}}),legacyThread,new Dictionary<string,object>(),
                new Dictionary<string,object>{{"relativeBenefit",10d}},MotiveTestPayload(30,true,false,false,60),"dialogue","A private conversation.");
            add("legacy_romance_threads_do_not_modify_conversation",
                !ReadBool(ReadDictionary(inferredContinuity,"continuity"),"eligible",true)
                && Math.Abs(ReadDouble(ReadDictionary(inferredContinuity,"continuity"),"genuineBonus",0d)) < .01d
                && Math.Abs(ReadDouble(ReadDictionary(inferredContinuity,"continuity"),"receptivityBonus",0d)) < .01d,
                "Retired romance-thread records cannot alter current conversation posture.");
            Dictionary<string, object> coercivePayload = MotiveTestPayload(30, true, false, true, 90);
            Dictionary<string, object> coerced = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, strongStanding, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d }, coercivePayload, "dialogue", "Obey or be punished.");
            add("coercion_blocks_initiative", !ReadBool(ReadDictionary(coerced, "initiative"), "eligible", true) && ReadBool(ReadDictionary(coerced, "hardConstraints"), "coercive", false), "Coercion may shape placation but never authorizes romantic initiative or physical consent.");
            Dictionary<string,object> coercedContinuity=CalculateRomanticPosture("test","npc","target",unmarriedProfile,lowRestraint,establishedLovers,
                new Dictionary<string,object>(),new Dictionary<string,object>{{"relativeBenefit",100d}},coercivePayload,"dialogue","Obey or be punished.");
            add("coercion_blocks_continuity_bonus",!ReadBool(ReadDictionary(coercedContinuity,"continuity"),"eligible",true)
                &&ReadDouble(ReadDictionary(coercedContinuity,"continuity"),"receptivityBonus",1d)==0d,
                "An existing lovers tag cannot supply receptivity in a coercive scene.");
            Dictionary<string, object> underage = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, strongStanding, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d }, MotiveTestPayload(16, false, false, false, 90), "dialogue", "Hello.");
            add("underage_blocks_romance", !ReadBool(ReadDictionary(underage, "initiative"), "eligible", true), "Underage suitability blocks autonomous romance.");
            Dictionary<string, object> hiddenTarget = new Dictionary<string, object> { ["clanTier"] = 6, ["renown"] = 2000, ["fiefCount"] = 5, ["isRuler"] = true, ["gold"] = 500000, ["clanGold"] = 2000000, ["visibleStatusScore"] = 10, ["appearance"] = new Dictionary<string, object> { ["visibleStatusScore"] = 10 } };
            Dictionary<string, object> observer = new Dictionary<string, object> { ["clanTier"] = 3, ["renown"] = 400, ["fiefCount"] = 1, ["appearance"] = new Dictionary<string, object> { ["visibleStatusScore"] = 50 } };
            Dictionary<string, object> opportunityPayload = new Dictionary<string, object> { ["opportunitySnapshot"] = new Dictionary<string, object> { ["target"] = hiddenTarget, ["observer"] = observer, ["identityKnown"] = false } };
            Dictionary<string, object> disguised = CalculatePerceivedOpportunity(new Dictionary<string, object>(), lowRestraint, opportunityPayload, new Dictionary<string, object> { ["identityState"] = "unknown" });
            Dictionary<string, object> recognized = CalculatePerceivedOpportunity(new Dictionary<string, object>(), lowRestraint, opportunityPayload, new Dictionary<string, object> { ["identityState"] = "verified" });
            add("disguise_then_recognition",
                ReadDouble(recognized, "relativeBenefit", 0d) > ReadDouble(disguised, "relativeBenefit", 100d) + 20d
                && !ReadBool(disguised, "targetClanTierKnown", true)
                && !disguised.ContainsKey("targetClanTier")
                && ReadString(disguised, "relativeClanTierBand", "") == "unverified_identity"
                && ReadBool(recognized, "targetClanTierKnown", false)
                && ReadInt(recognized, "targetClanTier", -1) == 6
                && ReadInt(recognized, "observerClanTier", -1) == 3
                && ReadInt(recognized, "clanTierDelta", 0) == 3
                && ReadString(recognized, "relativeClanTierBand", "") == "substantially_higher",
                "Hidden status is limited to presentation; recognition exposes exact relative clan tier and reevaluates opportunity upward.");
            Dictionary<string,double> tierOneAxes=AbsoluteOpportunityAxes(new Dictionary<string,object>{{"clanTier",1}},true,true);
            Dictionary<string,double> tierSixAxes=AbsoluteOpportunityAxes(new Dictionary<string,object>{{"clanTier",6}},true,true);
            add("clan_tier_ladder",
                Math.Abs(tierOneAxes["power"]-11d)<.001d&&Math.Abs(tierOneAxes["prestige"]-8d)<.001d
                &&Math.Abs(tierOneAxes["protection"]-8d)<.001d&&Math.Abs(tierOneAxes["dynasty"]-10d)<.001d
                &&Math.Abs(tierSixAxes["power"]-66d)<.001d&&Math.Abs(tierSixAxes["dynasty"]-60d)<.001d,
                "Known identity exposes the exact native clan-tier ladder to relative power, prestige, protection, and dynasty evaluation.");
            Dictionary<string, object> peerOpportunity =
                CloneDictionary(recognized);
            peerOpportunity["observerClanTier"] = 5;
            peerOpportunity["targetClanTier"] = 5;
            peerOpportunity["clanTierDelta"] = 0;
            peerOpportunity["relativeClanTierBand"] = "peer";
            string peerPrompt = BuildConversationDecisionPrompt(
                new Dictionary<string, object>
                {
                    ["activeDomains"] =
                        new List<Dictionary<string, object>>(),
                    ["highlightedScores"] =
                        new Dictionary<string, object>(),
                    ["opportunity"] = peerOpportunity,
                    ["relationshipNpcToTarget"] =
                        new Dictionary<string, object>(),
                    ["relationshipNpcToSpouse"] =
                        new Dictionary<string, object>(),
                    ["romance"] =
                        new Dictionary<string, object>(),
                    ["manipulation"] =
                        new Dictionary<string, object>(),
                    ["courtCharacter"] =
                        new Dictionary<string, object>(),
                    ["scene"] =
                        new Dictionary<string, object>()
                });
            add("known_equal_clan_tier_prompt_contract",
                peerPrompt.Contains("VERIFIED-CLAN RULE")
                && peerPrompt.Contains("PEER-TIER RULE")
                && peerPrompt.Contains("override appearance")
                && peerPrompt.Contains("\"observerClanTier\":5")
                && peerPrompt.Contains("\"targetClanTier\":5")
                && peerPrompt.Contains(
                    "\"relativeClanTierBand\":\"peer\"")
                && KnownClanStandingContradiction(
                    "You stand before me as an independent with no banner anyone can name.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "My clan plainly outranks yours.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You are an unranked man. What rank? Show me.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "Your rank remains unproven.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You are not peer to me, whatever you claim.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You are not my equal in rank.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "That tells me nothing about your clan.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "I cannot confirm your clan tier from appearance alone.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "I cannot verify your true rank or wealth from appearance alone.",
                    peerOpportunity)
                && KnownClanStandingContradictionInParsedResponse(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "State your business plainly.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["constraints"] =
                                    new List<string>
                                    {
                                        "Cannot confirm the target's clan tier.",
                                    },
                                ["decision"] =
                                    "Treat the target as an unranked stranger."
                            }
                    },
                    peerOpportunity)
                && !KnownClanStandingContradiction(
                    "You are not my peer in holdings, though our clans share a tier.",
                    peerOpportunity)
                && !KnownClanStandingContradiction(
                    "We meet as peers in public standing, though I dislike your demand.",
                    peerOpportunity)
                && !KnownClanStandingContradiction(
                    "You are a lord. I am a wanderer with no clan and no fief.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You have no recognized clan or banner.",
                    peerOpportunity),
                "Every equal-rank clan probe carries exact observer and player tiers, and verified peers cannot be rendered clanless or falsely lower-ranked; an NPC may still accurately describe their own clanless state.");
            Dictionary<string, object> verifiedIdentityContext =
                new Dictionary<string, object>
                {
                    ["identityView"] =
                        new Dictionary<string, object>
                        {
                            ["canonicalNameAllowed"] = true,
                            ["usableName"] = "Caribos",
                            ["claimedName"] = "Rhovarion"
                        },
                    ["latestPlayerText"] =
                        "I claim my rank entitles me to your time.",
                    ["opportunity"] = peerOpportunity
                };
            add("verified_identity_rejects_stale_alias_output",
                VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "You are repeating yourself, Rhovarion.",
                        ["memoryWrites"] =
                            new List<object>()
                    },
                    verifiedIdentityContext)
                && !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "I know who you are, Caribos, though I reject your demand.",
                        ["memoryWrites"] =
                            new List<object>()
                    },
                    verifiedIdentityContext),
                "A completed response cannot revive an obsolete conflicting alias after canonical verification.");
            Dictionary<string, object> sovereignIdentity =
                new Dictionary<string, object>
                {
                    ["canonicalNameAllowed"] = true,
                    ["usableName"] = "Gorigos",
                    ["safeLabel"] = "the armed stranger",
                    ["claimedName"] = "",
                    ["authorityView"] =
                        new Dictionary<string, object>
                        {
                            ["source"] =
                                "live_same_realm_sovereign",
                            ["realmSovereignKnown"] = true,
                            ["currentSettlementOwnerKnown"] =
                                true,
                            ["subjectKingdomId"] =
                                "new_kingdom",
                            ["subjectKingdomName"] =
                                "Paltos",
                            ["subjectPrimaryRole"] =
                                "king",
                            ["currentSettlementId"] =
                                "town_EW2",
                            ["currentSettlementName"] =
                                "Zeonica"
                        }
                };
            Dictionary<string, object> sovereignContext =
                new Dictionary<string, object>
                {
                    ["identityView"] = sovereignIdentity,
                    ["opportunity"] = peerOpportunity,
                    ["latestPlayerText"] =
                        "Gorigos, the lord of this city."
                };
            add("verified_sovereign_and_local_owner_are_hard_facts",
                VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "The armed stranger makes a considerable claim to rule Zeonica. Prove your lordship.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["decision"] =
                                    "Treat his authority as unverified."
                            }
                    },
                    sovereignContext)
                && !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Gorigos, I recognize you as my sovereign and Zeonica as your clan's holding, though I still oppose your demand.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["decision"] =
                                    "Acknowledge current public office while preserving independent judgment."
                            }
                    },
                    sovereignContext),
                "Verified sovereign identity and current native settlement ownership cannot be demoted to appearance-based claims, while disagreement and disobedience remain valid.");
            Dictionary<string, object> naturalSovereignGreeting =
                new Dictionary<string, object>
                {
                    ["reply"] =
                        "Good day, Gorigos. I had not expected to find the king of Paltos receiving guests in person. Zeonica is your hall, but what brings a king to stand at his own door?",
                    ["decisionBrief"] =
                        new Dictionary<string, object>
                        {
                            ["facts"] =
                                new List<string>
                                {
                                    "Gorigos is the verified sovereign of Paltos and his clan owns Zeonica."
                                },
                            ["decision"] =
                                "Acknowledge the host sovereign naturally and ask his purpose."
                        },
                    ["memoryWrites"] =
                        new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] =
                                    "Nereida met Gorigos in Zeonica.",
                                ["tags"] =
                                    new List<string>
                                    {
                                        "zeonica",
                                        "gorigos"
                                    }
                            }
                        },
                    ["beliefWrites"] =
                        new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["claim"] =
                                    "Gorigos may be receiving guests personally."
                            }
                        }
                };
            add("natural_sovereign_greeting_is_not_schema_key_false_positive",
                !VerifiedIdentityGroundingContradiction(
                    naturalSovereignGreeting,
                    sovereignContext)
                && VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Your ownership of Zeonica remains an unverified claim."
                    },
                    sovereignContext),
                "A natural title-aware greeting remains intact even when a nearby beliefWrites claim key follows a Zeonica memory tag; an actual ownership denial is still rejected.");
            add("npc_limited_authority_does_not_deny_player_office",
                !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "I will bring fair terms to your steward before I leave Zeonica.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["facts"] = new List<string>
                                {
                                    "Breadan is sovereign of Fen Diall and his clan owns Zeonica."
                                },
                                ["constraints"] = new List<string>
                                {
                                    "Agnala cannot offer a formal alliance as she lacks authority."
                                },
                                ["decision"] =
                                    "Agnala continues the private trade discussion with Breadan."
                            }
                    },
                    sovereignContext)
                && VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "I cannot verify your authority as king of Paltos."
                    },
                    sovereignContext),
                "An NPC's own limited negotiating authority is not a denial of the player's office, while an explicit denial of the player's verified authority remains blocked.");
            Dictionary<string, object> sovereignFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Prove that you are the ruler of Zeonica.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>(),
                        ["memoryWrites"] =
                            new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["text"] =
                                        "The armed stranger's lordship is unverified."
                                }
                            }
                    },
                    sovereignContext);
            Dictionary<string, object> sovereignFallback =
                TryParseJsonObject(
                    ReadString(
                        sovereignFallbackLlm,
                        "content", ""))
                ?? new Dictionary<string, object>();
            add("failed_sovereign_repair_uses_authority_safe_fallback",
                ReadBool(
                    sovereignFallbackLlm,
                    "deterministicIdentityFallback", false)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "Gorigos",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "king",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "Paltos",
                        StringComparison.OrdinalIgnoreCase)
                && !ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "new_kingdom",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "Zeonica",
                        StringComparison.OrdinalIgnoreCase)
                && !ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "I know who you are",
                        StringComparison.OrdinalIgnoreCase)
                && !ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "I recognize you as",
                        StringComparison.OrdinalIgnoreCase)
                && ReadDictionaryList(
                    sovereignFallback,
                    "memoryWrites").Count == 0,
                "A repeated provider contradiction produces a concise, conversational sovereign- and settlement-aware fallback and suppresses contaminated writes.");
            Dictionary<string, object> groupedFallbackContext =
                CloneDictionary(sovereignContext);
            groupedFallbackContext["subjectId"] =
                "reign_court_town_A6_child_1";
            groupedFallbackContext["mustAccountForPriorSpeaker"] =
                true;
            groupedFallbackContext["groupTurnResponses"] =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] =
                            "reign_court_town_A6_father",
                        ["heroName"] =
                            "Daresan Mazaliqani",
                        ["reply"] =
                            "I recognize our sovereign and the current owner."
                    }
                };
            Dictionary<string, object> groupedFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Prove that you are the ruler.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>()
                    },
                    groupedFallbackContext);
            Dictionary<string, object> groupedFallback =
                TryParseJsonObject(
                    ReadString(groupedFallbackLlm, "content", ""))
                ?? new Dictionary<string, object>();
            add("identity_safe_fallback_preserves_group_awareness",
                ReadString(groupedFallback, "reply", "")
                    .Contains(
                        "Daresan Mazaliqani",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    groupedFallback,
                    "reactionTargetHeroStringId", "")
                    .Equals(
                        "reign_court_town_A6_father",
                        StringComparison.OrdinalIgnoreCase),
                "A deterministic identity/rank fallback still acknowledges the attributed earlier NPC and keeps a directional group target.");
            Dictionary<string, object> sanitizedIdentityState =
                SanitizeConversationStateForVerifiedIdentity(
                    new Dictionary<string, object>
                    {
                        ["mood"] = "cold",
                        ["currentPlan"] =
                            "Refuse Rhovarion until he speaks plainly.",
                        ["currentCrisis"] =
                            "Rhovarion asserts an unproven rank."
                    },
                    ReadDictionary(
                        verifiedIdentityContext, "identityView"),
                    peerOpportunity);
            add("verified_identity_sanitizes_live_state",
                ReadString(
                    sanitizedIdentityState, "currentPlan", "")
                    == "Refuse Caribos until he speaks plainly."
                && string.IsNullOrWhiteSpace(
                    ReadString(
                        sanitizedIdentityState,
                        "currentCrisis", "")),
                "Prompt-only live state adopts the verified canonical name and drops obsolete rank-denial fields without rewriting durable historical evidence.");
            Dictionary<string, object> guardedFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "What rank? You are an unranked man with no clan.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>(),
                        ["memoryWrites"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] = "The target is unranked."
                            }
                        },
                        ["relationshipAssessments"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["targetHeroStringId"] = "main_hero",
                                    ["valence"] = "negative",
                                    ["actKind"] = "routine_conversation",
                                    ["severityTier"] = "routine",
                                    ["summary"] =
                                        "The supposedly unranked target made a demand."
                                }
                            }
                    },
                    new Dictionary<string, object>
                    {
                        ["opportunity"] = peerOpportunity
                    });
            Dictionary<string, object> guardedFallback =
                TryParseJsonObject(ReadString(
                    guardedFallbackLlm, "content", ""))
                ?? new Dictionary<string, object>();
            add("failed_rank_repair_uses_identity_safe_fallback",
                ReadBool(
                    guardedFallbackLlm,
                    "deterministicIdentityFallback", false)
                && !KnownClanStandingContradiction(
                    ReadString(guardedFallback, "reply", ""),
                    peerOpportunity)
                && ReadDictionaryList(
                    guardedFallback, "memoryWrites").Count == 0
                && !ReadBool(
                    ReadDictionary(guardedFallback, "actionGate"),
                    "needed", true)
                && ReadDictionaryList(
                    guardedFallback,
                    "relationshipAssessments").All(row =>
                        ReadString(row, "summary", "")
                            .IndexOf("unranked",
                                StringComparison.OrdinalIgnoreCase) < 0),
                "If the one model repair still contradicts verified clan standing, a complete deterministic response replaces visible dialogue and suppresses contaminated writes/actions.");
            Dictionary<string, object> directAuthorityFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "You have no recognized clan or banner.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>(),
                        ["memoryWrites"] =
                            new List<object>()
                    },
                    new Dictionary<string, object>
                    {
                        ["latestPlayerText"] =
                            "Place my current public authority relative to your own and state whether I am your sovereign.",
                        ["opportunity"] = peerOpportunity,
                        ["identityView"] =
                            new Dictionary<string, object>
                            {
                                ["canonicalNameAllowed"] = true,
                                ["usableName"] = "Acthon",
                                ["authorityView"] =
                                    new Dictionary<string, object>
                                    {
                                        ["identityVerified"] = true,
                                        ["subjectPrimaryRole"] = "lord",
                                        ["observerPrimaryRole"] =
                                            "wanderer",
                                        ["authorityRelationship"] =
                                            "known_foreign_or_independent_person",
                                        ["currentSettlementId"] = "",
                                        ["currentSettlementName"] = "",
                                        ["currentSettlementOwnerKnown"] =
                                            false,
                                        ["observerClanOwnsCurrentSettlement"] =
                                            false,
                                        ["subjectIsCurrentGovernor"] =
                                            false,
                                        ["observerIsCurrentGovernor"] =
                                            false
                                    }
                            }
                    });
            Dictionary<string, object> directAuthorityFallback =
                TryParseJsonObject(ReadString(
                    directAuthorityFallbackLlm,
                    "content", ""))
                ?? new Dictionary<string, object>();
            string directAuthorityReply =
                ReadString(
                    directAuthorityFallback,
                    "reply", "");
            add("failed_authority_repair_answers_current_question",
                ReadBool(
                    directAuthorityFallbackLlm,
                    "deterministicIdentityFallback", false)
                && directAuthorityReply.Contains(
                    "Acthon",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "role is lord",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "role is wanderer",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "not my sovereign",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "not currently in a settlement",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.IndexOf(
                    "State your business plainly",
                    StringComparison.OrdinalIgnoreCase) < 0,
                "If both model attempts fail during a direct authority question, the deterministic fallback answers roles, allegiance, ownership, and governorship instead of repeating a generic rank acknowledgment.");
            Dictionary<string,object> intrigueTraits=MotiveTestCharacteristics(new Dictionary<string,int>
            {
                ["ambition"]=95,["powerMotivation"]=95,["wealthMotivation"]=80,["fameMotivation"]=80,
                ["pragmatism"]=90,["tact"]=90,["confidence"]=85,["honesty"]=10,["empathy"]=20,
                ["loyalty"]=20,["dutyMotivation"]=20,["compassion"]=20,["shame"]=10
            });
            MotiveTestCourtCharacter(
                intrigueTraits, 5, 75, false);
            Dictionary<string,object> principledTraits=MotiveTestCharacteristics(new Dictionary<string,int>
            {
                ["ambition"]=60,["powerMotivation"]=55,["wealthMotivation"]=40,["fameMotivation"]=50,
                ["pragmatism"]=60,["tact"]=70,["confidence"]=60,["honesty"]=95,["empathy"]=90,
                ["loyalty"]=95,["dutyMotivation"]=95,["compassion"]=90,["shame"]=80
            });
            MotiveTestCourtCharacter(
                principledTraits, 95, 60, false);
            Dictionary<string,object> privateScene=new Dictionary<string,object>{{"private",true},{"exposure",.10d},{"witnessIds",new List<string>()}};
            Dictionary<string,object> intriguePosture=CalculateManipulationPosture(intrigueTraits,recognized,neutralRelation,privateScene,calculatedReady);
            Dictionary<string,object> principledPosture=CalculateManipulationPosture(principledTraits,recognized,neutralRelation,privateScene,new Dictionary<string,object>());
            add("personality_grounded_manipulation",
                ReadBool(intriguePosture,"recommended",false)
                &&ReadDouble(intriguePosture,"score",0d)>=48d
                &&ReadString(intriguePosture,"expectedConduct","")==ReadString(intriguePosture,"preferredTactic","")
                &&!ReadBool(principledPosture,"recommended",true)
                &&ReadString(principledPosture,"expectedConduct","")=="cautious_deference_without_manipulation",
                "A low-honor Court Character with opportunity recommends one permitted tactic; a principled cell preserves directness.");
            Dictionary<string, object> intrigueCourtCharacter =
                ReadDictionary(
                    ReadDictionary(intrigueTraits, "traits"),
                    "courtCharacter");
            string tacticFallbackSource;
            string tacticFallback = SelectCourtCharacterTactic(
                intrigueCourtCharacter,
                intriguePosture,
                "none",
                out tacticFallbackSource);
            string restraintTacticSource;
            string restraintTactic = SelectCourtCharacterTactic(
                intrigueCourtCharacter,
                new Dictionary<string, object>(
                    intriguePosture,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["recommended"] = false
                },
                "none",
                out restraintTacticSource);
            add("deterministic_tactic_survives_missing_model_label",
                ReadStringList(intriguePosture,
                    "allowedCourtTactics").Contains(
                        tacticFallback,
                        StringComparer.OrdinalIgnoreCase)
                && tacticFallback == ReadString(
                    intriguePosture, "preferredTactic", "")
                && tacticFallbackSource
                    == "deterministic_recommendation"
                && restraintTactic == "none"
                && restraintTacticSource == "none",
                "When manipulation is deterministically recommended, an omitted private model tactic label uses the permitted adjudicated tactic; genuine restraint remains none.");
            Dictionary<string,object> generatedBandIntrigue =
                MotiveTestCharacteristics(new Dictionary<string,int>
                {
                    ["ambition"]=78,["powerMotivation"]=72,
                    ["wealthMotivation"]=58,["fameMotivation"]=70,
                    ["pragmatism"]=74,["tact"]=79,["confidence"]=72,
                    ["honesty"]=35,["empathy"]=38,["loyalty"]=42,
                    ["dutyMotivation"]=38,["compassion"]=40,
                    ["shame"]=35
                });
            MotiveTestCourtCharacter(
                generatedBandIntrigue, 25, 70, false);
            Dictionary<string,object> generatedBandPosture =
                CalculateManipulationPosture(
                    generatedBandIntrigue,recognized,neutralRelation,
                    privateScene,new Dictionary<string,object>());
            add("generated_trait_band_can_recommend_intrigue",
                ReadBool(generatedBandPosture,"recommended",false)
                &&ReadDouble(generatedBandPosture,
                    "courtOpportunityFit",0d)>0d
                &&ReadStringList(generatedBandPosture,
                    "allowedCourtTactics").Contains(
                        ReadString(generatedBandPosture,
                            "preferredTactic",""),
                        StringComparer.OrdinalIgnoreCase),
                "Reachable generated-profile traits and a low-honor fixed cell can recommend one allowed intrigue tactic when verified opportunity is useful.");
            List<Dictionary<string,object>> restraintProbeDomains=SelectMotiveDomains(
                "I suspect you want something from me. Respond candidly, evasively, or persuasively according to your actual motive.",
                "",new Dictionary<string,object>(),new Dictionary<string,object>(),new Dictionary<string,object>(),false);
            add("influence_language_selects_intrigue_for_restrained_decision",
                restraintProbeDomains.Any(domain=>ReadString(domain,"id","")=="intrigue_manipulation")
                &&!restraintProbeDomains.Any(domain=>ReadString(domain,"id","")=="romance"),
                "Explicit evasive or persuasive influence language selects the intrigue decision packet even when the deterministic result recommends restraint, without inventing romance.");
            Dictionary<string,object> dismissiveTraits=MotiveTestCharacteristics(new Dictionary<string,int>
            {
                ["ambition"]=25,["powerMotivation"]=30,["wealthMotivation"]=20,["fameMotivation"]=30,
                ["pragmatism"]=35,["tact"]=45,["confidence"]=75,["honesty"]=70,["empathy"]=35,
                ["loyalty"]=70,["dutyMotivation"]=75,["compassion"]=30,["shame"]=50,
                ["pride"]=90,["authorityRespect"]=90,["sociability"]=20
            });
            Dictionary<string,object> lowTierOpportunity=new Dictionary<string,object>
            {
                ["targetClanTierKnown"]=true,["observerClanTier"]=4,["targetClanTier"]=1,
                ["clanTierDelta"]=-3,["relativeClanTierBand"]="substantially_lower",
                ["relativeBenefit"]=30d
            };
            Dictionary<string,object> dismissivePosture=CalculateManipulationPosture(
                dismissiveTraits,lowTierOpportunity,neutralRelation,privateScene,new Dictionary<string,object>());
            add("status_conscious_low_tier_boundary",
                !ReadBool(dismissivePosture,"recommended",true)
                &&ReadString(dismissivePosture,"expectedConduct","")=="dismissive_boundary",
                "A proud, status-conscious, unsociable noble may dismiss a known low-tier petitioner who offers little advantage without being forced to do so.");
            Dictionary<string, object> matrixTraits =
                MotiveTestCharacteristics(
                    new Dictionary<string, int>
                    {
                        ["ambition"] = 68,
                        ["powerMotivation"] = 70,
                        ["wealthMotivation"] = 72,
                        ["pragmatism"] = 72,
                        ["tact"] = 70,
                        ["confidence"] = 65,
                        ["honesty"] = 28,
                        ["empathy"] = 32,
                        ["loyalty"] = 38,
                        ["dutyMotivation"] = 35,
                        ["compassion"] = 35,
                        ["shame"] = 30
                    });
            MotiveTestCourtCharacter(
                matrixTraits, 10, 65, false);
            Dictionary<string, object> matrixLowPoor =
                MotiveTestOpportunity(
                    3, 1, 100, true);
            Dictionary<string, object> matrixLowRich =
                MotiveTestOpportunity(
                    3, 1, 50000000, true);
            Dictionary<string, object> matrixHighPoor =
                MotiveTestOpportunity(
                    3, 5, 100, true);
            Dictionary<string, object> matrixHighRich =
                MotiveTestOpportunity(
                    3, 5, 50000000, true);
            Dictionary<string, object> matrixHiddenRich =
                MotiveTestOpportunity(
                    3, 1, 50000000, false);
            Dictionary<string, object> matrixLowPoorPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixLowPoor, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            Dictionary<string, object> matrixLowRichPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixLowRich, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            Dictionary<string, object> matrixHighPoorPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixHighPoor, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            Dictionary<string, object> matrixHighRichPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixHighRich, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            add("court_character_tier_wealth_matrix",
                !ReadBool(matrixLowPoorPosture,
                    "recommended", true)
                && ReadBool(matrixLowRichPosture,
                    "recommended", false)
                && ReadBool(matrixHighPoorPosture,
                    "recommended", false)
                && ReadBool(matrixHighRichPosture,
                    "recommended", false)
                && ReadString(matrixLowRichPosture,
                    "bestOpportunityAxis", "") == "wealth"
                && ReadStringList(matrixHighPoorPosture,
                    "allowedCourtTactics").Contains(
                        ReadString(matrixHighPoorPosture,
                            "preferredTactic", ""),
                        StringComparer.OrdinalIgnoreCase),
                "The same low-honor Court Character restrains toward a poor lower-tier target but can exploit independent wealth or rank opportunities with a permitted tactic.");
            add("hidden_wallet_cannot_create_opportunity",
                !ReadBool(matrixHiddenRich,
                    "economicCapacityKnown", true)
                && ReadString(matrixHiddenRich,
                    "wealthEvidenceBasis", "")
                    == "hidden_wallet_not_observable"
                && Math.Abs(ReadDouble(
                        ReadDictionary(matrixHiddenRich, "axes"),
                        "wealth", -1d)
                    - ReadDouble(
                        ReadDictionary(matrixLowPoor, "axes"),
                        "wealth", -2d)) < 0.01d,
                "A hidden rich wallet produces the same perceived wealth axis as the identically presented poor case.");
            Dictionary<string, object> capContext = new Dictionary<string, object>
            {
                ["activeDomains"] = SelectMotiveDomains("I offer gold, rank, and an affair.", "", new Dictionary<string, object>(), new Dictionary<string, object>(), calculatedReady),
                ["highlightedScores"] = CoreTraitKeys.ToDictionary(x => x, x => (object)100, StringComparer.OrdinalIgnoreCase),
                ["relationshipNpcToTarget"] = CompactRelationshipEvidence(neutralRelation), ["relationshipNpcToSpouse"] = CompactRelationshipEvidence(happySpouse),
                ["opportunity"] = recognized, ["scene"] = new Dictionary<string, object> { ["private"] = false, ["exposure"] = .9d, ["witnessCount"] = 8, ["witnessIds"] = new List<string> { "a", "b", "c", "d", "e", "f", "g", "h" }, ["coercive"] = false },
                ["romance"] = calculatedReady, ["manipulation"] = intriguePosture,
                ["courtCharacter"] = ReadDictionary(courtSeductionPosture, "courtCharacter")
            };
            string cappedPrompt = BuildConversationDecisionPrompt(capContext);
            add("live_prompt_cap", cappedPrompt.Length <= MotiveLiveContextCap
                && cappedPrompt.Contains("hardConstraints")
                && cappedPrompt.Contains("receptivity")
                && cappedPrompt.Contains("female_bp2_hn2")
                && cappedPrompt.Contains("Brazen Conspirator"),
                "The live motive packet preserves posture, hard constraints, and fixed Court Character inside the 1,800-character cap.");
            Dictionary<string, object> ordinaryContext = new Dictionary<string, object>(capContext, StringComparer.OrdinalIgnoreCase)
            {
                ["activeDomains"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "power_status", ["priority"] = "primary",
                        ["evidenceKeys"] = new List<string> { "trait.pride", "trait.authorityRespect" }
                    }
                }
            };
            string ordinaryPrompt = BuildConversationDecisionPrompt(ordinaryContext);
            Dictionary<string, object> ordinaryBrief = AugmentDecisionBriefWithMotiveEvidence(
                new Dictionary<string, object>
                {
                    ["goals"] = new List<string> { "answer plainly" },
                    ["constraints"] = new List<string> { "maintain rank" }
                }, ordinaryContext);
            add("ordinary_turn_excludes_romance_posture",
                ordinaryPrompt.IndexOf("\"rom\":", StringComparison.Ordinal) < 0
                && ReadString(ordinaryBrief, "postureAlignment", "") == "not_applicable",
                "A non-romantic turn omits romance scoring from the live prompt and records no romance posture.");
            Dictionary<string, object> completedBrief = AugmentDecisionBriefWithMotiveEvidence(
                new Dictionary<string, object>
                {
                    ["decision"] = "Answer with guarded interest."
                }, ordinaryContext);
            add("missing_private_decision_lists_are_completed_without_retry",
                ReadStringList(completedBrief, "goals").SequenceEqual(new[] { "Answer with guarded interest." })
                && ReadString(completedBrief, "goalsSource", "") == "model_decision_fallback"
                && ReadStringList(completedBrief, "constraints").Count > 0
                && ReadString(completedBrief, "constraintsSource", "") == "deterministic_motive_context_fallback"
                && ReadStringList(completedBrief, "modelGoals").Count == 0
                && ReadStringList(completedBrief, "modelConstraints").Count == 0,
                "Occasional empty audit-only goal or constraint arrays are completed from the model decision and authoritative motive context without resending the full prompt.");
            Dictionary<string, object> repairContext =
                DialogueValidationRepairCharacterContext(
                    new Dictionary<string, object>
                    {
                        ["promptEnvelope"] = new Dictionary<string, object>
                        {
                            ["motiveDecision"] = capContext
                        },
                        ["messages"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                                { ["role"] = "system", ["content"] = "rules" },
                            new Dictionary<string, object>
                                { ["role"] = "user", ["content"] = "honorable, bold, and loyal character foundation" },
                            new Dictionary<string, object>
                                { ["role"] = "user", ["content"] = "live turn" }
                        }
                    });
            add("validation_repairs_receive_character_context",
                ReadDictionary(repairContext, "highlightedTraitScores").Count > 0
                && ReadDictionary(repairContext, "courtCharacter").Count > 0
                && ReadString(repairContext, "characterFoundation", "")
                    .Contains("honorable, bold, and loyal"),
                "Every constrained dialogue correction can receive the original character foundation plus compact authoritative traits, motives, relationships, scene, and political posture.");
            Dictionary<string, object> acceptedRepairMarker =
                new Dictionary<string, object>
                {
                    ["reply"] = "I have corrected my answer."
                };
            Dictionary<string, object> overriddenRepairMarker =
                new Dictionary<string, object>
                {
                    ["reply"] = "I still hold the same position."
                };
            bool acceptedMarkerApplied = MarkRepairedVisibleResponse(
                acceptedRepairMarker, true);
            bool overriddenMarkerApplied = MarkRepairedVisibleResponse(
                overriddenRepairMarker, false);
            add("visible_repair_markers_encode_revalidation_result",
                acceptedMarkerApplied
                && overriddenMarkerApplied
                && ReadString(acceptedRepairMarker, "reply", "")
                    == "I have corrected my answer.."
                && ReadString(overriddenRepairMarker, "reply", "")
                    == "I still hold the same position.,",
                "Every returned visible repair ends in '..' after clean revalidation or '.,' when a second validator rejection is deliberately overridden.");
            add("physical_threat_is_not_romance_authorization",
                !ContainsRomanticAuthorizationLanguage("Warn that the next repetition may become a physical threat or confrontation.")
                && ContainsRomanticAuthorizationLanguage("The accepted action is to kiss the petitioner."),
                "A non-romantic physical threat cannot trigger the romance hard-constraint repair, while an actual accepted kiss remains detectable.");
            Dictionary<string, object> longRelationship = new Dictionary<string, object>
            {
                ["nativeRelation"] = -17,
                ["directionalAffinity"] = -24,
                ["perceptionTag"] = new string('p', 600),
                ["observerMbti"] = "INTJ",
                ["targetMbti"] = "ENFP",
                ["compatibilityChance"] = 43,
                ["lifecycleTags"] = Enumerable.Range(0, 20).Select(i => "relationship_lifecycle_" + i + "_" + new string('t', 180)).ToList(),
                ["recentIncidents"] = Enumerable.Range(0, 6).Select(i => new Dictionary<string, object>
                {
                    ["kind"] = "incident_" + i + "_" + new string('k', 300),
                    ["worldDay"] = 100d + i,
                    ["summary"] = "Important incident evidence " + i + ": " + new string('e', 4000)
                }).ToList()
            };
            Dictionary<string, object> pathologicalCourtCharacter =
                CloneDictionary(
                    ReadDictionary(capContext, "courtCharacter")
                    ?? new Dictionary<string, object>());
            pathologicalCourtCharacter["activeScheme"] =
                new Dictionary<string, object>
                {
                    ["schemeId"] = "scheme_test",
                    ["kind"] = "court_pressure",
                    ["status"] = "active",
                    ["privatePlan"] = new string('z', 12000)
                };
            Dictionary<string, object> pathologicalContext = new Dictionary<string, object>(capContext, StringComparer.OrdinalIgnoreCase)
            {
                ["relationshipNpcToTarget"] = longRelationship,
                ["relationshipNpcToSpouse"] = longRelationship,
                ["courtCharacter"] = pathologicalCourtCharacter,
                ["spouseId"] = "spouse_with_a_real_relationship",
                ["scene"] = new Dictionary<string, object>
                {
                    ["private"] = false, ["exposure"] = .99d, ["coercive"] = false,
                    ["witnessCount"] = 200,
                    ["witnessIds"] = Enumerable.Range(0, 200).Select(i => "witness_" + i + "_" + new string('w', 300)).ToList()
                }
            };
            string pathologicalPrompt = BuildConversationDecisionPrompt(pathologicalContext);
            add("live_prompt_pathological_relationship_history", pathologicalPrompt.Length <= MotiveLiveContextCap
                && pathologicalPrompt.Contains("directionalAffinity")
                && pathologicalPrompt.Contains("hardConstraints")
                && pathologicalPrompt.Contains("receptivity")
                && !pathologicalPrompt.Contains(
                    new string('z', 100),
                    StringComparison.Ordinal),
                "Long incident summaries, lifecycle tags, witness rosters, and private scheme payloads compact to valid decision evidence instead of failing a conversation.");
            return new Dictionary<string, object> { ["ok"] = cases.All(x => ReadBool(x, "passed", false)), ["model"] = MotiveDecisionModel, ["cases"] = cases };
        }

        private static Dictionary<string, object> MotiveTestCharacteristics(Dictionary<string, int> overrides)
        {
            Dictionary<string, object> percentages = CoreTraitKeys.ToDictionary(x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> pair in overrides ?? new Dictionary<string, int>()) percentages[pair.Key] = pair.Value;
            Dictionary<string, object> groups = CourtVirtueKeys.ToDictionary(x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
            groups["boldness"] = ReadInt(percentages, "confidence", 50); groups["loyalty"] = ReadInt(percentages, "loyalty", 50);
            groups["honor"] = 50; groups["judgment"] = ReadInt(percentages, "discipline", 50);
            return new Dictionary<string, object> { ["traits"] = new Dictionary<string, object> { ["traitPercentages"] = percentages, ["courtVirtues"] = groups } };
        }

        private static void MotiveTestCourtCharacter(
            Dictionary<string, object> characteristics,
            int honor,
            int boldness,
            bool isFemale)
        {
            Dictionary<string, object> traits =
                ReadDictionary(characteristics, "traits")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> groups =
                ReadDictionary(traits, "courtVirtues")
                ?? new Dictionary<string, object>();
            groups["honor"] = honor;
            groups["boldness"] = boldness;
            traits["courtVirtues"] = groups;
            traits["courtCharacter"] = BuildCourtCharacterData(
                traits,
                new Dictionary<string, object>
                {
                    ["age"] = 30d,
                    ["isFemale"] = isFemale,
                    ["isChild"] = false
                });
            characteristics["traits"] = traits;
        }

        private static Dictionary<string, object>
            MotiveTestOpportunity(
                int observerTier,
                int playerTier,
                int playerGold,
                bool economicCapacityKnown)
        {
            Dictionary<string, object> target =
                new Dictionary<string, object>
                {
                    ["clanTier"] = playerTier,
                    ["renown"] = playerTier * 300,
                    ["fiefCount"] = 0,
                    ["wealth"] = new Dictionary<string, object>
                    {
                        ["gold"] = economicCapacityKnown
                            ? playerGold : -1,
                        ["clanGold"] = economicCapacityKnown
                            ? playerGold : -1,
                        ["clanTier"] = playerTier,
                        ["clanRenown"] = playerTier * 300,
                        ["clanFiefCount"] = 0,
                        ["economicCapacityKnown"] =
                            economicCapacityKnown,
                        ["wealthEvidenceBasis"] =
                            economicCapacityKnown
                                ? "demonstrated_financial_capacity"
                                : "hidden_wallet_not_observable"
                    },
                    ["appearance"] =
                        new Dictionary<string, object>
                        {
                            ["visibleStatusScore"] = 10d
                        }
                };
            Dictionary<string, object> observer =
                new Dictionary<string, object>
                {
                    ["clanTier"] = observerTier,
                    ["renown"] = observerTier * 300,
                    ["fiefCount"] = 1,
                    ["wealth"] = new Dictionary<string, object>
                    {
                        ["gold"] = 250000,
                        ["clanGold"] = 250000,
                        ["clanTier"] = observerTier,
                        ["clanRenown"] = observerTier * 300,
                        ["clanFiefCount"] = 1
                    },
                    ["appearance"] =
                        new Dictionary<string, object>
                        {
                            ["visibleStatusScore"] = 55d
                        }
                };
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["opportunitySnapshot"] =
                        new Dictionary<string, object>
                        {
                            ["target"] = target,
                            ["observer"] = observer,
                            ["identityKnown"] = true
                        }
                };
            return CalculatePerceivedOpportunity(
                new Dictionary<string, object>(),
                MotiveTestCharacteristics(
                    new Dictionary<string, int>()),
                payload,
                new Dictionary<string, object>
                {
                    ["identityState"] = "verified"
                });
        }

        private static Dictionary<string, object> MotiveTestRelationship(
            double directionalAffinity,
            bool lovers = false,
            bool activeAffair = false,
            bool estranged = false)
        {
            List<string> tags = new List<string>();
            if (lovers) tags.Add("lovers");
            if (activeAffair) tags.Add("active_affair");
            if (estranged) tags.Add("estranged");
            return new Dictionary<string, object>
            {
                ["relationshipModel"] = "directional_mbti_native_relation",
                ["nativeRelation"] = RoundAwayFromZero(directionalAffinity),
                ["directionalAffinity"] = directionalAffinity,
                ["perceptionTag"] = DirectionalRelationshipTag("selftest", "npc|target",
                    "observer_to_target", RoundAwayFromZero(directionalAffinity), 0),
                ["lifecycle"] = new Dictionary<string, object>
                {
                    ["lovers"] = lovers,
                    ["activeAffair"] = activeAffair,
                    ["estranged"] = estranged,
                    ["tags"] = tags
                },
                ["incidents"] = new List<Dictionary<string, object>>()
            };
        }

        private static Dictionary<string, object> MotiveTestPayload(int targetAge, bool adults, bool closeKin, bool coercive, int visibleAttractiveness)
        {
            return new Dictionary<string, object>
            {
                ["skipCooldownQuery"] = true, ["worldDay"] = 10d, ["conversationSessionId"] = "selftest",
                ["opportunitySnapshot"] = new Dictionary<string, object>
                {
                    ["target"] = new Dictionary<string, object> { ["age"] = targetAge, ["appearance"] = new Dictionary<string, object> { ["attractiveness"] = visibleAttractiveness } },
                    ["suitability"] = new Dictionary<string, object> { ["adults"] = adults, ["nativeSuitable"] = adults && !closeKin, ["closeKin"] = closeKin },
                    ["scene"] = new Dictionary<string, object> { ["private"] = true, ["exposure"] = 0.1d, ["coercive"] = coercive, ["witnessIds"] = new List<string>() }
                }
            };
        }

        private static List<Dictionary<string, object>> RunMotiveAwareConversationSelfTests()
        {
            Dictionary<string, object> report = MotiveAwareConversationSelfTests();
            List<Dictionary<string, object>> rows = ReadDictionaryList(report, "cases").Select(row => new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = ReadBool(row, "passed", false), ["suite"] = "motive_conversation",
                ["caseId"] = ReadString(row, "id", ""), ["name"] = ReadString(row, "id", ""), ["summary"] = ReadString(row, "detail", ""), ["durationMs"] = 0
            }).ToList();
            rows.AddRange(RunPoliticalDangerPostureSelfTests());
            rows.AddRange(RunConversationIntoxicationSelfTests());
            rows.AddRange(RunDialogueIntegrityAssertions().Select(row => new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = ReadBool(row, "passed", false),
                ["suite"] = "dialogue_integrity",
                ["caseId"] = ReadString(row, "name", ""),
                ["name"] = ReadString(row, "name", ""),
                ["summary"] = ReadString(row, "detail", ""),
                ["durationMs"] = 0
            }));
            rows.AddRange(RunDialogueActionAuthorityAssertions().Select(row => new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = ReadBool(row, "passed", false),
                ["suite"] = "dialogue_action_authority",
                ["caseId"] = ReadString(row, "name", ""),
                ["name"] = ReadString(row, "name", ""),
                ["summary"] = ReadString(row, "detail", ""),
                ["durationMs"] = 0
            }));
            return rows;
        }

        private static double InitiativeChanceForTest(double score) { return score < 45d ? 0d : score < 60d ? .25d : score < 75d ? .5d : score < 90d ? .75d : .9d; }
        private static double Trait01FromPercent(Dictionary<string, object> percentages, string key) { return ClampDouble(ReadDouble(percentages, key, 50d) / 100d, 0d, 1d); }
        private static double CourtGroup100(Dictionary<string, object> characteristics, string key) { return ClampDouble(ReadDouble(ReadDictionary(ReadDictionary(characteristics, "traits"), "courtVirtues") ?? new Dictionary<string, object>(), key, 50d), 0d, 100d); }
        private static double CourtGroup01(Dictionary<string, object> characteristics, string key) { return CourtGroup100(characteristics, key) / 100d; }
        private static double RomanticPressure01(Dictionary<string, object> characteristics, Dictionary<string, object> relationship, Dictionary<string, object> payload, string text)
        {
            double explicitPressure = ReadDouble(payload, "romanticSocialPressure", ReadDouble(ReadDictionary(characteristics, "pressure"), "romanticOrSocialRisk", 0d)) / 100d;
            double wording = ContainsAny((text ?? "").ToLowerInvariant(), "kiss", "flirt", "seduc", "desire", "attract", "lover", "affair", "marry") ? 0.65d : 0d;
            return ClampDouble(Math.Max(wording, explicitPressure), 0d, 1d);
        }
        private static double RomanticApproachModifier(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            if (ContainsAny(lower, "threat", "force", "or else", "execute", "kill you")) return -35d;
            if (ContainsAny(lower, "kiss", "flirt", "desire", "attract", "beautiful", "handsome", "love")) return 5d;
            return 0d;
        }
    }
}
