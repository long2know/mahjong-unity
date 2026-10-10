using System;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Layout;
using Mahjong.Renderer.Rendering;
using UnityEngine;

namespace Mahjong.Renderer.Input
{
    public readonly struct Pick
    {
        public readonly PickKind Kind;
        public readonly string Id;
        public readonly bool Found;
        public Pick(PickKind kind, string id) { Kind = kind; Id = id; Found = true; }
    }

    public sealed class CameraPicker
    {
        private readonly RendererScene scene;
        private readonly RendererState state;
        private readonly RaycastHit[] hits = new RaycastHit[128];
        public CameraPicker(RendererScene scene, RendererState state) { this.scene = scene; this.state = state; }

        public Camera At(Vector2 position)
        {
            if (state.Frame == null || state.Viewport == null || scene.ActiveFrame == null ||
                state.Viewport.ResizeRevision != scene.ActiveResizeRevision ||
                state.Frame.Session.ViewSeat != scene.ActiveFrame.Session.ViewSeat ||
                state.Frame.Presentation.BoardLayout != scene.ActiveFrame.Presentation.BoardLayout ||
                state.Frame.Presentation.HandLayout != scene.ActiveFrame.Presentation.HandLayout ||
                state.Frame.Presentation.ViewMode != scene.ActiveFrame.Presentation.ViewMode ||
                state.Frame.Presentation.Zoom != scene.ActiveFrame.Presentation.Zoom ||
                state.Frame.Presentation.LookDown != scene.ActiveFrame.Presentation.LookDown)
                return null;
            if (scene.HandCamera.enabled && scene.HandCamera.pixelRect.Contains(position)) return scene.HandCamera;
            if (scene.TableCamera.enabled && scene.TableCamera.pixelRect.Contains(position)) return scene.TableCamera;
            return null;
        }

        public Pick Entity(Vector2 position, bool touch)
        {
            Camera camera = At(position);
            if (camera == null) return default;
            int layer = camera == scene.HandCamera ? RendererLayers.Hand : RendererLayers.Table;
            Pick direct = Ray(camera, position, 1 << layer);
            if (direct.Found || !touch || state.Viewport == null) return direct;
            float radiusX = 22 * Screen.width / (float)state.Viewport.WidthCssPx;
            float radiusY = 22 * Screen.height / (float)state.Viewport.HeightCssPx;
            float best = 1;
            Pick nearest = default;
            foreach (var pair in scene.ActiveEntities)
            {
                EntityView view = pair.Value;
                if (view.Root.layer != layer || !state.Entities.TryGetValue(pair.Key, out Entity entity) ||
                    !entity.Interaction.Any) continue;
                Vector3 projected = camera.WorldToScreenPoint(view.Root.transform.position);
                if (projected.z < camera.nearClipPlane || !camera.pixelRect.Contains(projected)) continue;
                Vector2 delta = (Vector2)projected - position;
                float distance = delta.x * delta.x / (radiusX * radiusX) + delta.y * delta.y / (radiusY * radiusY);
                if (distance >= best) continue;
                best = distance;
                nearest = new Pick(PickKind.Entity, pair.Key);
            }
            return nearest;
        }

        public Pick Slot(Vector2 position)
        {
            Camera camera = At(position);
            if (camera == null) return default;
            int layer = camera == scene.HandCamera ? RendererLayers.HandSlots : RendererLayers.TableSlots;
            return Ray(camera, position, 1 << layer);
        }

        private Pick Ray(Camera camera, Vector2 position, int mask)
        {
            int count = Physics.RaycastNonAlloc(camera.ScreenPointToRay(position), hits, camera.farClipPlane,
                mask, QueryTriggerInteraction.Ignore);
            float distance = float.PositiveInfinity;
            PickTarget chosen = null;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].distance >= distance) continue;
                var target = hits[i].collider.GetComponent<PickTarget>();
                if (target == null) continue;
                chosen = target;
                distance = hits[i].distance;
            }
            return chosen == null ? default : new Pick(chosen.Kind, chosen.Id);
        }
    }
}
