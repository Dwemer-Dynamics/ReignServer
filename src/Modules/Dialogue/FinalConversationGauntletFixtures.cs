using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ReignBetaServer
{
    internal sealed class FinalGauntletFixtureV3
    {
        public string Schema = "reign-final-gauntlet-fixture-v3";
        public string CaseId = string.Empty;
        public string Family = string.Empty;
        public string FactoryKind = string.Empty;
        public int RandomSeed = 0;
        public string StateFingerprint = string.Empty;
        public Dictionary<string, object> CampaignState =
            new Dictionary<string, object>();
        public Dictionary<string, object> Speaker =
            new Dictionary<string, object>();
        public Dictionary<string, object> Player =
            new Dictionary<string, object>();
        public Dictionary<string, object> RetrievedContext =
            new Dictionary<string, object>();
        public Dictionary<string, object> Input =
            new Dictionary<string, object>();
        public Dictionary<string, object> Expected =
            new Dictionary<string, object>();
        public List<Dictionary<string, object>> Mutations =
            new List<Dictionary<string, object>>();
        public Dictionary<string, object> Restoration =
            new Dictionary<string, object>();
    }

    internal sealed class FinalGauntletRestorationResult
    {
        public bool Ok = false;
        public string Error = string.Empty;
        public Dictionary<string, object> State =
            new Dictionary<string, object>();
    }

    internal sealed class FinalGauntletPrecedenceResult
    {
        public string Source = string.Empty;
        public string Value = string.Empty;
        public List<string> RejectedSources = new List<string>();
    }

    internal static class FinalConversationGauntletFixtures
    {
        private static readonly JavaScriptSerializer FixtureJson =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal static FinalGauntletFixtureV3 Materialize(
            FinalGauntletCaseDescriptor descriptor,
            int randomSeed,
            Dictionary<string, object> source)
        {
            if (descriptor == null)
                throw new ArgumentNullException(nameof(descriptor));
            source = Clone(source ?? new Dictionary<string, object>());
            FinalGauntletFixtureV3 fixture = new FinalGauntletFixtureV3
            {
                CaseId = descriptor.CaseId,
                Family = descriptor.Family,
                FactoryKind = ResolveFactoryKind(descriptor),
                RandomSeed = randomSeed,
                CampaignState = Section(source, "campaignState"),
                Speaker = Section(source, "speaker"),
                Player = Section(source, "player"),
                RetrievedContext = Section(source, "retrievedContext"),
                Input = Section(source, "input"),
                Expected = Section(source, "expected"),
                Mutations = DictionaryList(source, "mutations"),
                Restoration = Section(source, "restoration")
            };
            fixture.Expected["behavioralRequirement"] =
                descriptor.BehavioralRequirement;
            fixture.Expected["hardProhibitions"] =
                descriptor.HardProhibitions.Cast<object>().ToList();
            fixture.Expected["evidenceNeeds"] =
                descriptor.EvidenceNeeds.Cast<object>().ToList();
            fixture.StateFingerprint = Fingerprint(FingerprintPayload(fixture));
            return fixture;
        }

        internal static List<string> Validate(object value)
        {
            FinalGauntletFixtureV3 fixture = value as FinalGauntletFixtureV3;
            List<string> errors = new List<string>();
            if (fixture == null)
            {
                errors.Add("Fixture is missing or has the wrong schema type.");
                return errors;
            }
            if (fixture.Schema != "reign-final-gauntlet-fixture-v3")
                errors.Add("Fixture schema must be reign-final-gauntlet-fixture-v3.");
            if (string.IsNullOrWhiteSpace(fixture.CaseId))
                errors.Add("caseId is required.");
            RequireSection(errors, fixture.CampaignState, "campaignState");
            RequireSection(errors, fixture.Speaker, "speaker");
            RequireSection(errors, fixture.Player, "player");
            RequireSection(errors, fixture.RetrievedContext, "retrievedContext");
            RequireSection(errors, fixture.Input, "input");
            RequireSection(errors, fixture.Expected, "expected");
            RequireSection(errors, fixture.Restoration, "restoration");

            string speakerId = Text(fixture.Speaker, "heroId");
            string playerId = Text(fixture.Player, "heroId");
            if (string.IsNullOrWhiteSpace(speakerId)
                || string.IsNullOrWhiteSpace(playerId)
                || speakerId.Equals(playerId, StringComparison.OrdinalIgnoreCase))
                errors.Add("Speaker and player require distinct canonical hero IDs.");
            if (Number(fixture.Speaker, "age", -1) < 18
                || !Boolean(fixture.Speaker, "isAlive", false))
                errors.Add("The speaking NPC must be an adult living character.");
            if (Number(fixture.Player, "age", -1) < 18
                || !Boolean(fixture.Player, "isAlive", false))
                errors.Add("The player must be an adult living character.");

            foreach (Dictionary<string, object> memory in
                DictionaryList(fixture.RetrievedContext, "memories"))
            {
                string owner = Text(memory, "ownerHeroId");
                HashSet<string> knownBy = new HashSet<string>(
                    StringList(memory, "knownBy"),
                    StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(owner)
                    && !owner.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                    && !knownBy.Contains(speakerId))
                    errors.Add(
                        "Retrieved knowledge is not authorized for the speaking NPC: "
                        + Text(memory, "sourceId") + ".");
            }
            foreach (Dictionary<string, object> mutation in fixture.Mutations)
            {
                if (!Text(mutation, "owner")
                    .Equals("synthetic_test_evidence", StringComparison.Ordinal))
                    errors.Add("Fixture mutations require synthetic_test_evidence ownership.");
            }
            string recomputed = Fingerprint(FingerprintPayload(fixture));
            if (!recomputed.Equals(
                fixture.StateFingerprint,
                StringComparison.OrdinalIgnoreCase))
                errors.Add("Fixture state fingerprint does not match its canonical payload.");
            return errors;
        }

        internal static FinalGauntletRestorationResult Restore(
            Dictionary<string, object> currentState,
            Dictionary<string, object> restoration,
            string campaignId,
            string timelineId,
            string gameInstanceId)
        {
            FinalGauntletRestorationResult result =
                new FinalGauntletRestorationResult
                {
                    State = Clone(currentState ?? new Dictionary<string, object>())
                };
            restoration = restoration ?? new Dictionary<string, object>();
            if (!ContextEquals(restoration, "campaignId", campaignId)
                || !ContextEquals(restoration, "timelineId", timelineId)
                || !ContextEquals(restoration, "gameInstanceId", gameInstanceId))
            {
                result.Error =
                    "Restoration context does not match campaign, timeline, or game instance.";
                return result;
            }
            foreach (Dictionary<string, object> mutation in
                DictionaryList(restoration, "mutations"))
            {
                string path = Text(mutation, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    result.Error = "Restoration mutation path is required.";
                    return result;
                }
                object before = mutation.TryGetValue("before", out object prior)
                    ? prior
                    : null;
                object after = mutation.TryGetValue("after", out object next)
                    ? next
                    : null;
                object current = PathValue(result.State, path);
                if (Canonical(current) == Canonical(before))
                    continue;
                if (Canonical(current) != Canonical(after))
                {
                    result.Error =
                        "Restoration refused unexpected current value at " + path + ".";
                    return result;
                }
                SetPathValue(result.State, path, CloneValue(before));
            }
            result.Ok = true;
            return result;
        }

        internal static List<FinalGauntletFixtureV3> MaterializeFactory(
            IEnumerable<FinalGauntletCaseDescriptor> descriptors,
            int baseSeed,
            Func<FinalGauntletCaseDescriptor, Dictionary<string, object>> stateFactory)
        {
            List<FinalGauntletFixtureV3> fixtures =
                new List<FinalGauntletFixtureV3>();
            int index = 0;
            foreach (FinalGauntletCaseDescriptor descriptor in
                descriptors ?? Array.Empty<FinalGauntletCaseDescriptor>())
                fixtures.Add(Materialize(
                    descriptor,
                    unchecked(baseSeed + index++ * 7919),
                    stateFactory?.Invoke(descriptor)
                        ?? new Dictionary<string, object>()));
            return fixtures;
        }

        internal static List<Dictionary<string, object>>
            BuildSyntheticHighVolumeHistory(
                string ownerHeroId,
                string sourceSessionId,
                int count)
        {
            List<Dictionary<string, object>> records =
                new List<Dictionary<string, object>>();
            for (int index = 0; index < Math.Max(0, count); index++)
                records.Add(new Dictionary<string, object>
                {
                    ["memoryId"] = "synthetic-" + sourceSessionId + "-"
                        + index.ToString("00000"),
                    ["ownerHeroId"] = ownerHeroId,
                    ["knownBy"] = new List<object> { ownerHeroId },
                    ["sourceSessionId"] = sourceSessionId,
                    ["sourceExchange"] = index / 2,
                    ["sourceTurn"] = index,
                    ["owner"] = "synthetic_test_evidence",
                    ["text"] = "Synthetic retrieval-pressure record " + index + "."
                });
            return records;
        }

        internal static FinalGauntletPrecedenceResult ResolveAuthoritativeValue(
            string key,
            Dictionary<string, object> authoritativeState,
            Dictionary<string, object> retrievedContext,
            Dictionary<string, object> playerClaims)
        {
            FinalGauntletPrecedenceResult result =
                new FinalGauntletPrecedenceResult();
            if (TryReadPrecedenceValue(authoritativeState, key, out string live))
            {
                result.Source = "authoritative_state";
                result.Value = live;
                if (TryReadPrecedenceValue(retrievedContext, key, out string memory)
                    && !memory.Equals(live, StringComparison.Ordinal))
                    result.RejectedSources.Add("retrieved_context");
                if (TryReadPrecedenceValue(playerClaims, key, out string claim)
                    && !claim.Equals(live, StringComparison.Ordinal))
                    result.RejectedSources.Add("player_claim");
                return result;
            }
            if (TryReadPrecedenceValue(retrievedContext, key, out string retrieved))
            {
                result.Source = "retrieved_context";
                result.Value = retrieved;
                if (TryReadPrecedenceValue(playerClaims, key, out string claim)
                    && !claim.Equals(retrieved, StringComparison.Ordinal))
                    result.RejectedSources.Add("player_claim");
                return result;
            }
            if (TryReadPrecedenceValue(playerClaims, key, out string claimed))
            {
                result.Source = "player_claim";
                result.Value = claimed;
                return result;
            }
            result.Source = "unknown";
            return result;
        }

        private static string ResolveFactoryKind(
            FinalGauntletCaseDescriptor descriptor)
        {
            switch (descriptor.EvaluationKind)
            {
                case "differential": return "paired_differential";
                case "statistical": return "stochastic_or_statistical_batch";
                case "sequence":
                    return descriptor.Family == "final_scene"
                        ? "final_scene"
                        : "multi_turn_or_long_horizon";
                default:
                    if (descriptor.Family == "action_conformance")
                        return "action_conformance";
                    return "atomic";
            }
        }

        private static bool TryReadPrecedenceValue(
            Dictionary<string, object> source,
            string key,
            out string value)
        {
            value = string.Empty;
            if (source == null
                || string.IsNullOrWhiteSpace(key)
                || !source.TryGetValue(key, out object raw)
                || raw == null)
                return false;
            value = Convert.ToString(raw, CultureInfo.InvariantCulture)
                ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static Dictionary<string, object> FingerprintPayload(
            FinalGauntletFixtureV3 fixture)
        {
            return new Dictionary<string, object>
            {
                ["schema"] = fixture.Schema,
                ["caseId"] = fixture.CaseId,
                ["family"] = fixture.Family,
                ["factoryKind"] = fixture.FactoryKind,
                ["randomSeed"] = fixture.RandomSeed,
                ["campaignState"] = fixture.CampaignState,
                ["speaker"] = fixture.Speaker,
                ["player"] = fixture.Player,
                ["retrievedContext"] = fixture.RetrievedContext,
                ["input"] = fixture.Input,
                ["expected"] = fixture.Expected,
                ["mutations"] = fixture.Mutations,
                ["restoration"] = fixture.Restoration
            };
        }

        private static void RequireSection(
            List<string> errors,
            Dictionary<string, object> section,
            string name)
        {
            if (section == null || section.Count == 0)
                errors.Add(name + " is required.");
        }

        private static Dictionary<string, object> Section(
            Dictionary<string, object> source,
            string key)
        {
            return source.TryGetValue(key, out object value)
                && value is Dictionary<string, object> dictionary
                ? Clone(dictionary)
                : new Dictionary<string, object>();
        }

        private static List<Dictionary<string, object>> DictionaryList(
            Dictionary<string, object> source,
            string key)
        {
            if (source == null || !source.TryGetValue(key, out object value))
                return new List<Dictionary<string, object>>();
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string)
                return new List<Dictionary<string, object>>();
            return enumerable.Cast<object>()
                .OfType<Dictionary<string, object>>()
                .Select(Clone)
                .ToList();
        }

        private static List<string> StringList(
            Dictionary<string, object> source,
            string key)
        {
            if (source == null || !source.TryGetValue(key, out object value)
                || !(value is IEnumerable enumerable) || value is string)
                return new List<string>();
            return enumerable.Cast<object>()
                .Select(Convert.ToString)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static Dictionary<string, object> Clone(
            Dictionary<string, object> value)
        {
            return FixtureJson.Deserialize<Dictionary<string, object>>(
                FixtureJson.Serialize(value ?? new Dictionary<string, object>()))
                ?? new Dictionary<string, object>();
        }

        private static object CloneValue(object value)
        {
            if (value == null) return null;
            return FixtureJson.DeserializeObject(FixtureJson.Serialize(value));
        }

        private static string Text(
            Dictionary<string, object> source,
            string key)
        {
            return source != null && source.TryGetValue(key, out object value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
        }

        private static int Number(
            Dictionary<string, object> source,
            string key,
            int fallback)
        {
            return source != null
                && source.TryGetValue(key, out object value)
                && int.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed)
                ? parsed
                : fallback;
        }

        private static bool Boolean(
            Dictionary<string, object> source,
            string key,
            bool fallback)
        {
            return source != null
                && source.TryGetValue(key, out object value)
                && bool.TryParse(Convert.ToString(value), out bool parsed)
                ? parsed
                : fallback;
        }

        private static bool ContextEquals(
            Dictionary<string, object> restoration,
            string key,
            string actual)
        {
            return Text(restoration, key)
                .Equals(actual ?? string.Empty, StringComparison.Ordinal);
        }

        private static object PathValue(
            Dictionary<string, object> state,
            string path)
        {
            object current = state;
            foreach (string segment in path.Split('.'))
            {
                if (!(current is Dictionary<string, object> dictionary)
                    || !dictionary.TryGetValue(segment, out current))
                    return null;
            }
            return current;
        }

        private static void SetPathValue(
            Dictionary<string, object> state,
            string path,
            object value)
        {
            string[] segments = path.Split('.');
            Dictionary<string, object> current = state;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                if (!current.TryGetValue(segments[index], out object nested)
                    || !(nested is Dictionary<string, object> dictionary))
                {
                    dictionary = new Dictionary<string, object>();
                    current[segments[index]] = dictionary;
                }
                current = dictionary;
            }
            current[segments[segments.Length - 1]] = value;
        }

        private static string Fingerprint(object value)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(Canonical(value)))
                    .Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static string Canonical(object value)
        {
            if (value == null) return "null";
            if (value is string text) return FixtureJson.Serialize(text);
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IDictionary dictionary)
            {
                SortedDictionary<string, object> sorted =
                    new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                    sorted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)
                        ?? string.Empty] = entry.Value;
                return "{" + string.Join(
                    ",",
                    sorted.Select(x =>
                        FixtureJson.Serialize(x.Key) + ":" + Canonical(x.Value)))
                    + "}";
            }
            if (value is IEnumerable enumerable)
                return "[" + string.Join(
                    ",",
                    enumerable.Cast<object>().Select(Canonical)) + "]";
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            return FixtureJson.Serialize(value);
        }
    }
}
