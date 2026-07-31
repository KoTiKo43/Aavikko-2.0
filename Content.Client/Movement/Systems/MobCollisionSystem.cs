using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Movement.Systems;
using Robust.Client.Player;
using Robust.Shared.Timing;

namespace Content.Client.Movement.Systems;

public sealed partial class MobCollisionSystem : SharedMobCollisionSystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _player = default!;

    public override void Update(float frameTime)
    {
        if (!CfgManager.GetCVar(CCVars.MovementMobPushing))
            return;

        if (_timing.IsFirstTimePredicted)
        {
            var player = _player.LocalEntity;

            if (MobQuery.TryComp(player, out var comp) && PhysicsQuery.TryComp(player, out var physics))
            {
                HandleCollisions((player.Value, comp, physics), frameTime);
            }
        }

        base.Update(frameTime);
    }

    private const int ThrottleTicks = 2; // Aavikko
    private GameTick _lastSentTick = GameTick.Zero; // Aavikko

    protected override void RaiseCollisionEvent(EntityUid uid, Vector2 direction, float speedMod)
    {
        var curTick = _timing.CurTick; // Aavikko
        var tickDiff = curTick.Value - _lastSentTick.Value; // Aavikko

        if (_lastSentTick == GameTick.Zero || tickDiff >= ThrottleTicks)
        {
            RaisePredictiveEvent(new MobCollisionMessage()
            {
                Direction = direction,
                SpeedModifier = speedMod,
            });
            _lastSentTick = curTick; // Aavikko
        }
    }
}
