using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Exact reviewed versions only. An unfamiliar customization must be mapped
        // to its active rule owners before adding its hash here; never accept by filename.
        private static readonly Dictionary<string, HashSet<string>> RetiredPromptHashes =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dialogue_user_template.txt"] = new HashSet<string>(new[] {
                    "2C827E012C791377E33E3E3EE149871EBD897CC621775B4D478D3EDB7C176A12",
                    "9A51E84382E6498613BE2B2BD8E20F81024E416DB42102539E6344326435ABCA",
                    "B7B7CD540F3F74B0354988FC150197ACA1A5C03E1A13FA7ECB211CEAF28255CA",
                    "B978F32F0BAAEA35D7367CF89E40F12AD381A6E4A7F026D1E33BBB76C27D5BF9",
                    "CD4212FA7BD734AD5AA8C8B55EC84842B60F6A15BAE165D6EEB73A6CCD17CB27",
                    "E5406558DF714CA8EC8EBDC73F71ABF7F499FF70686D35703CB48FEA0DB337F9" }, StringComparer.Ordinal),
                ["event_user_template.txt"] = new HashSet<string>(new[] {
                    "41A1301B66744EC1B273A16188839192AF010B413E8730247836EE912D84CB84",
                    "11A40515F9EBBD38B88BFF136AEC125092F6D7D8644272F326E702364CC8FA72",
                    "28C20A2FB29DAD8ADB27ADA16A480E6F4A96CE5DF9B081B62E42809B47C236B1",
                    "33D1694633737639D8E33BBB2044151F308B62E8E2DBDBFF0634977744B72127",
                    "5E9664BED5655AD0A0850530F7F8E9F1CFDCAC18342DB22A6ED058ED73322CE5",
                    "78BA05E1B01B06258B8EDEFD28EAF162D2EC11CBA45F6ECAB2836F4DAE9690AD" }, StringComparer.Ordinal),
                ["correspondence_user_template.txt"] = new HashSet<string>(new[] {
                    "24C0902F6873C067D660FB1BF98E7EC6982067BA3A787A7A38CF13225F9199E7" }, StringComparer.Ordinal),
                ["action_planner_user_template.txt"] = new HashSet<string>(new[] {
                    "3B8C0922FD2C62A0ADC9B731359FDA7F8912177D218F65B27EF1D514DC8057AE" }, StringComparer.Ordinal)
            };

        private static List<string> UnreviewedRetiredPromptFiles(string directory)
            => RetiredPromptHashes.Where(pair => File.Exists(Path.Combine(directory, pair.Key))
                && !pair.Value.Contains(PromptHash(File.ReadAllText(Path.Combine(directory, pair.Key), Encoding.UTF8))))
                .Select(pair => pair.Key).ToList();

        private static void RetireLegacyPromptTemplates(string directory)
        {
            lock (FileLock)
            {
                // Preflight the entire set before archiving anything. A restart after
                // a partial filesystem failure safely resumes the remaining files.
                var pending = new Dictionary<string, string>();
                foreach (var pair in RetiredPromptHashes)
                {
                    string path = Path.Combine(directory, pair.Key);
                    if (!File.Exists(path)) continue;
                    string hash = PromptHash(File.ReadAllText(path, Encoding.UTF8));
                    if (!pair.Value.Contains(hash))
                        throw new InvalidOperationException("Prompt retirement requires review: " + pair.Key + " (" + hash + "). The original file has been preserved; activation was stopped.");
                    pending.Add(pair.Key, hash);
                }
                foreach (var pair in pending)
                {
                    string source = Path.Combine(directory, pair.Key);
                    string archiveDirectory = Path.Combine(directory, "retired-v9");
                    Directory.CreateDirectory(archiveDirectory);
                    string archive = Path.Combine(archiveDirectory, pair.Key + "." + pair.Value + ".bak");
                    if (File.Exists(archive))
                    {
                        // Preserve exact bytes even if a hash-equivalent newline variant exists.
                        if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(archive)))
                            archive += "." + Guid.NewGuid().ToString("N");
                        else { File.Delete(source); continue; }
                    }
                    File.Move(source, archive);
                }
            }
        }
    }
}
