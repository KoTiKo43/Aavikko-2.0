using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Aavikko.Geth;

[RegisterComponent, NetworkedComponent]
public sealed partial class GethComponent : Component
{
    [DataField(required: true)]
    public EntProtoId EntityProduced;

    [DataField(required: true)]
    public EntProtoId Action;

    [DataField]
    public EntityUid? ActionEntity;
}
