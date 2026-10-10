using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Mahjong.Autotable.Api.Hosting;

public static class UnityStaticHosting
{
    public const string RequestPath = "/autotable/unity";

    public static void UseAutotableFiles(this IApplicationBuilder app, string autotablePath,
        IConfiguration configuration, ILogger logger)
    {
        var incompatibility = CspIncompatibility(configuration);
        if (incompatibility is not null)
        {
            logger.LogWarning("Unity Web renderer is incompatible with the enforcing operator CSP: {Reason}. " +
                "The policy has not been weakened; lobby/API hosting remains available.", incompatibility);
        }

        var unityPath = Path.Combine(autotablePath, "unity");
        if (!File.Exists(Path.Combine(unityPath, "renderer-build.json")))
        {
            logger.LogWarning("Unity Web build unavailable at /autotable/unity/renderer-build.json. " +
                "Supply a verified real Unity build; no legacy renderer is substituted.");
        }

        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".glb"] = "model/gltf-binary";
        contentTypes.Mappings[".gltf"] = "model/gltf+json";
        contentTypes.Mappings[".wasm"] = "application/wasm";
        contentTypes.Mappings[".data"] = "application/octet-stream";
        contentTypes.Mappings[".js"] = "application/javascript";
        contentTypes.Mappings[".json"] = "application/json";

        if (Directory.Exists(unityPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(unityPath),
                RequestPath = RequestPath,
                ContentTypeProvider = contentTypes,
                OnPrepareResponse = context =>
                    context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
            });
        }

        // Never let a missing Unity artifact fall through to any shell/default-file route.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RequestPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                return;
            }
            await next(context);
        });

        if (!Directory.Exists(autotablePath))
        {
            return;
        }
        var files = new PhysicalFileProvider(autotablePath);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/autotable" });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            RequestPath = "/autotable",
            ContentTypeProvider = contentTypes
        });
    }

    internal static string? CspIncompatibility(IConfiguration configuration)
    {
        if (configuration.GetValue<bool>(SecurityHeadersMiddleware.CspReportOnlyConfigKey))
        {
            return null;
        }
        var custom = configuration[SecurityHeadersMiddleware.CspConfigKey];
        var strict = configuration.GetValue<bool>(SecurityHeadersMiddleware.CspStrictConfigKey);
        if (string.IsNullOrWhiteSpace(custom))
        {
            return strict ? "Security:CspStrict removes WebAssembly compilation permission" : null;
        }
        var directives = custom.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(value => value.Length > 0).ToArray();
        var script = directives.FirstOrDefault(value => value[0].Equals("script-src", StringComparison.OrdinalIgnoreCase))
            ?? directives.FirstOrDefault(value => value[0].Equals("default-src", StringComparison.OrdinalIgnoreCase));
        if (script is null || script.Skip(1).Any(value => value is "'wasm-unsafe-eval'" or "'unsafe-eval'"))
        {
            return null;
        }
        return "Security:ContentSecurityPolicy omits WebAssembly compilation permission";
    }
}
