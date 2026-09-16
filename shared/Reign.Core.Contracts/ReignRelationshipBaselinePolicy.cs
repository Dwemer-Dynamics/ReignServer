namespace ReignBeta.Shared
{
    public static class ReignRelationshipBaselinePolicy
    {
        public const int NoBaseline = int.MinValue;
        public const int SpouseBaseline = 50;
        public const int ImmediateFamilyBaseline = 20;
        public const int LordToRulerBaseline = 10;

        public static int ResolveStartingBaseline(
            bool areSpouses,
            bool areImmediateFamily,
            bool isLordRulerPair)
        {
            if (areSpouses) return SpouseBaseline;
            if (areImmediateFamily) return ImmediateFamilyBaseline;
            if (isLordRulerPair) return LordToRulerBaseline;
            return NoBaseline;
        }
    }
}
