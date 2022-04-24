﻿
namespace Chloe.Query.QueryExpressions
{
    enum QueryExpressionType
    {
        Root = 1,
        Where,
        Take,
        Skip,
        Paging,
        OrderBy,
        OrderByDesc,
        ThenBy,
        ThenByDesc,
        Select,
        Include,
        Aggregate,
        JoinQuery,
        GroupingQuery,
        Distinct,
        IgnoreAllFilters,
        Tracking,
    }
}
