#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.FileFormats;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Server
{
	public class SocketTimeoutable : IDisposable
	{
		public const int DefaultConnectAsyncWaitTimeoutMillis = 10000;
		public const int DefaultReceiveAsyncWaitTimeoutMillis = 10000;
		public const int DefaultSendAsyncWaitTimeoutMillis = 10000;
		public const int DefaultDisconnectAsyncWaitTimeoutMillis = 10000;

		public readonly Socket Socket;
		public int ConnectAsyncWaitTimeoutMillis;
		public int ReceiveAsyncWaitTimeoutMillis;
		public int SendAsyncWaitTimeoutMillis;
		public int DisconnectAsyncWaitTimeoutMillis;
		public bool IsConnecting { get; private set; }
		public EndPoint RemoteEndPoint => Socket.RemoteEndPoint;
		public bool Blocking { get => Socket.Blocking; set => Socket.Blocking = value; }
		public bool NoDelay { get => Socket.NoDelay; set => Socket.NoDelay = value; }

		public SocketTimeoutable(Socket socket)
		{
			ConnectAsyncWaitTimeoutMillis = DefaultConnectAsyncWaitTimeoutMillis;
			ReceiveAsyncWaitTimeoutMillis = DefaultReceiveAsyncWaitTimeoutMillis;
			SendAsyncWaitTimeoutMillis = DefaultSendAsyncWaitTimeoutMillis;

			if (socket == null)
				throw new ArgumentNullException(nameof(socket));

			Socket = socket;
		}

		public static bool operator ==(SocketTimeoutable me, SocketTimeoutable other) { return me.Socket == other.Socket; }
		public static bool operator !=(SocketTimeoutable me, SocketTimeoutable other) { return !(me == other); }
		public override int GetHashCode() { return Socket.GetHashCode(); }

		public bool Equals(SocketTimeoutable other) { return Socket == other.Socket; }
		public override bool Equals(object obj) { return obj is SocketTimeoutable && Equals((SocketTimeoutable)obj); }

		public override string ToString() { return Socket.ToString(); }

		public void Dispose()
		{
			Socket.Dispose();
		}

		public void Connect(EndPoint remoteEP)
		{
			ConnectAsyncWaitTimeoutMillis = DefaultConnectAsyncWaitTimeoutMillis;
			if (Socket.Connected)
				Socket.Close();

			AwaitConnect(Socket.BeginConnect(remoteEP, null, null));
		}

		public void Connect(IPAddress address, int port)
		{
			ConnectAsyncWaitTimeoutMillis = DefaultConnectAsyncWaitTimeoutMillis;
			if (Socket.Connected)
				Socket.Close();

			AwaitConnect(Socket.BeginConnect(address, port, null, null));
		}

		public void Connect(string address, int port)
		{
			ConnectAsyncWaitTimeoutMillis = DefaultConnectAsyncWaitTimeoutMillis;
			if (Socket.Connected)
				Socket.Close();

			AwaitConnect(Socket.BeginConnect(address, port, null, null));
		}

		void AwaitConnect(IAsyncResult result)
		{
			var signalled = result.AsyncWaitHandle.WaitOne(ConnectAsyncWaitTimeoutMillis, true);
			if (signalled && Socket.Connected)
			{
				Socket.EndConnect(result);
			}
			else
			{
				CloseSocketEnsureInvokeEnd();
				throw new SocketException((int)SocketError.TimedOut);
			}
		}

		public bool Poll(int microSeconds, SelectMode mode)
		{
			return Socket.Poll(microSeconds, mode);
		}

		public int Receive(byte[] buffer)
		{
			var result = Socket.BeginReceive(buffer,
				0,
				buffer.Length,
				SocketFlags.None,
				null,
				null);

			var signalled = result.AsyncWaitHandle.WaitOne(ReceiveAsyncWaitTimeoutMillis, true);
			if (signalled)
			{
				var bytesRead = Socket.EndReceive(result);
				return bytesRead;
			}
			else
			{
				CloseSocketEnsureInvokeEnd();
				throw new SocketException((int)SocketError.TimedOut);
			}
		}

		public int Send(byte[] buffer, int offset, int size, SocketFlags socketFlags, out SocketError errorCode)
		{
			var result = Socket.BeginSend(buffer,
				offset,
				size,
				socketFlags,
				out errorCode,
				null,
				null);

			var signalled = result.AsyncWaitHandle.WaitOne(SendAsyncWaitTimeoutMillis, true);
			if (signalled)
			{
				var bytesSent = Socket.EndSend(result);
				return bytesSent;
			}
			else
			{
				CloseSocketEnsureInvokeEnd();
				throw new SocketException((int)SocketError.TimedOut);
			}
		}

		public int Send(byte[] buffer, int offset, int size, SocketFlags socketFlags)
		{
			var result = Socket.BeginSend(buffer,
				offset,
				size,
				socketFlags,
				null,
				null);

			var signalled = result.AsyncWaitHandle.WaitOne(SendAsyncWaitTimeoutMillis, true);
			if (signalled)
			{
				var bytesSent = Socket.EndSend(result);
				return bytesSent;
			}
			else
			{
				CloseSocketEnsureInvokeEnd();
				throw new SocketException((int)SocketError.TimedOut);
			}
		}

		public void Disconnect(bool reuseSocket)
		{
			Socket.Disconnect(reuseSocket);
			var result = Socket.BeginDisconnect(reuseSocket, null, null);
			var signalled = result.AsyncWaitHandle.WaitOne(DisconnectAsyncWaitTimeoutMillis, true);
			if (signalled)
			{
				Socket.EndDisconnect(result);
			}
			else
			{
				CloseSocketEnsureInvokeEnd();
				throw new SocketException((int)SocketError.TimedOut);
			}
		}

		void CloseSocketEnsureInvokeEnd()
		{
			try
			{
				// A async BeginInvoke method shound always end
				// with a EndInvoke method or closing of the socket
				// to avoid pending tasks to be incompleted.
				if (Socket.Connected)
					Socket.Close();
			}
			catch (Exception)
			{
			}
		}

		public void Close()
		{
			Socket.Close();
		}
	}
}
