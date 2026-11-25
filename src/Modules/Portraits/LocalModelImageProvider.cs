using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        internal const string LocalModelImageProviderId = "LocalModel";
        internal const string DefaultLocalModelImageModel = "qwen-image-2.1-int8";
        internal const string LocalModelGenerationsKey = "localModelImageGenerationsUrl";
        internal const string LocalModelEditsKey = "localModelImageEditsUrl";
        internal const string DefaultLocalModelGenerationsUrl = "http://127.0.0.1:18500/v1/images/generations";
        internal const string DefaultLocalModelEditsUrl = "http://127.0.0.1:18500/v1/images/edits";
        internal static readonly string[] LocalModelImageSamplers =
            { "euler", "euler_ancestral", "heun", "dpmpp_2m", "dpmpp_2m_sde", "res_multistep" };
        internal static readonly string[] LocalModelImageSchedulers =
            { "simple", "normal", "karras", "sgm_uniform", "beta", "exponential" };
        private const string LocalModelGenerationsPath = "/v1/images/generations";
        private const string LocalModelEditsPath = "/v1/images/edits";
        private const int LocalModelMaxReferenceBytes = 8 * 1024 * 1024;
        private const int LocalModelMaxResponseBytes = 32 * 1024 * 1024;
        private const int LocalModelMaxFittedDimension = 4096;
        // Allow the largest supported reference resolution squared, plus room for Desk's 32-pixel rounding.
        private const int LocalModelMaxFittedPixels = 2048 * 2048 + 256 * 1024;
        private const int LocalModelMaxPromptChars = 16000;
        private static readonly HttpClient LocalModelImageHttpClient = new HttpClient(CreateLocalModelImageHttpHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        internal sealed record LocalModelImageOptions(
            string Model, string Size, int Steps, double Cfg, string Sampler, string Scheduler,
            string NegativePrompt, int ReferenceResolution, bool FitReference, string Seed);

        private static bool TryLocalModelImageSize(string value, out int width, out int height)
        {
            width = height = 0;
            string[] parts = (value ?? "").Trim().ToLowerInvariant().Split('x');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
                && IsLocalModelImageDimension(width) && IsLocalModelImageDimension(height);
        }

        private static bool IsLocalModelImageDimension(int value) => value >= 256 && value <= 2048 && value % 32 == 0;

        private static string LocalModelProfileDefaultSize(string profile) =>
            IsCharacterPortraitProfile(profile) ? "1024x1536" : "1536x1024";

        private static LocalModelImageOptions ReadLocalModelImageOptions(Dictionary<string, object> settings, string prefix)
        {
            string configuredSize = ReadString(settings, prefix + "LocalModelImageSize", "");
            string size = TryLocalModelImageSize(configuredSize, out int width, out int height)
                ? FormattableString.Invariant($"{width}x{height}") : LocalModelProfileDefaultSize(prefix);
            double cfg = ReadDouble(settings, prefix + "LocalModelCfg", 1d);
            int resolution = ReadInt(settings, prefix + "LocalModelReferenceResolution", 1024);
            string sampler = ReadString(settings, prefix + "LocalModelSampler", "euler");
            string scheduler = ReadString(settings, prefix + "LocalModelScheduler", "simple");
            string seed = ReadString(settings, prefix + "LocalModelSeed", "").Trim();
            return new LocalModelImageOptions(DefaultLocalModelImageModel, size,
                Math.Clamp(ReadInt(settings, prefix + "LocalModelSteps", 25), 1, 100),
                double.IsFinite(cfg) ? Math.Clamp(cfg, 0.1d, 20d) : 1d,
                LocalModelImageSamplers.Contains(sampler, StringComparer.Ordinal) ? sampler : "euler",
                LocalModelImageSchedulers.Contains(scheduler, StringComparer.Ordinal) ? scheduler : "simple",
                ReadString(settings, prefix + "LocalModelNegativePrompt", ""),
                resolution == 0 || IsLocalModelImageDimension(resolution) ? resolution : 1024,
                ReadBool(settings, prefix + "LocalModelFitReference", false),
                ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out ulong exactSeed)
                    ? exactSeed.ToString(CultureInfo.InvariantCulture) : "");
        }

        internal static bool NormalizeLocalModelImageProfile(Dictionary<string, object> settings, string prefix)
        {
            LocalModelImageOptions options = ReadLocalModelImageOptions(settings, prefix);
            var values = new Dictionary<string, object>
            {
                ["LocalModelImageModel"] = options.Model,
                ["LocalModelImageSize"] = options.Size,
                ["LocalModelSteps"] = options.Steps,
                ["LocalModelCfg"] = options.Cfg,
                ["LocalModelSampler"] = options.Sampler,
                ["LocalModelScheduler"] = options.Scheduler,
                ["LocalModelNegativePrompt"] = options.NegativePrompt,
                ["LocalModelReferenceResolution"] = options.ReferenceResolution,
                ["LocalModelFitReference"] = options.FitReference,
                ["LocalModelSeed"] = options.Seed
            };
            bool changed = false;
            foreach (var item in values)
            {
                string key = prefix + item.Key;
                if (settings.TryGetValue(key, out object saved) && LocalModelSettingEquals(saved, item.Value)) continue;
                settings[key] = item.Value;
                changed = true;
            }
            return changed;
        }

        private static bool LocalModelSettingEquals(object saved, object value) =>
            Equals(saved, value)
            || (value is int or double && saved is int or long or double or decimal
                && Convert.ToDouble(saved, CultureInfo.InvariantCulture) == Convert.ToDouble(value, CultureInfo.InvariantCulture));

        private static bool TryLocalModelImageEndpoint(string text, string path, out Uri endpoint)
        {
            endpoint = null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttp
                || uri.AbsolutePath != path || uri.Port <= 0 || uri.UserInfo.Length != 0 || text.Contains('@')
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return false;
            // Resolve the spelled localhost alias to the same numeric loopback without using DNS or a proxy.
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                uri = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
            if (!IPAddress.TryParse(uri.DnsSafeHost, out IPAddress address) || !IPAddress.IsLoopback(address))
                return false;
            endpoint = uri;
            return true;
        }

        internal static bool IsLegacyLocalModelDeskEndpoint(string text, bool chat) =>
            TryLocalModelImageEndpoint(text, chat ? "/v1/chat/completions" : LocalModelGenerationsPath, out Uri endpoint)
            && endpoint.Port == 18500;

        internal static bool TryNormalizeLocalModelImageEndpoint(string text, bool edit, out string normalized)
        {
            normalized = "";
            if (!TryLocalModelImageEndpoint(text, edit ? LocalModelEditsPath : LocalModelGenerationsPath, out Uri endpoint))
                return false;
            normalized = endpoint.AbsoluteUri;
            return true;
        }

        private static bool TryGetLocalModelImageEndpoint(Dictionary<string, object> settings, bool edit, out Uri endpoint) =>
            TryLocalModelImageEndpoint(
                ReadString(settings, edit ? LocalModelEditsKey : LocalModelGenerationsKey,
                    edit ? DefaultLocalModelEditsUrl : DefaultLocalModelGenerationsUrl),
                edit ? LocalModelEditsPath : LocalModelGenerationsPath, out endpoint);

        private static SocketsHttpHandler CreateLocalModelImageHttpHandler() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false
        };

        private static HttpRequestMessage BuildLocalModelImageRequest(Dictionary<string, object> settings,
            ImageGenerationProfile profile, string prompt, byte[] reference, string sizeOverride,
            out int requestChars, out bool fitReference)
        {
            bool edit = reference != null;
            if (!TryGetLocalModelImageEndpoint(settings, edit, out Uri endpoint))
                throw new InvalidOperationException("Use the Local Model Desk loopback URL for the selected image route.");
            var options = profile.LocalModel;
            if (options == null || options.Model != DefaultLocalModelImageModel)
                throw new InvalidOperationException("Select the qwen-image-2.1-int8 image model in the Local Model profile.");
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > LocalModelMaxPromptChars
                || options.NegativePrompt.Length > LocalModelMaxPromptChars)
                throw new InvalidDataException("Local Model Desk limits a prompt or negative prompt to 16,000 characters.");
            bool sizePinned = !string.IsNullOrWhiteSpace(sizeOverride);
            fitReference = edit && !sizePinned && !profile.ChatView && options.FitReference;
            if (!TryLocalModelImageSize(sizePinned ? sizeOverride : options.Size, out int width, out int height))
                throw new InvalidDataException("Local Model Desk image dimensions must be multiples of 32 between 256 and 2048.");
            if (IsCharacterPortraitProfile(profile.Name) && (width < 768 || height < 1024
                || (double)width / height < 0.52d || (double)width / height > 0.77d))
                throw new InvalidDataException("A Local Model portrait needs a portrait-oriented size of at least 768x1024.");
            var body = new Dictionary<string, object>
            {
                ["model"] = options.Model,
                ["prompt"] = prompt,
                ["negative_prompt"] = options.NegativePrompt,
                ["width"] = width,
                ["height"] = height,
                ["steps"] = options.Steps,
                ["cfg"] = options.Cfg,
                ["sampler"] = options.Sampler,
                ["scheduler"] = options.Scheduler,
                ["n"] = 1,
                ["response_format"] = "b64_json"
            };
            if (options.Seed.Length != 0) body["seed"] = options.Seed;
            if (edit)
            {
                if (reference.Length > LocalModelMaxReferenceBytes)
                    throw new InvalidDataException("The Local Model Desk reference exceeds 8 MiB.");
                body["imageDataUrl"] = "data:" + LocalModelReferenceMediaType(reference) + ";base64," + Convert.ToBase64String(reference);
                body["reference_resolution"] = options.ReferenceResolution;
                body["fit_reference"] = fitReference;
            }
            string json = Json.Serialize(body);
            requestChars = json.Length;
            return new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private static bool HasLocalModelPngSignature(byte[] bytes) =>
            bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
            && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;

        private static string LocalModelReferenceMediaType(byte[] bytes)
        {
            if (HasLocalModelPngSignature(bytes)) return "image/png";
            if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "image/jpeg";
            if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
            throw new InvalidDataException("Local Model Desk requires a PNG, JPEG or WebP reference.");
        }

        private static bool IsLocalModelFittedDimension(int value) =>
            value >= 32 && value <= LocalModelMaxFittedDimension && value % 32 == 0;

        private static byte[] ReadLocalModelPngResponse(string json, bool fitReference)
        {
            var root = Json.DeserializeObject(json);
            if (ReadPathObject(root, "data", "0", "b64_json") is not string image
                || image.Length == 0 || image.Length > LocalModelMaxResponseBytes)
                throw new InvalidDataException("Local Model Desk did not return bounded PNG bytes in data[0].b64_json.");
            byte[] png = Convert.FromBase64String(image);
            if (png.Length < 33 || !HasLocalModelPngSignature(png) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4)) != 13)
                throw new InvalidDataException("Local Model Desk returned an invalid PNG.");

            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            if (fitReference)
            {
                if (!IsLocalModelFittedDimension(width) || !IsLocalModelFittedDimension(height)
                    || (long)width * height > LocalModelMaxFittedPixels)
                    throw new InvalidDataException("Local Model Desk returned a fitted PNG outside the supported dimensions. Reduce the reference resolution.");
            }
            else if (!IsLocalModelImageDimension(width) || !IsLocalModelImageDimension(height))
                throw new InvalidDataException("Local Model Desk returned a PNG outside the supported canvas dimensions.");
            return png;
        }

        private static PortraitImageCallResult GenerateLocalModelImage(Dictionary<string, object> settings,
            ImageGenerationProfile profile, string prompt, byte[] reference, string outputSizeOverride)
        {
            Stopwatch timer = Stopwatch.StartNew();
            int requestChars = 0, responseChars = 0;
            string model = profile.LocalModel?.Model ?? DefaultLocalModelImageModel;
            string adapter = reference == null ? "local_model_generations" : "local_model_edits";
            try
            {
                using HttpRequestMessage request = BuildLocalModelImageRequest(settings, profile, prompt, reference,
                    outputSizeOverride, out requestChars, out bool fitReference);
                // This dedicated local transport has no access to the credential vault, proxy, or redirect target.
                Program.ProviderTestGuard.BeforeDispatch("local_model_image");
                int timeoutMs = Math.Clamp(ReadInt(settings, "providerRequestTimeoutMs", 120000), 15000, 600000);
                using var timeout = new CancellationTokenSource(timeoutMs);
                using HttpResponseMessage response = LocalModelImageHttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string error = response.StatusCode == HttpStatusCode.ServiceUnavailable
                        ? "Turn on Qwen in Local Model Desk and wait for it to become ready."
                        : "Local Model Desk rejected the image request (HTTP " + (int)response.StatusCode + ").";
                    throw new InvalidOperationException(error);
                }
                if (response.Content.Headers.ContentLength > LocalModelMaxResponseBytes)
                    throw new InvalidDataException("The Local Model Desk response exceeds the configured image limit.");
                using Stream remote = response.Content.ReadAsStream(timeout.Token);
                using var buffer = new MemoryStream();
                byte[] bytes = new byte[64 * 1024];
                int read;
                while ((read = remote.ReadAsync(bytes.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) > 0)
                {
                    if (buffer.Length + read > LocalModelMaxResponseBytes)
                        throw new InvalidDataException("The Local Model Desk response exceeds the configured image limit.");
                    buffer.Write(bytes, 0, read);
                }
                string json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                responseChars = json.Length;
                return PortraitImageCallResult.Success(LocalModelImageProviderId, model, adapter,
                    ReadLocalModelPngResponse(json, fitReference), timer.ElapsedMilliseconds, requestChars, responseChars);
            }
            catch (Exception ex)
            {
                string error = ex is OperationCanceledException ? "Local Model Desk did not finish before the image request timeout."
                    : ex is HttpRequestException ? "Could not connect to Local Model Desk over the ReignServer loopback."
                    : ex is FormatException or System.Text.Json.JsonException ? "Local Model Desk returned malformed image data."
                    : ex.Message;
                var failure = PortraitImageCallResult.Fail(LocalModelImageProviderId, model, error,
                    timer.ElapsedMilliseconds, requestChars, responseChars);
                failure.Adapter = adapter;
                return failure;
            }
        }
    }
}
