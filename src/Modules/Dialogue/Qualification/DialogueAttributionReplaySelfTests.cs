using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunDialogueAttributionReplayAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool value) => rows.Add(TestDict("name", "attribution_replay_" + name, "passed", value));
            var item = TestDict("caseId", "ordering", "observerId", "mother", "peerId", "player", "query", "My scouts bring notice.",
                "worldDay", 10d, "asOfSequence", 2L, "participants", new[] { "mother", "daughter", "player" },
                "profiles", new[] { TestDict("id", "mother", "name", "Rhea"), TestDict("id", "daughter", "name", "Ira"), TestDict("id", "player", "name", "Michael") },
                "sources", new[] { TestDict("sourceId", "question", "speakerId", "daughter", "role", "npc", "text", "What does your army cost in a year?",
                    "sequence", 2L, "worldDay", 10d, "audience", new[] { "mother", "player" }) },
                "draft", TestDict("reply", "My daughter asked what the army costs in a year, and you answered with scouts."));
            var input = TestDict("schema", DialogueAttributionReplaySchema, "cases", new[] { item });
            Check("bounded_fixture", DialogueAttributionReplayError(input).Length == 0);
            var traced = TraceDialogueAttributionCase(item);
            Check("declared_group_reaches_native_group_resolver", GroupConversationParticipantIds(AttributionReplayPayload(item)).Count == 3);
            var claimPlan = ReadDictionary(traced, "claimPlan");
            Check("group_fixture_does_not_invent_private_addressee", ReadDictionaryList(claimPlan, "pairs").Count == 0
                && ReadDictionaryList(claimPlan, "claims").Exists(c => new[] { "actor_unresolved", "source_audience_unresolved" }.Contains(ReadString(c, "status", ""))));
            var future = CloneDictionary(input); ReadDictionaryList(ReadDictionaryList(future, "cases")[0], "sources")[0]["sequence"] = 3L;
            Check("future_source_rejected", DialogueAttributionReplayError(future) == "invalid_source");
            var foreign = CloneDictionary(input); ReadDictionaryList(foreign, "cases")[0]["campaignId"] = "live";
            Check("campaign_override_rejected", DialogueAttributionReplayError(foreign) == "invalid_case");
            var hidden = CloneDictionary(input); ReadDictionaryList(ReadDictionaryList(hidden, "cases")[0], "sources")[0]["audience"] = new[] { "player" };
            Check("unheard_source_rejected", DialogueAttributionReplayError(hidden) == "invalid_source");
            var injected = CloneDictionary(input); injected["path"] = "outside";
            Check("external_path_rejected", DialogueAttributionReplayError(injected) == "invalid_header");
            Check("empty_case_rejected", DialogueAttributionReplayError(TestDict("schema", DialogueAttributionReplaySchema, "cases", new List<Dictionary<string, object>>())) == "invalid_cases");
            var badCapture = CloneDictionary(input);
            ReadDictionaryList(badCapture, "cases")[0]["answerSupportCaptures"] = new[] { TestDict("requestHash", "not-a-hash", "response", TestDict()) };
            Check("unbound_answer_support_capture_rejected", DialogueAttributionReplayError(badCapture) == "invalid_answer_support_capture");
            var captureWithOverride = CloneDictionary(input);
            ReadDictionaryList(captureWithOverride, "cases")[0]["answerSupportCaptures"] = new[] { TestDict("requestHash", new string('a', 64), "response", TestDict(), "model", "foreign") };
            Check("answer_support_capture_cannot_override_model", DialogueAttributionReplayError(captureWithOverride) == "invalid_answer_support_capture");
            var late = CloneDictionary(input);
            var lateCase = ReadDictionaryList(late, "cases")[0];
            string badClaim = ReadString(ReadDictionary(lateCase, "draft"), "reply", "");
            lateCase["draft"] = TestDict("reply", "Welcome.");
            lateCase["lateRepairDraft"] = TestDict("reply", "Welcome. " + badClaim + " We can plan.",
                "continuityWrites", new[] { TestDict("meaning", badClaim), TestDict("meaning", "The grain is dry.") });
            Check("late_draft_bounded_data_is_valid", DialogueAttributionReplayError(late).Length == 0);
            var lateTrace = TraceDialogueAttributionCase(lateCase);
            var lateResult = ReadDictionary(lateTrace, "result");
            var final = TryParseJsonObject(ReadString(lateResult, "content", ""));
            Check("late_draft_audited_after_injection", ReadBool(lateTrace, "lateRepairApplied", false)
                && ReadInt(lateTrace, "sourceAuditsBeforeLateRepair", -1) == 0
                && ReadDictionaryList(lateTrace, "answerSupportRequests").Count == 1
                && ReadBool(lateResult, "ok", false) && !ReadString(final, "reply", "").Contains(badClaim)
                && ReadDictionaryList(final, "continuityWrites").Count == 1);
            Check("sealed_audit_survives_real_route_adapter", ReadDictionaryList(lateTrace, "answerSupportRequests").All(r =>
                ReadBool(r, "nativeReferenceSealed", false) && ReadBool(r, "routeAdapterPreservedRequest", false)));
            var malformedLate = CloneDictionary(late);
            ReadDictionaryList(malformedLate, "cases")[0]["lateRepairDraft"] = "not an object";
            Check("late_draft_must_be_object", DialogueAttributionReplayError(malformedLate) == "invalid_case");
            var injectedLateKnob = CloneDictionary(late);
            ReadDictionaryList(injectedLateKnob, "cases")[0]["lateRepairModel"] = "external";
            Check("late_draft_cannot_override_model", DialogueAttributionReplayError(injectedLateKnob) == "invalid_case");
            return rows;
        }
    }
}
