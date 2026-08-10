using Content.Server.Administration;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Administration;
using Content.Shared.Players.PlayTimeTracking;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server.Aavikko.Administration.Commands;

[AdminCommand(AdminFlags.AddRolePlayTime)]
public sealed partial class AddGeneralPlayTimeCommand : IConsoleCommand
{
    private const int MaxMinutes = 1000000;

    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private PlayTimeTrackingManager _playTimeTracking = default!;

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
        if (!_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
            return;
        }

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

        _playTimeTracking.AddTimeToOverallPlaytime(player, TimeSpan.FromMinutes(minutes));
        var overall = _playTimeTracking.GetOverallPlaytime(player);

        shell.WriteLine(Loc.GetString(
            "cmd-addgeneralplaytime-succeed",
            ("username", userName),
            ("time", overall)));
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
        if (!_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
            return;
        }

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

        _playTimeTracking.AddTimeToTracker(player, role, TimeSpan.FromMinutes(minutes));
        var time = _playTimeTracking.GetPlayTimeForTracker(player, role);
        shell.WriteLine(Loc.GetString("cmd-addroleplaytime-succeed",
            ("username", userName),
            ("role", role),
            ("time", time)));
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
        if (!_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
            return;
        }

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

        switch (department.ToLowerInvariant())
        {
            case "cargo":
                AddTimeForJobs(player, minutes, "JobCargoTechnician", "JobQuartermaster", "JobSalvageSpecialist");
                break;
            case "civilian":
                AddTimeForJobs(player, minutes, "JobBartender", "JobChef", "JobClown", "JobJanitor", "JobMime", "JobMusician", "JobServiceWorker", "JobVisitor", "JobLibrarian");
                break;
            case "command":
                AddTimeForJobs(player, minutes, "JobCaptain", "JobHeadOfPersonnel", "JobChiefEngineer", "JobChiefMedicalOfficer", "JobHeadOfSecurity", "JobResearchDirector", "JobCentralCommandOfficial");
                break;
            case "engineering":
                AddTimeForJobs(player, minutes, "JobStationEngineer", "JobAtmosphericTechnician", "JobTechnicalAssistant");
                break;
            case "medical":
                AddTimeForJobs(player, minutes, "JobMedicalDoctor", "JobMedicalIntern", "JobParamedic", "JobBrigmedic", "JobPsychologist");
                break;
            case "security":
                AddTimeForJobs(player, minutes, "JobSecurityOfficer", "JobWarden", "JobDetective", "JobSecurityCadet", "JobERTSecurity");
                break;
            case "science":
                AddTimeForJobs(player, minutes, "JobScientist", "JobResearchAssistant", "JobResearchDirector", "JobChemist");
                break;
            case "specific":
                AddTimeForJobs(player, minutes, "JobBorg", "JobChaplain", "JobLawyer", "JobReporter", "JobBoxer", "JobZookeeper");
                break;
            default:
                shell.WriteError(Loc.GetString("cmd-adddepartmentplaytime-invalid-department", ("department", department)));
                return;
        }

        shell.WriteLine(Loc.GetString("cmd-adddepartmentplaytime-succeed", ("username", userName), ("department", department), ("minutes", minutes)));
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
        if (!_playerManager.TryGetSessionByUsername(userName, out var player))
        {
            shell.WriteError(Loc.GetString("parse-session-fail", ("username", userName)));
            return;
        }

        AddTimeForAllRoles(player, MinutesToAdd);

        shell.WriteLine(Loc.GetString(
            "cmd-unlockEveryRole-succeed",
            ("username", userName),
            ("minutes", MinutesToAdd)));
    }

    private void AddTimeForAllRoles(ICommonSession player, int minutes)
    {
        var allRoles = new[]
        {
            "JobCargoTechnician", "JobQuartermaster", "JobSalvageSpecialist", // Cargo
            "JobBartender", "JobChef", "JobClown", "JobJanitor", "JobMime", "JobMusician", "JobServiceWorker", "JobVisitor", "JobBoxer", "JobZookeeper", "JobLibrarian", // Civilian
            "JobCaptain", "JobHeadOfPersonnel", "JobChiefEngineer", "JobChiefMedicalOfficer", "JobHeadOfSecurity", "JobResearchDirector", "JobCentralCommandOfficial", // Command
            "JobStationEngineer", "JobAtmosphericTechnician", "JobTechnicalAssistant", // Engineering
            "JobMedicalDoctor", "JobMedicalIntern", "JobParamedic", "JobBrigmedic", "JobPsychologist", // Medical
            "JobSecurityOfficer", "JobWarden", "JobDetective", "JobSecurityCadet", "JobERTSecurity", // Security
            "JobScientist", "JobResearchAssistant", "JobResearchDirector", "JobChemist", // Science
            "JobBorg", "JobChaplain", "JobLawyer", "JobReporter", "JobBoxer", "JobZookeeper", // Specific
        };

        foreach (var role in allRoles)
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

