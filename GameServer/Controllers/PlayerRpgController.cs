using Common.Server.Routing;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Controllers;

public class PlayerRpgController(PlayerComponent player) : PlayerBaseController(player)
{
    public override IReadOnlyList<IRouter> Routes() =>
        NewRouter()
            .With<ReqMove>(OnMove)
            .With<ReqAttack>(OnAttack)
            .With<ReqGameChat>(OnGameChat)
            .With<ReqChooseWeapon>(OnChooseWeapon)
            .Build();

    private void OnMove(ReqMove req)
        => Player.Room.CurrentRoom?.Stage?.ProcessMove(Player, req.X, req.Y);

    private void OnAttack(ReqAttack req)
        => Player.Room.CurrentRoom?.Stage?.ProcessAttack(Player, req.TargetMonsterId);

    private void OnGameChat(ReqGameChat req)
        => Player.Room.CurrentRoom?.Stage?.ProcessChat(Player, req.Message);

    private void OnChooseWeapon(ReqChooseWeapon req)
        => Player.Room.CurrentRoom?.Stage?.ProcessChooseWeapon(Player, req.WeaponId);
}
