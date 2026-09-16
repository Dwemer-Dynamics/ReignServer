using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBeta.Shared.Characters
{
    /// <summary>Provider-free resident identity rules shared by the native adapter and acceptance harness.</summary>
    public static class EncounteredResidentRules
    {
        public const double LocalAuthorityRecognitionProbability = 0.90;
        public const string EquippedOutfitPolicy = "resident_current_native_equipment";

        public static bool IsAvailable(string home, string current, string requested, bool met,
            bool alive, bool prisoner, bool inParty, bool adult) => met && alive && adult && !prisoner
            && !inParty && !string.IsNullOrEmpty(requested) && home == requested && current == requested;

        public static bool IsMilitary(string occupation, string sourceId)
        {
            string value = (occupation + " " + sourceId).ToLowerInvariant();
            return value.Contains("guard") || value.Contains("soldier") || value.Contains("mercenary")
                || value.Contains("infantry") || value.Contains("cavalry") || value.Contains("archer");
        }

        public static string SkillRole(string occupation, string sourceId)
        {
            string value = (occupation + " " + sourceId).ToLowerInvariant();
            if (IsMilitary(occupation, sourceId)) return "military";
            if (value.Contains("shipwright")) return "engineer";
            if (value.Contains("smith") || value.Contains("armorer") || value.Contains("weaponsmith")) return "smith";
            if (value.Contains("arena")) return "fighter";
            if (value.Contains("musician") || value.Contains("barber")) return "performer";
            if (value.Contains("tavern") || value.Contains("merchant") || value.Contains("trader")
                || value.Contains("shop") || value.Contains("broker") || value.Contains("gamehost")) return "trader";
            return "resident";
        }

        public static bool NamesTooSimilar(string left, string right)
        {
            string a = Normalize(left), b = Normalize(right);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b || Phonetic(a) == Phonetic(b)) return true;
            int limit = Math.Min(a.Length, b.Length) >= 7 ? 2 : 1;
            if (Math.Abs(a.Length - b.Length) > limit) return false;
            int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int[] next = new int[b.Length + 1]; next[0] = i;
                for (int k = 1; k <= b.Length; k++)
                    next[k] = Math.Min(Math.Min(next[k - 1] + 1, previous[k] + 1),
                        previous[k - 1] + (a[i - 1] == b[k - 1] ? 0 : 1));
                previous = next;
            }
            return previous[b.Length] <= limit;
        }

        public static string ChooseName(string culture, bool female, IEnumerable<string> nativeFirstNames,
            IEnumerable<string> existingFullNames, IEnumerable<string> recentNames, Func<int, int> random)
        {
            string[] pool = Pool(culture);
            string[] first = nativeFirstNames.Concat(pool[female ? 1 : 0].Split(' '))
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.Contains(" "))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] last = pool[2].Split(' ');
            string[] recent = recentNames.Reverse().Take(12).ToArray();
            var existing = new HashSet<string>(existingFullNames.Select(Normalize), StringComparer.Ordinal);
            // Shuffle complete existing names, never mutate a spelling to solve a collision.
            var candidates = first.SelectMany(f => last.Select(l => f + " " + l)).ToArray();
            for (int i = candidates.Length - 1; i > 0; i--)
            { int n = random(i + 1); string swap = candidates[i]; candidates[i] = candidates[n]; candidates[n] = swap; }
            foreach (string name in candidates)
            {
                string f = name.Split(' ')[0], surname = name.Substring(f.Length + 1);
                if (existing.Contains(Normalize(name))) continue;
                if (recent.Any(old => NamesTooSimilar(f, old.Split(' ')[0]) || NamesTooSimilar(name, old))) continue;
                if (recent.Take(4).Any(old => NamesTooSimilar(surname, old.Split(' ').Last()))) continue;
                return name;
            }
            throw new InvalidOperationException("No sufficiently distinct resident name is available in this culture's name pool.");
        }

        private static string Normalize(string value)
        {
            var result = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetter(c))
                    result.Append(char.ToLowerInvariant(c));
            return result.ToString();
        }

        private static string Phonetic(string value)
        {
            var result = new StringBuilder(); char previous = '\0';
            foreach (char raw in value.Replace("ph", "f").Replace("kh", "k").Replace("th", "t"))
            {
                char c = "aeiouy".IndexOf(raw) >= 0 ? 'a' : "ckq".IndexOf(raw) >= 0 ? 'k'
                    : "sz".IndexOf(raw) >= 0 ? 's' : "vw".IndexOf(raw) >= 0 ? 'v' : raw;
                if (c != previous) result.Append(c); previous = c;
            }
            return result.ToString();
        }

        private static string[] Pool(string culture)
        {
            switch ((culture ?? "").ToLowerInvariant())
            {
                case "empire": return new[] {
                    "Adrian Basil Cassian Dorian Evander Felix Gavril Hector Isidor Julian Leon Marius Nestor Octavian Petros Quintus Rufus Silvan Titus Valerian Xanthos Zenon Aurel Corvin Demetrios Florian Lucan Niketas Severin Theodor",
                    "Agatha Beata Camilla Damaris Eleni Flavia Galene Helena Irene Junia Kallista Lydia Marcella Nereida Octavia Petra Rhea Sabina Thalia Valeria Xenia Zoe Augusta Cassia Dorothea Eudora Justina Melina Sapphira Thekla",
                    "Acacius Bellator Corax Dacian Eirenikos Ferran Galatos Helikon Istrian Kallinos Laskaris Melanthos Neritos Orontes Pavlos Rhodios Salvius Thalassos Umbrian Ventor Xenides Zephiris Argyros Drakos Varenos Pellios Orestian Chariton Damianos Falkos" };
                case "vlandia": return new[] {
                    "Alaric Bernard Cedric Dunstan Edgar Ferrand Geoffrey Hugo Ivo Jocelin Lambert Martin Neville Oswin Piers Quentin Roland Simon Tristan Ulric Vincent Walter Yves Aldwin Bertram Conan Eustace Fulbert Gerard Reynard",
                    "Adelaide Beatrice Cecily Delphine Edith Fleur Giselle Heloise Isolde Joan Lenore Mathilde Nicolette Odette Philippa Rosamund Sybil Tamsin Ursula Viola Willa Yvette Agnes Blanche Clarice Eleanor Fenella Maude Rowena Selise",
                    "Alderwick Bellamy Carver Davenant Everard Fairford Galloway Hawthorne Ironwood Kingsley Langford Marchant Northcott Oakley Penrose Quill Redfern Shepherd Thorne Underhill Voss Whitlock Yarrow Ashcombe Briarvale Crowhurst Dunmore Fallow Hartwell Loxley" };
                case "sturgia": return new[] {
                    "Aleksei Boris Cedomir Dragomir Evgeni Fyodor Gavril Igor Jaromir Kazimir Luka Mstislav Nikita Oleg Pavel Radovan Sava Tihomir Vadim Yaroslav Zoran Bogdan Dobrynya Grigor Ilia Kirill Leonid Milan Rostislav Vsevolod",
                    "Anfisa Bozhena Daria Elena Fedora Galina Irina Jelena Katya Lada Milena Nadezhda Oksana Polina Raisa Svetlana Tamara Vasilisa Yelena Zlata Agnia Bronislava Danica Ekaterina Ksenia Ljuba Marfa Rada Tatiana Vesna",
                    "Belov Chernov Dobrin Efimov Frolov Gromov Ilyin Korovin Lebedev Morozov Nekrasov Orlov Petrov Rakitin Sokolov Taranov Ustinov Volkov Yermakov Zorin Arsenov Berezin Dubov Kamenev Listov Medvedev Ozerov Rybakov Serebryan Vetrov" };
                case "battania": return new[] {
                    "Aedan Bran Cormac Donal Eamon Fergus Gwilym Hugh Iestyn Keir Lorcan Madoc Niall Oisin Padraig Rhys Seoras Taliesin Ualan Vaughan Wynne Alun Breccan Cadoc Darragh Emrys Fintan Geraint Ronan Taran",
                    "Ailis Brigid Carys Deirdre Eira Fenella Grainne Heledd Iona Keelin Llinos Mair Nessa Orla Rhiannon Sioned Tegan Una Vevina Winifred Awen Blathnaid Ceridwen Elowen Ffion Isolde Maeve Morwen Siobhan Talwyn",
                    "Ashgrove Blackthorn Cairnwood Dovewick Elderbrook Fernvale Glenfall Hartglen Iverstone Kestrel Larkspur Mossfield Nightbriar Oakenfell Pineward Ravenshaw Silverbranch Thornwell Underbough Willowmere Yewridge Brackenfirth Cragborne Dewhurst Foxhollow Hawkridge Rowanbank Stormreed Westmere Wildbrook" };
                case "aserai": return new[] {
                    "Adnan Bashir Dawud Farid Ghassan Hamid Idris Jabir Khalil Latif Malik Nasir Omar Qasim Rashad Samir Tariq Usama Wahid Yazid Zakari Abbas Burhan Faisal Harun Imran Jalal Kamil Munir Nabil Zafir",
                    "Amina Basma Dalal Farah Ghada Huda Iman Jamila Karima Layla Mariam Nadira Rania Salma Tahira Warda Yasmin Zahra Afra Bushra Dunya Fatima Hana Jalila Lamis Muna Najwa Ruqayya Safiya Zaynab",
                    "Abbasi Badawi Darwish Farouq Ghazali Haddad Iskandar Jabali Khoury Mansuri Najjar Qadiri Rihani Sabbagh Tamimi Uthmani Wazir Zaydan Amari Bakri Dawrani Fakhri Habashi Idrisi Jaziri Karami Maqdisi Nuwari Ramlawi Saffar" };
                case "khuzait": return new[] {
                    "Altan Batu Chuluun Dorje Erden Ganbaatar Hulan Ider Jochi Khasar Lhagb Munkh Naran Odon Qulan Saran Temur Ulagan Yeke Zorig Arslan Bektur Chagan Delger Esen Ganzorig Kulan Nogai Sukh Torgul",
                    "Altani Bolormaa Chimeg Davaa Erdenetsetseg Gerel Haliun Khulan Mandukhai Narantuya Odval Qutulun Sarangerel Tsetseg Ujin Yesui Zaya Anu Bayarmaa Dulgian Enkhtuya Ganchimeg Maral Nomin Oyun Sarnai Solongo Tuya Udval Yanjin",
                    "Arban Bayatur Chagan Delger Elet Gurban Hargan Ilchi Jargal Kharan Mergen Noyan Ordu Qorchi Suld Taigan Ulan Yalgu Zeren Borol Dorgun Khirgis Mangud Onon Sayan Tenger Urgun Yargan Altun Burkhan" };
                case "nord": return new[] {
                    "Arne Bjorn Dag Eirik Finn Gunnar Halfdan Ivar Ketil Leif Magnus Njall Orm Ragnar Sigurd Torvald Ulf Vidar Yngvar Asmund Brand Einar Frode Hakon Ingolf Knut Odd Runar Sten Trygve",
                    "Astrid Birgit Dagny Eydis Freydis Gudrun Hilda Ingrid Jorunn Kari Liv Magnhild Nanna Ragna Sigrid Thora Unn Vigdis Yrsa Asta Borghild Disa Embla Frida Gro Helga Runa Solveig Tove Ylva",
                    "Ashstrand Birchfell Coldwater Dawnfjord Elkholm Frostvik Gullstrand Hailstone Icebrook Kettleford Longshore Moonfell Northwind Oarvik Pineholm Ravensvik Saltmere Tideborn Ulvstrand Wavecrest Yewholm Amberfell Brinewatch Deepford Farstrand Hearthvik Ironfjord Mistvale Snowbank Winterholm" };
                default: return Pool("vlandia");
            }
        }
    }
}
