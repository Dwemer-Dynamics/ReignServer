using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared
{
    public static class CorrespondenceContactOrdering
    {
        public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> contacts,
            Func<T, int> unread, Func<T, bool> hasHistory, Func<T, string> name, Func<T, string> id)
            => contacts.OrderBy(contact => unread(contact) > 0 ? 0 : hasHistory(contact) ? 1 : 2)
                .ThenBy(name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id, StringComparer.OrdinalIgnoreCase);
    }
}
