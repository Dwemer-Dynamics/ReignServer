using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Characters
{
    /// <summary>Fresh personal names, separate from Native's small name pool and occupational epithets.</summary>
    public static class WandererNames
    {
        private static readonly Dictionary<string, string[]> Patterns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["empire"] = new[] { "Aev Cal Damar Ery Flav Gav Hel Istr Jov Kyr Luc Mar Ner Or Pel Quin Rhag Sev Theod Ulp Val Xen Zor", "al an ar el en er il in ir on or us", "ios ian es os ion us", "ia ena ora illa antia ene" },
            ["vlandia"] = new[] { "Ald Amaur Baud Ber Ced Cor Dro Evr Foul Gal Gaut Hadr Lior Mont Odr Per Ren Roul Ser Thi Val Wern Ys", "al an ar el en er il in or un", "ard ric on ain ier aud", "elle ine ette iane ise ena" },
            ["sturgia"] = new[] { "Alek Bor Bran Chern Dob Draz Eryk Fed Gnev Kaz Khor Lad Mir Nov Olek Pred Rad Rur Svel Tvar Vlad Yar Zor", "an ar el en im ir od ol os ov", "imir islav odan yen ek or", "ena iva ysa mira slava anka" },
            ["battania"] = new[] { "Aed Aer Bevan Bran Cadan Caer Der Dov Eir Elid Fenn Gwy Ior Llew Madoc Mor Ner Ow Pry Rhyd Rian Tor Sul", "al an ar av ed el en er in or", "wyn oc eth dan ar in", "wen eth ia enne ara elis" },
            ["aserai"] = new[] { "Az Bah Dar Em Fai Far Ham Haz Idr Jal Kam Kir Lat Mal Maz Nas Om Qad Rash Saf Sam Tar Yaz Zah", "ab ad ah al am an ar as az im ir un", "an id ir un im ad aq", "iya ara ira una ah aya" },
            ["khuzait"] = new[] { "Ar Bat Bor Chag Cing Del Dor Ek Erd Gan Ger Hul Jor Kad Khar Mung Noy Or Qor Sang Tem Torg Ulag Yel", "ag al an ar at eg en er og ol or ug", "gan tem ur dai bek tai qai", "ai ene ul un jin ara" },
            ["nord"] = new[] { "Arn As Bjar Dag Eir Ein Finn Fro Gald Gunn Hal Hraf Ing Ivar Jor Ket Leif Njall Orm Ragn Sig Tor Ulf Vid Yng", "al an ar ei el en er il ing or ul un", "rik vald ar mund sten var", "dis run hild veig frid yna" }
        };

        public static string[] Cultures => Patterns.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        public static bool Supports(string culture) => culture != null && Patterns.ContainsKey(culture);

        public static string Choose(string culture, bool female, string identity, IEnumerable<string> reservedNames)
        {
            if (!Patterns.TryGetValue(culture ?? "", out var pattern))
                throw new InvalidOperationException("No reviewed wanderer name grammar for culture " + culture + ".");
            var reserved = reservedNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Split(' ')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var exact = new HashSet<string>(reserved.Select(WandererPopulationRules.NormalizeName));
            string[] starts = pattern[0].Split(' '), middles = pattern[1].Split(' '), endings = pattern[female ? 3 : 2].Split(' ');
            var rng = new Random(WandererPopulationRules.Seed(identity));
            for (int attempt = 0; attempt < 4096; attempt++)
            {
                string candidate = starts[rng.Next(starts.Length)] + middles[rng.Next(middles.Length)] + endings[rng.Next(endings.Length)];
                if (exact.Contains(WandererPopulationRules.NormalizeName(candidate))) continue;
                if (reserved.Any(old => EncounteredResidentRules.NamesTooSimilar(candidate, old))) continue;
                return candidate;
            }
            throw new InvalidOperationException("No distinct wanderer name remains for " + culture + "; candidate deferred, never duplicated.");
        }
    }
}
