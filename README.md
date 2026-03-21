# 🏢 enterprise-d365-gateway

> 🔗 Enterprise-grade Azure Functions integration platform - seamless data synchronization between heterogeneous systems and Microsoft Dynamics 365 Customer Engagement.

---

## 📊 Implementation Status

| Feature | Status | Details |
|---|---|---|
| 🔀 SOLID Architecture | ✅ Done | 7 interfaces, 9 services, `UpsertOrchestrator` pipeline |
| 📝 Contracts | ✅ Done | `KeyAttributes`, `ErrorCategory`, `NestedLookups`, `LookupTraces` |
| 🔍 Recursive Lookup Resolution | ✅ Done | Cycle detection, configurable depth, `CreateIfNotExists` |
| 🧠 In-Memory Cache | ✅ Done | `IMemoryCache` with sliding/absolute TTL, RAM-based `SizeLimit` |
| 🛡️ Polly v8 Resilience | ✅ Done | Retry → CircuitBreaker → Timeout pipeline |
| 🔒 Keyed Concurrency | ✅ Done | `SemaphoreSlim(1,1)` per normalized `KeyAttributes` signature |
| ⚡ Rate Limiting | ✅ Done | Token bucket per Dataverse API call |
| 🚫 Plugin Step Bypass | ✅ Done | `BypassBusinessLogicExecutionStepIds` per entity |
| 🌐 HTTP Trigger | ✅ Done | `POST /api/upsert` with batch support |
| 📨 Service Bus Trigger | ✅ Done | Queue-driven upsert processing |
| 🏷️ Early-bound Validation | ✅ Done | `MODEL` assembly reflection for entity/attribute mapping |
| 🔐 Managed Identity Auth | ✅ Done | User-assigned managed identity via `ServiceClient` |
| 📈 Load Test Script | ✅ Done | Multi-threaded PowerShell with p50/p95/p99 reporting |
| 📖 Documentation | ✅ Done | Full README, config table, JSON examples |
| 🪧 Unit Tests | ✅ Done | 88 xUnit tests - all 9 services fully covered |
| 🧪 Integration Tests | ✅ Done | 23 tests - FakeXrmEasy v9, HTTP trigger, ServiceBus trigger, DI |

---

## 🏗️ Overview

This repository implements:
- ⚡ **HTTP trigger** (`DataverseUpsertHttp`) - incoming UPSERT requests in batch format
- 📨 **Service Bus trigger** (`DataverseUpsertServiceBus`) - reliable queue-driven UPSERT processing
- 🔀 **SOLID-decomposed pipeline** - clearly separated responsibilities (validation → lookup → execution → classification)
- 🔍 **Recursive lookup resolution** - configurable max depth, cycle detection, per-lookup `CreateIfNotExists`
- 🧠 **KeyAttributes→Guid in-memory cache** - per instance with sliding/absolute TTL and RAM-based size limits
- 🔒 **Keyed concurrency control** per `KeyAttributes` signature - identical keys serialized, distinct keys parallel
- 🛡️ **Polly v8 resilience pipeline** - retry with exponential backoff + jitter, circuit breaker, per-operation timeout
- 🏷️ **Structured error classification** - Validation, Transient, Permanent, Throttling, Cancellation with per-payload detail
- 🔗 **Dataverse upsert orchestration** via `IOrganizationServiceAsync2` and `Microsoft.PowerPlatform.Dataverse.Client`
- 🔐 **User-assigned managed identity** authentication
- ⚡ **Rate limiting** (token bucket) and configurable degree-of-parallelism
- 🏷️ **Early-bound entity validation** and conversion via `MODEL` assembly

---

## 🏙️ Architecture

```
📥 Request
  → 📝 IRequestValidator
  → 🏷️ IEarlyboundEntityMapper
  → 🔍 ILookupResolver (recursive)
  → 🧠 IExternalIdResolver (cache-first)
  → ⚡ IEntityUpsertExecutor (Polly v8)
  → 📤 IResultMapper
→ 📥 Response
```

### 🧩 Core Services

| Icon | Interface | Responsibility |
|---|---|---|
| 📝 | `IRequestValidator` | Payload + structural validation (KeyAttributes, types, nested lookups) |
| 🏷️ | `IEarlyboundEntityMapper` | Entity mapping from JSON to SDK Entity via MODEL reflection |
| 🧠 | `IExternalIdResolver` | KeyAttributes→Guid resolution with cache-first, invalidation on failure |
| 🔍 | `ILookupResolver` | Recursive lookup resolution with cycle detection + depth limits |
| ⚡ | `IEntityUpsertExecutor` | Dataverse Create/Update/Query wrapped in Polly v8 resilience + rate limiter |
| 🔒 | `IUpsertLockCoordinator` | Keyed `SemaphoreSlim(1,1)` per normalized KeyAttributes signature |
| 🚨 | `IErrorClassifier` | Exception → `ErrorCategory` mapping |
| 📤 | `IResultMapper` | UpsertResult construction + batch HTTP status code determination |
| 🎯 | `UpsertOrchestrator` | Thin pipeline coordinating all above services |

### 📂 Project Structure

```
FUNC/
├── 📁 Extensions/          → DI registration (DataverseServiceCollectionExtensions.cs)
├── 📁 Functions/            → HTTP + ServiceBus triggers
├── 📁 Interfaces/           → 7 service interfaces
├── 📁 Models/               → DataverseOptions, UpsertContracts, ErrorCategory
├── 📁 Services/             → 9 service implementations
├── 📄 Program.cs            → Host builder + DI setup
MODEL/
├── 📁 Model/Entities/       → Early-bound entity classes (account, contact)
├── 📁 Model/OptionSets/     → OptionSet enums
└── 📁 Scripts/              → Code generation scripts
TESTS/
├── 📁 Unit/                 → 88 unit tests (all 9 services)
├── 📁 Integration/          → 23 integration tests (FakeXrmEasy, triggers, DI)
└── 📄 enterprise-d365-gateway.Tests.csproj
```

## ⚙️ Requirements

1. 📦 .NET 8 SDK (or newer) for building and running.
2. 🔧 Environment variables / `local.settings.json`:

| Key | Description | Default |
|---|---|---|
| `Dataverse__Url` | Dataverse org URL | *(required)* |
| `Dataverse__UserAssignedManagedIdentityClientId` | Managed identity client id | *(optional)* |
| `Dataverse__MaxRequestsPerSecond` | Token bucket rate limit | `300` |
| `Dataverse__MaxDegreeOfParallelism` | Parallel batch processing | `8` |
| `Dataverse__MaxRetries` | Retry attempts per operation | `4` |
| `Dataverse__RetryBaseDelayMs` | Base delay for exponential backoff | `200` |
| `Dataverse__TimeoutPerOperationSeconds` | Timeout per Dataverse call | `30` |
| `Dataverse__CircuitBreakerFailureThreshold` | Min throughput before evaluating failures | `10` |
| `Dataverse__CircuitBreakerSamplingDurationSeconds` | Sampling window for circuit breaker | `60` |
| `Dataverse__CircuitBreakerBreakDurationSeconds` | Duration circuit stays open | `30` |
| `Dataverse__CacheSlidingExpirationMinutes` | Cache sliding TTL (entry stays if accessed) | `120` (2h) |
| `Dataverse__CacheAbsoluteExpirationMinutes` | Cache absolute TTL (hard upper bound) | `360` (6h) |
| `Dataverse__CacheMemoryBudgetPercent` | % of available RAM for cache | `20` |
| `Dataverse__CacheEntrySizeBytes` | Estimated size per cache entry | `128` |
| `Dataverse__MaxLookupDepth` | Global max recursive lookup depth | `3` |
| `Dataverse__LookupTimeoutSeconds` | Timeout budget per lookup tree | `60` |
| `Dataverse__BypassPluginStepIds__<entity>` | Comma-separated plugin step registration GUIDs to bypass for `<entity>` | *(empty - no bypass)* |
| `ServiceBusConnection` | Azure Service Bus connection string | *(required for SB trigger)* |
| `ServiceBusQueueName` | Queue name | *(required for SB trigger)* |

---

## 📋 JSON Contract

### 📄 Simple Upsert
```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": {
        "accountnumber": "ACCT-1001"
      },
      "Attributes": {
        "name": "Contoso",
        "description": "Enterprise gateway upsert"
      },
      "SourceSystem": "ERP"
    }
  ]
}
```

### 🔍 Single Lookup (CreateIfNotExists)
```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": {
        "accountnumber": "ACCT-1002"
      },
      "Attributes": {
        "name": "Adventure Works",
        "address1_city": "Seattle"
      },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "KeyAttributes": {
            "emailaddress1": "john.doe@example.com"
          },
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

### 🔄 Recursive Lookup (Nested 2 Levels)
```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": {
        "accountnumber": "ACCT-2000"
      },
      "Attributes": {
        "name": "Recursive Corp"
      },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "KeyAttributes": {
            "emailaddress1": "nested@example.com"
          },
          "CreateIfNotExists": true,
          "CreateAttributes": {
            "firstname": "Nested",
            "lastname": "Contact",
            "emailaddress1": "nested@example.com"
          },
          "NestedLookups": {
            "accountid": {
              "EntityLogicalName": "account",
              "KeyAttributes": {
                "accountnumber": "PARENT-100"
              },
              "CreateIfNotExists": true,
              "CreateAttributes": {
                "name": "Parent Account",
                "accountnumber": "PARENT-100"
              }
            }
          }
        }
      },
      "MaxLookupDepth": 3
    }
  ]
}
```

### 📦 Batch with MaxLookupDepth Override
```json
{
  "Payloads": [ ... ],
  "MaxLookupDepth": 5
}
```

### 🔍 Lookup Resolution
The `Lookups` property allows automatic resolution of entity references:
- 🔑 **KeyAttributes**: Required key-value pairs used for lookup, cache, and concurrency signature.
- ➕ **CreateIfNotExists**: If `true`, creates the referenced entity if not found.
- 📝 **CreateAttributes**: Attributes used when creating the referenced entity.
- 🔄 **NestedLookups**: Recursive lookup definitions resolved *before* the parent entity is created.
- 📏 **MaxDepth**: Optional per-lookup override for maximum recursion depth.

Resolution order:
1. 🔍 Query Dataverse by `KeyAttributes`.
2. ✅ If found → return `EntityReference`.
3. ➕ If not found and `CreateIfNotExists` → resolve `NestedLookups` recursively → create entity → return `EntityReference`.
4. ❌ If not found and `CreateIfNotExists` is `false` → fail with `Permanent` error.

🛡️ Safety:
- 📏 **MaxDepth**: Global default 3, overridable per batch (`MaxLookupDepth`), per payload, or per lookup (`MaxDepth`).
- 🔄 **Cycle Detection**: Visited set per resolution path prevents infinite loops.
- 🗑️ Lookup resolution failures invalidate cache and trigger a single fresh re-resolve.

---

## 🚨 Error Model

### ErrorCategory Enum
| Value | Description | HTTP Mapping |
|---|---|---|
| `None` | Success | 200 |
| `Validation` | Structural/type/KeyAttributes errors | 400 |
| `Transient` | Timeout, network, circuit breaker | 500 |
| `Permanent` | Dataverse rejection, unrecoverable | 500 |
| `Throttling` | Rate limit exceeded | 500 |
| `Cancellation` | Request was canceled | 408 |

### 📤 Response Structure
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

### 🔢 Batch Status Code Logic
- ✅ `200 OK`: All payloads succeeded (`ErrorCategory == None`).
- ⚠️ `400 Bad Request`: Only `Validation` failures, no technical errors.
- ❌ `500 Internal Server Error`: At least one `Transient`, `Permanent`, or `Throttling` failure.

ℹ️ All payload results are always returned in full, regardless of overall status code.

---

## 🧠 Cache Behavior
- 🎯 **Scope**: Per Function instance (in-process `IMemoryCache`, not distributed).
- ⏱️ **Sliding Expiration**: 2h - entry stays alive as long as it’s accessed within this window.
- ⏰ **Absolute Expiration**: 6h - hard upper bound regardless of access frequency.
- 💾 **RAM Budget**: Cache `SizeLimit` is computed as `CacheMemoryBudgetPercent` of total available memory.
- 📏 **Entry Size**: Each entry’s `Size` is set to `CacheEntrySizeBytes` (128 bytes default).
- 🗑️ **Invalidation**: On upsert/resolve failure with a cached GUID, the cache key is evicted and a single fresh re-resolve is attempted.
- 🚫 **No Distribution**: Cache is local per instance. No Redis or cross-instance synchronization.

---

## 🛡️ Resilience Pipeline (Polly v8)
All Dataverse I/O (Create, Update, RetrieveMultiple) goes through a unified resilience pipeline:

1. 🔄 **Retry** (outermost): Exponential backoff with jitter. Handles `TimeoutException`, `HttpRequestException`, `TimeoutRejectedException`.
2. ⚡ **Circuit Breaker** (middle): Opens after `FailureRatio > 0.5` within the sampling window. Rejects calls for `BreakDuration` seconds.
3. ⏱️ **Timeout** (innermost): Per-operation timeout (`TimeoutPerOperationSeconds`).

⚡ Rate limiting is applied per Dataverse API call via a token bucket limiter (`MaxRequestsPerSecond`).

---

## 🔒 Concurrency Control
- 🏷️ `KeyAttributes` is **required** for every main entity and every lookup/nested lookup.
- ✂️ A deterministic signature is derived server-side: `{entity}:{sorted key=value pairs}`.
- 🔀 Identical `KeyAttributes` signatures are **serialized** (keyed `SemaphoreSlim(1,1)`), preventing race conditions on the same record.
- 🚀 Different `KeyAttributes` signatures run fully in parallel.
- ℹ️ Identical payloads are NOT deduplicated - each is processed and serialized by key.

---

## 🚫 Custom Plugin Step Bypass
When performing bulk operations it is often desirable to bypass specific custom Dataverse plugin steps to reduce processing time and avoid side-effects.
This is implemented via the [📖 `BypassBusinessLogicExecutionStepIds` optional parameter](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bypass-custom-business-logic?tabs=sdk).

**🔧 Configuration** (environment variables / `local.settings.json`):

Map entity logical name → comma-separated plugin step registration GUIDs:
```
Dataverse:BypassPluginStepIds:account = "45e0c603-0d0b-466e-a286-d7fc1cda8361,d5370603-e4b9-4b92-b765-5966492a4fd7"
Dataverse:BypassPluginStepIds:contact = "a1b2c3d4-0000-0000-0000-000000000001"
```

Or as environment variables:
```
Dataverse__BypassPluginStepIds__account=45e0c603-...,d5370603-...
Dataverse__BypassPluginStepIds__contact=a1b2c3d4-...
```

**⚙️ Behaviour**:
- ✅ Only `Create` and `Update` operations are affected; `RetrieveMultiple` (lookups) is never bypassed.
- 🎯 Per-entity: only entities with configured step IDs use the optional parameter.
- 📝 At startup the executor logs each entity and its bypass step IDs.
- ⚠️ **Limit**: Dataverse defaults to max 3 step IDs per request (adjustable via `BypassBusinessLogicExecutionStepIdsLimit` OrgDbOrgSetting, max 10).
- 🔐 **Requirement**: the Dataverse application user must hold the `prvBypassCustomBusinessLogic` privilege.
- 🔍 Step IDs can be found via the Plugin Registration Tool or by querying `sdkmessageprocessingstep` records.

---

## 🧪 Testing

**111 tests total** (88 unit + 23 integration), all passing.

```powershell
# Run all tests
dotnet test TESTS/ --verbosity normal

# Run with coverage
dotnet test TESTS/ --collect:"XPlat Code Coverage"
```

### Unit Tests (88)
| Service | Tests |
|---|---|
| `ErrorClassifier` | 10 |
| `ResultMapper` | 10 |
| `DataverseValueNormalizer` | 10 |
| `RequestValidator` | 10 |
| `UpsertLockCoordinator` | 8 |
| `EntityMappingCache` | 8 |
| `EarlyboundEntityMapper` | 8 |
| `ExternalIdResolver` | 12 |
| `LookupResolver` | 6 |
| `EntityUpsertExecutor` | 6 |

### Integration Tests (23)
| Scope | Tests | Notes |
|---|---|---|
| `UpsertOrchestrator` | 9 | FakeXrmEasy v9 in-memory Dataverse |
| `HttpUpsertTrigger` | 6 | End-to-end HTTP trigger testing |
| `ServiceBusTrigger` | 5 | Queue-driven processing |
| `DependencyInjection` | 3 | Full DI container validation |

### Test Stack
- **xUnit 2.9.2** - test framework
- **Moq 4.20.72** - mocking
- **FluentAssertions 6.12.2** - assertion library
- **FakeXrmEasy.v9 3.8.0** - in-memory Dataverse (RPL-1.5 license)
- **coverlet.collector 6.0.2** - code coverage

---

## 🚀 How to Run Locally
1. 📦 `dotnet build` (requires .NET 8 SDK).
2. ▶️ `func start` in `FUNC` folder with `local.settings.json` configured.
3. 🧪 `dotnet test TESTS/` to run the full test suite.

---

## 📡 Response Behavior
- ✅ `200 OK`: all payloads processed successfully.
- ⚠️ `400 Bad Request`: only validation failures (unknown attributes, type mismatch, missing KeyAttributes).
- ❌ `500 Internal Server Error`: at least one technical failure occurred.

ℹ️ Failure details are always returned per payload with `ErrorCategory`, `ErrorMessage`, and `ValidationErrors`.

---

## 📈 Load Testing
A PowerShell script `LoadTest.ps1` (repository root) is provided for load testing the HTTP endpoint:

```powershell
# Basic usage (requires PowerShell 7+)
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" -FunctionKey "your-function-key"

# Advanced usage with custom parameters
.\LoadTest.ps1 -FunctionUrl "https://your-function.azurewebsites.net/api/upsert" `
              -FunctionKey "your-function-key" `
              -ThreadCount 20 `
              -RequestsPerThread 50 `
              -BatchSize 10
```

Parameters:
- `FunctionUrl`: The URL of the upsert endpoint (required)
- `FunctionKey`: Azure Function key for authentication (optional)
- `ThreadCount`: Number of parallel threads (default: 10)
- `RequestsPerThread`: Number of requests per thread (default: 100)
- `BatchSize`: Number of payloads per request (default: 5)
- `RequestTimeoutSeconds`: Per-request timeout in seconds (default: 60)
- `LookupProbabilityPercent`: Chance of including a lookup per payload (default: 100)
- `DuplicateBurstSize`: Number of identical payloads to burst (default: 0 = off)
- `IncludeNegativeTests`: Include invalid JSON/type error requests (default: false)
- `ReportPath`: Optional JSON output path for detailed raw results

The script generates random account data with `KeyAttributes` and reports:
- Success/failure totals and throughput (requests/s, payloads/s)
- Response time stats including p50/p95/p99
- Status code distribution and sample errors

### 📊 Report Output Examples

```powershell
# Save a report with fixed filename
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" `
              -ThreadCount 10 `
              -RequestsPerThread 100 `
              -BatchSize 5 `
              -ReportPath ".\reports\loadtest-latest.json"

# Save a report with timestamped filename
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = ".\reports\loadtest-$timestamp.json"

.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" `
              -ThreadCount 20 `
              -RequestsPerThread 50 `
              -BatchSize 10 `
              -DuplicateBurstSize 5 `
              -IncludeNegativeTests `
              -ReportPath $reportPath
```

---

## 📝 Notes
- 🚀 This design can scale to millions of requests, within Dataverse limits. Add a queue-throttling layer (Azure Front Door / API Management) for further protection.
- 🧠 Cache is scoped per Function App instance - no distributed cache. Consider Redis if multi-instance consistency is critical.
- ℹ️ All clients must include `KeyAttributes` in payloads and lookups.
