using Content.Shared.Aavikko.Speech; // Aavikko: BarkVoiceComponent
using Content.Shared.Chat;
using Content.Shared.Random.Helpers;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared.Speech.EntitySystems;

public sealed partial class SpeechSoundSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IRobustRandom _random = default!; // Aavikko: bark pitch variation

    // Aavikko: Bark playback constants
    private const int BarkCharsPerBark = 20;
    private const float BarkMinDelay = 0.3f;
    private const int BarkMaxCount = 5;
    private const float BarkNormalizedVolume = -3f;
    private const float BarkRandomPitchSpread = 0.04f;

    [SubscribeLocalEvent]
    private void OnEntitySpoke(Entity<SpeechComponent> ent, ref EntitySpokeEvent args)
    {
        if (ent.Comp.SpeechSounds == null)
            return;

        // Aavikko: Check if this is a bark voice (ID starts with "Bark_")
        if (_protoMan.TryIndex<SpeechSoundsPrototype>(ent.Comp.SpeechSounds.Value, out var proto) && proto.ID.StartsWith("Bark_"))
        {
            PlayBarkSounds(ent, args.Message, proto);
            return;
        }

        // Standard speech sound (upstream behavior)
        var currentTime = _gameTiming.CurTime;
        var cooldown = TimeSpan.FromSeconds(ent.Comp.SoundCooldownTime);

        if (currentTime - ent.Comp.LastTimeSoundPlayed < cooldown)
            return;

        var sound = GetSpeechSound(ent, args.Message);
        ent.Comp.LastTimeSoundPlayed = currentTime;
        if (_net.IsServer)
            _audio.PlayPvs(sound, ent);
    }

    // Aavikko: Play bark sounds multiple times based on message length
    private void PlayBarkSounds(Entity<SpeechComponent> ent, string message, SpeechSoundsPrototype proto)
    {
        if (string.IsNullOrEmpty(message))
            return;

        var barkCount = Math.Clamp(message.Length / BarkCharsPerBark, 1, BarkMaxCount);
        var sound = GetBarkSound(proto, message);
        if (sound == null)
            return;

        if (!_net.IsServer)
            return;

        // Aavikko: Read pitch offset from BarkVoiceComponent (set from profile)
        var pitchOffset = 0f;
        if (TryComp<BarkVoiceComponent>(ent, out var barkComp))
            pitchOffset = barkComp.PitchOffset;

        // Aavikko: Play first bark immediately
        var pitch = ComputeBarkPitch(proto.Variation, pitchOffset);
        _audio.PlayPvs(sound, ent, MakeBarkAudioParams(pitch));

        // Aavikko: Play additional barks with delay
        for (var i = 1; i < barkCount; i++)
        {
            var delay = i * BarkMinDelay;
            var pitchN = ComputeBarkPitch(proto.Variation, pitchOffset);
            Timer.Spawn(TimeSpan.FromSeconds(delay), () =>
            {
                if (!TerminatingOrDeleted(ent))
                    _audio.PlayPvs(sound, ent, MakeBarkAudioParams(pitchN));
            });
        }
    }

    // Aavikko: Compute final pitch = reduced random spread + profile offset
    private float ComputeBarkPitch(float protoVariation, float pitchOffset)
    {
        var spread = Math.Min(protoVariation, BarkRandomPitchSpread);
        var randomPitch = (float) _random.NextGaussian(1, spread);
        return Math.Clamp(randomPitch + pitchOffset, 0.5f, 2.0f);
    }

    // Aavikko: Build AudioParams with normalized volume + limiter
    private AudioParams MakeBarkAudioParams(float pitch)
    {
        var volume = Math.Min(BarkNormalizedVolume, 0f);
        return AudioParams.Default
            .WithVolume(volume)
            .WithPitchScale(pitch);
    }

    // Aavikko: Pick bark sound variant based on message punctuation
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

        // Aavikko: Use exclaim if mostly uppercase
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

    /// <summary>
    /// Gets the speech sound for a message.
    /// </summary>
    public SoundSpecifier? GetSpeechSound(Entity<SpeechComponent> ent, string message)
    {
        if (ent.Comp.SpeechSounds == null)
            return null;

        var prototype = _protoMan.Index<SpeechSoundsPrototype>(ent.Comp.SpeechSounds);

        var contextSound = message[^1] switch
        {
            '?' => prototype.AskSound,
            '!' => prototype.ExclaimSound,
            _ => prototype.SaySound
        };

        var uppercaseCount = 0;
        foreach (var t in message)
        {
            if (char.IsUpper(t))
                uppercaseCount++;
        }

        if (uppercaseCount > message.Length / 2)
            contextSound = prototype.ExclaimSound;

        var random = SharedRandomExtensions.PredictedRandom(_gameTiming, GetNetEntity(ent));
        var scale = (float)random.NextGaussian(1, prototype.Variation);
        contextSound.Params = ent.Comp.AudioParams.WithPitchScale(scale);
        return contextSound;
    }
}
