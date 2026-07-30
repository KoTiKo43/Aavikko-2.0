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
/// Ответственности:
/// 1. Перехватывать ЛКМ, когда игрок в режиме передачи (есть ItemOfferModeComponent).
///    Вместо обычного взаимодействия — отправлять на сервер ItemOfferRequestEvent.
/// 2. Рисовать иконку подарка у курсора, пока режим активен (через overlay).
/// 3. Корректно управлять overlay при смене персонажа (admin aghost, ghost и т.д.).
/// </summary>
public sealed partial class ItemOfferClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;

    private ItemOfferCursorOverlay? _overlay;

    /// <summary>
    /// Флаг: клавиша F физически зажата сейчас. Нужен, чтобы подавлять
    /// повторные Down-события от движка (предикция может слать дубликаты).
    /// Без этого toggle срабатывал бы несколько раз за одно нажатие.
    /// </summary>
    private bool _modeKeyDown;

    public override void Initialize()
    {
        base.Initialize();

        // Подписка на изменение состояния компонента-режима — для управления overlay.
        // ВАЖНО: эти события приходят, только если компонент добавляется/удаляется.
        // При смене персонажа (например, admin aghost) компонент остаётся на старом,
        // и эти подписки НЕ срабатывают — нужна доп. подписка ниже.
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentInit>(OnModeInit);
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentShutdown>(OnModeShutdown);

        // Подписки на смену персонажа игроком (вселяется/покидаёт моба).
        // Это броадкаст-события из Robust.Shared.Player — приходят всегда.
        // Нужны, чтобы overlay следовал за _текущим_ персонажем, а не «зависал»
        // на старом при admin aghost/смене персонажа.
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);

        // Keybind. Используем State-тип (не Toggle) с явным флагом _modeKeyDown
        // для надёжной обработки быстрых тапов. Toggle-тип в keybinds.yml может
        // терять нажатия из-за дедупа в InputSystem.HandleInputCommand.
        CommandBinds.Builder
            .Bind(ItemOfferKeyFunctions.ToggleItemOffer,
                  InputCmdHandler.FromDelegate(
                      enabled: HandleModeKeyDown,
                      disabled: HandleModeKeyUp,
                      handle: true))
            .Register<ItemOfferClientSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<ItemOfferClientSystem>();
        RemoveOverlay();
    }

    /// <summary>
    /// Клавиша F нажата. Если уже была зажата — игнорируем (подавляем дубликаты
    /// от предикции). Иначе — toggle режима.
    /// </summary>
    private void HandleModeKeyDown(ICommonSession? session)
    {
        if (_modeKeyDown)
            return;
        _modeKeyDown = true;

        if (session is not { } playerSession)
            return;
        if (playerSession.AttachedEntity is not { Valid: true } playerEnt || !Exists(playerEnt))
            return;

        // Toggle: если компонент есть — снимаем, нет — вешаем
        if (HasComp<ItemOfferModeComponent>(playerEnt))
            RemComp<ItemOfferModeComponent>(playerEnt);
        else
            EnsureComp<ItemOfferModeComponent>(playerEnt);
    }

    /// <summary>
    /// Клавиша F отпущена. Сбрасываем флаг, чтобы следующее нажатие сработало.
    /// </summary>
    private void HandleModeKeyUp(ICommonSession? session)
    {
        _modeKeyDown = false;
    }

    /// <summary>
    /// Компонент-режим добавлен на сущность. Если это текущий персонаж игрока —
    /// показываем overlay.
    /// </summary>
    private void OnModeInit(EntityUid uid, ItemOfferModeComponent comp, ComponentInit args)
    {
        if (_playerManager.LocalEntity == uid)
            AddOverlay();
    }

    /// <summary>
    /// Компонент-режим удалён с сущности. Если это текущий персонаж —
    /// убираем overlay.
    /// </summary>
    private void OnModeShutdown(EntityUid uid, ItemOfferModeComponent comp, ComponentShutdown args)
    {
        if (_playerManager.LocalEntity == uid)
            RemoveOverlay();
    }

    /// <summary>
    /// Игрок всёлился в нового персонажа. Проверяем, есть ли у нового
    /// ItemOfferModeComponent — если есть, показываем overlay.
    /// Также сбрасываем флаг _modeKeyDown на случай, если клавиша была зажата
    /// при смене персонажа.
    /// </summary>
    private void OnPlayerAttached(LocalPlayerAttachedEvent ev)
    {
        _modeKeyDown = false;
        if (HasComp<ItemOfferModeComponent>(ev.Entity))
            AddOverlay();
        else
            RemoveOverlay();
    }

    /// <summary>
    /// Игрок покинул персонажа. Убираем overlay (он привязан к персонажу,
    /// а не к сессии). Сбрасываем флаг клавиши.
    /// </summary>
    private void OnPlayerDetached(LocalPlayerDetachedEvent ev)
    {
        _modeKeyDown = false;
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

    /// <summary>
    /// Перехват ЛКМ. Если игрок в режиме передачи и кликнул по сущности —
    /// отправляем запрос на сервер.
    /// </summary>
    private bool HandleUseClick(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (args.State != BoundKeyState.Down)
            return false;

        var player = _playerManager.LocalEntity;
        if (player == null || !HasComp<ItemOfferModeComponent>(player.Value))
            return false;

        var target = args.EntityUid;
        if (!target.IsValid())
            return false;

        RaiseNetworkEvent(new ItemOfferRequestEvent(GetNetEntity(target)));
        return true;
    }
}
