using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmailPlatform.Shared;
using EmailPlatform.Shared.Contracts;
using EmailPlatform.Storage.Configuration;
using Microsoft.Extensions.Options;

namespace EmailPlatform.Storage.Data;

/// <summary>
/// Low-level DynamoDB implementation. Uses AttributeValue maps directly so
/// the schema is explicit and auditable. No ORM, no magic.
///
/// Indexes used:
///   - Main table PK:      announcementId
///   - GSI by-manager:     managerId (PK) + createdAt (SK)
///   - GSI by-status:      status (PK) + scheduledFor (SK)
/// </summary>
public sealed class DynamoAnnouncementRepository : IAnnouncementRepository
{
    private const string ByManagerIndex = "by-manager-index";
    private const string ByStatusIndex = "by-status-index";

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly ILogger<DynamoAnnouncementRepository> _logger;

    public DynamoAnnouncementRepository(
        IAmazonDynamoDB client,
        IOptions<StorageOptions> options,
        ILogger<DynamoAnnouncementRepository> logger)
    {
        _client = client;
        _tableName = options.Value.TableName;
        _logger = logger;
    }

    // ---------------------------------------------------------------- Create

    public async Task<Announcement> CreateAsync(CreateAnnouncementRequest request, CancellationToken ct)
    {
        var announcement = new Announcement
        {
            AnnouncementId = Guid.NewGuid().ToString("N"),
            ManagerId = request.ManagerId,
            Subject = request.Subject,
            Body = request.Body,
            Recipients = request.Recipients,
            ScheduledFor = request.ScheduledFor,
            Status = AnnouncementStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _client.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = ToItem(announcement),
            ConditionExpression = "attribute_not_exists(announcementId)"
        }, ct);

        _logger.LogInformation("Created announcement {AnnouncementId} for manager {ManagerId}",
            announcement.AnnouncementId, announcement.ManagerId);

        return announcement;
    }

    // ------------------------------------------------------------------- Get

    public async Task<Announcement?> GetAsync(string announcementId, CancellationToken ct)
    {
        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["announcementId"] = new(announcementId)
            }
        }, ct);

        return response.IsItemSet ? FromItem(response.Item) : null;
    }

    // ----------------------------------------------------- List by manager

    public async Task<AnnouncementListResponse> ListByManagerAsync(
        string managerId, int limit, string? nextToken, CancellationToken ct)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = ByManagerIndex,
            KeyConditionExpression = "managerId = :mid",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":mid"] = new(managerId)
            },
            ScanIndexForward = false, // newest first (SK is createdAt)
            Limit = limit
        };

        if (!string.IsNullOrEmpty(nextToken))
        {
            request.ExclusiveStartKey = DecodeToken(nextToken);
        }

        var response = await _client.QueryAsync(request, ct);

        return new AnnouncementListResponse
        {
            Items = response.Items.Select(FromItem).ToList(),
            NextToken = response.LastEvaluatedKey is { Count: > 0 }
                ? EncodeToken(response.LastEvaluatedKey)
                : null
        };
    }

    // ---------------------------------------------------- Query by status

    public async Task<IReadOnlyList<Announcement>> QueryByStatusAsync(
        AnnouncementStatus status, DateOnly scheduledOnOrBefore, CancellationToken ct)
    {
        var response = await _client.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            IndexName = ByStatusIndex,
            KeyConditionExpression = "#s = :status AND scheduledFor <= :date",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#s"] = "status"  // status is a reserved word in DynamoDB
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new(status.ToString().ToUpperInvariant()),
                [":date"] = new(scheduledOnOrBefore.ToString("O", CultureInfo.InvariantCulture))
            }
        }, ct);

        return response.Items.Select(FromItem).ToList();
    }

    // ------------------------------------------------------------ Update

    public async Task<Announcement?> UpdateAsync(
        string announcementId, UpdateAnnouncementRequest update, CancellationToken ct)
    {
        var sets = new List<string>();
        var names = new Dictionary<string, string>();
        var values = new Dictionary<string, AttributeValue>();

        if (update.Subject is not null)
        {
            sets.Add("subject = :subject");
            values[":subject"] = new(update.Subject);
        }
        if (update.Body is not null)
        {
            sets.Add("body = :body");
            values[":body"] = new(update.Body);
        }
        if (update.Recipients is not null)
        {
            sets.Add("recipients = :recipients");
            values[":recipients"] = new AttributeValue { SS = update.Recipients.ToList() };
        }
        if (update.ScheduledFor is not null)
        {
            sets.Add("scheduledFor = :scheduledFor");
            values[":scheduledFor"] = new(update.ScheduledFor.Value.ToString("O", CultureInfo.InvariantCulture));
        }

        if (sets.Count == 0)
        {
            // Nothing to change — just return current state.
            return await GetAsync(announcementId, ct);
        }

        // Guard: only allow edits while still Pending.
        names["#s"] = "status";
        values[":pending"] = new(AnnouncementStatus.Pending.ToString().ToUpperInvariant());

        try
        {
            var response = await _client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["announcementId"] = new(announcementId)
                },
                UpdateExpression = "SET " + string.Join(", ", sets),
                ConditionExpression = "attribute_exists(announcementId) AND #s = :pending",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
                ReturnValues = ReturnValue.ALL_NEW
            }, ct);

            return FromItem(response.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            // Either the item doesn't exist, or it's no longer Pending.
            return await GetAsync(announcementId, ct); // caller decides 404 vs 409 from status
        }
    }

    // ------------------------------------------------------ Update status

    public async Task<Announcement?> UpdateStatusAsync(
        string announcementId, AnnouncementStatus newStatus, string? errorMessage, CancellationToken ct)
    {
        var names = new Dictionary<string, string>
        {
            ["#s"] = "status"
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":status"] = new(newStatus.ToString().ToUpperInvariant())
        };
        var setExpressions = new List<string> { "#s = :status" };

        if (newStatus == AnnouncementStatus.Sent)
        {
            setExpressions.Add("sentAt = :sentAt");
            values[":sentAt"] = new(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            setExpressions.Add("errorMessage = :err");
            values[":err"] = new(errorMessage);
        }

        try
        {
            var response = await _client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["announcementId"] = new(announcementId)
                },
                UpdateExpression = "SET " + string.Join(", ", setExpressions),
                ConditionExpression = "attribute_exists(announcementId)",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
                ReturnValues = ReturnValue.ALL_NEW
            }, ct);

            return FromItem(response.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------- Ping

    public async Task PingAsync(CancellationToken ct)
    {
        // DescribeTable is cheap and confirms both connectivity and that the
        // table actually exists with expected config.
        await _client.DescribeTableAsync(_tableName, ct);
    }

    // ============================================================ Mapping

    private static Dictionary<string, AttributeValue> ToItem(Announcement a)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["announcementId"] = new(a.AnnouncementId),
            ["managerId"]      = new(a.ManagerId),
            ["subject"]        = new(a.Subject),
            ["body"]           = new(a.Body),
            ["recipients"]     = new AttributeValue { SS = a.Recipients.ToList() },
            ["scheduledFor"]   = new(a.ScheduledFor.ToString("O", CultureInfo.InvariantCulture)),
            ["status"]         = new(a.Status.ToString().ToUpperInvariant()),
            ["createdAt"]      = new(a.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
        };

        if (a.SentAt is not null)
        {
            item["sentAt"] = new(a.SentAt.Value.ToString("O", CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(a.ErrorMessage))
        {
            item["errorMessage"] = new(a.ErrorMessage);
        }

        return item;
    }

    private static Announcement FromItem(Dictionary<string, AttributeValue> item) => new()
    {
        AnnouncementId = item["announcementId"].S,
        ManagerId      = item["managerId"].S,
        Subject        = item["subject"].S,
        Body           = item["body"].S,
        Recipients     = item.TryGetValue("recipients", out var r) ? r.SS : new List<string>(),
        ScheduledFor   = DateOnly.Parse(item["scheduledFor"].S, CultureInfo.InvariantCulture),
        Status         = Enum.Parse<AnnouncementStatus>(item["status"].S, ignoreCase: true),
        CreatedAt      = DateTimeOffset.Parse(item["createdAt"].S, CultureInfo.InvariantCulture),
        SentAt         = item.TryGetValue("sentAt", out var sent)
                           ? DateTimeOffset.Parse(sent.S, CultureInfo.InvariantCulture)
                           : null,
        ErrorMessage   = item.TryGetValue("errorMessage", out var err) ? err.S : null
    };

    // Pagination tokens: base64-encoded JSON of the LastEvaluatedKey.
    private static string EncodeToken(Dictionary<string, AttributeValue> key)
    {
        var simple = key.ToDictionary(k => k.Key, k => k.Value.S);
        var json = JsonSerializer.Serialize(simple);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static Dictionary<string, AttributeValue> DecodeToken(string token)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        var simple = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        return simple.ToDictionary(k => k.Key, v => new AttributeValue(v.Value));
    }
}
