using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    // Public snapshots, retained independently of acknowledgement and the live government roster.
    public sealed class GovernmentNewsItem
    {
        public string ReceiptId = "", BusinessId = "", Stage = "", Text = "";
    }

    public sealed class GovernmentNewsSection
    {
        public string KingdomId = "", KingdomName = "", InstitutionName = "";
        public List<GovernmentNewsItem> Items = new List<GovernmentNewsItem>();
        public string Body => string.Join("\n\n", Items.Select(x => x.Text));
        public int PresentedCount => Items.Count(x => x.Stage == "presented");
        public int DecidedCount => Items.Count(x => x.Stage == "decided");
    }

    public sealed class GovernmentNewsEdition
    {
        public string Id = "", DateLabel = "", Headline = "Proceedings of the kingdoms";
        public double SessionDay, PublishedDay = -1;
        public List<GovernmentNewsSection> Kingdoms = new List<GovernmentNewsSection>();
        public string Body => string.Join("\n\n", Kingdoms.OrderBy(x => x.KingdomName, StringComparer.Ordinal)
            .Select(x => x.KingdomName + " — " + x.InstitutionName + "\n" + x.Body));
    }

    public sealed class GovernmentNewsLedger
    {
        public int Version = 1;
        public List<GovernmentNewsEdition> Pending = new List<GovernmentNewsEdition>();
        public List<GovernmentNewsEdition> Archive = new List<GovernmentNewsEdition>();
        public List<string> Receipts = new List<string>();

        public bool Record(double sessionDay, string dateLabel, string kingdomId, string kingdomName,
            string institutionName, GovernmentNewsItem item)
        {
            if (double.IsNaN(sessionDay) || double.IsInfinity(sessionDay) || sessionDay < 0
                || string.IsNullOrWhiteSpace(kingdomId) || item == null
                || string.IsNullOrWhiteSpace(item.ReceiptId) || string.IsNullOrWhiteSpace(item.Text)) return false;
            if (Receipts.Contains(item.ReceiptId))
            {
                var existing = Pending.SelectMany(e => e.Kingdoms).SelectMany(k => k.Items)
                    .FirstOrDefault(i => i.ReceiptId == item.ReceiptId);
                if (existing == null || existing.Text == item.Text) return false;
                existing.Text = item.Text; existing.Stage = item.Stage;
                return true;
            }
            // Days here are absolute REIGN calendar days, not native campaign timestamps.
            // A season may begin halfway through a native day. A delayed result is
            // reported on the day it became known, never retroactively inserted in an old edition.
            double season = GovernmentSessionRules.SeasonStart(sessionDay);
            double day = season + Math.Floor(sessionDay - season + 0.00001);
            string id = "government_news_" + day.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (Archive.Any(e => e.Id == id)) return false;
            var edition = Pending.FirstOrDefault(e => e.Id == id);
            if (edition == null)
            {
                edition = new GovernmentNewsEdition { Id = id, SessionDay = day, DateLabel = dateLabel ?? "" };
                Pending.Add(edition);
            }
            var section = edition.Kingdoms.FirstOrDefault(k => k.KingdomId == kingdomId);
            if (section == null)
            {
                section = new GovernmentNewsSection { KingdomId = kingdomId, KingdomName = kingdomName ?? kingdomId,
                    InstitutionName = institutionName ?? "Government" };
                edition.Kingdoms.Add(section);
            }
            section.Items.Add(item);
            Receipts.Add(item.ReceiptId);
            if (Receipts.Count > 16384) Receipts.RemoveRange(0, Receipts.Count - 16384);
            return true;
        }

        public IReadOnlyList<GovernmentNewsEdition> PublishDue(double day)
        {
            if (double.IsNaN(day) || double.IsInfinity(day)) return Array.Empty<GovernmentNewsEdition>();
            var due = Pending.Where(e => day >= GovernmentSessionRules.NextCalendarDay(e.SessionDay)).OrderBy(e => e.SessionDay).ToList();
            foreach (var edition in due)
            {
                edition.PublishedDay = day;
                Pending.Remove(edition);
                Archive.Add(edition);
            }
            if (Archive.Count > 128) Archive.RemoveRange(0, Archive.Count - 128);
            return due;
        }

        public void Normalize()
        {
            if (Version != 1) throw new InvalidOperationException("Unsupported government news save format.");
            Pending = Pending ?? new List<GovernmentNewsEdition>();
            Archive = Archive ?? new List<GovernmentNewsEdition>();
            Receipts = Receipts ?? new List<string>();
            foreach (var edition in Pending.Concat(Archive))
            {
                edition.Kingdoms = edition.Kingdoms ?? new List<GovernmentNewsSection>();
                foreach (var section in edition.Kingdoms) section.Items = section.Items ?? new List<GovernmentNewsItem>();
            }
        }

        public bool IsPublished(string receiptId)
            => Archive.SelectMany(e => e.Kingdoms).SelectMany(k => k.Items).Any(i => i.ReceiptId == receiptId);
    }
}
