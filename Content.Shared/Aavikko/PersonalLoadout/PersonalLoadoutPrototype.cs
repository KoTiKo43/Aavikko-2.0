using Robust.Shared.Prototypes;

namespace Content.Shared.Aavikko.PersonalLoadout;

/// <summary>
/// Прототип индивидуального лодаута. Выдаёт набор предметов
/// конкретному игроку по ckey при спавне.
/// </summary>
[Prototype]
public sealed partial class PersonalLoadoutPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Ckey игрока, которому выдаётся этот лодаут (регистронезависимый).
    /// </summary>
    [DataField("ckey", required: true)]
    public string CKey = string.Empty;

    /// <summary>
    /// Список предметов, которые выдаются игроку.
    /// </summary>
    [DataField(required: true)]
    public List<PersonalLoadoutItem> Items = new();
}

/// <summary>
/// Один предмет в индивидуальном лодауте.
/// </summary>
[DataDefinition]
public sealed partial class PersonalLoadoutItem
{
    /// <summary>
    /// ID прототипа предмета.
    /// </summary>
    [DataField(required: true)]
    public EntProtoId Id = string.Empty;

    /// <summary>
    /// Количество (по умолчанию 1).
    /// </summary>
    [DataField]
    public int Amount = 1;
}
