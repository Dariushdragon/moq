// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Moq
{
	/// <summary>
	///   A per-thread observer that records invocations to mocks and matchers for later inspection.
	/// </summary>
	/// <remarks>
	///   <para>
	///     This component requires the active cooperation of the respective subsystems.
	///     That is, invoked matchers and mocks call into <see cref="OnMatch(Match)"/>
	///     or <see cref="OnInvocation(Mock, Invocation)"/> if an ambient observer is
	///     active on the current thread.
	///   </para>
	///   <para>
	///     This gets used in Moq's API to work around certain limitations of what kind
	///     of constructs the Roslyn compilers allow in in-source LINQ expression trees
	///     (e.g., assignment and event (un-)subscription are forbidden). Instead of
	///     letting user code provide a LINQ expression tree, Moq accepts a normal lambda.
	///     While a lambda cannot be directly inspected like a LINQ expression tree, we
	///     can instantiate an <see cref="AmbientObserver"/>, execute the lambda, and then
	///     check with the observer what invocations happened; and from there, we can
	///     "reverse-engineer" a LINQ expression tree (with some loss of accuracy).
	///   </para>
	/// </remarks>
	internal sealed class AmbientObserver : IDisposable
	{
		[ThreadStatic]
		private static Stack<AmbientObserver> activations;

		public static AmbientObserver Activate()
		{
			var activation = new AmbientObserver();

			var activations = AmbientObserver.activations;
			if (activations == null)
			{
				AmbientObserver.activations = activations = new Stack<AmbientObserver>();
			}
			activations.Push(activation);

			return activation;
		}

		public static bool IsActive(out AmbientObserver observer)
		{
			var activations = AmbientObserver.activations;

			if (activations != null && activations.Count > 0)
			{
				observer = activations.Peek();
				return true;
			}
			else
			{
				observer = null;
				return false;
			}
		}

		private int timestamp;
		private List<Observation> observations;

		private AmbientObserver()
		{
		}

		public void Dispose()
		{
			if (this.observations != null)
			{
				for (var i = this.observations.Count - 1; i >= 0; --i)
				{
					this.observations[i].Dispose();
				}
			}

			var activations = AmbientObserver.activations;
			Debug.Assert(activations != null && activations.Count > 0);
			activations.Pop();
		}

		/// <summary>
		///   Returns the current timestamp. The next call will return a timestamp greater than this one,
		///   allowing you to order invocations and matcher observations.
		/// </summary>
		public int GetNextTimestamp()
		{
			return ++this.timestamp;
		}

		/// <summary>
		///   Adds the specified mock invocation as an observation.
		/// </summary>
		public void OnInvocation(Mock mock, Invocation invocation)
		{
			if (this.observations == null)
			{
				this.observations = new List<Observation>();
			}

			observations.Add(new InvocationObservation(this.GetNextTimestamp(), mock, invocation));
		}

		/// <summary>
		///   Adds the specified <see cref="Match"/> as an observation.
		/// </summary>
		public void OnMatch(Match match)
		{
			if (this.observations == null)
			{
				this.observations = new List<Observation>();
			}

			this.observations.Add(new MatchObservation(this.GetNextTimestamp(), match));
		}

		/// <summary>
		///   Checks whether the last observed thing was a mock invocation.
		///   If <see langword="true"/>, details about that mock invocation are provided via the <see langword="out"/> parameters.
		/// </summary>
		/// <param name="mock">The <see cref="Mock"/> on which an invocation was observed.</param>
		/// <param name="invocation">The observed <see cref="Invocation"/>.</param>
		/// <param name="matches">The <see cref="Match"/>es that were observed just before the invocation.</param>
		public bool LastIsInvocation(out Mock mock, out Invocation invocation, out Matches matches)
		{
			if (this.observations != null)
			{
				var lastIndex = this.observations.Count - 1;

				if (this.observations[lastIndex] is InvocationObservation invocationRecord)
				{
					// Determine the first index of all recorded matches that immediately precede
					// the last invocation; up to the previous recorded invocation or the beginning
					// of the recording (whichever comes first):
					int offset = lastIndex;
					while (offset > 0 && this.observations[offset - 1] is MatchObservation)
					{
						--offset;
					}

					mock = invocationRecord.Mock;
					invocation = invocationRecord.Invocation;
					matches = new Matches(this, offset, lastIndex - offset);
					return true;
				}
			}

			mock = default;
			invocation = default;
			matches = default;
			return false;
		}

		/// <summary>
		///   Checks whether the last thing observed was a <see cref="Match"/> matcher.
		///   If <see langword="true"/>, details about that matcher are provided via the <see langword="out"/> parameter.
		/// </summary>
		/// <param name="match">The observed <see cref="Match"/> matcher.</param>
		public bool LastIsMatch(out Match match)
		{
			if (this.observations != null && this.observations[this.observations.Count - 1] is MatchObservation matchRecord)
			{
				match = matchRecord.Match;
				return true;
			}

			match = default;
			return false;
		}

		public IEnumerable<Match> GetMatchesBetween(int fromTimestampInclusive, int toTimestampExclusive)
		{
			if (this.observations != null)
			{
				return this.observations
				           .OfType<MatchObservation>()
				           .Where(o => fromTimestampInclusive <= o.Timestamp && o.Timestamp < toTimestampExclusive)
				           .Select(o => o.Match);
			}
			else
			{
				return Enumerable.Empty<Match>();
			}
		}

		/// <summary>
		///   Allocation-free pseudo-collection (think `ReadOnlySpan&lt;Match&gt;`)
		///   used to access all <see cref="Match"/>es associated with a recorded invocation.
		/// </summary>
		public readonly struct Matches
		{
			private readonly AmbientObserver observer;
			private readonly int offset;
			private readonly int count;

			public Matches(AmbientObserver observer, int offset, int count)
			{
				this.observer = observer;
				this.offset = offset;
				this.count = count;
			}

			public int Count => this.count;

			public Match this[int index] => ((MatchObservation)this.observer.observations[this.offset + index]).Match;
		}

		private abstract class Observation : IDisposable
		{
			protected Observation()
			{
			}

			public virtual void Dispose()
			{
			}
		}

		private sealed class InvocationObservation : Observation
		{
			public readonly int Timestamp;
			public readonly Mock Mock;
			public readonly Invocation Invocation;

			private DefaultValueProvider defaultValueProvider;

			public InvocationObservation(int timestamp, Mock mock, Invocation invocation)
			{
				this.Timestamp = timestamp;
				this.Mock = mock;
				this.Invocation = invocation;

				this.defaultValueProvider = mock.DefaultValueProvider;
				mock.DefaultValueProvider = DefaultValueProvider.Mock;
			}

			public override void Dispose()
			{
				this.Mock.DefaultValueProvider = this.defaultValueProvider;
			}
		}

		private sealed class MatchObservation : Observation
		{
			public readonly int Timestamp;
			public readonly Match Match;

			public MatchObservation(int timestamp, Match match)
			{
				this.Timestamp = timestamp;
				this.Match = match;
			}
		}
	}
}
