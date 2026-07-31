using Robust.Shared.GameStates;

namespace Content.Shared.Aavikko.ItemOffer;

/// <summary>
/// Маркерный компонент: навешивается на игрока, когда он находится в режиме
/// передачи предмета. Пока компонент есть, ЛКМ по другому игроку отправляет
/// предложение передать предмет (вместо обычной атаки/взаимодействия).
///
/// Компонент сетевой — чтобы клиент мог предсказывать, а сервер — авторитетно
/// хранить состояние режима. Создаётся и удаляется системой
/// <see cref="ItemOfferSystem"/> при нажатии клавиши ToggleItemOffer.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ItemOfferModeComponent : Component
{
    /// <summary>
    /// Дальность, в пределах которой можно предложить предмет (в тайлах).
    /// Дублирует MaxRange в системе для удобства настройки через VV.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Range = 1.5f;
}
