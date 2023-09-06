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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenRA.Server;

namespace OpenRA.Network
{
	public enum ConnectionState
	{
		PreConnecting,
		NotConnected,
		Connecting,
		Connected,
	}

	public interface IConnection : IDisposable
	{
		int LocalClientId { get; }
		void StartGame();
		void Send(int frame, IEnumerable<Order> orders);
		void SendImmediate(IEnumerable<Order> orders);
		void SendSync(SyncFrame frame);
		void Receive(OrderManager orderManager);
	}

	public sealed class EchoConnection : IConnection
	{
		const int LocalClientId = 1;
		readonly Queue<SyncFrame> sync = new();
		readonly Queue<(int Frame, OrderPacket Orders)> orders = new();
		readonly Queue<OrderPacket> immediateOrders = new();
		bool disposed;

		int IConnection.LocalClientId => LocalClientId;

		void IConnection.StartGame()
		{
			// Inject an empty frame to fill the gap we are making by projecting forward orders
			orders.Enqueue((0, new OrderPacket(Array.Empty<Order>())));
		}

		void IConnection.Send(int frame, IEnumerable<Order> o)
		{
			orders.Enqueue((frame, new OrderPacket(o)));
		}

		void IConnection.SendImmediate(IEnumerable<Order> o)
		{
			immediateOrders.Enqueue(new OrderPacket(o));
		}

		void IConnection.SendSync(SyncFrame frame)
		{
			sync.Enqueue(frame);
		}

		void IConnection.Receive(OrderManager orderManager)
		{
			while (immediateOrders.TryDequeue(out var i))
			{
				orderManager.ReceiveImmediateOrders(LocalClientId, i);

				// An immediate order may trigger a chain of actions that disposes the OrderManager and connection.
				// Bail out to avoid potential problems from acting on disposed objects.
				if (disposed)
					break;
			}

			// Project orders forward to the next frame
			while (orders.TryDequeue(out var o))
				orderManager.ReceiveOrders(LocalClientId, (o.Frame + 1, o.Orders));

			while (sync.TryDequeue(out var s))
				orderManager.ReceiveSync(s);
		}

		void IDisposable.Dispose()
		{
			disposed = true;
		}
	}

	public sealed class NetworkConnection : IConnection
	{
		public readonly ConnectionTarget Target;
		internal ReplayRecorder Recorder { get; private set; }
		readonly Queue<SyncFrame> sentSync = new();
		readonly Queue<SyncFrame> queuedSyncFrames = new();

		readonly Queue<(int Frame, OrderPacket Orders)> sentOrders = new();
		readonly Queue<OrderPacket> sentImmediateOrders = new();
		readonly ConcurrentQueue<(int FromClient, Frame Frame)> receivedPackets = new();
		TcpClient tcp;
		volatile ConnectionState connectionState = ConnectionState.Connecting;
		volatile int clientId;
		bool disposed;

		public NetworkConnection(ConnectionTarget target)
		{
			Target = target;
			new Thread(NetworkConnectionConnect)
			{
				Name = $"{GetType().Name} (connect to {target})",
				IsBackground = true
			}.Start();
		}

		public ConnectionState ConnectionState => connectionState;

		public IPEndPoint EndPoint { get; private set; }

		public string ErrorMessage { get; private set; }

		void NetworkConnectionConnect()
		{
			var queue = new BlockingCollection<TcpClient>();

			var atLeastOneEndpoint = false;
			foreach (var endpoint in Target.GetConnectEndPoints())
			{
				atLeastOneEndpoint = true;
				new Thread(() =>
				{
					try
					{
						var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
						client.Connect(endpoint.Address, endpoint.Port);

						try
						{
							queue.Add(client);
						}
						catch (InvalidOperationException)
						{
							// Another connection was faster, close this one.
							client.Close();
						}
					}
					catch (Exception ex)
					{
						ErrorMessage = "Failed to connect";
						Log.Write("client", $"Failed to connect to {endpoint}: {ex.Message}");
					}
				})
				{
					Name = $"{GetType().Name} (connect to {endpoint})",
					IsBackground = true
				}.Start();
			}

			if (!atLeastOneEndpoint)
			{
				ErrorMessage = "Failed to resolve address";
				connectionState = ConnectionState.NotConnected;
			}

			// Wait up to 5s for a successful connection. This should hopefully be enough because such high latency makes the game unplayable anyway.
			else if (queue.TryTake(out tcp, 5000))
			{
				// Copy endpoint here to have it even after getting disconnected.
				EndPoint = (IPEndPoint)tcp.Client.RemoteEndPoint;

				new Thread(NetworkConnectionReceive)
				{
					Name = $"{GetType().Name} (receive from {tcp.Client.RemoteEndPoint})",
					IsBackground = true
				}.Start();
			}
			else
			{
				connectionState = ConnectionState.NotConnected;
			}

			// Close all unneeded connections in the queue and make sure new ones are closed on the connect thread.
			queue.CompleteAdding();
			foreach (var client in queue)
				client.Close();
		}

		void NetworkConnectionReceive()
		{
			try
			{
				var stream = tcp.GetStream();
				var handshakeProtocol = stream.ReadInt32();

				if (handshakeProtocol != ProtocolVersion.Handshake)
					throw new InvalidOperationException($"Handshake protocol version mismatch. Server={handshakeProtocol} Client={ProtocolVersion.Handshake}");

				clientId = stream.ReadInt32();
				connectionState = ConnectionState.Connected;

				while (true)
				{
					var len = stream.ReadInt32();
					var client = stream.ReadInt32();
					var buf = stream.ReadBytes(len);
					if (len == 0)
						throw new NotImplementedException();

					var frame = FrameIO.Parse(client, buf);
					receivedPackets.Enqueue((client, frame));
				}
			}
			catch (Exception ex)
			{
				ErrorMessage = "Connection failed";
				Log.Write("client", $"Connection to {EndPoint} failed: {ex.Message}");
			}
			finally
			{
				connectionState = ConnectionState.NotConnected;
			}
		}

		int IConnection.LocalClientId => clientId;

		void IConnection.StartGame() { }

		void IConnection.Send(int frame, IEnumerable<Order> orders)
		{
			var o = new OrderPacket(orders);
			sentOrders.Enqueue((frame, o));
			Send(o.ToFrame(frame));
		}

		void IConnection.SendImmediate(IEnumerable<Order> orders)
		{
			var o = new OrderPacket(orders);
			sentImmediateOrders.Enqueue(o);
			Send(o.ToFrame(0));
		}

		void IConnection.SendSync(SyncFrame frame)
		{
			// Send sync packets together with the next set of orders.
			// This was originally explained as reducing network bandwidth
			// (TCP overhead?), but the original discussions have been lost to time.
			// Add the sync packets to the send queue before adding them to the local sync queue in the Send() method.
			// Otherwise the client will process the local sync queue before sending the packet.
			queuedSyncFrames.Enqueue(frame);
		}

		void Send(Frame frame)
		{
			try
			{
				var ms = new MemoryStream(frame.SerializedSize + queuedSyncFrames.Count * SyncFrame.SizeOfSyncFrame);
				frame.CopyTo(ms);

				foreach (var syncFrame in queuedSyncFrames)
				{
					syncFrame.CopyTo(ms);
					sentSync.Enqueue(syncFrame);
				}

				queuedSyncFrames.Clear();
				ms.WriteTo(tcp.GetStream());
			}
			catch (SocketException) { /* drop this on the floor; we'll pick up the disconnect from the reader thread */ }
			catch (ObjectDisposedException) { /* ditto */ }
			catch (InvalidOperationException) { /* ditto */ }
			catch (IOException) { /* ditto */ }
		}

		void ProcessPingRequest(PingRequestFrame frame, OrderManager orderManager)
		{
			// Note that processing this here, rather than in NetworkConnectionReceive,
			// so that poor world tick performance can be reflected in the latency measurement
			Send(new PingResponseFrame(frame.RunTime, (byte)orderManager.OrderQueueLength));
		}

		OrderFrame ProcessAck(AckFrame frame)
		{
			if (frame.FrameCount > sentOrders.Count)
				throw new InvalidOperationException($"Received Ack for {frame.FrameCount} > {sentOrders.Count} frames. {frame}.");

			// The Acknowledgement packet is a placeholder that tells us to process the first packet in our
			// local sent buffer and the frame at which it should be applied. This is an optimization to avoid having
			// to send the (much larger than 5 byte) packet back to us over the network.
			OrderPacket orderData;
			if (frame.FrameCount != 1)
			{
				var orders = Enumerable.Range(0, frame.FrameCount)
					.Select(i => sentOrders.Dequeue().Orders);
				orderData = OrderPacket.Combine(orders);
			}
			else
				orderData = sentOrders.Dequeue().Orders;

			return orderData.ToFrame(frame.Id);
		}

		public static void ProcessOrderData(int fromClient, OrderFrame frame, OrderManager orderManager)
		{
			// Frames are often empty.
			var orderData = frame.BodySize == 0 ? OrderPacket.Empty : new OrderPacket(frame);
			if (frame.Id == 0)
				orderManager.ReceiveImmediateOrders(fromClient, orderData);
			else
				orderManager.ReceiveOrders(fromClient, (frame.Id, orderData));
		}

		void IConnection.Receive(OrderManager orderManager)
		{
			// Locally generated orders
			while (sentImmediateOrders.TryDequeue(out var i))
			{
				orderManager.ReceiveImmediateOrders(clientId, i);
				Recorder?.Receive(clientId, i.ToImmediateFrame());

				// An immediate order may trigger a chain of actions that disposes the OrderManager and connection.
				// Bail out to avoid potential problems from acting on disposed objects.
				if (disposed)
					return;
			}

			while (sentSync.TryDequeue(out var s))
			{
				orderManager.ReceiveSync(s);
				Recorder?.Receive(clientId, ((IFrame)s).Serialize());
			}

			// Orders from other players
			while (receivedPackets.TryDequeue(out var d))
			{
				var frame = d.Frame;
				var frameToRecord = frame;
				switch (d.Frame.Type)
				{
					case OrderType.None:
						// Pass
						break;
					case OrderType.Ack:
						frameToRecord = ProcessAck((AckFrame)frame);
						break;
					case OrderType.Ping:
						ProcessPingRequest((PingRequestFrame)frame, orderManager);
						break;
					case OrderType.SyncHash:
						orderManager.ReceiveSync((SyncFrame)frame);
						break;
					case OrderType.TickScale:
						orderManager.ReceiveTickScale((TickScaleFrame)frame);
						break;
					case OrderType.Disconnect:
						orderManager.ReceiveDisconnect((DisconnectFrame)frame);
						break;
					case OrderType.Handshake:
						break;
					case OrderType.Fields:
						ProcessOrderData(d.FromClient, (OrderFrame)frame, orderManager);
						break;
					default:
						// FrameIO should already have caught this earlier during deserialization.
						throw new InvalidDataException($"Unsupported frame {nameof(d.Frame.Type)} {d.Frame.Type}. {d.Frame}.");
				}

				Recorder?.Receive(clientId, ((IFrame)frameToRecord).Serialize());

				// An immediate order may trigger a chain of actions that disposes the OrderManager and connection.
				// Bail out to avoid potential problems from acting on disposed objects.
				if (disposed)
					return;
			}
		}

		public void StartRecording(Func<string> chooseFilename)
		{
			// If we have a previous recording then save/dispose it and start a new one.
			Recorder?.Dispose();
			Recorder = new ReplayRecorder(chooseFilename);
		}

		void IDisposable.Dispose()
		{
			if (disposed)
				return;

			disposed = true;

			// Closing the stream will cause any reads on the receiving thread to throw.
			// This will mark the connection as no longer connected and the thread will terminate cleanly.
			tcp?.Close();

			Recorder?.Dispose();
		}
	}
}
