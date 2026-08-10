// Aavikko start: Bark voice system — plays bark sounds multiple times based on message length
using Content.Shared.Chat;
using Content.Shared.Speech;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Aavikko.Speech.EntitySystems;

/// <summary>
/// Aavikko: Plays bark voice sounds multiple times based on message length.
/// Unlike upstream SpeechSoundSystem which plays once per message,
/// this system plays the bark sound roughly once per ~20 characters.
/// </summary>
public sealed class BarkVoiceSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    // Aavikko: Play bark every N characters of message
    private const int CharsPerBark = 20;
    // Aavikko: Minimum delay between barks (seconds)
    private const float MinBarkDelay = 0.3f;
    // Aavikko: Max barks per message
    private const int MaxBarks = 5;

    [SubscribeLocalEvent]
    private void OnEntitySpoke(Entity<SpeechComponent> ent, ref EntitySpokeEvent args)
    {
        // Aavikko: Only process if entity has a BarkVoice-style speech sound
        if (ent.Comp.SpeechSounds == null)
            return;

        // Aavikko: Check if this is a bark voice (ID starts with "Bark_")
        if (!_proto.TryIndex<SpeechSoundsPrototype>(ent.Comp.SpeechSounds.Value, out var proto))
            return;

        if (!proto.ID.StartsWith("Bark_"))
            return;

        var message = args.Message;
        if (string.IsNullOrEmpty(message))
            return;

        // Aavikko: Calculate how many barks to play based on message length
        var barkCount = Math.Clamp(message.Length / CharsPerBark, 1, MaxBarks);

        // Aavikko: Pick the correct sound variant (say/ask/exclaim)
        var sound = GetBarkSound(proto, message);
        if (sound == null)
            return;

        // Aavikko: Server-only playback (PVS-based, replicated to clients)
        if (!_net.IsServer)
            return;

        // Aavikko: Play first bark immediately with pitch variation
        var pitch = (float) _random.NextGaussian(1, proto.Variation);
        _audio.PlayPvs(sound, ent, AudioParams.Default.WithPitchScale(pitch));

        // Aavikko: Play additional barks with delay (gives "stuttering" animal-crossing style)
        for (var i = 1; i < barkCount; i++)
        {
            var delay = i * MinBarkDelay;
            var pitchN = (float) _random.NextGaussian(1, proto.Variation);
            Timer.Spawn(TimeSpan.FromSeconds(delay), () =>
            {
                if (!TerminatingOrDeleted(ent))
                    _audio.PlayPvs(sound, ent, AudioParams.Default.WithPitchScale(pitchN));
            });
        }
    }

    private SoundSpecifier? GetBarkSound(SpeechSoundsPrototype proto, string message)
    {
        // Aavikko: Use ask/exclaim sounds based on message punctuation
        var sound = message[^1] switch
        {
            '?' => proto.AskSound,
            '!' => proto.ExclaimSound,
            _ => proto.SaySound
        };

        if (sound == null)
            return proto.SaySound ?? proto.AskSound ?? proto.ExclaimSound;

        // Aavikko: Use exclaim if mostly uppercase (shouting)
        var uppercaseCount = 0;
        foreach (var t in message)
        {
            if (char.IsUpper(t))
                uppercaseCount++;
        }

        if (uppercaseCount > message.Length / 2 && proto.ExclaimSound != null)
            sound = proto.ExclaimSound;

        return sound;
    }
}
// Aavikko end