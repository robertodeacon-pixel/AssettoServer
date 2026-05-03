using System;
using AssettoServer.Shared.Utils;

namespace AssettoServer.Server.Weather;

public class RainHelper
{
    private float _prevWetness = 0;
    private float _prevPuddles = 0;
    private float _prevHumidity = -1;

    private double _logTimer = 0;

    private void CalcGrip(WeatherData condition, double baseGrip, double rainTrackGripReduction)
    {
        condition.TrackGrip = (float)(
            baseGrip
            - MathUtils.Lerp(0, rainTrackGripReduction * 0.3, condition.RainWetness)
            - MathUtils.Lerp(0, rainTrackGripReduction * 0.7, condition.RainWater)
        );
    }

    private void CalcWater(WeatherData weather, double sun, double dt, bool calcHumidity = false)
    {
        double timeScale = dt; // dt is already in seconds

        // Drying factor
        double myDrying = (Math.Max(0.2, Math.Min(1, weather.TemperatureRoad / 40.0))
                           + (sun / 10.0)
                           - (weather.RainIntensity / 10.0)
                           - (weather.Humidity / 10.0)) / 2.0;

        // Wetness
        double wetUpExp = 1.15 + myDrying;
        double wetDnExp = 2.0 - myDrying;
        double deltaWet = weather.RainIntensity - _prevWetness;
        weather.RainWetness = deltaWet >= 0
            ? (float)(_prevWetness + Math.Pow(deltaWet, wetUpExp) * timeScale)
            : (float)(_prevWetness - Math.Pow(-deltaWet, wetDnExp) * timeScale);
        weather.RainWetness = (float)Math.Max(0, weather.RainWetness - 0.0001 * timeScale);

        // Puddles
        double pudUpExp = 2.4 + myDrying;
        double pudDnExp = 3.5 - myDrying;
        double deltaPud = weather.RainIntensity - _prevPuddles;
        weather.RainWater = deltaPud >= 0
            ? (float)(_prevPuddles + Math.Pow(deltaPud, pudUpExp) * timeScale)
            : (float)(_prevPuddles - Math.Pow(-deltaPud, pudDnExp) * timeScale);
        weather.RainWater = (float)Math.Max(0, weather.RainWater - 0.0001 * timeScale);

        // Humidity
        double humUpExp = 1.8 + myDrying;
        double humDnExp = 3.8 - myDrying;
        double humidityBase = 0.6 - myDrying / 4;
        double humidityTops = 1 - myDrying / 4;
        double humidityTarget = (humidityTops - humidityBase) * weather.RainIntensity + humidityBase;
        if (_prevHumidity < 0) _prevHumidity = (float)humidityBase;
        double deltaHum = humidityTarget - _prevHumidity;
        weather.Humidity = (float)(_prevHumidity + (deltaHum >= 0 ? Math.Pow(deltaHum, humUpExp) : -Math.Pow(-deltaHum, humDnExp)) * timeScale);
        weather.Humidity = (float)Math.Max(0, weather.Humidity - 0.0001 * timeScale);

        // Debugging log every 5 seconds
        _logTimer += dt;
        if (_logTimer >= 5.0)
        {
            _logTimer = 0;
            // Uncomment for console debugging
            //Console.WriteLine($">>>>");
            //Console.WriteLine($"Rain: {weather.RainIntensity:F4} | Wet: {weather.RainWetness:F4} | Puddle: {weather.RainWater:F4} | Humidity: {weather.Humidity:F4}");
            //Console.WriteLine($"(DryK: {myDrying:F3}) | (WetUp: {wetUpExp:F3} | WetDn: {wetDnExp:F3} | PudUp: {pudUpExp:F3} | PudDn: {pudDnExp:F3})");
        }

        // Save previous values
        _prevWetness = weather.RainWetness;
        _prevPuddles = weather.RainWater;
        _prevHumidity = weather.Humidity;
    }

    public void Update(WeatherData weather, double baseGrip, double rainTrackGripReduction, long dt)
    {
        // Handle transition
        if (weather.Type.WeatherFxType != weather.UpcomingType.WeatherFxType)
        {
            weather.TransitionValueInternal += dt / weather.TransitionDuration;

            if (weather.TransitionValueInternal >= 1)
            {
                weather.Type = weather.UpcomingType;
                weather.UpcomingType = weather.Type;
                weather.TransitionValueInternal = 0;
                weather.TransitionValue = 0;
                weather.RainIntensity = weather.Type.RainIntensity;
            }
            else
            {
                weather.TransitionValue = (ushort)(MathUtils.Smoothstep(0, 1, weather.TransitionValueInternal) * ushort.MaxValue);
                weather.RainIntensity = (float)MathUtils.Lerp(weather.Type.RainIntensity, weather.UpcomingType.RainIntensity, weather.TransitionValueInternal);
            }
        }

        // Interpolated sun
        double sunInterpolated = MathUtils.Lerp(weather.Type.Sun, weather.UpcomingType.Sun, weather.TransitionValueInternal);

        // Calculate water/grip
        CalcWater(weather, sunInterpolated, dt / 1000.0, true);
        CalcGrip(weather, baseGrip, rainTrackGripReduction);
    }
}
