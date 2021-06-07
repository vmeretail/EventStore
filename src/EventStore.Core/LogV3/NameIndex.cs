﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using EventStore.Core.LogAbstraction;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using Serilog;
using Value = System.UInt32;

namespace EventStore.Core.LogV3 {
	// There are two components injected NameIndex. Ther existenceFilter and the persistence.
	//
	// The NameIndex itself allows for reservation of entries, which generates the numbering and holds them
	// in memory. This is similar to the purpose of the 'IndexWriter' class.
	//
	// To do this it makes use of the existence filter which can quickly tell if a name
	// might exist or definitely does not exist.
	//
	// The entries can then be confirmed, which transfers them to the INameIndexPersistence
	// object which is allowed to persist them to disk. This is similar to the IndexCommiter class.
	//
	// Components wanting only entries that have been confirmed will read from the INameIndexPersistence.
	public class NameIndex :
		INameIndex<Value>,
		INameIndexConfirmer<Value> {

		private static readonly ILogger Log = Serilog.Log.ForContext<NameIndex>();
		private readonly ConcurrentDictionary<string, Value> _reservations = new();
		private readonly INameExistenceFilter _existenceFilter;
		private readonly INameIndexPersistence<Value> _persistence;
		private readonly string _indexName;
		private readonly Value _firstValue;
		private readonly Value _valueInterval;
		private readonly object _nextValueLock = new();
		private Value _nextValue;

		public NameIndex(
			string indexName,
			Value firstValue,
			Value valueInterval,
			INameExistenceFilter existenceFilter,
			INameIndexPersistence<Value> persistence) {

			_indexName = indexName;
			_firstValue = firstValue;
			_valueInterval = valueInterval;
			_nextValue = firstValue;
			_existenceFilter = existenceFilter;
			_persistence = persistence;
		}

		public void Dispose() {
			_persistence?.Dispose();
		}

		public void CancelReservations() {
			var count = _reservations.Count;
			_reservations.Clear();
			var nextValue = CalcNextValue();
			Log.Information("{indexName} {count} reservations cancelled. Next value is {value}",
				_indexName, count, nextValue);
		}

		public void InitializeWithConfirmed(INameLookup<Value> source) {
			_reservations.Clear();
			_persistence.Init(source);
			var nextValue = CalcNextValue();
			Log.Information("{indexName} initialized. Next value is {value}", _indexName, nextValue);
		}

		Value CalcNextValue() {
			lock (_nextValueLock) {
				_nextValue = _persistence.LastValueAdded == default
					? _firstValue
					: _persistence.LastValueAdded + _valueInterval;
				return _nextValue;
			}
		}

		public void Confirm(string name, Value value) {
			_existenceFilter.Add(name, value); //qq check that its supposed to be the stream number here and not the eventnumber
			_persistence.Add(name, value);
			if (_reservations.TryRemove(name, out var reservedValue)) {
				if (reservedValue != value) {
					throw new Exception($"This should never happen. Confirmed value for \"{name}\" was {value} but reserved as {reservedValue}");
				}
			} else {
				// an entry got confirmed that we didn't reserve. this is normal in the follower
				// and there is nothing to do. however it is currently possible in the leader too
				// because it only waits for the chaser to catch up and not the index.
				// in this case we need to maintain _nextValue
				lock (_nextValueLock) {
					_nextValue = value + _valueInterval;
				}
			}
		}

		public bool Confirm(IList<IPrepareLogRecord<Value>> prepares) {
			var confirmedSomething = false;
			for (int i = 0; i < prepares.Count; i++) {
				var prepare = prepares[i];
				if (prepare.RecordType == LogRecordType.Stream &&
					prepare is LogV3StreamRecord streamRecord) {
					Confirm(
						name: streamRecord.StreamName,
						value: streamRecord.StreamNumber);
					confirmedSomething = true;
				}
			}
			return confirmedSomething;
		}

		public bool GetOrReserve(string name, out Value value, out Value addedValue, out string addedName) {
			if (string.IsNullOrEmpty(name))
				throw new ArgumentNullException(nameof(name));

			if (_reservations.TryGetValue(name, out value)) {
				addedValue = default;
				addedName = default;
				return true;
			}

			if (!_existenceFilter.MightExist(name)) {
				// stream definitely does not exist, we can jump straight to reserving it.
				Reserve(name, out value, out addedValue, out addedName);
				return false;
			}

			if (!_persistence.TryGetValue(name, out value)) {
				Reserve(name, out value, out addedValue, out addedName);
				return false;
			}

			addedValue = default;
			addedName = default;
			return true;
		}

		private void Reserve(string name, out Value value, out Value addedValue, out string addedName) {
			lock (_nextValueLock) {
				value = _nextValue;
				_nextValue += _valueInterval;
				addedValue = value;
				addedName = name;
				_reservations[name] = value;
				Log.Debug("{indexName} reserved new entry: {key}:{value}", _indexName, name, value);
			}
		}
	}
}
