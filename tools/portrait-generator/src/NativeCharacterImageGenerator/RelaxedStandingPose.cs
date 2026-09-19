using System.Numerics;

namespace Bannerlord.NativeCharacterImageGenerator;

internal static class RelaxedStandingPose
{
    public static RenderMesh[] Apply(IEnumerable<RenderMesh> source) =>
        source.Select(mesh => mesh with
        {
            Vertices = mesh.Vertices.Select(PoseVertex).ToArray()
        }).ToArray();

    private static RenderVertex PoseVertex(RenderVertex vertex)
    {
        var position = vertex.Position;
        var side = MathF.Sign(position.X);
        var distanceFromCenter = MathF.Abs(position.X);
        if (side == 0 || distanceFromCenter < 0.16f || position.Z < 0.82f || position.Z > 1.66f)
        {
            return vertex;
        }

        var blend = SmoothStep(0.16f, 0.34f, distanceFromCenter);
        var shoulder = new Vector3(side * 0.20f, 0f, 1.48f);
        var angle = side * DegreesToRadians(52f);
        var rotation = Matrix4x4.CreateRotationY(angle);
        var posedPosition = shoulder + Vector3.Transform(position - shoulder, rotation);
        var posedNormal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, rotation));

        return vertex with
        {
            Position = Vector3.Lerp(position, posedPosition, blend),
            Normal = Vector3.Normalize(Vector3.Lerp(vertex.Normal, posedNormal, blend))
        };
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        var amount = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return amount * amount * (3f - (2f * amount));
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
