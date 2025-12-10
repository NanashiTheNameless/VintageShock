using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VintageShock;

/// <summary>
/// VintageShock - A mod for triggering haptic feedback shocks via the OpenShock API
/// based on in-game events. Uses Harmony to detect player damage directly.
/// </summary>
public class VintageShockSystem : ModSystem
{
    private const string DomainCode = "vintageshock";
    private const string ConfigFilePath = "ModConfig/vintageshock.yaml";

    private ICoreClientAPI? capi;
    private ICoreServerAPI? sapi;
    private Harmony? harmony;
    private IClientNetworkChannel? clientNetworkChannel;

    // Configuration values - will be populated from config file
    private VintageShockSettings settings = new();

    // Static fields accessible to Harmony patch methods
    private static VintageShockSettings? StaticSettings;
    private static ICoreClientAPI? StaticCapi;
    private static IServerNetworkChannel? StaticServerChannel;

    // Track damage cooldown
    private static long StaticLastDamageTime = 0;

    // Track last death time to prevent respawn shock
    private static long StaticLastDeathTime = 0;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        this.capi = capi;
        StaticCapi = capi;  // Store for Harmony patch access

        // Load config FIRST so settings are available to patch methods
        LoadConfig();

        // Register client commands for testing
        capi.ChatCommands
            .Create("vshock")
            .WithDescription("VintageShock - manage haptic feedback shock settings")
            .BeginSubCommand("reload")
                .WithDescription("Reload configuration from file")
                .HandleWith(args => OnCommandVShock(args, "reload"))
            .EndSubCommand()
            .BeginSubCommand("test")
                .WithDescription("Send a test shock to verify API connection")
                .HandleWith(args => OnCommandVShock(args, "test"))
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Display current configuration settings")
                .HandleWith(args => OnCommandVShock(args, "status"))
            .EndSubCommand()
            .BeginSubCommand("set")
                .WithDescription("Show how to edit configuration settings")
                .HandleWith(args => OnCommandVShock(args, "set"))
            .EndSubCommand();

        // Initialize Harmony to patch Entity.ReceiveDamage instead of EntityBehaviorHealth.OnEntityReceiveDamage
        if (!Harmony.HasAnyPatches(DomainCode))
        {
            harmony = new Harmony(DomainCode);

            try
            {
                // Try to get Entity type at runtime
                Type? entityType = null;

                // Try searching through loaded assemblies
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    entityType = asm.GetType("Vintagestory.API.Common.Entities.Entity");
                    if (entityType != null)
                    {
                        capi.Logger.Notification($"[VintageShock] Found Entity in assembly: {asm.FullName}");
                        break;
                    }
                }

                if (entityType == null)
                {
                    capi.Logger.Error("[VintageShock] Could not find Entity type");
                }
                else
                {
                    // Get the ReceiveDamage method
                    var receiveDamageMethod = entityType.GetMethod("ReceiveDamage",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (receiveDamageMethod != null)
                    {
                        capi.Logger.Notification($"[VintageShock] Found Entity.ReceiveDamage method");
                        var patchParams = receiveDamageMethod.GetParameters();
                        capi.Logger.Notification($"[VintageShock] ReceiveDamage has {patchParams.Length} parameters:");
                        foreach (var param in patchParams)
                        {
                            capi.Logger.Notification($"[VintageShock]   - {param.ParameterType.Name} {param.Name}");
                        }

                        // Get our patch method
                        var patchMethod = typeof(VintageShockSystem).GetMethod("OnReceiveDamageDetectDamage",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                        if (patchMethod != null)
                        {
                            capi.Logger.Notification($"[VintageShock] Found OnReceiveDamageDetectDamage patch method");

                            // Create a HarmonyMethod object for the prefix
                            var harmonyMethod = new HarmonyLib.HarmonyMethod(patchMethod);
                            var patchResult = harmony.Patch(receiveDamageMethod, prefix: harmonyMethod);

                            if (patchResult != null)
                            {
                                capi.Logger.Notification($"[VintageShock] Harmony prefix patch applied to Entity.ReceiveDamage!");
                            }
                            else
                            {
                                capi.Logger.Error("[VintageShock] Harmony.Patch returned null - patch may have failed");
                            }
                        }
                        else
                        {
                            capi.Logger.Error("[VintageShock] Could not find OnReceiveDamageDetectDamage patch method");
                        }
                    }
                    else
                    {
                        capi.Logger.Error("[VintageShock] ReceiveDamage method not found on Entity");
                    }
                }
            }
            catch (Exception ex)
            {
                capi.Logger.Error($"[VintageShock] Error during Harmony patching: {ex.Message}");
                capi.Logger.Error($"[VintageShock] Stack trace: {ex.StackTrace}");
            }
        }

        // Register network message handler to receive damage notifications from server
        clientNetworkChannel = capi.Network.RegisterChannel("vintageshock");
        clientNetworkChannel.RegisterMessageType(typeof(VintageShockDamageMessage));
        clientNetworkChannel.RegisterMessageType(typeof(VintageShockDamageOtherMessage));
        clientNetworkChannel.RegisterMessageType(typeof(VintageShockDeathMessage));
        clientNetworkChannel.SetMessageHandler<VintageShockDamageMessage>(OnServerDamageMessage);
        clientNetworkChannel.SetMessageHandler<VintageShockDamageOtherMessage>(OnServerDamageOtherMessage);
        clientNetworkChannel.SetMessageHandler<VintageShockDeathMessage>(OnServerDeathMessage);

        capi.Logger.Notification($"[VintageShock] Client initialized.");
    }

    private void OnServerDamageMessage(VintageShockDamageMessage msg)
    {
        if (StaticSettings?.Enabled == true && StaticSettings.OnPlayerDamage && capi != null)
        {
            TriggerDamageShock(capi, msg.DamageAmount, null);
        }
    }

    private void OnServerDamageOtherMessage(VintageShockDamageOtherMessage msg)
    {
        if (StaticSettings?.Enabled == true && StaticSettings.OnPlayerHurtOther && capi != null)
        {
            TriggerDamageShock(capi, msg.DamageAmount, null);
        }
    }

    private void OnServerDeathMessage(VintageShockDeathMessage msg)
    {
        capi?.Logger.Notification("[VintageShock] Death message received from server");
        if (StaticSettings?.Enabled == true && StaticSettings.OnPlayerDeath && capi != null)
        {
            capi.Logger.Notification($"[VintageShock] Triggering death shock (2x duration: {StaticSettings.DurationSec * 2}s)");
            // Trigger shock with longer duration for death
            _ = TriggerShockAsyncStatic(StaticSettings.ApiToken, StaticSettings.DeviceId,
                                       StaticSettings.DurationSec * 2, StaticSettings.Intensity, capi);
        }
        else
        {
            capi?.Logger.Warning($"[VintageShock] Death shock skipped - Enabled: {StaticSettings?.Enabled}, OnPlayerDeath: {StaticSettings?.OnPlayerDeath}");
        }
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        this.sapi = sapi;

        // Register network message types and cache the channel
        var serverChannel = sapi.Network.RegisterChannel("vintageshock");
        serverChannel.RegisterMessageType(typeof(VintageShockDamageMessage));
        serverChannel.RegisterMessageType(typeof(VintageShockDamageOtherMessage));
        serverChannel.RegisterMessageType(typeof(VintageShockDeathMessage));
        StaticServerChannel = serverChannel; // Cache for patch performance

        sapi.Logger.Notification($"[VintageShock] Server initialized.");
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(DomainCode);
    }

    private void LoadConfig()
    {
        try
        {
            // ConfigLib generates a YAML file at ModConfig/vintageshock.yaml
            // We read it directly using VS's built-in LoadModConfig
            string configPath = Path.Combine(GamePaths.DataPath, ConfigFilePath);

            if (File.Exists(configPath))
            {
                // Read YAML file as plain text and parse it manually
                var lines = File.ReadAllLines(configPath);
                foreach (var line in lines)
                {
                    int colonIndex = line.IndexOf(':');
                    if (colonIndex < 0) continue;

                    ReadOnlySpan<char> key = line.AsSpan(0, colonIndex);
                    ReadOnlySpan<char> value = line.AsSpan(colonIndex + 1).Trim();

                    if (key.SequenceEqual("enabled"))
                        settings.Enabled = value.Contains("true", StringComparison.Ordinal);
                    else if (key.SequenceEqual("api-url"))
                        settings.ApiUrl = value.ToString();
                    else if (key.SequenceEqual("api-token"))
                        settings.ApiToken = value.ToString();
                    else if (key.SequenceEqual("device-id"))
                        settings.DeviceId = value.ToString();
                    else if (key.SequenceEqual("intensity"))
                    {
                        int spaceIndex = value.IndexOf(' ');
                        ReadOnlySpan<char> intensityValue = spaceIndex > 0 ? value.Slice(0, spaceIndex) : value;
                        if (int.TryParse(intensityValue, out int intensity))
                            settings.Intensity = intensity;
                    }
                    else if (key.SequenceEqual("duration-sec"))
                    {
                        if (float.TryParse(value, out float duration))
                            settings.DurationSec = duration;
                    }
                    else if (key.SequenceEqual("on-player-death"))
                        settings.OnPlayerDeath = value.Contains("true", StringComparison.Ordinal);
                    else if (key.SequenceEqual("on-player-damage"))
                        settings.OnPlayerDamage = value.Contains("true", StringComparison.Ordinal);
                    else if (key.SequenceEqual("on-player-hurt-other"))
                        settings.OnPlayerHurtOther = value.Contains("true", StringComparison.Ordinal);
                }
                        capi?.Logger.Notification("[VintageShock] Configuration loaded from ConfigLib YAML");
                        capi?.Logger.Notification(
                            $"[VintageShock] Settings => enabled={settings.Enabled}, death={settings.OnPlayerDeath}, damage={settings.OnPlayerDamage}, hurtOther={settings.OnPlayerHurtOther}, intensity={settings.Intensity}, durationSec={settings.DurationSec}, device={settings.DeviceId}"
                        );
                        StaticSettings = settings;  // Make settings accessible to Harmony patch
            }
            else
            {
                capi?.Logger.Warning($"[VintageShock] ConfigLib YAML not found at {configPath}, using defaults");
                StaticSettings = settings;  // Make settings accessible to Harmony patch
            }
        }
        catch (Exception ex)
        {
            capi?.Logger.Warning($"[VintageShock] Error loading config: {ex.Message}. Using defaults.");
            StaticSettings = settings;  // Make settings accessible to Harmony patch
        }
    }

    /// <summary>
    /// Harmony patch for Entity.ReceiveDamage - optimized for performance.
    /// Only processes on server, sends network messages to clients.
    /// </summary>
    public static bool OnReceiveDamageDetectDamage(Entity __instance, DamageSource damageSource, float damage)
    {
        // Fast early exits
        if (__instance.Api?.Side != EnumAppSide.Server)
            return true;

        var serverChannel = StaticServerChannel;
        if (serverChannel == null)
            return true;

        // Check if this is a player entity taking damage
        if (__instance is EntityPlayer player && player.Player is IServerPlayer serverPlayer)
        {
            // Check if fatal damage (health drops to 0)
            var healthTree = player.WatchedAttributes.GetTreeAttribute("health");
            if (healthTree != null)
            {
                float currentHealth = healthTree.GetFloat("currenthealth", 20f);
                float maxHealth = healthTree.GetFloat("maxhealth", 20f);

                // Detect death scenarios:
                // Only trigger death if player currently has health (alive) and damage will kill them
                bool isAlive = currentHealth > 0;
                bool willDie = currentHealth - damage <= 0;
                bool isMassiveDamage = damage >= 9999f;

                __instance.Api.Logger.Debug($"[VintageShock] Player damage: current={currentHealth}, damage={damage}, isAlive={isAlive}, isMassive={isMassiveDamage}, willDie={willDie}");

                // Check cooldown to prevent respawn spam (2 seconds)
                long now = __instance.World.ElapsedMilliseconds;
                long lastDeathTime = System.Threading.Interlocked.Read(ref StaticLastDeathTime);
                bool isInCooldown = lastDeathTime != 0 && now - lastDeathTime <= 2000;

                // Only trigger death if:
                // 1. Player is currently alive (health > 0)
                // 2. Damage will kill them (normal damage OR massive damage like /kill)
                // 3. Not in cooldown period (prevents respawn triggers)
                if (isAlive && willDie && !isInCooldown)
                {
                    // Player is dying from this damage
                    __instance.Api.Logger.Notification($"[VintageShock] Player death detected! Sending death message. Current HP: {currentHealth}, Damage: {damage}");
                    System.Threading.Interlocked.Exchange(ref StaticLastDeathTime, now);
                    serverChannel.SendPacket(new VintageShockDeathMessage(), serverPlayer);
                }
                else if (isAlive && damage > 0 && !isInCooldown)
                {
                    // Player took non-fatal damage while alive
                    serverChannel.SendPacket(new VintageShockDamageMessage { DamageAmount = damage }, serverPlayer);
                }
                else if (!isAlive || isInCooldown)
                {
                    __instance.Api.Logger.Debug($"[VintageShock] Ignoring damage - isAlive={isAlive}, isInCooldown={isInCooldown}");
                }
                // else: ignore damage to dead players (respawn processing)
            }
        }
        // Check if the damage source is a player (player damaged something else)
        else if (damage > 0 && damageSource?.SourceEntity is EntityPlayer attackingPlayer &&
                 attackingPlayer.Player is IServerPlayer attackerServerPlayer)
        {
            serverChannel.SendPacket(new VintageShockDamageOtherMessage { DamageAmount = damage }, attackerServerPlayer);
        }

        return true;
    }

    /// <summary>
    /// Trigger a shock when damage is detected - optimized with early exit
    /// </summary>
    private static void TriggerDamageShock(ICoreClientAPI capi, float damage, DamageSource? source)
    {
        // Fast early exit if conditions not met
        if (StaticSettings?.Enabled != true || !StaticSettings.OnPlayerDamage)
            return;

        // Check cooldown (500ms)
        long now = capi.World.ElapsedMilliseconds;
        long lastDamageTime = System.Threading.Interlocked.Read(ref StaticLastDamageTime);
        if (lastDamageTime != 0 && now - lastDamageTime <= 500)
            return;

        System.Threading.Interlocked.Exchange(ref StaticLastDamageTime, now);

        // Trigger the shock asynchronously
        _ = TriggerShockAsyncStatic(StaticSettings.ApiToken, StaticSettings.DeviceId,
                                   StaticSettings.DurationSec, StaticSettings.Intensity, capi);
    }

    // Cached HTTP client for reuse (much more efficient than creating new WebClient each time)
    private static readonly System.Net.Http.HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    // Cache the API URL to avoid repeated string operations
    private static string _cachedApiUrl = "";
    private static string? _cachedApiUrlBase = null;

    /// <summary>
    /// Static async method to trigger shock - reuses HTTP client for better performance
    /// </summary>
    private static async Task TriggerShockAsyncStatic(string apiToken, string deviceId, float durationSec, int intensity, ICoreClientAPI capi)
    {
        try
        {
            int durationMs = (int)(durationSec * 1000);

            // Build JSON using string interpolation (compiler optimized)
            var jsonPayload = $"{{\"shocks\":[{{\"id\":\"{deviceId}\",\"type\":\"Shock\",\"intensity\":{intensity},\"duration\":{durationMs}}}]}}";

            // Cache API URL construction
            string currentApiUrl = StaticSettings?.ApiUrl ?? "https://api.openshock.app/";
            if (_cachedApiUrlBase != currentApiUrl)
            {
                _cachedApiUrlBase = currentApiUrl;
                _cachedApiUrl = currentApiUrl.TrimEnd('/') + "/2/shockers/control";
            }

            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, _cachedApiUrl);
            request.Headers.Add("Open-Shock-Token", apiToken);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("User-Agent", "VintageShock/0.0.1");
            request.Content = new System.Net.Http.StringContent(jsonPayload, Encoding.UTF8, "application/json");

            await HttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch
        {
            // Silently fail to avoid performance impact from logging
        }
    }
    /// <summary>
    /// Check if settings are properly configured for API calls
    /// </summary>
    private bool IsConfiguredForApi()
    {
        return !string.IsNullOrEmpty(settings.ApiToken) &&
               settings.ApiToken != "your-api-token-here" &&
               !string.IsNullOrEmpty(settings.DeviceId) &&
               settings.DeviceId != "your-device-id-here";
    }

    private TextCommandResult OnCommandVShock(TextCommandCallingArgs args, string subcmd)
    {
        if (subcmd == "reload")
        {
            capi?.Logger.Notification("[VintageShock] Reloading config file...");
            try
            {
                LoadConfig();
                return TextCommandResult.Success($"[VintageShock] Config reloaded.");
            }
            catch (Exception ex)
            {
                capi?.Logger.Warning($"[VintageShock] Error reloading config: {ex.Message}");
                return TextCommandResult.Error($"[VintageShock] Error reloading: {ex.Message}");
            }
        }

        if (subcmd == "test")
        {
            if (!settings.Enabled)
            {
                return TextCommandResult.Error("VintageShock is disabled in config");
            }
            if (!IsConfiguredForApi())
            {
                return TextCommandResult.Error("API token or Device ID not configured");
            }

            capi?.Logger.Notification("[VintageShock] Testing shock...");
            if (capi != null)
            {
                _ = TriggerShockAsyncStatic(settings.ApiToken, settings.DeviceId, settings.DurationSec, settings.Intensity, capi);
            }
            return TextCommandResult.Success($"Test shock triggered: {settings.Intensity}% intensity for {settings.DurationSec} seconds");
        }

        if (subcmd == "status")
        {
            var status = $"VintageShock Status:\n" +
                        $"  Enabled: {settings.Enabled}\n" +
                        $"  API URL: {settings.ApiUrl}\n" +
                        $"  API Token: {(string.IsNullOrEmpty(settings.ApiToken) || settings.ApiToken == "your-api-token-here" ? "Not configured" : "Configured")}\n" +
                        $"  Device ID: {(string.IsNullOrEmpty(settings.DeviceId) || settings.DeviceId == "your-device-id-here" ? "Not configured" : "Configured")}\n" +
                        $"  Intensity: {settings.Intensity}%\n" +
                        $"  Duration: {settings.DurationSec} seconds\n" +
                        $"  Triggers:\n" +
                        $"    - Player Death: {settings.OnPlayerDeath}\n" +
                        $"    - Player Damage: {settings.OnPlayerDamage}\n" +
                        $"    - Hurt Other: {settings.OnPlayerHurtOther}";
            return TextCommandResult.Success(status);
        }

        if (subcmd == "set")
        {
            return TextCommandResult.Success("To change settings, edit the config file at:\n" +
                                           "  ModConfig/vintageshock.yaml\n" +
                                           "Or use ConfigLib's in-game settings menu.\n" +
                                           "Then run .vshock reload to apply changes.");
        }

        return TextCommandResult.Success("Unknown subcommand");
    }
}


/// <summary>
/// Network message from server to client indicating player damage for shock triggering
/// </summary>
public class VintageShockDamageMessage
{
    public float DamageAmount { get; set; }
}

/// <summary>
/// Network message from server to client indicating player damaged another entity
/// </summary>
public class VintageShockDamageOtherMessage
{
    public float DamageAmount { get; set; }
}

/// <summary>
/// Network message from server to client indicating player death
/// </summary>
public class VintageShockDeathMessage
{
}

/// <summary>
/// Settings structure for VintageShock, read from ConfigLib-generated YAML
/// </summary>
public sealed class VintageShockSettings
{
    public bool Enabled { get; set; } = true;
    public string ApiUrl { get; set; } = "https://api.openshock.app/";
    public string ApiToken { get; set; } = "your-api-token-here";
    public string DeviceId { get; set; } = "your-device-id-here";
    public int Intensity { get; set; } = 30;
    public float DurationSec { get; set; } = 1.0f;
    public bool OnPlayerDeath { get; set; } = true;
    public bool OnPlayerDamage { get; set; } = false;
    public bool OnPlayerHurtOther { get; set; } = false;
}
