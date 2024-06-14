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