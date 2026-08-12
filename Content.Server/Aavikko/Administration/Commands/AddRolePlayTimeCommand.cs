using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Administration;
using Content.Shared.Players.PlayTimeTracking;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Aavikko.Administration.Commands;

// Aavikko start: Commands for adding playtime to players (online OR offline).
// Original author: KoTiKo43 (commit bb6a432a1e).
// Patched to support offline players — if the player is not currently online,
// we look up their NetUserId via GetPlayerRecordByUserName, read their existing
// playtime trackers from the DB, add the requested time, and write back.
// The DB layer uses REPLACE semantics (ent.TimeSpent = time), so we have to
// read-then-write to avoid clobbering existing playtime.

[AdminCommand(AdminFlags.AddRolePlayTime)]
public sealed partial class AddGeneralPlayTimeCommand : IConsoleCommand
{
    private const int MaxMinutes = 1000000;

    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private PlayTimeTrackingManager _playTimeTracking = default!;
    [Dependency] private IServerDbManager _db = default!; // Aavikko: offline support

    public string Command => "addgeneralplaytime";
    public string Description => Loc.GetString("cmd-addgeneralplaytime-desc");
    public string Help => Loc.GetString("cmd-addgeneralplaytime-help", ("command", Command));

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Loc.GetString("cmd-addgeneralplaytime-error-args"));
            return;
        }

        var userName = args[0];

        if (!int.TryParse(args[1], out var minutes))
        {
            shell.WriteError(Loc.GetString("parse-minutes-fail", ("minutes", args[1])));
            return;
        }

        if (minutes > MaxMinutes)
        {
            shell.WriteError(Loc.GetString("cmd-addgeneralplaytime-max-limit", ("minutes", MaxMinutes)));
            return;
        }

        // Aavikko start: try online first, fall back to offline DB write
        if (_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            _playTimeTracking.AddTimeToOverallPlaytime(player, TimeSpan.FromMinutes(minutes));
            var overall = _playTimeTracking.GetOverallPlaytime(player);

            shell.WriteLine(Loc.GetString(
                "cmd-addgeneralplaytime-succeed",
                ("username", userName),
                ("time", overall)));
            return;
        }

        // Offline path
        // Aavikko start: wrap async DB ops in try-catch — async void crashes the server on unhandled exception
        try
        {
            var record = await _db.GetPlayerRecordByUserName(userName);
            if (record is null)
            {
                shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
                return;
            }

            var tracker = PlayTimeTrackingShared.TrackerOverall.Id;
            var newTime = await OfflinePlayTimeHelpers.AddOfflineTimeAsync(_db, record.UserId, tracker, TimeSpan.FromMinutes(minutes));

            shell.WriteLine(Loc.GetString(
                "cmd-addgeneralplaytime-succeed-offline",
                ("username", userName),
                ("time", newTime)));
        }
        catch (Exception e)
        {
            shell.WriteError($"addgeneralplaytime: offline DB update failed: {e.Message}");

        }
        // Aavikko end
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(CompletionHelper.SessionNames(),
                Loc.GetString("cmd-addgeneralplaytime-arg-user"));

        if (args.Length == 2)
            return CompletionResult.FromHint(Loc.GetString("cmd-addgeneralplaytime-arg-minutes"));

        return CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.AddRolePlayTime)]
public sealed partial class AddRolePlayTimeCommand : IConsoleCommand
{
    private const int MaxMinutes = 1000000;

    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private PlayTimeTrackingManager _playTimeTracking = default!;
    [Dependency] private IServerDbManager _db = default!; // Aavikko: offline support

    public string Command => "addroleplaytime";
    public string Description => Loc.GetString("cmd-addroleplaytime-desc");
    public string Help => Loc.GetString("cmd-addroleplaytime-help", ("command", Command));

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Loc.GetString("cmd-addroleplaytime-error-args"));
            return;
        }

        var userName = args[0];
        var role = args[1];

        var m = args[2];
        if (!int.TryParse(m, out var minutes))
        {
            shell.WriteError(Loc.GetString("parse-minutes-fail", ("minutes", m)));
            return;
        }

        if (minutes > MaxMinutes)
        {
            shell.WriteError(Loc.GetString("cmd-addroleplaytime-max-limit", ("minutes", MaxMinutes)));
            return;
        }

        // Aavikko start: try online first, fall back to offline DB write
        if (_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            _playTimeTracking.AddTimeToTracker(player, role, TimeSpan.FromMinutes(minutes));
            var time = _playTimeTracking.GetPlayTimeForTracker(player, role);
            shell.WriteLine(Loc.GetString("cmd-addroleplaytime-succeed",
                ("username", userName),
                ("role", role),
                ("time", time)));
            return;
        }

        // Offline path
        // Aavikko start: wrap async DB ops in try-catch — async void crashes the server on unhandled exception
        try
        {
            var record = await _db.GetPlayerRecordByUserName(userName);
            if (record is null)
            {
                shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
                return;
            }

            var newTime = await OfflinePlayTimeHelpers.AddOfflineTimeAsync(_db, record.UserId, role, TimeSpan.FromMinutes(minutes));

            shell.WriteLine(Loc.GetString("cmd-addroleplaytime-succeed-offline",
                ("username", userName),
                ("role", role),
                ("time", newTime)));
        }
        catch (Exception e)
        {
            shell.WriteError($"addroleplaytime: offline DB update failed: {e.Message}");

        }
        // Aavikko end
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                CompletionHelper.SessionNames(players: _playerManager),
                Loc.GetString("cmd-addroleplaytime-arg-user"));
        }

        if (args.Length == 2)
        {
            return CompletionResult.FromHintOptions(
                CompletionHelper.PrototypeIDs<PlayTimeTrackerPrototype>(),
                Loc.GetString("cmd-addroleplaytime-arg-role"));
        }

        if (args.Length == 3)
            return CompletionResult.FromHint(Loc.GetString("cmd-addroleplaytime-arg-minutes"));

        return CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.AddRolePlayTime)]
public sealed partial class AddDepartmentPlayTimeCommand : IConsoleCommand
{
    private const int MaxMinutes = 1000000;

    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private PlayTimeTrackingManager _playTimeTracking = default!;
    [Dependency] private IServerDbManager _db = default!; // Aavikko: offline support

    public string Command => "adddepartmentplaytime";
    public string Description => Loc.GetString("cmd-adddepartmentplaytime-desc");
    public string Help => Loc.GetString("cmd-adddepartmentplaytime-help", ("command", Command));

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Loc.GetString("cmd-adddepartmentplaytime-error-args"));
            return;
        }

        var department = args[0];
        var userName = args[1];

        if (!int.TryParse(args[2], out var minutes))
        {
            shell.WriteError(Loc.GetString("parse-minutes-fail", ("minutes", args[2])));
            return;
        }

        if (minutes > MaxMinutes)
        {
            shell.WriteError(Loc.GetString("cmd-adddepartmentplaytime-max-limit", ("minutes", MaxMinutes)));
            return;
        }

        string[] jobs;
        switch (department.ToLowerInvariant())
        {
            case "cargo":
                jobs = new[] { "JobCargoTechnician", "JobQuartermaster", "JobSalvageSpecialist" };
                break;
            case "civilian":
                jobs = new[] { "JobBartender", "JobChef", "JobClown", "JobJanitor", "JobMime", "JobMusician", "JobServiceWorker", "JobVisitor", "JobLibrarian" };
                break;
            case "command":
                jobs = new[] { "JobCaptain", "JobHeadOfPersonnel", "JobChiefEngineer", "JobChiefMedicalOfficer", "JobHeadOfSecurity", "JobResearchDirector", "JobCentralCommandOfficial" };
                break;
            case "engineering":
                jobs = new[] { "JobStationEngineer", "JobAtmosphericTechnician", "JobTechnicalAssistant" };
                break;
            case "medical":
                jobs = new[] { "JobMedicalDoctor", "JobMedicalIntern", "JobParamedic", "JobBrigmedic", "JobPsychologist" };
                break;
            case "security":
                jobs = new[] { "JobSecurityOfficer", "JobWarden", "JobDetective", "JobSecurityCadet", "JobERTSecurity" };
                break;
            case "science":
                jobs = new[] { "JobScientist", "JobResearchAssistant", "JobResearchDirector", "JobChemist" };
                break;
            case "specific":
                jobs = new[] { "JobBorg", "JobChaplain", "JobLawyer", "JobReporter", "JobBoxer", "JobZookeeper" };
                break;
            default:
                shell.WriteError(Loc.GetString("cmd-adddepartmentplaytime-invalid-department", ("department", department)));
                return;
        }

        // Aavikko start: try online first, fall back to offline DB write
        if (_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            AddTimeForJobs(player, minutes, jobs);
            shell.WriteLine(Loc.GetString("cmd-adddepartmentplaytime-succeed", ("username", userName), ("department", department), ("minutes", minutes)));
            return;
        }

        // Offline path
        // Aavikko start: wrap async DB ops in try-catch — async void crashes the server on unhandled exception
        try
        {
            var record = await _db.GetPlayerRecordByUserName(userName);
            if (record is null)
            {
                shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
                return;
            }

            await OfflinePlayTimeHelpers.AddOfflineTimeForJobsAsync(_db, record.UserId, minutes, jobs);
            shell.WriteLine(Loc.GetString("cmd-adddepartmentplaytime-succeed-offline",
                ("username", userName),
                ("department", department),
                ("minutes", minutes)));
        }
        catch (Exception e)
        {
            shell.WriteError($"adddepartmentplaytime: offline DB update failed: {e.Message}");

        }
        // Aavikko end
    }

    private void AddTimeForJobs(ICommonSession player, int minutes, params string[] jobs)
    {
        foreach (var job in jobs)
        {
            _playTimeTracking.AddTimeToTracker(player, job, TimeSpan.FromMinutes(minutes));
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(
                new[] { "cargo", "civilian", "command", "engineering", "medical", "security", "science", "specific" },
                Loc.GetString("cmd-adddepartmentplaytime-arg-department"));

        if (args.Length == 2)
            return CompletionResult.FromHintOptions(
                CompletionHelper.SessionNames(players: _playerManager),
                Loc.GetString("cmd-adddepartmentplaytime-arg-user"));

        if (args.Length == 3)
            return CompletionResult.FromHint(Loc.GetString("cmd-adddepartmentplaytime-arg-minutes"));

        return CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.AddRolePlayTime)]
public sealed partial class UnlockEveryRoleCommand : IConsoleCommand
{
    private const int MinutesToAdd = 6000;

    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private PlayTimeTrackingManager _playTimeTracking = default!;
    [Dependency] private IServerDbManager _db = default!; // Aavikko: offline support

    public string Command => "unlockEveryFuckingRole";
    public string Description => "Adds 1000 minutes to every role for the specified player.";
    public string Help => Loc.GetString("cmd-unlockEveryRole-help", ("command", Command));

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Loc.GetString("cmd-unlockEveryRole-error-args"));
            return;
        }

        var userName = args[0];

        // Aavikko start: also include the overall tracker (cast to string).
        // NOTE: original KoTiKo43 list had duplicates (JobResearchDirector in
        // both command and science; JobBoxer/JobZookeeper in both civilian and
        // specific). With offline DB writes, duplicates cause UNIQUE constraint
        // violations on the second Add. Use Distinct() to be safe.
        var allRoles = new string[]
        {
            // Cargo
            "JobCargoTechnician", "JobQuartermaster", "JobSalvageSpecialist",
            // Civilian
            "JobBartender", "JobChef", "JobClown", "JobJanitor", "JobMime", "JobMusician", "JobServiceWorker", "JobVisitor", "JobBoxer", "JobZookeeper", "JobLibrarian",
            // Command
            "JobCaptain", "JobHeadOfPersonnel", "JobChiefEngineer", "JobChiefMedicalOfficer", "JobHeadOfSecurity", "JobResearchDirector", "JobCentralCommandOfficial",
            // Engineering
            "JobStationEngineer", "JobAtmosphericTechnician", "JobTechnicalAssistant",
            // Medical
            "JobMedicalDoctor", "JobMedicalIntern", "JobParamedic", "JobBrigmedic", "JobPsychologist",
            // Security
            "JobSecurityOfficer", "JobWarden", "JobDetective", "JobSecurityCadet", "JobERTSecurity",
            // Science
            "JobScientist", "JobResearchAssistant", "JobChemist",
            // Specific
            "JobBorg", "JobChaplain", "JobLawyer", "JobReporter",
            // Overall
            PlayTimeTrackingShared.TrackerOverall.Id,
        }.Distinct().ToArray();
        // Aavikko end

        // Aavikko start: try online first, fall back to offline DB write
        if (_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            AddTimeForAllRoles(player, MinutesToAdd, allRoles);
            shell.WriteLine(Loc.GetString(
                "cmd-unlockEveryRole-succeed",
                ("username", userName),
                ("minutes", MinutesToAdd)));
            return;
        }

        // Offline path
        // Aavikko start: wrap async DB ops in try-catch — async void crashes the server on unhandled exception
        try
        {
            var record = await _db.GetPlayerRecordByUserName(userName);
            if (record is null)
            {
                shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
                return;
            }

            await OfflinePlayTimeHelpers.AddOfflineTimeForJobsAsync(_db, record.UserId, MinutesToAdd, allRoles);
            shell.WriteLine(Loc.GetString(
                "cmd-unlockEveryRole-succeed-offline",
                ("username", userName),
                ("minutes", MinutesToAdd)));
        }
        catch (Exception e)
        {
            shell.WriteError($"unlockEveryFuckingRole: offline DB update failed: {e.Message}");

        }
        // Aavikko end
    }

    private void AddTimeForAllRoles(ICommonSession player, int minutes, string[] roles)
    {
        foreach (var role in roles)
        {
            _playTimeTracking.AddTimeToTracker(player, role, TimeSpan.FromMinutes(minutes));
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(CompletionHelper.SessionNames(players: _playerManager),
                Loc.GetString("cmd-unlockEveryRole-arg-user"));

        return CompletionResult.Empty;
    }
}

// Aavikko start: helpers for offline playtime updates.
// These read existing tracker values from the DB, add the requested time, and
// write back. UpdatePlayTimes uses REPLACE semantics (ent.TimeSpent = time),
// so we MUST read first to avoid clobbering.

public static class OfflinePlayTimeHelpers
{
    /// <summary>
    /// Add time to a single tracker for an offline player. Returns the new total.
    /// </summary>
    public static async Task<TimeSpan> AddOfflineTimeAsync(
        IServerDbManager db,
        NetUserId userId,
        string tracker,
        TimeSpan toAdd)
    {
        var existing = await db.GetPlayTimes(userId.UserId);
        var current = existing.FirstOrDefault(p => p.Tracker == tracker)?.TimeSpent ?? TimeSpan.Zero;
        var newTime = current + toAdd;

        await db.UpdatePlayTimes(new[]
        {
            new PlayTimeUpdate(userId, tracker, newTime),
        });

        return newTime;
    }

    /// <summary>
    /// Add the same amount of time to multiple trackers for an offline player.
    /// Reads all existing trackers in one DB query, then writes them all back in one call.
    /// Aavikko: deduplicates the job list — UpdatePlayTimes would otherwise try to INSERT
    /// the same (player_id, tracker) twice, causing UNIQUE constraint violations.
    /// </summary>
    public static async Task AddOfflineTimeForJobsAsync(
        IServerDbManager db,
        NetUserId userId,
        int minutes,
        params string[] jobs)
    {
        var toAdd = TimeSpan.FromMinutes(minutes);
        var existing = await db.GetPlayTimes(userId.UserId);
        var existingDict = existing.ToDictionary(p => p.Tracker, p => p.TimeSpent);

        // Aavikko: dedupe — keep first occurrence so we don't double-add or violate UNIQUE
        var seen = new HashSet<string>();
        var updates = new List<PlayTimeUpdate>(jobs.Length);
        foreach (var job in jobs)
        {
            if (!seen.Add(job))
                continue; // duplicate tracker, skip

            existingDict.TryGetValue(job, out var current);
            var newTime = current + toAdd;
            updates.Add(new PlayTimeUpdate(userId, job, newTime));
        }

        await db.UpdatePlayTimes(updates);
    }
}
// Aavikko end
