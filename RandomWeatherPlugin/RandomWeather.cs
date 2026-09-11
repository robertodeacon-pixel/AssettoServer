using AssettoServer.Server.Configuration;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Weather;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using Microsoft.Extensions.Hosting;
using Serilog;
using YamlDotNet.Serialization;

namespace RandomWeatherPlugin;

public class RandomWeather : BackgroundService
{
    private struct WeatherWeight
    {
        internal WeatherFxType Weather { get; init; }
        internal float PrefixSum { get; init; }
    }

    private readonly WeatherManager _weatherManager;
    private readonly IWeatherTypeProvider _weatherTypeProvider;
    private RandomWeatherConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly List<WeatherWeight> _weathers = [];
    private float _previousRainIntensity;
    private bool _drying;
    private bool _wetting;
    private bool _firstSession = true;
    private CancellationTokenSource? _weatherDelayCts;
    private volatile bool _sessionChanging;
    private readonly EntryCarManager _entryCarManager;

    public RandomWeather(
        RandomWeatherConfiguration configuration,
        WeatherManager weatherManager,
        IWeatherTypeProvider weatherTypeProvider,
        SessionManager sessionManager,
        EntryCarManager entryCarManager)
    {
        _configuration = configuration;
        _weatherManager = weatherManager;
        _weatherTypeProvider = weatherTypeProvider;
        _sessionManager = sessionManager;
        _sessionManager.SessionChanged += OnSessionChanged;
        _entryCarManager = entryCarManager;

        if (_configuration.Mode == RandomWeatherMode.TransitionTable)
        {
            if (_configuration.WeatherTransitions.Count == 0)
                throw new ConfigurationException("No entries were found in the WeatherTransitions list");

            // Initialise the transition table from the first configured weather.
            var start = _weatherTypeProvider.GetWeatherType(_configuration.WeatherTransitions.First().Key);
            RecalculateWeights(_configuration.WeatherTransitions[start.WeatherFxType]);
        }
        else if (_configuration.Mode == RandomWeatherMode.Default)
        {
            if (_configuration.WeatherWeights.Count == 0)
                throw new ConfigurationException("No entries were found in the WeatherWeights list");

            _configuration.WeatherWeights[WeatherFxType.None] = 0;
            RecalculateWeights(_configuration.WeatherWeights);

            // Cycle 250 times using var to avoid type resolution issues
            WeatherType next = null!;
            for (int i = 0; i < 250; i++)
            {
                next = _weatherTypeProvider.GetWeatherType(PickRandom());
            }

            // Create the cancellation source for the initial weather sequence.
            _weatherDelayCts =
                new CancellationTokenSource();

            _ = ApplyInitialWeather(next, _weatherDelayCts.Token);
        }
    }
    
    private static string WeatherDescription(WeatherFxType weather)
    {
        string name = weather.ToString();

        return System.Text.RegularExpressions.Regex.Replace(
            name,
            "(?<!^)([A-Z])",
            " $1");
    }
    private async Task ApplyInitialWeather(
        WeatherType next,
        CancellationToken token)
    {
        await Task.Delay(1000, token);

        _entryCarManager.BroadcastChat(
            $"Please allow 10 seconds for the session weather to be initialised to {WeatherDescription(next.WeatherFxType)}.");

        await Task.Delay(1000, token);
        _weatherManager.SetCspWeather(WeatherFxType.Clear, 0);

        await Task.Delay(2000, token);
        _weatherManager.SetCspWeather(next.WeatherFxType, 0);
    }

    private void RecalculateWeights(Dictionary<WeatherFxType, float> input)
    {
        // BUG FIX: Added Clear() so the list does not grow infinitely on every single recalculation
        _weathers.Clear();

        float weightSum = input.Select(w => w.Value).Sum();
        float prefixSum = 0.0f;

        foreach (var (weather, weight) in input)
        {
            if (weight > 0)
            {
                prefixSum += weight / weightSum;
                _weathers.Add(new WeatherWeight
                {
                    Weather = weather,
                    PrefixSum = prefixSum,
                });
            }
        }

        _weathers.Sort((a, b) =>
        {
            if (a.PrefixSum < b.PrefixSum) return -1;
            if (a.PrefixSum > b.PrefixSum) return 1;
            return 0;
        });
    }

    private bool ReloadSessionWeather(string filename)
    {
        try
        {
            string cfgFolder = Path.Combine(
                AppContext.BaseDirectory,
                "cfg");

            string source = Path.Combine(
                cfgFolder,
                "sessions",
                filename);

            if (!File.Exists(source))
            {
                Log.Information(
                    "No session weather file found: {File}. Using default configuration.",
                    filename);
                return false;
            }

            string destination = Path.Combine(
                cfgFolder,
                "plugin_random_weather_cfg.yml");

            using var reader = File.OpenText(source);

            var deserializer = new DeserializerBuilder().Build();

            _configuration =
                deserializer.Deserialize<RandomWeatherConfiguration>(reader);

            File.Copy(source, destination, true);

            Log.Information(
                "Mode = {Mode}, transitions = {Count}",
                _configuration.Mode,
                _configuration.WeatherTransitions.Count);

            //
            // rebuild the weather generator
            //

            if (_configuration.Mode == RandomWeatherMode.TransitionTable)
            {
                var start =
                    _weatherTypeProvider.GetWeatherType(
                        _configuration.WeatherTransitions.First().Key);

                RecalculateWeights(
                    _configuration.WeatherTransitions[start.WeatherFxType]);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "Failed to reload session weather.");
            return false;
        }

        return true;
    }

    private void OnSessionChanged(
        SessionManager sender,
        SessionChangedEventArgs e)
    {
        if (e.PreviousSession != null &&
            e.PreviousSession.Configuration.Id == e.NextSession.Configuration.Id)
        {
            Log.Information("Session restart detected, so weather carries on.");
            return;
        }

        string file = e.NextSession.Configuration.Type switch
        {
            SessionType.Practice => "PRACTICE.yml",
            SessionType.Qualifying => "QUALI.yml",
            SessionType.Race => "RACE.yml",
            _ => ""
        };

        _sessionChanging = true;

        // Cancel EVERYTHING that was waiting from the previous session.
        _weatherDelayCts?.Cancel();
        _weatherDelayCts?.Dispose();

        // Create a new cancellation source for this session.
        _weatherDelayCts = new CancellationTokenSource();
        CancellationToken token = _weatherDelayCts.Token;

        bool usingSessionYaml = false;

        if (!string.IsNullOrEmpty(file))
            usingSessionYaml = ReloadSessionWeather(file);

        WeatherType next;

        if (_firstSession)
        {
            next = FastForward(250);
            _firstSession = false;
        }
        else if (usingSessionYaml)
        {
            next = FastForward(250);
        }
        else
        {
            next = FastForward(Random.Shared.Next(5, 11));
        }

        // This sequence is now cancelled automatically if another
        // session change occurs before it has finished.
        _ = ApplyInitialWeather(next, token);

        _sessionChanging = false;
    }

    private WeatherType FastForward(int steps)
    {
        WeatherType next = _weatherManager.CurrentWeather.Type;

        for (int i = 0; i < steps; i++)
        {
            WeatherFxType weather = PickRandom();
            next = _weatherTypeProvider.GetWeatherType(weather);

            if (_configuration.Mode == RandomWeatherMode.TransitionTable)
                RecalculateWeights(_configuration.WeatherTransitions[weather]);
        }

        return next;
    }

    private WeatherFxType PickRandom()
    {
        if (_weathers.Count == 0) return WeatherFxType.None;

        // Encourage momentum depending how dry/drying it is etc.

        float rngExp = 1;

        if (_drying)
        {
            rngExp = 1.6f;
            if (_previousRainIntensity < 0.1f)
                rngExp = 3.2f;
            else if (_previousRainIntensity < 0.36f)
                rngExp = 2.1f;
        }
        else if (_wetting)
        {
            rngExp = 0.90f;
            if (_previousRainIntensity < 0.1f)
                rngExp = 0.35f;
            else if (_previousRainIntensity < 0.36f)
                rngExp = 0.85f;
        }

        float rng = Random.Shared.NextSingle();

        rng = MathF.Pow(rng, rngExp);

        WeatherFxType weather = WeatherFxType.None;
        int begin = 0, end = _weathers.Count - 1;

        while (begin <= end)
        {
            int i = (begin + end) / 2;

            if (_weathers[i].PrefixSum <= rng)
            {
                begin = i + 1;
            }
            else
            {
                end = i - 1;
                weather = _weathers[i].Weather;
            }
        }

        float rain = _weatherTypeProvider.GetWeatherType(weather).RainIntensity;
        _wetting = rain > _previousRainIntensity && rain < 1.0f;
        _drying = (!_wetting) && rain > 0.0f;
        _previousRainIntensity = rain;

        return weather;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int weatherDuration;
        int transitionDuration;
        bool firstTime = true;
        int DEBUGTOT = 0;
        int DRYTOT = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_sessionChanging)
            {
                await Task.Delay(10, stoppingToken);
                continue;
            }

            try
            {
                if (firstTime)
                {
                    firstTime = false;
                    weatherDuration = 0;
                    transitionDuration = 0;
                }
                else
                {
                    weatherDuration = Random.Shared.Next(
                        _configuration.MinWeatherDurationMilliseconds,
                        _configuration.MaxWeatherDurationMilliseconds);
                    
                    var currentRainIntensity = _previousRainIntensity;
                    var next = PickRandom();
                    var nextWeatherType = _weatherTypeProvider.GetWeatherType(next);
                    var durationMult = 1.0f;
                    
                    // Shorten weather duration when wetness is increasing
                    // and the current rain intensity is still relatively low.
                    if (nextWeatherType.RainIntensity > currentRainIntensity)
                    {
                        if (currentRainIntensity < 0.08f)
                            durationMult = 0.4f + (Random.Shared.NextSingle() * 0.6f);

                        else if (currentRainIntensity < 0.26f)
                            durationMult = 0.7f + (Random.Shared.NextSingle() * 0.3f);
                    }
                    
                    weatherDuration = (int)(weatherDuration * durationMult);
                    
                    if (weatherDuration < 25000)
                        weatherDuration = 25000;

                    transitionDuration = Random.Shared.Next(
                        _configuration.MinTransitionDurationMilliseconds,
                        _configuration.MaxTransitionDurationMilliseconds);

                    DEBUGTOT++;

                    switch (nextWeatherType.WeatherFxType)
                    {
                        case WeatherFxType.Clear:
                        case WeatherFxType.FewClouds:
                        case WeatherFxType.ScatteredClouds:
                        case WeatherFxType.BrokenClouds:
                        case WeatherFxType.OvercastClouds:
                            DRYTOT++;
                            break;
                    }

                    // Use the SAME cancellation token as the main weather delay.
                    var token = _weatherDelayCts?.Token ?? stoppingToken;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(150000, token);

                            _weatherManager.SetCspWeather(
                                next,
                                transitionDuration / 1000);
                        }
                        catch (OperationCanceledException)
                        {
                            // Session changed. Ignore this old weather.
                        }
                    }, token);

                    if (_configuration.Mode == RandomWeatherMode.TransitionTable)
                        RecalculateWeights(_configuration.WeatherTransitions[next]);
                }

                // Wait until it's time for the next weather,
                // or until OnSessionChanged() cancels us.
                if (_weatherDelayCts == null)
                {
                    _weatherDelayCts =
                        CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                }

                await Task.Delay(
                    transitionDuration + weatherDuration,
                    _weatherDelayCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Session changed.
                // Go straight round the loop again using the new configuration.
                continue;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during random weather update");
            }
        }
    }
}
