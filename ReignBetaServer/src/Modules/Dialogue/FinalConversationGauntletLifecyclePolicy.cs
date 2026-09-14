using System;
using System.Linq;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletLifecyclePolicy
    {
        private static readonly string[] Rotating =
        {
            "ConvTest_Gauntlet_A",
            "ConvTest_Gauntlet_B"
        };

        private static readonly string[] Protected =
        {
            "ConvTest", "BaseOne", "BaseTwo", "BaseThree"
        };

        public static string[] RotatingSaveNames()
        {
            return Rotating.ToArray();
        }

        public static bool IsProtectedSave(string saveName)
        {
            return Protected.Contains(
                saveName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
