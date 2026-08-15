using Content.Server.GameTicking;
using Content.Server.Popups;
using Content.Shared.Aavikko.PersonalLoadout;
using Content.Shared.GameTicking;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Containers;

namespace Content.Server.Aavikko.PersonalLoadout;

/// <summary>
/// Выдаёт индивидуальные предметы игрокам по ckey при спавне.
/// Предметы кладутся в руки; если руки заняты — падают на пол.
/// </summary>
public sealed partial class PersonalLoadoutSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn);
    }

    private void OnPlayerSpawn(PlayerSpawnCompleteEvent ev)
    {
        if (string.IsNullOrEmpty(ev.JobId))
            return;

        // ICommonSession.Name возвращает имя пользователя (ckey)
        var ckey = ev.Player?.Name;
        if (string.IsNullOrEmpty(ckey))
            return;

        var ckeyLower = ckey.ToLowerInvariant();
        var mob = ev.Mob;

        foreach (var proto in _proto.EnumeratePrototypes<PersonalLoadoutPrototype>())
        {
            if (proto.CKey.ToLowerInvariant() != ckeyLower)
                continue;

            var given = 0;
            foreach (var item in proto.Items)
            {
                for (var i = 0; i < item.Amount; i++)
                {
                    SpawnAndGive(mob, item.Id);
                    given++;
                }
            }

            if (given > 0)
            {
                _popup.PopupEntity(
                    $"Вам выданы личные предметы ({given} шт.)",
                    mob, mob, PopupType.Medium);
            }
        }
    }

    /// <summary>
    /// Спавнит предмет и пытается положить в рюкзак.
    /// Если рюкзак полон или отсутствует — падает на пол.
    /// </summary>
    private void SpawnAndGive(EntityUid mob, string protoId)
    {
        var coords = _transform.GetMapCoordinates(mob);
        var item = Spawn(protoId, _transform.ToCoordinates(coords));

        // Пытаемся найти рюкзак (слот "back") и его storage
        if (_container.TryGetContainer(mob, "back", out var backContainer))
        {
            // Перебираем предметы в слоте рюкзака
            foreach (var contained in backContainer.ContainedEntities)
            {
                // Ищем storage-контейнер внутри рюкзака
                if (_container.TryGetContainer(contained, "storagebase", out var storage))
                {
                    if (_container.Insert(item, storage))
                        return;
                }
            }
        }

        // Если не получилось — предмет остаётся на полу рядом с игроком
    }
}
