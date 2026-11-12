using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Mahjong.Autotable.Api.Hosting;

public static class BrowserStaticHosting
{
    public static void UseAutotableFiles(this IApplicationBuilder app, string path,
        IConfiguration configuration, ILogger logger)
    {
        // Retired player paths must never return HTML or accidentally staged engine files.
        app.Use(async (context, next) =>
        {
            var request = context.Request.Path.Value ?? "";
            if (context.Request.Path.StartsWithSegments("/autotable") ||
                context.Request.Path.StartsWithSegments("/unity") ||
                (request.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) ||
                 request.EndsWith(".data", StringComparison.OrdinalIgnoreCase) ||
                 request.EndsWith(".loader.js", StringComparison.OrdinalIgnoreCase) ||
                 request.EndsWith(".framework.js", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                return;
            }
            if (request is "/COPYING" or "/UPSTREAM_SHA")
            {
                var notice = Path.Combine(path, Path.GetFileName(request));
                if (!File.Exists(notice))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = new FileInfo(notice).Length;
                if (!HttpMethods.IsHead(context.Request.Method))
                    await context.Response.SendFileAsync(notice, context.RequestAborted);
                return;
            }
            await next(context);
        });
        if (!Directory.Exists(path))
        {
            logger.LogWarning("Browser assets are missing; build the frontend before starting the game.");
            return;
        }
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".js"] = "application/javascript";
        contentTypes.Mappings[".json"] = "application/json";
        var files = new PhysicalFileProvider(path);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files, ContentTypeProvider = contentTypes
        });
    }
}
