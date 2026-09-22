using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static class FamilyChambersSelfTests
    {
        internal static void Append(List<Dictionary<string, object>> results)
        {
            Program.AppendFamilyVisitSelfTests(results);
            Add(results, "family_chambers_participant_cap",
                Program.FamilySceneParticipantCountValid(1) && Program.FamilySceneParticipantCountValid(4)
                && !Program.FamilySceneParticipantCountValid(0) && !Program.FamilySceneParticipantCountValid(5),
                "Family scenes accept one through four NPC participants and reject every value outside that cap.");
            Add(results, "family_chambers_childhood_age_boundary",
                !Program.FamilyChildhoodRetainedAtAdulthood(4.999d)
                && Program.FamilyChildhoodRetainedAtAdulthood(5d)
                && Program.FamilyChildhoodRetainedAtAdulthood(17.9d),
                "Children may recall all minor experiences, while adulthood migration retains only experiences from exact age five onward.");
            string imageContract = Program.FamilyImageSafetyContract(2, "Evening");
            Add(results, "family_chambers_image_composition_contract",
                imageContract.Contains("Show exactly two human figures total")
                && imageContract.Contains("no background figures")
                && imageContract.Contains("no duplicate people")
                && imageContract.Contains("Do not render any words")
                && imageContract.Contains("only face in that person's zone")
                && !imageContract.Contains("16:9")
                && !imageContract.Contains("Participant"),
                "Family scene prompts deterministically prohibit extra figures, duplication, rendered metadata, and ambiguous fixed participant slots.");
        }

        private static void Add(List<Dictionary<string, object>> results, string id, bool passed, string summary)
        {
            results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "court_system", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["data"] = new Dictionary<string, object>(), ["durationMs"] = 0
            });
        }
    }
}
