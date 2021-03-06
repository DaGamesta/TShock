﻿/*   
TShock, a server mod for Terraria
Copyright (C) 2011 The TShock Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
/* TShock wouldn't be possible without:
 * Github
 * Microsoft Visual Studio 2010
 * Adrenic
 * And you, for your continued support and devotion to the evolution of TShock
 * Kerplunc Gaming
 * TerrariaGSP
 * XNS Technology Group (Xenon Servers)
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Mono.Data.Sqlite;
using Hooks;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Rests;
using Terraria;
using TShockAPI.DB;
using TShockAPI.Net;

namespace TShockAPI
{
    [APIVersion(1, 10)]
    public class TShock : TerrariaPlugin
    {
		public static readonly Version VersionNum = Assembly.GetExecutingAssembly().GetName().Version;
        public static readonly string VersionCodename = "Zidonuke fixin' what Redigit doesn't";

        public static string SavePath = "tshock";

        public static TSPlayer[] Players = new TSPlayer[Main.maxPlayers];
        public static BanManager Bans;
        public static WarpManager Warps;
        public static RegionManager Regions;
        public static BackupManager Backups;
        public static GroupManager Groups;
        public static UserManager Users;
        public static ItemManager Itembans;
        public static RemeberedPosManager RememberedPos;
        public static InventoryManager InventoryDB;
        public static ConfigFile Config { get; set; }
        public static IDbConnection DB;
        public static bool OverridePort;
        public static PacketBufferer PacketBuffer;
        public static MaxMind.GeoIPCountry Geo;
        public static SecureRest RestApi;
        public static RestManager RestManager;
		public static Utils Utils = new Utils();

        /// <summary>
        /// Called after TShock is initialized. Useful for plugins that needs hooks before tshock but also depend on tshock being loaded.
        /// </summary>
        public static event Action Initialized;


        public override Version Version
        {
            get { return VersionNum; }
        }

        public override string Name
        {
            get { return "TShock"; }
        }

        public override string Author
        {
            get { return "The Nyx Team"; }
        }

        public override string Description
        {
            get { return "The administration modification of the future."; }
        }

        public TShock(Main game)
            : base(game)
        {
            Config = new ConfigFile();
            Order = 0;
        }



        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        public override void Initialize()
        {
            HandleCommandLine(Environment.GetCommandLineArgs());

            if (!Directory.Exists(SavePath))
                Directory.CreateDirectory(SavePath);

#if DEBUG
            Log.Initialize(Path.Combine(SavePath, "log.txt"), LogLevel.All, false);
#else
            Log.Initialize(Path.Combine(SavePath, "log.txt"), LogLevel.All & ~LogLevel.Debug, false);
#endif
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            try
            {
                if (File.Exists(Path.Combine(SavePath, "tshock.pid")))
                {
                    Log.ConsoleInfo("TShock was improperly shut down. Please avoid this in the future, world corruption may result from this.");
                    File.Delete(Path.Combine(SavePath, "tshock.pid"));
                }
                File.WriteAllText(Path.Combine(SavePath, "tshock.pid"), Process.GetCurrentProcess().Id.ToString());

                ConfigFile.ConfigRead += OnConfigRead;
                FileTools.SetupConfig();

                HandleCommandLine_Port(Environment.GetCommandLineArgs());

                if (Config.StorageType.ToLower() == "sqlite")
                {
                    string sql = Path.Combine(SavePath, "tshock.sqlite");
                    DB = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
                }
                else if (Config.StorageType.ToLower() == "mysql")
                {
                    try
                    {
                        var hostport = Config.MySqlHost.Split(':');
                        DB = new MySqlConnection();
                        DB.ConnectionString =
                            String.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                                          hostport[0],
                                          hostport.Length > 1 ? hostport[1] : "3306",
                                          Config.MySqlDbName,
                                          Config.MySqlUsername,
                                          Config.MySqlPassword
                                );
                    }
                    catch (MySqlException ex)
                    {
                        Log.Error(ex.ToString());
                        throw new Exception("MySql not setup correctly");
                    }
                }
                else
                {
                    throw new Exception("Invalid storage type");
                }

                Backups = new BackupManager(Path.Combine(SavePath, "backups"));
                Backups.KeepFor = Config.BackupKeepFor;
                Backups.Interval = Config.BackupInterval;
                Bans = new BanManager(DB);
                Warps = new WarpManager(DB);
                Users = new UserManager(DB);
                Groups = new GroupManager(DB);
                Groups.LoadPermisions();
                Regions = new RegionManager(DB);
                Itembans = new ItemManager(DB);
                RememberedPos = new RemeberedPosManager(DB);
                InventoryDB = new InventoryManager(DB);
                RestApi = new SecureRest(Netplay.serverListenIP, 8080);
                RestApi.Verify += RestApi_Verify;
                RestApi.Port = Config.RestApiPort;
                RestManager = new RestManager(RestApi);
                RestManager.RegisterRestfulCommands();

                var geoippath = Path.Combine(SavePath, "GeoIP.dat");
                if (Config.EnableGeoIP && File.Exists(geoippath))
                    Geo = new MaxMind.GeoIPCountry(geoippath);

                Log.ConsoleInfo(string.Format("TerrariaShock Version {0} ({1}) now running.", Version, VersionCodename));

                GameHooks.PostInitialize += OnPostInit;
                GameHooks.Update += OnUpdate;
                ServerHooks.Join += OnJoin;
                ServerHooks.Leave += OnLeave;
                ServerHooks.Chat += OnChat;
                ServerHooks.Command += ServerHooks_OnCommand;
                NetHooks.GetData += OnGetData;
                NetHooks.SendData += NetHooks_SendData;
                NetHooks.GreetPlayer += OnGreetPlayer;
                NpcHooks.StrikeNpc += NpcHooks_OnStrikeNpc;
                ProjectileHooks.SetDefaults += OnProjectileSetDefaults;
                WorldHooks.StartHardMode += OnStartHardMode;

                GetDataHandlers.InitGetDataHandler();
                Commands.InitCommands();
                //RconHandler.StartThread();

                if (Config.BufferPackets)
                    PacketBuffer = new PacketBufferer();

                Log.ConsoleInfo("AutoSave " + (Config.AutoSave ? "Enabled" : "Disabled"));
                Log.ConsoleInfo("Backups " + (Backups.Interval > 0 ? "Enabled" : "Disabled"));

                if (Initialized != null)
                    Initialized();
            }
            catch (Exception ex)
            {
                Log.Error("Fatal Startup Exception");
                Log.Error(ex.ToString());
                Environment.Exit(1);
            }

        }

    	private void callHome()
    	{
    		string fp;
    		string lolpath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/.tshock/";
			if (!Directory.Exists(lolpath))
			{
				Directory.CreateDirectory(lolpath);
			}
    		if (!File.Exists(Path.Combine(lolpath, Netplay.serverPort + ".fingerprint")))
    		{
    			fp = "";
    			int random = Utils.Random.Next(500000, 1000000);
    			fp += random;

    			fp = Utils.HashPassword(Netplay.serverIP + fp + Netplay.serverPort + Netplay.serverListenIP);
    			TextWriter tw = new StreamWriter(Path.Combine(lolpath, Netplay.serverPort + ".fingerprint"));
    			tw.Write(fp);
    			tw.Close();
    		} else
    		{
    			fp = "";
				TextReader tr = new StreamReader(Path.Combine(lolpath, Netplay.serverPort + ".fingerprint"));
    			fp = tr.ReadToEnd();
    			tr.Close();
    		}

			using (var client = new WebClient())
			{
				client.Headers.Add("user-agent",
								   "TShock (" + VersionNum + ")");
				try
				{
					string response = client.DownloadString("http://tshock.co/tickto.php?do=log&fp=" + fp + "&ver=" + VersionNum + "&port=" + Netplay.serverPort);
					Console.ForegroundColor = ConsoleColor.Cyan;
					Console.WriteLine("\nRegistered with stat tracker: " + response + "\n");
					Console.ForegroundColor = ConsoleColor.Gray;
				}
				catch (Exception e)
				{
					Log.Error(e.ToString());
				}
			}
    	}

    	RestObject RestApi_Verify(string username, string password)
        {
            var userAccount = TShock.Users.GetUserByName(username);
            if (userAccount == null)
            {
                return new RestObject("401") { Error = "Invalid username/password combination provided. Please re-submit your query with a correct pair." };
            }

            if (TShock.Utils.HashPassword(password).ToUpper() != userAccount.Password.ToUpper())
            {
                return new RestObject("401") { Error = "Invalid username/password combination provided. Please re-submit your query with a correct pair." };
            }

            if (!TShock.Utils.GetGroup(userAccount.Group).HasPermission("api") && userAccount.Group != "superadmin")
            {
                return new RestObject("403") { Error = "Although your account was successfully found and identified, your account lacks the permission required to use the API. (api)" };
            }

            return new RestObject("200") { Response = "Successful login" }; //Maybe return some user info too?
        }

        protected override void  Dispose(bool disposing)
        {
            if (disposing)
            {
				if (Geo != null)
				{
					Geo.Dispose();					
				}
                GameHooks.PostInitialize -= OnPostInit;
                GameHooks.Update -= OnUpdate;
                ServerHooks.Join -= OnJoin;
                ServerHooks.Leave -= OnLeave;
                ServerHooks.Chat -= OnChat;
                ServerHooks.Command -= ServerHooks_OnCommand;
                NetHooks.GetData -= OnGetData;
                NetHooks.SendData -= NetHooks_SendData;
                NetHooks.GreetPlayer -= OnGreetPlayer;
                NpcHooks.StrikeNpc -= NpcHooks_OnStrikeNpc;
                ProjectileHooks.SetDefaults -= OnProjectileSetDefaults;
                if (File.Exists(Path.Combine(SavePath, "tshock.pid")))
                {
                    File.Delete(Path.Combine(SavePath, "tshock.pid"));
                }
                RestApi.Dispose();
                Log.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Handles exceptions that we didn't catch or that Red fucked up
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error(e.ExceptionObject.ToString());

            if (e.ExceptionObject.ToString().Contains("Terraria.Netplay.ListenForClients") ||
                e.ExceptionObject.ToString().Contains("Terraria.Netplay.ServerLoop"))
            {
                var sb = new List<string>();
                for (int i = 0; i < Netplay.serverSock.Length; i++)
                {
                    if (Netplay.serverSock[i] == null)
                    {
                        sb.Add("Sock[" + i + "]");
                    }
                    else if (Netplay.serverSock[i].tcpClient == null)
                    {
                        sb.Add("Tcp[" + i + "]");
                    }
                }
                Log.Error(string.Join(", ", sb));
            }

            if (e.IsTerminating)
            {
                if (Main.worldPathName != null && Config.SaveWorldOnCrash)
                {
                    Main.worldPathName += ".crash";
                    WorldGen.saveWorld();
                }
            }
        }

        private void HandleCommandLine(string[] parms)
        {
            for (int i = 0; i < parms.Length; i++)
            {
                if (parms[i].ToLower() == "-ip")
                {
                    IPAddress ip;
                    if (IPAddress.TryParse(parms[++i], out ip))
                    {
                        Netplay.serverListenIP = ip;
                        Console.Write("Using IP: {0}", ip);
                    }
                    else
                    {
                        Console.WriteLine("Bad IP: {0}", parms[i]);
                    }
                }
                if (parms[i].ToLower() == "-configpath")
                {
                    var path = parms[++i];
                    if (path.IndexOfAny(Path.GetInvalidPathChars()) == -1)
                    {
                        SavePath = path;
                        Log.ConsoleInfo("Config path has been set to " + path);
                    }
                }
                if (parms[i].ToLower() == "-worldpath")
                {
                    var path = parms[++i];
                    if (path.IndexOfAny(Path.GetInvalidPathChars()) == -1)
                    {
                        Main.WorldPath = path;
                        Log.ConsoleInfo("World path has been set to " + path);
                    }
                }
            }
        }

        private void HandleCommandLine_Port(string[] parms)
        {
            for (int i = 0; i < parms.Length; i++)
            {
                if (parms[i].ToLower() == "-port")
                {
                    int port = Convert.ToInt32(parms[++i]);
                    Netplay.serverPort = port;
                    Config.ServerPort = port;
                    OverridePort = true;
                    Log.ConsoleInfo("Port overridden by startup argument. Set to " + port);
                }
            }
        }

        /*
         * Hooks:
         * 
         */

        public static int AuthToken = -1;

        private void OnPostInit()
        {
            if (!File.Exists(Path.Combine(SavePath, "auth.lck")) && !File.Exists(Path.Combine(SavePath, "authcode.txt")))
            {
                var r = new Random((int)DateTime.Now.ToBinary());
                AuthToken = r.Next(100000, 10000000);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("TShock Notice: To become SuperAdmin, join the game and type /auth " + AuthToken);
                Console.WriteLine("This token will display until disabled by verification. (/auth-verify)");
                Console.ForegroundColor = ConsoleColor.Gray;
                FileTools.CreateFile(Path.Combine(SavePath, "authcode.txt"));
                using (var tw = new StreamWriter(Path.Combine(SavePath, "authcode.txt")))
                {
                    tw.WriteLine(AuthToken);
                }
            }
            else if (File.Exists(Path.Combine(SavePath, "authcode.txt")))
            {
                using (var tr = new StreamReader(Path.Combine(SavePath, "authcode.txt")))
                {
                    AuthToken = Convert.ToInt32(tr.ReadLine());
                }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    "TShock Notice: authcode.txt is still present, and the AuthToken located in that file will be used.");
                Console.WriteLine("To become superadmin, join the game and type /auth " + AuthToken);
                Console.WriteLine("This token will display until disabled by verification. (/auth-verify)");
                Console.ForegroundColor = ConsoleColor.Gray;
            }
            else
            {
                AuthToken = 0;
            }
            Regions.ReloadAllRegions();
            if (Config.RestApiEnabled)
                RestApi.Start();
        	
			Thread t = new Thread(callHome);
			t.Start();

        }


        private DateTime LastCheck = DateTime.UtcNow;

        private void OnUpdate()
        {
            UpdateManager.UpdateProcedureCheck();

            if (Backups.IsBackupTime)
                Backups.Backup();

            //call these every second, not every update
            if ((DateTime.UtcNow - LastCheck).TotalSeconds >= 1)
            {
                LastCheck = DateTime.UtcNow;
                foreach (TSPlayer player in Players)
                {
                    if (player != null && player.Active)
                    {
                        if (player.TilesDestroyed != null)
                        {
                            if (player.TileThreshold >= Config.TileThreshold)
                            {
                                TSPlayer.Server.RevertKillTile(player.TilesDestroyed);
                            }
                            if (player.TileThreshold > 0)
                            {
                                player.TileThreshold = 0;
                                player.TilesDestroyed.Clear();
                            }
                        }
                        /*if (CheckPlayerCollision(player.TileX, player.TileY))
                            player.SendMessage("You are currently nocliping!", Color.Red);*/
                        if (player.ForceSpawn && (DateTime.Now - player.LastDeath).Seconds >= 3)
                        {
                            player.Spawn();
                            player.ForceSpawn = false;
                        }
                    }
                }
            }
        }

        private void OnJoin(int ply, HandledEventArgs handler)
        {
            var player = new TSPlayer(ply);
            if (Config.EnableDNSHostResolution)
            {
                player.Group = Users.GetGroupForIPExpensive(player.IP);
            }
            else
            {
                player.Group = Users.GetGroupForIP(player.IP);
            }

            if (TShock.Utils.ActivePlayers() + 1 > Config.MaxSlots && !player.Group.HasPermission(Permissions.reservedslot))
            {
                TShock.Utils.ForceKick(player, Config.ServerFullReason);
                handler.Handled = true;
                return;
            }

            var ipban = Bans.GetBanByIp(player.IP);
            var nameban = Bans.GetBanByName(player.Name);
            Ban ban = null;
            if (ipban != null && Config.EnableIPBans)
                ban = ipban;
            else if (nameban != null && Config.EnableIPBans)
                ban = nameban;

            if (ban != null)
            {
                TShock.Utils.ForceKick(player, string.Format("You are banned: {0}", ban.Reason));
                handler.Handled = true;
                return;
            }

            if (!FileTools.OnWhitelist(player.IP))
            {
                TShock.Utils.ForceKick(player, "Not on whitelist.");
                handler.Handled = true;
                return;
            }

            Players[ply] = player;
        }

        private void OnLeave(int ply)
        {
            var tsplr = Players[ply];
            Players[ply] = null;

            if (tsplr != null && tsplr.ReceivedInfo)
            {
                TShock.Utils.Broadcast(tsplr.Name + " has left", Color.Yellow);
                Log.Info(string.Format("{0} left.", tsplr.Name));

                if (tsplr.IsLoggedIn)
                {
                    tsplr.PlayerData.CopyInventory(tsplr);
                    InventoryDB.InsertPlayerData(tsplr, tsplr.UserID);
                }

                if (Config.RememberLeavePos)
                {
                    RememberedPos.InsertLeavePos(tsplr.Name, tsplr.IP, (int)(tsplr.X / 16), (int)(tsplr.Y / 16));
                }
            }
        }

        private void OnChat(messageBuffer msg, int ply, string text, HandledEventArgs e)
        {
            if (e.Handled)
                return;

            var tsplr = Players[msg.whoAmI];
            if (tsplr == null)
            {
                e.Handled = true;
                return;
            }

            if (!TShock.Utils.ValidString(text))
            {
                e.Handled = true;
                return;
            }

            if (tsplr.Group.HasPermission(Permissions.adminchat) && !text.StartsWith("/") && Config.AdminChatEnabled)
            {
                TShock.Utils.Broadcast(Config.AdminChatPrefix + "<" + tsplr.Name + "> " + text,
                                tsplr.Group.R, tsplr.Group.G,
                                tsplr.Group.B);
                e.Handled = true;
                return;
            }

            if (text.StartsWith("/"))
            {
                try
                {
                    e.Handled = Commands.HandleCommand(tsplr, text);
                }
                catch (Exception ex)
                {
                    Log.ConsoleError("Command exception");
                    Log.Error(ex.ToString());
                }
            }
            else
            {
                TShock.Utils.Broadcast("{2}<{0}> {1}".SFormat(tsplr.Name, text, Config.ChatDisplayGroup ? "[{0}] ".SFormat(tsplr.Group.Name) : ""),
                                tsplr.Group.R, tsplr.Group.G,
                                tsplr.Group.B);
                //Log.Info(string.Format("{0} said: {1}", tsplr.Name, text));
                e.Handled = true;
            }
        }

        /// <summary>
        /// When a server command is run.
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="e"></param>
        private void ServerHooks_OnCommand(string text, HandledEventArgs e)
        {
            if (e.Handled)
                return;

            // Damn you ThreadStatic and Redigit
            if (Main.rand == null)
            {
                Main.rand = new Random();
            }
            if (WorldGen.genRand == null)
            {
                WorldGen.genRand = new Random();
            }

            if (text.StartsWith("exit"))
            {
                TShock.Utils.ForceKickAll("Server shutting down!");
            }
            else if (text.StartsWith("playing") || text.StartsWith("/playing"))
            {
                int count = 0;
                foreach (TSPlayer player in Players)
                {
                    if (player != null && player.Active)
                    {
                        count++;
                        TSPlayer.Server.SendMessage(string.Format("{0} ({1}) [{2}] <{3}>", player.Name, player.IP,
                                                                  player.Group.Name, player.UserAccountName));
                    }
                }
                TSPlayer.Server.SendMessage(string.Format("{0} players connected.", count));
                e.Handled = true;
            }
            else if (text.StartsWith("say "))
            {
                Log.Info(string.Format("Server said: {0}", text.Remove(0, 4)));
            }
            else if (text == "autosave")
            {
                Main.autoSave = Config.AutoSave = !Config.AutoSave;
                Log.ConsoleInfo("AutoSave " + (Config.AutoSave ? "Enabled" : "Disabled"));
                e.Handled = true;
            }
            else if (text.StartsWith("/"))
            {
                if (Commands.HandleCommand(TSPlayer.Server, text))
                    e.Handled = true;
            }
        }

        private void OnGetData(GetDataEventArgs e)
        {
            if (e.Handled)
                return;

            PacketTypes type = e.MsgID;

            Debug.WriteLine("Recv: {0:X}: {2} ({1:XX})", e.Msg.whoAmI, (byte)type, type);

            var player = Players[e.Msg.whoAmI];
            if (player == null)
            {
                e.Handled = true;
                return;
            }

            if (!player.ConnectionAlive)
            {
                e.Handled = true;
                return;
            }

            // Stop accepting updates from player as this player is going to be kicked/banned during OnUpdate (different thread so can produce race conditions)
            if (player.TileThreshold >= Config.TileThreshold && !player.Group.HasPermission(Permissions.ignorekilltiledetection))
            {
                e.Handled = true;
            }
            else
            {
                using (var data = new MemoryStream(e.Msg.readBuffer, e.Index, e.Length))
                {
                    try
                    {
                        if (GetDataHandlers.HandlerGetData(type, player, data))
                            e.Handled = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex.ToString());
                    }
                }
            }
        }

        private void OnGreetPlayer(int who, HandledEventArgs e)
        {
            var player = Players[who];
            if (player == null)
            {
                e.Handled = true;
                return;
            }

            TShock.Utils.ShowFileToUser(player, "motd.txt");
            if (HackedHealth(player))
            {
                player.IgnoreActionsForCheating = true;
                player.SendMessage("You are using a health/mana cheat. Please choose a different character.");
            }

            if (HackedInventory(player))
            {
                player.IgnoreActionsForCheating = true;
            }

            NetMessage.syncPlayers();

            if (Config.ServerSideInventory && !player.IsLoggedIn)
            {
                player.IgnoreActionsForInventory = true;
                player.SendMessage("Server Side Inventory is enabled! Please /register or /login to play!", Color.Red);
            }

            if (Config.AlwaysPvP && !player.TPlayer.hostile)
            {
                player.IgnoreActionsForPvP = true;
                player.SendMessage("PvP is forced! Enable PvP else you can't move or do anything!", Color.Red);
            }
            if (player.Group.HasPermission(Permissions.causeevents) && Config.InfiniteInvasion)
            {
                StartInvasion();
            }
            if (Config.RememberLeavePos)
            {
                var pos = RememberedPos.GetLeavePos(player.Name, player.IP);
                player.Teleport((int)pos.X, (int)pos.Y);
                player.SendTileSquare((int)pos.X, (int)pos.Y);
            }
            e.Handled = true;
        }

        private void NpcHooks_OnStrikeNpc(NpcStrikeEventArgs e)
        {
            if (Config.InfiniteInvasion)
            {
                IncrementKills();
                if (Main.invasionSize < 10)
                {
                    Main.invasionSize = 20000000;
                }
            }
        }

        void OnProjectileSetDefaults(SetDefaultsEventArgs<Projectile, int> e)
        {
            if (e.Info == 43)
                if (Config.DisableTombstones)
                    e.Object.SetDefaults(0);
        }

        /// <summary>
        /// Send bytes to client using packetbuffering if available
        /// </summary>
        /// <param name="client">socket to send to</param>
        /// <param name="bytes">bytes to send</param>
        /// <returns>False on exception</returns>
        public static bool SendBytes(ServerSock client, byte[] bytes)
        {
            if (PacketBuffer != null)
            {
                PacketBuffer.BufferBytes(client, bytes);
                return true;
            }

            return SendBytesBufferless(client,bytes);
        }
        /// <summary>
        /// Send bytes to a client ignoring the packet buffer
        /// </summary>
        /// <param name="client">socket to send to</param>
        /// <param name="bytes">bytes to send</param>
        /// <returns>False on exception</returns>
        public static bool SendBytesBufferless(ServerSock client, byte[] bytes)
        {
            try
            {
                if (client.tcpClient.Connected)
                    client.networkStream.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("This is a normal exception");
                Log.Warn(ex.ToString());
            }
            return false;
        }

        void NetHooks_SendData(SendDataEventArgs e)
        {
            if (e.MsgID == PacketTypes.Disconnect)
            {
                Action<ServerSock,string> senddisconnect = (sock, str) =>
                {
                    if (sock == null || !sock.active)
                        return;
                    sock.kill = true;
                    using (var ms = new MemoryStream())
                    {
                        new DisconnectMsg {Reason = str}.PackFull(ms);
                        SendBytesBufferless(sock, ms.ToArray());
                    }
                };

                if (e.remoteClient != -1)
                {
                    senddisconnect(Netplay.serverSock[e.remoteClient], e.text);
                }
                else
                {
                    for (int i = 0; i < Netplay.serverSock.Length; i++)
                    {
                        if (e.ignoreClient != -1 && e.ignoreClient == i)
                            continue;

                        senddisconnect(Netplay.serverSock[i], e.text);
                    }
                }
                e.Handled = true;
            }
        }

        private void OnSaveWorld(bool resettime, HandledEventArgs e)
        {
            TShock.Utils.Broadcast("Saving world. Momentary lag might result from this.", Color.Red);
            Thread SaveWorld = new Thread(TShock.Utils.SaveWorld);
            SaveWorld.Start();
            e.Handled = true;
        }

        void OnStartHardMode(HandledEventArgs e)
        {
            if (Config.DisableHardmode)
                e.Handled = true;
        }

        /*
         * Useful stuff:
         * */

        public static void StartInvasion()
        {
            Main.invasionType = 1;
            if (Config.InfiniteInvasion)
            {
                Main.invasionSize = 20000000;
            }
            else
            {
                Main.invasionSize = 100 + (Config.InvasionMultiplier * TShock.Utils.ActivePlayers());
            }

            Main.invasionWarn = 0;
            if (new Random().Next(2) == 0)
            {
                Main.invasionX = 0.0;
            }
            else
            {
                Main.invasionX = Main.maxTilesX;
            }
        }

        private static int KillCount;

        public static void IncrementKills()
        {
            KillCount++;
            Random r = new Random();
            int random = r.Next(5);
            if (KillCount % 100 == 0)
            {
                switch (random)
                {
                    case 0:
                        TShock.Utils.Broadcast(string.Format("You call that a lot? {0} goblins killed!", KillCount));
                        break;
                    case 1:
                        TShock.Utils.Broadcast(string.Format("Fatality! {0} goblins killed!", KillCount));
                        break;
                    case 2:
                        TShock.Utils.Broadcast(string.Format("Number of 'noobs' killed to date: {0}", KillCount));
                        break;
                    case 3:
                        TShock.Utils.Broadcast(string.Format("Duke Nukem would be proud. {0} goblins killed.", KillCount));
                        break;
                    case 4:
                        TShock.Utils.Broadcast(string.Format("You call that a lot? {0} goblins killed!", KillCount));
                        break;
                    case 5:
                        TShock.Utils.Broadcast(string.Format("{0} copies of Call of Duty smashed.", KillCount));
                        break;
                }
            }
        }

        public static bool CheckProjectilePermission(TSPlayer player, int index, int type)
        {
            if (type == 43)
            {
                return true;
            }

            if (type == 17 && !player.Group.HasPermission(Permissions.usebanneditem) && TShock.Itembans.ItemIsBanned("Dirt Wand")) //Dirt Wand Projectile
            {
                return true;
            }

            if ((type == 42 || type == 65 || type == 68) && !player.Group.HasPermission(Permissions.usebanneditem) && TShock.Itembans.ItemIsBanned("Sandgun")) //Sandgun Projectiles
            {
                return true;
            }

            Projectile proj = new Projectile();
            proj.SetDefaults(type);

            if (!player.Group.HasPermission(Permissions.usebanneditem) && TShock.Itembans.ItemIsBanned(proj.name))
            {
                return true;
            }

            if (proj.hostile)
            {
                return true;
            }

            return false;
        }

        public static bool CheckTilePermission(TSPlayer player, int tileX, int tileY)
        {
            if (TShock.Config.RangeChecks && ((Math.Abs(player.TileX - tileX) > 32) || (Math.Abs(player.TileY - tileY) > 32)))
            {
                return true;
            }
            if (!player.Group.HasPermission(Permissions.canbuild))
            {
                player.SendMessage("You do not have permission to build!", Color.Red);
                return true;
            }
            if (!player.Group.HasPermission(Permissions.editspawn) && !TShock.Regions.CanBuild(tileX, tileY, player) && TShock.Regions.InArea(tileX, tileY))
            {
                player.SendMessage("Region protected from changes.", Color.Red);
                return true;
            }
            if (TShock.Config.DisableBuild)
            {
                if (!player.Group.HasPermission(Permissions.editspawn))
                {
                    player.SendMessage("World protected from changes.", Color.Red);
                    return true;
                }
            }
            if (TShock.Config.SpawnProtection)
            {
                if (!player.Group.HasPermission(Permissions.editspawn))
                {
                    var flag = TShock.CheckSpawn(tileX, tileY);
                    if (flag)
                    {
                        player.SendMessage("Spawn protected from changes.", Color.Red);
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool CheckSpawn(int x, int y)
        {
            Vector2 tile = new Vector2(x, y);
            Vector2 spawn = new Vector2(Main.spawnTileX, Main.spawnTileY);
            return Distance(spawn, tile) <= Config.SpawnProtectionRadius;
        }
        public static float Distance(Vector2 value1, Vector2 value2)
        {
            float num2 = value1.X - value2.X;
            float num = value1.Y - value2.Y;
            float num3 = (num2 * num2) + (num * num);
            return (float)Math.Sqrt((double)num3);
        }

        public static bool HackedHealth(TSPlayer player)
        {
            return (player.TPlayer.statManaMax > 400) ||
                   (player.TPlayer.statMana > 400) ||
                   (player.TPlayer.statLifeMax > 400) ||
                   (player.TPlayer.statLife > 400);
        }

        public static bool HackedInventory(TSPlayer player)
        {
            bool check = false;

            Item[] inventory = player.TPlayer.inventory;
            Item[] armor = player.TPlayer.armor;
            for (int i = 0; i < NetItem.maxNetInventory; i++)
            {
                if (i < 49)
                {
                    Item item = new Item();
                    if (inventory[i] != null && inventory[i].netID != 0)
                    {
                        item.netDefaults(inventory[i].netID);
                        item.Prefix(inventory[i].prefix);
                        item.AffixName();
                        if (inventory[i].stack > item.maxStack)
                        {
                            check = true;
                            player.SendMessage(String.Format("Stack cheat detected. Remove item {0} ({1}) and then rejoin", item.name, inventory[i].stack), Color.Cyan);
                        }
                    }
                }
                else
                {
                    Item item = new Item();
                    if (armor[i - 48] != null && armor[i - 48].netID != 0)
                    {
                        item.netDefaults(armor[i - 48].netID);
                        item.Prefix(armor[i - 48].prefix);
                        item.AffixName();
                        if (armor[i - 48].stack > item.maxStack)
                        {
                            check = true;
                            player.SendMessage(String.Format("Stack cheat detected. Remove armor {0} ({1}) and then rejoin", item.name, armor[i - 48].stack), Color.Cyan);
                        }
                    }
                }
            }

            return check;
        }

        public static bool CheckInventory(TSPlayer player)
        {
            PlayerData playerData = player.PlayerData;
            bool check = true;

            if (player.TPlayer.statLifeMax > playerData.maxHealth)
            {
                player.SendMessage("Error: Your max health exceeded (" + playerData.maxHealth + ") which is stored on server", Color.Cyan);
                check = false;
            }

            if (player.TPlayer.statManaMax > playerData.maxMana)
            {
                player.SendMessage("Error: Your max mana exceeded (" + playerData.maxMana + ") which is stored on server", Color.Cyan);
                check = false;
            }

            Item[] inventory = player.TPlayer.inventory;
            Item[] armor = player.TPlayer.armor;
            for (int i = 0; i < NetItem.maxNetInventory; i++)
            {
                if (i < 49)
                {
                    Item item = new Item();
                    Item serverItem = new Item();
                    if (inventory[i] != null && inventory[i].netID != 0)
                    {
                        if (playerData.inventory[i].netID != inventory[i].netID)
                        {
                            item.netDefaults(inventory[i].netID);
                            item.Prefix(inventory[i].prefix);
                            item.AffixName();
                            player.SendMessage("Error: Your item (" + item.name + ") needs to be deleted.", Color.Cyan);
                            check = false;
                        }
                        else if (playerData.inventory[i].prefix != inventory[i].prefix)
                        {
                            item.netDefaults(inventory[i].netID);
                            item.Prefix(inventory[i].prefix);
                            item.AffixName();
                            player.SendMessage("Error: Your item (" + item.name + ") needs to be deleted.", Color.Cyan);
                            check = false;
                        }
                        else if (inventory[i].stack > playerData.inventory[i].stack)
                        {
                            item.netDefaults(inventory[i].netID);
                            item.Prefix(inventory[i].prefix);
                            item.AffixName();
                            player.SendMessage("Error: Your item (" + item.name + ") (" + inventory[i].stack + ") needs to have it's stack decreased to (" + playerData.inventory[i].stack + ").", Color.Cyan);
                            check = false;
                        }
                    }
                }
                else
                {
                    Item item = new Item();
                    Item serverItem = new Item();
                    if (armor[i - 48] != null && armor[i - 48].netID != 0)
                    {
                        if (playerData.inventory[i].netID != armor[i - 48].netID)
                        {
                            item.netDefaults(armor[i - 48].netID);
                            item.Prefix(armor[i - 48].prefix);
                            item.AffixName();
                            player.SendMessage("Error: Your armor (" + item.name + ") needs to be deleted.", Color.Cyan);
                            check = false;
                        }
                        else if (playerData.inventory[i].prefix != armor[i - 48].prefix)
                        {
                            item.netDefaults(armor[i - 48].netID);
                            item.Prefix(armor[i - 48].prefix);
                            item.AffixName();
                            player.SendMessage("Error: Your armor (" + item.name + ") needs to be deleted.", Color.Cyan);
                            check = false;
                        }
                        else if (armor[i - 48].stack > playerData.inventory[i].stack)
                        {
                            item.netDefaults(armor[i - 48].netID);
                            item.Prefix(armor[i - 48].prefix);
                            item.AffixName();
                            player.SendMessage("Error: Your armor (" + item.name + ") (" + inventory[i].stack + ") needs to have it's stack decreased to (" + playerData.inventory[i].stack + ").", Color.Cyan);
                            check = false;
                        }
                    }
                }
            }

            return check;
        }

        public static bool CheckIgnores(TSPlayer player)
        {
            bool check = false;
            if (player.IgnoreActionsForPvP)
                check = true;
            if (player.IgnoreActionsForInventory)
                check = true;
            if (player.IgnoreActionsForCheating)
                check = true;
            return check;
        }

        public static bool CheckPlayerCollision(int x, int y)
        {
            if (x + 1 <= Main.maxTilesX && y + 3 <= Main.maxTilesY
                && x >= 0 && y >= 0)
            {
                for (int i = x; i < x + 2; i++)
                {
                    for (int h = y; h < y + 4; h++)
                    {
                        if (!Main.tile[i, h].active || !Main.tileSolid[Main.tile[i, h].type])
                            return false;
                    }
                }
            }
            else
                return false;
            return true;
        }

        public void OnConfigRead(ConfigFile file)
        {
            NPC.defaultMaxSpawns = file.DefaultMaximumSpawns;
            NPC.defaultSpawnRate = file.DefaultSpawnRate;

            Main.autoSave = file.AutoSave;
            if (Backups != null)
            {
                Backups.KeepFor = file.BackupKeepFor;
                Backups.Interval = file.BackupInterval;
            }
            if (!OverridePort)
            {
                Netplay.serverPort = file.ServerPort;
            }

            Netplay.spamCheck = false;

            RconHandler.Password = file.RconPassword;
            RconHandler.ListenPort = file.RconPort;

            TShock.Utils.HashAlgo = file.HashAlgorithm;
        }
    }
}