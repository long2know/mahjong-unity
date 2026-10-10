using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mahjong.Renderer.Art
{
    public static class BitmapGlyphs
    {
        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['0'] = new[] { "01110","10001","10011","10101","11001","10001","01110" },
            ['1'] = new[] { "00100","01100","00100","00100","00100","00100","01110" },
            ['2'] = new[] { "01110","10001","00001","00010","00100","01000","11111" },
            ['3'] = new[] { "11110","00001","00001","01110","00001","00001","11110" },
            ['4'] = new[] { "00010","00110","01010","10010","11111","00010","00010" },
            ['5'] = new[] { "11111","10000","10000","11110","00001","00001","11110" },
            ['6'] = new[] { "01110","10000","10000","11110","10001","10001","01110" },
            ['7'] = new[] { "11111","00001","00010","00100","01000","01000","01000" },
            ['8'] = new[] { "01110","10001","10001","01110","10001","10001","01110" },
            ['9'] = new[] { "01110","10001","10001","01111","00001","00001","01110" },
            ['A'] = new[] { "00100","01010","10001","11111","10001","10001","10001" },
            ['B'] = new[] { "11110","10001","10001","11110","10001","10001","11110" },
            ['C'] = new[] { "01111","10000","10000","10000","10000","10000","01111" },
            ['D'] = new[] { "11110","10001","10001","10001","10001","10001","11110" },
            ['E'] = new[] { "11111","10000","10000","11110","10000","10000","11111" },
            ['F'] = new[] { "11111","10000","10000","11110","10000","10000","10000" },
            ['G'] = new[] { "01111","10000","10000","10111","10001","10001","01111" },
            ['H'] = new[] { "10001","10001","10001","11111","10001","10001","10001" },
            ['I'] = new[] { "01110","00100","00100","00100","00100","00100","01110" },
            ['J'] = new[] { "00001","00001","00001","00001","10001","10001","01110" },
            ['K'] = new[] { "10001","10010","10100","11000","10100","10010","10001" },
            ['L'] = new[] { "10000","10000","10000","10000","10000","10000","11111" },
            ['M'] = new[] { "10001","11011","10101","10101","10001","10001","10001" },
            ['N'] = new[] { "10001","11001","10101","10011","10001","10001","10001" },
            ['O'] = new[] { "01110","10001","10001","10001","10001","10001","01110" },
            ['P'] = new[] { "11110","10001","10001","11110","10000","10000","10000" },
            ['Q'] = new[] { "01110","10001","10001","10001","10101","10010","01101" },
            ['R'] = new[] { "11110","10001","10001","11110","10100","10010","10001" },
            ['S'] = new[] { "01111","10000","10000","01110","00001","00001","11110" },
            ['T'] = new[] { "11111","00100","00100","00100","00100","00100","00100" },
            ['U'] = new[] { "10001","10001","10001","10001","10001","10001","01110" },
            ['V'] = new[] { "10001","10001","10001","10001","10001","01010","00100" },
            ['W'] = new[] { "10001","10001","10001","10101","10101","10101","01010" },
            ['X'] = new[] { "10001","10001","01010","00100","01010","10001","10001" },
            ['Y'] = new[] { "10001","10001","01010","00100","00100","00100","00100" },
            ['Z'] = new[] { "11111","00001","00010","00100","01000","10000","11111" },
            ['-'] = new[] { "00000","00000","00000","11111","00000","00000","00000" },
            [':'] = new[] { "00000","00100","00100","00000","00100","00100","00000" },
            ['.'] = new[] { "00000","00000","00000","00000","00000","00100","00100" },
            ['/'] = new[] { "00001","00001","00010","00100","01000","10000","10000" },
            ['?'] = new[] { "01110","10001","00001","00010","00100","00000","00100" },
            ['+'] = new[] { "00000","00100","00100","11111","00100","00100","00000" }
        };

        public static string[] Rows(char value)
        {
            if (value == ' ') return null;
            return Glyphs.TryGetValue(char.ToUpperInvariant(value), out string[] rows) ? rows : Glyphs['?'];
        }

        public static void Draw(Color32[] pixels, int textureWidth, int textureHeight,
            string text, int x, int y, int scale, Color32 color)
        {
            foreach (char c in text)
            {
                string[] rows = Rows(c);
                if (rows != null)
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                            if (rows[row][col] == '1')
                                Rect(pixels, textureWidth, textureHeight,
                                    x + col * scale, y + (6 - row) * scale, scale, scale, color);
                x += 6 * scale;
            }
        }

        public static void Rect(Color32[] pixels, int width, int height, int x, int y, int w, int h, Color32 color)
        {
            for (int dy = Mathf.Max(0, y); dy < Mathf.Min(height, y + h); dy++)
                for (int dx = Mathf.Max(0, x); dx < Mathf.Min(width, x + w); dx++)
                    pixels[dy * width + dx] = color;
        }

        public static Mesh Mesh(string text, float height)
        {
            text = text ?? "";
            int limit = Mathf.Min(text.Length, 56);
            float pixel = height / 7;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var normals = new List<Vector3>();
            float start = -limit * 6 * pixel / 2;
            for (int character = 0; character < limit; character++)
            {
                string[] rows = Rows(text[character]);
                if (rows == null) continue;
                for (int row = 0; row < 7; row++)
                    for (int col = 0; col < 5; col++)
                    {
                        if (rows[row][col] != '1') continue;
                        float x = start + (character * 6 + col) * pixel;
                        float z = (6 - row) * pixel;
                        int i = vertices.Count;
                        vertices.Add(new Vector3(x, 0, z));
                        vertices.Add(new Vector3(x, 0, z + pixel * 0.86f));
                        vertices.Add(new Vector3(x + pixel * 0.86f, 0, z + pixel * 0.86f));
                        vertices.Add(new Vector3(x + pixel * 0.86f, 0, z));
                        for (int n = 0; n < 4; n++) normals.Add(Vector3.up);
                        triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                    }
            }
            var mesh = new Mesh { name = "Original bitmap label" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
