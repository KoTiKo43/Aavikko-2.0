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
/// </summary>
public sealed partial class ItemOfferCursorOverlay : Overlay
{
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    private readonly Texture _icon;

    public ItemOfferCursorOverlay()
    {
        IoCManager.InjectDependencies(this);

        // Загружаем иконку из RSI. Путь — относительный от /Resources/Textures/
        var cache = IoCManager.Resolve<IResourceCache>();
        var rsiPath = new ResPath("/Textures/Aavikko/Actions/item_offer.rsi");
        if (cache.TryGetResource<RSIResource>(rsiPath, out var rsi))
        {
            _icon = rsi.RSI["cursor_on"].Frame0;
        }
        else
        {
            // Фоллбэк — стандартная иконка из движка
            _icon = cache.GetResource<TextureResource>(new ResPath("/Textures/Interface/Default.rsi/cursor.png"));
        }
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.Space != OverlaySpace.ScreenSpace)
            return;

        var mousePos = _inputManager.MouseScreenPosition;
        if (!mousePos.IsValid)
            return;

        var screen = args.ScreenHandle;
        var pos = mousePos.Position + new Vector2(16, -16); // правее и ниже курсора
        var size = new Vector2(32, 32);
        var box = UIBox2.FromDimensions(pos, size);

        screen.DrawTexture(_icon, box, Color.White.WithAlpha(0.85f));
    }
}
