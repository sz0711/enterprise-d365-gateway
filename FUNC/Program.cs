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
    .ValidateOnStart();

builder.Services.AddDataverseIntegration();

builder.Build().Run();
