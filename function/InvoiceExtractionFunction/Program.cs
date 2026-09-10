using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.AI.DocumentIntelligence;
using InvoiceProcessing.ExtractionFunction.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureAppConfiguration(config =>
    {
        // Local dev reads local.settings.json (copied to local.settings.json, git-ignored);
        // in Azure these come from App Settings, which in turn resolve @Microsoft.KeyVault(...)
        // references transparently — the code never sees a difference between the two.
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Single credential instance reused everywhere. In Azure this resolves to the
        // Function App's system-assigned managed identity automatically; locally it falls
        // back through the DefaultAzureCredential chain (e.g. `az login` / VS Code sign-in).
        // No client secrets or storage keys are ever referenced in this project.
        services.AddSingleton(new DefaultAzureCredential());

        services.AddSingleton(sp =>
        {
            var credential = sp.GetRequiredService<DefaultAzureCredential>();
            var blobServiceUri = new Uri(configuration["InvoiceStorage__blobServiceUri"]
                ?? throw new InvalidOperationException("InvoiceStorage__blobServiceUri is not configured."));
            return new BlobServiceClient(blobServiceUri, credential);
        });

        services.AddSingleton(sp =>
        {
            var credential = sp.GetRequiredService<DefaultAzureCredential>();
            var documentIntelligenceEndpoint = new Uri(configuration["DocumentIntelligence__endpoint"]
                ?? throw new InvalidOperationException("DocumentIntelligence__endpoint is not configured."));
            return new DocumentIntelligenceClient(documentIntelligenceEndpoint, credential);
        });

        services.AddScoped<IInvoiceExtractionService, InvoiceExtractionService>();

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

host.Run();
