using System.Text.RegularExpressions;
using Content.Server.Aavikko.Speech.Components;
using Content.Shared.Speech.EntitySystems;
using Content.Shared.Speech;

namespace Content.Server.Aavikko.Speech.EntitySystems;

public sealed partial class OrcAccentSystem : EntitySystem
{
    [Dependency] private ReplacementAccentSystem _replacement = default!;

    private static readonly Regex Verb1A = new(@"аю\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb1B = new(@"ешь\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb1C = new(@"ет\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb1D = new(@"ем\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb1E = new(@"аете\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb1F = new(@"ете\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb1G = new(@"ют\b", RegexOptions.IgnoreCase);

    private static readonly Regex Verb2A = new(@"ю\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb2B = new(@"ишь\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb2C = new(@"ит\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb2D = new(@"им\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb2E = new(@"ите\b", RegexOptions.IgnoreCase);
    private static readonly Regex Verb2F = new(@"ят\b", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<OrcAccentComponent, AccentGetEvent>(OnAccent);
    }

    private void OnAccent(EntityUid uid, OrcAccentComponent component, AccentGetEvent args)
    {
        var message = args.Message;

        

        message = _replacement.ApplyReplacements(message, "orc");

        message = Verb1A.Replace(message, "ать");
        message = Verb1B.Replace(message, "ать");
        message = Verb1C.Replace(message, "ать");
        message = Verb1D.Replace(message, "ать");
        message = Verb1E.Replace(message, "ать");
        message = Verb1F.Replace(message, "ать");
        message = Verb1G.Replace(message, "ать");

        message = Verb2A.Replace(message, "ить");
        message = Verb2B.Replace(message, "ить");
        message = Verb2C.Replace(message, "ить");
        message = Verb2D.Replace(message, "ить");
        message = Verb2E.Replace(message, "ить");
        message = Verb2F.Replace(message, "ить");

        args.Message = message;
    }
}
