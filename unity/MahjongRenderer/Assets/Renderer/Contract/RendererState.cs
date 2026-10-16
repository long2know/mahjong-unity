using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Mahjong.Renderer.Contract
{
    public sealed class RendererState
    {
        private readonly string expectedBuildId;
        private readonly SchemaValidator schema;
        private readonly HashSet<string> sessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> pendingActions = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> recentActions = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> recentActionOrder = new Queue<string>();
        private long actionCounter;
        private long lastFrameId;
        private Session binding;
        private long? viewerRevision;

        public string InstanceId { get; private set; }
        public string SessionId { get; private set; }
        public bool Disposed { get; private set; }
        public bool Suspended { get; private set; }
        public bool InputRevoked { get; private set; }
        public RendererLayout Layout { get; private set; }
        public RendererFrame Frame { get; private set; }
        public Viewport Viewport { get; private set; }
        public IReadOnlyDictionary<string, Slot> Slots { get; private set; } =
            new Dictionary<string, Slot>();
        public IReadOnlyDictionary<string, Entity> Entities { get; private set; } =
            new Dictionary<string, Entity>();
        public bool Actionable => !Disposed && !Suspended && !InputRevoked &&
                                  Frame != null && Coherence.CanInteract(Frame);
        public event Action Invalidated;
        public event Action<ActionResult> ActionResolved;

        public RendererState(SchemaValidator schema, string expectedBuildId)
        {
            this.schema = schema;
            this.expectedBuildId = expectedBuildId;
        }

        public Ready Configure(Configure message)
        {
            if (InstanceId != null || message.BuildId != expectedBuildId)
                throw Invalid("Configure is single-use and must match the actual embedded build identity.");
            InstanceId = message.InstanceId;
            return new Ready { Type = "ready", InstanceId = InstanceId, BuildId = expectedBuildId };
        }

        public void Clear(Clear message)
        {
            RequireInstance(message);
            if (sessions.Contains(message.SessionId))
                throw Invalid("Clear must bind a fresh session epoch, never a retired/current epoch.");
            sessions.Add(message.SessionId);
            SessionId = message.SessionId;
            Frame = null;
            Layout = null;
            lastFrameId = 0;
            binding = null;
            viewerRevision = null;
            Slots = new Dictionary<string, Slot>();
            Entities = new Dictionary<string, Entity>();
            pendingActions.Clear();
            recentActions.Clear();
            recentActionOrder.Clear();
            InputRevoked = false;
            Disposed = message.Reason == "dispose";
            Invalidated?.Invoke();
        }

        public void ApplyLayout(RendererLayout message)
        {
            RequireSession(message);
            if (Layout != null && message.LayoutRevision <= Layout.LayoutRevision)
                throw Invalid("Layout revision is stale.");
            Dictionary<string, Slot> slots = Coherence.Layout(message);
            Layout = message;
            Slots = slots;
            Frame = null;
            Entities = new Dictionary<string, Entity>();
            InputRevoked = false;
            Invalidated?.Invoke();
        }

        public void ApplyFrame(RendererFrame message)
        {
            RequireSession(message);
            if (Layout == null || message.LayoutRevision != Layout.LayoutRevision)
                throw new ContractException("missing-layout", "A matching complete slot catalog is required.");
            if (message.FrameId <= lastFrameId)
                throw new ContractException("stale-frame", "Frame identifier is not newer than the accepted frame.");
            if (binding != null &&
                (binding.GameId != message.Session.GameId || binding.ActorSeat != message.Session.ActorSeat ||
                 binding.Mode != message.Session.Mode))
                throw Invalid("Room, actor or protocol change requires Clear with a new session epoch.");
            if (viewerRevision.HasValue &&
                (!message.Session.ViewerRevision.HasValue || message.Session.ViewerRevision.Value < viewerRevision.Value))
                throw Invalid("Viewer revision moved backwards within the session.");
            Dictionary<string, Entity> entities = Coherence.Frame(message, Layout, Slots);
            bool transformsChanged = Frame != null &&
                (Frame.Session.ViewSeat != message.Session.ViewSeat ||
                 Frame.Presentation.BoardLayout != message.Presentation.BoardLayout ||
                 Frame.Presentation.HandLayout != message.Presentation.HandLayout ||
                 Frame.Presentation.ViewMode != message.Presentation.ViewMode ||
                 Frame.Presentation.Zoom != message.Presentation.Zoom ||
                 Frame.Presentation.LookDown != message.Presentation.LookDown ||
                 !CameraView.Same(Frame.Presentation.Camera, message.Presentation.Camera) ||
                 Frame.LocalHand.Seat != message.LocalHand.Seat ||
                 Frame.LocalHand.DrawnEntityId != message.LocalHand.DrawnEntityId ||
                 !Frame.LocalHand.EntityIds.SequenceEqual(message.LocalHand.EntityIds));
            Frame = message;
            lastFrameId = message.FrameId;
            Entities = entities;
            binding = message.Session;
            viewerRevision = message.Session.ViewerRevision;
            InputRevoked = false;
            if (transformsChanged || !Actionable) Invalidated?.Invoke();
        }

        public void SetViewport(Viewport message)
        {
            RequireInstance(message);
            if (Viewport != null && message.ResizeRevision <= Viewport.ResizeRevision)
                throw Invalid("Resize revision is stale.");
            Coherence.Viewport(message);
            Invalidated?.Invoke();
            Viewport = message;
        }

        public void SetPlayback(Playback message)
        {
            RequireInstance(message);
            if (message.Suspended) Invalidated?.Invoke();
            Suspended = message.Suspended;
        }

        public void CancelInput(CancelInput message)
        {
            RequireInstance(message);
            Invalidated?.Invoke();
        }

        public void ReceiveActionResult(ActionResult message)
        {
            RequireSession(message);
            if (!pendingActions.Remove(message.ActionId))
                throw Invalid("Action result is stale, duplicated or was never emitted.");
            recentActions.Remove(message.ActionId);
            ActionResolved?.Invoke(message);
        }

        public RendererAction MakeAction(JObject intent)
        {
            if (!Actionable) throw Invalid("Input is revoked or the current source frame is not actionable.");
            string id = InstanceId + ":" + (++actionCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (id.Length > 128) id = actionCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var action = new RendererAction
            {
                Type = "action", InstanceId = InstanceId, SessionId = SessionId,
                ActionId = id, ContextFrameId = Frame.FrameId, Intent = intent
            };
            schema.Validate(JToken.FromObject(action), "RendererAction");
            pendingActions.Add(id);
            recentActions.Add(id);
            recentActionOrder.Enqueue(id);
            while (recentActionOrder.Count > 256)
            {
                string old = recentActionOrder.Dequeue();
                if (recentActions.Remove(old)) pendingActions.Remove(old);
            }
            return action;
        }

        public void RevokeInput()
        {
            InputRevoked = true;
            Invalidated?.Invoke();
        }

        private void RequireInstance(Envelope message)
        {
            if (InstanceId == null || message.InstanceId != InstanceId || Disposed)
                throw Invalid("Message is for an unconfigured, retired or different renderer instance.");
        }

        private void RequireSession(SessionEnvelope message)
        {
            RequireInstance(message);
            if (SessionId == null || message.SessionId != SessionId)
                throw Invalid("Message is for a retired or unbound session epoch.");
        }

        private static ContractException Invalid(string text) => new ContractException("invalid-message", text);
    }
}
