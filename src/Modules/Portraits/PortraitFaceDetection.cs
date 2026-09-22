using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PortraitFocusModelName = "version-RFB-320";
        private const string PortraitFocusModelSha256 = "34cd7e60aeff28744c657de7a3dc64e872d506741de66987f3426f2b79f88017";
        private static readonly object PortraitFocusModelLock = new object();
        private static InferenceSession PortraitFocusSession;

        private sealed class PortraitFaceCandidate
        {
            public double Confidence;
            public double Rank;
            public double X1;
            public double Y1;
            public double X2;
            public double Y2;
        }

        private static PortraitFaceFocus DetectPortraitFaceFocus(byte[] sourceRgba, int sourceWidth, int sourceHeight)
        {
            if (sourceRgba == null || sourceWidth <= 0 || sourceHeight <= 0
                || sourceRgba.Length < sourceWidth * sourceHeight * 4)
            {
                throw new InvalidDataException("The full-body portrait could not be decoded for face detection.");
            }

            byte[] resized = PortraitDerivativeCore.ResizeBilinear(sourceRgba, sourceWidth, sourceHeight, 320, 240);
            var input = new DenseTensor<float>(new[] { 1, 3, 240, 320 });
            for (int y = 0; y < 240; y++)
            {
                for (int x = 0; x < 320; x++)
                {
                    int pixel = (y * 320 + x) * 4;
                    input[0, 0, y, x] = (resized[pixel] - 127f) / 128f;
                    input[0, 1, y, x] = (resized[pixel + 1] - 127f) / 128f;
                    input[0, 2, y, x] = (resized[pixel + 2] - 127f) / 128f;
                }
            }

            InferenceSession session = GetPortraitFocusSession();
            string inputName = session.InputMetadata.Keys.First();
            float[] scores = null;
            float[] boxes = null;
            lock (PortraitFocusModelLock)
            {
                using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(
                    new[] { NamedOnnxValue.CreateFromTensor(inputName, input) }))
                {
                    foreach (DisposableNamedOnnxValue result in results)
                    {
                        Tensor<float> tensor = result.AsTensor<float>();
                        int lastDimension = tensor.Dimensions[tensor.Dimensions.Length - 1];
                        if (lastDimension == 2) scores = tensor.ToArray();
                        else if (lastDimension == 4) boxes = tensor.ToArray();
                    }
                }
            }
            if (scores == null || boxes == null || scores.Length / 2 != boxes.Length / 4)
            {
                throw new InvalidDataException("The portrait focus model returned an unexpected output shape.");
            }

            var candidates = new List<PortraitFaceCandidate>();
            for (int index = 0; index < scores.Length / 2; index++)
            {
                double confidence = scores[index * 2 + 1];
                if (confidence < 0.55d) continue;
                double x1 = ClampUnit(boxes[index * 4]);
                double y1 = ClampUnit(boxes[index * 4 + 1]);
                double x2 = ClampUnit(boxes[index * 4 + 2]);
                double y2 = ClampUnit(boxes[index * 4 + 3]);
                if (x2 <= x1 || y2 <= y1) continue;
                candidates.Add(new PortraitFaceCandidate
                {
                    Confidence = confidence,
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2
                });
            }
            candidates = SuppressOverlappingFaces(candidates, 0.3d);
            foreach (PortraitFaceCandidate candidate in candidates)
            {
                double width = candidate.X2 - candidate.X1;
                double height = candidate.Y2 - candidate.Y1;
                double centerX = (candidate.X1 + candidate.X2) * 0.5d;
                double centerY = (candidate.Y1 + candidate.Y2) * 0.5d;
                double centrality = Math.Max(0d, 1d - Math.Abs(centerX - 0.5d) * 1.5d);
                double upper = Math.Max(0d, 1d - Math.Max(0d, centerY - 0.45d) * 2d);
                double size = Math.Min(1d, Math.Max(width, height) / 0.12d);
                candidate.Rank = candidate.Confidence * (0.55d + 0.2d * centrality + 0.15d * upper + 0.1d * size);
            }
            PortraitFaceCandidate primary = candidates.OrderByDescending(x => x.Rank).FirstOrDefault();
            if (primary == null)
            {
                throw new InvalidDataException("No reliable face was detected in the full-body portrait master.");
            }
            return new PortraitFaceFocus
            {
                Method = "ultraface",
                Model = PortraitFocusModelName,
                Confidence = primary.Confidence,
                CandidateCount = candidates.Count,
                X = primary.X1,
                Y = primary.Y1,
                Width = primary.X2 - primary.X1,
                Height = primary.Y2 - primary.Y1
            };
        }

        private static InferenceSession GetPortraitFocusSession()
        {
            lock (PortraitFocusModelLock)
            {
                if (PortraitFocusSession != null) return PortraitFocusSession;
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portrait_models", "version-RFB-320.onnx");
                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException("The bundled portrait focus model was not found.", modelPath);
                }
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(modelPath))
                {
                    string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(digest, PortraitFocusModelSha256, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The bundled portrait focus model checksum did not match the approved release.");
                    }
                }
                var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                PortraitFocusSession = new InferenceSession(modelPath, options);
                return PortraitFocusSession;
            }
        }

        private static List<PortraitFaceCandidate> SuppressOverlappingFaces(List<PortraitFaceCandidate> candidates, double threshold)
        {
            var remaining = candidates.OrderByDescending(x => x.Confidence).ToList();
            var keep = new List<PortraitFaceCandidate>();
            while (remaining.Count > 0)
            {
                PortraitFaceCandidate current = remaining[0];
                keep.Add(current);
                remaining.RemoveAt(0);
                remaining.RemoveAll(candidate => FaceIntersectionOverUnion(current, candidate) > threshold);
            }
            return keep;
        }

        private static double FaceIntersectionOverUnion(PortraitFaceCandidate left, PortraitFaceCandidate right)
        {
            double intersectionWidth = Math.Max(0d, Math.Min(left.X2, right.X2) - Math.Max(left.X1, right.X1));
            double intersectionHeight = Math.Max(0d, Math.Min(left.Y2, right.Y2) - Math.Max(left.Y1, right.Y1));
            double intersection = intersectionWidth * intersectionHeight;
            double leftArea = (left.X2 - left.X1) * (left.Y2 - left.Y1);
            double rightArea = (right.X2 - right.X1) * (right.Y2 - right.Y1);
            double union = leftArea + rightArea - intersection;
            return union <= 0d ? 0d : intersection / union;
        }

        private static double ClampUnit(double value)
        {
            return Math.Max(0d, Math.Min(1d, value));
        }
    }
}
