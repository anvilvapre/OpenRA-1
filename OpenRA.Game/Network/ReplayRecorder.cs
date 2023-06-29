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
using System.Linq;
using OpenRA.FileFormats;

namespace OpenRA.Network
{
	public sealed class ReplayRecorder
	{
		// Arbitrary value.
		const int CreateReplayFileMaxRetryCount = 128;

		public ReplayMetadata Metadata;
		BinaryWriter writer;
		readonly Func<string> chooseFilename;
		MemoryStream preStartBuffer = new();

		static bool IsGameStart(Frame frame)
		{
			if (frame.Type != OrderType.Fields && frame.Type != OrderType.Handshake)
				return false;

			if (frame.Id != Frame.IdImmediateOrServerOrder)
				return false;

			var orderData = new OrderPacket((OrderFrame)frame);
			return orderData.GetOrders(null).Any(o => o.OrderString == "StartGame");
		}

		public ReplayRecorder(Func<string> chooseFilename)
		{
			this.chooseFilename = chooseFilename;

			writer = new BinaryWriter(preStartBuffer);
		}

		void StartSavingReplay(byte[] initialContent)
		{
			var filename = chooseFilename();
			var mod = Game.ModData.Manifest;
			var dir = Path.Combine(Platform.SupportDir, "Replays", mod.Id, mod.Metadata.Version);

			if (!Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			FileStream file = null;
			var id = -1;
			while (file == null)
			{
				var fullFilename = Path.Combine(dir, id < 0 ? $"{filename}.orarep" : $"{filename}-{id}.orarep");
				id++;
				try
				{
					file = File.Create(fullFilename);
				}
				catch (IOException ex)
				{
					if (id > CreateReplayFileMaxRetryCount)
						throw new ArgumentException($"Error creating replay file \"{filename}.orarep\"", ex);
				}
			}

			file.WriteArray(initialContent);
			writer = new BinaryWriter(file);
		}

		public void Receive(int clientID, byte[] frameData)
		{
			Receive(clientID, FrameIO.Parse(clientID, frameData));
		}

		public void Receive(int clientID, Frame frame)
		{
			if (disposed) // TODO: This can be removed once NetworkConnection is fixed to dispose properly.
				return;

			if (preStartBuffer != null && IsGameStart(frame))
			{
				writer.Flush();
				var preStartData = preStartBuffer.ToArray();
				preStartBuffer = null;
				StartSavingReplay(preStartData);
			}

			writer.Write(clientID);
			writer.Write(frame.SerializedSize);
			writer.Flush();
			frame.CopyTo(writer.BaseStream);
		}

		bool disposed;

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;

			if (Metadata != null)
			{
				if (Metadata.GameInfo != null)
					Metadata.GameInfo.EndTimeUtc = DateTime.UtcNow;
				Metadata.Write(writer);
			}

			preStartBuffer?.Dispose();
			writer.Close();
		}
	}
}
