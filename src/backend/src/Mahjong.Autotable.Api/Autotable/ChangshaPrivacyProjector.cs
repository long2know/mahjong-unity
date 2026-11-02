namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// SC-2 / G19 (Ripley, BINDING) — per-viewer opaque handle <b>projection / entitlement policy</b>
/// for tiles HIDDEN from a viewer (foreign concealed hands, ALL wall tiles, foreign concealed
/// kongs). This type owns only the <i>policy</i>: which tiles are hidden vs. visible, the stable
/// per-viewer scoping tuple, and the <c>h_</c> wire prefix. The actual cryptographic handle
/// derivation is <b>delegated</b> to <see cref="OpaqueTileHandleProvider"/> (HKDF-Extract/Expand,
/// domain-separated, length-prefixed, full-256-bit base64url) — this class holds no raw HMAC and
/// no key material of its own.
///
/// <para>Handle semantics (unchanged contract):</para>
/// <list type="bullet">
///   <item>is NOT the tileId and reveals no rank/suit (<c>key//4</c> yields nothing);</item>
///   <item>is <b>server-secret keyed</b> — the 108-tile space is tiny, so any client-replicable
///   derivation is brute-forceable; the provider's HKDF key (never on the wire) is the whole point;</item>
///   <item>is <b>stable</b> per (gameId, durable playerId, tileId) => reconnect-consistent for the
///   same authenticated identity (the SAME id used for BE-5 seat reclaim + #153 reconnect);</item>
///   <item>is <b>cross-player unlinkable</b> — the playerId is inside the derivation, so the same
///   physical tile gets a different handle per player and no peer can reproduce another's handles.</item>
/// </list>
/// Visible tiles (own face-up hand, discards, exposed melds, own concealed kong, hand-end scoring
/// reveal) keep the real numeric tileId so the client resolves face via <c>tileId/4</c>.
/// </summary>
public sealed class ChangshaPrivacyProjector
{
    /// <summary>Wire prefix marking an opaque hidden-tile handle (never parseable as a 0..107 id).</summary>
    private const string HandlePrefix = "h_";

    private readonly OpaqueTileHandleProvider _provider;
    private readonly string _gameId;
    private readonly string _viewerPlayerId;
    private readonly Dictionary<int, string> _cache = new(108);

    private ChangshaPrivacyProjector(OpaqueTileHandleProvider provider, string gameId, string viewerPlayerId)
    {
        _provider = provider;
        _gameId = gameId;
        _viewerPlayerId = viewerPlayerId;
    }

    /// <summary>
    /// Builds a projector from raw server-secret input key material, or <c>null</c> ONLY when privacy
    /// is disabled / the secret is missing / the secret is below the provider's minimum (=> the
    /// translator emits real tileIds, preserving pre-SC-2 behaviour). The secret is used strictly as
    /// HKDF IKM by <see cref="OpaqueTileHandleProvider"/> — never as a raw MAC key here.
    ///
    /// <para><b>SC-2 fail-closed on viewer identity (A, BINDING):</b> a missing/empty
    /// <paramref name="viewerPlayerId"/> must NEVER drop back to real tileIds. When privacy is
    /// available (valid secret + gameId) but the viewer is unidentified (raw/cookie-less WS, a
    /// spectator, a not-yet-reclaimed reconnect) we <b>mint a fresh ephemeral viewer scope</b> so
    /// hidden tiles stay opaque — <b>mint-or-opaque, never null→real</b>. The minted scope's handles
    /// are unlinkable and non-real; they are simply not reconnect-stable for an unidentified viewer.</para>
    /// </summary>
    public static ChangshaPrivacyProjector? Create(byte[]? secret, string? gameId, string? viewerPlayerId)
    {
        if (secret is null || secret.Length == 0) return null;
        if (string.IsNullOrEmpty(gameId)) return null;
        OpaqueTileHandleProvider provider;
        try
        {
            provider = new OpaqueTileHandleProvider(secret);
        }
        catch (ArgumentException)
        {
            // Sub-minimum IKM => privacy unavailable rather than a weak-key fallback.
            return null;
        }
        return new ChangshaPrivacyProjector(provider, gameId, EnsureViewerScope(viewerPlayerId));
    }

    /// <summary>
    /// Builds a projector around a <b>shared</b> <see cref="OpaqueTileHandleProvider"/> (the wiring
    /// path — one provider instance per process, scoped per (game, viewer) here), or <c>null</c>
    /// ONLY when the provider is absent (privacy disabled) or the gameId is missing.
    ///
    /// <para><b>SC-2 fail-closed on viewer identity (A, BINDING):</b> as with the secret overload, an
    /// empty/missing <paramref name="viewerPlayerId"/> mints a fresh ephemeral viewer scope rather
    /// than returning <c>null</c> — hidden tiles never fall back to real ids (mint-or-opaque).</para>
    /// </summary>
    public static ChangshaPrivacyProjector? Create(OpaqueTileHandleProvider? provider, string? gameId, string? viewerPlayerId)
    {
        if (provider is null) return null;
        if (string.IsNullOrEmpty(gameId)) return null;
        return new ChangshaPrivacyProjector(provider, gameId, EnsureViewerScope(viewerPlayerId));
    }

    /// <summary>
    /// SC-2 fail-closed on viewer identity: returns <paramref name="viewerPlayerId"/> when present,
    /// else a fresh ephemeral scope so an unidentified viewer still receives opaque (non-real,
    /// unlinkable) handles for hidden tiles. Never returns null/empty — the caller has already
    /// confirmed privacy is available, so the only choice here is <b>mint-or-opaque, never null→real</b>.
    /// </summary>
    private static string EnsureViewerScope(string? viewerPlayerId)
        => string.IsNullOrEmpty(viewerPlayerId)
            ? "anon-" + Guid.NewGuid().ToString("N")
            : viewerPlayerId;

    /// <summary>Opaque, stable, per-viewer handle string for a hidden <paramref name="tileId"/>.</summary>
    public string Handle(int tileId)
    {
        if (_cache.TryGetValue(tileId, out var cached)) return cached;
        var handle = HandlePrefix + _provider.DeriveHandle(_viewerPlayerId, _gameId, tileId);
        _cache[tileId] = handle;
        return handle;
    }

    /// <summary>Projects the wire KEY for a tile: real numeric id when visible, opaque handle when hidden.</summary>
    public object Key(int tileId, bool hidden) => hidden ? Handle(tileId) : tileId;
}
