using System.ComponentModel;

namespace DemoApi.Models;

/// <summary>Weather forecast for a city on a given day.</summary>
public class WeatherForecast
{
    /// <summary>Name of the city the forecast is for.</summary>
    public string City { get; set; } = "";

    /// <summary>Day the forecast applies to.</summary>
    public DateTime Date { get; set; }

    /// <summary>Temperature in degrees Celsius.</summary>
    public int TemperatureC { get; set; }

    /// <summary>Temperature in degrees Fahrenheit, converted from the Celsius value.</summary>
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

    /// <summary>One-word summary of the weather, such as Mild or Scorching.</summary>
    public string? Summary { get; set; }
}

/// <summary>Forecast to record for a city on a given day.</summary>
public class CreateWeatherForecastRequest
{
    /// <summary>Name of the city the forecast is for.</summary>
    public string City { get; set; } = "";

    /// <summary>Day the forecast applies to.</summary>
    public DateTime Date { get; set; }

    /// <summary>Temperature in degrees Celsius.</summary>
    [Description("Air temperature in degrees Celsius.")]
    public int TemperatureC { get; set; }

    /// <summary>One-word summary of the weather, such as Mild or Scorching.</summary>
    public string? Summary { get; set; }
}

/// <summary>Filter for the forecast search.</summary>
public class WeatherSearchFilter
{
    /// <summary>City to forecast for; the default city when omitted.</summary>
    public string? City { get; set; }

    /// <summary>First day to forecast; today when omitted.</summary>
    public DateOnly? From { get; set; }

    /// <summary>Summaries to produce one forecast each for.</summary>
    public List<string> Summaries { get; set; } = [];
}
