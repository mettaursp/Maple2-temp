﻿using System;
using System.Collections.Generic;
using System.Linq;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Core.PacketHandlers;
using Maple2.Server.Core.Packets;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Server.Game.Util;

namespace Maple2.Server.Game.PacketHandlers;

public class ItemUseHandler : PacketHandler<GameSession> {
    public override RecvOp OpCode => RecvOp.RequestItemUse;

    #region Autofac Autowired
    // ReSharper disable MemberCanBePrivate.Global
    public ItemMetadataStorage ItemMetadata { get; init; } = null!;
    // ReSharper restore All
    #endregion

    public override void Handle(GameSession session, IByteReader packet) {
        long itemUid = packet.ReadLong();

        Item? item = session.Item.Inventory.Get(itemUid);
        if (item == null) {
            Logger.Warning("RequestItemUse for invalid item:{ItemUid}", itemUid);
            return;
        }

        switch (item.Metadata.Function?.Type) {
            case ItemFunction.StoryBook:
                HandleStoryBook(session, item);
                break;
            case ItemFunction.ChatEmoticonAdd:
                HandleChatSticker(session, item);
                break;
            case ItemFunction.TitleScroll:
                HandleTitleScroll(session, item);
                break;
            case ItemFunction.OpenCoupleEffectBox:
                HandleBuddyBadgeBox(session, packet, item);
                break;
            default:
                Logger.Warning("Unhandled item function: {Name}", item.Metadata.Function?.Type);
                return;
        }
    }

    private static void HandleStoryBook(GameSession session, Item item) {
        if (!int.TryParse(item.Metadata.Function?.Parameters, out int storyBookId)) {
            return;
        }

        session.Send(StoryBookPacket.Load(storyBookId));
    }

    private static void HandleChatSticker(GameSession session, Item item) {
        Dictionary<string, string> parameters = XmlParseUtil.GetParameters(item.Metadata.Function?.Parameters);

        if (!parameters.ContainsKey("id") || !int.TryParse(parameters["id"], out int stickerSetId)) {
            session.Send(ChatStickerPacket.Error(ChatStickerError.s_msg_chat_emoticon_add_failed));
            return;
        }

        int duration = 0;
        if (parameters.TryGetValue("durationSec", out string? durationString)) {
            int.TryParse(durationString, out duration);
        }

        long existingTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (session.Player.Value.Unlock.StickerSets.ContainsKey(stickerSetId)) {
            existingTime = session.Player.Value.Unlock.StickerSets[stickerSetId];
            if (existingTime == long.MaxValue) {
                session.Send(ChatStickerPacket.Error(ChatStickerError.s_msg_chat_emoticon_add_failed_already_exist));
                return;
            }
        }

        long newTime = existingTime + duration;
        if (duration == 0) {
            newTime = long.MaxValue;
        }

        if (newTime <= existingTime) {
            session.Send(ChatStickerPacket.Error(ChatStickerError.s_msg_chat_emoticon_add_failed_already_exist));
            return;
        }

        if (!session.Item.Inventory.Consume(item.Uid, 1)) {
            return;
        }

        session.Player.Value.Unlock.StickerSets[stickerSetId] = newTime;

        session.Send(ChatStickerPacket.Add(item, new ChatSticker(stickerSetId, session.Player.Value.Unlock.StickerSets[stickerSetId])));
    }

    private static void HandleTitleScroll(GameSession session, Item item) {
        if (!int.TryParse(item.Metadata.Function?.Parameters, out int titleId)) {
            return;
        }

        if (session.Player.Value.Unlock.Titles.Contains(titleId)) {
            session.Send(ChatPacket.Alert(StringCode.s_title_scroll_duplicate_err));
            return;
        }

        if (session.Item.Inventory.Consume(item.Uid, 1)) {
            session.Send(UserEnvPacket.AddTitle(titleId));
            session.Player.Value.Unlock.Titles.Add(titleId);
        }
    }

    private void HandleBuddyBadgeBox(GameSession session, IByteReader packet, Item item) {
        string targetUser = packet.ReadUnicodeString();

        if (targetUser == session.Player.Value.Character.Name) {
            session.Send(NoticePacket.MessageBox(StringCode.s_couple_effect_error_openbox_myself_char));
            return;
        }

        using GameStorage.Request db = session.GameStorage.Context();
        CharacterInfo? receiverCharacterInfo = db.GetCharacterInfo(targetUser);
        if (receiverCharacterInfo == default) {
            session.Send(NoticePacket.MessageBox((StringCode.s_couple_effect_error_openbox_charname)));
            return;
        }

        if (receiverCharacterInfo.AccountId == session.Player.Value.Character.AccountId) {
            session.Send(NoticePacket.MessageBox(StringCode.s_couple_effect_error_openbox_myself_account));
            return;
        }

        int[] buddyBadgeBoxParams = item.Metadata.Function?.Parameters.Split(',').Select(int.Parse).ToArray() ?? Array.Empty<int>();
        if (buddyBadgeBoxParams.Length != 2) {
            session.Send(NoticePacket.MessageBox(StringCode.s_couple_effect_error_openbox_unknown));
            Logger.Error("Invalid buddy badge box parameters: {Parameters}", item.Metadata.Function?.Parameters);
            return;
        }

        if (!ItemMetadata.TryGet(buddyBadgeBoxParams[0], out ItemMetadata? itemMetadata)) {
            session.Send(NoticePacket.MessageBox(StringCode.s_couple_effect_error_openbox_unknown));
            Logger.Error("Unknown buddy badge box item Id: {Parameters}", buddyBadgeBoxParams[0]);
            return;
        }

        var selfBadge = new Item(itemMetadata, buddyBadgeBoxParams[1]) {
            CoupleInfo = new ItemCoupleInfo(receiverCharacterInfo.CharacterId, receiverCharacterInfo.Name, true),
        };

        if (!session.Item.Inventory.CanAdd(selfBadge)) {
            session.Send(NoticePacket.MessageBox(StringCode.s_gem_error_inventory_full));
            return;
        }

        if (!session.Item.Inventory.Consume(item.Uid, 1)) {
            Logger.Error("Failed to use buddy badge box: {ItemUid}", item.Uid);
            return;
        }

        var receiverMail = new Mail() {
            ReceiverId = receiverCharacterInfo.CharacterId,
            Type = MailType.System,
            ContentArgs = new[] {
                ("str", $"{session.Player.Value.Character.Name}"),
            },
        };

        receiverMail.SetTitle(StringCode.s_couple_effect_mail_title_receiver);
        receiverMail.SetContent(StringCode.s_couple_effect_mail_content_receiver);
        receiverMail.SetSenderName(StringCode.s_couple_effect_mail_sender);

        receiverMail = db.CreateMail(receiverMail);
        if (receiverMail == null) {
            session.Send(NoticePacket.MessageBox((StringCode.s_couple_effect_error_openbox_unknown)));
            throw new InvalidOperationException($"Failed to create buddy badge mail for receiver character id: {receiverCharacterInfo.CharacterId}");
            return;
        }

        Item? receiverItem = db.CreateItem(receiverMail.Id,
            new Item(itemMetadata, buddyBadgeBoxParams[1]) {
                CoupleInfo = new ItemCoupleInfo(session.Player.Value.Character.Id, session.Player.Value.Character.Name),
            });
        if (receiverItem == null) {
            throw new InvalidOperationException($"Failed to create buddy badge: {receiverItem.Id}");
        }

        receiverMail.Items.Add(receiverItem);

        try {
            session.World.MailNotification(new MailNotificationRequest {
                CharacterId = receiverCharacterInfo.CharacterId,
                MailId = receiverMail.Id,
            });
        } catch { /* ignored */ }

        session.Item.Inventory.Add(selfBadge, true);
        session.Send(NoticePacket.MessageBox(new InterfaceText(StringCode.s_couple_effect_mail_send_partner, receiverCharacterInfo.Name)));
    }
}
