using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Renderer.Contract;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Mahjong.Renderer.Tests
{
    internal static class SourceFixtures
    {
        public static SchemaValidator Schema => SchemaValidator.Load();

        public static JObject Json(string definition)
        {
            var fixture = (JArray)StrictJson.Parse(Resources.Load<TextAsset>("renderer-fixtures").text);
            return (JObject)fixture.Single(value => value["definition"].Value<string>() == definition)["value"].DeepClone();
        }

        public static T Read<T>(string definition) => Schema.Read<T>(Json(definition).ToString(), definition);
        public static T Copy<T>(T value, string definition) => Schema.Read<T>(Schema.Write(value, definition), definition);

        public static RendererLayout CanonicalChangsha() => Schema.Read<RendererLayout>(
            UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Tests/Editor/ChangshaLayout.json").text,
            "RendererLayout");

        public static RendererState State(bool relay = false)
        {
            var state = new RendererState(Schema, "fixture-build");
            state.Configure(Schema.Read<Configure>("{\"contractVersion\":\"1.0.0\",\"instanceId\":\"fixture-instance\",\"type\":\"configure\",\"buildId\":\"fixture-build\"}", "Configure"));
            state.Clear(new Clear { Type = "clear", InstanceId = "fixture-instance", SessionId = "fixture-session", Reason = "join" });
            RendererLayout layout = Read<RendererLayout>("RendererLayout");
            RendererFrame frame = Read<RendererFrame>("RendererFrame");
            if (relay)
            {
                layout.GameType = "FOUR_PLAYER";
                frame.Session.Mode = "relay";
                frame.Session.ViewerRevision = null;
                frame.Match.Conditions.GameType = "FOUR_PLAYER";
                frame.Entities[0].Interaction.CanDrag = true;
                frame.Entities[0].Interaction.CanFlip = true;
            }
            state.SetViewport(Read<Viewport>("Viewport"));
            state.ApplyLayout(layout);
            state.ApplyFrame(frame);
            return state;
        }

        public static RendererLayout Lanes(string gameType)
        {
            RendererLayout layout = Read<RendererLayout>("RendererLayout");
            layout.GameType = gameType;
            layout.ActiveSeats = gameType == "THREE_PLAYER" ? new[] { 0, 1, 2 } :
                gameType == "BAMBOO" || gameType == "MINEFIELD" ? new[] { 0, 2 } : new[] { 0, 1, 2, 3 };
            var slots = new List<Slot>();
            string[] names = { "hand", "meld", "wall", "wall.open", "discard", "kita", "tray", "payment", "riichi", "marker" };
            foreach (int seat in new[] { 0, 1, 2, 3 })
                foreach (string group in names)
                {
                    string kind = group == "marker" ? "MARKER" :
                        group == "tray" || group == "payment" || group == "riichi" ? "STICK" : "TILE";
                    int count = group == "wall" ? 4 : group == "wall.open" ? 6 : 3;
                    for (int n = 0; n < count; n++)
                    {
                        int layer = group == "wall" ? n % 2 : 0;
                        int column = group == "wall" ? n / 2 : n;
                        float angle = seat * Mathf.PI / 2;
                        double x = -24 + column * 8;
                        double y = -55;
                        var pose = new Contract.Pose
                        {
                            Position = new Vector3Dto
                            {
                                X = 87 + x * Math.Cos(angle) - y * Math.Sin(angle),
                                Y = 87 + x * Math.Sin(angle) + y * Math.Cos(angle),
                                Z = 2 + layer * 4
                            },
                            Rotation = new QuaternionDto { Z = Math.Sin(angle / 2), W = Math.Cos(angle / 2) },
                            Size = new Vector3Dto { X = kind == "STICK" ? 20 : kind == "MARKER" ? 12 : 6,
                                Y = kind == "STICK" ? 2 : kind == "MARKER" ? 6 : 9,
                                Z = kind == "TILE" ? 4 : 1 }
                        };
                        slots.Add(new Slot
                        {
                            SlotName = group + "." + n + "@" + seat, Group = group, Seat = seat,
                            Kind = kind, Indexes = group == "wall" ? new long[] { column, layer } : new long[] { n },
                            Poses = new[] { pose }, CanFlipMultiple = group == "hand", DrawShadow = true
                        });
                    }
                }
            layout.Slots = slots.ToArray();
            return layout;
        }

        public static RendererFrame Empty(RendererLayout layout)
        {
            RendererFrame frame = Read<RendererFrame>("RendererFrame");
            frame.Session.Mode = layout.GameType == "CHANGSHA" ? "changsha" : "relay";
            frame.Session.ViewerRevision = frame.Session.Mode == "changsha" ? 1 : (long?)null;
            frame.Match.Conditions.GameType = layout.GameType;
            frame.Entities = Array.Empty<Entity>();
            frame.Seats = layout.ActiveSeats.Select(seat => new SeatDisplay { Seat = seat }).ToArray();
            frame.LocalHand.EntityIds = Array.Empty<string>();
            frame.LocalHand.DrawnEntityId = null;
            return frame;
        }
    }
}
