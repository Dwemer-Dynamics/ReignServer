using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int CourtCharacterModelVersion = 1;
        private const string CourtCharacterModelId = "reign_court_character_v1";

        private sealed class CourtCharacterDefinition
        {
            public string Sex;
            public int Boldness;
            public int Honor;
            public string Title;
            public string Description;
            public string[] Actions;
            public string[] Tactics;
        }

        private static CourtCharacterDefinition CourtCharacterCell(
            string sex, int boldness, int honor, string title, string description,
            string actions, params string[] tactics)
        {
            return new CourtCharacterDefinition
            {
                Sex = sex,
                Boldness = boldness,
                Honor = honor,
                Title = title,
                Description = description,
                Actions = (actions ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
                Tactics = (tactics ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        private static readonly Dictionary<string, CourtCharacterDefinition> CourtCharacterMatrix =
            BuildCourtCharacterMatrix();

        private static Dictionary<string, CourtCharacterDefinition> BuildCourtCharacterMatrix()
        {
            List<CourtCharacterDefinition> cells = new List<CourtCharacterDefinition>
            {
                // Female noble matrix.
                CourtCharacterCell("female",-2,-2,"Hidden Schemer","Imagines manipulation but rarely creates danger herself. Exploits accidental privacy, repeats anonymous gossip, accepts secret advances, or quietly benefits from scandal.","Exploit accidental privacy|Repeat anonymous gossip|Accept secret advances|Quietly benefit from scandal","schemes","rumors","secrets","flirtation"),
                CourtCharacterCell("female",-2,-1,"Quiet Opportunist","Takes advantages that appear safely. Encourages misunderstandings, withholds useful information, accepts questionable favors, or abandons someone becoming unpopular.","Encourage misunderstandings|Withhold useful information|Accept questionable favors|Abandon someone becoming unpopular","rumors","information","favors","bargaining"),
                CourtCharacterCell("female",-2,0,"Silent Adapter","Follows the safest social current. Watches disputes, agrees privately with whoever holds power, avoids commitments, and waits for the outcome.","Watch disputes|Agree privately with whoever holds power|Avoid commitments|Wait for the outcome","information","bargaining"),
                CourtCharacterCell("female",-2,1,"Reserved Gentlewoman","Disapproves of cruelty but fears involvement. Refuses improper requests, offers private sympathy, quietly warns someone, or withdraws from the offender.","Refuse improper requests|Offer private sympathy|Quietly warn someone|Withdraw from the offender","warnings","protection","mediation"),
                CourtCharacterCell("female",-2,2,"Silent Conscience","Strongly recognizes injustice but struggles to act. Secretly aids the victim, preserves evidence, prays someone intervenes, or suffers guilt for remaining silent.","Secretly aid the victim|Preserve evidence|Seek intervention|Remain silent despite guilt","protection","warnings","secrets"),

                CourtCharacterCell("female",-1,-2,"Deniable Intriguer","Manipulates through indirect and concealable methods. Uses attendants, private letters, subtle flirtation, coded gossip, or carefully arranged introductions.","Use attendants|Send private letters|Use subtle flirtation|Spread coded gossip|Arrange introductions","schemes","secrets","flirtation","rumors","information"),
                CourtCharacterCell("female",-1,-1,"Subtle Climber","Exploits weakness without exposing herself. Uses jealousy, selective praise, social exclusion, private pressure, or another person's attraction.","Use jealousy|Offer selective praise|Apply social exclusion|Apply private pressure|Exploit another person's attraction","schemes","bargaining","threats","strategic_seduction"),
                CourtCharacterCell("female",-1,0,"Careful Broker","Searches for the safest useful outcome. Carries private messages, negotiates compromises, delays commitments, and supports whichever solution appears stable.","Carry private messages|Negotiate compromises|Delay commitments|Support the stable solution","information","mediation","bargaining"),
                CourtCharacterCell("female",-1,1,"Discreet Defender","Helps without making herself the center of conflict. Gives private warnings, corrects rumors quietly, shelters someone temporarily, or appeals to a trusted authority.","Give private warnings|Correct rumors quietly|Shelter someone temporarily|Appeal to a trusted authority","warnings","rumors","protection","mediation"),
                CourtCharacterCell("female",-1,2,"Quiet Guardian","Protects dignity through controlled action. Confronts offenders privately, gathers proof, finds respectable allies, or arranges safe removal from danger.","Confront offenders privately|Gather proof|Find respectable allies|Arrange safe removal from danger","protection","warnings","information","mediation"),

                CourtCharacterCell("female",0,-2,"Calculated Manipulator","Plots when reward exceeds risk. Uses targeted seduction, planted rumors, emotional leverage, compromising correspondence, or carefully chosen secrets.","Use targeted seduction|Plant rumors|Apply emotional leverage|Create compromising correspondence|Use carefully chosen secrets","strategic_seduction","rumors","secrets","blackmail","schemes"),
                CourtCharacterCell("female",0,-1,"Transactional Courtier","Uses questionable methods selectively. Trades information, applies social pressure, exploits attraction, bargains with support, or allows useful rumors to spread.","Trade information|Apply social pressure|Exploit attraction|Bargain with support|Allow useful rumors to spread","information","threats","strategic_seduction","bargaining","rumors"),
                CourtCharacterCell("female",0,0,"Practical Noblewoman","Judges each situation by relationships and consequences. Mediates, bargains, remains neutral, backs the stronger case, or protects her household's interests.","Mediate|Bargain|Remain neutral|Back the stronger case|Protect household interests","mediation","bargaining","protection"),
                CourtCharacterCell("female",0,1,"Fair Mediator","Supports truthful and proportionate outcomes. Challenges false gossip, hears both sides, protects confidences, mediates disputes, and offers respectable solutions.","Challenge false gossip|Hear both sides|Protect confidences|Mediate disputes|Offer respectable solutions","rumors","secrets","mediation"),
                CourtCharacterCell("female",0,2,"Principled Advocate","Actively supports justice while remaining controlled. Defends the wronged, exposes deliberate lies, demands proper treatment, and accepts moderate reputational risk.","Defend the wronged|Expose deliberate lies|Demand proper treatment|Accept moderate reputational risk","protection","information","warnings"),

                CourtCharacterCell("female",1,-2,"Active Intriguer","Creates opportunities instead of waiting. Arranges private encounters, recruits confidantes, spreads coordinated rumors, isolates rivals, or deliberately encourages dangerous attraction.","Arrange private encounters|Recruit confidantes|Spread coordinated rumors|Isolate rivals|Encourage dangerous attraction","schemes","secrets","rumors","rivalry","strategic_seduction"),
                CourtCharacterCell("female",1,-1,"Ambitious Operator","Pushes aggressively for advantage without embracing total treachery. Builds factions, pressures rivals, weaponizes invitations, tests romantic interest, and controls access to influential people.","Build factions|Pressure rivals|Weaponize invitations|Test romantic interest|Control access to influential people","schemes","threats","rivalry","flirtation","bargaining"),
                CourtCharacterCell("female",1,0,"Decisive Broker","Quickly chooses a practical position and shapes the court around it. Organizes meetings, pressures compromise, rallies supporters, and forces delayed issues toward resolution.","Organize meetings|Pressure compromise|Rally supporters|Force delayed issues toward resolution","mediation","bargaining","threats"),
                CourtCharacterCell("female",1,1,"Public Defender","Openly challenges unfair conduct. Rebukes slander, supports victims before witnesses, confronts coercion, gathers allies, and places her reputation behind her claims.","Rebuke slander|Support victims before witnesses|Confront coercion|Gather allies|Stake reputation on the claim","protection","rumors","warnings"),
                CourtCharacterCell("female",1,2,"Fearless Champion","Makes protection and justice part of her public identity. Takes vulnerable people under her protection, exposes powerful offenders, and refuses to abandon a righteous cause.","Protect vulnerable people|Expose powerful offenders|Refuse to abandon a righteous cause","protection","information","warnings"),

                CourtCharacterCell("female",2,-2,"Brazen Conspirator","Personally engineers extreme schemes. Enters a target's room, attempts romantic or pregnancy entrapment, fabricates intimacy, creates public scandal, blackmails rivals, or risks everything on deception.","Attempt a forbidden private encounter|Attempt romantic leverage|Attempt pregnancy leverage|Attempt to fabricate intimacy|Create public scandal|Blackmail rivals","schemes","strategic_seduction","pregnancy_leverage","rumors","blackmail"),
                CourtCharacterCell("female",2,-1,"Audacious Climber","Pursues status with little fear of scandal. Aggressively seduces, forces social choices, humiliates rivals, makes bold accusations, and uses family or romantic pressure openly.","Use aggressive seduction|Force social choices|Humiliate rivals|Make bold accusations|Use family or romantic pressure openly","strategic_seduction","threats","rivalry","rumors","schemes"),
                CourtCharacterCell("female",2,0,"Force of Will","Acts immediately according to her present judgment. Seizes control of disputes, makes sudden alliances, confronts rivals, and accepts major risks for practical outcomes.","Seize control of disputes|Make sudden alliances|Confront rivals|Accept major risks for practical outcomes","mediation","bargaining","rivalry"),
                CourtCharacterCell("female",2,1,"Fearless Reformer","Publicly opposes abuse even when powerful figures are involved. Interrupts injustice, names offenders, shelters victims, demands accountability, and refuses intimidation.","Interrupt injustice|Name offenders|Shelter victims|Demand accountability|Refuse intimidation","protection","warnings","information"),
                CourtCharacterCell("female",2,2,"Unyielding Protector","Risks status, marriage prospects, family favor, or personal safety for principle. Publicly champions the wronged and pursues justice until the matter is resolved.","Risk status for principle|Risk marriage prospects or family favor|Publicly champion the wronged|Pursue justice until resolution","protection","warnings","mediation"),

                // Male noble matrix.
                CourtCharacterCell("male",-2,-2,"Hidden Schemer","Desires revenge and advantage but fears discovery. Exploits unguarded secrets, repeats anonymous rumors, accepts invitations into existing plots, or betrays only when nearly safe.","Exploit unguarded secrets|Repeat anonymous rumors|Join existing plots|Betray only when nearly safe","secrets","rumors","schemes"),
                CourtCharacterCell("male",-2,-1,"Passive Opportunist","Benefits from wrongdoing without leading it. Accepts unfair favors, stays silent during abuse, abandons endangered allies, or keeps information that could help a rival.","Accept unfair favors|Stay silent during abuse|Abandon endangered allies|Withhold information from a rival","favors","information","bargaining"),
                CourtCharacterCell("male",-2,0,"Silent Follower","Avoids choosing sides until the outcome is clear. Watches, agrees privately, delays answers, and follows whoever appears safest.","Watch|Agree privately|Delay answers|Follow whoever appears safest","information","bargaining"),
                CourtCharacterCell("male",-2,1,"Reluctant Gentleman","Will not willingly commit dishonorable acts but rarely confronts them. Quietly refuses, privately apologizes to the victim, or removes himself from the situation.","Quietly refuse|Privately apologize to the victim|Leave the situation","warnings","mediation"),
                CourtCharacterCell("male",-2,2,"Silent Conscience","Feels intense outrage but lacks the nerve to intervene. Secretly warns the victim, refuses later involvement, preserves evidence, or regrets failing to act.","Secretly warn the victim|Refuse later involvement|Preserve evidence|Remain inactive despite regret","warnings","protection","secrets"),

                CourtCharacterCell("male",-1,-2,"Deniable Plotter","Works through others and preserves distance. Uses servants, agents, private letters, veiled threats, anonymous accusations, or carefully leaked secrets.","Use servants or agents|Send private letters|Make veiled threats|Make anonymous accusations|Leak secrets carefully","schemes","threats","rumors","secrets"),
                CourtCharacterCell("male",-1,-1,"Cautious Climber","Exploits weakness while avoiding open blame. Trades favors, flatters superiors, applies private pressure, quietly supports rumors, or changes allegiance when danger rises.","Trade favors|Flatter superiors|Apply private pressure|Quietly support rumors|Change allegiance when danger rises","favors","bargaining","threats","rumors"),
                CourtCharacterCell("male",-1,0,"Careful Broker","Searches for a safe and workable solution. Negotiates privately, delays commitments, carries messages, and backs compromise over confrontation.","Negotiate privately|Delay commitments|Carry messages|Back compromise over confrontation","mediation","information","bargaining"),
                CourtCharacterCell("male",-1,1,"Private Dissenter","Opposes wrongdoing away from the crowd. Warns the offender, advises the victim, refuses cooperation, or appeals quietly to someone with greater authority.","Warn the offender|Advise the victim|Refuse cooperation|Appeal quietly to higher authority","warnings","protection","mediation"),
                CourtCharacterCell("male",-1,2,"Restrained Guardian","Protects others through respectable channels. Invokes law or custom, gathers witnesses, privately demands correction, or arranges protection without public spectacle.","Invoke law or custom|Gather witnesses|Privately demand correction|Arrange discreet protection","protection","warnings","information"),

                CourtCharacterCell("male",0,-2,"Calculated Intriguer","Plots when the expected gain justifies the danger. Uses blackmail, targeted rumors, false friendship, romantic deception, controlled leaks, or compromising arrangements.","Use blackmail|Spread targeted rumors|Offer false friendship|Use romantic deception|Arrange controlled leaks|Create compromising arrangements","blackmail","rumors","schemes","strategic_seduction","secrets"),
                CourtCharacterCell("male",0,-1,"Transactional Lord","Uses manipulation selectively. Trades influence, conceals intentions, pressures dependents, exploits mistakes, and supports whatever alliance advances him.","Trade influence|Conceal intentions|Pressure dependents|Exploit mistakes|Support an advantageous alliance","bargaining","secrets","threats","schemes"),
                CourtCharacterCell("male",0,0,"Practical Noble","Judges situations by consequence rather than principle. Mediates, bargains, supports stability, remains neutral, or changes position when circumstances change.","Mediate|Bargain|Support stability|Remain neutral|Change position with circumstances","mediation","bargaining"),
                CourtCharacterCell("male",0,1,"Fair Arbiter","Seeks truthful and proportionate outcomes. Hears both sides, challenges false claims, keeps promises, mediates disputes, and uses influence openly.","Hear both sides|Challenge false claims|Keep promises|Mediate disputes|Use influence openly","mediation","information"),
                CourtCharacterCell("male",0,2,"Principled Champion","Intervenes when serious injustice becomes clear. Publicly supports the wronged, confronts misconduct, gathers evidence, and accepts reasonable personal risk.","Publicly support the wronged|Confront misconduct|Gather evidence|Accept reasonable personal risk","protection","warnings","information"),

                CourtCharacterCell("male",1,-2,"Active Conspirator","Creates weaknesses and opportunities. Recruits agents, plants rumors, manufactures accusations, arranges compromising encounters, threatens rivals, or coordinates betrayal.","Recruit agents|Plant rumors|Attempt to manufacture accusations|Arrange compromising encounters|Threaten rivals|Coordinate betrayal","schemes","rumors","blackmail","threats","rivalry"),
                CourtCharacterCell("male",1,-1,"Forceful Climber","Aggressively pursues status and leverage. Pressures allies, exploits attraction, controls patronage, intimidates weaker rivals, and bends agreements in his favor.","Pressure allies|Exploit attraction|Control patronage|Intimidate weaker rivals|Bend agreements in his favor","threats","strategic_seduction","favors","rivalry","bargaining"),
                CourtCharacterCell("male",1,0,"Power Broker","Chooses a useful outcome and actively imposes momentum. Calls meetings, rallies allies, forces decisions, negotiates hard, and confronts obstruction.","Call meetings|Rally allies|Force decisions|Negotiate hard|Confront obstruction","mediation","bargaining","rivalry"),
                CourtCharacterCell("male",1,1,"Vocal Defender","Publicly opposes unfair behavior. Challenges offenders, defends reputations, offers protection, gathers supporters, and demands an honest hearing.","Challenge offenders|Defend reputations|Offer protection|Gather supporters|Demand an honest hearing","protection","warnings","mediation"),
                CourtCharacterCell("male",1,2,"Gallant Champion","Makes honorable intervention central to his court identity. Places himself between victim and offender, challenges powerful men, and risks influence to defend others.","Stand between victim and offender|Challenge powerful offenders|Risk influence to defend others","protection","warnings"),

                CourtCharacterCell("male",2,-2,"Brazen Conspirator","Personally undertakes extreme deception. Engineers scandals, enters forbidden meetings, seduces for leverage, fabricates evidence, blackmails openly, or pursues revenge despite enormous danger.","Engineer scandals|Enter forbidden meetings|Seduce for leverage|Attempt to fabricate evidence|Blackmail openly|Pursue dangerous revenge","schemes","rumors","strategic_seduction","blackmail","rivalry"),
                CourtCharacterCell("male",2,-1,"Ruthless Opportunist","Seizes every opening with little restraint. Makes aggressive threats, publicly humiliates rivals, forces bargains, betrays weak allies, and pursues forbidden relationships for gain.","Make aggressive threats|Publicly humiliate rivals|Force bargains|Betray weak allies|Pursue forbidden relationships for gain","threats","rivalry","bargaining","strategic_seduction","schemes"),
                CourtCharacterCell("male",2,0,"Impulsive Power","Acts immediately according to present judgment. Seizes control, confronts rivals, makes sudden alliances, escalates disputes, and accepts severe political risk.","Seize control|Confront rivals|Make sudden alliances|Escalate disputes|Accept severe political risk","bargaining","rivalry","threats"),
                CourtCharacterCell("male",2,1,"Fearless Defender","Immediately confronts abuse and refuses intimidation. Publicly names wrongdoing, protects the target, challenges powerful offenders, and continues until correction is made.","Publicly name wrongdoing|Protect the target|Challenge powerful offenders|Continue until correction","protection","warnings","information"),
                CourtCharacterCell("male",2,2,"Uncompromising Paladin","Treats injustice as a personal challenge. Risks title, wealth, alliances, reputation, or life to defend the wronged and refuses to abandon the cause.","Risk title, wealth, alliances, reputation, or life|Defend the wronged|Refuse to abandon the cause","protection","warnings")
            };

            return cells.ToDictionary(CourtCharacterKey, x => x, StringComparer.OrdinalIgnoreCase);
        }

        private static string CourtCharacterKey(CourtCharacterDefinition cell)
        {
            return CourtCharacterKey(cell.Sex, cell.Boldness, cell.Honor);
        }

        private static string CourtCharacterKey(string sex, int boldness, int honor)
        {
            return (sex ?? "male").ToLowerInvariant() + "|" + boldness + "|" + honor;
        }

        private static int CourtCharacterBand(int score)
        {
            score = Clamp(score, 0, 100);
            if (score <= 20) return -2;
            if (score <= 40) return -1;
            if (score <= 60) return 0;
            if (score <= 80) return 1;
            return 2;
        }

        private static string CourtCharacterHonorLabel(int level)
        {
            switch (level)
            {
                case -2: return "Devious";
                case -1: return "Opportunistic";
                case 0: return "Pragmatic";
                case 1: return "Honorable";
                default: return "Chivalric";
            }
        }

        private static string CourtCharacterBoldnessLabel(int level)
        {
            switch (level)
            {
                case -2: return "Timid";
                case -1: return "Cautious";
                case 0: return "Measured";
                case 1: return "Assertive";
                default: return "Audacious";
            }
        }

        private static double CourtCharacterBoldnessFactor(int level)
        {
            switch (Math.Max(-2, Math.Min(2, level)))
            {
                case -2: return 0d;
                case -1: return 0.25d;
                case 0: return 0.50d;
                case 1: return 0.75d;
                default: return 1d;
            }
        }

        private static Dictionary<string, object> CourtCharacterModelDefinition()
        {
            return new Dictionary<string, object>
            {
                ["version"] = CourtCharacterModelVersion,
                ["id"] = CourtCharacterModelId,
                ["source"] = "fixed sex-specific Honor-Boldness matrix",
                ["scoreBands"] = TraitPercentageBands(),
                ["fixedCellRule"] = "Honor selects one column and Boldness selects one row. Scene conditions never shift either coordinate.",
                ["adultRule"] = "Every adult NPC receives one cell; children are unavailable.",
                ["opportunityRule"] = "Known public identity and visible presentation may strengthen relevant tactics but never change the cell.",
                ["tacticRule"] = "Listed actions are strongly preferred tactics constrained to registered Reign systems and verified outcomes."
            };
        }

        private static Dictionary<string, object> BuildCourtCharacterData(
            Dictionary<string, object> traits, Dictionary<string, object> profile)
        {
            traits = traits ?? new Dictionary<string, object>();
            profile = profile ?? new Dictionary<string, object>();
            Dictionary<string, object> virtues = ReadDictionary(traits, "courtVirtues") ?? new Dictionary<string, object>();
            bool isFemale = ReadBool(profile, "isFemale", ReadBool(ReadDictionary(profile, "sourceFacts"), "isFemale", false));
            double age = ReadDouble(profile, "age", ReadDouble(ReadDictionary(profile, "sourceFacts"), "age", 0d));
            bool isChild = ReadBool(profile, "isChild", ReadBool(ReadDictionary(profile, "sourceFacts"), "isChild", false))
                || (age > 0d && age < 18d);
            if (isChild)
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false,
                    ["reason"] = "child",
                    ["modelVersion"] = CourtCharacterModelVersion
                };
            }

            if (!virtues.ContainsKey("honor") || !virtues.ContainsKey("boldness"))
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false,
                    ["reason"] = "court_virtues_unavailable",
                    ["modelVersion"] = CourtCharacterModelVersion
                };
            }

            int honorScore = Clamp(ReadInt(virtues, "honor", 50), 0, 100);
            int boldnessScore = Clamp(ReadInt(virtues, "boldness", 50), 0, 100);
            int honorLevel = CourtCharacterBand(honorScore);
            int boldnessLevel = CourtCharacterBand(boldnessScore);
            string sex = isFemale ? "female" : "male";
            CourtCharacterDefinition cell = CourtCharacterMatrix[CourtCharacterKey(sex, boldnessLevel, honorLevel)];
            return new Dictionary<string, object>
            {
                ["available"] = true,
                ["modelVersion"] = CourtCharacterModelVersion,
                ["cellId"] = sex + "_b" + (boldnessLevel >= 0 ? "p" : "n") + Math.Abs(boldnessLevel)
                    + "_h" + (honorLevel >= 0 ? "p" : "n") + Math.Abs(honorLevel),
                ["sexMatrix"] = sex,
                ["title"] = cell.Title,
                ["description"] = cell.Description,
                ["honorScore"] = honorScore,
                ["honorLevel"] = honorLevel,
                ["honorLabel"] = CourtCharacterHonorLabel(honorLevel),
                ["boldnessScore"] = boldnessScore,
                ["boldnessLevel"] = boldnessLevel,
                ["boldnessLabel"] = CourtCharacterBoldnessLabel(boldnessLevel),
                ["preferredActions"] = cell.Actions.ToList(),
                ["tacticIds"] = cell.Tactics.ToList(),
                ["supportsStrategicSeduction"] = cell.Tactics.Contains("strategic_seduction", StringComparer.OrdinalIgnoreCase),
                ["supportsPregnancyLeverage"] = cell.Tactics.Contains("pregnancy_leverage", StringComparer.OrdinalIgnoreCase),
                ["immutableCoordinates"] = true
            };
        }

        private static bool EnsureCourtCharacterData(
            Dictionary<string, object> traits, Dictionary<string, object> profile, string heroId)
        {
            if (traits == null || traits.Count == 0) return false;
            Dictionary<string, object> calculated = BuildCourtCharacterData(traits, profile);
            Dictionary<string, object> existing = ReadDictionary(traits, "courtCharacter") ?? new Dictionary<string, object>();
            Dictionary<string, object> existingModel = ReadDictionary(traits, "courtCharacterModel") ?? new Dictionary<string, object>();
            bool changed = ReadInt(existingModel, "version", 0) != CourtCharacterModelVersion
                || !ReadString(existingModel, "id", "").Equals(CourtCharacterModelId, StringComparison.OrdinalIgnoreCase)
                || !CanonicalJson(existing).Equals(CanonicalJson(calculated), StringComparison.Ordinal);
            traits["courtCharacter"] = calculated;
            traits["courtCharacterModel"] = CourtCharacterModelDefinition();
            return changed;
        }

        private static double CourtCharacterGainStrength(Dictionary<string, object> opportunity)
        {
            opportunity = opportunity ?? new Dictionary<string, object>();
            double relative = ClampDouble((ReadDouble(opportunity, "relativeBenefit", 50d) - 50d) / 50d, 0d, 1d);
            double bestAxis = ClampDouble(
                ReadDouble(opportunity, "bestOpportunityGain", 0d), 0d, 1d);
            double tier = ReadBool(opportunity, "targetClanTierKnown", false)
                ? ClampDouble(Math.Max(0d, ReadInt(opportunity, "clanTierDelta", 0)) / 2d, 0d, 1d)
                : 0d;
            return Math.Max(bestAxis, Math.Max(relative, tier));
        }

        private static double CourtCharacterManipulationPropensity(int honorLevel)
        {
            switch (Math.Max(-2, Math.Min(2, honorLevel)))
            {
                case -2: return 1d;
                case -1: return 0.72d;
                case 0: return 0.25d;
                case 1: return 0.08d;
                default: return 0d;
            }
        }

        private static Dictionary<string, object> CourtCharacterDecisionSnapshot(
            Dictionary<string, object> characteristics, Dictionary<string, object> opportunity)
        {
            Dictionary<string, object> traits = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> courtCharacter = ReadDictionary(traits, "courtCharacter") ?? new Dictionary<string, object>();
            double gainStrength = CourtCharacterGainStrength(opportunity);
            int boldnessLevel = ReadInt(courtCharacter, "boldnessLevel", 0);
            int honorLevel = ReadInt(courtCharacter, "honorLevel", 0);
            bool available = ReadBool(courtCharacter, "available", false);
            bool seduction = ReadBool(courtCharacter, "supportsStrategicSeduction", false);
            double manipulationPropensity = available
                ? CourtCharacterManipulationPropensity(honorLevel)
                : 0d;
            bool supportsManipulation = available && honorLevel < 0
                && ReadStringList(courtCharacter, "tacticIds").Count > 0;
            double strategicBoost = available && seduction
                ? Math.Round(20d * gainStrength * CourtCharacterBoldnessFactor(boldnessLevel), 1)
                : 0d;
            return new Dictionary<string, object>
            {
                ["available"] = available,
                ["cellId"] = ReadString(courtCharacter, "cellId", ""),
                ["title"] = ReadString(courtCharacter, "title", ""),
                ["description"] = ReadString(courtCharacter, "description", ""),
                ["honorLevel"] = honorLevel,
                ["honorLabel"] = ReadString(courtCharacter, "honorLabel", ""),
                ["boldnessLevel"] = boldnessLevel,
                ["boldnessLabel"] = ReadString(courtCharacter, "boldnessLabel", ""),
                ["preferredActions"] = ReadStringList(courtCharacter, "preferredActions"),
                ["tacticIds"] = ReadStringList(courtCharacter, "tacticIds"),
                ["gainStrength"] = Math.Round(gainStrength, 3),
                ["matchingTacticWeight"] = gainStrength > 0d ? 3d : 1d,
                ["manipulationPropensity"] =
                    Math.Round(manipulationPropensity, 3),
                ["manipulationFit"] =
                    Math.Round(manipulationPropensity * gainStrength, 3),
                ["supportsManipulation"] = supportsManipulation,
                ["strategicBoost"] = strategicBoost,
                ["initiativeBoost"] = strategicBoost,
                ["supportsStrategicSeduction"] = seduction,
                ["supportsPregnancyLeverage"] = ReadBool(courtCharacter, "supportsPregnancyLeverage", false),
                ["fixedCoordinates"] = true
            };
        }

        private static Dictionary<string, object> LoadCourtCharacterScheme(
            string campaignId, string subjectId, string targetId, double worldDay)
        {
#if REIGN_EXCLUDE_COURT
            return new Dictionary<string, object>();
#else
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(subjectId)
                || string.IsNullOrWhiteSpace(targetId)) return new Dictionary<string, object>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureCourtSchema(connection);
                Dictionary<string, object> row = QuerySql(connection,
                    "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id='main' AND director_id=$subject AND target_id=$target AND status='active' ORDER BY updated_ts DESC LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
                if (row == null) return new Dictionary<string, object>();
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", ""))
                    ?? new Dictionary<string, object>();
                string tactic = ReadString(payload, "tacticId", "");
                string stage = ReadString(payload, "stage", "adopted");
                if (tactic == "pregnancy_leverage" && stage != "leverage_available")
                {
                    Dictionary<string, object> conception = QuerySql(connection,
                        "SELECT conception_id,status,conception_day FROM conceptions WHERE mother_id=$mother AND biological_father_id=$father AND conception_day>=$adopted AND status NOT IN ('failed','cancelled') ORDER BY conception_day DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["mother"] = subjectId, ["father"] = targetId,
                            ["adopted"] = ReadDouble(row, "created_day", 0d)
                        }).FirstOrDefault();
                    if (conception != null)
                    {
                        stage = "leverage_available";
                        payload["stage"] = stage;
                        payload["verifiedConceptionId"] = ReadString(conception, "conception_id", "");
                        payload["verifiedConceptionStatus"] = ReadString(conception, "status", "");
                        ExecuteSql(connection,
                            "UPDATE court_plots SET payload_json=$payload,progress=0.75,updated_ts=$ts WHERE plot_id=$id;",
                            new Dictionary<string, object>
                            {
                                ["payload"] = Json.Serialize(payload),
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["id"] = ReadString(row, "plot_id", "")
                            });
                    }
                }
                return new Dictionary<string, object>
                {
                    ["plotId"] = ReadString(row, "plot_id", ""),
                    ["tacticId"] = tactic,
                    ["goal"] = ReadString(payload, "intendedGain", ""),
                    ["stage"] = stage,
                    ["adoptedDay"] = ReadDouble(row, "created_day", worldDay),
                    ["private"] = true
                };
            }
#endif
        }

        private static Dictionary<string, object> PersistCourtCharacterSchemeFromOutcome(
            string campaignId, Dictionary<string, object> context, Dictionary<string, object> payload,
            string visibleReply, Dictionary<string, object> decisionBrief, string decisionId,
            bool actedRomantically, bool calculated)
        {
#if REIGN_EXCLUDE_COURT
            return new Dictionary<string, object> { ["stored"] = false, ["reason"] = "court_system_excluded" };
#else
            Dictionary<string, object> courtCharacter = ReadDictionary(context, "courtCharacter") ?? new Dictionary<string, object>();
            if (!ReadBool(courtCharacter, "available", false))
                return new Dictionary<string, object> { ["stored"] = false, ["reason"] = "court_character_unavailable" };
            List<string> allowed = ReadStringList(courtCharacter, "tacticIds");
            string tactic = ReadString(decisionBrief, "courtTactic", "").ToLowerInvariant();
            string text = (visibleReply ?? "").ToLowerInvariant();
            if (tactic == "none" || !allowed.Contains(tactic, StringComparer.OrdinalIgnoreCase)) tactic = "";
            if (string.IsNullOrWhiteSpace(tactic))
            {
                if (actedRomantically && calculated && allowed.Contains("strategic_seduction", StringComparer.OrdinalIgnoreCase))
                    tactic = "strategic_seduction";
                else if (allowed.Contains("blackmail", StringComparer.OrdinalIgnoreCase) && text.Contains("blackmail"))
                    tactic = "blackmail";
                else if (allowed.Contains("schemes", StringComparer.OrdinalIgnoreCase)
                    && (text.Contains("scheme") || text.Contains("plot") || text.Contains("arrange")))
                    tactic = "schemes";
            }
            bool adopted = tactic == "strategic_seduction" && actedRomantically
                || tactic == "pregnancy_leverage" && actedRomantically
                || tactic == "blackmail" && (text.Contains("blackmail") || text.Contains("secret") || text.Contains("expose"))
                || tactic == "schemes" && (text.Contains("scheme") || text.Contains("plot") || text.Contains("arrange") || text.Contains("plan"));
            if (!adopted)
                return new Dictionary<string, object> { ["stored"] = false, ["reason"] = "no_adopted_multistep_tactic", ["tacticId"] = tactic };

            string subjectId = ReadString(context, "subjectId", "");
            string targetId = ReadString(context, "targetId", "");
            if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(targetId))
                return new Dictionary<string, object> { ["stored"] = false, ["reason"] = "missing_pair" };
            double day = ReadDouble(payload, "worldDay", 0d);
            Dictionary<string, object> opportunity = ReadDictionary(context, "opportunity") ?? new Dictionary<string, object>();
            Dictionary<string, object> axes = ReadDictionary(opportunity, "axes") ?? new Dictionary<string, object>();
            string intendedGain = axes.OrderByDescending(x => ReadDouble(axes, x.Key, 0d)).Select(x => x.Key).FirstOrDefault() ?? "advantage";
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureCourtSchema(connection);
                List<Dictionary<string, object>> existingRows = QuerySql(connection,
                    "SELECT * FROM court_plots WHERE campaign_id=$campaign AND timeline_id='main' AND director_id=$subject AND target_id=$target AND status='active' ORDER BY updated_ts DESC;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["subject"] = subjectId, ["target"] = targetId });
                Dictionary<string, object> existing = existingRows.FirstOrDefault(row =>
                {
                    Dictionary<string, object> priorPayload = TryParseJsonObject(ReadString(row, "payload_json", ""))
                        ?? new Dictionary<string, object>();
                    return ReadString(priorPayload, "tacticId", "").Equals(tactic, StringComparison.OrdinalIgnoreCase);
                });
                string plotId = existing == null ? "court_character_" + Guid.NewGuid().ToString("N") : ReadString(existing, "plot_id", "");
                Dictionary<string, object> plotPayload = existing == null
                    ? new Dictionary<string, object>()
                    : TryParseJsonObject(ReadString(existing, "payload_json", "")) ?? new Dictionary<string, object>();
                List<string> supporting = ReadStringList(plotPayload, "supportingEventIds");
                if (!string.IsNullOrWhiteSpace(decisionId) && !supporting.Contains(decisionId, StringComparer.OrdinalIgnoreCase))
                    supporting.Add(decisionId);
                plotPayload["source"] = "court_character";
                plotPayload["courtCharacterCellId"] = ReadString(courtCharacter, "cellId", "");
                plotPayload["courtCharacterTitle"] = ReadString(courtCharacter, "title", "");
                plotPayload["tacticId"] = tactic;
                plotPayload["intendedGain"] = intendedGain;
                plotPayload["stage"] = ReadString(plotPayload, "stage", "adopted");
                plotPayload["supportingEventIds"] = supporting.Take(24).ToList();
                plotPayload["verifiedOutcomeRequired"] = true;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (existing == null)
                {
                    ExecuteSql(connection, @"INSERT INTO court_plots(
plot_id,campaign_id,timeline_id,plot_type,director_id,target_id,participants_json,known_by_json,hidden_from_json,status,
created_day,due_day,progress,terms_hash,secret_knowledge_id,revision,payload_json,created_ts,updated_ts)
VALUES($id,$campaign,'main','scheme',$subject,$target,$participants,$known,$hidden,'active',$day,-1,0,'','',1,$payload,$ts,$ts);",
                        new Dictionary<string, object>
                        {
                            ["id"] = plotId, ["campaign"] = campaignId, ["subject"] = subjectId, ["target"] = targetId,
                            ["participants"] = Json.Serialize(new[] { subjectId, targetId }),
                            ["known"] = Json.Serialize(new[] { subjectId }),
                            ["hidden"] = Json.Serialize(new[] { targetId }),
                            ["day"] = day, ["payload"] = Json.Serialize(plotPayload), ["ts"] = ts
                        });
                }
                else
                {
                    ExecuteSql(connection,
                        "UPDATE court_plots SET payload_json=$payload,revision=revision+1,updated_ts=$ts WHERE plot_id=$id;",
                        new Dictionary<string, object> { ["payload"] = Json.Serialize(plotPayload), ["ts"] = ts, ["id"] = plotId });
                }
                return new Dictionary<string, object>
                {
                    ["stored"] = true, ["plotId"] = plotId, ["tacticId"] = tactic,
                    ["stage"] = ReadString(plotPayload, "stage", "adopted"), ["intendedGain"] = intendedGain
                };
            }
#endif
        }

        private static double CourtCharacterEventWeight(
            Dictionary<string, object> postureA, Dictionary<string, object> postureB, params string[] tacticIds)
        {
            foreach (Dictionary<string, object> posture in new[] { postureA, postureB })
            {
                Dictionary<string, object> courtCharacter = ReadDictionary(posture, "courtCharacter")
                    ?? new Dictionary<string, object>();
                if (ReadDouble(courtCharacter, "gainStrength", 0d) <= 0d) continue;
                List<string> available = ReadStringList(courtCharacter, "tacticIds");
                if ((tacticIds ?? new string[0]).Any(tactic =>
                    available.Contains(tactic, StringComparer.OrdinalIgnoreCase)))
                    return 3d;
            }
            return 1d;
        }

        private static List<Dictionary<string, object>> RunCourtCharacterSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, pass, summary, data) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "court_character_" + id,
                ["suite"] = "court_character",
                ["passed"] = pass,
                ["summary"] = summary,
                ["data"] = data ?? new Dictionary<string, object>()
            });
            add("matrix_complete",
                CourtCharacterMatrix.Count == 50
                && CourtCharacterMatrix.Values.Count(x => x.Sex == "female") == 25
                && CourtCharacterMatrix.Values.Count(x => x.Sex == "male") == 25,
                "Both sex-specific 5x5 matrices contain every cell.", CourtCharacterMatrix.Count);
            add("band_boundaries",
                CourtCharacterBand(0) == -2 && CourtCharacterBand(20) == -2
                && CourtCharacterBand(21) == -1 && CourtCharacterBand(40) == -1
                && CourtCharacterBand(41) == 0 && CourtCharacterBand(60) == 0
                && CourtCharacterBand(61) == 1 && CourtCharacterBand(80) == 1
                && CourtCharacterBand(81) == 2 && CourtCharacterBand(100) == 2,
                "Honor and Boldness use the five fixed percentage bands.", null);
            Dictionary<string, object> testTraits = new Dictionary<string, object>
            {
                ["courtVirtues"] = new Dictionary<string, object> { ["honor"] = 5, ["boldness"] = 95 }
            };
            Dictionary<string, object> female = BuildCourtCharacterData(testTraits,
                new Dictionary<string, object> { ["isFemale"] = true, ["age"] = 28d });
            add("female_brazen_conspirator",
                ReadString(female, "title", "") == "Brazen Conspirator"
                && ReadBool(female, "supportsPregnancyLeverage", false),
                "An audacious devious adult woman receives the supplied leverage-capable cell.", female);
            Dictionary<string, object> male = BuildCourtCharacterData(testTraits,
                new Dictionary<string, object> { ["isFemale"] = false, ["age"] = 28d });
            add("male_brazen_conspirator",
                ReadString(male, "title", "") == "Brazen Conspirator"
                && !ReadBool(male, "supportsPregnancyLeverage", true),
                "An audacious devious adult man receives the corresponding male cell.", male);
            Dictionary<string, object> child = BuildCourtCharacterData(testTraits,
                new Dictionary<string, object> { ["isFemale"] = true, ["age"] = 15d });
            add("children_unavailable", !ReadBool(child, "available", true),
                "Children do not receive a Court Character cell.", child);
            Dictionary<string, object> knownTier = new Dictionary<string, object>
            {
                ["relativeBenefit"] = 50d, ["targetClanTierKnown"] = true, ["clanTierDelta"] = 1
            };
            Dictionary<string, object> hiddenTier = new Dictionary<string, object>
            {
                ["relativeBenefit"] = 50d, ["targetClanTierKnown"] = false, ["clanTierDelta"] = 6
            };
            add("known_information_boundary",
                Math.Abs(CourtCharacterGainStrength(knownTier) - 0.5d) < 0.0001d
                && CourtCharacterGainStrength(hiddenTier) == 0d,
                "Known clan rank affects gain strength while hidden rank does not.", null);
            add("manipulation_propensity_follows_honor",
                CourtCharacterManipulationPropensity(-2)
                    > CourtCharacterManipulationPropensity(-1)
                && CourtCharacterManipulationPropensity(-1)
                    > CourtCharacterManipulationPropensity(0)
                && CourtCharacterManipulationPropensity(0)
                    > CourtCharacterManipulationPropensity(1)
                && CourtCharacterManipulationPropensity(2) == 0d,
                "The fixed Honor column supplies a monotonic bounded manipulation propensity without changing the cell.",
                null);
            Dictionary<string, object> characteristics = new Dictionary<string, object>
            {
                ["traits"] = new Dictionary<string, object> { ["courtCharacter"] = female }
            };
            Dictionary<string, object> maximum = CourtCharacterDecisionSnapshot(characteristics,
                new Dictionary<string, object> { ["relativeBenefit"] = 100d });
            add("strong_bounded_emphasis",
                Math.Abs(ReadDouble(maximum, "strategicBoost", 0d) - 20d) < 0.0001d
                && Math.Abs(ReadDouble(maximum, "matchingTacticWeight", 0d) - 3d) < 0.0001d,
                "Maximum fitting opportunity produces the selected strong but bounded emphasis.", maximum);
            Dictionary<string, object> weightedPosture = new Dictionary<string, object>
            {
                ["courtCharacter"] = maximum
            };
            add("event_tactic_weight",
                Math.Abs(CourtCharacterEventWeight(weightedPosture, new Dictionary<string, object>(), "strategic_seduction") - 3d) < 0.0001d
                && Math.Abs(CourtCharacterEventWeight(weightedPosture, new Dictionary<string, object>(), "protection") - 1d) < 0.0001d,
                "Matching autonomous event tactics receive exactly 3x weight without stacking.", null);
            Dictionary<string, object> appearanceA = BuildCourtCharacterData(testTraits,
                new Dictionary<string, object> { ["isFemale"] = true, ["age"] = 28d, ["appearance"] = new Dictionary<string, object> { ["visibleStatusScore"] = 5 } });
            Dictionary<string, object> appearanceB = BuildCourtCharacterData(testTraits,
                new Dictionary<string, object> { ["isFemale"] = true, ["age"] = 28d, ["appearance"] = new Dictionary<string, object> { ["visibleStatusScore"] = 100 } });
            add("equipment_never_changes_cell",
                ReadString(appearanceA, "cellId", "") == ReadString(appearanceB, "cellId", ""),
                "Equipment and visible presentation cannot alter the fixed matrix coordinates.", null);
            return rows;
        }
    }
}
