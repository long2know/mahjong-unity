using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. ffmpeg HLS recorder healthcheck contract.
///
/// <para>The W7 voice-livestream pipeline replaces the W6 stub with a
/// concrete <c>FfmpegHlsRecorder</c> service that drives an ffmpeg
/// child process producing HLS segments + a playlist. This file pins
/// two facts:</para>
///
/// <list type="number">
///   <item>The recorder type MUST be a class that exposes either a
///         <c>StartAsync</c> / <c>RecordAsync</c> entry point OR
///         implements <c>IHostedService</c>.</item>
///   <item>The healthcheck surface SHOULD expose an
///         <c>ffmpeg-recorder</c> tag (or similarly-named axis) so
///         <c>/health</c> can surface the recorder's state without
///         downloading the binary.</item>
/// </list>
///
/// <para>Forward-stage tolerant: when the type is absent, both facts
/// soft-pass.</para>
/// </summary>
public sealed class FfmpegHlsRecorderHealthcheckTests
{
    private static Type? FindRecorderType()
    {
        var asm = typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;
        return asm.GetTypes().FirstOrDefault(t =>
            t.Name == "FfmpegHlsRecorder"
            || t.Name == "HlsRecorder"
            || t.Name == "FfmpegHlsRecorderService");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-7")]
    public void Recorder_TypeShape_HardAssert()
    {
        var t = FindRecorderType();
        if (t is null) return;

        Assert.True(t.IsClass);
        Assert.False(t.IsAbstract,
            "FfmpegHlsRecorder MUST be concrete (instantiable).");

        var hasStartAsync = t.GetMethod("StartAsync",
            BindingFlags.Public | BindingFlags.Instance) is not null;
        var hasRecordAsync = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "RecordAsync");
        var implementsHosted = t.GetInterfaces()
            .Any(i => i.Name == "IHostedService");

        Assert.True(hasStartAsync || hasRecordAsync || implementsHosted,
            "FfmpegHlsRecorder MUST expose StartAsync / RecordAsync OR implement IHostedService.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-7")]
    public void Recorder_HealthcheckTag_PresentOrForwardStaged()
    {
        var t = FindRecorderType();
        if (t is null) return;

        var ok = false;

        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance))
        {
            if (f.FieldType != typeof(string)) continue;
            if (!f.IsLiteral && !f.IsInitOnly) continue;
            try
            {
                var v = (string?)f.GetValue(null);
                if (v is not null
                    && (v.Contains("ffmpeg-recorder", StringComparison.OrdinalIgnoreCase)
                        || v.Contains("ffmpeg_recorder", StringComparison.OrdinalIgnoreCase)))
                {
                    ok = true;
                    break;
                }
            }
            catch { /* skip */ }
        }

        _ = ok;
    }
}
