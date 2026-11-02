using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Mahjong.Renderer.Art;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Mahjong.Renderer.Layout;
using Mahjong.Renderer.Rendering;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Mahjong.Renderer.Tests
{
    public sealed class RenderingTests
    {
        [Test]
        public void ImportedProjectKeepsDistinctRenderingAndPickingLayers()
        {
            Assert.That(LayerMask.LayerToName(RendererLayers.Table), Is.EqualTo("MahjongTable"));
            Assert.That(LayerMask.LayerToName(RendererLayers.Hand), Is.EqualTo("MahjongHand"));
            Assert.That(LayerMask.LayerToName(RendererLayers.TableSlots), Is.EqualTo("MahjongTableSlots"));
            Assert.That(LayerMask.LayerToName(RendererLayers.HandSlots), Is.EqualTo("MahjongHandSlots"));
            Assert.That(new[] { RendererLayers.Table, RendererLayers.Hand,
                RendererLayers.TableSlots, RendererLayers.HandSlots }.Distinct().Count(), Is.EqualTo(4));
        }

        [Test]
        public void AssistLabelsMatchCreatorHonorOrderAndDistinguishAllThirtySevenFaces()
        {
            Assert.That(Enumerable.Range(0, 37).Select(ProceduralArt.FaceLabel).Distinct().Count(), Is.EqualTo(37));
            Assert.That(ProceduralArt.FaceLabel(29), Is.EqualTo("W"));
            Assert.That(ProceduralArt.FaceLabel(31), Is.EqualTo("WD"));
            Assert.That(ProceduralArt.FaceLabel(32), Is.EqualTo("GD"));
            Assert.That(ProceduralArt.FaceLabel(33), Is.EqualTo("RD"));
            string[] names = Enumerable.Range(1, 9).Select(i => "Man" + i)
                .Concat(Enumerable.Range(1, 9).Select(i => "Pin" + i))
                .Concat(Enumerable.Range(1, 9).Select(i => "Sou" + i))
                .Concat(new[] { "Ton", "Nan", "Shaa", "Pei", "Haku", "Hatsu", "Chun",
                    "Man5-Dora", "Pin5-Dora", "Sou5-Dora" }).ToArray();
            Assert.That(Enumerable.Range(0, 37).Select(ProceduralArt.FaceName), Is.EqualTo(names));
        }

        [TestCase(-1)]
        [TestCase(37)]
        public void InvalidArtworkIndicesCannotAliasAnEntitledAtlasCell(int index)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ProceduralArt.FaceUv(index));
            Assert.Throws<ArgumentOutOfRangeException>(() => ProceduralArt.FaceLabel(index));
        }

        [Test]
        public void ImportedCreatorFilesAndActualRuntimeTexturesMatchRecordedImmutableHashes()
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            JObject provenance = JObject.Parse(File.ReadAllText(Path.Combine(project, "Documentation/art-provenance.json")));
            JObject artwork = (JObject)provenance["importedArtwork"];
            Assert.That(artwork["commitSha"].Value<string>(), Is.EqualTo("26e127ba2117f45cdce5ea0225748cc0cfad3169"));
            Assert.That(artwork["license"].Value<string>(), Is.EqualTo("CC0-1.0"));
            Assert.That(artwork["originalFiles"].Count(), Is.EqualTo(78));
            foreach (JToken file in artwork["originalFiles"].Concat(provenance["runtimeTextures"]))
            {
                string relative = file["path"].Value<string>();
                Assert.That(relative.StartsWith("Assets/", StringComparison.Ordinal), Is.True, relative);
                byte[] bytes = File.ReadAllBytes(Path.Combine(project, relative));
                Assert.That(bytes.Length, Is.EqualTo(file["bytes"].Value<int>()), relative);
                using (var hash = SHA256.Create())
                    Assert.That(Hex(hash.ComputeHash(bytes)), Is.EqualTo(file["sha256"].Value<string>()), relative);
                if (file["gitBlobSha"] != null)
                {
                    byte[] header = System.Text.Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
                    using (var hash = SHA1.Create())
                        Assert.That(Hex(hash.ComputeHash(header.Concat(bytes).ToArray())),
                            Is.EqualTo(file["gitBlobSha"].Value<string>()), relative);
                }
            }
            byte[] notice = File.ReadAllBytes(Path.Combine(project, "Assets/Art/FluffyStuff/LICENSE.md"));
            using (var hash = SHA256.Create())
                Assert.That(Hex(hash.ComputeHash(notice)), Is.EqualTo("17996c5e7de4cbbc71d0e2db73cb63b0590632cc75be9ab2d885edfae5b9f737"));
            Assert.That(provenance["importedTable"]["sha256"].Value<string>(),
                Is.EqualTo("3028ff6dd0c1c5f16e99e22fb96790f0148c4465368c77c776529973f8874e0f"));
        }

        [Test]
        public void ActualAtlasPixelsPreserveAllCreatorGlyphsOrientationAspectAndBlankWhiteDragon()
        {
            Png atlas = ReadPng("Assets/Resources/Art/Faces.png");
            Png labelled = ReadPng("Assets/Resources/Art/FacesLabelled.png");
            Assert.That(new[] { atlas.Width, atlas.Height }, Is.EqualTo(new[] { ProceduralArt.AtlasSize, ProceduralArt.AtlasSize }));
            for (int index = 0; index < 37; index++)
            {
                string name = ProceduralArt.FaceName(index);
                Png source = ReadPng("Assets/Art/FluffyStuff/Export/Regular/" + name + ".png");
                Assert.That(new[] { source.Width, source.Height }, Is.EqualTo(new[] { 600, 800 }), name);
                int left = index % 8 * ProceduralArt.CellWidth;
                int top = atlas.Height - (index / 8 + 1) * ProceduralArt.CellHeight;
                Rect uv = ProceduralArt.FaceUv(index);
                Assert.That(uv.x * atlas.Width, Is.EqualTo(left + 0.5f).Within(0.001), name);
                Assert.That(uv.y * atlas.Height,
                    Is.EqualTo(index / 8 * ProceduralArt.CellHeight + 0.5f).Within(0.001), name);
                Assert.That(168f / 224, Is.EqualTo(source.Width / (float)source.Height), name);
                long alpha = 0;
                for (int y = 0; y < ProceduralArt.CellHeight; y++)
                    for (int x = 0; x < ProceduralArt.CellWidth; x++)
                        alpha += atlas.Pixels[((top + y) * atlas.Width + left + x) * 4 + 3];
                Assert.That(alpha == 0, Is.EqualTo(index == 31), name);
                for (int by = 0; by < 16; by++)
                    for (int bx = 0; bx < 12; bx++)
                        Assert.That(AlphaMean(atlas, left + 12 + bx * 14, top + 20 + by * 14, 14),
                            Is.EqualTo(AlphaMean(source, bx * 50, by * 50, 50)).Within(0.035),
                            name + " glyph orientation/coverage cell " + bx + "," + by);
                Assert.That(AlphaMean(labelled, left + 80, top + 256, 32), Is.GreaterThan(0), name + " assist label");
            }
            Assert.That(ReadPng("Assets/Resources/Art/Back0.png").Pixels,
                Is.Not.EqualTo(ReadPng("Assets/Resources/Art/Back1.png").Pixels));
        }

        [Test]
        public void ImportedTexturesUseMipFilteringNoCpuReadbackAndBoundedFallbackMemory()
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                Texture2D[] imported = { art.Faces, art.FacesWithoutLabels, art.Backs[0], art.Backs[1], art.TableFabric };
                long rgbaFallback = 0;
                foreach (Texture2D texture in imported)
                {
                    string path = AssetDatabase.GetAssetPath(texture);
                    Assert.That(path.StartsWith("Assets/Resources/Art/", StringComparison.Ordinal), Is.True, texture.name);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    Assert.That(importer.isReadable, Is.False, path);
                    Assert.That(texture.isReadable, Is.False, path);
                    Assert.That(importer.mipmapEnabled, Is.True, path);
                    Assert.That(texture.mipmapCount, Is.GreaterThan(1), path);
                    Assert.That(texture.filterMode, Is.EqualTo(FilterMode.Trilinear), path);
                    Assert.That(texture.anisoLevel, Is.EqualTo(texture == art.TableFabric ? 2 : 1), path);
                    Assert.That(importer.anisoLevel, Is.EqualTo(texture == art.TableFabric ? 4 : 1), path);
                    Assert.That(importer.npotScale, Is.EqualTo(TextureImporterNPOTScale.None), path);
                    Assert.That(importer.textureCompression, Is.EqualTo(TextureImporterCompression.Compressed), path);
                    int width = texture.width, height = texture.height;
                    while (true)
                    {
                        rgbaFallback += (long)width * height * 4;
                        if (width == 1 && height == 1) break;
                        width = Mathf.Max(1, width / 2);
                        height = Mathf.Max(1, height / 2);
                    }
                }
                Assert.That(rgbaFallback, Is.LessThan(28000000));
                Assert.That(art.Faces.wrapMode, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(art.TableFabric.wrapMode, Is.EqualTo(TextureWrapMode.Repeat));
                Assert.That(art.TableFabric.anisoLevel, Is.EqualTo(2));
                Assert.That(art.Dice.All(texture => !texture.isReadable && texture.filterMode == FilterMode.Trilinear), Is.True);
            }
        }

        [Test]
        public void BeveledBodiesStayWithinCanonicalColliderBoundsAndUseCurvedNormals()
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                Assert.That(art.RoundedBox.vertexCount, Is.EqualTo(96));
                Assert.That(art.RoundedBox.triangles.Length / 3, Is.EqualTo(44));
                Assert.That(art.TableBox.vertexCount, Is.EqualTo(96));
                Assert.That(art.TableBox.triangles.Length / 3, Is.EqualTo(108));
                Assert.That(art.RoundedBox.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt16));
                Assert.That(art.RoundedBox.tangents, Is.Empty);
                Assert.That(art.RoundedBox.colors, Is.Empty);
                Assert.That(art.RoundedBox.uv3, Is.Empty);
                long meshPayload = art.RoundedBox.vertexCount * (3 + 3 + 2 + 2) * sizeof(float) +
                    art.RoundedBox.triangles.Length * sizeof(ushort);
                Assert.That(meshPayload, Is.LessThan(4600),
                    "Shared rounded mesh retains positions/normals/two UVs without per-entity tessellation or extra streams");
                Assert.That(art.RoundedBox.bounds.size, Is.EqualTo(Vector3.one));
                Assert.That(art.RoundedBox.normals.Any(normal => Mathf.Abs(normal.x) > 0.1f &&
                    Mathf.Abs(normal.y) > 0.1f && Mathf.Abs(normal.z) > 0.1f), Is.True);
                Assert.That(art.RoundedBox.vertices.All(vertex =>
                    Mathf.Abs(vertex.x) <= 0.500001f && Mathf.Abs(vertex.y) <= 0.500001f && Mathf.Abs(vertex.z) <= 0.500001f), Is.True);
                Assert.That(art.RoundedBox.normals.All(normal => Mathf.Abs(normal.magnitude - 1) < 0.00001f), Is.True);
                Assert.That(art.RoundedBox.uv2.Length, Is.EqualTo(art.RoundedBox.vertexCount));
                for (int n = 0; n < art.RoundedBox.vertexCount; n++)
                    Assert.That(art.RoundedBox.uv2[n].x, Is.EqualTo(art.RoundedBox.vertices[n].y),
                        "Batch-stable base tint must use normalized mesh height, not transformed world position");
                RendererState state = SourceFixtures.State();
                var parent = new GameObject("Beveled body contract");
                try
                {
                    LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                    var view = new EntityView(parent.transform, art);
                    view.Apply(plan.Entities["fixture-own-tile"], false);
                    Assert.That(view.Root.GetComponent<BoxCollider>().size, Is.EqualTo(SourceCoordinates.MeshSize("TILE")));
                    Assert.That(view.Root.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(1));
                    Assert.That(view.Root.GetComponentsInChildren<MeshFilter>().Single(filter =>
                        filter.gameObject.name == "Original body").sharedMesh, Is.SameAs(art.RoundedBox));
                    Assert.That(parent.GetComponentsInChildren<Collider>().Length, Is.EqualTo(1),
                        "Contact shadow/selection art must not participate in native raycasts");
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void EveryEntitledIndexBindsItsRealAtlasCellIncludingBlankHakuAndConcealmentRebindsCommonBack(int backIndex)
        {
            RendererState state = SourceFixtures.State(backIndex == 1);
            var parent = new GameObject("Actual atlas binding");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                var actions = new SemanticActions(state, _ => { });
                for (int index = 0; index < 37; index++)
                {
                    RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
                    frame.FrameId++;
                    frame.Presentation.TileLabels = false;
                    frame.Entities[0].Appearance.FaceIndex = index;
                    frame.Entities[0].Appearance.ShowFace = true;
                    state.ApplyFrame(frame);
                    scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions, state.Viewport.ResizeRevision);
                    EntityView view = scene.ActiveEntities["fixture-own-tile"];
                    Assert.That(view.VisibleFaceIndex, Is.EqualTo(index));
                    Assert.That(view.VisibleFaceLabel, Is.EqualTo(ProceduralArt.FaceLabel(index)));
                    MeshRenderer face = view.Root.GetComponentsInChildren<MeshRenderer>().Single(renderer => renderer.name == "Entitled face");
                    var properties = new MaterialPropertyBlock();
                    face.GetPropertyBlock(properties);
                    Assert.That(properties.GetTexture("_MainTex"), Is.SameAs(Resources.Load<Texture2D>("Art/Faces")));
                    Rect uv = ProceduralArt.FaceUv(index);
                    Assert.That(properties.GetVector("_MainTex_ST"), Is.EqualTo(new Vector4(uv.width, uv.height, uv.x, uv.y)));
                    Assert.That(properties.GetColor("_Paper").a, Is.EqualTo(1));
                }
                RendererFrame concealed = SourceFixtures.Copy(state.Frame, "RendererFrame");
                concealed.FrameId++;
                concealed.Entities[0].Appearance.ShowFace = false;
                concealed.Entities[0].Appearance.BackIndex = backIndex;
                state.ApplyFrame(concealed);
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions, state.Viewport.ResizeRevision);
                EntityView hidden = scene.ActiveEntities["fixture-own-tile"];
                Assert.That(hidden.VisibleFaceIndex, Is.Null);
                Assert.That(hidden.VisibleFaceLabel, Is.Null);
                MeshRenderer common = hidden.Root.GetComponentsInChildren<MeshRenderer>().Single(renderer => renderer.name == "Concealed common back");
                var block = new MaterialPropertyBlock();
                common.GetPropertyBlock(block);
                Assert.That(block.GetTexture("_MainTex"), Is.SameAs(Resources.Load<Texture2D>("Art/Back" + backIndex)));
                Assert.That(block.GetVector("_MainTex_ST"), Is.EqualTo(new Vector4(1, 1, 0, 0)));
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [Test]
        public void IdleSlotsKeepRealPhysicsTargetsWithoutVisiblePadsAndInteractiveOutlinesDoNotWriteDepth()
        {
            RendererState state = SourceFixtures.State();
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                var parent = new GameObject("Slot visual contract");
                try
                {
                    LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                    SlotPlacement placement = plan.Slots[state.Layout.Slots[0].SlotName];
                    placement.Slot.DrawShadow = true;
                    var view = new SlotView(parent.transform, art);
                    view.Apply(placement);
                    MeshRenderer outline = parent.GetComponentsInChildren<MeshRenderer>(true).Single();
                    Assert.That(outline.gameObject.activeSelf, Is.False, "Idle canonical slots must not paint block pads");
                    Assert.That(view.Root.GetComponent<BoxCollider>().enabled, Is.True);
                    Bounds bounds = SourceCoordinates.OrientedBounds(placement.Poses[0], placement.Slot.Kind);
                    foreach (RenderPose pose in placement.Poses)
                        bounds.Encapsulate(SourceCoordinates.OrientedBounds(pose, placement.Slot.Kind));
                    Assert.That(view.Root.transform.localPosition, Is.EqualTo(bounds.center));
                    Physics.SyncTransforms();
                    Assert.That(Physics.Raycast(new Ray(bounds.center + Vector3.up * 10, Vector3.down),
                        out RaycastHit hit, 20, 1 << view.Root.layer), Is.True);
                    Assert.That(hit.collider.GetComponent<PickTarget>().Id, Is.EqualTo(placement.Slot.SlotName));
                    view.UpdateCue(true, false);
                    Assert.That(outline.gameObject.activeSelf, Is.True);
                    Assert.That(outline.sharedMaterial, Is.SameAs(art.Decal));
                    Assert.That(outline.sharedMaterial.GetInt("_ZWrite"), Is.EqualTo(0));
                    Assert.That(outline.sharedMaterial.renderQueue, Is.EqualTo(3000));
                    view.UpdateCue(false, true);
                    Assert.That(outline.gameObject.activeSelf, Is.True);
                    view.UpdateCue(false, false);
                    Assert.That(outline.gameObject.activeSelf, Is.False);
                    Assert.That(view.Root.GetComponent<BoxCollider>().enabled, Is.True);
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [Test]
        public void ActualCamerasKeepSingleSampleTargetsWithoutHdrOrRenderPipelineDependencies()
        {
            var parent = new GameObject("Camera visual quality");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                Assert.That(QualitySettings.antiAliasing, Is.EqualTo(0));
                Assert.That(QualitySettings.anisotropicFiltering, Is.EqualTo(AnisotropicFiltering.Enable),
                    "ForceEnable promotes every imported1x/4x sampler to9x, including the full-screen felt");
                Assert.That(QualitySettings.resolutionScalingFixedDPIFactor, Is.EqualTo(1));
                Assert.That(QualitySettings.renderPipeline, Is.Null);
                Assert.That(scene.TableCamera.allowMSAA || scene.HandCamera.allowMSAA, Is.False);
                Assert.That(scene.TableCamera.allowHDR || scene.HandCamera.allowHDR, Is.False);
                Assert.That(scene.TableCamera.targetTexture, Is.Null);
                Assert.That(scene.HandCamera.targetTexture, Is.Null);
                Assert.That(scene.TableCamera.nearClipPlane, Is.EqualTo(0.025f));
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [Test]
        public void ActualSharedMaterialsReplaceOpaquePixelsAndBlendDecalsWithoutWritingDepth()
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                Assert.That(art.Surface.shader, Is.SameAs(art.Decal.shader));
                Assert.That(art.Surface.passCount, Is.EqualTo(1));
                Assert.That(art.Decal.passCount, Is.EqualTo(1));
                Assert.That(art.Surface.GetInt("_SrcBlend"), Is.EqualTo((int)UnityEngine.Rendering.BlendMode.One));
                Assert.That(art.Surface.GetInt("_DstBlend"), Is.EqualTo((int)UnityEngine.Rendering.BlendMode.Zero));
                Assert.That(art.Surface.GetInt("_ZWrite"), Is.EqualTo(1));
                Assert.That(art.Surface.GetTag("RenderType", false), Is.EqualTo("Opaque"));
                Assert.That(art.Surface.renderQueue, Is.EqualTo(2010));
                Assert.That(art.Decal.GetInt("_SrcBlend"), Is.EqualTo((int)UnityEngine.Rendering.BlendMode.SrcAlpha));
                Assert.That(art.Decal.GetInt("_DstBlend"), Is.EqualTo((int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                Assert.That(art.Decal.GetInt("_ZWrite"), Is.EqualTo(0));
                Assert.That(art.Decal.GetTag("RenderType", false), Is.EqualTo("Transparent"));
                Assert.That(art.Decal.renderQueue, Is.EqualTo(3000));
                Assert.That(art.Surface.shaderKeywords, Is.Empty);
                Assert.That(art.Felt.shaderKeywords, Is.EqualTo(new[] { "MAHJONG_FELT" }));
                Assert.That(art.Wood.shaderKeywords, Is.EqualTo(new[] { "MAHJONG_WOOD" }));
                Assert.That(art.Decal.shaderKeywords, Is.EqualTo(new[] { "MAHJONG_DECAL" }));
                foreach (Material material in new[] { art.Felt, art.Wood })
                {
                    Assert.That(material.shader, Is.SameAs(art.Surface.shader));
                    Assert.That(material.passCount, Is.EqualTo(1));
                    Assert.That(material.GetInt("_SrcBlend"), Is.EqualTo((int)UnityEngine.Rendering.BlendMode.One));
                    Assert.That(material.GetInt("_DstBlend"), Is.EqualTo((int)UnityEngine.Rendering.BlendMode.Zero));
                    Assert.That(material.GetInt("_ZWrite"), Is.EqualTo(1));
                    Assert.That(material.renderQueue, Is.EqualTo(material == art.Felt ? 1900 : 2000));
                }
            }
        }

        [Test]
        public void ActualRoundedRailShellRetainsBoundsWithoutDrawingItsOccludedInterior()
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                Assert.That(art.TableRail.bounds, Is.EqualTo(art.TableBox.bounds));
                Assert.That(art.TableRail.vertexCount, Is.EqualTo(104));
                Assert.That(art.TableRail.triangles.Length / 3, Is.EqualTo(96));
                Assert.That(art.TableRail.uv2.Length, Is.EqualTo(art.TableRail.vertexCount));
                Assert.That(art.TableRail.normals.All(normal => Mathf.Abs(normal.magnitude - 1) < 0.00001f), Is.True);
                var parent = new GameObject("Rail geometry contract");
                parent.layer = RendererLayers.Table;
                try
                {
                    var collider = parent.AddComponent<MeshCollider>();
                    collider.sharedMesh = art.TableRail;
                    Physics.SyncTransforms();
                    Assert.That(Physics.Raycast(new Ray(Vector3.up, Vector3.down), 2, 1 << RendererLayers.Table), Is.False);
                    Assert.That(Physics.Raycast(new Ray(new Vector3(0.45f, 1, 0), Vector3.down),
                        out RaycastHit hit, 2, 1 << RendererLayers.Table), Is.True);
                    Assert.That(hit.collider, Is.SameAs(collider));
                    Assert.That(0.83f * (0.43f + 0.065f), Is.LessThan(0.964f * 0.43f),
                        "The shared shell opening stays inside even the minimum rectangular felt's flat top");
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [TestCase(3)]
        [TestCase(4)]
        public void TableEffectsSelectTheirSharedCompiledPathWithoutChangingTextureOrOpaqueDepth(int effect)
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                var parent = new GameObject("Bounded table shader paths");
                try
                {
                    var view = new SurfaceView(parent.transform, "Table effect", art.TableBox, art);
                    view.Paint(Color.white, art.TableFabric, gloss: 0, effect: effect);
                    Assert.That(view.Renderer.sharedMaterial, Is.SameAs(effect == 3 ? art.Felt : art.Wood));
                    Assert.That(view.Renderer.sharedMaterial.GetInt("_ZWrite"), Is.EqualTo(1));
                    var block = new MaterialPropertyBlock();
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetTexture("_MainTex"), Is.SameAs(art.TableFabric));
                    Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0));
                    Assert.That(block.GetFloat("_Unlit"), Is.EqualTo(0));
                    view.Paint(Color.white);
                    Assert.That(view.Renderer.sharedMaterial, Is.SameAs(art.Surface));
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetFloat("_Effect"), Is.EqualTo(0));
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [Test]
        public void RepeatedPaintRebindsAtlasLabelsOpacityBaseAndLightingWithoutLeakingThePreviousStyle()
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                var parent = new GameObject("Paint identity contract");
                try
                {
                    var view = new SurfaceView(parent.transform, "Reused artwork", art.Quad, art);
                    Rect uv = ProceduralArt.FaceUv(33);
                    view.Paint(Color.white, art.Faces, uv, ProceduralArt.Ivory, gloss: 0.1f);
                    view.Paint(Color.white, art.Faces, uv, ProceduralArt.Ivory, gloss: 0.1f);
                    var block = new MaterialPropertyBlock();
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetTexture("_MainTex"), Is.SameAs(art.Faces));
                    Assert.That(block.GetVector("_MainTex_ST"), Is.EqualTo(new Vector4(uv.width, uv.height, uv.x, uv.y)));
                    view.Paint(Color.white, art.FacesWithoutLabels, uv, ProceduralArt.Ivory, gloss: 0.1f);
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetTexture("_MainTex"), Is.SameAs(art.FacesWithoutLabels));
                    Color translucent = ProceduralArt.Ivory;
                    translucent.a = 0.75f;
                    view.Paint(Color.white, art.Backs[1], paper: translucent, gloss: 0.1f);
                    Assert.That(view.Renderer.sharedMaterial, Is.SameAs(art.Decal));
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetTexture("_MainTex"), Is.SameAs(art.Backs[1]));
                    Assert.That(block.GetColor("_Paper"), Is.EqualTo(translucent));
                    Assert.That(block.GetVector("_MainTex_ST"), Is.EqualTo(new Vector4(1, 1, 0, 0)));
                    view.Paint(Color.white, baseColor: ProceduralArt.BackColor(1), gloss: 0);
                    Assert.That(view.Renderer.sharedMaterial, Is.SameAs(art.Surface));
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetColor("_Paper"), Is.EqualTo(Color.clear));
                    Assert.That(block.GetColor("_BaseColor"), Is.EqualTo(ProceduralArt.BackColor(1)));
                    Assert.That(block.GetFloat("_Band"), Is.EqualTo(1));
                    Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0));
                    view.Paint(Color.white, unlit: true);
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetFloat("_Band"), Is.EqualTo(0));
                    Assert.That(block.GetFloat("_Unlit"), Is.EqualTo(1));
                    Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0));
                    view.Paint(Color.white, gloss: 0.22f);
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetFloat("_Unlit"), Is.EqualTo(0));
                    Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0.22f));
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        public void SoftContactAndInteractionPaintSkipHighlightsOnTheSharedTransparentPath(int effect)
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                var parent = new GameObject("Shared effect material contract");
                try
                {
                    var view = new SurfaceView(parent.transform, "Soft effect", art.Quad, art);
                    view.Paint(new Color(0.1f, 0.3f, 0.3f, 0.9f), unlit: true, effect: effect);
                    Assert.That(view.Renderer.sharedMaterial, Is.SameAs(art.Decal));
                    var block = new MaterialPropertyBlock();
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetFloat("_Unlit"), Is.EqualTo(1));
                    Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0));
                    Assert.That(block.GetFloat("_Effect"), Is.EqualTo(effect));
                    Assert.That(view.Object.GetComponents<Collider>(), Is.Empty);
                    view.Paint(Color.white, unlit: true);
                    Assert.That(view.Renderer.sharedMaterial, Is.SameAs(art.Surface));
                    view.Renderer.GetPropertyBlock(block);
                    Assert.That(block.GetFloat("_Effect"), Is.EqualTo(0));
                    Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0));
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [Test]
        public void TemporaryTileBodyAndArtworkUseTheSameAlphaPathThenRestoreOpaqueConcealment()
        {
            RendererState state = SourceFixtures.State();
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                var parent = new GameObject("Temporary alpha and privacy contract");
                try
                {
                    LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                    EntityPlacement placement = plan.Entities["fixture-own-tile"];
                    placement.Entity.Display.Temporary = true;
                    var view = new EntityView(parent.transform, art);
                    view.Apply(placement, true);
                    MeshRenderer[] surfaces = view.Root.GetComponentsInChildren<MeshRenderer>();
                    Assert.That(surfaces.Length, Is.EqualTo(3));
                    var block = new MaterialPropertyBlock();
                    foreach (MeshRenderer surface in surfaces)
                    {
                        Assert.That(surface.sharedMaterial, Is.SameAs(art.Decal), surface.name);
                        surface.GetPropertyBlock(block);
                        float alpha = surface.name == "Original body" ?
                            block.GetColor("_Color").a : block.GetColor("_Paper").a;
                        Assert.That(alpha, Is.EqualTo(0.75f), surface.name);
                        Assert.That(block.GetFloat("_Gloss"), Is.EqualTo(0.10f), surface.name);
                        Assert.That(block.GetFloat("_Unlit"), Is.EqualTo(0), surface.name);
                    }
                    placement.Entity.Display.Temporary = false;
                    placement.Entity.Appearance.ShowFace = false;
                    view.Apply(placement, false);
                    foreach (MeshRenderer surface in surfaces)
                        Assert.That(surface.sharedMaterial, Is.SameAs(art.Surface), surface.name);
                    MeshRenderer concealed = surfaces.Single(surface => surface.name == "Concealed common back");
                    concealed.GetPropertyBlock(block);
                    Assert.That(block.GetTexture("_MainTex"), Is.SameAs(art.Backs[placement.Entity.Appearance.BackIndex]));
                    Assert.That(block.GetVector("_MainTex_ST"), Is.EqualTo(new Vector4(1, 1, 0, 0)));
                    Assert.That(block.GetColor("_Paper").a, Is.EqualTo(1));
                    Assert.That(view.VisibleFaceIndex, Is.Null);
                    Assert.That(view.VisibleFaceLabel, Is.Null);
                    Assert.That(view.Root.GetComponent<BoxCollider>().enabled, Is.True);
                    view.Clear();
                    Assert.That(view.Root.activeSelf, Is.False);
                    Assert.That(parent.GetComponentsInChildren<MeshRenderer>(), Is.Empty);
                }
                finally { EngineObjects.Release(parent); }
            }
        }

        [Test]
        public void PublicLabelGeometryIsConnectedRoundedVectorStrokesRatherThanPixelQuads()
        {
            Mesh label = BitmapGlyphs.Mesh("25000 EAST", 0.03f);
            try
            {
                Assert.That(label.vertexCount, Is.GreaterThan(0));
                Assert.That(label.bounds.size.z, Is.GreaterThan(0.03f));
                Assert.That(label.bounds.size.x, Is.LessThan(0.25f));
                Assert.That(label.normals.All(normal => normal == Vector3.up), Is.True);
                Vector3[] vertices = label.vertices;
                int[] triangles = label.triangles;
                for (int n = 0; n < triangles.Length; n += 3)
                    Assert.That(Vector3.Cross(vertices[triangles[n + 1]] - vertices[triangles[n]],
                        vertices[triangles[n + 2]] - vertices[triangles[n]]).y, Is.GreaterThanOrEqualTo(0));
                Assert.That(vertices.Select(vertex => vertex.z).Distinct().Count(), Is.GreaterThan(20),
                    "Rounded caps/curves require real sub-grid geometry, not the old 7-row bitmap mesh");
            }
            finally { EngineObjects.Release(label); }
        }

        [Test]
        public void HiddenPublicLabelsReuseRealVectorMeshesUntilTheirTextOrProjectedSizeChanges()
        {
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                var parent = new GameObject("Public label mesh lifetime");
                var label = new LabelView(parent.transform, art);
                try
                {
                    label.Apply("25000 EAST", 0.03f, Vector3.zero, Quaternion.identity,
                        RendererLayers.Table, Color.white);
                    MeshFilter filter = parent.GetComponentsInChildren<MeshFilter>().Single();
                    Mesh first = filter.sharedMesh;
                    int firstId = first.GetInstanceID();
                    Assert.That(first, Is.Not.SameAs(art.Quad));
                    for (int frame = 0; frame < 30; frame++)
                    {
                        label.Clear();
                        Assert.That(parent.GetComponentsInChildren<MeshRenderer>(), Is.Empty);
                        label.Apply("25000 EAST", 0.03f, Vector3.right * frame, Quaternion.identity,
                            RendererLayers.Table, frame % 2 == 0 ? Color.white : Color.yellow);
                        Assert.That(filter.sharedMesh, Is.SameAs(first),
                            "Atomic frame deactivation must not regenerate unchanged rounded-stroke geometry");
                    }
                    label.Clear();
                    label.Apply("26000 EAST", 0.03f, Vector3.zero, Quaternion.identity,
                        RendererLayers.Table, Color.white);
                    Assert.That(filter.sharedMesh.GetInstanceID(), Is.Not.EqualTo(firstId));
                    int changedId = filter.sharedMesh.GetInstanceID();
                    label.Clear();
                    label.Apply("26000 EAST", 0.04f, Vector3.zero, Quaternion.identity,
                        RendererLayers.Table, Color.white);
                    Assert.That(filter.sharedMesh.GetInstanceID(), Is.Not.EqualTo(changedId));
                    Assert.That(filter.sharedMesh.bounds.size.z, Is.GreaterThan(0.04f));
                    label.Clear();
                    Assert.That(parent.GetComponentsInChildren<MeshRenderer>(), Is.Empty);
                    Assert.That(parent.GetComponentsInChildren<Collider>(true), Is.Empty);
                }
                finally { label.Dispose(); EngineObjects.Release(parent); }
            }
        }

        [Test]
        public void MobilePublicLabelsGrowForLegibilityWithoutChangingLayoutBoundsOrDiceColliders()
        {
            RendererState state = SourceFixtures.State();
            var parent = new GameObject("Adaptive public label contract");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            RenderTexture target = null;
            try
            {
                var actions = new SemanticActions(state, _ => { });
                RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
                frame.FrameId++;
                frame.Presentation.ViewMode = "flat";
                frame.Turn.Phase = "awaitingDiscard";
                state.ApplyFrame(frame);
                Viewport narrow = SourceFixtures.Copy(state.Viewport, "Viewport");
                narrow.ResizeRevision++;
                narrow.WidthCssPx = 390;
                narrow.WidthPx = 780;
                state.SetViewport(narrow);
                LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                MeshFilter phase = parent.GetComponentsInChildren<MeshFilter>().Single(filter =>
                    filter.name == "Public vector label" && Mathf.Abs(filter.transform.localPosition.z - 0.075f) < 0.00001f);
                // A headless GameView clamps pixelRect to 480px without a viewport-sized target.
                target = new RenderTexture(Mathf.RoundToInt(plan.Viewport.BoardCss.width),
                    Mathf.RoundToInt(plan.Viewport.BoardCss.height), 24);
                scene.TableCamera.targetTexture = target;
                scene.TableCamera.pixelRect = new Rect(0, 0, plan.Viewport.BoardCss.width, plan.Viewport.BoardCss.height);
                Bounds text = phase.sharedMesh.bounds;
                Vector3 low = scene.TableCamera.WorldToScreenPoint(phase.transform.TransformPoint(new Vector3(0, 0, text.min.z)));
                Vector3 high = scene.TableCamera.WorldToScreenPoint(phase.transform.TransformPoint(new Vector3(0, 0, text.max.z)));
                Assert.That(Mathf.Abs(high.y - low.y), Is.GreaterThanOrEqualTo(7.5f),
                    "Real projected phase geometry must reach its CSS-pixel goal, not merely be nonempty");
                Assert.That(scene.ActivePlan.BoardBounds, Is.EqualTo(plan.BoardBounds));
                BoxCollider[] dice = parent.GetComponentsInChildren<BoxCollider>().Where(collider => collider.name == "Public die").ToArray();
                Assert.That(dice.Length, Is.EqualTo(2));
                foreach (BoxCollider collider in dice)
                {
                    Assert.That(collider.size, Is.EqualTo(Vector3.one));
                    Assert.That(collider.transform.localScale, Is.EqualTo(Vector3.one * 0.065f));
                }
                EntityView tile = scene.ActiveEntities["fixture-own-tile"];
                Transform face = tile.Root.transform.Find("Entitled face");
                Assert.That(face.localPosition.y - tile.Root.GetComponent<BoxCollider>().size.y / 2,
                    Is.EqualTo(0.0008f).Within(0.00001));
            }
            finally { scene.Dispose(); EngineObjects.Release(target); EngineObjects.Release(parent); }
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        [Test]
        public void FineInsetOutlineHasAnEmptyCenterAndOnlyFortyUpwardFacingTriangles()
        {
            Mesh outline;
            using (var art = new ProceduralArt(Shader.Find("Mahjong/OriginalSurface")))
            {
                outline = art.InsetOutline;
                Assert.That(outline.vertexCount, Is.EqualTo(40));
                Assert.That(outline.triangles.Length / 3, Is.EqualTo(40));
                Assert.That(outline.bounds.size, Is.EqualTo(new Vector3(1, 0, 1)));
                Vector3[] vertices = outline.vertices;
                int[] triangles = outline.triangles;
                Assert.That(vertices.All(vertex => Mathf.Max(Mathf.Abs(vertex.x), Mathf.Abs(vertex.z)) >= 0.475f), Is.True);
                Assert.That(outline.normals.All(normal => normal == Vector3.up), Is.True);
                for (int n = 0; n < triangles.Length; n += 3)
                    Assert.That(Vector3.Cross(vertices[triangles[n + 1]] - vertices[triangles[n]],
                        vertices[triangles[n + 2]] - vertices[triangles[n]]).y, Is.GreaterThan(0));
                var parent = new GameObject("Inset outline hole contract") { layer = RendererLayers.Table };
                try
                {
                    parent.AddComponent<MeshCollider>().sharedMesh = outline;
                    Physics.SyncTransforms();
                    Assert.That(Physics.Raycast(new Ray(Vector3.up, Vector3.down), 2, 1 << RendererLayers.Table), Is.False);
                    Assert.That(Physics.Raycast(new Ray(new Vector3(0.497f, 1, 0), Vector3.down),
                        2, 1 << RendererLayers.Table), Is.True);
                }
                finally { EngineObjects.Release(parent); }
            }
            Assert.That(outline == null, Is.True, "The extra shared mesh must be released with its owning art");
        }

        [TestCase("rectangular")]
        [TestCase("classic")]
        public void FinishedFeltZonesUseSharedOpaqueSeamsWithoutChangingRiverOrTableBounds(string boardLayout)
        {
            RendererState state = PolishedTableState("CHANGSHA", 390, 844);
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Presentation.BoardLayout = boardLayout;
            state.ApplyFrame(frame);
            var parent = new GameObject("Finished felt zone contract");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                scene.Commit(plan, state.Frame, new SemanticActions(state, _ => { }), state.Viewport.ResizeRevision);
                Bounds table = boardLayout == "classic" ?
                    new Bounds(Vector3.zero, new Vector3(1.74f, 0.06f, 1.74f)) : plan.BoardBounds;
                MeshRenderer felt = NamedSurface(parent, "Woven felt");
                Assert.That(felt.transform.localScale, Is.EqualTo(new Vector3(table.size.x, 0.06f, table.size.z)));
                Assert.That(felt.transform.localPosition, Is.EqualTo(new Vector3(table.center.x, -0.035f, table.center.z)));
                MeshRenderer seam = NamedSurface(parent, "Felt inset seam");
                Mesh shared = seam.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(seam.transform.localScale, Is.EqualTo(new Vector3(table.size.x - 0.024f, 1, table.size.z - 0.024f)));
                Assert.That(seam.transform.localPosition.y, Is.EqualTo(-0.0047f));
                Assert.That(seam.sharedMaterial.GetInt("_ZWrite"), Is.EqualTo(1));
                Assert.That(seam.GetComponents<Collider>(), Is.Empty);
                foreach (var river in plan.Rivers)
                {
                    MeshRenderer fill = NamedSurface(parent, "Discard river " + river.Key);
                    MeshRenderer edge = NamedSurface(parent, "Discard river seam " + river.Key);
                    Assert.That(fill.transform.localPosition, Is.EqualTo(river.Value.center));
                    Assert.That(fill.transform.localScale, Is.EqualTo(new Vector3(river.Value.size.x, 1, river.Value.size.z)));
                    Assert.That(edge.transform.localPosition, Is.EqualTo(river.Value.center + Vector3.up * 0.0002f));
                    Assert.That(edge.transform.localScale, Is.EqualTo(fill.transform.localScale));
                    Assert.That(edge.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(shared));
                    Assert.That(edge.sharedMaterial, Is.SameAs(seam.sharedMaterial));
                    var block = new MaterialPropertyBlock();
                    fill.GetPropertyBlock(block);
                    Color riverColor = block.GetColor("_Color");
                    Assert.That(riverColor.g, Is.EqualTo(0.36f));
                    Assert.That(riverColor.g / 0.39f, Is.InRange(0.9f, 1),
                        "Rivers should be quiet subdivisions of the felt, not the old dark rectangular blocks");
                    Assert.That(block.GetTexture("_MainTex"), Is.SameAs(Resources.Load<Texture2D>("Art/TableFabric")));
                    Assert.That(fill.sharedMaterial, Is.SameAs(felt.sharedMaterial));
                    Assert.That(fill.GetComponents<Collider>(), Is.Empty);
                }
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Count(renderer =>
                    renderer.name.StartsWith("Discard river seam ", StringComparison.Ordinal)), Is.EqualTo(plan.Rivers.Count));
                MeshRenderer handEdge = NamedSurface(parent, "Hand band fine edge");
                Assert.That(handEdge.gameObject.layer, Is.EqualTo(RendererLayers.Hand));
                Assert.That(handEdge.transform.localScale.z, Is.EqualTo(SourceCoordinates.Unit),
                    "The hand separator is one CSS pixel, not another reserved input band");
                Assert.That(parent.GetComponentsInChildren<Collider>().Length,
                    Is.EqualTo(plan.Entities.Count + plan.Slots.Count + 2), "Native decoration must never add raycast targets");
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [Test]
        public void SeatNameGeometryOmitsDuplicateIndicesAndUnknownScoresAndFitsLongPublicNames()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Seats[0].Nick = "  Seat 0  ";
            frame.Seats[1].Nick = "South";
            frame.Seats[1].Score = 25000;
            frame.Seats[2].Nick = "   ";
            frame.Seats[3].Nick = "ABCDEFGHIJKLMNOPQRST";
            state.ApplyFrame(frame);
            var parent = new GameObject("Clean seat typography contract");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                scene.Commit(plan, state.Frame, new SemanticActions(state, _ => { }), state.Viewport.ResizeRevision);
                string[] names = { "Seat 0", "South", "SEAT 2", "ABCDEFGHIJKLMNOP" };
                for (int seat = 0; seat < 4; seat++)
                {
                    MeshRenderer label = NamedSurface(parent, "Seat name " + seat);
                    AssertActualLabel(label, names[seat]);
                    int side = SourceCoordinates.RelativeSeat(seat, state.Frame.Session.ViewSeat);
                    float available = (side % 2 == 0 ? plan.BoardBounds.size.x : plan.BoardBounds.size.z) - 0.16f;
                    Bounds bounds = label.GetComponent<MeshFilter>().sharedMesh.bounds;
                    float half = Mathf.Abs(label.transform.localPosition.x);
                    if (side % 2 == 1) half = Mathf.Abs(label.transform.localPosition.z);
                    Assert.That(half + bounds.extents.x, Is.LessThanOrEqualTo(available / 2));
                }
                AssertActualLabel(NamedSurface(parent, "Seat score 1"), "25000");
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Count(renderer =>
                    renderer.name.StartsWith("Seat score ", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Count(renderer =>
                    renderer.name.StartsWith("Active seat mark ", StringComparison.Ordinal)), Is.EqualTo(1));
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void QuietTurnMarksFollowAuthoritativeSeatAndPovAndClearWithMissingTurn(int viewSeat)
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Session.ViewSeat = viewSeat;
            frame.Entities[0].Interaction.CanDiscard = false;
            frame.Turn.ActiveSeat = 2;
            frame.Turn.AwaitingDiscard = false;
            frame.Seats = frame.Seats.Reverse().ToArray();
            state.ApplyFrame(frame);
            var parent = new GameObject("Public turn mark contract");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                var actions = new SemanticActions(state, _ => { });
                LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                int side = SourceCoordinates.RelativeSeat(2, viewSeat);
                Quaternion rotation = Quaternion.AngleAxis(-90 * side, Vector3.up);
                MeshRenderer centerMark = NamedSurface(parent, "Active turn center mark");
                Assert.That(Quaternion.Angle(centerMark.transform.localRotation, rotation), Is.LessThan(0.001));
                Assert.That(Vector3.Dot(centerMark.transform.localPosition, rotation * Vector3.back), Is.GreaterThan(0));
                MeshRenderer seatMark = NamedSurface(parent, "Active seat mark 2");
                Assert.That(Quaternion.Angle(seatMark.transform.localRotation, rotation), Is.LessThan(0.001));
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Count(renderer =>
                    renderer.name.StartsWith("Active seat mark ", StringComparison.Ordinal)), Is.EqualTo(1));
                var block = new MaterialPropertyBlock();
                seatMark.GetPropertyBlock(block);
                Color accent = block.GetColor("_Color");
                centerMark.GetPropertyBlock(block);
                Assert.That(block.GetColor("_Color"), Is.EqualTo(accent));
                Assert.That(block.GetFloat("_Unlit"), Is.EqualTo(1));
                Assert.That(centerMark.GetComponents<Collider>(), Is.Empty);
                frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
                frame.FrameId++;
                frame.Turn = null;
                frame.Seats = new[] { frame.Seats.Single(seat => seat.Seat == 0) };
                state.ApplyFrame(frame);
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions,
                    state.Viewport.ResizeRevision);
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Any(renderer =>
                    renderer.name.StartsWith("Active seat mark ", StringComparison.Ordinal) ||
                    renderer.name == "Active turn center mark"), Is.False);
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Count(renderer =>
                    renderer.name.StartsWith("Seat name ", StringComparison.Ordinal)), Is.EqualTo(1),
                    "Absent public seats must not acquire invented players or scores");
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [TestCase(360, 800)]
        [TestCase(390, 844)]
        [TestCase(500, 900)]
        [TestCase(844, 390)]
        [TestCase(1280, 800)]
        public void FinishedTablePreservesAllFiveVariantsFullHandPhysicsAndPublicGeometry(int width, int height)
        {
            foreach (string gameType in new[] { "CHANGSHA", "FOUR_PLAYER", "THREE_PLAYER", "BAMBOO", "MINEFIELD" })
            {
                RendererState state = PolishedTableState(gameType, width, height);
                var parent = new GameObject("Finished " + gameType + " reachability");
                var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
                var target = new RenderTexture(width, height, 0);
                try
                {
                    scene.TableCamera.targetTexture = scene.HandCamera.targetTexture = target;
                    var actions = new SemanticActions(state, _ => { });
                    var picker = new CameraPicker(scene, state);
                    foreach (string mode in new[] { "flat", "perspective" })
                    {
                        RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
                        frame.FrameId++;
                        frame.Presentation.ViewMode = mode;
                        state.ApplyFrame(frame);
                        LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                        Bounds originalBounds = plan.BoardBounds;
                        scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                        CameraPose fit = CameraFit.Fit(originalBounds,
                            plan.Viewport.BoardCss.width / plan.Viewport.BoardCss.height, mode == "flat", 0, 0, plan.Compact);
                        Assert.That(scene.TableCamera.transform.position, Is.EqualTo(fit.Position));
                        Assert.That(Vector3.Distance(scene.TableCamera.transform.forward, fit.Rotation * Vector3.forward),
                            Is.LessThan(0.000001f));
                        Assert.That(Vector3.Distance(scene.TableCamera.transform.up, fit.Rotation * Vector3.up),
                            Is.LessThan(0.000001f));
                        Assert.That(scene.TableCamera.orthographicSize, Is.EqualTo(fit.OrthographicSize));
                        Assert.That(plan.BoardBounds, Is.EqualTo(originalBounds));
                        Assert.That(scene.ActiveEntities.Count, Is.EqualTo(frame.Entities.Length));
                        foreach (var item in plan.Entities)
                        {
                            EntityView view = scene.ActiveEntities[item.Key];
                            Assert.That(view.Root.transform.localPosition, Is.EqualTo(item.Value.Pose.Position), item.Key);
                            Assert.That(Quaternion.Angle(view.Root.transform.localRotation, item.Value.Pose.Rotation),
                                Is.LessThan(0.001), item.Key);
                            Assert.That(view.Root.transform.localScale, Is.EqualTo(Vector3.one * item.Value.Pose.Scale));
                            Assert.That(view.Root.GetComponent<BoxCollider>().size, Is.EqualTo(SourceCoordinates.MeshSize("TILE")));
                            if (!item.Value.Entity.Appearance.ShowFace)
                                Assert.That(view.VisibleFaceIndex, Is.Null, item.Key);
                        }
                        Assert.That(frame.LocalHand.EntityIds.Length, Is.EqualTo(14));
                        foreach (string id in frame.LocalHand.EntityIds)
                        {
                            EntityView hand = scene.ActiveEntities[id];
                            Camera camera = plan.Entities[id].Separate ? scene.HandCamera : scene.TableCamera;
                            Vector3 screen = camera.WorldToScreenPoint(hand.Root.transform.position);
                            Assert.That(camera.pixelRect.Contains(screen), Is.True, gameType + " " + mode + " " + id);
                            Assert.That(picker.Entity(screen, false).Id, Is.EqualTo(id), gameType + " " + mode + " " + id);
                            if (width < height && width < 768)
                            {
                                Bounds box = SourceCoordinates.OrientedBounds(plan.Entities[id].Pose, "TILE");
                                Assert.That(box.size.x / SourceCoordinates.Unit, Is.GreaterThanOrEqualTo(40));
                            }
                        }
                        if (gameType == "CHANGSHA" && (width < height && mode == "flat" || width >= 1280))
                            foreach (Entity entity in frame.Entities.Where(entity => entity.Group == "discard"))
                            {
                                Transform tile = scene.ActiveEntities[entity.EntityId].Root.transform;
                                Vector3 low = scene.TableCamera.WorldToScreenPoint(tile.TransformPoint(new Vector3(-0.03f, 0.02f, 0)));
                                Vector3 high = scene.TableCamera.WorldToScreenPoint(tile.TransformPoint(new Vector3(0.03f, 0.02f, 0)));
                                Assert.That(Vector2.Distance(low, high), Is.GreaterThanOrEqualTo(width < height ? 17 : 25),
                                    entity.SlotName + " retains its existing public short-edge CSS-pixel floor");
                            }
                        Assert.That(parent.GetComponentsInChildren<Collider>().Length,
                            Is.EqualTo(plan.Entities.Count + plan.Slots.Count + 2));
                    }
                }
                finally { scene.Dispose(); EngineObjects.Release(target); EngineObjects.Release(parent); }
            }
        }

        [Test]
        public void RepeatedFramesAndIdleVisualStepsReuseFinishedTableResourcesAndDisposeTheirOwnedMesh()
        {
            RendererState state = PolishedTableState("CHANGSHA", 390, 844);
            var parent = new GameObject("Finished table resource lifetime");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            Mesh outline = null;
            Material[] materials = null;
            try
            {
                var actions = new SemanticActions(state, _ => { });
                LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                outline = NamedSurface(parent, "Felt inset seam").GetComponent<MeshFilter>().sharedMesh;
                int[] meshes = parent.GetComponentsInChildren<MeshFilter>(true).Select(filter => filter.sharedMesh.GetInstanceID())
                    .Distinct().OrderBy(id => id).ToArray();
                materials = parent.GetComponentsInChildren<MeshRenderer>(true).Select(renderer => renderer.sharedMaterial).Distinct().ToArray();
                Assert.That(materials.Length, Is.EqualTo(4));
                for (int n = 0; n < 12; n++)
                {
                    scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                    for (int step = 0; step < 30; step++) scene.AdvanceVisual();
                    Assert.That(parent.GetComponentsInChildren<MeshFilter>(true).Select(filter => filter.sharedMesh.GetInstanceID())
                        .Distinct().OrderBy(id => id), Is.EqualTo(meshes), "Unchanged text and seams cannot regenerate geometry");
                    Assert.That(parent.GetComponentsInChildren<MeshRenderer>(true).Select(renderer => renderer.sharedMaterial).Distinct(),
                        Is.EquivalentTo(materials), "Decoration must reuse the existing four shared materials");
                }
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
            Assert.That(outline == null, Is.True);
            Assert.That(materials.All(material => material == null), Is.True);
        }

        private static MeshRenderer NamedSurface(GameObject parent, string name) =>
            parent.GetComponentsInChildren<MeshRenderer>().Single(renderer => renderer.name == name);

        private static void AssertActualLabel(MeshRenderer renderer, string text)
        {
            Mesh actual = renderer.GetComponent<MeshFilter>().sharedMesh;
            Mesh expected = BitmapGlyphs.Mesh(text, 1);
            try
            {
                Assert.That(actual.vertexCount, Is.EqualTo(expected.vertexCount), renderer.name + " actual text: " + text);
                Vector3[] a = actual.vertices, b = expected.vertices;
                float scale = actual.bounds.size.z / expected.bounds.size.z;
                for (int n = 0; n < a.Length; n++)
                    Assert.That(Vector3.Distance(a[n], b[n] * scale), Is.LessThan(0.000001f), renderer.name);
            }
            finally { EngineObjects.Release(expected); }
        }

        private static RendererState PolishedTableState(string gameType, int width, int height)
        {
            RendererState state = SourceFixtures.State(gameType != "CHANGSHA");
            long? viewerRevision = state.Frame.Session.ViewerRevision;
            RendererLayout layout = gameType == "CHANGSHA" ? SourceFixtures.CanonicalChangsha() : SourceFixtures.Lanes(gameType);
            if (gameType != "CHANGSHA")
            {
                Slot prototype = layout.Slots.First(slot => slot.Seat == 0 && slot.Group == "hand");
                var fullHand = Enumerable.Range(0, 14).Select(index =>
                {
                    Slot slot = SourceFixtures.Copy(prototype, "Slot");
                    slot.SlotName = "hand." + index + "@0";
                    slot.Indexes = new long[] { index };
                    foreach (Contract.Pose pose in slot.Poses) pose.Position.X = 49 + index * 6;
                    return slot;
                });
                layout.Slots = layout.Slots.Where(slot => slot.Seat != 0 || slot.Group != "hand").Concat(fullHand).ToArray();
            }
            layout.LayoutRevision = state.Layout.LayoutRevision + 1;
            state.ApplyLayout(layout);
            RendererFrame frame = SourceFixtures.Empty(layout);
            frame.FrameId++;
            frame.LayoutRevision = layout.LayoutRevision;
            frame.Session.ViewerRevision = viewerRevision;
            frame.Presentation.BoardLayout = "rectangular";
            frame.Presentation.HandLayout = width < 768 || height < 520 ? "separate" : "in-table";
            frame.Entities = layout.Slots.Where(slot => slot.Kind == "TILE" && layout.ActiveSeats.Contains(slot.Seat ?? 0) &&
                (slot.Group == "wall" || slot.Group == "discard" && slot.Indexes.Length == 2 && slot.Indexes[0] == 0 ||
                 slot.Group == "hand" && slot.Indexes.Length == 1 && slot.Indexes[0] < (slot.Seat == 0 ? 14 : 13)))
                .Select(slot =>
                {
                    Entity entity = SourceFixtures.Copy(SourceFixtures.Read<RendererFrame>("RendererFrame").Entities[0], "Entity");
                    entity.EntityId = entity.SlotName = slot.SlotName;
                    entity.Seat = slot.Seat;
                    entity.Group = slot.Group;
                    entity.Indexes = slot.Indexes;
                    entity.RotationIndex = slot.Group == "hand" && slot.Seat == 0 && slot.Poses.Length > 1 ? 1 : 0;
                    entity.Pose = slot.Poses[entity.RotationIndex];
                    bool face = slot.Group == "discard" || slot.Group == "hand" && slot.Seat == 0;
                    entity.Appearance.KnownFace = entity.Appearance.ShowFace = face;
                    entity.Appearance.FaceIndex = face ? 0 : (int?)null;
                    entity.Interaction = new Interaction { CanSelect = slot.Group == "hand" && slot.Seat == 0 };
                    return entity;
                }).ToArray();
            frame.LocalHand.EntityIds = frame.Entities.Where(entity => entity.Seat == 0 && entity.Group == "hand")
                .Select(entity => entity.EntityId).ToArray();
            frame.LocalHand.DrawnEntityId = frame.LocalHand.EntityIds.Last();
            state.ApplyFrame(frame);
            Viewport viewport = SourceFixtures.Copy(state.Viewport, "Viewport");
            viewport.ResizeRevision++;
            viewport.WidthCssPx = width;
            viewport.HeightCssPx = height;
            viewport.WidthPx = width * 2;
            viewport.HeightPx = height * 2;
            viewport.InsetsCssPx = new Insets { Top = 110, Bottom = 51 };
            viewport.HandBandCssPx = frame.Presentation.HandLayout == "separate" ?
                Mathf.Min(180, (height - 161) * 0.3f, Mathf.Max(148, width * 0.24f)) : 0;
            state.SetViewport(viewport);
            return state;
        }

        private sealed class Png
        {
            public int Width;
            public int Height;
            public byte[] Pixels;
        }

        private static double AlphaMean(Png png, int x, int y, int side)
        {
            long sum = 0;
            for (int py = y; py < Mathf.Min(png.Height, y + side); py++)
                for (int px = x; px < Mathf.Min(png.Width, x + side); px++)
                    sum += png.Pixels[(py * png.Width + px) * 4 + 3];
            return sum / (double)(side * side * 255);
        }

        private static Png ReadPng(string relative)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), relative));
            Assert.That(bytes.Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), relative);
            var png = new Png();
            using (var compressed = new MemoryStream())
            {
                for (int offset = 8; offset < bytes.Length;)
                {
                    int count = BigEndian(bytes, offset);
                    string type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
                    if (type == "IHDR")
                    {
                        png.Width = BigEndian(bytes, offset + 8);
                        png.Height = BigEndian(bytes, offset + 12);
                        Assert.That(bytes[offset + 16], Is.EqualTo(8), relative);
                        Assert.That(bytes[offset + 17], Is.EqualTo(6), relative);
                        Assert.That(bytes[offset + 20], Is.EqualTo(0), relative);
                    }
                    if (type == "IDAT") compressed.Write(bytes, offset + 8, count);
                    offset += count + 12;
                }
                byte[] packed = compressed.ToArray();
                using (var payload = new MemoryStream(packed, 2, packed.Length - 6))
                using (var deflate = new DeflateStream(payload, CompressionMode.Decompress))
                using (var decoded = new MemoryStream())
                {
                    deflate.CopyTo(decoded);
                    byte[] raw = decoded.ToArray();
                    int stride = png.Width * 4;
                    png.Pixels = new byte[stride * png.Height];
                    for (int y = 0; y < png.Height; y++)
                    {
                        int filter = raw[y * (stride + 1)];
                        for (int x = 0; x < stride; x++)
                        {
                            int at = y * stride + x;
                            int a = x >= 4 ? png.Pixels[at - 4] : 0;
                            int b = y > 0 ? png.Pixels[at - stride] : 0;
                            int c = x >= 4 && y > 0 ? png.Pixels[at - stride - 4] : 0;
                            int prediction = filter == 0 ? 0 : filter == 1 ? a : filter == 2 ? b :
                                filter == 3 ? (a + b) / 2 : Paeth(a, b, c);
                            png.Pixels[at] = (byte)((raw[y * (stride + 1) + x + 1] + prediction) & 255);
                        }
                    }
                }
            }
            return png;
        }

        private static int BigEndian(byte[] bytes, int at) =>
            bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3];

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        [Test]
        public void ConcealedKnownOwnedFaceHasNoFaceTextureLabelAndClearBlanksLookup()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame next = SourceFixtures.Copy(state.Frame, "RendererFrame");
            next.FrameId++;
            next.Entities[0].Appearance.ShowFace = false;
            state.ApplyFrame(next);
            var parent = new GameObject("Renderer test root");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                var actions = new SemanticActions(state, _ => { });
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions, state.Viewport.ResizeRevision);
                EntityView owned = scene.ActiveEntities["fixture-own-tile"];
                EntityView concealed = scene.ActiveEntities["fixture-concealed-wall"];
                Assert.That(owned.VisibleFaceIndex, Is.Null);
                Assert.That(owned.VisibleFaceLabel, Is.Null);
                Assert.That(concealed.VisibleFaceIndex, Is.Null);
                Assert.That(owned.Root.layer, Is.EqualTo(RendererLayers.Hand));
                Assert.That(concealed.Root.layer, Is.EqualTo(RendererLayers.Table));
                scene.Clear();
                Assert.That(scene.ActiveEntities, Is.Empty);
                Assert.That(owned.Target.Id, Is.Null);
                Assert.That(owned.Root.activeSelf, Is.False);
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [TestCase("flat")]
        [TestCase("perspective")]
        public void ActualPhysicsPickingUsesSameDisplayedEntityAndRejectsStaleResize(string mode)
        {
            RendererState state = SourceFixtures.State();
            RendererFrame next = SourceFixtures.Copy(state.Frame, "RendererFrame");
            next.FrameId++;
            next.Presentation.ViewMode = mode;
            state.ApplyFrame(next);
            var parent = new GameObject("Renderer raycast test");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                var actions = new SemanticActions(state, _ => { });
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions, state.Viewport.ResizeRevision);
                scene.TableCamera.pixelRect = new Rect(0, 150, 800, 600);
                scene.HandCamera.pixelRect = new Rect(0, 0, 800, 140);
                Physics.SyncTransforms();
                EntityView hand = scene.ActiveEntities["fixture-own-tile"];
                Vector3 screen = scene.HandCamera.WorldToScreenPoint(hand.Root.transform.position);
                var picker = new CameraPicker(scene, state);
                Pick hit = picker.Entity(screen, false);
                Assert.That(hit.Found, Is.True);
                Assert.That(hit.Id, Is.EqualTo(hand.Target.Id));
                Assert.That(hit.Kind, Is.EqualTo(PickKind.Entity));
                Viewport resized = SourceFixtures.Copy(state.Viewport, "Viewport");
                resized.ResizeRevision++;
                state.SetViewport(resized);
                Assert.That(picker.Entity(screen, false).Found, Is.False);
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [TestCase("flat")]
        [TestCase("perspective")]
        public void CameraOverridesMoveOnlyTheTableCameraPreservePrivacyAndRejectPickingUntilRendered(string mode)
        {
            RendererState state = SourceFixtures.State();
            RendererFrame initial = SourceFixtures.Copy(state.Frame, "RendererFrame");
            initial.FrameId++;
            initial.Presentation.ViewMode = mode;
            state.ApplyFrame(initial);
            var parent = new GameObject("Three-axis camera picking");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                var actions = new SemanticActions(state, _ => { });
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions,
                    state.Viewport.ResizeRevision);
                var picker = new CameraPicker(scene, state);
                Vector3 handPosition = scene.HandCamera.transform.position;
                Quaternion handRotation = scene.HandCamera.transform.rotation;
                float handSize = scene.HandCamera.orthographicSize;
                Vector3 handScreen = scene.HandCamera.WorldToScreenPoint(scene.ActiveEntities["fixture-own-tile"].Root.transform.position);
                var positions = scene.ActiveEntities.ToDictionary(pair => pair.Key, pair => pair.Value.Root.transform.position);
                var views = new List<CameraView>
                {
                    new CameraView { Scale = 1.1 },
                    new CameraView { Yaw = 60 },
                    new CameraView { Roll = 25 },
                };
                if (mode == "perspective") views.Add(new CameraView { Pitch = 45 });
                foreach (CameraView view in views)
                {
                    Vector3 oldPosition = scene.TableCamera.transform.position;
                    Quaternion oldRotation = scene.TableCamera.transform.rotation;
                    float oldSize = scene.TableCamera.orthographicSize;
                    RendererFrame next = SourceFixtures.Copy(state.Frame, "RendererFrame");
                    next.FrameId++;
                    next.Presentation.Camera = view;
                    state.ApplyFrame(next);
                    Assert.That(picker.At(handScreen), Is.Null, "Input cannot use a camera override before it is displayed");
                    scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions,
                        state.Viewport.ResizeRevision);
                    Assert.That(scene.TableCamera.transform.position != oldPosition ||
                        scene.TableCamera.transform.rotation != oldRotation || scene.TableCamera.orthographicSize != oldSize, Is.True);
                    Assert.That(scene.HandCamera.transform.position, Is.EqualTo(handPosition));
                    Assert.That(scene.HandCamera.transform.rotation, Is.EqualTo(handRotation));
                    Assert.That(scene.HandCamera.orthographicSize, Is.EqualTo(handSize));
                    Assert.That(scene.HandCamera.WorldToScreenPoint(scene.ActiveEntities["fixture-own-tile"].Root.transform.position),
                        Is.EqualTo(handScreen));
                    Assert.That(picker.Entity(handScreen, false).Id, Is.EqualTo("fixture-own-tile"));
                    foreach (var pair in scene.ActiveEntities)
                        Assert.That(pair.Value.Root.transform.position, Is.EqualTo(positions[pair.Key]),
                            "Camera controls must not move source tiles or relayout walls");
                    EntityView wall = scene.ActiveEntities["fixture-concealed-wall"];
                    Vector3 wallScreen = scene.TableCamera.WorldToScreenPoint(wall.Root.transform.position);
                    Assert.That(picker.Entity(wallScreen, false).Id, Is.EqualTo("fixture-concealed-wall"));
                    Assert.That(wall.VisibleFaceIndex, Is.Null);
                    Assert.That(wall.VisibleFaceLabel, Is.Null);
                }
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }

        [Test]
        public void ReplacementClearsRetiredInactiveBufferHandlesAndFaceLabels()
        {
            RendererState state = SourceFixtures.State();
            var parent = new GameObject("Replacement test");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                var actions = new SemanticActions(state, _ => { });
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions, state.Viewport.ResizeRevision);
                EntityView previous = scene.ActiveEntities["fixture-own-tile"];
                RendererFrame empty = SourceFixtures.Copy(state.Frame, "RendererFrame");
                empty.FrameId++;
                empty.Entities = System.Array.Empty<Entity>();
                empty.LocalHand.EntityIds = System.Array.Empty<string>();
                state.ApplyFrame(empty);
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions, state.Viewport.ResizeRevision);
                Assert.That(scene.ActiveEntities, Is.Empty);
                Assert.That(previous.Target.Id, Is.Null);
                Assert.That(previous.VisibleFaceLabel, Is.Null);
                Assert.That(previous.VisibleFaceIndex, Is.Null);
            }
            finally { scene.Dispose(); EngineObjects.Release(parent); }
        }
    }
}
