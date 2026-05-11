using System.Security.Claims;
using System.Text.Json;
using ApprovalPO.Options;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Antiforgery;
namespace ApprovalPO.Hosting;

public static class WebPushEndpointExtensions
{
    public static WebApplication MapWebPushEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/web-push");

        group.MapGet("/vapid-public-key", (IConfiguration cfg) =>
        {
            var o = new WebPushOptions();
            cfg.GetSection(WebPushOptions.SectionName).Bind(o);
            if (!o.HasVapidKeys)
                return Results.Json(new { enabled = false, publicKey = (string?)null });
            return Results.Json(new { enabled = true, publicKey = o.PublicKey.Trim() });
        }).AllowAnonymous();

        group.MapPost("/subscribe", SubscribeAsync).RequireAuthorization();
        group.MapPost("/unsubscribe", UnsubscribeAsync).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SubscribeAsync(
        HttpContext ctx,
        IAntiforgery antiforgery,
        WebPushSubscriptionFileStore store,
        IConfiguration cfg)
    {
        var o = new WebPushOptions();
        cfg.GetSection(WebPushOptions.SectionName).Bind(o);
        if (!o.HasVapidKeys)
            return Results.NotFound();

        try
        {
            await antiforgery.ValidateRequestAsync(ctx).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "Invalid or missing antiforgery token." });
        }

        var email = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(email))
            return Results.Unauthorized();

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("endpoint", out var epEl))
            return Results.BadRequest(new { error = "Missing endpoint." });
        var endpoint = epEl.GetString();
        if (string.IsNullOrWhiteSpace(endpoint))
            return Results.BadRequest(new { error = "Missing endpoint." });
        if (!root.TryGetProperty("keys", out var keys) ||
            !keys.TryGetProperty("p256dh", out var p256El) ||
            !keys.TryGetProperty("auth", out var authEl))
            return Results.BadRequest(new { error = "Missing keys." });
        var p256 = p256El.GetString();
        var auth = authEl.GetString();
        if (string.IsNullOrWhiteSpace(p256) || string.IsNullOrWhiteSpace(auth))
            return Results.BadRequest(new { error = "Missing key material." });

        await store.UpsertAsync(
            new WebPushSubscriptionRecord
            {
                UserEmail = email.Trim(),
                Endpoint = endpoint,
                P256dh = p256,
                Auth = auth,
            },
            ctx.RequestAborted).ConfigureAwait(false);

        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> UnsubscribeAsync(
        HttpContext ctx,
        IAntiforgery antiforgery,
        WebPushSubscriptionFileStore store,
        IConfiguration cfg)
    {
        var o = new WebPushOptions();
        cfg.GetSection(WebPushOptions.SectionName).Bind(o);
        if (!o.HasVapidKeys)
            return Results.NotFound();

        try
        {
            await antiforgery.ValidateRequestAsync(ctx).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "Invalid or missing antiforgery token." });
        }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("endpoint", out var epEl))
            return Results.BadRequest(new { error = "Missing endpoint." });
        var endpoint = epEl.GetString();
        if (string.IsNullOrWhiteSpace(endpoint))
            return Results.BadRequest(new { error = "Missing endpoint." });

        await store.RemoveByEndpointAsync(endpoint, ctx.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new { ok = true });
    }
}
