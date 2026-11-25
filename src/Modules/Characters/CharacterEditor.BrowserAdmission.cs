using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        // Publish the browser admission and native delivery as one workflow.
        // Canonical fixtures call the supplied guarded paths before activation.
        private static bool OwnedEditorBrowserDeliveryReady => false;

        private static Dictionary<string, object> ReadEditorBrowser(Dictionary<string, object> payload, bool ownedNative)
        {
            if (!ownedNative || Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld)
                return ReadEditorBrowserCore(payload, ownedNative);
            Program.CampaignDataGate.EnterReadLock();
            try { return ReadEditorBrowserCore(payload, ownedNative); }
            finally { Program.CampaignDataGate.ExitReadLock(); }
        }

        private static Dictionary<string, object> WriteEditorBrowser(Dictionary<string, object> payload, bool ownedNative, bool rollback)
        {
            payload ??= TestDict();
            return OwnEditorCampaignWrite(() =>
            {
                string ResolveCampaign()
                {
                    string value = ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
                    return !rollback && string.IsNullOrWhiteSpace(value) ? SettingsService.LatestCampaignId() : value;
                }
                Dictionary<string, object> Mutation(string campaign) => rollback
                    ? CharacterEditorRollbackApiLocked(payload, campaign, ownedNative)
                    : CharacterEditorSaveApiLocked(payload, campaign, ownedNative);
                if (!ownedNative)
                {
                    lock (NarrativeStateLock) return Mutation(ResolveCampaign());
                }
                string ownedCampaign = ResolveCampaign();
                return RunInEditorNativeBrowserWriteScope(ownedCampaign, () => Mutation(ownedCampaign));
            });
        }

        private static string ReadExactNativeEditorBasis(Dictionary<string, object> payload) =>
            payload.Keys.Count(key => key.Equals("nativeRevisionBasis", StringComparison.OrdinalIgnoreCase)) == 1
            && payload.Keys.Contains("nativeRevisionBasis", StringComparer.Ordinal)
            && payload.TryGetValue("nativeRevisionBasis", out object value) && value is string basis ? basis : "";

        private static bool EditorBrowserNativeSourcesAgree(Dictionary<string, object> payload, Dictionary<string, object> documents,
            Dictionary<string, object> native)
        {
            if (payload.ContainsKey("nativeChanges") && native == null) return false;
            if (documents?.ContainsKey("profile") == true && !payload.ContainsKey("profileEdit")) return false;
            if (payload.ContainsKey("profileEdit"))
            {
                var fields = ReadDictionary(ReadDictionary(payload, "profileEdit"), "fields");
                if (fields == null || fields.Keys.Any(key => native == null || !native.ContainsKey(key))) return false;
            }
            if (documents?.ContainsKey("wealth") != true) return true;
            var wealth = ReadDictionary(documents, "wealth");
            return wealth != null && native?.TryGetValue("wealth", out object change) == true
                && CanonicalJson(wealth) == CanonicalJson(change);
        }

        private static Dictionary<string, object> EditorBrowserNativeAdmissionRequired() =>
            TestDict("ok", false, "conflict", true, "nativeEditAdmission", "registered_load_required",
                "error", "Load a registered Save Sync save and reload the Character Editor to edit native fields.");

#if !REIGN_EXCLUDE_VERIFICATION
        private const string EditorBrowserAdmissionFixtureHero = "editor_browser_fixture_hero";

        internal static void VerifyEditorBrowserAdmission(Action<string, bool, string> add, string campaign, bool registered)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || !CampaignFileService.IsInternalCampaignId(campaign))
                throw new InvalidOperationException("The owned Editor browser fixture needs an isolated validation campaign.");

            const string hero = EditorBrowserAdmissionFixtureHero;
            string profilePath = CampaignFileService.CharacterFile(campaign, hero, "profile.json");
            string statePath = CampaignFileService.CharacterFile(campaign, hero, "state.json");
            if (registered)
            {
                CampaignFileService.WriteJsonObject(profilePath, TestDict("heroStringId", hero, "name", "Before"));
                CampaignFileService.WriteJsonObject(statePath, TestDict("value", "before"));
            }

            var target = TestDict("campaignId", campaign, "heroStringId", hero);
            Dictionary<string, object> Open() => ReadEditorBrowser(target, ownedNative: true);
            string State() => ReadString(CampaignFileService.ReadJsonObject(statePath), "value", "");
            string Name() => ReadString(CampaignFileService.ReadJsonObject(profilePath), "name", "");
            Dictionary<string, object> SavePacket(Dictionary<string, object> loaded, string nextState, string name = null)
            {
                var body = new Dictionary<string, object>(target)
                {
                    ["revisionToken"] = ReadString(loaded, "revisionToken", ""),
                    ["documents"] = TestDict("state", TestDict("value", nextState))
                };
                if (name != null)
                {
                    var changes = TestDict("name", name);
                    body["nativeChanges"] = changes;
                    body["profileEdit"] = TestDict("basis", ReadString(loaded, "profileEditBasis", ""), "fields", changes);
                }
                return body;
            }
            Dictionary<string, object> RollbackPacket(Dictionary<string, object> loaded, string revision) =>
                new Dictionary<string, object>(target)
                {
                    ["revisionId"] = revision, ["nativeRevisionBasis"] = ReadString(loaded, "nativeRevisionBasis", "")
                };

            var loaded = Open();
            byte[] initialState = File.ReadAllBytes(statePath);
            byte[] initialProfile = File.ReadAllBytes(profilePath);
            var denied = WriteEditorBrowser(SavePacket(loaded, "mixed_denied", "Mixed denied"), ownedNative: true, rollback: false);
            var bare = SavePacket(loaded, "bare_denied");
            ReadDictionary(bare, "documents")["profile"] = TestDict("heroStringId", hero, "name", "Bare raw");
            var bareDenied = WriteEditorBrowser(bare, ownedNative: true, rollback: false);
            bool refusedBeforeFiles = !ReadBool(denied, "ok", false) && !ReadBool(bareDenied, "ok", false)
                && File.ReadAllBytes(statePath).SequenceEqual(initialState)
                && File.ReadAllBytes(profilePath).SequenceEqual(initialProfile);
            if (!registered)
            {
                var serverOnly = WriteEditorBrowser(SavePacket(loaded, "server_only"), ownedNative: true, rollback: false);
                add("native_editor_unregistered_browser_is_readable_and_server_only_save_still_works",
                    ReadBool(loaded, "ok", false) && ReadString(loaded, "nativeEditAdmission", "") == "registered_load_required"
                    && !loaded.ContainsKey("nativeRevisionBasis") && refusedBeforeFiles
                    && ReadBool(serverOnly, "ok", false) && !ReadBool(serverOnly, "nativeQueued", false)
                    && State() == "server_only" && File.ReadAllBytes(profilePath).SequenceEqual(initialProfile),
                    "A real degraded continuation can read characters and save server-only state while mixed or raw native edits leave files unchanged.");
                return;
            }

            string basis = ReadString(loaded, "nativeRevisionBasis", "");
            var owned = SavePacket(loaded, "owned_effect", "After");
            owned["nativeRevisionBasis"] = basis;
            var priorBoundary = EditorRevisionBoundaryForTests;
            bool scopesHeld = true;
            int boundaries = 0;
            Dictionary<string, object> WriteWithCommitWitness(Dictionary<string, object> body, bool rollback)
            {
                EditorRevisionBoundaryForTests = stage =>
                {
                    priorBoundary?.Invoke(stage);
                    if (stage is "before_sql_commit" or "after_sql_commit")
                    {
                        boundaries++;
                        scopesHeld &= OwnsEditorNativeBrowserWriteScope(campaign) && System.Threading.Monitor.IsEntered(NarrativeStateLock);
                    }
                };
                try { return WriteEditorBrowser(body, ownedNative: true, rollback: rollback); }
                finally { EditorRevisionBoundaryForTests = priorBoundary; }
            }

            var saved = WriteWithCommitWitness(owned, rollback: false);
            if (!ReadBool(saved, "ok", false))
                throw new InvalidOperationException("The owned Editor browser fixture could not save its mixed revision.");
            string revision = ReadString(saved, "revisionId", "");
            using var reader = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: false, initializeInfrastructure: false);
            var commands = CharactersStore.SelectCharacterEditorNativeCommands2(reader, TestDict("id", hero));
            var command = commands.Single();
            bool ownedEffect = ReadBool(loaded, "ok", false) && ReadString(loaded, "nativeEditAdmission", "") == "ready"
                && refusedBeforeFiles && basis.Length == 64 && State() == "owned_effect" && Name() == "After"
                && ReadString(command, "status", "") == "pending" && ReadString(command, "revision_id", "") == revision
                && ReadString(command, "manual_origin_json", "").Length > 0;
            var latest = Open();
            var stale = SavePacket(latest, "stale_denied", "Stale denied");
            stale["nativeRevisionBasis"] = basis;
            var staleDenied = WriteEditorBrowser(stale, ownedNative: true, rollback: false);
            add("native_editor_browser_basis_is_fresh_and_mixed_commit_keeps_exact_campaign_scope",
                ownedEffect && ReadString(latest, "nativeRevisionBasis", "") != basis && !ReadBool(staleDenied, "ok", false)
                && State() == "owned_effect" && Name() == "After" && scopesHeld && boundaries == 2,
                "The actual browser save refuses missing and stale authority before file changes, then commits an owned revision and command while holding campaign and timeline.");

            var rollback = WriteWithCommitWitness(RollbackPacket(latest, revision), rollback: true);
            var afterRollback = CharactersStore.SelectCharacterEditorNativeCommands2(reader, TestDict("id", hero)).Single();
            add("native_editor_browser_rollback_checks_origin_before_superseding_pending_command",
                ReadBool(rollback, "ok", false) && scopesHeld && boundaries == 4 && State() == "before" && Name() == "Before"
                && ReadString(afterRollback, "status", "") == "superseded"
                && ReadString(afterRollback, "manual_origin_json", "") == ReadString(command, "manual_origin_json", ""),
                "An actual owned rollback supersedes the pending original only after admission and commits no inverse command.");

            var oldLoad = CharacterEditorLoadApi(target);
            var legacyPacket = SavePacket(oldLoad, "legacy_profile");
            ReadDictionary(legacyPacket, "documents")["profile"] = TestDict("heroStringId", hero, "name", "Legacy direct");
            var legacy = CharacterEditorSaveApi(legacyPacket);
            var unownedRollbackBasis = Open();
            var legacyRollback = WriteEditorBrowser(RollbackPacket(unownedRollbackBasis, ReadString(legacy, "revisionId", "")),
                ownedNative: true, rollback: true);
            add("native_editor_browser_rollback_holds_legacy_native_profile_without_a_command",
                ReadBool(legacy, "ok", false) && !ReadBool(legacyRollback, "ok", false)
                && State() == "legacy_profile" && Name() == "Legacy direct",
                "A historic raw profile revision with no native command cannot be rolled back as if it were server-only.");
        }
#endif
    }
}
