using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly object PoliticalProfileLock = new object();

        private static GovernmentPoliticalProfile EnsurePoliticalProfile(string campaignId, string heroId,
            Dictionary<string, object> profile, Dictionary<string, object> traits)
        {
            lock (PoliticalProfileLock)
            {
                string timeline = ReadString(profile, "politicalTimelineId", "main");
                // Absence of a client revision means first synchronization, not permission
                // to read an evolved future snapshot after an older save was restored.
                var stored = ReadJsonObject(CharacterFile(campaignId, heroId, "politics", "revision-1-" + SafeMemoryKey(timeline) + ".json"));
                GovernmentPoliticalProfile beliefs = stored.Count == 0 ? null : JsonSerializer.Deserialize<GovernmentPoliticalProfile>(Json.Serialize(stored));
                if (beliefs != null && !GovernmentPoliticalBeliefs.Valid(beliefs)) throw new InvalidOperationException("Invalid persisted political profile: " + heroId);
                // An applied campaign snapshot is accepted only if it is one of this character's
                // previously issued revisions. A future branch cannot overwrite a restored save.
                var applied = ReadDictionary(profile, "appliedPoliticalProfile");
                if (applied != null)
                {
                    var requested = JsonSerializer.Deserialize<GovernmentPoliticalProfile>(Json.Serialize(applied));
                    var revision = requested == null ? null : ReadJsonObject(CharacterFile(campaignId, heroId, "politics", "revision-" + requested.Revision + "-" + SafeMemoryKey(requested.TimelineId) + ".json"));
                    if (requested == null || !GovernmentPoliticalBeliefs.Valid(requested) || requested.CampaignId != campaignId || requested.HeroId != heroId
                        || revision == null || CanonicalJson(revision) != CanonicalJson(applied))
                        throw new InvalidOperationException("The applied political revision has not been issued for this character.");
                    beliefs = requested;
                    if (beliefs.TimelineId != timeline)
                    {
                        beliefs.TimelineId = timeline;
                        WritePoliticalRevision(campaignId, heroId, beliefs);
                    }
                }
                if (beliefs == null)
                {
                    if (traits.Count == 0) traits = BuildTraitDocument(profile);
                    EnsureTraitPercentageData(traits, heroId);
                    var percentages = CoreTraitKeys.ToDictionary(key => key, key => PersonalityTraitPercentage(traits, key), StringComparer.Ordinal);
                    beliefs = GovernmentPoliticalBeliefs.Seed(campaignId, heroId, timeline, percentages,
                        PoliticalStationContext(profile), ReadBool(profile, "isChild", false));
                    WritePoliticalRevision(campaignId, heroId, beliefs);
                }
                return beliefs;
            }
        }

        private static void WritePoliticalRevision(string campaignId, string heroId, GovernmentPoliticalProfile beliefs)
        {
            lock (PoliticalProfileLock)
            {
                // A replay from the same applied base reuses its already-issued result.
                // A divergent branch receives a new identity and cannot overwrite one.
                while (true)
                {
                    string path = CharacterFile(campaignId, heroId, "politics", "revision-" + beliefs.Revision + "-" + SafeMemoryKey(beliefs.TimelineId) + ".json");
                    var document = TryParseJsonObject(JsonSerializer.Serialize(beliefs));
                    var existing = ReadJsonObject(path);
                    if (existing.Count == 0)
                    {
                        WriteJsonObject(path, document);
                        WriteJsonObject(CharacterFile(campaignId, heroId, "politics", SafeMemoryKey(beliefs.TimelineId) + ".json"), document);
                        return;
                    }
                    if (CanonicalJson(existing) == CanonicalJson(document)) return;
                    beliefs.Revision++;
                }
            }
        }

        private static Dictionary<ReignGovernmentPlank, double> PoliticalStationContext(Dictionary<string, object> profile)
        {
            var signals = new Dictionary<ReignGovernmentPlank, List<double>>();
            void Add(ReignGovernmentPlank key, double value, bool applies)
            { if (!applies) return; if (!signals.ContainsKey(key)) signals[key] = new List<double>(); signals[key].Add(value); }
            string occupation = ReadString(profile, "occupation", "").ToLowerInvariant();
            bool merchant = occupation == "merchant", artisan = occupation == "artisan";
            bool rural = occupation == "headman" || occupation == "ruralnotable" || occupation == "rural_notable";
            bool notable = ReadBool(profile, "isNotable", false), governor = ReadBool(profile, "isGovernor", false);
            Add(ReignGovernmentPlank.RoyalAuthority, 1, ReadBool(profile, "isRulingClanMember", false));
            Add(ReignGovernmentPlank.RepresentativeAuthority, .5, ReadBool(profile, "isCivicRepresentative", false));
            Add(ReignGovernmentPlank.PopularWelfare, .5, rural);
            Add(ReignGovernmentPlank.Trade, 1, merchant || artisan);
            Add(ReignGovernmentPlank.Agriculture, 1, rural);
            Add(ReignGovernmentPlank.Infrastructure, .5, governor || artisan);
            Add(ReignGovernmentPlank.MilitaryStrength, .5, ReadBool(profile, "isArmyLeader", false) || ReadBool(profile, "isWarPartyLeader", false));
            Add(ReignGovernmentPlank.Expansion, .5, ReadBool(profile, "isArmyLeader", false));
            Add(ReignGovernmentPlank.Peace, .5, merchant);
            Add(ReignGovernmentPlank.Security, .5, governor || occupation == "headman");
            Add(ReignGovernmentPlank.NoblePrivilege, 1, ReadBool(profile, "isLandholdingLord", false));
            Add(ReignGovernmentPlank.NoblePrivilege, -.5, notable);
            Add(ReignGovernmentPlank.ClanPrivilege, .5, ReadBool(profile, "isClanLeader", false));
            Add(ReignGovernmentPlank.LocalAutonomy, .5, notable);
            Add(ReignGovernmentPlank.Espionage, .5, ReadBool(profile, "isSpymaster", false));
            return signals.ToDictionary(x => x.Key, x => x.Value.Average());
        }

        private static string RequiredPoliticalPrompt(string heroId, Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            var document = ReadDictionary(characteristics, "politicalProfile");
            GovernmentPoliticalProfile beliefs = document == null ? null : JsonSerializer.Deserialize<GovernmentPoliticalProfile>(Json.Serialize(document));
            if (beliefs == null)
            {
                var traits = ReadDictionary(characteristics, "traits") ?? BuildTraitDocument(profile);
                EnsureTraitPercentageData(traits, heroId);
                string campaign = ReadString(profile, "campaignId", "");
                // Pure offline prompt fixtures have no campaign. Every real character uses
                // the canonical persisted construction, including a newly encountered resident.
                beliefs = campaign.Length > 0 ? EnsurePoliticalProfile(campaign, heroId, profile, traits)
                    : GovernmentPoliticalBeliefs.Seed("prompt_fixture", heroId,
                    ReadString(profile, "politicalTimelineId", "main"), CoreTraitKeys.ToDictionary(x => x, x => PersonalityTraitPercentage(traits, x)),
                    PoliticalStationContext(profile), ReadBool(profile, "isChild", false));
            }
            var text = new StringBuilder();
            text.AppendLine("REQUIRED PRIVATE POLITICAL BELIEFS — " + heroId + " — revision " + beliefs.Revision + " — timeline " + beliefs.TimelineId);
            text.AppendLine("These values belong only to this character. Use them throughout ordinary and political reasoning, without forcing politics into unrelated conversation. Never disclose scores, hidden relations or another character's private beliefs. A promise can outweigh a preference without rewriting a belief.");
            text.AppendLine("For an actual conversation only: if a new substantive reason truly changes your political conviction, politicalReflection may contain at most two {value,stanceChange,importanceChange,evidenceQuote,sourceQuote} records. Change either stance OR importance by exactly one point. evidenceQuote must exactly quote your own visible acknowledgment of this changed belief; sourceQuote must exactly quote the new reason in the player's actual words. Empty is normal. Mere agreement, payment, repetition, compliance, bargaining and retelling old news do not change convictions. Never name scores or this mechanism in visible speech.");
            if (beliefs.Formative) text.AppendLine("These are formative childhood values; express them at the character's actual age. They confer no political office.");
            text.AppendLine("Priority order (stance -100 opposed to +100 supportive; importance 1..100):");
            foreach (var row in GovernmentPoliticalBeliefs.Priorities(beliefs)) text.Append(row.Value).Append(':').Append(row.Stance).Append('/').Append(row.Importance).Append(';');
            text.AppendLine();
            string office = ReadString(profile, "governmentOfficeTitle", "");
            if (office.Length > 0) text.AppendLine("Verified current office: " + office + ". Use this title naturally only when the observer knows the office and identity; a sovereign's title takes precedence.");
            return text.ToString();
        }

        private static Dictionary<string, object> PoliticalProfilesSync(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", ""), timeline = ReadString(payload, "timelineId", "main");
            var heroes = ReadDictionaryList(payload, "heroes");
            if (campaign.Length == 0 || heroes.Count > 64) return TestDict("ok", false, "error", "A campaign and at most 64 named characters are required.");
            var profiles = new List<object>();
            foreach (var hero in heroes)
            {
                string id = ReadString(hero, "heroStringId", "");
                if (id.Length == 0 || ReadBool(hero, "isPlayer", false)) continue;
                var native = ReadJsonObject(CharacterFile(campaign, id, "profile.json"));
                foreach (var pair in hero) native[pair.Key] = pair.Value;
                native["politicalTimelineId"] = timeline;
                var traits = ReadJsonObject(CharacterFile(campaign, id, "traits.json"));
                var political = EnsurePoliticalProfile(campaign, id, native, traits);
                lock (PoliticalProfileLock)
                {
                    political = ApplyPoliticalWorldEvidence(campaign, id, political, ReadDictionaryList(hero, "politicalEvents"), ReadDouble(payload, "worldDay", -1));
                    political = ApplyAcceptedPoliticalReflections(campaign, id, political, ReadDouble(payload, "worldDay", -1));
                    if (political.Formative && !ReadBool(native, "isChild", false))
                    {
                        political.Formative = false;
                        political.Revision++;
                        WritePoliticalRevision(campaign, id, political);
                    }
                }
                profiles.Add(TryParseJsonObject(JsonSerializer.Serialize(political)));
                // Publish only the snapshot acknowledged in the inbound campaign state.
                // The newly seeded response is applied by the client before its next turn.
                native.Remove("politicalEvents");
                WriteJsonObject(CharacterFile(campaign, id, "profile.json"), native);
            }
            return TestDict("ok", true, "campaignId", campaign, "timelineId", timeline, "profiles", profiles);
        }

        private static List<Dictionary<string, object>> RequiredPoliticalMessages(Dictionary<string, object> request,
            List<Dictionary<string, object>> source)
        {
            if (source.Count == 0) return source; // Context-capacity estimation is not a character turn.
            string campaign = ReadString(request, "campaignId", "");
            if (campaign.Length == 0) return source;
            var authors = ReadDictionaryList(request, "politicalAuthors").ToList();
            string single = ReadFirstString(request, "heroStringId", "speakerHeroStringId", "heroId", "characterId");
            if (single.Length > 0 && !authors.Any(x => CharacterIdFrom(x) == single))
            {
                var profile = ReadDictionary(request, "hero") ?? ReadJsonObject(CharacterFile(campaign, single, "profile.json"));
                if (profile.Count == 0) throw new InvalidOperationException("Political context is awaiting the named character's native profile: " + single);
                authors.Add(profile);
            }
            var messages = source.Select(CloneDictionary).ToList();
            foreach (var author in authors.GroupBy(CharacterIdFrom).Select(x => x.First()))
            {
                string id = CharacterIdFrom(author); if (id.Length == 0 || ReadBool(author, "isPlayer", false)) continue;
                var profile = CloneDictionary(author); profile["campaignId"] = campaign;
                var traits = ReadJsonObject(CharacterFile(campaign, id, "traits.json"));
                var beliefs = EnsurePoliticalProfile(campaign, id, profile, traits);
                string block = RequiredPoliticalPrompt(id, profile, TestDict("politicalProfile", TryParseJsonObject(JsonSerializer.Serialize(beliefs))));
                if (messages.Any(x => ReadString(x, "content", "").Contains(block, StringComparison.Ordinal))) continue;
                // Append complete required context. Never truncate history/personality to
                // make room or treat another author's private values as observer knowledge.
                messages.Add(TestDict("role", "user", "content", block));
            }
            return messages;
        }
    }
}
