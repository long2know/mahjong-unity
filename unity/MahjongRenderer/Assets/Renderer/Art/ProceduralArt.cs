using System;
using Mahjong.Renderer.Contract;
using UnityEngine;

namespace Mahjong.Renderer.Art
{
    public sealed class ProceduralArt : IDisposable
    {
        public readonly Material Surface;
        public readonly Mesh Box;
        public readonly Mesh Quad;
        public readonly Texture2D Faces;
        public readonly Texture2D FacesWithoutLabels;
        public readonly Texture2D[] Backs = new Texture2D[2];
        public readonly Texture2D[] Sticks = new Texture2D[6];
        public readonly Texture2D[] Dice = new Texture2D[6];
        public readonly Texture2D Marker;
        private const int TileWidth = 64;
        private const int TileHeight = 96;
        private static readonly Color32 Ink = new Color32(24, 39, 52, 255);
        private static readonly Color32 Red = new Color32(170, 36, 51, 255);
        private static readonly Color32 Green = new Color32(21, 112, 70, 255);
        private static readonly Color32 Paper = new Color32(244, 239, 218, 255);

        public ProceduralArt(Shader shader)
        {
            if (shader == null) throw new ContractException("renderer-failure", "Original surface shader is missing.");
            Surface = new Material(shader) { name = "Original procedural surface" };
            Box = MakeBox();
            Quad = MakeQuad();
            Faces = Atlas(true);
            FacesWithoutLabels = Atlas(false);
            for (int i = 0; i < 2; i++)
            {
                var pixels = Solid(TileWidth, TileHeight, i == 0 ?
                    new Color32(30, 92, 137, 255) : new Color32(35, 111, 75, 255));
                for (int y = 5; y < TileHeight - 5; y++)
                    for (int x = 5; x < TileWidth - 5; x++)
                        if ((x + y) % 13 < 2 || (x - y + 192) % 13 < 2)
                            pixels[y * TileWidth + x] = new Color32(129, 181, 181, 255);
                Backs[i] = Texture(pixels, TileWidth, TileHeight, "Original common back " + i);
            }
            string[] values = { "100", "500", "1000", "5000", "10000", "-10000" };
            for (int i = 0; i < 6; i++)
            {
                var pixels = Solid(160, 32, Paper);
                BitmapGlyphs.Draw(pixels, 160, 32, values[i], 8, 9, 2, i == 5 ? Red : Ink);
                for (int dot = 0; dot <= i; dot++) Circle(pixels, 160, 32, 100 + dot * 9, 16, 3, i > 2 ? Red : Ink);
                Sticks[i] = Texture(pixels, 160, 32, "Original value stick " + i);
                var dice = Solid(64, 64, Paper);
                Pips(dice, 64, 64, i + 1, Ink, 5);
                Dice[i] = Texture(dice, 64, 64, "Original die " + (i + 1));
            }
            var marker = Solid(128, 64, Paper);
            BitmapGlyphs.Draw(marker, 128, 64, "EAST", 16, 21, 4, Red);
            Marker = Texture(marker, 128, 64, "Original seat marker");
        }

        public static Rect FaceUv(int index) =>
            new Rect((index % 8 + 0.01f) / 8, (index / 8 + 0.01f) / 5, 0.98f / 8, 0.98f / 5);

        public static string FaceLabel(int index)
        {
            if (index < 27) return (index % 9 + 1) + new[] { "M", "P", "S" }[index / 9];
            if (index < 34) return new[] { "E", "S", "W", "N", "RD", "GD", "WD" }[index - 27];
            return "5" + new[] { "M", "P", "S" }[index - 34] + "R";
        }

        private static Texture2D Atlas(bool labels)
        {
            int width = TileWidth * 8;
            int height = TileHeight * 5;
            var result = Solid(width, height, Paper);
            for (int face = 0; face < 37; face++)
            {
                var tile = Solid(TileWidth, TileHeight, Paper);
                int original = face < 34 ? face : new[] { 4, 13, 22 }[face - 34];
                if (original < 27)
                {
                    int suit = original / 9;
                    int rank = original % 9 + 1;
                    Color32 color = face >= 34 ? Red : suit == 0 ? Red : suit == 1 ? Ink : Green;
                    if (suit == 0)
                    {
                        BitmapGlyphs.Draw(tile, TileWidth, TileHeight, rank.ToString(), 22, 46, 4, color);
                        for (int bar = 0; bar < 3; bar++) BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 17, 29 + bar * 5, 30, 2, color);
                    }
                    else
                    {
                        for (int pip = 0; pip < rank; pip++)
                        {
                            int x = 16 + pip % 3 * 16;
                            int y = 39 + pip / 3 * 17;
                            if (suit == 1) Circle(tile, TileWidth, TileHeight, x, y, 6, color);
                            else
                            {
                                BitmapGlyphs.Rect(tile, TileWidth, TileHeight, x - 3, y - 6, 6, 12, color);
                                BitmapGlyphs.Rect(tile, TileWidth, TileHeight, x - 5, y - 1, 10, 2, color);
                            }
                        }
                    }
                }
                else
                {
                    string label = FaceLabel(original);
                    BitmapGlyphs.Draw(tile, TileWidth, TileHeight, label, (TileWidth - label.Length * 24) / 2, 40, 4,
                        original == 31 ? Red : original == 32 ? Green : Ink);
                    if (original == 33)
                    {
                        BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 7, 32, 50, 2, Ink);
                        BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 7, 72, 50, 2, Ink);
                        BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 7, 32, 2, 42, Ink);
                        BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 55, 32, 2, 42, Ink);
                    }
                }
                if (face >= 34)
                {
                    BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 3, 3, 58, 2, Red);
                    BitmapGlyphs.Rect(tile, TileWidth, TileHeight, 3, 91, 58, 2, Red);
                }
                if (labels) BitmapGlyphs.Draw(tile, TileWidth, TileHeight, FaceLabel(face), 8, 8, 2, Ink);
                for (int y = 0; y < TileHeight; y++)
                    for (int x = 0; x < TileWidth; x++)
                        result[(face / 8 * TileHeight + y) * width + face % 8 * TileWidth + x] = tile[y * TileWidth + x];
            }
            return Texture(result, width, height, "Original face atlas" + (labels ? " labelled" : ""));
        }

        private static void Pips(Color32[] pixels, int width, int height, int value, Color32 color, int radius)
        {
            int a = width / 4, b = width * 3 / 4, c = width / 2;
            int low = height / 4, high = height * 3 / 4, middle = height / 2;
            if (value % 2 == 1) Circle(pixels, width, height, c, middle, radius, value == 1 ? Red : color);
            if (value >= 2) { Circle(pixels, width, height, a, low, radius, color); Circle(pixels, width, height, b, high, radius, color); }
            if (value >= 4) { Circle(pixels, width, height, a, high, radius, color); Circle(pixels, width, height, b, low, radius, color); }
            if (value == 6) { Circle(pixels, width, height, a, middle, radius, color); Circle(pixels, width, height, b, middle, radius, color); }
        }

        private static void Circle(Color32[] pixels, int width, int height, int x, int y, int radius, Color32 color)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dy * dy <= radius * radius && x + dx >= 0 && x + dx < width &&
                        y + dy >= 0 && y + dy < height)
                        pixels[(y + dy) * width + x + dx] = color;
        }

        private static Color32[] Solid(int width, int height, Color32 color)
        {
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            return pixels;
        }

        private static Texture2D Texture(Color32[] pixels, int width, int height, string name)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Mesh MakeQuad()
        {
            var mesh = new Mesh { name = "Original horizontal quad" };
            mesh.vertices = new[] { new Vector3(-0.5f,0,-0.5f), new Vector3(-0.5f,0,0.5f),
                new Vector3(0.5f,0,0.5f), new Vector3(0.5f,0,-0.5f) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[] { new Vector2(0,0), new Vector2(0,1), new Vector2(1,1), new Vector2(1,0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh MakeBox()
        {
            var vertices = new Vector3[24];
            var normals = new Vector3[24];
            var uv = new Vector2[24];
            var triangles = new int[36];
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int face = 0; face < 6; face++)
            {
                Vector3 normal = axes[face];
                Vector3 tangent = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, normal);
                Vector3 bitangent = Vector3.Cross(normal, tangent);
                for (int i = 0; i < 4; i++)
                {
                    float x = i == 0 || i == 1 ? -0.5f : 0.5f;
                    float y = i == 0 || i == 3 ? -0.5f : 0.5f;
                    vertices[face * 4 + i] = normal * 0.5f + tangent * x + bitangent * y;
                    normals[face * 4 + i] = normal;
                    uv[face * 4 + i] = new Vector2(x + 0.5f, y + 0.5f);
                }
                int n = face * 4;
                int start = face * 6;
                triangles[start] = n; triangles[start + 1] = n + 2; triangles[start + 2] = n + 1;
                triangles[start + 3] = n; triangles[start + 4] = n + 3; triangles[start + 5] = n + 2;
            }
            var mesh = new Mesh { name = "Original box", vertices = vertices, normals = normals, uv = uv, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Dispose()
        {
            EngineObjects.Release(Surface);
            EngineObjects.Release(Box);
            EngineObjects.Release(Quad);
            EngineObjects.Release(Faces);
            EngineObjects.Release(FacesWithoutLabels);
            foreach (Texture2D texture in Backs) EngineObjects.Release(texture);
            foreach (Texture2D texture in Sticks) EngineObjects.Release(texture);
            foreach (Texture2D texture in Dice) EngineObjects.Release(texture);
            EngineObjects.Release(Marker);
        }
    }
}
