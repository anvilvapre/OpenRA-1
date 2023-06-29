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

		void CopyTo(Stream stream);

		byte[] Serialize()
		{
			var ms = new MemoryStream(SerializedSize);
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

	public abstract class Frame : IFrame
	{
		public const int SizeOfOrderType = sizeof(byte);

		public const int SizeOfId = sizeof(int);
		public const int MaxBodySize = 131072;

		public const int MaxSerializedSize = SizeOfId + MaxBodySize;

		public const int IdImmediateOrServerOrder = 0;

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

		public abstract void CopyTo(Stream stream, bool bodyOnly);

		public void CopyTo(Stream stream)
		{
			CopyTo(stream, false);
		}

		protected Stream WriteId(Stream stream)
		{
			stream.Write(BitConverter.GetBytes(Id));
			return stream;
		}

		public StringBuilder ToString(StringBuilder sb, bool close)
		{
			sb.Append($"Frame [SerializedSize={SerializedSize}, Id={Id}, BodySize={BodySize}, Type={Type}");
			if (close)
				sb.Append(']');

			return sb;
		}

		public override string ToString()
		{
			var sb = new StringBuilder(128);
			ToString(sb, true);
			return sb.ToString();
		}
	}

	public class OrderFrame : Frame
	{
		public const OrderType DefaultOrderType = OrderType.Fields;

		public readonly byte[] Data;

		public OrderFrame(int id, byte[] data)
			: base(data.Length > 0 ? (OrderType)data[0] : DefaultOrderType, id, data.Length)
		{
			Data = data;
		}

		public static OrderFrame CreateImmediate(byte[] data)
		{
			return new OrderFrame(IdImmediateOrServerOrder, data);
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteArray(Data);
		}
	}

	public interface ISentByClient { }

	public interface ISentByServer { }

	public sealed class HandshakeFrame : OrderFrame
	{
		public HandshakeFrame(byte[] body)
			: base(IdImmediateOrServerOrder, body)
		{
			if (Type != OrderType.Handshake)
				throw new ArgumentException($"nameof(OrderType) of type {OrderType.Handshake} expected.");
		}
	}

	/// <summary>Sent by both client and servers.</summary>
	public sealed class SyncFrame : Frame
	{
		public const int SizeOfSyncFrame = SizeOfId + SizeOfBody;
		public const int SizeOfBody = SizeOfOrderType + sizeof(int) + sizeof(ulong);

		public readonly int SyncHash;
		public readonly ulong DefeatState;

		public SyncFrame(int id, int syncHash, ulong defeatState)
			: base(OrderType.SyncHash, id, SizeOfBody)
		{
			SyncHash = syncHash;
			DefeatState = defeatState;
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteByte((byte)OrderType.SyncHash);
			stream.WriteArray(BitConverter.GetBytes(SyncHash));
			stream.WriteArray(BitConverter.GetBytes(DefeatState));
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
				.Append($", {nameof(SyncHash)}=0x{SyncHash:X}, {nameof(DefeatState)}=0x{DefeatState:X} ]")
				.ToString();
		}
	}

	public sealed class PingResponseFrame : Frame, ISentByClient
	{
		public const int SizeOfBody = sizeof(byte) + sizeof(long) + sizeof(byte);

		public readonly long RequestRunTime;

		public readonly byte OrderQueueLength;

		public PingResponseFrame(long requestRunTime, byte orderQueueLength)
			: base(OrderType.Ping, IdImmediateOrServerOrder, SizeOfBody)
		{
			RequestRunTime = requestRunTime;
			OrderQueueLength = orderQueueLength;
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteByte((byte)OrderType.Ping);
			stream.WriteArray(BitConverter.GetBytes(RequestRunTime));
			stream.WriteByte(OrderQueueLength);
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
			.Append($", {nameof(RequestRunTime)}={RequestRunTime}, {nameof(OrderQueueLength)}={OrderQueueLength} ]")
			.ToString();
		}
	}

	public sealed class AckFrame : Frame, ISentByServer
	{
		public const int SizeOfBody = SizeOfOrderType + sizeof(byte);

		public readonly byte FrameCount;
		public AckFrame(int frame, byte frameCount)
			: base(OrderType.Ack, frame, SizeOfBody)
		{
			FrameCount = frameCount;
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteByte((byte)OrderType.Ack);
			stream.WriteByte(FrameCount);
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
				.Append($", {nameof(FrameCount)}={FrameCount} ]")
				.ToString();
		}
	}

	public sealed class PingRequestFrame : Frame, ISentByServer
	{
		public const int SizeOfBody = SizeOfOrderType + SizeOfRunTime;

		public const int SizeOfRunTime = sizeof(long);

		public readonly long RunTime;

		public PingRequestFrame(long runTime)
			: base(OrderType.Ping, IdImmediateOrServerOrder, SizeOfBody)
		{
			RunTime = runTime;
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteByte((byte)OrderType.Ping);
			stream.WriteArray(BitConverter.GetBytes(RunTime));
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
			.Append($", {nameof(RunTime)}={RunTime} ]")
			.ToString();
		}
	}

	public sealed class TickScaleFrame : Frame, ISentByServer
	{
		public const int SizeOfBody = SizeOfOrderType + sizeof(float);

		public readonly float TickScale;

		public TickScaleFrame(float tickScale)
			: base(OrderType.TickScale, IdImmediateOrServerOrder, SizeOfBody)
		{
			TickScale = tickScale;
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteByte((byte)OrderType.TickScale);
			stream.Write(TickScale);
		}

		public override string ToString()
		{
			return ToString(new StringBuilder(120), false)
				.Append($", {nameof(TickScale)}={TickScale} ]")
				.ToString();
		}
	}

	public sealed class DisconnectFrame : Frame, ISentByServer
	{
		public const int SizeOfBody = SizeOfOrderType + sizeof(int);

		public readonly int DisconnectClientId;

		public DisconnectFrame(int id, int disconnectClientId)
			: base(OrderType.Disconnect, id, SizeOfBody)
		{
			DisconnectClientId = disconnectClientId;
		}

		public override void CopyTo(Stream stream, bool bodyOnly)
		{
			if (!bodyOnly)
				WriteId(stream);

			stream.WriteByte((byte)OrderType.Disconnect);
			stream.WriteArray(BitConverter.GetBytes(DisconnectClientId));
		}
	}

	public interface IServerMessage
	{
		public int SerializedSize { get; }

		void CopyTo(Stream stream);

		public byte[] Serialize()
		{
			var ms = new MemoryStream(SerializedSize);
			CopyTo(ms);
			return ms.ToArray();
		}
	}

	/// <summary>Used as intermediate format to send the same frame to multiple clients.</summary>
	public sealed class SerializedMessage : IServerMessage
	{
		public readonly byte[] Data;

		public int SerializedSize => Data.Length;

		public SerializedMessage(ServerMessage source)
		{
			Data = ((IServerMessage)source).Serialize();
		}

		public void CopyTo(Stream stream)
		{
			stream.WriteArray(Data);
		}
	}

	/// <summary>Initial response by server after accepting a new connection.</summary>
	public sealed class ProtocolHandshakeMessage : IServerMessage
	{
		public const int SizeOfFrame = sizeof(int) + sizeof(int);

		public readonly int ProtocolVersion;

		public int SerializedSize => SizeOfFrame;

		/// <summary>
		/// As assigned by the server to the client. Matches the player index of the connection.
		/// Used as `target` field in frames forwarded by the server to clients.
		/// </summary>
		public readonly int ClientId;

		public ProtocolHandshakeMessage(int clientId)
		{
			ProtocolVersion = Server.ProtocolVersion.Handshake;
			ClientId = clientId;
		}

		public void CopyTo(Stream stream)
		{
			stream.WriteArray(BitConverter.GetBytes(ProtocolVersion));
			stream.WriteArray(BitConverter.GetBytes(ClientId));
		}

		public StringBuilder ToString(StringBuilder sb, bool close)
		{
			sb.Append($"{nameof(ProtocolHandshakeMessage)} [{nameof(ProtocolVersion)}={ProtocolVersion}, {nameof(ClientId)}={ClientId}]");
			return sb;
		}
	}

	/// <summary>Only the server sends the `Source` field to clients to indicate the origin of the frame. The value is a player index of a connection.</summary>
	public sealed class ServerMessage : IServerMessage
	{
		public const int SizeOfSizePrefix = sizeof(int);
		public const int SizeOfSource = sizeof(int);

		public const int SourceServer = 0;

		/// <summary>The sender. The player index of the connection the frame originated from.</summary>
		public readonly int Source;
		public readonly Frame Frame;

		public int SerializedSize => SizeOfSizePrefix + SizeOfSource + Frame.SerializedSize;

		public ServerMessage(int source, Frame frame)
		{
			Source = source;
			Frame = frame;
		}

		public ServerMessage(Frame frame)
			: this(SourceServer, frame) { }

		public void CopyTo(Stream stream)
		{
			// The frame size includes the frame id field, yet excludes the source field.
			stream.WriteArray(BitConverter.GetBytes(Frame.SerializedSize));
			stream.WriteArray(BitConverter.GetBytes(Source));
			Frame.CopyTo(stream);
			Console.WriteLine($"==== {Frame.SerializedSize} {Frame}");
		}
	}
}
