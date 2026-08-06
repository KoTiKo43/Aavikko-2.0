using Robust.Shared;
using Robust.Shared.Configuration;
using Content.Shared.Aavikko.CCVar;

namespace Content.Shared.Aavikko.CCVar
{
    [CVarDefs]
    public sealed class AavikkoCCVars : CVars
    {
        /*
         * Discord
         */
        public static readonly CVarDef<string> DiscordBanWebhook =
            CVarDef.Create("discord.ban_webhook", string.Empty, CVar.SERVERONLY);
    }
}
