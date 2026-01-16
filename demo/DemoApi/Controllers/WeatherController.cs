using Microsoft.AspNetCore.Mvc;
using DemoApi.Models;

namespace DemoApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WeatherController : ControllerBase
{
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    /// <summary>
    /// Gets a list of weather forecasts.
    /// </summary>
    [HttpGet]
    public Task<IEnumerable<WeatherForecast>> GetAll([FromQuery] int days = 5)
    {
        var forecasts = Enumerable.Range(1, days).Select(index => new WeatherForecast
        {
            City = "Default",
            Date = DateTime.Now.AddDays(index),
            TemperatureC = Random.Shared.Next(-20, 55),
            Summary = Summaries[Random.Shared.Next(Summaries.Length)]
        });

        return Task.FromResult(forecasts);
    }

    /// <summary>
    /// Gets weather forecast for a specific city.
    /// </summary>
    [HttpGet("{city}")]
    public Task<WeatherForecast> GetByCity(string city, [FromQuery] int daysAhead = 1)
    {
        var forecast = new WeatherForecast
        {
            City = city,
            Date = DateTime.Now.AddDays(daysAhead),
            TemperatureC = Random.Shared.Next(-20, 55),
            Summary = Summaries[Random.Shared.Next(Summaries.Length)]
        };

        return Task.FromResult(forecast);
    }

    /// <summary>
    /// Creates a new weather forecast entry.
    /// </summary>
    [HttpPost]
    public Task<WeatherForecast> Create([FromBody] CreateWeatherForecastRequest request)
    {
        var forecast = new WeatherForecast
        {
            City = request.City,
            Date = request.Date,
            TemperatureC = request.TemperatureC,
            Summary = request.Summary
        };

        return Task.FromResult(forecast);
    }
}
