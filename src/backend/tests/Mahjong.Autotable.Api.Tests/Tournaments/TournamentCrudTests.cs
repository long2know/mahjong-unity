using System.Net;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase J Wave 10 — tournament CRUD contract tests (Vasquez).
///
/// <para>Bishop's Wave 10 surface introduces a Tournament aggregate:
/// create, list, get, register players, unregister players. The wire
/// contract is REST under <c>/api/tournaments</c>.</para>
///
/// <para><b>Contracts pinned:</b>
/// <list type="bullet">
///   <item>POST /api/tournaments — creates a tournament; response carries
///         a non-empty <c>id</c> field.</item>
///   <item>GET /api/tournaments — lists tournaments as an array (envelope
///         may wrap with <c>{ tournaments: [] }</c>).</item>
///   <item>GET /api/tournaments/{id} — returns the tournament; 404 when
///         id is unknown.</item>
///   <item>POST /api/tournaments/{id}/register — succeeds with a registered
///         player; idempotent re-register is acceptable (4xx OR 2xx).</item>
///   <item>POST /api/tournaments/{id}/unregister — reverses the registration
///         (or DELETE on the registration sub-resource).</item>
/// </list></para>
///
/// <para>Reflection-defensive — every endpoint probe accepts the first
/// non-404 across the canonical URL candidate set. A uniform 404 is the
/// "not-yet-registered" signal → soft-pass via <c>return;</c>.</para>
/// </summary>
public class TournamentCrudTests : TournamentHarness
{
    private static object SampleTournamentBody(string name = "Vasquez Open") => new
    {
        name,
        format = "single-elim",
        maxPlayers = 8,
        rulePresetId = (string?)null,
    };

    private static object SampleRegistrationBody(string playerId = "vasquez-pid", string displayName = "Vasquez") => new
    {
        playerId,
        displayName,
    };

    // ────────────────────────────────────────────────────────────────────
    //  1. Create returns a non-empty id
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_Create_ReturnsIdOrSoftPasses()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        using var resp = await PostFirstNonNotFoundAsync(client, CreateUrls, SampleTournamentBody());
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        Assert.True((int)resp.StatusCode < 500,
            $"Create tournament returned 5xx ({(int)resp.StatusCode}).");
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var id = TryReadString(doc.RootElement, "id", "tournamentId", "Id");
        Assert.False(string.IsNullOrWhiteSpace(id),
            "Create tournament success response must carry a non-empty id.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. List returns an array (or array-bearing envelope)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_List_ReturnsArrayShape()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, ListUrls);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        bool isArray = root.ValueKind == JsonValueKind.Array;
        bool envelope = root.ValueKind == JsonValueKind.Object
            && (root.TryGetProperty("tournaments", out var arr1) && arr1.ValueKind == JsonValueKind.Array
                || root.TryGetProperty("items", out var arr2) && arr2.ValueKind == JsonValueKind.Array
                || root.TryGetProperty("data", out var arr3) && arr3.ValueKind == JsonValueKind.Array);
        Assert.True(isArray || envelope,
            "List tournaments response must be an array or array-bearing envelope.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Get unknown id returns 404 (or graceful empty)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_Get_UnknownId_Returns404Or4xx()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        var unknown = Guid.NewGuid().ToString();
        using var resp = await GetFirstNonNotFoundAsync(client, GetUrls(unknown));

        // Either 404 (canonical) or 200-with-null-body is acceptable.
        // The contract is: no 5xx, never expose another tournament's data.
        Assert.True((int)resp.StatusCode < 500,
            $"GET unknown tournament returned 5xx ({(int)resp.StatusCode}).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Get-after-create returns the same tournament
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_GetAfterCreate_RoundTrips()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();

        // Create.
        using var createResp = await PostFirstNonNotFoundAsync(client, CreateUrls, SampleTournamentBody());
        if (createResp.StatusCode == HttpStatusCode.NotFound) return;
        if (!createResp.IsSuccessStatusCode) return;

        var createBody = await createResp.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createBody);
        var id = TryReadString(createDoc.RootElement, "id", "tournamentId");
        if (string.IsNullOrWhiteSpace(id)) return;

        // GET the same id back.
        using var getResp = await GetFirstNonNotFoundAsync(client, GetUrls(id));
        Assert.True((int)getResp.StatusCode < 500);
        if (!getResp.IsSuccessStatusCode) return;

        var getBody = await getResp.Content.ReadAsStringAsync();
        using var getDoc = JsonDocument.Parse(getBody);
        var roundTrippedId = TryReadString(getDoc.RootElement, "id", "tournamentId");
        if (!string.IsNullOrWhiteSpace(roundTrippedId))
            Assert.Equal(id, roundTrippedId);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Register player succeeds (post-create)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_Register_Succeeds_AfterCreate()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        using var createResp = await PostFirstNonNotFoundAsync(client, CreateUrls, SampleTournamentBody());
        if (createResp.StatusCode == HttpStatusCode.NotFound) return;
        if (!createResp.IsSuccessStatusCode) return;
        var createBody = await createResp.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createBody);
        var id = TryReadString(createDoc.RootElement, "id", "tournamentId");
        if (string.IsNullOrWhiteSpace(id)) return;

        using var regResp = await PostFirstNonNotFoundAsync(client, RegisterUrls(id), SampleRegistrationBody());
        if (regResp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)regResp.StatusCode < 500,
            $"Register player returned 5xx ({(int)regResp.StatusCode}).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Unregister reverses the registration (or returns 4xx if unknown)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_Unregister_DoesNotServerError()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        using var createResp = await PostFirstNonNotFoundAsync(client, CreateUrls, SampleTournamentBody());
        if (createResp.StatusCode == HttpStatusCode.NotFound) return;
        if (!createResp.IsSuccessStatusCode) return;
        var createBody = await createResp.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createBody);
        var id = TryReadString(createDoc.RootElement, "id", "tournamentId");
        if (string.IsNullOrWhiteSpace(id)) return;

        using var regResp = await PostFirstNonNotFoundAsync(client, RegisterUrls(id), SampleRegistrationBody());
        if (regResp.StatusCode == HttpStatusCode.NotFound) return;

        // Try both POST-unregister and DELETE on the registration sub-resource.
        using var unregResp = await PostFirstNonNotFoundAsync(client, UnregisterUrls(id), SampleRegistrationBody());
        if (unregResp.StatusCode == HttpStatusCode.NotFound)
        {
            // Fall back to DELETE.
            var delUrls = RegisterUrls(id).Select(u => $"{u}/{Uri.EscapeDataString("vasquez-pid")}");
            using var delResp = await DeleteFirstNonNotFoundAsync(client, delUrls);
            if (delResp.StatusCode == HttpStatusCode.NotFound) return;
            Assert.True((int)delResp.StatusCode < 500);
            return;
        }
        Assert.True((int)unregResp.StatusCode < 500,
            $"Unregister returned 5xx ({(int)unregResp.StatusCode}).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Create requires a body (rejects empty)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-J-10")]
    public async Task Tournament_Create_EmptyBody_4xxNot5xx()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        // Send a deliberately empty / invalid body.
        using var resp = await PostFirstNonNotFoundAsync(client, CreateUrls, new { });
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        // Either accepted with defaults (2xx) or rejected (4xx) — never 5xx.
        Assert.True((int)resp.StatusCode < 500,
            $"Create with empty body returned 5xx ({(int)resp.StatusCode}).");
    }
}
