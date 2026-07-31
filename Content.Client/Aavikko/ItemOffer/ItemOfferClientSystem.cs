using Content.Shared.Aavikko.ItemOffer;
using Content.Shared.Hands.Components;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;

namespace Content.Client.Aavikko.ItemOffer;

/// <summary>
/// Клиентская часть системы передачи предмета.
///
/// Ответственности:
/// 1. Обработка клавиши ToggleItemOffer (по умолчанию F) - переключает
///    режим передачи. Компонент ItemOfferModeComponent сетевой, поэтому
///    состояние автоматически синхронизируется с сервером.
/// 2. Перехват ЛКМ при активном режиме - вместо обычной атаки/кормления
///    отправляет на сервер запрос на передачу предмета.
/// 3. Управление overlay (иконка подарка у курсора) - показывается, пока
///    режим активен на текущем персонаже игрока.
///
/// Перехват ЛКМ использует handle=true, поэтому при активном режиме
/// обычные системы взаимодействия (атака, кормление, использование предмета)
/// не запускаются - работает только передача.
/// </summary>
public sealed partial class ItemOfferClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;

    private ItemOfferCursorOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();

        // Подписка на изменение состояния компонента-режима для управления
        // overlay. ComponentInit срабатывает при добавлении (локально или
        // через state-sync с сервера), ComponentShutdown - при удалении.
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentInit>(OnModeInit);
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentShutdown>(OnModeShutdown);

        // Подписки на смену персонажа игроком (aghost, admin-переселение и т.д.)
        // - overlay должен следовать за текущим мобом, а не зависать на старом.
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);

        // Регистрация всех keybind-обработчиков одним Builder-ом.
        CommandBinds.Builder
        // Toggle режима передачи (клавиша F). handle=false, потому что
        // событие не должно «поглощаться» — сервер тоже получит его
        // через InputCmdMessage и подтвердит состояние.
            .Bind(ItemOfferKeyFunctions.ToggleItemOffer,
                  InputCmdHandler.FromDelegate(HandleToggleItemOffer, handle: false))
        // Перехват ЛКМ. EngineKeyFunctions.Use — стандартная функция
        // "использовать/атаковать" в SS14. Когда режим передачи активен,
        // клик по другому игроку отправляет запрос на сервер вместо обычного
        // взаимодействия. handle=true (возврат true из обработчика) означает,
        // что событие обработано — движок не передаёт его дальше по цепочке,
        // поэтому атака/кормление/использование предмета не срабатывают.
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
    /// Обработчик нажатия клавиши ToggleItemOffer. Переключает состояние
    /// режима передачи на текущем персонаже игрока. Сервер тоже получит
    /// keybind-событие и подтвердит состояние через state-sync.
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
    /// Перехват ЛКМ. Если режим передачи активен и клик был по валидной
    /// цели (сущность с руками, не сам игрок) - отправляем запрос на сервер
    /// и возвращаем true (событие обработано, обычные системы не сработают).
    ///
    /// Клик по сущности без HandsComponent пропускается как обычное
    /// взаимодействие - это позволяет, например, атаковать стены или
    /// взаимодействовать с предметами на полу, даже будучи в режиме передачи.
    /// </summary>
    private bool HandleUseClick(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        // Только на нажатие (Down), не на отпускание (Up).
        if (args.State != BoundKeyState.Down)
            return false;

        var player = _playerManager.LocalEntity;
        if (player == null || !HasComp<ItemOfferModeComponent>(player.Value))
            return false; // не в режиме - пропускаем обычное взаимодействие

        var target = args.EntityUid;
        if (!target.IsValid() || target == player.Value)
            return false; // нет цели или клик по себе - пропускаем

        // Только сущности с руками могут принимать предметы. Клик по сущности
        // без рук (стена, предмет на полу, животное) пропускаем как обычное
        // взаимодействие - не отправляем запрос на сервер.
        if (!HasComp<HandsComponent>(target))
            return false;

        // Отправляем запрос на сервер - он проверит условия и покажет alert.
        RaiseNetworkEvent(new ItemOfferRequestEvent(GetNetEntity(target)));

        // Событие обработано - обычные системы (атака, кормление) не сработают.
        return true;
    }

    /// <summary>
    /// При добавлении компонента-режима на сущность: если это текущий
    /// персонаж игрока - показываем overlay с иконкой подарка у курсора.
    /// </summary>
    private void OnModeInit(EntityUid uid, ItemOfferModeComponent comp, ComponentInit args)
    {
        if (_playerManager.LocalEntity == uid)
            AddOverlay();
    }

    /// <summary>
    /// При удалении компонента-режима: если это текущий персонаж - убираем
    /// overlay. Событие срабатывает и при локальном удалении, и при
    /// state-sync с сервера.
    /// </summary>
    private void OnModeShutdown(EntityUid uid, ItemOfferModeComponent comp, ComponentShutdown args)
    {
        if (_playerManager.LocalEntity == uid)
            RemoveOverlay();
    }

    /// <summary>
    /// При вселении в нового персонажа: проверяем, есть ли у него режим
    /// передачи, и показываем/скрываем overlay соответственно.
    /// </summary>
    private void OnPlayerAttached(LocalPlayerAttachedEvent ev)
    {
        if (HasComp<ItemOfferModeComponent>(ev.Entity))
            AddOverlay();
        else
            RemoveOverlay();
    }

    /// <summary>
    /// При покидании персонажа: убираем overlay. Компонент остаётся на
    /// старом мобе, но overlay привязан к текущей сессии игрока.
    /// </summary>
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
