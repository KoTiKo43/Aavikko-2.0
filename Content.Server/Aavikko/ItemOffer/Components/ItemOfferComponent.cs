namespace Content.Server.Aavikko.ItemOffer;

/// <summary>
/// Навешивается на принимающего игрока, когда дающий кликнул по нему ЛКМ
/// в режиме передачи. Хранит, кто передаёт (Giver) и что передаёт (Item),
/// а также радиус, в пределах которого предложение ещё актуально.
/// </summary>
[RegisterComponent]
[Access(typeof(ItemOfferSystem))]
public sealed partial class ItemOfferComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)]
    public EntityUid Giver;

    [ViewVariables(VVAccess.ReadOnly)]
    public EntityUid? Item;

    [ViewVariables(VVAccess.ReadWrite)]
    public float MaxRange = 1.5f;
}
