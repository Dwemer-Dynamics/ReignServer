using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ReignBeta.Shared.Characters;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPreparedTavernPortraitSourceTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            bool Rejects(Action operation) { try { operation(); return false; } catch (InvalidDataException) { return true; } }
            void Check(string name, Action test)
            {
                try { test(); results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = true, ["caseId"] = "prepared_tavern_" + name, ["summary"] = name }); }
                catch (Exception ex) { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = false, ["caseId"] = "prepared_tavern_" + name, ["summary"] = ex.ToString() }); }
            }
            string catalogPath = Path.Combine(FindVerificationSourceRoot(), "ReignBeta", "ModuleData", "reign_tavern_cast.json");
            var catalog = Json.Deserialize<TavernHouseCastCatalog>(File.ReadAllText(catalogPath));
            var town = catalog.Towns[0]; var cast = town.People[0];
            string root = Path.Combine(Path.GetTempPath(), "reign-prepared-tavern-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var rgba = new byte[768 * 1024 * 4];
                for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
                byte[] png = PngEncoder.EncodeRgba(rgba, 768, 1024);
                var fixture = WritePreparedTavernFixture(root, town, cast, png);
                var snapshot = ReadPreparedTavernJson(ReadString(fixture.Row, "snapshotPath", ""), root);
                var nativeResult = ReadPreparedTavernJson(ReadString(fixture.Row, "resultPath", ""), root);
                Dictionary<string, object> Copy(Dictionary<string, object> value) => Json.Deserialize<Dictionary<string, object>>(Json.Serialize(value));
                Check("native_body_outfit_and_frame_binding", () =>
                {
                    ValidatePreparedTavernBinding(snapshot, fixture.Request, nativeResult, cast, town.CultureId, fixture.SourcePath);
                    foreach (string field in new[] { "heroStringId", "bodyKey", "portraitSourceProfile", "cultureId" })
                    {
                        var wrong = Copy(snapshot); wrong[field] = "different";
                        Require(Rejects(() => ValidatePreparedTavernBinding(wrong, fixture.Request, nativeResult, cast, town.CultureId, fixture.SourcePath)), "Changed snapshot field passed: " + field);
                    }
                    var age = Copy(snapshot); age["age"] = 17;
                    Require(Rejects(() => ValidatePreparedTavernBinding(age, fixture.Request, nativeResult, cast, town.CultureId, fixture.SourcePath)), "Minor snapshot passed.");
                    var outfit = Copy(snapshot); outfit["civilianEquipment"] = new object[0];
                    Require(Rejects(() => ValidatePreparedTavernBinding(outfit, fixture.Request, nativeResult, cast, town.CultureId, fixture.SourcePath)), "Missing native clothing passed.");
                    var body = Copy(nativeResult); body["effectiveBodyProperties"] = ReadString(nativeResult, "effectiveBodyProperties", "").Replace(cast.BodyKey, new string('0', 128));
                    Require(Rejects(() => ValidatePreparedTavernBinding(snapshot, fixture.Request, body, cast, town.CultureId, fixture.SourcePath)), "Different effective native face passed.");
                    foreach (string field in new[] { "width", "height", "outputWidth", "outputHeight", "cameraCropScale", "cameraCenterYRatio", "cameraPitchDegrees" })
                    {
                        var frame = Copy(fixture.Request); frame[field] = -1;
                        Require(Rejects(() => ValidatePreparedTavernBinding(snapshot, frame, nativeResult, cast, town.CultureId, fixture.SourcePath)), "Different frame passed: " + field);
                    }
                    var unfinished = Copy(nativeResult); unfinished["state"] = "running";
                    Require(Rejects(() => ValidatePreparedTavernBinding(snapshot, fixture.Request, unfinished, cast, town.CultureId, fixture.SourcePath)), "Unfinished result passed.");
                });
                string store = Path.Combine(root, "store"), packId = new string('a', 64), campaign = "tavern_pack_contract";
                PreparedTavernSource source = ReadPreparedTavernSource(fixture.Row, fixture.Request, town, cast, root, campaign, packId, "catalog", "batch");
                Check("source_reuse_scope_physique_and_integrity", () =>
                {
                    PublishPreparedTavernSources(store, packId, campaign, "catalog", "batch", new List<PreparedTavernSource> { source });
                    byte[] reused = ResolvePreparedTavernSource(source.Request, store, out var provenance);
                    Require(reused.SequenceEqual(png) && ReadString(provenance, "renderContract", "") == ResidentOutfitRenderContract, "Imported bytes or full-outfit provenance changed.");
                    Require(NativePhysiqueUnit(ReadDictionary(provenance, "physique"), "weight") == cast.BodyWeight && PreserveResidentClothing(source.Request), "Imported native physique or clothing policy was lost.");
                    foreach (string field in new[] { "campaignId", "heroStringId", "promptPurpose" })
                    {
                        var bad = Copy(source.Request); bad[field] = "other";
                        Require(Rejects(() => ResolvePreparedTavernSource(bad, store, out _)), "Cross-scope prepared request passed: " + field);
                    }
                    var changed = Copy(source.Request); ReadDictionary(changed, "nativeCharacterSnapshot")["bodyBuild"] = .99;
                    Require(Rejects(() => ResolvePreparedTavernSource(changed, store, out _)), "Changed physique snapshot reused native bytes.");
                    var shared = Copy(source.Request); shared["sharedCacheOutput"] = true;
                    Require(Rejects(() => ResolvePreparedTavernSource(shared, store, out _)), "Prepared source wrote into shared portraits.");
                    string imagePath = Path.Combine(store, packId, cast.Id, "source.png");
                    File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
                    Require(Rejects(() => ResolvePreparedTavernSource(source.Request, store, out _)), "Changed source bytes passed their published hash.");
                    File.WriteAllBytes(imagePath, png);
                });
                Check("atomic_publication_and_idempotent_import", () =>
                {
                    var list = new List<PreparedTavernSource> { source };
                    PublishPreparedTavernSources(store, packId, campaign, "catalog", "batch", list);
                    string receiptPath = Path.Combine(store, packId, cast.Id, "receipt.json");
                    byte[] before = File.ReadAllBytes(receiptPath);
                    PublishPreparedTavernSources(store, packId, campaign, "catalog", "batch", list);
                    Require(before.SequenceEqual(File.ReadAllBytes(receiptPath)), "Identical resume rewrote the bound receipt.");
                    source.Source = new byte[] { 1, 2, 3 };
                    Require(Rejects(() => PublishPreparedTavernSources(store, packId, campaign, "catalog", "batch", list)), "Conflicting import replaced an existing source pack.");
                    source.Source = png;
                    Require(!Directory.EnumerateDirectories(store, ".incoming-*").Any(), "An interrupted publication left a visible partial pack.");
                });
                Check("confined_identifiers_and_fail_closed", () =>
                {
                    foreach (string id in new[] { "", "../source.png", new string('a', 64) + "/../../secret", "unknown" })
                    {
                        var bad = Copy(source.Request); bad["preparedNativeSourceId"] = id;
                        Require(Rejects(() => ResolvePreparedTavernSource(bad, store, out _)), "Unsafe identifier was resolved.");
                    }
                    Require(Rejects(() => AssertPreparedTavernPath(Path.GetFullPath(Path.Combine(root, "..", "outside.json")), root)), "Source-root traversal was allowed.");
                    Require(Rejects(() => AssertPreparedTavernPath("relative.json", root)), "Relative source path was allowed.");
                    var invalid = Copy(fixture.Row); invalid["snapshotSha256"] = new string('0', 64);
                    Require(Rejects(() => ReadPreparedTavernSource(invalid, fixture.Request, town, cast, root, campaign, packId, "catalog", "batch")), "Changed source snapshot was imported.");
                });
                Check("complete_authored_batch_before_publication", () =>
                {
                    string batchRoot = Path.Combine(root, "whole-batch"); Directory.CreateDirectory(batchRoot);
                    var rows = new List<Dictionary<string, object>>(); var requests = new List<Dictionary<string, object>>();
                    foreach (var nativeTown in catalog.Towns)
                        foreach (var person in nativeTown.People)
                        {
                            var item = WritePreparedTavernFixture(batchRoot, nativeTown, person, png); rows.Add(item.Row); requests.Add(item.Request);
                        }
                    string batchStatus = Path.Combine(batchRoot, "native-render-batch-status.json");
                    File.WriteAllText(batchStatus, Json.Serialize(new Dictionary<string, object> { ["batchId"] = "test-batch", ["state"] = "complete", ["completed"] = 284, ["failed"] = 0, ["total"] = 284 }));
                    File.WriteAllText(Path.Combine(batchRoot, "native-render-batch.json"), Json.Serialize(new Dictionary<string, object> { ["version"] = 3, ["batchId"] = "test-batch", ["batchStatusPath"] = batchStatus, ["requests"] = requests }));
                    string manifest = Path.Combine(batchRoot, "native-source-manifest.json");
                    File.WriteAllText(manifest, Json.Serialize(new Dictionary<string, object> { ["schema"] = "reign-tavern-native-source-manifest-v1",
                        ["castCatalogSha256"] = Sha256Hex(File.ReadAllBytes(catalogPath)), ["characterCount"] = 284, ["townCount"] = 57, ["renderContract"] = ResidentOutfitRenderContract, ["rows"] = rows }));
                    var last = rows.Last(); string lastResult = ReadString(last, "resultPath", ""); byte[] complete = File.ReadAllBytes(lastResult);
                    var failed = ReadPreparedTavernJson(lastResult, batchRoot); failed["state"] = "failed"; File.WriteAllText(lastResult, Json.Serialize(failed));
                    string batchStore = Path.Combine(root, "batch-store");
                    Require(Rejects(() => ImportPreparedTavernSources(manifest, campaign, catalogPath, batchStore)) && !Directory.Exists(batchStore), "A failed final source published a partial pack.");
                    File.WriteAllBytes(lastResult, complete);
                    var imported = ImportPreparedTavernSources(manifest, campaign, catalogPath, batchStore);
                    Require(ReadInt(imported, "count", 0) == 284 && ReadBool(imported, "ok", false), "The exact full authored pack did not import.");
                    Require(ReadString(ImportPreparedTavernSources(manifest, campaign, catalogPath, batchStore), "packId", "") == ReadString(imported, "packId", ""), "Full import resume changed the pack ID.");
                });
            }
            finally { AssertPreparedTavernPath(root, Path.GetTempPath()); if (Directory.Exists(root)) Directory.Delete(root, true); }
            return results;
        }

        private sealed class PreparedTavernFixture
        {
            public Dictionary<string, object> Row;
            public Dictionary<string, object> Request;
            public string SourcePath;
        }
        private static PreparedTavernFixture WritePreparedTavernFixture(string root, TavernHouseCastTown town, TavernHouseCastPerson cast, byte[] png)
        {
            string directory = Path.Combine(root, cast.Id); Directory.CreateDirectory(directory);
            string snapshotPath = Path.Combine(directory, "snapshot.json"), sourcePath = Path.Combine(directory, "source.png"), resultPath = Path.Combine(directory, "result.json");
            string body = string.Format(CultureInfo.InvariantCulture, "<BodyProperties version=\"4\" age=\"{0}\" weight=\"{1}\" build=\"{2}\" key=\"{3}\" />", cast.Age, cast.BodyWeight, cast.BodyBuild, cast.BodyKey);
            var snapshot = new Dictionary<string, object> { ["schema"] = "reign-native-portrait-snapshot-v1", ["campaignId"] = "_shared", ["heroStringId"] = cast.Id, ["characterObjectId"] = cast.Id,
                ["name"] = cast.Name, ["cultureId"] = town.CultureId, ["isFemale"] = cast.Female, ["age"] = cast.Age, ["bodyKey"] = cast.BodyKey, ["bodyWeight"] = cast.BodyWeight, ["bodyBuild"] = cast.BodyBuild,
                ["portraitSourceProfile"] = ResidentOutfitRenderContract, ["civilianEquipment"] = cast.CivilianEquipment.Select(e => new Dictionary<string, object> { ["slot"] = e.Slot, ["itemId"] = e.ItemId }).ToList() };
            File.WriteAllText(snapshotPath, Json.Serialize(snapshot)); File.WriteAllBytes(sourcePath, png);
            var slots = new Dictionary<string, int> { ["Body"] = 6, ["Leg"] = 7, ["Gloves"] = 8, ["Cape"] = 9 };
            var request = new Dictionary<string, object> { ["version"] = 4, ["jobId"] = "job-" + cast.Id, ["characterId"] = cast.Id, ["characterObjectId"] = cast.Id,
                ["appearanceMode"] = "native_authored", ["cultureId"] = town.CultureId, ["isFemale"] = cast.Female, ["race"] = 0, ["bodyProperties"] = body,
                ["equipmentCode"] = string.Concat(cast.CivilianEquipment.OrderBy(e => slots[e.Slot]).Select(e => "+" + slots[e.Slot] + "-" + e.ItemId + "-@null")),
                ["renderPreset"] = ResidentOutfitRenderContract, ["width"] = 1536, ["height"] = 2048, ["outputWidth"] = 768, ["outputHeight"] = 1024,
                ["cameraCropScale"] = 1d, ["cameraCenterYRatio"] = .5d, ["cameraPitchDegrees"] = 0d, ["preserveEncounteredOutfit"] = true, ["outputPath"] = sourcePath, ["statusPath"] = resultPath };
            File.WriteAllText(resultPath, Json.Serialize(new Dictionary<string, object> { ["version"] = 4, ["jobId"] = "job-" + cast.Id, ["state"] = "complete", ["outputPath"] = sourcePath, ["effectiveBodyProperties"] = body }));
            return new PreparedTavernFixture { Request = request, SourcePath = sourcePath, Row = new Dictionary<string, object> { ["castId"] = cast.Id, ["snapshotPath"] = snapshotPath,
                ["snapshotSha256"] = Sha256Hex(File.ReadAllBytes(snapshotPath)), ["sourcePath"] = sourcePath, ["resultPath"] = resultPath } };
        }
    }
}
