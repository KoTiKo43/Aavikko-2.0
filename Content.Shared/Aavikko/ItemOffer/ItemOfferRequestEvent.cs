using Robust.Shared.Serialization;
using Content.Shared.Actions; // NetEntity is in Robust.Shared.GameObjects, no import needed
using Robust.Shared.Map;

namespace Content.Shared.Aavikko.ItemOffer;

/// <summary>
/// Сетевое сообщение: клиент → сервер.
/// Отправляется, когда игрок в режиме передачи (есть ItemOfferModeComponent)
/// кликает ЛКМ по другому игроку. Сервер проверяет условия и показывает
/// alert цели + попап обоим.
///
/// Это простое NetSerializable-событие (не action-event) — мы сами
/// контролируем отправку из клиентской системы.
/// </summary>
[Serializable, NetSerializable]
public sealed class ItemOfferRequestEvent : EntityEventArgs
{
    /// <summary>
    /// Сущность-цель, которой игрок хочет передать предмет.
    /// NetEntity для сети, на сервере преобразуется в EntityUid.
    /// </summary>
    public readonly NetEntity Target;

    public ItemOfferRequestEvent(NetEntity target)
    {
        Target = target;
    }
}
