using System.Collections.ObjectModel;

namespace VoidCrewModLauncher.Infrastructure;

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="items"/> hold <paramref name="wanted"/>, in that order, by removing, moving and inserting only
    /// what differs (compared with <paramref name="same"/>, by default Equals: the same object for view models). Items that
    /// stay keep their place on screen: a list's selection and focus and an open drop-down in a row survive.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> items, IReadOnlyList<T> wanted, IEqualityComparer<T>? same = null)
    {
        same ??= EqualityComparer<T>.Default;
        var keep = new HashSet<T>(wanted, same);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i])) items.RemoveAt(i);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && same.Equals(items[i], wanted[i])) continue;
            int at = -1;
            for (int j = i + 1; j < items.Count; j++)
            {
                if (!same.Equals(items[j], wanted[i])) continue;
                at = j;
                break;
            }
            if (at >= 0) items.Move(at, i);
            else items.Insert(i, wanted[i]);
        }
        while (items.Count > wanted.Count) items.RemoveAt(items.Count - 1);
    }
}
