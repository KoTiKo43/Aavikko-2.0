using Content.Server.Hands.Systems;
using Content.Server.Popups;
using Content.Shared.Aavikko.ItemOffer;
using Content.Shared.Alert;
using Content.Shared.Hands.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Popups;
// using Robust.Shared.Audio; // Функционал звука при передаче предмета пока выключен
// using Robust.Shared.Audio.Systems;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.Aavikko.ItemOffer;

/// <summary>
/// Серверная логика передачи предмета от одного игрока другому.
///
/// Сценарий использования:
/// 1. Игрок нажимает клавишу ToggleItemOffer (по умолчанию F) - на нём
///    появляется ItemOfferModeComponent, у курсора рисуется иконка подарка.
/// 2. Игрок кликает ЛКМ по другому игроку - клиент отправляет
///    ItemOfferRequestEvent. Сервер проверяет условия и показывает
///    принимающему alert-иконку с предложением принять предмет.
/// 3. Принимающий кликает по alert'у - сервер выполняет фактическую
///    передачу предмета из руки дающего в руку принимающего.
///
/// Гарантия изоляции режима: клиент перехватывает ЛКМ через
/// PointerInputCmdHandler на EngineKeyFunctions.Use с handle=true, поэтому
/// при активном режиме обычные взаимодействия (атака, кормление, использование
/// предмета) не запускаются - работает только передача.
///
/// Сервер авторитетно хранит состояние через NetworkedComponent:
/// - ItemOfferModeComponent (режим передачи у дающего)
/// - ItemOfferComponent (активное предложение у принимающего)
/// </summary>
public sealed partial class ItemOfferSystem : EntitySystem
{
    private static readonly ProtoId<AlertPrototype> ItemOfferAlert = "ItemOffer";

    /// <summary>
    /// Сколько секунд предложение остаётся валидным, прежде чем будет
    /// автоматически снято. Предотвращает «зависание» alert'ов, если
    /// принимающий не реагирует.
    /// </summary>
    private const float OfferTimeoutSeconds = 15f;

    /// <summary>
    /// Звук, проигрываемый при предложении предмета (клик ЛКМ по цели).
    /// Слышен всем игрокам в PVS-радиусе цели.
    /// </summary>
    // private static readonly SoundSpecifier OfferSound =
    //     new SoundPathSpecifier("/Audio/Aavikko/Items/offer.ogg",
    //         AudioParams.Default.WithVolume(-3f).WithVariation(0.1f));

    /// <summary>
    /// Звук, проигрываемый при успешном приёме предмета (клик по alert'у).
    /// Слышен всем игрокам в PVS-радиусе принимающего.
    /// </summary>
    // private static readonly SoundSpecifier ReceiveSound =
    //     new SoundPathSpecifier("/Audio/Aavikko/Items/receive.ogg",
    //         AudioParams.Default.WithVolume(-3f).WithVariation(0.1f));

    /// <summary>
    /// Максимальное расстояние (в тайлах) между дающим и принимающим,
    /// при котором предложение остаётся валидным. Учитывает стены и стекло
    /// через InRangeUnobstructed.
    /// </summary>
    private const float MaxRange = 1.5f;

    [Dependency] private AlertsSystem _alerts = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    // [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Регистрация клавиши toggle режима на сервере. Движок автоматически
        // отправляет InputCmdMessage с клиента при нажатии F, сервер
        // авторитетно добавляет/удаляет ItemOfferModeComponent, state-sync
        // подтверждает предсказание клиента.
        CommandBinds.Builder
            .Bind(ItemOfferKeyFunctions.ToggleItemOffer,
                  InputCmdHandler.FromDelegate(HandleToggleItemOffer, handle: false))
            .Register<ItemOfferSystem>();

        // Обработка клика ЛКМ по цели: клиент перехватил клик и отправил
        // сетевое событие с указанием цели.
        SubscribeNetworkEvent<ItemOfferRequestEvent>(OnOfferRequest);

        // Обработка клика по alert-иконке: принимающий подтверждает приём.
        SubscribeLocalEvent<ItemOfferAlertClickedEvent>(OnAlertClicked);

        // Гарантированное снятие alert при удалении компонента предложения
        // (любой причиной: таймаут, выход из радиуса, потеря предмета и т.д.).
        SubscribeLocalEvent<ItemOfferComponent, ComponentShutdown>(OnOfferShutdown);

        // При выходе дающего из режима передачи - снимаем все его активные
        // предложения, чтобы alert'ы не оставались висеть у целей.
        SubscribeLocalEvent<ItemOfferModeComponent, ComponentShutdown>(OnModeShutdown);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<ItemOfferSystem>();
    }

    /// <summary>
    /// Обработчик нажатия клавиши ToggleItemOffer. Переключает состояние
    /// режима передачи на текущем персонаже игрока.
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
    /// Обработчик сетевого запроса от клиента: дающий кликнул ЛКМ по цели.
    /// Извлекает дающего из сессии отправителя и делегирует в TryOfferItem.
    /// </summary>
    private void OnOfferRequest(ItemOfferRequestEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } giver || !Exists(giver))
            return;

        var target = GetEntity(msg.Target);
        TryOfferItem(giver, target);
    }

    /// <summary>
    /// При выходе дающего из режима передачи - снимаем все его активные
    /// предложения. Без этого alert'ы оставались бы у целей бессрочно.
    /// </summary>
    private void OnModeShutdown(EntityUid uid, ItemOfferModeComponent comp, ComponentShutdown args)
    {
        RemComp<ItemOfferComponent>(uid);
    }

    /// <summary>
    /// Пытается предложить предмет цели. Проверяет все предусловия, и если
    /// всё ок - создаёт ItemOfferComponent на цели, показывает alert, играет
    /// звук и показывает попапы обоим сторонам.
    ///
    /// Если на цели уже есть предложение от другого дающего - первое
    /// предложение перезаписывается, а старому дающему показывается уведомление.
    /// </summary>
    public void TryOfferItem(EntityUid giver, EntityUid target)
    {
        // Режим передачи должен быть активен. Это защита от подделанного
        // сетевого запроса - клиент не может отправить запрос без режима.
        if (!HasComp<ItemOfferModeComponent>(giver))
        {
            _popup.PopupEntity(Loc.GetString("item-offer-fail-no-mode"), giver, giver, PopupType.Small);
            return;
        }

        // Нельзя передавать предметы самому себе - это бессмысленно.
        if (giver == target)
        {
            _popup.PopupEntity(Loc.GetString("item-offer-fail-self"), giver, giver, PopupType.Small);
            return;
        }

        // У дающего должны быть руки - без них передача невозможна.
        if (!TryComp<HandsComponent>(giver, out var giverHands))
        {
            _popup.PopupEntity(Loc.GetString("item-offer-fail-no-hands"), giver, giver, PopupType.Small);
            return;
        }

        // В активной руке дающего должен быть предмет.
        var heldItem = _hands.GetHeldItem(giver, giverHands.ActiveHandId);
        if (heldItem is not { } item)
        {
            _popup.PopupEntity(Loc.GetString("item-offer-fail-no-item"), giver, giver, PopupType.Small);
            return;
        }

        // У цели должны быть руки и хотя бы одна свободная рука для приёма.
        if (!TryComp<HandsComponent>(target, out var targetHands) ||
            _hands.CountFreeHands((target, targetHands)) == 0)
        {
            var targetName = Identity.Entity(target, EntityManager);
            _popup.PopupEntity(
                Loc.GetString("item-offer-fail-no-free-hand", ("target", targetName)),
                giver, giver, PopupType.Small);
            return;
        }

        // Дающий и цель должны быть в пределах MaxRange тайлов друг от друга,
        // без стен и препятствий между ними.
        if (!_interaction.InRangeUnobstructed(giver, target, MaxRange))
        {
            var targetName = Identity.Entity(target, EntityManager);
            _popup.PopupEntity(
                Loc.GetString("item-offer-fail-out-of-range", ("target", targetName)),
                giver, giver, PopupType.Small);
            return;
        }

        // Если на цели уже есть предложение от другого дающего - уведомляем
        // старого дающего, что его предложение перезаписано.
        if (TryComp<ItemOfferComponent>(target, out var existingOffer) && existingOffer.Giver != giver)
        {
            var oldItemName = existingOffer.Item is { } oldItem
                ? Identity.Name(oldItem, EntityManager)
                : Loc.GetString("item-offer-unknown-item");
            _popup.PopupEntity(
                Loc.GetString("item-offer-superseded", ("item", oldItemName)),
                existingOffer.Giver, existingOffer.Giver, PopupType.Small);
        }

        // Создаём или обновляем компонент предложения на цели.
        var offer = EnsureComp<ItemOfferComponent>(target);
        offer.Giver = giver;
        offer.Item = item;
        offer.MaxRange = MaxRange;
        offer.Deadline = _timing.CurTime + TimeSpan.FromSeconds(OfferTimeoutSeconds);

        // Показываем alert принимающему - иконка появится справа под здоровьем.
        _alerts.ShowAlert(target, ItemOfferAlert);

        // Звук предложения - слышен всем в PVS-радиусе цели.
        // _audio.PlayPvs(OfferSound, target);

        // Попап дающему: подтверждение, что предложение отправлено.
        var targetNameForGiver = Identity.Entity(target, EntityManager);
        _popup.PopupEntity(
            Loc.GetString("item-offer-to-giver", ("target", targetNameForGiver)),
            giver, giver, PopupType.Medium);

        // Попап принимающему: уведомление о входящем предложении.
        var giverNameForTarget = Identity.Entity(giver, EntityManager);
        _popup.PopupEntity(
            Loc.GetString("item-offer-to-target", ("giver", giverNameForTarget)),
            target, target, PopupType.Medium);
    }

    /// <summary>
    /// Обработчик клика по alert-иконке: принимающий подтверждает приём
    /// предмета. Делегирует в TransferItem для фактической передачи.
    /// </summary>
    private void OnAlertClicked(ItemOfferAlertClickedEvent ev)
    {
        if (!TryComp<ItemOfferComponent>(ev.User, out var offer))
            return;

        TransferItem(ev.User, offer);
        ev.Handled = true;
    }

    /// <summary>
    /// Фактическая передача предмета от дающего принимающему.
    ///
    /// Проверки в момент приёма (могут измениться между предложением и кликом
    /// по alert'у):
    /// - Предмет всё ещё существует
    /// - Дающий и принимающий всё ещё в радиусе
    /// - У принимающего всё ещё есть свободная рука
    ///
    /// При любой неудаче предмет возвращается дающему, не падает на пол.
    /// </summary>
    public void TransferItem(EntityUid receiver, ItemOfferComponent offer)
    {
        // Предмет мог быть удалён за время между предложением и приёмом.
        if (offer.Item is not { } item)
        {
            _popup.PopupEntity(Loc.GetString("item-offer-fail-item-lost"), offer.Giver, receiver, PopupType.Small);
            RemComp<ItemOfferComponent>(receiver);
            return;
        }

        // Дающий и принимающий могли разойтись. Проверяем радиус - иначе
        // предмет после PickupOrDrop упал бы на пол между ними.
        if (!_interaction.InRangeUnobstructed(offer.Giver, receiver, offer.MaxRange))
        {
            _popup.PopupEntity(
                Loc.GetString("item-offer-fail-target-lost"),
                offer.Giver, offer.Giver, PopupType.Small);
            _popup.PopupEntity(
                Loc.GetString("item-offer-fail-target-lost"),
                receiver, receiver, PopupType.Small);
            RemComp<ItemOfferComponent>(receiver);
            return;
        }

        // Снимаем предмет с дающего (drop, если он в руках).
        _hands.PickupOrDrop(offer.Giver, item);

        // У принимающего могла появиться занятая рука за время ожидания.
        if (!TryComp<HandsComponent>(receiver, out var targetHands) ||
            _hands.CountFreeHands((receiver, targetHands)) == 0)
        {
            // Возвращаем предмет дающему, не оставляем на полу.
            if (TryComp<HandsComponent>(offer.Giver, out var giverHands))
                _hands.TryPickupAnyHand(offer.Giver, item, handsComp: giverHands);

            var receiverName = Identity.Entity(receiver, EntityManager);
            _popup.PopupEntity(
                Loc.GetString("item-offer-fail-no-free-hand", ("target", receiverName)),
                offer.Giver, offer.Giver, PopupType.Small);
            return;
        }

        // Пытаемся положить предмет в любую свободную руку принимающего.
        if (_hands.TryPickupAnyHand(receiver, item, handsComp: targetHands))
        {
            // Успех - играем звук и показываем попапы обеим сторонам.
            // _audio.PlayPvs(ReceiveSound, receiver);

            var receiverName = Identity.Entity(receiver, EntityManager);
            var itemName = Identity.Entity(item, EntityManager);
            var giverName = Identity.Entity(offer.Giver, EntityManager);

            _popup.PopupEntity(
                Loc.GetString("item-offer-success-to-giver",
                    ("receiver", receiverName), ("item", itemName)),
                offer.Giver, offer.Giver, PopupType.Medium);
            _popup.PopupEntity(
                Loc.GetString("item-offer-success-to-target",
                    ("giver", giverName), ("item", itemName)),
                receiver, receiver, PopupType.Medium);

            // Удаляем компонент предложения - OnOfferShutdown снимет alert.
            RemComp<ItemOfferComponent>(receiver);
        }
        else
        {
            // TryPickupAnyHand может провалиться даже при свободной руке
            // (например, whitelist предмета не позволяет). Возвращаем дающему.
            if (TryComp<HandsComponent>(offer.Giver, out var giverHands))
                _hands.TryPickupAnyHand(offer.Giver, item, handsComp: giverHands);

            var receiverName = Identity.Entity(receiver, EntityManager);
            _popup.PopupEntity(
                Loc.GetString("item-offer-fail-no-free-hand", ("target", receiverName)),
                offer.Giver, offer.Giver, PopupType.Small);
        }
    }

    /// <summary>
    /// При удалении компонента предложения - гарантированно снимаем alert.
    /// Это вызывает, когда предложение снимается по любой причине: таймаут,
    /// выход из радиуса, потеря предмета, успешная передача, выход дающего
    /// из режима и т.д.
    /// </summary>
    private void OnOfferShutdown(EntityUid uid, ItemOfferComponent comp, ComponentShutdown args)
    {
        _alerts.ClearAlert(uid, ItemOfferAlert);
    }

    /// <summary>
    /// Периодически проверяет все активные предложения и снимает невалидные:
    /// - Дающий или предмет были удалены (death, deletion)
    /// - Принимающий вышел из радиуса
    /// - Дающий выкинул или убрал предмет из руки
    /// - Истёк таймаут предложения (OfferTimeoutSeconds)
    ///
    /// Это предотвращает «зависание» alert'ов у принимающего, когда дающий
    /// изменил состояние, но не нажимал F повторно.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var enumerator = EntityQueryEnumerator<ItemOfferComponent>();
        while (enumerator.MoveNext(out var uid, out var offer))
        {
            // Дающий или предмет были удалены - предложение больше не имеет смысла.
            if (TerminatingOrDeleted(offer.Giver) ||
                offer.Item is null || TerminatingOrDeleted(offer.Item.Value))
            {
                RemCompDeferred<ItemOfferComponent>(uid);
                continue;
            }

            // Принимающий вышел из радиуса - дающий не сможет передать предмет.
            if (!_interaction.InRangeUnobstructed(uid, offer.Giver, offer.MaxRange))
            {
                _popup.PopupEntity(Loc.GetString("item-offer-fail-target-lost"), uid, uid, PopupType.Small);
                RemCompDeferred<ItemOfferComponent>(uid);
                continue;
            }

            // Дающий выкинул или убрал предмет из руки - передавать нечего.
            if (!_hands.IsHolding(offer.Giver, offer.Item.Value))
            {
                RemCompDeferred<ItemOfferComponent>(uid);
                continue;
            }

            // Истёк таймаут - принимающий слишком долго не реагировал.
            if (_timing.CurTime > offer.Deadline)
            {
                _popup.PopupEntity(
                    Loc.GetString("item-offer-fail-timeout"),
                    offer.Giver, offer.Giver, PopupType.Small);
                _popup.PopupEntity(
                    Loc.GetString("item-offer-fail-timeout"),
                    uid, uid, PopupType.Small);
                RemCompDeferred<ItemOfferComponent>(uid);
            }
        }
    }
}
