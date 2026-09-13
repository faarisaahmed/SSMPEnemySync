using System.Collections.Generic;
using System.Linq;
using SSMP.Api.Server;
using SSMP.Api.Server.Networking;
using SSMP.Networking.Packet;

namespace SSMPEnemySync;

/// <summary>
/// Relays this addon's health traffic between the players in a scene.
///
/// The server deliberately holds no health state of its own. SSMP's entity model already makes one
/// client the authority for each scene, and putting a second authority on the server would mean two
/// sources of truth to reconcile. So this only forwards: damage reports travel to the scene host,
/// which owns the real enemy, and the health it broadcasts travels back to everyone else there.
///
/// Scene is the routing key, since entity IDs are only unique within a scene — they are handed out
/// in discovery order and reset when the active scene changes.
/// </summary>
public class EnemySyncServerAddon : ServerAddon {
    /// <inheritdoc />
    /// <remarks>Must match the client addon: SSMP pairs networked addons by name and version.</remarks>
    protected override string Name => "EnemySync";

    /// <inheritdoc />
    protected override string Version => "0.1.0";

    /// <inheritdoc />
    public override bool NeedsNetwork => true;

    /// <inheritdoc />
    /// <remarks>Deliberately a literal, not Addon.CurrentApiVersion: it records the API this addon
    /// was written against, so a future SSMP can refuse to load it rather than load it broken.</remarks>
    public override uint ApiVersion => 1;

    private IServerApi _api;

    private IServerAddonNetworkSender<EnemySyncPacketId> _sender;

    /// <inheritdoc />
    public override void Initialize(IServerApi serverApi) {
        _api = serverApi;

        Log.Initialize(Logger);

        _sender = serverApi.NetServer.GetNetworkSender<EnemySyncPacketId>(this);

        var receiver = serverApi.NetServer.GetNetworkReceiver<EnemySyncPacketId>(this, InstantiatePacket);

        receiver.RegisterPacketHandler<DamageReportPacket>(
            EnemySyncPacketId.DamageReport,
            (id, packet) => Relay(id, EnemySyncPacketId.DamageReport, packet)
        );
        receiver.RegisterPacketHandler<HealthUpdatePacket>(
            EnemySyncPacketId.HealthUpdate,
            (id, packet) => Relay(id, EnemySyncPacketId.HealthUpdate, packet)
        );

        Log.Info("Server-side enemy sync relay ready.");
    }

    private static IPacketData InstantiatePacket(EnemySyncPacketId packetId) => packetId switch {
        EnemySyncPacketId.HealthUpdate => new HealthUpdatePacket(),
        EnemySyncPacketId.DamageReport => new DamageReportPacket(),
        _ => null
    };

    /// <summary>
    /// Forward a packet to every other player in the sender's scene.
    ///
    /// Recipients filter by role: only the scene host acts on a damage report, only scene clients act
    /// on a health update. Sending to everyone in the scene avoids the server having to track which
    /// client currently hosts it, which SSMP can change mid-scene when a player leaves.
    /// </summary>
    private void Relay(ushort senderId, EnemySyncPacketId packetId, IPacketData packet) {
        var sender = _api.ServerManager.GetPlayer(senderId);
        if (sender == null || string.IsNullOrEmpty(sender.CurrentScene)) {
            return;
        }

        var recipients = _api.ServerManager.Players
            .Where(player => player.Id != senderId && player.CurrentScene == sender.CurrentScene)
            .Select(player => player.Id)
            .ToArray();

        if (recipients.Length == 0) {
            return;
        }

        _sender.SendSingleData(packetId, packet, recipients);
    }
}
