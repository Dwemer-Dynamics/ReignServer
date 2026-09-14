using System.Numerics;

namespace Bannerlord.NativeCharacterImageGenerator;

internal static class SoftwareRasterizer
{
    private readonly record struct ProjectedVertex(
        Vector2 Screen,
        float Depth,
        Vector3 Normal,
        Vector3 Tangent,
        Vector3 Binormal,
        Vector2 Uv);

    public static RenderResult Render(
        IReadOnlyList<RenderMesh> meshes,
        int width,
        int height,
        float yawDegrees,
        float pitchDegrees,
        bool transparent,
        float zoom = 1f,
        float verticalFocus = 0.5f)
    {
        var allPositions = meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Position).ToArray();
        var minimum = new Vector3(
            allPositions.Min(position => position.X),
            allPositions.Min(position => position.Y),
            allPositions.Min(position => position.Z));
        var maximum = new Vector3(
            allPositions.Max(position => position.X),
            allPositions.Max(position => position.Y),
            allPositions.Max(position => position.Z));
        var center = (minimum + maximum) * 0.5f;
        var rotation = Matrix4x4.CreateRotationZ(DegreesToRadians(yawDegrees))
            * Matrix4x4.CreateRotationX(DegreesToRadians(pitchDegrees));

        var transformed = meshes
            .Select(mesh => mesh.Vertices
                .Select(vertex => Vector3.Transform(vertex.Position - center, rotation))
                .ToArray())
            .ToArray();
        var transformedPositions = transformed.SelectMany(positions => positions).ToArray();
        var minX = transformedPositions.Min(position => position.X);
        var maxX = transformedPositions.Max(position => position.X);
        var minZ = transformedPositions.Min(position => position.Z);
        var maxZ = transformedPositions.Max(position => position.Z);
        var scale = MathF.Min(
            (width * 0.84f) / MathF.Max(maxX - minX, 0.001f),
            (height * 0.84f) / MathF.Max(maxZ - minZ, 0.001f)) * Math.Clamp(zoom, 0.65f, 4f);
        var imageCenterX = width * 0.5f;
        var imageCenterY = height * 0.5f;
        var focusZ = minZ + ((maxZ - minZ) * Math.Clamp(verticalFocus, 0f, 1f));

        var pixels = new byte[checked(width * height * 4)];
        FillBackground(pixels, transparent);
        var depthBuffer = Enumerable.Repeat(float.NegativeInfinity, width * height).ToArray();
        var triangleCount = 0;

        for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            var mesh = meshes[meshIndex];
            var vertices = new ProjectedVertex[mesh.Vertices.Length];
            for (var vertexIndex = 0; vertexIndex < mesh.Vertices.Length; vertexIndex++)
            {
                var position = transformed[meshIndex][vertexIndex];
                var source = mesh.Vertices[vertexIndex];
                vertices[vertexIndex] = new ProjectedVertex(
                    new Vector2(
                        imageCenterX + (position.X * scale),
                        imageCenterY - ((position.Z - focusZ) * scale)),
                    position.Y,
                    Vector3.Normalize(Vector3.TransformNormal(source.Normal, rotation)),
                    Vector3.Normalize(Vector3.TransformNormal(source.Tangent, rotation)),
                    Vector3.Normalize(Vector3.TransformNormal(source.Binormal, rotation)),
                    source.Uv);
            }

            foreach (var face in mesh.Faces)
            {
                RasterizeTriangle(
                    vertices[face.V0],
                    vertices[face.V1],
                    vertices[face.V2],
                    mesh,
                    pixels,
                    depthBuffer,
                    width,
                    height);
                triangleCount++;
            }
        }

        return new RenderResult(width, height, pixels, triangleCount);
    }

    private static void RasterizeTriangle(
        ProjectedVertex v0,
        ProjectedVertex v1,
        ProjectedVertex v2,
        RenderMesh mesh,
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height)
    {
        var area = Edge(v0.Screen, v1.Screen, v2.Screen);
        if (MathF.Abs(area) < 0.0001f)
        {
            return;
        }

        var minX = Math.Clamp((int)MathF.Floor(MathF.Min(v0.Screen.X, MathF.Min(v1.Screen.X, v2.Screen.X))), 0, width - 1);
        var maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(v0.Screen.X, MathF.Max(v1.Screen.X, v2.Screen.X))), 0, width - 1);
        var minY = Math.Clamp((int)MathF.Floor(MathF.Min(v0.Screen.Y, MathF.Min(v1.Screen.Y, v2.Screen.Y))), 0, height - 1);
        var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(v0.Screen.Y, MathF.Max(v1.Screen.Y, v2.Screen.Y))), 0, height - 1);
        var lightDirection = Vector3.Normalize(new Vector3(-0.35f, 0.78f, 0.52f));

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var point = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = Edge(v1.Screen, v2.Screen, point) / area;
                var w1 = Edge(v2.Screen, v0.Screen, point) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < -0.00001f || w1 < -0.00001f || w2 < -0.00001f)
                {
                    continue;
                }

                var depth = (w0 * v0.Depth) + (w1 * v1.Depth) + (w2 * v2.Depth);
                var pixelIndex = (y * width) + x;
                if (depth <= depthBuffer[pixelIndex])
                {
                    continue;
                }

                var uv = (w0 * v0.Uv) + (w1 * v1.Uv) + (w2 * v2.Uv);
                var color = mesh.DiffuseTexture is null
                    ? mesh.FallbackColor
                    : mesh.DiffuseTexture.Sample(uv) * mesh.FallbackColor;
                if (!mesh.UseAlpha)
                {
                    color.W = 1f;
                }

                if (mesh.UseDoubleColorMap && mesh.ColorMaskTexture is not null)
                {
                    var mask = mesh.ColorMaskTexture.Sample(uv);
                    var baseRgb = new Vector3(color.X, color.Y, color.Z);
                    var primaryRgb = new Vector3(mesh.PrimaryColor.X, mesh.PrimaryColor.Y, mesh.PrimaryColor.Z);
                    var secondaryRgb = new Vector3(mesh.SecondaryColor.X, mesh.SecondaryColor.Y, mesh.SecondaryColor.Z);
                    var tinted = Vector3.Lerp(baseRgb, baseRgb * primaryRgb, Math.Clamp(mask.X, 0f, 1f));
                    tinted = Vector3.Lerp(tinted, baseRgb * secondaryRgb, Math.Clamp(mask.Y, 0f, 1f));
                    color.X = tinted.X;
                    color.Y = tinted.Y;
                    color.Z = tinted.Z;
                }

                if (color.W < 0.12f)
                {
                    continue;
                }

                var normal = (w0 * v0.Normal) + (w1 * v1.Normal) + (w2 * v2.Normal);
                if (normal.LengthSquared() > 0.000001f)
                {
                    normal = Vector3.Normalize(normal);
                }

                if (mesh.NormalTexture is not null)
                {
                    var tangent = Vector3.Normalize((w0 * v0.Tangent) + (w1 * v1.Tangent) + (w2 * v2.Tangent));
                    var binormal = Vector3.Normalize((w0 * v0.Binormal) + (w1 * v1.Binormal) + (w2 * v2.Binormal));
                    var sampled = mesh.NormalTexture.Sample(uv);
                    var tangentX = ((sampled.X * 2f) - 1f) * mesh.NormalMapPower;
                    var tangentY = ((sampled.Y * 2f) - 1f) * mesh.NormalMapPower;
                    var tangentZ = MathF.Sqrt(MathF.Max(0f, 1f - (tangentX * tangentX) - (tangentY * tangentY)));
                    var mapped = (tangent * tangentX) + (binormal * tangentY) + (normal * tangentZ);
                    if (mapped.LengthSquared() > 0.000001f)
                    {
                        normal = Vector3.Normalize(mapped);
                    }
                }

                var diffuse = MathF.Max(Vector3.Dot(normal, lightDirection), 0f);
                var lighting = 0.34f + (0.66f * diffuse);
                var specularSample = mesh.SpecularTexture?.Sample(uv).X ?? 0.18f;
                var viewDirection = Vector3.UnitY;
                var halfDirection = Vector3.Normalize(lightDirection + viewDirection);
                var glossPower = 8f + (Math.Clamp(mesh.GlossCoefficient, 0f, 1.5f) * 56f);
                var specular = MathF.Pow(MathF.Max(Vector3.Dot(normal, halfDirection), 0f), glossPower)
                    * specularSample
                    * Math.Clamp(mesh.SpecularCoefficient, 0f, 2f)
                    * 0.42f;
                var outputIndex = pixelIndex * 4;
                pixels[outputIndex] = ToByte((color.X * lighting) + specular);
                pixels[outputIndex + 1] = ToByte((color.Y * lighting) + specular);
                pixels[outputIndex + 2] = ToByte((color.Z * lighting) + specular);
                pixels[outputIndex + 3] = ToByte(color.W);
                depthBuffer[pixelIndex] = depth;
            }
        }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((point.X - a.X) * (b.Y - a.Y)) - ((point.Y - a.Y) * (b.X - a.X));

    private static void FillBackground(byte[] pixels, bool transparent)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = transparent ? (byte)0 : (byte)24;
            pixels[index + 1] = transparent ? (byte)0 : (byte)27;
            pixels[index + 2] = transparent ? (byte)0 : (byte)34;
            pixels[index + 3] = transparent ? (byte)0 : (byte)255;
        }
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
