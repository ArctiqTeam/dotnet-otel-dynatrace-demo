### 1. Create an ActivitySource model

```csharp
using System.Diagnostics;

/// <summary>
/// It is recommended to use a custom type to hold references for ActivitySource.
/// This avoids possible type collisions with other components in the DI container.
/// </summary>
public class Instrumentation : IDisposable
{
    internal const string ActivitySourceName = "dotnet-otel";
    internal const string ActivitySourceVersion = "1.0.0";

    public Instrumentation()
    {
        this.ActivitySource = new ActivitySource(ActivitySourceName, ActivitySourceVersion);
    }

    public ActivitySource ActivitySource { get; }

    public void Dispose()
    {
        this.ActivitySource.Dispose();
    }
}
```

In `program.cs`, configure .NET instrumentation
```csharp
using System.Diagnostics;

/// <summary>
/// It is recommended to use a custom type to hold references for ActivitySource.
/// This avoids possible type collisions with other components in the DI container.
/// </summary>
var MyInstrumentation = new Instrumentation();
builder.Services.AddSingleton<Instrumentation>(MyInstrumentation);
ActivitySource MyActivitySource = MyInstrumentation.ActivitySource;
```

Load device and application metadata:
```csharp
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
```

### 2. OTEL logs

Add OTEL logging in program.cs
```csharp
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


services.AddSingleton<ILoggerFactory>(loggerFactoryOT);
```

In `Controllers/HomeController.cs`, add a `Test` action, logging a test message:

```csharp
 public IActionResult Test()
    {
        _logger.LogInformation("Test OK");
        return Ok("Test OK");
    }
```

Call to /Home/Test will show a log in Dynatrace.

### 3. OTEL tracing

Configure OTEL tracing in program.cs
```csharp
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
    });

var resourceBuilder = ResourceBuilder.CreateDefault();
configureResource!(resourceBuilder);

Sdk.CreateTracerProviderBuilder()
    .SetSampler(new AlwaysOnSampler())
    .AddSource(MyActivitySource.Name)
    .ConfigureResource(configureResource);
```

Check that calls are being traced in Dynatrace.

In `Controllers/HomeController.cs`, add a `Test` action and a private method with cutom spans:
```csharp
private void TestSpans() {
        using var activity1 = activitySource.StartActivity("test.1");

        using var activity2 = activitySource.StartActivity("test.2");
    }

    public IActionResult Test()
    {
        using (var activity0 = activitySource.StartActivity("test.a")) {
            using (var activity1 = activitySource.StartActivity("test.b")) {

                TestSpans();
            }
        }
        return Ok("Test OK");
    }
```

Call to /Home/Test will show a trace in Dynatrace, find it.

Next, we want to add some logs:

```csharp
private void TestSpans() {
        using var activity1 = activitySource.StartActivity("test.1");

        using var activity2 = activitySource.StartActivity("test.2");
        _logger.LogInformation("Test level 2");
    }

    public IActionResult Test()
    {
        using (var activity0 = activitySource.StartActivity("test.a")) {
            _logger.LogInformation("Test A");

            using (var activity1 = activitySource.StartActivity("test.b")) {
                _logger.LogInformation("Test B");

                TestSpans();
            }
        }
        return Ok("Test OK");
    }
```

Go to the trace in Dynatrace, find it. Check that the logs are correlated with the trace.

Then, let's try a more complex use case, with external and internal API calls:

```csharp
public async Task<IActionResult> MakeRequest([FromQuery(Name = "code")] string code)
    {
        using (var myActivity = activitySource.StartActivity("MakeRequest"))
        {
            if(code == "" || code is null) code = "0";

            int intCode;
            try
            {
                intCode = int.Parse(code);
            }
            catch (Exception ex)
            {
                return BadRequest("Code must be a number");
            }

            var http = new HttpClient();

            if (intCode == 0) {
                http.BaseAddress = new Uri("http://127.0.0.1:5120");
                var responseTask = http.GetAsync("/Home/MakeRequest?code=200");
                using var response = await responseTask;

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            } else {
                http.BaseAddress = new Uri("https://httpstat.us");
                var responseTask = http.GetAsync(intCode.ToString());
                using var response = await responseTask;

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
```

This endpoint will either call itself or call httpstat.us. Makeing a call without any parameter will call itself first, which will then make an external API call to httpstat.us. Make a call to /Home/MakeRequest and look at the rsulting trace in Dynatrace.

Now, let's add some custom tags to the trace, so we can have more informations about what is going on:

```csharp
public async Task<IActionResult> MakeRequest([FromQuery(Name = "code")] string code)
    {
        using (var myActivity = activitySource.StartActivity("MakeRequest"))
        {
            if(code == "" || code is null) code = "0";

            myActivity.SetTag("code", code);
            myActivity.SetTag("http.method", "GET");

            int intCode;
            try
            {
                intCode = int.Parse(code);
            }
            catch (Exception ex)
            {
                return BadRequest("Code must be a number");
            }

            var http = new HttpClient();

            if (intCode == 0) {
                http.BaseAddress = new Uri("http://127.0.0.1:5120");
                var responseTask = http.GetAsync("/Home/MakeRequest?code=200");
                using var response = await responseTask;

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            } else {
                http.BaseAddress = new Uri("https://httpstat.us");
                var responseTask = http.GetAsync(intCode.ToString());
                using var response = await responseTask;

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
```

This endpoint is still a bit complex, but we can add some events to the trace to make it more readable:

```csharp
public async Task<IActionResult> MakeRequest([FromQuery(Name = "code")] string code)
    {
        using (var myActivity = activitySource.StartActivity("MakeRequest"))
        {
            if(code == "" || code is null) code = "0";
            myActivity.SetTag("code", code);
            myActivity.SetTag("http.method", "GET");

            int intCode;
            try
            {
                intCode = int.Parse(code);
            }
            catch (Exception ex)
            {
                return BadRequest("Code must be a number");
            }

            var http = new HttpClient();

            if (intCode == 0) {
                http.BaseAddress = new Uri("http://127.0.0.1:5120");
                var responseTask = http.GetAsync("/Home/MakeRequest?code=200");
                myActivity.AddEvent(new("request.sent"));
                using var response = await responseTask;
                myActivity.AddEvent(new("request.completed"));

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            } else {
                http.BaseAddress = new Uri("https://httpstat.us");
                var responseTask = http.GetAsync(intCode.ToString());
                myActivity.AddEvent(new("request.sent"));
                using var response = await responseTask;
                myActivity.AddEvent(new("request.completed"));

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
```

Make a call to /Home/MakeRequest and look at the trace in Dynatrace. Check how the events the tags are displayed.

Finally, sometimes this request will fail. Let's add some details there too.

```csharp
public async Task<IActionResult> MakeRequest([FromQuery(Name = "code")] string code)
    {
        using (var myActivity = activitySource.StartActivity("MakeRequest"))
        {
            if(code == "" || code is null) code = "0";
            myActivity.SetTag("code", code);
            myActivity.SetTag("http.method", "GET");

            int intCode;
            try
            {
                intCode = int.Parse(code);
            }
            catch (Exception ex)
            {
                myActivity.SetStatus(ActivityStatusCode.Error, "User did not give a number as a code");
                myActivity.RecordException(ex);
                return BadRequest("Code must be a number");
            }

            var http = new HttpClient();

            if (intCode == 0) {
                http.BaseAddress = new Uri("http://127.0.0.1:5120");
                var responseTask = http.GetAsync("/Home/MakeRequest?code=200");
                myActivity.AddEvent(new("request.sent"));
                using var response = await responseTask;
                myActivity.AddEvent(new("request.completed"));

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            } else {
                http.BaseAddress = new Uri("https://httpstat.us");
                var responseTask = http.GetAsync(intCode.ToString());
                myActivity.AddEvent(new("request.sent"));
                using var response = await responseTask;
                myActivity.AddEvent(new("request.completed"));

                return Ok($"Response code: {response.StatusCode}\nMessage: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
```

Make a call to /Home/MakeRequest?code=abc. This will throw an exception, look in Dynatrace how this is displayed.

### 4. OTEL metrics

To create a new metric, first create a new Model (class) for a meter:

```csharp
using System.Diagnostics.Metrics;
using System.Diagnostics;


public class RequestMeter
{
    private static readonly string _meter_name = "request_meter";
    private static readonly string _meter_version = "1.0.0";

    private readonly Meter _meter;

    public RequestMeter(){
        _meter = new Meter(_meter_name, _meter_version);
    }

    public Meter Meter { get => _meter; }

    public static string MeterName { get => _meter_name; }
}
```

Right now, this meter measures nothing, it has no metrics assoicated, but we can add a counter metric to it:

```csharp
using System.Diagnostics.Metrics;
using System.Diagnostics;


public class RequestMeter
{
    private static readonly string _meter_name = "request_meter";
    private static readonly string _meter_version = "1.0.0";

    private readonly Meter _meter;
    private Counter<long> _request_counter;

    public RequestMeter(){
        _meter = new Meter(_meter_name, _meter_version);
        _request_counter = Meter.CreateCounter<long>("request_counter");
    }

    public Meter Meter { get => _meter; }

    public Counter<long> RequestCounter { get => _request_counter; }
    public static string MeterName { get => _meter_name; }
}
```

Next, we need to configure the OTEL metrics in the program.cs file. 
Here, we will also need to register the meter with the instrumentation so that the metrics are sent to Dynatrace:

```csharp
services.AddOpenTelemetry()
    .ConfigureResource(configureResource)
        .WithTracing(builder => { 
            ...
        })
        .WithMetrics(builder => {
        builder.AddMeter(RequestMeter.MeterName); // We register the metric here
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
```

Next, lets put that metric in action:

```csharp
public IActionResult Count()
    {
        _requestMeter.RequestCounter.Add(1);
        return Ok("OK");
    }
```

This is good, but we might want to add some dimension to the metric, for example so we can see the number of calls by IP address.

```csharp
public IActionResult Count()
    {
        _requestMeter.RequestCounter.Add(1, new KeyValuePair<string, object?>("ip", Request.HttpContext.Connection.RemoteIpAddress.ToString()));
        return Ok("OK");
    }
```