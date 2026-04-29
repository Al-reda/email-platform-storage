using EmailPlatform.Shared;
using EmailPlatform.Shared.Contracts;

namespace EmailPlatform.Storage.Data;

/// <summary>
/// The gateway to DynamoDB. All reads and writes to the announcements table
/// go through this interface. No other service touches the database directly.
/// </summary>
public interface IAnnouncementRepository
{
    Task<Announcement> CreateAsync(CreateAnnouncementRequest request, CancellationToken ct);

    Task<Announcement?> GetAsync(string announcementId, CancellationToken ct);

    /// <summary>List one manager's announcements, newest first, paginated.</summary>
    Task<AnnouncementListResponse> ListByManagerAsync(
        string managerId, int limit, string? nextToken, CancellationToken ct);

    /// <summary>Query by status — used by Scheduler to find Pending items due.</summary>
    Task<IReadOnlyList<Announcement>> QueryByStatusAsync(
        AnnouncementStatus status, DateOnly scheduledOnOrBefore, CancellationToken ct);

    /// <summary>Partial update. Only permitted while Status == Pending.</summary>
    Task<Announcement?> UpdateAsync(
        string announcementId, UpdateAnnouncementRequest update, CancellationToken ct);

    /// <summary>Transition an announcement's status (and optionally record an error).</summary>
    Task<Announcement?> UpdateStatusAsync(
        string announcementId, AnnouncementStatus newStatus, string? errorMessage, CancellationToken ct);

    /// <summary>Cheap operation used by health check to prove DynamoDB is reachable.</summary>
    Task PingAsync(CancellationToken ct);
}
