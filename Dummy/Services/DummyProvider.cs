﻿using Autofac;
using Cysharp.Threading.Tasks;
using Dummy.API;
using Dummy.Extensions;
using Dummy.Models;
using Dummy.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.API.Plugins;
using OpenMod.API.Prioritization;
using OpenMod.API.Users;
using OpenMod.Core.Helpers;
using OpenMod.Unturned.Users;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Dummy.Services
{
    [ServiceImplementation(Lifetime = ServiceLifetime.Singleton, Priority = Priority.Lowest)]
    public class DummyProvider : IDummyProvider, IAsyncDisposable
    {
        private readonly HashSet<DummyUser> m_Dummies;
        private readonly IPluginAccessor<Dummy> m_PluginAccessor;
        private readonly IUserManager m_UserManager;
        private readonly IUserDataStore m_UserDataStore;
        private readonly ILogger<DummyProvider> m_Logger;
        private readonly ILoggerFactory m_LoggerFactory;
        private readonly ITransportConnection m_TransportConnection;

        private IStringLocalizer m_StringLocalizer => m_PluginAccessor.Instance.LifetimeScope.Resolve<IStringLocalizer>();
        private IConfiguration m_Configuration => m_PluginAccessor.Instance.Configuration;
        private bool m_IsDisposing;

        public IReadOnlyCollection<DummyUser> Dummies => m_Dummies;

        public DummyProvider(IPluginAccessor<Dummy> pluginAccessor, IUserManager userManager,
            IUserDataStore userDataStore, ILogger<DummyProvider> logger, ILoggerFactory loggerFactory,
            ITransportConnection transportConnection)
        {
            m_Dummies = new HashSet<DummyUser>();
            m_PluginAccessor = pluginAccessor;
            m_UserManager = userManager;
            m_UserDataStore = userDataStore;
            m_Logger = logger;
            m_LoggerFactory = loggerFactory;
            m_TransportConnection = transportConnection;

            Provider.onServerDisconnected += OnServerDisconnected;
            SteamChannel.onTriggerSend += onTriggerSend;

            AsyncHelper.Schedule("Do not auto kick a dummies", DontAutoKickTask);
        }

        private void onTriggerSend(SteamPlayer player, string name, ESteamCall mode, ESteamPacket type, object[] arguments)
        {
            var dummy = Dummies.FirstOrDefault(x => x.SteamPlayer == player);
            if (dummy == null)
            {
                return;
            }
            if (name == nameof(Player.askTeleport))
            {
                // todo: works after simulation tick
                dummy.Simulation.PlayerInputPackets.Clear();
                dummy.Player.Player.transform.localPosition = (Vector3)arguments[0];
            }
            m_Logger.LogDebug($"{player.playerID.steamID} / server send {name}");
        }

        private async Task DontAutoKickTask()
        {
            while (!m_IsDisposing)
            {
                //m_Logger.LogTrace("Heartbeat dummies");
                foreach (var dummy in Dummies)
                {
                    var client = dummy.SteamPlayer;
                    client.timeLastPacketWasReceivedFromClient = Time.realtimeSinceStartup;
                }
                await Task.Delay(5000);
            }
        }

        private async Task KickTimerTask(ulong id, float timer)
        {
            if (timer == 0)
            {
                return;
            }
            m_Logger.LogDebug($"Start kick timer, will kicked after {timer} sec");
            await Task.Delay(TimeSpan.FromSeconds(timer));

            var user = await GetPlayerDummyAsync(id);
            if (user == null)
            {
                return;
            }
            m_Logger.LogDebug($"[Kick timer] => Kick dummy {id}");
            await user.Session.DisconnectAsync();
        }

        #region Events

        protected virtual void OnServerDisconnected(CSteamID steamID)
        {
            AsyncHelper.RunSync(() => RemoveDummyAsync(steamID));
        }

        #endregion Events

        private void CheckSpawn(CSteamID id)
        {
            if (m_Dummies.Any(x => x.SteamID == id))
            {
                throw new DummyContainsException(m_StringLocalizer, id.m_SteamID);
            }

            var amountDummiesConfig = m_Configuration.Get<Configuration>().Options.AmountDummies;
            if (amountDummiesConfig != 0 && Dummies.Count + 1 > amountDummiesConfig)
            {
                throw new DummyOverflowsException(m_StringLocalizer, (byte)Dummies.Count, amountDummiesConfig);
            }
        }

        public async Task<DummyUser> AddDummyAsync(CSteamID id, HashSet<CSteamID> owners)
        {
            await UniTask.SwitchToMainThread();

            CheckSpawn(id);

            var config = m_Configuration.Get<Configuration>();
            var @default = config.Default;
            var skins = config.Default.Skins;

            var dummyPlayerID = new SteamPlayerID(id, @default.CharacterId, @default.PlayerName,
                @default.CharacterName, @default.NickName, @default.SteamGroupId, @default.HWID.GetBytes());

            // todo: skins are VERY HARD to implement ;(
            var pending = new SteamPending(m_TransportConnection, dummyPlayerID, @default.IsPro, @default.FaceId,
                @default.HairId, @default.BeardId, @default.SkinColor.ToColor(), @default.Color.ToColor(),
                @default.MarkerColor.ToColor(), @default.IsLeftHanded, skins.Shirt, skins.Pants, skins.Hat,
                skins.Backpack, skins.Vest, skins.Mask, skins.Glasses, Array.Empty<ulong>(), @default.PlayerSkillset,
                @default.Language, @default.LobbyId)
            {
                hasAuthentication = true,
                hasGroup = true,
                hasProof = true
            };

            Provider.pending.Add(pending);

            PreAddDummy(dummyPlayerID);

            Provider.accept(dummyPlayerID, @default.IsPro, false, @default.FaceId, @default.HairId, @default.BeardId,
                @default.SkinColor.ToColor(), @default.Color.ToColor(), @default.MarkerColor.ToColor(),
                @default.IsLeftHanded, skins.Shirt, skins.Pants, skins.Hat, skins.Backpack, skins.Vest, skins.Mask,
                skins.Glasses, Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>(), @default.PlayerSkillset,
                @default.Language, @default.LobbyId);

            var playerDummy = new DummyUser((UnturnedUserProvider)m_UserManager.UserProviders.FirstOrDefault(c => c is UnturnedUserProvider),
                m_UserDataStore, Provider.clients.Last(), m_LoggerFactory, m_StringLocalizer, config.Options.DisableSimulations, owners);

            PostAddDummy(playerDummy);

            return playerDummy;
        }

        public async Task<DummyUser> AddCopiedDummyAsync(CSteamID id, HashSet<CSteamID> owners, UnturnedUser userCopy)
        {
            await UniTask.SwitchToMainThread();

            CheckSpawn(id);

            var config = m_Configuration.Get<Configuration>();
            var userSteamPlayer = userCopy.Player.SteamPlayer;

            // todo: maybe, also copy nickname?
            var dummyPlayerID = new SteamPlayerID(id, userSteamPlayer.playerID.characterID, "dummy", "dummy", "dummy",
                userSteamPlayer.playerID.group, userSteamPlayer.playerID.hwid);

            var pending = new SteamPending(m_TransportConnection, dummyPlayerID, userSteamPlayer.isPro,
                userSteamPlayer.face, userSteamPlayer.hair, userSteamPlayer.beard, userSteamPlayer.skin,
                userSteamPlayer.color, userSteamPlayer.markerColor, userSteamPlayer.hand,
                (ulong)userSteamPlayer.shirtItem, (ulong)userSteamPlayer.pantsItem, (ulong)userSteamPlayer.hatItem,
                (ulong)userSteamPlayer.backpackItem, (ulong)userSteamPlayer.vestItem, (ulong)userSteamPlayer.maskItem,
                (ulong)userSteamPlayer.glassesItem, Array.Empty<ulong>(), userSteamPlayer.skillset,
                userSteamPlayer.language, userSteamPlayer.lobbyID)
            {
                hasProof = true,
                hasGroup = true,
                hasAuthentication = true
            };

            Provider.pending.Add(pending);

            PreAddDummy(dummyPlayerID);

            Provider.accept(dummyPlayerID, userSteamPlayer.isPro, false, userSteamPlayer.face, userSteamPlayer.hair,
                userSteamPlayer.beard, userSteamPlayer.skin, userSteamPlayer.color, userSteamPlayer.markerColor,
                userSteamPlayer.hand, userSteamPlayer.shirtItem, userSteamPlayer.pantsItem, userSteamPlayer.hatItem,
                userSteamPlayer.backpackItem, userSteamPlayer.vestItem, userSteamPlayer.maskItem,
                userSteamPlayer.glassesItem, userSteamPlayer.skinItems, userSteamPlayer.skinTags,
                userSteamPlayer.skinDynamicProps, userSteamPlayer.skillset, userSteamPlayer.language,
                userSteamPlayer.lobbyID);

            var playerDummy = new DummyUser((UnturnedUserProvider)m_UserManager.UserProviders.FirstOrDefault(c => c is UnturnedUserProvider),
                m_UserDataStore, Provider.clients.Last(), m_LoggerFactory, m_StringLocalizer, config.Options.DisableSimulations, owners);

            PostAddDummy(playerDummy);

            return playerDummy;
        }

        public Task<DummyUser> AddDummyByParameters(CSteamID id, HashSet<CSteamID> owners, ConfigurationSettings settings)
        {
            throw new NotImplementedException();
        }

        private void PreAddDummy(SteamPlayerID dummy)
        {
            var config = m_Configuration.Get<Configuration>();

            if (config.Events.CallOnCheckValidWithExplanation)
            {
                var isValid = true;
                var explanation = string.Empty;
                try
                {
                    Provider.onCheckValidWithExplanation(new ValidateAuthTicketResponse_t
                    {
                        m_SteamID = dummy.steamID,
                        m_eAuthSessionResponse = EAuthSessionResponse.k_EAuthSessionResponseOK,
                        m_OwnerSteamID = dummy.steamID
                    }, ref isValid, ref explanation);
                }
                catch (Exception e)
                {
                    m_Logger.LogError(e, "Plugin raised an exception from onCheckValidWithExplanation: ");
                }
                if (!isValid)
                {
                    Provider.pending.RemoveAt(Provider.pending.Count - 1);
                    throw new DummyCanceledSpawnException($"Plugin reject connection a dummy({dummy.steamID}). Reason: {explanation}");
                }
            }

            if (config.Events.CallOnCheckBanStatusWithHWID)
            {
                m_TransportConnection.TryGetIPv4Address(out var ip);
                var isBanned = false;
                var banReason = string.Empty;
                var banRemainingDuration = 0U;
                if (SteamBlacklist.checkBanned(dummy.steamID, ip, out var steamBlacklistID))
                {
                    isBanned = true;
                    banReason = steamBlacklistID.reason;
                    banRemainingDuration = steamBlacklistID.getTime();
                }

                try
                {
                    Provider.onCheckBanStatusWithHWID?.Invoke(dummy, ip, ref isBanned, ref banReason, ref banRemainingDuration);
                }
                catch (Exception e)
                {
                    m_Logger.LogError(e, "Plugin raised an exception from onCheckValidWithExplanation: ");
                }

                if (isBanned)
                {
                    Provider.pending.RemoveAt(Provider.pending.Count - 1);
                    throw new DummyCanceledSpawnException($"Dummy {dummy.steamID} is banned! Ban reason: {banReason}, duration: {banRemainingDuration}");
                }
            }
        }

        private void PostAddDummy(DummyUser playerDummy)
        {
            var configuration = m_Configuration.Get<Configuration>();
            var kickTimer = configuration.Options.KickDummyAfterSeconds;
            if (kickTimer > 0)
            {
                AsyncHelper.Schedule("Kick a dummy timer", () => KickTimerTask(playerDummy.SteamID.m_SteamID, kickTimer));
            }

            UniTask.Run(() => RemoveRigidbody(playerDummy));
            if (configuration.Fun.AlwaysRotate)
            {
                UniTask.Run(() => RotateDummy(playerDummy, configuration.Fun.RotateYaw));
            }

            m_Dummies.Add(playerDummy);
        }

        private async UniTask RemoveRigidbody(DummyUser player)
        {
            await UniTask.SwitchToMainThread();
            await UniTask.DelayFrame(1);

            var movement = player.Player.Player.movement;
            var r = movement.gameObject.GetComponent<Rigidbody>();
            UnityEngine.Object.Destroy(r);
        }

        private async UniTask RotateDummy(DummyUser player, float rotateYaw)
        {
            while (player != null && !m_IsDisposing)
            {
                await UniTask.Delay(1);
                player.Simulation.Yaw += rotateYaw;
            }
        }

        public async Task<bool> RemoveDummyAsync(CSteamID id)
        {
            var playerDummy = await GetPlayerDummyAsync(id.m_SteamID);
            if (playerDummy == null)
            {
                return false;
            }
            await UniTask.SwitchToMainThread();
            await playerDummy.DisposeAsync();
            m_Dummies.Remove(playerDummy);

            return true;
        }

        public async Task ClearDummiesAsync()
        {
            await m_Dummies.DisposeAllAsync();
            m_Dummies.Clear();
        }

        public Task<CSteamID> GetAvailableIdAsync()
        {
            var result = new CSteamID(1);

            while (Dummies.Any(x => x.SteamID == result))
            {
                result.m_SteamID++;
            }
            return Task.FromResult(result);
        }

        public Task<DummyUser> GetPlayerDummyAsync(ulong id)
        {
            return Task.FromResult(Dummies.FirstOrDefault(p => p.SteamID.m_SteamID == id));
        }

        public ValueTask DisposeAsync()
        {
            if (m_IsDisposing)
            {
                return new ValueTask(Task.CompletedTask);
            }
            m_IsDisposing = true;

            Provider.onServerDisconnected -= OnServerDisconnected;
            SteamChannel.onTriggerSend -= onTriggerSend;
            return new ValueTask(ClearDummiesAsync());
        }
    }
}