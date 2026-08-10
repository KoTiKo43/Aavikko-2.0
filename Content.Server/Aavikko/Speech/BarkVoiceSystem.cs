// Aavikko start: Bark voice system — plays bark sounds multiple times based on message length
using Content.Shared.Chat;
using Content.Shared.Speech;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Aavikko.Speech;

/// <summary>
/// Aavikko: Plays bark voice sounds multiple times based on message length.
/// Unlike upstream SpeechSoundSystem which plays once per message,
/// this system plays the bark sound roughly once per ~20 characters.
/// </summary>
public sealed class BarkVoiceSystem : EntitySystem
{
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IRobustRandom _random = default!;

    // Play bark every N characters of message
    private const int CharsPerBark = 20;
    // Minimum delay between barks (seconds)
    private const float MinBarkDelay = 0.3f;

    [SubscribeLocalEvent]
    private void OnEntitySpoke(Entity<SpeechComponent> ent, ref EntitySpokeEvent args)
    {
        // Aavikko: Only process if entity has a BarkVoice-style speech sound
        if (ent.Comp.SpeechSounds == null)
            return;

        // Check if this is a bark voice (ID starts with "Bark_")
        if (!_proto.TryIndex<SpeechSoundsPrototype>(ent.Comp.SpeechSounds.Value, out var proto))
            return;

        if (!proto.ID.StartsWith("Bark_"))
            return;

        var message = args.Message;
        if (string.IsNullOrEmpty(message))
            return;

        // Calculate how many barks to play based on message length
        var barkCount = Math.Max(1, message.Length / CharsPerBark);
        barkCount = Math.Min(barkCount, 5); // Cap at 5 barks

        // Get the sound to play
        var sound = GetBarkSound(proto, message);

        if (!_net.IsServer)
            return;

        // Play first bark immediately
        _audio.PlayPvs(sound, ent);

        // Play additional barks with delay
        for (var i = 1; i < barkCount; i++)
        {
            var delay = i * MinBarkDelay;
            Timer.Spawn(TimeSpan.FromSeconds(delay), () =>
            {
                if (!TerminatingOrDeleted(ent))
                    _audio.PlayPvs(sound, ent);
            });
        }
    }

    private SoundSpecifier GetBarkSound(SpeechSoundsPrototype proto, string message)
    {
        // Use ask/exclaim sounds based on message punctuation
        var sound = message[^1] switch
        {
            '?' => proto.AskSound,
            '!' => proto.ExclaimSound,
            _ => proto.SaySound
        };

        // Use exclaim if mostly uppercase
        var uppercaseCount = 0;
        foreach (var t in message)
        {
            if (char.IsUpper(t))
                uppercaseCount++;
        }

        if (uppercaseCount > message.Length / 2)
            sound = proto.ExclaimSound;

        // Add pitch variation
        var scale = (float)_random.NextGaussian(1, proto.Variation);
        sound.Params = sound.Params.WithPitchScale(scale);
        return sound;
    }
}
// Aavikko end