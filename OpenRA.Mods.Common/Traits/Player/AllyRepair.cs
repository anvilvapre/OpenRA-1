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

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Attach this to the player actor to allow building repair by team mates.")]
	class AllyRepairInfo : TraitInfo<AllyRepair> { }

	class AllyRepair : IResolveOrder
	{
		public static class OrderID
		{
			public const string RepairBuilding = "RepairBuilding";
		}

		public IEnumerable<string> GetResolvableOrders(Actor self)
		{
			return new string[] { OrderID.RepairBuilding };
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.Target.Type == TargetType.Actor)
			{
				var building = order.Target.Actor;
				if (!building.AppearsFriendlyTo(self))
					return;

				building.TraitOrDefault<RepairableBuilding>()?.RepairBuilding(building, self.Owner);
			}
		}
	}
}
