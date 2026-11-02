using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Renderer.Bridge;
using Mahjong.Renderer.Contract;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Mahjong.Renderer.Editor
{
    public static class WebGlBuild
    {
        public const string ScenePath = "Assets/Scenes/MahjongRenderer.unity";
        private const string IdentityPath = "Assets/Resources/renderer-build-identity.json";

        [MenuItem("Mahjong/Build pinned Web renderer")]
        public static void Build()
        {
            int exitCode = 1;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
            string output = Path.Combine(root, "artifacts/unity-webgl");
            string manifestPath = Path.Combine(output, "renderer-build.json");
            try
            {
                RejectLinkedPath(root, output);
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                VerifyEnvironment();
                ApplySettings();
                VerifyScene();
                AssetDatabase.SaveAssets();
                var source = SourceMetadata(root);
                string sourceHash = source["rendererSourceTreeSha256"].Value<string>();
                string buildId = "renderer-" + sourceHash.Substring(0, 32);
                var identity = new JObject
                {
                    ["contractVersion"] = ContractVersion.Current, ["buildId"] = buildId,
                    ["unityVersion"] = ContractVersion.Editor, ["unityRevision"] = ContractVersion.EditorRevision
                };
                File.WriteAllText(IdentityPath, identity.ToString(Formatting.None), new UTF8Encoding(false));
                File.WriteAllText(IdentityPath + ".meta", "fileFormatVersion: 2\nguid: " +
                    "9409bc89925f44d22967b2a8b0334074\nTextScriptImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n");
                AssetDatabase.ImportAsset(IdentityPath, ImportAssetOptions.ForceSynchronousImport);
                Directory.CreateDirectory(output);
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath }, locationPathName = output,
                    target = BuildTarget.WebGL, options = BuildOptions.None
                });
                if (report == null || report.summary.result != BuildResult.Succeeded ||
                    report.summary.platform != BuildTarget.WebGL || report.summary.totalErrors != 0 ||
                    report.summary.totalSize == 0)
                    throw new InvalidOperationException("Actual Unity WebGL BuildReport did not succeed.");
                JObject after = SourceMetadata(root);
                if (sourceHash != after["rendererSourceTreeSha256"].Value<string>() ||
                    source["rendererSourceRevision"].Value<string>() != after["rendererSourceRevision"].Value<string>())
                    throw new InvalidOperationException("Renderer source changed during the actual build; rebuild from a stable source set.");
                var manifest = new BuildManifest
                {
                    BuildId = buildId, DevelopmentBuild = false,
                    RendererSourceRevision = source["rendererSourceRevision"].Value<string>(),
                    RendererSourceTreeSha256 = sourceHash,
                    SourceDirty = source["sourceDirty"].Value<bool>() || after["sourceDirty"].Value<bool>(),
                    BuiltAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Loader = FindArtifact(output, "*.loader.js"),
                    Framework = FindArtifact(output, "*.framework.js"),
                    Wasm = FindArtifact(output, "*.wasm"),
                    Data = FindArtifact(output, "*.data"),
                    StreamingAssetsPath = Directory.Exists(Path.Combine(output, "StreamingAssets")) ? "StreamingAssets" : null
                };
                string[] files = { manifest.Loader.Path, manifest.Framework.Path, manifest.Wasm.Path, manifest.Data.Path };
                if (files.Distinct(StringComparer.Ordinal).Count() != files.Length)
                    throw new InvalidOperationException("Unity artifact purposes must have distinct real files.");
                byte[] magic = new byte[8];
                using (FileStream stream = File.OpenRead(Path.Combine(output, manifest.Wasm.Path)))
                    if (stream.Read(magic, 0, 8) != 8 ||
                        !magic.SequenceEqual(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 }))
                        throw new InvalidOperationException("Actual WebAssembly output has invalid magic/version.");
                VerifySettings();
                string json = SchemaValidator.Load().Write(manifest, "BuildManifest");
                File.WriteAllText(manifestPath, json + "\n", new UTF8Encoding(false));
                Debug.Log("MAHJONG_REAL_WEBGL_BUILD_SUCCEEDED " + buildId + " sourceDirty=" + manifest.SourceDirty);
                exitCode = 0;
            }
            catch (Exception error)
            {
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                Debug.LogError("MAHJONG_WEBGL_BUILD_FAILED: " + error.Message);
            }
            finally
            {
                if (File.Exists(IdentityPath)) AssetDatabase.DeleteAsset(IdentityPath);
                if (File.Exists(IdentityPath + ".meta")) File.Delete(IdentityPath + ".meta");
            }
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        public static void VerifyEnvironment()
        {
            string full = InternalEditorUtility.GetFullUnityVersion();
            if (Application.unityVersion != ContractVersion.Editor ||
                full == null || !full.Contains(ContractVersion.EditorRevision))
                throw new InvalidOperationException("Require actual Unity " + ContractVersion.Editor +
                    " revision " + ContractVersion.EditorRevision + ".");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                throw new InvalidOperationException("The actual editor has no usable Web Build Support module.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                throw new InvalidOperationException("Invoke the editor with -buildTarget WebGL before this method.");
        }

        public static void ApplySettings()
        {
            PlayerSettings.productName = "MahjongRenderer";
            PlayerSettings.companyName = "MahjongRenderer";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.WebGL, "com.mahjong.renderer");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.colorSpace = ColorSpace.Gamma;
            PlayerSettings.runInBackground = false;
            PlayerSettings.stripEngineCode = true;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.threadsSupport = false;
            PlayerSettings.WebGL.wasm2023 = false;
            PlayerSettings.WebGL.dataCaching = false;
            PlayerSettings.WebGL.nameFilesAsHashes = false;
            PlayerSettings.WebGL.template = "PROJECT:MahjongHost";
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithoutStacktrace;
            EditorUserBuildSettings.development = false;
            EditorUserBuildSettings.allowDebugging = false;
            EditorUserBuildSettings.connectProfiler = false;
            var serialized = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            SerializedProperty input = serialized.FindProperty("activeInputHandler");
            if (input == null) throw new InvalidOperationException("Legacy mouse/touch input setting is unavailable.");
            input.intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            VerifySettings();
        }

        public static void VerifySettings()
        {
            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.WebGL) != ScriptingImplementation.IL2CPP ||
                !PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL).SequenceEqual(new[] { GraphicsDeviceType.OpenGLES3 }) ||
                PlayerSettings.WebGL.compressionFormat != WebGLCompressionFormat.Disabled ||
                PlayerSettings.WebGL.decompressionFallback || PlayerSettings.WebGL.threadsSupport ||
                PlayerSettings.WebGL.wasm2023 || PlayerSettings.WebGL.dataCaching ||
                EditorUserBuildSettings.development)
                throw new InvalidOperationException("Renderer settings drifted from IL2CPP/WebGL2/single-threaded/uncompressed.");
        }

        private static void VerifyScene()
        {
            if (!File.Exists(ScenePath)) throw new InvalidOperationException("Connected renderer scene is missing.");
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            MahjongBridge[] bridges = scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<MahjongBridge>(true)).ToArray();
            if (bridges.Length != 1 || bridges[0].gameObject.name != "MahjongBridge")
                throw new InvalidOperationException("Renderer scene must contain exactly one connected MahjongBridge receiver.");
            var bridge = new SerializedObject(bridges[0]);
            if (bridge.FindProperty("surfaceShader")?.objectReferenceValue == null)
                throw new InvalidOperationException("Renderer scene is not connected to the original shader.");
            EditorSceneManager.SaveScene(scene);
        }

        private static JObject SourceMetadata(string root)
        {
            var start = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = Quote(Path.Combine(root, "unity/MahjongRenderer/Tools/source_metadata.py")) + " " + Quote(root),
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Source metadata process could not start.");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("Renderer source metadata failed: " + error);
                return StrictJson.Parse(output) as JObject ??
                       throw new InvalidOperationException("Renderer source metadata is malformed.");
            }
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static ArtifactFile FindArtifact(string output, string pattern)
        {
            string directory = Path.Combine(output, "Build");
            if (!Directory.Exists(directory)) throw new InvalidOperationException("Actual Unity Build directory is missing.");
            string[] files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            if (files.Length != 1) throw new InvalidOperationException("Require exactly one actual Unity artifact: " + pattern);
            string file = files[0];
            RejectLinkedPath(output, file);
            long bytes = new FileInfo(file).Length;
            if (bytes <= 0) throw new InvalidOperationException("Actual Unity artifact is empty: " + pattern);
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new ArtifactFile { Path = "Build/" + Path.GetFileName(file), Bytes = bytes, Sha256 = hash };
        }

        private static void RejectLinkedPath(string root, string target)
        {
            string full = Path.GetFullPath(target);
            string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(boundary, StringComparison.Ordinal)) throw new InvalidOperationException("Output escaped its project-owned directory.");
            for (string path = full; path != Path.GetFullPath(root) && path != null; path = Path.GetDirectoryName(path))
                if ((File.Exists(path) || Directory.Exists(path)) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked renderer output paths are not allowed.");
        }
    }
}
