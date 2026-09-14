using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PreparedTavernSourceSchema = "reign-prepared-tavern-native-source-v1";
        private const string PreparedTavernPackSchema = "reign-prepared-tavern-native-pack-v1";
        private const int PreparedTavernMaximumSourceBytes = 10 * 1024 * 1024;
        private static readonly object PreparedTavernImportLock = new object();
        private static string PreparedTavernStore => Path.Combine(DataDir, "native_portrait_sources", "tavern");

        private sealed class PreparedTavernSource
        {
            public string CastId;
            public byte[] Source;
            public Dictionary<string, object> Receipt;
            public Dictionary<string, object> Request;
        }

        private static bool TryRunPreparedTavernSourceCommand(string[] args)
        {
            if (!HasArg(args, "--import-tavern-native-sources")) return false;
            try
            {
                string manifest = ArgValue(args, "--import-tavern-native-sources", "");
                string campaign = ArgValue(args, "--pack-campaign", "");
                if (manifest == "help" || manifest == "--help")
                {
                    Console.WriteLine("--import-tavern-native-sources <absolute native-source-manifest.json> --pack-campaign tavern_pack_<task-id>");
                    Console.WriteLine("Provider-free, non-listening import of a completed authored native batch. Validates all inputs before atomic publication. REIGN_DATA_ROOT selects the server-local source store; no shared portraits are changed. See docs/agent/TAVERN_PORTRAIT_SOURCE_PACK.md.");
                    return true;
                }
                string catalog = Path.Combine(FindVerificationSourceRoot(), "ReignBeta", "ModuleData", "reign_tavern_cast.json");
                Console.WriteLine(Json.Serialize(ImportPreparedTavernSources(manifest, campaign, catalog, PreparedTavernStore)));
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.WriteLine(Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }));
            }
            return true;
        }

        private static Dictionary<string, object> ImportPreparedTavernSources(string manifestPath, string campaign, string catalogPath, string store)
        {
            if (!Regex.IsMatch(campaign ?? "", @"^tavern_pack_[a-z0-9_-]{1,64}$"))
                throw new InvalidDataException("A dedicated tavern_pack_<task-id> campaign scope is required.");
            if (!Path.IsPathRooted(manifestPath ?? "")) throw new InvalidDataException("The native manifest path must be absolute.");
            manifestPath = Path.GetFullPath(manifestPath);
            string inputRoot = Path.GetDirectoryName(manifestPath);
            var manifest = ReadPreparedTavernJson(manifestPath, inputRoot);
            byte[] catalogBytes = File.ReadAllBytes(catalogPath);
            string catalogHash = Sha256Hex(catalogBytes);
            var catalog = Json.Deserialize<TavernHouseCastCatalog>(File.ReadAllText(catalogPath, Encoding.UTF8));
            TavernHouseRules.ValidateCatalog(catalog);
            var cast = catalog.Towns.SelectMany(t => t.People.Select(p => new { Town = t, Person = p })).ToDictionary(x => x.Person.Id, StringComparer.Ordinal);
            if (ReadString(manifest, "schema", "") != "reign-tavern-native-source-manifest-v1"
                || ReadString(manifest, "castCatalogSha256", "") != catalogHash || cast.Count != 284 || catalog.Towns.Count != 57
                || ReadInt(manifest, "characterCount", 0) != cast.Count || ReadInt(manifest, "townCount", 0) != catalog.Towns.Count
                || ReadString(manifest, "renderContract", "") != ResidentOutfitRenderContract)
                throw new InvalidDataException("The prepared manifest does not match the validated authored cast and full-outfit contract.");
            string batchPath = Path.Combine(inputRoot, "native-render-batch.json");
            var batch = ReadPreparedTavernJson(batchPath, inputRoot);
            var status = ReadPreparedTavernJson(ReadString(batch, "batchStatusPath", ""), inputRoot);
            var requests = ReadDictionaryList(batch, "requests");
            var rows = ReadDictionaryList(manifest, "rows");
            if (ReadInt(batch, "version", 0) != 3 || ReadString(status, "batchId", "") != ReadString(batch, "batchId", "")
                || string.IsNullOrWhiteSpace(ReadString(batch, "batchId", "")) || ReadString(status, "state", "") != "complete"
                || ReadInt(status, "completed", -1) != cast.Count || ReadInt(status, "total", -1) != cast.Count || ReadInt(status, "failed", -1) != 0
                || rows.Count != cast.Count || requests.Count != cast.Count
                || rows.Select(r => ReadString(r, "castId", "")).Distinct(StringComparer.Ordinal).Count() != cast.Count
                || requests.Select(r => ReadString(r, "characterId", "")).Distinct(StringComparer.Ordinal).Count() != cast.Count)
                throw new InvalidDataException("Every authored character must have exactly one completed native batch result.");
            string batchHash = Sha256Hex(File.ReadAllBytes(batchPath));
            string packId = TavernHouseHash(campaign + "|" + catalogHash + "|" + batchHash);
            var sources = new List<PreparedTavernSource>();
            long totalBytes = 0;
            foreach (var row in rows.OrderBy(r => ReadString(r, "castId", ""), StringComparer.Ordinal))
            {
                string id = ReadString(row, "castId", "");
                if (!cast.TryGetValue(id, out var authored)) throw new InvalidDataException("Unknown authored cast identity: " + id);
                var request = requests.Single(r => ReadString(r, "characterId", "") == id);
                var source = ReadPreparedTavernSource(row, request, authored.Town, authored.Person, inputRoot, campaign, packId, catalogHash, batchHash);
                totalBytes += source.Source.Length;
                if (totalBytes > 512L * 1024 * 1024) throw new InvalidDataException("The native pack exceeds its bounded 512 MiB import limit.");
                sources.Add(source);
            }
            // Nothing is published until every source, native result, identity and frame has passed.
            return PublishPreparedTavernSources(store, packId, campaign, catalogHash, batchHash, sources);
        }

        private static PreparedTavernSource ReadPreparedTavernSource(Dictionary<string, object> row, Dictionary<string, object> request,
            TavernHouseCastTown town, TavernHouseCastPerson cast, string inputRoot, string campaign, string packId, string catalogHash, string batchHash)
        {
            string snapshotPath = ReadString(row, "snapshotPath", ""), sourcePath = ReadString(row, "sourcePath", ""), resultPath = ReadString(row, "resultPath", "");
            var snapshot = ReadPreparedTavernJson(snapshotPath, inputRoot);
            var result = ReadPreparedTavernJson(resultPath, inputRoot);
            if (Path.GetFullPath(ReadString(request, "statusPath", "")) != Path.GetFullPath(resultPath))
                throw new InvalidDataException("The native row does not point to its own batch result.");
            AssertPreparedTavernPath(sourcePath, inputRoot);
            if (Sha256Hex(File.ReadAllBytes(snapshotPath)) != ReadString(row, "snapshotSha256", "")) throw new InvalidDataException("Native snapshot hash changed: " + cast.Id);
            ValidatePreparedTavernBinding(snapshot, request, result, cast, town.CultureId, sourcePath);
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length < 32 || info.Length > PreparedTavernMaximumSourceBytes) throw new InvalidDataException("Native source size is invalid.");
            byte[] source = File.ReadAllBytes(sourcePath);
            RequirePortraitSourceContract(source, "Prepared tavern native source");
            string sourceHash = Sha256Hex(source);
            var rebound = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(snapshot));
            rebound["campaignId"] = campaign;
            var physique = new Dictionary<string, object> { ["schema"] = "reign-native-physique-v1", ["weight"] = cast.BodyWeight, ["build"] = cast.BodyBuild,
                ["weightSource"] = "validated_authored_native_batch", ["buildSource"] = "validated_authored_native_batch", ["sourceSha256"] = sourceHash };
            ValidateNativePhysique(physique, source);
            string id = packId + "/" + cast.Id;
            var receipt = new Dictionary<string, object> { ["schema"] = PreparedTavernSourceSchema, ["preparedNativeSourceId"] = id,
                ["campaignId"] = campaign, ["heroStringId"] = cast.Id, ["catalogSha256"] = catalogHash, ["batchSha256"] = batchHash,
                ["requestSha256"] = PreparedTavernJsonHash(request), ["resultSha256"] = PreparedTavernJsonHash(result),
                ["originalSnapshotSha256"] = ReadString(row, "snapshotSha256", ""), ["snapshotSha256"] = PreparedTavernJsonHash(rebound),
                ["nativeCharacterSnapshot"] = rebound, ["sourceSha256"] = sourceHash, ["renderContract"] = ResidentOutfitRenderContract,
                ["renderContractHash"] = PreparedTavernFrameHash(), ["width"] = 768, ["height"] = 1024, ["physique"] = physique };
            var payload = new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = cast.Id, ["characterObjectId"] = cast.Id,
                ["cacheKey"] = cast.Name.Replace(' ', '_') + " (" + cast.Id + ")", ["name"] = cast.Name, ["cultureId"] = town.CultureId,
                ["age"] = cast.Age, ["isFemale"] = cast.Female, ["promptPurpose"] = "portrait", ["sharedCacheOutput"] = false,
                ["preparedNativeSourceId"] = id, ["nativeCharacterSnapshot"] = rebound };
            return new PreparedTavernSource { CastId = cast.Id, Source = source, Receipt = receipt, Request = payload };
        }

        private static void ValidatePreparedTavernBinding(Dictionary<string, object> snapshot, Dictionary<string, object> request,
            Dictionary<string, object> result, TavernHouseCastPerson cast, string culture, string sourcePath)
        {
            if (ReadString(snapshot, "schema", "") != "reign-native-portrait-snapshot-v1" || string.IsNullOrWhiteSpace(ReadString(snapshot, "campaignId", ""))
                || ReadString(snapshot, "heroStringId", "") != cast.Id || ReadString(snapshot, "characterObjectId", "") != cast.Id
                || ReadString(snapshot, "name", "") != cast.Name || ReadString(snapshot, "cultureId", "") != culture
                || ReadBool(snapshot, "isFemale", !cast.Female) != cast.Female || ReadDouble(snapshot, "age", -1) != cast.Age
                || ReadString(snapshot, "bodyKey", "") != cast.BodyKey || NativePhysiqueUnit(snapshot, "bodyWeight") != cast.BodyWeight
                || NativePhysiqueUnit(snapshot, "bodyBuild") != cast.BodyBuild || ReadString(snapshot, "portraitSourceProfile", "") != ResidentOutfitRenderContract)
                throw new InvalidDataException("The native snapshot differs from the authored adult identity.");
            var equipment = ReadDictionaryList(snapshot, "civilianEquipment");
            if (equipment.Count != cast.CivilianEquipment.Count || equipment.Select(e => ReadString(e, "slot", "")).Distinct().Count() != equipment.Count
                || cast.CivilianEquipment.Any(e => !equipment.Any(s => ReadString(s, "slot", "") == e.Slot && ReadString(s, "itemId", "") == e.ItemId
                    && string.IsNullOrEmpty(ReadString(s, "modifierId", "")))))
                throw new InvalidDataException("The native snapshot outfit differs from the authored equipment.");
            var slots = new Dictionary<string, int> { ["Body"] = 6, ["Leg"] = 7, ["Gloves"] = 8, ["Cape"] = 9 };
            string expectedEquipment = string.Concat(cast.CivilianEquipment.OrderBy(e => slots[e.Slot]).Select(e => "+" + slots[e.Slot] + "-" + e.ItemId + "-@null"));
            if (ReadInt(request, "version", 0) != 4 || ReadString(request, "characterId", "") != cast.Id || ReadString(request, "characterObjectId", "") != cast.Id
                || !string.IsNullOrEmpty(ReadString(request, "characterCode", "")) || !string.IsNullOrEmpty(ReadString(request, "faceTemplateId", ""))
                || ReadString(request, "appearanceMode", "") != "native_authored" || ReadString(request, "renderPreset", "") != ResidentOutfitRenderContract
                || ReadString(request, "cultureId", "") != culture || ReadBool(request, "isFemale", !cast.Female) != cast.Female || ReadInt(request, "race", -1) != 0
                || ReadString(request, "equipmentCode", "") != expectedEquipment || !ReadBool(request, "preserveEncounteredOutfit", false)
                || ReadInt(request, "width", 0) != 1536 || ReadInt(request, "height", 0) != 2048 || ReadInt(request, "outputWidth", 0) != 768 || ReadInt(request, "outputHeight", 0) != 1024
                || ReadDouble(request, "cameraCropScale", -1) != 1 || ReadDouble(request, "cameraCenterYRatio", -1) != .5 || ReadDouble(request, "cameraPitchDegrees", -1) != 0
                || ReadInt(result, "version", 0) != 4 || ReadString(result, "state", "") != "complete" || string.IsNullOrWhiteSpace(ReadString(request, "jobId", ""))
                || ReadString(result, "jobId", "") != ReadString(request, "jobId", "")
                || Path.GetFullPath(ReadString(request, "outputPath", "")) != Path.GetFullPath(sourcePath)
                || Path.GetFullPath(ReadString(result, "outputPath", "")) != Path.GetFullPath(sourcePath))
                throw new InvalidDataException("The native batch request/result does not prove the exact full-outfit source.");
            foreach (string body in new[] { ReadString(request, "bodyProperties", ""), ReadString(result, "effectiveBodyProperties", "") })
            {
                var xml = XElement.Parse(body);
                if (xml.Name != "BodyProperties" || (string)xml.Attribute("key") != cast.BodyKey
                    || ParsePreparedTavernNumber(xml, "age") != cast.Age || ParsePreparedTavernNumber(xml, "weight") != cast.BodyWeight || ParsePreparedTavernNumber(xml, "build") != cast.BodyBuild)
                    throw new InvalidDataException("Rendered native body and physique differ from the authored snapshot.");
            }
        }

        private static double ParsePreparedTavernNumber(XElement xml, string key)
        {
            if (!double.TryParse((string)xml.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException("Invalid rendered body field: " + key);
            return value;
        }

        private static Dictionary<string, object> PublishPreparedTavernSources(string store, string packId, string campaign, string catalogHash, string batchHash, List<PreparedTavernSource> sources)
        {
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                files.Add(source.CastId + "/source.png", source.Source);
                files.Add(source.CastId + "/receipt.json", Encoding.UTF8.GetBytes(Json.Serialize(source.Receipt)));
                files.Add(source.CastId + "/request.json", Encoding.UTF8.GetBytes(Json.Serialize(source.Request)));
            }
            var inventory = files.Select(f => new Dictionary<string, object> { ["path"] = f.Key, ["sha256"] = Sha256Hex(f.Value) }).ToList();
            files.Add("pack.json", Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["schema"] = PreparedTavernPackSchema,
                ["packId"] = packId, ["campaignId"] = campaign, ["catalogSha256"] = catalogHash, ["batchSha256"] = batchHash, ["files"] = inventory })));
            string destination = Path.Combine(store, packId);
            lock (PreparedTavernImportLock)
            {
                AssertPreparedTavernPath(destination, store);
                if (Directory.Exists(destination))
                {
                    foreach (var file in files)
                    {
                        string path = Path.Combine(destination, file.Key); AssertPreparedTavernPath(path, store);
                        if (!File.Exists(path) || Sha256Hex(File.ReadAllBytes(path)) != Sha256Hex(file.Value)) throw new InvalidDataException("An existing prepared pack conflicts with this import.");
                    }
                }
                else
                {
                    Directory.CreateDirectory(store);
                    string incoming = Path.Combine(store, ".incoming-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(incoming);
                        foreach (var file in files)
                        {
                            string path = Path.Combine(incoming, file.Key); AssertPreparedTavernPath(path, store);
                            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, file.Value);
                        }
                        Directory.Move(incoming, destination);
                    }
                    finally { if (Directory.Exists(incoming)) { AssertPreparedTavernPath(incoming, store); Directory.Delete(incoming, true); } }
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["packId"] = packId, ["campaignId"] = campaign, ["count"] = sources.Count,
                ["packPath"] = destination, ["requests"] = sources.Select(s => Path.Combine(destination, s.CastId, "request.json")).ToList() };
        }

        private static byte[] ResolvePreparedTavernSource(Dictionary<string, object> payload, string store, out Dictionary<string, object> provenance)
        {
            provenance = null;
            string id = ReadString(payload, "preparedNativeSourceId", "");
            if (!Regex.IsMatch(id, @"^[0-9a-f]{64}/reign_tavern_[A-Za-z0-9_]+$")) throw new InvalidDataException("Invalid prepared native source ID.");
            if (ReadString(payload, "promptPurpose", "portrait") != "portrait" || ReadBool(payload, "sharedCacheOutput", false))
                throw new InvalidDataException("Prepared tavern sources are confined to isolated portrait requests.");
            string packId = id.Split('/')[0], castId = id.Split('/')[1];
            string root = Path.Combine(store, packId), path = Path.Combine(root, castId, "source.png");
            var pack = ReadPreparedTavernJson(Path.Combine(root, "pack.json"), store);
            var receipt = ReadPreparedTavernJson(Path.Combine(root, castId, "receipt.json"), store);
            var snapshot = ReadDictionary(payload, "nativeCharacterSnapshot");
            string campaign = ReadString(payload, "campaignId", "");
            if (ReadString(pack, "schema", "") != PreparedTavernPackSchema || ReadString(pack, "packId", "") != packId
                || ReadString(receipt, "schema", "") != PreparedTavernSourceSchema || ReadString(receipt, "preparedNativeSourceId", "") != id
                || !Regex.IsMatch(campaign, @"^tavern_pack_[a-z0-9_-]{1,64}$") || ReadString(pack, "campaignId", "") != campaign
                || ReadString(receipt, "campaignId", "") != campaign || ReadFirstString(payload, "heroStringId", "heroId", "characterId") != castId
                || ReadString(receipt, "heroStringId", "") != castId || snapshot == null || ReadString(snapshot, "campaignId", "") != campaign
                || ReadString(snapshot, "heroStringId", "") != castId || PreparedTavernJsonHash(snapshot) != ReadString(receipt, "snapshotSha256", "")
                || ReadString(snapshot, "portraitSourceProfile", "") != ResidentOutfitRenderContract || ReadString(receipt, "renderContract", "") != ResidentOutfitRenderContract
                || ReadString(receipt, "renderContractHash", "") != PreparedTavernFrameHash())
                throw new InvalidDataException("The prepared native source does not match this exact portrait snapshot and scope.");
            AssertPreparedTavernPath(path, store);
            var inventory = ReadDictionaryList(pack, "files");
            string receiptHash = Sha256Hex(File.ReadAllBytes(Path.Combine(root, castId, "receipt.json")));
            if (!inventory.Any(f => ReadString(f, "path", "") == castId + "/receipt.json" && ReadString(f, "sha256", "") == receiptHash))
                throw new InvalidDataException("The prepared native receipt changed after pack publication.");
            if (new FileInfo(path).Length > PreparedTavernMaximumSourceBytes) throw new InvalidDataException("Prepared native source exceeds its size bound.");
            byte[] source = File.ReadAllBytes(path); string hash = Sha256Hex(source);
            if (hash != ReadString(receipt, "sourceSha256", "") || !inventory.Any(f => ReadString(f, "path", "") == castId + "/source.png" && ReadString(f, "sha256", "") == hash))
                throw new InvalidDataException("The prepared native image changed after import.");
            RequirePortraitSourceContract(source, "Prepared tavern source");
            var physique = ValidateNativePhysique(ReadDictionary(receipt, "physique"), source);
            if (NativePhysiqueUnit(physique, "weight") != NativePhysiqueUnit(snapshot, "bodyWeight") || NativePhysiqueUnit(physique, "build") != NativePhysiqueUnit(snapshot, "bodyBuild"))
                throw new InvalidDataException("Prepared source physique differs from the current snapshot.");
            provenance = new Dictionary<string, object> { ["kind"] = "validated_prepared_tavern_native_batch", ["preparedNativeSourceId"] = id,
                ["renderContract"] = ResidentOutfitRenderContract, ["renderContractHash"] = PreparedTavernFrameHash(), ["appearanceSource"] = "request_snapshot",
                ["appearanceSha256"] = PreparedTavernJsonHash(snapshot), ["catalogSha256"] = ReadString(receipt, "catalogSha256", ""),
                ["batchSha256"] = ReadString(receipt, "batchSha256", ""), ["requestSha256"] = ReadString(receipt, "requestSha256", ""),
                ["resultSha256"] = ReadString(receipt, "resultSha256", ""), ["width"] = 768, ["height"] = 1024, ["physique"] = physique };
            return source;
        }

        private static string PreparedTavernFrameHash() => TavernHouseHash(ResidentOutfitRenderContract + "|1536|2048|768|1024|1|0.5|0|preserve_native_outfit|native_authored|v4");
        private static string PreparedTavernJsonHash(object value) => TavernHouseHash(Json.Serialize(CanonicalPreparedTavernJson(value)));
        private static object CanonicalPreparedTavernJson(object value)
        {
            if (value is IDictionary dictionary)
            {
                var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary) sorted[(string)entry.Key] = CanonicalPreparedTavernJson(entry.Value);
                return sorted;
            }
            if (value is IEnumerable items && !(value is string)) return items.Cast<object>().Select(CanonicalPreparedTavernJson).ToArray();
            return value;
        }
        private static Dictionary<string, object> ReadPreparedTavernJson(string path, string root)
        {
            AssertPreparedTavernPath(path, root);
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("A bounded prepared native JSON artifact is missing.");
            return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new InvalidDataException("Prepared native JSON is empty.");
        }
        private static void AssertPreparedTavernPath(string path, string root)
        {
            if (!Path.IsPathRooted(path ?? "") || !Path.IsPathRooted(root ?? "")) throw new InvalidDataException("Prepared native artifact paths must be absolute.");
            string full = Path.GetFullPath(path), basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full != basePath && !full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Prepared native artifact escaped its confined root.");
            string current = full;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Prepared native artifacts cannot traverse reparse points.");
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
