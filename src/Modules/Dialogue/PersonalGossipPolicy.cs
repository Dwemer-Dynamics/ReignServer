using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PersonalGossipVersion = "personal-gossip-v1";
        private static Dictionary<string, object> GossipFields(params object[] fields)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < fields.Length; i += 2) result[(string)fields[i]] = fields[i + 1];
            return result;
        }

        private static Dictionary<string, object> DefaultPersonalGossipSettings() => GossipFields(
            "enabled", true, "policyVersion", PersonalGossipVersion,
            "friendThreshold", 30, "dislikeThreshold", -10, "familyThreshold", 0,
            "namedRecipientMultiplier", 4d, "localDelayHours", 1d,
            "maximumCandidates", 8, "maximumOriginalsPerConversation", 2,
            "maximumOutgoingPerDay", 3, "maximumOutwardHops", 2, "expiryDays", 14d);

        private static Dictionary<string, object> PersonalGossipSettings()
        {
            var settings = DefaultPersonalGossipSettings();
            var configured = ReadDictionary(LoadSettings(), "personalGossip");
            if (configured != null)
                foreach (string key in settings.Keys.ToList())
                    if (configured.ContainsKey(key)) settings[key] = configured[key];
            // A future policy requires an explicit implementation, not guessed semantics.
            if (ReadString(settings, "policyVersion", "") != PersonalGossipVersion) settings["enabled"] = false;
            settings["friendThreshold"] = Clamp(ReadInt(settings, "friendThreshold", 30), 1, 100);
            settings["dislikeThreshold"] = Clamp(ReadInt(settings, "dislikeThreshold", -10), -100, -1);
            settings["familyThreshold"] = Clamp(ReadInt(settings, "familyThreshold", 0), 0, 100);
            settings["namedRecipientMultiplier"] = ClampDouble(ReadDouble(settings, "namedRecipientMultiplier", 4), 1, 100);
            settings["localDelayHours"] = ClampDouble(ReadDouble(settings, "localDelayHours", 1), 1, 24);
            settings["maximumCandidates"] = Clamp(ReadInt(settings, "maximumCandidates", 8), 1, 8);
            settings["maximumOriginalsPerConversation"] = Clamp(ReadInt(settings, "maximumOriginalsPerConversation", 2), 1, 2);
            settings["maximumOutgoingPerDay"] = Clamp(ReadInt(settings, "maximumOutgoingPerDay", 3), 1, 3);
            settings["maximumOutwardHops"] = Clamp(ReadInt(settings, "maximumOutwardHops", 2), 1, 2);
            settings["expiryDays"] = ClampDouble(ReadDouble(settings, "expiryDays", 14), 1, 14);
            return settings;
        }

        private static int GossipPercent(double value, int cap = 100) =>
            (int)Math.Round(ClampDouble(double.IsFinite(value) ? value : 0, 0, cap), MidpointRounding.AwayFromZero);
        private static double GossipTrait(Dictionary<string, object> traits, string key) => RelationshipTraitPercent(traits, key);
        private static int GossipFeeling(Dictionary<string, object> signal, string key) =>
            new[] { 0, 25, 50, 75, 100 }.Contains(ReadInt(signal, key, 0)) ? ReadInt(signal, key, 0) : 0;
        private static bool GossipEmotionEligible(double emotion, Dictionary<string, object> traits) =>
            emotion >= 50 || emotion >= 25 && (GossipTrait(traits, "sociability") >= 75 || GossipTrait(traits, "curiosity") >= 75);
        private static int GossipShareChance(double emotion, Dictionary<string, object> t) => GossipPercent(
            .35 * emotion + .25 * GossipTrait(t, "sociability") + .15 * GossipTrait(t, "impulsiveness")
            + .10 * GossipTrait(t, "pride") + .15 * GossipTrait(t, "curiosity") - 15, 85);
        private static int GossipNegativeChance(Dictionary<string, object> t, double hurt, double affinity,
            double grievance, bool knownJealousy, bool knownEnvy) => GossipPercent(
            .35 * GossipTrait(t, "vengefulness") + .20 * Math.Max(Math.Max(0, -affinity), grievance)
            + .15 * hurt + .10 * GossipTrait(t, "pride") + .10 * (100 - GossipTrait(t, "compassion"))
            + (knownJealousy ? .05 * GossipTrait(t, "jealousy") : 0)
            + (knownEnvy ? .05 * GossipTrait(t, "envy") : 0) - 15
            - .20 * GossipTrait(t, "loyalty") * Math.Max(0, affinity) / 100, 85);

        private static string GossipPrivacy(string value) => new[] { "public", "personal", "intimate", "secret" }
            .Contains(value) ? value : "personal";
        private static int GossipDetailChance(Dictionary<string, object> t, string privacy, double emotion,
            double recipientAffinity, double confidenceBond, bool ownCompromising, bool promised, int negativeChance)
        {
            privacy = GossipPrivacy(privacy);
            if (promised) privacy = "secret";
            if (privacy == "public") return 100;
            double basis = privacy == "secret" ? 15 : privacy == "intimate" ? 40 : 65;
            double discretion = .45 * GossipTrait(t, "tact") + .30 * GossipTrait(t, "discipline") + .25 * GossipTrait(t, "empathy");
            return GossipPercent(basis - .45 * discretion + .20 * GossipTrait(t, "impulsiveness")
                + .15 * emotion + .15 * Math.Max(0, recipientAffinity) - .30 * GossipTrait(t, "loyalty") * confidenceBond / 100
                - (ownCompromising ? .20 * GossipTrait(t, "shame") : 0) + .30 * negativeChance
                - (promised ? .30 * GossipTrait(t, "honesty") : 0), 95);
        }

        private static int GossipDistortionChance(Dictionary<string, object> t, double hurt) => GossipPercent(
            (100 - GossipTrait(t, "honesty")) * (.40 * GossipTrait(t, "vengefulness") + .30 * hurt
                + .20 * GossipTrait(t, "pride") + .10 * GossipTrait(t, "impulsiveness")) / 100, 85);
        private static int GossipDistortionScore(Dictionary<string, object> t, double hurt) => GossipPercent(
            .60 * (100 - GossipTrait(t, "honesty")) + .25 * GossipTrait(t, "vengefulness") + .15 * hurt);
        private static string GossipDistortionSeverity(int score, int roll) => score < 40
            ? (roll <= 80 ? "loaded_exaggeration" : "false_escalation")
            : score < 70 ? (roll <= 45 ? "loaded_exaggeration" : roll <= 90 ? "false_escalation" : "invented_detail")
            : (roll <= 20 ? "loaded_exaggeration" : roll <= 65 ? "false_escalation" : "invented_detail");

        private static int GossipCredibility(double affinity, double socialTrust, string account, int retellings,
            bool corroborated, bool exposedLiar, bool inconsistent, string conclusive = "")
        {
            if (conclusive == "disproved") return 0;
            if (conclusive == "confirmed") return 100;
            int bonus = account == "firsthand" ? 10 : account == "speculation" ? -15 : 0;
            return (int)Math.Round(ClampDouble(40 + .25 * affinity + .20 * (socialTrust - 50) + bonus
                - 10 * retellings + (corroborated ? 15 : 0) - (exposedLiar ? 25 : 0) - (inconsistent ? 20 : 0), 5, 95), MidpointRounding.AwayFromZero);
        }
        private static int GossipRetellChance(int share, int credibility) => GossipPercent(.40 * share * (.25 + .75 * credibility / 100d), 34);
        private static int GossipRaiseChance(double curiosity, double stake, double uncertainty, double feeling) =>
            GossipPercent(10 + .30 * curiosity + .25 * stake + .20 * uncertainty + .15 * feeling, 80);

        private static string GossipHeroId(Dictionary<string, object> person) => ReadFirstString(person, "heroStringId", "heroId", "id");
        private static bool GossipSameId(string a, string b) => !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        private static bool GossipLocal(Dictionary<string, object> a, Dictionary<string, object> b) =>
            GossipSameId(ReadFirstString(a, "settlementId", "currentSettlementId"), ReadFirstString(b, "settlementId", "currentSettlementId"))
            || GossipSameId(ReadString(a, "partyId", ""), ReadString(b, "partyId", ""));
        private static bool GossipFamily(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            string aId = GossipHeroId(a), bId = GossipHeroId(b);
            if (new[] { "fatherId", "motherId", "spouseId" }.Any(k => GossipSameId(ReadString(a, k, ""), bId)
                || GossipSameId(ReadString(b, k, ""), aId))) return true;
            return new[] { "fatherId", "motherId" }.Any(k => GossipSameId(ReadString(a, k, ""), ReadString(b, k, "")));
        }

        private sealed class GossipCandidate
        {
            public string Id;
            public Dictionary<string, object> Person;
            public double Affinity, Grievance, Hurt, BaseWeight, Weight;
            public bool Family, Mentioned, Local, Relevant;
            public string Exclusion = "";
            public Dictionary<string, object> Audit() => GossipFields("id", Id, "affinity", Affinity, "family", Family,
                "mentioned", Mentioned, "local", Local, "relevant", Relevant, "baseWeight", BaseWeight, "weight", Weight, "exclusion", Exclusion);
        }

        private static List<GossipCandidate> GossipRecipients(IEnumerable<GossipCandidate> input, string type,
            double familyMotivation, Dictionary<string, object> policy)
        {
            var candidates = input.ToList();
            foreach (var c in candidates)
            {
                if (c.Exclusion.Length > 0) continue;
                if (!c.Local && !c.Mentioned) { c.Exclusion = "unnamed_remote"; continue; }
                bool relationship = type == "negative"
                    ? c.Affinity <= ReadInt(policy, "dislikeThreshold", -10) && c.Hurt >= 50
                    : c.Affinity >= ReadInt(policy, "friendThreshold", 30)
                        || c.Family && c.Affinity >= ReadInt(policy, "familyThreshold", 0);
                if (!relationship) { c.Exclusion = "relationship_or_motive"; continue; }
                c.BaseWeight = type == "negative" ? 30 + Math.Max(-c.Affinity, c.Grievance)
                    : 10 + Math.Max(0, c.Affinity) + (c.Family ? 25 * familyMotivation / 100 : 0);
                c.BaseWeight += c.Relevant ? 20 : 0;
                c.Weight = c.BaseWeight;
            }
            var eligible = candidates.Where(c => c.Exclusion.Length == 0).OrderByDescending(c => c.Mentioned)
                .ThenByDescending(c => c.BaseWeight).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            foreach (var excess in eligible.Skip(ReadInt(policy, "maximumCandidates", 8))) excess.Exclusion = "candidate_cap";
            eligible = eligible.Take(ReadInt(policy, "maximumCandidates", 8)).ToList();
            double ordinary = eligible.Where(c => !c.Mentioned).Sum(c => c.BaseWeight);
            var named = eligible.Where(c => c.Mentioned).ToList();
            if (ordinary > 0 && named.Count > 0)
            {
                double scale = ReadDouble(policy, "namedRecipientMultiplier", 4) * ordinary / named.Min(c => c.BaseWeight);
                foreach (var c in named) c.Weight = c.BaseWeight * scale;
            }
            return eligible.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        }

        private static GossipCandidate GossipChooseRecipient(List<GossipCandidate> candidates, double draw)
        {
            if (candidates.Count == 0) return null;
            double point = Math.Min(.9999999999999999, Math.Max(0, draw)) * candidates.Sum(c => c.Weight);
            foreach (var candidate in candidates) { point -= candidate.Weight; if (point < 0) return candidate; }
            return candidates.Last();
        }

        private static double GossipTransitDays(Dictionary<string, object> sender, Dictionary<string, object> recipient,
            Dictionary<string, object> policy, bool local)
        {
            if (local) return ReadDouble(policy, "localDelayHours", 1) / 24;
            bool same = GossipSameId(ReadString(sender, "kingdomId", ""), ReadString(recipient, "kingdomId", ""));
            if (!ReadBool(sender, "positionKnown", false) || !ReadBool(recipient, "positionKnown", false)) return same ? 2 : 5;
            double dx = ReadDouble(sender, "positionX", double.NaN) - ReadDouble(recipient, "positionX", double.NaN);
            double dy = ReadDouble(sender, "positionY", double.NaN) - ReadDouble(recipient, "positionY", double.NaN);
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(distance)) return same ? 2 : 5;
            return same ? Math.Min(3, 1 + distance / 80) : Math.Min(7, 3 + distance / 55);
        }

        // Stable stages survive repeated turns and Save Sync without accumulating odds.
        private static double GossipDraw(string topic, string stage) => StableUnit(PersonalGossipVersion + "|" + topic + "|" + stage);
        private static int GossipRoll(string topic, string stage) => Math.Min(100, 1 + (int)(GossipDraw(topic, stage) * 100));
        private static string GossipTopicCategory(string category) => new[] { "family", "relationship", "intimacy", "politics",
            "shared_activity", "gift", "question", "impression", "conversation" }.Contains(category) ? category : "conversation";
        private static string GossipTopicId(string timeline, string speaker, string player, double day, string category, string subject) =>
            "gossip_" + PromptHash(timeline + "|" + speaker + "|" + player + "|" + Math.Floor(day).ToString(CultureInfo.InvariantCulture)
                + "|" + GossipTopicCategory(category) + "|" + subject).Substring(0, 24).ToLowerInvariant();

        private static bool GossipQuoteSupported(string quote, string accessible) => !string.IsNullOrWhiteSpace(quote)
            && quote.Trim().Length >= 8 && (accessible ?? "").IndexOf(quote.Trim(), StringComparison.Ordinal) >= 0;
        private static bool GossipBodyAllowed(string body, string source, string severity)
        {
            if (string.IsNullOrWhiteSpace(body) || Regex.Matches(body, @"\S+").Count > 100 || body.Length > 1400) return false;
            if (Regex.Matches(body, @"[.!?](?:\s|$)").Count > 3) return false;
            // Ordinary generation is not permission to fabricate consequential encounters.
            foreach (string pattern in new[] { @"\b(spent the night|slept (with|together)|made love|shared (a|my|their) bed|kiss(?:ed|ing)?)\b",
                @"\b(making advances|made advances|flirt(?:ed|ing)?|propos(?:ed|al)|promised|threaten(?:ed|ing)?|insult(?:ed|ing)?)\b" })
                if (Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase) && !Regex.IsMatch(source ?? "", pattern, RegexOptions.IgnoreCase)
                    && severity != "invented_detail" && !(severity == "false_escalation" && pattern.Contains("advances"))) return false;
            return !Regex.IsMatch(body, @"\b(?:gossipSignals|sourceIds|traitPercentages|distortionSeverity)\b", RegexOptions.IgnoreCase);
        }
    }
}
