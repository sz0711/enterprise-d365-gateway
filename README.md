# 🏢 enterprise-d365-gateway

> Enterprise-grade Azure Functions integration platform — seamless data synchronization between heterogeneous systems and Microsoft Dynamics 365 Customer Engagement.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](#license)

---

## Overview

Azure Functions host exposing an **HTTP trigger** and a **Service Bus trigger** that accept batched UPSERT payloads and write them to Microsoft Dataverse, with resilience, concurrency control, caching, and early-bound entity validation built in.

### Key Capabilities

- **SOLID-decomposed pipeline** — 9 interfaces, 11 services, thin `UpsertOrchestrator`
- **HTTP + Service Bus triggers** — batch UPSERT via `POST /api/upsert` or queue-driven processing
- **Recursive lookup resolution** — configurable depth, cycle detection, `CreateIfNotExists`
- **In-memory cache** — KeyAttributes→Guid with sliding/absolute TTL and RAM-based size limit
- **Polly v8 resilience** — retry → circuit breaker → timeout pipeline for every Dataverse call
- **Keyed concurrency control** — `SemaphoreSlim(1,1)` per normalized `KeyAttributes` signature
- **Adaptive concurrency (AIMD)** — halve on 429, increment after sustained success
- **Rate limiting** — token-bucket per Dataverse API call (`MaxRequestsPerSecond`)
- **Early-bound validation** — `MODEL` assembly reflection for entity/attribute mapping
- **Health endpoints** — `/health/live` (liveness) + `/health/ready` (Dataverse readiness)
- **Input guards** — `MaxRequestBytes` (413), `MaxBatchItems` (400), JSON depth limit
- **Distributed tracing** — `ActivitySource` spans and structured events in all triggers
- **Startup validation** — HTTPS scheme, `[Range]` annotations, custom rules via `ValidateOnStart`
- **Plugin step bypass** — `BypassBusinessLogicExecutionStepIds` per entity for bulk operations

---

## Architecture

```
FUNC/
├── Extensions/          → DI registration (DataverseServiceCollectionExtensions.cs)
├── Functions/           → HTTP + ServiceBus + Health triggers
├── Interfaces/          → 9 service interfaces
├── Models/              → DataverseOptions, UpsertContracts, ErrorCategory
├── Services/            → 11 service implementations
└── Program.cs           → Host builder + DI + startup validation
MODEL/
├── Model/Entities/      → Early-bound entity classes (account, contact)
├── Model/OptionSets/    → OptionSet enums
└── Scripts/             → Code generation scripts
TESTS/
├── Unit/                → 104 unit tests (all 11 services)
└── Integration/         → 22 integration tests (FakeXrmEasy, triggers, DI)
```

**11 services** · **9 interfaces** · **126 tests** (104 unit + 22 integration) · **0 warnings**

### Pipeline

```
Request → IRequestValidator → IEarlyboundEntityMapper → ILookupResolver → IExternalIdResolver → IEntityUpsertExecutor → IResultMapper → Response
```

### Core Services

| Interface | Responsibility |
|-----------|----------------|
| `IRequestValidator` | Payload + structural validation (KeyAttributes, types, nested lookups) |
| `IEarlyboundEntityMapper` | Entity mapping from JSON to SDK Entity via MODEL reflection |
| `IExternalIdResolver` | KeyAttributes→Guid resolution with cache-first, invalidation on failure |
| `ILookupResolver` | Recursive lookup resolution with cycle detection + depth limits |
| `IEntityUpsertExecutor` | Dataverse Create/Update/Query wrapped in Polly v8 + rate limiter |
| `IAdaptiveConcurrencyLimiter` | AIMD concurrency: halve on throttle, increment on sustained success |
| `IUpsertLockCoordinator` | Keyed `SemaphoreSlim(1,1)` per normalized KeyAttributes signature |
| `IErrorClassifier` | Exception → `ErrorCategory` mapping |
| `IResultMapper` | UpsertResult construction + batch HTTP status code determination |
| `IHealthCheckService` | Liveness + readiness health checks (Dataverse connectivity) |
| `UpsertOrchestrator` | Thin pipeline coordinating all above services |

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (8.0+)
- [Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local) v4+
- Dataverse organization URL and (optionally) a user-assigned managed identity

### Build & Test

```bash
dotnet build enterprise-d365-gateway.sln
dotnet test  TESTS/
```

### Run Locally

1. Create `FUNC/local.settings.json` (see [Configuration](#configuration) below).
2. Start the host:

```bash
func start --prefix FUNC
```

3. Send a test UPSERT request:

```bash
curl -X POST http://localhost:7071/api/upsert \
     -H "Content-Type: application/json" \
     -d '{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACCT-1001"},"Attributes":{"name":"Contoso"}}]}'
```

---

## Configuration

Add the following to `FUNC/local.settings.json`:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "Dataverse:Url": "https://your-org.crm4.dynamics.com/",
    "Dataverse:UserAssignedManagedIdentityClientId": "",
    "Dataverse:MaxRequestsPerSecond": "120",
    "Dataverse:MaxDegreeOfParallelism": "4",
    "Dataverse:MinDegreeOfParallelism": "1",
    "Dataverse:AdaptiveConcurrencyEnabled": "true",
    "Dataverse:AdaptiveConcurrencySuccessThreshold": "20",
    "Dataverse:MaxRetries": "4",
    "Dataverse:RetryBaseDelayMs": "300",
    "Dataverse:RateLimitRetryDelaySeconds": "180",
    "Dataverse:TimeoutPerOperationSeconds": "45",
    "Dataverse:CircuitBreakerFailureThreshold": "8",
    "Dataverse:CircuitBreakerSamplingDurationSeconds": "60",
    "Dataverse:CircuitBreakerBreakDurationSeconds": "45",
    "Dataverse:CacheMemoryBudgetPercent": "20",
    "Dataverse:CacheMemoryBudgetMinMb": "64",
    "Dataverse:CacheMemoryBudgetMaxMb": "512",
    "Dataverse:CacheEntrySizeBytes": "128",
    "Dataverse:MaxBatchItems": "1000",
    "Dataverse:MaxRequestBytes": "10485760",
    "Dataverse:BypassPluginStepIds:account": "",
    "Dataverse:BypassPluginStepIds:contact": "",
    "ServiceBusConnection": "Endpoint=sb://your-servicebus-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=YOUR_KEY",
    "ServiceBusQueueName": "dataverse-upsert-queue",
    "APPLICATIONINSIGHTS_CONNECTION_STRING": ""
  },
  "Host": {
    "LocalHttpPort": 7071,
    "CORS": "*",
    "CORSCredentials": false
  }
}
```

### Configuration Reference

| Key | Description | Default |
|-----|-------------|---------|
| `Dataverse__Url` | Dataverse org URL | *(required)* |
| `Dataverse__UserAssignedManagedIdentityClientId` | Managed identity client ID | *(optional)* |
| `Dataverse__MaxRequestsPerSecond` | Token-bucket rate limit | `300` |
| `Dataverse__MaxDegreeOfParallelism` | Max parallel batch processing | `8` |
| `Dataverse__MinDegreeOfParallelism` | Floor for adaptive concurrency | `1` |
| `Dataverse__AdaptiveConcurrencyEnabled` | Enable AIMD auto-tuning | `true` |
| `Dataverse__AdaptiveConcurrencySuccessThreshold` | Consecutive successes before parallelism +1 | `20` |
| `Dataverse__MaxRetries` | Retry attempts per operation | `4` |
| `Dataverse__RetryBaseDelayMs` | Base delay for exponential backoff | `200` |
| `Dataverse__RateLimitRetryDelaySeconds` | Fixed cooldown for 429 retries | `300` |
| `Dataverse__TimeoutPerOperationSeconds` | Timeout per Dataverse call | `30` |
| `Dataverse__CircuitBreakerFailureThreshold` | Min throughput before evaluating failures | `10` |
| `Dataverse__CircuitBreakerSamplingDurationSeconds` | Sampling window for circuit breaker | `60` |
| `Dataverse__CircuitBreakerBreakDurationSeconds` | Duration circuit stays open | `30` |
| `Dataverse__CacheSlidingExpirationMinutes` | Cache sliding TTL | `120` (2 h) |
| `Dataverse__CacheAbsoluteExpirationMinutes` | Cache absolute TTL (hard upper bound) | `360` (6 h) |
| `Dataverse__CacheMemoryBudgetPercent` | % of available RAM for cache | `20` |
| `Dataverse__CacheMemoryBudgetMinMb` | Lower bound for cache `SizeLimit` | `64` |
| `Dataverse__CacheMemoryBudgetMaxMb` | Upper bound for cache `SizeLimit` | `512` |
| `Dataverse__CacheEntrySizeBytes` | Estimated size per cache entry | `128` |
| `Dataverse__MaxLookupDepth` | Global max recursive lookup depth | `3` |
| `Dataverse__LookupTimeoutSeconds` | Timeout budget per lookup tree | `60` |
| `Dataverse__MaxBatchItems` | Maximum payloads per request | `1000` |
| `Dataverse__MaxRequestBytes` | Maximum request body size | `10485760` (10 MB) |
| `Dataverse__BypassPluginStepIds__<entity>` | Comma-separated plugin step GUIDs to bypass | *(empty)* |
| `ServiceBusConnection` | Azure Service Bus connection string | *(required for SB trigger)* |
| `ServiceBusQueueName` | Queue name | *(required for SB trigger)* |

---

## JSON Contract

### Simple Upsert

```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": { "accountnumber": "ACCT-1001" },
      "Attributes": { "name": "Contoso", "description": "Enterprise gateway upsert" },
      "SourceSystem": "ERP"
    }
  ]
}
```

### Single Lookup (CreateIfNotExists)

```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": { "accountnumber": "ACCT-1002" },
      "Attributes": { "name": "Adventure Works", "address1_city": "Seattle" },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "KeyAttributes": { "emailaddress1": "john.doe@example.com" },
          "CreateIfNotExists": true,
          "CreateAttributes": {
            "firstname": "John",
            "lastname": "Doe",
            "emailaddress1": "john.doe@example.com"
          }
        }
      }
    }
  ]
}
```

### Recursive Lookup (Nested 2 Levels)

```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": { "accountnumber": "ACCT-2000" },
      "Attributes": { "name": "Recursive Corp" },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "KeyAttributes": { "emailaddress1": "nested@example.com" },
          "CreateIfNotExists": true,
          "CreateAttributes": {
            "firstname": "Nested",
            "lastname": "Contact",
            "emailaddress1": "nested@example.com"
          },
          "NestedLookups": {
            "accountid": {
              "EntityLogicalName": "account",
              "KeyAttributes": { "accountnumber": "PARENT-100" },
              "CreateIfNotExists": true,
              "CreateAttributes": { "name": "Parent Account", "accountnumber": "PARENT-100" }
            }
          }
        }
      },
      "MaxLookupDepth": 3
    }
  ]
}
```

### Lookup Resolution

Resolution order:
1. Query Dataverse by `KeyAttributes`.
2. If found → return `EntityReference`.
3. If not found and `CreateIfNotExists` → resolve `NestedLookups` recursively → create → return `EntityReference`.
4. If not found and `CreateIfNotExists` is `false` → fail with `Permanent` error.

**Safety:**
- **Max depth**: global default is 3, overridable per batch or per lookup.
- **Cycle detection**: visited-set per resolution path prevents infinite loops.
- **Cache eviction**: on failure with a cached GUID, the entry is evicted and a single fresh re-resolve is attempted.

---

## Error Model

### ErrorCategory

| Value | Description | HTTP Mapping |
|-------|-------------|--------------|
| `None` | Success | 200 |
| `Validation` | Structural / type / KeyAttributes errors | 400 |
| `Transient` | Timeout, network, circuit breaker | 500 |
| `Permanent` | Dataverse rejection, unrecoverable | 500 |
| `Throttling` | Rate limit exceeded | 500 |
| `Cancellation` | Request cancelled | 408 |

### Response

```json
{
  "Id": "00000000-0000-0000-0000-000000000000",
  "Created": true,
  "EntityLogicalName": "account",
  "UpsertKey": "account:accountnumber=ACCT-1001",
  "ErrorMessage": null,
  "ErrorCategory": "None",
  "ValidationErrors": null,
  "LookupTraces": [
    {
      "AttributeName": "primarycontactid",
      "EntityLogicalName": "contact",
      "ResolvedId": "...",
      "WasCreated": true,
      "Depth": 0,
      "NestedTraces": []
    }
  ]
}
```

### Batch Status Code Logic

| Result | HTTP Code |
|--------|-----------|
| All payloads succeeded | `200 OK` |
| Only `Validation` failures | `400 Bad Request` |
| Any `Transient` / `Permanent` / `Throttling` failure | `500 Internal Server Error` |
| Request body too large | `413 Payload Too Large` |

All payload results are always returned in full regardless of overall status code.

---

## Health Endpoints

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `GET /health/live` | Anonymous | Liveness — `200` if the process is running |
| `GET /health/ready` | Function Key | Readiness — validates Dataverse `ServiceClient` connectivity |

```json
{
  "Status": "Healthy",
  "Checks": {
    "DataverseConnection": { "Status": "Healthy", "Detail": null }
  }
}
```

If Dataverse is unreachable, returns `503` with `"Status": "Unhealthy"` and a `Detail` error message.

---

## Integration Tests

```bash
# Run all tests
dotnet test TESTS/

# Unit tests only
dotnet test TESTS/ --filter "Category!=Integration"

# With coverage
dotnet test TESTS/ --collect:"XPlat Code Coverage"
```

Tests require no external services — integration tests use FakeXrmEasy v9 in-memory Dataverse.

### Test Breakdown

| Suite | Tests |
|-------|-------|
| Unit (11 services) | 104 |
| Integration — `UpsertOrchestrator` (FakeXrmEasy) | 9 |
| Integration — `HttpUpsertTrigger` | 6 |
| Integration — `ServiceBusTrigger` | 5 |
| Integration — Dependency Injection | 2 |
| **Total** | **126** |

---

## Load Testing

A PowerShell script `LoadTest.ps1` (repository root) load-tests the HTTP endpoint:

```powershell
# Basic usage (PowerShell 7+)
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" -FunctionKey "your-function-key"

# Safe profile (minimise throttling)
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" -Profile Safe

# Custom parameters
.\LoadTest.ps1 -FunctionUrl "https://your-function.azurewebsites.net/api/upsert" `
               -Profile Normal -FunctionKey "your-function-key" `
               -ThreadCount 8 -RequestsPerThread 40 -BatchSize 3
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `FunctionUrl` | *(required)* | Upsert endpoint URL |
| `FunctionKey` | | Azure Function key |
| `Profile` | `Normal` | `Safe` / `Normal` / `Stress` / `Custom` |
| `ThreadCount` | `6` | Parallel threads |
| `RequestsPerThread` | `80` | Requests per thread |
| `BatchSize` | `3` | Payloads per request |
| `ReportPath` | | Optional JSON output path |

Reports p50/p95/p99 latency, throughput (req/s, payloads/s), and status-code distribution.

---

## Technology Stack

| Component | Version |
|-----------|---------|
| .NET | 8.0 LTS |
| Microsoft.PowerPlatform.Dataverse.Client | 1.x |
| Polly | 8.x |
| xUnit | 2.9.2 |
| Moq | 4.20.72 |
| FluentAssertions | 6.12.2 |
| FakeXrmEasy.v9 | 3.8.0 |

---

## License

MIT

## Disclaimer

Private project. Provided without warranty. Use at your own risk.

---

> Built with ❤️ for Microsoft Dynamics 365 integration engineers.
