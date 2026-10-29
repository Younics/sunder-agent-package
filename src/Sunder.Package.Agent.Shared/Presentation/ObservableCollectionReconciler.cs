using System.Collections.ObjectModel;

namespace Sunder.Package.Agent.Shared.Presentation;

internal static class ObservableCollectionReconciler
{
    // Keep retained items (and their controls, focus and expansion state) in place.
    internal static void Apply<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var desired = items.ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            if (index < collection.Count && EqualityComparer<T>.Default.Equals(collection[index], desired[index]))
                continue;
            var existing = collection.IndexOf(desired[index]);
            if (existing >= 0) collection.Move(existing, index);
            else collection.Insert(index, desired[index]);
        }
        while (collection.Count > desired.Length) collection.RemoveAt(collection.Count - 1);
    }
}
