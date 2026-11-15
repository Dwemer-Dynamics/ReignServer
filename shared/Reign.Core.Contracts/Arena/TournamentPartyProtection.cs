using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Arena
{
    public static class TournamentPartyProtection
    {
        // Hero identity is unique; ordinary troop types are shared by many parties.
        // Do not exclude an unrelated soldier because the player owns that troop type.
        public static List<T> Filter<T>(IEnumerable<T> entrants, bool playerCompetes,
            Func<T, string?> heroId, string playerId, IEnumerable<string> currentPartyHeroes)
        {
            var party = new HashSet<string>(currentPartyHeroes, StringComparer.Ordinal);
            return entrants.Where(entry => !playerCompetes || heroId(entry) is not string id
                || id.Length == 0 || id == playerId || !party.Contains(id)).ToList();
        }
    }
}
