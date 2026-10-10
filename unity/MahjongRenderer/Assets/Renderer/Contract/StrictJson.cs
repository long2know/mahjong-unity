using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mahjong.Renderer.Contract
{
    public sealed class ContractException : Exception
    {
        public string Code { get; }
        public ContractException(string code, string message) : base(message) { Code = code; }
    }

    public static class StrictJson
    {
        public const int MaximumLength = 16 * 1024 * 1024;
        public const int MaximumDepth = 64;

        public static JToken Parse(string json)
        {
            if (json == null || json.Length == 0 || json.Length > MaximumLength)
                throw Invalid("JSON length is invalid.");
            new Scanner(json).Run();
            try
            {
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Double;
                    reader.MaxDepth = MaximumDepth;
                    reader.Culture = CultureInfo.InvariantCulture;
                    return JToken.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Load,
                        LineInfoHandling = LineInfoHandling.Ignore
                    });
                }
            }
            catch (JsonException) { throw Invalid("Malformed JSON or duplicate property."); }
        }

        private static ContractException Invalid(string message) =>
            new ContractException("invalid-message", message);

        // Json.NET accepts JavaScript extensions. This scanner deliberately accepts only RFC JSON.
        private sealed class Scanner
        {
            private readonly string input;
            private int cursor;
            public Scanner(string input) { this.input = input; }
            public void Run()
            {
                Value(0);
                Space();
                if (cursor != input.Length) Fail();
            }

            private char Current => cursor < input.Length ? input[cursor] : '\0';
            private void Space()
            {
                while (Current == ' ' || Current == '\t' || Current == '\r' || Current == '\n')
                    cursor++;
            }

            private void Value(int depth)
            {
                if (depth >= MaximumDepth) throw Invalid("JSON nesting is too deep.");
                Space();
                if (Current == '{') Object(depth + 1);
                else if (Current == '[') Array(depth + 1);
                else if (Current == '"') String();
                else if (Current == 't') Literal("true");
                else if (Current == 'f') Literal("false");
                else if (Current == 'n') Literal("null");
                else if (Current == '-' || Digit(Current)) Number();
                else Fail();
            }

            private void Object(int depth)
            {
                cursor++;
                Space();
                if (Current == '}') { cursor++; return; }
                while (true)
                {
                    Space();
                    String();
                    Space();
                    Take(':');
                    Value(depth);
                    Space();
                    if (Current == '}') { cursor++; return; }
                    Take(',');
                }
            }

            private void Array(int depth)
            {
                cursor++;
                Space();
                if (Current == ']') { cursor++; return; }
                while (true)
                {
                    Value(depth);
                    Space();
                    if (Current == ']') { cursor++; return; }
                    Take(',');
                }
            }

            private void String()
            {
                Take('"');
                while (cursor < input.Length)
                {
                    char c = input[cursor++];
                    if (c == '"') return;
                    if (c < 0x20) Fail();
                    if (c != '\\') continue;
                    if (cursor >= input.Length) Fail();
                    c = input[cursor++];
                    if (c == '"' || c == '\\' || c == '/' || c == 'b' || c == 'f' ||
                        c == 'n' || c == 'r' || c == 't') continue;
                    if (c != 'u') Fail();
                    for (int i = 0; i < 4; i++)
                    {
                        if (!Hex(Current)) Fail();
                        cursor++;
                    }
                }
                Fail();
            }

            private void Literal(string value)
            {
                foreach (char c in value) Take(c);
            }

            private void Number()
            {
                int start = cursor;
                if (Current == '-') cursor++;
                if (Current == '0') cursor++;
                else
                {
                    if (Current < '1' || Current > '9') Fail();
                    while (Digit(Current)) cursor++;
                }
                if (Current == '.')
                {
                    cursor++;
                    if (!Digit(Current)) Fail();
                    while (Digit(Current)) cursor++;
                }
                if (Current == 'e' || Current == 'E')
                {
                    cursor++;
                    if (Current == '+' || Current == '-') cursor++;
                    if (!Digit(Current)) Fail();
                    while (Digit(Current)) cursor++;
                }
                if (!double.TryParse(input.Substring(start, cursor - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double number) ||
                    double.IsNaN(number) || double.IsInfinity(number))
                    throw Invalid("JSON numbers must be finite.");
            }

            private static bool Digit(char c) => c >= '0' && c <= '9';
            private static bool Hex(char c) => Digit(c) || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
            private void Take(char c) { if (Current != c) Fail(); cursor++; }
            private void Fail() { throw Invalid("Malformed JSON at character " + cursor + "."); }
        }
    }
}
