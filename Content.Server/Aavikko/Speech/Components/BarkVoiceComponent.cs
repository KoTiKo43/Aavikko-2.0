// Aavikko: Component holding bark voice parameters on an entity (set from profile on spawn).
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Server.Aavikko.Speech.Components;

/// <summary>
/// Aavikko: Stores bark voice parameters applied from the player's character profile.
/// Pitch offset is added on top of the random per-bark variation.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class BarkVoiceComponent : Component
{
    /// <summary>
    /// Aavikko: Manual pitch offset in range [-0.2, +0.2], added to random variation.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float PitchOffset = 0f;
}
