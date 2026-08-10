// Aavikko start: Bark voice system — plays bark sounds multiple times based on message length
using Content.Server.Aavikko.Speech.Components;
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
/// Volume is normalized with a hard limiter; pitch = reduced random spread + profile offset.
/// </summary>
public sealed class BarkVoiceSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private const int CharsPerBark = 20;
    private const float MinBarkDelay = 0.3f;
    private const int MaxBarks = 5;
    private const float NormalizedVolume = -3f; // Aavikko: all barks play at this level
    private const float MaxVolume = 0f;         // Aavikko: hard limiter
    private const float RandomPitchSpread = 0.06f; // Aavikko: reduced spread for consistency

    [SubscribeLocalEvent]
    private void OnEntitySpoke(Entity<SpeechComponent> ent, ref EntitySpokeEvent args)
    {
        if (ent.Comp.SpeechSounds == null)
            return;

        if (!_proto.TryIndex<SpeechSoundsPrototype>(ent.Comp.SpeechSounds.Value, out var proto))
            return;

        if (!proto.ID.StartsWith("Bark_"))
            return;

        var message = args.Message;
        if (string.IsNullOrEmpty(message))
            return;

        var barkCount = Math.Clamp(message.Length / CharsPerBark, 1, MaxBarks);
        var sound = GetBarkSound(proto, message);
        if (sound == null)
            return;

        if (!_net.IsServer)
            return;

        // Aavikko: Read pitch offset from BarkVoiceComponent (set from profile)
        var pitchOffset = 0f;
        if (TryComp<BarkVoiceComponent>(ent, out var barkComp))
            pitchOffset = barkComp.PitchOffset;

        // Aavikko: First bark
        var pitch = ComputePitch(proto.Variation, pitchOffset);
        _audio.PlayPvs(sound, ent, MakeAudioParams(pitch));

        // Aavikko: Additional barks with delay
        for (var i = 1; i < barkCount; i++)
        {
            var delay = i * MinBarkDelay;
            var pitchN = ComputePitch(proto.Variation, pitchOffset);
            Timer.Spawn(TimeSpan.FromSeconds(delay), () =>
            {
                if (!TerminatingOrDeleted(ent))
                    _audio.PlayPvs(sound, ent, MakeAudioParams(pitchN));
            });
        }
    }

    private float ComputePitch(float protoVariation, float pitchOffset)
    {
        var spread = Math.Min(protoVariation, RandomPitchSpread);
        var randomPitch = (float) _random.NextGaussian(1, spread);
        return Math.Clamp(randomPitch + pitchOffset, 0.5f, 2.0f);
    }

    private AudioParams MakeAudioParams(float pitch)
    {
        var volume = Math.Min(NormalizedVolume, MaxVolume);
        return AudioParams.Default
            .WithVolume(volume)
            .WithPitchScale(pitch);
    }

    private SoundSpecifier? GetBarkSound(SpeechSoundsPrototype proto, string message)
    {
        var sound = message[^1] switch
        {
            '?' => proto.AskSound,
            '!' => proto.ExclaimSound,
            _ => proto.SaySound
        };

        if (sound == null)
            return proto.SaySound ?? proto.AskSound ?? proto.ExclaimSound;

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
