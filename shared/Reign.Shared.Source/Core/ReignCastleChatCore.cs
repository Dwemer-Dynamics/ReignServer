using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.CastleChat
{
    public enum CastleTimeBlock { Morning = 0, Afternoon = 1, Evening = 2, Night = 3 }

    public enum CastleRoom
    {
        CastleGardens = 0, NobleSolar = 1, Library = 2, TrainingYard = 3,
        InnerCourtyard = 4, DiningChamber = 5, StableCourtyard = 6,
        PortraitGallery = 7, MainHall = 8, Chapel = 9, ThroneRoom = 10,
        Baths = 11, Battlements = 12, GuestBedrooms = 13, RoyalBedroom = 14
    }

    public sealed class CastleScheduleCandidate
    {
        public string HeroId = string.Empty;
        public bool IsFemale;
        public bool IsLord;
        public bool IsAmbassador;
        public bool IsPartyMember;
        public bool IsOfficeHolder;
        public bool IsClanLeader;
        public bool IsSpouse;
        public bool IsActiveLover;
        public bool IsWounded;
        public int HonorPercent = 50;
        public int BoldnessPercent = 50;
        public int Charm;
        public int Steward;
        public int Medicine;
        public int Engineering;
        public int Tactics;
        public int Athletics;
        public int Leadership;
        public int Trade;
        public int Riding;
        public int Scouting;
        public int Roguery;
        public int GenerosityPercent = 50;
        public int MercyPercent = 50;
    }

    public sealed class CastleBathHistory
    {
        public string HeroId = string.Empty;
        public int SelectionCount;
        public int LastDay = int.MinValue;
        public int LastBlock = -1;
    }

    public sealed class CastleScheduleResult
    {
        public readonly Dictionary<CastleRoom, List<string>> Rooms =
            Enum.GetValues(typeof(CastleRoom)).Cast<CastleRoom>()
                .ToDictionary(room => room, _ => new List<string>());
        public readonly List<string> BathSelections = new List<string>();
    }

    public static class CastleScheduleEngine
    {
        private static readonly CastleRoom[] OrdinaryRooms =
        {
            CastleRoom.CastleGardens, CastleRoom.NobleSolar, CastleRoom.Library,
            CastleRoom.TrainingYard, CastleRoom.InnerCourtyard, CastleRoom.DiningChamber,
            CastleRoom.StableCourtyard, CastleRoom.PortraitGallery, CastleRoom.MainHall,
            CastleRoom.Chapel, CastleRoom.ThroneRoom, CastleRoom.Battlements,
            CastleRoom.GuestBedrooms, CastleRoom.RoyalBedroom
        };

        public static CastleTimeBlock GetTimeBlock(double hour)
        {
            int normalized = ((int)Math.Floor(hour) % 24 + 24) % 24;
            if (normalized >= 5 && normalized <= 10) return CastleTimeBlock.Morning;
            if (normalized >= 11 && normalized <= 16) return CastleTimeBlock.Afternoon;
            if (normalized >= 17 && normalized <= 20) return CastleTimeBlock.Evening;
            return CastleTimeBlock.Night;
        }

        public static CastleScheduleResult Build(
            IEnumerable<CastleScheduleCandidate> source,
            IEnumerable<CastleBathHistory> history,
            string campaignSeed,
            int day,
            CastleTimeBlock block)
        {
            List<CastleScheduleCandidate> candidates = (source ?? Enumerable.Empty<CastleScheduleCandidate>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.HeroId))
                .GroupBy(x => x.HeroId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
            var result = new CastleScheduleResult();
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CastleScheduleCandidate candidate in candidates
                .OrderByDescending(x => BestScore(x, block, campaignSeed, day))
                .ThenBy(x => StableHash(campaignSeed, day, block, x.HeroId, "candidate")))
            {
                CastleRoom room = OrdinaryRooms
                    .Where(x => x != CastleRoom.RoyalBedroom || IsRoyalBedroomEligible(candidate))
                    .Where(x => result.Rooms[x].Count < Capacity(x))
                    .OrderByDescending(x => Score(candidate, x, block))
                    .ThenBy(x => StableHash(campaignSeed, day, block, candidate.HeroId, x.ToString()))
                    .FirstOrDefault();
                if (result.Rooms[room].Count < Capacity(room))
                {
                    result.Rooms[room].Add(candidate.HeroId);
                    assigned.Add(candidate.HeroId);
                }
            }

            SelectBaths(candidates, history, campaignSeed, day, block, result);
            if (result.BathSelections.Count > 0)
            {
                var bathIds = new HashSet<string>(result.BathSelections, StringComparer.OrdinalIgnoreCase);
                foreach (CastleRoom room in OrdinaryRooms)
                    result.Rooms[room].RemoveAll(id => bathIds.Contains(id));
                result.Rooms[CastleRoom.Baths].AddRange(result.BathSelections);
            }
            return result;
        }

        public static bool IsRoyalBedroomEligible(CastleScheduleCandidate x)
        {
            return x != null && (x.IsSpouse || x.IsActiveLover ||
                (x.HonorPercent <= 40 && x.BoldnessPercent >= 60));
        }

        public static int Capacity(CastleRoom room) => room == CastleRoom.GuestBedrooms ? 20 : 4;

        private static void SelectBaths(
            List<CastleScheduleCandidate> candidates,
            IEnumerable<CastleBathHistory> history,
            string seed, int day, CastleTimeBlock block, CastleScheduleResult result)
        {
            if (candidates.Count == 0) return;
            Dictionary<string, CastleBathHistory> byHero = (history ?? Enumerable.Empty<CastleBathHistory>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.HeroId))
                .GroupBy(x => x.HeroId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            CastleScheduleCandidate first = candidates
                .OrderBy(x => BathCount(x.HeroId, byHero))
                .ThenBy(x => BathLastDay(x.HeroId, byHero))
                .ThenBy(x => BathLastBlock(x.HeroId, byHero))
                .ThenBy(x => StableHash(seed, day, block, x.HeroId, "bath-first"))
                .First();
            result.BathSelections.Add(first.HeroId);

            int additional = 1 + Math.Abs(StableHash(seed, day, block, first.HeroId, "bath-1d3")) % 3;
            IEnumerable<CastleScheduleCandidate> sameSex = candidates.Where(x =>
                x.HeroId != first.HeroId && x.IsFemale == first.IsFemale);
            foreach (CastleScheduleCandidate next in sameSex
                .OrderBy(x => BathCount(x.HeroId, byHero))
                .ThenBy(x => BathLastDay(x.HeroId, byHero))
                .ThenBy(x => BathLastBlock(x.HeroId, byHero))
                .ThenBy(x => StableHash(seed, day, block, x.HeroId, "bath-extra"))
                .Take(additional))
                result.BathSelections.Add(next.HeroId);
        }

        private static int BathCount(string id, IDictionary<string, CastleBathHistory> h) =>
            h.TryGetValue(id, out CastleBathHistory x) ? x.SelectionCount : 0;
        private static int BathLastDay(string id, IDictionary<string, CastleBathHistory> h) =>
            h.TryGetValue(id, out CastleBathHistory x) ? x.LastDay : int.MinValue;
        private static int BathLastBlock(string id, IDictionary<string, CastleBathHistory> h) =>
            h.TryGetValue(id, out CastleBathHistory x) ? x.LastBlock : -1;

        private static int BestScore(CastleScheduleCandidate x, CastleTimeBlock block, string seed, int day)
        {
            return OrdinaryRooms.Where(r => r != CastleRoom.RoyalBedroom || IsRoyalBedroomEligible(x))
                .Max(r => Score(x, r, block));
        }

        public static int Score(CastleScheduleCandidate x, CastleRoom room, CastleTimeBlock block)
        {
            int social = x.Charm + x.GenerosityPercent + x.MercyPercent;
            int martial = x.Tactics + x.Athletics + x.Leadership;
            int score;
            switch (room)
            {
                case CastleRoom.CastleGardens: score = x.Medicine * 2 + x.Steward + x.MercyPercent + social / 3; break;
                case CastleRoom.NobleSolar: score = x.Charm + x.Steward * 2 + x.Leadership + (x.IsClanLeader ? 180 : 0); break;
                case CastleRoom.Library: score = x.Medicine + x.Engineering * 2 + x.Steward + x.Tactics; break;
                case CastleRoom.TrainingYard: score = martial * 2 + x.Riding + x.BoldnessPercent; break;
                case CastleRoom.InnerCourtyard: score = social * 2 + x.Charm; break;
                case CastleRoom.DiningChamber: score = x.Charm * 2 + x.Trade + x.GenerosityPercent * 2 + (x.IsAmbassador ? 160 : 0); break;
                case CastleRoom.StableCourtyard: score = x.Riding * 2 + x.Scouting * 2 + (x.IsPartyMember ? 140 : 0); break;
                case CastleRoom.PortraitGallery: score = x.Charm + x.Leadership + (x.IsLord ? 100 : 0) + (x.IsClanLeader ? 120 : 0); break;
                case CastleRoom.MainHall: score = x.Leadership * 2 + x.Charm + x.Steward + (x.IsOfficeHolder ? 180 : 0); break;
                case CastleRoom.Chapel: score = x.HonorPercent * 2 + x.MercyPercent + (x.IsLord ? 50 : 0); break;
                case CastleRoom.ThroneRoom: score = x.Leadership * 2 + x.Charm + (x.IsOfficeHolder ? 220 : 0) + (x.IsClanLeader ? 180 : 0); break;
                case CastleRoom.Battlements: score = x.Scouting * 2 + x.Tactics * 2 + x.Engineering + x.BoldnessPercent; break;
                case CastleRoom.GuestBedrooms: score = (x.IsAmbassador ? 240 : 0) + (x.IsWounded ? 300 : 0) + (block == CastleTimeBlock.Night ? 220 : 0); break;
                case CastleRoom.RoyalBedroom: score = (x.IsSpouse ? 500 : 0) + (x.IsActiveLover ? 450 : 0) + x.Roguery + x.BoldnessPercent - x.HonorPercent; break;
                default: score = 0; break;
            }
            if (block == CastleTimeBlock.Morning && (room == CastleRoom.CastleGardens || room == CastleRoom.TrainingYard || room == CastleRoom.Chapel)) score += 100;
            if (block == CastleTimeBlock.Afternoon && (room == CastleRoom.MainHall || room == CastleRoom.Library || room == CastleRoom.ThroneRoom)) score += 100;
            if (block == CastleTimeBlock.Evening && (room == CastleRoom.DiningChamber || room == CastleRoom.InnerCourtyard || room == CastleRoom.PortraitGallery)) score += 120;
            if (block == CastleTimeBlock.Night && (room == CastleRoom.GuestBedrooms || room == CastleRoom.RoyalBedroom || room == CastleRoom.Battlements)) score += 160;
            return score;
        }

        public static int StableHash(string seed, int day, CastleTimeBlock block, string heroId, string room)
        {
            unchecked
            {
                uint hash = 2166136261;
                string text = (seed ?? string.Empty) + "|" + day + "|" + (int)block + "|" + heroId + "|" + room;
                foreach (char c in text) { hash ^= c; hash *= 16777619; }
                return (int)(hash & 0x7fffffff);
            }
        }
    }
}
