#region Copyright & License Information
/*
 * Copyright 2007-2017 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion
//#define _TRAIT_CONTAINER_NEW_

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using OpenRA.Primitives;


namespace OpenRA
{
	static class ListExts
	{
		public static int BinarySearchMany(this List<Actor> list, uint searchFor)
		{
			var start = 0;
			var end = list.Count;
			while (start != end)
			{
				var mid = (start + end) / 2;
				if (list[mid].ActorID < searchFor)
					start = mid + 1;
				else
					end = mid;
			}

			return start;
		}
	}

	/// <summary>
	/// Provides efficient ways to query a set of actors by their traits.
	/// </summary>
	class TraitDictionary
	{
		 // static readonly Func<Type, ITraitContainer> CreateTraitContainer = t =>
		//	(ITraitContainer)typeof(TraitContainer<>).MakeGenericType(t).GetConstructor(Type.EmptyTypes).Invoke(null);
		static readonly Func<Type, ITraitContainer> CreateTraitContainer = t =>
			(ITraitContainer)typeof(ProfilingTraitContainer<>).MakeGenericType(t).GetConstructor(Type.EmptyTypes).Invoke(null);

		private ITraitContainer[] traits = null;

		public TraitDictionary()
		{
			Resize();
		}

		private void Resize()
		{
			// New trait types have been registered. Ensure our array is same size as trait type index map.
			int newSize = TraitTypeIndexMap.GetCount() + 64;
			if (traits == null)
				traits = new ITraitContainer[newSize];
			else
			{
				if (traits.Length < newSize)
					Array.Resize<ITraitContainer>(ref traits, newSize);
			}
		}

		private ITraitContainer InnerGet(int index, Type t) {
			// Lookup trait by index in array, rather than hash key in dictionary.
			if (index <= 0)
				throw new InvalidOperationException("Traits type index out of bounds (<=0)");

			if (index >= traits.Length)
			{
				Resize();
				if (index > traits.Length)
					throw new InvalidOperationException("Traits type index out of bounds, index greater than max registered type index");
			}

			ITraitContainer trait = traits[index];
			if (trait == null)
			{
				trait = CreateTraitContainer(t);
				traits[index] = trait;
			}

			return trait;
		}

		ITraitContainer InnerGet(Type t)
		{
			// Slower, dictionary lookup of index
			return InnerGet(TraitTypeIndexMap.RegisterType(t), t);
		}

		ProfilingTraitContainer<T> InnerGet<T>()
		{
			return (ProfilingTraitContainer<T>)InnerGet(TraitTypeIndex<T>.GetTypeIndex(), typeof(T));
		}

		public void PrintReport()
		{
			if (traits == null)
				return;

			const string ChannelId = "traitreport";
			Log.AddChannel(ChannelId, "traitreport.log");
			Log.Write("traitreport", "Number of registered trait types {0}", TraitTypeIndexMap.GetCount());
			for (int i = 1; i < traits.Length; i++)
			{
				var trait = traits[i];
				if (trait != null)
					Log.Write("traitreport", "{0}: {1}", TraitTypeIndexMap.GetType(i), trait.Queries);
			}

			for (int i = 1; i < traits.Length; i++)
			{
				var trait = traits[i];
				if (trait != null)
					trait.PrintReport(ChannelId, i);
			}
		}

		public void AddTrait(Actor actor, object val)
		{
			var t = val.GetType();

			foreach (var i in t.GetInterfaces())
				InnerAdd(actor, i, val);
			foreach (var tt in t.BaseTypes())
				InnerAdd(actor, tt, val);
		}

		void InnerAdd(Actor actor, Type t, object val)
		{
			InnerGet(t).Add(actor, val);
		}

		static void CheckDestroyed(Actor actor)
		{
			if (actor.Disposed)
				throw new InvalidOperationException("Attempted to get trait from destroyed object ({0})".F(actor));
		}

		public T Get<T>(Actor actor)
		{
			CheckDestroyed(actor);
			return InnerGet<T>().Get(actor.ActorID);
		}

		public T GetOrDefault<T>(Actor actor)
		{
			CheckDestroyed(actor);
			return InnerGet<T>().GetOrDefault(actor.ActorID);
		}

		public IEnumerable<T> WithInterface<T>(Actor actor)
		{
			CheckDestroyed(actor);
			return InnerGet<T>().GetMultiple(actor.ActorID);
		}

		public IEnumerable<TraitPair<T>> ActorsWithTrait<T>()
		{
			return InnerGet<T>().All();
		}

		public IEnumerable<Actor> ActorsHavingTrait<T>()
		{
			return InnerGet<T>().Actors();
		}

		public IEnumerable<Actor> ActorsHavingTrait<T>(Func<T, bool> predicate)
		{
			return InnerGet<T>().Actors(predicate);
		}

		public void RemoveActor(Actor a)
		{
			foreach (var t in traits) 
				if (t != null)
					t.RemoveActor(a.ActorID);
		}

		interface ITraitContainer
		{
			void Add(Actor actor, object trait);
			void RemoveActor(uint actor);
			int Queries { get; }
			void PrintReport(string logChannel, int traitIndex);
		}

		class TraitContainerStat
		{
			public enum SumId
			{
				ACTOR_COUNT = 0,
				TRAIT_COUNT,
				CALL,
				CALL_ADD,
				CALL_GET,
				CALL_GET_OR_DEFAULT,
				CALL_GET_MULTIPLE,
				CALL_ALL,
				CALL_ACTORS,
				CALL_ACTORS_PREDICATE,
				CALL_REMOVE_ACTOR,
				CALL_ENUM_MULTIPLE,
				CALL_ENUM_ACTOR,
				CALL_ENUM_ALL,
				TIME_GET,
				TIME_GET_OR_DEFAULT,
				TIME_GET_MULTIPLE,
				TIME_ALL,
				TIME_ACTORS,
				TIME_ACTORS_PREDICATE,
				TIME_REMOVE_ACTOR,
				TIME_ENUM_MULTIPLE,
				TIME_ENUM_ACTOR,
				TIME_ENUM_ALL,
				_MAX
			}

			public enum MaxId
			{
				TRAIT_PER_ACTOR,
				_MAX
			}

			public double[] SumValues = new double[(int)SumId._MAX];
			public double[] MaxValues = new double[(int)MaxId._MAX];

			public TraitContainerStat()
			{
				for (int i = 0; i < SumValues.Length; i++) {
					SumValues[i] = -1.0;
				}
				for (int i = 0; i < MaxValues.Length; i++) {
					MaxValues[i] = -1.0;
				}
			}
		}

		class ProfilingEnumerable<T> : IEnumerable<T>
		{
			private readonly int statCall;
			private readonly int statTime;
			private readonly TraitContainerStat stat;
			private readonly IEnumerable<T> parent;

			public ProfilingEnumerable(TraitContainerStat stat, int statCall, int statTime, IEnumerable<T> parent)
			{
				this.stat = stat;
				this.statCall = statCall;
				this.statTime = statTime;
				this.parent = parent;
			}

			public IEnumerator<T> GetEnumerator() { return new ProfilingEnumerator<T>(stat, statCall, statTime, parent.GetEnumerator()); }

			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
		}

		class ProfilingEnumerator<T> : IEnumerator<T>
		{
			private readonly int statCall;
			private readonly int statTime;
			private readonly TraitContainerStat stat;
			private readonly IEnumerator<T> parent;

			public ProfilingEnumerator(TraitContainerStat stat, int statCall, int statTime, IEnumerator<T> parent)
			{
				this.stat = stat;
				this.statCall = statCall;
				this.statTime = statTime;
				this.parent = parent;
			}

			public void Reset() { parent.Reset(); }
			public bool MoveNext()
			{
				Stopwatch sw = Stopwatch.StartNew();
				bool temp = parent.MoveNext();
				sw.Stop();
				stat.SumValues[statTime] += sw.Elapsed.TotalMilliseconds;
				stat.SumValues[statCall]++;
				return temp;
			}

			public T Current
			{
				get
				{
					Stopwatch sw = Stopwatch.StartNew();
					var temp = parent.Current;
					sw.Stop();
					stat.SumValues[statTime] += sw.Elapsed.TotalMilliseconds;
					stat.SumValues[statCall]++;
					return temp;
				}
			}

			object System.Collections.IEnumerator.Current { get { return Current; } }
			public void Dispose() { }
		}

		class ProfilingTraitContainer<T> : ITraitContainer
		{
			public TimeSpan TimeSpanAdd;
			public TimeSpan TimeSpanGet;
			public TimeSpan TimeSpanGetOrDefault;
			public TimeSpan TimeSpanGetMultiple;
			public TimeSpan TimeSpanAll;
			public TimeSpan TimeSpanActors;
			public TimeSpan TimeSpanActorsPredicate;
			public TimeSpan TimeSpanRemoveActor;
			public uint Count;
			public uint CountAdd;
			public uint CountGet;
			public uint CountGetOrDefault;
			public uint CountGetMultiple;
			public uint CountAll;
			public uint CountActors;
			public uint CountActorsPredicate;
			public uint CountRemoveActor;
			private TraitContainerStat stat = new TraitContainerStat();
			private TraitContainer<T> container = new TraitContainer<T>(); 

			public int Queries
			{
				get
				{
					return container.Queries;
				}
			}

			public void Add(Actor actor, object trait)
			{
				Stopwatch sw = Stopwatch.StartNew();
				container.Add(actor, trait);
				sw.Stop();
				TimeSpanAdd += sw.Elapsed;
				CountAdd++;
				Count++;
			}

			public T Get(uint actorID)
			{
				Stopwatch sw = Stopwatch.StartNew();
				T temp;
				temp = container.Get(actorID);
				sw.Stop();
				TimeSpanGet += sw.Elapsed;
				CountGet++;
				Count++;
				return temp;
			}

			public T GetOrDefault(uint actorID)
			{
				Stopwatch sw = Stopwatch.StartNew();
				var temp = container.GetOrDefault(actorID);
				sw.Stop();
				TimeSpanGetOrDefault += sw.Elapsed;
				CountGetOrDefault++;
				Count++;
				return temp;
			}

			public IEnumerable<T> GetMultiple(uint actorID)
			{
				Stopwatch sw = Stopwatch.StartNew();
				var temp = container.GetMultiple(actorID);
				sw.Stop();
				TimeSpanGetMultiple += sw.Elapsed;
				CountGetMultiple++;
				Count++;
				return new ProfilingEnumerable<T>(stat,
					(int)TraitContainerStat.SumId.CALL_ENUM_MULTIPLE,
					(int)TraitContainerStat.SumId.TIME_ENUM_MULTIPLE,
					temp);
			}

			public IEnumerable<TraitPair<T>> All()
			{
				Stopwatch sw = Stopwatch.StartNew();
				var temp = container.All();
				sw.Stop();
				TimeSpanAll += sw.Elapsed;
				CountAll++;
				Count++;
				return temp;
			}

			public IEnumerable<Actor> Actors()
			{
				Stopwatch sw = Stopwatch.StartNew();
				var temp = container.Actors();
				sw.Stop();
				TimeSpanActors += sw.Elapsed;
				CountActors++;
				Count++;
				return temp;
			}

			public IEnumerable<Actor> Actors(Func<T, bool> predicate)
			{
				Stopwatch sw = Stopwatch.StartNew();
				var temp = container.Actors(predicate);
				sw.Stop();
				TimeSpanActorsPredicate += sw.Elapsed;
				CountActorsPredicate++;
				Count++;
				return temp;
			}

			public void RemoveActor(uint actorID)
			{
				Stopwatch sw = Stopwatch.StartNew();
				container.RemoveActor(actorID);
				sw.Stop();
				TimeSpanRemoveActor += sw.Elapsed;
				CountRemoveActor++;
				Count++;
			}

			public void PrintReport(string logChannel, int traitIndex)
			{
				container.PrintReport(logChannel, traitIndex);
				Console.WriteLine("Profile report for trait {0} ", typeof(T));
				TraitContainerStat stat = GetStat();
				StringBuilder sb = new StringBuilder();
				if (traitIndex == 1) {
					sb.Append("Trait");
					for (var i = 0; i < (int)TraitContainerStat.SumId._MAX; i++)
						sb.Append("\t").Append(((TraitContainerStat.SumId)i).ToString());
					Log.Write(logChannel, sb.ToString());
					sb = new StringBuilder();
				}
				sb.Append(typeof(T));
				for (var i = 0; i < (int)TraitContainerStat.SumId._MAX; i++)
					sb.Append("\t").Append(stat.SumValues[i]);
				Log.Write(logChannel, sb.ToString());
			}

			public TraitContainerStat GetStat()
			{
				TraitContainerStat stat = container.GetStat();
				if (stat == null) {
					stat = new TraitContainerStat();
				}

				stat.SumValues[(int)TraitContainerStat.SumId.CALL] = Count;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ADD] = CountAdd;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_GET] = CountGet;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_GET_OR_DEFAULT] = CountGetOrDefault;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_GET_MULTIPLE] = CountGetMultiple;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ALL] = CountAll;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ACTORS] = CountActors;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ACTORS_PREDICATE] = CountActorsPredicate;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ACTORS_PREDICATE] = CountActorsPredicate;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_REMOVE_ACTOR] = CountRemoveActor;
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ENUM_MULTIPLE] = this.stat.SumValues[(int)TraitContainerStat.SumId.CALL_ENUM_MULTIPLE];
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ENUM_ACTOR] = this.stat.SumValues[(int)TraitContainerStat.SumId.CALL_ENUM_ACTOR];
				stat.SumValues[(int)TraitContainerStat.SumId.CALL_ENUM_ALL] = this.stat.SumValues[(int)TraitContainerStat.SumId.CALL_ENUM_ALL];

				stat.SumValues[(int)TraitContainerStat.SumId.TIME_GET] = TimeSpanGet.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_GET_OR_DEFAULT] = TimeSpanGetOrDefault.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_GET_MULTIPLE] = TimeSpanGetMultiple.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_ALL] = TimeSpanAll.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_ACTORS] = TimeSpanActors.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_ACTORS_PREDICATE] = TimeSpanActorsPredicate.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_REMOVE_ACTOR] = TimeSpanRemoveActor.TotalMilliseconds;
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_ENUM_MULTIPLE] = this.stat.SumValues[(int)TraitContainerStat.SumId.TIME_ENUM_MULTIPLE];
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_ENUM_ACTOR] = this.stat.SumValues[(int)TraitContainerStat.SumId.TIME_ENUM_ACTOR];
				stat.SumValues[(int)TraitContainerStat.SumId.TIME_ENUM_ALL] = this.stat.SumValues[(int)TraitContainerStat.SumId.TIME_ENUM_ALL];
				return stat;
			}
		}
#if _TRAIT_CONTAINER_NEW_

		class TraitContainer<T> : ITraitContainer
		{
			protected class Element
			{
				public uint ActorID;

				// Avoid heavy creation on pair objects in enumarators.
				public TraitPair<T>[] Pairs;
				public T[] Traits;

				public Element(Actor actor, T trait)
				{
					this.ActorID = actor.ActorID;
					this.Pairs = new TraitPair<T>[1];
					this.Pairs[0] = new TraitPair<T>(actor, trait);
					this.Traits = new T[1];
					this.Traits[0] = trait;
				}

				public void AddTrait(T trait)
				{
					var size = this.Pairs.Length + 1;
					Array.Resize<TraitPair<T> >(ref this.Pairs, size);
					this.Pairs[size - 1] = new TraitPair<T>(this.Pairs[0].Actor, trait);
					Array.Resize<T>(ref this.Traits, size);
					this.Traits[size - 1] = trait;
				}
			}

			protected class ArrayList
			{
				public Element[] Elements;
				public int Count;
				private int capacity;

				public ArrayList(int capacity)
				{
					Count = 0;
					this.capacity = capacity <= 0 ? 32 : capacity;
					Elements = new Element[this.capacity];
				}

				private void Grow()
				{
					const int CapInc = 32;
					Array.Resize<Element>(ref Elements, capacity + CapInc);
					capacity += CapInc;
				}

				private void Shrink()
				{
					var half = capacity >> 2;
					if (half >= Count && half > 0)
					{
						Array.Resize<Element>(ref Elements, half);
						capacity = half;
					}
				}

				public void Add(Element element)
				{
					if (Count >= capacity)
						Grow();
					Elements[Count++] = element;
				}

				public void Insert(int index, Element element) {
					if (Count >= capacity)
						Grow();
					if (index == Count)
						Elements[Count++] = element;
					else
					{
						if (index > Count)
							throw new InvalidOperationException("Index out of bounds {0}".F(index));
						Array.Copy(Elements, index, Elements, index + 1, Count - index);
						++Count;
						Elements[index] = element;
					}
				}

				public void RemoveAt(int index) {
					if (index < 0 || index >= Count)
						throw new InvalidOperationException("Index out of bounds {0}".F(index));
					--Count;
					if (index < Count)
						Array.Copy(Elements, index + 1, Elements, index, Count - index);
					Elements[Count] = null;
					Shrink();
				}
			}

			// 6 multi player ai game has 500 actors, though ai does not create a lot of actors.
			protected ArrayList list = new ArrayList(512);

			public int Queries { get; private set; }

			private Element Insert(Actor actor, T trait)
			{
				// Assumption: actor is new, has highest id, so needs to be added to back.
				var actorID = actor.ActorID;
				int index = 0;
				Element element = null;
				var elements = list.Elements;
				for (var i = list.Count - 1; i >= 0; --i)
				{
					var el = elements[i];
					var aid = el.ActorID;
					if (aid == actorID)
					{
						element = el;
						el.AddTrait(trait);
						break;
					}
					else if (aid < actorID)
					{
						index = i + 1;
						break;
					}
				}

				if (element == null)
				{
					element = new Element(actor, trait);
					list.Insert(index, element);
				}

				return element;
			}

			public void Add(Actor actor, object trait)
			{
				Insert(actor, (T)trait);
				// disabled
				if (list.Count == -1) {
					SelfTest(list.Count);
				}
			}

			private void SelfTest(int ac) {
				int actorCount = 0;
				foreach (var a in Actors()) {
					actorCount += 1;
				}
				if (actorCount != ac) {
					throw new InvalidOperationException("Actor enum {0}".F(actorCount));
				}
				uint lastActorId = 0xffff;
				actorCount = 0;
				int traitCount = 0;
				foreach (var p in All()) {
					if (p.Actor.ActorID != lastActorId) {
						actorCount+=1;
						lastActorId = p.Actor.ActorID;
					}
					traitCount+=1;
				}
				if (actorCount != ac) {
					throw new InvalidOperationException("All enum {0}".F(actorCount));
				}
				int tc = 0;
				int pc = 0;
				foreach (var el in list.Elements) {
					if (el != null) {
						tc += el.Traits.Length;
						pc += el.Pairs.Length;
					}
				}
				if (tc != pc) {
					throw new InvalidOperationException("trait cound enum hm {0} {1}".F(tc, traitCount));
				}
				if (tc != traitCount) {
					Console.WriteLine("aaaaaaaaa");
					foreach (var el in list.Elements) {
						if (el != null) {
							int i=0;
							foreach (var t in el.Traits) {
								Console.WriteLine("list.el.trait {0} {1} {2}", i, t, el.Pairs[0].Actor.Info.Name);
								i++;
							}
						}
					}

					Console.WriteLine("aaaaaaaaabbbbbbbbbbbbbbb");
					int j = 0;
					foreach (var p in All()) {
						Console.WriteLine("iter {0} {1} {2}", j, p.Trait, p.Actor.Info.Name);
						j++;
					}
					Console.WriteLine("aaaaaaaaabbbbbbbbbbbbbbcccccccccccccccccb");

					throw new InvalidOperationException("trait cound enum elem: {0}  iter:{1}".F(tc, traitCount));
				}
			}

			private Element FindActor(uint actorID)
			{
				var start = 0;
				var end = list.Count;
				var elements = list.Elements;
				while (start != end)
				{
					var mid = (start + end) / 2;
					var el = elements[mid];
					var aid = el.ActorID;
					if (aid == actorID)
						return el;
					if (aid < actorID)
						start = mid + 1;
					else
						end = mid;
				}

				return null;
			}

			private int FindActorIndex(uint actorID) {
				var start = 0;
				var end = list.Count;
				var elements = list.Elements;
				while (start != end)
				{
					var mid = (start + end) / 2;
					var el = elements[mid];
					var aid = el.ActorID;
					if (aid == actorID)
						return mid;
					if (aid < actorID)
						start = mid + 1;
					else
						end = mid;
				}

				return -1;
			}

			public T Get(uint actorID)
			{
				++Queries;
				var element = FindActor(actorID);
				if (element == null)
					throw new InvalidOperationException("Actor does not have trait of type `{0}`".F(typeof(T)));
				if (element.Traits.Length > 1)
					throw new InvalidOperationException("Actor {0} has multiple traits of type `{1}`".F(element.Pairs[0].Actor.Info.Name, typeof(T)));
				return element.Traits[0];
			}

			public T GetOrDefault(uint actorID)
			{
				++Queries;
				var element = FindActor(actorID);
				if (element == null)
					return default(T);
				if (element.Traits.Length > 1)
					throw new InvalidOperationException("Actor {0} has multiple traits of type `{1}`".F(element.Pairs[0].Actor.Info.Name, typeof(T)));
				return element.Traits[0];
			}

			public IEnumerable<T> GetMultiple(uint actorID)
			{
				++Queries;
				var element = FindActor(actorID);
				if (element == null)
					return Enumerable.Empty<T>();
					//throw new InvalidOperationException("Actor {1} does not have trait of type `{0}`".F(typeof(T), actorID));
				return element.Traits;
			}

			public IEnumerable<TraitPair<T>> All()
			{
				// PERF: Custom enumerator for efficiency - using `yield` is slower.
				++Queries;
				return new AllEnumerable(this);
			}

			class AllEnumerable : IEnumerable<TraitPair<T>>
			{
				readonly TraitContainer<T> container;
				public AllEnumerable(TraitContainer<T> container) { this.container = container; }
				public IEnumerator<TraitPair<T>> GetEnumerator() { return new AllEnumerator(container); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			class AllEnumerator : IEnumerator<TraitPair<T>>
			{
				readonly ArrayList list;
				int actorIndex;
				int traitIndex;
				TraitPair<T>[] pairs;
				public AllEnumerator(TraitContainer<T> container)
				{
					list = container.list;
					actorIndex = -1;
					traitIndex = -1;
					pairs = null;
				}

				public void Reset() { actorIndex = -1; traitIndex = -1; pairs = null; }
				public bool MoveNext()
				{
					if (actorIndex >= 0) 
					{
						if (actorIndex >= list.Count)
						{
							pairs = null;
							return false;
						}

						if (++traitIndex < pairs.Length)
							return true;
						else
						{
							if (++actorIndex < list.Count) {
								pairs = list.Elements[actorIndex].Pairs;
								traitIndex = 0;
								return true;
							}
						}
					}
					else
					{
						// Start
						if (++actorIndex < list.Count)
						{
							pairs = list.Elements[actorIndex].Pairs;
							traitIndex = 0;
							return true;
						}
					}

					return false;
				}

				public TraitPair<T> Current { get { return pairs[traitIndex]; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { pairs = null; }
			}

			public IEnumerable<Actor> Actors()
			{
				++Queries;
				return new ActorEnumerable(this);
			}

			class ActorEnumerable : IEnumerable<Actor>
			{
				readonly TraitContainer<T> container;
				public ActorEnumerable(TraitContainer<T> container) { this.container = container; }
				public IEnumerator<Actor> GetEnumerator() { return new ActorEnumerator(container); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			class ActorEnumerator : IEnumerator<Actor>
			{
				readonly ArrayList list;
				int actorIndex;
				public ActorEnumerator(TraitContainer<T> container)
				{
					list = container.list;
					actorIndex = -1;
				}

				public void Reset() { actorIndex = -1; }
				public bool MoveNext() { return ++actorIndex < list.Count; }
				public Actor Current { get { return list.Elements[actorIndex].Pairs[0].Actor; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { }
			}

			public IEnumerable<Actor> Actors(Func<T, bool> predicate)
			{
				++Queries;
				return new ActorPredicateEnumerable(this, predicate);
			}

			class ActorPredicateEnumerable : IEnumerable<Actor>
			{
				readonly TraitContainer<T> container;
				readonly Func<T, bool> predicate;
				public ActorPredicateEnumerable(TraitContainer<T> container, Func<T, bool> predicate) { this.container = container; this.predicate = predicate; }
				public IEnumerator<Actor> GetEnumerator() { return new ActorPredicateEnumerator(container, predicate); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			class ActorPredicateEnumerator : IEnumerator<Actor>
			{
				readonly ArrayList list;
				readonly Func<T, bool> predicate;
				int actorIndex;
				Actor actor;
				public ActorPredicateEnumerator(TraitContainer<T> container, Func<T, bool> predicate)
				{
					list = container.list;
					actorIndex = -1;
					this.predicate = predicate;
				}

				public void Reset() { actorIndex = -1; }
				public bool MoveNext()
				{
					while (++actorIndex < list.Count) {
						var pair = list.Elements[actorIndex].Pairs[0];
						if (predicate(pair.Trait))
						{
							actor = pair.Actor;
							return true;
						}
					}

					actor = null;
					return false;
				}

				public Actor Current { get { return actor; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { }
			}

			public void RemoveActor(uint actorID)
			{
				var index = FindActorIndex(actorID);
				if (index < 0)
					return;
				list.RemoveAt(index);
			}

			public void PrintReport(string logChannel, int traitIndex)
			{
			}

			public TraitContainerStat GetStat()
			{
				TraitContainerStat stat = new TraitContainerStat();
				stat.SumValues[(int)TraitContainerStat.SumId.ACTOR_COUNT] = list.Count;
				double traitCount = 0;
				double maxTraitCount = 0;
				foreach (var element in list.Elements)
				{
					if (element == null) {
						break;
					}
					traitCount += element.Traits.Length;
					if (element.Traits.Length > maxTraitCount)
						maxTraitCount = element.Traits.Length;
				}

				stat.SumValues[(int)TraitContainerStat.SumId.TRAIT_COUNT] = traitCount;
				stat.MaxValues[(int)TraitContainerStat.MaxId.TRAIT_PER_ACTOR] = maxTraitCount;
				return stat;
			}
		}
#else

		class TraitContainer<T> : ITraitContainer
		{
			readonly List<Actor> actors = new List<Actor>();
			readonly List<T> traits = new List<T>();

			public int Queries { get; private set; }

			public void Add(Actor actor, object trait)
			{
				var insertIndex = actors.BinarySearchMany(actor.ActorID + 1);
		//Console.WriteLine("=== Add {0}, {1}, type {2} index {3}", actor, actor.ActorID, typeof(T), insertIndex);
				actors.Insert(insertIndex, actor);
				traits.Insert(insertIndex, (T)trait);
			}

			public T Get(uint actor)
			{
				var result = GetOrDefault(actor);
				if (result == null)
					throw new InvalidOperationException("Actor does not have trait of type `{0}`".F(typeof(T)));
				return result;
			}

			public T GetOrDefault(uint actor)
			{
				++Queries;
				var index = actors.BinarySearchMany(actor);
				if (index >= actors.Count || actors[index].ActorID != actor)
					return default(T);
				else if (index + 1 < actors.Count && actors[index + 1].ActorID == actor)
					throw new InvalidOperationException("Actor {0} has multiple traits of type `{1}`".F(actors[index].Info.Name, typeof(T)));
				else return traits[index];
			}

			public IEnumerable<T> GetMultiple(uint actor)
			{
				// PERF: Custom enumerator for efficiency - using `yield` is slower.
				++Queries;
				return new MultipleEnumerable(this, actor);
			}

			class MultipleEnumerable : IEnumerable<T>
			{
				readonly TraitContainer<T> container;
				readonly uint actor;
				public MultipleEnumerable(TraitContainer<T> container, uint actor) { this.container = container; this.actor = actor; }
				public IEnumerator<T> GetEnumerator() { return new MultipleEnumerator(container, actor); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			class MultipleEnumerator : IEnumerator<T>
			{
				readonly List<Actor> actors;
				readonly List<T> traits;
				readonly uint actor;
				int index;
				public MultipleEnumerator(TraitContainer<T> container, uint actor)
				{
					actors = container.actors;
					traits = container.traits;
					this.actor = actor;
					Reset();
				}

				public void Reset() { index = actors.BinarySearchMany(actor) - 1; }
				public bool MoveNext() { return ++index < actors.Count && actors[index].ActorID == actor; }
				public T Current { get { return traits[index]; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { }
			}

			public IEnumerable<TraitPair<T>> All()
			{
				// PERF: Custom enumerator for efficiency - using `yield` is slower.
				++Queries;
				return new AllEnumerable(this);
			}

			public IEnumerable<Actor> Actors()
			{
				++Queries;
				Actor last = null;
				for (var i = 0; i < actors.Count; i++)
				{
					if (actors[i] == last)
						continue;
					yield return actors[i];
					last = actors[i];
				}
			}

			public IEnumerable<Actor> Actors(Func<T, bool> predicate)
			{
				++Queries;
				Actor last = null;

				for (var i = 0; i < actors.Count; i++)
				{
					if (actors[i] == last || !predicate(traits[i]))
						continue;
					yield return actors[i];
					last = actors[i];
				}
			}

			class AllEnumerable : IEnumerable<TraitPair<T>>
			{
				readonly TraitContainer<T> container;
				public AllEnumerable(TraitContainer<T> container) { this.container = container; }
				public IEnumerator<TraitPair<T>> GetEnumerator() { return new AllEnumerator(container); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			class AllEnumerator : IEnumerator<TraitPair<T>>
			{
				readonly List<Actor> actors;
				readonly List<T> traits;
				int index;
				public AllEnumerator(TraitContainer<T> container)
				{
					actors = container.actors;
					traits = container.traits;
					Reset();
				}

				public void Reset() { index = -1; }
				public bool MoveNext() { return ++index < actors.Count; }
				public TraitPair<T> Current { get { return new TraitPair<T>(actors[index], traits[index]); } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { }
			}

			public void RemoveActor(uint actor)
			{
				var startIndex = actors.BinarySearchMany(actor);
				if (startIndex >= actors.Count || actors[startIndex].ActorID != actor)
					return;
				var endIndex = startIndex + 1;
				while (endIndex < actors.Count && actors[endIndex].ActorID == actor)
					endIndex++;
				var count = endIndex - startIndex;
				actors.RemoveRange(startIndex, count);
				traits.RemoveRange(startIndex, count);
			}

			public void PrintReport(string logChannel, int traitIndex)
			{
			}

			public TraitContainerStat GetStat()
			{
				return null;
			}
		}
#endif
	}
}
