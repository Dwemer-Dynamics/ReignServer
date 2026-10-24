using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.World
{
    public static class ReignNewsCategories
    {
        public const string Captures = "announcementCapturesEnabled";
        public const string Sentences = "announcementSentencesEnabled";
        public const string Executions = "announcementExecutionsEnabled";
        public const string RulerOutcomes = "announcementRulerOutcomesEnabled";
        public const string ClanRulings = "announcementClanRulingsEnabled";
        public const string ForeignRulings = "announcementForeignRulingsEnabled";
        public const string WorldEvents = "announcementWorldEventsEnabled";
        public const string Diplomacy = "announcementDiplomacyEnabled";
        public const string Government = "announcementGovernmentEnabled";
        public const string Master = "announcementPopupsEnabled";
        public static readonly IReadOnlyDictionary<string, string> Switches = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Master] = "Town-crier popups",
            [Captures] = "Captures of rulers and rebellion leaders",
            [Sentences] = "Execution sentences",
            [Executions] = "Executions",
            [RulerOutcomes] = "Ruler settlements, releases and rescues",
            [ClanRulings] = "Rulings between noble houses",
            [ForeignRulings] = "Disputes between realms",
            [WorldEvents] = "Major world events, such as famine and golden ages",
            [Diplomacy] = "Diplomatic decisions, including war and peace",
            [Government] = "Government proceedings from all kingdoms"
        };
        public static bool IsCategory(string category) => category != Master && category != null && Switches.ContainsKey(category);
        public static bool CaptureQualifies(bool isRuler, bool isRebellionLeader) => isRuler || isRebellionLeader;
    }

    public sealed class ReignNewsOptions
    {
        private readonly Func<string, bool> _read;
        public ReignNewsOptions(Func<string, bool>? read = null) { _read = read ?? (_ => true); }
        public bool Preference(string key) => ReignNewsCategories.Switches.ContainsKey(key) && _read(key);
        public bool Allows(string category) => ReignNewsCategories.IsCategory(category) && Preference(ReignNewsCategories.Master) && Preference(category);
    }

    // The record contains public facts captured at publication, never a live lookup of an old ruler's title.
    public sealed class ReignNewsArticle
    {
        public string Id = "", Category = "", Title = "", Headline = "", Body = "", Consequences = "";
        public string ActorId = "", ActorName = "", ActorRealmId = "", ActorRealmName = "", ActorCaption = "";
        public string TargetId = "", TargetName = "", TargetRealmId = "", TargetRealmName = "", TargetCaption = "";
        public double WorldDay;
        public bool Positive;
        public Reign.Core.Contracts.Government.GovernmentNewsEdition? GovernmentNews;
        public bool IsValid => !string.IsNullOrWhiteSpace(Id) && ReignNewsCategories.IsCategory(Category)
            && !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Body)
            && !double.IsNaN(WorldDay) && !double.IsInfinity(WorldDay);
        public string Text => Title + ": " + Body + (string.IsNullOrWhiteSpace(Consequences) ? "" : "\n" + Consequences);
    }

    public sealed class ReignNewsState
    {
        public int Version = 1;
        public List<ReignNewsArticle> Pending = new List<ReignNewsArticle>();
        public List<string> Seen = new List<string>();
        public Dictionary<string, string> CaptivityEpisodes = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool Publish(ReignNewsArticle article, ReignNewsOptions options, bool optionsConfirmed)
        {
            if (article?.IsValid != true || Seen.Contains(article.Id) || Pending.Any(p => p.Id == article.Id)) return false;
            Seen.Add(article.Id);
            if (Seen.Count > 4096) Seen.RemoveRange(0, Seen.Count - 4096);
            // A notice arriving before settings are known waits; an explicitly disabled notice never becomes a backlog.
            if (!optionsConfirmed || options.Allows(article.Category)) Pending.Add(article);
            return true;
        }
        public void ApplyOptions(ReignNewsOptions options) => Pending.RemoveAll(p => !options.Allows(p.Category));
        public bool Acknowledge(string id)
        {
            if (Pending.Count == 0 || Pending[0].Id != id) return false;
            Pending.RemoveAt(0);
            return true;
        }
        public void NormalizeAfterLoad()
        {
            if (Version != 1) throw new InvalidOperationException("Unsupported town-crier save format.");
            Pending = (Pending ?? new List<ReignNewsArticle>()).Where(p => p?.IsValid == true)
                .GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            Seen = (Seen ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
            if (Seen.Count > 4096) Seen = Seen.Skip(Seen.Count - 4096).ToList();
            CaptivityEpisodes = CaptivityEpisodes ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    // Publication-time facts only; this formatter never resolves current campaign state.
    public sealed class ReignForeignRuling
    {
        public string Ruler = "", Realm = "", Stance = "";
        public string[] Petitioners = Array.Empty<string>();
        public int Before, After;
    }

    public static class ReignNewsCopy
    {
        public static string AnnouncementReport(string summary, string terms, string actor, string actorStatement,
            string target, string targetStatement, bool isNews)
        {
            var paragraphs = new List<string> { summary, terms };
            if (!isNews)
            {
                if (!string.IsNullOrWhiteSpace(actorStatement))
                    paragraphs.Add(Name(actor, "The announcing ruler") + " gave this reason: “" + actorStatement.Trim() + "”");
                if (!string.IsNullOrWhiteSpace(targetStatement))
                    paragraphs.Add(Name(target, "The other ruler") + " gave this reason: “" + targetStatement.Trim() + "”");
            }
            return string.Join("\n\n", paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        }

        public static string Name(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        public static string Sentence(string value)
        {
            value = Regex.Replace(value ?? "", @"\s+", " ").Trim();
            return value.Length == 0 || ".!?…".Contains(value[value.Length - 1].ToString()) ? value : value + ".";
        }
        public static string ClanRuling(string ruler, string clanA, string clanB, string winningSide, bool mediated)
        {
            ruler = Name(ruler, "The ruler"); clanA = Name(clanA, "the first house"); clanB = Name(clanB, "the other house");
            if (mediated) return ruler + " has reconciled " + clanA + " and " + clanB + ". Both houses have accepted an accord, bringing their quarrel to an end.";
            if (winningSide != "a" && winningSide != "b") return "The court has heard both houses, but a confirmed ruling has not reached the heralds.";
            string winner = winningSide == "a" ? clanA : clanB, loser = winningSide == "a" ? clanB : clanA;
            return ruler + " has ruled in favor of " + winner + ". " + loser + " leaves court embittered, and the feud between the houses remains unresolved.";
        }
        public static string ForeignRulingSummary(string headline, string channel, ReignForeignRuling origin, ReignForeignRuling target)
        {
            var paragraphs = new List<string>();
            foreach (var side in new[] { origin, target })
            {
                // A named affected ruler is not evidence that they made a decision.
                if (side.Stance != "support_lords" && !ForeignRefused(side.Stance) && side.Stance != "awaiting_player") continue;
                var other = ReferenceEquals(side, origin) ? target : origin;
                string neighbor = Name(other.Realm, "the other realm");
                string request = ForeignPetitionRequest(headline, channel).Replace("the other realm", neighbor);
                string[] names = (side.Petitioners ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                string petitioners = names.Length == 0 ? "nobles in " + Name(side.Realm, "that realm")
                    : names.Length == 1 ? names[0] : string.Join(", ", names.Take(names.Length - 1)) + " and " + names[names.Length - 1];
                paragraphs.Add(ForeignSubject(side) + " " + ForeignDecision(side.Stance) + " the " + request
                    + " put forward by " + petitioners + "." + (request.Contains(neighbor) ? "" : " The petition concerned relations with " + neighbor + "."));
            }
            return paragraphs.Count > 0 ? string.Join("\n\n", paragraphs)
                : "A petition concerning " + Name(origin.Realm, "one realm") + " and " + Name(target.Realm, "its neighbor") + " has been reported, but no confirmed ruling accompanies this report.";
        }

        public static string ForeignRulingConsequences(string polarity, string channel, ReignForeignRuling origin, ReignForeignRuling target)
        {
            var paragraphs = new List<string>();
            var refused = new[] { origin, target }.Where(s => ForeignRefused(s.Stance)).ToList();
            if (refused.Count > 0)
                paragraphs.Add(refused.Count == 2
                    ? "Both refusals angered the petitioning nobles, damaging their relations with their own ruler."
                    : "The refusal angered the petitioning nobles of " + Name(refused[0].Realm, "that realm") + ", damaging their relations with " + Name(refused[0].Ruler, "their ruler") + ".");
            foreach (var side in new[] { origin, target })
            {
                // Observed effects can reach the other realm without a second decision.
                if (side.Stance != "support_lords" && !ForeignRefused(side.Stance) && side.After == side.Before) continue;
                var other = ReferenceEquals(side, origin) ? target : origin;
                string direction = "In " + Name(side.Realm, "the realm") + ", ";
                string neighbor = Name(other.Realm, "the other realm");
                if (side.After == side.Before)
                    paragraphs.Add(direction + "calls for confrontation or cooperation with " + neighbor + " are unchanged.");
                else if (polarity == "peaceful" && side.After < side.Before)
                    paragraphs.Add(direction + "support for cooperation with " + neighbor + " has grown.");
                else
                    paragraphs.Add(direction + "calls for confrontation with " + neighbor + (side.After > side.Before ? " have grown." : " have eased."));
            }
            // These incidents change sentiment, not native diplomatic agreements or transfers.
            // Subsequent actions have their own confirmed announcements.
            paragraphs.Add(channel == "aid_exchange"
                ? "These rulings did not transfer money, supplies or prisoners, or grant passage. Any such arrangement still requires a separate agreement."
                : channel == "treaty_strain"
                    ? "These rulings did not launch enforcement action or change a treaty. Any further action remains a separate decision."
                    : "These rulings did not declare war, conclude peace or enact an agreement. Any further diplomatic action remains a separate decision.");
            return string.Join("\n\n", paragraphs);
        }

        private static string ForeignSubject(ReignForeignRuling side) => Name(side.Ruler, "The ruler") + " of " + Name(side.Realm, "the realm");
        private static bool ForeignRefused(string stance) => stance == "preserve_relations" || stance == "refuse_court" || stance == "refuse_lords";
        private static string ForeignDecision(string stance) => stance == "support_lords" ? "backed"
            : ForeignRefused(stance) ? "rejected" : stance == "awaiting_player" ? "has yet to rule on" : "has issued no confirmed ruling on";

        private static string ForeignPetitionRequest(string headline, string channel)
        {
            switch (headline)
            {
                case "Border Retaliation": return "demand for a forceful response to frontier provocations";
                case "Contested Pastures": return "request for royal protection in a grazing-rights dispute";
                case "Raid Accusations": return "demand for action over accusations of sheltering cross-border raiders";
                case "Military Demonstration": return "request for frontier troops and an explicit warning";
                case "Caravan Seizure": return "demand for compensation for seized caravans";
                case "Punitive Tariffs": return "demand for retaliation against discriminatory tolls";
                case "Fugitive Dispute": return "demand for a response over a protected fugitive";
                case "Mercenary Sponsorship": return "complaint about alleged support for hostile mercenaries";
                case "Broken Commercial Promise": return "complaint about promised commercial access";
                case "Treaty Insult": return "demand for a response to a public insult";
                case "Smuggling Crisis": return "demand for stronger action against protected smugglers";
                case "Envoy Humiliated": return "demand for a formal response to an envoy's treatment";
                case "Harsh Settlement Demands": return "demand for punitive settlement terms";
                case "Occupied Lands": return "demand for the return of occupied lands or compensation";
                case "Prisoner Mistreatment": return "demand for punishment over the treatment of captured kin";
                case "Indemnity Petition": return "demand for the enemy to pay the costs of the conflict";
                case "War Weariness": return "appeal to end an exhausting war";
                case "Peace Delegation": return "proposal for negotiated peace";
                case "Captured Kin": return "appeal for reconciliation and a prisoner exchange";
                case "Treasury Exhaustion": return "warning about the cost of continued war";
                case "Market Access": return "request for protected access to foreign markets";
                case "Caravan Road Compact": return "proposal for common rules on caravan roads";
                case "Seasonal Fair": return "proposal for a shared fair and temporary commercial privileges";
                case "Tariff Reconciliation": return "request for talks to lower punitive tolls";
                case "Shared Border Patrols": return "proposal for cooperation against bandits and raiders";
                case "Non-Aggression Petition": return "request for restraint by border forces";
                case "Common Threat": return "proposal for defensive cooperation against a common threat";
                case "Mutual Recognition": return "proposal for recognition of borders and titles";
                case "Famine Relief": return "appeal for assistance to a neighboring population";
                case "Prisoner Exchange": return "proposal for an organized prisoner exchange";
                case "Pilgrimage Access": return "request for protected cross-border passage";
                case "Material Assistance": return "proposal for a limited subsidy or supply arrangement with the other realm";
                default: return channel == "aid_exchange" ? "proposal for assistance between the realms" : "petition concerning the other realm";
            }
        }
        public static string WorldEvent(string archetype, string realm, string otherRealm, string formerRuler, string heir, bool ended, bool dissolved)
        {
            realm = Name(realm, "the realm");
            if (ended && dissolved) return "The realm of " + realm + " has ceased to exist. Its former kingdom-wide conditions no longer apply.";
            if (ended)
            {
                switch (archetype)
                {
                    case "famine": return "The famine in " + realm + " has passed. Village food production has returned to its usual level.";
                    case "pestilence": return "The pestilence in " + realm + " has abated. Villages are no longer losing households to this outbreak.";
                    case "golden_age": return "The golden age of building in " + realm + " has ended. Construction has returned to its usual pace.";
                    case "bountiful_harvest": return "The exceptional harvest in " + realm + " has ended. Village food production has returned to its usual level.";
                    case "population_boom": return "The surge in village growth across " + realm + " has settled back to its usual pace.";
                    case "crime_wave": return "The crime wave in " + realm + " has subsided. It no longer places an extra strain on the realm's security.";
                    case "trade_collapse": return "The trade collapse in " + realm + " has ended. Its drain on the wealth of towns and castles has ceased.";
                    case "trade_boom": return "The trade boom in " + realm + " has ended. Towns and castles no longer receive its extra prosperity.";
                    case "construction_stagnation": return "The building delays in " + realm + " have ended. Work can proceed at its usual pace.";
                    case "political_unrest": return "The recent unrest in " + realm + " has subsided. Its added strain on loyalty has ended.";
                    case "law_and_order": return "The period of strengthened law and order in " + realm + " has ended. Its extra security gains have ceased.";
                    case "national_unity": return "The wave of national unity in " + realm + " has passed. Its extra gains in loyalty have ended.";
                    default: return "The recent upheaval in " + realm + " has ended.";
                }
            }
            switch (archetype)
            {
                case "famine": return "Famine has struck " + realm + ". Its villages are producing less food, placing the realm's supplies under strain.";
                case "pestilence": return "Pestilence is spreading through " + realm + ". Village households are dwindling as the outbreak takes its toll.";
                case "crime_wave": return "A wave of crime has spread across " + realm + ", undermining security in its towns and castles.";
                case "trade_collapse": return "Trade has faltered across " + realm + ". Towns and castles are losing prosperity as commerce contracts.";
                case "construction_stagnation": return "Building work has slowed throughout " + realm + ". Projects in its towns and castles are taking longer to complete.";
                case "political_unrest": return "Unrest is spreading through " + realm + ". Loyalty is weakening in its towns and castles.";
                case "bountiful_harvest": return "A bountiful harvest has blessed " + realm + ". Its villages are producing more food, strengthening the realm's supplies.";
                case "population_boom": return "The villages of " + realm + " are flourishing. Their households are growing faster than usual.";
                case "law_and_order": return "Law and order are strengthening across " + realm + ". Security is improving in its towns and castles.";
                case "trade_boom": return "Trade is flourishing across " + realm + ", bringing greater prosperity to its towns and castles.";
                case "golden_age": return "A golden age of building has begun in " + realm + ". Construction is advancing more swiftly throughout its towns and castles.";
                case "national_unity": return "A spirit of unity has taken hold in " + realm + ". Loyalty is strengthening across its towns and castles.";
                case "border_crisis": return "A border crisis has brought " + realm + " and " + Name(otherRealm, "a neighboring realm") + " into open war.";
                case "grand_reconciliation": return realm + " and " + Name(otherRealm, "a neighboring realm") + " have made peace, bringing their war to an end.";
                case "orderly_succession": return Name(formerRuler, "The former ruler") + " has abdicated the throne of " + realm + ". " + Name(heir, "The ruling house's heir") + " has succeeded to the crown.";
                default: return "A major event has been reported in " + realm + ".";
            }
        }
    }
}
