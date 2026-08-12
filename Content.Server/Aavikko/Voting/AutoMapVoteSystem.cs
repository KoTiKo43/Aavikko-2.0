using Content.Server.Voting.Managers;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Voting;
using Robust.Shared.Configuration;

namespace Content.Server.Corvax.Voting;

/// <summary>
/// Автоматически запускает голосование за карту при открытии лобби после конца раунда.
/// Голосование завершается в лобби, когда CanUpdateMap() == true,
/// поэтому результат корректно применяется через SelectMap().
/// </summary>
public sealed partial class AutoMapVoteSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IVoteManager _voteManager = default!;

    private bool _enabled;

    public override void Initialize()
    {
        base.Initialize();
        _cfg.OnValueChanged(CCVars.AutoMapVote, v => _enabled = v, true);
        // Срабатывает при переходе в PreRoundLobby (после конца раунда)
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        if (!_enabled) return;
        // initiator: null → серверное голосование, обходит vote.map_enabled
        _voteManager.CreateStandardVote(initiator: null, StandardVoteType.Map);
    }
}
