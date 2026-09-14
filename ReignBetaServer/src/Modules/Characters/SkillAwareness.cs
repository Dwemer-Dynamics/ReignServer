using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int SkillAwarenessModelVersion = 1;
        private const string SkillAwarenessModelId = "reign_skill_awareness_v1";

        private static readonly string[] SkillBandLabels =
        {
            "Untrained", "Rudimentary", "Novice", "Practiced", "Competent", "Capable", "Seasoned",
            "Skilled", "Highly Skilled", "Expert", "Master", "Renowned Master", "Extraordinary", "Legendary"
        };

        private sealed class SkillAwarenessDefinition
        {
            public string Key;
            public string Name;
            public string[] Aliases;
            public string[] Descriptions;
        }

        private sealed class SkillAwarenessEntry
        {
            public SkillAwarenessDefinition Definition;
            public int Value;
            public int BandIndex;
            public int Rank;
        }

        private static readonly SkillAwarenessDefinition[] SkillAwarenessDefinitions =
        {
            SkillDefinition("oneHanded", "One Handed", new[] { "one handed", "one-handed", "swordsmanship", "sword", "arming sword", "mace", "hand axe", "shield fighting" },
                "Has no reliable training with a weapon held in one hand.",
                "Can grip a simple sidearm but lacks sound guard, balance, and timing.",
                "Knows basic cuts, thrusts, and blocks but falters under pressure.",
                "Has drilled common one-handed forms and can manage an ordinary bout.",
                "Handles sword, mace, or axe competently in a disciplined exchange.",
                "Can defend openings, vary attacks, and punish obvious mistakes.",
                "Has the seasoned timing and footwork of an experienced fighter.",
                "Uses one-handed weapons with practiced precision in difficult combat.",
                "Reads an opponent quickly and controls range with impressive consistency.",
                "Can outfight most trained warriors through timing, economy, and composure.",
                "Displays mastery of guard, distance, counters, and weapon transitions.",
                "Is renowned for one-handed technique even among veteran champions.",
                "Makes demanding weapon work appear effortless, exact, and brutally efficient.",
                "Possesses legendary one-handed skill worthy of songs and battlefield memory."),
            SkillDefinition("twoHanded", "Two Handed", new[] { "two handed", "two-handed", "greatsword", "great sword", "battle axe", "battleaxe", "maul", "long axe" },
                "Cannot safely control the reach or momentum of a two-handed weapon.",
                "Can swing a heavy weapon but wastes strength and leaves broad openings.",
                "Understands basic stance and leverage yet remains slow to recover.",
                "Has practiced committed blows, guards, and recovery with heavy arms.",
                "Can use reach and force competently without losing balance.",
                "Combines leverage, spacing, and purposeful aggression in a solid technique.",
                "Has veteran control of momentum and knows when not to overcommit.",
                "Uses heavy weapons skillfully to dominate space and break defenses.",
                "Can redirect powerful attacks and exploit small failures in an enemy guard.",
                "Wields two-handed arms with expert reach, timing, and destructive economy.",
                "Has mastered the difficult union of power, precision, and recovery.",
                "Is widely renowned for overcoming skilled opponents with heavy weapons.",
                "Controls immense force with extraordinary speed, judgment, and accuracy.",
                "Possesses legendary two-handed prowess capable of defining a battlefield."),
            SkillDefinition("polearm", "Polearm", new[] { "polearm", "pole arm", "spear", "lance", "pike", "glaive", "staff fighting" },
                "Has no dependable sense of polearm reach, grip, or point control.",
                "Can brace or thrust a spear clumsily but struggles to recover.",
                "Understands simple thrusts and guards while leaving the weapon easily displaced.",
                "Has practiced maintaining distance and presenting a credible spear point.",
                "Uses common polearms competently on foot or from a steady mount.",
                "Can shift grip, threaten lanes, and exploit the weapon's reach.",
                "Has seasoned control of point, haft, formation spacing, and recovery.",
                "Uses polearms skillfully across changing ranges and crowded fighting.",
                "Can deny ground and redirect attacks with impressive point control.",
                "Is expert at reach, line, leverage, and the timing of decisive thrusts.",
                "Has mastered polearm distance and can adapt fluidly between forms.",
                "Is renowned as a lancer or spearfighter among hardened veterans.",
                "Commands reach with extraordinary precision even in chaotic battle.",
                "Possesses legendary polearm skill, making a spear point seem inescapable."),
            SkillDefinition("bow", "Bow", new[] { "bow", "archery", "archer", "longbow", "shortbow", "arrow", "arrows" },
                "Cannot reliably draw, anchor, and loose an arrow toward a chosen mark.",
                "Can loose at close range but lacks consistency in aim and draw.",
                "Knows basic stance and release, though accuracy declines quickly under stress.",
                "Has practiced enough to group arrows against an uncomplicated target.",
                "Can shoot competently at useful ranges in ordinary conditions.",
                "Judges distance and release well enough to threaten moving opponents.",
                "Has seasoned archery habits, steady rhythm, and dependable battlefield accuracy.",
                "Shoots skillfully across varied ranges and adjusts without lengthy preparation.",
                "Can place rapid arrows with impressive calm and consistency.",
                "Is an expert archer who reads distance, movement, and wind instinctively.",
                "Has mastered draw, release, pace, and demanding shots under pressure.",
                "Is renowned for archery even among elite hunters and battlefield marksmen.",
                "Makes extraordinary shots at range with speed that seems almost effortless.",
                "Possesses legendary archery capable of turning improbable shots into certainty."),
            SkillDefinition("crossbow", "Crossbow", new[] { "crossbow", "cross bow", "crossbowman", "bolt", "bolts", "arbalest" },
                "Cannot reliably span, load, aim, and discharge a crossbow safely.",
                "Can operate a simple crossbow slowly but mishandles aim and timing.",
                "Understands loading and sighting yet remains awkward in a hurried exchange.",
                "Has practiced an orderly loading cycle and can strike clear targets.",
                "Uses a crossbow competently with reasonable accuracy and preparation.",
                "Can choose firing moments well and reload without needless confusion.",
                "Has seasoned judgment of range, cover, ammunition, and vulnerable openings.",
                "Uses crossbows skillfully in sieges, formations, and close danger.",
                "Can exploit brief targets with impressive accuracy and mechanical familiarity.",
                "Is expert at deliberate shots, efficient loading, and tactical positioning.",
                "Has mastered crossbow handling and extracts remarkable consistency from the weapon.",
                "Is renowned for deadly crossbow work among veteran soldiers and engineers.",
                "Places extraordinary shots while managing the weapon with flawless efficiency.",
                "Possesses legendary crossbow skill, wasting neither a bolt nor an opening."),
            SkillDefinition("throwing", "Throwing", new[] { "throwing", "thrown weapon", "javelin", "javelins", "throwing axe", "throwing knife" },
                "Cannot throw a weapon with dependable alignment, force, or safety.",
                "Can hurl a light weapon but rarely controls point or rotation.",
                "Understands basic throwing form and can threaten a nearby stationary target.",
                "Has practiced release and distance enough for useful short-range throws.",
                "Throws common weapons competently with sound balance and timing.",
                "Can judge movement, range, and weapon rotation in a real exchange.",
                "Has seasoned accuracy and knows when a thrown weapon is worth risking.",
                "Uses javelins and other thrown arms skillfully against changing targets.",
                "Can strike narrow openings with impressive speed and controlled force.",
                "Is expert at release timing, trajectory, and selecting decisive throws.",
                "Has mastered varied thrown weapons across difficult ranges and conditions.",
                "Is renowned for throwing accuracy among hunters and veteran skirmishers.",
                "Turns extraordinary moving throws into calculated, repeatable attacks.",
                "Possesses legendary throwing skill, making distance offer little protection."),
            SkillDefinition("riding", "Riding", new[] { "riding", "horsemanship", "horseman", "horsewoman", "horseback", "mounted", "cavalry", "ride a horse", "ride horses" },
                "Can barely remain secure in the saddle and cannot direct a mount confidently.",
                "Manages a calm horse at an easy pace but struggles with sudden movement.",
                "Knows basic reins, seat, and balance while remaining uneasy at speed.",
                "Has practiced ordinary travel and can control a familiar mount reliably.",
                "Rides competently across common ground and handles routine mounted demands.",
                "Can manage pace, obstacles, and an unsettled horse with useful confidence.",
                "Has the seasoned seat and instincts of someone accustomed to long riding.",
                "Rides skillfully in difficult terrain, formations, and sudden danger.",
                "Can guide a powerful mount with impressive balance and subtle control.",
                "Is an expert horseman who remains effective at speed and under pressure.",
                "Has mastered mounted movement, endurance, and the temperament of many horses.",
                "Is renowned for horsemanship among cavalry veterans and noble riders.",
                "Achieves extraordinary unity with a mount through terrain and violent maneuver.",
                "Possesses legendary horsemanship, seeming to move and fight as one with the horse."),
            SkillDefinition("athletics", "Athletics", new[] { "athletics", "athletic", "fitness", "stamina", "endurance", "running", "climbing", "on foot" },
                "Lacks the conditioning and coordination needed for sustained physical effort.",
                "Can manage light exertion but tires quickly and moves without efficiency.",
                "Has basic stamina and coordination yet struggles through prolonged hardship.",
                "Is accustomed to ordinary marching, lifting, and short bursts of effort.",
                "Has competent strength, balance, and endurance for everyday campaigning.",
                "Can sustain demanding movement and recover without becoming an immediate liability.",
                "Has seasoned conditioning built through travel, labor, or repeated combat.",
                "Moves skillfully on foot with strong endurance and dependable coordination.",
                "Maintains impressive speed, balance, and stamina through punishing exertion.",
                "Has expert conditioning and can outlast most trained soldiers on foot.",
                "Has mastered bodily economy, endurance, and movement under heavy strain.",
                "Is renowned for physical capability even among hardened warriors and scouts.",
                "Shows extraordinary endurance and control beyond nearly every ordinary athlete.",
                "Possesses legendary physical prowess that survives ordeals others consider impossible."),
            SkillDefinition("smithing", "Smithing", new[] { "smithing", "blacksmith", "blacksmithing", "forge", "forging", "metalwork", "weaponsmith", "armor smith" },
                "Cannot safely use a forge or produce a serviceable metal implement.",
                "Recognizes basic tools but spoils heat, shape, and finish without guidance.",
                "Can perform simple forge work while producing uneven and unreliable results.",
                "Has practiced repairs and plain pieces that can serve an undemanding need.",
                "Can competently forge and repair common tools or straightforward weapons.",
                "Understands heat, metal, balance, and finishing well enough for dependable work.",
                "Has seasoned workshop judgment and can diagnose flaws before they become failures.",
                "Produces skillful, balanced work suited to experienced soldiers and craftsmen.",
                "Can shape difficult pieces with impressive consistency and material understanding.",
                "Is an expert smith capable of refined weapons and demanding repairs.",
                "Has mastered the forge from material selection through temper and finish.",
                "Is renowned for metalwork sought by wealthy patrons and veteran warriors.",
                "Creates extraordinary pieces whose balance and durability distinguish them immediately.",
                "Possesses legendary smithing skill capable of producing heirlooms for generations."),
            SkillDefinition("scouting", "Scouting", new[] { "scouting", "scout", "tracking", "tracker", "trail", "pathfinding", "reconnaissance", "finding tracks" },
                "Cannot reliably read tracks, choose a route, or notice obvious field signs.",
                "Recognizes only clear trails and becomes easily confused away from roads.",
                "Knows basic tracks and landmarks but misses subtle or weathered evidence.",
                "Has practiced route-finding and can follow ordinary movement through familiar country.",
                "Scouts competently, noticing useful tracks, terrain, and travel hazards.",
                "Can compare signs, estimate movement, and choose practical approaches.",
                "Has seasoned field instincts formed through repeated marches and reconnaissance.",
                "Reads terrain and tracks skillfully while avoiding many common surprises.",
                "Can reconstruct movement from faint signs with impressive speed and judgment.",
                "Is an expert scout who finds routes and threats others consistently overlook.",
                "Has mastered tracking, concealment, navigation, and the reading of terrain.",
                "Is renowned for fieldcraft among veteran outriders, hunters, and commanders.",
                "Extracts extraordinary certainty from signs nearly everyone else would miss.",
                "Possesses legendary scouting instincts, making wilderness and pursuit seem transparent."),
            SkillDefinition("tactics", "Tactics", new[] { "tactics", "tactical", "battle plan", "battle plans", "battlefield", "formation", "formations", "maneuver", "manoeuvre" },
                "Cannot organize even a simple battle plan without creating dangerous confusion.",
                "Understands obvious orders but overlooks position, timing, and enemy response.",
                "Can arrange a basic line while struggling when circumstances change.",
                "Has practiced common formations and recognizes straightforward battlefield advantages.",
                "Uses terrain, timing, and troop roles competently in an ordinary engagement.",
                "Can adjust a plan when the enemy reveals a clear weakness.",
                "Has seasoned battlefield judgment shaped by victories, mistakes, and observation.",
                "Coordinates formations skillfully and anticipates several likely enemy responses.",
                "Can create and exploit tactical dilemmas with impressive discipline.",
                "Is an expert tactician who adapts quickly without losing the larger design.",
                "Has mastered combined movement, reserves, terrain, deception, and decisive timing.",
                "Is renowned for tactical judgment among commanders and veteran officers.",
                "Sees extraordinary battlefield possibilities before others understand the danger.",
                "Possesses legendary tactical insight capable of overturning seemingly hopeless battles."),
            SkillDefinition("roguery", "Roguery", new[] { "roguery", "rogue", "underworld", "criminal", "crime", "smuggling", "pickpocket", "thieving", "thief", "deception", "con game" },
                "Has no practical understanding of criminal methods, concealment, or underworld caution.",
                "Recognizes crude wrongdoing but is easily noticed, misled, or exploited.",
                "Knows a few dishonest methods while lacking contacts and disciplined concealment.",
                "Has practiced minor deception or illicit work with uneven but useful results.",
                "Navigates common underworld risks competently and recognizes obvious traps.",
                "Can manage discreet exchanges, false impressions, and questionable opportunities.",
                "Has seasoned instincts for leverage, suspicion, illicit value, and personal risk.",
                "Operates skillfully through deception and criminal circles without needless exposure.",
                "Can read schemes and exploit weak precautions with impressive subtlety.",
                "Is an expert rogue who plans deception several moves beyond ordinary suspicion.",
                "Has mastered concealment, illicit networks, misdirection, and calculated betrayal.",
                "Is renowned or feared within circles where secrets and crimes carry value.",
                "Executes extraordinary schemes while leaving remarkably little usable evidence.",
                "Possesses legendary roguery, turning guarded systems and people into opportunities."),
            SkillDefinition("charm", "Charm", new[] { "charm", "charming", "persuasion", "persuasive", "diplomacy", "diplomatic", "speech", "speaking", "courtly", "social grace" },
                "Cannot read a room or present a persuasive case without causing discomfort.",
                "Can manage basic courtesy but struggles to hold attention or build rapport.",
                "Understands simple persuasion while sounding awkward, obvious, or poorly timed.",
                "Has practiced polite conversation and can make a modest favorable impression.",
                "Speaks competently, adjusts tone, and handles ordinary social negotiation.",
                "Can build rapport and frame requests in ways others find reasonable.",
                "Has seasoned social instincts and recognizes pride, hesitation, and hidden objections.",
                "Uses charm skillfully to reassure, impress, persuade, or redirect a conversation.",
                "Can control a difficult room with impressive tact, timing, and presence.",
                "Is an expert persuader who makes carefully chosen arguments feel natural.",
                "Has mastered social rhythm, rhetoric, etiquette, and the shaping of impressions.",
                "Is renowned for winning audiences and negotiations that defeat lesser speakers.",
                "Exerts extraordinary influence through words without seeming to force agreement.",
                "Possesses legendary charm capable of defining courts, alliances, and public memory."),
            SkillDefinition("leadership", "Leadership", new[] { "leadership", "leader", "leading", "command", "commander", "commanding", "inspire", "inspiring", "morale" },
                "Cannot reliably direct others or sustain confidence when responsibility becomes difficult.",
                "Can issue simple instructions but quickly loses clarity, trust, or control.",
                "Understands basic command while struggling to inspire or resolve disorder.",
                "Has practiced directing small groups through familiar and limited tasks.",
                "Leads competently, communicates expectations, and maintains ordinary discipline.",
                "Can steady followers, delegate sensibly, and make responsibility feel purposeful.",
                "Has seasoned command presence formed through repeated pressure and accountability.",
                "Leads skillfully across disagreement, fear, fatigue, and changing circumstances.",
                "Can inspire impressive loyalty and coordinated effort without wasting authority.",
                "Is an expert leader who understands morale, trust, discipline, and example.",
                "Has mastered command at both personal and organizational scales.",
                "Is renowned for leadership among nobles, officers, and hardened retainers.",
                "Creates extraordinary unity and resolve in groups facing severe adversity.",
                "Possesses legendary leadership capable of shaping peoples, armies, and eras."),
            SkillDefinition("trade", "Trade", new[] { "trade", "trading", "merchant", "merchants", "commerce", "commercial", "market", "markets", "bargain", "bargaining", "barter", "caravan", "profit", "prices" },
                "Cannot reliably judge value, cost, or the consequences of a bargain.",
                "Recognizes obvious prices but is easily confused by quality and hidden costs.",
                "Understands simple buying and selling while missing leverage and changing demand.",
                "Has practiced ordinary bargaining and can avoid the crudest unfavorable deals.",
                "Trades competently, compares value, and negotiates routine exchanges with care.",
                "Can identify margins, alternatives, and bargaining pressure in practical transactions.",
                "Has seasoned commercial judgment shaped by markets, travel, and costly lessons.",
                "Trades skillfully across changing prices, risks, and competing interests.",
                "Can detect hidden value and negotiate impressive terms without losing credibility.",
                "Is an expert merchant who understands markets, logistics, credit, and leverage.",
                "Has mastered valuation, negotiation, risk, and the movement of valuable goods.",
                "Is renowned for commercial judgment among wealthy merchants and clan treasurers.",
                "Finds extraordinary opportunities and structures bargains others fail to imagine.",
                "Possesses legendary tradecraft capable of building fortunes and reshaping markets."),
            SkillDefinition("steward", "Steward", new[] { "steward", "stewardship", "administrator", "administration", "manage estates", "estate management", "supplies", "provisions", "logistics", "quartermaster" },
                "Cannot organize supplies, records, or household duties without serious waste.",
                "Can track a few simple needs but loses control as obligations multiply.",
                "Understands basic provisioning and records while overlooking many practical details.",
                "Has practiced managing routine stores, schedules, and household responsibilities.",
                "Administers competently, keeping ordinary people and supplies reasonably organized.",
                "Can balance competing needs, reduce waste, and anticipate common shortages.",
                "Has seasoned administrative judgment built through repeated responsibility and scarcity.",
                "Manages complex households, parties, or estates with skillful consistency.",
                "Can coordinate impressive resources while noticing failures before they spread.",
                "Is an expert steward who unites logistics, records, personnel, and priorities.",
                "Has mastered large-scale administration under pressure and incomplete information.",
                "Is renowned for stewardship among major households and demanding commanders.",
                "Creates extraordinary order and resilience from strained people and resources.",
                "Possesses legendary stewardship capable of sustaining realms through severe crises."),
            SkillDefinition("medicine", "Medicine", new[] { "medicine", "medical", "medic", "healing", "healer", "physician", "doctor", "wound", "wounds", "treatment", "surgery" },
                "Cannot recognize or safely treat even common injuries and illness.",
                "Knows crude first aid but may worsen anything beyond a minor wound.",
                "Understands basic cleaning and binding while lacking reliable diagnosis.",
                "Has practiced ordinary wound care and recognizes several common dangers.",
                "Treats routine injuries competently and understands cleanliness, rest, and observation.",
                "Can prioritize casualties and manage many common complications with sound judgment.",
                "Has seasoned medical instincts formed through repeated illness and battlefield injury.",
                "Practices medicine skillfully, recognizing subtle danger and adapting treatment.",
                "Can stabilize difficult cases with impressive calm and diagnostic attention.",
                "Is an expert physician trusted with severe wounds and uncertain illnesses.",
                "Has mastered the practical medical knowledge available across many conditions.",
                "Is renowned for healing among nobles, soldiers, and other experienced practitioners.",
                "Achieves extraordinary recoveries through rare judgment, discipline, and experience.",
                "Possesses legendary medical skill, preserving lives nearly everyone else would lose."),
            SkillDefinition("engineering", "Engineering", new[] { "engineering", "engineer", "siegecraft", "siege craft", "fortification", "fortifications", "siege engine", "construction", "mechanics", "machinery" },
                "Cannot safely plan or assemble even a basic engineered structure.",
                "Recognizes simple mechanisms but misjudges load, material, and construction order.",
                "Understands basic tools and plans while producing fragile or inefficient work.",
                "Has practiced straightforward construction and repair under stable conditions.",
                "Handles common engineering problems competently with workable measurements and materials.",
                "Can adapt plans, diagnose failures, and organize practical construction.",
                "Has seasoned understanding of structures, machines, fortifications, and siege demands.",
                "Engineers skillfully across difficult terrain, limited supplies, and enemy pressure.",
                "Can solve complex structural and mechanical problems with impressive efficiency.",
                "Is an expert engineer capable of sophisticated siege and defensive works.",
                "Has mastered design, materials, labor, machinery, and destructive countermeasures.",
                "Is renowned for engineering among master builders and experienced besiegers.",
                "Creates extraordinary works and solutions beyond the reach of ordinary specialists.",
                "Possesses legendary engineering insight capable of deciding sieges and shaping cities.")
        };

        private static SkillAwarenessDefinition SkillDefinition(string key, string name, string[] aliases, params string[] descriptions)
        {
            return new SkillAwarenessDefinition { Key = key, Name = name, Aliases = aliases, Descriptions = descriptions };
        }

        private static int SkillBandIndex(int value)
        {
            if (value <= 0) return 0;
            return Math.Min(13, value / 20);
        }

        private static Dictionary<string, object> ResolveNativeSkillValues(Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            profile = profile ?? new Dictionary<string, object>();
            characteristics = characteristics ?? new Dictionary<string, object>();
            Dictionary<string, object> merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> traits = ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>();
            foreach (Dictionary<string, object> source in new[]
            {
                ReadDictionary(traits, "nativeSkills"),
                ReadDictionary(ReadDictionary(profile, "sourceFacts"), "skills"),
                ReadDictionary(profile, "skills")
            })
            {
                if (source == null) continue;
                foreach (KeyValuePair<string, object> pair in source) merged[pair.Key] = pair.Value;
            }
            return merged;
        }

        private static List<SkillAwarenessEntry> ResolveSkillAwarenessEntries(Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            Dictionary<string, object> values = ResolveNativeSkillValues(profile, characteristics);
            List<SkillAwarenessEntry> entries = new List<SkillAwarenessEntry>();
            foreach (SkillAwarenessDefinition definition in SkillAwarenessDefinitions)
            {
                if (!values.TryGetValue(definition.Key, out object raw) || raw == null) continue;
                int value;
                try { value = Convert.ToInt32(raw, CultureInfo.InvariantCulture); }
                catch { continue; }
                entries.Add(new SkillAwarenessEntry
                {
                    Definition = definition,
                    Value = value,
                    BandIndex = SkillBandIndex(value)
                });
            }
            entries = entries
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => Array.IndexOf(SkillAwarenessDefinitions, entry.Definition))
                .ToList();
            for (int index = 0; index < entries.Count; index++) entries[index].Rank = index + 1;
            return entries;
        }

        private static string BuildCoreSkillAwarenessPrompt(Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            List<SkillAwarenessEntry> top = ResolveSkillAwarenessEntries(profile, characteristics).Take(3).ToList();
            string interests = BuildDefiningInterestsPrompt(characteristics);
            if (top.Count == 0 && interests.Length == 0) return "";
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Skill awareness model: " + SkillAwarenessModelId + ".");
            builder.AppendLine("These are defining capabilities. Exact values are private evidence: never state skill numbers, levels, points, or game-stat terminology in visible speech. Describe ability naturally through experience and setting-appropriate language.");
            foreach (SkillAwarenessEntry entry in top) builder.AppendLine(FormatSkillAwarenessPromptLine(entry));
            if (interests.Length > 0) builder.AppendLine("\nTHREE DEFINING PERSONAL INTERESTS\n" + interests);
            return builder.ToString().TrimEnd();
        }

        private static string BuildRelevantSkillAwarenessPrompt(Dictionary<string, object> profile, Dictionary<string, object> characteristics, string turnText, string sceneContext)
        {
            List<SkillAwarenessEntry> entries = ResolveSkillAwarenessEntries(profile, characteristics);
            if (entries.Count == 0) return "";
            List<SkillAwarenessEntry> relevant = SelectRelevantSkillAwareness(entries, turnText, sceneContext);
            if (relevant.Count == 0) return "";
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Use these exact capabilities to ground the answer. Never speak their numeric values or game-stat labels. A deliberate boast may exaggerate, but must remain a boast rather than replacing the authoritative capability.");
            foreach (SkillAwarenessEntry entry in relevant) builder.AppendLine(FormatSkillAwarenessPromptLine(entry));
            return builder.ToString().TrimEnd();
        }

        private static string FormatSkillAwarenessPromptLine(SkillAwarenessEntry entry)
        {
            string label = SkillBandLabels[entry.BandIndex];
            string description = entry.Definition.Descriptions[entry.BandIndex];
            return "- " + entry.Definition.Name + " (private exact value " + entry.Value.ToString(CultureInfo.InvariantCulture)
                + "; " + label + "; personal rank " + entry.Rank.ToString(CultureInfo.InvariantCulture) + "): " + description;
        }

        private static List<SkillAwarenessEntry> SelectRelevantSkillAwareness(List<SkillAwarenessEntry> entries, string turnText, string sceneContext)
        {
            string query = ((turnText ?? "") + "\n" + (sceneContext ?? "")).Trim();
            if (query.Length == 0) return new List<SkillAwarenessEntry>();
            bool fullAccounting = Regex.IsMatch(query, @"\b(all|every|complete|entire)\b.{0,35}\b(skills?|abilities|training|capabilities)\b|\b(skills?|abilities|training|capabilities)\b.{0,35}\b(all|every|complete|entire)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (fullAccounting) return entries.ToList();

            HashSet<string> topKeys = new HashSet<string>(entries.Take(3).Select(entry => entry.Definition.Key), StringComparer.OrdinalIgnoreCase);
            List<SkillAwarenessEntry> matches = new List<SkillAwarenessEntry>();
            foreach (SkillAwarenessEntry entry in entries)
            {
                bool match = entry.Definition.Aliases.Any(alias => ContainsSkillAlias(query, alias));
                if (entry.Definition.Key.Equals("trade", StringComparison.OrdinalIgnoreCase)
                    && (IsConversationalTradeUsage(query.ToLowerInvariant()) || NegatesMaterialTradeOffer(query.ToLowerInvariant())))
                    match = false;
                if (entry.Definition.Key.Equals("riding", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(query, @"\briding\s+(?:out\s+)?(?:the\s+)?(?:storm|wave|tide|trouble|crisis)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    && !Regex.IsMatch(query, @"\b(?:horse|mare|stallion|mount|saddle|cavalry|horsemanship)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    match = false;
                if (match && !topKeys.Contains(entry.Definition.Key)) matches.Add(entry);
            }
            return matches.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Rank).Take(3).ToList();
        }

        private static bool ContainsSkillAlias(string text, string alias)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(alias)) return false;
            return Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(alias.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static Dictionary<string, object> BuildSkillAwarenessDiagnostics(Dictionary<string, object> profile, Dictionary<string, object> characteristics, string turnText, string sceneContext)
        {
            List<SkillAwarenessEntry> entries = ResolveSkillAwarenessEntries(profile, characteristics);
            List<SkillAwarenessEntry> relevant = SelectRelevantSkillAwareness(entries, turnText, sceneContext);
            Func<SkillAwarenessEntry, Dictionary<string, object>> row = entry => new Dictionary<string, object>
            {
                ["key"] = entry.Definition.Key,
                ["value"] = entry.Value,
                ["band"] = SkillBandLabels[entry.BandIndex],
                ["rank"] = entry.Rank
            };
            return new Dictionary<string, object>
            {
                ["model"] = SkillAwarenessModelId,
                ["personalInterests"] = NarrativePromptDiagnostics(characteristics),
                ["version"] = SkillAwarenessModelVersion,
                ["source"] = ReadDictionary(profile ?? new Dictionary<string, object>(), "skills") != null ? "live_profile" : "persisted_native_skills",
                ["topSkills"] = entries.Take(3).Select(row).ToList(),
                ["onDemandSkills"] = relevant.Select(row).ToList()
            };
        }

        private static bool ContainsSpokenNumericSkillLevel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string skillNames = string.Join("|", SkillAwarenessDefinitions.SelectMany(definition => new[] { definition.Name, definition.Key }).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
            return Regex.IsMatch(text, @"\b(?:my\s+)?(?:" + skillNames + @")\s+(?:skill\s+)?(?:is|stands?\s+at|equals?)\s+\d{1,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(text, @"\b\d{1,4}\s+(?:points?|levels?)\s+(?:in|of)\s+(?:" + skillNames + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(text, @"\b(?:level|skill\s+level)\s+\d{1,4}\s+(?:in\s+)?(?:" + skillNames + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string RemoveSpokenNumericSkillLevels(string text, out bool repaired)
        {
            repaired = ContainsSpokenNumericSkillLevel(text);
            if (!repaired) return text ?? "";
            string skillNames = string.Join("|", SkillAwarenessDefinitions.SelectMany(definition => new[] { definition.Name, definition.Key }).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
            string result = Regex.Replace(text, @"\b((?:my\s+)?(?:" + skillNames + @")\s+(?:skill\s+)?)(?:is|stands?\s+at|equals?)\s+\d{1,4}\b", "$1is one of my practiced capabilities", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result, @"\b\d{1,4}\s+(?:points?|levels?)\s+(?:in|of)\s+((?:" + skillNames + @"))\b", "considerable experience in $1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result, @"\b(?:level|skill\s+level)\s+\d{1,4}\s+(?:in\s+)?((?:" + skillNames + @"))\b", "practiced ability in $1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return result;
        }

        private static List<Dictionary<string, object>> RunSkillAwarenessSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, detail, evidence) => results.Add(new Dictionary<string, object>
            {
                ["caseId"] = id, ["passed"] = passed, ["summary"] = detail, ["data"] = evidence
            });
            bool descriptionsComplete = SkillAwarenessDefinitions.Length == 18
                && SkillAwarenessDefinitions.All(definition => definition.Descriptions != null && definition.Descriptions.Length == 14
                    && definition.Descriptions.All(description => !string.IsNullOrWhiteSpace(description))
                    && definition.Descriptions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 14);
            add("skill_awareness_description_matrix", descriptionsComplete, "All 18 native skills have fourteen authored proficiency descriptions.", SkillAwarenessDefinitions.Select(definition => new { definition.Key, Count = definition.Descriptions == null ? 0 : definition.Descriptions.Length }).ToList());
            int[] anchors = { -5, 0, 19, 20, 39, 40, 259, 260, 400 };
            int[] expected = { 0, 0, 0, 1, 1, 2, 12, 13, 13 };
            add("skill_awareness_band_boundaries", anchors.Select(SkillBandIndex).SequenceEqual(expected), "Twenty-point bands and the 260+ Legendary cap resolve exactly.", anchors.Select(value => new { value, band = SkillBandLabels[SkillBandIndex(value)] }).ToList());
            Dictionary<string, object> profile = new Dictionary<string, object>
            {
                ["skills"] = new Dictionary<string, object> { ["trade"] = 180, ["riding"] = 180, ["charm"] = 170, ["medicine"] = 20 }
            };
            List<SkillAwarenessEntry> ranked = ResolveSkillAwarenessEntries(profile, new Dictionary<string, object>());
            add("skill_awareness_top_three_tie_order", ranked.Take(3).Select(entry => entry.Definition.Key).SequenceEqual(new[] { "riding", "trade", "charm" }), "Top skills sort by exact value and canonical skill order for ties.", ranked.Select(entry => new { entry.Definition.Key, entry.Value, entry.Rank }).ToList());
            string stable = BuildCoreSkillAwarenessPrompt(profile, new Dictionary<string, object>());
            string ridingQuery = BuildRelevantSkillAwarenessPrompt(profile, new Dictionary<string, object>(), "Are you any good at medicine?", "");
            string storyTrade = BuildRelevantSkillAwarenessPrompt(profile, new Dictionary<string, object>(), "Let us trade stories about home.", "");
            Dictionary<string, object> metaphorProfile = new Dictionary<string, object>
            {
                ["skills"] = new Dictionary<string, object> { ["oneHanded"] = 180, ["twoHanded"] = 170, ["charm"] = 160, ["riding"] = 20 }
            };
            string ridingMetaphor = BuildRelevantSkillAwarenessPrompt(metaphorProfile, new Dictionary<string, object>(), "We are riding out the storm.", "");
            add("skill_awareness_prompt_selection", stable.Contains("Riding") && stable.Contains("Trade") && stable.Contains("Charm") && ridingQuery.Contains("Medicine") && string.IsNullOrWhiteSpace(storyTrade) && string.IsNullOrWhiteSpace(ridingMetaphor), "Top three are stable, explicit non-top skills are selected, and conversational trade or riding metaphors are ignored.", new { stable, ridingQuery, storyTrade, ridingMetaphor });
            string repaired = RemoveSpokenNumericSkillLevels("My Riding skill is 180, though I rode 200 miles last winter.", out bool didRepair);
            add("skill_awareness_numeric_visible_repair", didRepair && !ContainsSpokenNumericSkillLevel(repaired) && repaired.Contains("200 miles"), "Only explicit game-stat skill phrasing is repaired; ordinary in-world quantities remain.", repaired);
            return results;
        }
    }
}
