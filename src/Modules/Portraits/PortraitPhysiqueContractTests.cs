using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPortraitPhysiqueContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (name, ok) => AddSharedPortraitSelfTest(rows, "physique_" + name, ok, name);
            byte[] source = { 1, 2, 3 };
            Func<double, double, Dictionary<string, object>> native = (weight, build) => new Dictionary<string, object> {
                ["schema"] = "reign-native-physique-v1", ["weight"] = weight, ["build"] = build,
                ["weightSource"] = "request_snapshot", ["buildSource"] = "native_engine_resolved", ["sourceSha256"] = Sha256Hex(source)
            };
            double[] boundaries = { 0, .199999, .2, .399999, .4, .599999, .6, .799999, .8, 1 };
            string[] weights = { "Very slim, minimal body fat", "Lean", "Medium body-fat level", "Heavyset, increased body fat", "Very heavyset, substantial body fat" };
            string[] builds = { "Low muscle mass", "Light musculature", "Moderate musculature", "Muscular", "Very muscular" };
            string[] femaleBuilds = { "Minimal muscle tone", "Lightly toned", "Moderately toned", "Athletically toned", "Highly toned and athletic" };
            check("boundaries", boundaries.Select((value, i) => PhysiqueDescription(value, false, false) == weights[i / 2]
                && PhysiqueDescription(value, true, false) == builds[i / 2]
                && PhysiqueDescription(value, true, true) == femaleBuilds[i / 2]).All(x => x));
            foreach (object invalid in new object[] { -1d, 1.01d, 74, double.NaN, double.PositiveInfinity, "0.5", null })
            {
                var data = native(.5, .5); data["weight"] = invalid;
                bool rejected = false;
                try { ValidateNativePhysique(data, source); } catch (InvalidDataException) { rejected = true; }
                check("invalid_" + (invalid ?? "null"), rejected);
            }
            bool hashRejected = false;
            try { ValidateNativePhysique(native(.3, .9), new byte[] { 4 }); } catch (InvalidDataException) { hashRejected = true; }
            check("cache_hash_binding", hashRejected && ValidateNativePhysique(native(.3, .9), source) != null);
            foreach (var values in new[] { new[] { .3, .9 }, new[] { .9, .1 } })
            {
                var evidence = BuildPortraitPhysiqueEvidence(native(values[0], values[1]), PortraitBodyPromptDefault, true);
                var payload = new Dictionary<string, object> { ["resolvedPortraitPhysique"] = evidence,
                    ["ageYears"] = 30, ["gender"] = "female", ["promptPurpose"] = "portrait",
                    ["physicalConfidence"] = new Dictionary<string, object> { ["score"] = 74 } };
                string layer = ReadString(evidence, "bodyLayer", "");
                string normal = BuildPortraitPrompt(payload, "", false), edit = BuildAdultPortraitClothingPrompt(payload);
                check("both_stages_" + values[0], normal.Contains(layer) && edit.Contains(layer)
                    && !layer.Contains("[NATIVE") && !layer.Contains("DESCRIPTION]")
                    && normal.IndexOf(layer, StringComparison.Ordinal) == normal.LastIndexOf(layer, StringComparison.Ordinal)
                    && AppendPortraitPhysique(normal, payload) == normal);
                check("receipt_roundtrip_" + values[0], Json.Serialize(evidence).Contains(ReadString(evidence, "bodyLayerSha256", ""))
                    && ReadString(evidence, "bodyLayerSha256", "") == Sha256Hex(Encoding.UTF8.GetBytes(layer)));
                var roundTrip = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(new Dictionary<string, object> { ["physique"] = evidence }));
                check("receipt_values_" + values[0], NativePhysiqueUnit(ReadDictionary(roundTrip, "physique"), "weight") == values[0]
                    && ReadString(ReadDictionary(roundTrip, "physique"), "bodyLayer", "") == layer);
                foreach (string model in AtlasImageModels.Concat(NanoImageModels))
                {
                    payload["model"] = model;
                    check("provider_layer_" + values[0] + "_" + model, BuildPortraitPrompt(payload, "", false) == normal
                        && BuildAdultPortraitClothingPrompt(payload) == edit);
                }
            }
            var femaleHighBuild = BuildPortraitPhysiqueEvidence(native(.5, .9), PortraitBodyPromptDefault, true);
            var maleHighBuild = BuildPortraitPhysiqueEvidence(native(.5, .9), PortraitBodyPromptDefault, false);
            check("sex_aware_high_build", ReadString(femaleHighBuild, "buildDescription", "") == "Highly toned and athletic"
                && ReadString(femaleHighBuild, "buildInterpretation", "") == "female_tone_and_firmness"
                && ReadString(femaleHighBuild, "bodyLayer", "").Contains("not added muscle mass, bulky shoulders, thick arms, or masculine bodybuilder proportions")
                && ReadString(maleHighBuild, "buildDescription", "") == "Very muscular"
                && ReadString(maleHighBuild, "buildInterpretation", "") == "male_muscle_mass");
            check("female_subject_alias", IsFemalePortraitSubject(new Dictionary<string, object> { ["isFemale"] = true })
                && !IsFemalePortraitSubject(new Dictionary<string, object> { ["isFemale"] = false }));
            foreach (string profile in new[] { "portrait", "adultPortrait", "scenery", "adultScenery" })
            {
                bool called = false;
                ResolveImageRequestSource(profile, source, () => { called = true; return source; });
                check("routing_" + profile, called == IsCharacterPortraitProfile(profile));
            }
            string customized = PortraitBodyPromptDefault + "\nKeep a custom garment instruction.";
            check("editable_template", ReadString(BuildPortraitPhysiqueEvidence(native(.5, .5), customized, false), "bodyLayer", "").EndsWith("Keep a custom garment instruction."));
            bool missingTokenRejected = false;
            try { BuildPortraitPhysiqueEvidence(native(.5, .5), "No body tokens", false); } catch (InvalidDataException) { missingTokenRejected = true; }
            check("missing_template_tokens_rejected", missingTokenRejected);
            check("catalog", PromptFileNames.Contains(PortraitBodyPromptFile) && DefaultPromptTemplates()[PortraitBodyPromptFile] == PortraitBodyPromptDefault);
            string fixture = Path.Combine(Path.GetTempPath(), "reign-shared-rebuild-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(fixture);
                var metadata = new Dictionary<string, object> { ["heroStringId"] = "fixture", ["gender"] = "female", ["bodyWeight"] = .3, ["bodyBuild"] = .9,
                    ["nativePhysique"] = native(.3, .9) };
                File.WriteAllText(Path.Combine(fixture, "portrait_input.json"), Json.Serialize(metadata));
                byte[] master = PngEncoder.EncodeRgba(new byte[768 * 1024 * 4], 768, 1024);
                File.WriteAllBytes(Path.Combine(fixture, "portrait.png"), master);
                var entry = LoadSharedPortraitEntry(fixture);
                check("shared_no_source_catalog", entry.HasPortrait && !entry.HasSource && string.IsNullOrEmpty(entry.Error)
                    && ReadBool(SharedPortraitEntrySnapshot(entry), "canGenerate", false));
                var bound = BindSharedPortraitPhysique(metadata, master);
                check("shared_reference_binding", ReadString(bound, "sourceSha256", "") == Sha256Hex(master)
                    && ReadString(bound, "savedSourceSha256", "") == Sha256Hex(source)
                    && ReadString(bound, "referenceKind", "") == "existing_ai_portrait");
                var request = BuildSharedPortraitGenerationPayload(entry, "NanoGPT");
                request["resolvedPortraitPhysique"] = BuildPortraitPhysiqueEvidence(bound, PortraitBodyPromptDefault, IsFemalePortraitSubject(request));
                string rebuildPrompt = BuildSharedPortraitRebuildPrompt(request);
                check("shared_preserve_outfit", rebuildPrompt.Contains("Preserve the existing clothing design")
                    && rebuildPrompt.Contains("Native weight: 0.3") && rebuildPrompt.Contains("Highly toned and athletic")
                    && !rebuildPrompt.Contains("[NATIVE"));
                foreach (string model in AtlasImageModels.Concat(NanoImageModels)) {
                    request["model"] = model;
                    check("shared_provider_" + model, BuildSharedPortraitRebuildPrompt(request) == rebuildPrompt);
                }
                metadata["bodyWeight"] = .8;
                bool mismatchRejected = false;
                try { BindSharedPortraitPhysique(metadata, master); } catch (InvalidDataException) { mismatchRejected = true; }
                check("shared_conflicting_stats_rejected", mismatchRejected);
                File.SetAttributes(Path.Combine(fixture, "portrait.png"), FileAttributes.ReadOnly);
                bool failed = false;
                try { WithSharedProductRollback(fixture, () => {
                    WriteSharedPortraitAtomic(Path.Combine(fixture, "portrait.png"), new byte[] { 99 });
                    WriteSharedPortraitAtomic(Path.Combine(fixture, "prompt.txt"), new byte[] { 88 });
                    throw new IOException("injected derivative/write failure");
                }); } catch (IOException) { failed = true; }
                check("shared_failure_preserves_product", failed && File.ReadAllBytes(Path.Combine(fixture, "portrait.png")).SequenceEqual(master)
                    && !File.Exists(Path.Combine(fixture, "prompt.txt")) && !File.Exists(Path.Combine(fixture, "source.png")));
                check("shared_readonly_preserved", (File.GetAttributes(Path.Combine(fixture, "portrait.png")) & FileAttributes.ReadOnly) != 0);
            } finally {
                foreach (string path in Directory.GetFiles(fixture)) File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(fixture, true);
            }
            return rows;
        }
    }
}
