using System.Linq;
using Mahjong.Renderer.Art;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Mahjong.Renderer.Layout;
using Mahjong.Renderer.Rendering;
using NUnit.Framework;
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
        public void OriginalLabelsDistinguishAllThirtySevenFacesIncludingWhiteDragon()
        {
            Assert.That(Enumerable.Range(0, 37).Select(ProceduralArt.FaceLabel).Distinct().Count(), Is.EqualTo(37));
            Assert.That(ProceduralArt.FaceLabel(29), Is.EqualTo("W"));
            Assert.That(ProceduralArt.FaceLabel(33), Is.EqualTo("WD"));
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
