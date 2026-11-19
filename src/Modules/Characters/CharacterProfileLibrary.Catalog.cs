using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        internal static Dictionary<string, object> GenerateCharacterProfileLibrary(string[] args)
        {
            string sandbox = ProfileArg(args, "--sandbox-data", "");
            string reign = ProfileArg(args, "--reign-data", "");
            string courtManifestPath = ProfileArg(args, "--court-manifest", "");
            string naval = ProfileArg(args, "--naval-data", Path.GetFullPath(Path.Combine(sandbox, "..", "..", "NavalDLC", "ModuleData")));
            string output = ProfileArg(args, "--output", CharacterProfileCatalogPath);
            if (ReadString(CampaignFileService.ReadJsonObject(output), "packVersion", "") == NarrativePack)
                throw new InvalidOperationException("The legacy foundation generator cannot overwrite a complete narrative catalog. Generate foundations to a separate path and use the guarded narrative authoring workflow.");
            if (!Directory.Exists(sandbox)) throw new DirectoryNotFoundException("Sandbox ModuleData was not found: " + sandbox);
            if (!Directory.Exists(reign)) throw new DirectoryNotFoundException("Reign ModuleData was not found: " + reign);

            XDocument nativeLords = XDocument.Load(Path.Combine(sandbox, "lords.xml"), LoadOptions.PreserveWhitespace);
            XDocument nativeHeroes = XDocument.Load(Path.Combine(sandbox, "heroes.xml"), LoadOptions.PreserveWhitespace);
            var navalFiles = new[] { "naval_lords.xml", "heroes.xml", "clans.xml" }.Select(file => Path.Combine(naval, file)).ToArray();
            if (navalFiles.Any(File.Exists))
            {
                if (!navalFiles.All(File.Exists)) throw new InvalidOperationException("Incomplete NavalDLC native profile sources.");
                nativeLords.Root.Add(XDocument.Load(navalFiles[0]).Root.Elements().Select(node => new XElement(node)));
                nativeHeroes.Root.Add(XDocument.Load(navalFiles[1]).Root.Elements().Select(node => new XElement(node)));
            }
            XDocument courtLords = XDocument.Load(Path.Combine(reign, "reign_court_lords.xml"), LoadOptions.PreserveWhitespace);
            XDocument courtHeroes = XDocument.Load(Path.Combine(reign, "reign_court_heroes.xml"), LoadOptions.PreserveWhitespace);
            XDocument skills = XDocument.Load(Path.Combine(sandbox, "sandbox_skill_sets.xml"), LoadOptions.PreserveWhitespace);
            XDocument clans = XDocument.Load(Path.Combine(sandbox, "spclans.xml"), LoadOptions.PreserveWhitespace);
            if (navalFiles.All(File.Exists))
            {
                // DLC clan XML also contains updates to existing mercenary clans.
                // Import only missing identities; these unrelated partial updates
                // must not duplicate or erase a Sandbox faction definition.
                var existingClanIds = new HashSet<string>(clans.Root.Elements().Select(node => (string)node.Attribute("id")), StringComparer.OrdinalIgnoreCase);
                clans.Root.Add(XDocument.Load(navalFiles[2]).Root.Elements()
                    .Where(node => !existingClanIds.Contains((string)node.Attribute("id"))).Select(node => new XElement(node)));
            }
            XDocument courtClans = XDocument.Load(Path.Combine(reign, "reign_court_clans.xml"), LoadOptions.PreserveWhitespace);
            Dictionary<string, object> courtManifest = CampaignFileService.ReadJsonObject(courtManifestPath);
            Dictionary<string, Dictionary<string, object>> courtRecords = ReadDictionaryList(courtManifest, "records").ToDictionary(x => ReadString(x, "id", ""), x => x, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, XElement> skillSets = skills.Descendants("SkillSet").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            string coreSkillsPath = Path.GetFullPath(Path.Combine(sandbox, "..", "..", "SandBoxCore", "ModuleData", "sandboxcore_skill_sets.xml"));
            if (File.Exists(coreSkillsPath))
                foreach (XElement core in XDocument.Load(coreSkillsPath).Descendants("SkillSet").Where(x => x.Attribute("id") != null))
                {
                    string coreId = (string)core.Attribute("id");
                    if (!skillSets.ContainsKey(coreId)) skillSets[coreId] = new XElement(core);
                }
            string navalSkillsPath = Path.Combine(naval, "naval_skill_sets.xml");
            if (File.Exists(navalSkillsPath))
                foreach (XElement extension in XDocument.Load(navalSkillsPath).Descendants("SkillSet").Where(x => x.Attribute("id") != null))
                {
                    string skillSetId = (string)extension.Attribute("id");
                    // War Sails augments existing skill sets with naval skills;
                    // replacing the whole node would erase ordinary combat/social skills.
                    XElement merged = skillSets.TryGetValue(skillSetId, out XElement prior) ? new XElement(prior) : new XElement("SkillSet", new XAttribute("id", skillSetId));
                    foreach (XElement skill in extension.Elements("skill"))
                    {
                        string skillId = (string)skill.Attribute("id");
                        merged.Elements("skill").Where(x => (string)x.Attribute("id") == skillId).Remove();
                        merged.Add(new XElement(skill));
                    }
                    skillSets[skillSetId] = merged;
                }
            Dictionary<string, XElement> clanNodes = clans.Descendants("Faction").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (XElement clan in courtClans.Descendants("Faction").Where(x => x.Attribute("id") != null))
                clanNodes[(string)clan.Attribute("id")] = clan;

            List<Tuple<XElement, XElement, string>> sources = new List<Tuple<XElement, XElement, string>>();
            Dictionary<string, XElement> nativeHeroById = nativeHeroes.Descendants("Hero").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (XElement npc in nativeLords.Descendants("NPCCharacter").Where(x => string.Equals((string)x.Attribute("occupation"), "Lord", StringComparison.OrdinalIgnoreCase) && string.Equals((string)x.Attribute("is_hero"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                string id = (string)npc.Attribute("id");
                if (nativeHeroById.TryGetValue(id, out XElement hero)) sources.Add(Tuple.Create(npc, hero, "native_sandbox"));
            }
            Dictionary<string, XElement> courtHeroById = courtHeroes.Descendants("Hero").Where(x => x.Attribute("id") != null).ToDictionary(x => (string)x.Attribute("id"), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (XElement npc in courtLords.Descendants("NPCCharacter").Where(x => string.Equals((string)x.Attribute("occupation"), "Lord", StringComparison.OrdinalIgnoreCase)))
            {
                string id = (string)npc.Attribute("id");
                if (courtHeroById.TryGetValue(id, out XElement hero)) sources.Add(Tuple.Create(npc, hero, "reign_court"));
            }

            Dictionary<string, string> nameById = sources.ToDictionary(x => (string)x.Item1.Attribute("id"), x => CleanGameText((string)x.Item1.Attribute("name")), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> profiles = new List<Dictionary<string, object>>();
            foreach (Tuple<XElement, XElement, string> source in sources.OrderBy(x => (string)x.Item1.Attribute("id"), StringComparer.OrdinalIgnoreCase))
            {
                string id = (string)source.Item1.Attribute("id");
                courtRecords.TryGetValue(id, out Dictionary<string, object> courtRecord);
                Dictionary<string, object> facts = BuildCatalogSourceFacts(source.Item1, source.Item2, source.Item3, courtRecord, skillSets, clanNodes);
                profiles.Add(BuildCatalogProfile(facts, source.Item3, courtRecord, nameById));
            }

            Dictionary<string, object> root = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["packVersion"] = CharacterProfilePackVersion,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["manifest"] = new Dictionary<string, object>
                {
                    ["expectedProfileCount"] = profiles.Count,
                    ["nativeSandboxCount"] = profiles.Count(x => ReadString(x, "source", "") == "native_sandbox"),
                    ["nativeNordCount"] = profiles.Count(x => ReadString(x, "source", "") == "native_sandbox" && ReadString(ReadDictionary(x, "sourceFacts"), "cultureId", "") == "nord"),
                    ["navalSourceHash"] = navalFiles.All(File.Exists) ? HashFiles(navalFiles) : "",
                    ["reignCourtCount"] = profiles.Count(x => ReadString(x, "source", "") == "reign_court"),
                    ["courtHouseholdCount"] = ReadInt(courtManifest, "household_count", 0),
                    ["traitCount"] = CoreTraitKeys.Length,
                    ["courtCharacterModelVersion"] = CourtService.CourtCharacterModelVersion,
                    ["sandboxSourceHash"] = HashFiles(Path.Combine(sandbox, "heroes.xml"), Path.Combine(sandbox, "lords.xml"), Path.Combine(sandbox, "sandbox_skill_sets.xml"), Path.Combine(sandbox, "spclans.xml"), coreSkillsPath, navalSkillsPath),
                    ["reignSourceHash"] = HashFiles(Path.Combine(reign, "reign_court_heroes.xml"), Path.Combine(reign, "reign_court_lords.xml"), Path.Combine(reign, "reign_court_clans.xml"), courtManifestPath)
                },
                ["traitDefinitions"] = TraitDefinitions(),
                ["assignmentSources"] = CoreTraitAssignmentSources(),
                ["profiles"] = profiles
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, Json.Serialize(root), new UTF8Encoding(false));
            CharacterProfileLibraryRoot = null;
            CharacterProfilesById = null;
            Dictionary<string, object> validation = ValidateProfileLibraryObject(root);
            validation["output"] = Path.GetFullPath(output);
            return validation;
        }

        private static Dictionary<string, object> BuildCatalogSourceFacts(XElement npc, XElement hero, string source, Dictionary<string, object> courtRecord, Dictionary<string, XElement> skillSets, Dictionary<string, XElement> clans)
        {
            string id = (string)npc.Attribute("id");
            string faction = RemoveObjectPrefix((string)hero.Attribute("faction"));
            clans.TryGetValue(faction, out XElement clan);
            Dictionary<string, object> nativeTraits = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "valor", "generosity", "honor", "mercy", "calculating" }) nativeTraits[key] = 0;
            foreach (XElement trait in npc.Descendants("Trait"))
            {
                string key = ((string)trait.Attribute("id") ?? "").ToLowerInvariant();
                if (nativeTraits.ContainsKey(key)) nativeTraits[key] = CharactersService.ClampTrait(ParseInt((string)trait.Attribute("value")));
            }
            string templateId = RemoveObjectPrefix((string)npc.Attribute("skill_template"));
            Dictionary<string, object> nativeSkills = ResolveCatalogNativeSkills(npc, skillSets);
            string description = CleanGameText((string)hero.Attribute("text"));
            Dictionary<string, object> facts = new Dictionary<string, object>
            {
                ["heroStringId"] = id,
                ["characterObjectId"] = id,
                ["name"] = CleanGameText((string)npc.Attribute("name")),
                ["occupation"] = "Lord",
                ["isLord"] = true,
                ["isNotable"] = false,
                ["isWanderer"] = false,
                ["isAlive"] = !string.Equals((string)hero.Attribute("alive"), "false", StringComparison.OrdinalIgnoreCase),
                ["isFemale"] = string.Equals((string)npc.Attribute("is_female"), "true", StringComparison.OrdinalIgnoreCase),
                ["age"] = ParseDouble((string)npc.Attribute("age")),
                ["cultureId"] = RemoveObjectPrefix((string)npc.Attribute("culture")),
                ["clanId"] = faction,
                ["clanTier"] = ParseInt((string)clan?.Attribute("tier")),
                ["kingdomId"] = RemoveObjectPrefix((string)clan?.Attribute("super_faction")),
                ["spouseId"] = RemoveObjectPrefix((string)hero.Attribute("spouse")),
                ["fatherId"] = RemoveObjectPrefix((string)hero.Attribute("father")),
                ["motherId"] = RemoveObjectPrefix((string)hero.Attribute("mother")),
                ["voiceId"] = (string)npc.Attribute("voice") ?? "",
                ["skillTemplateId"] = templateId,
                ["nativeEncyclopediaText"] = description,
                ["encyclopediaText"] = description,
                ["traits"] = nativeTraits,
                ["skills"] = nativeSkills,
                ["source"] = source
            };
            if (courtRecord != null)
            {
                facts["householdId"] = ReadString(courtRecord, "household_id", "");
                facts["houseName"] = ReadString(courtRecord, "house_name", "");
                facts["householdRole"] = ReadString(courtRecord, "role", "");
                facts["roleLabel"] = ReadString(courtRecord, "role_label", "");
                facts["homeSettlementId"] = ReadString(courtRecord, "home_settlement_id", "");
                facts["homeSettlementName"] = ReadString(courtRecord, "home_name", "");
                facts["sourceTemplateId"] = ReadString(courtRecord, "source_template_id", "");
            }
            return facts;
        }

        internal static Dictionary<string, object> ResolveCatalogNativeSkills(XElement npc, Dictionary<string, XElement> skillSets)
        {
            Dictionary<string, object> nativeSkills = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string templateId = RemoveObjectPrefix((string)npc?.Attribute("skill_template"));
            if (!string.IsNullOrWhiteSpace(templateId) && skillSets != null && skillSets.TryGetValue(templateId, out XElement skillSet))
            {
                foreach (XElement skill in skillSet.Elements("skill"))
                    nativeSkills[NormalizeSkillKey((string)skill.Attribute("id"))] = ParseInt((string)skill.Attribute("value"));
            }
            XElement directSkills = npc?.Element("skills");
            foreach (XElement skill in directSkills == null ? Enumerable.Empty<XElement>() : directSkills.Elements("skill"))
                nativeSkills[NormalizeSkillKey((string)skill.Attribute("id"))] = ParseInt((string)skill.Attribute("value"));
            return nativeSkills;
        }

        private static Dictionary<string, object> BuildCatalogProfile(Dictionary<string, object> facts, string source, Dictionary<string, object> courtRecord, Dictionary<string, string> names)
        {
            string id = ReadString(facts, "heroStringId", "");
            string name = ReadString(facts, "name", id);
            Dictionary<string, object> traits = BuildTraitDocument(facts);
            ApplyCatalogTraitEvidence(traits, facts, courtRecord);
            EnsureTraitPercentageData(traits, id);
            CourtService.EnsureCourtCharacterData(traits, facts, id);
            Dictionary<string, object> derivedMbti = RelationshipsService.DeriveMbtiAxes(traits);
            string mbtiType = ReadString(derivedMbti, "type", "XXXX");
            if (!RelationshipsService.MbtiDefinitions.TryGetValue(mbtiType, out RelationshipsService.MbtiDefinition mbtiDefinition))
                throw new InvalidOperationException("Pregenerated noble " + id + " did not produce a valid MBTI type.");
            Dictionary<string, object> mbtiProfile = new Dictionary<string, object>
            {
                ["type"] = mbtiType,
                ["title"] = mbtiDefinition.Title,
                ["description"] = mbtiDefinition.Description,
                ["source"] = "pregenerated_noble_catalog",
                ["templateVersion"] = RelationshipsService.NotableMbtiTemplateVersion,
                ["assignedDay"] = -1d,
                ["immutable"] = true
            };
            traits["mbtiProfile"] = mbtiProfile;
            RebuildPersonalityPortrait(traits, name, CatalogDistinctiveTension(facts, ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>()), "shipped_profile_library");
            traits.Remove("definitions");
            traits.Remove("assignmentSources");
            traits.Remove("assignmentProtocol");
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>();
            string culture = DialogueService.HumanizeKey(ReadString(facts, "cultureId", "Calradian"));
            string role = ReadString(facts, "roleLabel", "noble");
            string home = ReadString(facts, "homeSettlementName", "their clan's holdings");
            string house = ReadString(facts, "houseName", "their clan");
            string nativeText = source == "native_sandbox" ? ReadString(facts, "nativeEncyclopediaText", "") : "";
            string roleSentence = CatalogRoleSentence(ReadString(facts, "householdRole", ""), house, home);
            string personality = ReadString(traits, "basePersonalitySummary", BuildRuleBasedPersonalitySummary(name, foundation));
            string inferredSummary = name + " is a " + culture + " " + role + " whose place in " + house + " ties them to " + home + ". " + roleSentence + " " + personality;
            string summary = !string.IsNullOrWhiteSpace(nativeText)
                ? nativeText.Trim() + " " + inferredSummary
                : inferredSummary;
            string publicText = !string.IsNullOrWhiteSpace(nativeText)
                ? nativeText
                : name + " is a " + culture + " noble associated with " + house + " and " + home + ". " + roleSentence;

            Dictionary<string, object> background = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["summary"] = !string.IsNullOrWhiteSpace(nativeText) ? summary : DialogueService.LimitText(summary, 1400),
                ["centralWound"] = CatalogCentralWound(facts, foundation),
                ["publicReputation"] = CatalogPublicReputation(foundation),
                ["canonBuiltFrom"] = CatalogEvidenceList(facts),
                ["encyclopediaText"] = !string.IsNullOrWhiteSpace(nativeText) ? nativeText : DialogueService.LimitText(publicText, 900),
                ["nativeEncyclopediaText"] = nativeText,
                ["nativeTextPreserved"] = !string.IsNullOrWhiteSpace(nativeText)
            };
            Dictionary<string, object> voice = BuildCatalogVoice(facts, foundation);
            Dictionary<string, object> motivations = BuildCatalogMotivations(facts, foundation);
            Dictionary<string, object> hidden = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["privateBackstory"] = CatalogPrivateBackstory(facts, foundation),
                ["secretWounds"] = new List<object> { CatalogCentralWound(facts, foundation) },
                ["privateEntanglements"] = new List<object>(),
                ["knownOnlyTo"] = new List<object> { "The character and anyone who independently discovers it." }
            };
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["packVersion"] = CharacterProfilePackVersion,
                ["heroStringId"] = id,
                ["source"] = source,
                ["sourceFacts"] = facts,
                ["mbtiProfile"] = mbtiProfile,
                ["traits"] = traits,
                ["background"] = background,
                ["voice"] = voice,
                ["motivations"] = motivations,
                ["hiddenHistory"] = hidden,
                ["template"] = new Dictionary<string, object>
                {
                    ["version"] = 2,
                    ["source"] = "shipped_profile_library",
                    ["archetype"] = "lord",
                    ["occupation"] = "Lord",
                    ["profilePackVersion"] = CharacterProfilePackVersion,
                    ["notes"] = "Canonical core personality with campaign-seeded private hooks."
                },
                ["hookPools"] = BuildCatalogHookPools(facts, foundation, names)
            };
        }

        private static void ApplyCatalogTraitEvidence(Dictionary<string, object> traits, Dictionary<string, object> facts, Dictionary<string, object> courtRecord)
        {
            Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            Action<string, int, string> adjust = (key, delta, reason) =>
            {
                if (!foundation.ContainsKey(key) || NativeLockedCoreTraits.Contains(key)) return;
                int before = CharactersService.TraitInt(foundation, key);
                int after = CharactersService.ClampTrait(before + delta);
                if (after == before) return;
                foundation[key] = after;
                evidence.Add(new Dictionary<string, object> { ["trait"] = key, ["prior"] = before, ["value"] = after, ["evidence"] = reason });
            };
            string description = ReadString(facts, "nativeEncyclopediaText", "").ToLowerInvariant();
            if (description.Contains("ambiti") || description.Contains("claimant") || description.Contains("throne")) adjust("ambition", 1, "The native biography describes a pursuit of advancement or rule.");
            if (description.Contains("conservative") || description.Contains("tradition")) adjust("traditionalism", 1, "The native biography explicitly emphasizes conservatism or tradition.");
            if (description.Contains("cautious")) { adjust("riskTolerance", -1, "The native biography explicitly describes caution."); adjust("patience", 1, "The native biography explicitly describes caution."); }
            if (description.Contains("law") || description.Contains("duty")) adjust("dutyMotivation", 1, "The native biography emphasizes law or duty.");
            if (description.Contains("scheme") || description.Contains("intrigue")) { adjust("tact", 1, "The native biography connects the character to intrigue."); adjust("socialTrust", -1, "The native biography connects the character to intrigue."); }
            if (description.Contains("cruel") || description.Contains("ruthless")) adjust("compassion", -1, "The native biography explicitly describes harsh conduct.");

            traits["foundationTraits"] = foundation;
            traits["hiddenReignTraits"] = new Dictionary<string, object>(foundation, StringComparer.OrdinalIgnoreCase);
            traits["assignmentOverrides"] = evidence;
            // The complete personality portrait is rebuilt after percentage calibration in BuildCatalogProfile.
        }

        private static Dictionary<string, object> BuildCatalogVoice(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            string nativeVoice = ReadString(facts, "voiceId", "earnest");
            int tact = CharactersService.TraitInt(traits, "tact"), assertiveness = CharactersService.TraitInt(traits, "assertiveness"), stability = CharactersService.TraitInt(traits, "emotionalStability"), pride = CharactersService.TraitInt(traits, "pride");
            string speech = nativeVoice == "curt" ? "Uses clipped, economical statements" : nativeVoice == "softspoken" ? "Speaks quietly and makes listeners lean in" : nativeVoice == "ironic" ? "Uses dry implication and carefully placed irony" : "Speaks earnestly and prefers clear declarations";
            speech += tact >= 1 ? ", cushioning dangerous points with courtly phrasing." : tact <= -1 ? ", often letting blunt truth land without protection." : ", balancing directness with the needs of the room.";
            return new Dictionary<string, object>
            {
                ["version"] = 2,
                ["nativeVoice"] = nativeVoice,
                ["speechStyle"] = speech,
                ["socialMask"] = stability >= 1 ? "Maintains a controlled public composure." : pride >= 1 ? "Projects dignity even when composure is strained." : "Adapts their public manner to the immediate balance of power.",
                ["tells"] = new List<object> { assertiveness >= 1 ? "Takes possession of pauses before answering." : "Waits to see who will press first.", CharactersService.TraitInt(traits, "irritability") >= 1 ? "Impatience appears in shortened replies." : "Frustration is usually kept behind small changes in posture." }
            };
        }

        private static Dictionary<string, object> BuildCatalogMotivations(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            List<string> motives = new[] { "wealthMotivation", "powerMotivation", "familyMotivation", "fameMotivation", "knowledgeMotivation", "religionMotivation", "revengeMotivation", "dutyMotivation", "survivalMotivation", "legacyMotivation" }.OrderByDescending(x => CharactersService.TraitInt(traits, x)).ThenBy(x => StableIndex(ReadString(facts, "heroStringId", "") + x, 1000)).Take(3).ToList();
            Dictionary<string, string> dreams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["wealthMotivation"] = "Build enough wealth that neither creditors nor patrons can dictate the household's choices.", ["powerMotivation"] = "Gain durable influence over the decisions that shape their clan and realm.", ["familyMotivation"] = "Secure the safety and advancement of the people they recognize as family.", ["fameMotivation"] = "Earn a reputation that cannot be dismissed as inherited station.", ["knowledgeMotivation"] = "Understand the people, records, and systems others use as leverage.", ["religionMotivation"] = "Live in a way that gives sacred order practical force.", ["revengeMotivation"] = "Settle a grievance that still defines what justice means to them.", ["dutyMotivation"] = "Fulfill the obligations of station without becoming another noble remembered for neglect.", ["survivalMotivation"] = "Create enough security to survive the next reversal of war or politics.", ["legacyMotivation"] = "Leave heirs, works, or a reputation that outlasts immediate victories."
            };
            List<object> dreamList = motives.Select(x => (object)dreams[x]).ToList();
            return new Dictionary<string, object>
            {
                ["version"] = 2,
                ["dreams"] = dreamList,
                ["fears"] = new List<object> { CatalogFear(traits), "Being made irrelevant while rivals decide the future." },
                ["desires"] = new List<object> { dreams[motives[0]], "To be treated as a person with independent interests rather than a convenient extension of clan policy." },
                ["linesTheyWillNotCross"] = CatalogLimits(traits)
            };
        }

        private static Dictionary<string, object> BuildCatalogHookPools(Dictionary<string, object> facts, Dictionary<string, object> traits, Dictionary<string, string> names)
        {
            string role = ReadString(facts, "roleLabel", "noble"), home = ReadString(facts, "homeSettlementName", "the household's holding"), house = ReadString(facts, "houseName", "the clan"), spouse = ResolveCatalogName(names, ReadString(facts, "spouseId", ""), "their spouse"), father = ResolveCatalogName(names, ReadString(facts, "fatherId", ""), "their father"), mother = ResolveCatalogName(names, ReadString(facts, "motherId", ""), "their mother");
            int basePressure = 20 + Math.Max(0, CharactersService.TraitInt(traits, "ambition")) * 12 + Math.Max(0, CharactersService.TraitInt(traits, "pride")) * 8;
            return new Dictionary<string, object>
            {
                ["secretInterests"] = new List<string> { "Private correspondence with someone outside the expected social circle.", "Collecting accounts of old disputes that powerful people would rather forget.", "Testing whether a commercial venture could create independence from clan money.", "A fascination with a rival culture's customs and political methods." },
                ["jealousyTargetTypes"] = new List<string> { "A relative who receives more praise for less work.", "A politically favored outsider welcomed too quickly by " + house + ".", "Someone whose freedom exposes the constraints of being a " + role + ".", "A rival whose influence at " + home + " is growing." },
                ["currentTemptations"] = new List<string> { "Use private information to force recognition.", "Accept a dangerous favor that promises independence.", "Turn a family disagreement into an alliance with an outsider.", "Risk reputation for an attraction that feels personally chosen." },
                ["privateBackstories"] = new List<string> { "A past failure at " + home + " was quietly blamed on someone else, and the character has never decided whether relief or guilt matters more.", "They once protected a household secret at real personal cost and now question whether the sacrifice was deserved.", "An early promise about their future was withdrawn when clan needs changed, leaving a private grievance beneath outward duty." },
                ["secretWounds"] = new List<string> { "They fear affection is conditional on usefulness.", "Public comparison with relatives still feels like a verdict on their worth.", "They remember the first time clan duty overruled a choice they believed was their own." },
                ["privateEntanglements"] = new List<string> { "An unresolved favor links them to a household rival.", "A discreet correspondent expects repayment for old information.", "A promise made during a moment of fear conflicts with present duty.", "Someone at " + home + " knows enough to damage their public reputation." },
                ["rumors"] = new List<string> { "Some servants believe the character is preparing to challenge a household decision.", "A visitor claims the character has been asking questions beyond their proper responsibilities.", "People at " + home + " disagree over whether recent generosity was sincere or strategic." },
                ["privateTruths"] = new List<string> { "They have imagined leaving the role chosen for them.", "They keep a private measure of every time the household chose another person's interests over theirs.", "They would break with custom if convinced the alternative secured a more meaningful legacy." },
                ["pressureProfiles"] = new List<Dictionary<string, object>>
                {
                    PressureProfile(basePressure, 20, 25, 15, 20),
                    PressureProfile(25, basePressure, 35, 20, 30),
                    PressureProfile(30, 25, basePressure, 30, 25),
                    PressureProfile(20, 30, 25, basePressure, 40)
                },
                ["familyContext"] = new Dictionary<string, object> { ["spouse"] = spouse, ["father"] = father, ["mother"] = mother }
            };
        }

        private static Dictionary<string, object> PressureProfile(int recognition, int resentment, int loyalty, int rebellion, int social)
        {
            return new Dictionary<string, object> { ["needForRecognition"] = Math.Min(100, recognition), ["resentment"] = Math.Min(100, resentment), ["loyaltyConflict"] = Math.Min(100, loyalty), ["rebellionTemptation"] = Math.Min(100, rebellion), ["romanticOrSocialRisk"] = Math.Min(100, social) };
        }

        private static string CatalogRoleSentence(string role, string house, string home)
        {
            switch (role)
            {
                case "father": return "As household head, they are expected to protect the family's standing while preparing successors who may eventually displace their authority.";
                case "mother": return "As household matriarch, they hold together kinship, reputation, and private negotiations that formal titles rarely acknowledge.";
                case "child_1": return "As the eldest child, inheritance expectations make every success and failure part of the household's judgment of succession.";
                case "child_2": return "As the second child, they must build influence without assuming the household's first claim will ever be theirs.";
                case "child_3": return "As a younger child, they are pushed toward an independent reputation, useful marriage, or service beyond the household.";
                case "child_4": return "As the youngest adult child, they have the least settled place and the greatest tension between family protection and personal freedom.";
                default: return "Their station ties personal choices to clan reputation and the shifting demands of Calradian politics.";
            }
        }

        private static string CatalogCentralWound(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            string role = ReadString(facts, "householdRole", "");
            if (role == "child_1") return "They fear being prepared for responsibility yet still judged unworthy when succession matters.";
            if (role.StartsWith("child_", StringComparison.Ordinal)) return "They have learned that family affection and family usefulness are not always the same thing.";
            if (role == "father" || role == "mother") return "They carry the knowledge that protecting a household can require choices their children may never forgive.";
            if (CharactersService.TraitInt(traits, "pride") >= 1) return "Humiliation and public dismissal cut more deeply than they admit.";
            if (CharactersService.TraitInt(traits, "socialTrust") <= -1) return "Past disappointments taught them that dependence gives other people leverage.";
            return "They fear becoming useful to everyone while remaining truly known by no one.";
        }

        private static string CatalogPrivateBackstory(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            return "Behind their public station, " + ReadString(facts, "name", "the character") + " keeps a private account of obligations, slights, and sacrifices. " + CatalogCentralWound(facts, traits);
        }

        private static string CatalogDistinctiveTension(Dictionary<string, object> facts, Dictionary<string, object> traits)
        {
            string[] tensions =
            {
                "When duty and recognition conflict, they notice who receives credit and who bears the cost.",
                "They are most volatile when family expectations deny them an outcome they believe they earned.",
                "Their restraint weakens when another person tries to make a permanent decision on their behalf.",
                "They can endure hardship more easily than public dismissal or private irrelevance.",
                "They measure loyalty through remembered conduct and rarely treat a broken promise as isolated.",
                "They are vulnerable to opportunities that promise both independence and proof of personal worth.",
                "Their strongest choices emerge when personal attachment collides with the security of their station.",
                "They prefer controlled bargains, but accumulated humiliation can make a dangerous risk feel necessary."
            };
            return tensions[StableIndex(ReadString(facts, "heroStringId", "") + "|distinctive_tension|" + CharactersService.TraitInt(traits, "pride"), tensions.Length)];
        }

        private static string CatalogPublicReputation(Dictionary<string, object> traits)
        {
            if (CharactersService.TraitInt(traits, "tact") >= 1 && CharactersService.TraitInt(traits, "confidence") >= 1) return "Considered politically composed and difficult to corner in public.";
            if (CharactersService.TraitInt(traits, "aggression") >= 1) return "Known for answering resistance directly and sometimes too forcefully.";
            if (CharactersService.TraitInt(traits, "compassion") >= 1) return "Regarded as more attentive to dependents and casualties than many nobles.";
            if (CharactersService.TraitInt(traits, "socialTrust") <= -1) return "Regarded as guarded, observant, and slow to accept assurances.";
            return "Seen as a capable noble whose deeper priorities are not yet obvious.";
        }

        private static string CatalogFear(Dictionary<string, object> traits)
        {
            if (CharactersService.TraitInt(traits, "fearfulness") >= 1) return "A sudden reversal that leaves them exposed before preparations are complete.";
            if (CharactersService.TraitInt(traits, "shame") >= 1) return "Public disgrace that permanently changes how their family sees them.";
            if (CharactersService.TraitInt(traits, "familyMotivation") >= 1) return "Watching family loyalty fracture under pressure they failed to anticipate.";
            return "Losing control of a consequential choice to someone who does not understand its cost.";
        }

        private static List<object> CatalogLimits(Dictionary<string, object> traits)
        {
            List<object> limits = new List<object>();
            if (CharactersService.TraitInt(traits, "honesty") >= 1) limits.Add("Will not knowingly build a lasting agreement on a direct lie.");
            if (CharactersService.TraitInt(traits, "compassion") >= 1 || CharactersService.TraitInt(traits, "mercy") >= 1) limits.Add("Will not casually punish helpless people for another person's offense.");
            if (CharactersService.TraitInt(traits, "loyalty") >= 1) limits.Add("Will not abandon a chosen ally without warning when alternatives remain.");
            if (limits.Count == 0) limits.Add("Will not accept a bargain that leaves them permanently powerless.");
            return limits;
        }

        private static List<object> CatalogEvidenceList(Dictionary<string, object> facts)
        {
            List<object> result = new List<object> { "Native traits", "Native skills", "Age and noble station", "Clan and family links" };
            if (ReadString(facts, "source", "") == "native_sandbox" && !string.IsNullOrWhiteSpace(ReadString(facts, "nativeEncyclopediaText", ""))) result.Insert(0, "Authored native encyclopedia description");
            if (!string.IsNullOrWhiteSpace(ReadString(facts, "householdId", ""))) result.Add("Reign court-household role and home holding");
            return result;
        }

        private static string ResolveCatalogName(Dictionary<string, string> names, string id, string fallback)
        {
            return !string.IsNullOrWhiteSpace(id) && names.TryGetValue(id, out string name) && !string.IsNullOrWhiteSpace(name) ? name : fallback;
        }

        private static string ProfileArg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < (args ?? new string[0]).Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }

        internal static string CleanGameText(string value)
        {
            string text = value ?? "";
            if (text.StartsWith("{=", StringComparison.Ordinal))
            {
                int end = text.IndexOf('}');
                if (end >= 0 && end + 1 < text.Length) text = text.Substring(end + 1);
            }
            return text.Trim();
        }

        internal static string RemoveObjectPrefix(string value)
        {
            string text = value ?? "";
            int dot = text.IndexOf('.');
            return dot >= 0 ? text.Substring(dot + 1) : text;
        }

        private static string NormalizeSkillKey(string value)
        {
            string key = (value ?? "").Trim();
            if (key == "Crafting") return "smithing";
            if (string.Equals(key, "Polearms", StringComparison.OrdinalIgnoreCase)) return "polearm";
            return key.Length == 0 ? "" : char.ToLowerInvariant(key[0]) + key.Substring(1);
        }

        internal static double ParseDouble(string value) { return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0d; }

        private static string HashFiles(params string[] paths)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string path in paths.Where(File.Exists).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Dictionary<string, object> DeepCloneProfileDictionary(Dictionary<string, object> value)
        {
            return value == null ? new Dictionary<string, object>() : TryParseJsonObject(Json.Serialize(value)) ?? new Dictionary<string, object>();
        }

        internal static List<object> ReadObjectList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return new List<object>();
            if (value is ArrayList array) return array.Cast<object>().ToList();
            if (value is IEnumerable<object> objects) return objects.ToList();
            return new List<object>();
        }

        private static List<object> MergeLists(List<object> values, string extra)
        {
            List<object> result = values ?? new List<object>();
            if (!string.IsNullOrWhiteSpace(extra)) result.Add(extra);
            return result;
        }

        private static Dictionary<string, object> ValidateProfileLibraryObject(Dictionary<string, object> root)
        {
            List<Dictionary<string, object>> profiles = ReadDictionaryList(root, "profiles");
            int invalid = profiles.Count(x =>
            {
                Dictionary<string, object> document = ReadDictionary(x, "traits") ?? new Dictionary<string, object>();
                return (ReadDictionary(document, "foundationTraits") ?? new Dictionary<string, object>()).Count != CoreTraitKeys.Length
                    || !TraitPercentageDocumentReady(document)
                    || !PersonalityPortraitReady(document);
            });
            int expected = ReadInt(ReadDictionary(root, "manifest"), "expectedProfileCount", profiles.Count);
            return new Dictionary<string, object>
            {
                ["ok"] = profiles.Count == expected && invalid == 0,
                ["packVersion"] = ReadString(root, "packVersion", ""),
                ["profileCount"] = profiles.Count,
                ["nativeSandboxCount"] = profiles.Count(x => ReadString(x, "source", "") == "native_sandbox"),
                ["reignCourtCount"] = profiles.Count(x => ReadString(x, "source", "") == "reign_court"),
                ["invalidTraitDocuments"] = invalid
            };
        }
    }
}
