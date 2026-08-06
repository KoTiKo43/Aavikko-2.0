using Content.Shared.Aavikko.CCVar;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Robust.Shared;
using Robust.Shared.Configuration;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace Content.Server.Aavikko.BansNotifications;

/// <summary>
/// Отправляет уведомления о банах в Discord через webhook.
/// Слушает события:
/// - <see cref="BanEvent"/> — серверный бан
/// - <see cref="RoleBanEvent"/> — бан на роли (job + antag)
/// - <see cref="DepartmentBanEvent"/> — бан на отдел
///
/// Webhook URL настраивается через CVar discord.ban_webhook.
/// Если пусто — уведомления не отправляются.
/// </summary>
public sealed partial class BansNotificationsSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    private ISawmill _sawmill = default!;
    private readonly HttpClient _httpClient = new();
    private string _webhookUrl = String.Empty;
    private string _serverName = String.Empty;

    public override void Initialize()
    {
        _sawmill = Logger.GetSawmill("bans_notifications");
        SubscribeLocalEvent<BanEvent>(OnBan);
        SubscribeLocalEvent<RoleBanEvent>(OnRoleBan);
        SubscribeLocalEvent<DepartmentBanEvent>(OnDepartmentBan);
        _config.OnValueChanged(AavikkoCCVars.DiscordBanWebhook, value => _webhookUrl = value, true);
        _config.OnValueChanged(CVars.GameHostName, value => _serverName = value, true);
    }

    private async void SendDiscordMessage(WebhookPayload payload)
    {
        var request = await _httpClient.PostAsync(_webhookUrl,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        _sawmill.Debug($"Discord webhook json: {JsonSerializer.Serialize(payload)}");

        var content = await request.Content.ReadAsStringAsync();
        if (!request.IsSuccessStatusCode)
        {
            _sawmill.Error($"Discord returned bad status code when posting message: {request.StatusCode}\nResponse: {content}");
            return;
        }
    }

    /// <summary>
    /// Обработчик серверного бана. Отправляет embed с именем, причиной,
    /// сроком и админом.
    /// </summary>
    public void OnBan(BanEvent e)
    {
        if (String.IsNullOrEmpty(_webhookUrl))
            return;

        var expires = e.Expires == null
            ? Loc.GetString("discord-permanent")
            : Loc.GetString("discord-expires-at", ("date", e.Expires));

        var message = Loc.GetString("discord-ban-msg",
            ("username", e.Username),
            ("expires", expires),
            ("reason", e.Reason),
            ("severity", GetSeverityLocale(e.Severity)));

        var payload = new WebhookPayload
        {
            Username = _serverName,
            Embeds = new List<Embed>
            {
                new()
                {
                    Description = message,
                    Color = SeverityToColor(e.Severity),
                    Footer = new EmbedFooter { Text = e.AdminUsername },
                },
            },
        };

        SendDiscordMessage(payload);
    }

    /// <summary>
    /// Обработчик бана на роли (job + antag). Отправляет embed со списком
    /// всех забаненных ролей одним сообщением.
    /// </summary>
    public void OnRoleBan(RoleBanEvent e)
    {
        if (String.IsNullOrEmpty(_webhookUrl))
            return;

        var expires = e.Expires == null
            ? Loc.GetString("discord-permanent")
            : Loc.GetString("discord-expires-at", ("date", e.Expires));

        // Объединяем список ролей через запятую.
        var roles = string.Join(", ", e.RoleNames);

        var message = Loc.GetString("discord-roleban-msg",
            ("username", e.Username),
            ("roles", roles),
            ("expires", expires),
            ("reason", e.Reason),
            ("severity", GetSeverityLocale(e.Severity)));

        var payload = new WebhookPayload
        {
            Username = _serverName,
            Embeds = new List<Embed>
            {
                new()
                {
                    Description = message,
                    Color = SeverityToColor(e.Severity),
                    Footer = new EmbedFooter { Text = e.AdminUsername },
                },
            },
        };

        SendDiscordMessage(payload);
    }

    /// <summary>
    /// Обработчик бана на отдел. Заглушка — логика не реализована.
    /// </summary>
    public void OnDepartmentBan(DepartmentBanEvent e)
    {
        if (String.IsNullOrEmpty(_webhookUrl))
            return;
    }

    /// <summary>
    /// Возвращает локализованное название серьёзности бана.
    /// Использует ключи из administration/ui/admin-notes.ftl апстрима.
    /// </summary>
    private string GetSeverityLocale(NoteSeverity severity) => severity switch
    {
        NoteSeverity.None => Loc.GetString("admin-note-editor-severity-none"),
        NoteSeverity.Minor => Loc.GetString("admin-note-editor-severity-low"),
        NoteSeverity.Medium => Loc.GetString("admin-note-editor-severity-medium"),
        NoteSeverity.High => Loc.GetString("admin-note-editor-severity-high"),
        _ => Loc.GetString("admin-note-editor-severity-none"),
    };

    /// <summary>
    /// Преобразует серьёзность бана в цвет embed'а (HEX int).
    /// </summary>
    private static int SeverityToColor(NoteSeverity severity) => severity switch
    {
        NoteSeverity.None => 0x6aa84f,   // зелёный
        NoteSeverity.Minor => 0x45818e,  // сине-зелёный
        NoteSeverity.Medium => 0xf1c232, // жёлтый
        NoteSeverity.High => 0xff0000,   // красный
        _ => 0xff0000,
    };

    private struct WebhookPayload
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; } = "";

        [JsonPropertyName("embeds")]
        public List<Embed>? Embeds { get; set; } = null;

        [JsonPropertyName("allowed_mentions")]
        public Dictionary<string, string[]> AllowedMentions { get; set; } =
            new()
            {
                { "parse", Array.Empty<string>() },
            };

        public WebhookPayload()
        {
        }
    }

    // https://discord.com/developers/docs/resources/channel#embed-object-embed-structure
    private struct Embed
    {
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("color")]
        public int Color { get; set; } = 0;

        [JsonPropertyName("footer")]
        public EmbedFooter? Footer { get; set; } = null;

        public Embed()
        {
        }
    }

    // https://discord.com/developers/docs/resources/channel#embed-object-embed-footer-structure
    private struct EmbedFooter
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        public EmbedFooter()
        {
        }
    }

    // https://discord.com/developers/docs/resources/webhook#webhook-object-webhook-structure
    private struct WebhookData
    {
        [JsonPropertyName("guild_id")]
        public string? GuildId { get; set; } = null;

        [JsonPropertyName("channel_id")]
        public string? ChannelId { get; set; } = null;

        public WebhookData()
        {
        }
    }
}
