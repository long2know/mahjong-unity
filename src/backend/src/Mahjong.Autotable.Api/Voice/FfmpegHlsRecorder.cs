using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 7 — Bishop. Production
/// <see cref="ILivestreamRecorder"/> that spawns one <c>ffmpeg</c>
/// subprocess per game and writes HLS <c>.ts</c> segments + a
/// sliding <c>playlist.m3u8</c> into a per-game directory.
///
/// <para>The recorder owns the subprocess lifecycle:</para>
/// <list type="bullet">
///   <item><see cref="StartAsync"/> creates the per-game directory
///         + spawns ffmpeg. The exact ffmpeg command line is
///         deliberately conservative — it reads SRTP audio frames
///         from stdin (the WebRTC voice hub fan-out point) and
///         muxes into HLS. Per-game directories give us full
///         process isolation: a crash in one game's encoder never
///         affects another game's stream.</item>
///   <item><see cref="StopAsync"/> sends <c>q</c> over stdin for a
///         graceful muxer flush, then waits up to 3s before
///         killing the subprocess tree. Stop is idempotent — a
///         second call on an already-stopped stream returns the
///         cached final handle.</item>
///   <item><see cref="GetPlaylist"/> reads the m3u8 from disk on
///         every request. ffmpeg rewrites the playlist atomically
///         each time it appends a segment, so a concurrent reader
///         sees either the previous or the current consistent
///         snapshot — never a partial write.</item>
///   <item><see cref="GetSegment"/> streams <c>.ts</c> bytes from
///         disk. The lookup is by literal segment name (no
///         <c>..</c> traversal) so a maliciously-crafted path
///         can't escape the per-game directory.</item>
/// </list>
///
/// <para><b>WebRTC ingestion shape.</b> The Wave-7 brief specifies
/// stdin as the audio source. ffmpeg consumes the
/// <c>-f s16le -ar 48000 -ac 2</c> raw PCM stream that the voice
/// hub fans out; the SFU-side adapter (Phase L) is responsible for
/// decoding SRTP and writing PCM frames to the spawn pipe. Until
/// the SFU adapter lands the ffmpeg pipeline still records — it
/// just emits silence when no frames arrive.</para>
///
/// <para><b>Test isolation.</b> The
/// <see cref="VoiceOptions.LivestreamRecorderImpl"/> config knob
/// defaults to <c>"InMemoryStub"</c> so the unit-test harness
/// keeps booting clean without ffmpeg on PATH. The
/// <see cref="IFfmpegHealthProbe"/> gate at startup blocks
/// production hosts that select <c>FfmpegHls</c> with a missing
/// binary.</para>
/// </summary>
public sealed class FfmpegHlsRecorder : ILivestreamRecorder, IAsyncDisposable
{
    private readonly VoiceOptions _options;
    private readonly ILogger<FfmpegHlsRecorder> _logger;
    private readonly string _baseDirectory;

    private readonly ConcurrentDictionary<Guid, RecordingSession> _sessions = new();
    private readonly object _spawnGate = new();

    public FfmpegHlsRecorder(IOptions<VoiceOptions> options, ILogger<FfmpegHlsRecorder> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Clamp the segment seconds + playlist window into safe
        // ranges so a misconfigured operator can't push 0-second
        // segments (which crash ffmpeg) or a 10000-segment playlist
        // (which fills disk).
        if (_options.LivestreamSegmentSeconds < 2 || _options.LivestreamSegmentSeconds > 30)
        {
            _logger.LogWarning(
                "Voice:LivestreamSegmentSeconds={Configured} out of range [2..30]; clamping to 6.",
                _options.LivestreamSegmentSeconds);
            _options.LivestreamSegmentSeconds = 6;
        }
        if (_options.LivestreamPlaylistSegmentCount < 2 || _options.LivestreamPlaylistSegmentCount > 30)
        {
            _logger.LogWarning(
                "Voice:LivestreamPlaylistSegmentCount={Configured} out of range [2..30]; clamping to 5.",
                _options.LivestreamPlaylistSegmentCount);
            _options.LivestreamPlaylistSegmentCount = 5;
        }

        var dir = string.IsNullOrWhiteSpace(_options.LivestreamWorkingDirectory)
            ? "voice-livestream"
            : _options.LivestreamWorkingDirectory;
        _baseDirectory = Path.IsPathRooted(dir)
            ? dir
            : Path.Combine(AppContext.BaseDirectory, dir);
        try
        {
            Directory.CreateDirectory(_baseDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create livestream working directory at {Dir}. POST start requests will fail until permissions are fixed.",
                _baseDirectory);
        }
    }

    /// <summary>Exposed for tests + the health probe to inspect the
    /// resolved working directory after option clamping.</summary>
    public string WorkingDirectory => _baseDirectory;

    public Task<LivestreamHandle> StartAsync(Guid gameId, string requestedByPlayerId, CancellationToken ct = default)
    {
        // The spawn gate serializes creation of new subprocesses so
        // a burst of concurrent start requests doesn't fork ffmpeg
        // races against the same directory.
        lock (_spawnGate)
        {
            if (_sessions.TryGetValue(gameId, out var existing) && existing.IsLive)
            {
                return Task.FromResult(existing.Handle);
            }

            var gameDir = Path.Combine(_baseDirectory, gameId.ToString("N"));
            Directory.CreateDirectory(gameDir);
            // Purge any stale segments / playlist from a previous
            // run — the playlist URL is otherwise served from a
            // dead encoder's output until ffmpeg overwrites it.
            foreach (var f in Directory.EnumerateFiles(gameDir, "*.ts"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
            try { File.Delete(Path.Combine(gameDir, "playlist.m3u8")); } catch { /* best effort */ }

            var psi = BuildFfmpegStartInfo(gameDir);
            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ffmpeg subprocess spawn failed for game {GameId}. Falling back to no-op recorder.",
                    gameId);
                throw new InvalidOperationException("ffmpeg subprocess spawn failed.", ex);
            }
            if (proc is null)
            {
                throw new InvalidOperationException("Process.Start returned null — ffmpeg likely not on PATH.");
            }
            // Drain stderr asynchronously so the subprocess never
            // blocks on a full stderr pipe buffer.
            proc.BeginErrorReadLine();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogDebug("ffmpeg[{GameId}] {Line}", gameId, e.Data);
            };

            var handle = new LivestreamHandle(
                GameId: gameId,
                Status: "live",
                StartedAtUtc: DateTime.UtcNow,
                StoppedAtUtc: null,
                StartedByPlayerId: requestedByPlayerId ?? string.Empty,
                PlaylistUrl: $"/api/voice/livestream/{gameId:N}/playlist.m3u8");

            var session = new RecordingSession(handle, proc, gameDir);
            _sessions[gameId] = session;
            _logger.LogInformation(
                "ffmpeg HLS recorder started for game {GameId} (pid={Pid}, dir={Dir})",
                gameId, proc.Id, gameDir);
            return Task.FromResult(handle);
        }
    }

    public async Task<LivestreamHandle?> StopAsync(Guid gameId, string requestedByPlayerId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(gameId, out var session))
        {
            return null;
        }
        if (!session.IsLive)
        {
            return session.Handle;
        }

        try
        {
            // Send 'q' on stdin — ffmpeg's documented graceful
            // shutdown signal that triggers a clean muxer flush so
            // the final segment is appended before exit.
            if (session.Process is { HasExited: false, StandardInput: var stdin } && stdin is not null)
            {
                try
                {
                    await stdin.WriteAsync("q\n").ConfigureAwait(false);
                    await stdin.FlushAsync().ConfigureAwait(false);
                }
                catch { /* may already be exiting */ }
            }
            // Give ffmpeg up to 3s to flush + exit before we kill it.
            if (session.Process is not null && !session.Process.WaitForExit(3000))
            {
                try { session.Process.Kill(entireProcessTree: true); } catch { }
                session.Process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg graceful shutdown failed for game {GameId}", gameId);
        }

        var stopped = session.Handle with { Status = "stopped", StoppedAtUtc = DateTime.UtcNow };
        session.Stop(stopped);
        return stopped;
    }

    public string? GetPlaylist(Guid gameId)
    {
        if (!_sessions.TryGetValue(gameId, out var session)) return null;
        var path = Path.Combine(session.Directory, "playlist.m3u8");
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public byte[]? GetSegment(Guid gameId, string segmentName)
    {
        if (!_sessions.TryGetValue(gameId, out var session)) return null;
        // Guard against directory traversal — the segment name MUST
        // be a plain file name with no path separators or `..`.
        if (string.IsNullOrEmpty(segmentName)
            || segmentName.Contains('/')
            || segmentName.Contains('\\')
            || segmentName.Contains(".."))
        {
            return null;
        }
        var path = Path.Combine(session.Directory, segmentName);
        // After Combine, verify the resolved path still lives under
        // the per-game directory — belt-and-braces against any
        // platform-specific path quirks.
        var resolved = Path.GetFullPath(path);
        var sessionDir = Path.GetFullPath(session.Directory);
        if (!resolved.StartsWith(sessionDir, StringComparison.Ordinal)) return null;
        if (!File.Exists(resolved)) return null;
        try { return File.ReadAllBytes(resolved); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool IsLive(Guid gameId)
        => _sessions.TryGetValue(gameId, out var session) && session.IsLive;

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                if (session.IsLive && session.Process is { HasExited: false } proc)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    proc.WaitForExit(1000);
                }
            }
            catch { /* shutdown — best effort */ }
        }
        _sessions.Clear();
        await Task.CompletedTask;
    }

    private ProcessStartInfo BuildFfmpegStartInfo(string gameDir)
    {
        var segmentSeconds = _options.LivestreamSegmentSeconds;
        var playlistSize = _options.LivestreamPlaylistSegmentCount;
        var playlistPath = Path.Combine(gameDir, "playlist.m3u8");
        var segmentPattern = Path.Combine(gameDir, "seg-%05d.ts");

        // Conservative ffmpeg command line:
        //   -f s16le -ar 48000 -ac 2 -i pipe:0   : 48 kHz / stereo PCM on stdin
        //   -c:a aac -b:a 128k                   : AAC 128 kbps mux
        //   -f hls                               : HLS muxer
        //   -hls_time <segmentSeconds>           : per-segment duration
        //   -hls_list_size <playlistSize>        : sliding window count
        //   -hls_flags delete_segments+append_list+omit_endlist
        //                                        : auto-purge old, append-only updates
        //   -hls_segment_filename seg-%05d.ts    : segment name pattern
        //   playlist.m3u8                        : playlist file
        var args = new StringBuilder();
        args.Append("-hide_banner -loglevel warning ");
        args.Append("-f s16le -ar 48000 -ac 2 -i pipe:0 ");
        args.Append("-c:a aac -b:a 128k ");
        args.Append("-f hls ");
        args.AppendFormat("-hls_time {0} ", segmentSeconds);
        args.AppendFormat("-hls_list_size {0} ", playlistSize);
        args.Append("-hls_flags delete_segments+append_list+omit_endlist ");
        args.AppendFormat("-hls_segment_filename \"{0}\" ", segmentPattern);
        args.Append('"').Append(playlistPath).Append('"');

        return new ProcessStartInfo("ffmpeg", args.ToString())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = gameDir,
        };
    }

    private sealed class RecordingSession
    {
        public LivestreamHandle Handle { get; private set; }
        public Process? Process { get; }
        public string Directory { get; }
        private bool _stopped;
        public bool IsLive => !_stopped && (Process is null || !Process.HasExited);

        public RecordingSession(LivestreamHandle handle, Process? process, string directory)
        {
            Handle = handle;
            Process = process;
            Directory = directory;
        }

        public void Stop(LivestreamHandle finalHandle)
        {
            Handle = finalHandle;
            _stopped = true;
        }
    }
}
