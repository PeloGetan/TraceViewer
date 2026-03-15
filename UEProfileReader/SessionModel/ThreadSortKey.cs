namespace UEProfileReader.SessionModel;

public readonly record struct ThreadSortKey(
    int SpecialPriorityRank,
    uint GroupRank,
    int PriorityRank,
    uint FallbackOrder) : IComparable<ThreadSortKey>
{
    public int CompareTo(ThreadSortKey other)
    {
        var specialPriority = SpecialPriorityRank.CompareTo(other.SpecialPriorityRank);
        if (specialPriority != 0)
        {
            return specialPriority;
        }

        var groupRank = GroupRank.CompareTo(other.GroupRank);
        if (groupRank != 0)
        {
            return groupRank;
        }

        var priorityRank = PriorityRank.CompareTo(other.PriorityRank);
        if (priorityRank != 0)
        {
            return priorityRank;
        }

        return FallbackOrder.CompareTo(other.FallbackOrder);
    }
}
