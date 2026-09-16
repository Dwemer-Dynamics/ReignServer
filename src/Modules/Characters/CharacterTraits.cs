using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int CoreTraitDocumentVersion = 5;
        private const int FoundationTraitModelVersion = 2;
        private const string FoundationTraitModelId = "reign_foundation_weighted_v2";
        private const string EditorFoundationTraitModelId = "reign_foundation_editor_override_v1";
        private const int PersonalityPortraitVersion = 1;
        private const string PersonalityPortraitModelId = "reign_personality_portrait_v1";
        private const int PersonalitySummaryMinimumCharacters = 1200;
        private const int PersonalitySummaryMaximumCharacters = 1600;

        private static readonly string[] PersonalityPortraitKeys =
        {
            "coreTemperament", "socialStyle", "trustAndAttachment", "powerAndStatus", "decisionStyle",
            "emotionalPattern", "moralBoundaries", "underPressure", "internalContradictions"
        };

        private static readonly string[] CoreTraitKeys =
        {
            "curiosity", "ambition", "honesty", "compassion", "courage", "discipline", "sociability", "emotionalStability", "pride", "patience",
            "socialTrust", "flirtatiousness", "authorityRespect", "assertiveness", "tact", "loyalty", "vengefulness",
            "wealthMotivation", "powerMotivation", "familyMotivation", "fameMotivation", "knowledgeMotivation", "religionMotivation", "revengeMotivation", "dutyMotivation", "survivalMotivation", "legacyMotivation",
            "riskTolerance", "impulsiveness", "pragmatism", "traditionalism", "mercy", "aggression", "generosity", "greed",
            "jealousy", "empathy", "envy", "optimism", "fearfulness", "irritability", "confidence", "shame"
        };

        private static readonly HashSet<string> NativeLockedCoreTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "courage", "mercy", "generosity"
        };

        private static Dictionary<string, object> BuildTraitDocument(Dictionary<string, object> hero)
        {
            hero = hero ?? new Dictionary<string, object>();
            Dictionary<string, object> native = ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> skills = ReadDictionary(hero, "skills") ?? new Dictionary<string, object>();
            Dictionary<string, object> foundation = CalculateFoundationTraitsVNext(hero);
            Dictionary<string, object> attractiveness = BuildAttractiveness(hero);

            Dictionary<string, object> document = new Dictionary<string, object>
            {
                ["version"] = CoreTraitDocumentVersion,
                ["model"] = FoundationTraitModelId,
                ["scale"] = "integer -2..2",
                ["scaleMeaning"] = new Dictionary<string, object>
                {
                    ["-2"] = "strong defining inverse",
                    ["-1"] = "clear inverse tendency",
                    ["0"] = "balanced, ordinary, or situational",
                    ["1"] = "clear positive tendency",
                    ["2"] = "strong defining tendency"
                },
                ["visibleBannerlordTraits"] = native,
                ["nativeSkills"] = skills,
                ["foundationTraits"] = foundation,
                ["hiddenReignTraits"] = new Dictionary<string, object>(foundation, StringComparer.OrdinalIgnoreCase),
                ["foundationTraitModel"] = FoundationTraitModelDefinition(false),
                ["nativeDescriptionEvidence"] = FirstNonEmpty(ReadString(hero, "nativeEncyclopediaText", ""), ReadString(hero, "encyclopediaText", "")),
                ["assignmentProtocol"] = new Dictionary<string, object>
                {
                    ["precedence"] = new ArrayList { "locked native trait", "explicit native description", "weighted native personality traits", "signed native skills", "bounded age and station context" },
                    ["nativeDescriptionRule"] = "Explicit native character descriptions may override non-locked assignments only when the construction engine records supporting evidence.",
                    ["skillRule"] = "Skills use signal = clamp((skill - 100) / 50, -2, +2). They provide bounded evidence of practiced behavior and capability and do not independently assign morality.",
                    ["nativeSignalRule"] = "Native -1 or lower maps to -2, native 0 maps to 0, and native +1 or higher maps to +2.",
                    ["contextRule"] = "Age, notable/wanderer status, and clan position may be positive or negative evidence but together cannot exceed 25 percent absolute formula weight. Clan position is neutral at tiers 1-3, 0.30 at tier 4, 0.60 at tier 5, and 1.00 at tier 6.",
                    ["rounding"] = "Weighted results round away from zero at -1.5, -0.5, +0.5, and +1.5.",
                    ["familyRule"] = "Having a spouse, parents, children, or other family is not trait evidence.",
                    ["lockedMappings"] = new Dictionary<string, object> { ["courage"] = "expanded native Valor", ["mercy"] = "expanded native Mercy", ["generosity"] = "expanded native Generosity" }
                },
                ["assignmentSources"] = CoreTraitAssignmentSources(),
                ["attractiveness"] = attractiveness,
                ["definitions"] = TraitDefinitions()
            };
            EnsureTraitPercentageData(document, ReadString(hero, "heroStringId", ReadString(hero, "characterObjectId", "unknown")));
            EnsureCourtCharacterData(document, hero, ReadString(hero, "heroStringId", ReadString(hero, "characterObjectId", "unknown")));
            RebuildPersonalityPortrait(document, ReadString(hero, "name", "This character"), "", "deterministic_trait_foundation");
            return document;
        }

        private static Dictionary<string, object> FoundationTraitModelDefinition(bool editorOverride)
        {
            return new Dictionary<string, object>
            {
                ["version"] = FoundationTraitModelVersion,
                ["id"] = editorOverride ? EditorFoundationTraitModelId : FoundationTraitModelId,
                ["source"] = editorOverride ? "character_editor_override" : "declarative_weighted_formulas",
                ["nativeSignal"] = "-1 or lower -> -2; 0 -> 0; +1 or higher -> +2",
                ["skillSignal"] = "clamp((skill - 100) / 50, -2, +2)",
                ["contextAbsoluteWeightCapPercent"] = 25,
                ["familyPresenceRule"] = "neutral",
                ["rounding"] = "MidpointRounding.AwayFromZero at half-step boundaries"
            };
        }

        private static void MarkEditorFoundationTraitsCurrent(Dictionary<string, object> traits)
        {
            if (traits == null) return;
            traits["foundationTraitModel"] = FoundationTraitModelDefinition(true);
            traits["version"] = CoreTraitDocumentVersion;
            traits["model"] = EditorFoundationTraitModelId;
        }

        private static bool FoundationTraitModelCurrent(Dictionary<string, object> traits)
        {
            Dictionary<string, object> model = ReadDictionary(traits, "foundationTraitModel") ?? new Dictionary<string, object>();
            if (ReadInt(model, "version", 0) != FoundationTraitModelVersion) return false;
            string id = ReadString(model, "id", "");
            return id.Equals(FoundationTraitModelId, StringComparison.OrdinalIgnoreCase)
                || id.Equals(EditorFoundationTraitModelId, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildCoreTraitAssignments(Dictionary<string, object> hero, Dictionary<string, object> native, Dictionary<string, object> skills)
        {
            double valor = TraitInt(native, "valor");
            double generosity = TraitInt(native, "generosity");
            double honor = TraitInt(native, "honor");
            double mercy = TraitInt(native, "mercy");
            double calculating = TraitInt(native, "calculating");
            double charm = SkillTraitSignal(skills, "charm");
            double leadership = SkillTraitSignal(skills, "leadership");
            double steward = SkillTraitSignal(skills, "steward");
            double trade = SkillTraitSignal(skills, "trade");
            double roguery = SkillTraitSignal(skills, "roguery");
            double tactics = SkillTraitSignal(skills, "tactics");
            double medicine = SkillTraitSignal(skills, "medicine");
            double engineering = SkillTraitSignal(skills, "engineering");
            double scouting = SkillTraitSignal(skills, "scouting");
            double martial = AverageSkillSignal(skills, "oneHanded", "twoHanded", "polearm", "bow", "crossbow", "throwing", "riding", "athletics");
            double isLord = ReadBool(hero, "isLord", false) ? 1d : 0d;
            double isNotable = ReadBool(hero, "isNotable", false) ? 1d : 0d;
            double isWanderer = ReadBool(hero, "isWanderer", false) ? 1d : 0d;
            double age = ReadDouble(hero, "age", 0d) >= 40d ? 1d : 0d;
            double family = !string.IsNullOrWhiteSpace(ReadString(hero, "spouseId", "")) || !string.IsNullOrWhiteSpace(ReadString(hero, "fatherId", "")) || !string.IsNullOrWhiteSpace(ReadString(hero, "motherId", "")) ? 1d : 0d;
            double clan = ReadInt(hero, "clanTier", 0) >= 4 ? 1d : ReadInt(hero, "clanTier", 0) >= 2 ? 0.5d : 0d;
            string heroId = ReadString(hero, "heroStringId", ReadString(hero, "characterObjectId", "unknown"));

            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Action<string, double> set = (key, score) => values[key] = QuantizeCoreTrait(score + StableTraitTieBreaker(heroId, key));
            Func<string, double> value = key => ReadInt(values, key, 0);

            set("curiosity", 0.30d * calculating + 0.25d * scouting + 0.20d * engineering + 0.15d * medicine);
            set("ambition", 0.45d * calculating + 0.30d * leadership + 0.25d * isLord + 0.15d * clan - 0.10d * generosity);
            set("honesty", 0.70d * honor - 0.15d * roguery);
            set("compassion", 0.45d * mercy + 0.35d * generosity + 0.15d * medicine);
            values["courage"] = ClampTrait((int)valor);
            set("discipline", 0.45d * calculating + 0.25d * steward + 0.20d * tactics + 0.10d * age);
            set("sociability", 0.35d * generosity + 0.35d * charm + 0.15d * isNotable);
            set("emotionalStability", 0.60d * calculating + 0.15d * age);
            set("pride", 0.35d * valor + 0.25d * honor + 0.25d * isLord + 0.15d * leadership);
            set("patience", 0.55d * calculating + 0.25d * steward + 0.15d * age);

            set("socialTrust", 0.35d * generosity + 0.25d * mercy + 0.15d * honor - 0.10d * roguery);
            set("flirtatiousness", 0.35d * charm + 0.15d * generosity);
            set("authorityRespect", 0.45d * honor + 0.20d * steward + 0.20d * isLord + 0.15d * age);
            set("assertiveness", 0.40d * valor + 0.30d * leadership + 0.25d * isLord - 0.15d * mercy);
            set("tact", 0.40d * calculating + 0.35d * charm + 0.10d * steward - 0.15d * valor);
            set("loyalty", 0.45d * honor + 0.35d * generosity + 0.20d * family);
            set("vengefulness", -0.55d * mercy + 0.25d * valor + 0.20d * value("pride"));

            set("wealthMotivation", -0.35d * generosity + 0.40d * trade + 0.20d * isNotable);
            set("powerMotivation", 0.35d * value("ambition") + 0.35d * leadership + 0.30d * isLord);
            set("familyMotivation", 0.45d * generosity + 0.25d * honor + 0.30d * family);
            set("fameMotivation", 0.35d * valor + 0.30d * leadership + 0.20d * isLord + 0.15d * value("pride"));
            set("knowledgeMotivation", 0.25d * calculating + 0.25d * engineering + 0.20d * medicine + 0.20d * scouting + 0.10d * steward);
            set("religionMotivation", 0.40d * honor + 0.20d * age + 0.15d * isLord);
            set("revengeMotivation", 0.55d * value("vengefulness") + 0.20d * value("pride") - 0.20d * mercy);
            set("dutyMotivation", 0.45d * honor + 0.25d * steward + 0.20d * isLord + 0.10d * leadership);
            set("survivalMotivation", -0.25d * valor + 0.20d * calculating + 0.25d * isWanderer);
            set("legacyMotivation", 0.30d * value("ambition") + 0.25d * isLord + 0.20d * family + 0.15d * leadership + 0.10d * age);

            set("riskTolerance", 0.65d * valor + 0.20d * value("ambition") - 0.15d * value("survivalMotivation"));
            set("impulsiveness", -0.65d * calculating - 0.15d * steward + 0.20d * valor);
            set("pragmatism", 0.55d * calculating + 0.25d * trade + 0.20d * steward);
            set("traditionalism", 0.40d * honor + 0.25d * age + 0.20d * isLord - 0.15d * value("curiosity"));
            values["mercy"] = ClampTrait((int)mercy);
            set("aggression", 0.40d * valor - 0.45d * mercy + 0.20d * tactics + 0.15d * martial);
            values["generosity"] = ClampTrait((int)generosity);
            set("greed", -0.60d * generosity + 0.25d * trade + 0.15d * value("wealthMotivation"));

            set("envy", -0.50d * generosity + 0.25d * value("pride") + 0.25d * value("fameMotivation"));
            set("jealousy", 0.35d * value("envy") + 0.25d * value("pride") + 0.20d * value("flirtatiousness"));
            set("empathy", 0.40d * mercy + 0.35d * generosity + 0.15d * charm + 0.10d * medicine);
            set("optimism", 0.25d * valor + 0.25d * value("emotionalStability") + 0.15d * generosity);
            set("fearfulness", -0.55d * valor - 0.25d * value("emotionalStability") + 0.20d * value("survivalMotivation"));
            set("irritability", -0.45d * calculating + 0.25d * value("pride") + 0.20d * value("aggression"));
            set("confidence", 0.40d * valor + 0.30d * leadership + 0.15d * charm + 0.15d * martial);
            set("shame", 0.40d * honor + 0.30d * value("pride") + 0.15d * value("authorityRespect") + 0.15d * value("sociability"));

            return values;
        }

        private static int QuantizeCoreTrait(double score)
        {
            return ClampTrait((int)Math.Round(score, MidpointRounding.AwayFromZero));
        }

        private static double SkillTraitSignal(Dictionary<string, object> skills, string key)
        {
            int level = Math.Max(0, ReadInt(skills, key, 0));
            if (level < 50) return 0d;
            if (level < 100) return 0.5d;
            if (level < 150) return 1d;
            if (level < 200) return 1.5d;
            return 2d;
        }

        private static double AverageSkillSignal(Dictionary<string, object> skills, params string[] keys)
        {
            if (keys == null || keys.Length == 0) return 0d;
            return keys.Select(key => SkillTraitSignal(skills, key)).Average();
        }

        private static double StableTraitTieBreaker(string heroId, string traitKey)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in (heroId + "|" + traitKey + "|v2"))
                {
                    hash ^= c;
                    hash *= 16777619;
                }

                int bucket = (int)(hash % 5);
                return bucket == 0 ? -0.25d : bucket == 4 ? 0.25d : 0d;
            }
        }

        private static Dictionary<string, object> CoreTraitAssignmentSources()
        {
            Dictionary<string, object> sources = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["courage"] = "100% expanded native Valor; native-description corrections are locked out",
                ["mercy"] = "100% expanded native Mercy; native-description corrections are locked out",
                ["generosity"] = "100% expanded native Generosity; native-description corrections are locked out"
            };
            foreach (KeyValuePair<string, FoundationTraitRangeFormulaVNext> pair in GetFoundationTraitRangeVNextFormulas())
            {
                sources[pair.Key] = string.Join(", ", pair.Value.Terms.Select(term =>
                    (term.Weight < 0d ? "inverse " : "")
                    + term.InputKey
                    + " "
                    + Math.Round(Math.Abs(term.Weight) * 100d, 2).ToString("0.##", CultureInfo.InvariantCulture)
                    + "%"))
                    + "; explicit native-description evidence may correct non-locked assignments";
            }
            return sources;
        }

        private static Dictionary<string, object> TraitDefinitions()
        {
            Dictionary<string, object> definitions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string[]> pair in TraitSentenceDescriptors())
            {
                definitions[pair.Key] = new Dictionary<string, object>
                {
                    ["-2"] = pair.Value[0], ["-1"] = pair.Value[1], ["0"] = pair.Value[2], ["1"] = pair.Value[3], ["2"] = pair.Value[4]
                };
            }
            return definitions;
        }

        private static Dictionary<string, string[]> TraitSentenceDescriptors()
        {
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["curiosity"] = Levels("They avoid unfamiliar ideas and resent needless questions.", "They prefer familiar answers and rarely investigate without cause.", "They investigate when practical need or personal interest warrants it.", "They actively seek explanations, novelty, and hidden connections.", "They are driven to uncover what others overlook, even at personal cost."),
                ["ambition"] = Levels("They avoid advancement and prefer a small, secure place.", "They favor security over promotion, conquest, or greater responsibility.", "They accept advancement when the reward and risk seem reasonable.", "They actively seek greater status, influence, or achievement.", "Rising above their present station is a defining hunger."),
                ["honesty"] = Levels("They lie readily and shape truth around advantage.", "They conceal or distort truth when pressure or opportunity invites it.", "They are truthful or deceptive according to circumstance.", "They prefer direct truth and dislike relying on deception.", "Truthfulness defines their self-respect, even when honesty is costly."),
                ["compassion"] = Levels("Other people's suffering rarely restrains their choices.", "They notice suffering but seldom sacrifice much to relieve it.", "They care when suffering is immediate, personal, or clearly undeserved.", "They are moved to protect, comfort, or assist people in distress.", "The suffering of others strongly governs their conscience and choices."),
                ["courage"] = Levels("Fear routinely controls their choices and confrontations.", "They are cautious and need strong reason or support to face danger.", "They show ordinary courage when duty or necessity requires it.", "They face serious danger without easily yielding to fear.", "They are exceptionally bold and may seek danger to prove themselves."),
                ["discipline"] = Levels("They resist routine, abandon plans, and struggle to restrain themselves.", "They are inconsistent and maintain effort only under pressure.", "They can follow plans but bend when fatigue or temptation grows.", "They control their habits and reliably carry difficult plans through.", "Self-command and relentless follow-through define how they live."),
                ["sociability"] = Levels("They avoid company and find most social contact burdensome.", "They are reserved and engage mainly when duty requires it.", "They are comfortable alone or among others depending on circumstance.", "They seek conversation, company, and active social participation.", "They thrive on attention and connection and dislike being socially idle."),
                ["emotionalStability"] = Levels("Strong emotions rapidly overwhelm their judgment and composure.", "Stress readily unsettles them and colors their decisions.", "They are usually composed but can be shaken by serious pressure.", "They remain steady through conflict, insult, uncertainty, and loss.", "Their composure is exceptionally difficult to break, even in crisis."),
                ["pride"] = Levels("They readily humble themselves and place little weight on personal dignity.", "They can swallow slights or low status when doing so is useful.", "They value dignity without making every slight a contest.", "They strongly defend their status, achievements, and self-respect.", "Pride defines them; humiliation or disrespect can override good judgment."),
                ["patience"] = Levels("They cannot tolerate delay and force decisions before they are ready.", "They become restless quickly and prefer immediate movement.", "They wait when needed but dislike delays without visible purpose.", "They can wait, observe, and build advantage before acting.", "They are exceptionally willing to pursue plans across years or generations."),
                ["socialTrust"] = Levels("They assume hidden motives and treat trust as dangerous.", "They are guarded and expect proof before relying on others.", "They grant or withhold trust according to evidence and circumstance.", "They generally believe others until given reason for doubt.", "They trust readily and may remain open even after warning signs."),
                ["flirtatiousness"] = Levels("They reject flirtation and keep attraction tightly private or absent.", "They are restrained and rarely signal romantic or sexual interest.", "They flirt when attraction, safety, and circumstance align.", "They enjoy suggestive attention and readily test mutual attraction.", "Flirtation is a habitual tool of pleasure, connection, or influence."),
                ["authorityRespect"] = Levels("They instinctively resist hierarchy and resent being commanded.", "They question authority and comply only when legitimacy is demonstrated.", "They judge authority by circumstance, competence, and personal interest.", "They value rank, law, and established chains of command.", "Deference to legitimate authority is central to their moral order."),
                ["assertiveness"] = Levels("They yield control and avoid imposing their will on others.", "They prefer accommodation and retreat when stronger wills press them.", "They lead or yield according to status, confidence, and stakes.", "They state demands clearly and try to shape the terms themselves.", "They habitually dominate decisions and resist any attempt to control them."),
                ["tact"] = Levels("They speak with cutting bluntness and little concern for social damage.", "They favor plain speech even when it embarrasses or provokes.", "They balance candor and diplomacy according to the audience.", "They phrase difficult truths carefully and manage social consequences.", "They are exceptionally diplomatic and can conceal conflict beneath polished words."),
                ["loyalty"] = Levels("They abandon people and causes when loyalty becomes inconvenient.", "Their allegiance is conditional and weakens quickly under cost or pressure.", "They remain loyal while bonds, duty, and treatment justify it.", "They stand by chosen people and causes through meaningful hardship.", "Their loyalty is defining and can demand sacrifice beyond reason."),
                ["vengefulness"] = Levels("They release grievances and see revenge as corrosive or pointless.", "They prefer closure and retaliate only to prevent further harm.", "They answer some injuries but can also bargain, forgive, or move on.", "They remember injuries and actively seek proportionate repayment.", "Revenge can dominate their plans until the offender is ruined."),
                ["wealthMotivation"] = Levels("They reject material accumulation and may distrust wealth itself.", "They care little for riches beyond basic security and obligations.", "They value wealth as one useful resource among several.", "They actively seek prosperity, property, and financial leverage.", "Accumulating and protecting wealth is one of their defining aims."),
                ["powerMotivation"] = Levels("They avoid command and do not want control over others.", "They prefer influence without the burdens or exposure of power.", "They pursue power when it serves another goal or prevents vulnerability.", "They actively seek authority, leverage, and control over outcomes.", "Gaining and preserving power is a defining purpose of their life."),
                ["familyMotivation"] = Levels("Family claims hold little weight against personal desire or principle.", "They care for relatives but resist sacrificing major goals for them.", "Family is important but competes normally with duty and self-interest.", "They place family security, honor, and advancement among their highest concerns.", "Family survival and legacy govern nearly every major choice."),
                ["fameMotivation"] = Levels("They avoid renown and prefer useful obscurity.", "They value results more than recognition and dislike public attention.", "They welcome credit without organizing their life around it.", "They actively seek acclaim, reputation, and remembered deeds.", "They hunger to be celebrated and fear being forgotten or overlooked."),
                ["knowledgeMotivation"] = Levels("They distrust learning that does not offer immediate practical value.", "They seek only the knowledge required for present responsibilities.", "They value useful learning and occasional intellectual discovery.", "They actively pursue understanding, expertise, records, and informed counsel.", "The pursuit and possession of knowledge is a defining need."),
                ["religionMotivation"] = Levels("They reject religious authority and resist sacred explanations or duties.", "Faith and ritual have little influence over their decisions.", "They observe or question religion according to culture and circumstance.", "Faith, ritual, or sacred duty meaningfully guides their conduct.", "Religious conviction defines their identity, loyalties, and major choices."),
                ["revengeMotivation"] = Levels("They actively reject revenge as a life goal.", "They would rather recover, reconcile, or forget than pursue retaliation.", "Revenge matters only when a grievance remains serious and unresolved.", "Repaying important injuries is an active personal objective.", "A central purpose of their life is settling a defining grievance."),
                ["dutyMotivation"] = Levels("They reject imposed obligations and place self-interest first.", "They fulfill duties only when enforcement, reward, or affection supports them.", "They balance duty against personal needs and practical consequences.", "They feel strongly responsible for their office, promises, and dependents.", "Duty defines their worth and can demand severe personal sacrifice."),
                ["survivalMotivation"] = Levels("They readily risk death or ruin for values they consider greater.", "They accept serious danger when honor, love, or ambition calls.", "They protect themselves without making survival their highest concern.", "They prioritize security, escape, and continued survival under threat.", "Avoiding death, captivity, or ruin dominates their decisions."),
                ["legacyMotivation"] = Levels("They care little about what remains after their death.", "They focus on present needs rather than heirs or remembrance.", "They consider legacy when age, family, or major choices make it relevant.", "They actively build something meant to outlast them.", "Their heirs, name, works, or realm must endure beyond their lifetime."),
                ["riskTolerance"] = Levels("They avoid uncertainty and choose safety even at major opportunity cost.", "They require strong safeguards before accepting meaningful risk.", "They accept measured risks when expected gains justify them.", "They willingly gamble status, wealth, or safety for important gains.", "They embrace extreme risks and may confuse danger with opportunity."),
                ["impulsiveness"] = Levels("They deliberate extensively and rarely act before considering consequences.", "They usually pause, verify, and plan before committing.", "They can deliberate or act quickly according to pressure.", "They often act on immediate feeling, instinct, or opportunity.", "Impulse routinely outruns foresight and drives consequential choices."),
                ["pragmatism"] = Levels("They cling to ideals or habits even when results suffer.", "They prefer principle and consistency over expedient compromise.", "They balance ideals, custom, and practical results.", "They adapt methods readily to achieve workable outcomes.", "Results dominate their reasoning; almost any method is negotiable."),
                ["traditionalism"] = Levels("They reject inherited customs and actively favor radical change.", "They question tradition and discard it when alternatives seem better.", "They respect some customs while judging others by present needs.", "They favor established customs, roles, and inherited solutions.", "Tradition defines proper order and deviation feels dangerous or corrupt."),
                ["mercy"] = Levels("They are pitiless and see suffering as useful, deserved, or irrelevant.", "They are harsh and prefer punishment over lenience.", "They grant mercy according to circumstance, risk, and deservingness.", "They prefer restraint and spare defeated or vulnerable people when possible.", "Mercy defines their conduct even toward enemies and serious offenders."),
                ["aggression"] = Levels("They avoid force and escalation even when challenged.", "They prefer withdrawal, negotiation, or defensive responses.", "They use aggression when circumstance makes it effective or necessary.", "They readily confront, threaten, or attack to secure their aims.", "Aggression is their default answer to resistance, insult, or opportunity."),
                ["generosity"] = Levels("They hoard resources and exploit need for personal advantage.", "They give reluctantly and usually expect repayment or leverage.", "They share when cost, relationship, and purpose justify it.", "They readily give resources, credit, aid, or forgiveness.", "They are exceptionally giving and may sacrifice more than prudence allows."),
                ["greed"] = Levels("They are repelled by grasping accumulation and resist taking more than needed.", "They are content with enough and rarely press advantage for extra gain.", "They desire gain but recognize ordinary limits and competing values.", "They habitually seek more wealth, property, or reward than necessity requires.", "Acquisition is insatiable; enough rarely feels like enough."),
                ["jealousy"] = Levels("They feel secure in affection and status and rarely become possessive.", "They notice rivals without readily fearing replacement or exclusion.", "They become jealous only when attachment and evidence make the threat credible.", "They are sensitive to rivals, divided affection, and lost attention.", "Jealousy is consuming and can provoke control, sabotage, or obsession."),
                ["empathy"] = Levels("They struggle to recognize or care how others experience events.", "They often misread feelings unless they are stated plainly.", "They understand familiar emotions but miss subtler or alien perspectives.", "They read emotional cues well and account for other people's feelings.", "They absorb and understand others' emotions with exceptional intensity."),
                ["envy"] = Levels("Other people's advantages rarely diminish their own satisfaction.", "They can admire success without often resenting it.", "They compare themselves to others but usually keep envy contained.", "They resent advantages, praise, or possessions they believe they deserve.", "Envy persistently turns others' success into humiliation and grievance."),
                ["optimism"] = Levels("They expect plans to fail and interpret uncertainty as approaching loss.", "They prepare for disappointment and distrust hopeful predictions.", "They see both opportunity and danger without a fixed expectation.", "They generally expect effort and good fortune to improve matters.", "They remain strongly hopeful even when evidence and losses argue otherwise."),
                ["fearfulness"] = Levels("They feel little anticipatory fear and may overlook genuine danger.", "They remain calm around threats and rarely imagine the worst.", "They experience ordinary fear proportionate to apparent danger.", "They anticipate danger readily and seek reassurance, preparation, or escape.", "Fear saturates their expectations and strongly restricts their choices."),
                ["irritability"] = Levels("They are slow to anger and difficult to provoke.", "They tolerate frustration and minor offenses with little reaction.", "Their temper rises according to stress, insult, and circumstance.", "They become annoyed quickly and show impatience under friction.", "Their temper is volatile; small frustrations can trigger serious hostility."),
                ["confidence"] = Levels("They doubt their judgment and expect others to outperform them.", "They hesitate to rely on their own ability without support.", "They trust their ability in familiar situations and question it elsewhere.", "They expect their judgment and skills to withstand serious challenges.", "Their self-belief is exceptional and may persist beyond contrary evidence."),
                ["shame"] = Levels("They feel little personal shame and resist moral or social embarrassment.", "They recover quickly from disgrace and rarely internalize public judgment.", "They feel shame when failure clearly violates important standards.", "Disgrace, exposure, or moral failure weighs heavily on their self-image.", "Shame can dominate them, driving concealment, penance, rage, or self-destruction.")
            };
        }

        private static string[] Levels(string minusTwo, string minusOne, string zero, string plusOne, string plusTwo)
        {
            return new[] { minusTwo, minusOne, zero, plusOne, plusTwo };
        }

        private static void ApplyGeneratedTraitConstruction(Dictionary<string, object> traits, Dictionary<string, object> generated, Dictionary<string, object> hero)
        {
            if (traits == null || generated == null) return;
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>();
            string nativeDescription = FirstNonEmpty(ReadString(hero, "nativeEncyclopediaText", ""), ReadString(hero, "encyclopediaText", ""));
            List<Dictionary<string, object>> applied = new List<Dictionary<string, object>>();

            if (!string.IsNullOrWhiteSpace(nativeDescription))
            {
                foreach (Dictionary<string, object> assignment in ReadDictionaryList(generated, "traitAssignments").Take(12))
                {
                    string key = ReadFirstString(assignment, "trait", "key", "id");
                    string evidence = LimitText(ReadFirstString(assignment, "evidence", "reason"), 300);
                    if (!foundation.ContainsKey(key) || NativeLockedCoreTraits.Contains(key) || string.IsNullOrWhiteSpace(evidence)) continue;
                    int prior = TraitInt(foundation, key);
                    int assigned = ClampTrait(ReadInt(assignment, "value", prior));
                    foundation[key] = assigned;
                    applied.Add(new Dictionary<string, object> { ["trait"] = key, ["prior"] = prior, ["value"] = assigned, ["evidence"] = evidence, ["source"] = "native_description" });
                }
            }

            traits["foundationTraits"] = foundation;
            traits["hiddenReignTraits"] = new Dictionary<string, object>(foundation, StringComparer.OrdinalIgnoreCase);
            traits["assignmentOverrides"] = applied;
            EnsureTraitPercentageData(traits, ReadString(hero, "heroStringId", ReadString(hero, "characterObjectId", "unknown")));
            ApplyGeneratedPersonalityPortrait(traits, generated, ReadString(hero, "name", "This character"));
        }

        private static string BuildRuleBasedPersonalitySummary(string name, Dictionary<string, object> traits)
        {
            Dictionary<string, object> wrapper = new Dictionary<string, object>
            {
                ["foundationTraits"] = traits ?? new Dictionary<string, object>(),
                ["traitPercentages"] = new Dictionary<string, object>()
            };
            return BuildPersonalitySummaryFromPortrait(FirstNonEmpty(name, "This character"), BuildDeterministicPersonalityPortrait(wrapper, FirstNonEmpty(name, "This character"), ""));
        }

        private static bool EnsurePersonalityPortraitData(Dictionary<string, object> traits, string name, bool force)
        {
            if (traits == null || traits.Count == 0) return false;
            string fingerprint = PersonalityTraitFingerprint(traits);
            Dictionary<string, object> current = ReadDictionary(traits, "personalityPortrait") ?? new Dictionary<string, object>();
            bool ready = ReadInt(current, "version", 0) == PersonalityPortraitVersion
                && ReadString(current, "model", "").Equals(PersonalityPortraitModelId, StringComparison.OrdinalIgnoreCase)
                && ReadString(current, "traitFingerprint", "").Equals(fingerprint, StringComparison.OrdinalIgnoreCase)
                && PersonalityPortraitKeys.All(key => !string.IsNullOrWhiteSpace(ReadString(current, key, "")))
                && ReadString(traits, "basePersonalitySummary", "").Length >= PersonalitySummaryMinimumCharacters;
            if (ready && !force) return false;
            RebuildPersonalityPortrait(traits, FirstNonEmpty(name, "This character"), "", "deterministic_trait_foundation");
            return true;
        }

        private static bool PersonalityPortraitReady(Dictionary<string, object> traits)
        {
            if (traits == null) return false;
            Dictionary<string, object> portrait = ReadDictionary(traits, "personalityPortrait") ?? new Dictionary<string, object>();
            string summary = ReadString(traits, "basePersonalitySummary", "");
            return ReadInt(portrait, "version", 0) == PersonalityPortraitVersion
                && ReadString(portrait, "model", "").Equals(PersonalityPortraitModelId, StringComparison.OrdinalIgnoreCase)
                && ReadString(portrait, "traitFingerprint", "").Equals(PersonalityTraitFingerprint(traits), StringComparison.OrdinalIgnoreCase)
                && PersonalityPortraitKeys.All(key => !string.IsNullOrWhiteSpace(ReadString(portrait, key, "")))
                && summary.Length >= PersonalitySummaryMinimumCharacters
                && summary.Length <= PersonalitySummaryMaximumCharacters;
        }

        private static void RebuildPersonalityPortrait(Dictionary<string, object> traits, string name, string extraContradiction, string source)
        {
            Dictionary<string, object> portrait = BuildDeterministicPersonalityPortrait(traits, name, extraContradiction);
            portrait["version"] = PersonalityPortraitVersion;
            portrait["model"] = PersonalityPortraitModelId;
            portrait["source"] = FirstNonEmpty(source, "deterministic_trait_foundation");
            portrait["traitFingerprint"] = PersonalityTraitFingerprint(traits);
            traits["personalityPortrait"] = portrait;
            traits["basePersonalitySummary"] = BuildPersonalitySummaryFromPortrait(name, portrait);
        }

        private static void ApplyGeneratedPersonalityPortrait(Dictionary<string, object> traits, Dictionary<string, object> generated, string name)
        {
            Dictionary<string, object> portrait = BuildDeterministicPersonalityPortrait(traits, name, "");
            Dictionary<string, object> proposed = ReadDictionary(generated, "personalityPortrait") ?? new Dictionary<string, object>();
            int accepted = 0;
            foreach (string key in PersonalityPortraitKeys)
            {
                string value = LimitTextAtSentence(ReadString(proposed, key, ""), 420);
                if (value.Length < 40) continue;
                portrait[key] = value;
                accepted++;
            }
            portrait["version"] = PersonalityPortraitVersion;
            portrait["model"] = PersonalityPortraitModelId;
            portrait["source"] = accepted >= 6 ? "llm_character_construction" : "deterministic_trait_foundation";
            portrait["traitFingerprint"] = PersonalityTraitFingerprint(traits);
            traits["personalityPortrait"] = portrait;

            string proposedSummary = LimitTextAtSentence(ReadString(generated, "basePersonalitySummary", ""), PersonalitySummaryMaximumCharacters);
            traits["basePersonalitySummary"] = proposedSummary.Length >= PersonalitySummaryMinimumCharacters
                ? proposedSummary
                : BuildPersonalitySummaryFromPortrait(name, portrait);
        }

        private static Dictionary<string, object> BuildDeterministicPersonalityPortrait(Dictionary<string, object> document, string name, string extraContradiction)
        {
            Dictionary<string, object> traits = ReadDictionary(document, "foundationTraits") ?? ReadDictionary(document, "hiddenReignTraits") ?? document ?? new Dictionary<string, object>();
            string character = FirstNonEmpty(name, "This character");
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["coreTemperament"] = PersonalityDomainText(document, traits, "courage", "emotionalStability", "optimism"),
                ["socialStyle"] = PersonalityDomainText(document, traits, "sociability", "assertiveness", "tact"),
                ["trustAndAttachment"] = PersonalityDomainText(document, traits, "socialTrust", "loyalty", "empathy"),
                ["powerAndStatus"] = PersonalityDomainText(document, traits, "pride", "ambition", "authorityRespect"),
                ["decisionStyle"] = PersonalityDomainText(document, traits, "discipline", "pragmatism", "riskTolerance"),
                ["emotionalPattern"] = PersonalityDomainText(document, traits, "irritability", "fearfulness", "shame"),
                ["moralBoundaries"] = PersonalityDomainText(document, traits, "honesty", "compassion", "mercy"),
                ["underPressure"] = BuildPressureBehavior(document),
                ["internalContradictions"] = string.Join(" ", new[] { BuildPersonalityContradiction(character, document), extraContradiction }
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray())
            };
            return result;
        }

        private static string PersonalityDomainText(Dictionary<string, object> document, Dictionary<string, object> traits, params string[] keys)
        {
            List<string> selected = (keys ?? new string[0])
                .Where(key => traits.ContainsKey(key))
                .OrderByDescending(key => Math.Abs(TraitInt(traits, key)))
                .ThenByDescending(key => PersonalityTraitPercentage(document, key))
                .Take(2)
                .Select(key => TraitValueDescriptor(key, TraitInt(traits, key)))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            return selected.Count == 0 ? "Their behavior in this area remains balanced and depends heavily on circumstance." : string.Join(" ", selected);
        }

        private static string BuildPressureBehavior(Dictionary<string, object> traits)
        {
            int discipline = PersonalityTraitPercentage(traits, "discipline");
            int stability = PersonalityTraitPercentage(traits, "emotionalStability");
            int aggression = PersonalityTraitPercentage(traits, "aggression");
            int fear = PersonalityTraitPercentage(traits, "fearfulness");
            if (discipline >= 61 && stability >= 61) return "Under pressure they narrow their attention, contain visible emotion, and try to impose an orderly plan before acting.";
            if (aggression >= 61 && fear <= 40) return "Under pressure they are inclined to confront the source directly, accepting escalation more readily than retreat or delay.";
            if (fear >= 61) return "Under pressure they scan for danger, seek preparation or escape, and may treat uncertain motives as immediate threats.";
            if (PersonalityTraitPercentage(traits, "impulsiveness") >= 61 || PersonalityTraitPercentage(traits, "irritability") >= 61) return "Under pressure their restraint shortens; immediate feelings and provocations gain more influence over what they do next.";
            return "Under pressure they weigh the immediate danger against duty and self-interest, with circumstance deciding whether caution or resolve wins.";
        }

        private static string BuildPersonalityContradiction(string name, Dictionary<string, object> traits)
        {
            int compassion = PersonalityTraitPercentage(traits, "compassion"), vengeance = Math.Max(PersonalityTraitPercentage(traits, "vengefulness"), PersonalityTraitPercentage(traits, "revengeMotivation"));
            if (compassion >= 61 && vengeance >= 61) return "They can sincerely care about suffering while still treating a remembered wrong as a debt that must eventually be paid.";
            int loyalty = PersonalityTraitPercentage(traits, "loyalty"), ambition = Math.Max(PersonalityTraitPercentage(traits, "ambition"), PersonalityTraitPercentage(traits, "powerMotivation"));
            if (loyalty >= 61 && ambition >= 61) return "They value loyalty, yet advancement can tempt them to reinterpret whom or what their loyalty is truly meant to serve.";
            int pride = PersonalityTraitPercentage(traits, "pride"), tact = PersonalityTraitPercentage(traits, "tact");
            if (pride >= 61 && tact <= 40) return "Their need to preserve dignity can overpower diplomacy, especially when patience would feel like submission.";
            int courage = PersonalityTraitPercentage(traits, "courage"), survival = PersonalityTraitPercentage(traits, "survivalMotivation");
            if (courage >= 61 && survival >= 61) return "They are capable of real bravery, but they also calculate what must be preserved, creating tension between bold action and strategic retreat.";
            int honesty = PersonalityTraitPercentage(traits, "honesty"), pragmatism = PersonalityTraitPercentage(traits, "pragmatism");
            if (honesty >= 61 && pragmatism >= 61) return "They prefer truth, but practical necessity can make them search for the most useful version of what honesty requires.";
            return "Their competing needs do not form a simple virtue or flaw; rank, danger, affection, and humiliation can pull different parts of the same personality to the surface.";
        }

        private static string BuildPersonalitySummaryFromPortrait(string name, Dictionary<string, object> portrait)
        {
            string character = FirstNonEmpty(name, "This character");
            List<string> parts = new List<string>
            {
                character + " has a rounded temperament rather than a single governing trait.",
                ReadString(portrait, "coreTemperament", ""), ReadString(portrait, "socialStyle", ""),
                ReadString(portrait, "trustAndAttachment", ""), ReadString(portrait, "powerAndStatus", ""),
                ReadString(portrait, "decisionStyle", ""), ReadString(portrait, "emotionalPattern", ""),
                ReadString(portrait, "moralBoundaries", ""), ReadString(portrait, "underPressure", ""),
                ReadString(portrait, "internalContradictions", "")
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            string summary = string.Join(" ", parts);
            return LimitTextAtSentence(summary, PersonalitySummaryMaximumCharacters);
        }

        private static string LimitTextAtSentence(string text, int maximum)
        {
            text = (text ?? "").Trim();
            if (maximum <= 0 || text.Length <= maximum) return text;
            int period = text.LastIndexOfAny(new[] { '.', '!', '?' }, Math.Max(0, maximum - 1));
            if (period >= Math.Min(200, maximum / 2)) return text.Substring(0, period + 1).Trim();
            return text.Substring(0, maximum).TrimEnd();
        }

        private static int PersonalityTraitPercentage(Dictionary<string, object> traits, string key)
        {
            Dictionary<string, object> percentages = ReadDictionary(traits, "traitPercentages") ?? new Dictionary<string, object>();
            if (percentages.ContainsKey(key)) return Math.Max(0, Math.Min(100, ReadInt(percentages, key, 50)));
            int modifier = TraitInt(ReadDictionary(traits, "foundationTraits") ?? traits, key);
            TraitPercentageBand(modifier, out int minimum, out int maximum);
            return (minimum + maximum) / 2;
        }

        private static string PersonalityTraitFingerprint(Dictionary<string, object> traits)
        {
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? ReadDictionary(traits, "hiddenReignTraits") ?? new Dictionary<string, object>();
            Dictionary<string, object> percentages = ReadDictionary(traits, "traitPercentages") ?? new Dictionary<string, object>();
            string material = string.Join("|", CoreTraitKeys.Select(key => key + ":" + TraitInt(foundation, key).ToString(CultureInfo.InvariantCulture) + ":" + ReadInt(percentages, key, -1).ToString(CultureInfo.InvariantCulture)));
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in material) { hash ^= c; hash *= 16777619; }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static string BuildRelevantTraitEmphasis(Dictionary<string, object> characteristics, string playerText, string sceneContext)
        {
            Dictionary<string, object> traitDocument = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> traits = ReadDictionary(traitDocument, "foundationTraits") ?? new Dictionary<string, object>();
            if (traits.Count == 0) return string.Empty;
            List<Dictionary<string, object>> domains = SelectMotiveDomains(playerText, sceneContext,
                new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>());
            List<string> selected = domains
                .Select(item => MotiveDomainDefinitions.FirstOrDefault(domain => domain.Id == ReadString(item, "id", "")))
                .Where(domain => domain != null)
                .SelectMany(domain => domain.TraitKeys)
                .Where(key => traits.ContainsKey(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(key => Math.Abs(TraitInt(traits, key)))
                .ThenBy(key => Array.IndexOf(CoreTraitKeys, key))
                .Take(5)
                .ToList();
            if (selected.Count == 0) return string.Empty;

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("RELEVANT PERSONALITY EMPHASIS");
            builder.AppendLine("Deterministic motive routing selected these traits as relevant to this exchange. Give them extra weight without replacing the complete base personality.");
            foreach (string key in selected)
                builder.AppendLine("- " + HumanizeKey(key) + ": " + TraitValueDescriptor(key, TraitInt(traits, key)));
            return builder.ToString().TrimEnd();
        }
    }
}
