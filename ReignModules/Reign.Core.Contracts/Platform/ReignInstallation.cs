using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Reign.Core.Contracts.Platform
{
    [DataContract]
    public sealed class ReignInstallation
    {
        public const string SchemaName = "reign-installation-v1";
        public const int SupportedProtocol = 1;
        [DataMember(Name = "schema")] public string Schema { get; set; } = string.Empty;
        [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
        [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; }
        [DataMember(Name = "contentVersion")] public string ContentVersion { get; set; } = string.Empty;
        [DataMember(Name = "serverRoot")] public string ServerRoot { get; set; } = string.Empty;
        [DataMember(Name = "contentRoot")] public string ContentRoot { get; set; } = string.Empty;
        [DataMember(Name = "dataRoot")] public string DataRoot { get; set; } = string.Empty;
        [DataMember(Name = "bannerlordRoot")] public string BannerlordRoot { get; set; } = string.Empty;
        [DataMember(Name = "moduleRoot")] public string ModuleRoot { get; set; } = string.Empty;
        [DataMember(Name = "postgresBin")] public string PostgresBin { get; set; } = string.Empty;
        [DataMember(Name = "postgresPort")] public int PostgresPort { get; set; }

        public string PortraitCacheRoot => Path.Combine(DataRoot, "PortraitCache");
        public string SharedPortraitRoot => Path.Combine(ContentRoot, "PortraitCache", "_shared");

        // Validation must never discover and attach to the user's installed state.
        // A caller testing the record itself uses ReadFrom with an isolated path.
        public static ReignInstallation? TryLoadCurrent()
        {
            return TryLoad(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("REIGN_INSTALLATION_FILE"),
                Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1");
        }

        // Outside AppData: an MSIX-hosted installer can otherwise create a record
        // visible only inside its own package, invisible to a normally launched game.
        public static string GetDefaultRecordPath(string userProfile) =>
            Path.Combine(AbsoluteDirectory(userProfile, "userProfile"), ".reign", "installation.json");

        // Explicit inputs also allow isolated discovery contracts without changing
        // process-wide environment variables or reading the real player's state.
        public static ReignInstallation? TryLoad(string userProfile, string? explicitPath = null, bool validationMode = false)
        {
            if (validationMode) return null;
            string path = string.IsNullOrWhiteSpace(explicitPath)
                ? GetDefaultRecordPath(userProfile)
                : Path.GetFullPath(explicitPath!);
            if (!File.Exists(path))
            {
                if (!string.IsNullOrWhiteSpace(explicitPath))
                    throw new FileNotFoundException("The configured Reign installation record is missing.", path);
                return null;
            }
            return ReadFrom(path);
        }

        public static ReignInstallation ReadFrom(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) throw new InvalidDataException("Reign installation record exceeds 64 KiB.");
            using (var stream = File.OpenRead(path))
            {
                var record = (ReignInstallation?)new DataContractJsonSerializer(typeof(ReignInstallation)).ReadObject(stream);
                if (record == null) throw new InvalidDataException("Reign installation record is empty.");
                record.Validate();
                return record;
            }
        }

        public void Validate()
        {
            if (Schema != SchemaName) throw new InvalidDataException("Unsupported Reign installation record schema.");
            if (ProtocolVersion != SupportedProtocol) throw new InvalidDataException("Reign and ReignServer need matching protocol versions. Run the matching setup package.");
            if (string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(ContentVersion))
                throw new InvalidDataException("Installation version or content version is missing.");
            ServerRoot = AbsoluteDirectory(ServerRoot, "serverRoot");
            ContentRoot = AbsoluteDirectory(ContentRoot, "contentRoot");
            DataRoot = AbsoluteDirectory(DataRoot, "dataRoot");
            BannerlordRoot = AbsoluteDirectory(BannerlordRoot, "bannerlordRoot");
            ModuleRoot = AbsoluteDirectory(ModuleRoot, "moduleRoot");
            PostgresBin = AbsoluteDirectory(PostgresBin, "postgresBin");
            if (PostgresPort < 1024 || PostgresPort > 65535)
                throw new InvalidDataException("PostgreSQL port must be between 1024 and 65535.");
            if (!string.Equals(ModuleRoot, Path.Combine(BannerlordRoot, "Modules", "ReignBeta"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The ReignBeta module must belong to the selected Bannerlord installation.");
            if (IsWithin(ServerRoot, DataRoot) || IsWithin(ModuleRoot, DataRoot))
                throw new InvalidDataException("Writable Reign data must be outside the installed program and module directories.");
            if (!string.Equals(ContentRoot, ModuleRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Shared portrait content must be inside the directly distributable ReignBeta module.");
        }

        private static string AbsoluteDirectory(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)
                || value.StartsWith("\\\\", StringComparison.Ordinal)
                || (Path.DirectorySeparatorChar == '\\' && (value.Length < 3 || value[1] != ':' || value[2] != '\\' && value[2] != '/')))
                throw new InvalidDataException("Installation " + name + " must be an absolute local directory.");
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsWithin(string parent, string candidate) =>
            candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
