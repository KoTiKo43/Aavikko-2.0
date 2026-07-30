using Content.Shared.Aavikko.ItemOffer;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Client.Aavikko.ItemOffer;

/// <summary>
/// Клиентская часть системы передачи предмета.
///
/// Ответственности:
/// 1. Перехватывать ЛКМ, когда игрок в режиме передачи (есть ItemOfferModeComponent).
///    Вместо обычного взаимодействия — отправлять на сервер ItemOfferRequestEvent.
/// 2. Рисовать иконку подарка у курсора, пока режим активен (через overlay).
/// </summary>
public sealed partial class ItemOfferClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private ItemOfferCursorOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        // Подписка на изменение состояния компонента-режима — для управления overlay
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentInit>(OnModeInit);
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentShutdown>(OnModeShutdown);

        // Подписка на pointer-event (ЛКМ). Используем EngineKeyFunctions.Use
        // (это стандартная функция "использовать"/"атаковать" в SS14).
        // Когда игрок в режиме передачи, мы перехватываем клик.
        CommandBinds.Builder
            .Bind(EngineKeyFunctions.Use,
                  new PointerInputCmdHandler(HandleUseClick))
            .Register<ItemOfferClientSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<ItemOfferClientSystem>();

        if (_overlay != null)
        {
            _overlayManager.RemoveOverlay(_overlay);
            _overlay = null;
        }
    }

    /// <summary>
    /// Игрок вошёл в режим — добавляем overlay с иконкой курсора.
    /// </summary>
    private void OnModeInit(EntityUid uid, ItemOfferModeComponent comp, ComponentInit args)
    {
        if (_overlay == null)
        {
            _overlay = new ItemOfferCursorOverlay();
            _overlayManager.AddOverlay(_overlay);
        }
    }

    /// <summary>
    /// Игрок вышел из режима — убираем overlay.
    /// </summary>
    private void OnModeShutdown(EntityUid uid, ItemOfferModeComponent comp, ComponentShutdown args)
    {
        if (_overlay != null)
        {
            _overlayManager.RemoveOverlay(_overlay);
            _overlay = null;
        }
    }

    /// <summary>
    /// Перехват ЛКМ. Если игрок в режиме передачи и кликнул по сущности с руками —
    /// отправляем запрос на сервер.
    /// </summary>
    private bool HandleUseClick(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (args.State != BoundKeyState.Down)
            return false;

        var player = _playerManager.LocalEntity;
        if (player == null || !HasComp<ItemOfferModeComponent>(player.Value))
            return false; // не в режиме — пропускаем дальше

        // Получаем сущность под курсором
        var target = args.EntityUid;
        if (!target.IsValid())
            return false;

        // Отправляем запрос на сервер
        RaiseNetworkEvent(new ItemOfferRequestEvent(GetNetEntity(target)));

        // Поглощаем событие — не даём обычному взаимодействию сработать
        return true;
    }
}
