using System.Text.Json;
using Serilog;
using ShockLink.ShockOsc.Models;

namespace ShockLink.ShockOsc;

public static class OscConfigLoader
{
    private static readonly ILogger Logger = Log.ForContext(typeof(OscConfigLoader));
    
    public static void OnAvatarChange(string? avatarId)
    {
        ShockOsc.Shockers.Clear();
        var parameterCount = 0;
        var avatarConfig = ReadOscConfigFile(avatarId);
        if (avatarConfig == null)
        {
            Logger.Error("Failed to read avatar config file for {AvatarId}", avatarId);
            return;
        }

        foreach (var param in avatarConfig.Parameters)
        {
            if (!param.Name.StartsWith("ShockOsc/"))
                continue;
            
            var paramName = param.Name.Substring(9, param.Name.Length - 9);
            var lastUnderscoreIndex = paramName.LastIndexOf('_') + 1;
            var action = string.Empty;
            if (lastUnderscoreIndex != 0)
                action = paramName.Substring(lastUnderscoreIndex, paramName.Length - lastUnderscoreIndex);
            
            var shockerName = paramName;
            if (ShockOsc.ShockerParams.Contains(action))
                shockerName = paramName.Substring(0, lastUnderscoreIndex - 1);
            
            if (!ShockOsc.Shockers.ContainsKey(shockerName))
            {
                if (!Config.ConfigInstance.ShockLink.Shockers.ContainsKey(shockerName))
                {
                    Logger.Warning("Unknown shocker {Shocker}", shockerName);
                    continue;
                }
                ShockOsc.Shockers.TryAdd(shockerName, new Shocker(Config.ConfigInstance.ShockLink.Shockers[shockerName]));
            }
            
            var shocker = ShockOsc.Shockers[shockerName];
            switch (action)
            {
                case "Cooldown":
                    if (param.Input?.Type != "Bool") break;
                    shocker.HasCooldownParam = true;
                    parameterCount++;
                    break;
                case "Active":
                    if (param.Input?.Type != "Bool") break;
                    shocker.HasActiveParam = true;
                    parameterCount++;
                    break;
                case "Intensity":
                    if (param.Input?.Type != "Float") break;
                    shocker.HasIntensityParam = true;
                    parameterCount++;
                    break;
                case "Stretch":
                case "IsGrabbed":
                case "":
                    parameterCount++;
                    break;
            }
        }
        
        Logger.Information("Loaded avatar config for {AvatarId} with {ParamCount} parameters", avatarId, parameterCount);
    }

    private static AvatarConfigJson? ReadOscConfigFile(string? avatarId)
    {
        var latestWriteTime = DateTime.MinValue;
        AvatarConfigJson? aviConfig = null;
        
        var oscDirPath = Path.Combine($"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}Low", "VRChat\\VRChat\\OSC");
        if (!Directory.Exists(oscDirPath))
            return null;

        var userDir = Directory.GetDirectories(oscDirPath);
        foreach (var user in userDir)
        {
            var aviDirPath = Path.Combine(user, "Avatars");
            if (!Directory.Exists(aviDirPath))
                continue;

            var aviFiles = Directory.GetFiles(aviDirPath);
            foreach (var aviFile in aviFiles)
            {
                var configText = File.ReadAllText(aviFile);
                var config = JsonSerializer.Deserialize<AvatarConfigJson>(configText);
                if (config == null || config.Id != avatarId)
                    continue;
                
                var lastWriteTime = File.GetLastWriteTime(aviFile);
                if (lastWriteTime <= latestWriteTime)
                    continue;
                
                latestWriteTime = lastWriteTime;
                aviConfig = config;
            }
        }
        
        return aviConfig;
    }
}