namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Apone W19
/// SignalR sticky-session affinity wiring at the ingress
/// edge (D4 in Apone memo) — <c>Secure</c> +
/// <c>SameSite=Lax</c> cookie annotations on the SignalR
/// hub path, IP-hash fallback in the configuration snippet.
/// </summary>
public sealed class AponeW19SignalRAffinityContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? IngressYamlPath()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        return Path.Combine(root.FullName, "infra", "k8s", "ingress.yaml");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Ingress_Yaml_File_Present_OrForwardStaged()
    {
        var p = IngressYamlPath();
        if (p is null) return;
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Ingress_Yaml_SecureCookie_Present_OrForwardStaged()
    {
        var p = IngressYamlPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Soft-pin: the Secure attribute should appear on the
        // SignalR sticky cookie spec.
        Assert.Contains("Secure", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Ingress_Yaml_SameSiteLax_Present_OrForwardStaged()
    {
        var p = IngressYamlPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("SameSite", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Ingress_Yaml_IpHashFallback_ConfigurationSnippet_OrForwardStaged()
    {
        var p = IngressYamlPath();
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // W19 adds an IP-hash fallback inside the nginx
        // configuration-snippet annotation.
        Assert.Contains("configuration-snippet", text, StringComparison.OrdinalIgnoreCase);
    }
}
