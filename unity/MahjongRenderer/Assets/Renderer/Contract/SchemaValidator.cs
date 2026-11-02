using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Mahjong.Renderer.Contract
{
    public sealed class SchemaValidator
    {
        private readonly JObject schema;
        private readonly JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Error,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = StrictJson.MaximumDepth
        });

        public SchemaValidator(string schemaJson)
        {
            schema = StrictJson.Parse(schemaJson) as JObject ??
                     throw new ContractException("renderer-failure", "Renderer schema is missing.");
        }

        public static SchemaValidator Load()
        {
            TextAsset asset = Resources.Load<TextAsset>("renderer-v1.schema");
            if (asset == null) throw new ContractException("renderer-failure", "Renderer schema asset is missing.");
            return new SchemaValidator(asset.text);
        }

        public T Read<T>(string json, string definition)
        {
            JToken value = StrictJson.Parse(json);
            JToken version = value.Type == JTokenType.Object ? value["contractVersion"] : null;
            if (version != null && !JToken.DeepEquals(version, new JValue(ContractVersion.Current)))
                throw new ContractException("unsupported-contract", "Only renderer contract 1.0.0 is supported.");
            Validate(value, definition);
            try { return value.ToObject<T>(serializer); }
            catch (Exception e) when (e is JsonException || e is OverflowException || e is FormatException)
            {
                throw new ContractException("invalid-message", "Message cannot be represented by renderer DTOs.");
            }
        }

        public string Write(object value, string definition)
        {
            var token = JToken.FromObject(value, serializer);
            Validate(token, definition);
            return token.ToString(Formatting.None);
        }

        public void Validate(JToken value, string definition)
        {
            var rule = schema["definitions"]?[definition] as JObject;
            if (rule == null) throw new ContractException("renderer-failure", "Unknown schema definition.");
            string error = Check(value, rule, "$");
            if (error != null) throw new ContractException("invalid-message", error);
        }

        private string Check(JToken value, JObject rule, string path)
        {
            if (rule.TryGetValue("$ref", out JToken reference))
            {
                string prefix = "#/definitions/";
                string name = reference.Value<string>();
                if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                    !(schema["definitions"]?[name.Substring(prefix.Length)] is JObject resolved))
                    throw new ContractException("renderer-failure", "Unresolvable renderer schema reference.");
                return Check(value, resolved, path);
            }
            if (rule.TryGetValue("const", out JToken constant) && !Equal(value, constant))
                return path + ": incompatible constant.";
            if (rule["enum"] is JArray enums && !enums.Any(option => Equal(value, option)))
                return path + ": unsupported enum.";
            if (rule["type"] != null && !HasType(value, rule["type"].Value<string>()))
                return path + ": incorrect type.";
            if (rule["anyOf"] is JArray any && !any.Any(option => Check(value, (JObject)option, path) == null))
                return path + ": no permitted type.";
            if (rule["oneOf"] is JArray one && one.Count(option => Check(value, (JObject)option, path) == null) != 1)
                return path + ": ambiguous or unsupported shape.";
            if (rule["allOf"] is JArray all)
            {
                foreach (JObject option in all)
                {
                    string error = Check(value, option, path);
                    if (error != null) return error;
                }
            }
            if (rule["if"] is JObject condition)
            {
                string branch = Check(value, condition, path) == null ? "then" : "else";
                if (rule[branch] is JObject branchRule)
                {
                    string error = Check(value, branchRule, path);
                    if (error != null) return error;
                }
            }
            if (value is JObject obj)
            {
                if (rule["required"] is JArray required)
                    foreach (JToken key in required)
                        if (obj.Property(key.Value<string>(), StringComparison.Ordinal) == null)
                            return path + ": missing required property.";
                JObject properties = rule["properties"] as JObject;
                foreach (JProperty property in obj.Properties())
                {
                    if (properties?[property.Name] is JObject propertyRule)
                    {
                        string error = Check(property.Value, propertyRule, path + "." + property.Name);
                        if (error != null) return error;
                    }
                    else if (rule["additionalProperties"]?.Type == JTokenType.Boolean &&
                             !rule["additionalProperties"].Value<bool>())
                        return path + ": unknown property.";
                }
            }
            if (value is JArray array)
            {
                if (rule["minItems"] != null && array.Count < rule["minItems"].Value<int>() ||
                    rule["maxItems"] != null && array.Count > rule["maxItems"].Value<int>())
                    return path + ": incorrect array length.";
                if (rule["uniqueItems"]?.Value<bool>() == true)
                    for (int i = 0; i < array.Count; i++)
                        for (int j = 0; j < i; j++)
                            if (Equal(array[i], array[j])) return path + ": duplicate array item.";
                if (rule["items"] is JObject itemRule)
                    for (int i = 0; i < array.Count; i++)
                    {
                        string error = Check(array[i], itemRule, path + "[" + i + "]");
                        if (error != null) return error;
                    }
            }
            if (value.Type == JTokenType.String)
            {
                string text = value.Value<string>();
                int length = CodePointLength(text);
                if (rule["minLength"] != null && length < rule["minLength"].Value<int>() ||
                    rule["maxLength"] != null && length > rule["maxLength"].Value<int>())
                    return path + ": incorrect string length.";
                if (rule["pattern"] != null && !Regex.IsMatch(text, rule["pattern"].Value<string>(),
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    return path + ": incorrect string format.";
            }
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                double number = value.Value<double>();
                if (double.IsNaN(number) || double.IsInfinity(number)) return path + ": nonfinite number.";
                if (rule["minimum"] != null && number < rule["minimum"].Value<double>() ||
                    rule["maximum"] != null && number > rule["maximum"].Value<double>() ||
                    rule["exclusiveMinimum"] != null && number <= rule["exclusiveMinimum"].Value<double>())
                    return path + ": number outside bounds.";
            }
            return null;
        }

        private static bool HasType(JToken value, string type)
        {
            switch (type)
            {
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "null": return value.Type == JTokenType.Null;
                case "number": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
                case "integer":
                    return (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) &&
                           Math.Truncate(value.Value<double>()) == value.Value<double>();
                default: throw new ContractException("renderer-failure", "Unsupported renderer schema keyword.");
            }
        }

        private static bool Equal(JToken a, JToken b)
        {
            if (HasNumericType(a) && HasNumericType(b)) return a.Value<double>() == b.Value<double>();
            return JToken.DeepEquals(a, b);
        }

        private static bool HasNumericType(JToken value) =>
            value.Type == JTokenType.Integer || value.Type == JTokenType.Float;

        private static int CodePointLength(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++, count++)
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            return count;
        }
    }
}
