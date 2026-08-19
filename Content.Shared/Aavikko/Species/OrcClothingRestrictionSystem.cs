// Aavikko: ограничение экипировки скафандров для орков
// Орки НЕ могут надевать стандартные скафандры (тег Hardsuit без OrcHardsuit).
// Орки НЕ могут надевать EVA-костюмы (тег SuitEVA без OrcHardsuit).
// Другие расы НЕ могут надевать орочьи скафандры (тег OrcHardsuit).
using System.Linq;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Robust.Shared.Log;

namespace Content.Shared.Aavikko.Species;

public sealed partial class OrcClothingRestrictionSystem : EntitySystem
{
    [Dependency] private TagSystem _tag = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private ILogManager _logMan = default!;

    private ISawmill _sawmill = default!;

    private const string OrcSpeciesId = "orc";
    private const string OrcHardsuitTag = "OrcHardsuit";

    // Теги, помечающие предмет как "скафандр/EVA" (запрещённые для орка без OrcHardsuit)
    // Hardsuit - стандартные скафандры (upstream)
    // SuitEVA - EVA-костюмы (базовые, синдикат, заключёнческие, emergency, ancient, paramed)
    private static readonly string[] RestrictedHardsuitTags = { "Hardsuit", "SuitEVA" };

    private static readonly string[] RestrictedSlots = { "outerClothing", "head" };

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _logMan.GetSawmill("aavikko.orchardsuit");
        SubscribeLocalEvent<ClothingComponent, BeingEquippedAttemptEvent>(OnEquipAttempt);
    }

    private void OnEquipAttempt(EntityUid uid, ClothingComponent clothing, BeingEquippedAttemptEvent args)
    {
        _sawmill.Info($"OnEquipAttempt: equipment={ToPrettyString(uid)}, slot={args.Slot}, equipTarget={ToPrettyString(args.EquipTarget)}");

        if (args.Cancelled)
        {
            _sawmill.Info($"  -> already cancelled by another system");
            return;
        }

        if (!RestrictedSlots.Contains(args.Slot))
        {
            _sawmill.Info($"  -> slot {args.Slot} not in restricted list, skipping");
            return;
        }

        var equipment = args.Equipment;
        var equipTarget = args.EquipTarget;

        if (!TryComp<InventoryComponent>(equipTarget, out var inventory))
        {
            _sawmill.Info($"  -> no InventoryComponent on target");
            return;
        }

        var isOrc = inventory.SpeciesId == OrcSpeciesId;
        var isOrcHardsuit = _tag.HasTag(equipment, OrcHardsuitTag);

        // Проверяем, есть ли у предмета ЛЮБОЙ из тегов скафандра/EVA
        var hasRestrictedTag = RestrictedHardsuitTags.Any(t => _tag.HasTag(equipment, t));

        _sawmill.Info($"  -> speciesId={inventory.SpeciesId}, isOrc={isOrc}, hasRestrictedTag={hasRestrictedTag}, isOrcHardsuit={isOrcHardsuit}");

        // Случай 1: Орк пытается надеть скафандр/EVA (Hardsuit или SuitEVA), но НЕ OrcHardsuit - запрет
        if (isOrc && hasRestrictedTag && !isOrcHardsuit)
        {
            _sawmill.Info($"  -> CANCELLING: orc trying to wear standard hardsuit/EVA");
            args.Cancel();
            _popup.PopupEntity(
                "Этот скафандр Вам мал!",
                args.User,
                args.User,
                PopupType.MediumCaution);
            return;
        }

        // Случай 2: НЕ орк пытается надеть орочий скафандр (OrcHardsuit) - запрет
        if (!isOrc && isOrcHardsuit)
        {
            _sawmill.Info($"  -> CANCELLING: non-orc trying to wear orc hardsuit");
            args.Cancel();
            _popup.PopupEntity(
                "Этот скафандр Вам велик!",
                args.User,
                args.User,
                PopupType.MediumCaution);
            return;
        }

        _sawmill.Info($"  -> no restriction matched, allowing equip");
    }
}
