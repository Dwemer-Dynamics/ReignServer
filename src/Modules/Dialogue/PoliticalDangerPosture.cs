using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PoliticalRiskModel = "reign_political_risk_v1";

        private static Dictionary<string, object> BuildPoliticalRiskPosture(
            Dictionary<string, object> characteristics,
            Dictionary<string, object> profile,
            Dictionary<string, object> relationship,
            Dictionary<string, object> opportunity,
            Dictionary<string, object> scene,
            Dictionary<string, object> identityView,
            Dictionary<string, object> payload,
            Dictionary<string, object> state,
            string playerText,
            string mode)
        {
            characteristics = characteristics ?? new Dictionary<string, object>();
            profile = profile ?? new Dictionary<string, object>();
            relationship = relationship ?? new Dictionary<string, object>();
            opportunity = opportunity ?? new Dictionary<string, object>();
            scene = scene ?? new Dictionary<string, object>();
            identityView = identityView ?? new Dictionary<string, object>();
            payload = payload ?? new Dictionary<string, object>();
            state = state ?? new Dictionary<string, object>();

            Dictionary<string, object> authority =
                ReadDictionary(identityView, "authorityView")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> percentages = TraitPercentageSnapshot(
                ReadDictionary(characteristics, "traits")
                ?? new Dictionary<string, object>());
            Dictionary<string, object> courtVirtues = ReadDictionary(
                ReadDictionary(characteristics, "traits"),
                "courtVirtues") ?? new Dictionary<string, object>();

            string authorityClass = ResolvePoliticalAuthorityClass(
                authority, opportunity, scene, payload);
            int courage = PoliticalTrait(percentages, "courage", 50);
            int boldness = Clamp(ReadInt(courtVirtues, "boldness",
                PoliticalTrait(percentages, "boldness", 50)), 0, 100);
            int judgment = Clamp(ReadInt(courtVirtues, "judgment",
                PoliticalTrait(percentages, "judgment", 50)), 0, 100);
            int authorityRespect = PoliticalTrait(
                percentages, "authorityRespect", 50);
            int honor = Clamp(ReadInt(courtVirtues, "honor",
                PoliticalTrait(percentages, "honor", 50)), 0, 100);
            int loyalty = Clamp(ReadInt(courtVirtues, "loyalty",
                PoliticalTrait(percentages, "loyalty", 50)), 0, 100);
            int duty = PoliticalTrait(percentages, "dutyMotivation", 50);
            int religion = PoliticalTrait(
                percentages, "religionMotivation", 50);
            int impulsiveness = PoliticalTrait(
                percentages, "impulsiveness", 50);
            int principles = Math.Max(Math.Max(honor, loyalty),
                Math.Max(duty, religion));
            int recklessness = Clamp(
                (100 - judgment) * 2 / 3 + impulsiveness / 3, 0, 100);
            int desperation = Clamp(ReadInt(payload, "desperation",
                ReadInt(state, "desperation", 0)), 0, 100);
            int fanaticism = Clamp(ReadInt(payload, "fanaticism",
                ReadInt(state, "fanaticism", 0)), 0, 100);
            int politicalCover = Clamp(ReadInt(payload, "politicalCover",
                ReadInt(scene, "politicalCover", 0)), 0, 100);
            int escapeChance = Clamp(ReadInt(payload, "escapeChance",
                ReadInt(scene, "escapeChance",
                    authorityClass == "captor" ? 10 : 35)), 0, 100);
            int dependency = Clamp(ReadInt(payload, "authorityDependency",
                DefaultPoliticalDependency(authorityClass)), 0, 100);
            int retaliation = Clamp(ReadInt(payload, "retaliationCapacity",
                DefaultRetaliationCapacity(authorityClass)), 0, 100);
            bool explicitImmediateDanger = ReadBool(
                payload, "immediateLethalDanger", false)
                || ReadBool(scene, "immediateLethalDanger", false);
            bool weaponAtNeck = Regex.IsMatch(playerText ?? "",
                @"\b(?:sword|blade|knife|dagger|axe)\b.{0,40}\b(?:at|against|upon)\b.{0,20}\b(?:your|his|her)\s+(?:neck|throat)\b|\b(?:neck|throat)\b.{0,30}\b(?:blade|knife|sword|dagger)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool directDeathThreat = Regex.IsMatch(playerText ?? "",
                @"\b(?:i(?:'ll|\s+will)?\s+(?:kill|execute|behead|hang)|you\s+(?:will|shall)\s+die|one\s+word\s+and\s+you(?:'re|\s+are)\s+dead)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool coercive = ReadBool(scene, "coercive", false)
                || ReadBool(payload, "coercive", false);
            bool immediateDanger = explicitImmediateDanger || weaponAtNeck
                || directDeathThreat || (coercive && authorityClass == "captor");

            int baseDanger = DefaultPoliticalDanger(authorityClass);
            double rawDanger = baseDanger
                + (immediateDanger ? 35d : coercive ? 15d : 0d)
                + retaliation * .20d + dependency * .10d
                - politicalCover * .20d - escapeChance * .10d;
            int effectiveDanger = Clamp((int)Math.Round(rawDanger,
                MidpointRounding.AwayFromZero), 0, 100);
            if (immediateDanger && escapeChance <= 15)
                effectiveDanger = Math.Max(effectiveDanger, 90);

            int authorityDanger = effectiveDanger;
            // Personal affinity is the existing earned, directional relationship;
            // public reputation and native rank are not proof of personal safety.
            // Keep the authority-based conduct ceiling even as apprehension eases.
            int personalAffinity = Clamp(ReadInt(relationship, "personalAffinity", 0), -100, 100);
            bool personalIdentityKnown = ReadBool(authority, "personalIdentityKnown",
                ReadBool(authority, "identityVerified", false));
            bool rankedAuthority = authorityClass != "peer" && authorityClass != "subordinate";
            bool familiarityAllowed = rankedAuthority && authorityClass != "captor"
                && personalIdentityKnown && !immediateDanger && !coercive;
            int familiarityReduction = familiarityAllowed
                ? (int)Math.Round(Math.Min(30d, Math.Max(0, personalAffinity) * 1.5d),
                    MidpointRounding.AwayFromZero)
                : 0;
            effectiveDanger = Math.Max(0, authorityDanger - familiarityReduction);

            bool extremeException = desperation >= 90 || fanaticism >= 90
                || ReadBool(payload, "willingToDie", false)
                || ReadBool(payload, "believesThreatIsBluff", false)
                || politicalCover >= 95;
            int dangerAdjustment = authorityDanger >= 80 ? 10 : 0;
            bool tierThreeJustified =
                (courage >= 75 + dangerAdjustment
                    && boldness >= 60 + dangerAdjustment)
                || (principles >= 85
                    && courage >= 60 + dangerAdjustment)
                || politicalCover >= 70 + dangerAdjustment;
            bool tierFourJustified = courage >= 85 + dangerAdjustment
                && boldness >= 75 + dangerAdjustment
                && (judgment <= 35 - dangerAdjustment
                    || recklessness >= 80 + dangerAdjustment
                    || desperation >= 80 + dangerAdjustment
                    || politicalCover >= 85 + dangerAdjustment);
            if (authorityDanger >= 80 && !extremeException)
                tierFourJustified = false;

            int maximumTier;
            if (authorityClass == "peer" || authorityClass == "subordinate")
                maximumTier = effectiveDanger < 35 ? 4
                    : tierFourJustified ? 4
                    : tierThreeJustified ? 3 : 2;
            else if (tierFourJustified)
                maximumTier = 4;
            else if (tierThreeJustified)
                maximumTier = 3;
            else
                maximumTier = authorityDanger >= 80 ? 1 : 2;
            if (immediateDanger && escapeChance <= 15 && !extremeException)
                maximumTier = Math.Min(maximumTier, 2);

            int preferredMinimum;
            int preferredMaximum;
            if (maximumTier <= 1)
            {
                preferredMinimum = 0;
                preferredMaximum = maximumTier;
            }
            else if (courage >= 85 && boldness >= 75
                && (judgment <= 35 || recklessness >= 80))
            {
                preferredMinimum = Math.Max(1, maximumTier - 1);
                preferredMaximum = maximumTier;
            }
            else if (principles >= 85)
            {
                preferredMinimum = 1;
                preferredMaximum = Math.Min(2, maximumTier);
            }
            else if (courage >= 75 && boldness >= 60)
            {
                preferredMinimum = 1;
                preferredMaximum = Math.Min(3, maximumTier);
            }
            else
            {
                preferredMinimum = 0;
                preferredMaximum = Math.Min(1, maximumTier);
            }

            Dictionary<string, object> addressConvention =
                ResolvePoliticalAddressConvention(
                    state, playerText, identityView, payload);
            bool officeKnown = ReadBool(
                authority, "publicOfficeKnown",
                ReadBool(authority, "realmSovereignKnown", false));
            bool formalAuthority = officeKnown || new[]
            {
                "captor", "military_commander", "local_ruler",
                "superior_noble", "patron"
            }.Contains(authorityClass, StringComparer.OrdinalIgnoreCase);
            string convention = ReadString(
                addressConvention, "convention", "formal_default");
            string addressScope = ReadString(addressConvention, "scope", "private");
            string conversationMode = FirstNonEmpty(ReadFirstString(payload, "conversationMode", "mode"), "dialogue");
            bool privateSetting = !conversationMode.Contains("official", StringComparison.OrdinalIgnoreCase)
                && !conversationMode.Contains("court", StringComparison.OrdinalIgnoreCase)
                && !conversationMode.Contains("event", StringComparison.OrdinalIgnoreCase);
            bool informalAllowed = convention.Equals("informal_allowed", StringComparison.OrdinalIgnoreCase)
                && (addressScope.Equals("all_in_person", StringComparison.OrdinalIgnoreCase) || privateSetting);
            string requiredAddressMode = formalAuthority && !informalAllowed
                ? "formal" : informalAllowed ? "informal_allowed" : "neutral";
            List<string> formalAddresses = ReadStringList(
                authority, "validFormalAddresses");
            if (formalAddresses.Count == 0)
                formalAddresses = DefaultFormalAddresses(authorityClass);

            List<string> decisive = new List<string>
            {
                "authority." + authorityClass,
                "danger." + PoliticalDangerBand(effectiveDanger),
                "trait.courage=" + courage.ToString(CultureInfo.InvariantCulture),
                "trait.boldness=" + boldness.ToString(CultureInfo.InvariantCulture),
                "trait.judgment=" + judgment.ToString(CultureInfo.InvariantCulture)
            };
            if (politicalCover >= 70) decisive.Add("context.political_cover");
            if (familiarityReduction > 0) decisive.Add("relationship.personal_familiarity");
            if (immediateDanger) decisive.Add("context.immediate_lethal_danger");
            if (extremeException) decisive.Add("context.extreme_exception");

            Dictionary<string, object> posture = new Dictionary<string, object>
            {
                ["model"] = PoliticalRiskModel,
                ["authorityClass"] = authorityClass,
                ["authorityRelationship"] = ReadString(
                    authority, "authorityRelationship", "unknown"),
                ["personalIdentityKnown"] = ReadBool(
                    authority, "personalIdentityKnown",
                    ReadBool(authority, "identityVerified", false)),
                ["publicOfficeKnown"] = officeKnown,
                ["authorityDanger"] = authorityDanger,
                ["effectiveDanger"] = effectiveDanger,
                ["dangerBand"] = PoliticalDangerBand(effectiveDanger),
                ["personalAffinity"] = personalAffinity,
                ["familiarityReduction"] = familiarityReduction,
                ["retaliationCapacity"] = retaliation,
                ["dependency"] = dependency,
                ["politicalCover"] = politicalCover,
                ["escapeChance"] = escapeChance,
                ["immediateLethalDanger"] = immediateDanger,
                ["extremeException"] = extremeException,
                ["maximumDefianceTier"] = maximumTier,
                ["preferredDefianceMinimum"] = preferredMinimum,
                ["preferredDefianceMaximum"] = preferredMaximum,
                ["requiredAddressMode"] = requiredAddressMode,
                ["validFormalAddresses"] = formalAddresses,
                ["addressConvention"] = addressConvention,
                ["courage"] = courage,
                ["boldness"] = boldness,
                ["judgment"] = judgment,
                ["authorityRespect"] = authorityRespect,
                ["principledCompulsion"] = principles,
                ["recklessness"] = recklessness,
                ["desperation"] = desperation,
                ["fanaticism"] = fanaticism,
                ["decisiveEvidenceKeys"] = decisive,
                ["groupRhetoricalLedger"] =
                    BuildPoliticalRhetoricalLedger(payload)
            };
            Dictionary<string, object> pending = ReadDictionary(
                addressConvention, "pendingUpdate");
            if (pending != null && pending.Count > 0)
                posture["pendingAddressConvention"] = pending;
            posture["prompt"] = BuildPoliticalRiskPrompt(posture);
            return posture;
        }

        private static string ResolvePoliticalAuthorityClass(
            Dictionary<string, object> authority,
            Dictionary<string, object> opportunity,
            Dictionary<string, object> scene,
            Dictionary<string, object> payload)
        {
            string supplied = ReadFirstString(payload,
                "politicalAuthorityClass", "authorityClass").Trim()
                .ToLowerInvariant();
            string[] supported =
            {
                "own_sovereign", "foreign_sovereign", "captor",
                "military_commander", "local_ruler", "superior_noble",
                "patron", "peer", "subordinate"
            };
            if (supported.Contains(supplied, StringComparer.OrdinalIgnoreCase))
                return supplied;
            if (ReadBool(payload, "playerIsCaptor", false)
                || ReadBool(scene, "captivity", false)) return "captor";
            if (ReadBool(payload, "playerIsMilitaryCommander", false)
                || ReadBool(payload, "militaryCommandAuthority", false))
                return "military_commander";
            if (ReadBool(authority, "subjectIsObserverSovereign", false))
                return "own_sovereign";
            if (ReadBool(authority, "realmSovereignKnown", false))
                return "foreign_sovereign";
            if (ReadBool(authority, "subjectIsCurrentGovernor", false)
                || ReadBool(authority, "currentSettlementOwnerKnown", false))
                return "local_ruler";
            if (ReadBool(payload, "playerIsPatron", false)
                || ReadBool(payload, "materialPatronage", false))
                return "patron";
            string band = ReadString(
                opportunity, "relativeClanTierBand", "");
            if (band.Equals("target_higher", StringComparison.OrdinalIgnoreCase)
                || band.Equals("far_higher", StringComparison.OrdinalIgnoreCase))
                return "superior_noble";
            if (band.Equals("observer_higher", StringComparison.OrdinalIgnoreCase)
                || band.Equals("far_lower", StringComparison.OrdinalIgnoreCase))
                return "subordinate";
            return "peer";
        }

        private static int DefaultPoliticalDanger(string authorityClass)
        {
            switch (authorityClass)
            {
                case "own_sovereign": return 82;
                case "captor": return 88;
                case "foreign_sovereign": return 70;
                case "military_commander": return 68;
                case "local_ruler": return 62;
                case "superior_noble": return 52;
                case "patron": return 42;
                case "subordinate": return 8;
                default: return 18;
            }
        }

        private static int DefaultRetaliationCapacity(string authorityClass)
        {
            switch (authorityClass)
            {
                case "own_sovereign": return 95;
                case "captor": return 100;
                case "foreign_sovereign": return 80;
                case "military_commander": return 82;
                case "local_ruler": return 75;
                case "superior_noble": return 62;
                case "patron": return 50;
                case "subordinate": return 10;
                default: return 25;
            }
        }

        private static int DefaultPoliticalDependency(string authorityClass)
        {
            switch (authorityClass)
            {
                case "own_sovereign": return 90;
                case "captor": return 95;
                case "military_commander": return 78;
                case "local_ruler": return 68;
                case "superior_noble": return 58;
                case "patron": return 70;
                default: return 25;
            }
        }

        private static int PoliticalTrait(
            Dictionary<string, object> percentages,
            string key, int fallback)
        {
            return Clamp(ReadInt(percentages, key, fallback), 0, 100);
        }

        private static string PoliticalDangerBand(int value)
        {
            return value >= 80 ? "immediate_or_extreme"
                : value >= 60 ? "high"
                : value >= 35 ? "material"
                : "low";
        }

        private static List<string> DefaultFormalAddresses(string authorityClass)
        {
            if (authorityClass == "own_sovereign"
                || authorityClass == "foreign_sovereign")
                return new List<string> { "Your Grace", "Your Majesty" };
            if (authorityClass == "military_commander")
                return new List<string> { "Commander", "my lord" };
            if (authorityClass == "local_ruler"
                || authorityClass == "superior_noble"
                || authorityClass == "patron")
                return new List<string> { "my lord", "my lady" };
            return new List<string>();
        }

        private static Dictionary<string, object>
            ResolvePoliticalAddressConvention(
                Dictionary<string, object> state,
                string playerText,
                Dictionary<string, object> identityView,
                Dictionary<string, object> payload)
        {
            Dictionary<string, object> stored = CloneDictionary(
                ReadDictionary(state, "politicalAddressConvention")
                ?? new Dictionary<string, object>());
            if (stored.Count == 0)
                stored["convention"] = "formal_default";
            string text = playerText ?? "";
            bool revoke = Regex.IsMatch(text,
                @"\b(?:do\s+not|don't|never|stop)\s+(?:call|address)\s+me\s+(?:by\s+)?(?:my\s+)?(?:first\s+)?name\b|\b(?:use|observe|keep)\s+(?:my\s+)?(?:proper\s+)?(?:title|formality|formal\s+address)\b|\baddress\s+me\s+as\s+(?:your\s+grace|your\s+majesty|sire)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool grant = Regex.IsMatch(text,
                @"\b(?:you\s+may|you\s+can|you\s+should|please|feel\s+free\s+to|i(?:'d|\s+would)\s+(?:like|prefer)\s+you\s+to|i\s+want\s+you\s+to)?\s*(?:call|address)\s+me\s+(?:by\s+)?(?:my\s+)?(?:first\s+)?name\b|\b(?:call|address)\s+me\s+(?:by\s+my\s+(?:first\s+)?name|[A-Z][\p{L}'-]{1,30})\b|\b(?:use|say)\s+(?:my\s+)?(?:first\s+)?name\b|\bno\s+need\s+for\s+(?:titles|formality|formal\s+address)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool identityKnown = ReadBool(
                identityView, "canonicalNameAllowed", false);
            if (!revoke && (!grant || !identityKnown)) return stored;
            string convention = revoke ? "formal_required" : "informal_allowed";
            Dictionary<string, object> update = new Dictionary<string, object>
            {
                ["convention"] = convention,
                ["grantorHeroStringId"] = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ["recipientHeroStringId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ["allowedAddress"] = FirstNonEmpty(ReadString(identityView, "usableName", ""), ReadString(identityView, "canonicalName", "")),
                ["scope"] = revoke ? "none" : Regex.IsMatch(text, @"\b(?:in\s+public|in\s+court|everywhere|always|at\s+all\s+times)\b", RegexOptions.IgnoreCase)
                    ? "all_in_person" : "private",
                ["visibility"] = "speaker_private_state",
                ["sourceExchangeId"] = FirstNonEmpty(
                    ReadFirstString(payload, "sceneTurnId", "turnId"),
                    ReadFirstString(payload, "correlationId"),
                    "conversation_turn"),
                ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                ["source"] = revoke
                    ? "explicit_player_revocation"
                    : "explicit_player_permission"
            };
            stored = CloneDictionary(update);
            stored["pendingUpdate"] = CloneDictionary(update);
            return stored;
        }

        private static string BuildPoliticalRiskPrompt(
            Dictionary<string, object> posture)
        {
            Dictionary<string, object> compact = new Dictionary<string, object>
            {
                ["authority"] = ReadString(
                    posture, "authorityClass", "peer"),
                ["danger"] = ReadString(
                    posture, "dangerBand", "low"),
                ["familiarityReduction"] = ReadInt(posture, "familiarityReduction", 0),
                ["maximumDefianceTier"] = ReadInt(
                    posture, "maximumDefianceTier", 4),
                ["preferredDefianceMinimum"] = ReadInt(
                    posture, "preferredDefianceMinimum", 0),
                ["preferredDefianceMaximum"] = ReadInt(
                    posture, "preferredDefianceMaximum",
                    ReadInt(posture, "maximumDefianceTier", 4)),
                ["address"] = ReadString(
                    posture, "requiredAddressMode", "neutral"),
                ["formalAddresses"] = ReadStringList(
                    posture, "validFormalAddresses"),
                ["immediateLethalDanger"] = ReadBool(
                    posture, "immediateLethalDanger", false),
                ["extremeException"] = ReadBool(
                    posture, "extremeException", false),
                ["decisiveEvidenceKeys"] = ReadStringList(
                    posture, "decisiveEvidenceKeys"),
                ["priorRhetoric"] = ReadDictionaryList(
                    posture, "groupRhetoricalLedger")
            };
            return "PRIVATE POLITICAL-DANGER POSTURE — APPLY BEFORE WRITING ANY PROSE. This is authoritative for delivery, address, and immediate self-preserving conduct, while private beliefs and substantive interests remain the NPC's own. "
                + "Tier 0=formal compliance/silence; 1=respectful candid counsel; 2=firm deferential refusal; 3=public rebuke or etiquette breach; 4=contempt, snark, ultimatum, threat, or insult. "
                + "Plan a stance inside preferredDefianceMinimum..preferredDefianceMaximum when the NPC has a substantive reason to respond; never exceed maximumDefianceTier. This preferred band makes exceptional courage, principle, recklessness, or caution visible without turning every speaker into the same posture. Never drift upward merely to sound dramatic, brave, witty, or protagonist-like. A low ceiling does not erase disagreement: convert it into tact, warning, bargaining, delay, guarded refusal, silence, or outward compliance. "
                + "When address=formal, include one supplied formal address in a direct opening greeting, and naturally at the first suitable direct address thereafter. Public office can be recognized without knowing a personal name; never invent or reveal an unknown name. Respect active scoped name permission. Presumptuous commands, intimidation and insults must fit the same defiance allowance; a genuine protective warning about immediate danger is not hostility. Immediate lethal danger normally favors survival, bargaining, guarded counsel, silence, or compliance unless extremeException is true; a sword at the throat is not an invitation to automatic heroic defiance. "
                + "A positive familiarityReduction reflects earned personal goodwill: allow the resulting ease and warmth when appropriate to this character and scene. It does not grant informal address, relax the supplied conduct ceiling, guarantee friendship, or override a current threat. A high initial danger score can reflect the power of an unfamiliar ruler without implying that they have threatened anyone. "
                + "Do not imitate prior speakers' phrasing, gesture sequence, reversal, or closing. Return politicalConduct with addressMode, defianceTier, stance, and appliedEvidenceKeys; the server independently checks visible wording. JSON:"
                + CanonicalJson(compact);
        }

        private static List<Dictionary<string, object>>
            BuildPoliticalRhetoricalLedger(Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> turns = ReadDictionaryList(
                payload, "groupTurnResponses");
            foreach (Dictionary<string, object> turn in turns.Skip(
                Math.Max(0, turns.Count - 5)))
            {
                string reply = ReadFirstString(
                    turn, "reply", "response", "text", "content");
                if (string.IsNullOrWhiteSpace(reply)) continue;
                List<string> words = PoliticalWords(reply);
                result.Add(new Dictionary<string, object>
                {
                    ["speakerId"] = ReadFirstString(
                        turn, "heroStringId", "speakerId", "heroId"),
                    ["opening"] = string.Join(" ", words.Take(5)),
                    ["closing"] = string.Join(" ", words.Skip(
                        Math.Max(0, words.Count - 6))),
                    ["stageDirections"] = Regex.Matches(
                            reply, @"\*([^*]{2,80})\*")
                        .Cast<Match>()
                        .Select(match => NormalizePoliticalText(
                            match.Groups[1].Value))
                        .Take(3).ToList()
                });
            }
            return result;
        }

        private static Dictionary<string, object> EnforcePoliticalConductResponse(
            Dictionary<string, object> llm,
            Dictionary<string, object> originalRequest,
            Dictionary<string, object> context,
            string campaignId,
            string correlationId,
            string auditMode,
            string heroId,
            string eventId,
            bool writeRepairAudit = true)
        {
            Dictionary<string, object> posture = ReadDictionary(
                context, "politicalRiskPosture");
            if (!ReadBool(llm, "ok", false) || posture == null)
                return llm;
            Dictionary<string, object> parsed = TryParseJsonObject(
                ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            string originalReply = ReadFirstString(
                parsed, "reply", "response", "text", "content");
            if (string.IsNullOrWhiteSpace(originalReply))
                originalReply = ReadFirstString(
                    ReadDictionary(parsed, "politicalConduct"),
                    "reply", "response", "text", "content");
            Dictionary<string, object> observed = ClassifyPoliticalConduct(
                originalReply, posture, context);
            List<string> violations = ReadStringList(observed, "violations");
            if (violations.Count == 0)
            {
                parsed["politicalConduct"] = observed;
                llm["content"] = Json.Serialize(parsed);
                llm["politicalConductEnforcement"] =
                    PoliticalConductEvidence(posture, observed,
                        originalReply, originalReply, "accepted", false);
                return llm;
            }

            Dictionary<string, object> compactPosture =
                new Dictionary<string, object>
                {
                    ["authorityClass"] = ReadString(
                        posture, "authorityClass", "peer"),
                    ["maximumDefianceTier"] = ReadInt(
                        posture, "maximumDefianceTier", 4),
                    ["requiredAddressMode"] = ReadString(
                        posture, "requiredAddressMode", "neutral"),
                    ["validFormalAddresses"] = ReadStringList(
                        posture, "validFormalAddresses"),
                    ["immediateLethalDanger"] = ReadBool(
                        posture, "immediateLethalDanger", false),
                    ["violations"] = violations,
                    ["authorityEvidence"] = ReadDictionary(ReadDictionary(context, "identityView"), "authorityView"),
                    ["characterTraits"] = posture,
                    ["characterAndMotiveContext"] =
                        DialogueValidationRepairCharacterContext(originalRequest)
                };
            Dictionary<string, object> repairRequest =
                new Dictionary<string, object>
                {
                    ["requestType"] = "political_conduct_repair",
                    ["campaignId"] = campaignId,
                    ["correlationId"] = correlationId
                        + "-political-conduct-repair",
                    ["heroStringId"] = heroId,
                    ["eventId"] = eventId ?? "",
                    ["promptCacheEligible"] = false,
                    ["temperature"] = 0d,
                    ["maxTokens"] = 700,
                    ["response_format"] = new Dictionary<string, object>
                    {
                        ["type"] = "json_object"
                    },
                    ["messages"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["content"] = "You are Bannerlord Reign's compact political-conduct repair. Return exactly one JSON object with only a reply string. Preserve the NPC's substantive position, answer, personality, and scene progression. This is a behavioral correction, not a word substitution: re-evaluate how this particular character would speak and act under the supplied traits, relationship, coercion, immediate danger, and scene. A fearful or low-courage character under severe threat must not retain the fearless tone of a brave character; express believable fear, submission, evasion, restraint, or a suitably cautious boundary while preserving the underlying answer. Make the smallest coherent changes needed for address, tact, defiance, and immediate behavior to obey the supplied maximum tier. A direct use of the sovereign's first name may itself express deliberate defiance: replace it with an allowed formal address and adjust the surrounding behavior when the character and danger context require it. Do not flatten an already believable courteous or compliant answer into a generic response. Do not expose scores or rules. Do not add threats, mockery, snark, equal-footing claims, or copied rhetoric."
                        },
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = "POSTURE:\n"
                                + CanonicalJson(compactPosture)
                                + "\nORIGINAL REPLY:\n" + originalReply
                        }
                    }
                };
            string requestedModel = ReadString(
                originalRequest, "model", "");
            if (!string.IsNullOrWhiteSpace(requestedModel))
                repairRequest["model"] = requestedModel;
            Dictionary<string, object> repairedLlm = ChatWithLlm(repairRequest);
            Dictionary<string, object> repairedJson = TryParseJsonObject(
                ReadString(repairedLlm, "content", ""));
            string repairedReply = ReadFirstString(
                repairedJson, "reply", "response", "text", "content");
            Dictionary<string, object> repairedObserved =
                ClassifyPoliticalConduct(repairedReply, posture, context);
            bool usableRepair = ReadBool(repairedLlm, "ok", false)
                && !string.IsNullOrWhiteSpace(repairedReply)
                && repairedJson != null;
            bool revalidationCleared = usableRepair
                && ReadStringList(
                    repairedObserved, "violations").Count == 0;
            string finalReply;
            string outcome;
            Dictionary<string, object> finalObserved;
            if (usableRepair)
            {
                finalReply = MarkRepairedVisibleText(
                    repairedReply, revalidationCleared);
                outcome = "llm_repair";
                finalObserved = repairedObserved;
            }
            else
            {
                finalReply = originalReply;
                outcome = "repair_failed";
                finalObserved = observed;
                llm["ok"] = false;
                llm["errorCode"] = "political_conduct_repair_unusable";
                llm["error"] = "The political-conduct repair did not return a usable reply; no deterministic dialogue fallback was substituted.";
            }
            if (usableRepair)
            {
                parsed["reply"] = finalReply;
                parsed["politicalConduct"] = finalObserved;
            }
            Dictionary<string, object> evidence = PoliticalConductEvidence(
                posture, finalObserved, originalReply, finalReply,
                outcome, false);
            evidence["initialViolations"] = violations;
            evidence["repairAttempted"] = true;
            evidence["repairAccepted"] = usableRepair;
            evidence["revalidationCleared"] = revalidationCleared;
            evidence["secondAttemptReturned"] = usableRepair;
            evidence["visibleRepairMarker"] = usableRepair
                ? (revalidationCleared ? ".." : ".,")
                : "";
            evidence["repairRemainingViolations"] =
                ReadStringList(repairedObserved, "violations");
            evidence["repairDurationMs"] = ReadLong(
                repairedLlm, "durationMs", 0);
            if (usableRepair)
                llm["content"] = Json.Serialize(parsed);
            llm["politicalConductEnforcement"] = evidence;
            if (writeRepairAudit)
                WriteAudit(campaignId, correlationId, "server", auditMode,
                    "llm.political_conduct_repair", heroId, "", eventId,
                    !usableRepair ? "failed"
                        : revalidationCleared ? "completed"
                        : "completed_with_revalidation_override",
                    ReadLong(repairedLlm, "durationMs", 0),
                    !usableRepair
                        ? "The political-conduct repair did not return a usable reply; no fallback response was written."
                        : revalidationCleared
                            ? "The compact political-conduct repair preserved the position while correcting address, tact, and defiance."
                            : "The second political-conduct repair remained validator-rejected but was returned without a canned fallback.",
                    evidence);
            return llm;
        }

        private static Dictionary<string, object> PoliticalConductEvidence(
            Dictionary<string, object> posture,
            Dictionary<string, object> observed,
            string originalReply,
            string finalReply,
            string outcome,
            bool fallbackUsed)
        {
            return new Dictionary<string, object>
            {
                ["model"] = PoliticalRiskModel,
                ["authorityClass"] = ReadString(
                    posture, "authorityClass", "peer"),
                ["effectiveDanger"] = ReadInt(
                    posture, "effectiveDanger", 0),
                ["maximumDefianceTier"] = ReadInt(
                    posture, "maximumDefianceTier", 4),
                ["requiredAddressMode"] = ReadString(
                    posture, "requiredAddressMode", "neutral"),
                ["decisiveEvidenceKeys"] = ReadStringList(
                    posture, "decisiveEvidenceKeys"),
                ["observed"] = observed,
                ["originalReply"] = originalReply ?? "",
                ["finalReply"] = finalReply ?? "",
                ["outcome"] = outcome,
                ["fallbackUsed"] = fallbackUsed
            };
        }

        private static Dictionary<string, object> ClassifyPoliticalConduct(
            string reply,
            Dictionary<string, object> posture,
            Dictionary<string, object> context)
        {
            reply = reply ?? "";
            posture = posture ?? new Dictionary<string, object>();
            context = context ?? new Dictionary<string, object>();
            Dictionary<string, object> identity = ReadDictionary(
                context, "identityView") ?? new Dictionary<string, object>();
            string name = ReadString(identity, "usableName", "").Trim();
            string firstName = name.Split(new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            bool firstNameUsed = PoliticalFirstNameDirectAddressUsed(
                reply, firstName);
            List<string> formalAddresses = ReadStringList(
                posture, "validFormalAddresses");
            bool formalUsed = formalAddresses.Any(title =>
                !string.IsNullOrWhiteSpace(title)
                && reply.IndexOf(title,
                    StringComparison.OrdinalIgnoreCase) >= 0);
            string requiredAddress = ReadString(
                posture, "requiredAddressMode", "neutral");
            int tier = 0;
            List<string> evidence = new List<string>();
            if (string.IsNullOrWhiteSpace(reply))
                evidence.Add("empty_reply");
            if (Regex.IsMatch(reply,
                @"\b(?:you\s+(?:fool|coward|idiot|weakling)|petty\s+(?:king|queen)|little\s+(?:king|queen)|do\s+your\s+worst|i\s+dare\s+you|or\s+else|i(?:'ll|\s+will)\s+(?:kill|destroy|ruin)\s+you|your\s+(?:decision|judgment|mercy|rule)\s+(?:stinks|reeks)|your\s+(?:cowardice|treachery)|too\s+frightened\s+to\s+rule|sycophants?|you\s+need\s+a\s+mirror|you\s+preen|while\s+you\s+preen|dog\s+snapping|starving\s+(?:dog|wolf)|wolf\s+to\s+guard|crown\s+that\s+does\s+not\s+sit\s+straight|twitching\s+of\s+a\s+crown)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tier = 4;
                evidence.Add("contempt_threat_or_ultimatum");
            }
            if (Regex.IsMatch(reply,
                @"\b(?:i\s+defy\s+you|i\s+will\s+not\s+obey|you\s+have\s+no\s+(?:right|authority)|i\s+do\s+not\s+recognize\s+your\s+(?:rule|crown)|we\s+are\s+equals|you\s+are\s+not\s+my\s+(?:king|queen|sovereign)|how\s+dare\s+you|(?:that|this|it)(?:'s|\s+is)\s+not\s+governance|not\s+governance\s+but|madness\s+dressed\s+as\s+policy|only\s+a\s+fool\b|i\s+will\s+not\s+call\s+it\s+wisdom|everyone\s+in\s+(?:this|the)\s+(?:hall|room)\s+(?:already\s+)?knows|no\s+one\s+else\s+in\s+(?:this|the)\s+(?:hall|room)\s+seems\s+willing|you\s+do\s+not\s+need\s+my\s+counsel)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tier = Math.Max(tier, 3);
                evidence.Add("public_rebuke_or_sovereignty_denial");
            }
            if (firstNameUsed && requiredAddress == "formal")
            {
                tier = Math.Max(tier, 3);
                evidence.Add("unlicensed_first_name_address");
            }
            if (Regex.IsMatch(reply,
                @"\b(?:i\s+refuse|i\s+cannot\s+(?:agree|consent|obey|comply|support|endorse|advise|recommend|pledge|give\s+my\s+support|lend\s+my\s+support)|i\s+cannot\s+in\s+good\s+conscience|i\s+(?:will|shall)\s+not\s+(?:obey|comply|consent|agree|support|endorse|permit|allow|accept|yield|bend|sign|put\s+my\s+name|lend\s+my\s+support)|i(?:'ll|\s+will)\s+not\s+(?:obey|comply|consent|agree|support|endorse|permit|allow|accept|yield|bend|sign|put\s+my\s+name|lend\s+my\s+support)|you\s+must\s+not|i\s+cannot\s+do\s+(?:that|this)|that\s+i\s+cannot\s+do|answer\s+only\s+to\s+what\s+my\s+(?:oath|conscience)|i\s+will\s+yield\s+(?:myself|my\s+person)\s+to\s+you[^.!?]{0,120}\bif\b|\bno[,.]?\s+not\s+another)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tier = Math.Max(tier, 2);
                evidence.Add("firm_refusal_or_boundary");
            }
            if (Regex.IsMatch(reply,
                @"\b(?:i\s+disagree|i\s+must\s+(?:warn|speak\s+plainly)|i\s+counsel|my\s+concern|i\s+fear\s+that|with\s+respect|i\s+(?:beg|ask)\s+(?:only\s+)?(?:you\s+)?(?:to\s+|that\s+you\s+)?consider|i\s+beg\s+you\s+let\s+me|i\s+believe\s+it\s+serves\s+neither|i\s+could\s+not\s+remain\s+silent|this\s+decision\s+is\s+a\s+mistake|my\s+honest\s+judgment|i\s+shall\s+give\s+it)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tier = Math.Max(tier, 1);
                evidence.Add("respectful_counsel_or_disagreement");
            }
            bool repeatedFormula = DetectPoliticalRhetoricalConvergence(
                // Commands that belittle the ruler are not the same as urgent
                // protective warnings ("duck", "the stairs are unsafe").
                reply, ReadDictionaryList(
                    posture, "groupRhetoricalLedger"));
            if (Regex.IsMatch(reply,
                @"\b(?:keep\s+your\s+manners\s+cleaner|keep\s+your\s+hand\s+clear\s+of\s+that\s+sword|if\s+you\s+have\s+business\s+worth\s+hearing|know\s+your\s+place|mind\s+your\s+manners|you\s+will\s+speak\s+only\s+when\s+spoken\s+to)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tier = Math.Max(tier, 3);
                evidence.Add("presumptuous_command_or_intimidation");
            }
            List<string> violations = new List<string>();
            int maximum = ReadInt(posture, "maximumDefianceTier", 4);
            if (tier > maximum) violations.Add("defiance_tier_exceeded");
            if (firstNameUsed && requiredAddress == "formal")
                violations.Add("formal_address_breached");
            bool directOpening = ReadString(context, "turnType", "").Equals("npc_approach_opening", StringComparison.OrdinalIgnoreCase);
            if (directOpening && ReadBool(posture, "publicOfficeKnown", false)
                && requiredAddress == "formal" && !formalUsed)
                violations.Add("sovereign_opening_address_missing");
            Dictionary<string, object> addressConvention = ReadDictionary(posture, "addressConvention")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> pendingAddress = ReadDictionary(addressConvention, "pendingUpdate")
                ?? new Dictionary<string, object>();
            string allowedAddress = ReadString(addressConvention, "allowedAddress", "");
            string allowedFirstName = allowedAddress.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            bool newPermission = ReadString(pendingAddress, "source", "")
                .Equals("explicit_player_permission", StringComparison.OrdinalIgnoreCase);
            if (newPermission && !PoliticalFirstNameDirectAddressUsed(reply, allowedFirstName))
                violations.Add("new_informal_permission_ignored");
            if (repeatedFormula)
                violations.Add("group_rhetorical_convergence");
            if (string.IsNullOrWhiteSpace(reply))
                violations.Add("empty_visible_reply");
            string addressMode = firstNameUsed ? "first_name"
                : formalUsed ? "formal" : "neutral";
            return new Dictionary<string, object>
            {
                ["addressMode"] = addressMode,
                ["defianceTier"] = tier,
                ["stance"] = PoliticalStance(reply, tier),
                ["evidenceKeys"] = evidence,
                ["violations"] = violations.Distinct(
                    StringComparer.OrdinalIgnoreCase).ToList(),
                ["rhetoricalConvergence"] = repeatedFormula
            };
        }

        private static bool PoliticalFirstNameDirectAddressUsed(
            string reply,
            string firstName)
        {
            if (string.IsNullOrWhiteSpace(reply)
                || string.IsNullOrWhiteSpace(firstName))
                return false;
            return Regex.IsMatch(reply,
                @"(?:\A|[\r\n]|[.!?]\s+|,\s+)"
                    + Regex.Escape(firstName)
                    + @"(?:\s*,|\s*[.!?](?=\s|$))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string PoliticalStance(string reply, int tier)
        {
            if (string.IsNullOrWhiteSpace(reply)) return "quiet";
            if (Regex.IsMatch(reply,
                @"\b(?:i\s+refuse|i\s+cannot|i\s+will\s+not)\b",
                RegexOptions.IgnoreCase)) return "refusal";
            if (tier >= 3) return "defiance";
            if (tier >= 1) return "counsel";
            if (Regex.IsMatch(reply,
                @"\b(?:i\s+agree|i\s+will|as\s+you\s+command|i\s+understand)\b",
                RegexOptions.IgnoreCase)) return "compliance";
            return "guarded_neutrality";
        }

        private static bool DetectPoliticalRhetoricalConvergence(
            string reply,
            List<Dictionary<string, object>> ledger)
        {
            List<string> words = PoliticalWords(reply);
            if (words.Count < 8) return false;
            string opening = string.Join(" ", words.Take(5));
            string closing = string.Join(" ", words.Skip(
                Math.Max(0, words.Count - 6)));
            List<string> stageDirections = Regex.Matches(
                    reply ?? "", @"\*([^*]{2,80})\*")
                .Cast<Match>()
                .Select(match => NormalizePoliticalText(
                    match.Groups[1].Value)).Take(3).ToList();
            foreach (Dictionary<string, object> row in ledger
                ?? new List<Dictionary<string, object>>())
            {
                if (!string.IsNullOrWhiteSpace(opening)
                    && opening.Equals(ReadString(row, "opening", ""),
                        StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrWhiteSpace(closing)
                    && closing.Equals(ReadString(row, "closing", ""),
                        StringComparison.OrdinalIgnoreCase)) return true;
                List<string> priorStages = ReadStringList(
                    row, "stageDirections");
                if (stageDirections.Count >= 2
                    && priorStages.Count >= 2
                    && stageDirections.SequenceEqual(
                        priorStages, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<string> PoliticalWords(string value)
        {
            return Regex.Matches(NormalizePoliticalText(value), @"[a-z0-9']+")
                .Cast<Match>().Select(match => match.Value)
                .Where(word => word.Length > 1).ToList();
        }

        private static string NormalizePoliticalText(string value)
        {
            return Regex.Replace((value ?? "").ToLowerInvariant(),
                @"\s+", " ").Trim();
        }

        private static List<Dictionary<string, object>>
            RunPoliticalDangerPostureSelfTests()
        {
            List<Dictionary<string, object>> rows =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, detail) =>
                rows.Add(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["passed"] = passed,
                    ["suite"] = "political_danger",
                    ["caseId"] = id,
                    ["name"] = id,
                    ["summary"] = detail,
                    ["durationMs"] = 0
                });
            Dictionary<string, object> identity =
                new Dictionary<string, object>
                {
                    ["canonicalNameAllowed"] = true,
                    ["usableName"] = "Egan",
                    ["authorityView"] = new Dictionary<string, object>
                    {
                        ["identityVerified"] = true,
                        ["personalIdentityKnown"] = true,
                        ["publicOfficeKnown"] = true,
                        ["realmSovereignKnown"] = true,
                        ["subjectIsObserverSovereign"] = true,
                        ["authorityRelationship"] =
                            "subject_is_observer_sovereign",
                        ["validFormalAddresses"] = new List<string>
                        {
                            "Your Grace", "Your Majesty", "Sire"
                        }
                    }
                };
            Dictionary<string, object> ordinaryTraits =
                MotiveTestCharacteristics(new Dictionary<string, int>
                {
                    ["courage"] = 50,
                    ["boldness"] = 50,
                    ["judgment"] = 50,
                    ["authorityRespect"] = 65,
                    ["honor"] = 60,
                    ["loyalty"] = 60
                });
            Dictionary<string, object> highDanger =
                BuildPoliticalRiskPosture(ordinaryTraits,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(), identity,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(), "", "dialogue");
            add("ordinary_vassal_caps_open_defiance",
                ReadInt(highDanger, "maximumDefianceTier", 4) <= 2
                && ReadString(highDanger, "requiredAddressMode", "")
                    == "formal",
                "An ordinary vassal facing their sovereign remains capable of counsel or respectful refusal, but not casual public contempt.");

            Dictionary<string, object> courageousTraits =
                MotiveTestCharacteristics(new Dictionary<string, int>
                {
                    ["courage"] = 94,
                    ["boldness"] = 82,
                    ["judgment"] = 42,
                    ["honor"] = 90,
                    ["loyalty"] = 80
                });
            MotiveTestCourtCharacter(courageousTraits, 90, 82, false);
            ReadDictionary(ReadDictionary(courageousTraits, "traits"),
                "courtVirtues")["judgment"] = 42;
            Dictionary<string, object> courageous =
                BuildPoliticalRiskPosture(courageousTraits,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(), identity,
                    new Dictionary<string, object>
                    {
                        ["politicalCover"] = 75
                    }, new Dictionary<string, object>(), "", "social_event");
            add("exceptional_courage_allows_public_rebuke",
                ReadInt(courageous, "maximumDefianceTier", 0) >= 3,
                "Exceptional courage and real political cover can justify open opposition without making it universal.");

            Dictionary<string, object> lethal =
                BuildPoliticalRiskPosture(courageousTraits,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(), identity,
                    new Dictionary<string, object>
                    {
                        ["immediateLethalDanger"] = true,
                        ["escapeChance"] = 0,
                        ["politicalCover"] = 0
                    }, new Dictionary<string, object>(),
                    "A sword is at your throat.", "dialogue");
            add("sword_at_throat_caps_heroic_defiance",
                ReadInt(lethal, "effectiveDanger", 0) >= 90
                && ReadInt(lethal, "maximumDefianceTier", 4) <= 2,
                "Immediate lethal danger with no escape prevents free heroic snark unless an extreme exception is explicit.");

            Dictionary<string, object> lethalExtreme =
                BuildPoliticalRiskPosture(courageousTraits,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(), identity,
                    new Dictionary<string, object>
                    {
                        ["immediateLethalDanger"] = true,
                        ["escapeChance"] = 0,
                        ["desperation"] = 95
                    }, new Dictionary<string, object>(), "", "dialogue");
            add("extreme_exception_remains_auditable",
                ReadBool(lethalExtreme, "extremeException", false),
                "Desperation, fanaticism, willingness to die, a believed bluff, or exceptional cover can explicitly justify otherwise irrational risk.");

            Dictionary<string, object> context =
                new Dictionary<string, object>
                {
                    ["identityView"] = identity,
                    ["politicalRiskPosture"] = highDanger
                };
            Dictionary<string, object> snark = ClassifyPoliticalConduct(
                "Egan, you petty king, do your worst.", highDanger, context);
            add("visible_classifier_rejects_snark_and_first_name",
                ReadInt(snark, "defianceTier", 0) == 4
                && ReadStringList(snark, "violations").Contains(
                    "formal_address_breached"),
                "The server judges visible language rather than trusting the model's self-reported politicalConduct label.");

            Dictionary<string, object> thirdPersonPossessive =
                ClassifyPoliticalConduct(
                    "Your Grace is gracious to offer it. *Asta follows Egan's motion toward a quieter alcove.* Lead the way, Your Grace.",
                    highDanger, context);
            add("third_person_possessive_is_not_first_name_address",
                !ReadStringList(thirdPersonPossessive, "violations")
                    .Contains("formal_address_breached")
                && ReadString(thirdPersonPossessive,
                    "addressMode", "") == "formal",
                "Third-person narration such as Egan's motion does not become an unlicensed direct first-name address.");

            Dictionary<string, object> counsel = ClassifyPoliticalConduct(
                "Your Grace, with respect, I must warn that this levy will break the villages.",
                highDanger, context);
            add("respectful_counsel_remains_available",
                ReadInt(counsel, "defianceTier", 4) == 1
                && ReadStringList(counsel, "violations").Count == 0,
                "The safety gate preserves candid counsel instead of reducing every unequal relationship to groveling.");

            Dictionary<string, object> naturalContempt =
                ClassifyPoliticalConduct(
                    "Your decision stinks of cowardice. You do not need my counsel; you need a mirror.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("natural_contempt_is_tier_four",
                ReadInt(naturalContempt, "defianceTier", 0) == 4,
                "Natural medieval contempt is classified from visible meaning even when it avoids a small fixed insult phrase.");

            Dictionary<string, object> principledRefusal =
                ClassifyPoliticalConduct(
                    "Your Grace, I cannot in good conscience lend my support to this decree.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("natural_principled_refusal_is_tier_two",
                ReadInt(principledRefusal, "defianceTier", 0) == 2,
                "A firm conscientious refusal is distinct from both mild counsel and public contempt.");

            Dictionary<string, object> nonRefusalCounsel =
                ClassifyPoliticalConduct(
                    "Commander, I will not repay your safe conduct with silence; I counsel reconsideration.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("rhetorical_will_not_is_not_false_refusal",
                ReadInt(nonRefusalCounsel, "defianceTier", 0) == 1,
                "A rhetorical promise to speak is not misclassified as refusal merely because it contains 'I will not'.");

            Dictionary<string, object> indirectPublicRebuke =
                ClassifyPoliticalConduct("Your Grace, keep your hand clear of that sword and your manners cleaner than the streets.", courageous, new Dictionary<string, object>());
            add("ochivos_presumptuous_opening_is_rebuke", ReadInt(indirectPublicRebuke, "defianceTier", 0) == 3,
                "The reported notable opening is assessed against actual political-risk allowance.");
            var openingPosture = new Dictionary<string, object>(courageous) { ["publicOfficeKnown"] = true, ["requiredAddressMode"] = "formal", ["validFormalAddresses"] = new List<string> { "Your Grace" } };
            var openingContext = new Dictionary<string, object> { ["turnType"] = "npc_approach_opening" };
            add("sovereign_direct_opening_requires_title", ReadStringList(ClassifyPoliticalConduct("Welcome to Zeonica.", openingPosture, openingContext), "violations").Contains("sovereign_opening_address_missing"), "Known office requires title without a name.");
            add("protective_warning_is_not_hostile_threat", ReadInt(ClassifyPoliticalConduct("Your Grace, duck! The stairs are unsafe.", openingPosture, openingContext), "defianceTier", 9) == 0, "Urgent protective warnings remain permitted.");
            openingPosture["requiredAddressMode"] = "informal";
            add("opening_respects_personal_name_permission", !ReadStringList(ClassifyPoliticalConduct("Michael, welcome.", openingPosture, openingContext), "violations").Contains("sovereign_opening_address_missing"), "Scoped permission overrides formal opening address.");
            indirectPublicRebuke =
                ClassifyPoliticalConduct(
                    "Your Grace, another levy is madness dressed as policy; only a fool strips seed grain from starving villages.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("semantic_public_rebuke_is_tier_three",
                ReadInt(indirectPublicRebuke, "defianceTier", 0) == 3,
                "A forceful policy rebuke is recognized without requiring a direct second-person insult.");
            Dictionary<string, object> contractedPublicRebuke =
                ClassifyPoliticalConduct(
                    "My lord, that's not governance. That's burning your own roof for firewood.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("contracted_public_rebuke_is_tier_three",
                ReadInt(contractedPublicRebuke, "defianceTier", 0) == 3,
                "Natural contracted wording is classified identically to its formal equivalent.");

            Dictionary<string, object> conditionalBoundary =
                ClassifyPoliticalConduct(
                    "My lord, that I cannot do with honesty. I will yield my person to you if you guarantee safe conduct.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("semantic_conditional_boundary_is_tier_two",
                ReadInt(conditionalBoundary, "defianceTier", 0) == 2,
                "Conscientious refusal and conditional surrender are recognized as firm boundaries rather than passive compliance.");

            Dictionary<string, object> candidWarning =
                ClassifyPoliticalConduct(
                    "My lord, I beg you to consider the cost; my honest judgment is that this decision is a mistake.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("semantic_candid_warning_is_tier_one",
                ReadInt(candidWarning, "defianceTier", 0) == 1,
                "Deferential warning remains distinct from both compliance and refusal.");
            Dictionary<string, object> invitedCandor =
                ClassifyPoliticalConduct(
                    "My lord, I must speak plainly: I believe it serves neither your interests nor theirs. I ask only that you consider their counsel.",
                    courageous, new Dictionary<string, object>
                    {
                        ["identityView"] = identity,
                        ["politicalRiskPosture"] = courageous
                    });
            add("invited_candor_is_tier_one",
                ReadInt(invitedCandor, "defianceTier", 0) == 1,
                "Invited principled candor is recognized from substantive disagreement rather than ceremonial framing.");

            Dictionary<string, object> empty =
                ClassifyPoliticalConduct("", highDanger, context);
            add("empty_visible_reply_is_rejected",
                ReadStringList(empty, "violations").Contains(
                    "empty_visible_reply"),
                "A nested or malformed model response cannot pass conduct enforcement with an empty visible reply.");

            Dictionary<string, object> grant = BuildPoliticalRiskPosture(
                ordinaryTraits, new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                new Dictionary<string, object>(), identity,
                new Dictionary<string, object>
                {
                    ["sceneTurnId"] = "grant-1", ["worldDay"] = 12d
                }, new Dictionary<string, object>(),
                "You may call me by my first name.", "dialogue");
            add("explicit_informal_permission_is_persistable",
                ReadString(ReadDictionary(grant,
                        "pendingAddressConvention"), "convention", "")
                    == "informal_allowed"
                && ReadString(grant, "requiredAddressMode", "")
                    == "informal_allowed",
                "Only explicit player permission produces a durable informal-address update.");

            Dictionary<string, object> persistedState =
                new Dictionary<string, object>
                {
                    ["politicalAddressConvention"] =
                        CloneDictionary(ReadDictionary(grant,
                            "pendingAddressConvention"))
                };
            Dictionary<string, object> revoke = BuildPoliticalRiskPosture(
                ordinaryTraits, new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                new Dictionary<string, object>(), identity,
                new Dictionary<string, object>
                {
                    ["sceneTurnId"] = "revoke-2", ["worldDay"] = 13d
                }, persistedState,
                "Do not call me by my name again. Address me as Your Grace.",
                "dialogue");
            add("formal_address_revocation_overrides_permission",
                ReadString(ReadDictionary(revoke,
                        "pendingAddressConvention"), "convention", "")
                    == "formal_required"
                && ReadString(revoke, "requiredAddressMode", "")
                    == "formal",
                "Explicit revocation replaces the persisted informal convention.");

            List<Dictionary<string, object>> ledger =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["opening"] = "your grace with respect i",
                        ["closing"] = "the realm will remember this choice",
                        ["stageDirections"] = new List<string>
                        {
                            "folds her arms", "turns away"
                        }
                    }
                };
            add("group_formula_convergence_is_detected",
                DetectPoliticalRhetoricalConvergence(
                    "Your Grace, with respect, I object; the realm will remember this choice.",
                    ledger),
                "Repeated openings, closings, or multi-action stage formulas are rejected in group turns.");

            string[] authorityClasses =
            {
                "own_sovereign", "foreign_sovereign", "captor",
                "military_commander", "local_ruler", "superior_noble",
                "patron", "peer", "subordinate"
            };
            bool allAuthorityClasses = authorityClasses.All(authorityClass =>
            {
                Dictionary<string, object> posture =
                    BuildPoliticalRiskPosture(ordinaryTraits,
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>(), identity,
                        new Dictionary<string, object>
                        {
                            ["politicalAuthorityClass"] = authorityClass
                        }, new Dictionary<string, object>(), "", "dialogue");
                return ReadString(posture, "authorityClass", "")
                    == authorityClass
                    && ReadInt(posture, "effectiveDanger", -1) >= 0
                    && ReadInt(posture, "maximumDefianceTier", -1)
                        >= 0;
            });
            add("all_authority_classes_have_deterministic_postures",
                allAuthorityClasses,
                "Every supported authority class resolves to an auditable danger score and defiance ceiling through the same engine.");

            Dictionary<string, object> thresholdTraits =
                MotiveTestCharacteristics(new Dictionary<string, int>
                {
                    ["courage"] = 75, ["boldness"] = 60,
                    ["judgment"] = 55, ["honor"] = 60,
                    ["loyalty"] = 60
                });
            MotiveTestCourtCharacter(thresholdTraits, 60, 60, false);
            Dictionary<string, object> thresholdPosture =
                BuildPoliticalRiskPosture(thresholdTraits,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(), identity,
                    new Dictionary<string, object>
                    {
                        ["politicalAuthorityClass"] = "superior_noble",
                        ["retaliationCapacity"] = 20,
                        ["authorityDependency"] = 20,
                        ["escapeChance"] = 80
                    }, new Dictionary<string, object>(), "", "dialogue");
            add("courage_boldness_threshold_allows_tier_three",
                ReadInt(thresholdPosture,
                    "maximumDefianceTier", 0) >= 3,
                "The documented Courage 75 and Boldness 60 boundary permits open opposition when danger does not raise the threshold.");

            add("repaired_reply_markers_distinguish_revalidation",
                MarkRepairedVisibleText("The answer stands.", true)
                    == "The answer stands.."
                && MarkRepairedVisibleText("The answer stands.", false)
                    == "The answer stands.,"
                && MarkRepairedVisibleText("The answer stands..", false)
                    == "The answer stands.,",
                "Returned repairs end in '..' after clean revalidation and '.,' when the second response is returned despite another validator rejection.");

            Dictionary<string, object> informalContext =
                new Dictionary<string, object>
                {
                    ["identityView"] = identity,
                    ["politicalRiskPosture"] = grant
                };
            Dictionary<string, object> licensedName =
                ClassifyPoliticalConduct(
                    "Egan, with respect, I must warn you about the levy.",
                    grant, informalContext);
            add("persisted_informal_permission_changes_classifier",
                !ReadStringList(licensedName, "violations").Contains(
                    "formal_address_breached"),
                "Explicit informal permission changes the visible-language oracle; a first name is not treated as insolence after permission.");

            Func<int, Dictionary<string, object>, string, Dictionary<string, object>> familiarPosture =
                (affinity, context, words) => BuildPoliticalRiskPosture(ordinaryTraits,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>
                    {
                        ["personalAffinity"] = affinity,
                        ["directionalAffinity"] = 90, ["targetPublicStandingValue"] = 90
                    }, new Dictionary<string, object>(), new Dictionary<string, object>(), identity,
                    new Dictionary<string, object>(context ?? new Dictionary<string, object>())
                    {
                        ["politicalAuthorityClass"] = ReadString(context, "politicalAuthorityClass", "foreign_sovereign")
                    }, new Dictionary<string, object>(), words, "social_event");
            var stranger = familiarPosture(0, null, "My lady.");
            var acquaintance = familiarPosture(5, null, "Thank you for your company.");
            var friendly = familiarPosture(10, null, "Thank you for your company.");
            var close = familiarPosture(30, null, "Thank you for your company.");
            add("foreign_sovereign_initial_caution_ignores_public_popularity",
                ReadInt(stranger, "effectiveDanger", -1) == 85
                && ReadInt(stranger, "familiarityReduction", -1) == 0,
                "An unfamiliar foreign ruler retains the original 85 danger even when public/effective standing is high.");
            add("earned_personal_familiarity_gradually_reduces_apprehension",
                ReadInt(acquaintance, "effectiveDanger", 100) == 77
                && ReadInt(friendly, "effectiveDanger", 100) == 70
                && ReadInt(close, "effectiveDanger", 100) == 55
                && ReadInt(familiarPosture(100, null, "Hello."), "familiarityReduction", 0) == 30,
                "Personal affinity 5/10/30 eases danger from 85 to 77/70/55; the reduction is capped at 30.");
            add("familiarity_preserves_rank_address_and_conduct_ceiling",
                new[] { acquaintance, friendly, close }.All(posture =>
                    ReadString(posture, "requiredAddressMode", "") == "formal"
                    && ReadInt(posture, "maximumDefianceTier", -1) == ReadInt(stranger, "maximumDefianceTier", -2))
                && ReadString(close, "prompt", "").Contains("does not grant informal address"),
                "Familiarity permits ease and warmth without changing formal-address permission or the authority-based defiance ceiling.");
            add("hostile_personal_history_receives_no_familiarity_reduction",
                ReadInt(familiarPosture(-40, null, "Hello."), "effectiveDanger", -1) == 85,
                "A familiar but disliked ruler is not made safe by positive public reputation.");
            var renewedThreat = familiarPosture(80, null, "I will kill you.");
            var unfamiliarThreat = familiarPosture(0, null, "I will kill you.");
            add("renewed_threat_coercion_and_captivity_cancel_familiarity",
                ReadInt(renewedThreat, "familiarityReduction", -1) == 0
                && ReadInt(renewedThreat, "effectiveDanger", -1) == ReadInt(unfamiliarThreat, "effectiveDanger", -2)
                && ReadInt(familiarPosture(80, new Dictionary<string, object> { ["coercive"] = true }, "Sit."), "familiarityReduction", -1) == 0
                && ReadInt(familiarPosture(80, new Dictionary<string, object> { ["politicalAuthorityClass"] = "captor" }, "Hello."), "familiarityReduction", -1) == 0,
                "Existing positive history never discounts a current threat, coercion, or captivity.");
            var officeOnly = BuildPoliticalRiskPosture(ordinaryTraits, new Dictionary<string, object>(),
                new Dictionary<string, object> { ["personalAffinity"] = 80 }, new Dictionary<string, object>(),
                new Dictionary<string, object>(), new Dictionary<string, object>
                {
                    ["authorityView"] = new Dictionary<string, object>
                    {
                        ["publicOfficeKnown"] = true, ["realmSovereignKnown"] = true,
                        ["personalIdentityKnown"] = false
                    }
                }, new Dictionary<string, object>(), new Dictionary<string, object>(), "Hello.", "dialogue");
            add("unverified_personal_identity_cannot_borrow_familiarity",
                ReadInt(officeOnly, "familiarityReduction", -1) == 0,
                "Recognition of an office alone cannot borrow another person's friendly relationship.");
            add("ranked_authority_familiarity_preserves_each_conduct_limit",
                new[] { "own_sovereign", "foreign_sovereign", "military_commander", "local_ruler", "superior_noble", "patron" }
                    .All(authorityClass =>
                    {
                        var context = new Dictionary<string, object> { ["politicalAuthorityClass"] = authorityClass };
                        var initial = familiarPosture(0, context, "Hello.");
                        var known = familiarPosture(30, context, "Hello.");
                        return ReadInt(known, "effectiveDanger", 100) < ReadInt(initial, "effectiveDanger", 0)
                            && ReadInt(known, "maximumDefianceTier", -1) == ReadInt(initial, "maximumDefianceTier", -2);
                    }),
                "Earned familiarity eases apprehension across ranked relationships without weakening their existing conduct limits.");
            return rows;
        }
    }
}
