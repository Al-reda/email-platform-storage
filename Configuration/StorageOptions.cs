namespace EmailPlatform.Storage.Configuration;

/// <summary>
/// Configuration values the Storage Service reads at startup.
///
/// Factor 3 (Config): these are populated from environment variables by the
/// .NET configuration system. The double-underscore separator maps nested
/// keys: STORAGE__TABLENAME → Storage:TableName → StorageOptions.TableName.
///
/// Factor 4 (Backing services): ServiceUrl lets us point at a local
/// DynamoDB Local instance in dev without any code change. In AWS, we leave
/// it unset and the SDK uses the real regional endpoint.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Name of the DynamoDB table holding announcements.</summary>
    public string TableName { get; set; } = "Announcements";

    /// <summary>AWS region. Ignored when ServiceUrl is set.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Override endpoint. Set to http://dynamodb-local:8000 (or localhost:8000)
    /// in local dev. Leave null in production so the SDK talks to real DynamoDB.
    /// </summary>
    public string? ServiceUrl { get; set; }
}
