using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Aavikko.ItemOffer;

/// <summary>
/// Навешивается на принимающего игрока, когда дающий кликнул по нему ЛКМ
/// в режиме передачи. Хранит, кто передаёт (Giver) и что передаёт (Item),
/// а также радиус и таймаут предложения.
/// </summary>
[RegisterComponent]
[Access(typeof(ItemOfferSystem))]
public sealed partial class ItemOfferComponent : Component
{
    /// <summary>
    /// Игрок, который инициировал передачу (у которого предмет в руке).
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public EntityUid Giver;

    /// <summary>
    /// Предмет, который передаётся. Может быть null, если дающий успел
    /// выкинуть/убрать предмет до того, как получатель нажал на alert.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public EntityUid? Item;

    /// <summary>
    /// Максимальное расстояние (в тайлах) между дающим и получателем,
    /// при котором предложение ещё валидно.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float MaxRange = 1.5f;

    /// <summary>
    /// Время, после которого предложение автоматически снимается.
    /// Заполняется в TryOfferItem как CurTime + OfferTimeout.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan Deadline;
}
