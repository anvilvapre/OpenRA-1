#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using OpenRA.FileFormats;

namespace OpenRA.Network
{
	public sealed class ReplayConnection : IConnection
	{
		sealed class Chunk
		{
			public int Frame;
			public (int ClientId, Frame Frame)[] Frames;
		}

		readonly Queue<Chunk> chunks = new();
		readonly Queue<SyncFrame> sync = new();
		readonly int orderLatency;

		public readonly int TickCount;
		public readonly int FinalGameTick;
		public readonly bool IsValid;
		public readonly Session LobbyInfo;
		public readonly string Filename;

		public ReplayConnection(string replayFilename)
		{
			Filename = replayFilename;
			FinalGameTick = ReplayMetadata.Read(replayFilename).GameInfo.FinalGameTick;

			// Parse replay data into a struct that can be fed to the game in chunks
			// to avoid issues with all immediate orders being resolved on the first tick.
			using (var rs = File.OpenRead(replayFilename))
			{
				var frames = new List<(int ClientId, Frame Frame)>();
				var chunk = new Chunk();
				while (rs.Position < rs.Length)
				{
					var client = rs.ReadInt32();
					if (client == ReplayMetadata.MetaStartMarker)
						break;

					var packetLen = rs.ReadInt32();
					var packet = rs.ReadBytes(packetLen);
					var frameId = BitConverter.ToInt32(packet, 0);
					var frame = FrameIO.Parse(client, packet);
					frames.Add((client, frame));

					if (packet.Length > 4 && (packet[4] == (byte)OrderType.Disconnect || packet[4] == (byte)OrderType.SyncHash))
						continue;

					if ((frame.Type == OrderType.Fields || frame.Type == OrderType.Handshake)
						&& frameId == Frame.IdImmediateOrServerOrder)
					{
						var orderData = new OrderPacket((OrderFrame)frame);
						foreach (var o in orderData.GetOrders(null))
						{
							if (o.OrderString == "StartGame")
								IsValid = true;
							else if (o.OrderString == "SyncInfo" && !IsValid)
								LobbyInfo = Session.Deserialize(o.TargetString);
						}
					}
					else
					{
						// Regular order - finalize the chunk
						chunk.Frame = frameId;
						chunk.Frames = frames.ToArray();
						frames.Clear();
						chunks.Enqueue(chunk);
						chunk = new Chunk();

						TickCount = Math.Max(TickCount, frameId);
					}
				}
			}

			var gameSpeeds = Game.ModData.Manifest.Get<GameSpeeds>();
			var gameSpeedName = LobbyInfo.GlobalSettings.OptionOrDefault("gamespeed", gameSpeeds.DefaultSpeed);
			orderLatency = gameSpeeds.Speeds[gameSpeedName].OrderLatency;
		}

		void IConnection.StartGame() { }

		// Do nothing: ignore locally generated orders
		void IConnection.Send(int frame, IEnumerable<Order> orders) { }
		void IConnection.SendImmediate(IEnumerable<Order> orders) { }

		void IConnection.SendSync(SyncFrame frame)
		{
			sync.Enqueue(frame);
		}

		void IConnection.Receive(OrderManager orderManager)
		{
			while (sync.Count != 0)
				orderManager.ReceiveSync(sync.Dequeue());

			while (chunks.Count != 0 && chunks.Peek().Frame <= orderManager.NetFrameNumber + orderLatency)
			{
				foreach (var (clientId, frame) in chunks.Dequeue().Frames)
				{
					switch (frame.Type)
					{
						case OrderType.Disconnect:
							orderManager.ReceiveDisconnect((DisconnectFrame)frame);
							break;
						case OrderType.SyncHash:
							orderManager.ReceiveSync((SyncFrame)frame);
							break;
						case OrderType.Fields:
							{
								var orders = new OrderPacket((OrderFrame)frame);
								if (frame.Id == 0)
									orderManager.ReceiveImmediateOrders(clientId, orders);
								else
									orderManager.ReceiveOrders(clientId, (frame.Id, orders));
								break;
							}

						default:
							throw new InvalidDataException($"Received unknown frame from client {clientId}: {frame}");
					}
				}
			}
		}

		int IConnection.LocalClientId => -1;

		void IDisposable.Dispose() { }
	}
}
