using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class VerificationService
    {
        internal static string ArgValue(string[] args, string key, string fallback)
        {
            if (args == null)
            {
                return fallback;
            }

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        internal static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            if (source == null)
            {
                return new Dictionary<string, object>();
            }

            Dictionary<string, object> clone;
            try
            {
                clone = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(source)) ?? new Dictionary<string, object>();
            }
            catch
            {
                clone = new Dictionary<string, object>(source);
            }
            DialogueService.CopyConversationStructureReceipts(source, clone);
            return clone;
        }

        private static void CopyTestIfMissing(Dictionary<string, object> target, string key, object value)
        {
            if (target == null || target.ContainsKey(key) || value == null)
            {
                return;
            }

            string text = Convert.ToString(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                target[key] = value;
            }
        }

        internal static void SaveTestSnapshotFromPayload(string campaignId, string correlationId, string mode, string phase, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(correlationId) || payload == null)
            {
                return;
            }

            Dictionary<string, object> snapshot = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["mode"] = mode,
                ["phase"] = phase,
                ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
                ["payload"] = SanitizeTestValue(payload, 0)
            };
            CampaignFileService.WriteJsonObject(SnapshotPath(campaignId, correlationId), snapshot);
        }

        private static object SanitizeTestValue(object value, int depth)
        {
            if (value == null)
            {
                return null;
            }

            if (depth > Program.AuditMaxDepth + 3)
            {
                return "[test snapshot depth limit]";
            }

            if (value is string text)
            {
                return text.Length > 250000 ? text.Substring(0, 250000) + "\n[test snapshot string truncated]" : text;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                Dictionary<string, object> clean = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (OperationsService.IsAuditSecretKey(pair.Key))
                    {
                        clean[pair.Key] = "[redacted]";
                    }
                    else if (OperationsService.IsAuditBlobKey(pair.Key))
                    {
                        clean[pair.Key] = "[omitted image/blob data]";
                    }
                    else
                    {
                        clean[pair.Key] = SanitizeTestValue(pair.Value, depth + 1);
                    }
                }

                return clean;
            }

            if (value is IDictionary genericDictionary)
            {
                Dictionary<string, object> converted = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in genericDictionary)
                {
                    converted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = entry.Value;
                }

                return SanitizeTestValue(converted, depth + 1);
            }

            if (value is IEnumerable enumerable && !(value is byte[]))
            {
                ArrayList list = new ArrayList();
                foreach (object item in enumerable)
                {
                    list.Add(SanitizeTestValue(item, depth + 1));
                }

                return list;
            }

            return value;
        }

        internal static void EnsureDefaultTestCases()
        {
            EnsureDefaultSettlementTestCases();
            EnsureDefaultActionCommandTestCases();
        }

        private static void EnsureDefaultSettlementTestCases()
        {
            string path = TestCasePath("settlements", "lycaron_accent_surrender");
            if (File.Exists(path))
            {
                return;
            }

            Dictionary<string, object> testCase = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["suite"] = "settlements",
                ["caseId"] = "lycaron_accent_surrender",
                ["name"] = "Resolve Lycaron/Lýcaron settlement surrender",
                ["description"] = "Canned dry-run test proving natural settlement naming resolves to the accented Bannerlord settlement id.",
                ["mode"] = "action_only",
                ["input"] = new Dictionary<string, object>
                {
                    ["playerText"] = "Give me Lycaron for peace.",
                    ["action"] = new Dictionary<string, object>
                    {
                        ["command"] = "demand_settlement_peace",
                        ["actorKingdomId"] = "new_kingdom",
                        ["targetKingdomId"] = "empire_s",
                        ["TargetSettlement"] = "Lycaron",
                        ["reason"] = "Canned settlement surrender test."
                    }
                },
                ["snapshot"] = new Dictionary<string, object>
                {
                    ["campaignId"] = "test_campaign",
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["campaignId"] = "test_campaign",
                        ["actionResolutionIndex"] = new Dictionary<string, object>
                        {
                            ["version"] = "test_v1",
                            ["settlements"] = new ArrayList
                            {
                                new Dictionary<string, object> { ["settlementId"] = "town_ES4", ["name"] = "Lýcaron", ["type"] = "town", ["kingdomId"] = "empire_s", ["ownerClanId"] = "pethros" },
                                new Dictionary<string, object> { ["settlementId"] = "town_ES3", ["name"] = "Jalmarys", ["type"] = "town", ["kingdomId"] = "empire_w", ["ownerClanId"] = "neretzes" }
                            },
                            ["kingdoms"] = new ArrayList
                            {
                                new Dictionary<string, object> { ["kingdomId"] = "new_kingdom", ["name"] = "fen Seanel", ["leaderHeroStringId"] = "player" },
                                new Dictionary<string, object> { ["kingdomId"] = "empire_s", ["name"] = "Southern Empire", ["leaderHeroStringId"] = "rhagaea" }
                            },
                            ["clans"] = new ArrayList
                            {
                                new Dictionary<string, object> { ["clanId"] = "player_clan", ["name"] = "fen Seanel", ["kingdomId"] = "new_kingdom", ["leaderHeroStringId"] = "player" },
                                new Dictionary<string, object> { ["clanId"] = "pethros", ["name"] = "Pethros", ["kingdomId"] = "empire_s", ["leaderHeroStringId"] = "rhagaea" }
                            },
                            ["heroes"] = new ArrayList
                            {
                                new Dictionary<string, object> { ["heroStringId"] = "player", ["name"] = "Player", ["kingdomId"] = "new_kingdom", ["clanId"] = "player_clan" },
                                new Dictionary<string, object> { ["heroStringId"] = "rhagaea", ["name"] = "Rhagaea", ["kingdomId"] = "empire_s", ["clanId"] = "pethros" }
                            },
                            ["aliases"] = new ArrayList
                            {
                                new Dictionary<string, object> { ["alias"] = "player_kingdom", ["entityType"] = "kingdom", ["id"] = "new_kingdom" },
                                new Dictionary<string, object> { ["alias"] = "speaker_kingdom", ["entityType"] = "kingdom", ["id"] = "empire_s" },
                                new Dictionary<string, object> { ["alias"] = "Lycaron", ["entityType"] = "settlement", ["id"] = "town_ES4" }
                            },
                            ["assets"] = new Dictionary<string, object>()
                        }
                    }
                },
                ["llm"] = new Dictionary<string, object> { ["mode"] = "canned", ["cannedContent"] = "" },
                ["expected"] = new Dictionary<string, object>
                {
                    ["validationSuccess"] = true,
                    ["command"] = "demand_settlement_peace",
                    ["actorKingdomStringId"] = "new_kingdom",
                    ["targetKingdomStringId"] = "empire_s",
                    ["targetSettlementStringId"] = "town_ES4"
                },
                ["assertions"] = new Dictionary<string, object> { ["strict"] = true }
            };
            CampaignFileService.WriteJsonObject(path, testCase);
        }

        private static void EnsureDefaultActionCommandTestCases()
        {
            HashSet<string> version2Cases = new HashSet<string>(new[]
            {
                "start_ruling_clan_rebellion",
                "recruit_lord_to_rebellion",
                "join_rebellion",
                "surrender_rebellion",
                "attack_player_party"
            }, StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> testCase in DefaultActionCommandTestCases())
            {
                string caseId = ReadString(testCase, "caseId", "case");
                string path = TestCasePath(ReadString(testCase, "suite", "actions"), caseId);
                int requiredVersion = caseId.Equals("attack_player_party", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
                bool upgradeDefaultContract = version2Cases.Contains(caseId)
                    && ReadInt(CampaignFileService.ReadJsonObject(path), "version", 0) < requiredVersion;
                if (!File.Exists(path) || upgradeDefaultContract)
                {
                    if (version2Cases.Contains(caseId)) testCase["version"] = requiredVersion;
                    CampaignFileService.WriteJsonObject(path, testCase);
                }
            }
        }

        private static List<Dictionary<string, object>> DefaultActionCommandTestCases()
        {
            return new List<Dictionary<string, object>>
            {
                ActionCommandTestCase("declare_war", "Declare war", "I declare war on the Southern Empire.",
                    TestDict("command", "declare_war", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire"),
                    ExpectedTest("declare_war", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("make_peace", "Make peace", "Let us make peace with the Southern Empire.",
                    TestDict("command", "make_peace", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire"),
                    ExpectedTest("make_peace", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("offer_tribute_peace", "Offer tribute peace", "Offer tribute for peace.",
                    TestDict("command", "offer_tribute_peace", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("dailyTribute", 300, "durationDays", 60)),
                    ExpectedTest("offer_tribute_peace", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("record_promise", "Record promise", "Record my promise to defend their caravans.",
                    TestDict("command", "record_promise", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("promiseText", "A public promise to defend imperial caravans for one season.")),
                    ExpectedTest("record_promise", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("demand_reparations_peace", "Demand reparations peace", "Demand reparations in exchange for peace.",
                    TestDict("command", "demand_reparations_peace", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("reparationsGold", 15000, "dailyTribute", 100, "durationDays", 30)),
                    ExpectedTest("demand_reparations_peace", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("demand_settlement_peace", "Demand settlement peace", "Give me Lycaron for peace.",
                    TestDict("command", "demand_settlement_peace", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "TargetSettlement", "Lycaron"),
                    ExpectedTest("demand_settlement_peace", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("demand_surrender_peace", "Demand full surrender", "Accept full surrender and give all your towns and castles to my kingdom.",
                    TestDict("command", "demand_surrender_peace", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("allTargetFortifications", true, "reparationsGold", 5000)),
                    ExpectedTest("demand_surrender_peace", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("sign_trade_agreement", "Sign trade agreement", "Sign a trade agreement between our kingdoms.",
                    TestDict("command", "sign_trade_agreement", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("durationDays", 120)),
                    ExpectedTest("sign_trade_agreement", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("sign_non_aggression_pact", "Sign non aggression pact", "Sign a non-aggression pact.",
                    TestDict("command", "sign_non_aggression_pact", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("durationDays", 120)),
                    ExpectedTest("sign_non_aggression_pact", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("sign_alliance", "Sign alliance", "Sign a full alliance.",
                    TestDict("command", "sign_alliance", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("durationDays", 180)),
                    ExpectedTest("sign_alliance", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("sign_defensive_pact", "Sign defensive pact", "Sign a defensive pact.",
                    TestDict("command", "sign_defensive_pact", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("durationDays", 180)),
                    ExpectedTest("sign_defensive_pact", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("break_treaty", "Break treaty", "Break our trade treaty.",
                    TestDict("command", "break_treaty", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("treatyKind", "trade_agreement")),
                    ExpectedTest("break_treaty", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("exchange_prisoners", "Exchange prisoners", "Exchange noble prisoners.",
                    TestDict("command", "exchange_prisoners", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "PrisonerHero", "Prisoner Lord", "terms", TestDict("maxNoblePrisoners", 2)),
                    ExpectedTest("exchange_prisoners", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetHeroStringId", "prisoner_lord")),
                ActionCommandTestCase("ransom_package", "Ransom package", "Pay twenty thousand denars for the prisoner.",
                    TestDict("command", "ransom_package", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "PrisonerHero", "Prisoner Lord", "GoldAmount", 20000),
                    ExpectedTest("ransom_package", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetHeroStringId", "prisoner_lord")),
                ActionCommandTestCase("hostage_guarantee", "Hostage guarantee", "Send Mengus as a hostage guarantee.",
                    TestDict("command", "hostage_guarantee", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "TargetHero", "Mengus", "terms", TestDict("durationDays", 60)),
                    ExpectedTest("hostage_guarantee", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetHeroStringId", "mengus")),
                ActionCommandTestCase("war_indemnity", "War indemnity", "Pay war indemnity.",
                    TestDict("command", "war_indemnity", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "GoldAmount", 15000),
                    ExpectedTest("war_indemnity", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("recognize_conquest", "Recognize conquest", "Recognize my conquest of Lycaron.",
                    TestDict("command", "recognize_conquest", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "TargetSettlement", "Lycaron", "terms", TestDict("claim", "Recognize actor control of Lycaron.")),
                    ExpectedTest("recognize_conquest", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("return_occupied_settlement", "Return occupied settlement", "Return Lycaron to the Southern Empire.",
                    TestDict("command", "return_occupied_settlement", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "TargetSettlement", "Lycaron"),
                    ExpectedTest("return_occupied_settlement", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("demilitarized_border", "Demilitarized border", "Create a demilitarized border.",
                    TestDict("command", "demilitarized_border", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("durationDays", 90)),
                    ExpectedTest("demilitarized_border", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("supply_agreement", "Supply agreement", "Send grain supplies.",
                    TestDict("command", "supply_agreement", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "Item", "Grain", "Amount", 300),
                    ExpectedTest("supply_agreement", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("loan_or_subsidy", "Loan or subsidy", "Loan the Southern Empire ten thousand denars.",
                    TestDict("command", "loan_or_subsidy", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "GoldAmount", 10000),
                    ExpectedTest("loan_or_subsidy", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("pay_to_stay_neutral", "Pay to stay neutral", "Pay them to stay neutral.",
                    TestDict("command", "pay_to_stay_neutral", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "GoldAmount", 12000, "terms", TestDict("durationDays", 90)),
                    ExpectedTest("pay_to_stay_neutral", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("pay_to_join_war", "Pay to join war", "Pay the Southern Empire to join the war against Vlandia.",
                    TestDict("command", "pay_to_join_war", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "GoldAmount", 25000, "terms", TestDict("enemyKingdomId", "vlandia")),
                    ExpectedTest("pay_to_join_war", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("guarantee_independence", "Guarantee independence", "Guarantee their independence.",
                    TestDict("command", "guarantee_independence", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("durationDays", 180)),
                    ExpectedTest("guarantee_independence", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("protectorate_or_vassalage", "Protectorate or vassalage", "Make them our protectorate.",
                    TestDict("command", "protectorate_or_vassalage", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "terms", TestDict("arrangementType", "protectorate", "durationDays", 365)),
                    ExpectedTest("protectorate_or_vassalage", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("diplomatic_package", "Diplomatic package", "Trade twenty thousand denars for the prisoner and recognition of Lycaron.",
                    TestDict("command", "diplomatic_package", "actorKingdomId", "player_kingdom", "TargetKingdom", "Southern Empire", "TargetSettlement", "Lycaron", "PrisonerHero", "Prisoner Lord", "GoldAmount", 20000, "terms", TestDict("treatyKind", "prisoner_for_gold")),
                    ExpectedTest("diplomatic_package", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "targetSettlementStringId", "town_ES4", "targetHeroStringId", "prisoner_lord")),
                ActionCommandTestCase("recruit_and_recover", "Recruit and recover", "Mengus should recruit and recover at Lycaron.",
                    TestDict("command", "recruit_and_recover", "actorHeroId", "Mengus", "TargetSettlement", "Lycaron", "minimumTroops", 80),
                    ExpectedTest("recruit_and_recover", "actorHeroStringId", "mengus", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("form_army", "Form army", "Mengus should form an army at Lycaron.",
                    TestDict("command", "form_army", "actorHeroId", "Mengus", "TargetSettlement", "Lycaron", "desiredStrength", 600),
                    ExpectedTest("form_army", "actorHeroStringId", "mengus", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("attack_settlement", "Attack settlement", "Mengus should attack Uthelaim Castle.",
                    TestDict("command", "attack_settlement", "actorHeroId", "Mengus", "TargetSettlement", "Uthelaim Castle"),
                    ExpectedTest("attack_settlement", "actorHeroStringId", "mengus", "targetSettlementStringId", "castle_battania_1")),
                ActionCommandTestCase("capture_settlement_plan", "Capture settlement plan", "Mengus should capture Uthelaim Castle.",
                    TestDict("command", "capture_settlement_plan", "actorHeroId", "Mengus", "TargetSettlement", "Uthelaim Castle", "minimumTroops", 120, "desiredStrength", 800),
                    ExpectedTest("capture_settlement_plan", "actorHeroStringId", "mengus", "targetSettlementStringId", "castle_battania_1")),
                ActionCommandTestCase("start_ruling_clan_rebellion", "Declare rebellion", "The player challenges Rhagaea and declares a rebellion.",
                    TestDict("command", "start_ruling_clan_rebellion", "actorHeroId", "player", "actorKingdomId", "Southern Empire", "actorClanId", "player_clan", "TargetHero", "Rhagaea"),
                    ExpectedTest("start_ruling_clan_rebellion", "actorHeroStringId", "player", "actorKingdomStringId", "empire_s", "actorClanStringId", "player_clan", "targetHeroStringId", "rhagaea")),
                ActionCommandTestCase("recruit_lord_to_rebellion", "Recruit lord to rebellion", "Mengus accepts the player's rebellion.",
                    TestDict("command", "recruit_lord_to_rebellion", "actorHeroId", "player", "actorKingdomId", "player_kingdom", "actorClanId", "player_clan", "TargetHero", "Mengus", "TargetClan", "fen Gruffendoc"),
                    ExpectedTest("recruit_lord_to_rebellion", "actorHeroStringId", "player", "targetHeroStringId", "mengus", "targetClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("join_rebellion", "Join NPC rebellion", "The player joins Rhagaea's rebellion.",
                    TestDict("command", "join_rebellion", "actorHeroId", "player", "actorKingdomId", "player_kingdom", "actorClanId", "player_clan", "TargetHero", "Rhagaea"),
                    ExpectedTest("join_rebellion", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea")),
                ActionCommandTestCase("surrender_rebellion", "Surrender rebellion", "The player explicitly surrenders.",
                    TestDict("command", "surrender_rebellion", "actorHeroId", "player", "actorKingdomId", "player_kingdom", "actorClanId", "player_clan"),
                    ExpectedTest("surrender_rebellion", "actorHeroStringId", "player", "actorClanStringId", "player_clan")),
                ActionCommandTestCase("install_ruling_clan", "Install ruling clan", "Install Pethros as the ruling clan.",
                    TestDict("command", "install_ruling_clan", "actorKingdomId", "Southern Empire", "actorClanId", "Pethros"),
                    ExpectedTest("install_ruling_clan", "actorKingdomStringId", "empire_s", "actorClanStringId", "pethros")),
                ActionCommandTestCase("marriage_alliance", "Marriage alliance", "Make a marriage alliance with fen Gruffendoc.",
                    TestDict("command", "marriage_alliance", "actorKingdomId", "Southern Empire", "actorClanId", "Pethros", "TargetClan", "fen Gruffendoc"),
                    ExpectedTest("marriage_alliance", "actorKingdomStringId", "empire_s", "actorClanStringId", "pethros", "targetClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("support_claimant", "Support claimant", "Support the claimant clan.",
                    TestDict("command", "support_claimant", "actorKingdomId", "Southern Empire", "actorClanId", "Claimant Clan"),
                    ExpectedTest("support_claimant", "actorKingdomStringId", "empire_s", "actorClanStringId", "claimant_test_clan")),
                ActionCommandTestCase("encourage_clan_defection", "Encourage clan defection", "Encourage fen Gruffendoc to defect to my kingdom.",
                    TestDict("command", "encourage_clan_defection", "actorKingdomId", "player_kingdom", "actorClanId", "fen Gruffendoc"),
                    ExpectedTest("encourage_clan_defection", "actorKingdomStringId", "new_kingdom", "actorClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("exile_clan", "Exile clan", "Exile fen Gruffendoc from the kingdom.",
                    TestDict("command", "exile_clan", "actorKingdomId", "Battania", "actorClanId", "fen Gruffendoc"),
                    ExpectedTest("exile_clan", "actorKingdomStringId", "battania", "actorClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("restore_exiled_clan", "Restore exiled clan", "Restore the exiled clan to my kingdom.",
                    TestDict("command", "restore_exiled_clan", "actorKingdomId", "player_kingdom", "actorClanId", "Exiled Test Clan"),
                    ExpectedTest("restore_exiled_clan", "actorKingdomStringId", "new_kingdom", "actorClanStringId", "exiled_test_clan")),
                ActionCommandTestCase("mediate_clan_dispute", "Mediate clan dispute", "Mediate the dispute between Pethros and fen Gruffendoc.",
                    TestDict("command", "mediate_clan_dispute", "actorKingdomId", "Southern Empire", "actorClanId", "Pethros", "TargetClan", "fen Gruffendoc"),
                    ExpectedTest("mediate_clan_dispute", "actorKingdomStringId", "empire_s", "actorClanStringId", "pethros", "targetClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("follow", "Context-aware follow", "Mengus should follow me.",
                    TestDict("command", "follow", "actorHeroId", "Mengus", "TargetParty", "player party"),
                    ExpectedTest("follow", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("stop_following", "Stop following", "Mengus should stop following.",
                    TestDict("command", "stop_following", "actorHeroId", "Mengus"),
                    ExpectedTest("stop_following", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("go_to_settlement", "Go to settlement", "Mengus should go to Lycaron.",
                    TestDict("command", "go_to_settlement", "actorHeroId", "Mengus", "TargetSettlement", "Lycaron"),
                    ExpectedTest("go_to_settlement", "actorHeroStringId", "mengus", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("patrol_around_settlement", "Patrol around settlement", "Mengus should patrol around Lycaron.",
                    TestDict("command", "patrol_around_settlement", "actorHeroId", "Mengus", "TargetSettlement", "Lycaron"),
                    ExpectedTest("patrol_around_settlement", "actorHeroStringId", "mengus", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("wait_near_settlement", "Wait near settlement", "Mengus should wait near Lycaron.",
                    TestDict("command", "wait_near_settlement", "actorHeroId", "Mengus", "TargetSettlement", "Lycaron"),
                    ExpectedTest("wait_near_settlement", "actorHeroStringId", "mengus", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("raid_village", "Raid village", "Mengus should raid Amitatys.",
                    TestDict("command", "raid_village", "actorHeroId", "Mengus", "TargetSettlement", "Amitatys"),
                    ExpectedTest("raid_village", "actorHeroStringId", "mengus", "targetSettlementStringId", "village_lycaron_1")),
                ActionCommandTestCase("besiege_settlement", "Besiege settlement", "Mengus should besiege Uthelaim Castle.",
                    TestDict("command", "besiege_settlement", "actorHeroId", "Mengus", "TargetSettlement", "Uthelaim Castle"),
                    ExpectedTest("besiege_settlement", "actorHeroStringId", "mengus", "targetSettlementStringId", "castle_battania_1")),
                ActionCommandTestCase("create_party", "Create party", "Mengus should create a party at Lycaron.",
                    TestDict("command", "create_party", "actorHeroId", "Mengus", "TargetSettlement", "Lycaron"),
                    ExpectedTest("create_party", "actorHeroStringId", "mengus", "targetSettlementStringId", "town_ES4")),
                ActionCommandTestCase("show_the_way", "Show the way", "Mengus should show the way.",
                    TestDict("command", "show_the_way", "actorHeroId", "Mengus"),
                    ExpectedTest("show_the_way", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("attack_party", "Attack party", "Mengus should attack the bandit party.",
                    TestDict("command", "attack_party", "actorHeroId", "Mengus", "TargetParty", "bandit party"),
                    ExpectedTest("attack_party", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("attack_player_party", "Attack player party", "Rhagaea should attack my party.",
                    TestDict("command", "attack_player_party", "actorHeroId", "Rhagaea"),
                    ExpectedTest("attack_player_party", "actorHeroStringId", "rhagaea")),
                ActionCommandTestCase("surrender_to_player", "Surrender to player", "Mengus should surrender to me.",
                    TestDict("command", "surrender_to_player", "actorHeroId", "Mengus"),
                    ExpectedTest("surrender_to_player", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("leave_player_alone", "Leave player alone", "Mengus should leave me alone.",
                    TestDict("command", "leave_player_alone", "actorHeroId", "Mengus"),
                    ExpectedTest("leave_player_alone", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("kill_character", "Kill character", "Rhagaea orders Mengus killed.",
                    TestDict("command", "kill_character", "actorHeroId", "Rhagaea", "TargetHero", "Mengus", "KillerHero", "Rhagaea"),
                    ExpectedTest("kill_character", "actorHeroStringId", "rhagaea", "targetHeroStringId", "mengus")),
                ActionCommandTestCase("duel_player", "Duel player", "Mengus challenges the player to a duel.",
                    TestDict("command", "duel_player", "actorHeroId", "Mengus"),
                    ExpectedTest("duel_player", "actorHeroStringId", "mengus")),
                ActionCommandTestCase("give_gold_to_player", "Give gold to player", "Rhagaea gives me ten thousand denars.",
                    TestDict("command", "give_gold_to_player", "actorHeroId", "Rhagaea", "GoldAmount", 10000),
                    ExpectedTest("give_gold_to_player", "actorHeroStringId", "rhagaea")),
                ActionCommandTestCase("transfer_gold", "Transfer gold", "Rhagaea transfers ten thousand denars to Aeric.",
                    TestDict("command", "transfer_gold", "FromHero", "Rhagaea", "ToHero", "Aeric", "GoldAmount", 10000),
                    ExpectedTest("transfer_gold", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player")),
                ActionCommandTestCase("transfer_item", "Transfer item", "Aeric transfers grain to Rhagaea.",
                    TestDict("command", "transfer_item", "FromHero", "Aeric", "ToHero", "Rhagaea", "Item", "Grain", "Amount", 20),
                    ExpectedTest("transfer_item", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea")),
                ActionCommandTestCase("transfer_workshop", "Transfer workshop", "Transfer the Lycaron smithy to Aeric.",
                    TestDict("command", "transfer_workshop", "Workshop", "Lycaron smithy", "ToHero", "Aeric"),
                    ExpectedTest("transfer_workshop", "targetHeroStringId", "player")),
                ActionCommandTestCase("transfer_prisoner", "Transfer prisoner", "Release the prisoner lord to Aeric.",
                    TestDict("command", "transfer_prisoner", "PrisonerHero", "Prisoner Lord", "ToHero", "Aeric"),
                    ExpectedTest("transfer_prisoner", "targetHeroStringId", "prisoner_lord")),
                ActionCommandTestCase("hire_player_as_mercenary", "Hire player as mercenary", "Hire Aeric as a mercenary for the Southern Empire.",
                    TestDict("command", "hire_player_as_mercenary", "TargetKingdom", "Southern Empire"),
                    ExpectedTest("hire_player_as_mercenary", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("dismiss_player_mercenary", "Dismiss player mercenary", "Dismiss Aeric as a mercenary.",
                    TestDict("command", "dismiss_player_mercenary"),
                    ExpectedTest("dismiss_player_mercenary")),
                ActionCommandTestCase("offer_player_vassalage", "Offer player vassalage", "Offer Aeric vassalage under the Southern Empire.",
                    TestDict("command", "offer_player_vassalage", "TargetKingdom", "Southern Empire"),
                    ExpectedTest("offer_player_vassalage", "targetKingdomStringId", "empire_s")),
                ActionCommandTestCase("dismiss_player_vassal", "Dismiss player vassal", "Dismiss Aeric as a vassal.",
                    TestDict("command", "dismiss_player_vassal"),
                    ExpectedTest("dismiss_player_vassal")),
                ActionCommandTestCase("join_clan", "Join clan", "Mengus should join fen Seanel.",
                    TestDict("command", "join_clan", "actorHeroId", "Mengus", "TargetClan", "fen Seanel"),
                    ExpectedTest("join_clan", "actorHeroStringId", "mengus", "targetClanStringId", "player_clan")),
                ActionCommandTestCase("leave_clan", "Leave clan", "Mengus should leave his clan.",
                    TestDict("command", "leave_clan", "actorHeroId", "Mengus", "TargetClan", "fen Gruffendoc"),
                    ExpectedTest("leave_clan", "actorHeroStringId", "mengus", "targetClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("join_kingdom", "Join kingdom", "fen Gruffendoc joins my kingdom.",
                    TestDict("command", "join_kingdom", "actorClanId", "fen Gruffendoc", "TargetKingdom", "fen Seanel"),
                    ExpectedTest("join_kingdom", "actorClanStringId", "fen_gruffendoc", "targetKingdomStringId", "new_kingdom")),
                ActionCommandTestCase("leave_kingdom", "Leave kingdom", "fen Gruffendoc leaves Battania.",
                    TestDict("command", "leave_kingdom", "actorClanId", "fen Gruffendoc"),
                    ExpectedTest("leave_kingdom", "actorClanStringId", "fen_gruffendoc")),
                ActionCommandTestCase("hire_mercenary_clan", "Hire mercenary clan", "Hire fen Gruffendoc as mercenaries for my kingdom.",
                    TestDict("command", "hire_mercenary_clan", "TargetClan", "fen Gruffendoc", "TargetKingdom", "fen Seanel"),
                    ExpectedTest("hire_mercenary_clan", "targetClanStringId", "fen_gruffendoc", "targetKingdomStringId", "new_kingdom")),
                ActionExtractorTestCase("extract_give_ships", "Extract give ships", "@give me your ships@",
                    ExpectedTest("trade_package", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "actorKingdomStringId", "empire_s", "targetKingdomStringId", "new_kingdom")),
                ActionExtractorTestCase("extract_give_onira", "Extract give Onira", "@Give me Onira@",
                    ExpectedTest("demand_settlement_peace", "targetSettlementStringId", "town_ES8")),
                ActionExtractorTestCase("extract_take_grain", "Extract take grain", "@here take 100 more grain@",
                    ExpectedTest("transfer_item", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea")),
                ActionExtractorTestCase("extract_give_armor", "Extract give armor", "@give me your armor@",
                    ExpectedTest("transfer_item", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player")),
                ActionExtractorTestCase("extract_give_spoken_gold", "Extract spoken gold amount exactly", "@give me one hundred denars@",
                    ExpectedTest("give_gold_to_player", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "termsJsonContains", new ArrayList { "\"gold\":100", "\"GoldAmount\":100" })),
                ActionExtractorTestCase("extract_head_to_lycaron", "Extract head to Lycaron", "@you should head to Lycaron@",
                    ExpectedTest("go_to_settlement", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "town_ES4")),
                ActionExtractorTestCase("extract_follow_me", "Extract follow me", "@follow me please@",
                    ExpectedTest("follow", "actorHeroStringId", "rhagaea")),
                ActionExtractorTestCase("extract_raid_nearest_aserai_village", "Extract nearest Aserai village raid", "@go raid the nearest Aserai village@",
                    ExpectedTest("raid_village", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "village_aserai_1")),
                ActionExtractorTestCase("extract_raid_nearest_vlandian_village", "Extract nearest Vlandian village raid", "@go raid the nearest Vlandian village@",
                    ExpectedTest("raid_village", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "village_vlandia_1")),
                ActionExtractorTestCase("extract_sell_ships_for_gold", "Extract ship sale package", "@I offer you all my ships for 100 denars@",
                    ExpectedTest("trade_package", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionExtractorTestCase("extract_ship_price_discussed", "Extract ship sale from recent bargain context", "player: I offer you all my ships for 100 denars, as a good faith trade\nnpc: Tell me who might contest them.\nplayer: Nobody will contest them, I have the deeds here and will give them to you now for the price we discussed.",
                    ExpectedTest("trade_package", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionExtractorTestCase("extract_buy_armor_for_gold", "Extract armor purchase package", "@I will pay you 100 denars for your armor@",
                    ExpectedTest("trade_package", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "actorKingdomStringId", "empire_s", "targetKingdomStringId", "new_kingdom")),
                ActionExtractorTestCase("extract_buy_onira_for_gold", "Extract settlement purchase package", "@I will pay you 100 denars for Onira@",
                    ExpectedTest("trade_package", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "actorKingdomStringId", "empire_s", "targetKingdomStringId", "new_kingdom", "targetSettlementStringId", "town_ES8")),
                ActionExtractorTestCase("extract_buy_prisoner_for_gold", "Extract prisoner purchase package", "@I will pay you 20000 denars for the prisoner@",
                    ExpectedTest("trade_package", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "actorKingdomStringId", "empire_s", "targetKingdomStringId", "new_kingdom")),
                ActionExtractorTestCase("extract_trade_grain_horses_for_gold", "Extract multi-item trade package", "@I will sell you 50 grain and 4 horses for 1800 denars@",
                    ExpectedTest("trade_package", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionExtractorTestCase("extract_buy_grain_horses_for_gold", "Extract reverse multi-item trade package", "@Sell me 40 grain and 3 horses together for 2200 denars.@",
                    ExpectedTest("trade_package", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "actorKingdomStringId", "empire_s", "targetKingdomStringId", "new_kingdom")),
                ActionExtractorTestCase("extract_marriage_dowry_package", "Extract marriage dowry package", "@Accept a compound marriage alliance: one of your family members marries into my clan, and I will pay you 12000 denars plus 6 horses as the dowry.@",
                    ExpectedTest("diplomatic_package", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionExtractorTestCase("extract_named_marriage_dowry_package", "Extract named marriage dowry package", "@Accept a compound marriage alliance where Rhagaea specifically marries an eligible member of my clan, and I will pay you 12000 denars plus 6 horses as the dowry.@",
                    ExpectedTest("diplomatic_package", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "termsJsonContains", new ArrayList { "requestedMarriageHeroStringId", "rhagaea", "Rhagaea" })),
                ActionExtractorTestCase("extract_reverse_alliance_resource_package", "Extract reverse alliance resource package", "@Accept an alliance package where you give my kingdom 9000 denars, 40 grain, and 3 horses as the price for our public alliance.@",
                    ExpectedTest("diplomatic_package", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "termsJsonContains", new ArrayList { "items", "fromHeroStringId", "rhagaea", "toHeroStringId", "player" })),
                ActionExtractorTestCase("extract_plain_marriage_alliance", "Extract plain marriage alliance", "@Make a marriage alliance between your clan and mine.@",
                    ExpectedTest("marriage_alliance", "actorKingdomStringId", "new_kingdom", "actorClanStringId", "player_clan", "targetClanStringId", "pethros")),
                ActionExtractorTestCase("extract_named_marriage_alliance", "Extract named marriage alliance", "@Make a marriage alliance where Aurelia specifically marries an eligible member of your clan.@",
                    ExpectedTest("marriage_alliance", "actorKingdomStringId", "new_kingdom", "actorClanStringId", "player_clan", "targetClanStringId", "pethros", "termsJsonContains", new ArrayList { "requestedMarriageHeroStringId", "governor_aurelia", "Aurelia" })),
                ActionGateTestCase("gate_go_to_lycaron", "Gate planner go to Lycaron", "You should head to Lycaron.", "I will ride to Lycaron at once.", "The NPC agrees to travel to Lycaron now.",
                    "{\"actions\":[{\"action\":\"GoToSettlement\",\"fields\":{\"TargetSettlement\":\"Lycaron\"},\"confidence\":0.92,\"reason\":\"NPC accepted the travel order.\"}]}",
                    ExpectedTest("go_to_settlement", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "town_ES4")),
                ActionGateTestCase("gate_give_onira", "Gate planner give Onira", "Give me Onira.", "Onira is yours. I cede the city to your kingdom.", "The NPC agrees to cede the city of Onira to the player's kingdom.",
                    "{\"actions\":[{\"action\":\"DemandSettlementPeace\",\"fields\":{\"TargetSettlement\":\"Onira\"},\"confidence\":0.93,\"reason\":\"NPC finalized settlement surrender.\"}]}",
                    ExpectedTest("demand_settlement_peace", "targetSettlementStringId", "town_ES8")),
                ActionGateTestCase("gate_transfer_armor", "Gate planner transfer armor", "Give me your armor.", "Take my armor, then.", "The NPC agrees to give their armor to the player.",
                    "{\"actions\":[{\"action\":\"TransferItem\",\"fields\":{\"FromHero\":\"speaker\",\"ToHero\":\"player\",\"Item\":\"armor\",\"Amount\":1},\"confidence\":0.91,\"reason\":\"NPC accepted the item transfer.\"}]}",
                    ExpectedTest("transfer_item", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player")),
                ActionGateTestCase("gate_transfer_silver_spoken_amount", "Gate preserves planner and spoken item quantity", "Give me five units of Silver now.", "I accept and transfer five Silver to you now.", "The NPC accepts and completes the transfer of five Silver to the player.",
                    "{\"actions\":[{\"action\":\"TransferItem\",\"fields\":{\"FromHero\":\"speaker\",\"ToHero\":\"player\",\"Item\":\"Silver\",\"Amount\":5},\"confidence\":0.95,\"reason\":\"NPC accepted the exact item transfer.\"}]}",
                    ExpectedTest("transfer_item", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player", "termsJsonContains", new ArrayList { "\"item\":\"silver\"", "\"amount\":5" })),
                ActionGateTestCase("gate_give_gold_to_player", "Gate planner gives gold to player", "Give me one hundred denars.", "Very well. I will pay you one hundred denars.", "The NPC agrees to pay the player 100 denars.",
                    "{\"actions\":[{\"action\":\"GiveGoldToPlayer\",\"fields\":{\"actorHeroId\":\"Rhagaea\",\"GoldAmount\":100},\"confidence\":0.94,\"reason\":\"NPC accepted the gold payment.\"}]}",
                    ExpectedTest("give_gold_to_player", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player")),
                ActionGateTestCase("gate_prisoner_ransom", "Gate planner prisoner ransom", "Release the prisoner lord for twenty thousand denars.", "I accept. The prisoner lord will be released for twenty thousand denars.", "The NPC agrees to release the prisoner lord for 20000 denars.",
                    "{\"actions\":[{\"action\":\"RansomPackage\",\"fields\":{\"PrisonerHero\":\"Prisoner Lord\",\"GoldAmount\":20000},\"confidence\":0.9,\"reason\":\"NPC accepted the ransom.\"}]}",
                    ExpectedTest("ransom_package", "targetHeroStringId", "prisoner_lord")),
                ActionGateTestCase("gate_ransom_infer_named_prisoner", "Gate ransom infers named prisoner when planner omits it", "Release Prisoner Lord to me for twenty thousand denars now.", "I accept. The prisoner lord will be released for twenty thousand denars.", "Rhagaea accepts the ransom of Prisoner Lord to the player for twenty thousand denars.",
                    "{\"actions\":[{\"action\":\"RansomPackage\",\"fields\":{\"GoldAmount\":20000},\"confidence\":0.9,\"reason\":\"NPC accepted the ransom.\"}]}",
                    ExpectedTest("ransom_package", "actorKingdomStringId", "empire_s", "targetKingdomStringId", "new_kingdom", "actorClanStringId", "pethros", "targetClanStringId", "player_clan", "targetHeroStringId", "prisoner_lord", "termsJsonContains", new ArrayList { "prisoner_lord", "\"gold\":20000" })),
                ActionGateTestCase("gate_spoken_multi_item_trade_direction", "Gate preserves spoken multi-item trade direction and amounts", "Our trade is final: take forty Grain and three Imperial Chargers from me and pay me 2,200 denars.", "I accept the exact atomic trade; our prior alliance does not alter these terms.", "Rhagaea accepts receiving 40 grain and 3 Imperial Chargers from the player and paying 2,200 denars to the player.",
                    "{\"actions\":[{\"action\":\"TradePackage\",\"fields\":{\"FromHero\":\"player\",\"ToHero\":\"rhagaea\",\"GoldAmount\":2200,\"Item\":\"Grain\",\"Amount\":40},\"confidence\":0.95,\"reason\":\"Exact atomic trade accepted.\"}]}",
                    ExpectedTest("trade_package", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s", "termsJsonContains", new ArrayList { "\"itemId\":\"grain\"", "\"amount\":40", "\"itemId\":\"imperial_charger\"", "\"amount\":3", "\"goldFromHeroStringId\":\"rhagaea\"", "\"goldToHeroStringId\":\"player\"" })),
                ActionGateTestCase("gate_alliance", "Gate planner alliance", "Let us sign a full alliance.", "Then let it be sealed. Our kingdoms stand together.", "The NPC agrees to sign a full alliance with the player's kingdom.",
                    "{\"actions\":[{\"action\":\"SignAlliance\",\"fields\":{\"TargetKingdom\":\"Southern Empire\"},\"confidence\":0.9,\"reason\":\"NPC accepted the alliance.\"}]}",
                    ExpectedTest("sign_alliance", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionGateTestCase("gate_raid_village", "Gate planner raid village", "Go raid the nearest Aserai village.", "I will burn the nearest Aserai village and make them answer.", "The NPC agrees to raid the nearest Aserai village.",
                    "{\"actions\":[{\"action\":\"RaidVillage\",\"fields\":{\"TargetSettlement\":\"nearest Aserai village\"},\"confidence\":0.9,\"reason\":\"NPC accepted the raid order.\"}]}",
                    ExpectedTest("raid_village", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "village_aserai_1")),
                ActionGateTestCase("gate_patrol_town", "Gate planner patrol town", "Patrol around Lycaron.", "I will keep watch around Lycaron until the roads are quiet.", "The NPC agrees to patrol around Lycaron.",
                    "{\"actions\":[{\"action\":\"PatrolAroundSettlement\",\"fields\":{\"TargetSettlement\":\"Lycaron\"},\"confidence\":0.9,\"reason\":\"NPC accepted the patrol order.\"}]}",
                    ExpectedTest("patrol_around_settlement", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "town_ES4")),
                ActionGateTestCase("gate_duel_player", "Gate planner duel", "Face me in a friendly duel.", "Very well. We cross blades, friendly, until first yield.", "The NPC agrees to a friendly training duel with the player.",
                    "{\"actions\":[{\"action\":\"DuelPlayer\",\"fields\":{\"TargetHero\":\"player\"},\"confidence\":0.88,\"reason\":\"NPC accepted the duel.\"}]}",
                    ExpectedTest("duel_player", "actorHeroStringId", "rhagaea")),
                ActionRouterTestCase("router_follow_me", "Router follow me", "Follow me, please.", "Very well. I will follow your party.",
                    ExpectedTest("follow", "actorHeroStringId", "rhagaea")),
                ActionRouterTestCase("router_go_to_lycaron", "Router go to Lycaron", "You should head to Lycaron.", "I will ride to Lycaron at once.",
                    ExpectedTest("go_to_settlement", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "town_ES4")),
                ActionRouterTestCase("router_raid_aserai_village", "Router raid nearest Aserai village", "Go raid the nearest Aserai village.", "I will burn the nearest Aserai village and make them answer.",
                    ExpectedTest("raid_village", "actorHeroStringId", "rhagaea", "targetSettlementStringId", "village_aserai_1")),
                ActionRouterTestCase("router_give_onira", "Router give Onira", "Give me Onira.", "Onira is yours. I cede the city to your kingdom.",
                    ExpectedTest("demand_settlement_peace", "targetSettlementStringId", "town_ES8")),
                ActionRouterTestCase("router_give_armor", "Router give armor", "Give me your armor.", "Take my armor, then.",
                    ExpectedTest("transfer_item", "actorHeroStringId", "rhagaea", "targetHeroStringId", "player")),
                ActionRouterTestCase("router_sell_ships_for_gold", "Router sell ships for gold", "I offer you all my ships for 100 denars.", "I accept. I will pay the 100 denars for your ships.",
                    ExpectedTest("trade_package", "actorHeroStringId", "player", "targetHeroStringId", "rhagaea", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionRouterTestCase("router_marriage_dowry_package", "Router marriage dowry package", "Accept a compound marriage alliance: one of your family members marries into my clan, and I will pay you 12000 denars plus 6 horses as the dowry.", "I accept the marriage alliance and the dowry terms.",
                    ExpectedTest("diplomatic_package", "actorKingdomStringId", "new_kingdom", "targetKingdomStringId", "empire_s")),
                ActionRouterTestCase("router_plain_marriage_alliance", "Router plain marriage alliance", "Make a marriage alliance between your clan and mine.", "I accept. Let our clans be joined by marriage.",
                    ExpectedTest("marriage_alliance", "actorKingdomStringId", "new_kingdom", "actorClanStringId", "player_clan", "targetClanStringId", "pethros")),
                ActionRouterTestCase("router_named_marriage_alliance", "Router named marriage alliance", "Make a marriage alliance where Aurelia specifically marries an eligible member of your clan.", "I accept Aurelia as the named participant in this marriage alliance.",
                    ExpectedTest("marriage_alliance", "actorKingdomStringId", "new_kingdom", "actorClanStringId", "player_clan", "targetClanStringId", "pethros", "termsJsonContains", new ArrayList { "requestedMarriageHeroStringId", "governor_aurelia", "Aurelia" }))
            };
        }

        private static Dictionary<string, object> ActionRouterTestCase(string caseId, string name, string playerText, string npcReplyText, Dictionary<string, object> expected)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["suite"] = "action_router",
                ["caseId"] = caseId,
                ["name"] = name,
                ["description"] = "Router dry-run test. It routes player/NPC conversation text into action candidates, then runs resolver lookup, schema validation, and world validation.",
                ["mode"] = "action_router",
                ["input"] = new Dictionary<string, object>
                {
                    ["playerText"] = playerText,
                    ["npcReplyText"] = npcReplyText
                },
                ["snapshot"] = DefaultActionTestSnapshot(),
                ["llm"] = new Dictionary<string, object> { ["mode"] = "canned", ["cannedContent"] = "" },
                ["expected"] = expected,
                ["assertions"] = new Dictionary<string, object> { ["strict"] = true }
            };
        }

        private static Dictionary<string, object> ActionGateTestCase(string caseId, string name, string playerText, string npcReplyText, string gateIntent, string plannerJson, Dictionary<string, object> expected)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["suite"] = "action_gate",
                ["caseId"] = caseId,
                ["name"] = name,
                ["description"] = "Hidden action gate dry-run test. It starts from a finalized actionGate and canned planner JSON, then runs resolver lookup, schema validation, and world validation.",
                ["mode"] = "action_gate",
                ["input"] = new Dictionary<string, object>
                {
                    ["playerText"] = playerText,
                    ["npcReplyText"] = npcReplyText,
                    ["actionGate"] = new Dictionary<string, object>
                    {
                        ["needed"] = true,
                        ["commitment"] = "accepted",
                        ["intent"] = gateIntent,
                        ["confidence"] = 0.9d,
                        ["reason"] = "Canned hidden gate test."
                    }
                },
                ["snapshot"] = DefaultActionTestSnapshot(),
                ["llm"] = new Dictionary<string, object> { ["mode"] = "canned", ["cannedContent"] = plannerJson },
                ["expected"] = expected,
                ["assertions"] = new Dictionary<string, object> { ["strict"] = true }
            };
        }

        private static Dictionary<string, object> ActionExtractorTestCase(string caseId, string name, string playerText, Dictionary<string, object> expected)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["suite"] = "action_reliability",
                ["caseId"] = caseId,
                ["name"] = name,
                ["description"] = "Canned dry-run extraction test for recent live action phrasing. It starts from player text, then runs deterministic extraction, resolver lookup, schema validation, and world validation.",
                ["mode"] = "action_only",
                ["input"] = new Dictionary<string, object>
                {
                    ["playerText"] = playerText
                },
                ["snapshot"] = DefaultActionTestSnapshot(),
                ["llm"] = new Dictionary<string, object> { ["mode"] = "canned", ["cannedContent"] = "" },
                ["expected"] = expected,
                ["assertions"] = new Dictionary<string, object> { ["strict"] = true }
            };
        }

        private static Dictionary<string, object> ActionCommandTestCase(string command, string name, string playerText, Dictionary<string, object> action, Dictionary<string, object> expected)
        {
            action = action ?? new Dictionary<string, object>();
            action["command"] = command;
            ActionPipelineService.AddIfMissing(action, "reason", "Default canned Test Lab case: " + name + ".");
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["suite"] = "actions",
                ["caseId"] = command,
                ["name"] = name,
                ["description"] = "Canned dry-run action test for " + command + ". It exercises normalization, resolver lookup, schema validation, and world validation without queueing or executing in a campaign.",
                ["mode"] = "action_only",
                ["input"] = new Dictionary<string, object>
                {
                    ["playerText"] = playerText,
                    ["action"] = action
                },
                ["snapshot"] = DefaultActionTestSnapshot(),
                ["llm"] = new Dictionary<string, object> { ["mode"] = "canned", ["cannedContent"] = "" },
                ["expected"] = expected ?? ExpectedTest(command),
                ["assertions"] = new Dictionary<string, object> { ["strict"] = true }
            };
        }

        private static Dictionary<string, object> ExpectedTest(string command, params object[] extraPairs)
        {
            Dictionary<string, object> expected = TestDict("validationSuccess", true, "command", command);
            AddPairs(expected, extraPairs);
            return expected;
        }

        internal static Dictionary<string, object> DefaultActionTestSnapshot()
        {
            return new Dictionary<string, object>
            {
                ["campaignId"] = "test_campaign",
                ["payload"] = new Dictionary<string, object>
                {
                    ["campaignId"] = "test_campaign",
                    ["speakerHeroStringId"] = "rhagaea",
                    ["playerHeroStringId"] = "player",
                    ["speakerKingdomId"] = "empire_s",
                    ["playerKingdomId"] = "new_kingdom",
                    ["speakerClanId"] = "pethros",
                    ["playerClanId"] = "player_clan",
                    ["hero"] = TestDict("heroStringId", "rhagaea", "name", "Rhagaea", "kingdomId", "empire_s", "clanId", "pethros"),
                    ["sceneContext"] = new Dictionary<string, object>
                    {
                        ["mode"] = "test_lab",
                        ["currentSettlementId"] = "town_ES4",
                        ["currentSettlementName"] = "Lycaron"
                    },
                    ["actionResolutionIndex"] = DefaultActionResolutionIndex()
                }
            };
        }

        private static Dictionary<string, object> DefaultActionResolutionIndex()
        {
            return TestDict(
                "version", "test_v1",
                "settlements", TestList(
                    TestDict("settlementId", "town_ES4", "name", "Lycaron", "type", "town", "kingdomId", "empire_s", "ownerClanId", "pethros", "distance", 2),
                    TestDict("settlementId", "town_ES8", "name", "Onira", "type", "town", "kingdomId", "empire_s", "ownerClanId", "pethros", "distance", 9),
                    TestDict("settlementId", "town_ES3", "name", "Jalmarys", "type", "town", "kingdomId", "empire_w", "ownerClanId", "neretzes", "distance", 12),
                    TestDict("settlementId", "castle_battania_1", "name", "Uthelaim Castle", "type", "castle", "kingdomId", "battania", "ownerClanId", "fen_gruffendoc", "distance", 7),
                    TestDict("settlementId", "village_lycaron_1", "name", "Amitatys", "type", "village", "kingdomId", "empire_s", "ownerClanId", "pethros", "boundSettlementId", "town_ES4", "distance", 3),
                    TestDict("settlementId", "village_aserai_1", "name", "Qasira", "type", "village", "kingdomId", "aserai", "ownerClanId", "beni_zilal", "distance", 4),
                    TestDict("settlementId", "village_vlandia_1", "name", "Vlandian Test Village", "type", "village", "kingdomId", "vlandia", "ownerClanId", "dey_meroc", "distance", 6),
                    TestDict("settlementId", "hideout_mountain_1", "name", "Western Mountain Hideout", "type", "hideout", "kingdomId", "bandits", "ownerClanId", "mountain_bandits", "distance", 5)
                ),
                "kingdoms", TestList(
                    TestDict("kingdomId", "new_kingdom", "name", "fen Seanel", "formalName", "Kingdom of fen Seanel", "leaderHeroStringId", "player", "leaderName", "Aeric", "rulingClanId", "player_clan", "rulingClanName", "fen Seanel"),
                    TestDict("kingdomId", "empire_s", "name", "Southern Empire", "formalName", "The Southern Empire", "leaderHeroStringId", "rhagaea", "leaderName", "Rhagaea", "rulingClanId", "pethros", "rulingClanName", "Pethros"),
                    TestDict("kingdomId", "empire_w", "name", "Western Empire", "formalName", "The Western Empire", "leaderHeroStringId", "garios", "leaderName", "Garios", "rulingClanId", "neretzes", "rulingClanName", "Neretzes"),
                    TestDict("kingdomId", "vlandia", "name", "Vlandia", "formalName", "Kingdom of Vlandia", "leaderHeroStringId", "derthert", "leaderName", "Derthert", "rulingClanId", "dey_meroc", "rulingClanName", "dey Meroc"),
                    TestDict("kingdomId", "battania", "name", "Battania", "formalName", "Kingdom of Battania", "leaderHeroStringId", "caladog", "leaderName", "Caladog", "rulingClanId", "fen_gruffendoc", "rulingClanName", "fen Gruffendoc"),
                    TestDict("kingdomId", "aserai", "name", "Aserai", "formalName", "Aserai Sultanate", "leaderHeroStringId", "unqid", "leaderName", "Unqid", "rulingClanId", "beni_zilal", "rulingClanName", "Beni Zilal")
                ),
                "clans", TestList(
                    TestDict("clanId", "player_clan", "name", "fen Seanel", "kingdomId", "new_kingdom", "leaderHeroStringId", "player", "leaderName", "Aeric", "tier", 4, "renown", 950),
                    TestDict("clanId", "pethros", "name", "Pethros", "kingdomId", "empire_s", "leaderHeroStringId", "rhagaea", "leaderName", "Rhagaea", "tier", 5, "renown", 1600),
                    TestDict("clanId", "dey_meroc", "name", "dey Meroc", "kingdomId", "vlandia", "leaderHeroStringId", "derthert", "leaderName", "Derthert", "tier", 5, "renown", 1700),
                    TestDict("clanId", "fen_gruffendoc", "name", "fen Gruffendoc", "kingdomId", "battania", "leaderHeroStringId", "mengus", "leaderName", "Mengus", "tier", 4, "renown", 1100),
                    TestDict("clanId", "claimant_test_clan", "name", "Claimant Clan", "kingdomId", "empire_s", "leaderHeroStringId", "claimant_lord", "leaderName", "Claimant Lord", "tier", 3, "renown", 700),
                    TestDict("clanId", "exiled_test_clan", "name", "Exiled Test Clan", "kingdomId", "", "leaderHeroStringId", "exiled_lord", "leaderName", "Exiled Lord", "tier", 2, "renown", 350)
                ),
                "heroes", TestList(
                    TestDict("heroStringId", "player", "name", "Aeric", "kingdomId", "new_kingdom", "clanId", "player_clan", "isPlayer", true),
                    TestDict("heroStringId", "rhagaea", "name", "Rhagaea", "kingdomId", "empire_s", "clanId", "pethros", "isRuler", true),
                    TestDict("heroStringId", "derthert", "name", "Derthert", "kingdomId", "vlandia", "clanId", "dey_meroc", "isRuler", true),
                    TestDict("heroStringId", "caladog", "name", "Caladog", "kingdomId", "battania", "clanId", "fen_gruffendoc", "isRuler", true),
                    TestDict("heroStringId", "mengus", "name", "Mengus", "kingdomId", "battania", "clanId", "fen_gruffendoc", "partyId", "party_mengus"),
                    TestDict("heroStringId", "spouse_player", "name", "Player Spouse", "kingdomId", "new_kingdom", "clanId", "player_clan"),
                    TestDict("heroStringId", "prisoner_lord", "name", "Prisoner Lord", "kingdomId", "empire_s", "clanId", "pethros", "isPrisoner", true),
                    TestDict("heroStringId", "governor_aurelia", "name", "Aurelia", "kingdomId", "new_kingdom", "clanId", "player_clan", "governedSettlementId", "town_ES4")
                ),
                "parties", TestList(
                    TestDict("partyId", "player_party", "name", "Player Party", "leaderHeroStringId", "player", "kingdomId", "new_kingdom", "distance", 0),
                    TestDict("partyId", "party_mengus", "name", "Mengus's Party", "leaderHeroStringId", "mengus", "kingdomId", "battania", "distance", 4),
                    TestDict("partyId", "bandit_party_1", "name", "Mountain Bandit Party", "leaderHeroStringId", "", "kingdomId", "bandits", "distance", 5)
                ),
                "armies", TestList(
                    TestDict("armyId", "army_caladog", "name", "Caladog's Army", "leaderHeroStringId", "caladog", "kingdomId", "battania", "distance", 8)
                ),
                "workshops", TestList(
                    TestDict("workshopId", "town_ES4:workshop:smithy", "name", "Lycaron Smithy", "tag", "smithy", "typeName", "Smithy", "settlementId", "town_ES4", "settlementName", "Lycaron", "ownerHeroStringId", "rhagaea", "ownerName", "Rhagaea")
                ),
                "aliases", TestList(
                    TestDict("alias", "player", "entityType", "hero", "id", "player"),
                    TestDict("alias", "me", "entityType", "hero", "id", "player"),
                    TestDict("alias", "speaker", "entityType", "hero", "id", "rhagaea"),
                    TestDict("alias", "current_npc", "entityType", "hero", "id", "rhagaea"),
                    TestDict("alias", "prisoner", "entityType", "hero", "id", "prisoner_lord"),
                    TestDict("alias", "my spouse", "entityType", "hero", "id", "spouse_player"),
                    TestDict("alias", "the governor", "entityType", "hero", "id", "governor_aurelia"),
                    TestDict("alias", "player_kingdom", "entityType", "kingdom", "id", "new_kingdom"),
                    TestDict("alias", "my kingdom", "entityType", "kingdom", "id", "new_kingdom"),
                    TestDict("alias", "speaker_kingdom", "entityType", "kingdom", "id", "empire_s"),
                    TestDict("alias", "their kingdom", "entityType", "kingdom", "id", "empire_s"),
                    TestDict("alias", "current_settlement", "entityType", "settlement", "id", "town_ES4"),
                    TestDict("alias", "this city", "entityType", "settlement", "id", "town_ES4"),
                    TestDict("alias", "Lycaron", "entityType", "settlement", "id", "town_ES4"),
                    TestDict("alias", "Onira", "entityType", "settlement", "id", "town_ES8"),
                    TestDict("alias", "player party", "entityType", "party", "id", "player_party"),
                    TestDict("alias", "my party", "entityType", "party", "id", "player_party"),
                    TestDict("alias", "Mengus party", "entityType", "party", "id", "party_mengus"),
                    TestDict("alias", "bandit party", "entityType", "party", "id", "bandit_party_1"),
                    TestDict("alias", "Lycaron smithy", "entityType", "workshop", "id", "town_ES4:workshop:smithy"),
                    TestDict("alias", "strongest Battanian clan", "entityType", "clan", "id", "fen_gruffendoc"),
                    TestDict("alias", "claimant clan", "entityType", "clan", "id", "claimant_test_clan"),
                    TestDict("alias", "exiled clan", "entityType", "clan", "id", "exiled_test_clan")
                ),
                "recentContext", TestList(
                    TestDict("label", "last mentioned settlement", "entityType", "settlement", "id", "town_ES4", "name", "Lycaron"),
                    TestDict("label", "last mentioned hero", "entityType", "hero", "id", "rhagaea", "name", "Rhagaea"),
                    TestDict("label", "last mentioned clan", "entityType", "clan", "id", "pethros", "name", "Pethros")
                ),
                "assets", TestDict(
                    "player", TestDict("gold", 100000, "inventory", TestDict("topItems", TestList(
                        TestDict("itemId", "grain", "name", "Grain", "count", 300),
                        TestDict("itemId", "imperial_charger", "name", "Imperial Charger", "count", 20)
                    ))),
                    "speaker", TestDict("gold", 50000, "inventory", TestDict("topItems", TestList(
                        TestDict("itemId", "silver", "name", "Silver", "count", 50),
                        TestDict("itemId", "velvet", "name", "Velvet", "count", 10)
                    )), "appearance", TestDict("battleEquipment", TestList(
                        TestDict("itemId", "southern_scale_over_chain", "name", "Mastercrafted Southern Scale over Chain Mail", "slot", "Body", "sourceEquipmentSet", "battle", "isArmorOrClothing", true, "value", 42000)
                    ), "civilianEquipment", TestList(
                        TestDict("itemId", "southern_robes", "name", "Southern Noble Robes", "slot", "Body", "sourceEquipmentSet", "civilian", "isArmorOrClothing", true, "value", 9000)
                    )), "prisoners", TestList(
                        TestDict("heroStringId", "prisoner_lord", "name", "Prisoner Lord", "clanId", "pethros")
                    ))
                )
            );
        }
    }
}
