using Content.Shared.Database;

namespace Content.Shared.GameTicking;

/// <summary>
/// Событие создания role-бана (бана на роль/антага).
/// Raise'ится в BanManager.CreateRoleBan для каждого пользователя.
/// Содержит список имён всех забаненных ролей (job + antag) — одно событие
/// на пользователя, независимо от количества ролей.
///
/// Слушатель — BansNotificationsSystem, отправляет одно embed-сообщение
/// в Discord со списком всех забаненных ролей.
/// </summary>
public sealed class RoleBanEvent : EntityEventArgs
{
    public string Username { get; }

    /// <summary>
    /// Локализованные имена всех забаненных ролей (job + antag).
    /// Например: ["Captain", "Clown", "Traitor"].
    /// </summary>
    public List<string> RoleNames { get; }

    /// <summary>
    /// Время истечения бана. null = навсегда.
    /// </summary>
    public DateTimeOffset? Expires { get; }

    public string Reason { get; }
    public NoteSeverity Severity { get; }
    public string AdminUsername { get; }

    public RoleBanEvent(string username, List<string> roleNames, DateTimeOffset? expires, string reason, NoteSeverity severity, string adminUsername)
    {
        Username = username;
        RoleNames = roleNames;
        Expires = expires;
        Reason = reason;
        Severity = severity;
        AdminUsername = adminUsername;
    }
}
