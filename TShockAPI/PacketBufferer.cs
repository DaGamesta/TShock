﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Terraria;

using Hooks;

namespace TShockAPI
{
    public class PacketBufferer : IDisposable
    {
        /// <summary>
        /// Maximum number of bytes to send per update per socket
        /// </summary>
        public int BytesPerUpdate { get; set; }

        PacketBuffer[] buffers = new PacketBuffer[Netplay.serverSock.Length];

        int[] Bytes = new int[52];
        int[] Packets = new int[52];
        int[] Compressed = new int[52];

#if DEBUG_NET
        Command dump;
        Command flush;
#endif

        public PacketBufferer()
        {
            BytesPerUpdate = 0xFFFF;
            for (int i = 0; i < buffers.Length; i++)
                buffers[i] = new PacketBuffer();

#if DEBUG_NET
            dump = new Command("superadmin", Dump, "netdump");
            flush = new Command("superadmin", Flush, "netflush");
            Commands.ChatCommands.Add(dump);
            Commands.ChatCommands.Add(flush);
#endif

            NetHooks.SendBytes += ServerHooks_SendBytes;
            ServerHooks.SocketReset += ServerHooks_SocketReset;
            GameHooks.PostUpdate += GameHooks_Update;
        }

        ~PacketBufferer()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
#if DEBUG_NET
                Commands.ChatCommands.Remove(dump);
                Commands.ChatCommands.Remove(flush);
#endif
            NetHooks.SendBytes -= ServerHooks_SendBytes;
            ServerHooks.SocketReset -= ServerHooks_SocketReset;
            GameHooks.PostUpdate -= GameHooks_Update;
            }
        }

        void Dump(CommandArgs args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{0,-25}{1,-25}{2,-25}{3}".SFormat("Name:", "Packets", "Bytes", "Compression"));
            for (int i = 1; i < Bytes.Length; i++)
            {
                sb.AppendLine("{0,-25}{1,-25}{2,-25}{3}".SFormat(Enum.GetName(typeof(PacketTypes), i) + ":", Packets[i], Bytes[i], Compressed[i]));
            }
            File.WriteAllText(Path.Combine(TShock.SavePath, "dmp.txt"), sb.ToString());
        }

        void Flush(CommandArgs args)
        {
            Bytes = new int[52];
            Packets = new int[52];
            Compressed = new int[52];
        }

        void GameHooks_Update()
        {
            FlushAll();
        }

        public void FlushAll()
        {
            for (int i = 0; i < Netplay.serverSock.Length; i++)
            {
                Flush(Netplay.serverSock[i]);
            }
        }

        public bool Flush(ServerSock socket)
        {
            try
            {
                if (socket == null || !socket.active)
                    return false;

                if (buffers[socket.whoAmI].Count < 1)
                    return false;

                byte[] buff = buffers[socket.whoAmI].GetBytes(BytesPerUpdate);
                if (buff == null)
                    return false;

                if (SendBytes(socket, buff))
                {
                    buffers[socket.whoAmI].Pop(buff.Length);
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.ConsoleError(e.ToString());
            }
            return false;
        }


        void ServerHooks_SocketReset(ServerSock socket)
        {
            buffers[socket.whoAmI] = new PacketBuffer();
        }

        public bool SendBytes(ServerSock socket, byte[] buffer)
        {
            return SendBytes(socket, buffer, 0, buffer.Length);
        }
        public void BufferBytes(ServerSock socket, byte[] buffer)
        {
            BufferBytes(socket, buffer, 0, buffer.Length);
        }

        public void BufferBytes(ServerSock socket, byte[] buffer, int offset, int count)
        {
            lock (buffers[socket.whoAmI])
            {
#if DEBUG_NET
                int size = (count - offset);
                var pt = buffer[offset + 4];

                Packets[pt]++;
                Bytes[pt] += size;
                Compressed[pt] += Compress(buffer, offset, count);
#endif
                using (var ms = new MemoryStream(buffer, offset, count))
                {
                    buffers[socket.whoAmI].AddRange(ms.ToArray());
                }
            }
        }

        public bool SendBytes(ServerSock socket, byte[] buffer, int offset, int count)
        {
            try
            {
                if (socket.tcpClient.Client != null && socket.tcpClient.Client.Poll(0, SelectMode.SelectWrite))
                {
                    if (Main.runningMono)
                        socket.networkStream.Write(buffer, offset, count);
                    else
                        socket.tcpClient.Client.Send(buffer, offset, count, SocketFlags.None);
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            catch (IOException)
            {
            }
            return false;
        }

        void ServerHooks_SendBytes(ServerSock socket, byte[] buffer, int offset, int count, HandledEventArgs e)
        {
            e.Handled = true;
            BufferBytes(socket, buffer, offset, count);
        }
#if DEBUG_NET
        static int Compress(byte[] buffer, int offset, int count)
        {
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    gzip.Write(buffer, offset, count);
                }
                return (int)ms.Length;
            }
        }
#endif
    }

    public class PacketBuffer : List<byte>
    {
        public byte[] GetBytes(int max)
        {
            lock (this)
            {
                if (this.Count < 1)
                    return null;

                var ret = new byte[Math.Min(max, this.Count)];
                this.CopyTo(0, ret, 0, ret.Length);
                return ret;
            }
        }

        public void Pop(int count)
        {
            lock (this)
            {
                this.RemoveRange(0, count);
            }
        }
    }
}
