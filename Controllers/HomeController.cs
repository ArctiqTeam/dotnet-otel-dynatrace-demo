using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using dotnet_otel.Models;
using OpenTelemetry.Trace;

namespace dotnet_otel.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly RequestMeter _requestMeter;
    public ActivitySource activitySource;

    public HomeController(ILogger<HomeController> logger, Instrumentation instrumentation, RequestMeter requestMeter)
    {
        _logger = logger;
        _requestMeter = requestMeter;
        activitySource = instrumentation.ActivitySource;
    }

    public IActionResult Index()
    {
        return View();
    }

    private void TestSpans() {
        using var activity1 = activitySource.StartActivity("test.1");
        // _logger.LogInformation("Test level 1");

        using var activity2 = activitySource.StartActivity("test.2").SetParentId(activity1.TraceId, activity1.SpanId);
        // _logger.LogInformation("Test level 2");
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

    public IActionResult Count()
    {
        _requestMeter.RequestCounter.Add(1, new KeyValuePair<string, object?>("ip", Request.HttpContext.Connection.RemoteIpAddress.ToString()));
        return Ok("OK");
    }

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

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
