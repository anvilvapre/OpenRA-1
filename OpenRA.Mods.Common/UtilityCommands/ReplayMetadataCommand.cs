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
using System.IO;
using OpenRA.FileFormats;

namespace OpenRA.Mods.Common.UtilityCommands
{
	class ReplayMetadataCommand : IUtilityCommand
	{
		string IUtilityCommand.Name { get { return "--replay-metadata"; } }

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 2;
		}

		[Desc("REPLAYFILE", "Print the game metadata from a replay file.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var replay = ReplayMetadata.Read(args[1]);
			if (replay == null)
				throw new InvalidDataException("Failed to read replay meta data");

			var info = replay.GameInfo;

			var filePathNode = new MiniYamlNode(replay.FilePath, FieldSaver.Save(info));
			filePathNode.Write(System.Console.Out);

			Console.WriteLine("\tPlayers:");
			var playerCount = 0;
			foreach (var p in info.Players)
			{
				var playerNode = new MiniYamlNode("{0}".F(playerCount++), FieldSaver.Save(p));
				var playerLines = playerNode.WriteToString().Split('\n');
				foreach (var line in playerLines)
					Console.WriteLine("\t\t" + line);
			}
		}
	}
}
