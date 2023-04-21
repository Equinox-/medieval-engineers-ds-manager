using System.Collections.Generic;

namespace Meds.Shared
{
    public static class CollectionEquality<T>
    {
        public static IEqualityComparer<List<T>> List(IEqualityComparer<T> element = null)
        {
            return new ListEquality(element ?? EqualityComparer<T>.Default);
        }

        private sealed class ListEquality : IEqualityComparer<List<T>>
        {
            private readonly IEqualityComparer<T> _element;

            public ListEquality(IEqualityComparer<T> element) => _element = element;

            public bool Equals(List<T> x, List<T> y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.Count != y.Count) return false;
                for (var i = 0; i < x.Count; i++)
                    if (!_element.Equals(x[i], y[i]))
                        return false;
                return true;
            }

            public int GetHashCode(List<T> obj)
            {
                var hash = obj.Count;
                foreach (var e in obj)
                    hash = (hash * 397) ^ _element.GetHashCode(e);
                return hash;
            }
        }
    }
}