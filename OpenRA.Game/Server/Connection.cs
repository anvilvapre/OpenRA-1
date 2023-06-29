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
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenRA.Network;

namespace OpenRA.Server
{
	public sealed class Connection : IDisposable
	{
		sealed class Receiver
		{
			enum ReceiveState { FrameSize, FrameData }

			readonly Socket socket;

			readonly int playerIndex;

			public Queue<Frame> Frames = new();

			public long LastReceivedTime = 0;

			readonly byte[] buffer = new byte[ServerMessage.SizeOfFrameBodySize + Frame.MaxSerializedSize
				+ 4 * (ServerMessage.SizeOfFrameBodySize + SyncFrame.SizeOfSyncFrame)];

			ReceiveState state = ReceiveState.FrameSize;

			int expectLength = ServerMessage.SizeOfFrameBodySize;

			int endPos;

			public Receiver(Socket socket, int playerIndex)
			{
				this.socket = socket;
				this.playerIndex = playerIndex;
			}

			/// <returns>`false` if the connection was closed before any data was read.</returns>
			bool ReceiveData()
			{
				var remainingSize = buffer.Length - endPos;
				if (remainingSize == 0 || remainingSize < expectLength)
					throw new InvalidOperationException($"Read buffer overflow {remainingSize} < {expectLength}");

				var bytesRead = socket.Receive(buffer, endPos, remainingSize, SocketFlags.None);
				if (bytesRead <= 0)
					return false;

				endPos += bytesRead;

				LastReceivedTime = Game.RunTime;
				return true;
			}

			/// <returns>`false` if the connection was closed before any data was read.</returns>
			public bool TryReceive()
			{
				// Wait up to 100ms for data to arrive before checking for data to send
				if (!socket.Poll(100000, SelectMode.SelectRead))
					return true;

				if (!ReceiveData())
					return false;

				return ParseBuffer();
			}

			bool ParseBuffer()
			{
				if (endPos < expectLength)
					return true;

				var startPos = 0;
				while (endPos - startPos >= expectLength)
				{
					switch (state)
					{
						case ReceiveState.FrameSize:
							{
								var frameSize = BitConverter.ToInt32(buffer, startPos);
								startPos += ServerMessage.SizeOfFrameBodySize;
								if (frameSize < 0 || frameSize > Frame.MaxSerializedSize)
								{
									Log.Write("server", $"Closing socket connection to {socket.RemoteEndPoint} because of invalid frame size: {frameSize}");
									return false;
								}

								expectLength = frameSize;
								state = ReceiveState.FrameData;
								break;
							}

						case ReceiveState.FrameData:
							{
								var frameData = buffer.AsSpan().Slice(startPos, expectLength);
								var frame = FrameIO.Parse(playerIndex, frameData);
								Frames.Enqueue(frame);

								startPos += expectLength;
								expectLength = ServerMessage.SizeOfFrameBodySize;
								state = ReceiveState.FrameSize;
								break;
							}
					}
				}

				// Upon return the buffer will always start at zero.
				if (startPos > 0 && endPos > 0)
				{
					// Move remaining partial frame left
					var partSize = endPos - startPos;
					if (partSize > 0)
						Array.Copy(buffer, startPos, buffer, 0, partSize);

					endPos = partSize;
				}

				return true;
			}
		}

		// Cap ping history at 15 seconds as a balance between expiring stale state and having enough data for decent statistics
		const int MaxPingSamples = 15;

		const int EstimatedMaxSendQueueSize = 8;

		/// <summary>Option to log all sent and recevied messages/frames.</summary>
		readonly TextWriter traceWriter = Console.Error;

		readonly bool tracePingFramesEnabled = false;

		public readonly int PlayerIndex;
		public readonly string AuthToken;
		public readonly EndPoint EndPoint;
		public readonly Stopwatch ConnectionTimer = Stopwatch.StartNew();

		public long TimeSinceLastResponse => Game.RunTime - receiver.LastReceivedTime;

		public bool Validated;
		public int LastOrdersFrame;
		public long TimeoutMessageShownTime;

		readonly Receiver receiver;

		readonly Stopwatch lastPingSent = Stopwatch.StartNew();

		readonly BlockingCollection<IServerMessage> sendQueue = new();
		readonly Queue<int> pingHistory = new();

		readonly Server server;

		readonly Socket socket;

		readonly IServerMessage[] sendDequeueBuffer = new IServerMessage[EstimatedMaxSendQueueSize];

		public Connection(Server server, Socket socket, string authToken)
		{
			this.server = server;
			this.socket = socket;
			PlayerIndex = server.ChooseFreePlayerIndex();
			receiver = new Receiver(socket, PlayerIndex);
			AuthToken = authToken;
			EndPoint = socket.RemoteEndPoint;

			new Thread(SendReceiveLoop)
			{
				// Use short name as Linux only display first 8 characters of thread name.
				Name = $"Client {EndPoint}",
				IsBackground = true
			}.Start();
		}

		void ProcessReceivedFrame(Server server, Frame frame)
		{
			// Ping packets are sent and processed internally within this thread to reduce
			// server-introduced latencies from polling loops
			if (frame.Type == OrderType.Ping)
			{
				if (pingHistory.Count == MaxPingSamples)
					pingHistory.Dequeue();

				var ping = (PingResponseFrame)frame;
				pingHistory.Enqueue((int)(Game.RunTime - ping.RequestRunTime));
				server.OnConnectionPing(this, pingHistory.ToArray(), ping.OrderQueueLength);
			}
			else
				server.OnConnectionFrame(this, frame);
		}

		bool TrySendPing()
		{
			// Transmits 17 bytes each second.
			if (lastPingSent.ElapsedMilliseconds <= 1000)
				return true;

			if (!TrySendMessage(new ServerMessage(new PingRequestFrame(Game.RunTime))))
				return false;

			lastPingSent.Restart();
			return true;
		}

		/// <returns>`false` if the connection was closed.</returns>
		bool ProcessSendQueue()
		{
			if (sendQueue.Count == 0)
				return true;

			// Client has been dropped by the server
			if (sendQueue.IsCompleted)
				return false;

			// Combine all frames and try to send all data in one socket write call.
			// To limit OS calls, NoDelay writes and to try to fit multiple frames into one TCP/IP packet.
			var dataSize = 0;
			var i = 0;
			while (i < sendDequeueBuffer.Length && sendQueue.TryTake(out var message, 0))
			{
				dataSize += message.SerializedSize;
				sendDequeueBuffer[i++] = message;
			}

			var ms = new MemoryStream(dataSize);
			var messageCount = i;
			for (i = 0; i < messageCount; i++)
			{
				var message = sendDequeueBuffer[i];
				if (traceWriter != null && (tracePingFramesEnabled || (message is ServerMessage m && m.Frame.Type != OrderType.Ping)))
					traceWriter.WriteLine($"Server send: {Game.RunTime:D6}, {message}.");

				message.CopyTo(ms);
			}

			var start = 0;
			var data = ms.GetBuffer();
			var length = data.Length;

			// Non-blocking sends are free to send only part of the data
			while (start < length)
			{
				var sent = socket.Send(data, start, length - start, SocketFlags.None, out var error);
				if (error == SocketError.WouldBlock)
				{
					Log.Write("server", $"Non-blocking send of {length - start} bytes failed. Falling back to blocking send.");
					socket.Blocking = true;
					sent = socket.Send(data, start, length - start, SocketFlags.None);
					socket.Blocking = false;
				}
				else if (error != SocketError.Success)
					throw new SocketException((int)error);

				start += sent;
			}

			return true;
		}

		void SendReceiveLoop()
		{
			try
			{
				socket.Blocking = false;
				socket.NoDelay = true;

				while (true)
				{
					var closed = !receiver.TryReceive();
					while (receiver.Frames.TryDequeue(out var frame))
					{
						if (traceWriter != null && (tracePingFramesEnabled || frame.Type != OrderType.Ping))
							traceWriter.WriteLine($"Server recv: {Game.RunTime:6D}, {frame}.");

						ProcessReceivedFrame(server, frame);
					}

					if (closed)
						return;

					if (!TrySendPing())
						return;

					// Send all data immediately, we will block again on read
					if (!ProcessSendQueue())
						return;
				}
			}
			catch (InvalidDataException e)
			{
				Log.Write("server", $"Closing socket connection to {EndPoint} because of receiving invalid data: {e}");
			}
			catch (SocketException e)
			{
				Log.Write("server", $"Closing socket connection to {EndPoint} because of socket error: {e}");
			}
			finally
			{
				sendQueue.CompleteAdding();
				server.OnConnectionDisconnect(this);
				socket.Dispose();
			}
		}

		public bool TrySendMessage(IServerMessage message)
		{
			if (sendQueue.IsAddingCompleted)
				return false;

			try
			{
				sendQueue.Add(message);
				return true;
			}
			catch (InvalidOperationException)
			{
				// Occurs if the collection is marked completed for adding by another thread.
				return false;
			}
		}

		public void Dispose()
		{
			// Tell the sendReceiveThread that the socket should be closed
			sendQueue.CompleteAdding();
		}
	}
}
