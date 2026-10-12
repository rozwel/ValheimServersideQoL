using ServersideQoL.Utilities;
using UnityEngine;

namespace ServersideQoL.LeviathanRespawn;

[Processor(Id)]
public sealed class LeviathanProcessor : Processor<LeviathanProcessor.PrefabInfo>
{
  public const string Id = "6f1f2a54-4f1b-4b1a-9f3e-2e6a0d5c8b71";
  const int MaxPositionAttempts = 3;
  /// <summary>Depth (meters) below the water surface a Leviathan spawns at; the owner client's
  /// Leviathan component rises it to the surface at m_maxSpeed (~1 m/s).</summary>
  const float RiseDepth = 25f;
  /// <summary>Rejects candidate positions with another Leviathan within this distance
  /// (meters) — one zone width, matching the ≤1-per-zone spacing of world generation.</summary>
  const float ClearanceRadius = 64f;
  readonly List<ZDO> _sectorObjects = [];
  readonly List<int> _expiredKeys = [];

  /// <summary>Identifies which leviathan prefab a tracker ZDO holds pending respawns for.
  /// Stored in the ints dict, separate from entries which live in the vec3 dict.</summary>
  static readonly ServerVar<int> __prefabHash = LeviathanRespawnPlugin.RegisterServerVar<int>("PrefabHash");

  /// <summary>Vegetation spawn rules per Leviathan prefab hash, used to validate respawn positions
  /// the same way world generation does. Populated lazily by <see cref="EnsureTables"/> — prefab
  /// validation can run before <see cref="Initialize"/> (world-load ZDOs are processed first), so
  /// the tables cannot rely on Initialize ordering.</summary>
  static readonly Dictionary<int, ZoneSystem.ZoneVegetation> _vegByPrefabHash = [];
  static bool _tablesReady;

  /// <summary>Tracker ZDOs are parked outside the world; their sector is never near a peer, so they
  /// are never scheduled for processing. Their vec3 dict holds one entry per pending respawn:
  /// key = whole in-game minutes since world start (<see cref="ZNet.GetTimeSeconds"/> / 60),
  /// value = original Leviathan position.</summary>
  static Vector3 TrackerPosition => new(WorldGenerator.waterEdge * 10, -1000f, WorldGenerator.waterEdge * 10);

  /// <summary>Respawns all expired tracker entries. Called by the plugin's watchdog coroutine
  /// (the framework's PreProcess only runs when changed ZDOs sit near a peer — too unreliable
  /// for a timer).</summary>
  internal void CheckExpiredRespawns()
  {
    if (!Config.Instance.Enabled.Value)
      return;
    var nowMinutes = (int)(ZNet.instance.GetTimeSeconds() / 60);
    Logger.LogDiagnosticInfo($"CheckExpiredRespawns: now={nowMinutes}, trackers={PlacedObjects.Count}");
    foreach (var tracker in PlacedObjects)
    {
      var prefabHash = __prefabHash.Get(tracker);
      if (!tracker.ZDO.IsValid() || prefabHash <= 0)
        continue;
      ZDOExtraData.GetData(tracker.ZDO.m_uid, out _, out var vec3s, out _, out _, out _, out _, out _, out _);
      if (vec3s is null)
        continue;
      Logger.LogDiagnosticInfo($"  tracker {prefabHash}: {vec3s.Count} pending");
      foreach (var (key, position) in vec3s)
      {
        if (key > nowMinutes)
          continue;
        Logger.LogDiagnosticInfo($"Respawning prefab {prefabHash} near {position} (key {key}, now {nowMinutes})");
        TryRespawn(prefabHash, position);
        _expiredKeys.Add(key);
      }
      foreach (var key in _expiredKeys)
        tracker.ZDO.RemoveVec3(key);
      _expiredKeys.Clear();
    }
  }

  void TryRespawn(int prefabHash, Vector3 origin)
  {
    var pos = FindRespawnPosition(origin, _vegByPrefabHash.GetValueOrDefault(prefabHash));
    // Spawn below the surface: the owner client's Leviathan component rises toward
    // liquidLevel - m_floatOffset at m_maxSpeed, producing a "rise from the deeps" effect.
    pos.y = ZoneSystem.c_WaterLevel - RiseDepth;
    // Face the travel direction — the leviathan "swam" from its original spot.
    // Zero offset (position fallback) gets a random facing.
    var dx = pos.x - origin.x;
    var dz = pos.z - origin.z;
    var yaw = dx * dx + dz * dz < 1f
      ? UnityEngine.Random.value * 360f
      : Mathf.Atan2(dx, dz) * Mathf.Rad2Deg - 90f; // model's visual forward is +90 off its logical forward
    var spawned = Spawn(prefabHash, pos, Quaternion.Euler(0, yaw, 0));
    Logger.LogDiagnosticInfo($"Spawned prefab {prefabHash} at {pos} (zdo {spawned.ZDO.m_uid})");
    spawned.Destroyed += static z => Processor.Instance<LeviathanProcessor>().Logger
      .LogDiagnosticInfo($"Spawned leviathan {z.ZDO.m_uid} destroyed (s_dead={z.ZDO.GetBool(ZDOVars.s_dead)})");
  }

  /// <summary>Returns the tracker ZDO for <paramref name="prefabHash"/>, creating it parked outside
  /// the world if none exists (first run, or after accidental destruction).</summary>
  ServersideQoLZDO GetOrCreateTracker(int prefabHash)
  {
    foreach (var zdo in PlacedObjects)
    {
      if (zdo.ZDO.IsValid() && __prefabHash.Get(zdo) == prefabHash)
        return zdo;
    }
    var tracker = PlaceObject(TrackerPosition, Prefabs.Sconce, Quaternion.identity, CreatorMarkers.ProcessorOwned);
    __prefabHash.Set(tracker, prefabHash);
    Logger.LogInfo($"Created respawn tracker for prefab hash {prefabHash}");
    return tracker;
  }

  protected override void Initialize()
  {
    _tablesReady = false;
    EnsureTables();
  }

  static void EnsureTables()
  {
    if (_tablesReady || ZoneSystem.instance is null || ZNetScene.instance is null)
      return;
    _tablesReady = true;
    _vegByPrefabHash.Clear();
    var logger = Processor.Instance<LeviathanProcessor>().Logger;
    foreach (var veg in ZoneSystem.instance.m_vegetation)
    {
      if (veg.m_prefab?.GetComponentInChildren<Leviathan>(true) is not { } leviathan)
        continue;
      _vegByPrefabHash[veg.m_prefab.name.GetStableHashCode()] = veg;
      logger.LogDiagnosticInfo($"ZoneVegetation '{veg.m_name}' ({veg.m_prefab.name}): biome={veg.m_biome}, biomeArea={veg.m_biomeArea}, " +
        $"minOceanDepth={veg.m_minOceanDepth}, maxOceanDepth={veg.m_maxOceanDepth}, minAltitude={veg.m_minAltitude}, maxAltitude={veg.m_maxAltitude}, " +
        $"groupRadius={veg.m_groupRadius}, minDistanceFromCenter={veg.m_minDistanceFromCenter}, playerCheckSize={leviathan.m_playerCheckSize}");

      var bounds = new Bounds(Vector3.zero, Vector3.zero);
      foreach (var collider in veg.m_prefab.GetComponentsInChildren<Collider>(true))
        bounds.Encapsulate(collider.bounds);
      logger.LogDiagnosticInfo($"{veg.m_prefab.name} collider bounds: center={bounds.center}, extents={bounds.extents}");
    }
  }

  protected override ProcessResult Process(ServersideQoLZDO zdo, IReadOnlyList<Peer> peers, PrefabInfo prefabInfo)
  {
    if (!prefabInfo.IsLeviathan)
      return ProcessResult.UnregisterProcessor;

    Logger.LogDiagnosticInfo($"Watching Leviathan at {zdo.ZDO.GetPosition()}");
    zdo.Destroyed += OnLeviathanDestroyed;
    return ProcessResult.UnregisterProcessor;
  }

  void OnLeviathanDestroyed(ServersideQoLZDO zdo)
  {
    if (GetProcessorPrefabInfo(zdo) is not { IsLeviathan: true } prefabInfo)
      return;
    // Only a natural sink (Leave → dive → submerged) counts; s_dead is set by the owner's client.
    // Other destruction (self-destruct, admin kills, cleanup) leaves no entry.
    if (!zdo.ZDO.GetBool(ZDOVars.s_dead))
      return;
    if (!Config.Instance.Enabled.Value)
      return;
    if (prefabInfo.IsLava && !Config.Instance.IncludeLeviathanLava.Value)
      return;
    if (UnityEngine.Random.Range(0, 100) >= Config.Instance.RespawnChance.Value)
      return;

    var tracker = GetOrCreateTracker(zdo.ZDO.GetPrefab());

    var min = Config.Instance.RespawnDelayMinMinutes.Value;
    var max = Math.Max(min, Config.Instance.RespawnDelayMaxMinutes.Value);

    // Entries are keyed by whole in-game minutes; bump on the rare same-minute collision.
    var now = (int)(ZNet.instance.GetTimeSeconds() / 60);
    var key = now + UnityEngine.Random.Range(min, max + 1);
    while (ZDOExtraData.GetVec3(tracker.ZDO.m_uid, key, out _))
      key++;
    tracker.ZDO.Set(key, zdo.ZDO.GetPosition());
    Logger.LogDiagnosticInfo($"Leviathan sank at {zdo.ZDO.GetPosition()}, respawn scheduled for minute {key} (now {now}, in {key - now} min)");
  }

  /// <summary>Returns a valid respawn position: a random direction and distance within the configured annulus around
  /// the original position that satisfies the prefab's vegetation spawn rules and is clear of other
  /// Leviathans. Falls back to the original position after <see cref="MaxPositionAttempts"/>
  /// failed tries.</summary>
  Vector3 FindRespawnPosition(Vector3 origin, ZoneSystem.ZoneVegetation? veg)
  {
    var min = Mathf.Max(0, Config.Instance.RespawnOffsetMin.Value);
    var max = Mathf.Max(min, Config.Instance.RespawnOffsetMax.Value);
    for (var attempt = 0; attempt < MaxPositionAttempts; attempt++)
    {
      var angle = UnityEngine.Random.value * Mathf.PI * 2;
      var distance = UnityEngine.Random.Range(min, max);
      var candidate = origin + new Vector3(Mathf.Cos(angle) * distance, 0, Mathf.Sin(angle) * distance);
      if (IsValidRespawnPosition(candidate, veg))
        return candidate;
    }
    return origin;
  }

  /// <summary>Mirrors the biome, altitude and ocean-depth checks world generation applies in
  /// <see cref="ZoneSystem.PlaceVegetation"/>, plus a clearance check against other Leviathans.
  /// Ships are not avoided — a leviathan surfacing under a hull collides with it.</summary>
  bool IsValidRespawnPosition(Vector3 pos, ZoneSystem.ZoneVegetation? veg)
  {
    var heightmap = GetHeightmap(pos);
    if (veg is not null)
    {
      if ((veg.m_biome & heightmap.GetBiome(pos)) is 0)
        return false;
      var altitude = heightmap.GetHeight(pos) - ZoneSystem.c_WaterLevel;
      if (altitude < veg.m_minAltitude || altitude > veg.m_maxAltitude)
        return false;
      if (!veg.m_minOceanDepth.Equals(veg.m_maxOceanDepth))
      {
        var oceanDepth = heightmap.GetOceanDepth(pos);
        if (oceanDepth < veg.m_minOceanDepth || oceanDepth > veg.m_maxOceanDepth)
          return false;
      }
    }
    else if (heightmap.GetBiome(pos) is not Heightmap.Biome.Ocean)
      return false;
    return !HasBlockedNeighbor(pos);
  }

  /// <summary>True if another Leviathan is within <see cref="ClearanceRadius"/> of <paramref name="pos"/>.</summary>
  bool HasBlockedNeighbor(Vector3 pos)
  {
    ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(pos), new SimulationDistance(1, 0, classic: true), _sectorObjects);
    try
    {
      foreach (var zdo in _sectorObjects)
      {
        var dz = zdo.GetPosition() - pos;
        if (dz.x * dz.x + dz.z * dz.z > ClearanceRadius * ClearanceRadius)
          continue;
        var prefab = zdo.GetPrefab();
        if (_vegByPrefabHash.ContainsKey(prefab))
          return true;
      }
      return false;
    }
    finally
    {
      _sectorObjects.Clear();
    }
  }

  public sealed record PrefabInfo(ZNetView NView) : ProcessorPrefabInfo
  {
    public bool IsLava { get; private set; }
    public bool IsLeviathan { get; private set; }

    public override bool IsValid
    {
      get
      {
        if (PrefabInfo.Prefab is null)
          return false;

        EnsureTables();
        IsLava = PrefabInfo.Prefab.name is "LeviathanLava";
        IsLeviathan = IsLava || _vegByPrefabHash.ContainsKey(PrefabInfo.Prefab.name.GetStableHashCode());
        return IsLeviathan;
      }
    }
  }
}
