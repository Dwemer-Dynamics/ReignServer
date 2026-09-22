using System.Numerics;

namespace Bannerlord.NativeCharacterImageGenerator;

internal readonly record struct RenderVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    Vector3 Binormal,
    Vector2 Uv);

internal readonly record struct RenderFace(int V0, int V1, int V2);

internal sealed record RenderMesh(
    string Name,
    RenderVertex[] Vertices,
    RenderFace[] Faces,
    TextureSampler? DiffuseTexture,
    TextureSampler? ColorMaskTexture,
    TextureSampler? NormalTexture,
    TextureSampler? SpecularTexture,
    Vector4 FallbackColor,
    Vector4 PrimaryColor,
    Vector4 SecondaryColor,
    bool UseDoubleColorMap,
    bool UseAlpha,
    float NormalMapPower,
    float SpecularCoefficient,
    float GlossCoefficient);

internal sealed record RenderResult(int Width, int Height, byte[] Rgba, int TriangleCount);
