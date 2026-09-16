using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal enum FinalGauntletStage
    {
        BoundedQualification,
        ExhaustiveReview
    }

    internal enum FinalGauntletCaseState
    {
        Planned,
        Queued,
        Running,
        Passed,
        Failed,
        Skipped,
        NotApplicable,
        ProviderExhausted,
        Cancelled,
        Interrupted
    }

    internal enum FinalGauntletCoverageKind
    {
        Deterministic,
        RepresentedLive,
        DedicatedLive,
        LongHorizon,
        FinalScene
    }

    internal sealed class FinalGauntletCaseDescriptor
    {
        public string CaseId = string.Empty;
        public string Family = string.Empty;
        public string EvaluationKind = string.Empty;
        public string ExecutionKind = string.Empty;
        public string Mode = string.Empty;
        public bool RequiresProvider = false;
        public bool RequiresGame = false;
        public string[] RequirementIds = Array.Empty<string>();
        public string[] Tags = Array.Empty<string>();
        public string BehavioralRequirement = string.Empty;
        public string[] HardProhibitions = Array.Empty<string>();
        public string[] PrerequisiteCapabilities = Array.Empty<string>();
        public string[] EvidenceNeeds = Array.Empty<string>();
        public string CoverageKind = string.Empty;
        public int EstimatedProviderCalls = 0;
        public string[] RepresentedRequirementIds = Array.Empty<string>();
        public int RiskWeight = 0;
    }

    internal static class FinalGauntletContracts
    {
        internal const int CurrentFixtureSchemaVersion = 1;

        internal static List<string> ValidateDescriptors(
            IEnumerable<FinalGauntletCaseDescriptor> descriptors,
            int fixtureSchemaVersion)
        {
            List<string> errors = new List<string>();
            if (fixtureSchemaVersion != CurrentFixtureSchemaVersion)
            {
                errors.Add(
                    "Unsupported fixture schema version "
                    + fixtureSchemaVersion
                    + "; expected "
                    + CurrentFixtureSchemaVersion
                    + ".");
                return errors;
            }

            HashSet<string> caseIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (FinalGauntletCaseDescriptor descriptor in
                descriptors ?? Array.Empty<FinalGauntletCaseDescriptor>())
            {
                string prefix = "Descriptor " + index + ": ";
                if (descriptor == null)
                {
                    errors.Add(prefix + "value is null.");
                    index++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(descriptor.CaseId))
                    errors.Add(prefix + "caseId is required.");
                else if (!caseIds.Add(descriptor.CaseId.Trim()))
                    errors.Add(prefix + "caseId is duplicated: " + descriptor.CaseId + ".");
                if (string.IsNullOrWhiteSpace(descriptor.Family))
                    errors.Add(prefix + "family is required.");
                if (string.IsNullOrWhiteSpace(descriptor.EvaluationKind))
                    errors.Add(prefix + "evaluationKind is required.");
                if (string.IsNullOrWhiteSpace(descriptor.ExecutionKind))
                    errors.Add(prefix + "executionKind is required.");
                if (string.IsNullOrWhiteSpace(descriptor.Mode))
                    errors.Add(prefix + "mode is required.");
                if (descriptor.RequirementIds == null
                    || descriptor.RequirementIds.Length == 0)
                    errors.Add(prefix + "at least one requirementId is required.");
                if (string.IsNullOrWhiteSpace(descriptor.BehavioralRequirement))
                    errors.Add(prefix + "behavioralRequirement is required.");
                if (descriptor.HardProhibitions == null
                    || descriptor.HardProhibitions.Length == 0)
                    errors.Add(prefix + "at least one hardProhibition is required.");
                if (descriptor.PrerequisiteCapabilities == null
                    || descriptor.PrerequisiteCapabilities.Length == 0)
                    errors.Add(prefix + "at least one prerequisiteCapability is required.");
                if (descriptor.EvidenceNeeds == null
                    || descriptor.EvidenceNeeds.Length == 0)
                    errors.Add(prefix + "at least one evidenceNeed is required.");
                index++;
            }
            if (index == 0)
                errors.Add("At least one case descriptor is required.");
            return errors;
        }
    }

    internal static class FinalGauntletStateRules
    {
        internal static bool IsTerminal(FinalGauntletCaseState state)
        {
            switch (state)
            {
                case FinalGauntletCaseState.Passed:
                case FinalGauntletCaseState.Failed:
                case FinalGauntletCaseState.Skipped:
                case FinalGauntletCaseState.NotApplicable:
                case FinalGauntletCaseState.ProviderExhausted:
                case FinalGauntletCaseState.Cancelled:
                case FinalGauntletCaseState.Interrupted:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool CanTransition(
            FinalGauntletCaseState current,
            FinalGauntletCaseState next)
        {
            if (current == next) return true;
            if (IsTerminal(current)) return false;
            switch (current)
            {
                case FinalGauntletCaseState.Planned:
                    return next == FinalGauntletCaseState.Queued
                        || next == FinalGauntletCaseState.Cancelled;
                case FinalGauntletCaseState.Queued:
                    return next == FinalGauntletCaseState.Running
                        || next == FinalGauntletCaseState.Cancelled
                        || next == FinalGauntletCaseState.Interrupted;
                case FinalGauntletCaseState.Running:
                    return IsTerminal(next);
                default:
                    return false;
            }
        }

        internal static bool IsAttemptNumberValid(int attemptNumber)
        {
            return attemptNumber >= 1 && attemptNumber <= 3;
        }
    }
}
