﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Maple2.Database.Storage;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.PacketLib.Tools;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Microsoft.Extensions.Logging;

namespace Maple2.Server.Game.Manager.Field;

public sealed partial class FieldManager : IDisposable {
    public readonly MapMetadata Metadata;
    private readonly MapEntityMetadata entities;
    private readonly NpcMetadataStorage npcMetadata;

    private readonly ConcurrentBag<SpawnPointNPC> npcSpawns = new();

    private readonly CancellationTokenSource cancel;
    private readonly Thread thread;

    private readonly ILogger logger;

    public int MapId => Metadata.Id;
    public readonly int InstanceId;

    private FieldManager(int instanceId, MapMetadata metadata, MapEntityMetadata entities, NpcMetadataStorage npcMetadata, ILogger logger) {
        InstanceId = instanceId;
        this.Metadata = metadata;
        this.entities = entities;
        this.npcMetadata = npcMetadata;
        this.cancel = new CancellationTokenSource();
        this.thread = new Thread(Sync);
        this.logger = logger;

        foreach (Portal portal in entities.Portals.Values) {
            SpawnPortal(portal);
        }

        foreach (SpawnPointNPC spawnPointNpc in entities.NpcSpawns) {
            if (spawnPointNpc.RegenCheckTime > 0) {
                npcSpawns.Add(spawnPointNpc);
            }

            if (spawnPointNpc.SpawnOnFieldCreate) {
                for (int i = 0; i < spawnPointNpc.NpcCount; i++) {
                    // TODO: get other NpcIds too
                    int npcId = spawnPointNpc.NpcIds[0];
                    if (!npcMetadata.TryGet(npcId, out NpcMetadata? npc)) {
                        logger.LogWarning("Npc {NpcId} failed to load for map {MapId}", npcId, MapId);
                        continue;
                    }

                    SpawnNpc(npc, spawnPointNpc.Position, spawnPointNpc.Rotation);
                }
            }
        }

        thread.Start();
    }

    private void Sync() {
        while (!cancel.IsCancellationRequested) {
            foreach (FieldPlayer player in fieldPlayers.Values) {
                player.Sync();
            }
            foreach (FieldNpc npc in fieldNpcs.Values) {
                npc.Sync();
            }
            Thread.Sleep(50);
        }
    }

    public bool TryGetNpc(int objectId, [NotNullWhen(true)] out FieldNpc? npc) {
        return fieldNpcs.TryGetValue(objectId, out npc);
    }

    public bool TryGetPortal(int portalId, [NotNullWhen(true)] out Portal? portal) {
        return entities.Portals.TryGetValue(portalId, out portal);
    }

    public bool TryGetItem(int objectId, [NotNullWhen(true)] out FieldEntity<Item>? fieldItem) {
        return fieldItems.TryGetValue(objectId, out fieldItem);
    }

    public void InspectPlayer(GameSession session, long characterId) {
        foreach (FieldPlayer fieldPlayer in fieldPlayers.Values) {
            if (fieldPlayer.Value.Character.Id == characterId) {
                session.Send(PlayerInfoPacket.Load(fieldPlayer.Session));
                return;
            }
        }

        session.Send(PlayerInfoPacket.NotFound(characterId));
    }

    public void Multicast(ByteWriter packet, GameSession? sender = null) {
        foreach (FieldPlayer fieldPlayer in fieldPlayers.Values) {
            if (fieldPlayer.Session == sender) continue;
            fieldPlayer.Session.Send(packet);
        }
    }

    public void Dispose() {
        cancel.Cancel();
        thread.Join();
    }
}
