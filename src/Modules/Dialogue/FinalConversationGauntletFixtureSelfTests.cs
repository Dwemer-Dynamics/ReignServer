using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletFixtureSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type fixtures = typeof(Program).Assembly.GetType(
                "ReignBetaServer.FinalConversationGauntletFixtures",
                false);
            MethodInfo materialize = fixtures?.GetMethod(
                "Materialize",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo validate = fixtures?.GetMethod(
                "Validate",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo restore = fixtures?.GetMethod(
                "Restore",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo resolvePrecedence = fixtures?.GetMethod(
                "ResolveAuthoritativeValue",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo highVolume = fixtures?.GetMethod(
                "BuildSyntheticHighVolumeHistory",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            object fixture = null;
            if (materialize != null)
            {
                FinalGauntletCaseDescriptor descriptor =
                    FinalConversationGauntletCatalog.BuildApprovedCatalog()
                        .First(x => x.CaseId == "IDN-008");
                fixture = materialize.Invoke(
                    null,
                    new object[]
                    {
                        descriptor,
                        1042,
                        BuildFixtureTestState()
                    });
            }
            List<string> errors = validate?.Invoke(null, new[] { fixture })
                as List<string>
                ?? new List<string> { "validator unavailable" };
            add(
                "fixture_v3_materializes_deterministically",
                fixture != null
                    && ReadMemberString(fixture, "Schema") == "reign-final-gauntlet-fixture-v3"
                    && ReadMemberInt(fixture, "RandomSeed") == 1042
                    && ReadMemberString(fixture, "StateFingerprint").Length == 64
                    && errors.Count == 0,
                "Fixture v3 records every required section, its seed, and a stable state fingerprint.");

            object repeated = materialize?.Invoke(
                null,
                new object[]
                {
                    FinalConversationGauntletCatalog.BuildApprovedCatalog()
                        .First(x => x.CaseId == "IDN-008"),
                    1042,
                    BuildFixtureTestState()
                });
            add(
                "fixture_materialization_is_replay_stable",
                fixture != null
                    && repeated != null
                    && ReadMemberString(fixture, "StateFingerprint")
                        == ReadMemberString(repeated, "StateFingerprint"),
                "The same descriptor, seed, and state produce the same fingerprint.");

            Dictionary<string, object> underageState = BuildFixtureTestState();
            ReadDictionary(underageState, "speaker")["age"] = 17;
            object underage = materialize?.Invoke(
                null,
                new object[]
                {
                    FinalConversationGauntletCatalog.BuildApprovedCatalog()
                        .First(x => x.CaseId == "ROM-015"),
                    7,
                    underageState
                });
            List<string> underageErrors = validate?.Invoke(null, new[] { underage })
                as List<string>
                ?? new List<string>();
            add(
                "adult_and_identity_boundaries_fail_closed",
                underageErrors.Any(x => x.IndexOf("adult", StringComparison.OrdinalIgnoreCase) >= 0),
                "Ordinary and romantic fixtures reject underage speakers before provider use.");

            Dictionary<string, object> leakedState = BuildFixtureTestState();
            ReadDictionary(leakedState, "retrievedContext")["memories"] =
                new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["ownerHeroId"] = "other_hero",
                        ["knownBy"] = new List<object> { "other_hero" },
                        ["sourceId"] = "secret-1"
                    }
                };
            object leaked = materialize?.Invoke(
                null,
                new object[]
                {
                    FinalConversationGauntletCatalog.BuildApprovedCatalog()
                        .First(x => x.CaseId == "MEM-033"),
                    8,
                    leakedState
                });
            List<string> leakedErrors = validate?.Invoke(null, new[] { leaked })
                as List<string>
                ?? new List<string>();
            add(
                "memory_owner_and_witness_isolation_is_enforced",
                leakedErrors.Any(x => x.IndexOf("knowledge", StringComparison.OrdinalIgnoreCase) >= 0),
                "Retrieved private evidence must authorize the speaker through owner or known-by lineage.");

            object precedence = resolvePrecedence?.Invoke(
                null,
                new object[]
                {
                    "settlementOwner",
                    new Dictionary<string, object> { ["settlementOwner"] = "live_owner" },
                    new Dictionary<string, object> { ["settlementOwner"] = "stale_memory" },
                    new Dictionary<string, object> { ["settlementOwner"] = "player_claim" }
                });
            add(
                "authoritative_state_precedes_memory_and_claims",
                precedence != null
                    && ReadMemberString(precedence, "Source") == "authoritative_state"
                    && ReadMemberString(precedence, "Value") == "live_owner",
                "Live campaign state outranks stale memory and player claims with recorded precedence evidence.");

            List<Dictionary<string, object>> synthetic =
                highVolume?.Invoke(
                    null,
                    new object[] { "npc_same", "session-a", 1000 })
                as List<Dictionary<string, object>>
                ?? new List<Dictionary<string, object>>();
            add(
                "synthetic_history_has_exact_test_lineage",
                synthetic.Count == 1000
                    && synthetic.All(x =>
                        ReadString(x, "owner", "") == "synthetic_test_evidence"
                        && ReadString(x, "ownerHeroId", "") == "npc_same"
                        && ReadString(x, "sourceSessionId", "") == "session-a"),
                "High-volume history remains explicitly synthetic and carries exact owner/session/turn lineage.");

            Dictionary<string, object> current = new Dictionary<string, object>
            {
                ["gold"] = 50,
                ["title"] = "ruler"
            };
            Dictionary<string, object> restoration = new Dictionary<string, object>
            {
                ["campaignId"] = "campaign-a",
                ["timelineId"] = "main",
                ["gameInstanceId"] = "game-a",
                ["mutations"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["path"] = "gold",
                        ["before"] = 50,
                        ["after"] = 0,
                        ["owner"] = "synthetic_test_evidence"
                    }
                }
            };
            current["gold"] = 0;
            object restored = restore?.Invoke(
                null,
                new object[]
                {
                    current, restoration, "campaign-a", "main", "game-a"
                });
            Dictionary<string, object> restoredState =
                ReadFixtureMember<Dictionary<string, object>>(restored, "State");
            bool restoredOk = ReadMemberBool(restored, "Ok")
                && ReadInt(restoredState, "gold", -1) == 50;
            object secondRestore = restore?.Invoke(
                null,
                new object[]
                {
                    restoredState, restoration, "campaign-a", "main", "game-a"
                });
            object mismatch = restore?.Invoke(
                null,
                new object[]
                {
                    current, restoration, "other", "main", "game-a"
                });
            add(
                "restoration_is_exact_idempotent_and_context_bound",
                restoredOk
                    && ReadMemberBool(secondRestore, "Ok")
                    && ReadInt(
                        ReadFixtureMember<Dictionary<string, object>>(secondRestore, "State"),
                        "gold",
                        -1) == 50
                    && !ReadMemberBool(mismatch, "Ok"),
                "Restoration exactly replays before-values, is idempotent, and rejects stale campaign context.");
            return checks;
        }

        private static Dictionary<string, object> BuildFixtureTestState()
        {
            return new Dictionary<string, object>
            {
                ["campaignState"] = new Dictionary<string, object>
                {
                    ["campaignId"] = "campaign-a",
                    ["timelineId"] = "main",
                    ["gameInstanceId"] = "game-a",
                    ["worldDay"] = 12d,
                    ["settlementId"] = "town_a",
                    ["presentHeroIds"] = new List<object> { "npc_same", "player" }
                },
                ["speaker"] = new Dictionary<string, object>
                {
                    ["heroId"] = "npc_same",
                    ["name"] = "Lýcaron",
                    ["age"] = 30,
                    ["isAlive"] = true
                },
                ["player"] = new Dictionary<string, object>
                {
                    ["heroId"] = "player",
                    ["name"] = "Lýcaron",
                    ["age"] = 30,
                    ["isAlive"] = true
                },
                ["retrievedContext"] = new Dictionary<string, object>
                {
                    ["memories"] = new List<object>()
                },
                ["input"] = new Dictionary<string, object>
                {
                    ["playerMessage"] = "Good day."
                },
                ["expected"] = new Dictionary<string, object>
                {
                    ["hardRequirements"] = new List<object> { "identity_by_id" },
                    ["forbiddenContent"] = new List<object> { "identity_swap" }
                },
                ["mutations"] = new List<object>(),
                ["restoration"] = new Dictionary<string, object>
                {
                    ["campaignId"] = "campaign-a",
                    ["timelineId"] = "main",
                    ["gameInstanceId"] = "game-a",
                    ["mutations"] = new List<object>()
                }
            };
        }

        private static T ReadFixtureMember<T>(object instance, string name)
        {
            if (instance == null) return default(T);
            FieldInfo field = instance.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.GetValue(instance) is T value) return value;
            PropertyInfo property = instance.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetValue(instance) is T propertyValue)
                return propertyValue;
            return default(T);
        }

        private static string ReadMemberString(object instance, string name)
        {
            return ReadFixtureMember<string>(instance, name) ?? string.Empty;
        }

        private static int ReadMemberInt(object instance, string name)
        {
            return ReadFixtureMember<int>(instance, name);
        }

        private static bool ReadMemberBool(object instance, string name)
        {
            return ReadFixtureMember<bool>(instance, name);
        }
    }
}
