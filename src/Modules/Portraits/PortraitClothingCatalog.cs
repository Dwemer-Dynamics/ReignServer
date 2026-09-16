using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] PortraitClothingPromptFileNames =
        {
            "portrait_clothing_vlandian_landowner.txt",
            "portrait_clothing_vlandian_minor_lord.txt",
            "portrait_clothing_vlandian_high_noble.txt",
            "portrait_clothing_battanian_landowner.txt",
            "portrait_clothing_battanian_minor_lord.txt",
            "portrait_clothing_battanian_high_noble.txt",
            "portrait_clothing_sturgian_landowner.txt",
            "portrait_clothing_sturgian_minor_lord.txt",
            "portrait_clothing_sturgian_high_noble.txt",
            "portrait_clothing_imperial_landowner.txt",
            "portrait_clothing_imperial_minor_lord.txt",
            "portrait_clothing_imperial_high_noble.txt",
            "portrait_clothing_aserai_landowner.txt",
            "portrait_clothing_aserai_minor_lord.txt",
            "portrait_clothing_aserai_high_noble.txt",
            "portrait_clothing_khuzait_landowner.txt",
            "portrait_clothing_khuzait_minor_lord.txt",
            "portrait_clothing_khuzait_high_noble.txt",
            "portrait_clothing_nord_landowner.txt",
            "portrait_clothing_nord_minor_lord.txt",
            "portrait_clothing_nord_high_noble.txt",
            "portrait_clothing_vlandian_merchant.txt",
            "portrait_clothing_vlandian_artisan.txt",
            "portrait_clothing_vlandian_gang_leader.txt",
            "portrait_clothing_vlandian_headman.txt",
            "portrait_clothing_vlandian_rural_notable.txt",
            "portrait_clothing_battanian_merchant.txt",
            "portrait_clothing_battanian_artisan.txt",
            "portrait_clothing_battanian_gang_leader.txt",
            "portrait_clothing_battanian_headman.txt",
            "portrait_clothing_battanian_rural_notable.txt",
            "portrait_clothing_sturgian_merchant.txt",
            "portrait_clothing_sturgian_artisan.txt",
            "portrait_clothing_sturgian_gang_leader.txt",
            "portrait_clothing_sturgian_headman.txt",
            "portrait_clothing_sturgian_rural_notable.txt",
            "portrait_clothing_imperial_merchant.txt",
            "portrait_clothing_imperial_artisan.txt",
            "portrait_clothing_imperial_gang_leader.txt",
            "portrait_clothing_imperial_headman.txt",
            "portrait_clothing_imperial_rural_notable.txt",
            "portrait_clothing_aserai_merchant.txt",
            "portrait_clothing_aserai_artisan.txt",
            "portrait_clothing_aserai_gang_leader.txt",
            "portrait_clothing_aserai_headman.txt",
            "portrait_clothing_aserai_rural_notable.txt",
            "portrait_clothing_khuzait_merchant.txt",
            "portrait_clothing_khuzait_artisan.txt",
            "portrait_clothing_khuzait_gang_leader.txt",
            "portrait_clothing_khuzait_headman.txt",
            "portrait_clothing_khuzait_rural_notable.txt",
            "portrait_clothing_nord_merchant.txt",
            "portrait_clothing_nord_artisan.txt",
            "portrait_clothing_nord_gang_leader.txt",
            "portrait_clothing_nord_headman.txt",
            "portrait_clothing_nord_rural_notable.txt"
        };

        private static readonly Regex PortraitClothingColorGuidancePattern = new Regex(
            @"\s+Use (?:burgundy|deep blue|deep red|rich burgundy|moss green|deep green|forest green|dark red|cream|sand|muted blue)[^.]*\.",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void RemoveSavedPortraitClothingColorGuidance()
        {
            foreach (string name in PortraitClothingPromptFileNames)
            {
                string path = PromptPath(name);
                if (!File.Exists(path))
                {
                    continue;
                }

                string original = File.ReadAllText(path, Encoding.UTF8);
                string updated = PortraitClothingColorGuidancePattern.Replace(original, string.Empty)
                    .Replace("simple bronze brooch", "simple brooch")
                    .Replace("restrained silver or bronze jewelry", "restrained jewelry")
                    .Replace(
                        "craftsmanship, rare dyes, and fabric quality",
                        "craftsmanship and fabric quality");
                if (!string.Equals(original, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(path, updated, Encoding.UTF8);
                }
            }
        }

        private static string SelectPortraitClothingPromptFile(Dictionary<string, object> payload)
        {
            string culture = NormalizePortraitClothingCulture(ReadFirstString(payload, "cultureName", "cultureId"));
            if (string.IsNullOrWhiteSpace(culture))
            {
                return string.Empty;
            }

            string station = ReadBool(payload, "isNotable", false)
                ? PortraitClothingNotableKey(ReadString(payload, "occupation", ""))
                : PortraitClothingStationKey(ReadInt(payload, "clanTier", 0), ReadString(payload, "socialStation", ""));
            return string.IsNullOrWhiteSpace(station)
                ? string.Empty
                : "portrait_clothing_" + culture + "_" + station + ".txt";
        }

        private static string PortraitClothingNotableKey(string occupation)
        {
            string normalized = Regex.Replace((occupation ?? string.Empty).Trim().ToLowerInvariant(), @"[\s_-]+", string.Empty);
            if (normalized.Contains("merchant")) return "merchant";
            if (normalized.Contains("artisan")) return "artisan";
            if (normalized.Contains("gangleader")) return "gang_leader";
            if (normalized.Contains("headman")) return "headman";
            if (normalized.Contains("ruralnotable")) return "rural_notable";
            return string.Empty;
        }

        private static string NormalizePortraitClothingCulture(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains("vland")) return "vlandian";
            if (normalized.Contains("battan")) return "battanian";
            if (normalized.Contains("sturg")) return "sturgian";
            if (normalized.Contains("empire") || normalized.Contains("imperial")) return "imperial";
            if (normalized.Contains("aserai")) return "aserai";
            if (normalized.Contains("khuzait")) return "khuzait";
            if (normalized.Contains("nord")) return "nord";
            return string.Empty;
        }

        private static string PortraitClothingStationKey(int clanTier, string socialStation)
        {
            if (clanTier >= 1 && clanTier <= 2) return "landowner";
            if (clanTier >= 3 && clanTier <= 4) return "minor_lord";
            if (clanTier >= 5) return "high_noble";

            string normalized = (socialStation ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains("landowner")) return "landowner";
            if (normalized.Contains("lesser") || normalized.Contains("minor")) return "minor_lord";
            if (normalized.Contains("high noble")) return "high_noble";
            return string.Empty;
        }

        private static IEnumerable<Dictionary<string, object>> PortraitClothingPromptMetadata()
        {
            foreach (string name in PortraitClothingPromptFileNames)
            {
                string key = name.Replace("portrait_clothing_", "").Replace(".txt", "");
                yield return PromptMeta(
                    name,
                    "Portrait Clothing - " + HumanizeKey(key),
                    "image",
                    true,
                    key.EndsWith("_merchant") || key.EndsWith("_artisan") || key.EndsWith("_gang_leader") || key.EndsWith("_headman") || key.EndsWith("_rural_notable")
                        ? "Culture- and occupation-specific notable clothing guidance. Selected automatically; edit the clothing-only text without changing the file name."
                        : "Culture- and clan-tier-specific everyday civilian clothing guidance. Selected automatically; edit the text without changing the file name.");
            }
        }

        private static Dictionary<string, string> PortraitClothingPromptDefaults()
        {
            Dictionary<string, string> defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["portrait_clothing_vlandian_landowner.txt"] =
@"Culturally appropriate everyday Vlandian landowner attire inspired by practical high-medieval Norman and Frankish clothing. Use a well-made wool tunic over a linen underlayer, fitted sleeves, a sturdy leather belt, narrow trousers or chausses, and soft leather boots. For women, use a long fitted underdress with a simple but well-tailored overdress, narrow sleeves, a modest neckline, and a practical belt. The garments should appear prosperous, clean, and durable without looking aristocratic. Include minimal embroidery or woven trim. No headgear of any kind. No armor, weapons, crowns, ceremonial robes, or fantasy elements.",
                ["portrait_clothing_vlandian_minor_lord.txt"] =
@"Culturally appropriate everyday Vlandian minor-lord attire inspired by refined high-medieval Norman and Frankish noble dress. Use a fitted wool tunic or cote over a fine linen shirt, high-quality chausses, a decorated leather belt, and an optional short mantle or shoulder drape with restrained embroidery. For women, use a long underdress with an elegant tailored overdress, shaped sleeves, fine woven fabric, and decorative trim around the cuffs, hem, and neckline. Include modest clasps, brooches, or small jewelry details. No headgear of any kind. No armor, weapons, crowns, royal regalia, or ceremonial robes.",
                ["portrait_clothing_vlandian_high_noble.txt"] =
@"Culturally appropriate everyday Vlandian high-noble attire inspired by wealthy Norman and Frankish aristocratic dress. Use luxurious fine wool and linen, expertly tailored layers, richly cut outer tunics or gowns, refined belts, decorative borders, subtle heraldic embroidery, and elegant fastenings. Men should wear a long tailored tunic or refined surcoat-style outer layer over fine linen and well-fitted chausses. Women should wear a fitted noble gown layered over a high-quality underdress, with graceful sleeves, refined trim, and tasteful jewelry. The outfit should feel expensive and powerful while remaining believable everyday noble wear. No headgear of any kind. No armor, weapons, crowns, or ceremonial regalia.",

                ["portrait_clothing_battanian_landowner.txt"] =
@"Culturally appropriate everyday Battanian landowner attire inspired by rugged Celtic and highland clothing. Use coarse but well-maintained wool, linen, woven cloth, and soft leather. Men should wear a long-sleeved tunic, fitted trousers, a broad leather belt, and a heavy woven cloak or shoulder wrap fastened with a simple brooch. Women should wear a long linen dress beneath a sleeveless or short-sleeved wool overdress, secured with a woven belt and layered with a practical shawl or shoulder mantle. Include subtle woven checks or geometric trim, but no modern tartan or stereotypical kilts. No headgear of any kind. No armor, weapons, ceremonial costume, war paint, or fantasy druid elements.",
                ["portrait_clothing_battanian_minor_lord.txt"] =
@"Culturally appropriate everyday Battanian minor-lord attire inspired by refined Celtic and highland noble clothing. Use dense woven wool, quality linen, soft leather details, cleaner tailoring, decorative brooches, and more elaborate woven trim than ordinary landowners would possess. Men should wear a fitted tunic, practical trousers, a decorated belt, and a layered cloak fastened at the shoulder. Women should wear a long underdress with a tailored overdress, woven belt, elegant draped shawl, and restrained ornamental trim. Include subtle knotwork and geometric patterns without theatrical fantasy styling. No headgear of any kind. No armor, weapons, crowns, or ceremonial druid imagery.",
                ["portrait_clothing_battanian_high_noble.txt"] =
@"Culturally appropriate everyday Battanian high-noble attire inspired by elite Celtic and highland aristocratic clothing. Use fine woven wool, linen, soft leather accents, sophisticated layering, ornate brooches, and carefully worked borders featuring tasteful knotwork or geometric designs. Men should wear a richly made noble tunic with a broad decorated belt and an exceptionally well-crafted draped cloak. Women should wear a long fitted noble dress beneath a richly layered overdress, with elegant woven borders, decorative brooch fastenings, and a graceful shoulder wrap. The outfit should communicate power through craftsmanship and rich textiles rather than excessive decoration. No headgear of any kind. No armor, weapons, crowns, or fantasy druid elements.",

                ["portrait_clothing_sturgian_landowner.txt"] =
@"Culturally appropriate everyday Sturgian landowner attire inspired by medieval Rus and Slavic clothing suited to a cold northern climate. Men should wear a long linen shirt beneath a belted wool tunic or simple kaftan, loose trousers, wrapped lower legs, and a heavy wool outer layer or practical cloak. Women should wear a long linen shift beneath a wool overdress or simple coat-dress, tied with a woven sash and layered with a practical shoulder covering. Add modest geometric embroidery around the collar, cuffs, or hem. Fur may appear only as light functional trim. No headgear of any kind. No armor, weapons, ceremonial fur mantles, crowns, or fantasy elements.",
                ["portrait_clothing_sturgian_minor_lord.txt"] =
@"Culturally appropriate everyday Sturgian minor-lord attire inspired by refined medieval Rus and Slavic noble clothing. Men should wear a well-made long shirt beneath a tailored wool kaftan or long belted coat, decorated with fine geometric embroidery and quality trim. Women should wear a long embroidered shift with an elegant wool overdress or noble coat-dress, secured with a richly woven sash. Include subtle fur trim around collars or cuffs, tasteful clasps, and better-quality fabric than a landowner would wear. The clothing should remain practical for a northern climate while clearly showing noble rank. No headgear of any kind. No armor, weapons, crowns, ceremonial robes, or oversized fur displays.",
                ["portrait_clothing_sturgian_high_noble.txt"] =
@"Culturally appropriate everyday Sturgian high-noble attire inspired by wealthy medieval Rus aristocracy. Use richly woven wool, fine linen, elegant long coats or kaftans, tasteful embroidery, restrained fur trim, and expertly crafted belts or clasps. Men should wear a stately long noble coat layered over a fine embroidered shirt and tailored trousers. Women should wear a richly cut long dress or overdress with embroidered cuffs, collar, and hem, paired with a fitted outer garment or elegant shoulder layer. The outfit should feel imposing, expensive, and suited to the daily life of a powerful northern noble. No headgear of any kind. No armor, weapons, crowns, full ceremonial regalia, or extravagant fur mantles.",

                ["portrait_clothing_imperial_landowner.txt"] =
@"Culturally appropriate everyday Calradic Imperial landowner attire inspired by practical late Roman and Byzantine civilian clothing adapted into a grounded medieval style. Men should wear a linen undertunic beneath a structured knee-length tunic or simple long coat, secured with a narrow belt. Women should wear a long tunic or underdress beneath a modest draped overdress with elegant but simple sleeves and a practical girdle. Include narrow woven borders or restrained geometric decoration. The garments should appear orderly, respectable, clean, and slightly prosperous. No headgear of any kind. No armor, weapons, laurel crowns, togas, ceremonial robes, or fantasy elements.",
                ["portrait_clothing_imperial_minor_lord.txt"] =
@"Culturally appropriate everyday Calradic Imperial minor-lord attire inspired by refined late Roman and Byzantine noble clothing. Men should wear a high-quality tunic or long tailored outer coat over fine linen, decorated with narrow woven borders, refined clasps, and a decorated belt. Women should wear a long elegant dress or layered tunic ensemble with shaped sleeves, graceful draping, and tasteful trim around the neckline, cuffs, and edges. The garments should feel polished, educated, urban, and wealthy without resembling full imperial ceremony. No headgear of any kind. No armor, weapons, crowns, imperial regalia, laurel wreaths, or ceremonial robes.",
                ["portrait_clothing_imperial_high_noble.txt"] =
@"Culturally appropriate everyday Calradic Imperial high-noble attire inspired by luxurious Byzantine aristocratic fashion, restrained enough for daily noble life. Use fine layered linen and wool, controlled flowing silhouettes, elegant draping, richly woven borders, small jeweled clasps, and sophisticated fabric textures. Men should wear a stately long tunic or structured noble coat over a fine undertunic, with refined belts and detailed edging. Women should wear a sophisticated layered noble dress with graceful drapery, tailored sleeves, and subtle aristocratic ornamentation. The outfit should communicate ancient prestige, wealth, and political importance. No headgear of any kind. No armor, weapons, crowns, laurel wreaths, or full ceremonial imperial regalia.",

                ["portrait_clothing_aserai_landowner.txt"] =
@"Culturally appropriate everyday Aserai landowner attire inspired by practical medieval Arabian and North African civilian clothing suited to a hot desert climate. Men should wear a light linen or cotton undertunic beneath a loose ankle-length robe, open-front outer garment, or wrapped coat, secured with a woven sash. Women should wear a long breathable dress with layered flowing fabric, fitted or gathered sleeves, and a light draped outer layer around the shoulders. Include simple geometric borders or restrained embroidery. The outfit should appear prosperous, breathable, practical, and well maintained. No headgear of any kind. No turbans, veils, hoods, armor, weapons, crowns, ceremonial robes, or fantasy elements.",
                ["portrait_clothing_aserai_minor_lord.txt"] =
@"Culturally appropriate everyday Aserai minor-lord attire inspired by refined medieval Arabian, North African, and limited Persian noble clothing. Men should wear a flowing robe or structured open-front coat over a fine lightweight tunic, secured with a decorated woven sash. Women should wear a long elegant dress with soft layered fabrics, shaped sleeves, graceful draping, and fine embroidery around the neckline, cuffs, and hem. Show rank through superior fabric, elegant borders, geometric embroidery, and subtle jewelry rather than excessive ornamentation. No headgear of any kind. No turbans, veils, hoods, armor, weapons, crowns, or ceremonial excess.",
                ["portrait_clothing_aserai_high_noble.txt"] =
@"Culturally appropriate everyday Aserai high-noble attire inspired by elite medieval Arabian and Persian-influenced aristocratic dress. Use exceptionally fine layered fabrics, graceful draping, richly stitched borders, elegant geometric embroidery, and luxurious but believable textile textures. Men should wear a noble robe or long structured outer coat over a fine tunic, secured with an ornate sash and refined clasps. Women should wear a richly layered long dress with flowing outer fabric, carefully tailored sleeves, elegant embroidery, and tasteful jewelry integrated into the outfit. The clothing should feel cultured, powerful, and extremely wealthy without becoming full ceremonial regalia. No headgear of any kind. No turbans, veils, hoods, armor, weapons, or crowns.",

                ["portrait_clothing_khuzait_landowner.txt"] =
@"Culturally appropriate everyday Khuzait landowner attire inspired by practical medieval Turkic and Mongol steppe clothing suited to riding, travel, wind, and changing weather. Men should wear a crossed-front deel or caftan with an overlapping closure, a broad woven or leather sash, loose trousers, and practical layered sleeves. Women should wear a long crossed-front robe or fitted steppe coat over a simple underdress, secured with a woven sash. Fabrics should include wool, felt, linen, and restrained leather edging. Add simple woven geometric trim. No headgear of any kind. No fur hats, caps, hoods, armor, weapons, oversized fur displays, crowns, or fantasy elements.",
                ["portrait_clothing_khuzait_minor_lord.txt"] =
@"Culturally appropriate everyday Khuzait minor-lord attire inspired by refined medieval Turkic and Mongol steppe noble clothing. Men should wear a tailored crossed-front coat or deel with refined woven trim, a decorated sash, layered sleeves, and high-quality trousers. Women should wear an elegant long robe or fitted steppe coat over a fine underdress, with carefully worked edging and decorative textile details. Include restrained cloud, knot, or geometric patterns and a limited amount of practical fur trim. The clothing should remain mobile and functional while clearly showing noble status. No headgear of any kind. No fur hats, caps, hoods, armor, weapons, crowns, or ceremonial costume.",
                ["portrait_clothing_khuzait_high_noble.txt"] =
@"Culturally appropriate everyday Khuzait high-noble attire inspired by elite medieval Turkic and Mongol aristocratic clothing. Use superb tailoring, layered fine wool and silk-like woven fabrics, elegant crossed-front closures, decorated sashes, refined edging, and subtle noble motifs. Men should wear a stately long noble deel or structured coat over richly made underlayers. Women should wear a luxurious robe or coat-dress with graceful lines, elaborate woven trim, and exceptionally high-quality textiles. Fur trim may be included sparingly along cuffs, collars, or garment edges. The clothing should feel wealthy, mobile, and commanding without becoming ceremonial. No headgear of any kind. No fur hats, caps, hoods, armor, weapons, crowns, or fantasy styling.",

                ["portrait_clothing_nord_landowner.txt"] =
@"Culturally appropriate everyday Nord landowner attire inspired by historically grounded early medieval Scandinavian civilian clothing. Men should wear a long-sleeved linen undershirt beneath a knee-length wool tunic, straight or loose trousers, a sturdy leather belt, and wrapped lower legs. Women should wear a long linen shift beneath a wool apron dress or layered overdress, secured with modest brooches and a woven belt. Include subtle tablet-woven trim and practical layered fabric. The garments should appear sturdy, clean, and modestly prosperous. No headgear of any kind. No helmets, caps, hoods, armor, weapons, crowns, ceremonial garments, horned helmets, or fantasy Viking styling.",
                ["portrait_clothing_nord_minor_lord.txt"] =
@"Culturally appropriate everyday Nord minor-lord attire inspired by refined early medieval Scandinavian noble clothing. Men should wear a well-cut wool tunic over a fine linen underlayer, quality trousers, a decorated belt, and tasteful brooch fastenings. Women should wear a long fine-linen shift with an elegant apron dress or layered wool overdress, improved textile quality, decorative woven trim, and modest metal fittings. The clothing should look practical, prestigious, and exceptionally well made without becoming ceremonial. No headgear of any kind. No helmets, caps, hoods, armor, weapons, crowns, horned helmets, or fantasy costume elements.",
                ["portrait_clothing_nord_high_noble.txt"] =
@"Culturally appropriate everyday Nord high-noble attire inspired by luxurious but historically grounded elite Scandinavian clothing. Use fine wool, exceptionally high-quality linen, elegant layering, intricate tablet-woven trim, tasteful brooches, decorated belts, and rich but believable textile detail. Men should wear a stately knee-length or slightly longer noble tunic with excellent tailoring, refined fastenings, and finely woven borders. Women should wear a richly made long dress or layered apron-dress arrangement over fine linen, with graceful draping, ornamental weaving, and restrained jewelry. The clothing should communicate high rank through craftsmanship and fabric quality rather than excessive ornamentation. No headgear of any kind. No helmets, caps, hoods, armor, weapons, crowns, horned helmets, or fantasy Viking styling."
            };

            AddNotableClothingDefaults(defaults, "vlandian",
                "Prosperous Vlandian merchant clothing: a finely tailored wool cote or long tunic over crisp linen, fitted sleeves, a good leather belt with a neat purse, quality chausses or a long layered gown, restrained woven borders, and polished soft-leather shoes. The cut should advertise urban wealth and reliable taste without noble heraldry.",
                "Skilled Vlandian artisan clothing: a durable fitted wool tunic or practical long overdress over linen, sleeves shaped for work, a sturdy belt, reinforced cuffs, and a clean leather or heavy-cloth apron with subtle guild-quality stitching. The garments should be respectable, well maintained, and visibly made by capable hands.",
                "Vlandian gang-leader clothing: dark, expensive wool layers cut close for movement, a fitted cote or long tunic over linen, hard-wearing chausses, high soft-leather boots, a broad belt, discreet metal fastenings, and a weathered short mantle. The outfit should mix street practicality with controlled, illicit prosperity.",
                "Vlandian headman clothing: conservative high-medieval village authority dress in a well-made wool tunic or long overdress, linen underlayer, sturdy belt, practical chausses, strong boots, and a substantial shoulder mantle with restrained woven edging. The clothing should show sober local standing rather than aristocratic luxury.",
                "Vlandian rural-notable clothing: prosperous country attire in good wool and linen, with a belted knee-length tunic or long practical gown, durable leggings or layered skirt, soft-leather boots, and a clean cloak or shoulder wrap. Use modest decorative trim and excellent repair to suggest land, livestock, and household wealth.");
            AddNotableClothingDefaults(defaults, "battanian",
                "Prosperous Battanian merchant clothing: finely woven wool and linen in layered tunics or a long dress, a broad decorated belt with a neat purse, a quality checked or geometric shoulder wrap, soft-leather shoes, and tasteful brooch fastenings. The textiles should show market wealth without noble ostentation or modern tartan.",
                "Skilled Battanian artisan clothing: a fitted wool tunic or practical overdress over linen, close sleeves, a sturdy woven belt, reinforced hems, and a clean leather or heavy-wool work apron marked by subtle knotwork edging. The outfit should be rugged, dexterous, and exceptionally well crafted.",
                "Battanian gang-leader clothing: layered dark wool and soft leather, a close-cut tunic, fitted trousers or a practical long dress, a broad belt, wrapped forearms, strong boots, and an asymmetrical shoulder cloak fixed with a heavy brooch. Use subdued checks and knotwork to convey covert wealth and woodland mobility.",
                "Battanian headman clothing: dignified highland village dress in dense wool and linen, with a long tunic or layered dress, broad woven belt, practical trousers or full skirt, a heavy shoulder mantle, and a prominent but simple brooch. The clothing should express tradition, judgment, and communal standing.",
                "Battanian rural-notable clothing: prosperous upland clothing made from excellent homespun wool and linen, combining a belted tunic or long overdress with sturdy lower layers, soft leather, a warm checked wrap, and modest geometric borders. It should look durable, land-rooted, and richer than ordinary village dress.");
            AddNotableClothingDefaults(defaults, "sturgian",
                "Prosperous Sturgian merchant clothing: a fine linen shirt beneath a well-cut wool kaftan or long belted coat, quality loose trousers or layered long dress, a woven sash, polished boots, restrained geometric embroidery, and light fur only at practical edges. The outfit should suit a wealthy northern trader.",
                "Skilled Sturgian artisan clothing: a durable linen shirt under a knee-length wool tunic or work dress, sleeves secured close, a broad utility belt, reinforced cuffs, a clean leather apron, wrapped lower legs, and precise geometric embroidery at collar and hem. The clothing should be warm, practical, and proudly made.",
                "Sturgian gang-leader clothing: a dark fitted kaftan or long wool coat over layered linen, loose trousers, wrapped calves, high boots, a broad leather belt, discreet clasps, and sparse fur trim. The silhouette should be mobile and intimidating through costly restraint, never armor or war gear.",
                "Sturgian headman clothing: a substantial long wool coat or belted tunic over linen, warm trousers or a layered long dress, a rich woven sash, strong boots, restrained collar embroidery, and modest functional fur trim. The garments should signal seasoned village authority and northern practicality.",
                "Sturgian rural-notable clothing: prosperous cold-country layers of thick wool and good linen, with a belted tunic or coat-dress, loose lower garments, wrapped legs, durable boots, a woven sash, and carefully repaired outer layers with modest embroidery. The outfit should reflect livestock and land wealth rather than court fashion.");
            AddNotableClothingDefaults(defaults, "imperial",
                "Prosperous Calradic Imperial merchant clothing: a crisp linen undertunic beneath a structured knee-length tunic or long urban coat, a narrow decorated belt with purse, orderly woven borders, polished shoes, and for women a graceful layered tunic-dress with controlled draping. The clothing should look cosmopolitan, literate, and commercially wealthy.",
                "Skilled Calradic Imperial artisan clothing: an orderly linen tunic beneath a durable sleeved work tunic or modest long dress, a practical belt, reinforced edges, and a clean leather or heavy-cloth apron with narrow geometric borders. The outfit should combine workshop utility with urban guild respectability.",
                "Calradic Imperial gang-leader clothing: a dark structured long coat or close-fitted tunic ensemble over fine linen, narrow belt, soft boots, discreet clasps, and restrained patterned borders. The tailoring should look expensive and urban while remaining unobtrusive, mobile, and free of official or noble insignia.",
                "Calradic Imperial headman clothing: conservative provincial authority dress in a substantial knee-length tunic or long layered gown, linen underlayers, a firm belt, orderly draping, sturdy shoes, and restrained woven edging. The clothing should suggest administrative dignity and village prosperity without court ceremony.",
                "Calradic Imperial rural-notable clothing: prosperous provincial garments of good wool and linen, using a belted tunic or long practical dress, layered outer cloth, strong shoes, simple geometric borders, and careful tailoring. The outfit should balance Roman-Byzantine order with the durability of a landholding household.");
            AddNotableClothingDefaults(defaults, "aserai",
                "Prosperous Aserai merchant clothing: a fine lightweight tunic beneath a flowing ankle-length robe or open-front coat, a decorated woven sash with a neat purse, soft leather slippers or boots, elegant geometric borders, and tasteful embroidery. The fabrics should look breathable, traveled, and commercially luxurious.",
                "Skilled Aserai artisan clothing: a breathable linen or cotton tunic or long work dress with gathered sleeves, a practical woven sash, reinforced hems, and a clean leather or heavy-cloth apron carrying restrained geometric stitching. The garments should permit precise work while showing guild pride and reliable prosperity.",
                "Aserai gang-leader clothing: layered dark lightweight robes and a close-wrapped open-front coat, secured by a broad sash with discreet inner folds, soft boots, subtle geometric edging, and a few costly clasps. The outfit should convey mobility, secrecy, and underworld wealth without armor or theatrical menace.",
                "Aserai headman clothing: dignified desert village attire in a long breathable tunic with a substantial open-front robe, woven sash, layered shoulder cloth, strong soft-leather footwear, and restrained embroidered borders. The clothing should express hospitality, seniority, and practical local authority.",
                "Aserai rural-notable clothing: prosperous oasis or pastoral dress in layered linen and cotton, with a loose long tunic or dress, practical outer robe, sturdy sash, soft boots, and modest geometric embroidery. Use high-quality but durable fabric suited to heat, dust, livestock, and agricultural oversight.");
            AddNotableClothingDefaults(defaults, "khuzait",
                "Prosperous Khuzait merchant clothing: a finely tailored crossed-front deel or caftan over light underlayers, a decorated woven sash with a compact purse, quality loose trousers or long robe layers, soft riding boots, and refined geometric edging. The outfit should look mobile, traveled, and enriched by steppe trade.",
                "Skilled Khuzait artisan clothing: a durable crossed-front tunic or fitted steppe coat with sleeves secured for work, a broad utility sash, loose trousers or practical underdress, reinforced edging, and a clean leather apron. Subtle woven geometry should show technical pride without restricting movement.",
                "Khuzait gang-leader clothing: a dark close-fitted crossed-front coat over layered wool and linen, a wide sash with concealed folds, practical trousers, high soft boots, restrained leather edging, and sparse fur at cuffs. The silhouette should be fast, controlled, and quietly expensive without weapons or armor.",
                "Khuzait headman clothing: a substantial crossed-front deel or long steppe coat, broad woven sash, layered sleeves, good trousers or a long underdress, durable boots, and modest geometric trim with limited practical fur. The clothing should convey settled authority while retaining riding-country functionality.",
                "Khuzait rural-notable clothing: prosperous herding-country attire in layered wool, felt, and linen, centered on a belted crossed-front robe or coat, loose lower garments, strong riding boots, and restrained woven edging. The garments should look weather-ready, carefully maintained, and richer than common pastoral dress.");
            AddNotableClothingDefaults(defaults, "nord",
                "Prosperous Nord merchant clothing: a fine linen shirt beneath an exceptionally well-cut wool tunic or layered apron dress, a decorated belt with a neat purse, quality trousers or full skirt, polished leather shoes, modest brooches, and refined tablet-woven trim. The outfit should display seaborne trade wealth without noble regalia.",
                "Skilled Nord artisan clothing: a durable wool tunic or practical apron dress over linen, close sleeves, a broad utility belt, reinforced hems, and a clean leather or heavy-cloth work apron with precise tablet-woven edging. The clothing should be sturdy, dexterous, and visibly superior in workmanship.",
                "Nord gang-leader clothing: dark layered wool over linen, a close-cut tunic or practical long dress, strong trousers, high soft-leather boots, a broad belt, discreet brooches, and a weathered short cloak. The outfit should suggest dockside mobility and illicit prosperity without armor or exaggerated Viking styling.",
                "Nord headman clothing: conservative local authority dress in a substantial wool tunic or layered apron dress over good linen, a decorated but practical belt, sturdy lower garments, strong shoes, a heavy cloak, and restrained tablet-woven borders. The clothing should show seniority and communal respect.",
                "Nord rural-notable clothing: prosperous farmstead clothing of excellent wool and linen, with a belted knee-length tunic or layered apron dress, durable trousers or skirt, wrapped lower legs, strong leather shoes, a warm cloak, and modest woven trim. The outfit should communicate livestock, land, and household wealth.");
            return defaults;
        }

        private static void AddNotableClothingDefaults(Dictionary<string, string> defaults, string culture,
            string merchant, string artisan, string gangLeader, string headman, string ruralNotable)
        {
            const string exclusions = " Clothing only: no headgear, armor, weapons, tools, held objects, pose, body, face, expression, personality, occupation activity, setting, background, text, or fantasy elements.";
            defaults["portrait_clothing_" + culture + "_merchant.txt"] = merchant + exclusions;
            defaults["portrait_clothing_" + culture + "_artisan.txt"] = artisan + exclusions;
            defaults["portrait_clothing_" + culture + "_gang_leader.txt"] = gangLeader + exclusions;
            defaults["portrait_clothing_" + culture + "_headman.txt"] = headman + exclusions;
            defaults["portrait_clothing_" + culture + "_rural_notable.txt"] = ruralNotable + exclusions;
        }
    }
}
