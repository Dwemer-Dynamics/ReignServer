using System;

namespace Reign.Core.Contracts.Court
{
    public static class ReignNobleVisitorRules
    {
        public const int MaximumVisitors = 4;
        public static bool CanInitiatePrivateRomance(bool rulerMarried, int boldness, int honor)
            => !rulerMarried || (Math.Max(0, Math.Min(100, boldness)) >= 61 && Math.Max(0, Math.Min(100, honor)) <= 40);
        public static bool Eligible(bool noble, bool alive, bool active, bool prisoner, float age, float adultAge,
            bool atWar, bool hasParty, bool governor, bool officeHolder, bool assigned, bool questInvolved, bool ruler)
            => noble && alive && active && !prisoner && age >= adultAge && !atWar && !hasParty
                && !governor && !officeHolder && !assigned && !questInvolved && !ruler;
        public static bool ValidFamilyGroup(int parents, int children) => parents >= 1 && parents <= 2
            && children >= 1 && children <= 2 && parents + children <= MaximumVisitors;
        public static int StayDays(int stableRoll) => 3 + (int)((uint)stableRoll % 5);
        public static string Purpose(int stableRoll)
        {
            switch ((uint)stableRoll % 6)
            {
                case 0: return "Pay a respectful courtesy call while breaking a journey.";
                case 1: return "Visit local business contacts and acknowledge the ruler while staying in the city.";
                case 2: return "Visit relatives in the settlement and offer ordinary courtly greetings.";
                case 3: return "Observe local training or entertainment and enjoy a few days at court.";
                case 4: return "Meet household contacts concerning private estate business that needs no royal intervention.";
                default: return "Enjoy the city's hospitality and make a respectful personal introduction.";
            }
        }
    }
}
