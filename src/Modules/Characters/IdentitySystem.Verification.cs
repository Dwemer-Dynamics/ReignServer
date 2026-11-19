using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunIdentitySubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = passed,
                ["suite"] = "identity_system",
                ["caseId"] = id,
                ["name"] = id,
                ["summary"] = summary,
                ["data"] = data ?? new Dictionary<string, object>(),
                ["durationMs"] = 0
            });

            string campaignId = "__identity_self_test_" + Guid.NewGuid().ToString("N");
            string campaignPath = Program.CampaignDirectory(campaignId);
            string courtCampaignId = campaignId + "_court";
            string courtCampaignPath = Program.CampaignDirectory(courtCampaignId);
            string introductionCampaignId = campaignId + "_introduction";
            string introductionCampaignPath = Program.CampaignDirectory(introductionCampaignId);
            try
            {
                Dictionary<string, object> player = IdentityTestHero("player", "Aeric", "player_clan", "south", true, false);
                AddContextualSelfIntroductionTests(introductionCampaignId, add);
                // The prompt adapter observes a court at day 50 and persists its own
                // live roster context. Keep it separate from the day-10 roster fixture.
                AddCourtAudienceIdentityTests(courtCampaignId, add);
                using (ReignDbConnection isolationConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureIdentitySchema(isolationConnection);
                    int priorRosterRows = ReadInt(CharactersStore.CountIdentityRoster(isolationConnection).FirstOrDefault(), "count", -1);
                    add("court_audience_fixture_isolated", priorRosterRows == 0,
                        "The persisted court prompt fixture leaves the separate roster campaign empty.",
                        new Dictionary<string, object> { ["priorRosterRows"] = priorRosterRows });
                }
                player["childrenIds"] = new List<object> { "child" };
                Dictionary<string, object> sameKingdom = IdentityTestHero("same_kingdom", "Ira", "ira_clan", "south", true, false);
                Dictionary<string, object> clanmate = IdentityTestHero("clanmate", "Alynneth", "player_clan", "", false, false);
                Dictionary<string, object> child = IdentityTestHero("child", "Child", "ward_clan", "", false, false);
                child["fatherId"] = "player";
                Dictionary<string, object> ruler = IdentityTestHero("ruler", "Rhagaea", "ruler_clan", "empire", true, true);
                Dictionary<string, object> foreign = IdentityTestHero("foreign", "Derthert", "foreign_clan", "vlandia", true, false);
                List<Dictionary<string, object>> roster = new List<Dictionary<string, object>> { player, sameKingdom, clanmate, child, ruler, foreign };
                Dictionary<string, object> syncPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["correlationId"] = "identity_self_test_sync",
                    ["worldDay"] = 10d,
                    ["portraitRosterFingerprint"] = "portrait_roster_self_test",
                    ["heroes"] = roster
                };
                Dictionary<string, object> sync = IdentitySynchronizeApi(syncPayload);
                Program.WriteJsonObject(Path.Combine(campaignPath, "portrait_roster.json"), new Dictionary<string, object>
                {
                    ["version"] = 2,
                    ["campaignId"] = campaignId,
                    ["rosterFingerprint"] = "portrait_roster_self_test",
                    ["characters"] = new List<object>()
                });
                Dictionary<string, object> secondSync = IdentitySynchronizeApi(syncPayload);

                add("portrait_roster_resynchronizes_when_server_copy_is_missing",
                    ReadBool(sync, "portraitRosterSyncRequired", false)
                    && !ReadBool(secondSync, "portraitRosterSyncRequired", true),
                    "Identity synchronization requests a portrait roster upload when the server copy is missing and accepts the matching persisted copy.",
                    null);

                add("same_kingdom_requires_identification", IdentityTestState(campaignId, "player", "same_kingdom") == "" && IdentityTestState(campaignId, "same_kingdom", "player") == "", "Kingdom membership alone grants no identity knowledge in either direction.", null);
                Dictionary<string, object> swornLadyRosterRow =
                    new Dictionary<string, object>
                    {
                        ["hero_id"] = "sworn_lady",
                        ["clan_id"] = "sworn_house",
                        ["kingdom_id"] = "realm",
                        ["is_lord"] = 1,
                        ["is_ruler"] = 0,
                        ["family_ids_json"] = "[]"
                    };
                Dictionary<string, object> ownSovereignRosterRow =
                    new Dictionary<string, object>
                    {
                        ["hero_id"] = "own_sovereign",
                        ["clan_id"] = "ruling_house",
                        ["kingdom_id"] = "realm",
                        ["is_lord"] = 1,
                        ["is_ruler"] = 1,
                        ["family_ids_json"] = "[]"
                    };
                Dictionary<string, object> sameRealmPeerRosterRow =
                    new Dictionary<string, object>
                    {
                        ["hero_id"] = "same_realm_peer",
                        ["clan_id"] = "peer_house",
                        ["kingdom_id"] = "realm",
                        ["is_lord"] = 1,
                        ["is_ruler"] = 0,
                        ["family_ids_json"] = "[]"
                    };
                add("roster_own_sovereign_is_implicit_identity",
                    ImplicitIdentitySource(
                        swornLadyRosterRow,
                        ownSovereignRosterRow) == "own_sovereign"
                    && string.IsNullOrWhiteSpace(
                        ImplicitIdentitySource(
                            swornLadyRosterRow,
                            sameRealmPeerRosterRow)),
                    "A sworn noble's own sovereign is implicit identity knowledge in the compact roster, while another same-realm peer remains unidentified.",
                    null);
                add("foreign_nobles_unknown", IdentityTestState(campaignId, "foreign", "player") == "" && IdentityTestState(campaignId, "player", "foreign") == "", "Foreign nobles receive no automatic identity knowledge.", null);
                add("ruler_network_requires_identification", IdentityTestState(campaignId, "ruler", "foreign") == "" && IdentityTestState(campaignId, "foreign", "ruler") == "", "Ruler status does not create omniscient identity knowledge in either direction.", null);
                add("same_clan_and_family", IdentityTestState(campaignId, "player", "clanmate") == "verified" && IdentityTestState(campaignId, "player", "child") == "verified" && IdentityTestState(campaignId, "child", "player") == "verified", "Same-clan and immediate-family safety rules verify identity.", null);
                add("synchronization_idempotent", ReadBool(sync, "rosterChanged", false) && !ReadBool(secondSync, "rosterChanged", true), "An unchanged roster fingerprint does not rebuild the network.", secondSync);
                using (ReignDbConnection storageConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    int storedSyntheticPairs = ReadInt(CharactersStore.CountAcquaintances(storageConnection).FirstOrDefault(), "count", -1);
                    int rosterRows = ReadInt(CharactersStore.CountIdentityRoster(storageConnection).FirstOrDefault(), "count", -1);
                    add("compact_roster_storage", storedSyntheticPairs == 0 && rosterRows == roster.Count,
                        "Automatic clan and immediate-family knowledge is derived from one roster row per hero instead of an O(N-squared) pair matrix.",
                        new Dictionary<string, object> { ["storedSyntheticPairs"] = storedSyntheticPairs, ["rosterRows"] = rosterRows });
                    HashSet<string> chemistryIndexes = new HashSet<string>(
                        CharactersStore.SelectSqliteMaster(storageConnection)
                            .Select(row => ReadString(row, "name", "")),
                        StringComparer.OrdinalIgnoreCase);
                    add("court_popularity_uses_directional_pair_indexes",
                        chemistryIndexes.Contains("idx_pair_chemistry_hero_a")
                        && chemistryIndexes.Contains("idx_pair_chemistry_hero_b"),
                        "Court-popularity refreshes can seek relationship rows by either hero direction instead of scanning the full chemistry table per noble.",
                        new Dictionary<string, object> { ["indexes"] = chemistryIndexes.OrderBy(x => x).ToList() });
                }
                Dictionary<string, object> liveRealmObserver =
                    IdentityTestHero(
                        "live_realm_observer",
                        "Marvonara",
                        "observer_clan",
                        "new_kingdom",
                        true,
                        false);
                Dictionary<string, object> liveRealmSovereign =
                    IdentityTestHero(
                        "live_realm_sovereign",
                        "Gorigos",
                        "player_faction",
                        "new_kingdom",
                        true,
                        true);
                liveRealmObserver["currentCharm"] = 200;
                liveRealmSovereign["clanTier"] = 6;
                string recognizedSovereignEncounterId =
                    IdentityRecognitionEncounterForOutcome(
                        campaignId,
                        "live_realm_observer",
                        "live_realm_sovereign",
                        true,
                        0.80d);
                Dictionary<string, object> preRosterRealmEncounter =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["observerHeroStringId"] =
                                "live_realm_observer",
                            ["subjectHeroStringId"] =
                                "live_realm_sovereign",
                            ["canonicalName"] = "Gorigos",
                            ["observer"] = liveRealmObserver,
                            ["subject"] = liveRealmSovereign,
                            ["encounterId"] =
                                recognizedSovereignEncounterId,
                            ["worldDay"] = 1d,
                            ["playerText"] = "Good evening.",
                            ["contextBundles"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["id"] =
                                            "current_settlement_facts",
                                        ["data"] =
                                            new Dictionary<string, object>
                                            {
                                                ["settlement"] =
                                                    new Dictionary<string, object>
                                                    {
                                                        ["settlementId"] =
                                                            "town_EW2",
                                                        ["name"] =
                                                            "Zeonica",
                                                        ["ownerClanId"] =
                                                            "player_faction",
                                                        ["ownerClanName"] =
                                                            "Bustamys",
                                                        ["kingdomId"] =
                                                            "new_kingdom"
                                                    }
                                            }
                                    }
                                }
                        });
                Dictionary<string, object> preRosterRealmView =
                    ReadDictionary(
                        preRosterRealmEncounter, "identityView")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> preRosterAuthority =
                    ReadDictionary(
                        preRosterRealmView, "authorityView")
                    ?? new Dictionary<string, object>();
                string sovereignPrompt =
                    BuildIdentityPromptBlock(
                        preRosterRealmView);
                add("sworn_lord_knows_current_sovereign",
                    ReadBool(
                        preRosterRealmView,
                        "canonicalNameAllowed", false)
                    && ReadString(
                        preRosterRealmView,
                        "usableName", "") == "Gorigos"
                    && ReadString(
                        preRosterRealmView,
                        "knowledgeSource", "")
                        == "own_sovereign"
                    && ReadInt(
                        preRosterRealmView,
                        "recognitionAttempts", -1) == 0
                    && ReadBool(
                        preRosterAuthority,
                        "realmSovereignKnown", false)
                    && ReadBool(
                        preRosterAuthority,
                        "subjectIsObserverSovereign", false)
                    && ReadBool(
                        preRosterAuthority,
                        "currentSettlementOwnerKnown", false)
                    && sovereignPrompt.Contains(
                        "observer's own current sovereign"),
                    "A sworn lord knows their own current sovereign without a recognition roll; the rule remains one-way and does not grant same-kingdom peer omniscience.",
                    preRosterRealmEncounter);

                add("recognition_probability_curve",
                    Math.Abs(IdentityRecognitionTierMultiplier(1) - 0.50d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(2) - 0.75d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(3) - 1d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(4) - 7d / 6d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(5) - 4d / 3d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(6) - 1.50d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 100d / 250d * IdentityRecognitionTierMultiplier(1)) - 0.20d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 100d / 250d * IdentityRecognitionTierMultiplier(3)) - 0.40d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 100d / 250d * IdentityRecognitionTierMultiplier(6)) - 0.60d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 200d / 250d * IdentityRecognitionTierMultiplier(6)) - 0.80d) < 0.000001d,
                    "Charm 100/200 and clan tiers 1/3/6 produce the configured 20/40/60 and capped 40/80/80 percent chances.",
                    null);
                double stableRollA = StableIdentityRecognitionRoll(
                    campaignId, "observer", "subject", "encounter");
                double stableRollB = StableIdentityRecognitionRoll(
                    campaignId, "observer", "subject", "encounter");
                add("recognition_roll_is_stable",
                    stableRollA >= 0d && stableRollA < 1d
                    && Math.Abs(stableRollA - stableRollB) < 0.000000000001d,
                    "The recognition roll is stable for the same campaign, pair, encounter, and rules version.",
                    stableRollA);

                Dictionary<string, object> marvonara =
                    IdentityTestHero(
                        "marvonara",
                        "Marvonara Damarorides",
                        "clan_empire_west_2",
                        "new_kingdom",
                        true,
                        false);
                marvonara["isFemale"] = true;
                marvonara["sex"] = "female";
                Dictionary<string, object> gorigos =
                    IdentityTestHero(
                        "main_hero",
                        "Gorigos",
                        "player_faction",
                        "new_kingdom",
                        true,
                        true);
                gorigos["clanTier"] = 6;
                gorigos["kingdomName"] = "Paltos";
                marvonara["kingdomName"] = "Paltos";
                Dictionary<string, object> marvonaraNative =
                    new Dictionary<string, object>
                    {
                        ["authoritative"] = true,
                        ["observedWorldDay"] = 50d,
                        ["observer"] = marvonara,
                        ["subject"] = gorigos,
                        ["settlement"] =
                            new Dictionary<string, object>
                            {
                                ["settlementId"] = "town_EW2",
                                ["name"] = "Zeonica",
                                ["ownerClanId"] = "player_faction",
                                ["ownerClanName"] = "Bustamys",
                                ["kingdomId"] = "new_kingdom",
                                ["kingdomName"] = "Paltos",
                                ["governorHeroId"] = "governor_other"
                            },
                        ["diplomacy"] =
                            new Dictionary<string, object>
                            {
                                ["playerKingdom"] =
                                    new Dictionary<string, object>
                                    {
                                        ["available"] = true,
                                        ["name"] = "Paltos",
                                        ["warCount"] = 2
                                    },
                                ["playerEnemyKingdoms"] =
                                    new List<Dictionary<string, object>>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["kingdomId"] = "enemy_a",
                                            ["name"] = "Enemy A"
                                        },
                                        new Dictionary<string, object>
                                        {
                                            ["kingdomId"] = "enemy_b",
                                            ["name"] = "Enemy B"
                                        }
                                    }
                            }
                    };
                Dictionary<string, object> marvonaraIdentity =
                    BuildIdentityView(
                        new Dictionary<string, object>
                        {
                            ["identity_state"] = "verified",
                            ["canonical_name"] = "Gorigos",
                            ["verification_source"] =
                                "recognition_fixture",
                            ["confidence"] = 1d
                        },
                        new Dictionary<string, object>
                        {
                            ["observer"] = marvonara,
                            ["subject"] = gorigos,
                            ["nativePoliticalContext"] =
                                marvonaraNative
                        });
                Dictionary<string, object> marvonaraAuthority =
                    ReadDictionary(
                        marvonaraIdentity, "authorityView")
                    ?? new Dictionary<string, object>();
                string marvonaraPrompt =
                    BuildIdentityPromptBlock(
                        marvonaraIdentity);
                add("marvonara_authority_invariant",
                    ReadBool(
                        marvonaraAuthority,
                        "subjectIsObserverSovereign", false)
                    && ReadBool(
                        marvonaraAuthority,
                        "currentSettlementOwnerKnown", false)
                    && !ReadBool(
                        marvonaraAuthority,
                        "observerClanOwnsCurrentSettlement", true)
                    && ReadInt(
                        marvonaraAuthority,
                        "currentEnemyKingdomCount", -1) == 2
                    && marvonaraPrompt.Contains(
                        "observer's own current sovereign")
                    && marvonaraPrompt.Contains(
                        "Subject primary current role: king")
                    && marvonaraPrompt.Contains(
                        "Observer-relative authority relationship: subject_is_observer_sovereign")
                    && marvonaraPrompt.Contains(
                        "current sovereign of Paltos")
                    && !marvonaraPrompt.Contains(
                        "current sovereign of kingdom `new_kingdom`")
                    && marvonaraPrompt.Contains(
                        "must not evade, redirect, or omit the answer")
                    && marvonaraPrompt.Contains(
                        "Do not volunteer war counts"),
                    "The exact live failure now has observer-relative sovereignty, player-clan ownership, and distinct current enemy kingdoms in an always-on prompt invariant.",
                    marvonaraIdentity);
                Dictionary<string, object> institutionalUnknownIdentity =
                    BuildIdentityView(
                        null,
                        new Dictionary<string, object>
                        {
                            ["mode"] = "social_event",
                            ["canonicalName"] = "Gorigos",
                            ["observer"] = marvonara,
                            ["subject"] = gorigos,
                            ["nativePoliticalContext"] = marvonaraNative
                        });
                Dictionary<string, object> institutionalUnknownAuthority =
                    ReadDictionary(institutionalUnknownIdentity,
                        "authorityView")
                    ?? new Dictionary<string, object>();
                string institutionalUnknownPrompt =
                    BuildIdentityPromptBlock(
                        institutionalUnknownIdentity);
                add("institutional_office_survives_unknown_personal_identity",
                    !ReadBool(institutionalUnknownIdentity,
                        "canonicalNameAllowed", true)
                    && ReadBool(institutionalUnknownAuthority,
                        "publicOfficeKnown", false)
                    && ReadBool(institutionalUnknownAuthority,
                        "subjectIsObserverSovereign", false)
                    && ReadString(institutionalUnknownAuthority,
                        "subjectPrimaryRole", "")
                        == "realm_sovereign"
                    && ReadStringList(institutionalUnknownAuthority,
                        "validFormalAddresses").Contains("Your Grace")
                    && institutionalUnknownPrompt.Contains(
                        "canonical name remains forbidden")
                    && institutionalUnknownPrompt.Contains(
                        "Valid formal addresses:"),
                    "A failed face/name recognition state cannot erase a sovereign office established by an official same-realm event; the NPC uses a title without leaking the name.",
                    institutionalUnknownIdentity);

                var michael = new Dictionary<string, object>(gorigos) { ["name"] = "Michael", ["clanName"] = "Howarton", ["kingdomName"] = "Howarton" };
                foreach (string valid in new[] { "Michael", "Michael Howarton", "Michael of Howarton", "  michael   howarton  " })
                    add("qualified_introduction_" + valid.Trim().Replace(" ", "_"),
                        AuthoritativeIntroductionSource(valid, michael).Length > 0, "Authoritative names accept case and spacing.", valid);
                foreach (string invalid in new[] { "Howarton", "Michael Nobody", "Michael Howarton the Great", "Mich", "Howarton Michael" })
                    add("invalid_introduction_" + invalid.Replace(" ", "_"),
                        AuthoritativeIntroductionSource(invalid, michael).Length == 0, "No arbitrary suffix, partial name or clan-only introduction.", invalid);
                add("ochivos_full_name_introduction",
                    AuthoritativeIntroductionSource(ExtractAuthoritativeSelfIntroduction("*I glance over at him* Nice to meet you Ochivos, I am Michael Howarton.", michael), michael)
                        == "authoritative_qualified_self_introduction", "The original full-name introduction is authoritative.", null);
                var nameFirstObserver = IdentityTestHero(
                    "name_first_observer", "Hulara", "hulara_clan", "khuzait", true, false);
                var nameFirstPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "name_first_observer",
                    ["subjectHeroStringId"] = "main_hero",
                    ["observer"] = nameFirstObserver,
                    ["subject"] = michael,
                    ["attendees"] = new List<Dictionary<string, object>> { nameFirstObserver, michael },
                    ["mode"] = "social_event",
                    ["encounterId"] = "name_first_social_event",
                    ["worldDay"] = 51d,
                    ["playerText"] = "*I take a seat* Michael of Howarton, Im sorry I am a traveller, I never know where I will be welcome."
                };
                Dictionary<string, object> nameFirstEncounter = IdentityEncounterApi(nameFirstPayload);
                Dictionary<string, object> nameFirstView = ReadDictionary(
                    nameFirstEncounter, "identityView") ?? new Dictionary<string, object>();
                add("name_first_social_event_introduction",
                    ReadString(nameFirstEncounter, "introductionDetected", "") == "Michael of Howarton"
                    && ReadString(nameFirstView, "identityState", "") == "verified"
                    && ReadString(nameFirstView, "usableName", "") == "Michael"
                    && ReadString(nameFirstView, "knowledgeSource", "")
                        == "authoritative_qualified_self_introduction",
                    "The exact name-first feast introduction verifies identity before the first NPC response.",
                    nameFirstEncounter);
                using (ReignDbConnection nameFirstConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                    add("name_first_social_event_persists",
                        IdentityStateVerified(ReadAcquaintance(
                            nameFirstConnection, "name_first_observer", "main_hero")),
                        "The introduced identity survives a fresh database connection.", null);
                var sameNameAttendee = IdentityTestHero(
                    "same_name_attendee", "Michael", "other_clan", "khuzait", false, false);
                var ambiguousNamePayload = new Dictionary<string, object>(nameFirstPayload)
                {
                    ["attendees"] = new List<Dictionary<string, object>>
                        { nameFirstObserver, michael, sameNameAttendee }
                };
                add("name_first_introduction_false_positive_guards",
                    ExtractNameFirstSelfIntroduction(
                        "Hulara, I am here for the feast.", michael, nameFirstPayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "I heard Michael won the tournament.", michael, nameFirstPayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "Michael, good day.", michael, ambiguousNamePayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "Hulara, good day.", nameFirstObserver, nameFirstPayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "Michael, good day.", michael, nameFirstPayload) == "Michael",
                    "Name-first identification excludes third-person mentions, addresses to another present person and non-player subjects.", null);
                var ochivos = IdentityTestHero("ochivos", "Ochivos the Wheeler", "", "", false, false);
                var civic = new Dictionary<string, object> { ["authoritative"] = true, ["available"] = true,
                    ["heroStringId"] = "ochivos", ["settlementId"] = "town_EW2", ["kingdomId"] = "new_kingdom",
                    ["sovereignHeroStringId"] = "main_hero", ["localEncounter"] = true, ["source"] = "native_home_settlement" };
                var civicNative = new Dictionary<string, object>(marvonaraNative) { ["observer"] = ochivos, ["subject"] = michael, ["observerCivicAffiliation"] = civic };
                var civicContext = new Dictionary<string, object> { ["mode"] = "social_event", ["observer"] = ochivos, ["subject"] = michael, ["nativePoliticalContext"] = civicNative };
                var civicView = BuildIdentityView(null, civicContext);
                var civicAuthority = ReadDictionary(civicView, "authorityView");
                add("ochivos_local_office_not_personal_identity",
                    !ReadBool(civicView, "canonicalNameAllowed", true) && ReadBool(civicAuthority, "subjectIsObserverSovereign", false)
                    && !ReadBool(civicAuthority, "sameKingdom", true), "Civic office never grants formal membership or personal name knowledge.", civicView);
                civic["localEncounter"] = false;
                add("visiting_notable_no_false_local_office", !ReadBool(ReadDictionary(BuildIdentityView(null, civicContext), "authorityView"),
                    "publicOfficeKnown", true), "Visiting presence alone grants no civic office recognition.", null);
                civic["localEncounter"] = true;
                civic["sovereignHeroStringId"] = "successor";
                add("civic_succession_recalculates", !ReadBool(ReadDictionary(BuildIdentityView(null, civicContext), "authorityView"),
                    "publicOfficeKnown", true), "A former sovereign does not retain the current title through civic evidence.", null);
                civic["sovereignHeroStringId"] = "main_hero";
                civic["kingdomId"] = "conqueror";
                add("civic_conquest_recalculates", !ReadBool(ReadDictionary(BuildIdentityView(null, civicContext), "authorityView"),
                    "publicOfficeKnown", true), "A realm mismatch cannot confer civic authority.", null);
                using (ReignDbConnection introductionConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureIdentitySchema(introductionConnection);
                    UpsertClaimedIdentity(introductionConnection, "ochivos", "main_hero", "Michael", "Michael Howarton", "self_introduction", "main_hero", "legacy_intro", 49d,
                        new Dictionary<string, object> { ["playerText"] = "I am Michael Howarton." });
                }
                var reconciledEncounter = IdentityEncounterApi(new Dictionary<string, object> {
                    ["campaignId"] = campaignId, ["observer"] = ochivos, ["subject"] = michael,
                    ["observerHeroStringId"] = "ochivos", ["subjectHeroStringId"] = "main_hero",
                    ["encounterId"] = "qualified_reconciliation", ["worldDay"] = 50d });
                using (ReignDbConnection reopenedIntroductionConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    var savedIntroduction = ReadAcquaintance(reopenedIntroductionConnection, "ochivos", "main_hero");
                    add("qualified_claim_reconciliation_persists", IdentityStateVerified(savedIntroduction)
                        && ReadString(savedIntroduction, "verification_source", "") == "authoritative_qualified_self_introduction_reconciled",
                        "An evidence-backed legacy introduction is verified on encounter and survives a fresh database connection.", reconciledEncounter);
                    add("qualified_claim_keeps_original_evidence", CharactersStore.SelectIdentityEvidence4(reopenedIntroductionConnection).Count > 0,
                        "Reconciliation appends verification without deleting the original claim evidence.", null);
                }
                add("marvonara_false_claims_are_rejected",
                    DialogueService.VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "A sovereign at war on twelve fronts walks into my hall."
                        },
                        marvonaraIdentity)
                    && !DialogueService.VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "Gorigos, I know you are my sovereign. Welcome to Zeonica."
                        },
                        marvonaraIdentity),
                    "False local ownership and an inflated current-war count trigger repair, while a grounded observer-relative acknowledgment passes.",
                    null);
                add("cumulative_roles_and_conversation_venue_are_enforced",
                    DialogueService.VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "You are a king. That makes you a ruler, not a lord."
                        },
                        marvonaraIdentity)
                    && DialogueService.VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "1084 Winter — Amitatys\n\nThis is Amitatys, not your clan's city."
                        },
                        marvonaraIdentity)
                    && !DialogueService.VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "1084 Winter — Zeonica\n\nYou are both king and lord, and you are my sovereign here in Zeonica."
                        },
                        marvonaraIdentity),
                    "A ruler cannot lose another simultaneously listed public role, and a remote observer's home cannot replace the authoritative conversation settlement.",
                    null);

                Dictionary<string, object> independentLord =
                    IdentityTestHero(
                        "independent_lord",
                        "Acthon",
                        "independent_clan",
                        "",
                        true,
                        false);
                Dictionary<string, object> foreignObserver =
                    IdentityTestHero(
                        "foreign_observer",
                        "Aziman Damasaqani",
                        "banu_sarmal",
                        "aserai",
                        true,
                        false);
                Dictionary<string, object> independentLordIdentity =
                    BuildIdentityView(
                        new Dictionary<string, object>
                        {
                            ["identity_state"] = "verified",
                            ["canonical_name"] = "Acthon",
                            ["verification_source"] =
                                "exact_self_introduction",
                            ["confidence"] = 1d
                        },
                        new Dictionary<string, object>
                        {
                            ["observer"] = foreignObserver,
                            ["subject"] = independentLord,
                            ["nativePoliticalContext"] =
                                new Dictionary<string, object>
                                {
                                    ["authoritative"] = true,
                                    ["observer"] = foreignObserver,
                                    ["subject"] = independentLord,
                                    ["settlement"] =
                                        new Dictionary<string, object>
                                        {
                                            ["settlementId"] =
                                                "castle_A3",
                                            ["name"] =
                                                "Ain Baliq Castle",
                                            ["ownerClanId"] =
                                                "banu_sarmal",
                                            ["ownerClanName"] =
                                                "Banu Sarmal",
                                            ["kingdomId"] = "aserai",
                                            ["governorHeroId"] = ""
                                        }
                                }
                        });
                string independentLordPrompt =
                    BuildIdentityPromptBlock(
                        independentLordIdentity);
                add("independent_lord_direct_authority_answer_contract",
                    independentLordPrompt.Contains(
                        "Subject primary current role: lord")
                    && independentLordPrompt.Contains(
                        "Observer primary current role: lord")
                    && independentLordPrompt.Contains(
                        "Observer-relative authority relationship: known_foreign_or_independent_person")
                    && independentLordPrompt.Contains(
                        "must not evade, redirect, or omit the answer")
                    && independentLordPrompt.Contains(
                        "not your sovereign")
                    && independentLordPrompt.Contains(
                        "Ain Baliq Castle is held by clan Banu Sarmal"),
                    "A verified independent lord receives explicit role, observer-relative authority, settlement ownership, and a direct-answer instruction matching the live Aziman failure.",
                    independentLordIdentity);

                List<Dictionary<string, object>> roleFixtures =
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "Queen",
                            ["isFemale"] = true,
                            ["isRuler"] = true,
                            ["isLord"] = true
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Governor",
                            ["isFemale"] = true,
                            ["isLord"] = true,
                            ["governorOfSettlementId"] = "town"
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Notable",
                            ["isNotable"] = true,
                            ["occupation"] = "Merchant"
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Wanderer",
                            ["isWanderer"] = true
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Commoner",
                            ["occupation"] = "Commoner"
                        }
                    };
                List<string> roleProjection = roleFixtures
                    .Select(fixture => string.Join(
                        ",",
                        BuildAuthoritativePublicRoles(
                            fixture)))
                    .ToList();
                add("authoritative_role_projection",
                    roleProjection[0] == "queen,lady"
                    && roleProjection[1] == "lady,governor"
                    && roleProjection[2] == "notable"
                    && roleProjection[3] == "wanderer"
                    && roleProjection[4] ==
                        "commoner_or_unaffiliated",
                    "King/queen, lord/lady, governor, notable, wanderer, and commoner roles are additive and derived from current native facts.",
                    roleProjection);

                Dictionary<string, object> transitionObserver =
                    IdentityTestHero(
                        "transition_observer",
                        "Witness",
                        "witness_clan",
                        "realm_a",
                        true,
                        false);
                Dictionary<string, object> transitionSubject =
                    IdentityTestHero(
                        "transition_subject",
                        "Later King",
                        "later_king_clan",
                        "realm_b",
                        true,
                        false);
                IdentityEncounterApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["observerHeroStringId"] =
                            "transition_observer",
                        ["subjectHeroStringId"] =
                            "transition_subject",
                        ["canonicalName"] = "Later King",
                        ["observer"] = transitionObserver,
                        ["subject"] = transitionSubject,
                        ["encounterId"] =
                            "transition_unknown",
                        ["worldDay"] = 2d,
                        ["playerText"] = "Good day."
                    });
                transitionSubject["kingdomId"] = "realm_a";
                transitionSubject["isRuler"] = true;
                Dictionary<string, object> transitioned =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["observerHeroStringId"] =
                                "transition_observer",
                            ["subjectHeroStringId"] =
                                "transition_subject",
                            ["canonicalName"] = "Later King",
                            ["observer"] = transitionObserver,
                            ["subject"] = transitionSubject,
                            ["encounterId"] =
                                "transition_sovereign",
                            ["worldDay"] = 3d,
                            ["playerText"] = "We meet again."
                        });
                Dictionary<string, object> transitionedView =
                    ReadDictionary(transitioned, "identityView")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> transitionedAuthority =
                    ReadDictionary(
                        transitionedView, "authorityView")
                    ?? new Dictionary<string, object>();
                add("sovereign_transition_still_requires_identification",
                    ReadString(
                        transitionedView,
                        "identityState", "")
                        == "encountered_unknown"
                    && !ReadBool(
                        transitionedAuthority,
                        "subjectIsObserverSovereign", true)
                    && ReadStringList(
                        transitionedAuthority,
                        "recognizedRoles").Count == 0,
                    "Becoming a same-realm sovereign does not bypass the observer's required introduction or recognition roll.",
                    transitioned);

                Dictionary<string, object> titleIntroduction =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["observerHeroStringId"] =
                                "title_intro_observer",
                            ["subjectHeroStringId"] =
                                "title_intro_subject",
                            ["canonicalName"] = "Gorigos",
                            ["observer"] =
                                IdentityTestHero(
                                    "title_intro_observer",
                                    "Marvonara",
                                    "observer_clan",
                                    "realm_a",
                                    true,
                                    false),
                            ["subject"] =
                                IdentityTestHero(
                                    "title_intro_subject",
                                    "Gorigos",
                                    "player_clan",
                                    "realm_b",
                                    true,
                                    false),
                            ["encounterId"] =
                                "natural_title_introduction",
                            ["worldDay"] = 4d,
                            ["playerText"] =
                                "Gorigos, the lord of this city.",
                            ["transcript"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["role"] = "npc",
                                        ["text"] =
                                            "Who are you?"
                                    }
                                }
                        });
                Dictionary<string, object> titleIntroductionView =
                    ReadDictionary(
                        titleIntroduction, "identityView")
                    ?? new Dictionary<string, object>();
                add("natural_name_and_title_answer_detected",
                    ReadString(
                        titleIntroduction,
                        "introductionDetected", "")
                        == "Gorigos"
                    && ReadString(
                        titleIntroductionView,
                        "identityState", "") == "verified"
                    && ReadString(
                        titleIntroductionView,
                        "usableName", "") == "Gorigos",
                    "An exact canonical name followed by an appositive title verifies identity when answering an identity question without treating arbitrary NPC address as a player name."
                        + " Observed introduction='"
                        + ReadString(titleIntroduction, "introductionDetected", "")
                        + "', state='"
                        + ReadString(titleIntroductionView, "identityState", "")
                        + "', usable='"
                        + ReadString(titleIntroductionView, "usableName", "")
                        + "'.",
                    titleIntroduction);

                Dictionary<string, object> groupIdentityPayload =
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["correlationId"] =
                            "identity_group_privacy",
                        ["mode"] = "party_chat",
                        ["conversationSessionId"] =
                            "identity_group_session",
                        ["playerHeroStringId"] = "player",
                        ["worldDay"] = 9d,
                        ["participantProfiles"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>(
                                    player,
                                    StringComparer.OrdinalIgnoreCase)
                                {
                                    ["role"] = "player"
                                },
                                clanmate,
                                foreign
                            },
                        ["activeHeroIds"] =
                            new List<object>
                            {
                                "clanmate",
                                "foreign"
                            }
                    };
                List<Dictionary<string, object>> groupViews =
                    EnsureObserverParticipantIdentityViews(
                        campaignId,
                        groupIdentityPayload,
                        "player");
                List<Dictionary<string, object>>
                    secondObserverViews =
                        EnsureObserverParticipantIdentityViews(
                            campaignId,
                            groupIdentityPayload,
                            "clanmate");
                List<Dictionary<string, object>>
                    restoredPlayerViews =
                        EnsureObserverParticipantIdentityViews(
                            campaignId,
                            groupIdentityPayload,
                            "player");
                string observerSafeTranscript =
                    FormatEventLinesForObserver(
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["speakerHeroStringId"] =
                                    "clanmate",
                                ["speaker"] = "Alynneth",
                                ["role"] = "npc",
                                ["text"] =
                                    "The eastern road is safer."
                            },
                            new Dictionary<string, object>
                            {
                                ["speakerHeroStringId"] =
                                    "foreign",
                                ["speaker"] = "Derthert",
                                ["role"] = "npc",
                                ["text"] =
                                    "I disagree about the road."
                            }
                        },
                        groupIdentityPayload,
                        "player",
                        new Dictionary<string, object>
                        {
                            ["usableName"] = "Aeric",
                            ["canonicalNameAllowed"] = true
                        });
                add("group_identity_is_observer_relative",
                    groupViews.Count == 2
                    && secondObserverViews.All(row =>
                        ReadString(
                            row,
                            "observerHeroStringId",
                            "").Equals(
                                "clanmate",
                                StringComparison.OrdinalIgnoreCase))
                    && restoredPlayerViews.All(row =>
                        ReadString(
                            row,
                            "observerHeroStringId",
                            "").Equals(
                                "player",
                                StringComparison.OrdinalIgnoreCase))
                    && observerSafeTranscript.Contains(
                        "Alynneth [id=clanmate]")
                    && !observerSafeTranscript.Contains(
                        "Derthert")
                    && observerSafeTranscript.Contains(
                        "the stranger")
                    && IdentityTestState(
                        campaignId,
                        "foreign",
                        "player") == "",
                    "A shared transcript reveals a same-clan participant but withholds an unidentified NPC's canonical name without creating reverse-direction knowledge.",
                    observerSafeTranscript);
                DialogueService.UpdateSharedGroupConversationState(
                    campaignId,
                    groupIdentityPayload,
                    "foreign",
                    "Derthert",
                    "I am Derthert.",
                    "identity_group_session",
                    100);
                string safeCachedSpeaker =
                    DialogueService.BuildSharedGroupConversationPrompt(
                        campaignId,
                        groupIdentityPayload,
                        "player",
                        new List<Dictionary<string, object>>(),
                        "Continue.");
                add("group_self_introduction_is_witnessed_and_directional",
                    IdentityTestState(
                        campaignId,
                        "player",
                        "foreign") == "verified"
                    && IdentityTestState(
                        campaignId,
                        "clanmate",
                        "foreign") == "verified"
                    && IdentityTestState(
                        campaignId,
                        "foreign",
                        "player") == ""
                    && safeCachedSpeaker.Contains(
                        "Last contributing speaker: the stranger")
                    && !safeCachedSpeaker.Contains(
                        "Last contributing speaker: Derthert"),
                    "An exact NPC self-introduction is persisted for every present observer, remains directional, and cannot retroactively leak through a stale cached observer view in the same prompt.",
                    safeCachedSpeaker);
                Dictionary<string, object> debugKnown = IdentityDebugKnowEveryoneApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["correlationId"] = "identity_self_test_know_everyone",
                    ["worldDay"] = 10d,
                    ["testMode"] = true,
                    ["observer"] = player,
                    ["heroes"] = roster
                });
                add("debug_know_everyone_player_only", ReadBool(debugKnown, "ok", false)
                    && ReadInt(debugKnown, "known", 0) == roster.Count - 1
                    && ReadInt(debugKnown, "requested", 0) == roster.Count - 1
                    && ReadBool(debugKnown, "complete", false)
                    && IdentityTestState(campaignId, "player", "foreign") == "verified"
                    && IdentityTestState(campaignId, "foreign", "player") == "", "The guarded debug helper makes only the player know every living roster hero.", debugKnown);

                Dictionary<string, object> encounterBase = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "foreign",
                    ["subjectHeroStringId"] = "player",
                    ["canonicalName"] = "Aeric",
                    ["observer"] = foreign,
                    ["subject"] = player,
                    ["mode"] = "dialogue",
                    ["worldDay"] = 11d
                };
                Dictionary<string, object> firstEncounter = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_one", ["playerText"] = "Good day." });
                Dictionary<string, object> repeatedEncounter = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_one", ["playerText"] = "Still here." });
                Dictionary<string, object> nextEncounter = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_two", ["playerText"] = "We meet again." });
                Dictionary<string, object> unknownView = ReadDictionary(firstEncounter, "identityView") ?? new Dictionary<string, object>();
                Dictionary<string, object> nextView = ReadDictionary(nextEncounter, "identityView") ?? new Dictionary<string, object>();
                Dictionary<string, object> unknownAuthority =
                    ReadDictionary(unknownView, "authorityView")
                    ?? new Dictionary<string, object>();
                string unknownPrompt =
                    BuildIdentityPromptBlock(unknownView);
                add("unknown_prompt_view",
                    !ReadBool(
                        unknownView, "canonicalNameAllowed", true)
                    && ReadString(
                        unknownView, "usableName", "")
                        .IndexOf(
                            "Aeric",
                            StringComparison.OrdinalIgnoreCase) < 0
                    && ReadString(
                        unknownAuthority,
                        "subjectPrimaryRole", "")
                        == "identity_unverified"
                    && ReadBool(
                        unknownView,
                        "subjectSexKnown", false)
                    && ReadString(
                        unknownView,
                        "subjectSex", "") == "male"
                    && unknownPrompt.Contains(
                        "Subject visible sex: male")
                    && unknownPrompt.Contains(
                        "does not reveal or prove a name, noble title")
                    && !unknownPrompt.Contains(
                        "Subject primary current role: lord"),
                    "Unknown observers receive a scene-safe label and authoritative visible sex, but no hidden native identity or role before identification.",
                    unknownView);
                Dictionary<string, object> unknownFemaleView =
                    BuildIdentityView(
                        null,
                        new Dictionary<string, object>
                        {
                            ["mode"] = "party_chat",
                            ["canonicalName"] = "Hidden Woman",
                            ["subject"] =
                                new Dictionary<string, object>
                                {
                                    ["heroStringId"] =
                                        "unknown_female_player",
                                    ["name"] = "Hidden Woman",
                                    ["isFemale"] = true,
                                    ["isLord"] = true
                                }
                        });
                string unknownFemalePrompt =
                    BuildIdentityPromptBlock(unknownFemaleView);
                add("unknown_female_sex_is_visible_without_role_leak",
                    ReadString(
                        unknownFemaleView,
                        "subjectSex", "") == "female"
                    && unknownFemalePrompt.Contains(
                        "Subject visible sex: female")
                    && unknownFemalePrompt.Contains(
                        "Subject primary current role: identity_unverified")
                    && !unknownFemalePrompt.Contains(
                        "Recognized current roles: lady"),
                    "Party and event prompts can preserve a woman's visible sex without leaking her unknown name or inferring the title of lady.",
                    unknownFemaleView);
                add("recognition_is_once_per_encounter_with_cooldown",
                    ReadInt(ReadDictionary(repeatedEncounter, "identityView"), "recognitionAttempts", -1) == 1
                    && ReadInt(nextView, "recognitionAttempts", -1) == 1
                    && ReadString(
                        ReadDictionary(
                            nextEncounter,
                            "recognition"),
                        "status", "") == "cooldown"
                    && !ReadBool(ReadDictionary(nextEncounter, "recognition"), "recognized", true)
                    && !ReadBool(nextView, "canonicalNameAllowed", true),
                    "A failed deterministic recognition roll is not duplicated by transport retries or rapid session reopening.", nextEncounter);

                Dictionary<string, object> claim = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_three", ["playerText"] = "My name is Rowan." });
                Dictionary<string, object> claimView = ReadDictionary(claim, "identityView") ?? new Dictionary<string, object>();
                add("false_alias_claimed", ReadString(claimView, "identityState", "") == "claimed" && ReadString(claimView, "usableName", "") == "Rowan" && !ReadBool(claimView, "canonicalNameAllowed", true), "A self-introduced alias remains usable but unverified.", claimView);
                string verifiedAliasPrompt = BuildIdentityPromptBlock(
                    new Dictionary<string, object>
                    {
                        ["identityState"] = "verified",
                        ["usableName"] = "Aeric",
                        ["claimedName"] = "Rowan",
                        ["canonicalNameAllowed"] = true,
                        ["instruction"] =
                            "The observer knows the verified identity Aeric."
                    });
                add("verified_identity_dominates_earlier_alias",
                    verifiedAliasPrompt.Contains(
                        "Earlier conflicting claimed name: Rowan")
                    && verifiedAliasPrompt.Contains(
                        "does not replace the verified identity Aeric")
                    && verifiedAliasPrompt.Contains(
                        "Never address the person as the earlier claim"),
                    "A stale or false earlier alias remains auditable but cannot replace the later verified canonical identity in the prompt.",
                    verifiedAliasPrompt);
                string sanitizedVerifiedPrompt =
                    SanitizePromptForIdentity(
                        verifiedAliasPrompt
                            + "\nCURRENT PLAN: Refuse Rowan until he explains himself."
                            + "\nPLAYER TEXT: I claim my rank entitles me to your time.",
                        "I claim my rank entitles me to your time.",
                        "Aeric",
                        new Dictionary<string, object>
                        {
                            ["identityState"] = "verified",
                            ["usableName"] = "Aeric",
                            ["claimedName"] = "Rowan",
                            ["canonicalNameAllowed"] = true,
                            ["instruction"] =
                                "The observer knows the verified identity Aeric."
                        });
                add("verified_identity_sanitizes_stale_alias_context",
                    sanitizedVerifiedPrompt.Contains(
                        "CURRENT PLAN: Refuse Aeric")
                    && Regex.Matches(
                        sanitizedVerifiedPrompt,
                        @"(?<![\p{L}\p{N}])Rowan(?![\p{L}\p{N}])",
                        RegexOptions.IgnoreCase).Count == 1,
                    "A verified canonical identity rewrites stale alias-bearing state while preserving the one authoritative attributed identity record.",
                    sanitizedVerifiedPrompt);
                add("introduction_false_positive_guard", string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("I am a lord.")) && string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("I am ready.")) && string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("Yes, I am here for the tournament.")) && ExtractSelfIntroducedName("I am Aeric.") == "Aeric", "Occupations, ordinary states, and conversational openers are not mistaken for names, while an explicit capitalized identity remains valid.", null);
                add("direct_address_is_not_self_introduction",
                    string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("Menor, I am giving you this silver cup as a sincere gift."))
                    && ExtractSelfIntroducedName("Osarios, Im actually here for the tournament.", true) == "Osarios",
                    "A name-first address to an NPC cannot rename the player; name-first answers remain available only when an NPC actually requested the player's identity.", null);
                using (ReignDbConnection identityConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    UpsertClaimedIdentity(identityConnection, "alias_cleanup_observer", "player", "Aeric", "Menor", "self_introduction", "player",
                        "legacy_address_encounter", 11d, new Dictionary<string, object>
                        {
                            ["playerText"] = "Menor, I am giving you this silver cup as a sincere gift.",
                            ["transcript"] = new List<Dictionary<string, object>>()
                        });
                }
                Dictionary<string, object> repairedAlias = IdentityEncounterApi(new Dictionary<string, object>(encounterBase)
                {
                    ["observerHeroStringId"] = "alias_cleanup_observer", ["observer"] = new Dictionary<string, object> { ["heroStringId"] = "alias_cleanup_observer", ["name"] = "Witness" },
                    ["encounterId"] = "explicit_repair_encounter", ["playerText"] = "My name is Aeric."
                });
                Dictionary<string, object> repairedAliasView = ReadDictionary(repairedAlias, "identityView") ?? new Dictionary<string, object>();
                List<string> repairedAliases;
                using (ReignDbConnection identityConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    repairedAliases = DialogueService.TextListFromJson(ReadString(ReadStoredAcquaintance(identityConnection, "alias_cleanup_observer", "player"), "aliases_json", "[]"));
                }
                add("explicit_name_repairs_legacy_address_alias", ReadString(repairedAliasView, "identityState", "") == "verified"
                    && ReadString(repairedAliasView, "usableName", "") == "Aeric"
                    && !repairedAliases.Contains("Menor", StringComparer.OrdinalIgnoreCase),
                    "A direct self-introduction removes a legacy alias whose only evidence was a name-first NPC address.", repairedAliasView);
                add("real_name_introduction_detected",
                    ExtractSelfIntroducedName("For clarity, I am introducing myself to you now: my real name is Rhovarion.") == "Rhovarion",
                    "Natural explicit 'my real name is' wording records the claimed identity before the response is generated.", null);
                add("natural_name_answer_detected",
                    ExtractSelfIntroducedName("*I glance over at her and give her a friendly smile* Osarios, Im actually here for the tournament, just waiting for it to start.", true) == "Osarios",
                    "A natural name-first answer to an identity question records the claimed name.", null);
                add("name_correction_detected",
                    ExtractSelfIntroducedName("*I stop to face her* Im sorry, when I said Osarios, I mean that is my name. Ill be using my own.") == "Osarios",
                    "An explicit correction that identifies an earlier word as the player's name records the claim.", null);
                string sanitized = SanitizePromptForIdentity("PROFILE: Aeric\nMEMORY: Aeric visited Onira.\nLATEST: My name is Aeric.", "My name is Aeric.", "Aeric", claimView);
                add("prompt_privacy_sanitization", !sanitized.Contains("PROFILE: Aeric") && !sanitized.Contains("MEMORY: Aeric") && sanitized.Contains("LATEST: My name is Aeric."), "Canonical names are removed from prompt context while the latest explicit introduction remains visible.", sanitized);

                using (ReignDbConnection identityConnection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    UpsertVerifiedIdentity(
                        identityConnection, ruler, foreign,
                        "self_test_prior_knowledge", "ruler",
                        12d, "trusted_intro_setup");
                    UpsertVerifiedIdentity(
                        identityConnection, ruler, player,
                        "self_test_prior_knowledge", "ruler",
                        12d, "trusted_intro_setup");
                }
                Dictionary<string, object> trusted = IdentityIntroductionApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "foreign",
                    ["subjectHeroStringId"] = "player",
                    ["introducerHeroStringId"] = "ruler",
                    ["claimedName"] = "Aeric",
                    ["canonicalName"] = "Aeric",
                    ["kind"] = "third_party",
                    ["encounterId"] = "court_intro",
                    ["worldDay"] = 12d
                });
                add("trusted_introduction_verifies", ReadBool(trusted, "ok", false) && ReadBool(trusted, "verified", false) && IdentityTestState(campaignId, "foreign", "player") == "verified", "A trusted introducer who knows both people verifies the identity.", trusted);
                Dictionary<string, object> untrusted = IdentityIntroductionApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "same_kingdom",
                    ["subjectHeroStringId"] = "foreign",
                    ["introducerHeroStringId"] = "clanmate",
                    ["claimedName"] = "Derthert",
                    ["canonicalName"] = "Derthert",
                    ["kind"] = "third_party"
                });
                add("untrusted_introduction_rejected", !ReadBool(untrusted, "ok", true), "An introducer without verified knowledge of both people cannot verify identity.", untrusted);

                sameKingdom["kingdomId"] = "new_kingdom";
                IdentitySynchronizeApi(new Dictionary<string, object>(syncPayload) { ["heroes"] = roster, ["worldDay"] = 13d });
                add("knowledge_survives_defection", IdentityTestState(campaignId, "player", "same_kingdom") == "verified", "Synchronization never removes learned identity after a kingdom change.", null);
            }
            catch (Exception ex)
            {
                add("identity_self_test_exception", false, "Identity subsystem self-test threw an exception: " + ex.Message, ex.ToString());
            }
            finally
            {
                TryDeleteIdentitySelfTestCampaign(courtCampaignPath);
                TryDeleteIdentitySelfTestCampaign(introductionCampaignPath);
                TryDeleteIdentitySelfTestCampaign(campaignPath);
            }
            return results;
        }
#endif

        private static Dictionary<string, object> IdentityTestHero(string id, string name, string clanId, string kingdomId, bool isLord, bool isRuler)
        {
            return new Dictionary<string, object>
            {
                ["heroStringId"] = id,
                ["name"] = name,
                ["clanId"] = clanId,
                ["kingdomId"] = kingdomId,
                ["isLord"] = isLord,
                ["isRuler"] = isRuler,
                ["isAlive"] = true,
                ["isAdult"] = true,
                ["isFemale"] = false,
                ["sex"] = "male",
                ["occupation"] = isLord ? "Lord" : "Commoner",
                ["isNotable"] = false,
                ["isWanderer"] = false,
                ["clanTier"] = 3,
                ["currentCharm"] = 0,
                ["childrenIds"] = new List<object>()
            };
        }

        private static string IdentityRecognitionEncounterForOutcome(
            string campaignId,
            string observerId,
            string subjectId,
            bool recognized,
            double probability)
        {
            for (int i = 0; i < 10000; i++)
            {
                string encounterId =
                    "recognition_fixture_"
                    + i.ToString(
                        CultureInfo.InvariantCulture);
                bool outcome = StableIdentityRecognitionRoll(
                    campaignId,
                    observerId,
                    subjectId,
                    encounterId) < probability;
                if (outcome == recognized)
                    return encounterId;
            }
            throw new InvalidOperationException(
                "Unable to find a deterministic recognition fixture.");
        }

        private static string IdentityTestState(string campaignId, string observerId, string subjectId)
        {
            Dictionary<string, object> query = IdentityQueryApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["observerHeroStringId"] = observerId,
                ["subjectHeroStringId"] = subjectId,
                ["limit"] = 1
            });
            return ReadString(ReadDictionaryList(query, "acquaintances").FirstOrDefault(), "identity_state", "");
        }

        private static void TryDeleteIdentitySelfTestCampaign(string campaignPath)
        {
            try
            {
                string root = Path.GetFullPath(Program.CampaignsRoot()) + Path.DirectorySeparatorChar;
                string target = Path.GetFullPath(campaignPath ?? "");
                if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("__identity_self_test_", StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    Directory.Delete(target, true);
                }
            }
            catch { }
        }
    }
}
