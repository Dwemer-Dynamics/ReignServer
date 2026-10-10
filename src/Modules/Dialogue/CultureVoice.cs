using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string CulturalVoicePrompt(Dictionary<string, object> profile)
        {
            string culture = SpeakerDialogueCulture(profile);
            const string foundation = "Make the visible speech or letter body carry this speaker's cultural cadence through syntax, word choice, and an occasional fitting idiom. "
                + "In a substantial reply, give one or two recognizable touches; a short or urgent line need not force one. "
                + "Culture is a layer on this person's established voice, education, occupation, travel, audience, and mood. "
                + "Keep it readable: no blanket phonetic spelling, broken grammar, repeated catchphrase, invented foreign word, or unsupported custom. "
                + "Do not put all the cultural flavor in narrator actions, props, or scenery while the spoken words remain generic. "
                + "Avoid contemporary counseling or essay language when a concrete, characterful reply would say the same thing. Do not name real-world peoples or languages.";
            switch (culture)
            {
                case "empire":
                    return foundation + " Imperial voice: measured clauses, precise distinctions, and a habit of stating what follows from a claim. Duty, law, office, and legitimacy can supply words when relevant. A frontier soldier or market worker can be plainspoken; do not make every Imperial a magistrate.";
                case "vlandia":
                    return foundation + " Vlandian voice: firm, practical clauses that put promises and terms plainly. Oaths, service, land, inheritance, and bargains can supply words when relevant. A noble may be courtly and a soldier blunt; do not turn every answer into a contract.";
                case "sturgia":
                    return foundation + " Sturgian voice: grounded sentences with weight behind plain words and occasional dry understatement. Kin, hospitality, river trade, winter stores, and remembered obligations can supply words when relevant. Keep this land-and-river voice distinct from Nord seafaring speech.";
                case "battania":
                    return foundation + " Battanian voice: pointed wit, an oblique answer that quickly finds its point, or one vivid comparison when this person would use it. Clan ties and woodland life can supply words when relevant. Do not make every reply a riddle or a tale.";
                case "aserai":
                    return foundation + " Aserai voice: measured courtesy, a deliberate turn from welcome to terms, and an occasional compact image. Hospitality, lineage, and reputation can supply words when relevant. Courtesy need not mean submission; do not presume a religious formula.";
                case "khuzait":
                    return foundation + " Khuzait voice: short, decisive clauses; name where one stands, then the next move or consequence. Favor concrete ground, distance, routes, reins, alliances, or a promise kept when the exchange warrants it. Let an intimate admission remain direct rather than turn into a long explanation of feelings. Town dwellers are not all mounted nomads.";
                case "nord":
                    return foundation + " Nord voice: plain, forceful sentences and wry understatement; test a claim against shared work or risk. Ships, crews, voyages, and earned reputation can supply words when relevant. Keep this seafaring voice distinct from Sturgian speech; no forced archaic spelling.";
                default:
                    return foundation + " No specific cultural speech pattern is established for this character; follow only the supplied identity and personal voice.";
            }
        }

        private static string CulturalVoiceTurnReminder(Dictionary<string, object> profile, bool written)
        {
            string culture = SpeakerDialogueCulture(profile);
            if (string.IsNullOrWhiteSpace(culture)) return string.Empty;
            return "CULTURAL VOICE CHECK FOR THIS SPEAKER\nIn the visible " + (written ? "letter body" : "spoken words")
                + ", apply this " + culture + " speaker's cadence and wording from the character foundation. "
                + "A substantial reply should contain one or two natural, distinctive touches while preserving this individual's voice. "
                + "Narrator details alone do not count. Do not copy old neutral phrasing or force a stock idiom.";
        }

        private static string SpeakerDialogueCulture(Dictionary<string, object> profile)
        {
            string cultureId = ReadString(profile, "cultureId", "");
            return CanonicalDialogueCulture(string.IsNullOrWhiteSpace(cultureId)
                ? ReadString(profile, "cultureName", "") : cultureId);
        }

        private static string CanonicalDialogueCulture(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "empire": case "imperial": return "empire";
                case "vlandia": case "vlandian": return "vlandia";
                case "sturgia": case "sturgian": return "sturgia";
                case "battania": case "battanian": return "battania";
                case "aserai": return "aserai";
                case "khuzait": return "khuzait";
                case "nord": case "nords": return "nord";
                default: return string.Empty;
            }
        }
    }
}
