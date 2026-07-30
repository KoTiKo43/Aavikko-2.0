using Content.Shared.Actions;
using Content.Shared.Aavikko.Geth;
using Content.Shared.Hands.EntitySystems;

namespace Content.Server.Aavikko.Geth;

public sealed partial class GethSystem : EntitySystem
{
    [Dependency] private SharedActionsSystem _actionsSystem = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GethComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<GethComponent, ComponentShutdown>(OnCompRemove);
        SubscribeLocalEvent<GethComponent, GethCreateItemEvent>(OnCreateItem);
    }

    private void OnMapInit(EntityUid uid, GethComponent comp, MapInitEvent _)
    {
        // Создаем и выдаем кнопку игроку
        _actionsSystem.AddAction(uid, ref comp.ActionEntity, comp.Action);
    }

    private void OnCompRemove(EntityUid uid, GethComponent comp, ComponentShutdown _)
    {
        // Защита от утечки сущностей при удалении компонента/смерти
        _actionsSystem.RemoveAction(uid, comp.ActionEntity);
    }

    private void OnCreateItem(EntityUid uid, GethComponent comp, GethCreateItemEvent args)
    {
        if (args.Handled)
            return;

        // Спавним Емаг на координатах игрока
        var item = Spawn(comp.EntityProduced, Transform(uid).Coordinates);

        // Пытаемся автоматически взять его в свободную руку
        if (!_hands.TryPickupAnyHand(uid, item))
        {
            // Если руки заняты, удаляем созданный предмет, чтобы он не падал под ноги
            QueueDel(item);

            // Выходим из метода БЕЗ установки args.Handled = true. Заряд экшена не потратится!
            return;
        }

        // Помечаем событие как успешно обработанное
        args.Handled = true;
    }
}
