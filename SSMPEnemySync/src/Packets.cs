using System.Collections.Generic;
using SSMP.Networking.Packet;

namespace SSMPEnemySync;

/// <summary>
/// Packet IDs for this addon's own network channel.
///
/// SSMP gives addons a private channel rather than requiring changes to its own packet enums, so
/// health traffic rides alongside the built-in entity traffic without touching SSMP itself.
/// </summary>
internal enum EnemySyncPacketId : byte {
    /// <summary>Scene host -> everyone: the authoritative health of one or more entities.</summary>
    HealthUpdate = 0,

    /// <summary>Scene client -> scene host: damage this client dealt that the host has not applied yet.</summary>
    DamageReport = 1
}

/// <summary>One entity's health, as the scene host sees it.</summary>
internal struct EntityHealth {
    public ushort EntityId;
    public int Hp;
}

/// <summary>Damage one scene client dealt to an entity it does not own.</summary>
internal struct EntityDamage {
    public ushort EntityId;
    public int Amount;
}

/// <summary>
/// Authoritative health for a batch of entities, broadcast by the scene host.
///
/// Reliable so a client cannot be left holding a stale value indefinitely, but superseded by any
/// newer batch, since only the latest health for an entity is ever interesting.
/// </summary>
internal class HealthUpdatePacket : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;

    public List<EntityHealth> Entries { get; set; } = new();

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write((ushort) Entries.Count);

        foreach (var entry in Entries) {
            packet.Write(entry.EntityId);
            packet.Write(entry.Hp);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Entries = new List<EntityHealth>();

        var count = packet.ReadUShort();
        for (var i = 0; i < count; i++) {
            Entries.Add(new EntityHealth {
                EntityId = packet.ReadUShort(),
                Hp = packet.ReadInt()
            });
        }
    }
}

/// <summary>
/// Damage dealt by a client that is not the scene host, to be applied by the host.
///
/// Reliable and never superseded: each report is a distinct decrement, so dropping one silently
/// loses that player's damage, which is the exact bug this addon exists to fix.
/// </summary>
internal class DamageReportPacket : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    public List<EntityDamage> Entries { get; set; } = new();

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write((ushort) Entries.Count);

        foreach (var entry in Entries) {
            packet.Write(entry.EntityId);
            packet.Write(entry.Amount);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Entries = new List<EntityDamage>();

        var count = packet.ReadUShort();
        for (var i = 0; i < count; i++) {
            Entries.Add(new EntityDamage {
                EntityId = packet.ReadUShort(),
                Amount = packet.ReadInt()
            });
        }
    }
}
