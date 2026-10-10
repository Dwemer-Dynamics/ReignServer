using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool PortraitContentRefusal(string error)
        {
            return Regex.IsMatch(error ?? "", @"content[_ -]?(?:polic(?:y|ies)[_ -]?)?(?:violation|filter|blocked|rejected)|safety[_ -]?(?:system|filter|violation|guidelines|polic(?:y|ies))|moderation[_ -]?(?:blocked|rejected)|\b(?:nsfw|unsafe content|responsible ai|content polic(?:y|ies)|prohibited[_ -]content|image[_ -]safety|rai[_ -]filtered)\b|[""'](?:finish_?reason|block_?reason)[""']\s*:\s*[""']SAFETY[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Retain bounded failure fields, never an entire response with image/reference bytes.
        private static string PortraitProviderFailureDetail(Dictionary<string, object> failure)
            => LimitText(string.Join(" ", new[] { ReadString(failure, "type", ""), ReadString(failure, "code", ""),
                ReadString(failure, "message", "") }.Where(value => !string.IsNullOrWhiteSpace(value))), 1000);
    }
}
