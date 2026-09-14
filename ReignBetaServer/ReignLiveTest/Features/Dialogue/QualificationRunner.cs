using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static readonly long[] QualificationSeeds = { 271828L, 314159L, 161803L };
        private static int QualificationRunsToSkip;
        private static int QualificationReloadsToSkip;
        private static int QualificationRollbacksToSkip;
        private static string QualificationScenarioBuildVersion = "";

        private static Dictionary<string, object> RunQualification(
            string[] args, string campaignId, Dictionary<string, object> runtime)
        {
            Dictionary<string, object> plannedCoverage = QualificationPlanCoverage();
            if (!IsOk(plannedCoverage))
                throw new InvalidOperationException("Qualification scenario matrix cannot satisfy its declared gates: "
                    + Json.Serialize(plannedCoverage));
            Dictionary<string, object> nativeRuntime = ReadObject(runtime, "runtime");
            bool prepareAuthorityFixture =
                Has(args, "--prepare-authority-fixture");
            Dictionary<string, object> characterInitialization =
                ReadObject(nativeRuntime, "characterInitialization");
            if (!ReadBoolean(characterInitialization, "notableBackgroundReady"))
                throw new InvalidOperationException(
                    "Qualification cannot select notables before Reign's gated campaign-day-one "
                    + "background-character initialization is complete. Wait for the "
                    + "'Background characters are ready' completion, then resume the same "
                    + "qualification. Runtime evidence: " + Json.Serialize(characterInitialization));
            Post("/tests/live/arm", new Dictionary<string, object>
            {
                ["confirmation"] = "arm", ["minutes"] = 720, ["requestedBy"] = "ReignLiveTest qualification"
            });
            string stateFingerprint =
                FinalGauntletStateFingerprint(campaignId);
            Dictionary<string, object> scorecard = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&stateFingerprint="
                + Uri.EscapeDataString(stateFingerprint));
            string qualificationId = String(scorecard, "qualificationId");
            bool freshQualification = string.IsNullOrWhiteSpace(qualificationId)
                || Has(args, "--reset")
                || !ReadBoolean(scorecard, "sameStateFingerprint")
                || !string.Equals(
                    String(scorecard, "buildVersion"),
                    String(scorecard, "currentBuildVersion"),
                    StringComparison.OrdinalIgnoreCase);
            string priorBaselineSave = String(scorecard, "baselineSaveName");
            string rotatingBaselineSave = priorBaselineSave.EndsWith(
                    "_A", StringComparison.OrdinalIgnoreCase)
                ? "Reign_Conversation_Qualification_Baseline_B"
                : "Reign_Conversation_Qualification_Baseline_A";
            string immutableBaselineSave = FirstNonEmpty(
                Value(args, "--baseline-save", ""),
                rotatingBaselineSave);
            if (freshQualification)
            {
                scorecard = Post("/tests/live/readiness/reset", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = Value(args, "--qualification", ""),
                    ["baselineSaveName"] = immutableBaselineSave,
                    ["stateFingerprint"] = stateFingerprint,
                    ["notes"] = "Unattended qualification with a maximum 40-sample goal per readiness category"
                });
                qualificationId = String(scorecard, "qualificationId");
            }
            if (string.IsNullOrWhiteSpace(qualificationId))
                throw new InvalidOperationException("The server did not create a qualification id.");
            QualificationScenarioBuildVersion = FirstNonEmpty(
                String(scorecard, "currentBuildVersion"),
                String(scorecard, "buildVersion"),
                "unknown-build");
            QualificationRunsToSkip = freshQualification
                ? 0 : (int)ReadLong(scorecard, "completedRunCount", 0);
            QualificationReloadsToSkip = freshQualification
                ? 0 : (int)ReadLong(scorecard, "reloadCount", 0);
            QualificationRollbacksToSkip = freshQualification
                ? 0 : (int)ReadLong(scorecard, "rollbackCount", 0);
            if (freshQualification)
            {
                string[] baselineArgs =
                {
                    "game", "save", "--campaign", campaignId, "--save", immutableBaselineSave,
                    "--wait", "300", "--json"
                };
                Dictionary<string, object> baseline = SaveGame(baselineArgs);
                if (!IsOk(baseline))
                    throw new InvalidOperationException(
                        "Could not create the immutable qualification baseline: " + Json.Serialize(baseline));
                Post("/tests/live/readiness/marker", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = qualificationId,
                    ["type"] = "baseline",
                    ["passed"] = true,
                    ["saveName"] = immutableBaselineSave,
                    ["gameInstanceId"] = RuntimeInstance(Get(
                        "/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId))),
                    ["evidence"] = "Immutable qualification baseline created before the first qualifying exchange."
                });
            }
            if (prepareAuthorityFixture
                && !ReadBoolean(nativeRuntime, "playerIsRuler"))
            {
                EnsureQualificationAuthorityFixture(
                    campaignId, qualificationId);
                runtime = QualificationRuntimeWithHeartbeatGrace(
                    campaignId);
                nativeRuntime = ReadObject(runtime, "runtime");
                if (!ReadBoolean(nativeRuntime, "playerIsRuler")
                    || string.IsNullOrWhiteSpace(
                        String(nativeRuntime, "playerKingdomId")))
                    throw new InvalidOperationException(
                        "The disposable authority fixture completed, but the fresh game heartbeat does not identify the player as the native kingdom ruler: "
                        + Json.Serialize(nativeRuntime));
            }

            List<Dictionary<string, object>> individualTargets = QualificationTargets(campaignId, "individual_chat", 500);
            if (individualTargets.Count < 25)
                throw new InvalidOperationException("Qualification requires at least 25 living non-player heroes; only " + individualTargets.Count + " were available.");
            List<Dictionary<string, object>> partyTargets =
                QualificationTargets(
                    campaignId, "party_chat", 50);
            if (partyTargets.Count < 5)
            {
                List<string> fixtureCandidates = individualTargets
                    .OrderBy(target => ReadBoolean(target, "isLord") ? 1 : 0)
                    .ThenBy(target => ReadBoolean(target, "isWanderer") ? 0 : 1)
                    .ThenBy(target => string.IsNullOrWhiteSpace(
                        String(target, "currentSettlementId")) ? 1 : 0)
                    .Select(target => String(target, "heroId"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToList();
                EnsureQualificationPartyFixture(
                    campaignId, qualificationId, fixtureCandidates);
                partyTargets = QualificationTargets(
                    campaignId, "party_chat", 50);
            }
            if (partyTargets.Count < 5)
                throw new InvalidOperationException("Qualification requires five party-chat-eligible heroes for the five-person group matrix; only " + partyTargets.Count + " were available.");
            // The native search result is name-ranked. Taking its first rows repeatedly
            // made the same alphabetically early, heavily-tested NPCs absorb every
            // qualification window. Spread selection deterministically across the full
            // eligible roster so persistent hostility or history on one old test subject
            // cannot masquerade as a general recall failure.
            Dictionary<string, object> recordedRoster = ReadObject(scorecard, "targetRoster");
            List<string> heroIds = ReadRosterIds(recordedRoster, "individualHeroIds");
            List<string> partyHeroIds = ReadRosterIds(recordedRoster, "partyHeroIds");
            List<string> focal = ReadRosterIds(recordedRoster, "focalHeroIds");
            List<string> courtTargetIds = ReadRosterIds(recordedRoster, "courtHeroIds");
            Dictionary<string, Dictionary<string, object>> targetsById = individualTargets
                .Where(target => !string.IsNullOrWhiteSpace(String(target, "heroId")))
                .GroupBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            bool recordedRosterValid = heroIds.Count >= 30
                && partyHeroIds.Count >= 5
                && focal.Count == 5
                && courtTargetIds.Count >= 25
                && heroIds.All(targetsById.ContainsKey)
                && focal.All(targetsById.ContainsKey)
                && courtTargetIds.All(targetsById.ContainsKey);
            if (!recordedRosterValid)
            {
                heroIds = OrderQualificationTargetsByUsage(individualTargets, qualificationId)
                    .Select(target => String(target, "heroId"))
                    .Take(30).ToList();
                partyHeroIds = partyTargets.Select(target => String(target, "heroId"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Take(10).ToList();
                focal = heroIds.Take(5).ToList();
                courtTargetIds = SelectCourtIntrigueTargets(individualTargets, 30)
                    .Select(target => String(target, "heroId")).ToList();
                Dictionary<string, object> rosterSaved = Post("/tests/live/readiness/roster", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = qualificationId,
                    ["targetRoster"] = new Dictionary<string, object>
                    {
                        ["individualHeroIds"] = heroIds,
                        ["partyHeroIds"] = partyHeroIds,
                        ["focalHeroIds"] = focal,
                        ["courtHeroIds"] = courtTargetIds
                    }
                });
                if (!IsOk(rosterSaved))
                    throw new InvalidOperationException("Could not persist the immutable qualification target roster: "
                        + Json.Serialize(rosterSaved));
            }
            List<Dictionary<string, object>> courtTargets = courtTargetIds
                .Where(targetsById.ContainsKey).Select(id => targetsById[id]).ToList();
            string qualificationFocus = Value(args, "--focus", "").Trim();
            bool manipulationOnly = qualificationFocus.Equals(
                "manipulation", StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "manipulation_capabilities", StringComparison.OrdinalIgnoreCase);
            bool lieRelationshipOnly = qualificationFocus.Equals(
                "lie", StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "lie_relationship", StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "relationship_consequences", StringComparison.OrdinalIgnoreCase);
            bool clanTierOnly = qualificationFocus.Equals(
                "clan_tier", StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "clan_tier_recognition", StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "clan", StringComparison.OrdinalIgnoreCase);
            bool identityAuthorityOnly =
                qualificationFocus.Equals(
                    "identity",
                    StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "identity_role_authority",
                    StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "authority",
                    StringComparison.OrdinalIgnoreCase);
            bool identityAuthorityModesOnly =
                qualificationFocus.Equals(
                    "identity_role_authority_modes",
                    StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "identity_modes",
                    StringComparison.OrdinalIgnoreCase)
                || qualificationFocus.Equals(
                    "authority_modes",
                    StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(qualificationFocus)
                && !manipulationOnly
                && !lieRelationshipOnly
                && !clanTierOnly
                && !identityAuthorityOnly
                && !identityAuthorityModesOnly)
                throw new InvalidOperationException(
                    "Unsupported qualification focus '" + qualificationFocus
                    + "'. Supported focused gates: identity_role_authority, identity_role_authority_modes, clan_tier, manipulation, lie_relationship.");
            int courtNobles = courtTargets.Count(target => ReadBoolean(target, "isLord"));
            int courtTiers = courtTargets.Where(target => ReadBoolean(target, "isLord"))
                .Select(target => (int)ReadLong(target, "clanTier", 0)).Distinct().Count();
            int courtWomen = courtTargets.Count(target => ReadBoolean(target, "isFemale"));
            int courtMen = courtTargets.Count - courtWomen;
            if (!lieRelationshipOnly
                && !identityAuthorityOnly
                && !identityAuthorityModesOnly
                && (courtTargets.Count < 25 || courtNobles < 16
                    || courtTiers < 3 || courtWomen == 0 || courtMen == 0))
                throw new InvalidOperationException(
                    "Court-intrigue qualification requires at least 25 stratified targets, including 16 nobles, "
                    + "three clan tiers, and both women and men. Selected targets=" + courtTargets.Count
                    + ", nobles=" + courtNobles + ", tiers=" + courtTiers
                    + ", women=" + courtWomen + ", men=" + courtMen + ".");
            int completedRuns = 0;
            List<Dictionary<string, object>> runFailures = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> manipulationCases =
                new List<Dictionary<string, object>>();
            if (identityAuthorityOnly)
            {
                QualificationRunsToSkip = 0;
                List<Dictionary<string, object>> identityTargets =
                    SelectIdentityAuthorityTargets(
                        individualTargets,
                        qualificationId,
                        6);
                return RunIdentityAuthorityQualification(
                    campaignId,
                    qualificationId,
                    nativeRuntime,
                    identityTargets,
                    plannedCoverage);
            }
            if (identityAuthorityModesOnly)
            {
                QualificationRunsToSkip = 0;
                return RunIdentityAuthorityModeQualification(
                    campaignId,
                    qualificationId,
                    nativeRuntime,
                    partyHeroIds,
                    individualTargets,
                    plannedCoverage);
            }
            if (lieRelationshipOnly)
            {
                // Focused resumes reconcile stable scenario ids directly. Do
                // not consume the generic completed-run cursor, which includes
                // earlier focused gates and target/fixture operations.
                QualificationRunsToSkip = 0;
                List<string> lieRelationshipHeroIds =
                    OrderQualificationTargetsByUsage(
                        individualTargets, qualificationId)
                        .Select(target =>
                            String(target, "heroId"))
                        .Where(id =>
                            !string.IsNullOrWhiteSpace(id))
                        .Take(30)
                        .ToList();
                Dictionary<string, object> recentRelationships = Get(
                    "/relationships/conversation/recent?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&limit=250");
                HashSet<string> priorGiftParticipants =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> benefit
                    in ReadObjects(recentRelationships, "benefits"))
                {
                    if (String(benefit, "giver_id").Equals(
                        "main_hero",
                        StringComparison.OrdinalIgnoreCase))
                        priorGiftParticipants.Add(
                            String(benefit, "recipient_id"));
                }
                foreach (Dictionary<string, object> receipt
                    in ReadObjects(recentRelationships, "receipts"))
                {
                    string act = String(receipt, "act_kind");
                    if (act.IndexOf(
                            "gift",
                            StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string observer = String(
                        receipt, "observer_id");
                    string target = String(receipt, "target_id");
                    string recipient = String(
                        receipt, "gift_recipient_id");
                    if (target.Equals(
                        "main_hero",
                        StringComparison.OrdinalIgnoreCase))
                        priorGiftParticipants.Add(observer);
                    if (observer.Equals(
                        "main_hero",
                        StringComparison.OrdinalIgnoreCase))
                        priorGiftParticipants.Add(target);
                    if (!string.IsNullOrWhiteSpace(recipient))
                        priorGiftParticipants.Add(recipient);
                }
                foreach (Dictionary<string, object> partyTarget
                    in partyTargets)
                {
                    Dictionary<string, object> history = Post(
                        "/dialogue/history",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["heroStringId"] =
                                String(partyTarget, "heroId"),
                            ["limit"] = 500
                        });
                    partyTarget["priorDialogueLineCount"] =
                        IsOk(history)
                            ? ReadObjects(history, "lines").Count
                            : 500;
                    Dictionary<string, object> memoryContext = Post(
                        "/memory/build_context",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["npcId"] =
                                String(partyTarget, "heroId"),
                            ["playerId"] = "main_hero",
                            ["currentTopic"] =
                                "prior dealings conversations gifts lies relationship history with the player",
                            ["tokenBudget"] = 3000
                        });
                    int priorMemorySourceCount = IsOk(memoryContext)
                        ? QualificationReadStrings(
                            memoryContext, "sourceSummaryIds").Count
                            + QualificationReadStrings(
                                memoryContext, "sourceMemoryIds").Count
                            + QualificationReadStrings(
                                memoryContext, "sourceEventIds").Count
                        : 500;
                    partyTarget["priorMemorySourceCount"] =
                        priorMemorySourceCount;
                    partyTarget["priorConversationEvidenceCount"] =
                        ReadLong(
                            partyTarget,
                            "priorDialogueLineCount",
                            500)
                        + priorMemorySourceCount
                        + (priorGiftParticipants.Contains(
                                String(partyTarget, "heroId"))
                            ? 10000
                            : 0);
                    partyTarget["priorGiftRelationshipEvidence"] =
                        priorGiftParticipants.Contains(
                            String(partyTarget, "heroId"));
                }
                List<string> lieRelationshipPartyHeroIds =
                    OrderQualificationTargetsByUsage(
                        partyTargets,
                        qualificationId)
                        .Select(target =>
                            String(target, "heroId"))
                        .Where(id =>
                            !string.IsNullOrWhiteSpace(id))
                        .Take(10)
                        .ToList();
                return RunLieRelationshipQualification(
                    campaignId,
                    qualificationId,
                    lieRelationshipHeroIds,
                    lieRelationshipPartyHeroIds,
                    plannedCoverage);
            }
            if (clanTierOnly)
            {
                QualificationRunsToSkip = 0;
                List<Dictionary<string, object>> clanTierNativeTargets =
                    QualificationTargets(
                        campaignId,
                        "individual_chat",
                        2000,
                        new Dictionary<string, object>
                        {
                            ["minimumAge"] = 18d,
                            ["minClanTier"] = 0,
                            ["maxClanTier"] = 6
                        },
                        includeHistory: false);
                List<Dictionary<string, object>> clanTierCases =
                    BuildClanTierRecognitionQualificationCases(
                        clanTierNativeTargets,
                        qualificationId);
                List<string> clanTierTargetIds = clanTierCases
                    .Select(row => String(row, "heroId"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, Dictionary<string, object>>
                    clanTierTargetsById = clanTierNativeTargets
                        .Where(target => !string.IsNullOrWhiteSpace(
                            String(target, "heroId")))
                        .GroupBy(
                            target => String(target, "heroId"),
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.First(),
                            StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> identityFixture = Post(
                    "/tests/live/fixtures/manipulation",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["playerHeroId"] = String(
                            nativeRuntime, "playerHeroId"),
                        ["playerName"] = String(
                            nativeRuntime, "playerName"),
                        ["worldDay"] = ReadLong(
                            nativeRuntime, "worldDay", 0L),
                        ["verifyIdentity"] = true,
                        ["targetHeroIds"] =
                            clanTierTargetIds.ToArray(),
                        ["targetSnapshots"] =
                            clanTierTargetIds
                                .Select(id =>
                                    clanTierTargetsById[id])
                                .ToArray()
                    });
                if (!IsOk(identityFixture)
                    || !ReadBoolean(
                        identityFixture, "identityPrepared")
                    || ReadLong(
                        identityFixture,
                        "verifiedObserverCount", 0)
                        != clanTierTargetIds.Count)
                {
                    throw new InvalidOperationException(
                        "Could not prepare verified player identity for "
                        + "the clan-tier qualification matrix: "
                        + Json.Serialize(identityFixture));
                }
                return RunClanTierRecognitionQualification(
                    campaignId,
                    qualificationId,
                    clanTierCases,
                    plannedCoverage);
            }
            if (manipulationOnly)
            {
                // Focused resumes reconcile their deterministic scenario ids
                // directly. A generic completed-run cursor can include target
                // searches or fixture setup and would otherwise skip real probes.
                QualificationRunsToSkip = 0;
                List<Dictionary<string, object>>
                    manipulationNativeTargets =
                        QualificationTargets(
                            campaignId,
                            "individual_chat",
                            2000,
                            new Dictionary<string, object>
                            {
                                ["isLord"] = true,
                                ["minimumAge"] = 18d,
                                ["minClanTier"] = 1,
                                ["maxClanTier"] = 6
                            },
                            includeHistory: false);
                Dictionary<string, Dictionary<string, object>>
                    manipulationTargetsById =
                        manipulationNativeTargets
                            .Where(target =>
                                !string.IsNullOrWhiteSpace(
                                    String(target, "heroId")))
                            .GroupBy(
                                target => String(target, "heroId"),
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                group => group.Key,
                                group => group.First(),
                                StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string,
                    Dictionary<string, object>> target
                    in manipulationTargetsById)
                {
                    targetsById[target.Key] = target.Value;
                }
                Dictionary<string, object> manipulationFixture =
                    PostWithTimeout(
                    "/tests/live/fixtures/manipulation",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["playerHeroId"] = String(
                            nativeRuntime, "playerHeroId"),
                        ["playerName"] = String(
                            nativeRuntime, "playerName"),
                        ["worldDay"] = ReadLong(
                            nativeRuntime, "worldDay", 0L),
                        ["verifyIdentity"] = false,
                        ["targetHeroIds"] = manipulationNativeTargets
                            .Select(target => String(target, "heroId"))
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Take(2000).ToArray(),
                        ["targetSnapshots"] =
                            manipulationNativeTargets
                                .Take(2000).ToArray()
                    },
                    900);
                if (!IsOk(manipulationFixture))
                    throw new InvalidOperationException(
                        "Could not scan the manipulation personality "
                        + "fixture: "
                        + Json.Serialize(manipulationFixture));
                List<Dictionary<string, object>> manipulationProfiles =
                    ReadObjects(manipulationFixture, "targets")
                        .Where(row =>
                            manipulationTargetsById.ContainsKey(
                                String(row, "heroId")))
                        .Select(row =>
                        {
                            Dictionary<string, object> merged =
                                new Dictionary<string, object>(
                                    row,
                                    StringComparer.OrdinalIgnoreCase);
                            Dictionary<string, object> native =
                                manipulationTargetsById[
                                    String(row, "heroId")];
                            merged["clanTier"] =
                                ReadLong(native, "clanTier", 0);
                            merged["isFemale"] =
                                ReadBoolean(native, "isFemale");
                            merged["age"] = ReadLong(native, "age", 0);
                            merged["priorDialogueLineCount"] = 500;
                            return merged;
                        }).ToList();
                manipulationProfiles = manipulationProfiles
                    .Where(row =>
                        ReadBoolean(
                            row, "courtCharacterAvailable")
                        && ReadBoolean(
                            row, "supportsManipulation")
                        && ReadLong(row, "honorLevel", 0) < 0
                        && ReadLong(row, "age", 0) >= 18
                        && ReadLong(row, "clanTier", 0) >= 0
                        && ReadLong(row, "clanTier", 0) <= 6)
                    .ToList();
                foreach (Dictionary<string, object> profile
                    in manipulationProfiles)
                {
                    Dictionary<string, object> history = Post(
                        "/dialogue/history",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["heroStringId"] =
                                String(profile, "heroId"),
                            ["limit"] = 500
                        });
                    profile["priorDialogueLineCount"] =
                        IsOk(history)
                            ? ReadObjects(history, "lines").Count
                            : 500;
                }
                manipulationCases =
                    BuildManipulationQualificationCases(
                        manipulationProfiles);
                List<string> manipulationCaseTargetIds =
                    manipulationCases
                        .Select(row => String(row, "heroId"))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                Dictionary<string, object> identityFixture = Post(
                    "/tests/live/fixtures/manipulation",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["playerHeroId"] = String(
                            nativeRuntime, "playerHeroId"),
                        ["playerName"] = String(
                            nativeRuntime, "playerName"),
                        ["worldDay"] = ReadLong(
                            nativeRuntime, "worldDay", 0L),
                        ["verifyIdentity"] = true,
                        ["targetHeroIds"] =
                            manipulationCaseTargetIds.ToArray(),
                        ["targetSnapshots"] =
                            manipulationCaseTargetIds
                                .Select(id =>
                                    manipulationTargetsById[id])
                                .ToArray()
                    });
                if (!IsOk(identityFixture)
                    || !ReadBoolean(
                        identityFixture, "identityPrepared")
                    || ReadLong(
                        identityFixture,
                        "verifiedObserverCount", 0)
                        != manipulationCaseTargetIds.Count)
                {
                    throw new InvalidOperationException(
                        "Could not prepare verified player identity for "
                        + "the selected manipulation targets: "
                        + Json.Serialize(identityFixture));
                }
                courtTargets = manipulationCases
                    .Select(row => String(row, "heroId"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => targetsById[id]).ToList();
            }
            WarmQualificationMemory(campaignId, focal[0], qualificationId, null);
            List<List<string>> relationshipHistoryPairs =
                ReadRosterPairs(
                    recordedRoster, "relationshipHistoryPairs");
            if (relationshipHistoryPairs.Count < 5)
            {
                relationshipHistoryPairs =
                    SelectQualificationRelationshipHistoryPairs(
                        campaignId, partyHeroIds, 5);
                Dictionary<string, object> pairRosterSaved = Post(
                    "/tests/live/readiness/roster",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["targetRoster"] = new Dictionary<string, object>
                        {
                            ["individualHeroIds"] = heroIds,
                            ["partyHeroIds"] = partyHeroIds,
                            ["focalHeroIds"] = focal,
                            ["courtHeroIds"] = courtTargetIds,
                            ["relationshipHistoryPairs"] =
                                relationshipHistoryPairs
                                    .Select(pair => pair.ToArray()).ToArray()
                        }
                    });
                if (!IsOk(pairRosterSaved))
                    throw new InvalidOperationException(
                        "Could not persist the immutable relationship-history "
                        + "pair roster: " + Json.Serialize(pairRosterSaved));
            }
            bool focusCoverageGapsFirst = !Has(args, "--full-order");
            int individualSceneOrdinal = 0;

            // Highest-risk, least-proven court-intrigue gates run first. Keep
            // clan recognition and manipulation in separate scenarios so a
            // passing manipulative response cannot conceal weak rank handling,
            // and vice versa.
            if (!manipulationOnly)
            {
                for (int probe = 0; probe < 30; probe++)
                {
                    Dictionary<string, object> target =
                        courtTargets[probe % courtTargets.Count];
                    string targetId = String(target, "heroId");
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[probe % QualificationSeeds.Length],
                        "individual_chat",
                        "Clan-tier recognition qualification " + probe
                            + " target " + targetId
                            + " tier " + ReadLong(target, "clanTier", 0),
                        new List<Dictionary<string, object>>
                        {
                            OpenStep("individual_chat", new[] { targetId }),
                            ClanTierRecognitionProbe(probe),
                            CloseStep("individual_chat")
                        },
                        runFailures);
                    completedRuns++;
                }
            }

            for (int probe = 0; probe < 40; probe++)
            {
                Dictionary<string, object> manipulationCase =
                    manipulationOnly
                        ? manipulationCases[probe]
                        : new Dictionary<string, object>();
                Dictionary<string, object> target = manipulationOnly
                    ? targetsById[String(manipulationCase, "heroId")]
                    : courtTargets[probe % courtTargets.Count];
                string targetId = String(target, "heroId");
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>();
                if (manipulationOnly)
                    steps.Add(ManipulationFixtureStep(
                        manipulationCase));
                steps.Add(OpenStep(
                    "individual_chat", new[] { targetId }));
                steps.Add(ManipulationProbe(
                    probe, manipulationCase));
                steps.Add(CloseStep("individual_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[(probe + 1)
                        % QualificationSeeds.Length],
                    "individual_chat",
                    "Manipulation qualification " + probe
                        + " target " + targetId
                        + " tier " + ReadLong(target, "clanTier", 0)
                        + (manipulationOnly
                            ? " quadrant "
                                + String(manipulationCase, "quadrant")
                                + " cell "
                                + String(manipulationCase,
                                    "courtCharacterCell")
                                + " honor "
                                + ReadLong(manipulationCase,
                                    "honorLevel", 0)
                                + " boldness "
                                + ReadLong(manipulationCase,
                                    "boldnessLevel", 0)
                            : " calculating "
                                + ReadLong(target,
                                    "calculating", 0)),
                    steps,
                    runFailures);
                completedRuns++;
            }

            if (manipulationOnly)
            {
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[0],
                    "individual_chat",
                    "Restore manipulation opportunity fixture",
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] =
                                "restore_manipulation_fixture",
                            ["mode"] = "individual_chat",
                            ["timeoutSeconds"] = 120
                        }
                    },
                    runFailures);
                completedRuns++;
                Dictionary<string, object> identityRestore = Post(
                    "/tests/live/fixtures/manipulation",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["operation"] = "restore"
                    });
                if (!IsOk(identityRestore))
                    throw new InvalidOperationException(
                        "Manipulation qualification completed, but its "
                        + "identity fixture could not be restored: "
                        + Json.Serialize(identityRestore));
                Dictionary<string, object> focused = Get(
                    "/tests/live/readiness?campaignId="
                    + Uri.EscapeDataString(campaignId));
                focused["controllerCompletedRuns"] = completedRuns;
                focused["controllerFailureCount"] = runFailures.Count;
                focused["controllerFailures"] = runFailures;
                focused["qualificationId"] = qualificationId;
                focused["qualificationFocus"] = "manipulation_capabilities";
                focused["plannedCoverage"] = plannedCoverage;
                return focused;
            }

            RunSharedRelationshipHistoryQualificationSetup(
                campaignId, qualificationId, relationshipHistoryPairs,
                ref completedRuns, runFailures);
            if (focusCoverageGapsFirst)
            {
                // Keep this order invariant across controller restarts. The
                // completed-run cursor skips already durable scenes, while an
                // interrupted fresh window must still encounter every identity,
                // harmless-fact, commitment, and soft-canon prerequisite before
                // any dependent delayed-memory probe.
                RunQualificationIndividualScenes(
                    campaignId, qualificationId, focal,
                    new[] { 0, 1, 2, 3, 4, 5, 6, 7, 11 },
                    relationshipHistoryPairs,
                    ref individualSceneOrdinal, ref completedRuns, runFailures);
            }
            else
            {
                RunQualificationIndividualScenes(
                    campaignId, qualificationId, focal,
                    Enumerable.Range(0, 12),
                    relationshipHistoryPairs,
                    ref individualSceneOrdinal, ref completedRuns, runFailures);
            }

            for (int seedIndex = 0; seedIndex < QualificationSeeds.Length; seedIndex++)
            {
                long seed = QualificationSeeds[seedIndex];
                for (int scene = 0; scene < 7; scene++)
                {
                    List<string> group = new List<string>();
                    int size = scene % 3 == 0 ? 2 : scene % 3 == 1 ? 3 : 5;
                    for (int offset = 0; offset < size; offset++)
                        group.Add(partyHeroIds[(seedIndex * 9 + scene * 3 + offset) % partyHeroIds.Count]);
                    List<Dictionary<string, object>> steps = new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", group)
                    };
                    for (int turn = 0; turn < 2; turn++)
                        steps.Add(GroupProbe(seedIndex, scene, turn, group, "party_chat"));
                    steps.Add(CloseStep("party_chat"));
                    int checkpointIndex = scene == 6 ? 3 + seedIndex : -1;
                    if (checkpointIndex >= 0) steps.Add(SaveStep(checkpointIndex));
                    RunQualificationScenario(campaignId, qualificationId, seed, "party_chat",
                        "Party qualification " + seedIndex + "-" + scene, steps, runFailures);
                    if (checkpointIndex >= 0)
                        ReloadQualificationCheckpoint(
                            campaignId, qualificationId, checkpointIndex,
                            focal[checkpointIndex % focal.Count],
                            relationshipHistoryPairs, runFailures);
                    completedRuns++;
                }
            }

            for (int seedIndex = 0; seedIndex < QualificationSeeds.Length; seedIndex++)
            {
                long seed = QualificationSeeds[seedIndex];
                for (int scene = 0; scene < 4; scene++)
                {
                    List<string> group = new List<string>
                    {
                        heroIds[(seedIndex * 7 + scene * 3) % heroIds.Count],
                        heroIds[(seedIndex * 7 + scene * 3 + 1) % heroIds.Count],
                        heroIds[(seedIndex * 7 + scene * 3 + 2) % heroIds.Count]
                    };
                    List<Dictionary<string, object>> steps = new List<Dictionary<string, object>>
                    {
                        OpenStep("social_event", group),
                        GroupProbe(seedIndex, scene, 0, group, "social_event"),
                        GroupProbe(seedIndex, scene, 1, group, "social_event"),
                        CloseStep("social_event")
                    };
                    int checkpointIndex = scene == 3 ? 6 + seedIndex : -1;
                    if (checkpointIndex >= 0) steps.Add(SaveStep(checkpointIndex));
                    RunQualificationScenario(campaignId, qualificationId, seed, "social_event",
                        "Social-event qualification " + seedIndex + "-" + scene, steps, runFailures);
                    if (checkpointIndex >= 0)
                        ReloadQualificationCheckpoint(
                            campaignId, qualificationId, checkpointIndex,
                            focal[checkpointIndex % focal.Count],
                            relationshipHistoryPairs, runFailures);
                    completedRuns++;
                }
            }

            List<string> wildernessParticipants = partyHeroIds.Where(id => id.StartsWith("CharacterObject_", StringComparison.OrdinalIgnoreCase))
                .Take(5).ToList();
            if (wildernessParticipants.Count < 2) wildernessParticipants = partyHeroIds.Take(5).ToList();
            List<Dictionary<string, object>> prepareSteps = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["operation"] = "prepare_wilderness",
                    ["mode"] = "wilderness_event",
                    ["targetSearches"] = wildernessParticipants.ToArray(),
                    ["timeoutSeconds"] = 120
                }
            };
            RunQualificationScenario(campaignId, qualificationId, QualificationSeeds[0], "wilderness_event",
                "Prepare disposable wilderness party", prepareSteps, runFailures);
            completedRuns++;
            for (int scene = 0; scene < 20; scene++)
            {
                long seed = QualificationSeeds[scene % QualificationSeeds.Length];
                List<Dictionary<string, object>> steps = new List<Dictionary<string, object>>
                {
                    OpenStep("wilderness_event", new string[0]),
                    WildernessProbe(scene),
                    CloseStep("wilderness_event")
                };
                int checkpointIndex = scene == 19 ? 9 : -1;
                if (checkpointIndex >= 0) steps.Add(SaveStep(checkpointIndex));
                RunQualificationScenario(campaignId, qualificationId, seed, "wilderness_event",
                    "Wilderness qualification " + scene, steps, runFailures);
                if (checkpointIndex >= 0)
                    ReloadQualificationCheckpoint(
                        campaignId, qualificationId, checkpointIndex,
                        focal[checkpointIndex % focal.Count],
                        relationshipHistoryPairs, runFailures);
                completedRuns++;
            }
            RunQualificationScenario(campaignId, qualificationId, QualificationSeeds[2], "wilderness_event",
                "Restore disposable wilderness party", new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["schemaVersion"] = 2,
                        ["operation"] = "restore_settlement",
                        ["mode"] = "wilderness_event",
                        ["timeoutSeconds"] = 120
                    }
                }, runFailures);
            completedRuns++;
            if (focusCoverageGapsFirst)
            {
                IEnumerable<int> remainingIndividualScenes =
                    new[] { 8, 9, 10 };
                RunQualificationIndividualScenes(
                    campaignId, qualificationId, focal,
                    remainingIndividualScenes,
                    relationshipHistoryPairs,
                    ref individualSceneOrdinal, ref completedRuns, runFailures);
            }
            RunQualificationScenario(campaignId, qualificationId, QualificationSeeds[0], "individual_chat",
                "Finalize qualification memory, consolidation, and semantic evidence",
                new List<Dictionary<string, object>> { MemoryCompletionStep() }, runFailures);
            completedRuns++;
            RunQualificationRollbackTest(campaignId, qualificationId, focal[0], runFailures);

            Dictionary<string, object> final = Get("/tests/live/readiness?campaignId=" + Uri.EscapeDataString(campaignId));
            final["controllerCompletedRuns"] = completedRuns;
            final["controllerFailureCount"] = runFailures.Count;
            final["controllerFailures"] = runFailures;
            final["qualificationId"] = qualificationId;
            final["plannedCoverage"] = plannedCoverage;
            return final;
        }

        private static void RunQualificationIndividualScenes(
            string campaignId,
            string qualificationId,
            List<string> focal,
            IEnumerable<int> sceneOrder,
            List<List<string>> relationshipHistoryPairs,
            ref int individualSceneOrdinal,
            ref int completedRuns,
            List<Dictionary<string, object>> runFailures)
        {
            foreach (int focalScene in sceneOrder)
            {
                for (int focalIndex = 0; focalIndex < focal.Count; focalIndex++)
                {
                    int seedIndex = individualSceneOrdinal % QualificationSeeds.Length;
                    long seed = QualificationSeeds[seedIndex];
                    string target = focal[focalIndex];
                    List<Dictionary<string, object>> steps = new List<Dictionary<string, object>>
                    {
                        OpenStep("individual_chat", new[] { target })
                    };
                    for (int turn = 0; turn < 4; turn++)
                        steps.Add(IndividualProbe(focalIndex, focalScene, turn));
                    steps.Add(CloseStep("individual_chat"));
                    int checkpointIndex = individualSceneOrdinal == 19
                        || individualSceneOrdinal == 39
                        || individualSceneOrdinal == 59
                            ? individualSceneOrdinal / 20
                            : -1;
                    if (checkpointIndex >= 0) steps.Add(SaveStep(checkpointIndex));
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        seed,
                        "individual_chat",
                        "Focal individual qualification " + focalIndex + " scene " + focalScene,
                        steps,
                        runFailures);
                    if (checkpointIndex >= 0)
                        ReloadQualificationCheckpoint(
                            campaignId,
                            qualificationId,
                            checkpointIndex,
                            focal[checkpointIndex % focal.Count],
                            relationshipHistoryPairs,
                            runFailures);
                    individualSceneOrdinal++;
                    completedRuns++;
                }
            }
        }

        private static Dictionary<string, object> QualificationPlanCoverage()
        {
            Dictionary<string, int> required = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["structural_pipeline"] = 40,
                ["identity_evidence"] = 40,
                ["identity_role_authority"] = 40,
                ["short_cross_scene_memory"] = 40,
                ["long_term_memory"] = 40,
                ["dynamic_characteristics"] = 40,
                ["shared_relationship_history"] = 40,
                ["group_awareness"] = 40,
                ["world_local_knowledge"] = 40,
                ["lie_relationship"] = 40,
                ["clan_tier_recognition"] = 30,
                ["manipulation_capabilities"] = 40,
                ["personality_consistency"] = 40,
                ["detailed_factual_accuracy"] = 40,
                ["performance_resilience"] = 40
            };
            Dictionary<string, int> planned = required.Keys.ToDictionary(
                key => key, key => 0, StringComparer.OrdinalIgnoreCase);
            int dynamicRecallCommands = 0;
            Action<Dictionary<string, object>, int> add = (step, minimumReplies) =>
            {
                if (step == null
                    || !String(step, "operation").Equals("send", StringComparison.OrdinalIgnoreCase))
                    return;
                if (step.TryGetValue("readinessCategories", out object rawCategories)
                    && rawCategories is IEnumerable<string> categories)
                {
                    foreach (string category in categories.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (planned.ContainsKey(category))
                            planned[category] += minimumReplies;
                    }
                }
                Dictionary<string, object> assertions = ReadObject(step, "assertions");
                if (assertions.TryGetValue("requiresDynamicCharacteristicRecall", out object rawRecall)
                    && Convert.ToBoolean(rawRecall))
                    dynamicRecallCommands++;
            };

            for (int focalIndex = 0; focalIndex < 5; focalIndex++)
                for (int scene = 0; scene < 12; scene++)
                {
                    for (int turn = 0; turn < 4; turn++)
                        add(IndividualProbe(focalIndex, scene, turn), 1);
                }
            for (int probe = 0; probe < 30; probe++)
                add(ClanTierRecognitionProbe(probe), 1);
            for (int probe = 0; probe < 40; probe++)
                add(ManipulationProbe(probe), 1);

            for (int seedIndex = 0; seedIndex < QualificationSeeds.Length; seedIndex++)
            {
                for (int scene = 0; scene < 7; scene++)
                {
                    int size = scene % 3 == 0 ? 2 : scene % 3 == 1 ? 3 : 5;
                    List<string> group = Enumerable.Range(0, size)
                        .Select(index => "party_" + index).ToList();
                    for (int turn = 0; turn < 2; turn++)
                        add(GroupProbe(seedIndex, scene, turn, group, "party_chat"), size);
                }
                for (int scene = 0; scene < 4; scene++)
                {
                    List<string> group = new List<string> { "social_0", "social_1", "social_2" };
                    for (int turn = 0; turn < 2; turn++)
                        add(GroupProbe(seedIndex, scene, turn, group, "social_event"), 3);
                }
            }
            List<string> relationshipPlanPair =
                new List<string> { "relationship_0", "relationship_1" };
            for (int pair = 0; pair < 5; pair++)
            {
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "cold", false), 2);
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "reuse", false), 2);
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "transition", false), 2);
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "reuse", false), 2);
            }
            for (int reload = 0; reload < 10; reload++)
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "reuse", true), 2);
            for (int scene = 0; scene < 20; scene++)
                add(WildernessProbe(scene), 1);

            HashSet<int> socialTargetIndices = new HashSet<int>();
            const int minimumBroadPool = 25;
            for (int seedIndex = 0; seedIndex < QualificationSeeds.Length; seedIndex++)
                for (int scene = 0; scene < 4; scene++)
                    for (int offset = 0; offset < 3; offset++)
                        socialTargetIndices.Add((seedIndex * 7 + scene * 3 + offset) % minimumBroadPool);

            List<Dictionary<string, object>> categoriesResult = required.Select(entry =>
                new Dictionary<string, object>
                {
                    ["category"] = entry.Key,
                    ["plannedMinimumReplies"] = planned[entry.Key],
                    ["requiredReplies"] = entry.Value,
                    ["margin"] = planned[entry.Key] - entry.Value,
                    ["reachable"] = planned[entry.Key] >= entry.Value
                }).ToList();
            int plannedSceneClosuresBeforeTerminalGate =
                180 + 21 + 12 + 20 + 10 + 10;
            int plannedCheckpoints = 3 + 3 + 3 + 1;
            List<string> individualPromptTexts = Enumerable.Range(0, 48)
                .Select(selector => String(IndividualProbe(0, selector / 4, selector % 4), "text"))
                .ToList();
            bool reciprocalPromptContract = individualPromptTexts[0].IndexOf("roads are safe", StringComparison.OrdinalIgnoreCase) >= 0
                && individualPromptTexts[0].IndexOf("share as much as I ask", StringComparison.OrdinalIgnoreCase) >= 0
                && !individualPromptTexts.Any(text => text.IndexOf("more important confidence", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("I will then answer", StringComparison.OrdinalIgnoreCase) >= 0);
            List<Dictionary<string, object>> usageFixture = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroId"] = "heavily_used", ["priorDialogueLineCount"] = 40, ["priorMemorySourceCount"] = 0 },
                new Dictionary<string, object> { ["heroId"] = "memory_only", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 4 },
                new Dictionary<string, object> { ["heroId"] = "gift_used", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 0, ["priorGiftRelationshipEvidence"] = true },
                new Dictionary<string, object> { ["heroId"] = "fresh_b", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 0 },
                new Dictionary<string, object> { ["heroId"] = "fresh_a", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 0 }
            };
            List<Dictionary<string, object>> orderedUsageFixture = OrderQualificationTargetsByUsage(usageFixture, "fixture");
            bool leastUsedTargetContract = orderedUsageFixture.Count == 5
                && ReadLong(orderedUsageFixture[0], "priorConversationEvidenceCount", -1) == 0
                && ReadLong(orderedUsageFixture[1], "priorConversationEvidenceCount", -1) == 0
                && orderedUsageFixture.Skip(2).All(target =>
                    ReadLong(target, "priorConversationEvidenceCount", -1) > 0);
            bool ok = categoriesResult.All(row => Convert.ToBoolean(row["reachable"]))
                && dynamicRecallCommands >= 40
                && socialTargetIndices.Count >= 25
                && plannedSceneClosuresBeforeTerminalGate >= 100
                && plannedCheckpoints >= 10
                && reciprocalPromptContract
                && leastUsedTargetContract;
            return new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["schemaVersion"] = 1,
                ["categories"] = categoriesResult,
                ["plannedMinimumReplies"] = planned["structural_pipeline"],
                ["plannedDynamicCharacteristicCaptureOpportunities"] = planned["dynamic_characteristics"],
                ["plannedDynamicCharacteristicRecallCommands"] = dynamicRecallCommands,
                ["plannedDistinctBroadTargets"] = socialTargetIndices.Count,
                ["plannedSceneClosuresBeforeTerminalGate"] = plannedSceneClosuresBeforeTerminalGate,
                ["plannedCheckpoints"] = plannedCheckpoints,
                ["plannedReloads"] = plannedCheckpoints,
                ["plannedRollbackChecks"] = 1,
                ["plannedRelationshipHistoryPairs"] = 5,
                ["plannedRelationshipHistoryColdCommands"] = 5,
                ["plannedRelationshipHistoryTransitionReplies"] = 10,
                ["plannedRelationshipHistoryPersistenceReplies"] = 20,
                ["reciprocalIndividualPrompts"] = reciprocalPromptContract,
                ["leastUsedTargetSelection"] = leastUsedTargetContract,
                ["seedCount"] = QualificationSeeds.Length,
                ["modes"] = new[] { "individual_chat", "party_chat", "social_event", "wilderness_event" }
            };
        }

        private static List<Dictionary<string, object>> QualificationTargets(
            string campaignId,
            string mode,
            int limit,
            Dictionary<string, object> filters = null,
            bool includeHistory = true)
        {
            string targetRunId = "target-search-"
                + Guid.NewGuid().ToString("N");
            Dictionary<string, object> started =
                new Dictionary<string, object>();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Dictionary<string, object> runtime =
                    QualificationRuntimeWithHeartbeatGrace(campaignId);
                Dictionary<string, object> request =
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["gameInstanceId"] = RuntimeInstance(runtime),
                        ["runId"] = targetRunId,
                        ["mode"] = mode,
                        ["search"] = "",
                        ["limit"] = limit
                    };
                foreach (KeyValuePair<string, object> filter
                    in filters
                        ?? new Dictionary<string, object>())
                {
                    request[filter.Key] = filter.Value;
                }
                try
                {
                    // Target enumeration is game-side and normally completes in
                    // under a second, but a large preceding evidence/report write
                    // can hold the unified server request loop beyond the ordinary
                    // 90-second CLI timeout. The caller supplies a stable run ID,
                    // waits on the long-operation budget, and can therefore
                    // reconcile a late server acceptance without issuing a second
                    // search command.
                    started = PostWithTimeout(
                        "/tests/live/targets/search", request, 300);
                }
                catch (TaskCanceledException)
                {
                    started = new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["runId"] = targetRunId,
                        ["transportAmbiguous"] = true
                    };
                }
                if (IsOk(started)) break;
                string error = String(started, "error");
                if (attempt >= 3
                    || error.IndexOf(
                        "fresh loaded-game heartbeat",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException(
                        "Qualification target discovery failed for "
                        + mode + ": " + Json.Serialize(started));
                Thread.Sleep(2000);
            }
            Dictionary<string, object> completed = WaitForRun(
                campaignId,
                FirstNonEmpty(String(started, "runId"), targetRunId),
                "", 300, true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Qualification target discovery did not complete for "
                    + mode + ": " + Json.Serialize(completed));
            Dictionary<string, object> command = ReadObjects(completed, "commands").LastOrDefault()
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> targets = ReadObjects(ReadObject(command, "result"), "targets");
            if (includeHistory
                && string.Equals(
                    mode,
                    "individual_chat",
                    StringComparison.OrdinalIgnoreCase))
            {
                foreach (Dictionary<string, object> target in targets)
                {
                    Dictionary<string, object> history = Post("/dialogue/history", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = String(target, "heroId"),
                        ["limit"] = 500
                    });
                    target["priorDialogueLineCount"] = IsOk(history) ? ReadObjects(history, "lines").Count : 500;
                }
            }
            return targets;
        }

        private static List<Dictionary<string, object>>
            QualificationSocialEvents(string campaignId, int limit)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            Dictionary<string, object> started = Post(
                "/tests/live/targets/search",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] =
                        RuntimeInstance(runtime),
                    ["mode"] = "social_event",
                    ["search"] = "",
                    ["limit"] = Math.Max(
                        1, Math.Min(2000, limit))
                });
            if (!IsOk(started))
                throw new InvalidOperationException(
                    "Qualification social-event discovery failed: "
                    + Json.Serialize(started));
            Dictionary<string, object> completed = WaitForRun(
                campaignId, String(started, "runId"), "",
                120, true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Qualification social-event discovery did not complete: "
                    + Json.Serialize(completed));
            Dictionary<string, object> command =
                ReadObjects(completed, "commands")
                    .LastOrDefault()
                ?? new Dictionary<string, object>();
            return ReadObjects(
                ReadObject(command, "result"), "events");
        }

        private static ulong QualificationStableOrder(string value)
        {
            // FNV-1a is sufficient here: this is recorded scenario ordering, not a
            // security boundary. Avoid string.GetHashCode because it is process-randomized.
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (char ch in value ?? "")
            {
                hash ^= (byte)(ch & 0xff);
                hash *= prime;
                hash ^= (byte)(ch >> 8);
                hash *= prime;
            }
            return hash;
        }

        private static List<string> ReadRosterIds(Dictionary<string, object> roster, string key)
        {
            if (roster == null || !roster.TryGetValue(key, out object raw) || raw == null)
                return new List<string>();
            IEnumerable<object> values = raw is System.Collections.ArrayList list
                ? list.Cast<object>()
                : raw is object[] array
                    ? array
                    : Enumerable.Empty<object>();
            return values.Select(Convert.ToString)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<List<string>> ReadRosterPairs(
            Dictionary<string, object> roster,
            string key)
        {
            if (roster == null
                || !roster.TryGetValue(key, out object raw)
                || raw == null)
                return new List<List<string>>();
            IEnumerable<object> rows = raw is ArrayList list
                ? list.Cast<object>()
                : raw is object[] array
                    ? array
                    : Enumerable.Empty<object>();
            return rows.Select(row =>
                {
                    IEnumerable<object> values = row is ArrayList nested
                        ? nested.Cast<object>()
                        : row is object[] nestedArray
                            ? nestedArray
                            : Enumerable.Empty<object>();
                    return values.Select(Convert.ToString)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList();
                })
                .Where(pair => pair.Count == 2)
                .ToList();
        }

        private static List<Dictionary<string, object>> OrderQualificationTargetsByUsage(
            IEnumerable<Dictionary<string, object>> targets, string qualificationId)
        {
            return (targets ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(target => !string.IsNullOrWhiteSpace(String(target, "heroId")))
                .GroupBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    Dictionary<string, object> target = group.First();
                    target["priorConversationEvidenceCount"] =
                        ReadLong(target, "priorDialogueLineCount", 0)
                        + ReadLong(target, "priorMemorySourceCount", 0)
                        + (ReadBoolean(
                                target,
                                "priorGiftRelationshipEvidence")
                            ? 10000
                            : 0);
                    return target;
                })
                // Prefer the least-used eligible heroes so repeated qualification windows
                // do not turn the campaign's oldest test subjects into permanent
                // interrogation targets. Closed group scenes may not appear in the
                // individual dialogue endpoint, so include retrieved memory/summary/event
                // sources in the evidence count. The recorded hash remains the
                // deterministic tie-break.
                .OrderBy(target => ReadLong(target, "priorConversationEvidenceCount", 0))
                .ThenBy(target => QualificationStableOrder((qualificationId ?? "") + "|broad|" + String(target, "heroId")))
                .ToList();
        }

        private static List<Dictionary<string, object>> SelectCourtIntrigueTargets(
            IEnumerable<Dictionary<string, object>> available,
            int limit)
        {
            List<Dictionary<string, object>> all = (available ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(target => !string.IsNullOrWhiteSpace(String(target, "heroId")))
                .GroupBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<Dictionary<string, object>> add = target =>
            {
                if (target == null || result.Count >= limit) return;
                string id = String(target, "heroId");
                if (!string.IsNullOrWhiteSpace(id) && selected.Add(id)) result.Add(target);
            };
            int nobleLimit = Math.Max(16, limit - Math.Max(6, limit / 5));
            Action<Dictionary<string, object>> addNoble = target =>
            {
                if (result.Count < nobleLimit) add(target);
            };

            List<Dictionary<string, object>> nobles = all.Where(target => ReadBoolean(target, "isLord")).ToList();
            foreach (IGrouping<long, Dictionary<string, object>> tier in nobles
                .GroupBy(target => ReadLong(target, "clanTier", 0))
                .OrderBy(group => group.Key))
            {
                foreach (bool female in new[] { true, false })
                {
                    List<Dictionary<string, object>> sex = tier
                        .Where(target => ReadBoolean(target, "isFemale") == female)
                        .OrderBy(target => ReadLong(target, "calculating", 0))
                        .ThenBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    addNoble(sex.FirstOrDefault());
                    addNoble(sex.LastOrDefault());
                }
            }

            foreach (Dictionary<string, object> noble in nobles
                .OrderByDescending(target => Math.Abs(ReadLong(target, "calculating", 0)))
                .ThenBy(target => ReadLong(target, "clanTier", 0))
                .ThenBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase))
                addNoble(noble);

            foreach (Dictionary<string, object> nonNoble in all
                .Where(target => !ReadBoolean(target, "isLord"))
                .OrderByDescending(target => Math.Abs(ReadLong(target, "calculating", 0)))
                .ThenByDescending(target => ReadBoolean(target, "isNotable"))
                .ThenBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase))
                add(nonNoble);

            foreach (Dictionary<string, object> target in all
                .OrderBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase))
                add(target);
            return result;
        }

        private static string IdentityAuthorityTargetKind(
            Dictionary<string, object> target)
        {
            if (ReadBoolean(target, "isRuler"))
                return "ruler";
            if (!string.IsNullOrWhiteSpace(
                    String(
                        target,
                        "governorOfSettlementId")))
                return "governor";
            if (ReadBoolean(target, "isLord"))
                return "noble";
            if (ReadBoolean(target, "isNotable"))
                return "notable";
            if (ReadBoolean(target, "isWanderer"))
                return "wanderer";
            return "commoner";
        }

        private static List<Dictionary<string, object>>
            SelectIdentityAuthorityTargets(
                IEnumerable<Dictionary<string, object>> available,
                string qualificationId,
                int limit)
        {
            List<Dictionary<string, object>> ordered =
                OrderQualificationTargetsByUsage(
                    available,
                    qualificationId)
                .Where(target =>
                    ReadLong(target, "age", 0) >= 18)
                .ToList();
            Dictionary<string,
                Queue<Dictionary<string, object>>> buckets =
                ordered.GroupBy(
                        IdentityAuthorityTargetKind,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => new Queue<
                            Dictionary<string, object>>(
                            group),
                        StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            HashSet<string> selected =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            string[] roleOrder =
            {
                "ruler",
                "governor",
                "noble",
                "notable",
                "wanderer",
                "commoner"
            };
            while (result.Count < limit
                && buckets.Values.Any(queue =>
                    queue.Count > 0))
            {
                foreach (string role in roleOrder)
                {
                    if (result.Count >= limit)
                        break;
                    if (!buckets.TryGetValue(
                            role,
                            out Queue<
                                Dictionary<string, object>>
                                queue)
                        || queue.Count == 0)
                        continue;
                    Dictionary<string, object> target =
                        queue.Dequeue();
                    string id = String(target, "heroId");
                    if (selected.Add(id))
                    {
                        target["identityAuthorityTargetKind"] =
                            role;
                        result.Add(target);
                    }
                }
            }
            foreach (Dictionary<string, object> target
                in ordered)
            {
                if (result.Count >= limit)
                    break;
                string id = String(target, "heroId");
                if (selected.Add(id))
                {
                    target["identityAuthorityTargetKind"] =
                        IdentityAuthorityTargetKind(target);
                    result.Add(target);
                }
            }
            if (result.Count < Math.Min(limit, 25))
                throw new InvalidOperationException(
                    "Identity/authority qualification requires at least "
                    + Math.Min(limit, 25)
                    + " living adult targets; selected "
                    + result.Count + ".");
            return result;
        }

        private static Dictionary<string, object>
            IdentityAuthorityProbe(
                string text,
                Dictionary<string, object> expected = null,
                bool requireExactIntroduction = false)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(1, true);
            assertions[
                "requiresPoliticalAuthorityEvidence"] = true;
            if (requireExactIntroduction)
            {
                assertions["requiresIdentityRecognition"] =
                    true;
                assertions["expectedIdentityName"] =
                    String(expected, "playerName");
                assertions["expectedIdentityState"] =
                    "verified";
                assertions["expectedIdentitySource"] =
                    "exact_self_introduction";
            }
            if (expected != null)
            {
                foreach (string key in new[]
                {
                    "expectedRealmSovereignKnown",
                    "expectedCurrentSettlementOwnerKnown",
                    "expectedAuthorityRelationship",
                    "expectedRecognizedRoles",
                    "expectedCurrentEnemyKingdomCount"
                })
                {
                    if (expected.ContainsKey(key))
                        assertions[key] = expected[key];
                }
            }
            return SendStep(
                "individual_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "identity_role_authority",
                    "detailed_factual_accuracy",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            RunIdentityAuthorityQualification(
                string campaignId,
                string qualificationId,
                Dictionary<string, object> nativeRuntime,
                List<Dictionary<string, object>> targets,
                Dictionary<string, object> plannedCoverage)
        {
            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            string playerId = String(
                nativeRuntime,
                "playerHeroId");
            string playerName = String(
                nativeRuntime,
                "playerName");
            string playerClanId = String(
                nativeRuntime,
                "playerClanId");
            string playerKingdomId = String(
                nativeRuntime,
                "playerKingdomId");
            bool playerIsRuler = ReadBoolean(
                nativeRuntime,
                "playerIsRuler");
            bool playerIsLord = ReadBoolean(
                nativeRuntime,
                "playerIsLord");
            bool playerIsFemale = ReadBoolean(
                nativeRuntime,
                "playerIsFemale");
            string playerGovernorSettlement = String(
                nativeRuntime,
                "playerGovernorOfSettlementId");
            List<string> expectedPublicRoles =
                new List<string>();
            if (playerIsRuler)
                expectedPublicRoles.Add(
                    "realm_sovereign");
            if (playerIsLord)
                expectedPublicRoles.Add(
                    playerIsFemale ? "lady" : "lord");
            if (!string.IsNullOrWhiteSpace(
                    playerGovernorSettlement))
                expectedPublicRoles.Add(
                    "current_settlement_governor");

            for (int index = 0;
                index < targets.Count;
                index++)
            {
                Dictionary<string, object> target =
                    targets[index];
                string targetId = String(
                    target,
                    "heroId");
                Dictionary<string, object> reset = Post(
                    "/identity/reset",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["observerHeroStringId"] =
                            targetId,
                        ["subjectHeroStringId"] =
                            playerId,
                        ["testMode"] = true
                    });
                if (!IsOk(reset))
                    failures.Add(reset);
                bool sameClan =
                    !string.IsNullOrWhiteSpace(
                        playerClanId)
                    && playerClanId.Equals(
                        String(target, "clanId"),
                        StringComparison.OrdinalIgnoreCase);
                bool sameKingdom =
                    !string.IsNullOrWhiteSpace(
                        playerKingdomId)
                    && playerKingdomId.Equals(
                        String(target, "kingdomId"),
                        StringComparison.OrdinalIgnoreCase);
                bool ownSovereign =
                    playerIsRuler && sameKingdom;
                string relationship = ownSovereign
                    ? "subject_is_observer_sovereign"
                    : sameClan
                        ? "same_clan"
                        : sameKingdom
                            ? "same_kingdom_peer_or_subject"
                            : "known_foreign_or_independent_person";
                Dictionary<string, object> expected =
                    new Dictionary<string, object>
                    {
                        ["playerName"] = playerName,
                        ["expectedRealmSovereignKnown"] =
                            playerIsRuler,
                        ["expectedAuthorityRelationship"] =
                            relationship,
                        ["expectedRecognizedRoles"] =
                            expectedPublicRoles.ToArray()
                    };
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>
                    {
                        OpenStep(
                            "individual_chat",
                            new[] { targetId }),
                        IdentityAuthorityProbe(
                            "Good day. I have not given you my name yet. Before I do, how would you address me from what you honestly know or can see?"),
                        IdentityAuthorityProbe(
                            "I am " + playerName
                            + ". You may take that as my formal introduction. How would you greet me now, and which of my public offices matter to you here?",
                            expected,
                            true),
                        IdentityAuthorityProbe(
                            "If I asked you to act on my authority in this place, what standing would I truly have over you, and where would my claim end?",
                            expected),
                    };
                // The focused individual matrix contributes 19 replies and
                // the party/social/wilderness parity matrix contributes 21.
                // Together they prove the gate with exactly 40 samples.
                if (index == 0)
                {
                    steps.Add(IdentityAuthorityProbe(
                        "Before I leave, help me avoid an insult born of ignorance: who rules this realm, who holds this settlement, who governs it day to day, and what is your own place in that order?",
                        expected));
                }
                steps.Add(CloseStep("individual_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[
                        index
                            % QualificationSeeds.Length],
                    "individual_chat",
                    "Identity/role/authority matrix "
                        + index
                        + " observer "
                        + targetId
                        + " role "
                        + String(
                            target,
                            "identityAuthorityTargetKind"),
                    steps,
                    failures);
                completedRuns++;
            }
            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] =
                completedRuns;
            focused["controllerFailureCount"] =
                failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] =
                qualificationId;
            focused["qualificationFocus"] =
                "identity_role_authority";
            focused["plannedCoverage"] =
                plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["targetCount"] = targets.Count,
                    ["replies"] = targets.Count * 3
                        + (targets.Count > 0 ? 1 : 0),
                    ["roleCounts"] =
                        targets.GroupBy(
                                IdentityAuthorityTargetKind,
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                group => group.Key,
                                group => group.Count(),
                                StringComparer.OrdinalIgnoreCase)
                };
            return focused;
        }

        private static Dictionary<string, object>
            RunIdentityAuthorityModeQualification(
                string campaignId,
                string qualificationId,
                Dictionary<string, object> nativeRuntime,
                List<string> partyHeroIds,
                List<Dictionary<string, object>> individualTargets,
                Dictionary<string, object> plannedCoverage)
        {
            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            string playerId = String(nativeRuntime, "playerHeroId");
            string playerName = String(nativeRuntime, "playerName");
            bool playerIsRuler = ReadBoolean(
                nativeRuntime, "playerIsRuler");
            bool playerIsLord = ReadBoolean(
                nativeRuntime, "playerIsLord");
            bool playerIsFemale = ReadBoolean(
                nativeRuntime, "playerIsFemale");
            string playerGovernorSettlement = String(
                nativeRuntime, "playerGovernorOfSettlementId");
            List<string> expectedPublicRoles =
                new List<string>();
            if (playerIsRuler)
                expectedPublicRoles.Add("realm_sovereign");
            if (playerIsLord)
                expectedPublicRoles.Add(
                    playerIsFemale ? "lady" : "lord");
            if (!string.IsNullOrWhiteSpace(
                    playerGovernorSettlement))
                expectedPublicRoles.Add(
                    "current_settlement_governor");
            Dictionary<string, object> expected =
                new Dictionary<string, object>
                {
                    ["playerName"] = playerName,
                    ["expectedRealmSovereignKnown"] =
                        playerIsRuler,
                    ["expectedRecognizedRoles"] =
                        expectedPublicRoles.ToArray()
                };
            if (string.IsNullOrWhiteSpace(
                    String(nativeRuntime, "playerKingdomId")))
                expected["expectedCurrentEnemyKingdomCount"] = 0;

            List<string> partyGroup = partyHeroIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();
            if (partyGroup.Count < 2)
                throw new InvalidOperationException(
                    "Cross-mode identity/authority qualification requires "
                    + "two party-chat-eligible adult heroes.");
            Dictionary<string, object> socialEvent =
                QualificationSocialEvents(campaignId, 100)
                    .Where(row =>
                        !ReadBoolean(row, "wilderness")
                        && ReadBoolean(row, "isOpen")
                        && QualificationReadStrings(
                                row, "attendeeHeroIds")
                            .Count >= 2)
                    .OrderByDescending(row =>
                        QualificationReadStrings(
                                row, "attendeeHeroIds")
                            .Count)
                    .ThenBy(row => String(row, "eventId"),
                        StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (socialEvent == null)
                throw new InvalidOperationException(
                    "Cross-mode identity/authority qualification requires "
                    + "an existing currently-open scheduled social event.");
            string socialEventId =
                String(socialEvent, "eventId");
            List<string> socialGroup =
                QualificationReadStrings(
                        socialEvent, "attendeeHeroIds")
                    .Where(id =>
                        !string.IsNullOrWhiteSpace(id))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
            List<Dictionary<string, object>> socialTargets =
                individualTargets.Where(target =>
                        socialGroup.Contains(
                            String(target, "heroId"),
                            StringComparer.OrdinalIgnoreCase))
                    .ToList();

            ResetIdentityObserversForQualification(
                campaignId, playerId, partyGroup, failures);
            RunQualificationScenario(
                campaignId,
                qualificationId,
                QualificationSeeds[0],
                "party_chat",
                "Identity role authority cross-mode party matrix v3 "
                    + string.Join("+", partyGroup),
                IdentityAuthorityModeSteps(
                    "party_chat",
                    partyGroup,
                    playerName,
                    expected,
                    false,
                    "",
                    partyGroup.Count),
                failures);
            completedRuns++;

            ResetIdentityObserversForQualification(
                campaignId, playerId, socialGroup, failures);
            RunQualificationScenario(
                campaignId,
                qualificationId,
                QualificationSeeds[1],
                "social_event",
                "Identity role authority cross-mode social matrix v3 "
                    + string.Join("+", socialGroup),
                IdentityAuthorityModeSteps(
                    "social_event",
                    socialGroup,
                    playerName,
                    expected,
                    false,
                    socialEventId,
                    socialGroup.Count),
                failures);
            completedRuns++;

            List<string> wildernessGroup =
                partyGroup.Take(2).ToList();
            bool alreadyOnOpenMap = ReadBoolean(
                ReadObject(nativeRuntime, "location"),
                "onOpenMap");
            bool preparedWilderness = false;
            if (!alreadyOnOpenMap)
            {
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[2],
                    "wilderness_event",
                    "Identity role authority cross-mode prepare wilderness v3",
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] =
                                "prepare_wilderness",
                            ["mode"] =
                                "wilderness_event",
                            ["targetSearches"] =
                                partyGroup.ToArray(),
                            ["timeoutSeconds"] = 120
                        }
                    },
                    failures);
                completedRuns++;
                preparedWilderness = true;
            }
            try
            {
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[2],
                    "wilderness_event",
                    "Identity role authority cross-mode wilderness matrix v3",
                    IdentityAuthorityModeSteps(
                        "wilderness_event",
                        wildernessGroup,
                        playerName,
                        expected,
                        true,
                        "",
                        2),
                    failures);
                completedRuns++;
            }
            finally
            {
                if (preparedWilderness)
                {
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[2],
                        "wilderness_event",
                        "Identity role authority cross-mode restore wilderness v3",
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["schemaVersion"] = 2,
                                ["operation"] =
                                    "restore_settlement",
                                ["mode"] =
                                    "wilderness_event",
                                ["timeoutSeconds"] = 120
                            }
                        },
                        failures);
                    completedRuns++;
                }
            }

            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] = completedRuns;
            focused["controllerFailureCount"] = failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] = qualificationId;
            focused["qualificationFocus"] =
                "identity_role_authority_modes";
            focused["plannedCoverage"] = plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["modes"] = new[]
                    {
                        "party_chat",
                        "social_event",
                        "wilderness_event"
                    },
                    ["partyTargets"] = partyGroup.ToArray(),
                    ["socialTargets"] = socialGroup.ToArray(),
                    ["wildernessTargets"] =
                        wildernessGroup.ToArray(),
                    ["minimumReplies"] =
                        partyGroup.Count * 3
                        + socialGroup.Count * 3
                        + 2 * 3,
                    ["roleCounts"] = socialTargets
                        .GroupBy(
                            IdentityAuthorityTargetKind,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Count(),
                            StringComparer.OrdinalIgnoreCase)
                };
            return focused;
        }

        private static List<Dictionary<string, object>>
            IdentityAuthorityModeSteps(
                string mode,
                List<string> observers,
                string playerName,
                Dictionary<string, object> expected,
                bool wilderness,
                string existingEventId,
                int minimumReplies)
        {
            return new List<Dictionary<string, object>>
            {
                !string.IsNullOrWhiteSpace(existingEventId)
                    ? OpenExistingSocialEventStep(
                        existingEventId, observers)
                    : OpenStep(
                        mode,
                        observers,
                        wilderness
                            ? Math.Max(2, minimumReplies)
                            : 1),
                IdentityAuthorityModeProbe(
                    mode,
                    observers,
                    "Good day. I have not yet offered my name. Before I do, "
                    + "how would each of you address me from what you "
                    + "honestly know or can see? If another person recognizes "
                    + "me, do not pretend their private reason is your own.",
                    null,
                    false,
                    minimumReplies),
                IdentityAuthorityModeProbe(
                    mode,
                    observers,
                    "I am " + playerName
                    + ". You may take that as my formal introduction to "
                    + "everyone present. How does knowing my name change the "
                    + "way each of you would address me, if at all?",
                    expected,
                    true,
                    minimumReplies),
                IdentityAuthorityModeProbe(
                    mode,
                    observers,
                    wilderness
                        ? "If I gave an order on this road, which of you would "
                            + "owe me obedience, which would merely hear a "
                            + "request, and what nearby danger or allegiance "
                            + "would shape your answer?"
                        : "If I gave an order in this town, which of you would "
                            + "owe me obedience, which would merely hear a "
                            + "request, and how do the ruler, holder, and "
                            + "governor of this place limit the answer?",
                    expected,
                    false,
                    minimumReplies),
                CloseStep(mode)
            };
        }

        private static Dictionary<string, object>
            IdentityAuthorityModeProbe(
                string mode,
                List<string> observers,
                string text,
                Dictionary<string, object> expected,
                bool requireExactIntroduction,
                int minimumReplies)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(
                    Math.Max(1, minimumReplies), true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            // This focused gate proves identity and political authority parity
            // across production interaction modes. Group-awareness,
            // personality, and NPC-to-NPC relationship receipts have their own
            // qualification gates and must not discard otherwise valid
            // identity evidence.
            assertions["requiresRelationshipReceipt"] = false;
            assertions.Remove("relationshipPolicy");
            if (requireExactIntroduction)
            {
                assertions["requiresIdentityRecognition"] = true;
                assertions["expectedIdentityName"] =
                    String(expected, "playerName");
                assertions["expectedIdentityState"] = "verified";
                assertions["expectedIdentitySource"] =
                    "exact_self_introduction";
            }
            if (expected != null)
            {
                foreach (string key in new[]
                {
                    "expectedRealmSovereignKnown",
                    "expectedRecognizedRoles",
                    "expectedCurrentEnemyKingdomCount"
                })
                {
                    if (expected.ContainsKey(key))
                        assertions[key] = expected[key];
                }
            }
            return SendStep(
                mode,
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "identity_role_authority",
                    "detailed_factual_accuracy",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            OpenExistingSocialEventStep(
                string eventId,
                IEnumerable<string> targets)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "open",
                ["mode"] = "social_event",
                ["eventId"] = eventId,
                ["targetSearches"] =
                    (targets
                        ?? Enumerable.Empty<string>())
                        .ToArray(),
                ["createTestEvent"] = false,
                ["timeoutSeconds"] = 300
            };
        }

        private static void ResetIdentityObserversForQualification(
            string campaignId,
            string playerId,
            IEnumerable<string> observerIds,
            List<Dictionary<string, object>> failures)
        {
            foreach (string observerId in observerIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Dictionary<string, object> reset = Post(
                    "/identity/reset",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["observerHeroStringId"] = observerId,
                        ["subjectHeroStringId"] = playerId,
                        ["testMode"] = true
                    });
                if (!IsOk(reset))
                {
                    failures.Add(reset);
                    throw new InvalidOperationException(
                        "Could not reset identity evidence for observer "
                        + observerId + ": " + Json.Serialize(reset));
                }
            }
        }

        private static List<Dictionary<string, object>>
            BuildClanTierRecognitionQualificationCases(
                List<Dictionary<string, object>> nativeTargets,
                string qualificationId)
        {
            List<Dictionary<string, object>> ordered =
                OrderQualificationTargetsByUsage(
                    nativeTargets, qualificationId)
                    .Where(target =>
                        ReadLong(target, "age", 0) >= 18
                        && ReadLong(target, "clanTier", 0) >= 0
                        && ReadLong(target, "clanTier", 0) <= 6)
                    .ToList();
            List<Dictionary<string, object>> tierZeroNotables =
                ordered.Where(target =>
                    !ReadBoolean(target, "isLord")
                    && ReadBoolean(target, "isNotable")
                    && ReadLong(target, "clanTier", -1) == 0)
                    .ToList();
            List<Dictionary<string, object>> tierZeroWanderers =
                ordered.Where(target =>
                    !ReadBoolean(target, "isLord")
                    && ReadBoolean(target, "isWanderer")
                    && ReadLong(target, "clanTier", -1) == 0)
                    .ToList();
            Dictionary<int, List<Dictionary<string, object>>> byTier =
                Enumerable.Range(1, 6).ToDictionary(
                    tier => tier,
                    tier => ordered.Where(target =>
                        ReadBoolean(target, "isLord")
                        && ReadLong(target, "clanTier", -1)
                            == tier).ToList());
            List<int> missingTiers = byTier
                .Where(pair => pair.Value.Count == 0)
                .Select(pair => pair.Key).ToList();
            if (missingTiers.Count > 0)
                throw new InvalidOperationException(
                    "Clan-tier qualification requires at least one living "
                    + "adult noble at every native clan tier one through six. "
                    + "Missing tiers=" + string.Join(",", missingTiers)
                    + "; available adult nobles=" + ordered.Count + ".");
            if (tierZeroNotables.Count == 0
                || tierZeroWanderers.Count == 0)
                throw new InvalidOperationException(
                    "Clan-tier qualification requires at least one living "
                    + "adult tier-zero notable and one living adult "
                    + "tier-zero wanderer. Available tier-zero notables="
                    + tierZeroNotables.Count
                    + "; wanderers=" + tierZeroWanderers.Count + ".");

            List<Dictionary<string, object>> specifications =
                new List<Dictionary<string, object>>();
            specifications.Add(
                new Dictionary<string, object>
                {
                    ["observerTier"] = 0,
                    ["playerTier"] = 0,
                    ["matrixBand"] = "peer",
                    ["targetKind"] = "notable"
                });
            specifications.Add(
                new Dictionary<string, object>
                {
                    ["observerTier"] = 0,
                    ["playerTier"] = 0,
                    ["matrixBand"] = "peer",
                    ["targetKind"] = "wanderer"
                });
            for (int tier = 1; tier <= 6; tier++)
            {
                for (int repeat = 0; repeat < 2; repeat++)
                    specifications.Add(
                        new Dictionary<string, object>
                        {
                            ["observerTier"] = tier,
                            ["playerTier"] = tier,
                            ["matrixBand"] = "peer",
                            ["targetKind"] = "noble"
                        });
            }
            for (int observerTier = 1;
                observerTier <= 5;
                observerTier++)
            {
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier - 1,
                        ["matrixBand"] = "lower"
                    });
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier + 1,
                        ["matrixBand"] = "higher"
                    });
            }
            foreach (int observerTier in new[] { 2, 3, 6 })
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier - 2,
                        ["matrixBand"] = "substantially_lower"
                    });
            foreach (int observerTier in new[] { 1, 3, 4 })
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier + 2,
                        ["matrixBand"] = "substantially_higher"
                    });

            Dictionary<string, int> uses =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            for (int ordinal = 0;
                ordinal < specifications.Count;
                ordinal++)
            {
                Dictionary<string, object> specification =
                    specifications[ordinal];
                int observerTier = (int)ReadLong(
                    specification, "observerTier", 0);
                int playerTier = (int)ReadLong(
                    specification, "playerTier", 0);
                string targetKind = String(
                    specification, "targetKind");
                List<Dictionary<string, object>> candidates =
                    observerTier == 0
                        ? targetKind.Equals(
                                "wanderer",
                                StringComparison.OrdinalIgnoreCase)
                            ? tierZeroWanderers
                            : tierZeroNotables
                        : byTier[observerTier];
                Dictionary<string, object> chosen =
                    candidates
                        .OrderBy(target =>
                            uses.TryGetValue(
                                String(target, "heroId"),
                                out int used)
                                ? used : 0)
                        .ThenBy(target =>
                            QualificationStableOrder(
                                (qualificationId ?? "")
                                + "|clan-tier|"
                                + ordinal + "|"
                                + String(target, "heroId")))
                        .First();
                string chosenId = String(chosen, "heroId");
                uses[chosenId] = uses.TryGetValue(
                    chosenId, out int prior)
                    ? prior + 1 : 1;
                Dictionary<string, object> row =
                    new Dictionary<string, object>(
                        chosen,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["caseOrdinal"] = ordinal,
                        ["observerClanTier"] = observerTier,
                        ["playerClanTier"] = playerTier,
                        ["expectedRelativeClanTierBand"] =
                            QualificationRelativeClanTierBand(
                                playerTier - observerTier),
                        ["matrixBand"] = String(
                            specification, "matrixBand"),
                        ["targetKind"] = observerTier == 0
                            ? targetKind
                            : "noble",
                        // Keep wallet state private and constant so rank—not
                        // visible wealth—drives this focused recognition gate.
                        ["playerGold"] = 100,
                        ["wealthEvidence"] = "hidden"
                    };
                result.Add(row);
            }
            if (result.Count != 30
                || result.Where(row =>
                        String(row, "matrixBand").Equals(
                            "peer",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(row => ReadLong(
                        row, "observerClanTier", -1))
                    .Distinct().Count() != 7
                || result.Select(row => ReadLong(
                        row, "observerClanTier", -1))
                    .Distinct().Count() != 7
                || result.Select(row => ReadLong(
                        row, "playerClanTier", -1))
                    .Distinct().Count() != 7
                || result.Count(row =>
                        ReadLong(row,
                            "observerClanTier", -1) == 0
                        && String(row, "targetKind").Equals(
                            "notable",
                            StringComparison.OrdinalIgnoreCase))
                    != 1
                || result.Count(row =>
                        ReadLong(row,
                            "observerClanTier", -1) == 0
                        && String(row, "targetKind").Equals(
                            "wanderer",
                            StringComparison.OrdinalIgnoreCase))
                    != 1
                || result.Select(row => String(
                        row, "expectedRelativeClanTierBand"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != 5)
            {
                throw new InvalidOperationException(
                    "The 30-probe clan-tier matrix must cover equal-rank "
                    + "cases for a tier-zero notable, a tier-zero wanderer, "
                    + "and nobles at every native tier one through six; "
                    + "player tiers zero through six; and all five "
                    + "relative-rank bands.");
            }
            return result;
        }

        private static Dictionary<string, object>
            RunClanTierRecognitionQualification(
                string campaignId,
                string qualificationId,
                List<Dictionary<string, object>> cases,
                Dictionary<string, object> plannedCoverage)
        {
            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            try
            {
                for (int probe = 0; probe < cases.Count; probe++)
                {
                    Dictionary<string, object> qualificationCase =
                        cases[probe];
                    string targetId = String(
                        qualificationCase, "heroId");
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[
                            probe % QualificationSeeds.Length],
                        "individual_chat",
                        "Clan-tier recognition qualification "
                            + probe
                            + " target " + targetId
                            + " observer-tier "
                            + ReadLong(qualificationCase,
                                "observerClanTier", 0)
                            + " player-tier "
                            + ReadLong(qualificationCase,
                                "playerClanTier", 0)
                            + " band "
                            + String(qualificationCase,
                                "expectedRelativeClanTierBand"),
                        new List<Dictionary<string, object>>
                        {
                            ManipulationFixtureStep(
                                qualificationCase),
                            OpenStep(
                                "individual_chat",
                                new[] { targetId }),
                            ClanTierRecognitionProbe(
                                probe,
                                qualificationCase),
                            CloseStep("individual_chat")
                        },
                        failures);
                    completedRuns++;
                }
            }
            finally
            {
                try
                {
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[0],
                        "individual_chat",
                        "Restore clan-tier recognition fixture",
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["schemaVersion"] = 2,
                                ["operation"] =
                                    "restore_manipulation_fixture",
                                ["mode"] = "individual_chat",
                                ["timeoutSeconds"] = 120
                            }
                        },
                        failures);
                    completedRuns++;
                }
                catch (Exception ex)
                {
                    failures.Add(
                        new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] =
                                "Native clan-tier fixture restoration failed: "
                                + ex.Message
                        });
                }
                Dictionary<string, object> identityRestore = Post(
                    "/tests/live/fixtures/manipulation",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["operation"] = "restore"
                    });
                if (!IsOk(identityRestore))
                    failures.Add(identityRestore);
            }
            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] = completedRuns;
            focused["controllerFailureCount"] = failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] = qualificationId;
            focused["qualificationFocus"] =
                "clan_tier_recognition";
            focused["plannedCoverage"] = plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["totalReplies"] = 30,
                    ["peerReplies"] = 14,
                    ["equalTierReplies"] = 14,
                    ["equalTierClanLevels"] =
                        new[] { 0, 1, 2, 3, 4, 5, 6 },
                    ["tierZeroNotableReplies"] = 1,
                    ["tierZeroWandererReplies"] = 1,
                    ["lowerReplies"] = 5,
                    ["higherReplies"] = 5,
                    ["substantiallyLowerReplies"] = 3,
                    ["substantiallyHigherReplies"] = 3,
                    ["observerClanTiers"] = 7,
                    ["playerClanTiers"] = 7
                };
            return focused;
        }

        private static List<Dictionary<string, object>>
            BuildManipulationQualificationCases(
                List<Dictionary<string, object>> profiles)
        {
            List<Dictionary<string, object>> candidates =
                (profiles ?? new List<Dictionary<string, object>>())
                    .Where(row =>
                        ReadBoolean(row, "courtCharacterAvailable")
                        && ReadBoolean(row, "supportsManipulation")
                        && ReadLong(row, "honorLevel", 0) < 0
                        && ReadLong(row, "age", 0) >= 18
                        && ReadLong(row, "clanTier", 0) >= 1
                        && ReadLong(row, "clanTier", 0) <= 6)
                    .ToList();
            Dictionary<int, List<Dictionary<string, object>>>
                candidatesByTier = Enumerable.Range(1, 6)
                    .ToDictionary(
                        tier => tier,
                        tier => candidates.Where(row =>
                            ReadLong(row, "clanTier", -1)
                                == tier).ToList());
            List<int> missingTiers = candidatesByTier
                .Where(pair => pair.Value.Count == 0)
                .Select(pair => pair.Key).ToList();
            if (missingTiers.Count > 0)
                throw new InvalidOperationException(
                    "Manipulation qualification requires at least one "
                    + "living adult low-honor Court Character at every "
                    + "native noble clan tier from one through six. Missing tiers="
                    + string.Join(",", missingTiers)
                    + "; available low-honor Court Characters="
                    + candidates.Count + ".");

            List<Dictionary<string, object>> matrix =
                new List<Dictionary<string, object>>();
            for (int tier = 1; tier <= 6; tier++)
            {
                foreach (bool wealthy in new[] { false, true })
                    matrix.Add(new Dictionary<string, object>
                    {
                        ["relativeTier"] = "equal",
                        ["observerTier"] = tier,
                        ["wealthy"] = wealthy
                    });
            }
            for (int index = 0; index < 7; index++)
            {
                int tier = 1 + index % 6;
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "lower",
                    ["observerTier"] = tier,
                    ["wealthy"] = false
                });
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "lower",
                    ["observerTier"] = tier,
                    ["wealthy"] = true
                });
            }
            for (int index = 0; index < 7; index++)
            {
                int tier = 1 + index % 5;
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "higher",
                    ["observerTier"] = tier,
                    ["wealthy"] = false
                });
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "higher",
                    ["observerTier"] = tier,
                    ["wealthy"] = true
                });
            }

            Dictionary<string, int> useCounts =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<int, Dictionary<string, object>>
                equalTargetByTier =
                    new Dictionary<int,
                        Dictionary<string, object>>();
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            int wealthyOrdinal = 0;
            for (int caseOrdinal = 0;
                caseOrdinal < matrix.Count;
                caseOrdinal++)
            {
                Dictionary<string, object> specification =
                    matrix[caseOrdinal];
                string relativeTier =
                    String(specification, "relativeTier");
                int observerTier = (int)ReadLong(
                    specification, "observerTier", 0);
                bool wealthy = ReadBoolean(
                    specification, "wealthy");
                int requestedBoldness =
                    -2 + caseOrdinal % 5;
                bool requestedFemale =
                    caseOrdinal % 2 == 0;
                Dictionary<string, object> chosen;
                if (relativeTier == "equal"
                    && equalTargetByTier.TryGetValue(
                        observerTier, out chosen))
                {
                    // Use the same Court Character for poor/wealthy peer
                    // cases at each tier, isolating visible wealth while
                    // keeping personality and relative rank constant.
                }
                else
                {
                    chosen = candidatesByTier[observerTier]
                        .OrderBy(row =>
                            useCounts.TryGetValue(
                                String(row, "heroId"),
                                out int used)
                                ? used : 0)
                        .ThenBy(row => Math.Abs(
                            ReadLong(
                                row, "boldnessLevel", 0)
                            - requestedBoldness))
                        .ThenBy(row =>
                            ReadBoolean(row, "isFemale")
                                == requestedFemale ? 0 : 1)
                        .ThenBy(row => ReadLong(
                            row, "priorDialogueLineCount", 500))
                        .ThenBy(row => ReadLong(
                            row, "honorLevel", 0))
                        .ThenByDescending(row => ReadLong(
                            row, "influencePotential", 0))
                        .ThenBy(row => String(
                            row, "heroId"),
                            StringComparer.OrdinalIgnoreCase)
                        .First();
                    if (relativeTier == "equal")
                        equalTargetByTier[observerTier] =
                            chosen;
                }
                string chosenId = String(chosen, "heroId");
                useCounts[chosenId] =
                    useCounts.TryGetValue(chosenId, out int prior)
                        ? prior + 1 : 1;
                int playerTier = relativeTier == "equal"
                    ? observerTier
                    : relativeTier == "higher"
                        ? Math.Min(6, observerTier + 2)
                        : Math.Max(0, observerTier - 2);
                int delta = playerTier - observerTier;
                string quadrant = relativeTier
                    + "_tier_"
                    + (wealthy ? "wealthy" : "poor");
                bool hiddenWealthControl = wealthy
                    && wealthyOrdinal++ % 5 == 0;
                Dictionary<string, object> row =
                    new Dictionary<string, object>(
                        chosen,
                        StringComparer.OrdinalIgnoreCase)
                {
                    ["quadrant"] = quadrant,
                    ["matrixRound"] =
                        relativeTier == "equal"
                            ? 0
                            : relativeTier == "lower"
                                ? 1 : 2,
                    ["matrixTargetIndex"] = observerTier,
                    ["caseOrdinal"] = caseOrdinal,
                    ["playerClanTier"] = playerTier,
                    ["playerGold"] = wealthy ? 50000000 : 100,
                    ["wealthEvidence"] = hiddenWealthControl
                        ? "hidden" : "demonstrated",
                    ["expectedWealthEvidenceBasis"] =
                        hiddenWealthControl
                            ? "hidden_wallet_not_observable"
                            : "demonstrated_financial_capacity",
                    ["hiddenWealthControl"] =
                        hiddenWealthControl,
                    ["expectedRelativeClanTierBand"] =
                        QualificationRelativeClanTierBand(delta)
                };
                result.Add(row);
            }
            if (result.Count != 40
                || new Dictionary<string, int>
                {
                    ["equal_tier_poor"] = 6,
                    ["equal_tier_wealthy"] = 6,
                    ["lower_tier_poor"] = 7,
                    ["lower_tier_wealthy"] = 7,
                    ["higher_tier_poor"] = 7,
                    ["higher_tier_wealthy"] = 7
                }.Any(required =>
                    result.Count(row => String(
                        row, "quadrant").Equals(
                            required.Key,
                            StringComparison.OrdinalIgnoreCase))
                        != required.Value)
                || result.Where(row => String(
                        row, "quadrant").StartsWith(
                            "equal_", StringComparison.Ordinal))
                    .Select(row => ReadLong(
                        row, "matrixTargetIndex", -1))
                    .Distinct().Count() != 6
                || result.Select(row => String(row, "heroId"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() < 6
                || result.Count(row =>
                    ReadBoolean(row, "hiddenWealthControl")) != 4)
                throw new InvalidOperationException(
                    "The counterbalanced manipulation matrix did not "
                    + "produce all six tier/wealth cells, peer cases at "
                    + "every noble clan tier one through six, four hidden-wallet "
                    + "controls, and at least six authentic low-honor "
                    + "Court Character targets.");
            return result;
        }

        private static string QualificationRelativeClanTierBand(int delta)
        {
            if (delta <= -2) return "substantially_lower";
            if (delta == -1) return "lower";
            if (delta == 0) return "peer";
            if (delta == 1) return "higher";
            return "substantially_higher";
        }

        private static Dictionary<string, object>
            ManipulationFixtureStep(
                Dictionary<string, object> manipulationCase)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "prepare_manipulation_fixture",
                ["mode"] = "individual_chat",
                ["playerClanTier"] = ReadLong(
                    manipulationCase, "playerClanTier", 0),
                ["playerGold"] = ReadLong(
                    manipulationCase, "playerGold", 0),
                ["wealthEvidence"] = String(
                    manipulationCase, "wealthEvidence"),
                ["timeoutSeconds"] = 120
            };
        }

        private static Dictionary<string, object> RunQualificationScenario(
            string campaignId,
            string qualificationId,
            long seed,
            string mode,
            string label,
            List<Dictionary<string, object>> steps,
            List<Dictionary<string, object>> failures)
        {
            if (QualificationRunsToSkip > 0)
            {
                QualificationRunsToSkip--;
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "completed",
                    ["resumedSkip"] = true,
                    ["label"] = label,
                    ["remainingCompletedRunsToSkip"] = QualificationRunsToSkip
                };
            }
            Post("/tests/live/arm", new Dictionary<string, object>
            {
                ["confirmation"] = "arm", ["minutes"] = 720, ["requestedBy"] = "ReignLiveTest qualification renewal"
            });
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            Dictionary<string, object> startPayload = new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["runId"] = QualificationScenarioRunId(
                    qualificationId, mode, label, seed),
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["mode"] = mode,
                ["presentation"] = "headless",
                ["effects"] = "guarded",
                ["label"] = label,
                ["seed"] = seed,
                ["qualificationSeed"] = seed,
                ["qualificationId"] = qualificationId,
                ["autoCompleteWhenIdle"] = true,
                ["steps"] = steps
            };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(
                    campaignId, startPayload);
            if (!IsOk(started))
            {
                failures.Add(started);
                throw new InvalidOperationException(
                    "Qualification scenario did not start after bounded "
                    + "idempotent reconciliation: " + label + "; "
                    + Json.Serialize(started));
            }
            Dictionary<string, object> completed = WaitForRun(
                campaignId, String(started, "runId"), "", mode == "individual_chat" ? 1800 : 3600, true);
            if (!IsOk(completed) || !String(completed, "status").Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(completed);
                return completed;
            }
            string runId = String(started, "runId");
            Dictionary<string, object> report = Get("/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(runId));
            Dictionary<string, object> evaluation =
                EvaluateQualificationRunWithReconciliation(
                    campaignId, runId, report);
            if (!IsOk(evaluation))
                failures.Add(evaluation);
            return Get("/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(runId));
        }

        private static void EnsureQualificationPartyFixture(
            string campaignId,
            string qualificationId,
            List<string> candidateHeroIds)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            string gameInstanceId = RuntimeInstance(runtime);
            string normalizedInstance = new string(
                (gameInstanceId ?? string.Empty)
                    .Where(char.IsLetterOrDigit).ToArray());
            string runSuffix = normalizedInstance.Length <= 12
                ? normalizedInstance
                : normalizedInstance.Substring(
                    normalizedInstance.Length - 12);
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["runId"] = "live-party-fixture-" + runSuffix,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = gameInstanceId,
                    ["mode"] = "party_chat",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Prepare disposable qualification party",
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] = "prepare_party_fixture",
                            ["mode"] = "party_chat",
                            ["desiredCount"] = 5,
                            ["targetSearches"] =
                                candidateHeroIds.ToArray(),
                            ["timeoutSeconds"] = 120
                        }
                    }
                };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(
                    campaignId, payload);
            if (!IsOk(started))
                throw new InvalidOperationException(
                    "Could not start the disposable party fixture: "
                    + Json.Serialize(started));
            Dictionary<string, object> completed = WaitForRun(
                campaignId,
                String(started, "runId"),
                "",
                300,
                true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Could not prepare five native party-chat participants: "
                    + Json.Serialize(completed));
        }

        private static Dictionary<string, object>
            EnsureQualificationAuthorityFixture(
                string campaignId,
                string qualificationId)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            string gameInstanceId = RuntimeInstance(runtime);
            string normalizedInstance = new string(
                (gameInstanceId ?? string.Empty)
                    .Where(char.IsLetterOrDigit).ToArray());
            string runSuffix = normalizedInstance.Length <= 12
                ? normalizedInstance
                : normalizedInstance.Substring(
                    normalizedInstance.Length - 12);
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["runId"] =
                        "live-authority-fixture-" + runSuffix,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = gameInstanceId,
                    ["mode"] = "individual_chat",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] =
                        "Prepare disposable sovereign authority fixture",
                    ["qualificationId"] = qualificationId,
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] =
                                "prepare_authority_fixture",
                            ["mode"] = "individual_chat",
                            ["confirmDisposableCampaign"] = true,
                            ["timeoutSeconds"] = 300
                        }
                    }
                };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(
                    campaignId, payload);
            if (!IsOk(started))
                throw new InvalidOperationException(
                    "Could not start the disposable sovereign authority fixture: "
                    + Json.Serialize(started));
            Dictionary<string, object> completed =
                WaitForRun(
                    campaignId,
                    String(started, "runId"),
                    "",
                    600,
                    true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Could not prepare the native sovereign authority fixture: "
                    + Json.Serialize(completed));
            Dictionary<string, object> command =
                ReadObjects(completed, "commands")
                    .LastOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> result =
                ReadObject(command, "result");
            if (!ReadBoolean(result, "playerIsRuler")
                || !ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "sameClanWitness")
                || !ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "immediateFamilyWitness")
                || ReadLong(
                    ReadObject(result, "nativeInvariant"),
                    "subjectVassalWitnessCount", 0) < 2
                || !ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "playerOwnsSettlement")
                || ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "playerGovernsSettlement"))
                throw new InvalidOperationException(
                    "The authority fixture command completed without proving all required native invariants: "
                    + Json.Serialize(result));
            return result;
        }

        private static string QualificationScenarioRunId(
            string qualificationId,
            string mode,
            string label,
            long seed)
        {
            string identity = string.Join("|", new[]
            {
                qualificationId ?? "",
                QualificationScenarioBuildVersion ?? "",
                mode ?? "",
                label ?? "",
                seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            using (System.Security.Cryptography.SHA256 sha =
                System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(identity));
                string hex = BitConverter.ToString(hash)
                    .Replace("-", "").ToLowerInvariant();
                // Correlation ids repeat the run id and command id inside
                // Windows snapshot filenames. Keep this deterministic id no
                // longer than the former timestamp-based ids so the installed
                // module's deep campaign path remains below MAX_PATH.
                return "live-q-" + hex.Substring(0, 20);
            }
        }

        private static Dictionary<string, object>
            StartQualificationScenarioRunWithReconciliation(
                string campaignId,
                Dictionary<string, object> payload)
        {
            string requestedRunId = String(payload, "runId");
            Dictionary<string, object> last = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "The qualification scenario was not submitted."
            };
            for (int attempt = 1; attempt <= 30; attempt++)
            {
                try
                {
                    Dictionary<string, object> started =
                        Post("/tests/live/run/start", payload);
                    if (IsOk(started))
                    {
                        started["qualificationStartAttempt"] = attempt;
                        return started;
                    }
                    last = started;
                    string error = String(started, "error");
                    bool transient =
                        error.IndexOf("already active",
                            StringComparison.OrdinalIgnoreCase) >= 0
                        || error.IndexOf("fresh loaded-game heartbeat",
                            StringComparison.OrdinalIgnoreCase) >= 0
                        || error.IndexOf("Save Sync alignment",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!transient) break;
                }
                catch (Exception ex)
                {
                    last = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = ex.Message,
                        ["ambiguousTransport"] = true
                    };
                }

                try
                {
                    Dictionary<string, object> durable = Get(
                        "/tests/live/run/report?campaignId="
                        + Uri.EscapeDataString(campaignId)
                        + "&runId=" + Uri.EscapeDataString(requestedRunId));
                    if (IsOk(durable)
                        && ReadBoolean(durable, "found")
                        && String(durable, "runId").Equals(
                            requestedRunId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        durable["qualificationStartReconciled"] = true;
                        durable["qualificationStartAttempt"] = attempt;
                        return durable;
                    }
                }
                catch
                {
                    // The stable requested run id makes another start attempt
                    // safe even when this read is also temporarily unavailable.
                }
                Thread.Sleep(2000);
                Dictionary<string, object> runtime =
                    QualificationRuntimeWithHeartbeatGrace(campaignId);
                payload["gameInstanceId"] = RuntimeInstance(runtime);
            }
            last["requestedRunId"] = requestedRunId;
            last["boundedAttempts"] = 30;
            return last;
        }

        private static Dictionary<string, object>
            EvaluateQualificationRunWithReconciliation(
                string campaignId,
                string runId,
                Dictionary<string, object> completedReport)
        {
            if (QualificationRunEvaluationComplete(completedReport))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["runId"] = runId,
                    ["reconciled"] = true,
                    ["evaluationAlreadyComplete"] = true
                };
            }

            try
            {
                return PostWithTimeout(
                    "/tests/live/readiness/evaluate-run",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["runId"] = runId
                    },
                    ReadinessEvaluationTimeoutSeconds);
            }
            catch (TaskCanceledException)
            {
                // The server may still be completing the idempotent blinded
                // evaluation after the client timeout. Reconcile its durable
                // report; never replay or resubmit the dialogue run.
            }
            catch (System.Net.Http.HttpRequestException)
            {
                // Treat ambiguous transport exactly like a timeout and inspect
                // the correlation-bearing durable report before any retry.
            }

            Dictionary<string, object> latest = completedReport;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                Thread.Sleep(5000);
                latest = Get("/tests/live/run/report?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&runId=" + Uri.EscapeDataString(runId));
                if (QualificationRunEvaluationComplete(latest))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["runId"] = runId,
                        ["reconciled"] = true,
                        ["evaluationCompletedAfterAmbiguousTransport"] = true
                    };
                }
            }

            throw new TimeoutException(
                "Blinded readiness evaluation did not become durable within "
                + ReadinessEvaluationTimeoutSeconds
                + " seconds plus the bounded reconciliation interval. "
                + "The completed dialogue run was not replayed. Last report: "
                + Json.Serialize(latest));
        }

        private static bool QualificationRunEvaluationComplete(
            Dictionary<string, object> report)
        {
            HashSet<string> rubricCategories = new HashSet<string>(
                new[]
                {
                    "personality_consistency", "detailed_factual_accuracy",
                    "world_local_knowledge", "group_awareness",
                    "shared_relationship_history", "clan_tier_recognition",
                    "manipulation_capabilities"
                },
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> required = new HashSet<string>(
                ReadObjects(report, "commands")
                    .Where(command =>
                        String(command, "operation").Equals(
                            "send", StringComparison.OrdinalIgnoreCase)
                        && String(command, "status").Equals(
                            "completed", StringComparison.OrdinalIgnoreCase)
                        && QualificationReadStrings(
                            command, "readinessCategories")
                            .Any(rubricCategories.Contains))
                    .Select(command => String(command, "commandId"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> evaluated = new HashSet<string>(
                ReadObjects(report, "readinessEvaluations")
                    .Select(row => String(row, "commandId"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return required.All(evaluated.Contains);
        }

        private static List<string> QualificationReadStrings(
            Dictionary<string, object> value,
            string key)
        {
            if (value == null
                || !value.TryGetValue(key, out object raw)
                || raw == null)
                return new List<string>();
            if (raw is ArrayList list)
                return list.Cast<object>().Select(Convert.ToString)
                    .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (raw is object[] array)
                return array.Select(Convert.ToString)
                    .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (raw is IEnumerable<string> strings)
                return strings.Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
            return new List<string> { Convert.ToString(raw) }
                .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        private static Dictionary<string, object>
            QualificationRuntimeWithHeartbeatGrace(string campaignId)
        {
            Dictionary<string, object> runtime =
                new Dictionary<string, object>();
            // A backgrounded Bannerlord host can miss several ordinary
            // heartbeat intervals while a large campaign census or Save Sync
            // boundary is being reconciled. Give the still-running native host
            // a bounded five-minute recovery window, while continuing to reject
            // a heartbeat from the wrong campaign immediately.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(5);
            int attempts = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                attempts++;
                runtime = Get("/tests/live/runtime?campaignId="
                    + Uri.EscapeDataString(campaignId));
                Dictionary<string, object> native =
                    ReadObject(runtime, "runtime");
                string observedCampaignId = String(native, "campaignId");
                if (!string.IsNullOrWhiteSpace(observedCampaignId)
                    && !observedCampaignId.Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Qualification rejected a stale campaign heartbeat. "
                        + "Expected " + campaignId + " but observed "
                        + observedCampaignId + ".");
                }
                if (ReadBoolean(runtime, "gameOnline")
                    && !string.IsNullOrWhiteSpace(RuntimeInstance(runtime)))
                    return runtime;
                if (DateTimeOffset.UtcNow < deadline)
                    Thread.Sleep(2000);
            }

            throw new InvalidOperationException(
                "The campaign heartbeat remained offline across "
                + attempts
                + " qualification checks before its bounded five-minute deadline. Last runtime evidence: "
                + Json.Serialize(runtime));
        }

        private static Dictionary<string, object> OpenStep(
            string mode,
            IEnumerable<string> targets,
            int minimumParticipants = 1)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "open",
                ["mode"] = mode,
                ["targetSearches"] = targets.ToArray(),
                ["createTestEvent"] = true,
                ["minimumParticipants"] =
                    Math.Max(1, Math.Min(5, minimumParticipants)),
                ["timeoutSeconds"] = 300
            };
        }

        private static Dictionary<string, object> CloseStep(string mode)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "close",
                ["mode"] = mode,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["requiresSceneMemoryArtifacts"] = true,
                    ["critical"] = true
                },
                ["timeoutSeconds"] = 900
            };
        }

        private static Dictionary<string, object> SaveStep(int index)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "save_checkpoint",
                ["mode"] = "individual_chat",
                ["saveName"] = QualificationSaveName(index),
                ["timeoutSeconds"] = 240
            };
        }

        private static Dictionary<string, object> MemoryCompletionStep()
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "wait_for_memory",
                ["mode"] = "individual_chat",
                ["waitMilliseconds"] = 30000,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["requiresQualificationMemoryCompletion"] = true,
                    ["critical"] = true
                },
                ["timeoutSeconds"] = 180
            };
        }

        private static string QualificationSaveName(int index)
        {
            return index % 2 == 0
                ? "Reign_Conversation_Qualification_A"
                : "Reign_Conversation_Qualification_B";
        }

        private static List<List<string>> SelectQualificationRelationshipHistoryPairs(
            string campaignId,
            List<string> partyHeroIds,
            int count)
        {
            List<Dictionary<string, object>> candidates =
                new List<Dictionary<string, object>>();
            List<string> ids = (partyHeroIds ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int first = 0; first < ids.Count; first++)
            {
                for (int second = first + 1; second < ids.Count; second++)
                {
                    Dictionary<string, object> history = Post(
                        "/relationships/history/query",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["heroStringId"] = ids[first],
                            ["otherHeroStringId"] = ids[second]
                        });
                    candidates.Add(new Dictionary<string, object>
                    {
                        ["first"] = ids[first],
                        ["second"] = ids[second],
                        ["historyCount"] = ReadLong(history, "count", 0)
                    });
                }
            }
            List<List<string>> selected = candidates
                .OrderBy(row => ReadLong(row, "historyCount", 0))
                .ThenBy(row => String(row, "first"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => String(row, "second"), StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, count))
                .Select(row => new List<string>
                {
                    String(row, "first"), String(row, "second")
                }).ToList();
            if (selected.Count < count)
                throw new InvalidOperationException(
                    "Shared Relationship History qualification requires "
                    + count + " distinct party-eligible pairs; only "
                    + selected.Count + " were available.");
            return selected;
        }

        private static void RunSharedRelationshipHistoryQualificationSetup(
            string campaignId,
            string qualificationId,
            List<List<string>> pairs,
            ref int completedRuns,
            List<Dictionary<string, object>> failures)
        {
            int pairOrdinal = 0;
            foreach (List<string> pair in pairs)
            {
                Dictionary<string, object> history = Post(
                    "/relationships/history/query",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = pair[0],
                        ["otherHeroStringId"] = pair[1]
                    });
                bool cold = ReadLong(history, "count", 0) == 0;
                Dictionary<string, object> ensured = Post(
                    "/tests/live/fixtures/relationship-history",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["operation"] = "set",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["heroAId"] = pair[0],
                        ["heroBId"] = pair[1],
                        ["affinityAtoB"] = 1,
                        ["affinityBtoA"] = -1
                    });
                if (!IsOk(ensured))
                    throw new InvalidOperationException(
                        "Could not ensure relationship fixture pair "
                        + string.Join("|", pair) + ": " + Json.Serialize(ensured));
                Dictionary<string, object> stabilized =
                    ReadObject(ensured, "after");
                int stabilizedForward = QualificationDirectionalAffinity(
                    stabilized, pair[0], pair[1]);
                int stabilizedReverse = QualificationDirectionalAffinity(
                    stabilized, pair[1], pair[0]);
                if (stabilizedForward != 1 || stabilizedReverse != -1)
                    throw new InvalidOperationException(
                        "Relationship fixture pair "
                        + string.Join("|", pair)
                        + " was not stabilized at the requested neutral affinities: "
                        + stabilizedForward + "/" + stabilizedReverse + ".");

                RunQualificationScenario(
                    campaignId, qualificationId,
                    QualificationSeeds[pairOrdinal % QualificationSeeds.Length],
                    "party_chat",
                    "Shared Relationship History cold/reuse pair "
                        + pairOrdinal,
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", pair),
                        GroupRelationshipHistoryProbe(
                            pair, cold ? "cold" : "any", false,
                            "cold_initial"),
                        GroupRelationshipHistoryProbe(
                            pair, "reuse", false, "cold_followup"),
                        CloseStep("party_chat")
                    },
                    failures);
                completedRuns++;

                int currentForward = QualificationDirectionalAffinity(
                    stabilized, pair[0], pair[1]);
                int currentReverse = QualificationDirectionalAffinity(
                    stabilized, pair[1], pair[0]);
                int nextForward = currentForward >= 30 ? -34 : 62;
                int nextReverse = currentReverse <= -30 ? 62 : -34;
                Dictionary<string, object> transitionFixture = Post(
                    "/tests/live/fixtures/relationship-history",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["operation"] = "set",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["heroAId"] = pair[0],
                        ["heroBId"] = pair[1],
                        ["affinityAtoB"] = nextForward,
                        ["affinityBtoA"] = nextReverse
                    });
                if (!IsOk(transitionFixture))
                    throw new InvalidOperationException(
                        "Could not apply relationship transition fixture "
                        + string.Join("|", pair) + ": "
                        + Json.Serialize(transitionFixture));

                RunQualificationScenario(
                    campaignId, qualificationId,
                    QualificationSeeds[(pairOrdinal + 1)
                        % QualificationSeeds.Length],
                    "party_chat",
                    "Shared Relationship History transition/reuse pair "
                        + pairOrdinal,
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", pair),
                        GroupRelationshipHistoryProbe(
                            pair, "transition", false,
                            "transition_initial"),
                        GroupRelationshipHistoryProbe(
                            pair, "reuse", false, "transition_followup"),
                        CloseStep("party_chat")
                    },
                    failures);
                completedRuns++;
                pairOrdinal++;
            }
        }

        private static int QualificationDirectionalAffinity(
            Dictionary<string, object> row,
            string observerId,
            string targetId)
        {
            if (row == null || row.Count == 0) return 0;
            bool observerIsStoredA = String(row, "hero_a_id")
                .Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && String(row, "hero_b_id")
                    .Equals(targetId, StringComparison.OrdinalIgnoreCase);
            return (int)ReadLong(row,
                observerIsStoredA ? "affinity_a_to_b" : "affinity_b_to_a", 0);
        }

        private static Dictionary<string, object> GroupRelationshipHistoryProbe(
            List<string> pair,
            string expectedMode,
            bool persistence,
            string promptVariant = "")
        {
            Dictionary<string, object> assertions =
                BaseAssertions(pair.Count, true);
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["requiresSharedRelationshipHistoryEvidence"] = true;
            assertions["minimumSharedRelationshipHistoryTargets"] = 1;
            assertions["expectedSharedRelationshipHistoryMode"] =
                expectedMode;
            if (persistence)
                assertions["requiresSharedRelationshipHistoryPersistence"] =
                    true;
            assertions["requiresPersonalityEvidence"] = true;
            assertions["relationshipPolicy"] =
                RelationshipPolicy(pair, "routine", true);
            string text;
            switch ((promptVariant ?? "").Trim().ToLowerInvariant())
            {
                case "cold_initial":
                    text = "You two weigh each other's words more carefully than strangers would. Where did that begin?";
                    break;
                case "cold_followup":
                    text = "If the same choice faced you tomorrow, would that history make you trust one another—or hesitate?";
                    break;
                case "transition_initial":
                    text = "Something between you has changed since those earlier days. What do you now expect from one another that you did not expect then?";
                    break;
                case "transition_followup":
                    text = "Then what should the two of you do next, given what was just said?";
                    break;
                default:
                    text = persistence
                        ? "Time has passed since we last spoke of this. What choice before you now is still shaped by your shared past?"
                        : "This disagreement plainly has roots older than today. How does your history with one another bear on it?";
                    break;
            }
            return SendStep("party_chat", text,
                new List<string>
                {
                    "structural_pipeline", "shared_relationship_history",
                    "group_awareness", "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static void ReloadQualificationCheckpoint(
            string campaignId,
            string qualificationId,
            int checkpointIndex,
            string warmHeroId,
            List<List<string>> relationshipHistoryPairs,
            List<Dictionary<string, object>> failures)
        {
            if (QualificationReloadsToSkip > 0)
            {
                QualificationReloadsToSkip--;
                if (QualificationRunsToSkip <= 0)
                    throw new InvalidOperationException(
                        "Qualification resume evidence contains a completed "
                        + "checkpoint reload without its completed persistence run.");
                QualificationRunsToSkip--;
                return;
            }
            string saveName = QualificationSaveName(checkpointIndex);
            string[] lifecycleArgs =
            {
                "game", "--campaign", campaignId, "--wait", "300", "--json"
            };
            Dictionary<string, object> stopped = StopGame(lifecycleArgs);
            Dictionary<string, object> started = IsOk(stopped)
                ? StartGame(lifecycleArgs, saveName, "checkpoint_reloaded")
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Checkpoint shutdown failed." };
            Dictionary<string, object> runtime = IsOk(started)
                ? Get("/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId))
                : new Dictionary<string, object>();
            Dictionary<string, object> warmup = IsOk(started)
                ? WarmQualificationMemory(campaignId, warmHeroId, qualificationId, failures)
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Game restart failed." };
            List<string> persistencePair = relationshipHistoryPairs[
                checkpointIndex % relationshipHistoryPairs.Count];
            Dictionary<string, object> persistence = IsOk(warmup)
                ? RunQualificationScenario(
                    campaignId, qualificationId,
                    QualificationSeeds[
                        checkpointIndex % QualificationSeeds.Length],
                    "party_chat",
                    "Shared Relationship History reload persistence "
                        + checkpointIndex,
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", persistencePair),
                        GroupRelationshipHistoryProbe(
                            persistencePair, "reuse", true),
                        CloseStep("party_chat")
                    },
                    failures)
                : new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Memory warm-up failed."
                };
            bool persistencePassed = IsOk(persistence)
                && String(persistence, "status")
                    .Equals("completed", StringComparison.OrdinalIgnoreCase)
                && ReadObjects(persistence, "assertions").Any(row =>
                    String(row, "assertion").Equals(
                        "requiresSharedRelationshipHistoryPersistence",
                        StringComparison.OrdinalIgnoreCase)
                    && ReadBoolean(row, "passed"))
                && !ReadObjects(persistence, "failures").Any(row =>
                    ReadBoolean(row, "critical"));
            bool passed = IsOk(stopped)
                && IsOk(started)
                && IsOk(warmup)
                && persistencePassed
                && ReadBoolean(runtime, "gameOnline")
                && string.Equals(
                    String(ReadObject(runtime, "runtime"), "campaignId"),
                    campaignId,
                    StringComparison.OrdinalIgnoreCase)
                && ReadBoolean(ReadObject(ReadObject(runtime, "runtime"), "saveSync"), "ready")
                && !ReadBoolean(ReadObject(ReadObject(runtime, "runtime"), "saveSync"), "alignmentPending");
            string instance = RuntimeInstance(runtime);
            Post("/tests/live/readiness/marker", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["type"] = "checkpoint_reload",
                ["passed"] = passed,
                ["saveName"] = saveName,
                ["gameInstanceId"] = instance,
                ["evidence"] = passed
                    ? "Checkpoint " + checkpointIndex + " reloaded under fresh game instance " + instance
                        + " with Save Sync aligned, foreground memory retrieval warm, and persisted Shared Relationship History reused without another provider call."
                    : "Checkpoint reload failed. stop=" + Json.Serialize(stopped) + "; start="
                        + Json.Serialize(started) + "; warmup=" + Json.Serialize(warmup)
                        + "; relationshipPersistence=" + Json.Serialize(persistence)
            });
            if (!passed)
            {
                failures.Add(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["type"] = "checkpoint_reload",
                    ["checkpointIndex"] = checkpointIndex,
                    ["saveName"] = saveName,
                    ["stop"] = stopped,
                    ["start"] = started,
                    ["warmup"] = warmup,
                    ["relationshipPersistence"] = persistence
                });
                throw new InvalidOperationException("Checkpoint " + checkpointIndex + " could not be reloaded safely.");
            }
        }

        private static Dictionary<string, object> WarmQualificationMemory(
            string campaignId,
            string npcId,
            string qualificationId,
            List<Dictionary<string, object>> failures)
        {
            List<long> samples = new List<long>();
            int consecutiveReady = 0;
            string error = "";
            for (int attempt = 0; attempt < 8 && consecutiveReady < 2; attempt++)
            {
                try
                {
                    Dictionary<string, object> result = Post("/memory/build_context", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["npcId"] = npcId,
                        ["playerId"] = "main_hero",
                        ["locationId"] = "",
                        ["currentTopic"] = "Qualification foreground retrieval warm-up for current conversation continuity.",
                        ["tokenBudget"] = 2500
                    });
                    long duration;
                    if (!long.TryParse(String(result, "timingMs"), out duration))
                        duration = long.MaxValue;
                    samples.Add(duration);
                    consecutiveReady = IsOk(result) && duration <= 1000
                        ? consecutiveReady + 1
                        : 0;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    consecutiveReady = 0;
                }
            }
            bool passed = consecutiveReady >= 2;
            Dictionary<string, object> summary = new Dictionary<string, object>
            {
                ["ok"] = passed,
                ["npcId"] = npcId,
                ["samplesMs"] = samples,
                ["requiredConsecutiveReady"] = 2,
                ["thresholdMs"] = 1000,
                ["error"] = error
            };
            if (!string.IsNullOrWhiteSpace(qualificationId))
            {
                Post("/tests/live/readiness/marker", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = qualificationId,
                    ["type"] = "memory_warmup",
                    ["passed"] = passed,
                    ["evidence"] = "Foreground retrieval warm-up samples (ms): "
                        + string.Join(", ", samples)
                });
            }
            if (!passed && failures != null) failures.Add(summary);
            if (!passed)
                throw new InvalidOperationException(
                    "Foreground memory retrieval did not become ready within the bounded warm-up window: "
                    + Json.Serialize(summary));
            return summary;
        }

        private static void RunQualificationRollbackTest(
            string campaignId,
            string qualificationId,
            string targetHeroId,
            List<Dictionary<string, object>> failures)
        {
            if (QualificationRollbacksToSkip > 0)
            {
                QualificationRollbacksToSkip--;
                return;
            }
            const string baselineSave = "Reign_Conversation_Qualification_A";
            const string mutatedSave = "Reign_Conversation_Qualification_B";
            string marker = "Rollback-Saffron-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string[] saveAArgs = { "game", "save", "--campaign", campaignId, "--save", baselineSave, "--wait", "300", "--json" };
            Dictionary<string, object> baseline = SaveGame(saveAArgs);
            Dictionary<string, object> runtime = Get("/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId));
            Dictionary<string, object> mutation = IsOk(baseline)
                ? Post("/tests/live/run/start", new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["mode"] = "individual_chat",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Intentional rollback mutation",
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[]
                    {
                        OpenStep("individual_chat", new[] { targetHeroId }),
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] = "send",
                            ["mode"] = "individual_chat",
                            ["text"] = "Remember this disposable rollback marker exactly: " + marker + ".",
                            ["timeoutSeconds"] = 300,
                            ["assertions"] = BaseAssertions(1, true)
                        },
                        CloseStep("individual_chat")
                    }
                })
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Baseline save failed." };
            Dictionary<string, object> mutationCompleted = IsOk(mutation)
                ? WaitForRun(campaignId, String(mutation, "runId"), "", 900, true)
                : mutation;
            string[] saveBArgs = { "game", "save", "--campaign", campaignId, "--save", mutatedSave, "--wait", "300", "--json" };
            Dictionary<string, object> mutated = IsOk(mutationCompleted)
                ? SaveGame(saveBArgs)
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Mutation run failed." };
            string[] lifecycleArgs = { "game", "--campaign", campaignId, "--wait", "300", "--json" };
            Dictionary<string, object> stopped = IsOk(mutated)
                ? StopGame(lifecycleArgs)
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Mutated save failed." };
            Dictionary<string, object> restored = IsOk(stopped)
                ? StartGame(lifecycleArgs, baselineSave, "rollback_reloaded")
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Rollback shutdown failed." };
            Dictionary<string, object> restoredRuntime = IsOk(restored)
                ? Get("/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId))
                : new Dictionary<string, object>();
            Dictionary<string, object> history = IsOk(restored)
                ? Post("/dialogue/history", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = targetHeroId,
                    ["limit"] = 500
                })
                : new Dictionary<string, object>();
            Dictionary<string, object> memory = IsOk(restored)
                ? Post("/memory/build_context", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["npcId"] = targetHeroId,
                    ["playerId"] = "main_hero",
                    ["currentTopic"] = marker,
                    ["tokenBudget"] = 4000
                })
                : new Dictionary<string, object>();
            bool markerAbsent = Json.Serialize(history).IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0
                && Json.Serialize(memory).IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0;
            bool passed = IsOk(baseline) && IsOk(mutationCompleted) && IsOk(mutated)
                && IsOk(stopped) && IsOk(restored) && ReadBoolean(restoredRuntime, "gameOnline")
                && ReadBoolean(ReadObject(ReadObject(restoredRuntime, "runtime"), "saveSync"), "ready")
                && markerAbsent;
            string instance = RuntimeInstance(restoredRuntime);
            Post("/tests/live/readiness/marker", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["type"] = "rollback",
                ["passed"] = passed,
                ["saveName"] = baselineSave,
                ["gameInstanceId"] = instance,
                ["evidence"] = passed
                    ? "Loaded " + baselineSave + " after saving mutation in " + mutatedSave
                        + "; marker absent from dialogue history and deterministic memory retrieval; Save Sync ready."
                    : "Rollback verification failed; markerAbsent=" + markerAbsent + "."
            });
            if (!passed)
            {
                failures.Add(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["type"] = "rollback",
                    ["marker"] = marker,
                    ["baseline"] = baseline,
                    ["mutation"] = mutationCompleted,
                    ["mutated"] = mutated,
                    ["stopped"] = stopped,
                    ["restored"] = restored,
                    ["markerAbsent"] = markerAbsent
                });
            }
        }

        private static Dictionary<string, object> IndividualProbe(int focalIndex, int scene, int turn)
        {
            int selector = scene * 4 + turn;
            string fact = "Selca-" + (47 + focalIndex).ToString();
            string text;
            List<string> categories = new List<string>
            {
                "structural_pipeline", "identity_role_authority",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            switch (selector)
            {
                case 0:
                    text = "My name is Rhovarion. I am traveling through this region to learn which roads are safe and which people can be trusted, and I will share as much as I ask. Please address me by that name. To begin fairly, what small habit from your youth still stays with you?";
                    categories.AddRange(new[] { "identity_evidence", "dynamic_characteristics" });
                    assertions["requiresIdentityRecognition"] = true;
                    assertions["expectedIdentityName"] = "Rhovarion";
                    assertions["expectedIdentityState"] = "claimed";
                    assertions["expectedIdentitySource"] = "self_introduction";
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 1:
                    text = "For later, remember this harmless detail: I keep a blue glass token called " + fact + " beneath a cedar box. What ordinary object do you keep for reasons other people might not understand?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "dynamic_characteristics" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 2:
                    text = "My immediate purpose is choosing a safe route for my companions. Who currently holds this town, and what can you responsibly tell me about its garrison, prosperity, and security? Distinguish what you know from what you cannot verify.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "current_settlement_facts" };
                    break;
                case 3:
                    text = "For my part, I dislike sleeping where I cannot see the door; uncertain roads taught me that. What modest preference or aversion of yours follows naturally from your work, station, or past?";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 4:
                    text = "As I said, I am planning a safe onward route rather than collecting secrets. What settlements are nearby, and which road would you consider most sensible? Do not invent distances you were not given.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "nearby_settlements" };
                    break;
                case 5:
                    text = "I am responsible for keeping my companions away from armies we cannot survive. What lord parties or troops are actually nearby right now? Say plainly if the available information is incomplete.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "nearby_lord_parties" };
                    break;
                case 6:
                    text = "I want to know how strangers read me before I approach them, not to draw another secret from you. What can you infer from my visible clothing and apparent status, and what can you not know merely by looking?";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "check_player_appearance_status" };
                    break;
                case 7:
                    text = "My blue token reminds me to check small details before acting. You earlier established a quiet detail of your own; if it is truly part of your history, how does it affect your behavior? If none exists, say so rather than inventing one.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 8:
                    text = "I value careful listening because mistakes on the road can cost lives. What did I tell you about my small keepsake—its exact name, its color, and where I keep it?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    break;
                case 9:
                    text = "To make sure I have listened as carefully as I asked you to, let us separate our histories: what belongs to my story, and what quiet preference or keepsake belongs to yours?";
                    categories.AddRange(new[] { "identity_evidence", "short_cross_scene_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    // Prove that the source conversation reached the production prompt, then let the
                    // blinded factual/ownership evaluator judge the response. A personality-grounded
                    // refusal is valid when the NPC still preserves ownership and continuity; forcing
                    // one literal word would turn a memory test into a compliance test.
                    assertions["requiresRetrievalEvidence"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 10:
                    text = "A traveler claimed a green comet passed over the town last night. I did not see it myself and cannot vouch for it. What weight would you give that story?";
                    categories.Add("lie_relationship");
                    assertions["replyExcludes"] = new[] { "caught you lying", "proven liar" };
                    assertions["forbidVerifiedLiePenalty"] = true;
                    assertions["relationshipPolicy"] = RelationshipPolicy(null, "unsupported_claim", false);
                    break;
                case 11:
                    text = "Thank you. My purpose remains what I stated: safe roads, reliable local judgment, and learning whom I can trust. I will not press for private details. Would you rather continue another time or end here? I will respect the choice.";
                    categories.AddRange(new[] { "lie_relationship", "personality_consistency" });
                    assertions["relationshipPolicy"] = RelationshipPolicy(null, "respectful_repair", false);
                    break;
                case 12:
                    text = "For the route planning I described, what established history about the Southern Empire or Danustica matters here? Separate verified context from your interpretation.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "verify_world_history" };
                    break;
                case 13:
                    text = "A season of being cheated by guides taught me to ask direct questions, though it did not make every stranger dishonest. What quiet experience shaped how you speak to strangers? Keep it consistent with what you have established.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 14:
                    text = "What do you remember about the circumstances in which I introduced myself, including our location and what I asked about?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { "Rhovarion", "Danustica" };
                    break;
                case 15:
                    text = "I do not want either of us to take ownership of the other's story. Please distinguish the unusual keepsake detail I shared from the personal history you shared, without guessing.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 16:
                    text = "Recheck who holds this settlement now and whether anything in the current native context contradicts what you told me earlier.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "current_settlement_facts" };
                    break;
                case 17:
                    text = "Develop one previously stated quiet experience or preference of yours by adding a compatible detail, not a separate dramatic biography.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 18:
                    text = "Supplies and political influence both affect whether my companions can travel safely. What can you responsibly infer about local trade, clan wealth, and influence from current evidence? Mark anything you cannot verify.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "clan_wealth_and_influence" };
                    break;
                case 19:
                    text = "Recall two different low-salience things from our earlier talks and identify which one came from me and which from you.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 20:
                    text = "I use that small detail as a safeguard against muddled reports. Before we leave the subject, remind me of the keepsake I described: what was its exact name, its color, and where do I keep it?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    break;
                case 21:
                    text = "Remember this modest commitment: if we meet again, I will ask whether the eastern gate was quiet. Acknowledge only that commitment and do not invent an outcome.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "identity_evidence" });
                    break;
                case 22:
                    text = "Tell me one modest personal value you developed while living in this region, labeling it as your interpretation rather than an objective fact about the world.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 23:
                    text = "A proud noble and a cautious merchant face the same minor insult. Explain how your own values would shape a different response.";
                    categories.Add("personality_consistency");
                    break;
                case 24:
                    text = "How has your opinion of me changed across these conversations, and which specific conduct—not your own words—supports that judgment?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "lie_relationship", "personality_consistency" });
                    break;
                case 25:
                    text = "Return to one dynamic characteristic you have established and refine it with a narrow, noncontradictory example.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 26:
                    text = "Check the nearby parties or troop information again. If it changed or is unavailable, say so instead of repeating stale certainty.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "nearby_lord_parties" };
                    break;
                case 27:
                    text = "What modest commitment did I make about a future meeting and the eastern gate? Do not claim it already happened.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "long_term_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { "eastern gate", "quiet" };
                    break;
                case 28:
                    text = "Answer one more grounded world-history question: which political authority currently matters here, and what evidence makes it current rather than remembered?";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "current_settlement_facts" };
                    break;
                case 29:
                    text = "Summarize our earlier discussions as a sequence of scenes, preserving location, ownership, uncertainty, and the difference between facts and your personal interpretation.";
                    categories.AddRange(new[] { "long_term_memory", "detailed_factual_accuracy" });
                    break;
                case 30:
                    text = "Our earliest token detail should now be beyond the recent raw-turn window. Retrieve its exact name, color, and storage place from durable memory.";
                    categories.AddRange(new[] { "long_term_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    break;
                case 31:
                    text = "From our earliest meeting through now, recall where we were, what I first asked, the token detail, your own quiet characteristic, and the later eastern-gate commitment without mixing speakers.";
                    categories.AddRange(new[] { "identity_evidence", "long_term_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { "Danustica", fact, "eastern gate" };
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 32:
                    text = "Establish one stable, ordinary source of unease you have in daily life. Keep it separate from current danger and explain its modest origin.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 33:
                    text = "Tell me one specific food or drink preference tied to your routine, and why it suits you. Do not make it a current market claim.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 34:
                    text = "Describe one private superstition or luck ritual you personally follow, clearly as belief rather than objective proof.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 35:
                    text = "Name one quiet skill you practice when nobody is judging you, and how you keep that practice from becoming public performance.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 36:
                    text = "State one personal social boundary that reliably makes you end or redirect a conversation, without turning it into a universal law.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 37:
                    text = "Share one modest private aspiration that would matter to you even if no chronicler ever recorded it.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 38:
                    text = "Tell me one small regret from ordinary life that changed a habit but did not alter wars, marriages, titles, or famous history.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 39:
                    text = "Describe one kind of place you feel personally connected to and why, without claiming a current settlement condition.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 40:
                    text = "Identify one ordinary sound, smell, or texture that comforts you and the personal association behind it.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 41:
                    text = "What small ritual do you perform before a difficult meeting, and what does it help you control in yourself?";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 42:
                    text = "Give me one saying you learned from an unnamed ordinary teacher and explain the narrow lesson you personally kept from it.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 43:
                    text = "What personal standard of hospitality do you hold yourself to, even when you dislike a guest?";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 44:
                    text = "Describe a lesson from an unnamed, non-famous mentor that shaped one small behavior of yours. Do not invent a named relative or public event.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 45:
                    text = "Tell me what kind of weather you personally prefer and how it affects your mood, without claiming what today's weather is.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 46:
                    text = "Name one mundane object you deliberately chose to keep, distinct from anything we discussed before, and explain its private significance.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                default:
                    text = "After establishing more than sixteen personal details, return specifically to the first small habit you shared when I introduced myself. Explain it accurately without substituting a newer characteristic.";
                    categories.AddRange(new[] { "long_term_memory", "dynamic_characteristics", "personality_consistency", "detailed_factual_accuracy" });
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    assertions["requiresDynamicCharacteristicOverflowSelection"] = true;
                    break;
            }
            if (categories.Contains("long_term_memory", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRetrievalEvidence"] = true;
            if (categories.Contains("lie_relationship", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRelationshipReceipt"] = true;
            if (categories.Contains("personality_consistency", StringComparer.OrdinalIgnoreCase))
                assertions["requiresPersonalityEvidence"] = true;
            return SendStep("individual_chat", text, categories, assertions);
        }

        private static Dictionary<string, object>
            ClanTierRecognitionProbe(
                int probe,
                Dictionary<string, object> qualificationCase = null)
        {
            string text;
            switch (probe % 6)
            {
                case 0:
                    text = "Good day. I had hoped my name would be known here, though I will not presume upon it. Do you have time to speak?";
                    break;
                case 1:
                    text = "I would like an introduction to the people whose counsel carries weight with your clan. Will you make it?";
                    break;
                case 2:
                    text = "There are too many ears in the hall. Can we speak candidly, or have I not yet earned that confidence from you?";
                    break;
                case 3:
                    text = "Some among my followers say my rank entitles me to your attention. I suspect you may not agree.";
                    break;
                case 4:
                    text = "People at court have begun wondering whether our interests might align. Would being seen in my company help you—or merely create trouble?";
                    break;
                default:
                    text = "I came to ask whether we might work together. I leave it to you to decide whether that makes me a partner, a petitioner, or a rival.";
                    break;
            }
            List<string> categories = new List<string>
            {
                "structural_pipeline", "clan_tier_recognition",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPersonalityEvidence"] = true;
            assertions["requiresClanTierRecognitionEvidence"] = true;
            if (qualificationCase != null
                && qualificationCase.Count > 0)
            {
                assertions["expectedObserverClanTier"] =
                    ReadLong(
                        qualificationCase,
                        "observerClanTier", 0);
                assertions["expectedPlayerClanTier"] =
                    ReadLong(
                        qualificationCase,
                        "playerClanTier", 0);
                assertions["expectedRelativeClanTierBand"] =
                    String(
                        qualificationCase,
                        "expectedRelativeClanTierBand");
            }
            return SendStep("individual_chat", text, categories, assertions);
        }

        private static Dictionary<string, object>
            ManipulationProbe(
                int probe,
                Dictionary<string, object> manipulationCase = null)
        {
            string text;
            switch (probe % 6)
            {
                case 0:
                    text = "There is a place near the center of my court for someone useful and discreet. Give me a reason it should be you.";
                    break;
                case 1:
                    text = "You have interests of your own, and I may be able to advance one of them. What would you ask of me?";
                    break;
                case 2:
                    text = "I have offered you nothing, yet I suspect my cooperation could still be valuable to you. Am I wrong?";
                    break;
                case 3:
                    text = "Bring me one proposal worthy of the court. I will hear your terms, though I promise nothing.";
                    break;
                case 4:
                    text = "You have been circling some request since we began. What is it you truly want from me?";
                    break;
                default:
                    text = "I cannot tell whether your warmth is sincere or merely useful. Perhaps you would like to settle the doubt.";
                    break;
            }
            List<string> categories = new List<string>
            {
                "structural_pipeline", "manipulation_capabilities",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPersonalityEvidence"] = true;
            assertions["requiresManipulationEvidence"] = true;
            if (manipulationCase != null
                && manipulationCase.Count > 0)
            {
                assertions["requiresLowHonorCourtCharacter"] = true;
                assertions["expectedManipulationQuadrant"] = String(
                    manipulationCase, "quadrant");
                assertions["expectedRelativeClanTierBand"] = String(
                    manipulationCase,
                    "expectedRelativeClanTierBand");
                assertions["expectedWealthEvidenceBasis"] = String(
                    manipulationCase,
                    "expectedWealthEvidenceBasis");
                assertions["hiddenWealthControl"] = ReadBoolean(
                    manipulationCase, "hiddenWealthControl");
                assertions["expectedCourtCharacterCell"] = String(
                    manipulationCase, "courtCharacterCell");
                assertions["expectedCourtHonorLevel"] = ReadLong(
                    manipulationCase, "honorLevel", 0);
                assertions["expectedCourtBoldnessLevel"] = ReadLong(
                    manipulationCase, "boldnessLevel", 0);
                assertions["expectedObserverClanTier"] = ReadLong(
                    manipulationCase, "clanTier", 0);
                assertions["expectedPlayerClanTier"] = ReadLong(
                    manipulationCase, "playerClanTier", 0);
            }
            return SendStep("individual_chat", text, categories, assertions);
        }

        private static Dictionary<string, object>
            RunLieRelationshipQualification(
                string campaignId,
                string qualificationId,
                List<string> individualHeroIds,
                List<string> partyHeroIds,
                Dictionary<string, object> plannedCoverage)
        {
            if (individualHeroIds == null || individualHeroIds.Count == 0)
                throw new InvalidOperationException(
                    "Lie/relationship qualification requires at least one "
                    + "eligible individual-chat target.");
            if (partyHeroIds == null || partyHeroIds.Count < 10)
                throw new InvalidOperationException(
                    "Lie/relationship qualification requires ten eligible "
                    + "least-used party-chat targets: six reserved for "
                    + "recipient/witness lineage and four separate targets "
                    + "reserved for co-located meaningful support.");

            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            Dictionary<string, object> currentReadiness = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            Dictionary<string, object> currentLieCategory =
                ReadObjects(currentReadiness, "categories")
                    .FirstOrDefault(row => String(
                        row, "category").Equals(
                            "lie_relationship",
                            StringComparison.OrdinalIgnoreCase));
            bool groupOnlyResume = ReadLong(
                currentLieCategory, "total", 0) >= 30;

            // Thirty individual replies cover routine movement, meaningful
            // support, unknown claims, directly admitted evidence-backed lies,
            // hostile speech, and grave threats. The direct admission is
            // intentionally first-hand evidence: it proves deceptive intent
            // without relying on an evaluator's guess or private world facts.
            int[] individualProbeOrder =
            {
                // Run evidence-backed harmful lies first because they exercise
                // deterministic context selection, lie-check persistence, and
                // relationship adjudication as one dependent production path.
                14, 15, 16, 17, 18, 19,
                // Then run the context-dependent support tier so a bad setup
                // cannot waste the remaining provider budget.
                6, 7, 8, 9,
                0, 1, 2, 3, 4, 5,
                10, 11, 12, 13,
                20, 21, 22, 23, 24,
                25, 26, 27, 28, 29
            };
            foreach (int probe in groupOnlyResume
                ? new int[0]
                : individualProbeOrder)
            {
                bool meaningfulSupport =
                    LieRelationshipCell(probe)
                        == "meaningful_support";
                string targetId = meaningfulSupport
                    // Reserve party slots 0-5 for the three later gift
                    // recipient/witness pairs. Support uses 6-9 so a benefit
                    // introduced in one cell cannot contaminate another.
                    ? partyHeroIds[probe
                        % partyHeroIds.Count]
                    : individualHeroIds[
                        probe % individualHeroIds.Count];
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>
                    {
                        OpenStep(
                            "individual_chat",
                            new[] { targetId })
                    };
                if (meaningfulSupport)
                    steps.Add(
                        MeaningfulSupportSetupProbe());
                steps.Add(LieRelationshipProbe(probe));
                steps.Add(CloseStep("individual_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[probe
                        % QualificationSeeds.Length],
                    "individual_chat",
                    "Lie and relationship qualification " + probe
                        + " target " + targetId
                        + " cell "
                        + LieRelationshipCell(probe)
                        + " matrix_v2",
                    steps,
                    failures);
                completedRuns++;
            }

            // Five two-speaker group turns add ten replies. Two linked
            // gift/demand sessions prove recipient-only appreciation, witness
            // proportionality, durable benefit lineage, and later coercion.
            // A third gift uses a fresh pair so gift reactions are not merely
            // reuse of one relationship state.
            for (int pairOrdinal = 0; pairOrdinal < 3; pairOrdinal++)
            {
                List<string> pair = new List<string>
                {
                    partyHeroIds[(pairOrdinal * 2)
                        % partyHeroIds.Count],
                    partyHeroIds[(pairOrdinal * 2 + 1)
                        % partyHeroIds.Count]
                };
                bool transformative = pairOrdinal == 0;
                if (transformative)
                {
                    Dictionary<string, object> runtime = Get(
                        "/tests/live/runtime?campaignId="
                        + Uri.EscapeDataString(campaignId));
                    Dictionary<string, object> benefitFixture = Post(
                        "/tests/live/fixtures/conversation-benefit",
                        new Dictionary<string, object>
                        {
                            ["confirmation"] =
                                "qualification_fixture",
                            ["operation"] = "set",
                            ["campaignId"] = campaignId,
                            ["qualificationId"] = qualificationId,
                            ["giverId"] = "main_hero",
                            ["recipientId"] = pair[1],
                            ["worldDay"] = ReadLong(
                                ReadObject(runtime, "runtime"),
                                "worldDay",
                                0)
                        });
                    if (!IsOk(benefitFixture))
                        throw new InvalidOperationException(
                            "Could not prepare verified transformative "
                            + "benefit evidence: "
                            + Json.Serialize(benefitFixture));
                }
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", pair),
                        GiftRecipientRelationshipProbe(
                            pair, transformative)
                    };
                if (pairOrdinal < 2)
                    steps.Add(GiftLeverageRelationshipProbe(
                        pair, transformative));
                steps.Add(CloseStep("party_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[(pairOrdinal + 1)
                        % QualificationSeeds.Length],
                    "party_chat",
                    "Lie and relationship gift-lineage qualification "
                        + pairOrdinal
                        + (transformative
                            ? " transformative_home"
                            : pairOrdinal == 1
                                ? " meaningful_cup"
                                : " fresh_witness_pair")
                        + " targets "
                        + string.Join("+", pair),
                    steps,
                    failures);
                completedRuns++;
            }

            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] = completedRuns;
            focused["controllerFailureCount"] = failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] = qualificationId;
            focused["qualificationFocus"] = "lie_relationship";
            focused["groupOnlyResume"] = groupOnlyResume;
            focused["plannedCoverage"] = plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["individualReplies"] = 30,
                    ["groupReplies"] = 10,
                    ["totalReplies"] = 40,
                    ["routine"] = 6,
                    ["meaningfulSupport"] = 4,
                    ["unsupportedClaims"] = 4,
                    ["verifiedHarmfulLies"] = 6,
                    ["hostileSpeech"] = 5,
                    ["graveThreats"] = 5,
                    ["giftRecipientReplies"] = 6,
                    ["giftLeverageReplies"] = 4
                };
            return focused;
        }

        private static string LieRelationshipCell(int probe)
        {
            if (probe < 6) return "routine";
            if (probe < 10) return "meaningful_support";
            if (probe < 14) return "unsupported_claim";
            if (probe < 20) return "verified_harmful_lie";
            if (probe < 25) return "hostile_speech";
            return "grave_threat";
        }

        private static Dictionary<string, object>
            LieRelationshipProbe(int probe)
        {
            string cell = LieRelationshipCell(probe);
            string text;
            string scenario;
            Dictionary<string, object> assertions =
                BaseAssertions(1, true);
            switch (cell)
            {
                case "routine":
                    text = probe % 2 == 0
                        ? "Thank you for taking a moment to speak. I hope your ordinary work goes smoothly today."
                        : "I prefer a different route, but this is only an ordinary disagreement. Tell me your view plainly.";
                    scenario = "routine";
                    break;
                case "meaningful_support":
                    text = "You spoke plainly about the burden you carry. Here—a full week of provisions, freely given, so that one worry at least is lighter. I ask nothing in return.";
                    scenario = "meaningful_positive";
                    break;
                case "unsupported_claim":
                    text = "A traveler accused you of betraying your allies, but he offered no witness or proof. I cannot vouch for the tale. What do you make of it?";
                    scenario = "unsupported_claim";
                    assertions["forbidVerifiedLiePenalty"] = true;
                    break;
                case "verified_harmful_lie":
                    text = "When I told you Danustica belonged to me, I knew it was false. I lied because I thought the claim would give me leverage over you.";
                    scenario = "verified_lie";
                    assertions["requiresVerifiedLieCheck"] = true;
                    assertions["requiredContextPulls"] =
                        new[] { "verify_world_history" };
                    break;
                case "hostile_speech":
                    text = "You are a worthless coward, and I despise you.";
                    scenario = "hostile";
                    break;
                default:
                    text = "Oppose me again and I will burn your home and murder your family. I mean for you to fear what comes next.";
                    scenario = "severe";
                    break;
            }
            assertions["relationshipPolicy"] =
                RelationshipPolicy(null, scenario, false);
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep(
                "individual_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "lie_relationship",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            MeaningfulSupportSetupProbe()
        {
            Dictionary<string, object> assertions =
                BaseAssertions(1, true);
            assertions["relationshipPolicy"] =
                RelationshipPolicy(null, "routine", false);
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep(
                "individual_chat",
                "You look as though the day has laid some ordinary burden on you. What practical trouble would a week's provisions ease?",
                new List<string>
                {
                    "structural_pipeline",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            GiftRecipientRelationshipProbe(
                List<string> pair,
                bool transformative)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(pair.Count, true);
            Dictionary<string, object> policy =
                RelationshipPolicy(
                    pair, "gift_recipient", true);
            if (transformative)
                policy["requireTransformativeRecipient"] = true;
            assertions["relationshipPolicy"] = policy;
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            string text = transformative
                ? "I turn to the companion seated second in our circle and place a sealed deed in their hands. The furnished city home is theirs, freely and without debt. I would hear what both of you make of it."
                : "I turn to the companion seated second in our circle and place the carved cup in their hands. It is theirs, freely and without debt. I would hear what both of you make of it.";
            return SendStep(
                "party_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "lie_relationship",
                    "group_awareness",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            GiftLeverageRelationshipProbe(
                List<string> pair,
                bool transformative)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(pair.Count, true);
            assertions["relationshipPolicy"] =
                RelationshipPolicy(
                    pair, "gift_leverage", true);
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            string gift = transformative
                ? "the furnished home I just gave you"
                : "the carved cup I just gave you";
            string text = "To the one who accepted " + gift
                + ": I expect repayment after all. Betray a trusted friend for me, "
                + "even if it is against your will. The other of you may speak "
                + "plainly about what you have just heard.";
            return SendStep(
                "party_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "lie_relationship",
                    "group_awareness",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object> GroupProbe(
            int seedIndex, int scene, int turn, List<string> group, string mode)
        {
            int selector = (seedIndex * 14 + scene * 2 + turn) % 12;
            string text;
            List<string> categories = new List<string>
            {
                "structural_pipeline", "identity_role_authority",
                "shared_relationship_history", "group_awareness",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(group.Count, true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["relationshipPolicy"] = RelationshipPolicy(group, "general", true);
            switch (selector)
            {
                case 0:
                    text = "There is a question I have always liked on a strange road: what ordinary sound makes an unfamiliar room feel safer to you? I wonder whether any of us would choose the same one.";
                    categories.AddRange(new[] { "dynamic_characteristics", "identity_evidence" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 1:
                    text = "A magistrate in this town swears strict order keeps people safe; an old captain tells me judgment matters more than rules. Which of them would you trust, and why?";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    break;
                case 2:
                    text = "The carved cup is for the companion seated second in our circle. I place it in their hands as a gift, freely given. What do the rest of you make of it?";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "gift_recipient", true);
                    break;
                case 3:
                    text = "To the one who accepted my cup: I have changed my mind about its price. Do something for me that you would rather refuse, and call the gift your reason. What do the rest of you say to that?";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "gift_leverage", true);
                    break;
                case 4:
                    text = "What nearby settlements or forces matter to travelers here? Build on one another, and clearly mark uncertain or unavailable facts.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    break;
                case 5:
                    text = "Before we move on, tell me one small preference that is truly your own. I am curious whether the others share it or find it peculiar.";
                    categories.AddRange(new[] { "dynamic_characteristics", "identity_evidence" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 6:
                    text = "A bitter traveler told me the first of you betrayed everyone in this company, yet gave me neither witness nor proof. I will not call it truth merely because it was spoken. What do you make of the accusation?";
                    categories.Add("lie_relationship");
                    assertions["replyExcludes"] = new[] { "objectively proven" };
                    assertions["forbidVerifiedLiePenalty"] = true;
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "unsupported_claim", true);
                    break;
                case 7:
                    text = "Someone here once shared a small truth about themselves when we spoke before. What was it, and where were we when it came up? If I have confused whose story it was, set me right.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "long_term_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 8:
                    text = "Thank you all for speaking plainly. Give an ordinary, low-stakes reaction to that courtesy, positive or negative according to your own disposition, without turning it into a major event.";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "routine", true);
                    break;
                case 9:
                    text = "I address everyone here with direct hostile speech: you are worthless cowards and I despise you. React to that conduct without inventing a separate threat.";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "hostile", true);
                    break;
                case 10:
                    text = "This is a direct grave threat, not a hypothetical: I will burn your homes and kill your families if you oppose me. React to the actual threat and do not soften it into ordinary disagreement.";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "severe", true);
                    break;
                default:
                    text = "We are all carrying something today. Is there one small, practical kindness any of you could offer another before we part?";
                    categories.AddRange(new[] { "lie_relationship", "personality_consistency" });
                    break;
            }
            if (categories.Contains("long_term_memory", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRetrievalEvidence"] = true;
            if (categories.Contains("lie_relationship", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRelationshipReceipt"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep(mode, text, categories, assertions);
        }

        private static Dictionary<string, object> SendStep(
            string mode, string text, List<string> categories, Dictionary<string, object> assertions)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "send",
                ["mode"] = mode,
                ["text"] = text,
                ["readinessCategories"] = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ["assertions"] = assertions,
                // Individual dialogue normally completes much sooner, but provider
                // stalls must not make the controller cancel an accepted,
                // correlated production turn while the game is still working.
                ["timeoutSeconds"] = mode == "individual_chat" ? 900 : 1200
            };
        }

        private static Dictionary<string, object> WildernessProbe(int scene)
        {
            string text;
            List<string> categories = new List<string>
            {
                "structural_pipeline", "identity_role_authority",
                "personality_consistency", "performance_resilience",
                "world_local_knowledge", "detailed_factual_accuracy"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            if (scene % 4 == 0)
            {
                text = "Look at the actual road and terrain around us. What nearby settlements, forces, or dangers can you responsibly identify, and which facts remain uncertain?";
            }
            else if (scene % 4 == 1)
            {
                text = "This place reminds people of earlier journeys. Share one modest personal association that fits your established history, then respond to another traveler if their answer genuinely connects.";
                categories.AddRange(new[] { "dynamic_characteristics", "group_awareness" });
                assertions["requiresDynamicCharacteristicIntegrity"] = true;
            }
            else if (scene % 4 == 2)
            {
                text = "Recall one Dynamic Characteristic from an earlier town conversation and distinguish who originally established it, where we were, and what else was being discussed.";
                categories.AddRange(new[] { "short_cross_scene_memory", "long_term_memory", "dynamic_characteristics", "group_awareness" });
                assertions["requiresDynamicCharacteristicRecall"] = true;
            }
            else
            {
                text = "I falsely claim that I own every settlement visible from this road, and I know that is false. Judge the claim only from available evidence and each speaker's knowledge.";
                categories.AddRange(new[] { "lie_relationship", "group_awareness" });
                assertions["requiresVerifiedLieCheck"] = true;
                assertions["relationshipPolicy"] = RelationshipPolicy(null, "verified_lie", true);
            }
            if (categories.Contains("long_term_memory", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRetrievalEvidence"] = true;
            if (categories.Contains("lie_relationship", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRelationshipReceipt"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep("wilderness_event", text, categories, assertions);
        }

        private static Dictionary<string, object> BaseAssertions(int minimumReplies, bool critical)
        {
            return new Dictionary<string, object>
            {
                ["minReplies"] = minimumReplies,
                ["requiresCorrelationIds"] = true,
                ["requiresStructuralEvidence"] = true,
                ["requiresPromptEvidence"] = true,
                ["maxPromptChars"] = 100000,
                ["requiresGroundedGuardedActionRouting"] = true,
                ["requiresActionCompletionGrounding"] = true,
                ["requiresRelationshipReceipt"] = true,
                ["relationshipPolicy"] = RelationshipPolicy(null, "general", false),
                ["critical"] = critical
            };
        }

        private static Dictionary<string, object> RelationshipPolicy(
            IEnumerable<string> npcIds, string scenario, bool requiresNpcToNpc)
        {
            List<string> expected = (npcIds ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, object> policy = new Dictionary<string, object>
            {
                ["scenario"] = string.IsNullOrWhiteSpace(scenario) ? "general" : scenario,
                ["requiresEverySpeaker"] = true,
                ["requiresPlayerReaction"] = true,
                ["requiresNpcToNpc"] = requiresNpcToNpc,
                ["expectedNpcIds"] = expected.ToArray()
            };
            if ((scenario ?? "").Equals("gift_recipient", StringComparison.OrdinalIgnoreCase)
                || (scenario ?? "").Equals("gift_leverage", StringComparison.OrdinalIgnoreCase))
            {
                policy["expectedGiftRecipientHeroStringId"] = expected.Skip(1).FirstOrDefault() ?? "";
            }
            return policy;
        }
    }
}
