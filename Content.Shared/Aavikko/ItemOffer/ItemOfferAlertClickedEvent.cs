using Content.Shared.Alert;
using Robust.Shared.Prototypes;

namespace Content.Shared.Aavikko.ItemOffer;

/// <summary>
/// Событие, отправляемое клиентом на сервер при клике принимающей стороной
/// по alert-иконке «вам передают предмет».
/// </summary>
public sealed partial class ItemOfferAlertClickedEvent : BaseAlertEvent
{
    public ItemOfferAlertClickedEvent() : base(default, default)
    {
        // Базовый конструктор требует параметры, но система проставит их в рантайме.
    }
}
