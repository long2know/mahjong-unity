using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Rules;

/// <summary>
/// Phase J Wave 8 — REST surface for <see cref="ChangshaRulePreset"/>:
/// <list type="bullet">
///   <item><c>GET    /api/rule-presets</c>           — list all presets.</item>
///   <item><c>GET    /api/rule-presets/{id}</c>      — fetch one.</item>
///   <item><c>POST   /api/rule-presets</c>           — create (must be authenticated).</item>
///   <item><c>PUT    /api/rule-presets/{id}</c>      — update (creator only).</item>
///   <item><c>DELETE /api/rule-presets/{id}</c>      — delete (creator only; "Classic Changsha" cannot be deleted).</item>
/// </list>
///
/// <para>Auth is gated via <see cref="AuthCookieService"/> — a missing /
/// expired auth session returns <c>401</c>. The "Classic Changsha" preset
/// is the canonical default seeded by <c>DatabaseBootstrapper</c>; even
/// the creator (<c>system</c>) cannot delete it because it backs the
/// hub's fallback rule resolution.</para>
/// </summary>
[ApiController]
[Route("api/rule-presets")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class RulePresetController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthCookieService _cookies;

    public RulePresetController(IServiceScopeFactory scopeFactory, AuthCookieService cookies)
    {
        _scopeFactory = scopeFactory;
        _cookies = cookies;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ChangshaRulePresets.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        return Ok(new { presets = rows.Select(ToDto).ToArray() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preset = await db.ChangshaRulePresets.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (preset is null) return NotFound(new { error = "Preset not found.", id });
        return Ok(ToDto(preset));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RulePresetBody body, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required to create rule presets." });
        if (body is null) return BadRequest(new { error = "Body is required." });
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = "name is required." });

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var nameTrimmed = body.Name.Trim();
        if (await db.ChangshaRulePresets.AnyAsync(p => p.Name == nameTrimmed, ct))
            return Conflict(new { error = "A preset with that name already exists.", name = nameTrimmed });

        var preset = new ChangshaRulePreset
        {
            Id = Guid.NewGuid(),
            Name = nameTrimmed,
            Description = body.Description?.Trim() ?? string.Empty,
            HandLimit = body.HandLimit ?? 4,
            MaxScorePerHand = body.MaxScorePerHand ?? 0,
            AllowWashout = body.AllowWashout ?? true,
            AllowKongRobbing = body.AllowKongRobbing ?? true,
            AllowConcealedKongPromotion = body.AllowConcealedKongPromotion ?? true,
            AllowSevenPairs = body.AllowSevenPairs ?? true,
            AllowChow = body.AllowChow ?? true,
            BotDecisionTimeoutMs = body.BotDecisionTimeoutMs ?? 2000,
            CreatorPlayerId = session.PlayerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        try { ValidatePreset(preset); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        db.ChangshaRulePresets.Add(preset);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = preset.Id }, ToDto(preset));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] RulePresetBody body, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required." });
        if (body is null) return BadRequest(new { error = "Body is required." });

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preset = await db.ChangshaRulePresets.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (preset is null) return NotFound(new { error = "Preset not found.", id });
        if (!string.Equals(preset.CreatorPlayerId, session.PlayerId, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only the preset's creator may update it." });

        if (!string.IsNullOrWhiteSpace(body.Name))
        {
            var newName = body.Name.Trim();
            if (newName != preset.Name && await db.ChangshaRulePresets.AnyAsync(p => p.Name == newName && p.Id != id, ct))
                return Conflict(new { error = "A preset with that name already exists.", name = newName });
            preset.Name = newName;
        }
        if (body.Description is not null) preset.Description = body.Description.Trim();
        if (body.HandLimit.HasValue) preset.HandLimit = body.HandLimit.Value;
        if (body.MaxScorePerHand.HasValue) preset.MaxScorePerHand = body.MaxScorePerHand.Value;
        if (body.AllowWashout.HasValue) preset.AllowWashout = body.AllowWashout.Value;
        if (body.AllowKongRobbing.HasValue) preset.AllowKongRobbing = body.AllowKongRobbing.Value;
        if (body.AllowConcealedKongPromotion.HasValue) preset.AllowConcealedKongPromotion = body.AllowConcealedKongPromotion.Value;
        if (body.AllowSevenPairs.HasValue) preset.AllowSevenPairs = body.AllowSevenPairs.Value;
        if (body.AllowChow.HasValue) preset.AllowChow = body.AllowChow.Value;
        if (body.BotDecisionTimeoutMs.HasValue) preset.BotDecisionTimeoutMs = body.BotDecisionTimeoutMs.Value;
        preset.UpdatedAt = DateTime.UtcNow;
        try { ValidatePreset(preset); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(preset));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required." });
        if (id == Guid.Parse(ChangshaRulePreset.ClassicPresetId))
            return BadRequest(new { error = "The 'Classic Changsha' preset cannot be deleted." });

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preset = await db.ChangshaRulePresets.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (preset is null) return NotFound(new { error = "Preset not found.", id });
        if (!string.Equals(preset.CreatorPlayerId, session.PlayerId, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only the preset's creator may delete it." });

        db.ChangshaRulePresets.Remove(preset);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void ValidatePreset(ChangshaRulePreset preset)
    {
        if (preset.Name.Length is < 1 or > 64)
            throw new ArgumentException("Name must be 1..64 chars.");
        if (preset.Description.Length > 512)
            throw new ArgumentException("Description must be ≤ 512 chars.");
        if (preset.HandLimit is < 1 or > 64)
            throw new ArgumentException("HandLimit must be in [1, 64].");
        if (preset.MaxScorePerHand < 0)
            throw new ArgumentException("MaxScorePerHand must be ≥ 0.");
        if (preset.BotDecisionTimeoutMs is < 0 or > 60_000)
            throw new ArgumentException("BotDecisionTimeoutMs must be in [0, 60000].");
    }

    internal static object ToDto(ChangshaRulePreset p) => new
    {
        id = p.Id,
        name = p.Name,
        description = p.Description,
        handLimit = p.HandLimit,
        maxScorePerHand = p.MaxScorePerHand,
        allowWashout = p.AllowWashout,
        allowKongRobbing = p.AllowKongRobbing,
        allowConcealedKongPromotion = p.AllowConcealedKongPromotion,
        allowSevenPairs = p.AllowSevenPairs,
        allowChow = p.AllowChow,
        botDecisionTimeoutMs = p.BotDecisionTimeoutMs,
        creatorPlayerId = p.CreatorPlayerId,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    public sealed class RulePresetBody
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int? HandLimit { get; set; }
        public int? MaxScorePerHand { get; set; }
        public bool? AllowWashout { get; set; }
        public bool? AllowKongRobbing { get; set; }
        public bool? AllowConcealedKongPromotion { get; set; }
        public bool? AllowSevenPairs { get; set; }
        public bool? AllowChow { get; set; }
        public int? BotDecisionTimeoutMs { get; set; }
    }
}
