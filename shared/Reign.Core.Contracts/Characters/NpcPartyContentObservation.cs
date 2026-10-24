using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBeta.Shared.Characters
{
    public sealed class NpcPartyRosterEntry
    {
        public string CharacterId { get; set; } = "";
        public int Number { get; set; }
        public int Wounded { get; set; }
        public int Xp { get; set; }
    }
    public sealed class NpcPartyItemEntry
    {
        public string ItemId { get; set; } = "";
        public string ModifierId { get; set; } = "";
        public int Amount { get; set; }
    }
    public sealed class NpcPartyContentDigest
    {
        public string PartyId { get; set; } = "";
        public string MembersSha256 { get; set; } = "";
        public string PrisonersSha256 { get; set; } = "";
        public string InventorySha256 { get; set; } = "";
        public int MemberRows { get; set; }
        public int PrisonerRows { get; set; }
        public int InventoryRows { get; set; }
        public long MemberCount { get; set; }
        public long PrisonerCount { get; set; }
        public long ItemCount { get; set; }
    }
    /// <summary>Bounded previews with canonical digests over every observed row, including rows beyond the preview.</summary>
    public sealed class NpcPartyContentObservation
    {
        public const int PreviewLimit = 64;
        public string Schema { get; set; } = "reign-npc-party-content-observation-v1";
        public NpcPartyContentDigest Digest { get; set; } = new NpcPartyContentDigest();
        public List<NpcPartyRosterEntry> Members { get; set; } = new List<NpcPartyRosterEntry>();
        public List<NpcPartyRosterEntry> Prisoners { get; set; } = new List<NpcPartyRosterEntry>();
        public List<NpcPartyItemEntry> Inventory { get; set; } = new List<NpcPartyItemEntry>();
        public bool Truncated { get; set; }

        public static NpcPartyContentObservation Capture(string partyId, IEnumerable<NpcPartyRosterEntry> members,
            IEnumerable<NpcPartyRosterEntry> prisoners, IEnumerable<NpcPartyItemEntry> inventory)
        {
            var m = SortRoster(members); var p = SortRoster(prisoners);
            var items = inventory.OrderBy(r => r.ItemId, StringComparer.Ordinal).ThenBy(r => r.ModifierId, StringComparer.Ordinal)
                .ThenBy(r => r.Amount).ToList();
            return new NpcPartyContentObservation
            {
                Digest = new NpcPartyContentDigest
                {
                    PartyId = partyId, MembersSha256 = RosterHash("members", m), PrisonersSha256 = RosterHash("prisoners", p),
                    InventorySha256 = Hash("inventory", items.Count, writer =>
                    { foreach (var r in items) { writer.Write(r.ItemId); writer.Write(r.ModifierId); writer.Write(r.Amount); } }),
                    MemberRows = m.Count, PrisonerRows = p.Count, InventoryRows = items.Count,
                    MemberCount = m.Sum(r => (long)r.Number), PrisonerCount = p.Sum(r => (long)r.Number), ItemCount = items.Sum(r => (long)r.Amount)
                },
                Members = m.Take(PreviewLimit).ToList(), Prisoners = p.Take(PreviewLimit).ToList(), Inventory = items.Take(PreviewLimit).ToList(),
                Truncated = m.Count > PreviewLimit || p.Count > PreviewLimit || items.Count > PreviewLimit
            };
        }
        private static List<NpcPartyRosterEntry> SortRoster(IEnumerable<NpcPartyRosterEntry> rows) => rows
            .OrderBy(r => r.CharacterId, StringComparer.Ordinal).ThenBy(r => r.Number).ThenBy(r => r.Wounded).ThenBy(r => r.Xp).ToList();
        private static string RosterHash(string kind, List<NpcPartyRosterEntry> rows) => Hash(kind, rows.Count, writer =>
        { foreach (var r in rows) { writer.Write(r.CharacterId); writer.Write(r.Number); writer.Write(r.Wounded); writer.Write(r.Xp); } });
        private static string Hash(string kind, int count, Action<BinaryWriter> write)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write("reign-npc-party-content-v1"); writer.Write(kind); writer.Write(count); write(writer); }
                bytes.Position = 0;
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
