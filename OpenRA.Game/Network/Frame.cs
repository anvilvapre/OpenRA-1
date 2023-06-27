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
using System.IO;
using System.Text;

namespace OpenRA.Network
{
	public enum SenderType
	{
		Client,
		Server
	}

	public interface IFrame
	{
		int SerializedSize { get; }

		Stream CopyTo(Stream stream);

		byte[] Serialize()
		{
			var ms = new MemoryStream();
			CopyTo(ms);
			return ms.GetBuffer();
		}

		StringBuilder ToString(StringBuilder sb, bool close);

		string ToString()
		{
			var sb = new StringBuilder();
			return ToString(sb, true).ToString();
		}
	}

	/// <summary>Used as intermediate format to send the same frame to multiple clients.</summary>
	public sealed class SerializedFrame
	{
		public int SerializedSize => Data.Length;
		public readonly byte[] Data;

		public SerializedFrame(IFrame source)
			: this(source.Serialize())
		{
		}

		public SerializedFrame(byte[] data)
		{
			Data = data;
		}

		public Stream CopyTo(Stream stream)
		{
			stream.WriteArray(Data);
			return stream;
		}

		public StringBuilder ToString(StringBuilder sb, bool close)
		{
			return sb.Append($"SerializedFrame [SerializedSize={Data.Length}]");
		}
	}

	public abstract class Frame : IFrame
	{
		public const int SizeOfId = sizeof(int);
		public const int MaxSize = 666;

		public const int IdImmediateOrder = 0;

		public int SerializedSize => SizeOfId + BodySize;

		public readonly OrderType Type;

		public readonly int Id;
		public readonly int BodySize;

		protected Frame(OrderType type, int id, int bodySize)
		{
			Type = type;
			Id = id;
			BodySize = bodySize;
		}

		public abstract Stream CopyTo(Stream stream);
		protected Stream WriteId(Stream stream)
		{
			stream.Write(BitConverter.GetBytes(Id));
			return stream;
		}

		public StringBuilder ToString(StringBuilder sb, bool close)
		{
			sb.Append($"Frame [Id={Id}, BodySize={BodySize}, Type={Type}");
			if (close)
				sb.Append(']');

			return sb;
		}
	}

	public class OrderFrame : Frame
	{
		public const int SizeOfOrderType = sizeof(byte);
		public const OrderType DefaultOrderType = OrderType.Fields;

		public readonly byte[] Data;

		public OrderFrame(int id, byte[] data)
			: base(data.Length > 0 ? (OrderType)data[0] : DefaultOrderType, id, SizeOfOrderType + data.Length)
		{
			Data = data;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteArray(Data);
			return stream;
		}
	}

	/// <summary>Frame type sent only by a client to the server.</summary>
	public abstract class ClientFrame : Frame
	{
		protected ClientFrame(OrderType type, int id, int bodySize)
			: base(type, id, bodySize) { }
	}

	/// <summary>Frame type sent only by the server to clients.</summary>
	public abstract class ServerFrame : Frame
	{
		protected ServerFrame(OrderType type, int id, int bodySize)
			: base(type, id, bodySize) { }
	}

	public sealed class HandshakeFrame : OrderFrame
	{
		public HandshakeFrame(byte[] body)
			: base(IdImmediateOrder, body)
		{
			if (Type != OrderType.Handshake)
				throw new ArgumentException($"nameof(OrderType) of type {OrderType.Handshake} expected.");
		}
	}

	/// <summary>Sent by both client and servers.</summary>
	public sealed class SyncFrame : Frame
	{
		public const int SizeOfSyncFrame = SizeOfId + SizeOfBody;
		public const int SizeOfBody = sizeof(byte) + sizeof(int) + sizeof(ulong);

		public readonly int SyncHash;
		public readonly ulong DefeatState;

		public SyncFrame(int id, int syncHash, ulong defeatState)
			: base(OrderType.SyncHash, id, SizeOfBody)
		{
			SyncHash = syncHash;
			DefeatState = defeatState;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteByte((byte)OrderType.SyncHash);
			stream.WriteArray(BitConverter.GetBytes(SyncHash));
			stream.WriteArray(BitConverter.GetBytes(DefeatState));
			return stream;
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
				.Append($", {nameof(SyncHash)}=0x{SyncHash:X}, {nameof(DefeatState)}=0x{DefeatState:X} ]")
				.ToString();
		}
	}

	public sealed class PingResponseFrame : ClientFrame
	{
		public const int SizeOfBody = sizeof(byte) + sizeof(long) + sizeof(byte);

		public readonly long RequestRunTime;

		public readonly byte OrderQueueLength;

		public PingResponseFrame(long requestRunTime, byte orderQueueLength)
			: base(OrderType.Ping, IdImmediateOrder, SizeOfBody)
		{
			RequestRunTime = requestRunTime;
			OrderQueueLength = orderQueueLength;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteByte((byte)OrderType.Ping);
			stream.WriteArray(BitConverter.GetBytes(RequestRunTime));
			stream.WriteByte(OrderQueueLength);
			return stream;
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
			.Append($", {nameof(RequestRunTime)}={RequestRunTime}, {nameof(OrderQueueLength)}={OrderQueueLength} ]")
			.ToString();
		}
	}

	public sealed class AckFrame : ServerFrame
	{
		public const int SizeOfBody = sizeof(byte) + sizeof(byte);

		public readonly byte FrameCount;
		public AckFrame(int frame, byte frameCount)
			: base(OrderType.Ack, frame, SizeOfBody)
		{
			FrameCount = frameCount;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteByte((byte)OrderType.Ack);
			stream.WriteArray(BitConverter.GetBytes(FrameCount));
			return stream;
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
				.Append($", {nameof(FrameCount)}={FrameCount} ]")
				.ToString();
		}
	}

	public sealed class PingRequestFrame : ServerFrame
	{
		public const int SizeOfBody = sizeof(byte) + SizeOfRunTime;

		public const int SizeOfRunTime = sizeof(long);

		public readonly long RunTime;

		public PingRequestFrame(long runTime)
			: base(OrderType.Ping, IdImmediateOrder, SizeOfBody)
		{
			RunTime = runTime;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteByte((byte)OrderType.Ping);
			stream.WriteArray(BitConverter.GetBytes(RunTime));
			return stream;
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
			.Append($", {nameof(RunTime)}={RunTime} ]")
			.ToString();
		}
	}

	public sealed class TickScaleFrame : ServerFrame
	{
		public const int SizeOfBody = sizeof(byte) + sizeof(float);

		public readonly float TickScale;

		public TickScaleFrame(float tickScale)
			: base(OrderType.TickScale, IdImmediateOrder, SizeOfBody)
		{
			TickScale = tickScale;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteByte((byte)OrderType.TickScale);
			stream.Write(TickScale);
			return stream;
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
				.Append($", {nameof(TickScale)}={TickScale} ]")
				.ToString();
		}
	}

	public sealed class DisconnectFrame : ServerFrame
	{
		public const int SizeOfBody = sizeof(byte) + sizeof(int);

		public readonly int DisconnectClientId;

		public DisconnectFrame(int id, int disconnectClientId)
			: base(OrderType.Disconnect, id, SizeOfBody)
		{
			DisconnectClientId = disconnectClientId;
		}

		public override Stream CopyTo(Stream stream)
		{
			WriteId(stream);
			stream.WriteByte((byte)OrderType.Disconnect);
			stream.WriteArray(BitConverter.GetBytes(DisconnectClientId));
			return stream;
		}
	}

	/// <summary>Initial response by server after accepting a new connection.</summary>
	public sealed class ProtocolHandshakeFrame : IFrame
	{
		public const int SizeOfFrame = sizeof(int) + sizeof(int);

		public readonly int ProtocolVersion;

		public int SerializedSize => SizeOfFrame;

		/// <summary>
		/// As assigned by the server to the client. Matches the player index.
		/// Used as `target` field in frames forwarded by the server to clients.
		/// </summary>
		public readonly int ClientId;

		public ProtocolHandshakeFrame(int clientId)
		{
			ProtocolVersion = Server.ProtocolVersion.Handshake;
			ClientId = clientId;
		}

		public Stream CopyTo(Stream stream)
		{
			stream.WriteArray(BitConverter.GetBytes(ProtocolVersion));
			stream.WriteArray(BitConverter.GetBytes(ClientId));
			return stream;
		}

		public StringBuilder ToString(StringBuilder sb, bool close)
		{
			sb.Append($"{nameof(ProtocolHandshakeFrame)} [{nameof(ProtocolVersion)}={ProtocolVersion}, {nameof(ClientId)}={ClientId}]");
			return sb;
		}
	}
}
