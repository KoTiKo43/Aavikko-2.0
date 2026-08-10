// Aavikko: Component holding bark voice pitch offset (set from profile on spawn).
using Robust.Shared.GameObjects;

namespace Content.Shared.Aavikko.Speech;

/// <summary>
/// Aavikko: Stores bark voice pitch offset applied from the player's character profile.
/// Pitch offset is added on top of the random per-bark variation.
/// </summary>
[RegisterComponent]
public sealed partial class BarkVoiceComponent : Component
{
    /// <summary>
    /// Aavikko: Manual pitch offset in range [-0.2, +0.2], added to random variation.
    /// </summary>
    [DataField]
    public float PitchOffset = 0f;
}
