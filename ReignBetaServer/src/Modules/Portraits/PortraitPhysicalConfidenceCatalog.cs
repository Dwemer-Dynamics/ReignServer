using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PortraitPhysicalConfidenceDefinition = "physical_confidence";

        private static readonly string[] PortraitPhysicalConfidencePromptFileNames =
        {
            "portrait_female_physical_confidence_00_20.txt",
            "portrait_female_physical_confidence_21_40.txt",
            "portrait_female_physical_confidence_41_60.txt",
            "portrait_female_physical_confidence_61_80.txt",
            "portrait_female_physical_confidence_81_100.txt"
        };

        private static string SelectPortraitPhysicalConfidencePromptFile(Dictionary<string, object> payload)
        {
            if (!IsFemalePortraitSubject(payload))
            {
                return string.Empty;
            }

            Dictionary<string, object> physicalConfidence = EnsurePortraitPhysicalConfidence(payload);
            int score = ReadInt(physicalConfidence, "score", 50);
            if (score <= 20) return PortraitPhysicalConfidencePromptFileNames[0];
            if (score <= 40) return PortraitPhysicalConfidencePromptFileNames[1];
            if (score <= 60) return PortraitPhysicalConfidencePromptFileNames[2];
            if (score <= 80) return PortraitPhysicalConfidencePromptFileNames[3];
            return PortraitPhysicalConfidencePromptFileNames[4];
        }

        private static Dictionary<string, object> EnsurePortraitPhysicalConfidence(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> existing = ReadDictionary(payload, "physicalConfidence");
            if (existing != null && existing.ContainsKey("score"))
            {
                int existingScore = ClampPortraitPercentage(ReadInt(existing, "score", 50));
                existing["score"] = existingScore;
                existing["profile"] = PhysicalConfidenceProfile(existingScore);
                existing["definition"] = PortraitPhysicalConfidenceDefinition;
                payload["physicalConfidence"] = existing;
                payload["physical_confidence"] = existingScore;
                return existing;
            }

            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            int flirtatiousness = -1;
            int confidence = -1;
            string source = "neutral_fallback";

            if (payload.ContainsKey("flirtatiousnessPercentage") || payload.ContainsKey("confidencePercentage"))
            {
                flirtatiousness = ClampPortraitPercentage(ReadInt(payload, "flirtatiousnessPercentage", 50));
                confidence = ClampPortraitPercentage(ReadInt(payload, "confidencePercentage", 50));
                source = "request_trait_percentages";
            }
            else
            {
                Dictionary<string, object> traitDocument = string.IsNullOrWhiteSpace(heroId)
                    ? new Dictionary<string, object>()
                    : ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
                if (traitDocument.Count == 0 && !string.IsNullOrWhiteSpace(heroId))
                {
                    Dictionary<string, Dictionary<string, object>> profiles = LoadCharacterProfileLibrary();
                    if (profiles.TryGetValue(heroId, out Dictionary<string, object> profile))
                    {
                        traitDocument = ReadDictionary(profile, "traits") ?? new Dictionary<string, object>();
                        source = "shipped_profile_library";
                    }
                }
                else if (traitDocument.Count > 0)
                {
                    source = "campaign_traits";
                }

                Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages") ?? new Dictionary<string, object>();
                if (percentages.ContainsKey("flirtatiousness"))
                {
                    flirtatiousness = ClampPortraitPercentage(ReadInt(percentages, "flirtatiousness", 50));
                }
                if (percentages.ContainsKey("confidence"))
                {
                    confidence = ClampPortraitPercentage(ReadInt(percentages, "confidence", 50));
                }

                Dictionary<string, object> foundation = ReadDictionary(traitDocument, "foundationTraits")
                    ?? ReadDictionary(traitDocument, "hiddenReignTraits")
                    ?? new Dictionary<string, object>();
                if (flirtatiousness < 0 && foundation.ContainsKey("flirtatiousness"))
                {
                    flirtatiousness = StableTraitPercentage(heroId, "flirtatiousness", TraitInt(foundation, "flirtatiousness"));
                    source += "+stable_percentage_fallback";
                }
                if (confidence < 0 && foundation.ContainsKey("confidence"))
                {
                    confidence = StableTraitPercentage(heroId, "confidence", TraitInt(foundation, "confidence"));
                    source += "+stable_percentage_fallback";
                }
            }

            if (flirtatiousness < 0) flirtatiousness = 50;
            if (confidence < 0) confidence = 50;
            int score = ClampPortraitPercentage((int)Math.Round(
                flirtatiousness * 0.75d + confidence * 0.25d,
                MidpointRounding.AwayFromZero));

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["definition"] = PortraitPhysicalConfidenceDefinition,
                ["score"] = score,
                ["profile"] = PhysicalConfidenceProfile(score),
                ["flirtatiousnessPercentage"] = flirtatiousness,
                ["confidencePercentage"] = confidence,
                ["flirtatiousnessWeight"] = 0.75d,
                ["confidenceWeight"] = 0.25d,
                ["source"] = source
            };
            payload["physicalConfidence"] = result;
            payload["physical_confidence"] = score;
            return result;
        }

        private static bool IsFemalePortraitSubject(Dictionary<string, object> payload)
        {
            string gender = ReadString(payload, "gender", "").Trim().ToLowerInvariant();
            return gender == "woman" || gender == "female" || gender == "girl";
        }

        private static int ClampPortraitPercentage(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static string PhysicalConfidenceProfile(int score)
        {
            score = ClampPortraitPercentage(score);
            if (score <= 20) return "00-20";
            if (score <= 40) return "21-40";
            if (score <= 60) return "41-60";
            if (score <= 80) return "61-80";
            return "81-100";
        }

        private static List<Dictionary<string, object>> PortraitPhysicalConfidencePromptMetadata()
        {
            return new List<Dictionary<string, object>>
            {
                PromptMeta(PortraitPhysicalConfidencePromptFileNames[0], "Female Physical Confidence 00-20", "image", true, "Female clothing modifier for a physical_confidence score from 0 through 20."),
                PromptMeta(PortraitPhysicalConfidencePromptFileNames[1], "Female Physical Confidence 21-40", "image", true, "Female clothing modifier for a physical_confidence score from 21 through 40."),
                PromptMeta(PortraitPhysicalConfidencePromptFileNames[2], "Female Physical Confidence 41-60", "image", true, "Female clothing modifier for a physical_confidence score from 41 through 60."),
                PromptMeta(PortraitPhysicalConfidencePromptFileNames[3], "Female Physical Confidence 61-80", "image", true, "Female clothing modifier for a physical_confidence score from 61 through 80."),
                PromptMeta(PortraitPhysicalConfidencePromptFileNames[4], "Female Physical Confidence 81-100", "image", true, "Female clothing modifier for a physical_confidence score from 81 through 100.")
            };
        }

        private static Dictionary<string, string> PortraitPhysicalConfidencePromptDefaults()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PortraitPhysicalConfidencePromptFileNames[0]] =
@"FEMALE PHYSICAL CONFIDENCE MODIFIER - 00 TO 20
Computed physical_confidence score: [PHYSICAL CONFIDENCE] out of 100.
Respect and fully incorporate every other prompt’s clothing style, fabrics, cultural descriptions, ornamentation, and overall design for the woman. Apply this override only to revealingness. Tailor the outfit (within the given style and culture) to absolute-to-extreme modesty: sealed high collar to the chin or just below, long sleeves covering the arms fully to the wrists, multiple opaque layers or heavy draping that completely hide body shape and silhouette, floor-length closed skirts or gowns with zero slits or openings. Only face and hands may be visible. Zero skin, zero figure, zero allure from any cut or gap. All other elements remain free and must be used.",
                [PortraitPhysicalConfidencePromptFileNames[1]] =
@"FEMALE PHYSICAL CONFIDENCE MODIFIER - 21 TO 40
Computed physical_confidence score: [PHYSICAL CONFIDENCE] out of 100.
Respect and fully incorporate every other prompt’s clothing style, fabrics, cultural descriptions, ornamentation, and overall design for the woman. Apply this override only to revealingness. Tailor the outfit (within the given style and culture) to near-full modest coverage with the mildest possible opening: neckline at or just below the base of the throat/collarbones (soft square, boat, or high-V), full length sleeves to the wrists that may sit slightly closer to the arm, opaque fabrics that allow only a soft natural silhouette to show under structured or draped layers, floor-length skirts with no openings. Face, minimal neck, and hands only. Proper court coverage with slight shape awareness allowed. All other elements remain free and must be used.",
                [PortraitPhysicalConfidencePromptFileNames[2]] =
@"FEMALE PHYSICAL CONFIDENCE MODIFIER - 41 TO 60
Computed physical_confidence score: [PHYSICAL CONFIDENCE] out of 100.
Respect and fully incorporate every other prompt’s clothing style, fabrics, cultural descriptions, ornamentation, and overall design for the woman. Apply this override only to revealingness. Tailor the outfit (within the given style and culture) more toward moderate revealingness: lowered neckline that clearly shows upper chest and soft cleavage, sleeves shortened to the elbow or gently off the shoulder if the style permits, closer-fitted bodice that contours breasts and waist while remaining elegant, floor-length skirts that may include structured movement or low side slits. Shoulders may be lightly bare or softly covered. Clear limited skin and defined figure, yet still court-appropriate. All other elements remain free and must be used.",
                [PortraitPhysicalConfidencePromptFileNames[3]] =
@"FEMALE PHYSICAL CONFIDENCE MODIFIER - 61 TO 80
Computed physical_confidence score: [PHYSICAL CONFIDENCE] out of 100.
Respect and fully incorporate every other prompt’s clothing style, fabrics, cultural descriptions, ornamentation, and overall design for the woman. Apply this override only to revealingness. Tailor the outfit (within the given style and culture) toward high revealingness: deep plunging neckline with pronounced cleavage, bare shoulders and most of the arms (very short sleeves, off-shoulder, or none), tightly form-fitting construction that sculpts every curve of the torso and hips, long skirts featuring high slits that openly bare the legs to mid-thigh or higher when standing or moving. Open back or side panels allowed if they fit the cultural style. Substantial skin and body emphasis under ornate court presentation. All other elements remain free and must be used.",
                [PortraitPhysicalConfidencePromptFileNames[4]] =
@"FEMALE PHYSICAL CONFIDENCE MODIFIER - 81 TO 100
Computed physical_confidence score: [PHYSICAL CONFIDENCE] out of 100.
[CLOTHING REVEAL OVERRIDE 81-100]: Respect and fully incorporate every other prompt’s clothing style, fabrics, cultural descriptions, ornamentation, and overall design for the woman. Apply this override only to revealingness. Tailor the outfit (within the given style and culture) to almost scandalous revealingness: extremely strategic and minimal fabric used only for luxury panels or accents, neckline plunging nearly to the navel or lower, fully bare arms, shoulders, upper torso, sides, and midriff, ultra-high slits that reach the hips and expose the full length of the legs, open or completely bare back, and possible sheer cut-outs or translucent overlays over any remaining areas. Maximum deliberate skin and body display while the pieces still read as extravagant medieval royal court attire of the given culture and style. All other elements remain free and must be used.",
            };
        }
    }
}
