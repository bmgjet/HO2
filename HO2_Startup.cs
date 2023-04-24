using Facepunch;
using Facepunch.Models;
using Harmony;
using Network;
using Network.Visibility;
using Oxide.Core;
using ProtoBuf;
using Rust;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using UnityEngine;
using UnityEngine.Assertions;

namespace HO2_Startup
{
    //Load Oxide and call oxidehook init
    public class main
    {
        public static void startup()
        {
            if (File.Exists(Path.Combine("RustDedicated_Data", "Managed", "Oxide.Core.dll")))
            {
                UnityEngine.Debug.LogWarning("[HO2]: Starting Oxide. Patching " + GetClassCountInNamespace("HO2_Startup") + " Hooks.");
                Interface.Initialize();
                Interface.CallHook("InitLogging");
                return;
            }
            UnityEngine.Debug.LogWarning("[HO2]: Oxide not installed!");
        }
        public static int GetClassCountInNamespace(string namespaceName)
        {
            int classCount = -1;
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.Namespace == namespaceName && type.IsClass)
                {
                    classCount++;
                }
            }
            return classCount;
        }
    }

    #region ServerMgr Hooks
    //OnServerRestartInterrupt Hook
    //OnServerRestart Hook
    [HarmonyPatch(typeof(ServerMgr), "RestartServer", typeof(string), typeof(int))]
    internal class ServerMgr_RestartServer
    {
        [HarmonyPrefix]
        static bool Prefix(string strNotice, int iSeconds, ServerMgr __instance)
        {
            if (SingletonComponent<ServerMgr>.Instance == null)
            {
                return false;
            }
            if (SingletonComponent<ServerMgr>.Instance.restartCoroutine != null)
            {
                if (Interface.CallHook("OnServerRestartInterrupt") != null)
                {
                    return false;
                }
                ConsoleNetwork.BroadcastToAllClients("chat.add", new object[]
                {
                2,
                0,
                "<color=#fff>SERVER</color> Restart interrupted!"
                });
                SingletonComponent<ServerMgr>.Instance.StopCoroutine(SingletonComponent<ServerMgr>.Instance.restartCoroutine);
                SingletonComponent<ServerMgr>.Instance.restartCoroutine = null;
            }
            if (Interface.CallHook("OnServerRestart", strNotice, iSeconds) != null)
            {
                return false;
            }
            SingletonComponent<ServerMgr>.Instance.restartCoroutine = SingletonComponent<ServerMgr>.Instance.ServerRestartWarning(strNotice, iSeconds);
            SingletonComponent<ServerMgr>.Instance.StartCoroutine(SingletonComponent<ServerMgr>.Instance.restartCoroutine);
            SingletonComponent<ServerMgr>.Instance.UpdateServerInformation();
            return false;
        }
    }

    //OnPlayerDisconnected Hook
    [HarmonyPatch(typeof(ServerMgr), "OnDisconnected", typeof(string), typeof(Network.Connection))]
    internal class ServerMgr_OnDisconnected
    {
        [HarmonyPrefix]
        static bool Prefix(string strReason, Network.Connection connection, ServerMgr __instance)
        {
            Facepunch.Rust.Analytics.Azure.OnPlayerDisconnected(connection, strReason);
            __instance.connectionQueue.RemoveConnection(connection);
            ConnectionAuth.OnDisconnect(connection);
            PlatformService.Instance.EndPlayerSession(connection.userid);
            EACServer.OnLeaveGame(connection);
            BasePlayer basePlayer = connection.player as BasePlayer;
            if (basePlayer)
            {
                Interface.CallHook("OnPlayerDisconnected", basePlayer, strReason);
                basePlayer.OnDisconnected();
            }
            return false;
        }
    }

    //IOnPlayerBanned Hook
    [HarmonyPatch(typeof(ServerMgr), "OnValidateAuthTicketResponse", typeof(ulong),typeof(ulong),typeof(AuthResponse))]
    internal class ServerMgr_OnValidateAuthTicketResponse
    {
        [HarmonyPrefix]
        static bool Prefix(ulong SteamId, ulong OwnerId, AuthResponse Status, ServerMgr __instance)
        {
            if (Auth_Steam.ValidateConnecting(SteamId, OwnerId, Status))
            {
                return false;
            }
            Network.Connection connection = Network.Net.sv.connections.FirstOrDefault((Network.Connection x) => x.userid == SteamId);
            if (connection == null)
            {
                Debug.LogWarning(string.Format("Steam gave us a {0} ticket response for unconnected id {1}", Status, SteamId));
                return false;
            }
            if (Status == (AuthResponse)2)
            {
                Debug.LogWarning(string.Format("Steam gave us a 'ok' ticket response for already connected id {0}", SteamId));
                return false;
            }
            if (Status == (AuthResponse)1)
            {
                return false;
            }
            if ((Status == (AuthResponse)4 || Status == (AuthResponse)3) && !__instance.bannedPlayerNotices.Contains(SteamId))
            {
                Interface.CallHook("IOnPlayerBanned", connection, Status);
                global::ConsoleNetwork.BroadcastToAllClients("chat.add", new object[]
                {
                2,
                0,
                "<color=#fff>SERVER</color> Kicking " + StringEx.EscapeRichText(connection.username) + " (banned by anticheat)"
                });
                __instance.bannedPlayerNotices.Add(SteamId);
            }
            Debug.Log(string.Format("Kicking {0}/{1}/{2} (Steam Status \"{3}\")", new object[]
            {
            connection.ipaddress,
            connection.userid,
            connection.username,
            Status.ToString()
            }));
            connection.authStatus = Status.ToString();
            Network.Net.sv.Kick(connection, "Steam: " + Status.ToString(), false);
            return false;
        }
    }

    //OnClientAuth Hook
    [HarmonyPatch(typeof(ServerMgr), "OnGiveUserInformation", typeof(Message))]
    internal class ServerMgr_OnGiveUserInformation
    {
        [HarmonyPrefix]
        static bool Prefix(Message packet, ServerMgr __instance)
        {
            if (packet.connection.state != Network.Connection.State.Unconnected)
            {
                Network.Net.sv.Kick(packet.connection, "Invalid connection state", false);
                return false;
            }
            packet.connection.state = Network.Connection.State.Connecting;
            if (packet.read.UInt8() != 228)
            {
                Network.Net.sv.Kick(packet.connection, "Invalid Connection Protocol", false);
                return false;
            }
            packet.connection.userid = packet.read.UInt64();
            packet.connection.protocol = packet.read.UInt32();
            packet.connection.os = packet.read.String(128);
            packet.connection.username = packet.read.String(256);
            if (string.IsNullOrEmpty(packet.connection.os))
            {
                throw new Exception("Invalid OS");
            }
            if (string.IsNullOrEmpty(packet.connection.username))
            {
                Network.Net.sv.Kick(packet.connection, "Invalid Username", false);
                return false;
            }
            packet.connection.username = packet.connection.username.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
            if (string.IsNullOrEmpty(packet.connection.username))
            {
                Network.Net.sv.Kick(packet.connection, "Invalid Username", false);
                return false;
            }
            string text = string.Empty;
            string branch = ConVar.Server.branch;
            if (packet.read.Unread >= 4)
            {
                text = packet.read.String(128);
            }
            Interface.CallHook("OnClientAuth", packet.connection);
            if (branch != string.Empty && branch != text)
            {
                DebugEx.Log(string.Concat(new object[]
                {
                "Kicking ",
                packet.connection,
                " - their branch is '",
                text,
                "' not '",
                branch,
                "'"
                }), 0);
                Network.Net.sv.Kick(packet.connection, "Wrong Steam Beta: Requires '" + branch + "' branch!", false);
                return false;
            }
            if (packet.connection.protocol > 2385u)
            {
                DebugEx.Log(string.Concat(new object[]
                {
                "Kicking ",
                packet.connection,
                " - their protocol is ",
                packet.connection.protocol,
                " not ",
                2385
                }), 0);
                Network.Net.sv.Kick(packet.connection, "Wrong Connection Protocol: Server update required!", false);
                return false;
            }
            if (packet.connection.protocol < 2385u)
            {
                DebugEx.Log(string.Concat(new object[]
                {
                "Kicking ",
                packet.connection,
                " - their protocol is ",
                packet.connection.protocol,
                " not ",
                2385
                }), 0);
                Network.Net.sv.Kick(packet.connection, "Wrong Connection Protocol: Client update required!", false);
                return false;
            }
            packet.connection.token = packet.read.BytesWithSize(512u);
            if (packet.connection.token == null || packet.connection.token.Length < 1)
            {
                Network.Net.sv.Kick(packet.connection, "Invalid Token", false);
                return false;
            }
            __instance.auth.OnNewConnection(packet.connection);
            return false;
        }
    }

    //OnPlayerSetInfo Hook
    [HarmonyPatch(typeof(ServerMgr), "ClientReady", typeof(Message))]
    internal class ServerMgr_ClientReady
    {
        [HarmonyPrefix]
        static bool Prefix(Message packet,ServerMgr __instance)
        {
            if (packet.connection.state != Network.Connection.State.Welcoming)
            {
                Network.Net.sv.Kick(packet.connection, "Invalid connection state", false);
                return false;
            }
            using (ClientReady clientReady = ProtoBuf.ClientReady.Deserialize(packet.read))
            {
                foreach (ClientReady.ClientInfo clientInfo in clientReady.clientInfo)
                {
                    Interface.CallHook("OnPlayerSetInfo", packet.connection, clientInfo.name, clientInfo.value);
                    packet.connection.info.Set(clientInfo.name, clientInfo.value);
                }
                __instance.connectionQueue.JoinedGame(packet.connection);
                Facepunch.Rust.Analytics.Azure.OnPlayerConnected(packet.connection);
                using (TimeWarning.New("ClientReady", 0))
                {
                    global::BasePlayer basePlayer;
                    using (TimeWarning.New("SpawnPlayerSleeping", 0))
                    {
                        basePlayer = __instance.SpawnPlayerSleeping(packet.connection);
                    }
                    if (basePlayer == null)
                    {
                        using (TimeWarning.New("SpawnNewPlayer", 0))
                        {
                            basePlayer = __instance.SpawnNewPlayer(packet.connection);
                        }
                    }
                    if (basePlayer != null)
                    {
                        CompanionServer.Util.SendSignedInNotification(basePlayer);
                    }
                }
            }
            ServerMgr.SendReplicatedVars(packet.connection);
            return false;
        }
    }

    //OnPlayerSpawn Hook
    [HarmonyPatch(typeof(ServerMgr), "SpawnNewPlayer", typeof(Network.Connection))]
    internal class ServerMgr_SpawnNewPlayer
    {
        [HarmonyPrefix]
        static bool Prefix(Network.Connection connection, ref BasePlayer __result)
        {
            BasePlayer.SpawnPoint spawnPoint = ServerMgr.FindSpawnPoint(null);
            BasePlayer basePlayer = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", spawnPoint.pos, spawnPoint.rot, true).ToPlayer();
            if (Interface.CallHook("OnPlayerSpawn", basePlayer, connection) != null)
            {
                __result = null;
                return false;
            }
            basePlayer.health = 0f;
            basePlayer.lifestate = BaseCombatEntity.LifeState.Dead;
            basePlayer.ResetLifeStateOnSpawn = false;
            basePlayer.limitNetworking = true;
            basePlayer.Spawn();
            basePlayer.limitNetworking = false;
            basePlayer.PlayerInit(connection);
            if (BaseGameMode.GetActiveGameMode(true))
            {
                BaseGameMode.GetActiveGameMode(true).OnNewPlayer(basePlayer);
            }
            else if (UnityEngine.Application.isEditor || (SleepingBag.FindForPlayer(basePlayer.userID, true).Length == 0 && !basePlayer.hasPreviousLife))
            {
                basePlayer.Respawn();
            }
            else
            {
                basePlayer.SendRespawnOptions();
            }
            DebugEx.Log(string.Format("{0} with steamid {1} joined from ip {2}", basePlayer.displayName, basePlayer.userID, basePlayer.net.connection.ipaddress), 0);
            DebugEx.Log(string.Format("\tNetworkId {0} is {1} ({2})", basePlayer.userID, basePlayer.net.ID, basePlayer.displayName), 0);
            if (basePlayer.net.connection.ownerid != basePlayer.net.connection.userid)
            {
                DebugEx.Log(string.Format("\t{0} is sharing the account {1}", basePlayer, basePlayer.net.connection.ownerid), 0);
            }
            __result = basePlayer;
            return false;
        }
    }

    //OnClientDisconnect Hook
    [HarmonyPatch(typeof(ServerMgr), "ReadDisconnectReason",typeof(Message))]
    internal class ServerMgr_ReadDisconnectReason
    {
        [HarmonyPrefix]
        static bool Prefix(Message packet)
        {
            string text = packet.read.String(4096);
            string text2 = packet.connection.ToString();
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(text2))
            {
                Interface.CallHook("OnClientDisconnect", packet.connection, text);
                DebugEx.Log(text2 + " disconnecting: " + text, 0);
            }
            return false;
        }
    }

    //OnServerInformationUpdated Hook
    [HarmonyPatch(typeof(ServerMgr), "UpdateServerInformation")]
    internal class ServerMgr_UpdateServerInformation
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            Interface.CallHook("OnServerInformationUpdated");
        }
    }

    //OnTick Hook
    [HarmonyPatch(typeof(ServerMgr), "DoTick")]
    internal class ServerMgr_DoTick
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            Interface.CallHook("OnTick");
        }
    }

    //OnFindSpawnPoint Hook
    [HarmonyPatch(typeof(ServerMgr), "FindSpawnPoint",typeof(BasePlayer))]
    internal class ServerMgr_FindSpawnPoint
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer forPlayer, ref BasePlayer.SpawnPoint __result)
        {
            object obj = Interface.CallHook("OnFindSpawnPoint", forPlayer);
            if (obj is BasePlayer.SpawnPoint)
            {
                __result = (BasePlayer.SpawnPoint)obj;
                return false;
            }

            return true;
        }
    }

    //IOnServerShutdown Hook
    [HarmonyPatch(typeof(ServerMgr), "Shutdown")]
    internal class ServerMgr_Shutdown
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            Interface.CallHook("IOnServerShutdown");
        }
    }

    //OnServerInitialize Hook
    [HarmonyPatch(typeof(ServerMgr), "Initialize",typeof(bool),typeof(string),typeof(bool),typeof(bool))]
    internal class ServerMgr_Initialize
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            Interface.CallHook("OnServerInitialize");
        }
    }

    //IOnServerInitialized Hook
    [HarmonyPatch(typeof(ServerMgr), "OpenConnection")]
    internal class ServerMgr_OpenConnection
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            Interface.CallHook("IOnServerInitialized");
        }
    }
    #endregion

    #region BaseNetworkable Hooks
    //OnNetworkGroupLeft Hook
    [HarmonyPatch(typeof(BaseNetworkable), "OnNetworkGroupLeave", typeof(Group))]
    internal class BaseNetworkable_OnNetworkGroupLeave
    {
        [HarmonyPrefix]
        static void Prefix(Group group, BaseNetworkable __instance)
        {
            Interface.CallHook("OnNetworkGroupLeft", __instance, group);
        }
    }

    //OnNetworkGroupEntered Hook
    [HarmonyPatch(typeof(BaseNetworkable), "OnNetworkGroupEnter", typeof(Group))]
    internal class BaseNetworkable_OnNetworkGroupEnter
    {
        [HarmonyPrefix]
        static void Prefix(Group group, BaseNetworkable __instance)
        {
            Interface.CallHook("OnNetworkGroupEntered", __instance, group);
        }
    }

    //IOnEntitySaved Hook
    [HarmonyPatch(typeof(BaseNetworkable), "ToStream", typeof(Stream), typeof(BaseNetworkable.SaveInfo))]
    internal class BaseNetworkable_ToStream
    {
        [HarmonyPrefix]
        static bool Prefix(Stream stream, BaseNetworkable.SaveInfo saveInfo, BaseNetworkable __instance)
        {
            using (saveInfo.msg = Facepunch.Pool.Get<ProtoBuf.Entity>())
            {
                __instance.Save(saveInfo);
                if (saveInfo.msg.baseEntity == null)
                {
                    Debug.LogError(__instance + ": ToStream - no BaseEntity!?");
                }
                if (saveInfo.msg.baseNetworkable == null)
                {
                    Debug.LogError(__instance + ": ToStream - no baseNetworkable!?");
                }
                Interface.CallHook("IOnEntitySaved", __instance, saveInfo);
                saveInfo.msg.ToProto(stream);
                __instance.PostSave(saveInfo);
            }
            return false;
        }
    }

    //OnEntitySnapshot Hook
    [HarmonyPatch(typeof(BaseNetworkable), "SendAsSnapshot", typeof(Connection),typeof(bool))]
    internal class BaseNetworkable_SendAsSnapshot
    {
        [HarmonyPrefix]
        static bool Prefix(Connection connection, bool justCreated, BaseNetworkable __instance)
        {
            if (Interface.CallHook("OnEntitySnapshot", __instance, connection) != null)
            {
                return false;
            }
            return true;
        }
    }

    //CanNetworkTo Hook
    [HarmonyPatch(typeof(BaseNetworkable), "ShouldNetworkTo", typeof(BasePlayer))]
    internal class BaseNetworkable_ShouldNetworkTo
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer player, BaseNetworkable __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanNetworkTo", __instance, player);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }

    //OnEntityKill Hook
    [HarmonyPatch(typeof(BaseNetworkable), "Kill",typeof(BaseNetworkable.DestroyMode))]
    internal class BaseNetworkable_Kill
    {
        [HarmonyPrefix]
        static bool Prefix(BaseNetworkable.DestroyMode mode, BaseNetworkable __instance)
        {
            if (__instance.IsDestroyed || Interface.CallHook("OnEntityKill", __instance) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnEntitySpawned Hook
    [HarmonyPatch(typeof(BaseNetworkable), "Spawn")]
    internal class BaseNetworkable_Spawn
    {
        [HarmonyPrefix]
        static bool Prefix(BaseNetworkable __instance)
        {
            __instance.SpawnShared();
            if (__instance.net == null)
            {
                __instance.net = Network.Net.sv.CreateNetworkable();
            }
            __instance.creationFrame = UnityEngine.Time.frameCount;
            __instance.PreInitShared();
            __instance.InitShared();
            __instance.ServerInit();
            __instance.PostInitShared();
            __instance.UpdateNetworkGroup();
            __instance.isSpawned = true;
            Interface.CallHook("OnEntitySpawned", __instance);
            __instance.SendNetworkUpdateImmediate(true);
            if (Rust.Application.isLoading && !Rust.Application.isLoadingSave)
            {
                __instance.gameObject.SendOnSendNetworkUpdate(__instance as BaseEntity);
            }
            return false;
        }
    }
    #endregion

    #region ConsoleSystem Hooks
    //IOnRunCommandLine Hook
    [HarmonyPatch(typeof(ConsoleSystem), "UpdateValuesFromCommandLine")]
    internal class ConsoleSystem_UpdateValuesFromCommandLine
    {
        [HarmonyPrefix]
        static bool Prefix(ConsoleSystem __instance)
        {
            if (Interface.CallHook("IOnRunCommandLine") != null)
            {
                return false;
            }
            return true;
        }
    }

    //IOnServerCommand Hook
    [HarmonyPatch(typeof(ConsoleSystem), "Internal", typeof(ConsoleSystem.Arg))]
    internal class ConsoleSystem_Internal
    {
        [HarmonyPrefix]
        static bool Prefix(ConsoleSystem.Arg arg, ConsoleSystem __instance, ref bool __result)
        {
            if (arg.Invalid)
            {
                __result = false;
                return false;
            }
            object obj = Interface.CallHook("IOnServerCommand", arg);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }
    #endregion

    #region RCon Hooks
    //IOnRconInitialize Hook
    [HarmonyPatch(typeof(RCon), "Initialize")]
    internal class RCon_Initialize
    {
        [HarmonyPrefix]
        static bool Prefix(RCon.RConListener __instance)
        {
            if (Interface.CallHook("IOnRconInitialize") != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnRconConnection Hook
    [HarmonyPatch(typeof(RCon.RConListener), "ProcessConnections")]
    internal class RCon_ProcessConnections
    {
        [HarmonyPrefix]
        static bool Prefix(RCon.RConListener __instance)
        {
            if (!__instance.server.Pending())
            {
                return false;
            }
            Socket socket = __instance.server.AcceptSocket();
            if (socket == null)
            {
                return false;
            }
            IPEndPoint ipendPoint = socket.RemoteEndPoint as IPEndPoint;
            if (Interface.CallHook("OnRconConnection", ipendPoint.Address) != null)
            {
                socket.Close();
                return false;
            }
            if (RCon.IsBanned(ipendPoint.Address))
            {
                Debug.Log("[RCON] Ignoring connection - banned. " + ipendPoint.Address.ToString());
                socket.Close();
                return false;
            }
            __instance.clients.Add(new RCon.RConClient(socket));
            return false;
        }
    }
    #endregion

    #region ResourceEntity Hooks
    //OnEntityTakeDamage Hook
    [HarmonyPatch(typeof(ResourceEntity), "OnAttacked", typeof(HitInfo))]
    internal class ResourceEntity_OnAttacked
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, ResourceEntity __instance)
        {
            if (__instance.isServer && !__instance.isKilled)
            {
                if (Interface.CallHook("OnEntityTakeDamage", __instance, info) != null)
                {
                    return false;
                }
            }
            return true;
        }
    }

    //OnEntityDeath Hook
    [HarmonyPatch(typeof(ResourceEntity), "OnKilled", typeof(HitInfo))]
    internal class ResourceEntity_OnKilled
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, ResourceEntity __instance)
        {
            __instance.isKilled = true;
            Interface.CallHook("OnEntityDeath", __instance, info);
            __instance.Kill(BaseNetworkable.DestroyMode.None);
            return false;
        }
    }
    #endregion

    #region BaseCombatEntity Hooks
    //OnEntityDeath Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "Die", typeof(HitInfo))]
    internal class BaseCombatEntity_Die
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, BaseCombatEntity __instance)
        {
            if (__instance.IsDead())
            {
                return false;
            }
            if (ConVar.Global.developer > 1)
            {
                Debug.Log("[Combat]".PadRight(10) + __instance.gameObject.name + " died");
            }
            __instance.health = 0f;
            __instance.lifestate = BaseCombatEntity.LifeState.Dead;
            Interface.CallHook("OnEntityDeath", __instance, info);
            if (info != null && info.InitiatorPlayer)
            {
                BasePlayer initiatorPlayer = info.InitiatorPlayer;
                if (initiatorPlayer != null && initiatorPlayer.GetActiveMission() != -1 && !initiatorPlayer.IsNpc)
                {
                    initiatorPlayer.ProcessMissionEvent(BaseMission.MissionEventType.KILL_ENTITY, __instance.prefabID.ToString(), 1f);
                }
            }
            using (TimeWarning.New("OnKilled", 0))
            {
                __instance.OnKilled(info);
            }
            return false;
        }
    }

    //OnEntityMarkHostile Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "MarkHostileFor", typeof(float))]
    internal class BaseCombatEntity_MarkHostileFor
    {
        [HarmonyPrefix]
        static bool Prefix(float duration, BaseCombatEntity __instance)
        {
            if (Interface.CallHook("OnEntityMarkHostile", __instance, duration) != null)
            {
                return false;
            }
            return true;
        }
    }

    //CanEntityBeHostile Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "IsHostile")]
    internal class BaseCombatEntity_IsHostile
    {
        [HarmonyPrefix]
        static bool Prefix(BaseCombatEntity __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanEntityBeHostile", __instance);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }

    //IOnBaseCombatEntityHurt Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "Hurt", typeof(HitInfo))]
    internal class BaseCombatEntity_Hurt
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, BaseCombatEntity __instance)
        {
            Assert.IsTrue(__instance.isServer, "This should be called serverside only");
            if (__instance.IsDead())
            {
                return false;
            }
            using (TimeWarning.New("Hurt( HitInfo )", 50))
            {
                float health = __instance.health;
                __instance.ScaleDamage(info);
                if (info.PointStart != Vector3.zero)
                {
                    for (int i = 0; i < __instance.propDirection.Length; i++)
                    {
                        if (!(__instance.propDirection[i].extraProtection == null) && !__instance.propDirection[i].IsWeakspot(__instance.transform, info))
                        {
                            __instance.propDirection[i].extraProtection.Scale(info.damageTypes, 1f);
                        }
                    }
                }
                info.damageTypes.Scale(Rust.DamageType.Arrow, ConVar.Server.arrowdamage);
                info.damageTypes.Scale(Rust.DamageType.Bullet, ConVar.Server.bulletdamage);
                info.damageTypes.Scale(Rust.DamageType.Slash, ConVar.Server.meleedamage);
                info.damageTypes.Scale(Rust.DamageType.Blunt, ConVar.Server.meleedamage);
                info.damageTypes.Scale(Rust.DamageType.Stab, ConVar.Server.meleedamage);
                info.damageTypes.Scale(Rust.DamageType.Bleeding, ConVar.Server.bleedingdamage);
                if (!(__instance is BasePlayer))
                {
                    info.damageTypes.Scale(Rust.DamageType.Fun_Water, 0f);
                }
                if (Interface.CallHook("IOnBaseCombatEntityHurt", __instance, info) != null)
                {
                    return false;
                }
                __instance.DebugHurt(info);
                __instance.health = health - info.damageTypes.Total();
                __instance.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                if (ConVar.Global.developer > 1)
                {
                    Debug.Log(string.Concat(new object[]
                    {
                    "[Combat]".PadRight(10),
                    __instance.gameObject.name,
                    " hurt ",
                    info.damageTypes.GetMajorityDamageType(),
                    "/",
                    info.damageTypes.Total(),
                    " - ",
                    __instance.health.ToString("0"),
                    " health left"
                    }));
                }
                __instance.lastDamage = info.damageTypes.GetMajorityDamageType();
                __instance.lastAttacker = info.Initiator;
                if (__instance.lastAttacker != null)
                {
                    BaseCombatEntity baseCombatEntity = __instance.lastAttacker as BaseCombatEntity;
                    if (baseCombatEntity != null)
                    {
                        baseCombatEntity.lastDealtDamageTime = UnityEngine.Time.time;
                        baseCombatEntity.lastDealtDamageTo = __instance;
                    }
                }
                BaseCombatEntity baseCombatEntity2 = __instance.lastAttacker as BaseCombatEntity;
                if (__instance.markAttackerHostile && baseCombatEntity2 != null && baseCombatEntity2 != __instance)
                {
                    baseCombatEntity2.MarkHostileFor(60f);
                }
                if (DamageTypeEx.IsConsideredAnAttack(__instance.lastDamage))
                {
                    __instance.lastAttackedTime = UnityEngine.Time.time;
                    if (__instance.lastAttacker != null)
                    {
                        __instance.LastAttackedDir = (__instance.lastAttacker.transform.position - __instance.transform.position).normalized;
                    }
                }
                bool flag = __instance.Health() <= 0f;
                Facepunch.Rust.Analytics.Azure.OnEntityTakeDamage(info, flag);
                if (flag)
                {
                    __instance.Die(info);
                }
                BasePlayer initiatorPlayer = info.InitiatorPlayer;
                if (initiatorPlayer)
                {
                    if (__instance.IsDead())
                    {
                        initiatorPlayer.stats.combat.LogAttack(info, "killed", health);
                    }
                    else
                    {
                        initiatorPlayer.stats.combat.LogAttack(info, "", health);
                    }
                }
            }
            return false;
        }
    }

    //OnStructureRepair Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "DoRepair", typeof(BasePlayer))]
    internal class BaseCombatEntity_DoRepair
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer player, BaseCombatEntity __instance)
        {
            if (!__instance.repair.enabled || Interface.CallHook("OnStructureRepair", __instance, player) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnEntityPickedUp Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "OnPickedUp", typeof(Item),typeof(BasePlayer))]
    internal class BaseCombatEntity_OnPickedUp
    {
        [HarmonyPostfix]
        static void Postfix(Item createdItem, BasePlayer player, BaseCombatEntity __instance)
        {
            Interface.CallHook("OnEntityPickedUp", __instance, createdItem, player);
        }
    }

    //CanPickupEntity Hook
    [HarmonyPatch(typeof(BaseCombatEntity), "CanPickup", typeof(BasePlayer))]
    internal class BaseCombatEntity_CanPickup
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer player, BaseCombatEntity __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanPickupEntity", player, __instance);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }
    #endregion

    #region BaseEntity Hooks
    //OnEntityActiveCheck Hook
    [HarmonyPatch(typeof(BaseEntity.RPC_Server.IsActiveItem), "Test", typeof(uint), typeof(string), typeof(BaseEntity), typeof(BasePlayer))]
    internal class BaseEntity_TestIsActiveItem
    {
        [HarmonyPrefix]
        static bool Prefix(uint id, string debugName, BaseEntity ent, BasePlayer player, BaseEntity __instance, ref bool __result)
        {
            if (ent == null || player == null || ent.net == null || player.net == null || ent.parentEntity.uid != player.net.ID)
            {
                __result = false;
                return false;
            }
            object obj = Interface.CallHook("OnEntityActiveCheck", ent, player, id, debugName);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            if (ent.net.ID == player.net.ID)
            {
                __result = true;
                return false;
            }
            Item activeItem = player.GetActiveItem();
            __result = activeItem != null && !(activeItem.GetHeldEntity() != ent);
            return false;
        }
    }

    //OnEntityFromOwnerCheck Hook
    [HarmonyPatch(typeof(BaseEntity.RPC_Server.FromOwner), "Test", typeof(uint), typeof(string), typeof(BaseEntity), typeof(BasePlayer))]
    internal class BaseEntity_TestFromOwner
    {
        [HarmonyPrefix]
        static bool Prefix(uint id, string debugName, BaseEntity ent, BasePlayer player, BaseEntity __instance, ref bool __result)
        {
            if (ent == null || player == null || ent.net == null || player.net == null)
            {
                __result = false;
                return false;
            }
            object obj = Interface.CallHook("OnEntityFromOwnerCheck", ent, player, id, debugName);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            __result = ent.net.ID == player.net.ID || !(ent.parentEntity.uid != player.net.ID); 
            return false;
        }
    }

    //OnEntityVisibilityCheck Hook
    [HarmonyPatch(typeof(BaseEntity.RPC_Server.IsVisible), "Test", typeof(uint), typeof(string), typeof(BaseEntity), typeof(BasePlayer), typeof(float))]
    internal class BaseEntity_TestIsVisibl
    {
        [HarmonyPrefix]
        static bool Prefix(uint id, string debugName, BaseEntity ent, BasePlayer player, float maximumDistance, BaseEntity __instance, ref bool __result)
        {
            if (ent == null || player == null)
            {
                __result = false;
                return false;
            }
            object obj = Interface.CallHook("OnEntityVisibilityCheck", ent, player, id, debugName, maximumDistance);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            __result = GamePhysics.LineOfSight(player.eyes.center, player.eyes.position, 2162688, null) && (ent.IsVisible(player.eyes.HeadRay(), 1218519041, maximumDistance) || ent.IsVisible(player.eyes.position, maximumDistance));
            return false;
        }
    }

    //OnEntityDistanceCheck Hook
    [HarmonyPatch(typeof(BaseEntity.RPC_Server.MaxDistance), "Test", typeof(uint), typeof(string), typeof(BaseEntity),typeof(BasePlayer),typeof(float))]
    internal class BaseEntity_TestMaxDistance
    {
        [HarmonyPrefix]
        static bool Prefix(uint id, string debugName, BaseEntity ent, BasePlayer player, float maximumDistance, BaseEntity __instance, ref bool __result)
        {
            if (ent == null || player == null)
            {
                __result = false;
                return false;
            }
            object obj = Interface.CallHook("OnEntityDistanceCheck", ent, player, id, debugName, maximumDistance);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            __result = ent.Distance(player.eyes.position) <= maximumDistance;
            return false;
        }
    }

    //OnSignalBroadcast Hook
    [HarmonyPatch(typeof(BaseEntity), "SignalBroadcast", typeof(BaseEntity.Signal), typeof(Connection))]
    internal class BaseEntity_SignalBroadcast2
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.Signal signal, Connection sourceConnection, BaseEntity __instance)
        {
            if (__instance.net == null || __instance.net.group == null || __instance.limitNetworking || Interface.CallHook("OnSignalBroadcast", __instance) != null)
            {
                return false;
            }
            __instance.ClientRPCEx<int>(new SendInfo(__instance.net.group.subscribers)
            {
                method = SendMethod.Unreliable,
                priority = Network.Priority.Immediate
            }, sourceConnection, "SignalFromServer", (int)signal);
            return false;
        }
    }

    //OnSignalBroadcast Hook
    [HarmonyPatch(typeof(BaseEntity), "SignalBroadcast", typeof(BaseEntity.Signal),typeof(string),typeof(Connection))]
    internal class BaseEntity_SignalBroadcast
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.Signal signal, string arg, Connection sourceConnection, BaseEntity __instance)
        {
            if (__instance.net == null || __instance.net.group == null || __instance.limitNetworking || Interface.CallHook("OnSignalBroadcast", __instance) != null)
            {
                return false;
            }
            __instance.ClientRPCEx<int, string>(new SendInfo(__instance.net.group.subscribers)
            {
                method = SendMethod.Unreliable,
                priority = Network.Priority.Immediate
            }, sourceConnection, "SignalFromServerEx", (int)signal, arg);
            return false;
        }
    }

    //CanNetworkTo Hook
    [HarmonyPatch(typeof(BaseEntity), "ShouldNetworkTo", typeof(BasePlayer))]
    internal class BaseEntity_ShouldNetworkTo
    {
        [HarmonyPostfix]
        static void Postfix(BasePlayer player, BaseEntity __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanNetworkTo", __instance, player);
            if (obj is bool)
            {
                __result = (bool)obj;
            }
        }
    }

    //OnBuildingPrivilege Hook
    [HarmonyPatch(typeof(BaseEntity), "GetBuildingPrivilege", typeof(OBB))]
    internal class BaseEntity_GetBuildingPrivilege
    {
        [HarmonyPrefix]
        static bool Prefix(OBB obb, BaseEntity __instance, ref BuildingPrivlidge __result)
        {
            object obj = Interface.CallHook("OnBuildingPrivilege", __instance, obb);
            if (obj is BuildingPrivlidge)
            {
                __result = (BuildingPrivlidge)obj;
                return false;
            }
            return true;
        }
    }

    //OnEntityFlagsNetworkUpdate Hook
    [HarmonyPatch(typeof(BaseEntity), "SendNetworkUpdate_Flags")]
    internal class BaseEntity_SendNetworkUpdate_Flags
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity __instance)
        {
            if (Rust.Application.isLoading || Rust.Application.isLoadingSave || __instance.IsDestroyed || __instance.net == null || !__instance.isSpawned || Interface.CallHook("OnEntityFlagsNetworkUpdate", __instance) != null)
            {
                return false;
            }
            using (TimeWarning.New("SendNetworkUpdate_Flags", 0))
            {
                __instance.LogEntry(BaseMonoBehaviour.LogEntryType.Network, 2, "SendNetworkUpdate_Flags");
                List<Connection> subscribers = __instance.GetSubscribers();
                if (subscribers != null && subscribers.Count > 0)
                {
                    NetWrite netWrite = Network.Net.sv.StartWrite();
                    netWrite.PacketID(Message.Type.EntityFlags);
                    netWrite.EntityID(__instance.net.ID);
                    netWrite.Int32((int)__instance.flags);
                    SendInfo info = new SendInfo(subscribers);
                    netWrite.Send(info);
                }
                __instance.gameObject.SendOnSendNetworkUpdate(__instance);
            }
            return false;
        }
    }
    #endregion

    #region BasePlayer Hooks
    //Needs Transpiler

    //OnTeamUpdated
    //OnPlayerAttack
    //OnProjectileRicochet
    //OnWorldProjectileCreate
    //IOnPlayerConnected
    //OnRespawnInformationGiven
    //OnPlayerCorpseSpawned
    //OnPlayerRespawn
    //IOnBasePlayerHurt


    //OnPlayerTick Hook
    //OnPlayerInput Hook
    [HarmonyPatch(typeof(BasePlayer), "OnReceiveTick",typeof(PlayerTick),typeof(bool))]
    internal class BasePlayer_OnReceiveTick
    {
        [HarmonyPrefix]
        static bool Prefix(PlayerTick msg, bool wasPlayerStalled, BasePlayer __instance)
        {
            if (msg.inputState != null)
            {
                __instance.serverInput.Flip(msg.inputState);
            }
            if (Interface.CallHook("OnPlayerTick", __instance, msg, wasPlayerStalled) != null)
            {
                return false;
            }
            if (__instance.serverInput.current.buttons != __instance.serverInput.previous.buttons)
            {
                __instance.ResetInputIdleTime();
            }
            if (Interface.CallHook("OnPlayerInput", __instance, __instance.serverInput) != null || __instance.IsReceivingSnapshot)
            {
                return false; 
            }
            if (__instance.IsSpectating())
            {
                using (TimeWarning.New("Tick_Spectator", 0))
                {
                    __instance.Tick_Spectator();
                }
                return false;
            }
            if (__instance.IsDead())
            {
                return false;
            }
            if (__instance.IsSleeping())
            {
                if (__instance.serverInput.WasJustPressed(BUTTON.FIRE_PRIMARY) || __instance.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY) || __instance.serverInput.WasJustPressed(BUTTON.JUMP) || __instance.serverInput.WasJustPressed(BUTTON.DUCK))
                {
                    __instance.EndSleeping();
                    __instance.SendNetworkUpdateImmediate(false);
                }
                __instance.UpdateActiveItem(default(ItemId));
                return false;
            }
            __instance.UpdateActiveItem(msg.activeItem);
            __instance.UpdateModelStateFromTick(msg);
            if (__instance.IsIncapacitated())
            {
                return false;
            }
            if (__instance.isMounted)
            {
                __instance.GetMounted().PlayerServerInput(__instance.serverInput, __instance);
            }
            __instance.UpdatePositionFromTick(msg, wasPlayerStalled);
            __instance.UpdateRotationFromTick(msg);
            return false;
        }
    }

    //OnThreatLevelUpdate Hook
    [HarmonyPatch(typeof(BasePlayer), "EnsureUpdated")]
    internal class BasePlayer_EnsureUpdated
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (UnityEngine.Time.realtimeSinceStartup - __instance.lastUpdateTime < 30f)
            {
                return false;
            }
            __instance.lastUpdateTime = UnityEngine.Time.realtimeSinceStartup;
            __instance.cachedThreatLevel = 0f;
            if (!__instance.IsSleeping())
            {
                if (Interface.CallHook("OnThreatLevelUpdate", __instance) != null)
                {
                    return false;
                }
                if (__instance.inventory.containerWear.itemList.Count > 2)
                {
                    __instance.cachedThreatLevel += 1f;
                }
                foreach (Item item in __instance.inventory.containerBelt.itemList)
                {
                    BaseEntity heldEntity = item.GetHeldEntity();
                    if (heldEntity && heldEntity is BaseProjectile && !(heldEntity is BowWeapon))
                    {
                        __instance.cachedThreatLevel += 2f;
                        break;
                    }
                }
            }
            return false;
        }
    }

    //OnPlayerSetInfo Hook
    [HarmonyPatch(typeof(BasePlayer), "SetInfo", typeof(string),typeof(string))]
    internal class BasePlayer_SetInfo
    {
        [HarmonyPrefix]
        static bool Prefix(string key, string val, BasePlayer __instance)
        {
            if (!__instance.IsConnected)
            {
                return false;
            }
            Interface.CallHook("OnPlayerSetInfo", __instance.net.connection, key, val);
            __instance.net.connection.info.Set(key, val);
            return false;
        }
    }

    //OnPlayerDeath Hook
    [HarmonyPatch(typeof(BasePlayer), "WoundInsteadOfDying", typeof(HitInfo))]
    internal class BasePlayer_WoundInsteadOfDying
    {
        [HarmonyPostfix]
        static void Postfix(HitInfo info, BasePlayer __instance, bool __result)
        {
           if(__result == false)
            {
                if (Interface.CallHook("OnPlayerDeath", __instance, info) != null)
                {
                    __result = true;
                }
            }
        }
    }

    //OnSendModelState Hook
    [HarmonyPatch(typeof(BasePlayer), "SendModelState", typeof(bool))]
    internal class BasePlayer_SendModelState
    {
        [HarmonyPrefix]
        static bool Prefix(bool force, BasePlayer __instance)
        {
            if (!force && (!__instance.wantsSendModelState || __instance.nextModelStateUpdate > UnityEngine.Time.time))
            {
                    return false;
            }
            __instance.wantsSendModelState = false;
            __instance.nextModelStateUpdate = UnityEngine.Time.time + 0.1f;
            if (__instance.IsDead() || __instance.IsSpectating())
            {
                return false;
            }
            __instance.modelState.sleeping = __instance.IsSleeping();
            __instance.modelState.mounted = __instance.isMounted;
            __instance.modelState.relaxed = __instance.IsRelaxed();
            __instance.modelState.onPhone = (__instance.HasActiveTelephone && !__instance.activeTelephone.IsMobile);
            __instance.modelState.crawling = __instance.IsCrawling();
            if (__instance.limitNetworking || Interface.CallHook("OnSendModelState", __instance) != null)
            {
                return false;
            }
            __instance.ClientRPC<ModelState>(null, "OnModelState", __instance.modelState);
            return false;
        }
    }

    //OnMapMarkerRemove Hook
    [HarmonyPatch(typeof(BasePlayer), "Server_RemovePointOfInterest", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_Server_RemovePointOfInterest
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            int num = msg.read.Int32();
            if (__instance.State.pointsOfInterest != null && __instance.State.pointsOfInterest.Count > num && num >= 0)
            {
                if (Interface.CallHook("OnMapMarkerRemove", __instance, __instance.State.pointsOfInterest, num) != null)
                {
                    return false;
                }
                __instance.State.pointsOfInterest[num].Dispose();
                __instance.State.pointsOfInterest.RemoveAt(num);
                __instance.DirtyPlayerState();
                __instance.SendMarkersToClient();
                __instance.TeamUpdate();
            }
            return false;
        }
    }

    //OnPlayerKeepAlive Hook
    [HarmonyPatch(typeof(BasePlayer), "RPC_KeepAlive", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_RPC_KeepAlive
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            if (!msg.player.CanInteract() || msg.player == __instance || !__instance.IsWounded() || Interface.CallHook("OnPlayerKeepAlive", __instance, msg.player) != null)
            {
                return false;
            }
            __instance.ProlongWounding(10f);
            return false;
        }
    }

    //OnPlayerAssist Hook
    [HarmonyPatch(typeof(BasePlayer), "RPC_Assist", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_RPC_Assist
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            if (!msg.player.CanInteract() || msg.player == __instance || !__instance.IsWounded() || Interface.CallHook("OnPlayerAssist", __instance, msg.player) != null)
            {
                return false;
            }
            __instance.StopWounded(msg.player);
            msg.player.stats.Add("wounded_assisted", 1, (Stats)5);
            __instance.stats.Add("wounded_healed", 1, Stats.Steam);
            return false;
        }
    }

    //OnLootPlayer Hook
    [HarmonyPatch(typeof(BasePlayer), "RPC_LootPlayer", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_RPC_LootPlayer
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            BasePlayer player = msg.player;
            if (!player || !player.CanInteract() || !__instance.CanBeLooted(player))
            {
                return false;
            }
            if (player.inventory.loot.StartLootingEntity(__instance, true))
            {
                player.inventory.loot.AddContainer(__instance.inventory.containerMain);
                player.inventory.loot.AddContainer(__instance.inventory.containerWear);
                player.inventory.loot.AddContainer(__instance.inventory.containerBelt);
                Interface.CallHook("OnLootPlayer", __instance, player);
                player.inventory.loot.SendImmediate();
                player.ClientRPCPlayer<string>(null, player, "RPC_OpenLootPanel", "player_corpse");
            }
            return false;
        }
    }

    //OnMessagePlayer Hook
    [HarmonyPatch(typeof(BasePlayer), "ChatMessage",typeof(string))]
    internal class BasePlayer_ChatMessage
    {
        [HarmonyPrefix]
        static bool Prefix(string msg,BasePlayer __instance)
        {
            if (!__instance.isServer || Interface.CallHook("OnMessagePlayer", msg, __instance) != null)
            {
                return false;
            }
            __instance.SendConsoleCommand("chat.add", new object[]
            {
            2,
            0,
            msg
            });
            return false;
        }
    }

    //OnPlayerColliderEnable Hook
    [HarmonyPatch(typeof(BasePlayer), "EnablePlayerCollider")]
    internal class BasePlayer_EnablePlayerCollider
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (__instance.playerCollider.enabled || Interface.CallHook("OnPlayerColliderEnable", __instance, __instance.playerCollider) != null)
            {
                return false;
            }
            __instance.RefreshColliderSize(true);
            __instance.playerCollider.enabled = true;
            return false;
        }
    }

    //IOnBasePlayerAttacked Hook
    [HarmonyPatch(typeof(BasePlayer), "OnAttacked",typeof(HitInfo))]
    internal class BasePlayer_OnAttacked
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info,BasePlayer __instance)
        {
            if (Interface.CallHook("IOnBasePlayerAttacked", __instance, info) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnPlayerRecover Hook
    //OnPlayerRecovered Hook
    [HarmonyPatch(typeof(BasePlayer), "RecoverFromWounded")]
    internal class BasePlayer_RecoverFromWounded
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (Interface.CallHook("OnPlayerRecover", __instance) != null)
            {
                return false;
            }
            if (__instance.IsCrawling())
            {
                __instance.health = UnityEngine.Random.Range(2f, 6f) + __instance.healingWhileCrawling;
            }
            __instance.healingWhileCrawling = 0f;
            __instance.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);
            __instance.SetPlayerFlag(BasePlayer.PlayerFlags.Incapacitated, false);
            if (BaseGameMode.GetActiveGameMode(__instance.isServer))
            {
                BaseGameMode.GetActiveGameMode(__instance.isServer).OnPlayerRevived(null, __instance);
            }
            Interface.CallHook("OnPlayerRecovered", __instance);
            return false;
        }
    }

    //OnPlayerWound Hook
    [HarmonyPatch(typeof(BasePlayer), "BecomeWounded", typeof(HitInfo))]
    internal class BasePlayer_BecomeWounded
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, BasePlayer __instance)
        {
            if (__instance.IsWounded() || Interface.CallHook("OnPlayerWound", __instance, info) != null)
            {
                return false;
            }
            return true;
        }
    }


    //CanBeWounded Hook
    [HarmonyPatch(typeof(BasePlayer), "EligibleForWounding", typeof(HitInfo))]
    internal class BasePlayer_EligibleForWounding
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, BasePlayer __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanBeWounded", __instance, info);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }

    //OnActiveItemChange Hook
    //OnActiveItemChanged Hook
    [HarmonyPatch(typeof(BasePlayer), "UpdateActiveItem", typeof(ItemId))]
    internal class BasePlayer_UpdateActiveItem
    {
        [HarmonyPrefix]
        static bool Prefix(ItemId itemID, BasePlayer __instance)
        {
            Assert.IsTrue(__instance.isServer, "Realm should be server!");
            if (__instance.svActiveItemID == itemID)
            {
                return false;
            }
            if (__instance.equippingBlocked)
            {
                itemID = default(ItemId);
            }
            Item item = __instance.inventory.containerBelt.FindItemByUID(itemID);
            if (__instance.IsItemHoldRestricted(item))
            {
                itemID = default(ItemId);
            }
            Item activeItem = __instance.GetActiveItem();
            if (Interface.CallHook("OnActiveItemChange", __instance, activeItem, itemID) != null)
            {
                return false;
            }
            __instance.svActiveItemID = default(ItemId);
            if (activeItem != null)
            {
                HeldEntity heldEntity = activeItem.GetHeldEntity() as global::HeldEntity;
                if (heldEntity != null)
                {
                    heldEntity.SetHeld(false);
                }
            }
            __instance.svActiveItemID = itemID;
            __instance.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            Item activeItem2 = __instance.GetActiveItem();
            if (activeItem2 != null)
            {
                HeldEntity heldEntity2 = activeItem2.GetHeldEntity() as HeldEntity;
                if (heldEntity2 != null)
                {
                    heldEntity2.SetHeld(true);
                }
                __instance.NotifyGesturesNewItemEquipped();
            }
            __instance.inventory.UpdatedVisibleHolsteredItems();
            Interface.CallHook("OnActiveItemChanged", __instance, activeItem, activeItem2);
            return false;
        }
    }

    //OnPlayerVoice Hook
    [HarmonyPatch(typeof(BasePlayer), "OnReceivedVoice", typeof(byte[]))]
    internal class BasePlayer_OnReceivedVoice
    {
        [HarmonyPrefix]
        static bool Prefix(byte[] data, BasePlayer __instance)
        {
            if (Interface.CallHook("OnPlayerVoice", __instance, data) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnEntityMarkHostile Hook
    [HarmonyPatch(typeof(BasePlayer), "MarkHostileFor",typeof(float))]
    internal class BasePlayer_MarkHostileFor
    {
        [HarmonyPrefix]
        static bool Prefix(float duration,BasePlayer __instance)
        {
            if (Interface.CallHook("OnEntityMarkHostile", __instance, duration) != null)
            {
                return false;
            }
            return true;
        }
    }

    //CanEntityBeHostile Hook
    [HarmonyPatch(typeof(BasePlayer), "IsHostile")]
    internal class BasePlayer_IsHostile
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanEntityBeHostile", __instance);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            __result = __instance.State.unHostileTimestamp > TimeEx.currentTimestamp;
            return false;
        }
    }

    //OnPlayerSpectateEnd Hook
    [HarmonyPatch(typeof(BasePlayer), "StopSpectating")]
    internal class BasePlayer_StopSpectating
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (!__instance.IsSpectating() || Interface.CallHook("OnPlayerSpectateEnd", __instance, __instance.spectateFilter) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnPlayerSpectate Hook
    [HarmonyPatch(typeof(BasePlayer), "StartSpectating")]
    internal class BasePlayer_StartSpectating
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (__instance.IsSpectating() || Interface.CallHook("OnPlayerSpectate", __instance, __instance.spectateFilter) != null)
            {
                return false;
            }
            return true;
        }
    }

    //CanSpectateTarget Hook
    [HarmonyPatch(typeof(BasePlayer), "UpdateSpectateTarget",typeof(string))]
    internal class BasePlayer_UpdateSpectateTarget
    {
        [HarmonyPrefix]
        static bool Prefix(string strName,BasePlayer __instance)
        {
            if (Interface.CallHook("CanSpectateTarget", __instance, strName) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnDemoRecordingStop Hook
    //OnDemoRecordingStopped Hook
    [HarmonyPatch(typeof(BasePlayer), "StopDemoRecording")]
    internal class BasePlayer_StopDemoRecording
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (__instance.net == null || __instance.net.connection == null)
            {
                return false;
            }
            if (!__instance.net.connection.IsRecording)
            {
                return false;
            }
            if (Interface.CallHook("OnDemoRecordingStop", __instance.net.connection.recordFilename, __instance) != null)
            {
                return false;
            }
            Debug.Log(__instance.ToString() + " recording stopped: " + __instance.net.connection.RecordFilename);
            __instance.net.connection.StopRecording();
            __instance.CancelInvoke(new Action(__instance.MonitorDemoRecording));
            Interface.CallHook("OnDemoRecordingStopped", __instance.net.connection.recordFilename, __instance);
            return false;
        }
    }

    //OnDemoRecordingStart Hook
    //OnDemoRecordingStarted Hook
    [HarmonyPatch(typeof(BasePlayer), "StartDemoRecording")]
    internal class BasePlayer_StartDemoRecording
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (__instance.net == null || __instance.net.connection == null)
            {
                return false;
            }
            if (__instance.net.connection.IsRecording)
            {
                return false;
            }
            string text = string.Format("demos/{0}/{1:yyyy-MM-dd-hhmmss}.dem", __instance.UserIDString, DateTime.Now);
            if (Interface.CallHook("OnDemoRecordingStart", text, __instance) != null)
            {
                return false;
            }
            Debug.Log(__instance.ToString() + " recording started: " + text);
            __instance.net.connection.StartRecording(text, new ConVar.Demo.Header
            {
                version = ConVar.Demo.Version,
                level = UnityEngine.Application.loadedLevelName,
                levelSeed = World.Seed,
                levelSize = World.Size,
                checksum = World.Checksum,
                localclient = __instance.userID,
                position = __instance.eyes.position,
                rotation = __instance.eyes.HeadForward(),
                levelUrl = World.Url,
                recordedTime = DateTime.Now.ToBinary()
            });
            __instance.SendNetworkUpdateImmediate(false);
            __instance.SendGlobalSnapshot();
            __instance.SendFullSnapshot();
            __instance.SendEntityUpdate();
            TreeManager.SendSnapshot(__instance);
            ServerMgr.SendReplicatedVars(__instance.net.connection);
            __instance.InvokeRepeating(new Action(__instance.MonitorDemoRecording), 10f, 10f);
            Interface.CallHook("OnDemoRecordingStarted", text, __instance);
            return false;
        }
    }

    //OnFeedbackReported Hook
    [HarmonyPatch(typeof(BasePlayer), "OnFeedbackReport", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_OnFeedbackReport
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            string text = msg.read.String(256);
            string text2 = msg.read.StringMultiLine(2048);
            ReportType reportType = (ReportType)Mathf.Clamp(msg.read.Int32(), 0, 5);
            if (ConVar.Server.printReportsToConsole)
            {
                DebugEx.Log(string.Format("[FeedbackReport] {0} reported {1} - \"{2}\" \"{3}\"", new object[]
                {
                __instance,
                reportType,
                text,
                text2
                }), StackTraceLogType.None);
                Facepunch.RCon.Broadcast(Facepunch.RCon.LogType.Report, new
                {
                    PlayerId = __instance.UserIDString,
                    PlayerName = __instance.displayName,
                    Subject = text,
                    Message = text2,
                    Type = reportType
                });
            }
            if (!string.IsNullOrEmpty(ConVar.Server.reportsServerEndpoint))
            {
                string image = msg.read.StringMultiLine(60000);
                Facepunch.Models.Feedback feedback = new Facepunch.Models.Feedback
                {
                    Type = reportType,
                    Message = text2,
                    Subject = text
                };
                feedback.AppInfo.Image = image;
                Facepunch.Feedback.ServerReport(ConVar.Server.reportsServerEndpoint, __instance.userID, ConVar.Server.reportsServerEndpointKey, feedback);
            }
            Interface.CallHook("OnFeedbackReported", __instance, text, text2, reportType);
            return false;
        }
    }

    //OnPlayerReported Hook
    [HarmonyPatch(typeof(BasePlayer), "OnPlayerReported", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_OnPlayerReported
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            string text = msg.read.String(256);
            string text2 = msg.read.StringMultiLine(2048);
            string text3 = msg.read.String(256);
            string text4 = msg.read.String(256);
            string text5 = msg.read.String(256);
            DebugEx.Log(string.Format("[PlayerReport] {0} reported {1}[{2}] - \"{3}\"", new object[]
            {
            __instance,
            text5,
            text4,
            text
            }), StackTraceLogType.None);
            Facepunch.RCon.Broadcast(Facepunch.RCon.LogType.Report, new
            {
                PlayerId = __instance.UserIDString,
                PlayerName = __instance.displayName,
                TargetId = text4,
                TargetName = text5,
                Subject = text,
                Message = text2,
                Type = text3
            });
            Interface.CallHook("OnPlayerReported", __instance, text5, text4, text, text2, text3);
            return false;
        }
    }

    //CanNetworkTo Hook
    [HarmonyPatch(typeof(BasePlayer), "ShouldNetworkTo",typeof(BasePlayer))]
    internal class BasePlayer_ShouldNetworkTo
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer player,BasePlayer __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanNetworkTo", __instance, player);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }

    //OnPlayerKicked Hook
    [HarmonyPatch(typeof(BasePlayer), "Kick", typeof(string))]
    internal class BasePlayer_Kick
    {
        [HarmonyPostfix]
        static void Postfix(string reason, BasePlayer __instance)
        {
            Interface.CallHook("OnPlayerKicked", __instance, reason);
        }
    }

    //CanDropActiveItem Hook
    [HarmonyPatch(typeof(BasePlayer), "ShouldDropActiveItem")]
    internal class BasePlayer_ShouldDropActiveItem
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanDropActiveItem", __instance);
            __result = !(obj is bool) || (bool)obj;
            return false;
        }
    }

    //OnPlayerHealthChange Hook
    [HarmonyPatch(typeof(BasePlayer), "OnHealthChanged",typeof(float),typeof(float))]
    internal class BasePlayer_OnHealthChanged
    {
        [HarmonyPrefix]
        static bool Prefix(float oldvalue, float newvalue,BasePlayer __instance)
        {
            if (Interface.CallHook("OnPlayerHealthChange", __instance, oldvalue, newvalue) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnPlayerRespawned Hook
    [HarmonyPatch(typeof(BasePlayer), "RespawnAt", typeof(Vector3), typeof(Quaternion), typeof(BaseEntity))]
    internal class BasePlayer_RespawnAt
    {
        [HarmonyPostfix]
        static void Postfix(Vector3 position, Quaternion rotation, BaseEntity spawnPointEntity, BasePlayer __instance)
        {
            Interface.CallHook("OnPlayerRespawned", __instance);
        }
    }

    //OnPlayerCorpseSpawn Hook
    [HarmonyPatch(typeof(BasePlayer), "CreateCorpse")]
    internal class BasePlayer_CreateCorpse
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance, ref BaseCorpse __result)
        {
            if (Interface.CallHook("OnPlayerCorpseSpawn", __instance) != null)
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    //OnPlayerLanded Hook
    [HarmonyPatch(typeof(BasePlayer), "ApplyFallDamageFromVelocity", typeof(float))]
    internal class BasePlayer_ApplyFallDamageFromVelocity2
    {
        [HarmonyPostfix]
        static void Postfix(float velocity, BasePlayer __instance)
        {
            float num = Mathf.InverseLerp(-15f, -100f, velocity);
            if (num == 0f)
            {
                return;
            }
            Interface.CallHook("OnPlayerLanded", __instance, num);
        }
    }

    //OnPlayerLand Hook
    [HarmonyPatch(typeof(BasePlayer), "ApplyFallDamageFromVelocity",typeof(float))]
    internal class BasePlayer_ApplyFallDamageFromVelocity
    {
        [HarmonyPrefix]
        static bool Prefix(float velocity,BasePlayer __instance)
        {
            float num = Mathf.InverseLerp(-15f, -100f, velocity);
            if (num == 0f || Interface.CallHook("OnPlayerLand", __instance, num) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnPlayerSleepEnded Hook
    [HarmonyPatch(typeof(BasePlayer), "EndSleeping")]
    internal class BasePlayer_EndSleeping2
    {
        [HarmonyPostfix]
        static void Postfix(BasePlayer __instance)
        {
            Interface.CallHook("OnPlayerSleepEnded", __instance);
        }
    }

    //OnPlayerSleepEnd Hook
    [HarmonyPatch(typeof(BasePlayer), "EndSleeping")]
    internal class BasePlayer_EndSleeping
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (!__instance.IsSleeping() || Interface.CallHook("OnPlayerSleepEnd", __instance) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnPlayerSleep Hook
    [HarmonyPatch(typeof(BasePlayer), "StartSleeping")]
    internal class BasePlayer_StartSleeping
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer __instance)
        {
            if (__instance.IsSleeping())
            {
                return false;
            }
            Interface.CallHook("OnPlayerSleep", __instance);
            return true;
        }
    }

    //CanCreateWorldProjectile Hook
    [HarmonyPatch(typeof(BasePlayer), "CreateWorldProjectile", typeof(HitInfo), typeof(ItemDefinition), typeof(ItemModProjectile), typeof(Projectile), typeof(Item))]
    internal class BasePlayer_CreateWorldProjectile
    {
        [HarmonyPrefix]
        static bool Prefix(HitInfo info, ItemDefinition itemDef, ItemModProjectile itemMod, Projectile projectilePrefab, Item recycleItem)
        {
            if (Interface.CallHook("CanCreateWorldProjectile", info, itemDef) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnMapMarkersCleared Hook
    [HarmonyPatch(typeof(BasePlayer), "Server_AddMarker", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_Server_ClearMapMarkers2
    {
        [HarmonyPostfix]
        static void Postfix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            Interface.CallHook("OnMapMarkersCleared", __instance);
        }
    }

    //OnMapMarkersClear Hook
    [HarmonyPatch(typeof(BasePlayer), "Server_ClearMapMarkers", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_Server_ClearMapMarkers
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            if (Interface.CallHook("OnMapMarkersClear", __instance, __instance.State.pointsOfInterest) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnMapMarkerAdded Hook
    [HarmonyPatch(typeof(BasePlayer), "Server_AddMarker", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_Server_AddMarker2
    {
        [HarmonyPostfix]
        static void Postfix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            MapNote mapNote = MapNote.Deserialize(msg.read);
            mapNote.colourIndex = __instance.State.pointsOfInterest.Count;
            Interface.CallHook("OnMapMarkerAdded", __instance, mapNote);
        }
    }

    //OnMapMarkerAdd Hook
    [HarmonyPatch(typeof(BasePlayer), "Server_AddMarker", typeof(BaseEntity.RPCMessage))]
    internal class BasePlayer_Server_AddMarker
    {
        [HarmonyPrefix]
        static bool Prefix(BaseEntity.RPCMessage msg, BasePlayer __instance)
        {
            if (Interface.CallHook("OnMapMarkerAdd", __instance, MapNote.Deserialize(msg.read)) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnTeamUpdate Hook
    [HarmonyPatch(typeof(BasePlayer), "UpdateTeam", typeof(ulong))]
    internal class BasePlayer_UpdateTeam
    {
        [HarmonyPrefix]
        static bool Prefix(ulong newTeam, BasePlayer __instance)
        {
            if (Interface.CallHook("OnTeamUpdate", __instance.currentTeam, newTeam, __instance) != null)
            {
                return false;
            }
            return true;
        }
    }

    //OnEntitySnapshot Hook
    [HarmonyPatch(typeof(BasePlayer), "SendEntitySnapshot", typeof(BaseNetworkable))]
    internal class BasePlayer_SendEntitySnapshot
    {
        [HarmonyPrefix]
        static bool Prefix(BaseNetworkable ent, BasePlayer __instance)
        {
            if (Interface.CallHook("OnEntitySnapshot", ent, __instance.net.connection) != null)
            {
                return false;
            }
            return true;
        }
    }

    //CanLootPlayer Hook
    [HarmonyPatch(typeof(BasePlayer), "CanBeLooted", typeof(BasePlayer))]
    internal class BasePlayer_CanBeLooted
    {
        [HarmonyPrefix]
        static bool Prefix(BasePlayer player, BasePlayer __instance, ref bool __result)
        {
            object obj = Interface.CallHook("CanLootPlayer", __instance, player);
            if (obj is bool)
            {
                __result = (bool)obj;
                return false;
            }
            return true;
        }
    }

    #endregion
}
