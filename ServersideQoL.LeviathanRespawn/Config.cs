using BepInEx.Configuration;

namespace ServersideQoL.LeviathanRespawn;

public sealed class Config(ConfigFile cfg, Logger logger) : ConfigBase<Config>(cfg, logger)
{
  public override ConfigEntry<bool> Enabled { get; } = BindEx(cfg, true,
    "Enables/disables the entire mod");
  public ConfigEntry<int> RespawnDelayMinMinutes { get; } = BindEx(cfg, 60,
    "Minimum delay (in-game minutes) before a sunk Leviathan respawns", new AcceptableValueRange<int>(0, 100000));
  public ConfigEntry<int> RespawnDelayMaxMinutes { get; } = BindEx(cfg, 240,
    "Maximum delay (in-game minutes) before a sunk Leviathan respawns", new AcceptableValueRange<int>(0, 100000));
  public ConfigEntry<float> RespawnOffsetMin { get; } = BindEx(cfg, 300f,
    "Minimum distance (meters) a respawned Leviathan is relocated from the original position");
  public ConfigEntry<float> RespawnOffsetMax { get; } = BindEx(cfg, 1200f,
    "Maximum distance (meters) a respawned Leviathan is relocated from the original position");
  public ConfigEntry<int> RespawnChance { get; } = BindEx(cfg, 100,
    "The probability a sunk Leviathan will respawn", new AcceptableValueRange<int>(0, 100));
  public ConfigEntry<float> MaxCheckIntervalSeconds { get; } = BindEx(cfg, 60f,
    "Maximum interval (real-time seconds) between checks whether a pending respawn is due");
  public ConfigEntry<bool> IncludeLeviathanLava { get; } = BindEx(cfg, false,
    "Also respawn LeviathanLava (flametal) Leviathans");
}
