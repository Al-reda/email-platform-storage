using EmailPlatform.Storage.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EmailPlatform.Storage.HealthChecks;

/// <summary>
/// Liveness check that proves DynamoDB is reachable and our table exists.
/// Wired into /health/ready so ECS can tell whether to route traffic here.
///
/// Factor 9 (Disposability): if DynamoDB is unreachable the container is
/// marked unhealthy; ECS stops routing requests and eventually replaces it.
/// </summary>
public sealed class DynamoHealthCheck : IHealthCheck
{
    private readonly IAnnouncementRepository _repo;

    public DynamoHealthCheck(IAnnouncementRepository repo)
    {
        _repo = repo;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _repo.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy("DynamoDB reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("DynamoDB unreachable.", ex);
        }
    }
}
