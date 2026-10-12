using BepInEx.Configuration;
using System.Collections;
using UnityEngine;

namespace ServersideQoL.LeviathanRespawn;

partial class LeviathanRespawnPlugin : ServersideQoLPluginBase<LeviathanRespawnPlugin, Config>
{
  Coroutine? _watchdog;

  protected override Config CreateConfigSingleton(ConfigFile configFile, Logger logger) => new(configFile, logger);

  protected override void RegisterProcessors(IProcessorCollection processors) => processors
    .Add<LeviathanProcessor>();

  void Start() => StartCoroutine(RegisterWatchdog());

  /// <summary>Waits until the config is initialized (the core plugin binds configs at world load —
  /// <see cref="ConfigBase{T}.Instance"/> throws before then), then starts the respawn watchdog if
  /// the mod is enabled and keeps it in sync with runtime Enabled toggles.</summary>
  IEnumerator RegisterWatchdog()
  {
    var poll = new WaitForSecondsRealtime(5f);
    while (true)
    {
      try { _ = Config.Instance.Enabled; break; }
      catch (InvalidOperationException) { }
      yield return poll;
    }
    Config.Instance.Enabled.SettingChanged += OnEnabledChanged;
    Logger.LogInfo("Watchdog registered");
    UpdateWatchdog();
  }

  void OnEnabledChanged(object sender, EventArgs e) => UpdateWatchdog();

  void UpdateWatchdog()
  {
    if (Config.Instance.Enabled.Value)
      _watchdog ??= StartCoroutine(RespawnWatchdog());
    else if (_watchdog is not null)
    {
      StopCoroutine(_watchdog);
      _watchdog = null;
    }
  }

  /// <summary>Wall-clock heartbeat for pending respawn checks. The framework's PreProcess only runs
  /// when changed ZDOs sit near a peer, which is too unreliable for a timer — this coroutine runs
  /// independently of player proximity (and even while no peers are online).</summary>
  IEnumerator RespawnWatchdog()
  {
    while (true)
    {
      if (ZNet.instance is { } net && net.IsServer() && ZDOMan.instance is not null)
        Processor.Instance<LeviathanProcessor>().CheckExpiredRespawns();
      yield return new WaitForSecondsRealtime(Mathf.Max(1f, Config.Instance.MaxCheckIntervalSeconds.Value));
    }
  }
}
