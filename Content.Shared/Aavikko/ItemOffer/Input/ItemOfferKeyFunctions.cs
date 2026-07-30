using Robust.Shared.Input;

namespace Content.Shared.Aavikko.ItemOffer;

/// <summary>
/// Имена keybind-функций для системы передачи предмета.
/// Без этих объявлений keybind из keybinds.yml будет помечен invalid
/// и молча проигнорирован движком.
/// </summary>
[KeyFunctions]
public static class ItemOfferKeyFunctions
{
    /// <summary>
    /// Переключение режима передачи предмета. По нажатию — вход в режим
    /// (курсор с иконкой подарка), по следующему нажатию — выход.
    /// </summary>
    public static readonly BoundKeyFunction ToggleItemOffer = "ToggleItemOffer";
}
