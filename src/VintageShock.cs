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
    private static long StaticDecayListenerId = 0;
    private static VintageShockSystem? StaticInstance;

    // Configuration values - will be populated from config file
    private VintageShockSettings settings = new();

    // Static fields accessible to Harmony patch methods
    private static VintageShockSettings? StaticSettings;
    private static ICoreClientAPI? StaticCapi;
    private static IServerNetworkChannel? StaticServerChannel;

    // Track damage cooldown
    private static long StaticLastDamageTime = 0;

    // Track last shock time for ramp decay
    private static long StaticLastShockTime = 0;

    // Track last decay tick times independently for power and duration
    private static long StaticLastPowerDecayTick = 0;
    private static long StaticLastDurationDecayTick = 0;

    // Track last death time to prevent respawn shock
    private static long StaticLastDeathTime = 0;

    // Track ramping state
    private static int CurrentRampedIntensity = 0;
    private static float CurrentRampedDuration = 0f;

    private static void DebugLog(string message)
    {
        if (StaticSettings?.DebugMode == true)
        {
            try
            {
                // Sanitize angle brackets to avoid VS rich-text tag errors
                string safe = message.Replace('<', '[').Replace('>', ']');
                StaticCapi?.ShowChatMessage($"[VintageShock Debug] {safe}");
            }
            catch
            {
                // swallow debug logging errors
            }
        }
    }

    private static void ServerDebugLog(ICoreAPI api, string message)
    {
        if (StaticSettings?.DebugMode == true)
        {
            try
            {
                api.Logger.Notification($"[VintageShock Debug] {message}");
            }
            catch
            {
                // swallow debug logging errors
            }
        }
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        this.capi = capi;
        StaticCapi = capi;  // Store for Harmony patch access
        StaticInstance = this;  // Store instance for static access

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
            DebugLog($"Damage message received: {msg.DamageAmount}");
            TriggerDamageShock(capi, msg.DamageAmount, null);
        }
    }

    private void OnServerDamageOtherMessage(VintageShockDamageOtherMessage msg)
    {
        if (StaticSettings?.Enabled == true && StaticSettings.OnPlayerHurtOther && capi != null)
        {
            DebugLog($"Damage-other message received: {msg.DamageAmount}");
            TriggerDamageShock(capi, msg.DamageAmount, null);
        }
    }

    private void OnServerDeathMessage(VintageShockDeathMessage msg)
    {
        if (StaticSettings?.Enabled == true && StaticSettings.OnPlayerDeath && capi != null)
        {
            DebugLog("Death message received: triggering death shock");
            // Trigger shock with longer duration for death
            _ = TriggerShockAsyncStatic(StaticSettings.ApiToken, StaticSettings.DeviceId,
                                       StaticSettings.DurationSec, StaticSettings.Intensity, capi);
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

        // Apply Harmony patch on server side only
        if (!Harmony.HasAnyPatches(DomainCode))
        {
            harmony = new Harmony(DomainCode);

            try
            {
                Type? entityType = Type.GetType("Vintagestory.API.Common.Entities.Entity, VintagestoryAPI");

                if (entityType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        entityType = asm.GetType("Vintagestory.API.Common.Entities.Entity");
                        if (entityType != null) break;
                    }
                }

                if (entityType != null)
                {
                    var receiveDamageMethod = entityType.GetMethod("ReceiveDamage",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (receiveDamageMethod != null)
                    {
                        var patchMethod = typeof(VintageShockSystem).GetMethod("OnReceiveDamageDetectDamage",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                        if (patchMethod != null)
                        {
                            var harmonyMethod = new HarmonyLib.HarmonyMethod(patchMethod);
                            harmony.Patch(receiveDamageMethod, prefix: harmonyMethod);
                            sapi.Logger.Notification("[VintageShock] Harmony patch applied to Entity.ReceiveDamage");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[VintageShock] Harmony patching error: {ex.Message}");
            }
        }

        sapi.Logger.Notification($"[VintageShock] Server initialized.");
    }

    private void OnClientGameTick(float deltaTime)
    {
        if (capi == null || StaticSettings == null) return;

        // Check if we still need decay updates
        bool needsDecay = (StaticSettings.EnablePowerRamping && CurrentRampedIntensity > StaticSettings.Intensity) ||
                          (StaticSettings.EnableDurationRamping && CurrentRampedDuration > StaticSettings.DurationSec);

        if (needsDecay)
        {
            long nowMs = capi.World.ElapsedMilliseconds;
            UpdateDecay(nowMs);
        }
        else
        {
            // Nothing to decay, unregister listener to save performance
            if (StaticDecayListenerId != 0)
            {
                capi.Event.UnregisterGameTickListener(StaticDecayListenerId);
                StaticDecayListenerId = 0;
            }
        }
    }

    private static void EnsureDecayListenerActive()
    {
        if (StaticInstance?.capi != null && StaticDecayListenerId == 0 && StaticSettings != null)
        {
            // Only register if ramping is enabled
            if (StaticSettings.EnablePowerRamping || StaticSettings.EnableDurationRamping)
            {
                StaticDecayListenerId = StaticInstance.capi.Event.RegisterGameTickListener(StaticInstance.OnClientGameTick, 1000);
            }
        }
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
                    else if (key.SequenceEqual("enable-power-ramping"))
                        settings.EnablePowerRamping = value.Contains("true", StringComparison.Ordinal);
                    else if (key.SequenceEqual("power-ramp-step-percent"))
                    {
                        if (float.TryParse(value, out float rampStepPct))
                            settings.PowerRampStepPercent = rampStepPct;
                    }
                    else if (key.SequenceEqual("power-ramp-down-percent"))
                    {
                        if (float.TryParse(value, out float rampDownPct))
                            settings.PowerRampDownPercentPerInterval = rampDownPct;
                    }
                    else if (key.SequenceEqual("power-ramp-down-interval-sec"))
                    {
                        if (float.TryParse(value, out float rampDownInterval))
                            settings.PowerRampDownIntervalSec = rampDownInterval;
                    }
                    else if (key.SequenceEqual("max-intensity"))
                    {
                        if (int.TryParse(value, out int maxInt))
                            settings.MaxIntensity = maxInt;
                    }
                    else if (key.SequenceEqual("enable-duration-ramping"))
                        settings.EnableDurationRamping = value.Contains("true", StringComparison.Ordinal);
                    else if (key.SequenceEqual("duration-ramp-step-percent"))
                    {
                        if (float.TryParse(value, out float durRampStepPct))
                            settings.DurationRampStepPercent = durRampStepPct;
                    }
                    else if (key.SequenceEqual("duration-ramp-down-percent"))
                    {
                        if (float.TryParse(value, out float durRampDownPct))
                            settings.DurationRampDownPercentPerInterval = durRampDownPct;
                    }
                    else if (key.SequenceEqual("duration-ramp-down-interval-sec"))
                    {
                        if (float.TryParse(value, out float durRampDownInterval))
                            settings.DurationRampDownIntervalSec = durRampDownInterval;
                    }
                    else if (key.SequenceEqual("max-duration-sec"))
                    {
                        if (float.TryParse(value, out float maxDur))
                            settings.MaxDurationSec = maxDur;
                    }
                    else if (key.SequenceEqual("debug-mode"))
                        settings.DebugMode = value.Contains("true", StringComparison.Ordinal);
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
        // Fast early exit if mod is disabled
        if (StaticSettings?.Enabled != true)
            return true;

        // Fast early exits
        if (__instance.Api?.Side != EnumAppSide.Server)
            return true;

        var serverChannel = StaticServerChannel;
        if (serverChannel == null)
            return true;

        // Check if this is a player entity taking damage
        if (__instance is EntityPlayer player && player.Player is IServerPlayer serverPlayer)
        {
            // Skip if both death and damage triggers are disabled
            if (!StaticSettings.OnPlayerDeath && !StaticSettings.OnPlayerDamage)
                return true;

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
                    System.Threading.Interlocked.Exchange(ref StaticLastDeathTime, now);
                    serverChannel.SendPacket(new VintageShockDeathMessage(), serverPlayer);
                }
                else if (isAlive && damage > 0 && !isInCooldown)
                {
                    // Player took non-fatal damage while alive
                    serverChannel.SendPacket(new VintageShockDamageMessage { DamageAmount = damage }, serverPlayer);
                }
                // else: ignore damage to dead players (respawn processing)
            }
        }
        // Check if the damage source is a player (player damaged something else)
        else if (StaticSettings.OnPlayerHurtOther && damage > 0 && damageSource?.SourceEntity is EntityPlayer attackingPlayer &&
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
        {
            DebugLog($"Damage shock skipped: cooldown active ({now - lastDamageTime} ms)");
            return;
        }

        System.Threading.Interlocked.Exchange(ref StaticLastDamageTime, now);

        // Trigger the shock asynchronously
        DebugLog($"Triggering damage shock. Damage={damage}");
        _ = TriggerShockAsyncStatic(StaticSettings.ApiToken, StaticSettings.DeviceId,
                                   StaticSettings.DurationSec, StaticSettings.Intensity, capi);
    }

    // Cached HTTP client for reuse (much more efficient than creating new WebClient each time)
    private static readonly System.Net.Http.HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>
    /// Lightweight decay update - call this on game tick periodically
    /// </summary>
    private static void UpdateDecay(long nowMs)
    {
        if (StaticSettings == null) return;

        // Update power decay (caller already checked if needed)
        if (StaticSettings.EnablePowerRamping)
        {
            int baseIntensity = StaticSettings.Intensity;
            float decayInterval = StaticSettings.PowerRampDownIntervalSec;
            long decayIntervalMs = (long)(decayInterval * 1000f);

            if (StaticLastPowerDecayTick == 0)
            {
                StaticLastPowerDecayTick = nowMs;
                return; // Skip first tick
            }

            if (nowMs - StaticLastPowerDecayTick >= decayIntervalMs)
            {
                float decayPct = StaticSettings.PowerRampDownPercentPerInterval;
                float maxIntensity = StaticSettings.MaxIntensity;
                float decayAmount = maxIntensity * (decayPct / 100f);
                CurrentRampedIntensity = (int)System.Math.Round(System.Math.Max(baseIntensity, CurrentRampedIntensity - decayAmount));
                StaticLastPowerDecayTick = nowMs;

                if (StaticSettings.DebugMode)
                {
                    DebugLog($"Power decay: -{decayAmount:F1} -> {CurrentRampedIntensity}");
                }

                if (CurrentRampedIntensity <= baseIntensity)
                {
                    StaticLastPowerDecayTick = 0;
                }
            }
        }

        // Update duration decay (caller already checked if needed)
        if (StaticSettings.EnableDurationRamping)
        {
            float baseDuration = StaticSettings.DurationSec;
            float decayInterval = StaticSettings.DurationRampDownIntervalSec;
            long decayIntervalMs = (long)(decayInterval * 1000f);

            if (StaticLastDurationDecayTick == 0)
            {
                StaticLastDurationDecayTick = nowMs;
                return; // Skip first tick
            }

            if (nowMs - StaticLastDurationDecayTick >= decayIntervalMs)
            {
                float decayPct = StaticSettings.DurationRampDownPercentPerInterval;
                float maxDuration = StaticSettings.MaxDurationSec;
                float decayAmount = maxDuration * (decayPct / 100f);
                CurrentRampedDuration = System.Math.Max(baseDuration, CurrentRampedDuration - decayAmount);
                StaticLastDurationDecayTick = nowMs;

                if (StaticSettings.DebugMode)
                {
                    DebugLog($"Duration decay: -{decayAmount:F2} -> {CurrentRampedDuration:F2}");
                }

                if (CurrentRampedDuration <= baseDuration)
                {
                    StaticLastDurationDecayTick = 0;
                }
            }
        }
    }

    /// <summary>
    /// Calculate ramped intensity - applies step increment only (decay handled separately)
    /// </summary>
    private static int CalculateRampedIntensity(int baseIntensity, long nowMs)
    {
        if (StaticSettings?.EnablePowerRamping != true)
        {
            CurrentRampedIntensity = baseIntensity;
            return baseIntensity;
        }

        float stepPercent = System.Math.Clamp(StaticSettings.PowerRampStepPercent, 0f, 100f);
        float maxIntensity = StaticSettings.MaxIntensity;
        float current = CurrentRampedIntensity <= 0 ? baseIntensity : CurrentRampedIntensity;

        float increment = maxIntensity * (stepPercent / 100f);
        current = System.Math.Min(maxIntensity, current + increment);

        CurrentRampedIntensity = (int)System.Math.Round(current);
        DebugLog($"Power step: base={baseIntensity}, step={stepPercent}% of max({maxIntensity}) = {increment:F1}, final={CurrentRampedIntensity}");
        return CurrentRampedIntensity;
    }

    /// <summary>
    /// Calculate ramped duration - applies step increment only (decay handled separately)
    /// </summary>
    private static float CalculateRampedDuration(float baseDuration, long nowMs)
    {
        if (StaticSettings?.EnableDurationRamping != true)
        {
            CurrentRampedDuration = baseDuration;
            return baseDuration;
        }

        float stepPercent = System.Math.Clamp(StaticSettings.DurationRampStepPercent, 0f, 100f);
        float maxDuration = StaticSettings.MaxDurationSec;
        float current = CurrentRampedDuration <= 0f ? baseDuration : CurrentRampedDuration;

        float increment = maxDuration * (stepPercent / 100f);
        current = System.Math.Min(maxDuration, current + increment);

        CurrentRampedDuration = current;
        DebugLog($"Duration step: base={baseDuration:F2}, step={stepPercent}% of max({maxDuration:F2}) = {increment:F2}, final={CurrentRampedDuration:F2}");
        return CurrentRampedDuration;
    }    // Cache the API URL to avoid repeated string operations
    private static string _cachedApiUrl = "";
    private static string? _cachedApiUrlBase = null;

    /// <summary>
    /// Static async method to trigger shock - reuses HTTP client for better performance
    /// </summary>
    private static async Task TriggerShockAsyncStatic(string apiToken, string deviceId, float durationSec, int intensity, ICoreClientAPI capi)
    {
        try
        {
            long now = capi.World.ElapsedMilliseconds;

            // Apply ramping if enabled (per-shock step toward max with decay between shocks)
            int finalIntensity = CalculateRampedIntensity(intensity, now);
            float finalDuration = CalculateRampedDuration(durationSec, now);

            // Start decay listener if ramping is active
            EnsureDecayListenerActive();

            System.Threading.Interlocked.Exchange(ref StaticLastShockTime, now);

            int durationMs = (int)(finalDuration * 1000);

            DebugLog($"Shock payload => intensity: {finalIntensity}, durationMs: {durationMs}");

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
            request.Headers.Add("User-Agent", "VintageShock/0.0.4");
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
                        $"    - Hurt Other: {settings.OnPlayerHurtOther}\n" +
                        $"  Debug Mode: {settings.DebugMode}";
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
    public bool DebugMode { get; set; } = false;

    // Power ramping settings
    public bool EnablePowerRamping { get; set; } = false;
    public float PowerRampStepPercent { get; set; } = 10.0f; // per-shock increase toward max
    public float PowerRampDownPercentPerInterval { get; set; } = 5.0f; // percent to decay per interval
    public float PowerRampDownIntervalSec { get; set; } = 5.0f;
    public int MaxIntensity { get; set; } = 100;

    // Duration ramping settings
    public bool EnableDurationRamping { get; set; } = false;
    public float DurationRampStepPercent { get; set; } = 10.0f; // per-shock increase toward max
    public float DurationRampDownPercentPerInterval { get; set; } = 5.0f; // percent to decay per interval
    public float DurationRampDownIntervalSec { get; set; } = 5.0f;
    public float MaxDurationSec { get; set; } = 65.0f;

}
