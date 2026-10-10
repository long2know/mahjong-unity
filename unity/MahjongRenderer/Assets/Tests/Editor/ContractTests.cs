using System;
using Mahjong.Renderer.Contract;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Mahjong.Renderer.Tests
{
    public sealed class ContractTests
    {
        [TestCase("{\"x\":1,}")]
        [TestCase("{x:1}")]
        [TestCase("{'x':1}")]
        [TestCase("[1,]")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("{\"x\":Infinity}")]
        [TestCase("{\"x\":1e999}")]
        [TestCase("{\"x\":01}")]
        [TestCase("{\"x\":.5}")]
        [TestCase("{\"x\":1}/*comment*/")]
        [TestCase("{\"x\":1,\"\\u0078\":2}")]
        [TestCase("{} {}")]
        public void StrictParserRejectsJavaScriptExtensionsAndDuplicateProperties(string text)
        {
            Assert.That(() => StrictJson.Parse(text), Throws.TypeOf<ContractException>());
        }

        [Test]
        public void FrozenFixturesRoundTripThroughActualSchemaAndDtoParser()
        {
            foreach (string definition in new[] { "RendererLayout", "RendererFrame", "RendererAction", "Viewport" })
                Assert.DoesNotThrow(() => SourceFixtures.Schema.Validate(SourceFixtures.Json(definition), definition), definition);
            RendererFrame frame = SourceFixtures.Read<RendererFrame>("RendererFrame");
            JObject json = JObject.Parse(SourceFixtures.Schema.Write(frame, "RendererFrame"));
            Assert.That(json["entities"][1]["appearance"]["faceIndex"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["localHand"]["drawnEntityId"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["pickup"].Type, Is.EqualTo(JTokenType.Null));
        }

        [TestCase("tileId")]
        [TestCase("auth")]
        [TestCase("stateVersion")]
        [TestCase("$type")]
        public void UnknownWireAndAuthPropertiesAreRejected(string property)
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][0][property] = 108;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void UnsupportedVersionAndUnknownEnumsDoNotDefault()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["contractVersion"] = "2.0.0";
            ContractException version = Assert.Throws<ContractException>(() =>
                SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"));
            Assert.That(version.Code, Is.EqualTo("unsupported-contract"));
            frame["contractVersion"] = "1.0.0";
            frame["turn"]["phase"] = "EndGame";
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void NullHiddenFaceNeverBecomesFaceZeroAndPrivacyConditionsAreEnforced()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][1]["appearance"]["faceIndex"] = 0;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][0]["appearance"]["faceIndex"] = JValue.CreateNull();
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void UnreadySnapshotCannotCarryEntitiesOrHand()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["session"]["snapshotReady"] = false;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void MissingRequiredNullsAndNonfiniteNumbersAreNotCoerced()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            ((JObject)frame["session"]).Remove("actorSeat");
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Json("RendererFrame");
            frame["presentation"]["zoom"] = double.NaN;
            Assert.That(() => SourceFixtures.Schema.Validate(frame, "RendererFrame"),
                Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][0]["pose"]["size"]["x"] = 0;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void InvalidActionsCannotInjectActorOrTileWireFields()
        {
            JObject action = SourceFixtures.Json("RendererAction");
            action["intent"]["tileId"] = 1;
            Assert.That(() => SourceFixtures.Schema.Validate(action, "RendererAction"), Throws.TypeOf<ContractException>());
            action = SourceFixtures.Json("RendererAction");
            action["actorSeat"] = 0;
            Assert.That(() => SourceFixtures.Schema.Validate(action, "RendererAction"), Throws.TypeOf<ContractException>());
        }
    }
}
