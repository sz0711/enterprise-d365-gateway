using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace enterprise_d365_gateway.Functions
{
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Text.Json;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatform.Dataverse.Client;
    using Microsoft.Xrm.Sdk;

    namespace enterprise_d365_gateway.Functions
    {
        public class RuntimeDiagnosticsTrigger
        {
            private readonly ILogger<RuntimeDiagnosticsTrigger> _logger;

            public RuntimeDiagnosticsTrigger(ILogger<RuntimeDiagnosticsTrigger> logger)
            {
                _logger = logger;
            }

            [Function("RuntimeDiagnosticsHttp")]
            public async Task<HttpResponseData> RunAsync(
                [HttpTrigger(AuthorizationLevel.Function, "get", Route = "diagnostics/runtime")] HttpRequestData req)
            {
                var correlationId = req.Headers.TryGetValues("x-correlation-id", out var headerValues)
                    ? headerValues.FirstOrDefault() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");

                // Force-load critical assemblies so the response reflects actual runtime binding.
                _ = typeof(ServiceClient).Assembly;
                _ = typeof(Entity).Assembly;

                var loadedAssemblies = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .OrderBy(a => a.GetName().Name)
                    .ToDictionary(
                        a => a.GetName().Name ?? "unknown",
                        a => new
                        {
                            version = a.GetName().Version?.ToString(),
                            location = SafeGetAssemblyLocation(a)
                        },
                        StringComparer.OrdinalIgnoreCase);

                var inspectedAssemblies = new[]
                {
                "enterprise-d365-gateway",
                "MODEL",
                "Microsoft.PowerPlatform.Dataverse.Client",
                "Microsoft.Xrm.Sdk",
                "System.Runtime.Serialization",
                "System.Runtime.Serialization.Primitives"
            }
                .Select(name => new
                {
                    name,
                    loaded = loadedAssemblies.TryGetValue(name, out var metadata),
                    version = loadedAssemblies.TryGetValue(name, out var metadata2) ? metadata2.version : null,
                    location = loadedAssemblies.TryGetValue(name, out var metadata3) ? metadata3.location : null
                })
                .ToList();

                var entryAssembly = Assembly.GetEntryAssembly();
                var targetFramework = entryAssembly
                    ?.GetCustomAttribute<TargetFrameworkAttribute>()
                    ?.FrameworkName;

                var idcsType = Type.GetType("System.Runtime.Serialization.IDataContractSurrogate, System.Runtime.Serialization", throwOnError: false);

                var payload = new
                {
                    correlationId,
                    runtime = new
                    {
                        frameworkDescription = RuntimeInformation.FrameworkDescription,
                        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        osDescription = RuntimeInformation.OSDescription,
                        environmentVersion = Environment.Version.ToString(),
                        targetFramework = targetFramework ?? "unknown",
                        baseDirectory = AppContext.BaseDirectory,
                        currentDirectory = Environment.CurrentDirectory
                    },
                    checks = new
                    {
                        iDataContractSurrogateTypeResolved = idcsType != null,
                        iDataContractSurrogateAssemblyQualifiedName = idcsType?.AssemblyQualifiedName
                    },
                    assemblies = inspectedAssemblies
                };

                _logger.LogInformation("Runtime diagnostics requested. CorrelationId={CorrelationId}", correlationId);

                var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");
                response.Headers.Add("x-correlation-id", correlationId);
                await response.WriteStringAsync(JsonSerializer.Serialize(payload));
                return response;
            }

            private static string SafeGetAssemblyLocation(Assembly assembly)
            {
                try
                {
                    return string.IsNullOrWhiteSpace(assembly.Location) ? "<dynamic>" : assembly.Location;
                }
                catch
                {
                    return "<unavailable>";
                }
            }
        }
    }
}
