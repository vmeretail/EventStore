﻿namespace EventStore.Core.LogAbstraction {
	public class NameExistenceFilterValueLookupDecorator<TValue> : IValueLookup<TValue> {
		private readonly IValueLookup<TValue> _wrapped;
		private readonly INameExistenceFilter _existenceFilter;

		public NameExistenceFilterValueLookupDecorator(
			IValueLookup<TValue> wrapped,
			INameExistenceFilter existenceFilter) {

			_wrapped = wrapped;
			_existenceFilter = existenceFilter;
		}

		public TValue LookupValue(string name) {
			if (_existenceFilter.MightExist(name))
				return _wrapped.LookupValue(name);

			return default;
		}
	}
}
