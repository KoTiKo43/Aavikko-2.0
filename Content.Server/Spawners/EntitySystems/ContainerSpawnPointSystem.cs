using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared.Preferences;
using Robust.Server.Containers;
using Robust.Shared.Containers;
using Robust.Shared.Random;

namespace Content.Server.Spawners.EntitySystems;

public sealed partial class ContainerSpawnPointSystem : EntitySystem
{
    [Dependency] private ContainerSystem _container = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private StationSpawningSystem _stationSpawning = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawningEvent>(HandlePlayerSpawning, before: new []{ typeof(SpawnPointSystem) });
    }

    public void HandlePlayerSpawning(PlayerSpawningEvent args)
    {
        if (args.SpawnResult != null)
            return;

        var isCryoPref = args.HumanoidCharacterProfile?.SpawnPriority == SpawnPriorityPreference.Cryosleep;
        var isJobEntity = ProtoMan.Resolve(args.Job, out var jobProto) && jobProto.JobEntity != null;

        // Aavikko start: cryo fallback for latejoin when no SpawnPointLatejoin exists on station.
        // Some Aavikko maps (paper, awesome, pearl, silly) have no SpawnPointLatejoin entities,
        // so latejoiners would otherwise fall back to a random first spawn point (often medbay).
        // When this fallback fires we treat the spawn as if the player had Cryosleep preference.
        var useCryoFallback = _gameTicker.RunLevel == GameRunLevel.InRound
            && !isCryoPref
            && !isJobEntity
            && args.HumanoidCharacterProfile?.SpawnPriority != SpawnPriorityPreference.Arrivals
            && !HasLateJoinSpawnPoint(args.Station);
        // Aavikko end

        // If it's just a spawn pref check if it's for cryo (silly).
        if (!isCryoPref && !isJobEntity && !useCryoFallback)
            return;

        // Aavikko: log why we are using container/cryo spawn
        if (useCryoFallback)
            Log.Info($"Aavikko cryo fallback: latejoin with no SpawnPointLatejoin on station={ToPrettyString(args.Station)} job={args.Job}, using cryo containers");

        var query = EntityQueryEnumerator<ContainerSpawnPointComponent, ContainerManagerComponent, TransformComponent>();
        var possibleContainers = new List<Entity<ContainerSpawnPointComponent, ContainerManagerComponent, TransformComponent>>();

        while (query.MoveNext(out var uid, out var spawnPoint, out var container, out var xform))
        {
            if (args.Station != null && _station.GetOwningStation(uid, xform) != args.Station)
                continue;

            // If it's unset, then we allow it to be used for both roundstart and midround joins
            if (spawnPoint.SpawnType == SpawnPointType.Unset)
            {
                // make sure we also check the job here for various reasons.
                if (spawnPoint.Job == null || spawnPoint.Job == args.Job)
                    possibleContainers.Add((uid, spawnPoint, container, xform));
                continue;
            }

            if (_gameTicker.RunLevel == GameRunLevel.InRound && spawnPoint.SpawnType == SpawnPointType.LateJoin)
            {
                possibleContainers.Add((uid, spawnPoint, container, xform));
            }

            if (_gameTicker.RunLevel != GameRunLevel.InRound &&
                spawnPoint.SpawnType == SpawnPointType.Job &&
                (args.Job == null || spawnPoint.Job == args.Job))
            {
                possibleContainers.Add((uid, spawnPoint, container, xform));
            }
        }

        if (possibleContainers.Count == 0)
        {
            if (useCryoFallback)
                Log.Info($"Aavikko cryo fallback: no cryo containers found on station={ToPrettyString(args.Station)} job={args.Job}, falling through to SpawnPointSystem");
            return;
        }
        // we just need some default coords so we can spawn the player entity.
        var baseCoords = possibleContainers[0].Comp3.Coordinates;

        args.SpawnResult = _stationSpawning.SpawnPlayerMob(
            baseCoords,
            args.Job,
            args.HumanoidCharacterProfile,
            args.Station);

        _random.Shuffle(possibleContainers);
        foreach (var (uid, spawnPoint, manager, xform) in possibleContainers)
        {
            if (!_container.TryGetContainer(uid, spawnPoint.ContainerId, out var container, manager))
                continue;

            if (!_container.Insert(args.SpawnResult.Value, container, containerXform: xform))
                continue;

            var ev = new ContainerSpawnEvent(args.SpawnResult.Value);
            RaiseLocalEvent(uid, ref ev);

            // Aavikko: log successful cryo spawn
            if (useCryoFallback)
                Log.Info($"Aavikko cryo fallback: spawned player entity={ToPrettyString(args.SpawnResult)} in container={ToPrettyString(uid)} job={args.Job}");
            return;
        }

        Del(args.SpawnResult);
        args.SpawnResult = null;
    }

    // Aavikko start: helper for cryo fallback (see HandlePlayerSpawning).
    private bool HasLateJoinSpawnPoint(EntityUid? station)
    {
        var query = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var spawnPoint, out var xform))
        {
            if (station != null && _station.GetOwningStation(uid, xform) != station)
                continue;
            if (spawnPoint.SpawnType == SpawnPointType.LateJoin)
                return true;
        }
        return false;
    }
    // Aavikko end
}

/// <summary>
/// Raised on a container when a player is spawned into it.
/// </summary>
[ByRefEvent]
public record struct ContainerSpawnEvent(EntityUid Player);

