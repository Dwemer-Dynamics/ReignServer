using System;

namespace Reign.Core.Contracts.Court
{
    public static class ReignCourtAudienceArtRules
    {
        public const string RenderContract = "court_audience_all_cast_host_hall_v6";

        public const string PromptContract =
            "Treat every identity portrait as a face and identity reference, not as permission to copy its crop, missing garments, exposed body, or camera distance. "
            + "Every courtier must wear complete, modest, culturally plausible formal court clothing appropriate to their rank. No bare chest, exposed torso, underwear, nightwear, nudity, or missing garments. "
            + "Arrange all courtiers in one shallow group at a consistent apparent scale. No person may stand in a near foreground or appear substantially larger than another. "
            + "Show every person completely from the top of the hair to the soles of both feet, with clear floor visible below and around the feet and clear space above the head. "
            + "No head, shoulder, hand, garment, leg, or foot may touch or cross any image edge. Do not use a waist-up, bust, close-up, or cut-off composition for any participant. "
            + "If a reference portrait is cropped or underdressed, reconstruct the missing full body and formal clothing while preserving the face, age, build, coloring, and recognizable identity.";

        public static bool HasRequiredConstraints(string prompt)
        {
            string value = prompt ?? string.Empty;
            return value.IndexOf("top of the hair to the soles of both feet", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("clear floor visible below and around the feet", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("No person may stand in a near foreground", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("complete, modest, culturally plausible formal court clothing", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("No bare chest", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("reference portrait is cropped or underdressed", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
