using System;
using UnityEngine;

namespace Mahjong.Renderer.Layout
{
    public readonly struct CameraPose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly float OrthographicSize;
        public CameraPose(Vector3 position, Quaternion rotation, float size)
        {
            Position = position;
            Rotation = rotation;
            OrthographicSize = size;
        }
    }

    public static class CameraFit
    {
        public const float FieldOfView = 40;

        public static CameraPose Fit(Bounds bounds, float aspect, bool flat, double lookDown, double zoom, bool compact = false)
        {
            float elevation = flat ? 90 : Mathf.Lerp(compact ? 78 : 38, compact ? 88 : 78, (float)lookDown);
            Quaternion rotation = Quaternion.Euler(elevation, 0, 0);
            Vector3 forward = rotation * Vector3.forward;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            float maxRight = 0;
            float maxUp = 0;
            float distance = 0;
            float tangent = Mathf.Tan(FieldOfView * Mathf.Deg2Rad / 2);
            float margin = compact ? Mathf.Lerp(1.10f, 1.02f, ((float)zoom + 1) / 2) :
                Mathf.Lerp(1.28f, 1.04f, ((float)zoom + 1) / 2);
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 point = Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                        maxRight = Mathf.Max(maxRight, Mathf.Abs(Vector3.Dot(point, right)));
                        maxUp = Mathf.Max(maxUp, Mathf.Abs(Vector3.Dot(point, up)));
                        float fit = Mathf.Max(Mathf.Abs(Vector3.Dot(point, up)),
                            Mathf.Abs(Vector3.Dot(point, right)) / Mathf.Max(0.01f, aspect));
                        distance = Mathf.Max(distance, fit * margin / tangent - Vector3.Dot(point, forward));
                    }
            // Zoom changes margins, never crops canonical groups out of the safe viewport.
            float size = Mathf.Max(maxUp, maxRight / Mathf.Max(0.01f, aspect)) * margin;
            distance = flat ? bounds.size.magnitude + 1 : distance + (compact ? 0.02f : 0.1f);
            return new CameraPose(bounds.center - forward * distance, rotation, size);
        }
    }
}
