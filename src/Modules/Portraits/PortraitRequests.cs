using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        internal static Dictionary<string, object> PortraitRegister(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string characterObjectId = ReadString(payload, "characterObjectId", "");
            string cacheKey = ReadString(payload, "cacheKey", "");
            string filePath = ReadString(payload, "filePath", "");
            string source = ReadString(payload, "source", "game");
            if (string.IsNullOrWhiteSpace(heroId))
            {
                heroId = DialogueService.ResolveHeroIdByCharacterObjectId(campaignId, characterObjectId);
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "portrait filePath was not found." };
            }

            string safeFileName = source == "source" ? "source.png" : "portrait.png";
            string destination;
            bool resolved = !string.IsNullOrWhiteSpace(heroId);
            if (resolved)
            {
                destination = Program.CharacterFile(campaignId, heroId, "portraits", safeFileName);
            }
            else
            {
                destination = Path.Combine(Program.CampaignDirectory(campaignId), "portraits", "unresolved", Program.SafePathSegment(cacheKey, "unknown"), safeFileName);
            }

            lock (Program.FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(filePath, destination, true);
            }

            Dictionary<string, object> meta = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId ?? "",
                ["characterObjectId"] = characterObjectId ?? "",
                ["cacheKey"] = cacheKey ?? "",
                ["source"] = source,
                ["originalPath"] = filePath,
                ["storedPath"] = destination,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };

            if (resolved)
            {
                Program.WriteJsonObject(Program.CharacterFile(campaignId, heroId, "portraits", "portrait.json"), meta);
            }
            else
            {
                Program.WriteJsonObject(Path.Combine(Path.GetDirectoryName(destination), "portrait.json"), meta);
            }

            Program.LogOperational("portrait.register", meta);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId ?? "",
                ["resolved"] = resolved,
                ["path"] = destination
            };
        }

        internal static Dictionary<string, object> PortraitStatus(Dictionary<string, object> payload, Dictionary<string, string> query)
        {
            payload = payload ?? new Dictionary<string, object>();
            query = query ?? new Dictionary<string, string>();
            string campaignId = ReadString(payload, "campaignId", query.ContainsKey("campaignId") ? query["campaignId"] : "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            if (string.IsNullOrWhiteSpace(heroId) && query.ContainsKey("heroStringId"))
            {
                heroId = query["heroStringId"];
            }

            if (string.IsNullOrWhiteSpace(heroId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            }

            string portraitPath = Program.CharacterFile(campaignId, heroId, "portraits", "portrait.png");
            string metaPath = Program.CharacterFile(campaignId, heroId, "portraits", "portrait.json");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["exists"] = File.Exists(portraitPath),
                ["portraitPath"] = portraitPath,
                ["metadata"] = Program.ReadJsonObject(metaPath)
            };
        }

        internal static Dictionary<string, object> PortraitDiagnose(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string characterObjectId = ReadString(payload, "characterObjectId", "");
            string cacheKey = ReadString(payload, "cacheKey", "");
            if (string.IsNullOrWhiteSpace(heroId))
            {
                heroId = DialogueService.ResolveHeroIdByCharacterObjectId(campaignId, characterObjectId);
            }

            string characterDir = string.IsNullOrWhiteSpace(heroId) ? "" : Program.CharacterDirectory(campaignId, heroId);
            string portraitDir = string.IsNullOrWhiteSpace(characterDir) ? "" : Path.Combine(characterDir, "portraits");
            string customPath = string.IsNullOrWhiteSpace(portraitDir) ? "" : Path.Combine(portraitDir, "custom.png");
            string portraitPath = string.IsNullOrWhiteSpace(portraitDir) ? "" : Path.Combine(portraitDir, "portrait.png");
            string sourcePath = string.IsNullOrWhiteSpace(portraitDir) ? "" : Path.Combine(portraitDir, "source.png");
            string cacheCampaignDir = PersistenceService.CampaignPortraitCacheDirectory(campaignId);
            string cacheFolder = string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(cacheCampaignDir) ? "" : Path.Combine(cacheCampaignDir, cacheKey);
            string cacheCustomPath = string.IsNullOrWhiteSpace(cacheFolder) ? "" : Path.Combine(cacheFolder, "custom.png");
            string cachePortraitPath = string.IsNullOrWhiteSpace(cacheFolder) ? "" : Path.Combine(cacheFolder, "portrait.png");
            string cacheSourcePath = string.IsNullOrWhiteSpace(cacheFolder) ? "" : Path.Combine(cacheFolder, "source.png");
            string cachePromptPath = string.IsNullOrWhiteSpace(cacheFolder) ? "" : Path.Combine(cacheFolder, "prompt.txt");
            string indexPath = string.IsNullOrWhiteSpace(cacheCampaignDir) ? "" : Path.Combine(cacheCampaignDir, "index.json");
            int indexMatchCount = 0;
            try
            {
                if (File.Exists(indexPath) && !string.IsNullOrWhiteSpace(cacheKey))
                {
                    Dictionary<string, object> index = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(indexPath, Encoding.UTF8)) ?? new Dictionary<string, object>();
                    indexMatchCount = index.Values.Count(x => string.Equals(Convert.ToString(x), cacheKey, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch
            {
                indexMatchCount = -1;
            }

            string preferredPath = "";
            if (!string.IsNullOrWhiteSpace(cacheCustomPath) && File.Exists(cacheCustomPath))
            {
                preferredPath = cacheCustomPath;
            }
            else if (!string.IsNullOrWhiteSpace(cachePortraitPath) && File.Exists(cachePortraitPath))
            {
                preferredPath = cachePortraitPath;
            }
            else if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                preferredPath = customPath;
            }
            else if (!string.IsNullOrWhiteSpace(portraitPath) && File.Exists(portraitPath))
            {
                preferredPath = portraitPath;
            }

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId ?? "",
                ["characterObjectId"] = characterObjectId ?? "",
                ["cacheKey"] = cacheKey ?? "",
                ["dataDir"] = Program.DataDir,
                ["moduleDir"] = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent()?.ModuleRoot ?? "",
                ["cacheCampaignDir"] = cacheCampaignDir,
                ["cacheCampaignDirExists"] = !string.IsNullOrWhiteSpace(cacheCampaignDir) && Directory.Exists(cacheCampaignDir),
                ["cacheFolder"] = cacheFolder,
                ["cacheFolderExists"] = !string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder),
                ["cacheCustomPath"] = cacheCustomPath,
                ["cacheCustomExists"] = !string.IsNullOrWhiteSpace(cacheCustomPath) && File.Exists(cacheCustomPath),
                ["cachePortraitPath"] = cachePortraitPath,
                ["cachePortraitExists"] = !string.IsNullOrWhiteSpace(cachePortraitPath) && File.Exists(cachePortraitPath),
                ["cacheSourcePath"] = cacheSourcePath,
                ["cacheSourceExists"] = !string.IsNullOrWhiteSpace(cacheSourcePath) && File.Exists(cacheSourcePath),
                ["cachePromptPath"] = cachePromptPath,
                ["cachePromptExists"] = !string.IsNullOrWhiteSpace(cachePromptPath) && File.Exists(cachePromptPath),
                ["indexPath"] = indexPath,
                ["indexExists"] = !string.IsNullOrWhiteSpace(indexPath) && File.Exists(indexPath),
                ["indexMatchCount"] = indexMatchCount,
                ["preferredPath"] = preferredPath,
                ["preferredExists"] = !string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath),
                ["textureFactoryReported"] = payload.ContainsKey("textureFactoryHas"),
                ["textureFactoryHas"] = ReadBool(payload, "textureFactoryHas", false),
                ["activeWidgetIds"] = ReadStringList(payload, "activeWidgetIds"),
                ["characterDir"] = characterDir,
                ["characterDirExists"] = !string.IsNullOrWhiteSpace(characterDir) && Directory.Exists(characterDir),
                ["portraitDir"] = portraitDir,
                ["portraitDirExists"] = !string.IsNullOrWhiteSpace(portraitDir) && Directory.Exists(portraitDir),
                ["customPath"] = customPath,
                ["customExists"] = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath),
                ["portraitPath"] = portraitPath,
                ["portraitExists"] = !string.IsNullOrWhiteSpace(portraitPath) && File.Exists(portraitPath),
                ["sourcePath"] = sourcePath,
                ["sourceExists"] = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath),
                ["profile"] = string.IsNullOrWhiteSpace(characterDir) ? new Dictionary<string, object>() : Program.ReadJsonObject(Path.Combine(characterDir, "profile.json")),
                ["metadata"] = string.IsNullOrWhiteSpace(portraitDir) ? new Dictionary<string, object>() : Program.ReadJsonObject(Path.Combine(portraitDir, "portrait.json"))
            };
            Program.LogOperational("portrait.diagnose", response);
            return response;
        }

        internal static Dictionary<string, object> PortraitRequest(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string cacheKey = ReadString(payload, "cacheKey", "");
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["cacheKey"] = cacheKey,
                ["payload"] = payload
            };
            Program.AppendJsonLineToPath(Path.Combine(Program.CampaignDirectory(campaignId), "portraits", "requests.jsonl"), row);
            Program.LogOperational("portrait.request", row);
            return new Dictionary<string, object> { ["ok"] = true, ["request"] = row };
        }
    }
}
