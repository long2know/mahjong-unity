using System;
using System.Collections.Generic;
using Mahjong.Renderer.Contract;
using UnityEngine;

namespace Mahjong.Renderer.Art
{
    public sealed class ProceduralArt : IDisposable
    {
        public readonly Material Surface;
        public readonly Material Felt;
        public readonly Material Wood;
        public readonly Material Decal;
        public readonly Mesh Box;
        public readonly Mesh RoundedBox;
        public readonly Mesh TableBox;
        public readonly Mesh TableRail;
        public readonly Mesh Quad;
        public readonly Texture2D Faces;
        public readonly Texture2D FacesWithoutLabels;
        public readonly Texture2D[] Backs = new Texture2D[2];
        public readonly Texture2D[] Sticks = new Texture2D[6];
        public readonly Texture2D[] Dice = new Texture2D[6];
        public readonly Texture2D Marker;
        public readonly Texture2D TableFabric;
        public const int AtlasSize = 1536;
        public const int CellWidth = 192;
        public const int CellHeight = 288;
        public static readonly Color Ivory = new Color32(247, 243, 230, 255);
        private static readonly string[] FaceNames = {
            "Man1","Man2","Man3","Man4","Man5","Man6","Man7","Man8","Man9",
            "Pin1","Pin2","Pin3","Pin4","Pin5","Pin6","Pin7","Pin8","Pin9",
            "Sou1","Sou2","Sou3","Sou4","Sou5","Sou6","Sou7","Sou8","Sou9",
            "Ton","Nan","Shaa","Pei","Haku","Hatsu","Chun","Man5-Dora","Pin5-Dora","Sou5-Dora"
        };
        private static readonly Color32 Ink = new Color32(24, 39, 52, 255);
        private static readonly Color32 Red = new Color32(170, 36, 51, 255);
        private static readonly Color32 Paper = new Color32(247, 243, 230, 255);

        public ProceduralArt(Shader shader)
        {
            if (shader == null) throw new ContractException("renderer-failure", "Original surface shader is missing.");
            Surface = new Material(shader) { name = "Mahjong satin surface", enableInstancing = true };
            Felt = new Material(Surface) { name = "Mahjong woven felt", renderQueue = 1900 };
            Felt.EnableKeyword("MAHJONG_FELT");
            Wood = new Material(Surface) { name = "Mahjong walnut grain", renderQueue = 2000 };
            Wood.EnableKeyword("MAHJONG_WOOD");
            Decal = new Material(shader) { name = "Mahjong soft decals", renderQueue = 3000 };
            Decal.EnableKeyword("MAHJONG_DECAL");
            Decal.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            Decal.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            Decal.SetInt("_ZWrite", 0);
            Decal.SetOverrideTag("RenderType", "Transparent");
            Box = MakeBox();
            RoundedBox = MakeBeveledBox(0.07f, "Original beveled tile / die");
            TableBox = MakeRoundedBox(0.018f, "Original softly rounded table");
            TableRail = MakeRoundedBox(0.018f, "Original rounded rail shell", true);
            Quad = MakeQuad();
            Faces = Load("FacesLabelled", AtlasSize, AtlasSize);
            FacesWithoutLabels = Load("Faces", AtlasSize, AtlasSize);
            Backs[0] = Load("Back0", CellWidth, CellHeight);
            Backs[1] = Load("Back1", CellWidth, CellHeight);
            TableFabric = Load("TableFabric", 512, 512);
            TableFabric.anisoLevel = 2;
            string[] values = { "100", "500", "1000", "5000", "10000", "-10000" };
            for (int i = 0; i < 6; i++)
            {
                var pixels = Solid(320, 64, Paper);
                BitmapGlyphs.Draw(pixels, 320, 64, values[i], 12, 11, 6, i == 5 ? Red : Ink);
                for (int dot = 0; dot <= i; dot++) Circle(pixels, 320, 64, 236 + dot * 11, 32, 4, i > 2 ? Red : Ink);
                Sticks[i] = Texture(pixels, 320, 64, "Original value stick " + i);
                var dice = Solid(128, 128, Paper);
                Pips(dice, 128, 128, i + 1, Ink, 9);
                Dice[i] = Texture(dice, 128, 128, "Original die " + (i + 1));
            }
            var marker = Solid(256, 128, Paper);
            BitmapGlyphs.Draw(marker, 256, 128, "EAST", 10, 29, 10, Red);
            Marker = Texture(marker, 256, 128, "Original seat marker");
        }

        private static Texture2D Load(string name, int width, int height)
        {
            Texture2D texture = Resources.Load<Texture2D>("Art/" + name);
            if (texture == null || texture.width != width || texture.height != height)
                throw new ContractException("renderer-failure", "Pinned artwork is missing or incorrectly imported: " + name);
            return texture;
        }

        public static Rect FaceUv(int index)
        {
            FaceName(index);
            return new Rect((index % 8 * CellWidth + 0.5f) / AtlasSize,
                (index / 8 * CellHeight + 0.5f) / AtlasSize,
                (CellWidth - 1f) / AtlasSize, (CellHeight - 1f) / AtlasSize);
        }

        public static string FaceName(int index)
        {
            if (index < 0 || index >= FaceNames.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return FaceNames[index];
        }

        public static Color BackColor(int index) => index == 0 ?
            (Color)new Color32(35, 105, 116, 255) : new Color32(165, 113, 55, 255);

        public static string FaceLabel(int index)
        {
            FaceName(index);
            if (index < 27) return (index % 9 + 1) + new[] { "M", "P", "S" }[index / 9];
            if (index < 34) return new[] { "E", "S", "W", "N", "WD", "GD", "RD" }[index - 27];
            return "5" + new[] { "M", "P", "S" }[index - 34] + "R";
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
            for (int dy = -radius - 1; dy <= radius + 1; dy++)
                for (int dx = -radius - 1; dx <= radius + 1; dx++)
                    if (x + dx >= 0 && x + dx < width &&
                        y + dy >= 0 && y + dy < height)
                    {
                        float coverage = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                        int at = (y + dy) * width + x + dx;
                        pixels[at] = Color.Lerp(pixels[at], color, coverage);
                    }
        }

        private static Color32[] Solid(int width, int height, Color32 color)
        {
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            return pixels;
        }

        private static Texture2D Texture(Color32[] pixels, int width, int height, string name)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
                { name = name, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
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

        private static Mesh MakeRoundedBox(float radius, string name, bool rail = false)
        {
            float core = 0.5f - radius;
            float[] steps = { -0.5f, -core, core, 0.5f };
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (Vector3 normal in axes)
            {
                Vector3 tangent = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, normal);
                Vector3 bitangent = Vector3.Cross(normal, tangent);
                int start = vertices.Count;
                for (int y = 0; y < steps.Length; y++)
                    for (int x = 0; x < steps.Length; x++)
                    {
                        Vector3 point = normal * 0.5f + tangent * steps[x] + bitangent * steps[y];
                        Vector3 nearest = new Vector3(Mathf.Clamp(point.x, -core, core),
                            Mathf.Clamp(point.y, -core, core), Mathf.Clamp(point.z, -core, core));
                        Vector3 outward = (point - nearest).normalized;
                        vertices.Add(nearest + outward * radius);
                        normals.Add(outward);
                        uv.Add(new Vector2(steps[x] + 0.5f, steps[y] + 0.5f));
                        if (x == steps.Length - 1 || y == steps.Length - 1) continue;
                        if (rail && (normal == Vector3.down || normal == Vector3.up && x == 1 && y == 1)) continue;
                        int n = start + y * steps.Length + x;
                        triangles.AddRange(new[] { n, n + 1, n + steps.Length + 1,
                            n, n + steps.Length + 1, n + steps.Length });
                    }
            }
            if (rail)
            {
                int start = vertices.Count;
                foreach (float half in new[] { core, 0.415f })
                    foreach (Vector3 corner in new[] { new Vector3(-1, 0, -1), new Vector3(-1, 0, 1),
                        new Vector3(1, 0, 1), new Vector3(1, 0, -1) })
                    {
                        Vector3 point = corner * half + Vector3.up * 0.5f;
                        vertices.Add(point);
                        normals.Add(Vector3.up);
                        uv.Add(new Vector2(point.x + 0.5f, 0.5f - point.z));
                    }
                for (int n = 0; n < 4; n++)
                {
                    int next = (n + 1) % 4;
                    triangles.AddRange(new[] { start + n, start + next, start + 4 + next,
                        start + n, start + 4 + next, start + 4 + n });
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv);
            var heights = new List<Vector2>(vertices.Count);
            foreach (Vector3 vertex in vertices) heights.Add(new Vector2(vertex.y, 0));
            mesh.SetUVs(1, heights);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh MakeBeveledBox(float radius, string name)
        {
            float core = 0.5f - radius;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            void Polygon(Vector3 normal, Vector3[] points, Vector3[] smooth = null)
            {
                normal.Normalize();
                Vector3 tangent = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, normal).normalized;
                Vector3 bitangent = Vector3.Cross(normal, tangent);
                int start = vertices.Count;
                for (int n = 0; n < points.Length; n++)
                {
                    vertices.Add(points[n]);
                    normals.Add(smooth == null ? normal : smooth[n]);
                    uv.Add(new Vector2(Vector3.Dot(points[n], tangent) + 0.5f, Vector3.Dot(points[n], bitangent) + 0.5f));
                }
                bool reverse = Vector3.Dot(Vector3.Cross(points[1] - points[0], points[2] - points[0]), normal) < 0;
                for (int n = 1; n < points.Length - 1; n++)
                    triangles.AddRange(reverse ? new[] { start, start + n + 1, start + n } :
                        new[] { start, start + n, start + n + 1 });
            }
            Vector3[] faces = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (Vector3 normal in faces)
            {
                Vector3 tangent = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, normal);
                Vector3 bitangent = Vector3.Cross(normal, tangent);
                Polygon(normal, new[] {
                    normal * 0.5f - tangent * core - bitangent * core,
                    normal * 0.5f - tangent * core + bitangent * core,
                    normal * 0.5f + tangent * core + bitangent * core,
                    normal * 0.5f + tangent * core - bitangent * core
                });
            }
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            int[] signs = { -1, 1 };
            for (int a = 0; a < 3; a++)
                for (int b = a + 1; b < 3; b++)
                    foreach (int sa in signs)
                        foreach (int sb in signs)
                        {
                            Vector3 na = axes[a] * sa, nb = axes[b] * sb;
                            Vector3 along = Vector3.Cross(na, nb) * core;
                            Polygon(na + nb, new[] {
                                na * 0.5f + nb * core - along, na * 0.5f + nb * core + along,
                                na * core + nb * 0.5f + along, na * core + nb * 0.5f - along
                            }, new[] { na, na, nb, nb });
                        }
            foreach (int x in signs)
                foreach (int y in signs)
                    foreach (int z in signs)
                    {
                        Vector3 nx = Vector3.right * x, ny = Vector3.up * y, nz = Vector3.forward * z;
                        Polygon(nx + ny + nz, new[] {
                            nx * 0.5f + ny * core + nz * core, nx * core + ny * 0.5f + nz * core,
                            nx * core + ny * core + nz * 0.5f
                        });
                    }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, vertices.ConvertAll(vertex => new Vector2(vertex.y, 0)));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Dispose()
        {
            EngineObjects.Release(Surface);
            EngineObjects.Release(Felt);
            EngineObjects.Release(Wood);
            EngineObjects.Release(Decal);
            EngineObjects.Release(Box);
            EngineObjects.Release(RoundedBox);
            EngineObjects.Release(TableBox);
            EngineObjects.Release(TableRail);
            EngineObjects.Release(Quad);
            foreach (Texture2D texture in Sticks) EngineObjects.Release(texture);
            foreach (Texture2D texture in Dice) EngineObjects.Release(texture);
            EngineObjects.Release(Marker);
        }
    }
}
