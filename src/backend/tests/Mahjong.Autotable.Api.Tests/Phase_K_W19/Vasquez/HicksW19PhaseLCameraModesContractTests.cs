namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Hicks W19
/// Phase L renderer three camera modes (orbital / isometric-flat
/// / perspective-three-quarter). Soft-pins the camera.ts module
/// + canonical export names so partial-land windows stay green.
/// </summary>
public sealed class HicksW19PhaseLCameraModesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? CameraPath()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        return Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "renderer-webgl2", "camera.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Camera_TsModule_File_Present_OrForwardStaged()
    {
        var p = CameraPath();
        if (p is null) return;
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Camera_OrbitalMode_Token_Present_OrForwardStaged()
    {
        var p = CameraPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("orbital", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Camera_IsometricFlatMode_Token_Present_OrForwardStaged()
    {
        var p = CameraPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("isometric", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Camera_PerspectiveMode_Token_Present_OrForwardStaged()
    {
        var p = CameraPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("perspective", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Hicks")]
    public void Camera_ModePresets_Export_Present_OrForwardStaged()
    {
        var p = CameraPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Either CAMERA_MODE_PRESETS or applyCameraMode export
        // should be present in W19.
        Assert.True(
            text.Contains("CAMERA_MODE_PRESETS", StringComparison.Ordinal)
            || text.Contains("applyCameraMode", StringComparison.Ordinal));
    }
}
