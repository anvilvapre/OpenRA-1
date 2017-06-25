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

using System;
using System.Collections.Generic;
using System.Linq;
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
		static readonly Func<Type, ITraitContainer> CreateTraitContainer = t =>
			(ITraitContainer)typeof(TraitContainer<>).MakeGenericType(t).GetConstructor(Type.EmptyTypes).Invoke(null);

		private ITraitContainer[] traits = null;

		public TraitDictionary() {
			Resize();
		}

		private void Resize() {
			// New trait types have been registered. Ensure our array is same size as trait type index map.
			int newSize = TraitTypeIndexMap.GetCount() + 64;
			if (traits == null) {
				traits = new ITraitContainer[newSize];
			}
			else {
				if (traits.Length < newSize) {
					Array.Resize(ref traits, newSize);
				}
			}
		}

		private ITraitContainer InnerGet(int index, Type t) {
			// Lookup trait by index in array, rather than hash key in dictionary.
			if (index <= 0) {
				throw new InvalidOperationException("Traits type index out of bounds (<=0)");
			}

			if (index >= traits.Length) {
				Resize();
				if (index > traits.Length) {
					throw new InvalidOperationException("Traits type index out of bounds, index greater than max registered type index");
				}
			}

			ITraitContainer trait = traits[index];
			if (trait == null) {
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

		TraitContainer<T> InnerGet<T>()
		{
			return (TraitContainer<T>)InnerGet(TraitTypeIndex<T>.GetTypeIndex(), typeof(T));
		}

		public void PrintReport()
		{
			if (traits == null) {
				return;
			}

			Log.AddChannel("traitreport", "traitreport.log");
			Log.Write("traitreport", "Number of registered trait types {0}", TraitTypeIndexMap.GetCount());
			for (int i = 1; i < traits.Length; i++) {
				var trait = traits[i];
				if (trait != null) {
					Log.Write("traitreport", "{0}: {1}", TraitTypeIndexMap.GetType(i), trait.Queries);
				}
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
			foreach (var t in traits) {
				if (t != null) {
					t.RemoveActor(a.ActorID);
				}
			}
		}

		interface ITraitContainer
		{
			void Add(Actor actor, object trait);
			void RemoveActor(uint actor);

			int Queries { get; }
		}



		class TraitContainer<T> : ITraitContainer
		{
			protected class Element
			{
				public uint ActorID;
				// Avoid heavy creation on pair objects in enumarators.
				public TraitPair<T> Pair;
				// Linked list to next trait for same actor.
				public Element Next;
				// public bool removed = false;

				public Element(Actor actor, T trait) {
					this.ActorID = actor.ActorID;
					this.Pair = new TraitPair<T>(actor, trait);
				}

				public Element(Actor actor, T trait, Element next) {
					this.ActorID = actor.ActorID;
					this.Pair = new TraitPair<T>(actor, trait);
					this.Next = next;
				}
			}

			protected class ArrayList
			{
				public Element[] Elements;
				public int Count;
				public int Capacity;

				public ArrayList(int Capacity) {
					Count = 0;
					this.Capacity = Capacity <= 0 ? 32 : Capacity;
					Elements = new Element[this.Capacity];
				}

				private void Grow() {
					Array.Resize(ref Elements, Capacity+32);
					Capacity += 32;
				}

				public void Add(Element element) {
					if (Count >= Capacity) {
						Grow();
					}
					Elements[Count++] = element;
				}

				public void Insert(int index, Element element) {
					if (Count >= Capacity) {
						Grow();
					}
					if (index == Count) {
						Elements[Count++] = element;
					}
					else {
						Array.Copy(Elements, index, Elements, index+1, Count-index); 
						Elements[index] = element;
					}
				}

				public void RemoveAt(int index) {
					if (index < 0 || index >= Count) {
						throw new InvalidOperationException("Index out of bounds {0}".F(index));
					}
					Array.Copy(Elements, index, Elements, index-1, Count-index); 
					--Count;
				}
			}

			// Array with one element per actor, each element may link to a next trait for the same actor.
			// 6 multi player ai game has 500 actors.
			protected ArrayList List = new ArrayList(512);

			public int Queries { get; private set; }

			private Element Insert(Actor actor, T trait) {
				// Assumption: actor is new, has highest id, so needs to be added to back.
				var ActorID = actor.ActorID;
				int index = 0;
				Element element = null;
				var elements = List.Elements;
				for (int i=List.Count-1; i >= 0; --i) {
					var el = elements[i];
					var aid = el.ActorID;
					if (aid == ActorID) {
						element = new Element(actor, trait);
						element.Next = el;
						elements[i] = element;
						break;
					}
					else if (aid < ActorID) {
						index = i+1;
						break;
					}
				}
				if (element == null) {
					element = new Element(actor, trait);
					List.Insert(index, element);
				}
				return element;
			}

			public void Add(Actor actor, object trait) 
			{
				Insert(actor, (T)trait);
			}

			private Element FindActor(uint ActorID) {
				var start = 0;
				var end = List.Count;
				var elements = List.Elements;
				while (start != end)
				{
					var mid = (start + end) / 2;
					var el = elements[mid];
					var aid = el.ActorID;
					if (aid == ActorID)
						return el;
					if (aid < ActorID)
						start = mid + 1;
					else
						end = mid;
				}
				return null;
			}

			private int FindActorIndex(uint ActorID) {
				var start = 0;
				var end = List.Count;
				var elements = List.Elements;
				while (start != end)
				{
					var mid = (start + end) / 2;
					var el = elements[mid];
					var aid = el.ActorID;
					if (aid == ActorID)
						return mid;
					if (aid < ActorID)
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
				if (element.Next != null)
					throw new InvalidOperationException("Actor {0} has multiple traits of type `{1}`".F(element.Pair.Actor.Info.Name, typeof(T)));
				return element.Pair.Trait;
			}

			public T GetOrDefault(uint actorID)
			{
				++Queries;
				var element = FindActor(actorID);
				if (element == null)
					return default(T);
				if (element.Next != null)
					throw new InvalidOperationException("Actor {0} has multiple traits of type `{1}`".F(element.Pair.Actor.Info.Name, typeof(T)));
				return element.Pair.Trait;
			}

			public IEnumerable<T> GetMultiple(uint actorID)
			{
				++Queries;
				var element = FindActor(actorID);
				if (element == null)
					throw new InvalidOperationException("Actor does not have trait of type `{0}`".F(typeof(T)));
				return new ActorMultipleEnumerable(element);
			}

			class ActorMultipleEnumerable : IEnumerable<T>
			{
				readonly Element element;
				public ActorMultipleEnumerable(Element element) { this.element = element; }
				public IEnumerator<T> GetEnumerator() { return new ActorMultipleEnumerator(element); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			class ActorMultipleEnumerator : IEnumerator<T>
			{
				readonly Element element;
				Element it;

				public ActorMultipleEnumerator(Element element)
				{
					this.element = element;
					it = null;
				}

				public void Reset() { it = null; }
				public bool MoveNext() { if (it == null) { it = element; } else { it = it.Next; } return it != null; }
				public T Current { get { return it.Pair.Trait; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { it = null; }
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
				Element it;
				public AllEnumerator(TraitContainer<T> container)
				{
					list = container.List;
					it = null;
				}

				public void Reset() { actorIndex = -1; it = null; }
				public bool MoveNext() 
				{ 
					if (it != null) {
						it = it.Next;
					}
					if (it == null) {
						if (++actorIndex > list.Count)
							return false;
						it = list.Elements[actorIndex];
					}
					return true;
				}

				public TraitPair<T> Current { get { return it.Pair; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { it = null; }
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
					list = container.List;
				}

				public void Reset() { actorIndex = -1; }
				public bool MoveNext() { return (++actorIndex < list.Count); }
				public Actor Current { get { return list.Elements[actorIndex].Pair.Actor; } }
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
				int actorIndex;
				readonly Func<T, bool> predicate;
				public ActorPredicateEnumerator(TraitContainer<T> container, Func<T, bool> predicate)
				{
					list = container.List;
					this.predicate = predicate;
				}

				public void Reset() { actorIndex = -1; }
				public bool MoveNext() 
				{ 
					while (++actorIndex < list.Count) {
						if (predicate(list.Elements[actorIndex].Pair.Trait)) {
							return true;
						}
					}
					return false;
				}
				public Actor Current { get { return list.Elements[actorIndex].Pair.Actor; } }
				object System.Collections.IEnumerator.Current { get { return Current; } }
				public void Dispose() { }
			}

			public void RemoveActor(uint actor)
			{
				var index = FindActorIndex(actor);
				if (index < 0) {
					throw new InvalidOperationException("Actor with id {0} not fond".F(typeof(T)));
				}
				List.RemoveAt(index);
			}
		}
/*
		class DisabledTraitContainer<T> : ITraitContainer
		{
			readonly List<Actor> actors = new List<Actor>();
			readonly List<T> traits = new List<T>();

			public int Queries { get; private set; }

			public void Add(Actor actor, object trait)
			{
				var insertIndex = actors.BinarySearchMany(actor.ActorID + 1);
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
		}
	*/
	}
}
