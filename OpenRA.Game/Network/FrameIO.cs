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
using System.Runtime.InteropServices;
using System.Text;

namespace OpenRA.Network
{
	public static class FrameIO
	{
		static class FrameParser
		{
			static InvalidDataException CreateParseException(int conn, int size, int id, string message)
			{
				var sb = new StringBuilder(64);
				sb.Append("Error parsing frame data. Invalid data format. ")
					.Append(message ?? "")
					 .Append($"[Connection=${conn}, Size={size}");

				if (id != int.MinValue)
					sb.Append($", {nameof(Frame.Id)}={id}");

				sb.Append(']');
				return new InvalidDataException(sb.ToString());
			}

			static InvalidDataException CreateValueException(int conn, int size, int id, string name, string value)
			{
				return CreateParseException(conn, size, id, $"Invalid value \"{value}\" for field {name}.");
			}

			public static OrderFrame Parse(int incomingConn, Span<byte> body)
			{
				var id = int.MinValue;
				var size = body.Length;
				if (size < Frame.SizeOfId || size > Frame.MaxSize)
					throw CreateValueException(incomingConn, size, id, "Size", $"{size}");

				var offset = 0;
				id = MemoryMarshal.Read<int>(body.Slice(offset, Frame.SizeOfId));
				if (id < 0)
					throw CreateValueException(incomingConn, size, id, nameof(Frame.Id), $"{id}");

				// Order data is often empty
				var bodySize = size - Frame.SizeOfId;
				var orderData = bodySize > 0 ? body.Slice(Frame.SizeOfId, bodySize).ToArray() : Array.Empty<byte>();
				return new OrderFrame(id, orderData);
			}
		}

		static class BodyParser
		{
			static InvalidDataException CreateParseException(OrderFrame source, OrderType type, string message)
			{
				var sb = new StringBuilder(64);
				sb.Append("Error parsing frame body. Invalid data format. ");
				sb.Append(message ?? "")
					.Append($" [Frame={source}, {nameof(Frame.Type)}={type}");

				sb.Append(']');
				return new InvalidDataException(sb.ToString());
			}

			static InvalidDataException CreateInvalidValueException(OrderFrame source, OrderType type, string field, string value)
			{
				return CreateParseException(source, type, $" Invalid value \"{value}\" for field {field}.");
			}

			static void ValidateBodySize(OrderFrame source, OrderType type, int bodySize)
			{
				if (source.BodySize != bodySize)
					throw CreateInvalidValueException(source, type, nameof(Frame.BodySize), $"{source.BodySize}");
			}

			static void ValidateId(OrderFrame source, OrderType type, int requiredId)
			{
				if (source.Id != requiredId)
					throw CreateInvalidValueException(source, type, nameof(Frame.Id), $"{source.Id}");
			}

			static AckFrame ParseAck(OrderFrame source)
			{
				ValidateBodySize(source, OrderType.Ack, AckFrame.SizeOfBody);

				var body = source.Data.AsSpan();
				var frameCount = body[OrderFrame.SizeOfOrderType];
				return new AckFrame(source.Id, frameCount);
			}

			static PingRequestFrame ParsePingRequest(OrderFrame source)
			{
				ValidateBodySize(source, OrderType.Ping, PingRequestFrame.SizeOfBody);
				ValidateId(source, OrderType.Ping, Frame.IdImmediateOrder);

				var body = source.Data.AsSpan();
				var runTime = MemoryMarshal.Read<long>(body.Slice(OrderFrame.SizeOfOrderType, sizeof(long)));
				if (runTime < 0)
					throw CreateInvalidValueException(source, OrderType.Ping, nameof(PingRequestFrame.RunTime), $"{runTime}");

				return new PingRequestFrame(runTime);
			}

			static PingResponseFrame ParsePingResponse(OrderFrame source)
			{
				ValidateBodySize(source, OrderType.Ping, PingResponseFrame.SizeOfBody);
				ValidateId(source, OrderType.Ping, Frame.IdImmediateOrder);

				var body = source.Data.AsSpan();
				var runTime = MemoryMarshal.Read<long>(body.Slice(OrderFrame.SizeOfOrderType, sizeof(long)));
				if (runTime < 0)
					throw CreateInvalidValueException(source, OrderType.Ping, nameof(PingRequestFrame.RunTime), $"{runTime}");

				var orderQueueLength = body[OrderFrame.SizeOfOrderType + sizeof(long)];
				return new PingResponseFrame(runTime, orderQueueLength);
			}

			static Frame ParsePing(OrderFrame source)
			{
				return source.BodySize == PingRequestFrame.SizeOfBody ? ParsePingRequest(source) : ParsePingResponse(source);
			}

			static SyncFrame ParseSync(OrderFrame source)
			{
				// Sync frames are sent by both client and server.
				if (source.BodySize != SyncFrame.SizeOfBody)
					throw CreateInvalidValueException(source, OrderType.SyncHash, nameof(Frame.BodySize), $"{source.BodySize}");

				var body = source.Data.AsSpan();
				var syncHash = MemoryMarshal.Read<int>(body.Slice(OrderFrame.SizeOfOrderType, sizeof(int)));
				var defeatState = MemoryMarshal.Read<ulong>(body.Slice(OrderFrame.SizeOfOrderType + sizeof(int), sizeof(ulong)));
				return new SyncFrame(source.Id, syncHash, defeatState);
			}

			static TickScaleFrame ParseTickScale(OrderFrame source)
			{
				ValidateBodySize(source, OrderType.TickScale, TickScaleFrame.SizeOfBody);

				var body = source.Data.AsSpan();
				var tickScale = MemoryMarshal.Read<float>(body.Slice(OrderFrame.SizeOfOrderType, sizeof(float)));
				return new TickScaleFrame(tickScale);
			}

			static DisconnectFrame ParseDisconnect(OrderFrame source)
			{
				ValidateBodySize(source, OrderType.Disconnect, DisconnectFrame.SizeOfBody);

				var body = source.Data.AsSpan();
				var disconnectClientId = MemoryMarshal.Read<int>(body.Slice(OrderFrame.SizeOfOrderType, sizeof(int)));
				return new DisconnectFrame(source.Id, disconnectClientId);
			}

			static Frame ParseHandshake(OrderFrame source)
			{
				if (source.BodySize < OrderFrame.SizeOfOrderType)
					throw CreateInvalidValueException(source, OrderType.Handshake, nameof(Frame.BodySize), $"{source.BodySize}");

				return new HandshakeFrame(source.Data);
			}

			public static Frame Convert(OrderFrame source)
			{
				if (source.BodySize == 0)
				{
					if (source.Type != OrderType.Fields)
						throw CreateParseException(source, OrderType.None, $"Invalid frame type \"{source.Type}\" for frame with an empty body. {source}.");

					return source;
				}

				Frame frame;
				var body = source.Data.AsSpan();
				var type = body[0];

				switch (type)
				{
					case (byte)OrderType.Ack:
						frame = ParseAck(source);
						break;
					case (byte)OrderType.Ping:
						frame = ParsePing(source);
						break;
					case (byte)OrderType.SyncHash:
						frame = ParseSync(source);
						break;
					case (byte)OrderType.TickScale:
						frame = ParseTickScale(source);
						break;
					case (byte)OrderType.Disconnect:
						frame = ParseDisconnect(source);
						break;
					case (byte)OrderType.Handshake:
						frame = ParseHandshake(source);
						break;
					case (byte)OrderType.Fields:
						frame = source;
						break;
					case (byte)OrderType.None:
					default:
						throw CreateParseException(source, OrderType.None, $"Unsupported frame type {type:X}. {source}.");
				}

				return frame;
			}
		}

		public static Frame Parse(int incomingConnection, Span<byte> body)
		{
			var genericFrame = FrameParser.Parse(incomingConnection, body);
			return BodyParser.Convert(genericFrame);
		}

		public static Frame Convert(OrderFrame source)
		{
			return BodyParser.Convert(source);
		}
	}
}
