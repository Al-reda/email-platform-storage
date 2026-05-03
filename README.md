# Email Platform — Storage Service

Internal API — the only service that talks to DynamoDB. All other services call this over HTTP.

## Endpoints

- `POST /internal/v1/announcements` — create
- `GET /internal/v1/announcements/{id}` — get by ID
- `GET /internal/v1/announcements?managerId=...` — list by manager (paginated)
- `GET /internal/v1/announcements?status=...&scheduledBefore=...` — query by status
- `PUT /internal/v1/announcements/{id}` — edit (only while Pending)
- `PATCH /internal/v1/announcements/{id}/status` — change status
- `GET /health` — liveness
- `GET /health/ready` — readiness (pings DynamoDB)

## Run locally

```bash
STORAGE__SERVICEURL=http://localhost:4566 STORAGE__TABLENAME=Announcements \
  dotnet run --urls http://localhost:5001
```


