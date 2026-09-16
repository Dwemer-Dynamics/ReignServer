using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bannerlord.ReignCourtAppearance;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.Tableaus;
using TaleWorlds.ObjectSystem;
using IOPath = System.IO.Path;
using MatrixFrame = TaleWorlds.Library.MatrixFrame;
using Vec3 = TaleWorlds.Library.Vec3;

namespace Bannerlord.NativePortraitRenderer
{
    internal static class NativePortraitRenderJob
    {
        private const string JobEnvironmentVariable = "REIGN_NATIVE_PORTRAIT_JOB";
        private const double InitializeTimeoutSeconds = 60.0;
        // CharacterTableau's mesh-loading counter can reach zero before all material
        // textures have streamed. A longer per-character settle prevents the square
        // placeholder blocks seen in long persistent-engine batches.
        private const double SettleSeconds = 4.0;
        private const double FirstRequestSettleSeconds = 6.0;
        private const double SaveTimeoutSeconds = 4.0;
        private const double OverallTimeoutSeconds = 100.0;
        private const double CaptureConfirmationDelaySeconds = 0.8;
        private const int MaximumCaptureAttempts = 20;
        private const float TableauVerticalFovRadians = (float)(Math.PI / 4.0);

        private static readonly FieldInfo CameraFrameField = typeof(CharacterTableau).GetField(
            "_camPos",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo GatheredCameraFrameField = typeof(CharacterTableau).GetField(
            "_camPosGatheredFromScene",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpawnFrameField = typeof(CharacterTableau).GetField(
            "_initialSpawnFrame",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TableauSceneField = typeof(CharacterTableau).GetField(
            "_tableauScene",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AgentVisualsField = typeof(CharacterTableau).GetField(
            "_agentVisuals",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AgentVisualLoadingCounterField = typeof(CharacterTableau).GetField(
            "_agentVisualLoadingCounter",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Stopwatch Clock = new Stopwatch();
        private static NativePortraitBatchRequest _batch;
        private static NativePortraitRenderRequest[] _requests;
        private static NativePortraitRenderRequest _request;
        private static CharacterTableau _tableau;
        private static string _capturePath;
        private static DateTime _captureRequestedUtc;
        private static long _lastCaptureLength = -1;
        private static int _stableCaptureChecks;
        private static int _saveAttempts;
        private static byte[] _candidateFrame;
        private static DateTime _nextCaptureUtc;
        private static int _requestIndex = -1;
        private static readonly HashSet<string> WarmedSourceModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _requestNeedsModuleWarmup;
        private static int _completedCount;
        private static int _failedCount;
        private static Dictionary<string, ReignCourtAppearanceResult> _courtAppearances;
        private static bool _courtAppearancesPrepared;
        private static bool _gameStartRequested;
        private static bool _requestComplete;
        private static bool _allComplete;
        private static bool _quitRequested;

        internal static void TryArm()
        {
            string path = Environment.GetEnvironmentVariable(JobEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                if (root["requests"] is JArray)
                {
                    _batch = root.ToObject<NativePortraitBatchRequest>();
                    _requests = _batch == null ? null : _batch.Requests;
                }
                else
                {
                    NativePortraitRenderRequest single = root.ToObject<NativePortraitRenderRequest>();
                    _requests = single == null ? null : new[] { single };
                    _batch = new NativePortraitBatchRequest
                    {
                        Version = single == null ? 1 : single.Version,
                        BatchId = single == null ? string.Empty : single.JobId,
                        BatchStatusPath = string.Empty,
                        ContinueOnError = false,
                        CourtAppearances = new NativePortraitAppearanceRequest[0],
                        Requests = _requests
                    };
                }

                if (_requests == null || _requests.Length == 0)
                {
                    throw new InvalidDataException("The native portrait job contains no render requests.");
                }
                if (!string.IsNullOrWhiteSpace(_batch.BatchStatusPath)
                    && !IOPath.IsPathRooted(_batch.BatchStatusPath))
                {
                    throw new InvalidDataException("BatchStatusPath must be absolute.");
                }

                StartRequest(0);
            }
            catch (Exception ex)
            {
                if (_request != null)
                {
                    FailCurrent("Could not read native portrait render job: " + ex.Message);
                }
            }
        }

        internal static void Tick(float dt)
        {
            if (_allComplete)
            {
                if (!_quitRequested && Clock.Elapsed.TotalSeconds >= 0.5)
                {
                    _quitRequested = true;
                    Utilities.QuitGame();
                }
                return;
            }
            if (_request == null)
            {
                return;
            }
            try
            {
                if (_requestComplete)
                {
                    if (Clock.Elapsed.TotalSeconds >= 0.12)
                    {
                        AdvanceRequest();
                    }
                    return;
                }
                if (Clock.Elapsed.TotalSeconds > OverallTimeoutSeconds)
                {
                    FailCurrent("Native portrait rendering timed out before a completed frame was available.");
                    return;
                }
                if (_tableau == null)
                {
                    if (Game.Current == null)
                    {
                        if (!_gameStartRequested && ThumbnailCacheManager.Current != null)
                        {
                            _gameStartRequested = true;
                            WriteStatus("initializing", "Registering Bannerlord's native faces, skeletons, materials, and items.");
                            MBGameManager.StartNewGame(new PortraitGameManager());
                        }
                        return;
                    }
                    if (MBGameManager.Current == null || !MBGameManager.Current.IsLoaded)
                    {
                        return;
                    }
                    if (ThumbnailCacheManager.Current == null)
                    {
                        if (Clock.Elapsed.TotalSeconds > InitializeTimeoutSeconds)
                        {
                            FailCurrent("Bannerlord's native tableau renderer did not initialize in time.");
                        }
                        return;
                    }
                    CreateTableau();
                    return;
                }

                ApplyDeterministicPortraitPose();
                ApplyDirectCameraGaze();
                _tableau.OnTick(dt);
                if (_capturePath == null)
                {
                    double requiredSettleSeconds = _requestNeedsModuleWarmup
                        ? FirstRequestSettleSeconds
                        : SettleSeconds;
                    if (Clock.Elapsed.TotalSeconds >= requiredSettleSeconds
                        && DateTime.UtcNow >= _nextCaptureUtc
                        && AreCharacterResourcesReady())
                    {
                        RequestTextureSave();
                    }
                    return;
                }
                PollTextureSave();
            }
            catch (Exception ex)
            {
                FailCurrent("Native portrait render failed: " + ex);
            }
        }

        private static bool AreCharacterResourcesReady()
        {
            if (_tableau == null)
            {
                return false;
            }
            if (AgentVisualLoadingCounterField != null)
            {
                object value = AgentVisualLoadingCounterField.GetValue(_tableau);
                if (value is int counter && counter > 0)
                {
                    return false;
                }
            }
            Scene scene = TableauSceneField == null ? null : TableauSceneField.GetValue(_tableau) as Scene;
            if (scene == null)
            {
                return true;
            }
            return scene.IsLoadingFinished();
        }

        internal static void FinalizeJob()
        {
            try
            {
                _tableau?.OnFinalize();
                _tableau = null;
            }
            catch
            {
            }
        }

        private static void StartRequest(int index)
        {
            _requestIndex = index;
            _effectiveBodyProperties = null;
            _request = _requests[index];
            ValidateRequest(_request);
            string sourceModule = _request.SourceModule ?? string.Empty;
            _requestNeedsModuleWarmup = index == 0 || !WarmedSourceModules.Contains(sourceModule);
            _capturePath = null;
            _captureRequestedUtc = default(DateTime);
            _lastCaptureLength = -1;
            _stableCaptureChecks = 0;
            _saveAttempts = 0;
            _candidateFrame = null;
            _nextCaptureUtc = DateTime.MinValue;
            _requestComplete = false;
            Clock.Restart();
            WriteStatus(
                "starting",
                "Loading native character " + (index + 1).ToString(CultureInfo.InvariantCulture) +
                " of " + _requests.Length.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static void AdvanceRequest()
        {
            int next = _requestIndex + 1;
            if (next < _requests.Length)
            {
                StartRequest(next);
                return;
            }

            string state = _failedCount == 0 ? "complete" : "complete_with_errors";
            string message = _failedCount == 0
                ? "All native character sources rendered successfully."
                : _completedCount.ToString(CultureInfo.InvariantCulture) + " sources rendered; " +
                  _failedCount.ToString(CultureInfo.InvariantCulture) + " failed.";
            WriteBatchStatus(state, message);
            _allComplete = true;
            Clock.Restart();
        }

        private static void CreateTableau()
        {
            EnsureCourtAppearances();
            CharacterCode characterCode = ResolveCharacterCode();
            string bodyProperties = ResolveBodyProperties(characterCode);
            _effectiveBodyProperties = bodyProperties;
            string equipmentCode = !string.IsNullOrWhiteSpace(_request.EquipmentCode)
                ? _request.EquipmentCode
                : characterCode?.EquipmentCode;
            bool isFemale = characterCode?.IsFemale ?? _request.IsFemale;
            int race = characterCode?.Race ?? _request.Race;
            if (string.IsNullOrWhiteSpace(bodyProperties) || (string.IsNullOrWhiteSpace(equipmentCode) && !_request.PreserveEncounteredOutfit))
            {
                throw new InvalidDataException("Body properties and civilian equipment are required for native rendering.");
            }

            _tableau = new CharacterTableau();
            _tableau.SetCharStringID(_request.CharacterObjectId ?? _request.CharacterId ?? string.Empty);
            _tableau.SetIsFemale(isFemale);
            _tableau.SetRace(race);
            _tableau.SetBodyProperties(bodyProperties);
            _tableau.SetEquipmentCode(equipmentCode);
            _tableau.SetArmorColor1(_request.ClothingColor1 ?? characterCode?.Color1 ?? uint.MaxValue);
            _tableau.SetArmorColor2(_request.ClothingColor2 ?? characterCode?.Color2 ?? uint.MaxValue);
            _tableau.SetIsBannerShownInBackground(false);
            _tableau.SetStanceIndex(0);
            ApplyCameraFraming();
            _tableau.SetTargetSize(_request.Width, _request.Height);
            _tableau.OnTick(0.016f);
            // Let CharacterTableau stream its resources on subsequent ticks. Forcing
            // synchronous scene loading here can access native resources before the
            // off-screen tableau has finished initializing (0xC0000005).
            Clock.Restart();
            WriteStatus("rendering", "Rendering a fresh frame with Bannerlord's native CharacterTableau.");
        }

        private static void ApplyCameraFraming()
        {
            float cropScale = (float)_request.CameraCropScale;
            if (cropScale >= 0.999f)
            {
                return;
            }
            if (CameraFrameField == null
                || GatheredCameraFrameField == null
                || SpawnFrameField == null)
            {
                throw new MissingFieldException(
                    "The installed CharacterTableau does not expose the expected camera-frame fields.");
            }

            MatrixFrame camera = (MatrixFrame)CameraFrameField.GetValue(_tableau);
            MatrixFrame spawn = (MatrixFrame)SpawnFrameField.GetValue(_tableau);
            // Bannerlord's tableau camera uses U as its view-depth axis and F as
            // screen vertical (rather than the conventional F view axis).
            Vec3 viewDepthAxis = camera.rotation.u;
            Vec3 screenUpAxis = camera.rotation.f;
            Vec3 delta = spawn.origin - camera.origin;
            float depth = -(delta.x * viewDepthAxis.x
                + delta.y * viewDepthAxis.y
                + delta.z * viewDepthAxis.z);
            if (depth <= 0.1f)
            {
                throw new InvalidDataException(
                    "The inventory tableau camera is not facing the character spawn point.");
            }

            float centerY = (float)_request.CameraCenterYRatio;
            float advance = depth * (1f - cropScale);
            float verticalOffset = (0.5f - centerY)
                * 2f
                * (float)Math.Tan(TableauVerticalFovRadians / 2f)
                * depth;
            // Move the native camera instead of lowering the character, then pitch it
            // slightly down around the same portrait center. This keeps the close crop
            // stable while producing a subtle above-eye viewpoint.
            camera.origin -= viewDepthAxis * advance;
            camera.origin += screenUpAxis * verticalOffset;
            float pitchRadians = (float)(_request.CameraPitchDegrees * Math.PI / 180d);
            if (Math.Abs(pitchRadians) > 0.0001f)
            {
                float remainingDepth = depth * cropScale;
                camera.origin += screenUpAxis * ((float)Math.Tan(pitchRadians) * remainingDepth);
                float cosine = (float)Math.Cos(pitchRadians);
                float sine = (float)Math.Sin(pitchRadians);
                camera.rotation.f = screenUpAxis * cosine - viewDepthAxis * sine;
                camera.rotation.u = viewDepthAxis * cosine + screenUpAxis * sine;
            }
            CameraFrameField.SetValue(_tableau, camera);
            GatheredCameraFrameField.SetValue(_tableau, camera);
            ApplyPortraitLighting(camera, spawn);
        }

        private static void ApplyDirectCameraGaze()
        {
            if (_request == null
                || _request.CameraCropScale >= 0.999
                || _tableau == null
                || AgentVisualsField == null
                || CameraFrameField == null)
            {
                return;
            }

            AgentVisuals visuals = AgentVisualsField.GetValue(_tableau) as AgentVisuals;
            if (visuals == null)
            {
                return;
            }
            MatrixFrame camera = (MatrixFrame)CameraFrameField.GetValue(_tableau);
            Vec3 eye = visuals.GetGlobalStableEyePoint(true);
            Vec3 direction = camera.origin - eye;
            float lengthSquared = direction.x * direction.x
                + direction.y * direction.y
                + direction.z * direction.z;
            if (lengthSquared <= 0.0001f)
            {
                return;
            }
            direction.Normalize();
            MatrixFrame entityFrame = visuals.GetEntity().GetGlobalFrame();
            Vec3 localDirection = entityFrame.rotation.TransformToLocal(in direction);
            localDirection.Normalize();
            visuals.SetLookDirection(localDirection);
        }

        private static void ApplyDeterministicPortraitPose()
        {
            if (_request == null
                || _request.CameraCropScale >= 0.999
                || _tableau == null
                || AgentVisualsField == null)
            {
                return;
            }
            AgentVisuals visuals = AgentVisualsField.GetValue(_tableau) as AgentVisuals;
            if (visuals == null)
            {
                return;
            }
            ActionIndexCache portraitIdle = ActionIndexCache.Create("act_character_developer_idle");
            if (portraitIdle == ActionIndexCache.act_none)
            {
                throw new InvalidOperationException("Bannerlord's native character-development idle action is unavailable.");
            }
            visuals.SetAction(in portraitIdle, 0.25f, true);
        }

        private static void ApplyPortraitLighting(MatrixFrame camera, MatrixFrame spawn)
        {
            if (TableauSceneField == null)
            {
                throw new MissingFieldException(
                    "The installed CharacterTableau does not expose its native scene for portrait lighting.");
            }
            Scene scene = TableauSceneField.GetValue(_tableau) as Scene;
            if (scene == null)
            {
                throw new InvalidOperationException("The native inventory tableau scene is unavailable.");
            }

            var entities = new List<GameEntity>();
            scene.GetEntities(ref entities);
            foreach (GameEntity entity in entities)
            {
                Light light = entity.GetLight();
                if (light != null && light.IsValid)
                {
                    // CharacterTableau reuses its native portrait scene across the
                    // persistent batch. Hiding our old lights is insufficient because
                    // a later tableau tick can make them visible again, causing each
                    // character to become progressively brighter. Remove only lights
                    // created by this renderer and keep native scene lights hidden.
                    if (!string.IsNullOrEmpty(entity.Name)
                        && entity.Name.StartsWith("reign_portrait_", StringComparison.Ordinal))
                    {
                        entity.Remove(0);
                    }
                    else
                    {
                        light.SetVisibility(false);
                    }
                }
            }

            Vec3 screenRight = camera.rotation.s;
            Vec3 screenUp = camera.rotation.f;
            Vec3 viewDepth = camera.rotation.u;
            AddPortraitPointLight(
                scene,
                "reign_portrait_key",
                camera.origin + screenRight * 0.34f + screenUp * 0.24f,
                new Vec3(1.0f, 0.96f, 0.90f),
                0.55f,
                4.0f);
            AddPortraitPointLight(
                scene,
                "reign_portrait_fill",
                camera.origin - screenRight * 0.34f + screenUp * 0.06f,
                new Vec3(0.88f, 0.94f, 1.0f),
                0.20f,
                4.0f);
            AddPortraitPointLight(
                scene,
                "reign_portrait_rim",
                spawn.origin - viewDepth * 0.75f + screenUp * 1.55f,
                new Vec3(0.82f, 0.88f, 1.0f),
                0.08f,
                3.0f);
        }

        private static void AddPortraitPointLight(
            Scene scene,
            string name,
            Vec3 position,
            Vec3 color,
            float intensity,
            float radius)
        {
            GameEntity entity = GameEntity.CreateEmpty(scene, false, false, false);
            entity.Name = name;
            Light light = Light.CreatePointLight(radius);
            light.LightColor = color;
            light.Intensity = intensity;
            light.Radius = radius;
            light.SetShadowType(Light.ShadowType.NoShadow);
            light.ShadowEnabled = false;
            if (!entity.AddLight(light))
            {
                throw new InvalidOperationException("Could not attach native portrait light: " + name);
            }
            MatrixFrame frame = MatrixFrame.Identity;
            frame.origin = position;
            entity.SetGlobalFrame(in frame);
        }

        private static string ResolveBodyProperties(CharacterCode characterCode)
        {
            // A live campaign snapshot is authoritative over catalog-generated court appearances.
            if (!string.IsNullOrWhiteSpace(_request.BodyProperties))
            {
                return _request.BodyProperties;
            }
            if (_courtAppearances != null
                && _courtAppearances.TryGetValue(_request.CharacterId ?? string.Empty, out ReignCourtAppearanceResult court))
            {
                return court.BodyProperties.ToString();
            }
            if (characterCode != null)
            {
                return characterCode.BodyProperties.ToString();
            }
            if (string.IsNullOrWhiteSpace(_request.FaceTemplateId) || MBObjectManager.Instance == null)
            {
                return string.Empty;
            }

            MBBodyProperty range = MBObjectManager.Instance.GetObject<MBBodyProperty>(_request.FaceTemplateId);
            if (range == null)
            {
                throw new InvalidDataException("Native face template was not loaded: " + _request.FaceTemplateId);
            }
            BodyProperties generated = BodyProperties.GetRandomBodyProperties(
                _request.Race,
                _request.IsFemale,
                range.BodyPropertyMin,
                range.BodyPropertyMax,
                0,
                StableFaceSeed(_request.CharacterId),
                range.HairTags,
                range.BeardTags,
                range.TattooTags,
                0f);
            float age = _request.Age > 0f ? _request.Age : generated.Age;
            float weight = Clamp01(_request.Weight);
            float build = Clamp01(_request.Build);
            return new BodyProperties(
                new DynamicBodyProperties(age, weight, build),
                generated.StaticProperties).ToString();
        }

        private static void EnsureCourtAppearances()
        {
            if (_courtAppearancesPrepared)
            {
                return;
            }
            _courtAppearancesPrepared = true;
            _courtAppearances = new Dictionary<string, ReignCourtAppearanceResult>(StringComparer.OrdinalIgnoreCase);
            var specifications = new List<NativePortraitAppearanceRequest>();
            if (_batch != null && _batch.CourtAppearances != null)
            {
                specifications.AddRange(_batch.CourtAppearances);
            }
            if (specifications.Count == 0 && _requests != null)
            {
                foreach (NativePortraitRenderRequest request in _requests)
                {
                    if (string.Equals(
                        request.AppearanceMode,
                        ReignCourtAppearanceGenerator.Version,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        specifications.Add(ToAppearanceRequest(request));
                    }
                }
            }
            if (specifications.Count == 0)
            {
                return;
            }

            var inputs = new List<ReignCourtAppearanceInput>();
            foreach (NativePortraitAppearanceRequest specification in specifications)
            {
                if (string.IsNullOrWhiteSpace(specification.FaceTemplateId))
                {
                    continue;
                }
                MBBodyProperty range = MBObjectManager.Instance == null
                    ? null
                    : MBObjectManager.Instance.GetObject<MBBodyProperty>(specification.FaceTemplateId);
                if (range == null)
                {
                    throw new InvalidDataException(
                        "Reign court face template was not loaded: " + specification.FaceTemplateId);
                }
                int[] cultureHair = TaleWorlds.Core.FaceGen.GetHairIndicesByTag(
                    specification.Race,
                    specification.IsFemale ? 1 : 0,
                    specification.Age,
                    specification.CultureId ?? string.Empty);
                inputs.Add(new ReignCourtAppearanceInput
                {
                    CharacterId = specification.CharacterId,
                    IsFemale = specification.IsFemale,
                    Race = specification.Race,
                    Age = specification.Age,
                    Weight = specification.Weight,
                    Build = specification.Build,
                    Range = range,
                    CultureHairIndices = cultureHair,
                    MotherId = specification.MotherId,
                    FatherId = specification.FatherId
                });
            }

            _courtAppearances = ReignCourtAppearanceGenerator.Generate(inputs);
            if (_courtAppearances.Count != inputs.Count)
            {
                throw new InvalidDataException(
                    "The deterministic Reign court appearance set is incomplete (" +
                    _courtAppearances.Count.ToString(CultureInfo.InvariantCulture) + "/" +
                    inputs.Count.ToString(CultureInfo.InvariantCulture) + ").");
            }
            ReignCourtAppearanceResult nonUnique = _courtAppearances.Values.FirstOrDefault(result => !result.Unique);
            if (nonUnique != null)
            {
                throw new InvalidDataException(
                    "The deterministic Reign court appearance set contains a duplicate face: " +
                    nonUnique.CharacterId);
            }
            WriteStatus(
                "initializing",
                "Prepared " + _courtAppearances.Count.ToString(CultureInfo.InvariantCulture) +
                " deterministic Reign court appearances with " + ReignCourtAppearanceGenerator.Version + ".");
        }

        private static NativePortraitAppearanceRequest ToAppearanceRequest(NativePortraitRenderRequest request)
        {
            string id = request.CharacterId ?? string.Empty;
            string familyPrefix = id;
            string[] suffixes = { "_father", "_mother", "_child_1", "_child_2", "_child_3", "_child_4" };
            foreach (string suffix in suffixes)
            {
                if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    familyPrefix = id.Substring(0, id.Length - suffix.Length);
                    break;
                }
            }
            bool isChild = id.IndexOf("_child_", StringComparison.OrdinalIgnoreCase) >= 0;
            return new NativePortraitAppearanceRequest
            {
                CharacterId = id,
                CharacterObjectId = request.CharacterObjectId,
                FaceTemplateId = request.FaceTemplateId,
                CultureId = request.CultureId,
                IsFemale = request.IsFemale,
                Race = request.Race,
                Age = request.Age,
                Weight = request.Weight,
                Build = request.Build,
                MotherId = isChild ? familyPrefix + "_mother" : string.Empty,
                FatherId = isChild ? familyPrefix + "_father" : string.Empty
            };
        }

        private static int StableFaceSeed(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }
                return (int)(hash % 2000u);
            }
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        private static CharacterCode ResolveCharacterCode()
        {
            if (!string.IsNullOrWhiteSpace(_request.CharacterCode))
            {
                return CharacterCode.CreateFrom(_request.CharacterCode);
            }
            string objectId = !string.IsNullOrWhiteSpace(_request.CharacterObjectId)
                ? _request.CharacterObjectId
                : _request.CharacterId;
            if (string.IsNullOrWhiteSpace(objectId) || MBObjectManager.Instance == null)
            {
                return null;
            }
            BasicCharacterObject character = MBObjectManager.Instance.GetObject<BasicCharacterObject>(objectId);
            return character == null ? null : CharacterCode.CreateFrom(character);
        }

        private static void RequestTextureSave()
        {
            if (_tableau.Texture == null || !_tableau.Texture.IsValid)
            {
                return;
            }
            _saveAttempts++;
            _capturePath = IOPath.Combine(
                IOPath.GetDirectoryName(_request.OutputPath) ?? string.Empty,
                ".native-capture-" + _request.JobId + "-" + _saveAttempts.ToString(CultureInfo.InvariantCulture) + ".png");
            TryDelete(_capturePath);
            _lastCaptureLength = -1;
            _stableCaptureChecks = 0;
            _captureRequestedUtc = DateTime.UtcNow;
            _tableau.Texture.SaveToFile(_capturePath, false);
            WriteStatus("capturing", "Reading the newly rendered native texture from GPU memory.");
        }

        private static void PollTextureSave()
        {
            if (!File.Exists(_capturePath))
            {
                if ((DateTime.UtcNow - _captureRequestedUtc).TotalSeconds > SaveTimeoutSeconds)
                {
                    RetryCapture();
                }
                return;
            }
            long length = new FileInfo(_capturePath).Length;
            if (length <= 0 || length != _lastCaptureLength)
            {
                _lastCaptureLength = length;
                _stableCaptureChecks = 0;
                return;
            }
            if (++_stableCaptureChecks < 3)
            {
                return;
            }

            byte[] normalized = FixSavedTextureColors(
                File.ReadAllBytes(_capturePath),
                _request.OutputWidth,
                _request.OutputHeight);
            if (normalized == null || normalized.Length == 0 || !LooksRendered(normalized))
            {
                RetryCapture();
                return;
            }
            if (HasTextureTileArtifacts(normalized))
            {
                RetryCapture();
                _nextCaptureUtc = DateTime.UtcNow.AddSeconds(CaptureConfirmationDelaySeconds);
                WriteStatus(
                    "verifying",
                    "Rejected an incomplete tiled material frame; waiting for native textures to finish streaming.");
                return;
            }
            TryDelete(_capturePath);
            _capturePath = null;
            if (_candidateFrame == null)
            {
                _candidateFrame = normalized;
                _nextCaptureUtc = DateTime.UtcNow.AddSeconds(CaptureConfirmationDelaySeconds);
                WriteStatus("verifying", "Waiting for a second stable native frame before publication.");
                return;
            }
            Directory.CreateDirectory(IOPath.GetDirectoryName(_request.OutputPath) ?? string.Empty);
            File.WriteAllBytes(_request.OutputPath, normalized);
            _candidateFrame = null;
            CompleteCurrent();
        }

        private static bool HasTextureTileArtifacts(byte[] png)
        {
            using (var stream = new MemoryStream(png, false))
            using (var source = new Bitmap(stream))
            using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.DrawImageUnscaled(source, 0, 0);
                }

                Rectangle bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int stride = Math.Abs(data.Stride);
                    byte[] pixels = new byte[stride * data.Height];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    return HasPeriodicTileEdges(pixels, stride, data.Width, data.Height, 8)
                        || HasPeriodicTileEdges(pixels, stride, data.Width, data.Height, 16);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }
        }

        private static bool HasPeriodicTileEdges(
            byte[] pixels,
            int stride,
            int width,
            int height,
            int period)
        {
            double[] horizontalSums = new double[period];
            double[] verticalSums = new double[period];
            long[] horizontalCounts = new long[period];
            long[] verticalCounts = new long[period];

            // Use every fourth row/column for speed, while retaining every possible
            // x/y phase so a tiled placeholder cannot hide between sample points.
            for (int y = 2; y < height; y += 4)
            {
                int row = y * stride;
                for (int x = 1; x < width; x++)
                {
                    int current = row + x * 3;
                    int previous = current - 3;
                    int currentLuma = Luma(pixels, current);
                    int previousLuma = Luma(pixels, previous);
                    if (Math.Max(currentLuma, previousLuma) <= 15)
                    {
                        continue;
                    }
                    int phase = x % period;
                    horizontalSums[phase] += Math.Abs(currentLuma - previousLuma);
                    horizontalCounts[phase]++;
                }
            }

            for (int x = 2; x < width; x += 4)
            {
                for (int y = 1; y < height; y++)
                {
                    int current = y * stride + x * 3;
                    int previous = current - stride;
                    int currentLuma = Luma(pixels, current);
                    int previousLuma = Luma(pixels, previous);
                    if (Math.Max(currentLuma, previousLuma) <= 15)
                    {
                        continue;
                    }
                    int phase = y % period;
                    verticalSums[phase] += Math.Abs(currentLuma - previousLuma);
                    verticalCounts[phase]++;
                }
            }

            double horizontalRatio;
            double horizontalPeak;
            MeasurePeriodicPeak(horizontalSums, horizontalCounts, out horizontalRatio, out horizontalPeak);
            double verticalRatio;
            double verticalPeak;
            MeasurePeriodicPeak(verticalSums, verticalCounts, out verticalRatio, out verticalPeak);
            return horizontalPeak >= 6.0d
                && verticalPeak >= 6.0d
                && horizontalRatio >= 1.25d
                && verticalRatio >= 1.25d;
        }

        private static void MeasurePeriodicPeak(
            double[] sums,
            long[] counts,
            out double ratio,
            out double peak)
        {
            double[] means = new double[sums.Length];
            for (int index = 0; index < sums.Length; index++)
            {
                means[index] = counts[index] > 0 ? sums[index] / counts[index] : 0.0d;
            }
            Array.Sort(means);
            peak = means[means.Length - 1];
            double median = means.Length % 2 == 0
                ? (means[means.Length / 2 - 1] + means[means.Length / 2]) * 0.5d
                : means[means.Length / 2];
            ratio = peak / Math.Max(0.01d, median);
        }

        private static int Luma(byte[] pixels, int index)
        {
            return (pixels[index + 2] * 3 + pixels[index + 1] * 6 + pixels[index]) / 10;
        }

        private static void CompleteCurrent()
        {
            FinalizeJob();
            WarmedSourceModules.Add(_request.SourceModule ?? string.Empty);
            _completedCount++;
            WriteStatus("complete", "Fresh native source rendered successfully.");
            _requestComplete = true;
            Clock.Restart();
        }

        private static byte[] FixSavedTextureColors(byte[] png, int outputWidth, int outputHeight)
        {
            using (var input = new MemoryStream(png, false))
            using (var source = new Bitmap(input))
            using (var corrected = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(corrected))
                {
                    graphics.DrawImageUnscaled(source, 0, 0);
                }
                Rectangle bounds = new Rectangle(0, 0, corrected.Width, corrected.Height);
                BitmapData data = corrected.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int byteCount = Math.Abs(data.Stride) * data.Height;
                    byte[] pixels = new byte[byteCount];
                    Marshal.Copy(data.Scan0, pixels, 0, byteCount);
                    for (int row = 0; row < data.Height; row++)
                    {
                        int rowStart = row * Math.Abs(data.Stride);
                        for (int x = 0; x < data.Width; x++)
                        {
                            int index = rowStart + x * 4;
                            byte blue = pixels[index];
                            pixels[index] = pixels[index + 2];
                            pixels[index + 2] = blue;
                        }
                    }
                    Marshal.Copy(pixels, 0, data.Scan0, byteCount);
                }
                finally
                {
                    corrected.UnlockBits(data);
                }

                using (var opaque = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(opaque))
                using (var output = new MemoryStream())
                {
                    graphics.Clear(Color.Black);
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(
                        corrected,
                        new Rectangle(0, 0, outputWidth, outputHeight),
                        new Rectangle(0, 0, corrected.Width, corrected.Height),
                        GraphicsUnit.Pixel);
                    opaque.Save(output, ImageFormat.Png);
                    return output.ToArray();
                }
            }
        }

        private static bool LooksRendered(byte[] png)
        {
            using (var stream = new MemoryStream(png, false))
            using (var bitmap = new Bitmap(stream))
            {
                long lit = 0;
                long sampled = 0;
                for (int y = 0; y < bitmap.Height; y += 8)
                {
                    for (int x = 0; x < bitmap.Width; x += 8)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        sampled++;
                        if (pixel.R > 14 || pixel.G > 14 || pixel.B > 14)
                        {
                            lit++;
                        }
                    }
                }
                return sampled > 0 && (double)lit / sampled >= 0.004;
            }
        }

        private static void RetryCapture()
        {
            TryDelete(_capturePath);
            _capturePath = null;
            _stableCaptureChecks = 0;
            _lastCaptureLength = -1;
            if (_saveAttempts >= MaximumCaptureAttempts)
            {
                throw new InvalidDataException(
                    "The renderer could not produce two clean, fully textured frames after " +
                    MaximumCaptureAttempts.ToString(CultureInfo.InvariantCulture) + " attempts.");
            }
        }

        private static void ValidateRequest(NativePortraitRenderRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.JobId))
            {
                throw new InvalidDataException("JobId is required.");
            }
            if (string.IsNullOrWhiteSpace(request.OutputPath) || !IOPath.IsPathRooted(request.OutputPath)
                || string.IsNullOrWhiteSpace(request.StatusPath) || !IOPath.IsPathRooted(request.StatusPath))
            {
                throw new InvalidDataException("OutputPath and StatusPath must be absolute.");
            }
            if (request.Width < 384 || request.Width > 4096 || request.Height < 384 || request.Height > 4096)
            {
                throw new InvalidDataException("Render dimensions must be between 384 and 4096 pixels.");
            }
            if (request.OutputWidth < 384 || request.OutputWidth > 4096
                || request.OutputHeight < 384 || request.OutputHeight > 4096)
            {
                throw new InvalidDataException("Output dimensions must be between 384 and 4096 pixels.");
            }
            if (request.CameraCropScale < 0.15 || request.CameraCropScale > 1.0
                || request.CameraCenterYRatio < 0.15 || request.CameraCenterYRatio > 0.85
                || request.CameraPitchDegrees < -15d || request.CameraPitchDegrees > 15d)
            {
                throw new InvalidDataException("Camera framing values are outside the supported range.");
            }
        }

        private static void FailCurrent(string message)
        {
            try
            {
                FinalizeJob();
                _failedCount++;
                WriteStatus("failed", message);
            }
            catch
            {
            }
            _requestComplete = true;
            Clock.Restart();
        }

        private static string _effectiveBodyProperties;

        private static void WriteStatus(string state, string message)
        {
            if (_request == null || string.IsNullOrWhiteSpace(_request.StatusPath))
            {
                return;
            }
            string directory = IOPath.GetDirectoryName(_request.StatusPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            WriteTextAtomically(_request.StatusPath, JsonConvert.SerializeObject(new
            {
                version = 4,
                jobId = _request.JobId,
                state,
                message,
                outputPath = _request.OutputPath,
                effectiveBodyProperties = _effectiveBodyProperties,
                utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }, Formatting.Indented));
            WriteBatchStatus("running", message);
        }

        private static void WriteBatchStatus(string state, string message)
        {
            if (_batch == null || string.IsNullOrWhiteSpace(_batch.BatchStatusPath))
            {
                return;
            }
            string directory = IOPath.GetDirectoryName(_batch.BatchStatusPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            WriteTextAtomically(_batch.BatchStatusPath, JsonConvert.SerializeObject(new
            {
                version = 4,
                batchId = _batch.BatchId,
                state,
                message,
                completed = _completedCount,
                failed = _failedCount,
                total = _requests == null ? 0 : _requests.Length,
                currentCharacterId = _request == null ? string.Empty : _request.CharacterId,
                currentIndex = _requestIndex,
                utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }, Formatting.Indented));
        }

        private static void WriteTextAtomically(string path, string contents)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, contents);
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private sealed class NativePortraitBatchRequest
        {
            public int Version { get; set; }
            public string BatchId { get; set; }
            public string BatchStatusPath { get; set; }
            public bool ContinueOnError { get; set; }
            public NativePortraitAppearanceRequest[] CourtAppearances { get; set; }
            public NativePortraitRenderRequest[] Requests { get; set; }
        }

        private sealed class NativePortraitAppearanceRequest
        {
            public string CharacterId { get; set; }
            public string CharacterObjectId { get; set; }
            public string FaceTemplateId { get; set; }
            public string CultureId { get; set; }
            public bool IsFemale { get; set; }
            public int Race { get; set; }
            public float Age { get; set; }
            public float Weight { get; set; }
            public float Build { get; set; }
            public string MotherId { get; set; }
            public string FatherId { get; set; }
        }

        private sealed class NativePortraitRenderRequest
        {
            public int Version { get; set; }
            public string JobId { get; set; }
            public string CharacterId { get; set; }
            public string CharacterObjectId { get; set; }
            public string CharacterCode { get; set; }
            public string FaceTemplateId { get; set; }
            public string AppearanceMode { get; set; }
            public string CultureId { get; set; }
            public string CacheKey { get; set; }
            public string RenderPreset { get; set; }
            public string SourceModule { get; set; }
            public string SourceFile { get; set; }
            public string CivilianTemplate { get; set; }
            public string CivilianEquipmentProvenance { get; set; }
            public float Age { get; set; }
            public float Weight { get; set; }
            public float Build { get; set; }
            public string BodyProperties { get; set; }
            public string EquipmentCode { get; set; }
            public uint? ClothingColor1 { get; set; }
            public uint? ClothingColor2 { get; set; }
            public bool PreserveEncounteredOutfit { get; set; }
            public bool IsFemale { get; set; }
            public int Race { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int OutputWidth { get; set; }
            public int OutputHeight { get; set; }
            public double CameraCropScale { get; set; }
            public double CameraCenterYRatio { get; set; }
            public double CameraPitchDegrees { get; set; }
            public string OutputPath { get; set; }
            public string StatusPath { get; set; }
        }
    }
}
