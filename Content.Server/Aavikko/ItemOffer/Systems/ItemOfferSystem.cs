using Content.Server.Hands.Systems;
using Content.Server.Popups;
using Content.Shared.Aavikko.ItemOffer;
using Content.Shared.Alert;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Aavikko.ItemOffer;

/// <summary>
/// Серверная логика передачи предмета.
/// Архитектура: keybind-toggle mode + alert-подтверждение.
///
/// Сценарий:
/// 1. Игрок нажимает клавишу ToggleItemOffer (по умолчанию F) — на нём
///    появляется ItemOfferModeComponent. Курсор с иконкой подарка (клиент).
/// 2. Игрок кликает ЛКМ по другому игроку — клиент отправляет
///    ItemOfferRequestEvent(target). Сервер проверяет условия и показывает
///    alert цели + попап обоим.
/// 3. Цель кликает по alert'у — сервер выполняет передачу предмета.
/// 4. Повторное нажатие клавиши — выход из режима.
///
/// Keybind регистрируется через CommandBinds.Builder — это shared API,
/// но в Server-сборке он сработает только на сервере. Клиентская часть
/// (перехват ЛКМ, иконка курсора) — в ItemOfferClientSystem.
/// </summary>
public sealed partial class ItemOfferSystem : EntitySystem
{
    private static readonly ProtoId<AlertPrototype> ItemOfferAlert = "ItemOffer";

    private const string PopupOfferToGiver = "Вы передаёте предмет игроку {0}";
    private const string PopupOfferToTarget = "{0} хочет передать вам предмет. Нажмите на иконку подарка, чтобы принять.";
    private const string PopupSuccessToGiver = "{0} принял ваш предмет ({1})";
    private const string PopupSuccessToTarget = "{0} передал вам {1}";
    private const string PopupFailNoItem = "В активной руке нет предмета";
    private const string PopupFailNoFreeHand = "У {0} заняты руки";
    private const string PopupFailOutOfRange = "{0} слишком далеко";
    private const string PopupFailSelf = "Нельзя передать предмет самому себе";
    private const string PopupFailItemLost = "Передача отменена: предмет больше не у вас в руках";
    private const string PopupFailTargetLost = "Передача отменена: цель слишком далеко";
    private const string PopupFailNoMode = "Сначала войдите в режим передачи (клавиша F)";

    private const float MaxRange = 1.5f;

    [Dependency] private AlertsSystem _alerts = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Регистрируем keybind-обработчик для переключения режима.
        // Вызывается при нажатии клавиши ToggleItemOffer.
        CommandBinds.Builder
            .Bind(ItemOfferKeyFunctions.ToggleItemOffer,
                  InputCmdHandler.FromDelegate(HandleToggleItemOffer, handle: true))
            .Register<ItemOfferSystem>();

        // Сетевой запрос от клиента: клик ЛКМ по цели в режиме передачи.
        // Используем SubscribeNetworkEvent с EntitySessionEventArgs, чтобы
        // получить отправителя (session.AttachedEntity).
        SubscribeNetworkEvent<ItemOfferRequestEvent>(OnOfferRequest);

        // Клик по alert'у — принимает предмет
        SubscribeLocalEvent<ItemOfferAlertClickedEvent>(OnAlertClicked);

        // Снятие alert при удалении компонента предложения
        SubscribeLocalEvent<ItemOfferComponent, ComponentShutdown>(OnOfferShutdown);

        // При выходе из режима — снимаем активное предложение
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentShutdown>(OnModeShutdown);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<ItemOfferSystem>();
    }

    /// <summary>
    /// Игрок нажал клавишу ToggleItemOffer. Входим или выходим из режима.
    /// </summary>
    private void HandleToggleItemOffer(ICommonSession? session)
    {
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
    /// Клиент в режиме передачи кликнул ЛКМ по цели.
    /// </summary>
    private void OnOfferRequest(ItemOfferRequestEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } giver || !Exists(giver))
            return;

        var target = GetEntity(msg.Target);
        TryOfferItem(giver, target);
    }

    private void OnModeShutdown(EntityUid uid, ItemOfferModeComponent comp, ComponentShutdown args)
    {
        // Если в момент выхода из режима есть активное предложение — снимаем
        if (TryComp<ItemOfferComponent>(uid, out _))
            RemComp<ItemOfferComponent>(uid);
    }

    /// <summary>
    /// Пытается предложить предмет цели: проверяет условия, показывает alert.
    /// </summary>
    public void TryOfferItem(EntityUid giver, EntityUid target)
    {
        // 1. Должен быть в режиме передачи
        if (!HasComp<ItemOfferModeComponent>(giver))
        {
            _popup.PopupEntity(PopupFailNoMode, giver, giver, PopupType.Small);
            return;
        }

        // 2. Не передаём самому себе
        if (giver == target)
        {
            _popup.PopupEntity(PopupFailSelf, giver, giver, PopupType.Small);
            return;
        }

        // 3. У дающего должна быть активная рука с предметом
        if (!TryComp<HandsComponent>(giver, out var giverHands))
        {
            _popup.PopupEntity(PopupFailNoItem, giver, giver, PopupType.Small);
            return;
        }

        var heldItem = _hands.GetHeldItem(giver, giverHands.ActiveHandId);
        if (heldItem is not { } item)
        {
            _popup.PopupEntity(PopupFailNoItem, giver, giver, PopupType.Small);
            return;
        }

        // 4. У цели должны быть руки и свободная рука
        if (!TryComp<HandsComponent>(target, out var targetHands) ||
            _hands.CountFreeHands((target, targetHands)) == 0)
        {
            _popup.PopupEntity(string.Format(PopupFailNoFreeHand, Name(target)), giver, giver, PopupType.Small);
            return;
        }

        // 5. Проверка радиуса и препятствий
        if (!_interaction.InRangeUnobstructed(giver, target, MaxRange))
        {
            _popup.PopupEntity(string.Format(PopupFailOutOfRange, Name(target)), giver, giver, PopupType.Small);
            return;
        }

        // 6. Создаём компонент предложения на цели
        var offer = EnsureComp<ItemOfferComponent>(target);
        offer.Giver = giver;
        offer.Item = item;
        offer.MaxRange = MaxRange;

        // 7. Показываем alert цели
        _alerts.ShowAlert(target, ItemOfferAlert);

        // 8. Попап дающему
        _popup.PopupEntity(
            string.Format(PopupOfferToGiver, Name(target)),
            giver, giver, PopupType.Medium);

        // 9. Попап цели
        _popup.PopupEntity(
            string.Format(PopupOfferToTarget, Name(giver)),
            target, target, PopupType.Medium);
    }

    /// <summary>
    /// Цель кликнула по alert-иконке. Выполняем передачу.
    /// </summary>
    private void OnAlertClicked(ItemOfferAlertClickedEvent ev)
    {
        if (!TryComp<ItemOfferComponent>(ev.User, out var offer))
            return;

        TransferItem(ev.User, offer);
        ev.Handled = true;
    }

    /// <summary>
    /// Фактическая передача предмета.
    /// </summary>
    public void TransferItem(EntityUid receiver, ItemOfferComponent offer)
    {
        if (offer.Item is not { } item)
        {
            _popup.PopupEntity(PopupFailItemLost, offer.Giver, receiver, PopupType.Small);
            RemComp<ItemOfferComponent>(receiver);
            return;
        }

        // Снимаем предмет с дающего
        _hands.PickupOrDrop(offer.Giver, item);

        // Проверяем свободную руку у цели
        if (!TryComp<HandsComponent>(receiver, out var targetHands) ||
            _hands.CountFreeHands((receiver, targetHands)) == 0)
        {
            if (TryComp<HandsComponent>(offer.Giver, out var giverHands))
                _hands.TryPickupAnyHand(offer.Giver, item, handsComp: giverHands);

            _popup.PopupEntity(
                string.Format(PopupFailNoFreeHand, Name(receiver)),
                offer.Giver, offer.Giver, PopupType.Small);
            return;
        }

        if (_hands.TryPickupAnyHand(receiver, item, handsComp: targetHands))
        {
            _popup.PopupEntity(
                string.Format(PopupSuccessToGiver, Name(receiver), Name(item)),
                offer.Giver, offer.Giver, PopupType.Medium);
            _popup.PopupEntity(
                string.Format(PopupSuccessToTarget, Name(offer.Giver), Name(item)),
                receiver, receiver, PopupType.Medium);

            RemComp<ItemOfferComponent>(receiver);
        }
        else
        {
            if (TryComp<HandsComponent>(offer.Giver, out var giverHands))
                _hands.TryPickupAnyHand(offer.Giver, item, handsComp: giverHands);

            _popup.PopupEntity(
                string.Format(PopupFailNoFreeHand, Name(receiver)),
                offer.Giver, offer.Giver, PopupType.Small);
        }
    }

    /// <summary>
    /// При удалении компонента предложения — снимаем alert.
    /// </summary>
    private void OnOfferShutdown(EntityUid uid, ItemOfferComponent comp, ComponentShutdown args)
    {
        _alerts.ClearAlert(uid, ItemOfferAlert);
    }

    /// <summary>
    /// Периодически снимает невалидные предложения.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var enumerator = EntityQueryEnumerator<ItemOfferComponent>();
        while (enumerator.MoveNext(out var uid, out var offer))
        {
            if (TerminatingOrDeleted(offer.Giver) ||
                offer.Item is null || TerminatingOrDeleted(offer.Item.Value))
            {
                RemCompDeferred<ItemOfferComponent>(uid);
                continue;
            }

            if (!_interaction.InRangeUnobstructed(uid, offer.Giver, offer.MaxRange))
            {
                _popup.PopupEntity(PopupFailTargetLost, uid, uid, PopupType.Small);
                RemCompDeferred<ItemOfferComponent>(uid);
                continue;
            }

            if (!_hands.IsHolding(offer.Giver, offer.Item.Value))
            {
                RemCompDeferred<ItemOfferComponent>(uid);
            }
        }
    }
}
