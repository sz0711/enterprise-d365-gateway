using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using enterprise_d365_gateway.Extensions;
using enterprise_d365_gateway.Models;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();


builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services
    .AddOptions<DataverseOptions>()
    .Bind(builder.Configuration.GetSection("Dataverse"))
    .ValidateDataAnnotations()
    .Validate(options => Uri.TryCreate(options.Url, UriKind.Absolute, out _), "Dataverse:Url must be a valid absolute URI.")
    .Validate(options =>
    {
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }, "Dataverse:Url must use the HTTPS scheme.")
    .Validate(options => options.MaxRequestBytes >= 1024, "Dataverse:MaxRequestBytes must be at least 1024 bytes.")
    .Validate(options => options.MaxBatchItems >= 1, "Dataverse:MaxBatchItems must be at least 1.")
    .ValidateOnStart();

builder.Services.AddDataverseIntegration();

builder.Build().Run();
