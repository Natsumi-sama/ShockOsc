using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using CoreOSC;
using CoreOSC.IO;
using Serilog;
using Serilog.Events;
using ShockLink.ShockOsc.Models;
using ShockLink.ShockOsc.Utils;

#pragma warning disable CS4014

namespace ShockLink.ShockOsc;

public static class ShockOsc
{
    private static ILogger _logger = null!;
    private static bool _isAfk;
    private static bool _isMuted;
    private static readonly Random Random = new();
    public static readonly ConcurrentDictionary<string, Shocker> Shockers = new();

    public static readonly List<string> ShockerParams = new()
    {
        "Stretch",
        "IsGrabbed",
        "IsPosed",
        "Angle",
        "Squish",
        "Cooldown",
        "Active",
        "Intensity"
    };

    private static readonly UdpClient ReceiverClient = new((int)Config.ConfigInstance.Osc.ReceivePort);

    private static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .Filter.ByExcluding(ev =>
                ev.Exception is InvalidDataException a && a.Message.StartsWith("Invocation provides"))
            .WriteTo.Console(LogEventLevel.Information,
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // ReSharper disable once RedundantAssignment
        var isDebug = false;
#if DEBUG
        isDebug = true;
#endif
        if ((args.Length > 0 && args[0] == "--debug") || isDebug)
        {
            Log.Information("Debug logging enabled");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Filter.ByExcluding(ev =>
                    ev.Exception is InvalidDataException a && a.Message.StartsWith("Invocation provides"))
                .WriteTo.Console(LogEventLevel.Debug,
                    "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        _logger = Log.ForContext(typeof(ShockOsc));

        _logger.Information("Starting ShockLink.ShockOsc version {Version}",
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "error");
        _logger.Information("Found shockers: {Shockers}", Config.ConfigInstance.ShockLink.Shockers.Select(x => x.Key));

        _logger.Information("Init user hub...");
        await UserHubClient.InitializeAsync();

        _logger.Information("Connecting UDP Clients...");

        // Start tasks
        SlTask.Run(ReceiverLoopAsync);
        SlTask.Run(SenderLoopAsync);
        SlTask.Run(CheckLoop);

        foreach (var (shockerName, shockerId) in Config.ConfigInstance.ShockLink.Shockers)
            Shockers.TryAdd(shockerName, new Shocker(shockerId));

        _logger.Information("Ready");
        await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
    }

    private static async Task ReceiverLoopAsync()
    {
        while (true) await ReceiveLogic();
    }

    private static async Task ReceiveLogic()
    {
        var received = await ReceiverClient.ReceiveMessageAsync();
        var addr = received.Address.Value;
        _logger.Verbose("Received message: {Addr}", addr);

        switch (addr)
        {
            case "/avatar/change":
                var avatarId = received.Arguments.ElementAtOrDefault(0);
                OscConfigLoader.OnAvatarChange(avatarId?.ToString());
                return;
            case "/avatar/parameters/AFK":
                _isAfk = received.Arguments.ElementAtOrDefault(0) is OscTrue;
                _logger.Debug("Afk: {State}", _isAfk);
                return;
            case "/avatar/parameters/MuteSelf":
                _isMuted = received.Arguments.ElementAtOrDefault(0) is OscTrue;
                _logger.Debug("Muted: {State}", _isMuted);
                return;
        }

        if (!addr.StartsWith("/avatar/parameters/ShockOsc/"))
            return;

        var pos = addr.Substring(28, addr.Length - 28);
        var lastUnderscoreIndex = pos.LastIndexOf('_') + 1;
        var action = string.Empty;
        if (lastUnderscoreIndex != 0)
            action = pos.Substring(lastUnderscoreIndex, pos.Length - lastUnderscoreIndex);

        var shockerName = pos;
        if (ShockerParams.Contains(action))
            shockerName = pos.Substring(0, lastUnderscoreIndex - 1);

        if (!Shockers.ContainsKey(shockerName))
        {
            if (!Config.ConfigInstance.ShockLink.Shockers.ContainsKey(shockerName))
            {
                _logger.Warning("Unknown shocker {Shocker}", shockerName);
                return;
            }

            Shockers.TryAdd(shockerName, new Shocker(Config.ConfigInstance.ShockLink.Shockers[shockerName]));
        }

        var shocker = Shockers[shockerName];

        var value = received.Arguments.ElementAtOrDefault(0);
        switch (action)
        {
            case "Stretch":
                if (value is float stretch)
                    shocker.LastStretchValue = stretch;
                return;
            case "IsGrabbed":
                var isGrabbed = value is OscTrue;
                if (shocker.IsGrabbed && !isGrabbed)
                {
                    // on physbone release
                    if (shocker.LastStretchValue != 0)
                    {
                        shocker.TriggerMethod = TriggerMethod.PhysBoneRelease;
                        shocker.LastActive = DateTime.UtcNow;
                    }
                    else if (Config.ConfigInstance.Behaviour.VibrateWhileBoneHeld)
                    {
                        CancelAction(shocker);
                    }
                }

                shocker.IsGrabbed = isGrabbed;
                return;
            case "":
                break;
            default:
                return;
        }

        if (value is OscTrue)
        {
            shocker.TriggerMethod = TriggerMethod.Manual;
            shocker.LastActive = DateTime.UtcNow;
        }
        else shocker.TriggerMethod = TriggerMethod.None;
    }

    private static async Task SenderLoopAsync()
    {
        while (true)
        {
            await SendParams();
            await Task.Delay(300);
        }
    }

    private static async Task SendParams()
    {
        foreach (var (shockerName, shocker) in Shockers)
        {
            var isActive = shocker.LastExecuted.AddMilliseconds(shocker.LastDuration) > DateTime.UtcNow;
            var isActiveOrOnCooldown =
                shocker.LastExecuted.AddMilliseconds(Config.ConfigInstance.Behaviour.CooldownTime)
                    .AddMilliseconds(shocker.LastDuration) > DateTime.UtcNow;
            if (!isActiveOrOnCooldown && shocker.LastIntensity > 0)
                shocker.LastIntensity = 0;

            if (shocker.HasActiveParam)
            {
                await SenderClient.SendOscMessage($"/avatar/parameters/ShockOsc/{shockerName}_Active",
                    new object[] { isActive ? OscTrue.True : OscFalse.False });

                if (isActive)
                    _logger.Debug("Set param: Active: {Active}", isActive ? "true" : "false");
            }

            if (shocker.HasCooldownParam)
            {
                var onCoolDown = !isActive && isActiveOrOnCooldown;

                await SenderClient.SendOscMessage($"/avatar/parameters/ShockOsc/{shockerName}_Cooldown",
                    new object[] { onCoolDown ? OscTrue.True : OscFalse.False });

                if (onCoolDown)
                    _logger.Debug("Set param: Cooldown: {Cooldown}", onCoolDown ? "true" : "false");
            }

            if (shocker.HasIntensityParam)
            {
                await SenderClient.SendOscMessage($"/avatar/parameters/ShockOsc/{shockerName}_Intensity",
                    new object[] { shocker.LastIntensity });

                if (shocker.LastIntensity > 0)
                    _logger.Debug("Set param: Intensity: {Intensity}", shocker.LastIntensity);
            }
        }
    }

    private static async Task CheckLoop()
    {
        while (true)
        {
            await CheckLogic();
            await Task.Delay(20);
        }
    }

    private static async Task CheckLogic()
    {
        var config = Config.ConfigInstance.Behaviour;
        foreach (var (pos, shocker) in Shockers)
        {
            var isActiveOrOnCooldown =
                shocker.LastExecuted.AddMilliseconds(Config.ConfigInstance.Behaviour.CooldownTime)
                    .AddMilliseconds(shocker.LastDuration) > DateTime.UtcNow;

            if (shocker.TriggerMethod == TriggerMethod.None && config.VibrateWhileBoneHeld && !isActiveOrOnCooldown &&
                shocker.IsGrabbed &&
                shocker.LastVibration < DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(300)))
            {
                var vibrationIntensity = shocker.LastStretchValue * 100f;
                if (vibrationIntensity < 1)
                    vibrationIntensity = 1;
                _logger.Debug("Vibrating {Shocker} at {Intensity}", pos, vibrationIntensity);
                shocker.LastVibration = DateTime.UtcNow;
                await UserHubClient.Control(new Control
                {
                    Id = shocker.Id,
                    Intensity = (byte)vibrationIntensity,
                    Duration = 1000,
                    Type = ControlType.Vibrate
                });
            }

            if (shocker.TriggerMethod == TriggerMethod.None)
                continue;

            if (shocker.TriggerMethod == TriggerMethod.Manual &&
                shocker.LastActive.AddMilliseconds(config.HoldTime) > DateTime.UtcNow)
                continue;

            if (isActiveOrOnCooldown)
            {
                shocker.TriggerMethod = TriggerMethod.None;
                _logger.Information("Ignoring shock {Shocker} is on cooldown", pos);
                continue;
            }

            if (_isAfk && Config.ConfigInstance.Behaviour.DisableWhileAfk)
            {
                shocker.TriggerMethod = TriggerMethod.None;
                _logger.Information("Ignoring shock {Shocker} user is AFK", pos);
                continue;
            }

            shocker.LastExecuted = DateTime.UtcNow;

            byte intensity = 0;
            float intensityFloat = 0;
            uint duration;

            if (config.RandomDuration)
            {
                var rdr = config.DurationRange;
                duration = (uint)(Random.Next((int)(rdr.Min / config.RandomDurationStep),
                    (int)(rdr.Max / config.RandomDurationStep)) * config.RandomDurationStep);
            }
            else duration = config.FixedDuration;

            if (shocker.TriggerMethod == TriggerMethod.Manual)
            {
                if (config.RandomIntensity)
                {
                    var rir = config.IntensityRange;
                    var intensityValue = Random.Next((int)rir.Min, (int)rir.Max);
                    intensity = (byte)intensityValue;
                    intensityFloat = ClampFloat((float)intensityValue / rir.Max);
                }
                else
                {
                    intensity = config.FixedIntensity;
                    intensityFloat = ClampFloat((float)intensity / 100);
                }
            }

            if (shocker.TriggerMethod == TriggerMethod.PhysBoneRelease)
            {
                var rir = config.IntensityRange;
                intensity = (byte)LerpFloat(rir.Min, rir.Max, shocker.LastStretchValue);
                intensityFloat = ClampFloat(shocker.LastStretchValue);
                if (intensityFloat < 0.01f)
                    intensityFloat = 0.01f;
                shocker.LastStretchValue = 0;
            }

            shocker.LastDuration = duration;
            var intensityPercentage = Math.Round(intensityFloat * 100f);
            shocker.LastIntensity = intensityFloat;

            ForceUnmute();
            SendParams();

            shocker.TriggerMethod = TriggerMethod.None;
            var inSeconds = ((float)duration / 1000).ToString(CultureInfo.InvariantCulture);
            _logger.Information(
                "Sending shock to {Shocker} strength:{Intensity} intensityPercentage:{IntensityPercentage}% length:{Length}s",
                pos, intensity, intensityPercentage, inSeconds);

            await UserHubClient.Control(new Control
            {
                Id = shocker.Id,
                Intensity = intensity,
                Duration = duration,
                Type = ControlType.Shock
            });

            if (!Config.ConfigInstance.Osc.Chatbox) continue;
            var msg = $"[ShockOsc] \"{pos}\" {intensityPercentage}%:{inSeconds}s";

            await SenderClient.SendChatboxMessage(msg);
        }
    }

    public static async Task RemoteActivateShocker(ControlLogSender sender, ControlLog log)
    {
        if (sender.ConnectionId == UserHubClient.ConnectionId)
        {
            _logger.Debug("Ignoring remote command log cause it was the local connection");
            return;
        }
        
        if (Config.ConfigInstance.Osc.Chatbox)
        {
            Console.WriteLine("Remote message sent!");
            var inSeconds = ((float)log.Duration / 1000).ToString(CultureInfo.InvariantCulture);
            var msg = $"[ShockOsc] \"{log.Shocker.Name}\" {log.Intensity}%:{inSeconds}s by {sender.Name}";
            await SenderClient.SendChatboxMessage(msg);
        }

        var shocker = Shockers.Values.FirstOrDefault(s => s.Id == log.Shocker.Id);
        if (shocker == null)
            return;

        switch (log.Type)
        {
            case ControlType.Shock:
            {
                var rir = Config.ConfigInstance.Behaviour.IntensityRange;
                if (shocker.LastIntensity == 0) // don't override calculated intensity
                    shocker.LastIntensity = ClampFloat((float)log.Intensity / rir.Max);
                shocker.LastDuration = log.Duration;
                shocker.LastExecuted = log.ExecutedAt;

                ForceUnmute();
                SendParams();
                break;
            }
            case ControlType.Vibrate:
                shocker.LastVibration = log.ExecutedAt;
                break;
            case ControlType.Stop:
                shocker.LastDuration = 0;
                SendParams();
                break;
            case ControlType.Sound:
                break;
            default:
                _logger.Error("ControlType was out of range. Value was: {Type}", log.Type);
                break;
        }
    }

    private static async Task ForceUnmute()
    {
        if (!Config.ConfigInstance.Behaviour.ForceUnmute || !_isMuted) return;
        _logger.Debug("Force unmuting...");
        await SenderClient.SendOscMessage("/input/Voice", new object[] { OscFalse.False });
        await Task.Delay(50);
        await SenderClient.SendOscMessage("/input/Voice", new object[] { OscTrue.True });
        await Task.Delay(50);
        await SenderClient.SendOscMessage("/input/Voice", new object[] { OscFalse.False });
    }

    private static async Task CancelAction(Shocker shocker)
    {
        await UserHubClient.Control(new Control
        {
            Id = shocker.Id,
            Intensity = 0,
            Duration = 0,
            Type = ControlType.Stop
        });
        _logger.Debug("Cancelling action");
    }

    private static float LerpFloat(float min, float max, float t) => min + (max - min) * t;
    private static float ClampFloat(float value) => value < 0 ? 0 : value > 1 ? 1 : value;
}