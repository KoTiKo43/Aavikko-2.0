using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Client.Aavikko.ItemOffer;

/// <summary>
/// Overlay, рисующий иконку подарка рядом с курсором, пока игрок находится
/// в режиме передачи предмета. Иконка берётся из RSI-файла проекта.
///
/// Позиция мыши читается прямо в Draw — это канонический паттерн апстрима
/// (см. CombatModeIndicatorsOverlay). Задержка ≤1 кадр неустранима и
/// одинакова для всех screen-space overlay.
/// </summary>
public sealed partial class ItemOfferCursorOverlay : Overlay
{
    [Dependency] private IInputManager _inputManager = default!;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    private readonly Texture? _icon;

    public ItemOfferCursorOverlay()
    {
        IoCManager.InjectDependencies(this);

        var cache = IoCManager.Resolve<IResourceCache>();

        // Пробуем несколько путей — если основного RSI ещё нет, берём
        // стандартный прицел из движка.
        _icon = TryLoadIcon(cache,
            new ResPath("/Textures/Aavikko/Actions/item_offer.rsi"), "cursor_on")
            ?? TryLoadIcon(cache,
            new ResPath("/Textures/Interface/Misc/crosshair_pointers.rsi"), "melee_sight");
    }

    private static Texture? TryLoadIcon(IResourceCache cache, ResPath rsiPath, string state)
    {
        if (!cache.TryGetResource<RSIResource>(rsiPath, out var rsi))
            return null;
        if (!rsi.RSI.TryGetState(new RSI.StateId(state), out var rsiState))
            return null;
        return rsiState.Frame0;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        // Если ни одной текстуры не нашли — не рисуем (overlay виден, но пустой)
        if (_icon == null)
            return;

        if (args.Space != OverlaySpace.ScreenSpace)
            return;

        var mousePos = _inputManager.MouseScreenPosition;
        if (!mousePos.IsValid)
            return;

        var screen = args.ScreenHandle;
        var pos = mousePos.Position + new Vector2(16, -16);
        var size = new Vector2(32, 32);
        var box = UIBox2.FromDimensions(pos, size);

        screen.DrawTextureRect(_icon, box, Color.White.WithAlpha(0.85f));
    }
}
