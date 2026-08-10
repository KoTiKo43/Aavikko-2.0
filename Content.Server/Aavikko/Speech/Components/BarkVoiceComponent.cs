// Aavikko: Component holding bark voice parameters on an entity (set from profile on spawn).
// Server-only: BarkVoiceSystem runs server-side, no need to network this component.
using Robust.Shared.GameObjects;

namespace Content.Server.Aavikko.Speech.Components;

/// <summary>
/// Aavikko: Stores bark voice parameters applied from the player's character profile.
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
