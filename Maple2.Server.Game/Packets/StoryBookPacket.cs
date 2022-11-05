﻿using System;
using System.Collections.Generic;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Core.Packets;
using Maple2.Tools.Extensions;

namespace Maple2.Server.Game.Packets;

public static class StoryBookPacket {

    public static ByteWriter Load(int storyBookId) {
        var pWriter = Packet.Of(SendOp.StoryBook);
        pWriter.WriteInt(storyBookId);

        return pWriter;
    }
}
