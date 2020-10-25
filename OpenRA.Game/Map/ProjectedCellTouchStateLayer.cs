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
using OpenRA.Primitives;

namespace OpenRA
{
	public sealed class ProjectedCellTouchStateLayer : CellLayerBase<byte>
	{
		readonly PPos[] cells;
		int markedCellCount;

		/// <summary>Divides cells in blocks/chunks independant of UV position</summary>
		public ProjectedCellTouchStateLayer(Map map)
			: this(map.Grid.Type, new Size(map.MapSize.X, map.MapSize.Y)) { }

		public ProjectedCellTouchStateLayer(MapGridType gridType, Size size)
			: base(gridType, size) { cells = new PPos[Size.Width * Size.Height]; markedCellCount = 0; }

		public override void Clear(byte clearValue = 0)
		{
			if (clearValue != 0)
				throw new ArgumentException("Unsupported value");
			base.Clear(clearValue);
			markedCellCount = 0;
		}

		int Index(PPos uv)
		{
			return uv.V * Size.Width + uv.U;
		}

		/// <summary>Gets or the layer contents using projected map coordinates.</summary>
		public bool this[PPos uv]
		{
			get
			{
				return entries[Index(uv)] == 1;
			}
		}

		public void Mark(PPos uv)
		{
			var index = Index(uv);
			var prevValue = entries[index];
			if (prevValue == 0)
				cells[markedCellCount++] = uv;
			entries[index] = 1;
		}

		public bool Marked()
		{
			return markedCellCount > 0;
		}

		public bool Contains(PPos uv)
		{
			return bounds.Contains(uv.U, uv.V);
		}

		public void ApplyToMarked(Action<PPos> action, bool clearState = true)
		{
			if (clearState)
			{
				for (var i = 0; i < markedCellCount; i++)
				{
					var cell = cells[i];
					action(cell);
					entries[Index(cell)] = 0;
				}

				markedCellCount = 0;
			}
			else
			{
				for (var i = 0; i < markedCellCount; i++)
					action(cells[i]);
			}
		}

		public float GetPercentageOfCellsMarked()
		{
			return markedCellCount / (Size.Width * Size.Height * 0.01f);
		}

		public int GetNumberOfCellsMarked()
		{
			return markedCellCount;
		}
	}
}
