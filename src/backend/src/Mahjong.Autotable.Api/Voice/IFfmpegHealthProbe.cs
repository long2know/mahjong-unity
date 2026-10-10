using System.Diagnostics;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 7 — Bishop. Health probe for the ffmpeg binary the
/// <see cref="FfmpegHlsRecorder"/> shells out to. Wired as a startup
/// gate so the host fails fast when
/// <see cref="VoiceOptions.LivestreamRecorderImpl"/> is set to
/// <c>"FfmpegHls"</c> but ffmpeg is missing from <c>PATH</c> — better
/// to crash at boot than to surface 500s at the
/// <c>POST /api/voice/livestream/{gameId}/start</c> endpoint.
///
/// <para>The probe is intentionally lightweight: it shells
/// <c>ffmpeg -version</c> with a 2s timeout and reports whether the
/// process launched + exited 0. We don't parse the version string —
/// any ffmpeg 4.x+ supports the HLS muxer we depend on.</para>
/// </summary>
public interface IFfmpegHealthProbe
{
    /// <summary>True when <c>ffmpeg</c> is resolvable on <c>PATH</c>
    /// and reports a clean <c>-version</c> exit. False on every
    /// failure mode (binary missing, non-zero exit, timeout, permission
    /// denied). The probe never throws.</summary>
    bool IsAvailable();
}

/// <summary>
/// Phase K Wave 7 — Bishop. Default <see cref="IFfmpegHealthProbe"/>
/// that shells <c>ffmpeg -version</c>. Singleton — the result is
/// cached after the first call so a slow startup doesn't re-probe
/// per-request when downstream services check availability.
/// </summary>
public sealed class FfmpegBinaryHealthProbe : IFfmpegHealthProbe
{
    private readonly ILogger<FfmpegBinaryHealthProbe>? _logger;
    private bool? _cached;
    private readonly object _gate = new();

    public FfmpegBinaryHealthProbe(ILogger<FfmpegBinaryHealthProbe>? logger = null)
    {
        _logger = logger;
    }

    public bool IsAvailable()
    {
        lock (_gate)
        {
            if (_cached.HasValue) return _cached.Value;
            _cached = ProbeOnce();
            return _cached.Value;
        }
    }

    private bool ProbeOnce()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            // 2s is generous for a -version probe; ffmpeg typically
            // returns within ~50ms. The timeout protects against a
            // hung subprocess preventing the host from booting.
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                _logger?.LogWarning("ffmpeg -version probe timed out after 2s — treating as unavailable.");
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ffmpeg -version probe failed — treating as unavailable.");
            return false;
        }
    }
}
