#region Copyright & License Information
/*
 * Copyright 2007-2021 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Renders barrels for units with the Turreted trait.")]
	public class WithSpriteBarrelInfo : ConditionalTraitInfo, IRenderActorPreviewSpritesInfo, Requires<TurretedInfo>,
		Requires<ArmamentInfo>, Requires<RenderSpritesInfo>, Requires<BodyOrientationInfo>
	{
		[SequenceReference]
		[Desc("Sequence name to use.")]
		public readonly string Sequence = "barrel";

		[Desc("Armament to use for recoil.")]
		public readonly string Armament = "primary";

		[Desc("Visual offset.")]
		public readonly WVec LocalOffset = WVec.Zero;

		public override object Create(ActorInitializer init) { return new WithSpriteBarrel(init.Self, this); }

		public IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, string image, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var armament = init.Actor.TraitInfos<ArmamentInfo>()
				.First(a => a.Name == Armament);
			var t = init.Actor.TraitInfos<TurretedInfo>()
				.First(tt => tt.Turret == armament.Turret);

			var turretFacing = t.WorldFacingFromInit(init);

			var facing = init.GetFacing();
			Func<WRot> orientation = () => body.QuantizeOrientation(WRot.FromYaw(facing()), facings);
			Func<WVec> turretOffset = () => body.LocalToWorld(t.Offset.Rotate(orientation()));
			Func<WPos, int> zOffset = (pos) => -(pos.Y + pos.Z) + 1;

			var anim = new AnimationWithDynamicOffset(init.World, image, null, turretFacing, turretOffset, zOffset);
			anim.Play(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), Sequence));
			yield return new SpriteActorPreview(anim, p);
		}
	}

	public class WithSpriteBarrel : ConditionalTrait<WithSpriteBarrelInfo>
	{
		public readonly AnimationWithDynamicOffset DefaultAnimation;
		readonly RenderSprites rs;
		readonly Actor self;
		readonly Armament armament;
		readonly Turreted turreted;
		readonly BodyOrientation body;

		public WithSpriteBarrel(Actor self, WithSpriteBarrelInfo info)
			: base(info)
		{
			this.self = self;
			body = self.Trait<BodyOrientation>();
			armament = self.TraitsImplementing<Armament>()
				.First(a => a.Info.Name == Info.Armament);
			turreted = self.TraitsImplementing<Turreted>()
				.First(tt => tt.Name == armament.Info.Turret);

			rs = self.Trait<RenderSprites>();

			DefaultAnimation = new AnimationWithDynamicOffset(
				self.World, 
				rs.GetImage(self),
				null,
				() => turreted.WorldOrientation.Yaw,
				() => BarrelOffset(),
				p => RenderUtils.ZOffsetFromCenter(self, p, 0),
				() => IsTraitDisabled);

			DefaultAnimation.PlayRepeating(NormalizeSequence(self, Info.Sequence));
			rs.Add(DefaultAnimation);

			// Restrict turret facings to match the sprite
			turreted.QuantizedFacings = DefaultAnimation.CurrentSequence.Facings;
		}

		public string NormalizeSequence(Actor self, string sequence)
		{
			return RenderSprites.NormalizeSequence(DefaultAnimation, self.GetDamageState(), sequence);
		}

		WVec BarrelOffset()
		{
			var orientation = turreted != null ? turreted.WorldOrientation : self.Orientation;
			var localOffset = Info.LocalOffset + new WVec(-armament.Recoil, WDist.Zero, WDist.Zero);
			var turretLocalOffset = turreted != null ? turreted.Offset : WVec.Zero;
			return body.LocalToWorld(turretLocalOffset + localOffset.Rotate(orientation));
		}
	}
}
