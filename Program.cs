using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.HttpOverrides;

ILoggerFactory loggerFactoryOT;


var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var MyInstrumentation = new Instrumentation();
builder.Services.AddSingleton<Instrumentation>(MyInstrumentation);
ActivitySource MyActivitySource = MyInstrumentation.ActivitySource;

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

IConfiguration configuration = configBuilder.Build();

// Add services to the container.
services.AddControllersWithViews();

// Load Dynatrace Metadata
List<KeyValuePair<string, object>> metadata = new List<KeyValuePair<string, object>>();
foreach (string name in configuration.GetSection("Otlp:MetadataFiles").Get<string[]>())
{
    try
    {
        foreach (string line in System.IO.File.ReadAllLines(name.StartsWith("/var") ? name : System.IO.File.ReadAllText(name)))
        {
            var keyvalue = line.Split("=");
            metadata.Add(new KeyValuePair<string, object>(keyvalue[0], keyvalue[1]));
        }
    }
    catch { }
}

Action<ResourceBuilder> configureResource = r => r
    .AddService(serviceName: MyActivitySource.Name)
    .AddAttributes(metadata);

// Add OTEL services
services.AddOpenTelemetry()
    .ConfigureResource(configureResource)
    .WithTracing(builder => { 
        builder.SetSampler(new AlwaysOnSampler());
        builder.AddSource(MyActivitySource.Name);
        builder.AddAspNetCoreInstrumentation();
        if (configuration.GetValue<string>("Otlp:TracesEndpoint") != "") {
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(configuration.GetValue<string>("Otlp:TracesEndpoint")); 
                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                options.Headers = configuration.GetValue<string>("Otlp:Headers");
                options.ExportProcessorType = OpenTelemetry.ExportProcessorType.Simple;
            });
        }
    })
    .WithMetrics(builder => {
        builder.AddMeter(RequestMeter.MeterName);
        if (configuration.GetValue<string>("Otlp:MetricsEndpoint") != "") {
            builder.AddOtlpExporter((OtlpExporterOptions exporterOptions, MetricReaderOptions readerOptions) =>
            {
                exporterOptions.Endpoint = new Uri(configuration.GetValue<string>("Otlp:MetricsEndpoint"));
                exporterOptions.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                exporterOptions.Headers = configuration.GetValue<string>("Otlp:Headers");
                readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
            });
        }
    });

var resourceBuilder = ResourceBuilder.CreateDefault();
configureResource!(resourceBuilder);

Sdk.CreateTracerProviderBuilder()
    .SetSampler(new AlwaysOnSampler())
    .AddSource(MyActivitySource.Name)
    .ConfigureResource(configureResource);

services.AddSingleton<RequestMeter>();

// Configure OTEL logger
loggerFactoryOT = LoggerFactory.Create(builder =>
{
   builder
       .AddOpenTelemetry(builder =>
       {
           builder.SetResourceBuilder(resourceBuilder);
           if (configuration.GetValue<string>("Otlp:LogsEndpoint") != ""){
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(configuration.GetValue<string>("Otlp:LogsEndpoint"));
                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                options.Headers = configuration.GetValue<string>("Otlp:Headers");
                options.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;
            });
           }
       })
       .AddConsole();
});


var logger = loggerFactoryOT.CreateLogger<Program>();
services.AddSingleton<ILoggerFactory>(loggerFactoryOT);
// services.AddSingleton(logger);


// Test logger
logger.LogInformation(eventId: 123, "OtelTest");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseForwardedHeaders();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
