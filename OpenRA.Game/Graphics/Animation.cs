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
using OpenRA.Primitives;
using OpenRA.Support;

namespace OpenRA.Graphics
{
	using AnimationWithOffset = AnimationWithDynamicOffset;

	public class SpriteAnimation
	{
		int frame;
		bool backwards;
		bool tickAlways;
		int timeUntilNextFrame;
		Action tickFunc = () => { };
		readonly SequenceProvider sequenceProvider;
		readonly Func<bool> paused;
		protected bool modified;
		protected ISpriteSequence currentSequence;

		public ISpriteSequence CurrentSequence {
			get => currentSequence; 
			private set { currentSequence = value; modified = true; } 
		}

		public string Name { get; private set; }
		public bool IsDecoration { get; set; }
	
		public SpriteAnimation(World world, string name)
			: this(world, name, null) { }

		public SpriteAnimation(World world, string name, Func<bool> paused)
		{
			sequenceProvider = world.Map.Rules.Sequences;
			Name = name.ToLowerInvariant();
			this.paused = paused;
			modified = true;
		}

		public int CurrentFrame => backwards ? currentSequence.Length - frame - 1 : frame;

		public void Play(string sequenceName)
		{
			PlayThen(sequenceName, null);
		}

		int CurrentSequenceTickOrDefault()
		{
			const int DefaultTick = 40; // 25 fps == 40 ms
			return currentSequence?.Tick ?? DefaultTick;
		}

		void PlaySequence(string sequenceName)
		{
			CurrentSequence = GetSequence(sequenceName);
			timeUntilNextFrame = CurrentSequenceTickOrDefault();
		}

		public void PlayRepeating(string sequenceName)
		{
			backwards = false;
			tickAlways = false;
			PlaySequence(sequenceName);

			frame = 0;
			tickFunc = () =>
			{
				++frame;
				if (frame >= currentSequence.Length)
					frame = 0;
			};
		}

		public bool ReplaceAnim(string sequenceName)
		{
			if (!HasSequence(sequenceName))
				return false;

			CurrentSequence = GetSequence(sequenceName);
			timeUntilNextFrame = Math.Min(CurrentSequenceTickOrDefault(), timeUntilNextFrame);
			frame %= currentSequence.Length;
			modified = true;
			return true;
		}

		public void PlayThen(string sequenceName, Action after)
		{
			backwards = false;
			tickAlways = false;
			PlaySequence(sequenceName);

			frame = 0;
			tickFunc = () =>
			{
				++frame;
				if (frame >= currentSequence.Length)
				{
					frame = currentSequence.Length - 1;
					tickFunc = () => { };
					after?.Invoke();
				}
			};
		}

		public void PlayBackwardsThen(string sequenceName, Action after)
		{
			PlayThen(sequenceName, after);
			backwards = true;
		}

		public void PlayFetchIndex(string sequenceName, Func<int> func)
		{
			backwards = false;
			tickAlways = true;
			PlaySequence(sequenceName);

			frame = func();
			tickFunc = () => frame = func();
		}

		public void PlayFetchDirection(string sequenceName, Func<int> direction)
		{
			tickAlways = false;
			PlaySequence(sequenceName);

			frame = 0;
			tickFunc = () =>
			{
				var d = direction();
				if (d > 0 && ++frame >= currentSequence.Length)
					frame = 0;

				if (d < 0 && --frame < 0)
					frame = currentSequence.Length - 1;
			};
		}

		public virtual bool Tick(Actor self = null)
		{
			if (paused == null || !paused())
				return Tick(self, 40); // tick one frame

			return false;
		}

		public virtual bool Tick(Actor self, int t)
		{
			var prevFrame = frame;

			if (tickAlways)
				tickFunc();
			else
			{
				timeUntilNextFrame -= t;
				while (timeUntilNextFrame <= 0)
				{
					tickFunc();
					timeUntilNextFrame += CurrentSequenceTickOrDefault();
				}
			}

			if (prevFrame != frame)
				modified = true;

			var u = modified;
			modified = false;
			return u;
		}

		public void ChangeImage(string newImage, string newAnimIfMissing)
		{
			newImage = newImage.ToLowerInvariant();

			if (Name != newImage)
			{
				Name = newImage;
				if (!ReplaceAnim(currentSequence.Name))
					if (!ReplaceAnim(newAnimIfMissing))
						modified = true;
			}
		}

		public bool HasSequence(string seq) { return sequenceProvider.HasSequence(Name, seq); }

		public ISpriteSequence GetSequence(string sequenceName)
		{
			return sequenceProvider.GetSequence(Name, sequenceName);
		}

		public string GetRandomExistingSequence(string[] sequences, MersenneTwister random)
		{
			return sequences.Where(s => HasSequence(s)).RandomOrDefault(random);
		}
	};

	public class AnimationWithFacing : SpriteAnimation
	{
		WAngle facing;

		public WAngle Facing { get => facing; set { facing = value; modified = true; }  }

		public Sprite Image => currentSequence.GetSprite(CurrentFrame, facing);
	
		public AnimationWithFacing(World world, string name)
			: this(world, name, null) { }

		public AnimationWithFacing(World world, string name, Func<bool> pausedFunc, WAngle facing = default(WAngle)) :
			base(world, name, pausedFunc)
		{
			this.facing = facing;
		}

		protected void Render(WPos pos, in WVec offset, int zOffset, PaletteReference palette, List<IRenderable> collection)
		{
			var tintModifiers = CurrentSequence.IgnoreWorldTint ? TintModifiers.IgnoreWorldTint : TintModifiers.None;
			var alpha = CurrentSequence.GetAlpha(CurrentFrame);
			var imageRenderable = new SpriteRenderable(Image, pos, offset, CurrentSequence.ZOffset + zOffset, palette, CurrentSequence.Scale, alpha, float3.Ones, tintModifiers, IsDecoration);

			if (CurrentSequence.ShadowStart >= 0)
			{
				var shadow = CurrentSequence.GetShadow(CurrentFrame, facing);
				var shadowRenderable = new SpriteRenderable(shadow, pos, offset, CurrentSequence.ShadowZOffset + zOffset, palette, CurrentSequence.Scale, 1f, float3.Ones, tintModifiers, true);
				collection.Add(shadowRenderable);
			}
			collection.Add(imageRenderable);
		}

		protected IRenderable[] Render(WPos pos, in WVec offset, int zOffset, PaletteReference palette)
		{
			var tintModifiers = CurrentSequence.IgnoreWorldTint ? TintModifiers.IgnoreWorldTint : TintModifiers.None;
			var alpha = CurrentSequence.GetAlpha(CurrentFrame);
			var imageRenderable = new SpriteRenderable(Image, pos, offset, CurrentSequence.ZOffset + zOffset, palette, CurrentSequence.Scale, alpha, float3.Ones, tintModifiers, IsDecoration);

			if (CurrentSequence.ShadowStart >= 0)
			{
				var shadow = CurrentSequence.GetShadow(CurrentFrame, facing);
				var shadowRenderable = new SpriteRenderable(shadow, pos, offset, CurrentSequence.ShadowZOffset + zOffset, palette, CurrentSequence.Scale, 1f, float3.Ones, tintModifiers, true);
				return new IRenderable[] { shadowRenderable, imageRenderable };
			}

			return new IRenderable[] { imageRenderable };
		}

		public virtual IRenderable[] Render(WPos pos, PaletteReference palette)
		{
			return Render(pos, WVec.Zero, 0, palette);
		}

		public virtual IRenderable[] Render(Actor self, WorldRenderer wr, PaletteReference palette)
		{
			return Render(self.CenterPosition, WVec.Zero, 0, palette);
		}

		protected IRenderable[] RenderUI(WorldRenderer wr, int2 pos, in WVec offset, int zOffset, PaletteReference palette, float scale = 1f)
		{
			scale *= CurrentSequence.Scale;
			var screenOffset = (scale * wr.ScreenVectorComponents(offset)).XY.ToInt2();
			var imagePos = pos + screenOffset - new int2((int)(scale * Image.Size.X / 2), (int)(scale * Image.Size.Y / 2));
			var alpha = CurrentSequence.GetAlpha(CurrentFrame);
			var imageRenderable = new UISpriteRenderable(Image, WPos.Zero + offset, imagePos, CurrentSequence.ZOffset + zOffset, palette, scale, alpha);

			if (CurrentSequence.ShadowStart >= 0)
			{
				var shadow = CurrentSequence.GetShadow(CurrentFrame, facing);
				var shadowPos = pos - new int2((int)(scale * shadow.Size.X / 2), (int)(scale * shadow.Size.Y / 2));
				var shadowRenderable = new UISpriteRenderable(shadow, WPos.Zero + offset, shadowPos, CurrentSequence.ShadowZOffset + zOffset, palette, scale);
				return new IRenderable[] { shadowRenderable, imageRenderable };
			}

			return new IRenderable[] { imageRenderable };
		}

		public virtual IRenderable[] RenderUI(WorldRenderer wr, int2 pos, PaletteReference palette, float scale = 1f)
		{
			return RenderUI(wr, pos, WVec.Zero, 0, palette, scale);	
		}

		protected Rectangle ScreenBounds(WorldRenderer wr, WPos pos, in WVec offset)
		{
			var scale = CurrentSequence.Scale;
			var xy = wr.ScreenPxPosition(pos) + wr.ScreenPxOffset(offset);
			var cb = CurrentSequence.Bounds;
			return Rectangle.FromLTRB(
				xy.X + (int)(cb.Left * scale),
				xy.Y + (int)(cb.Top * scale),
				xy.X + (int)(cb.Right * scale),
				xy.Y + (int)(cb.Bottom * scale));
		}

		public virtual Rectangle ScreenBounds(WorldRenderer wr, WPos pos)
		{
			return ScreenBounds(wr, pos, WVec.Zero);
		}
	};

	public class Animation : AnimationWithFacing
	{
		readonly Func<WAngle> facingFunc;

		public Animation(World world, string name, Func<WAngle> facingFunc = null)
			: this(world, name, null, facingFunc) { }

		public Animation(World world, string name, Func<bool> pausedFunc, Func<WAngle> facingFunc = null)
			: base(world, name, pausedFunc)
		{
			this.facingFunc = facingFunc;
		}

		public override bool Tick(Actor self, int t)
		{
			Facing = facingFunc();
			return base.Tick(self, t);
		}
	};

	public class AnimationWithDynamicOffset : AnimationWithFacing
	{
		public readonly Func<bool> DisableFunc;

		readonly Func<WAngle> facingFunc;
		readonly Func<WVec> offsetFunc;
		readonly Func<WPos, int> zOffsetFunc;

		WVec offset;
		int zOffset;
	
		public AnimationWithDynamicOffset(World world, string name, Func<bool> pausedFunc, Func<WAngle> facingFunc, Func<WVec> offsetFunc, Func<WPos, int> zOffsetFunc, Func<bool> disableFunc = null)
			: base(world, name, pausedFunc)
		{
			this.facingFunc = facingFunc;
			this.offsetFunc = offsetFunc;
			this.zOffsetFunc = zOffsetFunc;
			DisableFunc = disableFunc;
		}

		public override bool Tick(Actor self, int t)
		{
			Facing = facingFunc();

			var o = offsetFunc();
			if (o != offset)
			{
				offset = o;
				modified = true;
			}

			var zo = zOffsetFunc(self.CenterPosition + offset);
			if (zo != zOffset)
			{
				zOffset = zo;
				modified = true;
			}

			return base.Tick(self, t);
		}

		public void Render(Actor self, WorldRenderer wr, PaletteReference pal, List<IRenderable> collection)
		{
			Render(self.CenterPosition, offset, zOffset, pal, collection);
		}

		public override IRenderable[] Render(WPos pos, PaletteReference palette)
		{
			return Render(pos, offset, zOffset, palette);
		}

		public override IRenderable[] Render(Actor self, WorldRenderer wr, PaletteReference palette)
		{
			return Render(self.CenterPosition, offset, zOffset, palette);
		}

		public override IRenderable[] RenderUI(WorldRenderer wr, int2 pos, PaletteReference palette, float scale = 1f)
		{
			return RenderUI(wr, pos, offset, zOffset, palette, scale);
		}

		public Rectangle ScreenBounds(Actor self, WorldRenderer wr)
		{
			return ScreenBounds(wr, self.CenterPosition, offset);
		}
	};

	public class AnimationWithStaticOffset : AnimationWithFacing
	{
		public readonly Func<bool> DisableFunc;

		WVec offset;
		int zOffset;
	
		public AnimationWithStaticOffset(World world, string name, Func<bool> pausedFunc = null, WAngle facing = default(WAngle), WVec offset = default(WVec), int zOffset = 0, Func<bool> disableFunc = null)
			: base(world, name, pausedFunc, facing)
		{
			this.offset = offset;
			this.zOffset = zOffset;
			DisableFunc = disableFunc;
		}

		public virtual void Render(Actor self, WorldRenderer wr, PaletteReference pal, List<IRenderable> collection)
		{
			Render(self.CenterPosition, offset, zOffset, pal, collection);
		}

		public override IRenderable[] Render(WPos pos, PaletteReference palette)
		{
			return Render(pos, offset, zOffset, palette);
		}

		public override IRenderable[] Render(Actor self, WorldRenderer wr, PaletteReference palette)
		{
			return Render(self.CenterPosition, offset, zOffset, palette);
		}

		public override IRenderable[] RenderUI(WorldRenderer wr, int2 pos, PaletteReference palette, float scale = 1f)
		{
			return RenderUI(wr, pos, offset, zOffset, palette, scale);
		}

		public Rectangle ScreenBounds(Actor self, WorldRenderer wr)
		{
			return ScreenBounds(wr, self.CenterPosition, offset);
		}
	};

	/*
	public class AnimationOrig
	{
		public ISpriteSequence CurrentSequence { get; private set; }
		public string Name { get; private set; }
		public bool IsDecoration { get; set; }

		readonly SequenceProvider sequenceProvider;
		readonly Func<WAngle> facingFunc;
		readonly Func<bool> paused;

		int frame;
		bool backwards;
		bool tickAlways;
		int timeUntilNextFrame;
		Action tickFunc = () => { };

		public Animation(World world, string name)
			: this(world, name, () => WAngle.Zero) { }

		public Animation(World world, string name, Func<WAngle> facingFunc)
			: this(world, name, facingFunc, null) { }

		public Animation(World world, string name, Func<bool> paused)
			: this(world, name, () => WAngle.Zero, paused) { }

		public Animation(World world, string name, Func<WAngle> facingFunc, Func<bool> paused)
		{
			sequenceProvider = world.Map.Rules.Sequences;
			Name = name.ToLowerInvariant();
			this.facingFunc = facingFunc;
			this.paused = paused;
		}

		public int CurrentFrame => backwards ? CurrentSequence.Length - frame - 1 : frame;
		public Sprite Image => CurrentSequence.GetSprite(CurrentFrame, facingFunc());

		public void Render(WPos pos, in WVec offset, int zOffset, PaletteReference palette, List<IRenderable> collection)
		{
			var tintModifiers = CurrentSequence.IgnoreWorldTint ? TintModifiers.IgnoreWorldTint : TintModifiers.None;
			var alpha = CurrentSequence.GetAlpha(CurrentFrame);
			var imageRenderable = new SpriteRenderable(Image, pos, offset, CurrentSequence.ZOffset + zOffset, palette, CurrentSequence.Scale, alpha, float3.Ones, tintModifiers, IsDecoration);

			if (CurrentSequence.ShadowStart >= 0)
			{
				var shadow = CurrentSequence.GetShadow(CurrentFrame, facingFunc());
				var shadowRenderable = new SpriteRenderable(shadow, pos, offset, CurrentSequence.ShadowZOffset + zOffset, palette, CurrentSequence.Scale, 1f, float3.Ones, tintModifiers, true);
				collection.Add(shadowRenderable);
			}
			collection.Add(imageRenderable);
		}

		public IRenderable[] Render(WPos pos, in WVec offset, int zOffset, PaletteReference palette)
		{
			var tintModifiers = CurrentSequence.IgnoreWorldTint ? TintModifiers.IgnoreWorldTint : TintModifiers.None;
			var alpha = CurrentSequence.GetAlpha(CurrentFrame);
			var imageRenderable = new SpriteRenderable(Image, pos, offset, CurrentSequence.ZOffset + zOffset, palette, CurrentSequence.Scale, alpha, float3.Ones, tintModifiers, IsDecoration);

			if (CurrentSequence.ShadowStart >= 0)
			{
				var shadow = CurrentSequence.GetShadow(CurrentFrame, facingFunc());
				var shadowRenderable = new SpriteRenderable(shadow, pos, offset, CurrentSequence.ShadowZOffset + zOffset, palette, CurrentSequence.Scale, 1f, float3.Ones, tintModifiers, true);
				return new IRenderable[] { shadowRenderable, imageRenderable };
			}

			return new IRenderable[] { imageRenderable };
		}

		public IRenderable[] RenderUI(WorldRenderer wr, int2 pos, in WVec offset, int zOffset, PaletteReference palette, float scale = 1f)
		{
			scale *= CurrentSequence.Scale;
			var screenOffset = (scale * wr.ScreenVectorComponents(offset)).XY.ToInt2();
			var imagePos = pos + screenOffset - new int2((int)(scale * Image.Size.X / 2), (int)(scale * Image.Size.Y / 2));
			var alpha = CurrentSequence.GetAlpha(CurrentFrame);
			var imageRenderable = new UISpriteRenderable(Image, WPos.Zero + offset, imagePos, CurrentSequence.ZOffset + zOffset, palette, scale, alpha);

			if (CurrentSequence.ShadowStart >= 0)
			{
				var shadow = CurrentSequence.GetShadow(CurrentFrame, facingFunc());
				var shadowPos = pos - new int2((int)(scale * shadow.Size.X / 2), (int)(scale * shadow.Size.Y / 2));
				var shadowRenderable = new UISpriteRenderable(shadow, WPos.Zero + offset, shadowPos, CurrentSequence.ShadowZOffset + zOffset, palette, scale);
				return new IRenderable[] { shadowRenderable, imageRenderable };
			}

			return new IRenderable[] { imageRenderable };
		}

		public Rectangle ScreenBounds(WorldRenderer wr, WPos pos, in WVec offset)
		{
			var scale = CurrentSequence.Scale;
			var xy = wr.ScreenPxPosition(pos) + wr.ScreenPxOffset(offset);
			var cb = CurrentSequence.Bounds;
			return Rectangle.FromLTRB(
				xy.X + (int)(cb.Left * scale),
				xy.Y + (int)(cb.Top * scale),
				xy.X + (int)(cb.Right * scale),
				xy.Y + (int)(cb.Bottom * scale));
		}

		public IRenderable[] Render(WPos pos, PaletteReference palette)
		{
			return Render(pos, WVec.Zero, 0, palette);
		}

		public void Play(string sequenceName)
		{
			PlayThen(sequenceName, null);
		}

		int CurrentSequenceTickOrDefault()
		{
			const int DefaultTick = 40; // 25 fps == 40 ms
			return CurrentSequence?.Tick ?? DefaultTick;
		}

		void PlaySequence(string sequenceName)
		{
			CurrentSequence = GetSequence(sequenceName);
			timeUntilNextFrame = CurrentSequenceTickOrDefault();
		}

		public void PlayRepeating(string sequenceName)
		{
			backwards = false;
			tickAlways = false;
			PlaySequence(sequenceName);

			frame = 0;
			tickFunc = () =>
			{
				++frame;
				if (frame >= CurrentSequence.Length)
					frame = 0;
			};
		}

		public bool ReplaceAnim(string sequenceName)
		{
			if (!HasSequence(sequenceName))
				return false;

			CurrentSequence = GetSequence(sequenceName);
			timeUntilNextFrame = Math.Min(CurrentSequenceTickOrDefault(), timeUntilNextFrame);
			frame %= CurrentSequence.Length;
			return true;
		}

		public void PlayThen(string sequenceName, Action after)
		{
			backwards = false;
			tickAlways = false;
			PlaySequence(sequenceName);

			frame = 0;
			tickFunc = () =>
			{
				++frame;
				if (frame >= CurrentSequence.Length)
				{
					frame = CurrentSequence.Length - 1;
					tickFunc = () => { };
					after?.Invoke();
				}
			};
		}

		public void PlayBackwardsThen(string sequenceName, Action after)
		{
			PlayThen(sequenceName, after);
			backwards = true;
		}

		public void PlayFetchIndex(string sequenceName, Func<int> func)
		{
			backwards = false;
			tickAlways = true;
			PlaySequence(sequenceName);

			frame = func();
			tickFunc = () => frame = func();
		}

		public void PlayFetchDirection(string sequenceName, Func<int> direction)
		{
			tickAlways = false;
			PlaySequence(sequenceName);

			frame = 0;
			tickFunc = () =>
			{
				var d = direction();
				if (d > 0 && ++frame >= CurrentSequence.Length)
					frame = 0;

				if (d < 0 && --frame < 0)
					frame = CurrentSequence.Length - 1;
			};
		}

		public void Tick()
		{
			if (paused == null || !paused())
				Tick(40); // tick one frame
		}

		public void Tick(int t)
		{
			if (tickAlways)
				tickFunc();
			else
			{
				timeUntilNextFrame -= t;
				while (timeUntilNextFrame <= 0)
				{
					tickFunc();
					timeUntilNextFrame += CurrentSequenceTickOrDefault();
				}
			}
		}

		public void ChangeImage(string newImage, string newAnimIfMissing)
		{
			newImage = newImage.ToLowerInvariant();

			if (Name != newImage)
			{
				Name = newImage;
				if (!ReplaceAnim(CurrentSequence.Name))
					ReplaceAnim(newAnimIfMissing);
			}
		}

		public bool HasSequence(string seq) { return sequenceProvider.HasSequence(Name, seq); }

		public ISpriteSequence GetSequence(string sequenceName)
		{
			return sequenceProvider.GetSequence(Name, sequenceName);
		}

		public string GetRandomExistingSequence(string[] sequences, MersenneTwister random)
		{
			return sequences.Where(s => HasSequence(s)).RandomOrDefault(random);
		}
	}
	*/
}
