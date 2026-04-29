using System.Globalization;
using EmailPlatform.Shared;
using EmailPlatform.Shared.Contracts;
using EmailPlatform.Storage.Data;

namespace EmailPlatform.Storage.Endpoints;

/// <summary>
/// Maps every HTTP endpoint exposed by the Storage Service. Grouping them
/// in one static class keeps Program.cs short and makes the API surface
/// easy to review at a glance.
///
/// All endpoints are under /internal/v1/announcements — the /internal prefix
/// signals this service is NOT meant to be exposed to end users. In AWS we
/// put it on an internal load balancer with no public DNS.
/// </summary>
public static class AnnouncementEndpoints
{
    public static IEndpointRouteBuilder MapAnnouncementEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/internal/v1/announcements")
                          .WithTags("Announcements");

        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapGet("/", ListOrQueryAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapPatch("/{id}/status", UpdateStatusAsync);

        return routes;
    }

    // -------------------------------------------------- POST /announcements

    private static async Task<IResult> CreateAsync(
        CreateAnnouncementRequest request,
        IAnnouncementRepository repo,
        CancellationToken ct)
    {
        // Basic validation. Real projects would use FluentValidation; we're
        // keeping it minimal here — the contract itself (required props)
        // catches most missing-field cases at deserialization time.
        if (string.IsNullOrWhiteSpace(request.ManagerId) ||
            string.IsNullOrWhiteSpace(request.Subject) ||
            string.IsNullOrWhiteSpace(request.Body) ||
            request.Recipients is null || request.Recipients.Count == 0)
        {
            return Results.BadRequest(new { error = "Missing required fields." });
        }

        // Teacher said scheduledFor must be a Thursday.
        if (request.ScheduledFor.DayOfWeek != DayOfWeek.Thursday)
        {
            return Results.BadRequest(new { error = "scheduledFor must fall on a Thursday." });
        }

        var announcement = await repo.CreateAsync(request, ct);
        return Results.Created($"/internal/v1/announcements/{announcement.AnnouncementId}", announcement);
    }

    // ---------------------------------------------- GET /announcements/{id}

    private static async Task<IResult> GetAsync(
        string id,
        IAnnouncementRepository repo,
        CancellationToken ct)
    {
        var announcement = await repo.GetAsync(id, ct);
        return announcement is null ? Results.NotFound() : Results.Ok(announcement);
    }

    // -------------------------------------------------- GET /announcements
    // Dispatches on query params:
    //   ?managerId=XYZ                        → list by manager (paginated)
    //   ?status=PENDING&scheduledBefore=DATE  → scheduler query

    private static async Task<IResult> ListOrQueryAsync(
        HttpContext ctx,
        IAnnouncementRepository repo,
        string? managerId,
        string? status,
        string? scheduledBefore,
        int? limit,
        string? nextToken,
        CancellationToken ct)
    {
        var effectiveLimit = (limit is null || limit <= 0 || limit > 100) ? 20 : limit.Value;

        if (!string.IsNullOrEmpty(managerId))
        {
            var page = await repo.ListByManagerAsync(managerId, effectiveLimit, nextToken, ct);
            return Results.Ok(page);
        }

        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<AnnouncementStatus>(status, ignoreCase: true, out var parsedStatus))
            {
                return Results.BadRequest(new { error = $"Unknown status '{status}'." });
            }

            var cutoff = string.IsNullOrEmpty(scheduledBefore)
                ? DateOnly.FromDateTime(DateTime.UtcNow)
                : DateOnly.Parse(scheduledBefore, CultureInfo.InvariantCulture);

            var items = await repo.QueryByStatusAsync(parsedStatus, cutoff, ct);
            return Results.Ok(new AnnouncementListResponse { Items = items, NextToken = null });
        }

        return Results.BadRequest(new
        {
            error = "Provide either ?managerId=... or ?status=... query parameter."
        });
    }

    // -------------------------------------------- PUT /announcements/{id}

    private static async Task<IResult> UpdateAsync(
        string id,
        UpdateAnnouncementRequest update,
        IAnnouncementRepository repo,
        CancellationToken ct)
    {
        if (update.ScheduledFor is { } sf && sf.DayOfWeek != DayOfWeek.Thursday)
        {
            return Results.BadRequest(new { error = "scheduledFor must fall on a Thursday." });
        }

        var result = await repo.UpdateAsync(id, update, ct);
        if (result is null) return Results.NotFound();

        // If the returned item is no longer Pending, the update was silently
        // rejected by the conditional check. Tell the client honestly.
        if (result.Status != AnnouncementStatus.Pending)
        {
            return Results.Conflict(new
            {
                error = $"Cannot edit — status is {result.Status}.",
                current = result
            });
        }

        return Results.Ok(result);
    }

    // -------------------------------- PATCH /announcements/{id}/status

    private static async Task<IResult> UpdateStatusAsync(
        string id,
        UpdateStatusRequest request,
        IAnnouncementRepository repo,
        CancellationToken ct)
    {
        var result = await repo.UpdateStatusAsync(id, request.Status, request.ErrorMessage, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
