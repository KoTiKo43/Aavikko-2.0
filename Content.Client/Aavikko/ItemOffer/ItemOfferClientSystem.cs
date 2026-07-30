using Content.Shared.Aavikko.ItemOffer;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;

namespace Content.Client.Aavikko.ItemOffer;

/// <summary>
/// Клиентская часть системы передачи предмета.
///
/// Архитектура:
/// - Keybind ToggleItemOffer (F) переключает ItemOfferModeComponent напрямую.
///   NetworkedComponent + серверный keybind синхронизируют состояние.
/// - Когда режим активен, ЛКМ по другому игроку перехватывается через
///   PointerInputCmdHandler на EngineKeyFunctions.Use. Клиент отправляет
///   ItemOfferRequestEvent на сервер, который выполняет передачу.
///   handle: true гарантирует, что обычные взаимодействия (атака, кормление)
///   не запускаются.
/// - Overlay (иконка подарка у курсора) управляется через ComponentInit/
///   ComponentShutdown и смену персонажа.
/// </summary>
public sealed partial class ItemOfferClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;

    private ItemOfferCursorOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        // Подписка на изменение состояния компонента-режима.
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentInit>(OnModeInit);
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentShutdown>(OnModeShutdown);

        // Подписки на смену персонажа игроком — overlay следует за текущим мобом.
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);

        // Keybind ToggleItemOffer (F). Toggle режима напрямую на клиенте.
        // Сервер тоже регистрирует keybind — state-sync подтверждает предсказание.
        CommandBinds.Builder
            .Bind(ItemOfferKeyFunctions.ToggleItemOffer,
                  InputCmdHandler.FromDelegate(HandleToggleItemOffer, handle: false))
            .Register<ItemOfferClientSystem>();

        // Перехват ЛКМ. EngineKeyFunctions.Use — это стандартная функция
        // "использовать/атаковать" в SS14. Когда игрок в режиме передачи,
        // перехватываем клик, отправляем запрос на сервер и не даём обычным
        // системам (атака, кормление, использование предмета) сработать.
        // handle: true означает "событие обработано" — движок не передаёт
        // его дальше по цепочке обработчиков.
        CommandBinds.Builder
            .Bind(EngineKeyFunctions.Use,
                  new PointerInputCmdHandler(HandleUseClick))
            .Register<ItemOfferClientSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<ItemOfferClientSystem>();
        RemoveOverlay();
    }

    /// <summary>
    /// Клавиша F нажата. Toggle режима.
    /// </summary>
    private void HandleToggleItemOffer(ICommonSession? session)
    {
        if (session is not { } playerSession)
            return;
        if (playerSession.AttachedEntity is not { Valid: true } playerEnt || !Exists(playerEnt))
            return;

        if (HasComp<ItemOfferModeComponent>(playerEnt))
            RemComp<ItemOfferModeComponent>(playerEnt);
        else
            EnsureComp<ItemOfferModeComponent>(playerEnt);
    }

    /// <summary>
    /// Перехват ЛКМ. Если игрок в режиме передачи — отправляем запрос на сервер.
    /// Возвращаем true только если событие обработано (режим активен и клик
    /// был по валидной сущности). false — пропускаем дальше (обычное взаимодействие).
    /// </summary>
    private bool HandleUseClick(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        // Только на нажатие (Down), не на отпускание (Up)
        if (args.State != BoundKeyState.Down)
            return false;

        var player = _playerManager.LocalEntity;
        if (player == null || !HasComp<ItemOfferModeComponent>(player.Value))
            return false; // не в режиме — пропускаем обычное взаимодействие

        // Получаем сущность под курсором
        var target = args.EntityUid;
        if (!target.IsValid() || target == player.Value)
            return false; // нет цели или клик по себе — пропускаем

        // Отправляем запрос на сервер
        RaiseNetworkEvent(new ItemOfferRequestEvent(GetNetEntity(target)));

        // Возвращаем true — событие обработано, обычные системы (атака,
        // кормление, использование предмета) НЕ сработают.
        return true;
    }

    private void OnModeInit(EntityUid uid, ItemOfferModeComponent comp, ComponentInit args)
    {
        if (_playerManager.LocalEntity == uid)
            AddOverlay();
    }

    private void OnModeShutdown(EntityUid uid, ItemOfferModeComponent comp, ComponentShutdown args)
    {
        if (_playerManager.LocalEntity == uid)
            RemoveOverlay();
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent ev)
    {
        if (HasComp<ItemOfferModeComponent>(ev.Entity))
            AddOverlay();
        else
            RemoveOverlay();
    }

    private void OnPlayerDetached(LocalPlayerDetachedEvent ev)
    {
        RemoveOverlay();
    }

    private void AddOverlay()
    {
        if (_overlay == null)
        {
            _overlay = new ItemOfferCursorOverlay();
            _overlayManager.AddOverlay(_overlay);
        }
    }

    private void RemoveOverlay()
    {
        if (_overlay != null)
        {
            _overlayManager.RemoveOverlay(_overlay);
            _overlay = null;
        }
    }
}
