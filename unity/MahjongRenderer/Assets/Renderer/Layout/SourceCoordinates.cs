using System;
using Mahjong.Renderer.Contract;
using UnityEngine;
using Pose = Mahjong.Renderer.Contract.Pose;

namespace Mahjong.Renderer.Layout
{
    public readonly struct RenderPose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Size;
        public readonly float Scale;
        public RenderPose(Vector3 position, Quaternion rotation, Vector3 size, float scale = 1)
        {
            Position = position;
            Rotation = rotation;
            Size = size;
            Scale = scale;
        }
    }

    public static class SourceCoordinates
    {
        public const float Unit = 0.01f;
        public const double Center = 87;

        public static Vector3 Position(Vector3Dto value) => new Vector3(
            Float((value.X - Center) * Unit), Float(value.Z * Unit), Float((value.Y - Center) * Unit));

        public static Vector3 Size(Vector3Dto value) =>
            new Vector3(Float(value.X * Unit), Float(value.Z * Unit), Float(value.Y * Unit));

        public static Quaternion Rotation(QuaternionDto value)
        {
            double norm = Math.Sqrt(value.X * value.X + value.Y * value.Y +
                                    value.Z * value.Z + value.W * value.W);
            if (double.IsInfinity(norm) || double.IsNaN(norm) || norm < 1e-8 || Math.Abs(norm - 1) > 0.002)
                throw new ContractException("invalid-message", "Pose quaternion must be a finite unit quaternion.");
            return new Quaternion(Float(-value.X / norm), Float(-value.Z / norm),
                Float(-value.Y / norm), Float(value.W / norm));
        }

        public static RenderPose Convert(Pose pose, double scale = 1) =>
            new RenderPose(Position(pose.Position), Rotation(pose.Rotation), Size(pose.Size), Float(scale));

        public static Quaternion ViewRotation(int? viewSeat) =>
            Quaternion.AngleAxis(90 * (viewSeat ?? 0), Vector3.up);

        public static Quaternion SeatRotation(int seat) => Quaternion.AngleAxis(-90 * seat, Vector3.up);
        public static int RelativeSeat(int seat, int? viewSeat) => (seat - (viewSeat ?? 0) + 4) % 4;

        public static Vector3 MeshSize(string kind)
        {
            switch (kind)
            {
                case "TILE": return new Vector3(0.06f, 0.04f, 0.09f);
                case "STICK": return new Vector3(0.20f, 0.01f, 0.02f);
                case "MARKER": return new Vector3(0.12f, 0.01f, 0.06f);
                default: throw new ContractException("invalid-message", "Unsupported mesh kind.");
            }
        }

        public static Bounds OrientedBounds(RenderPose pose, string kind)
        {
            Vector3 half = MeshSize(kind) * pose.Scale * 0.5f;
            var bounds = new Bounds(pose.Position, Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        bounds.Encapsulate(pose.Position + pose.Rotation *
                            Vector3.Scale(half, new Vector3(x, y, z)));
            return bounds;
        }

        public static float Float(double value)
        {
            if (double.IsInfinity(value) || double.IsNaN(value) || Math.Abs(value) > 1e16 ||
                value != 0 && (float)value == 0)
                throw new ContractException("invalid-message", "Geometry cannot be represented by Unity.");
            return (float)value;
        }
    }
}
