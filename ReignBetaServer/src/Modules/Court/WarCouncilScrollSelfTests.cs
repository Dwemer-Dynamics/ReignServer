using System;
using System.Collections.Generic;
using ReignBeta.Shared.WarCouncil;

namespace ReignBetaServer
{
    internal static class WarCouncilScrollSelfTests
    {
        internal static void Append(List<Dictionary<string, object>> results)
        {
            Action<string, bool, string, object> add = (id, passed, summary, data) =>
                results.Add(new Dictionary<string, object>
                {
                    { "ok", true }, { "passed", passed }, { "suite", "court_system" },
                    { "caseId", id }, { "name", id }, { "summary", summary },
                    { "data", data ?? new Dictionary<string, object>() }, { "durationMs", 0 }
                });

            const double maximum = 960d;
            double downOne = ReignWarCouncilScrollRules.StepWholeRow(0d, -120d, maximum);
            double upOne = ReignWarCouncilScrollRules.StepWholeRow(160d, 120d, maximum);
            double partialDown = ReignWarCouncilScrollRules.StepWholeRow(120d, -1d, maximum);
            double partialUp = ReignWarCouncilScrollRules.StepWholeRow(120d, 1d, maximum);
            add("war_council_scroll_one_notch",
                downOne == 160d && upOne == 0d && partialDown == 160d && partialUp == 0d,
                "Every native or fallback wheel notch advances to exactly one adjacent 160-pixel roster boundary.",
                new Dictionary<string, object>
                {
                    { "downOne", downOne }, { "upOne", upOne },
                    { "partialDown", partialDown }, { "partialUp", partialUp }
                });

            double repeated = 0d;
            bool repeatedOnBoundaries = true;
            for (int i = 0; i < 20; i++)
            {
                repeated = ReignWarCouncilScrollRules.StepWholeRow(repeated, -120d, maximum);
                repeatedOnBoundaries &= repeated % ReignWarCouncilScrollRules.LordRowStride == 0d;
            }
            double bottom = repeated;
            for (int i = 0; i < 20; i++)
            {
                repeated = ReignWarCouncilScrollRules.StepWholeRow(repeated, 120d, maximum);
                repeatedOnBoundaries &= repeated % ReignWarCouncilScrollRules.LordRowStride == 0d;
            }
            add("war_council_scroll_repeated_and_clamped",
                repeatedOnBoundaries && bottom == maximum && repeated == 0d
                    && ReignWarCouncilScrollRules.SnapToWholeRow(-40d, maximum) == 0d
                    && ReignWarCouncilScrollRules.SnapToWholeRow(10000d, maximum) == maximum
                    && ReignWarCouncilScrollRules.MaximumWholeRowOffset(1015d) == maximum,
                "Repeated scrolling remains on whole rows and clamps at the first and last complete roster rows.",
                new Dictionary<string, object> { { "bottom", bottom }, { "top", repeated } });

            double selectedTop = ReignWarCouncilScrollRules.SelectedRowOffset(0, maximum);
            double selectedMiddle = ReignWarCouncilScrollRules.SelectedRowOffset(6, maximum);
            double selectedBottom = ReignWarCouncilScrollRules.SelectedRowOffset(99, maximum);
            add("war_council_scroll_selected_autosnap",
                ReignWarCouncilScrollRules.LordViewportHeight == 640d
                    && ReignWarCouncilScrollRules.LordVisibleRowCount == 4
                    && selectedTop == 0d && selectedMiddle == 640d && selectedBottom == maximum
                    && selectedMiddle % ReignWarCouncilScrollRules.LordRowStride == 0d,
                "Selected-row autoscroll targets a complete four-row viewport and always settles on a 160-pixel boundary.",
                new Dictionary<string, object>
                {
                    { "selectedTop", selectedTop },
                    { "selectedMiddle", selectedMiddle },
                    { "selectedBottom", selectedBottom }
                });
        }
    }
}
