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
    }
}
