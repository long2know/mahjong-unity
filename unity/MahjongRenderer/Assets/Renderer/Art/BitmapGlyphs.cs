using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Mahjong.Renderer.Art
{
    public static class BitmapGlyphs
    {
        private static readonly Dictionary<char, Vector2[][]> Strokes = new Dictionary<char, string[]>
        {
            ['0'] = new[] { "2,0 0.6,0.6 0,2 0,8 0.6,9.4 2,10 4,10 5.4,9.4 6,8 6,2 5.4,0.6 4,0 2,0" },
            ['1'] = new[] { "1.2,8 3,10 3,0", "1,0 5,0" },
            ['2'] = new[] { "0,8 0.5,9.4 2,10 4.3,10 6,8.6 6,7 0,0 6,0" },
            ['3'] = new[] { "0,10 4.6,10 6,8.6 6,6.6 4,5.2 2,5.2", "4,5.2 6,3.8 6,1.6 4.6,0 0,0" },
            ['4'] = new[] { "4.5,0 4.5,10 0,3.2 6,3.2" },
            ['5'] = new[] { "6,10 0,10 0,5.6 4,5.6 6,4 6,1.6 4.4,0 0,0" },
            ['6'] = new[] { "5.4,10 2.2,10 0,7.2 0,1.6 1.6,0 4.4,0 6,1.6 6,4 4.4,5.6 0,5.6" },
            ['7'] = new[] { "0,10 6,10 1.2,0" },
            ['8'] = new[] { "1.6,5.2 0,6.8 0,8.4 1.6,10 4.4,10 6,8.4 6,6.8 4.4,5.2 1.6,5.2 0,3.6 0,1.6 1.6,0 4.4,0 6,1.6 6,3.6 4.4,5.2" },
            ['9'] = new[] { "6,4.4 1.6,4.4 0,6 0,8.4 1.6,10 4.4,10 6,8.4 6,2.6 3.8,0 0.6,0" },
            ['A'] = new[] { "0,0 3,10 6,0", "1.2,4 4.8,4" },
            ['B'] = new[] { "0,0 0,10 4,10 6,8.6 6,6.6 4,5.2 0,5.2", "4,5.2 6,3.8 6,1.6 4,0 0,0" },
            ['C'] = new[] { "6,9 4.4,10 1.6,10 0,8.2 0,1.8 1.6,0 4.4,0 6,1" },
            ['D'] = new[] { "0,0 0,10 3,10 5.2,8.4 6,6.6 6,3.4 5.2,1.6 3,0 0,0" },
            ['E'] = new[] { "6,10 0,10 0,0 6,0", "0,5 4.8,5" },
            ['F'] = new[] { "6,10 0,10 0,0", "0,5 4.8,5" },
            ['G'] = new[] { "6,9 4.4,10 1.6,10 0,8.2 0,1.8 1.6,0 4.4,0 6,1.6 6,4.8 3.4,4.8" },
            ['H'] = new[] { "0,0 0,10", "6,0 6,10", "0,5 6,5" },
            ['I'] = new[] { "1,10 5,10", "3,10 3,0", "1,0 5,0" },
            ['J'] = new[] { "6,10 6,2 4.4,0 1.6,0 0,2" },
            ['K'] = new[] { "0,0 0,10", "6,10 0,4.2", "2.6,6.6 6,0" },
            ['L'] = new[] { "0,10 0,0 6,0" },
            ['M'] = new[] { "0,0 0,10 3,4.4 6,10 6,0" },
            ['N'] = new[] { "0,0 0,10 6,0 6,10" },
            ['O'] = new[] { "2,0 0.6,0.6 0,2 0,8 0.6,9.4 2,10 4,10 5.4,9.4 6,8 6,2 5.4,0.6 4,0 2,0" },
            ['P'] = new[] { "0,0 0,10 4.4,10 6,8.4 6,6.6 4.4,5 0,5" },
            ['Q'] = new[] { "2,0 0.6,0.6 0,2 0,8 0.6,9.4 2,10 4,10 5.4,9.4 6,8 6,2 5.4,0.6 4,0 2,0", "3.5,2.5 6.5,-0.5" },
            ['R'] = new[] { "0,0 0,10 4.4,10 6,8.4 6,6.6 4.4,5 0,5", "3,5 6,0" },
            ['S'] = new[] { "6,9.2 4.4,10 1.6,10 0,8.4 0,6.8 1.6,5.4 4.4,4.6 6,3.2 6,1.6 4.4,0 1.6,0 0,0.8" },
            ['T'] = new[] { "0,10 6,10", "3,10 3,0" },
            ['U'] = new[] { "0,10 0,2 1.6,0 4.4,0 6,2 6,10" },
            ['V'] = new[] { "0,10 3,0 6,10" },
            ['W'] = new[] { "0,10 1.2,0 3,4.6 4.8,0 6,10" },
            ['X'] = new[] { "0,10 6,0", "0,0 6,10" },
            ['Y'] = new[] { "0,10 3,5 6,10", "3,5 3,0" },
            ['Z'] = new[] { "0,10 6,10 0,0 6,0" },
            ['-'] = new[] { "0,5 6,5" },
            [':'] = new[] { "3,7 3,7.1", "3,2.9 3,3" },
            ['.'] = new[] { "3,0 3,0.1" },
            ['/'] = new[] { "0,0 6,10" },
            ['?'] = new[] { "0,8.5 1.6,10 4.4,10 6,8.5 6,7 3,4.5 3,3", "3,0 3,0.1" },
            ['+'] = new[] { "0,5 6,5", "3,2 3,8" }
        }.ToDictionary(item => item.Key, item => item.Value.Select(path => path.Split(' ').Select(point =>
        {
            string[] pair = point.Split(',');
            return new Vector2(float.Parse(pair[0], CultureInfo.InvariantCulture),
                float.Parse(pair[1], CultureInfo.InvariantCulture));
        }).ToArray()).ToArray());
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
            float unit = scale * 0.7f;
            foreach (char c in text)
            {
                foreach (Vector2[] stroke in Paths(c))
                    for (int segment = 1; segment < stroke.Length; segment++)
                    {
                        Vector2 a = new Vector2(x, y) + stroke[segment - 1] * unit;
                        Vector2 b = new Vector2(x, y) + stroke[segment] * unit;
                        float radius = unit * 0.37f;
                        int left = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - radius - 1));
                        int right = Mathf.Min(textureWidth - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + radius + 1));
                        int low = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - radius - 1));
                        int high = Mathf.Min(textureHeight - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + radius + 1));
                        Vector2 delta = b - a;
                        for (int py = low; py <= high; py++)
                            for (int px = left; px <= right; px++)
                            {
                                var point = new Vector2(px + 0.5f, py + 0.5f);
                                float along = Mathf.Clamp01(Vector2.Dot(point - a, delta) / delta.sqrMagnitude);
                                float coverage = Mathf.Clamp01(radius + 0.5f - Vector2.Distance(point, a + delta * along));
                                int at = py * textureWidth + px;
                                pixels[at] = Color.Lerp(pixels[at], color, coverage);
                            }
                    }
                x += Mathf.RoundToInt(7.6f * unit);
            }
        }

        private static Vector2[][] Paths(char value) => value == ' ' || value == '_' ?
            Array.Empty<Vector2[]>() : Strokes.TryGetValue(char.ToUpperInvariant(value), out Vector2[][] paths) ? paths : Strokes['?'];

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
            float unit = height / 10;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var normals = new List<Vector3>();
            float start = -Mathf.Max(0, (limit - 1) * 7.6f + 6) * unit / 2;
            for (int character = 0; character < limit; character++)
            {
                foreach (Vector2[] stroke in Paths(text[character]))
                {
                    var origin = new Vector2(start + character * 7.6f * unit, 0);
                    float radius = unit * 0.37f;
                    for (int segment = 1; segment < stroke.Length; segment++)
                    {
                        Vector2 a = origin + stroke[segment - 1] * unit;
                        Vector2 b = origin + stroke[segment] * unit;
                        Vector2 direction = (b - a).normalized;
                        var offset = new Vector2(-direction.y, direction.x) * radius;
                        int i = vertices.Count;
                        foreach (Vector2 point in new[] { a - offset, a + offset, b + offset, b - offset })
                            vertices.Add(new Vector3(point.x, 0, point.y));
                        for (int n = 0; n < 4; n++) normals.Add(Vector3.up);
                        triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                    }
                    foreach (Vector2 point in stroke)
                    {
                        Vector2 center = origin + point * unit;
                        int i = vertices.Count;
                        vertices.Add(new Vector3(center.x, 0, center.y));
                        normals.Add(Vector3.up);
                        for (int cap = 0; cap < 8; cap++)
                        {
                            float angle = cap * Mathf.PI / 4;
                            vertices.Add(new Vector3(center.x + Mathf.Cos(angle) * radius, 0,
                                center.y + Mathf.Sin(angle) * radius));
                            normals.Add(Vector3.up);
                            triangles.AddRange(new[] { i, i + (cap + 1) % 8 + 1, i + cap + 1 });
                        }
                    }
                }
            }
            var mesh = new Mesh { name = "Original rounded-stroke vector label" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
