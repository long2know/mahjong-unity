using System;
using Newtonsoft.Json;

namespace Mahjong.Renderer.Contract
{
    public static class ContractVersion
    {
        public const string Current = "1.0.0";
        public const string Editor = "6000.3.24f1";
        public const string EditorRevision = "4e7b9b5b6244";
        public const string Baseline = "d10d351e543aa3e341960de9800828b6c0662a82";
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public class Envelope
    {
        [JsonProperty("contractVersion")] public string Version = ContractVersion.Current;
        [JsonProperty("instanceId")] public string InstanceId;
        [JsonProperty("type")] public string Type;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public class SessionEnvelope : Envelope
    {
        [JsonProperty("sessionId")] public string SessionId;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Vector3Dto
    {
        [JsonProperty("x")] public double X;
        [JsonProperty("y")] public double Y;
        [JsonProperty("z")] public double Z;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class QuaternionDto
    {
        [JsonProperty("x")] public double X;
        [JsonProperty("y")] public double Y;
        [JsonProperty("z")] public double Z;
        [JsonProperty("w")] public double W;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Pose
    {
        [JsonProperty("position")] public Vector3Dto Position;
        [JsonProperty("rotation")] public QuaternionDto Rotation;
        [JsonProperty("size")] public Vector3Dto Size;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Conditions
    {
        [JsonProperty("gameType")] public string GameType;
        [JsonProperty("back")] public int Back;
        [JsonProperty("fives")] public string Fives;
        [JsonProperty("points")] public string Points;
        [JsonProperty("dealType")] public string DealType;
        [JsonProperty("baseUnit")] public int BaseUnit;
        [JsonProperty("dealMode")] public string DealMode;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Match
    {
        [JsonProperty("dealer")] public int Dealer;
        [JsonProperty("honba")] public long Honba;
        [JsonProperty("conditions")] public Conditions Conditions;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Session
    {
        [JsonProperty("gameId")] public string GameId;
        [JsonProperty("mode")] public string Mode;
        [JsonProperty("connected")] public bool Connected;
        [JsonProperty("snapshotReady")] public bool SnapshotReady;
        [JsonProperty("actorSeat")] public int? ActorSeat;
        [JsonProperty("viewSeat")] public int? ViewSeat;
        [JsonProperty("viewerRevision")] public long? ViewerRevision;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Presentation
    {
        [JsonProperty("viewMode")] public string ViewMode;
        [JsonProperty("boardLayout")] public string BoardLayout;
        [JsonProperty("handLayout")] public string HandLayout;
        [JsonProperty("tileLabels")] public bool TileLabels;
        [JsonProperty("lookDown")] public double LookDown;
        [JsonProperty("zoom")] public double Zoom;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Display
    {
        [JsonProperty("selected")] public bool Selected;
        [JsonProperty("hovered")] public bool Hovered;
        [JsonProperty("held")] public bool Held;
        [JsonProperty("temporary")] public bool Temporary;
        [JsonProperty("bottom")] public bool Bottom;
        [JsonProperty("drawn")] public bool Drawn;
        [JsonProperty("highlight")] public double Highlight;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Interaction
    {
        [JsonProperty("canSelect")] public bool CanSelect;
        [JsonProperty("canDrag")] public bool CanDrag;
        [JsonProperty("canFlip")] public bool CanFlip;
        [JsonProperty("canDiscard")] public bool CanDiscard;
        [JsonProperty("canPickup")] public bool CanPickup;
        public bool Any => CanSelect || CanDrag || CanFlip || CanDiscard || CanPickup;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Appearance
    {
        [JsonProperty("faceIndex")] public int? FaceIndex;
        [JsonProperty("knownFace")] public bool KnownFace;
        [JsonProperty("showFace")] public bool ShowFace;
        [JsonProperty("backIndex")] public int BackIndex;
        [JsonProperty("valueIndex")] public int? ValueIndex;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Entity
    {
        [JsonProperty("entityId")] public string EntityId;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("slotName")] public string SlotName;
        [JsonProperty("group")] public string Group;
        [JsonProperty("seat")] public int? Seat;
        [JsonProperty("indexes")] public long[] Indexes;
        [JsonProperty("rotationIndex")] public int RotationIndex;
        [JsonProperty("pose")] public Pose Pose;
        [JsonProperty("scale")] public double Scale;
        [JsonProperty("appearance")] public Appearance Appearance;
        [JsonProperty("display")] public Display Display;
        [JsonProperty("interaction")] public Interaction Interaction;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Slot
    {
        [JsonProperty("slotName")] public string SlotName;
        [JsonProperty("group")] public string Group;
        [JsonProperty("seat")] public int? Seat;
        [JsonProperty("indexes")] public long[] Indexes;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("poses")] public Pose[] Poses;
        [JsonProperty("drawShadow")] public bool DrawShadow;
        [JsonProperty("shadowRotationIndex")] public int ShadowRotationIndex;
        [JsonProperty("canFlipMultiple")] public bool CanFlipMultiple;
        [JsonProperty("rotateHeld")] public bool RotateHeld;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class SeatDisplay
    {
        [JsonProperty("seat")] public int Seat;
        [JsonProperty("playerId")] public string PlayerId;
        [JsonProperty("nick")] public string Nick;
        [JsonProperty("score")] public double? Score;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class LocalHand
    {
        [JsonProperty("seat")] public int? Seat;
        [JsonProperty("entityIds")] public string[] EntityIds;
        [JsonProperty("drawnEntityId")] public string DrawnEntityId;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Turn
    {
        [JsonProperty("activeSeat")] public int? ActiveSeat;
        [JsonProperty("phase")] public string Phase;
        [JsonProperty("awaitingDiscard")] public bool? AwaitingDiscard;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class BreakPoint
    {
        [JsonProperty("wallIndex")] public int WallIndex;
        [JsonProperty("stackIndex")] public long StackIndex;
        [JsonProperty("tileIndex")] public int TileIndex;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class PickupDisplay
    {
        [JsonProperty("phase")] public string Phase;
        [JsonProperty("seatIndex")] public int SeatIndex;
        [JsonProperty("count")] public int Count;
        [JsonProperty("dealMode")] public string DealMode;
        [JsonProperty("breakPoint")] public BreakPoint BreakPoint;
        [JsonProperty("wallIndex")] public long? WallIndex;
        [JsonProperty("triggerSlotName")] public string TriggerSlotName;
        [JsonProperty("batchPreviewSlotNames")] public string[] BatchPreviewSlotNames;
        [JsonProperty("canTake")] public bool CanTake;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class DiceDisplay
    {
        [JsonProperty("values")] public int[] Values;
        [JsonProperty("state")] public string State;
        [JsonProperty("canRoll")] public bool CanRoll;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Configure : Envelope
    {
        [JsonProperty("buildId")] public string BuildId;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Clear : SessionEnvelope
    {
        [JsonProperty("reason")] public string Reason;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Playback : Envelope
    {
        [JsonProperty("suspended")] public bool Suspended;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class CanonicalBoard
    {
        [JsonProperty("width")] public int Width;
        [JsonProperty("depth")] public int Depth;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class RendererLayout : SessionEnvelope
    {
        [JsonProperty("layoutRevision")] public long LayoutRevision;
        [JsonProperty("gameType")] public string GameType;
        [JsonProperty("activeSeats")] public int[] ActiveSeats;
        [JsonProperty("canonicalBoard")] public CanonicalBoard CanonicalBoard;
        [JsonProperty("slots")] public Slot[] Slots;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class RendererFrame : SessionEnvelope
    {
        [JsonProperty("frameId")] public long FrameId;
        [JsonProperty("layoutRevision")] public long LayoutRevision;
        [JsonProperty("session")] public Session Session;
        [JsonProperty("presentation")] public Presentation Presentation;
        [JsonProperty("match")] public Match Match;
        [JsonProperty("entities")] public Entity[] Entities;
        [JsonProperty("seats")] public SeatDisplay[] Seats;
        [JsonProperty("localHand")] public LocalHand LocalHand;
        [JsonProperty("turn")] public Turn Turn;
        [JsonProperty("pickup")] public PickupDisplay Pickup;
        [JsonProperty("dice")] public DiceDisplay Dice;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Insets
    {
        [JsonProperty("top")] public double Top;
        [JsonProperty("right")] public double Right;
        [JsonProperty("bottom")] public double Bottom;
        [JsonProperty("left")] public double Left;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Viewport : Envelope
    {
        [JsonProperty("resizeRevision")] public long ResizeRevision;
        [JsonProperty("widthCssPx")] public double WidthCssPx;
        [JsonProperty("heightCssPx")] public double HeightCssPx;
        [JsonProperty("devicePixelRatio")] public double DevicePixelRatio;
        [JsonProperty("renderPixelRatio")] public double RenderPixelRatio;
        [JsonProperty("widthPx")] public long WidthPx;
        [JsonProperty("heightPx")] public long HeightPx;
        [JsonProperty("insetsCssPx")] public Insets InsetsCssPx;
        [JsonProperty("handBandCssPx")] public double HandBandCssPx;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class RendererAction : SessionEnvelope
    {
        [JsonProperty("actionId")] public string ActionId;
        [JsonProperty("contextFrameId")] public long ContextFrameId;
        [JsonProperty("intent")] public Newtonsoft.Json.Linq.JObject Intent;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class ActionResult : SessionEnvelope
    {
        [JsonProperty("actionId")] public string ActionId;
        [JsonProperty("status")] public string Status;
        [JsonProperty("reason")] public string Reason;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class Ready : Envelope
    {
        [JsonProperty("buildId")] public string BuildId;
        [JsonProperty("unityVersion")] public string UnityVersion = ContractVersion.Editor;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class FrameRendered : SessionEnvelope
    {
        [JsonProperty("frameId")] public long FrameId;
        [JsonProperty("layoutRevision")] public long LayoutRevision;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class RendererError : SessionEnvelope
    {
        [JsonProperty("code")] public string Code;
        [JsonProperty("message")] public string Message;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class ArtifactFile
    {
        [JsonProperty("path")] public string Path;
        [JsonProperty("bytes")] public long Bytes;
        [JsonProperty("sha256")] public string Sha256;
    }

    [Serializable, JsonObject(MemberSerialization.OptIn)]
    public sealed class BuildManifest
    {
        [JsonProperty("artifactKind")] public string ArtifactKind = "unity-webgl";
        [JsonProperty("contractVersion")] public string Version = ContractVersion.Current;
        [JsonProperty("buildId")] public string BuildId;
        [JsonProperty("unityVersion")] public string UnityVersion = ContractVersion.Editor;
        [JsonProperty("unityRevision")] public string UnityRevision = ContractVersion.EditorRevision;
        [JsonProperty("compression")] public string Compression = "none";
        [JsonProperty("threading")] public string Threading = "single-threaded";
        [JsonProperty("developmentBuild")] public bool DevelopmentBuild;
        [JsonProperty("baselineRevision")] public string BaselineRevision = ContractVersion.Baseline;
        [JsonProperty("rendererSourceRevision")] public string RendererSourceRevision;
        [JsonProperty("rendererSourceTreeSha256")] public string RendererSourceTreeSha256;
        [JsonProperty("sourceDirty")] public bool SourceDirty;
        [JsonProperty("builtAtUtc")] public string BuiltAtUtc;
        [JsonProperty("loader")] public ArtifactFile Loader;
        [JsonProperty("framework")] public ArtifactFile Framework;
        [JsonProperty("data")] public ArtifactFile Data;
        [JsonProperty("wasm")] public ArtifactFile Wasm;
        [JsonProperty("streamingAssetsPath")] public string StreamingAssetsPath;
    }
}
