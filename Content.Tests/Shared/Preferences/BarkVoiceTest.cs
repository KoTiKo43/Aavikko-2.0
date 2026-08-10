// Aavikko start: Unit tests for bark voice serialization and validation
using Content.Shared.Preferences;
using NUnit.Framework;

namespace Content.Tests.Shared.Preferences;

[TestFixture]
[TestOf(typeof(HumanoidCharacterProfile))]
public sealed class BarkVoiceTest : ContentUnitTest
{
    [Test]
    public void BarkVoice_DefaultIsBarkHuman1()
    {
        var profile = new HumanoidCharacterProfile();
        Assert.That(profile.BarkVoice, Is.EqualTo(HumanoidCharacterProfile.DefaultBarkVoice));
        Assert.That(profile.BarkVoice, Is.EqualTo("Bark_human_1"));
    }

    [Test]
    public void BarkPitch_DefaultIsZero()
    {
        var profile = new HumanoidCharacterProfile();
        Assert.That(profile.BarkPitch, Is.EqualTo(0f));
    }

    [Test]
    public void WithBarkVoice_SetsBarkVoice()
    {
        var profile = new HumanoidCharacterProfile();
        var modified = profile.WithBarkVoice("Bark_animal_1");
        Assert.That(modified.BarkVoice, Is.EqualTo("Bark_animal_1"));
        Assert.That(profile.BarkVoice, Is.EqualTo("Bark_human_1"));
    }

    [Test]
    public void WithBarkPitch_SetsAndClampsPitch()
    {
        var profile = new HumanoidCharacterProfile();

        var modified1 = profile.WithBarkPitch(0.3f);
        Assert.That(modified1.BarkPitch, Is.EqualTo(0.3f));

        var modified2 = profile.WithBarkPitch(2.0f);
        Assert.That(modified2.BarkPitch, Is.EqualTo(0.5f));

        var modified3 = profile.WithBarkPitch(-2.0f);
        Assert.That(modified3.BarkPitch, Is.EqualTo(-0.5f));
    }

    [Test]
    public void Clone_PreservesBarkVoiceAndPitch()
    {
        var profile = new HumanoidCharacterProfile()
            .WithBarkVoice("Bark_robot_1")
            .WithBarkPitch(-0.25f);

        var cloned = profile.Clone();

        Assert.That(cloned.BarkVoice, Is.EqualTo("Bark_robot_1"));
        Assert.That(cloned.BarkPitch, Is.EqualTo(-0.25f));
    }

    [Test]
    public void MemberwiseEquals_DifferentBarkVoice_ReturnsFalse()
    {
        var profile1 = new HumanoidCharacterProfile().WithBarkVoice("Bark_human_1");
        var profile2 = new HumanoidCharacterProfile().WithBarkVoice("Bark_human_2");

        Assert.That(profile1.MemberwiseEquals(profile2), Is.False);
    }

    [Test]
    public void MemberwiseEquals_DifferentBarkPitch_ReturnsFalse()
    {
        var profile1 = new HumanoidCharacterProfile().WithBarkPitch(0.1f);
        var profile2 = new HumanoidCharacterProfile().WithBarkPitch(0.2f);

        Assert.That(profile1.MemberwiseEquals(profile2), Is.False);
    }

    [Test]
    public void MemberwiseEquals_SameBarkVoiceAndPitch_ReturnsTrue()
    {
        var profile1 = new HumanoidCharacterProfile()
            .WithBarkVoice("Bark_human_1")
            .WithBarkPitch(0.0f);
        var profile2 = new HumanoidCharacterProfile()
            .WithBarkVoice("Bark_human_1")
            .WithBarkPitch(0.0f);

        Assert.That(profile1.MemberwiseEquals(profile2), Is.True);
    }

    [Test]
    public void RandomizeCfg_IncludesBarkVoice()
    {
        var allCfg = HumanoidCharacterProfile.RandomizeConfigAll;
        Assert.That((allCfg & HumanoidCharacterProfile.RandomizeCfg.BarkVoice) != 0, Is.True,
            "BarkVoice should be part of RandomizeConfigAll");
    }
}
// Aavikko end
