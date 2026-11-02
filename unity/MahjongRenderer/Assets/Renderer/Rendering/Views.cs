using System;
using Mahjong.Renderer.Art;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Mahjong.Renderer.Layout;
using UnityEngine;

namespace Mahjong.Renderer.Rendering
{
    public static class RendererLayers
    {
        public const int Table = 8;
        public const int Hand = 9;
        public const int TableSlots = 10;
        public const int HandSlots = 11;
    }

    public sealed class SurfaceView
    {
        public readonly GameObject Object;
        public readonly MeshRenderer Renderer;
        public readonly MeshFilter Filter;
        private readonly ProceduralArt art;
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        public SurfaceView(Transform parent, string name, Mesh mesh, ProceduralArt art)
        {
            this.art = art;
            Object = new GameObject(name);
            Object.transform.SetParent(parent, false);
            Filter = Object.AddComponent<MeshFilter>();
            Filter.sharedMesh = mesh;
            Renderer = Object.AddComponent<MeshRenderer>();
            Renderer.sharedMaterial = art.Surface;
            Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Renderer.receiveShadows = false;
        }

        public void Paint(Color color, Texture texture = null, Rect? uv = null,
            Color? paper = null, Color? baseColor = null, float gloss = 0.06f, bool unlit = false, int effect = 0)
        {
            properties.Clear();
            properties.SetColor("_Color", color);
            properties.SetTexture("_MainTex", texture != null ? texture : Texture2D.whiteTexture);
            Rect rect = uv ?? new Rect(0, 0, 1, 1);
            properties.SetVector("_MainTex_ST", new Vector4(rect.width, rect.height, rect.x, rect.y));
            properties.SetColor("_Paper", paper ?? Color.clear);
            properties.SetColor("_BaseColor", baseColor ?? Color.white);
            properties.SetFloat("_Band", baseColor.HasValue ? 1 : 0);
            properties.SetFloat("_Gloss", unlit ? 0 : gloss);
            properties.SetFloat("_Unlit", unlit ? 1 : 0);
            properties.SetFloat("_Effect", effect);
            Material material = effect == 1 || effect == 2 || color.a < 0.99f ||
                paper.HasValue && paper.Value.a < 0.99f ? art.Decal :
                effect == 3 ? art.Felt : effect == 4 ? art.Wood : art.Surface;
            if (Renderer.sharedMaterial != material) Renderer.sharedMaterial = material;
            Renderer.SetPropertyBlock(properties);
        }

        public void Pose(Vector3 position, Quaternion rotation, Vector3 scale, int layer)
        {
            Object.transform.localPosition = position;
            Object.transform.localRotation = rotation;
            Object.transform.localScale = scale;
            Object.layer = layer;
        }
    }

    public sealed class EntityView
    {
        public readonly GameObject Root;
        public readonly PickTarget Target;
        private readonly SurfaceView body;
        private readonly SurfaceView face;
        private readonly SurfaceView back;
        private readonly SurfaceView cue;
        private readonly SurfaceView shadow;
        private readonly BoxCollider collider;
        private readonly ProceduralArt art;
        private EntityPlacement placement;
        public RenderPose VisualPose { get; private set; }
        public int? VisibleFaceIndex { get; private set; }
        public string VisibleFaceLabel { get; private set; }
        public Entity Current => placement?.Entity;

        public EntityView(Transform parent, ProceduralArt art)
        {
            this.art = art;
            Root = new GameObject("Renderer entity");
            Root.transform.SetParent(parent, false);
            Target = Root.AddComponent<PickTarget>();
            collider = Root.AddComponent<BoxCollider>();
            body = new SurfaceView(Root.transform, "Original body", art.Box, art);
            face = new SurfaceView(Root.transform, "Concealed surface", art.Quad, art);
            back = new SurfaceView(Root.transform, "Common back", art.Quad, art);
            cue = new SurfaceView(parent, "Selection cue", art.Quad, art);
            shadow = new SurfaceView(parent, "Soft tile contact", art.Quad, art);
        }

        public void Apply(EntityPlacement item, bool labels)
        {
            placement = item;
            Entity entity = item.Entity;
            RenderPose pose = item.Pose;
            int layer = item.Separate ? RendererLayers.Hand : RendererLayers.Table;
            Vector3 size = SourceCoordinates.MeshSize(entity.Kind);
            Root.layer = layer;
            Target.Bind(PickKind.Entity, entity.EntityId);
            collider.size = size;
            collider.enabled = true;
            body.Filter.sharedMesh = entity.Kind == "TILE" ? art.RoundedBox : art.Box;
            body.Pose(Vector3.zero, Quaternion.identity, size, layer);
            Color ivory = ProceduralArt.Ivory;
            if (entity.Display.Temporary) ivory.a = 0.75f;
            if (entity.Display.Bottom) ivory *= new Color(0.94f, 0.94f, 0.94f, 1);
            body.Paint(ivory, baseColor: entity.Kind == "TILE" ?
                (Color?)ProceduralArt.BackColor(entity.Appearance.BackIndex) : null, gloss: 0.10f);
            face.Pose(new Vector3(0, size.y / 2 + 0.0008f, 0), Quaternion.identity,
                new Vector3(size.x * 0.84f, 1, size.z * 0.84f), layer);
            back.Pose(new Vector3(0, -size.y / 2 - 0.0008f, 0), Quaternion.Euler(180, 0, 0),
                new Vector3(size.x * 0.84f, 1, size.z * 0.84f), layer);
            VisibleFaceIndex = null;
            VisibleFaceLabel = null;
            if (entity.Kind == "TILE")
            {
                bool visible = entity.Appearance.KnownFace && entity.Appearance.ShowFace &&
                               entity.Appearance.FaceIndex.HasValue;
                face.Object.name = visible ? "Entitled face" : "Concealed common back";
                if (visible)
                {
                    VisibleFaceIndex = entity.Appearance.FaceIndex.Value;
                    VisibleFaceLabel = ProceduralArt.FaceLabel(VisibleFaceIndex.Value);
                    face.Paint(Color.white, labels ? art.Faces : art.FacesWithoutLabels,
                        ProceduralArt.FaceUv(VisibleFaceIndex.Value), paper: ivory, gloss: 0.10f);
                }
                else face.Paint(Color.white, art.Backs[entity.Appearance.BackIndex], paper: ivory, gloss: 0.10f);
                back.Paint(Color.white, art.Backs[entity.Appearance.BackIndex], paper: ivory, gloss: 0.10f);
            }
            else
            {
                face.Object.name = entity.Kind == "STICK" ? "Point stick value" : "Seat marker";
                face.Paint(Color.white, entity.Kind == "STICK" ?
                    art.Sticks[entity.Appearance.ValueIndex.Value] : art.Marker);
                back.Paint(new Color(0.88f, 0.86f, 0.76f));
            }
            SetVisualPose(pose);
            Root.SetActive(true);
            shadow.Object.SetActive(true);
            UpdateCue(false, false);
        }

        public void SetVisualPose(RenderPose pose)
        {
            VisualPose = pose;
            Root.transform.localPosition = pose.Position;
            Root.transform.localRotation = pose.Rotation;
            Root.transform.localScale = Vector3.one * pose.Scale;
            if (placement == null) return;
            Bounds bounds = SourceCoordinates.OrientedBounds(pose, placement.Entity.Kind);
            float ground = placement.Separate ? 0.0001f : -0.0045f;
            shadow.Pose(new Vector3(bounds.center.x + 0.002f, ground, bounds.center.z + 0.003f),
                Quaternion.identity, new Vector3(bounds.size.x + 0.016f, 1, bounds.size.z + 0.016f), Root.layer);
            shadow.Paint(new Color(0.01f, 0.025f, 0.027f, 0.30f), unlit: true, effect: 1);
            cue.Pose(new Vector3(bounds.center.x, ground + 0.0002f, bounds.center.z),
                Quaternion.identity, new Vector3(bounds.size.x + 0.015f, 1, bounds.size.z + 0.015f), Root.layer);
        }

        public void UpdateCue(bool selected, bool hovered)
        {
            if (placement == null) return;
            Contract.Display source = placement.Entity.Display;
            bool enabled = selected || hovered || source.Selected || source.Hovered || source.Held || source.Highlight > 0;
            cue.Object.SetActive(enabled);
            cue.Paint(source.Held ? new Color(0.40f, 0.85f, 0.90f, 0.9f) :
                selected || source.Selected ? new Color(0.97f, 0.78f, 0.39f, 0.95f) :
                new Color(0.97f, 0.87f, 0.63f, 0.85f), unlit: true, effect: 2);
        }

        public void Clear()
        {
            placement = null;
            VisibleFaceIndex = null;
            VisibleFaceLabel = null;
            Target.Clear();
            collider.enabled = false;
            face.Object.name = "Concealed common back";
            face.Paint(Color.white, art.Backs[0]);
            back.Paint(Color.white, art.Backs[0]);
            Root.SetActive(false);
            cue.Object.SetActive(false);
            shadow.Object.SetActive(false);
        }
    }

    public sealed class SlotView
    {
        public readonly GameObject Root;
        private readonly PickTarget target;
        private readonly BoxCollider collider;
        private readonly SurfaceView shadow;
        private SlotPlacement placement;

        public SlotView(Transform parent, ProceduralArt art)
        {
            Root = new GameObject("Canonical slot target");
            Root.transform.SetParent(parent, false);
            target = Root.AddComponent<PickTarget>();
            collider = Root.AddComponent<BoxCollider>();
            shadow = new SurfaceView(parent, "Interactive slot outline", art.Quad, art);
        }

        public void Apply(SlotPlacement item)
        {
            placement = item;
            Bounds bounds = SourceCoordinates.OrientedBounds(item.Poses[0], item.Slot.Kind);
            foreach (RenderPose pose in item.Poses) bounds.Encapsulate(SourceCoordinates.OrientedBounds(pose, item.Slot.Kind));
            Root.transform.localPosition = bounds.center;
            Root.transform.localRotation = Quaternion.identity;
            Root.transform.localScale = Vector3.one;
            Root.layer = item.Separate ? RendererLayers.HandSlots : RendererLayers.TableSlots;
            collider.size = new Vector3(Mathf.Max(0.015f, bounds.size.x), Mathf.Max(0.008f, bounds.size.y),
                Mathf.Max(0.015f, bounds.size.z));
            collider.enabled = true;
            target.Bind(PickKind.Slot, item.Slot.SlotName);
            int layer = item.Separate ? RendererLayers.Hand : RendererLayers.Table;
            int shadowIndex = Mathf.Min(item.Slot.ShadowRotationIndex, item.Poses.Length - 1);
            Bounds shadowBounds = SourceCoordinates.OrientedBounds(item.Poses[shadowIndex], item.Slot.Kind);
            shadow.Pose(new Vector3(shadowBounds.center.x, item.Separate ? 0.0003f : 0.0038f, shadowBounds.center.z),
                Quaternion.identity, new Vector3(shadowBounds.size.x + 0.008f, 1, shadowBounds.size.z + 0.008f), layer);
            Root.SetActive(true);
            UpdateCue(false, false);
        }

        public void UpdateCue(bool drop, bool preview)
        {
            if (placement == null) return;
            // Canonical slot colliders remain live; empty addresses are not giant table pads.
            shadow.Object.SetActive(drop || preview);
            shadow.Paint(drop ? new Color(0.4f, 0.85f, 0.9f, 0.9f) :
                new Color(0.95f, 0.77f, 0.4f, 0.7f), unlit: true, effect: 2);
        }

        public void Clear()
        {
            placement = null;
            target.Clear();
            collider.enabled = false;
            Root.SetActive(false);
            shadow.Object.SetActive(false);
        }
    }

    public sealed class LabelView
    {
        private readonly SurfaceView surface;
        private string currentText;
        private float currentHeight;
        private Mesh ownedMesh;
        public LabelView(Transform parent, ProceduralArt art) { surface = new SurfaceView(parent, "Public vector label", art.Quad, art); }

        public void Apply(string text, float height, Vector3 position, Quaternion rotation, int layer, Color color)
        {
            if (text != currentText || height != currentHeight)
            {
                EngineObjects.Release(ownedMesh);
                ownedMesh = BitmapGlyphs.Mesh(text, height);
                surface.Filter.sharedMesh = ownedMesh;
                currentText = text;
                currentHeight = height;
            }
            surface.Pose(position, rotation, Vector3.one, layer);
            surface.Paint(color, unlit: true);
            surface.Object.SetActive(true);
        }

        public void Clear() { surface.Object.SetActive(false); }
        public void Dispose() { EngineObjects.Release(ownedMesh); }
    }
}
